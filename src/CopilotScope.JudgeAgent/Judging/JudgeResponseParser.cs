using System.Text.Json;
using CopilotScope.Collector.Quality;

namespace CopilotScope.JudgeAgent.Judging;

/// <summary>Parses the judge model's raw text response into InsightReport-shaped results. The
/// model's JSON field names ("name", "algorithm", "status", "score", "metrics", "findings")
/// match InsightReport's constructor parameters exactly, so no separate wire DTO is needed —
/// System.Text.Json binds the record's primary constructor directly.</summary>
public static class JudgeResponseParser
{
    private sealed record JudgeResponseEnvelope(List<InsightReport> Results);

    public static List<InsightReport> Parse(string rawResponse)
    {
        var json = StripMarkdownFence(rawResponse);
        JudgeResponseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<JudgeResponseEnvelope>(json, JudgeJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Judge response was not valid JSON: {ex.Message}", ex);
        }

        if (envelope?.Results is null)
            throw new InvalidOperationException("Judge response did not contain a 'results' array.");

        return envelope.Results;
    }

    /// <summary>The rubric template forbids markdown fencing, but strips it defensively anyway —
    /// models occasionally wrap JSON in ```json ... ``` despite instructions not to.</summary>
    private static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        trimmed = firstNewline >= 0 ? trimmed[(firstNewline + 1)..] : trimmed;
        var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (fenceEnd >= 0) trimmed = trimmed[..fenceEnd];
        return trimmed.Trim();
    }
}
