using System.Security.Cryptography;
using System.Text;
using CopilotScope.Collector.Alerting;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Review;

/// <summary>How much of the base a pack carries.</summary>
public enum ReviewTier
{
    /// <summary>Rollups, contrasts and patterns with counts only: no session ids, no exemplars.
    /// What a shared deployment serves, and all a team-level review needs.</summary>
    Aggregate,

    /// <summary>The aggregate tier plus the evidence behind it: session rows, the best and worst
    /// sessions per stratum, and the ids each pattern rests on. One machine and one person's own
    /// sessions — or a shared deployment whose works agreement permits individual review.</summary>
    Sessions
}

/// <summary>What a pack is built for: the windows, the tier, the options and the version to stamp it with.</summary>
public sealed record ReviewPackInput(
    DateTimeOffset Since,
    DateTimeOffset Until,
    DateTimeOffset BaselineSince,
    ReviewTier Tier,
    ReviewOptions Options,
    string CollectorVersion);

public sealed record ReviewScope(
    DateTimeOffset Since, DateTimeOffset Until, DateTimeOffset BaselineSince,
    string Tier, int Sessions, int BaselineSessions,
    string CollectorVersion, int PackVersion,
    /// <summary>Identifies the population, not the moment: the same sessions at the same tier
    /// give the same fingerprint, so a report can say which base it was written about.</summary>
    string Fingerprint);

public sealed record ReviewProvenance(
    Dictionary<string, int> ByOrigin,
    Dictionary<string, int> ByAssistant,
    Dictionary<string, int> ByMode,
    Dictionary<string, int> ByStratum,
    int InternalExcluded, int SyntheticExcluded, int EmptyExcluded);

/// <summary>The published arithmetic, restated in the pack so a reader never has to guess at it.</summary>
public sealed record ReviewFormula(
    Dictionary<string, Dictionary<string, double>> Profiles,
    Dictionary<string, int> GradeBands,
    int MinSessionsForAverages,
    int MinSessionsForContrast,
    double RegressionDropPoints,
    List<string> Notes);

/// <summary>A cohort row as the pack carries it: the tooling axis and its totals, and no subject count.</summary>
public sealed record ReviewCohortRow(
    string Dimension, string Value, int Sessions,
    long InputTokens, long OutputTokens,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors, int Turns,
    int EditsAccepted, int EditsRejected,
    /// <summary>Null below <see cref="Cohorts.MinSessionsForAverages"/>, never zero: a missing
    /// average and a terrible one must not look alike to a reader skimming a table.</summary>
    double? AvgQualityScore, double? AvgConfidence,
    double ErrorRate);

public sealed record ReviewBucket(string Range, int Sessions);

public sealed record ReviewDistribution(
    Dictionary<string, int> Grades, List<ReviewBucket> Scores, List<ReviewBucket> Confidence);

public sealed record ReviewCoverageRow(string Assistant, List<string> AlwaysPrior, string Note);

/// <summary>Something that recurred, with the counts behind it and the sessions it rests on.</summary>
public sealed record ReviewPattern(
    string Id, string Kind, string Label,
    /// <summary>origin/assistant/profile. Every contrast is computed inside one stratum, because an
    /// imported session and a live one are scored on different component sets and a cross-stratum
    /// "low band" would mostly be the stratum without latency and acceptance.</summary>
    string Stratum,
    /// <summary>Sessions the pattern occurs in.</summary>
    int Support,
    /// <summary>Times it occurred across those sessions.</summary>
    int Occurrences,
    Dictionary<string, double> Stats,
    string Reading,
    /// <summary>Ids to look up. Empty on the aggregate tier, and capped at
    /// <see cref="ReviewPack.MaxEvidenceIds"/> — the row count in <c>Support</c> is the full figure.</summary>
    List<string> EvidenceSessionIds);

public sealed record ReviewToolRow(string Name, int Calls, int Errors, double AvgMs);

