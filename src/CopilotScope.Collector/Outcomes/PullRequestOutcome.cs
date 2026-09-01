namespace CopilotScope.Collector.Outcomes;

/// <summary>
/// What happened to the code after the session ended.
///
/// Every signal CopilotScope scores today stops at the session boundary: it can say a
/// conversation ran cleanly and its edits were accepted, and still not know whether the
/// change shipped. That is the same critique the project levels at usage counters, and it
/// is what makes the composite an opinion rather than an instrument — nothing has ever
/// checked the score against an outcome anyone cares about.
///
/// These records are the other half of that check. They are deliberately repository-level:
/// a merged pull request is a fact about a change, not about a person, and no author field
/// is stored (see the "not a developer scoreboard" non-goal).
/// </summary>
public sealed record PullRequestOutcome(
    /// <summary>Normalized "owner/repo".</summary>
    string Repository,
    int Number,
    /// <summary>Head branch — the join key against a session's branch.</summary>
    string Branch,
    string Title,
    DateTimeOffset OpenedAt,
    DateTimeOffset? MergedAt,
    DateTimeOffset? ClosedAt,
    /// <summary>First review submitted, for time-to-first-review.</summary>
    DateTimeOffset? FirstReviewAt,
    int Additions,
    int Deletions,
    int ChangedFiles,
    /// <summary>True once a later commit is seen reverting this PR's merge.</summary>
    bool Reverted = false,
    DateTimeOffset? RevertedAt = null)
{
    public PullRequestState State =>
        Reverted ? PullRequestState.Reverted
        : MergedAt is not null ? PullRequestState.Merged
        : ClosedAt is not null ? PullRequestState.Closed
        : PullRequestState.Open;

    /// <summary>How long the change waited for its first review — the review-load signal
    /// the 2026 benchmarks track for AI-assisted PRs.</summary>
    public TimeSpan? TimeToFirstReview => FirstReviewAt is { } r ? r - OpenedAt : null;

    public TimeSpan? TimeToMerge => MergedAt is { } m ? m - OpenedAt : null;
}

public enum PullRequestState { Open, Merged, Closed, Reverted }

/// <summary>
/// A session joined to a pull request, and how much that join can be trusted.
///
/// The link is a heuristic — telemetry carries a repository and branch, not a commit — so
/// the confidence travels with it rather than being quietly dropped. A correlation study
/// that treats a Low link as if it were Attributed would be measuring its own join errors.
/// </summary>
public sealed record OutcomeLink(
    PullRequestOutcome PullRequest,
    LinkConfidence Confidence,
    string Reason);

public enum LinkConfidence
{
    /// <summary>Repository matches; branch or timing does not pin it down.</summary>
    Low,

    /// <summary>Repository and branch match, but the timing is loose.</summary>
    Medium,

    /// <summary>Repository and branch match and the PR was opened in the session's window.</summary>
    High,

    /// <summary>An external provenance source (e.g. git-ai notes) names the session id.</summary>
    Attributed
}
