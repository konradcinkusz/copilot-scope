# Changelog

Notable changes per release. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

Each release attaches the native `copilotscope` archives for six platforms (Windows, macOS
and Linux, each on x64 and arm64) with a `SHA256SUMS` file, and the research paper PDF. It
also publishes five images to GHCR: `ghcr.io/konradcinkusz/copilotscope-collector`,
`-dashboard`, `-tools`, `-agentforge` and `-judgeagent`.

## [Unreleased]

## [1.1.0] — 2026-09-25

The first release since 1.0.7, and most of what CopilotScope now is. The headlines:

- **One program per platform.** `copilotscope` installs with one line on Windows, macOS and
  Linux, x64 and arm64 alike. It needs no Docker, .NET or database, keeps sessions as files,
  and reads Claude Code's history by itself. The Docker Compose stack stays, for teams and
  shared servers ([ADR-004](docs/architecture/ADR-004-native-distribution.md)).
- **Team controls that are enforced, not documented:** privacy mode with a k-anonymity floor,
  scoped keys, dashboard sign-in, and team-lead views by window and cohort.
- **More to score and to watch:** per-mode scoring profiles, Prometheus metrics, alerts and a
  weekly digest, a read-only MCP server, and pull-request outcomes shown beside the score.
- **Container images are checked before they ship.** CI builds and smoke-starts every image,
  and fetches the dashboard's `blazor.web.js`. A published dashboard image had rendered a UI
  nobody could click.

### Upgrading from 1.0.x

- **`GET /api/sessions` returns a page object**, `{sessions, total, limit, offset, durable}`,
  not a bare array. A client that reads the array needs a one-line change.
- **With an ingest key set, all of `/api` needs a key**, reads and `DELETE` included. Only
  `/api/health` stays open. A single legacy key still grants every scope.
- **Some scores move.** Autonomous-agent sessions are scored with their own weights, in which
  latency and acceptance no longer count; interactive sessions keep the published weights.
  Edits Claude Code accepted on its own, under `acceptEdits` or `bypassPermissions`, no
  longer count as a developer's acceptance. A session scored under 1.0.x can therefore score
  differently now.
- **Frustration analysis is now workflow-friction signals, and is off** unless
  `CopilotScope:WorkflowFriction:Enabled` is set. The seeder's `frustrated` persona is now
  `repair-loop`, which changes seeded session ids.
- **Cursor is no longer listed as supported.** It is labelled unverified; four assistants are
  supported.
- **Building from source needs the .NET 10 SDK.** Every project targets `net10.0`.
- **`install.sh` and `install.ps1` install the native binary.** Pass `--docker` (`-Docker`)
  for the Compose stack, as before.

The sections below are grouped by when the changes landed, newest first.

### Changed
- **The documentation starts from the native binary — the last step of
  [ADR-004](docs/architecture/ADR-004-native-distribution.md).** The first-run tutorial, in English
  and Polish, now goes from an empty machine to scored history with nothing installed first. It
  covers install, start, the Claude Code history read by itself, `setup` and `doctor`.
  - The connect and team tutorials say where each path starts. `docs/TUTORIAL.md` §0 and §0.5
    lead with the binary and its automatic scan, and both manual editions follow.
  - The Docker stack stays documented in full, for teams and shared servers, behind `--docker`.
  - `copilotscope` says "Claude Code sends telemetry here", not "send", when one assistant does.
- **The installers install the native binary — step six of
  [ADR-004](docs/architecture/ADR-004-native-distribution.md).** `install.sh` and
  `install.ps1` no longer need Docker for one developer on one machine:
  - They pick the release archive for this platform and check it against the release's
    `SHA256SUMS`, refusing it on any mismatch, and refusing a release that has none.
  - They install it into `~/.copilotscope/app`, swapped in whole, so an interrupted install
    leaves the previous version. A running copy is stopped first.
  - They put `copilotscope` on the PATH, then run `copilotscope setup` — on the terminal even
    under `curl | sh`, and writing nothing where there is no one to ask.
  - They recognise Rosetta and refuse musl, where there is no build.
  - They say so when the Docker stack already holds port 4318.
  - Until a release carries native builds, they fall back to the Docker stack when Docker is
    installed.

  `--docker` (`-Docker`) installs the Docker Compose stack exactly as before, for a team or a
  shared server; `--bind`, `--api-key` and `--tag` without it are refused instead of ignored.
  `scripts/test-install.sh` runs the real installer against each platform's archive served
  as a release, on every pull request that touches it. It checks the binary on the PATH, an
  update in place, `--yes` connecting Claude Code, and a tampered download being refused with
  the installed copy untouched.

### Added
- **`copilotscope capture-fixture` and `scan --report` — so GitHub Copilot's history can be read
  next ([ADR-004](docs/architecture/ADR-004-native-distribution.md), decision 5).** There is
  still no reader for VS Code Copilot Chat's or Copilot CLI's files, and there will not be one
  built from a guess at their format.
  - `scan --report` describes what each assistant keeps on the machine — files, sizes, the field
    names at the top of each record — and never a word of content.
  - `capture-fixture <assistant>` writes a redacted sample to a local folder, to read and then
    share. It keeps the structure, the field names, the numbers and a short list of structural
    values. Ids, paths, URLs and e-mail addresses become stand-ins that stay consistent across
    files, including fields named like ids whatever their shape, so records still pair up and
    deduplicate. Timestamps move by one random offset. Every other string becomes its length.
    MCP tool names are replaced too, except CopilotScope's own, because a server's name can name
    something internal.
  - Before anything is written, the result is searched for the home directory, the user and
    machine names, the git name and e-mail, and token patterns. Any match refuses the whole
    capture, and the refusal names the kind of match, never the value.
  - Tested on a real Claude Code transcript. A redacted capture parses to the same calls, turns,
    tool calls, tokens and duration as the original.
- **`copilotscope setup`, `connect`, `disconnect` and `doctor` in the native binary — step five
  of [ADR-004](docs/architecture/ADR-004-native-distribution.md).** Pointing an assistant at
  CopilotScope no longer needs the control script, or the python or node it merged settings
  with. `setup` finds Claude Code, VS Code and Copilot CLI, shows the exact change to each one's
  own settings, and writes it only on a yes — typed, or given up front with `--yes`; with no
  terminal to ask in, it writes nothing. `connect <assistant>` and `disconnect` do one at a time
  (`--capture`, `--traces`, `--endpoint` and `--print` as before), and every start names the
  assistants that send telemetry and the ones that could.
  - **The same keys as the control script, held to it by a test** that reads
    `scripts/copilotscope` and `scripts/copilotscope.ps1`: disconnecting with either removes
    what the other wrote, and Copilot CLI's shell-profile block keeps the script's markers.
  - **Settings files are handled as someone else's.** Strict JSON only — a file with comments
    or trailing commas is left alone and the change printed, since rewriting it would drop
    them; a backup beside it before it changes; written atomically, through a symbolic link
    rather than over it (dotfile managers link these files into place), with its line endings
    and permissions kept; and not touched at all when nothing would change. Disconnecting takes
    out exactly the keys connecting put in, and a profile block with the blank line before it.
  - **Copilot CLI**: a block in the shell's profile on macOS and Linux — in fish syntax for
    fish — and user environment variables on Windows.
  - **`doctor`** checks the running instance, its storage and the dashboard's files; each
    assistant's settings, and whether they point here; an `OTEL_EXPORTER_OTLP_ENDPOINT` in the
    shell that overrides them; and the history on disk. It also catches the mistake everyone
    makes once: Claude Code connected, used since, and none of its telemetry arrived — a session
    that read its settings before they changed.
