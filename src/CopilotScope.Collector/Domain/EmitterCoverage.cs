namespace CopilotScope.Collector.Domain;

/// <summary>How completely an assistant reports one signal.</summary>
public enum SignalSupport
{
    /// <summary>Never sent by this assistant. The component it feeds is always a prior.</summary>
    None,

    /// <summary>Sent only under a plan, flag or beta channel — see the note on the row.</summary>
    Conditional,

    /// <summary>Sent by a default install.</summary>
    Full
}

/// <summary>What one assistant reports, and what that costs the score.</summary>
public sealed record EmitterSignals(
    EmitterKind Emitter,
    string DisplayName,
    SignalSupport Traces,
    SignalSupport Metrics,
    SignalSupport Events,
    SignalSupport EditDecisions,
    SignalSupport EditSurvival,
    SignalSupport Feedback,
    SignalSupport TimeToFirstToken,
    string Note)
{
    /// <summary>
    /// Quality components this assistant can never populate, so they are always scored as a
    /// prior and the composite is renormalized over what is left.
    /// </summary>
    public IReadOnlyList<string> AlwaysPrior
    {
        get
        {
            var missing = new List<string>();
            // Acceptance needs either a human edit decision or a survival measurement.
            if (EditDecisions == SignalSupport.None && EditSurvival == SignalSupport.None)
                missing.Add("acceptance");
            if (Feedback == SignalSupport.None) missing.Add("feedback");
            if (TimeToFirstToken == SignalSupport.None) missing.Add("latency");
            // Friction is per-turn, and a turn is one invoke_agent trace.
            if (Traces == SignalSupport.None) missing.Add("friction");
            return missing;
        }
    }
}

/// <summary>
/// Which signals each supported assistant actually emits.
///
/// This exists because the composite renormalizes over the components that have data, which
/// means <b>an 80 from one assistant is not an 80 from another</b>: a Claude Code session is
/// scored without feedback or edit survival, so its 80 rests on a different — and smaller —
/// set of evidence than a VS Code session's 80. The project's own product review flagged this
/// (docs/architecture/PRODUCT-REVIEW-2026-08.md, finding B2) and noted that nothing in the UI
/// warned about it, while "compare assistants before you buy" is the headline use case.
///
/// It lives in code rather than in three hand-maintained tables (a doc, the in-app Docs page,
/// the README) because those drift, and a stale disclosure is worse than none: it is a claim
/// that has stopped being checked. The API serves this, the dashboard renders it, and
/// EmitterCoverageTests asserts each row against what the ingest pipeline really produces.
/// </summary>
public static class EmitterCoverage
{
    public static readonly IReadOnlyList<EmitterSignals> All =
    [
        new(EmitterKind.VSCode, "VS Code Copilot",
            Traces: SignalSupport.Full,
            Metrics: SignalSupport.Full,
            Events: SignalSupport.Full,
            EditDecisions: SignalSupport.Full,
            EditSurvival: SignalSupport.Full,
            Feedback: SignalSupport.Full,
            TimeToFirstToken: SignalSupport.Full,
            Note: "The only surface that reports every component. Scores from it rest on the "
                + "widest evidence base, which is what makes them the awkward comparison point."),

        new(EmitterKind.CLI, "Copilot CLI",
            Traces: SignalSupport.Full,
            Metrics: SignalSupport.Full,
            Events: SignalSupport.Full,
            EditDecisions: SignalSupport.None,
            EditSurvival: SignalSupport.None,
            Feedback: SignalSupport.None,
            TimeToFirstToken: SignalSupport.Full,
            Note: "No editor UI, so no accept/reject and no thumbs. Acceptance and feedback are "
                + "always priors; the composite is renormalized over the rest."),

        new(EmitterKind.ClaudeCode, "Claude Code",
            Traces: SignalSupport.Conditional,
            Metrics: SignalSupport.Full,
            Events: SignalSupport.Full,
            EditDecisions: SignalSupport.Full,
            EditSurvival: SignalSupport.None,
            Feedback: SignalSupport.None,
            TimeToFirstToken: SignalSupport.Conditional,
            Note: "Speaks claude_code.* metrics and log events; spans only on the beta trace "
                + "channel, which is also the only source of TTFT. Edit decisions come from "
                + "tool_decision events — permission-mode auto-accepts are excluded from "
                + "acceptance. No survival signal and no thumbs."),

        new(EmitterKind.Cowork, "Claude Cowork",
            Traces: SignalSupport.Conditional,
            Metrics: SignalSupport.Full,
            Events: SignalSupport.Full,
            EditDecisions: SignalSupport.Full,
            EditSurvival: SignalSupport.None,
            Feedback: SignalSupport.None,
            TimeToFirstToken: SignalSupport.Conditional,
            Note: "Same dialect as Claude Code, configured in the desktop app's settings UI. "
                + "Wants the full /v1/logs path."),

        new(EmitterKind.Cursor, "Cursor",
            Traces: SignalSupport.None,
            Metrics: SignalSupport.Conditional,
            Events: SignalSupport.Conditional,
            EditDecisions: SignalSupport.None,
            EditSurvival: SignalSupport.None,
            Feedback: SignalSupport.None,
            TimeToFirstToken: SignalSupport.None,
            Note: "UNVERIFIED — not a supported assistant (ADR-002). OTel export is Enterprise-plan "
                + "only and sends metrics and logs but no traces, so turn-level friction analysis "
                + "cannot run at all. What exists is a service.name match plus a namespace rename, "
                + "with no captured fixtures and no payload from a real Cursor session ever tested — "
                + "resolved as demote rather than implement in ADR-002 (#93)."),
    ];

    public static EmitterSignals? For(EmitterKind emitter) =>
        All.FirstOrDefault(e => e.Emitter == emitter);

    /// <summary>
    /// True when a set of sessions spans assistants whose scores rest on different evidence.
    /// Sessions from one assistant are comparable; a mixed list needs the caveat, because the
    /// obvious reading of two numbers side by side is exactly the wrong one.
    /// </summary>
    public static bool NeedsComparabilityCaveat(IEnumerable<EmitterKind> emitters)
    {
        var bases = emitters
            .Where(e => e != EmitterKind.Unknown)
            .Select(e => For(e) is { } signals ? string.Join(",", signals.AlwaysPrior) : "?")
            .Distinct()
            .ToList();
        return bases.Count > 1;
    }
}
