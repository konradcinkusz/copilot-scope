using CopilotScope.Collector.Alerting;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Calibration;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Forwarding;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Outcomes;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Privacy;
using CopilotScope.Collector.Quality;
using CopilotScope.Collector.Vendor;
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
// Workflow-friction signals (docs/WORKFLOW_FRICTION.md). Off unless explicitly enabled:
// it is the one analyzer that reads the developer's own prompt text, and it is the one an
// EU deployer has to be able to point at and say "that is not running here". The options
// bind from the built container's configuration, and the pipeline skips the analyzer when
// they say off — a flag read before a host wrapper's sources land is how a feature ends up
// running in a deployment that switched it off.
builder.Services.AddSingleton(sp =>
{
    var options = new WorkflowFrictionOptions();
    sp.GetRequiredService<IConfiguration>().GetSection("CopilotScope:WorkflowFriction").Bind(options);
    return options;
});
// Registered as itself as well as through the interface: the aggregate endpoint needs this
// one analyzer over every session in a window, and going through the pipeline there would run
// the other four for a number nobody asked for.
builder.Services.AddSingleton<WorkflowFrictionAnalyzer>();
builder.Services.AddSingleton<IInsightAnalyzer>(sp => sp.GetRequiredService<WorkflowFrictionAnalyzer>());
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

// Outbound alerts and the weekly digest (docs/TUTORIAL.md §11). Off by default: this is the
// only thing in the collector that sends data anywhere, so it needs an explicit decision rather
// than a default. Registered only when configured, so a deployment that has not opted in runs
// no extra loop and holds no HttpClient pointed at anything.
var alertOptions = new AlertOptions();
builder.Configuration.GetSection("CopilotScope:Alerts").Bind(alertOptions);
builder.Services.AddSingleton(alertOptions);
builder.Services.AddHttpClient<AlertDispatcher>(c => c.Timeout = TimeSpan.FromSeconds(10));
if (alertOptions.Active) builder.Services.AddHostedService<AlertService>();

// Vendor usage archiving (docs/TUTORIAL.md §12). GitHub's Copilot Metrics API keeps 28 days and
// nothing older; this saves the window before it expires. Context beside the quality score, never
// instead of it — counting usage still does not tell you whether the tooling is helping.
// Bound from the built container's IConfiguration, like privacy and labelling: sources a host
// wrapper adds land after CreateBuilder, and binding early would silently leave archiving off in
// a deployment that configured it — which nobody notices until the vendor's window has expired.
builder.Services.AddSingleton(sp =>
{
    var options = new VendorMetricsOptions();
    sp.GetRequiredService<IConfiguration>().GetSection("CopilotScope:VendorMetrics").Bind(options);
    return options;
});
builder.Services.AddSingleton<VendorMetricsCache>();
builder.Services.AddSingleton<VendorMetricsSnapshot>();

// Human session labelling (docs/CALIBRATION.md §8). Off by default: it puts a write control on
// a read-only surface, and most deployments are not running a labelling study. Without it the
// calibration machinery in JudgeAgent has nothing to consume, which is why the composite is
// still "an opinion with a confidence interval" rather than a measurement.
builder.Services.AddSingleton(sp =>
{
    var options = new LabellingOptions();
    sp.GetRequiredService<IConfiguration>().GetSection("CopilotScope:Labelling").Bind(options);
    return options;
});
builder.Services.AddSingleton<LabelStore>();

// Privacy mode (docs/PRIVACY.md). Off by default; on, it pseudonymizes identity at ingest,
// drops prompt/response content, applies an aggregation floor to every view, and logs reads.
// Bound from the built container's IConfiguration rather than from builder.Configuration:
// sources added by a host wrapper (a test host, a sidecar) land after CreateBuilder has run,
// so binding early would silently read defaults and report privacy mode off while it is on.
builder.Services.AddSingleton(sp =>
{
    var options = new PrivacyOptions();
    sp.GetRequiredService<IConfiguration>().GetSection("CopilotScope:Privacy").Bind(options);
    return options;
});
builder.Services.AddSingleton(sp => new Pseudonymizer(sp.GetRequiredService<PrivacyOptions>().Salt));
builder.Services.AddSingleton<PrivacyRedactor>();
builder.Services.AddSingleton<PrivacyGuard>();
builder.Services.AddSingleton<AccessAuditLog>();

