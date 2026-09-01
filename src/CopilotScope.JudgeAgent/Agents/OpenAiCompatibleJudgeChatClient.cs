using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotScope.JudgeAgent.Config;

namespace CopilotScope.JudgeAgent.Agents;

/// <summary>
/// Talks to any server implementing the OpenAI chat-completions API — Ollama, vLLM, LM Studio,
/// llama.cpp, or a self-hosted gateway. This is what makes "the judge runs entirely on your own
/// hardware" true, which is the whole point: judging is the one feature that sends real
/// transcript text somewhere, and until now the only somewhere was Azure.
///
/// Hand-rolled over HttpClient rather than pulled in through an SDK, for the same reason the
/// Collector hand-writes its OTLP decoder: the request is a dozen lines of JSON, and the
/// OpenAI-compatible servers differ in small ways (which optional fields they reject) that are
/// far easier to accommodate with direct control over the payload than through a client library
/// built for the canonical API.
///
/// The prompt, rubric and fingerprint pipeline is untouched — this class only swaps out the
/// transport — so a κ measured against one backend stays comparable with another.
/// </summary>
public sealed class OpenAiCompatibleJudgeChatClient(OpenAiCompatibleOptions options, HttpClient http)
    : IJudgeChatClient
{
    public string BackendName => "openai-compatible";
    public string ModelName => options.Model ?? "unknown";

    public async Task<string> JudgeAsync(string systemPrompt, string sessionPayloadJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException(
                "JudgeAgent's OpenAI-compatible backend is not configured. Set " +
                "CopilotScope:JudgeAgent:OpenAiCompatible:BaseUrl (e.g. http://localhost:11434/v1) " +
                "and :Model before requesting a judge run.");
        }

        var request = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["temperature"] = options.Temperature,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = sessionPayloadJson }
            }
        };

        // Belt-and-suspenders with the rubric's own "emit bare JSON" instruction — but opt-out,
        // because a server that does not know the field returns 400 for the whole request.
        if (options.UseJsonResponseFormat)
            request["response_format"] = new { type = "json_object" };

        using var content = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUri(options.BaseUrl))
        {
            Content = content
        };
        if (!string.IsNullOrEmpty(options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var response = await http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Include the body: these servers explain themselves ("model not found",
            // "response_format unsupported"), and swallowing that turns a one-line
            // configuration fix into a debugging session.
            throw new HttpRequestException(
                $"Judge backend at {options.BaseUrl} returned {(int)response.StatusCode}: {Truncate(body)}",
                inner: null, statusCode: response.StatusCode);
        }

        return ExtractContent(body, options.BaseUrl);
    }

    /// <summary>
    /// Joins the configured base URL to <c>chat/completions</c>. Tolerates a trailing slash and
    /// a base that already ends in <c>/v1</c>, because every server's docs write it differently
    /// and a 404 from a doubled path segment is a miserable first-run experience.
    /// </summary>
    internal static Uri ChatCompletionsUri(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return new Uri(trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/chat/completions");
    }

    /// <summary>Pulls the assistant message out of a chat-completions response.</summary>
    internal static string ExtractContent(string body, string baseUrl)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var messageElement)
            && messageElement.TryGetProperty("content", out var contentElement)
            && contentElement.GetString() is { } text)
        {
            return text;
        }

        throw new InvalidOperationException(
            $"Judge backend at {baseUrl} returned no assistant message. " +
            $"Response was: {Truncate(body)}");
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";
}
