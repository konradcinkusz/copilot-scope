using CopilotScope.Collector.Api;
using CopilotScope.Collector.Privacy;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Alerting;

/// <summary>
/// Evaluates the regression condition on a timer and sends the weekly digest.
///
/// <para>Registered only when alerts are configured, so a deployment that has not opted in runs
/// no extra loop and holds no HttpClient pointed anywhere.</para>
/// </summary>
public sealed class AlertService(
    SessionQueryService sessions,
    QualityEngine quality,
    AlertOptions options,
    AlertDispatcher dispatcher,
    PrivacyGuard privacy,
    ILogger<AlertService> logger) : BackgroundService
{
    /// <summary>Cohort → when it last fired, so an hourly check over a week-long window does
    /// not re-send the same regression 168 times.</summary>
    private readonly Dictionary<(string, string), DateTimeOffset> _lastFired = [];

    private DateTimeOffset? _lastDigest;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A first evaluation immediately at startup would alert on a window the collector has
        // only just begun filling, so the loop waits one interval before its first check.
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(options.CheckInterval, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                await CheckRegressionsAsync(ct);
                if (options.Digest) await MaybeSendDigestAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Alerting must never take ingest down with it.
                logger.LogWarning(ex, "Alert evaluation failed; will retry next interval.");
            }
        }
    }

    /// <summary>Both windows and their rollups — shared by the regression check and the digest
    /// so the two cannot disagree about what the week contained.</summary>
    private async Task<(CohortReport Current, CohortReport Baseline, DateTimeOffset Since, DateTimeOffset Until, bool Allowed)>
        WindowsAsync(CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow;
        var since = until.AddDays(-options.WindowDays);
        var baselineSince = since.AddDays(-options.WindowDays);

        var current = await sessions.AllInWindowAsync(since, ct, until);
        var baseline = await sessions.AllInWindowAsync(baselineSince, ct, since);

        // The aggregation floor applies to an outbound message exactly as it applies to a
        // screen — arguably more, since the message leaves the deployment.
        var allowed = privacy.Evaluate(current.Concat(baseline)).Allowed;

        return (Cohorts.Build(current, quality, since, until),
                Cohorts.Build(baseline, quality, baselineSince, since),
                since, until, allowed);
    }

    private async Task CheckRegressionsAsync(CancellationToken ct)
    {
        var (current, baseline, since, until, allowed) = await WindowsAsync(ct);
        if (!allowed)
        {
            logger.LogDebug("Regression check withheld by the k-anonymity floor.");
            return;
        }

        var regressions = RegressionDetector.Detect(baseline, current, options);
        var now = DateTimeOffset.UtcNow;

        var fresh = regressions
            .Where(r => !_lastFired.TryGetValue((r.Dimension, r.Value), out var last)
                        || now - last >= options.Cooldown)
            .ToList();
        if (fresh.Count == 0) return;

        foreach (var r in fresh) _lastFired[(r.Dimension, r.Value)] = now;

        var text = "*CopilotScope: quality regression*\n" +
                   string.Join('\n', fresh.Select(r => "• " + r.Headline));

        await dispatcher.SendAsync("regression",
            new { windowDays = options.WindowDays, since, until, regressions = fresh }, text, ct);
    }

    private async Task MaybeSendDigestAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now.DayOfWeek != options.DigestDay || now.Hour != options.DigestHourUtc) return;
        // Hour-granular scheduling with a sub-hour check interval would otherwise send the
        // digest once per tick for a whole hour.
        if (_lastDigest is { } last && now - last < TimeSpan.FromHours(23)) return;
        _lastDigest = now;

        var (current, baseline, since, until, allowed) = await WindowsAsync(ct);
        if (!allowed)
        {
            logger.LogInformation("Weekly digest withheld by the k-anonymity floor.");
            return;
        }

        var report = Digest.Build(current, baseline,
            RegressionDetector.Detect(baseline, current, options), since, until);
        await dispatcher.SendAsync("digest", report, Digest.ToText(report), ct);
    }
}
