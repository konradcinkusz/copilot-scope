using System.Text.Json;
using CopilotScope.Collector.Domain;

namespace CopilotScope.LogImporter;

/// <summary>
/// Turns one Claude Code JSONL transcript into a scored-ready <see cref="CopilotSession"/>.
///
/// <para><b>Why this exists.</b> Most developers never flip OTEL env vars — the grassroots
/// tools that parse these files (ccusage, agentsview, claude-view) built their whole user base
/// on that fact. Claude Code already writes a complete record of every session to
/// <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl</c>, so a developer can have a
/// scored history of work they have already done, with no configuration at all. What those
/// tools lack is exactly what this repo has: turn analysis, a composite score, a baseline.</para>
///
/// <para><b>What is reconstructed and what is not.</b> Token counts, models, tool calls and
/// their outcomes, turn boundaries and wall-clock timings are all in the file and are real.
/// Time-to-first-token, edit accept/reject decisions and thumbs feedback are <i>not</i> —
/// they are OTel events, and no amount of parsing invents them. They are therefore left empty
/// rather than defaulted, so the quality engine treats those components as priors carrying no
/// weight and the session's confidence honestly reflects the smaller evidence base. An
/// imported session that scored the same as a live one on a quarter of the signals would be
/// the whole feature quietly lying.</para>
/// </summary>
public sealed record TranscriptSession(CopilotSession Session, int Lines, int Skipped);

