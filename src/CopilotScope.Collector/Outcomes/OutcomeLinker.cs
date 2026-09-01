using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Outcomes;

/// <summary>
/// Joins sessions to the pull requests they plausibly produced.
///
/// Telemetry gives a repository and a branch, never a commit, so this is a heuristic and
/// says so: every link carries a <see cref="LinkConfidence"/> and a human-readable reason.
/// The alternative — silently presenting a guess as a fact — would put join errors straight
/// into any correlation between score and outcome, which is exactly the measurement this is
/// meant to make trustworthy.
/// </summary>
public static class OutcomeLinker
{
    /// <summary>
    /// How long after a session ends a pull request can still be attributed to it. Work is
    /// commonly opened as a PR the following morning; beyond a day the connection is guesswork.
    /// </summary>
    public static readonly TimeSpan OpenWindow = TimeSpan.FromHours(24);

    /// <summary>Tolerance for a PR opened just *before* the last telemetry arrived — a
    /// developer opens the PR and keeps talking to the assistant about it.</summary>
    private static readonly TimeSpan PreOpenGrace = TimeSpan.FromHours(2);

    /// <summary>
    /// Reduces a repository identifier to "owner/repo". Sessions report whatever the client
    /// had — an SSH remote, an HTTPS URL, a bare path — so the join has to normalize both
    /// sides or it silently never matches.
    /// </summary>
    public static string? NormalizeRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return null;
        var value = repository.Trim();

        // git@github.com:owner/repo.git → owner/repo
        var colon = value.LastIndexOf(':');
        if (value.StartsWith("git@", StringComparison.OrdinalIgnoreCase) && colon > 0)
            value = value[(colon + 1)..];
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            value = uri.AbsolutePath;

        value = value.Trim('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];

        // Keep only the trailing owner/repo, so a self-hosted path prefix does not break the join.
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[^2]}/{parts[^1]}".ToLowerInvariant()
            : parts.Length == 1 ? parts[0].ToLowerInvariant() : null;
    }

    /// <summary>
    /// Ranks the candidate pull requests for one session, best match first. Candidates are
    /// expected to be pre-filtered to the session's repository.
    /// </summary>
    public static List<OutcomeLink> Link(CopilotSession session, IEnumerable<PullRequestOutcome> candidates)
    {
        var (repository, branch, firstSeen, lastSeen) = session.Snapshot(s =>
            (NormalizeRepository(s.Repository), s.Branch, s.FirstSeen, s.LastSeen));
        if (repository is null) return [];

        var links = new List<OutcomeLink>();
        foreach (var pr in candidates)
        {
            if (!string.Equals(NormalizeRepository(pr.Repository), repository, StringComparison.Ordinal)) continue;

            var branchMatches = branch is not null
                && string.Equals(pr.Branch, branch, StringComparison.OrdinalIgnoreCase);
            var inWindow = pr.OpenedAt >= firstSeen - PreOpenGrace && pr.OpenedAt <= lastSeen + OpenWindow;

            var (confidence, reason) = (branchMatches, inWindow) switch
            {
                (true, true) => (LinkConfidence.High,
                    $"branch '{pr.Branch}' matches and the pull request opened within the session window"),
                (true, false) => (LinkConfidence.Medium,
                    $"branch '{pr.Branch}' matches but the pull request opened outside the session window"),
                (false, true) => (LinkConfidence.Low,
                    "same repository and timing, but the session reports no matching branch"),
                _ => (LinkConfidence.Low, "same repository only")
            };

            links.Add(new OutcomeLink(pr, confidence, reason));
        }

        // Best confidence first, then closest in time — the nearest plausible PR is the
        // likeliest one when a branch produced several.
        return links
            .OrderByDescending(l => l.Confidence)
            .ThenBy(l => Math.Abs((l.PullRequest.OpenedAt - lastSeen).Ticks))
            .ToList();
    }

    /// <summary>
    /// Links usable as evidence in a correlation study. Low-confidence links are shown in the
    /// UI (an operator can judge them) but excluded here, because a study that treats a
    /// repository-only guess as an outcome is measuring its own join errors.
    /// </summary>
    public static IEnumerable<OutcomeLink> Confident(IEnumerable<OutcomeLink> links) =>
        links.Where(l => l.Confidence >= LinkConfidence.Medium);
}
