using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Replays real captured payloads through the real decoder and session store.
///
/// Every other OTLP payload in this suite is hand-built from a reading of vendor docs, so the
/// "five assistants land in one schema" claim is currently an assertion rather than a
/// demonstration — and it fails silently: a renamed attribute keeps ingest returning 200 while
/// the counters it feeds go to zero. A fixture captured from a real client is the only thing
/// that turns that into a failing test.
///
/// Fixtures are captured with tools/CopilotScope.FixtureCapture and live under tests/fixtures/
/// (see the README there). No real captures are committed yet — capturing needs a machine
/// running the assistants — so these tests currently assert over an empty set and light up the
/// moment one is added. That is deliberate: the harness landing first is what makes capturing a
/// `git add` rather than a project.
/// </summary>
public class FixtureGoldenTests
{
    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string? FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Directory name → the emitter a batch from it must classify as.</summary>
    private static readonly Dictionary<string, EmitterKind> ExpectedEmitter =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["vscode"] = EmitterKind.VSCode,
            ["cli"] = EmitterKind.CLI,
            ["claude-code"] = EmitterKind.ClaudeCode,
            ["cowork"] = EmitterKind.Cowork,
            ["cursor"] = EmitterKind.Cursor,
        };

    /// <summary>
    /// Stands in when no fixtures are committed yet. xUnit fails a theory with an empty data set,
    /// and a green suite that silently checks nothing is worse than a visible placeholder.
    /// </summary>
    private const string NoFixturesYet = "(none captured yet)";

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        if (FixtureRoot() is { } root)
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".pb", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f, StringComparer.Ordinal))
                data.Add(Path.GetRelativePath(root, file));

        if (data.Count == 0) data.Add(NoFixturesYet);
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AFixtureDecodesAndRoutesToItsAssistant(string relativePath)
    {
        // Capturing needs a machine running the assistants, which CI is not. The harness is
        // here so that adding one is a `git add`; see tests/fixtures/README.md.
        if (relativePath == NoFixturesYet) return;

        var root = FixtureRoot()!;
        var payload = File.ReadAllBytes(Path.Combine(root, relativePath));
        var signal = SignalOf(relativePath);
        var isJson = relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        var batch = new OtlpBatch();
        Decode(payload, signal, isJson, batch);

        // A batch that decodes to nothing means the decoder silently skipped everything — the
        // exact failure mode this suite exists to catch.
        Assert.True(batch.Spans.Count + batch.Metrics.Count + batch.Logs.Count > 0,
            $"{relativePath} decoded to an empty batch — the wire format changed, or the decoder no longer understands it.");

        var store = new SessionStore();
        var touched = store.Ingest(batch, sourceId: "fixture");
        Assert.NotEmpty(touched);

        // The directory names the assistant, so this asserts the claim directly: telemetry from
        // this client still classifies as this client.
        var assistant = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[0];
        if (ExpectedEmitter.TryGetValue(assistant, out var expected))
        {
            var kinds = store.All.Select(s => s.EmitterKind).Distinct().ToList();
            Assert.True(kinds.Contains(expected),
                $"{relativePath} classified as [{string.Join(", ", kinds)}], expected {expected}.");
        }
    }

    [Fact]
    public void FixtureDirectoriesUseKnownAssistantNames()
    {
        // A typo'd directory silently disables the emitter assertion above, which would leave the
        // fixture passing while checking nothing.
        if (FixtureRoot() is not { } root) return;

        var unknown = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !ExpectedEmitter.ContainsKey(name))
            .ToList();

        Assert.True(unknown.Count == 0,
            $"Unknown assistant directory: {string.Join(", ", unknown)}. " +
            $"Expected one of: {string.Join(", ", ExpectedEmitter.Keys)}.");
    }

    private static string SignalOf(string path) =>
        Path.GetFileNameWithoutExtension(path).Split('-').Last();

    private static void Decode(byte[] payload, string signal, bool isJson, OtlpBatch batch)
    {
        switch (signal)
        {
            case "traces": if (isJson) OtlpJsonDecoder.DecodeTraces(payload, batch); else OtlpDecoder.DecodeTraces(payload, batch); break;
            case "metrics": if (isJson) OtlpJsonDecoder.DecodeMetrics(payload, batch); else OtlpDecoder.DecodeMetrics(payload, batch); break;
            case "logs": if (isJson) OtlpJsonDecoder.DecodeLogs(payload, batch); else OtlpDecoder.DecodeLogs(payload, batch); break;
            default: throw new InvalidOperationException(
                $"Fixture name must end in -traces, -metrics or -logs; got signal '{signal}'.");
        }
    }
}
