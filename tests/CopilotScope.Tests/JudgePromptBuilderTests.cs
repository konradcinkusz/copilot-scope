using CopilotScope.JudgeAgent.Agents;
using CopilotScope.JudgeAgent.Domain;
using Xunit;

namespace CopilotScope.Tests;

public class JudgePromptBuilderTests
{
    [Fact]
    public void Build_SubstitutesSessionIdAndLocalComponentsSummary()
    {
        var context = new SessionJudgeContext(
            "session-42",
            new List<JudgeTranscriptTurn> { new(0, "claude", "hi", "hello") },
            new List<JudgeToolStat>(),
            new Dictionary<string, int>(),
            new List<JudgeLocalComponent> { new("reliability", 0.87, 5, "detail") });

        var prompt = new JudgePromptBuilder().Build(context);

        Assert.Contains("session-42", prompt);
        Assert.Contains("reliability=0.87", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void Build_WithNoLocalComponents_StillProducesAPrompt()
    {
        var context = new SessionJudgeContext(
            "session-empty",
            new List<JudgeTranscriptTurn>(),
            new List<JudgeToolStat>(),
            new Dictionary<string, int>(),
            new List<JudgeLocalComponent>());

        var prompt = new JudgePromptBuilder().Build(context);

        Assert.Contains("session-empty", prompt);
        Assert.Contains("no local quality components computed yet", prompt);
        Assert.DoesNotContain("{{", prompt);
    }
}
