# CopilotScope — working in this repository

Quality scoring for AI coding-assistant sessions. Ingests OpenTelemetry from VS Code
Copilot, Copilot CLI, Claude Code and Claude Cowork; aggregates it per session; scores it
with a published, deterministic formula; serves a dashboard, a REST API and Prometheus
metrics. Self-hosted by design — nothing leaves the machine it runs on.

If you only want to *run* it, you need none of this: see README.md.

## Build and test

```bash
dotnet build                       # whole solution; everything targets net10.0
dotnet test                        # tests/CopilotScope.Tests — no Docker, no live collector
npm run check:diagrams             # diagrams agree with their standalone sources
node scripts/check-doc-parity.mjs origin/master   # bilingual documents stayed paired
```

CI runs all four on every pull request, plus five jobs nothing else covers: every
container image is built and smoke-started, `scripts/copilotscope` is shellchecked at
`-S warning` while every `.ps1` is parsed by `pwsh`, the published compose files are
rendered with no environment at all, the session-store contract suite runs against a
real Postgres (`COPILOTSCOPE_TEST_PG`; without it the suite covers the file store only),
and the native `copilotscope` binary is packaged (`scripts/package-native.sh`) and
smoke-tested from the extracted archive (`scripts/smoke-native.sh`).
Run at least `dotnet build` and `dotnet test` before pushing, and
`shellcheck -S warning -e SC1090,SC1091 scripts/copilotscope` if you touched the control
script.

## Invariants that break quietly

These are the ones that compile, pass review, and fail later. Each has already cost this
project something.

**`PersistedSession` mirrors `CopilotSession` exactly.** Persistence is a single JSONB
column per session. Adding a field to either one means adding it to the other *and* to
both conversions, `ToSession()` and `From()`. Miss one and the field silently vanishes on
the first round-trip through storage — the in-memory session looks right until the store
trims it.

**The two session stores answer identically.** `PostgresSessionRepository` states the read
contract in SQL and `FileSessionRepository` in C#, and neither reads the other; the contract
itself is written once, on `ISessionRepository`. The read path overlays live sessions on
whatever the store returns, so a filter, a sort or a window bound that differs between them
shows a different history for the same data. `SessionRepositoryContractTests` runs every case
against both — add the case there, not beside one store.

