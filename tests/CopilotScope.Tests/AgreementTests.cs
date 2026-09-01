using CopilotScope.Collector.Calibration;
using CopilotScope.JudgeAgent.Calibration;
using Xunit;

namespace CopilotScope.Tests;

// The arithmetic behind every calibration verdict. Anchored on published worked examples and
// hand-computed weighted values rather than on whatever the implementation happened to return
// first — a κ implementation that is merely self-consistent would certify a judge just as
// confidently while being wrong.
public class AgreementTests
{
    /// <summary>Expands a confusion matrix back into the two rating vectors that produce it, so
    /// tests can be written in the form textbooks state their examples.</summary>
    private static (List<int> A, List<int> B) FromConfusion(int[,] confusion)
    {
        List<int> a = [], b = [];
        for (var i = 0; i < confusion.GetLength(0); i++)
            for (var j = 0; j < confusion.GetLength(1); j++)
                for (var n = 0; n < confusion[i, j]; n++)
                {
                    a.Add(i);
                    b.Add(j);
                }
        return (a, b);
    }

    // ------------------------------------------------------------ published values

    [Fact]
    public void Cohen_MatchesTheStandardTwoByTwoWorkedExample()
    {
        // 50 items: both yes 20, A yes/B no 5, A no/B yes 10, both no 15.
        // p_o = 35/50 = 0.70; p_e = 0.5·0.6 + 0.5·0.4 = 0.50; κ = 0.20/0.50 = 0.40.
        var (a, b) = FromConfusion(new[,] { { 15, 10 }, { 5, 20 } });

        var result = Agreement.Cohen(a, b, categories: 2, bootstrapIterations: 0);

        Assert.Equal("ok", result.Status);
        Assert.Equal(0.40, result.Kappa!.Value, 4);
        Assert.Equal(0.70, result.ObservedAgreement, 4);
        Assert.Equal(0.50, result.ExpectedAgreement, 4);
        Assert.Equal(50, result.Samples);
        // Landis & Koch put 0.21–0.40 in "fair"; 0.40 sits on that band's upper edge.
        Assert.Equal("fair", result.Interpretation);
    }

    [Fact]
    public void Cohen_MatchesTheWorkedExampleWhereRawAgreementMisleads()
    {
        // 100 items, 60% raw agreement — but skewed marginals mean chance alone explains 54% of
        // it, so κ collapses to 0.13. This is the case that makes raw agreement unusable.
        var (a, b) = FromConfusion(new[,] { { 15, 25 }, { 15, 45 } });

        var result = Agreement.Cohen(a, b, categories: 2, bootstrapIterations: 0);

        Assert.Equal(0.6, result.ObservedAgreement, 4);
        Assert.Equal(0.1304, result.Kappa!.Value, 4);
        Assert.Equal("slight", result.Interpretation);
    }

    [Fact]
    public void Cohen_WeightedVariantsMatchHandComputedValues()
    {
        // 3 bands, 10 items, every disagreement adjacent:
        //   [[3,1,0],
        //    [1,2,1],
        //    [0,1,1]]
        // unweighted  p_o 0.60, p_e 0.36            -> 0.24/0.64      = 0.375
        // linear      obs 0.20, exp 0.40            -> 1 - 0.5        = 0.5
        // quadratic   obs 0.10, exp 0.28            -> 1 - 0.357142…  = 0.642857…
        var (a, b) = FromConfusion(new[,] { { 3, 1, 0 }, { 1, 2, 1 }, { 0, 1, 1 } });

        var unweighted = Agreement.Cohen(a, b, 3, KappaWeighting.Unweighted, bootstrapIterations: 0);
        var linear = Agreement.Cohen(a, b, 3, KappaWeighting.Linear, bootstrapIterations: 0);
        var quadratic = Agreement.Cohen(a, b, 3, KappaWeighting.Quadratic, bootstrapIterations: 0);

        Assert.Equal(0.375, unweighted.Kappa!.Value, 4);
        Assert.Equal(0.5, linear.Kappa!.Value, 4);
        Assert.Equal(0.642857, quadratic.Kappa!.Value, 4);

        // Weighting only pays off when misses are near-misses; that is the whole reason an
        // ordinal rubric reads the quadratic number.
        Assert.True(quadratic.Kappa > linear.Kappa && linear.Kappa > unweighted.Kappa);
    }

