# Phase 9 Persistence — 설계 Spec

## Context

원래 요청서의 Phase 9는 다음을 구현한다.

```text
MySQL 연동. Player Profile / Stats / Match History
```

관련 조항은 다음과 같다.

- §36: MySQL에는 영속 데이터만 저장한다(Account, PlayerProfile, Statistics, MatchHistory). 실시간 위치는 저장하지 않는다.
- §37: 최소 저장 항목은 MatchId, PlayerId, Kills, Placement, Damage, SurvivalTime, CreatedAt이다. 이벤트마다 쓰지 않고, 경기가 끝난 뒤 정리해서 저장한다.
- §38: 초기에는 개발용 인증(DevPlayerId)을 쓴다. 비밀번호를 평문으로 저장하지 않는다.
- §44 Queue: 최대 크기, 생산자, 소비자, 넘침 정책을 정한다.
- 절대 규칙 4: DB를 Game Tick에서 직접 기다리지 않는다.
- `game-core-rules`: DB Connection은 정상 반환하고, Transaction은 짧게 한다.

**사용자가 확인한 것(2026-10-01):**
- MySQL: Docker 컨테이너(`docker-compose.yml`)
- 드라이버: NuGet `MySqlConnector`
- 스키마: 아래 추천안

**성공 기준:**

- `docker compose up -d` 하나로 개발용 MySQL이 뜬다. 서버는 시작할 때 스키마를 만든다.
- 경기가 끝나면 참가자 전원의 기록이 한 Transaction으로 저장된다.
  - 저장 항목: 순위, 처치, 피해, 생존 시간
  - 중간 이탈자도 포함한다.
  - 누적 통계(경기 수, 승리, 처치, 사망, 피해, 생존 시간)가 갱신된다.
