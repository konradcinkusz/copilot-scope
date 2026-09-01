using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using Xunit;

namespace CopilotScope.Tests;

// ------------------------------------------------------------- OTLP decoding

public class OtlpDecoderTests
{
    [Fact]
    public void DecodesChatSpanWithAttributesAndStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = TestOtlp.TracesRequest("vs-1",
            TestOtlp.Span(TestOtlp.Trace(1), TestOtlp.SpanId(1), "chat gpt-4o",
                now, now.AddMilliseconds(1200), error: "RateLimitError",
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.request.model", "gpt-4o"),
                ("gen_ai.usage.input_tokens", 1234L),
                ("copilot_chat.time_to_first_token", 456L)));

        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces(payload, batch);

        var span = Assert.Single(batch.Spans);
        Assert.Equal("chat gpt-4o", span.Name);
        Assert.Equal("chat", span.Attr(Sem.Operation));
        Assert.Equal(1234L, span.AttrLong(Sem.InputTokens));
        Assert.Equal(456L, span.AttrLong(Sem.TimeToFirstToken));
        Assert.Equal("RateLimitError", span.Attr(Sem.ErrorType));
        Assert.Equal(2, span.StatusCode);
        Assert.True(span.DurationMs is > 1100 and < 1300);
        Assert.Equal("copilot-chat", span.Resource[Sem.ServiceName].ToString());
    }

    [Fact]
    public void EmptyPayloadYieldsEmptyBatch()
    {
        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces([], batch);
        Assert.Empty(batch.Spans);
    }
}

// ------------------------------------------------------------- OTLP/JSON decoding
// VS Code's metrics/logs OTel exporters ship JSON regardless of exporterType/protocol
// settings (github/copilot-cli#2934) — OtlpJsonDecoder exists to not drop that data.

public class OtlpJsonDecoderTests
{
    [Fact]
    public void DecodesTraceSpanFromJson()
    {
        var traceIdB64 = Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 16).ToArray());
        var spanIdB64 = Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 8).ToArray());
        var startNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        var endNano = startNano + 500_000_000UL;
        var json = """
        {
          "resourceSpans": [{
            "resource": { "attributes": [{"key":"service.name","value":{"stringValue":"copilot-chat"}}] },
            "scopeSpans": [{
              "spans": [{
                "traceId": "TRACE_ID",
                "spanId": "SPAN_ID",
                "name": "chat gpt-4o",
                "startTimeUnixNano": "START_NANO",
                "endTimeUnixNano": "END_NANO",
                "attributes": [
                  {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                  {"key":"gen_ai.usage.input_tokens","value":{"intValue":"1234"}}
                ],
                "status": {"code": 2, "message": "boom"}
              }]
            }]
          }]
        }
        """
            .Replace("TRACE_ID", traceIdB64)
            .Replace("SPAN_ID", spanIdB64)
            .Replace("START_NANO", startNano.ToString())
            .Replace("END_NANO", endNano.ToString());

        var batch = new OtlpBatch();
        OtlpJsonDecoder.DecodeTraces(System.Text.Encoding.UTF8.GetBytes(json), batch);

        var span = Assert.Single(batch.Spans);
        Assert.Equal("chat gpt-4o", span.Name);
        Assert.Equal("chat", span.Attr("gen_ai.operation.name"));
        Assert.Equal(1234L, span.AttrLong("gen_ai.usage.input_tokens"));
        Assert.Equal(2, span.StatusCode);
        Assert.Equal(new string('1', 32), span.TraceId); // 16 bytes of 0x11 -> hex "11"*16
        Assert.Equal("copilot-chat", span.Resource["service.name"].ToString());
        Assert.True(span.DurationMs is > 400 and < 600);
    }

    [Fact]
    public void DecodesGaugeMetricFromJson()
    {
        var nowNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        var json = """
        {
          "resourceMetrics": [{
            "resource": { "attributes": [] },
            "scopeMetrics": [{
              "metrics": [{
                "name": "gen_ai.client.token.usage",
                "unit": "token",
                "sum": {
                  "dataPoints": [{
                    "timeUnixNano": "NOW_NANO",
                    "asInt": "42",
                    "attributes": []
                  }]
                }
              }]
            }]
          }]
        }
        """.Replace("NOW_NANO", nowNano.ToString());

        var batch = new OtlpBatch();
        OtlpJsonDecoder.DecodeMetrics(System.Text.Encoding.UTF8.GetBytes(json), batch);

        var point = Assert.Single(batch.Metrics);
        Assert.Equal("gen_ai.client.token.usage", point.MetricName);
        Assert.Equal(MetricKind.Sum, point.Kind);
        Assert.Equal(42, point.Value);
    }

    [Fact]
    public void DecodesLogRecordFromJson()
    {
        var nowNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        var json = """
        {
          "resourceLogs": [{
            "resource": { "attributes": [] },
            "scopeLogs": [{
              "logRecords": [{
                "timeUnixNano": "NOW_NANO",
                "severityNumber": 9,
                "eventName": "copilot_chat.user.feedback",
                "body": {"stringValue": "thumbs up"},
                "attributes": []
              }]
            }]
          }]
        }
        """.Replace("NOW_NANO", nowNano.ToString());

        var batch = new OtlpBatch();
        OtlpJsonDecoder.DecodeLogs(System.Text.Encoding.UTF8.GetBytes(json), batch);

        var log = Assert.Single(batch.Logs);
        Assert.Equal("copilot_chat.user.feedback", log.EventName);
        Assert.Equal("thumbs up", log.Body);
        Assert.Equal(9, log.SeverityNumber);
    }

    [Fact]
    public void EmptyJsonYieldsEmptyBatch()
    {
        var batch = new OtlpBatch();
        OtlpJsonDecoder.DecodeTraces(System.Text.Encoding.UTF8.GetBytes("{}"), batch);
        Assert.Empty(batch.Spans);
    }
}

