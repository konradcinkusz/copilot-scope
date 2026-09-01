using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Forwarding;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using CopilotScope.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Shared kernel: OTel self-instrumentation, health, discovery, resilience (P2/P15).
// The observability product now emits telemetry about itself when an OTLP endpoint
// is configured. This is additive to the hand-written /api/health below.
builder.AddServiceDefaults();

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<QualityEngine>();

// Insight pipeline — pluggable per-algorithm analyzers (docs/ANALYSIS.md §8).
var pricing = new PricingOptions();
builder.Configuration.GetSection("CopilotScope:Pricing").Bind(pricing.Models);
builder.Services.AddSingleton(pricing);
builder.Services.AddSingleton<IInsightAnalyzer, EditSurvivalAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, ThroughputAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, LatencyUtilityAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, TokenEconomicsAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer, FrustrationAnalyzer>();
builder.Services.AddSingleton<InsightPipeline>();

// Prometheus scrape endpoint — exports the *computed* quality signals, whereas
// OtlpForwarder relays raw OTLP upstream. Complementary, not alternatives.
var prometheusOptions = new PrometheusOptions();
builder.Configuration.GetSection("CopilotScope:Prometheus").Bind(prometheusOptions);
builder.Services.AddSingleton(prometheusOptions);
builder.Services.AddSingleton<PrometheusExporter>();

builder.Services.AddSingleton<OtlpForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OtlpForwarder>());

// Persistence is optional: the "copilotdb" connection string is injected by the
// Aspire AppHost (WithReference(db)); without it the collector runs in-memory only,
// so `dotnet run` on a bare machine still works.
var connectionString = builder.Configuration.GetConnectionString("copilotdb");
var persistenceEnabled = !string.IsNullOrEmpty(connectionString);
if (persistenceEnabled)
{
    builder.Services.AddSingleton(new SessionRepository(connectionString!));
    builder.Services.AddSingleton<PersistenceWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());
}

var app = builder.Build();

app.MapDefaultEndpoints(); // /health (readiness) + /alive (liveness)

var ingestApiKey = app.Configuration["CopilotScope:Ingest:ApiKey"]; // null/empty → open (dev mode)

// Single, constant-time key check used by every gated surface (/v1, /api, /metrics).
// An empty configured key means dev/open mode. Keeping this in one place is what
// lets the whole /api group be gated deny-by-default instead of endpoint-by-endpoint —
// which is how the destructive DELETE used to sit unauthenticated.
bool KeyAuthorized(HttpRequest request)
{
    if (string.IsNullOrEmpty(ingestApiKey)) return true;
    var provided = request.Headers["x-api-key"].FirstOrDefault()
                ?? request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
    if (string.IsNullOrEmpty(provided)) return false;
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(provided),
        System.Text.Encoding.UTF8.GetBytes(ingestApiKey));
}

var store = app.Services.GetRequiredService<SessionStore>();
var quality = app.Services.GetRequiredService<QualityEngine>();
var insightPipeline = app.Services.GetRequiredService<InsightPipeline>();
var forwarder = app.Services.GetRequiredService<OtlpForwarder>();
var persistence = app.Services.GetService<PersistenceWriter>(); // null when persistence disabled

// ---------------------------------------------------------------- OTLP ingest
// Copilot Chat's default exporter is otlp-http (protobuf) on http://localhost:4318.
// The three standard OTLP/HTTP paths are implemented below.

var otlp = app.MapGroup("/v1");

// Ingest is gated deny-by-default; the filter logs the client hint on rejection.
otlp.AddEndpointFilter(async (ctx, next) =>
{
    if (!KeyAuthorized(ctx.HttpContext.Request))
    {
        app.Logger.LogWarning("Rejected {Path}: missing or wrong x-api-key/Authorization header " +
            "from {RemoteIp}. Set OTEL_EXPORTER_OTLP_HEADERS=\"x-api-key=<key>\" on the client.",
            ctx.HttpContext.Request.Path, ctx.HttpContext.Connection.RemoteIpAddress);
        return Results.Unauthorized();
    }
    return await next(ctx);
});

