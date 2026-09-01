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

Two things stand out. First, the open-source field is entirely **usage
dashboards** — seats, tokens, cost, acceptance rate. Second, the commercial
platforms work from git and ticket metadata, which means they can tell you a PR
took three days but not that turn 23 burned four tool calls in a repair loop.

Nobody in open source scores the *quality of a session*.

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
   Cursor all normalize onto one namespace (`Domain/Sem.cs`). The 480★ competitor
   supports one.
5. **A research layer.** Eight notebooks and a LaTeX paper built into every release.
   Nothing else in this niche has one.

Timing helps: VS Code Copilot, Claude Code and Codex now emit OTel GenAI semantic
conventions natively, and OpenTelemetry graduated in CNCF in May 2026. An
OTLP-ingesting design landed on the right side of a standard that had just
finished settling.

**One sentence:** *the only open-source tool that turns telemetry from any AI
coding assistant into a session quality score, instead of a usage count.*

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

Category to compete in: **session quality scoring**, not "Copilot dashboard". The
second is crowded and already lost; the first is empty and defensible by the paper.

Three claims, in order:

1. **Quality, not usage.** The score, the components, the turn where it went wrong.
2. **Every assistant, not just one.** Copilot, Claude Code, Cursor, CLI.
3. **Local and private.** No SDK, no account, nothing leaves the machine.

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
