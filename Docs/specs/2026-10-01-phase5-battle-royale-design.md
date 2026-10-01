# Phase 5 Battle Royale — 설계 Spec

## Context

Phase 4까지 만든 것은 다음과 같다.

- 접속, 이동, 아레나 충돌, 서버가 판정하는 Hitscan 전투
- 인벤토리, Loot, 줍기, 버리기, 회복

지금 규칙은 계속 이어지는 테스트용 난투다. 죽으면 3초 뒤 부활하고 Loot는 30초마다 다시 생긴다. 시작, 끝, 승자가 없다.

원래 요청서의 Phase 5는 다음을 구현한다.

```text
Match State / Player Count / Zone / Zone Damage / Permanent Death / Winner / Match Result
이 단계에서 기본적인 Battle Royale가 플레이 가능해야 한다.
```

관련 조항은 다음과 같다.

- §26 Safe Zone: Server가 관리한다. Waiting과 Shrinking을 번갈아 반복하다가 Final이 된다. 데이터는 Center, CurrentRadius, TargetRadius, StartTime, EndTime, Phase다.
- §27 Zone Damage: Zone 밖이면 Server가 피해를 준다. Phase가 진행될수록 피해가 커지고, 수치는 데이터화한다.
- §28 Zone 생성: 다음 Zone은 현재 Zone 안에 만든다. 맵 밖으로 나가지 않게 하고, 초기에는 단순 원형으로 한다.
- §29 Match State Machine: WaitingForPlayers, Warmup, Starting, Playing, FinalPhase, Finished, Closing. 각 State의 역할을 명확히 한다.
- §30 Match 종료: Alive ≤ 1이면 종료한다. Server가 Winner를 결정하고 결과를 Client에 보낸다.

**성공 기준:**

- 두 명 이상이 접속하면 카운트다운 뒤 경기가 시작된다.
- 시작하면 모두가 빈손으로 Spawn 위치에 서고, 아레나에 Loot가 새로 생긴다.
- 경기 중에는 죽으면 부활하지 않고 관전자가 된다. Loot도 다시 생기지 않는다.
- Safe Zone이 단계별로 줄어든다. Zone 밖에 있으면 체력이 깎이고, 단계가 오를수록 더 많이 깎인다.
  - 모든 Client는 Zone 원과 남은 시간을 똑같이 본다.
- 살아 있는 사람이 1명 이하가 되면 서버가 승자를 정하고 결과를 보낸다.
  - 결과: 승자, 내 순위, 처치 수
