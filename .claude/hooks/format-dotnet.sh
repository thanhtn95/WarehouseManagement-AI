#!/usr/bin/env bash
# PostToolUse (Edit|Write) — formats C# files after a write. Never blocks.
set -uo pipefail

input=$(cat)
path=$(printf '%s' "$input" | python3 -c \
  'import json,sys; print(json.load(sys.stdin).get("tool_input",{}).get("file_path",""))' 2>/dev/null)

case "$path" in *.cs) ;; *) exit 0 ;; esac
command -v dotnet >/dev/null 2>&1 || exit 0

dotnet format --include "$path" --no-restore >/dev/null 2>&1 || true
exit 0
