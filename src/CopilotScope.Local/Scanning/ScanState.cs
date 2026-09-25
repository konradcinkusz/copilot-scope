using System.Text.Json;

namespace CopilotScope.Local.Scanning;

/// <summary>
/// Which sessions the scanner has already handed to the collector, and from which version of
/// each file: per source, a fingerprint per session id and the reader version that read them.
///
/// Kept in the data directory it describes (<c>scan-state.json</c>) rather than beside it:
/// deleting the history, or pointing <c>--data</c> somewhere new, has to start the import over,
/// and a record kept elsewhere would claim sessions were imported into a store that never saw
/// them. With <c>--memory</c> it lives in memory too, for the same reason. Losing it costs one
/// full re-read: an import replaces a session rather than adding to it, and never replaces a
/// live one.
/// </summary>
internal sealed class ScanState
{
    public const string FileName = "scan-state.json";

    private const int Schema = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string? _path;
    private readonly Dictionary<string, SourceState> _sources;
    private bool _dirty;

    private ScanState(string? path, Dictionary<string, SourceState> sources)
    {
        _path = path;
        _sources = sources;
    }

    private sealed record Document(int Schema, Dictionary<string, SourceState> Sources);

    private sealed class SourceState
    {
        public int Version { get; set; }
        public Dictionary<string, string> Sessions { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>A state that is never written anywhere.</summary>
    public static ScanState InMemory() => new(null, new(StringComparer.Ordinal));

    /// <summary>Reads the state at the path. Missing, unreadable or from another version, it
    /// starts empty — never fails start-up.</summary>
    public static ScanState Load(string path, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json) is { Schema: Schema } document)
                return new ScanState(path, new Dictionary<string, SourceState>(document.Sources, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Could not read {Path} ({Error}); local history will be read again in full.", path, ex.Message);
        }
        return new ScanState(path, new(StringComparer.Ordinal));
    }

    /// <summary>Whether this version of the session was already handed over by this version of
    /// the reader.</summary>
    public bool IsCurrent(string source, int version, string sessionId, string fingerprint) =>
        _sources.TryGetValue(source, out var state) && state.Version == version
        && state.Sessions.TryGetValue(sessionId, out var recorded) && recorded == fingerprint;

    public void Record(string source, int version, string sessionId, string fingerprint)
    {
        var state = For(source, version);
        if (state.Sessions.TryGetValue(sessionId, out var recorded) && recorded == fingerprint) return;
        state.Sessions[sessionId] = fingerprint;
        _dirty = true;
    }

    /// <summary>Forgets sessions whose files are gone. Their sessions stay in the store: a
    /// deleted transcript never deletes history (Claude Code clears its own after a month).</summary>
    public void Retain(string source, IReadOnlySet<string> sessionIds)
    {
        if (!_sources.TryGetValue(source, out var state)) return;
        foreach (var gone in state.Sessions.Keys.Where(id => !sessionIds.Contains(id)).ToList())
        {
            state.Sessions.Remove(gone);
            _dirty = true;
        }
    }

    /// <summary>Writes the state if it changed: to a temporary file, then renamed over the old
    /// one, so a crash leaves the previous state rather than half of the new one.</summary>
    public void Save()
    {
        if (!_dirty || _path is null) return;

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options))
                JsonSerializer.Serialize(stream, new Document(Schema, _sources), Json);
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private SourceState For(string source, int version)
    {
        if (!_sources.TryGetValue(source, out var state) || state.Version != version)
        {
            // A new reader: nothing the old one recorded says anything about what this one reads.
            _sources[source] = state = new SourceState { Version = version };
            _dirty = true;
        }
        return state;
    }
}