/// <summary>One of a stratum's best or worst sessions, with what the turn analysis said about it.</summary>
public sealed record ReviewExemplar(
    string Id, string Stratum, string Why,
    double Score, double Confidence, string Grade,
    string Assistant, string Origin, string Mode, string? Repository,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    int Turns, int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    long InputTokens, long OutputTokens,
    List<string> Models,
    List<ReviewToolRow> Tools,
    Dictionary<string, int> ErrorTypes,
    List<string> TurnFindings,
    List<string> WorstTurnReasons,
    /// <summary>The last events as the timeline summarizes them — tool names, token counts and
    /// durations. No prompt or response text is ever put in a timeline entry.</summary>
    List<string> RecentEvents);

public sealed record ReviewSessionRow(
    string Id, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    string Assistant, string Origin, string Mode, string? Repository,
    double Score, double Confidence, string Grade,
    int Turns, int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    long InputTokens, long OutputTokens,
    List<string> Models,
    /// <summary>Ids of the patterns this session is evidence for.</summary>
    List<string> Patterns);

/// <summary>
/// The review pack: what an outside reader needs in order to review a base of scored sessions.
///
/// <para>Aggregate-only by construction on the aggregate tier — it is built from rollups and
/// counts, so there is no row about an individual to leak. The sessions tier adds rows and
/// exemplars, and the endpoint decides who may have those. Neither tier carries prompt, response
/// or tool-argument text, whatever the capture setting, and neither names a subject, a branch or
/// a rater.</para>
/// </summary>
public sealed record ReviewPackReport(
    ReviewScope Scope,
    ReviewProvenance Provenance,
    ReviewFormula Formula,
    List<ReviewCohortRow> Cohorts,
    ComparisonReport? Comparison,
    List<Regression> Regressions,
    ReviewDistribution Distribution,
    List<ReviewCoverageRow> Coverage,
    List<ReviewPattern> Patterns,
    List<ReviewExemplar> Exemplars,
    /// <summary>Null on the aggregate tier.</summary>
    List<ReviewSessionRow>? Sessions,
    List<string> Notes);

/// <summary>One session as the pack sees it: scored once, analyzed once, placed in its stratum.</summary>
internal sealed record ScoredSession(CopilotSession Session, QualityReport Report, TurnAnalysis Turns, string Stratum)
{
    public string Id => Session.Id;
}

/// <summary>
/// Builds the pack. A pure function of the sessions and the input it is handed — no clock, no
/// I/O, no store — so two calls over the same base produce byte-identical packs, whatever
/// order the sessions arrived in. That property is what lets a report cite the pack it was
/// written from, and it is tested.
///
/// The model that reads this pack is expected to narrate and to draft; it is never asked to
/// count. Everything countable is counted here, deterministically, with the thresholds stated in
/// the formula block, so an assistant's claim about the base can be checked against the pack
/// rather than taken on trust.
/// </summary>
public static class ReviewPack
{
    public const int PackVersion = 1;

    /// <summary>Sessions each side of a contrast needs. The same floor the regression detector uses
    /// for a window (<see cref="AlertOptions.MinSessionsPerWindow"/>): below it a mean is an
    /// anecdote with a decimal point.</summary>
    public const int MinSessionsForContrast = 10;

    /// <summary>Sessions a recurrence needs before it is called a pattern. Twice is a coincidence.</summary>
    public const int MinRecurrence = 3;

    public const int MaxPatterns = 20;
    public const int MaxRowsPerDimension = 10;
    public const int MaxEvidenceIds = 25;
    public const int MaxExemplarEvents = 20;
    public const int MaxExemplarTools = 10;
    public const int MaxExemplarStrata = 6;
    public const int MaxRegressions = 10;

