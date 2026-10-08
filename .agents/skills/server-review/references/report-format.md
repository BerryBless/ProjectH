# 서버 리뷰 보고 형식

원 지침 80–86절과 92절이다. 리뷰어는 1–3절로 지적을 만들고, 리더는 4절 형식으로 최종 보고서를 쓴다.

## 1. Severity (80)

| 등급 | 해당 |
|---|---|
| Critical | Server Crash, Deadlock, Data Corruption, Remote Exploit, 무한 Memory 증가, Game Loop 영구 정지 |
| High | Race Condition, Session Leak, Queue 무한 증가, DB Connection Leak, Severe Tick Spike, 잘못된 Server Authority, 100 Player에서 구조적으로 무너지는 알고리즘 |
| Medium | 불필요한 Allocation, 부분적인 Lock Contention, 비효율적인 Query, Network Waste, 복구 가능한 Lifecycle 문제 |
| Low | 가독성, Naming, 작은 구조 개선, Hot Path가 아닌 Minor Allocation, 문서와 코드의 어긋남 |

등급은 "실제로 일어나는 경로"로 정한다. 외부 입력(악성 Client)으로 일어나면 정상 Client에서 안 일어나도 해당 등급이다.

## 2. 원칙 (81, 84, 85, 86)

- **확실하지 않음:** 근거가 부족하면 `확실하지 않음`으로 표시하고 무엇을 확인해야 확정할 수 있는지 적는다. 추측을 사실처럼 쓰지 않는다.
- **스타일 취향 금지:** var/explicit type, 중괄호, 파일 정렬은 버그·성능·안정성·유지보수성에 영향이 있을 때만.
- **중복 금지:** 같은 근본 원인의 증상은 한 건으로 묶는다(예: Session Dispose 누락 → Timer·Event·Peer 누수 = 1건).
- **Root Cause 우선:** "Memory가 증가할 수 있다"가 아니라 "DisconnectedSession이 sessionsById에서 제거되지 않아 Player → Timer → Callback 참조 사슬이 살아남는다"처럼 쓴다.

## 3. 문제 한 건의 형식 (82)

```text
[Severity] 제목

Location:
파일 / 클래스 / 함수 (파일:라인)

Problem:
무엇이 문제인지

Why:
왜 문제가 되는지

Scenario:
실제로 문제가 발생할 수 있는 상황

Impact:
Crash / Deadlock / Latency / Memory / Network / DB 등

Fix:
가장 단순한 수정 방향

Verification:
수정 후 확인 방법
```

Commit 리뷰면 제목 뒤에 `(신규)` 또는 `(기존)`을 붙인다. 확실하지 않은 항목은 제목 뒤에 `(확실하지 않음)`을 붙이고 Problem 끝에 확정에 필요한 확인 사항을 적는다.

예시 (83):

```text
[High] DB Writer Queue가 장애 중 무한 증가할 가능성

Location:
PersistenceWorker.cs

Problem:
DB 연결 실패 상태에서도 Queue 입력은 계속 허용되며 Queue 크기 제한이 없다.

Why:
MySQL 장애가 길어질 경우 Match 결과가 계속 Queue에 누적된다.

Scenario:
DB 30분 장애 + 지속적인 Match 종료

Impact:
Memory 증가 → GC 증가 → 최종적으로 OOM 가능

Fix:
Bounded Channel을 사용하고 Full 정책을 명시한다.

Verification:
DB를 끈 상태에서 지속적으로 Match 결과를 생성하고 Queue Length / Memory를 관찰한다.
```

## 4. 최종 보고서 (92)

~~~markdown
# Server Code Review

(범위: 전체 / 파일·기능 / Commit base..target, 기준 Commit 해시)

## Summary

```text
Critical: X
High: X
Medium: X
Low: X
```

전체 서버 상태 3–5줄 요약.

## Critical        ← 있을 때만
## High
## Medium
## Low
(각 문제는 3절 형식)

## Concurrency / Deadlock
```text
Thread Model:
Shared Mutable State:
Lock Count:
Deadlock Risk:
Contention Risk:
```

## Memory / Lifetime
```text
Leak Risk:
Unbounded Collection:
Task Lifetime:
Timer Lifetime:
Session Cleanup:
```

## Game Loop
```text
Blocking:
Allocation:
Complexity:
Tick Risk:
```

## Network
```text
Packet Validation:
Authority:
MTU:
Replication:
Reconnect:
```

## Database
```text
Game Loop DB Blocking:
Connection Lifetime:
Queue:
Failure Handling:
```

## Performance
```text
Confirmed:
Needs Measurement:
```
(실제 코드에서 확인한 것만. 측정 필요는 따로)

## Tests Needed
(발견한 문제를 재현·방지할 테스트)

## Good
(실제로 확인한 좋은 부분만 짧게. 예: DB Write가 Game Loop 밖, Queue bounded, Snapshot allocation 없음)

## Priority
1.
2.
3.
(Severity 순서가 기본. 의존 관계가 있으면 실제 수정 순서로)

## 검증 기록
(지적 수, 확정·기각·미검증 수, 리뷰가 실패한 영역)
~~~

요약 블록의 숫자는 **확정된 지적과 `확실하지 않음` 지적**만 센다. 기각(refuted)된 지적은 보고서 본문에 넣지 않고 검증 기록의 수에만 반영한다.
