using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// HTTP-layer tests for the collector's auth gate — the security surface the review
/// flagged (open read API, unauthenticated DELETE). Uses WebApplicationFactory over the
/// real Program pipeline. The four web projects each have a top-level Program in the
/// global namespace, so a collector type (SessionSummaryDto) is used as the assembly
/// marker to disambiguate which entry point to boot.
/// </summary>
public sealed class CollectorApiTests
{
    private static WebApplicationFactory<SessionSummaryDto> Factory(string? apiKey) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CopilotScope:Ingest:ApiKey"] = apiKey })));

    private static HttpClient Keyed(HttpClient c, string key)
    {
        c.DefaultRequestHeaders.Add("x-api-key", key);
        return c;
    }

    [Fact]
    public async Task Health_StaysOpen_EvenWithAKeyConfigured()
    {
        using var f = Factory("secret");
        using var c = f.CreateClient();
        var r = await c.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Sessions_RequireTheKey_WhenConfigured()
    {
        using var f = Factory("secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Keyed(f.CreateClient(), "secret").GetAsync("/api/sessions")).StatusCode);
    }

    [Fact]
    public async Task Sessions_RejectAWrongKey()
    {
        using var f = Factory("secret");
        var r = await Keyed(f.CreateClient(), "not-the-key").GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Delete_IsGated_WhenKeyConfigured()
    {
        // The finding that mattered most: DELETE used to be reachable with no key.
        using var f = Factory("secret");
        var r = await f.CreateClient().DeleteAsync("/api/sessions/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Overview_IsGated_WhenKeyConfigured()
    {
        using var f = Factory("secret");
        var r = await f.CreateClient().GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Ingest_IsGated_WhenKeyConfigured()
    {
        using var f = Factory("secret");
        var body = new ByteArrayContent([0x0a]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var r = await f.CreateClient().PostAsync("/v1/traces", body);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Seed_RejectsNonSeedPrefixedIds()
    {
        // Enforce the seed- prefix server-side so a key holder can't overwrite a real
        // session. A valid persisted session whose id is NOT seed-prefixed must be
        // refused (400) before it can be Put over real data.
        using var f = Factory(null); // open mode so the group filter passes; prefix check still runs
        var request = new SeedRequest(Reset: false, Sessions: [MinimalPersisted("real-session-not-seed")]);
        var r = await f.CreateClient().PostAsJsonAsync("/api/admin/seed", request);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Seed_AcceptsSeedPrefixedIds()
    {
        using var f = Factory(null);
        var request = new SeedRequest(Reset: false, Sessions: [MinimalPersisted("seed-unit-test-1")]);
        var r = await f.CreateClient().PostAsJsonAsync("/api/admin/seed", request);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task OpenMode_NoKey_AllowsReads()
    {
        using var f = Factory(null); // empty key = dev/open mode
        var r = await f.CreateClient().GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    /// <summary>A valid, empty PersistedSession with the given id — enough to round-trip
    /// through ToSession() so the seed handler's prefix check is what decides the outcome.</summary>
    private static PersistedSession MinimalPersisted(string id) => new(
        id, null, null, null, null,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        new List<double>(), new List<double>(), new List<double>(),
        new Dictionary<string, int>(),
        new List<PersistedToolStat>(),
        new Dictionary<string, int>(),
        new List<SessionEvent>(),
        new List<TranscriptEntry>(),
        new List<PersistedTurn>());
}
