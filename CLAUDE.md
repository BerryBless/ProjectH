# ProjectH

Unity(Client) / .NET 10(Server) / MySQL 게임 프로젝트. Client와 Server는 별도 프로젝트이며, 필요할 때만 Protocol / DTO 수준의 Shared를 둔다.

## 항상 적용

모든 코드 작업에 적용한다. 세부 기준은 `game-core-rules` 스킬에, 함수 주석 형식은 `code-comments` 스킬에 있다.

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
함수 주석(기능 / 입력 / 출력)
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

## 하네스: Codex 검증

**목표:** Codex CLI(읽기 전용, reasoning effort high)로 변경분은 diff 범위 파일을, 전체는 영역을 나눠 파일을 끝까지 읽게 검증하고, Codex 지적은 game-reviewer가 반박 검증해 확인된 것만 session id·토큰 기록과 함께 보고한다.

**호출 조건:** 사용자 입력에 "코덱스"가 명시적으로 있을 때만 `codex-review` 스킬을 사용한다("코덱스", "코덱스 <commit>", "코덱스 <ref>..HEAD", "코덱스 전체", "코덱스 서버 전체", "코덱스 클라 전체", "코덱스 다시"). "검토해줘", "리뷰해줘", "서버리뷰", "성능점검", "QA"는 Codex 호출로 해석하지 않는다. 수정은 "코덱스 검증하고 고쳐줘", "코덱스 문제 있으면 수정"처럼 명시했을 때만 한다.

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
| 2026-10-02 | Codex 교차검증 하네스 추가 | `codex-crosscheck` 스킬(`run_codex.sh`, `codex-prompt.md`, `findings.schema.json`) | "코덱스" 키워드로만 현재 변경분을 Codex(read-only)로 짧게 2차 검증. 결과는 리더가 코드·테스트로 확인한 것만 보고 |

| 2026-10-03 | Codex 실행에서 `--ephemeral` 제거 | `codex-crosscheck` `run_codex.sh`, SKILL.md 2단계 | 세션이 Codex 기록에 남지 않아 사용자가 실행 여부를 확인할 수 없었음. read-only는 유지 |
| 2026-10-03 | Codex 하네스 삭제 후 재구성 (`codex-crosscheck` → `codex-review`) | `codex-review` 스킬(`codex_diff.sh`, `codex_full.sh`, `codex_area.sh`, `plan_areas.py`, `summarize.py`, 프롬프트 2개, 영역 Schema), CLAUDE.md 호출 조건 | effort none·최대 3건·540초 제한으로 서버 1.6만 줄 중 12파일만 훑고 "전체 검증"을 할 수 없었음. 변경분은 diff 파일 목록을 `codex exec`(Schema 응답, 읽은 파일 대조)로(내장 `codex review`는 --commit과 지시문을 함께 받지 않아 맥락을 못 넘김), 전체는 약 2,500줄 영역을 3개씩 동시에 effort high로 실행하고 읽은 파일을 대조, 지적은 game-reviewer가 반박 검증, 세션 기록 유지 |
| 2026-10-05 | 함수 주석 규칙 추가 | `code-comments` 스킬(+`references/examples.md`), `game-core-rules` 19절 포인터, server·client-engineer 작업 원칙, game-reviewer safety 기준, CLAUDE.md 항상 적용 | 코드를 수정할 때마다 추가·수정한 함수에 기능/입력/출력 주석을 달고, 동작이 바뀐 함수의 주석을 고쳐 코드와 주석이 어긋나지 않게 함. 건드린 함수에만 적용 |
| 2026-10-08 | Shared 예외에 건설 편집 모양 추가, 확정 조각의 편집 예측 허용 | `game-core-rules` 4절 예외 4 끝·예외 5 | 편집된 모양으로 이동·예측 충돌·미리보기가 서버와 같아야 하고 Client 선택과 서버 검증이 같은 유효성 규칙을 써야 함. 편집 권한·사거리·시선·끼임·지지는 서버에만 둠 (Phase 13.5 D1–D3, D11) |
| 2026-10-08 | Shared 예외에 기절 이동과 Reboot Station 좌표 추가 | `game-core-rules` 4절 예외 6 | 기절 기어가기를 예측과 서버가 같이 계산해야 하고, 스테이션 좌표는 맵 배치 상수. 기절·소생·재투입·팀 규칙과 `squad.json`은 서버, Client 표시 상수는 `SquadPrompt` 복사본 + 서버 비교 테스트 (Phase 14 D4, D10) |
| 2026-10-08 | Shared 예외에 Loot Container 배치와 Supply Drop 낙하 계산 추가 | `game-core-rules` 4절 예외 7 | 상자 좌표는 맵 배치 상수이고 Client 표시·안내도 쓴다. 낙하 위치는 서버가 보낸 Tick으로 양쪽이 같이 계산해야 함. Loot Table·굴림·일정·착지 자리·열기 판정과 `loot.json`은 서버 (Phase 16) |
| 2026-10-08 | Shared 예외에 차량 운동 계산·차량 상수·차량 생성 위치 추가 | `game-core-rules` 4절 예외 8 | 운전자 예측과 서버가 같은 차량 결과를 내야 하고 생성 위치는 맵 배치 상수. 탑승·하차·피해·관심 영역·`vehicles.json`은 서버, Client 안내는 `VehiclePrompt` 복사본 + 서버 비교 테스트 (Phase 19 D2, D8, D12) |

## 에이전트 브랜치·PR 규칙 (2026-11-07)

**목표:** 에이전트는 main 에 직접 올리지 않는다. 작업 브랜치 → push → Pull Request 로 올리고, **머지는 사용자만 GitHub 에서 한다.**

1. **작업 시작 전** `git branch --show-current` 를 확인한다. `main` 이면 파일을 고치기 전에 `git switch -c agent/<작업요약-영문-소문자>` 로 작업 브랜치를 만든다. 브랜치 생성은 커밋 하네스(github-push / commit-push 등) 밖의 "작업 시작" 단계다. 이후 커밋·push 는 기존 규칙대로 **현재 브랜치에만** 한다.
2. `agent/*` 브랜치에 upstream 이 없으면 `git push -u origin <브랜치>` 로 올린다. 이 경우는 사용자에게 묻지 않는다.
3. push 뒤 `gh pr create --base main --title "<커밋 제목>" --body "<본문>"` 으로 PR 을 연다(이미 열려 있으면 생략). PR 본문과 커밋 본문에 `Prompt: <사용자 요청 문장>`, 변경 요약, 검증 명령·결과를 적는다. PR 링크를 사용자에게 보고하고 끝낸다.
4. **금지:** main 에서의 commit·push, main 으로의 push, `git merge` 로 main 에 합치기, force push, `gh pr merge`. 머지는 사용자가 한다.
5. **강제 수단:** PreToolUse 훅 `.agents/hooks/protect_main.py` (Claude Code: `.claude/settings.json`, Codex: `.codex/hooks.json`) 와 `.claude/settings.json` 의 deny 규칙이 4번을 차단한다. 훅이 막으면 우회하거나 끄지 말고 1번으로 돌아간다.
6. 이 절은 이 파일의 다른 커밋·push 규칙보다 우선한다. 충돌하는 문장은 "현재 브랜치가 `agent/*` 일 때 기존 규칙을 적용한다" 로 읽는다.
