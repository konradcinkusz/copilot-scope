using System.Net.Http.Json;

namespace CopilotScope.Dashboard.Services;

/// <summary>
/// Typed client for the collector's query API. Base address is resolved from Aspire
/// service discovery env vars (services__collector__http__0) with a config/localhost
/// fallback, so the app also runs standalone without the AppHost.
/// </summary>
public sealed class CollectorClient(HttpClient http)
{
    public async Task<List<SessionSummaryDto>> GetSessionsAsync(bool includeInternal = false, CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<SessionSummaryDto>>($"/api/sessions?includeInternal={includeInternal}", ct) ?? [];

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

public enum SessionKind
{
    UserChat,
    InternalTitleGeneration,
    InternalSummary,
    InternalHelper,
    Unattributed
}

public enum EmitterKind { Unknown, VSCode, CLI, ClaudeCode, Cursor, Cowork }

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
    List<InsightReportDto> Insights);

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
