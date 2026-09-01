using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Serializable snapshot of a <see cref="CopilotSession"/> aggregate, stored as a
/// single jsonb column. Raw OTLP spans are intentionally NOT persisted (heavy and
/// reconstructible from a forwarded backend); everything the quality engine and
/// the dashboard need survives a restart.
/// </summary>
public sealed record PersistedSession(
    string Id,
    string? VsCodeSessionId,
    string? AgentName,
    string? Repository,
    string? Branch,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    int AgentInvocations, int Turns,
    int EditsAccepted, int EditsRejected, int ThumbsUp, int ThumbsDown,
    double LinesAdded, double LinesRemoved,
    List<double> TtftMs,
    List<double> ChatDurationMs,
    List<double> SurvivalScores,
    Dictionary<string, int> ModelCalls,
    List<PersistedToolStat> Tools,
    Dictionary<string, int> ErrorTypes,
    List<SessionEvent> Events,
    List<TranscriptEntry> Transcript,
    List<PersistedTurn> TurnStats,
    List<double>? SurvivalFourGram = null,
    List<double>? SurvivalNoRevert = null,
    Dictionary<string, PersistedModelStat>? ModelUsage = null,
    EmitterKind EmitterKind = EmitterKind.Unknown,
    /// <summary>Permission-mode auto-accepts. Defaulted so snapshots written before this
    /// field existed still deserialize — they simply report no auto-applied edits.</summary>
    int EditsAutoAccepted = 0,
    /// <summary>Origin scope used by the k-anonymity floor. Defaulted: snapshots written
    /// before privacy mode existed have no subject, and each then counts as its own.</summary>
    string? SubjectId = null)
{
    public static PersistedSession From(CopilotSession s) => s.Snapshot(x => new PersistedSession(
        x.Id, x.VsCodeSessionId, x.AgentName, x.Repository, x.Branch,
        x.FirstSeen, x.LastSeen,
        x.InputTokens, x.OutputTokens, x.CacheReadTokens, x.CacheCreationTokens,
        x.ChatCalls, x.ChatErrors, x.ToolCalls, x.ToolErrors,
        x.AgentInvocations, x.Turns,
        x.EditsAccepted, x.EditsRejected, x.ThumbsUp, x.ThumbsDown,
        x.LinesAdded, x.LinesRemoved,
        new List<double>(x.TtftMs),
        new List<double>(x.ChatDurationMs),
        new List<double>(x.SurvivalScores),
        new Dictionary<string, int>(x.ModelCalls),
        x.Tools.Select(t => new PersistedToolStat(t.Key, t.Value.Calls, t.Value.Errors, t.Value.TotalMs)).ToList(),
        new Dictionary<string, int>(x.ErrorTypes),
        x.RecentEvents.ToList(),
        new List<TranscriptEntry>(x.Transcript),
        x.TurnList.Select(t => new PersistedTurn(t.TraceId, t.Index, t.Start, t.End,
            t.ChatCalls, t.ChatErrors, t.ToolCalls, t.ToolErrors,
            t.InputTokens, t.OutputTokens, t.TtftTotalMs, t.TtftCount, t.PrimaryModel)).ToList(),
        new List<double>(x.SurvivalFourGram),
        new List<double>(x.SurvivalNoRevert),
        x.ModelUsage.ToDictionary(kv => kv.Key, kv => new PersistedModelStat(
            kv.Value.Calls, kv.Value.InputTokens, kv.Value.OutputTokens, kv.Value.CacheReadTokens)),
        x.EmitterKind,
        x.EditsAutoAccepted,
        x.SubjectId));

    public CopilotSession ToSession()
    {
        var s = new CopilotSession
        {
            Id = Id,
            VsCodeSessionId = VsCodeSessionId,
            EmitterKind = EmitterKind,
            AgentName = AgentName,
            Repository = Repository,
            Branch = Branch,
            SubjectId = SubjectId,
            FirstSeen = FirstSeen,
            LastSeen = LastSeen,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            CacheReadTokens = CacheReadTokens,
            CacheCreationTokens = CacheCreationTokens,
            ChatCalls = ChatCalls,
            ChatErrors = ChatErrors,
            ToolCalls = ToolCalls,
            ToolErrors = ToolErrors,
            AgentInvocations = AgentInvocations,
            Turns = Turns,
            EditsAccepted = EditsAccepted,
            EditsRejected = EditsRejected,
            EditsAutoAccepted = EditsAutoAccepted,
            ThumbsUp = ThumbsUp,
            ThumbsDown = ThumbsDown,
            LinesAdded = LinesAdded,
            LinesRemoved = LinesRemoved
        };
        s.TtftMs.AddRange(TtftMs);
        s.ChatDurationMs.AddRange(ChatDurationMs);
        s.SurvivalScores.AddRange(SurvivalScores);
        foreach (var (k, v) in ModelCalls) s.ModelCalls[k] = v;
        foreach (var t in Tools) s.Tools[t.Name] = (t.Calls, t.Errors, t.TotalMs);
        foreach (var (k, v) in ErrorTypes) s.ErrorTypes[k] = v;
        foreach (var e in Events) s.AddEvent(e);
        s.Transcript.AddRange(Transcript ?? []);
        s.SurvivalFourGram.AddRange(SurvivalFourGram ?? []);
        s.SurvivalNoRevert.AddRange(SurvivalNoRevert ?? []);
        foreach (var (k, v) in ModelUsage ?? new())
            s.ModelUsage[k] = new ModelStat { Calls = v.Calls, InputTokens = v.InputTokens, OutputTokens = v.OutputTokens, CacheReadTokens = v.CacheReadTokens };
        foreach (var t in TurnStats ?? [])
        {
            var turn = new TurnStat
            {
                TraceId = t.TraceId, Index = t.Index, Start = t.Start, End = t.End,
                ChatCalls = t.ChatCalls, ChatErrors = t.ChatErrors,
                ToolCalls = t.ToolCalls, ToolErrors = t.ToolErrors,
                InputTokens = t.InputTokens, OutputTokens = t.OutputTokens,
                TtftTotalMs = t.TtftTotalMs, TtftCount = t.TtftCount, PrimaryModel = t.PrimaryModel
            };
            s.TurnsByTrace[t.TraceId] = turn;
            s.TurnList.Add(turn);
        }
        // AddEvent bumps LastSeen through no path here (it only appends), restore explicitly:
        s.LastSeen = LastSeen;
        return s;
    }
}

public sealed record PersistedToolStat(string Name, int Calls, int Errors, double TotalMs);

public sealed record PersistedModelStat(int Calls, long InputTokens, long OutputTokens, long CacheReadTokens);

public sealed record PersistedTurn(
    string TraceId, int Index, DateTimeOffset Start, DateTimeOffset End,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    long InputTokens, long OutputTokens, double TtftTotalMs, int TtftCount,
    string? PrimaryModel = null);