- Game Loop는 DB를 기다리지 않는다. 경기 기록은 넘치지 않는 큐로 별도 작업에 넘긴다.
- DB가 없거나 꺼져 있어도 서버는 정상 동작한다. 기록만 남지 않고, 그 사실을 로그와 카운터로 알 수 있다.
- 모든 동작을 테스트로 검증한다. DB 테스트는 연결 문자열 환경 변수가 있을 때만 돈다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 진행 방식 | 추천안대로 진행하고 추천 이유는 이 문서에 남긴다. Phase마다 푸시한다. |
| MySQL 실행 | Docker 컨테이너(추천) |
| 드라이버 | MySqlConnector(추천) |
| 스키마 | 추천 스키마로 진행 |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **개발용 MySQL은 저장소 루트의 `docker-compose.yml`(mysql:8.4)이다.**<ul><li>127.0.0.1:3306에만 연다.</li><li>데이터는 이름 있는 볼륨에 둔다.</li><li>개발용 계정 `projecth` / `projecth_dev`를 쓴다. 환경 변수 `PROJECTH_DB_PASSWORD`로 바꿀 수 있다.</li></ul> | 이 PC에는 Docker가 있고 설치형 MySQL은 없다. 명령 하나로 같은 환경을 만들 수 있다. 외부 인터페이스에 열지 않으므로 개발용 비밀번호가 밖으로 노출되지 않는다. | 운영 배포에서는 다른 DB를 쓴다. 연결 문자열을 환경 변수로 덮어쓰면 된다. |
| D2 | **드라이버는 `MySqlConnector`(MIT)이고 ORM은 없다.** 매개변수화한 SQL을 직접 쓴다. | 완전한 async I/O라 스레드를 막지 않는다(§49). 쓰는 쿼리가 몇 개뿐이라 ORM은 과한 설계다. 매개변수화로 SQL Injection을 막는다. | 쿼리가 많아지면 Dapper 정도를 검토한다. |
| D3 | **스키마**(서버가 시작할 때 `CREATE TABLE IF NOT EXISTS`로 만든다)<ul><li>`account(id, dev_player_id UNIQUE, created_at)`</li><li>`player_profile(account_id, display_name, updated_at)`</li><li>`player_stats(account_id, matches, wins, kills, deaths, damage, survival_ms, updated_at)`</li><li>`game_match(id, round_no, started_at, ended_at, players, winner_account_id)`</li><li>`match_player(match_id, account_id, placement, kills, damage, survival_ms)`, 인덱스 `(account_id, match_id)`</li></ul>`MATCH`는 MySQL 예약어라 `game_match`로 쓴다. 시각은 UTC `DATETIME(3)`이다. 비밀번호 열은 없다(DevPlayerId 인증 유지, §38). 모든 표는 `utf8mb4` / `utf8mb4_0900_bin`이다. 이진 비교이고 NO PAD라서 DB에서 같은 `dev_player_id`는 C#의 서수(ordinal) 비교로 같은 값과 정확히 일치한다(PAD SPACE 콜레이션이면 끝 공백만 다른 두 id가 한 계정이 된다). MySQL 8.0.19 이상이 필요하다(`INSERT ... AS new ON DUPLICATE KEY UPDATE`, `utf8mb4_0900_bin`). | §36·§37의 항목을 그대로 담는다. 통계를 누적 표로 두면 조회가 행 하나로 끝난다. `(account_id, match_id)` 인덱스로 "내 최근 경기" 조회가 인덱스만으로 된다. 이름이 같은 `CREATE IF NOT EXISTS`면 마이그레이션 도구 없이도 반복 실행이 안전하다. | 열을 바꿀 때는 마이그레이션 단계가 필요하다. 그때 버전 표를 더한다. |
| D4 | **Game Loop는 경기가 끝나는 Tick에 기록 하나(`MatchRecord`)를 만들어 넘긴다.**<ul><li>참가자마다 다음을 기록한다.<ul><li>순위, 처치</li><li>피해: 다른 플레이어에게서 실제로 깎은 Shield + Health. 넘치는 피해와 Zone 피해는 넣지 않는다.</li><li>생존 시간: 경기 시작부터 탈락 Tick까지. 살아남은 사람은 종료 Tick까지다.</li></ul></li><li>중간 이탈자는 떠날 때 기록해 두고 넣는다. 죽은 뒤에 떠난 참가자는 한 번만, 죽었을 때의 순위·생존 시간으로 들어간다.</li><li>승자는 남아 있는 1위다. 마지막 둘이 같은 Tick 전에 모두 떠나 1위가 이탈자이면, 결과 패킷의 `WinnerId`는 0이지만 기록의 승자는 그 이탈자다.</li><li>경기 중 합류한 관전자는 넣지 않는다.</li><li>`DevRespawn` 샌드박스는 기록하지 않는다.</li><li>기록을 만드는 할당은 경기마다 한 번이다(Tick마다가 아니다).</li></ul> | §37의 "경기 종료 후 정리하여 저장"이다. 접속 때는 DB를 전혀 건드리지 않는다. 계정도 저장할 때 `dev_player_id`로 만든다. 그래서 Join이 DB 지연에 묶이지 않는다. 피해를 실제로 깎은 양으로 세면 오버킬로 통계가 부풀지 않는다. | 접속할 때 내 통계를 보여 주는 기능이 생기면 읽기 경로를 따로 만든다(D10). |
| D5 | **저장은 별도 async 작업(`MatchHistoryWriter`, Hosted Service)이 한다.**<ul><li>한 경기를 Transaction 하나로 저장한다: 계정 upsert → 프로필(없으면) → 경기 → 참가자 → 통계 누적. 실패하면 전부 롤백한다.</li><li>Connection은 매번 풀에서 받아 `await using`으로 돌려준다.</li><li>쓰는 쪽이 하나뿐이라 Transaction끼리 잠금 순서가 엇갈리는 Deadlock이 없다.</li><li>한 기록 안에 같은 DevPlayerId가 둘 이상이면(같은 id로 두 Client가 접속) 계정마다 순위가 가장 좋은 기록 하나만 쓴다. 가장 작은 0이 아닌 순위가 가장 좋고, 순위 0(미정)은 가장 나쁘며, 같으면 앞의 것이다. `players`는 실제로 쓴 행 수이고 `winner_account_id`는 남긴 행과 맞는다. `(match_id, account_id)` 키 충돌로 경기 전체가 사라지지 않게 하고, 중복된 쪽이 이겼으면 승리가 남는다.</li></ul> | 절대 규칙 4. Game Loop 스레드는 큐에 넣기만 한다. Transaction이 짧고(경기당 행 수백 개 이하) 한 번에 끝나므로 일부만 저장되는 경기가 없다. | 저장이 느려도 게임은 영향받지 않는다. 큐가 넘치면 D6에 따라 버린다. |
| D6 | **큐(§44): `MatchHistoryQueue`(bounded Channel)**<ul><li>최대 16개(`QueueCapacity`, 1–1024)</li><li>생산자: Game Loop(경기 종료 때 한 번)</li><li>소비자: Writer 하나</li><li>넘침 정책: Reject. `TryWrite`가 실패하면 기록을 버리고 `Dropped`를 센다. 게임은 절대 기다리지 않는다.</li></ul> | 경기는 몇 분에 한 번 끝나므로 16개가 차려면 DB가 오래 멈춰 있어야 한다. 그때 메모리를 계속 쓰는 것보다 버리고 세는 편이 안전하다(§43 무한 증가 금지). | DB가 오래 멈추면 그동안의 경기 기록은 사라진다. 재시도 큐 영속화는 Hardening(Phase 10)에서 검토한다. |
| D7 | **실패 처리.**<ul><li>저장 실패는 기록마다 최대 3번 시도한다(`MaxAttempts`, 1초·2초 간격). 그래도 실패하면 로그(경기 번호·인원)와 `Failed`를 남긴다.</li><li>일시적이지 않은 `MySqlException`(제약·데이터 오류, `IsTransient == false`)은 매번 같은 이유로 실패하므로 다시 시도하지 않고 바로 `Failed`로 센다.</li><li>재시도는 at-least-once다. COMMIT은 성공했는데 응답만 잃으면 같은 경기가 두 번 저장될 수 있다.</li><li>Writer 작업(`ExecuteAsync`)은 예외를 밖으로 내보내지 않는다. `BackgroundService`가 실패하면 Host 전체가 멈추기 때문이다. 예상 밖 예외면 로그를 남기고 읽기를 멈추며, 남은 기록은 종료 때 `Discarded`로 센다.</li><li>Game Loop 쪽: `FinishMatch`는 모든 `MatchResult`를 보낸 뒤에 sink를 부르고, sink 예외는 잡아서 `Match.MatchSinkFailures`로 센다(Stats 줄 `matchSinkFailures`). 저장 쪽 문제로 결과 전송이나 Tick이 멈추지 않는다.</li><li>시작할 때 DB에 연결하지 못하면 이번 실행은 저장하지 않는다. 기록은 버리고 `Discarded`로 센다. 서버는 정상 동작한다.</li></ul> | DB는 게임의 필수 조건이 아니다(§36 "영속 데이터에만 사용"). 개발자가 Docker를 띄우지 않아도 서버와 테스트가 돈다. | 재연결은 Phase 10(Hardening)에서 한다. 이번에는 서버를 다시 켜면 다시 연결한다. |
| D8 | **종료 순서.** Writer를 게임 서버보다 먼저 등록한다. Host는 등록의 역순으로 멈추므로, Game Loop가 먼저 멈추고 그 다음 Writer가 멈춘다. Writer는 큐를 닫고 남은 기록을 `ShutdownDrainSeconds`(기본 5초, 1–60) 동안 저장한다. 시간이 다 되면 진행 중인 저장을 취소한다. 취소된 저장과 큐에 남은 기록은 판 번호·인원을 로그로 남기고 `Discarded`로 센다. Host `ShutdownTimeout`은 90초(Drain 최대 60초 + 30초)로 둔다. Host 기본값 30초는 모든 Hosted Service의 `StopAsync`가 함께 쓰므로, 그대로 두면 Drain 제한보다 먼저 잘린다. | 마지막 경기 기록이 종료 때문에 사라지지 않게 한다. 시간 제한이 있어 종료가 무한정 늘어지지 않는다. | 없음. |
| D9 | **설정(`appsettings.json`의 `Persistence` 절).**<ul><li>`Enabled`(true)</li><li>`ConnectionString`(로컬 컨테이너, 개발용 비밀번호, `Connection Timeout=5`)</li><li>`QueueCapacity` 16(1–1024), `MaxAttempts` 3(1–10), `ShutdownDrainSeconds` 5(1–60)</li></ul>시작할 때 검증한다. 운영은 환경 변수 `Persistence__ConnectionString`으로 덮어쓴다. | 기본값만으로 Docker 개발 환경에서 바로 돈다. 개발용 비밀번호는 127.0.0.1 전용 컨테이너의 더미 값이다. 진짜 비밀은 저장소에 넣지 않는다. | 커밋 검사(`scan_secrets.sh`)가 이 더미 값을 잡을 수 있다. Push 보고에 근거를 적는다. |
| D10 | **지금 넣지 않는 것:**<ul><li>Client에 통계·전적 보여 주기(새 패킷과 UI)</li><li>실제 인증·비밀번호, 표시 이름 바꾸기</li><li>DB 재연결, 실패 기록의 디스크 보관</li><li>마이그레이션 도구, 읽기 API 서버</li></ul> | 저장이 이 Phase의 범위다. 읽기는 `MatchStore`의 조회 메서드로 테스트와 도구가 쓴다. 나머지는 Hardening·인증 단계에서 다룬다. | 없음. |

