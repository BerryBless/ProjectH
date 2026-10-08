---
name: codex-review
description: "Codex CLI(읽기 전용, reasoning effort high)로 ProjectH 코드를 검증한다. 변경분은 diff 범위의 파일을, 서버·클라이언트 전체는 영역을 나눠 3개씩 동시에 `codex exec`로 끝까지 읽게 한다. Codex가 낸 지적은 game-reviewer가 하나씩 반박 검증하고, 확인된 것만 보고한다. Codex 세션은 기록에 남겨 session id와 토큰을 함께 보고한다. 사용자 입력에 '코덱스'가 명시적으로 있을 때만 쓴다: '코덱스', '코덱스 <commit>', '코덱스 main..HEAD', '코덱스 전체', '코덱스 서버 전체', '코덱스 클라 전체', '코덱스 다시', '코덱스 이 영역만 다시', '코덱스 검증하고 고쳐줘', '코덱스 문제 있으면 수정'. '코덱스'가 없는 '리뷰해줘', '검토해줘', '서버리뷰', '성능점검', 'QA', 'codex'에는 쓰지 않는다(각각 직접 처리, game-dev-orchestrator, server-review)."
---

# Codex 검증

Codex는 Claude와 다른 모델이라 다른 관점에서 코드를 본다. 이 하네스는 Codex가 **실제로 코드를 끝까지 읽게** 하고, Codex가 낸 지적을 **그대로 믿지 않고** 검증한 뒤 보고한다.

이 하네스는 이전 `codex-crosscheck`를 대신한다. 그 하네스에는 문제가 넷 있었다.
- reasoning effort가 none이었다.
- 지적이 최대 3건이었다.
- 제한 시간이 540초였다.
- `--ephemeral` 때문에 Codex 기록이 남지 않았다.

그 결과 서버 16,400줄 중 12개 파일만 훑고 끝났다. 그래서 이 하네스는 다음 네 가지를 지킨다.
- **끝까지 읽기:** 전체 검증은 영역을 약 2,500줄 단위로 나누고, 영역마다 파일 목록 전부를 읽게 한다. Codex가 낸 `files_read`를 목록과 대조해 빠진 파일이 있으면 다시 돌린다.
- **충분한 깊이:** reasoning effort는 `high`다. 사용자가 2026-10-03에 정했다. 한 번만 바꾸려면 `CODEX_EFFORT=xhigh`처럼 환경 변수를 준다.
- **기록:** `--ephemeral`을 쓰지 않는다. 모든 실행의 session id와 토큰을 `runs.tsv`에 남기고 보고에 적는다. 사용자가 Codex 쪽 기록(`codex resume <id>`, `~/.codex/sessions`)과 대조할 수 있어야 한다.
- **읽기 전용:** Codex는 파일을 바꾸지 않는다(모든 실행이 `codex exec -s read-only`). 수정은 사용자가 요청했을 때만 Claude 쪽에서 한다.

## 실행 모드: 혼합

| 단계 | 실행 모드 | 이유 |
|------|----------|------|
| 1. 범위 확정, 요청서 | 리더 | 범위와 알려진 제한을 정해야 Codex가 같은 지적을 반복하지 않는다 |
| 2. Codex 실행 | `scripts/` (Bash) | 같은 옵션·같은 기록 형식으로 매번 실행하기 위해 스크립트로 고정했다 |
| 3. 지적 검증 | 워크플로 조율 (`game-reviewer` verify 모드) | 전체 검증은 지적이 수십 건일 수 있다. 판정 규칙을 코드로 고정한다 |
| 4. 보고 | 리더 | 근본 원인 묶기와 우선순위는 전체를 한 번에 보는 판단이 필요하다 |
| 5. 수정 (요청 시만) | `game-dev-orchestrator` 4단계 경로 | 일반 수정과 같은 규칙과 재검토를 쓴다 |

새 에이전트는 두지 않는다. 검증은 기존 `game-reviewer`(읽기 전용, verify 모드)가 맡는다.

## 0단계: 기존 작업 확인

산출물 폴더는 검증 대상 저장소 루트의 `_workspace_codex/`다. `.gitignore`의 `_workspace_*/` 규칙 때문에 커밋되지 않는다.

