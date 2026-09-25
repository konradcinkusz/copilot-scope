using System.Text.Json;

namespace CopilotScope.Collector.Import;

/// <summary>
/// Where Claude Code keeps its transcripts, and which of them make up one session. Shared by
/// the importer (<c>tools/CopilotScope.LogImporter</c>) and the native binary's scanner
/// (ADR-004), so a session found either way is read from the same files in the same order.
/// </summary>
public static class ClaudeCodeFiles
{
    /// <summary>One session's transcript files: the main one, and any a subagent wrote under
    /// the same session id.</summary>
    public sealed record SessionFiles(string SessionId, IReadOnlyList<string> Files);

    /// <summary>
    /// Where Claude Code keeps its transcripts: <c>$CLAUDE_CONFIG_DIR/projects</c> when that is
    /// set, otherwise both <c>~/.claude/projects</c> and <c>~/.config/claude/projects</c> — Claude
    /// Code has used both locations, and a machine upgraded across the change has history in
    /// each. Reading one would silently leave the other half of someone's history out.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return [Path.Combine(configured, "projects")];

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [Path.Combine(home, ".claude", "projects"), Path.Combine(home, ".config", "claude", "projects")];
    }

    /// <summary>Every transcript under the root. Claude Code nests one directory per project.</summary>
    public static IEnumerable<string> Discover(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            : [];

    /// <summary>
    /// Groups transcript files by the session id written inside them, not by file name. A
    /// subagent's transcript carries its parent's session id; imported one file at a time, the
    /// two became two imports of the same session and whichever was sent last replaced the
    /// other — a session's whole main conversation lost to its subagent's side trip. Files with
    /// no session id in them hold nothing to import and are left out.
    /// </summary>
    public static IReadOnlyList<SessionFiles> GroupBySession(IEnumerable<string> files) =>
        files.Select(file => (File: file, SessionId: SessionIdOf(file)))
            .Where(f => f.SessionId is not null)
            .GroupBy(f => f.SessionId!, StringComparer.Ordinal)
            .Select(g => new SessionFiles(g.Key, g.Select(f => f.File).Order(StringComparer.Ordinal).ToList()))
            .ToList();

    /// <summary>
    /// A session's lines. One file is read as it is, lazily. Several are interleaved by each
    /// line's own timestamp, so a subagent's calls land in the turn that ran them rather than
    /// all in the last one. A line without a timestamp travels with the line before it in its
    /// own file; one before any timestamp at all goes ahead of everything, in file order.
    /// </summary>
    public static IEnumerable<string> LinesOf(IReadOnlyList<string> files)
    {
        if (files.Count == 1) return File.ReadLines(files[0]);

        var lines = new List<(string Line, DateTimeOffset? At, int Order)>();
        foreach (var file in files)
        {
            DateTimeOffset? last = null;
            foreach (var line in File.ReadLines(file))
            {
                last = TimestampOf(line) ?? last;
                lines.Add((line, last, lines.Count));
            }
        }
        // Stable, with nulls first: equal times keep the order they were read in, so a line
        // without a timestamp stays right behind the line whose time it took.
        return lines.OrderBy(l => l.At).ThenBy(l => l.Order).Select(l => l.Line).ToList();
    }

    /// <summary>A line's own top-level timestamp, or null when it has none that parses.</summary>
    private static DateTimeOffset? TimestampOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("timestamp", out var at)
                && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The session id a transcript belongs to, from the first line that carries one.</summary>
    public static string? SessionIdOf(string file) => FirstString(file, "sessionId");

    /// <summary>
    /// The working directory a transcript was recorded in, read from its first line. Cheaper
    /// and more reliable than decoding it out of Claude Code's directory-name encoding, which
    /// is lossy — a path containing a dash cannot be recovered from it.
    /// </summary>
    public static string? WorkingDirectoryOf(string file) => FirstString(file, "cwd");

    private static string? FirstString(string file, string property)
    {
        // Scans until it finds one, rather than reading only the first line: a transcript
        // routinely opens with a `summary` line, which carries neither a cwd nor a session id.
        // Bounded, because a transcript without one in its first few lines has none at all.
        const int MaxLines = 50;
        var seen = 0;

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (++seen > MaxLines) break;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } found)
                        return found;
                }
                catch (JsonException) { /* a half-written line proves nothing about the rest */ }
            }
        }
        catch (IOException) { /* locked or vanished: the caller reports what it could not read */ }
        return null;
    }
}
