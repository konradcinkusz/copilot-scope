using System.Security.Cryptography;
using System.Text;
using CopilotScope.Collector.Persistence;

namespace CopilotScope.Local.Scanning;

/// <summary>
/// One assistant's history on this machine, as the files it keeps (ADR-004, decision 4). A
/// source finds sessions and reads them; deciding when, remembering what was read and handing
/// sessions to the collector is the <see cref="Scanner"/>'s job, the same for every source.
/// </summary>
internal interface IScanSource
{
    /// <summary>Stable key, used in the scan state: <c>claude-code</c>.</summary>
    string Name { get; }

    /// <summary>For people: <c>Claude Code</c>.</summary>
    string DisplayName { get; }

    /// <summary>The version of the reader. When it changes, every session from this source is
    /// read again, so a parser fix reaches history imported before it.</summary>
    int Version { get; }

    /// <summary>Where the source looks. Named in the start-up banner, so nobody has to guess
    /// what is being read.</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>The sessions on disk right now, each with the files it is read from. Cheap:
    /// it runs every pass, and reads a file's content only the first time it sees the file.</summary>
    Discovery Discover(CancellationToken ct);

    /// <summary>Reads one session, or null when its files hold nothing to score. May throw
    /// <see cref="IOException"/>: the files belong to another program and can change or vanish
    /// under the read.</summary>
    PersistedSession? Read(ScanUnit unit);
}

/// <summary>What a source found. <c>Complete</c> is false when some of it could not be walked,
/// in which case the scan state keeps its record of sessions that were not seen.</summary>
internal sealed record Discovery(IReadOnlyList<ScanUnit> Units, bool Complete);

internal sealed record ScanFile(string Path, long Length, DateTimeOffset LastWrite);

/// <summary>One session and the files it is read from.</summary>
internal sealed record ScanUnit(string SessionId, IReadOnlyList<ScanFile> Files)
{
    /// <summary>When any of its files was last written.</summary>
    public DateTimeOffset LastWrite { get; } = Files.Max(f => f.LastWrite);

    /// <summary>Changes whenever a file is added, removed, grown or rewritten — what decides
    /// whether a session has to be read again.</summary>
    public string Fingerprint { get; } = FingerprintOf(Files);

    private static string FingerprintOf(IReadOnlyList<ScanFile> files)
    {
        var text = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            text.Append(file.Path).Append('\0').Append(file.Length).Append('\0')
                .Append(file.LastWrite.UtcTicks).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
