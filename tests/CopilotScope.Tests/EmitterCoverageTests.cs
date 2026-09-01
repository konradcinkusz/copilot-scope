using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Holds the published coverage matrix to what the pipeline actually does.
///
/// A disclosure that nobody checks is worse than none — it is a claim that has quietly stopped
/// being true. So rather than trusting the table in EmitterCoverage, these tests drive real
/// batches through the real ingest and scoring path and assert that the components the matrix
/// says are unavailable really do come back as priors.
/// </summary>
public class EmitterCoverageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A Claude Code session as a default install reports one: metrics and log events,
    /// no spans, no thumbs, edit decisions from tool_decision.</summary>
    private static CopilotSession ClaudeCodeSession()
    {
        var s = new CopilotSession { Id = "cc", FirstSeen = T0, LastSeen = T0, EmitterKind = EmitterKind.ClaudeCode };
        s.ChatCalls = 6;
        s.ToolCalls = 4;
        s.InputTokens = 5000;
        s.EditsAccepted = 3;
        s.EditsRejected = 1;
        return s;
    }

    /// <summary>A Copilot CLI session: no editor UI, so no edit decisions and no thumbs.</summary>
    private static CopilotSession CliSession()
    {
        var s = new CopilotSession { Id = "cli", FirstSeen = T0, LastSeen = T0, EmitterKind = EmitterKind.CLI };
        s.ChatCalls = 6;
        s.ToolCalls = 2;
        s.InputTokens = 5000;
        s.TtftMs.AddRange([700, 800, 900]);
        return s;
    }

    private static QualityComponent Component(CopilotSession s, string name) =>
        new QualityEngine().Evaluate(s).Components.Single(c => c.Name == name);

    // ------------------------------------------------------- the matrix matches reality

    [Fact]
    public void EveryEmitterKindWeClaimToSupportHasARow()
    {
        // A new emitter added without a row would be published as "supported" with no stated
        // coverage at all.
        var declared = EmitterCoverage.All.Select(e => e.Emitter).ToHashSet();
        var supported = Enum.GetValues<EmitterKind>().Where(k => k != EmitterKind.Unknown);

        Assert.All(supported, kind =>
            Assert.True(declared.Contains(kind), $"EmitterKind.{kind} has no coverage row."));
    }

    [Fact]
    public void ClaudeCodeReallyCannotPopulateFeedback()
    {
        // The matrix says feedback is None for Claude Code. Prove it: a fully-populated Claude
        // session still scores feedback as a prior with zero samples.
        var row = EmitterCoverage.For(EmitterKind.ClaudeCode)!;
        Assert.Equal(SignalSupport.None, row.Feedback);
        Assert.Contains("feedback", row.AlwaysPrior);

        Assert.Equal(0, Component(ClaudeCodeSession(), "feedback").Samples);
    }

    [Fact]
    public void ClaudeCodeDoesPopulateAcceptanceFromToolDecisions()
    {
        // The interesting half: Claude has no survival signal but does have edit decisions, so
        // acceptance is NOT always a prior for it. Claiming otherwise would understate it.
        var row = EmitterCoverage.For(EmitterKind.ClaudeCode)!;
        Assert.Equal(SignalSupport.Full, row.EditDecisions);
        Assert.Equal(SignalSupport.None, row.EditSurvival);
        Assert.DoesNotContain("acceptance", row.AlwaysPrior);

        Assert.True(Component(ClaudeCodeSession(), "acceptance").Samples > 0);
    }

    [Fact]
    public void TheCliReallyCannotPopulateAcceptanceOrFeedback()
    {
        var row = EmitterCoverage.For(EmitterKind.CLI)!;
        Assert.Contains("acceptance", row.AlwaysPrior);
        Assert.Contains("feedback", row.AlwaysPrior);

        var session = CliSession();
        Assert.Equal(0, Component(session, "acceptance").Samples);
        Assert.Equal(0, Component(session, "feedback").Samples);
        // But latency is available, so it must not be listed as always-prior.
        Assert.DoesNotContain("latency", row.AlwaysPrior);
        Assert.True(Component(session, "latency").Samples > 0);
    }

    [Fact]
    public void CursorCannotRunTurnLevelFrictionAtAll()
    {
        // Cursor's Enterprise export sends metrics and logs but no traces, and a turn is one
        // invoke_agent trace — so TFRA cannot run. That is the sharpest limitation in the
        // matrix and the one most likely to surprise someone comparing scores.
        var row = EmitterCoverage.For(EmitterKind.Cursor)!;
        Assert.Equal(SignalSupport.None, row.Traces);
        Assert.Contains("friction", row.AlwaysPrior);
    }

    // ------------------------------------------------------- the comparability caveat

    [Fact]
    public void OneAssistantNeedsNoCaveat() =>
        Assert.False(EmitterCoverage.NeedsComparabilityCaveat(
            [EmitterKind.ClaudeCode, EmitterKind.ClaudeCode]));

    [Fact]
    public void MixingAssistantsWithDifferentEvidenceNeedsTheCaveat() =>
        // The case the product review named: an 80 here is not an 80 there.
        Assert.True(EmitterCoverage.NeedsComparabilityCaveat(
            [EmitterKind.VSCode, EmitterKind.ClaudeCode]));

    [Fact]
    public void AssistantsWithIdenticalEvidenceNeedNoCaveat() =>
        // Cowork speaks the same dialect as Claude Code, so their scores rest on the same
        // components and are directly comparable. Warning there would be noise, and a caveat
        // shown everywhere is a caveat nobody reads.
        Assert.False(EmitterCoverage.NeedsComparabilityCaveat(
            [EmitterKind.ClaudeCode, EmitterKind.Cowork]));

    [Fact]
    public void UnknownEmittersDoNotTriggerTheCaveat() =>
        Assert.False(EmitterCoverage.NeedsComparabilityCaveat(
            [EmitterKind.ClaudeCode, EmitterKind.Unknown]));
}
