# Database

Phase 9(Persistence), Phase 10(DB 재연결). MySQL에는 영속 데이터만 저장한다: 계정, 프로필, 누적 통계, 경기 기록. 실시간 위치·전투 상태는 저장하지 않는다. 설계 근거: `Docs/specs/2026-10-01-phase9-persistence-design.md`, `Docs/specs/2026-10-01-phase10-hardening-design.md`(D8).

Game Loop는 DB를 기다리지 않는다. DB가 없거나 꺼져 있어도 서버는 정상 동작하고, 기록만 남지 않는다.

**MySQL 8.0.19 이상이 필요하다.** 통계 누적에 `INSERT ... AS new ON DUPLICATE KEY UPDATE`(8.0.19부터)를, 표에 Collation `utf8mb4_0900_bin`을 쓴다. 개발 컨테이너는 mysql:8.4다.

## 실행

```bash
docker compose up -d     # 개발용 MySQL(mysql:8.4) 시작
docker compose down      # 중지(데이터는 볼륨에 남는다)
```

- 저장소 루트의 `docker-compose.yml`. `127.0.0.1:3306`에만 열고, 데이터는 이름 있는 볼륨(`projecth-mysql-data`)에 둔다.
- 개발용 계정: DB `projecth`, 사용자 `projecth`, 비밀번호 `projecth_dev`. 127.0.0.1 전용 컨테이너의 더미 값이다.
- 서버는 시작할 때 스키마를 만든다(`CREATE TABLE IF NOT EXISTS`). 따로 초기화할 것이 없다.
- 덮어쓰기: 컨테이너 비밀번호는 환경 변수 `PROJECTH_DB_PASSWORD`(root는 `PROJECTH_DB_ROOT_PASSWORD`), 서버 연결은 `Persistence__ConnectionString`. 운영이나 공유 환경에서는 반드시 덮어쓴다.
- `appsettings.json`의 `Persistence` 절:

| 키 | 기본값 | 범위 / 의미 |
|---|---|---|
| Enabled | true | false면 DB에 연결하지 않고 기록을 버린다 |
| ConnectionString | 로컬 컨테이너, `Connection Timeout=5` | `Enabled`이면 필수 |
| QueueCapacity | 16 | 1–1024. 경기 기록 큐 크기 |
| MaxAttempts | 3 | 1–10. 기록 하나당 저장 시도 횟수 |
| ShutdownDrainSeconds | 5 | 1–60. 종료 때 남은 기록을 저장하는 시간(0은 언제나 시간 초과가 되므로 받지 않는다) |

잘못된 값이면 시작 시 종료된다(`PersistenceOptions.Validate`).

개발 DB 초기화: 스키마가 `CREATE TABLE IF NOT EXISTS`라서, 이 Phase의 정의가 바뀌기 전에 만든 개발 DB는 옛 정의를 그대로 유지한다(예: 대소문자 구분 Collation이 없는 표). 처음부터 다시 만들려면 아래를 실행한다. **로컬 DB의 모든 데이터가 지워진다.**

```bash
docker compose down -v
docker compose up -d
```

## 스키마

`MatchStore.Schema`와 같다. InnoDB, `utf8mb4`, Collation `utf8mb4_0900_bin`이다(NO PAD, 대소문자·끝 공백을 구분하므로 DB의 같음 비교가 서버의 `DevPlayerId` 비교와 일치한다). 시각은 UTC `DATETIME(3)`이다. `MATCH`는 예약어라 `game_match`를 쓴다. 비밀번호 열은 없다(DevPlayerId 인증).

