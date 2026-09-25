namespace CopilotScope.Local.Connecting;

internal enum Os { Windows, MacOS, Linux }

/// <summary>
/// Where this machine's assistants keep their settings — the same places the control scripts
/// look (<c>scripts/copilotscope</c>, <c>scripts/copilotscope.ps1</c>). Read from the
/// environment once, so every command sees the same answers, and constructed directly by tests.
/// </summary>
internal sealed record Machine(
    string Home,
    Os Os,
    string? ClaudeConfigDir = null,
    string? AppData = null,
    string? XdgConfigHome = null,
    string? Shell = null,
    string? PathVariable = null)
{
    public static Machine Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.MacOS : Os.Linux,
        Variable("CLAUDE_CONFIG_DIR"), Variable("APPDATA"), Variable("XDG_CONFIG_HOME"), Variable("SHELL"),
        Variable("PATH"));

    private static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>Claude Code's own directory: <c>$CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>.</summary>
    public string ClaudeDirectory => ClaudeConfigDir ?? Path.Combine(Home, ".claude");

    /// <summary>User-level settings: they apply in every terminal, every project, and to Claude Code
    /// inside VS Code — which is why connecting writes here and not to a shell profile.</summary>
    public string ClaudeSettings => Path.Combine(ClaudeDirectory, "settings.json");

    /// <summary>
    /// VS Code's user settings: the first that exists for Code, Code - Insiders and VSCodium, or
    /// failing that the first whose folder exists (installed, never configured). Null when none
    /// is installed.
    /// </summary>
    public string? VsCodeSettings()
    {
        var root = Os switch
        {
            Os.Windows => AppData ?? Path.Combine(Home, "AppData", "Roaming"),
            Os.MacOS => Path.Combine(Home, "Library", "Application Support"),
            _ => XdgConfigHome ?? Path.Combine(Home, ".config")
        };
        var candidates = new[] { "Code", "Code - Insiders", "VSCodium" }
            .Select(editor => Path.Combine(root, editor, "User", "settings.json")).ToList();
        return candidates.FirstOrDefault(File.Exists)
               ?? candidates.FirstOrDefault(candidate => Directory.Exists(Path.GetDirectoryName(candidate)));
    }

    /// <summary>The start-up file of the user's shell, where Copilot CLI's variables go on macOS
    /// and Linux (it reads nothing else), and whether it speaks fish rather than POSIX sh.</summary>
    public (string Path, bool Fish) ShellProfile => Path.GetFileName(Shell ?? "bash") switch
    {
        "zsh" => (Path.Combine(Home, ".zshrc"), false),
        "fish" => (Path.Combine(XdgConfigHome ?? Path.Combine(Home, ".config"), "fish", "config.fish"), true),
        _ => (Path.Combine(Home, ".bashrc"), false)
    };

    /// <summary>Whether a program of that name is on the PATH.</summary>
    public bool OnPath(string program)
    {
        if (PathVariable is null) return false;
        string[] extensions = Os == Os.Windows ? [".exe", ".cmd", ".bat", ""] : [""];
        return PathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => extensions.Any(extension =>
            {
                try { return File.Exists(Path.Combine(directory, program + extension)); }
                catch (ArgumentException) { return false; } // a PATH entry with characters no path may hold
            }));
    }
}

/// <summary>User-scope environment variables: where Copilot CLI's settings go on Windows, which
/// has no shell profile every terminal reads.</summary>
internal interface IUserEnvironment
{
    string? Get(string name);
    void Set(string name, string? value);
}

internal sealed class WindowsUserEnvironment : IUserEnvironment
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public void Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        // This process too, as the PowerShell script does for its own session.
        Environment.SetEnvironmentVariable(name, value);
    }
}
