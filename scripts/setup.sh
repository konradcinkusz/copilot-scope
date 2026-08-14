#!/usr/bin/env bash
# CopilotScope one-command setup wizard.
#
# Starts the collector stack, prints ready-to-paste client config (with the
# real endpoint/key filled in), optionally exports CLI env vars into THIS
# shell, and runs a TelemetryGen smoke test to confirm the pipeline works
# end to end.
#
# Usage:
#   ./scripts/setup.sh [options]                    # start + verify only
#   source ./scripts/setup.sh --copilot-cli          # also export env vars into this shell
#   source ./scripts/setup.sh --claude-code --capture-content
#
# Options:
#   --mode compose|aspire|skip-start   How to start CopilotScope (default: compose)
#   --copilot-cli                      Export GitHub Copilot CLI OTel env vars (needs: source)
#   --claude-code                      Export Claude Code OTel env vars (needs: source)
#   --capture-content                  Also request prompt/response content capture
#   --traces                           Claude Code only: also export the beta trace spans,
#                                       the one source of time-to-first-token (schema may change)
#   --endpoint URL                     OTLP endpoint (default: http://localhost:4318)
#   --api-key KEY                      x-api-key for ingest auth
#                                       (in --mode compose, a random key is generated into
#                                        the gitignored .env and reused on re-runs)
#   --persist                          Also append the CLI env vars to your shell rc file
#                                       (~/.zshrc or ~/.bashrc, auto-detected) so new terminals
#                                       pick them up without re-sourcing this script.
#                                       Requires --copilot-cli and/or --claude-code. Safe to
#                                       re-run — replaces its own marked block, doesn't duplicate.
#   --rc-file PATH                     Override the rc file used by --persist
#   --skip-verify                      Skip the TelemetryGen smoke test
#   --health-timeout SECONDS           How long to wait for the collector (default: 60)
#   -h, --help                         Show this help
#
# Examples:
#   ./scripts/setup.sh
#   source ./scripts/setup.sh --copilot-cli --capture-content
#   source ./scripts/setup.sh --copilot-cli --persist
#   ./scripts/setup.sh --mode aspire --skip-verify
#   ./scripts/setup.sh --mode skip-start --endpoint https://copilotscope.example.com --api-key "$SCOPE_KEY"
#
# Note: this script does NOT `set -e` — it is meant to be sourced, and that
# would leak shell options into your interactive session.

SCRIPT_PATH="${BASH_SOURCE[0]:-$0}"
SCRIPT_DIR="$(cd "$(dirname "$SCRIPT_PATH")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Generates a random hex secret (no committed default key — see the review's S6).
_gen_secret() {
    if command -v openssl >/dev/null 2>&1; then openssl rand -hex 24
    else head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n'; fi
}

(return 0 2>/dev/null)
IS_SOURCED=$?   # 0 = sourced, non-zero = executed directly

ORIG_ARGS=("$@")

MODE="compose"
COPILOT_CLI=""
CLAUDE_CODE=""
CAPTURE=""
TRACES=""
ENDPOINT="http://localhost:4318"
API_KEY=""
API_KEY_SET=""
PERSIST=""
RC_FILE=""
SKIP_VERIFY=""
HEALTH_TIMEOUT=60

_die() {
    echo "$1" >&2
    if [ "$IS_SOURCED" -eq 0 ]; then return 1; else exit 1; fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode)            MODE="$2"; shift 2 ;;
        --copilot-cli)      COPILOT_CLI="true"; shift ;;
        --claude-code)      CLAUDE_CODE="true"; shift ;;
        --capture-content)  CAPTURE="true"; shift ;;
        --traces)           TRACES="true"; shift ;;
        --endpoint)         ENDPOINT="$2"; shift 2 ;;
        --api-key)          API_KEY="$2"; API_KEY_SET="true"; shift 2 ;;
        --persist)          PERSIST="true"; shift ;;
        --rc-file)          RC_FILE="$2"; shift 2 ;;
        --skip-verify)      SKIP_VERIFY="true"; shift ;;
        --health-timeout)   HEALTH_TIMEOUT="$2"; shift 2 ;;
        -h|--help)
            awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$SCRIPT_PATH"
            if [ "$IS_SOURCED" -eq 0 ]; then return 0; else exit 0; fi
            ;;
        *) echo "Unknown option: $1" >&2; shift ;;
    esac
