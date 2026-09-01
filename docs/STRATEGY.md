# CopilotScope — positioning and strategy

Why this project exists, who it is for, what it deliberately does not do, and how
it is meant to find its audience. Written in July 2026, when the repo was two weeks
old and had no users.

Kept in the repo on purpose: a project that publishes a scoring framework should
be willing to publish its own reasoning too.

---

## 1. The problem is real, and it is not the one most tools solve

The measurement gap is well documented by now:

- **90%** of developers use AI in daily work (DORA, 2025 → 2026), and AI writes
  roughly **41%** of code globally.
- Yet **95%** of organizations report no measurable P&L impact from GenAI
  investment. Activity is visible; outcomes are not.
- METR's controlled study found developers were **19% slower** with AI tools while
  believing they were **20% faster**. Self-report is not a measurement.
- GitClear, across 623 million code changes (2023–2026): duplicated blocks **+81%**,
  within-commit copy/paste **+41%**, error-masking constructs **+47%**, refactoring
  line moves **−70%**.

Read together these say something specific: **counting AI usage does not tell you
whether AI is helping.** Token counts go up either way. So do acceptance rates —
especially if you tell people to raise them.

That is the gap CopilotScope aims at. Not "how much AI did we use", but "were
those sessions any good, and where did they go wrong".

## 2. What already exists

| Project | What it measures | Scale |
|---|---|---|
| `github-copilot-resources/copilot-metrics-viewer` | org-level Copilot Metrics API | ~630★ |
| `ColeMurray/claude-code-otel` | Claude Code cost/tokens/sessions → Grafana | ~480★ |
| `microsoft/copilot-metrics-dashboard` | same API, Azure accelerator | ~200★ |
| `satomic/copilot-usage-advanced-dashboard` | Copilot API + retention | ~80★ |
| `o11y-dev/opentelemetry-hooks` | OTel hooks for Cursor/Copilot/Claude/Codex | ~30★ |
| `git-ai-project/git-ai` | per-line AI attribution in git notes | Apache-2.0, Rust |
| GitHub, natively | Copilot usage metrics API and dashboard | 28-day retention, org-level only |
| DX, Jellyfish, LinearB, Faros, Swarmia | engineering intelligence, ROI reporting | commercial |

The open-source field here is entirely **usage dashboards** — seats, tokens, cost,
acceptance rate. That much still holds.

### The category stopped being empty in mid-2026

An earlier version of this document said *"nobody scores the quality of a session"*.
That is no longer true, and pretending otherwise would fail on the first search anyone
runs. Three commercial entrants shipped the category:

| Product | Shipped | What it is |
|---|---|---|
| **DX Agent Experience** (DX is now part of Atlassian) | Atlassian Team '26, May 2026 | An agent-effectiveness score, filterable by team, with a per-session view surfacing bottlenecks — missing context, ambiguous instructions, scope drift. Scored by a separate evaluation model across three dimensions. |
| **Datadog Agent Console** | DASH, June 2026 | Unified view across Claude Code, Cursor and GitHub Copilot: adoption analytics, engineering-impact metrics, spend attribution, automated waste detection. Leads with *"who is using coding agents the most?"* |
| **New Relic AI Coding Observability** | announced 2026-06-08, available 2026-06-23 | Telemetry normalization across Claude Code, Cursor, Copilot, Windsurf and Amazon Q. Announced as an **open-source** feature at no additional cost — standard ingest rates apply, with a local-only mode announced as coming later. |

**Read this as validation, not as a eulogy.** Three companies with vastly better market
research than this project independently concluded the problem is real and worth building
for. That is the strongest external evidence the thesis has ever had. What it costs is the
"empty niche" framing, which was the weakest part of the argument anyway — a category
nobody has entered is usually a category nobody wants.

See [`COMPARISON.md`](COMPARISON.md) for what each does next to what CopilotScope does, and
[ADR-003](architecture/ADR-003-positioning.md) for the decision this landscape forced.

## 3. The wedge

What CopilotScope has that the list above does not:

1. **A composite quality score with published weights and a confidence figure.**
   Reliability 0.25 · acceptance 0.20 · friction 0.20 · latency 0.15 · feedback 0.10
   · efficiency 0.10, renormalized over the components that actually have data.
   Confidence = coverage × sample ramp, exported next to every score.
2. **TFRA — turn-level friction and repair.** Each `invoke_agent` trace is a turn;
   each turn is scored against *this session's* own median latency, its error
   pattern, and tool-call bursts that look like repair loops. This is the metric
   that cannot be derived from any vendor API, because it needs the trace.
3. **Edit survival**, not just acceptance. 0.4 four-gram + 0.6 no-revert. It is the
   direct counter-metric to the GitClear findings, and the reason acceptance rate
   cannot be gamed here without the gaming showing up.
4. **Four assistants in one schema.** VS Code Copilot, Copilot CLI, Claude Code and
   Claude Cowork all normalize onto one namespace (`Domain/Sem.cs`). The 480★ competitor
   supports one. (Cursor telemetry is detected and normalized too, but is unverified and
   deliberately not counted — see [ADR-002](architecture/ADR-002-cursor-support.md).)
5. **A research layer.** Eight notebooks and a LaTeX paper built into every release.
   Nothing else in this niche has one.

6. **No per-developer dimension, enforced rather than promised.** The cohort filter has
   no developer axis by construction (`Api/CohortFilter.cs`), privacy mode applies a
   k-anonymity floor to every view *and* every outbound payload, and per-session Prometheus
   series are refused under it. Datadog Agent Console leads with "who is using coding agents
   the most"; this cannot answer that question, and tests assert it cannot.

On standards: VS Code Copilot, Claude Code and Codex emit OTel GenAI semantic conventions
natively, and OpenTelemetry graduated in CNCF in May 2026. An earlier version of this
document called that "a standard that had just finished settling" — which was wrong.
Semantic conventions **v1.42.0 (2026-06-12) deprecated the `gen_ai.*` conventions** and
federated them to a separate `open-telemetry/semantic-conventions-genai` repository, with no
stable release yet. The standard did not settle; it split. The honest version is better
anyway: a weekly canary (`.github/workflows/semconv-canary.yml`) diffs the attributes this
project consumes against upstream and opens an issue when they drift. Tracking a moving
standard, and saying so, beats betting on a settled one — and it is checkable.

**One sentence:** *the open-source, self-hosted session-quality scorer with a published,
deterministic formula — and the only one that cannot produce a per-developer ranking.*

## 4. What this project deliberately is not

Stated here so it can be pointed at, and so scope creep has something to fail
against:

- **Not a developer scoreboard.** No per-developer view, and none planned. Every
  researcher who has studied this at scale reaches the same conclusion: individual
  productivity metrics distort behaviour more than they inform. A tool that scores
  sessions can be pointed at people; the answer to that is to refuse the feature,
  not to add it carefully.
- **Not an acceptance-rate optimizer.** Acceptance is 0.20 of the composite and
  permanently paired with edit survival. Anyone maximizing acceptance will watch
  survival fall and the composite with it. That is the design working.
- **Not a billing system.** Cost figures are list-price estimates from a
  configurable sheet, for relative comparison.
- **Not a commercial product.** No open-core split, no paid tier, no telemetry
  phoning home. The Azure judge-agent tier is a deployment option, not a paywall.

Goodhart's law is the standing objection to everything in this repo, and the honest
answer is not that CopilotScope is immune — it is that a single number is always
wrong, which is why the components, the confidence and the per-turn detail are all
exported alongside it.

## 5. Positioning

Category to compete in: **session quality scoring**, not "Copilot dashboard". The second is
crowded and already lost. The first is no longer empty either — DX, Datadog and New Relic all
entered it in mid-2026 (§2) — so the position is not "the only one" but **the only one with
these three properties**.

Three claims, in order, each scoped to survive a reader who has just come from a competitor's
landing page:

1. **Local and private.** The collector runs on your machine, writes to your Postgres, and
   has no callback of any kind. Every commercial alternative is SaaS; New Relic's local-only
   mode was announced as coming later. This is the claim that leads now, because it is the
   one none of them can match today.
