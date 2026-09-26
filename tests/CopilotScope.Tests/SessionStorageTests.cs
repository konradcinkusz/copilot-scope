using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>A fresh, empty session store of one kind, removed again on dispose.</summary>
internal sealed class StoreUnderTest : IAsyncDisposable
{
    /// <summary>Connection string of a Postgres server the tests may create databases on.
    /// Unset, the Postgres half of the contract suite is left out rather than failed: the
    /// suite needs no Docker and no live database, like the rest of this project.</summary>
    public static string? PostgresServer => Environment.GetEnvironmentVariable("COPILOTSCOPE_TEST_PG");

    private readonly Func<ValueTask> _cleanup;

    private StoreUnderTest(ISessionRepository store, Func<ValueTask> cleanup)
    {
        Store = store;
        _cleanup = cleanup;
    }

    public ISessionRepository Store { get; }

    public static async Task<StoreUnderTest> CreateAsync(string kind)
    {
        if (kind == "files")
        {
            var directory = TempDirectory.Create();
            var files = new FileSessionRepository(directory);
            await files.EnsureSchemaAsync(CancellationToken.None);
            return new StoreUnderTest(files, async () =>
            {
                await files.DisposeAsync();
                TempDirectory.Delete(directory);
            });
        }

        var database = $"copilotscope_test_{Guid.NewGuid():N}";
        await ExecuteAsync(PostgresServer!, $"CREATE DATABASE {database}");
        var connection = new NpgsqlConnectionStringBuilder(PostgresServer) { Database = database }.ConnectionString;
        var postgres = new PostgresSessionRepository(connection);
        await postgres.EnsureSchemaAsync(CancellationToken.None);
        return new StoreUnderTest(postgres, async () =>
        {
            await postgres.DisposeAsync();
            await ExecuteAsync(PostgresServer!, $"DROP DATABASE {database} WITH (FORCE)");
        });
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _cleanup();
}

internal static class TempDirectory
{
    public static string Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "copilotscope-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Retried for a moment. A collector built by <c>WebApplicationFactory</c> is disposed twice
    /// at once — by the factory, and by the application's own <c>RunAsync</c> on its thread —
    /// and the second disposal can still be writing the file store's index when the first has
    /// returned to the test. That is the test harness racing itself, not the store losing
    /// anything; a cleanup that failed the test over it would make every file-storage test flaky.
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}

internal static class StoredSessions
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A snapshot on the timeline at <paramref name="lastSeen"/>. Whole seconds only:
    /// Postgres keeps microseconds, and a contract test must not fail on a rounding the
    /// stores are allowed to differ in.</summary>
    public static PersistedSession Snapshot(string id, DateTimeOffset lastSeen, string? repository = null,
        EmitterKind emitter = EmitterKind.VSCode, string model = "gpt-5", int chatCalls = 1)
    {
        var s = new CopilotSession
        {
            Id = id, Repository = repository, EmitterKind = emitter,
            FirstSeen = lastSeen.AddMinutes(-5), LastSeen = lastSeen,
            ChatCalls = chatCalls, InputTokens = 100L * chatCalls
        };
        if (chatCalls > 0) s.ModelCalls[model] = chatCalls;
        return PersistedSession.From(s);
    }

    public static Task PutAsync(this ISessionRepository store, PersistedSession snapshot, double score = 0.8,
        string grade = "B", SessionKind kind = SessionKind.UserChat) =>
        store.UpsertAsync(snapshot, score, grade, CancellationToken.None, kind.ToString());

    public static string Json(PersistedSession snapshot) =>
        JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>
/// The contract every session store owes the read path (<see cref="ISessionRepository"/>), run
/// against each implementation. The two stores are only interchangeable if the same data gives
/// the same pages, counts and baselines in both, so every case here runs against the file store
/// always and against Postgres whenever COPILOTSCOPE_TEST_PG names a server.
/// </summary>
public sealed class SessionRepositoryContractTests
{
    private static readonly DateTimeOffset T0 = StoredSessions.T0;
    private static readonly CancellationToken None = CancellationToken.None;

    public static TheoryData<string> Stores()
    {
        var stores = new TheoryData<string> { "files" };
        if (!string.IsNullOrEmpty(StoreUnderTest.PostgresServer)) stores.Add("postgres");
        return stores;
    }

