using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Api;

/// <summary>One row of a cohort rollup: what a slice of the fleet cost and how well it went.</summary>
/// <param name="Dimension">Which axis this row groups by (repository, assistant, model, kind).</param>
/// <param name="Value">The value on that axis.</param>
public sealed record CohortRow(
    string Dimension, string Value,
    int Sessions, int Subjects,
    long InputTokens, long OutputTokens, long CacheReadTokens,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors, int Turns,
    int EditsAccepted, int EditsRejected,
    double AvgQualityScore, double AvgConfidence,
    double ErrorRate);

/// <summary>Rollups across every axis at once, so one request answers "where is the spend going".</summary>
public sealed record CohortReport(
    DateTimeOffset? Since, DateTimeOffset? Until,
    int Sessions,
    List<CohortRow> ByRepository,
    List<CohortRow> ByAssistant,
    List<CohortRow> ByModel,
    List<CohortRow> ByKind);

/// <summary>One metric measured in two windows, with the movement between them.</summary>
public sealed record MetricDelta(string Metric, double Baseline, double Current, double Delta, double? PercentChange)
{
    public static MetricDelta Of(string metric, double baseline, double current) =>
        new(metric, Math.Round(baseline, 4), Math.Round(current, 4), Math.Round(current - baseline, 4),
            // A percentage change from zero is not "infinite improvement", it is undefined —
            // and rendering ∞ next to a rollout decision is how a chart lies.
            baseline == 0 ? null : Math.Round((current - baseline) / Math.Abs(baseline) * 100, 1));
}

/// <summary>
/// Before/after for one cohort: the head-to-head evaluation this product exists to support.
///
/// Two windows, the same filter, the same metrics. That is what "did the model upgrade help"
/// actually is — and without it, a team lead comparing assistants is reading two dashboards
/// side by side and doing the subtraction in their head.
/// </summary>
public sealed record ComparisonReport(
    string Cohort,
    DateTimeOffset? BaselineSince, DateTimeOffset? BaselineUntil, int BaselineSessions,
    DateTimeOffset? CurrentSince, DateTimeOffset? CurrentUntil, int CurrentSessions,
    List<MetricDelta> Deltas,
    List<string> Caveats);

public static class Cohorts
{
    /// <summary>
    /// A group needs this many sessions before its averages are reported as a number rather
    /// than as a count. Three is not a statistical threshold — it is the point below which an
    /// "average quality" is one session wearing a mean's clothes, and a rollout decision made
    /// on it is a coin flip with a decimal point.
    /// </summary>
    public const int MinSessionsForAverages = 3;

    public static CohortReport Build(IReadOnlyCollection<CopilotSession> sessions, QualityEngine quality,
        DateTimeOffset? since, DateTimeOffset? until)
    {
        // Score once. Each Evaluate walks the session's distributions, and four rollups over
        // the same population would otherwise re-score every session four times.
        var scored = sessions
            .Where(s => !SessionClassifier.IsInternal(s.Kind))
            .Select(s => (Session: s, Report: quality.Evaluate(s)))
            .ToList();

        return new CohortReport(
            since, until, scored.Count,
            Group(scored, "repository", s => s.Repository ?? "(none)"),
            Group(scored, "assistant", s => s.EmitterKind.ToString()),
            GroupByModel(scored),
            Group(scored, "kind", s => s.Kind.ToString()));
    }

    private static List<CohortRow> Group(
        List<(CopilotSession Session, QualityReport Report)> scored,
        string dimension, Func<CopilotSession, string> key) =>
        scored.GroupBy(x => key(x.Session), StringComparer.OrdinalIgnoreCase)
              .Select(g => Row(dimension, g.Key, g.ToList()))
              .OrderByDescending(r => r.InputTokens + r.OutputTokens)
              .ToList();

    /// <summary>
    /// Models are the one axis where a session belongs to several groups at once: a session
    /// that called two models is real work on both. It is therefore counted in both rows, and
    /// the model rows deliberately do not sum to the session total — stated here because a
    /// reader who assumes they do will conclude the numbers are broken.
    /// </summary>
    private static List<CohortRow> GroupByModel(List<(CopilotSession Session, QualityReport Report)> scored) =>
        scored.SelectMany(x => x.Session.ModelCalls.Keys.DefaultIfEmpty("(unknown)").Select(m => (Model: m, Item: x)))
              .GroupBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
              .Select(g => Row("model", g.Key, g.Select(x => x.Item).ToList()))
              .OrderByDescending(r => r.InputTokens + r.OutputTokens)
              .ToList();

