# ADR-005 — Session review on the user's own assistant: the collector counts, the assistant narrates

- Status: **Proposed**, amended 2026-09-26 (see *Amendment: functions on the dashboard*). Step 1,
  the review pack, is implemented (`docs/REVIEW.md`); the observer exclusion and a launcher driven
  from the dashboard's Functions page are implemented (`docs/FUNCTIONS.md`); the rest is sequenced
  under *Consequences*.
- Date: 2026-09-25
- Amends: [ADR-004](ADR-004-native-distribution.md), decision 6, by adding the one path on which
  telemetry-derived data may leave the machine — and the conditions on it.
- Context: the owner proposed that once CopilotScope has collected enough sessions it should be
  able to hand the whole telemetry base, scores included, to the user's own coding assistant —
  the one they already pay for, on their own subscription — to review it, write a report, and
  propose skills that can be extracted from the work that was done; perhaps as a multi-agent
  workflow. This record is the assessment of that idea and the shape it takes here. It was
  produced from a reading of every subsystem the feature touches, four independent designs
  from different angles, three scored judgements and three adversarial reviews; the claims
  about Claude Code's command line were checked against the installed binary (2.1.282), and
  the ones about Copilot CLI were not, and are marked.

## Assessment

The idea is right in its two load-bearing parts.

1. **The evidence is CopilotScope's alone.** A deterministic composite with a confidence figure
   per session, per-turn friction reasons, tool and error tables, cohort deltas and regressions —
   nothing else on the machine holds that, and a base of a few dozen sessions holds more of it
   than one dashboard screen shows. Copilot CLI's own `/chronicle` command shows the demand for
   "look back over my sessions and tell me what to change"; a version grounded in scores, across
   assistants, from files that never leave the box, is a defensible position under
   [ADR-003](ADR-003-positioning.md).
2. **The user's assistant is the cheapest honest narrator.** It costs no SDK, no API key, no
   account and no new package — the dependency budget stays at Npgsql — and it is the only
   reader that already owns the one thing the collector deliberately does not store: the
   transcripts. A skill can only be drafted from what the work actually looked like, and by
   default the base holds tool *names*, never tool inputs or prompt text.

Three phrasings have to change, and two prerequisites have to exist first.

- **"Automatically invoke" becomes "automatically ready, explicitly run".** A review is the
  first path on which telemetry-derived data leaves the machine, to a model vendor. The
  settled posture for anything outbound applies in full: `AlertOptions` is off by default and
  called "the first thing in the collector that sends data somewhere"; `--capture` warns at the
  moment it is enabled; `copilotscope setup` writes nothing without a yes. Readiness is
  surfaced automatically. The run is a command that shows exactly what it will send and asks.
- **"Review the whole base" becomes a bounded, model-sized pack.** `AllInWindowAsync` caps at a
  thousand sessions, privacy mode refuses per-session detail on a shared deployment, and a
  model reading through Claude Code refuses a single read above 25,000 tokens. The reader gets
  one document sized for a context window, plus the JSON to look cited sessions up in.
- **"Multi-agent workflow" is not the first version.** Fan-out costs subscription quota and
  adds nothing the deterministic pack does not already do better: the counting is the
  collector's, and a "critic" agent that re-applies rules a linter already enforces is theatre.
  If a second agent is ever added, it gets the one job code cannot do — reading each cited
  session and striking findings whose citations do not show the pattern.
- **Prerequisite one: the review session is itself a session.** `SelfObservation` recognises
  CopilotScope's own *tool calls* by name; a headless assistant run reviewing the base would be
  ingested over OTLP if the assistant is connected, picked up by the scanner from its transcript
  file, and would move the numbers it was reading. The exclusion has to grow from tool calls to
  sessions before the run exists.
