using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// A run CopilotScope launches on the user's own assistant is never scored (ADR-005, decision 6).
///
/// The run reads the review pack and writes a report. If it were ingested, the base would grow a
/// session whose only subject is the base, and every figure the next review reads would have moved
/// because the last one ran. These tests pin the exclusion at every ingest path — log events,
/// metric points and spans over OTLP, and the import endpoint the scanner uses — by id and by
/// marker, and pin that the user's own sessions in the same batch are untouched.
/// </summary>
public class ObserverSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARegisteredClaudeCodeSessionIsDroppedAndTheUsersIsKept()
    {
        var observers = new ObserverRegistry();
        observers.Register("run-1");
        var store = new SessionStore(observers);

        var batch = new OtlpBatch();
        batch.Logs.Add(ToolResult("run-1", "Read"));
        batch.Logs.Add(ApiRequest("run-1"));
        batch.Metrics.Add(TokenMetric("run-1"));
        batch.Logs.Add(ToolResult("users-own", "Edit"));

        store.Ingest(batch);

        Assert.Null(store.Get("run-1"));
        Assert.Equal(1, store.Get("users-own")!.ToolCalls);
        Assert.Single(store.All);
        Assert.Equal(3, observers.Dropped);
    }

    [Fact]
    public void AMarkedResourceIsDroppedWhateverItsSessionId()
    {
        // Copilot CLI: its session id cannot be chosen, so the marker is all there is.
        var observers = new ObserverRegistry();
        var store = new SessionStore(observers);

        var batch = new OtlpBatch();
        batch.Logs.Add(ToolResult("unregistered", "Read", marked: true));
        batch.Spans.Add(ChatSpan("conv-marked", "trace-m", marked: true));

        store.Ingest(batch);

        Assert.Empty(store.All);
        Assert.Equal(2, observers.Dropped);
    }

    [Fact]
    public void SiblingSpansOfAnObservedTraceDoNotFallIntoABucket()
    {
        // Copilot-family turns name their conversation on the invoke_agent root only; the chat and
        // tool spans beside it are resolved through the trace. Dropping the root alone would leave
        // them to land in a resource-fingerprint bucket — a session the user never had.
        var observers = new ObserverRegistry();
        observers.Register("conv-run");
        var store = new SessionStore(observers);

        var batch = new OtlpBatch();
        batch.Spans.Add(ChatSpan("conv-run", "trace-1"));
        batch.Spans.Add(ChatSpan(conversationId: null, "trace-1"));
        batch.Spans.Add(ToolSpan("trace-1", "read_file"));

        store.Ingest(batch);

        Assert.Empty(store.All);
        Assert.Equal(3, observers.Dropped);

        // And a later batch of the same turn follows it.
        var late = new OtlpBatch();
        late.Spans.Add(ToolSpan("trace-1", "grep_search"));
        store.Ingest(late);
        Assert.Empty(store.All);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("run-7f3a", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void TheMarkerIsReadAsAFlag(string value, bool marked) =>
        Assert.Equal(marked, ObserverRegistry.IsMarked(new Dictionary<string, AttrValue>
        {
            [ObserverRegistry.MarkerAttribute] = AttrValue.Str(value)
        }));

    [Fact]
    public void ABooleanMarkerIsReadAsABoolean()
    {
        Assert.True(ObserverRegistry.IsMarked(new Dictionary<string, AttrValue> { [ObserverRegistry.MarkerAttribute] = AttrValue.Bool(true) }));
        Assert.False(ObserverRegistry.IsMarked(new Dictionary<string, AttrValue> { [ObserverRegistry.MarkerAttribute] = AttrValue.Bool(false) }));
        Assert.False(ObserverRegistry.IsMarked(new Dictionary<string, AttrValue>()));
    }

    [Fact]
    public void WithNothingRegisteredAndNoMarkerNothingIsDropped()
    {
        var observers = new ObserverRegistry();
        var store = new SessionStore(observers);
        var batch = new OtlpBatch();
        batch.Logs.Add(ToolResult("users-own", "Edit"));

        store.Ingest(batch);

        Assert.Equal(0, observers.Dropped);
        Assert.NotNull(store.Get("users-own"));
    }

    [Fact]
    public async Task TheImportEndpointRefusesARegisteredSession()
    {
        // The scanner reaches the store through /api/import and nowhere else, so this one check
        // covers a transcript the assistant wrote despite being asked not to.
        using var factory = new WebApplicationFactory<SessionSummaryDto>();
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<ObserverRegistry>().Register("run-imported");

        var response = await client.PostAsJsonAsync("/api/import", new ImportRequest(
        [
            PersistedSession.From(Imported("run-imported")),
            PersistedSession.From(Imported("users-own"))
        ]));
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();

        Assert.Equal(1, result!.Imported);
        Assert.Contains(result.Rejected, r => r.StartsWith("run-imported:", StringComparison.Ordinal));
        var store = factory.Services.GetRequiredService<SessionStore>();
        Assert.Null(store.Get("run-imported"));
        Assert.NotNull(store.Get("users-own"));
    }

    [Fact]
    public async Task AMarkedOtlpBatchLeavesTheBaseEmptyAndIsCounted()
    {
        // Over the real wire, as a forced-on telemetry policy would deliver it.
        using var factory = new WebApplicationFactory<SessionSummaryDto>();
        using var client = factory.CreateClient();

        var resource = new ProtoWriter();
        resource.Message(1, TestOtlp.KeyValue("service.name", "copilot-cli"));
        resource.Message(1, TestOtlp.KeyValue(ObserverRegistry.MarkerAttribute, "true"));
        var span = TestOtlp.Span(TestOtlp.Trace(1), TestOtlp.SpanId(1), "chat gpt-5", T0, T0.AddSeconds(2), null,
            (Sem.ConversationId, "conv-cli-run"), (Sem.Operation, "chat"), (Sem.InputTokens, 1200L));
        var scopeSpans = new ProtoWriter();
        scopeSpans.Message(2, span);
        var resourceSpans = new ProtoWriter();
        resourceSpans.Message(1, resource.ToArray());
        resourceSpans.Message(2, scopeSpans.ToArray());
        var request = new ProtoWriter();
        request.Message(1, resourceSpans.ToArray());

        var content = new ByteArrayContent(request.ToArray());
        content.Headers.ContentType = new("application/x-protobuf");
        (await client.PostAsync("/v1/traces", content)).EnsureSuccessStatusCode();

        using var health = JsonDocument.Parse(await client.GetStringAsync("/api/health"));
        Assert.Equal(0, health.RootElement.GetProperty("sessions").GetInt32());
        Assert.Equal(1, health.RootElement.GetProperty("observerSignals").GetInt64());
    }

    [Fact]
    public void RegisteringNeedsAnId() =>
        Assert.Throws<ArgumentException>(() => new ObserverRegistry().Register(" "));

    private static CopilotSession Imported(string id) => new()
    {
        Id = id,
        Origin = SessionOrigin.LogImport,
        EmitterKind = EmitterKind.ClaudeCode,
        FirstSeen = T0,
        LastSeen = T0.AddMinutes(5),
        ChatCalls = 2
    };

    private static Dictionary<string, AttrValue> Resource(string service, bool marked)
    {
        var resource = new Dictionary<string, AttrValue> { ["service.name"] = AttrValue.Str(service) };
        if (marked) resource[ObserverRegistry.MarkerAttribute] = AttrValue.Str("true");
        return resource;
    }

    private static OtlpLogEvent ToolResult(string sessionId, string toolName, bool marked = false) => new()
    {
        EventName = "claude_code.tool_result",
        Time = T0,
        Resource = Resource("claude-code", marked),
        Attributes = new Dictionary<string, AttrValue>
        {
            ["session.id"] = AttrValue.Str(sessionId),
            ["tool_name"] = AttrValue.Str(toolName),
            ["success"] = AttrValue.Str("true"),
            ["duration_ms"] = AttrValue.Dbl(12)
        }
    };

    private static OtlpLogEvent ApiRequest(string sessionId) => new()
    {
        EventName = "claude_code.api_request",
        Time = T0,
        Resource = Resource("claude-code", marked: false),
        Attributes = new Dictionary<string, AttrValue>
        {
            ["session.id"] = AttrValue.Str(sessionId),
            ["model"] = AttrValue.Str("claude-sonnet-5"),
            ["input_tokens"] = AttrValue.Int(900),
            ["output_tokens"] = AttrValue.Int(120),
            ["duration_ms"] = AttrValue.Dbl(1800)
        }
    };

    private static OtlpMetricPoint TokenMetric(string sessionId) => new()
    {
        MetricName = "claude_code.token.usage",
        Kind = MetricKind.Sum,
        Time = T0,
        Value = 900,
        Count = 1,
        Resource = Resource("claude-code", marked: false),
        Attributes = new Dictionary<string, AttrValue>
        {
            ["session.id"] = AttrValue.Str(sessionId),
            ["type"] = AttrValue.Str("input"),
            ["model"] = AttrValue.Str("claude-sonnet-5")
        }
    };

    private static OtlpSpan ChatSpan(string? conversationId, string traceId, bool marked = false)
    {
        var attributes = new Dictionary<string, AttrValue>
        {
            [Sem.Operation] = AttrValue.Str("chat"),
            [Sem.InputTokens] = AttrValue.Int(1000)
        };
        if (conversationId is not null) attributes[Sem.ConversationId] = AttrValue.Str(conversationId);
        return new OtlpSpan
        {
            TraceId = traceId,
            SpanId = Guid.NewGuid().ToString("N")[..16],
            Name = "chat",
            Start = T0,
            End = T0.AddSeconds(1),
            Resource = Resource("copilot-chat", marked),
            Attributes = attributes
        };
    }

    private static OtlpSpan ToolSpan(string traceId, string toolName) => new()
    {
        TraceId = traceId,
        SpanId = Guid.NewGuid().ToString("N")[..16],
        Name = "execute_tool",
        Start = T0,
        End = T0.AddMilliseconds(40),
        Resource = Resource("copilot-chat", marked: false),
        Attributes = new Dictionary<string, AttrValue>
        {
            [Sem.Operation] = AttrValue.Str("execute_tool"),
            [Sem.ToolName] = AttrValue.Str(toolName)
        }
    };
}
