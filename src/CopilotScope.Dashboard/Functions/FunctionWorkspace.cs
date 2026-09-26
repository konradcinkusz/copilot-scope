using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotScope.Dashboard.Functions;

/// <summary>The assistant a function runs on. Both are driven headlessly on the user's own login.</summary>
public enum FunctionAssistant
{
    ClaudeCode,
    CopilotCli
}

/// <summary>One file of a run's working directory, path relative to it, always with forward slashes.</summary>
public sealed record WorkspaceFile(string Path, string Content);

/// <summary>What a run is built from: the function, the pack the collector served, and who asked.</summary>
/// <param name="PackMarkdown">The pack as <c>format=markdown</c>: what the assistant reads whole.</param>
/// <param name="PackJson">The pack as JSON: what the assistant looks cited sessions up in.</param>
/// <param name="Tier">The tier the collector actually served — <c>sessions</c>, or <c>aggregate</c> when
/// the sessions tier was refused.</param>
public sealed record WorkspaceInput(
    AssistantFunction Function,
    string RunId,
    int Days,
    string Tier,
    string PackMarkdown,
    string PackJson);

/// <summary>A program and its arguments, exactly as they are started — never through a shell.</summary>
public sealed record LaunchCommand(string Program, IReadOnlyList<string> Arguments)
{
    /// <summary>The command as a person would type it, for the consent screen and the kit's README.</summary>
    public string Display => string.Join(' ', new[] { Program }.Concat(Arguments).Select(Quote));

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ',' or '=' or ':')
            ? argument
            : "'" + argument.Replace("'", "'\\''") + "'";
}

/// <summary>
/// Everything a function run consists of, as files and one command — computed here, as a pure
/// function, so the same bytes are what the native binary launches, what the consent screen lists
/// and what the kit download carries.
///
/// <para>Nothing complex travels on a command line. The task, the agents and the settings are
/// files in the run's directory, and the arguments are fixed words and relative paths: on Windows
/// an npm-installed assistant is a <c>.cmd</c> shim, whose arguments <c>cmd.exe</c> re-parses, and
/// a JSON object on that command line is an injection waiting for a quote.</para>
///
/// <para>The flags were checked against Claude Code 2.1.283 and GitHub Copilot CLI 1.0.88 (their
/// <c>--help</c>, and for Claude Code the tools and agents its <c>init</c> message reports). Each
/// one earns its place; the comments beside them say how.</para>
/// </summary>
public static class FunctionWorkspace
{
    /// <summary>What both assistants are asked on the command line. Fixed, and free of anything a
    /// shell or <c>cmd.exe</c> would interpret.</summary>
    public const string Prompt = "Read TASK.md in the current directory and do what it says. Reply with the report only.";

    public const string TaskFile = "TASK.md";
    public const string PackMarkdownFile = "pack.md";
    public const string PackJsonFile = "pack.json";
    public const string ClaudeAgentsFile = "claude/agents.json";
    public const string ClaudeSettingsFile = "claude/settings.json";
    public const string ReportFile = "report.md";

    /// <summary>The resource attribute the collector drops a run's telemetry by (the Collector's
    /// <c>ObserverRegistry.MarkerAttribute</c>; the dashboard takes no reference to the Collector,
    /// and a test holds the two spellings together).</summary>
    public const string ObserverMarker = "copilotscope.observer";

    /// <summary>The environment assignment that marks a run's telemetry, as a POSIX shell spells it.</summary>
    public const string ObserverMarkerAssignment = $"OTEL_RESOURCE_ATTRIBUTES={ObserverMarker}=true";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DisplayName(FunctionAssistant assistant) => assistant switch
    {
        FunctionAssistant.ClaudeCode => "Claude Code",
        _ => "GitHub Copilot CLI"
    };

    /// <summary>Who receives what the assistant reads, for the consent screen.</summary>
    public static string Vendor(FunctionAssistant assistant) => assistant switch
    {
        FunctionAssistant.ClaudeCode => "Anthropic",
        _ => "GitHub"
    };

