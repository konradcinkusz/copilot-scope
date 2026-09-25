#!/bin/sh
# CopilotScope installer — Linux, macOS, WSL.
#
#   curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh
#
# Installs the native `copilotscope` binary (ADR-004): one self-contained program, with no
# Docker, no .NET and no python or node to install. In order: it picks the release archive for
# this platform, checks it against the release's SHA256SUMS and refuses it on any mismatch,
# installs it into ~/.copilotscope/app, puts `copilotscope` on the PATH, and offers to point the
# assistants it finds at it — showing each change first and making it only on a yes. Then
# `copilotscope` starts it.
#
# For a team or a shared server, `--docker` installs the Docker Compose stack instead, as
# before: it checks Docker, downloads the compose file and the control script, starts the
# stack and waits until the collector answers.
#
# Options (pass them after `| sh -s --`):
#   -y, --yes          configure every assistant found, without asking
#   --no-connect       install only, configure nothing
#   --capture          also export prompt/response text (sensitive; off by default)
#   --version VER      a specific release, e.g. v1.1.0 (default: the latest)
#   --dir PATH         install location (default: ~/.copilotscope)
#   --docker           the Docker Compose stack instead; with it:
#     --no-start       install the files, start nothing
#     --bind ADDR      publish beyond loopback (requires --api-key)
#     --api-key KEY    ingest key for a shared deployment
#     --tag TAG        pin the image tag (default: latest)
#
# Example:
#   curl -fsSL .../install.sh | sh -s -- --yes --capture
set -eu

REPO="konradcinkusz/copilot-scope"
REPO_RAW="${COPILOTSCOPE_REPO_RAW:-https://raw.githubusercontent.com/$REPO/master}"
INSTALL_DIR="${COPILOTSCOPE_HOME:-$HOME/.copilotscope}"
DOCKER=""
VERSION=""
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
        --docker) DOCKER="true"; shift ;;
        --version) VERSION="${2:?--version needs a release, e.g. v1.1.0}"; shift 2 ;;
        -h|--help)
            # Printed inline rather than read back out of this file: piped through
            # `sh`, the script has no path of its own to read.
            cat <<'USAGE'
CopilotScope installer.

  curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh

Installs the native `copilotscope` binary into ~/.copilotscope, checked against the
release's SHA256SUMS, puts it on the PATH, and offers to point the assistants it finds
at it. Then `copilotscope` starts it. Nothing else to install, and nothing leaves the
machine.

  -y, --yes        configure every assistant found, without asking
  --no-connect     install only, configure nothing
  --capture        also export prompt/response text (sensitive; off by default)
  --version VER    a specific release, e.g. v1.1.0 (default: the latest)
  --dir PATH       install location (default: ~/.copilotscope)
  --docker         the Docker Compose stack instead, for a team or a shared server:
    --no-start     install the files, start nothing
    --bind ADDR    publish beyond loopback (requires --api-key)
    --api-key KEY  ingest key for a shared deployment
    --tag TAG      pin the image tag (default: latest)

Pass options after `| sh -s --`, e.g.  ... | sh -s -- --yes --capture
USAGE
            exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

say ""
say "${C_BOLD}CopilotScope${C_RESET} — quality scoring for AI coding-assistant sessions."
say "${C_DIM}Runs on this machine. Nothing is sent anywhere.${C_RESET}"

# ------------------------------------------------------------------ native

