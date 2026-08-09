namespace CopilotScope.JudgeAgent.Agents;

/// <summary>Abstraction over the underlying agent/model call, so the API layer and its tests
/// don't depend directly on the Azure AI Foundry SDK.</summary>
public interface IJudgeChatClient
{
    Task<string> JudgeAsync(string systemPrompt, string sessionPayloadJson, CancellationToken ct);
}
