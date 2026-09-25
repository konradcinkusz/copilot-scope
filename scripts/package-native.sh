#!/usr/bin/env bash
# Builds the release archive of the native `copilotscope` binary for one platform (ADR-004):
#
#   artifacts/native/copilotscope-<rid>.tar.gz     (copilotscope-<rid>.zip for win-*)
#     copilotscope-<rid>/
#       copilotscope[.exe]   self-contained, single file: no .NET needed on the machine
#       wwwroot/             the dashboard's static files, from the dashboard's own publish
#       LICENSE
#
# Usage: scripts/package-native.sh <rid> [version]
#   e.g. scripts/package-native.sh linux-x64 1.1.0
#
# The archive name carries no version on purpose: releases/latest/download/<name> then always
# resolves to the newest one, which is what the installers download.
set -euo pipefail

rid="${1:?usage: scripts/package-native.sh <rid> [version]}"
version="${2:-0.0.0-dev}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/native"
work="$out/work-$rid"
name="copilotscope-$rid"

case "$rid" in
  win-x64|win-arm64) exe="copilotscope.exe"; archive="$out/$name.zip" ;;
  linux-x64|linux-arm64|osx-x64|osx-arm64) exe="copilotscope"; archive="$out/$name.tar.gz" ;;
  *) echo "package-native: unsupported runtime identifier '$rid'" >&2; exit 2 ;;
esac

rm -rf "$work" "$archive"
mkdir -p "$work/$name"

# The dashboard's static files come from the dashboard's own publish, because only that publish
# produces _framework/blazor.web.js. Not --no-restore: a publish that trusts a restore cache taken
# before the sources were present emits a wwwroot without _framework/, and the dashboard then
# renders once and never responds while every health check stays green (Dockerfile.dashboard).
dotnet publish "$root/src/CopilotScope.Dashboard" -c Release -o "$work/dashboard" -p:Version="$version"
if [ ! -f "$work/dashboard/wwwroot/_framework/blazor.web.js" ]; then
  echo "package-native: the dashboard publish has no wwwroot/_framework/blazor.web.js" >&2
  exit 1
fi

dotnet publish "$root/src/CopilotScope.Local" -c Release -r "$rid" -o "$work/$name" -p:Version="$version"

cp -R "$work/dashboard/wwwroot" "$work/$name/wwwroot"
# Pre-compressed copies are for the static-assets endpoint, which this host does not use.
find "$work/$name/wwwroot" -type f \( -name '*.br' -o -name '*.gz' \) -delete
cp "$root/LICENSE" "$work/$name/LICENSE"

# The archive holds the binary, its wwwroot and the licence, and nothing else. A stray
# appsettings.json beside the binary would replace the defaults compiled into it.
unexpected="$(cd "$work/$name" && find . -mindepth 1 -maxdepth 1 ! -name "$exe" ! -name wwwroot ! -name LICENSE)"
if [ -n "$unexpected" ]; then
  echo "package-native: unexpected files in the archive:" >&2
  echo "$unexpected" >&2
  exit 1
fi
[ -f "$work/$name/$exe" ] || { echo "package-native: $exe is missing from the publish" >&2; exit 1; }

case "$archive" in
  *.zip)
    # Git Bash on a Windows runner has no zip; it has 7-Zip, and PowerShell as a last resort.
    if command -v zip >/dev/null 2>&1; then
      (cd "$work" && zip -qr "$archive" "$name")
    elif command -v 7z >/dev/null 2>&1; then
      (cd "$work" && 7z a -tzip -bso0 -bsp0 "$archive" "$name")
    else
      native() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
      pwsh -NoProfile -Command "Compress-Archive -Path '$(native "$work/$name")' -DestinationPath '$(native "$archive")'"
    fi
    ;;
  *)
    tar -C "$work" -czf "$archive" "$name"
    ;;
esac

rm -rf "$work"
echo "$archive"
