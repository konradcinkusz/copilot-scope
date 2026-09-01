# Per-assistant signal coverage

**Scores are comparable within an assistant, and directional across assistants.**

That sentence is the whole document, but it needs the reason attached, because the obvious way
to read two scores side by side is exactly the wrong one.

## Why an 80 is not always an 80

The composite only aggregates components that actually have data, and renormalizes the weights
across them (`QualityEngine`, and the README's "Session quality" section). That is the right
behaviour — the alternative, letting empty components contribute a neutral prior at full weight,
pinned every ordinary session near 80 and destroyed all discrimination.

But it has a consequence the UI used to leave unsaid: **a session scored on four components and
a session scored on six are not the same measurement.** A Claude Code session has no thumbs and
no edit-survival signal, so its 80 rests on a smaller evidence base than a VS Code session's 80.
Neither number is wrong. Ranking them against each other is.

This repository's own product review flagged it (`docs/architecture/PRODUCT-REVIEW-2026-08.md`,
finding **B2**) and noted that nothing in the UI warned — while "compare assistants before you
buy" is the headline use case. This page, the in-app **Docs** page and `GET /api/coverage` are
that warning, all three served from one table in `Domain/EmitterCoverage.cs` so they cannot drift
apart. `EmitterCoverageTests` asserts each row against what the ingest pipeline really produces.

## The matrix

`Full` = a default install sends it · `Conditional` = only under a plan, flag or beta channel ·
`None` = never sent, so the component it feeds is always a prior.

| Assistant | Traces | Metrics | Events | Edit decisions | Edit survival | Feedback | TTFT |
|---|---|---|---|---|---|---|---|
| VS Code Copilot | Full | Full | Full | Full | Full | Full | Full |
| Copilot CLI | Full | Full | Full | None | None | None | Full |
| Claude Code | Conditional | Full | Full | Full | None | None | Conditional |
| Claude Cowork | Conditional | Full | Full | Full | None | None | Conditional |
| Cursor *(unverified — not supported)* | None | Conditional | Conditional | None | None | None | None |

### What that costs each assistant

| Assistant | Always scored as a prior | Why |
|---|---|---|
| VS Code Copilot | — | The only surface reporting every component. |
| Copilot CLI | acceptance, feedback | No editor UI, so no accept/reject and no thumbs. |
| Claude Code | feedback | Acceptance *does* work — from `tool_decision` events, excluding permission-mode auto-accepts. No survival signal, no thumbs. TTFT needs the beta trace channel. |
| Claude Cowork | feedback | Same dialect as Claude Code. |
| Cursor *(unverified)* | acceptance, feedback, latency, **friction** | **Not a supported assistant** — see [ADR-002](architecture/ADR-002-cursor-support.md). The Enterprise-only export sends metrics and logs but **no traces**, and a turn is one `invoke_agent` trace, so turn-level friction analysis cannot run at all. What exists is a `service.name` match plus a namespace rename, with no captured fixtures and no payload from a real Cursor session ever tested against. |

## How to compare fairly

- **Within one assistant** — compare freely. Same components, same weights, same evidence.
- **Across assistants** — read the *components*, not the composite. The dashboard shows which
  ones carried weight for a given session, and warns when a visible list spans assistants whose
  scores rest on different evidence.
- **For a bake-off** — compare the components both assistants actually report. Reliability,
  friction and efficiency are available almost everywhere; acceptance and feedback are not.

## Related

- `docs/architecture/PRODUCT-REVIEW-2026-08.md` §B2 — where this was first written down
- [ADR-002](architecture/ADR-002-cursor-support.md) — the decision to demote Cursor rather than implement it (#93)
- `GET /api/coverage` — the same table, as JSON
