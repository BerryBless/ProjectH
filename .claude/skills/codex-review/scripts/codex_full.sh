#!/usr/bin/env bash
# 전체 검증: 영역을 나누고, 영역마다 codex_area.sh를 최대 N개씩 동시에 실행한 뒤 결과를 모은다.
#
# 사용: codex_full.sh <target_repo> <workspace_dir> <scope> [parallel=3] [max_lines=2500]
#   scope: server | client | shared | all | <경로 접두어,...>
#   이미 영역 결과가 있는 workspace에 다시 실행하면, 결과 파일이 없거나 실패한 영역만 다시 돈다(ONLY_AREAS로 직접 지정도 가능).
# 출력: <workspace_dir>/areas.json, area_*, runs.tsv, 02_summary.json (summarize.py)
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

repo="${1:?target_repo}"; ws="${2:?workspace_dir}"; scope="${3:?scope}"; par="${4:-3}"; max_lines="${5:-2500}"
mkdir -p "$ws"; ws="$(cd "$ws" && pwd)"
here="$(dirname "${BASH_SOURCE[0]}")"

[ -f "$ws/areas.json" ] || python "$here/plan_areas.py" "$repo" "$ws" "$scope" "$max_lines" || exit 2

if [ -n "${ONLY_AREAS:-}" ]; then
  ids=$(echo "$ONLY_AREAS" | tr ', ' '\n\n' | grep -v '^$')
else
  ids=$(python -c "import json,sys,os; ws=sys.argv[1]
for a in json.load(open(os.path.join(ws,'areas.json'),encoding='utf-8')):
    p=os.path.join(ws,'area_%s_result.json'%a['id'])
    if not (os.path.exists(p) and os.path.getsize(p)>0): print(a['id'])" "$ws")
fi
# Windows의 Python은 줄 끝에 \r을 붙인다. 남겨 두면 영역 id가 "a01\r"이 되어 파일을 찾지 못한다.
ids=$(printf '%s\n' "$ids" | tr -d '\r' | grep -v '^$')
n=$(echo "$ids" | grep -c . || true)
echo "running $n area(s), $par at a time, effort=$CODEX_EFFORT, timeout=${CODEX_TIMEOUT_SECONDS}s each"
[ "$n" -gt 0 ] && echo "$ids" | xargs -P "$par" -I{} bash "$here/codex_area.sh" "$repo" "$ws" {}

python "$here/summarize.py" "$ws"