## 1. 구조 (`Server/src/ProjectH.Server/Persistence`)

| 파일 | 책임 |
|---|---|
| `MatchRecord.cs` | `MatchRecord`(판 번호, 시작·종료 UTC, 승자 DevPlayerId, 참가자 목록), `PlayerRecord`(DevPlayerId, 순위, 처치, 피해, 생존 ms). 불변이다. |
| `PersistenceOptions.cs` | D9 설정과 검증 |
| `MatchHistoryQueue.cs` | D6 큐(`TryEnqueue`, `Reader`, `Complete`, `Dropped`) |
| `MatchStore.cs` | D3 스키마, D5 저장 Transaction, 조회(`GetStatsAsync`, `GetHistoryAsync`) |
| `MatchHistoryWriter.cs` | D5·D7·D8: Hosted Service. 카운터는 `Saved`, `Failed`, `Discarded`다. |

**Game Loop 쪽:**

- `PlayerEntity`에 `DamageDealt`, `EliminatedTick`을 더한다.
- `Match`에서 바뀌는 것:
  - 피해를 셀 때: `ApplyHit`
  - 탈락 Tick을 남길 때: `Kill`, `Leave`
  - 이탈자를 기록할 때: `Leave`
  - 경기를 시작할 때: `StartMatch`(초기화)
  - 경기가 끝날 때: `FinishMatch`(기록을 만들어 sink에 넘긴다)
