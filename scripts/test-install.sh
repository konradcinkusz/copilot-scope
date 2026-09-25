#!/usr/bin/env bash
# Installs a native release archive with the real installer, from a local web server standing
# in for the GitHub release, and holds the installer to what it promises:
#
#   - the binary it installs runs, from the PATH as well as from where it was put;
#   - a second install replaces the first in place;
#   - with --yes, the assistants it finds are connected (install.sh; Claude Code here);
#   - a download that does not match SHA256SUMS is refused, and the installed copy is untouched.
#
# Usage: scripts/test-install.sh <archive>
#   e.g. scripts/test-install.sh artifacts/native/copilotscope-linux-x64.tar.gz
# A .zip is installed with install.ps1 through pwsh, a .tar.gz with install.sh.
set -euo pipefail

archive="${1:?usage: scripts/test-install.sh <archive>}"
archive="$(cd "$(dirname "$archive")" && pwd)/$(basename "$archive")"
repo="$(cd "$(dirname "$0")/.." && pwd)"
port="${INSTALL_TEST_PORT:-38080}"

tmp="$(mktemp -d)"
server=""
cleanup() {
  if [ -n "$server" ]; then kill "$server" 2>/dev/null || true; fi
  rm -rf "$tmp"
}
trap cleanup EXIT

fail() {
  echo "::error title=Install test failed::$*"
  if [ -f "$tmp/install.log" ]; then echo "── installer output"; cat "$tmp/install.log"; fi
  exit 1
}

sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

# ── the release, served locally
release="$tmp/release"
mkdir -p "$release"
cp "$archive" "$release/"
(cd "$release" && sha256 "$(basename "$archive")" > SHA256SUMS)
python="$(command -v python3 || command -v python)" || fail "python is needed to serve the release"
"$python" -m http.server "$port" --bind 127.0.0.1 --directory "$release" >/dev/null 2>&1 &
server=$!
for _ in $(seq 1 30); do
  curl -fsS "http://127.0.0.1:$port/SHA256SUMS" >/dev/null 2>&1 && break
  sleep 0.5
done
curl -fsS "http://127.0.0.1:$port/SHA256SUMS" >/dev/null || fail "the local release server did not start"
export COPILOTSCOPE_DOWNLOAD_BASE="http://127.0.0.1:$port"

# ── a home of its own
export HOME="$tmp/home"
mkdir -p "$HOME/.local/bin" "$HOME/.claude"
export PATH="$HOME/.local/bin:$PATH"
unset COPILOTSCOPE_HOME CLAUDE_CONFIG_DIR XDG_CONFIG_HOME

case "$archive" in
  *.zip)
    native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
    install_dir="$tmp/home/.copilotscope"
    installed="$install_dir/app/copilotscope.exe"
    run_installer() {
      pwsh -NoProfile -ExecutionPolicy Bypass -File "$(native "$repo/install.ps1")" \
        -NoConnect -InstallDir "$(native "$install_dir")" >"$tmp/install.log" 2>&1
    }
    ;;
  *)
    installed="$HOME/.copilotscope/app/copilotscope"
    run_installer() { sh "$repo/install.sh" --yes >"$tmp/install.log" 2>&1 </dev/null; }
    ;;
esac

# ── first install
run_installer || fail "the installer failed"
"$installed" version >/dev/null || fail "the installed binary does not run: $installed"
grep -q "checksum verified" "$tmp/install.log" || fail "the installer did not verify the checksum"
echo "ok installed: $("$installed" version)"

case "$archive" in
  *.zip) ;;
  *)
    [ "$(command -v copilotscope)" = "$HOME/.local/bin/copilotscope" ] || fail "copilotscope is not on the PATH"
    copilotscope doctor >"$tmp/doctor.log" 2>&1 || true
    grep -q "dashboard files complete" "$tmp/doctor.log" \
      || fail "the linked binary does not find its dashboard files: $(cat "$tmp/doctor.log")"
    grep -q '"OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4318"' "$HOME/.claude/settings.json" \
      || fail "--yes did not connect Claude Code"
    echo "ok on the PATH, dashboard files found, Claude Code connected"
    ;;
esac

# ── an update in place
run_installer || fail "installing over an existing install failed"
"$installed" version >/dev/null || fail "the binary does not run after an update"
echo "ok updated in place"

# ── a download that does not match its checksum
printf 'tampered' >> "$release/$(basename "$archive")"
if run_installer; then fail "a tampered archive was installed"; fi
grep -q "does not match its checksum" "$tmp/install.log" || fail "the refusal did not name the checksum"
"$installed" version >/dev/null || fail "a refused download damaged the installed copy"
echo "ok tampered download refused, installed copy intact"

echo "install test passed: $(basename "$archive")"
