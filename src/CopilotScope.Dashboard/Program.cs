using CopilotScope.Dashboard.Components;
using CopilotScope.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Aspire's WithReference(collector) injects services__collector__http__0; the config
// system maps "__" to ":". Fallbacks keep the app runnable without the AppHost.
var collectorBase = builder.Configuration["services:collector:http:0"];
if (string.IsNullOrWhiteSpace(collectorBase))
    collectorBase = builder.Configuration["Collector:BaseUrl"];
if (string.IsNullOrWhiteSpace(collectorBase))
    collectorBase = "http://localhost:4318";

// When the collector is deployed with an ingest key, its /api group is gated, so the
// dashboard must present the same key. In local/dev mode the key is empty and the
// header is simply omitted.
var ingestApiKey = builder.Configuration["CopilotScope:Ingest:ApiKey"];

builder.Services.AddHttpClient<CollectorClient>(client =>
{
    client.BaseAddress = new Uri(collectorBase);
    client.Timeout = TimeSpan.FromSeconds(5);
    if (!string.IsNullOrEmpty(ingestApiKey))
        client.DefaultRequestHeaders.Add("x-api-key", ingestApiKey);
});

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Logger.LogInformation("CopilotScope dashboard started — collector at {Collector}", collectorBase);

app.Run();
