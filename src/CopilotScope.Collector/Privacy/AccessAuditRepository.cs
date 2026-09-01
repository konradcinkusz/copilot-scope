using Npgsql;

namespace CopilotScope.Collector.Privacy;

/// <summary>
/// Durable sink for the access audit log.
///
/// Separate table, separate retention: the audit record has to outlive the sessions it
/// describes, or it cannot answer "who looked at the data you deleted last month" — which
/// is the question it exists for. Its own retention is deliberately long and independent of
/// <c>CopilotScope:History:RetentionDays</c>.
/// </summary>
public sealed class AccessAuditRepository(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS access_audit (
                id       bigserial PRIMARY KEY,
                at       timestamptz NOT NULL,
                actor    text NOT NULL,
                action   text NOT NULL,
                target   text,
                outcome  text NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_access_audit_at ON access_audit (at DESC);
            CREATE INDEX IF NOT EXISTS ix_access_audit_actor ON access_audit (actor, at DESC);
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Writes a drained batch in one round trip.</summary>
    public async Task AppendAsync(IReadOnlyList<AccessAuditEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY access_audit (at, actor, action, target, outcome) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var e in entries)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(e.At, NpgsqlTypes.NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(e.Actor, NpgsqlTypes.NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Action, NpgsqlTypes.NpgsqlDbType.Text, ct);
            if (e.Target is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(e.Target, NpgsqlTypes.NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Outcome, NpgsqlTypes.NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    /// <summary>Most recent entries first, for the export endpoint.</summary>
    public async Task<List<AccessAuditEntry>> RecentAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT at, actor, action, target, outcome
            FROM access_audit
            ORDER BY at DESC
            LIMIT $1;
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(Math.Clamp(limit, 1, 50_000));

        var list = new List<AccessAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AccessAuditEntry(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        return list;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