done

case "$MODE" in
    compose|aspire|skip-start) ;;
    *) _die "Invalid --mode '$MODE' (expected: compose, aspire, skip-start)" || return 1 ;;
esac

if [ "$MODE" = "compose" ]; then
    # Compose requires COPILOTSCOPE_API_KEY and POSTGRES_PASSWORD (no default).
    # Reuse an existing .env so re-runs stay stable; otherwise generate secrets
    # and write them to the gitignored .env that docker compose reads.
    ENV_FILE="$REPO_ROOT/.env"
    _env_val() { [ -f "$ENV_FILE" ] && sed -n "s/^$1=//p" "$ENV_FILE" | head -1; }
    GEN_KEY="$(_env_val COPILOTSCOPE_API_KEY)"; [ -n "$GEN_KEY" ] || GEN_KEY="$(_gen_secret)"
    GEN_PW="$(_env_val POSTGRES_PASSWORD)";     [ -n "$GEN_PW" ]  || GEN_PW="$(_gen_secret)"
    printf 'COPILOTSCOPE_API_KEY=%s\nPOSTGRES_PASSWORD=%s\n' "$GEN_KEY" "$GEN_PW" > "$ENV_FILE"
    echo "Wrote generated secrets to $ENV_FILE (gitignored)."
    [ -n "$API_KEY_SET" ] || API_KEY="$GEN_KEY"
fi

if [ -n "$PERSIST" ] && [ -z "$COPILOT_CLI" ] && [ -z "$CLAUDE_CODE" ]; then
    echo "--persist has no effect without --copilot-cli and/or --claude-code." >&2
fi

if ! command -v curl >/dev/null 2>&1; then
    _die "curl is required by this script." || return 1
fi

_default_rc_file() {
    case "$(basename "${SHELL:-/bin/bash}")" in
        zsh) echo "$HOME/.zshrc" ;;
        *)   echo "$HOME/.bashrc" ;;
    esac
}

# Replaces the marked block for $2 in rc file $1 with $3 (idempotent — safe to re-run).
_persist_block() {
    local rc="$1" marker="$2" content="$3"
    local begin="# >>> CopilotScope ($marker) >>>"
    local end="# <<< CopilotScope ($marker) <<<"
    touch "$rc"
    awk -v b="$begin" -v e="$end" '
        $0==b {skip=1}
        skip!=1 {print}
        $0==e {skip=0}
    ' "$rc" > "$rc.copilotscope.tmp" && mv "$rc.copilotscope.tmp" "$rc"
    {
        echo ""
        echo "$begin"
        printf '%s\n' "$content" | grep -v '^$'
        echo "$end"
    } >> "$rc"
}

echo "=== CopilotScope setup ==="
echo ""

# ------------------------------------------------------------- 1. start stack
case "$MODE" in
    compose)
        if ! command -v docker >/dev/null 2>&1; then
            _die "docker is required for --mode compose." || return 1
        fi
        echo "Starting CopilotScope via Docker Compose..."
        if ! docker compose -f "$REPO_ROOT/docker-compose.yml" up --build -d; then
            _die "docker compose failed to start. See output above." || return 1
        fi
        ;;
    aspire)
        if ! command -v dotnet >/dev/null 2>&1; then
            _die "dotnet SDK is required for --mode aspire." || return 1
        fi
        ASPIRE_LOG="$(mktemp -t copilotscope-aspire.XXXXXX.log)"
        echo "Starting CopilotScope via .NET Aspire (background, log: $ASPIRE_LOG)..."
        nohup dotnet run --project "$REPO_ROOT/src/CopilotScope.AppHost" >"$ASPIRE_LOG" 2>&1 &
        disown 2>/dev/null || true
        ;;
    skip-start)
        echo "Skipping start — assuming CopilotScope is already running at $ENDPOINT"
        ;;