    private static List<string> Ids(IEnumerable<PersistedSession> sessions) => sessions.Select(s => s.Id).ToList();

    [Theory, MemberData(nameof(Stores))]
    public async Task ASnapshotComesBackAsItWentIn(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        var session = new CopilotSession
        {
            Id = "rt", AgentName = "copilot", Repository = "github.com/acme/api", Branch = "main",
            EmitterKind = EmitterKind.ClaudeCode, Origin = SessionOrigin.LogImport, SubjectId = "subject-1",
            FirstSeen = T0, LastSeen = T0.AddMinutes(3),
            ChatCalls = 7, ChatErrors = 1, ToolCalls = 12, InputTokens = 5000, EditsAutoAccepted = 2
        };
        session.TtftMs.AddRange([300, 500.5, 900]);
        session.ModelCalls["claude-sonnet-5"] = 7;
        session.Tools["Read"] = (3, 1, 210.5);
        session.ErrorTypes["tool_error"] = 1;
        session.AddEvent(new SessionEvent(T0.AddMinutes(1), "chat", "chat claude-sonnet-5"));
        session.TurnList.Add(new TurnStat { TraceId = "t0", Index = 0, Start = T0, End = T0.AddSeconds(4), ChatCalls = 2 });
        session.AddAgentName("copilot");
        session.AddAgentName("explore");
        var snapshot = PersistedSession.From(session);

        await t.Store.PutAsync(snapshot);

        var stored = await t.Store.GetAsync("rt", None);
        Assert.NotNull(stored);
        Assert.Equal(StoredSessions.Json(snapshot), StoredSessions.Json(stored));
        Assert.Equal(["copilot", "explore"], stored!.AgentNames!);
        Assert.Null(await t.Store.GetAsync("missing", None));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task WritingASessionAgainReplacesIt(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("s", T0, chatCalls: 1));
        await t.Store.PutAsync(StoredSessions.Snapshot("s", T0.AddMinutes(1), chatCalls: 4));

        Assert.Equal(1, await t.Store.CountAsync(null, null, None));
        Assert.Equal(4, (await t.Store.GetAsync("s", None))!.ChatCalls);
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task PagesAreNewestFirstAndCountTheWholeWindow(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        for (var i = 0; i < 5; i++) await t.Store.PutAsync(StoredSessions.Snapshot($"s{i}", T0.AddMinutes(i)));

        Assert.Equal(["s3", "s2"], Ids(await t.Store.QueryAsync(null, null, limit: 2, offset: 1, None)));
        Assert.Equal(5, await t.Store.CountAsync(null, null, None));
        Assert.Empty(await t.Store.QueryAsync(null, null, limit: 2, offset: 5, None));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task TheWindowBoundsLastSeenInclusively(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        for (var i = 0; i < 5; i++) await t.Store.PutAsync(StoredSessions.Snapshot($"s{i}", T0.AddMinutes(i)));

        var since = T0.AddMinutes(1);
        var until = T0.AddMinutes(3);
        Assert.Equal(["s3", "s2", "s1"], Ids(await t.Store.QueryAsync(since, until, 10, 0, None)));
        Assert.Equal(3, await t.Store.CountAsync(since, until, None));
        Assert.Equal(["s4", "s3"], Ids(await t.Store.QueryAsync(T0.AddMinutes(3), null, 10, 0, None)));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task InternalHelperSessionsAreLeftOutUnlessAskedFor(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("chat", T0), kind: SessionKind.UserChat);
        await t.Store.PutAsync(StoredSessions.Snapshot("title", T0.AddMinutes(1)), kind: SessionKind.InternalTitleGeneration);
        await t.Store.PutAsync(StoredSessions.Snapshot("summary", T0.AddMinutes(2)), kind: SessionKind.InternalSummary);
        await t.Store.PutAsync(StoredSessions.Snapshot("orphan", T0.AddMinutes(3)), kind: SessionKind.Unattributed);

        // Unattributed is not internal: it is somebody's real telemetry without an identity.
        Assert.Equal(["orphan", "chat"], Ids(await t.Store.QueryAsync(null, null, 10, 0, None)));
        Assert.Equal(2, await t.Store.CountAsync(null, null, None));
        Assert.Equal(4, await t.Store.CountAsync(null, null, None, includeInternal: true));
        Assert.Equal(["orphan", "summary", "title", "chat"],
            Ids(await t.Store.QueryAsync(null, null, 10, 0, None, includeInternal: true)));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task TheCohortFilterNarrowsThePageAndItsCountAlike(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("a", T0, "Acme/API", EmitterKind.ClaudeCode, "sonnet-5"), grade: "B");
        await t.Store.PutAsync(StoredSessions.Snapshot("b", T0.AddMinutes(1), "acme/web", EmitterKind.VSCode, "gpt-5"), grade: "A");
        await t.Store.PutAsync(StoredSessions.Snapshot("c", T0.AddMinutes(2), null, EmitterKind.VSCode, "gpt-5"),
            grade: "C", kind: SessionKind.Unattributed);
        await t.Store.PutAsync(StoredSessions.Snapshot("d", T0.AddMinutes(3), "acme/api", EmitterKind.VSCode, "gpt-5"),
            grade: "A", kind: SessionKind.InternalTitleGeneration);

        async Task Expect(CohortFilter cohort, string[] expected, bool includeInternal = false)
        {
            Assert.Equal(expected, Ids(await t.Store.QueryAsync(null, null, 10, 0, None, includeInternal, cohort)));
            Assert.Equal(expected.Length, await t.Store.CountAsync(null, null, None, includeInternal, cohort));
        }

        await Expect(new CohortFilter(Repository: "acme/api"), ["a"]);                          // case-insensitive
        await Expect(new CohortFilter(Repository: "acme/api"), ["d", "a"], includeInternal: true);
        await Expect(new CohortFilter(Emitter: EmitterKind.ClaudeCode), ["a"]);
        await Expect(new CohortFilter(Kind: SessionKind.Unattributed), ["c"]);
        await Expect(new CohortFilter(Grade: "a"), ["b"]);                                       // case-insensitive
        await Expect(new CohortFilter(Model: "gpt-5"), ["c", "b"]);
        await Expect(new CohortFilter(Repository: "acme/web", Model: "sonnet-5"), []);
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task TheBaselineIsUserChatsThatRanACall(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("old", T0, chatCalls: 2), score: 0.9);
        await t.Store.PutAsync(StoredSessions.Snapshot("idle", T0.AddMinutes(1), chatCalls: 0), score: 0.1);
        await t.Store.PutAsync(StoredSessions.Snapshot("helper", T0.AddMinutes(2), chatCalls: 3), score: 0.2,
            kind: SessionKind.InternalSummary);
        await t.Store.PutAsync(StoredSessions.Snapshot("orphan", T0.AddMinutes(3)), score: 0.3,
            kind: SessionKind.Unattributed);
        await t.Store.PutAsync(StoredSessions.Snapshot("new", T0.AddMinutes(4)), score: 0.7);

        Assert.Equal([0.7, 0.9], await t.Store.ScoresAsync(null, 10, None));
        Assert.Equal([0.7], await t.Store.ScoresAsync(T0.AddMinutes(1), 10, None));
        Assert.Equal([0.7], await t.Store.ScoresAsync(null, 1, None));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task RetentionRemovesOnlySessionsLastSeenBeforeTheCutoff(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("expired", T0.AddHours(-1)));
        await t.Store.PutAsync(StoredSessions.Snapshot("boundary", T0));
        await t.Store.PutAsync(StoredSessions.Snapshot("fresh", T0.AddHours(1)));

        Assert.Equal(["expired"], await t.Store.IdsOlderThanAsync(T0, None));
        Assert.Equal(1, await t.Store.DeleteOlderThanAsync(T0, None));
        Assert.Equal(["fresh", "boundary"], Ids(await t.Store.QueryAsync(null, null, 10, 0, None)));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task ADeleteSaysWhetherTheSessionExisted(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("s", T0));

        Assert.Equal(1, await t.Store.DeleteAsync("s", None));
        Assert.Equal(0, await t.Store.DeleteAsync("s", None));
        Assert.Equal(0, await t.Store.DeleteAsync("never-existed", None));
        Assert.Null(await t.Store.GetAsync("s", None));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task APrefixDeleteStaysInsideItsNamespace(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("seed-1", T0));
        await t.Store.PutAsync(StoredSessions.Snapshot("seed-2", T0.AddMinutes(1)));
        await t.Store.PutAsync(StoredSessions.Snapshot("SEED-3", T0.AddMinutes(2)));
        await t.Store.PutAsync(StoredSessions.Snapshot("real", T0.AddMinutes(3)));

        Assert.Equal(2, await t.Store.DeleteByPrefixAsync("seed-", None));
        Assert.Equal(["real", "SEED-3"], Ids(await t.Store.QueryAsync(null, null, 10, 0, None)));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task StartupLoadsTheNewestSessionsOfEveryKind(string kind)
    {
        await using var t = await StoreUnderTest.CreateAsync(kind);
        await t.Store.PutAsync(StoredSessions.Snapshot("oldest", T0));
        await t.Store.PutAsync(StoredSessions.Snapshot("helper", T0.AddMinutes(1)), kind: SessionKind.InternalHelper);
        await t.Store.PutAsync(StoredSessions.Snapshot("newest", T0.AddMinutes(2)));

        Assert.Equal(["newest", "helper"], Ids(await t.Store.LoadAllAsync(2, None)));
    }
}

/// <summary>What only the file store has to get right: its files, its index, its lock.</summary>
public sealed class FileSessionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = StoredSessions.T0;
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly string _directory = TempDirectory.Create();

    public void Dispose() => TempDirectory.Delete(_directory);

    private string SessionsDirectory => Path.Combine(_directory, "sessions");

    private async Task<FileSessionRepository> OpenAsync()
    {
        var store = new FileSessionRepository(_directory);
        await store.EnsureSchemaAsync(None);
        return store;
    }

    [Fact]
    public async Task SessionsSurviveTheStoreBeingReopened()
    {
        await using (var store = await OpenAsync())
            for (var i = 0; i < 3; i++) await store.PutAsync(StoredSessions.Snapshot($"s{i}", T0.AddMinutes(i)), score: i / 10.0);

        await using var reopened = await OpenAsync();
        Assert.Equal(["s2", "s1", "s0"], (await reopened.QueryAsync(null, null, 10, 0, None)).Select(s => s.Id));
        Assert.Equal([0.2, 0.1, 0.0], await reopened.ScoresAsync(null, 10, None));
    }

    [Fact]
    public async Task IdsThatAreNotSafeFileNamesStillRoundTrip()
    {
        // Real ids carry separators (unattributed:proc:host:svc:pid), and two may differ only in
        // case — which a case-insensitive file system would otherwise fold into one file.
        string[] ids =
        [
            "unattributed:proc:host:copilot:4242", "a/b\\c", "CON", "Case", "case", "zażółć 🙂",
            new string('x', 400)
        ];
        await using (var store = await OpenAsync())
            for (var i = 0; i < ids.Length; i++) await store.PutAsync(StoredSessions.Snapshot(ids[i], T0.AddMinutes(i)));

        await using var reopened = await OpenAsync();
        foreach (var id in ids) Assert.Equal(id, (await reopened.GetAsync(id, None))?.Id);
        Assert.Equal(ids.Length, await reopened.CountAsync(null, null, None));
        Assert.All(Directory.GetFiles(SessionsDirectory, "*.json"),
            f => Assert.Matches("^[0-9a-f]{32}\\.json$", Path.GetFileName(f)));
    }

    [Fact]
    public async Task ASecondStoreOnTheSameDirectoryIsRefused()
    {
        await using (var first = await OpenAsync())
        {
            var second = new FileSessionRepository(_directory);
            var refused = await Assert.ThrowsAsync<IOException>(() => second.EnsureSchemaAsync(None));
            Assert.Contains("in use by another CopilotScope process", refused.Message);
            // Every operation opens the store on first use, so the refusal holds there too.
            await Assert.ThrowsAsync<IOException>(() => second.PutAsync(StoredSessions.Snapshot("s", T0)));
        }

        // Once the directory is free, the next open succeeds.
        await using var later = await OpenAsync();
        Assert.Equal(0, await later.CountAsync(null, null, None));
    }

    [Fact]
    public async Task AnUnreadableIndexCacheIsRebuiltFromTheFiles()
    {
        await using (var store = await OpenAsync())
            await store.PutAsync(StoredSessions.Snapshot("kept", T0));
        await File.WriteAllTextAsync(Path.Combine(_directory, "index.json"), "{ not json");

        await using var reopened = await OpenAsync();
        Assert.Equal("kept", (await reopened.QueryAsync(null, null, 10, 0, None)).Single().Id);
    }

    [Fact]
    public async Task AStaleIndexCacheNeverOutvotesTheFiles()
    {
        await using (var store = await OpenAsync())
        {
            await store.PutAsync(StoredSessions.Snapshot("rewritten", T0), score: 0.1);
            await store.PutAsync(StoredSessions.Snapshot("removed", T0.AddMinutes(1)));
        }
        var staleIndex = await File.ReadAllTextAsync(Path.Combine(_directory, "index.json"));

        // A later run changed one session and deleted another, then died before refreshing the
        // cache — which is exactly what a crash looks like from the next startup.
        await using (var store = await OpenAsync())
        {
            await store.PutAsync(StoredSessions.Snapshot("rewritten", T0.AddMinutes(5)), score: 0.9);
            await store.DeleteAsync("removed", None);
        }
        await File.WriteAllTextAsync(Path.Combine(_directory, "index.json"), staleIndex);

        await using var reopened = await OpenAsync();
        Assert.Equal(["rewritten"], (await reopened.QueryAsync(null, null, 10, 0, None)).Select(s => s.Id));
        Assert.Equal([0.9], await reopened.ScoresAsync(null, 10, None));
    }

    [Fact]
    public async Task ACorruptSessionFileIsSkippedAndLeftInPlace()
    {
        await using (var store = await OpenAsync())
            await store.PutAsync(StoredSessions.Snapshot("good", T0));
        var corrupt = Path.Combine(SessionsDirectory, "00000000000000000000000000000000.json");
        await File.WriteAllTextAsync(corrupt, "{ \"schema\": 1, \"id\": ");
        File.Delete(Path.Combine(_directory, "index.json"));

        await using var reopened = await OpenAsync();
        Assert.Equal("good", (await reopened.QueryAsync(null, null, 10, 0, None)).Single().Id);
        // Evidence for whoever has to work out what happened, not something to tidy away.
        Assert.True(File.Exists(corrupt));
    }

    [Fact]
    public async Task AFileRenamedByHandIsNotIndexedUnderAnotherSessionsName()
    {
        await using (var store = await OpenAsync())
            await store.PutAsync(StoredSessions.Snapshot("original", T0));
        File.Copy(Path.Combine(SessionsDirectory, FileSessionRepository.FileNameFor("original")),
            Path.Combine(SessionsDirectory, FileSessionRepository.FileNameFor("impostor")));
        File.Delete(Path.Combine(_directory, "index.json"));

        await using var reopened = await OpenAsync();
        Assert.Equal(1, await reopened.CountAsync(null, null, None));
        Assert.Null(await reopened.GetAsync("impostor", None));
    }

    [Fact]
    public async Task ADirectoryWrittenByANewerVersionIsNotOpened()
    {
        await using (var store = await OpenAsync())
            await store.PutAsync(StoredSessions.Snapshot("s", T0));
        var path = Path.Combine(SessionsDirectory, FileSessionRepository.FileNameFor("s"));
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"schema\":1", "\"schema\":99"));
        File.Delete(Path.Combine(_directory, "index.json"));

        var newer = new FileSessionRepository(_directory);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => newer.EnsureSchemaAsync(None));
        Assert.Contains("newer CopilotScope", refused.Message);
    }

