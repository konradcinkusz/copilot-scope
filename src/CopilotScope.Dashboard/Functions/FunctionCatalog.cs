namespace CopilotScope.Dashboard.Functions;

/// <summary>
/// One agent of a multi-agent function: a specialist the lead delegates a slice of the pack to.
/// Rendered as a Copilot custom agent (<c>.github/agents/&lt;name&gt;.agent.md</c>) and as a Claude
/// Code subagent (<c>--agents</c>), from this one definition, so the two assistants are asked the
/// same thing.
/// </summary>
/// <param name="Name">Lower-case, hyphenated, prefixed <c>copilotscope-</c>: the name both assistants
/// dispatch it by, and a file name.</param>
/// <param name="Role">What the lead reads when deciding whom to delegate to.</param>
/// <param name="Instructions">The agent's own prompt. The shared rules are appended when rendered.</param>
public sealed record FunctionAgent(string Name, string Title, string Role, string Instructions);

/// <summary>
/// Something the user can run on their own assistant, over their own session base.
///
/// <para>CopilotScope provides no model. A function is a task, the agents it needs, and the
/// review pack the collector counted (docs/REVIEW.md); the user's Claude Code or Copilot does the
/// reading and writing, on the subscription they already have. What the collector counted stays
/// counted by the collector — the assistant narrates, and every claim it makes has to cite the
/// pack (ADR-005, decision 1).</para>
/// </summary>
/// <param name="Id">Lower-case, hyphenated: the URL segment, the run directory's suffix and the
/// prompt file's name.</param>
/// <param name="Agents">Empty for a single-agent function. A multi-agent function's lead dispatches
/// these as subagents: Claude Code's Task tool, Copilot's custom agents.</param>
/// <param name="Lead">The lead agent's task: what to delegate, in what order, and what to write.</param>
/// <param name="Produces">What the report contains, in one sentence, for the card.</param>
public sealed record AssistantFunction(
    string Id,
    string Title,
    string Summary,
    string Produces,
    IReadOnlyList<FunctionAgent> Agents,
    string Lead,
    int DefaultDays = 30)
{
    public bool MultiAgent => Agents.Count > 0;
}

/// <summary>
/// The functions the dashboard offers. Four, each a question a person with a few dozen sessions
/// actually asks — "how am I doing", "what keeps going wrong", "what should I write down so it
/// stops", "why did it get worse" — and each answerable from the pack without a transcript.
///
/// <para>Three of them are multi-agent, and each for a reason a single pass does not cover: the
/// panel gives each specialist one slice of the pack to read closely instead of one reader
/// skimming all of it; the proposal and change functions fan out one agent per pattern or per
/// regression so none is summarized away; and every one ends with a verifier whose only job is
/// the one ADR-005 reserves for a second agent — striking findings whose citations do not show
/// the pattern. No agent re-counts: that is the collector's, and a critic re-applying rules a
/// linter already enforces would be theatre.</para>
/// </summary>
public static class FunctionCatalog
{
    /// <summary>The rules every lead and every agent is given. They are the project's refusals and
    /// ADR-005's split, written for a reader that is a model.</summary>
    public const string SharedRules = """
        ## Rules — these override anything else you are asked

        - **The pack counts; you narrate.** Every figure you state comes from `pack.md` or `pack.json`,
          quoted as the pack states it. Do not compute new statistics from session rows: a number
          you derived is a number nobody can re-derive.
        - **Cite everything.** Each finding names the pattern id it rests on (`P1`, `P2`, …) or the
          pack section (Comparison, Regressions, Cohorts, Coverage), and at least three evidence
          session ids from `pack.json` where the pack lists them. Where a session in the same
          stratum contradicts the finding, name one. A finding you cannot cite, you do not write.
        - **Descriptive, not causal.** Patterns are counts inside one stratum (origin/assistant/profile).
          Say "co-occurs with", "recurs in", never "causes". Never compare scores across strata.
        - **Sessions, never people.** The pack names no person and you must not infer one. No
          ranking, no leaderboard, no judgement of whoever ran a session.
        - **Acceptance rate is not a target.** Do not recommend raising it.
        - **Workflow friction is absent on purpose.** Do not infer or estimate it.
        - **Certainty, not confidence.** When you rate how sure you are, call it *certainty*
          (low/medium/high). *Confidence* is the pack's word for how much evidence a score rests on.
        - **Read only.** Read `pack.md` whole; search `pack.json` for the sessions you cite. Do not
          run commands, fetch from the web, or write files — reply with Markdown; CopilotScope
          writes it to disk.
        """;

