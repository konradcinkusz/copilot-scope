# 1. First run

**Situation:** an empty machine with Docker on it. **Result:** a running stack
and a scored session you can click through.

Time: about five minutes, most of it spent pulling images.

## Install

```bash
curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh
```

Windows, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.ps1 | iex
```

The installer checks Docker, writes the compose file and a `copilotscope`
control script into `~/.copilotscope`, starts the stack, waits for the collector
to answer, and offers to configure any assistant it finds.

**There is nothing to declare.** No key to generate, no environment variable to
export, no JSON to hand-edit. Every port binds to `127.0.0.1` and Postgres
publishes no port at all, so on one machine a credential would protect nothing
while costing a configuration step in every client.

Prefer to drive Compose yourself? This does the same thing:

```bash
curl -O https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/docker-compose.ghcr.yml
docker compose -f docker-compose.ghcr.yml up -d
```

## Prove the pipeline before blaming a client

Do this before configuring anything. It saves an hour of debugging the wrong
half of the system.

```bash
copilotscope probe
```

This sends one simulated session over real OTLP/HTTP protobuf, then checks that
the collector serves it back. If it passes, ingest, decoding, scoring and
persistence all work — so anything still missing afterwards is client
configuration, not the stack.

## Put something on the screen

```bash
copilotscope demo     # a dozen fabricated sessions
copilotscope open     # http://localhost:5200
```

Seeded sessions are badged `demo`, so a screenshot of them is never mistaken for
evidence about a real assistant. `copilotscope demo demo` loads the larger
multi-day dataset instead.

## Already use Claude Code? Skip ahead

Claude Code records every session to disk whether or not telemetry is
configured. Scoring that history needs no client setup at all:

```bash
copilotscope import --dry-run     # see what it found, send nothing
copilotscope import
```

Re-running is safe: sessions keep Claude Code's own identifier, so a second run
replaces rather than duplicates. Prompt text stays out unless you pass
`--include-content`.

Imported sessions are badged `imported` and carry lower confidence, honestly:
a transcript records tokens, models, tools and real timings, but not
time-to-first-token, edit decisions or thumbs feedback, because those are
OpenTelemetry events rather than anything written to the file.

## What you have now

| | |
|---|---|
| Dashboard | <http://localhost:5200> |
| OTLP ingest | <http://localhost:4318> |
| Stop it | `copilotscope down` |
| Remove it | `copilotscope uninstall` (add `--purge` to drop the database too) |

## Next

[Connect a real assistant](02-connect-your-assistant.md), so the sessions are
yours instead of fabricated.

<!-- probe -->