    public static ReviewPackReport Build(IReadOnlyCollection<CopilotSession> current,
        IReadOnlyCollection<CopilotSession> baseline, QualityEngine quality, ReviewPackInput input)
    {
        var withEvidence = input.Tier == ReviewTier.Sessions;
        var notes = new List<string>();

        int internalExcluded = 0, syntheticExcluded = 0, emptyExcluded = 0;
        var scored = new List<ScoredSession>();
        foreach (var s in current)
        {
            if (SessionClassifier.IsInternal(s.Kind)) { internalExcluded++; continue; }
            if (s.Id.StartsWith(ReviewReadiness.SyntheticPrefix, StringComparison.Ordinal)) { syntheticExcluded++; continue; }
            if (s.ChatCalls == 0) { emptyExcluded++; continue; }
            var report = quality.Evaluate(s);
            scored.Add(new ScoredSession(s, report, SegmentAnalyzer.Analyze(s), Stratum(s, report)));
        }
        // Ordered once, by id. Every list below derives from this one, so the pack is the same
        // whatever order the store handed the sessions over in.
        scored.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        var eligible = scored.Select(x => x.Session).ToList();
        var baselineEligible = baseline
            .Where(ReviewReadiness.IsEligible)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var currentCohorts = Cohorts.Build(eligible, quality, input.Since, input.Until);
        var baselineCohorts = Cohorts.Build(baselineEligible, quality, input.BaselineSince, input.Since);

        var comparison = baselineEligible.Count > 0
            ? Cohorts.Compare("all sessions",
                baselineEligible, input.BaselineSince, input.Since,
                eligible, input.Since, input.Until, quality)
            : null;
        if (comparison is null)
            notes.Add("No sessions in the baseline window, so there is no before/after comparison and no regression could be detected.");

        var regressions = RegressionDetector.Detect(baselineCohorts, currentCohorts, new AlertOptions());
        if (regressions.Count > MaxRegressions)
        {
            notes.Add($"{regressions.Count - MaxRegressions} smaller regression(s) omitted; the {MaxRegressions} largest drops are listed.");
            regressions = regressions.Take(MaxRegressions).ToList();
        }
        if (regressions.Any(r => r.BasisChanged))
            notes.Add("One or more score drops came with a confidence drop: those cohorts are being measured on fewer " +
                      "signals than before, which is a reporting change rather than a quality change.");

        var detected = ReviewPatterns.Detect(scored);
        if (detected.Count > MaxPatterns)
        {
            notes.Add($"{detected.Count - MaxPatterns} pattern(s) with the least support omitted; the {MaxPatterns} best-supported are listed.");
            detected = detected.OrderByDescending(p => p.Support).ThenBy(p => p.Kind, StringComparer.Ordinal)
                               .ThenBy(p => p.Stratum, StringComparer.Ordinal).ThenBy(p => p.Label, StringComparer.Ordinal)
                               .Take(MaxPatterns).ToList();
        }
        var patterns = detected.Select((p, i) => new ReviewPattern(
                $"P{i + 1}", p.Kind, p.Label, p.Stratum, p.Support, p.Occurrences, p.Stats, p.Reading,
                withEvidence ? p.Evidence.Take(MaxEvidenceIds).ToList() : []))
            .ToList();

        var exemplars = withEvidence ? Exemplars(scored, input.Options, notes) : [];
        var sessions = withEvidence ? Rows(scored, detected, input.Options, notes) : null;

        var imported = scored.Count(x => x.Session.Origin == SessionOrigin.LogImport);
        if (imported > 0)
            notes.Add($"{imported} session(s) were imported from an assistant's own transcript files and carry no latency, " +
                      "edit-decision or feedback signal: their scores rest on fewer components, and the pack keeps them in " +
                      "their own strata.");
        if (scored.Count > 0 && scored.Count < Cohorts.MinSessionsForAverages)
            notes.Add($"Only {scored.Count} session(s) in the window; every average below is anecdote.");
        if (scored.Count == 0)
            notes.Add("No eligible sessions in the window: either nothing ran, or the emitters stopped reporting.");
        notes.Add("Scores are comparable within one assistant and only directional across assistants; the coverage block says why.");
        notes.Add("Every pattern is a descriptive contrast within one origin/assistant/profile stratum, not a causal estimate.");
        notes.Add("The pack carries no prompt, response or tool-argument text, whatever the capture setting, and names no person.");
        if (!withEvidence)
            notes.Add("Aggregate tier: counts and contrasts only, no session ids and no exemplars.");

        return new ReviewPackReport(
            new ReviewScope(input.Since, input.Until, input.BaselineSince, input.Tier.ToString().ToLowerInvariant(),
                scored.Count, baselineEligible.Count, input.CollectorVersion, PackVersion, Fingerprint(scored, input.Tier)),
            Provenance(scored, internalExcluded, syntheticExcluded, emptyExcluded),
            Formula(),
            Project(currentCohorts),
            comparison,
            regressions,
            Distribution(scored),
            Coverage(scored),
            patterns,
            exemplars,
            sessions,
            notes);
    }

