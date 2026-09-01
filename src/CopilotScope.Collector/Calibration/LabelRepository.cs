using Npgsql;

namespace CopilotScope.Collector.Calibration;

/// <summary>
/// Durable store for human labels.
///
/// Labels are the most expensive data this project holds — a rater's minute per session, and a
/// study needs dozens of sessions across at least two raters before κ means anything. Losing an
/// afternoon of that to a collector restart would end the study, so unlike almost everything
/// else here the write is awaited rather than queued: a human clicking Save is a write rate
/// that can afford a round trip, and "saved" has to mean saved.
/// </summary>
public sealed class LabelRepository(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        // The primary key is (session, rater, rubric): a rater revising their own judgment is
        // the normal reason for a second write, and keeping both would record a person as
        // disagreeing with themselves. `level` is nullable because a skip is a real answer.
        const string ddl = """
            CREATE TABLE IF NOT EXISTS session_labels (
                session_id text NOT NULL,
                rater      text NOT NULL,
                algorithm  text NOT NULL,
                level      int,
                note       text,
                at         timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (session_id, rater, algorithm)
            );
            CREATE INDEX IF NOT EXISTS ix_session_labels_session ON session_labels (session_id);
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(SessionLabel label, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO session_labels (session_id, rater, algorithm, level, note, at)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (session_id, rater, algorithm) DO UPDATE SET
                level = EXCLUDED.level,
                note  = EXCLUDED.note,
                at    = EXCLUDED.at;
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(label.SessionId);
        cmd.Parameters.AddWithValue(label.Rater);
        cmd.Parameters.AddWithValue(label.Algorithm);
        cmd.Parameters.Add(new NpgsqlParameter
        { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = (object?)label.Level ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter
        { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)label.Note ?? DBNull.Value });
        cmd.Parameters.AddWithValue(label.At);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<SessionLabel>> AllAsync(CancellationToken ct)
    {
        var labels = new List<SessionLabel>();
        await using var cmd = _dataSource.CreateCommand(
            "SELECT session_id, rater, algorithm, level, note, at FROM session_labels;");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            labels.Add(new SessionLabel(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        return labels;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
