// Lives in the Collector rather than in JudgeAgent because three things now depend on it: the
// judge's output vocabulary, the human labelling flow (#102), and the calibration engine that
// compares them. A rubric question a rater reads and a rubric question the judge is asked have
// to be the same sentence, or the agreement statistic measures the difference between two
// forms rather than between a person and a model.
namespace CopilotScope.Collector.Calibration;

/// <summary>One level of the shared ordinal scale, with the anchor text a labeller grades against.</summary>
public sealed record RubricBand(int Level, string Name, double Lower, double Upper, string Anchor);

/// <summary>What one of the judge's five rubrics measures, and which way its score runs.</summary>
/// <param name="Algorithm">Matches the `algorithm` field the judge emits (JudgeSystemPromptTemplate.txt).</param>
/// <param name="Question">The question a human labeller is answering for this rubric.</param>
/// <param name="HigherIsBetter">False for rubrics whose score rises as the session gets worse.</param>
public sealed record RubricDefinition(string Algorithm, string Question, bool HigherIsBetter);

/// <summary>
/// The ordinal scale judge scores and human labels are compared on.
///
/// <para>κ is a statistic over categories, and the judge emits a continuous 0–1 score, so the
/// two only meet once the score is binned. Four bands, which is also what
/// <c>AI-EVALS.md</c> §5 asks for — "a small ordinal scale with an anchor description per
/// level", never a bare 1–10. Four is a deliberate ceiling: with the 50–100 labelled sessions
/// this repo's own research plan calls for (<c>research/RESEARCH_PROPOSALS.md</c>), a
/// five- or six-band scale leaves most confusion cells empty and κ starts swinging on single
/// items.</para>
///
/// <para><b>Polarity matters more than it looks.</b> Four of the five rubrics score "how good
/// was this", but <c>deep-friction</c> scores "how much did the user have to repair" — it runs
/// the other way, and the judge is behaving correctly when it returns 0.1 for a session that
/// went straight through. A labeller who reads band 3 as "great session" on that rubric would
/// be recorded as maximally disagreeing with a judge that got it right, and the calibration
/// would report a broken judge on the strength of a broken form. So each rubric declares its direction and supplies its own
/// question, and labels are recorded on the rubric's own scale rather than a global
/// "higher is better" one.</para>
/// </summary>
public static class RubricScale
{
    /// <summary>Number of ordinal bands. Judge scores and human labels share this scale.</summary>
    public const int Categories = 4;

    /// <summary>Band boundaries on the judge's native 0–1 output, with generic anchors. The
    /// anchors read as "how much of what this rubric measures is present", which stays correct
    /// whichever way the rubric runs — the per-rubric <see cref="RubricDefinition.Question"/>
    /// supplies the direction.</summary>
    public static readonly IReadOnlyList<RubricBand> Bands =
    [
        new(0, "none/poor",  0.00, 0.40, "Clearly absent, or the criterion is plainly not met."),
        new(1, "partial",    0.40, 0.65, "Present in part, with gaps that materially matter."),
        new(2, "mostly",     0.65, 0.85, "Present, with minor gaps that do not change the outcome."),
        new(3, "full",       0.85, 1.00, "Fully present; nothing material missing.")
    ];

    /// <summary>The five rubrics JudgeSystemPromptTemplate.txt emits. Keyed by the `algorithm`
    /// field so a report can never silently pair a human's G-Eval label with a SPUR score.</summary>
    public static readonly IReadOnlyDictionary<string, RubricDefinition> Rubrics =
        new Dictionary<string, RubricDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["G-Eval"] = new("G-Eval",
                "How correct, complete and clear was the assistant's work in this session?", true),
            ["SPUR"] = new("SPUR",
                "Would the user who ran this session have rated it satisfactory?", true),
            ["RAGAS"] = new("RAGAS",
                "Were the assistant's answers faithful to, and supported by, the retrieved context?", true),
            ["deep-friction"] = new("deep-friction",
                "How much did the user have to repair — re-ask, correct, restate — to get what " +
                "they wanted? (higher = more repair)", false),
            ["task-completion"] = new("task-completion",
                "Was the user's original ask actually resolved by the end of the session?", true)
        };

    /// <summary>Bins a judge score onto the ordinal scale. Scores outside 0–1 are clamped: a
    /// model that returns 1.2 has broken its contract, but dropping the item would silently
    /// shrink the calibration sample, and the nearest band is the honest reading.</summary>
    public static int Band(double score) => Math.Clamp(score, 0.0, 1.0) switch
    {
        >= 0.85 => 3,
        >= 0.65 => 2,
        >= 0.40 => 1,
        _ => 0
    };

    /// <summary>True when <paramref name="level"/> is a usable band index.</summary>
    public static bool IsValidLevel(int level) => level >= 0 && level < Categories;

    /// <summary>
    /// Retired rubric ids, mapped to their current names.
    ///
    /// <c>deep-frustration</c> became <c>deep-friction</c> when the rubric was restated in
    /// terms of observed repair behaviour rather than inferred emotion (issue #95 — EU AI Act
    /// Art. 5(1)(f) prohibits workplace emotion recognition, and the rubric never measured
    /// that anyway). Human labels are expensive and slow to collect, so a label file written
    /// against the old id keeps resolving instead of silently dropping out of the agreement
    /// statistics — which is the failure that would look like a judge getting worse.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RetiredAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deep-frustration"] = "deep-friction",
        };

    /// <summary>The rubric definition for an algorithm id, or null when the id is unknown.
    /// Retired ids resolve to their replacement.</summary>
    public static RubricDefinition? Find(string algorithm)
    {
        if (Rubrics.TryGetValue(algorithm, out var rubric)) return rubric;
        return RetiredAliases.TryGetValue(algorithm, out var current)
            && Rubrics.TryGetValue(current, out var aliased) ? aliased : null;
    }

    /// <summary>The current id for an algorithm name, so a label file written against a retired
    /// id groups with the labels and scores that use the current one.</summary>
    public static string Canonical(string algorithm) =>
        RetiredAliases.TryGetValue(algorithm, out var current) ? current : algorithm;
}
