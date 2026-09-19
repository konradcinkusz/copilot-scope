# Diagrams

Every structural claim CopilotScope makes, as a picture — rendered by GitHub
itself, with no build step and no JavaScript.

These are the same diagrams the two LaTeX editions of the manual include. There
is one source for each: the `.mmd` files in [`docs/diagrams/`](diagrams/).
`scripts/render-diagrams.mjs` turns them into vector PDFs for the papers, and
`scripts/check-diagrams.mjs` fails the build if the copy embedded below ever
disagrees with the file.

## Each diagram exists twice, and a check keeps them equal

- **Inline here**, because that is the only form GitHub renders.
- **As a standalone `.mmd`**, because a paper cannot include a fenced code
  block, and because a diagram nobody can open on its own is a diagram nobody
  reuses — in a slide, an issue, or a review comment.

The pairing rule is the section id: `### A1.` owns `docs/diagrams/a1-*.mmd`. The
same check also holds the architecture diagram embedded in `README.md` to
`architecture.mmd`, a pair that was previously identical only by luck.

```bash
npm ci                      # once — the pinned mermaid CLI
npm run check:diagrams      # exactly what CI runs
npm run render:diagrams     # vector PDFs into docs/diagrams/rendered/
```

## How to read these

The series are ordered the way the manual is: **A** is what the system *is*,
**B** is how data gets into it, **C** is what it measures, **D** is how it runs.
One box is highlighted in each diagram — the thing that diagram is actually
about.

## A. Structure

### A1. System context — who emits, who collects

Four assistants, one port. Every supported surface speaks OpenTelemetry over
HTTP to the same endpoint, and the collector tells them apart from the payload
rather than from configuration. Cowork is the one that wants the full
`/v1/logs` path instead of the base endpoint; everything else takes `:4318`.
The GitHub Metrics API is the odd one out — the collector polls it, rather than
being pushed to, and what it returns is org-level usage, context beside the
score rather than the score itself.

```mermaid
flowchart TD
    vscode["VS Code<br/>Copilot Chat"]
    cli["GitHub Copilot CLI"]
    claude["Claude Code"]
    cowork["Claude Cowork<br/>desktop app"]
    ghapi["GitHub Copilot<br/>Metrics API"]

    collector["<b>CopilotScope collector</b><br/>OTLP ingest · scoring · REST API"]
    dash["Dashboard<br/>Blazor Server"]
    pg[("Postgres<br/>session snapshots")]
    prom["Prometheus / Grafana<br/>your existing stack"]

    vscode -- "OTLP/HTTP :4318" --> collector
    cli -- "OTLP/HTTP :4318" --> collector
    claude -- "OTLP/HTTP :4318" --> collector
    cowork -- "OTLP/HTTP /v1/logs" --> collector
    ghapi -. "daily poll, org level" .-> collector

    collector --> dash
    collector --> pg
    collector -- "GET /metrics" --> prom

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class collector star
```

<sub>Source: [`docs/diagrams/a1-system-context.mmd`](diagrams/a1-system-context.mmd)</sub>

### A2. Solution layout — what each project is for

The collector is the product; everything else is either a view onto it, a
development convenience, or opt-in. `ServiceDefaults` is a thin shared kernel —
self-instrumentation, health endpoints, service discovery, HTTP resilience —
and carries no domain logic. The two agent services and the three tools all
reference the collector for its domain types, which is why a change to a DTO
is a change to five projects.

