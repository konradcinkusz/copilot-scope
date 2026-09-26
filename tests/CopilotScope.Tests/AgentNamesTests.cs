using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// A session can involve several agents. Copilot CLI emits one <c>invoke_agent</c> span per agent
/// and subagent, each naming itself in <c>gen_ai.agent.name</c>; <see cref="CopilotSession.AgentName"/>
/// keeps one of them, and <see cref="CopilotSession.AgentNames"/> keeps them all — through ingest,
/// merges, storage and the REST API.
/// </summary>
public class AgentNamesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static byte[] InvokeAgent(int seed, string agentName, string? conversationId, DateTimeOffset at)
    {
        var attrs = new List<(string, object)>
        {
            ("gen_ai.operation.name", "invoke_agent"),
            ("gen_ai.agent.name", agentName),
        };
        if (conversationId is not null) attrs.Add(("gen_ai.conversation.id", conversationId));
        return TestOtlp.Span(TestOtlp.Trace(seed), TestOtlp.SpanId(seed), $"invoke_agent {agentName}",
            at, at.AddSeconds(2), error: null, attrs.ToArray());
    }

    private static OtlpBatch Batch(params byte[][] spans)
    {
        var batch = new OtlpBatch();
        OtlpDecoder.DecodeTraces(TestOtlp.TracesRequest("vscode-window-1", spans), batch);
        return batch;
    }

    private static List<string> Names(CopilotSession s) => s.Snapshot(x => x.AgentNames.ToList());

    // ------------------------------------------------------------------ ingest

    [Fact]
    public void EveryAgentInAConversationIsKeptInTheOrderFirstSeen()
    {
        var store = new SessionStore();
        store.Ingest(Batch(
            InvokeAgent(1, "copilot", "conv-multi", T0),
            InvokeAgent(2, "explore", "conv-multi", T0.AddSeconds(1))));

        var session = store.Get("conv-multi")!;
        Assert.Equal(["copilot", "explore"], Names(session));
        // AgentName is unchanged: the last name reported, as before the list existed.
        Assert.Equal("explore", session.AgentName);

        // The parent agent again: named once in the list, and AgentName still follows the last span.
        store.Ingest(Batch(InvokeAgent(3, "copilot", "conv-multi", T0.AddSeconds(5))));
        Assert.Equal(["copilot", "explore"], Names(session));
        Assert.Equal("copilot", session.AgentName);
        Assert.Equal(3, session.AgentInvocations);
    }

    [Fact]
    public void TheListIsCappedWhileAgentNameIsNot()
    {
        var store = new SessionStore();
        var spans = Enumerable.Range(0, 40)
            .Select(i => InvokeAgent(100 + i, $"agent-{i:D2}", "conv-many", T0.AddSeconds(i)))
            .ToArray();
        store.Ingest(Batch(spans));

        var session = store.Get("conv-many")!;
        var names = Names(session);
        Assert.Equal(CopilotSession.MaxAgentNames, names.Count);
        Assert.Equal(Enumerable.Range(0, 32).Select(i => $"agent-{i:D2}"), names);
        Assert.Equal("agent-39", session.AgentName);
    }

    [Fact]
    public void NamesAreOrdinalNonBlankAndBounded()
    {
        var s = new CopilotSession { Id = "bounds" };
        s.AddAgentName(null);
        s.AddAgentName("");
        s.AddAgentName("   ");
        s.AddAgentName("Explore");
        s.AddAgentName("explore");   // a different name under ordinal comparison
        s.AddAgentName("Explore");   // a repeat
        s.AddAgentName(new string('x', 500));
        // A cut that would land between the halves of a surrogate pair drops the whole pair.
        s.AddAgentName(new string('a', CopilotSession.MaxAgentNameChars - 1) + "\U0001F600" + "tail");

        Assert.Equal(4, s.AgentNames.Count);
        Assert.Equal("Explore", s.AgentNames[0]);
        Assert.Equal("explore", s.AgentNames[1]);
        Assert.Equal(new string('x', CopilotSession.MaxAgentNameChars), s.AgentNames[2]);
        Assert.Equal(new string('a', CopilotSession.MaxAgentNameChars - 1), s.AgentNames[3]);
    }

    [Fact]
    public void AnImportedTranscriptNamesItsAgent()
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "user", uuid = "u1", sessionId = "imported-1", timestamp = T0,
            message = new { role = "user", content = "fix the build" },
        });

        var imported = CopilotScope.Collector.Import.ClaudeCodeTranscript.Parse([line], repository: null)!;

        Assert.Equal("claude-code", imported.Session.AgentName);
        Assert.Equal(["claude-code"], imported.Session.AgentNames);
    }

    // ------------------------------------------------------------------ merges

    private static CopilotSession With(string id, DateTimeOffset firstSeen, params string[] names)
    {
        var s = new CopilotSession { Id = id, FirstSeen = firstSeen, LastSeen = firstSeen, AgentName = names.LastOrDefault() };
        foreach (var n in names) s.AddAgentName(n);
        return s;
    }

    [Fact]
    public void AMergeUnionsTheListsWithTheEarlierSessionsNamesFirst()
    {
        // The rehydration repair: a recreated aggregate merges its older stored snapshot back in.
        var recreated = With("conv-x", T0.AddHours(1), "copilot", "explore");
        var stored = With("conv-x", T0, "plan", "copilot");

        recreated.MergeFrom(stored);

        Assert.Equal(["plan", "copilot", "explore"], Names(recreated));
        Assert.Equal("explore", recreated.AgentName); // the target's own, as before
    }

    [Fact]
    public void AMergeOfALaterSessionAppendsItsNewNames()
    {
        var target = With("conv-y", T0, "copilot", "explore");
        target.MergeFrom(With("bucket", T0.AddMinutes(5), "review", "explore"));

        Assert.Equal(["copilot", "explore", "review"], Names(target));
    }

    [Fact]
    public void AMergeHoldsTheCap()
    {
        var target = With("conv-z", T0, Enumerable.Range(0, 30).Select(i => $"a{i}").ToArray());
        target.MergeFrom(With("other", T0.AddMinutes(1), "b0", "b1", "b2", "b3", "b4"));

        var names = Names(target);
        Assert.Equal(CopilotSession.MaxAgentNames, names.Count);
        Assert.Equal(["b0", "b1"], names.Skip(30));
    }

    [Fact]
    public void AnUnattributedBucketHandsItsAgentsToTheConversationThatClaimsIt()
    {
        var store = new SessionStore();
        var now = DateTimeOffset.UtcNow;

        // A subagent span that arrives before anything in its emitter names the conversation.
        store.Ingest(Batch(InvokeAgent(41, "explore", conversationId: null, now)));
        var bucket = Assert.Single(store.All);
        Assert.StartsWith("unattributed", bucket.Id);

        store.Ingest(Batch(InvokeAgent(42, "copilot", "conv-claim", now.AddSeconds(3))));

        var session = Assert.Single(store.All);
        Assert.Equal("conv-claim", session.Id);
        Assert.Equal(["explore", "copilot"], Names(session));
    }

    // ------------------------------------------------------------- persistence

    [Fact]
    public void ARoundTripThroughStorageKeepsTheList()
    {
        var s = With("rt-agents", T0, "copilot", "explore", "review");

        var restored = PersistedSession.From(s).ToSession();

        Assert.Equal(["copilot", "explore", "review"], restored.AgentNames);
        Assert.Equal("review", restored.AgentName);
    }

    [Fact]
    public void ASnapshotWrittenBeforeTheListExistedLoadsWithAnEmptyOne()
    {
        var json = JsonSerializer.SerializeToNode(PersistedSession.From(With("old", T0, "copilot")), Web)!.AsObject();
        Assert.True(json.Remove("agentNames"));

        var snapshot = json.Deserialize<PersistedSession>(Web)!;
        var restored = snapshot.ToSession();

        Assert.Empty(restored.AgentNames);
        Assert.Equal("copilot", restored.AgentName);
    }

    [Fact]
    public void ASnapshotFromElsewhereIsHeldToTheSameBounds()
    {
        // Seed and import accept snapshots over HTTP; loading one must not bypass the bounds.
        var names = Enumerable.Range(0, 50).Select(i => $"n{i}").Concat(["n0", " ", new string('y', 300)]).ToList();
        var snapshot = PersistedSession.From(new CopilotSession { Id = "foreign" }) with { AgentNames = names };

        var restored = snapshot.ToSession();

        Assert.Equal(Enumerable.Range(0, 32).Select(i => $"n{i}"), restored.AgentNames);
    }

    // ------------------------------------------------------------------- the API

    [Fact]
    public void TheSummaryDtoCarriesTheListNextToTheAgent()
    {
        var dto = Dto.Summary(With("dto", T0, "copilot", "explore"), new QualityEngine());
        var json = JsonSerializer.SerializeToElement(dto, Web);

        Assert.Equal("explore", json.GetProperty("agent").GetString());
        Assert.Equal(["copilot", "explore"],
            json.GetProperty("agentNames").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public void TheSummaryDtoIsNeverNullWithoutTheList()
    {
        var dto = Dto.Summary(new CopilotSession { Id = "none" }, new QualityEngine());
        Assert.Empty(dto.AgentNames);

        var json = JsonSerializer.SerializeToNode(dto, Web)!.AsObject();
        json.Remove("agentNames");
        Assert.Empty(json.Deserialize<SessionSummaryDto>(Web)!.AgentNames);
    }

    [Fact]
    public void TheDashboardReadsTheListTheCollectorWrites()
    {
        // The dashboard mirrors the DTO rather than sharing an assembly: the JSON is the contract.
        var dto = Dto.Summary(With("mirror", T0, "copilot", "explore"), new QualityEngine());
        var json = JsonSerializer.SerializeToNode(dto, Web)!.AsObject();
        Assert.Equal(["copilot", "explore"],
            json.Deserialize<CopilotScope.Dashboard.Services.SessionSummaryDto>(Web)!.AgentNames);

        json.Remove("agentNames");
        Assert.Empty(json.Deserialize<CopilotScope.Dashboard.Services.SessionSummaryDto>(Web)!.AgentNames);
    }

    [Fact]
    public async Task TheSessionListAndDetailServeEveryAgentName()
    {
        using var collector = new WebApplicationFactory<SessionSummaryDto>();
        var client = collector.CreateClient();
        var now = DateTimeOffset.UtcNow;

        var payload = TestOtlp.TracesRequest("window-agents",
            InvokeAgent(61, "copilot", "conv-http-agents", now),
            InvokeAgent(62, "explore", "conv-http-agents", now.AddSeconds(1)),
            InvokeAgent(63, "copilot", "conv-http-agents", now.AddSeconds(2)));
        var body = new ByteArrayContent(payload) { Headers = { ContentType = new("application/x-protobuf") } };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/traces", body)).StatusCode);

        var page = await client.GetFromJsonAsync<JsonElement>("/api/sessions");
        var row = page.GetProperty("sessions").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "conv-http-agents");
        Assert.Equal("copilot", row.GetProperty("agent").GetString());
        Assert.Equal(["copilot", "explore"], row.GetProperty("agentNames").EnumerateArray().Select(e => e.GetString()!));

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/sessions/conv-http-agents");
        var summary = detail.GetProperty("summary");
        Assert.Equal("copilot", summary.GetProperty("agent").GetString());
        Assert.Equal(["copilot", "explore"], summary.GetProperty("agentNames").EnumerateArray().Select(e => e.GetString()!));
    }
}