    /// <summary>The run's files, in the order a person would want to read them.</summary>
    public static IReadOnlyList<WorkspaceFile> Files(WorkspaceInput input)
    {
        var files = new List<WorkspaceFile>
        {
            new(TaskFile, Task(input)),
            new(PackMarkdownFile, input.PackMarkdown),
            new(PackJsonFile, input.PackJson)
        };

        // Copilot (CLI and VS Code) reads custom agents from .github/agents; Claude Code is handed
        // the same agents as one JSON file. One definition, two spellings.
        foreach (var agent in input.Function.Agents)
            files.Add(new($".github/agents/{agent.Name}.agent.md", CopilotAgent(agent)));
        if (input.Function.MultiAgent)
            files.Add(new(ClaudeAgentsFile, ClaudeAgents(input.Function)));
        files.Add(new(ClaudeSettingsFile, ClaudeSettings()));

        // VS Code Copilot Chat cannot be driven headlessly: it gets a prompt file to run by hand.
        files.Add(new($".github/prompts/copilotscope-{input.Function.Id}.prompt.md", VsCodePrompt(input.Function)));
        files.Add(new("README.md", Readme(input)));
        return files;
    }

    /// <summary>The lead's task: the function's instructions, the roster, the rules, the output contract.</summary>
    public static string Task(WorkspaceInput input)
    {
        var f = input.Function;
        var sb = new StringBuilder();
        sb.Append($"# CopilotScope function: {f.Title}\n\n");
        sb.Append("You are running a CopilotScope function on this machine, on the user's own subscription. ")
          .Append("CopilotScope provides no model: it counted the numbers in the review pack, and you read them ")
          .Append("and write.\n\n");
        sb.Append($"- Run `{input.RunId}` · the last {input.Days} day(s) · pack tier `{input.Tier}`\n");
        sb.Append($"- `{PackMarkdownFile}` — the review pack. Read it whole before anything else.\n");
        sb.Append($"- `{PackJsonFile}` — the same pack as JSON. Search it for the sessions you cite.\n");
        if (input.Tier != "sessions")
            sb.Append("- This pack is the aggregate tier: it carries no session ids. Cite pattern ids and pack sections ")
              .Append("instead, and say that per-session evidence was not available.\n");

        if (f.MultiAgent)
        {
            sb.Append("\n## Agents you can dispatch\n\n");
            sb.Append("Each is a subagent with its own context, defined in this directory. Dispatch them by name; ")
              .Append("where the task says in parallel, dispatch them all in one step.\n\n");
            foreach (var agent in f.Agents)
                sb.Append($"- `{agent.Name}` — {agent.Title}: {agent.Role}\n");
        }

        sb.Append("\n## Your task\n\n").Append(f.Lead.Trim()).Append("\n\n");
        sb.Append("## Output\n\n");
        sb.Append($"Reply with the report in Markdown and nothing else — no preamble, no sign-off. Begin with ")
          .Append($"`# {f.Title}`, and under it a line giving the window and the pack's fingerprint. ")
          .Append("CopilotScope saves your reply as `report.md` beside the pack, under a header saying which ")
          .Append("parts it computed and which you wrote.\n\n");
        sb.Append(FunctionCatalog.SharedRules.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>A Copilot custom agent profile. <c>read</c> and <c>search</c> are the documented tool
    /// aliases for viewing and searching files; nothing that edits, runs or fetches is listed.</summary>
    public static string CopilotAgent(FunctionAgent agent) =>
        "---\n" +
        $"name: {agent.Name}\n" +
        $"description: {JsonSerializer.Serialize(agent.Role, Json)}\n" +
        "tools: [\"read\", \"search\"]\n" +
        "---\n\n" +
        agent.Instructions.Trim() + "\n\n" +
        FunctionCatalog.SharedRules.Trim() + "\n";

    /// <summary>The function's agents as Claude Code's <c>--agents</c> JSON: read-only tools each.</summary>
    public static string ClaudeAgents(AssistantFunction function)
    {
        var agents = new JsonObject();
        foreach (var agent in function.Agents)
            agents[agent.Name] = new JsonObject
            {
                ["description"] = agent.Role,
                ["prompt"] = agent.Instructions.Trim() + "\n\n" + FunctionCatalog.SharedRules.Trim(),
                ["tools"] = new JsonArray("Read", "Grep", "Glob")
            };
        return agents.ToJsonString(Json) + "\n";
    }

    /// <summary>
    /// Settings Claude Code is launched with. <c>--restricted</c> ignores the user's settings files —
    /// including the telemetry <c>copilotscope connect</c> wrote there — and these switch it off
    /// explicitly, with the marker set in case a managed policy switches it back on.
    /// </summary>
    public static string ClaudeSettings() =>
        new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["CLAUDE_CODE_ENABLE_TELEMETRY"] = "0",
                ["OTEL_RESOURCE_ATTRIBUTES"] = $"{ObserverMarker}=true"
            }
        }.ToJsonString(Json) + "\n";

