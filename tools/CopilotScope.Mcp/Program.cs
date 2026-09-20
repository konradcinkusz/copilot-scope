using CopilotScope.Mcp;

// stdout is the MCP transport. Anything a human should read goes to stderr, which the
// client surfaces as server logs; a stray write to stdout corrupts the message stream.

if (args.Any(a => a is "--help" or "-h" or "help"))
{
    Console.WriteLine("""
        copilotscope-mcp — read-only MCP server over a running CopilotScope collector.

        Speaks the Model Context Protocol on stdin/stdout; it is started by an MCP client,
        not by hand. Register it with:

            claude mcp add copilotscope -- copilotscope mcp

        Environment:
          COPILOTSCOPE_COLLECTOR   collector base URL (default http://127.0.0.1:4318)
          COPILOTSCOPE_API_KEY     key with Read scope, if the deployment has keys

        Tools: health · list_sessions · get_session · overview · signal_coverage
        There is no write, delete, seed or import tool, by design.
        """);
    return 0;
}

var collectorUrl = Environment.GetEnvironmentVariable("COPILOTSCOPE_COLLECTOR") is { Length: > 0 } configured
    ? configured
    : "http://127.0.0.1:4318";

// HttpClient resolves a relative request URI against BaseAddress only when the base ends
// in a slash; without it "api/sessions" would replace the last path segment instead.
var normalized = collectorUrl.EndsWith('/') ? collectorUrl : collectorUrl + "/";

if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseAddress))
{
    Console.Error.WriteLine($"copilotscope-mcp: COPILOTSCOPE_COLLECTOR is not a valid URL: {collectorUrl}");
    return 1;
}

using var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(20) };

// Only sent when the deployment has keys. On the single-machine default there is none:
// the collector binds to 127.0.0.1 and a credential would buy nothing.
if (Environment.GetEnvironmentVariable("COPILOTSCOPE_API_KEY") is { Length: > 0 } apiKey)
    http.DefaultRequestHeaders.Add("x-api-key", apiKey);

Console.Error.WriteLine($"copilotscope-mcp: reading {baseAddress}");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var server = new McpServer(new ToolCatalog(new CollectorClient(http)));
await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellation.Token);

return 0;
