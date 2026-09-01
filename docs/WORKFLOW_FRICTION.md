# Workflow friction signals

**Position statement: this is workflow-event detection, not emotion recognition.**

CopilotScope can count how often a developer had to *repair* a request — re-ask, restate,
correct, undo. That signal used to be called "frustration analysis". This document explains
why it was renamed, why the rename is a correction rather than a euphemism, why the feature
now ships switched off, and what an EU deployer should configure.

---

## 1. Why the name mattered

EU AI Act **Art. 5(1)(f)** prohibits placing on the market or using AI systems that infer
emotions of a person in the workplace. Not "high-risk with obligations" — **prohibited**,
applicable since February 2025, with fines to EUR 35M or 7% of global turnover. The
Annex III high-risk regime covering employment and worker management applies from
2026-08-02 on top of that.

A feature labelled "frustration analysis" describes itself as inferring an emotional state
from a worker's text. It does not matter that the implementation is a word list, that the
output is report-only, or that no one intended to use it that way. A DPO reading the feature
list has to stop at that row, and a works council has to ask why the tool is measuring how
people feel. In the EU/self-hosted segment — the one where a tool like this is most
defensible against SaaS competitors — that single row was the cheapest possible objection to
hand someone.

## 2. Why the rename is a correction, not a euphemism

Look at what the code actually computes, per captured user message:

| Signal | What is observed | What it is evidence of |
|---|---|---|
| Lexicon hit | The message contains "doesn't work", "wrong again", "nie działa" | The previous response did not resolve the ask |
| Rephrasing | Jaccard word-set similarity ≥ 0.6 with the previous user message | The same request is being made again |
| Short corrective reply | A sub-20-character message starting "no", "stop", "źle" | A correction, not a new request |
| Sustained CAPS / `?!` bursts | Typography | Emphasis |

Three of the four are unambiguously *events in the workflow*: the ask was repeated, the
answer was rejected, the request was restated. None of them requires a claim about anyone's
internal state, and the analytical value never came from such a claim — a team lead deciding
whether to keep paying for a model wants to know **how often it took three attempts**, not
how anyone felt about it.

The old name overclaimed. "Frustration index (0=calm)" asserted a measurement of mood that
a word list cannot make and that the tool never needed. **Workflow friction** is what the
code does, stated accurately.

The typography signal is the weakest of the four on this reading — CAPS is emphasis, and
emphasis is closer to affect than the other three. It is kept because it co-occurs with
repair in practice and carries only 0.15 weight, and because every flag is reported with its
reasons so a human can discount it. If your DPO wants it gone, the lexicon and thresholds
are in `src/CopilotScope.Collector/Quality/WorkflowFrictionAnalyzer.cs` and it is four lines.

## 3. What ships, and what is off

```jsonc
{
  "CopilotScope": {
    "WorkflowFriction": {
      "Enabled": false,               // default — the analyzer does not run at all
      "IncludeFlaggedMessages": false, // default — no prompt text is quoted back
      "FlagThreshold": 0.3
    }
  }
}
```

- **`Enabled: false` by default.** No report is produced, no dashboard section appears, and
  `GET /api/friction` answers `409` with an explanation. This is the only analyzer that reads
  the developer's own prompt text; it should not run because someone forgot to look.
- **`IncludeFlaggedMessages: false` by default**, separately. With friction on but this off,
  you get the rate and the counts; you do not get *"14:32 [65%] «this still doesn't work, wrong
  again»"* attached to a named session. The rate answers "is our tooling making people repeat
  themselves". The quote answers "what did this person type at 14:32", which is a different
  question with a different audience, and it needs its own decision.
- **Aggregate first.** `GET /api/friction?days=30` returns a team/period rate — sessions in
  the window, sessions carrying the signal, mean index, how many crossed the threshold. It
  never breaks down by person, and it is subject to the same k-anonymity floor as every other
  view under [privacy mode](PRIVACY.md).
- **Report-only, always.** The signal is not part of the composite quality score and never has
  been. Whether it should ever be promoted is tracked separately in issue #61, and would be a
  config decision with its own rationale — not a side effect of this feature being on.
- **Privacy mode makes it moot.** Privacy mode drops prompt and response content at ingest,
  so the analyzer has nothing to read and reports `no-data`. The two features compose
  correctly: you cannot end up with quoted prompts in a deployment that promised not to keep
  them.

## 4. The cloud judge rubric

`CopilotScope.JudgeAgent` (opt-in, requires an LLM endpoint) carries the same signal as the
**`deep-friction`** rubric — renamed from `deep-frustration`, and rewritten. It scores how
much repair work the session required and is instructed explicitly not to describe, infer or
score feelings, mood or affect. Its advantage over the local lexicon is recognising repair
that keyword matching misses: *"this is great, but can you do it again properly"* is repair
regardless of its tone.

Calibration label files written against the old `deep-frustration` id still resolve —
`RubricScale` maps the retired id onto the current one and the calibration engine folds them
together before grouping, so historical human labels keep counting. Human labels are
expensive; a rename that silently dropped them would look like a judge getting worse.

## 5. Not the same as the composite's `friction` component

Two different things share the word, and the difference is worth stating once:

| | Source | Needs prompt content? | In the score? |
|---|---|---|---|
| **`friction` component** (composite) | Turn-level telemetry: errors, retries, turn duration | No | Yes — 0.20 of the composite |
| **Workflow friction signals** (this document) | Lexical markers in captured prompt text | Yes | **No** — report-only |

A deployment with no content capture at all still gets the composite's `friction` component.
That is by design: the scored signal is the one that needs no prompt text.

## 6. What an EU deployer should configure

- Leave `WorkflowFriction:Enabled` **off** unless you have a specific question that needs it
  and a works agreement that covers it.
- If you turn it on, leave `IncludeFlaggedMessages` **off** and use `GET /api/friction`. Name
  the aggregate surface in the works agreement; name the per-message previews separately if
  you ever enable them.
- Turn on [privacy mode](PRIVACY.md). It closes the question entirely by removing the input.
- Attach §2 of this document to your DPIA. The argument a DPO needs is that the system
  records observed workflow events, and the table there is that argument.

*This is not legal advice. Have your DPO assess your deployment.*
