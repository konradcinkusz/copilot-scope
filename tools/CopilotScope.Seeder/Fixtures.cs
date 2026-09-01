namespace CopilotScope.Seeder;

/// <summary>Fixed pools of realistic-looking demo data — models, repos, tools, prompt/response
/// pairs — drawn from randomly by <see cref="SessionFactory"/> to make each seeded session
/// look like a distinct, plausible Copilot conversation rather than a templated clone.</summary>
public static class Fixtures
{
    public static readonly string[] Models =
    [
        "claude-sonnet-5", "claude-opus-4-8", "claude-haiku-4-5", "claude-sonnet-4-5",
        "gpt-4o", "gpt-4o-mini", "gpt-4.1", "claude-3-5-sonnet", "claude-fable-5",
    ];

    public static readonly string[] Repositories =
    [
        "https://github.com/acme/aurelius-promptus",
        "https://github.com/acme/billing-service",
        "https://github.com/acme/frontend-shell",
        "https://github.com/acme/infra-terraform",
        "https://github.com/acme/mobile-app",
        "https://github.com/acme/data-pipeline",
        "https://github.com/acme/auth-gateway",
        "https://github.com/acme/notifications-svc",
    ];

    public static readonly string[] Branches =
    [
        "main", "develop", "feature/otel-dashboard", "fix/retry-policy", "chore/deps-bump",
        "feature/search-v2", "hotfix/nullref", "feature/pagination", "refactor/session-store",
        "spike/vector-index", "release/2.4.0",
    ];

    public static readonly string[] Tools =
    [
        "readFile", "editFile", "runCommand", "grepSearch", "listDirectory", "createFile",
        "semanticSearch", "applyPatch", "runTests", "gitDiff", "terminalCommand", "webFetch",
        "findUsages", "renameSymbol",
    ];

    public static readonly string[] ErrorTypes =
    [
        "RateLimitError", "ToolExecutionError", "ContextLengthExceeded", "NetworkTimeout",
        "AuthenticationError", "ModelOverloaded", "ContentFiltered", "InvalidRequest",
    ];

    public static readonly (string Prompt, string Response)[] Turns =
    [
        ("Refactor SessionStore.Ingest to avoid double-counting tokens.",
         "Updated the aggregation so chat spans are the single token source; invoke_agent totals are ignored."),
        ("Why does the OTLP decoder return an empty batch for this payload?",
         "The payload was gzip-compressed; the request body must be decompressed before protobuf decoding."),
        ("Add a retry policy with exponential backoff to the forwarder.",
         "Added a bounded channel with 3 retries and exponential backoff starting at 500 ms."),
        ("Write unit tests for the quality engine prior blending.",
         "Added tests covering the empty-session prior and the confidence ramp at 5 samples."),
        ("Explain the difference between gauge and sum metric points.",
         "A gauge reports the last value; a sum accumulates deltas or cumulative totals over time."),
        ("Can you fix the null reference in the dashboard session list?",
         "Guarded the null repository case and added a fallback label for anonymous sessions."),
        ("Optimize the Postgres upsert to batch multiple sessions at once.",
         "Switched to a single multi-row INSERT ... ON CONFLICT statement."),
        ("Add pagination to the /api/sessions endpoint.",
         "Added limit/offset query parameters with a default page size of 50."),
        ("Investigate why TTFT samples look bimodal in the chart.",
         "Found two code paths reporting TTFT with different units; normalized both to milliseconds."),
        ("Split the SegmentAnalyzer into per-turn and per-session scoring.",
         "Extracted a TurnScorer helper; SegmentAnalyzer now just ranks its output."),
        ("Add a health check that verifies the Postgres connection at startup.",
         "Registered an IHealthCheck that runs SELECT 1 and wired it into /health."),
        ("The SignalR hub drops updates when many sessions change at once.",
         "Batched the broadcast behind a 250 ms debounce so bursts collapse into one push."),
        ("Convert the pricing table to be configurable via appsettings.",
         "Bound CopilotScope:Pricing to PricingOptions and switched the analyzer to resolve from it."),
        ("Write a migration to add the emitter_kind column to sessions.",
         "Added migration 0007 with a nullable smallint defaulting to 0 (Unknown)."),
        ("Why is cache_read sometimes larger than input tokens?",
         "Two spans were summing cache reads twice; deduplicated by trace id before aggregation."),
        ("Make the workflow-friction analyzer bilingual for Polish prompts.",
         "Added a Polish lexicon slice and normalized diacritics before matching."),
        ("Add a Dockerfile for the dashboard with a multi-stage build.",
         "Added Dockerfile.dashboard: SDK build stage, trimmed runtime stage on aspnet:8.0."),
        ("The abandonment-risk threshold feels too aggressive at 8s.",
         "Kept 8s as the hard threshold but added a softer 2s 'degraded attention' bucket."),
        ("Deduplicate tool stats when a session is merged from the unattributed bucket.",
         "MergeFrom now folds tool tuples by name instead of overwriting them."),
        ("Add a percentile-rank badge to the session quality score.",
         "Computed the rank against the trailing history window and surfaced it in the header."),
        ("Trim captured content so a single prompt can't blow up the jsonb column.",
         "Truncated transcript entries to 4000 chars with a '…[truncated]' marker."),
        ("Explain why the composite score uses a Bayesian prior.",
         "Small sessions have noisy signals; the prior pulls them toward the global mean until enough samples arrive."),
        ("Cursor sessions show empty edit tiles — is that a bug?",
         "No — CLI-like emitters never send edit/thumbs/LOC signals, so those tiles are hidden by design."),
        ("Add a 'Normalize by repo' toggle to the sessions rail.",
         "Added the toggle; when on, the score color uses the repo-scoped percentile instead of the absolute grade."),
        ("Batch the OTLP export so we send one request per 512 spans.",
         "Added a size-and-time flush trigger to the exporter queue."),
        ("Why do internal title-generation calls show up as sessions?",
         "They're classified as InternalTitleGeneration and filtered out of the default rail view."),
        ("Add jitter to the retry backoff to avoid a thundering herd.",
         "Added full jitter: delay = random(0, base * 2^attempt)."),
        ("Surface the biggest drag on the quality score in the header.",
         "Ranked components by weight × shortfall and rendered the worst one as a callout."),
    ];

