using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Cross-machine attribution. Identity-less metrics and logs resolve to a conversation
/// through a resource fingerprint; the process- and service-scoped forms of that
/// fingerprint are only unique within one machine, so behind a shared team collector
/// they must be scoped by host or two developers' telemetry merges into one session.
/// </summary>
public class AttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An invoke_agent root naming its conversation — this is what registers a fingerprint.</summary>
    private static OtlpBatch Conversation(string conversationId, Dictionary<string, AttrValue> resource, int traceSeed)
    {
        var batch = new OtlpBatch();
        batch.Spans.Add(new OtlpSpan
        {
            TraceId = $"trace-{traceSeed}",
            SpanId = $"span-{traceSeed}",
            Name = "invoke_agent copilot",
            Start = T0,
            End = T0.AddSeconds(2),
            Attributes = new()
            {
                ["gen_ai.operation.name"] = AttrValue.Str("invoke_agent"),
                ["gen_ai.conversation.id"] = AttrValue.Str(conversationId)
            },
            Resource = resource
        });
        return batch;
    }

    /// <summary>An identity-less token metric — no conversation id, resolves only by fingerprint.</summary>
    private static OtlpBatch IdentitylessTokens(Dictionary<string, AttrValue> resource, long inputTokens)
    {
        var batch = new OtlpBatch();
        batch.Metrics.Add(new OtlpMetricPoint
        {
            MetricName = "copilot_chat.lines_of_code.count",
            Kind = MetricKind.Sum,
            Time = T0.AddSeconds(3),
            Value = inputTokens,
            Count = 1,
            Attributes = new() { ["copilot_chat.lines.kind"] = AttrValue.Str("added") },
            Resource = resource
        });
        return batch;
    }

    private static Dictionary<string, AttrValue> CliResource(string? hostName = null, long pid = 4242) =>
        hostName is null
            ? new() { ["service.name"] = AttrValue.Str("copilot-cli"), ["process.pid"] = AttrValue.Int(pid) }
            : new()
            {
                ["service.name"] = AttrValue.Str("copilot-cli"),
                ["process.pid"] = AttrValue.Int(pid),
                ["host.name"] = AttrValue.Str(hostName)
            };

    [Fact]
    public void SameServiceNameOnDifferentHostsDoesNotMerge()
    {
        var store = new SessionStore();

        // Two developers, identical service.name and even the same pid, different machines.
        store.Ingest(Conversation("conv-alice", CliResource("alice-laptop"), 1));
        store.Ingest(Conversation("conv-bob", CliResource("bob-laptop"), 2));

        // Alice's identity-less metric must land on Alice's conversation, not Bob's —
        // even though Bob's conversation registered more recently ("last active wins").
        store.Ingest(IdentitylessTokens(CliResource("alice-laptop"), 40));

        Assert.Equal(40, store.Get("conv-alice")!.LinesAdded);
        Assert.Equal(0, store.Get("conv-bob")!.LinesAdded);
    }

    [Fact]
    public void SameHostStillAttributesIdentitylessSignals()
    {
        // The fix must not break the single-machine case it exists to serve: one developer's
        // CLI metrics still fold into the conversation from the same process.
        var store = new SessionStore();
        store.Ingest(Conversation("conv-solo", CliResource("dev-box"), 3));
        store.Ingest(IdentitylessTokens(CliResource("dev-box"), 12));

        Assert.Equal(12, store.Get("conv-solo")!.LinesAdded);
        Assert.Single(store.All);
    }

    [Fact]
    public void DifferentSourceConnectionsDoNotMergeWhenHostAttributesAreAbsent()
    {
        // Emitters without host.name are the common case (Copilot CLI ships a bare resource).
        // The connection the batch arrived on is then the only discriminator available.
        var store = new SessionStore();

        store.Ingest(Conversation("conv-one", CliResource(), 4), sourceId: "10.0.0.1");
        store.Ingest(Conversation("conv-two", CliResource(), 5), sourceId: "10.0.0.2");
        store.Ingest(IdentitylessTokens(CliResource(), 25), sourceId: "10.0.0.1");

        Assert.Equal(25, store.Get("conv-one")!.LinesAdded);
        Assert.Equal(0, store.Get("conv-two")!.LinesAdded);
        Assert.Equal(0, store.HostlessSignals); // a source id counts as a discriminator
    }

    [Fact]
    public void HostAttributeWinsOverSourceConnection()
    {
        // One machine may open many connections (or sit behind a changing NAT address);
        // host.name keeps its signals together where the remote address would not.
        var store = new SessionStore();
        store.Ingest(Conversation("conv-stable", CliResource("build-agent"), 6), sourceId: "10.0.0.1");
        store.Ingest(IdentitylessTokens(CliResource("build-agent"), 9), sourceId: "10.0.0.99");

        Assert.Equal(9, store.Get("conv-stable")!.LinesAdded);
    }

    [Fact]
    public void SignalsWithNoHostDiscriminatorAreCounted()
    {
        // Neither host attributes nor a source connection: behavior is unchanged (they share
        // one scope) but the counter tells an operator the deployment needs emitter config.
        var store = new SessionStore();
        store.Ingest(Conversation("conv-blind", CliResource(), 7));

        Assert.True(store.HostlessSignals > 0);
    }

    [Fact]
    public void SessionScopedFingerprintsNeedNoHostScope()
    {
        // A VS Code window id names one conversation on one machine already, so it must keep
        // working across connections — the host scoping applies only to the weak forms.
        var store = new SessionStore();
        var vsCode = new Dictionary<string, AttrValue> { ["session.id"] = AttrValue.Str("window-7") };

        store.Ingest(Conversation("conv-vscode", vsCode, 8), sourceId: "10.0.0.1");
        store.Ingest(IdentitylessTokens(vsCode, 31), sourceId: "10.0.0.2");

        Assert.Equal(31, store.Get("conv-vscode")!.LinesAdded);
        Assert.Equal(0, store.HostlessSignals);
    }

    [Fact]
    public void UnfingerprintableBucketIsNotClaimedByAnUnrelatedConversation()
    {
        // A signal whose emitter cannot be fingerprinted at all lands in the plain
        // "unattributed" bucket. Merging that into whichever conversation registers next
        // is exactly the cross-attribution this scoping prevents, so it must stay orphaned.
        var store = new SessionStore();
        var bare = new Dictionary<string, AttrValue>(); // no service.name, no host, nothing

        store.Ingest(IdentitylessTokens(bare, 50));
        Assert.Equal(50, store.Get("unattributed")!.LinesAdded);

        store.Ingest(Conversation("conv-unrelated", CliResource("some-host"), 9));

        Assert.Equal(50, store.Get("unattributed")!.LinesAdded); // still orphaned
        Assert.Equal(0, store.Get("conv-unrelated")!.LinesAdded);
    }
}
