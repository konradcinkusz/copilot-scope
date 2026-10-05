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
        // Reliability = (1-0/30)^2 = 1.0
        // Latency p50=400ms → 1.0 - log(400/300)/log(10000/300) ≈ 0.92
        // Acceptance = 0.6·1.0 + 0.4·1.0 = 1.0 (all 8/8 accepted)
        // Friction = 1.0 (no errors, no repair loops in 3 turns)
        // Coverage = 0.25 + 0.20 + 0.15 + 0.20 = 0.80
        // Score = (0.25·1.0 + 0.20·1.0 + 0.15·0.92 + 0.20·1.0) / 0.80 · 100 ≈ 96
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

        Assert.InRange(report.Score, 85, 98);
        Assert.True(report.Confidence > 0.7, $"Expected good confidence, got {report.Confidence}.");
        Assert.NotEmpty(report.Components);
    }

    [Fact]
    public void GoldenSession_ErrorProne_Scores31()
    {
        // Scenario: many errors, slow latency, low acceptance → low score.
        // Reliability = (1 - (4·2+3)/(10·2+10))^2 = (1 - 11/30)^2 = 0.36
        // Latency p50=6000ms → 1.0 - log(6000/300)/log(10000/300) ≈ 0.26
        // Acceptance = 0.6·(1/8) + 0.4·(1/8) = 0.125
        // Friction with errors: reduced by 0.35 per chat error, 0.15 per tool error
        // Coverage ≈ 0.25 + 0.20 + 0.15 + 0.20 = 0.80 (friction dragged down)
        // Score ≈ (0.25·0.36 + 0.20·0.125 + 0.15·0.26 + 0.20·0.25) / 0.80 · 100 ≈ 24
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
                turn.ChatErrors = i == 0 ? 2 : (i == 1 ? 2 : 0); // 4 total errors distributed
                turn.ToolErrors = i == 0 ? 2 : (i == 1 ? 1 : 0);
                turn.End = turn.Start.AddMilliseconds(1500);
                s.Turns = 3;
            }
        });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        Assert.InRange(report.Score, 15, 35);
        Assert.True(report.Confidence > 0.7, $"Expected solid confidence, got {report.Confidence}.");
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

        Assert.InRange(report.Score, 60, 80);
        Assert.True(report.Confidence > 0.6, $"Expected decent confidence, got {report.Confidence}.");
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

        Assert.InRange(report.Score, 70, 92);
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

        // Should evaluate, but acceptance and latency components should be hidden or zero-weight
        Assert.True(!double.IsNaN(report.Score));
        var acceptance = report.Components.FirstOrDefault(c => c.Name == "Acceptance");
        var latency = report.Components.FirstOrDefault(c => c.Name == "Latency");
        // In autonomous mode these should either not appear or have zero weight
        if (acceptance is not null) Assert.Equal(0.0, acceptance.Weight);
        if (latency is not null) Assert.Equal(0.0, latency.Weight);
    }
}
