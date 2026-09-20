# MCP server and the CopilotScope skill

Two ways to put CopilotScope in front of the assistant it is measuring: an **MCP server**
that lets the assistant read its own session scores, and a **skill** that teaches it to
read them correctly. They are independent — the skill is useful with no server at all —
and neither is on by default.

```bash
copilotscope mcp install      # register the read-only MCP server with Claude Code
copilotscope skill install    # write the skill into ~/.claude/skills/copilotscope/
```

## Why a measurement tool may not measure itself

This is the part that had to be solved before the server could ship honestly.

An MCP server is called *from inside the session being scored*. Those calls arrive at the
collector like any other tool call — as `execute_tool` spans on the Copilot path, and as
`tool_result` log events on the Claude Code one, which is the default path rather than an
opt-in extra.

That is not cosmetic. Two parts of the formula move:

- **Reliability**, 0.25 of the composite and its largest single weight, is the error-free
  rate over `ChatCalls * 2 + ToolCalls`. Every *successful* read of a score would raise
  the score being read.
- **Repair-loop detection** in the turn analysis compares each turn's tool-to-chat ratio
  against the session median. Injected calls move both sides of that comparison at once,
  masking real repair loops in some sessions and manufacturing them in others.

So the collector drops its own reads at ingest, before they reach any counter
(`src/CopilotScope.Collector/Domain/SelfObservation.cs`). Asking what a session scored
does not change what it scored.

The call is still **visible**: both ingest paths record their timeline event outside the
scoring block, so the session's event list shows that the assistant asked. Dropped from
the counters, not from the record.

**This is why the server name matters.** Recognition is by tool name, which is all the
wire carries — `mcp__copilotscope__*` from Claude Code and Cowork, `mcp_copilotscope_*`
from VS Code Copilot. `copilotscope mcp install` registers it as `copilotscope`, which is
what makes the exclusion work. If you register it by hand under another name, its calls
**will** be counted as ordinary tool calls and will move your scores. The match is
deliberately narrow rather than fuzzy: silently dropping a user's own unrelated tools
would be the worse failure.

## What it exposes

Five tools. All of them reads.

| Tool | Returns |
|---|---|
| `health` | Collector reachable, and persisting |
| `list_sessions` | Recent sessions with score and confidence; filter by `days`, `limit`, `repository`, `emitter`, `model`, `grade` |
| `get_session` | One session in full: components, turn analysis, tools, errors, insights, timeline, transcript |
| `overview` | Token burn, per-model calls, daily usage, top sessions over a window |
| `signal_coverage` | Which assistant emits which signal |

There is **no write, delete, seed, import or label tool**, and there is no way to add one
by configuration. The collector's destructive endpoints need Admin scope; this server
never asks for it. An assistant holding a credential that could wipe the team's session
history is a worse trade than any convenience it would buy. A test enforces the list.

Every call goes over the same HTTP API as any other client, which is the point:
`tools/CopilotScope.Mcp` takes no project reference to the collector, so the privacy
guard, the k-anonymity floor and the access audit log all apply to it. Reaching into the
collector's types in-process would route around all three.

## On a shared deployment

Read this before registering it against a team collector.

The collector's **Read** scope covers the query API *and* captured transcripts. If content
capture is on, a Read key lets the holder's assistant read back prompt and response text —
everyone's, not just its own user's. On the single-machine default there is no key at all
and nothing to think about: the collector binds to `127.0.0.1` and Postgres publishes no
port. On a shared deployment, handing that key to every developer's assistant is a
decision for whoever runs it.

With privacy mode on, the controls apply here as everywhere else:

- A filter that narrows a list below the *k*-subject floor returns a suppressed page.
- Per-session detail can be refused outright, and then `get_session` returns the
  collector's own explanation. The server reports that as what it is — a control doing its
  job — and does not suggest finding a credential to get around it.

A 401 means a missing or wrong key. A 403 means privacy mode. They are reported
differently on purpose.

## Registration

`copilotscope mcp install` does this for you. By hand:

```bash
# macOS / Linux
claude mcp add copilotscope -- ~/.copilotscope/bin/copilotscope mcp
```

```powershell
# Windows
copilotscope mcp install
```

On Windows the installer registers the `docker compose run` command directly rather than
the PowerShell script. PowerShell re-emits a native command's stdout through its object
pipeline, which is fine for log lines and is not a safe carrier for a protocol framed one
JSON message per line; launching docker directly takes the script out of the data path.

The server runs out of the `copilotscope-tools` image, so there is nothing to install and
no .NET on your machine. Configuration is two environment variables, both already set by
the compose service:

| Variable | Default | Meaning |
|---|---|---|
| `COPILOTSCOPE_COLLECTOR` | `http://127.0.0.1:4318` | Collector base URL |
| `COPILOTSCOPE_API_KEY` | unset | Sent as `x-api-key` when the deployment has keys |

## When it does not work

- **The client reports protocol errors or garbage.** Something other than the server is
  writing to stdout. `docker compose run` must be given `-T`; without it a TTY is
  allocated, and a TTY echoes what it reads and rewrites line endings.
- **Every tool returns "could not reach the collector".** The stack is not running, or the
  MCP server is resolving a different address than you think. `copilotscope doctor` walks
  the whole path.
- **Scores moved after you registered it.** Check the registered name is `copilotscope`.
  See above.
- **Running `copilotscope mcp` in a terminal appears to hang.** It is waiting for a client
  on stdin. That is correct. `copilotscope mcp --help` prints usage instead.

## The skill

`copilotscope skill install` writes `skills/copilotscope/SKILL.md` into
`~/.claude/skills/copilotscope/`. It needs no server and no clone — the text ships inside
the tools image.

What it is for: a quality score is easy to quote badly, and the ways it goes wrong are
predictable. The skill teaches the assistant to quote confidence alongside the number, to
check signal coverage before comparing two assistants, to read the turn analysis instead
of restating the composite, and never to rank people with a session score. It also carries
the triage path for the most common complaint — it runs, but no sessions appear.

Those rules are in `README.md` too, under *How not to use CopilotScope*. The difference is
that a skill is read by the thing generating the answer, rather than by a person who has
already been given the wrong one.
