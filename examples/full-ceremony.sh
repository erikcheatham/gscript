#!/usr/bin/env bash
# gscript FULL CEREMONY EXAMPLE (bash)
#
# Bash port of full-ceremony.ps1. Multi-file stage, JSON validator,
# CI watch with per-step granularity, multi-endpoint post-deploy probe.
# Requires bash 4+, curl, jq, python3.

set -euo pipefail

COMMIT_NAME="ai-bot"
COMMIT_EMAIL="ai-bot@example.com"
REPO_OWNER="your-github-username"
REPO_NAME="your-repo-name"
CI_WORKFLOW_FILE="deploy.yml"
WATCH_CI=true
CI_WATCH_MAX_MINUTES=15
CI_WATCH_POLL_SECONDS=20

PROBE_ENDPOINTS=(
    "https://staging.your-app.example.com/|200-399"
    "https://staging.your-app.example.com/api/v1/health|200-399"
)

LOCALMD_PATH="${HOME}/private/local.md"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Color helpers
if [ -t 1 ]; then
    C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_RED=$'\033[31m'
    C_YELLOW=$'\033[33m'; C_GRAY=$'\033[90m'; C_RESET=$'\033[0m'
else
    C_CYAN=""; C_GREEN=""; C_RED=""; C_YELLOW=""; C_GRAY=""; C_RESET=""
fi

# Stale-lock auto-recovery
clear_stale_locks() {
    local git_dir="$1"
    for lock in index.lock HEAD.lock config.lock packed-refs.lock shallow.lock fetch.lock; do
        if [ -f "$git_dir/$lock" ]; then
            if [ -z "$(pgrep -x git 2>/dev/null || true)" ]; then
                echo "${C_YELLOW}Removing stale lock: $lock${C_RESET}"
                rm -f "$git_dir/$lock"
            else
                echo "${C_YELLOW}Locks present and git processes running. Wait + retry.${C_RESET}" >&2
                exit 1
            fi
        fi
    done
}
clear_stale_locks "$SCRIPT_DIR/.git"

# Git retry wrapper
git_with_retry() {
    local context="$1"; shift
    local max_attempts=3 delay=1
    for attempt in $(seq 1 $max_attempts); do
        local stderr
        if stderr=$(git "$@" 2>&1); then
            [ -n "$stderr" ] && echo "$stderr"
            return 0
        fi
        if echo "$stderr" | grep -qE "index\.lock|HEAD\.lock|Unable to create"; then
            if [ $attempt -lt $max_attempts ]; then
                echo "${C_YELLOW}  $context retry $attempt/$max_attempts in ${delay}s...${C_RESET}"
                sleep $delay
                clear_stale_locks "$SCRIPT_DIR/.git"
                delay=$((delay * 2))
                continue
            fi
        fi
        echo "${C_RED}$stderr${C_RESET}" >&2
        return 1
    done
    return 1
}

# PAT from localmd
if [ ! -f "$LOCALMD_PATH" ]; then
    echo "${C_RED}ERROR: $LOCALMD_PATH not found.${C_RESET}" >&2
    exit 1
fi
PAT=$(grep -oE 'github_pat_[A-Za-z0-9_]{40,}' "$LOCALMD_PATH" | head -n 1 || true)
if [ -z "$PAT" ]; then
    echo "${C_RED}ERROR: No PAT in localmd.${C_RESET}" >&2
    exit 1
fi

PUSH_URL="https://x-access-token:${PAT}@github.com/${REPO_OWNER}/${REPO_NAME}.git"

# Fetch
echo "${C_CYAN}Fetching origin/main...${C_RESET}"
if ! git_with_retry "git fetch" fetch --quiet "$PUSH_URL" main; then
    echo "${C_RED}ERROR: fetch failed.${C_RESET}" >&2
    exit 1
fi
AHEAD=$(git rev-list HEAD ^FETCH_HEAD --count)
BEHIND=$(git rev-list FETCH_HEAD ^HEAD --count)
echo "  $AHEAD ahead, $BEHIND behind origin/main"
if [ "$BEHIND" != "0" ]; then
    echo "${C_RED}ERROR: behind origin/main; pull --rebase first.${C_RESET}" >&2
    exit 1
fi

# Files to stage
FILES_TO_STAGE=(
    "src/Modules/MyFeature/Domain/MyEntity.cs"
    "src/Modules/MyFeature/Application/MyEntityService.cs"
    "src/Modules/MyFeature/Infrastructure/MyEntityRepository.cs"
    "src/Web/Endpoints/MyFeatureEndpoint.cs"
    "src/Web/Pages/MyFeaturePage.razor"
    "src/Web/Pages/MyFeaturePage.razor.css"
    "src/Web/appsettings.json"
)

