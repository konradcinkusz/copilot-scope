using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The observer stays out of the observation.
///
/// CopilotScope's MCP server lets an assistant read its own session scores, and the
/// assistant makes those reads from inside the session being scored. Reliability is
/// the error-free rate over ChatCalls * 2 + ToolCalls — 0.25 of the composite — so a
/// counted read would raise the number it just read, and the tool-to-chat ratio
/// SegmentAnalyzer uses to find repair loops would move with it. These tests pin the
/// exclusion at both ingest paths, and pin the one thing that must survive it: the
/// call still shows up in the timeline.
/// </summary>
public class SelfObservationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    // Claude Code and Cowork spell an MCP tool mcp__server__tool …
    [InlineData("mcp__copilotscope__list_sessions", true)]
    [InlineData("mcp__copilotscope__get_session", true)]
    // … VS Code Copilot spells the same thing mcp_server_tool.
    [InlineData("mcp_copilotscope_overview", true)]
    // Case is the emitter's business, not a signal.
    [InlineData("MCP__CopilotScope__Overview", true)]
    // Another MCP server is the user's own work and is scored like any other tool.
    [InlineData("mcp__github__create_pull_request", false)]
    // A plain tool that merely mentions the project is not an MCP call against it.
    [InlineData("copilotscope", false)]
    [InlineData("Bash", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesOnlyItsOwnMcpTools(string? toolName, bool expected) =>
        Assert.Equal(expected, SelfObservation.IsSelfObservation(toolName));

    [Fact]
    public void ClaudeCodeToolResultsFromItsOwnMcpServerAreNotCounted()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(ToolResult("cc-mcp", "Edit", failed: false));
        batch.Logs.Add(ToolResult("cc-mcp", "mcp__copilotscope__list_sessions", failed: false));
        batch.Logs.Add(ToolResult("cc-mcp", "mcp__copilotscope__get_session", failed: false));

        store.Ingest(batch);

        var session = store.Get("cc-mcp")!;
        Assert.Equal(1, session.ToolCalls);
        Assert.Equal(0, session.ToolErrors);
        // The tool table is what the session detail view renders; the reads are not work.
        Assert.True(session.Tools.ContainsKey("Edit"));
        Assert.DoesNotContain(session.Tools.Keys, k => k.StartsWith("mcp__copilotscope__", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailingSelfObservationDoesNotDockReliability()
    {
        // The nastier direction: a broken collector would otherwise punish the score of
        // every session that asked it a question.
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(ToolResult("cc-mcp-err", "mcp__copilotscope__overview", failed: true));

        store.Ingest(batch);

        var session = store.Get("cc-mcp-err")!;
        Assert.Equal(0, session.ToolCalls);
        Assert.Equal(0, session.ToolErrors);
        Assert.Empty(session.ErrorTypes);
    }

    [Fact]
    public void TheExcludedCallIsStillVisibleInTheTimeline()
    {
        // Dropped from the counters, not from the record — a reader has to be able to
        // see that the assistant asked.
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Logs.Add(ToolResult("cc-mcp-vis", "mcp__copilotscope__list_sessions", failed: false));

        store.Ingest(batch);

        var session = store.Get("cc-mcp-vis")!;
        Assert.Equal(0, session.ToolCalls);
        Assert.Contains(session.RecentEvents, e => e.Summary.Contains("mcp__copilotscope__list_sessions", StringComparison.Ordinal));
    }

    [Fact]
    public void ExecuteToolSpansFromItsOwnMcpServerAreNotCounted()
    {
        // The Copilot-family path, where tool calls arrive as spans rather than log events.
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Spans.Add(ToolSpan("conv-mcp", "trace-1", "read_file"));
        batch.Spans.Add(ToolSpan("conv-mcp", "trace-1", "mcp__copilotscope__list_sessions"));
        batch.Spans.Add(ToolSpan("conv-mcp", "trace-1", "mcp_copilotscope_overview"));

        store.Ingest(batch);

        var session = store.Get("conv-mcp")!;
        Assert.Equal(1, session.ToolCalls);

        // Per-turn counters matter on their own: SegmentAnalyzer compares each turn's
        // tool-to-chat ratio against the session median to flag repair loops.
        var turn = Assert.Single(session.TurnList);
        Assert.Equal(1, turn.ToolCalls);
    }

    private static OtlpLogEvent ToolResult(string sessionId, string toolName, bool failed) => new()
    {
        EventName = "claude_code.tool_result",
        Time = T0,
        Resource = new Dictionary<string, AttrValue> { ["service.name"] = AttrValue.Str("claude-code") },
        Attributes = new Dictionary<string, AttrValue>
        {
            ["session.id"] = AttrValue.Str(sessionId),
            ["tool_name"] = AttrValue.Str(toolName),
            ["success"] = AttrValue.Str(failed ? "false" : "true"),
            ["duration_ms"] = AttrValue.Dbl(12)
        }
    };

    private static OtlpSpan ToolSpan(string conversationId, string traceId, string toolName) => new()
    {
        TraceId = traceId,
        SpanId = Guid.NewGuid().ToString("N")[..16],
        Name = "execute_tool",
        Start = T0,
        End = T0.AddMilliseconds(40),
        Resource = new Dictionary<string, AttrValue> { ["service.name"] = AttrValue.Str("copilot-chat") },
        Attributes = new Dictionary<string, AttrValue>
        {
            [Sem.ConversationId] = AttrValue.Str(conversationId),
            [Sem.Operation] = AttrValue.Str("execute_tool"),
            [Sem.ToolName] = AttrValue.Str(toolName)
        }
    };
}