    [Fact]
    public async Task AWriteThatNeverReachedItsRenameIsCleanedUp()
    {
        Directory.CreateDirectory(SessionsDirectory);
        var snapshot = Path.Combine(SessionsDirectory, "0123456789abcdef0123456789abcdef.json.1234.tmp");
        var index = Path.Combine(_directory, "index.json.5678.tmp");
        await File.WriteAllTextAsync(snapshot, "half a snapshot");
        await File.WriteAllTextAsync(index, "half an index");

        await using var store = await OpenAsync();
        Assert.False(File.Exists(snapshot));
        Assert.False(File.Exists(index));
    }

    [Fact]
    public async Task AFileWithNoSessionInItDoesNotBreakThePage()
    {
        await using (var store = await OpenAsync())
            await store.PutAsync(StoredSessions.Snapshot("whole", T0));
        await File.WriteAllTextAsync(Path.Combine(SessionsDirectory, FileSessionRepository.FileNameFor("hollow")),
            """{"schema":1,"id":"hollow","lastSeen":"2026-09-01T11:00:00+00:00","score":0.5,"grade":"B","kind":"UserChat","chatCalls":1,"emitter":"VSCode","models":[]}""");
        File.Delete(Path.Combine(_directory, "index.json"));

        await using var reopened = await OpenAsync();
        Assert.Equal(["whole"], (await reopened.QueryAsync(null, null, 10, 0, None)).Select(s => s.Id));
        Assert.Null(await reopened.GetAsync("hollow", None));
    }

