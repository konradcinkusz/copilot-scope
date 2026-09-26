using CopilotScope.Dashboard.Functions;

namespace CopilotScope.Local.Functions;

/// <summary>
/// The environment a function run's assistant starts with: this process's, minus everything that
/// would send its telemetry anywhere or make it think it runs inside another session, plus the
/// switches that turn its telemetry off and mark whatever gets through (ADR-005, decision 5).
///
/// <para>Removed: every <c>OTEL_*</c> and <c>COPILOT_OTEL_*</c> variable (an exported endpoint alone
/// switches Copilot CLI's telemetry on), <c>COPILOT_ALLOW_ALL</c> (exactly "true" would also trust the
/// directory's hooks), <c>CLAUDECODE</c>, and every <c>CLAUDE_CODE_*</c> variable except the few that
/// carry a login or a model provider — a run started from inside a Claude Code session would
/// otherwise inherit that session's id and report into it. The ADR's wording scrubbed every
/// <c>CLAUDE_CODE_*</c>; that would also have dropped <c>CLAUDE_CODE_OAUTH_TOKEN</c>, which is how
/// <c>claude setup-token</c> users sign in, and moved their run off the subscription it is meant for.</para>
/// </summary>
internal static class ChildEnvironment
{
    /// <summary><c>CLAUDE_CODE_*</c> variables a run keeps: how Claude Code signs in and which provider it uses.</summary>
    internal static readonly string[] KeptClaudeCode =
    [
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
        "CLAUDE_CODE_SKIP_BEDROCK_AUTH", "CLAUDE_CODE_SKIP_VERTEX_AUTH", "CLAUDE_CODE_SKIP_FOUNDRY_AUTH",
        "CLAUDE_CODE_API_KEY_HELPER_TTL_MS",
        "CLAUDE_CODE_CLIENT_CERT", "CLAUDE_CODE_CLIENT_KEY", "CLAUDE_CODE_CLIENT_KEY_PASSPHRASE",
        "CLAUDE_CODE_GIT_BASH_PATH"
    ];

    /// <summary>What every run is started with, whatever it inherited.</summary>
    internal static readonly (string Name, string Value)[] Set =
    [
        ("CLAUDE_CODE_ENABLE_TELEMETRY", "0"),
        ("COPILOT_OTEL_ENABLED", "false"),
        ("OTEL_SDK_DISABLED", "true"),
        // If a managed policy switches telemetry back on, this is what the collector drops it by.
        ("OTEL_RESOURCE_ATTRIBUTES", $"{FunctionWorkspace.ObserverMarker}=true"),
        ("NO_COLOR", "1")
    ];

    /// <summary>Whether a variable is withheld from a run.</summary>
    public static bool Withheld(string name) =>
        name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COPILOT_OTEL_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("COPILOT_ALLOW_ALL", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
            && !KeptClaudeCode.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The environment to start a run with, from the one given.</summary>
    public static Dictionary<string, string> For(IEnumerable<KeyValuePair<string, string?>> current)
    {
        // Windows spells variable names in any case and means the same one.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, string>(comparer);
        foreach (var (name, value) in current)
            if (value is not null && !Withheld(name))
                result[name] = value;
        foreach (var (name, value) in Set) result[name] = value;
        return result;
    }

    /// <summary>Replaces a start's inherited environment with the one a run gets.</summary>
    public static void Apply(System.Diagnostics.ProcessStartInfo start)
    {
        var environment = For(Current());
        start.Environment.Clear();
        foreach (var (name, value) in environment) start.Environment[name] = value;
    }

    /// <summary>This process's environment, as <see cref="For"/> takes it.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Current()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            yield return new((string)entry.Key, entry.Value as string);
    }
}
