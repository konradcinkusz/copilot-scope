using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Api;

/// <summary>One page of session history plus what it is a page of.</summary>
public sealed record SessionPage(
    List<SessionSummaryDto> Sessions,
    int Total,
    int Limit,
    int Offset,
    /// <summary>False when the collector is running without Postgres, so the caller knows
    /// the window is bounded by what memory holds rather than by the query.</summary>
    bool Durable);

/// <summary>
/// The read path for session history.
///
/// The in-memory <see cref="SessionStore"/> is capped, so it holds only the most recently
/// active sessions — a team churns past that cap in hours. Reading the API off memory alone
/// therefore made a team's history vanish shortly after it was written, even though every
/// session was safely in Postgres. This service reads from Postgres and overlays the live
/// aggregates on top, so the answer is both complete and current:
///
///   - Postgres supplies the window (paged, ordered by last activity).
///   - Live sessions override their stored row, because the write-behind flush lags by up
///     to a second and a session being typed into right now must not look stale.
///   - Live sessions missing from the page (created since the last flush) are added, so a
///     new conversation appears immediately instead of on the next flush.
///
/// Without Postgres the collector still runs; the same methods then serve memory alone and
/// say so via <see cref="SessionPage.Durable"/>.
/// </summary>
public sealed class SessionQueryService(
    SessionStore store,
    QualityEngine quality,
    HistoryOptions options,
    SessionRepository? repository = null)
{
    /// <summary>Deepest offset served. Paging is capped rather than unbounded because the
    /// merge below materializes limit+offset rows to keep the ordering honest.</summary>
    private const int MaxOffset = 10_000;

    public bool Durable => repository is not null;

    public async Task<SessionPage> PageAsync(bool includeInternal, DateTimeOffset? since, DateTimeOffset? until,
        int? limit, int? offset, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? options.DefaultPageSize, 1, options.MaxPageSize);
        var skip = Math.Clamp(offset ?? 0, 0, MaxOffset);

        var candidates = await CandidatesAsync(since, until, take + skip, ct);
        var visible = candidates
            .Where(s => includeInternal || !SessionClassifier.IsInternal(s.Kind))
            .OrderByDescending(s => s.LastSeen)
            .ToList();

        var page = visible.Skip(skip).Take(take).ToList();
        var baseline = await BaselineAsync(ct);
        var repoPools = RepoPools(visible, baseline);

        var dtos = page
            .Select(s => Dto.Summary(s, quality,
                s.Repository is { } repo && repoPools.TryGetValue(repo, out var pool) ? pool : baseline))
            .ToList();

        var total = repository is not null
            ? Math.Max(await repository.CountAsync(since, until, ct), visible.Count)
            : visible.Count;

        return new SessionPage(dtos, total, take, skip, Durable);
    }

    /// <summary>
    /// Finds a session by id, falling back to Postgres for one that has been trimmed from
    /// memory. A link to a week-old session has to keep working.
    /// </summary>
    public async Task<CopilotSession?> FindAsync(string id, CancellationToken ct)
    {
        if (store.Get(id) is { } live) return live;
        if (repository is null) return null;
        return (await repository.GetAsync(id, ct))?.ToSession();
    }

    /// <summary>
    /// Scores the percentile rank is computed against: user chats over a fixed trailing
    /// window. Fixed, rather than "whatever survives in memory", so a session's rank means
    /// the same thing from one day to the next — the number is only comparable if the
    /// population behind it is stable.
    /// </summary>
    public async Task<IReadOnlyList<double>> BaselineAsync(CancellationToken ct)
    {
        if (repository is not null)
        {
            var since = options.BaselineDays > 0
                ? DateTimeOffset.UtcNow.AddDays(-options.BaselineDays)
                : (DateTimeOffset?)null;
            try { return await repository.ScoresAsync(since, options.BaselineMaxSamples, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* Postgres hiccup — fall through to memory rather than dropping the rank. */ }
        }

        return store.All
            .Where(x => !SessionClassifier.IsInternal(x.Kind) && x.ChatCalls > 0)
            .Select(x => quality.Evaluate(x).Score)
            .ToList();
    }

    /// <summary>
    /// Every session in the window, for cross-session aggregates. Bounded by
    /// <see cref="HistoryOptions.BaselineMaxSamples"/> so an overview query on a long
    /// history stays a bounded amount of work rather than loading the whole table.
    /// </summary>
    public async Task<IReadOnlyCollection<CopilotSession>> AllInWindowAsync(DateTimeOffset? since, CancellationToken ct) =>
        await CandidatesAsync(since, null, options.BaselineMaxSamples, ct);

    /// <summary>All sessions in the window: the stored page, with live aggregates layered over it.</summary>
    private async Task<List<CopilotSession>> CandidatesAsync(
        DateTimeOffset? since, DateTimeOffset? until, int depth, CancellationToken ct)
    {
        var live = store.All.ToList();
        if (repository is null) return InWindow(live, since, until).ToList();

        List<CopilotSession> stored;
        try
        {
            stored = (await repository.QueryAsync(since, until, depth, 0, ct))
                .Select(p => p.ToSession()).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A database outage degrades the read path to memory, exactly as ingest degrades
            // to in-memory writes. A partial answer beats an error page.
            return InWindow(live, since, until).ToList();
        }

        var byId = new Dictionary<string, CopilotSession>(StringComparer.Ordinal);
        foreach (var s in stored) byId[s.Id] = s;
        foreach (var s in InWindow(live, since, until)) byId[s.Id] = s; // live wins over stored
        return byId.Values.ToList();
    }

    private static IEnumerable<CopilotSession> InWindow(
        IEnumerable<CopilotSession> sessions, DateTimeOffset? since, DateTimeOffset? until) =>
        sessions.Where(s => (since is null || s.LastSeen >= since) && (until is null || s.LastSeen <= until));

    /// <summary>
    /// Per-repo score pools, so a session ranks against its own repository when that repo has
    /// enough history to mean anything. Below three peers the pool is noise and the shared
    /// baseline is the better comparison.
    /// </summary>
    private Dictionary<string, IReadOnlyList<double>> RepoPools(
        List<CopilotSession> sessions, IReadOnlyList<double> fallback)
    {
        var pools = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        foreach (var group in sessions
            .Where(s => s.Repository is not null && !SessionClassifier.IsInternal(s.Kind) && s.ChatCalls > 0)
            .GroupBy(s => s.Repository!, StringComparer.Ordinal))
        {
            var scores = group.Select(s => quality.Evaluate(s).Score).ToList();
            pools[group.Key] = scores.Count >= 3 ? scores : fallback;
        }
        return pools;
    }
}