2. **A published, deterministic formula.** Six weights, renormalized over the components that
   have data, with a confidence figure exported next to every score — re-derivable by hand and
   diffable between versions. DX scores with an evaluation model, which reads nuance this
   cannot; the trade is auditability, and it is a trade rather than a win.
3. **Cannot rank developers.** Not a policy: no developer dimension exists in the cohort
   filter, privacy mode applies a k-anonymity floor to every view and outbound payload, and
   tests assert both. Datadog leads with "who is using coding agents the most". This is the
   answer to a different question, on purpose.

"Quality, not usage" and "every assistant, not just one" are still true and still matter —
they are just no longer *distinguishing*, because the three entrants do both.

.NET, Blazor and Aspire are implementation details and belong below the fold. The
advertised path is `docker compose up`; a reader should never need to know what the
collector is written in. For teams that already run Prometheus and Grafana, the
`/metrics` endpoint is the door in — quality scores in the stack they already have.

## 6. Distribution

Order matters, because attention is spent once:

1. **Foundation first** — screenshots, green CI, working quickstart, the
   "how *not* to use this" section. A visitor who cannot see the product in ten
   seconds does not come back.
2. **Awesome lists** — `awesome-opentelemetry`, `awesome-copilot`,
   `awesome-ai-devtools`, `awesome-dotnet`. Slow, compounding, no downside.
3. **The adjacent projects, as neighbours rather than rivals.** The
   `copilot-metrics-viewer` and `claude-code-otel` communities are exactly the
   audience, and the pitch is honest: complementary, not competing — they count
   usage, this scores quality.
4. **Data before tool.** "I scored 78 AI sessions and here is the friction
   distribution" travels; "I built a thing" does not. The seeded demo dataset and
   the notebooks make this cheap to produce.
5. **Show HN, last.** Only once 1–3 are done. Expect the top comment to be about
   Goodhart's law and surveillance — the README section answers it before it is
   asked, which turns the predictable attack into a credibility signal.
6. **Use the paper.** It is the one asset nothing else in the niche has. A chart
   from a notebook carries further than a feature list.

## 7. Open items

- GitHub Discussions is not enabled; `CONTRIBUTING.md` currently routes design
  questions to Issues instead.
- Release tags for the two GHCR images have drifted apart (collector reached 1.0.7,
  dashboard 1.0.6).
- Analyzers #1–#3 (G-Eval, SPUR, RAGAS) and the deep variants of #9/#10 need the
  Azure judge agent and remain unimplemented. The README's status table says so
  plainly, and it should stay that way — an honest "not implemented" is worth more
  than an aspirational checkmark.

## 8. Execution status (updated 2026-09-01)

§6 laid out a six-step distribution sequence in July. **None of it has been executed
yet**, and recording that here is the point: a plan whose status is implicit reads as
a plan that is going fine.

| Step | Status |
|---|---|
| 1. Foundation — screenshots, green CI, working quickstart | **In progress.** Screenshots and CI are done. The quickstart snippet omitted its two required env exports until 2026-09-01; the GHCR image set is still incomplete (#54, #56, #57). |
| 2. Awesome lists | Not started |
| 3. Adjacent projects as neighbours | Not started |
| 4. Data before tool (a post with real numbers) | Not started — but see below |
| 5. Show HN | Not started, correctly: it is last |
| 6. Use the paper | Not started |

Step 1 is the gate, and it is nearly closed. The remaining blocker is a tagged release
containing everything in `CHANGELOG.md` `[Unreleased]`, verified from a clean machine
against the published images.

Step 4 changed shape. The strongest available post is no longer "here is a friction
distribution from seeded sessions" — the outcome linkage added in #87 makes "we scored
N real sessions and the score predicts merge/revert behaviour" possible, and that is a
result rather than a demo. It needs real deployments first, which makes steps 1–3 the
prerequisite for the post rather than an alternative to it.

**Adoption, honestly:** 1 star, 0 forks, no issues from anyone outside the project. That
is what an unexecuted launch looks like, not what a rejected product looks like — the
distinction only holds while the launch is still pending.