    /// <summary>origin/assistant/profile — the three things that decide which components a score rests on.</summary>
    internal static string Stratum(CopilotSession s, QualityReport report) =>
        $"{s.Origin}/{s.EmitterKind}/{report.Profile}";

    private static string Fingerprint(List<ScoredSession> scored, ReviewTier tier)
    {
        var sb = new StringBuilder();
        foreach (var x in scored) sb.Append(x.Id).Append(':').Append(x.Session.LastSeen.UtcTicks).Append('\n');
        sb.Append("tier=").Append(tier).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    private static ReviewProvenance Provenance(List<ScoredSession> scored, int internalExcluded, int syntheticExcluded, int emptyExcluded) =>
        new(Count(scored, x => x.Session.Origin),
            Count(scored, x => x.Session.EmitterKind.ToString()),
            Count(scored, x => x.Report.ModeLabel),
            Count(scored, x => x.Stratum),
            internalExcluded, syntheticExcluded, emptyExcluded);

    private static Dictionary<string, int> Count(List<ScoredSession> scored, Func<ScoredSession, string> key) =>
        scored.GroupBy(key, StringComparer.Ordinal)
              .OrderBy(g => g.Key, StringComparer.Ordinal)
              .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static ReviewFormula Formula()
    {
        static Dictionary<string, double> Weights(ScoringProfile p) => new(StringComparer.Ordinal)
        {
            ["reliability"] = p.Reliability, ["acceptance"] = p.Acceptance, ["friction"] = p.Friction,
            ["latency"] = p.Latency, ["feedback"] = p.Feedback, ["efficiency"] = p.Efficiency
        };
        var profiles = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal)
        {
            [ScoringProfile.Interactive.Name] = Weights(ScoringProfile.Interactive),
            [ScoringProfile.SupervisedAgent.Name] = Weights(ScoringProfile.SupervisedAgent),
            [ScoringProfile.Autonomous.Name] = Weights(ScoringProfile.Autonomous)
        };
        var bands = new Dictionary<string, int>(StringComparer.Ordinal)
            { ["excellent"] = 85, ["good"] = 70, ["fair"] = 55, ["poor"] = 40, ["critical"] = 0 };
        var options = new AlertOptions();
        return new ReviewFormula(profiles, bands, Cohorts.MinSessionsForAverages, MinSessionsForContrast, options.ScoreDropPoints,
        [
            "Composite 0–100: a weighted sum of the components that have data, weights renormalized over them. " +
            "Confidence = data coverage × sample ramp, and is exported beside every score.",
            "Grade bands are lower bounds on the composite.",
            $"Averages are reported for groups of at least {Cohorts.MinSessionsForAverages} sessions and are null below that.",
            $"A contrast needs at least {MinSessionsForContrast} sessions on each side, within one stratum. " +
            $"A recurrence needs at least {MinRecurrence} sessions before it is called a pattern.",
            $"A regression is a cohort mean that fell by at least {options.ScoreDropPoints} points between the baseline " +
            $"and the current window, with at least {options.MinSessionsPerWindow} sessions in each.",
            "Turn analysis (TFRA): each invoke_agent trace is a turn, penalized for LLM and tool errors, for time-to-first-token " +
            "measured against this session's own median, and for repair loops (tool-call bursts with failures)."
        ]);
    }

