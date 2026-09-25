# 1. First run

**Situation:** an empty machine. **Result:** CopilotScope running, and your own sessions
scored on screen.

Time: about two minutes. There is nothing to install first: no Docker, no .NET, no runtime.

## Install

```bash
curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh
```

Windows, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.ps1 | iex
```

The installer does four things:
- downloads one self-contained program, `copilotscope`, for your system;
- checks it against the release's `SHA256SUMS`, refusing a download that does not match;
- installs it into `~/.copilotscope/app` and puts it on your PATH;
- offers to point the assistants it finds at it, showing each change before making it.

Say no to all of them if you like: tutorial 2 does it one at a time.

**There is nothing to declare.** No key to generate, no environment variable to export, no
JSON to hand-edit. Everything binds to `127.0.0.1`, so on one machine a credential would
protect nothing.

## Start it

```bash
copilotscope
```

```text
CopilotScope 1.1.0 is running.
  Dashboard   http://localhost:5200
  Telemetry   http://localhost:4318   (point your assistant's OTLP/HTTP exporter here)
  Sessions    /home/you/.copilotscope/data
  History     Claude Code, read from /home/you/.claude/projects (never changed)
  Assistants  Claude Code sends telemetry here; VS Code could too: `copilotscope setup` (it asks first)
```

The dashboard opens in your browser. Ctrl+C stops everything; the sessions stay in
`~/.copilotscope/data` for the next start.

## Already use Claude Code? It is already there

Claude Code records every session to disk whether or not telemetry is configured.
CopilotScope reads that history as it starts, with no import step, and within a few seconds
the dashboard lists your past sessions, scored.

A session that is still being written waits until it has been quiet for ten minutes. That
way live telemetry, if the assistant sends any, is not counted twice.

```bash
copilotscope scan       # read it now, and say what was found
```

Imported sessions are badged `imported` and carry lower confidence, honestly. A transcript
records tokens, models, tools and real timings, but not time-to-first-token, edit decisions
or thumbs feedback: those are OpenTelemetry events, not anything written to the file. Prompt
text is never imported.

This is what the page looks like once it has something to show — the session list on the
left, and the one you picked scored on the right:

![The Sessions page](../img/dashboard-sessions.png)

The score is the headline; `View: Basic` keeps it to that. Tutorial 3 takes the same page
apart panel by panel.

## Check the whole path

```bash
copilotscope doctor
```

It checks:
- that CopilotScope is running and its dashboard files are in place;
- what each assistant's settings actually say, and where they point;
- whether a variable exported in your shell overrides them;
- how much history is on disk.

## What you have now

| | |
|---|---|
| Dashboard | <http://localhost:5200> |
| OTLP ingest | <http://localhost:4318> |
| Sessions | `~/.copilotscope/data` — delete it to start over |
| Stop it | Ctrl+C, or `copilotscope stop` from another terminal |
| Remove it | `copilotscope disconnect`, then delete `~/.copilotscope` and take `copilotscope` off your PATH |

For a team, a shared server, or Grafana alongside, the Docker Compose stack is still there:
see [tutorial 4](04-team-deployment.md).

## Next

[Connect a real assistant](02-connect-your-assistant.md), so new sessions arrive as you
work, with the latency and edit decisions a transcript cannot record.
