namespace CopilotScope.JudgeAgent.Config;

/// <summary>Bound from CopilotScope:JudgeAgent:AzureAI. Not validated at startup — only when a
/// judge call is actually attempted — so the health endpoint keeps working without any Azure
/// credentials configured.</summary>
public sealed class AzureAiOptions
{
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }

    /// <summary>Null → use DefaultAzureCredential (managed identity / az login) instead of a key.</summary>
    public string? ApiKey { get; set; }
}