// ----------------------------------------------------------- session routing

public class SessionStoreTests
{
    private static byte[] AgentTracePayload(string conversationId, byte[] traceId, DateTimeOffset now) =>
        TestOtlp.TracesRequest("vscode-window-1",
            TestOtlp.Span(traceId, TestOtlp.SpanId(10), "chat gpt-4o",
                now, now.AddMilliseconds(800), error: null,
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.response.model", "gpt-4o"),
                ("gen_ai.usage.input_tokens", 100L),
                ("gen_ai.usage.output_tokens", 20L),
                ("copilot_chat.time_to_first_token", 400L)),
            TestOtlp.Span(traceId, TestOtlp.SpanId(11), "execute_tool readFile",
                now, now.AddMilliseconds(50), error: null,
                ("gen_ai.operation.name", "execute_tool"),
                ("gen_ai.tool.name", "readFile")),
            TestOtlp.Span(traceId, TestOtlp.SpanId(12), "invoke_agent copilot",
                now, now.AddSeconds(3), error: null,
                ("gen_ai.operation.name", "invoke_agent"),
                ("gen_ai.conversation.id", conversationId),
                ("copilot_chat.turn_count", 1L)));

    [Fact]
    public void ChildSpansWithoutConversationIdLandInTheSameSession()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces(AgentTracePayload("conv-A", TestOtlp.Trace(7), DateTimeOffset.UtcNow), batch);

        var touched = store.Ingest(batch);

