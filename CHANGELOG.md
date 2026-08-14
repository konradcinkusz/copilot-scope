# Changelog

Notable changes per release. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

Releases publish four images to GHCR — `ghcr.io/konradcinkusz/copilotscope-collector`,
`-dashboard`, `-agentforge` and `-judgeagent` — plus the research paper PDF as a release asset.

## [Unreleased]

### Changed
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
