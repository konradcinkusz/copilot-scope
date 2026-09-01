# Contributing to CopilotScope

Thank you for your interest in CopilotScope! This document describes how to set up a development environment, run tests, and submit changes.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/9.0) | 9.0 | `dotnet --version` to verify |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | any recent | needed for Postgres + pgAdmin containers via Aspire |

Everything targets `net8.0`, but build with the **9.0 SDK**: it resolves Aspire 9 from
plain NuGet packages, so no `dotnet workload install aspire` is needed. On the 8.0 SDK
the AppHost project fails with `NETSDK1147: the following workloads must be installed:
aspire` — the collector, dashboard, tools and tests still build there, only the AppHost
does not.

## Quick dev loop

```bash
# clone
git clone https://github.com/konradcinkusz/copilotscope.git
cd copilotscope

# restore & build everything
dotnet build

# run the full stack (Postgres + pgAdmin + Collector + Dashboard)
dotnet run --project src/CopilotScope.AppHost

# open the Aspire dashboard (shown in console output) and
# open the CopilotScope dashboard at http://localhost:5XXX

# send synthetic telemetry to see data immediately
dotnet run --project tools/CopilotScope.TelemetryGen

# or seed a whole dataset straight into the running collector (always clears
# previously seeded data first, so this is safe to re-run at any time):
dotnet run --project tools/CopilotScope.Seeder -- quick   # ~12 sessions incl. showcase + curated chats, fast
dotnet run --project tools/CopilotScope.Seeder -- demo    # big varied set incl. showcase chats, for demos
```

## Running tests

```bash
dotnet test
```

All tests live in `tests/CopilotScope.Tests`. They run without Docker or a live collector.

## Project layout

```
src/
  CopilotScope.AppHost/       Aspire orchestration (containers, ports, env)
  CopilotScope.Collector/     OTLP/HTTP ingest, session aggregation, quality engine, REST API
  CopilotScope.Dashboard/     Blazor Server UI (zero JS dependencies)
tests/
  CopilotScope.Tests/         xUnit tests — decoder, routing, quality, persistence
tools/
  CopilotScope.TelemetryGen/  realistic demo telemetry generator
  CopilotScope.Seeder/        seeds a comprehensive session dataset into a running collector
```

## Submitting changes

1. **Fork** the repository and create a feature branch from `master` (the default branch).
2. Keep changes focused — one logical change per PR.
3. Add or update tests for any new logic in `CopilotScope.Collector`.
4. Run `dotnet test` and `dotnet build` before pushing. CI runs both on every PR.
5. Open a pull request against `master`. The PR description should explain *why* the change is needed, not just what it does.

## Architecture notes

- **Session aggregation** happens in `CopilotSession` (mutable, lock-guarded) inside `SessionStore`. All mutations go through `Apply()`.
- **Quality scoring** (`QualityEngine`) and **turn analysis** (`SegmentAnalyzer`) are pure functions over session snapshots — no side effects.
- **Persistence** is a single JSONB column per session in Postgres. The `PersistedSession` record mirrors `CopilotSession` exactly; adding a new field to either requires updating both and the `ToSession()` / `From()` conversions.
- **Dashboard DTOs** live in `CollectorClient.cs` (Dashboard project) and must stay in sync with the collector's `Dtos.cs`. There is no shared assembly by design — the JSON contract is the boundary.
- **No JS frameworks** — the dashboard is Blazor Server with inline CSS and vanilla JS only for one `scrollToBottom` helper.

## Code style

- C# 12 / .NET 8 idioms (primary constructors, collection expressions, pattern switches).
- No XML doc comments except on non-obvious public APIs.
- No abbreviations in names unless they are domain-standard (`ttft`, `otlp`, `llm`).
- Prefer records for DTOs and value objects; mutable classes only for aggregates that need lock-guarded mutation.

## Maintenance, stability and who reviews this

[GOVERNANCE.md](GOVERNANCE.md) states what to expect: issue and PR response posture for a
solo-maintained project, which surfaces are covered by a stability contract (REST DTOs,
Prometheus metric names, the jsonb snapshot shape, the label schema) versus which may move in
any release, release cadence, and what happens to your data if the project stops moving.

Worth reading before a large PR: it names the surfaces where a breaking change costs a major
version, which is usually the difference between a change that can be merged and one that has
to wait.

**Interested in co-maintaining?** [GOVERNANCE.md §6](GOVERNANCE.md) says what is actually
needed and how to get commit access. Short version: land two or three non-trivial PRs, then say
so.

## Questions / ideas

Open an [Issue](https://github.com/konradcinkusz/copilotscope/issues) for bugs, and for design questions or feature proposals — please raise one before writing a large PR, so we can agree on the approach first.