if (persistenceEnabled)
{
    builder.Services.AddSingleton(new SessionRepository(connectionString!));
    builder.Services.AddSingleton<PersistenceWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());

    if (outcomeOptions.Enabled)
        builder.Services.AddSingleton(new OutcomeRepository(connectionString!));

    // Archiving needs somewhere to archive to. Without Postgres the poll would fetch a window
    // and drop it, which is worse than not running: it spends an org's rate limit to keep
    // nothing.
    // Registered whenever Postgres is present and switched on at runtime by
    // VendorMetricsOptions.Active — the options are not yet bound here, and guessing them is how
    // a deployment ends up with archiving that reports itself configured while polling nothing.
    builder.Services.AddSingleton(new VendorMetricsRepository(connectionString!));
    builder.Services.AddHttpClient<IVendorMetricsSource, GitHubCopilotMetricsSource>(
        c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddHostedService<VendorMetricsArchiver>();

    // Labels are the most expensive data here — a rater's minute per session — so they get a
    // durable home whenever there is one, restored at startup below.
    builder.Services.AddSingleton(new LabelRepository(connectionString!));

    // The audit record must outlive the sessions it describes, so it gets its own table and
    // is never touched by the session retention sweep. Registered whenever Postgres is
    // present and switched on at runtime by AccessAuditLog.Enabled — the privacy options are
    // not yet bound at this point, and guessing them here is how a deployment ends up with
    // an audit log that reports itself as durable while writing nowhere.
    builder.Services.AddSingleton(new AccessAuditRepository(connectionString!));
    builder.Services.AddHostedService<AccessAuditWriter>();
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
var privacyOptions = app.Services.GetRequiredService<PrivacyOptions>();
var redactor = app.Services.GetRequiredService<PrivacyRedactor>();
var privacyGuard = app.Services.GetRequiredService<PrivacyGuard>();
var audit = app.Services.GetRequiredService<AccessAuditLog>();

// Raw forwarding relays the payload exactly as it arrived — before redaction, by design,
// since a faithful relay is the whole point. Under privacy mode that is a hole straight
// through every control here, so it is refused unless the operator states that the upstream
// backend is in scope of the same works agreement.
var forwardRaw = !privacyOptions.Enabled || privacyOptions.AllowRawForwarding;
if (forwarder.Enabled && !forwardRaw)
    app.Logger.LogWarning(
        "Privacy mode is on and CopilotScope:Privacy:AllowRawForwarding is not set — OTLP forwarding is " +
        "DISABLED. Raw forwarding relays un-redacted payloads upstream; set AllowRawForwarding=true only " +
        "if the upstream backend is covered by the same data-processing agreement.");
if (privacyOptions.Enabled && app.Services.GetRequiredService<Pseudonymizer>().SaltIsEphemeral)
    app.Logger.LogWarning(
        "Privacy mode is on but CopilotScope:Privacy:Salt is not set — an ephemeral salt was generated. " +
        "Pseudonyms will change on every restart, so history stops correlating across a deploy. " +
        "Set a stable secret salt before this is more than a trial.");

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

    // Redact BEFORE anything aggregates the batch: nothing downstream — the store, the
    // write-behind snapshot, the Prometheus exporter — then ever holds the identifying value,
    // so the guarantee survives a database dump rather than depending on every read path.
    redactor.Apply(batch);

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

    if (forwardRaw) forwarder.Enqueue($"/v1/{signal}", payload);

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
    int? limit, int? offset, string? repository, string? emitter, string? model, string? kind, string? grade,
    HttpRequest request, SessionQueryService sessions, CancellationToken ct) =>
{
    var from = since ?? (days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null);
    var cohort = CohortFilter.From(repository, emitter, model, kind, grade);
    var page = await sessions.PageAsync(includeInternal == true, from, until, limit, offset, ct, cohort);

    // The aggregation floor is applied to what the caller would actually see. Filtering a
    // "team" view down to one repository worked on by one person is how a pseudonymous list
    // becomes a named one, and the filters that do it are the query parameters above.
    var verdict = privacyGuard.EvaluateSubjects(page.Subjects);
    var actor = AccessAuditLog.ActorFor(request);
    audit.Record(actor, "sessions.list",
        $"{DescribeQuery(days, since, until, limit, offset)} {cohort.Describe()}".Trim(),
        verdict.Allowed ? $"served {page.Sessions.Count} session(s)" : "withheld (k-anonymity)");

    return verdict.Allowed
        ? Results.Ok(page)
        : Results.Ok(page.Suppressed(verdict.Reason));
});

// One line describing what a read covered, for the audit record. Deliberately the query,
// not the result: an auditor asks what was looked for, not what happened to match.
static string DescribeQuery(int? days, DateTimeOffset? since, DateTimeOffset? until, int? limit, int? offset)
{
    var parts = new List<string>();
    if (days is > 0) parts.Add($"days={days}");
    if (since is { } s) parts.Add($"since={s.UtcDateTime:O}");
    if (until is { } u) parts.Add($"until={u.UtcDateTime:O}");
    if (limit is > 0) parts.Add($"limit={limit}");
    if (offset is > 0) parts.Add($"offset={offset}");
    return parts.Count > 0 ? string.Join(" ", parts) : "all";
}

