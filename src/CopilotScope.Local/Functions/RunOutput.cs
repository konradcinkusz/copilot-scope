using System.Text.Json;
using CopilotScope.Dashboard.Functions;

namespace CopilotScope.Local.Functions;

/// <summary>How a run ended, read from what the assistant printed.</summary>
internal sealed record RunOutcome(bool Succeeded, string? Report, string? Error);

/// <summary>
/// Reads an assistant's reply. Claude Code is asked for <c>--output-format json</c>: one object whose
/// <c>result</c> is the reply and whose <c>is_error</c> says whether the run failed. Copilot CLI is asked
/// for <c>-s</c>: the reply as plain text and nothing else. Its JSON-lines mode is not parsed —
/// a parser for an emitter's format lands only with a fixture captured from a real installation
/// (ADR-002, ADR-004), and plain text needs none.
/// </summary>
internal static class RunOutput
{
    /// <summary>Most of stderr kept for an error message: its end, where the reason usually is.</summary>
    private const int ErrorTail = 600;

    public static RunOutcome Parse(FunctionAssistant assistant, int exitCode, string stdout, string stderr)
    {
        if (assistant == FunctionAssistant.ClaudeCode && TryClaudeResult(stdout) is var (result, isError, subtype))
        {
            if (isError || exitCode != 0)
                return new(false, null, $"Claude Code reported an error{(subtype is null ? "" : $" ({subtype})")}: " +
                                         Tail(result is { Length: > 0 } ? result : stderr));
            return result is { Length: > 0 }
                ? new(true, result.Trim(), null)
                : new(false, null, "Claude Code finished without a reply.");
        }

        if (exitCode != 0)
            return new(false, null, $"{FunctionWorkspace.DisplayName(assistant)} exited with code {exitCode}: " +
                                     Tail(stderr.Length > 0 ? stderr : stdout));
        return stdout.Trim() is { Length: > 0 } text
            ? new(true, text, null)
            : new(false, null, $"{FunctionWorkspace.DisplayName(assistant)} finished without a reply." +
                               (stderr.Length > 0 ? " " + Tail(stderr) : ""));
    }

    private static (string? Result, bool IsError, string? Subtype)? TryClaudeResult(string stdout)
    {
        var text = stdout.Trim();
        if (!text.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result)) return null;
            var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
            var subtype = root.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return (result.ValueKind == JsonValueKind.String ? result.GetString() : null, isError,
                subtype is "success" ? null : subtype);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= ErrorTail ? trimmed : "…" + trimmed[^ErrorTail..];
    }
}
