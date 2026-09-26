#!/usr/bin/env bash
# CopilotScope developer setup — the tools to build, test and run CopilotScope from a clone.
#
# install.sh is for running CopilotScope and installs no .NET, on purpose (ADR-004). This
# script is for working on it. In order, it:
#
#   1. makes sure there is a .NET SDK of the major the code targets, read from the
#      TargetFramework so it retargets with the code. One already on the PATH is used as it is;
#      otherwise Microsoft's dotnet-install script puts one in ~/.dotnet, without root.
#   2. installs the Aspire CLI (`aspire run`), a .NET global tool from NuGet, on the Aspire
#      major the AppHost is built with. Aspire itself needs no install: the AppHost gets it as
#      NuGet packages, and `dotnet run --project src/CopilotScope.AppHost` works without the CLI.
#   3. checks for a container runtime, which Aspire runs Postgres and pgAdmin in. It installs
#      none: that needs root, and Docker Desktop is licensed on its own terms.
#   4. restores the solution, so the Aspire packages are on disk before the first build.
#   5. reports the other tools the pull-request checks use: node for the documentation
#      checks, shellcheck for the shell scripts. It installs neither.
#
# Safe to re-run: a step whose tool is already in place does nothing.
#
# Usage: scripts/dev-setup.sh [options]
#   --dotnet-dir PATH   use the SDK in PATH, installing it there if it is missing
#                       (default: the SDK on the PATH if it is the right major, else ~/.dotnet)
#   --no-aspire-cli     skip the Aspire CLI
#   --no-restore        skip `dotnet restore`
#   --persist           add DOTNET_ROOT and the PATH entries this needs to your shell rc file,
#                       in a marked block that a re-run replaces rather than duplicates
#   --rc-file PATH      the file --persist writes (default: ~/.zshrc or ~/.bashrc, by $SHELL)
#   -h, --help          show this help
set -euo pipefail

SCRIPT_PATH="${BASH_SOURCE[0]:-$0}"
REPO_ROOT="$(cd "$(dirname "$SCRIPT_PATH")/.." && pwd)"
DOTNET_INSTALL_URL="https://dot.net/v1/dotnet-install.sh"
TOOLS_DIR="$HOME/.dotnet/tools"

DOTNET_DIR=""
ASPIRE_CLI="true"
RESTORE="true"
PERSIST=""
RC_FILE=""

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
    C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_RED=$'\033[31m'
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
        --dotnet-dir) DOTNET_DIR="${2:?--dotnet-dir needs a path}"; shift 2 ;;
        --no-aspire-cli) ASPIRE_CLI=""; shift ;;
        --no-restore) RESTORE=""; shift ;;
        --persist) PERSIST="true"; shift ;;
        --rc-file) RC_FILE="${2:?--rc-file needs a path}"; shift 2 ;;
        -h|--help)
            awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$SCRIPT_PATH"
            exit 0 ;;
        *) die "unknown option '$1' (see --help)" ;;
    esac
done

# The SDK major is the TargetFramework's, and the Aspire CLI's is the AppHost SDK's: read from
# the project files, so a retarget needs no edit here.
TFM="$(awk -F'[<>]' '/<TargetFramework>net/ { sub(/^net/, "", $3); print $3; exit }' \
    "$REPO_ROOT/src/CopilotScope.Collector/CopilotScope.Collector.csproj")"
[ -n "$TFM" ] || die "no TargetFramework found in src/CopilotScope.Collector — is this a CopilotScope clone?"
SDK_MAJOR="${TFM%%.*}"
ASPIRE_MAJOR="$(awk '/Name="Aspire.AppHost.Sdk"/ && match($0, /Version="[0-9]+/) { print substr($0, RSTART + 9, RLENGTH - 9); exit }' \
    "$REPO_ROOT/src/CopilotScope.AppHost/CopilotScope.AppHost.csproj")"

# The newest SDK of the right major that the dotnet at $1 has, or nothing.
sdk_of() {
    local sdks
    sdks="$("$1" --list-sdks 2>/dev/null)" || return 0
    grep "^${SDK_MAJOR}\." <<<"$sdks" | tail -n 1 | awk '{print $1}' || true
}

