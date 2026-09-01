using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.JudgeAgent.Agents;
using CopilotScope.JudgeAgent.Clients;
using CopilotScope.JudgeAgent.Judging;
using Xunit;

namespace CopilotScope.Tests;

// Exercises the same sequence Program.cs's POST /api/sessions/{id}/judge handler runs — fetch
// session -> build context -> build prompt -> call the judge chat client -> parse the response —
// without hosting the app. Mirrors AgentForge's PersonaProvisioningFlowTests convention: this
// repo tests logic directly rather than through HTTP round-trips.
public class JudgeFlowTests
{
    private sealed class FakeCollectorClient(SessionDetailDto session) : ICollectorClient
    {
        public Task<SessionDetailDto?> GetSessionDetailAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<SessionDetailDto?>(sessionId == session.Summary.Id ? session : null);
    }

    private sealed class StubJudgeChatClient(string response) : IJudgeChatClient
    {
        public string BackendName => "stub";
        public string ModelName => "stub-model";

        public string? LastSystemPrompt { get; private set; }
        public string? LastSessionPayloadJson { get; private set; }

        public Task<string> JudgeAsync(string systemPrompt, string sessionPayloadJson, CancellationToken ct)
        {
            LastSystemPrompt = systemPrompt;
            LastSessionPayloadJson = sessionPayloadJson;
            return Task.FromResult(response);
        }
    }

    private const string StubResponse = """
        {
          "results": [
            { "name": "LLM-as-a-Judge (G-Eval)", "algorithm": "G-Eval", "status": "ok",
              "score": 0.75, "metrics": [], "findings": [ "Turn 0 resolves the ask." ] },
            { "name": "SPUR (learned satisfaction rubric)", "algorithm": "SPUR", "status": "ok",
              "score": 0.8, "metrics": [], "findings": [ "No rephrasing observed." ] },
            { "name": "RAG component metrics (RAGAS)", "algorithm": "RAGAS", "status": "no-data",
              "score": null, "metrics": [], "findings": [ "No retrieval context." ] },
            { "name": "Frustration classification (deep)", "algorithm": "deep-frustration", "status": "ok",
              "score": 0.1, "metrics": [], "findings": [ "Agrees with local heuristic." ] },
            { "name": "Task-completion detection", "algorithm": "task-completion", "status": "ok",
              "score": 1.0, "metrics": [], "findings": [ "Final turn confirms the fix works." ] }
          ]
        }
        """;

    [Fact]
    public async Task JudgeSession_RunsFullPipelineAndReturnsFiveResults()
    {
        var transcript = new List<TranscriptEntry>
        {
            new(DateTimeOffset.UtcNow, "claude", "why does this throw NPE?", "Because x is unset; add a null check.", 0)
        };
        var session = JudgeAgentTestSupport.MakeSessionDetail("s-1", transcript);

        var collector = new FakeCollectorClient(session);
        var contextBuilder = new SessionJudgeContextBuilder();
        var promptBuilder = new JudgePromptBuilder();
        var chatClient = new StubJudgeChatClient(StubResponse);

        var detail = await collector.GetSessionDetailAsync("s-1", CancellationToken.None);
        Assert.NotNull(detail);

        var context = contextBuilder.Build(detail!);
        var systemPrompt = promptBuilder.Build(context);
        var payload = System.Text.Json.JsonSerializer.Serialize(context, JudgeJson.Options);

        var raw = await chatClient.JudgeAsync(systemPrompt, payload, CancellationToken.None);
        var results = JudgeResponseParser.Parse(raw);

        Assert.Equal(5, results.Count);
        Assert.Equal("G-Eval", results[0].Algorithm);
        Assert.Contains("s-1", chatClient.LastSystemPrompt);
        Assert.Contains("why does this throw NPE?", chatClient.LastSessionPayloadJson);
    }

    [Fact]
    public async Task JudgeSession_UnknownId_CollectorReturnsNull()
    {
        var session = JudgeAgentTestSupport.MakeSessionDetail("s-1", new List<TranscriptEntry>());
        var collector = new FakeCollectorClient(session);

        var detail = await collector.GetSessionDetailAsync("does-not-exist", CancellationToken.None);

        Assert.Null(detail);
    }
}
