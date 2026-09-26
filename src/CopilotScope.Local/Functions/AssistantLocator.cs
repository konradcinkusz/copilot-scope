using System.Diagnostics;
using System.Text.Json.Nodes;
using CopilotScope.Dashboard.Functions;
using CopilotScope.Local.Connecting;

namespace CopilotScope.Local.Functions;

/// <summary>
/// Finds the assistants a function can run on, and what their administrators have forced on them.
/// </summary>
internal static class AssistantLocator
{
    public static string Program(FunctionAssistant assistant) =>
        assistant == FunctionAssistant.ClaudeCode ? "claude" : "copilot";

    /// <summary>
    /// The executable, from the PATH first and then the places each installer puts one: a desktop
    /// launcher often starts a process with a PATH that lacks the user's own bin directories.
    /// </summary>
    public static string? Find(Machine machine, FunctionAssistant assistant)
    {
        var program = Program(assistant);
        string[] extensions = machine.Os == Os.Windows ? [".exe", ".cmd", ".bat"] : [""];
        var directories = (machine.PathVariable ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(assistant == FunctionAssistant.ClaudeCode
                ? [Path.Combine(machine.Home, ".local", "bin"), Path.Combine(machine.ClaudeDirectory, "local")]
                : [Path.Combine(machine.Home, ".local", "bin")]);

        foreach (var directory in directories)
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, program + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { } // a PATH entry with characters no path may hold
            }
        return null;
    }

    /// <summary>The first line <c>--version</c> prints, or null when it would not say within a few seconds.</summary>
    public static string? Version(string path)
    {
        try
        {
            var start = new ProcessStartInfo(path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--version");
            ChildEnvironment.Apply(start);

            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(8)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where an administrator's Claude Code settings live. They apply over everything a launcher
    /// passes, <c>--restricted</c> and <c>--settings</c> included.
    /// </summary>
    public static IReadOnlyList<string> ClaudeManagedSettings(Machine machine) => machine.Os switch
    {
        Os.Windows => [@"C:\Program Files\ClaudeCode\managed-settings.json", @"C:\ProgramData\ClaudeCode\managed-settings.json"],
        Os.MacOS => ["/Library/Application Support/ClaudeCode/managed-settings.json"],
        _ => ["/etc/claude-code/managed-settings.json"]
    };

    /// <summary>
    /// A warning for the consent screen when managed settings switch Claude Code's telemetry on:
    /// the run will then report to wherever the organisation's policy points, which the person
    /// should know before they press Run. When that is this CopilotScope, the run's registered
    /// session id and its marker keep it out of the scores.
    /// </summary>
    public static string? ManagedTelemetryWarning(IEnumerable<string> managedSettingsFiles)
    {
        foreach (var file in managedSettingsFiles)
        {
            if (!File.Exists(file)) continue;
            if (!SettingsFile.TryRead(file, out var settings, out _))
                return $"Claude Code's managed settings at {file} could not be read, so whether they force telemetry on " +
                       "is unknown.";
            if (settings["env"] is not JsonObject env) continue;

            var enabled = Text(env["CLAUDE_CODE_ENABLE_TELEMETRY"]) is { } flag && flag.Trim() is not ("" or "0" or "false");
            var endpoint = Text(env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
            if (enabled || endpoint is not null)
                return $"Your organisation's managed settings ({file}) switch Claude Code telemetry on" +
                       (endpoint is null ? "" : $", to {endpoint}") + ". Managed settings override the launcher's, so this " +
                       "run's telemetry goes there too. If that is this CopilotScope, the run is still not scored.";
        }
        return null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString();
}
