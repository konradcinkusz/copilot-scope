using CopilotScope.Collector.Api;

namespace CopilotScope.Collector.Alerting;

/// <summary>One cohort whose mean score fell between two windows.</summary>
/// <param name="BasisChanged">
/// True when the cohort's confidence fell alongside its score. The composite renormalizes over
/// the components that have data, so a cohort that stopped reporting feedback or edit decisions
/// is being measured differently rather than performing worse. Reported, never alerted on as a
/// regression — sending a team to hunt a change that never happened is how an alert channel
/// gets muted.
/// </param>
public sealed record Regression(
    string Dimension, string Value,
    double BaselineScore, double CurrentScore, double Drop,
    int BaselineSessions, int CurrentSessions,
    double BaselineConfidence, double CurrentConfidence,
    bool BasisChanged)
{
    public string Headline => BasisChanged
        ? $"{Value} ({Dimension}): score {BaselineScore:0.0} → {CurrentScore:0.0}, but confidence fell " +
          $"{BaselineConfidence:0.00} → {CurrentConfidence:0.00} — the measurement basis changed, " +
          "so this is not evidence of a quality drop."
        : $"{Value} ({Dimension}): quality {BaselineScore:0.0} → {CurrentScore:0.0} " +
          $"(−{Drop:0.0} pts) over {CurrentSessions} session(s), was {BaselineSessions}.";
}

/// <summary>
/// Finds cohorts whose quality fell between two windows.
///
/// A pure function of two rollups, so the thing that decides whether to wake someone up is
/// testable without a clock, a database or a webhook.
/// </summary>
public static class RegressionDetector
{
    /// <summary>
    /// Axes worth alerting on. Session kind is excluded deliberately: "internal helper calls
    /// scored worse this week" is not a decision anyone can act on, and every alert that
    /// cannot be acted on costs the credibility of the ones that can.
    /// </summary>
    private static readonly string[] Alertable = ["repository", "assistant", "model"];

    public static List<Regression> Detect(CohortReport baseline, CohortReport current, AlertOptions options)
    {
        var previous = Index(baseline);
        var results = new List<Regression>();

        foreach (var row in Rows(current))
        {
            if (!previous.TryGetValue((row.Dimension, row.Value), out var before)) continue;

            // Both windows need enough sessions. A cohort that appeared this week has nothing
            // to regress from, and one that nearly vanished has a mean built on noise.
            if (before.Sessions < options.MinSessionsPerWindow ||
                row.Sessions < options.MinSessionsPerWindow) continue;

            var drop = before.AvgQualityScore - row.AvgQualityScore;
            if (drop < options.ScoreDropPoints) continue;

            var confidenceDrop = before.AvgConfidence - row.AvgConfidence;
            results.Add(new Regression(
                row.Dimension, row.Value,
                before.AvgQualityScore, row.AvgQualityScore, Math.Round(drop, 1),
                before.Sessions, row.Sessions,
                before.AvgConfidence, row.AvgConfidence,
                BasisChanged: confidenceDrop > options.ConfidenceDropTolerance));
        }

        // Worst first: an alert body is read from the top, and the biggest drop is the one the
        // reader should be looking at.
        return results.OrderByDescending(r => r.Drop).ToList();
    }

    private static IEnumerable<CohortRow> Rows(CohortReport report) =>
        report.ByRepository.Concat(report.ByAssistant).Concat(report.ByModel)
              .Where(r => Alertable.Contains(r.Dimension));

    private static Dictionary<(string, string), CohortRow> Index(CohortReport report)
    {
        var map = new Dictionary<(string, string), CohortRow>();
        foreach (var row in Rows(report)) map[(row.Dimension, row.Value)] = row;
        return map;
    }
}
