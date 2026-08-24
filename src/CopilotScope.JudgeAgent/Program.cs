using System.Text.Json;
using CopilotScope.Collector.Quality;
using CopilotScope.JudgeAgent.Agents;
using CopilotScope.JudgeAgent.Calibration;
using CopilotScope.JudgeAgent.Clients;
using CopilotScope.JudgeAgent.Config;
using CopilotScope.JudgeAgent.Judging;
using CopilotScope.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Shared kernel: OTel, health (/health + /alive), discovery, resilience on the
// collector + Azure AI HttpClients (P2/P15).
builder.AddServiceDefaults();

var azureAiOptions = new AzureAiOptions();
builder.Configuration.GetSection("CopilotScope:JudgeAgent:AzureAI").Bind(azureAiOptions);
builder.Services.AddSingleton(azureAiOptions);

var collectorBaseUrl = builder.Configuration["CopilotScope:JudgeAgent:CollectorBaseUrl"] ?? "http://collector:4318";
builder.Services.AddHttpClient<ICollectorClient, CollectorClient>(c => c.BaseAddress = new Uri(collectorBaseUrl));

var calibrationOptions = new CalibrationOptions();
builder.Configuration.GetSection("CopilotScope:JudgeAgent:Calibration").Bind(calibrationOptions);
builder.Services.AddSingleton(calibrationOptions);
builder.Services.AddSingleton<CalibrationEngine>();

builder.Services.AddSingleton<SessionJudgeContextBuilder>();
builder.Services.AddSingleton<JudgePromptBuilder>();
builder.Services.AddSingleton<IJudgeChatClient, AzureFoundryJudgeChatClient>();

var app = builder.Build();

app.MapDefaultEndpoints(); // /health + /alive

var ingestApiKey = app.Configuration["CopilotScope:JudgeAgent:Ingest:ApiKey"]; // null/empty → open (dev mode)

bool IsAuthorized(HttpRequest request)
{
    if (string.IsNullOrEmpty(ingestApiKey)) return true;
    var provided = request.Headers["x-api-key"].FirstOrDefault()
                ?? request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
    return provided == ingestApiKey;
}

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    azureAiConfigured = !string.IsNullOrEmpty(azureAiOptions.Endpoint)
}));

// One session through the judge: fetch -> build context -> render rubric -> call model ->
// parse. Extracted so the single-session endpoint and the calibration batch below cannot
// drift apart; a calibration run has to grade sessions exactly the way production does or the
// κ it produces describes a judge nobody is running.
async Task<List<InsightReport>?> JudgeSessionAsync(
    string sessionId, ICollectorClient collector, SessionJudgeContextBuilder contextBuilder,
    JudgePromptBuilder promptBuilder, IJudgeChatClient chatClient, CancellationToken ct)
{
    var detail = await collector.GetSessionDetailAsync(sessionId, ct);
    if (detail is null) return null;

    var context = contextBuilder.Build(detail);
    var systemPrompt = promptBuilder.Build(context);
    var sessionPayloadJson = JsonSerializer.Serialize(context, JudgeJson.Options);

    var rawResponse = await chatClient.JudgeAsync(systemPrompt, sessionPayloadJson, ct);
    return JudgeResponseParser.Parse(rawResponse);
}

app.MapPost("/api/sessions/{id}/judge", async (string id, HttpRequest request,
    ICollectorClient collector, SessionJudgeContextBuilder contextBuilder,
    JudgePromptBuilder promptBuilder, IJudgeChatClient chatClient, CancellationToken ct) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();

    var results = await JudgeSessionAsync(id, collector, contextBuilder, promptBuilder, chatClient, ct);
    return results is null ? Results.NotFound() : Results.Ok(new { results });
});

// ------------------------------------------------------------------- calibration
// "Calibrated against humans, or discarded" (AI-EVALS.md §5). Two endpoints because the two
// halves of that sentence have very different costs.