# Lines a new terminal needs to find what this installed; printed, or written by --persist.
# Judged against the environment this script was started in, before it adds to PATH.
ENV_LINES=()
ORIG_PATH="$PATH"
ORIG_DOTNET_ROOT="${DOTNET_ROOT:-}"
on_path() { case ":$ORIG_PATH:" in *":$1:"*) return 0 ;; *) return 1 ;; esac; }

say ""
say "${C_BOLD}CopilotScope developer setup${C_RESET} — .NET $TFM SDK, Aspire, a container runtime."

# ------------------------------------------------------------------ 1. .NET SDK
step "1. .NET $TFM SDK"
DOTNET=""
if [ -z "$DOTNET_DIR" ] && command -v dotnet >/dev/null 2>&1 && [ -n "$(sdk_of dotnet)" ]; then
    DOTNET="$(command -v dotnet)"
    ok "SDK $(sdk_of dotnet) on the PATH ($DOTNET)"
else
    if [ -z "$DOTNET_DIR" ]; then
        DOTNET_DIR="$HOME/.dotnet"
        if command -v dotnet >/dev/null 2>&1; then
            info "the dotnet on the PATH has no $SDK_MAJOR.x SDK: $("$(command -v dotnet)" --list-sdks 2>/dev/null | awk '{print $1}' | paste -sd' ' -)"
        fi
    fi
    mkdir -p "$DOTNET_DIR"
    DOTNET_DIR="$(cd "$DOTNET_DIR" && pwd)"
    DOTNET="$DOTNET_DIR/dotnet"
    if [ -x "$DOTNET" ] && [ -n "$(sdk_of "$DOTNET")" ]; then
        ok "SDK $(sdk_of "$DOTNET") in $DOTNET_DIR"
    else
        command -v curl >/dev/null 2>&1 || die "curl is needed to download the .NET SDK."
        info "Installing the .NET $TFM SDK into $DOTNET_DIR, with Microsoft's $DOTNET_INSTALL_URL"
        installer="$(mktemp -t dotnet-install.XXXXXX)"
        trap 'rm -f "$installer"' EXIT
        curl -fsSL "$DOTNET_INSTALL_URL" -o "$installer" \
            || die "could not download $DOTNET_INSTALL_URL (curl's reason is above)."
        bash "$installer" --channel "$TFM" --install-dir "$DOTNET_DIR" --no-path \
            || die "dotnet-install failed; its output is above."
        [ -n "$(sdk_of "$DOTNET")" ] || die "dotnet-install finished, but $DOTNET has no $SDK_MAJOR.x SDK."
        ok "SDK $(sdk_of "$DOTNET") installed in $DOTNET_DIR"
    fi
    # The rest of this script, and whatever it starts, runs on this SDK.
    export DOTNET_ROOT="$DOTNET_DIR"
    export PATH="$DOTNET_DIR:$PATH"
    [ "$ORIG_DOTNET_ROOT" = "$DOTNET_DIR" ] || ENV_LINES+=("export DOTNET_ROOT=\"$DOTNET_DIR\"")
    on_path "$DOTNET_DIR" || ENV_LINES+=("export PATH=\"$DOTNET_DIR:\$PATH\"")
fi

# -------------------------------------------------------------- 2. Aspire CLI
step "2. Aspire"
info "Aspire itself arrives as NuGet packages on restore — no workload to install."
if [ -z "$ASPIRE_CLI" ]; then
    info "Aspire CLI skipped (--no-aspire-cli); \`dotnet run --project src/CopilotScope.AppHost\` needs none."
elif [ -z "$ASPIRE_MAJOR" ]; then
    warn "no Aspire.AppHost.Sdk version found in the AppHost project; Aspire CLI skipped."
