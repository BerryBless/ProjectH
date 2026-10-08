---
name: server-review
description: ".NET 10 실시간 게임 서버(Server/)를 시니어 서버 프로그래머 관점으로 감사하는 서버 코드 리뷰 하네스. 서버 구조·Thread Model·Ownership을 먼저 정리하고, concurrency·gameloop·lifetime·network·database·resilience 6개 영역을 병렬 리뷰한 뒤 지적마다 반박 검증해 Critical~Low 보고서(Summary, 영역 요약, Tests Needed, Good, Priority)를 낸다. 사용자가 '서버리뷰', '서버 리뷰', '서버 코드 리뷰', 'server review'를 입력하거나, 서버 전체·특정 서버 기능·특정 Commit의 서버 코드 리뷰를 요청하면 반드시 사용한다. '서버리뷰 다시', '리뷰만 다시', '이 영역만 다시 리뷰', '리뷰하고 고쳐줘', '지적 사항 수정까지' 같은 후속 요청에도 사용한다. 기능 구현 직후 변경분 점검이나 '성능점검'·'최적화'는 game-dev-orchestrator, Client만의 리뷰는 이 스킬의 대상이 아니다."
---

# 서버 코드 리뷰 오케스트레이터

목적은 코드를 재작성하는 것이 아니라 **문제를 찾고, 원인을 설명하고, 영향도를 판단하고, 수정 방향을 제시하는 것**이다. 사용자가 "고쳐줘", "수정까지", "문제 있으면 수정해"라고 하지 않았다면 코드를 바꾸지 않는다. 리뷰 결과가 실제 코드와 어긋나면 안 되기 때문에, 리뷰 도중 리더도 코드를 고치지 않는다.

## 실행 모드: 혼합

| 단계 | 실행 모드 | 선택 이유 |
|------|----------|-----------|
| 1. 범위 확정 | 메인(리더) 직접 수행 | 전체·부분·Commit 범위와 영역을 정해야 뒤 단계가 단순해진다. |
| 2. 구조 파악 → 영역 리뷰 → 검증 | 워크플로 조율 | 영역 목록, 검증 규칙, 판정 기준을 코드로 고정할 수 있다. 매번 같은 방식으로 판정해야 결과를 비교할 수 있다. |
| 3. 보고서 | 메인(리더) 직접 수행 | 근본 원인 묶기와 우선순위 정렬은 전체 지적을 한 번에 보는 판단이 필요하다. |
| 4. 수정(요청 시만) | 지속형 에이전트 협업 | 기존 `server-engineer`에게 확정 지적을 넘겨 맥락을 유지하며 고친다. |

## 에이전트

| 이름 | `agentType` / `subagent_type` | 역할 | 산출물 |
|------|------|------|------|
| (워크플로 내부) | `server-reviewer` | map·review·verify 모드 (읽기 전용) | 워크플로 반환값 → `_workspace_review/02_*.json` |
| server-engineer | `server-engineer` | 4단계 수정(요청 시만) | `_workspace_review/04_server-engineer_changes.md` |

기준 문서: `references/checklist.md`(영역별 점검 항목), `references/report-format.md`(Severity·보고 형식). 리뷰어는 이 두 파일을 직접 읽으므로 리더가 프롬프트에 내용을 복사하지 않는다.

## 0단계: 기존 작업 확인

1. `_workspace_review/`가 있는지 확인한다. (`.gitignore`의 `_workspace_*/`에 걸려 커밋되지 않는다. `game-dev-orchestrator`의 `_workspace/`와 섞이지 않게 따로 쓴다.)
   - 없으면 → 처음부터.
   - 있고 같은 리뷰의 후속 요청이면 → `00_scope.md`를 읽고 필요한 단계만. 예: "보고서만 다시" → 3단계, "network만 다시" → 2단계를 그 영역만, "고쳐줘" → 4단계.
   - 있고 새 리뷰면 → `_workspace_review_{YYYYMMDD-HHMM}/`로 옮기고 새로 시작한다(시각은 `date +%Y%m%d-%H%M`).
2. 리뷰만 다시 하고 코드가 바뀌지 않았으면 `run_meta.json`의 `runId`로 `resumeFromRunId` 재개할 수 있다. 코드가 바뀌었으면(현재 `git rev-parse HEAD`와 작업 트리 diff가 `00_scope.md` 기록과 다르면) 재개하지 말고 새로 실행한다. 캐시된 리뷰는 바뀐 코드를 보지 않는다.

## 1단계: 범위 확정 (리더)

