using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotScope.Collector;
using CopilotScope.Collector.Persistence;
using CopilotScope.Local.Scanning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The native binary's scanner (ADR-004, decision 4): Claude Code's transcripts, read from disk
/// and handed to a real collector over its import endpoint, the way <c>copilotscope</c> runs it.
/// </summary>
public sealed class ScannerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = TempDirectory.Create();        // stands in for ~/.claude/projects
    private readonly string _contentRoot = TempDirectory.Create();
    private readonly Clock _clock = new(Now);
    private WebApplication _collector = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _collector = await CollectorApp.BuildAsync(new WebApplicationOptions
        {
            ApplicationName = "CopilotScope.Collector",
            ContentRootPath = _contentRoot,
            EnvironmentName = "Production",
            Args = []
        }, b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotScope:Storage:Mode"] = "memory",
                ["ConnectionStrings:copilotdb"] = ""
            });
        });
        await _collector.StartAsync();
        _http = _collector.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _collector.StopAsync();
        await _collector.DisposeAsync();
        TempDirectory.Delete(_root);
        TempDirectory.Delete(_contentRoot);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A transcript of <paramref name="turns"/> prompts, each answered by one model call,
    /// last written at <paramref name="lastWrite"/>.</summary>
    private string Transcript(string sessionId, int turns, DateTimeOffset lastWrite, string? file = null,
        bool sidechain = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "-home-dev-acme")).FullName;
        var path = Path.Combine(directory, file ?? $"{sessionId}.jsonl");
        var start = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var lines = new List<string>();
        for (var i = 0; i < turns; i++)
        {
            var prefix = sidechain ? "side-" : "";
            lines.Add(Line("user", sessionId, $"{prefix}u{i}", start.AddMinutes(i), sidechain,
                new JsonObject { ["role"] = "user", ["content"] = $"request {i}" }));
            lines.Add(Line("assistant", sessionId, $"{prefix}a{i}", start.AddMinutes(i).AddSeconds(5), sidechain, new JsonObject
            {
                ["id"] = $"msg_{prefix}{i}",
                ["model"] = "claude-sonnet-5",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "done" }),
                ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 10 }
            }));
        }
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    private static string Line(string type, string sessionId, string uuid, DateTimeOffset at, bool sidechain,
        JsonObject message) => new JsonObject
    {
        ["type"] = type,
        ["sessionId"] = sessionId,
        ["uuid"] = uuid,
        ["isSidechain"] = sidechain,
        ["cwd"] = "/nonexistent/acme",
        ["timestamp"] = at.ToString("O"),
        ["message"] = message
    }.ToJsonString();

    private Scanner NewScanner(ScanState? state = null, HttpClient? http = null, ScannerOptions? options = null,
        IScanSource? source = null) =>
        new([source ?? new ClaudeCodeSource([_root])], state ?? ScanState.InMemory(), http ?? _http, options, _clock);

    private async Task<JsonElement?> SummaryAsync(string id)
    {
        using var response = await _http.GetAsync($"/api/sessions/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("summary");
    }

    private static SourceReport Only(ScanReport report) => Assert.Single(report.Sources);

    // ------------------------------------------------------------------ the rules

    [Fact]
    public async Task AQuietTranscriptIsImportedAndThenLeftAlone()
    {
        Transcript("s-quiet", turns: 2, lastWrite: Now.AddHours(-1));
        var scanner = NewScanner();

        var first = Only(await scanner.ScanNowAsync());
        Assert.Equal((1, 1), (first.Found, first.Imported));
        var summary = await SummaryAsync("s-quiet");
        Assert.NotNull(summary);
        Assert.Equal("log-import", summary.Value.GetProperty("origin").GetString());
        Assert.Equal(2, summary.Value.GetProperty("chatCalls").GetInt32());

        var second = Only(await scanner.ScanNowAsync());
        Assert.Equal((1, 0, 0), (second.Unchanged, second.Imported, second.Updated));
    }

    [Fact]
    public async Task ASessionStillBeingWrittenWaitsUntilItHasBeenQuiet()
    {
        // Telemetry has to win the race for a live session, or the same calls count twice.
        Transcript("s-active", turns: 1, lastWrite: Now.AddMinutes(-2));
        var scanner = NewScanner();

        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Waiting);
        Assert.Null(await SummaryAsync("s-active"));

        // Written two minutes before Now: quiet for ten minutes at Now + 8.
        _clock.Now = Now.AddMinutes(7);
        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Waiting);

        _clock.Now = Now.AddMinutes(8);
        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Imported);
        Assert.NotNull(await SummaryAsync("s-active"));
    }

    [Fact]
    public async Task ATranscriptThatGrewIsReadAgainAndReplacesItsSession()
    {
        Transcript("s-grows", turns: 1, lastWrite: Now.AddHours(-2));
        var scanner = NewScanner();
        await scanner.ScanNowAsync();

        Transcript("s-grows", turns: 3, lastWrite: Now.AddHours(-1));
        var report = Only(await scanner.ScanNowAsync());

        Assert.Equal(1, report.Updated);
        // Replaced, not added to: three calls, not four.
        Assert.Equal(3, (await SummaryAsync("s-grows"))!.Value.GetProperty("chatCalls").GetInt32());
    }

    [Fact]
    public async Task ASessionTelemetryAlreadyHasIsLeftToTelemetry()
    {
        var span = new StringContent("""
            {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"claude-code"}}]},
            "scopeSpans":[{"spans":[{"traceId":"0102030405060708090a0b0c0d0e0f10","spanId":"0102030405060708","name":"chat",
            "startTimeUnixNano":"1790000000000000000","endTimeUnixNano":"1790000001000000000","attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
            {"key":"gen_ai.conversation.id","value":{"stringValue":"s-live"}},
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"42"}}]}]}]}]}
            """, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync("/v1/traces", span)).StatusCode);
        Transcript("s-live", turns: 2, lastWrite: Now.AddHours(-1));
        var scanner = NewScanner();

        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Live);
        Assert.Equal("otel", (await SummaryAsync("s-live"))!.Value.GetProperty("origin").GetString());
        // Refused is an answer, not a failure: it is not asked again until the file changes.
        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Unchanged);
    }

    [Fact]
    public async Task ADeletedTranscriptNeverDeletesItsSession()
    {
        // Claude Code clears its own transcripts after a month; the history must outlive them.
        var path = Transcript("s-gone", turns: 1, lastWrite: Now.AddHours(-1));
        var scanner = NewScanner();
        await scanner.ScanNowAsync();

        File.Delete(path);
        Assert.Equal(0, Only(await scanner.ScanNowAsync()).Found);
        Assert.NotNull(await SummaryAsync("s-gone"));
    }

    [Fact]
    public async Task ASubagentsFileIsReadAsPartOfItsSession()
    {
        Transcript("s-parent", turns: 1, lastWrite: Now.AddHours(-1));
        Transcript("s-parent", turns: 1, lastWrite: Now.AddHours(-1), file: "agent-1.jsonl", sidechain: true);

        var report = Only(await NewScanner().ScanNowAsync());

        Assert.Equal((1, 1), (report.Found, report.Imported));
        var summary = (await SummaryAsync("s-parent"))!.Value;
        Assert.Equal(2, summary.GetProperty("chatCalls").GetInt32()); // the subagent's call counts
        Assert.Equal(1, summary.GetProperty("turns").GetInt32());     // its prompt is not a turn
    }

    [Fact]
    public async Task NothingIsLostWhileTheCollectorCannotBeReached()
    {
        Transcript("s-later", turns: 1, lastWrite: Now.AddHours(-1));
        var state = ScanState.InMemory();

        using var unreachable = new HttpClient(new Refusing()) { BaseAddress = new Uri("http://localhost") };
        var failed = Only(await NewScanner(state, unreachable).ScanNowAsync());
        Assert.Equal((1, 0), (failed.Failed, failed.Imported));

        Assert.Equal(1, Only(await NewScanner(state).ScanNowAsync()).Imported);
    }

    [Fact]
    public async Task ALongHistoryIsHandedOverInBatches()
    {
        for (var i = 0; i < 5; i++) Transcript($"s-batch-{i}", turns: 1, lastWrite: Now.AddHours(-1 - i));
        var counting = new Counting(_collector.GetTestServer().CreateHandler());
        using var http = new HttpClient(counting) { BaseAddress = new Uri("http://localhost") };

        var report = Only(await NewScanner(http: http, options: new ScannerOptions { BatchSessions = 2 }).ScanNowAsync());

        Assert.Equal(5, report.Imported);
        Assert.Equal(3, counting.Posts);
    }

    [Fact]
    public async Task OneSessionTheCollectorRefusesDoesNotHoldBackTheRest()
    {
        Transcript("s-fine-1", turns: 1, lastWrite: Now.AddHours(-1));
        Transcript("s-poison", turns: 1, lastWrite: Now.AddHours(-2));
        Transcript("s-fine-2", turns: 1, lastWrite: Now.AddHours(-3));
        using var http = new HttpClient(new RefusingOne("s-poison", _collector.GetTestServer().CreateHandler()))
        {
            BaseAddress = new Uri("http://localhost")
        };
        var scanner = NewScanner(http: http);

        var report = Only(await scanner.ScanNowAsync());

        Assert.Equal((2, 1), (report.Imported, report.Failed));
        Assert.Null(await SummaryAsync("s-poison"));
        // Not retried every minute: only a change to its files asks again.
        Assert.Equal(3, Only(await scanner.ScanNowAsync()).Unchanged);
    }

    [Fact]
    public async Task ASessionTheReaderChokesOnDoesNotStopTheOthers()
    {
        // Newest first: a pass that gave up on the newest session would never reach the rest.
        Transcript("s-choke", turns: 1, lastWrite: Now.AddHours(-1));
        Transcript("s-older", turns: 1, lastWrite: Now.AddHours(-2));
        var scanner = NewScanner(source: new Choking(new ClaudeCodeSource([_root]), "s-choke"));

        var report = Only(await scanner.ScanNowAsync());

        Assert.Equal((1, 1), (report.Imported, report.Failed));
        Assert.NotNull(await SummaryAsync("s-older"));
        // Not recorded: it is tried again, in case what broke was passing.
        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Failed);
    }

    [Fact]
    public async Task ANewReaderReadsEverySessionAgain()
    {
        // A parser fix has to reach the history imported before it.
        Transcript("s-reread", turns: 1, lastWrite: Now.AddHours(-1));
        var state = ScanState.InMemory();
        var source = new ClaudeCodeSource([_root]);
        await NewScanner(state, source: source).ScanNowAsync();

        var newer = NewScanner(state, source: new Versioned(source, source.Version + 1));

        Assert.Equal(1, Only(await newer.ScanNowAsync()).Updated);
        // Once, not on every pass from then on.
        Assert.Equal(1, Only(await newer.ScanNowAsync()).Unchanged);
    }

    [Fact]
    public async Task EmptyTranscriptsAreRecordedAndNotReadAgain()
    {
        // Only a subagent's lines: nothing the developer did, so nothing to score.
        Transcript("s-empty", turns: 1, lastWrite: Now.AddHours(-1), sidechain: true);
        var scanner = NewScanner();

        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Empty);
        Assert.Equal(1, Only(await scanner.ScanNowAsync()).Unchanged);
        Assert.Null(await SummaryAsync("s-empty"));
    }

    // ------------------------------------------------------------------ the state

    [Fact]
    public void TheScanStateSurvivesARestartAndIsTheOwnersAlone()
    {
        var path = Path.Combine(_contentRoot, ScanState.FileName);
        var state = ScanState.Load(path);
        state.Record("claude-code", 1, "s1", "f1");
        state.Save();

        var reloaded = ScanState.Load(path);
        Assert.True(reloaded.IsCurrent("claude-code", 1, "s1", "f1"));
        Assert.False(reloaded.IsCurrent("claude-code", 1, "s1", "f2"));
        Assert.False(reloaded.IsCurrent("claude-code", 2, "s1", "f1"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void AnUnreadableScanStateStartsOverInsteadOfFailing()
    {
        var path = Path.Combine(_contentRoot, ScanState.FileName);
        File.WriteAllText(path, "{ not json");
        Assert.False(ScanState.Load(path).IsCurrent("claude-code", 1, "s1", "f1"));

        File.WriteAllText(path, """{"schema":99,"sources":{}}""");
        Assert.False(ScanState.Load(path).IsCurrent("claude-code", 1, "s1", "f1"));
    }

    [Fact]
    public void ForgettingASessionWhoseFilesAreGoneKeepsTheRest()
    {
        var state = ScanState.InMemory();
        state.Record("claude-code", 1, "kept", "f1");
        state.Record("claude-code", 1, "gone", "f2");

        state.Retain("claude-code", new HashSet<string> { "kept" });

        Assert.True(state.IsCurrent("claude-code", 1, "kept", "f1"));
        Assert.False(state.IsCurrent("claude-code", 1, "gone", "f2"));
    }

    [Fact]
    public void AFingerprintChangesWithAnyFileOfTheSession()
    {
        var at = Now.AddHours(-1);
        var one = new ScanUnit("s", [new ScanFile("/a.jsonl", 10, at)]);
        Assert.Equal(one.Fingerprint, new ScanUnit("s", [new ScanFile("/a.jsonl", 10, at)]).Fingerprint);
        Assert.NotEqual(one.Fingerprint, new ScanUnit("s", [new ScanFile("/a.jsonl", 11, at)]).Fingerprint);
        Assert.NotEqual(one.Fingerprint, new ScanUnit("s", [new ScanFile("/a.jsonl", 10, at.AddSeconds(1))]).Fingerprint);
        Assert.NotEqual(one.Fingerprint,
            new ScanUnit("s", [new ScanFile("/a.jsonl", 10, at), new ScanFile("/b.jsonl", 1, at)]).Fingerprint);
    }

    // ------------------------------------------------------------------ test doubles

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }

    private sealed class Counting(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posts++;
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>Answers 413 to any import holding the named session, as a collector would to a
    /// session too large for it.</summary>
    private sealed class RefusingOne(string sessionId, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return body.Contains($"\"{sessionId}\"", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
                : await base.SendAsync(request, ct);
        }
    }

    private sealed class Choking(IScanSource inner, string sessionId) : IScanSource
    {
        public string Name => inner.Name;
        public string DisplayName => inner.DisplayName;
        public int Version => inner.Version;
        public IReadOnlyList<string> Roots => inner.Roots;
        public Discovery Discover(CancellationToken ct) => inner.Discover(ct);
        public PersistedSession? Read(ScanUnit unit) =>
            unit.SessionId == sessionId ? throw new InvalidOperationException("unexpected layout") : inner.Read(unit);
    }

    private sealed class Versioned(IScanSource inner, int version) : IScanSource
    {
        public string Name => inner.Name;
        public string DisplayName => inner.DisplayName;
        public int Version => version;
        public IReadOnlyList<string> Roots => inner.Roots;
        public Discovery Discover(CancellationToken ct) => inner.Discover(ct);
        public PersistedSession? Read(ScanUnit unit) => inner.Read(unit);
    }
}
