# ADR-002 — Cursor support: demoted to unverified, not implemented

- Status: **Accepted**
- Date: 2026-09-01
- Context: the 2026-08-31 business evaluation ([#93](https://github.com/konradcinkusz/copilot-scope/issues/93))
  put a positioning claim on trial. The README's headline counted "five assistants",
  `docs/STRATEGY.md` counted four (with a *different* membership), and the fifth —
  Cursor — was supported by a `service.name` substring check plus a `cursor.*` → `copilot_chat.*`
  prefix rename, with zero Cursor-specific tests and no captured payload from a real
  Cursor session. The screenshots that made it look proven came from a seeded demo
  session the repository fabricates itself.

  Radical honesty is this project's main competitive virtue. It is the reason the score
  publishes its own confidence, the reason the calibration docs say "no calibration has
  been run", and the reason the coverage matrix exists. A support claim that the code
  cannot back is the one thing that would cost more than the feature is worth: the first
  reader who checks finds the gap, and then nothing else the project says is trusted
  either.

## Decision

**Option B — demote.** Cursor is *not* claimed as a supported assistant. The decoding
path stays; the claim does not.

1. **The supported count is four**: VS Code Copilot, Copilot CLI, Claude Code, and
   Claude Cowork. README and `docs/STRATEGY.md` now agree on both the number and the
   membership — they previously agreed on neither.

2. **Cursor is listed separately and labelled unverified**, wherever it appears in a
   user-facing surface: the README, the in-app Docs page, `docs/SIGNAL_COVERAGE.md`
   and `Domain/EmitterCoverage.cs`. The wording says what is actually true — telemetry
   whose `service.name` contains `cursor` is detected and its `cursor.*` attributes are
   normalized, and no payload from a real Cursor session has ever been tested against.

3. **The speculative setup guidance is removed.** The Docs page told users to "try
   adding the same VS Code settings … if Cursor exposes an OTLP env-var hook". That is
   an instruction to guess, published as documentation. Replaced by the two things that
   are known: Cursor's OTel export is Enterprise-plan-only, and it sends metrics and
   logs but no traces.

4. **Seeded sessions are visibly labelled as demo data in the dashboard.** The Cursor
   session in the seeded dataset is the specific reason this claim looked proven, and
   the general fix is better than removing one row: every `seed-` session now carries a
   **demo** badge, so no screenshot of fabricated data can be mistaken for evidence
   about any assistant.

5. **No second unverified emitter is added while this stands.** This directly settled
   the Codex CLI half of [#98](https://github.com/konradcinkusz/copilot-scope/issues/98):
   a transcript parser written from documentation alone, with no captured file, is the
   same mistake with a different name.

## What would change this

Option A stays available and the issue records what it needs: a verified ingest path
against Cursor Enterprise's actual export, **with captured fixtures** in
`tests/fixtures/cursor/` — which `tools/CopilotScope.FixtureCapture` exists to produce —
plus honest degradation for a traceless emitter.

The traceless part is not a detail. A turn in this codebase is one `invoke_agent` trace,
so `SegmentAnalyzer`'s turn-level friction analysis cannot run for Cursor at all as
things stand, and the composite's `friction` component would be a prior on every Cursor
session. The coverage matrix already records that; implementing Cursor means deciding
what a score built without it is worth, not just writing a decoder.

## Consequences

- The headline claim gets smaller and true. "Four assistants in one schema" is still
  three more than the 480★ competitor in `docs/STRATEGY.md`'s table.
- Anyone already pointing Cursor at the collector keeps exactly the behaviour they had.
  Nothing is removed from the ingest path; only the claim about it changes.
- `GOVERNANCE.md` §6 names Cursor coverage as the clearest open example of work a
  co-maintainer could take, which stays accurate and now points at a decision rather
  than an oversight.
