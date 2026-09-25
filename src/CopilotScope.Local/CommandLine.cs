namespace CopilotScope.Local;

/// <summary>What the user asked for on the command line.</summary>
internal sealed record LocalOptions
{
    public string Command { get; init; } = "start";

    /// <summary>OTLP ingest and the query API. 4318 is the OTLP/HTTP default every assistant
    /// exporter already points at, and the port the Docker stack publishes.</summary>
    public int OtlpPort { get; init; } = 4318;

    /// <summary>The dashboard — the port the Docker stack publishes it on, too.</summary>
    public int DashboardPort { get; init; } = 5200;

    public string? DataDirectory { get; init; }
    public bool Memory { get; init; }
    public string? WebRoot { get; init; }
    public bool NoBrowser { get; init; }

    /// <summary>Read no assistant's local history; telemetry only.</summary>
    public bool NoScan { get; init; }

    /// <summary>connect and disconnect: which assistant, by its canonical name.</summary>
    public string? Target { get; init; }

    /// <summary>connect and setup: also export prompt, response and tool text.</summary>
    public bool Capture { get; init; }

    /// <summary>connect and setup: Claude Code's beta spans, the only source of time-to-first-token.</summary>
    public bool Traces { get; init; }

    /// <summary>connect and setup: show the change, make none.</summary>
    public bool Print { get; init; }

    /// <summary>connect, setup and doctor: telemetry goes here instead of this machine's instance.</summary>
    public string? Endpoint { get; init; }

    /// <summary>setup: connect everything found without asking.</summary>
    public bool Yes { get; init; }

    /// <summary>scan: describe the history on disk instead of importing it.</summary>
    public bool Report { get; init; }

    /// <summary>capture-fixture: where the redacted files go.</summary>
    public string Out { get; init; } = "copilotscope-capture";

    /// <summary>capture-fixture: how many of the newest sessions or files to take.</summary>
    public int Limit { get; init; } = 3;
    public bool Verbose { get; init; }
}

/// <summary>
/// The command line, parsed by hand. A parsing library would be the host's first package, and
/// the dependency budget allows it none (CLAUDE.md): the grammar is a command and a handful of
/// flags, which is not worth a dependency to audit.
/// </summary>
internal static class CommandLine
{
    public static readonly string[] Commands =
        ["start", "status", "stop", "open", "url", "scan", "connect", "disconnect", "setup", "doctor", "capture-fixture",
            "version", "help"];

    public const string Usage = """
        copilotscope — session quality scores for AI coding assistants, on this machine.

        Usage: copilotscope [command] [options]

        Commands:
          start      Start the collector and the dashboard (the default). Ctrl+C stops both.
          status     Say whether CopilotScope is running, and where.
          stop       Stop a running CopilotScope.
          open       Open the dashboard in the browser.
          url        Print the dashboard's address.
          scan       Read local chat history now, and say what was found. --report: describe
                     every assistant's history on disk instead, by its shape, not its content.
          setup      Find the assistants on this machine and offer to connect each one.
          connect    Send an assistant's telemetry here: claude-code, vscode, copilot-cli, cowork, all.
          disconnect Take out exactly what connect wrote: one assistant, or all (the default).
          doctor     Check every link from each assistant's settings to the dashboard.
          capture-fixture <claude-code|vscode|copilot-cli>
                     Write a redacted sample of that assistant's history to ./copilotscope-capture
                     (--out <dir>, --limit <n>), for a reader to be built from. It sends nothing.
          version    Print the version.
          help       Show this text.

        Options for start:
          --otlp-port <port>       Telemetry (OTLP/HTTP) and API port. Default 4318.
          --dashboard-port <port>  Dashboard port. Default 5200; the next free one if taken.
          --data <directory>       Where sessions are kept. Default ~/.copilotscope/data.
          --memory                 Keep nothing on disk: history ends when the process does.
          --webroot <directory>    The dashboard's static files. Default: wwwroot beside the binary.
          --no-browser             Do not open the dashboard.
          --no-scan                Do not read local chat history; collect telemetry only.
          --verbose                Log what ASP.NET Core logs, too.

        Options for connect and setup:
          --capture                Also export prompt, response and tool text. Sensitive; off by default.
          --traces                 Claude Code: beta spans, the only source of time-to-first-token.
          --endpoint <url>         Send telemetry there instead of to this machine's CopilotScope.
          --print                  Show what would change, and change nothing.
          --yes                    setup: connect everything it finds without asking.

        While it runs, CopilotScope reads the chat history your assistants keep on this machine —
        Claude Code's transcripts — and scores each session once it has been quiet for ten
        minutes. It only ever reads those files, and keeps no prompt or response text.

        Everything binds to this machine only (127.0.0.1). Nothing leaves it: there is no
        account, no update check and no telemetry of CopilotScope's own. For a shared or team
        deployment, use the Docker Compose stack instead — see the README.
        """;

