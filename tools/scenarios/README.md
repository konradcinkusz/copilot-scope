# E2E Scenarios (Layer 2)

This directory contains deterministic scenario definitions for end-to-end testing of the quality scoring formula.

## Format

Each scenario is a YAML file with:

- **metadata**: Name, description, version, target assistants
- **generator**: Seed (for reproducibility), protocol, endpoint
- **session**: Signal data (chat calls, errors, TTFT, edits, etc.)
- **expected**: Golden score ranges and component bounds

## Scenarios

- `scenario-01-vscode-perfect.yaml` — VS Code clean session (expected score ~85–95)
- `scenario-02-claude-code-errors.yaml` — Claude Code error-prone (expected score ~20–40)
- `scenario-03-cli-balanced.yaml` — Copilot CLI typical quality (expected score ~65–80)

## Expected Scores

`expected-scores.json` holds the golden ranges and confidence minimums for each scenario.
Used in CI to verify that scoring output matches intent after changes to the formula.

## Running Scenarios

Once TelemetryGen is extended to support `--scenario` mode:

```bash
dotnet run --project tools/CopilotScope.TelemetryGen -- \
  --scenario tools/scenarios/scenario-01-vscode-perfect.yaml \
  --endpoint http://localhost:4318
```

## Adding New Scenarios

1. Create a new YAML file following the format above.
2. Populate with realistic signal data (chat calls, errors, latencies, etc.).
3. Manually calculate or empirically determine the expected score range based on `Quality/QualityEngine.cs`.
4. Add an entry to `expected-scores.json`.
5. Commit both files.

## Notes

- Scenarios are deterministic: same seed + manifest = same payload + score.
- Format is vendor-agnostic and extensible (CLI, Cowork, Cursor, custom tools).
- Scores must match the formula in `QualityEngine.cs`; formula changes require conscious golden-value updates.
