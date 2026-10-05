using System.Diagnostics;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Generates golden OTLP fixtures for multi-assistant validation.
/// Run once to populate tests/fixtures/; commit the .pb/.json files.
/// These fixtures replace hand-built payloads and validate real decoder behavior.
/// </summary>
public class FixtureBuilderTests
{
    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find tests/fixtures directory.");
    }

    /// <summary>
    /// Generates a minimal VS Code Copilot Chat session: 3 chat calls, 2 tool calls, 1 error.
    /// </summary>
    [Fact(Skip = "Only run manually to regenerate fixtures: dotnet test --filter FixtureBuilderTests.GenerateVSCodeFixture")]
    public void GenerateVSCodeFixture()
    {
        var root = Path.Combine(FixtureRoot(), "vscode", "v1");
        Directory.CreateDirectory(root);

        // Minimal session: 3 chat turns, 2 tool invocations, 1 error
        var traceId = TestOtlp.Trace(42);
        var spans = new List<byte[]>();

        // Chat turn 0: successful
        spans.Add(TestOtlp.Span(
            traceId, TestOtlp.SpanId(1),
            "copilot_chat.chat_call",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMilliseconds(250),
            error: null,
            ("copilot_chat.turnaround", 250),
            ("copilot_chat.token.size", 150)
        ));

        // Tool call 0: error
        spans.Add(TestOtlp.Span(
            traceId, TestOtlp.SpanId(2),
            "copilot_chat.tool_call",
            DateTimeOffset.UtcNow.AddMilliseconds(250), DateTimeOffset.UtcNow.AddMilliseconds(500),
            error: "ToolError",
            ("copilot_chat.tool", "codeSearch")
        ));

        // Chat turn 1: recovery
        spans.Add(TestOtlp.Span(
            traceId, TestOtlp.SpanId(3),
            "copilot_chat.chat_call",
            DateTimeOffset.UtcNow.AddMilliseconds(500), DateTimeOffset.UtcNow.AddMilliseconds(750),
            error: null,
            ("copilot_chat.turnaround", 250)
        ));

        var payload = TestOtlp.TracesRequest("vscode-golden-0001", spans.ToArray());

        // Write and verify
        var path = Path.Combine(root, "0001-traces.pb");
        File.WriteAllBytes(path, payload);

        VerifyFixture(payload, isJson: false);
        Debug.WriteLine($"Generated {path}");
    }

    /// <summary>
    /// Generates a minimal Claude Code session: 2 turns, 1 tool call with decision, reliability focus.
    /// </summary>
    [Fact(Skip = "Only run manually to regenerate fixtures: dotnet test --filter FixtureBuilderTests.GenerateClaudeCodeFixture")]
    public void GenerateClaudeCodeFixture()
    {
        var root = Path.Combine(FixtureRoot(), "claude-code", "v1");
        Directory.CreateDirectory(root);

        var traceId = TestOtlp.Trace(100);
        var spans = new List<byte[]>();

        // Turn 0: prompt
        spans.Add(TestOtlp.Span(
            traceId, TestOtlp.SpanId(10),
            "claude_code.prompt_call",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMilliseconds(1500),
            error: null,
            ("gen_ai.usage.input_tokens", 2000),
            ("gen_ai.usage.output_tokens", 500),
            ("llm_request.ttft_ms", 350)
        ));

        // Tool call: Edit (accepted)
        spans.Add(TestOtlp.Span(
            traceId, TestOtlp.SpanId(11),
            "tool_decision",
            DateTimeOffset.UtcNow.AddMilliseconds(1500), DateTimeOffset.UtcNow.AddMilliseconds(2000),
            error: null,
            ("tool.name", "Edit"),
            ("tool.outcome", "accepted"),
            ("tool_decision.source", "human")
        ));

        // Turn 1: follow-up
        spans.Add(TestOtlp.Span(
            traceId, TestOtlp.SpanId(12),
            "claude_code.prompt_call",
            DateTimeOffset.UtcNow.AddMilliseconds(2000), DateTimeOffset.UtcNow.AddMilliseconds(3500),
            error: null,
            ("gen_ai.usage.input_tokens", 1500),
            ("gen_ai.usage.output_tokens", 300)
        ));

        var payload = TestOtlp.TracesRequest("claude-code-golden-0001", spans.ToArray());

        var path = Path.Combine(root, "0001-traces.pb");
        File.WriteAllBytes(path, payload);

        VerifyFixture(payload, isJson: false);
        Debug.WriteLine($"Generated {path}");
    }

    /// <summary>
    /// Generates a minimal Copilot CLI session: completions with latency data.
    /// </summary>
    [Fact(Skip = "Only run manually to regenerate fixtures: dotnet test --filter FixtureBuilderTests.GenerateCliFixture")]
    public void GenerateCliFixture()
    {
        var root = Path.Combine(FixtureRoot(), "cli", "v1");
        Directory.CreateDirectory(root);

        var traceId = TestOtlp.Trace(200);
        var spans = new List<byte[]>();

        // Completion span
        spans.Add(TestOtlp.Span(
            traceId, TestOtlp.SpanId(20),
            "copilot.completion",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMilliseconds(2000),
            error: null,
            ("copilot.completion.tokens", 45),
            ("copilot.completion.turnaround", 2000)
        ));

        var payload = TestOtlp.TracesRequest("cli-golden-0001", spans.ToArray());

        var path = Path.Combine(root, "0001-traces.pb");
        File.WriteAllBytes(path, payload);

        VerifyFixture(payload, isJson: false);
        Debug.WriteLine($"Generated {path}");
    }

    private static void VerifyFixture(byte[] payload, bool isJson)
    {
        var batch = new OtlpBatch();
        if (isJson)
            OtlpJsonDecoder.DecodeTraces(payload, batch);
        else
            OtlpDecoder.DecodeTraces(payload, batch);

        Assert.True(batch.Spans.Count > 0, "Fixture decoded to no spans — check payload generation.");
    }
}
