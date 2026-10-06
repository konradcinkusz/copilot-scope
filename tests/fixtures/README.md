# Real emitter fixtures

Captured OTLP payloads from assistants people actually run, replayed through the real decoder
and session store by `FixtureGoldenTests`.

## Why these exist

Every other OTLP payload in the test suite is hand-built from a reading of vendor docs. That
makes "five assistants land in one schema" an assertion rather than a demonstration — and it
fails *silently*: when a vendor renames an attribute, ingest keeps returning 200 while the
counters it feeds quietly go to zero. A fixture captured from a real client is the only thing
that turns that into a failing test.

## Capturing

```bash
# 1. Turn OFF content capture in the client. Fixtures are committed to a public repository.
# 2. Start the recording proxy in front of your collector:
dotnet run --project tools/CopilotScope.FixtureCapture -- \
    --assistant claude-code --version 2.1.0 --out tests/fixtures

# 3. Point the assistant at http://localhost:4319 instead of :4318 and use it normally.
# 4. Commit what lands in tests/fixtures/<assistant>/<version>/.
```

The proxy forwards every batch upstream unchanged, so a capture session is also a working
session. It **refuses** to write any batch carrying prompt or response text, and refuses batches
it cannot decode — "we could not read it" is not evidence that it is safe to publish. There is
deliberately no `--allow-content` flag.

## Layout

```
tests/fixtures/<assistant>/<version>/NNNN-<signal>.pb     # or .json for JSON exporters
```

`<assistant>` matches the emitter the batch should route to: `vscode`, `cli`, `claude-code`,
`cowork`, `cursor`. The golden test asserts that a fixture in `claude-code/` really does classify
as `EmitterKind.ClaudeCode` — which is exactly the assertion that breaks when a vendor changes
what it sends.

## Test Harness

`tests/CopilotScope.Tests/FixtureGoldenTests.cs` discovers all fixtures in this directory,
decodes them through the real OTLP decoder and session store, and asserts:
- Emitter kind matches the directory name (vscode, cli, claude-code, cowork, cursor)
- The decoded session is non-empty (has signals: chat calls, tool calls, tokens, etc.)
- Round-trip through persistence (JSONB serialization) preserves integrity

Run with: `dotnet test FixtureGoldenTests.cs`

When no fixtures exist, the test returns 0 cases (passes without running anything).
When fixtures are added, the test automatically discovers and validates them.

## Status

**Layer 0 infrastructure is in place.** Real captures are next. Capturing requires:
- A machine with the assistant installed and telemetry enabled
- `copilotscope capture-fixture` running as a proxy (blocks content batches for privacy)
- Using the assistant normally to generate real sessions

The multi-assistant compatibility claim currently rests on hand-built payloads in
CollectorTests, ClaudeCodeTests, etc. Adding real captures here will replace assumption
with evidence.
