using CopilotScope.Collector.Calibration;

namespace CopilotScope.JudgeAgent.Calibration;

// ------------------------------------------------------------------------ input

/// <summary>One human's grade for one rubric on one session — the atom of the calibration
/// dataset. Kept as flat data rather than a nested per-session shape so labels can be appended
/// by different people, at different times, without merging structures.</summary>
/// <param name="SessionId">Collector session id the label applies to.</param>
/// <param name="Rater">Stable identifier for the labeller. Any string; used to pair raters.</param>
/// <param name="Algorithm">Rubric id, matching <see cref="RubricScale.Rubrics"/>.</param>
/// <param name="Level">Band index on the shared ordinal scale, 0..<see cref="RubricScale.Categories"/>−1.</param>
/// <param name="Note">Optional free text — why this band. Read when judge and human diverge.</param>
public sealed record HumanLabel(string SessionId, string Rater, string Algorithm, int Level, string? Note = null);

/// <summary>One judge score for one rubric on one session, as emitted by
/// <c>POST /api/sessions/{id}/judge</c>.</summary>
public sealed record JudgeScore(string SessionId, string Algorithm, double Score);

/// <summary>
/// A complete calibration run's input: the human labels, the judge scores they are compared
/// against, and the provenance needed to read the result later.
///
/// <para>Judge scores are supplied rather than fetched so the arithmetic is a pure function of
/// its inputs — the same dataset always produces the same report, no model access required.
/// That is what makes calibration runnable in CI and reviewable in a pull request. Producing
/// the scores in the first place is the separate, expensive step (<c>/api/calibration/run</c>).</para>
/// </summary>
/// <param name="Labels">Every human label in the dataset.</param>
/// <param name="JudgeScores">Judge output for the same sessions.</param>
/// <param name="DatasetVersion">Free-form label for this dataset revision; echoed into the report.</param>
/// <param name="JudgeModel">Which judge model produced <paramref name="JudgeScores"/>. A κ is
/// only meaningful for the pinned model that earned it, so a report without this is unreadable
/// six weeks later.</param>
/// <param name="JudgePromptVersion">Which revision of the rubric prompt produced the scores.</param>
public sealed record CalibrationDataset(
    List<HumanLabel> Labels,
    List<JudgeScore> JudgeScores,
    string? DatasetVersion = null,
    string? JudgeModel = null,
    string? JudgePromptVersion = null);

// ----------------------------------------------------------------------- output

/// <summary>Verdict strings. Deliberately not an enum: they cross the HTTP boundary and are
/// read by humans, and the repo's existing report contracts (InsightReport.Status,
/// QualityReport.Grade) are strings for the same reason.</summary>
public static class CalibrationVerdict
{
    /// <summary>Judge agreement clears the threshold against a panel that agrees with itself.</summary>
    public const string Calibrated = "calibrated";

    /// <summary>Judge agreement is below the threshold. Its scores must not gate anything.</summary>
    public const string NotCalibrated = "not-calibrated";

    /// <summary>The humans do not agree with each other well enough to be a ground truth, so
    /// the judge cannot be validated against them at all — whatever its own κ came out as.</summary>
    public const string CeilingTooLow = "ceiling-too-low";

    /// <summary>Too few paired items to say anything. Numbers are still reported; they just
    /// do not license a conclusion.</summary>
    public const string InsufficientData = "insufficient-data";
}

/// <summary>Agreement of the human panel with itself for one rubric — the ceiling.</summary>
/// <param name="Raters">Who labelled this rubric.</param>
/// <param name="Pairs">Every rater pair's κ, so one dissenting labeller stays visible.</param>
/// <param name="MeanKappa">Mean κ across pairs; null when no pair produced a defined κ.</param>
/// <param name="Status">"ok", "single-rater" (no ceiling measurable), or "no-data".</param>
/// <param name="Detail">Plain-language reading of the above.</param>
public sealed record HumanCeiling(
    IReadOnlyList<string> Raters,
    IReadOnlyList<PairAgreement> Pairs,
    double? MeanKappa,
    string Status,
    string Detail);

/// <summary>Everything the calibration says about one rubric.</summary>
/// <param name="Algorithm">Rubric id.</param>
/// <param name="Question">What labellers were asked, verbatim.</param>
/// <param name="HigherIsBetter">Direction of this rubric's scale.</param>
/// <param name="PairedSessions">Sessions with both a human consensus and a judge score.</param>
/// <param name="LabelledSessions">Sessions with at least one human label for this rubric.</param>
/// <param name="DroppedForMissingJudgeScore">Labelled sessions the judge produced no score for
/// (typically a "no-data" rubric). Reported, never silently absorbed.</param>
/// <param name="HumanCeiling">Panel self-agreement.</param>
/// <param name="JudgeVsHuman">Judge against the human consensus, under all three weightings.</param>
/// <param name="Headline">The quadratic-weighted result — the one the verdict is read from.</param>
/// <param name="Verdict">See <see cref="CalibrationVerdict"/>.</param>
/// <param name="Detail">Why this verdict.</param>
public sealed record RubricCalibration(
    string Algorithm,
    string Question,
    bool HigherIsBetter,
    int PairedSessions,
    int LabelledSessions,
    int DroppedForMissingJudgeScore,
    HumanCeiling HumanCeiling,
    IReadOnlyList<AgreementResult> JudgeVsHuman,
    AgreementResult? Headline,
    string Verdict,
    string Detail);

/// <summary>The ordinal scale a report was computed on, echoed so a stored report stays
/// readable if the bands are ever re-cut.</summary>
public sealed record ScaleDescriptor(int Categories, IReadOnlyList<RubricBand> Bands);

/// <summary>A complete calibration report — the artefact <c>AI-EVALS.md</c> §5 means by
/// "agreement is measured and recorded before the judge's scores gate anything".</summary>
public sealed record CalibrationReport(
    string? DatasetVersion,
    string? JudgeModel,
    string? JudgePromptVersion,
    double MinKappa,
    int MinPairedSessions,
    ScaleDescriptor Scale,
    IReadOnlyList<RubricCalibration> Rubrics,
    string Verdict,
    string Summary,
    IReadOnlyList<string> Warnings);

/// <summary>Payload for <c>POST /api/calibration/run</c> — labels to calibrate against, with the
/// judge scores to be produced live from the sessions those labels name.</summary>
public sealed record CalibrationRunRequest(List<HumanLabel> Labels, string? DatasetVersion = null)
{
    /// <summary>Ceiling on sessions per run. Every session is a metered model call over a full
    /// transcript, so an unbounded batch is a way to spend a budget by accident.</summary>
    public const int MaxSessions = 200;
}