- `Match`와 `GameLoop`는 선택 인자 `Action<MatchRecord>? matchSink`를 받는다. null이면 기록하지 않는다.
- `GameServerService`와 `Program`은 큐를 싱글턴으로 등록하고, Writer → 게임 서버 순으로 Hosted Service를 등록한다.

## 2. 테스트

- **기록(DB 없음):**
  - 끝난 경기 하나에 기록 하나: 순위, 처치, 피해(오버킬 없음), 생존 시간, 승자
  - 결과 화면과 다음 카운트다운은 기록하지 않는다.
  - 이탈자가 포함된다(순위·생존 시간).
  - 죽은 뒤 떠난 참가자는 한 번만, 죽었을 때 기준으로 들어간다.
  - Shield만 깎은 피해도 센다.
  - 떠난 1위가 기록의 승자다.
  - 관전 합류자는 빠진다.
  - `DevRespawn`은 기록이 없다.
  - sink가 없어도 경기는 정상으로 끝난다.
  - sink는 모든 결과 패킷을 보낸 뒤에 불린다. sink가 예외를 던지면 세고, 결과는 나가며 다음 판으로 넘어간다.
- **큐·설정:**
  - 큐가 차면 거절하고 센다. 닫힌 큐도 거절한다.
  - 설정을 검증한다(`ShutdownDrainSeconds` 0은 거부). 출하 설정이 유효하다.
