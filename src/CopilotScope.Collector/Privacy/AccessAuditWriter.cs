namespace CopilotScope.Collector.Privacy;

/// <summary>
/// Drains the in-memory audit queue into Postgres.
///
/// Separate from <see cref="Persistence.PersistenceWriter"/> on purpose: the audit record has
/// to keep being written when session persistence is degraded or switched off, because the
/// question it answers ("who read what") does not stop mattering when the thing being read
/// is only in memory.
/// </summary>
public sealed class AccessAuditWriter(
    AccessAuditRepository repository,
    AccessAuditLog log,
    ILogger<AccessAuditWriter> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public override async Task StartAsync(CancellationToken ct)
    {
        // Registered whenever Postgres is configured, active only when privacy mode turned the
        // audit log on. Creating the table for a deployment that will never write to it would
        // put an empty access_audit in every schema and imply a control that is not running.
        if (!log.Enabled)
        {
            await base.StartAsync(ct);
            return;
        }

        try { await repository.EnsureSchemaAsync(ct); }
        catch (Exception ex)
        {
            // Same posture as the session schema: an opt-in table that Postgres is not ready
            // for must not take ingest down. The tail stays in memory and the flush retries.
            logger.LogError(ex, "Access audit schema not ready — entries stay in memory until Postgres accepts them.");
        }

        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!log.Enabled) return;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }

            var batch = log.DrainPending();
            if (batch.Count == 0) continue;

            try { await repository.AppendAsync(batch, ct); }
            catch (OperationCanceledException) { log.Requeue(batch); break; }
            catch (Exception ex)
            {
                // Re-queue rather than drop: an audit entry that silently disappears on a
                // transient database error is worse than a late one, because nothing about
                // the export would say it is incomplete.
                log.Requeue(batch);
                logger.LogWarning(ex, "Could not write {Count} access audit entr{Suffix} — re-queued.",
                    batch.Count, batch.Count == 1 ? "y" : "ies");
            }
        }
    }
}