// Pure arithmetic: human labels in, agreement report out, no model access. This is the one CI
// runs — it is deterministic, free, and reviewable in a pull request, which is what lets a
// calibration act as a baseline rather than an anecdote.
app.MapPost("/api/calibration/report", (CalibrationDataset? dataset, HttpRequest request,
    CalibrationEngine engine, JudgePromptBuilder promptBuilder) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();
    if (dataset is null) return Results.BadRequest(new { error = "Supply a calibration dataset." });

    try
    {
        return Results.Ok(engine.Evaluate(dataset with
        {
            JudgePromptVersion = dataset.JudgePromptVersion ?? promptBuilder.TemplateFingerprint
        }));
    }
    catch (ArgumentException ex)
    {
        // A malformed dataset is the caller's bug, and a silently-repaired one would produce a
        // κ that describes data nobody supplied.
        return Results.BadRequest(new { error = ex.Message });
    }
});

// The expensive half: grade every labelled session with the live judge, then compute the same
// report. One model call per session, so this is a deliberate, occasional run — not something
// to wire into a pipeline.
app.MapPost("/api/calibration/run", async (CalibrationRunRequest? run, HttpRequest request,
    ICollectorClient collector, SessionJudgeContextBuilder contextBuilder,
    JudgePromptBuilder promptBuilder, IJudgeChatClient chatClient, CalibrationEngine engine,
    CancellationToken ct) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();

    var labels = run?.Labels ?? [];
    if (labels.Count == 0)
        return Results.BadRequest(new { error = "Supply the human labels to calibrate against." });

    var sessionIds = labels.Select(l => l.SessionId)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();

    if (sessionIds.Count > CalibrationRunRequest.MaxSessions)
        return Results.BadRequest(new
        {
            error = $"{sessionIds.Count} sessions exceeds the {CalibrationRunRequest.MaxSessions}-session " +
                    "cap for one run; each session is a metered model call. Split the dataset."
        });

    var scores = new List<JudgeScore>();
    var failures = new List<object>();

    foreach (var sessionId in sessionIds)
    {
        // Sequential on purpose: fanning out across a judge deployment is the fastest way to
        // trip rate limits and turn a calibration run into a partial one.
        try
        {
            var results = await JudgeSessionAsync(sessionId, collector, contextBuilder, promptBuilder, chatClient, ct);
            if (results is null)
            {
                failures.Add(new { sessionId, reason = "not found in the collector" });
                continue;
            }

            // "no-data" rubrics carry no score and must not be invented — they drop out of the
            // pairing and are reported as dropped, per rubric, in the report itself.
            scores.AddRange(results
                .Where(r => r is { Status: "ok", Score: not null })
                .Select(r => new JudgeScore(sessionId, r.Algorithm, r.Score!.Value)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One bad session must not throw away the model calls already spent on the rest.
            app.Logger.LogWarning(ex, "Calibration run: judging session {SessionId} failed.", sessionId);
            failures.Add(new { sessionId, reason = ex.Message });
        }
    }

    try
    {
        var report = engine.Evaluate(new CalibrationDataset(
            labels, scores, run!.DatasetVersion, azureAiOptions.DeploymentName, promptBuilder.TemplateFingerprint));

        return Results.Ok(new { report, judged = sessionIds.Count - failures.Count, failures });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message, failures });
    }
});

app.MapGet("/", () => Results.Text(
    "CopilotScope JudgeAgent — opt-in, cloud-only session quality judge (G-Eval, SPUR, RAGAS,\n" +
    "deep frustration classification, task-completion detection).\n" +
    "See docs/JUDGE_AGENT.md before enabling this for a team.\n" +
    "API: GET /api/health | POST /api/sessions/{id}/judge\n" +
    "Calibration: POST /api/calibration/report (offline, free) | POST /api/calibration/run (live, metered)\n" +
    "See docs/CALIBRATION.md before trusting a judge score.\n"));

app.Run();
