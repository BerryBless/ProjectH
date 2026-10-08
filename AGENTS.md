# ProjectH

Unity(Client) / .NET 10(Server) / MySQL 게임 프로젝트. Client와 Server는 별도 프로젝트이며, 필요할 때만 Protocol / DTO 수준의 Shared를 둔다.

## 항상 적용

모든 코드 작업에 적용한다. 세부 기준은 `game-core-rules` 스킬에 있다.

```text
Client / Server 분리
정확성
안정성
단순한 구조
Resource Lifetime 관리
무한 증가 Collection 방지
외부 입력 검증
Server Blocking 최소화
DB Connection 정상 반환
짧은 Transaction
Lock 사용 시 Deadlock 검토
Lock Ordering 유지
```

```text
안전성은 항상 확인한다.
성능은 관련된 코드에서만 확인한다.
최적화는 문제가 있을 때 한다.
Lock을 썼다면 Deadlock은 항상 확인한다.
```

## 하네스: 게임 개발

**목표:** 기본 안전성 규칙을 항상 지키면서, 변경 내용과 관련된 성능·동시성 점검만 켜서 Client / Server / DB 코드를 구현하고 검증한다.

**호출 조건:** Client·Server·Shared·DB 코드의 기능 구현·수정 요청, 또는 "성능점검", "성능검토", "최적화" 요청을 받으면 `game-dev-orchestrator` 스킬을 사용한다. 코드 설명이나 단순 질문에는 직접 답해도 된다. 오케스트레이터 없이 코드를 직접 고칠 때도 `game-core-rules`는 적용한다.

## 하네스: 서버 리뷰

**목표:** 서버 구조·Thread Model·Ownership을 먼저 파악하고 영역별 리뷰와 반박 검증으로 Critical~Low 문제를 근본 원인 단위로 보고한다. 요청이 없으면 코드를 수정하지 않는다.

**호출 조건:** 사용자가 "서버리뷰", "서버 리뷰", "서버 코드 리뷰", "server review"를 입력하거나 서버 전체·기능·Commit 리뷰를 요청하면 `server-review` 스킬을 사용한다. 수정은 "고쳐줘", "수정까지"처럼 명시했을 때만 한다.

## 하네스: GitHub 푸시

**목표:** 변경사항·민감정보·빌드를 검증한 뒤 현재 Branch로 Commit·Push한다.

**호출 조건:** 사용자가 "푸시", "github push", "깃허브 푸시"를 명시적으로 입력했을 때만 `github-push` 스킬을 사용한다. 그 외에는 작업이 끝나도 Commit·Push하지 않는다. Force Push는 하지 않는다.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
| --- | --- | --- | --- |
| 2026-09-30 | Harness v2로 처음 구성 | 전체 | - |
| 2026-09-30 | GitHub 푸시 하네스 추가 | `github-push` 스킬, `scan_secrets.sh` | 명시적 호출 시에만 검증 후 Commit·Push |
| 2026-09-30 | Shared 이동 계산 예외 추가 | `game-core-rules` 4절 | Client Prediction과 서버가 같은 이동 코드를 써야 함 |
| 2026-09-30 | Shared 예외에 지형 박스·충돌 계산 추가 | `game-core-rules` 4절 | 서버와 예측이 같은 충돌 결과를 내야 함 (Phase 1 D7) |
| 2026-10-01 | Shared 예외에 맵 배치 데이터(Loot Spawn Point 좌표 상수) 추가 | `game-core-rules` 4절 | 맵을 바꿀 때 박스와 함께 고쳐야 함. 규칙은 넣지 않음 (Phase 4 D6) |
| 2026-10-01 | Shared 예외에 높이 격자 지형, 투입 지점, POI 추가 (`TestArena` → `GameMap`) | `game-core-rules` 4절 | 이동 예측이 서버와 같은 지형을 써야 하고, 맵 배치를 박스·지형과 함께 고쳐야 함 (Phase 6 D8) |
| 2026-10-01 | 작업 경로에 봇(`Server/src/ProjectH.Bots`) 추가 | `game-core-rules` 작업 대상 경로 | 봇은 서버 폴더에 있지만 프로토콜로만 통신하는 Client임을 명시 (Phase 7 D1) |
| 2026-10-02 | Shared 예외에 이동 모드·Vault 판정·수송기 경로 위치(`DropRoute`·`Ride`)·문 상자(`GameMap.Doors`) 추가 | `game-core-rules` 4절 | 예측과 서버가 같은 이동 결과를 내야 함. 경로 난수와 문 상호작용 규칙은 Shared 밖, 두 규칙 복사본이 같이 읽는 문 상호작용 거리·각도 상수만 Shared, 서버 전용 낙하 피해 상수는 서버(`CombatRules`) (Phase 12) |
| 2026-10-02 | Shared 예외에 건설 격자·조각 모양·경사면·충돌 후보 수집·채집 대상 상자 추가 | `game-core-rules` 4절 | 예측과 서버가 같은 조각 충돌을 계산해야 함. 검증·지지·복제 규칙은 서버에만 둠 (Phase 13 D1–D3, D6) |
| 2026-10-02 | 서버 리뷰 하네스 추가 | `server-review` 스킬, `server-reviewer` 에이전트, `game-dev-orchestrator` description | "서버리뷰" 키워드로 서버 전체 감사(구조 파악 → 6개 영역 리뷰 → 반박 검증 → 보고서). 변경분 점검(game-reviewer)과 구분 |
| 2026-10-02 | 작업 경로에 QA 도구(`Server/src/ProjectH.QA`, `QA/`)와 서버 QA Control(`Server/src/ProjectH.Server/Qa/`) 추가 | `game-core-rules` 작업 대상 경로 | QA 도구는 봇처럼 Protocol과 QA HTTP로만 통신하는 Client. 서버 QA 코드는 한 폴더에만 두고 Match에 QA 분기를 넣지 않으며, 명령은 GameLoop 스레드에서만 실행 (QA-1, Docs/QA.md) |
