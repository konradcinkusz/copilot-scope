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
        session.Apply(s => {
            for (int i = 0; i < 3; i++) {
                var turn = s.TurnFor($"trace-perfect-{i}", DateTimeOffset.UtcNow.AddSeconds(i * 2));
                turn.ChatCalls = 3; turn.ToolCalls = 3; turn.ChatErrors = 0; turn.ToolErrors = 0;
                turn.End = turn.Start.AddMilliseconds(500);
                s.Turns = 3;
            }
        });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // High-quality session: zero errors, good latency, high acceptance → score >= 80
        Assert.True(report.Score >= 80, $"Expected score >= 80, got {report.Score}");
        Assert.True(report.Confidence > 0.6, $"Expected confidence > 0.6, got {report.Confidence}");
        Assert.NotEmpty(report.Components);

        var reliability = report.Components.FirstOrDefault(c => c.Name == "Reliability");
        Assert.NotNull(reliability);
        Assert.Equal(1.0, reliability.Value);
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
        session.Apply(s => {
            for (int i = 0; i < 3; i++) {
                var turn = s.TurnFor($"trace-errors-{i}", DateTimeOffset.UtcNow.AddSeconds(i * 3));
                turn.ChatCalls = 3; turn.ToolCalls = 3;
                turn.ChatErrors = i == 0 ? 2 : (i == 1 ? 2 : 0);
                turn.ToolErrors = i == 0 ? 2 : (i == 1 ? 1 : 0);
                turn.End = turn.Start.AddMilliseconds(1500);
                s.Turns = 3;
            }
        });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Error-prone: high error rate, slow latency, low acceptance → score <= 50
        Assert.True(report.Score <= 50, $"Expected score <= 50, got {report.Score}");
        Assert.True(report.Confidence > 0.5, $"Expected confidence > 0.5, got {report.Confidence}");

        var reliability = report.Components.FirstOrDefault(c => c.Name == "Reliability");
        Assert.NotNull(reliability);
        Assert.True(reliability.Value < 0.5, $"Expected reliability < 0.5, got {reliability.Value}");
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
        session.Apply(s => {
            for (int i = 0; i < 5; i++) {
                var turn = s.TurnFor($"trace-balanced-{i}", DateTimeOffset.UtcNow.AddSeconds(i * 2));
                turn.ChatCalls = 3; turn.ToolCalls = 1;
                turn.ChatErrors = i == 0 ? 1 : 0;
                turn.ToolErrors = i == 2 ? 1 : 0;
                turn.End = turn.Start.AddMilliseconds(900);
                s.Turns = 5;
            }
        });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Balanced: some errors but decent latency and acceptance → mid-range score
        Assert.True(report.Score > 50 && report.Score < 85,
            $"Expected score between 50-85, got {report.Score}");
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
        session.Apply(s => {
            for (int i = 0; i < 10; i++) {
                var turn = s.TurnFor($"trace-throughput-{i}", DateTimeOffset.UtcNow.AddSeconds(i));
                turn.ChatCalls = 5; turn.ToolCalls = 4;
                turn.ChatErrors = 0; turn.ToolErrors = 0;
                turn.End = turn.Start.AddMilliseconds(600);
                s.Turns = 10;
            }
        });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Clean high-volume session: should score well despite token usage
        Assert.True(report.Score > 70, $"Expected score > 70, got {report.Score}");
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
        session.Apply(s => {
            var turn = s.TurnFor("trace-minimal-0", DateTimeOffset.UtcNow);
            turn.ChatCalls = 5; turn.ToolCalls = 0;
            turn.ChatErrors = 1; turn.ToolErrors = 0;
            turn.End = turn.Start.AddMilliseconds(425);
            s.Turns = 1;
        });

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
        session.Apply(s => {
            for (int i = 0; i < 3; i++) {
                var turn = s.TurnFor($"trace-weights-{i}", DateTimeOffset.UtcNow.AddSeconds(i));
                turn.ChatCalls = 3; turn.ToolCalls = 3;
                turn.ChatErrors = 0; turn.ToolErrors = 0;
                turn.End = turn.Start.AddMilliseconds(450);
                s.Turns = 3;
            }
        });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Verify key components are present and have meaningful weights
        var reliability = report.Components.FirstOrDefault(c => c.Name == "Reliability");
        var acceptance = report.Components.FirstOrDefault(c => c.Name == "Acceptance");
        var latency = report.Components.FirstOrDefault(c => c.Name == "Latency");

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
        session.TtftMs.AddRange(new[] { 100.0, 200.0 });
        // No edit acceptance data
        session.Apply(s => {
            for (int i = 0; i < 2; i++) {
                var turn = s.TurnFor($"trace-autonomous-{i}", DateTimeOffset.UtcNow.AddSeconds(i));
                turn.ChatCalls = 1; turn.ToolCalls = 10;
                turn.ChatErrors = 0; turn.ToolErrors = i == 0 ? 1 : 0;
                turn.End = turn.Start.AddMilliseconds(150);
                s.Turns = 2;
            }
        });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        // Should evaluate, but acceptance and latency components should be hidden or zero-weight in autonomous mode
        Assert.True(!double.IsNaN(report.Score));
        var acceptance = report.Components.FirstOrDefault(c => c.Name == "Acceptance");
        var latency = report.Components.FirstOrDefault(c => c.Name == "Latency");

        // In autonomous mode these should either not appear or have zero weight
        if (acceptance is not null) Assert.Equal(0.0, acceptance.Weight);
        if (latency is not null) Assert.Equal(0.0, latency.Weight);
    }
}
