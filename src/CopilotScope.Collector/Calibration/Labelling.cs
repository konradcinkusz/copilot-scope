using System.Collections.Concurrent;

namespace CopilotScope.Collector.Calibration;

/// <summary>
/// Human session labelling, bound from <c>CopilotScope:Labelling</c>.
///
/// <para>The composite score's credibility problem is written down in this repo twice: the
/// calibration docs say plainly that no calibration has been run and that there are no human
/// labels, and the product review calls the composite "an opinion with a confidence interval,
/// not a measurement". METR's 2025 RCT — developers perceived a 20% speedup while measuring 19%
/// slower — is the standing proof that unvalidated measurement collapses the moment anyone
/// checks. The consumption machinery (quadratic-weighted κ, bootstrap CIs, human-ceiling checks)
/// already exists and has nothing to consume. This is the missing half: a way for a person to
/// produce a label.</para>
///
/// <para>Off by default, because it puts a write control on a read-only surface and most
/// deployments are not running a labelling study.</para>
/// </summary>
public sealed class LabellingOptions
{
    /// <summary>Show the labelling controls and accept labels. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bound on stored labels when there is no Postgres to write them to.</summary>
    public int MaxInMemoryLabels { get; set; } = 20_000;

    public string Describe() => Enabled ? "on (human labels accepted)" : "off";
}

/// <summary>
/// One human judgment: a rater, a session, a rubric, a band.
///
/// <para><b>Labels rate sessions, never people.</b> The rater handle names who did the rating,
/// which is what inter-rater agreement is computed over — it is not the developer whose session
/// is being rated, and the schema has no field for that person at all.</para>
/// </summary>
/// <param name="Level">Band 0–3 on the rubric's own scale. Null means the rater skipped this
/// rubric, which is a real answer — "this session has no retrieval context to judge RAGAS on"
/// is information, and forcing a number would be worse than recording nothing.</param>
public sealed record SessionLabel(
    string SessionId, string Rater, string Algorithm, int? Level, string? Note,
    DateTimeOffset At);

/// <summary>The flat record shape <c>calibration/labels.example.json</c> uses and the
/// calibration engine ingests. Deliberately identical, so an export needs no hand-editing —
/// which is the acceptance criterion this whole feature turns on.</summary>
public sealed record LabelRecord(string SessionId, string Rater, string Algorithm, int Level, string? Note = null);

/// <summary>The dataset document the engine reads.</summary>
public sealed record LabelDataset(
    string DatasetVersion, string? JudgeModel, string? JudgePromptVersion,
    List<LabelRecord> Labels, List<object> JudgeScores);

/// <summary>
/// Stores labels and exports them in the calibration schema.
///
/// In-memory with an optional Postgres sink, the same shape as the access audit log: a
/// labelling session must not lose the last hour's work to a database blip, and a deployment
/// with no Postgres should still be able to run a study and export the result.
/// </summary>
public sealed class LabelStore(LabellingOptions options)
{
    /// <summary>Keyed by (session, rater, rubric): a rater revising their own judgment is the
    /// normal reason for a second label, and keeping both would record a person as disagreeing
    /// with themselves.</summary>
    private readonly ConcurrentDictionary<(string Session, string Rater, string Algorithm), SessionLabel> _labels
        = new();

    public bool Enabled => options.Enabled;

    public int Count => _labels.Count;

    /// <summary>Records one label, replacing any earlier judgment by the same rater on the same
    /// rubric. Returns false when the rubric or the band is not one the engine can read —
    /// rejected loudly rather than stored and dropped at export.</summary>
    public bool Record(SessionLabel label, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(label.SessionId)) { error = "sessionId is required."; return false; }
        if (string.IsNullOrWhiteSpace(label.Rater)) { error = "rater is required."; return false; }
        if (RubricScale.Find(label.Algorithm) is null)
        {
            error = $"'{label.Algorithm}' is not a rubric this project scores. " +
                    $"Expected one of: {string.Join(", ", RubricScale.Rubrics.Keys)}.";
            return false;
        }
        if (label.Level is { } level && !RubricScale.IsValidLevel(level))
        {
            error = $"Level {level} is outside the 0..{RubricScale.Categories - 1} band scale.";
            return false;
        }
        if (_labels.Count >= options.MaxInMemoryLabels && !_labels.ContainsKey(Key(label)))
        {
            error = "The in-memory label store is full; export and configure Postgres.";
            return false;
        }

        // The rubric id is canonicalized on the way in, so a label written against a retired
        // name groups with the current one instead of forming a rubric of its own.
        var canonical = label with { Algorithm = RubricScale.Canonical(label.Algorithm) };
        _labels[Key(canonical)] = canonical;
        return true;
    }

    private static (string, string, string) Key(SessionLabel l) => (l.SessionId, l.Rater, l.Algorithm);

    public IReadOnlyList<SessionLabel> ForSession(string sessionId) =>
        _labels.Values.Where(l => string.Equals(l.SessionId, sessionId, StringComparison.Ordinal))
               .OrderBy(l => l.Algorithm, StringComparer.Ordinal).ToList();

    public IReadOnlyList<SessionLabel> All() =>
        _labels.Values.OrderBy(l => l.SessionId, StringComparer.Ordinal)
               .ThenBy(l => l.Rater, StringComparer.Ordinal)
               .ThenBy(l => l.Algorithm, StringComparer.Ordinal).ToList();

    /// <summary>Restores labels from the durable store at startup.</summary>
    public void Load(IEnumerable<SessionLabel> labels)
    {
        foreach (var label in labels) _labels[Key(label)] = label;
    }

    /// <summary>Id prefix the Seeder namespaces its fabricated sessions under.</summary>
    public const string SeedPrefix = "seed-";

    /// <summary>
    /// The dataset the calibration engine consumes.
    ///
    /// <para><b>Seeded sessions are excluded by default, and that is the point.</b> The
    /// project's own research plan suggests labelling Seeder-generated sessions to bootstrap
    /// the dataset — which would validate the scoring model against the synthetic personas the
    /// same repository wrote. That is a circle, and a calibration report built on it would be
    /// worse than none, because it would carry a κ and a confidence interval. Including them
    /// requires asking, and the dataset version then says <c>synthetic</c> so the report cannot
    /// later be read as though it came from real work.</para>
    /// </summary>
    public LabelDataset Export(bool includeSynthetic = false, string? datasetVersion = null)
    {
        var labels = All()
            .Where(l => l.Level is not null)   // a skip is a real answer, but not a label
            .Where(l => includeSynthetic || !l.SessionId.StartsWith(SeedPrefix, StringComparison.Ordinal))
            .Select(l => new LabelRecord(l.SessionId, l.Rater, l.Algorithm, l.Level!.Value, l.Note))
            .ToList();

        var version = datasetVersion ?? $"labels-{DateTimeOffset.UtcNow:yyyyMMdd}";
        if (includeSynthetic && labels.Any(l => l.SessionId.StartsWith(SeedPrefix, StringComparison.Ordinal)))
            version += "-synthetic";

        // JudgeScores are supplied separately by a judge run: the calibration engine takes them
        // as an input rather than fetching them, which is what makes a calibration report a pure
        // function of its dataset and therefore re-runnable to the same number.
        return new LabelDataset(version, null, null, labels, []);
    }
}
