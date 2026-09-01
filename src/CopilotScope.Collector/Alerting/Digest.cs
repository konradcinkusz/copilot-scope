using CopilotScope.Collector.Api;

namespace CopilotScope.Collector.Alerting;

/// <summary>One line of the digest: an axis value and how its week went.</summary>
public sealed record DigestLine(string Dimension, string Value, int Sessions, long Tokens,
    double AvgQualityScore, double ErrorRate);

/// <summary>
/// The aggregate week, as the artefact a lead forwards instead of a dashboard link.
///
/// Aggregate-only by construction: it is built from cohort rollups, so there is no per-session
/// or per-developer row to accidentally include. That is not a filter applied at the end — the
/// input type has no individual in it.
/// </summary>
public sealed record DigestReport(
    DateTimeOffset Since, DateTimeOffset Until,
    int Sessions, long TotalTokens, double AvgQualityScore, double ErrorRate,
    List<DigestLine> ByAssistant, List<DigestLine> ByModel, List<DigestLine> ByRepository,
    List<Regression> Regressions,
    List<string> Notes);

public static class Digest
{
    /// <summary>How many rows per axis. A digest that lists forty repositories is a dashboard
    /// with worse formatting; the point is the handful worth looking at.</summary>
    private const int TopN = 5;

    public static DigestReport Build(CohortReport current, CohortReport baseline,
        List<Regression> regressions, DateTimeOffset since, DateTimeOffset until)
    {
        var all = current.ByAssistant;
        var sessions = current.Sessions;
        var tokens = all.Sum(r => r.InputTokens + r.OutputTokens);
        var calls = all.Sum(r => r.ChatCalls + r.ToolCalls);
        var errors = all.Sum(r => r.ChatErrors + r.ToolErrors);

        var notes = new List<string>();
        if (sessions == 0)
            notes.Add("No sessions in this window — either nothing ran, or the emitters stopped reporting.");
        else if (sessions < Cohorts.MinSessionsForAverages)
            notes.Add($"Only {sessions} session(s) this window; the averages below are anecdote.");
        if (baseline.Sessions == 0 && sessions > 0)
            notes.Add("No previous window to compare against, so no regressions could be detected yet.");
        if (regressions.Any(r => r.BasisChanged))
            notes.Add("One or more score drops came with a confidence drop: those cohorts are being " +
                      "measured on fewer signals than before, which is a reporting change rather than " +
                      "a quality change.");

        return new DigestReport(since, until,
            sessions, tokens,
            // The weighted mean, not the mean of the per-assistant means: an assistant with two
            // sessions must not weigh the same as one with two hundred.
            Weighted(all, r => r.AvgQualityScore),
            calls == 0 ? 0 : Math.Round((double)errors / calls, 4),
            Top(current.ByAssistant), Top(current.ByModel), Top(current.ByRepository),
            regressions, notes);
    }

    private static double Weighted(List<CohortRow> rows, Func<CohortRow, double> value)
    {
        // Only rows that actually have an average contribute — a group below the reporting
        // floor carries 0, and averaging that in would drag the headline number down for a
        // reason that has nothing to do with quality.
        var scored = rows.Where(r => r.Sessions >= Cohorts.MinSessionsForAverages).ToList();
        var total = scored.Sum(r => r.Sessions);
        return total == 0 ? 0 : Math.Round(scored.Sum(r => value(r) * r.Sessions) / total, 1);
    }

    private static List<DigestLine> Top(List<CohortRow> rows) =>
        rows.OrderByDescending(r => r.Sessions).Take(TopN)
            .Select(r => new DigestLine(r.Dimension, r.Value, r.Sessions,
                r.InputTokens + r.OutputTokens, r.AvgQualityScore, r.ErrorRate))
            .ToList();

    /// <summary>
    /// Plain-text rendering, for a chat webhook. Written to be readable in a Slack message
    /// rather than to be complete — a digest nobody reads to the end is a digest that failed,
    /// and the API serves the full document for anyone who wants it.
    /// </summary>
    public static string ToText(DigestReport d)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("*CopilotScope weekly digest* — ")
          .Append(d.Since.UtcDateTime.ToString("yyyy-MM-dd")).Append(" to ")
          .Append(d.Until.UtcDateTime.ToString("yyyy-MM-dd")).Append('\n');
        sb.Append($"{d.Sessions} session(s) · {Tokens(d.TotalTokens)} tokens · " +
                  $"mean quality {d.AvgQualityScore:0.0} · error rate {d.ErrorRate:P1}\n");

        if (d.Regressions.Count > 0)
        {
            sb.Append("\n*Regressions*\n");
            foreach (var r in d.Regressions.Take(5)) sb.Append("• ").Append(r.Headline).Append('\n');
        }

        Section(sb, "By assistant", d.ByAssistant);
        Section(sb, "By model", d.ByModel);
        Section(sb, "By repository", d.ByRepository);

        foreach (var note in d.Notes) sb.Append("\n_").Append(note).Append('_');
        return sb.ToString();
    }

    private static void Section(System.Text.StringBuilder sb, string title, List<DigestLine> lines)
    {
        if (lines.Count == 0) return;
        sb.Append('\n').Append('*').Append(title).Append("*\n");
        foreach (var l in lines)
            sb.Append("• ").Append(l.Value).Append(" — ").Append(l.Sessions).Append(" session(s), ")
              .Append(Tokens(l.Tokens)).Append(" tokens")
              // A group below the floor reports no average, and says so rather than showing 0.0.
              .Append(l.Sessions >= Cohorts.MinSessionsForAverages ? $", quality {l.AvgQualityScore:0.0}" : ", quality n/a")
              .Append('\n');
    }

    private static string Tokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.00") + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.0") + "k",
        _ => n.ToString()
    };
}
