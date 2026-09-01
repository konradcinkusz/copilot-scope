using System.IO.Compression;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;

// CopilotScope.FixtureCapture — record what a real assistant actually sends.
//
// Every OTLP payload in the test suite is hand-built from a reading of vendor docs. That makes
// the "five assistants land in one schema" claim an assertion rather than a demonstration: if a
// vendor renames an attribute, ingest keeps succeeding and the scores quietly go wrong, because
// nothing in the repository has ever seen a real payload.
//
// This is a recording proxy. Point an assistant at it instead of the collector; it captures each
// payload to disk and forwards it upstream unchanged, so a capture session is also a working
// session.
//
//   dotnet run --project tools/CopilotScope.FixtureCapture -- \
//       --assistant vscode --version 1.119 --out tests/fixtures
//
// Then point the assistant at http://localhost:4319 and use it normally.
//
// SAFETY: raw payloads are committed to a public repository, so the capture REFUSES any batch
// carrying prompt or response text (content capture enabled). Metadata-only telemetry — the
// default for every supported assistant — has nothing sensitive in it; captured content would.
// The refusal is the point: an --allow-content escape hatch would eventually be used by someone
// in a hurry, so there isn't one. Turn content capture off in the client, capture, turn it back on.

var assistant = Arg("--assistant") ?? "unknown";
var version = Arg("--version") ?? "unspecified";
var outRoot = Arg("--out") ?? "tests/fixtures";
var upstream = Arg("--upstream") ?? "http://localhost:4318";
var listen = Arg("--listen") ?? "http://localhost:4319";

var outDir = Path.Combine(outRoot, assistant, version);
Directory.CreateDirectory(outDir);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(listen);
builder.Services.AddHttpClient("upstream", c => c.BaseAddress = new Uri(upstream));

var app = builder.Build();
var captured = 0;
var refused = 0;

app.MapPost("/v1/{signal}", async (string signal, HttpRequest request, IHttpClientFactory clients) =>
{
    if (signal is not ("traces" or "metrics" or "logs")) return Results.NotFound();

    // Read once, use twice: inspect a decompressed copy, forward the bytes exactly as received —
    // re-encoding would defeat the whole point of capturing the real wire format.
    using var raw = new MemoryStream();
    await request.Body.CopyToAsync(raw);
    var body = raw.ToArray();

    var encoding = request.Headers.ContentEncoding.ToString();
    var decoded = Decompress(body, encoding);
    var contentType = request.ContentType ?? "";
    var isJson = !contentType.Contains("protobuf", StringComparison.OrdinalIgnoreCase)
              && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    if (CarriesContent(decoded, signal, isJson, out var reason))
    {
        refused++;
        app.Logger.LogWarning(
            "REFUSED a {Signal} batch: {Reason}. Fixtures are committed to a public repository, so " +
            "captured prompt/response text must never reach one. Disable content capture in the " +
            "client, capture, then re-enable it.", signal, reason);
    }
    else
    {
        // Sequence-numbered so the batch order a real session produced is reproducible, and
        // the extension records how to read it back.
        var index = Interlocked.Increment(ref captured);
        var extension = isJson ? "json" : "pb";
        var name = $"{index:D4}-{signal}.{extension}";
        await File.WriteAllBytesAsync(Path.Combine(outDir, name), decoded);
        app.Logger.LogInformation("Captured {Name} ({Bytes} bytes)", name, decoded.Length);
    }

    // Forward untouched, so capturing does not change what the collector sees.
    var forward = new HttpRequestMessage(HttpMethod.Post, $"/v1/{signal}")
    {
        Content = new ByteArrayContent(body)
    };
    foreach (var header in new[] { "Content-Type", "Content-Encoding", "x-api-key", "Authorization" })
        if (request.Headers.TryGetValue(header, out var value))
            forward.Content.Headers.TryAddWithoutValidation(header, value.ToArray());

    try
    {
        using var response = await clients.CreateClient("upstream").SendAsync(forward);
        var payload = await response.Content.ReadAsByteArrayAsync();
        return Results.Bytes(payload, response.Content.Headers.ContentType?.ToString() ?? "application/x-protobuf");
    }
    catch (Exception ex)
    {
        // Capturing is the job; an unreachable collector should not stop it.
        app.Logger.LogWarning(ex, "Upstream {Upstream} unreachable — captured anyway.", upstream);
        return Results.Bytes(Array.Empty<byte>(), "application/x-protobuf");
    }
});

app.MapGet("/", () => Results.Text(
    $"CopilotScope fixture capture\n" +
    $"  assistant : {assistant} {version}\n" +
    $"  writing to: {Path.GetFullPath(outDir)}\n" +
    $"  forwarding: {upstream}\n" +
    $"  captured  : {captured}, refused (carried content): {refused}\n"));

app.Logger.LogInformation(
    """
    Fixture capture listening on {Listen}
      assistant  : {Assistant} {Version}
      writing to : {OutDir}
      forwarding : {Upstream}
    Point the assistant's OTLP endpoint at {Listen} and use it normally.
    Batches carrying prompt/response text are refused, not written.
    """,
    listen, assistant, version, Path.GetFullPath(outDir), upstream, listen);

app.Run();

static byte[] Decompress(byte[] body, string contentEncoding)
{
    if (body.Length == 0) return body;
    using var source = new MemoryStream(body);
    Stream stream = contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)
        ? new GZipStream(source, CompressionMode.Decompress)
        : contentEncoding.Contains("deflate", StringComparison.OrdinalIgnoreCase)
            ? new DeflateStream(source, CompressionMode.Decompress)
            : source;
    using var target = new MemoryStream();
    stream.CopyTo(target);
    return target.ToArray();
}

/// <summary>
/// Decodes the batch and looks for prompt/response text. Errs toward refusing: a batch that
/// cannot be decoded is refused too, because "we could not read it" is not evidence that it is
/// safe to publish.
/// </summary>
static bool CarriesContent(byte[] payload, string signal, bool isJson, out string reason)
{
    var batch = new OtlpBatch();
    try
    {
        switch (signal)
        {
            case "traces": if (isJson) OtlpJsonDecoder.DecodeTraces(payload, batch); else OtlpDecoder.DecodeTraces(payload, batch); break;
            case "metrics": if (isJson) OtlpJsonDecoder.DecodeMetrics(payload, batch); else OtlpDecoder.DecodeMetrics(payload, batch); break;
            case "logs": if (isJson) OtlpJsonDecoder.DecodeLogs(payload, batch); else OtlpDecoder.DecodeLogs(payload, batch); break;
        }
    }
    catch (Exception ex)
    {
        reason = $"could not decode it to check for content ({ex.GetType().Name})";
        return true;
    }

    string[] contentKeys = [Sem.InputMessages, Sem.OutputMessages, Sem.Prompt, Sem.Completion];

    foreach (var span in batch.Spans)
        foreach (var key in contentKeys)
            if (span.Attributes.ContainsKey(key)) { reason = $"span attribute '{key}'"; return true; }

    foreach (var log in batch.Logs)
    {
        foreach (var key in contentKeys)
            if (log.Attributes.ContainsKey(key)) { reason = $"log attribute '{key}'"; return true; }

        // gen_ai.content.* events carry the text in the body rather than an attribute.
        if (log.EventName is "gen_ai.content.prompt" or "gen_ai.content.completion"
            or "gen_ai.user.message" or "gen_ai.assistant.message" or "gen_ai.choice")
        {
            reason = $"log event '{log.EventName}' carries message content";
            return true;
        }
    }

    reason = "";
    return false;
}

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
