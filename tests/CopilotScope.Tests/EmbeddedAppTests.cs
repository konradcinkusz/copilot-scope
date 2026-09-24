using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector;
using CopilotScope.Collector.Quality;
using CopilotScope.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The collector and the dashboard built by another process, the way the native
/// <c>copilotscope</c> host (ADR-004) runs them: its own content root, which holds neither
/// application's <c>appsettings.json</c>, and its own configuration added before the build.
/// </summary>
public sealed class EmbeddedAppTests : IDisposable
{
    private readonly string _contentRoot = TempDirectory.Create();

    public void Dispose() => TempDirectory.Delete(_contentRoot);

    private WebApplicationOptions Options(string applicationName) => new()
    {
        ApplicationName = applicationName,
        ContentRootPath = _contentRoot,
        EnvironmentName = "Production",
        Args = []
    };

    [Fact]
    public async Task TheCollectorRunsInsideAnotherProcessWithItsOwnDefaults()
    {
        await using var app = await CollectorApp.BuildAsync(Options("CopilotScope.Collector"),
            b => b.WebHost.UseTestServer());
        await app.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync("/api/health")).StatusCode);
        // The pricing table lives in appsettings.json, which this content root does not have.
        Assert.Contains("claude-sonnet-5", app.Services.GetRequiredService<PricingOptions>().Models.Keys);
        await app.StopAsync();
    }

    [Fact]
    public async Task AnAppSettingsFileBesideTheHostWinsOutright()
    {
        // Not merged over: an operator who wrote their own file removed what they removed.
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "appsettings.json"),
            """{ "CopilotScope": { "Pricing": { "house-model": { "Input": 1, "Output": 2, "CacheRead": 0 } } } }""");

        await using var app = await CollectorApp.BuildAsync(Options("CopilotScope.Collector"),
            b => b.WebHost.UseTestServer());

        var models = app.Services.GetRequiredService<PricingOptions>().Models.Keys;
        Assert.Contains("house-model", models);
        Assert.DoesNotContain("claude-sonnet-5", models);
    }

    [Fact]
    public async Task TheHostsConfigurationIsInPlaceBeforeTheBuildDecidesAnything()
    {
        // The storage mode decides what gets registered, so it is read early; a host's
        // configuration has to be there by then, not arrive after the build.
        var data = TempDirectory.Create();
        try
        {
            await using var app = await CollectorApp.BuildAsync(Options("CopilotScope.Collector"), b =>
            {
                b.WebHost.UseTestServer();
                b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CopilotScope:Storage:Mode"] = "files",
                    ["CopilotScope:Storage:Path"] = data
                });
            });
            await app.StartAsync();

            var health = await app.GetTestClient().GetFromJsonAsync<JsonElement>("/api/health");
            Assert.Equal("files", health.GetProperty("storage").GetString());
            await app.StopAsync();
        }
        finally { TempDirectory.Delete(data); }
    }

    [Fact]
    public async Task TheDashboardRunsInsideAnotherProcess()
    {
        await using var app = DashboardApp.Build(Options("CopilotScope.Dashboard"),
            b => b.WebHost.UseTestServer());
        await app.StartAsync();

        // The docs page renders without a collector, so it proves the pages are found and served
        // from a host that is not the dashboard's own entry point.
        var response = await app.GetTestClient().GetAsync("/docs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Supported sources", await response.Content.ReadAsStringAsync());
        await app.StopAsync();
    }
}
