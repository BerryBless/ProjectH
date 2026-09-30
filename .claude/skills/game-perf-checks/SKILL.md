---
name: game-perf-checks
description: "Unity Client / .NET Server / MySQL 코드의 조건부 성능·동시성 점검 기준 문서. 변경 내용을 보고 어떤 점검(Deadlock, Server 동시성, Server Hot Path, Queue/Cache 크기, Client Hot Path, Client Allocation/Pool, Rendering/UI, DB Query/Index/Transaction)을 켤지 정하는 판단표와 각 점검 기준을 담는다. game-dev-orchestrator, game-reviewer, 엔지니어 에이전트가 참조하는 기준이며, 오케스트레이터 없이 Update·Instantiate·Rendering·UI·Packet·Session·Tick·Worker·Queue·Lock·Query·Index·Transaction 코드를 직접 고칠 때도 읽는다. 사용자의 '성능점검', '성능검토', '최적화' 요청은 이 스킬이 아니라 game-dev-orchestrator로 처리한다."
---

# 조건부 성능 점검

성능 점검은 모든 작업에서 실행하지 않는다. 현재 작업이 해당 영역과 직접 관련된 경우에만 켠다.
관련 없는 코드까지 최적화하면 복잡도만 늘고 정확성·안정성이 떨어지기 때문이다(`game-core-rules` 1·18절).

## 1. 점검 영역 판단표

변경된 코드(또는 요청 내용)를 보고 아래 표에서 해당하는 행만 켠다. 여러 행에 해당하면 모두 켠다.
`safety`는 성능 점검이 아니라 `game-core-rules` 안전성 점검이며 항상 켠다.

| 변경 내용 | 켜는 점검 키 | 기준 문서 |
|---|---|---|
| 일반 기능 개발 | `safety` | `game-core-rules` |
| Lock 추가/수정 | `safety` + `deadlock` | `game-core-rules` 13–15절 |
| Thread / Worker / async 수정 | `server-concurrency` | `references/server.md` 33–35절 |
| Packet / Session / Tick / Broadcast / Serialization 수정 | `server-hotpath` | `references/server.md` 29–32·37·42절 |
| Queue / Cache 추가 | `queue-cache` | `game-core-rules` 7–8절, `references/server.md` 36절 |
| Unity Update 계열·반복 Coroutine 수정 | `client-hotpath` | `references/client.md` 22–25절 |
| Instantiate / Destroy 반복 로직 | `client-hotpath` (Pool 필요성 포함) | `references/client.md` 24·26절 |
| Rendering / UI 수정 | `client-render-ui` | `references/client.md` 27–28절 |
| Query 추가·수정, 반복 Query, 대량 조회·저장, 실시간 경로의 DB 접근 | `db-query` | `references/db.md` 38–40절 |
| Index 변경, Table 구조 변경 | `db-query` (Index 포함) | `references/db.md` 41절 |
| Transaction 수정 | `db-query` (Transaction 포함) | `game-core-rules` 16–17절, `references/db.md` |
| Memory 사용 구조 변경 | `safety` (Lifetime / Leak 중심) | `game-core-rules` 6–8절 |
| 사용자가 "성능점검", "성능검토", "최적화" 호출 | 관련 영역의 성능 키 전체 | 아래 3절 절차 |

판단할 때 키워드만 보지 않는다. 예를 들어 `Update()` 안에 한 번만 실행되는 초기화 분기만 추가했다면 반복 실행 경로가 아니므로 `client-hotpath`를 켜지 않는다. 반대로 이름에 Update가 없어도 매 프레임 호출되는 메서드라면 켠다.

## 2. 점검 키별 기준

각 키에 해당하는 `references/` 문서의 절만 읽는다. 켜지지 않은 키의 문서는 읽지 않는다.

- `references/client.md` — Client 성능 목표, Hot Path, Update, Object Pool, Rendering, UI, Unity Profiling (22–28·43절)
- `references/server.md` — Server 성능 목표, Hot Path, Allocation, ThreadPool, Lock Contention, Lock-Free, Queue, Network, Benchmark, Server Profiling (29–37·42·44절)
- `references/db.md` — DB 점검 조건, Query, N+1, Index (38–41절)

## 3. 사용자가 성능점검을 요청했을 때

"성능점검", "성능검토", "최적화" 요청을 받으면 Client / Server / DB 중 관련 영역을 집중적으로 검토한다. 이때만 다음 절차를 따른다.

```text
Measure
↓
Find Bottleneck
↓
Change
↓
Measure Again
```

추측만으로 최적화하지 않는다. 측정값 없이 코드만 읽고 찾은 문제는 "측정 필요" 후보로 보고하고, 측정 방법(Unity Profiler 항목, dotnet-counters 지표, BenchmarkDotNet 대상, EXPLAIN 대상 Query)을 함께 제시한다.
측정 없이 바로 고쳐도 되는 것은 명백한 결함뿐이다. 예: Transaction 안의 Network I/O, 매 프레임 `GetComponent`, Hot Path의 `Task.Result`.

## 4. 보고 형식

점검 결과는 항목마다 다음 정보를 남긴다. 리뷰 워크플로는 이 필드를 JSON으로 받는다.

```text
Area: Client / Server / DB
Check: 점검 키 (예: server-hotpath)
Rule: 근거 규칙 절 번호 (예: server.md 31절)
Location: 파일:라인
Problem: 문제 설명과 코드상 근거
Impact: 영향 (Frame Time, P99 Latency, Memory 증가, Deadlock 등)
Severity: Low / Medium / High
Suggested Fix: 가장 단순한 수정 방향
Needs Measurement: 측정이 필요한지 여부와 측정 방법
```

성능 변경을 실제로 적용했다면 무엇을 측정해 효과를 확인할지 함께 적는다.

## 5. 과잉 적용 금지

다음은 점검이 켜져 있어도 근거 없이 권하지 않는다: 모든 객체 Object Pool, 모든 Collection Concurrent 전환, 전면 Span 전환, Lock-Free 전환, 모든 Update 제거, 작은 Allocation 제거.
권장한다면 어떤 측정값이나 코드 경로 때문에 필요한지 근거를 적는다.