public static class ClaudeCodeTranscript
{
    /// <summary>
    /// Parses a transcript file. Malformed lines are counted and skipped rather than aborting:
    /// these files are appended to by a live process, so the last line of an in-progress
    /// session is routinely half-written, and refusing the whole session over it would make
    /// the importer useless on exactly the sessions someone most wants to see.
    /// </summary>
    public static TranscriptSession? Parse(IEnumerable<string> lines, string? repository = null,
        bool includeContent = false)
    {
        CopilotSession? session = null;
        var lineCount = 0;
        var skipped = 0;
        var anyMainLine = false;

        // One model response is written across several lines — one per content block — and
        // each line may repeat the response's `usage`. Keyed by the response's message id, this
        // is what has been counted for it so far, so a repeat adds nothing and a later, larger
        // figure adds only the difference.
        var counted = new Dictionary<string, CountedCall>(StringComparer.Ordinal);
        // A line folded in twice — from a copy of the file, or a subagent file repeating its
        // parent's lines — is counted once. Keyed by the line's own uuid.
        var seenLines = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? lastAt = null;

        // tool_use id → (name, when it was issued), so a tool_result can be paired back to its
        // call and the gap between them read as the tool's wall-clock duration. That timing is
        // real data in the file, not a fabrication, and it is what the tool panel needs.
        var pendingTools = new Dictionary<string, (string Name, DateTimeOffset At)>(StringComparer.Ordinal);
        DateTimeOffset? lastUserPromptAt = null;
        TurnStat? turn = null;
        var turnIndex = 0;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            lineCount++;

            JsonElement root;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); root = doc.RootElement; }
            catch (JsonException) { skipped++; continue; }

            using (doc)
            {
                if (root.ValueKind != JsonValueKind.Object) { skipped++; continue; }

                var type = Str(root, "type");
                // "summary" lines carry a title Claude generated for the session and no
                // measurable work; "system" lines are hook and command noise. Neither is a turn.
                if (type is not ("user" or "assistant")) continue;
                if (Str(root, "uuid") is { } uuid && !seenLines.Add(uuid)) continue;

                var sessionId = Str(root, "sessionId");
                if (string.IsNullOrEmpty(sessionId)) { skipped++; continue; }

                // A line without a readable timestamp is placed at the last one seen. It never
                // moves the session's own window: stamping it "now" made a months-old session
                // look active today, and put it in every "last 7 days" view.
                var stamp = Time(root, "timestamp");
                if (stamp is not null) lastAt = stamp;
                if (lastAt is not { } at) { skipped++; continue; }

                // A subagent's own conversation (the Task tool). Its model and tool calls are
                // real work and count; its prompts are the parent agent's instructions, not the
                // developer's, and do not start turns.
                var sidechain = root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True;
                if (!sidechain) anyMainLine = true;

                session ??= new CopilotSession
                {
                    Id = sessionId,
                    Origin = SessionOrigin.LogImport,
                    EmitterKind = EmitterKind.ClaudeCode,
                    AgentName = "claude-code",
                    FirstSeen = at,
                    LastSeen = at,
                    // The transcript's own session id is the one the OTel path would use too,
                    // which is what makes re-import idempotent instead of duplicating history.
                    VsCodeSessionId = sessionId,
                };

                session.Repository ??= repository;
                session.Branch ??= Str(root, "gitBranch");
                if (stamp is { } real)
                {
                    if (real < session.FirstSeen) session.FirstSeen = real;
                    if (real > session.LastSeen) session.LastSeen = real;
                }

                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                    continue;

                if (type == "user") ApplyUser(session, message, at, sidechain, ref lastUserPromptAt, ref turn,
                    ref turnIndex, pendingTools, includeContent);
                else ApplyAssistant(session, message, at, sidechain,
                    Str(message, "id") ?? Str(root, "requestId") ?? Str(root, "uuid"),
                    lastUserPromptAt, turn, pendingTools, counted, includeContent);
            }
        }

        // A file holding nothing but a subagent's conversation cannot be attributed to the
        // session that ran it, and reported as a session of its own it would be one with calls
        // and no turns — a developer session that never happened.
        if (session is null || !anyMainLine) return null;

        // Every prompt, including any beyond the turn list's cap.
        session.Turns = turnIndex;
        return new TranscriptSession(session, lineCount, skipped);
    }

    /// <summary>What has been counted for one model response so far.</summary>
    private sealed record CountedCall(string Model, TurnStat? Turn, long Input, long Output, long CacheRead,
        long CacheCreation);

    private static void ApplyUser(CopilotSession session, JsonElement message, DateTimeOffset at, bool sidechain,
        ref DateTimeOffset? lastUserPromptAt, ref TurnStat? turn, ref int turnIndex,
        Dictionary<string, (string Name, DateTimeOffset At)> pendingTools, bool includeContent)
    {
        if (!message.TryGetProperty("content", out var content)) return;

        // A user "message" is either a real prompt (a string, or text blocks) or the transport
        // for tool results. Only the former starts a turn — treating tool results as prompts
        // would report a session with four tool calls as five turns.
        if (content.ValueKind == JsonValueKind.Array &&
            content.EnumerateArray().Any(b => Str(b, "type") == "tool_result"))
        {
            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != "tool_result") continue;
                var id = Str(block, "tool_use_id");
                if (id is null || !pendingTools.Remove(id, out var pending)) continue;

                var failed = block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                var durationMs = Math.Max(0, (at - pending.At).TotalMilliseconds);

                // CopilotScope reading its own scores (the MCP server) is never scored — the
                // rule both OTel ingest paths follow (Domain/SelfObservation.cs). Reliability is
                // the error-free rate over ChatCalls * 2 + ToolCalls, so counting the read would
                // raise the score being read. Kept on the timeline, dropped from the counters.
                if (!SelfObservation.IsSelfObservation(pending.Name))
                {
                    session.ToolCalls++;
                    if (failed) session.ToolErrors++;
                    session.Tools.AddOrUpdate(pending.Name,
                        (1, failed ? 1 : 0, durationMs),
                        (_, t) => (t.Calls + 1, t.Errors + (failed ? 1 : 0), t.TotalMs + durationMs));
                    if (failed) session.ErrorTypes.AddOrUpdate("tool_error", 1, (_, c) => c + 1);
                    if (turn is not null)
                    {
                        turn.ToolCalls++;
                        if (failed) turn.ToolErrors++;
                        if (at > turn.End) turn.End = at;
                    }
                }
                session.AddEvent(new SessionEvent(at, "execute_tool",
                    $"{pending.Name} · {durationMs:F0} ms{(failed ? " · ERROR" : "")}"));
            }
            return;
        }

        var text = Text(content);
        if (sidechain)
        {
            // The parent agent briefing a subagent. Not a turn, and not the developer's words.
            session.AddEvent(new SessionEvent(at, "subagent_prompt", $"subagent_prompt · {text?.Length ?? 0} chars"));
            return;
        }
        lastUserPromptAt = at;

        // One user prompt starts one turn — the same rule the OTel path applies to Claude
        // Code's user_prompt event, so a turn means the same thing whichever way the data
        // arrived. Trace ids do not exist here, so the prompt's own index is the key. TurnFor
        // applies the session's cap on the turn list, as it does for live telemetry: a year of
        // prompts in one transcript would otherwise make one session larger than the store.
        turn = session.TurnFor($"import-turn-{turnIndex}", at);
        turnIndex++;

        session.AgentInvocations++;
        session.AddEvent(new SessionEvent(at, "user_prompt", $"user_prompt · {text?.Length ?? 0} chars"));

        if (includeContent && text is { Length: > 0 })
            session.AddTranscript(at, "user", text, null, turn.Index);
    }

    private static void ApplyAssistant(CopilotSession session, JsonElement message, DateTimeOffset at,
        bool sidechain, string? responseKey, DateTimeOffset? lastUserPromptAt, TurnStat? turn,
        Dictionary<string, (string Name, DateTimeOffset At)> pendingTools,
        Dictionary<string, CountedCall> counted, bool includeContent)
    {
        var model = Str(message, "model") ?? "unknown";

        long input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
        var hasUsage = message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;
        if (hasUsage)
        {
            input = Long(usage, "input_tokens");
            output = Long(usage, "output_tokens");
            cacheRead = Long(usage, "cache_read_input_tokens");
            cacheCreate = Long(usage, "cache_creation_input_tokens");
        }

        // Only messages carrying usage are model calls. Claude Code splits one response across
        // several assistant lines (text, then tool_use); counting each as a call would inflate
        // call counts and deflate tokens-per-call for every imported session.
        if (hasUsage && responseKey is not null && counted.TryGetValue(responseKey, out var earlier))
        {
            // Another line of a response already counted. Lines that repeat the same usage add
            // nothing; a line reporting more (a later point in the stream) adds the difference,
            // so the response ends up counted once, at the most complete figure it reported.
            var addInput = Math.Max(0, input - earlier.Input);
            var addOutput = Math.Max(0, output - earlier.Output);
            var addCacheRead = Math.Max(0, cacheRead - earlier.CacheRead);
            var addCacheCreation = Math.Max(0, cacheCreate - earlier.CacheCreation);

            session.InputTokens += addInput;
            session.OutputTokens += addOutput;
            session.CacheReadTokens += addCacheRead;
            session.CacheCreationTokens += addCacheCreation;
            if (session.ModelUsage.TryGetValue(earlier.Model, out var stat))
            {
                stat.InputTokens += addInput;
                stat.OutputTokens += addOutput;
                stat.CacheReadTokens += addCacheRead;
            }
            if (earlier.Turn is { } earlierTurn)
            {
                earlierTurn.InputTokens += addInput;
                earlierTurn.OutputTokens += addOutput;
                if (at > earlierTurn.End) earlierTurn.End = at;
            }

            counted[responseKey] = earlier with
            {
                Input = Math.Max(input, earlier.Input), Output = Math.Max(output, earlier.Output),
                CacheRead = Math.Max(cacheRead, earlier.CacheRead),
                CacheCreation = Math.Max(cacheCreate, earlier.CacheCreation)
            };
            hasUsage = false; // counted; what follows only records the line's content blocks
        }
        else if (hasUsage && responseKey is not null)
        {
            counted[responseKey] = new CountedCall(model, turn, input, output, cacheRead, cacheCreate);
        }

        if (hasUsage)
        {
            session.ChatCalls++;
            session.InputTokens += input;
            session.OutputTokens += output;
            session.CacheReadTokens += cacheRead;
            session.CacheCreationTokens += cacheCreate;
            session.ModelCalls.AddOrUpdate(model, 1, (_, c) => c + 1);
            session.ModelUsage.AddOrUpdate(model,
                new ModelStat { Calls = 1, InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead },
                (_, e) => { e.Calls++; e.InputTokens += input; e.OutputTokens += output; e.CacheReadTokens += cacheRead; return e; });

            if (turn is not null)
            {
                turn.ChatCalls++;
                turn.InputTokens += input;
                turn.OutputTokens += output;
                turn.PrimaryModel ??= model;
                if (at > turn.End) turn.End = at;
            }

            // Prompt→response wall clock. This is a real duration in the file and is what the
            // throughput analyzer reads. It is NOT time-to-first-token, which the transcript
            // does not record — TtftMs stays empty, so the latency component correctly reports
            // itself as having no data rather than being handed a number that means
            // something else.
            // Not for a subagent's calls: the developer's prompt did not wait on each of them.
            if (!sidechain && lastUserPromptAt is { } started && at > started)
                AddBounded(session.ChatDurationMs, (at - started).TotalMilliseconds);

            session.AddEvent(new SessionEvent(at, "chat",
                $"{model} · {input}→{output} tok" + (cacheRead > 0 ? $" · {cacheRead} cached" : "")));
        }

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            switch (Str(block, "type"))
            {
                case "tool_use":
                    // Recorded, not counted, until its result arrives: a tool call whose result
                    // never came back is an abandoned session, and counting it as a completed
                    // call would hide exactly that.
                    if (Str(block, "id") is { } id)
                        pendingTools[id] = (Str(block, "name") ?? "unknown", at);
                    break;

                // A subagent's replies are addressed to the agent that briefed it, not to the
                // developer, and would read as non sequiturs in the conversation.
                case "text" when includeContent && !sidechain:
                    if (Str(block, "text") is { Length: > 0 } responseText)
                        session.AddTranscript(at, model, null, responseText, turn?.Index ?? -1);
                    break;
            }
        }
    }

    /// <summary>Mirrors the collector's own distribution bound. A transcript can hold a year
    /// of a heavy user's work, and an unbounded list of every call's duration would make one
    /// imported session larger than the rest of the store.</summary>
    private static void AddBounded(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > 1000) list.RemoveRange(0, list.Count - 1000);
    }

    // ------------------------------------------------------------------- json helpers

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var l) ? l : 0;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    /// <summary>User content is a bare string in the common case and an array of blocks when
    /// the client attached anything; both spellings appear in the same file.</summary>
    private static string? Text(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var parts = content.EnumerateArray()
            .Where(b => Str(b, "type") == "text")
            .Select(b => Str(b, "text"))
            .Where(t => !string.IsNullOrEmpty(t));
        var joined = string.Join('\n', parts);
        return joined.Length > 0 ? joined : null;
    }
}