**The TFM and the `Dockerfile*` base images retarget together.** A runtime image on a
different major than the target framework builds fine and then exits at startup with
"framework not found". That shipped a dead release once (#55). The container job in CI
exists because `dotnet build` does not cover it.

**The native binary's dashboard files come from the dashboard's own publish.** Only that
publish produces `_framework/blazor.web.js`; `src/CopilotScope.Local` is a plain SDK project
that copies nothing, and `scripts/package-native.sh` puts the dashboard's `wwwroot` beside
the binary. A dashboard without that script renders once and then never responds while every
health check stays green — it shipped once in a container image (see `Dockerfile.dashboard`),
which is why the native smoke test fetches the script from the extracted archive.

**Scoring is a pure function over a session snapshot.** `QualityEngine` and
`SegmentAnalyzer` take a session and return a report — no I/O, no mutation, no clock
reads. Keep it that way: it is what makes a score reproducible and lets the formula be
audited, which is the project's entire differentiator against a model-judged competitor.

**Session mutation goes through `Apply()`.** `CopilotSession` is mutable and lock-guarded
inside `SessionStore`. Touching fields outside `Apply()` is a data race.

**The three writing tools ship in one image.** `CopilotScope.Seeder`,
`CopilotScope.LogImporter` and `CopilotScope.TelemetryGen` are dispatched by
`scripts/tools-entrypoint.sh` out of `Dockerfile.tools`. Changing how one is invoked means
checking both. CI builds that image and runs its entrypoint on every pull request.

**Bilingual documents come in pairs.** `docs/tutorials/*.md` each need a `.pl.md` twin,
and the LaTeX manual pairs on `.pl.tex`. Editing one half alone fails CI — and shipping a
stale translation is worse than shipping none, because the reader trusts it. Everything
else in the repository is English only.

**Reads go over the HTTP API, never into `SessionStore` directly.** The privacy guard, the
k-anonymity floor and the access audit log all live on the API path. An in-process reader
routes around all three. This is why `tools/CopilotScope.Mcp` takes no project reference to
the collector.

**CopilotScope's own tool calls are never scored.** See
`src/CopilotScope.Collector/Domain/SelfObservation.cs`. Reliability is the error-free rate
over `ChatCalls * 2 + ToolCalls`, so a counted read of a score would raise the score being
read, and injected calls would move the tool-to-chat ratio that repair-loop detection is
measured against. Any new self-observation surface has to be excluded at every ingest
path — `execute_tool` spans in `SessionStore`, `tool_result` log events in `ClaudeCode`, and
the `tool_result` blocks of an imported transcript in `ClaudeCodeTranscript`.

## Dependency budget

Deliberately near zero, and worth defending: it is what lets the whole product be audited.

| Project | Allowed |
|---|---|
| `CopilotScope.Collector` | Npgsql only — OTLP protobuf is decoded in-repo |
| `CopilotScope.Dashboard` | nothing; Blazor Server with zero JS dependencies |
| `CopilotScope.Local` | nothing beyond project references to the Collector and the Dashboard; its command line is parsed by hand |
| `tools/*` | nothing beyond a project reference to the Collector |
| `CopilotScope.AgentForge`, `CopilotScope.JudgeAgent` | Azure.AI.*, Microsoft.Agents.AI — opt-in services, behind a Compose profile |

`package.json` is the documentation toolchain only. It is not part of the product, and the
product does not gain a Node runtime.

## Layout

```
src/CopilotScope.Collector/      OTLP ingest, session aggregation, quality engine, REST API
  Domain/                        session aggregates, emitter dialects (Sem, ClaudeCode)
  Quality/                       QualityEngine, SegmentAnalyzer — pure scoring
  Privacy/                       pseudonymization, k-anonymity floor, access audit
  Api/                           DTOs, query service, Prometheus exporter
  Persistence/                   session stores: Postgres, or one JSON file per session
  Import/                        assistants' own history files: Claude Code transcripts
src/CopilotScope.Dashboard/      Blazor Server UI
src/CopilotScope.Local/          the native `copilotscope` binary: both apps in one process (ADR-004)
src/CopilotScope.AppHost/        Aspire orchestration
src/CopilotScope.{AgentForge,JudgeAgent}/   opt-in agent services
tools/CopilotScope.Seeder/       demo data into a running collector
tools/CopilotScope.LogImporter/  Claude Code transcripts → scored sessions, over /api/import
tools/CopilotScope.TelemetryGen/ synthetic OTLP over the real ingest path
tools/CopilotScope.FixtureCapture/ records real payloads as test fixtures
tools/CopilotScope.Mcp/          read-only MCP server over the collector's API
tests/CopilotScope.Tests/        xUnit; no Docker, no live collector
skills/copilotscope/             the skill shipped to users of CopilotScope
```

## Pull requests

One logical change per PR. Add or update tests for any new logic in the Collector. Explain
*why* in the description, not just what. Branch from `master`.

## What this project refuses to do

Worth knowing before proposing a feature, because these are decisions, not gaps:

- **No per-developer view, and no way to build one.** The score grades a session. Privacy
  mode enforces this in software: pseudonymized identities, and any view covering fewer
  than *k* subjects is refused. "Add a leaderboard" is not a feature request this project
  can accept.
- **Acceptance rate is not a target.** It is 0.20 of the composite and paired with edit
  survival, on purpose.
- **Workflow-friction analysis stays off by default and report-only.** It counts observed
  repair events. It is never folded into the score.
- **Nothing leaves the machine.** No SDK, no account, no telemetry home.

`docs/STRATEGY.md` and `docs/architecture/ADR-003-positioning.md` carry the reasoning.
