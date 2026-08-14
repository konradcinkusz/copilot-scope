# CopilotScope — Product & Architecture Review (2026-08)

> Reviewed against the working tree at `claude/copilot-scope-review-wxvi5w`
> (branched from `master` @ `1aa8c0a`), 2026-08-14.
>
> This is a **second-pass, whole-product review**. It does not replace
> [`ARCHITECTURE_REVIEW.md`](ARCHITECTURE_REVIEW.md) — it re-verifies it against the
> current tree (with two factual corrections), extends it to the two axes that review
> left untouched — **business viability** and **UI/UX** — and cross-checks the repo
> against the estate constitution
> ([`architecture-standards/docs/architecture/00-REFERENCE-ARCHITECTURE.md`](https://github.com/konradcinkusz/architecture-standards/blob/master/docs/architecture/00-REFERENCE-ARCHITECTURE.md))
> principle by principle, plus the operational guides (`REPO-BASELINE`,
> `TESTING-STRATEGY`, `SECURITY-REVIEW`).
>
> Method note (per `SECURITY-REVIEW.md` §1): the security section is a static code
> review, not a penetration test. Every code claim is anchored to `file:line`.

---

## 0. TL;DR — is this fit to deploy anywhere?

**As a local-first, single-user / single-team OSS tool run on a trusted network or a
laptop: yes, today.** It builds, it runs, ingest is genuinely robust, and the app was
exercised live for this review (12 seeded sessions, in-memory collector on `:4318`,
dashboard on `:5200`) with **zero exceptions in either service's logs** across build,
seed, a dozen navigations, the transcript modal and view-mode switches.

**As anything reachable by people you do not trust — the Azure Container Apps path in
`infra/main.bicep` with `external: true` — no, not until the auth model is fixed.** The
query and delete API is unauthenticated while ingest is not, and `DELETE` in particular
has **no key check at all** (§S1). With content capture on, that means: anyone who can
reach the port reads every captured conversation and can wipe the whole team's history,
but cannot write telemetry. That inversion is a blocker for any shared deployment.

**As the container images published to GHCR on every release: no — they do not start.**
All four Dockerfiles are `sdk:10.0`/`aspnet:10.0` while every project targets `net8.0`;
default roll-forward does not cross a major, so the published images fail at startup
(§A-P6). This is the single highest-severity, lowest-effort fix in the repo.

**As a commercial product: not yet, and not without a decision the repo hasn't made.**
The market gap it targets is real and genuinely unoccupied, but the headline metric has
no external validation and the one strong buying scenario (compare assistants) is
undercut by a cross-assistant comparability problem the product doesn't disclose (§B).

Scores: **architecture alignment** — solid core, three structural gaps open since the
last review. **Business thesis** — 4/10 as a business, 7/10 as an OSS research tool.
**UX** — the reason it "feels unusable" is diagnosable and mostly cheap to fix; the good
view already exists in the code, it just isn't the default.

---

## 1. What the last review got right, and two corrections

`ARCHITECTURE_REVIEW.md` is **~90% still accurate** — the code has not changed since it
was written (the only commit after its base is the review document itself). Its
strengths section (zero-dependency ingest, edge normalization, the analyzer plugin
pipeline, persistence-that-cannot-block-ingest, documented non-goals) all verify against
the current tree and are repeated below where relevant. Two claims need correcting:

- **§3.3 is factually wrong.** It states *"`infra/main.bicep` does not set
  `scale.minReplicas`/`maxReplicas`"* and recommends adding them. In fact
  `infra/main.bicep:81` pins `scale: { minReplicas: 1, maxReplicas: 1 }` with a comment,
  and has since the file's first commit (`git show 7d135c2:infra/main.bicep`). The
  underlying concern — a singleton `SessionStore` that cannot be horizontally scaled — is
  real, but the recommended fix was already shipped. Only the second half survives:
  document the single-instance constraint in the README's deployment table.

- **§2.3 / README:233 describe a mechanism that does not exist.** Both say cloud-only
  analyzers "implement the same interface and register conditionally, degrading to a
  no-data result." In the current tree the collector registers exactly five *local*
  analyzers unconditionally (`Collector/Program.cs:17-21`) and the cloud judge is a
  **separate HTTP service** (`JudgeAgent`), by deliberate design
  (`docs/JUDGE_AGENT.md:32`). Per the stale-doc corollary (P14) this is itself a finding.
  Fix the README and, ideally, the constitution's P10 example, which inherits the same
  wording.

One escalation the last review under-stated: it grouped the open read/delete API under a
single §3.2. The `DELETE` case is worse than "read is open" — see §S1.

---

## 2. Architecture alignment (P1–P15)

Full per-principle evidence is long; the verdict table is the load-bearing part.

| # | Principle | Verdict | Key evidence |
|---|---|---|---|
| P1 | AppHost is the composition root (dev) | **PARTIAL** | ports pinned correctly (`AppHost/Program.cs:19-24`); but **no `WithHttpHealthCheck` on any resource**, and `Aspire.AppHost.Sdk` 9.3.0 vs `Aspire.Hosting.*` 13.4.6 — 4 majors apart (`AppHost.csproj:3,15-16`), README still says "9.3" |
| P2 (+P2a) | Shared *kernel*, not shared *domain* | **NON-COMPLIANT** | no `ServiceDefaults`, no `Contracts`; agents take a `ProjectReference` on the whole Collector (`AgentForge.csproj:14`, `JudgeAgent.csproj:15`); three copies of `CollectorClient`; no service calls `AddServiceDefaults()` |
| P3 | Service per bounded context; DB per service | **COMPLIANT** | `Npgsql` only in Collector; all cross-context reads over HTTP; the modular monolith is explicitly blessed by the constitution |
| P4 | Provider-portable persistence, migrated not "ensured" | **PARTIAL** | `CREATE TABLE IF NOT EXISTS` (`SessionRepository.cs:16-32`); no provider switch; correct in-memory fallback. The jsonb-snapshot exception is defensible but **undocumented** |
| P5 | Config via env; secrets via platform | **PARTIAL** | model is clean, committed secrets are empty; but **no secret scanner in CI** (P5 requires one), `dev-secret-123` committed in 3 compose files + prometheus.yml + setup scripts, and the open read/delete API |
| P6 | One container/service, multi-stage Dockerfile | **NON-COMPLIANT** | **all four images `aspnet:10.0` on `net8.0` → dead on startup**; no restore layer, no `USER`, no `LABEL ...source`, `HEALTHCHECK` uses `wget` (likely absent in base image) |
| P7 | Fly.io target, cost-shaped topology | **NON-COMPLIANT** | zero `fly.toml`; `infra/main.bicep` deploys 1 of 4 services (collector only, no Postgres, no dashboard); no ADR making ACA a deliberate choice |
| P8 | Optional deps degrade, don't fail startup | **PARTIAL** | core is exemplary (Postgres/forwarding/Prometheus all optional with real fallbacks); but cloud-agent misconfig throws a raw HTTP 500 instead of the "clear not-configured error" the compose file promises (`AzureFoundryJudgeChatClient.cs:25-30`) |
| P9 | Program.cs is a manifest; wiring in extensions | **PARTIAL** | **zero `*Extensions` classes in `src/`**; the API-key check is copy-pasted **5×** across 3 services; domain logic (per-repo score pools, seeding) lives in endpoint bodies |
| P10 | Interface + registration, not inheritance | **COMPLIANT** | `IInsightAnalyzer` + 5 one-line registrations, failure isolated to a `no-data` report; zero abstract classes; this is the constitution's own source example, and it holds |
| P11 | Anti-corruption at the edge | **COMPLIANT** | `Domain/Sem.cs` + `Domain/ClaudeCode.cs` fold three vendor dialects into one model; boundary is airtight (grep confirms Quality/Api/Persistence never touch vendor namespaces) |
| P12 | Tag-driven CI/CD, change detection, ordered deploy | **PARTIAL** | the "build" half is exemplary (`build-containers.yml`, cited by the constitution); the "deploy" half doesn't exist, there's no change detection or layer cache, and **the pipeline publishes images that can't start with no smoke test to catch it** |
| P13 | Test at the layer that has the logic | **PARTIAL** | 90 genuinely good unit tests, no theatre; but **the Dashboard has zero tests**, there's no real-Postgres integration test, and the collector's HTTP layer (where the auth bugs live) is untested |
| P14 | Docs record reasoning | **PARTIAL** | reasoning docs are above average; but build-critical comments **actively lie** (`Dockerfile:4-11` describes the exact failure the `FROM` line below it causes), README's Aspire version is 3 majors stale, CHANGELOG says "two images" for four |
| P15 | Observability is a build-time decision | **NON-COMPLIANT** | **an observability product that emits no telemetry about itself**: zero `OpenTelemetry.*` packages, no `ActivitySource`/`Meter`, Dashboard has no health endpoint, no resilience on outbound HTTP to Azure AI Foundry |

**Compliant: P3, P10, P11.** These three are the load-bearing decisions worth carrying
into other repos — and two of them (P10, P11) are already the constitution's source
examples. **Non-compliant: P2, P6, P7, P15.** Three of those four (P2, P6, P15) are
already logged as open deviations in the constitution's §3a and **nothing has moved since
the last review**.

### The three structural gaps, in priority order

1. **P6 — the four GHCR images do not start.** `aspnet:10.0` + `net8.0` = framework-not-
   found at boot. The Dependabot guard (`dependabot.yml:27-31`) exists but was defeated by
   PRs #63/#64 that were already open when the ignore landed. Highest severity, smallest
   fix: retarget to `net10.0` (and update `ci.yml`) or pin images back to 8.0, delete the
   false comment, and **add a `docker run` + `/api/health` smoke test to
   `build-containers.yml`** so this class of failure can never ship silently again.

2. **P2/P15 — no shared kernel in an observability product.** No `ServiceDefaults`, no
   self-instrumentation, triple-duplicated `CollectorClient`, agents compile-coupled to
   the entire Collector. This is one fix (`CopilotScope.ServiceDefaults` +
   `CopilotScope.Contracts`, alignment actions #5/#6 in the last review) that closes P2,
   P15, most of P9, and the awkward Docker source-copy at once. The irony is also a
   product argument: the collector could dogfood its own ingest.

3. **P7/P12 — no compliant deployment path.** Zero Fly.io (the constitution's decision),
   and the Azure story is a Bicep that deploys the collector alone, without persistence or
   dashboard. The system advertises two clouds and has neither working, with no ADR. Pick
   one, finish it, and record the decision.

---

## 3. Security (S) — the deployment blocker

The good news first (`SECURITY-REVIEW.md` §1 requires positive findings): `SECURITY.md`
is an honest residual-risk register; `/metrics` and `/api/admin/seed` are key-gated; the
decoder is bounded where it matters (transcript 100×4 000, LRU 200 sessions); auth runs
*before* the body is read on ingest; there is no CORS wildcard; the Bicep uses
`@secure()`. AgentForge and JudgeAgent gate **every** endpoint. The collector does not.

| # | Finding | Where | Severity |
|---|---|---|---|
| **S1** | **`DELETE /api/sessions/{id}` has no key check at all** — even when a key is set. Fabricating data needs the key; **destroying** it does not. A shared-host deployment (the repo's own documented mode) lets anyone on the network `GET /api/sessions` then loop `DELETE` and wipe the team's history. | `Program.cs:206-217` (compare the gate at `:66-77`, `:232-237`) | **P1 / blocker** |
| S2 | Transcript read without auth when a key is set — with content capture on, that's source code and pasted secrets. The one key in the system does not protect the most sensitive read. | `Program.cs:191-204` | High |
| S3 | Decompression bomb: `GZipStream → MemoryStream` with no cap; Kestrel limits only the *compressed* size (~28 MB), gzip ~1000:1. With the default empty key, that's a pre-auth DoS. | `Program.cs:96-105` | High |
| S4 | Seed endpoint does not enforce the `seed-` prefix server-side — it `Put`s any id from the request, so a key holder (or anyone, with an empty key) can **overwrite real sessions**. The "namespaced" comment is a Seeder convention, not a server guarantee. | `Program.cs:248-257` vs comment `:226-227` | Medium |
| S5 | Key comparison is not constant-time (`provided != ingestApiKey`); the auth check is inlined 3× in the collector — which is *why* DELETE lacks it. One middleware fixes both. | `Program.cs:70,236,288` | Low |
| S6 | Committed known default key `dev-secret-123` in the pull-and-run path (`docker-compose.ghcr.yml:27`) becomes a production key for anyone who doesn't change it. No secret scanner to catch it. | 5 files | Medium |

**The single most important change before any shared deployment:** replace the three
inlined key checks with one endpoint filter / middleware applied deny-by-default to the
whole `/api` group, with `DELETE` behind the strongest credential — and add a `gitleaks`
job to CI.

---

## 4. Business viability (B)

**The gap is real and genuinely unoccupied.** A market sweep (GitHub native metrics,
Cursor/Anthropic analytics, the OSS Copilot-metrics dashboards, ccusage, the
claude-code-otel Grafana stacks, and the engineering-intelligence platforms DX / Faros /
Jellyfish / LinearB / Swarmia) found **nobody** computing a synthetic **session-level
quality score from OTel telemetry, locally, cross-vendor, without an account.** The
market splits cleanly into usage/cost tooling (no quality synthesis) and
engineering-intelligence platforms (quality, but *downstream* from git/PRs, days later,
SaaS, per-seat, data leaves the box). CopilotScope's exact intersection is empty.

**But the thesis has four cracks, and they matter:**

- **B1 — no ground truth.** The weights (0.25/0.20/0.20/0.15/0.10/0.10) are heuristically
  justified, not calibrated against any external outcome (task success, PR merge, human
  rating). The only path to validation — the JudgeAgent — is **cloud-only on Azure**,
  which contradicts the "nothing leaves the box" positioning. The composite is, for now,
  *an opinion with a confidence interval*, not a measurement.
- **B2 — the headline buying scenario is undercut.** Edit-survival and thumbs feedback
  exist only in the Copilot dialect (`Sem.cs:59-61`); Claude Code has neither. Because the
  composite renormalizes over available components (`QualityEngine.cs:123-124`), **an 80
  for a Claude Code session and an 80 for a Copilot session are different numbers** — which
  quietly undermines "compare assistants before you buy." Nothing in the UI warns about
  cross-assistant comparison.
- **B3 — the niche is a five-way intersection.** ≥2 assistants used formally × self-hosted
  Prometheus × a local-only requirement × a platform team that can push flags fleet-wide ×
  belief that session telemetry answers the buying question better than a DX pilot. That's
  realistically hundreds, not thousands, of organizations — and there is no revenue model
  by the project's own choice (`docs/STRATEGY.md:104`).
- **B4 — both flanks are closing.** GitHub shipped an "impact" dashboard (2026-07-22) and
  cites 88% character-retention (a survival analogue); Anthropic and Cursor have analytics
  APIs. Vendors won't build *cross-vendor* (conflict of interest) — but the horizontal
  players (DX, Faros) could add OTel GenAI ingest and close the gap in a quarter.

**Verdict: 4/10 as a business, 7/10 as an OSS research tool.** The problem is real and
freshly validated by the best available sources (DORA's AI productivity paradox, the METR
RCT where experienced devs were 19% slower while believing they were faster, the
mainstream critique of acceptance-rate). The position is genuinely empty. But a business
needs a buyer with a proven need for *this class of data*, a revenue path, and a
defensible moat — and here the buyer's need is unproven, there is no revenue by design,
the key metric has no ground truth, and comparability (the core of the one strong buying
case) is technically shaky. As an open-source instrument for platform-team bake-offs,
local-only regulated shops, and AI-productivity research (the 8 notebooks + LaTeX paper
are a real distribution asset), it is coherent and valuable.

**What would raise the business score:** (1) public validation of the score against *any*
outcome, even on a seeded corpus with human labels; (2) an explicit cross-assistant
comparability matrix in the UI; (3) *technical*, not just declarative, anti-surveillance
guarantees (k-anonymous aggregation, hashed resource attributes); (4) 3–5 documented
bake-off deployments.

---

## 5. UI/UX — why it "feels unusable", and the cheap fixes

The author's own framing — "just running it, reading it and interpreting the results
makes it practically unusable" — is correct, and the root cause is specific: **the
dashboard is built as the algorithm author's control panel, not a user's tool.** The
default view is `ViewMode.Advanced` (`Home.razor.cs:29`), so the first screen anyone sees
is a firehose — roughly **350–400 discrete information elements at once**, requiring
knowledge of **~20 concepts** just to parse (composite, grade, confidence, percentile, σ,
w=, the six component names, TFRA, repair loop, TTFT p50/p95, net compute, …). The whole
product vocabulary is 35+ terms. This was confirmed live: the seeded showcase session
opens on a wall of monospace numbers with `σ` and `w=0.25` and 31 TFRA rows.

Three compounding causes:

1. **The good view exists but isn't the default.** The Basic view (`Home.razor:148-218`)
   is genuinely excellent — a plain-language verdict ("Usable, but friction was noticeable
   — worth a look at the weak factor below"), the single weakest and strongest factor, and
   five big numbers. It answers "was it good and what do I fix" in three sentences. But
   Advanced is the default, and the switcher only renders *after* you pick a session, so a
   new user never learns Basic exists.
2. **The main number has no single meaning.** Score 65.6 is colored *relatively* by
   percentile (green `grade-good` at the 60th pct) yet sits on an *absolute* VU-meter whose
   segments are red/orange for 65/100 — the same number says "good" and "bad" at once, and
   a `Normalize by repo` checkbox silently flips the meaning of every color in the app.
   Grade thresholds live only in `/docs`.
3. **The interface won't hold still.** A full 2 s refresh (`Home.razor.cs:169-181`)
   re-sorts the session rail by `LastSeen` under the cursor, rewrites `Ago()` widths every
   tick, and animates `.vu-seg` — the screen shimmers exactly while you try to read it.
   (Measured stable on static data, but reorders the moment a session is live.)

The explanatory layer the product needs already exists — the tile tooltips are written in
interpretation language ("Under ~1 s feels instant; over 2 s breaks flow") and `/docs` is
unusually honest — but it is **physically separated from the data**: a 390-line separate
route with no contextual link from any panel.

**Also confirmed live** (screenshots taken this session): the empty state tells you to
"pick one from the list on the left" when the list is empty (~85% of the screen is black
void, the real instruction is small text in the corner, and the Seeder — the most
effective onboarding tool — isn't mentioned there); the session counts disagree
("12 sessions" topbar vs "SESSIONS 11" rail, an unexplained Internal filter); the
documented onboarding (README: ".NET 9 + Docker") is heavier than reality (two
`dotnet run` on SDK 8, no container — a path you have to discover from the code).

**Top fixes, by impact/cost** (the full top-10 with `file:line` targets is in the working
notes; these five are the leverage):

1. **Default to Basic + move the switcher to the topbar** (`Home.razor.cs:29`;
   `Home.razor:109-114`). Minutes of work; turns first contact from firehose into a verdict.
2. **One color semantics for the score** — drop the double encoding, show percentile as a
   separate labeled element ("better than 6/10 sessions in this repo"), add a text grade
   next to the number (fixes the color-only accessibility problem too; adjacent grade
   classes currently contrast **1.10:1**).
3. **Verdict + recommendation on top of every view**, with the weakest-component detail
   linking to the Tools/Errors panel three screens below. Closes "the dashboard never says
   what to do."
4. **Stabilize the poll** — freeze rail order between polls, recompute `Ago()` every 15–30 s,
   suppress transitions during data patches, add "updated Ns ago" + a pause control.
5. **Contextual help instead of a trip to /docs** — replicate the `data-tip` pattern onto
   the quality components, TFRA findings and percentile bar; the content is already written
   in `Docs.razor`, it's copy-shrink-paste.

**Keep:** the Basic view as a concept, the interpretation-language tooltips, the honest
`/docs`, the a11y foundations (`role="radiogroup"`, `aria-live`, `prefers-reduced-motion`,
Escape-closes-modal), the two-step delete confirm, and the "no data ≠ zero" distinction.

---

## 6. Reverse direction — what to extract *into* the standards

The review of copilot-scope against the standards has a mirror: several patterns here are
generic and **not yet** in the constitution or its guides. These are proposed as
additions to `architecture-standards` (full drafts in that repo's companion branch). Top
candidates:

1. **Prometheus exposition discipline** (`Api/PrometheusExporter.cs`) — cardinality as a
   budget, opt-in per-entity labels with caps and a `_dropped` counter, `_sum`/`_count`
   over pre-averaged gauges, "quantiles of quantiles don't compose", locale-safe format.
   P15 covers the *emitter* side only; nothing covers exposition. → new guide
   `METRICS-EXPOSITION.md`.
2. **Write-behind snapshot persistence** (`Persistence/PersistenceWriter.cs`) — dirty-set +
   debounce, rehydrate-on-start with a cap, ghost-delete consistency, DB-outage-degrades.
   A legitimate alternative to P4's EF+migrations that P4 currently seems to forbid. → new
   guide or a P4 amendment legalizing the jsonb-snapshot variant.
3. **Demo-data discipline** (`Seeder` + `TelemetryGen`) — seed *through* the running
   service's API (never beside it), namespace-prefix ownership so reset only clears its
   own, personas that tell a story, two generator tiers (protocol-true vs API-level). → new
   guide `DEMO-DATA-AND-SEEDING.md`.
4. **Metric ethics / anti-Goodhart** (`README.md` §"How not to use") — counter-metric for
   every pressurable metric, confidence beside every score, human-emotion heuristics are
   report-only, the unit is the session never the person, enforced in *architecture* (no
   per-developer view) not just policy. P14 cites this as good *writing*; the *rules* live
   nowhere. → short guide `METRIC-ETHICS.md`.
5. **Pluggable report contract** (`Quality/Insights.cs`) — P10 covers the registration
   mechanics; the value ("new algorithm = zero UI work") lives in the normalized output
   record + generic renderer + fail-soft pipeline. → section in `SERVICE-API-PATTERNS.md`
   + a one-line P10 fix (the cloud-analyzer example is now a separate service).

Also worth a paragraph each: fire-and-forget forwarding with an explicit drop policy;
client-enablement scripts that document the vendor dialect (including variables that
*don't* exist); the public GHCR + one-curl-compose delivery shape; the dependency-budget
column in the README; and research-artifacts-in-repo (notebooks + thesis topics numbered
against the code).

---

## 7. Alignment actions (supersedes and extends `ARCHITECTURE_REVIEW.md` §5)

Ordered so each step is independently shippable. **S / M** = small / medium effort.

> **Implementation status (2026-08-14).** A first pass landed on branch
> `claude/copilot-scope-review-wxvi5w`: actions **1, 2, 3, 4, 5, 7, 8, 9** are done, and
> **6** is done for its ServiceDefaults half (OTel self-instrumentation, `/health` +
> `/alive`, discovery, resilience, AppHost health checks — closing P15/P9), plus the
> net10.0 retarget throughout. **10** is recorded as
> [`ADR-001-deployment-target.md`](ADR-001-deployment-target.md).
>
> **Deferred — the `Contracts` half of action 6.** Decoupling the agents from the whole
> Collector cannot be done cheaply: `SessionDetailDto` embeds real domain/quality types
> (`QualityReport`, `SessionEvent`, `TurnAnalysis`, `InsightReport`, `TranscriptEntry`),
> not flat wire DTOs, so a drift-safe `CopilotScope.Contracts` shared by both sides means
> relocating a large slice of the collector's public surface (touching the scoring engine
> and most of the 93 tests). The only cheap alternative — giving the agents their own DTO
> copies, Dashboard-style — reintroduces the drift the author deliberately engineered out.
> This is a deliberate design trade-off worth its own focused PR, not a rushed change; the
> agent Dockerfiles keep copying the Collector source until it lands. **11** landed for its
> two cheapest, highest-value tiers: a `WebApplicationFactory` HTTP-layer suite over the real
> collector pipeline (the auth gate, the gated `DELETE`, ingest, and the `seed-` prefix
> enforcement) and unit tests for the Dashboard's `ChatMessageParser` — the two coverage gaps
> the review named — taking the suite from 93 to 113 tests. The one remaining piece is the
> real-Postgres integration test (`SessionRepository`/`PersistenceWriter` via Testcontainers),
> left as follow-up because it needs a container the build sandbox can't reliably provide.

| # | Action | Axis | Sev | Effort |
|---|---|---|---|---|
| 1 | Reconcile Dockerfile base images with the TFM; delete the false comment; **add a smoke test to `build-containers.yml`** | Arch P6/P12 | HIGH | S |
| 2 | Deny-by-default auth on the whole `/api` group via one middleware; **`DELETE` behind the strongest key**; constant-time compare | Sec S1/S2/S5 | HIGH | S |
| 3 | Add a `gitleaks` job to CI + generate (not hardcode) the setup key; `.env.example` | Sec S6 / P5 | HIGH | S |
| 4 | Cap decompressed body size; enforce `seed-` prefix server-side | Sec S3/S4 | MED | S |
| 5 | Default the dashboard to Basic + move the view switcher to the topbar | UX | HIGH | S |
| 6 | Add `CopilotScope.ServiceDefaults` (OTel/health/discovery/resilience) + `Contracts`; call from all four services; `WithHttpHealthCheck` in AppHost; drop the Collector source-copy from agent Dockerfiles | Arch P2/P9/P15 | MED | M |
| 7 | Align `Aspire.AppHost.Sdk` with `Aspire.Hosting.*`; fix README/CHANGELOG stale versions | Arch P1/P14 | MED | S |
| 8 | One color semantics for the score + text grade in the rail; contextual `/docs` tooltips on components/TFRA | UX | MED | M |
| 9 | Stabilize the 2 s poll (freeze order, throttle `Ago()`, suppress transitions) | UX | MED | S |
| 10 | Decide the cloud target (Fly.io per the constitution, or finish the ACA Bicep) and record it as an ADR; correct §3.3/§2.3 of the prior review | Arch P7/P14 | LOW | M |
| 11 | Dashboard tests (`ChatMessageParser`) + one real-Postgres integration test + a WebApplicationFactory test for the collector's auth/gzip/seed paths | Arch P13 | MED | M |

Items 1–4 gate any shared deployment. Item 5 is the biggest UX win for the smallest
change. Item 6 is the one structural refactor that closes four principles at once.

---

## 8. Cross-cutting scorecard

| Concern | State | Note |
|---|---|---|
| Ingest robustness | **Strong** | zero-dependency decoder, bounded, in-memory fallback; ran live with zero exceptions |
| Extensibility (P10/P11) | **Strong** | plugin pipeline + edge normalization are estate-reference examples |
| Auth model | **Blocking for shared deploy** | open read, **unauthenticated DELETE** (S1) |
| Self-observability (P15) | **Absent** | an observability product emitting nothing about itself |
| Container images | **Broken** | `aspnet:10.0` on `net8.0` — don't start |
| Deployment path | **None compliant** | no Fly.io; ACA Bicep deploys 1 of 4 services |
| Tests | **Good-but-partial** | 90 real unit tests; Dashboard + HTTP layer + real-DB uncovered |
| Docs reasoning | **Above average** | but build-critical comments are stale/false |
| Business thesis | **Real gap, unproven buyer** | 4/10 business, 7/10 OSS research tool |
| UX | **The stated problem** | firehose default; the good view exists but is hidden |

---

*This document is a review, not a set of applied changes. It records reasoning and cites
evidence so the next session can act on it (P14). Nothing in the working tree was modified
by this review other than adding this file.*
