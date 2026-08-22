namespace CopilotScope.JudgeAgent.Calibration;

/// <summary>
/// Bound from <c>CopilotScope:JudgeAgent:Calibration</c>. Thresholds live in config rather than
/// in the arithmetic because "how much agreement is enough" is a policy question — it depends
/// on what the judge's score is allowed to decide — while κ itself is not.
/// </summary>
public sealed class CalibrationOptions
{
    /// <summary>
    /// Minimum κ for a rubric to count as calibrated, applied to both the human ceiling and the
    /// judge's agreement with it.
    ///
    /// <para>0.7 is this repo's own published acceptance criterion, not a borrowed default:
    /// <c>research/articles/thesis_topics.tex</c> §551 and
    /// <c>research/RESEARCH_PROPOSALS.md</c> §207 both set the bar there. It sits inside
    /// Landis &amp; Koch's "substantial" band.</para>
    /// </summary>
    public double MinKappa { get; set; } = 0.70;

    /// <summary>
    /// Minimum paired sessions before a κ is allowed to mean anything. Below this the report
    /// still shows the number — hiding it would make a thin pilot run look like no run at all —
    /// but the verdict stays <c>insufficient-data</c>.
    ///
    /// <para>20 is a floor for reading a number at all, not the target. The repo's research plan
    /// calls for 50–100 labelled sessions (<c>research/RESEARCH_PROPOSALS.md</c> §40).</para>
    /// </summary>
    public int MinPairedSessions { get; set; } = 20;

    /// <summary>Bootstrap resamples behind each confidence interval. 0 disables intervals.</summary>
    public int BootstrapIterations { get; set; } = Agreement.DefaultBootstrapIterations;

    /// <summary>Bootstrap seed. Fixed by default so a re-run on unchanged labels reproduces the
    /// interval exactly; a calibration number that drifts on its own cannot be a baseline.</summary>
    public int BootstrapSeed { get; set; } = Agreement.DefaultSeed;
}
