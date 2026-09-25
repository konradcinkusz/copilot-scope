using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotScope.Local.Connecting;

/// <summary>Lines for a person, in the control script's shape: a heading, then ✓ / ! / ✗ / - items.</summary>
internal sealed class Say(TextWriter writer)
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>What went wrong, counted, for a command's summary and exit code.</summary>
    public int Problems { get; private set; }

    private string? _section;

    /// <summary>Starts a section, unless it is the one already open: showing a change and then
    /// making it is one section, not two.</summary>
    public void Head(string text)
    {
        if (text == _section) return;
        _section = text;
        writer.WriteLine();
        writer.WriteLine(text);
    }

    public void Ok(string text) => writer.WriteLine($"  ✓ {text}");

    public void Warn(string text, bool problem = false)
    {
        if (problem) Problems++;
        writer.WriteLine($"  ! {text}");
    }

    public void Bad(string text)
    {
        Problems++;
        writer.WriteLine($"  ✗ {text}");
    }

    public void Info(string text) => writer.WriteLine($"  - {text}");

    public void Line(string text = "") => writer.WriteLine(text);

    public void Block(IEnumerable<string> lines)
    {
        foreach (var line in lines) writer.WriteLine($"      {line}");
    }

    public void Json(JsonNode node) => Block(node.ToJsonString(Indented).Split('\n'));
}

/// <summary>
/// <c>connect</c>, <c>disconnect</c> and <c>setup</c>: pointing an assistant's own telemetry at
/// this CopilotScope, in its own settings, and taking exactly that back out. The native binary's
/// port of the control script's commands, with nothing left for python or node to do.
/// </summary>
internal sealed class Connector(Machine machine, Say say, IUserEnvironment? userEnvironment = null)
{
    /// <summary>A target's canonical name, from the spellings the control script accepts too.</summary>
    public static string? Normalize(string target) => target switch
    {
        "claude-code" or "claude" => "claude-code",
        "vscode" or "vs-code" or "code" => "vscode",
        "copilot-cli" or "cli" => "copilot-cli",
        "cowork" => "cowork",
        "all" => "all",
        _ => null
    };

