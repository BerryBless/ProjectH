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

## 하네스: GitHub 푸시

**목표:** 변경사항·민감정보·빌드를 검증한 뒤 현재 Branch로 Commit·Push한다.

**호출 조건:** 사용자가 "푸시", "github push", "깃허브 푸시"를 명시적으로 입력했을 때만 `github-push` 스킬을 사용한다. 그 외에는 작업이 끝나도 Commit·Push하지 않는다. Force Push는 하지 않는다.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
| --- | --- | --- | --- |
| 2026-09-30 | Harness v2로 처음 구성 | 전체 | - |
| 2026-09-30 | GitHub 푸시 하네스 추가 | `github-push` 스킬, `scan_secrets.sh` | 명시적 호출 시에만 검증 후 Commit·Push |