- 폴더가 없으면 처음부터 시작한다.
- 있고 같은 검증의 후속 요청이면 `00_request.md`, `runs.tsv`, `02_summary.json`을 읽고 필요한 단계만 한다.
  - "코덱스 다시": 전체 검증이면 `codex_full.sh`를 같은 workspace로 다시 실행한다. 결과가 없거나 실패한 영역만 돈다.
  - "이 영역만 다시": `ONLY_AREAS=a03,a05`를 준다.
  - "고쳐줘": 5단계로 간다.
  - 단, 그 사이 코드가 바뀌었으면(`git rev-parse HEAD`와 `git status --short`가 `00_request.md` 기록과 다르면) 새로 시작한다.
- 있고 새 검증이면 `_workspace_codex_{date +%Y%m%d-%H%M}/`로 옮기고 새로 시작한다.

**검증 대상 저장소:** 기본은 현재 작업 디렉터리의 저장소다. 다른 세션이 같은 작업 트리를 쓰고 있거나 특정 Commit을 봐야 하면 `git worktree add --detach <scratch>/codex-target <commit>`로 따로 만든 worktree를 대상으로 한다.

## 1단계: 범위 확정 (리더)

| 입력 | 모드 | 실행 |
|------|------|------|
| `코덱스` | 변경분 | 작업 트리 변경이 있으면 `codex_diff.sh <repo> <ws> uncommitted`. 없으면 직전 Commit: `commit HEAD` |
| `코덱스 <sha>` | 변경분 | `codex_diff.sh <repo> <ws> commit <sha>` |
| `코덱스 <ref>..HEAD`, `코덱스 Phase13` | 변경분 | `codex_diff.sh <repo> <ws> base <ref>`. 작업명이면 `git log --oneline --grep`으로 시작 Commit의 부모를 찾는다 |
| `코덱스 전체`, `코덱스 서버 전체` | 전체 | `codex_full.sh <repo> <ws> server 3` |
| `코덱스 클라 전체` | 전체 | `codex_full.sh <repo> <ws> client 3` |
| `코덱스 <경로> 전체` | 전체 | `codex_full.sh <repo> <ws> <경로,...> 3` |

"전체"라고만 하고 대상을 말하지 않으면 서버(`Server/src` + `Shared/Runtime`)로 잡고 한 줄로 알린다. 사용자 소유의 하네스 파일(`.claude/`, `.agents/`, `.codex/`, `CLAUDE.md`, `AGENTS.md`)은 사용자가 지정하지 않았으면 범위에서 뺀다.

`_workspace_codex/00_request.md`에는 다음만 적는다. 이 내용은 Codex 프롬프트의 "맥락"으로 붙는다.

```markdown
- HEAD: <git rev-parse --short HEAD>, 작업 트리: <깨끗함 / git status --short 요약>
- 목적: <한두 줄>
- 이미 한 검증: <Build/Test/리뷰 결과 한 줄씩, 없으면 "없음">
- 알려진 제한(다시 보고하지 않음): <이미 알려진 지적·의도한 trade-off 목록>
```

알려진 제한은 대화와 `Docs/`에 있는 이전 리뷰 결과(server-review 보고서, `Docs/Troubleshooting.md` 등)에서 가져온다. 이것을 적지 않으면 Codex가 이미 아는 문제를 다시 보고해 검증 비용만 든다.

## 2단계: Codex 실행

스크립트는 `.claude/skills/codex-review/scripts/`에 있다. Bash `timeout`과 `run_in_background`는 다음처럼 준다.

- **변경분:**
  - `bash <scripts>/codex_diff.sh <repo> <ws> <mode> [arg]`
  - Bash `timeout`은 1,500,000ms(25분)다. 길어질 수 있으니 `run_in_background: true`로 실행한다.
  - 결과는 `01_diff_result.json`(영역과 같은 Schema)이다. 그 `findings`가 검증 대상이다.
  - 종료 코드 3은 변경 파일을 다 읽지 않았다는 뜻이다. 한 번 더 실행하고, 그래도 남으면 보고서에 적는다.
  - Codex 내장 `codex review`는 쓰지 않는다. 0.154.0에서 `--commit`/`--base`와 사용자 지시문을 함께 받지 않아, 알려진 제한이나 맥락을 넘길 수 없기 때문이다(2026-10-03 확인).
