using System.Text.RegularExpressions;

namespace CopilotScope.Local.Capturing;

/// <summary>What would identify the person or the machine a capture came from.</summary>
internal sealed record Identity(string Home, string? UserName, string? MachineName, string? GitEmail, string? GitName)
{
    /// <summary>This machine's, with the git identity read from git's own config files rather
    /// than by running git.</summary>
    public static Identity Current(string home)
    {
        string? email = null, name = null;
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        foreach (var file in new[] { Path.Combine(home, ".gitconfig"), Path.Combine(xdg, "git", "config") })
        {
            if (!File.Exists(file)) continue;
            var inUser = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inUser = line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inUser || line.IndexOf('=') is not (> 0 and var eq)) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim().Trim('"');
                if (key.Equals("email", StringComparison.OrdinalIgnoreCase)) email ??= value;
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name ??= value;
            }
        }
        return new Identity(home, Environment.UserName, Environment.MachineName, email, name);
    }
}

/// <summary>
/// The last check before anything is written: redaction is meant to leave nothing that could
/// identify someone, and this looks for what would, in the redacted output. Any hit refuses the
/// whole capture — there is no override, because the one time it is used in a hurry is the time
/// it matters. Hits are named by kind, never by value: the report of a leak must not leak.
/// </summary>
internal static partial class LeakScan
{
    // Names too generic to search for: they are also ordinary words a structural field keeps
    // ("role": "user"), and a machine account called "runner" identifies no one.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "users", "root", "admin", "administrator", "runner", "dev", "developer", "test", "guest",
        "localhost", "default", "ubuntu", "vscode", "codespace", "codespaces", "home"
    };

    [GeneratedRegex(@"gh[oprsu]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{16,}|sk-[A-Za-z0-9]{32,}|AIza[0-9A-Za-z_\-]{35}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.")]
    private static partial Regex SecretBlock();

    public static IReadOnlyList<string> Find(string text, Identity identity)
    {
        var found = new List<string>();
        void Check(string? value, string what, int minimum, bool word = false)
        {
            if (value is null || value.Length < minimum || Generic.Contains(value)) return;
            // A one-word name is looked for as a word of its own: the same letters inside an
            // identifier ("claude-opus-5", "service.name=claude-code") are a model or a product,
            // and refusing every capture of someone who shares a name with one helps nobody.
            var hit = word && !value.Any(char.IsWhiteSpace)
                ? Regex.IsMatch(text, $@"(?<![\w.\-/]){Regex.Escape(value)}(?![\w.\-/])", RegexOptions.IgnoreCase)
                : text.Contains(value, StringComparison.OrdinalIgnoreCase);
            if (hit) found.Add(what);
        }

        Check(identity.Home, "your home directory", 4);
        Check(identity.Home.Replace('\\', '/'), "your home directory", 4);
        Check(identity.UserName, "your user name", 4, word: true);
        Check(identity.MachineName, "this machine's name", 4, word: true);
        Check(identity.GitEmail, "your git e-mail address", 6);
        Check(identity.GitName, "your git name", 4, word: true);
        if (GitHubToken().IsMatch(text)) found.Add("a GitHub token");
        if (ApiKey().IsMatch(text)) found.Add("an API key");
        if (SecretBlock().IsMatch(text)) found.Add("a private key or a signed token");
        return found.Distinct().ToList();
    }
}