# The release archive's name for this machine, as scripts/package-native.sh names them.
platform() {
    os="$(uname -s 2>/dev/null || echo unknown)"
    arch="$(uname -m 2>/dev/null || echo unknown)"
    case "$os" in
        Linux)
            os=linux
            # The Linux builds link against glibc; Alpine's musl cannot load them.
            if ldd --version 2>&1 | grep -qi musl; then
                printf 'error: there is no native build for musl-based Linux (Alpine). Use --docker.\n' >&2
                return 1
            fi
            ;;
        Darwin)
            os=osx
            # A shell running under Rosetta reports x86_64 on Apple silicon.
            if [ "$arch" = "x86_64" ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null)" = "1" ]; then arch=arm64; fi
            ;;
        MINGW*|MSYS*|CYGWIN*)
            printf 'error: on Windows, install from PowerShell:\n  irm https://raw.githubusercontent.com/%s/master/install.ps1 | iex\n' "$REPO" >&2
            return 1
            ;;
        *)
            printf 'error: there is no native build for %s. Use --docker.\n' "$os" >&2
            return 1
            ;;
    esac
    case "$arch" in
        x86_64|amd64) arch=x64 ;;
        arm64|aarch64) arch=arm64 ;;
        *) printf 'error: there is no native build for %s. Use --docker.\n' "$arch" >&2; return 1 ;;
    esac
    printf '%s-%s' "$os" "$arch"
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | awk '{print $1}'
    else die "neither sha256sum nor shasum is available, so the download cannot be checked."
    fi
}

# Puts `copilotscope` on the PATH: a link in the first writable PATH directory of the usual two.
# The binary finds its dashboard files beside where the link points, not beside the link.
link_onto_path() {
    target="$1"
    linked=""
    for dir in "$HOME/.local/bin" "/usr/local/bin"; do
        case ":$PATH:" in
            *":$dir:"*)
                if [ -d "$dir" ] && [ -w "$dir" ]; then
                    ln -sf "$target" "$dir/copilotscope" 2>/dev/null && linked="$dir/copilotscope" && break
                fi
                ;;
        esac
    done
    if [ -z "$linked" ]; then
        mkdir -p "$HOME/.local/bin" 2>/dev/null || true
        if [ -w "$HOME/.local/bin" ] && ln -sf "$target" "$HOME/.local/bin/copilotscope" 2>/dev/null; then
            linked="$HOME/.local/bin/copilotscope"
            case ":$PATH:" in
                *":$HOME/.local/bin:"*) ;;
                *) warn "$HOME/.local/bin is not on your PATH yet."
                   info "Add it: echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.profile" ;;
            esac
        fi
    fi
    if [ -n "$linked" ]; then ok "copilotscope → $linked"
    else warn "could not put copilotscope on your PATH; call it by its full path: $target"
    fi
}

