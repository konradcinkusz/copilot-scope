using System.Text.Json;

namespace CopilotScope.JudgeAgent.Judging;

/// <summary>Shared JSON options for everything that crosses the judge-model boundary: the
/// session payload sent as the user message, and the rubric result the model sends back.
/// Web defaults (camelCase, case-insensitive) match the field names in
/// JudgeSystemPromptTemplate.txt's schema ("results", "name", "algorithm", ...).</summary>
internal static class JudgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