    [Fact]
    public async Task AClosedStoreDoesNotQuietlyReopen()
    {
        // A caller that outlives the host must not take the directory's lock back and keep it,
        // which would lock the next collector out of its own history.
        var store = await OpenAsync();
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.PutAsync(StoredSessions.Snapshot("late", T0)));

        await using var next = await OpenAsync();
        Assert.Equal(0, await next.CountAsync(null, null, None));
    }

    [Fact]
    public async Task TheSessionDocumentIsWhatPostgresStores()
    {
        // Same record, same serializer settings: moving history between the two stores is a
        // copy of this document, not a conversion.
        var snapshot = StoredSessions.Snapshot("s", T0, "acme/api");
        await using (var store = await OpenAsync())
            await store.PutAsync(snapshot);

        using var file = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(SessionsDirectory, FileSessionRepository.FileNameFor("s"))));
        Assert.Equal(StoredSessions.Json(snapshot), file.RootElement.GetProperty("session").GetRawText());
        Assert.Equal(1, file.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public async Task SessionFilesAreReadableByTheirOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;

        await using (var store = await OpenAsync())
            await store.PutAsync(StoredSessions.Snapshot("s", T0));

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(ownerOnly,
            File.GetUnixFileMode(Path.Combine(SessionsDirectory, FileSessionRepository.FileNameFor("s"))));
        Assert.Equal(ownerOnly | UnixFileMode.UserExecute, File.GetUnixFileMode(SessionsDirectory));
    }
}

