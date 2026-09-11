#!/usr/bin/env bash
# PostToolUse (Edit|Write) — surfaces WMS invariant violations to Claude.
#
# Runs after the edit rather than blocking it, so Claude can finish a pass and
# then correct itself. Exit 2 on PostToolUse shows stderr to Claude.
set -uo pipefail

input=$(cat)
path=$(printf '%s' "$input" | python3 -c \
  'import json,sys; print(json.load(sys.stdin).get("tool_input",{}).get("file_path",""))' 2>/dev/null)

[ -n "$path" ] && [ -f "$path" ] || exit 0
case "$path" in *.cs|*.sql) ;; *) exit 0 ;; esac

findings=""

# 1. Guard clause on a fact-path balance decrement.
if grep -Eqi 'on_hand[^;]*>=[[:space:]]*[@$:]?(qty|quantity)' "$path" \
   && grep -Eqi "movement_type[[:space:]]*=[[:space:]]*'?(pick|putaway|receipt)" "$path"; then
  findings="${findings}
- A fact-path balance decrement appears to carry a guard clause (on_hand >= qty).
  The goods have already physically moved, so the decrement must not be able to
  fail. See CLAUDE.md invariant 3."
fi

# 2. Constraint forbidding negative balances.
if grep -Eqi 'CHECK[[:space:]]*\([[:space:]]*on_hand[[:space:]]*>=' "$path"; then
  findings="${findings}
- A CHECK constraint forbids negative on_hand. Offline facts drive balances
  negative and that is correct; the exception queue surfaces it.
  See CLAUDE.md invariant 4."
fi

# 3. Role-name comparison instead of a permission check.
if grep -Eq '\.Role[[:space:]]*==[[:space:]]*"' "$path"; then
  findings="${findings}
- Authorisation compares a role name. Check permissions instead.
  See CLAUDE.md invariant 8."
fi

# 4. Queue or lease query without SKIP LOCKED.
if grep -qi 'FOR UPDATE' "$path" && ! grep -qi 'SKIP LOCKED' "$path" \
   && grep -Eqi 'FROM[[:space:]]+(task|allocation_request|outbox_message)' "$path"; then
  findings="${findings}
- A queue query uses FOR UPDATE without SKIP LOCKED, which serialises every
  operator. See CLAUDE.md invariant 6."
fi

[ -z "$findings" ] && exit 0

printf 'WMS invariant check on %s:%s\n' "$(basename "$path")" "$findings" >&2
exit 2
