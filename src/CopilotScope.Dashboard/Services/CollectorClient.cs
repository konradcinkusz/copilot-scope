using System.Net.Http.Json;

namespace CopilotScope.Dashboard.Services;

/// <summary>
/// Typed client for the collector's query API. Base address is resolved from Aspire
/// service discovery env vars (services__collector__http__0) with a config/localhost
/// fallback, so the app also runs standalone without the AppHost.
/// </summary>
public sealed class CollectorClient(HttpClient http)
{
    /// <summary>
    /// One page of session history. The collector serves this from Postgres with live
    /// sessions layered on top, so the list is no longer bounded by what its memory holds.
    /// </summary>
    public async Task<SessionPageDto> GetSessionPageAsync(bool includeInternal = false, int? days = null,
        int? limit = null, int? offset = null, CancellationToken ct = default)
    {
        var query = $"/api/sessions?includeInternal={includeInternal}"
            + (days is > 0 ? $"&days={days}" : "")
            + (limit is > 0 ? $"&limit={limit}" : "")
            + (offset is > 0 ? $"&offset={offset}" : "");
        return await http.GetFromJsonAsync<SessionPageDto>(query, ct)
            ?? new SessionPageDto([], 0, 0, 0, false);
    }

    public async Task<List<SessionSummaryDto>> GetSessionsAsync(bool includeInternal = false, CancellationToken ct = default)
        => (await GetSessionPageAsync(includeInternal, ct: ct)).Sessions;

    public async Task<SessionDetailDto?> GetSessionAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/api/sessions/{Uri.EscapeDataString(id)}", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<SessionDetailDto>(cancellationToken: ct);
    }

    public async Task<OverviewDto?> GetOverviewAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<OverviewDto>("/api/overview", ct); }
        catch { return null; }
    }

    public async Task<bool> DeleteSessionAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"/api/sessions/{Uri.EscapeDataString(id)}", ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<HealthDto?> GetHealthAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<HealthDto>("/api/health", ct); }
        catch { return null; }
    }
}

// --- DTOs mirroring CopilotScope.Collector.Api (deserialized with Web defaults) ---

/// <summary>One page of session history. <c>Durable</c> is false when the collector is
/// running without Postgres, i.e. the window is bounded by memory rather than by the query.</summary>
public sealed record SessionPageDto(
    List<SessionSummaryDto> Sessions, int Total, int Limit, int Offset, bool Durable);

public enum SessionKind
{
    UserChat,
    InternalTitleGeneration,
    InternalSummary,
    InternalHelper,
    Unattributed
}

public enum EmitterKind { Unknown, VSCode, CLI, ClaudeCode, Cursor, Cowork }

/// <summary>
/// Which quality components an assistant can never populate, mirroring the collector's
/// EmitterCoverage. Kept here so the rail can warn about mixed-assistant lists without a
/// round trip per render.
/// </summary>
public static class EmitterCoverage
{
    private static readonly Dictionary<EmitterKind, string[]> AlwaysPrior = new()
    {
        [EmitterKind.VSCode] = [],
        [EmitterKind.CLI] = ["acceptance", "feedback"],
        [EmitterKind.ClaudeCode] = ["feedback"],
        [EmitterKind.Cowork] = ["feedback"],
        [EmitterKind.Cursor] = ["acceptance", "feedback", "latency", "friction"],
    };

    public static IReadOnlyList<string> PriorComponents(EmitterKind emitter) =>
        AlwaysPrior.TryGetValue(emitter, out var names) ? names : [];

    /// <summary>
    /// True when a visible set spans assistants whose scores rest on different evidence.
    /// The composite renormalizes over the components that have data, so an 80 built on four
    /// components is not an 80 built on six — and reading them side by side is the obvious
    /// mistake. Sessions from assistants with identical coverage need no warning: a caveat
    /// shown everywhere is a caveat nobody reads.
    /// </summary>
    public static bool NeedsComparabilityCaveat(IEnumerable<EmitterKind> emitters) =>
        emitters.Where(e => e != EmitterKind.Unknown)
                .Select(e => string.Join(",", PriorComponents(e)))
                .Distinct()
                .Count() > 1;
}

/// <summary>Mirrors the collector's SessionMode — how the session was driven.</summary>
public enum SessionMode { Unknown, Interactive, SupervisedAgent, Autonomous }