    // -------------------------------------------------------------------- extremes

    [Fact]
    public void Cohen_IsOneOnPerfectAgreement()
    {
        List<int> ratings = [0, 1, 2, 3, 0, 1, 2, 3];

        var result = Agreement.Cohen(ratings, ratings, 4, bootstrapIterations: 0);

        Assert.Equal(1.0, result.Kappa!.Value, 6);
        Assert.Equal("almost perfect", result.Interpretation);
    }

    [Fact]
    public void Cohen_IsZeroWhenAgreementIsExactlyWhatChancePredicts()
    {
        // Every cell equals N·r_i·c_j, so observed agreement is precisely the expected amount.
        List<int> a = [0, 0, 1, 1];
        List<int> b = [0, 1, 0, 1];

        var result = Agreement.Cohen(a, b, 2, bootstrapIterations: 0);

        Assert.Equal(0.0, result.Kappa!.Value, 6);
        Assert.Equal(0.5, result.ObservedAgreement, 6);
        Assert.Equal(0.5, result.ExpectedAgreement, 6);
    }

    [Fact]
    public void Cohen_GoesNegativeWhenRatersSystematicallyDisagree()
    {
        List<int> a = [0, 0, 1, 1];
        List<int> b = [1, 1, 0, 0];

        var result = Agreement.Cohen(a, b, 2, bootstrapIterations: 0);

        Assert.Equal(-1.0, result.Kappa!.Value, 6);
        Assert.Equal("poor (worse than chance)", result.Interpretation);
    }

