using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The read path over session history: paging, windowing, and the eviction rules that keep
/// a memory cap from destroying persisted data.
///
/// The Postgres-backed half of <see cref="SessionQueryService"/> needs a live database and
/// is exercised by the integration suite; these tests cover the store's eviction bookkeeping
/// and the in-memory fallback the service degrades to when Postgres is absent or down.
/// </summary>
public class SessionHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private static OtlpBatch ChatBatch(string conversationId, DateTimeOffset at, int seed)
    {
        var batch = new OtlpBatch();
        batch.Spans.Add(new OtlpSpan
        {
            TraceId = $"trace-{seed}",
            SpanId = $"span-{seed}",
            Name = "chat gpt-4o",
            Start = at,
            End = at.AddSeconds(1),
            Attributes = new()
            {
                ["gen_ai.operation.name"] = AttrValue.Str("chat"),
                ["gen_ai.conversation.id"] = AttrValue.Str(conversationId),
                ["gen_ai.usage.input_tokens"] = AttrValue.Int(10)
            },
            Resource = new() { ["session.id"] = AttrValue.Str($"window-{seed}") }
        });
        return batch;
    }

    /// <summary>
    /// Ingests one chat span and stamps the session's activity time. LastSeen is set from the
    /// wall clock at ingest ("when we last heard about it"), not from the span, so a test
    /// about time windows has to place the session on the timeline itself.
    /// </summary>
    private static void IngestAt(SessionStore store, string id, DateTimeOffset lastSeen, int seed)
    {
        store.Ingest(ChatBatch(id, lastSeen, seed));
        if (store.Get(id) is { } s) { s.FirstSeen = lastSeen; s.LastSeen = lastSeen; }
    }

    // ---------------------------------------------------------------- eviction bookkeeping

    [Fact]
    public void TrimmedSessionsAreReportedWhenIngestRecreatesThem()
    {
        // The bug this guards: a trimmed session that receives late telemetry was recreated
        // as an empty aggregate, and the next write-behind flush wrote THAT over the stored
        // snapshot — persisted history destroyed by a memory cap.
        var store = new SessionStore();
        for (var i = 0; i < 260; i++) IngestAt(store, $"conv-{i}", T0.AddSeconds(i), i);

        Assert.True(store.All.Count <= 200, "store must stay under its cap");
        Assert.Null(store.Get("conv-0"));                  // evicted (oldest)
        Assert.Empty(store.DrainResurrected());            // nothing recreated yet

        // Late telemetry for the evicted session.
        store.Ingest(ChatBatch("conv-0", T0.AddHours(1), 900));

        Assert.Contains("conv-0", store.DrainResurrected());
        Assert.Empty(store.DrainResurrected());            // reported exactly once
    }

    [Fact]
    public void ActiveSessionsAreNeverReportedAsResurrected()
    {
        var store = new SessionStore();
        store.Ingest(ChatBatch("conv-live", T0, 1));
        store.Ingest(ChatBatch("conv-live", T0.AddSeconds(5), 2));

        Assert.Empty(store.DrainResurrected());
        Assert.Equal(2, store.Get("conv-live")!.ChatCalls);
    }

    [Fact]
    public void DeletingASessionCancelsItsPendingRehydration()
    {
        // A deliberate delete must not be "repaired" later by merging back the row it removed.
        var store = new SessionStore();
        for (var i = 0; i < 260; i++) IngestAt(store, $"conv-{i}", T0.AddSeconds(i), i);

        store.Remove("conv-0");
        store.Ingest(ChatBatch("conv-0", T0.AddHours(1), 901));

        Assert.DoesNotContain("conv-0", store.DrainResurrected());
    }

    [Fact]
    public void TrimReleasesTraceMappingsOfEvictedSessions()
    {
        // Trace mappings outlived their sessions, so the index grew without bound and a
        // recycled trace id could route a span into a session that no longer existed.
        var store = new SessionStore();
        for (var i = 0; i < 260; i++) IngestAt(store, $"conv-{i}", T0.AddSeconds(i), i);

        // trace-0 belonged to the evicted conv-0. A span on that trace carrying no
        // conversation id must not resolve back to it.
        var orphan = new OtlpBatch();
        orphan.Spans.Add(new OtlpSpan
        {
            TraceId = "trace-0",
            SpanId = "span-orphan",
            Name = "chat gpt-4o",
            Start = T0.AddHours(2),
            End = T0.AddHours(2).AddSeconds(1),
            Attributes = new() { ["gen_ai.operation.name"] = AttrValue.Str("chat") },
            Resource = new() { ["session.id"] = AttrValue.Str("window-orphan") }
        });
        store.Ingest(orphan);

        Assert.Null(store.Get("conv-0"));
    }

    [Fact]
    public void MergingAStoredSnapshotBackIsAdditiveAndTurnSafe()
    {
        // What the rehydration repair relies on: merging the stored snapshot into the
        // recreated aggregate must total the same as if nothing had ever been evicted.
        var original = new CopilotSession { Id = "conv-x", FirstSeen = T0, LastSeen = T0 };
        original.ChatCalls = 5;
        original.InputTokens = 500;
        original.Apply(s => s.TurnFor("trace-a", T0));

        var stored = PersistedSession.From(original);

        var recreated = new CopilotSession { Id = "conv-x", FirstSeen = T0.AddHours(1), LastSeen = T0.AddHours(1) };
        recreated.ChatCalls = 1;
        recreated.InputTokens = 10;
        recreated.Apply(s => s.TurnFor("trace-a", T0.AddHours(1))); // same turn, seen again

        recreated.MergeFrom(stored.ToSession());

        Assert.Equal(6, recreated.ChatCalls);
        Assert.Equal(510, recreated.InputTokens);
        Assert.Equal(T0, recreated.FirstSeen);                  // earliest start wins
        Assert.Single(recreated.TurnList);                      // turn de-duplicated by trace id
    }

    // ---------------------------------------------------------------- in-memory read path

    private static SessionQueryService InMemoryService(SessionStore store, HistoryOptions? options = null) =>
        new(store, new QualityEngine(), options ?? new HistoryOptions(), repository: null);

    [Fact]
    public async Task PageReportsItIsNotDurableWithoutPostgres()
    {
        var store = new SessionStore();
        store.Ingest(ChatBatch("conv-a", T0, 1));

        var page = await InMemoryService(store).PageAsync(false, null, null, null, null, CancellationToken.None);

        Assert.False(page.Durable);
        Assert.Single(page.Sessions);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task PagingSlicesNewestFirst()
    {
        var store = new SessionStore();
        for (var i = 0; i < 10; i++) IngestAt(store, $"conv-{i}", T0.AddMinutes(i), i);

        var svc = InMemoryService(store);
        var first = await svc.PageAsync(false, null, null, limit: 3, offset: 0, CancellationToken.None);
        var second = await svc.PageAsync(false, null, null, limit: 3, offset: 3, CancellationToken.None);

        Assert.Equal(["conv-9", "conv-8", "conv-7"], first.Sessions.Select(s => s.Id));
        Assert.Equal(["conv-6", "conv-5", "conv-4"], second.Sessions.Select(s => s.Id));
        Assert.Equal(10, first.Total);
    }

    [Fact]
    public async Task PageSizeIsCappedByConfiguration()
    {
        var store = new SessionStore();
        for (var i = 0; i < 30; i++) IngestAt(store, $"conv-{i}", T0.AddMinutes(i), i);

        var svc = InMemoryService(store, new HistoryOptions { MaxPageSize = 5 });
        var page = await svc.PageAsync(false, null, null, limit: 1000, offset: 0, CancellationToken.None);

        Assert.Equal(5, page.Sessions.Count);
        Assert.Equal(5, page.Limit);
    }

    [Fact]
    public async Task TimeWindowExcludesOlderSessions()
    {
        var store = new SessionStore();
        IngestAt(store, "conv-old", T0, 1);
        IngestAt(store, "conv-new", T0.AddDays(10), 2);

        var page = await InMemoryService(store)
            .PageAsync(false, since: T0.AddDays(5), until: null, null, null, CancellationToken.None);

        Assert.Equal("conv-new", Assert.Single(page.Sessions).Id);
    }

    [Fact]
    public async Task FindFallsBackToNullForUnknownIdsWithoutPostgres()
    {
        var store = new SessionStore();
        Assert.Null(await InMemoryService(store).FindAsync("nope", CancellationToken.None));
    }
}
