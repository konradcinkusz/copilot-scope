using CopilotScope.Collector.Outcomes;

namespace CopilotScope.Collector.Import;

/// <summary>Repository labels for sessions reconstructed from files on this machine.</summary>
public static class GitRemote
{
    /// <summary>
    /// The repository label for a working directory.
    ///
    /// This is the one thing the importer can do that the OTel path cannot help with: it runs
    /// on the developer's machine, where the repository is checked out, so it can read the
    /// actual git remote and normalize it exactly the way outcome linkage does. Without that
    /// an imported session would be labelled with a bare directory name and would form its own
    /// cohort next to the live sessions from the same repository — two rows for one project,
    /// which is worse than no label.
    ///
    /// The remote is read from the repository's config file, not by running <c>git</c>. The
    /// native binary's scanner calls this unattended, for every project in someone's history:
    /// there, git may not be installed, a process per project is the wrong cost, and on a Mac
    /// without the developer tools <c>/usr/bin/git</c> answers by opening an installer dialog.
    /// </summary>
    public static string? RepositoryFor(string cwd, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(cwd, out var cached)) return cached;

        string? resolved = null;
        try
        {
            if (ConfigFileFor(cwd) is { } config && OriginUrl(config) is { } url)
                resolved = OutcomeLinker.NormalizeRepository(url);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            // Unreadable, or a path this platform cannot express: no label, as with no remote.
        }

        // Falling back to the directory name would invent a second cohort for a repository the
        // collector already knows by its remote. A missing label is the honest answer.
        cache[cwd] = resolved;
        return resolved;
    }

    /// <summary>
    /// The config file of the repository holding the directory, found the way git finds it:
    /// the nearest <c>.git</c> at or above it. That is a directory, or — in a linked worktree
    /// or a submodule — a file naming the real one. Only for a directory that exists: a
    /// transcript can name a project deleted since, or a path from another operating system,
    /// which read as a relative path would be resolved against this process's own directory.
    /// </summary>
    internal static string? ConfigFileFor(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return null;

        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit)) return ConfigIn(dotGit);
            if (File.Exists(dotGit))
                return GitDirNamedBy(dotGit, dir.FullName) is { } gitDir ? ConfigIn(gitDir) : null;
        }
        return null;
    }

    /// <summary>A linked worktree's git directory holds a <c>commondir</c> file naming the main
    /// one, and the remotes are configured there.</summary>
    private static string? ConfigIn(string gitDir)
    {
        var commonDir = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDir) && File.ReadAllText(commonDir).Trim() is { Length: > 0 } common)
            gitDir = Path.GetFullPath(Path.Combine(gitDir, common));

        var config = Path.Combine(gitDir, "config");
        return File.Exists(config) ? config : null;
    }

    private static string? GitDirNamedBy(string dotGitFile, string workTree)
    {
        foreach (var line in File.ReadLines(dotGitFile))
        {
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) continue;
            var path = line["gitdir:".Length..].Trim();
            return path.Length == 0 ? null : Path.GetFullPath(Path.Combine(workTree, path));
        }
        return null;
    }

    /// <summary>
    /// The <c>url</c> of <c>[remote "origin"]</c>, from a git config file. Only the part of the
    /// format a remote uses: sections, <c>key = value</c> lines, comments and quoted values.
    /// Include directives and <c>insteadOf</c> rewrites are not followed; the label keeps only
    /// the owner and name, which a rewrite of the host does not change.
    /// </summary>
    internal static string? OriginUrl(string configFile)
    {
        var inOrigin = false;
        foreach (var raw in File.ReadLines(configFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                inOrigin = close > 0 && IsOrigin(line[1..close]);
                if (close < 0 || line[(close + 1)..].Trim() is not { Length: > 0 } rest) continue;
                line = rest; // a key on the same line as its section header
            }
            if (!inOrigin) continue;

            var eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
            if (Value(line[(eq + 1)..]) is { Length: > 0 } url) return url;
        }
        return null;
    }

    /// <summary><c>remote "origin"</c>, or the older <c>remote.origin</c>. Section names ignore
    /// case; a quoted subsection name does not.</summary>
    private static bool IsOrigin(string section)
    {
        section = section.Trim();
        var quote = section.IndexOf('"');
        if (quote < 0) return section.Equals("remote.origin", StringComparison.OrdinalIgnoreCase);
        return section[..quote].Trim().Equals("remote", StringComparison.OrdinalIgnoreCase)
            && section[(quote + 1)..] == "origin\"";
    }

    /// <summary>A value with its trailing comment removed and its quotes and escapes undone.</summary>
    private static string Value(string text)
    {
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c is '#' or ';') break;
            if (c == '\\' && i + 1 < text.Length) c = text[++i] switch { 'n' => '\n', 't' => '\t', var other => other };
            value.Append(c);
        }
        return value.ToString().Trim();
    }
}
