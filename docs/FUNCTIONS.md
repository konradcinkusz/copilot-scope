# Functions: your own assistant, over your own session base

CopilotScope provides no model and asks for no API key. A **function** is something you run on the
assistant you already pay for — Claude Code or GitHub Copilot — over the sessions CopilotScope has
scored: *review my sessions*, *what keeps going wrong*, *what should I write down so it stops*,
*why did it get worse*. The dashboard's **Functions** page lists them; one click prepares a run,
a second one starts it.

This is step two onwards of [ADR-005](architecture/ADR-005-session-review.md). Step one, the
[review pack](REVIEW.md), is what every function reads.

## The split that makes it honest

- **CopilotScope counts.** Every figure a function works from is in the review pack, computed by
  the collector as a pure function of the sessions, with every threshold stated.
- **The assistant narrates.** It names mechanisms, decides what is worth acting on, and drafts. It
  is told — in every task and every agent's prompt — that each claim must cite a pattern id and
  session ids from the pack, that patterns are counts and not causes, that it must never compare
  across strata, rank a person, set an acceptance-rate target or infer workflow friction, and that
  it does not compute numbers of its own.
- **A verifier checks.** Every multi-agent function ends with `copilotscope-verifier`, whose only
  job is to look each cited pattern and session up in the pack and strike the findings the
  citations do not show. It is the one job ADR-005 reserves for a second agent.

## The four functions

| Function | Agents | What it writes |
|---|---|---|
| **Review my sessions** | one | Summary, what went well, what recurred, what changed, at most five recommendations, and what the review cannot see |
| **Review panel** | four specialists in parallel — `copilotscope-reliability` (tools and errors), `copilotscope-workflow` (repair loops, latency stalls), `copilotscope-models` (models and cost), `copilotscope-trends` (change over time) — then the verifier | One report merged from four close readings, every finding marked kept or amended |
| **Propose instructions and skills** | `copilotscope-pattern-miner` chooses what guidance could fix; one `copilotscope-author` per problem, in parallel; then the verifier | Draft `copilot-instructions.md` / `CLAUDE.md` snippets and Agent Skills `SKILL.md` files, each with the evidence it rests on |
| **Explain what changed** | one `copilotscope-investigator` per regression, in parallel; then the verifier | Per regression: the figures, what co-occurs with it, what was ruled out, what to check next |

Each function's task and agents are defined once, in
`src/CopilotScope.Dashboard/Functions/FunctionCatalog.cs`, and rendered into each assistant's own
format.

## Multi-agent, on each assistant

| | Claude Code | GitHub Copilot CLI | VS Code Copilot Chat |
|---|---|---|---|
| Agents | subagents, passed as `--agents claude/agents.json` | custom agents, `.github/agents/*.agent.md` | the same custom agents |
| Parallel dispatch | the lead dispatches with the `Task` tool | **fleet mode** (`--fleet`): Copilot's orchestrator for parallel subagents, with the `task` tool | `runSubagent` (the `agent` tool set) |
| Started by CopilotScope | yes | yes | no — download the kit and run `/copilotscope-<function>` |

The flags below were checked against Claude Code 2.1.283 and GitHub Copilot CLI 1.0.88: the
arguments CopilotScope generates were passed to both binaries, and Claude Code's `init` message
confirmed the session id, the tool list (`Task, Glob, Grep, Read`), the custom agents, and that no
MCP server or skill was loaded. A Copilot CLI run itself was not observed, only its argument parsing.

## What a run is

Pressing **Run with Claude Code** or **Run with Copilot CLI** does not start anything. It fetches
the review pack over the collector's HTTP API — the path the privacy guard, the aggregation floor
and the access audit sit on — and writes a run directory:

```
~/.copilotscope/runs/20260926-141502-review-panel-3f9a/
  TASK.md                         the lead's task, the agents it may dispatch, the rules
  pack.md, pack.json              the review pack (sessions tier where the collector serves it)
  .github/agents/*.agent.md       the agents, for Copilot
  claude/agents.json              the same agents, for Claude Code
  claude/settings.json            telemetry off for this run, and the observer marker
  .github/prompts/copilotscope-review-panel.prompt.md   for VS Code
  README.md                       how to run it by hand
  run.json                        the run's state, kept by CopilotScope
```