    /// <summary>A VS Code prompt file: run it from Copilot Chat in agent mode with this folder open.</summary>
    public static string VsCodePrompt(AssistantFunction function) =>
        "---\n" +
        $"description: {JsonSerializer.Serialize($"CopilotScope: {function.Title}", Json)}\n" +
        "agent: agent\n" +
        (function.MultiAgent ? "tools: [\"read\", \"search\", \"agent\"]\n" : "tools: [\"read\", \"search\"]\n") +
        "---\n\n" +
        $"Read [TASK.md](../../{TaskFile}) and do what it says, using [pack.md](../../{PackMarkdownFile}) and " +
        $"[pack.json](../../{PackJsonFile})." +
        (function.MultiAgent
            ? " The agents it names are the custom agents in `.github/agents`; run them as subagents."
            : "") +
        " Reply with the report in Markdown.\n";

    /// <summary>
    /// The command a function is launched with. <paramref name="sessionId"/> is chosen by CopilotScope
    /// and registered as an observer before the process starts, so nothing the run sends is scored;
    /// null for a kit run by hand, which is kept out of the scores by its marker instead.
    /// </summary>
    public static LaunchCommand Command(FunctionAssistant assistant, AssistantFunction function, string? sessionId,
        string program = "") => assistant switch
    {
        FunctionAssistant.ClaudeCode => new(program.Length > 0 ? program : "claude", ClaudeArguments(function, sessionId)),
        _ => new(program.Length > 0 ? program : "copilot", CopilotArguments(function, sessionId))
    };

    private static List<string> ClaudeArguments(AssistantFunction function, string? sessionId)
    {
        var args = new List<string>
        {
            "-p", Prompt,
            // A single JSON result: the report, and whether the run ended in an error.
            "--output-format", "json"
        };
        if (sessionId is not null) args.AddRange(["--session-id", sessionId]);
        args.AddRange(
        [
            // No command execution and no WebFetch; user, project and local settings files ignored;
            // file tools confined to this directory. Never --bare, which skips OAuth and would move
            // the run off the subscription, and never --add-dir, which would widen the confinement.
            "--restricted",
            // Read-only tools; Task only where there are subagents to dispatch.
            "--tools", function.MultiAgent ? "Read,Grep,Glob,Task" : "Read,Grep,Glob"
        ]);
        if (function.MultiAgent) args.AddRange(["--agents", ClaudeAgentsFile]);
        args.AddRange(
        [
            "--strict-mcp-config",          // no MCP servers — including CopilotScope's own
            "--no-session-persistence",     // no transcript for the scanner to import
            "--permission-prompts", "none", // anything that would ask is refused, not waited on
            "--disable-slash-commands",     // the user's skills stay out of it
            "--settings", ClaudeSettingsFile
        ]);
        return args;
    }

