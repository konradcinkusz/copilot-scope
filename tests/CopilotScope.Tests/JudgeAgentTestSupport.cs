using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Tests;

/// <summary>Shared helper for JudgeAgent tests that need a hand-built SessionDetailDto — mirrors
/// AgentForgeTestSupport but also lets tests populate quality components, tools and error types,
/// since SessionJudgeContextBuilder reads all three (AgentForge's profile builder does not).</summary>
internal static class JudgeAgentTestSupport
{
    public static SessionDetailDto MakeSessionDetail(
        string id,
        List<TranscriptEntry> transcript,
        List<ToolStatDto>? tools = null,
        Dictionary<string, int>? errorTypes = null,
        List<QualityComponent>? components = null)
    {
        var quality = new QualityReport(80, 1.0, "good", components ?? new List<QualityComponent>());

        var summary = new SessionSummaryDto(
            id, "claude-code", null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            0, 0, 0,
            0, 0, 0, 0,
            0, 0,
            0, 0, 0, 0,
            0, 0,
            0, 0,
            new Dictionary<string, int>(),
            quality,
            SessionKind.UserChat);

        return new SessionDetailDto(
            summary,
            tools ?? new List<ToolStatDto>(),
            errorTypes ?? new Dictionary<string, int>(),
            new List<SessionEvent>(),
            transcript,
            new TurnAnalysis("TFRA", new List<TurnReport>(), null, null, new List<string>()),
            new List<InsightReport>());
    }
}
