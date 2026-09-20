namespace CopilotScope.Collector.Domain;

/// <summary>
/// Tool calls CopilotScope made against itself, which must not be scored.
///
/// Why this exists:
///
///   The MCP server (tools/CopilotScope.Mcp) lets an assistant read its own session
///   scores. Those reads are tool calls, and the assistant makes them from inside the
///   session being measured, so the collector ingests them like any other tool call —
///   as <c>execute_tool</c> spans on the Copilot path and <c>tool_result</c> log events
///   on the Claude Code one.
///
///   That is not a cosmetic problem. <see cref="Quality.QualityEngine"/> builds the
///   reliability component — 0.25 of the composite, its largest single weight — as the
///   error-free rate over <c>ChatCalls * 2 + ToolCalls</c>, so every *successful* read
///   of a score would raise the score being read. <see cref="Quality.SegmentAnalyzer"/>
///   then compares each turn's tool-to-chat ratio against the session median to find
///   repair loops, and injected calls move both sides of that comparison at once —
///   masking real repair loops in some sessions and manufacturing them in others.
///
///   A measurement that changes the number it reports is not a measurement. So the
///   observer is dropped at ingest, before it reaches any counter.
///
/// The call is still visible: both ingest paths record their timeline event outside the
/// scoring block, so a reader sees that the assistant asked without the asking moving
/// the score. Nothing else in the session is affected.
///
/// Recognition is by tool name, which is all the wire carries. Claude Code and Cowork
/// expose an MCP tool as <c>mcp__server__tool</c> and VS Code Copilot as
/// <c>mcp_server_tool</c>; both embed the server name, and <c>copilotscope mcp</c>
/// registers under <see cref="ServerName"/>. A deployment that registers the server
/// under some other name is not recognized and its reads *are* scored — documented in
/// docs/MCP.md rather than guessed at here, because a looser match would start dropping
/// a user's own unrelated tools, which is the worse failure of the two.
/// </summary>
public static class SelfObservation
{
    /// <summary>The MCP server name <c>copilotscope mcp</c> registers under.</summary>
    public const string ServerName = "copilotscope";

    /// <summary>How the two emitter families spell an MCP tool belonging to that server.</summary>
    private static readonly string[] Prefixes =
    [
        $"mcp__{ServerName}__",  // Claude Code, Cowork
        $"mcp_{ServerName}_"     // VS Code Copilot
    ];

    /// <summary>Is this tool call CopilotScope reading itself?</summary>
    public static bool IsSelfObservation(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return false;

        foreach (var prefix in Prefixes)
            if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
