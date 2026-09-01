using Npgsql;

namespace CopilotScope.Collector.Outcomes;

/// <summary>
/// Storage for pull-request outcomes. A separate table from <c>sessions</c> on purpose:
/// outcomes arrive on their own schedule (a PR merges days after the session), are keyed by
/// repository rather than by conversation, and must survive a session being deleted.
/// </summary>
public sealed class OutcomeRepository(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS pull_request_outcomes (
                repository      text NOT NULL,
                number          int  NOT NULL,
                branch          text NOT NULL DEFAULT '',
                title           text NOT NULL DEFAULT '',
                opened_at       timestamptz NOT NULL,
                merged_at       timestamptz,
                closed_at       timestamptz,
                first_review_at timestamptz,
                additions       int NOT NULL DEFAULT 0,
                deletions       int NOT NULL DEFAULT 0,
                changed_files   int NOT NULL DEFAULT 0,
                reverted        boolean NOT NULL DEFAULT false,
                reverted_at     timestamptz,
                updated_at      timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (repository, number)
            );
            CREATE INDEX IF NOT EXISTS ix_pr_outcomes_repo_opened
                ON pull_request_outcomes (repository, opened_at DESC);
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Upserts an outcome. Timestamps are merged with COALESCE rather than overwritten:
    /// webhook deliveries arrive out of order and can be replayed, and a redelivered
    /// "opened" event must not erase the merge that already happened.
    /// </summary>
    public async Task UpsertAsync(PullRequestOutcome pr, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO pull_request_outcomes
                (repository, number, branch, title, opened_at, merged_at, closed_at,
                 first_review_at, additions, deletions, changed_files, reverted, reverted_at, updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13, now())
            ON CONFLICT (repository, number) DO UPDATE SET
                branch          = EXCLUDED.branch,
                title           = EXCLUDED.title,
                merged_at       = COALESCE(pull_request_outcomes.merged_at, EXCLUDED.merged_at),
                closed_at       = COALESCE(pull_request_outcomes.closed_at, EXCLUDED.closed_at),
                first_review_at = LEAST(
                    COALESCE(pull_request_outcomes.first_review_at, EXCLUDED.first_review_at),
                    COALESCE(EXCLUDED.first_review_at, pull_request_outcomes.first_review_at)),
                additions       = GREATEST(pull_request_outcomes.additions, EXCLUDED.additions),
                deletions       = GREATEST(pull_request_outcomes.deletions, EXCLUDED.deletions),
                changed_files   = GREATEST(pull_request_outcomes.changed_files, EXCLUDED.changed_files),
                reverted        = pull_request_outcomes.reverted OR EXCLUDED.reverted,
                reverted_at     = COALESCE(pull_request_outcomes.reverted_at, EXCLUDED.reverted_at),
                updated_at      = now();
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(pr.Repository);
        cmd.Parameters.AddWithValue(pr.Number);
        cmd.Parameters.AddWithValue(pr.Branch);
        cmd.Parameters.AddWithValue(pr.Title);
        cmd.Parameters.AddWithValue(pr.OpenedAt);
        cmd.Parameters.Add(Nullable(pr.MergedAt));
        cmd.Parameters.Add(Nullable(pr.ClosedAt));
        cmd.Parameters.Add(Nullable(pr.FirstReviewAt));
        cmd.Parameters.AddWithValue(pr.Additions);
        cmd.Parameters.AddWithValue(pr.Deletions);
        cmd.Parameters.AddWithValue(pr.ChangedFiles);
        cmd.Parameters.AddWithValue(pr.Reverted);
        cmd.Parameters.Add(Nullable(pr.RevertedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Candidate outcomes for one repository within a time window.</summary>
    public async Task<List<PullRequestOutcome>> ForRepositoryAsync(
        string repository, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        var result = new List<PullRequestOutcome>();
        await using var cmd = _dataSource.CreateCommand("""
            SELECT repository, number, branch, title, opened_at, merged_at, closed_at,
                   first_review_at, additions, deletions, changed_files, reverted, reverted_at
            FROM pull_request_outcomes
            WHERE repository = $1 AND opened_at >= $2 AND opened_at <= $3
            ORDER BY opened_at DESC;
            """);
        cmd.Parameters.AddWithValue(repository);
        cmd.Parameters.AddWithValue(since);
        cmd.Parameters.AddWithValue(until);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    /// <summary>Every outcome in a window, for the correlation export.</summary>
    public async Task<List<PullRequestOutcome>> AllAsync(DateTimeOffset since, int limit, CancellationToken ct)
    {
        var result = new List<PullRequestOutcome>();
        await using var cmd = _dataSource.CreateCommand("""
            SELECT repository, number, branch, title, opened_at, merged_at, closed_at,
                   first_review_at, additions, deletions, changed_files, reverted, reverted_at
            FROM pull_request_outcomes
            WHERE opened_at >= $1
            ORDER BY opened_at DESC LIMIT $2;
            """);
        cmd.Parameters.AddWithValue(since);
        cmd.Parameters.AddWithValue(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    private static PullRequestOutcome Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
        r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
        r.GetInt32(8), r.GetInt32(9), r.GetInt32(10),
        r.GetBoolean(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12));

    private static NpgsqlParameter Nullable(DateTimeOffset? value) => new()
    {
        NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
        Value = value.HasValue ? value.Value : DBNull.Value
    };

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
