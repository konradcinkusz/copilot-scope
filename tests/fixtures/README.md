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

## Status

**No real captures are committed yet.** Capturing requires a machine running the assistants;
this directory and the harness exist so that a capture is a `git add` away rather than a project.
Until then the multi-assistant compatibility claim rests on hand-built payloads, and the README's
supported-assistant list should be read accordingly.