- 결과 화면이 끝나면 다음 판이 자동으로 준비된다.
- 상태 전환, Zone, 피해, 승자 판정은 Unity 없이 `dotnet test`로 검증한다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 진행 방식 | 추천안대로 진행하고 추천 이유는 이 문서에 남긴다. Phase마다 푸시한다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **State Machine**은 다음 순서로 돈다. 각 전환 조건은 서버 Tick으로 판정한다.<ul><li>`WaitingForPlayers`: 인원 부족.</li><li>`Starting`: `MinPlayers`(기본 2) 이상이면 10초 카운트다운. 도중에 인원이 모자라면 `WaitingForPlayers`로 돌아간다.</li><li>`Playing`</li><li>`FinalPhase`: 마지막 Zone 단계.</li><li>`Finished`: 결과 표시 10초.</li><li>`Closing`: 판 정리. 한 Tick 만에 끝나고 `WaitingForPlayers` 또는 `Starting`으로 간다.</li></ul> | 요청서 §29의 State 이름을 쓰되, 각 State가 하는 일이 겹치지 않게 했다. `Warmup`은 따로 두지 않았다. 대기 중 자유 행동이 곧 Warmup이다(D2). 전환을 Tick으로 판정하면 테스트에서 시간을 결정적으로 다룰 수 있다. | State가 하나 적다. 필요해지면 `Starting` 앞에 끼워 넣는다. |
| D2 | **경기 전(대기·카운트다운)**: 자유롭게 움직이고 사격할 수 있지만 **피해는 없다**. 월드에 Loot가 없고, 죽음도 없다. | 기다리는 동안 조작을 익힐 수 있다. 경기 전 결과가 경기에 남지 않는다. | 대기 중 연습이 단조롭다. |
| D3 | **경기 시작 순간**(`Starting` → `Playing`)에 다음을 한 Tick 안에 처리한다.<ul><li>모든 접속자를 Spawn 위치로 옮긴다.</li><li>인벤토리를 비우고, Health 100, Shield 0으로 만든다.</li><li>월드 아이템을 전부 지운다.</li><li>Loot를 새로 굴린다(시드 = `LootSeed` + 판 번호).</li><li>생존 목록을 확정한다.</li></ul>이동은 `PlayerRespawned`(전원, Seq 유지)로 알린다. 다른 Client는 그 플레이어의 보간 기록을 정리해 미끄러지지 않고 바로 옮긴다(Spawn 5 m 안의 최근 샘플만 남긴다). | 모두 같은 조건에서 시작해야 공정하다. 판마다 시드를 바꾸면 Loot 배치가 달라진다. 한 Tick 안에 끝내면 중간 상태가 Client에 보이지 않는다. | 없음. |
| D4 | **경기 중 사망은 영구적이다.** 부활하지 않고 관전자가 된다. 경기 중 Loot 재생성은 끄고, 경기 전과 결과 화면에서는 켜지 않는다. Phase 3·4의 테스트용 부활과 재생성은 `ServerOptions`의 플래그(`DevRespawn`)로 남기고 기본값은 끈다. 이 플래그는 Phase 3·4 테스트 아레나 전체다: 상태 기계가 돌지 않고, 피해 항상, 3초 부활, Loot는 서버 시작 때부터 있고 `LootRespawnSeconds`마다 재생성되며, 경기 패킷을 보내지 않는다(Client는 `MatchState`를 받은 적이 없으면 Phase 4처럼 동작한다). | 요청서의 Permanent Death다. 기존 테스트는 플래그를 켜서 그대로 동작하게 해, 전투·Loot 테스트를 다시 쓰지 않아도 된다. | 없음. |
| D5 | **관전:** 죽은 플레이어의 카메라는 자기를 죽인 사람을 따라간다. 좌클릭으로 살아 있는 다음 사람으로 넘어간다. 따라가던 사람이 죽으면 다음 사람으로 자동으로 넘어간다. 관전 대상은 Client가 원격 보간 위치로 그린다. 서버는 관여하지 않는다. 경기 중 합류자(D10)와 Zone 사망(처치자 없음)은 첫 생존자부터 따라간다. | 관전은 표시 기능이라 Client가 맡으면 충분하다. 원격 플레이어 보간(Phase 0)을 그대로 재사용한다. | 관전 대상의 조준 방향(Pitch)이 보이지 않는다(Phase 3 D15). |
| D6 | **Zone은 원형이고 단계는 `zones.json` 데이터로 정한다.** 단계마다 대기 초, 축소 초, 목표 반지름, 초당 피해를 둔다. 첫 원은 아레나 전체를 덮는다(중심 (0,0), 반지름 30). 다음 원의 중심은 시드 난수로 정한다. 새 원은 현재 원 안에 완전히 들어가야 하고(중심 거리 ≤ 현재 반지름 − 목표 반지름), 중심은 아레나 안쪽 경계 ±19.5 m를 넘지 않는다(경계 안의 중심이 나올 때까지 16번 다시 뽑고, 그래도 안 되면 이전 중심을 쓴다). | 요청서 §26–28 그대로다. 수치를 데이터로 두면 아레나 크기에 맞춰 조정하기 쉽다. 시드 난수라 테스트에서 재현된다. | 원형이라 단순하다. 지형 가중치는 맵 단계에서 추가한다. |
| D7 | **기본 Zone 단계**는 40 × 40 m 테스트 아레나용이다.<br>(대기, 축소, 목표 반지름, 초당 피해)<ul><li>1단계: 20 s, 15 s, 20 m, 1</li><li>2단계: 15 s, 12 s, 12 m, 2</li><li>3단계: 12 s, 10 s, 6 m, 5</li><li>4단계: 10 s, 8 s, 2 m, 10</li><li>마지막 단계: 8 s, 8 s, 0 m, 20. 이 단계부터 `FinalPhase`다.</li></ul>전체 약 2분 반이다. | 작은 아레나에서 한 판을 몇 분 안에 끝내야 반복 테스트가 가능하다. 반지름 0까지 줄이고 마지막 단계의 피해가 0보다 크면 경기가 반드시 끝난다. `zones.json` 검증이 이 둘을 요구한다. | 긴 경기를 시험하려면 데이터만 고치면 된다. |
| D8 | **Zone 피해는 1초마다 Health에 직접 준다(Shield 무시).** 판정은 수평 거리 > 현재 반지름이고, 반지름 0인 원은 안이 없다(누구나 밖). 마지막 원의 정확한 중심에 서도 피해를 피할 수 없다. 서버 `SafeZone.IsOutside`와 Client `ZoneMath.IsOutside`가 같은 규칙이다. 축소 중에는 매 Tick 선형 보간한 원을 쓴다. 이 피해로 죽으면 처치자는 없다(KillerId = 0). | Shield가 Zone 피해를 막으면 Zone이 플레이어를 몰아내는 역할을 못 한다. 1초 간격이면 피해 이벤트가 과도하게 많지 않다. 매 Tick 보간하면 Client가 그리는 원과 판정이 일치한다. | 장르의 흔한 규칙이다. 밸런스 단계에서 조정한다. |
| D9 | **종료와 승자:** `Playing`·`FinalPhase`에서 생존자 ≤ 1이면 `Finished`. 생존자가 1명이면 그가 승자다. 같은 Tick에 모두 죽으면 그 Tick에 마지막으로 처리된 피해자를 1위로 한다. Zone 피해 순서는 플레이어 목록 순서로 고정한다. 순위는 사망 순서의 역순이다. | 요청서 §30 그대로다. 동시 사망에도 규칙이 결정적이어야 테스트와 재현이 가능하다. | 동시 사망 순위가 목록 순서에 의존한다. 공정성 이슈는 매우 드물다. |
| D10 | **경기 중 이탈은 탈락으로 처리한다.** 인벤토리를 떨어뜨리고 생존자에서 뺀다. 이탈자는 결과를 받지 않는다. 경기 중 새로 들어온 사람은 관전자로 합류해 다음 판을 기다린다. | 이탈해서 사망 페널티를 피하는 걸 막는다. 들고 있던 Loot는 남은 사람에게 돌아가야 공정하다(Phase 4 Drop 규칙 재사용). | 네트워크가 끊겨도 탈락이다. 재접속은 Persistence·Hardening 단계에서 다룬다. |
| D11 | **Protocol v5**에 Reliable 이벤트를 추가한다. Snapshot은 그대로다.<ul><li>`MatchState`: 상태, 상태 종료 Tick, 생존자 수, 전체 인원, 판 번호, `MinPlayers`(HUD의 "Waiting for players 1/2"에 필요하다). 변경될 때와 Join 때 보낸다.</li><li>`ZoneState`: 단계, 현재 중심·반지름, 목표 중심·반지름, 축소 시작·끝 Tick, 초당 피해. 단계가 바뀔 때와 Join 때 보낸다.</li><li>`MatchResult`: 승자 EntityId, 내 순위, 내 처치 수, 전체 인원. 본인에게만 보낸다.</li><li>`PlayerDied`에 순위(남은 생존자 수 + 1)를 붙인다.</li></ul> | Zone 원은 시작·끝 값과 Tick만 알면 Client가 서버와 똑같이 보간할 수 있다. 그러면 매 Snapshot에 넣을 필요가 없다(Snapshot 여유가 33 B뿐). 이벤트가 Reliable이라 유실로 원이 어긋나지 않는다. | 없음. |
| D12 | **처치 수**는 서버가 플레이어별로 센다(`Kills`). 판이 새로 시작하면 초기화한다. 자기 자신이나 Zone에 의한 죽음은 세지 않는다. 죽어 있는 동안의 좌클릭은 관전 대상을 넘길 뿐 발사하지 않고, 버튼을 뗄 때까지 발사로 치지 않는다(죽을 때 눌려 있던 발사 버튼이 다음 판 시작에 발사되지 않게 한다). | 결과 화면의 기본 정보다. DB 저장은 Persistence 단계에서 한다. | 없음. |
| D13 | **판 재시작:** `Finished` 10초 뒤 `Closing`에서 다음을 처리한다.<ul><li>모든 접속자를 살려 Spawn 위치에 두고 인벤토리를 비운다.</li><li>월드 아이템을 전부 지운다.</li><li>Zone을 초기화하고 판 번호를 올린다.</li><li>인원에 따라 `WaitingForPlayers` 또는 `Starting`으로 간다.</li></ul> | 서버를 다시 켜지 않고 여러 판을 테스트할 수 있다. 한 서버 = 여러 판이라는 이후 Match Flow 단계와도 맞는다. | 없음. |
| D14 | **Client 표시:**<ul><li>**Zone:** 지면의 원(LineRenderer, 원 둘레 64점)과 반투명 원통 벽. 원통은 공유 Material(내장 `Sprites/Default`. URP Lit 투명 변형은 빌드에서 빠져 벽이 불투명해질 수 있다), 단위 원통 Mesh의 Scale만 바꾼다. 다음 목표 원은 점선 색으로 표시한다.</li><li>**HUD 상단:** 상태 문구("Waiting for players 1/2", "Starting in 7", "Alive 3/5"), Zone 안내("Zone shrinking in 12s", "Zone closing").</li><li>**Zone 밖:** 화면 가장자리가 붉게 된다.</li><li>**결과:** 중앙에 "#1 VICTORY" 또는 "ELIMINATED #3 — 2 kills".</li><li>**관전 중:** "Spectating Player <EntityId>" 표시. 이름은 Client에 전달되지 않는다(이름 전송은 Persistence 단계).</li></ul>모든 문자열은 값이 바뀔 때만 만든다. | 모델과 이펙트 없이도 Zone 위치와 경기 흐름을 알아볼 수 있다. LineRenderer와 Scale 조정은 매 프레임 할당이 없다. 영어 문구는 Phase 4 판정(Legacy 내장 폰트의 한글 미검증)과 같은 이유다. | 겉모습이 단순하다. |
| D15 | **지금 넣지 않는 것:** 비행기·낙하산 투입, 팀·분대, 기절(Knockdown), 킬로그 UI, 관전자 채팅, 재접속, 전적 저장(Phase 9), 지형 가중 Zone, Zone 이동 경고 음향. | 원래 요청서의 뒤 단계(Map, Persistence, Hardening) 범위이거나, 기본 BR 규칙 검증에 필요 없다. | 없음. |

