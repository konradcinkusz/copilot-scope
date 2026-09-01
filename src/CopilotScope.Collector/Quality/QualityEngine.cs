using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>
/// Composite 0–100 session quality score, recalculated on every ingest.
///
/// v2 design notes — why not a fixed prior:
/// v1 let components without data contribute a neutral 0.7 at full weight, which
/// pinned every ordinary session (no edit/feedback telemetry yet) to ~79–80 and
/// destroyed discrimination. v2 only aggregates components that actually have
/// data and renormalizes the weights across them; missing components are still
/// reported (samples=0) but carry zero influence. A session with literally no
/// signals gets the neutral prior of 70 at confidence 0.
///
/// Base weights (renormalized over informative components):
///   reliability 0.25 — squared error-free rate (errors bite quadratically)
///   acceptance  0.20 — accepted vs rejected edits + code survival
///   friction    0.20 — mean turn score from the TFRA turn model (repair loops,
///                      error clustering, latency outliers vs session median)
///   latency     0.15 — TTFT p50 on a log-linear [0.3s..10s] curve
///   feedback    0.10 — explicit thumbs up/down
///   efficiency  0.10 — prompt-cache hit ratio + turns-per-invocation sanity
///
/// Confidence = (weight coverage of informative components) × (sample ramp),
/// so "score 91 at confidence 0.2" reads as "early but promising", not "certain".
///
/// Those base weights describe an *interactive* session. A delegated agent run has no
/// human waiting on the first token and no human accepting edits, so scoring it on
/// latency and acceptance measures a person who is not there — see
/// <see cref="ScoringProfile"/>, which supplies the weights for the session's mode.
/// </summary>
public sealed class QualityEngine
{
    private const double Prior = 0.70;

    public QualityReport Evaluate(CopilotSession s) => s.Snapshot(Compute);

    private static QualityReport Compute(CopilotSession s)
    {
        var mode = SessionModeClassifier.Classify(s);
        var profile = ScoringProfile.For(mode);
        var components = new List<QualityComponent>();

        // Every component carries the weight its mode's profile assigns; a zero weight
        // means "computed and reported, but it does not measure anything here".
        void Add(string name, double value, int samples, string detail) =>
            components.Add(new(name, profile.WeightOf(name), value, samples, detail));

        // ---- Reliability -----------------------------------------------------
        var calls = s.ChatCalls + s.ToolCalls;
        if (calls > 0)
        {
            var weightedErrors = s.ChatErrors * 2.0 + s.ToolErrors;
            var weightedCalls = s.ChatCalls * 2.0 + s.ToolCalls;
            var errorFree = Math.Clamp(1.0 - weightedErrors / Math.Max(1, weightedCalls), 0, 1);
            Add("reliability", errorFree * errorFree, calls,
                $"{s.ChatErrors} LLM err / {s.ChatCalls} calls, {s.ToolErrors} tool err / {s.ToolCalls} calls");
        }
        else Add("reliability", Prior, 0, "no calls yet");

        // ---- Acceptance --------------------------------------------------------
        // EditsAccepted/Rejected are human decisions only; permission-mode auto-accepts
        // are counted in EditsAutoAccepted and reported in the detail, never scored.
        var editSamples = s.EditsAccepted + s.EditsRejected;
        var autoNote = s.EditsAutoAccepted > 0 ? $", {s.EditsAutoAccepted} auto-applied (not scored)" : "";
        if (editSamples > 0 || s.SurvivalScores.Count > 0)
        {
            var accRatio = editSamples > 0 ? (double)s.EditsAccepted / editSamples : Prior;
            var survival = s.SurvivalScores.Count > 0 ? s.SurvivalScores.Average() : accRatio;
            Add("acceptance", Math.Clamp(0.6 * accRatio + 0.4 * survival, 0, 1),
                editSamples + s.SurvivalScores.Count,
                $"{s.EditsAccepted}✓ / {s.EditsRejected}✗ edits" +
                (s.SurvivalScores.Count > 0 ? $", survival {s.SurvivalScores.Average():P0}" : "") + autoNote);
        }
        else Add("acceptance", Prior, 0, "no edit telemetry" + autoNote);

        // ---- Friction (turn-level, TFRA-aligned) -------------------------------
        var turns = s.TurnList.Where(t => t.ChatCalls + t.ToolCalls > 0).ToList();
        if (turns.Count > 0)
        {
            var friction = turns.Average(TurnScore);
            var worst = turns.Min(TurnScore);
            Add("friction", Math.Clamp(friction, 0, 1), turns.Count,
                $"{turns.Count} turns, mean {friction:P0}, worst {worst:P0}");
        }
        else Add("friction", Prior, 0, "no completed turns");

        // ---- Latency -----------------------------------------------------------
        if (s.TtftMs.Count > 0)
        {
            var p50 = CopilotSession.Percentile(s.TtftMs, 0.5);
            // <=300 ms → 1.0, >=10 000 ms → 0.0, log-linear in between.
            var latency = p50 <= 300 ? 1.0
                        : p50 >= 10_000 ? 0.0
                        : 1.0 - Math.Log(p50 / 300.0) / Math.Log(10_000.0 / 300.0);
            Add("latency", Math.Clamp(latency, 0, 1), s.TtftMs.Count, $"TTFT p50 {p50:F0} ms");
        }
        else Add("latency", Prior, 0, "no TTFT samples");

        // ---- Explicit feedback -------------------------------------------------
        var votes = s.ThumbsUp + s.ThumbsDown;
        if (votes > 0)
            Add("feedback", (double)s.ThumbsUp / votes, votes, $"👍{s.ThumbsUp} 👎{s.ThumbsDown}");
        else Add("feedback", Prior, 0, "no votes");

        // ---- Efficiency ----------------------------------------------------------
        var promptTokens = s.InputTokens + s.CacheReadTokens;
        if (promptTokens > 0 || s.AgentInvocations > 0)
        {
            var parts = new List<double>();
            if (promptTokens > 0) parts.Add((double)s.CacheReadTokens / promptTokens);
            if (s.AgentInvocations > 0)
            {
                var turnsPerInvocation = (double)s.Turns / s.AgentInvocations;
                parts.Add(turnsPerInvocation <= 8 ? 1.0
                        : Math.Clamp(1.0 - (turnsPerInvocation - 8) / 17.0, 0, 1));
            }
            Add("efficiency", Math.Clamp(parts.Average(), 0, 1),
                (int)Math.Min(int.MaxValue, promptTokens),
                $"cache hit {(promptTokens > 0 ? (double)s.CacheReadTokens / promptTokens : 0):P0}, " +
                $"{(s.AgentInvocations > 0 ? (double)s.Turns / s.AgentInvocations : 0):F1} turns/invocation");
        }
        else Add("efficiency", Prior, 0, "no token data");

        // ---- Composite: informative components only, weights renormalized -------
        // A component needs both data AND a non-zero weight under this profile: a
        // zero-weighted one must not drag the coverage denominator either.
        var informative = components.Where(c => c.Samples > 0 && c.Weight > 0).ToList();
        double score, confidence;
        if (informative.Count == 0)
        {
            score = Prior * 100;
            confidence = 0;
        }
        else
        {
            var coverage = informative.Sum(c => c.Weight); // ≤ 1.0
            score = informative.Sum(c => c.Weight * c.Value) / coverage * 100.0;
            var sampleRamp = informative.Sum(c => c.Weight * Math.Min(1.0, c.Samples / 5.0)) / coverage;
            confidence = coverage * sampleRamp;
        }

        return new QualityReport(
            Math.Round(score, 1),
            Math.Round(confidence, 2),
            Grade(score),
            components,
            Mode: mode,
            Profile: profile.Name);
    }