1. 요청에서 범위를 정한다.
   - **전체**(기본, "서버리뷰"만 입력한 경우): `Server/src/ProjectH.Server` 전체 + 서버가 쓰는 `Shared/Runtime` + 봇(`Server/src/ProjectH.Bots`)은 network 영역의 Protocol 동작 비교에만.
   - **부분**(특정 파일·기능): 그 파일과, 그 코드가 호출하는 핵심 경로(Packet Handler → Game Loop Command → World → Collision → Replication)까지.
   - **Commit**(Commit·브랜치·"이번 변경"): `git diff --stat <base>..<target>`(또는 작업 트리 `git diff`)로 바뀐 서버 파일을 모은다. 리뷰어는 diff 너머 영향까지 보고, 신규/기존 문제를 구분한다.
2. 영역은 기본 6개 전부다: `concurrency`, `gameloop`, `lifetime`, `network`, `database`, `resilience`. 부분·Commit 범위에서 관련 없는 영역이 명백하면(예: DB 코드를 전혀 거치지 않는 변경 → `database` 제외) 빼고 이유를 적는다. Lock·Queue·Thread가 하나라도 걸리면 `concurrency`·`lifetime`은 빼지 않는다.
3. 검증 인원: 기본 1명. 사용자가 "철저히", "전수", "꼼꼼히"를 요청하면 3명, 과반 판정.
4. `_workspace_review/00_scope.md`에 범위, 대상 파일(또는 diff 범위), 영역과 제외 이유, `git rev-parse HEAD`, `git status --short` 요약, 수정 요청 여부를 쓴다.

## 2단계: 구조 파악 → 영역 리뷰 → 검증 (워크플로)

사용자가 "서버리뷰" 키워드나 이 스킬을 직접 호출했으면 Workflow 사용에 동의한 것으로 본다. 아래 스크립트를 `script`로 직접 넘긴다.

`args` 예:

```json
{
  "scope": "전체",
  "scopeDetail": "Server/src/ProjectH.Server 전체 (HEAD 89ac904)",
  "files": [],
  "diff": "",
  "domains": ["concurrency", "gameloop", "lifetime", "network", "database", "resilience"],
  "verifiers": 1,
  "verifyLow": false
}
```

Commit 범위면 `diff`에 `base..target`을, 부분 범위면 `files`에 시작 파일을 넣는다.

