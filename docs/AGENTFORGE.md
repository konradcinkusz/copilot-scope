# AgentForge — opt-in persona agents grounded on consented sessions

`src/CopilotScope.AgentForge` is an experimental, opt-in sibling service to the Collector. It
builds a chat agent (via Azure AI Foundry + Microsoft Agent Framework) whose responses are
grounded on a specific person's own captured session transcripts and quality signals — nothing
more, nothing hidden.

This document exists because the feature is more sensitive than the rest of CopilotScope: it
uses a person's own recorded work as grounding context for something that talks *as* them. Read
this before enabling it for a team.

## Purpose

AgentForge does **not** train or fine-tune a model on raw session data by default, and it does
**not** attempt to clone a person's low-level actions (tool calls, edits, keystrokes) — the
Collector doesn't capture that level of detail, by design (see the main README's "How not to use
CopilotScope" section). Instead, AgentForge assembles a **context-grounded agent**: it retrieves
a person's consented session transcripts and quality metrics, folds a bounded set of them into a
system prompt as exemplars, and hands that prompt to a model hosted in Azure AI Foundry via
Microsoft Agent Framework (MAF). The agent's "style" comes from in-context grounding, not
weights.

## Opt-in / no hidden identity

CopilotScope's Collector deliberately stores **no per-person identity** — no `user.id`, no
author field, nothing that would let you reconstruct "which sessions belong to which developer"
automatically. AgentForge does not change that and does not try to work around it. The only way
sessions become associated with a person in AgentForge is a **`PersonaCohort`**: an explicit,
manually-authored configuration entry naming which session ids belong to a named persona, who
granted consent, and when.

```csharp
public sealed record PersonaCohort(
    string PersonaId,
    string DisplayLabel,
    string ConsentGrantedBy,
    DateOnly ConsentDate,
    List<string> SessionIds);
```

There is no code path that infers a `PersonaCohort` from telemetry. If a cohort isn't in config,
AgentForge has no data for that persona.

## Revoking consent

- **Removing a persona from serving traffic immediately**: `DELETE /api/personas/{personaId}`
  clears the provisioned agent (system prompt + cached profile) from the running process's
  memory. Subsequent chat requests for that persona return `409` until re-provisioned.
- **Permanently withdrawing a persona**: remove its `PersonaCohort` entry from
  `CopilotScope:AgentForge:Cohorts` in configuration and restart the service (or redeploy with
  the updated config). Once removed, `PersonaProfileBuilder` has no session ids to read and the
  persona can no longer be provisioned at all.

## Every response is labeled as a simulation

Every `POST /api/personas/{personaId}/chat` response includes `"simulated": true` and the
`personaId` it was grounded on. This field is hard-coded in the response DTO — it does not come
from configuration and cannot be turned off. AgentForge is a simulation of a working style built
from consented transcripts; it is not, and must never present itself as, the person it is
grounded on.

## Example usage

Verified locally against a real Collector seeded via `dotnet run --project tools/CopilotScope.Seeder -- quick`.

Configure a cohort (e.g. in `appsettings.Development.json`) with real, consented session ids:

```json
{
  "CopilotScope": {
    "AgentForge": {
      "Cohorts": [
        {
          "PersonaId": "demo-persona",
          "DisplayLabel": "Demo Persona (placeholder)",
          "ConsentGrantedBy": "local-dev",
          "ConsentDate": "2026-08-08",
          "SessionIds": ["seed-quick-01-golden", "seed-quick-chat-auth-ratelimit"]
        }
      ]
    }
  }
}
```

### Talking to a secured Collector

AgentForge reads sessions from the Collector, and `infra/main.bicep` makes the Collector's
ingest key a **required** parameter — so every Azure deployment runs a Collector whose whole
`/api` group is gated. Present a key or every profile build 401s at request time:

```json
{
  "CopilotScope": {
    "AgentForge": {
      "CollectorBaseUrl": "http://localhost:4318",
      "CollectorApiKey": "<a Read-scoped Collector key>"
    }
  }
}
```

A **Read**-scoped key (`CopilotScope:Keys:Read` on the Collector) is the right one: AgentForge
only ever reads sessions a cohort already names, and a Read key cannot delete or seed. Unset,
it falls back to `CopilotScope:Ingest:ApiKey`, so a compose file exporting one shared key keeps
working. `GET /api/health` reports `collectorAuthConfigured` so a missing key is visible before
the first request fails.

Note this is the *outbound* key. `CopilotScope:AgentForge:Ingest:ApiKey` is the separate
*inbound* one, which callers of AgentForge's own endpoints must present.

Preview the assembled profile before provisioning (no Azure AI call, just Collector reads):

```bash
curl http://localhost:5300/api/personas/demo-persona/profile
# → { "personaId": "demo-persona", "sessionsUsed": 2, "avgQualityScore": 87.8,
#     "commonTools": ["editFile", "runTests", ...], "exemplars": [ ... ] }
```

Provision the agent (builds the profile + system prompt, caches it in memory):

```bash
curl -X POST http://localhost:5300/api/personas/demo-persona/provision
# → { "personaId": "demo-persona", "exemplarCount": 37, "provisionedAt": "2026-08-08T13:19:49Z" }
```

Chat with it (requires `CopilotScope:AgentForge:AzureAI:Endpoint` and `:DeploymentName` to be
configured — without them this returns `500` with a clear "not configured" message rather than
silently doing nothing):

```bash
curl -X POST http://localhost:5300/api/personas/demo-persona/chat \
  -H "Content-Type: application/json" -d '{"message":"how should I approach this bug?"}'
# → { "personaId": "demo-persona", "simulated": true, "reply": "..." }
```

Revoke immediately (chat then returns `409` until re-provisioned):

```bash
curl -X DELETE http://localhost:5300/api/personas/demo-persona
```
