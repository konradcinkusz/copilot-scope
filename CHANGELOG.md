# Changelog

Notable changes per release. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

Releases publish four images to GHCR — `ghcr.io/konradcinkusz/copilotscope-collector`,
`-dashboard`, `-agentforge` and `-judgeagent` — plus the research paper PDF as a release asset.

## [Unreleased]

### Added
- **`GOVERNANCE.md` — the "who maintains it?" answer** (#103). The first question anyone asks
  before putting a tool in their telemetry path, and until now unanswerable: one maintainer,
  no stated response posture beyond security reports, and no schema-stability guarantee, so an
  adopter could not know what an upgrade might break. The document names the stable surfaces
  (OTLP ingest and its attribute vocabulary, REST DTO fields, Prometheus family and label names,
  the jsonb snapshot shape, the calibration label schema, `CopilotScope:` config keys) and the
  ones that may move in any release — including, explicitly, the quality score's weights, which
  *will* change when calibration happens, and which is why scores are comparable within a
  version and not across one. It also answers the abandonment scenario head-on rather than
  hoping nobody raises it: the data is in the adopter's own Postgres with documented export
  paths for every table, the licence permits forking permanently, and nothing phones home — a
  self-hosted MIT tool that stops moving keeps working, which is a lower risk than a SaaS vendor
  that goes away with your data. §6 states what a co-maintainer would actually do, in order of
  value, and how commit access works.
- **A GitHub Copilot Metrics archiver, breaking the 28-day window** (#97). GitHub serves 28 days
  of org usage and nothing older; admins have been asking for history since it shipped, and the
  most-used tool in this space had to add a database purely to keep it. `CopilotScope:VendorMetrics`
  (off by default, needs Postgres) polls daily and archives the window indefinitely, keyed
  `(provider, scope, day)` so a re-poll overwrites rather than accumulating 28 duplicates a day
  and a restart costs nothing. **The full response document is stored verbatim** alongside the
  extracted counts — the vendor deletes the original, so a parser that kept only what it
  understood today would silently discard history that cannot be re-fetched. Served at
  `GET /api/vendor/metrics` and exported as `copilotscope_vendor_*` gauges, including "days held
  beyond the vendor's own horizon", with two new provisioned Grafana panels. The connector
  interface is deliberately narrow (fetch a window, return days) so Anthropic's and Cursor's
  admin APIs can follow as one class each. Org/enterprise level only: the GitHub API can be
  asked for a per-developer breakdown and this does not ask. Framed in the payload, the docs and
  the architecture diagram as **context beside the quality score, never instead of it** —
  counting usage still does not tell you whether the tooling helped.
- **An opt-in session labelling flow** (#102). The composite's credibility problem is written
  down in this repo twice over — no calibration has been run, there are no human labels, and the
  product review calls the score "an opinion with a confidence interval, not a measurement" —
  while the machinery that would fix it (quadratic-weighted κ, bootstrap CIs, human-ceiling
  checks) was already built and had nothing to consume. `CopilotScope:Labelling:Enabled` (off by
  default) adds a **Rate this session** panel: a free-text rater handle not tied to telemetry
  identity, band 0–3 per rubric with the anchor text, and a skip option — a skip is a real
  answer and is stored but not exported as a label. `GET /api/labels/export` emits exactly the
  shape `calibration/labels.example.json` uses, pinned by a round-trip test through
  `CalibrationEngine`. **Seeded sessions are excluded by default**: labelling the Seeder's own
  synthetic personas would validate the scoring model against fiction this repository wrote, and
  a report built on that circle is worse than none because it arrives carrying a κ. Overriding
  stamps `-synthetic` into the dataset version. `RubricScale` moved into the Collector, since
  the judge's output, a rater's form and the calibration engine now all have to be reading the
  same sentence. Labels rate sessions, never people — the schema has no field for the developer
  being rated.
- **Import Claude Code's own transcripts — the no-configuration path** (#98). Most developers
  never flip OTEL env vars; the grassroots tools that parse these files built their entire user
  base on that fact. Claude Code already writes every session to
  `~/.claude/projects/**/*.jsonl`, so `tools/CopilotScope.LogImporter` reads them and posts
  first-class scored sessions to a new admin `POST /api/import`. Re-running is idempotent —
  sessions keep Claude Code's own id — and the collector **refuses** to overwrite a session it
  already holds from live telemetry, because the import carries less signal and would silently
  lower its score. Prompt text is not imported unless `--include-content` is passed. The parsing
  handles the two traps in the format: one model response is split across several assistant
  lines (counting blocks rather than `usage` doubles every call count) and tool results arrive
  as *user* messages (counting them as prompts invents turns). Because the importer runs on the
  developer's machine it resolves the actual git remote and normalizes it the way outcome
  linkage does, so imported sessions land in the same repository cohort as live ones rather than
  forming a duplicate. Imported sessions are badged **imported** and carry genuinely lower
  confidence: time-to-first-token, edit decisions and feedback are OTel events the transcript
  does not record, so they are left absent rather than defaulted to zero.
- **Quality-regression alerts and a weekly digest — push, not just pull** (#101). A dashboard
  that must be visited gets abandoned; an output that triggers a decision gets renewed, and
  session scoring is the only thing that can raise a *quality* regression — a vendor usage
  dashboard cannot alert on what it does not measure. `CopilotScope:Alerts` (off by default,
  and the only outbound path in the collector) compares each window against the one before it
  by repository, assistant and model and posts a webhook, as JSON or as the single `text`
  field most chat webhooks render. The precision is the feature: a cohort needs enough
  sessions in **both** windows, session kind is never alerted on, and — the one that matters —
  a score drop that came with a confidence drop is reported as a *changed measurement basis*
  rather than a regression, because the composite renormalizes over the components that have
  data and a cohort that stopped reporting a signal is measured differently, not worse.
  `GET /api/digest` serves the aggregate week with no webhook configured; `POST /api/digest/send`
  (Admin) sends it. `grafana/provisioning/alerting/` provisions two equivalent Grafana rules
  built entirely from gauge ratios — never `rate()` over a family that can decrease — so they
  sidestep the counter-semantics problem in #70 rather than waiting on it, with a test pinning
  that. Both surfaces honour the k-anonymity floor, which matters more here than on a screen
  because the payload leaves the deployment.
- **Team-lead views: windows, cohorts, before/after, export** (#96). The stated buyer — a
  platform lead evaluating assistants for a team — could not answer a single leadership
  question inside the product: no trends, no cohorts, no comparison, no shareable links, no
  export. Both pages now take a time window and a cohort (repository / assistant / model),
  filtered in SQL rather than after the page is fetched. `GET /api/cohorts` rolls a window up
  by each axis; `GET /api/compare` puts two windows side by side for one cohort, defaulting
  the baseline to the equally long window before the current one, and reports its caveats
  (small samples, mismatched window lengths) next to the deltas instead of folding them in.
  Sessions are deep-linkable at `/sessions/{id}`, and Overview's top-session links now
  resolve to the named session instead of dropping the reader on the list. Any filtered
  rollup or comparison exports as CSV — group rows only, no session ids — and the Overview
  page prints to a one-page utilization / impact / cost summary. **No view and no export has
  a per-developer dimension**, which is asserted by tests rather than assumed.
- **Privacy mode: works-council and GDPR controls that are enforced, not documented** (#94).
  The "not for performance reviews" promise was a convention — nothing stopped an operator
  reading one developer's transcripts, and German BetrVG §87(1)(6) grants co-determination
  over any system *capable* of monitoring performance, so intent is not an answer. Under
  `CopilotScope:Privacy` (off by default) identifying attributes are replaced by salted HMAC
  tokens at ingest, prompt/response content is dropped, no view renders below a k-anonymity
  floor of distinct subjects (per-session detail and per-session Prometheus series included),
  and every read and deletion is recorded to an access log with its own Postgres table and
  CSV export. Raw OTLP forwarding is refused unless explicitly allowed, because it relays
  payloads before redaction. `GET /api/privacy` reports what a deployment enforces;
  `docs/PRIVACY.md` carries the Art. 30 data map, retention behaviour and a template
  Betriebsvereinbarung annex.

- **Per-assistant signal coverage is disclosed** (#100). The composite renormalizes over the
  components that have data, which means an 80 from one assistant is not an 80 from another —
  a Claude Code session is scored without feedback or edit survival, so its 80 rests on less
  evidence. The project's own product review flagged this (B2) and noted nothing in the UI
  warned, while "compare assistants before you buy" is the headline use case. There is now a
  coverage matrix in `docs/SIGNAL_COVERAGE.md`, on the in-app Docs page and at
  `GET /api/coverage`, all served from one table in `Domain/EmitterCoverage.cs`; the session
  detail names the components its assistant can never report; and the rail warns when a visible
  list mixes assistants whose scores rest on different evidence.
- **Fixture capture and a semconv drift canary** (#92). `tools/CopilotScope.FixtureCapture` is a
  recording proxy: point an assistant at it, and it writes each real OTLP payload to
  `tests/fixtures/<assistant>/<version>/` while forwarding it upstream unchanged.
  `FixtureGoldenTests` replays whatever is committed there through the real decoder and session
  store, asserting the batch decodes to something and still classifies as its assistant. A weekly
  `semconv canary` workflow checks that upstream still defines every `gen_ai.*` name
  `Domain/Sem.cs` reads, and opens an issue when one disappears — a renamed attribute otherwise
  keeps ingest returning 200 while the counters it feeds go to zero. **No real captures are
  committed yet**: capturing needs a machine running the assistants, so until then the
  multi-assistant claim still rests on hand-built payloads.
- **The judge runs on your own hardware** (#91). `CopilotScope:JudgeAgent:Backend` selects
  `AzureFoundry` (the default, unchanged) or `OpenAiCompatible` — one implementation covering
  Ollama, vLLM, LM Studio and any in-region OpenAI-compatible gateway. Judging is the only
  feature that sends real transcript text anywhere, and the only destination used to be a cloud
  vendor, which locked the five LLM-graded algorithms away from exactly the self-hosted and
  regulated deployments they are most valuable in. Same prompts, rubric and fingerprint — only
  the transport changes. Judge responses now carry `backend`, `model` and `judgePromptVersion`
  provenance, matching what calibration runs already record.
- **Cloud services authenticate to a secured Collector** (#90). `JudgeAgent` and `AgentForge`
  read `CopilotScope:{Service}:CollectorApiKey` and present it as `x-api-key`, falling back to
  `CopilotScope:Ingest:ApiKey`. Both report `collectorAuthConfigured` on `/api/health`.
- **Session history is served from Postgres** (#84). `/api/sessions` and `/api/overview`
  read the database with the live in-memory aggregates layered on top, instead of reading
  the bounded in-memory store alone — a team churned past that cap in hours, after which
  its history vanished from every surface despite being safely persisted. Endpoints take
  `days`/`since`/`until`, `limit` and `offset` and return `{sessions, total, limit, offset,
  durable}`; `/api/sessions/{id}` falls back to Postgres so a link to last week's session
  keeps working. Without Postgres the same paths serve memory and report `durable: false`.
  Retention is configurable (`CopilotScope:History:RetentionDays`, default 0 = keep
  everything) and the percentile baseline is computed over a fixed trailing window rather
  than whatever survived in memory.
- **Per-mode scoring profiles** (#88). Sessions are classified interactive /
  supervised-agent / autonomous from telemetry shape, and `ScoringProfile` supplies the
  weights. Interactive keeps the published v2 weights exactly; autonomous zeroes latency
  and acceptance — nobody waits on a background run's first token, and an agent under
  `acceptEdits` applies its own edits — and moves that weight to friction and reliability.
  Excluded components are still computed and shown as "not scored" with the reason.
- **Delivered-outcome linkage** (#87, opt-in). A HMAC-verified GitHub webhook at
  `POST /api/outcomes/github` records pull-request outcomes (merged, closed, reverted,
  time to first review, time to merge), joined to sessions by repository, branch and time
  with an explicit confidence. Shown beside the score, never folded into it: the score has
  not been validated against outcomes yet, and collecting them is what makes that possible.
  Repository-level only — no author, no reviewer.
- **Scoped collector API keys and dashboard sign-in** (#86). `CopilotScope:Keys` splits the
  single shared secret into Ingest / Read / Admin, so the key every editor holds can no
  longer read transcripts or delete history; `CopilotScope:Dashboard:Auth` adds optional
  viewer/admin sign-in, with transcripts and deletion restricted to admin. Both off by
  default; the legacy single key still grants every scope.
- **Judge calibration (Cohen's κ)** — `src/CopilotScope.JudgeAgent/Calibration/` measures how
  well the judge agrees with human labels, closing the gap the README and
  `JudgeSystemPromptTemplate.txt` have both been stating ("directional, not final… until
  calibration data exists"). Cohen's κ with unweighted, linear- and quadratic-weighted variants,
  each carrying a seeded bootstrap 95% CI; the human panel's agreement with itself is measured
  first as the ceiling, because a judge validated against labels the labellers cannot reproduce
  is validated against noise. Verdicts are `calibrated` / `not-calibrated` / `ceiling-too-low` /
  `insufficient-data` against a configurable threshold (`CopilotScope:JudgeAgent:Calibration`,
  default κ ≥ 0.70 — the repo's own published acceptance criterion). Two endpoints:
  `POST /api/calibration/report` (pure arithmetic, deterministic, no model access — the one CI
  can run) and `POST /api/calibration/run` (grades each session with the live judge first,
  sequential, capped at 200 sessions per run). Labels are versioned JSON in `calibration/`;
  `labels.example.json` is a format template deliberately too small to certify anything. New
  `docs/CALIBRATION.md`. **No calibration has been run** — there are no human labels in the
  repository yet, so no judge score gates anything.
- Judge reports now record `judgePromptVersion`, a hash of `JudgeSystemPromptTemplate.txt`
  derived rather than declared, so a later rubric edit surfaces as a re-baseline instead of a
  silently moved measuring stick.

### Changed
- **Positioning restated for a post-DX/Datadog market** (#99,
  [ADR-003](docs/architecture/ADR-003-positioning.md)). `docs/STRATEGY.md` claimed this was
  "the only open-source tool that turns telemetry from any AI coding assistant into a session
  quality score" and called the category empty. Three commercial entrants shipped it since:
  DX Agent Experience (Atlassian Team '26, May 2026), Datadog Agent Console (DASH, June 2026)
  and New Relic AI Coding Observability (2026-06-23) — the last of which is itself
  open-source, which removes even the fallback claim. A "nobody does this" line that a
  two-minute search refutes would discredit a project whose brand is radical honesty, on a
  page kept in the repo specifically so readers can check the reasoning. The landscape section
  now names all three with dates and treats their arrival as **category validation**: three
  companies with far better market research concluding the problem is real is the strongest
  external evidence the thesis has, and it costs only the "empty niche" framing, which was the
  weakest part of the argument. The claim is re-scoped to what survives all three — open
  source, **self-hosted**, **published deterministic formula**, and **cannot produce a
  per-developer ranking** (enforced by #94 and #96, not merely stated). `docs/COMPARISON.md`
  sets out what each product does, including a section on where the others are simply better.
  The timing claim ("a standard that had just finished settling") is also corrected: semantic
  conventions v1.42.0 deprecated `gen_ai.*` into a separate repository with no stable release,
  so the standard split rather than settled — and the weekly canary from #92 is the honest,
  checkable version of that argument.
- **Cursor is demoted from a supported assistant to unverified** (#93,
  [ADR-002](docs/architecture/ADR-002-cursor-support.md)). "Five assistants" in the README
  included one supported by a `service.name` substring check and a namespace rename, with zero
  Cursor-specific tests and no payload from a real Cursor session ever tested — while
  `docs/STRATEGY.md` said four, with a *different* membership. Radical honesty is this
  project's main competitive virtue, and a support claim the code cannot back costs more than
  the feature is worth. The supported count is now **four** (VS Code Copilot, Copilot CLI,
  Claude Code, Claude Cowork) and both documents agree on the membership as well as the number.
  Cursor is listed separately and labelled unverified in the README, the in-app Docs page,
  `docs/SIGNAL_COVERAGE.md`, `docs/TUTORIAL.md` §6 and `Domain/EmitterCoverage.cs`. The
  speculative setup guidance ("try adding the same VS Code settings … if Cursor exposes an OTLP
  env-var hook") is gone — that was an instruction to guess, published as documentation.
  **Nothing is removed from the ingest path**: anyone already pointing Cursor at the collector
  keeps exactly the behaviour they had, only the claim about it changes. Seeded sessions now
  carry a **demo** badge in the dashboard, since a fabricated Cursor session is what made the
  claim look proven in screenshots. Documentation tests hold both counts and the unverified
  marking in place.

### Fixed
- **The in-app Docs page called four implemented algorithms "not implemented"** (#58). G-Eval,
  SPUR, RAGAS and task-completion detection all ship in `CopilotScope.JudgeAgent` and the
  README's matrix says so; the dashboard's own copy of that matrix still described them as
  unbuilt, and it was also missing delivered-outcome linkage entirely. The two now agree. The
  signal-coverage section added this cycle was also reachable only from one link in the session
  rail — it is in the Docs table of contents now.

### Changed
- **Frustration analysis is now workflow-friction signals, and ships off** (#95). EU AI Act
  Art. 5(1)(f) prohibits workplace emotion recognition outright, so a feature named for
  inferring how a developer feels was the cheapest possible objection to hand a DPO — in the
  segment where this tool is most defensible. The rename is also a correction: the detector
  counts *observed repair events* (re-asking, rephrasing, corrective replies), which is what
  the signal was always useful for. `FrustrationAnalyzer` → `WorkflowFrictionAnalyzer`, and
  the JudgeAgent rubric `deep-frustration` → `deep-friction` (retired ids still resolve, so
  existing calibration labels keep counting). The analyzer no longer runs unless
  `CopilotScope:WorkflowFriction:Enabled` is set, per-message previews that quote prompt text
  need a second opt-in, and the default surface is the team/period rate at `GET /api/friction`.
  Rationale and the Art. 5(1)(f) position statement: `docs/WORKFLOW_FRICTION.md`. The seeder's
  `frustrated` persona is now `repair-loop`, which changes seeded session ids.

### Fixed
- **The cloud tier 401'd against any secured Collector** (#90). `infra/main.bicep` makes the
  Collector's ingest key a required parameter, so every Azure deployment gates `/api` — and both
  cloud services sent no key at all, so the judge and persona cohorts only ever worked against an
  open dev-mode Collector. Their inbound key checks also moved to a constant-time comparison
  (they still used `==`, which leaks the key under timing analysis); that comparison now lives
  once in `CopilotScope.ServiceDefaults` instead of in three copies that had already drifted.
- **Cross-developer session contamination behind a shared collector** (#85). Process- and
  service-scoped resource fingerprints are unique only within one machine, so two
  developers running the same assistant had their identity-less metrics merged into one
  conversation. Those fingerprint forms are now scoped by host (or by the source
  connection), and `copilotscope_hostless_signals_total` reports when neither is available.
- **Trimming a session could destroy its persisted snapshot** (#84). Late telemetry for an
  evicted session recreated it as an empty aggregate, and the next write-behind flush wrote
  that over the stored row. Evictions are now tracked and the stored snapshot is merged back
  before flushing. Trim also releases the evicted session's trace mappings, which previously
  outlived it.
- **Permission-mode auto-accepts inflated the acceptance score** (#88). Claude Code's
  `tool_decision` events carry a `source`; under `acceptEdits` or `bypassPermissions` that
  is `config`, not a human. Those now count as `EditsAutoAccepted` — reported, never scored.
- **README quick start could not work as written** (#89). The headline snippet omitted the
  `COPILOTSCOPE_API_KEY` and `POSTGRES_PASSWORD` exports that `docker-compose.ghcr.yml`
  hard-requires via `${VAR:?}` guards, so a first-time reader's very first command failed.

### Changed
- **`GET /api/sessions` returns a page object**, `{sessions, total, limit, offset, durable}`,
  rather than a bare array (#84). The dashboard client is updated; an external consumer
  reading the array directly needs a one-line change.
- **Retargeted every project to `net10.0`** and aligned the container base images
  (`sdk:10.0` / `aspnet:10.0`) with the TFM, so the published GHCR images start.
  `Aspire.AppHost.Sdk` aligned with `Aspire.Hosting.*` (13.4.6). CI builds on a
  single 10.0 SDK; `build-containers.yml` now smoke-tests each image before publish.
- **Security:** the whole `/api` group is gated deny-by-default by the ingest key
  (constant-time compare), so the query API and the destructive `DELETE` are no
  longer reachable unauthenticated when a key is set; `/api/health` stays open as a
  liveness probe. Decoded OTLP payloads are bounded (compression-bomb guard) and
  `/admin/seed` enforces the `seed-` id prefix server-side.
- **Secrets:** removed the committed `dev-secret-123` / `copilot-dev` defaults; the
  compose files require `COPILOTSCOPE_API_KEY` + `POSTGRES_PASSWORD` (no default),
  the setup scripts generate them into a gitignored `.env`, and a `gitleaks` job runs
  in CI.
- **Self-observability:** new `CopilotScope.ServiceDefaults` (OTel, health `/health`
  + `/alive`, service discovery, HTTP resilience) is called by all four services;
  the AppHost health-checks every resource.
- **Dashboard UX:** defaults to the Basic view with the view switcher in the topbar;
  score colour is now the absolute grade consistently (with grade text in the rail
  for colour-independent reading); the session rail no longer reshuffles under the
  cursor between polls.

### Added
- **Prometheus scrape endpoint** (`GET /metrics`) exporting the *computed* signals,
  not just usage: composite quality score and confidence, the six weighted score
  components, edit survival, TTFT percentiles, token and cost breakdowns, edit
  outcomes and feedback — every family labelled by `emitter`, so the four supported
  assistants stay distinguishable. Written by hand in the text exposition format,
  so the collector keeps its single NuGet dependency.
- Aggregates are exported as `_sum`/`_count` pairs so PromQL rollups over any label
  subset stay arithmetically correct.
- Per-session series (`session=` label) behind `CopilotScope:Prometheus:PerSession`,
  off by default and capped by `MaxSessionSeries`; the overflow is reported as
  `copilotscope_session_series_dropped` rather than silently inflating cardinality.
- `docker-compose.grafana.yml` — the full stack plus Prometheus and Grafana with a
  provisioned datasource and dashboard (`grafana/dashboards/copilotscope.json`).
- CI workflow building the solution and running the test suite on every pull request.
- Screenshots of the dashboard and the Grafana view in `docs/img/`.
- README section stating what CopilotScope must *not* be used for — performance
  reviews, acceptance-rate targets, single-number verdicts.

### Fixed
- Seeded "frustrated" persona sessions produced no strong-marker or rephrasing
  signal on short conversations: the final-turn fixtures were indexed by absolute
  turn number, so they landed on the mild corrective pair and `FrustrationAnalyzer`
  only ever reported "mild friction". Now indexed by position within the final pair.
- Broken architecture diagram in the README — `architecture.svg` sat in the repo
  root while the README (and the Pages site) referenced `docs/architecture.svg`.
- `CONTRIBUTING.md` referred to a `main` branch that does not exist (default is
  `master`) and pointed at GitHub Discussions, which is not enabled.
- Documented that the **9.0 SDK** is required: on the 8.0 SDK the AppHost fails with
  `NETSDK1147: the following workloads must be installed: aspire`. Everything still
  targets `net8.0`.
- Removed the stale "GHCR packages start private" note — both packages are public.

## [1.0.7] — 2026-07-20
- GitHub Pages deployment workflow (#15)
- Maturity progression timeline on the sessions view (#16)
- Basic view mode hiding advanced session details (#17)

## [1.0.6] — 2026-07-19
- Landing page and documentation website (#14)

## [1.0.5] — 2026-07-19
- Fixed horizontal overflow breaking the Docs page layout (#12)
- Walkthrough and practice exercises for the quality engine (#13)

## [1.0.4] — 2026-07-19
- Removed an unnecessary Razor code block in the Home component (#11)

## [1.0.3] — 2026-07-19
- Basic/Advanced/Full view modes on session detail (#8)
- Per-repo session normalization for quality percentile ranking (#9)
- Worked examples throughout the quality measurement framework (#10)

## [1.0.2] — 2026-07-18
- Expanded edit survival analysis: mechanics, examples, sensitivity (#7)
- Razor syntax cleanup (#6)

## [1.0.1] — 2026-07-18
- **Claude Code and Cursor support** (#3)
- Conversation popup with turn analysis (#2)
- Quality Measurement Framework paper (#4) and its automated PDF build (#5)

## [1.0.0] — 2026-07-13
- First release: OTLP/HTTP ingest with an in-repo protobuf decoder, session
  aggregation, the composite quality engine, TFRA turn analysis, Postgres
  persistence, and the Blazor dashboard, orchestrated with .NET Aspire.

[Unreleased]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.7...HEAD
[1.0.7]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.6...v1.0.7
[1.0.6]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.5...v1.0.6
[1.0.5]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/konradcinkusz/copilotscope/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/konradcinkusz/copilotscope/releases/tag/v1.0.0
