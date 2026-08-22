namespace CopilotScope.JudgeAgent.Calibration;

/// <summary>
/// Turns a labelled dataset into the calibration report <c>AI-EVALS.md</c> §5 requires before
/// a judge's scores are allowed to gate anything.
///
/// <para>Two measurements per rubric, and the order matters:</para>
/// <list type="number">
///   <item><b>The ceiling</b> — how well the human panel agrees with itself. This repo's own
///     thesis notes make the point that low agreement <em>between people</em> is a result in
///     its own right: it fixes a bound no algorithm can climb past
///     (<c>research/articles/thesis_topics.tex</c> §535). A judge measured against labels the
///     labellers cannot reproduce is measured against noise.</item>
///   <item><b>The judge</b> — how well the judge agrees with the panel's consensus. Only
///     meaningful once the ceiling holds, which is why a low ceiling produces its own verdict
///     rather than a pass or a fail.</item>
/// </list>
///
/// <para>The whole thing is a pure function of the dataset: no clock, no model call, no
/// randomness beyond a fixed bootstrap seed. That is deliberate — a calibration you cannot
/// re-run to the same number is not a baseline.</para>
/// </summary>
public sealed class CalibrationEngine(CalibrationOptions? options = null)
{
    private readonly CalibrationOptions _options = options ?? new CalibrationOptions();

    /// <summary>The weighting the verdict is read from. Quadratic, because the scale is ordinal:
    /// a judge that says "full" where the human said "none" should not be scored the same as one
    /// that said "mostly". The other two weightings are still reported for comparison.</summary>
    public const KappaWeighting HeadlineWeighting = KappaWeighting.Quadratic;

