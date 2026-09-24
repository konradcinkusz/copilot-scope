using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Write-behind persistence: OTLP ingest marks sessions dirty, a background loop
/// flushes their snapshots to the session repository (Postgres, or local files) at most
/// once per second, so bursts of telemetry batches don't turn into a write storm. On
/// startup it bootstraps the schema and rehydrates the in-memory store, so a collector
/// restart doesn't lose session history. A storage outage degrades to in-memory-only
/// (logged), it never blocks ingest.
/// </summary>
public sealed class PersistenceWriter(
    ISessionRepository repository,
    SessionStore store,
    QualityEngine quality,
    HistoryOptions history,
    ILogger<PersistenceWriter> logger) : BackgroundService
{
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.UtcNow;

    public void MarkDirty(IEnumerable<string> sessionIds)
    {
        lock (_lock) foreach (var id in sessionIds) _dirty.Add(id);
    }

    /// <summary>
    /// Merges the stored snapshot back into sessions that ingest recreated after a trim.
    /// Without this the next flush would replace a full session with the near-empty
    /// aggregate late telemetry just created — persisted history destroyed by a memory
    /// cap. MergeFrom is additive and de-duplicates turns by trace id, so repairing after
    /// the fact yields the same totals as never having evicted.
    /// </summary>
    private async Task RepairResurrectedAsync(CancellationToken ct)
    {
        foreach (var id in store.DrainResurrected())
        {
            if (store.Get(id) is not { } live) continue;
            try
            {
                if (await repository.GetAsync(id, ct) is not { } stored) continue;
                live.MergeFrom(stored.ToSession());
                MarkDirty([id]);
                logger.LogDebug("Rehydrated trimmed session {Id} before flush.", id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Losing the repair is bad, but flushing the empty aggregate over the stored
                // one is worse — leave it dirty-free this round so the snapshot survives.
                logger.LogWarning(ex, "Could not rehydrate trimmed session {Id}; skipping its flush.", id);
                lock (_lock) _dirty.Remove(id);
            }
        }
    }

    /// <summary>
    /// Deletes sessions past the configured retention window, in the database and in memory.
    /// No-op unless <see cref="HistoryOptions.RetentionDays"/> is set.
    /// </summary>
    private async Task SweepRetentionAsync(CancellationToken ct)
    {
        if (history.RetentionDays <= 0 || DateTimeOffset.UtcNow < _nextSweep) return;
        _nextSweep = DateTimeOffset.UtcNow + history.SweepInterval;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-history.RetentionDays);
        try
        {
            foreach (var id in await repository.IdsOlderThanAsync(cutoff, ct)) store.Remove(id);
            var deleted = await repository.DeleteOlderThanAsync(cutoff, ct);
            if (deleted > 0)
                logger.LogInformation("Retention sweep removed {Count} session(s) older than {Days} day(s).",
                    deleted, history.RetentionDays);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Retention sweep failed; will retry next interval."); }
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await repository.EnsureSchemaAsync(ct);
            var persisted = await repository.LoadAllAsync(limit: 200, ct);
            var restored = store.Rehydrate(persisted.Select(p => p.ToSession()));
            logger.LogInformation("Persistence ready — rehydrated {Count} session(s) from {Store}.",
                restored, repository.Description);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Store} unavailable at startup — continuing in-memory only, will retry on writes.",
                repository.Description);
        }

        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                // Both must run before the flush: repair fills in snapshots the memory cap
                // evicted, retention drops what the operator asked to expire.
                await RepairResurrectedAsync(ct);
                await SweepRetentionAsync(ct);
            }
            catch (OperationCanceledException) { break; }

            try { await FlushAsync(ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Writes what the last second left dirty before the process exits. Without it a normal
    /// shutdown — Ctrl+C on a laptop, <c>docker compose down</c> — dropped up to a second of
    /// telemetry: the loop above only ever flushes on its next tick, and there is no next tick.
    /// Bounded, because a store that has gone away must not hold the shutdown hostage.
    ///
    /// Idempotent: every caller shares the one final flush. A host can be stopped twice at once
    /// — WebApplicationFactory stops it while the application's own RunAsync, seeing the stop,
    /// stops it again and then disposes the container. The second call used to find nothing
    /// left to flush, return at once, and let the store be disposed under the write the first
    /// call was still making, which lost that session.
    /// </summary>
    public override Task StopAsync(CancellationToken ct)
    {
        lock (_lock) return _stopping ??= StopOnceAsync(ct);
    }

    private Task? _stopping;

    private async Task StopOnceAsync(CancellationToken ct)
    {
        // Stop the loop first, so the final flush is the only writer.
        await base.StopAsync(ct);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(FinalFlushTimeout);
        try { await FlushAsync(bounded.Token); }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown flush did not finish within {Timeout}; the newest changes may be lost.",
                FinalFlushTimeout);
        }
    }

    /// <summary>How long a shutdown waits for the final flush.</summary>
    private static readonly TimeSpan FinalFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Writes every dirty session once. A failed write is re-queued for the next
    /// pass; a cancelled one puts back everything it had not reached, so a shutdown that
    /// interrupts the loop mid-flush still hands the rest to the final flush.</summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        string[] ids;
        lock (_lock)
        {
            if (_dirty.Count == 0) return;
            ids = _dirty.ToArray();
            _dirty.Clear();
        }

        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            if (store.Get(id) is not { } session) continue;
            try
            {
                var report = quality.Evaluate(session);
                await repository.UpsertAsync(PersistedSession.From(session), report.Score, report.Grade, ct,
                    session.Kind.ToString());
            }
            catch (OperationCanceledException)
            {
                lock (_lock) foreach (var unwritten in ids[i..]) _dirty.Add(unwritten);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist session {Id} — re-queueing.", id);
                lock (_lock) _dirty.Add(id); // retry on next tick
            }
        }
    }
}
