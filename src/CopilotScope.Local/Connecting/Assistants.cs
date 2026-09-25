using System.Text.Json.Nodes;

namespace CopilotScope.Local.Connecting;

/// <summary>What connecting writes: where telemetry goes, a key when the collector wants one,
/// and the two opt-ins.</summary>
internal sealed record ConnectSettings(string Endpoint, string? ApiKey = null, bool Capture = false, bool Traces = false);

internal enum Connection
{
    NotInstalled,
    NotConnected,
    /// <summary>Sends telemetry here.</summary>
    Connected,
    /// <summary>Sends telemetry, but to another address.</summary>
    Elsewhere,
    /// <summary>Points here, but misses a setting it needs.</summary>
    Incomplete,
    /// <summary>The settings file could not be read as strict JSON.</summary>
    Unreadable
}

internal sealed record AssistantStatus(string Name, Connection Connection, string? Where, string? Detail = null);

/// <summary>
/// The settings each assistant reads, key for key the ones <c>scripts/copilotscope</c> writes
/// (<c>claude_env_json</c>, <c>vscode_settings_json</c>, <c>connect_copilot_cli</c>) and removes
/// (<c>cmd_disconnect</c>). A test holds these lists to the script's, so the binary and the
/// Docker path never disagree about what "connected" means.
/// </summary>
internal static class Assistants
{
    public const string ClaudeCode = "Claude Code";
    public const string VsCode = "VS Code (Copilot Chat)";
    public const string CopilotCli = "GitHub Copilot CLI";

    /// <summary>For a list in one line.</summary>
    public static string ShortName(string name) => name switch
    {
        VsCode => "VS Code",
        CopilotCli => "Copilot CLI",
        _ => name
    };

    // ------------------------------------------------------------------ Claude Code

