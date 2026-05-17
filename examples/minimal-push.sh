#!/usr/bin/env bash
# gscript MINIMAL EXAMPLE (bash) — README typo fix.
#
# The smallest viable form: stages one file, commits, pushes, exits.
# No CI watch, no post-deploy probe. Use this shape when you're shipping
# a quick doc fix and don't need the full ceremony.

set -euo pipefail

COMMIT_NAME="ai-bot"
COMMIT_EMAIL="ai-bot@example.com"
REPO_OWNER="your-github-username"
REPO_NAME="your-repo-name"
LOCALMD_PATH="${HOME}/private/local.md"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Stale-lock auto-recovery (minimal inline version)
for lock in index.lock HEAD.lock config.lock; do
    if [ -f "$SCRIPT_DIR/.git/$lock" ]; then
        if [ -z "$(pgrep -x git 2>/dev/null || true)" ]; then
            echo "Removing stale lock: $lock" >&2
            rm -f "$SCRIPT_DIR/.git/$lock"
        else
            echo "Locks present and git processes running. Wait + retry." >&2
            exit 1
        fi
    fi
done

# PAT from localmd
if [ ! -f "$LOCALMD_PATH" ]; then
    echo "ERROR: $LOCALMD_PATH not found." >&2
    exit 1
fi
PAT=$(grep -oE 'github_pat_[A-Za-z0-9_]{40,}' "$LOCALMD_PATH" | head -n 1 || true)
if [ -z "$PAT" ]; then
    echo "ERROR: No PAT in localmd." >&2
    exit 1
fi

PUSH_URL="https://x-access-token:${PAT}@github.com/${REPO_OWNER}/${REPO_NAME}.git"

# Fetch + divergence check
echo "Fetching..."
git fetch --quiet "$PUSH_URL" main
BEHIND=$(git rev-list FETCH_HEAD ^HEAD --count)
if [ "$BEHIND" != "0" ]; then
    echo "ERROR: origin/main is $BEHIND ahead. Pull --rebase first." >&2
    exit 1
fi

# Trailing-null check on the one file we're staging
FILE="README.md"
NULL_COUNT=$(python3 -c "
import pathlib
b = pathlib.Path('$SCRIPT_DIR/$FILE').read_bytes()
i = len(b) - 1; n = 0
while i >= 0 and b[i] == 0:
    n += 1; i -= 1
print(n)
")
if [ "$NULL_COUNT" -gt 0 ]; then
    echo "ERROR: $FILE has $NULL_COUNT trailing 0x00 bytes." >&2
    exit 1
fi

# Stage + commit + push
git add -- "$FILE"
git -c "user.name=$COMMIT_NAME" -c "user.email=$COMMIT_EMAIL" commit -m "docs: fix typo in README"

echo "Pushing..."
git push "$PUSH_URL" main
echo "PUSHED. Done."

# Self-delete
rm -f "${BASH_SOURCE[0]}"
