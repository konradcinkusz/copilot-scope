// CopilotScope.TelemetryGen
// Simulates GitHub Copilot Chat OTLP telemetry (spans, metrics, log events)
// and posts it to the collector over OTLP/HTTP protobuf.
//
//   dotnet run --project tools/CopilotScope.TelemetryGen -- [endpoint] [conversationId]
//
// Default endpoint: http://localhost:4318

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

var endpoint = (args.Length > 0 ? args[0] : "http://localhost:4318").TrimEnd('/');
var conversationId = args.Length > 1 ? args[1] : $"conv-{Guid.NewGuid().ToString()[..8]}";
var apiKey = Environment.GetEnvironmentVariable("COPILOTSCOPE_API_KEY");

var http = new HttpClient();
var rng = Random.Shared;
var vsCodeSession = Guid.NewGuid().ToString();

Console.WriteLine($"Simulating Copilot Chat session '{conversationId}' → {endpoint}");

string[] tools = ["readFile", "editFile", "runCommand", "grepSearch", "listDirectory"];
string[] models = ["gpt-4o", "claude-sonnet-4.5"];

var postCounter = 0;

string[] samplePrompts =
[
    "Refactor SessionStore.Ingest to avoid double-counting tokens.",
    "Why does the OTLP decoder return an empty batch for this payload?",
    "Add a retry policy with exponential backoff to the forwarder.",
    "Write unit tests for the quality engine prior blending.",
    "Explain the difference between gauge and sum metric points."
];

string[] sampleResponses =
[
    "I updated the aggregation so chat spans are the single token source; invoke_agent totals are ignored.",
    "The payload was gzip-compressed; the request body must be decompressed before protobuf decoding.",
    "Added a bounded channel with 3 retries and exponential backoff starting at 500 ms.",
    "Added tests covering the empty-session prior and the confidence ramp at 5 samples.",
    "A gauge reports the last value; a sum accumulates deltas or cumulative totals over time."
];

for (var turn = 1; turn <= 5; turn++)
{
    var traceId = RandomBytes(16);
    var agentSpanId = RandomBytes(8);
    var now = DateTimeOffset.UtcNow;
    var spans = new List<byte[]>();
    var model = models[rng.Next(models.Length)];

    // chat span #1 — LLM plans tool calls
    var chatStart = now;
    var ttft = 350 + rng.Next(1200);
    spans.Add(Span(traceId, RandomBytes(8), agentSpanId, $"chat {model}",
        chatStart, chatStart.AddMilliseconds(ttft + rng.Next(2000)),
        error: null,
        attrs: ChatAttrs(model, input: 1500 + rng.Next(4000), output: 150 + rng.Next(500),
                         cacheRead: rng.Next(3000), ttftMs: ttft, turnIdx: turn)));

    // 1-3 tool spans
    var toolCount = 1 + rng.Next(3);
    for (var t = 0; t < toolCount; t++)
    {
        var tool = tools[rng.Next(tools.Length)];
        var failed = rng.NextDouble() < 0.12;
        var ts = chatStart.AddSeconds(2 + t);
        spans.Add(Span(traceId, RandomBytes(8), agentSpanId, $"execute_tool {tool}",
            ts, ts.AddMilliseconds(40 + rng.Next(1800)),
            error: failed ? "ToolExecutionError" : null,
            attrs:
            [
                ("gen_ai.operation.name", "execute_tool"),
                ("gen_ai.tool.name", tool),
                ("gen_ai.tool.type", "function")
            ]));
    }

    // chat span #2 — final answer
    var ttft2 = 300 + rng.Next(900);
    var chat2Start = chatStart.AddSeconds(4 + toolCount);
    spans.Add(Span(traceId, RandomBytes(8), agentSpanId, $"chat {model}",
        chat2Start, chat2Start.AddMilliseconds(ttft2 + rng.Next(3000)),
        error: rng.NextDouble() < 0.05 ? "RateLimitError" : null,
        attrs: ChatAttrs(model, input: 2500 + rng.Next(5000), output: 300 + rng.Next(900),
                         cacheRead: 1000 + rng.Next(4000), ttftMs: ttft2, turnIdx: turn)));

    // invoke_agent root span
    spans.Add(Span(traceId, agentSpanId, parent: null, "invoke_agent copilot",
        now, chat2Start.AddSeconds(4),
        error: null,
        attrs:
        [
            ("gen_ai.operation.name", "invoke_agent"),
            ("gen_ai.agent.name", "copilot"),
            ("gen_ai.conversation.id", conversationId),
            ("gen_ai.request.model", model),
            ("github.copilot.git.repository", "https://github.com/example/search-service"),
            ("github.copilot.git.branch", "feature/otel-dashboard"),
            ("copilot_chat.turn_count", 2L)
        ]));

    await Post("/v1/traces", TracesRequest(vsCodeSession, spans));

    // Metrics: edit acceptance + feedback + lines of code
    var metrics = new List<byte[]>();
    if (rng.NextDouble() < 0.8)
        metrics.Add(SumMetric("copilot_chat.edit.acceptance.count", 1,
            [("gen_ai.conversation.id", conversationId),
             ("copilot_chat.edit.action", rng.NextDouble() < 0.75 ? "accepted" : "rejected")]));
    if (rng.NextDouble() < 0.5)
        metrics.Add(SumMetric("copilot_chat.lines_of_code.count", 5 + rng.Next(60),
            [("gen_ai.conversation.id", conversationId), ("copilot_chat.lines.kind", "added")]));
    if (rng.NextDouble() < 0.3)
        metrics.Add(SumMetric("copilot_chat.user.feedback.count", 1,
            [("gen_ai.conversation.id", conversationId),
             ("copilot_chat.feedback.vote", rng.NextDouble() < 0.8 ? "up" : "down")]));
    if (metrics.Count > 0)
        await Post("/v1/metrics", MetricsRequest(vsCodeSession, metrics));

    // Log event: agent turn
    await Post("/v1/logs", LogsRequest(vsCodeSession,
    [
        LogRecord("copilot_chat.agent.turn", $"turn {turn} completed",
            [("gen_ai.conversation.id", conversationId)])
    ]));

    Console.WriteLine($"  turn {turn}/5 sent");
    await Task.Delay(1500);
}

