namespace CopilotScope.Collector.Quality;

/// <summary>
/// Workflow-friction signal detection, bound from <c>CopilotScope:WorkflowFriction</c>.
///
/// This analyzer reads captured prompt text for *repair markers* — rephrasing the same ask,
/// short corrective replies, negative-feedback phrases. Those are observed workflow events,
/// and naming them as such is not cosmetic: EU AI Act Art. 5(1)(f) prohibits emotion
/// inference in the workplace outright, with fines to 7% of global turnover, so a feature
/// that claims to measure how a developer *feels* is a feature a DPO has to block. One that
/// counts how often the developer had to ask again is one they can approve. See
/// docs/WORKFLOW_FRICTION.md.
///
/// Off by default, and that is the compliance posture rather than a taste: the analyzer only
/// runs on captured prompt content, which most deployments neither collect nor should. Turning
/// it on is a decision an operator makes deliberately, in writing, with the works agreement in
/// front of them.
/// </summary>
public sealed class WorkflowFrictionOptions
{
    /// <summary>Run the analyzer at all. Off by default — no report is produced, and the
    /// dashboard shows no friction section, until an operator sets this.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Include per-message previews — a timestamp, a score and a quote from the developer's
    /// own prompt — in the session report. A second, separate opt-in, because this is the part
    /// that reproduces someone's words next to a number about them. The aggregate rate answers
    /// "is our tooling making people repeat themselves"; the quotes answer "what did Konrad
    /// type at 14:32", which is a different question with a different audience.
    /// </summary>
    public bool IncludeFlaggedMessages { get; set; }

    /// <summary>Per-message score at or above which a message counts as carrying a repair
    /// marker. Exposed because the lexicon is language- and team-specific, and a threshold
    /// nobody can tune is a threshold nobody can validate.</summary>
    public double FlagThreshold { get; set; } = 0.3;
}
