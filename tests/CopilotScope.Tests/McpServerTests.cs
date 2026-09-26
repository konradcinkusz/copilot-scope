using System.Text;
using System.Text.Json.Nodes;
using CopilotScope.Mcp;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The MCP server's protocol surface, exercised without a collector behind it.
///
/// Two classes of thing are pinned here. The protocol mechanics — negotiation, framing,
/// notifications getting no reply, malformed input getting a JSON-RPC error rather than a
/// crashed transport — because an MCP client's only recourse for a broken server is to
/// drop it silently. And the read-only ceiling, because "this server cannot delete your
/// history" is a claim the documentation makes and a test should keep true.
/// </summary>
public class McpServerTests
{
    private sealed class FakeCollector(CollectorResponse response) : ICollectorReader
    {
        public List<string> Requests { get; } = new();
        public string Endpoint => "http://127.0.0.1:4318/";

        public Task<CollectorResponse> GetAsync(string path, CancellationToken ct)
        {
            Requests.Add(path);
            return Task.FromResult(response);
        }
    }

    private static (McpServer Server, FakeCollector Collector) Build(CollectorResponse? response = null)
    {
        var collector = new FakeCollector(response ?? new CollectorResponse(200, true, """{"ok":true}"""));
        return (new McpServer(new ToolCatalog(collector)), collector);
    }

    private static async Task<JsonNode> AskAsync(McpServer server, string request)
    {
        var raw = await server.HandleLineAsync(request, CancellationToken.None);
        Assert.NotNull(raw);
        return JsonNode.Parse(raw!)!;
    }

