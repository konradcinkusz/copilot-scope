using System.Collections.Concurrent;
using CopilotScope.Collector.Otlp;

namespace CopilotScope.Collector.Domain;

/// <summary>
/// Sessions CopilotScope started itself, which must not be scored.
///
/// Why this exists:
///
///   <see cref="SelfObservation"/> keeps CopilotScope's own <em>tool calls</em> out of the score.
///   A function run from the dashboard (docs/FUNCTIONS.md) is bigger than a tool call: it is a
///   whole session of the user's own assistant, launched to read the review pack. If that
///   assistant is connected, the run is ingested over OTLP like any other session; if it keeps a
///   transcript, the scanner imports it. Either way the base would grow a session whose only
///   subject is the base — one that reads a few files, makes a handful of successful calls and
///   scores well — and every figure the next review reads would have moved because the last
///   one ran. A review that changes the numbers it reviews is not a review.
///
///   So the exclusion grows from tool calls to sessions (ADR-005, decision 6), in two ways that
///   do not depend on each other:
///
///   1. <b>By id.</b> The launcher chooses the session id where the assistant lets it (Claude
///      Code's <c>--session-id</c>) and registers it here <em>before</em> the process starts, so
///      no signal can arrive first. <see cref="SessionStore.Ingest"/> drops every signal keyed to
///      a registered id, and <c>POST /api/import</c> refuses a registered id, which covers the
///      scanner too: it reaches the store only through that endpoint.
///   2. <b>By marker.</b> The launcher puts <see cref="MarkerAttribute"/> in the child's
///      <c>OTEL_RESOURCE_ATTRIBUTES</c>. An assistant whose session id cannot be chosen (Copilot
///      CLI), or whose telemetry a managed policy forces on after the launcher switched it off,
///      still stamps every signal with the marker, and ingest drops those too.
///
///   The launcher also switches the child's telemetry off and asks for no transcript, so in the
///   ordinary case nothing arrives at all. This class is what holds when that is overridden.
///
/// Unlike a self-observing tool call, nothing of an observer session is kept — not even a
/// timeline entry: there is no session of the user's to attach one to.
/// </summary>
public sealed class ObserverRegistry
{
    /// <summary>The resource attribute a launched run carries. Any value but false/0 marks it.</summary>
    public const string MarkerAttribute = "copilotscope.observer";

    /// <summary>A registry larger than this was not filled by a person clicking a button.</summary>
    private const int MaxRegistered = 10_000;

    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);

    /// <summary>Traces a dropped span belonged to: the sibling spans of an observer's turn carry no
    /// conversation id of their own, and would otherwise land in a resource-fingerprint bucket.</summary>
    private readonly ConcurrentDictionary<string, byte> _traces = new(StringComparer.Ordinal);

    private long _dropped;

    /// <summary>Signals — spans, metric points and log events — dropped because an observer sent them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The ids registered so far, for a host that persists them across restarts.</summary>
    public IReadOnlyCollection<string> Registered => (IReadOnlyCollection<string>)_sessions.Keys;

    /// <summary>Registers a session id CopilotScope is about to launch. Idempotent.</summary>
    public void Register(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        if (_sessions.Count >= MaxRegistered && !_sessions.ContainsKey(sessionId))
            throw new InvalidOperationException($"More than {MaxRegistered} observer sessions are registered.");
        _sessions.TryAdd(sessionId, 0);
    }

    public bool IsRegistered(string? sessionId) => sessionId is { Length: > 0 } && _sessions.ContainsKey(sessionId);

    /// <summary>Whether a resource carries the observer marker.</summary>
    public static bool IsMarked(IReadOnlyDictionary<string, AttrValue> resource)
    {
        if (!resource.TryGetValue(MarkerAttribute, out var value)) return false;
        if (value.B is { } flag) return flag;
        var text = value.ToString().Trim();
        return !(text.Length == 0
                 || text.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || text == "0");
    }

    /// <summary>
    /// Takes every signal an observer sent out of the batch, in place, before anything aggregates
    /// it. A span, point or record goes when its resource carries the marker, when any id it is
    /// keyed by — <c>gen_ai.conversation.id</c>, or <c>session.id</c> as an attribute (Claude Code)
    /// or on the resource (VS Code) — is registered, or when it shares a trace with a span that
    /// went. Returns how many signals were dropped.
    /// </summary>
    public int Filter(OtlpBatch batch)
    {
        if (_sessions.IsEmpty && _traces.IsEmpty && !AnyMarked(batch)) return 0;

        // Spans first, so a log record that only names its trace follows the span it belongs to.
        var dropped = batch.Spans.RemoveAll(span =>
        {
            if (!Observed(span.Resource, span.Attributes)
                && !(span.TraceId.Length > 0 && _traces.ContainsKey(span.TraceId))) return false;
            if (span.TraceId.Length > 0) RememberTrace(span.TraceId);
            return true;
        });
        dropped += batch.Metrics.RemoveAll(point => Observed(point.Resource, point.Attributes));
        dropped += batch.Logs.RemoveAll(log =>
            Observed(log.Resource, log.Attributes)
            || (log.TraceId is { Length: > 0 } trace && _traces.ContainsKey(trace)));

        if (dropped > 0) Interlocked.Add(ref _dropped, dropped);
        return dropped;
    }

    private bool Observed(Dictionary<string, AttrValue> resource, Dictionary<string, AttrValue> attributes) =>
        IsMarked(resource)
        || IsRegistered(Text(attributes, Sem.ConversationId))
        || IsRegistered(Text(attributes, Sem.SessionId))
        || IsRegistered(Text(resource, Sem.SessionId));

    private static bool AnyMarked(OtlpBatch batch) =>
        batch.Spans.Any(s => IsMarked(s.Resource))
        || batch.Metrics.Any(m => IsMarked(m.Resource))
        || batch.Logs.Any(l => IsMarked(l.Resource));

    private void RememberTrace(string traceId)
    {
        // Bounded like the store's own maps: a trace id is only needed while its turn is arriving.
        if (_traces.Count >= MaxRegistered) _traces.Clear();
        _traces.TryAdd(traceId, 0);
    }

    private static string? Text(Dictionary<string, AttrValue> values, string key) =>
        values.TryGetValue(key, out var value) ? value.ToString() : null;
}
