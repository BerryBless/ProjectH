---
name: game-dev-orchestrator
description: "Unity Client / .NET 10 Server / MySQL 게임 프로젝트의 기능 구현과 성능점검을 조율한다. 작업을 Client·Server·Shared·DB로 나누고, server-engineer·client-engineer가 구현하게 한 뒤, 변경 내용에 맞는 점검만 켜서 game-reviewer 워크플로로 검토·검증하고 수정까지 마친다. '기능 구현해줘', '서버에 ~ 추가', '클라이언트에 ~ 만들어줘', 'Packet/Session/DB ~ 작업', '성능점검', '성능검토', '최적화' 요청에 사용한다. 이전 작업의 수정, 보완, 다시 실행, 재실행, 리뷰만 다시, 지적 사항 반영, 이전 결과 개선 요청에도 사용한다. 코드 설명이나 단순 질문에는 사용하지 않는다."
---

# 게임 개발 오케스트레이터

## 실행 모드: 혼합

| 단계 | 실행 모드 | 선택 이유 |
|------|----------|-----------|
| 1. 분석·점검 판단 | 메인(리더) 직접 수행 | 범위와 켤 점검을 확정해야 뒤 단계가 단순해진다. |
| 2. 구현 | 지속형 에이전트 협업 | 엔지니어가 맥락을 유지해야 리뷰 지적을 정확히 고친다. |
| 3. 리뷰·검증 | 워크플로 조율 | 점검 키 목록과 적대적 검증 규칙을 코드로 정할 수 있다. |
| 4. 수정 | 지속형 에이전트 협업 | 2단계의 엔지니어에게 이어서 지시한다. |
| 5. 재검토 | 서브에이전트 위임 | 지적 목록 대비 해소 여부만 한 번 확인하면 된다. |

사용자가 이 스킬을 직접 호출했다면 3단계의 Workflow 사용에 동의한 것으로 본다. 그렇지 않은 경우는 3단계의 "Workflow를 쓸 수 없을 때"를 따른다. 기본 규모는 작게 유지한다(점검 키 1~4개, 지적 한 건당 검증 1명). 사용자가 "철저히", "전수", "꼼꼼히"를 요청하면 지적 한 건당 검증을 3명으로 늘리고 과반 `confirmed`만 통과시킨다.

## 에이전트 구성

| 이름 | `subagent_type` / `agentType` | 역할 | 참조 스킬 | 산출물 |
|------|------|------|------|------|
| server-engineer | `server-engineer` | Server·Shared·DB 구현 | game-core-rules, game-perf-checks | `_workspace/02_server-engineer_changes.md` |
| client-engineer | `client-engineer` | Unity Client 구현 | game-core-rules, game-perf-checks | `_workspace/02_client-engineer_changes.md` |
| (워크플로 내부) | `game-reviewer` | 점검 키별 리뷰, 지적별 검증 | game-core-rules, game-perf-checks | 워크플로 반환값 → `_workspace/03_review_confirmed.json` |

필요한 엔지니어만 실행한다. Server만 바뀌는 작업에 client-engineer를 띄우지 않는다.

## 작업 절차

### 0단계: 기존 작업 확인

1. `_workspace/`가 있는지 확인한다.
   - 없으면 → 처음부터 실행한다.
   - 있고 같은 작업의 수정·보완 요청이면 → `00_lead_plan.md`와 `02_*_changes.md`를 읽고 필요한 단계만 다시 실행한다. 예: "리뷰만 다시" → 3단계부터, "지적 사항 반영" → 4단계부터.
   - 있고 새 작업이면 → 기존 폴더를 `_workspace_{YYYYMMDD-HHMM}/`로 옮기고 새로 시작한다. 시각은 셸 `date +%Y%m%d-%H%M`으로 얻는다.
2. 리뷰만 다시 실행하고 `_workspace/run_meta.json`에 직전 `runId`가 있으면 `resumeFromRunId`로 재개할 수 있다. 바뀌지 않은 `agent()` 호출은 캐시 결과를 쓴다. 코드가 바뀌었다면 재개하지 말고 새로 실행한다. 캐시된 리뷰는 바뀐 코드를 보지 않기 때문이다.

### 1단계: 분석과 점검 판단 (리더 직접 수행)

