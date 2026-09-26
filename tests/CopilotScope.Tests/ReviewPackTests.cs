using System.Text;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using CopilotScope.Collector.Review;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The review pack — the deterministic half of a session review (docs/REVIEW.md).
///
/// Three properties carry the feature, and each is asserted rather than assumed: the pack is a
/// pure function of the base (the same sessions in any order give byte-identical JSON), nothing
/// individual leaves in it (no subject, no branch, no rater, no prompt text — the aggregate tier
/// not even a session id), and every pattern states a count a reader can check against the
/// sessions it cites.
/// </summary>
public sealed class ReviewPackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly QualityEngine Quality = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CopilotSession Session(string id, EmitterKind emitter = EmitterKind.VSCode,
        string origin = SessionOrigin.Otel, string model = "gpt-5", string? repo = "acme/api",
        string? subject = "host-a", int chatCalls = 4, int chatErrors = 0, DateTimeOffset? lastSeen = null,
        Action<CopilotSession>? tweak = null)
    {
        var s = new CopilotSession
        {
            Id = id,
            Repository = repo,
            EmitterKind = emitter,
            Origin = origin,
            SubjectId = subject,
            FirstSeen = (lastSeen ?? T0).AddMinutes(-30),
            LastSeen = lastSeen ?? T0,
            ChatCalls = chatCalls,
            ChatErrors = chatErrors,
            InputTokens = 1000,
            OutputTokens = 500,
            Turns = 3,
        };
        s.ModelCalls[model] = chatCalls;
        tweak?.Invoke(s);
        return s;
    }

    private static void Tool(CopilotSession s, string name, int calls, int errors) =>
        s.Tools[name] = (calls, errors, 100.0 * calls);

    /// <summary>Three turns: two ordinary ones and one that retries its way through failures.</summary>
    private static void RepairLoop(CopilotSession s)
    {
        Turn(s, "t1", 1, 1, 0);
        Turn(s, "t2", 1, 1, 0);
        Turn(s, "t3", 1, 8, 2);
    }

    private static void Turn(CopilotSession s, string trace, int chat, int tools, int toolErrors, double ttft = 0)
    {
        var t = s.TurnFor(trace, T0.AddMinutes(-30 + s.TurnList.Count));
        t.End = t.Start.AddSeconds(20);
        t.ChatCalls = chat;
        t.ToolCalls = tools;
        t.ToolErrors = toolErrors;
        if (ttft > 0) { t.TtftTotalMs = ttft; t.TtftCount = 1; }
    }

    private static ReviewPackInput Input(ReviewTier tier = ReviewTier.Sessions, ReviewOptions? options = null) =>
        new(T0.AddDays(-30), T0, T0.AddDays(-60), tier, options ?? new ReviewOptions(), "test");

    private static ReviewPackReport Build(IEnumerable<CopilotSession> current, IEnumerable<CopilotSession>? baseline = null,
        ReviewTier tier = ReviewTier.Sessions, ReviewOptions? options = null) =>
        ReviewPack.Build(current.ToList(), (baseline ?? []).ToList(), Quality, Input(tier, options));

    private static string Serialize(ReviewPackReport pack) => JsonSerializer.Serialize(pack, Json);

    /// <summary>A varied base: three strata, some errors, some loops, deterministic from the seed.</summary>
    private static List<CopilotSession> Varied(int count, int seed = 7)
    {
        var random = new Random(seed);
        var list = new List<CopilotSession>();
        for (var i = 0; i < count; i++)
        {
            var kind = i % 3;
            var s = Session($"sess-{seed}-{i:0000}",
                emitter: kind == 0 ? EmitterKind.VSCode : kind == 1 ? EmitterKind.ClaudeCode : EmitterKind.CLI,
                origin: kind == 1 ? SessionOrigin.LogImport : SessionOrigin.Otel,
                model: kind == 0 ? "gpt-5" : kind == 1 ? "claude-sonnet-5" : random.Next(2) == 0 ? "gpt-5" : "gpt-5-mini",
                repo: $"acme/{(char)('a' + random.Next(4))}",
                subject: $"host-{random.Next(6)}",
                chatCalls: 2 + random.Next(8),
                chatErrors: random.Next(5) == 0 ? 1 : 0,
                lastSeen: T0.AddHours(-random.Next(24 * 29)));
            Tool(s, "Read", 5 + random.Next(20), 0);
            Tool(s, "Bash", 3 + random.Next(10), random.Next(3) == 0 ? 2 : 0);
            if (random.Next(4) == 0) s.ErrorTypes["rate_limit"] = 1;
            if (random.Next(3) == 0) RepairLoop(s); else { Turn(s, "a", 1, 1, 0); Turn(s, "b", 1, 2, 0); }
            for (var e = 0; e < 5; e++) s.AddEvent(new SessionEvent(s.LastSeen.AddSeconds(-e), "execute_tool", $"Read · {40 + e} ms"));
            list.Add(s);
        }
        return list;
    }

    // ------------------------------------------------------------------ purity

    [Fact]
    public void TheSamePopulationInAnyOrderGivesTheSamePack()
    {
        var sessions = Varied(40);
        var forwards = Serialize(Build(sessions, Varied(20, seed: 3)));

        var shuffled = sessions.OrderBy(_ => Guid.NewGuid()).ToList();
        var backwards = Serialize(Build(shuffled, Varied(20, seed: 3).AsEnumerable().Reverse()));

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void TheFingerprintNamesThePopulationAndTheTier()
    {
        var sessions = Varied(12);
        var a = Build(sessions).Scope.Fingerprint;
        var b = Build(sessions.AsEnumerable().Reverse()).Scope.Fingerprint;
        var other = Build(Varied(12, seed: 9)).Scope.Fingerprint;
        var aggregate = Build(sessions, tier: ReviewTier.Aggregate).Scope.Fingerprint;

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.NotEqual(a, aggregate);
        Assert.Equal(16, a.Length);
    }

    // ----------------------------------------------------------------- privacy

    [Fact]
    public void NothingIndividualLeavesInThePack()
    {
        // Every field the pack must not carry, set to a value that would be easy to spot.
        var sessions = Varied(12).Select(s =>
        {
            s.Branch = "konrad/fix-login";
            s.AgentName = "Konrad";
            s.AddAgentName("Konrad-subagent");
            s.SubjectId = "host-of-konrad";
            s.AddTranscript(s.LastSeen, "gpt-5", "SECRET-PROMPT-TEXT", "SECRET-RESPONSE-TEXT", 0);
            return s;
        }).ToList();

        var pack = Build(sessions);
        var json = Serialize(pack);
        var markdown = ReviewPackMarkdown.Render(pack);

        foreach (var leak in new[] { "konrad/fix-login", "Konrad", "host-of-konrad", "SECRET-PROMPT-TEXT", "SECRET-RESPONSE-TEXT" })
        {
            Assert.DoesNotContain(leak, json, StringComparison.Ordinal);
            Assert.DoesNotContain(leak, markdown, StringComparison.Ordinal);
        }

        // The keys, not just the values: a subject count is a k-anonymity input, not a report figure.
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "subjectId", "subjects", "branch", "agent", "agentName", "agentNames", "author", "rater", "user", "developer", "email", "host", "transcript" };
        using var doc = JsonDocument.Parse(json);
        var keys = new List<string>();
        Walk(doc.RootElement, keys);
        Assert.DoesNotContain(keys, forbidden.Contains);
        Assert.NotEmpty(keys);
    }

    private static void Walk(JsonElement element, List<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject()) { keys.Add(p.Name); Walk(p.Value, keys); }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, keys);
                break;
        }
    }

    [Fact]
    public void TheAggregateTierCarriesNoSessionIdsAndNoExemplars()
    {
        var sessions = Varied(30);
        var pack = Build(sessions, tier: ReviewTier.Aggregate);

        Assert.Null(pack.Sessions);
        Assert.Empty(pack.Exemplars);
        Assert.NotEmpty(pack.Patterns);
        Assert.All(pack.Patterns, p => Assert.Empty(p.EvidenceSessionIds));

        var json = Serialize(pack);
        foreach (var s in sessions) Assert.DoesNotContain(s.Id, json, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalSeededAndEmptySessionsAreExcludedAndCounted()
    {
        var sessions = Varied(6);
        sessions.Add(Session("seed-demo-1"));
        sessions.Add(Session("empty-1", chatCalls: 0));
        sessions.Add(Session("helper-1", tweak: s =>
            s.AddTranscript(T0, "gpt-5", "Please write a brief title for the following request: fix it", "Fix", 0)));

        var pack = Build(sessions);

        Assert.Equal(6, pack.Scope.Sessions);
        Assert.Equal(1, pack.Provenance.SyntheticExcluded);
        Assert.Equal(1, pack.Provenance.EmptyExcluded);
        Assert.Equal(1, pack.Provenance.InternalExcluded);
        Assert.DoesNotContain(pack.Sessions!, r => r.Id is "seed-demo-1" or "empty-1" or "helper-1");
    }

    // ---------------------------------------------------------------- patterns

    [Fact]
    public void AToolThatFailsFarMoreThanTheOthersIsAPatternWithItsSessionsAsEvidence()
    {
        var sessions = new List<CopilotSession>();
        for (var i = 0; i < 12; i++)
        {
            var s = Session($"s{i:00}");
            Tool(s, "Read", 10, 0);
            Tool(s, "Bash", 5, i < 4 ? 3 : 0);
            // Too few calls across the stratum to say anything, however often it fails.
            if (i < 3) Tool(s, "Rare", 2, 1);
            sessions.Add(s);
        }

        var pack = Build(sessions);
        var bash = Assert.Single(pack.Patterns, p => p.Kind == "tool-errors");

        Assert.Equal("Bash fails often", bash.Label);
        Assert.Equal(4, bash.Support);
        Assert.Equal(12, bash.Occurrences);
        Assert.Equal(["s00", "s01", "s02", "s03"], bash.EvidenceSessionIds);
        Assert.Equal(0.2, bash.Stats["errorRate"], 3);
        Assert.DoesNotContain(pack.Patterns, p => p.Label.StartsWith("Rare", StringComparison.Ordinal));

        // The rows say which patterns each session is evidence for, so a reader can go from a
        // session to the recurrence it belongs to.
        Assert.Contains(bash.Id, pack.Sessions!.Single(r => r.Id == "s01").Patterns);
        Assert.Empty(pack.Sessions!.Single(r => r.Id == "s07").Patterns);
    }

    [Fact]
    public void RepairLoopsAndStallsComeFromTheTurnAnalysis()
    {
        var sessions = new List<CopilotSession>();
        for (var i = 0; i < 3; i++) sessions.Add(Session($"loop{i}", tweak: RepairLoop));
        for (var i = 0; i < 3; i++) sessions.Add(Session($"slow{i}", tweak: s =>
        {
            Turn(s, "a", 1, 1, 0, ttft: 500);
            Turn(s, "b", 1, 1, 0, ttft: 500);
            Turn(s, "c", 1, 1, 0, ttft: 2000);
        }));
        for (var i = 0; i < 2; i++) sessions.Add(Session($"clean{i}", tweak: s => { Turn(s, "a", 1, 1, 0); Turn(s, "b", 1, 1, 0); }));

        var pack = Build(sessions);

        var loops = Assert.Single(pack.Patterns, p => p.Kind == "repair-loops");
        Assert.Equal(3, loops.Support);
        Assert.Equal(["loop0", "loop1", "loop2"], loops.EvidenceSessionIds);

        var stalls = Assert.Single(pack.Patterns, p => p.Kind == "latency-stalls");
        Assert.Equal(3, stalls.Support);
        Assert.Equal(["slow0", "slow1", "slow2"], stalls.EvidenceSessionIds);
    }

    [Fact]
    public void AModelContrastNeedsTenSessionsOnEachSide()
    {
        static List<CopilotSession> Base(int perSide)
        {
            var list = new List<CopilotSession>();
            for (var i = 0; i < perSide; i++) list.Add(Session($"a{i:00}", model: "model-a", chatErrors: 2));
            for (var i = 0; i < perSide; i++) list.Add(Session($"b{i:00}", model: "model-b"));
            return list;
        }

        Assert.DoesNotContain(Build(Base(9)).Patterns, p => p.Kind == "model-contrast");

        var contrasts = Build(Base(10)).Patterns.Where(p => p.Kind == "model-contrast").ToList();
        Assert.Equal(2, contrasts.Count);
        var a = contrasts.Single(p => p.Label.StartsWith("model-a", StringComparison.Ordinal));
        Assert.True(a.Stats["delta"] < 0, "the erroring model's sessions score lower");
        Assert.Equal(10, a.Support);
        Assert.Contains("not a causal estimate", a.Reading, StringComparison.Ordinal);
    }

    [Fact]
    public void ContrastsNeverCrossStrata()
    {
        // Imported Claude Code sessions score on fewer components than live VS Code ones. A
        // cross-stratum model contrast would read that measurement difference as a quality one.
        var sessions = new List<CopilotSession>();
        for (var i = 0; i < 12; i++)
            sessions.Add(Session($"cc{i:00}", EmitterKind.ClaudeCode, SessionOrigin.LogImport, model: "claude-sonnet-5", chatErrors: 1));
        for (var i = 0; i < 12; i++)
            sessions.Add(Session($"vs{i:00}", EmitterKind.VSCode, model: "gpt-5"));

        var pack = Build(sessions);

        Assert.DoesNotContain(pack.Patterns, p => p.Kind == "model-contrast");
        Assert.Equal(2, pack.Provenance.ByStratum.Count);
        Assert.All(pack.Patterns, p => Assert.Contains('/', p.Stratum));
    }

    // --------------------------------------------------------------- rollups

    [Fact]
    public void AveragesBelowTheFloorAreNullNotZero()
    {
        var sessions = new List<CopilotSession>();
        for (var i = 0; i < 2; i++) sessions.Add(Session($"x{i}", repo: "acme/x"));
        for (var i = 0; i < 5; i++) sessions.Add(Session($"y{i}", repo: "acme/y"));

        var rows = Build(sessions).Cohorts.Where(r => r.Dimension == "repository").ToList();

        Assert.Null(rows.Single(r => r.Value == "acme/x").AvgQualityScore);
        Assert.NotNull(rows.Single(r => r.Value == "acme/y").AvgQualityScore);
    }

    [Fact]
    public void ExemplarsAreChosenWithinAStratumAndNeedThreeSessions()
    {
        var sessions = new List<CopilotSession>();
        for (var i = 0; i < 5; i++) sessions.Add(Session($"vs{i}", chatErrors: i < 2 ? 3 : 0));
        for (var i = 0; i < 2; i++) sessions.Add(Session($"cli{i}", EmitterKind.CLI));

        var exemplars = Build(sessions).Exemplars;

        Assert.NotEmpty(exemplars);
        Assert.All(exemplars, e => Assert.StartsWith("otel/VSCode/", e.Stratum, StringComparison.Ordinal));
        Assert.Equal(exemplars.Count, exemplars.Select(e => e.Id).Distinct().Count());
        Assert.Contains(exemplars, e => e.Why == "best");
        Assert.Contains(exemplars, e => e.Why == "worst");
        Assert.True(exemplars.Where(e => e.Why == "worst").All(e => e.ChatErrors > 0));
    }

    [Fact]
    public void ComparisonAndRegressionsUseTheBaselineWindow()
    {
        var baseline = new List<CopilotSession>();
        var current = new List<CopilotSession>();
        for (var i = 0; i < 12; i++) baseline.Add(Session($"old{i:00}", lastSeen: T0.AddDays(-40)));
        for (var i = 0; i < 12; i++) current.Add(Session($"new{i:00}", chatErrors: 2));

        var pack = Build(current, baseline);

        Assert.NotNull(pack.Comparison);
        Assert.Equal(12, pack.Comparison!.BaselineSessions);
        var quality = pack.Comparison.Deltas.Single(d => d.Metric == "quality score");
        Assert.True(quality.Delta < 0);
        Assert.NotEmpty(pack.Regressions);
        Assert.Contains(pack.Regressions, r => r.Dimension == "assistant" && r.Value == "VSCode");
    }

    // -------------------------------------------------------------- readiness

    [Fact]
    public void ReadinessCountsOnlyEligibleSessionsSinceTheWatermark()
    {
        var sessions = new List<CopilotSession>();
        for (var i = 0; i < 30; i++) sessions.Add(Session($"s{i:00}", lastSeen: T0.AddHours(-i)));
        sessions.Add(Session("seed-x"));
        sessions.Add(Session("empty", chatCalls: 0));
        sessions.Add(Session("helper", tweak: s =>
            s.AddTranscript(T0, "gpt-5", "Please write a brief title for the following request: x", "y", 0)));

        var ready = ReviewReadiness.Evaluate(sessions, new ReviewOptions(), coveredUntil: null, now: T0);
        Assert.True(ready.Ready);
        Assert.Equal(30, ready.Eligible);
        Assert.Equal(25, ready.MinSessions);

        // Ten of the thirty were last active before the watermark of the previous review.
        var covered = ReviewReadiness.Evaluate(sessions, new ReviewOptions(), coveredUntil: T0.AddHours(-20), now: T0);
        Assert.False(covered.Ready);
        Assert.Equal(20, covered.Eligible);
        Assert.Equal(T0.AddHours(-20), covered.Since);
    }

    // --------------------------------------------------------------- markdown

    [Fact]
    public void TheMarkdownCarriesTheReadingRules()
    {
        var markdown = ReviewPackMarkdown.Render(Build(Varied(30)));

        Assert.StartsWith("# CopilotScope review pack", markdown, StringComparison.Ordinal);
        Assert.Contains("Never quote a score without its confidence", markdown, StringComparison.Ordinal);
        Assert.Contains("never a person", markdown, StringComparison.Ordinal);
        Assert.Contains("Acceptance rate is not a target", markdown, StringComparison.Ordinal);
        Assert.Contains("## Patterns", markdown, StringComparison.Ordinal);
        Assert.Contains("## Exemplars", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkdownStaysUnderTheCapForAThousandSessions()
    {
        var pack = Build(Varied(1000), Varied(300, seed: 5));
        var markdown = ReviewPackMarkdown.Render(pack);

        Assert.True(Encoding.UTF8.GetByteCount(markdown) <= ReviewPackMarkdown.MaxBytes,
            $"{Encoding.UTF8.GetByteCount(markdown)} bytes");
        Assert.Contains(pack.Scope.Fingerprint, markdown, StringComparison.Ordinal);
        Assert.Equal(1000, pack.Scope.Sessions);
        Assert.Equal(new ReviewOptions().MaxSessionRows, pack.Sessions!.Count);
        Assert.Contains(pack.Notes, n => n.Contains("most recent of 1000", StringComparison.Ordinal));
    }
}