    /// <summary>Per-turn friction score — same penalty model as <see cref="SegmentAnalyzer"/>.</summary>
    private static double TurnScore(TurnStat t)
    {
        var score = 1.0;
        if (t.ChatErrors > 0) score -= 0.35 * Math.Min(t.ChatErrors, 2);
        if (t.ToolErrors > 0) score -= 0.15 * Math.Min(t.ToolErrors, 3);
        if (t.ToolErrors > 0 && t.ToolCalls >= 3 && t.ToolCalls >= 3 * Math.Max(1, t.ChatCalls))
            score -= 0.10; // repair loop
        return Math.Clamp(score, 0, 1);
    }

    private static string Grade(double score) => score switch
    {
        >= 85 => "excellent",
        >= 70 => "good",
        >= 55 => "fair",
        >= 40 => "poor",
        _ => "critical"
    };
}

public sealed record QualityComponent(string Name, double Weight, double Value, int Samples, string Detail);

public sealed record QualityReport(
    double Score,
    double Confidence,
    string Grade,
    IReadOnlyList<QualityComponent> Components,
    /// <summary>0–1 percentile rank vs. recent sessions (null when history is too short for comparison).</summary>
    double? PercentileRank = null,
    int? HistoryCount = null,
    double? HistoryMean = null,
    double? HistoryStdDev = null,
    /// <summary>How the session was driven — decides which components carry weight.</summary>
    SessionMode Mode = SessionMode.Unknown,
    /// <summary>Name of the <see cref="ScoringProfile"/> the weights above came from.</summary>
    string Profile = "interactive")
{
    /// <summary>Human-readable session mode, for the API and dashboard.</summary>
    public string ModeLabel => SessionModeClassifier.Label(Mode);

    /// <summary>z-score relative to the history window (null when HistoryStdDev is zero or unavailable).</summary>
    public double? ZScore =>
        HistoryMean is { } mu && HistoryStdDev is > 0 ? (Score - mu) / HistoryStdDev : null;

    /// <summary>Human-readable relative label ("above baseline", "at baseline", "below baseline").</summary>
    public string? RelativeGrade => PercentileRank switch
    {
        >= 0.75 => "above baseline",
        >= 0.35 => "at baseline",
        < 0.35 => "below baseline",
        _ => null
    };
}
