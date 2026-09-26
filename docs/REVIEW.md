# The review pack

A session base that has grown past a few dozen sessions holds more than a dashboard shows at
once: what recurred, which tool kept failing, where the repair loops were, which sessions went
best and worst and why. The review pack is that material, computed by the collector and served
in one document, so that something else — the user's own coding assistant, most likely — can
read it and say what a person should do about it.

This document describes what the pack is, what it deliberately is not, and who may have which
part of it. It is the first step of a larger design, recorded in
[ADR-005](architecture/ADR-005-session-review.md); the steps that follow it (a `copilotscope
review` command that runs the user's assistant over the pack, a skill that teaches the assistant
what to write, and the exclusion of that run from scoring) are not shipped yet and are
described there.

## The split that makes it honest

An assistant asked to "review my sessions" will happily count things — and will count them
differently each time, from whatever subset fits its context window. This project's whole
differentiator is that its numbers are re-derivable, so the review is split in two:

- **The pack counts.** Everything countable is counted here, deterministically, with the
  threshold for each figure stated in the pack itself: the composite and its confidence per
  session, cohort rollups, the before/after comparison, regressions, the score and grade
  distribution, the signal coverage of each assistant, the patterns that recurred and the
  sessions they rest on, and the best and worst sessions per stratum with the turn analysis's
  reasons. `ReviewPack.Build` is a pure function of the sessions it is handed: the same base in
  any order produces byte-identical JSON, and a test holds it to that.
- **The reader narrates.** Whatever reads the pack names the mechanism behind a pattern,
  decides which are worth acting on, and drafts what to change. Every claim it makes should cite
  a pattern id and session ids from the pack, so the claim can be checked against numbers the
  reader did not produce.

## Fetching it

```bash
curl http://localhost:4318/api/review/readiness
curl http://localhost:4318/api/review/pack                      # aggregate tier, JSON
curl http://localhost:4318/api/review/pack?format=markdown      # the same, as one document
curl http://localhost:4318/api/review/pack?tier=sessions        # with session rows and exemplars
```

`readiness` says whether there is enough new material to be worth reviewing: at least
`CopilotScope:Review:MinSessions` (default 25) eligible sessions in the last
`CopilotScope:Review:WindowDays` (default 30), optionally after a `coveredUntil` timestamp —
the point a previous review reached. Eligible means a real conversation with at least one chat
call: internal helper calls, seeded demo sessions and empty sessions are excluded before anything
is counted, and the pack reports how many of each it excluded.

`pack` takes `days` (default `WindowDays`, capped at 365), `tier` and `format`. The baseline
window for the comparison and the regressions is the same length immediately before it.

### Two tiers

| Tier | Contains | Who may have it |
|---|---|---|
| `aggregate` (default) | Rollups, comparison, regressions, distribution, coverage, patterns with counts | Any Read credential — the same as `/api/cohorts` and `/api/digest` |
| `sessions` | The above, plus the session ids each pattern rests on, the best and worst sessions per stratum, and up to `MaxSessionRows` (default 100) session rows | Admin scope, and never under privacy mode |

The sessions tier needs Admin for the reason `POST /api/digest/send` does: it is an artefact whose
purpose is to be handed to something else, and it carries session ids. A read credential — the
dashboard's, Prometheus's, every MCP server's — is not the right thing to produce it with. On
the native binary and any other open-mode deployment there is no key, and both tiers are served.

Under privacy mode the sessions tier is refused outright, before anything is read, exactly as
`/api/sessions/{id}` is: a session row is a group of one. The aggregate tier is subject to the
k-anonymity floor like every other view, and a window covering fewer than *k* distinct subjects
returns a suppressed 403. Every fetch is recorded in the access audit log under
`review.pack` and `review.readiness`.

## What is in it

- **Scope** — the windows, the tier, the count, the collector version, and a fingerprint of the
  population. The fingerprint identifies the sessions, not the moment, so a report can say which
  base it was written about.
- **Provenance** — sessions by origin, assistant, mode and stratum, and the three exclusion counts.
- **Formula** — the profile weights, the grade bands, and every threshold the pack applies,
  restated so the reader never has to guess.
- **Cohorts** — the rollup by assistant, model, repository and kind. Averages are `null` below
  three sessions, never zero.
- **Comparison and regressions** — the current window against the one before it, with the same
  caveats `/api/compare` and the weekly digest carry.
- **Distribution** — grade counts and score and confidence histograms.
- **Coverage** — which components each assistant present in the base can never report, so a
  Claude Code 80 and a VS Code 80 are not read as the same evidence.
- **Patterns** — what recurred. Five kinds, all from metadata the collector already holds:

  | Kind | What it counts | Threshold |
  |---|---|---|
  | `tool-errors` | A tool whose error rate is at least twice the rest of its stratum's | ≥ 10 calls, ≥ 3 sessions in which it failed, rate ≥ 5% |
  | `error-types` | The same emitter-reported error type across sessions | ≥ 3 sessions |
  | `llm-errors` | Chat calls the emitter reported as failed | ≥ 3 sessions |
  | `repair-loops` | Turns the turn analysis flagged as retrying rather than progressing | ≥ 3 sessions |
  | `latency-stalls` | Turns at least 1.5× the session's own median time-to-first-token | ≥ 3 sessions |
  | `model-contrast` | Mean score of sessions that called a model against those in the same stratum that did not | ≥ 10 sessions on each side, ≥ 2 points apart |

  Every pattern is computed inside one **stratum** — origin/assistant/profile — because an
  imported session and a live one are scored on different component sets, and a cross-stratum
  "low band" would mostly be the stratum without latency and acceptance. Every pattern is a
  descriptive count, and says so; none is a causal estimate.
- **Exemplars** (sessions tier) — the best and worst sessions per stratum by composite, chosen
  among sessions whose confidence is near the stratum's median, each with its tool table, error
  types, turn-analysis findings and the last twenty timeline entries.
- **Session rows** (sessions tier) — the most recent sessions with score, confidence, grade,
  counts, models and the patterns each is evidence for.
- **Notes** — every caveat the reader needs, including how many imported sessions there are and
  what was trimmed.

The Markdown form is capped at 48 KB — about twelve thousand tokens, so a model can read it
whole and still have room to look sessions up. Above that it is rendered compact and says so;
the JSON form always carries everything.

## What is deliberately not in it

- **No prompt, response or tool-argument text**, whatever the capture setting. The timeline
  entries in an exemplar are tool names, token counts and durations. A test builds a pack from
  sessions with captured content and asserts none of it appears in either form.
- **No subject, no branch, no agent name, no rater, no subject count.** The aggregate tier has
  no session id either. A recursive walk of the serialized pack asserts the keys are absent — the
  precedent is `CohortExport`, whose input type has no individual in it.
- **No workflow-friction figures.** They are report-only everywhere else, and a pack that
  carried them would be an invitation to infer them. The Markdown form tells its reader not to.
- **Nothing model-written.** The pack is input to a review, never the output of one; the
  collector stores no report and no proposal.
- **No new dependency.** It is cohort arithmetic and the turn analysis, in the Collector, tested
  like scoring.

Switch it off with `CopilotScope:Review:Enabled=false`; both endpoints then answer 409, and
`GET /api/privacy` reports `review.enabled: false`.
