using System.Text;
using CopilotScope.Collector.Api;

namespace CopilotScope.Collector.Review;

/// <summary>
/// The pack as one Markdown document, for a reader that is a model.
///
/// <para>Sized for a context window rather than for storage: the JSON form of the pack is the
/// reference, and this is the part a model is told to read whole. It opens with the rules a
/// score is read by, because the reader of this document is the thing that will generate the
/// answer, and the ways a quality score gets quoted badly are predictable.</para>
/// </summary>
public static class ReviewPackMarkdown
{
    /// <summary>Bytes the document may reach before it is rendered compact — roughly twelve
    /// thousand tokens, so a model reading it whole still has room to look sessions up.</summary>
    public const int MaxBytes = 48 * 1024;

    /// <summary>Patterns and cohort rows per dimension kept in the compact rendering.</summary>
    private const int CompactPatterns = 10;
    private const int CompactRowsPerDimension = 5;

    public static string Render(ReviewPackReport pack)
    {
        var full = Render(pack, compact: false);
        return Encoding.UTF8.GetByteCount(full) <= MaxBytes ? full : Render(pack, compact: true);
    }

    private static string Render(ReviewPackReport pack, bool compact)
    {
        var sb = new StringBuilder();
        var scope = pack.Scope;

        sb.Append("# CopilotScope review pack\n\n");
        sb.Append($"- Window: {Date(scope.Since)} → {Date(scope.Until)} UTC · baseline {Date(scope.BaselineSince)} → {Date(scope.Since)}\n");
        sb.Append($"- Sessions: {scope.Sessions} (baseline {scope.BaselineSessions}) · tier `{scope.Tier}` · fingerprint `{scope.Fingerprint}`\n");
        sb.Append($"- Collector {scope.CollectorVersion} · pack format v{scope.PackVersion}\n");
        if (compact)
            sb.Append($"- Rendered compact: the full document exceeded {MaxBytes / 1024} KB. Exemplar timelines and the " +
                      "smaller patterns and cohort rows are omitted here; `format=json` carries everything.\n");
        sb.Append('\n');

        Rules(sb);
        Formula(sb, pack.Formula);
        Provenance(sb, pack.Provenance);
        Coverage(sb, pack.Coverage);
        CohortRows(sb, pack.Cohorts, compact);
        Comparison(sb, pack.Comparison);
        Regressions(sb, pack.Regressions);
        Distribution(sb, pack.Distribution);
        Patterns(sb, pack.Patterns, compact);
        Exemplars(sb, pack.Exemplars, compact);
        if (pack.Sessions is { } rows)
            sb.Append("## Session rows\n\n")
              .Append($"{rows.Count} row(s) travel in the JSON form of this pack (`format=json`): id, window, assistant, origin, ")
              .Append("mode, repository, score, confidence, grade, counts, models, and the patterns each session is evidence for.\n\n");
        Notes(sb, pack.Notes);

        return sb.ToString();
    }

    private static void Rules(StringBuilder sb)
    {
        sb.Append("## How to read this pack\n\n");
        sb.Append("1. Never quote a score without its confidence. A 90 built on four samples means less than a 70 built on forty.\n");
        sb.Append("2. The score grades a session, never a person. Do not rank, compare or name people: the pack contains no way to, ");
        sb.Append("and a review that tries has left the evidence.\n");
        sb.Append("3. Acceptance rate is not a target. It is paired with edit survival on purpose; never recommend raising it.\n");
        sb.Append("4. Compare across assistants only directionally. The coverage block says which components each assistant cannot report.\n");
        sb.Append("5. Imported sessions carry fewer signals than live ones and sit in their own strata.\n");
        sb.Append("6. Every pattern is a count within one stratum, with its threshold stated. Cite pattern ids and session ids ");
        sb.Append("from this pack; do not infer what the pack does not contain.\n");
        sb.Append("7. Workflow-friction figures are not in this pack and are not to be inferred. Nothing here measures how anyone felt.\n");
        sb.Append("8. This review is report-only. Nothing written about this pack changes a score.\n\n");
    }

