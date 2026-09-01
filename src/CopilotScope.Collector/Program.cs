using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Forwarding;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Outcomes;
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
// Registered with an optional SessionRepository so the same read path serves the
// Postgres-backed and in-memory-only deployments.
builder.Services.AddSingleton(sp => new SessionQueryService(
    sp.GetRequiredService<SessionStore>(),
    sp.GetRequiredService<QualityEngine>(),
    sp.GetRequiredService<HistoryOptions>(),
    sp.GetService<SessionRepository>()));

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

// History/retention knobs. Bound even without Postgres so the paging limits below
// behave identically in the in-memory fallback.
var historyOptions = new HistoryOptions();
builder.Configuration.GetSection("CopilotScope:History").Bind(historyOptions);
builder.Services.AddSingleton(historyOptions);

// Outcome linkage: opt-in, and only meaningful with somewhere to store outcomes.
var outcomeOptions = new OutcomeOptions();
builder.Configuration.GetSection("CopilotScope:Outcomes").Bind(outcomeOptions);
builder.Services.AddSingleton(outcomeOptions);

if (persistenceEnabled)
{
    builder.Services.AddSingleton(new SessionRepository(connectionString!));
    builder.Services.AddSingleton<PersistenceWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());

    if (outcomeOptions.Enabled)
        builder.Services.AddSingleton(new OutcomeRepository(connectionString!));
}

var app = builder.Build();

app.MapDefaultEndpoints(); // /health (readiness) + /alive (liveness)

var ingestApiKey = app.Configuration["CopilotScope:Ingest:ApiKey"]; // null/empty → open (dev mode)
var scopedKeys = new ApiKeyOptions();
app.Configuration.GetSection("CopilotScope:Keys").Bind(scopedKeys);

// Scoped, constant-time key check used by every gated surface (/v1, /api, /metrics).
// Keeping it in one place is what lets the whole /api group be gated deny-by-default
// instead of endpoint-by-endpoint — which is how the destructive DELETE used to sit
// unauthenticated. The legacy single key still grants every scope, so an existing
// deployment is unaffected until it opts into CopilotScope:Keys.
var apiKeys = ApiKeyRegistry.Build(ingestApiKey, scopedKeys);
bool KeyAuthorized(HttpRequest request, ApiScope scope = ApiScope.Read) => apiKeys.Authorized(request, scope);

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
    if (!KeyAuthorized(ctx.HttpContext.Request, ApiScope.Ingest))
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

// Deny-by-default: the whole query/admin surface needs at least the Read scope. Reads
// expose captured transcripts, so an ingest-only key — the one handed to every developer's
// editor — must not reach them. Destructive and administrative endpoints re-check for
// Admin below. /api/health is deliberately mapped OUTSIDE this group (further down) so
// liveness probes stay unauthenticated.
api.AddEndpointFilter(async (ctx, next) =>
{
    if (!KeyAuthorized(ctx.HttpContext.Request, ApiScope.Read)) return Results.Unauthorized();
    return await next(ctx);
});

// Reads go through SessionQueryService, which serves Postgres with the live in-memory
// aggregates layered on top. Reading memory alone made a team's history disappear within
// hours of being written, because the store only ever keeps the most recent sessions.
// `days` is the friendly form of the window; `since`/`until` are the precise one.
api.MapGet("/sessions", async (bool? includeInternal, int? days, DateTimeOffset? since, DateTimeOffset? until,
    int? limit, int? offset, SessionQueryService sessions, CancellationToken ct) =>
{
    var from = since ?? (days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null);
    var page = await sessions.PageAsync(includeInternal == true, from, until, limit, offset, ct);
    return Results.Ok(page);
});

api.MapGet("/sessions/{id}", async (string id, SessionQueryService sessions, CancellationToken ct) =>
{
    // Falls back to Postgres for sessions trimmed from memory — a link to last week's
    // session has to keep working.
    if (await sessions.FindAsync(Uri.UnescapeDataString(id), ct) is not { } s) return Results.NotFound();
    var baseline = await sessions.BaselineAsync(ct);

    // Outcome links are opt-in and best-effort: an outcome store that is unreachable must
    // not take the session detail down with it.
    IReadOnlyList<OutcomeLink>? links = null;
    if (app.Services.GetService<OutcomeRepository>() is { } outcomeRepo
        && OutcomeLinker.NormalizeRepository(s.Repository) is { } repo)
    {
        try
        {
            var candidates = await outcomeRepo.ForRepositoryAsync(
                repo, s.FirstSeen.AddDays(-1), s.LastSeen + OutcomeLinker.OpenWindow, ct);
            links = OutcomeLinker.Link(s, candidates);
        }
        catch (Exception ex) { app.Logger.LogDebug(ex, "Outcome lookup failed for {Id}.", s.Id); }
    }

    return Results.Ok(Dto.Detail(s, quality, insightPipeline, baseline, links));
});