    public static readonly (string Prompt, string Response)[] RepairTurns =
    [
        ("This still doesn't work, the tests are failing again.",
         "Sorry about that — let me look at the failure more carefully."),
        ("Wrong again!! I already told you the bug is in the decoder, not the store.",
         "You're right, apologies — fixing the decoder path now."),
        ("Please add a retry policy to the forwarder queue.",
         "Added a bounded retry with backoff."),
        ("add a retry policy to the forwarder queue please",
         "Done, same change applied with jitter added this time."),
        ("no. that is not what I asked for, revert it.",
         "Reverted the change and restored the previous implementation."),
        ("why did you touch the migration? I said only the analyzer.",
         "My mistake — I've reverted the migration and limited the change to the analyzer."),
    ];

    /// <summary>Captured content in the richer OTEL dialect — a JSON array of role-tagged
    /// messages. Exercises the dashboard chat view's system/user/assistant/tool rendering
    /// (plain-text turns only ever produce user + assistant bubbles).</summary>
    public static readonly (string Prompt, string Response)[] RichTurns =
    [
        ("""[{"role":"system","content":"You are GitHub Copilot, an AI programming assistant working inside VS Code."},{"role":"user","content":"Add limit/offset pagination to the /api/sessions endpoint and cap the page size at 100."}]""",
         """[{"role":"assistant","content":"I'll add limit/offset query parameters with a hard cap of 100, then update the OpenAPI annotations."},{"role":"tool","content":"editFile → src/CopilotScope.Collector/Api/SessionsEndpoint.cs (+18 −2)"},{"role":"assistant","content":"Done. Page size now clamps to [1, 100] with a default of 50."}]"""),
        ("""[{"role":"system","content":"Follow the repository's C# style: file-scoped namespaces, primary constructors where practical."},{"role":"user","content":"Extract the TTFT utility curve into its own testable method."}]""",
         """[{"role":"assistant","content":"Extracting Utility(double ms) as a private static method and adding boundary tests."},{"role":"tool","content":"createFile → tests/CopilotScope.Tests/LatencyUtilityTests.cs"},{"role":"tool","content":"runTests → 12 passed, 0 failed (1.8s)"},{"role":"assistant","content":"All green — the curve is now unit-tested at 300ms, 2s and 10s boundaries."}]"""),
        ("""[{"role":"user","content":"The dashboard shows negative net-compute for one session. Trace it."}]""",
         """[{"role":"assistant","content":"Net compute is tokens_in − cache_read; a negative value means cache_read exceeded input, which shouldn't happen."},{"role":"tool","content":"grepSearch → CacheReadTokens (7 matches)"},{"role":"tool","content":"readFile → src/CopilotScope.Collector/Domain/SessionStore.cs"},{"role":"assistant","content":"Found it: cache reads were double-added on span retry. Guarding by trace id fixes the invariant."}]"""),
        ("""[{"role":"system","content":"Prefer minimal diffs. Do not reformat untouched lines."},{"role":"user","content":"Wire the new AuthenticationError type into the error breakdown panel."}]""",
         """[{"role":"assistant","content":"Adding AuthenticationError to the ErrorTypes enum surface and the dashboard legend."},{"role":"tool","content":"applyPatch → src/CopilotScope.Dashboard/Components/Pages/Home.razor"},{"role":"assistant","content":"The panel now renders AuthenticationError with its own row and count."}]"""),
    ];

    public static readonly string[] TitlePromptTails =
    [
        "write 5 paragraphs about the release notes",
        "explain the retry policy change to a new teammate",
        "summarize the OTLP decoder bug we just fixed",
        "draft the changelog entry for the pagination endpoint",
        "describe the workflow-friction analyzer for the docs page",
    ];

    /// <summary>Per-turn "mood" presets used by the <c>showcase</c> persona to manufacture
    /// genuine intra-session variety: a single 30+ turn chat that walks through clean/fast
    /// turns, latency stalls (abandonment risk), an error storm, and a tool repair loop —
    /// so every dashboard panel and every analyzer finding lights up on one session.</summary>
    public sealed record TurnMood(
        string Name,
        double MinTtftMs, double MaxTtftMs,
        double ChatErrorRate, double ToolErrorRate,
        int MinTools, int MaxTools);

    public static readonly TurnMood[] ShowcaseMoods =
    [
        new("clean-fast",  200,   650,  0.00, 0.00, 0, 2),
        new("steady",      450,  1400,  0.05, 0.06, 1, 3),
        // Above the analyzer's 8s abandonment threshold — guarantees the "abandonment risk" finding.
        new("stall",      8200,  9700,  0.05, 0.05, 0, 2),
        // High error rates — feeds the error-concentration finding and the error-type breakdown.
        new("error-storm", 900,  2400,  0.55, 0.45, 1, 3),
        // Many tool calls plus tool failures — the trace signature SegmentAnalyzer flags as a repair loop.
        new("repair-loop", 700,  1900,  0.15, 0.55, 3, 5),
    ];
}
