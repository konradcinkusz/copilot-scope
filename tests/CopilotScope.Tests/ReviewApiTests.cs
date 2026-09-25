using System.Net;
using System.Text.Json;
using CopilotScope.Collector.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The review endpoints over the real pipeline: who may have which tier, what privacy mode refuses,
/// and that the switch is a switch.
/// </summary>
public sealed class ReviewApiTests
{
    private static WebApplicationFactory<SessionSummaryDto> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    private static HttpRequestMessage Get(string path, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null) request.Headers.Add("x-api-key", key);
        return request;
    }

    [Fact]
    public async Task TheAggregateTierIsServedOnAnEmptyBase()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/review/pack");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("aggregate", root.GetProperty("scope").GetProperty("tier").GetString());
        Assert.Equal(0, root.GetProperty("scope").GetProperty("sessions").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sessions").ValueKind);
        Assert.Equal(1, root.GetProperty("scope").GetProperty("packVersion").GetInt32());
    }

    [Fact]
    public async Task TheMarkdownFormIsServedAsMarkdown()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/review/pack?format=markdown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("# CopilotScope review pack", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessSaysNotReadyOnAnEmptyBase()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/review/readiness"));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("ready").GetBoolean());
        Assert.Equal(0, root.GetProperty("eligible").GetInt32());
        Assert.Equal(25, root.GetProperty("minSessions").GetInt32());
        Assert.Equal(30, root.GetProperty("windowDays").GetInt32());
    }

    [Fact]
    public async Task TheSessionsTierNeedsAdminScope()
    {
        using var factory = Factory(
            ("CopilotScope:Keys:Read:0", "read-key"),
            ("CopilotScope:Keys:Admin:0", "admin-key"));
        using var client = factory.CreateClient();

        // The aggregate tier is a read like any other view. The sessions tier is an artefact
        // built to be handed to something else, carrying session ids: the digest.send precedent.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/review/pack", "read-key"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/review/pack?tier=sessions", "read-key"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/review/pack?tier=sessions", "admin-key"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/review/pack"))).StatusCode);
    }

    [Fact]
    public async Task ThePackIsWithheldBelowTheFloor()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "5"));
        using var client = factory.CreateClient();

        var pack = await client.GetAsync("/api/review/pack");
        Assert.Equal(HttpStatusCode.Forbidden, pack.StatusCode);
        Assert.Contains("suppressed", await pack.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/review/readiness")).StatusCode);
    }

    [Fact]
    public async Task TheSessionsTierIsRefusedUnderPrivacyModeBeforeAnythingIsRead()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "1"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/review/pack?tier=sessions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("sessions tier", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewCanBeSwitchedOffAndTheReportsSaySo()
    {
        using var factory = Factory(("CopilotScope:Review:Enabled", "false"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/review/pack")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/review/readiness")).StatusCode);

        using var privacy = JsonDocument.Parse(await client.GetStringAsync("/api/privacy"));
        Assert.False(privacy.RootElement.GetProperty("review").GetProperty("enabled").GetBoolean());
        Assert.Equal("off", privacy.RootElement.GetProperty("review").GetProperty("sessionTier").GetString());

        using var health = JsonDocument.Parse(await client.GetStringAsync("/api/health"));
        Assert.False(health.RootElement.GetProperty("review").GetBoolean());
    }

    [Fact]
    public async Task ThePrivacyReportDescribesWhoMayHaveTheSessionsTier()
    {
        using (var open = Factory())
        using (var client = open.CreateClient())
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/privacy"));
            Assert.Equal("admin scope", doc.RootElement.GetProperty("review").GetProperty("sessionTier").GetString());
        }

        using (var guarded = Factory(("CopilotScope:Privacy:Enabled", "true"), ("CopilotScope:Privacy:Salt", "test-salt")))
        using (var client = guarded.CreateClient())
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/privacy"));
            Assert.Equal("refused (privacy mode)", doc.RootElement.GetProperty("review").GetProperty("sessionTier").GetString());
        }
    }

    [Fact]
    public async Task AnUnknownTierIsRejected()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/review/pack?tier=people")).StatusCode);
    }
}
