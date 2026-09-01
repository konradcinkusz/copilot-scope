using Npgsql;

namespace CopilotScope.Collector.Vendor;

/// <summary>
/// Durable home for archived vendor usage.
///
/// <para>Its own table, and deliberately outside the session retention sweep: the whole reason
/// this exists is that the vendor throws the data away after 28 days, so a retention policy
/// written for session snapshots must not reach it. Deleting the archive would be deleting the
/// only copy.</para>
/// </summary>
public sealed class VendorMetricsRepository(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        // The primary key is (provider, scope, day), which is what makes a re-poll idempotent:
        // the API returns the same 28 days every time, and a run must overwrite rather than
        // accumulate 28 duplicates a day.
        const string ddl = """
            CREATE TABLE IF NOT EXISTS vendor_metrics (
                provider                  text NOT NULL,
                scope                     text NOT NULL,
                day                       date NOT NULL,
                total_active_users        int NOT NULL DEFAULT 0,
                total_engaged_users       int NOT NULL DEFAULT 0,
                completions_engaged_users int NOT NULL DEFAULT 0,
                chat_engaged_users        int NOT NULL DEFAULT 0,
                dotcom_chat_engaged_users int NOT NULL DEFAULT 0,
                pr_engaged_users          int NOT NULL DEFAULT 0,
                raw                       jsonb NOT NULL,
                archived_at               timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (provider, scope, day)
            );
            CREATE INDEX IF NOT EXISTS ix_vendor_metrics_day ON vendor_metrics (provider, scope, day DESC);
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Stores a fetched window. Returns how many rows were new, so a run can report
    /// "archived 1 new day" rather than "archived 28 days" every single time.</summary>
    public async Task<int> UpsertAsync(IReadOnlyList<VendorMetricsDay> days, CancellationToken ct)
    {
        var inserted = 0;
        foreach (var day in days)
        {
            const string sql = """
                INSERT INTO vendor_metrics (provider, scope, day, total_active_users, total_engaged_users,
                    completions_engaged_users, chat_engaged_users, dotcom_chat_engaged_users,
                    pr_engaged_users, raw, archived_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, now())
                ON CONFLICT (provider, scope, day) DO UPDATE SET
                    total_active_users = EXCLUDED.total_active_users,
                    total_engaged_users = EXCLUDED.total_engaged_users,
                    completions_engaged_users = EXCLUDED.completions_engaged_users,
                    chat_engaged_users = EXCLUDED.chat_engaged_users,
                    dotcom_chat_engaged_users = EXCLUDED.dotcom_chat_engaged_users,
                    pr_engaged_users = EXCLUDED.pr_engaged_users,
                    raw = EXCLUDED.raw
                RETURNING (xmax = 0) AS was_insert;
                """;
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue(day.Provider);
            cmd.Parameters.AddWithValue(day.Scope);
            cmd.Parameters.AddWithValue(day.Day);
            cmd.Parameters.AddWithValue(day.TotalActiveUsers);
            cmd.Parameters.AddWithValue(day.TotalEngagedUsers);
            cmd.Parameters.AddWithValue(day.CompletionsEngagedUsers);
            cmd.Parameters.AddWithValue(day.ChatEngagedUsers);
            cmd.Parameters.AddWithValue(day.DotcomChatEngagedUsers);
            cmd.Parameters.AddWithValue(day.PullRequestEngagedUsers);
            cmd.Parameters.Add(new NpgsqlParameter
            { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb, Value = day.RawJson });

            if (await cmd.ExecuteScalarAsync(ct) is true) inserted++;
        }
        return inserted;
    }

    /// <summary>The archive, newest first — including everything older than the vendor's own
    /// window, which is the entire point.</summary>
    public async Task<List<VendorMetricsDay>> ReadAsync(string provider, string scope, int days,
        CancellationToken ct)
    {
        const string sql = """
            SELECT provider, scope, day, total_active_users, total_engaged_users,
                   completions_engaged_users, chat_engaged_users, dotcom_chat_engaged_users,
                   pr_engaged_users, raw::text
            FROM vendor_metrics
            WHERE provider = $1 AND scope = $2
            ORDER BY day DESC
            LIMIT $3;
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(provider);
        cmd.Parameters.AddWithValue(scope);
        cmd.Parameters.AddWithValue(Math.Clamp(days, 1, 3650));

        var results = new List<VendorMetricsDay>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new VendorMetricsDay(
                reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9)));
        return results;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
