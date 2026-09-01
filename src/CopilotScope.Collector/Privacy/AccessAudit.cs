using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CopilotScope.Collector.Privacy;

/// <summary>One recorded read of session data.</summary>
/// <param name="At">When the read happened (UTC).</param>
/// <param name="Actor">Who read it — a dashboard sign-in role, or a credential fingerprint.</param>
/// <param name="Action">What was read: <c>sessions.list</c>, <c>sessions.detail</c>, <c>overview</c>, <c>audit.export</c>…</param>
/// <param name="Target">The session id or query the read covered, where there is one.</param>
/// <param name="Outcome">Whether the data was served, withheld by the aggregation floor, or refused.</param>
public sealed record AccessAuditEntry(
    DateTimeOffset At, string Actor, string Action, string? Target, string Outcome);

/// <summary>
/// Append-only record of who looked at session data.
///
/// The other privacy controls are promises about what the system will not do. This is the
/// one that lets someone check — which is the difference between a works agreement a
/// council signs and one it argues about. GDPR Art. 5(2) puts the burden of demonstrating
/// compliance on the controller, and "our tool suppresses individual views" is a claim, not
/// a demonstration; an export showing that nobody queried outside the agreed pattern is.
///
/// Writes are on the read path of a live API, so they are cheap and non-blocking: an
/// in-memory ring always holds the recent tail, and a Postgres sink (when configured)
/// drains the queue in the background for the durable record. Losing an audit entry must
/// never fail the request that produced it — a hard-failing audit log is an outage waiting
/// for a database blip — so the sink is best-effort and its failures are logged loudly.
/// </summary>
public sealed class AccessAuditLog(PrivacyOptions options, ILogger<AccessAuditLog>? logger = null)
{
    private readonly ConcurrentQueue<AccessAuditEntry> _recent = new();
    private readonly ConcurrentQueue<AccessAuditEntry> _pending = new();
    private long _recorded;

    public bool Enabled => options.Enabled && options.AuditLog;

    /// <summary>Total entries recorded since startup, including those already drained.</summary>
    public long Recorded => Interlocked.Read(ref _recorded);

    public void Record(string actor, string action, string? target, string outcome)
    {
        if (!Enabled) return;
        var entry = new AccessAuditEntry(DateTimeOffset.UtcNow, actor, action, target, outcome);

        _recent.Enqueue(entry);
        _pending.Enqueue(entry);
        Interlocked.Increment(ref _recorded);

        while (_recent.Count > Cap) _recent.TryDequeue(out _);
        // The pending queue is drained by the writer; bound it anyway so a Postgres outage
        // cannot turn the audit log into the thing that exhausts the collector's memory.
        while (_pending.Count > Cap * 2)
        {
            _pending.TryDequeue(out _);
            logger?.LogWarning("Access audit sink is backed up — dropping the oldest queued entry.");
        }
    }

    /// <summary>The in-memory tail, newest first. Always available, even without Postgres.</summary>
    public IReadOnlyList<AccessAuditEntry> Recent(int limit = 200) =>
        _recent.Reverse().Take(Math.Clamp(limit, 1, Cap)).ToList();

    /// <summary>Ring size, floored: a misconfigured <c>AuditBufferSize: 0</c> must degrade to a
    /// small buffer, not to an exception on the read path or an audit log that keeps nothing.</summary>
    private int Cap => Math.Max(100, options.AuditBufferSize);

    /// <summary>Takes everything queued for the durable sink. Entries are removed, so a failed
    /// write must re-queue them rather than assume they are safe somewhere.</summary>
    public List<AccessAuditEntry> DrainPending()
    {
        var list = new List<AccessAuditEntry>();
        while (_pending.TryDequeue(out var entry)) list.Add(entry);
        return list;
    }

    /// <summary>Puts undelivered entries back at the tail after a sink failure.</summary>
    public void Requeue(IEnumerable<AccessAuditEntry> entries)
    {
        foreach (var entry in entries) _pending.Enqueue(entry);
    }

    /// <summary>
    /// Who is asking, in a form safe to store.
    ///
    /// The dashboard holds the collector's key and calls on a signed-in person's behalf, so
    /// the collector alone only ever sees one credential — "the dashboard read 400 sessions"
    /// is useless for an access log. The dashboard therefore forwards the signed-in role in
    /// <c>X-CopilotScope-Actor</c>, and this trusts it only from a caller that already holds
    /// a read credential: forging it requires the key that would have granted the read anyway.
    /// The fallback is a fingerprint of the credential, never the credential itself — an
    /// audit log full of live API keys is a breach with a retention policy.
    /// </summary>
    public static string ActorFor(HttpRequest request)
    {
        var forwarded = request.Headers["X-CopilotScope-Actor"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return Sanitize(forwarded);

        var key = request.Headers["x-api-key"].FirstOrDefault()
               ?? request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        if (!string.IsNullOrEmpty(key))
            return "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 4)).ToLowerInvariant();

        return "anonymous@" + (request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>Header values are attacker-controlled text that ends up in a log line and a
    /// CSV cell; keep them short and free of the characters that make either lie.</summary>
    private static string Sanitize(string value)
    {
        var clean = new string(value.Where(c => !char.IsControl(c) && c != ',' && c != '"').ToArray()).Trim();
        return clean.Length <= 120 ? clean : clean[..120];
    }

    /// <summary>RFC 4180 CSV, for handing to a DPO or a works council.</summary>
    public static string ToCsv(IEnumerable<AccessAuditEntry> entries)
    {
        var sb = new StringBuilder("timestamp,actor,action,target,outcome\n");
        foreach (var e in entries)
            sb.Append(e.At.UtcDateTime.ToString("O")).Append(',')
              .Append(Csv(e.Actor)).Append(',')
              .Append(Csv(e.Action)).Append(',')
              .Append(Csv(e.Target ?? "")).Append(',')
              .Append(Csv(e.Outcome)).Append('\n');
        return sb.ToString();
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
}
