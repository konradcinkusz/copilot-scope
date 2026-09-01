using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Session mode and the per-mode scoring profiles. The composite was designed around a
/// person watching a chat; a delegated agent run has nobody waiting on the first token
/// and nobody accepting edits, so those components must stop counting for it.
/// </summary>
public class SessionModeTests
{
    private static CopilotSession Session(string id = "s1") =>
        new() { Id = id, FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow };

    private static CopilotSession Interactive()
    {
        var s = Session();
        s.ChatCalls = 6;
        s.ToolCalls = 2;
        s.EditsAccepted = 3;
        s.EditsRejected = 1;
        s.TtftMs.AddRange([800, 900, 1000]);
        return s;
    }

    private static CopilotSession Delegated()
    {
        var s = Session("agent-run");
        s.ChatCalls = 4;
        s.ToolCalls = 40;             // heavy tool fan-out
        s.EditsAutoAccepted = 12;     // applied under a permission mode
        s.TtftMs.AddRange([9000, 9500, 9800]); // slow, but nobody is watching
        return s;
    }

    // ------------------------------------------------------------------ classification

    [Fact]
    public void ChatDominantSessionIsInteractive() =>
        Assert.Equal(SessionMode.Interactive, SessionModeClassifier.Classify(Interactive()));

    [Fact]
    public void AutoAppliedEditsWithNoHumanSignalIsAutonomous() =>
        Assert.Equal(SessionMode.Autonomous, SessionModeClassifier.Classify(Delegated()));

    [Fact]
    public void HeavyToolUseWithHumanDecisionsIsSupervised()
    {
        var s = Session();
        s.ChatCalls = 3;
        s.ToolCalls = 30;
        s.EditsAccepted = 4;   // a person is still approving
        Assert.Equal(SessionMode.SupervisedAgent, SessionModeClassifier.Classify(s));
    }

    [Fact]
    public void HeavyToolUseWithNoHumanSignalAtAllIsAutonomous()
    {
        // A read-only agent run: no edit decisions of any kind, no feedback.
        var s = Session();
        s.ChatCalls = 2;
        s.ToolCalls = 25;
        Assert.Equal(SessionMode.Autonomous, SessionModeClassifier.Classify(s));
    }

    [Fact]
    public void EmptySessionHasNoMode() =>
        Assert.Equal(SessionMode.Unknown, SessionModeClassifier.Classify(Session()));

    [Fact]
    public void AFewToolCallsIsStillInteractive()
    {
        // The agentic test needs an absolute floor, or a two-tool chat reads as an agent run.
        var s = Session();
        s.ChatCalls = 1;
        s.ToolCalls = 4;
        Assert.Equal(SessionMode.Interactive, SessionModeClassifier.Classify(s));
    }

    // ------------------------------------------------------------------ scoring

    [Fact]
    public void AutonomousSessionIsNotPenalizedForSlowFirstToken()
    {
        // Same 9.5s TTFT that would nearly zero the latency component interactively.
        var report = new QualityEngine().Evaluate(Delegated());

        var latency = Assert.Single(report.Components, c => c.Name == "latency");
        Assert.Equal(0, latency.Weight);          // excluded by the profile
        Assert.True(latency.Samples > 0);          // still computed and reported
        Assert.Equal("autonomous", report.Profile);
        Assert.Equal(SessionMode.Autonomous, report.Mode);
    }

    [Fact]
    public void AutoAcceptedEditsDoNotCountAsAcceptance()
    {
        var report = new QualityEngine().Evaluate(Delegated());

        var acceptance = Assert.Single(report.Components, c => c.Name == "acceptance");
        Assert.Equal(0, acceptance.Weight);
        Assert.Equal(0, acceptance.Samples);   // 12 auto-applied edits are not evidence
        Assert.Contains("auto-applied", acceptance.Detail);
    }

    [Fact]
    public void InteractiveWeightsAreUnchanged()
    {
        // The published v2 weights are a documented contract; only agent modes deviate.
        var report = new QualityEngine().Evaluate(Interactive());

        Assert.Equal("interactive", report.Profile);
        Assert.Equal(0.25, report.Components.Single(c => c.Name == "reliability").Weight);
        Assert.Equal(0.20, report.Components.Single(c => c.Name == "acceptance").Weight);
        Assert.Equal(0.20, report.Components.Single(c => c.Name == "friction").Weight);
        Assert.Equal(0.15, report.Components.Single(c => c.Name == "latency").Weight);
        Assert.Equal(0.10, report.Components.Single(c => c.Name == "feedback").Weight);
        Assert.Equal(0.10, report.Components.Single(c => c.Name == "efficiency").Weight);
    }

    [Fact]
    public void SlowHeadlessRunOutscoresTheSameRunJudgedInteractively()
    {
        // The point of the whole change: an error-free delegated run with a 9.5s first
        // token should not be graded as if a developer sat waiting for it.
        var delegatedScore = new QualityEngine().Evaluate(Delegated()).Score;

        // Identical work, but a human accepted the edits — so it is scored on latency too.
        var supervised = Session("supervised");
        supervised.ChatCalls = 4;
        supervised.ToolCalls = 40;
        supervised.EditsAccepted = 12;
        supervised.TtftMs.AddRange([9000, 9500, 9800]);
        var supervisedScore = new QualityEngine().Evaluate(supervised).Score;

        Assert.True(delegatedScore > supervisedScore,
            $"delegated {delegatedScore} should beat supervised {supervisedScore} on identical work");
    }

    [Fact]
    public void ZeroWeightComponentsDoNotDistortConfidence()
    {
        // Coverage must be computed over components that actually carry weight, or an
        // excluded component would inflate the denominator and depress confidence.
        var report = new QualityEngine().Evaluate(Delegated());
        Assert.True(report.Confidence > 0);
        Assert.True(report.Confidence <= 1.0);
    }
}
