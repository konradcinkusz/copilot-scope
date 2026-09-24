using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Session storage for one machine, with no database: one JSON file per session (ADR-004).
///
/// The session document is exactly what <see cref="PostgresSessionRepository"/> stores in its
/// jsonb column — same record, same serializer settings — wrapped in an envelope that carries
/// the fields Postgres keeps in columns beside it (score, grade, kind, the cohort keys), so a
/// query never has to open a file to decide whether it matches. Layout under the directory:
///
///   <c>sessions/&lt;hash&gt;.json</c>  one envelope per session. Named by a hash of the id, not the
///                              id itself: ids contain <c>:</c> and <c>/</c>, can differ only in
///                              case on a case-insensitive file system, and can collide with
///                              names Windows reserves.
///   <c>index.json</c>              a cache of the envelopes' header fields. Only a cache: startup
///                              checks every file's size and write time against it and re-reads
///                              what disagrees, so a crash can leave it stale but never wrong.
///   <c>.lock</c>                   held exclusively while the store is open, so a second collector
///                              pointed at the same directory is refused instead of interleaving
///                              writes (the single-writer rule of ADR-001, per directory).
///
/// Writes go to a temporary file that is flushed to disk and then renamed over the old one, so
/// a reader — or a crash — sees either the previous snapshot or the new one, never half of
/// either. Queries run against the in-memory index and open only the files of the page they
/// return.
/// </summary>
public sealed class FileSessionRepository(string directory, ILogger? logger = null) : ISessionRepository, IDisposable
{
    /// <summary>Version of the envelope layout. A file with a higher number was written by a
    /// newer CopilotScope, and this one refuses to open the directory rather than rewrite those
    /// files without the fields it does not know about.</summary>
    internal const int SchemaVersion = 1;

    // Identical to PostgresSessionRepository's, so the session document is the same bytes in
    // either store and moving history between them is a copy, not a conversion.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan IndexCacheInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly string _root = Path.GetFullPath(directory);
    private readonly Dictionary<string, IndexEntry> _index = new(StringComparer.Ordinal);
    private readonly object _indexLock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private FileStream? _lock;
    private bool _indexDirty;
    private DateTimeOffset _indexWrittenAt;
    private bool _disposed;
    // Set when the directory holds files from a newer version. Remembered, because every write
    // retries opening the store, and re-reading the whole directory once a second to reach the
    // same refusal again would be all this process did.
    private InvalidOperationException? _incompatible;

    public string Kind => "files";

    public string Description => $"local files ({_root})";

    /// <summary>The data directory, fully qualified.</summary>
    public string Directory => _root;

    private string SessionsDirectory => Path.Combine(_root, "sessions");
    private string IndexPath => Path.Combine(_root, "index.json");
    private string LockPath => Path.Combine(_root, ".lock");

