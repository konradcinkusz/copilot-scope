# ADR-003 — Positioning: the self-hosted, auditable-formula alternative

- Status: **Accepted**
- Date: 2026-09-01
- Context: `docs/STRATEGY.md` claimed CopilotScope was *"the only open-source tool that
  turns telemetry from any AI coding assistant into a session quality score"* and described
  the category as empty — *"the first is empty and defensible by the paper"*. Between that
  being written and now, three commercial entrants shipped the category
  ([#99](https://github.com/konradcinkusz/copilot-scope/issues/99)):

  - **DX Agent Experience** (DX is now part of Atlassian; announced at Atlassian Team '26,
    May 2026) — an agent-effectiveness score, filterable by team, with a per-session view
    that surfaces bottlenecks like missing context and scope drift.
  - **Datadog Agent Console** (DASH, June 2026) — a unified view across Claude Code, Cursor
    and GitHub Copilot with adoption analytics, engineering-impact metrics, spend attribution
    and waste detection.
  - **New Relic AI Coding Observability** (announced 2026-06-08, available 2026-06-23) —
    telemetry normalization across Claude Code, Cursor, Copilot, Windsurf and Amazon Q,
    announced as an **open-source** feature at no additional cost.

  The distribution plan ends with "Show HN, last", and predicts the top comment will test
  this project's honesty. A strategy document kept in the repository *on purpose*, as proof
  of published reasoning, is the first thing a skeptical reader opens — and a "nobody does
  this" claim that a two-minute search refutes would discredit a project whose entire brand
  is radical honesty, at its first moment of public scrutiny.

## Decision

**The category is no longer empty, and the strategy says so.** Three commercial entrants
are recorded in `docs/STRATEGY.md` with dates, and their arrival is treated as what it
actually is: **validation that the problem is real**, by companies with far better market
research than this project has.

The uniqueness claim is re-scoped to what survives contact with all three:

> **The open-source, self-hosted session-quality scorer with a published, deterministic
> formula — and the only one that cannot produce a per-developer ranking.**

Every clause is load-bearing, and each one is there because a competitor took a claim away:

1. **Self-hosted.** New Relic's feature is open-source and free of *additional* charge, but
   standard ingest rates apply and the telemetry goes to New Relic; a local-only mode was
   announced as coming later. "Open source" alone is therefore no longer a differentiator —
   **"nothing leaves your infrastructure" is**, and it is true today rather than soon.

2. **Published, deterministic formula.** DX assesses sessions with a separate evaluation
   model. That is a legitimate design with real advantages — it reads nuance a weighted sum
   cannot. It is also not auditable in the same way: you cannot read the weights, re-derive
   a score by hand, or diff two versions of it. CopilotScope's composite is six components
   with published weights, renormalized over the ones that have data, and its confidence
   figure is exported next to every score. Different instrument, different failure mode,
   stated plainly rather than as a slur.

3. **Cannot produce a per-developer ranking.** Datadog Agent Console leads with *"who in my
   organization is using coding agents the most?"* and ships spend attribution. That is a
   real product answering a real demand, and it is this project's explicit non-goal. The
   difference is now **enforced rather than promised**: the cohort filter has no developer
   dimension by construction (#96), privacy mode applies a k-anonymity floor to every view
   and every outbound payload (#94), and per-session Prometheus series are refused under it.
   A claim that used to be a paragraph in the README is now a property tests assert.

**What is dropped:** "the only open-source tool that…" as a standalone claim, and the
timing argument in §3.

## The timing claim was also wrong

`docs/STRATEGY.md` said an OTLP-ingesting design "landed on the right side of a standard
that had just finished settling". Semantic conventions **v1.42.0 (2026-06-12) deprecated the
`gen_ai.*` conventions** and federated them to a separate
`open-telemetry/semantic-conventions-genai` repository, which has no stable release yet. The
standard did not finish settling; it split.

The honest version is stronger anyway: this project already ships a weekly canary
(`.github/workflows/semconv-canary.yml`, #92) that diffs the attributes it consumes against
upstream and opens an issue on drift. "We track a moving standard and will tell you when it
moves" beats "we bet on a settled one" — and unlike the original claim, it is checkable.

## Consequences

- The wedge gets narrower and defensible. Narrower is the point: the previous claim was
  wide and false, which is worth less than nothing to a project selling honesty.
- `docs/COMPARISON.md` states what each of the three does and what CopilotScope does, in
  terms a reader can verify, without disparaging products that are better resourced and in
  several respects better. A comparison page that reads as marketing fails the same test
  the old claim failed.
- Anyone evaluating CopilotScope against DX or Datadog on breadth of integrations, polish
  or support should choose DX or Datadog. The three reasons to choose this are in the
  claim above, and if none of them matter to a reader, it is not for them.
