using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using CopilotScope.JudgeAgent.Config;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CopilotScope.JudgeAgent.Agents;

/// <summary>
/// Talks to a model deployment hosted in Azure AI Foundry via the Azure OpenAI-compatible
/// endpoint, wrapped as a Microsoft Agent Framework AIAgent. The agent (and its instructions)
/// are built fresh per call from the caller-supplied system prompt — the rubric lives entirely
/// in that prompt (see JudgePromptBuilder), not in any stored model state, so nothing about a
/// past judge call is retained.
///
/// NuGet package names/versions used here (Microsoft.Agents.AI, Azure.AI.OpenAI, Azure.Identity)
/// were verified against nuget.org on 2026-08-08 (see docs/AGENTFORGE.md's linked implementation
/// plan, which this client mirrors) — this space moves quickly, re-verify before bumping versions.
/// </summary>
public sealed class AzureFoundryJudgeChatClient(AzureAiOptions options) : IJudgeChatClient
{
    public string BackendName => "azure-foundry";
    public string ModelName => options.DeploymentName ?? "unknown";

    public async Task<string> JudgeAsync(string systemPrompt, string sessionPayloadJson, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Endpoint) || string.IsNullOrEmpty(options.DeploymentName))
        {
            throw new InvalidOperationException(
                "JudgeAgent Azure AI is not configured. Set CopilotScope:JudgeAgent:AzureAI:Endpoint " +
                "and CopilotScope:JudgeAgent:AzureAI:DeploymentName before requesting a judge run.");
        }

        var azureClient = string.IsNullOrEmpty(options.ApiKey)
            ? new AzureOpenAIClient(new Uri(options.Endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey));

        IChatClient chatClient = azureClient
            .GetChatClient(options.DeploymentName)
            .AsIChatClient();

        // Request JSON-object output mode from the model provider (not just prompt instructions) —
        // the rubric template also tells the model to emit bare JSON, this is belt-and-suspenders.
        var chatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            ResponseFormat = ChatResponseFormat.Json
        };

        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "CopilotScopeJudgeAgent",
            ChatOptions = chatOptions
        });

        var response = await agent.RunAsync(sessionPayloadJson, cancellationToken: ct);
        return response.Text;
    }
}
