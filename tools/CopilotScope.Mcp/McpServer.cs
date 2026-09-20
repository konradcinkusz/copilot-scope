using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotScope.Mcp;

/// <summary>
/// Model Context Protocol over stdio: one JSON-RPC 2.0 message per line, requests in on
/// stdin, responses out on stdout.
///
/// Hand-rolled rather than taken from a package, for the same reason the collector decodes
/// OTLP protobuf in-repo: the protocol surface actually used here is four methods, and a
/// dependency that has to be tracked, audited and kept current costs more than the code it
/// would save. Everything below is shared-framework only.
///
/// Nothing is written to stdout except protocol messages — stdout *is* the transport, so a
/// stray Console.WriteLine corrupts the stream. Diagnostics go to stderr.
/// </summary>
public sealed class McpServer(ToolCatalog tools)
{
    public const string ServerName = "copilotscope";

    /// <summary>Protocol revisions this server understands, newest first.</summary>
    private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;

    private static string Version =>
        typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Reads requests until the client closes stdin.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(input);
        // NewLine is pinned to "\n": the framing is one JSON message per line, and a
        // platform-dependent line ending is not something a transport should carry.
        await using var writer = new StreamWriter(output) { AutoFlush = true, NewLine = "\n" };

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null) return;               // stdin closed: the client is gone
            if (line.Length == 0) continue;

            var response = await HandleLineAsync(line, ct);
            if (response is not null) await writer.WriteLineAsync(response);
        }
    }

    /// <summary>Handles one incoming line. Returns the response, or null for a notification.</summary>
    public async Task<string?> HandleLineAsync(string line, CancellationToken ct)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, ParseError, "Parse error");
        }

        if (parsed is not JsonObject request) return Error(null, InvalidRequest, "Invalid Request");

        // A JSON-RPC notification carries no id and gets no reply — not even an error
        // one. MCP sends notifications/initialized once the handshake is done, and
        // notifications/cancelled when the client abandons a call; answering either is a
        // protocol violation, so the id is established before anything can fail.
        var isNotification = !request.ContainsKey("id");
        JsonNode? id = isNotification ? null : request["id"]?.DeepClone();

        var method = StringOf(request["method"]);
        if (method is null)
            return isNotification ? null : Error(id, InvalidRequest, "Invalid Request: no method");
        if (isNotification) return null;

        return method switch
        {
            "initialize" => Initialize(id, request),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = ToolCatalog.Descriptors() }),
            "tools/call" => await CallToolAsync(id, request, ct),
            _ => Error(id, MethodNotFound, $"Method not found: {method}")
        };
    }

    private static string Initialize(JsonNode? id, JsonObject request)
    {
        // Answer in the client's revision when it is one we speak, otherwise in ours and
        // let the client decide whether it can live with that — which is what the spec asks
        // for, and is why this is negotiated rather than hardcoded.
        var requested = StringOf((request["params"] as JsonObject)?["protocolVersion"]);
        var negotiated = requested is not null && Array.IndexOf(SupportedProtocols, requested) >= 0
            ? requested
            : SupportedProtocols[0];

        return Result(id, new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = Version },
            ["instructions"] =
                "CopilotScope scores the quality of AI coding-assistant sessions. Every tool here is " +
                "read-only. Three things to hold on to when reporting what they return: a composite " +
                "score is meaningless without the confidence next to it; the score grades a session, " +
                "never a person, and must not be used to rank or compare developers; and acceptance " +
                "rate is not a target — pushed on, it rewards accepting bad suggestions, which is why " +
                "edit survival sits beside it. When asked why a session scored what it did, read the " +
                "turn analysis from get_session rather than restating the number."
        });
    }

    private async Task<string> CallToolAsync(JsonNode? id, JsonObject request, CancellationToken ct)
    {
        var parameters = request["params"] as JsonObject;
        var name = StringOf(parameters?["name"]);
        if (name is null) return Error(id, InvalidParams, "tools/call requires params.name");

        var arguments = parameters?["arguments"] as JsonObject;

        string text;
        bool isError;
        try
        {
            (text, isError) = await tools.CallAsync(name, arguments, ct);
        }
        catch (Exception ex)
        {
            // A failing tool is a failed tool call, not a failed session. The assistant can
            // say what went wrong and carry on; killing the transport would end the turn.
            text = $"The tool failed: {ex.Message}";
            isError = true;
        }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        });
    }

    private static string Result(JsonNode? id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }.ToJsonString();

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
