using CopilotScope.Collector.Calibration;
using CopilotScope.JudgeAgent.Calibration;
using Xunit;

namespace CopilotScope.Tests;

// The verdict logic on top of Agreement's arithmetic. What matters here is that the engine
// refuses to certify in every situation where the number looks good but means nothing — a
// single labeller, a panel that cannot agree with itself, a sample too thin to read.
public class CalibrationEngineTests
{
    private const string Rubric = "G-Eval";

    /// <summary>Mid-band judge scores, so a score maps back to the band it was built from.</summary>
    private static double ScoreFor(int band) => band switch
    {
        0 => 0.20,
        1 => 0.52,
        2 => 0.75,
        _ => 0.92
    };

    /// <summary>Builds a dataset of <paramref name="sessions"/> items whose true band cycles
    /// 0–3, with each rater and the judge derived from that band by a caller-supplied shift.</summary>
    private static CalibrationDataset Build(
        int sessions,
        IReadOnlyDictionary<string, Func<int, int, int>> raters,
        Func<int, int, int>? judge = null,
        int judgeScoreLimit = int.MaxValue,
        string rubric = Rubric)
    {
        List<HumanLabel> labels = [];
        List<JudgeScore> scores = [];

        for (var i = 0; i < sessions; i++)
        {
            var id = $"seed-{i:D3}";
            var trueBand = i % 4;

            foreach (var (rater, shift) in raters)
                labels.Add(new HumanLabel(id, rater, rubric, shift(trueBand, i)));

            if (i < judgeScoreLimit)
                scores.Add(new JudgeScore(id, rubric, ScoreFor(judge is null ? trueBand : judge(trueBand, i))));
        }

        return new CalibrationDataset(labels, scores, "test-v1", "gpt-test", "prompt-v1");
    }

    private static readonly Dictionary<string, Func<int, int, int>> AgreeingPanel = new()
    {
        // Bob differs from Alice by one band on two of every 24 sessions — a real panel, not a
        // copy-paste of one person's opinion.
        ["alice"] = (band, _) => band,
        ["bob"] = (band, i) => i % 12 == 5 ? Math.Min(3, band + 1) : band
    };

    // -------------------------------------------------------------------- verdicts

    [Fact]
    public void Evaluate_CertifiesAJudgeThatTracksAPanelAgreeingWithItself()
    {
        var dataset = Build(24, AgreeingPanel, judge: (band, i) => i % 11 == 4 ? Math.Min(3, band + 1) : band);

        var report = new CalibrationEngine().Evaluate(dataset);
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(CalibrationVerdict.Calibrated, rubric.Verdict);
        Assert.Equal(CalibrationVerdict.Calibrated, report.Verdict);
        Assert.True(rubric.Headline!.Kappa >= 0.7, $"κ was {rubric.Headline.Kappa}");
        Assert.Equal("ok", rubric.HumanCeiling.Status);
        Assert.True(rubric.HumanCeiling.MeanKappa >= 0.7);
        Assert.Equal(24, rubric.PairedSessions);
    }

