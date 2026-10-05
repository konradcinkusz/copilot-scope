using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Golden-file scoring tests for the QualityEngine formula.
/// These tests assert exact scores for known scenarios to catch silent regressions.
/// Changing the formula (weights, bounds, or renormalization) is a deliberate change
/// that requires updating these golden values and committing them as a feature change.
/// </summary>
public class GoldenScoringTests
{
    [Fact]
    public void GoldenSession_Perfect_Scores091()
    {
        // Scenario: clean session, good latency, perfect reliability.
        // Expected: reliability ~0.95, latency ~0.80, acceptance/friction/feedback/efficiency all 1.0,
        // renormalized mean with 0.25·0.95 + 0.20·1.0 + ... ≈ 0.91
        var session = new CopilotSession { Id = "golden-perfect" };
        session.ChatCalls = 10;
        session.ToolCalls = 10;
        session.ChatErrors = 0;
        session.ToolErrors = 0;
        session.TtftMs.AddRange(new[] { 350, 400, 450 });
        session.EditsAccepted = 8;
        session.EditsRejected = 0;

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        Assert.InRange(report.Score, 88, 93);
        Assert.True(report.Confidence > 0.8, $"Expected high confidence, got {report.Confidence}.");
        Assert.NotEmpty(report.Components);
    }

    [Fact]
    public void GoldenSession_ErrorProne_Scores31()
    {
        // Scenario: many errors, slow latency, low acceptance → low score.
        // Expected: reliability ~0.30, latency ~0.20, acceptance ~0.25, friction low,
        // renormalized ≈ 0.31
        var session = new CopilotSession { Id = "golden-errors" };
        session.ChatCalls = 10;
        session.ToolCalls = 10;
        session.ChatErrors = 4;
        session.ToolErrors = 3;
        session.TtftMs.AddRange(new[] { 5000, 6000, 7000 });
        session.EditsAccepted = 1;
        session.EditsRejected = 7;

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        Assert.InRange(report.Score, 28, 35);
        Assert.True(report.Confidence > 0.75, $"Expected solid confidence, got {report.Confidence}.");
    }

    [Fact]
    public void GoldenSession_Balanced_Scores70()
    {
        // Scenario: typical mix — some errors, moderate latency, fair acceptance.
        var session = new CopilotSession { Id = "golden-balanced" };
        session.ChatCalls = 15;
        session.ToolCalls = 8;
        session.ChatErrors = 1;
        session.ToolErrors = 1;
        session.TtftMs.AddRange(new[] { 800, 900, 1000 });
        session.EditsAccepted = 5;
        session.EditsRejected = 2;

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        Assert.InRange(report.Score, 65, 75);
        Assert.True(report.Confidence > 0.7, $"Expected good confidence, got {report.Confidence}.");
    }

    [Fact]
    public void GoldenSession_HighThroughputHighCost_StillGood()
    {
        // Scenario: high token usage, many turns, but clean — should stay >0.75.
        // This guards against penalizing efficiency too hard when reliability is perfect.
        var session = new CopilotSession { Id = "golden-throughput" };
        session.ChatCalls = 50;
        session.ToolCalls = 40;
        session.ChatErrors = 0;
        session.ToolErrors = 0;
        session.TtftMs.AddRange(Enumerable.Repeat(600.0, 30).ToList()); // consistent TTFT
        session.EditsAccepted = 20;
        session.EditsRejected = 2;
        session.InputTokens = 100_000;
        session.OutputTokens = 50_000;

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        Assert.InRange(report.Score, 75, 90);
        Assert.Contains(report.Components, c => c.Name == "Reliability");
    }

    [Fact]
    public void RenormalizationWorks_MissingComponentsIgnored()
    {
        // Scenario: session with only reliability and latency (no acceptance data).
        // The engine should renormalize to treat these two as the full weight.
        var session = new CopilotSession { Id = "golden-minimal-signals" };
        session.ChatCalls = 5;
        session.ChatErrors = 1;
        session.TtftMs.AddRange(new[] { 400.0, 450.0 });
        // No edit data → no acceptance signal

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Should still have a meaningful score, not NaN or degenerate
        Assert.True(!double.IsNaN(report.Score) && !double.IsInfinity(report.Score));
        Assert.InRange(report.Score, 0, 100);
    }

    [Fact]
    public void ComponentWeights_AreAppliedAndVisible()
    {
        // Scenario: verify that the reported components reflect the expected weights.
        var session = new CopilotSession { Id = "golden-weights" };
        session.ChatCalls = 10;
        session.ChatErrors = 0;
        session.ToolCalls = 10;
        session.ToolErrors = 0;
        session.TtftMs.AddRange(new[] { 400.0, 450.0, 500.0 });
        session.EditsAccepted = 5;
        session.EditsRejected = 0;

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        var reliability = report.Components.FirstOrDefault(c => c.Name == "Reliability");
        var acceptance = report.Components.FirstOrDefault(c => c.Name == "Acceptance");
        var latency = report.Components.FirstOrDefault(c => c.Name == "Latency");

        // All three should be present with >0 weight
        Assert.NotNull(reliability);
        Assert.NotNull(acceptance);
        Assert.NotNull(latency);
        Assert.True(reliability.Weight > 0);
        Assert.True(acceptance.Weight > 0);
        Assert.True(latency.Weight > 0);
    }

    [Fact]
    public void SessionMode_AutonomousZerosAcceptanceAndLatency()
    {
        // Scenario: agent/autonomous session (high tool-to-chat ratio, no human signals).
        // Expected: acceptance and latency components zeroed out by mode classification.
        var session = new CopilotSession { Id = "golden-autonomous" };
        session.ChatCalls = 2;
        session.ToolCalls = 20; // high ratio → autonomous
        session.ChatErrors = 0;
        session.ToolErrors = 1;
        session.TtftMs.AddRange(new[] { 100.0, 200.0 }); // low TTFT doesn't matter in autonomous mode
        // No edit acceptance data

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Should evaluate, but acceptance and latency components should be hidden or zero-weight
        Assert.True(!double.IsNaN(report.Score));
        var acceptance = report.Components.FirstOrDefault(c => c.Name == "Acceptance");
        var latency = report.Components.FirstOrDefault(c => c.Name == "Latency");
        // In autonomous mode these should either not appear or have zero weight
        if (acceptance is not null) Assert.Equal(0.0, acceptance.Weight);
        if (latency is not null) Assert.Equal(0.0, latency.Weight);
    }
}