- **전체:**
  - `bash <scripts>/codex_full.sh <repo> <ws> <scope> 3`
  - `run_in_background: true`로 실행한다. 영역이 8개면 3개씩 약 3라운드, 30–60분 걸린다.
  - 끝나면 `summarize.py`가 다음을 출력한다: 영역 표(파일 수, 읽은 수, 지적 수, 토큰, session), `02_summary.json`, 읽지 않은 파일 목록.
  - **범위 검사:** 실패한 영역이나 읽지 않은 파일이 있으면(종료 코드 1) 같은 명령을 한 번 더 실행한다. 결과가 없는 영역만 다시 돈다.
  - 그래도 남은 파일은 보고서의 "읽지 못한 파일"에 적는다. 다 읽었다고 보고하지 않는다.
- **실패:**
  - 종료 코드 124는 시간 초과다. 그 영역만 `CODEX_TIMEOUT_SECONDS=2400 ONLY_AREAS=<id>`로 한 번 더 돌린다.
  - 인증 만료(`codex login status`)나 사용량 한도는 다시 시도하지 않는다. 로그 끝 몇 줄과 함께 보고한다.
  - Codex가 실패했다고 리더가 대신 리뷰를 만들어 Codex 결과처럼 보고하지 않는다.

지적 형식은 `assets/area-findings.schema.json`의 `findings` 항목이다. 변경분은 `01_diff_result.json`의 `findings`를, 전체는 `02_summary.json`의 `findings`를 그대로 검증 단계에 넘긴다.

## 3단계: 지적 검증

Codex 지적은 자동으로 정답이 아니다. Critical·High·Medium 지적은 하나씩 `game-reviewer`(verify 모드)로 반박 근거부터 찾는다.
- 지적이 3건 이하면 리더가 실제 코드를 직접 읽어 판정해도 된다.
- Low는 검증하지 않고 "Low(검증 생략)"로 따로 적는다.

사용자가 "코덱스"를 직접 입력해 이 스킬을 불렀으므로 아래 Workflow 사용에 동의한 것으로 본다. `args`는 `{"root": "<대상 저장소 절대 경로>", "findings": <Medium 이상 지적 배열>, "verifiers": 1}`이다. 사용자가 "꼼꼼히", "철저히"라고 하면 `verifiers: 3`으로 두고 과반 판정한다.

```javascript
export const meta = {
  name: 'codex-verify',
  description: 'Codex 지적을 하나씩 반박 근거부터 찾아 검증한다',
  phases: [{ title: '검증', detail: '지적마다 game-reviewer가 실제 코드로 판정한다' }],
}
const VERDICT = { type: 'object', required: ['status', 'reason'], properties: {
  status: { type: 'string', enum: ['confirmed', 'refuted', 'uncertain'] },
  reason: { type: 'string' }, severity_adjust: { type: 'string' } } }
const findings = args.findings ?? []
const verifiers = args.verifiers ?? 1
const need = Math.floor(verifiers / 2) + 1
phase('검증')
const results = await parallel(findings.map(f => () =>
  parallel(Array.from({ length: verifiers }, (_, i) => () => agent(
    `[verify 모드] 검증자 ${i + 1}/${verifiers}. 코드 위치: ROOT = ${args.root} (이 경로에서만 읽는다). ` +
    `Codex가 낸 아래 지적을 실제 코드에서 확인하고 반박 근거(이미 막는 검증·상한·정리 경로)부터 찾아라. ` +
    `근거가 충분하면 confirmed, 반박되면 refuted, 판단할 수 없으면 uncertain. ` +
    `등급이 맞지 않으면 severity_adjust에 Critical/High/Medium/Low와 이유를 적어라.\n${JSON.stringify(f)}`,
    { agentType: 'game-reviewer', label: `검증:${f.file}:${f.line}`, phase: '검증', schema: VERDICT })))
    .then(vs => {
      const got = vs.filter(Boolean)
      const ok = got.filter(v => v.status === 'confirmed').length
      const no = got.filter(v => v.status === 'refuted').length
      // 검증자가 모두 실패하면 기각이 아니라 미검증이다. 기각과 섞으면 High 지적이 조용히 사라진다.
      const status = got.length === 0 ? 'unverified' : ok >= need ? 'confirmed' : no >= need ? 'rejected' : 'uncertain'
      return { ...f, status, verdicts: got }
    })))
const all = results.filter(Boolean)
const pick = s => all.filter(f => f.status === s)
log(`지적 ${findings.length}건: 확정 ${pick('confirmed').length}, 확실하지 않음 ${pick('uncertain').length}, 기각 ${pick('rejected').length}, 미검증 ${pick('unverified').length}`)
return { confirmed: pick('confirmed'), uncertain: pick('uncertain'), rejected: pick('rejected'), unverified: pick('unverified') }
```