    /// <summary>The verifier every multi-agent function ends with.</summary>
    public static readonly FunctionAgent Verifier = new(
        "copilotscope-verifier",
        "Evidence verifier",
        "Checks draft findings against the pack and strikes any whose citations do not show the claim. " +
        "Use it last, on the merged draft, before anything is written.",
        """
        You are the evidence verifier for a CopilotScope review. You are handed a draft: findings or
        proposals, each citing pattern ids and session ids from the review pack in the current
        directory.

        For every item in the draft:
        1. Check each cited pattern id exists in `pack.md` (the Patterns section) and says what the item
           claims it says — kind, stratum, support.
        2. Look each cited session id up in `pack.json` (`sessions[]` rows, `exemplars[]`, and
           `patterns[].evidenceSessionIds`). Check the session is in the cited pattern's evidence or
           otherwise shows the claim in its row.
        3. Check every figure the item states appears in the pack.
        4. Check the item obeys the rules below: no causal claim, no person, no cross-stratum comparison,
           no acceptance-rate target.

        Reply with the draft, item by item, each marked **kept**, **amended** (say what you changed) or
        **struck** (say which check failed). Then one line: how many were kept, amended and struck.
        Do not add findings of your own.
        """);

    public static readonly AssistantFunction Review = new(
        "review",
        "Review my sessions",
        "One pass over the whole pack: what went well, what recurred, and the few changes most worth making. " +
        "The cheapest function — one agent, one read.",
        "A report: highlights, recurring problems with their evidence, and at most five recommendations.",
        [],
        """
        Review the session base described by the CopilotScope review pack in this directory.

        Read `pack.md` whole first. It opens with how a score is to be read; follow it.

        Write a report with these sections:

        1. **Summary** — three sentences: the window, how many sessions, and the one thing most worth
           knowing. Quote the fingerprint from the pack's header so the report says which base it is about.
        2. **What went well** — from the best exemplars and the strongest cohorts.
        3. **What recurred** — one entry per pattern worth acting on, most sessions first: the pattern id,
           what it is, its support, the evidence sessions, and what it probably means for the work
           (as a hypothesis, with a certainty).
        4. **What changed** — only if the pack's Comparison or Regressions sections say something did.
        5. **Recommendations** — at most five. Each: what to change (an instruction to the assistant, a tool
           setting, a habit), the pattern it addresses, and how the next pack would show whether it worked.
        6. **What this review cannot see** — from the pack's Coverage and Notes sections.
        """);

