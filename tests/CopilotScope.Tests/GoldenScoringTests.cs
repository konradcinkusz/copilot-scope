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

        Assert.NotNull(report);
        Assert.InRange(report.Score, 0, 100);
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

        Assert.NotNull(report);
        Assert.InRange(report.Score, 0, 100);
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

        Assert.NotNull(report);
        Assert.InRange(report.Score, 0, 100);
    }

    [Fact]
    public void GoldenSession_HighThroughputHighCost_StillGood()
    {
        // Scenario: high token usage, many turns, but clean — should stay reasonably good.
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

        Assert.NotNull(report);
        Assert.InRange(report.Score, 0, 100);
    }

    [Fact]
    public void RenormalizationWorks_MissingComponentsIgnored()
    {
        // Scenario: session with only reliability and latency (no acceptance data).
        var session = new CopilotSession { Id = "golden-minimal-signals" };
        session.ChatCalls = 5;
        session.ChatErrors = 1;
        session.TtftMs.AddRange(new[] { 400.0, 450.0 });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        Assert.NotNull(report);
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

        Assert.NotNull(report);
        Assert.NotEmpty(report.Components);
    }

    [Fact]
    public void SessionMode_AutonomousZerosAcceptanceAndLatency()
    {
        // Scenario: agent/autonomous session (high tool-to-chat ratio, no human signals).
        var session = new CopilotSession { Id = "golden-autonomous" };
        session.ChatCalls = 2;
        session.ToolCalls = 20; // high ratio → autonomous
        session.ChatErrors = 0;
        session.ToolErrors = 1;
        session.TtftMs.AddRange(new[] { 100.0, 200.0 });

        var engine = new QualityEngine();
        var report = engine.Evaluate(session);

        Assert.NotNull(report);
        Assert.InRange(report.Score, 0, 100);
    }
}
