---
name: copilotscope
description: Read and explain CopilotScope session quality scores, and fix a CopilotScope install that runs but shows no sessions. Use when the user asks how a coding session scored, why a score is what it is, which turn went wrong, what their AI-assistant sessions cost, or when telemetry is configured but the dashboard stays empty. Also use when they mention copilotscope, a session quality score, TFRA turn analysis, edit survival, or `copilotscope doctor`.
---

# CopilotScope

CopilotScope scores the quality of AI coding-assistant sessions from the OpenTelemetry
those assistants already emit. It runs on the user's own machine: the collector binds to
`127.0.0.1`, Postgres publishes no port, nothing leaves the box.

This skill covers two jobs: **reading a score correctly**, and **getting data to appear at
all**. Both matter, and the first is the one that is easy to do badly.

## Reading a score

The composite is 0–100, built from six weighted components, renormalized over the ones
that actually have data:

| Component | Weight | What it is |
|---|---|---|
| Reliability | 0.25 | Error-free rate, errors weighted quadratically |
| Acceptance | 0.20 | Accepted vs rejected edits, plus code survival |
| Friction | 0.20 | Repair loops and stalled turns |
| Latency | 0.15 | TTFT p50 on a log-linear 0.3 s–10 s curve |
| Feedback | 0.10 | Thumbs, where the assistant reports them |
| Efficiency | 0.10 | Token and cache economics |

Those are the `interactive` weights. Autonomous and supervised-agent sessions are scored
on different profiles — an autonomous run has no latency component and no acceptance
signal, because nobody was waiting and nobody clicked accept.

**Four rules when reporting a score. They are the difference between a useful answer and
a misleading one.**

1. **Never quote a score without its confidence.** Confidence says how much telemetry the
   number rests on. A 90 built on four samples means less than a 70 built on forty. If you
   only have room for one number, give the confidence too or give neither.

2. **The score grades a session, not a person.** Do not rank developers, do not aggregate
   by author, do not answer "who is the best/worst" — there is no per-developer view and
   that is deliberate. On a deployment with privacy mode on, the software enforces this:
   identities are pseudonymized at ingest and any view covering fewer than *k* subjects is
   refused. If a query comes back suppressed, that is the control working. Say so; do not
   look for a way around it.

3. **Acceptance rate is not a target.** Push on it and you reward accepting bad
   suggestions. It is 0.20 of the composite precisely so it cannot dominate, and it is
   paired with edit survival — the counter-metric that catches code accepted and then
   reverted. Never recommend "raise your acceptance rate".

4. **Compare across assistants only directionally.** The four supported assistants do not
   emit the same signals. A Claude Code session has no thumbs and no edit-survival signal,
   so its 80 rests on less evidence than a VS Code session's 80. Within one assistant,
   scores are comparable; across them, they are a direction, not a ranking. Check
   `docs/SIGNAL_COVERAGE.md` or the `signal_coverage` tool before any cross-assistant claim.

**When asked why a session scored what it did, do not restate the number.** Read the turn
analysis (TFRA). It names the turn that went wrong and the reason: LLM or tool errors,
latency measured against *this session's own* median rather than a global threshold, or a
repair loop — a turn spending far more tool calls per chat call than the session's norm.
That is the answer the user actually wants.

Two more things that change how a number should be read:

- **Imported sessions score on less evidence.** `copilotscope import` reconstructs sessions
  from Claude Code's local transcripts. A transcript has tokens, models, tools and timings,
  but no latency, edit-decision or feedback signal. Those sessions are badged `imported`;
  say so when comparing them to live ones.
- **Workflow-friction analysis is off by default and report-only.** It counts observed
  repair events — rephrasing, corrective replies, negative feedback markers. It is never
  folded into the score, and it is not a measure of how the user felt.

## If the MCP server is connected

When `copilotscope mcp` is registered, these read-only tools are available:

| Tool | Use it for |
|---|---|
| `health` | Is the collector up and persisting |
| `list_sessions` | Recent sessions with score and confidence; filter by days, repo, assistant, model |
| `get_session` | One session in full — components, turn analysis, tools, errors, transcript |
| `overview` | Token burn and usage across a window |
| `signal_coverage` | Which assistant emits which signal |

There is no write, delete, seed or import tool, by design. Reads go over the same HTTP API
as any other client, so the privacy guard and the access audit log apply to them too.

The server's own calls are excluded from scoring at ingest, so asking about a score does
not change it. Do not try to work around that by reading scores through another path — the
exclusion is what makes the number trustworthy.

## When sessions do not appear

The single most common problem, and there is a command for it:

```bash
copilotscope doctor
```

It walks the whole path — stack running, collector reachable, assistant configured,
telemetry arriving — and names the link that is broken. Run it before guessing.

The usual causes, in the order they occur:

1. **The stack is not running.** `copilotscope up`, then check `copilotscope status`.
2. **Claude Code has telemetry off.** It exports nothing at all without
   `CLAUDE_CODE_ENABLE_TELEMETRY=1`, *plus* the logs exporter that actually carries the
   session. `copilotscope connect claude-code` writes all of it into `~/.claude/settings.json`,
   which applies to every terminal and project. Restart `claude` afterwards.
3. **VS Code was not reloaded.** `copilotscope connect vscode` writes user settings; the
   window has to be reloaded before Copilot Chat picks them up.
4. **Cowork is configured in the desktop app's own settings UI**, and wants the full
   `/v1/logs` path. No script can write that file.
5. **Nothing has happened yet.** A session appears after the assistant actually does
   something. `copilotscope probe` sends one session over the real OTLP path to prove
   ingest works, and `copilotscope demo` loads fabricated sessions to prove the UI does.

For a no-setup path — scoring the Claude Code history already on disk — use
`copilotscope import`. It needs no telemetry configuration at all.

## Useful commands

```bash
copilotscope up                    # start the stack; dashboard on http://localhost:5200
copilotscope connect claude-code   # or: vscode · copilot-cli · cowork · all
copilotscope import                # score the Claude Code history already on disk
copilotscope demo                  # fabricated sessions, badged DEMO
copilotscope doctor                # diagnose "it runs but no sessions appear"
copilotscope status | logs | open  # inspect the running stack
```

`connect` takes `--capture` to also export prompt, response and tool text. It is off by
default because it is sensitive. Do not suggest it casually, and never suggest it for a
shared deployment without saying what it means: full prompt and response content lands in
the collector, and anything holding a Read-scope key can read it back.
