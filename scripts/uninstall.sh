#!/usr/bin/env bash
# Bash wrapper around uninstall.ps1. See install.sh for context.

set -euo pipefail

if ! command -v powershell.exe >/dev/null 2>&1; then
    echo "[uninstall.sh] powershell.exe not on PATH. Use uninstall.ps1 directly." >&2
    exit 1
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
PS1_PATH=$(cygpath -w "$DIR/uninstall.ps1")
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PS1_PATH" "$@"
