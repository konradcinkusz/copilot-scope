using CopilotScope.Collector.Domain;

namespace CopilotScope.JudgeAgent.Calibration;

/// <summary>How much an ordinal miss costs. Cohen's κ treats every off-diagonal cell alike;
/// on an ordered rubric that is usually the wrong model, because "excellent vs. poor" is a
/// worse miss than "excellent vs. good" and unweighted κ scores them identically.</summary>
public enum KappaWeighting
{
    /// <summary>Classic Cohen's κ — any disagreement counts the same.</summary>
    Unweighted,

    /// <summary>Cost grows with the distance between bands: |i−j| / (K−1).</summary>
    Linear,

    /// <summary>Cost grows with the square of the distance: (i−j)² / (K−1)². The usual
    /// choice for an ordinal rubric, and the one to read first in a calibration report.</summary>
    Quadratic
}

/// <summary>One κ measurement between two raters, with everything needed to argue about it.</summary>
/// <param name="Weighting">Which weighting produced <paramref name="Kappa"/>.</param>
/// <param name="Kappa">Chance-corrected agreement, or null when κ is undefined (see <paramref name="Status"/>).</param>
/// <param name="ObservedAgreement">Share of items the raters agreed on (weighted: 1 − mean disagreement cost).</param>
/// <param name="ExpectedAgreement">Agreement the raters' own marginals predict by luck alone.</param>
/// <param name="Samples">Number of rated pairs.</param>
/// <param name="Categories">Size of the ordinal scale.</param>
/// <param name="CiLow">Lower bound of the 95% bootstrap interval, when one could be computed.</param>
/// <param name="CiHigh">Upper bound of the 95% bootstrap interval, when one could be computed.</param>
/// <param name="Interpretation">Landis &amp; Koch band for <paramref name="Kappa"/>.</param>
/// <param name="ConfusionMatrix">Row = rater A's band, column = rater B's band.</param>
/// <param name="Status">"ok" when κ was computed, "undefined" or "no-data" otherwise.</param>
/// <param name="Detail">Why the status is what it is — always populated, including on "ok".</param>
public sealed record AgreementResult(
    string Weighting,
    double? Kappa,
    double ObservedAgreement,
    double ExpectedAgreement,
    int Samples,
    int Categories,
    double? CiLow,
    double? CiHigh,
    string Interpretation,
    IReadOnlyList<IReadOnlyList<int>> ConfusionMatrix,
    string Status,
    string Detail);

/// <summary>
/// Chance-corrected agreement between two raters on an ordinal scale — Cohen's κ and its
/// weighted variants.
///
/// <para>κ = (p_o − p_e) / (1 − p_e), where p_o is the share of items both raters put in the
/// same band and p_e is the share they would be expected to share if each rated independently
/// at their own observed rate. Raw agreement is not usable on its own: two raters who each
/// call 90% of sessions "good" agree 82% of the time by luck, which is why a calibration
/// report that leads with "the judge matched the human 82% of the time" says nothing.</para>
///
/// <para>All three weightings run through one code path, because unweighted κ is just weighted
/// κ with every off-diagonal cell costed at 1:</para>
///
/// <code>
///   κ_w = 1 − (Σ d_ij·O_ij / N) / (Σ d_ij·r_i·c_j)
///
///   d_ij = 0 on the diagonal, and off it
///     unweighted  1
///     linear      |i−j| / (K−1)
///     quadratic   (i−j)² / (K−1)²
/// </code>
///
/// <para>Intervals are a seeded percentile bootstrap rather than the Fleiss–Cohen–Everitt
/// asymptotic standard error. Two reasons: the analytic variance has a different (and much
/// messier) form for weighted κ, so one bootstrap covers all three weightings with a single
/// testable implementation; and the sample this repo will realistically calibrate on is
/// 50–100 sessions, where the asymptotic approximation is at its weakest. The seed is fixed
/// so a report re-run on the same labels returns the same interval — a calibration number
/// that moves on its own is not a baseline anyone can regress against.</para>
/// </summary>
public static class Agreement
{
    /// <summary>Resamples per bootstrap. 2000 puts the Monte-Carlo error on a percentile
    /// bound well below the sampling error the interval is reporting.</summary>
    public const int DefaultBootstrapIterations = 2000;

    /// <summary>Fixed so the same labels always yield the same interval.</summary>
    public const int DefaultSeed = 20260822;

    /// <summary>Below this, a bootstrap interval is so wide it only invites over-reading.</summary>
    private const int MinSamplesForBootstrap = 10;