Console.WriteLine("Done. Open the dashboard to inspect the session.");
return;

// ============================================================== HTTP =========

async Task Post(string path, byte[] body)
{
    // Alternate plain/gzip payloads — real OTLP exporters often compress,
    // and this keeps the collector's decompression path exercised.
    var useGzip = Interlocked.Increment(ref postCounter) % 2 == 0;
    if (useGzip)
    {
        using var buf = new MemoryStream();
        await using (var gz = new System.IO.Compression.GZipStream(buf, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(body);
        body = buf.ToArray();
    }

    using var req = new HttpRequestMessage(HttpMethod.Post, endpoint + path)
    {
        Content = new ByteArrayContent(body)
    };
    req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-protobuf");
    if (useGzip) req.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
    if (apiKey is not null) req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
    var resp = await http.SendAsync(req);
    if (!resp.IsSuccessStatusCode)
        Console.WriteLine($"  ! {path} → HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
}

static byte[] RandomBytes(int n)
{
    var b = new byte[n];
    RandomNumberGenerator.Fill(b);
    return b;
}



List<(string, object)> ChatAttrs(string model, long input, long output, long cacheRead, long ttftMs, int turnIdx) =>
[
    ("gen_ai.operation.name", "chat"),
    ("gen_ai.request.model", model),
    ("gen_ai.response.model", model),
    ("gen_ai.usage.input_tokens", input),
    ("gen_ai.usage.output_tokens", output),
    ("gen_ai.usage.cache_read.input_tokens", cacheRead),
    ("copilot_chat.time_to_first_token", ttftMs),
    // Simulates github.copilot.chat.otel.captureContent=true
    ("gen_ai.input.messages", samplePrompts[turnIdx % samplePrompts.Length]),
    ("gen_ai.output.messages", sampleResponses[turnIdx % sampleResponses.Length])
];

// ==================================================== OTLP protobuf writer ===
// Field numbers mirror open-telemetry/opentelemetry-proto.

static byte[] TracesRequest(string sessionId, List<byte[]> spans)
{
    var scopeSpans = new ProtoWriter();
    foreach (var s in spans) scopeSpans.Message(2, s);

    var resourceSpans = new ProtoWriter();
    resourceSpans.Message(1, Resource(sessionId));
    resourceSpans.Message(2, scopeSpans.ToArray());

    var req = new ProtoWriter();
    req.Message(1, resourceSpans.ToArray());
    return req.ToArray();
}

static byte[] MetricsRequest(string sessionId, List<byte[]> metrics)
{
    var scopeMetrics = new ProtoWriter();
    foreach (var m in metrics) scopeMetrics.Message(2, m);

    var resourceMetrics = new ProtoWriter();
    resourceMetrics.Message(1, Resource(sessionId));
    resourceMetrics.Message(2, scopeMetrics.ToArray());

    var req = new ProtoWriter();
    req.Message(1, resourceMetrics.ToArray());
    return req.ToArray();
}

static byte[] LogsRequest(string sessionId, List<byte[]> records)
{
    var scopeLogs = new ProtoWriter();
    foreach (var r in records) scopeLogs.Message(2, r);

    var resourceLogs = new ProtoWriter();
    resourceLogs.Message(1, Resource(sessionId));
    resourceLogs.Message(2, scopeLogs.ToArray());

    var req = new ProtoWriter();
    req.Message(1, resourceLogs.ToArray());
    return req.ToArray();
}

static byte[] Resource(string sessionId)
{
    var res = new ProtoWriter();
    res.Message(1, KeyValue("service.name", "copilot-chat"));
    res.Message(1, KeyValue("service.version", "0.31.0"));
    res.Message(1, KeyValue("session.id", sessionId));
    return res.ToArray();
}

static byte[] Span(byte[] traceId, byte[] spanId, byte[]? parent, string name,
    DateTimeOffset start, DateTimeOffset end, string? error, List<(string, object)> attrs)
{
    var w = new ProtoWriter();
    w.Bytes(1, traceId);
    w.Bytes(2, spanId);
    if (parent is not null) w.Bytes(4, parent);
    w.String(5, name);
    w.Fixed64(7, (ulong)start.ToUnixTimeMilliseconds() * 1_000_000UL);
    w.Fixed64(8, (ulong)end.ToUnixTimeMilliseconds() * 1_000_000UL);
    foreach (var (k, v) in attrs) w.Message(9, KeyValue(k, v));
    if (error is not null)
    {
        w.Message(9, KeyValue("error.type", error));
        var status = new ProtoWriter();
        status.String(2, error);
        status.Varint(3, 2); // STATUS_CODE_ERROR
        w.Message(15, status.ToArray());
    }
    return w.ToArray();
}

static byte[] SumMetric(string name, double value, List<(string, object)> attrs)
{
    var point = new ProtoWriter();
    point.Fixed64(3, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    point.Double(4, value);
    foreach (var (k, v) in attrs) point.Message(7, KeyValue(k, v));

    var sum = new ProtoWriter();
    sum.Message(1, point.ToArray());
    sum.Varint(2, 2);  // AGGREGATION_TEMPORALITY_CUMULATIVE
    sum.Varint(3, 1);  // is_monotonic

    var metric = new ProtoWriter();
    metric.String(1, name);
    metric.Message(7, sum.ToArray());
    return metric.ToArray();
}

static byte[] LogRecord(string eventName, string body, List<(string, object)> attrs)
{
    var anyValue = new ProtoWriter();
    anyValue.String(1, body);

    var w = new ProtoWriter();
    w.Fixed64(1, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    w.Varint(2, 9); // SEVERITY_NUMBER_INFO
    w.Message(5, anyValue.ToArray());
    foreach (var (k, v) in attrs) w.Message(6, KeyValue(k, v));
    w.String(12, eventName);
    return w.ToArray();
}

static byte[] KeyValue(string key, object value)
{
    var any = new ProtoWriter();
    switch (value)
    {
        case string s: any.String(1, s); break;
        case bool b: any.Varint(2, b ? 1UL : 0UL); break;
        case long l: any.Varint(3, unchecked((ulong)l)); break;
        case int i: any.Varint(3, unchecked((ulong)(long)i)); break;
        case double d: any.Double(4, d); break;
        default: any.String(1, value.ToString() ?? ""); break;
    }
    var kv = new ProtoWriter();
    kv.String(1, key);
    kv.Message(2, any.ToArray());
    return kv.ToArray();
}

/// <summary>Minimal protobuf wire-format writer.</summary>
file sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public void Varint(int field, ulong value) { Tag(field, 0); WriteVarint(value); }

    public void Fixed64(int field, ulong value)
    {
        Tag(field, 1);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        _ms.Write(b);
    }

    public void Double(int field, double value) => Fixed64(field, BitConverter.DoubleToUInt64Bits(value));
    public void Bytes(int field, byte[] value) { Tag(field, 2); WriteVarint((ulong)value.Length); _ms.Write(value); }
    public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public void Message(int field, byte[] encoded) => Bytes(field, encoded);

    public byte[] ToArray() => _ms.ToArray();

    private void Tag(int field, int wire) => WriteVarint((ulong)(field << 3 | wire));

    private void WriteVarint(ulong v)
    {
        while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
        _ms.WriteByte((byte)v);
    }
}