    public static JsonObject ClaudeCodePatch(ConnectSettings settings)
    {
        var env = new JsonObject
        {
            ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1",
            ["OTEL_METRICS_EXPORTER"] = "otlp",
            ["OTEL_LOGS_EXPORTER"] = "otlp",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = settings.Endpoint,
            ["OTEL_METRIC_EXPORT_INTERVAL"] = "10000",
            ["OTEL_LOGS_EXPORT_INTERVAL"] = "5000",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.name=claude-code"
        };
        if (settings.Traces)
        {
            env["CLAUDE_CODE_ENHANCED_TELEMETRY_BETA"] = "1";
            env["OTEL_TRACES_EXPORTER"] = "otlp";
        }
        if (settings.Capture)
        {
            env["OTEL_LOG_USER_PROMPTS"] = "1";
            env["OTEL_LOG_ASSISTANT_RESPONSES"] = "1";
            env["OTEL_LOG_TOOL_DETAILS"] = "1";
        }
        if (settings.ApiKey is { Length: > 0 } key) env["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-api-key={key}";
        return new JsonObject { ["env"] = env };
    }

    /// <summary>Every variable connecting may have put in <c>env</c>: what disconnecting removes.</summary>
    public static readonly string[] ClaudeCodeVariables =
    [
        "CLAUDE_CODE_ENABLE_TELEMETRY", "CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "OTEL_METRICS_EXPORTER",
        "OTEL_LOGS_EXPORTER", "OTEL_TRACES_EXPORTER", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_METRIC_EXPORT_INTERVAL", "OTEL_LOGS_EXPORT_INTERVAL",
        "OTEL_RESOURCE_ATTRIBUTES", "OTEL_LOG_USER_PROMPTS", "OTEL_LOG_ASSISTANT_RESPONSES", "OTEL_LOG_TOOL_DETAILS"
    ];

    public static IEnumerable<string[]> ClaudeCodeKeys => ClaudeCodeVariables.Select(variable => new[] { "env", variable });

    public static AssistantStatus ClaudeCodeStatus(Machine machine, string endpoint)
    {
        var file = machine.ClaudeSettings;
        var installed = Directory.Exists(machine.ClaudeDirectory) || machine.OnPath("claude");
        if (!SettingsFile.TryRead(file, out var settings, out _))
            return new(ClaudeCode, Connection.Unreadable, file);

        var env = settings["env"] as JsonObject;
        if (Text(env?["CLAUDE_CODE_ENABLE_TELEMETRY"]) != "1")
            return new(ClaudeCode, installed ? Connection.NotConnected : Connection.NotInstalled, file);
        if (Text(env?["OTEL_EXPORTER_OTLP_ENDPOINT"]) is var target && !SameEndpoint(target, endpoint))
            return new(ClaudeCode, Connection.Elsewhere, file, target ?? "no endpoint, so the default gRPC one");
        if (Text(env?["OTEL_LOGS_EXPORTER"]) != "otlp")
            return new(ClaudeCode, Connection.Incomplete, file,
                "OTEL_LOGS_EXPORTER is not otlp, and the log events are what carry the session");
        return new(ClaudeCode, Connection.Connected, file);
    }

    // ------------------------------------------------------------------ VS Code

    public static JsonObject VsCodePatch(ConnectSettings settings)
    {
        var patch = new JsonObject
        {
            ["github.copilot.chat.otel.enabled"] = true,
            ["github.copilot.chat.otel.otlpEndpoint"] = settings.Endpoint,
            ["github.copilot.chat.otel.exporterType"] = "otlp-http"
        };
        if (settings.Capture) patch["github.copilot.chat.otel.captureContent"] = true;
        return patch;
    }

    public static readonly string[] VsCodeSettingNames =
    [
        "github.copilot.chat.otel.enabled", "github.copilot.chat.otel.otlpEndpoint",
        "github.copilot.chat.otel.exporterType", "github.copilot.chat.otel.captureContent"
    ];

    /// <summary>Flat names that contain dots: each is one segment, not a path.</summary>
    public static IEnumerable<string[]> VsCodeKeys => VsCodeSettingNames.Select(name => new[] { name });

    public static AssistantStatus VsCodeStatus(Machine machine, string endpoint)
    {
        if (machine.VsCodeSettings() is not { } file) return new(VsCode, Connection.NotInstalled, null);
        if (!SettingsFile.TryRead(file, out var settings, out _)) return new(VsCode, Connection.Unreadable, file);

        if (settings["github.copilot.chat.otel.enabled"] is not JsonValue enabled
            || !enabled.TryGetValue<bool>(out var on) || !on)
            return new(VsCode, Connection.NotConnected, file);
        var target = Text(settings["github.copilot.chat.otel.otlpEndpoint"]);
        return SameEndpoint(target, endpoint)
            ? new(VsCode, Connection.Connected, file)
            : new(VsCode, Connection.Elsewhere, file, target ?? "no endpoint");
    }

    // ------------------------------------------------------------------ Copilot CLI

    /// <summary>Environment variables only: Copilot CLI reads no settings file for telemetry.</summary>
    public static IReadOnlyList<(string Name, string Value)> CopilotCliVariables(ConnectSettings settings)
    {
        var variables = new List<(string, string)>
        {
            ("COPILOT_OTEL_ENABLED", "true"),
            ("COPILOT_OTEL_EXPORTER_TYPE", "otlp-http"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", settings.Endpoint),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
        };
        // The OTel GenAI standard's name; COPILOT_OTEL_CAPTURE_CONTENT does not exist.
        if (settings.Capture) variables.Add(("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true"));
        if (settings.ApiKey is { Length: > 0 } key) variables.Add(("OTEL_EXPORTER_OTLP_HEADERS", $"x-api-key={key}"));
        return variables;
    }

    public static readonly string[] CopilotCliVariableNames =
    [
        "COPILOT_OTEL_ENABLED", "COPILOT_OTEL_EXPORTER_TYPE", "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "OTEL_EXPORTER_OTLP_HEADERS"
    ];

    /// <summary>The block's marker in a shell profile — the control script's own.</summary>
    public const string CopilotCliMarker = "copilot-cli";

    /// <summary>The variables as lines of the user's shell.</summary>
    public static IReadOnlyList<string> ProfileLines(IReadOnlyList<(string Name, string Value)> variables, bool fish) =>
        variables.Select(v => fish ? $"set -gx {v.Name} \"{v.Value}\"" : $"export {v.Name}=\"{v.Value}\"").ToList();

    public static AssistantStatus CopilotCliStatus(Machine machine, string endpoint, IUserEnvironment? userEnvironment)
    {
        var installed = machine.OnPath("copilot") || Directory.Exists(Path.Combine(machine.Home, ".copilot"));
        Dictionary<string, string> set;
        string where;
        if (machine.Os == Os.Windows && userEnvironment is not null)
        {
            where = "your user environment variables";
            set = CopilotCliVariableNames
                .Select(name => (name, value: userEnvironment.Get(name)))
                .Where(v => v.value is not null)
                .ToDictionary(v => v.name, v => v.value!);
        }
        else
        {
            where = machine.ShellProfile.Path;
            set = ParseProfile(RcBlock.Read(where, CopilotCliMarker) ?? []);
        }

        if (set.GetValueOrDefault("COPILOT_OTEL_ENABLED") != "true")
            return new(CopilotCli, installed ? Connection.NotConnected : Connection.NotInstalled, where);
        var target = set.GetValueOrDefault("OTEL_EXPORTER_OTLP_ENDPOINT");
        return SameEndpoint(target, endpoint)
            ? new(CopilotCli, Connection.Connected, where)
            : new(CopilotCli, Connection.Elsewhere, where, target ?? "no endpoint");
    }

    /// <summary>The variables a profile block sets, in either shell's spelling.</summary>
    internal static Dictionary<string, string> ParseProfile(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            string name, value;
            if (line.StartsWith("export ", StringComparison.Ordinal) && line.IndexOf('=') is var eq and > 7)
                (name, value) = (line[7..eq].Trim(), line[(eq + 1)..].Trim());
            else if (line.StartsWith("set -gx ", StringComparison.Ordinal) && line[8..].Trim().Split(' ', 2) is [var n, var v])
                (name, value) = (n, v.Trim());
            else continue;
            values[name] = value.Trim('"', '\'');
        }
        return values;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// Whether two endpoints reach the same collector: scheme, port and host, with every
    /// loopback spelling taken as one. Someone who connected with <c>127.0.0.1</c> is connected.
    /// </summary>
    public static bool SameEndpoint(string? configured, string endpoint)
    {
        if (configured is null) return false;
        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var a) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var b))
            return string.Equals(configured.Trim().TrimEnd('/'), endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        static string Host(Uri uri) => uri.IsLoopback ? "loopback" : uri.Host.ToLowerInvariant();
        return a.Scheme == b.Scheme && a.Port == b.Port && Host(a) == Host(b)
               && a.AbsolutePath.TrimEnd('/') == b.AbsolutePath.TrimEnd('/');
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