```mermaid
flowchart LR
    subgraph runtime["Runs in every deployment"]
        collector["CopilotScope.Collector<br/><i>ingest · quality engine · insights · REST</i>"]
        dashboard["CopilotScope.Dashboard<br/><i>Blazor Server, zero JS deps</i>"]
        defaults["CopilotScope.ServiceDefaults<br/><i>OTel · health · resilience</i>"]
    end

    subgraph optin["Opt-in, behind a Compose profile"]
        judge["CopilotScope.JudgeAgent<br/><i>G-Eval · SPUR · RAGAS</i>"]
        forge["CopilotScope.AgentForge<br/><i>persona agents</i>"]
    end

    subgraph tools["One-shot tooling, one image"]
        seeder["Seeder<br/><i>demo data</i>"]
        importer["LogImporter<br/><i>transcripts</i>"]
        gen["TelemetryGen<br/><i>probe</i>"]
    end

    apphost["CopilotScope.AppHost<br/><i>Aspire, dev only</i>"]

    defaults --> collector
    defaults --> dashboard
    collector --> dashboard
    collector --> judge
    collector --> forge
    seeder --> collector
    importer --> collector
    gen --> collector
    apphost -.-> collector
    apphost -.-> dashboard

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class collector star
```

<sub>Source: [`docs/diagrams/a2-solution-layout.mmd`](diagrams/a2-solution-layout.mmd)</sub>

### A3. Ingest pipeline — what happens to one OTLP batch

Read this as the answer to "why is my dashboard empty". Every arrow that
leaves the happy path logs why. Two details matter more than they look:
redaction happens *before* anything aggregates, so privacy mode's guarantee
survives a database dump rather than depending on every read path; and the
decoded payload is bounded, because gzip reaches roughly 1000:1 and an
unbounded copy of a compressed body is a memory-exhaustion vector reachable
before authentication when no key is set.

```mermaid
flowchart TD
    post["POST /v1/traces | /v1/metrics | /v1/logs"]
    auth{"key configured<br/>and presented?"}
    ctype{"protobuf<br/>or JSON?"}
    decomp["Decompress<br/><i>gzip / deflate</i>"]
    bound["Bound the decoded payload<br/><i>64 MB ceiling</i>"]
    decode["Decode OTLP<br/><i>in-repo decoder, no OTel SDK</i>"]
    redact["Redact<br/><i>privacy mode, before anything aggregates</i>"]
    norm["Normalize dialects<br/><i>gen_ai.* · github.copilot.* · claude_code.* · cursor.*</i>"]
    agg["Aggregate into sessions<br/><i>keyed by conversation id</i>"]
    score["Score<br/><i>quality engine + insight analyzers</i>"]
    dirty["Mark dirty"]
    fwd["Forward raw OTLP<br/><i>optional upstream</i>"]

    post --> auth
    auth -- no --> r401["401, logged with the client hint"]
    auth -- yes --> ctype
    ctype -- neither --> r415["415, with a configuration hint"]
    ctype -- yes --> decomp --> bound --> decode --> redact --> norm --> agg --> score --> dirty
    norm -.-> fwd

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class score star
```

<sub>Source: [`docs/diagrams/a3-ingest-pipeline.mmd`](diagrams/a3-ingest-pipeline.mmd)</sub>

### A4. Session lifecycle — memory, Postgres, and eviction

The in-memory store is a bounded working set of the most recently active
sessions, not the extent of your history: a team churns past that cap in hours.
Eviction is therefore not deletion. Late telemetry for an evicted session
merges the stored snapshot back in before the next flush rather than
overwriting it, which is the difference between a session that grows and a
session that silently restarts.

```mermaid
stateDiagram-v2
    [*] --> Live: first OTLP batch
    Live --> Live: more telemetry
    Live --> Flushed: PersistenceWriter upsert<br/>(once per second)
    Flushed --> Live: more telemetry
    Flushed --> Evicted: bounded working set<br/>overflows
    Evicted --> Rehydrated: late telemetry, or<br/>GET /api/sessions/{id}
    Rehydrated --> Flushed: merged, not overwritten
    Flushed --> [*]: retention sweep<br/>(off by default)

    note right of Evicted
        Eviction is not deletion.
        The snapshot stays in Postgres
        and still resolves by id.
    end note
```

<sub>Source: [`docs/diagrams/a4-session-lifecycle.mmd`](diagrams/a4-session-lifecycle.mmd)</sub>

