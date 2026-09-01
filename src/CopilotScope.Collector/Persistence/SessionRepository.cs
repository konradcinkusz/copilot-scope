using System.Text.Json;
using Npgsql;

namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Thin Npgsql repository — one table, jsonb snapshot per session, upserted by the
/// debounced <see cref="PersistenceWriter"/>. No EF: the access pattern is a pure
/// key/value upsert + full scan on startup, an ORM would only add weight.
/// </summary>
public sealed class SessionRepository(string connectionString) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        // session_kind and chat_calls are denormalized out of the snapshot so the baseline
        // query can exclude internal helper calls without deserializing every row. Added
        // with IF NOT EXISTS so an existing deployment upgrades in place; existing rows get
        // the defaults and are corrected on their next write.
        const string ddl = """
            CREATE TABLE IF NOT EXISTS sessions (
                id            text PRIMARY KEY,
                first_seen    timestamptz NOT NULL,
                last_seen     timestamptz NOT NULL,
                quality_score double precision NOT NULL DEFAULT 0,
                quality_grade text NOT NULL DEFAULT '',
                snapshot      jsonb NOT NULL,
                updated_at    timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS session_kind text NOT NULL DEFAULT 'UserChat';
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS chat_calls int NOT NULL DEFAULT 0;
            CREATE INDEX IF NOT EXISTS ix_sessions_last_seen ON sessions (last_seen DESC);
            CREATE INDEX IF NOT EXISTS ix_sessions_baseline ON sessions (session_kind, last_seen DESC);
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(PersistedSession session, double qualityScore, string qualityGrade,
        CancellationToken ct, string sessionKind = "UserChat")
    {
        const string sql = """
            INSERT INTO sessions (id, first_seen, last_seen, quality_score, quality_grade, snapshot,
                                  session_kind, chat_calls, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, now())
            ON CONFLICT (id) DO UPDATE SET
                last_seen = EXCLUDED.last_seen,
                quality_score = EXCLUDED.quality_score,
                quality_grade = EXCLUDED.quality_grade,
                snapshot = EXCLUDED.snapshot,
                session_kind = EXCLUDED.session_kind,
                chat_calls = EXCLUDED.chat_calls,
                updated_at = now();
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(session.Id);
        cmd.Parameters.AddWithValue(session.FirstSeen);
        cmd.Parameters.AddWithValue(session.LastSeen);
        cmd.Parameters.AddWithValue(qualityScore);
        cmd.Parameters.AddWithValue(qualityGrade);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = JsonSerializer.Serialize(session, Json)
        });
        cmd.Parameters.AddWithValue(sessionKind);
        cmd.Parameters.AddWithValue(session.ChatCalls);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<PersistedSession>> LoadAllAsync(int limit, CancellationToken ct)
    {
        var result = new List<PersistedSession>();
        await using var cmd = _dataSource.CreateCommand(
            "SELECT snapshot FROM sessions ORDER BY last_seen DESC LIMIT $1;");
        cmd.Parameters.AddWithValue(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0);
            if (JsonSerializer.Deserialize<PersistedSession>(json, Json) is { } snapshot)
                result.Add(snapshot);
        }
        return result;
    }

    /// <summary>
    /// Reads one page of session history, newest first, optionally bounded by time.
    /// This is the query path behind /api/sessions: without it the API could only ever
    /// show the newest 200 sessions the in-memory store happens to hold, and a team's
    /// history would silently disappear hours after it was written.
    /// </summary>
    public async Task<List<PersistedSession>> QueryAsync(
        DateTimeOffset? since, DateTimeOffset? until, int limit, int offset, CancellationToken ct)
    {
        var result = new List<PersistedSession>();
        await using var cmd = _dataSource.CreateCommand("""
            SELECT snapshot FROM sessions
            WHERE ($1::timestamptz IS NULL OR last_seen >= $1)
              AND ($2::timestamptz IS NULL OR last_seen <= $2)
            ORDER BY last_seen DESC
            LIMIT $3 OFFSET $4;
            """);
        cmd.Parameters.Add(Nullable(since));
        cmd.Parameters.Add(Nullable(until));
        cmd.Parameters.AddWithValue(limit);
        cmd.Parameters.AddWithValue(offset);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (JsonSerializer.Deserialize<PersistedSession>(reader.GetString(0), Json) is { } snapshot)
                result.Add(snapshot);
        return result;
    }

    /// <summary>Reads a single session's snapshot, for ids no longer held in memory.</summary>
    public async Task<PersistedSession?> GetAsync(string id, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT snapshot FROM sessions WHERE id = $1;");
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? JsonSerializer.Deserialize<PersistedSession>(reader.GetString(0), Json)
            : null;
    }

    /// <summary>Total rows matching the window — so a pager can report what it is paging through.</summary>
    public async Task<int> CountAsync(DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("""
            SELECT count(*) FROM sessions
            WHERE ($1::timestamptz IS NULL OR last_seen >= $1)
              AND ($2::timestamptz IS NULL OR last_seen <= $2);
            """);
        cmd.Parameters.Add(Nullable(since));
        cmd.Parameters.Add(Nullable(until));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    /// <summary>
    /// Quality scores over a window, read straight from the indexed column rather than by
    /// deserializing snapshots. This is what the percentile baseline is computed over, so
    /// a session's rank is against a defined window of history instead of against whatever
    /// happened to survive in memory.
    /// </summary>
    public async Task<List<double>> ScoresAsync(DateTimeOffset? since, int limit, CancellationToken ct)
    {
        // Same population the in-memory baseline used: real user chats that actually ran a
        // call. Internal helper sessions (title generation, summarization) would otherwise
        // drag the baseline toward scores nobody is being compared against.
        var scores = new List<double>();
        await using var cmd = _dataSource.CreateCommand("""
            SELECT quality_score FROM sessions
            WHERE session_kind = 'UserChat' AND chat_calls > 0
              AND ($1::timestamptz IS NULL OR last_seen >= $1)
            ORDER BY last_seen DESC LIMIT $2;
            """);
        cmd.Parameters.Add(Nullable(since));
        cmd.Parameters.AddWithValue(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) scores.Add(reader.GetDouble(0));
        return scores;
    }

    /// <summary>
    /// Deletes sessions last seen before the cutoff. Retention is off unless configured,
    /// so this only ever runs against a policy the operator set deliberately.
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM sessions WHERE last_seen < $1;");
        cmd.Parameters.AddWithValue(cutoff);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ids of sessions last seen before the cutoff — needed to evict them from memory too.</summary>
    public async Task<List<string>> IdsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var ids = new List<string>();
        await using var cmd = _dataSource.CreateCommand("SELECT id FROM sessions WHERE last_seen < $1;");
        cmd.Parameters.AddWithValue(cutoff);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>Npgsql needs an explicitly typed parameter to bind a null timestamptz.</summary>
    private static NpgsqlParameter Nullable(DateTimeOffset? value) => new()
    {
        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
        Value = value.HasValue ? value.Value : DBNull.Value
    };

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM sessions WHERE id = $1;");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Bulk-deletes rows whose id starts with the given prefix — used to clear a
    /// previously seeded demo/local dataset before writing a fresh one.</summary>
    public async Task<int> DeleteByPrefixAsync(string prefix, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM sessions WHERE id LIKE $1;");
        cmd.Parameters.AddWithValue(prefix + "%");
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