    [Fact]
    public void Cohen_ReportsUndefinedRatherThanPerfectWhenEveryRatingLandsInOneBand()
    {
        // Both raters called all ten sessions "mostly". Raw agreement is 100%, but there is no
        // variance to be chance-corrected against and κ is 0/0. Reporting 1.0 here would be the
        // single most dangerous thing this class could do: it would certify a judge on a sample
        // that demonstrates nothing.
        List<int> a = [2, 2, 2, 2, 2, 2, 2, 2, 2, 2];

        var result = Agreement.Cohen(a, a, 4);

        Assert.Equal("undefined", result.Status);
        Assert.Null(result.Kappa);
        Assert.Equal(1.0, result.ObservedAgreement, 6);
        Assert.Contains("degenerate", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cohen_ReturnsNoDataForAnEmptySample()
    {
        var result = Agreement.Cohen([], [], 4);

        Assert.Equal("no-data", result.Status);
        Assert.Null(result.Kappa);
        Assert.Equal(0, result.Samples);
    }

    // ------------------------------------------------------------------ invariants

    [Fact]
    public void Cohen_IsSymmetricInItsRaters()
    {
        var (a, b) = FromConfusion(new[,] { { 3, 1, 0 }, { 1, 2, 1 }, { 0, 1, 1 } });

        foreach (var weighting in Enum.GetValues<KappaWeighting>())
            Assert.Equal(
                Agreement.Cohen(a, b, 3, weighting, bootstrapIterations: 0).Kappa!.Value,
                Agreement.Cohen(b, a, 3, weighting, bootstrapIterations: 0).Kappa!.Value, 9);
    }

    [Fact]
    public void Cohen_WeightingIsIrrelevantOnATwoBandScale()
    {
        // With two bands the only possible miss is already the maximum one, so all three
        // weightings must collapse to the same number. A weighting bug that scales the cost
        // matrix wrongly shows up here immediately.
        var (a, b) = FromConfusion(new[,] { { 15, 10 }, { 5, 20 } });

        var unweighted = Agreement.Cohen(a, b, 2, KappaWeighting.Unweighted, bootstrapIterations: 0);
        var linear = Agreement.Cohen(a, b, 2, KappaWeighting.Linear, bootstrapIterations: 0);
        var quadratic = Agreement.Cohen(a, b, 2, KappaWeighting.Quadratic, bootstrapIterations: 0);

        Assert.Equal(unweighted.Kappa!.Value, linear.Kappa!.Value, 9);
        Assert.Equal(unweighted.Kappa!.Value, quadratic.Kappa!.Value, 9);
    }

    [Fact]
    public void Cohen_ConfusionMatrixCountsRowsAsTheFirstRater()
    {
        List<int> a = [0, 0, 0];
        List<int> b = [1, 1, 2];

        var matrix = Agreement.Cohen(a, b, 3, bootstrapIterations: 0).ConfusionMatrix;

        Assert.Equal(2, matrix[0][1]);
        Assert.Equal(1, matrix[0][2]);
        Assert.Equal(0, matrix[1][0]);
    }

    // ------------------------------------------------------------------- bootstrap

    [Fact]
    public void Bootstrap_ProducesTheSameIntervalForTheSameSeed()
    {
        var (a, b) = FromConfusion(new[,] { { 12, 4, 1 }, { 3, 14, 4 }, { 1, 3, 12 } });

        var first = Agreement.Cohen(a, b, 3, KappaWeighting.Quadratic);
        var second = Agreement.Cohen(a, b, 3, KappaWeighting.Quadratic);

        Assert.Equal(first.CiLow, second.CiLow);
        Assert.Equal(first.CiHigh, second.CiHigh);
        Assert.NotNull(first.CiLow);
    }

    [Fact]
    public void Bootstrap_ShiftsWithTheSeedButStillBracketsTheEstimate()
    {
        var (a, b) = FromConfusion(new[,] { { 12, 4, 1 }, { 3, 14, 4 }, { 1, 3, 12 } });

        var result = Agreement.Cohen(a, b, 3, KappaWeighting.Quadratic, seed: 12345);

        Assert.True(result.CiLow <= result.Kappa!.Value, $"CI low {result.CiLow} above κ {result.Kappa}");
        Assert.True(result.CiHigh >= result.Kappa!.Value, $"CI high {result.CiHigh} below κ {result.Kappa}");
    }

    [Fact]
    public void Bootstrap_NarrowsAsTheSampleGrows()
    {
        // Same agreement structure, ten times the items: the point estimate barely moves, the
        // interval should tighten a lot. This is the property that makes the interval worth
        // reporting at all — it is what tells a reader whether κ 0.72 is a result or a rumour.
        var small = FromConfusion(new[,] { { 6, 2, 0 }, { 2, 7, 2 }, { 0, 2, 6 } });
        var large = FromConfusion(new[,] { { 60, 20, 0 }, { 20, 70, 20 }, { 0, 20, 60 } });

        var narrow = Agreement.Cohen(large.A, large.B, 3, KappaWeighting.Quadratic);
        var wide = Agreement.Cohen(small.A, small.B, 3, KappaWeighting.Quadratic);

        Assert.Equal(wide.Kappa!.Value, narrow.Kappa!.Value, 1);
        Assert.True(narrow.CiHigh - narrow.CiLow < (wide.CiHigh - wide.CiLow) / 2,
            $"expected the larger sample's interval to at least halve: {narrow.CiLow}–{narrow.CiHigh} vs {wide.CiLow}–{wide.CiHigh}");
    }

    [Fact]
    public void Bootstrap_IsSkippedForSamplesTooSmallToResample()
    {
        List<int> a = [0, 1, 2, 3];
        List<int> b = [0, 1, 2, 2];

        var result = Agreement.Cohen(a, b, 4);

        Assert.Equal("ok", result.Status);
        Assert.Null(result.CiLow);
        Assert.Contains("no bootstrap interval", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void Cohen_RejectsRatersWhoRatedDifferentNumbersOfItems()
    {
        var error = Assert.Throws<ArgumentException>(() => Agreement.Cohen([0, 1], [0], 2));
        Assert.Contains("same items", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cohen_RejectsBandsOutsideTheScale()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Agreement.Cohen([0, 4], [0, 1], 4));
        Assert.Contains("outside the scale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cohen_RejectsAScaleWithNothingToDisagreeAbout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Agreement.Cohen([0], [0], categories: 1));
    }

    // -------------------------------------------------------------------- pairwise

    [Fact]
    public void Pairwise_ScoresEachPairOnTheSessionsBothOfThemRated()
    {
        // Cara only rated two of the four sessions. Her pairs must be scored on that overlap —
        // padding her gaps with a default band would manufacture agreement out of absence.
        var raters = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["alice"] = new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 1, ["s3"] = 2, ["s4"] = 3 },
            ["bob"] = new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 1, ["s3"] = 2, ["s4"] = 3 },
            ["cara"] = new Dictionary<string, int> { ["s1"] = 3, ["s2"] = 0 }
        };

        var (pairs, mean) = Agreement.Pairwise(raters, 4, bootstrapIterations: 0);

        Assert.Equal(3, pairs.Count);
        var aliceBob = pairs.Single(p => p is { RaterA: "alice", RaterB: "bob" });
        var aliceCara = pairs.Single(p => p is { RaterA: "alice", RaterB: "cara" });

        Assert.Equal(4, aliceBob.Agreement.Samples);
        Assert.Equal(1.0, aliceBob.Agreement.Kappa!.Value, 6);
        Assert.Equal(2, aliceCara.Agreement.Samples);
        Assert.NotNull(mean);
    }

    [Fact]
    public void Pairwise_OrdersPairsStablyRegardlessOfInsertionOrder()
    {
        var forward = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["zoe"] = new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 2 },
            ["alice"] = new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 1 }
        };
        var reversed = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["alice"] = forward["alice"],
            ["zoe"] = forward["zoe"]
        };

        var first = Agreement.Pairwise(forward, 4, bootstrapIterations: 0).Pairs.Single();
        var second = Agreement.Pairwise(reversed, 4, bootstrapIterations: 0).Pairs.Single();

        Assert.Equal("alice", first.RaterA);
        Assert.Equal("zoe", first.RaterB);
        Assert.Equal(first.RaterA, second.RaterA);
    }

    // ----------------------------------------------------------------------- bands

    [Theory]
    [InlineData(0.00, 0)]
    [InlineData(0.39, 0)]
    [InlineData(0.40, 1)]
    [InlineData(0.64, 1)]
    [InlineData(0.65, 2)]
    [InlineData(0.84, 2)]
    [InlineData(0.85, 3)]
    [InlineData(1.00, 3)]
    public void Band_BinsJudgeScoresOnTheDocumentedBoundaries(double score, int expected)
        => Assert.Equal(expected, RubricScale.Band(score));

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.7, 3)]
    public void Band_ClampsScoresThatBrokeTheJudgeContract(double score, int expected)
        => Assert.Equal(expected, RubricScale.Band(score));

    [Fact]
    public void RubricScale_CoversEveryAlgorithmTheJudgePromptEmits()
    {
        // The prompt template is the spec; if a rubric is added there and not here, its labels
        // would be scored with no anchor question and silently marked "unknown rubric".
        string[] emitted = ["G-Eval", "SPUR", "RAGAS", "deep-friction", "task-completion"];

        Assert.All(emitted, algorithm => Assert.NotNull(RubricScale.Find(algorithm)));
        Assert.Equal(emitted.Length, RubricScale.Rubrics.Count);
        Assert.Equal(RubricScale.Categories, RubricScale.Bands.Count);
    }

    [Fact]
    public void RubricScale_MarksFrictionAsTheRubricThatRunsTheOtherWay()
    {
        // The trap this guards: the judge is correct to score a session that went straight
        // through 0.1 on this rubric. A labeller reading band 3 as "great session" would be
        // recorded as maximally disagreeing with a judge that got it right.
        Assert.False(RubricScale.Find("deep-friction")!.HigherIsBetter);
        Assert.True(RubricScale.Find("G-Eval")!.HigherIsBetter);
        Assert.Contains("repair", RubricScale.Find("deep-friction")!.Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RubricScale_KeepsNoEmotionalVocabularyInItsQuestions()
    {
        // EU AI Act Art. 5(1)(f) prohibits workplace emotion recognition outright. The rubric
        // measures repair work and always did; the question text is what a labeller reads, so
        // it is the surface that decides whether they grade behaviour or mood.
        string[] emotional = ["frustrat", "angry", "upset", "mood", "emotion", "feel"];
        foreach (var rubric in RubricScale.Rubrics.Values)
            Assert.All(emotional, word =>
                Assert.DoesNotContain(word, rubric.Question, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RubricScale_StillResolvesTheRetiredFrustrationId()
    {
        // Human labels are expensive to collect. A label file written before the rename must
        // keep resolving, or those sessions drop out of the agreement statistics and the
        // report reads as a judge that got worse.
        Assert.Same(RubricScale.Find("deep-friction"), RubricScale.Find("deep-frustration"));
        Assert.Equal("deep-friction", RubricScale.Canonical("deep-frustration"));
        Assert.Equal("G-Eval", RubricScale.Canonical("G-Eval"));
    }
}