- **`copilotscope` reads your Claude Code history by itself — step four of
  [ADR-004](docs/architecture/ADR-004-native-distribution.md).** Transcripts Claude Code already
  keeps on disk (`~/.claude/projects`, `~/.config/claude/projects`, or `$CLAUDE_CONFIG_DIR`) are
  scored without an import step, from the first start on. The native binary looks once a
  minute, reads only what changed, and hands sessions to the collector through
  `POST /api/import` over HTTP, the way the importer does, so the endpoint's guards apply
  unchanged. The start-up banner names the folders it reads; `copilotscope scan` scans now and
  says what it found; `--no-scan` turns it off.
  - **Telemetry wins the race.** A session is imported only after its files have been quiet
    for ten minutes. An import never replaces a live session, but telemetry arriving after an
    import is added to it and would count the same calls twice; ten minutes is longer than any
    exporter an assistant uses waits to send.
  - **Read again only when something changed.** `scan-state.json`, kept in the data directory
    it describes, records a fingerprint of each session's files and the version of the reader
    that read them. A grown transcript is imported again and replaces its session; a parser fix
    (`ClaudeCodeTranscript.Version`) reaches the whole history, not only what comes after it.
    Deleting the history, or pointing `--data` elsewhere, starts the import over.
  - **Read-only, and nothing lost.** Transcripts are never written to, prompt and response
    text is never imported, and a transcript Claude Code deletes after a month never deletes
    its session. A collector that cannot be reached, a file that cannot be read, or a session
    the collector refuses costs that session one retry, never the rest of the pass.
  - **Repository labels without running `git`.** `GitRemote` now reads the origin remote from
    the repository's own config file, linked worktrees included. The scanner runs unattended
    over every project in someone's history: git may not be installed, a process per project
    is the wrong cost, and on a Mac without the developer tools `/usr/bin/git` opens an
    installer dialog. The importer gets the same labels, faster.
- **`copilotscope`, the native binary — step three of
  [ADR-004](docs/architecture/ADR-004-native-distribution.md).** One self-contained executable
  that runs the collector and the dashboard in one process on this machine: no Docker, no
  Postgres, no .NET install. `copilotscope` starts both — telemetry on `localhost:4318`, the
  dashboard on `localhost:5200` — keeps sessions in `~/.copilotscope/data` (the file store), and
  opens the browser; `status`, `stop`, `open`, `url` and `version` do what they say. It binds to
  loopback only, answers only to loopback host names (DNS rebinding cannot reach the
  transcripts), loads no hosting-startup assemblies from the environment, and never exports its
  own telemetry — the collector a developer's shell points `OTEL_EXPORTER_OTLP_ENDPOINT` at would
  otherwise ingest itself, forever. A second `copilotscope` finds the first and opens it instead
  of failing on the port; a Docker stack already on `:4318` is named rather than fought with; the
  dashboard moves to the next free port if `5200` is taken, the telemetry port never does. Stop
  and start are orderly — the dashboard first, then the collector with its final flush — and
  `copilotscope stop` proves it is talking to its own instance with a per-run token, not a
  process id.
  - `scripts/package-native.sh <rid>` builds the release archive — the single-file binary, the
    dashboard's `wwwroot` from the dashboard's own publish, the licence, and nothing else — and
    `scripts/smoke-native.sh` tests it the way a user meets it. The new `native` CI job runs
    both on every pull request and keeps the linux-x64 archive for a week.
  - ServiceDefaults' self-telemetry is now switchable (`CopilotScope:SelfTelemetry:Enabled`,
    on by default, so every other deployment is unchanged).

  Not yet in this step: the installers, which follow as their own change. Scanning existing
  chat history followed in step four, above.
- **Native binaries on every release** (`.github/workflows/release-native.yml`). A
  `v<major>.<minor>.<patch>` tag builds `copilotscope` for linux-x64, linux-arm64, osx-arm64,
  osx-x64, win-x64 and win-arm64, each packaged and smoke-tested on its own kind of runner, and
  attaches the six archives and a `SHA256SUMS` file to that tag's release, marking it latest.
  Archive names carry no version, so `releases/latest/download/copilotscope-<platform>.…` always
  resolves to the newest. A pull request touching the native binary runs the same matrix and
  attaches nothing, so a Windows, macOS or ARM break shows up before a tag does. `copilotscope`
  also stops in order when its terminal closes (SIGHUP; on Windows, the console window), rather
  than being cut off mid-write.

### Fixed
- **Imported Claude Code history is counted the way live telemetry is.** The transcript
  importer had drifted from the rules the OTel path follows, in ways that only ever made a
  session look busier, newer or better than it was — and the native binary is about to run it
  continuously (ADR-004):
  - **CopilotScope's own MCP reads were scored.** Both OTel ingest paths drop them
    (`SelfObservation`); the importer counted them as tool calls, so reading a score raised it.
    They stay on the timeline and leave the counters, as they do live.
  - **One response written across several lines was counted once per line** whenever those
    lines repeated its `usage`. Calls are now counted once per message id, at the most
    complete figure any of its lines reported.
  - **A subagent's transcript replaced the session that ran it.** It carries its parent's
    session id, and files were imported one at a time, so whichever was sent last won. Files
    are now grouped by the session id inside them and read together in timestamp order; a
    subagent's calls count toward the turn that launched it, and its prompts — the parent
    agent's instructions — no longer count as the developer's turns.
  - **A line with no timestamp was stamped "now"**, dragging a months-old session into every
    "last 7 days" view. It is placed at the previous line's time and never moves the session.
  - **The turn list had no cap on import**, unlike live telemetry's 200; now it has the same
    one, with every prompt still counted.
  - **Only `~/.claude/projects` was read.** `~/.config/claude/projects` is read too, and
    `$CLAUDE_CONFIG_DIR/projects` when that is set.
- **Live telemetry now marks an imported session as live.** Only a merge used to, so the next
  import of the same transcript replaced the session — and with it the latency and edit
  decisions the telemetry had added. The import endpoint already refuses to overwrite a live
  session; now it sees one.
- **Importing a long history no longer keeps all of it in memory** when storage is durable:
  the memory cap is applied as sessions arrive, since the store still serves every one of them.
  In memory-only mode nothing is evicted, because there it would be deleted.
- **A manual research-PDF run no longer becomes the "latest" release.** It published a PDF-only
  release that GitHub then treated as the newest — `manual-run-9` is — which would have made
  every `releases/latest/download/…` link for the native binaries 404. Manual runs are now
  marked as pre-releases and never latest.
- **A shutdown that arrives twice no longer loses the session being written.** The shutdown
  flush added with file storage ran once per call, and a host can be stopped twice at once:
  `WebApplicationFactory` stops it while the application's own `RunAsync` sees the stop, stops
  it again, and then disposes the container. The second call found nothing left to flush,
  returned immediately, and the store was disposed under the write the first call was still
  making. `PersistenceWriter.StopAsync` is now idempotent — every caller waits on the one final
  flush — and `FileStorageCollectorTests.SessionsOutliveTheCollectorProcess`, which had become
  flaky on `master`, is deterministic again.
