using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using CopilotScope.JudgeAgent.Judging;
using Xunit;

namespace CopilotScope.Tests;

public class SessionJudgeContextBuilderTests
{
    [Fact]
    public void Build_MapsToolsErrorTypesAndLocalComponents()
    {
        var transcript = new List<TranscriptEntry>
        {
            new(DateTimeOffset.UtcNow, "claude", "fix the bug", "Here's the fix.", 0)
        };
        var tools = new List<ToolStatDto> { new("read_file", 3, 1, 12.5) };
        var errorTypes = new Dictionary<string, int> { ["timeout"] = 2 };
        var components = new List<QualityComponent>
        {
            new("reliability", 0.25, 0.9, 4, "0 err / 4 calls")
        };
        var detail = JudgeAgentTestSupport.MakeSessionDetail("s-1", transcript, tools, errorTypes, components);

        var context = new SessionJudgeContextBuilder().Build(detail);

        Assert.Equal("s-1", context.SessionId);
        Assert.Single(context.Transcript);
        Assert.Equal("fix the bug", context.Transcript[0].Prompt);
        Assert.Single(context.Tools);
        Assert.Equal("read_file", context.Tools[0].Name);
        Assert.Equal(2, context.ErrorTypes["timeout"]);
        Assert.Single(context.LocalComponents);
        Assert.Equal("reliability", context.LocalComponents[0].Name);
        Assert.Equal(0.9, context.LocalComponents[0].Value);
        Assert.Null(context.CompletionSignals);
        Assert.Null(context.RetrievalContext);
    }

    [Fact]
    public void Build_WithLongTranscript_KeepsBothStartAndEndOfTheArc()
    {
        var transcript = Enumerable.Range(0, 100)
            .Select(i => new TranscriptEntry(DateTimeOffset.UtcNow.AddMinutes(i), "claude", $"prompt {i}", $"response {i}", i))
            .ToList();
        var detail = JudgeAgentTestSupport.MakeSessionDetail("s-long", transcript);

        var context = new SessionJudgeContextBuilder().Build(detail);

        Assert.True(context.Transcript.Count <= 40);
        Assert.Contains(context.Transcript, t => t.Turn == 0); // start of the conversation
        Assert.Contains(context.Transcript, t => t.Turn == 99); // how it ended — task-completion needs this
    }

    [Fact]
    public void Build_TruncatesOverlongPromptOrResponseText()
    {
        var longText = new string('x', 5000);
        var transcript = new List<TranscriptEntry>
        {
            new(DateTimeOffset.UtcNow, "claude", longText, longText, 0)
        };
        var detail = JudgeAgentTestSupport.MakeSessionDetail("s-1", transcript);

        var context = new SessionJudgeContextBuilder().Build(detail);

        Assert.True(context.Transcript[0].Prompt!.Length < 5000);
        Assert.Contains("truncated", context.Transcript[0].Prompt);
    }

    [Fact]
    public void Build_SkipsTranscriptEntriesWithNoContent()
    {
        var transcript = new List<TranscriptEntry>
        {
            new(DateTimeOffset.UtcNow, "claude", null, null, 0),
            new(DateTimeOffset.UtcNow, "claude", "hello", "hi", 1)
        };
        var detail = JudgeAgentTestSupport.MakeSessionDetail("s-1", transcript);

        var context = new SessionJudgeContextBuilder().Build(detail);

        Assert.Single(context.Transcript);
        Assert.Equal(1, context.Transcript[0].Turn);
    }
}
