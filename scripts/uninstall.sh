#!/usr/bin/env bash
# Bash wrapper around uninstall.ps1.  Two invocation modes:
#
#   1. Local (extracted from the release zip):
#        ./uninstall.sh
#      Picks up uninstall.ps1 sitting next to this script on disk.
#
#   2. curl-pipe-bash (web one-liner):
#        curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.sh | bash
#      $0 is `bash`, so there's no script directory; downloads uninstall.ps1
#      from the repo into a temp file and runs that.

set -euo pipefail

OWNER="${OWNER:-tooluse-labs}"
REPO="${REPO:-wpa-mcp}"
URL="https://raw.githubusercontent.com/$OWNER/$REPO/main/scripts/uninstall.ps1"

if ! command -v powershell.exe >/dev/null 2>&1; then
    echo "[uninstall.sh] powershell.exe not on PATH. Use uninstall.ps1 directly." >&2
    exit 1
fi

# Mode 1: local sibling on disk.
SCRIPT_PATH="${BASH_SOURCE[0]:-$0}"
LOCAL_PS1=""
if [[ -f "$SCRIPT_PATH" ]]; then
    DIR="$(cd "$(dirname "$SCRIPT_PATH")" && pwd)"
    if [[ -f "$DIR/uninstall.ps1" ]]; then
        LOCAL_PS1="$DIR/uninstall.ps1"
    fi
fi

if [[ -n "$LOCAL_PS1" ]]; then
    PS1_PATH=$(cygpath -w "$LOCAL_PS1")
    exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PS1_PATH" "$@"
fi

# Mode 2: download from the repo.
TMP=$(mktemp -t wpa-mcp-uninstall-XXXXXX)
mv "$TMP" "$TMP.ps1"
TMP="$TMP.ps1"
trap 'rm -f "$TMP"' EXIT

if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$URL" -o "$TMP"
elif command -v wget >/dev/null 2>&1; then
    wget -q "$URL" -O "$TMP"
else
    echo "[uninstall.sh] Need curl or wget on PATH to download uninstall.ps1" >&2
    exit 1
fi

WIN_PATH=$(cygpath -w "$TMP")
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$WIN_PATH" "$@"
