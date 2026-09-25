using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Review;

/// <summary>Whether the base has enough new material to be worth reviewing, and the numbers behind that.</summary>
public sealed record ReviewReadinessReport(
    bool Ready,
    int Eligible,
    int MinSessions,
    int WindowDays,
    DateTimeOffset Since,
    DateTimeOffset Until,
    DateTimeOffset? CoveredUntil,
    string Note);

/// <summary>
/// The readiness rule: one predicate, computed once, over the same population the pack is built
/// from. A caller that counted sessions through <c>/api/sessions</c> would count seeded, internal
/// and empty sessions too, and would read a different number than the pack contains.
///
/// A pure function of the sessions and the clock it is handed, like the scoring it sits beside.
/// </summary>
public static class ReviewReadiness
{
    /// <summary>Ids the seeder owns. Fabricated sessions prove the UI works; reviewing them would
    /// produce advice about a demo.</summary>
    public const string SyntheticPrefix = "seed-";

    /// <summary>
    /// A session the pack counts: a real conversation, not a helper call or a seeded one, that
    /// contains at least one chat call. Sessions with no calls are unscored and say nothing.
    /// </summary>
    public static bool IsEligible(CopilotSession session) =>
        !SessionClassifier.IsInternal(session.Kind)
        && !session.Id.StartsWith(SyntheticPrefix, StringComparison.Ordinal)
        && session.ChatCalls > 0;

    /// <summary>
    /// Counts the eligible sessions last active after <paramref name="coveredUntil"/> — the point
    /// the previous review covered — within the last <see cref="ReviewOptions.WindowDays"/>.
    /// </summary>
    public static ReviewReadinessReport Evaluate(IEnumerable<CopilotSession> window, ReviewOptions options,
        DateTimeOffset? coveredUntil, DateTimeOffset now)
    {
        var windowDays = Math.Max(1, options.WindowDays);
        var since = now.AddDays(-windowDays);
        if (coveredUntil is { } covered && covered > since) since = covered;

        var eligible = window.Count(s => IsEligible(s) && s.LastSeen > since && s.LastSeen <= now);
        var minSessions = Math.Max(1, options.MinSessions);
        var ready = eligible >= minSessions;

        var note = ready
            ? $"{eligible} eligible session(s) since {since:yyyy-MM-dd}: enough to review."
            : $"{eligible} of {minSessions} eligible session(s) since {since:yyyy-MM-dd}; not enough yet to say anything a dashboard does not.";

        return new ReviewReadinessReport(ready, eligible, minSessions, windowDays, since, now, coveredUntil, note);
    }
}
