# 2. Connect your assistant

**Situation:** the stack runs and shows demo data. **Result:** your own sessions,
scored as you work.

One command per assistant. Each writes the settings file that assistant actually
reads, so the configuration survives new terminals, new projects and reboots.

```bash
copilotscope connect claude-code     # ~/.claude/settings.json
copilotscope connect vscode          # VS Code user settings
copilotscope connect copilot-cli     # shell rc, or Windows User scope
copilotscope connect cowork          # prints what to type into the app
copilotscope connect all             # Claude Code and VS Code together
```

Two flags apply everywhere:

- `--print` shows exactly what would be written and changes nothing. Run this
  first if you would rather see it before it happens.
- `--capture` also exports prompt and response text. **Off by default**, and it
  is the switch that turns a metadata-only deployment into one holding source
  code and anything a developer pasted into a chat.

`copilotscope disconnect <target>` removes exactly the keys that were added and
leaves the rest of your settings alone.

## Then the step no script can take

| Assistant | What you have to do yourself |
|---|---|
| VS Code | Reload the window — `Ctrl/Cmd+Shift+P` → *Developer: Reload Window*. Settings are read at extension startup |
| Claude Code | Nothing. Start or restart `claude` |
| Copilot CLI | Open a new terminal, or `source` your shell rc |
| Cowork | Restart Claude Desktop — configuration is read at session start |

Then **chat in agent mode**. Inline completions alone produce no chat telemetry,
and this is the second most common reason for an empty dashboard.

## Assistant-specific notes worth knowing

### Claude Code

Four values matter, and the first two are the ones people omit when doing this
by hand:

- `CLAUDE_CODE_ENABLE_TELEMETRY=1` — the master switch. Without it nothing at
  all is exported, whatever else is set.
- `OTEL_LOGS_EXPORTER=otlp` — the log events are what carry the session. A
  default install emits metrics and events and no spans, so setting only the
  metrics exporter gives you an almost empty session.

Two optional extras:

```bash
copilotscope connect claude-code --traces    # time to first token (beta)
copilotscope connect claude-code --capture   # prompt, response and tool text
```

`--traces` turns on the tracing beta, which is the **only** source of
time-to-first-token for this assistant. The span schema can still change; that
is what the beta flag means.

Content capture here is three separate opt-ins, and the OpenTelemetry GenAI
standard variable that works for Copilot CLI is **not** read by Claude Code.

### Copilot CLI

`COPILOT_OTEL_CAPTURE_CONTENT` is not a real variable and silently does nothing.
The CLI follows the OpenTelemetry GenAI standard instead, which is what
`--capture` sets.

### Cowork

Configured in the desktop app's own settings UI — there is no file to write.
It wants the **full path**, `http://localhost:4318/v1/logs`, not the base
endpoint the others take. Requires a Team or Enterprise plan, Claude Desktop
1.1.4173 or later, and org admin access. It exports log events only, so a Cowork
session has no lines-of-code and no time-to-first-token.

## Check it

```bash
copilotscope doctor
```

If a session still does not appear, the classic causes in order of frequency:

1. VS Code was not reloaded.
2. Inline completions only — no chat or agent turn happened.
3. Claude Code: the master switch or the logs exporter is missing.
4. An exported `OTEL_EXPORTER_OTLP_ENDPOINT` beats the settings file for
   anything launched from that shell. `doctor` checks this explicitly.
5. Enterprise managed settings pin the endpoint to a corporate collector.
   Managed values always win.

## Next

[Reading a session](03-reading-a-session.md).