The page then shows every file with its size, the exact command, and who receives what the
assistant reads (Anthropic or GitHub, under your subscription's terms — as in any session). Only
**Run** starts it; **Discard** deletes the directory. When the run ends, `report.md` is written
beside the pack under a header that says which parts CopilotScope computed and which the assistant
wrote; the assistant's self-rating is called *certainty* so it is never read as a score's
confidence. Drafts the proposal function fenced as `file=skills/<name>/SKILL.md` or
`file=instructions/<name>.md` are saved under `proposals/` — and only those two shapes, only
lower-case hyphenated names, and never a draft that names a session id from the pack, since a
draft is written to be committed and shared. **Nothing is installed.**

One run at a time. A run still going after thirty minutes is stopped, and so is one running when
CopilotScope stops.

## What the assistant may do

Read the run directory. Nothing else.

**Claude Code:**

```
claude -p "Read TASK.md in the current directory and do what it says. Reply with the report only."
  --output-format json --session-id <uuid>
  --restricted --tools Read,Grep,Glob[,Task] [--agents claude/agents.json]
  --strict-mcp-config --no-session-persistence --permission-prompts none
  --disable-slash-commands --settings claude/settings.json
```

`--restricted` removes every tool that runs commands or code and WebFetch, ignores the user's,
project's and local settings files (including the telemetry `copilotscope connect` wrote), and
confines the file tools to the working directory. Never `--bare`, which skips OAuth and would move
the run off the subscription, and never `--add-dir`, which would widen what it may read.

**GitHub Copilot CLI:**

```
copilot -p "Read TASK.md in the current directory and do what it says. Reply with the report only."
  -s --session-id <uuid>
  --available-tools view glob grep [task read_agent list_agents]
  --allow-all-tools --deny-tool shell --deny-tool write --deny-tool url
  --disallow-temp-dir --disable-builtin-mcps --no-custom-instructions
  --no-ask-user --no-auto-update --no-color [--fleet --add-dir .]
```

`--available-tools` decides which tools exist for the model at all; with only the read tools
present, `--allow-all-tools` (which non-interactive mode requires) approves reading, and the
denials win over it regardless. `--add-dir .` loads the run's `.github/agents` as trusted
configuration outside a git repository.

Neither command goes through a shell, and every argument is a fixed word or a relative path: the
task, the agents and the settings travel as files, because an npm-installed assistant on Windows
is a `.cmd` shim whose arguments `cmd.exe` parses again.

## A run is never scored

A function run is a session of your assistant, and if it were scored the base would grow a session
whose only subject is the base — the numbers the next review reads would have moved because the
last one ran. So:

1. Its session id is chosen by CopilotScope and registered with the collector's
   `ObserverRegistry` **before** the process starts. Ingest drops every span, metric point and log
   record keyed to a registered id (and every span sharing a trace with one), and `POST /api/import`
   refuses one — which covers the scanner, whose only way in is that endpoint. Runs found on disk at
   start are registered again.
2. The child's environment has every `OTEL_*` and `COPILOT_OTEL_*` variable, `COPILOT_ALLOW_ALL`,
   `CLAUDECODE` and the `CLAUDE_CODE_*` variables removed — except the ones that carry a login or a
   model provider (`CLAUDE_CODE_OAUTH_TOKEN`, `CLAUDE_CODE_USE_BEDROCK`, …) — and has telemetry
   switched off (`CLAUDE_CODE_ENABLE_TELEMETRY=0`, `COPILOT_OTEL_ENABLED=false`,
   `OTEL_SDK_DISABLED=true`).
3. It carries `OTEL_RESOURCE_ATTRIBUTES=copilotscope.observer=true`. If an organisation's managed
   settings switch telemetry back on, every signal is still marked, and ingest drops marked
   signals whatever their session id. The consent screen says so when Claude Code's managed
   settings force telemetry on. `GET /api/health` counts what was dropped (`observerSignals`).
4. Claude Code keeps no transcript (`--no-session-persistence`).

## Where it runs

Only the native `copilotscope` binary starts an assistant: it is the one process that owns local
state and runs as the person whose assistant it starts. Started with `--memory`, it keeps nothing on
disk and so runs none. A Compose deployment never does — a shared collector has no business starting
a program on anyone's machine — and its Functions page offers each function as a **kit**: the same
files as a zip (`/functions/<id>/kit.zip?days=30`), with a README giving the commands. A kit carries
the sessions tier only for a viewer who may read transcripts and only when the collector serves it to
the dashboard's key; otherwise it carries the aggregate tier and says so. A kit run by hand is marked
the same way (`OTEL_RESOURCE_ATTRIBUTES=copilotscope.observer=true` in the Copilot command it shows;
Claude Code's telemetry is off through `claude/settings.json`).

With dashboard sign-in on, running a function or downloading a sessions-tier kit needs the admin
sign-in, like reading a transcript.

## Limits, stated rather than hidden

- **Proposals are hypotheses.** A before/after around a skill's installation is uncontrolled and
  blind to whether the skill was used, until the collector persists a skill name per session.
- **Structure only.** The pack holds tool names, counts, error types and turn shapes — never what
  was typed or read. A function can propose instructions from that; a skill drafted from it is a
  procedure inferred from structure, and says so.
- **Your quota.** A multi-agent function spends more of your subscription than a single pass. The
  single-agent review is the cheap one.
- **Copilot CLI's reply is read as plain text** (`-s`). Its JSON-lines mode is not parsed: a parser
  for an emitter's format lands with a fixture captured from a real installation (ADR-002, ADR-004).