## 1. 데이터

`zones.json` (서버, 출력 폴더로 복사, 시작 시 검증):

```json
{
  "initialCenter": [0, 0],
  "initialRadius": 30,
  "arenaHalfSize": 19.5,
  "phases": [
    {"waitSeconds":20,"shrinkSeconds":15,"targetRadius":20,"damagePerSecond":1},
    {"waitSeconds":15,"shrinkSeconds":12,"targetRadius":12,"damagePerSecond":2},
    {"waitSeconds":12,"shrinkSeconds":10,"targetRadius":6,"damagePerSecond":5},
    {"waitSeconds":10,"shrinkSeconds":8,"targetRadius":2,"damagePerSecond":10},
    {"waitSeconds":8,"shrinkSeconds":8,"targetRadius":0,"damagePerSecond":20}
  ]
}
```

검증 항목: 단계는 1개 이상 16개 이하, 목표 반지름은 계속 줄어들고 0 이상, 시간은 0보다 크고 유한, 피해는 0 이상, 초기 반지름은 첫 목표 반지름 이상. **마지막 단계는 목표 반지름 0, 피해 > 0이어야 한다**(싸우지 않으면 끝나지 않는 경기를 막는다).

`ServerOptions`에 다음을 추가하고 검증한다.

- `MinPlayers` (2, 범위 2–MaxPlayers)
- `StartCountdownSeconds` (10)
- `ResultSeconds` (10)
- `DevRespawn` (false)
- `ZoneSeed`

