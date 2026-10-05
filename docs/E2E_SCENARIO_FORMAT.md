# E2E Scenario Format (Warstwa 2)

Scenariusze to deterministyczne definicje OTLP payloadów z oczekiwanymi wynikami scoring'u.
Format umożliwia:
- Generowanie realnych payloadów dla każdego asystenta
- Powtarzalność (seed-driven, wersjonowanie)
- Asercje na scoring'u, persystencji, API

## Struktura

```
tools/scenarios/
├── scenario-01-vscode-perfect.yaml      # VS Code — sesja czysta
├── scenario-02-claude-code-errors.yaml  # Claude Code — z błędami
├── scenario-03-cli-throughput.yaml      # Copilot CLI — wysoki throughput
└── expected-scores.json                 # Golden scores
```

## Manifest scenariusza (YAML)

```yaml
# scenario-01-vscode-perfect.yaml
metadata:
  name: "vscode-perfect"
  description: "VS Code session: clean, good latency, high acceptance"
  version: "1"
  assistants:
    - "vscode"

# Generator config
generator:
  seed: 42
  protocol: "protobuf"  # or "json"
  compressed: false     # gzip
  endpoint: "http://localhost:4318"

# Session state (translated to OTLP spans/metrics)
session:
  id: "vscode-perfect-001"
  duration_seconds: 300
  
  # Reliability: chat + tool signals
  chat_calls: 10
  chat_errors: 0
  tool_calls: 8
  tool_errors: 0
  
  # Latency: TTFT samples (milliseconds)
  ttft_ms: [350, 400, 450, 380, 420]
  
  # Acceptance: edit signals (VS Code only)
  edits_accepted: 7
  edits_rejected: 0
  edits_with_survival: 6  # 4-gram survival
  
  # Turnaround time per turn (optional, for friction)
  turnaround_ms: [250, 280, 300, 260, 270]

# Expected output
expected:
  score_range: [88, 92]  # ±2 tolerance
  confidence_min: 0.80
  components:
    reliability: [0.90, 1.0]
    acceptance: [0.95, 1.0]
    latency: [0.75, 0.85]
    friction: [0.90, 1.0]
```

## Generator Implementation (TelemetryGen erweitert)

```bash
# Run scenario
dotnet run --project tools/CopilotScope.TelemetryGen -- \
  --scenario tools/scenarios/scenario-01-vscode-perfect.yaml \
  --endpoint http://localhost:4318 \
  --api-key secret-key

# Output: stdout prints generated session ID, allows capture/verification
# Exit code 0 if score matches expected range, 1 otherwise
```

## CI Integration (New Job in `.github/workflows/ci.yml`)

```yaml
e2e-scenarios:
  runs-on: ubuntu-latest
  services:
    postgres:
      image: postgres:16
    collector:
      image: ghcr.io/konradcinkusz/copilot-scope:latest
      ports:
        - 4318:4318
  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
    - name: Run scenarios
      run: |
        for scenario in tools/scenarios/scenario-*.yaml; do
          dotnet run --project tools/CopilotScope.TelemetryGen -- \
            --scenario "$scenario" \
            --endpoint http://localhost:4318 \
            --api-key test-key || exit 1
        done
    - name: Verify scores
      run: |
        # Poll /api/sessions, check count and scores vs expected-scores.json
        curl http://localhost:4318/api/sessions | jq . > /tmp/sessions.json
        python3 scripts/verify-scenarios.py \
          tools/scenarios/expected-scores.json \
          /tmp/sessions.json
```

## Expected Scores File

```json
{
  "scenarios": [
    {
      "name": "vscode-perfect",
      "min_score": 88,
      "max_score": 92,
      "min_confidence": 0.80
    },
    {
      "name": "claude-code-errors",
      "min_score": 28,
      "max_score": 35,
      "min_confidence": 0.75
    }
  ]
}
```

## Implementation Checklist

- [ ] Extend `TelemetryGen/Program.cs` to parse YAML manifest
- [ ] Implement `ScenarioGenerator` class (OTLP builder from manifest)
- [ ] Support `--seed` flag for reproducibility
- [ ] Add exit code check: 0 if score matches, 1 otherwise
- [ ] Create 3–5 golden scenarios (vscode, claude-code, cli, error cases)
- [ ] New CI job: `e2e-scenarios` in `.github/workflows/ci.yml`
- [ ] Verification script: `scripts/verify-scenarios.py`
- [ ] Document in README.md: "Layer 2: E2E Cross-Process Testing"

## Notes

- Scenarios are **idempotent**: same seed + manifest = same payload + score
- Format is **vendor-agnostic**: can extend for CLI, Cowork, Cursor
- Scores must **deterministically** match the formula in `QualityEngine.cs`
- If formula changes, update golden scores and commit as feature work