| 표 | 열 | 키 |
|---|---|---|
| `account` | `id` BIGINT AUTO_INCREMENT, `dev_player_id` VARCHAR(32), `created_at` | PK `id`, UNIQUE `dev_player_id` |
| `player_profile` | `account_id`, `display_name` VARCHAR(32), `updated_at` | PK `account_id`, FK → `account` |
| `player_stats` | `account_id`, `matches`, `wins`, `kills`, `deaths` INT, `damage`, `survival_ms` BIGINT, `updated_at` | PK `account_id`, FK → `account` |
| `game_match` | `id` BIGINT AUTO_INCREMENT, `round_no` INT, `started_at`, `ended_at`, `players` TINYINT UNSIGNED, `winner_account_id` NULL | PK `id`, FK `winner_account_id` → `account` |
| `match_player` | `match_id`, `account_id`, `placement` TINYINT UNSIGNED, `kills` SMALLINT UNSIGNED, `damage` INT, `survival_ms` INT, (v2) `shots`, `pellets`, `hits`, `rewind_ticks`, `rewind_clamped`, `max_hit_distance_cm`, `movement_anomalies`, `max_aim_turn` INT NOT NULL DEFAULT 0 | PK `(match_id, account_id)`, 인덱스 `(account_id, match_id)`, FK → `game_match`, `account` |
| `schema_version` | `id` TINYINT UNSIGNED(늘 1), `version` INT, `updated_at` | PK `id` |

- **스키마 버전(리뷰 수정 C7):** 지금 버전은 2(`MatchStore.SchemaVersion`)다. `EnsureSchemaAsync`는 표를 `CREATE TABLE IF NOT EXISTS`로 만든 뒤 `schema_version`을 읽는다. 행이 없으면(Phase 9의 기존 DB) 1이다. 2보다 낮으면 `match_player`의 Anti-cheat 열 8개를 열마다 `information_schema.COLUMNS`로 확인해 없는 것만 `ALTER TABLE … ADD COLUMN … INT NOT NULL DEFAULT 0`으로 더하고 버전 2를 적는다(`GREATEST`로 더 높은 버전은 낮추지 않는다). 새 DB도 같은 길로 v1 표를 만든 뒤 열을 더한다. 몇 번을 실행해도, 중간에 끊겨 일부 열만 있는 DB에서 다시 실행해도 안전하다. 확인과 ALTER 사이에 다른 서버가 먼저 더하면 중복 열 오류(1060)를 무시한다. MySQL DDL은 Transaction 밖(열마다 자동 Commit)이라 짧은 Lock만 잡는다. 버전이 2보다 높으면 아무것도 하지 않는다. 기존 행의 새 열은 0이다.

- `player_profile.display_name`은 처음 저장할 때 `dev_player_id`로 채운다. 이름을 바꾸는 기능은 아직 없다.
- `game_match.players`는 실제로 `match_player`에 쓴 행 수다.
- `(account_id, match_id)` 인덱스로 "내 최근 경기" 조회가 인덱스만으로 끝난다.

## 저장 흐름

```text
Game Loop(경기가 끝나는 Tick) → MatchRecord 하나 → MatchHistoryQueue(최대 16, Reject)
  → MatchHistoryWriter(async, Game Loop 밖) → MatchStore.SaveAsync(Transaction 하나) → MySQL
```

- Game Loop는 경기 종료 Tick에 `Match.BuildRecord`로 기록 하나를 만들고 `TryEnqueue`만 한다. 기록은 불변이고 경기마다 한 번만 할당한다. 접속·Tick 중에는 DB를 건드리지 않는다.
- 큐: 크기 `QueueCapacity`(16), 생산자는 Game Loop, 소비자는 Writer 하나. 가득 차면(또는 닫혔으면) Reject: 기록을 버리고 `Dropped`를 센다. 게임은 절대 기다리지 않는다.
- Writer: Hosted Service 하나가 큐를 읽어 기록마다 `SaveAsync`를 부른다. Connection은 매번 풀에서 받아 `await using`으로 돌려준다.
- Transaction 하나(짧다, 경기당 행 수백 개 이하): 계정 upsert → 프로필(없으면) → `game_match` → `match_player` → `player_stats` 누적. 실패하면 전부 롤백한다. 쓰는 쪽이 하나뿐이라 Transaction끼리 잠금 순서가 엇갈리는 Deadlock이 없다.
- 계정은 저장할 때 `dev_player_id`로 만든다(처음 보는 id면 새로 만든다).