    /// <summary>0 when everything asked for is in place, 1 when something is left to do by hand.
    /// <c>all</c> means Claude Code and VS Code, as it does for the control script: a shell
    /// profile is written only when asked for by name.</summary>
    public int Connect(string target, ConnectSettings settings, bool print)
    {
        var done = target switch
        {
            "claude-code" => ConnectClaudeCode(settings, print),
            "vscode" => ConnectVsCode(settings, print),
            "copilot-cli" => ConnectCopilotCli(settings, print),
            "cowork" => ConnectCowork(settings),
            "all" => ConnectClaudeCode(settings, print) & ConnectVsCode(settings, print),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
        return done ? 0 : 1;
    }

    public int Disconnect(string target)
    {
        var all = target == "all";
        var done = true;
        if (all || target == "claude-code") done &= DisconnectClaudeCode();
        if (all || target == "vscode") done &= DisconnectVsCode();
        if (all || target == "copilot-cli") done &= DisconnectCopilotCli();
        if (target == "cowork")
            say.Info("Claude Cowork keeps its endpoint in the Claude desktop app; remove it there.");
        return done ? 0 : 1;
    }

    // ------------------------------------------------------------------ Claude Code

    public bool ConnectClaudeCode(ConnectSettings settings, bool print)
    {
        var file = machine.ClaudeSettings;
        var patch = Assistants.ClaudeCodePatch(settings);
        say.Head(Assistants.ClaudeCode);
        if (print)
        {
            say.Info($"would merge into {file}:");
            say.Json(patch);
            return true;
        }

        var result = Edit(file, () => SettingsFile.Merge(file, patch));
        switch (result)
        {
            case EditResult.Written or EditResult.Unchanged:
                say.Ok(result == EditResult.Written
                    ? $"{file} updated."
                    : $"{file} already sends telemetry to {settings.Endpoint}.");
                say.Info("Applies to every terminal, every project, and to Claude Code inside VS Code.");
                say.Info("Start claude again: a running session read its settings when it started.");
                if (settings.Capture) say.Warn("Prompt, response and tool text will be exported.");
                if (settings.Traces) say.Info("Beta spans on: adds time-to-first-token, and the schema may still change.");
                return true;
            case EditResult.Unparseable:
                say.Bad($"{file} is not valid JSON, so it was left untouched.");
                say.Info("Claude Code settings must be strict JSON: no comments, no trailing commas.");
                say.Info("Fix the file, or add this block by hand:");
                say.Json(patch);
                return false;
            default:
                return false;
        }
    }

    private bool DisconnectClaudeCode()
    {
        var file = machine.ClaudeSettings;
        switch (Edit(file, () => SettingsFile.Remove(file, Assistants.ClaudeCodeKeys)))
        {
            case EditResult.Written:
                say.Ok($"Claude Code telemetry settings removed from {file}");
                return true;
            case EditResult.Unparseable:
                say.Warn($"could not read {file} as JSON; remove the OTEL_* and CLAUDE_CODE_* keys under env by hand", problem: true);
                return false;
            case EditResult.Unchanged or EditResult.Missing:
                say.Info($"Claude Code: nothing of CopilotScope's in {file}");
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ VS Code

    public bool ConnectVsCode(ConnectSettings settings, bool print)
    {
        var patch = Assistants.VsCodePatch(settings);
        say.Head(Assistants.VsCode);
        if (machine.VsCodeSettings() is not { } file)
        {
            say.Warn("No VS Code settings found. Is it installed?");
            say.Info("Add this to Settings (JSON): Ctrl/Cmd+Shift+P → Preferences: Open User Settings (JSON)");
            say.Json(patch);
            return false;
        }
        if (print)
        {
            say.Info($"would merge into {file}:");
            say.Json(patch);
            return true;
        }

        var result = Edit(file, () => SettingsFile.Merge(file, patch));
        switch (result)
        {
            case EditResult.Written or EditResult.Unchanged:
                say.Ok(result == EditResult.Written ? $"{file} updated." : $"{file} already sends telemetry to {settings.Endpoint}.");
                break;
            case EditResult.Unparseable:
                say.Warn($"{file} was left untouched: it uses comments or trailing commas.");
                say.Info("VS Code accepts them and a JSON parser does not; rewriting the file would drop them.");
                say.Info("Add this by hand:");
                say.Json(patch);
                return false;
            default:
                return false;
        }

        say.Warn("Reload the VS Code window: Ctrl/Cmd+Shift+P → Developer: Reload Window.");
        say.Info("Then chat in Agent mode: inline completions alone send no chat telemetry.");
        if (settings.ApiKey is { Length: > 0 } key)
        {
            say.Warn("The collector wants a key, and VS Code takes it only from the environment:");
            say.Info($"export OTEL_EXPORTER_OTLP_HEADERS=\"x-api-key={key}\", then start VS Code from that shell.");
        }
        return true;
    }

    private bool DisconnectVsCode()
    {
        if (machine.VsCodeSettings() is not { } file || !File.Exists(file)) return true;
        switch (Edit(file, () => SettingsFile.Remove(file, Assistants.VsCodeKeys)))
        {
            case EditResult.Written:
                say.Ok($"Copilot telemetry settings removed from {file} (reload the window)");
                return true;
            case EditResult.Unparseable:
                say.Warn($"could not read {file} as strict JSON; remove the github.copilot.chat.otel.* keys by hand", problem: true);
                return false;
            default:
                say.Info($"VS Code: nothing of CopilotScope's in {file}");
                return true;
        }
    }

    // ------------------------------------------------------------------ Copilot CLI

    public bool ConnectCopilotCli(ConnectSettings settings, bool print)
    {
        var variables = Assistants.CopilotCliVariables(settings);
        say.Head(Assistants.CopilotCli);

        if (machine.Os == Os.Windows && userEnvironment is not null)
        {
            if (print)
            {
                say.Info("would set in your user environment variables:");
                say.Block(variables.Select(v => $"{v.Name} = {v.Value}"));
                return true;
            }
            try
            {
                foreach (var (name, value) in variables) userEnvironment.Set(name, value);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                say.Bad($"could not set user environment variables: {ex.Message}");
                return false;
            }
            say.Ok("Set in your user environment variables: every new terminal has them.");
            say.Info("Copilot CLI reads environment variables only, which is why this one is not a settings file.");
        }
        else
        {
            var (profile, fish) = machine.ShellProfile;
            var lines = Assistants.ProfileLines(variables, fish);
            if (print)
            {
                say.Info($"would add to {profile}:");
                say.Block(lines);
                return true;
            }
            bool changed;
            try { changed = RcBlock.Write(profile, Assistants.CopilotCliMarker, lines); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                say.Bad($"could not write {profile}: {ex.Message}");
                return false;
            }
            say.Ok(changed
                ? $"{profile} updated (replacing any earlier CopilotScope block)."
                : $"{profile} already sends Copilot CLI telemetry to {settings.Endpoint}.");
            say.Info($"Copilot CLI reads environment variables only: open a new terminal, or run `source {profile}`.");
        }

        if (settings.ApiKey is { Length: > 0 })
            say.Warn("The collector's key is now stored in plain text with those variables.");
        say.Info("COPILOT_OTEL_CAPTURE_CONTENT is not a real variable; --capture sets the OTel GenAI standard's,");
        say.Info("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT.");
        return true;
    }

    private bool DisconnectCopilotCli()
    {
        if (machine.Os == Os.Windows && userEnvironment is not null)
        {
            foreach (var name in Assistants.CopilotCliVariableNames) userEnvironment.Set(name, null);
            say.Ok("Copilot CLI variables removed from your user environment variables.");
            return true;
        }

        // Every profile the block could be in, not only the current shell's: someone who changed
        // shells since connecting still wants it gone.
        var removed = false;
        var (current, _) = machine.ShellProfile;
        foreach (var profile in new[] { current, Path.Combine(machine.Home, ".bashrc"), Path.Combine(machine.Home, ".zshrc"),
                     Path.Combine(machine.XdgConfigHome ?? Path.Combine(machine.Home, ".config"), "fish", "config.fish") }.Distinct())
        {
            try
            {
                if (!RcBlock.Contains(profile, Assistants.CopilotCliMarker)) continue;
                RcBlock.Write(profile, Assistants.CopilotCliMarker, []);
                say.Ok($"Copilot CLI block removed from {profile} (open a new terminal)");
                removed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                say.Warn($"could not edit {profile}: {ex.Message}", problem: true);
                return false;
            }
        }
        if (!removed) say.Info("Copilot CLI: no CopilotScope block in any shell profile");
        return true;
    }

    // ------------------------------------------------------------------ Cowork

    public bool ConnectCowork(ConnectSettings settings)
    {
        say.Head("Claude Cowork (Claude desktop app)");
        say.Info("Cowork is configured in the app's own settings, not in a file this can write:");
        say.Line();
        say.Line("    Claude Desktop → organization / Cowork settings → monitoring");
        say.Line($"    OTLP endpoint:  {settings.Endpoint.TrimEnd('/')}/v1/logs");
        say.Line("    Protocol:       HTTP");
        say.Line();
        say.Warn("The full /v1/logs path, not the base endpoint Claude Code takes.");
        say.Warn("Restart the app afterwards: configuration is read at session start.");
        say.Info("Needs a Team or Enterprise plan, Claude Desktop 1.1.4173+, and org admin access.");
        return true;
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Every assistant on this machine, and an offer to connect each one that is not: the
    /// change is shown first, and nothing is written without a yes — typed, or given up front
    /// with <c>--yes</c>. With no terminal to ask in and no <c>--yes</c>, nothing is written.
    /// </summary>
    public int Setup(ConnectSettings settings, bool yes, Func<string, bool>? ask, bool print = false)
    {
        say.Line($"CopilotScope receives telemetry at {settings.Endpoint}.");
        var found = new[]
        {
            (Status: Assistants.ClaudeCodeStatus(machine, settings.Endpoint), Target: "claude-code"),
            (Status: Assistants.VsCodeStatus(machine, settings.Endpoint), Target: "vscode"),
            (Status: Assistants.CopilotCliStatus(machine, settings.Endpoint, userEnvironment), Target: "copilot-cli")
        };

        // What needs nothing first, so the questions come together after it.
        foreach (var (status, _) in found)
        {
            if (status.Connection == Connection.NotInstalled) say.Info($"{status.Name}: not found on this machine");
            else if (status.Connection == Connection.Connected) say.Ok($"{status.Name} already sends telemetry here ({status.Where})");
        }

        var offered = 0;
        var declined = 0;
        foreach (var (status, target) in found)
        {
            if (status.Connection is Connection.NotInstalled or Connection.Connected) continue;
            offered++;
            Connect(target, settings, print: true);
            var question = $"{status.Name} {Describe(status)}. Connect it?";
            if (print)
            {
                say.Info($"{question[..^"Connect it?".Length].TrimEnd(' ', '.')}.");
                continue;
            }
            if (!yes && (ask is null || !ask($"{question} [Y/n] ")))
            {
                declined++;
                say.Info($"{(ask is null ? question[..^"Connect it?".Length].TrimEnd(' ', '.') + ": left" : "Left")} " +
                         $"as it is; `copilotscope connect {target}` does it later.");
                continue;
            }
            Connect(target, settings, print: false);
        }

        say.Line();
        say.Info("Claude Cowork is configured in the Claude desktop app: `copilotscope connect cowork` says what to enter.");
        if (print && offered > 0)
            say.Info("--print: nothing was changed. `copilotscope setup` asks about each of the above.");
        else if (offered > 0 && declined == offered && ask is null && !yes)
            say.Info("Nothing was changed without a terminal to ask in. `copilotscope setup --yes` connects all of the above.");
        return 0;
    }

    private static string Describe(AssistantStatus status) => status.Connection switch
    {
        Connection.NotConnected => "is installed and sends no telemetry",
        Connection.Elsewhere => $"sends its telemetry to {status.Detail}",
        Connection.Incomplete => $"points here, but {status.Detail}",
        Connection.Unreadable => $"has settings that are not strict JSON ({status.Where})",
        _ => status.Connection.ToString()
    };

    private EditResult? Edit(string file, Func<EditResult> edit)
    {
        try { return edit(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            say.Bad($"could not write {file}: {ex.Message}");
            return null;
        }
    }
}
