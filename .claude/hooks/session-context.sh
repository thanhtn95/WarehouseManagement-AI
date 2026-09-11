#!/usr/bin/env bash
# SessionStart — injects current project state as context for Claude.
set -uo pipefail

dir="${CLAUDE_PROJECT_DIR:-.}"
migrations=$(ls "$dir/db/migrations" 2>/dev/null | grep -cE '^[0-9]{4}__' || true)
latest=$(ls "$dir/db/migrations" 2>/dev/null | grep -E '^[0-9]{4}__' | sort | tail -1)
branch=$(git -C "$dir" rev-parse --abbrev-ref HEAD 2>/dev/null || true)
shortcuts=$(grep -c '^- ' "$dir/docs/shortcuts.md" 2>/dev/null || true)

python3 - "${branch:-unknown}" "${migrations:-0}" "${latest:-none}" "${shortcuts:-0}" <<'PY'
import json, sys
branch, migrations, latest, shortcuts = sys.argv[1:5]
ctx = (
    f"Project state: branch {branch}. {migrations} migrations present, "
    f"latest {latest}. {shortcuts} tracked shortcuts in docs/shortcuts.md. "
    "The active phase and the correctness invariants are in CLAUDE.md; "
    "the full design is in docs/wms-design-document.md."
)
print(json.dumps({"hookSpecificOutput": {
    "hookEventName": "SessionStart", "additionalContext": ctx}}))
PY
