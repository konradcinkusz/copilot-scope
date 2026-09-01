using System.Net;
using System.Net.Http.Json;
using CopilotScope.Collector.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Scoped API keys. One shared secret authorized ingest, transcript reads, destructive
/// deletes and seeding alike — so the credential handed to every developer's editor was
/// also the one that could wipe the team's history. These tests pin the separation, and
/// pin that an existing single-key deployment is unaffected.
/// </summary>
public sealed class ApiScopeTests
{
    private const string Ingest = "ingest-key";
    private const string Read = "read-key";
    private const string Admin = "admin-key";

    private static WebApplicationFactory<SessionSummaryDto> ScopedFactory() =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CopilotScope:Keys:Ingest:0"] = Ingest,
                    ["CopilotScope:Keys:Read:0"] = Read,
                    ["CopilotScope:Keys:Admin:0"] = Admin
                })));

    private static WebApplicationFactory<SessionSummaryDto> LegacyFactory(string key) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CopilotScope:Ingest:ApiKey"] = key })));

    private static HttpClient Keyed(WebApplicationFactory<SessionSummaryDto> f, string key)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("x-api-key", key);
        return c;
    }

    /// <summary>An empty OTLP protobuf body is a valid, if uninteresting, export request —
    /// enough to exercise the ingest gate without building a payload.</summary>
    private static HttpContent EmptyOtlp() =>
        new ByteArrayContent([]) { Headers = { ContentType = new("application/x-protobuf") } };

    // ---------------------------------------------------------------- ingest scope

    [Fact]
    public async Task IngestKey_CanWriteTelemetry()
    {
        using var f = ScopedFactory();
        using var c = Keyed(f, Ingest);
        var r = await c.PostAsync("/v1/traces", EmptyOtlp());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task IngestKey_CannotReadSessions()
    {
        // The whole point of the split: the key every editor holds must not reach the
        // captured transcripts.
        using var f = ScopedFactory();
        using var c = Keyed(f, Ingest);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/sessions")).StatusCode);
    }

    [Fact]
    public async Task IngestKey_CannotDelete()
    {
        using var f = ScopedFactory();
        using var c = Keyed(f, Ingest);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.DeleteAsync("/api/sessions/anything")).StatusCode);
    }

    // ---------------------------------------------------------------- read scope

    [Fact]
    public async Task ReadKey_CanQuery()
    {
        using var f = ScopedFactory();
        using var c = Keyed(f, Read);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/overview")).StatusCode);
    }

    [Fact]
    public async Task ReadKey_CannotDelete()
    {
        using var f = ScopedFactory();
        using var c = Keyed(f, Read);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.DeleteAsync("/api/sessions/anything")).StatusCode);
    }

    [Fact]
    public async Task ReadKey_CannotSeed()
    {
        using var f = ScopedFactory();
        using var c = Keyed(f, Read);
        var r = await c.PostAsJsonAsync("/api/admin/seed", new { reset = false, sessions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task ReadKey_CannotWriteTelemetry()
    {
        using var f = ScopedFactory();
        using var c = Keyed(f, Read);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/v1/traces", EmptyOtlp())).StatusCode);
    }

    // ---------------------------------------------------------------- admin scope

    [Fact]
    public async Task AdminKey_CanReadAndDelete()
    {
        // Admin implies read: an operator should not need two keys to look at what they
        // are about to delete.
        using var f = ScopedFactory();
        using var c = Keyed(f, Admin);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/sessions")).StatusCode);
        // NotFound rather than Unauthorized is the pass condition: the gate let it through.
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/sessions/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task AdminKey_DoesNotGrantIngest()
    {
        // Ingest is orthogonal, not a lesser scope: an admin credential leaking into an
        // emitter config should not silently become a valid telemetry writer.
        using var f = ScopedFactory();
        using var c = Keyed(f, Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/v1/traces", EmptyOtlp())).StatusCode);
    }

    [Fact]
    public async Task UnknownKey_IsRejectedEverywhere()
    {
        using var f = ScopedFactory();
        using var c = Keyed(f, "not-a-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/v1/traces", EmptyOtlp())).StatusCode);
    }

    // ---------------------------------------------------------------- compatibility

    [Fact]
    public async Task LegacySingleKey_StillGrantsEveryScope()
    {
        // An existing deployment sets only CopilotScope:Ingest:ApiKey. Narrowing that on
        // upgrade would lock a running collector out of itself.
        using var f = LegacyFactory("one-key");
        using var c = Keyed(f, "one-key");

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/v1/traces", EmptyOtlp())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/sessions/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task NoKeysConfigured_IsStillOpenDevMode()
    {
        using var f = LegacyFactory("");
        using var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/sessions")).StatusCode);
    }

    [Fact]
    public async Task AWhitespaceOnlyKeyStillEnforcesAuth()
    {
        // Regression: dropping whitespace-only keys left the registry empty, which reads as
        // dev/open mode and disabled authentication on ingest, reads, DELETE and /metrics
        // alike. A strange key is still a key — fail closed.
        using var f = LegacyFactory("   ");
        using var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sessions")).StatusCode);

        using var keyed = Keyed(f, "   ");
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/api/sessions")).StatusCode);
    }
}
