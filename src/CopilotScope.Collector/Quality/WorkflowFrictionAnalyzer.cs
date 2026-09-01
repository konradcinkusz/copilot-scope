using System.Text;
using System.Text.Json;
using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>
/// #9 Workflow-friction signals — a lexical, LLM-free detector for *repair markers*.
///
/// What it counts, per captured user message:
///   · repair/negative-feedback lexicon hits, weighted strong/mild, bilingual EN/PL
///   · rephrasing — Jaccard word-set similarity with the previous user message
///     (asking nearly the same thing again is the classic repair signal)
///   · typography — sustained CAPS, bursts of ?!
///   · short corrective replies ("no.", "źle, popraw")
///
/// <para><b>These are observed workflow events, not inferred emotions, and the distinction is
/// load-bearing.</b> EU AI Act Art. 5(1)(f) prohibits emotion-recognition systems in the
/// workplace outright — not "high-risk", prohibited, with fines to 7% of global turnover — so
/// a feature that claims to measure how a developer *feels* is one a DPO has to block on
/// sight, in exactly the EU/self-hosted segment where this tool is most defensible. What the
/// code actually does is count how often someone had to ask again. That is a property of the
/// tooling, it is what the signal was always useful for, and it is approvable. The rename is
/// therefore also a correction: "frustration index" was never what this measured.
/// See docs/WORKFLOW_FRICTION.md.</para>
///
/// <para>Off unless <see cref="WorkflowFrictionOptions.Enabled"/> is set, and per-message
/// previews need a second opt-in — the aggregate rate answers the question worth asking, and
/// quoting someone's prompts back next to a score about them is a different act.</para>
///
/// <para>Deliberately REPORT-ONLY: it does not feed the composite quality score. A lexicon
/// heuristic is noisy (false positives like "no worries", language bias, sarcasm-blind) —
/// every flagged message therefore carries its reasons, so the human can judge. Whether it
/// should ever be promoted into the composite is tracked separately (issue #61); SPUR-style
/// learned rubrics are the upgrade path when an LLM budget is acceptable.</para>
///
/// <para>Not to be confused with the composite's <c>friction</c> component, which is a
/// different measurement from a different source: that one scores turn-level repair loops
/// from telemetry (errors, retries, turn duration) and needs no prompt content at all.</para>
/// </summary>
public sealed class WorkflowFrictionAnalyzer(WorkflowFrictionOptions options) : IInsightAnalyzer
{
    public bool Enabled => options.Enabled;

    /// <summary>Phrases that read as "this did not work", not as "I am upset". The list is
    /// about the work, which is what keeps the signal defensible.</summary>
    private static readonly string[] Strong =
    [
        "doesn't work", "does not work", "not working", "still broken", "still wrong",
        "wrong again", "useless", "terrible", "wtf", "stop doing", "i give up", "you broke",
        "nie działa", "dalej nie działa", "znowu źle", "bez sensu", "do niczego", "zepsułeś", "poddaję się"
    ];

    private static readonly string[] Mild =
    [
        "no,", "no.", "that's not", "that is not", "wrong", "incorrect", "not what i",
        "again", "still", "undo", "revert", "why did you", "i said", "as i said",
        "nie o to", "źle", "popraw", "cofnij", "jeszcze raz", "przecież", "mówiłem", "pisałem", "nie tego"
    ];