1. `game-core-rules`와 `game-perf-checks` 스킬을 읽는다.
2. 요청과 관련 코드를 읽고 다음을 판단한다.
   - Client 작업인가, Server 작업인가, 둘 다인가?
   - Network Protocol(Shared DTO) 변경이 필요한가?
   - DB 변경(Query, Table, Index, Transaction)이 필요한가?
   - Lock, Queue·Cache, 반복 실행 경로가 생기거나 바뀌는가?
3. `game-perf-checks` 1절 판단표로 켤 점검 키를 정한다. `safety`는 항상 포함한다. 일반 기능이면 `safety`만 켠다.
4. 되돌리기 어려운 결정이 있으면 구현 전에 사용자에게 확인한다.
   - Shared 프로젝트를 처음 만드는 경우(위치, Unity 쪽 참조 방식)
   - DB 스키마 변경
   - 새 NuGet·Unity 패키지 추가
5. `_workspace/00_lead_plan.md`를 쓴다.

```markdown
## 요청
## 범위
- Client: (변경 내용 또는 "없음")
- Server: 
- Shared / Protocol: (DTO·Protocol ID 합의 내용)
- DB: 
## 켜진 점검 키
- safety — 항상
- (키) — (판단표의 어느 행 때문인지)
## 사용자 확인 사항
## 가정
```

**성능점검 요청일 때:** 구현(2단계)을 건너뛴다. 대상 영역(Client / Server / DB)의 코드 파일 목록을 확정하고, 해당 영역의 성능 키 전체와 `safety`를 켠 뒤 3단계로 간다. 이 경우 수정은 보고 후 사용자가 승인한 항목만 4단계로 진행한다. `game-perf-checks` 3절대로 측정 없이 최적화하지 않기 위해서다.

### 2단계: 구현

**실행 모드:** 지속형 에이전트 협업

1. 필요한 엔지니어를 한 메시지에서 실행한다. `name`은 SendMessage 대상 지정에 쓰므로 반드시 붙인다.
   - `Agent(name: "server-engineer", subagent_type: "server-engineer", prompt: "<요청 요약> / 계획: _workspace/00_lead_plan.md / 켜진 점검 키: <키 목록> / 완료 후 _workspace/02_server-engineer_changes.md 작성")`
   - `Agent(name: "client-engineer", subagent_type: "client-engineer", prompt: "...")`
2. Protocol 변경이 양쪽에 걸치면 server-engineer가 Shared DTO를 먼저 정의하게 하고, 완료 보고를 받은 뒤 client-engineer에게 DTO 경로를 SendMessage로 전달한다. 양쪽이 동시에 DTO를 정의하면 어긋나기 때문이다.
3. 엔지니어의 질문(되돌리기 어려운 결정)은 리더가 판단하거나 사용자에게 묻고 답을 전달한다.
4. 엔지니어 사이 메시지는 리더가 중계한다. 엔지니어가 서로 직접 보내지 않는다.

**산출물 동결:** 모든 엔지니어가 완료를 보고하면 SendMessage로 동결을 알린다("2단계 산출물을 동결한다. 이후 고칠 내용은 `_changes_v2.md`에 쓰고 알려라"). 그리고 변경 파일 목록을 `_workspace/02_changed_files.txt`에 모은다. 리뷰 도중 코드가 바뀌면 리뷰 결과가 실제 코드와 어긋나기 때문이다.

### 3단계: 리뷰와 검증

**실행 모드:** 워크플로 조율

Workflow 도구에 아래 스크립트를 `script`로 직접 전달한다. `args`:

```json
{
  "dimensions": ["safety", "deadlock"],
  "files": ["Server/Net/Session.cs"],
  "summary": "요청과 구현 요약 한 단락",
  "verifiers": 1
}
```