- **`copilotscope` no longer watches its own data directory.** Both applications reloaded their
  settings on change. That put a recursive file watcher on `~/.copilotscope`, sessions and all,
  once for each application. On Linux each watcher holds one of the user's inotify instances.
  There are 128 by default, shared with every editor and .NET tool the user runs, and at that
  limit `copilotscope` could not start. Settings are now read once, at start.
- **A failed native smoke test says why.** Where a command failed outside a check, the script
  used to exit without a word: an unguarded `curl` exited 7 and printed nothing. Any failure
  now names the line and prints the process's own output. The test also waits for the instance
  to report itself running, rather than for the collector alone.

### Changed
- **The collector and the dashboard are built, not just run.** Everything their `Program.cs`
  did inline now lives in `CollectorApp.BuildAsync` and `DashboardApp.Build`, and each
  `Program.cs` is a single line that builds the application and runs it. Nothing changes for the
  container images, the Aspire AppHost or `dotnet run`. What it allows is the next step of
  [ADR-004](docs/architecture/ADR-004-native-distribution.md): one native `copilotscope` process
  that runs both applications side by side. A host passes its content root, web root and
  environment up front, since none of them can change once the builder exists, and gets a
  callback before anything reads configuration. Each application also carries its
  `appsettings.json` compiled in, and uses it only when its content root has none. A host's
  publish directory can't hold two applications' copies, and without the collector's copy the
  model pricing table would go with it.

### Added
- **Session history on disk without a database, and the decision it starts.** Without Postgres
  the collector kept only the 200 most recently active sessions, in memory, and lost them all
  on restart. That is how `dotnet run` has always behaved, and how the native binary for
  individuals planned in [ADR-004](docs/architecture/ADR-004-native-distribution.md) would have
  behaved too. `CopilotScope:Storage:Mode` now chooses: `postgres`; `files`, one JSON file per
  session under `CopilotScope:Storage:Path` (default `~/.copilotscope/data`); `memory`; or
  `auto`, the default, which does exactly what every existing deployment already did.
  - **`ISessionRepository`**, extracted from the Npgsql repository (now
    `PostgresSessionRepository`, its SQL unchanged), with the contract both stores owe the read
    path written down once. `FileSessionRepository` keeps the same snapshot document Postgres
    keeps in its jsonb column, and answers queries from an in-memory index. It writes
    atomically, refuses a second collector on the same directory, and refuses a directory
    written by a newer version. Its index cache is only a cache: startup re-reads any file
    whose size or write time disagrees with it.
  - **One contract suite, run against both stores**: pages, time windows, internal sessions,
    cohort filters, the baseline population, retention and deletes. The new
    `storage-contract` CI job runs it against a real Postgres. A filter added to one store
    and not the other is the kind of difference that passes review and then shows two
    different histories for the same data.
  - **A shutdown flush.** `PersistenceWriter` wrote once a second and never on the way out, so
    a normal stop — Ctrl+C on a laptop, `docker compose down` — dropped up to a second of
    telemetry. It now writes what is pending before the host stops, within five seconds.
  - `/api/health` gains `storage` (`memory`, `postgres` or `files`); `persistence` stays, for
    the control scripts' `doctor`. The dashboard's status chip said "Postgres" for any durable
    store; it now names the one in use.

  ADR-004 records the wider decision: a native binary for individuals, Docker Compose for
  teams, and the order the work lands in. This is the first step, and it is useful on its own:
  `dotnet run` keeps its history now.