Phase 4의 `LootRespawnSeconds`는 `DevRespawn`이 켜져 있을 때만 쓴다.

## 2. Server

- **`MatchFlow`** (새로, Game Loop 소유): State, 상태 종료 Tick, 판 번호, 생존 목록, 사망 순서.
  - Tick 시작에 전환을 판정한다.
  - `Match.Tick`의 부활·재생성·피해 허용 여부를 결정한다.
- **`SafeZone`** (새로, 순수 계산):
  - 단계 데이터와 시드 난수로 원을 만든다.
  - `Sample(tick)`로 (중심, 반지름)을 돌려준다.
  - `IsOutside(position, tick)`로 Zone 밖인지 판정한다.
  - 할당이 없다.
- **`Match.Tick` 순서:**
  1. `MatchFlow` 전환 판정
  2. Zone 단계 진행과 피해(`Playing`·`FinalPhase`, 1초마다)
  3. 기존 입력 처리
  4. 사망 처리와 순위 기록
  5. 종료 판정
- **피해 차단:** 경기 전 상태에서는 `ApplyDamage`가 호출되지 않는다. 발사, 궤적, 명중 표시는 남기되 피해는 0으로 처리하고 `HitConfirmed`도 보내지 않는다.
- **이탈** (D10): `Leave`에서 경기 중이면 인벤토리를 떨어뜨리고 생존자에서 뺀다.
- **Lock은 여전히 없다.** 모든 상태는 Game Loop 스레드가 소유한다.

## 3. Protocol v5

| 패킷 | 방향·Delivery | 내용 |
|---|---|---|
| `MatchState` | S→C, Reliable, 전원(변경 시)·Join | `State byte`, `StateEndTick u32`(없으면 0), `Alive byte`, `Participants byte`, `Round u16`, `MinPlayers byte`(11 B) |
| `ZoneState` | S→C, Reliable, 전원(단계 변경 시)·Join | `Phase byte`, `FromCenter (float,float)`, `FromRadius float`, `ToCenter (float,float)`, `ToRadius float`, `ShrinkStartTick u32`, `ShrinkEndTick u32`, `DamagePerSecond u16` |
| `MatchResult` | S→C, Reliable, 본인 | `WinnerId u16`(없으면 0), `Placement byte`, `Kills byte`, `Participants byte` |
| `PlayerDied` | 기존 | 뒤에 `Placement byte`를 추가한다(6 B) |