## B. Getting data in

### B1. Install and connect — the path to a first scored session

The whole install is two commands and one manual step per assistant that no
script can take. `connect` is the interesting box: it writes the settings file
the assistant itself reads, rather than exporting variables that live in one
terminal. `doctor` exists because the failure mode here is silent — a correctly
started stack and a correctly started assistant that never talk to each other
look exactly like a working install with nothing to show yet.

```mermaid
flowchart TD
    start(["curl -fsSL .../install.sh | sh"])
    docker{"Docker present<br/>and running?"}
    fetch["Fetch compose file<br/>and control script into ~/.copilotscope"]
    up["copilotscope up<br/><i>pull, start, wait for health</i>"]
    detect["Detect assistants<br/>on this machine"]
    connect["copilotscope connect &lt;assistant&gt;<br/><i>writes the settings file it reads</i>"]
    manual["Reload VS Code window<br/>restart Claude Desktop<br/>new terminal for Copilot CLI"]
    first(["First scored session<br/>appears on the dashboard"])
    doctor["copilotscope doctor<br/><i>names the broken link</i>"]

    start --> docker
    docker -- no --> stop["Stop with an actionable message"]
    docker -- yes --> fetch --> up --> detect --> connect --> manual --> first
    first -. "nothing arrives" .-> doctor
    doctor -.-> connect

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class connect star
```

<sub>Source: [`docs/diagrams/b1-install-and-connect.mmd`](diagrams/b1-install-and-connect.mmd)</sub>

### B2. Where each assistant's switch actually lives

This is the diagram that explains why setup used to be fiddly. The four
surfaces keep their configuration in three different kinds of place, and only
one of those places survives opening a new terminal. Claude Code is the best
case: an `env` block in its settings file applies to every terminal and every
project. Copilot CLI is the worst: environment variables are the only thing it
reads, so the configuration is as durable as the shell you set it in.

```mermaid
flowchart LR
    subgraph file["Written to a settings file — survives new terminals"]
        cc["Claude Code<br/><code>~/.claude/settings.json</code><br/>an <code>env</code> block"]
        vs["VS Code Copilot Chat<br/>user <code>settings.json</code><br/>reload the window"]
    end

    subgraph env["Environment variables — the only thing it reads"]
        cli["GitHub Copilot CLI<br/>shell rc, or Windows User scope"]
    end

    subgraph ui["The app's own settings UI — no file to write"]
        cw["Claude Cowork<br/>full <code>/v1/logs</code> path<br/>Team plan, admin, restart"]
    end

    collector["collector :4318"]

    cc --> collector
    vs --> collector
    cli --> collector
    cw --> collector

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class cc star
```

<sub>Source: [`docs/diagrams/b2-where-each-switch-lives.mmd`](diagrams/b2-where-each-switch-lives.mmd)</sub>

### B3. Import — scoring the history that already exists

Claude Code records every session to disk whether or not telemetry is
configured, and most developers never configure it. This path needs no client
setup at all. Two refusals in it are deliberate: a repository label is left
absent rather than guessed from a directory name, because a guess would invent
a second cohort for a project the collector already knows; and a session
already held from live telemetry is not overwritten, because the import
carries less signal and would quietly lower its score.

```mermaid
flowchart TD
    disk[("~/.claude/projects/**/*.jsonl<br/><i>written whether or not OTel is configured</i>")]
    parse["Parse transcript<br/><i>turns, tokens, models, tools, timings</i>"]
    repo{"git remote<br/>resolvable?"}
    label["Repository label<br/>normalized like outcome linkage"]
    nolabel["No repository label<br/><i>honest absence, not a guess</i>"]
    post["POST /api/import<br/><i>admin scope</i>"]
    exists{"session already held<br/>from live telemetry?"}
    refuse["Refused — the import carries<br/>less signal and would lower the score"]
    store["Stored, badged <b>imported</b><br/>lower confidence"]

    disk --> parse --> repo
    repo -- yes --> label --> post
    repo -- no --> nolabel --> post
    post --> exists
    exists -- yes --> refuse
    exists -- no --> store

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class store star
```