install_native() {
    step "Finding the build for this machine"
    command -v curl >/dev/null 2>&1 || die "curl is required."
    command -v tar >/dev/null 2>&1 || die "tar is required."
    rid="$(platform)" || exit 1
    ok "$rid"

    # COPILOTSCOPE_DOWNLOAD_BASE stands in for the release, for a mirror or a test.
    base="${COPILOTSCOPE_DOWNLOAD_BASE:-}"
    if [ -z "$base" ]; then
        if [ -n "$VERSION" ]; then base="https://github.com/$REPO/releases/download/$VERSION"
        else base="https://github.com/$REPO/releases/latest/download"
        fi
    fi
    archive="copilotscope-$rid.tar.gz"

    step "Downloading $archive"
    work="$(mktemp -d)"
    # shellcheck disable=SC2064  # expand now: $work is local to this run
    trap "rm -rf '$work'" EXIT
    if ! curl -fsSL "$base/$archive" -o "$work/$archive"; then
        # Releases carry native builds from the first one after ADR-004. Before that, the
        # Docker stack is what there is.
        if [ -z "$VERSION" ] && [ -z "${COPILOTSCOPE_DOWNLOAD_BASE:-}" ] && command -v docker >/dev/null 2>&1; then
            warn "the latest release has no native build yet; installing the Docker stack instead."
            install_docker
            return
        fi
        die "could not download $base/$archive.
       Pick a release with native builds (--version), or install the Docker stack (--docker)."
    fi
    curl -fsSL "$base/SHA256SUMS" -o "$work/SHA256SUMS" \
        || die "the release has no SHA256SUMS, and a download that cannot be checked is not installed."
    expected="$(awk -v f="$archive" '$2 == f || $2 == ("*" f) { print $1; exit }' "$work/SHA256SUMS")"
    [ -n "$expected" ] || die "SHA256SUMS does not list $archive, so it cannot be checked. Nothing was installed."
    actual="$(sha256_of "$work/$archive")"
    [ "$expected" = "$actual" ] || die "$archive does not match its checksum (expected $expected, got $actual).
       Nothing was installed. Try again; if it persists, report it."
    ok "checksum verified"

    step "Installing into $INSTALL_DIR"
    tar -C "$work" -xzf "$work/$archive"
    [ -x "$work/copilotscope-$rid/copilotscope" ] || die "the archive holds no copilotscope binary."
    mkdir -p "$INSTALL_DIR"
    app="$INSTALL_DIR/app"

    # A running copy is stopped first: its dashboard files are about to be replaced.
    restarted=""
    if [ -x "$app/copilotscope" ] && "$app/copilotscope" status >/dev/null 2>&1; then
        "$app/copilotscope" stop >/dev/null 2>&1 || true
        restarted="true"
        info "stopped the running CopilotScope to replace it"
    fi
    # Swapped in whole: an interrupted install leaves the previous version, never a mixture.
    rm -rf "$app.new" "$app.old"
    mv "$work/copilotscope-$rid" "$app.new"
    if [ -d "$app" ]; then mv "$app" "$app.old"; fi
    mv "$app.new" "$app"
    rm -rf "$app.old"
    ok "$("$app/copilotscope" version)"
    link_onto_path "$app/copilotscope"

    # The Docker stack on the same port would make `copilotscope` refuse to start; say so now.
    if curl -fsS --max-time 2 "http://127.0.0.1:4318/api/health" 2>/dev/null | grep -q hostlessSignals; then
        warn "a CopilotScope collector already answers on port 4318, most likely the Docker stack."
        info "Stop it before starting this one: $INSTALL_DIR/bin/copilotscope down (its data volume is kept)."
    fi

    if [ -n "$DO_CONNECT" ]; then
        step "Connecting your assistants"
        set -- setup
        [ -n "$ASSUME_YES" ] && set -- "$@" --yes
        [ -n "$CAPTURE" ] && set -- "$@" --capture
        # With `curl | sh` this script's stdin is the pipe, so setup asks on the terminal.
        # Without one it writes nothing, and says what it would have. Probed in a subshell: a
        # redirection that fails on a special built-in ends a non-interactive shell.
        if ( : </dev/tty ) 2>/dev/null; then "$app/copilotscope" "$@" </dev/tty || true
        else "$app/copilotscope" "$@" </dev/null || true
        fi
    fi

    transcripts=0
    claude_dir="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
    if [ -d "$claude_dir/projects" ]; then
        transcripts=$(find "$claude_dir/projects" -name '*.jsonl' -type f 2>/dev/null | wc -l | tr -d ' ')
    fi

    step "Done"
    if [ -n "$restarted" ]; then say "  It was running, and was stopped for the update. Start it again with:"
    else say "  Start it with:"
    fi
    say ""
    say "      copilotscope"
    say ""
    say "  The dashboard opens at http://localhost:5200; Ctrl+C stops it."
    if [ "${transcripts:-0}" -gt 0 ]; then
        say "  Your ${transcripts} Claude Code session(s) on disk are scored as it starts."
    fi
    say ""
    say "  ${C_DIM}copilotscope setup${C_RESET}    point your assistants at it"
    say "  ${C_DIM}copilotscope doctor${C_RESET}   check the wiring end to end"
    say ""
}

# ------------------------------------------------------------------ docker

install_docker() {
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
}

# The Docker stack's options mean nothing to the native binary, which binds to loopback only
# and is started by hand; saying so beats quietly ignoring them.
if [ -z "$DOCKER" ] && { [ -n "$BIND" ] || [ -n "$API_KEY" ] || [ -n "$TAG" ]; }; then
    die "--bind, --api-key and --tag are for the Docker stack: add --docker."
fi

if [ -n "$DOCKER" ]; then install_docker; else install_native; fi
