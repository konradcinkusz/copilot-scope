using System.Text.Json.Nodes;

namespace CopilotScope.Mcp;

/// <summary>
/// The tools this server exposes, and what each one does.
///
/// Every tool is a read. There is no delete, no seed, no import and no way to write a
/// label from here: the collector's destructive endpoints need Admin scope and this
/// server never asks for it. That is a deliberate ceiling, not an oversight — an
/// assistant holding a credential that can wipe the team's session history is a worse
/// trade than any convenience it would buy.
///
/// The descriptions below are part of the product. An assistant reads them before it
/// decides which tool to call and how to phrase the answer, so the things a reader gets
/// wrong about a quality score — reading the composite without its confidence, comparing
/// two assistants that do not emit the same signals, treating the score as a verdict on
/// a person — are written where they will actually be read.
/// </summary>
public sealed class ToolCatalog(ICollectorReader collector)
{
    /// <summary>Above this many characters a tool result is cut with a note saying so.
    /// A captured transcript can be megabytes, and an assistant that spends its whole
    /// context window on one session cannot then answer a question about it.</summary>
    private const int MaxBody = 60_000;

    /// <summary>The tools/list payload.</summary>
    public static JsonArray Descriptors() => new(
        Tool("health",
            "Is the collector up, and is it persisting? Start here when sessions are missing: " +
            "this answers whether the collector is reachable at all, before anything about scores. " +
            "For the rest of the path — telemetry actually leaving the assistant — the user runs " +
            "`copilotscope doctor` in a terminal.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),

        Tool("list_sessions",
            "Recent scored sessions, newest first, with the composite quality score (0-100) and its " +
            "confidence. Confidence is not decoration: a 90 built on four samples means less than a 70 " +
            "built on forty, so quote the two together or neither. Optional filters narrow the window " +
            "and the cohort. On a deployment running privacy mode, a filter that narrows the result " +
            "below the k-anonymity floor returns a suppressed page rather than rows — that is the " +
            "control working, not an error to retry around.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["days"] = Prop("integer", "How many days back to look. Default 7."),
                    ["limit"] = Prop("integer", "Maximum sessions to return. Default 20."),
                    ["repository"] = Prop("string", "Only sessions on this repository."),
                    ["emitter"] = Prop("string",
                        "Only this assistant: VSCode, CLI (Copilot CLI), ClaudeCode or Cowork. Any other value " +
                        "is ignored, so the result covers every assistant."),
                    ["model"] = Prop("string", "Only sessions that used this model."),
                    ["grade"] = Prop("string", "Only sessions in this grade band.")
                }
            }),

        Tool("get_session",
            "Everything recorded for one session: score components, per-turn analysis (TFRA), tool " +
            "stats, errors, insights, the event timeline, and the transcript when content capture was " +
            "on. This is the tool that answers 'which turn went wrong' — the turn analysis names the " +
            "turn and the reason (LLM or tool errors, latency against this session's own median, a " +
            "repair loop), which is far more useful than the headline number.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = Prop("string", "Session id, as returned by list_sessions.")
                },
                ["required"] = new JsonArray("id")
            }),

        Tool("overview",
            "Cross-session summary for a window: token burn, per-model calls, daily usage, top " +
            "sessions. Usage totals, not quality — say which you are reporting.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["days"] = Prop("integer", "How many days back to summarize. Default 7.")
                }
            }),

        Tool("signal_coverage",
            "Which signals each assistant actually emits. Read this before comparing scores across " +
            "assistants: a Claude Code session has no thumbs and no edit-survival signal, so its 80 " +
            "rests on less evidence than a VS Code session's 80. Scores are comparable within an " +
            "assistant and only directional across them.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }));

    /// <summary>Run one tool. Returns the text to hand back and whether it is an error result.</summary>
    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonObject? args, CancellationToken ct)
    {
        string path;
        switch (name)
        {
            case "health":
                path = "api/health";
                break;

            case "signal_coverage":
                path = "api/coverage";
                break;

            case "overview":
                path = "api/overview" + Query(("days", (Int(args, "days") ?? 7).ToString()));
                break;

            case "list_sessions":
                path = "api/sessions" + Query(
                    ("days", (Int(args, "days") ?? 7).ToString()),
                    ("limit", (Int(args, "limit") ?? 20).ToString()),
                    ("repository", Str(args, "repository")),
                    ("emitter", Str(args, "emitter")),
                    ("model", Str(args, "model")),
                    ("grade", Str(args, "grade")));
                break;

            case "get_session":
                if (Str(args, "id") is not { } id)
                    return ("get_session needs an \"id\". Run list_sessions first to find one.", true);
                path = "api/sessions/" + Uri.EscapeDataString(id);
                break;

            default:
                return ($"Unknown tool \"{name}\".", true);
        }

        var response = await collector.GetAsync(path, ct);

        if (response.Status == 0)
            return ($"Could not reach the collector at {collector.Endpoint} ({response.Body}). " +
                    "If the stack is not running, `copilotscope up` starts it and `copilotscope doctor` " +
                    "says which link is broken.", true);

        // 401 and 403 mean different things here and must not be collapsed. The collector
        // answers 401 for a missing or wrong credential, and 403 only when privacy mode is
        // refusing the view on purpose — telling someone to go find an API key for that
        // would be advice to work around a control their organisation chose.
        if (response.Status == 401)
            return ("The collector refused the read (401). This deployment has API keys configured, so " +
                    "the MCP server needs COPILOTSCOPE_API_KEY set to a key with Read scope. Read scope " +
                    "also reaches captured transcripts, so on a shared deployment that is a decision " +
                    "for whoever runs it.", true);

        if (response.Status == 403)
            return ("Privacy mode is withholding this view, which is the aggregation floor doing its " +
                    $"job rather than a fault to retry around. The collector said: {Truncate(response.Body, 600)}",
                    true);

        if (response.Status == 404)
            return ("The collector has no such session. It may have been deleted, or fallen outside " +
                    "the retention window.", true);

        if (!response.Ok)
            return ($"The collector answered {response.Status}: {Truncate(response.Body, 400)}", true);

        return (Truncate(response.Body, MaxBody), false);
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description
    };

    /// <summary>Builds a query string, dropping the parameters the caller left out.</summary>
    private static string Query(params (string Key, string? Value)[] parts)
    {
        var pairs = parts
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}")
            .ToArray();

        return pairs.Length == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }

    private static string? Str(JsonObject? args, string key) =>
        args is not null
        && args.TryGetPropertyValue(key, out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && text.Length > 0
            ? text
            : null;

    /// <summary>Reads an integer argument. Clients are not consistent about whether a
    /// number arrives as a JSON number or a string, so both are accepted.</summary>
    private static int? Int(JsonObject? args, string key)
    {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
            return null;

        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;

        return null;
    }

    private static string Truncate(string body, int max) =>
        body.Length <= max
            ? body
            : body[..max] + $"\n\n[truncated at {max} characters of {body.Length}]";
}