```javascript
export const meta = {
  name: 'game-review',
  description: '켜진 점검 키별로 변경 코드를 리뷰하고 지적마다 반박 근거를 찾아 검증한다',
  phases: [
    { title: '리뷰', detail: '점검 키마다 game-reviewer가 문제를 찾는다' },
    { title: '검증', detail: '지적 한 건마다 반박 근거를 찾아 판정한다' },
  ],
}

const FINDINGS = { type: 'object', required: ['findings'], properties: {
  findings: { type: 'array', items: { type: 'object',
    required: ['title', 'check', 'rule', 'file', 'line', 'severity', 'evidence', 'suggested_fix'],
    properties: {
      title: { type: 'string' }, check: { type: 'string' }, rule: { type: 'string' },
      file: { type: 'string' }, line: { type: 'integer' },
      severity: { type: 'string', enum: ['High', 'Medium', 'Low'] },
      evidence: { type: 'string' }, suggested_fix: { type: 'string' },
      needs_measurement: { type: 'string' } } } } } }
const VERDICT = { type: 'object', required: ['status', 'reason'], properties: {
  status: { type: 'string', enum: ['confirmed', 'refuted', 'uncertain'] },
  reason: { type: 'string' } } }

const dims = args.dimensions ?? ['safety']
const files = args.files ?? []
const verifiers = args.verifiers ?? 1
const need = Math.floor(verifiers / 2) + 1

const reviewed = await pipeline(
  dims,
  key => agent(
    `[review 모드] 점검 키: ${key}\n변경 파일:\n${files.join('\n')}\n작업 요약: ${args.summary ?? ''}\n` +
    `지정된 점검 키 기준으로만 검토하고 findings JSON을 반환하라.`,
    { agentType: 'game-reviewer', label: `리뷰:${key}`, phase: '리뷰', schema: FINDINGS }),
  // 리뷰가 실패하면 null을 그대로 넘겨야 failedDims로 누락을 셀 수 있다.
  review => !review ? null : parallel(review.findings.map(f => () =>
    // line 0은 리뷰어가 남긴 "검토 대상 파일 없음" 알림이다. 검증하면 refuted로 사라지므로 그대로 넘긴다.
    f.line === 0 ? Promise.resolve({ ...f, status: 'notice', verdicts: [] }) :
    parallel(Array.from({ length: verifiers }, (_, i) => () =>
      agent(
        `[verify 모드] 검증자 ${i + 1}/${verifiers}. 다음 지적을 코드에서 직접 확인하고 반박 근거부터 찾아라. ` +
        `근거가 충분하면 confirmed, 반박되면 refuted, 판단 불가면 uncertain.\n${JSON.stringify(f)}`,
        { agentType: 'game-reviewer', label: `검증:${f.file}:${f.line}`, phase: '검증', schema: VERDICT })))
      .then(vs => {
        const got = vs.filter(Boolean)
        const ok = got.filter(v => v.status === 'confirmed').length
        // 검증자가 모두 실패한 지적은 기각이 아니라 미검증이다. 기각으로 섞으면 High 지적이 조용히 사라진다.
        const status = ok >= need ? 'confirmed' : got.length === 0 ? 'unverified' : 'rejected'
        return { ...f, verdicts: got, status }
      })))
)

const failedDims = reviewed.filter(r => !r).length
const raw = reviewed.filter(Boolean).flat().filter(Boolean)

// safety는 deadlock·queue-cache 기준(core-rules 7–8·13–15절)을 포함하므로 같은 결함이 여러 키로 보고된다.
// file:line 기준으로 합쳐 엔지니어에게 한 번만 전달한다.
const RANK = { confirmed: 3, unverified: 2, notice: 2, rejected: 1 }
const SEV = { High: 3, Medium: 2, Low: 1 }
const merged = new Map()
for (const f of raw) {
  const key = `${f.file}:${f.line}`
  const prev = merged.get(key)
  if (!prev) { merged.set(key, { ...f, checks: [f.check] }); continue }
  const best = RANK[f.status] > RANK[prev.status] ||
    (RANK[f.status] === RANK[prev.status] && SEV[f.severity] > SEV[prev.severity]) ? f : prev
  merged.set(key, { ...best, checks: [...new Set([...prev.checks, f.check])],
    verdicts: [...prev.verdicts, ...f.verdicts] })
}
const all = [...merged.values()]
const pick = s => all.filter(f => f.status === s)
const confirmed = pick('confirmed'), rejected = pick('rejected')
const unverified = pick('unverified'), notices = pick('notice')

if (failedDims) log(`점검 키 ${dims.length}개 중 ${failedDims}개의 리뷰가 실패해 결과에서 빠졌습니다.`)
if (raw.length !== all.length) log(`중복 지적 ${raw.length - all.length}건을 합쳤습니다.`)
if (unverified.length) log(`검증자가 모두 실패한 지적 ${unverified.length}건은 미검증으로 따로 보고합니다.`)
log(`지적 ${all.length}건 중 확정 ${confirmed.length}, 기각 ${rejected.length}, 미검증 ${unverified.length}, 알림 ${notices.length}건입니다.`)
return { dims, failedDims, confirmed, rejected, unverified, notices }
```

