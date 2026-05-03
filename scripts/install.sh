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

TMP=$(mktemp -t wpa-mcp-install-XXXXXX)
mv "$TMP" "$TMP.ps1"
TMP="$TMP.ps1"
trap 'rm -f "$TMP"' EXIT

if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$URL" -o "$TMP"
elif command -v wget >/dev/null 2>&1; then
    wget -q "$URL" -O "$TMP"
else
    echo "[install.sh] Need curl or wget on PATH to download install.ps1" >&2
    exit 1
fi

WIN_PATH=$(cygpath -w "$TMP")
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$WIN_PATH" "$@"