else
    aspire_version=""; aspire_where=""
    if command -v aspire >/dev/null 2>&1; then
        aspire_version="$(aspire --version 2>/dev/null | head -n 1 || true)"
        aspire_where="$(command -v aspire)"
    else
        tools="$("$DOTNET" tool list --global 2>/dev/null || true)"
        aspire_version="$(grep -i '^aspire\.cli ' <<<"$tools" | awk '{print $2}' || true)"
        [ -z "$aspire_version" ] || aspire_where="$TOOLS_DIR"
    fi
    if [ -z "$aspire_where" ]; then
        info "Installing the Aspire CLI $ASPIRE_MAJOR.x, a .NET global tool from NuGet"
        (cd "$REPO_ROOT" && "$DOTNET" tool install --global Aspire.Cli --version "$ASPIRE_MAJOR.*") \
            || die "installing the Aspire CLI failed; its output is above. --no-aspire-cli skips it."
        ok "Aspire CLI installed in $TOOLS_DIR"
    elif [ "${aspire_version%%.*}" = "$ASPIRE_MAJOR" ]; then
        ok "Aspire CLI $aspire_version ($aspire_where)"
    else
        # One installed for another project, on another major: left alone, but named.
        warn "Aspire CLI ${aspire_version:-of unknown version} ($aspire_where), but the AppHost is on Aspire $ASPIRE_MAJOR."
        warn "from NuGet, it moves with: dotnet tool update --global Aspire.Cli --version \"$ASPIRE_MAJOR.*\""
    fi
    on_path "$TOOLS_DIR" || ENV_LINES+=("export PATH=\"\$HOME/.dotnet/tools:\$PATH\"")
fi

# ------------------------------------------------------ 3. container runtime
step "3. Container runtime"
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    ok "Docker is running"
elif command -v docker >/dev/null 2>&1; then
    warn "Docker is installed but not answering — start it before running the AppHost."
elif command -v podman >/dev/null 2>&1; then
    ok "Podman — Aspire uses it with ASPIRE_CONTAINER_RUNTIME=podman"
else
    warn "no Docker. Build, test and the collector without Postgres all work without it;"
    warn "the AppHost's Postgres and pgAdmin do not: https://docs.docker.com/get-docker/"
fi

# --------------------------------------------------------------- 4. restore
step "4. Restore"
if [ -z "$RESTORE" ]; then
    info "skipped (--no-restore)"
else
    "$DOTNET" restore "$REPO_ROOT/CopilotScope.sln" \
        || die "dotnet restore failed; its output is above."
    ok "packages restored"
fi

# ------------------------------------------------ 5. the pull-request checks
step "5. Other tools the pull-request checks use (reported, not installed)"
if command -v node >/dev/null 2>&1; then
    ok "node $(node --version) — npm ci && npm run check:diagrams"
else
    info "no node: needed only for the documentation checks (npm run check:diagrams)"
fi
if command -v shellcheck >/dev/null 2>&1; then
    ok "shellcheck $(shellcheck --version | sed -n 's/^version: //p')"
else
    info "no shellcheck: CI lints every shell script with it at -S warning"
fi

# ------------------------------------------------------------ the environment
if [ ${#ENV_LINES[@]} -gt 0 ]; then
    step "New terminals"
    if [ -n "$PERSIST" ]; then
        rc="${RC_FILE:-}"
        if [ -z "$rc" ]; then
            case "$(basename "${SHELL:-/bin/bash}")" in
                zsh) rc="$HOME/.zshrc" ;;
                *)   rc="$HOME/.bashrc" ;;
            esac
        fi
        begin="# >>> CopilotScope (dev-setup) >>>"
        end="# <<< CopilotScope (dev-setup) <<<"
        touch "$rc"
        awk -v b="$begin" -v e="$end" '$0==b{skip=1} skip!=1{print} $0==e{skip=0}' "$rc" > "$rc.copilotscope.tmp"
        mv "$rc.copilotscope.tmp" "$rc"
        # One blank line before the block, and only one however often this is re-run.
        separator=""
        if [ -s "$rc" ] && [ -n "$(tail -n 1 "$rc")" ]; then separator="true"; fi
        {
            if [ -n "$separator" ]; then echo ""; fi
            echo "$begin"; printf '%s\n' "${ENV_LINES[@]}"; echo "$end"
        } >> "$rc"
        ok "written to $rc — open a new terminal, or: source $rc"
    else
        info "this terminal does not have them yet; add these to your shell rc (or re-run with --persist):"
        printf '      %s\n' "${ENV_LINES[@]}"
    fi
fi

step "Ready"
say "  dotnet build                                   # the whole solution"
say "  dotnet test                                    # no Docker, no live collector"
if [ -n "$ASPIRE_CLI" ] && [ -n "$ASPIRE_MAJOR" ]; then
    say "  aspire run                                     # Postgres, pgAdmin, collector, dashboard"
fi
say "  dotnet run --project src/CopilotScope.AppHost  # the same, without the CLI"
say ""