```javascript
export const meta = {
  name: 'server-review',
  description: '서버 구조를 파악하고 6개 영역을 병렬 리뷰한 뒤 지적마다 반박 검증한다',
  phases: [
    { title: '구조 파악', detail: 'Entry Point부터 흐름·Thread Model·Ownership 정리' },
    { title: '영역 리뷰', detail: '영역마다 server-reviewer가 근본 원인 단위로 문제를 찾는다' },
    { title: '검증', detail: '지적 한 건마다 반박 근거를 찾아 판정한다' },
  ],
}

const SEVS = ['Critical', 'High', 'Medium', 'Low']
const MAP = { type: 'object', required: ['architecture', 'files'], properties: {
  architecture: { type: 'string' }, files: { type: 'array', items: { type: 'string' } } } }
const FINDINGS = { type: 'object', required: ['findings', 'area_summary', 'good'], properties: {
  area_summary: { type: 'string' },
  good: { type: 'array', items: { type: 'string' } },
  findings: { type: 'array', items: { type: 'object',
    required: ['title', 'domain', 'severity', 'file', 'line', 'symbol', 'problem', 'why', 'scenario',
      'impact', 'fix', 'verification', 'certainty', 'origin', 'perf_class'],
    properties: {
      title: { type: 'string' }, domain: { type: 'string' },
      severity: { type: 'string', enum: SEVS },
      file: { type: 'string' }, line: { type: 'integer' }, symbol: { type: 'string' },
      problem: { type: 'string' }, why: { type: 'string' }, scenario: { type: 'string' },
      impact: { type: 'string' }, fix: { type: 'string' }, verification: { type: 'string' },
      certainty: { type: 'string', enum: ['확정', '확실하지 않음'] },
      to_confirm: { type: 'string' },
      origin: { type: 'string', enum: ['new', 'existing', 'n/a'] },
      perf_class: { type: 'string', enum: ['hot-path', 'potential', 'needs-measurement', 'n/a'] } } } } } }
const VERDICT = { type: 'object', required: ['status', 'reason'], properties: {
  status: { type: 'string', enum: ['confirmed', 'refuted', 'uncertain'] },
  reason: { type: 'string' },
  severity_adjust: { type: 'string' } } }

const domains = args.domains ?? ['concurrency', 'gameloop', 'lifetime', 'network', 'database', 'resilience']
const verifiers = args.verifiers ?? 1
const need = Math.floor(verifiers / 2) + 1
const scopeText = `범위: ${args.scope ?? '전체'} — ${args.scopeDetail ?? ''}` +
  (args.diff ? `\nCommit 범위: ${args.diff} (git diff ${args.diff}로 확인하라)` : '') +
  ((args.files ?? []).length ? `\n시작 파일:\n${args.files.join('\n')}` : '')

phase('구조 파악')
const map = await agent(
  `[map 모드]\n${scopeText}\n서버 Entry Point부터 따라가 흐름, Thread Model, Ownership 표, ` +
  `둘 이상 Thread가 닿는 상태와 보호 방식, Lock·Queue·Cache·Timer·CTS·static 목록을 정리하라. ` +
  `files에는 이 범위의 리뷰 대상 서버 파일 경로를 넣어라.`,
  { agentType: 'server-reviewer', label: '구조 파악', phase: '구조 파악', schema: MAP })
if (!map) {
  log('구조 파악이 실패해 리뷰를 진행하지 않았습니다.')
  return { failed: 'map' }
}

const reviewed = await pipeline(
  domains,
  d => agent(
    `[review 모드] 영역: ${d}\n${scopeText}\n` +
    `checklist.md의 "${d}" 절과 report-format.md를 기준으로 하라.\n` +
    `대상 파일:\n${map.files.join('\n')}\n\n구조 파악 결과:\n${map.architecture}\n\n` +
    `area_summary에는 보고서의 해당 영역 요약 블록(예: concurrency → Thread Model/Shared Mutable State/Lock Count/` +
    `Deadlock Risk/Contention Risk)을 채울 내용을 써라. 근본 원인 단위로 findings JSON을 반환하라.`,
    { agentType: 'server-reviewer', label: `리뷰:${d}`, phase: '영역 리뷰', schema: FINDINGS }),
  // 리뷰가 실패하면 null을 넘겨 failedDomains로 센다.
  review => !review ? null : parallel(review.findings.map(f => () => {
    // line 0은 "검토 대상 없음" 알림이다. Low는 기본으로 검증하지 않는다(agent 수를 줄이고, 등급이 낮아 오판 비용이 작다).
    if (f.line === 0) return Promise.resolve({ ...f, status: 'notice', verdicts: [] })
    if (f.severity === 'Low' && !args.verifyLow) return Promise.resolve({ ...f, status: 'low-unverified', verdicts: [] })
    return parallel(Array.from({ length: verifiers }, (_, i) => () =>
      agent(
        `[verify 모드] 검증자 ${i + 1}/${verifiers}. 다음 지적을 코드에서 직접 확인하고 반박 근거부터 찾아라. ` +
        `등급이 과하거나 모자라면 severity_adjust에 Critical/High/Medium/Low 중 하나와 이유를 적어라.\n` +
        JSON.stringify(f),
        { agentType: 'server-reviewer', label: `검증:${f.file}:${f.line}`, phase: '검증', schema: VERDICT })))
      .then(vs => {
        const got = vs.filter(Boolean)
        const ok = got.filter(v => v.status === 'confirmed').length
        const no = got.filter(v => v.status === 'refuted').length
        // 검증자가 모두 실패하면 기각이 아니라 미검증이다. 기각과 섞으면 High 지적이 조용히 사라진다.
        const status = got.length === 0 ? 'unverified' : ok >= need ? 'confirmed' : no >= need ? 'rejected' : 'uncertain'
        const adj = got.filter(v => v.status === 'confirmed')
          .map(v => SEVS.find(s => (v.severity_adjust ?? '').startsWith(s))).filter(Boolean)
        const severity = status === 'confirmed' && adj.length >= need && adj.every(s => s === adj[0]) ? adj[0] : f.severity
        return { ...f, severity, original_severity: f.severity, verdicts: got, status }
      })
  })).then(items => ({ domain: review.findings[0]?.domain, area_summary: review.area_summary, good: review.good, items }))
)

const done = reviewed.filter(Boolean)
const failedDomains = domains.filter((d, i) => !reviewed[i])
const raw = done.flatMap(r => r.items.filter(Boolean))

// 같은 file:line은 한 건으로 합친다. 근본 원인 묶기(다른 줄의 같은 원인)는 리더가 3단계에서 한다.
const RANK = { confirmed: 5, uncertain: 4, unverified: 3, 'low-unverified': 3, notice: 3, rejected: 1 }
const SEV = { Critical: 4, High: 3, Medium: 2, Low: 1 }
const merged = new Map()
for (const f of raw) {
  const key = `${f.file}:${f.line}`
  const prev = merged.get(key)
  if (!prev) { merged.set(key, { ...f, domains: [f.domain] }); continue }
  const best = RANK[f.status] > RANK[prev.status] ||
    (RANK[f.status] === RANK[prev.status] && SEV[f.severity] > SEV[prev.severity]) ? f : prev
  merged.set(key, { ...best, domains: [...new Set([...prev.domains, f.domain])],
    verdicts: [...(prev.verdicts ?? []), ...(f.verdicts ?? [])] })
}
const all = [...merged.values()]
const pick = s => all.filter(f => f.status === s)
const result = {
  architecture: map.architecture,
  areas: domains.map((d, i) => reviewed[i] ? { domain: d, area_summary: reviewed[i].area_summary, good: reviewed[i].good } : { domain: d, failed: true }),
  confirmed: pick('confirmed'), uncertain: pick('uncertain'), unverified: pick('unverified'),
  lowUnverified: pick('low-unverified'), notices: pick('notice'), rejected: pick('rejected'),
  failedDomains,
}
if (failedDomains.length) log(`영역 ${domains.length}개 중 ${failedDomains.join(', ')} 리뷰가 실패해 결과에서 빠졌습니다.`)
if (raw.length !== all.length) log(`같은 위치의 중복 지적 ${raw.length - all.length}건을 합쳤습니다.`)
log(`지적 ${all.length}건: 확정 ${result.confirmed.length}, 확실하지 않음 ${result.uncertain.length}, ` +
  `미검증 ${result.unverified.length}, Low(검증 생략) ${result.lowUnverified.length}, 기각 ${result.rejected.length}`)
return result
```

