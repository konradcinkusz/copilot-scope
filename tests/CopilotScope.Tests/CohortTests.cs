using System.Net;
using System.Net.Http.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Team-lead views: cohort rollups, before/after comparison, and export.
///
/// The load-bearing property across all of it is the one that is easiest to lose by accident:
/// <b>no view and no export has a per-developer dimension</b>. Every axis describes the tooling.
/// A rollup that grouped by subject, or an export that carried session ids, would be the
/// scoreboard this product refuses to build, arriving through a side door — so that is asserted
/// rather than assumed.
/// </summary>
public sealed class CohortTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static CopilotSession Session(string id, string? repo = "acme/api",
        EmitterKind emitter = EmitterKind.VSCode, string model = "gpt-5",
        string? subject = "host-a", int chatCalls = 4, int errors = 0,
        DateTimeOffset? lastSeen = null)
    {
        var s = new CopilotSession
        {
            Id = id,
            Repository = repo,
            EmitterKind = emitter,
            SubjectId = subject,
            FirstSeen = lastSeen ?? T0,
            LastSeen = lastSeen ?? T0,
            ChatCalls = chatCalls,
            ChatErrors = errors,
            InputTokens = 1000,
            OutputTokens = 500,
            Turns = 2,
        };
        s.ModelCalls[model] = chatCalls;
        return s;
    }

    // -------------------------------------------------------------------- filter

    [Fact]
    public void AnUnparseableFilterValueWidensRatherThanMatchingEverythingByAccident()
    {
        // A stale bookmark naming a renamed assistant should widen to everything, not 400 —
        // but it must never be silently treated as "matches this session".
        var filter = CohortFilter.From(null, "NotAnAssistant", null, "NotAKind", null);

        Assert.Null(filter.Emitter);
        Assert.Null(filter.Kind);
        Assert.True(filter.IsEmpty);
    }

    [Fact]
    public void TheFilterMatchesOnEveryToolingAxis()
    {
        var session = Session("s1", repo: "acme/api", emitter: EmitterKind.ClaudeCode, model: "sonnet-5");

        Assert.True(CohortFilter.From("ACME/API", null, null, null, null).MatchesExceptGrade(session));
        Assert.True(CohortFilter.From(null, "claudecode", null, null, null).MatchesExceptGrade(session));
        Assert.True(CohortFilter.From(null, null, "SONNET-5", null, null).MatchesExceptGrade(session));

        Assert.False(CohortFilter.From("other/repo", null, null, null, null).MatchesExceptGrade(session));
        Assert.False(CohortFilter.From(null, "cursor", null, null, null).MatchesExceptGrade(session));
        Assert.False(CohortFilter.From(null, null, "gpt-5", null, null).MatchesExceptGrade(session));
    }

    [Fact]
    public void GradeIsSeparatedBecauseItIsAPropertyOfScoringNotOfTheSession()
    {
        var filter = CohortFilter.From(null, null, null, null, "A");
        var session = Session("s1");

        // The candidate pass runs before anything has a QualityEngine, so it must not claim to
        // have applied the grade half — a filter that silently passed here would widen the page.
        Assert.True(filter.MatchesExceptGrade(session));
        Assert.True(filter.MatchesGrade("a"));
        Assert.False(filter.MatchesGrade("C"));
    }

    // ------------------------------------------------------------------- rollups

    [Fact]
    public void RollupsGroupByEveryToolingAxisAndNoOther()
    {
        var report = Cohorts.Build(
        [
            Session("s1", repo: "acme/api", emitter: EmitterKind.VSCode),
            Session("s2", repo: "acme/api", emitter: EmitterKind.ClaudeCode),
            Session("s3", repo: "acme/web", emitter: EmitterKind.VSCode),
        ], new QualityEngine(), T0.AddDays(-30), null);

        Assert.Equal(3, report.Sessions);
        Assert.Equal(2, report.ByRepository.Count);
        Assert.Equal(2, report.ByAssistant.Count);
        // Repository rows partition the population, so they sum to the session total. (Model
        // rows do not — a session can call two models — which the next test pins.)
        Assert.Equal(report.Sessions, report.ByRepository.Sum(r => r.Sessions));

        // Every row names a dimension that describes the tooling. If a developer axis is ever
        // added, this is the test that has to be deleted to do it — deliberately.
        string[] allowed = ["repository", "assistant", "model", "kind"];
        var rows = report.ByRepository.Concat(report.ByAssistant)
                         .Concat(report.ByModel).Concat(report.ByKind);
        Assert.All(rows, r => Assert.Contains(r.Dimension, allowed));
    }

    [Fact]
    public void ASessionCallingTwoModelsCountsInBothModelRows()
    {
        // Real work on both, so it belongs to both — which also means the model rows do not sum
        // to the session total. The export says so in a header comment for the same reason.
        var session = Session("s1", model: "gpt-5");
        session.ModelCalls["sonnet-5"] = 2;

        var report = Cohorts.Build([session], new QualityEngine(), null, null);

        Assert.Equal(2, report.ByModel.Count);
        Assert.All(report.ByModel, r => Assert.Equal(1, r.Sessions));
        Assert.Equal(1, report.Sessions);
    }

    [Fact]
    public void RowsCountDistinctOriginsSoAOneDeveloperGroupIsVisibleAsOne()
    {
        var report = Cohorts.Build(
        [
            Session("s1", subject: "host-a"),
            Session("s2", subject: "host-a"),
            Session("s3", subject: "host-a"),
        ], new QualityEngine(), null, null);

        var row = Assert.Single(report.ByRepository);
        Assert.Equal(3, row.Sessions);
        // Three sessions, one person. A reader who saw only "3 sessions" would call this a team
        // signal; the subject count is what stops that.
        Assert.Equal(1, row.Subjects);
    }

    [Fact]
    public void AveragesAreWithheldForGroupsTooSmallToHaveOne()
    {
        // A mean over two sessions is one session wearing a mean's clothes, and a rollout
        // decision made on it is a coin flip with a decimal point.
        var small = Cohorts.Build([Session("s1"), Session("s2")], new QualityEngine(), null, null);
        Assert.Equal(0, Assert.Single(small.ByRepository).AvgQualityScore);

        var enough = Cohorts.Build(
            [Session("s1"), Session("s2"), Session("s3")], new QualityEngine(), null, null);
        Assert.True(Assert.Single(enough.ByRepository).AvgQualityScore > 0);
    }

    [Fact]
    public void TheErrorRateIsCallsNotSessions()
    {
        var report = Cohorts.Build(
        [
            Session("s1", chatCalls: 10, errors: 1),
            Session("s2", chatCalls: 10, errors: 1),
            Session("s3", chatCalls: 10, errors: 1),
        ], new QualityEngine(), null, null);

        Assert.Equal(0.1, Assert.Single(report.ByRepository).ErrorRate, 4);
    }

    // ---------------------------------------------------------------- comparison

    [Fact]
    public void ComparingTwoWindowsReportsTheMovement()
    {
        var baseline = new[] { Session("b1", chatCalls: 10, errors: 4), Session("b2", chatCalls: 10, errors: 4),
                               Session("b3", chatCalls: 10, errors: 4) };
        var current = new[] { Session("c1", chatCalls: 10, errors: 1), Session("c2", chatCalls: 10, errors: 1),
                              Session("c3", chatCalls: 10, errors: 1) };

        var report = Cohorts.Compare("all sessions",
            baseline, T0.AddDays(-60), T0.AddDays(-30),
            current, T0.AddDays(-30), T0, new QualityEngine());

        var errors = report.Deltas.Single(d => d.Metric == "error rate");
        Assert.Equal(0.4, errors.Baseline, 3);
        Assert.Equal(0.1, errors.Current, 3);
        Assert.True(errors.Delta < 0);
        Assert.Empty(report.Caveats);
    }

    [Fact]
    public void APercentChangeFromZeroIsUndefinedRatherThanInfinite()
    {
        // Rendering ∞ next to a rollout decision is how a chart lies.
        var report = Cohorts.Compare("all sessions",
            [Session("b1", chatCalls: 10, errors: 0), Session("b2", chatCalls: 10, errors: 0),
             Session("b3", chatCalls: 10, errors: 0)], T0.AddDays(-60), T0.AddDays(-30),
            [Session("c1", chatCalls: 10, errors: 5), Session("c2", chatCalls: 10, errors: 5),
             Session("c3", chatCalls: 10, errors: 5)], T0.AddDays(-30), T0, new QualityEngine());

        var errors = report.Deltas.Single(d => d.Metric == "error rate");
        Assert.Null(errors.PercentChange);
        // The absolute delta is still reported: something did change, and saying nothing would
        // be worse than saying "we cannot express this as a percentage".
        Assert.Equal(0.5, errors.Delta, 3);
    }

    [Fact]
    public void ASmallSampleIsCalledOutRatherThanRenderedAsAResult()
    {
        var report = Cohorts.Compare("all sessions",
            [Session("b1")], T0.AddDays(-60), T0.AddDays(-30),
            [Session("c1")], T0.AddDays(-30), T0, new QualityEngine());

        Assert.Contains(report.Caveats, c => c.Contains("anecdote", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MismatchedWindowLengthsAreCalledOut()
    {
        // Otherwise "sessions: +200%" reports that one window is three times longer.
        var many = Enumerable.Range(0, 5).Select(i => Session($"c{i}")).ToArray();
        var report = Cohorts.Compare("all sessions",
            Enumerable.Range(0, 5).Select(i => Session($"b{i}")).ToArray(), T0.AddDays(-100), T0.AddDays(-90),
            many, T0.AddDays(-90), T0, new QualityEngine());

        Assert.Contains(report.Caveats, c => c.Contains("length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AcceptanceIgnoresPermissionModeAutoAccepts()
    {
        // Counting them would let a permission flag look like better suggestions.
        var b = Session("b1"); b.EditsAccepted = 1; b.EditsRejected = 1;
        var c = Session("c1"); c.EditsAccepted = 1; c.EditsRejected = 1; c.EditsAutoAccepted = 50;

        var report = Cohorts.Compare("all", [b], T0.AddDays(-2), T0.AddDays(-1),
            [c], T0.AddDays(-1), T0, new QualityEngine());

        var acceptance = report.Deltas.Single(d => d.Metric == "edit acceptance rate");
        Assert.Equal(0, acceptance.Delta, 4);
    }

    // -------------------------------------------------------------------- export

    [Fact]
    public void TheExportCarriesNoIndividualIdentifiers()
    {
        // The whole point of exporting a rollup rather than rows: there is nothing individual
        // in scope to leak. Session ids and subjects must not appear anywhere in the file.
        var report = Cohorts.Build(
        [
            Session("very-distinctive-session-id", subject: "very-distinctive-subject"),
            Session("s2"), Session("s3"),
        ], new QualityEngine(), T0.AddDays(-30), null);

        var csv = CohortExport.ToCsv(report);

        Assert.DoesNotContain("very-distinctive-session-id", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("very-distinctive-subject", csv, StringComparison.Ordinal);
        Assert.Contains("dimension,value,sessions,subjects,", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportNamesItsWindowAndItsCaveats()
    {
        // A CSV that lands in an inbox three weeks later without its window is a number nobody
        // can check.
        var csv = CohortExport.ToCsv(Cohorts.Build([Session("s1")], new QualityEngine(), T0.AddDays(-7), T0));

        Assert.Contains("# CopilotScope cohort export", csv, StringComparison.Ordinal);
        Assert.Contains("do not sum", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportQuotesValuesThatWouldOtherwiseShiftColumns()
    {
        var report = Cohorts.Build([Session("s1", repo: "acme/api, fork \"main\"")], new QualityEngine(), null, null);
        var csv = CohortExport.ToCsv(report);

        Assert.Contains("\"acme/api, fork \"\"main\"\"\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComparisonExportCarriesItsCaveatsAsComments()
    {
        var report = Cohorts.Compare("repository=acme/api",
            [Session("b1")], T0.AddDays(-2), T0.AddDays(-1),
            [Session("c1")], T0.AddDays(-1), T0, new QualityEngine());

        var csv = CohortExport.ToCsv(report);

        Assert.Contains("# CAVEAT:", csv, StringComparison.Ordinal);
        Assert.Contains("metric,baseline,current,delta,percent_change", csv, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------- API

    private static WebApplicationFactory<SessionSummaryDto> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    [Fact]
    public async Task TheCohortEndpointServesEveryRollup()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<CohortReport>("/api/cohorts?days=30");

        Assert.NotNull(report);
        Assert.NotNull(report.ByRepository);
        Assert.NotNull(report.ByAssistant);
        Assert.NotNull(report.ByModel);
        Assert.NotNull(report.ByKind);
    }

    [Fact]
    public async Task TheCohortEndpointExportsCsv()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/cohorts?days=30&format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("dimension,value,sessions", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComparisonNeedsTwoBoundedWindowsAndSaysSo()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Without a current window there is nothing to infer a "before" from. A 400 that
        // explains what to pass beats a comparison against a silently invented baseline.
        var response = await client.GetAsync("/api/compare");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("baselineSince", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComparisonDefaultsTheBaselineToThePrecedingWindow()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<ComparisonReport>("/api/compare?days=30");

        Assert.NotNull(report);
        Assert.NotNull(report.BaselineSince);
        Assert.NotNull(report.CurrentSince);
        // 30 days of "now", preceded by the 30 before it — which is what a reader means by
        // "compared to before".
        Assert.Equal(30, (report.CurrentSince!.Value - report.BaselineSince!.Value).TotalDays, 0);
    }

    [Fact]
    public async Task FacetsOfferOnlyToolingDimensions()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var doc = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/facets"));

        Assert.True(doc.RootElement.TryGetProperty("repositories", out _));
        Assert.True(doc.RootElement.TryGetProperty("assistants", out _));
        Assert.True(doc.RootElement.TryGetProperty("models", out _));
        // No developers, no subjects, no users — not as an empty list, not at all.
        foreach (var forbidden in new[] { "developers", "subjects", "users", "people" })
            Assert.False(doc.RootElement.TryGetProperty(forbidden, out _));
    }

    [Fact]
    public async Task AggregateViewsObeyTheKAnonymityFloor()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "5"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/cohorts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/compare?days=30")).StatusCode);
    }

    [Fact]
    public async Task SessionListAcceptsCohortParametersWithoutBreakingThePager()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<SessionPage>(
            "/api/sessions?days=30&repository=acme%2Fapi&emitter=VSCode&limit=10");

        Assert.NotNull(page);
        // Total must be reachable by paging: a total counted without the cohort filter would
        // promise pages that return nothing — the bug this codebase has already shipped once.
        Assert.True(page.Total >= page.Sessions.Count);
        Assert.Equal(10, page.Limit);
    }
}
