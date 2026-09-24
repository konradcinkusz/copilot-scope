using CopilotScope.Collector.Api;

namespace CopilotScope.Collector.Persistence;

/// <summary>
/// The durable home of session snapshots, behind the capped in-memory
/// <see cref="Domain.SessionStore"/>.
///
/// Two implementations exist: <see cref="PostgresSessionRepository"/> for teams and shared
/// servers, and <see cref="FileSessionRepository"/>, one JSON file per session, for a single
/// machine (ADR-004). They must answer every query identically — the read path overlays live
/// sessions on whatever the store returns, and a store that filtered or ordered differently
/// would show a different history for the same data. The contract is written down here, once,
/// because one implementation states it in SQL and the other in C#, and neither reads the
/// other:
///
///   - "Newest first" orders by the session's last activity (<c>LastSeen</c>), descending.
///   - A time window bounds <c>LastSeen</c>, inclusive at both ends; a null bound is open.
///   - Internal helper sessions (title generation, summarization, other helpers) are excluded
///     unless the caller asks for them. <c>Unattributed</c> is not internal.
///   - The cohort filter applies before paging, and a count applies exactly the filter its
///     page query does — or a pager reports a total it cannot page to.
///
/// One difference predates this interface: Postgres matches a cohort's model id
/// case-sensitively (<c>jsonb_exists</c>), memory and files case-insensitively. Each emitter
/// reports its model ids with consistent casing, so in practice they agree, and the contract
/// suite pins exact-case matching only.
/// </summary>
public interface ISessionRepository : IAsyncDisposable
{
    /// <summary>Machine-readable store name, as <c>/api/health</c> reports it:
    /// <c>postgres</c> or <c>files</c>.</summary>
    string Kind { get; }

    /// <summary>Human-readable store name, for logs and the startup banner.</summary>
    string Description { get; }

    /// <summary>Creates what the store needs and takes ownership of it. Called once at
    /// startup. May throw; the collector then runs in memory and retries on writes.</summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Inserts or replaces the snapshot stored under <c>session.Id</c>. Score, grade
    /// and kind are kept beside the snapshot so queries can filter on them without
    /// deserializing it.</summary>
    Task UpsertAsync(PersistedSession session, double qualityScore, string qualityGrade,
        CancellationToken ct, string sessionKind = "UserChat");

    /// <summary>The most recently active sessions, newest first, internal kinds included:
    /// what startup rehydrates into memory.</summary>
    Task<List<PersistedSession>> LoadAllAsync(int limit, CancellationToken ct);

    /// <summary>One page of history, newest first.</summary>
    Task<List<PersistedSession>> QueryAsync(DateTimeOffset? since, DateTimeOffset? until, int limit, int offset,
        CancellationToken ct, bool includeInternal = false, CohortFilter? cohort = null);

    /// <summary>One session's snapshot, or null. For ids no longer held in memory.</summary>
    Task<PersistedSession?> GetAsync(string id, CancellationToken ct);

    /// <summary>How many sessions <see cref="QueryAsync"/> can page through with the same
    /// arguments.</summary>
    Task<int> CountAsync(DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct,
        bool includeInternal = false, CohortFilter? cohort = null);

    /// <summary>Quality scores of user chats that ran at least one call, newest first — the
    /// population the percentile baseline is computed over.</summary>
    Task<List<double>> ScoresAsync(DateTimeOffset? since, int limit, CancellationToken ct);

    /// <summary>Ids of sessions last seen before the cutoff — needed to evict them from memory
    /// too.</summary>
    Task<List<string>> IdsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>Deletes sessions last seen before the cutoff. Returns how many were removed.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>Deletes one session. Returns how many were removed, so a caller can tell
    /// whether it existed at all — it may live only here, not in memory.</summary>
    Task<int> DeleteAsync(string id, CancellationToken ct);

    /// <summary>Deletes every session whose id starts with the prefix, compared ordinally.
    /// Used to clear a seeded demo dataset before writing a fresh one.</summary>
    Task<int> DeleteByPrefixAsync(string prefix, CancellationToken ct);
}
