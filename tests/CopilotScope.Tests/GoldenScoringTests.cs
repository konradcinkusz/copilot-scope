using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Golden-file scoring tests for the QualityEngine formula.
/// These tests verify the scoring formula works correctly with known-answer scenarios.
/// Changing weights in ScoringProfile requires consciously updating these tests.
/// </summary>
public class GoldenScoringTests
{
    [Fact]
    public void GoldenSession_Perfect_HighScore()
    {
        // Scenario: clean session, good latency, perfect reliability.
        // No errors, high acceptance, good latency → high score.
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

        // High-quality session: zero errors, good latency, high acceptance → score > 70
        Assert.True(report.Score > 70, $"Expected score > 70, got {report.Score}");
        Assert.True(report.Confidence > 0.5, $"Expected confidence > 0.5, got {report.Confidence}");
        Assert.NotEmpty(report.Components);

        var reliability = report.Components.FirstOrDefault(c => c.Name == "Reliability");
        Assert.NotNull(reliability);
        Assert.True(reliability.Value > 0.95, $"Expected high reliability, got {reliability.Value}");
    }

    [Fact]
    public void GoldenSession_ErrorProne_LowScore()
    {
        // Scenario: many errors, slow latency, low acceptance → low score.
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

        // Error-prone: high error rate, slow latency, low acceptance → score < 70
        Assert.True(report.Score < 70, $"Expected score < 70, got {report.Score}");
        Assert.True(report.Confidence > 0.5, $"Expected confidence > 0.5, got {report.Confidence}");

        var reliability = report.Components.FirstOrDefault(c => c.Name == "Reliability");
        Assert.NotNull(reliability);
        Assert.True(reliability.Value < 0.8, $"Expected low reliability, got {reliability.Value}");
    }

    [Fact]
    public void GoldenSession_Balanced_MidScore()
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

        // Balanced: some errors but decent latency and acceptance → mid-range score
        Assert.True(report.Score > 30 && report.Score < 90,
            $"Expected score between 30-90, got {report.Score}");
        Assert.True(report.Confidence > 0.5, $"Expected confidence > 0.5, got {report.Confidence}");
    }

    [Fact]
    public void GoldenSession_HighThroughputHighCost_StillGood()
    {
        // Scenario: high token usage, many turns, but clean — should stay reasonably good.
        // This guards against penalizing efficiency too hard when reliability is perfect.
        var session = new CopilotSession { Id = "golden-throughput" };
        session.ChatCalls = 50;
        session.ToolCalls = 40;
        session.ChatErrors = 0;
        session.ToolErrors = 0;
        session.TtftMs.AddRange(Enumerable.Repeat(600.0, 30).ToList());
        session.EditsAccepted = 20;
        session.EditsRejected = 2;
        session.InputTokens = 100_000;
        session.OutputTokens = 50_000;

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Clean high-volume session: should score reasonably well despite token usage
        Assert.True(report.Score > 50, $"Expected score > 50, got {report.Score}");
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

        // Should produce a valid score even with missing signals
        Assert.True(!double.IsNaN(report.Score) && !double.IsInfinity(report.Score));
        Assert.InRange(report.Score, 0, 100);
        Assert.True(report.Confidence >= 0, "Confidence should be non-negative");
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

        // Verify key components are present
        var reliability = report.Components.FirstOrDefault(c => c.Name == "Reliability");
        var acceptance = report.Components.FirstOrDefault(c => c.Name == "Acceptance");
        var latency = report.Components.FirstOrDefault(c => c.Name == "Latency");

        Assert.NotNull(reliability);
        Assert.NotNull(acceptance);
        Assert.NotNull(latency);
        // For interactive mode, these should all have non-zero weight
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
        session.TtftMs.AddRange(new[] { 100.0, 200.0 });
        // No edit acceptance data

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Should evaluate successfully
        Assert.True(!double.IsNaN(report.Score));
        // Autonomous mode should have 0 weight for acceptance and latency
        var acceptance = report.Components.FirstOrDefault(c => c.Name == "Acceptance");
        var latency = report.Components.FirstOrDefault(c => c.Name == "Latency");

        if (acceptance is not null) Assert.True(acceptance.Weight < 0.001, $"Expected acceptance weight ~0 in autonomous, got {acceptance.Weight}");
        if (latency is not null) Assert.True(latency.Weight < 0.001, $"Expected latency weight ~0 in autonomous, got {latency.Weight}");
    }
}
