using System.Diagnostics;
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
    /// </summary>
    public static string? RepositoryFor(string cwd, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(cwd, out var cached)) return cached;

        string? resolved = null;
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "remote get-url origin")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (git is not null)
            {
                var url = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit(5000);
                if (git.ExitCode == 0 && url.Length > 0)
                    resolved = OutcomeLinker.NormalizeRepository(url);
            }
        }
        catch (Exception) { /* no git, not a repo, or a directory that no longer exists */ }

        // Falling back to the directory name would invent a second cohort for a repository the
        // collector already knows by its remote. A missing label is the honest answer.
        cache[cwd] = resolved;
        return resolved;
    }
}
