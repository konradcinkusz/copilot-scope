using System.Collections.Concurrent;
using CopilotScope.Collector.Otlp;

namespace CopilotScope.Collector.Domain;

/// <summary>
/// Mutable aggregate of one Copilot chat session (keyed by gen_ai.conversation.id,
/// falling back to the VS Code window session.id). Thread-safe via its own lock;
/// snapshots are taken for the API / SignalR layer.
/// </summary>
/// <summary>Appended to, never reordered — the value is persisted as an int in Postgres.</summary>
public enum EmitterKind { Unknown, VSCode, CLI, ClaudeCode, Cursor, Cowork }

public sealed class CopilotSession
{
    private readonly object _lock = new();
    private const int MaxSpans = 500;
    private const int MaxEvents = 300;

    public required string Id { get; init; }
    public string? VsCodeSessionId { get; set; }
    public EmitterKind EmitterKind { get; set; } = EmitterKind.Unknown;
    public string? AgentName { get; set; }
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    // Tokens
    public long InputTokens;
    public long OutputTokens;
    public long CacheReadTokens;
    public long CacheCreationTokens;

    // Counters
    public int ChatCalls;
    public int ChatErrors;
    public int ToolCalls;
    public int ToolErrors;
    public int AgentInvocations;
    public int Turns;
    /// <summary>Edits a human accepted. Never includes permission-mode auto-accepts — see
    /// <see cref="EditsAutoAccepted"/> — so the acceptance component stays a measure of
    /// human judgment rather than of how the assistant was configured.</summary>
    public int EditsAccepted;
    public int EditsRejected;

    /// <summary>
    /// Edits applied without a human decision, because the assistant was running under a
    /// permission mode that auto-approves them (Claude Code reports <c>source=config</c> on
    /// the tool_decision event). Reported, never scored: counting these as acceptance would
    /// let a permission flag raise the quality score.
    /// </summary>
    public int EditsAutoAccepted;
    public int ThumbsUp;
    public int ThumbsDown;
    public double LinesAdded;
    public double LinesRemoved;

    // Distributions (bounded)
    public readonly List<double> TtftMs = new();
    public readonly List<double> ChatDurationMs = new();
    public readonly List<double> SurvivalScores = new();      // combined (compat)
    public readonly List<double> SurvivalFourGram = new();
    public readonly List<double> SurvivalNoRevert = new();
    public readonly ConcurrentDictionary<string, ModelStat> ModelUsage = new();
    public readonly ConcurrentDictionary<string, int> ModelCalls = new();
    public readonly ConcurrentDictionary<string, (int Calls, int Errors, double TotalMs)> Tools = new();
    public readonly ConcurrentDictionary<string, int> ErrorTypes = new();

    public readonly LinkedList<OtlpSpan> RecentSpans = new();
    public readonly LinkedList<SessionEvent> RecentEvents = new();

    // Captured prompt/response content (only present when the Copilot side has
    // captureContent enabled — otherwise these stay empty by design).
    public readonly List<TranscriptEntry> Transcript = new();
    private const int MaxTranscript = 100;
    private const int MaxContentChars = 4000;

    // Per-turn aggregates (one turn ≈ one invoke_agent trace).
    public readonly Dictionary<string, TurnStat> TurnsByTrace = new(StringComparer.Ordinal);
    public readonly List<TurnStat> TurnList = new();
    private const int MaxTurns = 200;

    /// <summary>Must be called while holding the session lock (i.e. inside Apply).</summary>
    public TurnStat TurnFor(string traceId, DateTimeOffset start)
    {
        if (TurnsByTrace.TryGetValue(traceId, out var existing)) return existing;
        var turn = new TurnStat { TraceId = traceId, Index = TurnList.Count, Start = start, End = start };
        if (TurnList.Count < MaxTurns) { TurnsByTrace[traceId] = turn; TurnList.Add(turn); }
        return turn;
    }

    /// <summary>Must be called while holding the session lock (i.e. inside Apply).</summary>
    public void AddTranscript(DateTimeOffset time, string model, string? prompt, string? response, int turnIndex)
    {
        static string? Trunc(string? v) => v is { Length: > MaxContentChars } ? v[..MaxContentChars] + " …[truncated]" : v;
        Transcript.Add(new TranscriptEntry(time, model, Trunc(prompt), Trunc(response), turnIndex));
        while (Transcript.Count > MaxTranscript) Transcript.RemoveAt(0);
    }