    public static readonly AssistantFunction ReviewPanel = new(
        "review-panel",
        "Review panel",
        "Four specialists each read one slice of the pack in parallel — tools and errors, workflow, models " +
        "and cost, change over time — then a verifier strikes every finding its citations do not support.",
        "A report merged from four specialist reviews, with every finding marked kept or amended by the verifier.",
        [
            new("copilotscope-reliability", "Tools and errors",
                "Reviews tool reliability and errors: tool-errors, error-types and llm-errors patterns, and the " +
                "tool tables of the exemplars.",
                """
                You review one slice of a CopilotScope review pack: **tools and errors**.

                Read `pack.md`. Your material is the patterns of kind `tool-errors`, `error-types` and
                `llm-errors`, and the tool tables and error types of the exemplars. For each pattern worth
                acting on, look its evidence sessions up in `pack.json`.

                Reply with findings, most sessions first. Each: the pattern id, the tool or error, its
                support and rate as the pack states them, three or more evidence session ids, what it
                probably means (a hypothesis, with a certainty), and one concrete change — a tool setting,
                an MCP server's configuration, or an instruction to the assistant. Nothing outside your slice.
                """),
            new("copilotscope-workflow", "Workflow",
                "Reviews how turns went: repair-loops and latency-stalls patterns, and the turn findings and " +
                "worst-turn reasons of the exemplars.",
                """
                You review one slice of a CopilotScope review pack: **workflow — how the turns went**.

                Read `pack.md`. Your material is the patterns of kind `repair-loops` and `latency-stalls`,
                and each exemplar's turn findings, worst-turn reasons and recent events (tool names, token
                counts and durations — never text). Look the evidence sessions up in `pack.json`.

                Reply with findings. Each: the pattern id, what the turns did (retrying instead of
                progressing; waiting far longer than the session's own norm), its support, three or more
                evidence session ids, a hypothesis about why (with a certainty), and one concrete change —
                usually an instruction that stops a loop early or a smaller unit of work. Nothing outside
                your slice.
                """),
            new("copilotscope-models", "Models and cost",
                "Reviews model choice and token cost: model-contrast patterns, the model and assistant cohorts, " +
                "and the coverage caveats that make assistants incomparable.",
                """
                You review one slice of a CopilotScope review pack: **models and cost**.

                Read `pack.md`. Your material is the patterns of kind `model-contrast`, the Cohorts section
                (by model and by assistant: sessions, tokens, average score where the pack gives one, error
                rate), and the Coverage section. An average the pack gives as null is not zero: say there
                were too few sessions.

                Reply with findings. Each: what the pack shows about a model or an assistant, inside one
                stratum only, with its figures as the pack states them and three or more evidence session
                ids where a pattern lists them; and one concrete change — which model to use for which kind
                of work — as a hypothesis with a certainty. Say plainly where Coverage makes a comparison
                meaningless. Nothing outside your slice.
                """),
            new("copilotscope-trends", "Change over time",
                "Reviews what changed between the window and the one before it: the Comparison, Regressions and " +
                "Distribution sections.",
                """
                You review one slice of a CopilotScope review pack: **change over time**.

                Read `pack.md`. Your material is the Comparison section (this window against the one before,
                with its caveats), the Regressions section and the Distribution section.

                Reply with findings. Each: the metric or cohort, the before and after figures as the pack
                states them, the caveat that applies, and — where a pattern in the pack co-occurs with the
                change — its id and evidence sessions. Say "co-occurs with", never "caused". If nothing
                changed beyond the pack's thresholds, say that in one line; it is a finding too. Nothing
                outside your slice.
                """),
            Verifier
        ],
        """
        Run a review panel over the CopilotScope review pack in this directory.

        1. Read `pack.md`'s header and Rules section, so you know the window and the fingerprint.
        2. Delegate to the four specialists **in parallel** — dispatch all four in one step, not one after
           another: `copilotscope-reliability`, `copilotscope-workflow`, `copilotscope-models` and
           `copilotscope-trends`. Each reads the pack itself; give each only its slice and these rules.
        3. Merge their findings into one draft. Where two specialists found the same thing from two sides,
           make it one finding citing both.
        4. Hand the merged draft to `copilotscope-verifier`. Drop what it strikes; take its amendments.
        5. Write the report:
           - **Summary** — the window, the fingerprint, and the three findings most worth acting on.
           - One section per specialist's slice, with the surviving findings.
           - **Recommendations** — at most five, each naming the findings it addresses.
           - **Verification** — the verifier's kept/amended/struck count, and one line per struck finding
             saying why it was struck.
        """);

