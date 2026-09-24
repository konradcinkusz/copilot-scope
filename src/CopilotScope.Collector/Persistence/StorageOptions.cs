namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Where session snapshots live (<c>CopilotScope:Storage</c>, ADR-004).
///
/// <list type="bullet">
/// <item><c>auto</c>, the default: Postgres when <c>ConnectionStrings:copilotdb</c> is set,
/// otherwise local files when <see cref="Path"/> is set, otherwise memory only. Every
/// deployment that configured nothing new keeps doing exactly what it did.</item>
/// <item><c>postgres</c>: sessions in Postgres; the connection string is required.</item>
/// <item><c>files</c>: one JSON file per session under <see cref="Path"/>, or
/// <c>~/.copilotscope/data</c> when it is unset. Durable on a single machine, no database.</item>
/// <item><c>memory</c>: nothing survives a restart.</item>
/// </list>
///
/// This chooses where <em>sessions</em> are kept, and nothing else. Labels, outcome linkage,
/// the vendor-metrics archive and the durable access audit are team features that need
/// Postgres, and they follow the connection string whatever the mode says.
/// </summary>
public sealed class StorageOptions
{
    public string Mode { get; set; } = "auto";

    /// <summary>Data directory for file storage. A leading <c>~</c> means the home directory.</summary>
    public string? Path { get; set; }
}

public enum StorageKind { Memory, Postgres, Files }

/// <summary>The storage a collector actually runs with, resolved once at startup.</summary>
public sealed record StoragePlan(StorageKind Kind, string? Directory = null)
{
    /// <summary>What <c>/api/health</c> reports: <c>memory</c>, <c>postgres</c> or <c>files</c>.</summary>
    public string Name => Kind.ToString().ToLowerInvariant();

    public bool Durable => Kind != StorageKind.Memory;

    /// <summary>
    /// Resolves the configured mode. A mode that cannot be honoured stops startup instead of
    /// falling back: a collector asked for durable storage that quietly ran in memory would
    /// lose the history it was configured to keep, and say so only in a log line.
    /// </summary>
    public static StoragePlan Resolve(StorageOptions options, string? connectionString)
    {
        var mode = string.IsNullOrWhiteSpace(options.Mode) ? "auto" : options.Mode.Trim().ToLowerInvariant();
        var hasPostgres = !string.IsNullOrWhiteSpace(connectionString);
        var hasPath = !string.IsNullOrWhiteSpace(options.Path);

        return mode switch
        {
            "auto" when hasPostgres => new(StorageKind.Postgres),
            "auto" when hasPath => new(StorageKind.Files, ExpandPath(options.Path!)),
            "auto" => new(StorageKind.Memory),
            "postgres" when hasPostgres => new(StorageKind.Postgres),
            "postgres" => throw new InvalidOperationException(
                "CopilotScope:Storage:Mode is 'postgres' but ConnectionStrings:copilotdb is empty."),
            "files" => new(StorageKind.Files, ExpandPath(hasPath ? options.Path! : DefaultDirectory())),
            "memory" => new(StorageKind.Memory),
            _ => throw new InvalidOperationException(
                $"CopilotScope:Storage:Mode '{options.Mode}' is not one of: auto, postgres, files, memory.")
        };
    }

    /// <summary><c>~/.copilotscope/data</c>: the directory the native binary uses (ADR-004).</summary>
    public static string DefaultDirectory() => System.IO.Path.Combine(Home(), ".copilotscope", "data");

    private static string ExpandPath(string path)
    {
        path = path.Trim();
        if (path == "~") path = Home();
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = System.IO.Path.Combine(Home(), path[2..]);
        return System.IO.Path.GetFullPath(path);
    }

    private static string Home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home)
            ? throw new InvalidOperationException(
                "File storage needs a directory, and this process has no home directory to default to. " +
                "Set CopilotScope:Storage:Path.")
            : home;
    }
}