    /// <summary>
    /// Folds another session's aggregates into this one. Used when signals that
    /// arrived without a conversation identity (CLI metrics/logs land in an
    /// "unattributed" bucket) are later claimed by a real conversation session.
    /// The source session should be discarded by the caller afterwards.
    /// </summary>
    public void MergeFrom(CopilotSession other)
    {
        // Snapshot the source first — no nested locks, no ordering issues.
        var o = Persistence.PersistedSession.From(other);

        Apply(s =>
        {
            if (o.FirstSeen < s.FirstSeen) s.FirstSeen = o.FirstSeen;
            if (o.LastSeen > s.LastSeen) s.LastSeen = o.LastSeen;
            s.AgentName ??= o.AgentName;
            s.Repository ??= o.Repository;
            s.Branch ??= o.Branch;
            s.VsCodeSessionId ??= o.VsCodeSessionId;

            s.InputTokens += o.InputTokens; s.OutputTokens += o.OutputTokens;
            s.CacheReadTokens += o.CacheReadTokens; s.CacheCreationTokens += o.CacheCreationTokens;
            s.ChatCalls += o.ChatCalls; s.ChatErrors += o.ChatErrors;
            s.ToolCalls += o.ToolCalls; s.ToolErrors += o.ToolErrors;
            s.AgentInvocations += o.AgentInvocations; s.Turns += o.Turns;
            s.EditsAccepted += o.EditsAccepted; s.EditsRejected += o.EditsRejected;
            s.EditsAutoAccepted += o.EditsAutoAccepted;
            s.ThumbsUp += o.ThumbsUp; s.ThumbsDown += o.ThumbsDown;
            s.LinesAdded += o.LinesAdded; s.LinesRemoved += o.LinesRemoved;

            s.TtftMs.AddRange(o.TtftMs);
            s.ChatDurationMs.AddRange(o.ChatDurationMs);
            s.SurvivalScores.AddRange(o.SurvivalScores);
            s.SurvivalFourGram.AddRange(o.SurvivalFourGram ?? []);
            s.SurvivalNoRevert.AddRange(o.SurvivalNoRevert ?? []);

            foreach (var (k, v) in o.ModelCalls)
                s.ModelCalls[k] = s.ModelCalls.TryGetValue(k, out var c) ? c + v : v;
            foreach (var (k, v) in o.ModelUsage ?? new())
                s.ModelUsage.AddOrUpdate(k,
                    new ModelStat { Calls = v.Calls, InputTokens = v.InputTokens, OutputTokens = v.OutputTokens, CacheReadTokens = v.CacheReadTokens },
                    (_, e) => { e.Calls += v.Calls; e.InputTokens += v.InputTokens; e.OutputTokens += v.OutputTokens; e.CacheReadTokens += v.CacheReadTokens; return e; });
            foreach (var t in o.Tools)
                s.Tools[t.Name] = s.Tools.TryGetValue(t.Name, out var existing)
                    ? (existing.Calls + t.Calls, existing.Errors + t.Errors, existing.TotalMs + t.TotalMs)
                    : (t.Calls, t.Errors, t.TotalMs);
            foreach (var (k, v) in o.ErrorTypes)
                s.ErrorTypes[k] = s.ErrorTypes.TryGetValue(k, out var c) ? c + v : v;

            foreach (var e in o.Events) s.AddEvent(e);
            foreach (var t in o.Transcript) s.AddTranscript(t.Time, t.Model, t.Prompt, t.Response, -1);

            foreach (var t in o.TurnStats)
            {
                if (s.TurnsByTrace.ContainsKey(t.TraceId)) continue;
                var turn = new TurnStat
                {
                    TraceId = t.TraceId, Index = s.TurnList.Count, Start = t.Start, End = t.End,
                    ChatCalls = t.ChatCalls, ChatErrors = t.ChatErrors,
                    ToolCalls = t.ToolCalls, ToolErrors = t.ToolErrors,
                    InputTokens = t.InputTokens, OutputTokens = t.OutputTokens,
                    TtftTotalMs = t.TtftTotalMs, TtftCount = t.TtftCount, PrimaryModel = t.PrimaryModel
                };
                s.TurnsByTrace[t.TraceId] = turn;
                s.TurnList.Add(turn);
            }
        });
    }

    public void Apply(Action<CopilotSession> mutation)
    {
        lock (_lock)
        {
            mutation(this);
            LastSeen = DateTimeOffset.UtcNow;
        }
    }

    public void AddSpan(OtlpSpan span)
    {
        lock (_lock)
        {
            RecentSpans.AddLast(span);
            while (RecentSpans.Count > MaxSpans) RecentSpans.RemoveFirst();
        }
    }

    public void AddEvent(SessionEvent evt)
    {
        lock (_lock)
        {
            RecentEvents.AddLast(evt);
            while (RecentEvents.Count > MaxEvents) RecentEvents.RemoveFirst();
        }
    }

    public T Snapshot<T>(Func<CopilotSession, T> projector)
    {
        lock (_lock) return projector(this);
    }

    /// <summary>UserChat vs. internal Copilot helper call (title generation, summarization, ...).</summary>
    public SessionKind Kind => Snapshot(SessionClassifier.Classify);

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        var idx = (int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }
}

public sealed record SessionEvent(DateTimeOffset Time, string Kind, string Summary);

/// <summary>One captured prompt/response pair (content capture must be enabled Copilot-side).</summary>
public sealed record TranscriptEntry(DateTimeOffset Time, string Model, string? Prompt, string? Response, int Turn);

/// <summary>Mutable per-model token/call aggregate.</summary>
public sealed class ModelStat
{
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
}

/// <summary>Mutable per-turn aggregate; one turn corresponds to one invoke_agent trace.</summary>
public sealed class TurnStat
{
    public required string TraceId { get; init; }
    public int Index { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int ChatCalls { get; set; }
    public int ChatErrors { get; set; }
    public int ToolCalls { get; set; }
    public int ToolErrors { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double TtftTotalMs { get; set; }
    public int TtftCount { get; set; }
    public string? PrimaryModel { get; set; }
    public double AvgTtftMs => TtftCount > 0 ? TtftTotalMs / TtftCount : 0;
    public double DurationMs => Math.Max(0, (End - Start).TotalMilliseconds);
}