    private static List<string> CopilotArguments(AssistantFunction function, string? sessionId)
    {
        var args = new List<string>
        {
            "-p", Prompt,
            "-s"                            // the agent's reply only, no statistics
        };
        if (sessionId is not null) args.AddRange(["--session-id", sessionId]);
        // Only these tools exist for the model; everything that edits, runs or fetches is absent.
        args.Add("--available-tools");
        args.AddRange(function.MultiAgent
            ? ["view", "glob", "grep", "task", "read_agent", "list_agents"]
            : ["view", "glob", "grep"]);
        args.AddRange(
        [
            // Non-interactive mode needs tools pre-approved; with only the read tools above available,
            // that approves reading. The denials win over it regardless.
            "--allow-all-tools",
            "--deny-tool", "shell",
            "--deny-tool", "write",
            "--deny-tool", "url",
            "--disallow-temp-dir",          // this directory only
            "--disable-builtin-mcps",       // no GitHub MCP server
            "--no-custom-instructions",     // the user's AGENTS.md is about their code, not this
            "--no-ask-user",
            "--no-auto-update",
            "--no-color"
        ]);
        if (function.MultiAgent)
            // Fleet mode: Copilot's orchestrator for parallel subagents. The run directory is added
            // so its .github/agents load as trusted configuration outside a git repository.
            args.AddRange(["--fleet", "--add-dir", "."]);
        return args;
    }

    /// <summary>The top of <c>report.md</c>: which parts CopilotScope computed and which the assistant wrote.</summary>
    public static string ReportHeader(AssistantFunction function, FunctionAssistant assistant, string runId,
        DateTimeOffset finished) =>
        $"<!-- CopilotScope function run {runId} -->\n" +
        $"> **{function.Title}** · run `{runId}` · {finished:yyyy-MM-dd HH:mm} UTC\n" +
        $"> **Computed by CopilotScope:** every figure in `{PackMarkdownFile}` and `{PackJsonFile}` beside this file.\n" +
        $"> **Written by {DisplayName(assistant)}:** everything below this header. Its claims cite the pack; " +
        "check them there. *Certainty* below is the assistant's own rating, not a score's confidence.\n\n";

    /// <summary>The kit's README: what is here, and how to run it on each assistant by hand.</summary>
    public static string Readme(WorkspaceInput input)
    {
        var f = input.Function;
        return $"""
            # CopilotScope function kit: {f.Title}

            {f.Summary}

            CopilotScope provides no model. This folder holds a task, the review pack CopilotScope counted
            (window: the last {input.Days} day(s), tier `{input.Tier}`), and{(f.MultiAgent ? " the agents the task dispatches, in each assistant's format" : " nothing else")}.
            Run it on the assistant you already use. Whatever the assistant reads here is sent to its vendor
            under your subscription's terms; nothing in this folder contains prompt, response or tool-argument
            text.

            ## Files

            - `{TaskFile}` — the task, with the rules every claim is held to.
            - `{PackMarkdownFile}`, `{PackJsonFile}` — the review pack (docs/REVIEW.md).
            {(f.MultiAgent ? $"- `.github/agents/*.agent.md` — the agents, for Copilot (CLI and VS Code).\n- `{ClaudeAgentsFile}` — the same agents, for Claude Code.\n" : "")}- `{ClaudeSettingsFile}` — telemetry off for the run, so it is not scored as a session of yours.
            - `.github/prompts/copilotscope-{f.Id}.prompt.md` — a prompt file for VS Code Copilot Chat.

            ## Run it

            Open a terminal in this folder.

            Claude Code (telemetry is switched off by `{ClaudeSettingsFile}`, and no transcript is kept):

                {Command(FunctionAssistant.ClaudeCode, f, null).Display}

            GitHub Copilot CLI (the variable marks any telemetry the run sends, so CopilotScope drops it):

                {ObserverMarkerAssignment} {Command(FunctionAssistant.CopilotCli, f, null).Display}

            VS Code: open this folder, open Copilot Chat in agent mode, and run `/copilotscope-{f.Id}`.

            Save the reply as `{ReportFile}` beside the pack. The native `copilotscope` binary does all of
            this from its dashboard's Functions page, and keeps the run out of your scores.
            """.Replace("\r\n", "\n") + "\n";
    }
}
