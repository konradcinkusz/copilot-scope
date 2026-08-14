using System.Text.Json;
using CopilotScope.JudgeAgent.Agents;
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

app.MapPost("/api/sessions/{id}/judge", async (string id, HttpRequest request,
    ICollectorClient collector, SessionJudgeContextBuilder contextBuilder,
    JudgePromptBuilder promptBuilder, IJudgeChatClient chatClient, CancellationToken ct) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();

    var detail = await collector.GetSessionDetailAsync(id, ct);
    if (detail is null) return Results.NotFound();

    var context = contextBuilder.Build(detail);
    var systemPrompt = promptBuilder.Build(context);
    var sessionPayloadJson = JsonSerializer.Serialize(context, JudgeJson.Options);

    var rawResponse = await chatClient.JudgeAsync(systemPrompt, sessionPayloadJson, ct);
    var results = JudgeResponseParser.Parse(rawResponse);

    return Results.Ok(new { results });
});

app.MapGet("/", () => Results.Text(
    "CopilotScope JudgeAgent — opt-in, cloud-only session quality judge (G-Eval, SPUR, RAGAS,\n" +
    "deep frustration classification, task-completion detection).\n" +
    "See docs/JUDGE_AGENT.md before enabling this for a team.\n" +
    "API: GET /api/health | POST /api/sessions/{id}/judge\n"));

app.Run();
