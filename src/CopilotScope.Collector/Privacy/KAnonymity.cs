using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Privacy;

/// <summary>Why a view was allowed or withheld, and what would unblock it.</summary>
public sealed record AnonymityVerdict(bool Allowed, int Subjects, int Required, string Reason)
{
    public static AnonymityVerdict Off { get; } =
        new(true, 0, 0, "Privacy mode is off; no aggregation floor applies.");
}

/// <summary>
/// The aggregation floor.
///
/// Pseudonymization alone does not stop individual monitoring: filter a "team" view down to
/// one repository worked on by one person and the tokens become a name again. So privacy
/// mode also refuses to render any view that covers fewer than k distinct subjects. This is
/// the control that makes the README's "not a per-developer scoreboard" true of the
/// software rather than of its documentation — the view a manager would need in order to
/// build one is the view that does not render.
///
/// Counting distinct *subjects*, not sessions, is the point. Fifty sessions from one
/// developer are one person's week, and reporting on them is exactly what a works agreement
/// prohibits; five sessions from five developers are a team signal.
/// </summary>
public sealed class PrivacyGuard(PrivacyOptions options)
{
    public bool Enabled => options.Enabled;
    public int MinimumGroupSize => options.MinimumGroupSize;
    public bool SessionDetailSuppressed => options.Enabled && options.SuppressSessionDetail;

    /// <summary>
    /// Does this set of sessions cover enough distinct subjects to be shown?
    ///
    /// Sessions whose subject could not be determined each count as their own subject: an
    /// unknown identity is not evidence that the set is diverse, and treating them as one
    /// shared "unknown" bucket would be the safer-sounding choice that is actually wrong in
    /// the other direction — it would suppress genuinely broad views on a deployment whose
    /// emitters happen not to send host attributes. Fail toward the honest count, and let
    /// SessionStore.HostlessSignals tell the operator to fix the emitters.
    /// </summary>
    public AnonymityVerdict Evaluate(IEnumerable<CopilotSession> sessions)
    {
        if (!options.Enabled) return AnonymityVerdict.Off;

        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sessions)
            subjects.Add(s.SubjectId ?? $"unknown:{s.Id}");

        return EvaluateSubjects(subjects);
    }

    /// <summary>Same question for a set already reduced to subject ids.</summary>
    public AnonymityVerdict EvaluateSubjects(IReadOnlyCollection<string> subjects)
    {
        if (!options.Enabled) return AnonymityVerdict.Off;
        var required = Math.Max(1, options.MinimumGroupSize);
        return subjects.Count >= required
            ? new AnonymityVerdict(true, subjects.Count, required, $"{subjects.Count} distinct subjects (floor {required}).")
            : new AnonymityVerdict(false, subjects.Count, required,
                $"Withheld: this view covers {subjects.Count} distinct " +
                $"{(subjects.Count == 1 ? "subject" : "subjects")}, below the configured k-anonymity " +
                $"floor of {required}. Widen the time range or drop the filters.");
    }
}
