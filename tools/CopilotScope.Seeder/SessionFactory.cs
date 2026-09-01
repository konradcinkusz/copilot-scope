using CopilotScope.Collector.Domain;

namespace CopilotScope.Seeder;

/// <summary>
/// Builds a fully-populated <see cref="CopilotSession"/> aggregate for one persona: tokens,
/// TTFT/duration samples, per-model usage, per-tool stats, error types, edits/feedback,
/// survival scores, a per-turn breakdown and a captured transcript — every field the
/// dashboard and quality/insight engines read, not just the counters.
/// </summary>
public static class SessionFactory
{
    public static CopilotSession Build(string id, Persona persona, DateTimeOffset start, Random rng)
    {
        var session = new CopilotSession
        {
            Id = id,
            AgentName = "copilot",
            Repository = Fixtures.Repositories[rng.Next(Fixtures.Repositories.Length)],
            Branch = Fixtures.Branches[rng.Next(Fixtures.Branches.Length)],
            // The showcase session is pinned to VS Code so every editor-only signal (edits,
            // thumbs, LOC) is present and its tiles render; other personas rotate emitters.
            EmitterKind = persona.Showcase ? EmitterKind.VSCode : (EmitterKind)rng.Next(1, 6), // VSCode..Cowork — never Unknown
            FirstSeen = start,
        };

        var turnCount = rng.Next(persona.MinTurns, persona.MaxTurns + 1);
        var isInternal = persona.InternalPromptPrefix is not null;
        var clock = start;
        var lastActivity = start;

        // The showcase session carries a heavier, guaranteed-mixed edit load so its
        // acceptance/throughput tiles are populated and never all-or-nothing.
        var totalEdits = isInternal ? 0 : persona.Showcase ? rng.Next(20, 40) : rng.Next(2, 11);
        var editsAccepted = (int)Math.Round(totalEdits * persona.EditAcceptRatio);
        if (persona.Showcase) editsAccepted = Math.Clamp(editsAccepted, 1, totalEdits - 1); // keep both ✓ and ✗ non-zero

        for (var t = 0; t < turnCount; t++)
        {
            var traceId = $"{id}-t{t}";
            var turn = session.TurnFor(traceId, clock);
            var model = Fixtures.Models[rng.Next(Fixtures.Models.Length)];

            // For the showcase persona, each turn adopts a "mood" that overrides latency and
            // error rates so one long chat contains clean, stalled, error-storm and repair-loop
            // turns. Turns 2–4 are pinned to the three interesting moods so those findings always
            // appear; the rest are random for texture.
            var mood = persona.Showcase
                ? t switch
                {
                    2 => Fixtures.ShowcaseMoods[2], // stall (abandonment risk)
                    3 => Fixtures.ShowcaseMoods[3], // error storm
                    4 => Fixtures.ShowcaseMoods[4], // repair loop
                    _ => Fixtures.ShowcaseMoods[rng.Next(Fixtures.ShowcaseMoods.Length)]
                }
                : null;

            var minTtft = mood?.MinTtftMs ?? persona.MinTtftMs;
            var maxTtft = mood?.MaxTtftMs ?? persona.MaxTtftMs;
            var chatErrorRate = mood?.ChatErrorRate ?? persona.ChatErrorRate;
            var toolErrorRate = mood?.ToolErrorRate ?? persona.ToolErrorRate;

            var input = rng.NextInt64(persona.MinInputTokens, persona.MaxInputTokens);
            var output = rng.NextInt64(persona.MinOutputTokens, persona.MaxOutputTokens);
            var cacheRead = (long)(input * rng.NextDouble() * 0.6);
            var ttft = minTtft + rng.NextDouble() * (maxTtft - minTtft);
            // Turn 3 of the showcase is the pinned error-storm turn — force a chat error so the
            // error-type breakdown panel is always populated on the headline demo session.
            var isChatError = rng.NextDouble() < chatErrorRate || (persona.Showcase && t == 3);
            var chatDurationMs = ttft + 200 + rng.Next(1500);

            session.ChatCalls++;
            turn.ChatCalls++;
            if (isChatError)
            {
                session.ChatErrors++;
                turn.ChatErrors++;
                var errorType = Fixtures.ErrorTypes[rng.Next(Fixtures.ErrorTypes.Length)];
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
                (_, e) =>
                {
                    e.Calls++;
                    e.InputTokens += input;
                    e.OutputTokens += output;
                    e.CacheReadTokens += cacheRead;
                    return e;
                });

            session.TtftMs.Add(ttft);
            session.ChatDurationMs.Add(chatDurationMs);
            turn.TtftTotalMs += ttft;
            turn.TtftCount++;
            turn.PrimaryModel ??= model;

            var (prompt, response) = PickTurnText(persona, t, turnCount, rng);
            session.AddTranscript(clock, model, prompt, response, t);
            session.AddEvent(new SessionEvent(clock, "chat",
                $"chat {model} · {input}→{output} tok · {chatDurationMs:F0} ms{(isChatError ? " · ERROR" : "")}"));

            var toolCount = isInternal ? 0
                : mood is not null ? rng.Next(mood.MinTools, mood.MaxTools + 1)
                : rng.Next(0, 4);
            var toolClock = clock.AddSeconds(1 + rng.Next(3));
            for (var k = 0; k < toolCount; k++)
            {
                var tool = Fixtures.Tools[rng.Next(Fixtures.Tools.Length)];
                // Turn 4 of the showcase is the pinned repair-loop turn — force its first tool to
                // fail so the repair-loop signature and tool-error markers reliably appear.
                var isToolError = rng.NextDouble() < toolErrorRate || (persona.Showcase && t == 4 && k == 0);
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

            clock = turnEnd.AddSeconds(30 + rng.Next(150));
        }

        session.Turns = turnCount;

        if (!isInternal)
        {
            session.EditsAccepted = editsAccepted;
            session.EditsRejected = totalEdits - editsAccepted;
            session.LinesAdded = persona.Showcase ? rng.Next(80, 340) : rng.Next(10, 200);
            session.LinesRemoved = persona.Showcase
                ? rng.Next(15, (int)session.LinesAdded / 2 + 2)  // always some churn to show
                : rng.Next(0, (int)session.LinesAdded / 2 + 1);

            if (persona.Showcase)
            {
                // Guarantee both 👍 and 👎 so the feedback tile shows a mixed signal.
                session.ThumbsUp = rng.Next(3, 8);
                session.ThumbsDown = rng.Next(1, 4);
            }
            else
            {
                var totalFeedback = rng.Next(0, 3);
                var up = (int)Math.Round(totalFeedback * persona.ThumbsUpRatio);
                session.ThumbsUp = up;
                session.ThumbsDown = totalFeedback - up;
            }

            for (var s = 0; s < turnCount; s++)
            {
                var noRevert = Lerp(persona.MinSurvivalNoRevert, persona.MaxSurvivalNoRevert, rng.NextDouble());
                var fourGram = Lerp(persona.MinSurvivalFourGram, persona.MaxSurvivalFourGram, rng.NextDouble());
                session.SurvivalNoRevert.Add(noRevert);
                session.SurvivalFourGram.Add(fourGram);
                session.SurvivalScores.Add(0.4 * fourGram + 0.6 * noRevert);
            }
        }

        session.LastSeen = lastActivity;
        return session;
    }

    private static (string? Prompt, string? Response) PickTurnText(Persona persona, int turnIndex, int turnCount, Random rng)
    {
        if (persona.InternalPromptPrefix is not null)
        {
            var tail = Fixtures.TitlePromptTails[rng.Next(Fixtures.TitlePromptTails.Length)];
            return (persona.InternalPromptPrefix + tail, "Session summary.");
        }

        // The last two turns of a "repair-loop" persona carry recognizable rephrasing +
        // strong-marker language so WorkflowFrictionAnalyzer actually flags them. Index by
        // position *within the final pair*, not by absolute turn number: the old
        // Math.Min(turnIndex, …) form landed on the mild corrective pair for short
        // sessions, which the analyzer only scores as "mild friction" — the persona
        // then produced no strong-marker or rephrasing signal at all.
        // turnCount is stable per session but varies between them, so the dataset
        // still shows both flavours.
        if (persona.RepairHeavy && turnIndex >= turnCount - 2)
        {
            var pairStart = turnCount % 2 == 0 ? 0 : 2;   // 0–1 strong markers, 2–3 rephrasing
            var pick = Fixtures.RepairTurns[pairStart + (turnIndex - (turnCount - 2))];
            return (pick.Prompt, pick.Response);
        }

        if (persona.Showcase)
            return PickShowcaseText(turnIndex, turnCount, rng);

        var t = Fixtures.Turns[rng.Next(Fixtures.Turns.Length)];
        return (t.Prompt, t.Response);
    }

    /// <summary>Text plan for the showcase session: two scattered repair blocks (a
    /// strong-marker pair, then a rephrasing pair so the WorkflowFrictionAnalyzer lights up on
    /// both signals), periodic multi-role captured content so the chat view shows
    /// system/tool bubbles, and ordinary engineering turns everywhere else.</summary>
    private static (string? Prompt, string? Response) PickShowcaseText(int turnIndex, int turnCount, Random rng)
    {
        var strongAt = turnCount / 4;        // e.g. turn ~8 of 32
        var rephraseAt = turnCount * 2 / 3;  // e.g. turn ~21 of 32

        if (turnIndex == strongAt)       return Fixtures.RepairTurns[0]; // "still doesn't work"
        if (turnIndex == strongAt + 1)   return Fixtures.RepairTurns[1]; // "Wrong again!!"
        if (turnIndex == rephraseAt)     return Fixtures.RepairTurns[2]; // "Please add a retry policy…"
        if (turnIndex == rephraseAt + 1) return Fixtures.RepairTurns[3]; // near-identical rephrasing

        if (turnIndex % 5 == 2)
            return Fixtures.RichTurns[(turnIndex / 5) % Fixtures.RichTurns.Length];

        var t = Fixtures.Turns[rng.Next(Fixtures.Turns.Length)];
        return (t.Prompt, t.Response);
    }

    private static double Lerp(double min, double max, double t) => min + (max - min) * t;
}