    public CalibrationReport Evaluate(CalibrationDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var labels = dataset.Labels ?? [];
        var judgeScores = dataset.JudgeScores ?? [];
        var warnings = new List<string>();

        // Reject out-of-scale labels loudly. A level of 4 on a four-band scale is a broken
        // labelling form, and silently clamping it would bury the form bug inside a κ.
        var invalid = labels.Where(l => !RubricScale.IsValidLevel(l.Level)).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException(
                $"{invalid.Count} label(s) fall outside the 0..{RubricScale.Categories - 1} scale, " +
                $"first: session '{invalid[0].SessionId}' rater '{invalid[0].Rater}' level {invalid[0].Level}.",
                nameof(dataset));

        foreach (var unknown in labels.Select(l => l.Algorithm)
                     .Where(a => RubricScale.Find(a) is null)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(a => a, StringComparer.Ordinal))
            warnings.Add($"Labels reference unknown rubric '{unknown}'; it is reported but has no anchor text.");

        // One score per (session, rubric). A dataset carrying two judge scores for the same
        // pair is ambiguous — usually two runs concatenated — and picking one silently would
        // make the report depend on list order.
        var duplicateScores = judgeScores
            .GroupBy(s => (s.SessionId, s.Algorithm.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicateScores.Count > 0)
            throw new ArgumentException(
                $"{duplicateScores.Count} (session, rubric) pair(s) carry more than one judge score, " +
                $"first: '{duplicateScores[0].Key.SessionId}' / '{duplicateScores[0].Key.Item2}'. " +
                "Supply one score per rubric per session.", nameof(dataset));

        var scoresByKey = judgeScores.ToDictionary(
            s => (s.SessionId, s.Algorithm), s => s.Score,
            new SessionRubricComparer());

        var rubrics = labels
            .Select(l => l.Algorithm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.Ordinal)
            .Select(algorithm => EvaluateRubric(algorithm, labels, scoresByKey))
            .ToList();

        var (verdict, summary) = Summarize(rubrics);

        return new CalibrationReport(
            dataset.DatasetVersion,
            dataset.JudgeModel,
            dataset.JudgePromptVersion,
            _options.MinKappa,
            _options.MinPairedSessions,
            new ScaleDescriptor(RubricScale.Categories, RubricScale.Bands),
            rubrics,
            verdict,
            summary,
            warnings);
    }

    // --------------------------------------------------------------- per rubric

    private RubricCalibration EvaluateRubric(
        string algorithm,
        IReadOnlyList<HumanLabel> allLabels,
        IReadOnlyDictionary<(string, string), double> judgeScores)
    {
        var definition = RubricScale.Find(algorithm);
        var labels = allLabels.Where(l => l.Algorithm.Equals(algorithm, StringComparison.OrdinalIgnoreCase)).ToList();

        // rater → (session → band). Last label wins for a repeated (rater, session): a labeller
        // revising their own grade is the normal reason for a duplicate.
        var byRater = labels
            .GroupBy(l => l.Rater, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, int>)g
                    .GroupBy(l => l.SessionId, StringComparer.Ordinal)
                    .ToDictionary(s => s.Key, s => s.Last().Level, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var ceiling = MeasureCeiling(byRater);

        // Consensus per session, then pair it with the judge. Ordinal sort keeps the bootstrap
        // reproducible — it resamples by position, so item order must not float.
        var labelledSessions = labels.Select(l => l.SessionId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var humanBands = new List<int>();
        var judgeBands = new List<int>();
        var dropped = 0;

        foreach (var sessionId in labelledSessions)
        {
            if (!judgeScores.TryGetValue((sessionId, algorithm), out var score))
            {
                // Typically a rubric the judge returned "no-data" for — RAGAS on a session with
                // no retrieval. Counted and reported so the paired N is never mistaken for the
                // labelled N.
                dropped++;
                continue;
            }

            humanBands.Add(Consensus(byRater, sessionId));
            judgeBands.Add(RubricScale.Band(score));
        }

        var agreements = new[] { KappaWeighting.Unweighted, KappaWeighting.Linear, KappaWeighting.Quadratic }
            .Select(w => Agreement.Cohen(humanBands, judgeBands, RubricScale.Categories, w,
                _options.BootstrapIterations, _options.BootstrapSeed))
            .ToList();

        var headline = agreements.FirstOrDefault(a =>
            a.Weighting.Equals(HeadlineWeighting.ToString(), StringComparison.OrdinalIgnoreCase));

        var (verdict, detail) = Judge(headline, ceiling, humanBands.Count, dropped);

        return new RubricCalibration(
            algorithm,
            definition?.Question ?? "(unknown rubric — no anchor question registered)",
            definition?.HigherIsBetter ?? true,
            humanBands.Count,
            labelledSessions.Count,
            dropped,
            ceiling,
            agreements,
            headline,
            verdict,
            detail);
    }

    /// <summary>
    /// The panel's agreement with itself, pair by pair.
    ///
    /// <para>A single rater has no ceiling to measure — that is not a failure, but it is a
    /// caveat that has to travel with the number, because "the judge agrees with Alice" is a
    /// weaker claim than "the judge agrees with a panel that agrees with itself".</para>
    /// </summary>
    private HumanCeiling MeasureCeiling(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> byRater)
    {
        var raters = byRater.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();

        if (raters.Count == 0)
            return new HumanCeiling(raters, [], null, "no-data", "No labels for this rubric.");

        if (raters.Count == 1)
            return new HumanCeiling(raters, [], null, "single-rater",
                $"Only '{raters[0]}' labelled this rubric, so there is no inter-rater ceiling. " +
                "Judge agreement below is against one person's opinion, not a validated ground truth — " +
                "add a second labeller before treating it as one.");

        var (pairs, meanKappa) = Agreement.Pairwise(byRater, RubricScale.Categories,
            HeadlineWeighting, _options.BootstrapIterations, _options.BootstrapSeed);

        var thin = pairs.Where(p => p.Agreement.Samples < _options.MinPairedSessions).ToList();
        var detail = meanKappa is null
            ? "No rater pair produced a defined κ — the panel's labels are degenerate or disjoint."
            : $"Mean pairwise κ (quadratic) {meanKappa:F3} across {pairs.Count} pair(s) — " +
              $"{Agreement.Interpret(meanKappa.Value)}." +
              (thin.Count > 0
                  ? $" {thin.Count} pair(s) overlap on fewer than {_options.MinPairedSessions} sessions."
                  : "");

        return new HumanCeiling(raters, pairs, meanKappa, "ok", detail);
    }

    /// <summary>
    /// The panel's consensus band for one session: the median of the bands its raters gave.
    ///
    /// <para>Median rather than mean, because the scale is ordinal — the midpoint between
    /// "partial" and "full" is not a grade anyone assigned. With an even number of raters and
    /// two different middle bands the consensus is genuinely ambiguous; the lower one is taken
    /// so the rule is deterministic and independent of the order labels arrive in. On a panel
    /// that agrees with itself those ties are adjacent bands and the choice barely moves κ; if
    /// it moves κ a lot, the ceiling is the finding, not the tie-break.</para>
    /// </summary>
    private static int Consensus(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> byRater, string sessionId)
    {
        var bands = byRater.Values
            .Where(r => r.ContainsKey(sessionId))
            .Select(r => r[sessionId])
            .Order()
            .ToList();

        return bands[(bands.Count - 1) / 2];
    }

    // ----------------------------------------------------------------- verdicts

    private (string Verdict, string Detail) Judge(
        AgreementResult? headline, HumanCeiling ceiling, int paired, int dropped)
    {
        var droppedNote = dropped > 0
            ? $" {dropped} labelled session(s) had no judge score for this rubric and were excluded."
            : "";

        if (headline is null || headline.Kappa is null)
            return (CalibrationVerdict.InsufficientData,
                $"No κ could be computed from {paired} paired session(s): " +
                $"{headline?.Detail ?? "no agreement result"}.{droppedNote}");

        var kappa = headline.Kappa.Value;
        var interval = headline.CiLow is { } low && headline.CiHigh is { } high
            ? $" (95% CI {low:F3}–{high:F3})"
            : " (no interval — sample too small)";
        var measured = $"Judge vs. human consensus: quadratic-weighted κ {kappa:F3}{interval}, " +
                       $"{Agreement.Interpret(kappa)}, over {paired} paired session(s).";

        if (paired < _options.MinPairedSessions)
            return (CalibrationVerdict.InsufficientData,
                $"{measured} Below the {_options.MinPairedSessions}-session minimum, so this number " +
                $"does not license a conclusion either way.{droppedNote}");

        // The ceiling gates the verdict. A judge cannot be validated against labels the
        // labellers themselves cannot reproduce, however well it happens to match them.
        if (ceiling.Status == "ok")
        {
            if (ceiling.MeanKappa is not { } ceilingKappa)
                // A measurable panel whose every pair came out undefined: they all used one
                // band. No disagreement was possible, so nothing was demonstrated.
                return (CalibrationVerdict.CeilingTooLow,
                    $"{measured} But no rater pair produced a defined κ — the panel put every session in a " +
                    $"single band, so the labels are not a ground truth yet and the judge cannot be validated " +
                    $"against them. Label a spread of sessions.{droppedNote}");

            if (ceilingKappa < _options.MinKappa)
                return (CalibrationVerdict.CeilingTooLow,
                    $"{measured} But the human panel only agrees with itself at κ {ceilingKappa:F3}, below the " +
                    $"{_options.MinKappa:F2} threshold — the labels are not a ground truth yet, so the judge " +
                    $"cannot be validated against them. Fix the rubric anchors or the labelling brief first.{droppedNote}");
        }

        // One labeller is not a panel. However high κ comes out, it says the judge agrees with
        // one person — not that the labels it agrees with are reproducible. Reported, never
        // certified: this is missing data, not a failed judge.
        if (ceiling.Status == "single-rater")
            return (CalibrationVerdict.InsufficientData,
                $"{measured} A single labeller means there is no measured ceiling, so this κ cannot certify " +
                $"the judge {(kappa >= _options.MinKappa ? "however well it scores" : "either way")}. " +
                $"A second labeller is what turns this into a calibration.{droppedNote}");

        return kappa >= _options.MinKappa
            ? (CalibrationVerdict.Calibrated,
                $"{measured} Clears the {_options.MinKappa:F2} threshold against a panel agreeing at " +
                $"κ {ceiling.MeanKappa:F3}.{droppedNote}")
            : (CalibrationVerdict.NotCalibrated,
                $"{measured} Below the {_options.MinKappa:F2} threshold — these scores must not gate anything. " +
                $"Read the confusion matrix: a judge biased one band high is a rubric-anchor problem, " +
                $"a scattered matrix is a judge problem.{droppedNote}");
    }

    private static (string Verdict, string Summary) Summarize(IReadOnlyList<RubricCalibration> rubrics)
    {
        if (rubrics.Count == 0)
            return (CalibrationVerdict.InsufficientData, "No labels supplied — nothing to calibrate.");

        var counts = rubrics.GroupBy(r => r.Verdict)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        int Count(string verdict) => counts.TryGetValue(verdict, out var n) ? n : 0;

        // Worst verdict wins: one rubric that cannot gate is enough to stop the suite from
        // claiming the judge is calibrated.
        var verdict = Count(CalibrationVerdict.CeilingTooLow) > 0 ? CalibrationVerdict.CeilingTooLow
                    : Count(CalibrationVerdict.NotCalibrated) > 0 ? CalibrationVerdict.NotCalibrated
                    : Count(CalibrationVerdict.InsufficientData) > 0 ? CalibrationVerdict.InsufficientData
                    : CalibrationVerdict.Calibrated;

        var parts = new[]
            {
                (CalibrationVerdict.Calibrated, Count(CalibrationVerdict.Calibrated)),
                (CalibrationVerdict.NotCalibrated, Count(CalibrationVerdict.NotCalibrated)),
                (CalibrationVerdict.CeilingTooLow, Count(CalibrationVerdict.CeilingTooLow)),
                (CalibrationVerdict.InsufficientData, Count(CalibrationVerdict.InsufficientData))
            }
            .Where(p => p.Item2 > 0)
            .Select(p => $"{p.Item2} {p.Item1}");

        return (verdict, $"{rubrics.Count} rubric(s): {string.Join(", ", parts)}. " +
                         $"Overall: {verdict}.");
    }

    /// <summary>Pairs (session, rubric) with the rubric id compared case-insensitively, so a
    /// dataset written with "g-eval" still matches a judge that emitted "G-Eval".</summary>
    private sealed class SessionRubricComparer : IEqualityComparer<(string SessionId, string Algorithm)>
    {
        public bool Equals((string SessionId, string Algorithm) x, (string SessionId, string Algorithm) y) =>
            string.Equals(x.SessionId, y.SessionId, StringComparison.Ordinal) &&
            string.Equals(x.Algorithm, y.Algorithm, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string SessionId, string Algorithm) obj) =>
            HashCode.Combine(obj.SessionId, obj.Algorithm.ToLowerInvariant());
    }
}
