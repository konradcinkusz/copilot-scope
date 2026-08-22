using System.Security.Cryptography;
using System.Text;
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
        TemplateFingerprint = Fingerprint(_template);
    }

    /// <summary>Short, stable hash of the rubric template — the prompt's version, derived rather
    /// than declared so it cannot be forgotten on an edit.
    ///
    /// <para>A calibration κ belongs to the exact rubric that earned it. AI-EVALS.md §5 puts it
    /// as "a judge that silently upgrades is a measuring stick that changes length": recording
    /// this alongside the κ is what turns a later prompt edit into a visible re-baseline instead
    /// of an invisible one. Line endings are normalised first so a CRLF checkout does not
    /// present itself as a changed rubric.</para></summary>
    public string TemplateFingerprint { get; }

    private static string Fingerprint(string template)
    {
        var normalized = template.Replace("\r\n", "\n");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
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
