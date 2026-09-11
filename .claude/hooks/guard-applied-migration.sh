#!/usr/bin/env bash
# PreToolUse (Edit|Write) — blocks edits to migrations that are already applied.
#
# A migration that has run anywhere is immutable: editing it means some
# databases hold the old version and some the new, with no way to tell which.
# The newest file is treated as still in progress and stays editable.
#
# Uses python3 rather than jq so there is no extra dependency to install.
set -uo pipefail

input=$(cat)
path=$(printf '%s' "$input" | python3 -c \
  'import json,sys; print(json.load(sys.stdin).get("tool_input",{}).get("file_path",""))' 2>/dev/null)

# Normalise Windows separators before matching. Claude Code passes a native
# path, so on Windows this arrives as D:\...\db\migrations\0001__x.sql — which
# never matches a forward-slash glob. Without this the hook exits 0 on every
# call and silently guards nothing, which is worse than not installing it.
path=$(printf '%s' "$path" | tr '\\' '/')

case "$path" in
  */db/migrations/*.sql) ;;
  *) exit 0 ;;
esac

# A file that does not exist yet is a new migration. Always allowed.
[ -f "$path" ] || exit 0

dir="${CLAUDE_PROJECT_DIR:-.}/db/migrations"
newest=$(ls "$dir" 2>/dev/null | grep -E '^[0-9]{4}__' | sort | tail -1)
file=$(basename "$path")

# Editing the newest migration is allowed; it is not applied yet.
[ "$file" = "$newest" ] && exit 0

python3 - "$file" <<'PY'
import json, sys
f = sys.argv[1]
print(json.dumps({"hookSpecificOutput": {
    "hookEventName": "PreToolUse",
    "permissionDecision": "deny",
    "permissionDecisionReason": (
        f"{f} is an already-applied migration and is immutable. "
        "Editing it leaves some databases on the old version with no way to "
        "tell which. Write a new migration instead — see "
        ".claude/skills/sql-migrations."
    )}}))
PY