esac
echo ""

# ------------------------------------------------------------ 2. wait healthy
echo -n "Waiting for the collector at $ENDPOINT "
waited=0
until curl -fsS "$ENDPOINT/api/health" >/dev/null 2>&1; do
    if [ "$waited" -ge "$HEALTH_TIMEOUT" ]; then
        echo ""
        echo "Timed out after ${HEALTH_TIMEOUT}s waiting for $ENDPOINT/api/health" >&2
        echo "Check logs (docker compose logs collector, or the Aspire log above)." >&2
        echo "See docs/TUTORIAL.md section 8 for troubleshooting." >&2
        _die "" || return 1
    fi
    echo -n "."
    sleep 2
    waited=$((waited + 2))
done
echo " up."
curl -fsS "$ENDPOINT/api/health"
echo ""
echo ""

# --------------------------------------------------- 3. configure CLI clients
if [ -n "$COPILOT_CLI" ] || [ -n "$CLAUDE_CODE" ]; then
    if [ "$IS_SOURCED" -ne 0 ]; then
        echo "WARNING: this script was executed, not sourced — env vars set below"
        echo "will NOT reach your shell. Re-run as:"
        echo "  source ./scripts/setup.sh ${ORIG_ARGS[*]}"
        echo ""
    fi
fi

if [ -n "$COPILOT_CLI" ]; then
    export COPILOT_OTEL_ENABLED=true
    export COPILOT_OTEL_EXPORTER_TYPE=otlp-http
    export OTEL_EXPORTER_OTLP_ENDPOINT="$ENDPOINT"
    export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
    export OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf
    export OTEL_EXPORTER_OTLP_METRICS_PROTOCOL=http/protobuf
    export OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=http/protobuf
    if [ -n "$CAPTURE" ]; then
        export OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true
    fi
    if [ -n "$API_KEY" ]; then
        export OTEL_EXPORTER_OTLP_HEADERS="x-api-key=$API_KEY"
    fi
    echo "Copilot CLI OTel env vars exported into this shell. Run 'copilot' from THIS terminal."

    if [ -n "$PERSIST" ]; then
        target_rc="${RC_FILE:-$(_default_rc_file)}"
        block=$(cat <<BLOCK
export COPILOT_OTEL_ENABLED=true
export COPILOT_OTEL_EXPORTER_TYPE=otlp-http
export OTEL_EXPORTER_OTLP_ENDPOINT="$ENDPOINT"
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_METRICS_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=http/protobuf
$( [ -n "$CAPTURE" ] && echo 'export OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true' )
$( [ -n "$API_KEY" ] && echo "export OTEL_EXPORTER_OTLP_HEADERS=\"x-api-key=$API_KEY\"" )
BLOCK
)
        _persist_block "$target_rc" "copilot-cli" "$block"
        echo "Persisted to $target_rc — new terminals will have it automatically"
        echo "(this one already does; run 'source $target_rc' elsewhere, or open a new terminal)."
        [ -n "$API_KEY" ] && echo "Note: this stores the API key in plaintext in $target_rc."
    fi
    echo ""
fi

