using System.Net;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Quality;
using CopilotScope.Collector.Vendor;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Archiving GitHub's Copilot usage window before it expires.
///
/// <para>GitHub serves 28 days and nothing older. The value of this feature is entirely in what
/// it keeps, so the properties worth testing are the ones that decide whether a day survives:
/// the parser must not drop a day it half-understands, the raw document must be kept verbatim
/// so a field added next month is still archived, and storage must be idempotent because the
/// same 28 days come back on every poll.</para>
/// </summary>
public sealed class VendorMetricsTests
{
    /// <summary>The documented GA shape, including a surface this parser reads and one it does
    /// not, so the "keep everything" property is actually exercised.</summary>
    private const string SampleResponse = """
        [
          {
            "date": "2026-08-30",
            "total_active_users": 42,
            "total_engaged_users": 31,
            "copilot_ide_code_completions": { "total_engaged_users": 28, "languages": [{ "name": "csharp", "total_engaged_users": 20 }] },
            "copilot_ide_chat": { "total_engaged_users": 17 },
            "copilot_dotcom_chat": { "total_engaged_users": 5 },
            "copilot_dotcom_pull_requests": { "total_engaged_users": 9 },
            "copilot_agents_some_future_surface": { "total_engaged_users": 3 }
          },
          {
            "date": "2026-08-29",
            "total_active_users": 40,
            "total_engaged_users": 29,
            "copilot_ide_chat": { "total_engaged_users": 15 }
          }
        ]
        """;

    // -------------------------------------------------------------------- parsing

    [Fact]
    public void TheDocumentedShapeIsParsedIntoDays()
    {
        var days = GitHubCopilotMetricsSource.Parse(SampleResponse, "org:acme");

        Assert.Equal(2, days.Count);
        var first = days[0];
        Assert.Equal(new DateOnly(2026, 8, 30), first.Day);
        Assert.Equal(42, first.TotalActiveUsers);
        Assert.Equal(31, first.TotalEngagedUsers);
        Assert.Equal(28, first.CompletionsEngagedUsers);
        Assert.Equal(17, first.ChatEngagedUsers);
        Assert.Equal(5, first.DotcomChatEngagedUsers);
        Assert.Equal(9, first.PullRequestEngagedUsers);
        Assert.Equal("org:acme", first.Scope);
    }