    private static void Formula(StringBuilder sb, ReviewFormula f)
    {
        sb.Append("## Formula\n\n");
        sb.Append("| Profile | reliability | acceptance | friction | latency | feedback | efficiency |\n|---|---|---|---|---|---|---|\n");
        foreach (var (name, w) in f.Profiles)
            sb.Append($"| {name} | {w["reliability"]:0.00} | {w["acceptance"]:0.00} | {w["friction"]:0.00} | {w["latency"]:0.00} | {w["feedback"]:0.00} | {w["efficiency"]:0.00} |\n");
        sb.Append('\n');
        sb.Append("Grade bands (lower bound on the composite): ")
          .Append(string.Join(" · ", f.GradeBands.Select(b => $"{b.Key} ≥ {b.Value}"))).Append(".\n\n");
        foreach (var note in f.Notes) sb.Append("- ").Append(note).Append('\n');
        sb.Append('\n');
    }

    private static void Provenance(StringBuilder sb, ReviewProvenance p)
    {
        sb.Append("## Provenance\n\n");
        sb.Append("- By origin: ").Append(Counts(p.ByOrigin)).Append('\n');
        sb.Append("- By assistant: ").Append(Counts(p.ByAssistant)).Append('\n');
        sb.Append("- By mode: ").Append(Counts(p.ByMode)).Append('\n');
        sb.Append("- By stratum (origin/assistant/profile): ").Append(Counts(p.ByStratum)).Append('\n');
        sb.Append($"- Excluded before anything was counted: {p.InternalExcluded} internal helper call(s), " +
                  $"{p.SyntheticExcluded} seeded demo session(s), {p.EmptyExcluded} session(s) with no chat calls.\n\n");
    }

    private static void Coverage(StringBuilder sb, List<ReviewCoverageRow> rows)
    {
        if (rows.Count == 0) return;
        sb.Append("## Signal coverage\n\n");
        foreach (var r in rows)
            sb.Append($"- **{r.Assistant}** — always a prior: {(r.AlwaysPrior.Count == 0 ? "none" : string.Join(", ", r.AlwaysPrior))}. {r.Note}\n");
        sb.Append('\n');
    }

    private static void CohortRows(StringBuilder sb, List<ReviewCohortRow> rows, bool compact)
    {
        if (rows.Count == 0) return;
        sb.Append("## Cohorts\n\n");
        sb.Append("| Dimension | Value | Sessions | Quality | Confidence | Error rate | Tokens |\n|---|---|---|---|---|---|---|\n");
        foreach (var group in rows.GroupBy(r => r.Dimension))
            foreach (var r in compact ? group.Take(CompactRowsPerDimension) : group)
                sb.Append($"| {r.Dimension} | {r.Value} | {r.Sessions} | {Avg(r.AvgQualityScore, "0.0")} | {Avg(r.AvgConfidence, "0.00")} | {r.ErrorRate:P1} | {Tokens(r.InputTokens + r.OutputTokens)} |\n");
        sb.Append("\nModel rows count a session once per model it called, so they do not sum to the session total. " +
                  "A quality of n/a means fewer sessions than the averaging floor.\n\n");
    }

    private static void Comparison(StringBuilder sb, ComparisonReport? c)
    {
        if (c is null) return;
        sb.Append("## Before and after\n\n");
        sb.Append($"Baseline window: {c.BaselineSessions} session(s). Current window: {c.CurrentSessions} session(s).\n\n");
        sb.Append("| Metric | Baseline | Current | Delta | Change |\n|---|---|---|---|---|\n");
        foreach (var d in c.Deltas)
            sb.Append($"| {d.Metric} | {d.Baseline:0.###} | {d.Current:0.###} | {d.Delta:+0.###;-0.###;0} | {(d.PercentChange is { } p ? $"{p:+0.0;-0.0;0}%" : "n/a")} |\n");
        foreach (var caveat in c.Caveats) sb.Append("\n_").Append(caveat).Append('_');
        sb.Append("\n\n");
    }

    private static void Regressions(StringBuilder sb, List<Alerting.Regression> regressions)
    {
        sb.Append("## Regressions\n\n");
        if (regressions.Count == 0) { sb.Append("None detected between the two windows.\n\n"); return; }
        foreach (var r in regressions) sb.Append("- ").Append(r.Headline).Append('\n');
        sb.Append('\n');
    }

    private static void Distribution(StringBuilder sb, ReviewDistribution d)
    {
        sb.Append("## Distribution\n\n");
        sb.Append("- Grades: ").Append(Counts(d.Grades)).Append('\n');
        sb.Append("- Scores: ").Append(string.Join(" · ", d.Scores.Select(b => $"{b.Range}: {b.Sessions}"))).Append('\n');
        sb.Append("- Confidence: ").Append(string.Join(" · ", d.Confidence.Select(b => $"{b.Range}: {b.Sessions}"))).Append("\n\n");
    }