완료 알림을 받기 전에 결과를 단정하지 않는다. 반환값을 받으면 저장한다.

- `_workspace_review/01_architecture.md` — `architecture`
- `_workspace_review/02_confirmed.json`, `02_uncertain.json`, `02_unverified.json`(unverified + lowUnverified + notices), `02_rejected.json`(기각 사유 확인용), `02_areas.json`
- `_workspace_review/run_meta.json` — `runId`, 영역, 검증 인원, 날짜, HEAD

**Workflow를 쓸 수 없을 때**(거부되었거나 사용자가 스킬을 직접 부르지 않은 경우): 같은 절차를 서브에이전트로 한다. `Agent(subagent_type: "server-reviewer")`로 map 한 번 → 영역별 review를 한 메시지에서 병렬 → 지적별 verify를 한 메시지에서 병렬. 판정 규칙(과반 confirmed, 검증자 전원 실패는 미검증, `line: 0`과 Low는 검증 생략)과 저장 파일은 같다.

## 3단계: 보고서 (리더)

1. **미검증 지적은 버리지 않는다.** `02_unverified.json`의 Critical~Medium은 리더가 코드를 직접 열어 확정·기각을 정하고 근거를 적는다. 직접 확인할 수 없으면 `확실하지 않음`으로 보고한다.
2. **근본 원인으로 묶는다.** 다른 파일·줄이지만 원인이 같은 지적(예: Disconnect 경로 하나의 누락으로 Timer·Queue Entry·Peer가 같이 남음)은 한 건으로 합치고, Location에 관련 위치를 모두 적는다. 묶을 때 등급은 가장 높은 것을 쓴다.
3. 확정 + 확실하지 않음 지적으로 `references/report-format.md` 4절 형식의 보고서를 쓴다. 영역 요약 블록은 `02_areas.json`의 `area_summary`, Good은 `good`에서 실제로 확인된 것만 쓴다. Commit 범위면 신규 문제를 각 등급 안에서 먼저 둔다.
4. Performance 절에는 `perf_class`가 `hot-path`인 확정 지적을 Confirmed에, `needs-measurement`·`potential`을 Needs Measurement에 둔다.
5. 리뷰가 실패한 영역은 한 번만 다시 실행한다(`domains`를 그 영역만으로). 또 실패하면 보고서 검증 기록에 "(영역) 리뷰 누락"을 적고, `concurrency`가 빠졌다면 리더가 Lock·Thread 목록만은 직접 확인해 적는다(Deadlock 검토는 생략할 수 없는 프로젝트 규칙이다).
6. 보고서를 `_workspace_review/03_report.md`에 저장하고, 같은 내용을 사용자에게 출력한다.

## 4단계: 수정 (요청 시만)

