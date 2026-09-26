using CopilotScope.Collector.Domain;

namespace CopilotScope.Seeder;

/// <summary>One authored exchange in a curated conversation. Only <see cref="Prompt"/> and
/// <see cref="Response"/> are required; the rest add texture (a model switch, an LLM error,
/// tool calls, a deliberately slow response) where the story calls for it. Everything left
/// unset is derived plausibly by <see cref="ScriptedSessionFactory"/>.</summary>
public sealed record ScriptedTurn(
    string Prompt,
    string Response,
    string? Model = null,
    bool ChatError = false,
    string? ErrorType = null,
    string[]? Tools = null,
    int[]? ToolErrorIndexes = null,
    double? TtftMs = null);

/// <summary>A hand-authored, coherent multi-turn session — a real developer working a single
/// problem end to end — as opposed to the persona generator's statistically-shaped but
/// content-random sessions. These are what the dashboard's transcript view is for.</summary>
public sealed record ScriptedSession(
    string Slug,
    string Repository,
    string Branch,
    EmitterKind Emitter,
    string DefaultModel,
    int EditsAccepted,
    int EditsRejected,
    int ThumbsUp,
    int ThumbsDown,
    int LinesAdded,
    int LinesRemoved,
    ScriptedTurn[] Turns);

/// <summary>
/// Builds a fully-populated <see cref="CopilotSession"/> from a <see cref="ScriptedSession"/>.
/// The authored prompts/responses drive the transcript verbatim; the numeric telemetry
/// (tokens that grow as context accumulates, a prompt cache that warms up over the
/// conversation, TTFT samples, tool timings, think-time gaps between turns) is synthesized to
/// look like a real long session rather than a benchmark.
/// </summary>
public static class ScriptedSessionFactory
{
    public static CopilotSession Build(string id, ScriptedSession script, DateTimeOffset start, Random rng)
    {
        var isEditor = script.Emitter == EmitterKind.VSCode; // only editor surfaces emit edit/LOC/survival signals
        var session = new CopilotSession
        {
            Id = id,
            AgentName = "copilot",
            Repository = script.Repository,
            Branch = script.Branch,
            EmitterKind = script.Emitter,
            FirstSeen = start,
        };
        session.AddAgentName(session.AgentName);

        var turnCount = script.Turns.Length;
        var clock = start;
        var lastActivity = start;
        var baseInput = 1300 + rng.Next(0, 700);
        string currentModel = script.DefaultModel;

        for (var t = 0; t < turnCount; t++)
        {
            var scripted = script.Turns[t];
            var traceId = $"{id}-t{t}";
            var turn = session.TurnFor(traceId, clock);
            currentModel = scripted.Model ?? currentModel;
            var model = currentModel;

            // Context accumulates: prompt tokens grow turn over turn (files re-sent, history
            // replayed), and the prompt cache warms up so a growing share is served from cache.
            long input = Math.Min(46_000, baseInput + t * (650 + rng.Next(0, 550)) + rng.Next(0, 500));
            var cacheFrac = t == 0 ? 0.04 : Math.Min(0.85, 0.32 + t * 0.028 + rng.NextDouble() * 0.10);
            long cacheRead = (long)(input * cacheFrac);
            long output = Math.Clamp(scripted.Response.Length / 3 + rng.Next(40, 180), 120, 950);

            var ttft = scripted.TtftMs ?? 420 + rng.NextDouble() * 950;
            var chatDurationMs = ttft + 250 + rng.Next(1200);
            var isChatError = scripted.ChatError;

            session.ChatCalls++;
            turn.ChatCalls++;
            if (isChatError)
            {
                session.ChatErrors++;
                turn.ChatErrors++;
                var errorType = scripted.ErrorType ?? Fixtures.ErrorTypes[rng.Next(Fixtures.ErrorTypes.Length)];
                session.ErrorTypes.AddOrUpdate(errorType, 1, (_, c) => c + 1);
            }

            session.InputTokens += input;
            session.OutputTokens += output;
            session.CacheReadTokens += cacheRead;
            turn.InputTokens += input;
            turn.OutputTokens += output;

            session.ModelCalls.AddOrUpdate(model, 1, (_, c) => c + 1);
            session.ModelUsage.AddOrUpdate(model,
                new ModelStat { Calls = 1, InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead },
                (_, e) => { e.Calls++; e.InputTokens += input; e.OutputTokens += output; e.CacheReadTokens += cacheRead; return e; });

            session.TtftMs.Add(ttft);
            session.ChatDurationMs.Add(chatDurationMs);
            turn.TtftTotalMs += ttft;
            turn.TtftCount++;
            turn.PrimaryModel ??= model;

            session.AddTranscript(clock, model, scripted.Prompt, scripted.Response, t);
            session.AddEvent(new SessionEvent(clock, "chat",
                $"chat {model} · {input}→{output} tok · {chatDurationMs:F0} ms{(isChatError ? " · ERROR" : "")}"));

            var tools = scripted.Tools ?? [];
            var toolErrors = scripted.ToolErrorIndexes ?? [];
            var toolClock = clock.AddSeconds(1 + rng.Next(3));
            for (var k = 0; k < tools.Length; k++)
            {
                var tool = tools[k];
                var isToolError = toolErrors.Contains(k);
                var toolMs = 40 + rng.Next(1600);

                session.ToolCalls++;
                turn.ToolCalls++;
                if (isToolError) { session.ToolErrors++; turn.ToolErrors++; }
                session.Tools.AddOrUpdate(tool,
                    (1, isToolError ? 1 : 0, (double)toolMs),
                    (_, e) => (e.Calls + 1, e.Errors + (isToolError ? 1 : 0), e.TotalMs + toolMs));

                session.AddEvent(new SessionEvent(toolClock, "execute_tool",
                    $"{tool} · {toolMs} ms{(isToolError ? " · ERROR" : "")}"));
                toolClock = toolClock.AddSeconds(1 + rng.Next(2));
            }

            var turnEnd = toolClock.AddSeconds(1 + rng.Next(3));
            turn.End = turnEnd;
            lastActivity = turnEnd;
            session.AgentInvocations++;
            session.AddEvent(new SessionEvent(turnEnd, "invoke_agent", $"invoke_agent copilot · turn {t + 1}"));

            // Real think-time between turns: usually seconds-to-a-minute, occasionally a longer
            // pause (reading, running the app, a coffee break).
            var gapSeconds = rng.Next(20, 160) + (rng.NextDouble() < 0.15 ? rng.Next(180, 900) : 0);
            clock = turnEnd.AddSeconds(gapSeconds);
        }

        session.Turns = turnCount;

        if (isEditor)
        {
            session.EditsAccepted = script.EditsAccepted;
            session.EditsRejected = script.EditsRejected;
            session.ThumbsUp = script.ThumbsUp;
            session.ThumbsDown = script.ThumbsDown;
            session.LinesAdded = script.LinesAdded;
            session.LinesRemoved = script.LinesRemoved;

            // One survival sample per turn, mostly durable with a few rewrites — the shape a real
            // productive session leaves behind.
            for (var s = 0; s < turnCount; s++)
            {
                var rewrite = rng.NextDouble() < 0.2;
                var noRevert = rewrite ? 0.25 + rng.NextDouble() * 0.3 : 0.7 + rng.NextDouble() * 0.28;
                var fourGram = rewrite ? 0.15 + rng.NextDouble() * 0.3 : 0.5 + rng.NextDouble() * 0.35;
                session.SurvivalNoRevert.Add(noRevert);
                session.SurvivalFourGram.Add(fourGram);
                session.SurvivalScores.Add(0.4 * fourGram + 0.6 * noRevert);
            }
        }

        session.LastSeen = lastActivity;
        return session;
    }
}
