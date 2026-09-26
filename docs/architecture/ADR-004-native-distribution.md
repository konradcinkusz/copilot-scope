# ADR-004 — Native distribution: one binary for individuals, Compose for teams

- Status: **Accepted**
- Date: 2026-09-24
- Amends: [ADR-001](ADR-001-deployment-target.md), decision 1
- Context: the product exists first for one developer on one laptop who wants to know
  whether the assistant they pay for is helping. Everything that person installs before the
  first score is the real cost of adoption, and today that is:

  - Docker with the compose plugin and a running daemon;
  - three containers, one of them Postgres;
  - `python3` or `node`, because `copilotscope connect` merges JSON settings files with them;
  - a 1,100-line bash control script, or its PowerShell twin.

  None of that is needed for what the individual actually does, which is score their own
  sessions on their own machine. The code already concedes most of it:

  - the collector runs without Postgres (`Program.cs`, an empty `copilotdb` connection
    string means in-memory) and writes nothing to disk;
  - the dashboard finds the collector at `http://localhost:4318` with no configuration;
  - `tools/CopilotScope.LogImporter` already reconstructs sessions from Claude Code's own
    transcripts, and `/api/import` already makes that idempotent.

  What is missing is durable storage without Postgres, a host that runs both apps as one
  process, scanning that keeps running, readers for GitHub Copilot's local files, and a
  release pipeline that produces binaries.

## Decision

1. **Two distributions for two audiences.**
   - *Individuals* get a self-contained native binary per platform (`win-x64`, `win-arm64`,
     `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`), shipped as an archive holding the
     `copilotscope` executable and the dashboard's `wwwroot/`. No .NET, Docker, Postgres,
     python or node on the machine. One command, `copilotscope`, starts the collector and the
     dashboard on loopback, keeps sessions under `~/.copilotscope/data`, scans the local
     history of the assistants it finds, and opens the browser. It asks before it edits any
     assistant's settings file.
   - *Teams and shared servers* keep Docker Compose and Postgres exactly as they are:
     `docker-compose.ghcr.yml`, the Grafana stack, the GHCR images. The installers keep that
     path behind `--docker`.

2. **One process, two Kestrel apps, HTTP between them.** The dashboard keeps reading the
   collector over loopback HTTP. The privacy guard, the k-anonymity floor and the access
   audit live on the API path, and an in-process reader would route around all three. One
   shared pipeline would also collide on `/`, `/health` and `/alive`.

3. **Storage without Postgres is JSON files, not SQLite.** A session is already persisted
   as one JSON document (`PersistedSession`, a single jsonb column); a file per session is
   the same document on disk. SQLite would be the Collector's second package, against a
   dependency budget of Npgsql only. Both stores sit behind `ISessionRepository` and must
   answer every query identically. The features that need Postgres today stay Postgres
   features: durable labels, outcome linkage, the vendor-metrics archive and the durable
   access audit are team features, and a single developer does not need them.

4. **Scanning feeds the same import path as the importer.** Files an assistant already
   keeps on disk become sessions through `POST /api/import` over HTTP, with its guards:
   `Origin` stays `log-import` and `EmitterKind` records which tool. Telemetry stays the
   primary source. A scanned session carries no latency samples and, from Claude Code or
   Copilot CLI, no edit decisions, so it scores on fewer components and says so.

5. **No parser without a captured real file.** ADR-002 applies to files exactly as it
   applied to Cursor's OTLP. Claude Code comes first: the parser and a fixture exist. VS Code
   Copilot Chat and Copilot CLI readers land only with fixtures captured from real
   installations. A capture command redacts every string outside a structural allowlist and
   refuses to write anything that still looks like a path, a name, an e-mail address or a
   token. Until then an unknown layout is counted and reported, never guessed at.

6. **Loopback only, and nothing leaves the machine.** The native binary binds 127.0.0.1 and
   has no update check, no telemetry and no account. Binding to a network interface is what
   the Docker path, with its API keys, is for.

7. **Unsigned first, verifiable always.** Every release publishes `SHA256SUMS` and build
   provenance. The installers verify the checksum and refuse on mismatch. Scripted downloads
   (`curl`, `Invoke-WebRequest`) carry no quarantine attribute or Mark-of-the-Web; a browser
   download meets Gatekeeper or SmartScreen, and the README says how to proceed. Signing and
   notarization steps sit in the release workflow and switch on when certificates exist.

   *Amended 2026-09-26.* Until then, "build provenance" was a sentence and not a step: the
   native release workflow produced `SHA256SUMS` only, and the container and research-PDF
   workflows nothing verifiable at all. All three now run `actions/attest` — on every archive,
   installer script, the checksum file, the PDFs and the container images — and the README says
   how to check the result with `gh attestation verify`. The installers ship as release assets,
   so a release can be installed with the script it was tested with (`--version <tag>`) rather
   than the one on `master` that day.

## Consequences

- ADR-001's first decision now reads, for teams and shared deployments: Docker Compose and
  the GHCR images are the distribution. For individuals it is the native binary. The rest of
  ADR-001, including the single-writer constraint, stands.
- The single-writer constraint now also applies to a data directory. A second process on the
  same directory is refused by a lock file rather than allowed to interleave writes.
- CI must package the binary and smoke-test it **from the extracted archive**, and assert
  that `/_framework/blazor.web.js` is served. A dashboard publish without `_framework/`
  renders once and then never responds while `/` still answers 200. That has shipped once
  already (see `Dockerfile.dashboard`).
- The dependency budget gains one row: the native host may take project references to the
  Collector and the Dashboard, and nothing else. Its command line is parsed by hand.
- The work lands as a sequence of pull requests, each one change: file-backed storage;
  embeddable app builders; importer accuracy fixes; the host; `connect` and `doctor` in C#;
  the scanner; fixture capture; the release workflow; the installers; the documentation;
  then one Copilot reader per captured format.
- Two limits are accepted and documented rather than hidden. A Copilot session that is both
  scanned and sent as telemetry can only be matched by id once a paired capture shows the ids
  agree; until then, scanning skips periods when that assistant was connected and the
  collector was running. And a loopback-only collector cannot receive telemetry from a VS
  Code Remote or WSL window without mirrored networking.