    [Fact]
    public void Evaluate_RefusesToCertifyWhenTheHumansCannotAgreeWithEachOther()
    {
        // Bob is two bands off Alice everywhere. Whatever the judge's own κ turns out to be, the
        // labels are not a ground truth, so there is nothing to validate against — this repo's
        // own thesis notes call low human agreement a result in its own right.
        var panel = new Dictionary<string, Func<int, int, int>>
        {
            ["alice"] = (band, _) => band,
            ["bob"] = (band, _) => (band + 2) % 4
        };

        var report = new CalibrationEngine().Evaluate(Build(24, panel));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(CalibrationVerdict.CeilingTooLow, rubric.Verdict);
        Assert.Equal(CalibrationVerdict.CeilingTooLow, report.Verdict);
        Assert.Contains("agrees with itself", rubric.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_FailsAJudgeThatDoesNotTrackTheHumans()
    {
        var report = new CalibrationEngine().Evaluate(
            Build(24, AgreeingPanel, judge: (band, _) => (band + 2) % 4));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(CalibrationVerdict.NotCalibrated, rubric.Verdict);
        Assert.Contains("must not gate anything", rubric.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WillNotCertifyOnOneLabellersOpinion()
    {
        // The judge agrees perfectly — and it still cannot be certified, because there is no
        // second labeller to show the labels are reproducible.
        var solo = new Dictionary<string, Func<int, int, int>> { ["alice"] = (band, _) => band };

        var report = new CalibrationEngine().Evaluate(Build(24, solo));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(1.0, rubric.Headline!.Kappa!.Value, 6);
        Assert.Equal(CalibrationVerdict.InsufficientData, rubric.Verdict);
        Assert.Equal("single-rater", rubric.HumanCeiling.Status);
        Assert.Contains("second labeller", rubric.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WillNotCertifyBelowTheSessionMinimum()
    {
        var report = new CalibrationEngine().Evaluate(Build(12, AgreeingPanel));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(CalibrationVerdict.InsufficientData, rubric.Verdict);
        Assert.Contains("20-session minimum", rubric.Detail);
        // The number is still shown: hiding a pilot run would read as "no run at all".
        Assert.NotNull(rubric.Headline!.Kappa);
    }

    [Fact]
    public void Evaluate_HonoursAConfiguredThresholdForTheJudge()
    {
        // Panel in lockstep, so its ceiling clears any threshold and only the judge's own κ
        // (~0.87 with a miss every third session) decides the verdict.
        var lockstep = new Dictionary<string, Func<int, int, int>>
        {
            ["alice"] = (band, _) => band,
            ["bob"] = (band, _) => band
        };
        var dataset = Build(24, lockstep, judge: (band, i) => i % 3 == 0 ? Math.Min(3, band + 1) : band);

        var strict = new CalibrationEngine(new CalibrationOptions { MinKappa = 0.99 }).Evaluate(dataset);
        var lenient = new CalibrationEngine(new CalibrationOptions { MinKappa = 0.50 }).Evaluate(dataset);

        Assert.Equal(CalibrationVerdict.NotCalibrated, strict.Rubrics[0].Verdict);
        Assert.Equal(CalibrationVerdict.Calibrated, lenient.Rubrics[0].Verdict);
        Assert.Equal(0.99, strict.MinKappa);
    }

    [Fact]
    public void Evaluate_AppliesTheThresholdToTheCeilingBeforeTheJudge()
    {
        // The same threshold gates both, and the ceiling is checked first: a judge that tracks
        // its panel perfectly still cannot be certified when the panel is not reproducible
        // enough to be a ground truth at the bar the caller set.
        var dataset = Build(24, AgreeingPanel);

        var report = new CalibrationEngine(new CalibrationOptions { MinKappa = 0.99 }).Evaluate(dataset);
        var rubric = Assert.Single(report.Rubrics);

        Assert.True(rubric.HumanCeiling.MeanKappa < 0.99, $"panel κ was {rubric.HumanCeiling.MeanKappa}");
        Assert.Equal(CalibrationVerdict.CeilingTooLow, rubric.Verdict);
        Assert.Contains("not a ground truth yet", rubric.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_RefusesToCertifyWhenThePanelPutEverythingInOneBand()
    {
        // Two labellers who both graded every session "mostly" agree 100% of the time and have
        // demonstrated nothing — there was no disagreement available to survive. κ is 0/0 for
        // every pair, and the correct reading is that there is no ground truth here, not that
        // the panel is flawless.
        var flat = new Dictionary<string, Func<int, int, int>>
        {
            ["alice"] = (_, _) => 2,
            ["bob"] = (_, _) => 2
        };

        var report = new CalibrationEngine().Evaluate(Build(24, flat));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal("ok", rubric.HumanCeiling.Status);
        Assert.Null(rubric.HumanCeiling.MeanKappa);
        Assert.Equal(CalibrationVerdict.CeilingTooLow, rubric.Verdict);
        Assert.Contains("single band", rubric.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- data handling

    [Fact]
    public void Evaluate_ReportsLabelledSessionsTheJudgeNeverScored()
    {
        // RAGAS on sessions with no retrieval context is the real case: the judge returns
        // "no-data" and those sessions cannot be paired. Counted, never quietly absorbed.
        var report = new CalibrationEngine().Evaluate(Build(24, AgreeingPanel, judgeScoreLimit: 20));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(24, rubric.LabelledSessions);
        Assert.Equal(20, rubric.PairedSessions);
        Assert.Equal(4, rubric.DroppedForMissingJudgeScore);
        Assert.Contains("no judge score", rubric.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_TakesTheMedianBandAsThePanelsConsensus()
    {
        // Three raters split 0 / 2 / 3 on every session; the median is 2. The judge is scored
        // at band 2 throughout, so a correct median yields perfect agreement and a mean-based
        // consensus (which would land near band 2 but drift) would not.
        List<HumanLabel> labels = [];
        List<JudgeScore> scores = [];
        for (var i = 0; i < 24; i++)
        {
            var id = $"seed-{i:D3}";
            // Vary which band the trio brackets so the sample is not degenerate.
            var offset = i % 2;
            labels.Add(new HumanLabel(id, "alice", Rubric, 0 + offset));
            labels.Add(new HumanLabel(id, "bob", Rubric, 1 + offset));
            labels.Add(new HumanLabel(id, "cara", Rubric, 2 + offset));
            scores.Add(new JudgeScore(id, Rubric, ScoreFor(1 + offset)));
        }

        var rubric = Assert.Single(new CalibrationEngine().Evaluate(new CalibrationDataset(labels, scores)).Rubrics);

        Assert.Equal(1.0, rubric.Headline!.Kappa!.Value, 6);
        Assert.Equal(3, rubric.HumanCeiling.Raters.Count);
        Assert.Equal(3, rubric.HumanCeiling.Pairs.Count);
    }

    [Fact]
    public void Evaluate_LetsARaterReviseTheirOwnGrade()
    {
        List<HumanLabel> labels = [];
        List<JudgeScore> scores = [];
        for (var i = 0; i < 24; i++)
        {
            var id = $"seed-{i:D3}";
            var band = i % 4;
            labels.Add(new HumanLabel(id, "alice", Rubric, (band + 2) % 4, "first pass, misread the rubric"));
            labels.Add(new HumanLabel(id, "alice", Rubric, band, "corrected"));
            labels.Add(new HumanLabel(id, "bob", Rubric, band));
            scores.Add(new JudgeScore(id, Rubric, ScoreFor(band)));
        }

        var rubric = Assert.Single(new CalibrationEngine().Evaluate(new CalibrationDataset(labels, scores)).Rubrics);

        Assert.Equal(CalibrationVerdict.Calibrated, rubric.Verdict);
        Assert.Equal(1.0, rubric.HumanCeiling.MeanKappa!.Value, 6);
    }

    [Fact]
    public void Evaluate_MatchesRubricIdsWithoutCaringAboutCase()
    {
        var labels = Enumerable.Range(0, 24)
            .SelectMany(i => new[]
            {
                new HumanLabel($"seed-{i:D3}", "alice", "g-eval", i % 4),
                new HumanLabel($"seed-{i:D3}", "bob", "g-eval", i % 4)
            }).ToList();
        var scores = Enumerable.Range(0, 24)
            .Select(i => new JudgeScore($"seed-{i:D3}", "G-Eval", ScoreFor(i % 4)))
            .ToList();

        var report = new CalibrationEngine().Evaluate(new CalibrationDataset(labels, scores));

        Assert.Empty(report.Warnings);
        Assert.Equal(24, report.Rubrics[0].PairedSessions);
    }

    [Fact]
    public void Evaluate_WarnsWhenLabelsNameARubricTheJudgeDoesNotEmit()
    {
        var dataset = Build(24, AgreeingPanel, rubric: "vibes");

        var report = new CalibrationEngine().Evaluate(dataset);

        Assert.Contains(report.Warnings, w => w.Contains("vibes", StringComparison.Ordinal));
        Assert.Contains("unknown rubric", report.Rubrics[0].Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_TakesTheWorstRubricVerdictAsTheOverallOne()
    {
        var good = Build(24, AgreeingPanel, rubric: "G-Eval");
        var bad = Build(24, AgreeingPanel, judge: (band, _) => (band + 2) % 4, rubric: "SPUR");

        var report = new CalibrationEngine().Evaluate(new CalibrationDataset(
            [.. good.Labels, .. bad.Labels], [.. good.JudgeScores, .. bad.JudgeScores]));

        Assert.Equal(2, report.Rubrics.Count);
        Assert.Equal(CalibrationVerdict.Calibrated, report.Rubrics.Single(r => r.Algorithm == "G-Eval").Verdict);
        Assert.Equal(CalibrationVerdict.NotCalibrated, report.Rubrics.Single(r => r.Algorithm == "SPUR").Verdict);
        Assert.Equal(CalibrationVerdict.NotCalibrated, report.Verdict);
    }

    [Fact]
    public void Evaluate_IsAPureFunctionOfItsDataset()
    {
        // A calibration you cannot re-run to the same number is not a baseline anyone can
        // regress against, so this covers the seed, the ordering and the tie-breaks at once.
        var dataset = Build(24, AgreeingPanel, judge: (band, i) => i % 7 == 3 ? Math.Min(3, band + 1) : band);

        var first = new CalibrationEngine().Evaluate(dataset);
        var second = new CalibrationEngine().Evaluate(dataset);

        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal(first.Rubrics[0].Headline!.Kappa, second.Rubrics[0].Headline!.Kappa);
        Assert.Equal(first.Rubrics[0].Headline!.CiLow, second.Rubrics[0].Headline!.CiLow);
        Assert.Equal(first.Rubrics[0].Headline!.CiHigh, second.Rubrics[0].Headline!.CiHigh);
        Assert.Equal(first.Rubrics[0].HumanCeiling.MeanKappa, second.Rubrics[0].HumanCeiling.MeanKappa);
    }

    [Fact]
    public void Evaluate_ReportsAllThreeWeightingsAndReadsTheVerdictFromTheQuadratic()
    {
        var report = new CalibrationEngine().Evaluate(Build(24, AgreeingPanel));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(3, rubric.JudgeVsHuman.Count);
        Assert.Contains(rubric.JudgeVsHuman, a => a.Weighting == "unweighted");
        Assert.Contains(rubric.JudgeVsHuman, a => a.Weighting == "linear");
        Assert.Equal("quadratic", rubric.Headline!.Weighting);
    }

    [Fact]
    public void Evaluate_EchoesProvenanceSoAStoredReportStaysReadable()
    {
        // A κ belongs to the exact model and prompt that earned it; without those a stored
        // report is unreadable six weeks later.
        var report = new CalibrationEngine().Evaluate(Build(24, AgreeingPanel));

        Assert.Equal("test-v1", report.DatasetVersion);
        Assert.Equal("gpt-test", report.JudgeModel);
        Assert.Equal("prompt-v1", report.JudgePromptVersion);
        Assert.Equal(RubricScale.Categories, report.Scale.Categories);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void Evaluate_RejectsLabelsOutsideTheScale()
    {
        var dataset = new CalibrationDataset(
            [new HumanLabel("s1", "alice", Rubric, 4)], []);

        var error = Assert.Throws<ArgumentException>(() => new CalibrationEngine().Evaluate(dataset));
        Assert.Contains("outside the 0..3 scale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_RejectsTwoJudgeScoresForTheSameSessionAndRubric()
    {
        // Usually two runs concatenated. Picking one silently would make the report depend on
        // list order, which is exactly the kind of drift a baseline cannot tolerate.
        var dataset = new CalibrationDataset(
            [new HumanLabel("s1", "alice", Rubric, 2)],
            [new JudgeScore("s1", Rubric, 0.9), new JudgeScore("s1", Rubric, 0.2)]);

        var error = Assert.Throws<ArgumentException>(() => new CalibrationEngine().Evaluate(dataset));
        Assert.Contains("more than one judge score", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_HandlesAnEmptyDataset()
    {
        var report = new CalibrationEngine().Evaluate(new CalibrationDataset([], []));

        Assert.Empty(report.Rubrics);
        Assert.Equal(CalibrationVerdict.InsufficientData, report.Verdict);
        Assert.Contains("Nothing to calibrate", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_HandlesALabelledSessionSetWithNoJudgeScoresAtAll()
    {
        var report = new CalibrationEngine().Evaluate(Build(24, AgreeingPanel, judgeScoreLimit: 0));
        var rubric = Assert.Single(report.Rubrics);

        Assert.Equal(0, rubric.PairedSessions);
        Assert.Equal(24, rubric.DroppedForMissingJudgeScore);
        Assert.Equal(CalibrationVerdict.InsufficientData, rubric.Verdict);
    }
}
