using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector.Import;

namespace CopilotScope.Local.Connecting;

/// <summary>
/// <c>copilotscope doctor</c>: walks the path telemetry takes on this machine — each assistant's
/// settings, the variables that override them, the running instance, its storage and the
/// dashboard's files — and names the first broken link, with the command that fixes it. The
/// native binary's counterpart of the control script's doctor, which checks Docker instead.
/// </summary>
internal sealed class Doctor(Machine machine, Say say, IUserEnvironment? userEnvironment = null,
    Func<string, string?>? variables = null)
{
    // This process's environment: the shell doctor runs in is the one whose overrides matter.
    private readonly Func<string, string?> _variable = variables ?? Environment.GetEnvironmentVariable;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="running">The instance this machine runs, if it answers.</param>
    /// <param name="collector">A client for it; null when it is not running.</param>
    /// <param name="endpoint">Where assistants should send telemetry.</param>
    /// <param name="otlpPort">The port a stopped instance would take, to say what holds it.</param>
    /// <param name="webRoot">The dashboard's files, as a start would resolve them.</param>
    /// <param name="transcriptRoots">Where Claude Code's history is read from.</param>
    public async Task<int> RunAsync(Instance? running, HttpClient? collector, string endpoint, int otlpPort,
        string webRoot, IReadOnlyList<string> transcriptRoots, CancellationToken ct = default)
    {
        say.Line("CopilotScope doctor");

        say.Head("CopilotScope");
        var health = running is not null && collector is not null ? await HealthAsync(collector, ct) : null;
        if (running is null)
        {
            switch (await Ports.ProbeAsync(otlpPort))
            {
                case PortState.CopilotScope:
                    say.Warn($"not running here, but a CopilotScope collector answers on port {otlpPort}, most likely " +
                             "the Docker stack: assistants connected to it report there.");
                    break;
                case PortState.Other:
                    say.Bad($"not running, and another program holds port {otlpPort}: telemetry sent to {endpoint} " +
                            "does not reach CopilotScope. Free the port, or start with --otlp-port.");
                    break;
                default:
                    say.Bad($"not running: start it with `copilotscope`. Telemetry sent to {endpoint} until then is lost.");
                    break;
            }
        }
        else
        {
            say.Ok($"running (pid {running.ProcessId}, {running.Version}): dashboard {running.DashboardUrl}, " +
                   $"telemetry {running.CollectorUrl}");
            if (health is { } h)
            {
                var sessions = h.TryGetProperty("sessions", out var s) && s.TryGetInt32(out var n) ? n : 0;
                if (h.TryGetProperty("storage", out var storage) && storage.GetString() == "memory")
                    say.Warn($"sessions are kept in memory only (started with --memory): {sessions} now, none after it stops");
                else
                    say.Ok($"sessions kept in {running.Storage} ({sessions} in memory now)");
            }
            else say.Bad($"{running.CollectorUrl}/api/health does not answer.");
        }

        if (WebRoot.IsComplete(webRoot)) say.Ok($"dashboard files complete ({webRoot})");
        else say.Bad($"the dashboard's files are missing from {webRoot}: reinstall, or start with --webroot <directory>");

        say.Head("Assistants");
        var claude = Assistants.ClaudeCodeStatus(machine, endpoint);
        Report(claude, "claude-code");
        if (claude.Connection == Connection.Connected && collector is not null)
            await CheckClaudeCodeRestartedAsync(collector, transcriptRoots, ct);
        Report(Assistants.VsCodeStatus(machine, endpoint), "vscode",
            connectedHint: "If sessions still do not appear: reload the window, and chat in Agent mode.");
        Report(Assistants.CopilotCliStatus(machine, endpoint, userEnvironment), "copilot-cli",
            connectedHint: CopilotCliHint(endpoint));
        say.Info("Claude Cowork is configured in the Claude desktop app: `copilotscope connect cowork` says how.");

        say.Head("This shell");
        if (_variable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 } exported
            && !Assistants.SameEndpoint(exported, endpoint))
        {
            say.Warn($"OTEL_EXPORTER_OTLP_ENDPOINT is set to {exported} here, and it beats a settings file for " +
                     "anything started from this shell.", problem: true);
        }
        else say.Ok("no OTEL_EXPORTER_OTLP_ENDPOINT pointing anywhere else");

        say.Head("History on disk");
        foreach (var root in transcriptRoots)
        {
            if (!Directory.Exists(root)) { say.Info($"{root} does not exist"); continue; }
            var count = ClaudeCodeFiles.Discover(root).Count();
            if (count > 0) say.Ok($"{count} Claude Code transcript(s) in {root}, read while CopilotScope runs");
            else say.Info($"no transcripts in {root} yet");
        }

        say.Line();
        if (say.Problems == 0)
        {
            say.Ok("no problems found.");
            return 0;
        }
        say.Line($"{say.Problems} problem(s) above.");
        return 1;
    }

    private void Report(AssistantStatus status, string target, string? connectedHint = null)
    {
        switch (status.Connection)
        {
            case Connection.NotInstalled:
                say.Info($"{status.Name}: not found on this machine");
                break;
            case Connection.NotConnected:
                say.Info($"{status.Name}: sends no telemetry here. `copilotscope connect {target}`");
                break;
            case Connection.Connected:
                say.Ok($"{status.Name} sends telemetry here ({status.Where})");
                if (connectedHint is not null) say.Info(connectedHint);
                break;
            case Connection.Elsewhere:
                say.Warn($"{status.Name} sends telemetry to {status.Detail}, not here ({status.Where}). " +
                         $"`copilotscope connect {target}`", problem: true);
                break;
            case Connection.Incomplete:
                say.Bad($"{status.Name}: {status.Detail} ({status.Where}). `copilotscope connect {target}`");
                break;
            case Connection.Unreadable:
                say.Warn($"{status.Name}: {status.Where} is not strict JSON, so it cannot be checked.");
                break;
        }
    }

    /// <summary>
    /// The mistake everyone makes once: connecting Claude Code, and then carrying on in a session
    /// that read its settings before they changed. When Claude Code has written transcripts since
    /// its settings were, and none of its telemetry has arrived since, that is what happened.
    /// </summary>
    private async Task CheckClaudeCodeRestartedAsync(HttpClient collector, IReadOnlyList<string> transcriptRoots,
        CancellationToken ct)
    {
        var connectedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(machine.ClaudeSettings), TimeSpan.Zero);
        var lastRun = transcriptRoots.Where(Directory.Exists)
            .SelectMany(ClaudeCodeFiles.Discover)
            .Select(file => new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero))
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (lastRun < connectedAt.AddMinutes(1)) return; // not run since: nothing to expect yet

        try
        {
            var page = await collector.GetFromJsonAsync<JsonElement>(
                $"/api/sessions?emitter=ClaudeCode&limit=200&since={Uri.EscapeDataString(connectedAt.ToString("O"))}", Json, ct);
            var live = page.TryGetProperty("sessions", out var sessions) && sessions.EnumerateArray()
                .Any(s => s.TryGetProperty("origin", out var origin) && origin.GetString() == "otel");
            if (live) say.Ok("Claude Code telemetry has arrived since it was connected");
            else say.Warn("Claude Code has run since it was connected, but none of its telemetry has arrived. " +
                          "Restart claude: each session reads its settings when it starts.", problem: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException) { }
    }

    private string? CopilotCliHint(string endpoint)
    {
        if (machine.Os == Os.Windows) return "Terminals opened before connecting do not have the variables yet.";
        return Assistants.SameEndpoint(_variable("OTEL_EXPORTER_OTLP_ENDPOINT"), endpoint)
               && _variable("COPILOT_OTEL_ENABLED") == "true"
            ? null
            : $"This terminal does not have the variables yet: open a new one, or `source {machine.ShellProfile.Path}`.";
    }

    private static async Task<JsonElement?> HealthAsync(HttpClient collector, CancellationToken ct)
    {
        try { return await collector.GetFromJsonAsync<JsonElement>("/api/health", Json, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }
}