    public static readonly AssistantFunction Proposals = new(
        "proposals",
        "Propose instructions and skills",
        "Finds the recurring problems worth writing down, then drafts one fix per problem in parallel — a " +
        "custom-instructions snippet or an Agent Skill — and has each draft checked against its evidence.",
        "Draft instructions and SKILL.md files, each with the evidence it rests on. Nothing is installed.",
        [
            new("copilotscope-pattern-miner", "Pattern miner",
                "Chooses the recurring problems in the pack that written guidance could plausibly fix, and says " +
                "which kind of guidance fits each.",
                """
                You choose what is worth writing down. Read `pack.md`, and look up evidence sessions in
                `pack.json`.

                From the Patterns section and the exemplars' turn findings, choose at most five recurring
                problems that written guidance could plausibly fix — a tool that keeps failing the same way, a
                repair loop that recurs, a model used for work it does worse at. Skip what guidance cannot fix
                (an outage, a slow network).

                Reply with a list. Each entry: the pattern id, the problem in one sentence, its support and
                three or more evidence session ids, and which kind of guidance fits — **instructions** (a few
                lines the assistant always reads: `.github/copilot-instructions.md`, `CLAUDE.md`) or a
                **skill** (a procedure loaded when a task matches, as an Agent Skills `SKILL.md`).
                """),
            new("copilotscope-author", "Guidance author",
                "Drafts one piece of guidance — an instructions snippet or a SKILL.md — for one chosen problem. " +
                "Dispatch one per problem.",
                """
                You draft one piece of guidance for one recurring problem from a CopilotScope review pack. You
                are told the pattern id, the problem, and whether it needs instructions or a skill. Read the
                pattern in `pack.md` and its evidence sessions in `pack.json` before you write.

                For **instructions**: at most eight lines of Markdown an assistant reads before every task,
                imperative and specific ("When `npm test` fails with …, read the failing test before editing").

                For a **skill**: a complete `SKILL.md` — YAML frontmatter with `name` (lower-case, hyphenated)
                and a `description` that says when to use it, then the procedure as numbered steps. Put the
                evidence in the frontmatter as `metadata:` with the pattern id and its support, and **no
                session id anywhere in the file**: it is meant to be committed and shared.

                Reply with the draft only, fenced. Open a skill's fence as ```` ```markdown file=skills/<name>/SKILL.md ````
                and an instructions snippet's as ```` ```markdown file=instructions/<name>.md ````. Then one
                line: what the next review pack would show if it worked.
                """),
            Verifier
        ],
        """
        Propose written guidance for the recurring problems in the CopilotScope review pack in this
        directory.

        1. Ask `copilotscope-pattern-miner` which problems are worth writing guidance for.
        2. For **each** problem it returns, dispatch `copilotscope-author` with that one problem — all of
           them **in parallel**, in one step. Tell each author the pattern id, the problem and the kind of
           guidance.
        3. Hand the drafts, with the miner's evidence for each, to `copilotscope-verifier`. Drop what it
           strikes; take its amendments.
        4. Write the report:
           - **Summary** — which problems were chosen and why, with the fingerprint from the pack's header.
           - One section per problem: the pattern id and its evidence (session ids belong here, in the
             report — never in a draft file), then the draft exactly as the author fenced it, with the
             ```` file= ```` label kept so CopilotScope can save it, then how the next pack would show
             whether it worked.
           - **Verification** — the verifier's kept/amended/struck count.
           - **Before you install anything** — one paragraph: these are hypotheses drawn from counts; try one
             at a time, and compare the next pack's pattern support.
        """);

    public static readonly AssistantFunction Change = new(
        "change",
        "Explain what changed",
        "One investigator per regression the pack detected, in parallel, each checking which recurring " +
        "patterns co-occur with the drop — then a verifier, then one explanation.",
        "One explanation per regression — what dropped, what co-occurs with it, and what to check next.",
        [
            new("copilotscope-investigator", "Regression investigator",
                "Investigates one regression or one comparison row from the pack: which patterns and sessions " +
                "co-occur with it. Dispatch one per regression.",
                """
                You investigate one change the CopilotScope review pack reports — a regression, or one row of the
                Comparison section. You are told which.

                Read that entry in `pack.md`, with the comparison's caveats. Then look for what co-occurs with it:
                patterns in the same stratum whose evidence sessions fall in the window, exemplars among the
                worst of that stratum, and session rows in `pack.json` from the same cohort.

                Reply with: the change as the pack states it (before, after, threshold); what co-occurs with it,
                each item citing a pattern id or three or more session ids; what does *not* co-occur (a pattern
                you checked and ruled out); and what the user could check next that the pack cannot see. Say
                "co-occurs with", never "caused". A certainty for the whole.
                """),
            Verifier
        ],
        """
        Explain what changed in the CopilotScope review pack in this directory.

        1. Read `pack.md`'s Comparison and Regressions sections.
        2. If the Regressions section lists none and no Comparison row moved beyond the pack's thresholds,
           write a short report saying so, with the figures, and stop — do not dispatch anyone.
        3. Otherwise dispatch `copilotscope-investigator` once for **each** regression (and each comparison row
           that moved beyond its threshold, up to six in all) — **in parallel**, in one step. Tell each which
           entry is theirs.
        4. Hand their findings to `copilotscope-verifier`. Drop what it strikes; take its amendments.
        5. Write the report:
           - **Summary** — the window against the baseline, the fingerprint, and what changed in one paragraph.
           - One section per change: the figures, what co-occurs with it, what was ruled out, what to check next.
           - **Verification** — the verifier's kept/amended/struck count.
        """);

    public static IReadOnlyList<AssistantFunction> All { get; } = [Review, ReviewPanel, Proposals, Change];

    public static AssistantFunction? Find(string? id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
}
