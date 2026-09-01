using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
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
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS repository text;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS emitter_kind text;
            CREATE INDEX IF NOT EXISTS ix_sessions_last_seen ON sessions (last_seen DESC);
            CREATE INDEX IF NOT EXISTS ix_sessions_baseline ON sessions (session_kind, last_seen DESC);
            CREATE INDEX IF NOT EXISTS ix_sessions_cohort ON sessions (repository, emitter_kind, last_seen DESC);
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);

        await BackfillCohortColumnsAsync(ct);
    }

    /// <summary>
    /// Fills the cohort columns for rows written before they existed.
    ///
    /// Without this, "show me the Cursor sessions" would quietly mean "show me the Cursor
    /// sessions written since the upgrade" — a filter that returns a subset and says nothing
    /// about it is worse than a filter that is missing. Rows are rewritten on their next
    /// flush anyway, but only for sessions that are still active; history never would be.
    ///
    /// <c>emitter_kind IS NULL</c> is the not-yet-backfilled marker: every upsert writes a
    /// non-null value, so the update touches each historical row exactly once. The enum
    /// mapping is generated from the enum itself rather than written out, so it cannot drift
    /// from the integers the snapshots actually contain.
    /// </summary>
    private async Task BackfillCohortColumnsAsync(CancellationToken ct)
    {
        var mapping = string.Join(" ", Enum.GetValues<EmitterKind>()
            .Select(k => $"WHEN {(int)k} THEN '{k}'"));

        await using var cmd = _dataSource.CreateCommand($"""
            UPDATE sessions SET
                repository   = snapshot->>'repository',
                emitter_kind = CASE (snapshot->>'emitterKind')::int {mapping} ELSE 'Unknown' END
            WHERE emitter_kind IS NULL;
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(PersistedSession session, double qualityScore, string qualityGrade,
        CancellationToken ct, string sessionKind = "UserChat")
    {
        const string sql = """
            INSERT INTO sessions (id, first_seen, last_seen, quality_score, quality_grade, snapshot,
                                  session_kind, chat_calls, repository, emitter_kind, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, now())
            ON CONFLICT (id) DO UPDATE SET
                last_seen = EXCLUDED.last_seen,
                quality_score = EXCLUDED.quality_score,
                quality_grade = EXCLUDED.quality_grade,
                snapshot = EXCLUDED.snapshot,
                session_kind = EXCLUDED.session_kind,
                chat_calls = EXCLUDED.chat_calls,
                repository = EXCLUDED.repository,
                emitter_kind = EXCLUDED.emitter_kind,
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
        // Denormalized out of the snapshot so a cohort filter is an indexed column comparison
        // rather than a jsonb scan over every row the window contains.
        cmd.Parameters.Add(Nullable(session.Repository));
        cmd.Parameters.AddWithValue(session.EmitterKind.ToString());
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
    /// <summary>
    /// Internal Copilot helper sessions (title generation, summarization). Excluded in SQL
    /// rather than after the LIMIT: filtering afterwards under-fills every page and, once
    /// the offset passes the number of user sessions in the fetched slice, returns nothing
    /// at all — making older history unreachable through the API.
    /// </summary>
    private const string InternalKindFilter =
        "session_kind NOT IN ('InternalTitleGeneration','InternalSummary','InternalHelper')";

    /// <summary>
    /// Cohort predicate, appended to both the page query and its count so the pager can never
    /// report a total it cannot page to.
    ///
    /// Every clause is a no-op when its parameter is null, which keeps one SQL string for all
    /// 32 combinations of filters instead of a builder nobody can read. The model clause uses
    /// <c>jsonb_exists</c> rather than the <c>?</c> operator: <c>?</c> is legal jsonb syntax
    /// and legal parameter syntax, and which one a driver decides it is has bitten enough
    /// people to be worth avoiding.
    /// </summary>
    private const string CohortPredicate = """
              AND ($5::text IS NULL OR lower(repository) = lower($5))
              AND ($6::text IS NULL OR emitter_kind = $6)
              AND ($7::text IS NULL OR session_kind = $7)
              AND ($8::text IS NULL OR lower(quality_grade) = lower($8))
              AND ($9::text IS NULL OR jsonb_exists(snapshot->'modelCalls', $9))
        """;

    /// <summary>Binds the five cohort parameters in the order <see cref="CohortPredicate"/>
    /// reads them. Callers must have bound $1..$4 already.</summary>
    private static void BindCohort(NpgsqlCommand cmd, CohortFilter cohort)
    {
        cmd.Parameters.Add(Nullable(cohort.Repository));
        cmd.Parameters.Add(Nullable(cohort.Emitter?.ToString()));
        cmd.Parameters.Add(Nullable(cohort.Kind?.ToString()));
        cmd.Parameters.Add(Nullable(cohort.Grade));
        cmd.Parameters.Add(Nullable(cohort.Model));
    }

    public async Task<List<PersistedSession>> QueryAsync(
        DateTimeOffset? since, DateTimeOffset? until, int limit, int offset,
        CancellationToken ct, bool includeInternal = false, CohortFilter? cohort = null)
    {
        var result = new List<PersistedSession>();
        await using var cmd = _dataSource.CreateCommand($"""
            SELECT snapshot FROM sessions
            WHERE ($1::timestamptz IS NULL OR last_seen >= $1)
              AND ($2::timestamptz IS NULL OR last_seen <= $2)
              {(includeInternal ? "" : $"AND {InternalKindFilter}")}
            {CohortPredicate}
            ORDER BY last_seen DESC
            LIMIT $3 OFFSET $4;
            """);
        cmd.Parameters.Add(Nullable(since));
        cmd.Parameters.Add(Nullable(until));
        cmd.Parameters.AddWithValue(limit);
        cmd.Parameters.AddWithValue(offset);
        BindCohort(cmd, cohort ?? CohortFilter.None);
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
    public async Task<int> CountAsync(DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct,
        bool includeInternal = false, CohortFilter? cohort = null)
    {
        // Must apply the same filter as QueryAsync, or the pager reports a total it can
        // never page to. $3/$4 are unused here and bound to keep the cohort parameter
        // positions identical to the page query — one predicate, one binding order.
        await using var cmd = _dataSource.CreateCommand($"""
            SELECT count(*) FROM sessions
            WHERE ($1::timestamptz IS NULL OR last_seen >= $1)
              AND ($2::timestamptz IS NULL OR last_seen <= $2)
              AND ($3::int IS NOT NULL) AND ($4::int IS NOT NULL)
              {(includeInternal ? "" : $"AND {InternalKindFilter}")}
            {CohortPredicate};
            """);
        cmd.Parameters.Add(Nullable(since));
        cmd.Parameters.Add(Nullable(until));
        cmd.Parameters.AddWithValue(0);
        cmd.Parameters.AddWithValue(0);
        BindCohort(cmd, cohort ?? CohortFilter.None);
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

    /// <summary>Typed null for an absent text filter. The type has to be explicit, or Npgsql
    /// cannot infer it for a DBNull and the <c>$n::text IS NULL</c> no-op clause fails.</summary>
    private static NpgsqlParameter Nullable(string? value) => new()
    {
        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
        Value = value is null ? DBNull.Value : value
    };

    /// <summary>Deletes a session. Returns rows removed, so a caller can tell whether the
    /// session existed at all — it may live only here, not in the in-memory working set.</summary>
    public async Task<int> DeleteAsync(string id, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM sessions WHERE id = $1;");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct);
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
