# Claude Code transcript fixture

Kept here rather than under `tests/fixtures/`: that tree is the captured-OTLP corpus from #92,
whose directory names are validated against the emitter list. This is a different kind of
artefact — an assistant's own log file, not a payload the collector received.

`sample-session.jsonl` is a hand-written transcript in the shape Claude Code writes to
`~/.claude/projects/<encoded-cwd>/<sessionId>.jsonl`. It exercises the paths the importer has
to get right, and only those:

- a user prompt, an assistant reply carrying `usage`, and the token fields inside it
- an assistant message split across `text` and `tool_use` blocks — one model call, two blocks,
  which is where a naive parser doubles the call count
- a `tool_result` arriving as a *user* message, which is how tool outcomes are transported and
  is where a naive parser invents extra turns
- a failed tool (`is_error`)
- a second turn, so turn boundaries are actually tested
- a `summary` line and a `system` line, which carry no measurable work
- a deliberately truncated final line — the normal state of a session Claude Code is still
  writing to, and the one thing that must not fail the whole import

It is **not** captured from a real session: a real transcript is prompts and source code, and
the repository is not the place for either. `tools/CopilotScope.FixtureCapture` exists for
capturing real payloads locally when a format question needs settling against reality.
