#!/usr/bin/env bash
# Bash wrapper around install.ps1 for Git Bash users on Windows. The underlying logic
# (.NET install, MCP client config edits, registry-style operations) needs PowerShell;
# this wrapper just forwards args. All install.ps1 flags work — pass them as you
# normally would in PowerShell:
#
#   ./scripts/install.sh -Client codex -SymbolPath "SRV*C:\Symbols*https://msdl..."
#
# Note: this only works in environments where powershell.exe is on PATH (Git Bash on
# Windows). Genuine Linux/macOS bash isn't supported because the underlying MCP server
# is Windows-only.

set -euo pipefail

if ! command -v powershell.exe >/dev/null 2>&1; then
    echo "[install.sh] powershell.exe not on PATH. wpa-mcp targets Windows; run from Git Bash on Windows or invoke install.ps1 directly." >&2
    exit 1
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
PS1_PATH=$(cygpath -w "$DIR/install.ps1")
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PS1_PATH" "$@"