    [Fact]
    public void TheRawDocumentIsKeptIncludingFieldsThisParserDoesNotUnderstand()
    {
        // The whole point of an archiver: the vendor deletes the original in 28 days, so a
        // parser that only kept what it understood today would silently throw away history
        // that cannot be re-fetched when a later version learns to read it.
        var day = GitHubCopilotMetricsSource.Parse(SampleResponse, "org:acme")[0];

        Assert.Contains("copilot_agents_some_future_surface", day.RawJson, StringComparison.Ordinal);
        Assert.Contains("csharp", day.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSurfaceIsZeroNotAFailure()
    {
        // The second day in the sample has no completions block at all — an org where nobody
        // used completions that day. Failing over it would cost the whole window.
        var day = GitHubCopilotMetricsSource.Parse(SampleResponse, "org:acme")[1];

        Assert.Equal(0, day.CompletionsEngagedUsers);
        Assert.Equal(15, day.ChatEngagedUsers);
    }

    [Fact]
    public void ADayWithNoUsableDateIsSkippedRatherThanGuessed()
    {
        // A day cannot be stored idempotently without its date, and inventing one would corrupt
        // the archive this exists to protect.
        var days = GitHubCopilotMetricsSource.Parse(
            """[{"total_active_users":5},{"date":"2026-08-30","total_active_users":7}]""", "org:acme");

        Assert.Equal(new DateOnly(2026, 8, 30), Assert.Single(days).Day);
    }

    [Fact]
    public void AnErrorBodyOrGarbageYieldsNoDaysRatherThanThrowing()
    {
        // GitHub returns an object, not an array, for 403/404 — and a failing poll must not take
        // the archiver's loop down with it.
        Assert.Empty(GitHubCopilotMetricsSource.Parse("""{"message":"Bad credentials"}""", "org:acme"));
        Assert.Empty(GitHubCopilotMetricsSource.Parse("not json", "org:acme"));
        Assert.Empty(GitHubCopilotMetricsSource.Parse("[]", "org:acme"));
    }

    // --------------------------------------------------------------------- config

    [Fact]
    public void ArchivingNeedsBothAScopeAndAToken()
    {
        Assert.False(new VendorMetricsOptions { Enabled = true }.Active);
        Assert.False(new VendorMetricsOptions { Enabled = true, Organization = "acme" }.Active);
        Assert.False(new VendorMetricsOptions { Enabled = false, Organization = "acme", Token = "t" }.Active);
        Assert.True(new VendorMetricsOptions { Enabled = true, Organization = "acme", Token = "t" }.Active);
    }

    [Fact]
    public void TheScopeNamesWhatIsBeingArchived()
    {
        Assert.Equal("org:acme", new VendorMetricsOptions { Organization = " acme " }.Scope);
        // Enterprise wins: asking for both is a configuration mistake, and the wider scope is
        // the one whose data would be lost if the narrower were polled instead.
        Assert.Equal("enterprise:acme-inc",
            new VendorMetricsOptions { Organization = "acme", Enterprise = "acme-inc" }.Scope);
    }

    [Fact]
    public void TheDescriptionSaysWhyItIsNotRunning()
    {
        // "off" and "misconfigured" look identical in a log line unless they are spelled apart.
        Assert.Contains("off", new VendorMetricsOptions().Describe(), StringComparison.Ordinal);
        Assert.Contains("missing", new VendorMetricsOptions { Enabled = true }.Describe(), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- prometheus

    [Fact]
    public void TheExporterIsSilentWhenNothingHasBeenArchived()
    {
        var text = new PrometheusExporter(new Collector.Domain.SessionStore(), new QualityEngine(),
            new PricingOptions(), new PrometheusOptions()).Render();

        Assert.DoesNotContain("copilotscope_vendor_", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExporterPublishesHowMuchHistoryTheVendorWouldHaveDeleted()
    {
        // The number that says what the archive bought: days held past the vendor's own horizon.
        var snapshot = new VendorMetricsSnapshot
        {
            Enabled = true,
            Scope = "org:acme",
            DaysArchived = 100,
            Latest = new VendorMetricsDay("github", "org:acme", new DateOnly(2026, 8, 30),
                42, 31, 28, 17, 5, 9, "{}"),
        };

        var text = new PrometheusExporter(new Collector.Domain.SessionStore(), new QualityEngine(),
            new PricingOptions(), new PrometheusOptions(), null, snapshot).Render();

        Assert.Contains("copilotscope_vendor_days_archived", text, StringComparison.Ordinal);
        Assert.Contains("copilotscope_vendor_days_beyond_window", text, StringComparison.Ordinal);
        Assert.Contains("72", text, StringComparison.Ordinal);          // 100 archived − 28 served
        Assert.Contains("surface=\"ide_chat\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExporterNeverReportsNegativeHistoryBeyondTheWindow()
    {
        var snapshot = new VendorMetricsSnapshot { Enabled = true, Scope = "org:acme", DaysArchived = 3 };

        var text = new PrometheusExporter(new Collector.Domain.SessionStore(), new QualityEngine(),
            new PricingOptions(), new PrometheusOptions(), null, snapshot).Render();

        Assert.DoesNotContain("-25", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- the API

    [Fact]
    public async Task TheEndpointRefusesUntilArchivingIsConfiguredAndSaysWhatToSet()
    {
        using var factory = new WebApplicationFactory<SessionSummaryDto>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/vendor/metrics");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("CopilotScope:VendorMetrics",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivingWithoutPostgresIsRefusedRatherThanPollingIntoNothing()
    {
        // A poll with nowhere to store spends an org's rate limit to keep nothing, which is
        // worse than not running at all.
        using var factory = new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotScope:VendorMetrics:Enabled"] = "true",
                ["CopilotScope:VendorMetrics:Organization"] = "acme",
                ["CopilotScope:VendorMetrics:Token"] = "not-a-real-token",
            })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/vendor/metrics");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Postgres", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
