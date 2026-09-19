#!/bin/sh
# CopilotScope installer — Linux, macOS, WSL, Git Bash.
#
#   curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh
#
# What it does, in order: checks Docker, downloads the compose file and the
# `copilotscope` control script into ~/.copilotscope, starts the stack, waits
# until the collector answers, and offers to point the assistants it finds at it.
#
# What it does NOT do: generate secrets you then have to keep track of, ask you
# to edit a JSON file by hand, or leave environment variables that only exist in
# one terminal. A local collector binds to 127.0.0.1 and needs no key, and the
# assistant configuration is written to the settings file each tool reads at
# startup.
#
# Options (pass them after `| sh -s --`):
#   -y, --yes          configure every assistant found, without asking
#   --no-connect       start the stack only, configure nothing
#   --no-start         install the files, start nothing
#   --capture          also export prompt/response text (sensitive; off by default)
#   --bind ADDR        publish beyond loopback (requires --api-key)
#   --api-key KEY      ingest key for a shared deployment
#   --tag TAG          pin the image tag (default: latest)
#   --dir PATH         install location (default: ~/.copilotscope)
#
# Example:
#   curl -fsSL .../install.sh | sh -s -- --yes --capture
set -eu

REPO_RAW="${COPILOTSCOPE_REPO_RAW:-https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master}"
INSTALL_DIR="${COPILOTSCOPE_HOME:-$HOME/.copilotscope}"
ASSUME_YES=""
DO_CONNECT="true"
DO_START="true"
CAPTURE=""
BIND=""
API_KEY=""
TAG=""

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_RESET=$(printf '\033[0m'); C_BOLD=$(printf '\033[1m'); C_DIM=$(printf '\033[2m')
    C_GREEN=$(printf '\033[32m'); C_YELLOW=$(printf '\033[33m'); C_RED=$(printf '\033[31m')
else
    C_RESET=""; C_BOLD=""; C_DIM=""; C_GREEN=""; C_YELLOW=""; C_RED=""
fi
say()  { printf '%s\n' "$*"; }
step() { printf '\n%s%s%s\n' "$C_BOLD" "$*" "$C_RESET"; }
ok()   { printf '  %s✓%s %s\n' "$C_GREEN" "$C_RESET" "$*"; }
warn() { printf '  %s!%s %s\n' "$C_YELLOW" "$C_RESET" "$*"; }
info() { printf '  %s-%s %s\n' "$C_DIM" "$C_RESET" "$*"; }
die()  { printf '\n%serror:%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        -y|--yes) ASSUME_YES="true"; shift ;;
        --no-connect) DO_CONNECT=""; shift ;;
        --no-start) DO_START=""; shift ;;
        --capture|--capture-content) CAPTURE="true"; shift ;;
        --bind) BIND="${2:?--bind needs an address}"; shift 2 ;;
        --api-key) API_KEY="${2:?--api-key needs a value}"; shift 2 ;;
        --tag) TAG="${2:?--tag needs a value}"; shift 2 ;;
        --dir) INSTALL_DIR="${2:?--dir needs a path}"; shift 2 ;;
        -h|--help)
            # Printed inline rather than read back out of this file: piped through
            # `sh`, the script has no path of its own to read.
            cat <<'USAGE'
CopilotScope installer.

  curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh

Checks Docker, installs into ~/.copilotscope, starts the stack, and offers to
point the assistants it finds at it. A local collector binds to 127.0.0.1 and
needs no key, so there is nothing to declare.

  -y, --yes        configure every assistant found, without asking
  --no-connect     start the stack only, configure nothing
  --no-start       install the files, start nothing
  --capture        also export prompt/response text (sensitive; off by default)
  --bind ADDR      publish beyond loopback (requires --api-key)
  --api-key KEY    ingest key for a shared deployment
  --tag TAG        pin the image tag (default: latest)
  --dir PATH       install location (default: ~/.copilotscope)

Pass options after `| sh -s --`, e.g.  ... | sh -s -- --yes --capture
USAGE
            exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

say ""
say "${C_BOLD}CopilotScope${C_RESET} — quality scoring for AI coding-assistant sessions."
say "${C_DIM}Runs on this machine. Nothing is sent anywhere.${C_RESET}"

