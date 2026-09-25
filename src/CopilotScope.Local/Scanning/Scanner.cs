using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Persistence;

namespace CopilotScope.Local.Scanning;

/// <summary>How often the scanner looks, and how long a session has to be quiet first.</summary>
internal sealed record ScannerOptions
{
    /// <summary>Between two passes. A pass looks at every transcript's size and write time and
    /// reads only what changed, so looking once a minute costs next to nothing. No file-system
    /// watcher: a session is not read until it has been quiet for <see cref="Settle"/>, so
    /// hearing about a write the moment it happens would change nothing.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a session's files must go unwritten before it is imported. Telemetry has to win
    /// the race for a session it is reporting: an import never replaces a live session, but
    /// telemetry arriving after an import is added to it, and would count the same calls twice.
    /// Every exporter an assistant uses sends within a minute, so after ten quiet minutes a
    /// connected assistant's session is already live — and the import is refused — or there is
    /// no telemetry for it and the transcript is the only record there is.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>One import request at most, well under Kestrel's 30 MB default body limit.</summary>
    public int BatchBytes { get; init; } = 4 * 1024 * 1024;

    public int BatchSessions { get; init; } = 50;
}

/// <summary>What one pass found in one source.</summary>
internal sealed record SourceReport(
    string Source, int Found, int Imported, int Updated, int Live, int Unchanged, int Waiting, int Empty, int Failed)
{
    public string Describe(double settleMinutes)
    {
        if (Found == 0) return $"{Source}: no sessions found.";
        var parts = new List<string>();
        if (Imported > 0) parts.Add($"{Imported} imported");
        if (Updated > 0) parts.Add($"{Updated} updated");
        if (Live > 0) parts.Add($"{Live} already live from telemetry");
        if (Waiting > 0) parts.Add($"{Waiting} still active, imported once quiet for {settleMinutes:0} min");
        if (Unchanged > 0) parts.Add($"{Unchanged} unchanged");
        if (Empty > 0) parts.Add($"{Empty} with nothing to score");
        if (Failed > 0) parts.Add($"{Failed} not imported, see the log");
        return $"{Source}: {Found} session(s) — {string.Join(", ", parts)}.";
    }
}

internal sealed record ScanReport(IReadOnlyList<SourceReport> Sources, double SettleMinutes);

