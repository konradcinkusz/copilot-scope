# Governance and maintenance

The first question anyone asks before putting a tool in their telemetry path is **"who
maintains it?"** — and for a project like this the honest answer has to come before the pitch,
not after someone discovers it.

This document is that answer. It is deliberately conservative: every commitment here is one a
single maintainer can actually keep, because a governance document that overpromises is worse
than none — it fails at exactly the moment someone relies on it.

---

## 1. Who maintains this

**One person**, [@konradcinkusz](https://github.com/konradcinkusz), working on it alongside
other things. There are no other maintainers today and no organization behind it.

That is not a disclaimer to skim past. It is the single most important fact for deciding
whether to adopt this, and §5 addresses what it means for you head-on.

## 2. What you can expect

| | Commitment |
|---|---|
| **Security reports** | Acknowledged within a week. If a fix will take longer, you are told so rather than left waiting. See [SECURITY.md](SECURITY.md). |
| **Bug reports** | Best-effort triage within two weeks. No SLA. A bug with a reproduction gets looked at first, because a bug without one usually cannot be. |
| **Feature requests** | Read, and answered honestly — including "no", with a reason. An unanswered issue is worse than a declined one. |
| **Pull requests** | Reviewed within two weeks, or you are told why not. See [CONTRIBUTING.md](CONTRIBUTING.md). |
| **Releases** | When there is something worth releasing. Deliberately **not** on a calendar: a solo project that promises monthly releases either ships filler or misses the promise. |

**These are intentions, not contractual terms.** They exist so you can cite something concrete
when someone asks, and so that if they are missed you can point at the gap rather than guess.

## 3. Stability contract

An adopter cannot evaluate upgrade risk without knowing which surfaces are allowed to move.
This enumerates them by name.

### Stable — a breaking change requires a major version bump and a migration note in [CHANGELOG.md](CHANGELOG.md)

| Surface | Where |
|---|---|
| **OTLP ingest** — the paths, the accepted encodings, and the attribute vocabulary the collector reads | `POST /v1/{traces,metrics,logs}`, `Domain/Sem.cs` |
| **REST DTO field names and types** on `/api/sessions`, `/api/sessions/{id}`, `/api/overview`, `/api/health` | `Collector/Api/Dtos.cs`, mirrored in `Dashboard/Services/CollectorClient.cs` |
| **Prometheus metric family names and label names** | `Collector/Api/PrometheusExporter.cs` |
| **The Postgres session snapshot shape** (the `sessions.snapshot` jsonb) | `Collector/Persistence/PersistedSession.cs` |
| **The calibration label schema** — the flat `{sessionId, rater, algorithm, level, note}` records and the dataset document around them | `Collector/Calibration/Labelling.cs`, `calibration/labels.example.json` |
| **Configuration key names** under `CopilotScope:` | `appsettings.json` |

Adding a field to any of these is **not** a breaking change. Every DTO field added since 1.0 has
a default, and every snapshot field is optional, so an older snapshot deserializes into a newer
build unchanged — that is a property the code maintains deliberately, not an accident.

### Unstable — may change in any release

| Surface | Why |
|---|---|
| **Quality score weights and component definitions** | The score is not calibrated yet ([docs/CALIBRATION.md](docs/CALIBRATION.md)). Weights *will* move when it is. **Scores are comparable within a version, not across one** — a recalibration is announced in the changelog, and the reason it is announced is precisely that your history's meaning changes. |
| **Insight analyzer output** — report names, metric labels, findings text | Report-only, human-read, deliberately free to improve. |
| **Dashboard routes and markup** | It is a UI. |
| **Everything under `tools/`** | Development and demo utilities, not a product surface. |
| **Newer endpoints still settling** — `/api/cohorts`, `/api/compare`, `/api/facets`, `/api/digest`, `/api/friction`, `/api/vendor/metrics`, `/api/labels/*`, `/api/import` | These shipped recently and have had no external use. They will stabilize; today they are not promised. |

### Database migrations

Every table is created with `CREATE TABLE IF NOT EXISTS` and every added column with
`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`, applied at startup. Upgrades are in-place and do not
need a migration tool. **There is no automatic downgrade**: rolling back to an older build
against a newer schema is untested, so take a dump first.

Tables: `sessions`, `session_labels`, `access_audit`, `pull_request_outcomes`, `vendor_metrics`.

## 4. Versioning

[Semantic versioning](https://semver.org/), applied to the surfaces in §3:

- **Major** — a breaking change to a stable surface, with a migration note.
- **Minor** — new features, new fields, new endpoints.
- **Patch** — fixes.

A change to the quality score's weights is called out in the changelog under its own heading
whatever the version number is, because it changes what your existing history *means* — which
matters more to a user than which number moved.

## 5. If the maintainer stops

The realistic failure mode for a project like this is not a bad release. It is that one person
gets busy and it quietly stops moving. Planning for that is part of being adoptable.

**Your data is yours and it is not in here.** CopilotScope is self-hosted. Sessions live in
*your* Postgres, in a documented shape, and the project holds nothing:

```bash
# everything, in the shape the collector writes
pg_dump -t sessions -t session_labels -t access_audit \
        -t pull_request_outcomes -t vendor_metrics > copilotscope.sql
```

Structured exports, no database access required:

| What | How |
|---|---|
| Cohort rollups | `GET /api/cohorts?format=csv` |
| Window comparison | `GET /api/compare?format=csv` |
| Human labels | `GET /api/labels/export` — the calibration schema |
| Archived vendor usage | `GET /api/vendor/metrics?days=3650` |
| Access log | `GET /api/audit?format=csv` |
| Raw session snapshots | `GET /api/sessions` |

**The license permits forking, permanently.** MIT. If this stops moving, fork it — no
permission needed, no contributor agreement to unwind, no relicensing risk. Every dependency is
permissively licensed and the collector's only NuGet dependency is Npgsql.

**Nothing phones home.** No telemetry about your telemetry, no license check, no callback. An
abandoned CopilotScope keeps running exactly as it did the day it was abandoned, until you turn
it off.

**The images stay published.** Releases go to GHCR and are not deleted.

That is the bus-factor answer, and it is the reason a self-hosted MIT tool can be a *lower* risk
than a SaaS vendor with a real team: a vendor that goes away takes your data and your access
with it. This one cannot.

## 6. Becoming a co-maintainer

**A co-maintainer would be welcome**, and this is the honest version of what that means rather
than a badge offer.

What is actually needed, roughly in order:

1. **Triage.** Reading incoming issues, asking for reproductions, closing duplicates. The single
   highest-value thing, and the one that decays fastest when one person is busy.
2. **Emitter coverage.** The assistants move constantly. Capturing real payloads with
   `tools/CopilotScope.FixtureCapture` and keeping `Domain/EmitterCoverage.cs` honest is
   ongoing work that needs someone who uses the assistant in question. **Cursor support is
   currently claimed without a single captured payload** ([#93](https://github.com/konradcinkusz/copilot-scope/issues/93)) — that is the clearest open example.
3. **Calibration.** The scoring machinery is built and unvalidated. Someone willing to label
   sessions ([docs/CALIBRATION.md §8](docs/CALIBRATION.md)) moves the project's central claim
   further than any feature would.
4. **Release engineering.** Cutting releases, checking the images actually work.

**The path**: land two or three non-trivial PRs, then say you are interested — in an issue or
directly. There is no committee. Commit access follows demonstrated judgment about what *not*
to merge, which is most of the job.

If none of that appeals but you rely on the project, the most useful thing you can do is say so
in an issue. A project with three users who have said so is meaningfully different from one with
zero known users, and right now this is the latter.

---

*This document describes intent for a solo-maintained MIT project. It is not a warranty; see
[LICENSE](LICENSE).*
