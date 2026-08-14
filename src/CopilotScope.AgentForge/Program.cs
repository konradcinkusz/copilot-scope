using CopilotScope.AgentForge.Agents;
using CopilotScope.AgentForge.Clients;
using CopilotScope.AgentForge.Config;
using CopilotScope.AgentForge.Domain;
using CopilotScope.AgentForge.Profiling;
using CopilotScope.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Shared kernel: OTel, health (/health + /alive), discovery, resilience on the
// collector + Azure AI HttpClients (P2/P15) — a Foundry blip is now retried.
builder.AddServiceDefaults();

var cohortsOptions = new CohortsOptions();
builder.Configuration.GetSection("CopilotScope:AgentForge:Cohorts").Bind(cohortsOptions.Cohorts);
builder.Services.AddSingleton(cohortsOptions);

var azureAiOptions = new AzureAiOptions();
builder.Configuration.GetSection("CopilotScope:AgentForge:AzureAI").Bind(azureAiOptions);
builder.Services.AddSingleton(azureAiOptions);

var collectorBaseUrl = builder.Configuration["CopilotScope:AgentForge:CollectorBaseUrl"] ?? "http://collector:4318";
builder.Services.AddHttpClient<ICollectorClient, CollectorClient>(c => c.BaseAddress = new Uri(collectorBaseUrl));

builder.Services.AddTransient<PersonaProfileBuilder>();
builder.Services.AddSingleton<PersonaPromptBuilder>();
builder.Services.AddSingleton<IPersonaChatClient, AzureFoundryPersonaChatClient>();
builder.Services.AddSingleton<ProvisionedAgentCache>();

var app = builder.Build();

app.MapDefaultEndpoints(); // /health + /alive

var ingestApiKey = app.Configuration["CopilotScope:AgentForge:Ingest:ApiKey"]; // null/empty → open (dev mode)

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
    azureAiConfigured = !string.IsNullOrEmpty(azureAiOptions.Endpoint),
    cohortsLoaded = cohortsOptions.Cohorts.Count
}));

var api = app.MapGroup("/api/personas");

api.MapGet("/", (HttpRequest request) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();
    return Results.Ok(cohortsOptions.Cohorts.Select(c => new
    {
        personaId = c.PersonaId,
        displayLabel = c.DisplayLabel,
        sessionCount = c.SessionIds.Count
    }));
});

api.MapGet("/{personaId}/profile", async (string personaId, HttpRequest request,
    PersonaProfileBuilder profileBuilder, CancellationToken ct) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();
    var cohort = cohortsOptions.Cohorts.FirstOrDefault(c => c.PersonaId == personaId);
    if (cohort is null) return Results.NotFound();

    var profile = await profileBuilder.BuildAsync(cohort, ct);
    return Results.Ok(profile);
});

api.MapPost("/{personaId}/provision", async (string personaId, HttpRequest request,
    PersonaProfileBuilder profileBuilder, PersonaPromptBuilder promptBuilder,
    ProvisionedAgentCache cache, CancellationToken ct) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();
    var cohort = cohortsOptions.Cohorts.FirstOrDefault(c => c.PersonaId == personaId);
    if (cohort is null) return Results.NotFound();

    var profile = await profileBuilder.BuildAsync(cohort, ct);
    var systemPrompt = promptBuilder.Build(profile);
    var provisionedAt = DateTimeOffset.UtcNow;
    cache.Set(personaId, new ProvisionedAgent(profile, systemPrompt, provisionedAt));

    return Results.Ok(new
    {
        personaId,
        exemplarCount = profile.Exemplars.Count,
        provisionedAt
    });
});

api.MapPost("/{personaId}/chat", async (string personaId, ChatRequest chatRequest, HttpRequest request,
    IPersonaChatClient chatClient, ProvisionedAgentCache cache, CancellationToken ct) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();
    if (!cache.TryGet(personaId, out var provisioned))
        return Results.Conflict(new { error = $"Persona '{personaId}' is not provisioned. Call provision first." });

    var reply = await chatClient.ChatAsync(provisioned.SystemPrompt, chatRequest.Message, ct);
    return Results.Ok(new
    {
        personaId,
        simulated = true, // hard-coded — never sourced from config, see docs/AGENTFORGE.md
        reply
    });
});

api.MapDelete("/{personaId}", (string personaId, HttpRequest request, ProvisionedAgentCache cache) =>
{
    if (!IsAuthorized(request)) return Results.Unauthorized();
    return cache.Remove(personaId) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/", () => Results.Text(
    "CopilotScope AgentForge — opt-in persona agents grounded on consented sessions.\n" +
    "See docs/AGENTFORGE.md for consent/opt-in requirements before enabling this for a team.\n" +
    "API: GET /api/health | GET /api/personas | GET /api/personas/{id}/profile\n" +
    "     POST /api/personas/{id}/provision | POST /api/personas/{id}/chat | DELETE /api/personas/{id}\n"));

app.Run();

public sealed record ChatRequest(string Message);
