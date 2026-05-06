#!/usr/bin/env bash
# Bash one-liner installer for wpa-mcp. Designed to be curl-piped:
#
#   curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash
#
# Downloads install.ps1 from the same repo and runs it via powershell.exe. All
# flags forward to install.ps1; with curl-pipe-bash you'd typically pass them via
# the trailing argument convention:
#
#   curl -fsSL .../install.sh | bash -s -- -Tag v0.2.0
#
# Requires Git Bash on Windows (or any environment with both bash and powershell.exe
# on PATH). For native Linux/macOS, this tool isn't applicable — the underlying MCP
# server uses Windows-only ETW APIs.

set -euo pipefail

OWNER="${OWNER:-tooluse-labs}"
REPO="${REPO:-wpa-mcp}"
URL="https://raw.githubusercontent.com/$OWNER/$REPO/main/scripts/install.ps1"

if ! command -v powershell.exe >/dev/null 2>&1; then
    cat >&2 <<'EOF'
[install.sh] powershell.exe not on PATH.

wpa-mcp targets Windows. From Git Bash on Windows this normally works; if you're on
native Linux/macOS, the underlying MCP server can't run there (TraceEvent kernel
parsers are Windows-only).
EOF
    exit 1
fi

# IMPORTANT: do NOT name this variable TMP / TEMP / TMPDIR.  Those names are exported
# env vars on MSYS2/Git Bash, so a plain `TMP=...` assignment here updates the export
# and the exec'd powershell.exe inherits a $env:TMP pointing at our temp .ps1 FILE
# rather than the temp DIRECTORY.  dotnet-install.ps1 then calls .NET's
# Path.GetTempPath() (which prefers $TMP) and tries to create a sub-file inside what
# it thinks is a directory, throwing "Could not find a part of the path".
SCRIPT_FILE=$(mktemp -t wpa-mcp-install-XXXXXX)
mv "$SCRIPT_FILE" "$SCRIPT_FILE.ps1"
SCRIPT_FILE="$SCRIPT_FILE.ps1"
trap 'rm -f "$SCRIPT_FILE"' EXIT

if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$URL" -o "$SCRIPT_FILE"
elif command -v wget >/dev/null 2>&1; then
    wget -q "$URL" -O "$SCRIPT_FILE"
else
    echo "[install.sh] Need curl or wget on PATH to download install.ps1" >&2
    exit 1
fi

WIN_PATH=$(cygpath -w "$SCRIPT_FILE")
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$WIN_PATH" "$@"