/// <summary>
/// Reads the history assistants keep on this machine and hands it to the collector
/// (ADR-004, decision 4): in the background, once a minute, and on request
/// (<c>copilotscope scan</c>).
///
/// Everything goes through <c>POST /api/import</c> over HTTP, like the importer, so the
/// endpoint's guards apply to it: a session must declare itself imported, and a session
/// telemetry has made live is never replaced. A session is read again only when its files
/// change or its reader does, and never while it is still being written. Files are only ever
/// read, and a deleted file never deletes a session.
/// </summary>
internal sealed class Scanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<IScanSource> _sources;
    private readonly ScanState _state;
    private readonly HttpClient _collector;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    // Read failures already logged, by source, session and fingerprint: a file that stays
    // unreadable is retried on every pass and reported once.
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private bool _deliveryFailing;
    private Task? _loop;

    public Scanner(IReadOnlyList<IScanSource> sources, ScanState state, HttpClient collector,
        ScannerOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _sources = sources;
        _state = state;
        _collector = collector;
        Options = options ?? new ScannerOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public ScannerOptions Options { get; }

    public IReadOnlyList<IScanSource> Sources => _sources;

    /// <summary>Scans now, then once every interval, until stopped.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stopping.Token));

    /// <summary>Stops the background loop, cancelling a pass in flight. What that pass already
    /// handed over is recorded; the rest is read again next time. Bounded: a read cannot be
    /// cancelled half-way, and one stuck on a hung file system must not keep the process alive.</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync();
        if (_loop is not { } loop) return;
        try { await loop.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { _logger.LogWarning("Stopped without waiting for a local-history read to finish."); }
    }

    /// <summary>One pass over every source, now — or right after the pass already running.</summary>
    public async Task<ScanReport> ScanNowAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            var reports = new List<SourceReport>(_sources.Count);
            foreach (var source in _sources) reports.Add(await ScanAsync(source, linked.Token));
            return new ScanReport(reports, Options.Settle.TotalMinutes);
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        for (var first = true; !ct.IsCancellationRequested; first = false)
        {
            try
            {
                var report = await ScanNowAsync(ct);
                // The first pass says what was found; later ones only speak when they did something.
                foreach (var source in report.Sources.Where(s => first || s.Imported + s.Updated > 0))
                    _logger.LogInformation("{Summary}", source.Describe(report.SettleMinutes));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Never the end of scanning: whatever broke this pass is tried again on the next.
                _logger.LogWarning(ex, "Scanning local history failed; trying again in {Seconds:0} s.",
                    Options.Interval.TotalSeconds);
            }

            try { await Task.Delay(Options.Interval, _time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<SourceReport> ScanAsync(IScanSource source, CancellationToken ct)
    {
        var discovery = source.Discover(ct);
        if (discovery.Complete)
            _state.Retain(source.Name, discovery.Units.Select(u => u.SessionId).ToHashSet(StringComparer.Ordinal));

        var now = _time.GetUtcNow();
        var tally = new Tally();
        var ready = new List<ScanUnit>();
        foreach (var unit in discovery.Units)
        {
            if (_state.IsCurrent(source.Name, source.Version, unit.SessionId, unit.Fingerprint)) tally.Unchanged++;
            else if (now - unit.LastWrite < Options.Settle) tally.Waiting++;
            else ready.Add(unit);
        }

        var batch = new List<(ScanUnit Unit, byte[] Json)>();
        var batchBytes = 0;
        var delivering = true;

        // Newest first: on a first run over a long history, what someone is most likely to look
        // for is on the dashboard first.
        foreach (var unit in ready.OrderByDescending(u => u.LastWrite))
        {
            ct.ThrowIfCancellationRequested();

            PersistedSession? session;
            try { session = source.Read(unit); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Files another program owns can be locked, vanish or hold what no parser
                // expected. One session's trouble must not stop the ones after it — and with
                // newest first, a pass that gave up here would never reach the older history.
                if (_reported.Add($"{source.Name}\n{unit.SessionId}\n{unit.Fingerprint}"))
                {
                    if (ex is IOException or UnauthorizedAccessException)
                        _logger.LogWarning("{Source}: could not read session {Session} ({Error}); it is tried again on every pass.",
                            source.DisplayName, unit.SessionId, ex.Message);
                    else
                        _logger.LogWarning(ex, "{Source}: could not read session {Session}; it is tried again on every pass.",
                            source.DisplayName, unit.SessionId);
                }
                continue;
            }

            if (session is null)
            {
                tally.Empty++;
                _state.Record(source.Name, source.Version, unit.SessionId, unit.Fingerprint);
                continue;
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(session, Json);
            if (batch.Count > 0 && (batch.Count >= Options.BatchSessions || batchBytes + json.Length > Options.BatchBytes))
            {
                if (!(delivering = await DeliverAsync(source, batch, tally, ct))) break;
                batch.Clear();
                batchBytes = 0;
            }
            batch.Add((unit, json));
            batchBytes += json.Length;
        }
        if (delivering && batch.Count > 0) await DeliverAsync(source, batch, tally, ct);

        // Ready, and neither handed over nor empty: unreadable, refused, or not delivered.
        var failed = ready.Count - tally.Delivered - tally.Empty;
        Save();
        return new SourceReport(source.DisplayName, discovery.Units.Count, tally.Imported, tally.Updated, tally.Live,
            tally.Unchanged, tally.Waiting, tally.Empty, failed);
    }

    /// <summary>Hands one batch to the collector. False when it could not be delivered at all,
    /// which ends the pass: the next batch would meet the same collector.</summary>
    private async Task<bool> DeliverAsync(IScanSource source, List<(ScanUnit Unit, byte[] Json)> batch, Tally tally,
        CancellationToken ct)
    {
        HttpStatusCode? status = null;
        ImportResult? result = null;
        string? problem = null;
        try
        {
            using var content = new ByteArrayContent(Body(batch));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _collector.PostAsync("/api/import", content, ct);
            status = response.StatusCode;
            if (response.IsSuccessStatusCode) result = await response.Content.ReadFromJsonAsync<ImportResult>(Json, ct);
            else problem = response.StatusCode == HttpStatusCode.Unauthorized
                ? "the collector wants an API key for imports, and none it accepts was sent"
                : $"the collector answered {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (HttpRequestException ex) { problem = ex.Message; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { problem = "the collector did not answer in time"; }
        catch (JsonException ex) { problem = $"the collector's answer could not be read ({ex.Message})"; }

        if (result is null)
        {
            problem ??= "the collector answered without a result";
            // Refused for what it is rather than for where it went: take the batch apart, so one
            // session the collector cannot take does not hold back every session after it.
            if (status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
                or HttpStatusCode.UnprocessableEntity)
            {
                if (batch.Count > 1)
                {
                    foreach (var single in batch)
                        if (!await DeliverAsync(source, [single], tally, ct)) return false;
                    return true;
                }

                var unit = batch[0].Unit;
                _logger.LogWarning("{Source}: the collector refused session {Session} ({Problem}); it is tried again when its files change.",
                    source.DisplayName, unit.SessionId, problem);
                _state.Record(source.Name, source.Version, unit.SessionId, unit.Fingerprint);
                return true;
            }

            if (!_deliveryFailing)
                _logger.LogWarning("{Source}: could not hand {Count} session(s) to the collector: {Problem}. Trying again on the next pass.",
                    source.DisplayName, batch.Count, problem);
            _deliveryFailing = true;
            return false;
        }

        _deliveryFailing = false;
        tally.Imported += result.Imported;
        tally.Updated += result.Updated;
        tally.Live += result.Skipped;
        tally.Delivered += batch.Count;
        foreach (var (unit, _) in batch)
            _state.Record(source.Name, source.Version, unit.SessionId, unit.Fingerprint);
        // After every batch, so an interrupted first run over a long history resumes where it
        // stopped instead of starting over.
        Save();
        return true;
    }

    private void Save()
    {
        try { _state.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This process still knows what it handed over; only a restart would repeat work.
            _logger.LogWarning("Could not save the scan state ({Error}).", ex.Message);
        }
    }

    /// <summary><c>{"sessions":[…]}</c> from sessions serialized once, when they were measured.</summary>
    private static byte[] Body(List<(ScanUnit Unit, byte[] Json)> batch)
    {
        using var body = new MemoryStream();
        body.Write("{\"sessions\":["u8);
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) body.WriteByte((byte)',');
            body.Write(batch[i].Json);
        }
        body.Write("]}"u8);
        return body.ToArray();
    }

    private sealed class Tally
    {
        public int Imported, Updated, Live, Unchanged, Waiting, Empty, Delivered;
    }
}