    private static void Patterns(StringBuilder sb, List<ReviewPattern> patterns, bool compact)
    {
        sb.Append("## Patterns\n\n");
        if (patterns.Count == 0) { sb.Append("Nothing recurred often enough to be called a pattern.\n\n"); return; }
        var shown = compact ? patterns.OrderByDescending(p => p.Support).Take(CompactPatterns).OrderBy(p => p.Id, StringComparer.Ordinal).ToList() : patterns;
        if (shown.Count < patterns.Count) sb.Append($"{patterns.Count - shown.Count} pattern(s) with less support are in the JSON form only.\n\n");
        foreach (var p in shown)
        {
            sb.Append($"### {p.Id} · {p.Kind} · {p.Label}\n\n");
            sb.Append($"- Stratum: `{p.Stratum}`\n");
            sb.Append($"- Support: {p.Support} session(s), {p.Occurrences} occurrence(s)\n");
            sb.Append("- Stats: ").Append(string.Join(", ", p.Stats.Select(s => $"{s.Key}={s.Value:0.###}"))).Append('\n');
            sb.Append("- Reading: ").Append(p.Reading).Append('\n');
            if (p.EvidenceSessionIds.Count > 0)
                sb.Append("- Evidence: ").Append(string.Join(", ", p.EvidenceSessionIds.Select(id => $"`{id}`")))
                  .Append(p.EvidenceSessionIds.Count < p.Support ? $" (first {p.EvidenceSessionIds.Count} of {p.Support})" : "").Append('\n');
            sb.Append('\n');
        }
    }

    private static void Exemplars(StringBuilder sb, List<ReviewExemplar> exemplars, bool compact)
    {
        if (exemplars.Count == 0) return;
        sb.Append("## Exemplars\n\nThe best and worst sessions per stratum, by composite, among sessions whose confidence is near the stratum's median.\n\n");
        foreach (var e in exemplars)
        {
            sb.Append($"### `{e.Id}` — {e.Why} in `{e.Stratum}`\n\n");
            sb.Append($"- Score {e.Score:0.0} (confidence {e.Confidence:0.00}, {e.Grade}) · {e.Assistant} · {e.Origin} · {e.Mode}");
            if (e.Repository is { } repo) sb.Append(" · ").Append(repo);
            sb.Append('\n');
            sb.Append($"- {Date(e.FirstSeen)} → {Date(e.LastSeen)} · {e.Turns} turn(s) · {e.ChatCalls} chat call(s), {e.ChatErrors} error(s) · " +
                      $"{e.ToolCalls} tool call(s), {e.ToolErrors} error(s) · {Tokens(e.InputTokens + e.OutputTokens)} tokens\n");
            if (e.Models.Count > 0) sb.Append("- Models: ").Append(string.Join(", ", e.Models)).Append('\n');
            if (e.Tools.Count > 0)
                sb.Append("- Tools: ").Append(string.Join(", ", e.Tools.Select(t => $"{t.Name} {t.Calls}×{(t.Errors > 0 ? $" ({t.Errors} failed)" : "")}"))).Append('\n');
            if (e.ErrorTypes.Count > 0) sb.Append("- Error types: ").Append(Counts(e.ErrorTypes)).Append('\n');
            foreach (var finding in e.TurnFindings) sb.Append("- Turn analysis: ").Append(finding).Append('\n');
            if (!compact && e.RecentEvents.Count > 0)
                sb.Append("- Timeline (last ").Append(e.RecentEvents.Count).Append("): ").Append(string.Join(" | ", e.RecentEvents)).Append('\n');
            sb.Append('\n');
        }
    }

    private static void Notes(StringBuilder sb, List<string> notes)
    {
        if (notes.Count == 0) return;
        sb.Append("## Notes\n\n");
        foreach (var note in notes) sb.Append("- ").Append(note).Append('\n');
    }

    private static string Counts(Dictionary<string, int> counts) =>
        counts.Count == 0 ? "none" : string.Join(" · ", counts.Select(c => $"{c.Key}: {c.Value}"));

    private static string Avg(double? value, string format) => value is { } v ? v.ToString(format) : "n/a";

    private static string Date(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm");

    private static string Tokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.00") + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.0") + "k",
        _ => n.ToString()
    };
}