    private static List<ReviewCohortRow> Project(CohortReport report)
    {
        var rows = new List<ReviewCohortRow>();
        foreach (var group in new[] { report.ByAssistant, report.ByModel, report.ByRepository, report.ByKind })
            rows.AddRange(group
                .OrderByDescending(r => r.Sessions).ThenBy(r => r.Value, StringComparer.Ordinal)
                .Take(MaxRowsPerDimension)
                .Select(r => new ReviewCohortRow(
                    r.Dimension, r.Value, r.Sessions,
                    r.InputTokens, r.OutputTokens,
                    r.ChatCalls, r.ChatErrors, r.ToolCalls, r.ToolErrors, r.Turns,
                    r.EditsAccepted, r.EditsRejected,
                    r.Sessions >= Cohorts.MinSessionsForAverages ? r.AvgQualityScore : null,
                    r.Sessions >= Cohorts.MinSessionsForAverages ? r.AvgConfidence : null,
                    r.ErrorRate)));
        return rows;
    }

    private static ReviewDistribution Distribution(List<ScoredSession> scored)
    {
        var grades = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var grade in new[] { "excellent", "good", "fair", "poor", "critical" })
            grades[grade] = scored.Count(x => x.Report.Grade == grade);

        static List<ReviewBucket> Buckets(IEnumerable<double> values, (string Range, double Lo, double Hi)[] edges)
        {
            var list = values.ToList();
            return edges.Select(e => new ReviewBucket(e.Range, list.Count(v => v >= e.Lo && v < e.Hi))).ToList();
        }

