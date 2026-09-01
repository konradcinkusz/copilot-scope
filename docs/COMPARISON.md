# CopilotScope vs. DX, Datadog and New Relic

Three commercial products now measure AI coding-assistant sessions. This page says what each
does and where CopilotScope differs, in terms you can check.

**It is not a page arguing you should pick CopilotScope.** All three are better resourced,
better supported and broader than a solo-maintained OSS project, and for most organizations
that will decide it. There are three reasons to choose this instead; they are at the bottom,
and if none of them matter to you, one of the others is the right answer.

*Last verified 2026-09-01. Vendor products move; if something here is out of date, it is a
bug — [open an issue](https://github.com/konradcinkusz/copilot-scope/issues).*

---

## What each one is

### DX Agent Experience
*DX is now part of Atlassian. Announced at Atlassian Team '26, May 2026.*

An **agent-effectiveness score**, filterable by team, with a per-session view that surfaces
where agents hit bottlenecks — missing context, ambiguous instructions, scope drift. DX
scores sessions with a separate evaluation model across three dimensions: the quality of the
initial requirements and context, the effectiveness of the developer's guidance during the
session, and whether the work stayed appropriately scoped. It sits inside a mature
developer-experience platform with survey data, DORA metrics and org-wide benchmarks
alongside it.

**Closest to CopilotScope in intent.** Both ask "was this session any good?" rather than "how
much did we use?".

### Datadog Agent Console
*DASH, June 2026.*

A unified view of activity across Claude Code, Cursor and GitHub Copilot (plus Datadog's own
agents), with adoption analytics, **engineering-impact metrics, spend attribution and
automated waste detection**. Datadog frames it around questions including *"who in my
organization is using coding agents the most?"* and *"how does AI spend correlate with
engineering output?"*. A related capability tags commits with the tool and model that
assisted them, following the code from pull request to production.

**The broadest of the three**, and the only one that connects assistant usage to production
telemetry — because Datadog already has the production telemetry.

### New Relic AI Coding Observability
*Announced 2026-06-08, available 2026-06-23.*

Telemetry normalization across Claude Code, Cursor, Copilot, Windsurf and Amazon Q, capturing
session metrics, cost breakdowns, behavioural patterns and efficiency scores. Announced as an
**open-source feature at no additional cost** — standard New Relic ingest rates apply, and a
local-only mode was announced as coming later.

**The one that removes "but it's open source" as a differentiator**, which is why the claim
on this project changed (see [ADR-003](architecture/ADR-003-positioning.md)).

---

## Where CopilotScope differs

| | DX | Datadog | New Relic | CopilotScope |
|---|---|---|---|---|
| Session quality score | ✅ | ✅ | ✅ efficiency scores | ✅ |
| Runs on your infrastructure | ❌ SaaS | ❌ SaaS | ⚠️ local-only announced as coming | ✅ today |
| Telemetry leaves your network | yes | yes | yes (ingest) | **no** |
| Scoring formula published | ❌ evaluation model | ❌ | ❌ | ✅ six weights, renormalized |
| Score re-derivable by hand | ❌ | ❌ | ❌ | ✅ |
| Confidence exported per score | ❌ | ❌ | ❌ | ✅ coverage × sample ramp |
| Per-developer ranking | team-level | ✅ a headline feature | — | **cannot** |
| Turn-level repair analysis | bottleneck detection | struggle/waste detection | behavioural patterns | ✅ TFRA, from traces |
| Assistants | broad | Claude Code, Cursor, Copilot | 5 named | 4 verified |
| Connects to production | ❌ | ✅ | ✅ | ⚠️ PR outcomes only |
| Price | commercial | commercial | ingest rates | free |
| Support | vendor | vendor | vendor | one person ([GOVERNANCE.md](../GOVERNANCE.md)) |

### The three reasons to choose this

**1. Nothing leaves your infrastructure.** Not "we don't sell your data" — the collector runs
on your machine, writes to your Postgres, and has no callback of any kind. For a works
council or a DPO this is the difference between a negotiation and a non-starter: German
BetrVG §87(1)(6) gives co-determination over any system *capable* of monitoring performance,
and a SaaS vendor's assurance is a contract term while a local process is a fact.
[Privacy mode](PRIVACY.md) goes further: pseudonymization at ingest, no prompt retention, an
aggregation floor, an access log.

**2. The formula is published and deterministic.** Six components with stated weights,
renormalized over the ones that have data, with a confidence figure next to every score. You
can read it, re-derive a score by hand, and diff two versions of it. DX's evaluation model
reads nuance a weighted sum cannot — that is a real advantage — but you cannot audit it, and
when it changes your history changes with it and you will not be told which part.

CopilotScope's honesty about this cuts the other way too: **the formula is not calibrated
yet.** [CALIBRATION.md](CALIBRATION.md) says so, ships the machinery, and now ships the
labelling flow to fix it. None of the three tells you how well their score agrees with human
judgment either — the difference is that this one says it doesn't know.

**3. It cannot rank developers.** Datadog leads with *"who is using coding agents the most?"*
That is a real product for a real demand. This one refuses it, and the refusal is enforced
rather than promised: the cohort filter has no developer dimension by construction, privacy
mode applies a k-anonymity floor to every view and every outbound payload, per-session
Prometheus series are refused under it, and tests assert all of it. If you *want* per-developer
adoption ranking, Datadog is the correct choice and this is the wrong tool.

### Where the others are simply better

Stated because a comparison page that only flatters its author is worth nothing:

- **Breadth.** Datadog and New Relic support more assistants and integrate with what you
  already run. This supports four, verified.
- **Production linkage.** Datadog follows an AI-assisted commit to production. CopilotScope
  reaches pull-request outcomes at best, and does not fold them into the score.
- **Support and continuity.** They have teams. This has one maintainer — answered honestly in
  [GOVERNANCE.md](../GOVERNANCE.md), including what happens if that stops.
- **Polish.** Their dashboards are made by design teams.

### What all four share

None has published a validation of its score against human judgment. CopilotScope is the
only one that says so out loud, which is not the same as being better — it is the same gap
with the lights on.

---

## Sources

- DX Agent Experience — <https://getdx.com/blog/introducing-agent-experience/>,
  <https://docs.getdx.com/reports/agent-experience-score/>,
  <https://www.atlassian.com/blog/company-news/dx-team-26>
- Datadog Agent Console — <https://www.datadoghq.com/blog/datadog-agent-console/>,
  <https://docs.datadoghq.com/ai_agents_console/>,
  <https://www.datadoghq.com/blog/dash-2026-new-feature-roundup-ai/>
- New Relic AI Coding Observability — <https://newrelic.com/press-release/20260608>,
  <https://newrelic.com/blog/news/introducing-ai-coding-observability>