결과는 `_workspace_codex/03_verified.json`에 저장한다. 미검증 지적은 리더가 코드를 직접 열어 판정한다. 검증이 실패했다는 이유로 버리지 않는다.

## 4단계: 보고

Codex 문장을 복사하지 않는다. 확인한 내용으로 다시 쓴다. 같은 원인의 지적은 한 건으로 묶는다.

```text
## Codex 검증

범위: <변경분 base..HEAD / 서버 전체 (N개 파일, M줄, 영역 K개)>
읽은 파일: <읽은 수>/<전체> (읽지 못한 파일: <목록 또는 없음>)
Codex 실행: <실행 수>회, effort high, 토큰 합계 <N>
  - <label> session <id> tokens <n>   (codex resume <id> 로 확인)
결과: 확정 <n> / 확실하지 않음 <n> / 기각 <n> / Low(검증 생략) <n>

### [High] <제목>
Location: <파일:줄>
Problem: <구체적인 문제>
Scenario: <실제 발생 경로>
Impact: <영향>
Suggested Fix: <짧은 수정 방향>
```

`확실하지 않음`이면 제목 옆에 `(확실하지 않음)`을 붙인다. 문제가 없으면 "확인된 문제 없음"과 범위·읽은 파일·실행 기록만 쓴다. 보고서는 `_workspace_codex/04_report.md`에도 저장한다.

## 5단계: 수정 (명시 요청 시만)

기본 호출은 검증만 한다. 수정은 "코덱스 검증하고 고쳐줘", "코덱스 문제 있으면 수정"처럼 요청했을 때만 한다.
- 대상은 **확정** 지적이다. `확실하지 않음`은 고치기 전에 사용자에게 묻는다.
- 작은 수정은 리더가 `game-core-rules`를 지켜 직접 한다.
- 여러 파일이나 Server/Client에 걸치면 `game-dev-orchestrator`의 수정·재검토 경로(2·4·5단계)를 쓴다.
- 수정 후 관련 테스트를 돌린다.
- Commit·Push는 사용자가 "푸시"라고 할 때만 한다.

## 테스트 시나리오

- **정상 (변경분):** 사용자가 "코덱스 923ff3a..HEAD"를 입력한다.
  1. `00_request.md`를 쓰고 `codex_diff.sh <repo> <ws> base 923ff3a`를 실행한다.
  2. Codex가 변경 파일 26개를 다 읽고 지적 2건을 낸다. `runs.tsv`에 session과 tokens가 남는다.
  3. 리더가 직접 확인해 1건 확정, 1건 기각(이미 상한이 있음).
  4. `[High]` 1건과 "기각 1건", session id와 토큰을 보고한다.
- **정상 (전체):** 사용자가 "코덱스 서버 전체"를 입력한다.
  1. `plan_areas.py`가 영역 7개를 만든다. `codex_full.sh`가 3개씩 실행한다.
  2. a04가 시간 초과로 실패하고 파일 3개가 읽히지 않았다.
  3. 같은 명령을 다시 실행해 a04만 돌고, 99/99 파일을 읽었다.
  4. 지적 9건(Medium 이상 6건)을 Workflow로 검증한다. 확정 3, 기각 3이 나온다.
  5. 보고서를 쓴다.
- **오류:**
  1. `codex login` 만료로 모든 영역이 exit 1이다.
  2. 다시 시도하지 않고 "Codex 인증 필요(`codex login`)"와 로그 끝을 보고한다.
  3. 리더가 대신 리뷰를 만들지 않는다.
- **비호출:** "서버리뷰 해줘"는 `server-review`로, "리뷰해줘"는 직접 처리한다. 이 스킬을 쓰지 않는다.