/// <summary>
/// Choosing the store. A mode that cannot be honoured must stop startup: a collector told to
/// keep history that quietly ran in memory would lose it and say so only in a log line.
/// </summary>
public sealed class StoragePlanTests
{
    [Fact]
    public void AutoPrefersPostgresThenFilesThenMemory()
    {
        Assert.Equal(StorageKind.Postgres,
            StoragePlan.Resolve(new StorageOptions { Path = "/data" }, "Host=db").Kind);
        Assert.Equal(new StoragePlan(StorageKind.Files, Path.GetFullPath("/data")),
            StoragePlan.Resolve(new StorageOptions { Path = "/data" }, null));
        Assert.Equal(StorageKind.Memory, StoragePlan.Resolve(new StorageOptions(), "").Kind);
    }

    [Fact]
    public void AnExplicitModeWinsOverAConnectionString()
    {
        var plan = StoragePlan.Resolve(new StorageOptions { Mode = "Files", Path = "/data" }, "Host=db");
        Assert.Equal(StorageKind.Files, plan.Kind);
        Assert.Equal(StorageKind.Memory, StoragePlan.Resolve(new StorageOptions { Mode = "memory" }, "Host=db").Kind);
    }

    [Fact]
    public void FilesWithoutAPathUseTheDirectoryTheNativeBinaryUses()
    {
        var plan = StoragePlan.Resolve(new StorageOptions { Mode = "files" }, null);
        Assert.Equal(StoragePlan.DefaultDirectory(), plan.Directory);
        Assert.EndsWith(Path.Combine(".copilotscope", "data"), plan.Directory);
    }

