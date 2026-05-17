#!/usr/bin/env bash
# gscript TEMPLATE — bash. Copy to <repo>/gscript.sh, fill in the per-sprint
# sections marked CONFIGURE, run, watch it self-delete on success.
#
# Self-deletes after a successful push per the gscript convention (a per-sprint
# instance is an AI-generated artifact specific to one commit; not part of
# repo history). The template stays committed.
#
# Requires: bash 4+ (for associative arrays), curl, jq, python3 (for trailing-
# null check + JSON validate; py3 is on every modern Linux/macOS).
#
# https://github.com/erikcheatham/gscript
# Apache 2.0

set -euo pipefail

# ── CONFIGURE: persona identity for AI-authored commits ───────────────
COMMIT_NAME="ai-bot"
COMMIT_EMAIL="ai-bot@example.com"

# ── CONFIGURE: repo identifier ────────────────────────────────────────
REPO_OWNER="your-github-username"
REPO_NAME="your-repo-name"

# ── CONFIGURE: CI watch ───────────────────────────────────────────────
WATCH_CI=true
CI_WORKFLOW_FILE="deploy.yml"
CI_WATCH_MAX_MINUTES=15
CI_WATCH_POLL_SECONDS=20

# ── CONFIGURE: post-deploy probes ─────────────────────────────────────
# Format: "URL|MIN-MAX". Add per-sprint smoke targets.
PROBE_ENDPOINTS=(
    # "https://your-app.example.com/|200-399"
)

# ── CONFIGURE: persona-specific localmd path (defaults to ~/private/local.md) ──
LOCALMD_PATH="${HOME}/private/local.md"

# ── Color helpers (no-op if stdout isn't a tty) ───────────────────────
if [ -t 1 ]; then
    C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_RED=$'\033[31m'
    C_YELLOW=$'\033[33m'; C_GRAY=$'\033[90m'; C_RESET=$'\033[0m'
else
    C_CYAN=""; C_GREEN=""; C_RED=""; C_YELLOW=""; C_GRAY=""; C_RESET=""
fi
log_cyan()   { printf "%s%s%s\n" "$C_CYAN" "$1" "$C_RESET"; }
log_green()  { printf "%s%s%s\n" "$C_GREEN" "$1" "$C_RESET"; }
log_red()    { printf "%s%s%s\n" "$C_RED" "$1" "$C_RESET" >&2; }
log_yellow() { printf "%s%s%s\n" "$C_YELLOW" "$1" "$C_RESET"; }
log_gray()   { printf "%s%s%s\n" "$C_GRAY" "$1" "$C_RESET"; }

