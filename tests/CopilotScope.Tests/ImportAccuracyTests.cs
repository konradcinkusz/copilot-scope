using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using CopilotScope.LogImporter;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The ways a Claude Code transcript import used to count wrongly — each one quiet, each one
/// in the direction of a score that was not earned or a history that was not there. They
/// matter more now that the native binary scans transcripts continuously (ADR-004): a
/// miscount repeated on every rescan is a miscount in every view.
/// </summary>
public sealed class ImportAccuracyTests
{
    private const string Session = "aaaaaaaa-0000-0000-0000-000000000001";

    private static JsonObject Line(string type, string uuid, string? at, bool sidechain, JsonObject message)
    {
        var line = new JsonObject
        {
            ["type"] = type, ["isSidechain"] = sidechain, ["sessionId"] = Session, ["uuid"] = uuid,
            ["message"] = message
        };
        if (at is not null) line["timestamp"] = at;
        return line;
    }

    private static string User(string uuid, string at, string text, bool sidechain = false) =>
        Line("user", uuid, at, sidechain, new JsonObject { ["role"] = "user", ["content"] = text }).ToJsonString();

    private static string Assistant(string uuid, string? at, string messageId, int input, int output,
        JsonArray? content = null, bool sidechain = false) =>
        Line("assistant", uuid, at, sidechain, new JsonObject
        {
            ["id"] = messageId,
            ["model"] = "claude-sonnet-5",
            ["content"] = content ?? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }),
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output }
        }).ToJsonString();

    private static string ToolResult(string uuid, string at, string toolUseId, bool error = false, bool sidechain = false) =>
        Line("user", uuid, at, sidechain, new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["is_error"] = error
            })
        }).ToJsonString();

    private static JsonArray ToolUse(string id, string name) =>
        new(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() });

    private static CopilotSession Parse(params string[] lines) => ClaudeCodeTranscript.Parse(lines)!.Session;

    // ------------------------------------------------------------------ the parser

    [Fact]
    public void ARepeatedUsageOnTheLinesOfOneResponseIsCountedOnce()
    {
        // Claude Code writes one response as one line per content block, and each line can
        // repeat the response's usage. Counting per line doubled tokens and calls.
        var session = Parse(
            User("u1", "2026-09-01T10:00:00Z", "fix it"),
            Assistant("a1", "2026-09-01T10:00:02Z", "msg_1", 100, 50),
            Assistant("a2", "2026-09-01T10:00:02Z", "msg_1", 100, 50, ToolUse("t1", "Read")));

        Assert.Equal(1, session.ChatCalls);
        Assert.Equal(100, session.InputTokens);
        Assert.Equal(50, session.OutputTokens);
        Assert.Equal(1, session.ModelCalls["claude-sonnet-5"]);
    }

    [Fact]
    public void ALaterLineOfTheSameResponseAddsOnlyWhatItReportsBeyondTheFirst()
    {
        var session = Parse(
            User("u1", "2026-09-01T10:00:00Z", "fix it"),
            Assistant("a1", "2026-09-01T10:00:01Z", "msg_1", 100, 5),
            Assistant("a2", "2026-09-01T10:00:03Z", "msg_1", 100, 50));

        Assert.Equal(1, session.ChatCalls);
        Assert.Equal(50, session.OutputTokens);
        Assert.Equal(50, session.ModelUsage["claude-sonnet-5"].OutputTokens);
        Assert.Equal(50, session.TurnList.Single().OutputTokens);
    }

    [Fact]
    public void CopilotScopesOwnToolCallsAreNeverScored()
    {
        // The rule both OTel paths already follow (SelfObservation): a read of a score must not
        // move the score being read.
        var session = Parse(
            User("u1", "2026-09-01T10:00:00Z", "how did that session score?"),
            Assistant("a1", "2026-09-01T10:00:01Z", "msg_1", 10, 5, ToolUse("t1", "mcp__copilotscope__get_session")),
            ToolResult("u2", "2026-09-01T10:00:02Z", "t1"),
            Assistant("a2", "2026-09-01T10:00:03Z", "msg_2", 10, 5, ToolUse("t2", "Read")),
            ToolResult("u3", "2026-09-01T10:00:04Z", "t2"));

        Assert.Equal(1, session.ToolCalls);
        Assert.False(session.Tools.ContainsKey("mcp__copilotscope__get_session"));
        Assert.Equal(1, session.TurnList.Single().ToolCalls);
        // Dropped from the counters, not from the record.
        Assert.Contains(session.RecentEvents, e => e.Summary.StartsWith("mcp__copilotscope__get_session", StringComparison.Ordinal));
    }

    [Fact]
    public void ASubagentsWorkCountsButItsPromptsAreNotTheDevelopersTurns()
    {
        var session = Parse(
            User("u1", "2026-09-01T10:00:00Z", "refactor the forwarder"),
            Assistant("a1", "2026-09-01T10:00:01Z", "msg_1", 100, 10, ToolUse("t1", "Task")),
            User("s1", "2026-09-01T10:00:02Z", "find every caller of Forward()", sidechain: true),
            Assistant("s2", "2026-09-01T10:00:03Z", "msg_s1", 300, 20, ToolUse("t2", "Grep"), sidechain: true),
            ToolResult("s3", "2026-09-01T10:00:04Z", "t2", sidechain: true),
            ToolResult("u2", "2026-09-01T10:00:05Z", "t1"));

        Assert.Equal(1, session.Turns);
        Assert.Equal(1, session.AgentInvocations);
        Assert.Equal(2, session.ChatCalls);
        Assert.Equal(400, session.InputTokens);
        Assert.Equal(2, session.ToolCalls);
        Assert.Equal(2, session.TurnList.Single().ChatCalls);
    }

    [Fact]
    public void ATranscriptOfOnlyASubagentsConversationIsNotASessionOfItsOwn()
    {
        var parsed = ClaudeCodeTranscript.Parse([
            User("s1", "2026-09-01T10:00:02Z", "find every caller", sidechain: true),
            Assistant("s2", "2026-09-01T10:00:03Z", "msg_s1", 300, 20, sidechain: true)
        ]);
        Assert.Null(parsed);
    }

    [Fact]
    public void ALineWithoutATimestampDoesNotMakeAnOldSessionLookCurrent()
    {
        var session = Parse(
            User("u1", "2026-01-15T09:00:00Z", "old work"),
            Assistant("a1", null, "msg_1", 10, 5));

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero), session.LastSeen);
        Assert.Equal(1, session.ChatCalls);
    }

    [Fact]
    public void TheTurnListIsCappedButEveryPromptIsCounted()
    {
        var lines = Enumerable.Range(0, 250).SelectMany(i => new[]
        {
            User($"u{i}", $"2026-09-01T10:{i / 60:D2}:{i % 60:D2}Z", "next"),
            Assistant($"a{i}", $"2026-09-01T10:{i / 60:D2}:{i % 60:D2}Z", $"msg_{i}", 1, 1)
        }).ToArray();

        var session = Parse(lines);

        Assert.Equal(250, session.Turns);
        Assert.Equal(200, session.TurnList.Count);
        Assert.Equal(250, session.ChatCalls);
    }

    [Fact]
    public void ALineReadTwiceIsCountedOnce()
    {
        var reply = Assistant("a1", "2026-09-01T10:00:01Z", "msg_1", 10, 5);
        var session = Parse(User("u1", "2026-09-01T10:00:00Z", "hi"), reply, reply);
        Assert.Equal(1, session.ChatCalls);
        Assert.Equal(10, session.InputTokens);
    }

    // ------------------------------------------------------------------ the files

    [Fact]
    public void ASubagentsFileIsFoldedIntoItsParentInsteadOfReplacingIt()
    {
        // Imported one file at a time, a subagent's transcript — same session id — was a second
        // import of the session, and whichever was sent last replaced the other.
        var root = Directory.CreateTempSubdirectory("copilotscope-subagents");
        try
        {
            var main = Path.Combine(root.FullName, $"{Session}.jsonl");
            File.WriteAllLines(main, [
                User("u1", "2026-09-01T10:00:00Z", "first request"),
                Assistant("a1", "2026-09-01T10:00:01Z", "msg_1", 100, 10, ToolUse("t1", "Task")),
                ToolResult("u2", "2026-09-01T10:00:05Z", "t1"),
                User("u3", "2026-09-01T10:10:00Z", "second request"),
                Assistant("a3", "2026-09-01T10:10:01Z", "msg_3", 100, 10)
            ]);
            var subagents = Directory.CreateDirectory(Path.Combine(root.FullName, Session, "subagents"));
            File.WriteAllLines(Path.Combine(subagents.FullName, "agent-1.jsonl"), [
                User("s1", "2026-09-01T10:00:02Z", "search", sidechain: true),
                Assistant("s2", "2026-09-01T10:00:03Z", "msg_s1", 300, 20, sidechain: true)
            ]);

            var group = Assert.Single(ImportCommand.GroupBySession(ImportCommand.Discover(root.FullName)));
            Assert.Equal(2, group.Files.Count);

            var session = ClaudeCodeTranscript.Parse(ImportCommand.LinesOf(group.Files))!.Session;
            Assert.Equal(2, session.Turns);
            Assert.Equal(3, session.ChatCalls);
            // Interleaved by time: the subagent's call belongs to the turn that ran it.
            Assert.Equal(2, session.TurnList[0].ChatCalls);
            Assert.Equal(1, session.TurnList[1].ChatCalls);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void ALineWithoutATimestampStaysWithItsOwnFileWhenFilesAreMerged()
    {
        // Sorted on its own, a line with no timestamp went ahead of every file, before any time
        // was known, and was dropped; it belongs where its own file put it.
        var root = Directory.CreateTempSubdirectory("copilotscope-untimed");
        try
        {
            File.WriteAllLines(Path.Combine(root.FullName, $"{Session}.jsonl"), [
                User("u1", "2026-09-01T10:00:00Z", "first request"),
                Assistant("a1", "2026-09-01T10:00:01Z", "msg_1", 100, 10, ToolUse("t1", "Task")),
                User("u3", "2026-09-01T10:10:00Z", "second request"),
                Assistant("a3", "2026-09-01T10:10:01Z", "msg_3", 100, 10)
            ]);
            File.WriteAllLines(Path.Combine(root.FullName, "agent-1.jsonl"), [
                User("s1", "2026-09-01T10:00:02Z", "search", sidechain: true),
                Assistant("s2", null, "msg_s1", 300, 20, sidechain: true)
            ]);

            var group = Assert.Single(ImportCommand.GroupBySession(ImportCommand.Discover(root.FullName)));
            var session = ClaudeCodeTranscript.Parse(ImportCommand.LinesOf(group.Files))!.Session;

            Assert.Equal(3, session.ChatCalls);
            Assert.Equal(2, session.TurnList[0].ChatCalls);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void BothOfClaudeCodesDataDirectoriesAreReadByDefault()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"))) return;
        var roots = ImportCommand.DefaultRoots();
        Assert.Contains(roots, r => r.EndsWith(Path.Combine(".claude", "projects"), StringComparison.Ordinal));
        Assert.Contains(roots, r => r.EndsWith(Path.Combine(".config", "claude", "projects"), StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the collector

    private static CopilotSession Imported(string id, DateTimeOffset lastSeen) => new()
    {
        Id = id, Origin = SessionOrigin.LogImport, EmitterKind = EmitterKind.ClaudeCode,
        FirstSeen = lastSeen.AddMinutes(-5), LastSeen = lastSeen, ChatCalls = 1, InputTokens = 10
    };

    [Fact]
    public void LiveTelemetryMakesAnImportedSessionLive()
    {
        // While it still said "imported", the next import of the same transcript replaced it —
        // and with it the latency and edit decisions the telemetry had added.
        var store = new SessionStore();
        store.Put(Imported("conv-imported-then-live", DateTimeOffset.UtcNow.AddMinutes(-1)));

        var batch = new OtlpBatch();
        batch.Spans.Add(new OtlpSpan
        {
            TraceId = "trace-live", SpanId = "span-live", Name = "chat claude-sonnet-5",
            Start = DateTimeOffset.UtcNow, End = DateTimeOffset.UtcNow.AddSeconds(1),
            Attributes = new()
            {
                ["gen_ai.operation.name"] = AttrValue.Str("chat"),
                ["gen_ai.conversation.id"] = AttrValue.Str("conv-imported-then-live")
            },
            Resource = new() { ["session.id"] = AttrValue.Str("window-1") }
        });
        store.Ingest(batch);

        Assert.Equal(SessionOrigin.Otel, store.Get("conv-imported-then-live")!.Origin);
    }

    private static ImportRequest Many(int count) => new([.. Enumerable.Range(0, count)
        .Select(i => PersistedSession.From(Imported($"history-{i:D4}", StoredSessions.T0.AddMinutes(i))))]);

    [Fact]
    public async Task ALongImportedHistoryKeepsMemoryBoundedWhenStorageIsDurable()
    {
        var data = TempDirectory.Create();
        try
        {
            using var factory = new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            {
                b.UseSetting("CopilotScope:Storage:Mode", "files");
                b.UseSetting("CopilotScope:Storage:Path", data);
            });
            using var client = factory.CreateClient();

            Assert.True((await client.PostAsJsonAsync("/api/import", Many(250))).IsSuccessStatusCode);

            var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
            Assert.InRange(health.GetProperty("sessions").GetInt32(), 0, 200);
            // Evicted from memory, not lost: the read path still pages through all of it.
            var page = await client.GetFromJsonAsync<SessionPage>("/api/sessions?limit=500");
            Assert.Equal(250, page!.Total);
        }
        finally { TempDirectory.Delete(data); }
    }

    [Fact]
    public async Task InMemoryOnlyAnImportIsNotThrownAwayAsItArrives()
    {
        using var factory = new WebApplicationFactory<SessionSummaryDto>();
        using var client = factory.CreateClient();

        Assert.True((await client.PostAsJsonAsync("/api/import", Many(250))).IsSuccessStatusCode);

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal(250, health.GetProperty("sessions").GetInt32());
    }
}
