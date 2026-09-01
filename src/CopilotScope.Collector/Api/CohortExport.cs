using System.Globalization;
using System.Text;

namespace CopilotScope.Collector.Api;

/// <summary>
/// CSV rendering of a cohort rollup, for the spreadsheet the decision actually gets made in.
///
/// <para>Export is the feature that decides whether a tool is used after the demo: a platform
/// lead has to put these numbers in front of people who will never open the dashboard. So the
/// output is a flat table with one header — not nested JSON prettified, not a screenshot.</para>
///
/// <para><b>No individual identifiers leave here.</b> Not session ids, not subjects, not
/// branches: every row is a group. That is not a policy choice bolted on afterwards — the
/// input type is a rollup, so there is nothing individual in scope to leak. An export of
/// per-session rows would be the per-developer scoreboard this product refuses to build,
/// arriving by the back door as a .csv.</para>
/// </summary>
public static class CohortExport
{
    public static string ToCsv(CohortReport report)
    {
        var sb = new StringBuilder();

        // A leading comment line naming the window: a CSV that lands in an inbox three weeks
        // later without its window is a number nobody can check.
        sb.Append("# CopilotScope cohort export · window ")
          .Append(report.Since?.UtcDateTime.ToString("O") ?? "all history")
          .Append(" .. ")
          .Append(report.Until?.UtcDateTime.ToString("O") ?? "now")
          .Append(" · ").Append(report.Sessions).Append(" session(s)")
          .Append('\n');
        sb.Append("# Averages are reported only for groups of at least ")
          .Append(Cohorts.MinSessionsForAverages)
          .Append(" sessions; smaller groups show 0 and should be read from the counts.\n");
        sb.Append("# Model rows count a session once per model it called, so they do not sum " +
                  "to the session total.\n");

        sb.Append("dimension,value,sessions,subjects,input_tokens,output_tokens,cache_read_tokens," +
                  "chat_calls,chat_errors,tool_calls,tool_errors,turns,edits_accepted,edits_rejected," +
                  "avg_quality_score,avg_confidence,error_rate\n");

        foreach (var row in report.ByRepository
                     .Concat(report.ByAssistant)
                     .Concat(report.ByModel)
                     .Concat(report.ByKind))
            Append(sb, row);

        return sb.ToString();
    }

    public static string ToCsv(ComparisonReport report)
    {
        var sb = new StringBuilder();
        sb.Append("# CopilotScope window comparison · cohort ").Append(Comment(report.Cohort)).Append('\n');
        sb.Append("# baseline ").Append(Window(report.BaselineSince, report.BaselineUntil))
          .Append(" (").Append(report.BaselineSessions).Append(" sessions)")
          .Append(" · current ").Append(Window(report.CurrentSince, report.CurrentUntil))
          .Append(" (").Append(report.CurrentSessions).Append(" sessions)\n");
        foreach (var caveat in report.Caveats) sb.Append("# CAVEAT: ").Append(Comment(caveat)).Append('\n');

        sb.Append("metric,baseline,current,delta,percent_change\n");
        foreach (var d in report.Deltas)
            sb.Append(Csv(d.Metric)).Append(',')
              .Append(Num(d.Baseline)).Append(',')
              .Append(Num(d.Current)).Append(',')
              .Append(Num(d.Delta)).Append(',')
              .Append(d.PercentChange is { } p ? Num(p) : "").Append('\n');

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, CohortRow r) =>
        sb.Append(Csv(r.Dimension)).Append(',')
          .Append(Csv(r.Value)).Append(',')
          .Append(r.Sessions).Append(',')
          .Append(r.Subjects).Append(',')
          .Append(r.InputTokens).Append(',')
          .Append(r.OutputTokens).Append(',')
          .Append(r.CacheReadTokens).Append(',')
          .Append(r.ChatCalls).Append(',')
          .Append(r.ChatErrors).Append(',')
          .Append(r.ToolCalls).Append(',')
          .Append(r.ToolErrors).Append(',')
          .Append(r.Turns).Append(',')
          .Append(r.EditsAccepted).Append(',')
          .Append(r.EditsRejected).Append(',')
          .Append(Num(r.AvgQualityScore)).Append(',')
          .Append(Num(r.AvgConfidence)).Append(',')
          .Append(Num(r.ErrorRate)).Append('\n');

    private static string Window(DateTimeOffset? since, DateTimeOffset? until) =>
        (since?.UtcDateTime.ToString("O") ?? "all history") + " .. " + (until?.UtcDateTime.ToString("O") ?? "now");

    /// <summary>Invariant culture, always: a decimal comma would split the cell in a locale
    /// nobody testing this runs in.</summary>
    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Newlines out of a comment line, which has no quoting rules to hide behind.</summary>
    private static string Comment(string value) => value.Replace('\n', ' ').Replace('\r', ' ');

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
}