    /// <summary>Cohen's κ between two raters over <paramref name="categories"/> ordinal bands.</summary>
    /// <param name="raterA">Band index per item, 0-based.</param>
    /// <param name="raterB">Band index per item, aligned element-wise with <paramref name="raterA"/>.</param>
    /// <param name="categories">Size of the scale; band indices must fall in [0, categories).</param>
    /// <param name="weighting">How ordinal distance is costed.</param>
    /// <param name="bootstrapIterations">0 disables the interval.</param>
    /// <param name="seed">Bootstrap seed; the default keeps reports reproducible.</param>
    public static AgreementResult Cohen(
        IReadOnlyList<int> raterA,
        IReadOnlyList<int> raterB,
        int categories,
        KappaWeighting weighting = KappaWeighting.Unweighted,
        int bootstrapIterations = DefaultBootstrapIterations,
        int seed = DefaultSeed)
    {
        ArgumentNullException.ThrowIfNull(raterA);
        ArgumentNullException.ThrowIfNull(raterB);
        if (raterA.Count != raterB.Count)
            throw new ArgumentException(
                $"Raters must have rated the same items: got {raterA.Count} and {raterB.Count} ratings.", nameof(raterB));
        if (categories < 2)
            throw new ArgumentOutOfRangeException(nameof(categories), categories,
                "κ needs at least two bands; a one-band scale cannot express disagreement.");

        var name = weighting.ToString().ToLowerInvariant();
        var empty = Array.Empty<IReadOnlyList<int>>();

        if (raterA.Count == 0)
            return new AgreementResult(name, null, 0, 0, 0, categories, null, null,
                "no data", empty, "no-data", "No rated items.");

        for (var i = 0; i < raterA.Count; i++)
        {
            Validate(raterA[i], categories, nameof(raterA), i);
            Validate(raterB[i], categories, nameof(raterB), i);
        }

        var a = raterA.ToArray();
        var b = raterB.ToArray();
        var costs = DisagreementCosts(categories, weighting);
        var confusion = Confusion(a, b, categories);
        var (kappa, observedCost, expectedCost) = Score(confusion, a.Length, costs);

        var matrix = ToJagged(confusion);
        var observedAgreement = 1.0 - observedCost;
        var expectedAgreement = 1.0 - expectedCost;

        if (kappa is null)
            return new AgreementResult(name, null, observedAgreement, expectedAgreement,
                a.Length, categories, null, null, "undefined", matrix, "undefined",
                "Every rating landed in a single band, so chance agreement is already total and " +
                "κ is 0/0. This is a degenerate sample, not perfect agreement — label a spread of sessions.");

        var ci = Bootstrap(a, b, categories, costs, bootstrapIterations, seed);

        return new AgreementResult(name, Math.Round(kappa.Value, 4),
            Math.Round(observedAgreement, 4), Math.Round(expectedAgreement, 4),
            a.Length, categories,
            ci is null ? null : Math.Round(ci.Value.Low, 4),
            ci is null ? null : Math.Round(ci.Value.High, 4),
            Interpret(kappa.Value), matrix, "ok",
            ci is null
                ? $"{a.Length} paired ratings; no bootstrap interval " +
                  $"({(a.Length < MinSamplesForBootstrap ? $"fewer than {MinSamplesForBootstrap} items" : "too many degenerate resamples")})."
                : $"{a.Length} paired ratings; 95% bootstrap CI over {bootstrapIterations} resamples (seed {seed}).");

        static void Validate(int band, int categories, string parameter, int index)
        {
            if (band < 0 || band >= categories)
                throw new ArgumentOutOfRangeException(parameter, band,
                    $"Band at index {index} is outside the scale [0, {categories}).");
        }
    }

    /// <summary>
    /// Mean pairwise Cohen's κ across two or more raters — the agreement of a human panel
    /// with itself.
    ///
    /// <para>Fleiss' κ is the textbook multi-rater statistic, but it answers a different
    /// question (agreement among interchangeable raters drawn from a pool) and collapses the
    /// panel into one number. Averaging the pairwise Cohen values keeps every pair visible, so
    /// a panel that looks acceptable in the mean but hides one rater disagreeing with both
    /// others shows up as a low pair instead of a slightly depressed average.</para>
    ///
    /// <para>Raters rarely label exactly the same set, so each pair is scored on the items
    /// <em>both</em> of them rated. Padding the gaps with a default band would manufacture
    /// agreement out of absence.</para>
    /// </summary>
    /// <param name="raters">Rater id → (item id → band). Items are aligned per pair by id.</param>
    /// <returns>Every pair's result in a stable order, plus their mean κ (null when no pair
    /// produced a defined κ).</returns>
    public static (IReadOnlyList<PairAgreement> Pairs, double? MeanKappa) Pairwise(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> raters,
        int categories,
        KappaWeighting weighting = KappaWeighting.Unweighted,
        int bootstrapIterations = DefaultBootstrapIterations,
        int seed = DefaultSeed)
    {
        ArgumentNullException.ThrowIfNull(raters);

        var names = raters.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var pairs = new List<PairAgreement>();

        for (var i = 0; i < names.Count; i++)
            for (var j = i + 1; j < names.Count; j++)
            {
                var left = raters[names[i]];
                var right = raters[names[j]];

                // Ordinal sort, not enumeration order: the bootstrap resamples by position, so
                // an unstable item order would move the interval between runs on identical data.
                var shared = left.Keys.Where(right.ContainsKey).OrderBy(x => x, StringComparer.Ordinal).ToList();

                pairs.Add(new PairAgreement(names[i], names[j],
                    Cohen(shared.Select(id => left[id]).ToList(),
                          shared.Select(id => right[id]).ToList(),
                          categories, weighting, bootstrapIterations, seed)));
            }

        var defined = pairs.Where(p => p.Agreement.Kappa is not null).Select(p => p.Agreement.Kappa!.Value).ToList();
        return (pairs, defined.Count > 0 ? Math.Round(defined.Average(), 4) : null);
    }

