using System.Globalization;
using CopilotScope.Collector.Otlp;

namespace CopilotScope.Collector.Domain;

/// <summary>
/// Maps the native OpenTelemetry schema of Claude Code (the CLI) and Claude Cowork
/// (the agent surface in the Claude desktop app) onto the shared session aggregates.
///
/// Both speak a dialect the rest of the collector does not: a default Claude Code
/// install emits <c>claude_code.*</c> metrics and log events and <b>no spans at all</b>
/// — the gen_ai.* spans <see cref="Sem"/> describes only appear behind
/// <c>CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1</c>. Cowork is narrower still: log events
/// only, no metrics and no traces. Log events are therefore the one signal every
/// Claude surface has, and this class treats them as the source of truth for calls,
/// tokens, tools, edit decisions and turns.
///
/// Reference: https://code.claude.com/docs/en/monitoring-usage
/// </summary>
public static class ClaudeCode
{
    private const string Prefix = "claude_code.";

    /// <summary>Log events that carry session signal (the rest are consumed and dropped).</summary>
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "user_prompt", "assistant_response", "tool_result", "tool_decision",
        "api_request", "api_error", "api_refusal"
    };

    /// <summary>Tools whose accept/reject decision is a decision about generated code.</summary>
    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal)
    {
        "Edit", "MultiEdit", "Write", "NotebookEdit"
    };

    /// <summary>
    /// Strips the <c>claude_code.</c> prefix off a signal name, or returns null when the name is
    /// not a Claude signal. The event name reaches the collector either as the log record's own
    /// event name (<c>claude_code.user_prompt</c>) or as a bare <c>event.name</c> attribute
    /// (<c>user_prompt</c>) depending on exporter version, so both spellings are accepted — but
    /// the bare one only on records that also name a session, since "api_request" on its own is
    /// too generic a name to claim away from another emitter.
    /// </summary>
    public static string? Signal(string? name, Dictionary<string, AttrValue>? attributes) =>
        name is null ? null
        : name.StartsWith(Prefix, StringComparison.Ordinal) ? name[Prefix.Length..]
        : Events.Contains(name) && attributes?.ContainsKey(Sem.SessionId) == true ? name
        : null;

    /// <summary>
    /// Conversation key for a Claude signal, or null when it isn't one. Claude carries
    /// <c>session.id</c> as a point/record attribute rather than a resource attribute, so
    /// without this every session on the machine would collapse into a single
    /// resource-fingerprint bucket.
    /// </summary>
    public static string? SessionKey(string? signalName, Dictionary<string, AttrValue> attributes) =>
        Signal(signalName, attributes) is null ? null
        : attributes.TryGetValue(Sem.SessionId, out var sid) && sid.ToString() is { Length: > 0 } id ? id
        : null;

    /// <summary>
    /// Operation name (in the gen_ai.operation.name vocabulary the span aggregation switches on)
    /// for a Claude Code beta trace span. Only the interaction root maps: the llm_request and
    /// tool spans mirror log events call for call, and counting both would double every token.
    /// </summary>
    public static string? Operation(string spanName) =>
        spanName == "claude_code.interaction" ? "invoke_agent" : null;

    // ---------------------------------------------------------------------- metrics

    /// <summary>
    /// Folds a <c>claude_code.*</c> metric point into the session. Returns true when the point
    /// was a Claude metric — handled or deliberately dropped — so the caller skips Copilot routing.
    /// </summary>
    public static bool TryApplyMetric(CopilotSession s, OtlpMetricPoint point)
    {
        if (Signal(point.MetricName, point.Attributes) is not { } metric) return false;

        switch (metric)
        {
            case "lines_of_code.count":
                // The only edit-volume signal Claude has; no log event duplicates it.
                if (point.Attr("type") is { } type && type.Contains("remov", StringComparison.OrdinalIgnoreCase))
                    s.LinesRemoved += point.Value;
                else
                    s.LinesAdded += point.Value;
                break;

            // token.usage and code_edit_tool.decision describe exactly the calls and decisions
            // that api_request / tool_decision events already report, but carry no request id
            // or tool_use id, so they cannot be de-duplicated against them. The events win
            // because they are the only signal Cowork emits at all — adding these on top would
            // double-count every token and every accepted edit. Keep OTEL_LOGS_EXPORTER=otlp.
            case "token.usage":
            case "code_edit_tool.decision":

            // Consumed so they don't fall through to Copilot metric routing under their
            // normalized copilot_chat.* alias. No session field carries them yet.
            case "session.count":
            case "cost.usage":
            case "active_time.total":
            case "commit.count":
            case "pull_request.count":
            default:
                break;
        }

        return true;
    }

    // ------------------------------------------------------------------- log events

    /// <summary>
    /// Folds a <c>claude_code.*</c> log event into the session. Returns true when the record
    /// was a Claude event, so the caller skips Copilot routing. Must be called while holding
    /// the session lock (i.e. inside <see cref="CopilotSession.Apply"/>).
    /// </summary>
    public static bool TryApplyLog(CopilotSession s, OtlpLogEvent log)
    {
        if (Signal(log.EventName, log.Attributes) is not { } evt) return false;

        var turn = TurnFor(s, log);
        var turnIndex = turn?.Index ?? -1;

        switch (evt)
        {
            case "user_prompt":
                // One user prompt starts one turn. TurnFor already created it, so tracking the
                // list keeps the counter right whether turns came from events or from beta spans;
                // when neither a trace id nor a prompt.id is present the prompt is all we have.
                if (turn is not null) s.Turns = Math.Max(s.Turns, s.TurnList.Count);
                else s.Turns++;
                if (Attr(log, "prompt") is { } prompt)   // only present with OTEL_LOG_USER_PROMPTS=1
                    s.AddTranscript(log.Time, "user", prompt, null, turnIndex);
                break;

            case "assistant_response":
                if (Attr(log, "response") is { } response) // OTEL_LOG_ASSISTANT_RESPONSES=1
                    s.AddTranscript(log.Time, Attr(log, "model") ?? "unknown", null, response, turnIndex);
                break;

            case "api_request":
                RecordChat(s, turn,
                    model: Attr(log, "model") ?? "unknown",
                    input: AttrLong(log, "input_tokens") ?? 0,
                    output: AttrLong(log, "output_tokens") ?? 0,
                    cacheRead: AttrLong(log, "cache_read_tokens") ?? 0,
                    cacheCreation: AttrLong(log, "cache_creation_tokens") ?? 0,
                    durationMs: AttrDouble(log, "duration_ms"),
                    isError: false);
                break;

            case "api_error":
            case "api_refusal":
                // A failed request is still an attempted call — the span path counts an errored
                // chat span in both ChatCalls and ChatErrors, and this keeps the ratio comparable.
                RecordChat(s, turn, Attr(log, "model") ?? "unknown", 0, 0, 0, 0,
                    AttrDouble(log, "duration_ms"), isError: true);
                s.ErrorTypes.AddOrUpdate(
                    Attr(log, "status_code") is { } code ? $"http_{code}" : evt, 1, (_, c) => c + 1);
                break;

            case "tool_result":
                var failed = string.Equals(Attr(log, "success"), "false", StringComparison.OrdinalIgnoreCase);
                RecordTool(s, turn, Attr(log, "tool_name") ?? "unknown", failed, AttrDouble(log, "duration_ms") ?? 0);
                if (failed)
                    s.ErrorTypes.AddOrUpdate(Attr(log, "error_type") ?? "tool_error", 1, (_, c) => c + 1);
                break;

            case "tool_decision":
                // Claude asks permission per tool call; only decisions about the code-editing
                // tools mean what "edit acceptance" means everywhere else in the scoring.
                if (!EditTools.Contains(Attr(log, "tool_name") ?? "")) break;
                var decision = Attr(log, "decision");
                // The event's `source` says who decided. Under acceptEdits or
                // bypassPermissions the answer is "the config did" (source=config), and
                // treating that as a human accepting the code would let a permission flag
                // inflate the acceptance component. Absent source stays human: emitters
                // that predate the attribute only fire the event on a real prompt.
                var source = Attr(log, "source");
                var byHuman = source is null || source.StartsWith("user", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(decision, "accept", StringComparison.OrdinalIgnoreCase))
                {
                    if (byHuman) s.EditsAccepted++;
                    else s.EditsAutoAccepted++;
                }
                else if (string.Equals(decision, "reject", StringComparison.OrdinalIgnoreCase) && byHuman)
                    s.EditsRejected++;
                break;
        }

        return true;
    }

    /// <summary>Timeline summary for a Claude event, or null to fall back to the raw event name.</summary>
    public static string? Describe(OtlpLogEvent log) => Signal(log.EventName, log.Attributes) switch
    {
        "api_request" => $"api_request · {Attr(log, "model") ?? "?"} · " +
                         $"{AttrLong(log, "input_tokens") ?? 0}→{AttrLong(log, "output_tokens") ?? 0} tok · " +
                         $"{AttrDouble(log, "duration_ms") ?? 0:F0} ms",
        "api_error" => $"api_error · {Attr(log, "model") ?? "?"} · {Attr(log, "status_code") ?? "no status"}",
        "tool_result" => $"{Attr(log, "tool_name") ?? "tool"} · {AttrDouble(log, "duration_ms") ?? 0:F0} ms" +
                         (string.Equals(Attr(log, "success"), "false", StringComparison.OrdinalIgnoreCase) ? " · ERROR" : ""),
        "tool_decision" => $"{Attr(log, "tool_name") ?? "tool"} · {Attr(log, "decision") ?? "?"}" +
                           $" ({Attr(log, "source") ?? "?"})",
        "user_prompt" => $"user_prompt · {AttrLong(log, "prompt_length") ?? 0} chars",
        "assistant_response" => $"assistant_response · {AttrLong(log, "response_length") ?? 0} chars",
        _ => null
    };

    // ------------------------------------------------------------------ beta traces

    /// <summary>
    /// Takes from a Claude Code beta trace span the one thing its log events do not carry.
    /// <c>claude_code.llm_request</c> mirrors the <c>claude_code.api_request</c> event call for
    /// call, so the events stay authoritative for counts and tokens and only time-to-first-token
    /// — which no event reports — is read off the span. Must be called under the session lock.
    /// </summary>
    public static void ApplySpanExtras(CopilotSession s, OtlpSpan span, TurnStat? turn)
    {
        if (span.Name != "claude_code.llm_request") return;
        if (span.AttrLong("ttft_ms") is not { } ttft || ttft <= 0) return;

        SessionStore.AddBounded(s.TtftMs, ttft);
        if (turn is null) return;
        turn.TtftTotalMs += ttft;
        turn.TtftCount++;
    }

    // ---------------------------------------------------------------------- helpers

    /// <summary>
    /// With beta tracing on, events carry the interaction span's trace id, so keying on it first
    /// keeps event-derived and span-derived turns in one numbering. Without tracing there are no
    /// spans and <c>prompt.id</c> — which correlates a prompt with every call it caused — is the
    /// only thing that groups a turn.
    /// </summary>
    private static TurnStat? TurnFor(CopilotSession s, OtlpLogEvent log)
    {
        var key = log.TraceId is { Length: > 0 } trace ? trace : Attr(log, "prompt.id");
        if (key is null) return null;

        var turn = s.TurnFor(key, log.Time);
        if (log.Time < turn.Start && log.Time > DateTimeOffset.UnixEpoch) turn.Start = log.Time;
        if (log.Time > turn.End) turn.End = log.Time;
        return turn;
    }

    private static void RecordChat(CopilotSession s, TurnStat? turn, string model,
        long input, long output, long cacheRead, long cacheCreation, double? durationMs, bool isError)
    {
        s.ChatCalls++;
        if (isError) s.ChatErrors++;
        s.InputTokens += input;
        s.OutputTokens += output;
        s.CacheReadTokens += cacheRead;
        s.CacheCreationTokens += cacheCreation;

        s.ModelCalls.AddOrUpdate(model, 1, (_, c) => c + 1);
        s.ModelUsage.AddOrUpdate(model,
            new ModelStat { Calls = 1, InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead },
            (_, e) => { e.Calls++; e.InputTokens += input; e.OutputTokens += output; e.CacheReadTokens += cacheRead; return e; });

        if (durationMs is { } duration && duration > 0) SessionStore.AddBounded(s.ChatDurationMs, duration);

        if (turn is null) return;
        turn.ChatCalls++;
        if (isError) turn.ChatErrors++;
        turn.InputTokens += input;
        turn.OutputTokens += output;
        turn.PrimaryModel ??= model;
    }

    private static void RecordTool(CopilotSession s, TurnStat? turn, string tool, bool isError, double durationMs)
    {
        s.ToolCalls++;
        if (isError) s.ToolErrors++;
        s.Tools.AddOrUpdate(tool,
            (1, isError ? 1 : 0, durationMs),
            (_, t) => (t.Calls + 1, t.Errors + (isError ? 1 : 0), t.TotalMs + durationMs));

        if (turn is null) return;
        turn.ToolCalls++;
        if (isError) turn.ToolErrors++;
    }

    private static string? Attr(OtlpLogEvent log, string key) =>
        log.Attributes.TryGetValue(key, out var v) && v.ToString() is { Length: > 0 } s ? s : null;

    // Numeric event attributes arrive typed from the protobuf exporter but as strings from
    // some JSON exporters, so string parsing is the last resort rather than the first.
    private static long? AttrLong(OtlpLogEvent log, string key) =>
        log.Attributes.TryGetValue(key, out var v)
            ? v.I ?? (long?)v.D ?? (long.TryParse(v.S, out var parsed) ? (long?)parsed : null)
            : null;

    private static double? AttrDouble(OtlpLogEvent log, string key) =>
        log.Attributes.TryGetValue(key, out var v)
            ? v.D ?? v.I ?? (double.TryParse(v.S, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? (double?)parsed
                : null)
            : null;
}
