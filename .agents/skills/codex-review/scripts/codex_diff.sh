#!/usr/bin/env bash
# 변경분 검증: 변경 파일 목록과 diff 명령을 주고 `codex exec`(읽기 전용, Schema 응답)로 한 번 실행한다.
# Codex 내장 `codex review`는 --commit/--base와 사용자 지시문을 함께 받지 않아(0.154.0),
# 이미 알려진 제한·맥락을 넘길 수 없으므로 쓰지 않는다.
#
# 사용: codex_diff.sh <target_repo> <workspace_dir> uncommitted
#       codex_diff.sh <target_repo> <workspace_dir> commit <sha>
#       codex_diff.sh <target_repo> <workspace_dir> base <ref>     # <ref>..HEAD
# 입력: <workspace_dir>/00_request.md (맥락)
# 출력: diff_files.txt, 01_diff_prompt.md, 01_diff_result.json, 01_diff.log, runs.tsv에 한 줄
# 종료 코드: Codex 종료 코드. Codex가 성공했어도 변경 파일을 다 읽지 않았으면 3.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

repo="${1:?target_repo}"; ws="${2:?workspace_dir}"; mode="${3:?uncommitted|commit|base}"; arg="${4:-}"
mkdir -p "$ws"; ws="$(cd "$ws" && pwd)"
case "$mode" in
  uncommitted) diffcmd="git diff HEAD (staged + unstaged), 새 파일은 목록의 경로를 직접 읽는다"
               git -C "$repo" status --porcelain | awk '{print $NF}' > "$ws/diff_files.txt" ;;
  commit) : "${arg:?sha}"; diffcmd="git show $arg"
          git -C "$repo" show --name-only --format= "$arg" > "$ws/diff_files.txt" ;;
  base) : "${arg:?ref}"; diffcmd="git diff $arg..HEAD"
        git -C "$repo" diff --name-only "$arg..HEAD" > "$ws/diff_files.txt" ;;
  *) echo "unknown mode $mode" >&2; exit 2 ;;
esac
# 사용자 소유 하네스 파일은 기본 범위에서 뺀다(SKILL.md 1단계).
grep -vE '^(\.claude/|\.agents/|\.codex/|CLAUDE\.md$|AGENTS\.md$|_workspace)' "$ws/diff_files.txt" | grep . > "$ws/diff_files.tmp" || true
mv "$ws/diff_files.tmp" "$ws/diff_files.txt"
[ -s "$ws/diff_files.txt" ] || { echo "no changed files in scope" >&2; exit 2; }

prompt="$ws/01_diff_prompt.md"
{
  cat "$SKILL_DIR/references/prompt-diff.md"
  printf '\n## 변경 범위\n\n- diff 명령: `%s`\n- 변경 파일(모두 끝까지 본다):\n' "$diffcmd"
  sed 's/^/  - /' "$ws/diff_files.txt"
  if [ -f "$ws/00_request.md" ]; then printf '\n## 맥락\n\n'; cat "$ws/00_request.md"; fi
  printf '\n`area` 값에는 "diff"를 쓴다. `files_read`에는 변경 파일 중 diff와 주변 코드를 끝까지 본 파일을 적는다.\n'
} > "$prompt"

log="$ws/01_diff.log"; out="$ws/01_diff_result.json"
rm -f "$out"
set +e
timeout "$CODEX_TIMEOUT_SECONDS" codex exec \
  -C "$repo" \
  -s read-only \
  -c model_reasoning_effort="\"$CODEX_EFFORT\"" \
  --color never \
  --output-schema "$SKILL_DIR/assets/area-findings.schema.json" \
  -o "$out" \
  - < "$prompt" > "$log" 2>&1
code=$?
set -e
record_run "$ws" "diff:$mode${arg:+:$arg}" "$code" "$log"
echo "codex diff exit=$code effort=$CODEX_EFFORT"
[ "$code" -eq 0 ] && [ -s "$out" ] || { tail -n 15 "$log"; exit "$code"; }

# 변경 파일을 다 읽었는지 대조한다.
python - "$ws" <<'PY'
import json, os, sys
ws = sys.argv[1]
listed = {l.strip().replace("\\", "/") for l in open(os.path.join(ws, "diff_files.txt"), encoding="utf-8") if l.strip()}
r = json.load(open(os.path.join(ws, "01_diff_result.json"), encoding="utf-8"))
read = {f.replace("\\", "/").lstrip("./") for f in r.get("files_read", [])} & listed
missing = sorted(listed - read)
print(f"read {len(read)}/{len(listed)} changed files, findings {len(r.get('findings', []))}")
if missing:
    print("not read:", ", ".join(missing))
sys.exit(3 if missing else 0)
PY