`ProtocolVersion = 5`. 모든 패킷은 1200 B 이하이고, 이를 테스트로 고정한다.

## 4. Client

| 영역 | 변경 |
|---|---|
| `NetClient` | 새 패킷 이벤트를 추가한다. |
| `ZoneView` (새로) | LineRenderer 원 2개(현재, 목표)와 원통 벽 1개(`Sprites/Default` Material). 서버 Tick 기준으로 보간한다(`ServerClock`). |
| `ZoneMath` (새로, 순수 계산) | 서버 `SafeZone.Sample`과 같은 보간 공식을 쓴다. EditMode 테스트로 서버 값과 일치하는지 확인한다. |
| `MatchHud` (새로) | 상태, 생존자 수, Zone 안내, Zone 밖 붉은 테두리, 결과 화면, 관전 표시. 문자열은 값이 바뀔 때만 만든다. |
| `SpectatorCamera` (새로) | 원격 플레이어 보간 위치를 따라가고, 좌클릭으로 다음 대상으로 바꾼다. 로컬 플레이어가 죽어 있을 때만 켠다. |
| `GameClient` | 연결하고 해제한다. 경기가 새로 시작되면 로컬 예측을 부활 처리와 같은 방식으로 다시 맞춘다(Seq 유지). |

## 5. 하네스

변경 없음. Zone 계산은 서버와 Client에 각각 둔다. Client의 `ZoneMath`는 표시용이라 Shared 예외를 넓히지 않는다. 두 쪽 공식의 일치는 테스트로 고정한다.

## 6. 테스트

**Server (xUnit):**

- **데이터:** `zones.json` 로드와 검증(정상, 반지름 증가, 음수 시간, 빈 목록). 실제 파일이 이 Spec의 값과 같은지 확인한다.
- **SafeZone:**
  - 시드가 같으면 결과가 같다.
  - 다음 원이 현재 원 안에 완전히 들어간다(1000개 시드).
  - 중심이 아레나 경계 안에 있다.
  - 축소 중에는 선형 보간한다.
  - 마지막 단계는 반지름 0이다.
  - `Sample`은 할당이 없다.
- **MatchFlow:**
  - 인원 부족이면 Waiting에 머문다.
  - 2명이 되면 Starting이 되고 10초 뒤 Playing이 된다.
  - 카운트다운 중 인원이 빠지면 Waiting으로 돌아간다.
  - 시작 순간 모두 Spawn 위치에 있고, 빈 인벤토리, Loot 재생성, Kills 0이다.
- **경기 전:** 피해 없음, Loot 없음.
- **경기 중:**
  - 사망하면 부활하지 않는다. Loot가 재생성되지 않는다.
  - 순위는 사망 역순이다. 처치 수를 센다(Zone 사망은 제외).
  - Zone 피해: 밖에 있으면 1초마다 Health가 준다(Shield 유지). 안에 있으면 피해가 없다. 단계별 피해량이 맞다.
- **종료:** 1명이 남으면 Finished가 되고 승자와 결과가 맞다. 동시 사망 규칙. 10초 뒤 Closing을 거쳐 다음 판이 준비된다.
- **이탈** (D10)과 **경기 중 합류자 관전.**
- **DevRespawn 플래그:** 켜면 기존 Phase 3·4 테스트가 그대로 통과한다.
- **Protocol:** 새 패킷 왕복, 잘린 패킷, 크기 ≤ 1200 B, 버전 5.
- **통합** (HeadlessClient 2개, 짧은 테스트용 Zone·카운트다운 설정):
  1. Join 후 카운트다운을 거쳐 Playing이 된다.
  2. A가 B를 처치한다.
  3. 두 Client가 Finished를 받고, 각자 승자·순위·처치 수가 맞는 `MatchResult`를 받는다.
  4. Closing을 거쳐 다음 판 Starting이 된다.

**Client EditMode:** `ZoneMath`가 서버 공식과 같은지(같은 입력 → 같은 값), MatchHud 문자열 캐시, 관전 대상 순환 규칙.

**Unity 수동 확인 (사용자):** 두 Client로 대기 → 카운트다운 → 경기 → Zone 축소와 피해 → 처치 → 관전 → 결과 → 다음 판.

## 7. 범위 밖

D15의 항목.
