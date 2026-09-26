using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Review;

/// <summary>A recurrence before it is numbered: the full evidence list, capped later by the tier.</summary>
internal sealed record DetectedPattern(
    string Kind, string Label, string Stratum,
    int Support, int Occurrences,
    Dictionary<string, double> Stats,
    string Reading,
    List<string> Evidence);

/// <summary>
/// What recurred, counted. Every detector runs inside one stratum (origin/assistant/profile) and
/// states its threshold, so a reader can check a pattern against the sessions it cites.
///
/// Five kinds, all from metadata the collector already holds — tool stats, error types, chat
/// errors, the turn analysis, and the models called. No detector reads prompt text, and none
/// mines the timeline's display strings: a pattern that only a captured transcript could support
/// belongs to whoever reads the transcript, which this collector does not.
/// </summary>
internal static class ReviewPatterns
{
    /// <summary>Calls a tool needs across the stratum before its error rate means anything.</summary>
    private const int MinToolCalls = 10;

    /// <summary>Error rate below which a tool is not "failing often" however the others are doing.</summary>
    private const double MinToolErrorRate = 0.05;

    /// <summary>Composite points a model's sessions must differ from the rest of the stratum by.</summary>
    private const double MinModelDelta = 2.0;

    private const string NotCausal = "Descriptive: a count within one stratum, not a causal estimate.";

    public static List<DetectedPattern> Detect(IReadOnlyList<ScoredSession> scored)
    {
        var found = new List<DetectedPattern>();
        foreach (var stratum in scored.GroupBy(x => x.Stratum, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = stratum.ToList();
            ToolErrors(stratum.Key, members, found);
            ErrorTypes(stratum.Key, members, found);
            LlmErrors(stratum.Key, members, found);
            TurnReason(stratum.Key, members, found, "repair-loops", "repair loop",
                "Turns spending far more tool calls per chat call than this session's norm, with failures: the " +
                "assistant retrying rather than progressing. " + NotCausal);
            TurnReason(stratum.Key, members, found, "latency-stalls", "TTFT",
                "Turns whose time-to-first-token was at least 1.5× this session's own median. Measured against the " +
                "session, so a slow model is not penalized for being slow — only turns slow for that session count. " + NotCausal);
            ModelContrast(stratum.Key, members, found);
        }

        return found
            .OrderBy(p => p.Kind, StringComparer.Ordinal)
            .ThenBy(p => p.Stratum, StringComparer.Ordinal)
            .ThenBy(p => p.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static void ToolErrors(string stratum, List<ScoredSession> members, List<DetectedPattern> found)
    {
        var totals = new Dictionary<string, (int Calls, int Errors, HashSet<string> Using, HashSet<string> Erring)>(StringComparer.Ordinal);
        foreach (var m in members)
            foreach (var (name, stat) in m.Session.Snapshot(s => s.Tools.Select(t => (t.Key, t.Value)).ToList()))
            {
                if (!totals.TryGetValue(name, out var t)) t = (0, 0, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                t.Calls += stat.Calls;
                t.Errors += stat.Errors;
                t.Using.Add(m.Id);
                if (stat.Errors > 0) t.Erring.Add(m.Id);
                totals[name] = t;
            }

        long allCalls = totals.Values.Sum(t => (long)t.Calls), allErrors = totals.Values.Sum(t => (long)t.Errors);
        foreach (var (name, t) in totals.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (t.Calls < MinToolCalls || t.Erring.Count < ReviewPack.MinRecurrence) continue;
            var rate = (double)t.Errors / t.Calls;
            var otherCalls = allCalls - t.Calls;
            var otherRate = otherCalls == 0 ? 0 : (double)(allErrors - t.Errors) / otherCalls;
            if (rate < Math.Max(MinToolErrorRate, 2 * otherRate)) continue;

            found.Add(new DetectedPattern("tool-errors", $"{name} fails often", stratum,
                t.Erring.Count, t.Errors,
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["calls"] = t.Calls, ["errors"] = t.Errors, ["errorRate"] = Math.Round(rate, 4),
                    ["otherToolsErrorRate"] = Math.Round(otherRate, 4), ["sessionsUsing"] = t.Using.Count
                },
                $"This tool's error rate is at least twice the rest of the stratum's, over {t.Calls} calls. " +
                "The sessions cited are the ones in which it failed. " + NotCausal,
                t.Erring.Order(StringComparer.Ordinal).ToList()));
        }
    }

    private static void ErrorTypes(string stratum, List<ScoredSession> members, List<DetectedPattern> found)
    {
        var byType = new Dictionary<string, (int Occurrences, List<string> Sessions)>(StringComparer.Ordinal);
        foreach (var m in members)
            foreach (var (type, count) in m.Session.Snapshot(s => s.ErrorTypes.Select(e => (e.Key, e.Value)).ToList()))
            {
                if (count <= 0) continue;
                if (!byType.TryGetValue(type, out var t)) t = (0, []);
                t.Occurrences += count;
                t.Sessions.Add(m.Id);
                byType[type] = t;
            }

        foreach (var (type, t) in byType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (t.Sessions.Count < ReviewPack.MinRecurrence) continue;
            found.Add(new DetectedPattern("error-types", $"{type} recurs", stratum,
                t.Sessions.Count, t.Occurrences,
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["sessions"] = t.Sessions.Count, ["occurrences"] = t.Occurrences,
                    ["shareOfStratum"] = Math.Round((double)t.Sessions.Count / members.Count, 3)
                },
                "The same error type, as the emitter reported it, in several sessions. " + NotCausal,
                t.Sessions.Order(StringComparer.Ordinal).ToList()));
        }
    }

    private static void LlmErrors(string stratum, List<ScoredSession> members, List<DetectedPattern> found)
    {
        var erring = members.Where(m => m.Session.ChatErrors > 0).ToList();
        if (erring.Count < ReviewPack.MinRecurrence) return;
        var chatCalls = members.Sum(m => m.Session.ChatCalls);
        var errors = erring.Sum(m => m.Session.ChatErrors);
        found.Add(new DetectedPattern("llm-errors", "LLM calls failed", stratum,
            erring.Count, errors,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["sessions"] = erring.Count, ["errors"] = errors, ["chatCalls"] = chatCalls,
                ["errorRate"] = chatCalls == 0 ? 0 : Math.Round((double)errors / chatCalls, 4),
                ["shareOfStratum"] = Math.Round((double)erring.Count / members.Count, 3)
            },
            "Chat calls the emitter reported as failed. Reliability is the largest weight in the composite, so these " +
            "sessions score lower for it; the error types block says what kind. " + NotCausal,
            erring.Select(m => m.Id).Order(StringComparer.Ordinal).ToList()));
    }

    private static void TurnReason(string stratum, List<ScoredSession> members, List<DetectedPattern> found,
        string kind, string reasonPrefix, string reading)
    {
        var hits = members
            .Select(m => (m.Id, Turns: m.Turns.Turns.Count(t => t.Reasons.Any(r => r.StartsWith(reasonPrefix, StringComparison.Ordinal)))))
            .Where(x => x.Turns > 0)
            .ToList();
        if (hits.Count < ReviewPack.MinRecurrence) return;

        var label = kind == "repair-loops" ? "Repair loops" : "Latency stalls";
        found.Add(new DetectedPattern(kind, label, stratum,
            hits.Count, hits.Sum(h => h.Turns),
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["sessions"] = hits.Count, ["turns"] = hits.Sum(h => h.Turns),
                ["shareOfStratum"] = Math.Round((double)hits.Count / members.Count, 3)
            },
            reading,
            hits.Select(h => h.Id).Order(StringComparer.Ordinal).ToList()));
    }

