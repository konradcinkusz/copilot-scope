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
    public bool Verbose { get; init; }
}

/// <summary>
/// The command line, parsed by hand. A parsing library would be the host's first package, and
/// the dependency budget allows it none (CLAUDE.md): the grammar is a command and a handful of
/// flags, which is not worth a dependency to audit.
/// </summary>
internal static class CommandLine
{
    public static readonly string[] Commands = ["start", "status", "stop", "open", "url", "version", "help"];

    public const string Usage = """
        copilotscope — session quality scores for AI coding assistants, on this machine.

        Usage: copilotscope [command] [options]

        Commands:
          start      Start the collector and the dashboard (the default). Ctrl+C stops both.
          status     Say whether CopilotScope is running, and where.
          stop       Stop a running CopilotScope.
          open       Open the dashboard in the browser.
          url        Print the dashboard's address.
          version    Print the version.
          help       Show this text.

        Options for start:
          --otlp-port <port>       Telemetry (OTLP/HTTP) and API port. Default 4318.
          --dashboard-port <port>  Dashboard port. Default 5200; the next free one if taken.
          --data <directory>       Where sessions are kept. Default ~/.copilotscope/data.
          --memory                 Keep nothing on disk: history ends when the process does.
          --webroot <directory>    The dashboard's static files. Default: wwwroot beside the binary.
          --no-browser             Do not open the dashboard.
          --verbose                Log what ASP.NET Core logs, too.

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
                    case "--verbose":
                        options = options with { Verbose = true };
                        break;
                    default:
                        if (arg.StartsWith('-')) return (null, $"Unknown option '{arg}'. Run 'copilotscope help'.");
                        if (!Commands.Contains(arg)) return (null, $"Unknown command '{arg}'. Run 'copilotscope help'.");
                        if (command is not null) return (null, $"Unexpected argument '{arg}'. Run 'copilotscope help'.");
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

        // --help and --version answer whatever else is on the line, as every CLI's do.
        return (options with { Command = help ? "help" : version ? "version" : command ?? "start" }, null);
    }

    private static int Port(string name, string value) =>
        int.TryParse(value, out var port) && port is > 0 and < 65536
            ? port
            : throw new FormatException($"{name} must be a port number between 1 and 65535, not '{value}'.");
}
