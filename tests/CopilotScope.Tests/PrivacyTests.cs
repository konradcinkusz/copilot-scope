using System.Net;
using System.Net.Http.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;
using CopilotScope.Collector.Persistence;
using CopilotScope.Collector.Quality;
using CopilotScope.Collector.Privacy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Privacy mode — the works-council/GDPR controls.
///
/// The point of these tests is that the controls are *enforced*, not configured: a promise in
/// a README is not something a works council can agree to, and a Betriebsvereinbarung annex
/// is only worth signing if the software actually behaves the way the annex says. So each
/// test drives the real ingest and read path and asserts on what the system holds and serves,
/// not on what the options object says it intends.
/// </summary>
public sealed class PrivacyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static PrivacyOptions On(Action<PrivacyOptions>? tweak = null)
    {
        var o = new PrivacyOptions { Enabled = true, Salt = "test-salt" };
        tweak?.Invoke(o);
        return o;
    }

    private static PrivacyRedactor Redactor(PrivacyOptions options) =>
        new(options, new Pseudonymizer(options.Salt));

    private static OtlpSpan ChatSpan(Dictionary<string, AttrValue> resource,
        Dictionary<string, AttrValue>? attributes = null) => new()
    {
        TraceId = "trace-1",
        SpanId = "span-1",
        Name = "chat",
        Start = T0,
        End = T0.AddMilliseconds(500),
        Resource = resource,
        Attributes = attributes ?? new Dictionary<string, AttrValue>
        {
            [Sem.Operation] = AttrValue.Str("chat"),
            [Sem.RequestModel] = AttrValue.Str("gpt-5"),
        },
    };

    private static Dictionary<string, AttrValue> Resource(string host, string? user = null)
    {
        var r = new Dictionary<string, AttrValue>
        {
            [Sem.ServiceName] = AttrValue.Str("copilot-chat"),
            ["host.name"] = AttrValue.Str(host),
        };
        if (user is not null) r["user.email"] = AttrValue.Str(user);
        return r;
    }

    // ------------------------------------------------------------- pseudonymization

    [Fact]
    public void IdentifyingAttributesAreReplacedByStableTokens()
    {
        var redactor = Redactor(On());
        var batch = new OtlpBatch();
        batch.Spans.Add(ChatSpan(Resource("konrad-macbook", "konrad@example.com")));
        batch.Spans.Add(ChatSpan(Resource("konrad-macbook", "konrad@example.com")));

        redactor.Apply(batch);

        var first = batch.Spans[0].Resource;
        var second = batch.Spans[1].Resource;

        Assert.DoesNotContain("konrad", first["host.name"].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("konrad", first["user.email"].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("host-", first["host.name"].ToString(), StringComparison.Ordinal);
        Assert.StartsWith("user-", first["user.email"].ToString(), StringComparison.Ordinal);

        // Equality is the one property that must survive: it is what lets two signals from one
        // machine still resolve to one session and one subject.
        Assert.Equal(first["host.name"].ToString(), second["host.name"].ToString());
        // Non-identifying attributes are untouched — redaction is not a blanket scrub.
        Assert.Equal("copilot-chat", first[Sem.ServiceName].ToString());
    }

    [Fact]
    public void DifferentHostsGetDifferentTokens()
    {
        var redactor = Redactor(On());
        var batch = new OtlpBatch();
        batch.Spans.Add(ChatSpan(Resource("laptop-a")));
        batch.Spans.Add(ChatSpan(Resource("laptop-b")));

        redactor.Apply(batch);

        Assert.NotEqual(batch.Spans[0].Resource["host.name"].ToString(),
                        batch.Spans[1].Resource["host.name"].ToString());
    }

    [Fact]
    public void TheSaltIsWhatMakesTheTokenNonReversible()
    {
        // Without a secret salt the identifier space here — hostnames and work email addresses
        // at one company — is small enough to invert with a wordlist, which is not
        // pseudonymization in the sense Art. 4(5) means. Two salts must not agree.
        var a = new Pseudonymizer("salt-a").Token("host", "konrad-macbook");
        var b = new Pseudonymizer("salt-b").Token("host", "konrad-macbook");
        Assert.NotEqual(a, b);
        Assert.Equal(a, new Pseudonymizer("salt-a").Token("host", "konrad-macbook"));
    }

    [Fact]
    public void AnUnconfiguredSaltIsReportedAsEphemeral()
    {
        Assert.True(new Pseudonymizer("").SaltIsEphemeral);
        Assert.True(new Pseudonymizer(null).SaltIsEphemeral);
        Assert.False(new Pseudonymizer("configured").SaltIsEphemeral);
    }

    [Fact]
    public void PrivacyModeOffLeavesTheBatchExactlyAsItArrived()
    {
        var redactor = Redactor(new PrivacyOptions { Enabled = false, Salt = "test-salt" });
        var batch = new OtlpBatch();
        batch.Spans.Add(ChatSpan(Resource("konrad-macbook", "konrad@example.com")));

        redactor.Apply(batch);

        Assert.Equal("konrad-macbook", batch.Spans[0].Resource["host.name"].ToString());
        Assert.Equal(0, redactor.AttributesPseudonymized);
    }

    [Fact]
    public void BranchIsKeptByDefaultAndPseudonymizedOnRequest()
    {
        // Branch names carry authorship ("konrad/fix-login") but are also how sessions link to
        // pull-request outcomes, so this is the operator's trade-off to make, not ours.
        var attributes = new Dictionary<string, AttrValue>
        {
            [Sem.Operation] = AttrValue.Str("chat"),
            [Sem.GitBranch] = AttrValue.Str("konrad/fix-login"),
        };

        var kept = new OtlpBatch();
        kept.Spans.Add(ChatSpan(Resource("h1"), new Dictionary<string, AttrValue>(attributes)));
        Redactor(On()).Apply(kept);
        Assert.Equal("konrad/fix-login", kept.Spans[0].Attributes[Sem.GitBranch].ToString());

        var hashed = new OtlpBatch();
        hashed.Spans.Add(ChatSpan(Resource("h1"), new Dictionary<string, AttrValue>(attributes)));
        Redactor(On(o => o.PseudonymizeBranch = true)).Apply(hashed);
        Assert.StartsWith("branch-", hashed.Spans[0].Attributes[Sem.GitBranch].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraIdentifyingAttributesFromConfigurationArePseudonymizedToo()
    {
        var options = On(o => o.ExtraIdentifyingAttributes = ["acme.employee_number"]);
        var batch = new OtlpBatch();
        var resource = Resource("h1");
        resource["acme.employee_number"] = AttrValue.Str("E-4471");
        batch.Spans.Add(ChatSpan(resource));

        Redactor(options).Apply(batch);

        Assert.DoesNotContain("E-4471", batch.Spans[0].Resource["acme.employee_number"].ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- content

    [Fact]
    public void PromptAndResponseContentIsDroppedNotTokenized()
    {
        var redactor = Redactor(On());
        var batch = new OtlpBatch();
        batch.Spans.Add(ChatSpan(Resource("h1"), new Dictionary<string, AttrValue>
        {
            [Sem.Operation] = AttrValue.Str("chat"),
            [Sem.InputMessages] = AttrValue.Str("here is our API key: sk-live-123"),
            [Sem.OutputMessages] = AttrValue.Str("sure, here is the patch"),
        }));

        redactor.Apply(batch);

        Assert.False(batch.Spans[0].Attributes.ContainsKey(Sem.InputMessages));
        Assert.False(batch.Spans[0].Attributes.ContainsKey(Sem.OutputMessages));
        Assert.Equal(2, redactor.ContentDropped);
    }

    [Fact]
    public void ClaudeCodePromptEventsLoseTheirText()
    {
        var redactor = Redactor(On());
        var batch = new OtlpBatch();
        batch.Logs.Add(new OtlpLogEvent
        {
            EventName = "claude_code.user_prompt",
            Time = T0,
            Resource = Resource("h1"),
            Attributes = new Dictionary<string, AttrValue>
            {
                ["prompt"] = AttrValue.Str("refactor the billing module"),
                ["prompt_length"] = AttrValue.Int(27),
            },
        });

        redactor.Apply(batch);

        Assert.False(batch.Logs[0].Attributes.ContainsKey("prompt"));
        // The length survives: it is a metric, not content, and the turn analysis uses it.
        Assert.True(batch.Logs[0].Attributes.ContainsKey("prompt_length"));
    }

    [Fact]
    public void ContentCarriedInALogBodyIsDroppedToo()
    {
        // The GenAI content events put the text in the body rather than an attribute, and
        // SessionStore reads it from there — so scrubbing attributes alone would leave the
        // transcript path wide open for exactly the emitters that use bodies.
        var redactor = Redactor(On());
        var batch = new OtlpBatch();
        batch.Logs.Add(new OtlpLogEvent
        {
            EventName = "gen_ai.content.prompt",
            Time = T0,
            Body = "the whole conversation",
            Resource = Resource("h1"),
        });

        redactor.Apply(batch);

        Assert.Null(batch.Logs[0].Body);
    }

    [Fact]
    public void NoTranscriptSurvivesIngestUnderPrivacyMode()
    {
        // End to end: the control is only real if the aggregate the dashboard renders, and the
        // snapshot the database stores, hold nothing.
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Spans.Add(ChatSpan(Resource("h1"), new Dictionary<string, AttrValue>
        {
            [Sem.Operation] = AttrValue.Str("chat"),
            [Sem.ConversationId] = AttrValue.Str("conv-1"),
            [Sem.RequestModel] = AttrValue.Str("gpt-5"),
            [Sem.Prompt] = AttrValue.Str("secret business logic"),
            [Sem.Completion] = AttrValue.Str("here you go"),
        }));

        Redactor(On()).Apply(batch);
        store.Ingest(batch, "10.0.0.1");

        var session = Assert.Single(store.All);
        Assert.Empty(session.Transcript);
        Assert.Empty(PersistedSession.From(session).Transcript);
    }

    [Fact]
    public void RedactionDoesNotBreakSessionCorrelation()
    {
        // Tokens preserve equality, so two batches from one machine must still land in one
        // session. If they did not, privacy mode would silently multiply every developer into
        // a crowd — and quietly satisfy the k-anonymity floor by fabricating subjects.
        var store = new SessionStore();
        var redactor = Redactor(On());

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var batch = new OtlpBatch();
            batch.Spans.Add(ChatSpan(Resource("konrad-macbook"), new Dictionary<string, AttrValue>
            {
                [Sem.Operation] = AttrValue.Str("chat"),
                [Sem.ConversationId] = AttrValue.Str("conv-1"),
            }));
            redactor.Apply(batch);
            store.Ingest(batch, "10.0.0.1");
        }

        var session = Assert.Single(store.All);
        Assert.Equal(2, session.ChatCalls);
    }

    // --------------------------------------------------------------- subject scope

    [Fact]
    public void SessionsCarryAPseudonymousSubjectDerivedFromTheirOrigin()
    {
        var store = new SessionStore();
        var batch = new OtlpBatch();
        batch.Spans.Add(ChatSpan(Resource("konrad-macbook"), new Dictionary<string, AttrValue>
        {
            [Sem.Operation] = AttrValue.Str("chat"),
            [Sem.ConversationId] = AttrValue.Str("conv-1"),
        }));

        Redactor(On()).Apply(batch);
        store.Ingest(batch, "10.0.0.1");

        var subject = Assert.Single(store.All).SubjectId;
        Assert.NotNull(subject);
        Assert.DoesNotContain("konrad", subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANamedUserOutranksTheMachineAsTheSubject()
    {
        // One developer on two machines is one person, and one shared machine used by two
        // people is two — so where the emitter names a user, that is the subject.
        var store = new SessionStore();
        foreach (var (host, conv) in new[] { ("laptop", "conv-1"), ("desktop", "conv-2") })
        {
            var batch = new OtlpBatch();
            batch.Spans.Add(ChatSpan(Resource(host, "konrad@example.com"), new Dictionary<string, AttrValue>
            {
                [Sem.Operation] = AttrValue.Str("chat"),
                [Sem.ConversationId] = AttrValue.Str(conv),
            }));
            Redactor(On()).Apply(batch);
            store.Ingest(batch, "10.0.0.1");
        }

        Assert.Equal(2, store.All.Count);
        Assert.Single(store.All.Select(s => s.SubjectId).Distinct());
    }

    [Fact]
    public void SubjectSurvivesTheSnapshotRoundTrip()
    {
        var session = new CopilotSession { Id = "s1", SubjectId = "host-abc123", FirstSeen = T0, LastSeen = T0 };
        Assert.Equal("host-abc123", PersistedSession.From(session).ToSession().SubjectId);
    }

    // ------------------------------------------------------------- aggregation floor

    private static CopilotSession WithSubject(string id, string? subject) =>
        new() { Id = id, SubjectId = subject, FirstSeen = T0, LastSeen = T0 };

    [Fact]
    public void AViewCoveringFewerThanKSubjectsIsWithheld()
    {
        var guard = new PrivacyGuard(On(o => o.MinimumGroupSize = 5));
        var sessions = Enumerable.Range(0, 4).Select(i => WithSubject($"s{i}", $"host-{i}")).ToList();

        var verdict = guard.Evaluate(sessions);

        Assert.False(verdict.Allowed);
        Assert.Equal(4, verdict.Subjects);
        Assert.Equal(5, verdict.Required);
        Assert.Contains("k-anonymity", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AViewCoveringKSubjectsIsServed()
    {
        var guard = new PrivacyGuard(On(o => o.MinimumGroupSize = 5));
        var sessions = Enumerable.Range(0, 5).Select(i => WithSubject($"s{i}", $"host-{i}")).ToList();

        Assert.True(guard.Evaluate(sessions).Allowed);
    }

    [Fact]
    public void TheFloorCountsSubjectsNotSessions()
    {
        // Fifty sessions from one developer are one person's week. Counting sessions would
        // make the busiest individual the easiest one to report on, which is the exact
        // inversion the floor exists to prevent.
        var guard = new PrivacyGuard(On(o => o.MinimumGroupSize = 5));
        var sessions = Enumerable.Range(0, 50).Select(i => WithSubject($"s{i}", "host-one")).ToList();

        var verdict = guard.Evaluate(sessions);

        Assert.False(verdict.Allowed);
        Assert.Equal(1, verdict.Subjects);
    }

    [Fact]
    public void SessionsWithNoKnownSubjectEachCountAsOne()
    {
        // An unknown origin is not evidence that the set is diverse, but collapsing them all
        // into a single "unknown" bucket would suppress genuinely broad views on a deployment
        // whose emitters simply do not send host attributes. Count honestly in both directions.
        var guard = new PrivacyGuard(On(o => o.MinimumGroupSize = 3));
        var sessions = Enumerable.Range(0, 3).Select(i => WithSubject($"s{i}", null)).ToList();

        Assert.True(guard.Evaluate(sessions).Allowed);
    }

    [Fact]
    public void TheFloorDoesNotApplyWhenPrivacyModeIsOff()
    {
        var guard = new PrivacyGuard(new PrivacyOptions { Enabled = false, MinimumGroupSize = 100 });
        Assert.True(guard.Evaluate([WithSubject("s1", "host-1")]).Allowed);
        Assert.False(guard.SessionDetailSuppressed);
    }

    // ------------------------------------------------------------------ audit log

    [Fact]
    public void TheAuditLogRecordsReadsWhenPrivacyModeIsOn()
    {
        var log = new AccessAuditLog(On());
        log.Record("viewer", "sessions.list", "days=7", "served 12 session(s)");

        var entry = Assert.Single(log.Recent());
        Assert.Equal("viewer", entry.Actor);
        Assert.Equal("sessions.list", entry.Action);
        Assert.Equal(1, log.Recorded);
    }

    [Fact]
    public void TheAuditLogIsInertWhenPrivacyModeIsOff()
    {
        var log = new AccessAuditLog(new PrivacyOptions { Enabled = false });
        log.Record("viewer", "sessions.list", null, "served");

        Assert.False(log.Enabled);
        Assert.Empty(log.Recent());
    }

    [Fact]
    public void TheInMemoryTailIsBounded()
    {
        // It sits on the read path of a long-lived process; an unbounded log is a leak that
        // only shows up on the deployments that use the feature most.
        var log = new AccessAuditLog(On(o => o.AuditBufferSize = 100));
        for (var i = 0; i < 500; i++) log.Record("viewer", "sessions.list", $"q{i}", "served");

        Assert.Equal(100, log.Recent(100).Count);
        Assert.Equal(500, log.Recorded);
    }

    [Fact]
    public void UndeliveredEntriesAreRequeuedRatherThanLost()
    {
        // A transient Postgres error must not silently shorten the record: nothing in the
        // export would say it is incomplete.
        var log = new AccessAuditLog(On());
        log.Record("viewer", "sessions.list", null, "served");

        var drained = log.DrainPending();
        Assert.Single(drained);
        Assert.Empty(log.DrainPending());

        log.Requeue(drained);
        Assert.Single(log.DrainPending());
    }

    [Fact]
    public void TheActorIsTheForwardedUserWhenTheDashboardNamesOne()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["x-api-key"] = "super-secret-key";
        ctx.Request.Headers["X-CopilotScope-Actor"] = "admin";

        Assert.Equal("admin", AccessAuditLog.ActorFor(ctx.Request));
    }

    [Fact]
    public void TheActorFallsBackToACredentialFingerprintNeverTheCredential()
    {
        // An audit log full of live API keys is a breach with a retention policy.
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["x-api-key"] = "super-secret-key";

        var actor = AccessAuditLog.ActorFor(ctx.Request);

        Assert.StartsWith("key:", actor, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-key", actor, StringComparison.Ordinal);
    }

    [Fact]
    public void AForgedActorCannotBreakTheCsvExport()
    {
        // The header is attacker-controlled text that lands in a CSV cell; a comma or a quote
        // in it must not shift every following column.
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-CopilotScope-Actor"] = "evil,\"name\nnewline";

        var actor = AccessAuditLog.ActorFor(ctx.Request);
        Assert.DoesNotContain(",", actor, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", actor, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", actor, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCsvExportQuotesFieldsThatNeedIt()
    {
        var csv = AccessAuditLog.ToCsv([
            new AccessAuditEntry(T0, "admin", "sessions.list", "days=7, limit=50", "served")
        ]);

        Assert.StartsWith("timestamp,actor,action,target,outcome\n", csv, StringComparison.Ordinal);
        Assert.Contains("\"days=7, limit=50\"", csv, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ /metrics

    [Fact]
    public void PerSessionPrometheusSeriesAreRefusedUnderPrivacyMode()
    {
        // Prometheus is not a less sensitive surface for being a different port: a series
        // labelled by session id, carrying that session's quality score, is exactly the
        // individual-level view the API's aggregation floor withholds.
        var store = new SessionStore();
        store.Put(new CopilotSession
        {
            Id = "conv-1", SubjectId = "host-abc", FirstSeen = T0, LastSeen = T0,
            ChatCalls = 4, InputTokens = 1000,
        });
        var promOptions = new PrometheusOptions { PerSession = true };

        var open = new PrometheusExporter(store, new QualityEngine(), new PricingOptions(), promOptions)
            .Render();
        Assert.Contains("session=\"conv-1\"", open, StringComparison.Ordinal);

        var guarded = new PrometheusExporter(store, new QualityEngine(), new PricingOptions(),
            promOptions, new PrivacyGuard(On())).Render();
        Assert.DoesNotContain("session=\"conv-1\"", guarded, StringComparison.Ordinal);
        // The aggregates are still exported — the floor removes the per-individual view, not
        // the team-level signal the whole exporter exists for.
        Assert.Contains("copilotscope_quality", guarded, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- the API

    private static WebApplicationFactory<SessionSummaryDto> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    [Fact]
    public async Task PrivacyModeOffKeepsTheApiExactlyAsItWas()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/privacy");
        Assert.NotNull(report);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/sessions/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task PerSessionDetailIsRefusedUnderPrivacyMode()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"));
        using var client = factory.CreateClient();

        // 403 rather than 404: whether the session exists is itself information about one
        // person, so the refusal has to come before the lookup.
        var response = await client.GetAsync("/api/sessions/anything");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DetailStaysAvailableWhenTheAgreementPermitsIndividualReview()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:SuppressSessionDetail", "false"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sessions/anything")).StatusCode);
    }

    [Fact]
    public async Task TheEmptySessionListIsWithheldRatherThanServedUnderTheFloor()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "5"));
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<SessionPage>("/api/sessions");

        Assert.NotNull(page);
        Assert.NotNull(page.SuppressedReason);
        Assert.Empty(page.Sessions);
    }

    [Fact]
    public async Task TheOverviewIsWithheldUnderTheFloor()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "5"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/overview")).StatusCode);
    }

    [Fact]
    public async Task TheAuditLogIsAdminOnly()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Keys:Read:0", "read-key"),
            ("CopilotScope:Keys:Admin:0", "admin-key"));
        using var client = factory.CreateClient();

        // Who looked at what is exactly the record that should not be readable by everyone
        // it describes.
        var read = new HttpRequestMessage(HttpMethod.Get, "/api/audit");
        read.Headers.Add("x-api-key", "read-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(read)).StatusCode);

        var admin = new HttpRequestMessage(HttpMethod.Get, "/api/audit?format=csv");
        admin.Headers.Add("x-api-key", "admin-key");
        var response = await client.SendAsync(admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("timestamp,actor,action,target,outcome",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePrivacyReportDescribesWhatIsEnforced()
    {
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "7"));
        using var client = factory.CreateClient();

        using var doc = System.Text.Json.JsonDocument.Parse(
            await client.GetStringAsync("/api/privacy"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, root.GetProperty("minimumGroupSize").GetInt32());
        Assert.True(root.GetProperty("sessionDetailSuppressed").GetBoolean());
        Assert.False(root.GetProperty("transcriptsRetained").GetBoolean());
        Assert.True(root.GetProperty("auditLog").GetBoolean());
    }
}