    [Fact]
    public void ATildeMeansTheHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plan = StoragePlan.Resolve(new StorageOptions { Path = "~/scope-data" }, null);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "scope-data")), plan.Directory);
    }

    [Fact]
    public void AModeThatCannotBeHonouredStopsStartup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StoragePlan.Resolve(new StorageOptions { Mode = "postgres" }, null));
        Assert.Throws<InvalidOperationException>(() =>
            StoragePlan.Resolve(new StorageOptions { Mode = "sqlite" }, null));
    }

    [Fact]
    public void HealthNamesAreLowerCase()
    {
        Assert.Equal("files", new StoragePlan(StorageKind.Files, "/d").Name);
        Assert.False(new StoragePlan(StorageKind.Memory).Durable);
    }
}

/// <summary>The write-behind path in front of whichever store is configured.</summary>
public sealed class PersistenceWriterTests : IDisposable
{
    private readonly string _directory = TempDirectory.Create();

    public void Dispose() => TempDirectory.Delete(_directory);

    private static void Ingest(SessionStore store, string conversationId) =>
        store.Ingest(new Collector.Otlp.OtlpBatch
        {
            Spans =
            {
                new Collector.Otlp.OtlpSpan
                {
                    TraceId = $"trace-{conversationId}", SpanId = "span-1", Name = "chat gpt-5",
                    Start = StoredSessions.T0, End = StoredSessions.T0.AddSeconds(1),
                    Attributes = new()
                    {
                        ["gen_ai.operation.name"] = Collector.Otlp.AttrValue.Str("chat"),
                        ["gen_ai.conversation.id"] = Collector.Otlp.AttrValue.Str(conversationId),
                        ["gen_ai.usage.input_tokens"] = Collector.Otlp.AttrValue.Int(10)
                    },
                    Resource = new() { ["session.id"] = Collector.Otlp.AttrValue.Str("window-1") }
                }
            }
        });