otlp.MapPost("/{signal}", async (string signal, HttpRequest request, ILogger<Program> logger) =>
{
    if (signal is not ("traces" or "metrics" or "logs"))
        return Results.NotFound();

    var contentType = request.ContentType ?? "";
    var isProtobuf = contentType.Contains("protobuf", StringComparison.OrdinalIgnoreCase);
    // Some Copilot surfaces (VS Code metrics/logs exporters, as of July 2026) ship the
    // JSON-only OTLP exporter regardless of exporterType/protocol settings — a confirmed
    // upstream gap (github/copilot-cli#2934), not something fixable from the client side.
    // Accept OTLP/HTTP JSON too so those signals aren't silently dropped.
    var isJson = !isProtobuf && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
    if (!isProtobuf && !isJson)
    {
        logger.LogWarning("Rejected /v1/{Signal}: unsupported content type '{ContentType}'. " +
            "Expected OTLP/HTTP protobuf or JSON.", signal, contentType);
        return Results.Json(new { error = "Only OTLP/HTTP protobuf or JSON is supported." },
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    // Real-world OTLP exporters frequently compress payloads; ASP.NET Core does
    // not auto-decompress request bodies, so handle it explicitly.
    Stream body = request.Body;
    var contentEncoding = request.Headers.ContentEncoding.ToString();
    if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        body = new System.IO.Compression.GZipStream(body, System.IO.Compression.CompressionMode.Decompress);
    else if (contentEncoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
        body = new System.IO.Compression.DeflateStream(body, System.IO.Compression.CompressionMode.Decompress);

    // Bound the *decoded* payload. Kestrel limits only the compressed request size,
    // and gzip/deflate reach ~1000:1, so an unbounded copy of a compressed body is a
    // memory-exhaustion (compression-bomb) vector — reachable pre-auth when no key is set.
    const long MaxDecodedBytes = 64L * 1024 * 1024; // 64 MB ceiling on a single OTLP batch
    byte[] payload;
    using (var ms = new MemoryStream())
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + read > MaxDecodedBytes)
            {
                logger.LogWarning("Rejected /v1/{Signal}: decoded payload exceeds {Limit} bytes.", signal, MaxDecodedBytes);
                return Results.Json(new { error = "Decoded payload exceeds the size limit." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            ms.Write(buffer, 0, read);
        }
        payload = ms.ToArray();
    }

    var batch = new OtlpBatch();
    try
    {
        if (isJson)
        {
            switch (signal)
            {
                case "traces": OtlpJsonDecoder.DecodeTraces(payload, batch); break;
                case "metrics": OtlpJsonDecoder.DecodeMetrics(payload, batch); break;
                case "logs": OtlpJsonDecoder.DecodeLogs(payload, batch); break;
            }
        }
        else
        {
            switch (signal)
            {
                case "traces": OtlpDecoder.DecodeTraces(payload, batch); break;
                case "metrics": OtlpDecoder.DecodeMetrics(payload, batch); break;
                case "logs": OtlpDecoder.DecodeLogs(payload, batch); break;
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to decode OTLP {Signal} payload ({Bytes} bytes, {Format})",
            signal, payload.Length, isJson ? "json" : "protobuf");
        return Results.BadRequest(new { error = ex.Message });
    }

    var knownBefore = store.All.Count;
    // The connection identity scopes process/service fingerprints to one machine. Without
    // it, two developers behind a shared collector whose emitters report the same
    // service.name would have their identity-less metrics merged into one conversation.
    var touched = store.Ingest(batch, request.HttpContext.Connection.RemoteIpAddress?.ToString());
    persistence?.MarkDirty(touched);

    // Buckets consumed by a merge must also disappear from Postgres, or they'd
    // come back as ghosts on the next rehydration.
    var merged = store.DrainRemoved();
    if (merged.Count > 0 && app.Services.GetService<SessionRepository>() is { } mergeRepo)
        foreach (var id in merged)
        {
            try { await mergeRepo.DeleteAsync(id, CancellationToken.None); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not delete merged bucket {Id} from Postgres.", id); }
        }

    forwarder.Enqueue($"/v1/{signal}", payload);

    if (store.All.Count > knownBefore)
        logger.LogInformation("New session(s) started: {Sessions}", string.Join(", ", touched));

    logger.LogDebug("OTLP {Signal}: {Spans} spans, {Metrics} points, {Logs} logs → {Sessions} session(s)",
        signal, batch.Spans.Count, batch.Metrics.Count, batch.Logs.Count, touched.Count);

    // OTLP/HTTP success: empty Export*ServiceResponse. An empty protobuf message is valid;
    // the JSON mapping of the same empty response is `{}`.
    return isJson
        ? Results.Text("{}", "application/json")
        : Results.Bytes(Array.Empty<byte>(), "application/x-protobuf");
});

// ------------------------------------------------------------------ query API

var api = app.MapGroup("/api");

// Deny-by-default: the whole query/admin surface is gated by the ingest key when one
// is set. Reads expose captured transcripts and DELETE is destructive, so an open key
// must not leave them reachable. /api/health is deliberately mapped OUTSIDE this group
// (below) so liveness probes stay unauthenticated.
api.AddEndpointFilter(async (ctx, next) =>
{
    if (!KeyAuthorized(ctx.HttpContext.Request)) return Results.Unauthorized();
    return await next(ctx);
});

api.MapGet("/sessions", (bool? includeInternal) =>
{
    // Build per-repo quality score pools so the list can show relative rank within each repo.
    var userSessions = store.All
        .Where(x => !SessionClassifier.IsInternal(x.Kind) && x.ChatCalls > 0)
        .ToList();
    var repoScores = userSessions
        .Where(x => x.Repository is not null)
        .GroupBy(x => x.Repository!, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(x => quality.Evaluate(x).Score).ToList(), StringComparer.Ordinal);

    return Results.Ok(store.All
        .Where(s => includeInternal == true || !SessionClassifier.IsInternal(s.Kind))
        .OrderByDescending(s => s.LastSeen)
        .Select(s =>
        {
            var scores = s.Repository is { } repo
                && repoScores.TryGetValue(repo, out var rs) && rs.Count >= 3 ? rs : null;
            return Dto.Summary(s, quality, scores);
        }));
});

api.MapGet("/sessions/{id}", (string id) =>
{
    if (store.Get(Uri.UnescapeDataString(id)) is not { } s) return Results.NotFound();
    var userSessions = store.All
        .Where(x => !SessionClassifier.IsInternal(x.Kind) && x.ChatCalls > 0);
    // Prefer repo-scoped peer group; fall back to all sessions when fewer than 3 peers share the repo.
    var repoScores = s.Repository is { } repo
        ? userSessions.Where(x => x.Repository == repo).Select(x => quality.Evaluate(x).Score).ToList()
        : null;
    var allScores = repoScores is { Count: >= 3 }
        ? repoScores
        : userSessions.Select(x => quality.Evaluate(x).Score).ToList();
    return Results.Ok(Dto.Detail(s, quality, insightPipeline, allScores));
});

api.MapDelete("/sessions/{id}", async (string id, ILogger<Program> logger) =>
{
    var key = Uri.UnescapeDataString(id);
    var removed = store.Remove(key);
    if (app.Services.GetService<SessionRepository>() is { } repo)
    {
        try { await repo.DeleteAsync(key, CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete session {Id} from Postgres.", key); }
    }
    logger.LogInformation("Session {Id} deleted (existed in memory: {Removed}).", key, removed);
    return removed ? Results.NoContent() : Results.NotFound();
});

api.MapGet("/overview", () => Results.Ok(DtoOverview.Build(store.All, quality)));

// ------------------------------------------------------------ admin / seeding
// Lets tools/CopilotScope.Seeder push a local-dev or demo dataset straight into
// a running collector — no Postgres network access and no restart required.
// Shares the ingest API key: anyone who can already post fake OTLP telemetry
// can fabricate session data too, so this doesn't widen the trust boundary.
// Seeded rows are namespaced under the "seed-" id prefix so a reset never
// touches real captured sessions.
const string SeedIdPrefix = "seed-";

api.MapPost("/admin/seed", async (SeedRequest req, ILogger<Program> logger) =>
{
    // (Auth is handled by the /api group filter above.)
    // Enforce the seed- prefix server-side: the "namespaced" guarantee was only a
    // Seeder convention, so a key holder could otherwise Put over a real captured
    // session. Refuse the whole batch if any id is out of namespace.
    var offending = req.Sessions.Select(p => p.ToSession().Id)
        .FirstOrDefault(id => !id.StartsWith(SeedIdPrefix, StringComparison.Ordinal));
    if (offending is not null)
        return Results.BadRequest(new { error = $"Seed session ids must start with '{SeedIdPrefix}'; refusing '{offending}' to avoid overwriting real sessions." });

    var repo = app.Services.GetService<SessionRepository>();

    if (req.Reset)
    {
        var removedMemory = store.RemoveWhere(id => id.StartsWith(SeedIdPrefix, StringComparison.Ordinal));
        var removedDb = repo is not null ? await repo.DeleteByPrefixAsync(SeedIdPrefix, CancellationToken.None) : 0;
        logger.LogInformation("Seed reset: cleared {Memory} in-memory / {Db} Postgres seed session(s).", removedMemory, removedDb);
    }

    foreach (var persisted in req.Sessions)
    {
        var session = persisted.ToSession();
        store.Put(session);
        if (repo is not null)
        {
            var report = quality.Evaluate(session);
            await repo.UpsertAsync(persisted, report.Score, report.Grade, CancellationToken.None);
        }
    }

    logger.LogInformation("Seeded {Count} session(s) (reset={Reset}).", req.Sessions.Count, req.Reset);
    return Results.Ok(new { seeded = req.Sessions.Count, reset = req.Reset });
});

// Health stays UNAUTHENTICATED (outside the /api group filter): it is the container
// and orchestrator liveness probe, and exposes only counts and feature booleans.
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    sessions = store.All.Count,
    hostlessSignals = store.HostlessSignals,
    persistence = persistenceEnabled,
    forwarding = forwarder.Enabled,
    prometheus = prometheusOptions.Enabled,
    environment = app.Environment.EnvironmentName
}));

// -------------------------------------------------------------- Prometheus
// Scrape endpoint for teams that already run Prometheus/Grafana. Shares the
// ingest key when one is configured: with PerSession enabled this exposes
// session ids, so it must not be more open than the data it summarizes.

if (prometheusOptions.Enabled)
{
    var exporter = app.Services.GetRequiredService<PrometheusExporter>();

    app.MapGet("/metrics", (HttpRequest request) =>
    {
        if (!KeyAuthorized(request)) return Results.Unauthorized();

        // version=0.0.4 is what Prometheus negotiates for the text exposition format.
        return Results.Text(exporter.Render(), "text/plain; version=0.0.4; charset=utf-8");
    });
}

app.MapGet("/", () => Results.Text(
    "CopilotScope collector.\n" +
    "OTLP ingest: POST /v1/traces | /v1/metrics | /v1/logs\n" +
    "API: GET /api/sessions | /api/sessions/{id} | /api/health | POST /api/admin/seed\n" +
    "Prometheus: GET /metrics\n" +
    "UI lives in the CopilotScope.Dashboard Blazor app (run via the Aspire AppHost).\n"));

app.Logger.LogInformation(
    """
    CopilotScope collector started ({Env}).
      OTLP/HTTP ingest : POST /v1/traces | /v1/metrics | /v1/logs
      Query API        : GET /api/sessions
      Prometheus       : {Prom}
      Ingest auth      : {Auth}
      Persistence      : {Persist}
      Forwarding       : {Fwd}
    Point VS Code at this endpoint:
      "github.copilot.chat.otel.enabled": true,
      "github.copilot.chat.otel.otlpEndpoint": "<this host>"
    """,
    app.Environment.EnvironmentName,
    prometheusOptions.Enabled
        ? $"GET /metrics (per-session series: {(prometheusOptions.PerSession ? "on" : "off")})"
        : "disabled",
    string.IsNullOrEmpty(ingestApiKey) ? "disabled (dev)" : "x-api-key required",
    persistenceEnabled ? "Postgres" : "in-memory only",
    forwarder.Enabled ? "enabled" : "disabled");

app.Run();