api.MapGet("/sessions/{id}", async (string id, HttpRequest request, SessionQueryService sessions, CancellationToken ct) =>
{
    var requested = Uri.UnescapeDataString(id);
    var actor = AccessAuditLog.ActorFor(request);

    // A single session is a group of one, so drilling into it is precisely the
    // individual-level inspection the aggregation floor exists to prevent. Refused before
    // the lookup: whether the session exists is itself information about one person.
    if (privacyGuard.SessionDetailSuppressed)
    {
        audit.Record(actor, "sessions.detail", requested, "refused (privacy mode)");
        return Results.Json(new
        {
            error = "Per-session detail is disabled under privacy mode.",
            reason = "A single session covers one subject, below any k-anonymity floor. " +
                     "Set CopilotScope:Privacy:SuppressSessionDetail=false only where the works " +
                     "agreement permits individual review.",
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    // Falls back to Postgres for sessions trimmed from memory — a link to last week's
    // session has to keep working.
    if (await sessions.FindAsync(requested, ct) is not { } s)
    {
        audit.Record(actor, "sessions.detail", requested, "not found");
        return Results.NotFound();
    }
    audit.Record(actor, "sessions.detail", requested, "served");
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
    // Irreversible, and it destroys the record of one person's work — exactly the act an
    // access log has to be able to account for afterwards.
    audit.Record(AccessAuditLog.ActorFor(request), "sessions.delete", key,
        removed || removedFromDb ? "deleted" : "not found");
    return removed || removedFromDb ? Results.NoContent() : Results.NotFound();
});

// Overview aggregates the same window the session list pages through, so "everything you
// burned" means everything, not just what memory still holds. Defaults to the retention
// window when one is configured, otherwise all history.
// Which signals each assistant actually emits, and therefore which quality components it can
// never populate. Served from the same table the dashboard renders and the tests assert against,
// so the disclosure cannot drift from the behaviour it describes.
api.MapGet("/coverage", () => Results.Ok(EmitterCoverage.All.Select(e => new
{
    emitter = e.Emitter.ToString(),
    name = e.DisplayName,
    traces = e.Traces.ToString(),
    metrics = e.Metrics.ToString(),
    events = e.Events.ToString(),
    editDecisions = e.EditDecisions.ToString(),
    editSurvival = e.EditSurvival.ToString(),
    feedback = e.Feedback.ToString(),
    timeToFirstToken = e.TimeToFirstToken.ToString(),
    alwaysPrior = e.AlwaysPrior,
    note = e.Note
})));

api.MapGet("/overview", async (int? days, DateTimeOffset? since, DateTimeOffset? until,
    string? repository, string? emitter, string? model, string? kind, string? grade,
    HttpRequest request, SessionQueryService sessions, CancellationToken ct) =>
{
    var from = since ?? (days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null);
    var cohort = CohortFilter.From(repository, emitter, model, kind, grade);
    var all = await InCohort(sessions, from, until, cohort, ct);

    var verdict = privacyGuard.Evaluate(all);
    audit.Record(AccessAuditLog.ActorFor(request), "overview",
        $"{DescribeQuery(days, since, until, null, null)} {cohort.Describe()}".Trim(),
        verdict.Allowed ? $"served {all.Count} session(s)" : "withheld (k-anonymity)");

    return verdict.Allowed
        ? Results.Ok(DtoOverview.Build(all, quality))
        : Results.Json(new { suppressed = true, reason = verdict.Reason, subjects = verdict.Subjects, required = verdict.Required },
            statusCode: StatusCodes.Status403Forbidden);
});

// The window plus the cohort, with the grade half applied here because it needs a score.
// Shared by every aggregate endpoint so they cannot disagree about what a cohort contains.
async Task<IReadOnlyCollection<CopilotSession>> InCohort(SessionQueryService sessions,
    DateTimeOffset? from, DateTimeOffset? to, CohortFilter cohort, CancellationToken ct)
{
    var all = await sessions.AllInWindowAsync(from, ct, to, cohort);
    return cohort.Grade is null
        ? all
        : all.Where(s => cohort.MatchesGrade(quality.Evaluate(s).Grade)).ToList();
}

// ------------------------------------------------------------------- cohorts
// Rollups by repository, assistant, model and session kind — "where is the spend going, and
// which slice is going badly". This is the view a platform lead evaluating assistants needs,
// and it deliberately has no developer axis: every dimension describes the tooling.
api.MapGet("/cohorts", async (int? days, DateTimeOffset? since, DateTimeOffset? until,
    string? repository, string? emitter, string? model, string? kind, string? grade, string? format,
    HttpRequest request, SessionQueryService sessions, CancellationToken ct) =>
{
    var from = since ?? (days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null);
    var cohort = CohortFilter.From(repository, emitter, model, kind, grade);
    var all = await InCohort(sessions, from, until, cohort, ct);

    var verdict = privacyGuard.Evaluate(all);
    audit.Record(AccessAuditLog.ActorFor(request), "cohorts",
        $"{DescribeQuery(days, since, until, null, null)} {cohort.Describe()}".Trim(),
        verdict.Allowed ? $"served {all.Count} session(s)" : "withheld (k-anonymity)");
    if (!verdict.Allowed)
        return Results.Json(new { suppressed = true, reason = verdict.Reason, subjects = verdict.Subjects, required = verdict.Required },
            statusCode: StatusCodes.Status403Forbidden);

    var report = Cohorts.Build(all, quality, from, until);
    return IsCsv(format)
        ? Results.Text(CohortExport.ToCsv(report), "text/csv; charset=utf-8")
        : Results.Ok(report);
});

// --------------------------------------------------------------- vendor usage
// The archive, including everything past the vendor's own 28-day horizon. Framed in the payload
// itself as context rather than as the measurement: this project's claim is that counting usage
// does not tell you whether the tooling is helping, and an endpoint that quietly implied
// otherwise would undercut the thing it sits next to.
api.MapGet("/vendor/metrics", async (int? days, HttpRequest request, VendorMetricsOptions vendorOptions,
    CancellationToken ct) =>
{
    if (!vendorOptions.Active)
        return Results.Json(new
        {
            enabled = false,
            reason = "Vendor usage archiving is off. Set CopilotScope:VendorMetrics with a scope " +
                     "and a read-only token; see docs/TUTORIAL.md §12.",
        }, statusCode: StatusCodes.Status409Conflict);

    if (app.Services.GetService<VendorMetricsRepository>() is not { } vendorRepo)
        return Results.Json(new { enabled = false, reason = "Archiving needs Postgres." },
            statusCode: StatusCodes.Status409Conflict);

    var window = Math.Clamp(days ?? 90, 1, 3650);
    var archive = await vendorRepo.ReadAsync("github", vendorOptions.Scope, window, ct);
    var cache = app.Services.GetRequiredService<VendorMetricsCache>();

    audit.Record(AccessAuditLog.ActorFor(request), "vendor.metrics", $"days={window}",
        $"served {archive.Count} day(s)");

    return Results.Ok(new
    {
        enabled = true,
        provider = "github",
        scope = vendorOptions.Scope,
        lastPoll = cache.LastPoll,
        vendorWindowDays = cache.LastWindowDays,
        // The number that says what the archive is for: days held beyond what the vendor still
        // serves. Zero means archiving has not outrun the window yet.
        daysBeyondVendorWindow = Math.Max(0, archive.Count - 28),
        note = "Vendor usage counts, archived past their 28-day expiry. Context for the quality " +
               "score, not a substitute for it — usage volume does not say whether the tooling helped.",
        days = archive.Select(d => new
        {
            day = d.Day,
            totalActiveUsers = d.TotalActiveUsers,
            totalEngagedUsers = d.TotalEngagedUsers,
            completionsEngagedUsers = d.CompletionsEngagedUsers,
            chatEngagedUsers = d.ChatEngagedUsers,
            dotcomChatEngagedUsers = d.DotcomChatEngagedUsers,
            pullRequestEngagedUsers = d.PullRequestEngagedUsers,
        }),
    });
});

// ---------------------------------------------------------------- labelling
// The four-band scale and the five rubric questions, served from the same table the judge
// prompt and the calibration engine read. A rater and the judge have to be answering the same
// sentence, or the agreement statistic measures the difference between two forms.
api.MapGet("/labels/rubrics", (LabellingOptions labelling) => Results.Ok(new
{
    enabled = labelling.Enabled,
    categories = RubricScale.Categories,
    bands = RubricScale.Bands.Select(b => new { b.Level, b.Name, b.Lower, b.Upper, b.Anchor }),
    rubrics = RubricScale.Rubrics.Values.Select(r => new
    {
        algorithm = r.Algorithm,
        question = r.Question,
        // Four rubrics ask "how good"; deep-friction asks how much repair was needed and runs
        // the other way. A rater who reads band 3 as "great session" on that one would be
        // recorded as maximally disagreeing with a judge that got it right.
        higherIsBetter = r.HigherIsBetter,
    }),
}));

// Existing judgments for a session, so a rater can see what they already recorded rather than
// re-rating from memory.
api.MapGet("/labels", (string? sessionId, LabelStore labels) =>
    Results.Ok(string.IsNullOrEmpty(sessionId) ? labels.All() : labels.ForSession(sessionId)));

// Records one rater's judgments for one session. Read scope, not Admin: a rater is a person
// with dashboard access running a study, and requiring the credential that can wipe history in
// order to fill in a form would mean nobody ever does.
api.MapPost("/labels", async (List<SessionLabel> submitted, HttpRequest request, LabelStore labels,
    LabellingOptions labelling, CancellationToken ct) =>
{
    if (!labelling.Enabled)
        return Results.Json(new
        {
            error = "Labelling is off.",
            detail = "Set CopilotScope:Labelling:Enabled; see docs/CALIBRATION.md §8.",
        }, statusCode: StatusCodes.Status409Conflict);

    var labelRepo = app.Services.GetService<LabelRepository>();
    var accepted = 0;
    var errors = new List<string>();
    foreach (var label in submitted)
    {
        var stamped = label with { At = DateTimeOffset.UtcNow };
        if (!labels.Record(stamped, out var error)) { errors.Add($"{label.Algorithm}: {error}"); continue; }
        accepted++;

        // Awaited, not queued: a human clicking Save is a write rate that can afford a round
        // trip, and "saved" has to mean saved. A database failure is reported to the rater
        // rather than swallowed — the label is still in memory and the export still holds it,
        // but they need to know it will not survive a restart.
        if (labelRepo is not null)
        {
            try { await labelRepo.UpsertAsync(labels.ForSession(stamped.SessionId)
                    .First(l => l.Algorithm == RubricScale.Canonical(stamped.Algorithm)), ct); }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Could not persist a label for {Session}.", stamped.SessionId);
                errors.Add($"{label.Algorithm}: recorded in memory, but the database write failed.");
            }
        }
    }

    audit.Record(AccessAuditLog.ActorFor(request), "labels.write",
        submitted.FirstOrDefault()?.SessionId, $"accepted {accepted}/{submitted.Count}");

    return errors.Count == 0
        ? Results.Ok(new { accepted, total = labels.Count })
        : Results.BadRequest(new { accepted, errors });
});

// The dataset, in exactly the shape calibration/labels.example.json uses — so a study's output
// drops into the calibration engine with no hand-editing.
api.MapGet("/labels/export", (bool? includeSynthetic, string? datasetVersion, HttpRequest request,
    LabelStore labels) =>
{
    var synthetic = includeSynthetic == true;
    audit.Record(AccessAuditLog.ActorFor(request), "labels.export",
        synthetic ? "includeSynthetic=true" : null, $"{labels.Count} label(s) held");
    return Results.Ok(labels.Export(synthetic, datasetVersion));
});

// ------------------------------------------------------------------- digest
// The aggregate week, as the artefact a lead forwards instead of a dashboard link. Available
// on demand whether or not the scheduled webhook is configured — reading it costs nothing and
// a team that has not set up a webhook still wants the summary.
api.MapGet("/digest", async (int? days, HttpRequest request, SessionQueryService sessions, CancellationToken ct) =>
{
    var window = Math.Clamp(days ?? alertOptions.WindowDays, 1, 365);
    var until = DateTimeOffset.UtcNow;
    var since = until.AddDays(-window);
    var baselineSince = since.AddDays(-window);

    var currentSessions = await sessions.AllInWindowAsync(since, ct, until);
    var baselineSessions = await sessions.AllInWindowAsync(baselineSince, ct, since);

    var verdict = privacyGuard.Evaluate(currentSessions.Concat(baselineSessions));
    audit.Record(AccessAuditLog.ActorFor(request), "digest", $"days={window}",
        verdict.Allowed ? $"served {currentSessions.Count} session(s)" : "withheld (k-anonymity)");
    if (!verdict.Allowed)
        return Results.Json(new { suppressed = true, reason = verdict.Reason, subjects = verdict.Subjects, required = verdict.Required },
            statusCode: StatusCodes.Status403Forbidden);

    var current = Cohorts.Build(currentSessions, quality, since, until);
    var baseline = Cohorts.Build(baselineSessions, quality, baselineSince, since);
    var report = Digest.Build(current, baseline,
        RegressionDetector.Detect(baseline, current, alertOptions), since, until);

    return Results.Ok(report);
});

// Sends the digest now, to the configured webhook. Admin scope: it puts the team's numbers on
// an external service, which is not something a read credential should be able to trigger.
api.MapPost("/digest/send", async (HttpRequest request, SessionQueryService sessions,
    AlertDispatcher dispatcher, CancellationToken ct) =>
{
    if (!KeyAuthorized(request, ApiScope.Admin)) return Results.Unauthorized();
    if (!alertOptions.Active)
        return Results.Json(new
        {
            error = "No alert webhook is configured.",
            detail = "Set CopilotScope:Alerts:Enabled and CopilotScope:Alerts:WebhookUrl; " +
                     "see docs/TUTORIAL.md §11.",
        }, statusCode: StatusCodes.Status409Conflict);

    var window = alertOptions.WindowDays;
    var until = DateTimeOffset.UtcNow;
    var since = until.AddDays(-window);
    var baselineSince = since.AddDays(-window);

    var currentSessions = await sessions.AllInWindowAsync(since, ct, until);
    var baselineSessions = await sessions.AllInWindowAsync(baselineSince, ct, since);

    var verdict = privacyGuard.Evaluate(currentSessions.Concat(baselineSessions));
    audit.Record(AccessAuditLog.ActorFor(request), "digest.send", $"days={window}",
        verdict.Allowed ? "sent" : "withheld (k-anonymity)");
    if (!verdict.Allowed)
        return Results.Json(new { suppressed = true, reason = verdict.Reason },
            statusCode: StatusCodes.Status403Forbidden);

    var current = Cohorts.Build(currentSessions, quality, since, until);
    var baseline = Cohorts.Build(baselineSessions, quality, baselineSince, since);
    var report = Digest.Build(current, baseline,
        RegressionDetector.Detect(baseline, current, alertOptions), since, until);

    var sent = await dispatcher.SendAsync("digest", report, Digest.ToText(report), ct);
    return sent
        ? Results.Ok(new { sent = true, sessions = report.Sessions, regressions = report.Regressions.Count })
        : Results.Json(new { sent = false, error = "The webhook did not accept the digest; see the collector log." },
            statusCode: StatusCodes.Status502BadGateway);
});

// Distinct values worth filtering on, so the UI offers what exists instead of a free-text box
// that silently matches nothing.
api.MapGet("/facets", async (int? days, SessionQueryService sessions, CancellationToken ct) =>
{
    var from = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : (DateTimeOffset?)null;
    var (repositories, assistants, models) = await sessions.FacetsAsync(from, ct);
    return Results.Ok(new { repositories, assistants, models });
});

// Before/after for one cohort: did the model upgrade help, is the new assistant better. The
// buying question, answered in one request instead of two dashboards and mental subtraction.
api.MapGet("/compare", async (DateTimeOffset? baselineSince, DateTimeOffset? baselineUntil,
    DateTimeOffset? since, DateTimeOffset? until, int? days,
    string? repository, string? emitter, string? model, string? kind, string? grade, string? format,
    HttpRequest request, SessionQueryService sessions, CancellationToken ct) =>
{
    var currentFrom = since ?? (days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null);
    // Default the baseline to the window immediately before the current one, which is the
    // comparison a reader means by "before". Requires a bounded current window to infer from.
    var length = currentFrom is { } cf ? (until ?? DateTimeOffset.UtcNow) - cf : (TimeSpan?)null;
    var baseFrom = baselineSince ?? (length is { } l && currentFrom is { } c ? c - l : null);
    var baseUntil = baselineUntil ?? currentFrom;

    if (baseFrom is null || currentFrom is null)
        return Results.BadRequest(new
        {
            error = "A comparison needs two bounded windows.",
            detail = "Pass days (or since) for the current window; baselineSince/baselineUntil " +
                     "default to the equally long window immediately before it.",
        });

    var cohort = CohortFilter.From(repository, emitter, model, kind, grade);
    var current = await InCohort(sessions, currentFrom, until, cohort, ct);
    var baseline = await InCohort(sessions, baseFrom, baseUntil, cohort, ct);

    // The floor applies to the union: two windows each just under k would otherwise be a way
    // of reading a small group twice.
    var verdict = privacyGuard.Evaluate(current.Concat(baseline));
    audit.Record(AccessAuditLog.ActorFor(request), "compare",
        $"baseline={baseFrom:O}..{baseUntil:O} current={currentFrom:O}..{until:O} {cohort.Describe()}".Trim(),
        verdict.Allowed ? $"served {current.Count}/{baseline.Count} session(s)" : "withheld (k-anonymity)");
    if (!verdict.Allowed)
        return Results.Json(new { suppressed = true, reason = verdict.Reason, subjects = verdict.Subjects, required = verdict.Required },
            statusCode: StatusCodes.Status403Forbidden);

    var report = Cohorts.Compare(cohort.Describe(),
        baseline, baseFrom, baseUntil, current, currentFrom, until, quality);

    return IsCsv(format)
        ? Results.Text(CohortExport.ToCsv(report), "text/csv; charset=utf-8")
        : Results.Ok(report);
});

static bool IsCsv(string? format) => string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);

// ------------------------------------------------------------- workflow friction
// The aggregate-first surface for workflow-friction signals: a team/period rate, never a
// per-person one. Individual sessions carry the same signal in their insight report, but the
// question worth asking — "is our tooling making people repeat themselves" — is answered
// here, at a level that cannot be turned into a ranking. Subject to the same aggregation
// floor as every other view.
api.MapGet("/friction", async (int? days, HttpRequest request, SessionQueryService sessions,
    WorkflowFrictionOptions friction, WorkflowFrictionAnalyzer analyzer, CancellationToken ct) =>
{
    if (!friction.Enabled)
        return Results.Json(new
        {
            enabled = false,
            reason = "Workflow-friction analysis is off. Set CopilotScope:WorkflowFriction:Enabled " +
                     "to turn it on; see docs/WORKFLOW_FRICTION.md for what it does and does not measure.",
        }, statusCode: StatusCodes.Status409Conflict);

    var from = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : (DateTimeOffset?)null;
    var all = await sessions.AllInWindowAsync(from, ct);

    var verdict = privacyGuard.Evaluate(all);
    audit.Record(AccessAuditLog.ActorFor(request), "friction.aggregate", days is > 0 ? $"days={days}" : "all",
        verdict.Allowed ? $"served {all.Count} session(s)" : "withheld (k-anonymity)");
    if (!verdict.Allowed)
        return Results.Json(new { suppressed = true, reason = verdict.Reason, subjects = verdict.Subjects, required = verdict.Required },
            statusCode: StatusCodes.Status403Forbidden);

    var scored = all
        .Select(analyzer.Analyze)
        .Where(r => r is { Status: "ok", Score: not null })
        .Select(r => r.Score!.Value)
        .ToList();

    return Results.Ok(new
    {
        enabled = true,
        windowDays = days,
        sessionsInWindow = all.Count,
        // Sessions without captured content cannot carry the signal at all, and reporting a
        // rate over a denominator that silently excludes them would overstate it.
        sessionsWithContent = scored.Count,
        meanFrictionIndex = scored.Count > 0 ? Math.Round(scored.Average(), 3) : (double?)null,
        sessionsWithRepairMarkers = scored.Count(v => v >= friction.FlagThreshold),
        threshold = friction.FlagThreshold,
        note = "Counts observed repair events (re-asking, corrections, rephrasing) — not emotional state. " +
               "Report-only; never part of the composite score.",
    });
});

// ------------------------------------------------------------------- privacy
// What privacy mode is actually enforcing right now, with live counters. A works council or
// a DPO asks to see the control, not the setting — and an operator needs to confirm the
// deployment matches the annex they signed. Read scope: it exposes no session data.
api.MapGet("/privacy", () => Results.Ok(new
{
    enabled = privacyOptions.Enabled,
    mode = privacyOptions.Describe(),
    minimumGroupSize = privacyOptions.MinimumGroupSize,
    sessionDetailSuppressed = privacyGuard.SessionDetailSuppressed,
    transcriptsRetained = !privacyOptions.Enabled,
    pseudonymizeBranch = privacyOptions.PseudonymizeBranch,
    rawForwarding = forwarder.Enabled && forwardRaw,
    saltConfigured = !app.Services.GetRequiredService<Pseudonymizer>().SaltIsEphemeral,
    auditLog = audit.Enabled,
    auditDurable = audit.Enabled && app.Services.GetService<AccessAuditRepository>() is not null,
    counters = new
    {
        attributesPseudonymized = redactor.AttributesPseudonymized,
        contentDropped = redactor.ContentDropped,
        accessesRecorded = audit.Recorded,
    },
    documentation = "docs/PRIVACY.md",
}));

// The access log itself. Admin scope: it names who looked at what, which is exactly the
// kind of record that should not be readable by everyone it describes. `format=csv` is what
// gets handed to a DPO or a works council.
api.MapGet("/audit", async (HttpRequest request, string? format, int? limit, CancellationToken ct) =>
{
    if (!KeyAuthorized(request, ApiScope.Admin)) return Results.Unauthorized();
    if (!audit.Enabled)
        return Results.Json(new { error = "The access audit log is off. Enable CopilotScope:Privacy." },
            statusCode: StatusCodes.Status409Conflict);

    var take = Math.Clamp(limit ?? 500, 1, 50_000);

    // Prefer the durable record: the in-memory tail is only the recent slice, and an export
    // that silently stops at the last restart would misrepresent itself as complete.
    List<AccessAuditEntry> entries;
    if (audit.Enabled && app.Services.GetService<AccessAuditRepository>() is { } auditRepo)
    {
        try { entries = await auditRepo.RecentAsync(take, ct); }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Audit export fell back to the in-memory tail.");
            entries = audit.Recent(take).ToList();
        }
    }
    else entries = audit.Recent(take).ToList();

    // Reading the audit log is itself an access worth recording.
    audit.Record(AccessAuditLog.ActorFor(request), "audit.export", $"limit={take}", $"served {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");

    return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
        ? Results.Text(AccessAuditLog.ToCsv(entries), "text/csv; charset=utf-8")
        : Results.Ok(entries);
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

// ------------------------------------------------------------------ log import
// Sessions reconstructed from an assistant's own local transcript files
// (tools/CopilotScope.LogImporter). Most developers never flip OTEL env vars, and this is the
// path that scores their existing history without asking them to.
//
// Deliberately NOT the seed endpoint: seeding namespaces everything under "seed-" precisely so
// it can never touch real captured sessions, and an import has to use the assistant's own
// session id — that is what makes re-running it idempotent instead of duplicating a year of
// history on every run.
api.MapPost("/import", async (ImportRequest req, HttpRequest request, ILogger<Program> logger,
    CancellationToken ct) =>
{
    // Fabricating session data is administrative, exactly as seeding is.
    if (!KeyAuthorized(request, ApiScope.Admin)) return Results.Unauthorized();

    // Resolved here rather than injected: SessionRepository is only registered when Postgres is
    // configured, and a minimal-API handler parameter for an unregistered service is bound as a
    // second request body — which fails route building for the whole application, not just this
    // endpoint.
    var repo = app.Services.GetService<SessionRepository>();

    var rejected = new List<string>();
    int imported = 0, updated = 0, skipped = 0;

    foreach (var persisted in req.Sessions)
    {
        var session = persisted.ToSession();

        // The origin has to be declared by the caller and has to be an import. Accepting a
        // snapshot that claims to be OTLP would let this endpoint forge live telemetry.
        if (session.Origin != SessionOrigin.LogImport)
        {
            rejected.Add($"{session.Id}: origin must be '{SessionOrigin.LogImport}', got '{session.Origin}'.");
            continue;
        }

        // An imported session is reconstructed from a file and carries no latency samples and
        // no edit decisions; a live one carries both. Overwriting a live session with the
        // import of the same conversation would therefore *lose* evidence — silently, and in
        // the direction that lowers its score. Refuse, and say so.
        var existing = store.Get(session.Id)
            ?? (repo is not null ? (await repo.GetAsync(session.Id, ct))?.ToSession() : null);
        if (existing is not null && existing.Origin != SessionOrigin.LogImport)
        {
            skipped++;
            rejected.Add($"{session.Id}: already present from live telemetry; import would lose signal.");
            continue;
        }

        if (existing is null) imported++; else updated++;

        // Replace rather than merge: re-importing the same file must be idempotent, and the
        // file is the whole truth about that session. Merging would double every token on the
        // second run.
        store.Put(session);
        if (repo is not null)
        {
            var report = quality.Evaluate(session);
            await repo.UpsertAsync(PersistedSession.From(session), report.Score, report.Grade, ct,
                session.Kind.ToString());
        }
    }

    logger.LogInformation("Imported {Imported} new / {Updated} updated / {Skipped} skipped session(s) from local transcripts.",
        imported, updated, skipped);
    return Results.Ok(new ImportResult(imported, updated, skipped, rejected));
});

// Labels are restored before the first request: a rater reopening a session has to see the
// judgment they already recorded, not a blank form that invites them to disagree with themselves.
if (app.Services.GetService<LabelRepository>() is { } labelStartupRepo
    && app.Services.GetRequiredService<LabellingOptions>().Enabled)
{
    try
    {
        await labelStartupRepo.EnsureSchemaAsync(CancellationToken.None);
        var restored = await labelStartupRepo.AllAsync(CancellationToken.None);
        app.Services.GetRequiredService<LabelStore>().Load(restored);
        app.Logger.LogInformation("Restored {Count} human label(s) from Postgres.", restored.Count);
    }
    catch (Exception ex)
    {
        // Same posture as every other optional table: an opt-in feature Postgres is not ready
        // for must not take ingest down. Labels then live in memory for this process.
        app.Logger.LogError(ex, "Could not restore human labels — labelling will run in memory only.");
    }
}

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
    "Privacy: GET /api/privacy | /api/audit?format=csv (admin)\n" +
    "Friction: GET /api/friction (aggregate; off unless CopilotScope:WorkflowFriction:Enabled)\n" +
    "Team views: GET /api/cohorts | /api/compare | /api/facets (add format=csv to export)\n" +
    "Digest: GET /api/digest | POST /api/digest/send (admin, needs CopilotScope:Alerts)\n" +
    "Import: POST /api/import (admin) — tools/CopilotScope.LogImporter, no OTel setup needed\n" +
    "Labelling: GET /api/labels/rubrics | POST /api/labels | GET /api/labels/export\n" +
    "Vendor usage: GET /api/vendor/metrics (archives GitHub's 28-day window indefinitely)\n" +
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
      Privacy mode     : {Privacy}
      Alerts           : {Alerts}
      Labelling        : {Labelling}
      Vendor archive   : {Vendor}
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
    forwarder.Enabled ? (forwardRaw ? "enabled" : "blocked by privacy mode") : "disabled",
    privacyOptions.Describe(),
    alertOptions.Describe(),
    app.Services.GetRequiredService<LabellingOptions>().Describe(),
    app.Services.GetRequiredService<VendorMetricsOptions>().Describe());

app.Run();