    /// <summary>Landis &amp; Koch (1977) bands. A convention for reading κ, not a law — the
    /// gate threshold is configured separately, because what counts as "good enough" depends
    /// on what the score is allowed to decide.</summary>
    public static string Interpret(double kappa) => kappa switch
    {
        < 0.00 => "poor (worse than chance)",
        < 0.21 => "slight",
        < 0.41 => "fair",
        < 0.61 => "moderate",
        < 0.81 => "substantial",
        _ => "almost perfect"
    };

    // ---------------------------------------------------------------- internals

    /// <summary>κ from a confusion matrix. Returns null when κ is undefined rather than
    /// pretending the answer is 1.0 — see the "undefined" branch in <see cref="Cohen"/>.</summary>
    private static (double? Kappa, double ObservedCost, double ExpectedCost) Score(
        int[,] confusion, int total, double[,] costs)
    {
        var categories = confusion.GetLength(0);
        var rowMarginal = new double[categories];
        var columnMarginal = new double[categories];

        for (var i = 0; i < categories; i++)
            for (var j = 0; j < categories; j++)
            {
                rowMarginal[i] += confusion[i, j];
                columnMarginal[j] += confusion[i, j];
            }

        for (var i = 0; i < categories; i++)
        {
            rowMarginal[i] /= total;
            columnMarginal[i] /= total;
        }

        double observed = 0, expected = 0;
        for (var i = 0; i < categories; i++)
            for (var j = 0; j < categories; j++)
            {
                observed += costs[i, j] * confusion[i, j] / total;
                expected += costs[i, j] * rowMarginal[i] * columnMarginal[j];
            }

        // Expected disagreement is zero only when the marginals leave no room for any — both
        // raters used exactly one band, and the same one. κ is then 0/0. Returning 1.0 would
        // turn "this sample has no variance to measure" into "perfect agreement", which is
        // precisely the illusion κ exists to strip out.
        double? kappa = expected <= double.Epsilon ? null : 1.0 - observed / expected;
        return (kappa, observed, expected);
    }

    private static int[,] Confusion(int[] a, int[] b, int categories)
    {
        var confusion = new int[categories, categories];
        for (var i = 0; i < a.Length; i++) confusion[a[i], b[i]]++;
        return confusion;
    }

    private static double[,] DisagreementCosts(int categories, KappaWeighting weighting)
    {
        var costs = new double[categories, categories];
        double span = categories - 1; // ≥ 1: categories ≥ 2 is enforced by the caller
        for (var i = 0; i < categories; i++)
            for (var j = 0; j < categories; j++)
                costs[i, j] = weighting switch
                {
                    KappaWeighting.Linear => Math.Abs(i - j) / span,
                    KappaWeighting.Quadratic => (i - j) * (i - j) / (span * span),
                    _ => i == j ? 0.0 : 1.0
                };
        return costs;
    }

    /// <summary>Percentile bootstrap: resample the rated pairs with replacement, recompute κ,
    /// and read the 2.5th/97.5th percentiles off the resulting distribution.</summary>
    private static (double Low, double High)? Bootstrap(
        int[] a, int[] b, int categories, double[,] costs, int iterations, int seed)
    {
        if (iterations <= 0 || a.Length < MinSamplesForBootstrap) return null;

        var random = new Random(seed);
        var kappas = new List<double>(iterations);
        var resampleA = new int[a.Length];
        var resampleB = new int[a.Length];

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            for (var i = 0; i < a.Length; i++)
            {
                // Resample *pairs*, never the two raters independently — drawing each rater's
                // ratings separately would break the pairing that κ is entirely about and
                // quietly measure agreement between two unrelated rating sequences.
                var pick = random.Next(a.Length);
                resampleA[i] = a[pick];
                resampleB[i] = b[pick];
            }

            var (kappa, _, _) = Score(Confusion(resampleA, resampleB, categories), a.Length, costs);
            if (kappa is { } value) kappas.Add(value);
        }

        // Some resamples land entirely in one band and have no κ; a handful is normal. Once
        // most of them degenerate, the surviving resamples are a biased subset and an interval
        // built from them would read as precision the data does not have.
        if (kappas.Count < iterations / 2) return null;

        return (CopilotSession.Percentile(kappas, 0.025),
                CopilotSession.Percentile(kappas, 0.975));
    }

    private static IReadOnlyList<IReadOnlyList<int>> ToJagged(int[,] confusion)
    {
        var categories = confusion.GetLength(0);
        var rows = new List<IReadOnlyList<int>>(categories);
        for (var i = 0; i < categories; i++)
        {
            var row = new int[categories];
            for (var j = 0; j < categories; j++) row[j] = confusion[i, j];
            rows.Add(row);
        }
        return rows;
    }
}

/// <summary>Agreement between one named pair of raters.</summary>
public sealed record PairAgreement(string RaterA, string RaterB, AgreementResult Agreement);