    private static CohortRow Row(string dimension, string value,
        List<(CopilotSession Session, QualityReport Report)> group)
    {
        var calls = group.Sum(x => x.Session.ChatCalls + x.Session.ToolCalls);
        var errors = group.Sum(x => x.Session.ChatErrors + x.Session.ToolErrors);
        var enough = group.Count >= MinSessionsForAverages;

        return new CohortRow(
            dimension, value,
            group.Count,
            // Distinct origins, so a "repository" row that is really one developer's week is
            // visible as such rather than reading like a team signal.
            group.Select(x => x.Session.SubjectId ?? $"unknown:{x.Session.Id}")
                 .Distinct(StringComparer.Ordinal).Count(),
            group.Sum(x => x.Session.InputTokens),
            group.Sum(x => x.Session.OutputTokens),
            group.Sum(x => x.Session.CacheReadTokens),
            group.Sum(x => x.Session.ChatCalls),
            group.Sum(x => x.Session.ChatErrors),
            group.Sum(x => x.Session.ToolCalls),
            group.Sum(x => x.Session.ToolErrors),
            group.Sum(x => x.Session.Turns),
            group.Sum(x => x.Session.EditsAccepted),
            group.Sum(x => x.Session.EditsRejected),
            enough ? Math.Round(group.Average(x => x.Report.Score), 1) : 0,
            enough ? Math.Round(group.Average(x => x.Report.Confidence), 2) : 0,
            calls == 0 ? 0 : Math.Round((double)errors / calls, 4));
    }

    /// <summary>
    /// Compares one cohort across two windows.
    ///
    /// The caveats matter as much as the numbers: a comparison run over four sessions is not
    /// evidence, and a comparison whose windows are wildly different lengths is measuring the
    /// window rather than the change. Both are reported rather than silently folded into a
    /// confident-looking delta.
    /// </summary>
    public static ComparisonReport Compare(
        string cohort,
        IReadOnlyCollection<CopilotSession> baseline, DateTimeOffset? baselineSince, DateTimeOffset? baselineUntil,
        IReadOnlyCollection<CopilotSession> current, DateTimeOffset? currentSince, DateTimeOffset? currentUntil,
        QualityEngine quality)
    {
        var b = baseline.Where(s => !SessionClassifier.IsInternal(s.Kind)).ToList();
        var c = current.Where(s => !SessionClassifier.IsInternal(s.Kind)).ToList();

        var deltas = new List<MetricDelta>
        {
            MetricDelta.Of("quality score", Mean(b, quality), Mean(c, quality)),
            MetricDelta.Of("error rate", ErrorRate(b), ErrorRate(c)),
            MetricDelta.Of("tokens per session", PerSession(b, s => s.InputTokens + s.OutputTokens),
                                                 PerSession(c, s => s.InputTokens + s.OutputTokens)),
            MetricDelta.Of("turns per session", PerSession(b, s => s.Turns), PerSession(c, s => s.Turns)),
            MetricDelta.Of("tool calls per session", PerSession(b, s => s.ToolCalls), PerSession(c, s => s.ToolCalls)),
            MetricDelta.Of("edit acceptance rate", Acceptance(b), Acceptance(c)),
            MetricDelta.Of("sessions", b.Count, c.Count),
        };

        var caveats = new List<string>();
        if (b.Count < MinSessionsForAverages || c.Count < MinSessionsForAverages)
            caveats.Add($"One or both windows hold fewer than {MinSessionsForAverages} sessions " +
                        $"(baseline {b.Count}, current {c.Count}). Read the deltas as anecdote, not as a result.");
        if (b.Count > 0 && c.Count > 0 && WindowLength(baselineSince, baselineUntil) is { } bl
            && WindowLength(currentSince, currentUntil) is { } cl
            && Math.Max(bl.TotalHours, cl.TotalHours) > 2 * Math.Min(bl.TotalHours, cl.TotalHours))
            caveats.Add("The two windows differ in length by more than 2×, so per-window totals " +
                        "(sessions) compare the windows rather than the change. Per-session metrics still hold.");

        return new ComparisonReport(cohort,
            baselineSince, baselineUntil, b.Count,
            currentSince, currentUntil, c.Count,
            deltas, caveats);
    }

    private static TimeSpan? WindowLength(DateTimeOffset? since, DateTimeOffset? until) =>
        since is { } s ? (until ?? DateTimeOffset.UtcNow) - s : null;

    private static double Mean(List<CopilotSession> sessions, QualityEngine quality) =>
        sessions.Count == 0 ? 0 : sessions.Average(s => quality.Evaluate(s).Score);

    private static double PerSession(List<CopilotSession> sessions, Func<CopilotSession, double> value) =>
        sessions.Count == 0 ? 0 : sessions.Average(value);

    private static double ErrorRate(List<CopilotSession> sessions)
    {
        var calls = sessions.Sum(s => s.ChatCalls + s.ToolCalls);
        return calls == 0 ? 0 : (double)sessions.Sum(s => s.ChatErrors + s.ToolErrors) / calls;
    }

    private static double Acceptance(List<CopilotSession> sessions)
    {
        // Auto-accepts are excluded on purpose: counting them would let a permission-mode
        // change look like an improvement in how good the suggestions are.
        var decided = sessions.Sum(s => s.EditsAccepted + s.EditsRejected);
        return decided == 0 ? 0 : (double)sessions.Sum(s => s.EditsAccepted) / decided;
    }
}
