using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.JudgeAgent.Domain;

namespace CopilotScope.JudgeAgent.Judging;

/// <summary>Assembles a SessionJudgeContext from a SessionDetailDto — the same read the Collector
/// already serves, reshaped into the bounded payload the judge model is asked to grade.</summary>
public sealed class SessionJudgeContextBuilder
{
    // Hard cap on transcript turns folded into the judge payload, mirroring AgentForge's
    // MaxExemplars bound so a long session never produces an unbounded prompt. Unlike
    // AgentForge (which picks exemplars across many sessions), a single session's *ending*
    // matters as much as its start — task-completion and G-Eval both need to see how the
    // conversation resolved — so when a session exceeds the cap this keeps the earliest and
    // latest turns rather than just the most recent ones, preserving the arc.
    private const int MaxTranscriptTurns = 40;
    private const int MaxFieldChars = 4000;

    public SessionJudgeContext Build(SessionDetailDto detail)
    {
        var transcript = BoundTranscript(detail.Transcript);

        var tools = detail.Tools
            .Select(t => new JudgeToolStat(t.Name, t.Calls, t.Errors))
            .ToList();

        var localComponents = detail.Summary.Quality.Components
            .Select(c => new JudgeLocalComponent(c.Name, Math.Round(c.Value, 3), c.Samples, c.Detail))
            .ToList();

        return new SessionJudgeContext(
            detail.Summary.Id,
            transcript,
            tools,
            new Dictionary<string, int>(detail.ErrorTypes),
            localComponents,
            CompletionSignals: null, // no external build/test exit-code ingest path exists yet (see docs/JUDGE_AGENT.md)
            RetrievalContext: null); // Collector doesn't capture retrieval context today; RAGAS reports "no-data" until it does
    }

    private static List<JudgeTranscriptTurn> BoundTranscript(List<TranscriptEntry> entries)
    {
        var withContent = entries.Where(e => e.Prompt is not null || e.Response is not null).ToList();

        var selected = withContent.Count <= MaxTranscriptTurns
            ? withContent
            : withContent.Take(MaxTranscriptTurns / 2)
                .Concat(withContent.Skip(withContent.Count - MaxTranscriptTurns / 2))
                .ToList();

        return selected
            .Select(e => new JudgeTranscriptTurn(e.Turn, e.Model, Truncate(e.Prompt), Truncate(e.Response)))
            .ToList();
    }

    private static string? Truncate(string? text) =>
        text is not null && text.Length > MaxFieldChars
            ? text[..MaxFieldChars] + " …[truncated]"
            : text;
}
