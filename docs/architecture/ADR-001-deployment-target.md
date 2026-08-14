# ADR-001 — Deployment target: local-first, cloud deferred to Fly.io

- Status: **Accepted**
- Date: 2026-08-14
- Context: the review (`PRODUCT-REVIEW-2026-08.md`, P7/P12) flagged that CopilotScope
  "advertises two clouds and has neither working" — `architecture.mmd` and the README
  describe an Azure Container Apps deployment, `infra/main.bicep` deploys only the
  collector (no Postgres, no dashboard, no Key Vault), and there is no Fly.io config at
  all, while the estate constitution (`architecture-standards`, P7) resolves the
  ACA-vs-Fly question in favour of Fly.io. An unacknowledged deviation is drift; this
  ADR turns it into a decision.

## Decision

1. **The shipping model is local-first.** The product's real distribution is the
   Docker Compose stacks (`docker-compose.yml`, `docker-compose.ghcr.yml`,
   `docker-compose.grafana.yml`) and the public GHCR images. "Runs on your machine,
   no account, no data leaving the box" is the value proposition, so a managed cloud
   deployment is **not** on the critical path and is not maintained as a supported
   target today.

2. **The partial Azure Container Apps Bicep is experimental, not a deployment.**
   `infra/main.bicep` provisions a single collector container for demos. It is
   explicitly incomplete (no persistence, no dashboard, no Key Vault, no auth beyond
   the ingest key) and must be treated as a spike, not a runbook. It stays in the repo
   only as a starting point; `architecture.mmd` marks the Azure blocks `(planned)`.

3. **If and when a managed cloud is pursued, it is Fly.io, per the constitution (P7).**
   Not ACA. The path is the estate's `FLY-IO-DEPLOYMENT.md`: one `fly.toml` per
   service, Postgres with no public listener on the 6PN network, `min_machines_running
   = 1` for the collector (it is on the dashboard/agents' synchronous request path),
   and the tag-driven pipeline already in `build-containers.yml` extended with an
   ordered deploy job. The single-writer constraint below must be honoured there too.

## Consequences

- The collector is a **single-writer, single-instance** service: `SessionStore` holds
  all session state in memory and aggregates across OTLP batches, so it cannot be
  horizontally scaled without a partitioned queue keyed on `gen_ai.conversation.id`.
  The ACA Bicep already pins `scale { minReplicas: 1, maxReplicas: 1 }`
  (`infra/main.bicep`); any Fly.io deployment must pin one machine for the same reason.
  (This corrects the prior review's §3.3, which claimed the replicas were unpinned —
  they have been pinned since the file's first commit.)
- Documentation must not describe ACA as a live deployment. The README deployment
  table and `architecture.mmd` present ACA as planned/partial only.
- Revisiting this decision (e.g. a customer requires a hosted SaaS) means writing
  ADR-002 that supersedes this one, and following the Fly.io path in §3 rather than
  finishing the ACA Bicep.