# Find repo root (where this script lives)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Stale-lock auto-recovery ──────────────────────────────────────────
clear_stale_locks() {
    local git_dir="$1"
    local locks=("index.lock" "HEAD.lock" "config.lock" "packed-refs.lock" "shallow.lock" "fetch.lock")
    local found=()
    for lock in "${locks[@]}"; do
        [ -f "$git_dir/$lock" ] && found+=("$git_dir/$lock")
    done
    [ ${#found[@]} -eq 0 ] && return 0

    # Are any git processes running?
    local git_pids
    git_pids=$(pgrep -x git 2>/dev/null || true)
    if [ -n "$git_pids" ]; then
        log_yellow "WARN: ${#found[@]} lock file(s) present and git processes are running:"
        for pid in $git_pids; do
            log_yellow "  PID $pid"
        done
        log_yellow "Not auto-removing. Wait for git processes to finish, then re-run."
        exit 1
    fi

    for lock_path in "${found[@]}"; do
        local age_sec
        age_sec=$(( $(date +%s) - $(stat -c %Y "$lock_path" 2>/dev/null || stat -f %m "$lock_path") ))
        log_yellow "Removing stale lock: $(basename "$lock_path") (age ${age_sec}s, no git procs running)"
        rm -f "$lock_path"
    done
}
clear_stale_locks "$SCRIPT_DIR/.git"

# ── Git retry wrapper ─────────────────────────────────────────────────
# Wraps each git operation in 3-attempt exponential backoff (1s/2s/4s).
# Detects lock-collision errors specifically; re-runs lock cleanup
# between retries.
git_with_retry() {
    local context="$1"
    shift
    local max_attempts=3
    local delay=1
    local attempt
    for attempt in $(seq 1 $max_attempts); do
        local stderr
        if stderr=$(git "$@" 2>&1); then
            [ -n "$stderr" ] && echo "$stderr"
            return 0
        fi
        if echo "$stderr" | grep -qE "index\.lock|HEAD\.lock|Unable to create"; then
            if [ $attempt -lt $max_attempts ]; then
                log_yellow "  $context attempt $attempt/$max_attempts hit lock; retrying in ${delay}s..."
                sleep $delay
                clear_stale_locks "$SCRIPT_DIR/.git"
                delay=$((delay * 2))
                continue
            fi
        fi
        log_red "$stderr"
        return 1
    done
    return 1
}

# ── Resolve PAT from localmd ──────────────────────────────────────────
if [ ! -f "$LOCALMD_PATH" ]; then
    log_red "ERROR: $LOCALMD_PATH not found."
    log_yellow "See https://github.com/erikcheatham/gscript/blob/main/docs/LOCALMD.md for the localmd convention."
    exit 1
fi
PAT=$(grep -oE 'github_pat_[A-Za-z0-9_]{40,}' "$LOCALMD_PATH" | head -n 1 || true)
if [ -z "$PAT" ]; then
    log_red "ERROR: No PAT matching 'github_pat_...' found in $LOCALMD_PATH."
    log_yellow "See https://github.com/erikcheatham/gscript/blob/main/docs/PAT-SETUP.md for scoping."
    exit 1
fi

# ── Trailing-null preflight ───────────────────────────────────────────
# Defends against the FUSE-mount trailing-null-padding gotcha. Iterates
# every text-extension file in FILES_TO_STAGE, refuses to push if any
# has trailing 0x00 bytes.
TEXT_EXTENSIONS_REGEX='\.(cs|razor|css|js|ts|html|md|json|yml|yaml|xml|csproj|props|targets|sln|slnx|ps1|py|sql|txt|sh|gitignore|editorconfig|env|rb|go|rs|java|kt|swift|c|h|cpp|hpp|jsx|tsx|vue|svelte|toml|ini|cfg|conf)$'

test_trailing_nulls() {
    # Returns 0 if clean, 1 if file has trailing nulls. Prints count to stdout.
    local path="$1"
    python3 -c "
import sys, pathlib
b = pathlib.Path('$path').read_bytes()
if not b:
    print(0); sys.exit(0)
i = len(b) - 1
n = 0
while i >= 0 and b[i] == 0:
    n += 1
    i -= 1
print(n)
sys.exit(1 if n > 0 else 0)
"
}

# ── Fetch + divergence check ──────────────────────────────────────────
PUSH_URL="https://x-access-token:${PAT}@github.com/${REPO_OWNER}/${REPO_NAME}.git"
log_cyan "Fetching origin/main via PAT-in-URL..."
if ! git_with_retry "git fetch" fetch --quiet "$PUSH_URL" main; then
    log_red "ERROR: git fetch failed after retries."
    log_yellow "Most likely the PAT in localmd has expired or lacks Contents:R/W."
    exit 1
fi
AHEAD=$(git rev-list HEAD ^FETCH_HEAD --count)
BEHIND=$(git rev-list FETCH_HEAD ^HEAD --count)
echo "  local is $AHEAD ahead, $BEHIND behind origin/main"
if [ "$BEHIND" != "0" ]; then
    log_red "ERROR: origin/main is $BEHIND commit(s) ahead of local."
    log_red "Resolve manually: git pull --rebase origin main, then re-run."
    exit 1
fi

# ── CONFIGURE: stage explicit paths only ──────────────────────────────
# Replace with this sprint's actual files. NEVER `git add .` here.
FILES_TO_STAGE=(
    # "src/MyEntity.cs"
    # "src/MyEndpoint.cs"
)
if [ ${#FILES_TO_STAGE[@]} -eq 0 ]; then
    log_red "ERROR: FILES_TO_STAGE is empty. Edit this script first."
    exit 1
fi

# Trailing-null check on every text-extension file
log_cyan "Pre-flight: trailing-null check..."
null_fails=()
for f in "${FILES_TO_STAGE[@]}"; do
    full_path="$SCRIPT_DIR/$f"
    [ ! -f "$full_path" ] && continue
    if echo "$f" | grep -qE "$TEXT_EXTENSIONS_REGEX"; then
        if count=$(test_trailing_nulls "$full_path"); then
            log_green "  OK  $f"
        else
            log_red "  FAIL $f ($count trailing 0x00 bytes)"
            null_fails+=("$f")
        fi
    fi
done
if [ ${#null_fails[@]} -gt 0 ]; then
    log_red "ERROR: trailing nulls detected. Strip with:"
    log_yellow "  python3 -c \"import pathlib; p='<file>'; pathlib.Path(p).write_bytes(pathlib.Path(p).read_bytes().rstrip(b'\\\\x00'))\""
    exit 1
fi

# ── CONFIGURE: per-sprint validators (JSON parse example) ─────────────
JSON_VALIDATE_FILES=(
    # "src/appsettings.json"
)
if [ ${#JSON_VALIDATE_FILES[@]} -gt 0 ]; then
    log_cyan "Pre-flight: JSON parse..."
    for f in "${JSON_VALIDATE_FILES[@]}"; do
        full_path="$SCRIPT_DIR/$f"
        if [ ! -f "$full_path" ]; then
            log_yellow "  SKIP $f (not found)"
            continue
        fi
        if python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$full_path" 2>/dev/null; then
            bytes=$(wc -c < "$full_path")
            log_green "  OK  $f ($bytes bytes)"
        else
            log_red "  FAIL $f - invalid JSON"
            log_red "ERROR: refusing to push; fix file first."
            exit 1
        fi
    done
fi

# Stage files (with retry wrapper)
log_cyan "Staging files..."
for f in "${FILES_TO_STAGE[@]}"; do
    full_path="$SCRIPT_DIR/$f"
    if [ ! -f "$full_path" ]; then
        log_yellow "  SKIP $f (not found)"
        continue
    fi
    if ! git_with_retry "git add $f" add -- "$f"; then
        log_red "ERROR: git add failed for $f after retries"
        exit 1
    fi
done

# ── Audit what was staged ─────────────────────────────────────────────
staged_files=$(git diff --cached --name-only)
staged_count=$(echo "$staged_files" | grep -c . || true)
log_cyan "Files staged: $staged_count"
echo "$staged_files" | while read -r line; do
    [ -n "$line" ] && log_gray "  $line"
done
if [ "$staged_count" -eq 0 ]; then
    log_red "ERROR: nothing was staged."
    exit 1
fi

# Refuse to commit if anything unexpected slipped in
expected_normalized=$(printf '%s\n' "${FILES_TO_STAGE[@]}")
unexpected=$(echo "$staged_files" | grep -vxF "$expected_normalized" || true)
if [ -n "$unexpected" ]; then
    log_yellow "WARNING: unexpected files staged (will commit anyway):"
    echo "$unexpected" | while read -r line; do
        [ -n "$line" ] && log_yellow "  $line"
    done
fi

# ── CONFIGURE: commit message via tempfile ────────────────────────────
COMMIT_MSG=$(cat <<'EOF'
<short imperative subject — what this commit does>

<paragraph explaining the why + any non-obvious decisions>
EOF
)
MSG_PATH=$(mktemp)
echo "$COMMIT_MSG" > "$MSG_PATH"

if ! git_with_retry "git commit" -c "user.name=$COMMIT_NAME" -c "user.email=$COMMIT_EMAIL" commit -F "$MSG_PATH"; then
    rm -f "$MSG_PATH"
    log_red "ERROR: git commit failed after retries"
    exit 1
fi
rm -f "$MSG_PATH"

COMMIT_SHA=$(git rev-parse HEAD)
SHORT_SHA="${COMMIT_SHA:0:7}"

# ── Push via PAT-in-URL ───────────────────────────────────────────────
log_cyan "Pushing to origin/main..."
if ! git_with_retry "git push" push "$PUSH_URL" main; then
    log_red "ERROR: git push failed after retries"
    exit 1
fi
echo ""
log_green "PUSHED: $SHORT_SHA to origin/main."

# ── CI watch with per-step granularity ────────────────────────────────
format_duration() {
    local started="$1"
    local completed="$2"
    if [ -z "$started" ] || [ -z "$completed" ] || [ "$started" = "null" ] || [ "$completed" = "null" ]; then
        echo ""
        return
    fi
    local start_sec end_sec span
    # Try GNU date format first, fall back to BSD (macOS)
    start_sec=$(date -d "$started" +%s 2>/dev/null || date -j -f "%Y-%m-%dT%H:%M:%SZ" "$started" +%s 2>/dev/null || echo 0)
    end_sec=$(date -d "$completed" +%s 2>/dev/null || date -j -f "%Y-%m-%dT%H:%M:%SZ" "$completed" +%s 2>/dev/null || echo 0)
    span=$((end_sec - start_sec))
    if [ $span -lt 60 ]; then
        echo "${span}s"
    else
        echo "$((span / 60))m $((span % 60))s"
    fi
}

if [ "$WATCH_CI" = true ]; then
    echo ""
    log_cyan "Watching CI for $CI_WORKFLOW_FILE (commit $SHORT_SHA, max $CI_WATCH_MAX_MINUTES min)..."
    api_base="https://api.github.com/repos/$REPO_OWNER/$REPO_NAME"
    deadline=$(($(date +%s) + CI_WATCH_MAX_MINUTES * 60))
    run_id=""
    last_status=""
    last_conclusion=""
    declare -A step_states
    declare -A job_headers_printed

    while [ "$(date +%s)" -lt "$deadline" ]; do
        runs_json=$(curl -sS -H "Accept: application/vnd.github+json" \
                           -H "Authorization: Bearer $PAT" \
                           -H "X-GitHub-Api-Version: 2022-11-28" \
                           --max-time 15 \
                           "$api_base/actions/workflows/$CI_WORKFLOW_FILE/runs?head_sha=$COMMIT_SHA&per_page=1" 2>/dev/null || echo "{}")

        total_count=$(echo "$runs_json" | jq -r '.total_count // 0' 2>/dev/null || echo "0")
        if [ "$total_count" -gt 0 ]; then
            run_id=$(echo "$runs_json" | jq -r '.workflow_runs[0].id')
            status=$(echo "$runs_json" | jq -r '.workflow_runs[0].status')
            conclusion=$(echo "$runs_json" | jq -r '.workflow_runs[0].conclusion // ""')
            html_url=$(echo "$runs_json" | jq -r '.workflow_runs[0].html_url')

            if [ "$status" != "$last_status" ] || [ "$conclusion" != "$last_conclusion" ]; then
                ts=$(date "+%H:%M:%S")
                msg="  [$ts] status=$status"
                [ -n "$conclusion" ] && msg+=" conclusion=$conclusion"
                if [ "$conclusion" = "success" ]; then
                    log_green "$msg"
                elif [ -n "$conclusion" ]; then
                    log_red "$msg"
                else
                    log_cyan "$msg"
                fi
                last_status="$status"
                last_conclusion="$conclusion"
            fi

            # Per-job per-step granularity (needs Actions: Read PAT scope)
            if [ -n "$run_id" ] && [ "$run_id" != "null" ]; then
                jobs_json=$(curl -sS -H "Accept: application/vnd.github+json" \
                                   -H "Authorization: Bearer $PAT" \
                                   -H "X-GitHub-Api-Version: 2022-11-28" \
                                   --max-time 15 \
                                   "$api_base/actions/runs/$run_id/jobs" 2>/dev/null || echo "{}")

                # Loop jobs
                job_count=$(echo "$jobs_json" | jq -r '.total_count // 0' 2>/dev/null || echo "0")
                if [ "$job_count" -gt 0 ]; then
                    while IFS=$'\t' read -r job_name job_status step_idx step_name step_status step_conclusion step_started step_completed; do
                        # Print job header once when it leaves queued state
                        if [ -z "${job_headers_printed[$job_name]:-}" ] && [ "$job_status" != "queued" ]; then
                            log_cyan "  $job_name job:"
                            job_headers_printed[$job_name]=1
                        fi

                        step_key="${job_name}::${step_idx}"
                        cur="$step_status|$step_conclusion"
                        prev="${step_states[$step_key]:-}"
                        if [ "$cur" != "$prev" ]; then
                            ts=$(date "+%H:%M:%S")
                            if [ "$step_status" = "completed" ]; then
                                dur=$(format_duration "$step_started" "$step_completed")
                                case "$step_conclusion" in
                                    success) icon="OK "; color="$C_GREEN" ;;
                                    skipped) icon="-- "; color="$C_GRAY" ;;
                                    *)       icon="XX "; color="$C_RED" ;;
                                esac
                                line="    [$ts] $icon $step_name ($dur)"
                                if [ "$step_conclusion" != "success" ] && [ "$step_conclusion" != "skipped" ]; then
                                    line+=" [conclusion=$step_conclusion]"
                                fi
                                printf "%s%s%s\n" "$color" "$line" "$C_RESET"
                            elif [ "$step_status" = "in_progress" ]; then
                                printf "%s    [%s] >> %s ...%s\n" "$C_YELLOW" "$ts" "$step_name" "$C_RESET"
                            fi
                            step_states[$step_key]="$cur"
                        fi
                    done < <(echo "$jobs_json" | jq -r '
                        .jobs[]? | . as $j |
                        .steps[]? |
                        [$j.name, $j.status, .number, .name, .status, (.conclusion // ""), (.started_at // ""), (.completed_at // "")]
                        | @tsv
                    ' 2>/dev/null)
                fi
            fi

            if [ "$status" = "completed" ]; then
                echo ""
                if [ "$conclusion" = "success" ]; then
                    log_green "CI GREEN: $html_url"
                else
                    log_red "CI ${conclusion^^}: $html_url"
                    log_yellow "Inspect failure logs at the URL above."
                    exit 1
                fi
                break
            fi
        fi

        sleep "$CI_WATCH_POLL_SECONDS"
    done

    if [ -z "$run_id" ] || [ "$last_status" != "completed" ]; then
        log_yellow "TIMEOUT: CI didn't complete within $CI_WATCH_MAX_MINUTES min. Check manually:"
        log_yellow "  https://github.com/$REPO_OWNER/$REPO_NAME/actions"
        exit 1
    fi

    # ── Post-deploy probes ────────────────────────────────────────────
    if [ ${#PROBE_ENDPOINTS[@]} -gt 0 ]; then
        echo ""
        log_cyan "Probing post-deploy endpoints..."
        probe_fails=()
        for entry in "${PROBE_ENDPOINTS[@]}"; do
            url="${entry%%|*}"
            range="${entry##*|}"
            min="${range%%-*}"
            max="${range##*-}"
            code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 "$url" 2>/dev/null || echo "0")
            if [ "$code" -ge "$min" ] && [ "$code" -le "$max" ]; then
                log_green "  $url -> $code (expected $min-$max)"
            else
                log_red "  $url -> $code (expected $min-$max)"
                probe_fails+=("$url")
            fi
        done
        if [ ${#probe_fails[@]} -gt 0 ]; then
            log_red "PROBE FAIL: ${#probe_fails[@]} endpoint(s) outside expected range."
            log_yellow "Staging may still be warming up — try probing manually in 30s."
            exit 1
        fi
        log_green "ALL PROBES GREEN."
    fi
fi

echo ""
log_green "SUCCESS: sprint shipped end-to-end (push + CI green + probes pass)."

# Self-delete per the gscript convention
rm -f "${BASH_SOURCE[0]}"