# ------------------------------------------------------------- prerequisites

step "Checking prerequisites"
command -v curl >/dev/null 2>&1 || die "curl is required."
command -v docker >/dev/null 2>&1 || die "Docker is required — https://docs.docker.com/get-docker/"
docker compose version >/dev/null 2>&1 \
    || die "this Docker has no 'compose' subcommand. Update Docker Desktop, or install the compose plugin."
if [ -n "$DO_START" ]; then
    docker info >/dev/null 2>&1 || die "the Docker daemon is not running. Start Docker Desktop (or dockerd) and re-run."
fi
ok "docker $(docker version --format '{{.Client.Version}}' 2>/dev/null || echo present)"

# A collector published beyond this machine without a key would serve transcripts
# and accept deletes from anyone who can reach the port. Refuse the combination
# here rather than printing a warning nobody reads.
if [ -n "$BIND" ] && [ "$BIND" != "127.0.0.1" ] && [ "$BIND" != "localhost" ] && [ -z "$API_KEY" ]; then
    die "--bind $BIND publishes the collector beyond this machine, so it needs --api-key.
       Generate one with: openssl rand -hex 24
       See SECURITY.md for what the key gates."
fi

# ------------------------------------------------------------------ download

step "Installing into $INSTALL_DIR"
mkdir -p "$INSTALL_DIR/bin"

fetch() {
    # $1 = path in the repository, $2 = destination
    if ! curl -fsSL "$REPO_RAW/$1" -o "$2.part"; then
        rm -f "$2.part"
        die "could not download $1 from $REPO_RAW"
    fi
    mv "$2.part" "$2"
}

fetch docker-compose.ghcr.yml "$INSTALL_DIR/docker-compose.yml"
ok "compose file"
fetch scripts/copilotscope "$INSTALL_DIR/bin/copilotscope"
chmod +x "$INSTALL_DIR/bin/copilotscope"
ok "control script"

CLI="$INSTALL_DIR/bin/copilotscope"
export COPILOTSCOPE_HOME="$INSTALL_DIR"

# ---------------------------------------------------------------------- .env

ENV_FILE="$INSTALL_DIR/.env"
touch "$ENV_FILE"
chmod 600 "$ENV_FILE" 2>/dev/null || true

set_env() {
    name="$1"; value="$2"
    if grep -q "^${name}=" "$ENV_FILE" 2>/dev/null; then
        grep -v "^${name}=" "$ENV_FILE" > "$ENV_FILE.tmp" || true
        mv "$ENV_FILE.tmp" "$ENV_FILE"
    fi
    printf '%s=%s\n' "$name" "$value" >> "$ENV_FILE"
}

# Claude Code records every session here whether or not telemetry is configured,
# so this is what makes `copilotscope import` work with no client setup at all.
CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
if [ -d "$CLAUDE_DIR/projects" ]; then
    set_env CLAUDE_TRANSCRIPTS "$CLAUDE_DIR/projects"
    ok "found Claude Code transcripts in $CLAUDE_DIR/projects"
fi
[ -n "$BIND" ] && set_env COPILOTSCOPE_BIND "$BIND"
[ -n "$API_KEY" ] && set_env COPILOTSCOPE_API_KEY "$API_KEY"
[ -n "$TAG" ] && set_env COPILOTSCOPE_TAG "$TAG"

# --------------------------------------------------------------- PATH wiring

LINKED=""
for dir in "$HOME/.local/bin" "/usr/local/bin"; do
    case ":$PATH:" in
        *":$dir:"*)
            if [ -d "$dir" ] && [ -w "$dir" ]; then
                ln -sf "$CLI" "$dir/copilotscope" 2>/dev/null && LINKED="$dir/copilotscope" && break
            fi
            ;;
    esac
done
if [ -z "$LINKED" ] && [ -d "$HOME/.local/bin" ] && [ -w "$HOME/.local/bin" ]; then
    ln -sf "$CLI" "$HOME/.local/bin/copilotscope" 2>/dev/null && LINKED="$HOME/.local/bin/copilotscope"