사용자가 수정을 요청했을 때만 한다. 그렇지 않으면 보고서 끝에 "수정하려면 '리뷰 지적 수정해줘'라고 요청하라"는 한 줄만 둔다.

1. 사용자가 고칠 항목을 지정하지 않았으면 Priority 순서대로 Critical·High를 대상으로 한다. `needs-measurement` 항목은 측정 전에 고치지 않는다.
2. `Agent(name: "server-engineer", subagent_type: "server-engineer", prompt: "_workspace_review/03_report.md의 [번호] 지적을 수정하라. 테스트를 먼저 추가해 문제를 재현하고, 테스트를 지우거나 약화하지 않는다. 완료 후 _workspace_review/04_server-engineer_changes.md 작성")`. Client나 Shared 이동 코드가 걸리면 `game-dev-orchestrator` 2단계 규칙(Shared 먼저, 엔지니어 사이 메시지는 리더가 중계)을 따른다.
3. 수정 후 `server-reviewer`를 `name` 없이 한 번 호출해 재검토한다("재검토: 지적 목록과 수정 파일만 보고 해소 여부와 새 문제만 보고하라"). 미해소 항목은 최대 2회까지 다시 맡기고, 그 뒤에도 남으면 사용자에게 보고한다.
4. 리더가 `dotnet build Server/ProjectH.Server.slnx`와 `dotnet test Server/ProjectH.Server.slnx`를 실행해 결과를 보고한다. Commit·Push는 사용자가 "푸시"를 입력할 때만 한다.

## 오류 처리

| 상황 | 대응 |
|------|------|
| 구조 파악 실패 | 워크플로가 `failed: 'map'`을 반환한다. 한 번 다시 실행하고, 또 실패하면 리더가 `Docs/Server.md`·`Docs/Architecture.md`와 Entry Point를 직접 읽어 구조를 정리한 뒤 `01_architecture.md`로 저장하고, 서브에이전트 방식으로 영역 리뷰를 진행한다. |
| 영역 리뷰 실패 | `failedDomains`. 그 영역만 한 번 다시 실행하고, 또 실패하면 보고서에 누락을 적는다. |
| 결과가 비어 있음 | 문제가 없다고 단정하지 않는다. 워크플로 journal에서 리뷰어의 실제 반환값을 확인한다. |
| 사용량 한도, 인증 만료, 권한 거부 | 다시 시도하지 않는다. 부분 산출물을 열어 진행 범위를 확인하고 `_workspace_review/99_incomplete.md`에 기록한 뒤 보고한다. 한도라면 풀리는 시각도 알린다. 리뷰어의 판단을 추측해 채우지 않는다. |
| 리뷰어와 검증자 판단 충돌 | 지우지 않는다. 검증 판정이 우선이지만 두 근거를 `02_*.json`에 그대로 남기고, 사용자가 물으면 보여 준다. |

## 테스트 시나리오

### 정상 흐름 — 전체 리뷰

1. 요청: "서버리뷰"
2. 1단계: 범위 전체, 영역 6개, 검증 1명. `00_scope.md`에 HEAD 기록.
3. 2단계: map이 Network Thread → `InboundChannels` → GameLoop 단일 스레드 → `Match`, `MatchHistoryQueue` → `MatchHistoryWriter` → MySQL 흐름과 Ownership 표를 반환. 영역 리뷰 6건이 지적 12건을 내고 Low 4건을 뺀 8건이 검증되어 확정 6, 기각 2.
4. 3단계: 같은 Disconnect 경로에서 나온 지적 2건을 한 건으로 묶어 5건으로 보고서 작성, `03_report.md` 저장 후 출력. 코드 변경 없음.

### 정상 흐름 — Commit 리뷰 후 수정

1. 요청: "89ac904 서버리뷰하고 고쳐줘"
2. 1단계: 범위 Commit `89ac904^..89ac904`, 바뀐 서버 파일 수집, 수정 요청 있음.
3. 2·3단계 후 보고서의 신규 High 2건을 4단계에서 server-engineer가 테스트와 함께 수정, 재검토 해소 확인, `dotnet test` 통과 보고.

### 오류 흐름

1. 2단계에서 `database` 리뷰어가 스키마에 맞지 않는 결과를 반복해 `failedDomains: ['database']`.
2. 리더가 `domains: ['database']`로 한 번 다시 실행한다. 또 실패하면 보고서 검증 기록에 "database 리뷰 누락 — 수동 확인 필요"를 적고, 리더가 Game Loop의 DB 대기 여부(가장 중요한 항목)만은 직접 확인해 결과를 함께 적는다.
