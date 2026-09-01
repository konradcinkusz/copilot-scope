namespace CopilotScope.Collector.Domain;

/// <summary>
/// How a session was driven — the axis that decides which signals mean anything.
///
/// The original scoring model assumed a person watching a chat: time-to-first-token
/// measures their wait, and an accepted edit is their judgment about the code. Neither
/// holds for a delegated agent run. Nobody is waiting on a background agent's first
/// token, and an agent working under <c>acceptEdits</c> applies its own edits, so the
/// "acceptance" it reports is a permission setting rather than an opinion.
///
/// Scoring an autonomous run on those two components measures the wrong thing, which is
/// why <see cref="Quality.ScoringProfile"/> selects component weights by this mode.
/// </summary>
public enum SessionMode
{
    /// <summary>Not enough signal yet to tell (no calls, or no tool/decision activity).</summary>
    Unknown,

    /// <summary>A person in a chat loop: chat-dominant, low tool fan-out per turn.</summary>
    Interactive,

    /// <summary>Agentic tool use with a person still approving or reacting to the work.</summary>
    SupervisedAgent,

    /// <summary>Delegated run: agentic tool use with no human decision or feedback in it.</summary>
    Autonomous
}

/// <summary>
/// Classifies a session's mode from the shape of its telemetry. Deliberately shape-based
/// rather than emitter-based: the same assistant is used interactively and headlessly, and
/// no vendor reports which one a given session was.
/// </summary>
public static class SessionModeClassifier
{
    /// <summary>Tool calls per chat call above which a session reads as agent-driven.</summary>
    private const double AgenticToolRatio = 3.0;

    /// <summary>Floor on absolute tool calls, so a 2-tool session isn't called agentic.</summary>
    private const int AgenticToolFloor = 5;

    /// <summary>Must be called while holding the session lock (i.e. inside Snapshot/Apply).</summary>
    public static SessionMode Classify(CopilotSession s)
    {
        var calls = s.ChatCalls + s.ToolCalls;
        if (calls == 0) return SessionMode.Unknown;

        var humanSignals = s.EditsAccepted + s.EditsRejected + s.ThumbsUp + s.ThumbsDown;

        // Strongest single signal: the agent applied edits under a permission setting and
        // no human decision was ever recorded. That is a delegated run by construction,
        // whatever the tool ratio looks like.
        if (s.EditsAutoAccepted > 0 && humanSignals == 0) return SessionMode.Autonomous;

        var agentic = s.ToolCalls >= AgenticToolFloor
                   && s.ToolCalls >= AgenticToolRatio * Math.Max(1, s.ChatCalls);
        if (!agentic) return SessionMode.Interactive;

        return humanSignals > 0 ? SessionMode.SupervisedAgent : SessionMode.Autonomous;
    }

    /// <summary>Short human-readable label for the API and dashboard.</summary>
    public static string Label(SessionMode mode) => mode switch
    {
        SessionMode.Interactive => "interactive",
        SessionMode.SupervisedAgent => "supervised agent",
        SessionMode.Autonomous => "autonomous agent",
        _ => "unknown"
    };
}
