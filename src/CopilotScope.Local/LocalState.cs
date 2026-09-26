using System.Text.Json;

namespace CopilotScope.Local;

/// <summary>
/// Where the native binary keeps its state: <c>~/.copilotscope</c>, the directory the installers
/// already use, or <c>COPILOTSCOPE_HOME</c> when set.
/// </summary>
internal sealed record LocalPaths(string Home)
{
    public static LocalPaths Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("COPILOTSCOPE_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return new LocalPaths(Path.GetFullPath(configured));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            throw new InvalidOperationException("No home directory to keep CopilotScope's data in. Set COPILOTSCOPE_HOME.");
        return new LocalPaths(Path.Combine(home, ".copilotscope"));
    }

    /// <summary>One JSON file per session (the file store of ADR-004).</summary>
    public string Data => Path.Combine(Home, "data");

    /// <summary>Function runs: each one's task, pack, agents and report (docs/FUNCTIONS.md).</summary>
    public string Runs => Path.Combine(Home, "runs");

    /// <summary>What only a running process needs: which ports it took and how to stop it.</summary>
    public string Run => Path.Combine(Home, "run");

    public string InstanceFile => Path.Combine(Run, "instance.json");

    public void CreatePrivate(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>
/// The running instance, as <c>status</c>, <c>stop</c> and <c>open</c> find it. The token proves
/// that whatever answers on the recorded port is this instance and not a process that took the
/// port — or the process id — after this one exited; it is also what <c>stop</c> presents, so
/// the file is readable by its owner only.
/// </summary>
internal sealed record Instance(
    int ProcessId, int OtlpPort, int DashboardPort, string Token, string Version,
    DateTimeOffset StartedAt, string Storage)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string CollectorUrl => $"http://localhost:{OtlpPort}";

    public string DashboardUrl => $"http://localhost:{DashboardPort}";

    public static Instance? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Instance>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable is as good as absent: the next start rewrites it.
            return null;
        }
    }

    public void Write(string path)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
            JsonSerializer.Serialize(stream, this, Json);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Removes the file, but only if it still describes this instance: a second start
    /// that raced this one's shutdown must keep its own record.</summary>
    public static void Delete(string path, string token)
    {
        try
        {
            if (Read(path)?.Token == token) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
