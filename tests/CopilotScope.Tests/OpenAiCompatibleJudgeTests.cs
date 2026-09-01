using System.Net;
using System.Text;
using System.Text.Json;
using CopilotScope.JudgeAgent.Agents;
using CopilotScope.JudgeAgent.Config;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The judge backend that runs on your own hardware.
///
/// Judging is the one feature that sends real transcript text somewhere, and the only somewhere
/// used to be Azure — which locked the five judge algorithms away from exactly the self-hosted
/// and regulated deployments the project is most defensible in. These tests drive the client
/// against a fake OpenAI-compatible server (the shape Ollama, vLLM and LM Studio all speak),
/// checking both what goes on the wire and how failures surface.
/// </summary>
public class OpenAiCompatibleJudgeTests
{
    /// <summary>Stands in for the model server: records the request, returns a scripted reply.</summary>
    private sealed class FakeServer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public JsonElement LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var raw = await request.Content!.ReadAsStringAsync(ct);
            LastBody = JsonDocument.Parse(raw).RootElement.Clone();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private const string ValidReply = """
        {"choices":[{"message":{"role":"assistant","content":"{\"gEval\":0.82}"}}]}
        """;

    private static (OpenAiCompatibleJudgeChatClient Client, FakeServer Server) Build(
        OpenAiCompatibleOptions? options = null,
        HttpStatusCode status = HttpStatusCode.OK,
        string body = ValidReply)
    {
        options ??= new OpenAiCompatibleOptions
        {
            BaseUrl = "http://localhost:11434/v1",
            Model = "qwen2.5-coder:14b"
        };
        var server = new FakeServer(status, body);
        return (new OpenAiCompatibleJudgeChatClient(options, new HttpClient(server)), server);
    }

    // ---------------------------------------------------------------- the wire format

    [Fact]
    public async Task TheModelAndBothMessagesGoOnTheWire()
    {
        var (client, server) = Build();

        var result = await client.JudgeAsync("RUBRIC", """{"sessionId":"s1"}""", CancellationToken.None);

        Assert.Equal("""{"gEval":0.82}""", result);
        Assert.Equal("qwen2.5-coder:14b", server.LastBody.GetProperty("model").GetString());

        var messages = server.LastBody.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("RUBRIC", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("""{"sessionId":"s1"}""", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TemperatureIsZeroByDefault()
    {
        // A judge that answers differently on identical input cannot be calibrated — κ against
        // human labels would be measuring the sampler.
        var (client, server) = Build();
        await client.JudgeAsync("RUBRIC", "{}", CancellationToken.None);

        Assert.Equal(0d, server.LastBody.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task JsonResponseFormatIsRequestedByDefault()
    {
        var (client, server) = Build();
        await client.JudgeAsync("RUBRIC", "{}", CancellationToken.None);

        Assert.Equal("json_object",
            server.LastBody.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task JsonResponseFormatCanBeTurnedOffForServersThatRejectIt()
    {
        // Some OpenAI-compatible servers 400 the whole request over an unknown field, and the
        // rubric already instructs the model to emit bare JSON.
        var (client, server) = Build(new OpenAiCompatibleOptions
        {
            BaseUrl = "http://localhost:8000/v1",
            Model = "local",
            UseJsonResponseFormat = false
        });

        await client.JudgeAsync("RUBRIC", "{}", CancellationToken.None);
        Assert.False(server.LastBody.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task NoAuthorizationHeaderIsSentWhenNoKeyIsConfigured()
    {
        // Local servers want no credential; sending an empty bearer confuses some of them.
        var (client, server) = Build();
        await client.JudgeAsync("RUBRIC", "{}", CancellationToken.None);

        Assert.Null(server.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task ABearerTokenIsSentWhenTheGatewayNeedsOne()
    {
        var (client, server) = Build(new OpenAiCompatibleOptions
        {
            BaseUrl = "https://gateway.internal/v1", Model = "local", ApiKey = "sk-local"
        });

        await client.JudgeAsync("RUBRIC", "{}", CancellationToken.None);

        Assert.Equal("Bearer", server.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-local", server.LastRequest.Headers.Authorization.Parameter);
    }

    // ---------------------------------------------------------------- URL handling

    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:8000/v1/chat/completions", "http://localhost:8000/v1/chat/completions")]
    public void TheBaseUrlIsJoinedForgivingly(string configured, string expected) =>
        // Every server's docs write this differently, and a 404 from a doubled path segment is a
        // miserable first-run experience.
        Assert.Equal(expected, OpenAiCompatibleJudgeChatClient.ChatCompletionsUri(configured).ToString());

    // ---------------------------------------------------------------- failure modes

    [Fact]
    public async Task AnUnconfiguredBackendSaysWhatToSet()
    {
        var (client, _) = Build(new OpenAiCompatibleOptions()); // no BaseUrl, no Model

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.JudgeAsync("RUBRIC", "{}", CancellationToken.None));
        Assert.Contains("OpenAiCompatible:BaseUrl", ex.Message);
    }

    [Fact]
    public async Task AServerErrorSurfacesTheServersOwnExplanation()
    {
        // These servers explain themselves ("model not found"); swallowing that turns a
        // one-line configuration fix into a debugging session.
        var (client, _) = Build(status: HttpStatusCode.NotFound,
            body: """{"error":{"message":"model 'nope' not found"}}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.JudgeAsync("RUBRIC", "{}", CancellationToken.None));
        Assert.Contains("model 'nope' not found", ex.Message);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public void AResponseWithNoChoicesIsReportedNotSilentlyEmpty()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OpenAiCompatibleJudgeChatClient.ExtractContent("""{"choices":[]}""", "http://x/v1"));
        Assert.Contains("no assistant message", ex.Message);
    }

    [Fact]
    public void AWellFormedResponseYieldsTheAssistantMessage() =>
        Assert.Equal("hello", OpenAiCompatibleJudgeChatClient.ExtractContent(
            """{"choices":[{"message":{"content":"hello"}}]}""", "http://x/v1"));

    // ---------------------------------------------------------------- provenance

    [Fact]
    public void ProvenanceNamesTheBackendAndModel()
    {
        var (client, _) = Build();
        Assert.Equal("openai-compatible", client.BackendName);
        Assert.Equal("qwen2.5-coder:14b", client.ModelName);
    }

    [Fact]
    public void TheDefaultBackendIsStillAzureSoExistingDeploymentsAreUnchanged() =>
        Assert.Equal(JudgeBackend.AzureFoundry, new JudgeBackendOptions().Backend);
}
