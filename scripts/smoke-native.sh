#!/usr/bin/env bash
# Smoke-tests a native release archive the way a user meets it: extracted into an empty
# directory, started with a home directory of its own, used, stopped, started again.
#
# Usage: scripts/smoke-native.sh <archive>
#   e.g. scripts/smoke-native.sh artifacts/native/copilotscope-linux-x64.tar.gz
#
# Checks the things that have failed silently before, or would:
#   - the dashboard serves _framework/blazor.web.js from the extracted archive, not just "/";
#   - sessions land in files and survive a stop and a start;
#   - a second start finds the first instead of failing on the port;
#   - OTEL_EXPORTER_OTLP_ENDPOINT pointing at the collector itself does not make it ingest its
#     own telemetry.
set -euo pipefail

archive="${1:?usage: scripts/smoke-native.sh <archive>}"
archive="$(cd "$(dirname "$archive")" && pwd)/$(basename "$archive")"
otlp="${SMOKE_OTLP_PORT:-34318}"
dash="${SMOKE_DASHBOARD_PORT:-35200}"

tmp="$(mktemp -d)"
pid=""
cleanup() {
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then kill "$pid" 2>/dev/null || true; fi
  rm -rf "$tmp"
}
trap cleanup EXIT

fail() {
  echo "::error title=Native smoke test failed::$*"
  if [ -f "$tmp/run.log" ]; then echo "── process output"; cat "$tmp/run.log"; fi
  exit 1
}

case "$archive" in
  *.zip)
    # Git Bash on a Windows runner may have no unzip; it has 7-Zip, and PowerShell.
    if command -v unzip >/dev/null 2>&1; then
      (cd "$tmp" && unzip -q "$archive")
    elif command -v 7z >/dev/null 2>&1; then
      7z x -bso0 -bsp0 "-o$tmp" "$archive"
    else
      native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
      pwsh -NoProfile -Command "Expand-Archive -Path '$(native "$archive")' -DestinationPath '$(native "$tmp")'"
    fi
    ;;
  *) tar -C "$tmp" -xzf "$archive" ;;
esac
dir="$(find "$tmp" -mindepth 1 -maxdepth 1 -type d -name 'copilotscope-*' | head -n 1)"
[ -n "$dir" ] || fail "the archive has no copilotscope-<rid> directory"
bin="$dir/copilotscope"
[ -x "$bin" ] || bin="$dir/copilotscope.exe"
[ -x "$bin" ] || fail "no copilotscope executable in $dir"

export COPILOTSCOPE_HOME="$tmp/home"
"$bin" version

start() {
  "$bin" start --otlp-port "$otlp" --dashboard-port "$dash" --no-browser >>"$tmp/run.log" 2>&1 &
  pid=$!
  for _ in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:$otlp/api/health" >/dev/null 2>&1; then return 0; fi
    kill -0 "$pid" 2>/dev/null || fail "copilotscope exited during start-up"
    sleep 1
  done
  fail "the collector did not answer on :$otlp within 60 s"
}

expect_status() { # path port expected-code
  local code
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$2$1")"
  [ "$code" = "$3" ] || fail "GET :$2$1 answered $code, expected $3"
  echo "ok GET :$2$1 → $code"
}

span='{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"copilot-chat"}},{"key":"session.id","value":{"stringValue":"smoke-window"}}]},"scopeSpans":[{"spans":[{"traceId":"0102030405060708090a0b0c0d0e0f10","spanId":"0102030405060708","name":"chat gpt-5","startTimeUnixNano":"1790000000000000000","endTimeUnixNano":"1790000001000000000","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},{"key":"gen_ai.conversation.id","value":{"stringValue":"conv-native-smoke"}},{"key":"gen_ai.usage.input_tokens","value":{"intValue":"42"}}]}]}]}]}'

# ── first run
start
health="$(curl -fsS "http://127.0.0.1:$otlp/api/health")"
echo "$health" | grep -q '"storage":"files"' || fail "expected file storage, health says: $health"
expect_status / "$dash" 200
expect_status /_framework/blazor.web.js "$dash" 200
expect_status /app.css "$dash" 200
expect_status /docs "$dash" 200

curl -fsS -H 'Content-Type: application/json' --data "$span" "http://127.0.0.1:$otlp/v1/traces" >/dev/null \
  || fail "OTLP ingest was refused"
expect_status /api/sessions/conv-native-smoke "$otlp" 200

"$bin" status || fail "status says it is not running"
second="$("$bin" start --otlp-port "$otlp" --dashboard-port "$dash" --no-browser 2>&1)" \
  || fail "a second start failed instead of finding the first: $second"
echo "$second" | grep -q "already running" || fail "a second start did not report the running instance: $second"

"$bin" stop || fail "stop failed"
for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
kill -0 "$pid" 2>/dev/null && fail "the process is still running after stop"
[ ! -f "$COPILOTSCOPE_HOME/run/instance.json" ] || fail "stop left the instance file behind"
ls "$COPILOTSCOPE_HOME/data/sessions/"*.json >/dev/null 2>&1 || fail "no session file was written"
echo "ok stopped, session on disk"

# ── second run: history survives, and self-telemetry stays off even when the environment
# points OpenTelemetry at the collector itself — the usual state of a shell `copilotscope
# connect copilot-cli` has configured.
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:$otlp"
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"   # the default is gRPC, which this endpoint would refuse anyway
start
expect_status /api/sessions/conv-native-smoke "$otlp" 200
sleep 8   # longer than the OpenTelemetry batch exporter's 5 s schedule
sessions="$(curl -fsS "http://127.0.0.1:$otlp/api/health" | sed -E 's/.*"sessions":([0-9]+).*/\1/')"
[ "$sessions" = "1" ] || fail "expected exactly the one smoke session, found $sessions — is the collector ingesting its own telemetry?"
echo "ok no self-telemetry"
"$bin" stop || fail "second stop failed"

echo "native smoke test passed: $(basename "$archive")"
