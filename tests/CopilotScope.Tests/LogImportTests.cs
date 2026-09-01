using System.Net;
using System.Net.Http.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using CopilotScope.LogImporter;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Importing Claude Code's own transcript files — the path that scores a developer's existing
/// history with no OTel configuration at all.
///
/// <para>Two things are load-bearing and both are easy to get wrong quietly. First, the counts
/// have to be right: Claude Code splits one model response across several assistant lines and
/// transports tool results as user messages, so a naive parser doubles the call count and
/// invents turns. Second, the confidence has to be <i>lower</i>: an imported session genuinely
/// has no latency or edit-decision signal, and one that scored like a live session on a
/// quarter of the evidence would be the feature lying.</para>
/// </summary>
public sealed class LogImportTests
{
    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // Deliberately not under tests/fixtures/: that tree is the captured-OTLP corpus
            // from #92 and its directory names are validated against the emitter list.
            var candidate = Path.Combine(dir.FullName, "tests", "transcripts", "claude-code",
                "sample-session.jsonl");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Claude Code transcript fixture not found.");
    }

    private static TranscriptSession Parsed(bool includeContent = false) =>
        ClaudeCodeTranscript.Parse(File.ReadLines(FixturePath()), "acme/api", includeContent)!;

    // ------------------------------------------------------------------- parsing

    [Fact]
    public void ATranscriptBecomesAScoredSession()
    {
        var session = Parsed().Session;

        Assert.Equal("11111111-2222-3333-4444-555555555555", session.Id);
        Assert.Equal(EmitterKind.ClaudeCode, session.EmitterKind);
        Assert.Equal(SessionOrigin.LogImport, session.Origin);
        Assert.Equal("acme/api", session.Repository);
        Assert.Equal("feature/retry", session.Branch);

        var report = new QualityEngine().Evaluate(session);
        Assert.InRange(report.Score, 0, 100);
        Assert.NotEmpty(report.Components);
    }

    [Fact]
    public void OneResponseSplitAcrossBlocksIsOneModelCall()
    {
        // The fixture's first assistant message carries a text block AND a tool_use block with
        // a single `usage`. Counting blocks rather than usage would double every call count
        // and halve every tokens-per-call figure in the product.
        var session = Parsed().Session;

        Assert.Equal(3, session.ChatCalls);
        Assert.Equal(4800, session.InputTokens);
        Assert.Equal(620, session.OutputTokens);
        Assert.Equal(47800, session.CacheReadTokens);
        Assert.Equal(800, session.CacheCreationTokens);
    }

    [Fact]
    public void ToolResultsArriveAsUserMessagesAndDoNotStartTurns()
    {
        // Tool outcomes are transported as user messages. Treating them as prompts would report
        // this two-prompt session as five turns — and turn count is an input to the analysis.
        var session = Parsed().Session;

        Assert.Equal(2, session.Turns);
        Assert.Equal(2, session.TurnList.Count);
    }

    [Fact]
    public void ToolCallsAreCountedWithTheirOutcomeAndRealDuration()
    {
        var session = Parsed().Session;

        Assert.Equal(2, session.ToolCalls);
        Assert.Equal(1, session.ToolErrors);
        Assert.True(session.Tools.ContainsKey("Read"));
        Assert.True(session.Tools.ContainsKey("Edit"));

        // 10:00:04 → 10:00:06.5 is in the file. The duration is measured, not invented — which
        // is why importing does not have to leave the tool panel showing "0 ms" everywhere.
        Assert.Equal(2500, session.Tools["Read"].TotalMs, 0);
    }

    [Fact]
    public void ATruncatedFinalLineDoesNotFailTheImport()
    {
        // The last line of a session Claude Code is still writing to is routinely half-written,
        // and that is the session someone most wants to look at.
        var parsed = Parsed();

        Assert.Equal(1, parsed.Skipped);
        Assert.Equal(3, parsed.Session.ChatCalls);
    }

    [Fact]
    public void SummaryAndSystemLinesCarryNoMeasurableWork()
    {
        var session = Parsed().Session;
        // The fixture holds one summary and one system line; neither is a turn or a call.
        Assert.Equal(2, session.Turns);
        Assert.Equal(3, session.ChatCalls);
    }

    [Fact]
    public void PromptTextIsNotImportedUnlessAsked()
    {
        Assert.Empty(Parsed().Session.Transcript);

        var withContent = Parsed(includeContent: true).Session;
        Assert.NotEmpty(withContent.Transcript);
        Assert.Contains(withContent.Transcript,
            t => t.Prompt is not null && t.Prompt.Contains("retry policy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModelsAreAttributedPerCall()
    {
        var session = Parsed().Session;

        Assert.Equal(2, session.ModelCalls["claude-sonnet-4-5"]);
        Assert.Equal(1, session.ModelCalls["claude-opus-4-6"]);
    }

    [Fact]
    public void AnEmptyOrUnparseableFileYieldsNoSession()
    {
        Assert.Null(ClaudeCodeTranscript.Parse([]));
        Assert.Null(ClaudeCodeTranscript.Parse(["not json at all", "{\"type\":\"summary\"}"]));
    }

    // -------------------------------------------------------------- honest confidence

    [Fact]
    public void AnImportedSessionCarriesNoFabricatedLatencyOrEditSignal()
    {
        // Time-to-first-token, edit decisions and thumbs are OTel events. No amount of parsing
        // invents them, and defaulting them to zero would be worse than leaving them absent:
        // zero accepted edits reads as "everything was rejected".
        var session = Parsed().Session;

        Assert.Empty(session.TtftMs);
        Assert.Equal(0, session.EditsAccepted);
        Assert.Equal(0, session.EditsRejected);
        Assert.Equal(0, session.ThumbsUp);
        Assert.Equal(0, session.ThumbsDown);

        // Prompt→response wall clock IS in the file, and is recorded — it is a real duration,
        // just not the same measurement as TTFT.
        Assert.NotEmpty(session.ChatDurationMs);
    }

    [Fact]
    public void ImportedComponentsWithoutDataReportThemselvesAsPriors()
    {
        var report = new QualityEngine().Evaluate(Parsed().Session);

        foreach (var name in new[] { "latency", "acceptance", "feedback" })
        {
            var component = report.Components.Single(c => c.Name == name);
            Assert.Equal(0, component.Samples);
        }
    }

    [Fact]
    public void ImportingScoresOnLessEvidenceThanLiveTelemetryDoes()
    {
        // The same work, once as an import and once as a live session that also reported
        // latency, edit decisions and feedback. The live one must be the more confident
        // measurement, or the import is quietly claiming evidence it does not have.
        var imported = Parsed().Session;

        var live = Parsed().Session;
        live.Origin = SessionOrigin.Otel;
        live.TtftMs.AddRange([600, 700, 800]);
        live.EditsAccepted = 4;
        live.EditsRejected = 1;
        live.ThumbsUp = 2;

        var engine = new QualityEngine();
        Assert.True(engine.Evaluate(live).Confidence > engine.Evaluate(imported).Confidence,
            "a live session must be measured with more confidence than an import of the same work");
    }

    [Fact]
    public void OriginSurvivesTheSnapshotRoundTripAndReachesTheDto()
    {
        var session = Parsed().Session;

        Assert.Equal(SessionOrigin.LogImport, PersistedSession.From(session).ToSession().Origin);
        Assert.Equal(SessionOrigin.LogImport, Dto.Summary(session, new QualityEngine()).Origin);
    }

    // ------------------------------------------------------------------ the endpoint

    private static WebApplicationFactory<SessionSummaryDto> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    private static ImportRequest Request(params CopilotSession[] sessions) =>
        new([.. sessions.Select(PersistedSession.From)]);

    [Fact]
    public async Task ImportingIsIdempotent()
    {
        // It is meant to run on a schedule or a file watcher. Re-importing the same transcript
        // must replace the session, not add a second copy or double its tokens.
        using var factory = Factory();
        using var client = factory.CreateClient();

        var first = await (await client.PostAsJsonAsync("/api/import", Request(Parsed().Session)))
            .Content.ReadFromJsonAsync<ImportResult>();
        var second = await (await client.PostAsJsonAsync("/api/import", Request(Parsed().Session)))
            .Content.ReadFromJsonAsync<ImportResult>();

        Assert.Equal(1, first!.Imported);
        Assert.Equal(0, second!.Imported);
        Assert.Equal(1, second.Updated);

        var page = await client.GetFromJsonAsync<SessionPage>("/api/sessions?limit=50");
        var rows = page!.Sessions.Where(s => s.Id == Parsed().Session.Id).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(4800, row.InputTokens);
    }

    [Fact]
    public async Task ImportRefusesToOverwriteLiveTelemetry()
    {
        // An import of a session the collector already has from OTLP would REMOVE signal —
        // latency, edit decisions — and lower its score, silently. Refuse and say why.
        using var factory = Factory();
        using var client = factory.CreateClient();

        var live = Parsed().Session;
        live.Origin = SessionOrigin.Otel;
        var seeded = new SeedRequest(false, [PersistedSession.From(Rename(live, "seed-live-1"))]);
        Assert.True((await client.PostAsJsonAsync("/api/admin/seed", seeded)).IsSuccessStatusCode);

        var reimport = Rename(Parsed().Session, "seed-live-1");
        var result = await (await client.PostAsJsonAsync("/api/import", Request(reimport)))
            .Content.ReadFromJsonAsync<ImportResult>();

        Assert.Equal(0, result!.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(result.Rejected, r => r.Contains("live telemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASnapshotClaimingToBeLiveTelemetryIsRejected()
    {
        // Otherwise this endpoint is a way to forge OTLP data with an admin key that was only
        // ever meant to import files.
        using var factory = Factory();
        using var client = factory.CreateClient();

        var forged = Parsed().Session;
        forged.Origin = SessionOrigin.Otel;

        var result = await (await client.PostAsJsonAsync("/api/import", Request(forged)))
            .Content.ReadFromJsonAsync<ImportResult>();

        Assert.Equal(0, result!.Imported);
        Assert.Contains(result.Rejected, r => r.Contains("origin must be", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportIsAdminOnly()
    {
        using var factory = Factory(
            ("CopilotScope:Keys:Read:0", "read-key"),
            ("CopilotScope:Keys:Admin:0", "admin-key"));
        using var client = factory.CreateClient();

        var read = new HttpRequestMessage(HttpMethod.Post, "/api/import")
        { Content = JsonContent.Create(Request(Parsed().Session)) };
        read.Headers.Add("x-api-key", "read-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(read)).StatusCode);

        var admin = new HttpRequestMessage(HttpMethod.Post, "/api/import")
        { Content = JsonContent.Create(Request(Parsed().Session)) };
        admin.Headers.Add("x-api-key", "admin-key");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(admin)).StatusCode);
    }

    private static CopilotSession Rename(CopilotSession source, string id)
    {
        var snapshot = PersistedSession.From(source) with { Id = id };
        return snapshot.ToSession();
    }

    // --------------------------------------------------------------------- the CLI

    [Fact]
    public void TheCliDefaultsToClaudeCodesOwnTranscriptDirectory()
    {
        var options = ImportCommand.Parse([]);

        Assert.NotNull(options);
        Assert.Contains(Path.Combine(".claude", "projects"), options!.Root, StringComparison.Ordinal);
        Assert.False(options.IncludeContent, "content import must be opt-in");
        Assert.Null(options.Error);
    }

    [Fact]
    public void TheCliRejectsAnUnusableCollectorUrlAndAnUnparseableDate()
    {
        Assert.NotNull(ImportCommand.Parse(["--collector", "not a url"])!.Error);
        Assert.NotNull(ImportCommand.Parse(["--since", "last tuesday"])!.Error);
        Assert.Null(ImportCommand.Parse(["--since", "2026-08-01"])!.Error);
    }

    [Fact]
    public void TheWorkingDirectoryIsReadFromTheTranscriptNotFromTheDirectoryName()
    {
        // Claude Code's directory-name encoding is lossy — a project path containing a dash
        // cannot be recovered from it — so the cwd is read out of the file itself.
        Assert.Equal("/home/dev/acme-api", ImportCommand.WorkingDirectoryOf(FixturePath()));
    }

    [Fact]
    public void ARepositoryWithNoGitRemoteGetsNoLabelRatherThanAnInventedOne()
    {
        // Falling back to the directory name would invent a second cohort for a repository the
        // collector already knows by its normalized remote. A missing label is honest; a
        // duplicate cohort is a wrong number.
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var temp = Directory.CreateTempSubdirectory("copilotscope-import-test");
        try { Assert.Null(ImportCommand.RepositoryFor(temp.FullName, cache)); }
        finally { temp.Delete(recursive: true); }
    }
}
