# 4. A team deployment

**Situation:** it works on your machine and someone wants it for the team.
**Result:** a deployment that is defensible to a security reviewer and to a
works council.

Read this one before running anything. The moment a second person's telemetry
reaches the same collector, the system changes category.

## What changes, and why it is not optional

A collector holding one developer's sessions is a personal tool. A collector
holding a team's sessions is **a technical system capable of monitoring employee
performance**. In the EU that triggers works-council co-determination on the
capability alone, whatever you intend to do with it.

The single-machine defaults are therefore scoped, not lax:

| | One machine | Shared |
|---|---|---|
| Ports | `127.0.0.1` | published deliberately |
| Ingest key | none — open mode | required, and split into scopes |
| Postgres | no published port, `trust` | password + `scram-sha-256` |
| Dashboard | open | sign-in, two roles |
| Privacy mode | off | on, before the first other person's data arrives |

## Step 1 — turn privacy mode on first

Before pointing anybody else's editor at it, not after the first question about
it.

```jsonc
// collector appsettings.json
{
  "CopilotScope": {
    "Privacy": {
      "Enabled": true,
      "Salt": "<a long random secret, stored separately from the database>",
      "MinimumGroupSize": 5
    },
    "History": { "RetentionDays": 90 }
  }
}
```

What it enforces:

- identities pseudonymized **before anything stores them**;
- prompt and response content dropped at ingest, regardless of how the clients
  are configured;
- no view renders for fewer than *k* developers;
- every read logged, to a table the retention sweep never touches — the audit
  record has to outlive the sessions it describes;
- raw OTLP forwarding refused unless you explicitly state that the upstream
  backend is covered by the same agreement.

Set the salt. If you forget, an ephemeral one is generated and the collector
warns loudly, because pseudonyms that change on every restart stop correlating
history across a deploy.

Verify what is actually enforced rather than trusting the file:

```bash
curl http://localhost:4318/api/privacy
```

[`docs/PRIVACY.md`](../PRIVACY.md) carries the GDPR Article 30 data map, the
retention and deletion behaviour, and a template works-agreement annex.

## Step 2 — publish it, with a key in the same breath

```bash
copilotscope up --bind 0.0.0.0 --api-key "$(openssl rand -hex 24)"
```

Both the installer and the control script **refuse** `--bind` without
`--api-key`, because a collector reachable on the network with no key accepts
telemetry, serves transcripts and allows deletes to anyone who can reach the
port. The collector also logs a startup warning if it finds itself published
beyond loopback with no key, since it cannot work that out for itself — behind
Docker every request arrives from the bridge gateway.

Set the database credentials in the same change:

```bash
POSTGRES_PASSWORD=$(openssl rand -hex 16)
POSTGRES_HOST_AUTH_METHOD=scram-sha-256
```

## Step 3 — split the one key into scopes

The key every developer's editor holds should not also be the key that reads
transcripts and deletes history.

```jsonc
"CopilotScope": { "Keys": {
  "Ingest": ["emitter-key"],      // POST /v1/* only
  "Read":   ["dashboard-key"],    // /api/* + /metrics
  "Admin":  ["operator-key"]      // delete, seed, import — implies Read
} }
```

## Step 4 — put a password on the dashboard

Transcripts are the sensitive payload: with content capture on, that text can
contain source code, credentials someone pasted into a chat, and customer data.

```jsonc
"CopilotScope": { "Dashboard": { "Auth": {
  "ViewerPassword": "…",   // scores, turns, aggregates
  "AdminPassword":  "…"    // + transcripts and delete
} } }
```

## Step 5 — terminate TLS

Neither compose file does. These credentials travel in a header and a cookie, so
put a reverse proxy in front of anything shared.

## Step 6 — make it produce an output, not just a dashboard

A dashboard that has to be visited gets abandoned.

```jsonc
"CopilotScope": { "Alerts": {
  "Enabled": true,
  "WebhookUrl": "https://hooks.example.com/services/…",
  "Format": "slack",
  "WindowDays": 7,
  "ScoreDropPoints": 5,
  "MinSessionsPerWindow": 10,
  "Digest": true
} }
```

Note what deliberately does *not* fire: a drop that came with a confidence drop
is reported as a changed measurement basis rather than a regression. A cohort
that stopped reporting a signal is being measured differently, not performing
worse.

## The thing to say out loud to the team

The score grades a **session**, not a person. There is no per-developer view, no
per-developer axis in any cohort filter, and no export with such a column —
tests assert this, so it is enforced rather than promised.

Say this before the first question about it, not after. The useful question is
"where is our AI tooling wasting people's time", not "who is the best
developer", and a tool deployed with the second question in the air will be
resisted no matter what its code does.

## Further reading

- [`SECURITY.md`](../../SECURITY.md) — the trust model, scope by scope
- [`docs/PRIVACY.md`](../PRIVACY.md) — data map, retention, works-agreement annex
- [`GOVERNANCE.md`](../../GOVERNANCE.md) — who maintains this, what is stable,
  and what happens if the maintainer stops
