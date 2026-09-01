using System.Text.Json;

namespace CopilotScope.Collector.Vendor;

/// <summary>
/// Reads GitHub's Copilot Metrics API — the 28-day rolling window, archived before it expires.
///
/// <para>The response is an array of per-day objects. Parsing is deliberately forgiving: every
/// field is read defensively and a day that cannot be understood at all is skipped rather than
/// failing the run, because the alternative is that one unexpected shape costs a day of history
/// that cannot be re-fetched. The full document is stored regardless, so a field this parser
/// does not know about is still archived.</para>
/// </summary>
public sealed class GitHubCopilotMetricsSource(HttpClient http, VendorMetricsOptions options,
    ILogger<GitHubCopilotMetricsSource> logger) : IVendorMetricsSource
{
    public string Provider => "github";

    private string Path => string.IsNullOrWhiteSpace(options.Enterprise)
        ? $"/orgs/{Uri.EscapeDataString(options.Organization.Trim())}/copilot/metrics"
        : $"/enterprises/{Uri.EscapeDataString(options.Enterprise.Trim())}/copilot/metrics";

    public async Task<IReadOnlyList<VendorMetricsDay>> FetchAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.BaseUrl.TrimEnd('/') + Path);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.Token}");
        request.Headers.TryAddWithoutValidation("User-Agent", "CopilotScope");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // The body is not logged: an error response from a misconfigured proxy can echo the
            // request, and the request carries the token.
            logger.LogWarning("GitHub Copilot metrics returned {Status} for {Scope}. " +
                "403 usually means the token lacks manage_billing:copilot (or read:org/read:enterprise); " +
                "404 usually means the scope name is wrong or Copilot metrics are not enabled for it.",
                (int)response.StatusCode, options.Scope);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return Parse(json, options.Scope, logger);
    }

    /// <summary>Parses a metrics response. Static and public so the shape is testable without
    /// a network call or a token.</summary>
    public static IReadOnlyList<VendorMetricsDay> Parse(string json, string scope, ILogger? logger = null)
    {
        var days = new List<VendorMetricsDay>();

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Copilot metrics response was not JSON.");
            return days;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                logger?.LogWarning("Copilot metrics response was {Kind}, expected an array of days.",
                    document.RootElement.ValueKind);
                return days;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (Str(element, "date") is not { } dateText
                    || !DateOnly.TryParse(dateText, System.Globalization.CultureInfo.InvariantCulture, out var day))
                {
                    // A day with no date cannot be stored idempotently, and guessing one would
                    // corrupt the archive it is supposed to protect.
                    logger?.LogDebug("Skipped a metrics entry with no usable date.");
                    continue;
                }

                days.Add(new VendorMetricsDay(
                    "github", scope, day,
                    Int(element, "total_active_users"),
                    Int(element, "total_engaged_users"),
                    Engaged(element, "copilot_ide_code_completions"),
                    Engaged(element, "copilot_ide_chat"),
                    Engaged(element, "copilot_dotcom_chat"),
                    Engaged(element, "copilot_dotcom_pull_requests"),
                    // Verbatim, including anything this parser does not understand. The vendor
                    // deletes the original in 28 days; this is the only copy that will exist.
                    element.GetRawText()));
            }
        }

        return days;
    }

    private static int Engaged(JsonElement day, string surface) =>
        day.TryGetProperty(surface, out var s) && s.ValueKind == JsonValueKind.Object
            ? Int(s, "total_engaged_users")
            : 0;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var i) ? i : 0;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