- **Prerequisite two: there is no content to extract skills from.** `Import/ClaudeCodeTranscript`
  reads a tool's name and never its input; `--capture` is off by default and dropped under
  privacy mode. Skill drafting therefore either rests on structure — tool names, counts, error
  types, turn shapes — or the assistant reads the transcripts it wrote itself, scoped to the
  sessions the pack cites.

## Decision

1. **The collector counts; the reader narrates.** Everything countable about the base is
   computed in the Collector as a pure function — `Review/ReviewPack.Build`, a sibling of
   `Digest.Build` and `Cohorts.Build`, no clock, no I/O, byte-identical output over any
   ordering, held to that by a test — and served at `GET /api/review/pack`. Every figure states
   its threshold in the pack. The reader names mechanisms, decides what is worth acting on and
   drafts; every claim it makes cites a pattern id and session ids from the pack. This is
   ADR-003's differentiator applied to the review: the model never counts.

2. **Two tiers, and who may have them.** The aggregate tier — rollups, comparison, regressions,
   distribution, coverage, patterns with counts — is a read like any other, served to any Read
   credential under the k-anonymity floor. The sessions tier adds session rows, the best and
   worst sessions per stratum, and the ids each pattern rests on; it needs Admin scope on the
   `POST /api/digest/send` precedent (an artefact built to be handed on is not what a read
   credential produces) and is refused under privacy mode before anything is read, as per-session
   detail is. Neither tier carries prompt, response or tool-argument text, a subject, a branch, an
   agent name, a rater or a subject count, and tests walk the serialised keys to assert it.
   `CopilotScope:Review:Enabled=false` switches both endpoints off; `GET /api/privacy` reports it.