### 저장하는 값(`Match.BuildRecord`)

| 값 | 정의 |
|---|---|
| 순위 | 경기 중 탈락한 순서에 따른 순위. 승자는 1 |
| 처치 | 그 경기의 처치 수 |
| 피해 | 다른 플레이어에게서 실제로 깎은 Shield + Health의 합. 넘치는 피해(오버킬)와 Zone 피해, 자기 자신에게 준 피해는 넣지 않는다 |
| 생존 시간 | 경기 시작부터 탈락 Tick까지(ms). 끝까지 산 사람은 종료 Tick까지 |
| 승자 | 순위 1인 플레이어. 없으면 NULL |
| Anti-cheat(리뷰 수정 C7, v2 열) | 경기 시작부터 센다(`PlayerEntity`의 정수 카운터, 할당 없음). `shots` 방아쇠 수(투사체 포함), `pellets` Hitscan 광선 수, `hits` 플레이어를 맞힌 광선 수, `rewind_ticks` 되감은 Tick 합, `rewind_clamped` RTT 허용보다 오래된 ViewTick을 자른 횟수, `max_hit_distance_cm` 맞힌 광선의 가장 먼 거리(cm, 히트박스 앞면까지), `movement_anomalies` 모드 속도를 넘은 이동 수, `max_aim_turn` 두 실제 입력 사이 AimYaw 변화(180°로 감은 값)의 최댓값(0.1° 단위). 판정은 하지 않고 사후 확인용으로만 남긴다 |

- 참가자 전원을 넣는다. 경기 중 접속을 끊은 참가자는 떠나는 때 기록해 두었다가 함께 넣는다(이탈은 탈락, Phase 5 D10).
- 경기 중 합류한 관전자는 참가자가 아니므로 넣지 않는다.
- `DevRespawn` 샌드박스는 경기가 없으므로 기록하지 않는다.
- `player_stats` 갱신: `matches` +1, 순위 1이면 `wins` +1, 아니면 `deaths` +1, 나머지는 더한다.
- 같은 `DevPlayerId`가 한 기록에 두 번 이상 나오면(클라이언트 둘이 같은 id를 씀) 계정마다 순위가 가장 좋은 기록 하나만 저장한다. 0이 아닌 가장 작은 순위가 가장 좋고, 순위 0(미정)은 가장 나쁘며, 같으면 앞의 것이다. 나머지는 건너뛰어 `(match_id, account_id)` 키 충돌과 경기 유실을 피하고, 중복된 쪽이 이겼으면 승리가 남는다. 이때 `game_match.players`는 쓴 행 수이고 `winner_account_id`는 남긴 행과 맞는다. 끝 공백만 다른 id는 다른 계정이다(NO PAD).
- 승자가 경기 중에 떠났으면(마지막 둘이 같은 Tick 전에 모두 떠남) 결과 패킷의 `WinnerId`는 0이지만, 기록의 승자는 순위 1인 그 이탈자다.
- `kills`는 SMALLINT UNSIGNED 한도(65535)로 자른다.

## 실패 처리와 종료