    // ------------------------------------------------------------------ lifecycle

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lock is not null) return;
            if (_incompatible is not null) throw _incompatible;

            CreatePrivateDirectory(_root);
            CreatePrivateDirectory(SessionsDirectory);
            var held = AcquireLock();
            try { await LoadIndexAsync(ct); }
            catch (Exception ex)
            {
                held.Dispose();
                if (ex is InvalidOperationException incompatible) _incompatible = incompatible;
                throw;
            }
            Volatile.Write(ref _lock, held);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Opens the store on first use. Startup opens it, but a failed startup (another process
    /// held the directory) is retried here, so the collector picks the store up once the
    /// directory is free instead of staying in memory for the life of the process.
    /// </summary>
    private Task EnsureOpenAsync(CancellationToken ct) =>
        Volatile.Read(ref _lock) is null ? EnsureSchemaAsync(ct) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            // After this, an operation throws instead of quietly reopening the directory — and
            // holding its lock — for a caller that outlived the host.
            _disposed = true;
            if (_lock is null) return;
            try { await WriteIndexCacheAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not write the session index cache on shutdown."); }
            await _lock.DisposeAsync();
            _lock = null;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>For a container disposed synchronously, which refuses a service that can only be
    /// disposed asynchronously. Releasing the directory is not optional: a store that never let
    /// go of its lock would lock the next collector out of its own history.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private FileStream AcquireLock()
    {
        try
        {
            // FileShare.None is an exclusive flock on Linux and macOS and a sharing-mode lock on
            // Windows: either way, a second process gets an IOException right here.
            return new FileStream(LockPath, CreateOptions(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Session storage at {_root} is in use by another CopilotScope process. Stop that process, " +
                "or give this one its own directory with CopilotScope:Storage:Path.", ex);
        }
    }

    // ------------------------------------------------------------------ writes

    public async Task UpsertAsync(PersistedSession session, double qualityScore, string qualityGrade,
        CancellationToken ct, string sessionKind = "UserChat")
    {
        await EnsureOpenAsync(ct);

        var file = FileNameFor(session.Id);
        var header = new EnvelopeHeader(SchemaVersion, session.Id, session.LastSeen,
            // A NaN would make the serializer throw, and a session that cannot be written is
            // re-queued every second forever. Postgres would store it; nothing reads it usefully.
            double.IsFinite(qualityScore) ? qualityScore : 0, qualityGrade, sessionKind,
            session.ChatCalls, session.Repository, session.EmitterKind.ToString(),
            session.ModelCalls?.Keys.ToArray() ?? []);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Envelope.Of(header, DateTimeOffset.UtcNow, session), Json);

        await _writeGate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(SessionsDirectory, file);
            await WriteAtomicallyAsync(path, bytes, ct);
            var written = new FileInfo(path);
            lock (_indexLock)
            {
                _index[session.Id] = IndexEntry.From(header, file, written);
                _indexDirty = true;
            }
            if (DateTimeOffset.UtcNow - _indexWrittenAt > IndexCacheInterval)
            {
                // The session is written; a cache that could not be refreshed only costs the
                // next startup some re-reading, so it must not fail the write.
                try { await WriteIndexCacheAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not refresh the session index cache."); }
            }
        }
        finally { _writeGate.Release(); }
    }

    public async Task<int> DeleteAsync(string id, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        return await DeleteWhereAsync(e => e.Id == id, ct, alsoFile: FileNameFor(id));
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        return await DeleteWhereAsync(e => e.LastSeen < cutoff, ct);
    }

    public async Task<int> DeleteByPrefixAsync(string prefix, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        return await DeleteWhereAsync(e => e.Id.StartsWith(prefix, StringComparison.Ordinal), ct);
    }

    /// <param name="alsoFile">A file to remove even if the index has no entry for it, so a
    /// delete by id cannot leave a snapshot behind that the next startup would index again.</param>
    private async Task<int> DeleteWhereAsync(Func<IndexEntry, bool> predicate, CancellationToken ct,
        string? alsoFile = null)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            List<IndexEntry> doomed;
            lock (_indexLock) doomed = _index.Values.Where(predicate).ToList();

            var files = doomed.Select(e => e.File).ToHashSet(StringComparer.Ordinal);
            var strayRemoved = alsoFile is not null && !files.Contains(alsoFile)
                && DeleteFile(Path.Combine(SessionsDirectory, alsoFile));
            foreach (var entry in doomed) DeleteFile(Path.Combine(SessionsDirectory, entry.File));

            lock (_indexLock)
            {
                foreach (var entry in doomed) _index.Remove(entry.Id);
                if (doomed.Count > 0) _indexDirty = true;
            }
            return doomed.Count + (strayRemoved ? 1 : 0);
        }
        finally { _writeGate.Release(); }
    }

    // ------------------------------------------------------------------ reads

    public async Task<List<PersistedSession>> LoadAllAsync(int limit, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        return await ReadAsync(Select(null, null, includeInternal: true, CohortFilter.None).Take(Math.Max(0, limit)), ct);
    }

    public async Task<List<PersistedSession>> QueryAsync(DateTimeOffset? since, DateTimeOffset? until, int limit,
        int offset, CancellationToken ct, bool includeInternal = false, CohortFilter? cohort = null)
    {
        await EnsureOpenAsync(ct);
        var page = Select(since, until, includeInternal, cohort ?? CohortFilter.None)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit));
        return await ReadAsync(page, ct);
    }

    public async Task<int> CountAsync(DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct,
        bool includeInternal = false, CohortFilter? cohort = null)
    {
        await EnsureOpenAsync(ct);
        return Select(since, until, includeInternal, cohort ?? CohortFilter.None).Count;
    }

    public async Task<PersistedSession?> GetAsync(string id, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        var envelope = await ReadEnvelopeAsync(Path.Combine(SessionsDirectory, FileNameFor(id)), ct);
        return envelope is { Session: not null } && envelope.Id == id ? envelope.Session : null;
    }

    public async Task<List<double>> ScoresAsync(DateTimeOffset? since, int limit, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        // The population PostgresSessionRepository.ScoresAsync selects: user chats that ran a call.
        lock (_indexLock)
            return _index.Values
                .Where(e => e.Kind == nameof(SessionKind.UserChat) && e.ChatCalls > 0)
                .Where(e => since is null || e.LastSeen >= since)
                .OrderByDescending(e => e.LastSeen).ThenBy(e => e.Id, StringComparer.Ordinal)
                .Take(Math.Max(0, limit))
                .Select(e => e.Score)
                .ToList();
    }

    public async Task<List<string>> IdsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await EnsureOpenAsync(ct);
        lock (_indexLock) return _index.Values.Where(e => e.LastSeen < cutoff).Select(e => e.Id).ToList();
    }

    /// <summary>
    /// The index entries a query matches, newest first. The same rules the SQL states: the
    /// window bounds LastSeen inclusively, internal helper kinds are out unless asked for, and
    /// the cohort filter is <see cref="CohortFilter"/>'s own matcher — the one the read path
    /// applies to the live sessions it overlays on this page. Ties are broken by id, which
    /// Postgres leaves unspecified; a stable order is what keeps a pager from repeating a row.
    /// </summary>
    private List<IndexEntry> Select(DateTimeOffset? since, DateTimeOffset? until, bool includeInternal,
        CohortFilter cohort)
    {
        lock (_indexLock)
            return _index.Values
                .Where(e => (since is null || e.LastSeen >= since) && (until is null || e.LastSeen <= until))
                .Where(e => includeInternal || !(e.SessionKind is { } k && SessionClassifier.IsInternal(k)))
                .Where(e => cohort.MatchesExceptGrade(e.Repository, e.EmitterKind, () => e.SessionKind, e.Models)
                            && cohort.MatchesGrade(e.Grade))
                .OrderByDescending(e => e.LastSeen).ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
    }

    private async Task<List<PersistedSession>> ReadAsync(IEnumerable<IndexEntry> entries, CancellationToken ct)
    {
        var result = new List<PersistedSession>();
        foreach (var entry in entries)
        {
            // Null when the file went away between the index lookup and the read — a delete
            // that raced this query. Skipping it is the answer the delete asked for.
            if (await ReadEnvelopeAsync(Path.Combine(SessionsDirectory, entry.File), ct) is { Session: not null } envelope
                && envelope.Id == entry.Id)
                result.Add(envelope.Session);
        }
        return result;
    }

    // ------------------------------------------------------------------ index

    /// <summary>
    /// Builds the in-memory index: from the cache where a file's size and write time still
    /// match it, from the file's own header where they do not. Files that cannot be read are
    /// logged and left where they are — never deleted, since an unreadable file is the
    /// evidence someone needs to work out what went wrong.
    /// </summary>
    private async Task LoadIndexAsync(CancellationToken ct)
    {
        // A temporary file is a write that never reached its rename; the snapshot (or index
        // cache) it was replacing is still intact next to it.
        foreach (var stray in System.IO.Directory.EnumerateFiles(SessionsDirectory, "*.tmp")
                     .Concat(System.IO.Directory.EnumerateFiles(_root, "*.tmp")))
            DeleteFile(stray);

        var cached = ReadIndexCache();
        var fresh = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        var reread = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(SessionsDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.GetFileName(path);
            var info = new FileInfo(path);
            if (cached is not null && cached.TryGetValue(file, out var hit)
                && hit.Length == info.Length && hit.WrittenTicks == info.LastWriteTimeUtc.Ticks)
            {
                fresh[hit.Id] = hit;
                continue;
            }

            reread++;
            if (ReadHeader(path, file) is { } header) fresh[header.Id] = IndexEntry.From(header, file, info);
        }

        lock (_indexLock)
        {
            _index.Clear();
            foreach (var (id, entry) in fresh) _index[id] = entry;
            _indexDirty = cached is null || reread > 0 || cached.Count != fresh.Count;
        }
        await WriteIndexCacheAsync();
        _logger.LogInformation("Session storage opened at {Directory}: {Count} session(s), {Reread} re-read from disk.",
            _root, fresh.Count, reread);
    }

    private EnvelopeHeader? ReadHeader(string path, string file)
    {
        EnvelopeHeader? header;
        try
        {
            using var stream = OpenRead(path);
            header = JsonSerializer.Deserialize<EnvelopeHeader>(stream, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Skipping unreadable session file {File}.", path);
            return null;
        }

        if (header is null || string.IsNullOrEmpty(header.Id))
        {
            _logger.LogWarning("Skipping session file {File}: it has no session id.", path);
            return null;
        }
        if (header.Schema > SchemaVersion)
            throw new InvalidOperationException(
                $"{path} was written by a newer CopilotScope (storage schema {header.Schema}; this version reads " +
                $"up to {SchemaVersion}). Upgrade, or point CopilotScope:Storage:Path at another directory.");
        if (FileNameFor(header.Id) != file)
        {
            // Renamed or copied in by hand. Indexing it would give one id two files, and a
            // delete would remove only the one the id hashes to.
            _logger.LogWarning("Skipping session file {File}: its name does not match session {Id}.", path, header.Id);
            return null;
        }
        return header;
    }

    private Dictionary<string, IndexEntry>? ReadIndexCache()
    {
        try
        {
            if (!File.Exists(IndexPath)) return null;
            using var stream = OpenRead(IndexPath);
            var cache = JsonSerializer.Deserialize<IndexCache>(stream, Json);
            return cache?.Schema == SchemaVersion && cache.Entries is not null
                ? cache.Entries.GroupBy(e => e.File, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Only a cache: rebuild from the files.
            _logger.LogInformation(ex, "Session index cache unreadable; rebuilding it from the session files.");
            return null;
        }
    }

    /// <summary>Writes the index cache if it changed. Called with the write gate held.</summary>
    private async Task WriteIndexCacheAsync()
    {
        List<IndexEntry> entries;
        lock (_indexLock)
        {
            if (!_indexDirty) return;
            entries = _index.Values.ToList();
            _indexDirty = false;
        }
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new IndexCache(SchemaVersion, entries), Json);
            await WriteAtomicallyAsync(IndexPath, bytes, CancellationToken.None);
            _indexWrittenAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            lock (_indexLock) _indexDirty = true;
            throw;
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>The file a session id lives in: 128 bits of its SHA-256, in hex.</summary>
    internal static string FileNameFor(string id) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32] + ".json";

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary,
                             CreateOptions(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                await stream.WriteAsync(bytes, ct);
                // Reach the disk before the rename makes the file visible: a rename that lands
                // ahead of its data is how a power cut leaves an empty snapshot behind.
                stream.Flush(flushToDisk: true);
            }
            await MoveWithRetryAsync(temporary, path, ct);
        }
        catch
        {
            DeleteFile(temporary);
            throw;
        }
    }

    /// <summary>
    /// Replaces the destination, retrying briefly: on Windows a virus scanner or the search
    /// indexer holding the file open fails a rename that would succeed a moment later. The
    /// caller re-queues the session if every attempt fails.
    /// </summary>
    private static async Task MoveWithRetryAsync(string source, string destination, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt * attempt), ct);
            }
        }
    }

    private static async Task<Envelope?> ReadEnvelopeAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Envelope>(stream, Json, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Read access that does not stand in the way of a concurrent rename or delete.</summary>
    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.SequentialScan
    });

    /// <summary>Owner-only on Unix: a session file can hold prompt text, when content capture
    /// is on, and the data directory sits in a home directory other accounts may read.</summary>
    private static FileStreamOptions CreateOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(path);
        else System.IO.Directory.CreateDirectory(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ on-disk shapes

    /// <summary>The fields Postgres keeps in columns beside the snapshot, written ahead of it
    /// so the index can be rebuilt without materializing a single session.</summary>
    private sealed record EnvelopeHeader(
        int Schema, string Id, DateTimeOffset LastSeen, double Score, string Grade, string Kind,
        int ChatCalls, string? Repository, string Emitter, string[] Models);

    private sealed record Envelope(
        int Schema, string Id, DateTimeOffset LastSeen, double Score, string Grade, string Kind,
        int ChatCalls, string? Repository, string Emitter, string[] Models,
        DateTimeOffset UpdatedAt, PersistedSession Session)
    {
        public static Envelope Of(EnvelopeHeader h, DateTimeOffset updatedAt, PersistedSession session) =>
            new(h.Schema, h.Id, h.LastSeen, h.Score, h.Grade, h.Kind, h.ChatCalls, h.Repository, h.Emitter,
                h.Models, updatedAt, session);
    }

    private sealed record IndexCache(int Schema, List<IndexEntry> Entries);

    /// <summary>One session as queries see it, plus the file state it was read from.</summary>
    private sealed record IndexEntry(
        string Id, string File, DateTimeOffset LastSeen, double Score, string Grade, string Kind,
        int ChatCalls, string? Repository, string Emitter, string[] Models, long Length, long WrittenTicks)
    {
        public SessionKind? SessionKind =>
            Enum.TryParse<SessionKind>(Kind, out var kind) ? kind : null;

        public EmitterKind EmitterKind =>
            Enum.TryParse<EmitterKind>(Emitter, out var emitter) ? emitter : Domain.EmitterKind.Unknown;

        public static IndexEntry From(EnvelopeHeader h, string file, FileInfo written) => new(
            h.Id, file, h.LastSeen, h.Score, h.Grade ?? "", h.Kind ?? nameof(Domain.SessionKind.UserChat),
            h.ChatCalls, h.Repository, h.Emitter ?? nameof(Domain.EmitterKind.Unknown), h.Models ?? [],
            written.Length, written.LastWriteTimeUtc.Ticks);
    }
}