<sub>Source: [`docs/diagrams/b3-transcript-import.mmd`](diagrams/b3-transcript-import.mmd)</sub>

## C. The measurement

### C1. The composite score — six components, renormalized

The weights are published and the arithmetic is re-derivable by hand. The part
worth understanding is the renormalization: only components that actually have
data enter the composite. An earlier version let empty components contribute a
neutral prior, which made every session look like an 80. Confidence is exported
next to every score for the same reason — a 90 built on four samples means less
than a 70 built on forty.

```mermaid
flowchart TD
    subgraph components["Six components, each scored only if it has data"]
        rel["Reliability 0.25<br/><i>squared error-free rate</i>"]
        acc["Acceptance 0.20<br/><i>paired with edit survival</i>"]
        fri["Friction 0.20<br/><i>mean TFRA turn score</i>"]
        lat["Latency 0.15<br/><i>2 s attention, 8 s abandonment</i>"]
        fee["Feedback 0.10<br/><i>thumbs</i>"]
        eff["Efficiency 0.10<br/><i>token economics</i>"]
    end

    renorm["Renormalize over the components<br/>that actually reported"]
    score["Composite 0-100"]
    conf["Confidence<br/><i>coverage x sample ramp</i>"]
    grade["Grade: 85+ excellent · 70+ good<br/>55+ fair · 40+ poor"]

    rel --> renorm
    acc --> renorm
    fri --> renorm
    lat --> renorm
    fee --> renorm
    eff --> renorm
    renorm --> score --> grade
    renorm --> conf

    note["A session with no edit or feedback telemetry<br/>is scored on what it did produce,<br/>not pinned near a neutral prior."]
    conf -.-> note

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class renorm star
```

<sub>Source: [`docs/diagrams/c1-composite-score.mmd`](diagrams/c1-composite-score.mmd)</sub>

### C2. Turn analysis — where the conversation actually went wrong

A composite score says a session was poor. This says which turn, and why. Every
`invoke_agent` trace is one turn, scored for errors, latency against *this
session's own* median, and repair loops. The per-session median is the
load-bearing choice: it separates a slow turn from a slow model, which a global
threshold cannot do.

```mermaid
flowchart LR
    trace["One invoke_agent trace<br/>= one turn"]
    err{"LLM or tool<br/>error?"}
    slow{"TTFT above this<br/>session's own median?"}
    loop{"tool-call burst<br/>with failures?"}
    scoreturn["Turn friction score"]
    best["Best turn, with reasons"]
    worst["Worst turn, with reasons"]
    mean["Mean turn score<br/>= the friction component"]

    trace --> err --> slow --> loop --> scoreturn
    scoreturn --> best
    scoreturn --> worst
    scoreturn --> mean

    note["The median is per session, not global:<br/>a slow model is not a slow turn."]
    slow -.-> note

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class scoreturn star
```

<sub>Source: [`docs/diagrams/c2-turn-analysis.mmd`](diagrams/c2-turn-analysis.mmd)</sub>

### C3. Signal coverage — why scores compare within, not across

Every assistant lands in the same schema, and they do not report the same
signals. A Claude Code session has no thumbs feedback and no edit-survival
signal, so its 80 rests on less evidence than a VS Code session's 80. The
composite handles this honestly by renormalizing, and the confidence figure
reports what it had to work with. What it cannot do is make the two numbers
mean the same thing.

