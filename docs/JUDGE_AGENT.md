# JudgeAgent — opt-in, cloud-only session quality judge

`src/CopilotScope.JudgeAgent` is an opt-in, cloud-only sibling service to the Collector. It grades
one session at a time using LLM-graded rubrics (G-Eval, SPUR, RAGAS, deep frustration
classification, task-completion detection) via Azure AI Foundry + Microsoft Agent Framework
(MAF). This is the "judge agent" the main README and `docs/STRATEGY.md` have described as planned
since before this service existed — the five algorithms in the README's "Evaluation algorithms"
table marked `❌ not implemented` / `🔜 planned` were blocked on exactly this.

## Grades sessions, never people

Same hard rule as the rest of CopilotScope (see the main README's "How *not* to use
CopilotScope" section): JudgeAgent scores **one recorded session**, not the person who ran it.
`JudgeSystemPromptTemplate.txt` — the master rubric sent to the judge model on every call — states
this explicitly in its role instructions and tells the model to refuse any request to rank or
compare people. There is no per-developer view here either, and none is planned. JudgeAgent has
no concept of "who ran this session" at all; the Collector stores no per-person identity, and
JudgeAgent doesn't add one.

## Why this is cloud-only

Local analyzers (`Quality/Insights.cs`'s `IInsightAnalyzer` implementations in the Collector) run
synchronously, in-process, on metadata the Collector already has. The five algorithms here need an
LLM call against real transcript content, which means:
- **Model access** — a deployed Azure AI Foundry model and the credentials to call it.
- **A prompt/token budget** — every judge call sends up to ~40 transcript turns of prompt/response
  text, which local-only analyzers never do.
- **Judge-bias awareness and calibration** — see `docs/ANALYSIS.md` §8/§8a for why SPUR in
  particular is explicitly "directional, not final" until CopilotScope collects labeled SAT/DSAT
  session data to calibrate against. The machinery for that measurement now exists
  (`Calibration/`, `POST /api/calibration/report` and `/run`, Cohen's κ against human labels) —
  see **[docs/CALIBRATION.md](CALIBRATION.md)**. No calibration has been run yet, so every score
  below is still directional.

That's why this lives in its own deployable service rather than as another `IInsightAnalyzer`
registered into the Collector's `InsightPipeline` — that interface is synchronous and local-only;
a judge call is an async network call to Azure. A local-only deployment simply never runs this
service, and the five algorithms it covers stay unavailable, exactly as the README's table says.

## The five algorithms

| # | Algorithm | What it answers |
|---|---|---|
| 1 | G-Eval | Correctness / completeness / style, weighted 0.5/0.3/0.2, evidence-cited per turn |
| 2 | SPUR | P(user would rate this session SAT), from behavioral signals — zero-shot until calibration data exists |
| 3 | RAGAS | Faithfulness / answer relevance / context precision — only when `retrievalContext` is present |
| 4 | Deep frustration classification | Sarcasm- and context-aware upgrade over the Collector's local lexicon heuristic; stays report-only |
| 5 | Task-completion detection | Did the user's original ask actually get resolved by session end, not just attempted |

Full per-rubric instructions live in `src/CopilotScope.JudgeAgent/Agents/JudgeSystemPromptTemplate.txt`
— that file **is** the spec; this document doesn't duplicate it. Every calibration report records
a hash of it (`judgePromptVersion`), so a later edit to the rubric shows up as a re-baseline
rather than a silently moved measuring stick.

## Before trusting any of these scores

None of the five rubrics has been calibrated against human labels, so none of them should gate
anything. [docs/CALIBRATION.md](CALIBRATION.md) explains what calibration measures (Cohen's κ
against a human panel, with the panel's own agreement as the ceiling), how to run it, and how to
read a bad result. The endpoints are `POST /api/calibration/report` (offline, free, deterministic)
and `POST /api/calibration/run` (live, one metered model call per session).

## Request flow

```
POST /api/sessions/{id}/judge
  1. GetSessionDetailAsync(id) — the Collector's own read API (ICollectorClient), same as AgentForge
  2. SessionJudgeContextBuilder — reshapes SessionDetailDto into a bounded judge payload
     (transcript capped at 40 turns, keeping both ends of a longer session so the resolution
     is never dropped; each prompt/response field capped at 4000 chars)
  3. JudgePromptBuilder — renders JudgeSystemPromptTemplate.txt with {{SessionId}} and
     {{LocalComponentsSummary}} filled in
  4. IJudgeChatClient.JudgeAsync(systemPrompt, sessionPayloadJson) — Azure AI Foundry call via MAF
  5. JudgeResponseParser — parses the model's JSON straight into InsightReport records
     (CopilotScope.Collector.Quality), the same shape every local analyzer already produces
  6. Response: { "results": [ ...5 InsightReport objects... ] }
```

`localComponents` in the payload (the session's already-computed reliability / acceptance /
friction / latency / feedback / efficiency, from `SessionDetailDto.Summary.Quality.Components`) is
passed to the judge model as *prior context*, not ground truth — the rubric explicitly instructs
it to disagree with the local heuristic when transcript evidence says otherwise, and to say why.

`completionSignals` and `retrievalContext` are defined in the payload schema but always sent as
`null` today — the Collector has no ingest path for external build/test exit codes or captured
retrieval context yet (see the README's evaluation-algorithms table, row 10: "local partial" only).
When the Collector gains that ingest path, `SessionJudgeContextBuilder` is the one place that needs
to change to start populating them.

## Running it

JudgeAgent is a normal ASP.NET Core service — it only ever *reads* from a Collector over HTTP
(`ICollectorClient`), so start a Collector first (any of the ways the main README describes)
before starting JudgeAgent. Three ways to run it, same options the rest of the repo offers:

**Standalone, from source** (fastest inner loop while touching JudgeAgent code):

```bash
dotnet run --project src/CopilotScope.JudgeAgent
```

`src/CopilotScope.JudgeAgent/Properties/launchSettings.json` fixes this to
`http://localhost:5400` in the `Development` environment — no environment variables to set by
hand. `appsettings.Development.json` points it at a Collector on `http://localhost:4318` (e.g.
one started via `dotnet run --project src/CopilotScope.AppHost` or `dotnet run --project
src/CopilotScope.Collector`). Verified locally: `curl http://localhost:5400/api/health` responds
immediately after `Application started` appears in the console.

**Aspire (whole stack together)**:

```bash
dotnet run --project src/CopilotScope.AppHost
```

Starts Postgres, the Collector (fixed on `:4318`), the Dashboard, AgentForge and JudgeAgent
together, each waiting on the Collector via `.WaitFor(collector)`. JudgeAgent's URL isn't
fixed in this mode — open the Aspire dashboard (URL printed in the console) and find the
`judgeagent` resource there for its actual port.

**Docker Compose** (closest to how the GHCR images run in production):

```bash
docker compose up --build          # builds Dockerfile.judgeagent locally, or:
docker compose -f docker-compose.ghcr.yml up   # pulls the published image
```

Both compose files start `judgeagent` on `http://localhost:5400` alongside `postgres`,
`collector`, `dashboard` and `agentforge` — it isn't gated behind a Compose profile, so it
starts by default like AgentForge does. "Opt-in" here means *configuration*, not *whether the
container runs*: without `CopilotScope__JudgeAgent__AzureAI__Endpoint` /
`__DeploymentName` set (commented out by default in both compose files), the container is up and
`/api/health` responds, but every `/judge` call fails with the "not configured" error below until
you supply real Azure AI Foundry credentials.

## What it doesn't do

- Doesn't modify anything in `src/CopilotScope.Collector` — a strictly read-only, additive sibling
  service, same non-invasive pattern as AgentForge.
- Doesn't recompute or replace `QualityEngine`'s composite score — it adds cloud-only signals
  alongside it. Promoting any of them (e.g. deep frustration) into the composite is a future,
  separate decision made by config, not something this service does on its own.
- Doesn't retry or cache judge calls — every `POST /judge` is a fresh model call. Add caching at
  the caller if you're judging the same session repeatedly.

## Example usage

Verified locally against a real Collector seeded via `dotnet run --project tools/CopilotScope.Seeder -- quick`:

```bash
curl http://localhost:5400/api/health
# → { "status": "ok", "azureAiConfigured": false }

curl -X POST http://localhost:5400/api/sessions/seed-quick-01-golden/judge
# Without CopilotScope:JudgeAgent:AzureAI:Endpoint/:DeploymentName configured, this returns 500
# with a clear "JudgeAgent Azure AI is not configured" message rather than doing nothing silently
# — verified by actually running it. With real Azure AI Foundry credentials configured:
# → { "results": [ { "name": "LLM-as-a-Judge (G-Eval)", "algorithm": "G-Eval", "status": "ok",
#                     "score": 0.82, "metrics": [...], "findings": [...] }, ...4 more ] }
```

Configure Azure AI Foundry (e.g. in `appsettings.Development.json` or environment variables):

```json
{
  "CopilotScope": {
    "JudgeAgent": {
      "CollectorBaseUrl": "http://localhost:4318",
      "CollectorApiKey": null,
      "AzureAI": {
        "Endpoint": "https://<your-foundry-resource>.openai.azure.com",
        "DeploymentName": "<your-model-deployment>",
        "ApiKey": null
      }
    }
  }
}
```

`ApiKey: null` (the default) uses `DefaultAzureCredential` (managed identity / `az login`) instead
of a key, same as AgentForge.

### Two keys, in opposite directions

Secured deployments need both, and they are easy to confuse:

| Setting | Direction | What it is for |
|---|---|---|
| `CopilotScope:JudgeAgent:Ingest:ApiKey` | **inbound** | the key a caller must present to `POST /api/sessions/{id}/judge` |
| `CopilotScope:JudgeAgent:CollectorApiKey` | **outbound** | the key this service presents *to the Collector* when reading a session |

The outbound one is the one people miss. `infra/main.bicep` makes the Collector's ingest key a
**required** parameter, so every Azure deployment runs a Collector whose whole `/api` group is
gated — and a judge that presents no key gets a 401 on every session read, at request time, with
nothing at startup to warn about it. Give it a **Read**-scoped Collector key
(`CopilotScope:Keys:Read`): the judge only ever reads the one session a request names, and a
Read key cannot delete or seed.

If `CollectorApiKey` is unset it falls back to `CopilotScope:Ingest:ApiKey`, so a compose file
that already exports one shared key keeps working with no extra configuration.

Check it before you need it — `GET /api/health` reports `collectorAuthConfigured`:

```bash
curl -s http://localhost:5400/api/health
# {"status":"ok","collectorAuthConfigured":true,"azureAiConfigured":true}
```
