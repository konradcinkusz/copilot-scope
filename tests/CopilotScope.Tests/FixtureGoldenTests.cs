using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Real OTLP payloads captured from assistants people actually run, replayed through the
/// real decoder and session store. These are the "golden" fixtures that prove the pipeline
/// handles real data from each vendor.
///
/// Each fixture is committed to tests/fixtures/&lt;assistant&gt;/&lt;version&gt;/ as .pb
/// (protobuf) files. The test verifies that:
/// - The payload decodes without error
/// - Emitter classification matches the directory name
/// - Key signals are present and populated
/// - Round-trip through the session store preserves integrity
/// </summary>
public class FixtureGoldenTests
{
    /// <summary>
    /// Each assistant directory maps to an expected EmitterKind. The test discovers
    /// these automatically and verifies that fixtures in each directory classify
    /// with the correct emitter.
    /// </summary>
    private static readonly Dictionary<string, EmitterKind> DirectoryToEmitter = new()
    {
        ["vscode"] = EmitterKind.VSCode,
        ["cli"] = EmitterKind.CLI,
        ["claude-code"] = EmitterKind.ClaudeCode,
        ["cowork"] = EmitterKind.Cowork,
        ["cursor"] = EmitterKind.Cursor,
    };

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FixtureDecodesAndClassifiesCorrectly(string assistantDir, string fixturePath)
    {
        var expectedEmitter = DirectoryToEmitter[assistantDir];

        var session = IngestFixture(fixturePath);
        Assert.NotNull(session);

        Assert.Equal(expectedEmitter, session.EmitterKind);
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FixtureRoundTripsCorrectly(string assistantDir, string fixturePath)
    {
        var session = IngestFixture(fixturePath);
        Assert.NotNull(session);

        var persisted = PersistedSession.From(session);
        var restored = persisted.ToSession();

        Assert.NotNull(restored);
        Assert.Equal(session.Id, restored.Id);
        Assert.Equal(session.EmitterKind, restored.EmitterKind);
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FixtureContainsExpectedSignals(string assistantDir, string fixturePath)
    {
        var session = IngestFixture(fixturePath);
        Assert.NotNull(session);

        var hasSignal = session.ChatCalls > 0
            || session.ToolCalls > 0
            || session.InputTokens > 0
            || session.OutputTokens > 0
            || session.EditsAccepted > 0
            || session.EditsRejected > 0
            || session.TtftMs.Count > 0
            || session.Turns > 0;

        Assert.True(hasSignal, $"Fixture {fixturePath} is empty (no signals populated)");
    }

    public static IEnumerable<object[]> FixtureFiles()
    {
        var fixtureRoot = Path.Combine(
            Path.GetDirectoryName(typeof(FixtureGoldenTests).Assembly.Location)!,
            "..", "..", "fixtures");

        if (!Directory.Exists(fixtureRoot))
        {
            yield break;
        }

        foreach (var assistantDir in Directory.GetDirectories(fixtureRoot))
        {
            var dirName = Path.GetFileName(assistantDir);

            if (!DirectoryToEmitter.ContainsKey(dirName))
                continue;

            foreach (var versionDir in Directory.GetDirectories(assistantDir))
            {
                foreach (var file in Directory.GetFiles(versionDir, "*.pb"))
                {
                    yield return [dirName, file];
                }
            }
        }
    }

    private static CopilotSession IngestFixture(string filePath)
    {
        var data = File.ReadAllBytes(filePath);

        var batch = new OtlpBatch();
        try
        {
            OtlpDecoder.DecodeTraces(data, batch);
        }
        catch
        {
            // Not traces, try metrics.
        }

        try
        {
            OtlpDecoder.DecodeMetrics(data, batch);
        }
        catch
        {
            // Not metrics, try logs.
        }

        try
        {
            OtlpDecoder.DecodeLogs(data, batch);
        }
        catch
        {
            // Not logs either.
        }

        if (batch.Spans.Count == 0 && batch.Metrics.Count == 0 && batch.Logs.Count == 0)
        {
            throw new InvalidOperationException($"Fixture {filePath} decoded to empty batch");
        }

        var store = new SessionStore();
        var touched = store.Ingest(batch);

        if (touched.Count == 0)
        {
            throw new InvalidOperationException($"Fixture {filePath} produced no sessions on ingest");
        }

        var sessionId = touched.First();
        var session = store.Get(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found after ingest");

        return session;
    }
}
