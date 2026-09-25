using CopilotScope.Collector.Import;
using CopilotScope.Local.Connecting;

namespace CopilotScope.Local.Capturing;

/// <summary>One file of an assistant's history, and what it is in that assistant's layout.</summary>
internal sealed record HistoryFile(string Path, string Role, DateTimeOffset LastWrite, long Length);

/// <summary>
/// Where each assistant keeps its chat history on this machine. Claude Code's layout is known —
/// there is a reader for it. VS Code Copilot Chat's and Copilot CLI's are where they are
/// believed to be, and not yet confirmed from a real capture (ADR-004, decision 5): this finds
/// the files a capture would take and a report would count, and reads nothing out of them.
/// </summary>
internal sealed class LocalHistory(Machine machine)
{
    public static readonly string[] Sources = ["claude-code", "vscode", "copilot-cli"];

    public static string DisplayName(string source) => source switch
    {
        "claude-code" => "Claude Code",
        "vscode" => "VS Code Copilot Chat",
        "copilot-cli" => "GitHub Copilot CLI",
        _ => source
    };

    /// <summary>A source's name from the spellings `connect` accepts too.</summary>
    public static string? Normalize(string source) => Connector.Normalize(source) is { } s && Sources.Contains(s) ? s : null;

    /// <summary>The directories a source's history lives under.</summary>
    public IReadOnlyList<string> Roots(string source) => source switch
    {
        "claude-code" => machine.ClaudeConfigDir is { } configured
            ? [Path.Combine(configured, "projects")]
            : [Path.Combine(machine.Home, ".claude", "projects"), Path.Combine(machine.Home, ".config", "claude", "projects")],
        "vscode" => machine.VsCodeUserDirectories(),
        "copilot-cli" => [Path.Combine(machine.Home, ".copilot")],
        _ => []
    };

    /// <summary>Where the source was looked for, for a message saying nothing was there.</summary>
    public string Searched(string source) => Roots(source) is { Count: > 0 } roots
        ? string.Join(" or ", roots)
        : source == "vscode" ? "any VS Code settings folder (Code, Code - Insiders, VSCodium)" : "its usual folders";

    /// <summary>Every history file of the source, newest first.</summary>
    public IReadOnlyList<HistoryFile> Files(string source)
    {
        var files = new List<HistoryFile>();
        foreach (var root in Roots(source).Where(Directory.Exists))
        {
            foreach (var (path, role) in Candidates(source, root))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) files.Add(new HistoryFile(path, role, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return files.OrderByDescending(f => f.LastWrite).ToList();
    }

    private static IEnumerable<(string Path, string Role)> Candidates(string source, string root)
    {
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        switch (source)
        {
            case "claude-code":
                foreach (var file in ClaudeCodeFiles.Discover(root))
                    yield return (file, file.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}") ? "subagent" : "transcript");
                break;

            case "vscode":
                // One folder per workspace, each with its chat sessions; and one for windows with
                // no folder open.
                var workspaces = Path.Combine(root, "workspaceStorage");
                if (Directory.Exists(workspaces))
                    foreach (var workspace in Directory.EnumerateDirectories(workspaces))
                    foreach (var file in Json(Path.Combine(workspace, "chatSessions"), walk))
                        yield return (file, "chatSessions");
                foreach (var file in Json(Path.Combine(root, "globalStorage", "emptyWindowChatSessions"), walk))
                    yield return (file, "emptyWindowChatSessions");
                break;

            case "copilot-cli":
                // Session state, wherever under ~/.copilot it turns out to be. Never its
                // configuration, which is where a sign-in would be kept.
                foreach (var file in Json(root, walk))
                {
                    var relative = Path.GetRelativePath(root, file);
                    var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    if (relative == top) continue; // files directly in ~/.copilot: configuration
                    if (IsSecretLooking(relative)) continue;
                    yield return (file, top);
                }
                break;
        }
    }

    private static IEnumerable<string> Json(string directory, EnumerationOptions walk) =>
        !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "*", walk)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));

    private static bool IsSecretLooking(string relative) =>
        new[] { "config", "auth", "token", "credential", "secret", "key" }
            .Any(word => relative.Contains(word, StringComparison.OrdinalIgnoreCase));
}