3. **Every contrast stays inside one stratum.** A stratum is origin/assistant/profile. Imported
   and live sessions are scored on different component sets, and so are assistants, so a
   cross-stratum "low band" would mostly be the stratum without latency and acceptance. Contrasts
   need ten sessions on each side (the regression detector's own floor), recurrences three, and
   every pattern says it is a descriptive count and not a causal estimate.

4. **Readiness, never a launch.** The collector answers `GET /api/review/readiness`: at least
   `CopilotScope:Review:MinSessions` (25) eligible sessions in `CopilotScope:Review:WindowDays`
   (30) since a `coveredUntil` watermark, counting only real conversations with at least one chat
   call. The native binary surfaces it in the start banner, `status` and `doctor` (as
   information, never a problem). No scheduler, no scanner-triggered run, no cron recipe in the
   documentation. The run is `copilotscope review`: it writes the pack and its evidence files
   under `~/.copilotscope/reviews/<id>/`, shows the file list, sizes, the exact command and one
   line on the vendor's data-use terms, and asks `[y/N]`. `--print` shows and launches nothing;
   with no terminal and no `--yes`, nothing runs. `--pack` writes the files and launches nothing,
   for an assistant that cannot be driven headlessly.

5. **Invocation on the user's own subscription, Claude Code first.** The launcher lives in
   `src/CopilotScope.Local`, the one process that owns local state and asks before it acts, and
   uses `System.Diagnostics.Process` only. The command, verified against Claude Code 2.1.282:
   `claude -p` with `--session-id` chosen by CopilotScope and registered as an observer *before*
   launch, `--output-format json --json-schema` for a structured result, `--restricted` with
   `--tools Read,Grep,Glob` (no command execution, no WebFetch, and user, project and local
   settings files ignored — including the telemetry `copilotscope connect` wrote),
   `--strict-mcp-config` with no servers, `--no-session-persistence` so no transcript is written,
   `--settings` carrying `CLAUDE_CODE_ENABLE_TELEMETRY=0` and the `copilotscope.observer` marker,
   and `--max-turns` and `--max-budget-usd` derived from the pack size. Never `--bare` (it skips
   OAuth and would move the run off the subscription) and never `--add-dir` (it would grant every
   transcript of every project). The child environment is scrubbed of every `CLAUDE_CODE_*` and
   `OTEL_*` variable; a managed settings file that sets any of them refuses the launch unless the
   user says otherwise. Transcript access, on by default on the native path, copies only the
   cited sessions' own files into the review directory. Copilot CLI's programmatic mode
   (`copilot -p`, tool allow/deny flags, `COPILOT_OTEL_ENABLED=false`) is **unverified** and lands
   only with a fixture captured from a real installation, the rule ADR-002 and ADR-004 already
   apply to parsers. VS Code Copilot Chat cannot be driven headlessly; it gets `--pack` and a
   path to paste.

6. **CopilotScope's own sessions are never scored.** The self-observation invariant grows from
   tool calls to sessions: an `ObserverRegistry` in the Collector holds registered session ids
   and the marker attribute, `SessionStore.Ingest` drops their spans, metrics and logs before the
   pre-pass, `POST /api/import` rejects them, and the scanner skips them. Every ingest path gets
   a test, and a smoke test that can fail: a stub assistant that posts a canned OTLP batch under
   its session id and drops a transcript file, after which the base must hold zero new sessions.

7. **Report and proposals are files the user owns.** The result is validated against the
   schema, then linted deterministically: every finding and proposal must cite pattern and
   session ids that exist in the pack, name at least three evidence sessions and one
   contradicting one, and contain no session id, no person-ranking phrase, no acceptance-rate
   target, no emotional vocabulary and no causal claim about an adoption; anything else is struck
   and the count shown. `report.md` marks each section as computed by the pack or written by the
   assistant, and the assistant's self-rating is called *certainty* so it is never mistaken for a
   score's confidence. Proposed skills are Agent Skills `SKILL.md` files — the format Claude Code,
   Copilot and others read — with the evidence in frontmatter metadata and no session id, so
   they can be committed and shared. Nothing is installed by the run; `copilotscope review
   install <name>` shows the file, asks, and writes only into a directory it marked as its own.
   Nothing model-written enters the collector: no report store, no `PersistedSession` field.

8. **Teams get the aggregate tier and no launcher.** On a Compose deployment the review is a
   human's decision: a lead fetches the aggregate pack and runs whatever they run. A team tier
   with an `AllowSessionTier` opt-in under the works agreement, and MCP tools for readiness and
   the pack, are the second version, not the first.

## Consequences

- The work lands as a sequence of pull requests, each one change with its tests: **(1) the
  review pack** — builder, patterns, readiness, endpoints, options, documentation (done);
  (2) the observer registry and the marker drop at every ingest path; (3) the `copilotscope-review`
  skill, and `skill install <name>` in the control scripts and the tools image; (4) `ReviewState`,
  the host's token-bound review endpoints, the banner, `status` and `doctor` lines and the
  scanner deny-list; (5) the proposal and output linters, the proposal format, `review --pack`,
  `install`, `uninstall`, `list`; (6) the Claude Code launcher with its consent screen, managed
  settings check, scrubbed environment and transcript copy, plus the smoke test; (7) the
  tutorial pair and the diagram. Roughly three to four weeks solo.
- A new quiet invariant for `CLAUDE.md`, once (2) lands: *CopilotScope's own sessions are never
  scored.* It extends the tool-call rule, and it fails the same way — silently, at the next
  ingest path someone adds.
- `CopilotScope:Review:*` are configuration keys under `CopilotScope:` and therefore a stable
  surface from the release they ship in (GOVERNANCE.md §3).
- Three limits are accepted and documented rather than hidden. Proposals are hypotheses:
  before/after comparisons around a skill's install are uncontrolled and blind to whether the
  skill was used, until the collector persists a skill name per session. Structure-only reviews —
  no transcripts — can propose instructions but not skills, and say so. The interactive route,
  a person running the skill inside an ordinary session, is *not* excluded from scoring; the
  skill's first step says so and points at the command.
- The privacy document gains a paragraph, and the works-agreement checklist a row: whether the
  review is served, and who may have the sessions tier.

## What would change this

- Anthropic's or GitHub's terms on programmatic use under a subscription, or on data use for
  such runs. Both are assumptions here, stated on the consent screen, and a change to either
  changes decision 5.
- A captured Copilot CLI fixture, which would add a second launcher under the same rules.
- Evidence that a proposed skill changed anything. Until a skill name is persisted per session
  and a before/after with usage observed exists, a proposal is a hypothesis the user tests.

## Amendment: functions on the dashboard (2026-09-26)

The owner asked for the review to be one of several **functions** on a dedicated dashboard page —
"analyse all my sessions" as a button — each run on the user's own Copilot or Claude subscription,
and for the functions to use Copilot's multi-agent capabilities. Implemented in
`docs/FUNCTIONS.md`. What that changes above:

- **Multi-agent is in the first version**, by the owner's decision, in the shape the assessment
  allowed: three of four functions fan out (four specialists over slices of the pack; one author per
  pattern; one investigator per regression), and every one ends with the evidence verifier — the job
  code cannot do. No agent re-counts. On Copilot CLI the fan-out is fleet mode (`--fleet`) over custom
  agents; on Claude Code, subagents passed with `--agents`.
- **The surface is the dashboard, the consent is the Run button.** Decision 4's `[y/N]` becomes a
  two-step page: *Run with…* writes the files and shows each one with its size, the exact command and
  the vendor that receives what the assistant reads; only *Run* starts it. Nothing is automatic.
  `copilotscope review` on the command line is not built yet.
- **The launcher lives in `src/CopilotScope.Local`** as decision 5 says, behind an `IFunctionRunner`
  contract the dashboard declares and only the native host registers. A Compose deployment registers
  none and serves each function as a kit (decision 8). Runs are kept in `~/.copilotscope/runs/<id>/`
  rather than `reviews/`, since a review is one function of several.
- **Copilot CLI is launched too.** Decision 5 held it back until a fixture existed. That rule is about
  parsers; the launcher parses no Copilot output format — it reads `-s` plain text — and the flags were
  checked against Copilot CLI 1.0.88's own help, with the generated arguments accepted by the binary.
  A run on a signed-in Copilot CLI has not yet been observed.
- **Two details differ from decision 5.** The child environment keeps the `CLAUDE_CODE_*` variables that
  carry a login or a provider (`CLAUDE_CODE_OAUTH_TOKEN`, `CLAUDE_CODE_USE_BEDROCK`, …): scrubbing all of
  them would move a `claude setup-token` user off the subscription the run is for. And `--max-turns`
  (hidden from `--help`) and `--max-budget-usd` are not passed; a thirty-minute wall-clock limit and one
  run at a time bound the spend instead. A managed settings file that forces telemetry on is shown as
  a warning on the consent screen rather than refusing the launch, because the registered session id
  and the marker keep the run out of the scores either way.
- **Decision 6 is implemented** for OTLP ingest and `/api/import` (`ObserverRegistry`), with the marker
  attribute `copilotscope.observer`. The scanner is covered through `/api/import`, its only way in.
- **Decision 7 in part.** `report.md` carries the computed-versus-written header and *certainty*;
  drafts are saved only as `skills/<name>/SKILL.md` or `instructions/<name>.md`, never with a pack
  session id, and are installed nowhere. The full output linter (citation checks, vocabulary) remains
  step (5); the verifier agent is its model-side counterpart, not its replacement.

## Open decisions for the owner

1. Confirm that the run is never automatic — readiness only — as an amendment to ADR-004
   decision 6.
2. Confirm transcript access on by default on the native path, scoped to the cited sessions'
   own files.
3. Confirm 25 sessions and 30 days as the stable defaults.
4. Confirm Admin scope for the sessions tier on shared collectors.
5. Whether the team tier and the MCP tools follow now or when a team asks.
6. Who captures the Copilot CLI fixture.
