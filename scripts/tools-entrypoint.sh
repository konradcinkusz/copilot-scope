#!/bin/sh
# Entrypoint for the copilotscope-tools image: the three things a user needs on a
# first run, without a .NET SDK or a clone of this repository.
#
#   import [flags]        score the Claude Code transcripts mounted at /transcripts
#   demo   [quick|demo]   load a demo dataset into a running collector
#   probe  [id]           send one real OTLP/HTTP session through the ingest path
#
# The collector URL comes from $COPILOTSCOPE_COLLECTOR and the key, when the
# deployment has one, from $COPILOTSCOPE_API_KEY. Both are set by the compose
# service, so the commands above take no arguments in normal use.
set -eu

COLLECTOR="${COPILOTSCOPE_COLLECTOR:-http://localhost:4318}"
TRANSCRIPTS="${COPILOTSCOPE_TRANSCRIPTS:-/transcripts}"

usage() {
    cat <<USAGE
copilotscope-tools — first-run tooling for a running CopilotScope collector.

  import [flags]        Import Claude Code transcripts from $TRANSCRIPTS.
                        Flags pass through: --dry-run, --include-content,
                        --since <date>, --root <dir>, --collector <url>.
  demo [quick|demo]     Seed demo sessions (default: quick). Fabricated data,
                        badged "demo" in the dashboard.
  probe [id]            Send one simulated session over real OTLP/HTTP protobuf.
                        Exercises ingest, decoding, scoring and persistence.
  help                  This text.

Collector: $COLLECTOR
USAGE
}

# The importer takes the first occurrence of a repeated flag, so a caller's own
# --root / --collector must not be shadowed by the defaults added here.
has_flag() {
    needle="$1"
    shift
    for arg in "$@"; do
        if [ "$arg" = "$needle" ]; then return 0; fi
    done
    return 1
}

command="${1:-help}"
if [ "$#" -gt 0 ]; then shift; fi

case "$command" in
    import)
        if ! has_flag --collector "$@"; then set -- --collector "$COLLECTOR" "$@"; fi
        if ! has_flag --root "$@"; then set -- --root "$TRANSCRIPTS" "$@"; fi
        exec dotnet /app/importer/copilotscope-import.dll "$@"
        ;;
    demo | seed)
        profile="${1:-quick}"
        if [ "$#" -gt 0 ]; then shift; fi
        exec dotnet /app/seeder/CopilotScope.Seeder.dll "$profile" "$COLLECTOR" "$@"
        ;;
    probe)
        id="${1:-probe-$(date +%s)}"
        if [ "$#" -gt 0 ]; then shift; fi
        exec dotnet /app/telemetrygen/CopilotScope.TelemetryGen.dll "$COLLECTOR" "$id" "$@"
        ;;
    help | --help | -h)
        usage
        ;;
    *)
        echo "unknown command: $command" >&2
        echo >&2
        usage >&2
        exit 2
        ;;
esac