    private static void ModelContrast(string stratum, List<ScoredSession> members, List<DetectedPattern> found)
    {
        var models = members
            .SelectMany(m => m.Session.Snapshot(s => s.ModelCalls.Keys.ToList()))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var model in models)
        {
            var with = members.Where(m => m.Session.ModelCalls.ContainsKey(model)).ToList();
            var without = members.Where(m => !m.Session.ModelCalls.ContainsKey(model)).ToList();
            if (with.Count < ReviewPack.MinSessionsForContrast || without.Count < ReviewPack.MinSessionsForContrast) continue;

            var meanWith = with.Average(m => m.Report.Score);
            var meanWithout = without.Average(m => m.Report.Score);
            var delta = meanWith - meanWithout;
            if (Math.Abs(delta) < MinModelDelta) continue;

            found.Add(new DetectedPattern("model-contrast",
                $"{model}: mean score {meanWith:0.0} with it, {meanWithout:0.0} without", stratum,
                with.Count, with.Count,
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["sessionsWith"] = with.Count, ["sessionsWithout"] = without.Count,
                    ["meanScoreWith"] = Math.Round(meanWith, 1), ["meanScoreWithout"] = Math.Round(meanWithout, 1),
                    ["delta"] = Math.Round(delta, 1),
                    ["meanConfidenceWith"] = Math.Round(with.Average(m => m.Report.Confidence), 2),
                    ["meanConfidenceWithout"] = Math.Round(without.Average(m => m.Report.Confidence), 2)
                },
                "Sessions that called this model against sessions in the same stratum that did not. Sessions choose " +
                "models for reasons the pack cannot see, so this is a contrast to look into, not an effect. " + NotCausal,
                with.Select(m => m.Id).Order(StringComparer.Ordinal).ToList()));
        }
    }
}