- **DB 없는 Writer:**
  - 기록을 버리고 세며, 예외 없이 멈춘다.
  - 연결 중에 Drain 제한이 오면(인사하지 않는 TCP 서버) 작업이 조용히 끝나고(`RanToCompletion`) 남은 기록을 센다.
- **MySQL(환경 변수 `PROJECTH_TEST_MYSQL`이 있을 때만):**
  - 스키마를 두 번 만들어도 된다(멱등).
  - 두 경기를 저장하면 통계가 누적되고, 전적이 최신순으로 나온다.
  - 실패하는 저장(32자를 넘는 DevPlayerId)은 아무것도 남기지 않는다(롤백).
  - 중복 DevPlayerId는 한 행만 쓰고 경기를 지킨다. 끝 공백만 다른 id는 다른 계정이다(NO PAD).
  - 중복 중 뒤의 기록이 더 좋은 순위(승리)면 그 기록이 남고 승리와 `winner_account_id`가 맞는다. 순위 0은 진짜 순위에 진다.
  - Hosted Writer가 넣은 기록을 저장하고, 종료 때 큐를 비운다.
  - Drain 제한에 걸린 진행 중 저장(행 잠금으로 막음)은 `Failed`가 아니라 `Discarded`로 센다.
- **테스트 수:** `[MySqlFact]` 6개. DB 없이 `dotnet test`는 658 통과 + 6 건너뜀(전체 664)이다. Persistence 네임스페이스 테스트는 21개(MySQL 6개 포함)다.
- **계획 단계 확인(리뷰 수정 전):**
  - 컨테이너로 당시 테스트가 모두 통과했다.
  - Release 서버가 시작할 때 "connected, schema ready"를 남겼다.

## 3. 문서

- `Docs/Database.md`: 실행 방법, 스키마, 저장 흐름, 실패 처리, 테스트 실행 방법
- `Docs/Server.md`, `Docs/Architecture.md`: MySQL과 Writer를 넣는다.

## 4. 범위 밖

D10의 항목.

## 5. 구현 중 변경

리뷰에서 나온 것을 반영했다. 위 결정 표는 이미 바뀐 내용으로 적었다.

| 변경 | 이유 |
|---|---|
| sink 호출을 `MatchResult` 전송 뒤로 옮기고 예외를 잡아 센다(D7) | 저장 쪽 예외나 지연이 플레이어 결과와 Game Loop를 막으면 안 된다. |
| 떠난 1위를 기록의 승자로 쓴다(D4) | 둘 다 떠난 판도 승자가 통계에 남아야 한다. |
| 일시적이지 않은 `MySqlException`은 재시도하지 않는다(D7) | 제약·데이터 오류는 몇 번 해도 같다. 재시도는 종료만 늦춘다. |
| 표 콜레이션을 `utf8mb4_0900_bin`(NO PAD)으로 한다(D3) | PAD SPACE면 끝 공백만 다른 id가 한 계정이 되어 C#의 중복 판단과 어긋난다. |
| 중복 DevPlayerId는 첫 기록이 아니라 가장 좋은 순위를 남긴다(D5) | 같은 id의 두 번째 Client가 이겼을 때 승리가 사라지지 않게 한다. |
| Writer의 `ExecuteAsync` 전체를 감싼다(D7) | 연결 중 취소나 예상 밖 예외가 `BackgroundService`를 실패시키면 Host가 멈춘다. |
| Drain 제한에 걸린 진행 중 저장도 로그를 남기고 `Discarded`로 센다(D8) | 그 기록은 저장되지 않았는데 어느 카운터에도 안 잡혔다. |
| `ShutdownDrainSeconds`는 1–60(0 금지), Host `ShutdownTimeout` 90초(D8, D9) | 0은 언제나 시간 초과처럼 보였다. Host 기본 30초는 Drain 최대 60초보다 짧다. |
