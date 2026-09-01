# CopilotScope — Setup Tutorial

Step-by-step configuration for every Copilot surface that can emit OpenTelemetry,
plus troubleshooting for the most common "everything starts but no sessions appear"
situations.

## 0. Fastest path: the setup wizard

`scripts/setup.sh` / `scripts/setup.ps1` chain everything below into one command:
start the stack, wait for it to be healthy, optionally export CLI env vars into
your current shell, print the exact VS Code snippet for your endpoint/key, and
run a smoke test to confirm telemetry actually reaches the collector.

```bash
# macOS / Linux — source it if you want Copilot CLI / Claude Code env vars
# exported into THIS shell (export only survives in the sourcing shell):
source ./scripts/setup.sh --copilot-cli
```

```powershell
# Windows — env vars land in the current session either way
.\scripts\setup.ps1 -CopilotCli
```

Run `./scripts/setup.sh --help` / `Get-Help .\scripts\setup.ps1 -Full` for all
options (`--mode compose|aspire|skip-start`, `--claude-code`,
`--capture-content`, `--traces`, `--endpoint`, `--api-key`, `--skip-verify`). For VS Code,
the wizard only prints the snippet — you still edit `settings.json` and reload
the window (step 2 below explains why that step can't be automated).

CLI env vars (`--copilot-cli` / `--claude-code`) only live in the shell you
sourced the wizard in — add `--persist` (`-Persist` on Windows) to also write
them to your shell rc file (`~/.zshrc`/`~/.bashrc`, auto-detected) or the
Windows User environment scope, so new terminals pick them up without
re-running anything. Safe to re-run — it replaces its own block instead of
duplicating.

The rest of this document is the manual walkthrough the wizard automates —
useful if you want to understand each step, configure a surface the wizard
doesn't cover yet, or troubleshoot (section 9).

## 1. Start CopilotScope

Pick one of two ways to run it — both expose the same two things: an OTLP ingest
endpoint on **:4318** and the dashboard UI.

**A. .NET Aspire (recommended for development)** — requires .NET 8 SDK + Docker:

```bash
dotnet run --project src/CopilotScope.AppHost
```

The Aspire dashboard opens in your browser. It shows four resources: `postgres`
(container with a persistent volume), `postgres-pgadmin` (browse the `sessions`
table directly from here), `collector` (pinned to http://localhost:4318) and
`dashboard` (click its endpoint link to open the UI).

**B. docker-compose (containers + Postgres + API key):**

```bash
docker compose up --build     # dashboard on :5200, ingest on :4318, key: dev-secret-123
```

Verify the collector is up before configuring any client:

```bash
curl http://localhost:4318/api/health
```

## 2. VS Code (Copilot Chat)

1. Open Settings JSON (`Ctrl+Shift+P` → *Preferences: Open User Settings (JSON)*).
2. Add:

```jsonc
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:4318",
  "github.copilot.chat.otel.exporterType": "otlp-http",
  // Optional — sends prompt/response text so the dashboard's
  // "Prompts & responses" panel has content. Only in trusted environments:
  "github.copilot.chat.otel.captureContent": true
}
```

3. **Reload the VS Code window** (`Ctrl+Shift+P` → *Developer: Reload Window*).
   Settings are read at extension startup — this step is not optional.
4. Open Copilot Chat and send a message **in Agent mode** (or any chat interaction).
   Plain inline code completions do not produce chat telemetry.
5. The session appears on the CopilotScope dashboard within seconds.

Environment variables (`COPILOT_OTEL_ENABLED=true`, `OTEL_EXPORTER_OTLP_ENDPOINT`)
work too and take precedence over settings — but then VS Code must be **launched
from a shell that has them exported**, not from the taskbar icon.

When OTel is enabled in VS Code, all agent types are instrumented — including
Copilot CLI agents and Claude agents running inside VS Code.

## 3. GitHub Copilot CLI (standalone terminal)

The CLI is configured via environment variables:

```bash
export COPILOT_OTEL_ENABLED=true
export COPILOT_OTEL_EXPORTER_TYPE=otlp-http     # the CLI supports otlp-http only
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
# content capture (optional, sensitive) — note this is the OTel GenAI standard
# variable; COPILOT_OTEL_CAPTURE_CONTENT does NOT exist and silently does nothing:
export OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true
copilot
```

On Windows, `scripts/Enable-CopilotOtel.ps1` sets all of the above in one go
(`-CaptureContent` switch included) and warns about the fake variable trap.

Notes:
- The CLI runtime **only supports otlp-http** — configuring gRPC silently falls
  back to HTTP, which is exactly what CopilotScope ingests.
- CLI traces appear as their own sessions (service `github-copilot`), separate
  from VS Code window sessions.
- Alternative: `COPILOT_OTEL_EXPORTER_TYPE=file` writes JSONL to
  `~/.copilot/otel/` instead of sending anywhere (not used by CopilotScope).

## 4. Claude Code (CLI) and Claude Cowork (desktop app)

Both of Anthropic's local surfaces export OpenTelemetry, and both land in
CopilotScope — but they are configured in completely different places, and
neither speaks the `gen_ai.*` span vocabulary the Copilot surfaces use.

### 4.1 Claude Code — the one variable everything depends on

`CLAUDE_CODE_ENABLE_TELEMETRY=1` is the master switch. Without it Claude Code
exports **nothing**, no matter what else is set — a correct endpoint and
protocol on their own produce an empty dashboard.

```bash
export CLAUDE_CODE_ENABLE_TELEMETRY=1
export OTEL_METRICS_EXPORTER=otlp
export OTEL_LOGS_EXPORTER=otlp                  # this is the one that carries sessions
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
export OTEL_METRIC_EXPORT_INTERVAL=10000        # default is 60 s — too slow to watch live
claude
```

```powershell
# Windows — same thing in one command, in the CURRENT session:
.\scripts\Enable-ClaudeCodeOtel.ps1
claude
```

`scripts/Enable-ClaudeCodeOtel.sh` is the bash equivalent (`source` it). Both
accept `-CaptureContent`/`--capture`, `-Traces`/`--traces`, `-ApiKey`/`--api-key`
and `-Persist`, and `-Disable`/`--disable` removes everything again.

**Keep the logs exporter on.** Claude Code emits metrics *and* log events, and
the log events are what carry the session: API calls, tokens, tool results, tool
permission decisions and prompts all arrive as `claude_code.*` events. The
metrics are mostly a coarser view of the same facts, so CopilotScope reads the
events and deliberately ignores `claude_code.token.usage` and
`claude_code.code_edit_tool.decision` — summing both would double-count every
token and every accepted edit. `claude_code.lines_of_code.count` has no event
equivalent and is read from metrics.

**Content capture is three separate opt-ins**, all off by default:

```bash
export OTEL_LOG_USER_PROMPTS=1          # prompt text on claude_code.user_prompt
export OTEL_LOG_ASSISTANT_RESPONSES=1   # response text on claude_code.assistant_response
export OTEL_LOG_TOOL_DETAILS=1          # tool arguments, bash commands, file paths
```

The GenAI standard variable `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`
does **not** work here — Claude Code does not read it. (Earlier versions of the
scripts in this repo set it; `-Disable` cleans it up.)

**Time to first token needs the tracing beta.** No Claude Code metric or event
carries TTFT, so the latency-utility insight stays empty without it:

```bash
export CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1
export OTEL_TRACES_EXPORTER=otlp
```

CopilotScope reads `claude_code.interaction` as the turn boundary and takes
`ttft_ms` off `claude_code.llm_request` — but it does **not** count those spans
as extra calls, because they mirror the `claude_code.api_request` events one for
one. It is a beta: the span schema can still change.

### 4.2 Claude Cowork — Claude desktop app

Cowork (the agent surface in the Claude desktop app) exports OTel too, but:

- It is configured in the **app's own settings UI**, not through environment
  variables — a desktop app never sees the env vars you export in a terminal.
  Open Claude Desktop → organization/Cowork settings → the monitoring section,
  enter the OTLP endpoint, pick the protocol, add headers if your collector
  needs auth. Configuration is read at session start, so **restart the app**.
- It requires a **Team or Enterprise plan** and Claude Desktop **1.1.4173 or
  later**, and setting it up needs org admin access. There is no equivalent for
  individual Free/Pro/Max accounts.
- It sends **HTTP OTLP only** (no gRPC) and wants the **full path** rather than
  the base endpoint that Claude Code takes — point it at
  `http://localhost:4318/v1/logs`, not `http://localhost:4318`.
- It exports **log events only** — no metrics, no traces. So a Cowork session in
  CopilotScope has calls, tokens, tools, edit decisions and turns, but no lines
  of code and no TTFT.

Plain Claude desktop *chat* — conversations outside Cowork — emits no OTel at
all. There is nothing to point at a collector, and this is a product limit, not
a configuration one.

## 5. Copilot SDK (your own apps)

Every SDK language accepts a telemetry config pointing at the collector:

```csharp
var client = new CopilotClient(new CopilotClientOptions
{
    Telemetry = new TelemetryConfig { OtlpEndpoint = "http://localhost:4318" }
});
```

(Equivalent one-liners exist for TypeScript, Python, Go, Java and Rust — see
the Copilot SDK OpenTelemetry docs.)

## 6. Other Copilot surfaces — current status

| Surface | OTel export | How |
|---|---|---|
| VS Code Copilot Chat | ✅ | settings / env / managed settings |
| Copilot CLI | ✅ (otlp-http only) | `COPILOT_OTEL_*` env vars |
| Copilot SDK apps | ✅ | `TelemetryConfig` |
| Claude agents inside VS Code | ✅ | same VS Code settings |
| Claude Code CLI | ✅ | `CLAUDE_CODE_ENABLE_TELEMETRY=1` + `OTEL_*` env vars (section 4) |
| Claude Cowork (desktop app) | ✅ (events only) | in-app settings UI; Team/Enterprise, Desktop 1.1.4173+ |
| Claude desktop chat (outside Cowork) | ❌ | no OTel export as of July 2026 |
| Copilot coding agent (github.com) | ➖ | runs on GitHub's infra; metrics surface in VS Code's agent-outcome metrics, no direct OTLP to your collector |
| **Visual Studio (2022/2026)** | ❌ | no OTel export as of July 2026 |
| JetBrains / Xcode / Eclipse plugins | ❌ | no OTel export as of July 2026 |
| Copilot Studio (Power Platform) | ➖ | exports OTel-aligned spans, but only to Azure Application Insights (admin-configured), not to arbitrary OTLP endpoints |

## 7. Enterprise: force the configuration centrally

Organizations can mandate the OTLP endpoint through Copilot **managed settings**
(the `telemetry` block), delivered via native MDM (Windows registry / macOS
managed preferences), a server-managed policy on the GitHub account, or
`managed-settings.json` on disk. Managed values override both env vars and user
settings, and can also lock `captureContent`. Precedence: policy → env var →
user setting → default.

## 8. Cloud / team mode

**Turn privacy mode on first.** The moment a second developer's telemetry reaches the same
collector, you are running a technical system capable of monitoring employee performance —
which in the EU triggers works-council co-determination on the capability alone, whatever
you intend to do with it. Privacy mode makes the intent enforceable: identities are
pseudonymized before anything stores them, prompt and response text is dropped, no view
renders for fewer than *k* developers, and every read is logged.

```jsonc
// appsettings.json on the collector
{
  "CopilotScope": {
    "Privacy": {
      "Enabled": true,
      "Salt": "<long random secret, stored separately from the database>",
      "MinimumGroupSize": 5
    },
    "History": { "RetentionDays": 90 }
  }
}
```

Check what a running deployment enforces with `GET /api/privacy`. The full data map,
retention behaviour and a template works-agreement annex are in
[`docs/PRIVACY.md`](PRIVACY.md) — read it before the first team deployment, not after the
first question about it.

Then deploy the collector where the team can reach it (e.g. Azure Container Apps —
`infra/main.bicep`), set an ingest key, and point clients at it:

```jsonc
// VS Code settings.json
"github.copilot.chat.otel.otlpEndpoint": "https://copilotscope.<region>.azurecontainerapps.io"
```

```bash
# auth header — exported before starting VS Code / the CLI
export OTEL_EXPORTER_OTLP_HEADERS="x-api-key=<secret>"
```

## 9. Troubleshooting: "it starts fine but no sessions show up"

Work through these in order — they cover, in practice, every case we've seen:

1. **Did you reload the VS Code window after changing settings?** OTel settings
   are read at startup. `Developer: Reload Window`, then chat again.
2. **Are you actually chatting?** Telemetry comes from *chat/agent* interactions
   (`invoke_agent` → `chat`/`execute_tool` spans). Inline tab-completions alone
   don't create chat sessions.
3. **Check the collector log.** Every accepted batch logs `New session(s)
   started`, and every *rejected* request logs a warning with the reason
   (wrong content type, bad decode, unauthorized). Silence in the log = nothing
   is reaching port 4318 → the problem is on the client side (settings,
   endpoint URL, firewall, VS Code not reloaded).
4. **exporterType mismatch.** CopilotScope ingests OTLP/HTTP protobuf. If you set
   `otlp-grpc` in VS Code, the extension speaks gRPC on your endpoint and the
   collector rejects it (415 in logs). Set `"otlp-http"` (the default).
5. **Compressed payloads** (`Content-Encoding: gzip/deflate`) are handled by the
   collector — if you run an older build, update: this was a real
   "logs look fine, sessions empty" cause.
6. **Env vars overriding settings.** If `OTEL_EXPORTER_OTLP_ENDPOINT` is exported
   in the shell VS Code started from, it wins over settings.json — check
   `echo $OTEL_EXPORTER_OTLP_ENDPOINT`.
7. **Managed settings overriding you.** On a company machine, enterprise policy
   may pin the endpoint to a corporate collector. Managed values always win.
8. **API key mode.** In Production the `/v1/*` routes require `x-api-key`; a
   missing header is a 401 warning in the collector log. Export
   `OTEL_EXPORTER_OTLP_HEADERS="x-api-key=<secret>"` before launching the client.
9. **A second session named "unattributed" appears next to your real one.**
   Fixed in current builds: CLI metrics and logs carry no conversation id (and,
   unlike VS Code, no `session.id` resource attribute), so they used to pile up
   in a permanent "unattributed" bucket — taking edit-acceptance and feedback
   data with them. The collector now maps each emitter (resource fingerprint) to
   its most recent conversation and merges the bucket into it as soon as the
   conversation identifies itself. If you still see one, rebuild the collector
   image; a bucket may also appear briefly at startup before the first
   conversation span arrives — it disappears on its own.
10. **Claude Code is running but nothing arrives.** In order: is
   `CLAUDE_CODE_ENABLE_TELEMETRY=1` set (nothing is exported without it), is
   `OTEL_LOGS_EXPORTER=otlp` set (the events are what carry the session), and are
   you running `claude` from the *same* terminal you exported the variables in?
   `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` is not read by Claude
   Code — prompts need `OTEL_LOG_USER_PROMPTS=1`. Metrics also default to a 60 s
   export interval, so a short session can look empty for a minute; the scripts
   set `OTEL_METRIC_EXPORT_INTERVAL=10000` to avoid that.
11. **Cowork is configured but nothing arrives.** Cowork wants the *full* path,
   not the base endpoint Claude Code takes: `http://localhost:4318/v1/logs`. It
   also reads its configuration at session start — restart Claude Desktop.
12. **Sanity check the pipeline without Copilot:**
   `dotnet run --project tools/CopilotScope.TelemetryGen -- http://localhost:4318 probe`
   — if `probe` shows up on the dashboard, CopilotScope is healthy and the issue
   is purely client configuration.
13. **Want a populated dashboard instead of one probe session?** Run
   `dotnet run --project tools/CopilotScope.Seeder -- demo` — it builds a big,
   varied set of comprehensive sessions (clean, error-prone, laggy,
   rejected-edits, frustrated, internal helper calls, ...) plus a 30+ turn
   **showcase** chat that exercises every dashboard panel at once, and posts
   them straight to the running collector's `/api/admin/seed`. It always clears any
   previously seeded data first, so it's safe to re-run against a container
   that's already up (no restart needed).

## 10. Privacy note

By default Copilot sends **metadata only** (models, tokens, durations, tool
names, error types) — no prompts, no code. The "Prompts & responses" panel stays
empty unless `captureContent` is enabled on the client. Repository URLs are
stripped of embedded credentials before display or storage.

That is the default, not a guarantee: a client with `captureContent` on sends the
conversation, and the collector stores it. **Privacy mode** turns the default into
something enforced — content dropped at ingest, identities pseudonymized, an aggregation
floor on every view, and an access log — regardless of how the clients are configured. For
any deployment with more than one developer on it, see
[`docs/PRIVACY.md`](PRIVACY.md), which also carries the GDPR Art. 30 data map, the
retention and deletion behaviour, and a template works-agreement annex.