완료 알림을 받기 전에 결과를 단정하지 않는다. 반환값을 받으면 다음을 저장한다.

- `_workspace/03_review_confirmed.json` — `confirmed`
- `_workspace/03_review_rejected.json` — `rejected` (기각 사유 확인용)
- `_workspace/03_review_unverified.json` — `unverified`, `notices`. 미검증 지적은 리더가 코드를 직접 확인해 확정 여부를 정한다. 검증이 실패했다는 이유로 버리지 않는다.
- `_workspace/run_meta.json` — `runId`, 점검 키, 날짜

**Workflow를 쓸 수 없을 때:** 사용자가 이 스킬을 직접 호출하지 않고 일반 요청에서 리더가 스킬을 불러왔다면, Workflow 사용 동의로 보기 어려울 수 있다. 이때나 Workflow 호출이 거부되면 같은 절차를 서브에이전트로 수행한다(실행 모드: 서브에이전트 위임).

1. 켜진 점검 키마다 `Agent(subagent_type: "game-reviewer", prompt: "[review 모드] 점검 키: ... 변경 파일: ... 위 findings JSON 구조로 반환하라")`를 한 메시지에서 병렬 호출한다.
2. 돌아온 지적을 `file:line`으로 합친 뒤, 지적마다 `[verify 모드]` 호출을 한 메시지에서 병렬로 보낸다.
3. 판정 규칙(과반 confirmed, 검증자 전원 실패는 미검증, `line: 0`은 검증 생략)과 저장 파일은 위와 같다.

Workflow가 가능하면 Workflow를 우선한다. 판정 규칙이 코드로 고정되어 매번 같은 방식으로 판정되기 때문이다.

### 4단계: 수정

**실행 모드:** 지속형 에이전트 협업

1. 확정된 지적을 파일 경로에 따라 server-engineer 또는 client-engineer에게 SendMessage로 보낸다. 2단계의 에이전트가 맥락을 유지하므로 "3번 지적만 고쳐라"처럼 범위를 좁혀 지시한다.
2. 성능점검 흐름에서 엔지니어가 아직 없으면 같은 이름과 유형으로 새로 실행한다.
3. 엔지니어가 근거를 들어 반박하면 리더가 코드를 직접 확인해 판단한다. 판단이 어려우면 사용자에게 올린다.
4. `needs_measurement`가 있는 항목은 측정 전에는 고치지 않고 보고서의 "측정 필요" 목록으로 넘긴다.

### 5단계: 재검토

**실행 모드:** 서브에이전트 위임

`Agent(subagent_type: "game-reviewer", prompt: "재검토: _workspace/03_review_confirmed.json의 지적이 해소되었는지와 수정으로 새 문제가 생겼는지만 확인하라. 수정된 파일: ...")`를 `name` 없이 한 번 호출한다.
해소되지 않은 항목이 있으면 4단계로 돌아간다. 4~5단계 반복은 최대 2회로 제한한다. 그 뒤에도 남은 항목은 사용자에게 보고한다. 무한 반복을 막고 판단이 필요한 문제를 사람에게 넘기기 위해서다.

### 6단계: 빌드 확인과 보고

1. `Server/`에 솔루션·프로젝트가 있으면 리더가 직접 `dotnet build`를 실행하고, 테스트 프로젝트가 있으면 `dotnet test`도 실행한다.
2. Client 컴파일을 확인하지 못했으면 그렇다고 보고한다.
3. 사용자에게 다음 순서로 보고한다. 단순한 수정이면 짧게 줄인다.

