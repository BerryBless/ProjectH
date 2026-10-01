# Bots

`Server/src/ProjectH.Bots`: 테스트·부하용 Headless 봇 Client(.NET 10 콘솔). 실제 UDP로 서버에 접속하고 Unity Client와 같은 프로토콜을 쓴다. 서버 프로젝트를 참조하지 않고(Shared 프로토콜·맵 데이터만 쓴다), Client 코드도 링크하지 않는다. 서버는 봇을 구분하지 않는다(DevPlayerId가 `bot-001` 같은 이름일 뿐이고 같은 검증을 거친다). 설계 근거: `Docs/specs/2026-10-01-phase7-bots-design.md`. 부하 측정은 `LoadTest.md`.

## 실행

```bash
dotnet run --project Server/src/ProjectH.Bots -c Release -- --port 7777 --count 8
dotnet Server/src/ProjectH.Bots/bin/Release/net10.0/ProjectH.Bots.dll --port 7790 --count 50 --duration 120 --connect-interval-ms 50
```

서버는 따로 띄운다(`Server.md`). 경기는 2명 이상이면 시작한다. 모양은 `--이름 값` 쌍이고, 모르는 이름이나 잘못된 숫자는 오류로 끝난다.

| 옵션 | 기본값 | 의미 |
|---|---|---|
| `--host` | 127.0.0.1 | 서버 주소 |
| `--port` | 7777 | 1–65535 |
| `--count` | 1 | 봇 수, 1–50 (한 경기 상한 `MaxSnapshotEntities`) |
| `--seed` | 1 | 난수 시드. 봇 i는 seed + i |
| `--duration` | 0 | 실행 시간(초). 0 = Ctrl+C까지 |
| `--connect-interval-ms` | 100 | 봇 접속 사이 간격 0–10000 (접속 폭주 방지) |
| `--name-prefix` | bot | 이름 접두어 1–20자 |
| `--stats-interval` | 10 | 로그 간격(초), 1 이상 |

`--stats-interval` 초마다 한 줄을 남긴다: 연결된 수, 살아 있는 수(`alive`), 경기 상태, 보낸 입력/s, 받은 패킷/s, 받은 바이트/s, 봇 루프 p95 ms. 끊긴 봇은 다시 접속하지 않는다(재접속은 Phase 10).

## 구조

| 파일 | 책임 |
|---|---|
| `BotOptions` | 명령줄 파싱과 검증 |
| `BotConnection` | 봇 하나의 LiteNetLib 수동 모드 연결. 패킷을 읽어 `BotView`를 갱신하고 입력 패킷을 보낸다 |
| `BotView` | 봇이 아는 세계: 내 상태, 다른 플레이어(최대 50), 월드 아이템(최대 256), 경기·Zone 상태, 무기·아이템 Catalog |
| `BotBrain` | 판단과 그 결과의 입력 하나를 만든다. 시각과 난수는 인자로 받는다 |
| `BotSteering` | 직선 조향, 막힘 탈출 |
| `BotAim`, `LineOfSight` | 조준 각과 오차, 시선 검사 |
| `BotRunner` | 고정 Tick 루프, 접속 간격, 통계 로그 |
| `Program` | 진입점, Ctrl+C 처리 |

- 봇 N명을 스레드 하나가 돈다. LiteNetLib 수동 모드(`StartInManualMode`, `ManualUpdate`)라 봇마다 수신 스레드가 없고, 상태를 루프 스레드 하나가 소유하므로 Lock이 없다. 루프는 SimHz(Join 응답 값, 기본 30 Hz)로 돌고, 밀리면 따라잡기는 최대 3 Tick이다.
- 컬렉션은 모두 상한이 있다(플레이어 50, 아이템 256).
- 판단·입력 Tick은 할당하지 않는다(테스트로 고정).
- 입력은 Unity Client와 같다: Tick마다 Seq를 1 올리고 최근 입력 3개를 한 패킷에 담는다. 눌림 버튼(점프, E, R, 회복)은 그 Tick에만 싣는다.

## 판단 규칙

10 Hz로 다시 판단하고 입력은 매 Tick 보낸다. 위에서부터 처음 맞는 것을 한다.

1. 죽었으면 아무 입력도 보내지 않는다.
2. 경기 전·결과 화면이면 제자리에 선다. `MatchState`를 받은 적이 없는 `DevRespawn` 서버는 항상 경기 중으로 본다.
3. Zone: 현재 원 밖이거나, 다음 원이 정해졌는데 그 밖이면 다음 원 중심으로 간다.
4. 교전: 보이는(시선 검사) 살아 있는 가장 가까운 적이 사거리 안(무기 사거리, 최대 60 m)에 있고 탄이 있으면 조준하고 쏘며 좌우로 움직인다. 탄창이 비면 장전하거나 탄 있는 칸으로 바꾼다.
5. 회복: 근처 15 m에 적이 없으면 Health 60 미만일 때 Medkit, Shield 25 미만일 때 Shield Cell을 쓴다.
6. Loot: 쓸모 있는 아이템(빈 무기 칸이 있을 때의 무기, 가진 무기의 탄약, 최대 미만인 회복) 중 가장 가까운 것으로 간다. 수평 1.5 m, 높이 차 1.8 m 안(`BotBrain.PickupReach`·`PickupHeight`, 서버 허용은 2 m)이면 E를 누른다.
7. 배회: Zone 다음 원(없으면 맵 중앙 60 m) 안의 무작위 지점으로 간다.

