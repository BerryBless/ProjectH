#!/usr/bin/env bash
# codex-review 스크립트 공용 설정과 도우미. 다른 스크립트가 source한다.

SKILL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# reasoning effort: 사용자가 high로 정했다(2026-10-03). 한 번만 바꾸려면 CODEX_EFFORT=xhigh 처럼 환경 변수로 준다.
CODEX_EFFORT="${CODEX_EFFORT:-high}"
# 영역 하나 또는 변경분 리뷰 한 번의 제한 시간(초). 넘으면 timeout이 124로 끝낸다.
CODEX_TIMEOUT_SECONDS="${CODEX_TIMEOUT_SECONDS:-1200}"

# Codex 로그에서 session id와 사용 토큰을 뽑아 runs.tsv에 한 줄 남긴다. Codex 기록(codex resume)과 대조할 때 쓴다.
# 사용: record_run <ws> <label> <exit_code> <log_file>
record_run() {
  local ws="$1" label="$2" code="$3" log="$4" sid tokens
  sid=$(grep -m1 '^session id:' "$log" 2>/dev/null | awk '{print $3}')
  tokens=$(grep -A1 '^tokens used' "$log" 2>/dev/null | tail -1 | tr -d ', \r')
  [ -f "$ws/runs.tsv" ] || printf 'label\texit\tsession\ttokens\tlog\n' > "$ws/runs.tsv"
  printf '%s\t%s\t%s\t%s\t%s\n' "$label" "$code" "${sid:--}" "${tokens:--}" "$(basename "$log")" >> "$ws/runs.tsv"
}
