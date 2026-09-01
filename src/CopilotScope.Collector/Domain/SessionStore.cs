using System.Collections.Concurrent;
using CopilotScope.Collector.Otlp;

namespace CopilotScope.Collector.Domain;

/// <summary>
/// In-memory store of Copilot sessions. Routes decoded OTLP spans, metric points
/// and log events into per-session aggregates and reports which sessions changed
/// so the realtime layer can broadcast deltas.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, CopilotSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _traceToSession = new(StringComparer.Ordinal);

    // Resource fingerprint (VS Code window / CLI process) → conversation session id.
    // CLI metrics and logs carry no gen_ai.conversation.id and no session.id resource
    // attribute, so without this mapping they'd pile up in an "unattributed" bucket
    // forever instead of enriching the conversation they belong to.
    private readonly ConcurrentDictionary<string, string> _resourceToSession = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _removed = new();
    private long _hostlessSignals;

    /// <summary>
    /// Count of identity-less signals that arrived with neither a host resource attribute
    /// nor a source-connection identity, and were therefore fingerprinted under a shared
    /// <c>nohost</c> scope. Non-zero behind a shared collector means emitter configuration
    /// needs fixing — see docs/TUTORIAL.md §8.
    /// </summary>
    public long HostlessSignals => Interlocked.Read(ref _hostlessSignals);

    /// <summary>Ids of bucket sessions consumed by a merge since the last drain (for persistence cleanup).</summary>
    public List<string> DrainRemoved()
    {
        var list = new List<string>();
        while (_removed.TryDequeue(out var id)) list.Add(id);
        return list;
    }
    private const int MaxSessions = 200;

    public IReadOnlyCollection<CopilotSession> All => (IReadOnlyCollection<CopilotSession>)_sessions.Values;

    public CopilotSession? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Seeds the store with sessions restored from persistence. Live sessions (already
    /// created by concurrent ingest) win over persisted snapshots. Returns count added.
    /// </summary>
    public int Rehydrate(IEnumerable<CopilotSession> sessions)
    {
        var added = 0;
        foreach (var session in sessions)
            if (_sessions.TryAdd(session.Id, session)) added++;
        return added;
    }

    /// <summary>Inserts or replaces a session unconditionally (used by admin seeding — unlike
    /// <see cref="Rehydrate"/>'s add-if-absent semantics, a reseed must overwrite stale data).</summary>
    public void Put(CopilotSession session) => _sessions[session.Id] = session;

    /// <summary>Removes every session whose id matches the predicate. Returns the count removed.</summary>
    public int RemoveWhere(Func<string, bool> predicate)
    {
        var ids = _sessions.Keys.Where(predicate).ToList();
        foreach (var id in ids) Remove(id);
        return ids.Count;
    }

    /// <summary>Removes a session and its trace mappings. Returns true if it existed.</summary>
    public bool Remove(string id)
    {
        var removed = _sessions.TryRemove(id, out _);
        if (removed)
            foreach (var kv in _traceToSession.Where(kv => kv.Value == id).ToList())
                _traceToSession.TryRemove(kv.Key, out _);
        return removed;
    }

    /// <summary>Ingests a decoded batch; returns ids of sessions that were touched.</summary>
    /// <param name="batch">The decoded OTLP batch.</param>
    /// <param name="sourceId">
    /// Identity of the connection the batch arrived on (the remote address, for HTTP ingest).
    /// Process- and service-scoped resource fingerprints are only unique within one machine,
    /// so behind a shared team collector this is what keeps two developers' identity-less
    /// signals from resolving to the same conversation. Null is accepted (in-process callers,
    /// tests) and falls back to a shared scope, counted in <see cref="HostlessSignals"/>.
    /// </param>
    public HashSet<string> Ingest(OtlpBatch batch, string? sourceId = null)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);

        // Pre-pass: spans carrying gen_ai.conversation.id (typically invoke_agent roots)
        // register their trace so that sibling chat/tool spans in the same trace —
        // which do NOT carry the attribute — resolve to the same session regardless
        // of ordering within the batch.
        foreach (var span in batch.Spans)
            if (span.Attr(Sem.ConversationId) is { } conv)
            {
                if (span.TraceId.Length > 0) _traceToSession[span.TraceId] = conv;
                if (ResourceFingerprint(span.Resource, sourceId) is { } fp)
                    RegisterResource(fp, conv, span.Resource);
            }

        // Claude events always name their session; Claude metrics only do so while
        // OTEL_METRICS_INCLUDE_SESSION_ID is on. Registering the events' identity claims
        // whatever the metrics dropped into the "unattributed" bucket for that emitter.
        foreach (var log in batch.Logs)
            if (ClaudeCode.SessionKey(log.EventName, log.Attributes) is { } claudeSession
                && ResourceFingerprint(log.Resource, sourceId) is { } fp)
                RegisterResource(fp, claudeSession, log.Resource);

        foreach (var span in batch.Spans) IngestSpan(span, touched, sourceId);
        foreach (var point in batch.Metrics) IngestMetric(point, touched, sourceId);
        foreach (var log in batch.Logs) IngestLog(log, touched, sourceId);

        TrimIfNeeded();
        return touched;
    }

    // ------------------------------------------------------------------ spans

    private void IngestSpan(OtlpSpan span, HashSet<string> touched, string? sourceId)
    {
        var session = Resolve(SessionKeyFor(span, sourceId), span.Resource);
        if (span.TraceId.Length > 0) _traceToSession[span.TraceId] = session.Id;
        touched.Add(session.Id);
        session.AddSpan(span);

        var op = span.Attr(Sem.Operation) ?? ClaudeCode.Operation(span.Name) ?? Classify(span.Name);
        var isError = span.StatusCode == 2 || span.Attr(Sem.ErrorType) is not null;
        var errorType = span.Attr(Sem.ErrorType);

        session.Apply(s =>
        {
            if (s.EmitterKind == EmitterKind.Unknown) s.EmitterKind = DetectEmitter(span.Resource, span.Name, span.Attributes);
            if (span.Attr(Sem.AgentName) is { } agent) s.AgentName = agent;
            if (span.Attr(Sem.GitRepository) is { } repo) s.Repository = repo;
            if (span.Attr(Sem.GitBranch) is { } branch) s.Branch = branch;
            if (span.Resource.TryGetValue(Sem.SessionId, out var sid)) s.VsCodeSessionId = sid.ToString();
            if (span.Start < s.FirstSeen && span.Start > DateTimeOffset.UnixEpoch) s.FirstSeen = span.Start;
            if (isError && errorType is not null)
                s.ErrorTypes.AddOrUpdate(errorType, 1, (_, c) => c + 1);

            // Per-turn attribution: every span in one invoke_agent trace belongs to one turn.
            var turn = span.TraceId.Length > 0 ? s.TurnFor(span.TraceId, span.Start) : null;
            if (turn is not null)
            {
                if (span.Start < turn.Start && span.Start > DateTimeOffset.UnixEpoch) turn.Start = span.Start;
                var end = span.Start.AddMilliseconds(span.DurationMs);
                if (end > turn.End) turn.End = end;
            }

            switch (op)
            {
                case "invoke_agent":
                    s.AgentInvocations++;
                    // Token totals on invoke_agent are cumulative for the invocation;
                    // per-call chat spans are the authoritative incremental source,
                    // so only take turn count here.
                    if ((span.AttrLong(Sem.TurnCount) ?? span.AttrLong(Sem.TurnCountGh)) is { } turns) s.Turns += (int)turns;
                    break;

                case "chat":
                    s.ChatCalls++;
                    if (isError) s.ChatErrors++;
                    s.InputTokens += span.AttrLong(Sem.InputTokens) ?? 0;
                    s.OutputTokens += span.AttrLong(Sem.OutputTokens) ?? 0;
                    s.CacheReadTokens += span.AttrLong(Sem.CacheReadTokens) ?? 0;
                    s.CacheCreationTokens += span.AttrLong(Sem.CacheCreationTokens) ?? 0;
                    var model = span.Attr(Sem.ResponseModel) ?? span.Attr(Sem.RequestModel) ?? "unknown";
                    s.ModelCalls.AddOrUpdate(model, 1, (_, c) => c + 1);
                    var inTok = span.AttrLong(Sem.InputTokens) ?? 0;
                    var outTok = span.AttrLong(Sem.OutputTokens) ?? 0;
                    var cacheTok = span.AttrLong(Sem.CacheReadTokens) ?? 0;
                    s.ModelUsage.AddOrUpdate(model,
                        new ModelStat { Calls = 1, InputTokens = inTok, OutputTokens = outTok, CacheReadTokens = cacheTok },
                        (_, e) => { e.Calls++; e.InputTokens += inTok; e.OutputTokens += outTok; e.CacheReadTokens += cacheTok; return e; });
                    var ttftValue = span.AttrLong(Sem.TimeToFirstToken)
                                 ?? span.AttrLong(Sem.TimeToFirstTokenGh)
                                 ?? span.AttrLong(Sem.TimeToFirstTokenSrv);
                    if (ttftValue is { } ttft && ttft > 0) AddBounded(s.TtftMs, ttft);
                    AddBounded(s.ChatDurationMs, span.DurationMs);

                    if (turn is not null)
                    {
                        turn.ChatCalls++;
                        if (isError) turn.ChatErrors++;
                        turn.InputTokens += span.AttrLong(Sem.InputTokens) ?? 0;
                        turn.OutputTokens += span.AttrLong(Sem.OutputTokens) ?? 0;
                        if (ttftValue is { } t2 && t2 > 0) { turn.TtftTotalMs += t2; turn.TtftCount++; }
                        turn.PrimaryModel ??= model;
                    }

                    // Prompt/response content is only present when captureContent is on.
                    var prompt = span.Attr(Sem.InputMessages) ?? span.Attr(Sem.Prompt);
                    var response = span.Attr(Sem.OutputMessages) ?? span.Attr(Sem.Completion);
                    if (prompt is not null || response is not null)
                        s.AddTranscript(span.Start, model, prompt, response, turn?.Index ?? -1);
                    break;

                case "execute_tool":
                    s.ToolCalls++;
                    if (isError) s.ToolErrors++;
                    var tool = span.Attr(Sem.ToolName) ?? span.Name;
                    s.Tools.AddOrUpdate(tool,
                        (1, isError ? 1 : 0, span.DurationMs),
                        (_, t) => (t.Calls + 1, t.Errors + (isError ? 1 : 0), t.TotalMs + span.DurationMs));
                    if (turn is not null)
                    {
                        turn.ToolCalls++;
                        if (isError) turn.ToolErrors++;
                    }
                    break;
            }

            ClaudeCode.ApplySpanExtras(s, span, turn);
        });

        session.AddEvent(new SessionEvent(span.Start, op,
            op switch
            {
                "chat" => $"{span.Name} · {span.AttrLong(Sem.InputTokens) ?? 0}→{span.AttrLong(Sem.OutputTokens) ?? 0} tok · {span.DurationMs:F0} ms{(isError ? " · ERROR" : "")}",
                "execute_tool" => $"{span.Attr(Sem.ToolName) ?? span.Name} · {span.DurationMs:F0} ms{(isError ? " · ERROR" : "")}",
                "invoke_agent" => $"{span.Name} · {span.DurationMs / 1000:F1} s",
                _ => span.Name
            }));
    }

    private static string Classify(string spanName)
    {
        if (spanName.StartsWith("invoke_agent", StringComparison.Ordinal)) return "invoke_agent";
        if (spanName.StartsWith("chat", StringComparison.Ordinal)) return "chat";
        if (spanName.StartsWith("execute_tool", StringComparison.Ordinal)) return "execute_tool";
        if (spanName.StartsWith("execute_hook", StringComparison.Ordinal)) return "execute_hook";
        return "other";
    }

    // ---------------------------------------------------------------- metrics

    private void IngestMetric(OtlpMetricPoint point, HashSet<string> touched, string? sourceId)
    {
        var session = Resolve(SessionKeyFor(point.MetricName, point.Attributes, point.Resource, sourceId), point.Resource);
        touched.Add(session.Id);

        session.Apply(s =>
        {
            if (s.EmitterKind == EmitterKind.Unknown) s.EmitterKind = DetectEmitter(point.Resource, point.MetricName, point.Attributes);
            if (ClaudeCode.TryApplyMetric(s, point)) return;

            switch (Sem.Normalize(point.MetricName))
            {
                case Sem.MEditAcceptance:
                case Sem.MChatEditOutcome:
                    var action = point.Attr("copilot_chat.edit.action")
                                 ?? point.Attr("copilot_chat.edit.outcome")
                                 ?? point.Attr("action") ?? point.Attr("outcome") ?? "";
                    var delta = (int)point.Value;
                    if (action.Contains("accept", StringComparison.OrdinalIgnoreCase) ||
                        action.Contains("saved", StringComparison.OrdinalIgnoreCase)) s.EditsAccepted += delta;
                    else if (action.Contains("reject", StringComparison.OrdinalIgnoreCase)) s.EditsRejected += delta;
                    break;

                case Sem.MUserFeedback:
                    var vote = point.Attr("copilot_chat.feedback.vote") ?? point.Attr("vote") ?? "";
                    if (vote.Contains("up", StringComparison.OrdinalIgnoreCase)) s.ThumbsUp += (int)point.Value;
                    else if (vote.Contains("down", StringComparison.OrdinalIgnoreCase)) s.ThumbsDown += (int)point.Value;
                    break;

                case Sem.MLinesOfCode:
                    var kind = point.Attr("copilot_chat.lines.kind") ?? point.Attr("kind") ?? "";
                    if (kind.Contains("remov", StringComparison.OrdinalIgnoreCase)) s.LinesRemoved += point.Value;
                    else s.LinesAdded += point.Value;
                    break;

                case Sem.MSurvivalFourGram:
                    // Histogram: Value = sum of scores, Count = samples → store the mean per point.
                    if (point.Count > 0)
                    {
                        AddBounded(s.SurvivalScores, point.Value / point.Count);
                        AddBounded(s.SurvivalFourGram, point.Value / point.Count);
                    }
                    break;
                case Sem.MSurvivalNoRevert:
                    if (point.Count > 0)
                    {
                        AddBounded(s.SurvivalScores, point.Value / point.Count);
                        AddBounded(s.SurvivalNoRevert, point.Value / point.Count);
                    }
                    break;

                case Sem.MTtft when point.Count > 0:
                    // Seconds histogram → ms mean. Span attribute is preferred; only add if spans absent.
                    if (s.TtftMs.Count == 0) AddBounded(s.TtftMs, point.Value / point.Count * 1000.0);
                    break;
            }
        });
    }

    // ------------------------------------------------------------------- logs

    private void IngestLog(OtlpLogEvent log, HashSet<string> touched, string? sourceId)
    {
        string? key = SessionKeyFor(log.EventName, log.Attributes, log.Resource, sourceId);
        if (key is null && log.TraceId is not null && _traceToSession.TryGetValue(log.TraceId, out var mapped))
            key = mapped;

        var session = Resolve(key, log.Resource);
        touched.Add(session.Id);

        var name = ClaudeCode.Describe(log) ?? log.EventName ?? log.Body ?? "log";
        session.Apply(s =>
        {
            if (s.EmitterKind == EmitterKind.Unknown) s.EmitterKind = DetectEmitter(log.Resource, log.EventName, log.Attributes);
            if (ClaudeCode.TryApplyLog(s, log)) return;

            switch (log.EventName is null ? null : Sem.Normalize(log.EventName))
            {
                case Sem.EUserFeedback:
                    var vote = Attr(log, "copilot_chat.feedback.vote") ?? Attr(log, "vote") ?? log.Body ?? "";
                    if (vote.Contains("up", StringComparison.OrdinalIgnoreCase)) s.ThumbsUp++;
                    else if (vote.Contains("down", StringComparison.OrdinalIgnoreCase)) s.ThumbsDown++;
                    break;
                case Sem.EEditFeedback:
                    var act = Attr(log, "copilot_chat.edit.action") ?? Attr(log, "action") ?? "";
                    if (act.Contains("accept", StringComparison.OrdinalIgnoreCase)) s.EditsAccepted++;
                    else if (act.Contains("reject", StringComparison.OrdinalIgnoreCase)) s.EditsRejected++;
                    break;
            }

            // Content capture, log-event flavor: depending on the emitter version,
            // prompt/response text arrives as span attributes (handled in IngestSpan)
            // OR as log records — gen_ai.content.* events carry it in the body,
            // newer emitters use gen_ai.* message attributes on the record.
            var prompt = Attr(log, Sem.InputMessages) ?? Attr(log, Sem.Prompt)
                ?? (log.EventName is "gen_ai.content.prompt" or "gen_ai.user.message" ? log.Body : null);
            var response = Attr(log, Sem.OutputMessages) ?? Attr(log, Sem.Completion)
                ?? (log.EventName is "gen_ai.content.completion" or "gen_ai.assistant.message" or "gen_ai.choice" ? log.Body : null);
            if (prompt is not null || response is not null)
            {
                var turnIdx = log.TraceId is not null && s.TurnsByTrace.TryGetValue(log.TraceId, out var t) ? t.Index : -1;
                s.AddTranscript(log.Time, Attr(log, Sem.ResponseModel) ?? Attr(log, Sem.RequestModel) ?? "unknown",
                    prompt, response, turnIdx);
            }
        });

        session.AddEvent(new SessionEvent(log.Time, "event", name));

        static string? Attr(OtlpLogEvent l, string k) =>
            l.Attributes.TryGetValue(k, out var v) ? v.ToString() : null;
    }

    // ---------------------------------------------------------------- helpers

    private string? SessionKeyFor(OtlpSpan span, string? sourceId) =>
        span.Attr(Sem.ConversationId)
        ?? ClaudeCode.SessionKey(span.Name, span.Attributes)
        ?? (span.TraceId.Length > 0 && _traceToSession.TryGetValue(span.TraceId, out var mapped) ? mapped : null)
        ?? ResourceFallback(span.Resource, sourceId);

    private string? SessionKeyFor(string? signalName, Dictionary<string, AttrValue> attrs,
        Dictionary<string, AttrValue> resource, string? sourceId) =>
        (attrs.TryGetValue(Sem.ConversationId, out var c) ? c.ToString() : null)
        ?? ClaudeCode.SessionKey(signalName, attrs)
        ?? ResourceFallback(resource, sourceId);

    /// <summary>
    /// Identity-less signals resolve through the fingerprint mapping to the most
    /// recently active conversation from the same emitter; if none is known yet,
    /// they wait in a per-emitter "unattributed:" bucket that gets merged into the
    /// conversation session as soon as one identifies itself.
    /// </summary>
    private string? ResourceFallback(Dictionary<string, AttrValue> resource, string? sourceId)
    {
        if (ResourceFingerprint(resource, sourceId) is not { } fp) return null; // → plain "unattributed"
        return _resourceToSession.TryGetValue(fp, out var mapped) ? mapped : $"unattributed:{fp}";
    }

    /// <summary>
    /// Stable identity for the emitter a signal came from, used to attach identity-less
    /// metrics and logs to the conversation they belong to.
    ///
    /// The first two forms are session-scoped and globally unique on their own — a VS Code
    /// window id or a Claude Code session id names one conversation on one machine. Every
    /// form below them is process- or service-scoped, and therefore unique only *within* a
    /// machine: two developers running the same assistant produce the same
    /// <c>service.name</c>, and independent machines hand out the same pids routinely.
    /// Behind a shared team collector those would collide and "last active conversation
    /// wins" would cross-attribute one developer's tokens, edits and feedback to another's
    /// conversation, so the weak forms are scoped by host.
    /// </summary>
    private string? ResourceFingerprint(Dictionary<string, AttrValue> resource, string? sourceId)
    {
        if (resource.TryGetValue(Sem.SessionId, out var sid)) return $"vscode:{sid}";
        if (resource.TryGetValue(Sem.ClaudeCodeSessionId, out var ccSid)) return $"claude:{ccSid}";

        var svc = resource.TryGetValue(Sem.ServiceName, out var name) ? name.ToString() : null;
        var hasWeakForm = resource.ContainsKey("service.instance.id") || svc is not null;
        if (!hasWeakForm) return null;

        var host = HostScope(resource, sourceId);
        if (host is null)
        {
            // No host attributes and no source connection: cannot tell machines apart.
            // Preserves single-host behavior; on a shared collector the counter is the
            // operator's signal that emitters need host.name (or a proxy is hiding it).
            Interlocked.Increment(ref _hostlessSignals);
            host = "nohost";
        }

        if (resource.TryGetValue("service.instance.id", out var inst)) return $"inst:{host}:{inst}";
        if (resource.TryGetValue("process.pid", out var pid) && svc is not null) return $"proc:{host}:{svc}:{pid}";
        return $"svc:{host}:{svc}";
    }

    /// <summary>
    /// Machine discriminator for the weak fingerprint forms. Prefers the emitter's own
    /// host attributes; falls back to the connection the batch arrived on. The connection
    /// fallback is weaker — clients behind one NAT or reverse proxy share an address — so
    /// <c>host.name</c> is what a shared deployment should configure.
    /// </summary>
    private static string? HostScope(Dictionary<string, AttrValue> resource, string? sourceId)
    {
        if (resource.TryGetValue("host.id", out var hostId)) return $"host:{hostId}";
        if (resource.TryGetValue("host.name", out var hostName)) return $"host:{hostName}";
        if (resource.TryGetValue("k8s.pod.name", out var pod)) return $"pod:{pod}";
        if (resource.TryGetValue("container.id", out var container)) return $"ctr:{container}";
        return string.IsNullOrEmpty(sourceId) ? null : $"src:{sourceId}";
    }

    /// <param name="signalName">
    /// Span, metric or event name the emitter is being detected from. Claude Code and Cowork
    /// may ship a bare resource, in which case the claude_code.* namespace is the only thing
    /// that identifies them.
    /// </param>
    /// <param name="attributes">Attributes of that same signal, read alongside the name.</param>
    private static EmitterKind DetectEmitter(Dictionary<string, AttrValue> resource,
        string? signalName = null, Dictionary<string, AttrValue>? attributes = null)
    {
        if (resource.ContainsKey(Sem.SessionId)) return EmitterKind.VSCode;
        if (resource.TryGetValue(Sem.ServiceName, out var svc))
        {
            var svcName = svc.ToString();
            // Checked before claude-code: the desktop app's service name names both.
            if (svcName.Contains(Sem.ServiceNameCowork, StringComparison.OrdinalIgnoreCase))
                return EmitterKind.Cowork;
            if (svcName.Contains(Sem.ServiceNameClaudeCode, StringComparison.OrdinalIgnoreCase))
                return EmitterKind.ClaudeCode;
            if (svcName.Contains(Sem.ServiceNameCursor, StringComparison.OrdinalIgnoreCase))
                return EmitterKind.Cursor;
        }
        if (resource.TryGetValue(Sem.GenAiSystem, out var genAiSys) &&
            genAiSys.ToString().Contains("anthropic", StringComparison.OrdinalIgnoreCase))
            return EmitterKind.ClaudeCode;
        if (ClaudeCode.Signal(signalName, attributes) is not null) return EmitterKind.ClaudeCode;
        if (resource.ContainsKey("process.pid")) return EmitterKind.CLI;
        return EmitterKind.Unknown;
    }

    private void RegisterResource(string fingerprint, string conversationId,
        Dictionary<string, AttrValue> resource)
    {
        _resourceToSession[fingerprint] = conversationId; // last active conversation wins

        // Claim anything that piled up under THIS fingerprint before the conversation
        // identified itself. The plain "unattributed" bucket is deliberately not claimed:
        // it holds signals whose emitter could not be fingerprinted at all, so merging it
        // would hand one arbitrary conversation another machine's telemetry — the same
        // cross-attribution the host scoping above exists to prevent. An orphan bucket is
        // the safer failure: it stays visible and wrong-for-nobody.
        var bucketId = $"unattributed:{fingerprint}";
        if (_sessions.TryRemove(bucketId, out var bucket))
        {
            var target = Resolve(conversationId, resource);
            target.MergeFrom(bucket);
            _removed.Enqueue(bucketId);
        }
    }

    private CopilotSession Resolve(string? key, Dictionary<string, AttrValue> resource)
    {
        key ??= "unattributed";
        return _sessions.GetOrAdd(key, id => new CopilotSession
        {
            Id = id,
            VsCodeSessionId = resource.TryGetValue(Sem.SessionId, out var s) ? s.ToString() : null
        });
    }

    /// <summary>Appends to a bounded distribution list, keeping the most recent 1000 samples.</summary>
    internal static void AddBounded(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > 1000) list.RemoveRange(0, list.Count - 1000);
    }

    private void TrimIfNeeded()
    {
        if (_sessions.Count <= MaxSessions) return;
        foreach (var stale in _sessions.Values.OrderBy(s => s.LastSeen).Take(_sessions.Count - MaxSessions))
            _sessions.TryRemove(stale.Id, out _);
    }
}