    [Fact]
    public async Task InitializeAnswersInTheClientsProtocolRevisionWhenItIsOneWeSpeak()
    {
        var (server, _) = Build();

        var response = await AskAsync(server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");

        var result = response["result"]!;
        Assert.Equal("2025-03-26", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("copilotscope", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task InitializeFallsBackToItsOwnRevisionForAnUnknownOne()
    {
        var (server, _) = Build();

        var response = await AskAsync(server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        Assert.Equal("2025-06-18", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task NotificationsGetNoReply()
    {
        var (server, _) = Build();

        Assert.Null(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            CancellationToken.None));
    }

    [Fact]
    public async Task EveryExposedToolIsAReadAndCarriesASchema()
    {
        var (server, _) = Build();

        var response = await AskAsync(server, """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""");

        var tools = response["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

        Assert.Equal(
            new[] { "health", "list_sessions", "get_session", "overview", "signal_coverage" },
            names);

        // The ceiling the README and docs/MCP.md promise: nothing here can change anything.
        Assert.DoesNotContain(names, n =>
            n.Contains("delete", StringComparison.Ordinal) ||
            n.Contains("seed", StringComparison.Ordinal) ||
            n.Contains("import", StringComparison.Ordinal) ||
            n.Contains("label", StringComparison.Ordinal) ||
            n.Contains("judge", StringComparison.Ordinal));

        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t!["description"]!.GetValue<string>()));
            Assert.Equal("object", t["inputSchema"]!["type"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task TheEmitterFilterNamesOnlyValuesTheCollectorUnderstands()
    {
        // The collector drops an emitter it cannot parse (CohortFilter.From), so a documented
        // value that is not an EmitterKind widens the result to every assistant without a word.
        // "CopilotCli" was documented here once.
        var (server, _) = Build();

        var response = await AskAsync(server, """{"jsonrpc":"2.0","id":8,"method":"tools/list"}""");

        var listSessions = response["result"]!["tools"]!.AsArray()
            .Single(t => t!["name"]!.GetValue<string>() == "list_sessions")!;
        var description = listSessions["inputSchema"]!["properties"]!["emitter"]!["description"]!.GetValue<string>();

        foreach (var value in new[] { "VSCode", "CLI", "ClaudeCode", "Cowork" })
        {
            Assert.Contains(value, description, StringComparison.Ordinal);
            Assert.NotNull(CopilotScope.Collector.Api.CohortFilter.From(null, value, null, null, null).Emitter);
        }
        Assert.DoesNotContain("CopilotCli", description, StringComparison.Ordinal);
        Assert.Null(CopilotScope.Collector.Api.CohortFilter.From(null, "CopilotCli", null, null, null).Emitter);
    }

    [Fact]
    public async Task ListSessionsSendsTheDefaultWindowAndEscapesFilters()
    {
        var (server, collector) = Build();

        await AskAsync(server,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_sessions","arguments":{"repository":"acme/web"}}}""");

        Assert.Equal("api/sessions?days=7&limit=20&repository=acme%2Fweb", Assert.Single(collector.Requests));
    }

    [Fact]
    public async Task ToolResultsPassTheCollectorsJsonThroughUnchanged()
    {
        var (server, _) = Build(new CollectorResponse(200, true, """{"sessions":[],"total":0}"""));

        var response = await AskAsync(server,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_sessions","arguments":{}}}""");

        var result = response["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal("""{"sessions":[],"total":0}""", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetSessionEscapesTheIdAndRefusesToGuessOne()
    {
        var (server, collector) = Build();

        await AskAsync(server,
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_session","arguments":{"id":"conv/1 2"}}}""");
        Assert.Equal("api/sessions/conv%2F1%202", Assert.Single(collector.Requests));

        var missing = await AskAsync(server,
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_session","arguments":{}}}""");
        Assert.True(missing["result"]!["isError"]!.GetValue<bool>());
        Assert.Single(collector.Requests);   // nothing was sent
    }

    [Fact]
    public async Task AnUnreachableCollectorIsAnErrorResultThatSaysWhatToRun()
    {
        var (server, _) = Build(new CollectorResponse(0, false, "connection refused"));

        var response = await AskAsync(server,
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"health","arguments":{}}}""");

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("copilotscope doctor", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedReadExplainsTheScopeItNeeds()
    {
        var (server, _) = Build(new CollectorResponse(401, false, ""));

        var response = await AskAsync(server,
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"overview","arguments":{}}}""");

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("COPILOTSCOPE_API_KEY", text, StringComparison.Ordinal);
        Assert.Contains("Read scope", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivacyModeRefusalIsNotReportedAsAMissingCredential()
    {
        // The collector answers 403 only when privacy mode withholds a view on purpose.
        // Advising an API key there would be advising someone around their own control.
        var (server, _) = Build(new CollectorResponse(403, false,
            """{"error":"Per-session detail is disabled under privacy mode."}"""));

        var response = await AskAsync(server,
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"get_session","arguments":{"id":"s1"}}}""");

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.DoesNotContain("COPILOTSCOPE_API_KEY", text, StringComparison.Ordinal);
        Assert.Contains("privacy mode", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnknownToolIsAnErrorResultRatherThanADeadTransport()
    {
        var (server, collector) = Build();

        var response = await AskAsync(server,
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"delete_everything","arguments":{}}}""");

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Empty(collector.Requests);
    }

    [Theory]
    [InlineData("not json at all", -32700)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""", -32601)]
    public async Task MalformedOrUnsupportedRequestsGetAJsonRpcError(string request, int expectedCode)
    {
        var (server, _) = Build();

        var response = await AskAsync(server, request);

        Assert.Equal(expectedCode, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsyncWritesExactlyOneLinePerRequest()
    {
        // Framing is the part a client cannot recover from: one JSON message per line,
        // nothing on stdout that is not a response, and no reply at all to a notification.
        var (server, _) = Build();
        var requests = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
        };

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", requests) + "\n"));
        using var output = new MemoryStream();

        await server.RunAsync(input, output, CancellationToken.None);

        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonNode.Parse(lines[0])!["id"]!.GetValue<int>());
        Assert.Equal(2, JsonNode.Parse(lines[1])!["id"]!.GetValue<int>());
    }
}
