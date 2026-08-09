using CopilotScope.JudgeAgent.Domain;

namespace CopilotScope.JudgeAgent.Agents;

/// <summary>Renders JudgeSystemPromptTemplate.txt against a SessionJudgeContext. Plain
/// string.Replace placeholder substitution — no templating engine dependency, matching
/// AgentForge's PersonaPromptBuilder and the rest of the repo.</summary>
public sealed class JudgePromptBuilder
{
    private const string ResourceName = "CopilotScope.JudgeAgent.Agents.JudgeSystemPromptTemplate.txt";
    private readonly string _template;

    public JudgePromptBuilder()
    {
        var assembly = typeof(JudgePromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        _template = reader.ReadToEnd();
    }

    public string Build(SessionJudgeContext context)
    {
        return _template
            .Replace("{{SessionId}}", context.SessionId)
            .Replace("{{LocalComponentsSummary}}", RenderLocalComponentsSummary(context.LocalComponents));
    }

    private static string RenderLocalComponentsSummary(List<JudgeLocalComponent> components)
    {
        if (components.Count == 0) return "no local quality components computed yet";
        return string.Join(", ", components.Select(c => $"{c.Name}={c.Value:0.00}"));
    }
}
