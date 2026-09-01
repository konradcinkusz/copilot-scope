using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Api;

/// <summary>Payload for POST /api/admin/seed (tools/CopilotScope.Seeder). Sessions are full
/// <see cref="PersistedSession"/> snapshots — the same shape the collector itself writes —
/// so seeding never drifts from what real ingestion produces.</summary>
public sealed record SeedRequest(bool Reset, List<PersistedSession> Sessions);

public sealed record SessionSummaryDto(
    string Id, string? Agent, string? Repository, string? Branch,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    long InputTokens, long OutputTokens, long CacheReadTokens,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    int AgentInvocations, int Turns,
    int EditsAccepted, int EditsRejected, int ThumbsUp, int ThumbsDown,
    double LinesAdded, double LinesRemoved,
    double TtftP50Ms, double TtftP95Ms,
    Dictionary<string, int> Models,
    QualityReport Quality,
    SessionKind Kind,
    EmitterKind EmitterKind = EmitterKind.Unknown,
    /// <summary>Edits applied under a permission mode rather than by a human decision.
    /// Reported so a high "accepted" count can be read correctly; never scored.</summary>
    int EditsAutoAccepted = 0);

public sealed record SessionDetailDto(
    SessionSummaryDto Summary,
    List<ToolStatDto> Tools,
    Dictionary<string, int> ErrorTypes,
    List<SessionEvent> Events,
    List<TranscriptEntry> Transcript,
    TurnAnalysis Turns,
    List<InsightReport> Insights);

public sealed record ToolStatDto(string Name, int Calls, int Errors, double AvgMs);

public sealed record OverviewDto(
    int Sessions,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors, int Turns,
    int EditsAccepted, int EditsRejected, int ThumbsUp, int ThumbsDown,
    double AvgQualityScore,
    Dictionary<string, int> ModelCalls,
    List<DailyTokensDto> Daily,
    List<TopSessionDto> TopSessions);

public sealed record DailyTokensDto(DateOnly Date, long InputTokens, long OutputTokens, int Sessions);

public sealed record TopSessionDto(string Id, long TotalTokens, double QualityScore, DateTimeOffset LastSeen);

public static class DtoOverview
{
    /// <summary>Aggregates every user-visible session into one "all my chats" summary. Internal Copilot
    /// helper calls (title generation, summarization) are excluded so they don't skew quality/token stats.</summary>
    public static OverviewDto Build(IReadOnlyCollection<CopilotSession> sessions, QualityEngine quality)
    {
        long inTok = 0, outTok = 0, cacheRead = 0, cacheCreate = 0;
        int chat = 0, chatErr = 0, tools = 0, toolErr = 0, turns = 0;
        int accepted = 0, rejected = 0, up = 0, down = 0;
        var models = new Dictionary<string, int>(StringComparer.Ordinal);
        var daily = new Dictionary<DateOnly, (long In, long Out, int Sessions)>();
        var tops = new List<TopSessionDto>();
        var scores = new List<double>();
        var counted = 0;

        foreach (var session in sessions)
        {
            if (SessionClassifier.IsInternal(session.Kind)) continue;
            counted++;

            var snap = session.Snapshot(x => new
            {
                x.InputTokens, x.OutputTokens, x.CacheReadTokens, x.CacheCreationTokens,
                x.ChatCalls, x.ChatErrors, x.ToolCalls, x.ToolErrors, x.Turns,
                x.EditsAccepted, x.EditsRejected, x.ThumbsUp, x.ThumbsDown,
                x.FirstSeen, x.LastSeen, x.Id,
                Models = new Dictionary<string, int>(x.ModelCalls)
            });

            inTok += snap.InputTokens; outTok += snap.OutputTokens;
            cacheRead += snap.CacheReadTokens; cacheCreate += snap.CacheCreationTokens;
            chat += snap.ChatCalls; chatErr += snap.ChatErrors;
            tools += snap.ToolCalls; toolErr += snap.ToolErrors; turns += snap.Turns;
            accepted += snap.EditsAccepted; rejected += snap.EditsRejected;
            up += snap.ThumbsUp; down += snap.ThumbsDown;

            foreach (var (model, count) in snap.Models)
                models[model] = models.TryGetValue(model, out var c) ? c + count : count;

            var day = DateOnly.FromDateTime(snap.LastSeen.UtcDateTime);
            var d = daily.TryGetValue(day, out var acc) ? acc : (0, 0, 0);
            daily[day] = (d.Item1 + snap.InputTokens, d.Item2 + snap.OutputTokens, d.Item3 + 1);

            var score = quality.Evaluate(session).Score;
            scores.Add(score);
            tops.Add(new TopSessionDto(snap.Id, snap.InputTokens + snap.OutputTokens, score, snap.LastSeen));
        }

        return new OverviewDto(
            counted,
            inTok, outTok, cacheRead, cacheCreate,
            chat, chatErr, tools, toolErr, turns,
            accepted, rejected, up, down,
            scores.Count > 0 ? Math.Round(scores.Average(), 1) : 0,
            models,
            daily.OrderByDescending(kv => kv.Key).Take(14)
                 .Select(kv => new DailyTokensDto(kv.Key, kv.Value.In, kv.Value.Out, kv.Value.Sessions))
                 .ToList(),
            tops.OrderByDescending(t => t.TotalTokens).Take(10).ToList());
    }
}