        return new ReviewDistribution(grades,
            Buckets(scored.Select(x => x.Report.Score),
                [("0-39", 0, 40), ("40-54", 40, 55), ("55-69", 55, 70), ("70-84", 70, 85), ("85-100", 85, double.PositiveInfinity)]),
            Buckets(scored.Select(x => x.Report.Confidence),
                [("0.00-0.24", 0, 0.25), ("0.25-0.49", 0.25, 0.5), ("0.50-0.74", 0.5, 0.75), ("0.75-1.00", 0.75, double.PositiveInfinity)]));
    }

    private static List<ReviewCoverageRow> Coverage(List<ScoredSession> scored)
    {
        var present = scored.Select(x => x.Session.EmitterKind).ToHashSet();
        return EmitterCoverage.All
            .Where(e => present.Contains(e.Emitter))
            .OrderBy(e => e.DisplayName, StringComparer.Ordinal)
            .Select(e => new ReviewCoverageRow(e.DisplayName, e.AlwaysPrior.ToList(), e.Note))
            .ToList();
    }

    private static List<ReviewExemplar> Exemplars(List<ScoredSession> scored, ReviewOptions options, List<string> notes)
    {
        var perSide = Math.Max(0, options.ExemplarsPerSide);
        var strata = scored
            .GroupBy(x => x.Stratum, StringComparer.Ordinal)
            .Where(g => g.Count() >= Cohorts.MinSessionsForAverages)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (strata.Count > MaxExemplarStrata)
        {
            notes.Add($"Exemplars cover the {MaxExemplarStrata} largest strata; {strata.Count - MaxExemplarStrata} smaller one(s) carry none.");
            strata = strata.Take(MaxExemplarStrata).ToList();
        }

        var result = new List<ReviewExemplar>();
        foreach (var stratum in strata)
        {
            var members = stratum.ToList();
            // Eligibility is relative to the stratum: a flat confidence gate would drop nearly every
            // imported session, whose coverage tops out well below a live VS Code session's, and the
            // native binary's default data is entirely imported.
            var floor = 0.8 * Median(members.Select(m => m.Report.Confidence).ToList());
            var candidates = members.Where(m => m.Report.Confidence >= floor).ToList();

            var best = candidates.OrderByDescending(m => m.Report.Score).ThenBy(m => m.Id, StringComparer.Ordinal)
                                 .Take(perSide).ToList();
            var worst = candidates.OrderBy(m => m.Report.Score).ThenBy(m => m.Id, StringComparer.Ordinal)
                                  .Take(perSide).Where(m => !best.Contains(m)).ToList();
            result.AddRange(best.Select(m => Exemplar(m, "best")));
            result.AddRange(worst.Select(m => Exemplar(m, "worst")));
        }
        return result;
    }

    private static ReviewExemplar Exemplar(ScoredSession m, string why)
    {
        var worst = m.Turns.WorstIndex is { } w ? m.Turns.Turns.FirstOrDefault(t => t.Index == w)?.Reasons ?? [] : [];
        return m.Session.Snapshot(s => new ReviewExemplar(
            s.Id, m.Stratum, why,
            m.Report.Score, m.Report.Confidence, m.Report.Grade,
            s.EmitterKind.ToString(), s.Origin, m.Report.ModeLabel, Dto.Anonymize(s.Repository),
            s.FirstSeen, s.LastSeen,
            s.Turns, s.ChatCalls, s.ChatErrors, s.ToolCalls, s.ToolErrors,
            s.InputTokens, s.OutputTokens,
            s.ModelCalls.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            s.Tools.OrderByDescending(t => t.Value.Calls).ThenBy(t => t.Key, StringComparer.Ordinal)
                   .Take(MaxExemplarTools)
                   .Select(t => new ReviewToolRow(t.Key, t.Value.Calls, t.Value.Errors,
                       t.Value.Calls > 0 ? Math.Round(t.Value.TotalMs / t.Value.Calls, 1) : 0))
                   .ToList(),
            s.ErrorTypes.OrderBy(e => e.Key, StringComparer.Ordinal).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
            m.Turns.Findings.ToList(),
            worst.ToList(),
            s.RecentEvents.Reverse().Take(MaxExemplarEvents).Reverse().Select(e => e.Summary).ToList()));
    }

    private static List<ReviewSessionRow> Rows(List<ScoredSession> scored, List<DetectedPattern> detected,
        ReviewOptions options, List<string> notes)
    {
        var byPattern = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < detected.Count; i++)
            foreach (var id in detected[i].Evidence)
                (byPattern.TryGetValue(id, out var list) ? list : byPattern[id] = []).Add($"P{i + 1}");

        var ordered = scored.OrderByDescending(x => x.Session.LastSeen).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        var max = Math.Max(0, options.MaxSessionRows);
        if (ordered.Count > max)
        {
            notes.Add($"Session rows list the {max} most recent of {ordered.Count} sessions; the rest are reachable by id through the API.");
            ordered = ordered.Take(max).ToList();
        }

        return ordered.Select(m => m.Session.Snapshot(s => new ReviewSessionRow(
            s.Id, s.FirstSeen, s.LastSeen,
            s.EmitterKind.ToString(), s.Origin, m.Report.ModeLabel, Dto.Anonymize(s.Repository),
            m.Report.Score, m.Report.Confidence, m.Report.Grade,
            s.Turns, s.ChatCalls, s.ChatErrors, s.ToolCalls, s.ToolErrors,
            s.InputTokens, s.OutputTokens,
            s.ModelCalls.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            byPattern.TryGetValue(s.Id, out var ids) ? ids : []))).ToList();
    }

    internal static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
