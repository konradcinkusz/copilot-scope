using System.Net;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Workflow-friction signals: the rename, the default-off posture, and the two-stage opt-in.
///
/// These are compliance-shaped tests, so they assert on the surfaces a DPO or a works council
/// would actually look at — the strings in the API payload, whether the analyzer runs at all,
/// and whether prompt text comes back — rather than on internal state. EU AI Act Art. 5(1)(f)
/// prohibits workplace emotion inference outright, so "no user-facing surface claims to measure
/// emotion" is a property worth a test rather than a code review.
/// </summary>
public sealed class WorkflowFrictionTests
{
    private static CopilotSession SessionWithRepairMarkers()
    {
        var s = new CopilotSession { Id = "friction-1" };
        var t = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        s.AddTranscript(t, "gpt-5", "Please add a retry policy to the forwarder queue", "done", 0);
        s.AddTranscript(t.AddMinutes(1), "gpt-5", "add a retry policy to the forwarder queue please", "done", 1);
        s.AddTranscript(t.AddMinutes(2), "gpt-5", "this still doesn't work, wrong again!!", "sorry", 2);
        return s;
    }

    private static WorkflowFrictionAnalyzer Analyzer(bool enabled = true, bool previews = false) =>
        new(new WorkflowFrictionOptions { Enabled = enabled, IncludeFlaggedMessages = previews });

    // ------------------------------------------------------------------ naming

    [Fact]
    public void NoUserFacingStringClaimsToMeasureEmotion()
    {
        // The acceptance criterion for #95, as a test: every string that reaches the API
        // payload — report name, algorithm, metric labels, findings — has to describe observed
        // events. A word list cannot measure mood, and claiming to is what Art. 5(1)(f) bans.
        var report = Analyzer(previews: true).Analyze(SessionWithRepairMarkers());

        var surfaces = new List<string> { report.Name, report.Algorithm };
        surfaces.AddRange(report.Metrics.Select(m => m.Label));
        surfaces.AddRange(report.Findings);

        // Words that would assert a claim about someone's inner state. "emotion" is not on the
        // list because the report deliberately says it does NOT measure emotional state, and a
        // test that banned the disclaimer would be pushing in the wrong direction.
        string[] claims = ["frustrat", "angry", "upset", "furious", "mood", "calm", "infuriat", "feels"];
        foreach (var surface in surfaces)
            Assert.All(claims, word =>
                Assert.DoesNotContain(word, surface, StringComparison.OrdinalIgnoreCase));

        // And the disclaimer is present, so the surface says what it is rather than only
        // avoiding what it is not.
        Assert.Contains(report.Findings, f =>
            f.Contains("not emotional state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheReportIsNamedForWhatItObserves()
    {
        var report = Analyzer().Analyze(SessionWithRepairMarkers());
        Assert.Equal("Workflow friction signals", report.Name);
        Assert.Contains("repair", report.Algorithm, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------- the two switches

    [Fact]
    public void TheAnalyzerDoesNotRunUntilItIsSwitchedOn()
    {
        Assert.False(Analyzer(enabled: false).Enabled);
        Assert.True(Analyzer().Enabled);
        Assert.False(new WorkflowFrictionOptions().Enabled);
        Assert.False(new WorkflowFrictionOptions().IncludeFlaggedMessages);
    }

    [Fact]
    public void ADisabledAnalyzerProducesNoReportAtAll()
    {
        // Not an empty report and not a "disabled" placeholder: the dashboard renders reports
        // generically, so a placeholder would still put the feature's name on screen in a
        // deployment whose works agreement does not mention it.
        var pipeline = new InsightPipeline([Analyzer(enabled: false)]);
        Assert.Empty(pipeline.Analyze(SessionWithRepairMarkers()));

        Assert.Single(new InsightPipeline([Analyzer()]).Analyze(SessionWithRepairMarkers()));
    }

    [Fact]
    public void EnablingTheAnalyzerDoesNotAlsoQuotePromptText()
    {
        // The second opt-in is the point: the rate is a team signal, the quote is a record of
        // what one person typed. Turning on the first must not silently turn on the second.
        var report = Analyzer().Analyze(SessionWithRepairMarkers());

        Assert.Equal("ok", report.Status);
        Assert.All(report.Findings, f =>
            Assert.DoesNotContain("still doesn't work", f, StringComparison.OrdinalIgnoreCase));
        // The count is still reported — suppressing the quote must not suppress the signal.
        Assert.Contains(report.Metrics, m => m.Label.Contains("with markers", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Contains("IncludeFlaggedMessages", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSecondOptInBringsBackThePerMessagePreviews()
    {
        var report = Analyzer(previews: true).Analyze(SessionWithRepairMarkers());
        Assert.Contains(report.Findings, f => f.Contains("still doesn't work", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Findings, f => f.Contains("strong marker", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSignalItselfIsUnchangedByTheRename()
    {
        // A rename that quietly changed the detector would invalidate every comparison a user
        // has made across it. Same four inputs, same arithmetic, same index.
        var report = Analyzer(previews: true).Analyze(SessionWithRepairMarkers());
        Assert.True(report.Score >= 0.4, $"index was {report.Score}");
        Assert.Contains(report.Findings, f => f.Contains("rephrasing", StringComparison.Ordinal));
    }

    [Fact]
    public void ASessionWithNoCapturedContentReportsNoData()
    {
        var report = Analyzer().Analyze(new CopilotSession { Id = "empty" });
        Assert.Equal("no-data", report.Status);
        Assert.Contains(report.Findings, f => f.Contains("Privacy mode", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFlagThresholdIsConfigurable()
    {
        // The lexicon is language- and team-specific; a threshold nobody can tune is one nobody
        // can validate. A single mild marker scores 0.15, so it crosses a 0.1 floor and not the
        // 0.3 default — which is exactly the band an operator would want to move.
        var mild = new CopilotSession { Id = "mild" };
        mild.AddTranscript(DateTimeOffset.UtcNow, "gpt-5", "please revert the last change", "sure", 0);

        WorkflowFrictionAnalyzer At(double threshold) => new(new WorkflowFrictionOptions
        { Enabled = true, IncludeFlaggedMessages = true, FlagThreshold = threshold });

        Assert.Contains("1 / 1", At(0.1).Analyze(mild).Metrics
            .Single(m => m.Label.Contains("with markers", StringComparison.Ordinal)).Value);
        Assert.Contains("1 / 0", At(0.3).Analyze(mild).Metrics
            .Single(m => m.Label.Contains("with markers", StringComparison.Ordinal)).Value);
    }

    // ------------------------------------------------------------------- the API

    private static WebApplicationFactory<SessionSummaryDto> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    [Fact]
    public async Task TheAggregateEndpointRefusesUntilTheFeatureIsOn()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/friction");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("WorkflowFriction", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAggregateEndpointServesATeamRateWhenOn()
    {
        using var factory = Factory(("CopilotScope:WorkflowFriction:Enabled", "true"));
        using var client = factory.CreateClient();

        using var doc = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/friction?days=30"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(30, root.GetProperty("windowDays").GetInt32());
        // No per-session breakdown anywhere in the payload — an aggregate surface that shipped
        // a session list would be the ranking this whole design refuses to build.
        Assert.False(root.TryGetProperty("sessions", out _));
        Assert.Contains("not emotional state", root.GetProperty("note").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAggregateEndpointObeysTheKAnonymityFloor()
    {
        using var factory = Factory(
            ("CopilotScope:WorkflowFriction:Enabled", "true"),
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "5"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/friction")).StatusCode);
    }
}
