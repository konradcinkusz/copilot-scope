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

    /// <summary>
    /// Every agent that took part in this session, in the order the collector first saw each
    /// name. Copilot CLI emits one <c>invoke_agent</c> span per agent and subagent, each naming
    /// itself in <c>gen_ai.agent.name</c>, so a multi-agent workflow reports several — where
    /// <see cref="AgentName"/> keeps only one, and keeps its own meaning: it is not derived from
    /// this list.
    ///
    /// Bounded because the names come from the emitter: distinct under ordinal comparison, blank
    /// names ignored, each cut to <see cref="MaxAgentNameChars"/> characters and the list to
    /// <see cref="MaxAgentNames"/>, so an emitter that minted a name per call cannot grow a
    /// session without limit. Changed only through <see cref="AddAgentName"/>, which is what
    /// holds those bounds.
    /// </summary>
    public IReadOnlyList<string> AgentNames => _agentNames;
    private readonly List<string> _agentNames = new();
    public const int MaxAgentNames = 32;
    public const int MaxAgentNameChars = 200;

    public string? Repository { get; set; }
    public string? Branch { get; set; }

    /// <summary>
    /// Where this session's data came from. <c>otel</c> for anything the collector received
    /// over OTLP — the default, and the only value ingest ever sets — and <c>log-import</c>
    /// for a session reconstructed from an assistant's own local transcript files.
    ///
    /// Provenance is not decoration: an imported session carries no latency samples and no
    /// edit-decision or feedback events, so it is genuinely measured on less evidence than a
    /// live one. The confidence model already reflects that (missing components carry no
    /// weight), but a reader comparing two scores needs to be told <i>why</i> one rests on
    /// less, and that is what the origin says.
    /// </summary>
    public string Origin { get; set; } = SessionOrigin.Otel;

    /// <summary>
    /// Which machine/person this session's telemetry came from — the host scope the resource
    /// fingerprint already computes, kept so a view can count distinct subjects rather than
    /// distinct sessions. Under privacy mode the underlying attributes are pseudonymized
    /// before this is derived, so it is a token; without it, it is the plain host name.
    /// Null when the emitter sends nothing that identifies an origin.
    /// </summary>
    public string? SubjectId { get; set; }
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
    /// Records an agent as having taken part in this session (see <see cref="AgentNames"/>).
    /// Blank names, repeats and anything past the cap are ignored. Must be called while holding
    /// the session lock (i.e. inside Apply), or on a session nothing else can see yet.
    /// </summary>
    public void AddAgentName(string? name)
    {
        if (name is null || _agentNames.Count >= MaxAgentNames) return;
        if (name.Length > MaxAgentNameChars)
        {
            // Never cut between the halves of a surrogate pair: a lone surrogate is not valid
            // UTF-16, and Postgres refuses it in a jsonb value — the whole snapshot would fail
            // to write.
            var cut = char.IsHighSurrogate(name[MaxAgentNameChars - 1]) ? MaxAgentNameChars - 1 : MaxAgentNameChars;
            name = name[..cut];
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var known in _agentNames)
            if (string.Equals(known, name, StringComparison.Ordinal)) return;
        _agentNames.Add(name);
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
            // Union of both agent lists in first-seen order: the session that started earlier
            // saw its agents earlier, so its names lead. Decided before FirstSeen is widened.
            s.UnionAgentNames(o.AgentNames ?? [], otherFirst: o.FirstSeen < s.FirstSeen);
            if (o.FirstSeen < s.FirstSeen) s.FirstSeen = o.FirstSeen;
            if (o.LastSeen > s.LastSeen) s.LastSeen = o.LastSeen;
            s.AgentName ??= o.AgentName;
            s.Repository ??= o.Repository;
            s.Branch ??= o.Branch;
            s.VsCodeSessionId ??= o.VsCodeSessionId;

            s.SubjectId ??= o.SubjectId;
            // An imported session merged into a live one is no longer purely imported, and
            // claiming otherwise would understate the evidence behind its score.
            if (s.Origin != o.Origin && o.Origin == SessionOrigin.Otel) s.Origin = SessionOrigin.Otel;
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

    /// <summary>Must be called while holding the session lock (i.e. inside Apply).</summary>
    private void UnionAgentNames(IEnumerable<string> other, bool otherFirst)
    {
        if (!otherFirst)
        {
            foreach (var name in other) AddAgentName(name);
            return;
        }
        var mine = _agentNames.ToList();
        _agentNames.Clear();
        foreach (var name in other) AddAgentName(name);
        foreach (var name in mine) AddAgentName(name);
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