- DB 재연결(Phase 10 D8): 시작할 때 DB에 연결하지 못해도 Writer는 계속 돈다(Warning 로그). 기록마다 스키마 준비부터 다시 시도하고, 실패하면 보통 재시도(`MaxAttempts`)를 거쳐 `Failed`가 된다. 그래서 서버를 먼저 켜고 DB를 나중에 띄워도 그 뒤의 경기는 저장된다. `Enabled=false`일 때만 기록을 버리고 `Discarded`로 센다. 서버는 어느 쪽이든 정상 동작한다. 디스크 보관은 하지 않는다. DB가 오래 없으면 기록마다 연결 시간 초과(5초)만큼 Writer가 늦어진다. 큐 16개로 버티고, 넘치면 `Dropped`다. Game Loop는 기다리지 않는다.
- 저장이 실패하면 일시적인(transient) 오류는 기록마다 최대 `MaxAttempts`번 시도한다(시도 사이 1초, 2초 대기). 그래도 실패하면 로그(판 번호·인원)와 `Failed`를 남긴다.
- transient가 아닌 `MySqlException`(제약·데이터 오류)은 몇 번을 해도 같으므로 재시도하지 않고 바로 `Failed`로 센다.
- 저장은 at-least-once다. COMMIT은 성공했는데 응답이 유실되면 재시도가 같은 경기를 두 번 저장할 수 있다.
- 종료 순서: Writer를 게임 서버보다 먼저 등록해서, Host가 역순으로 멈출 때 Game Loop가 먼저 멈추고 그 다음 Writer가 멈춘다. Writer는 큐를 닫고 남은 기록을 `ShutdownDrainSeconds`(1–60초) 동안 저장한다. 시간이 다 되면 진행 중인 저장을 취소한다. 취소된 저장과 큐에 남은 기록은 판 번호·인원과 함께 로그로 남기고 `Discarded`로 센다.
- Host `ShutdownTimeout`은 90초다(`Program.cs`, Drain 최대 60초 + 30초). Host 기본 30초는 모든 Hosted Service의 `StopAsync`가 함께 쓰므로 그대로 두면 Drain 제한보다 먼저 잘린다.
- Writer 작업은 예외를 밖으로 내보내지 않는다(실패한 `BackgroundService`는 Host를 멈춘다). 예상 밖 예외면 로그를 남기고 읽기를 멈추며, 남은 기록은 종료 때 `Discarded`로 센다.
- Game Loop는 모든 `MatchResult`를 보낸 뒤에 기록을 큐에 넣는다. 그때 예외가 나면 잡아서 `Match.MatchSinkFailures`로 세고(Stats 줄 `matchSinkFailures`) Tick을 계속한다.

## 카운터와 로그

| 카운터 | 뜻 |
|---|---|
| `Saved` | 저장에 성공한 경기 수(Writer) |
| `Failed` | 시도를 모두 쓰거나 재시도 불가 오류로 포기한 경기 수. DB가 없어서 저장하지 못한 기록도 여기에 센다(Writer) |
| `Discarded` | `Enabled=false`여서, 종료 시간 안에 못 저장해서(취소된 진행 중 저장 포함), 또는 Writer가 예외로 멈춰서 버린 기록 수(Writer) |
| `MatchSinkFailures` | 기록을 만들거나 큐에 넣다가 예외가 난 경기 수(`Match`, Stats 줄) |
| `Dropped` | 큐가 가득 차거나 닫혀서 버린 기록 수(`MatchHistoryQueue`) |

- 시작 로그: `Match history: connected, schema ready.` 또는 `database unavailable at start (...); each finished match will try again.`(Warning)
- Health 줄 `db saved/failed/discarded/dropped`(누적, `Server.md` "관측")와 Meter `projecth.db_records`(태그 `result`)가 같은 값을 낸다.
- 경기마다: `saved match {MatchId} (round, players)`
- 종료할 때 한 줄: `saved=… failed=… discarded=… droppedQueueFull=…`

## 조회 경로 (Phase 11 D8)

설계 근거: `Docs/specs/2026-10-01-phase11-game-ui-design.md` D8, 5절. Client가 전적을 요청하면(`Networking.md` "전적 조회") `StatsQueryService`(Hosted Service, Game Loop 밖)가 읽는다.