    public InsightReport Analyze(CopilotSession session)
    {
        var prompts = session.Snapshot(s => s.Transcript
            .Where(t => t.Prompt is not null)
            .Select(t => (t.Time, Text: ExtractUserText(t.Prompt!)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .ToList());

        if (prompts.Count == 0)
            return new InsightReport(ReportName, AlgorithmName,
                "no-data", null, [],
                ["Requires content capture — enable captureContent (VS Code) or OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true (CLI). " +
                 "Privacy mode drops captured content at ingest, so this analyzer reports no data there by construction."]);

        var flagged = new List<string>();
        var flaggedCount = 0;
        var scores = new List<double>();
        string? previous = null;

        foreach (var (time, text) in prompts)
        {
            var reasons = new List<string>();
            var lower = text.ToLowerInvariant();
            double score = 0;

            var strongHits = Strong.Count(m => lower.Contains(m));
            if (strongHits > 0) { score += 0.45 * Math.Min(strongHits, 2); reasons.Add($"strong marker ×{strongHits}"); }

            var mildHits = Mild.Count(m => lower.Contains(m));
            if (mildHits > 0) { score += 0.15 * Math.Min(mildHits, 3); reasons.Add($"mild marker ×{mildHits}"); }

            if (previous is not null)
            {
                var similarity = Jaccard(previous, lower);
                if (similarity >= 0.6) { score += 0.30; reasons.Add($"rephrasing (similarity {similarity:P0})"); }
            }

            var letters = text.Count(char.IsLetter);
            if (letters >= 12 && (double)text.Count(char.IsUpper) / letters > 0.6)
            { score += 0.15; reasons.Add("sustained CAPS"); }

            if (text.Contains("!!") || text.Contains("??") || text.Contains("?!"))
            { score += 0.10; reasons.Add("punctuation burst"); }

            if (text.Length < 20 && (lower.StartsWith("no") || lower.StartsWith("nie") || lower.StartsWith("stop") || lower.StartsWith("wrong") || lower.StartsWith("źle")))
            { score += 0.20; reasons.Add("short corrective reply"); }

            score = Math.Min(1.0, score);
            scores.Add(score);
            if (score >= options.FlagThreshold)
            {
                flaggedCount++;
                // The quote is the part that reproduces a developer's own words next to a
                // number about them, so it is built only when the second opt-in is set. The
                // count above is always available, because the count is the aggregate signal.
                if (options.IncludeFlaggedMessages)
                {
                    var preview = text.Length > 90 ? text[..90] + "…" : text;
                    flagged.Add($"{time.ToLocalTime():HH:mm:ss} [{score:P0}] \"{preview}\" — {string.Join(", ", reasons)}");
                }
            }
            previous = lower;
        }

        // Session index: mean pulled halfway toward the peak, so one heavily-marked
        // message isn't averaged away by ten clean ones.
        var mean = scores.Average();
        var index = mean + 0.5 * (scores.Max() - mean);

        var metrics = new List<InsightMetric>
        {
            new("friction index (0 = no repair markers)", $"{index:P0}"),
            new("messages analyzed / with markers", $"{prompts.Count} / {flaggedCount}"),
            new("peak message score", $"{scores.Max():P0}")
        };

        var findings = new List<string>();
        findings.Add(index switch
        {
            >= 0.5 => "Repeated repair markers — the developer had to re-ask or correct several times.",
            >= 0.25 => "Some repair markers: occasional rephrasing or correction.",
            _ => "No meaningful repair markers."
        });
        findings.AddRange(flagged.Take(5));
        if (flaggedCount > 0 && !options.IncludeFlaggedMessages)
            findings.Add($"{flaggedCount} message(s) carried markers. Per-message previews quote prompt text and " +
                         "are off; set CopilotScope:WorkflowFriction:IncludeFlaggedMessages to include them.");
        findings.Add("Lexical, report-only signal — counts observed repair events (re-asking, corrections), " +
                     "not emotional state, and is not part of the composite score. Verify before acting on it.");

        return new InsightReport(ReportName, AlgorithmName, "ok", index, metrics, findings);
    }

    /// <summary>Report title. A single constant so the API payload, the dashboard and the
    /// tests cannot drift apart on the one string this issue is about.</summary>
    public const string ReportName = "Workflow friction signals";

    /// <summary>What the analyzer actually does, stated in terms of observable events.</summary>
    public const string AlgorithmName =
        "Lexical repair markers (rephrasing, corrective replies, negative feedback)";

    /// <summary>Pulls user-role text out of raw captured content (JSON message arrays or plain text).</summary>
    internal static string ExtractUserText(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith('[') && !text.StartsWith('{')) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var sb = new StringBuilder();
            void Walk(JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray()) Walk(item);
                        break;
                    case JsonValueKind.Object:
                        var role = el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() : null;
                        if (role is not null && !role.Equals("user", StringComparison.OrdinalIgnoreCase)) return;
                        if (el.TryGetProperty("content", out var c))
                        {
                            if (c.ValueKind == JsonValueKind.String) sb.AppendLine(c.GetString());
                            else Walk(c);
                        }
                        else if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            sb.AppendLine(t.GetString());
                        else if (el.TryGetProperty("parts", out var p)) Walk(p);
                        break;
                    case JsonValueKind.String:
                        sb.AppendLine(el.GetString());
                        break;
                }
            }
            Walk(doc.RootElement);
            var extracted = sb.ToString().Trim();
            return extracted.Length > 0 ? extracted : text;
        }
        catch (JsonException) { return text; }
    }

    private static double Jaccard(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var intersection = setA.Intersect(setB).Count();
        return (double)intersection / (setA.Count + setB.Count - intersection);

        static HashSet<string> Tokenize(string s) =>
            s.Split(new[] { ' ', '\n', '\t', ',', '.', '!', '?', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
             .Where(w => w.Length > 2)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
