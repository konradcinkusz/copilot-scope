using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>
/// Component weights for one <see cref="SessionMode"/>. A profile is not a tuning knob —
/// it says which signals are meaningful for how a session was driven.
///
/// The interactive weights are the published v2 weights and stay exactly as they were.
/// The autonomous profile zeroes the two components that measure a human who is not there:
///
///   latency    — time-to-first-token is a person's wait. On a background run nobody is
///                waiting, so a slow first token costs nothing and must not be scored as
///                if it did. (The raw TTFT numbers are still reported; only the weight goes.)
///   acceptance — an agent under acceptEdits applies its own edits. What survives of the
///                signal is edit *survival*, which the analyzer still reports separately.
///
/// The weight those two carried moves to friction and reliability: for a delegated run,
/// "did it thrash, error and repair-loop" is the whole question. Zero-weight components are
/// still computed and returned, so the UI can show what was excluded and why.
/// </summary>
public sealed record ScoringProfile(
    string Name,
    double Reliability,
    double Acceptance,
    double Friction,
    double Latency,
    double Feedback,
    double Efficiency)
{
    /// <summary>Published v2 weights — a person in a chat loop.</summary>
    public static readonly ScoringProfile Interactive =
        new("interactive", Reliability: 0.25, Acceptance: 0.20, Friction: 0.20,
            Latency: 0.15, Feedback: 0.10, Efficiency: 0.10);

    /// <summary>Delegated run: no human wait to measure, no human decision to count.</summary>
    public static readonly ScoringProfile Autonomous =
        new("autonomous", Reliability: 0.30, Acceptance: 0.00, Friction: 0.45,
            Latency: 0.00, Feedback: 0.10, Efficiency: 0.15);

    /// <summary>
    /// A person is approving the agent's work, so their decisions count — but they are not
    /// sitting on the first token of every call, so latency is discounted rather than dropped.
    /// </summary>
    public static readonly ScoringProfile SupervisedAgent =
        new("supervised-agent", Reliability: 0.25, Acceptance: 0.20, Friction: 0.30,
            Latency: 0.05, Feedback: 0.10, Efficiency: 0.10);

    public static ScoringProfile For(SessionMode mode) => mode switch
    {
        SessionMode.Autonomous => Autonomous,
        SessionMode.SupervisedAgent => SupervisedAgent,
        _ => Interactive
    };

    public double WeightOf(string component) => component switch
    {
        "reliability" => Reliability,
        "acceptance" => Acceptance,
        "friction" => Friction,
        "latency" => Latency,
        "feedback" => Feedback,
        "efficiency" => Efficiency,
        _ => 0
    };
}
