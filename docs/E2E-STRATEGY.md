# E2E Testing Strategy for CopilotScope

## Overview

CopilotScope's E2E testing is organized in 6 layers, from cheapest to most resource-intensive:

- **Layer 0**: Real fixture data collection
- **Layer 1**: Known-answer tests for scoring
- **Layer 2**: Deterministic scenario playback and cross-process CI E2E
- **Layer 3**: UI dashboard tests (Playwright)
- **Layer 4**: Real assistants with telemetry and signal coverage validation
- **Layer 5**: Resilience and load testing
- **Layer 6**: Human involvement (identity attributes, k-anonymity, calibration)

## Layer 0: Real Fixture Data

### Status

✅ **Harness complete**: `tests/CopilotScope.Tests/FixtureGoldenTests.cs` 
  - Auto-discovers fixtures in `tests/fixtures/<assistant>/<version>/`
  - Decodes via real OTLP decoder and SessionStore
  - Validates emitter classification matches directory
  - Checks round-trip through JSONB persistence

📍 **Waiting for**: Actual fixture captures from machines running assistants

### How to Contribute

1. **Disable content capture** in your assistant (settings or env var)
2. **Start the capture proxy**:
   ```bash
   dotnet run --project tools/CopilotScope.FixtureCapture -- \
       --assistant claude-code --version 2.1.0 --out tests/fixtures
   ```
3. **Point assistant at proxy** (`http://localhost:4319` instead of `:4318`)
4. **Use the assistant normally** for ~5-10 minutes (mix of calls, errors, tools)
5. **Commit captured batches** under `tests/fixtures/<assistant>/<version>/`

The proxy **refuses content-bearing batches** — all captures are safe for public repo.

### Coverage Matrix

| Assistant | Signals | Traces | Logs | Metrics | Status |
|---|---|---|---|---|---|
| Claude Code | chat, tool, edit decisions | ❌ | ✅ | ✅ | 🔴 No fixture |
| Copilot CLI | chat, tool, latency | ✅ | ✅ | ✅ | 🔴 No fixture |
| VS Code Copilot | chat, tool, latency | ✅ | ✅ | ✅ | 🔴 No fixture |
| Cowork | chat, tool, latency | ❌ | ✅ | ❌ | 🔴 No fixture |
| Cursor | chat, tool, traces | ✅ | ✅ | ✅ | 🔴 No fixture |

## Layer 1: Known-Answer Scoring Tests

### Status

✅ **In progress**: Golden file assertions for QualityEngine

Expected work:
- Hand-compute scores for 3-5 scenarios using `QualityEngine.cs` + `ScoringProfile.cs`
- Replace `InRange(50, 90)` assertions with exact values
- Create golden file on full `QualityReport` (JSON) so formula changes require review

### Why This Matters

- Scores are deterministic and auditable — small formula changes must fail tests
- Prevents silent regressions (vendor attribute renames, weight changes)
- Provides reproducible reference for validation (Layer 6)

## Layer 2: Deterministic E2E in CI (✅ Complete)

### Status

✅ **Shipped in PR #170**:
- `tools/CopilotScope.TelemetryGen --scenario <file>` replays YAML manifests
- `scripts/scenarios/` holds test cases with expected score ranges
- GitHub Actions job: builds collector locally, starts health check, runs scenarios, validates `/api/sessions`
- Python validation script handles SessionPageDto response structure
- Cross-process OTLP ingest path tested (real decoder → real SessionStore → real API)

### Coverage

- ✅ vscode-perfect: scores [85, 95]
- ✅ claude-code-errors: scores [20, 40]
- ✅ cli-balanced: scores [65, 80]

## Layer 3: UI Dashboard Tests (✅ Complete)

### Status

✅ **Shipped in PR #170**:
- Playwright tests in `tests/CopilotScope.E2E.Playwright/`
- Four test methods covering page load, score display, confidence ranges, refresh persistence
- Not in main solution file (Chromium not in CI, manual testing only)

### Next Steps

- Run locally: `cd tests/CopilotScope.E2E.Playwright && dotnet test`
- Requires running collector and populated sessions

## Layer 4: Real Assistants + Signal Coverage

### Effort

Medium. Requires 1-2 hours per assistant.

### What to Test

- Point each assistant at running collector with telemetry enabled
- Run `copilotscope doctor` after each to check signal coverage
- Verify: expected emitter kind detected, no silent attribute misses

### Prerequisites

- Layer 1 (known-answer tests) so we have reference scores
- A running collector instance (local or cloud, accessible from dev machine)
- Environment variables for OTLP export (OTEL_EXPORTER_OTLP_ENDPOINT, x-api-key)

## Layer 5: Resilience & Load Testing

### Test Cases

- Burst ingest (k6, TelemetryGen with high rate)
- Decompression bomb (gzip payload limits)
- Postgres unavailable, restart during ingest
- Mixed JSON/protobuf, sessions without identity
- Collector container restart with persisted sessions

## Layer 6: Human Validation (Calibration)

### Prerequisites

- Layer 4 complete (real data flowing)
- 3–6 volunteer assistants with telemetry enabled
- ≥2 human labelers for score validity (inter-rater κ ≥ 0.70)

### Inputs & Outputs

- Inputs: Real, unseen sessions (n ≥ 20, ideally 50–100)
- Outputs: Human labels + algorithm scores
- Calculate: κ (inter-rater agreement), correlation with repo events (merge/revert)

This is **product validation**, not testing infrastructure — separate from E2E.

## Priority & Timeline

| Layer | Blocker | When | Owner |
|---|---|---|---|
| 0 | Real fixtures | ASAP | Contributors with running assistants |
| 1 | Known-answer tests | After layer 0 yields data | Scoring expert |
| 2 | ✅ Shipped | 2026-10-05 | Done |
| 3 | ✅ Shipped | 2026-10-05 | Done |
| 4 | Layers 0+1 | Week 2 | QA / Contributors |
| 5 | Layer 4 green | Week 3–4 | DevOps / QA |
| 6 | Calibration dataset | Month 2+ | Product team |

## Running Tests Locally

```bash
# Layer 0 (no fixtures yet)
dotnet test tests/CopilotScope.Tests/FixtureGoldenTests.cs

# Layer 1 (placeholder)
dotnet test tests/CopilotScope.Tests/QualityEngineTests.cs

# Layer 2 (scenarios)
.github/workflows/ci.yml → "scenarios" job (runs in CI only for now)

# Layer 3 (dashboard)
cd tests/CopilotScope.E2E.Playwright
export COPILOTSCOPE_URL=http://localhost:5000
dotnet test --filter "CopilotScopeDashboardTests"
```

## Key Invariants

From `CLAUDE.md`:

1. **Scoring is pure**: No I/O, no mutation, no clock reads. Reproducibility is auditable.
2. **Emitter detection must be infallible**: Wrong classification on real data is silent.
3. **Privacy enforcement**: Reads go through HTTP API, never direct SessionStore access.
4. **CopilotScope's own activity is never scored**: Self-observation is marked, dropped at ingest.
5. **JSONB round-trip must preserve data**: PersistedSession ↔ CopilotSession bidirectional sync.

## References

- `tests/fixtures/README.md` — How to capture fixtures
- `.github/workflows/ci.yml` — Layer 2 automation (scenarios job)
- `tests/CopilotScope.Tests/FixtureGoldenTests.cs` — Layer 0 harness
- `tools/CopilotScope.TelemetryGen/Program.cs` — Scenario playback
- `tools/CopilotScope.FixtureCapture` — Fixture capture proxy
- `docs/SIGNAL_COVERAGE.md` — What each emitter reports