fi
if [ -n "$LINKED" ]; then
    ok "copilotscope → $LINKED"
else
    warn "$INSTALL_DIR/bin is not on your PATH."
    info "Add it: echo 'export PATH=\"\$PATH:$INSTALL_DIR/bin\"' >> ~/.bashrc"
    info "Until then, call it by its full path: $CLI"
fi

# -------------------------------------------------------------------- start

if [ -n "$DO_START" ]; then
    step "Starting CopilotScope"
    "$CLI" up || die "the stack did not come up. Inspect it with: $CLI logs collector"
else
    info "skipping start (--no-start). Start it later with: copilotscope up"
fi

# ------------------------------------------------------------------ connect

# Prompting is only possible when a terminal is reachable: with `curl | sh` the
# script's own stdin is the pipe, so questions go to /dev/tty or are not asked.
ask() {
    prompt="$1"
    [ -n "$ASSUME_YES" ] && return 0
    if [ -r /dev/tty ]; then
        printf '  %s [Y/n] ' "$prompt" > /dev/tty
        read -r reply < /dev/tty || reply=""
        case "$reply" in [nN]*) return 1 ;; *) return 0 ;; esac
    fi
    return 2   # nothing to ask with — the caller prints the command instead
}

CONNECT_ARGS=""
[ -n "$CAPTURE" ] && CONNECT_ARGS="--capture"

connect_if_wanted() {
    target="$1"; label="$2"
    set +e
    ask "Point $label at CopilotScope?"
    answer=$?
    set -e
    case "$answer" in
        0) say ""; "$CLI" connect "$target" $CONNECT_ARGS || true ;;
        1) info "skipped — run later: copilotscope connect $target" ;;
        *) info "run: copilotscope connect $target" ;;
    esac
}

if [ -n "$DO_CONNECT" ] && [ -n "$DO_START" ]; then
    step "Connecting your assistants"
    FOUND=""

    if [ -d "$CLAUDE_DIR" ] || command -v claude >/dev/null 2>&1; then
        FOUND="true"
        connect_if_wanted claude-code "Claude Code"
    fi

    VSCODE_DIR=""
    for candidate in \
        "$HOME/Library/Application Support/Code/User" \
        "${XDG_CONFIG_HOME:-$HOME/.config}/Code/User" \
        "${APPDATA:-/nonexistent}/Code/User"; do
        [ -d "$candidate" ] && VSCODE_DIR="$candidate" && break
    done
    if [ -n "$VSCODE_DIR" ] || command -v code >/dev/null 2>&1; then
        FOUND="true"
        say ""
        connect_if_wanted vscode "VS Code Copilot Chat"
    fi

    if command -v copilot >/dev/null 2>&1; then
        FOUND="true"
        say ""
        connect_if_wanted copilot-cli "GitHub Copilot CLI"
    fi

    if [ -z "$FOUND" ]; then
        info "No assistant found on this machine yet."
        info "When you have one: copilotscope connect claude-code | vscode | copilot-cli"
    fi
fi

# ------------------------------------------------------------------ first data

if [ -n "$DO_START" ]; then
    TRANSCRIPT_COUNT=0
    if [ -d "$CLAUDE_DIR/projects" ]; then
        TRANSCRIPT_COUNT=$(find "$CLAUDE_DIR/projects" -name '*.jsonl' -type f 2>/dev/null | wc -l | tr -d ' ')
    fi

    step "Done"
    ok "Dashboard    $("$CLI" url 2>/dev/null || echo 'http://localhost:5200')"
    ok "OTLP ingest  http://localhost:4318"
    say ""
    if [ "${TRANSCRIPT_COUNT:-0}" -gt 0 ]; then
        say "  You already have ${TRANSCRIPT_COUNT} Claude Code session(s) on disk. Score them now,"
        say "  with no telemetry configuration at all:"
        say ""
        say "      copilotscope import"
        say ""
    fi
    say "  ${C_DIM}copilotscope doctor${C_RESET}   check the wiring end to end"
    say "  ${C_DIM}copilotscope demo${C_RESET}     load demo sessions to look around"
    say "  ${C_DIM}copilotscope probe${C_RESET}    verify ingest without an assistant"
    say ""
fi