```markdown
## 변경 내용
## 구조
Client / Server / Shared / DB 변경
## 켜진 점검
(키와 켠 이유, 켜지 않은 영역은 "해당 없음")
## 동시성
Lock / Deadlock 검토 결과 (Lock 변경이 없으면 "Lock 변경 없음")
## Lifetime
새로 만든 Resource·Collection과 해제·제거 시점
## 리뷰 결과
확정 N건 (수정 M건, 남은 항목), 기각 K건, 미검증 U건(리더 확인 결과), 누락된 점검 키
## 측정 필요
## 검증
빌드·테스트 결과, 확인하지 못한 항목
```

`_workspace/`는 사후 확인을 위해 지우지 않는다. 엔지니어 에이전트는 작업이 끝나면 자연히 종료되며, 필요하면 TaskStop으로 중지한다.

## 오류 처리

| 상황 | 대응 |
|------|------|
| 엔지니어가 응답하지 않음 | SendMessage로 상태를 확인하고 다시 지시한다. 그래도 실패하면 같은 유형을 새 이름(예: `server-engineer-b`)으로 실행하고 `_workspace/00_lead_plan.md`와 기존 `_changes.md` 경로를 넘긴다. |
| 빌드 실패가 2회 수정 후에도 남음 | 오류 원문과 함께 사용자에게 보고한다. 빌드가 깨진 상태를 완료로 보고하지 않는다. |
| 워크플로에서 점검 키 리뷰 일부 실패 | 스크립트가 `failedDims`로 알린다. 해당 키만 한 번 다시 실행하고, 또 실패하면 보고서에 "(키) 리뷰 누락"이라고 적는다. |
| 결과가 비어 있음 | 문제가 없다고 단정하지 않는다. 워크플로 `journal`에서 각 리뷰어의 실제 반환값을 확인한다. |
| 사용량 한도 소진, 인증 만료, 권한 거부 | 다시 시도하지 않는다. 부분 산출물을 열어 어디까지 진행됐는지 확인하고 `_workspace/99_incomplete.md`에 기록한 뒤 보고한다. 한도라면 풀리는 시각도 알린다. 리더는 직접 확인한 사실만 대신 반영하고 에이전트의 판단을 추측해 채우지 않는다. |
| 엔지니어와 리뷰어 판단 충돌 | 둘 다 지우지 않고 근거와 함께 보고서에 나란히 적는다. |

## 테스트 시나리오

### 정상 흐름 — Server 기능

1. 요청: "서버에 접속한 세션 목록을 관리하는 SessionManager를 만들어줘. 동시 접속자 수 제한도 넣어줘."
2. 1단계: Server만 해당. Session Registry(Collection)와 Lock이 생기므로 `safety`, `deadlock`, `queue-cache`를 켠다. Shared 변경 없음.
3. 2단계: server-engineer만 실행한다. `_changes.md`에 최대 세션 수, 세션 제거 시점, Lock 검토 결과가 적힌다.
4. 3단계: 점검 키 3개로 워크플로를 실행한다. 예: 지적 2건 중 1건 confirmed.
5. 4~5단계: server-engineer가 수정하고 재검토에서 해소를 확인한다.
6. 6단계: `dotnet build` 성공, 보고서 작성.

### 정상 흐름 — 성능점검

1. 요청: "클라이언트 성능점검 해줘."
2. 1단계: 구현을 건너뛰고 `Client/Assets`의 스크립트 목록을 확정한다. `safety`, `client-hotpath`, `client-render-ui`를 켠다.
3. 3단계: 워크플로 실행. `needs_measurement` 항목은 "측정 필요"로 분류한다.
4. 사용자가 승인한 항목만 4단계에서 client-engineer가 수정한다.

### 오류 흐름

1. 3단계에서 `deadlock` 키 리뷰어가 스키마에 맞지 않는 결과를 반복해 `null`을 반환한다.
2. 스크립트가 `failedDims: 1`을 반환하고 "점검 키 3개 중 1개의 리뷰가 실패" 로그를 남긴다.
3. 리더가 `deadlock` 키만으로 워크플로를 한 번 더 실행한다. 또 실패하면 보고서에 "deadlock 리뷰 누락 — 수동 확인 필요"라고 적는다. Lock을 건드린 작업에서 Deadlock 검토는 생략할 수 없는 규칙이므로, 리더가 직접 확인한 결과를 함께 적는다.