수치(코드의 상수, 옵션이 아니다):

| 항목 | 값 |
|---|---|
| 이동 | 목표 방향으로 걷고 10 m 넘게 남으면 달린다 |
| 막힘 탈출 | 0.5초 동안 0.3 m도 못 가면 점프 1회, 1초째도 막혔으면 좌우 90° 중 하나로 1–2초 우회, 8초 동안 목표에 가까워지지 않으면 그 목표(아이템, 지점)를 30초 버린다 |
| 조준 | 눈(발 + 1.6 m)에서 적 가슴(발 + 1.2 m)으로. 적 위치는 마지막 Snapshot 값. 오차는 Yaw·Pitch에 ±(1° + 0.04° × 거리 m) 균등 분포를 0.3초마다 다시 뽑는다. ViewTick은 마지막 Snapshot의 서버 Tick |
| 시선 검사 | 눈에서 가슴까지 0.5 m 간격 점. 점이 박스 안이거나 지형 아래면 막힘. 서버 `HitScan`은 쓰지 않는다. Fire를 45 Tick(`BotBrain.FireTicksWithoutHit`) 누르는 동안 `HitConfirmed`가 한 번도 없으면 그 적을 5초 동안 무시(자동 무기는 약 1.5초, 반자동은 한 Tick 걸러 눌러서 더 길다) |
| 교전 사거리 | 무기 사거리, 최대 60 m |
| 회복 | Health < 60 → Medkit, Shield < 25 → Shield Cell, 근처 15 m에 적 없을 때만 |
| 줍기 | 수평 1.5 m, 높이 차 1.8 m 안에서 E (서버 허용 2 m) |

## 봇이 쓰는 정보

Client가 받는 정보만 쓴다. 서버 내부 상태는 보지 않는다.

- Snapshot: 모든 플레이어 위치, 생존, 내 Health·Shield·탄창
- 이벤트: 아이템, 인벤토리, 경기 상태, Zone, 사망
- Shared 맵 데이터: `GameMap` 박스·지형

## 한계

지금 넣지 않은 것: 길찾기(NavMesh, A*: 건물 안 Loot를 놓칠 때가 있다), 봇 난이도 단계, 팀 AI, 서버 안 봇, 50명 초과(Phase 8), 재접속(Phase 10), Unity의 봇 전용 표시(봇은 일반 원격 플레이어로 보인다). 조준 오차 등 수치는 옵션이 아니라 코드 상수다. 행동은 단조롭다. 목적은 경기·부하 테스트다.

## 테스트

`Server/tests/ProjectH.Server.Tests/Bots/` (`ProjectH.Server.Tests`가 `ProjectH.Bots`를 참조).

- `BotBrainTests`: 규칙별 단위 테스트
  - 접속 전·Snapshot 전·사망이면 입력 없음
  - 경기 밖이면 정지, `DevRespawn`(MatchState 없음)은 경기 중으로 본다
  - Zone: 다음 원 밖이면 중심으로, 안이면 목표 아님
  - 교전: 보이는 적은 조준·발사. 벽·언덕 뒤나 사거리 밖은 대상이 아니다. 빈 탄창은 장전, 탄이 없으면 무기 교체, 모두 없으면 싸우지 않음. 반자동은 격발 Tick을 건너뛴다. 맞히지 못하면 대상을 잠시 무시하고, 맞히면 유지
  - 회복: 체력·실드 기준, 가까운 적이 있으면 미룸
  - Loot: 쓸모 있는 아이템으로 가서 줍기, 서버가 계속 거절하는 아이템은 제외, 가져간 아이템은 목표 해제, `IsUseful`은 인벤토리를 따른다
  - 배회 지점은 다음 원 안
  - `ATick_AllocatesNothing`: 판단 Tick 무할당
- `BotPartsTests`: `LineOfSight`(트인 곳, 벽, 언덕), `BotAim`(오차 0이면 서버 `CombatRules`와 같은 방향, 오차는 범위 안), `BotSteering`(직진, 점프 → 우회 → 포기, 진전이 있으면 포기를 미룸), `BotOptions`(파싱·범위 검증)
- `BotIntegrationTests`: 같은 프로세스에서 서버 `GameLoop`를 띄우고 봇이 실제 UDP로 접속
  - `ABot_FindsAndPicksUpAWeapon`: `DevRespawn`, 빈손, 무기만 나오는 Loot에서 20초 안에 무기를 줍는다
  - `TwoArmedBots_FightUntilOneDies`: 전투 장비 봇 2명이 30초 안에 한 명을 처치한다
  - `FourBots_PlayAWholeMatch_AndTheNextRoundStarts`: 경기 서버에서 봇 4명이 경기를 끝까지 하고 `MatchResult`를 받고 다음 판 카운트다운이 시작된다
