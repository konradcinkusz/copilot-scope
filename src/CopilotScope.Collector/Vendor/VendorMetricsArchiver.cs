namespace CopilotScope.Collector.Vendor;

/// <summary>
/// Polls the vendor on a timer and archives the window before it expires.
///
/// <para>Restart-safe by construction rather than by bookkeeping: the API returns the same 28
/// days on every call and storage is keyed by day, so a poll after any gap re-archives whatever
/// it can still see. A restart costs nothing; an outage longer than 28 days is the only thing
/// that loses history, which is also the only thing no design could prevent.</para>
/// </summary>
public sealed class VendorMetricsArchiver(
    IVendorMetricsSource source,
    VendorMetricsRepository repository,
    VendorMetricsOptions options,
    VendorMetricsCache cache,
    VendorMetricsSnapshot snapshot,
    ILogger<VendorMetricsArchiver> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken ct)
    {
        // Registered whenever Postgres is configured, active only when a scope and a token are.
        // Creating the table for a deployment that will never poll would put an empty
        // vendor_metrics in every schema and imply a feature that is not running.
        if (!options.Active)
        {
            await base.StartAsync(ct);
            return;
        }

        try { await repository.EnsureSchemaAsync(ct); }
        catch (Exception ex)
        {
            // Same posture as every optional table here: Postgres not being ready must not take
            // ingest down over a feature the operator opted into on the side.
            logger.LogError(ex, "Vendor metrics schema not ready — archiving will retry on its next poll.");
        }

        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Active) return;

        // Polls immediately: the window is already 28 days wide, so the first run has real
        // history to save and waiting a day to start would risk losing its oldest edge.
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Vendor metrics poll failed; will retry."); }

            try { await Task.Delay(options.PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var days = await source.FetchAsync(ct);
        if (days.Count == 0) return;

        try
        {
            var inserted = await repository.UpsertAsync(days, ct);
            cache.Note(days.Count, inserted);

            // Refreshed here rather than read per scrape: /metrics is hit every few seconds and
            // these numbers move once a day.
            var archived = await repository.ReadAsync(source.Provider, options.Scope, 3650, ct);
            snapshot.Enabled = true;
            snapshot.Provider = source.Provider;
            snapshot.Scope = options.Scope;
            snapshot.DaysArchived = archived.Count;
            snapshot.Latest = archived.FirstOrDefault();

            logger.LogInformation(
                "Archived {New} new day(s) of {Provider} usage for {Scope} ({Seen} in the vendor's window).",
                inserted, source.Provider, options.Scope, days.Count);
        }
        catch (Exception ex)
        {
            // Worth an error rather than a warning: every failed poll is a day closer to the
            // vendor deleting data that cannot be re-fetched.
            logger.LogError(ex, "Could not archive {Count} day(s) of vendor usage — this window " +
                "expires at the vendor in 28 days.", days.Count);
        }
    }
}

/// <summary>
/// What the last poll saw, for the health endpoint and the startup banner.
///
/// An archiver that quietly stops working looks exactly like an archiver with nothing new to
/// archive, and the difference only becomes visible 28 days later when the data is gone.
/// </summary>
public sealed class VendorMetricsCache
{
    private long _daysSeen;
    private long _daysArchived;
    private DateTimeOffset? _lastPoll;

    public void Note(int seen, int archived)
    {
        Interlocked.Exchange(ref _daysSeen, seen);
        Interlocked.Add(ref _daysArchived, archived);
        _lastPoll = DateTimeOffset.UtcNow;
    }

    public int LastWindowDays => (int)Interlocked.Read(ref _daysSeen);
    public long TotalArchived => Interlocked.Read(ref _daysArchived);
    public DateTimeOffset? LastPoll => _lastPoll;
}
