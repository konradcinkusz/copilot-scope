# Judge calibration — Cohen's κ against human labels

`src/CopilotScope.JudgeAgent/Calibration/` measures how well the judge agrees with people,
and refuses to certify it when the answer is "not well enough, or we cannot tell yet".

The rule it implements is one sentence from the estate standard
([`AI-EVALS.md`](https://github.com/konradcinkusz/architecture-standards/blob/main/docs/guides/AI-EVALS.md) §5):

> **Calibrated against humans, or discarded.** A sample of judged runs gets human labels;
> agreement is measured and recorded before the judge's scores gate anything.

Until now CopilotScope stated the gap rather than closing it — `JudgeSystemPromptTemplate.txt`
says SPUR is "directional, not final… until calibration data exists", and `README.md`'s
algorithm table says the same. This is the machinery that produces that data.

**Current state, stated plainly:** the arithmetic, the endpoints and the dataset format exist
and are tested. **No calibration has been run.** There are no human labels in this repository,
and `calibration/labels.example.json` is a format template carrying invented values — it is
sized so the engine correctly answers `insufficient-data`, which is the example working. No κ
in this repo describes the real judge yet.

---

## 1. Why raw agreement is not the number

Two people who both call 90% of sessions "good" agree with each other 82% of the time without
reading anything. Cohen's κ subtracts that:

```
κ = (p_o − p_e) / (1 − p_e)
```

`p_o` is how often the two raters picked the same band; `p_e` is how often they would have by
luck, given each rater's own habits. κ = 1 is perfect, 0 is indistinguishable from chance,
negative is worse than chance.

Because the rubric bands are **ordered**, the headline number is **quadratic-weighted κ**: a
judge that says "full" where the human said "none" should not score the same as one that said
"mostly". All three weightings (unweighted, linear, quadratic) are reported so the difference
between them is visible — a large gap means the misses are near-misses.

Every κ carries a **95% bootstrap confidence interval** (2000 seeded resamples). A point
estimate with no interval is how "κ 0.72" gets quoted from twelve labelled sessions.

## 2. Two measurements, in this order

| # | Measurement | Question | Failing it means |
|---|---|---|---|
| 1 | **Human ceiling** | Do the labellers agree with *each other*? | The labels are not a ground truth. Nothing can be validated against them. |
| 2 | **Judge vs. consensus** | Does the judge track the panel's median band? | The judge's scores must not gate anything. |

The order is not cosmetic. This repo's own research notes make the point
(`research/articles/thesis_topics.tex` §535, in Polish): low agreement *between people* is a
result in its own right — it fixes a ceiling no algorithm can climb past. A judge measured
against labels the labellers cannot reproduce is measured against noise, so a low ceiling
produces its own verdict rather than a pass or a fail.

With three or more labellers the ceiling is the **mean pairwise Cohen's κ**, not Fleiss' κ —
pairwise keeps every pair visible, so one labeller drifting from the other two shows up as a
low pair instead of a slightly depressed average.

## 3. The scale

κ is a statistic over categories; the judge emits a continuous 0–1 score. They meet on four
ordinal bands — `AI-EVALS.md` §5's "small ordinal scale with an anchor description per level",
never a bare 1–10. Four is a deliberate ceiling: at the 50–100 labelled sessions
`research/RESEARCH_PROPOSALS.md` §40 calls for, a five- or six-band scale leaves most confusion
cells empty and κ starts swinging on single items.

| Level | Name | Judge score | Anchor |
|---|---|---|---|
| 0 | none/poor | 0.00 – 0.39 | Clearly absent, or the criterion is plainly not met. |
| 1 | partial | 0.40 – 0.64 | Present in part, with gaps that materially matter. |
| 2 | mostly | 0.65 – 0.84 | Present, with minor gaps that do not change the outcome. |
| 3 | full | 0.85 – 1.00 | Fully present; nothing material missing. |

**Label on the rubric's own scale, not on "higher is better".** Four of the five rubrics score
how *good* the session was; `deep-frustration` scores how *frustrated* the user was and runs
the other way. The judge is behaving correctly when it returns 0.1 for a calm session. A
labeller who reads band 3 there as "great session" would be recorded as maximally disagreeing
with a judge that got it right — and the report would blame the judge for a broken form. Each
rubric therefore carries its own question:

| Rubric | What the labeller is asked | Direction |
|---|---|---|
| `G-Eval` | How correct, complete and clear was the assistant's work? | higher = better |
| `SPUR` | Would the user who ran this session have rated it satisfactory? | higher = better |
| `RAGAS` | Were the answers faithful to, and supported by, the retrieved context? | higher = better |
| `deep-frustration` | How frustrated does the user read as being? | **higher = worse** |
| `task-completion` | Was the original ask actually resolved by session end? | higher = better |

## 4. Verdicts

| Verdict | Meaning |
|---|---|
| `calibrated` | Judge κ ≥ threshold, against a panel that clears the same threshold. |
| `not-calibrated` | Judge κ below threshold. Its scores must not gate anything. |
| `ceiling-too-low` | The labellers do not agree with each other. Fix the anchors or the brief first. |
| `insufficient-data` | Too few paired sessions, or only one labeller. Numbers shown, no conclusion licensed. |

The overall verdict is the **worst** rubric verdict — one rubric that cannot gate is enough to
stop the suite claiming the judge is calibrated.

Thresholds are config, not arithmetic, because "how much agreement is enough" depends on what
the score is allowed to decide:

```json
{
  "CopilotScope": {
    "JudgeAgent": {
      "Calibration": {
        "MinKappa": 0.70,
        "MinPairedSessions": 20,
        "BootstrapIterations": 2000,
        "BootstrapSeed": 20260822
      }
    }
  }
}
```

`MinKappa` defaults to **0.70** because that is this repo's own published acceptance criterion
(`research/articles/thesis_topics.tex` §551, `research/RESEARCH_PROPOSALS.md` §207), not a
borrowed convention. `MinPairedSessions` is a floor for reading a number at all, not the
target — the research plan asks for 50–100 labelled sessions.

## 5. Running it

### Offline — free, deterministic, the one CI can run

`POST /api/calibration/report` takes labels *and* judge scores and returns the report. No model
access, no clock, no unseeded randomness: the same dataset always produces the same numbers,
which is what lets a calibration act as a baseline rather than an anecdote.

```bash
curl -X POST http://localhost:5400/api/calibration/report \
  -H 'Content-Type: application/json' \
  --data @calibration/labels.example.json
```

### Live — one metered model call per session

`POST /api/calibration/run` takes labels only, grades each named session with the real judge,
then computes the same report. Sessions are judged sequentially (fanning out across a judge
deployment is the quickest way to trip rate limits) and capped at 200 per run. A session that
fails to judge is reported in `failures` rather than aborting the batch.

```bash
curl -X POST http://localhost:5400/api/calibration/run \
  -H 'Content-Type: application/json' \
  -d '{ "datasetVersion": "2026-08-labels-v1", "labels": [ ... ] }'
```

Both endpoints sit behind the same ingest key as the rest of JudgeAgent's API.

## 6. The dataset is data, in the repo

One label is one flat record, so several people can append at different times without merging
structures:

```json
{ "sessionId": "seed-quick-01-golden", "rater": "alice", "algorithm": "G-Eval", "level": 3,
  "note": "why this band" }
```

Keep it in `calibration/` and version it like code. A calibration living in a database is
invisible to review; one in a JSON file is diffable, and a re-baseline shows up in a pull
request. See `calibration/labels.example.json` for the full shape.

Rules the engine enforces rather than papering over:

- A level outside 0–3 is a **rejected dataset**, not a clamped one — it means the labelling
  form is broken, and burying that inside a κ hides the bug.
- Two judge scores for the same (session, rubric) is a **rejected dataset** — usually two runs
  concatenated, and picking one silently would make the report depend on list order.
- A labelled session the judge produced no score for (`RAGAS` on a session with no retrieval)
  is **dropped and counted**, so paired-N is never mistaken for labelled-N.
- A rater revising their own grade is fine: the last label for a (rater, session) wins.

## 7. Reading a bad result

`not-calibrated` is a finding, not a failure to route around. The confusion matrix says which:

- **Judge sits consistently one band above the humans** → a rubric-anchor problem. The anchors
  are too vague to grade against, and `AI-EVALS.md` §5's rule applies: *fix the rubric, not the
  human*.
- **Matrix scattered with no pattern** → a judge problem. The model is not tracking the
  criterion at all.
- **Ceiling low too** → start there. Nothing about the judge is knowable until the labellers
  agree.

A κ belongs to the exact judge model and rubric revision that earned it, so every report
records both — `judgeModel` from the deployment name, and `judgePromptVersion` as a hash of
`JudgeSystemPromptTemplate.txt` derived automatically rather than declared, so it cannot be
forgotten on an edit. A judge that silently upgrades is a measuring stick that changes length.

## 8. What this does not do

- **It does not gate CI.** Nothing in `.github/workflows/` fails on a κ today. Wiring that up
  is a deliberate next step, and it needs real labels first.
- **It does not collect labels.** There is no labelling UI; the dataset is hand-authored JSON.
- **It does not promote judge scores into the composite.** The Collector stays telemetry-only
  by design (`research/articles/quality_measurement_framework.tex` §77–85: *no LLM judge, no
  prompt inspection, no network round-trip at scoring time*). Calibration lives in JudgeAgent
  precisely because the Collector must not depend on a judge.

## 9. Where the code is

| File | Role |
|---|---|
| `Calibration/Agreement.cs` | Cohen's κ, weighted variants, bootstrap CI, Landis & Koch bands |
| `Calibration/RubricScale.cs` | The four bands, the five rubrics, score → band binning |
| `Calibration/CalibrationEngine.cs` | Ceiling, consensus, judge agreement, verdicts |
| `Calibration/CalibrationModels.cs` | Dataset and report contracts |
| `Config/CalibrationOptions.cs` | Thresholds |
| `tests/CopilotScope.Tests/AgreementTests.cs` | The arithmetic, against published worked examples |
| `tests/CopilotScope.Tests/CalibrationEngineTests.cs` | The verdict logic |
| `tests/CopilotScope.Tests/CalibrationFlowTests.cs` | The judge → calibration join |