    public static (LocalOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        var options = new LocalOptions();
        string? command = null;
        bool help = false, version = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inline = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq and > 2)
            {
                inline = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            string Value(ref int index)
            {
                if (inline is not null) return inline;
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new FormatException($"{arg} needs a value.");
                return args[++index];
            }

            try
            {
                switch (arg)
                {
                    case "-h" or "--help":
                        help = true;
                        break;
                    case "--version":
                        version = true;
                        break;
                    case "--otlp-port":
                        options = options with { OtlpPort = Port(arg, Value(ref i)) };
                        break;
                    case "--dashboard-port":
                        options = options with { DashboardPort = Port(arg, Value(ref i)) };
                        break;
                    case "--data":
                        options = options with { DataDirectory = Value(ref i) };
                        break;
                    case "--webroot":
                        options = options with { WebRoot = Value(ref i) };
                        break;
                    case "--memory":
                        options = options with { Memory = true };
                        break;
                    case "--no-browser":
                        options = options with { NoBrowser = true };
                        break;
                    case "--no-scan":
                        options = options with { NoScan = true };
                        break;
                    case "--verbose":
                        options = options with { Verbose = true };
                        break;
                    case "--capture" or "--capture-content":
                        options = options with { Capture = true };
                        break;
                    case "--traces":
                        options = options with { Traces = true };
                        break;
                    case "--print" or "--dry-run":
                        options = options with { Print = true };
                        break;
                    case "--yes" or "-y":
                        options = options with { Yes = true };
                        break;
                    case "--endpoint":
                        options = options with { Endpoint = Endpoint(Value(ref i)) };
                        break;
                    case "--report":
                        options = options with { Report = true };
                        break;
                    case "--out":
                        options = options with { Out = Value(ref i) };
                        break;
                    case "--limit":
                        options = options with
                        {
                            Limit = int.TryParse(Value(ref i), out var limit) && limit is > 0 and <= 100
                                ? limit
                                : throw new FormatException("--limit must be a number from 1 to 100.")
                        };
                        break;
                    default:
                        if (arg.StartsWith('-')) return (null, $"Unknown option '{arg}'. Run 'copilotscope help'.");
                        if (command == "capture-fixture" && options.Target is null)
                        {
                            if (Capturing.LocalHistory.Normalize(arg) is not { } source)
                                return (null, $"Unknown assistant '{arg}': claude-code, vscode or copilot-cli.");
                            options = options with { Target = source };
                            break;
                        }
                        if (command is "connect" or "disconnect" && options.Target is null)
                        {
                            if (Connecting.Connector.Normalize(arg) is not { } target)
                                return (null, $"Unknown assistant '{arg}': claude-code, vscode, copilot-cli, cowork or all.");
                            options = options with { Target = target };
                            break;
                        }
                        if (command is not null) return (null, $"Unexpected argument '{arg}'. Run 'copilotscope help'.");
                        if (!Commands.Contains(arg)) return (null, $"Unknown command '{arg}'. Run 'copilotscope help'.");
                        command = arg;
                        break;
                }
            }
            catch (FormatException ex)
            {
                return (null, ex.Message);
            }
        }

        if (options.Memory && options.DataDirectory is not null)
            return (null, "--memory and --data contradict each other: one keeps nothing, the other says where to keep it.");
        if (options.OtlpPort == options.DashboardPort)
            return (null, "The telemetry port and the dashboard port must differ.");
        if (!help && command == "connect" && options.Target is null)
            return (null, "connect needs an assistant: claude-code, vscode, copilot-cli, cowork or all.");
        if ((options.Capture || options.Traces || options.Print) && command is not ("connect" or "setup") && !help)
            return (null, "--capture, --traces and --print apply to connect and setup.");
        if (options.Yes && command != "setup" && !help)
            return (null, "--yes applies to setup.");
        if (options.Report && command != "scan" && !help)
            return (null, "--report applies to scan.");
        if (!help && command == "capture-fixture" && options.Target is null)
            return (null, "capture-fixture needs an assistant: claude-code, vscode or copilot-cli.");

        // --help and --version answer whatever else is on the line, as every CLI's do.
        return (options with { Command = help ? "help" : version ? "version" : command ?? "start" }, null);
    }

    private static string Endpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? value.TrimEnd('/')
            : throw new FormatException($"--endpoint must be an http:// or https:// address, not '{value}'.");

    private static int Port(string name, string value) =>
        int.TryParse(value, out var port) && port is > 0 and < 65536
            ? port
            : throw new FormatException($"{name} must be a port number between 1 and 65535, not '{value}'.");
}
