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
    bool Durable,
    /// <summary>Distinct origins the page covers, for the k-anonymity floor. Counted here
    /// because the page is where the caller's filters have already been applied — the floor
    /// has to be evaluated against what would actually be shown, not against all of history.
    /// Empty and unused when privacy mode is off.</summary>
    IReadOnlyCollection<string> Subjects,
    /// <summary>Set when the aggregation floor withheld the page; null when it was served.</summary>
    string? SuppressedReason = null)
{
    /// <summary>The same page with its contents withheld. Shape is preserved deliberately:
    /// a caller that cannot parse the response learns nothing, whereas an empty page plus a
    /// reason tells the UI exactly what to say and why.</summary>
    public SessionPage Suppressed(string reason) =>
        // Total goes too. "0 sessions shown of 412" is harmless; "0 of 412" for a window that
        // covers one person is a report about that person's week, which is the thing the floor
        // was withholding in the first place.
        this with { Sessions = [], Subjects = [], Total = 0, SuppressedReason = reason };
}

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
        int? limit, int? offset, CancellationToken ct, CohortFilter? cohort = null)
    {
        var take = Math.Clamp(limit ?? options.DefaultPageSize, 1, options.MaxPageSize);
        var skip = Math.Clamp(offset ?? 0, 0, MaxOffset);
        var filter = cohort ?? CohortFilter.None;

        // The database applies both the internal-session filter and the cohort itself, so the
        // page comes back the right size; the in-memory overlay still needs filtering here.
        // Both filters have to agree — a page narrowed in SQL and widened in memory would
        // return sessions the caller filtered out.
        var candidates = await CandidatesAsync(since, until, take + skip, ct, includeInternal, filter);
        var visible = candidates
            .Where(s => includeInternal || !SessionClassifier.IsInternal(s.Kind))
            .Where(s => filter.Matches(s, quality.Evaluate(s).Grade))
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
            ? Math.Max(await repository.CountAsync(since, until, ct, includeInternal, filter), visible.Count)
            : visible.Count;

        // Subjects of the page, not of the whole window: the floor answers "how many people
        // does this screen cover", and the screen is one page of one filtered query.
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in page) subjects.Add(s.SubjectId ?? $"unknown:{s.Id}");

        return new SessionPage(dtos, total, take, skip, Durable, subjects);
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

    // The overview is a whole-window aggregate — thousands of JSONB snapshots deserialized
    // and re-scored — and the dashboard polls it on a timer from every open tab. Without a
    // short shared cache, N tabs multiply that cost by N for an answer that cannot
    // meaningfully change between polls.
    private readonly SemaphoreSlim _overviewLock = new(1, 1);
    // Keyed by the cohort as well as the window: a cache that ignored the filter would serve
    // one team's repository rollup as another's, which is the worst kind of wrong number —
    // plausible.
    private (DateTimeOffset? Since, DateTimeOffset? Until, CohortFilter Cohort, DateTimeOffset At,
        IReadOnlyCollection<CopilotSession> Sessions)? _overviewCache;

    /// <summary>
    /// Every session in the window, for cross-session aggregates. Bounded by
    /// <see cref="HistoryOptions.OverviewMaxSessions"/> so a long history stays a bounded
    /// amount of work, and memoized briefly so concurrent viewers share one computation.
    /// </summary>
    public async Task<IReadOnlyCollection<CopilotSession>> AllInWindowAsync(DateTimeOffset? since,
        CancellationToken ct, DateTimeOffset? until = null, CohortFilter? cohort = null)
    {
        var filter = cohort ?? CohortFilter.None;
        var ttl = options.OverviewCacheSeconds;
        if (ttl <= 0) return await CandidatesAsync(since, until, options.OverviewMaxSessions, ct, cohort: filter);

        await _overviewLock.WaitAsync(ct);
        try
        {
            if (_overviewCache is { } cached
                && cached.Since == since && cached.Until == until && cached.Cohort == filter
                && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(ttl))
                return cached.Sessions;

            var sessions = await CandidatesAsync(since, until, options.OverviewMaxSessions, ct, cohort: filter);
            _overviewCache = (since, until, filter, DateTimeOffset.UtcNow, sessions);
            return sessions;
        }
        finally { _overviewLock.Release(); }
    }

    /// <summary>
    /// Distinct values a caller can filter on, so the UI offers the repositories and models
    /// that actually exist rather than a free-text box that silently matches nothing. Read
    /// from the same window the filters apply to.
    /// </summary>
    public async Task<(List<string> Repositories, List<string> Assistants, List<string> Models)>
        FacetsAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var sessions = await AllInWindowAsync(since, ct);
        return (
            sessions.Select(s => s.Repository).Where(r => !string.IsNullOrEmpty(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                    .Select(r => r!).ToList(),
            sessions.Select(s => s.EmitterKind.ToString()).Distinct(StringComparer.Ordinal)
                    .OrderBy(a => a, StringComparer.Ordinal).ToList(),
            sessions.SelectMany(s => s.ModelCalls.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>All sessions in the window: the stored page, with live aggregates layered over it.</summary>
    private async Task<List<CopilotSession>> CandidatesAsync(
        DateTimeOffset? since, DateTimeOffset? until, int depth, CancellationToken ct,
        bool includeInternal = false, CohortFilter? cohort = null)
    {
        var filter = cohort ?? CohortFilter.None;
        // Grade is the one cohort field this pass cannot evaluate without scoring, so it is
        // left to the caller that has a QualityEngine in hand; everything else narrows here.
        var live = store.All.Where(filter.MatchesExceptGrade).ToList();
        if (repository is null) return InWindow(live, since, until).ToList();

        List<CopilotSession> stored;
        try
        {
            stored = (await repository.QueryAsync(since, until, depth, 0, ct, includeInternal, filter))
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
