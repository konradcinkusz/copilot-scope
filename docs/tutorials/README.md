# Tutorials

Step-by-step, hands-on. Each one starts from a stated situation and ends with
something you can see on screen.

These complement rather than replace the two other documents:

| Document | What it is for |
|---|---|
| **These tutorials** | Doing it, in order, the first time |
| [The manual](../papers/copilotscope-manual.tex) (PDF, EN/PL) | Understanding the whole system: architecture, every signal, the limits |
| [`docs/TUTORIAL.md`](../TUTORIAL.md) | Reference: every assistant's configuration in full, plus troubleshooting |

Every tutorial exists in English and Polish, and a CI check
([`scripts/check-doc-parity.mjs`](../../scripts/check-doc-parity.mjs)) fails the
build if one half is edited without the other — a translation that drifts is
worse than no translation, because the reader trusts it.

## In order

1. **[First run](01-first-run.md)** — from an empty machine to a scored session.
   Nothing to install first.
2. **[Connect your assistant](02-connect-your-assistant.md)** — real telemetry
   from Claude Code, VS Code, Copilot CLI or Cowork.
3. **[Reading a session](03-reading-a-session.md)** — what the numbers mean and
   which one to act on.
4. **[A team deployment](04-team-deployment.md)** — more than one machine, which
   changes the security and privacy posture entirely.

## If something does not work

```bash
copilotscope doctor
```

It checks that CopilotScope is running and its dashboard files are in place, what
each assistant's settings file actually says and where it points, whether an
exported variable is overriding that file, and the history on disk. On the Docker
stack, the control script's `doctor` checks Docker, the containers, and whether the
deployment is exposed without a key.
