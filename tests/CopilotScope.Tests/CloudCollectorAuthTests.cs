using System.Net;
using CopilotScope.Collector.Api;
using CopilotScope.ServiceDefaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The cloud tier talking to a secured Collector.
///
/// infra/main.bicep makes the ingest key a REQUIRED parameter, so every Azure deployment runs
/// a Collector whose /api group is gated — and both cloud services sent no key at all, so
/// every session read 401'd at request time. The judge and the persona cohorts therefore only
/// ever worked against an open dev-mode Collector, the opposite of the project's own
/// deployment guidance. These tests run the real clients against the real Collector pipeline.
/// </summary>
public sealed class CloudCollectorAuthTests
{
    private const string CollectorKey = "collector-read-key";

    private static WebApplicationFactory<SessionSummaryDto> SecuredCollector() =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CopilotScope:Ingest:ApiKey"] = CollectorKey })));

    /// <summary>An HttpClient wired the way each service's Program.cs wires its own.</summary>
    private static HttpClient Client(WebApplicationFactory<SessionSummaryDto> f, string? key)
    {
        var c = f.CreateClient();
        if (!string.IsNullOrEmpty(key)) c.DefaultRequestHeaders.Add(ApiKeyAuth.HeaderName, key);
        return c;
    }

    [Fact]
    public async Task JudgeAgentReachesASecuredCollectorWhenTheKeyIsConfigured()
    {
        using var collector = SecuredCollector();
        var client = new JudgeAgent.Clients.CollectorClient(Client(collector, CollectorKey));

        // A NotFound (returned as null) means the request passed the auth gate and the
        // Collector genuinely has no such session — which is the success condition here.
        Assert.Null(await client.GetSessionDetailAsync("no-such-session", CancellationToken.None));
    }

    [Fact]
    public async Task JudgeAgentIsRejectedWithoutTheKey()
    {
        // The regression: an unconfigured cloud service gets 401, not null, and the judge
        // fails at request time with no hint about why.
        using var collector = SecuredCollector();
        var client = new JudgeAgent.Clients.CollectorClient(Client(collector, key: null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetSessionDetailAsync("no-such-session", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task AgentForgeReachesASecuredCollectorWhenTheKeyIsConfigured()
    {
        using var collector = SecuredCollector();
        var client = new AgentForge.Clients.CollectorClient(Client(collector, CollectorKey));

        Assert.Null(await client.GetSessionDetailAsync("no-such-session", CancellationToken.None));
    }

    [Fact]
    public async Task AgentForgeIsRejectedWithoutTheKey()
    {
        using var collector = SecuredCollector();
        var client = new AgentForge.Clients.CollectorClient(Client(collector, key: null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetSessionDetailAsync("no-such-session", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task AReadScopedKeyIsEnoughForTheCloudServices()
    {
        // These services only ever read one named session, so they should be given a
        // Read-scoped key rather than an admin one — verify Read actually suffices.
        using var collector = new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CopilotScope:Keys:Read:0"] = "read-only" })));

        var client = new JudgeAgent.Clients.CollectorClient(Client(collector, "read-only"));
        Assert.Null(await client.GetSessionDetailAsync("no-such-session", CancellationToken.None));
    }
}