- `StatsQueryService`는 자기 `MatchStore`(Writer와 같은 연결 문자열)로 `GetStatsAsync`를 부르고, 기록이 있으면 `GetHistoryAsync(10)`을 부른다. 매 호출이 풀링된 연결을 열고 돌려준다.
- 조회마다 3초 제한이다. 취소 토큰과 `WaitAsync`를 함께 건다. MySqlConnector는 서버 인사말(greeting)을 기다리는 동안 취소를 따르지 않아서(측정: `Connection Timeout=4`에서 4.05초) 토큰만으로는 제한을 지킬 수 없다. 제한을 넘겨 남겨진 조회는 Connection Timeout(5초)까지 뒤에서 남을 수 있고, 그 수는 서비스가 한 번에 하나만 처리하는 것과 연결 시간 제한으로 묶인다.
- 결과는 `StatsResponse`로 바꾼다. DB는 64비트 합을 갖고 있어서 u32로 자르고, 누적 생존 시간은 초 단위다. 최근 경기는 최대 10개, 최신순이다.
- 기록이 없으면 `NoRecord`, 실패·시간 초과·Persistence 꺼짐은 `Unavailable`이다. 예외는 밖으로 나가지 않는다.
- 두 쿼리는 Transaction 없이 따로 읽는다. 그 사이에 경기가 저장되면 요약과 행이 한 경기 어긋날 수 있다(다음에 열면 맞는다).

## 조회 예시

서버는 Client 요청에 이 두 쿼리로 답한다(위 절). SQL로 직접 볼 때:

```sql
-- 내 통계
SELECT s.matches, s.wins, s.kills, s.deaths, s.damage, s.survival_ms
FROM player_stats s JOIN account a ON a.id = s.account_id
WHERE a.dev_player_id = 'dev-1';

-- 내 최근 경기 10개
SELECT m.id, m.round_no, m.ended_at, m.players, p.placement, p.kills, p.damage, p.survival_ms
FROM match_player p
JOIN account a ON a.id = p.account_id
JOIN game_match m ON m.id = p.match_id
WHERE a.dev_player_id = 'dev-1'
ORDER BY p.match_id DESC
LIMIT 10;
```

## 테스트

- DB 없는 테스트(기록 내용, 큐, 설정, DB 없는 Writer)는 항상 돈다.
- MySQL 테스트(`MySqlTests`, `[MySqlFact]` 9개)는 환경 변수 `PROJECTH_TEST_MYSQL`에 연결 문자열이 있을 때만 돈다. 없으면 건너뛰므로 DB 없이 `dotnet test`는 통과하고 건너뛴 테스트가 9개 보인다.

```bash
docker compose up -d
export PROJECTH_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev"
dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Persistence"
```

- 내용: 저장하면 통계가 누적되고 전적이 최신순으로 나온다 / 실패하는 저장은 아무것도 남기지 않는다(롤백) / 같은 DevPlayerId는 한 번만(가장 좋은 순위로) 저장되고 경기는 남는다 / Hosted Writer가 큐의 기록을 저장한다 / 종료 시간에 걸린 진행 중 저장은 `Discarded`로 센다 / DB 없이 시작한 Writer는 나중에 DB가 생기면 저장한다(`TheWriter_StartedWithoutTheDatabase_SavesOnceItAppears`. Writer가 쓰는 포트에 아무도 없을 때 기록 하나를 넣어 `Failed`로 세게 한 뒤, 그 포트에서 테스트 MySQL로 잇는 TCP 중계기를 열고 다음 기록이 저장되는지 본다).
- Phase 11: `AStatsQuery_AfterSavedMatches_AnswersOk_WithTotalsAndTheNewestMatchFirst`(저장한 경기를 `StatsQueryService`가 `Ok`와 통계·최근 경기로 답한다), `AStatsQuery_ForAnIdWithNoMatch_AnswersNoRecord`. DB 없는 `StatsQueryTests`(응답 변환, Persistence 꺼짐, 멈춘 DB에 대한 시간 제한 포함)는 항상 돈다.
- 테스트도 개발 DB(`projecth`)에 그대로 쓴다. 별도 테스트 스키마는 없고, 이름 충돌을 피하려고 무작위 id를 쓴다. 지우려면 위 "개발 DB 초기화"를 쓴다.

## 범위 밖

- 실제 인증·비밀번호, 표시 이름 바꾸기
- 실패한 기록의 디스크 보관
- 마이그레이션 도구, 읽기 API 서버
