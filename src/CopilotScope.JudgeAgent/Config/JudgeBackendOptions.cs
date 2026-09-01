namespace CopilotScope.JudgeAgent.Config;

/// <summary>Which model endpoint the judge talks to.</summary>
public enum JudgeBackend
{
    /// <summary>Azure AI Foundry via the Azure OpenAI SDK. The original, and still the default.</summary>
    AzureFoundry,

    /// <summary>
    /// Any server speaking the OpenAI chat-completions API: Ollama, vLLM, LM Studio, llama.cpp,
    /// or a self-hosted gateway. One implementation covers all of them.
    /// </summary>
    OpenAiCompatible
}

/// <summary>
/// Bound from <c>CopilotScope:JudgeAgent</c>. Selects the judge backend and configures the
/// OpenAI-compatible one.
///
/// Why this exists: the judge is the only feature that sends real transcript content off the
/// machine, and it could only send it to Azure. That directly contradicts the "nothing leaves
/// the machine" pillar in docs/STRATEGY.md, and it locks the five judge algorithms away from
/// the self-hosted and regulated deployments that are the project's most defensible segment —
/// exactly the people who cannot ship ~40 turns of prompt and response text to a cloud vendor.
///
/// The default stays AzureFoundry so no existing deployment changes behaviour.
/// </summary>
public sealed class JudgeBackendOptions
{
    public JudgeBackend Backend { get; set; } = JudgeBackend.AzureFoundry;

    public OpenAiCompatibleOptions OpenAiCompatible { get; set; } = new();
}

/// <summary>Bound from <c>CopilotScope:JudgeAgent:OpenAiCompatible</c>.</summary>
public sealed class OpenAiCompatibleOptions
{
    /// <summary>
    /// Base URL of the server, e.g. <c>http://localhost:11434/v1</c> for Ollama or
    /// <c>http://localhost:8000/v1</c> for vLLM. The client appends <c>chat/completions</c>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Model name as the server knows it, e.g. <c>qwen2.5-coder:14b</c>.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Optional bearer token. Local servers usually need none; a shared in-region gateway will.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Send <c>response_format: {"type":"json_object"}</c>. On by default because it is the
    /// strongest guarantee of parseable output, but some OpenAI-compatible servers reject the
    /// field outright — turn it off there and rely on the rubric's own "emit bare JSON"
    /// instruction, which every prompt already carries.
    /// </summary>
    public bool UseJsonResponseFormat { get; set; } = true;

    /// <summary>
    /// Sampling temperature. Zero by default: a judge that answers differently on identical
    /// input cannot be calibrated, and κ against human labels would be measuring the sampler.
    /// </summary>
    public double Temperature { get; set; }

    /// <summary>How long to wait for a completion. Local models on CPU are slow.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