```mermaid
flowchart TD
    subgraph vscode["VS Code Copilot"]
        v1["tokens · calls · errors"]
        v2["TTFT"]
        v3["edit accept / reject"]
        v4["thumbs feedback"]
        v5["lines of code"]
    end

    subgraph claude["Claude Code"]
        c1["tokens · calls · errors"]
        c2["TTFT only with the tracing beta"]
        c3["edit decisions"]
        c5["lines of code"]
    end

    subgraph cowork["Claude Cowork"]
        w1["tokens · calls · errors"]
        w3["edit decisions"]
    end

    subgraph imported["Imported transcript"]
        i1["tokens · calls · tools · real timings"]
    end

    composite["Composite renormalizes<br/>over whatever arrived"]

    vscode --> composite
    claude --> composite
    cowork --> composite
    imported --> composite

    verdict["Comparable <b>within</b> an assistant.<br/>Directional <b>across</b> them.<br/>An 80 here is not an 80 there."]
    composite --> verdict

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class verdict star
```

<sub>Source: [`docs/diagrams/c3-signal-coverage.mmd`](diagrams/c3-signal-coverage.mmd)</sub>

## D. Running it

### D1. Deployment modes — and the one combination that is refused

The zero-credential default is not laxity; it is scoped. On loopback, with no
published database port, a key protects nothing and costs a configuration step
in every client — and that step is the one people get wrong. Publishing beyond
loopback is a different deployment, so the installer and the control script
refuse to do it without a key in the same command, and the collector says so
at startup if it finds itself exposed anyway.

```mermaid
flowchart TD
    subgraph local["One machine — the default"]
        l1["Ports bound to 127.0.0.1"]
        l2["Postgres publishes no port,<br/>trusts its unpublished socket"]
        l3["Ingest key empty = open mode"]
        l4["Nothing to declare"]
    end

    subgraph team["Shared host — all four together"]
        t1["COPILOTSCOPE_BIND=0.0.0.0"]
        t2["COPILOTSCOPE_API_KEY, split into<br/>ingest / read / admin scopes"]
        t3["POSTGRES_PASSWORD +<br/>scram-sha-256"]
        t4["Dashboard sign-in:<br/>viewer and admin"]
        t5["Privacy mode:<br/>pseudonyms, k-anonymity, audit log"]
    end

    guard{"bind beyond loopback<br/>without a key?"}
    refuse["Refused by install.sh --bind<br/>and copilotscope up --bind"]
    warn["Collector logs a startup warning<br/>if it sees one anyway"]

    local --> guard
    guard -- yes --> refuse
    guard -- yes --> warn
    guard -- no --> team

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class refuse star
```

<sub>Source: [`docs/diagrams/d1-deployment-modes.mmd`](diagrams/d1-deployment-modes.mmd)</sub>

### D2. CI gates — what blocks a merge, and what a tag releases

For most of this repository's life the images were built only when a release
tag was pushed, so a broken Dockerfile produced a partially-published release
rather than a red pull request. All five images are now built and started on
every pull request, which found two bugs that had made every image unbuildable
in its first run.

```mermaid
flowchart LR
    pr(["Pull request"])

    subgraph gates["What must be green"]
        build["build-and-test<br/><i>solution + xUnit</i>"]
        containers["containers<br/><i>five images built and started</i>"]
        scripts["scripts<br/><i>shell + PowerShell parse, shellcheck</i>"]
        compose["compose<br/><i>renders with no environment,<br/>nothing binds beyond loopback</i>"]
        docs["docs<br/><i>diagrams paired, translations coupled</i>"]
        leaks["gitleaks"]
    end

    merge(["Merge to master"])
    pages["GitHub Pages deploy"]
    tag(["Release tag"])
    ghcr["Five images to GHCR"]
    pdfs["Research + manual PDFs<br/>attached to the release"]

    pr --> gates --> merge
    merge --> pages
    tag --> ghcr
    tag --> pdfs

    classDef star fill:#fdf0d5,stroke:#c8860d,stroke-width:2px,color:#3d2b00
    class containers star
```

<sub>Source: [`docs/diagrams/d2-ci-gates.mmd`](diagrams/d2-ci-gates.mmd)</sub>