if [ -n "$CLAUDE_CODE" ]; then
    claude_args=(--endpoint "$ENDPOINT")
    [ -n "$CAPTURE" ] && claude_args+=(--capture)
    [ -n "$TRACES" ] && claude_args+=(--traces)
    [ -n "$API_KEY" ] && claude_args+=(--api-key "$API_KEY")
    source "$SCRIPT_DIR/Enable-ClaudeCodeOtel.sh" "${claude_args[@]}"

    if [ -n "$PERSIST" ]; then
        target_rc="${RC_FILE:-$(_default_rc_file)}"
        block=$(cat <<BLOCK
export CLAUDE_CODE_ENABLE_TELEMETRY=1
export OTEL_METRICS_EXPORTER=otlp
export OTEL_LOGS_EXPORTER=otlp
export OTEL_EXPORTER_OTLP_ENDPOINT="$ENDPOINT"
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_METRICS_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=http/protobuf
export OTEL_METRIC_EXPORT_INTERVAL=10000
export OTEL_LOGS_EXPORT_INTERVAL=5000
export OTEL_RESOURCE_ATTRIBUTES=service.name=claude-code
$( [ -n "$TRACES" ] && printf '%s\n' 'export CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1' 'export OTEL_TRACES_EXPORTER=otlp' 'export OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf' )
$( [ -n "$CAPTURE" ] && printf '%s\n' 'export OTEL_LOG_USER_PROMPTS=1' 'export OTEL_LOG_ASSISTANT_RESPONSES=1' 'export OTEL_LOG_TOOL_DETAILS=1' )
$( [ -n "$API_KEY" ] && echo "export OTEL_EXPORTER_OTLP_HEADERS=\"x-api-key=$API_KEY\"" )
BLOCK
)
        _persist_block "$target_rc" "claude-code" "$block"
        echo "Persisted to $target_rc — new terminals will have it automatically"
        echo "(this one already does; run 'source $target_rc' elsewhere, or open a new terminal)."
        [ -n "$API_KEY" ] && echo "Note: this stores the API key in plaintext in $target_rc."
    fi
    echo ""
fi

# --------------------------------------------------------- 4. VS Code snippet
echo "VS Code — Settings JSON (Ctrl+Shift+P -> \"Preferences: Open User Settings (JSON)\"):"
echo "{"
echo "  \"github.copilot.chat.otel.enabled\": true,"
echo "  \"github.copilot.chat.otel.otlpEndpoint\": \"$ENDPOINT\","
if [ -n "$CAPTURE" ]; then
    echo "  \"github.copilot.chat.otel.exporterType\": \"otlp-http\","
    echo "  \"github.copilot.chat.otel.captureContent\": true"
else
    echo "  \"github.copilot.chat.otel.exporterType\": \"otlp-http\""
fi
echo "}"
echo "Then: Ctrl+Shift+P -> \"Developer: Reload Window\" (required — settings are read at startup)."
if [ -n "$API_KEY" ]; then
    echo ""
    echo "This endpoint requires an API key. Export before launching VS Code from a terminal:"
    echo "  export OTEL_EXPORTER_OTLP_HEADERS=\"x-api-key=$API_KEY\""
fi
echo ""

# --------------------------------------------------------------- 5. smoke test
if [ -z "$SKIP_VERIFY" ]; then
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "dotnet not found — skipping smoke test. Open the dashboard and chat with a Copilot"
        echo "client to verify manually, or install the .NET 8 SDK and re-run without --skip-verify."
    else
        PROBE_ID="setup-probe-$(date +%s)"
        PROBE_LOG="$(mktemp -t copilotscope-telemetrygen.XXXXXX.log)"
        echo "Running smoke test (TelemetryGen) — conversation '$PROBE_ID'..."
        (COPILOTSCOPE_API_KEY="$API_KEY" dotnet run --project "$REPO_ROOT/tools/CopilotScope.TelemetryGen" -- "$ENDPOINT" "$PROBE_ID") >"$PROBE_LOG" 2>&1
        vwaited=0
        ok=""
        until curl -fsS "$ENDPOINT/api/sessions/$PROBE_ID" >/dev/null 2>&1; do
            if [ "$vwaited" -ge 15 ]; then
                break
            fi
            sleep 1
            vwaited=$((vwaited + 1))
        done
        if curl -fsS "$ENDPOINT/api/sessions/$PROBE_ID" >/dev/null 2>&1; then
            echo "Smoke test OK — '$PROBE_ID' is visible via the collector API. CopilotScope is healthy end to end."
        else
            echo "Probe session did not appear within 15s. See $PROBE_LOG and docs/TUTORIAL.md section 8." >&2
        fi
    fi
    echo ""
fi

echo "=== Done ==="
echo "Dashboard: for --mode compose, http://localhost:5200 ; for --mode aspire, see the Aspire dashboard URL above."
