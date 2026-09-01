# Security Policy

## Reporting a vulnerability

Report privately through GitHub's
[security advisory form](https://github.com/konradcinkusz/copilotscope/security/advisories/new).
Please do not open a public issue for a vulnerability.

Expect an acknowledgement within a week. This is a single-maintainer project — if
a fix will take longer than that, you will be told so rather than left waiting.

## Supported versions

The latest release only. There are no maintained back-branches.

## What CopilotScope handles

Worth knowing before you deploy it, because the sensitivity depends entirely on
one setting:

- **Metadata only (default).** Token counts, latencies, model names, tool names,
  error types. No prompt or response text.
- **With content capture enabled** (`captureContent` on the client, or
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`), prompt and response
  text arrives in span attributes and is stored in the session snapshot — bounded
  to the last 100 entries, 4 000 chars each. That text can contain source code,
  credentials a developer pasted into a chat, and customer data. Treat the Postgres
  volume and the dashboard as carrying whatever your developers typed.

## Trust model

Two independent credentials: API keys guard the collector, a password guards the
dashboard UI. Both are off by default, which suits a laptop and nothing else.

**Collector — scoped API keys.** Three scopes, configured under `CopilotScope:Keys`:

| Scope | Grants | Held by |
|---|---|---|
| `Ingest` | `POST /v1/traces\|metrics\|logs` | every developer's editor / CLI |
| `Read` | `/api/*` queries and `/metrics` | the dashboard, Prometheus |
| `Admin` | `DELETE`, `/api/admin/seed`, plus everything `Read` grants | operators |

`Admin` implies `Read`; `Ingest` is orthogonal, so an admin key is not silently a
valid telemetry writer and an emitter key cannot reach captured transcripts. Each
scope takes a list, so a key can be rotated by adding the new one, moving clients
over, then removing the old.

```jsonc
"CopilotScope": {
  "Keys": {
    "Ingest": ["emitter-key"],
    "Read":   ["dashboard-key", "prometheus-key"],
    "Admin":  ["operator-key"]
  }
}
```

The legacy single key (`CopilotScope__Ingest__ApiKey`) still works and still grants
**every** scope — an upgrade never locks a running deployment out of itself. Scoping
only takes effect once `CopilotScope:Keys` is populated. **With no key set at all,
ingest and the query API are open**; that default suits localhost, not a shared host.

**Dashboard — sign-in with two roles.** Set a password to turn it on:

```jsonc
"CopilotScope": { "Dashboard": { "Auth": {
  "ViewerPassword": "…",
  "AdminPassword":  "…"
} } }
```

- *Viewer* — scores, turn analysis, aggregates. **No conversation transcripts, no delete.**
- *Admin* — everything, including reading captured prompts/responses and deleting sessions.

Transcripts are admin-only because that text is the sensitive payload: it is the
conversation itself, including whatever a developer pasted into it. With no password
configured the dashboard is unauthenticated exactly as before — do not expose it
beyond localhost in that state.

The dashboard authenticates its *own* users; it still presents a collector API key
(`CopilotScope__Ingest__ApiKey`) for its calls, and that key should be a `Read`-scoped
one so a dashboard compromise cannot delete history.

Neither compose file terminates TLS. Put a reverse proxy in front of anything shared —
these credentials travel in a header and a cookie.
## Deployment notes

- `docker-compose.grafana.yml` runs Grafana with anonymous Admin and no login form.
  That is a local-demo convenience; remove the `GF_AUTH_ANONYMOUS_*` settings for
  anything shared.
- The seed endpoint can fabricate session data. It shares the ingest key
  deliberately: anyone who can post fake OTLP can already fabricate sessions, so
  it does not widen the trust boundary — but it does mean the ingest key is enough
  to pollute the dataset.