        Assert.Contains("conv-A", touched);
        var session = store.Get("conv-A");
        Assert.NotNull(session);
        Assert.Equal(1, session!.ChatCalls);
        Assert.Equal(1, session.ToolCalls);
        Assert.Equal(1, session.AgentInvocations);
        Assert.Equal(100, session.InputTokens);
        // No stray fallback session may exist.
        Assert.Single(store.All);
    }

    [Fact]
    public void LaterBatchOfSameTraceRoutesByTraceId()
    {
        var store = new SessionStore();
        var traceId = TestOtlp.Trace(9);
        var now = DateTimeOffset.UtcNow;

        var first = new OtlpBatch();
        OtlpDecoder.DecodeTraces(AgentTracePayload("conv-B", traceId, now), first);
        store.Ingest(first);

        // A follow-up chat span in the same trace, no conversation.id attribute.
        var second = new OtlpBatch();
        OtlpDecoder.DecodeTraces(TestOtlp.TracesRequest("vscode-window-1",
            TestOtlp.Span(traceId, TestOtlp.SpanId(13), "chat gpt-4o",
                now.AddSeconds(5), now.AddSeconds(6), error: null,
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.usage.input_tokens", 50L))), second);
        store.Ingest(second);

        Assert.Equal(2, store.Get("conv-B")!.ChatCalls);
        Assert.Equal(150, store.Get("conv-B")!.InputTokens);
        Assert.Single(store.All);
    }

    [Fact]
    public void RemoveDeletesSessionAndTraceMapping()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces(AgentTracePayload("conv-C", TestOtlp.Trace(21), DateTimeOffset.UtcNow), batch);
        store.Ingest(batch);

        Assert.True(store.Remove("conv-C"));
        Assert.Null(store.Get("conv-C"));
        Assert.False(store.Remove("conv-C"));
    }

    [Fact]
    public void UnattributedBucketIsMergedOnceConversationIdentifiesItself()
    {
        // CLI metrics/logs (and early spans) carry no conversation id; they must
        // wait in a per-emitter bucket and fold into the conversation session as
        // soon as one identifies itself — not linger as a ghost "unattributed" row.
        var store = new SessionStore();
        var now = DateTimeOffset.UtcNow;

        // Batch 1: a chat span with NO conversation id (unknown trace).
        var first = new OtlpBatch();
        OtlpDecoder.DecodeTraces(TestOtlp.TracesRequest("vscode-window-1",
            TestOtlp.Span(TestOtlp.Trace(41), TestOtlp.SpanId(41), "chat gpt-4o",
                now, now.AddSeconds(1), error: null,
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.usage.input_tokens", 77L))), first);
        store.Ingest(first);

        var bucket = Assert.Single(store.All);
        Assert.StartsWith("unattributed", bucket.Id);
        Assert.Equal(1, bucket.ChatCalls);

        // Batch 2: same emitter (same resource fingerprint) starts a real conversation.
        var second = new OtlpBatch();
        OtlpDecoder.DecodeTraces(AgentTracePayload("conv-M", TestOtlp.Trace(42), now.AddSeconds(5)), second);
        store.Ingest(second);

        var session = Assert.Single(store.All);
        Assert.Equal("conv-M", session.Id);
        Assert.Equal(2, session.ChatCalls);          // 1 merged from the bucket + 1 own
        Assert.Equal(177, session.InputTokens);      // 77 merged + 100 own
        Assert.Contains(store.DrainRemoved(), id => id.StartsWith("unattributed"));

        // Batch 3: an identity-less follow-up now routes straight to the conversation.
        var third = new OtlpBatch();
        OtlpDecoder.DecodeTraces(TestOtlp.TracesRequest("vscode-window-1",
            TestOtlp.Span(TestOtlp.Trace(43), TestOtlp.SpanId(43), "chat gpt-4o",
                now.AddSeconds(9), now.AddSeconds(10), error: null,
                ("gen_ai.operation.name", "chat"))), third);
        store.Ingest(third);

        Assert.Single(store.All);
        Assert.Equal(3, store.Get("conv-M")!.ChatCalls);
    }

    [Fact]
    public void CanonicalGithubCopilotNamespaceIsUnderstood()
    {
        // Copilot CLI emits github.copilot.* instead of legacy copilot_chat.*.
        var store = new SessionStore();
        var now = DateTimeOffset.UtcNow;
        var traceId = TestOtlp.Trace(51);
        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces(TestOtlp.TracesRequest("vscode-window-1",
            TestOtlp.Span(traceId, TestOtlp.SpanId(51), "invoke_agent copilot",
                now, now.AddSeconds(2), error: null,
                ("gen_ai.operation.name", "invoke_agent"),
                ("gen_ai.conversation.id", "conv-CLI"),
                ("github.copilot.turn_count", 3L)),
            TestOtlp.Span(traceId, TestOtlp.SpanId(52), "chat gpt-4o",
                now, now.AddSeconds(1), error: null,
                ("gen_ai.operation.name", "chat"),
                ("github.copilot.time_to_first_token", 512L))), batch);
        store.Ingest(batch);

        var session = store.Get("conv-CLI")!;
        Assert.Equal(3, session.Turns);
        Assert.Equal(512, Assert.Single(session.TtftMs));
    }

    [Fact]
    public void TranscriptIsCapturedFromContentAttributes()
    {
        var store = new SessionStore();
        var now = DateTimeOffset.UtcNow;
        var traceId = TestOtlp.Trace(31);
        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces(TestOtlp.TracesRequest("vscode-window-1",
            TestOtlp.Span(traceId, TestOtlp.SpanId(31), "invoke_agent copilot",
                now, now.AddSeconds(2), error: null,
                ("gen_ai.operation.name", "invoke_agent"),
                ("gen_ai.conversation.id", "conv-T")),
            TestOtlp.Span(traceId, TestOtlp.SpanId(32), "chat gpt-4o",
                now, now.AddSeconds(1), error: null,
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.input.messages", "Fix the bug in Ingest"),
                ("gen_ai.output.messages", "Done — tokens are now counted once."))), batch);
        store.Ingest(batch);

        var session = store.Get("conv-T")!;
        var entry = Assert.Single(session.Transcript);
        Assert.Equal("Fix the bug in Ingest", entry.Prompt);
        Assert.Equal("Done — tokens are now counted once.", entry.Response);
        Assert.Equal(0, entry.Turn);
    }

    [Theory]
    [InlineData("Please write a brief title for the following request:\nwrite 5 paragraphs about ww2", SessionKind.InternalTitleGeneration)]
    [InlineData("Summarize the following content in a SINGLE sentence (under 10 words) using past tense...", SessionKind.InternalSummary)]
    [InlineData("write 5 paragraphs about ww2", SessionKind.UserChat)]
    public void ClassifiesSessionKindFromTranscriptPrefix(string prompt, SessionKind expected)
    {
        var store = new SessionStore();
        var now = DateTimeOffset.UtcNow;
        var traceId = TestOtlp.Trace(41);
        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces(TestOtlp.TracesRequest("vscode-window-1",
            TestOtlp.Span(traceId, TestOtlp.SpanId(41), "invoke_agent copilot",
                now, now.AddSeconds(2), error: null,
                ("gen_ai.operation.name", "invoke_agent"),
                ("gen_ai.conversation.id", "conv-K")),
            TestOtlp.Span(traceId, TestOtlp.SpanId(42), "chat gpt-4o",
                now, now.AddSeconds(1), error: null,
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.input.messages", prompt),
                ("gen_ai.output.messages", "response"))), batch);
        store.Ingest(batch);

        Assert.Equal(expected, store.Get("conv-K")!.Kind);
    }
}

// ------------------------------------------------------------ quality engine

public class QualityEngineTests
{
    [Fact]
    public void EmptySessionScoresNearPriorWithLowConfidence()
    {
        var engine = new QualityEngine();
        var report = engine.Evaluate(new CopilotSession { Id = "empty" });

        Assert.InRange(report.Score, 50, 90);   // prior-dominated
        Assert.InRange(report.Confidence, 0, 0.35);
        Assert.NotEmpty(report.Components);
    }

    [Fact]
    public void ErrorsLowerTheScore()
    {
        var engine = new QualityEngine();
        var healthy = new CopilotSession { Id = "h" };
        var broken = new CopilotSession { Id = "b" };
        for (var i = 0; i < 10; i++)
        {
            healthy.ChatCalls++; broken.ChatCalls++;
        }
        broken.ChatErrors = 6;

        Assert.True(engine.Evaluate(broken).Score < engine.Evaluate(healthy).Score);
    }

    [Fact]
    public void CleanAndErroredSessionsAreClearlySeparated()
    {
        // Regression for the v1 flattening bug: neutral priors at full weight used
        // to pin every ordinary session to ~80 regardless of what happened in it.
        var engine = new QualityEngine();

        var clean = new CopilotSession { Id = "clean" };
        clean.ChatCalls = 10; clean.ToolCalls = 10;
        clean.TtftMs.AddRange([400, 450, 500]);

        var errored = new CopilotSession { Id = "errored" };
        errored.ChatCalls = 10; errored.ChatErrors = 3;
        errored.ToolCalls = 10; errored.ToolErrors = 4;
        errored.TtftMs.AddRange([3000, 3500, 4000]);

        var gap = engine.Evaluate(clean).Score - engine.Evaluate(errored).Score;
        Assert.True(gap >= 20, $"Expected a clear separation, got only {gap:F1} points.");
    }
}

// ----------------------------------------------------------- segment analysis

public class SegmentAnalyzerTests
{
    private static TurnStat Turn(int index, int chatErrors = 0, int toolErrors = 0, double ttft = 500, int toolCalls = 2)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(index);
        return new TurnStat
        {
            TraceId = $"trace-{index}", Index = index, Start = start, End = start.AddSeconds(10),
            ChatCalls = 2, ChatErrors = chatErrors, ToolCalls = toolCalls, ToolErrors = toolErrors,
            InputTokens = 1000, OutputTokens = 200, TtftTotalMs = ttft * 2, TtftCount = 2
        };
    }

    [Fact]
    public void WorstTurnIsTheOneWithErrors()
    {
        var session = new CopilotSession { Id = "seg" };
        session.TurnList.AddRange([Turn(0), Turn(1, chatErrors: 1, toolErrors: 2), Turn(2)]);

        var analysis = SegmentAnalyzer.Analyze(session);

        Assert.Equal(3, analysis.Turns.Count);
        Assert.Equal(1, analysis.WorstIndex);
        Assert.NotEqual(1, analysis.BestIndex);
        var worst = analysis.Turns.Single(t => t.Index == 1);
        Assert.True(worst.Score < 1.0);
        Assert.Contains(worst.Reasons, r => r.Contains("LLM error"));
    }

    [Fact]
    public void SlowTurnRelativeToMedianIsPenalized()
    {
        var session = new CopilotSession { Id = "lat" };
        session.TurnList.AddRange([Turn(0, ttft: 400), Turn(1, ttft: 400), Turn(2, ttft: 2000)]);

        var analysis = SegmentAnalyzer.Analyze(session);

        Assert.Equal(2, analysis.WorstIndex);
        Assert.Contains(analysis.Turns.Single(t => t.Index == 2).Reasons,
            r => r.Contains("session median"));
    }

    [Fact]
    public void EmptySessionYieldsNoTurns()
    {
        var analysis = SegmentAnalyzer.Analyze(new CopilotSession { Id = "none" });
        Assert.Empty(analysis.Turns);
        Assert.Null(analysis.BestIndex);
    }
}

// ------------------------------------------------------- persistence roundtrip

public class PersistedSessionTests
{
    [Fact]
    public void RoundtripPreservesAggregatesTranscriptAndTurns()
    {
        var s = new CopilotSession { Id = "rt", AgentName = "copilot", Repository = "https://github.com/x/y" };
        s.ChatCalls = 7; s.ChatErrors = 1; s.ToolCalls = 12; s.InputTokens = 5000;
        s.TtftMs.AddRange([300, 500, 900]);
        s.ModelCalls["gpt-4o"] = 5;
        s.Tools["readFile"] = (3, 1, 210.5);
        s.ErrorTypes["RateLimitError"] = 1;
        s.AddEvent(new SessionEvent(DateTimeOffset.UtcNow, "chat", "chat gpt-4o"));
        s.AddTranscript(DateTimeOffset.UtcNow, "gpt-4o", "prompt text", "response text", 0);
        s.TurnList.Add(new TurnStat { TraceId = "t0", Index = 0, Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow.AddSeconds(4), ChatCalls = 2 });

        var restored = PersistedSession.From(s).ToSession();

        Assert.Equal(s.Id, restored.Id);
        Assert.Equal(7, restored.ChatCalls);
        Assert.Equal(5000, restored.InputTokens);
        Assert.Equal(3, restored.TtftMs.Count);
        Assert.Equal(5, restored.ModelCalls["gpt-4o"]);
        Assert.Equal((3, 1, 210.5), restored.Tools["readFile"]);
        Assert.Single(restored.Transcript);
        Assert.Equal("prompt text", restored.Transcript[0].Prompt);
        var turn = Assert.Single(restored.TurnList);
        Assert.Equal(2, turn.ChatCalls);
        Assert.Single(restored.RecentEvents);
    }
}

// --------------------------------------------------------------- insights

public class InsightAnalyzerTests
{
    [Fact]
    public void EditSurvivalWeighsNoRevertHigher()
    {
        var s = new CopilotSession { Id = "surv" };
        s.SurvivalFourGram.AddRange([0.2, 0.2]);   // heavy textual rework...
        s.SurvivalNoRevert.AddRange([1.0, 1.0]);   // ...but nothing reverted

        var report = new EditSurvivalAnalyzer().Analyze(s);

        Assert.Equal("ok", report.Status);
        Assert.NotNull(report.Score);
        Assert.InRange(report.Score!.Value, 0.67, 0.69); // 0.4*0.2 + 0.6*1.0 = 0.68
    }

    [Fact]
    public void ThroughputPenalizesRejections()
    {
        var accepted = new CopilotSession { Id = "a", EditsAccepted = 8, EditsRejected = 2, LinesAdded = 40, Turns = 4, OutputTokens = 2000 };
        var rejected = new CopilotSession { Id = "r", EditsAccepted = 2, EditsRejected = 8, LinesAdded = 40, Turns = 4, OutputTokens = 2000 };

        var analyzer = new ThroughputAnalyzer();
        Assert.True(analyzer.Analyze(accepted).Score > analyzer.Analyze(rejected).Score);
    }

    [Fact]
    public void LatencyUtilityFlagsAbandonmentRisk()
    {
        var s = new CopilotSession { Id = "lat" };
        s.TtftMs.AddRange([500, 600, 9000, 12000]);

        var report = new LatencyUtilityAnalyzer().Analyze(s);

        Assert.Equal("ok", report.Status);
        Assert.Contains(report.Findings, f => f.Contains("abandonment"));
    }

    [Fact]
    public void TokenEconomicsComputesCostAndCacheSavings()
    {
        var s = new CopilotSession { Id = "eco", InputTokens = 1_000_000, CacheReadTokens = 1_000_000, Turns = 2 };
        s.ModelUsage["gpt-4o"] = new ModelStat { Calls = 10, InputTokens = 1_000_000, OutputTokens = 100_000, CacheReadTokens = 1_000_000 };

        var report = new TokenEconomicsAnalyzer(new PricingOptions()).Analyze(s);

        Assert.Equal("ok", report.Status);
        // gpt-4o: 1M*2.5 + 0.1M*10 + 1M*1.25 = 2.5 + 1.0 + 1.25 = $4.75
        Assert.Contains(report.Metrics, m => m.Label == "estimated session cost" && m.Value.Contains("4.75"));
        // savings: 1M * (2.5 - 1.25) = $1.25
        Assert.Contains(report.Metrics, m => m.Label.StartsWith("cache savings") && m.Value.Contains("1.25"));
    }

    [Fact]
    public void PricingPrefersLongestPrefixMatch()
    {
        var pricing = new PricingOptions();
        Assert.Equal(0.15, pricing.Resolve("gpt-4o-mini-2024").Input);
        Assert.Equal(2.50, pricing.Resolve("gpt-4o-2024-11").Input);
        Assert.Equal(3.0, pricing.Resolve("some-unknown-model").Input);
    }

    [Fact]
    public void WorkflowFrictionDetectsMarkersAndRephrasing()
    {
        var s = new CopilotSession { Id = "fr" };
        var t = DateTimeOffset.UtcNow;
        s.AddTranscript(t, "gpt-4o", "Please add a retry policy to the forwarder queue", "done", 0);
        s.AddTranscript(t.AddMinutes(1), "gpt-4o", "add a retry policy to the forwarder queue please", "done", 1); // rephrase
        s.AddTranscript(t.AddMinutes(2), "gpt-4o", "this still doesn't work, wrong again!!", "sorry", 2);          // strong

        var report = TestFriction.Analyzer().Analyze(s);

        Assert.Equal("ok", report.Status);
        Assert.True(report.Score >= 0.4, $"index was {report.Score}");
        Assert.Contains(report.Findings, f => f.Contains("strong marker"));
        Assert.Contains(report.Findings, f => f.Contains("rephrasing"));
    }

    [Fact]
    public void WorkflowFrictionExtractsUserTextFromJsonMessages()
    {
        var raw = """[{"role":"system","content":"be nice"},{"role":"user","content":"why is this wrong again"}]""";
        var text = WorkflowFrictionAnalyzer.ExtractUserText(raw);
        Assert.Contains("wrong again", text);
        Assert.DoesNotContain("be nice", text);
    }

    [Fact]
    public void PipelineSurvivesAFailingAnalyzer()
    {
        var pipeline = new InsightPipeline([new ThrowingAnalyzer(), new LatencyUtilityAnalyzer()]);
        var reports = pipeline.Analyze(new CopilotSession { Id = "x" });
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, r => r.Findings.Any(f => f.Contains("Analyzer failed")));
    }

    private sealed class ThrowingAnalyzer : IInsightAnalyzer
    {
        public InsightReport Analyze(CopilotSession session) => throw new InvalidOperationException("boom");
    }
}