public sealed record SessionSummaryDto(
    string Id, string? Agent, string? Repository, string? Branch,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    long InputTokens, long OutputTokens, long CacheReadTokens,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    int AgentInvocations, int Turns,
    int EditsAccepted, int EditsRejected, int ThumbsUp, int ThumbsDown,
    double LinesAdded, double LinesRemoved,
    double TtftP50Ms, double TtftP95Ms,
    Dictionary<string, int> Models,
    QualityReportDto Quality,
    SessionKind Kind,
    EmitterKind EmitterKind = EmitterKind.Unknown,
    /// <summary>Edits applied under a permission mode rather than by a human decision.</summary>
    int EditsAutoAccepted = 0);

public sealed record SessionDetailDto(
    SessionSummaryDto Summary,
    List<ToolStatDto> Tools,
    Dictionary<string, int> ErrorTypes,
    List<SessionEventDto> Events,
    List<TranscriptEntryDto> Transcript,
    TurnAnalysisDto Turns,
    List<InsightReportDto> Insights,
    /// <summary>Pull requests this session plausibly produced; null unless outcome
    /// ingestion is configured on the collector.</summary>
    List<OutcomeLinkDto>? Outcomes = null);

/// <summary>One session→pull-request link and how much it can be trusted.</summary>
public sealed record OutcomeLinkDto(
    string Repository, int Number, string Branch, string Title,
    string State, string Confidence, string Reason,
    DateTimeOffset OpenedAt, DateTimeOffset? MergedAt,
    double? HoursToFirstReview, double? HoursToMerge,
    int Additions, int Deletions, int ChangedFiles);

public sealed record InsightMetricDto(string Label, string Value);

public sealed record InsightReportDto(
    string Name, string Algorithm, string Status, double? Score,
    List<InsightMetricDto> Metrics, List<string> Findings);

public sealed record TranscriptEntryDto(DateTimeOffset Time, string Model, string? Prompt, string? Response, int Turn);

public sealed record TurnAnalysisDto(
    string Algorithm, List<TurnReportDto> Turns, int? BestIndex, int? WorstIndex, List<string> Findings);

public sealed record TurnReportDto(
    int Index, DateTimeOffset Start, double DurationMs,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors,
    long InputTokens, long OutputTokens, double AvgTtftMs,
    double Score, List<string> Reasons,
    string? Model = null);

public sealed record ToolStatDto(string Name, int Calls, int Errors, double AvgMs);

public sealed record SessionEventDto(DateTimeOffset Time, string Kind, string Summary);

public sealed record QualityReportDto(
    double Score, double Confidence, string Grade, List<QualityComponentDto> Components,
    double? PercentileRank = null, int? HistoryCount = null, double? HistoryMean = null, double? HistoryStdDev = null,
    /// <summary>How the session was driven; decides which components carried weight.</summary>
    SessionMode Mode = SessionMode.Unknown,
    /// <summary>Name of the scoring profile the weights came from.</summary>
    string Profile = "interactive")
{
    public double? ZScore =>
        HistoryMean is { } mu && HistoryStdDev is > 0 ? (Score - mu) / HistoryStdDev : null;

    /// <summary>Human-readable session mode, mirroring the collector's own label.</summary>
    public string ModeLabel => Mode switch
    {
        SessionMode.Interactive => "interactive",
        SessionMode.SupervisedAgent => "supervised agent",
        SessionMode.Autonomous => "autonomous agent",
        _ => "unknown"
    };
    public string? RelativeGrade => PercentileRank switch
    {
        >= 0.75 => "above baseline",
        >= 0.35 => "at baseline",
        < 0.35 => "below baseline",
        _ => null
    };
}

public sealed record QualityComponentDto(
    string Name, double Weight, double Value, int Samples, string Detail);

public sealed record OverviewDto(
    int Sessions,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
    int ChatCalls, int ChatErrors, int ToolCalls, int ToolErrors, int Turns,
    int EditsAccepted, int EditsRejected, int ThumbsUp, int ThumbsDown,
    double AvgQualityScore,
    Dictionary<string, int> ModelCalls,
    List<DailyTokensDto> Daily,
    List<TopSessionDto> TopSessions);

public sealed record DailyTokensDto(DateOnly Date, long InputTokens, long OutputTokens, int Sessions);

public sealed record TopSessionDto(string Id, long TotalTokens, double QualityScore, DateTimeOffset LastSeen);

public sealed record HealthDto(string Status, int Sessions, bool Persistence, bool Forwarding, string Environment);
