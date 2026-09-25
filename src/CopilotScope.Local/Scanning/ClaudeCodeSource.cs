using CopilotScope.Collector.Import;
using CopilotScope.Collector.Persistence;

namespace CopilotScope.Local.Scanning;

/// <summary>
/// Claude Code's transcripts: <c>projects/&lt;encoded-cwd&gt;/&lt;session&gt;.jsonl</c> under its data
/// directory, plus the files its subagents write under the same session id. Read with the
/// importer's own parser (<see cref="ClaudeCodeTranscript"/>), so a session scanned here and
/// one imported with <c>copilotscope-import</c> are the same session. Never written to.
/// </summary>
internal sealed class ClaudeCodeSource(IReadOnlyList<string>? roots = null) : IScanSource
{
    // A file's session id is read from its first lines once, and again only if it had none —
    // a session file does not change sessions, and reading every file every minute would be
    // most of what the scanner did.
    private readonly Dictionary<string, (long Length, long Ticks, string? SessionId)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _repositories = new(StringComparer.Ordinal);

    public string Name => "claude-code";

    public string DisplayName => "Claude Code";

    public int Version => ClaudeCodeTranscript.Version;

    public IReadOnlyList<string> Roots { get; } = roots ?? ClaudeCodeFiles.DefaultRoots();

    public Discovery Discover(CancellationToken ct)
    {
        var complete = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, List<ScanFile>>(StringComparer.Ordinal);

        foreach (var root in Roots)
        {
            try
            {
                foreach (var path in ClaudeCodeFiles.Discover(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(path)) continue;

                    var info = new FileInfo(path);
                    if (!info.Exists) continue; // gone since the directory was listed
                    var (length, ticks) = (info.Length, info.LastWriteTimeUtc.Ticks);

                    if (!_files.TryGetValue(path, out var known) || known.SessionId is null && (known.Length, known.Ticks) != (length, ticks))
                        known = (length, ticks, ClaudeCodeFiles.SessionIdOf(path));
                    _files[path] = (length, ticks, known.SessionId);

                    if (known.SessionId is not { } id) continue;
                    if (!sessions.TryGetValue(id, out var files)) sessions[id] = files = [];
                    files.Add(new ScanFile(path, length, new DateTimeOffset(ticks, TimeSpan.Zero)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A directory removed mid-walk, say. What was found still counts; what was not
                // is not taken to be gone.
                complete = false;
            }
        }

        foreach (var gone in _files.Keys.Where(path => !seen.Contains(path)).ToList())
            _files.Remove(gone);

        return new Discovery(sessions.Select(s => new ScanUnit(s.Key, s.Value)).ToList(), complete);
    }

    public PersistedSession? Read(ScanUnit unit)
    {
        var paths = unit.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
        var cwd = paths.Select(ClaudeCodeFiles.WorkingDirectoryOf).FirstOrDefault(c => c is not null);
        var repository = cwd is null ? null : GitRemote.RepositoryFor(cwd, _repositories);

        // Prompt and response text stays out, as it does for the importer unless asked: the
        // score is computed from counts and timings, and the text is the most sensitive thing
        // on the machine.
        var parsed = ClaudeCodeTranscript.Parse(ClaudeCodeFiles.LinesOf(paths), repository);
        return parsed is null ? null : PersistedSession.From(parsed.Session);
    }
}
