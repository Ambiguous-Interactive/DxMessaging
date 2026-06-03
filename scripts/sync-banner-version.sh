#!/bin/bash
#
# Wrapper script to run sync-banner-version.ps1 with PowerShell.
# This script is used by pre-commit hooks to keep YAML lines short.
# If PowerShell is unavailable, it falls back to the Node implementation.
#

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if command -v pwsh >/dev/null 2>&1; then
    pwsh -NoProfile -File "$SCRIPT_DIR/sync-banner-version.ps1"
elif command -v powershell >/dev/null 2>&1; then
    powershell -NoProfile -ExecutionPolicy Bypass -File "$SCRIPT_DIR/sync-banner-version.ps1"
else
    if command -v node >/dev/null 2>&1; then
        node "$SCRIPT_DIR/sync-banner-version.js"
    else
        echo "Neither PowerShell nor Node.js is available; cannot sync banner." >&2
        exit 1
    fi
fi
