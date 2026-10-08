#!/usr/bin/env bash
# 전체 검증의 영역 하나: 영역 파일 목록 전부를 읽게 하고, 결과를 Schema(JSON) 형식으로 받는다.
#
# 사용: codex_area.sh <target_repo> <workspace_dir> <area_id>
# 입력: <workspace_dir>/area_<id>_files.txt (plan_areas.py가 만든다), <workspace_dir>/00_request.md (맥락)
# 출력: <workspace_dir>/area_<id>_result.json, area_<id>.log, runs.tsv에 한 줄
# 읽기 전용(-s read-only)이고 세션은 Codex 기록에 남는다.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

repo="${1:?target_repo}"; ws="${2:?workspace_dir}"; id="${3:?area_id}"
ws="$(cd "$ws" && pwd)"
files="$ws/area_${id}_files.txt"
[ -s "$files" ] || { echo "missing $files" >&2; exit 2; }

prompt="$ws/area_${id}_prompt.md"
{
  cat "$SKILL_DIR/references/prompt-area.md"
  printf '\n## 이 영역 (%s): 아래 파일을 모두 끝까지 읽는다\n\n' "$id"
  sed 's/^/- /' "$files"
  if [ -f "$ws/00_request.md" ]; then printf '\n## 맥락\n\n'; cat "$ws/00_request.md"; fi
  printf '\n`area` 값에는 "%s"를 쓴다.\n' "$id"
} > "$prompt"

log="$ws/area_${id}.log"; out="$ws/area_${id}_result.json"
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
record_run "$ws" "area:$id" "$code" "$log"
echo "area $id exit=$code"
exit $code
