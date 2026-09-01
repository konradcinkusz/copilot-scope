namespace CopilotScope.JudgeAgent.Agents;

/// <summary>Abstraction over the underlying agent/model call, so the API layer and its tests
/// don't depend directly on the Azure AI Foundry SDK.</summary>
public interface IJudgeChatClient
{
    Task<string> JudgeAsync(string systemPrompt, string sessionPayloadJson, CancellationToken ct);

    /// <summary>
    /// Which backend produced a verdict, e.g. <c>azure-foundry</c> or <c>openai-compatible</c>.
    /// Reported with every judge result: a score is only interpretable if you know what produced
    /// it, and two backends grading the same rubric are not interchangeable evidence.
    /// </summary>
    string BackendName { get; }

    /// <summary>Model or deployment name the verdict came from.</summary>
    string ModelName { get; }
}