- **A read-only MCP server, a skill, and the ingest fix that had to come first.** The score
  is most useful to the person whose session it was, at the moment the session ended — which
  is exactly when nobody is looking at a dashboard. Three pieces, all opt-in:
  - **`tools/CopilotScope.Mcp`** — a zero-dependency stdio MCP server exposing five reads:
    `health`, `list_sessions`, `get_session`, `overview`, `signal_coverage`. No write, delete,
    seed or import tool, and no way to configure one; a test enforces the list. It takes *no*
    project reference to the Collector and talks to it over the same HTTP API as any other
    client, deliberately: the privacy guard, the k-anonymity floor and the access audit log
    all live on that path, and an in-process reader would route around all three. 401 and 403
    are reported differently, because the collector answers 403 only when privacy mode is
    withholding a view on purpose, and telling someone to go find an API key for that would
    be advice to work around a control their organisation chose.
  - **`SelfObservation` in the Collector, and the exclusion at both ingest paths.** An MCP
    call is made from inside the session being scored, and tool calls are part of the formula.
    Reliability — 0.25 of the composite, its largest single weight — is the error-free rate
    over `ChatCalls * 2 + ToolCalls`, so every successful read of a score would have raised
    the score being read; and `SegmentAnalyzer` compares each turn's tool-to-chat ratio
    against the session median to find repair loops, which injected calls move on both sides,
    masking real ones and manufacturing false ones. The collector now drops its own reads
    before they reach any counter, on the `execute_tool` span path and the `tool_result` log
    path alike — the latter being Claude Code's default, not an opt-in extra. The call is
    still recorded in the session timeline: dropped from the counters, not from the record.
    Recognition is by tool name, which is why `copilotscope mcp install` registers the server
    as `copilotscope` and why registering it by hand under another name will move your scores.
  - **`skills/copilotscope/SKILL.md`**, installed by `copilotscope skill install` and shipped
    inside the tools image so it needs no clone. A quality score is easy to quote badly in
    predictable ways, so the skill teaches the assistant to quote confidence alongside the
    number, check signal coverage before comparing two assistants, read the turn analysis
    instead of restating the composite, and never rank people with a session score. Those
    rules were already in the README; the difference is that a skill is read by the thing
    generating the answer rather than by someone who has already been given a wrong one.

  Also **`CLAUDE.md`**, which the repository had never had: the build and test commands, and
  the invariants that compile, pass review and fail later — `PersistedSession` mirroring
  `CopilotSession` through both conversions, the TFM and the `Dockerfile*` base images
  retargeting together (#55), scoring staying a pure function, and the bilingual pairs.
  Documentation in `docs/MCP.md`.
- **A bilingual manual, built from LaTeX, and the toolchain that keeps it honest.** The
  repository had reference documentation and no explanation: `docs/TUTORIAL.md` tells a reader
  which variable to set, and nothing told them what the composite score is made of, why
  eviction is not deletion, or why an 80 from Claude Code is not an 80 from VS Code. There was
  also nothing in Polish, which is the first language of the people most likely to review it.
  Modelled on the same pattern as the `agent-eval-bench` estate, adapted to this repository:
  - **`docs/papers/copilotscope-manual.tex` and `.pl.tex`** — the whole system in one
    document, 24 pages per edition: the problem, the architecture, the ingest pipeline, the
    session lifecycle, the six components and the arithmetic that renormalizes them, turn
    analysis, all ten evaluation algorithms with their real status, the per-emitter signal
    matrix, the honest limits (not calibrated, not a scoreboard, and why both matter), the
    privacy and credential model, and a tutorial that runs from an empty machine to a Grafana
    panel. Neither edition is a translation of the other's prose; both present the same
    documents of record and say so.
  - **`docs/tutorials/`** — four guided tutorials in English and Polish: first run, connecting
    an assistant, reading a session, and a team deployment. The last one is deliberately a
    document you read before running anything.
  - **`docs/DIAGRAMS.md` and `docs/diagrams/`** — twelve diagrams, each existing exactly twice:
    inline for GitHub to render, and as a `.mmd` file the manual includes as a vector PDF.
    `scripts/render-diagrams.mjs` does the rendering with a pinned Mermaid CLI, so the two
    editions share one picture rather than each maintaining a translated copy, and nothing is
    redrawn in TikZ.
  - **`.github/workflows/build-docs-pdf.yml`** — builds both editions on demand and uploads
    them as one run artifact. Deliberately separate from `build-research-pdf.yml`: that builds
    the formal papers on a release tag and needs `contents: write`, while this needs no write
    permission at all. PDFs stay build output and are never committed.
  - **Two new CI checks, because both halves of this are drift surfaces.**
    `scripts/check-diagrams.mjs` fails if a diagram disagrees with its embedded copy — which
    also closes an existing gap, since `README.md` and `architecture.mmd` held the same bytes
    with nothing checking that they stayed equal. `scripts/check-doc-parity.mjs` fails if one
    language edition is edited without the other; it cannot check that a translation is
    correct, but it checks that somebody looked. The `docs` job also renders every diagram for
    real, because a Mermaid syntax error is otherwise invisible until a manual build somebody
    runs once a quarter.
- **A one-command install, and nothing to declare on a local run.** Getting to a first scored
  session took eight or nine steps: download a compose file, generate two secrets, export them,
  start the stack, edit a JSON file by hand, reload a window, export a third variable for the
  key, and — to see anything at all — install a .NET SDK to run a generator. `install.sh` /
  `install.ps1` replace the first half and a `copilotscope` control script replaces the rest.
  The pieces:
  - **No credential on one machine.** Every published port now binds to `127.0.0.1` (#65),
    Postgres publishes none at all and trusts its unpublished socket, and the ingest key is
    empty — the collector's open mode. A key on loopback protects nothing and costs a
    configuration step in *every* client, which is the step people got wrong. Publishing
    beyond loopback is a different deployment and is treated as one: `copilotscope up --bind`
    and `install.sh --bind` both refuse to run without `--api-key`, and the compose files pass
    the published address as `CopilotScope__Ingest__Bind` so the collector can log a startup
    warning when it is exposed without a key. It cannot infer that by itself — behind Docker
    every request arrives from the bridge gateway, so the remote address proves nothing.
  - **`copilotscope connect <assistant>` writes the assistant's own settings file.** Claude
    Code gets an `env` block in `~/.claude/settings.json`, which applies to every terminal and
    every project, so the most common failure here disappears: exporting variables in one shell
    and starting `claude` in another. VS Code gets its user `settings.json` (a window reload is
    still needed, and is still not automatable). Copilot CLI remains environment variables
    because they are the only thing it reads. Cowork prints what to type into the desktop app,
    including the `/v1/logs` path it wants and the base endpoint it does not. Merging goes
    through a real JSON parser with a `.copilotscope.bak` beside the file; a settings file with
    comments in it is reported and left alone rather than rewritten by a regex.
    `copilotscope disconnect` removes exactly the keys that were added.
  - **`copilotscope doctor`** automates the thirteen-point troubleshooting list: Docker, the
    containers, the collector's health document, exposure without a key, what each assistant's
    settings file actually says, whether an exported `OTEL_EXPORTER_OTLP_ENDPOINT` is
    overriding it, and how many transcripts are sitting on disk unimported.
  - **A `copilotscope-tools` image** carrying the seeder, the transcript importer and the
    telemetry generator, so `copilotscope import`, `demo` and `probe` work with no .NET SDK and
    no clone. This is what finally makes the no-configuration import path
    (`tools/CopilotScope.LogImporter`, added for #98) reachable by the users it was written
    for. One caveat, stated in the output: the container cannot read git remotes, so imports
    run this way carry no repository label — run the importer from a clone when that matters.
  - **AgentForge and the judge agent moved behind a compose profile.** Both used to start with
    every stack and neither serves traffic until model configuration is supplied, so a first
    run paid for two idle containers.
- **CI now builds the container images and checks the scripts** (#57). `dotnet build` never
  covered either, so a broken Dockerfile or a syntax error in the installer surfaced at release
  time — which is how a release once shipped images that exited at startup (#55). Three new
  jobs: build and smoke-test all five images, parse every shell and PowerShell script
  (`pwsh` is preinstalled on the Ubuntu runner, so the Windows scripts are covered without a
  Windows job) and run `shellcheck`, and render the compose files with no environment at all to
  assert that a first run needs no variable and that nothing binds beyond loopback.
- **`GOVERNANCE.md` — the "who maintains it?" answer** (#103). The first question anyone asks
  before putting a tool in their telemetry path, and until now unanswerable: one maintainer,
  no stated response posture beyond security reports, and no schema-stability guarantee, so an
  adopter could not know what an upgrade might break. The document names the stable surfaces
  (OTLP ingest and its attribute vocabulary, REST DTO fields, Prometheus family and label names,
  the jsonb snapshot shape, the calibration label schema, `CopilotScope:` config keys) and the
  ones that may move in any release — including, explicitly, the quality score's weights, which
  *will* change when calibration happens, and which is why scores are comparable within a
  version and not across one. It also answers the abandonment scenario head-on rather than
  hoping nobody raises it: the data is in the adopter's own Postgres with documented export
  paths for every table, the licence permits forking permanently, and nothing phones home — a
  self-hosted MIT tool that stops moving keeps working, which is a lower risk than a SaaS vendor
  that goes away with your data. §6 states what a co-maintainer would actually do, in order of
  value, and how commit access works.
- **A GitHub Copilot Metrics archiver, breaking the 28-day window** (#97). GitHub serves 28 days
  of org usage and nothing older; admins have been asking for history since it shipped, and the
  most-used tool in this space had to add a database purely to keep it. `CopilotScope:VendorMetrics`
  (off by default, needs Postgres) polls daily and archives the window indefinitely, keyed
  `(provider, scope, day)` so a re-poll overwrites rather than accumulating 28 duplicates a day
  and a restart costs nothing. **The full response document is stored verbatim** alongside the
  extracted counts — the vendor deletes the original, so a parser that kept only what it
  understood today would silently discard history that cannot be re-fetched. Served at
  `GET /api/vendor/metrics` and exported as `copilotscope_vendor_*` gauges, including "days held
  beyond the vendor's own horizon", with two new provisioned Grafana panels. The connector
  interface is deliberately narrow (fetch a window, return days) so Anthropic's and Cursor's
  admin APIs can follow as one class each. Org/enterprise level only: the GitHub API can be
  asked for a per-developer breakdown and this does not ask. Framed in the payload, the docs and
  the architecture diagram as **context beside the quality score, never instead of it** —
  counting usage still does not tell you whether the tooling helped.
- **An opt-in session labelling flow** (#102). The composite's credibility problem is written
  down in this repo twice over — no calibration has been run, there are no human labels, and the
  product review calls the score "an opinion with a confidence interval, not a measurement" —
  while the machinery that would fix it (quadratic-weighted κ, bootstrap CIs, human-ceiling
  checks) was already built and had nothing to consume. `CopilotScope:Labelling:Enabled` (off by
  default) adds a **Rate this session** panel: a free-text rater handle not tied to telemetry
  identity, band 0–3 per rubric with the anchor text, and a skip option — a skip is a real
  answer and is stored but not exported as a label. `GET /api/labels/export` emits exactly the
  shape `calibration/labels.example.json` uses, pinned by a round-trip test through
  `CalibrationEngine`. **Seeded sessions are excluded by default**: labelling the Seeder's own
  synthetic personas would validate the scoring model against fiction this repository wrote, and
  a report built on that circle is worse than none because it arrives carrying a κ. Overriding
  stamps `-synthetic` into the dataset version. `RubricScale` moved into the Collector, since
  the judge's output, a rater's form and the calibration engine now all have to be reading the
  same sentence. Labels rate sessions, never people — the schema has no field for the developer
  being rated.
- **Import Claude Code's own transcripts — the no-configuration path** (#98). Most developers
  never flip OTEL env vars; the grassroots tools that parse these files built their entire user
  base on that fact. Claude Code already writes every session to
  `~/.claude/projects/**/*.jsonl`, so `tools/CopilotScope.LogImporter` reads them and posts
  first-class scored sessions to a new admin `POST /api/import`. Re-running is idempotent —
  sessions keep Claude Code's own id — and the collector **refuses** to overwrite a session it
  already holds from live telemetry, because the import carries less signal and would silently
  lower its score. Prompt text is not imported unless `--include-content` is passed. The parsing
  handles the two traps in the format: one model response is split across several assistant
  lines (counting blocks rather than `usage` doubles every call count) and tool results arrive
  as *user* messages (counting them as prompts invents turns). Because the importer runs on the
  developer's machine it resolves the actual git remote and normalizes it the way outcome
  linkage does, so imported sessions land in the same repository cohort as live ones rather than
  forming a duplicate. Imported sessions are badged **imported** and carry genuinely lower
  confidence: time-to-first-token, edit decisions and feedback are OTel events the transcript
  does not record, so they are left absent rather than defaulted to zero.
- **Quality-regression alerts and a weekly digest — push, not just pull** (#101). A dashboard
  that must be visited gets abandoned; an output that triggers a decision gets renewed, and
  session scoring is the only thing that can raise a *quality* regression — a vendor usage
  dashboard cannot alert on what it does not measure. `CopilotScope:Alerts` (off by default,
  and the only outbound path in the collector) compares each window against the one before it
  by repository, assistant and model and posts a webhook, as JSON or as the single `text`
  field most chat webhooks render. The precision is the feature: a cohort needs enough
  sessions in **both** windows, session kind is never alerted on, and — the one that matters —
  a score drop that came with a confidence drop is reported as a *changed measurement basis*
  rather than a regression, because the composite renormalizes over the components that have
  data and a cohort that stopped reporting a signal is measured differently, not worse.
  `GET /api/digest` serves the aggregate week with no webhook configured; `POST /api/digest/send`
  (Admin) sends it. `grafana/provisioning/alerting/` provisions two equivalent Grafana rules
  built entirely from gauge ratios — never `rate()` over a family that can decrease — so they
  sidestep the counter-semantics problem in #70 rather than waiting on it, with a test pinning
  that. Both surfaces honour the k-anonymity floor, which matters more here than on a screen
  because the payload leaves the deployment.
- **Team-lead views: windows, cohorts, before/after, export** (#96). The stated buyer — a
  platform lead evaluating assistants for a team — could not answer a single leadership
  question inside the product: no trends, no cohorts, no comparison, no shareable links, no
  export. Both pages now take a time window and a cohort (repository / assistant / model),
  filtered in SQL rather than after the page is fetched. `GET /api/cohorts` rolls a window up
  by each axis; `GET /api/compare` puts two windows side by side for one cohort, defaulting
  the baseline to the equally long window before the current one, and reports its caveats
  (small samples, mismatched window lengths) next to the deltas instead of folding them in.
  Sessions are deep-linkable at `/sessions/{id}`, and Overview's top-session links now
  resolve to the named session instead of dropping the reader on the list. Any filtered
  rollup or comparison exports as CSV — group rows only, no session ids — and the Overview
  page prints to a one-page utilization / impact / cost summary. **No view and no export has
  a per-developer dimension**, which is asserted by tests rather than assumed.
- **Privacy mode: works-council and GDPR controls that are enforced, not documented** (#94).
  The "not for performance reviews" promise was a convention — nothing stopped an operator
  reading one developer's transcripts, and German BetrVG §87(1)(6) grants co-determination
  over any system *capable* of monitoring performance, so intent is not an answer. Under
  `CopilotScope:Privacy` (off by default) identifying attributes are replaced by salted HMAC
  tokens at ingest, prompt/response content is dropped, no view renders below a k-anonymity
  floor of distinct subjects (per-session detail and per-session Prometheus series included),
  and every read and deletion is recorded to an access log with its own Postgres table and
  CSV export. Raw OTLP forwarding is refused unless explicitly allowed, because it relays
  payloads before redaction. `GET /api/privacy` reports what a deployment enforces;
  `docs/PRIVACY.md` carries the Art. 30 data map, retention behaviour and a template
  Betriebsvereinbarung annex.

- **Per-assistant signal coverage is disclosed** (#100). The composite renormalizes over the
  components that have data, which means an 80 from one assistant is not an 80 from another —
  a Claude Code session is scored without feedback or edit survival, so its 80 rests on less
  evidence. The project's own product review flagged this (B2) and noted nothing in the UI
  warned, while "compare assistants before you buy" is the headline use case. There is now a
  coverage matrix in `docs/SIGNAL_COVERAGE.md`, on the in-app Docs page and at
  `GET /api/coverage`, all served from one table in `Domain/EmitterCoverage.cs`; the session
  detail names the components its assistant can never report; and the rail warns when a visible
  list mixes assistants whose scores rest on different evidence.
- **Fixture capture and a semconv drift canary** (#92). `tools/CopilotScope.FixtureCapture` is a
  recording proxy: point an assistant at it, and it writes each real OTLP payload to
  `tests/fixtures/<assistant>/<version>/` while forwarding it upstream unchanged.
  `FixtureGoldenTests` replays whatever is committed there through the real decoder and session
  store, asserting the batch decodes to something and still classifies as its assistant. A weekly
  `semconv canary` workflow checks that upstream still defines every `gen_ai.*` name
  `Domain/Sem.cs` reads, and opens an issue when one disappears — a renamed attribute otherwise
  keeps ingest returning 200 while the counters it feeds go to zero. **No real captures are
  committed yet**: capturing needs a machine running the assistants, so until then the
  multi-assistant claim still rests on hand-built payloads.
- **The judge runs on your own hardware** (#91). `CopilotScope:JudgeAgent:Backend` selects
  `AzureFoundry` (the default, unchanged) or `OpenAiCompatible` — one implementation covering
  Ollama, vLLM, LM Studio and any in-region OpenAI-compatible gateway. Judging is the only
  feature that sends real transcript text anywhere, and the only destination used to be a cloud
  vendor, which locked the five LLM-graded algorithms away from exactly the self-hosted and
  regulated deployments they are most valuable in. Same prompts, rubric and fingerprint — only
  the transport changes. Judge responses now carry `backend`, `model` and `judgePromptVersion`
  provenance, matching what calibration runs already record.
- **Cloud services authenticate to a secured Collector** (#90). `JudgeAgent` and `AgentForge`
  read `CopilotScope:{Service}:CollectorApiKey` and present it as `x-api-key`, falling back to
  `CopilotScope:Ingest:ApiKey`. Both report `collectorAuthConfigured` on `/api/health`.
- **Session history is served from Postgres** (#84). `/api/sessions` and `/api/overview`
  read the database with the live in-memory aggregates layered on top, instead of reading
  the bounded in-memory store alone — a team churned past that cap in hours, after which
  its history vanished from every surface despite being safely persisted. Endpoints take
  `days`/`since`/`until`, `limit` and `offset` and return `{sessions, total, limit, offset,
  durable}`; `/api/sessions/{id}` falls back to Postgres so a link to last week's session
  keeps working. Without Postgres the same paths serve memory and report `durable: false`.
  Retention is configurable (`CopilotScope:History:RetentionDays`, default 0 = keep
  everything) and the percentile baseline is computed over a fixed trailing window rather
  than whatever survived in memory.
- **Per-mode scoring profiles** (#88). Sessions are classified interactive /
  supervised-agent / autonomous from telemetry shape, and `ScoringProfile` supplies the
  weights. Interactive keeps the published v2 weights exactly; autonomous zeroes latency
  and acceptance — nobody waits on a background run's first token, and an agent under
  `acceptEdits` applies its own edits — and moves that weight to friction and reliability.
  Excluded components are still computed and shown as "not scored" with the reason.
- **Delivered-outcome linkage** (#87, opt-in). A HMAC-verified GitHub webhook at
  `POST /api/outcomes/github` records pull-request outcomes (merged, closed, reverted,
  time to first review, time to merge), joined to sessions by repository, branch and time
  with an explicit confidence. Shown beside the score, never folded into it: the score has
  not been validated against outcomes yet, and collecting them is what makes that possible.
  Repository-level only — no author, no reviewer.
- **Scoped collector API keys and dashboard sign-in** (#86). `CopilotScope:Keys` splits the
  single shared secret into Ingest / Read / Admin, so the key every editor holds can no
  longer read transcripts or delete history; `CopilotScope:Dashboard:Auth` adds optional
  viewer/admin sign-in, with transcripts and deletion restricted to admin. Both off by
  default; the legacy single key still grants every scope.
- **Judge calibration (Cohen's κ)** — `src/CopilotScope.JudgeAgent/Calibration/` measures how
  well the judge agrees with human labels, closing the gap the README and
  `JudgeSystemPromptTemplate.txt` have both been stating ("directional, not final… until
  calibration data exists"). Cohen's κ with unweighted, linear- and quadratic-weighted variants,
  each carrying a seeded bootstrap 95% CI; the human panel's agreement with itself is measured
  first as the ceiling, because a judge validated against labels the labellers cannot reproduce
  is validated against noise. Verdicts are `calibrated` / `not-calibrated` / `ceiling-too-low` /
  `insufficient-data` against a configurable threshold (`CopilotScope:JudgeAgent:Calibration`,
  default κ ≥ 0.70 — the repo's own published acceptance criterion). Two endpoints:
  `POST /api/calibration/report` (pure arithmetic, deterministic, no model access — the one CI
  can run) and `POST /api/calibration/run` (grades each session with the live judge first,
  sequential, capped at 200 sessions per run). Labels are versioned JSON in `calibration/`;
  `labels.example.json` is a format template deliberately too small to certify anything. New
  `docs/CALIBRATION.md`. **No calibration has been run** — there are no human labels in the
  repository yet, so no judge score gates anything.
- Judge reports now record `judgePromptVersion`, a hash of `JudgeSystemPromptTemplate.txt`
  derived rather than declared, so a later rubric edit surfaces as a re-baseline instead of a
  silently moved measuring stick.

### Fixed
- **The published dashboard image shipped a UI nobody could click.** `Dockerfile.dashboard`
  restored against a context holding the `.csproj` files alone — the layer-caching pattern —
  and then published with `--no-restore`. That publish trusts the static web asset set the
  restore cached at a moment when the project's `wwwroot` was not in the context yet, so the
  output carried `app.css`, `app.js` and `favicon.svg` and no `_framework/` at all.
  `blazor.web.js` was therefore absent from the image, the Blazor circuit never started, and
  the dashboard rendered exactly once, server-side, with every control on it inert: the
  filters, the 7d/30d/90d range, the repository and assistant selectors, the
  Basic/Advanced/Full toggle, selecting a session, delete, and the two-second refresh. The
  page still answered `200` on `/`, which is precisely why nothing went red — the container
  healthcheck curls `/`, and so did the `containers` CI job. Anyone following the README's
  primary path, `docker compose -f docker-compose.ghcr.yml up -d`, got the dead copy; anyone
  running `dotnet run` did not, because a development build resolves those assets from disk.
  The publish no longer passes `--no-restore`, and the `containers` smoke test now also
  fetches `/_framework/blazor.web.js`, so an image that serves a page it cannot animate fails
  the build instead of shipping.

- **Every container image was unbuildable, and nothing was checking** (#57). All four
  Dockerfiles copy their own project into the build context and nothing else, but the
  collector and the dashboard both carry a `ProjectReference` to
  `src/CopilotScope.ServiceDefaults` — added when OTel self-instrumentation, health
  endpoints, service discovery and HTTP resilience were factored into a shared kernel. The
  project is simply not in the context, so `dotnet publish` fails with `CS0234` on the
  `CopilotScope.ServiceDefaults` namespace, behind an `MSB9008` warning naming the missing
  csproj. AgentForge and the judge agent reference it transitively and fail the same way.
  `dotnet build` at the solution level never noticed, because there the project is right
  where the reference says it is; and images were built only on a release tag, so the
  failure had nowhere to surface until CI started building them. Every Dockerfile now
  copies `ServiceDefaults`, the two that restore in a separate layer copy its csproj first
  so that layer stays cached, and the new `containers` CI job builds and starts all five on
  every pull request.

  Behind that failure sat a second one, reached only once the first was fixed: AgentForge and
  the judge agent are web projects that reference the collector, itself a web project, so the
  collector's `appsettings.json` and `appsettings.Development.json` land at the same
  publish-relative path as their own — error `NETSDK1152`. Suppressing it with
  `ErrorOnDuplicatePublishOutputFiles` would be worse than the error, because which copy wins
  is unspecified and the collector's winning would hand those services the collector's
  configuration under their own name, pricing and keys included. Each now drops the
  referenced project's copies from its publish list and keeps its own. This is a publish-time
  conflict only, which is why `dotnet build` and the test suite never saw it either.

### Changed
- **The dashboard screenshots are current again, and there are two more of them.** The
  committed shots predated the Overview rewrite: the file showed an "all chats — token burn"
  page that the application no longer leads with, next to a Sessions view from before the
  Basic/Advanced/Full split. All four are now captured from the running stack against the
  seeded demo dataset over a 60-day window — wide enough that the Overview's
  this-window-versus-the-one-before-it table compares two populated windows instead of
  printing its too-few-sessions caveat. `docs/img/dashboard-session-detail.png` and
  `docs/img/dashboard-docs.png` are new: the first is the Advanced breakdown of a single
  session, which is the one view that shows what the product actually computes, and the
  second is the built-in documentation page. Both tutorials that describe a screen now show
  it, in each language. The README says once, under the hero, that every screenshot in it is
  seeded demo data — the `DEMO` badge is visible in the images, and the tutorials already
  made the same promise in prose.

- **Dependencies.** `actions/setup-node` 4 → 7 and `actions/github-script` 7 → 9 (the v9
  breaks are `require('@actions/github')` and redeclaring `getOctokit`; the one script in
  `semconv-canary.yml` reaches only `github.rest.issues.*` and `context.repo`), Aspire
  hosting 13.4.6 → 13.5.4, the three OpenTelemetry packages in `ServiceDefaults` 1.17.0 →
  1.18.0, and `Microsoft.Agents.AI` 1.17.0 → 1.19.0 — whose two breaking changes in that
  range, the `AgentIsolationKeyProvider` rename and the MCP long-running task migration, name
  nothing AgentForge or the judge agent use. Dependabot closed its own
  `OpenTelemetry.Extensions.Hosting` pull request as "updatable in another way" after its two
  siblings merged, leaving that one package a version behind the set; it is bumped here.

- **The docs that the install change left behind are now correct too.** Moving AgentForge and
  the judge agent behind a Compose profile made `docs/JUDGE_AGENT.md` state something false:
  it said the judge agent "isn't gated behind a Compose profile, so it starts by default", and
  both its compose commands would now leave `:5400` refusing connections rather than answering
  "not configured". `docs/AGENTFORGE.md` had the opposite problem — it never said how to start
  the service at all, and its examples assume `:5300` is up. Both now lead with
  `--profile agents`, and `docs/CALIBRATION.md`, whose endpoints live on the judge agent, points
  at it. `CONTRIBUTING.md` still asked for the 9.0 SDK to build `net8.0` projects and cited
  C# 12 idioms, on a repository that has targeted `net10.0` for some time; it also cloned from
  the pre-rename repository slug. The landing page's "try it without a Copilot subscription"
  walkthrough, the README's no-config import section and its Copilot CLI section all still led
  with `dotnet run`, which is the one thing the pull-and-run path exists to avoid — each now
  leads with the container command and keeps the from-source form beside it.
- **The in-app and published setup instructions now match what the tools actually read.** The
  dashboard's own Docs page told Claude Code users to set an endpoint and a protocol and
  nothing else — omitting `CLAUDE_CODE_ENABLE_TELEMETRY=1`, without which nothing is exported
  at all, and `OTEL_LOGS_EXPORTER=otlp`, which is what carries the session — and pointed at
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` for content capture, a variable Claude
  Code does not read. Anyone following that page saw an empty dashboard and no error. The row
  now names all four required values, the three separate content opt-ins and the tracing beta
  that is the only source of time-to-first-token, and a Cowork row was added. The landing
  page's quick start could not work as written either (it omitted the two secrets the compose
  file then required) and claimed "GHCR packages start private", which has not been true since
  they were made public; both are corrected, along with a `.NET 8` badge and prerequisite on a
  repository that targets `net10.0`. The tutorial's §1 said `.NET 8 SDK` and advertised the
  removed `dev-secret-123`, and the setup scripts pointed at "section 8" for troubleshooting
  that lives in §9.
- **`scripts/setup.sh` / `setup.ps1` no longer generate an ingest key for a local run.** They
  wrote two random secrets into `.env` because the compose files demanded them; with the
  loopback-only posture that just puts an `x-api-key` step back into every client. A key is
  used only when one is passed explicitly or already sits in `.env`.
- **The dashboard's empty state carries the next action.** It used to name a `dotnet run`
  command as the way to see anything, which required the SDK the pull-and-run path exists to
  avoid, and described the problem rather than the fix. It now lists the `connect`, `import`,
  `demo` and `doctor` commands, all of which run in a container.
- **Positioning restated for a post-DX/Datadog market** (#99,
  [ADR-003](docs/architecture/ADR-003-positioning.md)). `docs/STRATEGY.md` claimed this was
  "the only open-source tool that turns telemetry from any AI coding assistant into a session
  quality score" and called the category empty. Three commercial entrants shipped it since:
  DX Agent Experience (Atlassian Team '26, May 2026), Datadog Agent Console (DASH, June 2026)
  and New Relic AI Coding Observability (2026-06-23) — the last of which is itself
  open-source, which removes even the fallback claim. A "nobody does this" line that a
  two-minute search refutes would discredit a project whose brand is radical honesty, on a
  page kept in the repo specifically so readers can check the reasoning. The landscape section
  now names all three with dates and treats their arrival as **category validation**: three
  companies with far better market research concluding the problem is real is the strongest
  external evidence the thesis has, and it costs only the "empty niche" framing, which was the
  weakest part of the argument. The claim is re-scoped to what survives all three — open
  source, **self-hosted**, **published deterministic formula**, and **cannot produce a
  per-developer ranking** (enforced by #94 and #96, not merely stated). `docs/COMPARISON.md`
  sets out what each product does, including a section on where the others are simply better.
  The timing claim ("a standard that had just finished settling") is also corrected: semantic
  conventions v1.42.0 deprecated `gen_ai.*` into a separate repository with no stable release,
  so the standard split rather than settled — and the weekly canary from #92 is the honest,
  checkable version of that argument.
- **Cursor is demoted from a supported assistant to unverified** (#93,
  [ADR-002](docs/architecture/ADR-002-cursor-support.md)). "Five assistants" in the README
  included one supported by a `service.name` substring check and a namespace rename, with zero
  Cursor-specific tests and no payload from a real Cursor session ever tested — while
  `docs/STRATEGY.md` said four, with a *different* membership. Radical honesty is this
  project's main competitive virtue, and a support claim the code cannot back costs more than
  the feature is worth. The supported count is now **four** (VS Code Copilot, Copilot CLI,
  Claude Code, Claude Cowork) and both documents agree on the membership as well as the number.
  Cursor is listed separately and labelled unverified in the README, the in-app Docs page,
  `docs/SIGNAL_COVERAGE.md`, `docs/TUTORIAL.md` §6 and `Domain/EmitterCoverage.cs`. The
  speculative setup guidance ("try adding the same VS Code settings … if Cursor exposes an OTLP
  env-var hook") is gone — that was an instruction to guess, published as documentation.
  **Nothing is removed from the ingest path**: anyone already pointing Cursor at the collector
  keeps exactly the behaviour they had, only the claim about it changes. Seeded sessions now
  carry a **demo** badge in the dashboard, since a fabricated Cursor session is what made the
  claim look proven in screenshots. Documentation tests hold both counts and the unverified
  marking in place.

### Fixed
- **The in-app Docs page called four implemented algorithms "not implemented"** (#58). G-Eval,
  SPUR, RAGAS and task-completion detection all ship in `CopilotScope.JudgeAgent` and the
  README's matrix says so; the dashboard's own copy of that matrix still described them as
  unbuilt, and it was also missing delivered-outcome linkage entirely. The two now agree. The
  signal-coverage section added this cycle was also reachable only from one link in the session
  rail — it is in the Docs table of contents now.

### Changed
- **Frustration analysis is now workflow-friction signals, and ships off** (#95). EU AI Act
  Art. 5(1)(f) prohibits workplace emotion recognition outright, so a feature named for
  inferring how a developer feels was the cheapest possible objection to hand a DPO — in the
  segment where this tool is most defensible. The rename is also a correction: the detector
  counts *observed repair events* (re-asking, rephrasing, corrective replies), which is what
  the signal was always useful for. `FrustrationAnalyzer` → `WorkflowFrictionAnalyzer`, and
  the JudgeAgent rubric `deep-frustration` → `deep-friction` (retired ids still resolve, so
  existing calibration labels keep counting). The analyzer no longer runs unless
  `CopilotScope:WorkflowFriction:Enabled` is set, per-message previews that quote prompt text
  need a second opt-in, and the default surface is the team/period rate at `GET /api/friction`.
  Rationale and the Art. 5(1)(f) position statement: `docs/WORKFLOW_FRICTION.md`. The seeder's
  `frustrated` persona is now `repair-loop`, which changes seeded session ids.

### Fixed
- **The cloud tier 401'd against any secured Collector** (#90). `infra/main.bicep` makes the
  Collector's ingest key a required parameter, so every Azure deployment gates `/api` — and both
  cloud services sent no key at all, so the judge and persona cohorts only ever worked against an
  open dev-mode Collector. Their inbound key checks also moved to a constant-time comparison
  (they still used `==`, which leaks the key under timing analysis); that comparison now lives
  once in `CopilotScope.ServiceDefaults` instead of in three copies that had already drifted.
- **Cross-developer session contamination behind a shared collector** (#85). Process- and
  service-scoped resource fingerprints are unique only within one machine, so two
  developers running the same assistant had their identity-less metrics merged into one
  conversation. Those fingerprint forms are now scoped by host (or by the source
  connection), and `copilotscope_hostless_signals_total` reports when neither is available.
- **Trimming a session could destroy its persisted snapshot** (#84). Late telemetry for an
  evicted session recreated it as an empty aggregate, and the next write-behind flush wrote
  that over the stored row. Evictions are now tracked and the stored snapshot is merged back
  before flushing. Trim also releases the evicted session's trace mappings, which previously
  outlived it.
- **Permission-mode auto-accepts inflated the acceptance score** (#88). Claude Code's
  `tool_decision` events carry a `source`; under `acceptEdits` or `bypassPermissions` that
  is `config`, not a human. Those now count as `EditsAutoAccepted` — reported, never scored.
- **README quick start could not work as written** (#89). The headline snippet omitted the
  `COPILOTSCOPE_API_KEY` and `POSTGRES_PASSWORD` exports that `docker-compose.ghcr.yml`
  hard-requires via `${VAR:?}` guards, so a first-time reader's very first command failed.

### Changed
- **`GET /api/sessions` returns a page object**, `{sessions, total, limit, offset, durable}`,
  rather than a bare array (#84). The dashboard client is updated; an external consumer
  reading the array directly needs a one-line change.
- **Retargeted every project to `net10.0`** and aligned the container base images
  (`sdk:10.0` / `aspnet:10.0`) with the TFM, so the published GHCR images start.
  `Aspire.AppHost.Sdk` aligned with `Aspire.Hosting.*` (13.4.6). CI builds on a
  single 10.0 SDK; `build-containers.yml` now smoke-tests each image before publish.
- **Security:** the whole `/api` group is gated deny-by-default by the ingest key
  (constant-time compare), so the query API and the destructive `DELETE` are no
  longer reachable unauthenticated when a key is set; `/api/health` stays open as a
  liveness probe. Decoded OTLP payloads are bounded (compression-bomb guard) and
  `/admin/seed` enforces the `seed-` id prefix server-side.
- **Secrets:** removed the committed `dev-secret-123` / `copilot-dev` defaults; the
  compose files require `COPILOTSCOPE_API_KEY` + `POSTGRES_PASSWORD` (no default),
  the setup scripts generate them into a gitignored `.env`, and a `gitleaks` job runs
  in CI.
- **Self-observability:** new `CopilotScope.ServiceDefaults` (OTel, health `/health`
  + `/alive`, service discovery, HTTP resilience) is called by all four services;
  the AppHost health-checks every resource.
- **Dashboard UX:** defaults to the Basic view with the view switcher in the topbar;
  score colour is now the absolute grade consistently (with grade text in the rail
  for colour-independent reading); the session rail no longer reshuffles under the
  cursor between polls.

### Added
- **Prometheus scrape endpoint** (`GET /metrics`) exporting the *computed* signals,
  not just usage: composite quality score and confidence, the six weighted score
  components, edit survival, TTFT percentiles, token and cost breakdowns, edit
  outcomes and feedback — every family labelled by `emitter`, so the four supported
  assistants stay distinguishable. Written by hand in the text exposition format,
  so the collector keeps its single NuGet dependency.
- Aggregates are exported as `_sum`/`_count` pairs so PromQL rollups over any label
  subset stay arithmetically correct.
- Per-session series (`session=` label) behind `CopilotScope:Prometheus:PerSession`,
  off by default and capped by `MaxSessionSeries`; the overflow is reported as
  `copilotscope_session_series_dropped` rather than silently inflating cardinality.
- `docker-compose.grafana.yml` — the full stack plus Prometheus and Grafana with a
  provisioned datasource and dashboard (`grafana/dashboards/copilotscope.json`).
- CI workflow building the solution and running the test suite on every pull request.
- Screenshots of the dashboard and the Grafana view in `docs/img/`.
- README section stating what CopilotScope must *not* be used for — performance
  reviews, acceptance-rate targets, single-number verdicts.

### Fixed
- Seeded "frustrated" persona sessions produced no strong-marker or rephrasing
  signal on short conversations: the final-turn fixtures were indexed by absolute
  turn number, so they landed on the mild corrective pair and `FrustrationAnalyzer`
  only ever reported "mild friction". Now indexed by position within the final pair.
- Broken architecture diagram in the README — `architecture.svg` sat in the repo
  root while the README (and the Pages site) referenced `docs/architecture.svg`.
- `CONTRIBUTING.md` referred to a `main` branch that does not exist (default is
  `master`) and pointed at GitHub Discussions, which is not enabled.
- Removed the stale "GHCR packages start private" note — both packages are public.

## [1.0.7] — 2026-07-20
- GitHub Pages deployment workflow (#15)
- Maturity progression timeline on the sessions view (#16)
- Basic view mode hiding advanced session details (#17)

## [1.0.6] — 2026-07-19
- Landing page and documentation website (#14)

## [1.0.5] — 2026-07-19
- Fixed horizontal overflow breaking the Docs page layout (#12)
- Walkthrough and practice exercises for the quality engine (#13)

## [1.0.4] — 2026-07-19
- Removed an unnecessary Razor code block in the Home component (#11)

## [1.0.3] — 2026-07-19
- Basic/Advanced/Full view modes on session detail (#8)
- Per-repo session normalization for quality percentile ranking (#9)
- Worked examples throughout the quality measurement framework (#10)

## [1.0.2] — 2026-07-18
- Expanded edit survival analysis: mechanics, examples, sensitivity (#7)
- Razor syntax cleanup (#6)

## [1.0.1] — 2026-07-18
- **Claude Code and Cursor support** (#3)
- Conversation popup with turn analysis (#2)
- Quality Measurement Framework paper (#4) and its automated PDF build (#5)

## [1.0.0] — 2026-07-13
- First release: OTLP/HTTP ingest with an in-repo protobuf decoder, session
  aggregation, the composite quality engine, TFRA turn analysis, Postgres
  persistence, and the Blazor dashboard, orchestrated with .NET Aspire.

[Unreleased]: https://github.com/konradcinkusz/copilot-scope/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.7...v1.1.0
[1.0.7]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.6...v1.0.7
[1.0.6]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.5...v1.0.6
[1.0.5]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.4...v1.0.5
[1.0.4]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/konradcinkusz/copilot-scope/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/konradcinkusz/copilot-scope/releases/tag/v1.0.0
