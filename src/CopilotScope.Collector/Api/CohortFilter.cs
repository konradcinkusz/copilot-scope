using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Api;

/// <summary>
/// The cohort a view is about: a repository, an assistant, a model, a session kind, a grade.
///
/// <para><b>There is deliberately no developer dimension, and there never will be.</b> Every
/// other axis here describes the *tooling* — which assistant, which model, which repository —
/// and answers the question this product exists for: is the tool we are paying for working.
/// A developer axis would answer a different question, and adding one is the single change
/// that would turn a fleet-evaluation tool into a surveillance tool. The type is closed over
/// its fields for exactly that reason: a filter that cannot express "one person" cannot be
/// composed into a view that reports on one.</para>
///
/// <para>Every field maps to a column or an indexed jsonb lookup, so filtering happens in SQL
/// rather than after the page is fetched. That is not an optimization: filtering after LIMIT
/// under-fills every page and, past the first few, returns nothing at all — a bug this
/// codebase has already shipped once.</para>
/// </summary>
public sealed record CohortFilter(
    string? Repository = null,
    EmitterKind? Emitter = null,
    string? Model = null,
    SessionKind? Kind = null,
    string? Grade = null)
{
    public static CohortFilter None { get; } = new();

    public bool IsEmpty =>
        Repository is null && Emitter is null && Model is null && Kind is null && Grade is null;

    /// <summary>Parses the query-string form. Unparseable values are dropped rather than
    /// rejected: a stale bookmark with a renamed assistant should widen to everything, not
    /// 400 — but an unparseable value is never silently treated as a match either.</summary>
    public static CohortFilter From(string? repository, string? emitter, string? model,
        string? kind, string? grade) => new(
            string.IsNullOrWhiteSpace(repository) ? null : repository.Trim(),
            Enum.TryParse<EmitterKind>(emitter, ignoreCase: true, out var e) ? e : null,
            string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Enum.TryParse<SessionKind>(kind, ignoreCase: true, out var k) ? k : null,
            string.IsNullOrWhiteSpace(grade) ? null : grade.Trim());

    /// <summary>
    /// In-memory form, for the live sessions layered over the stored page and for deployments
    /// with no Postgres at all. Kept in lockstep with the SQL predicate by tests, because two
    /// filters that disagree is worse than one that is slow.
    ///
    /// Grade is excluded here: it is not a property of the session but of scoring it, and the
    /// candidate pass runs before anything has a <c>QualityEngine</c> in hand. Callers that
    /// filter on grade apply <see cref="MatchesGrade"/> once they have the score.
    /// </summary>
    public bool MatchesExceptGrade(CopilotSession session)
    {
        if (Repository is not null &&
            !string.Equals(session.Repository, Repository, StringComparison.OrdinalIgnoreCase)) return false;
        if (Emitter is { } emitter && session.EmitterKind != emitter) return false;
        if (Kind is { } kind && session.Kind != kind) return false;
        if (Model is not null && !session.ModelCalls.Keys.Any(
                m => string.Equals(m, Model, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>The grade half of the filter, applied once the session has been scored.</summary>
    public bool MatchesGrade(string grade) =>
        Grade is null || string.Equals(grade, Grade, StringComparison.OrdinalIgnoreCase);

    /// <summary>The whole filter, for a caller that already has the score.</summary>
    public bool Matches(CopilotSession session, string grade) =>
        MatchesExceptGrade(session) && MatchesGrade(grade);

    /// <summary>One line naming the cohort, for an export header and the audit record.</summary>
    public string Describe()
    {
        if (IsEmpty) return "all sessions";
        var parts = new List<string>();
        if (Repository is not null) parts.Add($"repository={Repository}");
        if (Emitter is { } e) parts.Add($"assistant={e}");
        if (Model is not null) parts.Add($"model={Model}");
        if (Kind is { } k) parts.Add($"kind={k}");
        if (Grade is not null) parts.Add($"grade={Grade}");
        return string.Join(" ", parts);
    }
}