# Trailing-null check
echo "${C_CYAN}Trailing-null check...${C_RESET}"
TEXT_REGEX='\.(cs|razor|css|md|json|yml|ps1)$'
NULL_FAILS=()
for f in "${FILES_TO_STAGE[@]}"; do
    full="$SCRIPT_DIR/$f"
    [ ! -f "$full" ] && continue
    if echo "$f" | grep -qE "$TEXT_REGEX"; then
        n=$(python3 -c "
import pathlib
b = pathlib.Path('$full').read_bytes()
i = len(b) - 1; n = 0
while i >= 0 and b[i] == 0: n += 1; i -= 1
print(n)
")
        if [ "$n" -gt 0 ]; then
            echo "${C_RED}  FAIL $f ($n trailing 0x00 bytes)${C_RESET}"
            NULL_FAILS+=("$f")
        else
            echo "${C_GREEN}  OK  $f${C_RESET}"
        fi
    fi
done
[ ${#NULL_FAILS[@]} -gt 0 ] && exit 1

# JSON parse
echo "${C_CYAN}JSON parse...${C_RESET}"
for f in src/Web/appsettings.json; do
    full="$SCRIPT_DIR/$f"
    [ ! -f "$full" ] && continue
    if python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$full" 2>/dev/null; then
        echo "${C_GREEN}  OK  $f${C_RESET}"
    else
        echo "${C_RED}  FAIL $f - invalid JSON${C_RESET}"
        exit 1
    fi
done

# Stage
echo "${C_CYAN}Staging...${C_RESET}"
for f in "${FILES_TO_STAGE[@]}"; do
    full="$SCRIPT_DIR/$f"
    if [ ! -f "$full" ]; then
        echo "${C_YELLOW}  SKIP $f${C_RESET}"
        continue
    fi
    git_with_retry "git add $f" add -- "$f"
done

staged=$(git diff --cached --name-only)
staged_count=$(echo "$staged" | grep -c . || true)
echo "${C_CYAN}Files staged: $staged_count${C_RESET}"
echo "$staged" | while read -r line; do
    [ -n "$line" ] && echo "${C_GRAY}  $line${C_RESET}"
done
[ "$staged_count" -eq 0 ] && exit 1

# Commit
MSG=$(cat <<'EOF'
feat(my-feature): add MyEntity with full CRUD + endpoint + UI page

Standard four-project pattern for new domain primitives:
- MyEntity domain class
- IMyEntityService application seam
- MyEntityRepository infrastructure impl
- Carter endpoint at /api/v1/my-feature
- Razor page at /my-feature with theme integration

appsettings.json gains MyFeature config section with default-on policy.

Migration not included in this commit (additive entity goes through the
auto-migrate-at-boot DbInitializer; existing rows untouched).
EOF
)
MSG_PATH=$(mktemp)
echo "$MSG" > "$MSG_PATH"
if ! git_with_retry "git commit" -c "user.name=$COMMIT_NAME" -c "user.email=$COMMIT_EMAIL" commit -F "$MSG_PATH"; then
    rm -f "$MSG_PATH"
    echo "${C_RED}ERROR: commit failed.${C_RESET}" >&2
    exit 1
fi
rm -f "$MSG_PATH"

COMMIT_SHA=$(git rev-parse HEAD)
SHORT_SHA="${COMMIT_SHA:0:7}"

# Push
echo "${C_CYAN}Pushing...${C_RESET}"
git_with_retry "git push" push "$PUSH_URL" main
echo "${C_GREEN}PUSHED: $SHORT_SHA${C_RESET}"

# CI watch
format_duration() {
    local s="$1" e="$2"
    [ -z "$s" ] || [ -z "$e" ] || [ "$s" = "null" ] || [ "$e" = "null" ] && { echo ""; return; }
    local ss es span
    ss=$(date -d "$s" +%s 2>/dev/null || date -j -f "%Y-%m-%dT%H:%M:%SZ" "$s" +%s 2>/dev/null || echo 0)
    es=$(date -d "$e" +%s 2>/dev/null || date -j -f "%Y-%m-%dT%H:%M:%SZ" "$e" +%s 2>/dev/null || echo 0)
    span=$((es - ss))
    if [ $span -lt 60 ]; then echo "${span}s"; else echo "$((span / 60))m $((span % 60))s"; fi
}

if [ "$WATCH_CI" = true ]; then
    echo ""
    echo "${C_CYAN}Watching CI...${C_RESET}"
    api_base="https://api.github.com/repos/$REPO_OWNER/$REPO_NAME"
    deadline=$(($(date +%s) + CI_WATCH_MAX_MINUTES * 60))
    run_id=""; last_status=""; last_conclusion=""
    declare -A step_states job_headers

    while [ "$(date +%s)" -lt "$deadline" ]; do
        runs_json=$(curl -sS -H "Accept: application/vnd.github+json" \
                           -H "Authorization: Bearer $PAT" \
                           -H "X-GitHub-Api-Version: 2022-11-28" \
                           --max-time 15 \
                           "$api_base/actions/workflows/$CI_WORKFLOW_FILE/runs?head_sha=$COMMIT_SHA&per_page=1" 2>/dev/null || echo "{}")

        total=$(echo "$runs_json" | jq -r '.total_count // 0' 2>/dev/null || echo "0")
        if [ "$total" -gt 0 ]; then
            run_id=$(echo "$runs_json" | jq -r '.workflow_runs[0].id')
            status=$(echo "$runs_json" | jq -r '.workflow_runs[0].status')
            conclusion=$(echo "$runs_json" | jq -r '.workflow_runs[0].conclusion // ""')
            html_url=$(echo "$runs_json" | jq -r '.workflow_runs[0].html_url')

            if [ "$status" != "$last_status" ] || [ "$conclusion" != "$last_conclusion" ]; then
                ts=$(date "+%H:%M:%S")
                msg="  [$ts] status=$status"
                [ -n "$conclusion" ] && msg+=" conclusion=$conclusion"
                if [ "$conclusion" = "success" ]; then echo "${C_GREEN}$msg${C_RESET}"
                elif [ -n "$conclusion" ]; then echo "${C_RED}$msg${C_RESET}"
                else echo "${C_CYAN}$msg${C_RESET}"
                fi
                last_status="$status"; last_conclusion="$conclusion"
            fi

            if [ -n "$run_id" ] && [ "$run_id" != "null" ]; then
                jobs_json=$(curl -sS -H "Accept: application/vnd.github+json" \
                                   -H "Authorization: Bearer $PAT" \
                                   -H "X-GitHub-Api-Version: 2022-11-28" \
                                   --max-time 15 \
                                   "$api_base/actions/runs/$run_id/jobs" 2>/dev/null || echo "{}")
                if [ "$(echo "$jobs_json" | jq -r '.total_count // 0')" -gt 0 ]; then
                    while IFS=$'\t' read -r jn js si sn ss sc st sct; do
                        if [ -z "${job_headers[$jn]:-}" ] && [ "$js" != "queued" ]; then
                            echo "${C_CYAN}  $jn job:${C_RESET}"
                            job_headers[$jn]=1
                        fi
                        k="${jn}::${si}"; cur="$ss|$sc"; prev="${step_states[$k]:-}"
                        if [ "$cur" != "$prev" ]; then
                            ts=$(date "+%H:%M:%S")
                            if [ "$ss" = "completed" ]; then
                                dur=$(format_duration "$st" "$sct")
                                case "$sc" in
                                    success) icon="OK "; col="$C_GREEN" ;;
                                    skipped) icon="-- "; col="$C_GRAY" ;;
                                    *)       icon="XX "; col="$C_RED" ;;
                                esac
                                line="    [$ts] $icon $sn ($dur)"
                                [ "$sc" != "success" ] && [ "$sc" != "skipped" ] && line+=" [conclusion=$sc]"
                                echo "${col}${line}${C_RESET}"
                            elif [ "$ss" = "in_progress" ]; then
                                echo "${C_YELLOW}    [$ts] >> $sn ...${C_RESET}"
                            fi
                            step_states[$k]="$cur"
                        fi
                    done < <(echo "$jobs_json" | jq -r '.jobs[]? | . as $j | .steps[]? | [$j.name, $j.status, .number, .name, .status, (.conclusion // ""), (.started_at // ""), (.completed_at // "")] | @tsv' 2>/dev/null)
                fi
            fi

            if [ "$status" = "completed" ]; then
                echo ""
                if [ "$conclusion" = "success" ]; then
                    echo "${C_GREEN}CI GREEN: $html_url${C_RESET}"
                else
                    echo "${C_RED}CI ${conclusion^^}: $html_url${C_RESET}"
                    exit 1
                fi
                break
            fi
        fi
        sleep "$CI_WATCH_POLL_SECONDS"
    done

    # Probes
    if [ ${#PROBE_ENDPOINTS[@]} -gt 0 ]; then
        echo ""
        echo "${C_CYAN}Probing...${C_RESET}"
        for entry in "${PROBE_ENDPOINTS[@]}"; do
            url="${entry%%|*}"; range="${entry##*|}"
            min="${range%%-*}"; max="${range##*-}"
            code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 "$url" 2>/dev/null || echo "0")
            if [ "$code" -ge "$min" ] && [ "$code" -le "$max" ]; then
                echo "${C_GREEN}  $url -> $code${C_RESET}"
            else
                echo "${C_RED}  $url -> $code${C_RESET}"
                exit 1
            fi
        done
    fi
fi

echo ""
echo "${C_GREEN}SUCCESS.${C_RESET}"
rm -f "${BASH_SOURCE[0]}"