api.MapDelete("/sessions/{id}", async (string id, HttpRequest request, ILogger<Program> logger) =>
{
    // Destroying history needs the strongest credential, not merely a readable one.
    if (!KeyAuthorized(request, ApiScope.Admin)) return Results.Unauthorized();
    var key = Uri.UnescapeDataString(id);
    var removed = store.Remove(key);
    // Existing only in Postgres is now the normal case for anything older than the
    // in-memory working set, so the outcome cannot be decided by the store alone: that
    // reported 404 for a session it had just successfully deleted.
    var removedFromDb = false;
    if (app.Services.GetService<SessionRepository>() is { } repo)
    {
        try { removedFromDb = await repo.DeleteAsync(key, CancellationToken.None) > 0; }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to delete session {Id} from Postgres.", key); }
    }
    logger.LogInformation("Session {Id} deleted (memory: {Memory}, database: {Db}).",
        key, removed, removedFromDb);
    return removed || removedFromDb ? Results.NoContent() : Results.NotFound();
});

// Overview aggregates the same window the session list pages through, so "everything you
// burned" means everything, not just what memory still holds. Defaults to the retention
// window when one is configured, otherwise all history.
api.MapGet("/overview", async (int? days, SessionQueryService sessions, CancellationToken ct) =>
{
    var from = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : (DateTimeOffset?)null;
    var all = await sessions.AllInWindowAsync(from, ct);
    return Results.Ok(DtoOverview.Build(all, quality));
});

// ------------------------------------------------------------ admin / seeding
// Lets tools/CopilotScope.Seeder push a local-dev or demo dataset straight into
// a running collector — no Postgres network access and no restart required.
// Shares the ingest API key: anyone who can already post fake OTLP telemetry
// can fabricate session data too, so this doesn't widen the trust boundary.
// Seeded rows are namespaced under the "seed-" id prefix so a reset never
// touches real captured sessions.
const string SeedIdPrefix = "seed-";

api.MapPost("/admin/seed", async (SeedRequest req, HttpRequest request, ILogger<Program> logger) =>
{
    // Fabricating session data is an administrative act; the group filter above only
    // established that the caller may read.
    if (!KeyAuthorized(request, ApiScope.Admin)) return Results.Unauthorized();
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
            await repo.UpsertAsync(persisted, report.Score, report.Grade, CancellationToken.None,
                session.Kind.ToString());
        }
    }

    logger.LogInformation("Seeded {Count} session(s) (reset={Reset}).", req.Sessions.Count, req.Reset);
    return Results.Ok(new { seeded = req.Sessions.Count, reset = req.Reset });
});

// ------------------------------------------------------------ outcome ingestion
// Opt-in: set CopilotScope:Outcomes:WebhookSecret and point a GitHub webhook here.
// Deliberately OUTSIDE the /api key group — GitHub authenticates with its own HMAC
// signature over the raw body, and cannot send an x-api-key header.
if (outcomeOptions.Enabled && app.Services.GetService<OutcomeRepository>() is { } outcomes)
{
    // Postgres is commonly not accepting connections yet when the collector starts under
    // compose. Persistence tolerates that and degrades; an unguarded schema call here would
    // instead take the whole collector down, losing ingest over an opt-in side feature.
    // The webhook route re-attempts the schema on its first delivery.
    var outcomeSchemaReady = false;
    try
    {
        await outcomes.EnsureSchemaAsync(CancellationToken.None);
        outcomeSchemaReady = true;
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "Outcome schema not ready at startup (is Postgres up?) — will retry on first webhook delivery.");
    }

    app.MapPost("/api/outcomes/github", async (HttpRequest request, ILogger<Program> logger) =>
    {
        // Read the raw bytes: the HMAC is over exactly what GitHub sent, so any
        // deserialize-and-reserialize round trip would break verification.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();

        if (!GitHubWebhook.VerifySignature(body, request.Headers["X-Hub-Signature-256"].FirstOrDefault(),
                outcomeOptions.WebhookSecret))
        {
            logger.LogWarning("Rejected outcome webhook from {RemoteIp}: bad or missing signature.",
                request.HttpContext.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }

        if (!outcomeSchemaReady)
        {
            await outcomes.EnsureSchemaAsync(CancellationToken.None);
            outcomeSchemaReady = true;
        }

        var eventName = request.Headers["X-GitHub-Event"].FirstOrDefault() ?? "";
        using var document = System.Text.Json.JsonDocument.Parse(body);

        if (GitHubWebhook.Parse(eventName, document.RootElement) is { } outcome)
        {
            await outcomes.UpsertAsync(outcome, CancellationToken.None);
            return Results.Ok(new { recorded = $"{outcome.Repository}#{outcome.Number}" });
        }

        if (eventName == "push")
        {
            var reverted = 0;
            foreach (var (repo, number, at) in GitHubWebhook.ParseReverts(document.RootElement))
                if (await outcomes.MarkRevertedAsync(repo, number, at, CancellationToken.None))
                    reverted++;
            return Results.Ok(new { reverted });
        }

        return Results.Ok(new { ignored = eventName });
    });
}

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
        // Read scope: with per-session series enabled this exposes session ids, so it must
        // not be more open than the API that summarizes the same data.
        if (!KeyAuthorized(request, ApiScope.Read)) return Results.Unauthorized();

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
    apiKeys.Describe(),
    persistenceEnabled ? "Postgres" : "in-memory only",
    forwarder.Enabled ? "enabled" : "disabled");

app.Run();