public static class Dto
{
    public static SessionSummaryDto Summary(CopilotSession s, QualityEngine quality, IReadOnlyList<double>? allScores = null)
    {
        var report = quality.Evaluate(s);
        if (allScores is { Count: >= 3 })
        {
            var sorted = allScores.OrderBy(x => x).ToList();
            var rank = (double)sorted.Count(x => x <= report.Score) / sorted.Count;
            var mean = sorted.Average();
            var stddev = Math.Sqrt(sorted.Average(x => (x - mean) * (x - mean)));
            report = report with { PercentileRank = rank, HistoryCount = sorted.Count, HistoryMean = Math.Round(mean, 1), HistoryStdDev = Math.Round(stddev, 2) };
        }

        return s.Snapshot(x => new SessionSummaryDto(
            x.Id, x.AgentName, Anonymize(x.Repository), x.Branch,
            x.FirstSeen, x.LastSeen,
            x.InputTokens, x.OutputTokens, x.CacheReadTokens,
            x.ChatCalls, x.ChatErrors, x.ToolCalls, x.ToolErrors,
            x.AgentInvocations, x.Turns,
            x.EditsAccepted, x.EditsRejected, x.ThumbsUp, x.ThumbsDown,
            x.LinesAdded, x.LinesRemoved,
            CopilotSession.Percentile(x.TtftMs, 0.5), CopilotSession.Percentile(x.TtftMs, 0.95),
            new Dictionary<string, int>(x.ModelCalls),
            report,
            SessionClassifier.Classify(x),
            x.EmitterKind,
            x.EditsAutoAccepted));
    }

    public static SessionDetailDto Detail(CopilotSession s, QualityEngine quality, InsightPipeline insights, IReadOnlyList<double>? allScores = null)
    {
        var summary = Summary(s, quality, allScores);
        var turns = SegmentAnalyzer.Analyze(s);
        var reports = insights.Analyze(s);
        return s.Snapshot(x => new SessionDetailDto(
            summary,
            x.Tools.Select(t => new ToolStatDto(t.Key, t.Value.Calls, t.Value.Errors,
                t.Value.Calls > 0 ? t.Value.TotalMs / t.Value.Calls : 0)).OrderByDescending(t => t.Calls).ToList(),
            new Dictionary<string, int>(x.ErrorTypes),
            x.RecentEvents.Reverse().Take(120).ToList(),
            x.Transcript.AsEnumerable().Reverse().Take(50).Reverse().ToList(),
            turns,
            reports));
    }

    private static string? Anonymize(string? repoUrl)
    {
        if (repoUrl is null) return null;
        // Strip credentials that may be embedded in a remote URL.
        var idx = repoUrl.IndexOf('@');
        return idx > 0 && repoUrl.Contains("://") ? repoUrl[..(repoUrl.IndexOf("://") + 3)] + repoUrl[(idx + 1)..] : repoUrl;
    }
}
