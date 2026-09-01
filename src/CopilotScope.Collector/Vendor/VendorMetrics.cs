namespace CopilotScope.Collector.Vendor;

/// <summary>
/// Pull-based vendor usage archiving, bound from <c>CopilotScope:VendorMetrics</c>.
///
/// <para><b>What this is, and what it is not.</b> GitHub's Copilot Metrics API returns a
/// 28-day rolling window and nothing older; org admins have been asking for history since it
/// shipped, and the most-used tool in this space had to add a database purely to keep it. This
/// archives that window before it evaporates.</para>
///
/// <para>It is <i>not</i> a pivot to a usage dashboard. This project's whole thesis is that
/// counting AI usage does not tell you whether AI is helping — the session quality score
/// remains the product, and archived vendor counts are context beside it, never instead of it.
/// What the archive buys is a baseline: "seats went up 40% in March" is the sentence that gives
/// a quality trend its denominator, and it is also the one thing this tool can deliver on day
/// one to an org that has Copilot seats and no OTLP instrumentation anywhere.</para>
///
/// <para>Off by default and org-level only. No per-developer breakdown is fetched, stored or
/// displayed — GitHub's API can be asked for one, and this deliberately does not ask.</para>
/// </summary>
public sealed class VendorMetricsOptions
{
    public bool Enabled { get; set; }

    /// <summary>Which connector to run. Only <c>github</c> exists today; the interface exists so
    /// Anthropic's and Cursor's admin APIs can follow without reshaping anything.</summary>
    public string Provider { get; set; } = "github";

    /// <summary>Organization login to poll. Mutually exclusive with <see cref="Enterprise"/>.</summary>
    public string Organization { get; set; } = "";

    /// <summary>Enterprise slug to poll, for an enterprise-wide rollup.</summary>
    public string Enterprise { get; set; } = "";

    /// <summary>
    /// API token. Needs exactly one read scope — <c>manage_billing:copilot</c>, or
    /// <c>read:org</c> / <c>read:enterprise</c> for the metrics endpoints. Nothing here writes,
    /// so a token with write scopes is a token with more blast radius than the feature has use
    /// for. See docs/TUTORIAL.md §12.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>API root. Overridable for GitHub Enterprise Server.</summary>
    public string BaseUrl { get; set; } = "https://api.github.com";

    /// <summary>How often to poll. Daily is the resolution of the data itself; polling more
    /// often re-fetches the same numbers and spends someone's rate limit on nothing.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Export the archive as Prometheus families, so Grafana can chart past 28 days.</summary>
    public bool Prometheus { get; set; } = true;

    public bool Active => Enabled
        && !string.IsNullOrWhiteSpace(Token)
        && (!string.IsNullOrWhiteSpace(Organization) || !string.IsNullOrWhiteSpace(Enterprise));

    /// <summary>The scope being archived, as stored: <c>org:acme</c> or <c>enterprise:acme</c>.</summary>
    public string Scope => !string.IsNullOrWhiteSpace(Enterprise)
        ? $"enterprise:{Enterprise.Trim()}"
        : $"org:{Organization.Trim()}";

    public string Describe() => Active
        ? $"{Provider} {Scope}, every {PollInterval.TotalHours:0}h"
        : Enabled ? "enabled but missing a token or a scope" : "off";
}

/// <summary>
/// One day of vendor usage for one scope.
///
/// <para><b>The raw document is stored verbatim alongside the extracted numbers, and that is
/// the point of an archiver.</b> The vendor deletes this data after 28 days; if the payload
/// grows a field next month — agent activity breakouts, a new surface — a parser that only kept
/// what it understood today would have silently thrown away history that cannot be re-fetched.
/// Extract for querying, keep everything for later.</para>
/// </summary>
public sealed record VendorMetricsDay(
    string Provider, string Scope, DateOnly Day,
    int TotalActiveUsers, int TotalEngagedUsers,
    int CompletionsEngagedUsers, int ChatEngagedUsers,
    int DotcomChatEngagedUsers, int PullRequestEngagedUsers,
    string RawJson);

/// <summary>
/// A pull source of vendor usage data.
///
/// Deliberately narrow: fetch a window, return days. Everything else — scheduling, idempotent
/// storage, the API and the Prometheus export — is shared, so adding Anthropic's or Cursor's
/// admin API later is one class implementing this and nothing else.
/// </summary>
public interface IVendorMetricsSource
{
    /// <summary>Provider id, stored on every row.</summary>
    string Provider { get; }

    /// <summary>Fetches whatever window the vendor exposes. Implementations return days newest
    /// or oldest first as they please; the caller sorts.</summary>
    Task<IReadOnlyList<VendorMetricsDay>> FetchAsync(CancellationToken ct);
}

/// <summary>
/// What the Prometheus exporter needs to know about the archive, refreshed by the archiver.
///
/// A snapshot rather than a repository handle: /metrics is scraped every few seconds and must
/// never turn into a database query per scrape, and the numbers here change once a day.
/// </summary>
public sealed class VendorMetricsSnapshot
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "github";
    public string Scope { get; set; } = "";

    /// <summary>Days held in the archive.</summary>
    public int DaysArchived { get; set; }

    /// <summary>The most recent archived day, or null before the first successful poll.</summary>
    public VendorMetricsDay? Latest { get; set; }
}