    [Fact]
    public async Task ShutdownWritesWhatTheLastSecondLeftDirty()
    {
        // The loop flushes once a second; stopping between two ticks used to drop whatever
        // arrived since the last one — on a laptop, the normal way the process ends.
        var store = new SessionStore();
        var files = new FileSessionRepository(_directory);
        var writer = new PersistenceWriter(files, store, new QualityEngine(), new HistoryOptions(),
            NullLogger<PersistenceWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        Ingest(store, "conv-last-second");
        writer.MarkDirty(["conv-last-second"]);
        await writer.StopAsync(CancellationToken.None);
        await files.DisposeAsync();

        await using var reopened = new FileSessionRepository(_directory);
        Assert.NotNull(await reopened.GetAsync("conv-last-second", CancellationToken.None));
    }

    /// <summary>A store whose writes wait to be released, so a test can hold one mid-flight.</summary>
    private sealed class HeldRepository : ISessionRepository
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes;
        public Task WriteStarted => _started.Task;
        public void Release() => _release.TrySetResult();

        public async Task UpsertAsync(PersistedSession session, double qualityScore, string qualityGrade,
            CancellationToken ct, string sessionKind = "UserChat")
        {
            _started.TrySetResult();
            await _release.Task;
            Interlocked.Increment(ref Writes);
        }

        public string Kind => "held";
        public string Description => "held";
        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<List<PersistedSession>> LoadAllAsync(int limit, CancellationToken ct) => Task.FromResult(new List<PersistedSession>());
        public Task<List<PersistedSession>> QueryAsync(DateTimeOffset? since, DateTimeOffset? until, int limit, int offset,
            CancellationToken ct, bool includeInternal = false, CohortFilter? cohort = null) => Task.FromResult(new List<PersistedSession>());
        public Task<PersistedSession?> GetAsync(string id, CancellationToken ct) => Task.FromResult<PersistedSession?>(null);
        public Task<int> CountAsync(DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct,
            bool includeInternal = false, CohortFilter? cohort = null) => Task.FromResult(0);
        public Task<List<double>> ScoresAsync(DateTimeOffset? since, int limit, CancellationToken ct) => Task.FromResult(new List<double>());
        public Task<List<string>> IdsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(new List<string>());
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteAsync(string id, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteByPrefixAsync(string prefix, CancellationToken ct) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TwoStopsAtOnceShareOneFinalFlush()
    {
        // WebApplicationFactory stops a host while the application's own RunAsync stops it again
        // and then disposes the container. The second stop must not return — letting the store be
        // disposed — while the first is still writing.
        var store = new SessionStore();
        var held = new HeldRepository();
        var writer = new PersistenceWriter(held, store, new QualityEngine(), new HistoryOptions(),
            NullLogger<PersistenceWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        Ingest(store, "conv-two-stops");
        writer.MarkDirty(["conv-two-stops"]);

        var first = writer.StopAsync(CancellationToken.None);
        var second = writer.StopAsync(CancellationToken.None);
        await held.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(second.IsCompleted, "the second stop returned while the final flush was still writing");

        held.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, held.Writes);
    }

    [Fact]
    public async Task StartupRehydratesMemoryFromTheFiles()
    {
        await using (var files = new FileSessionRepository(_directory))
            await files.PutAsync(StoredSessions.Snapshot("from-yesterday", StoredSessions.T0));

        var store = new SessionStore();
        await using var reopened = new FileSessionRepository(_directory);
        var writer = new PersistenceWriter(reopened, store, new QualityEngine(), new HistoryOptions(),
            NullLogger<PersistenceWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        Assert.NotNull(store.Get("from-yesterday"));
    }

    [Fact]
    public async Task TheReadPathServesHistoryTheMemoryCapNoLongerHolds()
    {
        await using var files = new FileSessionRepository(_directory);
        for (var i = 0; i < 3; i++)
            await files.PutAsync(StoredSessions.Snapshot($"s{i}", StoredSessions.T0.AddMinutes(i)));

        var service = new SessionQueryService(new SessionStore(), new QualityEngine(), new HistoryOptions(), files);
        var page = await service.PageAsync(false, null, null, null, null, CancellationToken.None);

        Assert.True(page.Durable);
        Assert.Equal(["s2", "s1", "s0"], page.Sessions.Select(s => s.Id));
        Assert.Equal(3, page.Total);
        Assert.NotNull(await service.FindAsync("s0", CancellationToken.None));
    }
}

/// <summary>The collector itself, configured for file storage, across a restart.</summary>
public sealed class FileStorageCollectorTests : IDisposable
{
    private readonly string _directory = TempDirectory.Create();

    public void Dispose() => TempDirectory.Delete(_directory);

    // UseSetting rather than ConfigureAppConfiguration: the storage mode decides what gets
    // registered, so it is read before the host is built, and only host settings reach it then.
    private WebApplicationFactory<SessionSummaryDto> Collector() =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
        {
            b.UseSetting("CopilotScope:Storage:Mode", "files");
            b.UseSetting("CopilotScope:Storage:Path", _directory);
        });

    private static HttpContent ChatSpan(string conversationId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = TestOtlp.TracesRequest("window-1",
            TestOtlp.Span(TestOtlp.Trace(7), TestOtlp.SpanId(7), "chat gpt-5", now, now.AddMilliseconds(900), null,
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.conversation.id", conversationId),
                ("gen_ai.response.model", "gpt-5"),
                ("gen_ai.usage.input_tokens", 120L)));
        return new ByteArrayContent(payload) { Headers = { ContentType = new("application/x-protobuf") } };
    }

    [Fact]
    public async Task SessionsOutliveTheCollectorProcess()
    {
        using (var collector = Collector())
        {
            var client = collector.CreateClient();
            var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
            Assert.Equal("files", health.GetProperty("storage").GetString());
            Assert.True(health.GetProperty("persistence").GetBoolean());

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/traces", ChatSpan("conv-restart"))).StatusCode);
        } // disposing the host stops it — and the shutdown flush writes the session

        using var restarted = Collector();
        var response = await restarted.CreateClient().GetAsync("/api/sessions/conv-restart");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithoutConfigurationTheCollectorStillRunsInMemory()
    {
        using var collector = new WebApplicationFactory<SessionSummaryDto>();
        var health = await collector.CreateClient().GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("memory", health.GetProperty("storage").GetString());
        Assert.False(health.GetProperty("persistence").GetBoolean());
    }
}

/// <summary>The dashboard's status chip, which used to say "Postgres" for any durable store.</summary>
public sealed class DashboardStorageLabelTests
{
    private sealed class FixedResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Theory]
    [InlineData("""{"status":"ok","sessions":1,"persistence":true,"storage":"files","forwarding":false,"environment":"Production"}""", "local files")]
    [InlineData("""{"status":"ok","sessions":1,"persistence":true,"storage":"postgres","forwarding":false,"environment":"Production"}""", "Postgres")]
    [InlineData("""{"status":"ok","sessions":1,"persistence":false,"storage":"memory","forwarding":false,"environment":"Production"}""", "in-memory")]
    // A collector from before file storage reports only the boolean, and then durable meant Postgres.
    [InlineData("""{"status":"ok","sessions":1,"persistence":true,"forwarding":false,"environment":"Production"}""", "Postgres")]
    public async Task TheChipNamesTheStoreTheCollectorReports(string health, string label)
    {
        var http = new HttpClient(new FixedResponse(health)) { BaseAddress = new Uri("http://collector") };
        var reported = await new CopilotScope.Dashboard.Services.CollectorClient(http).GetHealthAsync();
        Assert.Equal(label, reported!.StorageLabel);
    }
}
