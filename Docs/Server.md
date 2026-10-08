# Server

## 실행

```bash
dotnet run --project Server/src/ProjectH.Server
dotnet run --project Server/src/ProjectH.Server -- --Server:AirDrop=false   # Phase 12: 수송기 없이 땅에서 시작(비교용)
dotnet run --project Server/src/ProjectH.Server -- --Server:BuildInfiniteResources=true   # Phase 13: 건설 비용 0(부하 테스트)
dotnet run --project Server/src/ProjectH.Server -- --Server:ConnectBurstPerIp=200 --Server:MaxConnectionsPerIp=200   # 서버 리뷰 M2·리뷰 수정 A2: 한 PC에서 봇 4명 넘게 붙일 때
dotnet test Server/ProjectH.Server.slnx
```

MySQL에 경기 기록을 남기려면 먼저 `docker compose up -d`(개발용 컨테이너, `Database.md`)를 실행한다. DB가 없어도 서버는 정상 동작하고 기록만 남지 않는다. DB 설정은 `appsettings.json`의 `Persistence` 절(`Database.md`).

설정: `Server/src/ProjectH.Server/appsettings.json`의 `Server` 섹션. 명령줄로 덮어쓰기: `-- --Server:Port=7778`. 잘못된 값이면 시작 시 종료된다(`ServerOptions.Validate`, `GameLoop` 생성자에서 예외).

| 키 | 기본값 | 범위 / 의미 |
|---|---|---|
| Port | 7777 | 0–65535 (UDP) |
| MaxPlayers | 16 | 1–100 (Snapshot은 패킷당 90명 한도로 분할: 91명 이상이면 2패킷) |
| SimHz | 30 | 10–128 |
| SnapshotEveryTicks | 2 | 1–SimHz, SimHz의 약수 |
| InputBufferPerPlayer | 8 | 2–64 |
| MaxInputMessagesPerTick | 512 | ≥ 1, Tick당 소비하는 입력 메시지 수 |
| BadPacketDisconnectThreshold | 20 | ≥ 1 |
| DisconnectTimeoutMs | 5000 | ≥ 500 |
| StatsIntervalSeconds | 10 | ≥ 1 |
| LootSeed | 1 | ≥ 0. Loot 난수 시드. 경기마다 `LootSeed + 판 번호`로 굴린다(같으면 배치가 같다) |
| LootRespawnSeconds | 30 | 0–3600. 다 가져간 Spawn Point를 다시 채우는 시간, 0이면 끔. `DevRespawn`일 때만 쓴다 |
| MinPlayers | 2 | 2–MaxPlayers (1명이면 경기가 첫 Tick에 끝나 판이 무한 반복되므로 2 이상). 이 인원이 모이면 카운트다운 |
| StartCountdownSeconds | 10 | 1–300. 카운트다운(`Starting`) |
| ResultSeconds | 10 | 1–300. 결과 화면(`Finished`) |
| DevRespawn | false | true = Phase 3·4 테스트 아레나(경기 흐름 없음, 피해 항상, 3초 부활, Loot 처음부터·재생성, 경기 패킷 없음). 운영은 false |
| ZoneSeed | 1 | ≥ 0. Zone 중심 난수 시드. 경기마다 `ZoneSeed + 판 번호` |
| SpawnSeed | 1 | ≥ 0. 투입 지점 섞기 시드. 경기마다 `SpawnSeed + 판 번호`. `AirDrop`이면 수송기 경로의 시드이기도 하다 |
| AirDrop | true | Phase 12: 경기가 수송기에서 시작한다(`BattleRoyale.md` "공중 투입". Zone 시계는 경로가 끝나는 Tick에 시작). false면 Phase 6의 투입 지점에서 땅으로 시작한다(Phase 5–11 규칙 테스트와 Phase 11과의 부하 비교용). `DevRespawn`이 true면 이 값과 상관없이 땅에서 시작한다 |
| BuildInfiniteResources | false | Phase 13: true면 건설에 자원이 들지 않는다(부하 테스트용, `Building.md` "자원"). 운영은 false |
| TeamSize | 1 | 1–4. Phase 14: 팀 크기(1 Solo, 2 Duo, 4 Squad). 경기 시작 때 참가자를 입장 순서로 묶는다(언제나 2팀 이상). 기절·소생·Reboot은 2 이상에서만 생긴다(`Squad.md`) |
| ReconnectGraceSeconds | 10 | 0–60, 0 = 끔. 경기 중 끊긴 참가자가 캐릭터를 지키는 시간(`Networking.md` "끊기와 재접속") |
| JoinTimeoutSeconds | 5 | 1–60. 연결한 뒤 Join해야 하는 시간. 넘으면 `JoinTimeout`으로 끊는다 |
| ConnectBurstPerIp | 20 | 0 = 끔, 아니면 1–10000. 서버 리뷰 M2: 한 IP가 한 번에 할 수 있는 연결 요청 수(Token Bucket 크기). 넘으면 `ServerFull`로 거절하고 `rejects connectRate`로 센다(`Networking.md` "Validation"). 봇 여러 명을 한 PC에서 붙이는 부하 테스트는 200을 준다 |
| ConnectsPerIpPerSecond | 5 | 0 = 끔, 아니면 1–1000. 위 Bucket이 초당 채워지는 수 |
| MaxConnectionsPerIp | 4 | 0 = 끔, 아니면 1–10000. 리뷰 수정 A2(SEC-3): 한 IP가 동시에 가질 수 있는 연결 수. 넘으면 `ServerFull`로 거절하고 `rejects perIp`로 센다. 한 PC에서 봇 여러 명을 붙이는 부하 테스트는 200, QA 도구는 1000을 준다. PC방·CGNAT처럼 한 주소에 사람이 많으면 올린다 |
| AcceptsPerSecond | 20 | 1–10000. 리뷰 수정 A2: 모든 주소를 합친 초당 수락 수(Token Bucket, 한 번에 `AcceptBurst` = MaxPlayers개). 넘으면 `ServerFull`, `rejects accept`. Control 채널 크기가 이 값으로 정해진다("Queue") |
| FatalStallSeconds | 30 | 0 = 끔, 아니면 5–3600. 서버 리뷰 M8: Game Loop가 이 시간보다 오래 멈추면 새 연결을 막고 종료 코드 1로 끝낸다("예외 복구") |
| InputTimeoutSeconds | 10 | 0 = 끔, 아니면 2–300이고 `InputTimeoutSeconds × 1000 ≥ DisconnectTimeoutMs + 2000`(기본 5000이면 7 이상, 최솟값 500이면 3 이상). Join한 peer가 입력을 보내야 하는 간격. 넘으면 `InputTimeout`으로 끊는다. LiteNetLib Timeout보다 먼저 오면 네트워크 끊김이 서버 끊기로 보여 유예를 잃으므로 이 조건을 둔다 |

데이터 파일: `Server/src/ProjectH.Server/weapons.json`, `items.json`, `loot.json`, `zones.json`, `building.json`(Phase 13), `squad.json`(Phase 14), `map.json`(Phase 15, 출력 폴더로 복사). 시작 시 `GameData.LoadDirectory`가 일곱을 읽고 검증한다.
- `map.json`(`MapCatalog`, Phase 15): Ping 수명(Location·Item·Danger `pingSeconds` 8, Enemy `enemyPingSeconds` 4, 각 1–60초), 플레이어당·팀당 활성 Ping(`pingsPerPlayer` 3 ≤ `pingsPerTeam` 8 ≤ 8, `TeamMarkers` 상한), Enemy·Item 확인 거리(`enemyPingRange` 150, `itemPingRange` 60, 1–500 m), 수신 스레드 속도 제한(`pingsPerSecond` 2, `pingBurst` 4, 각 1–20, `maxMarkerPacketsPerSecond` 20, 그 둘 이상 100 이하). 틀리면 서버가 시작하지 않는다. 코드 안의 같은 값(`MapCatalog.DefaultJson`, 파일과 같음을 `MapCatalogTests`가 고정)은 파일 없이 만드는 테스트용 `GameData`만 쓴다. 규칙은 `Map.md` "지도 UI·Ping".
- `squad.json`(`SquadCatalog`, Phase 14): 기절 체력·출혈 시간, 소생 시간·거리·체력·피해 취소, 재투입 시간·거리, 카드 수명·소지 최대(1–3), 스테이션 대기, 재투입 장비(무기 id·탄은 `weapons.json`·`items.json`과 맞아야 한다). `friendlyFire`는 false만 된다. 틀리면 서버가 시작하지 않는다. 표는 `Squad.md`. 팀 크기는 설정 `Server:TeamSize`(1–4, 기본 1 Solo)다.
- `building.json`(`BuildingCatalog`, Phase 13): 재료 Wood·Stone·Metal 각 1개(비용·최대 체력·처음 체력 비율·건설 초·피해 배율), 최대 자원, 채집 도구(사거리·간격·피해·약점 반지름·배율), 채집 대상 4종(체력·한 번 양·부술 때 추가), 건설(사거리, 시야각, 최소 간격, 경기·플레이어 조각 상한, 초당 요청 상한 1–1000), 관심 영역(칸 크기는 건설 칸의 배수이고 맵을 64칸 이하로 나눔, 반지름, 여유). 칸 크기는 20·40·80·160 m만 된다. 파일이 없거나 틀리면 서버가 시작하지 않는다. 코드 안의 같은 값(`BuildingCatalog.DefaultJson`, 파일과 같음을 `BuildingCatalogTests`가 고정)은 파일 없이 만드는 테스트용 `GameData`만 쓴다.
- `weapons.json`(`WeaponCatalog`): 무기 1–8개, Id 1–255·이름 중복 없음, 이름 1–16 UTF-8 바이트, damage 1–65535, magazineSize 1–255, fireIntervalSeconds·reloadSeconds > 0이고 Tick으로 바꿔 65535 이하, range > 0, ammoType Light·Medium·Heavy·(Phase 17) Shells·Rockets, 모두 유한. Phase 17 D2(선택, 빠지면 옛 동작): `pellets` 1–16(기본 1), `spreadDegrees` 0–30(기본 0), `recoilDegrees` 0–30(Client만), `falloffStart` 0–range(기본 range = 감쇠 없음), `falloffMinRatio` 0–1(기본 1), `structureMultiplier` 0–10(기본 1), `projectile` "Grenade"·"Rocket"(`projectiles`에 정의가 있어야 하고 pellets 1). 옛 `spread`·`recoil` 필드는 읽지 않는다.
  - Phase 17 `projectiles`(선택): 키 Grenade·Rocket. `speed` 0 초과–200, `gravity` 0–50, `lifetimeSeconds` 0 초과–30(수류탄의 퓨즈, 로켓의 수명), `explosionRadius` 0 초과–10, `explosionDamage`·`structureDamage` 0–65535(합 > 0), `bounce` 0–0.95(0 = 맞으면 폭발). Grenade는 bounce > 0, `throwIntervalSeconds` > 0, `throwUpDegrees` −45–45가 필요하다. 운영 시작(`GameData.LoadDirectory`)은 Grenade 정의가 있어야 한다(`WeaponCatalog.RequireGrenade`). 표와 규칙은 `Weapons.md`.
- `items.json`(`ItemCatalog`): 등급 정확히 5개(이름 중복 없음, 배율 0 초과 10 이하), 탄약 Light·Medium·Heavy·(Phase 17) Shells·Rockets 각 1개(max 1–65535, pickupAmount 1–max), 소모품 Medkit·ShieldCell 각 1개(useSeconds > 0, heal·shield ≥ 0이고 합 > 0, maxStack 1–255)와 (Phase 17) Grenade 1개(useSeconds·heal·shield 없음 또는 0, maxStack 1–255). 이름은 1–16 UTF-8 바이트이고 목록 안에서 중복 없음.
- `loot.json`(`LootTable`): rarityWeights에 5개 등급 이름이 모두 있고 가중치 1–1,000,000, 표 1개 이상, 표마다 항목 1개 이상, kind(Weapon, Ammo, Medkit, ShieldCell, Phase 16: Material, Phase 17: Grenade) 중복 없음, 가중치 1–1,000,000. Shared `LootPoints`가 쓰는 표 이름이 모두 있어야 한다. Phase 17 D13: 객체 표는 `weapons`(무기 id 목록, 중복 없음, 모두 `weapons.json`에 있어야 한다 — `GameData`가 검사)와 `ammo`(탄 종류 이름 목록)를 가질 수 있다. 없으면 카탈로그 전체·모든 탄 종류에서 고른다. 표는 `Loot.md`.
  - Phase 16 D5: 표는 예전처럼 항목 배열(굴림 1번, 전역 등급)이거나 객체 `{ "rarityWeights"?, "rolls"?, "guaranteed"?, "entries" }`다. `rarityWeights`(표별, 선택)는 이름이 items.json에 있고 가중치 0–1,000,000, 합 > 0(빠진 등급 = 0: Supply Drop은 Epic·Legendary만). `rolls` 1–4(기본 1, Container 칸 수). `guaranteed`는 Weapon·Ammo·Medkit·ShieldCell 이름 목록(먼저 하나씩 나온다, rolls 이하, Material 불가). 남은 굴림은 `entries` 가중치로(남은 굴림이 있으면 항목 1개 이상). `Material` 항목은 `amount` 1–1000이 있어야 하고(재료는 나무·돌·금속 중 균등, DefId = 재료 + 1), 다른 종류에는 `amount`가 없어야 한다.
  - `spawnChance`(선택): `{ "Chest": 0–1, "AmmoBox": 0–1 }`, 기본 1. 경기 시작의 Container 생성 확률.
  - `supplyDrops`(선택): `{ "times": [초…], "fallSpeed": m/s }`. times는 자기장 시계(수송기 경로 끝) 기준 0–3600초 오름차순, 0–4개. fallSpeed 0.5–60(기본 4). 시작 높이 60 m는 Shared 상수 `SupplyDropFall.StartHeight`다(패킷에 없어서).
  - 운영 시작(`GameData.LoadDirectory`)은 표 `Chest`·`AmmoBox`·`SupplyDrop`이 모두 있어야 한다(없으면 시작하지 않는다). 테스트용 `GameData` 생성자는 검사하지 않는다(그 표가 없으면 그 Container가 생기지 않는다).
- `zones.json`(`ZoneData`): initialCenter [x, z]는 유한하고 ±arenaHalfSize 안, arenaHalfSize·initialRadius는 0 초과 10000 이하, 단계 1–16개, 단계마다 waitSeconds·shrinkSeconds > 0(Tick으로 65535 이하), targetRadius ≥ 0이고 첫 단계는 initialRadius 이하·그 뒤로는 계속 줄어든다, damagePerSecond 0–65535. 마지막 단계는 targetRadius 0이고 damagePerSecond > 0이어야 한다(모든 경기가 끝나도록).
초 값은 `SimHz`로 반올림해 Tick으로 바꾼다(최소 1). 파일이 없거나 틀리면 `GameServerService` 생성자가 `InvalidOperationException`을 던져 서버가 시작하지 않는다.

파생값: `SnapshotHz = SimHz / SnapshotEveryTicks`, `MaxInputPacketsPerSecond = SimHz * 2`(peer별 입력 Token Bucket이 초당 채워지는 수), `InputBurst = SimHz`(그 Bucket 크기. 서버 리뷰 M5, L5).

로그(서버 리뷰 M1): Console logger는 큐가 차면 기다리지 않고 줄을 버린다(`QueueFullMode = DropWrite`, `ServerHost` 코드와 `appsettings.json` 양쪽). 출력을 받지 않는 콘솔(Windows 콘솔에서 텍스트를 선택한 채 둔 QuickEdit, 아무도 읽지 않는 파이프)이 Game Loop와 LiteNetLib 스레드를 멈추지 않게 하기 위해서다. 운영 콘솔에서는 QuickEdit를 끈다. 연결마다 생기는 로그(연결·해제, Join, 유예 시작, Timeout·Join 거절 끊기)는 Debug이고 Health 줄이 센다.

## 스레드와 소유권

| 스레드 | 하는 일 | 접근하는 상태 |
|---|---|---|
| LiteNetLib 스레드 | `NetworkListener`: 연결 요청 검사(쿠키·IP별·전역 제한), 패킷 검증·파싱 | `InboundChannels`(쓰기), `PeerState`, IP별 표(`ConnectRateLimiter`: 빈도 Token Bucket·동시 연결 수, 서버 리뷰 M2·리뷰 수정 A2. 빈도 Bucket은 `OnConnectionRequest`만 쓰므로 수신 스레드 하나만 접근한다. 칸의 동시 연결 수 `Active`는 Accept 때 수신 스레드가, 끊길 때 `OnPeerDisconnected`를 부른 스레드(Game Loop의 `Close` — `UnsyncedEvents`라 `peer.Disconnect` 안에서 바로 불린다 — 또는 LiteNetLib 스레드)가 `Interlocked`로 바꾸고(0 하한은 CompareExchange 반복) `Volatile.Read`로 읽는다(리뷰 A 1차). 칸마다 `PenaltyUntilMs`는 Game Loop가 `Interlocked.Exchange`로 쓰고 수신 스레드가 `Volatile.Read`로 읽는다, 리뷰 수정 A6), 전역 수락 Bucket(`AcceptRateLimiter`), 쿠키(`ConnectCookie`: 읽기 전용 비밀 + 정적 `HMACSHA256.HashData`)와 16B 재사용 버퍼 2개(수신 스레드 전용) |
| `GameLoop` 전용 스레드(Background) | 채널 소비, Timeout 검사, `Match.Tick`, Snapshot 송신, 통계·Health 로그. 멈춰도 프로세스 종료를 막지 않는다(종료의 Join 5초 제한이 의미가 있도록) | `Match`, `_peers` 단독 소유(경기 초기화로 `Match`를 바꾸는 것도 이 스레드) |
| `StallWatchdog` Timer 스레드(Phase 10) | 1초마다 Game Loop의 마지막 Tick 시각을 `Volatile`로 읽기만 한다. Timer 콜백이 겹치면 뒤의 것은 바로 돌아간다(`Interlocked` 플래그, Lock 없음). 서버 리뷰 M8: 멈춤이 `FatalStallSeconds`를 넘으면 한 번만 `NetworkListener.BeginStopping`(volatile)과 종료 경로(`StopApplication`을 Thread Pool로)를 부른다. 그 1회 플래그와 예외 로그 시각은 겹침 방지 플래그 안에서만 읽고 쓴다 | `GameLoop.LastTickTimestamp`(읽기), `HealthCounters`(`Interlocked`) |
| `MatchHistoryWriter`(Hosted Service, async, Game Loop 밖) | `MatchHistoryQueue`에서 경기 기록을 읽어 MySQL에 저장(Phase 9). Game Loop는 큐에 넣기만 하고 DB를 기다리지 않는다 | `MatchHistoryQueue`(읽기), `MatchStore`, 카운터(`Interlocked`) |
| `StatsQueryService`(Hosted Service, async, Game Loop 밖, Phase 11) | `StatsQueryQueue`의 요청 채널의 유일한 소비자. 요청마다 DB를 조회해(조회당 3초 제한, 넘기면 그 조회를 취소) 답 채널에 넣는다. 큐에서 5초 넘게 기다린 요청은 조회하지 않고 `Unavailable`로 답한다(Client가 이미 포기했다). 예외를 밖으로 내보내지 않는다(실패는 `Unavailable` 답). 응답 송신은 하지 않는다(Game Loop가 한다) | `StatsQueryQueue`(요청 읽기·답 쓰기), 자기 `MatchStore`, 카운터(`Interlocked`) |

`NetManager`는 `UnsyncedEvents = true`, `AutoRecycle = true`. 그래서 `NetworkListener`가 받은 데이터는 그 자리에서 값 타입 메시지로 복사한다.

Phase 12: 문 상태(`DoorSet`, 열린 문 마스크와 충돌 세계 배열)와 수송기 경로(`Match`의 `_route`)는 `Match`가 소유하고 Game Loop 스레드만 읽고 쓴다. 새 Lock은 없다.

우리 코드는 Lock을 쓰지 않는다. 스레드 간 전달은 `System.Threading.Channels`, 카운터는 `Interlocked`. 따라서 Lock Ordering·Deadlock 대상이 없다.
Lock을 추가하게 되면 이 문서에 순서를 적는다.

Tick 루프: `DrainControl` → `DrainInput` → `SweepPeers`(stale peer 정리 + Join·Input Timeout + Join 거절된 peer 끊기) → `SendStatsReplies`(전적 답 최대 32개) → `Match.Tick`(맨 앞에서 `ExpireGrace`). Tick이 5 Tick 이상 밀리면 밀린 분을 건너뛴다(`lateTicksSkipped`). Tick 예외는 "예외 복구"를 본다.

`Match.Tick`은 플레이어마다 `MovementSimulation.Step(ref state, input, 1/SimHz, DoorSet.World, GameMap.Terrain)`를 호출한다(Shared 지형과 충돌. 규칙은 `Networking.md` "이동 충돌", 모드는 `Movement.md`). Phase 12: 세계는 `GameMap.Boxes` 뒤에 닫힌 문을 붙인 고정 배열이고(문이 바뀔 때만 다시 쓴다), 공중 투입 경기에서 `Transport` 탑승자는 `Step` 대신 `DropTransport.Ride`로 경로에 놓는다. `Step` 결과로 문 밀치기·이동 이상 검사·낙하 피해를 처리한다. 박스 58개(128개 이하) + 문 5개 × 100명 × 30 Hz, 지형 높이 조회는 칸 하나라 무시할 수준이고 할당이 없다. 대기 Spawn은 중앙 광장(반지름 12 m) 안의 5 m 원 위다(`GameMapTests`). 경기 시작은 수송기에서(`AirDrop`) 또는 투입 지점에서 한다(`BattleRoyale.md`).

Snapshot 송신(Phase 8): `Match.SendSnapshots`가 플레이어 목록을 90명씩 나눠 Part마다 버퍼 하나를 만들고, 수신자마다 Ack·수신자 블록만 덮어써 보낸다(100명이면 수신자당 2패킷, 할당 없음). 형식은 `Networking.md` "Snapshot 분할과 양자화".

전투(Phase 3, 규칙은 `Networking.md` "전투"·"인벤토리와 Loot"): `Match.Tick`은 (Phase 5: 경기 흐름 전환 → Zone 진행·피해) → (`DevRespawn`만) 부활 → (`DevRespawn`만) Loot 재생성 → 입력·이동 → 재장전 완료 → 실제 입력의 사용 취소·칸 선택·버리기·줍기·재장전·발사(`HitScan`)·사용 시작 → 사용 완료 → (Phase 5: 종료 판정) → `ServerTick++` → 바뀐 인벤토리 전송 → (Phase 5: 바뀐 `MatchState`·`ZoneState` 전송) → History 기록 → Snapshot 순서다. 전투 코드는 `Game/Combat/`(`WeaponCatalog`, `WeaponDefinition`, `WeaponRules`, `HitScan`, `CombatRules`, `PositionHistory`)에 있고 Game Loop 스레드만 쓴다. `WeaponCatalog`는 시작 후 바뀌지 않는다. 발사 한 번은 박스 58개 + 지형 칸 + 플레이어 수만큼의 slab 교차이고, 전송은 `_sendBuffer` 하나를 재사용하므로 발사 Tick도 할당이 없다(`LagCompensationTests.FiringTick_AllocatesNothing`).

경기 흐름(Phase 5, 규칙은 `BattleRoyale.md`): `Game/Flow/MatchFlow`(상태 기계, 판 번호, 참가자·생존자 수, 피해·부활 허용 여부)와 `Game/Zone/`(`ZoneData`, `SafeZone`: 경기마다 시드로 원을 모두 굴려 두고 `Sample`·`IsOutside`는 고정 배열만 읽는다. 반지름 0인 원은 안이 없다). 둘 다 `Match`가 소유하고 Game Loop 스레드만 쓰며 Lock이 없다. 서버는 빈 채로, 경기 전(`WaitingForPlayers`, 월드 아이템 없음)으로 시작한다. 경기 시작의 `System.Random` 생성(Loot·Zone·투입 지점 섞기, Phase 12의 수송기 경로 `DropPlanner`, Phase 16의 Container·Supply Drop 각 1개)만 할당이고(판 재시작은 할당이 없다), 진행 중인 경기의 Tick은 Zone 피해가 있어도 할당이 없다(`MatchEliminationTests.MatchTicks_WithZoneDamage_DoNotAllocate`). 경기 전에는 피해가 없고(`MatchFlow.DamageAllowed`), 경기 중 사망은 영구적이며(부활·Loot 재생성은 `DevRespawn`일 때만), 이탈은 탈락으로 처리해 인벤토리를 떨어뜨린다. `Match`의 테스트용 접근자(`Flow`, `Zone`, `MatchStartTick`, `WinnerId`)는 `InternalsVisibleTo`로만 보인다.

인벤토리·Loot(Phase 4): `Game/Items/`(`ItemCatalog`, `LootTable`, `LootSpawner`, `WorldItems`, `Inventory`, `StartingLoadout`, `ItemRules`, `ConsumableRules`)와 `Game/GameData`. 모두 Game Loop 스레드 소유이고 Lock이 없다. `WorldItems`는 256칸 고정 배열이라 선형 탐색(최대 256)이 줍기 한 번의 비용이다. 월드 아이템을 바꾸는 곳은 `Match.SpawnItem`·`RemoveItemAt`·`SetItemAmount` 셋뿐이고, 각자 이벤트 전송을 끝낸 뒤 돌아오므로 `_sendBuffer`를 쓰는 `PacketWriter`가 다른 전송과 겹치지 않는다. 줍기·회복 Tick도 할당이 없다(`PickupDropTests.PickupTick_AllocatesNothing`, `ConsumableTests.UseTicks_AllocateNothing`). `Match`와 `GameLoop` 생성자의 `StartingLoadout`·`LootPoint[]` 인자는 테스트용이고, 운영은 빈손 시작과 Shared `LootPoints`를 쓴다. 아이템은 월드(`SpawnItem`)가 받은 뒤에만 인벤토리에서 빠지고(Drop·교환·사망 Drop), `RefillLootPoints`는 Point에 아이템이 남아 있으면 다시 굴리지 않는다. `Kill`은 재장전과 회복 채널을 직접 취소한다.

## 예외 복구 (Phase 10 D6)

`GameLoop`는 어떤 예외로도 스레드가 죽지 않게 한다. 프로세스는 Game Loop 스레드 하나에 달려 있다.

- 반복 전체를 try/catch로 감싼다. Stats 로그나 대기에서 예외가 나도 스레드는 산다(`loopFailures`로 센다. Stats 주기마다 첫 하나만 로그).
- Tick 예외(`RunTickGuarded`): `tickFailures`로 센다. 로그에는 Tick 번호·경기 상태·판 번호를 넣고, 통계 주기마다 첫 하나만 남긴다(30 Hz로 같은 로그가 쌓이지 않게). 한 번의 실패는 그 Tick을 건너뛸 뿐이다.
- Timeout 검사(`SweepPeers`)에서 난 예외도 한 Tick의 실패로 센다. 경기를 초기화하지 않고 다음 Tick부터 이어 간다(`SweepPeers`는 시작할 때 목록을 비우므로 실패한 Tick의 찌꺼기가 남지 않는다). 실패가 3초 연속되어야 아래 초기화가 된다.
- Tick 예외의 로그 호출도 try/catch로 감싼다(로거나 깨진 경기 상태를 읽다가 던져도 `RunTickGuarded`는 던지지 않는다).
- `SimHz × 3`번 연속 실패(3초)하면 경기를 초기화한다: 모든 peer를 `ServerError`로 끊고 `Match`를 새로 만든다(진행 중인 판은 기록하지 않는다). Client와 봇은 자동으로 다시 접속해서 새 판에 들어온다. `matchResets`로 센다.
- 10분 안에 초기화가 3번이면(3번째로 쳐야 할 때) 코드의 문제로 보고 Critical 로그를 남기고 서버를 끝낸다(`StopApplication`, 종료 코드 1). 이 경우는 초기화가 일어나지 않으므로 `matchResets`로 세지 않는다. 이때는 peer를 먼저 `ServerError`로 끊지 않는다(다시 접속해 올 수 있으므로). 새 연결 요청은 이때부터 `ServerFull`로 거절한다. Game Loop는 Tick을 멈추고 Stop을 기다리며, Stop이 `ServerShutdown`으로 끊는다.
- 경기 기록 sink 예외는 구간마다 첫 하나를 Error 로그로 남긴다(`matchSinkFailures`는 그대로 센다).
- `PlayerSpawned`를 쓰다 넘치면(이름이 32바이트를 넘음. 연결 요청 검사 뒤라 일어나지 않아야 한다) `Match`는 반쯤 쓴 패킷을 보내지 않고 `SpawnEncodeFailures`로 센다. `GameLoop`는 Stats 때 처음 한 번만 Error 로그를 남긴다(Phase 11). 초기화가 버리는 `Match`에 아직 로그하지 않은 sink 예외가 있으면 `GameLoop`가 넘겨받아 다음 Stats 때 로그한다.
- Player 단위 격리(서버 리뷰 M7): `Match.Tick`의 플레이어 루프는 플레이어마다(`TickPlayer`: 입력·이동·재장전·행동·회복 완료) try/catch로 감싼다. 던진 플레이어는 목록(최대 `MaxPlayers`, 미리 만든 버퍼)에 적고, 루프가 끝난 뒤(붕괴·종료 판정 전) 그 플레이어만 경기에서 내보낸다(이탈과 같다. 유예 중이면 바로 지운다). 그 뒤 `GameLoop`가 `playerFailures`로 세고(구간당 첫 하나만 Error 로그) 그 연결을 `ServerError`로 끊고(`kicks serverError`) `_peers`에서 뺀다. 다른 플레이어와 Tick, Snapshot은 그대로 이어진다. 경기 초기화는 플레이어 루프 밖 단계(흐름·Zone·건설·송신)의 실패에 쓴다. 리뷰 1차: 경기 상태가 깨져 모든 플레이어가 매 Tick 실패하면 Tick은 성공으로 끝나므로, `GameLoop`가 플레이어 실패의 Tick 번호와 출처(그 연결의 `ConnectRateLimiter` 칸, 리뷰 수정 A6. 연결이 없으면 각자 다른 출처)를 Ring에 적는다. 10초 안에 `MaxPlayers`번이 되고 출처가 둘 이상이면 경기 전체의 결함으로 보고 그 Tick 뒤에 같은 초기화를 한다(초기화 횟수와 fatal 판정도 같다). 한 출처뿐이면 그 주소의 짓으로 보고 초기화하지 않는다(SEC-14: Client가 일으킬 수 있는 예외 하나로 공격자 한 명이 리셋·서버 정지를 만들지 못하게). 같은 출처가 60초 안에 3번이면 그 IP 칸을 60초 동안 거절한다(`penalties`). 아래 '모두 실패한 Tick' 5번 규칙도 같은 창 안 실패의 출처가 둘 이상일 때만 초기화한다(한 주소가 연결 여러 개를 열어 둔 채 혼자 거듭 실패해도 초기화하지 않는다). 유예 중 플레이어(연결 없음)의 실패는 출처를 몰라 따로 센다. 리뷰 2차: 접속한 Client가 적으면 `MaxPlayers`번에 닿지 않으므로(1명이 재접속마다 실패하면 10초에 약 9번), 한 Tick에 루프를 돈 플레이어가 모두 실패한 Tick(`Match.EveryPlayerFailed`)도 5칸 Ring에 적는다. 10초 안에 5번이면 같은 초기화를 한다. 플레이어 자신의 상태는 그 플레이어와 함께 사라지므로, 새로 들어온 플레이어까지 다른 모두와 함께 거듭 실패하면 경기의 결함으로 본다. 4명 중 1명만 거듭 실패하는 경우는 초기화하지 않는다.
- 멈춤 대응(서버 리뷰 M8): Watchdog이 Tick이 `FatalStallSeconds`(기본 30초)보다 오래 없다고 보면 한 번만 `stallExits`를 세고 Critical 로그를 남기고, 새 연결 요청을 `ServerFull`로 거절하기 시작하고, 종료 코드 1로 Host를 멈춘다(위 초기화 실패와 같은 종료 경로). 멈춘 Game Loop 스레드는 종료의 Join 5초 제한 뒤 남겨 두고(Background) `ServerShutdown`으로 끊는다. 재시작은 프로세스 감시자(서비스 관리자, 컨테이너 재시작 정책)가 한다. 0이면 끈다. 앞선 검사가 이미 본 멈춤이 이어질 때만 멈춘다: 디버거 중단이나 프로세스 일시 정지 뒤 한 번의 검사가 긴 공백을 보는 것만으로는 서버를 멈추지 않는다(실제 Hang은 최대 1초 늦게 멈춘다).
- 콜백 경계(서버 리뷰 L9): `OnConnectionRequest`, `OnPeerDisconnected`, `OnNetworkError`, Watchdog의 `Check`는 예외를 밖으로 내지 않는다. `callbackErrors`로 세고 구간당 한 번 Error 로그를 남긴다(Watchdog은 Stats 줄이 없어 10초에 한 번). `Accept` 전에 던진 연결 요청은 거절하고, `Accept` 뒤에 던진 연결은 `ServerError`로 끊는다(Game Loop가 모르는 연결이 자리를 차지하지 않게). `OnNetworkError`는 평소에도 `networkErrors`로 세고 구간당 한 번만 Warning 로그를 남긴다(서버 리뷰 M1).
- `Program`이 `AppDomain.UnhandledException`(Critical 로그, `Console.Error`에도 기록. 로그 큐가 프로세스 종료 전에 비지 않을 수 있어서다)과 `TaskScheduler.UnobservedTaskException`(Error 로그, 관찰한 것으로 처리)을 로그로 남긴다. 처리되지 않은 예외는 런타임이 정한 대로 프로세스를 끝낸다.

## Queue

| Queue | 크기 | 가득 찼을 때 |
|---|---|---|
| Control 채널 | 3 × (MaxPlayers + AcceptBurst + ⌈AcceptsPerSecond / SimHz⌉)(리뷰 1·2차, 리뷰 수정 A2: 전역 수락 Bucket 기준. 기본 3 × (16 + 16 + 1) = 99) | TryWrite 실패 → 해당 peer를 `ServerError`로 끊는다(Critical 로그, `kicks serverError`로 센다. 서버가 끊은 것이라 유예 없음). Disconnected 메시지가 유실되면 stale-peer 정리가 대신 처리 |
| Input 채널 | MaxPlayers × InputBurst(= SimHz, 기본 16 × 30 = 480. 리뷰 수정 A5: 모든 Player의 burst를 한꺼번에) | 가장 오래된 입력 폐기(inputDrops) |
| Build 채널(Phase 13) | MaxPlayers × 2 × `building.json` maxRequestsPerSecond(기본 16 × 2 × 20 = 640. 리뷰 수정 A5: 연결당 1초 고정 창 두 개가 맞붙으면 2배가 한꺼번에 온다). Game Loop는 Tick당 MaxPlayers × 8개까지 비운다 | 가장 오래된 요청 폐기(`buildInboxDrops`) |
| Marker 채널(Phase 15) | MaxPlayers × 4(`InboundChannels.MarkersPerPlayer`) | 가장 오래된 지도 표시 요청 폐기(`markerInboxDrops`). 수신 스레드가 연결당 한 번에 4개만 넘기고 Game Loop가 Tick마다 채널 용량만큼 비운다 |
| 팀 Ping(Phase 15, `Match.Map`) | 팀당 8칸 고정 배열(256 × 8, 생성 때 한 번) | 가장 오래된 Ping 교체(`replaced`). 만료·이탈·경기 시작·끝·라운드 리셋에 비운다 |
| PlayerInputBuffer(플레이어별) | InputBufferPerPlayer(8) | 가장 오래된 입력 폐기(bufferDrops) |
| `MatchHistoryQueue`(Phase 9) | `Persistence:QueueCapacity`(16) | Reject: 기록을 버리고 `Dropped`를 센다. 생산자 Game Loop(경기당 1회), 소비자 `MatchHistoryWriter` 하나 |
| `StatsQueryQueue` 요청 채널(Phase 11) | 32 | Reject: 수신 스레드가 요청자에게 `Busy`를 답한다(`busy`). 생산자 수신 스레드, 소비자 `StatsQueryService` 하나 |
| `StatsQueryQueue` 응답 채널(Phase 11) | 32 | Reject: 답을 버리고 `undelivered`를 센다(Client는 5초 뒤 "응답 없음"). 생산자 `StatsQueryService`와 수신 스레드(`Busy`), 소비자 Game Loop |

Game Loop는 Tick당 입력 메시지를 최대 `MaxInputMessagesPerTick`개만 소비한다. Control 채널은 연결당 이벤트가 최대 3개(Connected, Join, Disconnected)라는 전제로 크기를 정했다. 모든 Player 몫에 더해, 한 Drain 주기(1 Tick) 안에 전역 수락 Bucket이 들일 수 있는 연결 전부(burst `AcceptBurst` = MaxPlayers와 그 사이 다시 채워지는 Token ⌈AcceptsPerSecond / SimHz⌉)가 연결·Join·해제해도 다른 Player의 메시지가 들어갈 자리를 둔다(리뷰 1·2차, 리뷰 수정 A2: 여러 IP가 함께 churn해도 넘지 않는다. 기본 3 × (16 + 16 + 1) = 99, MaxPlayers 100이면 603). 연결당 3개 전제는 "Join은 연결당 1회" 규칙(`PeerState.JoinRequested`)이 지킨다. Input 채널은 모든 peer가 공유하므로, Join 전 입력 거절과 peer별 입력 Token Bucket(한 번에 `SimHz`, 초당 `SimHz * 2`. 넘으면 버리고 `inputRate`로 센다)으로 한 peer가 채널을 독점하지 못하게 한다. Control 채널은 전역 수락 Bucket(리뷰 수정 A2)이 연결 churn으로부터 지키고, IP별 Bucket·동시 연결 수(서버 리뷰 M2, 리뷰 수정 A2)가 한 주소의 몫을 제한한다.

| 표·버퍼(서버 리뷰) | 크기 | 제거·덮어쓰기 |
|---|---|---|
| `ConnectRateLimiter` 표(M2, 리뷰 수정 A2·A6) | 1024칸 고정, 언제나 만든다(빈도 제한이 꺼져도 동시 연결 수·벌점에 쓴다). 칸 = 해시(IP ^ 시작 때 난수 salt) | 지우지 않는다. 같은 칸에 오는 IP들이 그 칸의 Bucket·연결 수·벌점을 나눠 쓴다(빈 칸만 가득 찬 Bucket으로 시작). 연결 수는 Accept 때 +1(수신 스레드), 끊길 때 −1(끊김을 처리한 스레드, 0 아래로 가지 않는다). 둘 다 `Interlocked`. 벌점은 시각이 지나면 효력이 없다 |
| `AcceptRateLimiter`(리뷰 수정 A2) | Bucket 하나 | 없음(값만 바뀐다) |
| `GameLoop._playerFailureMarks`(리뷰 1차, 리뷰 수정 A6) | max(`MaxPlayers`, 3)칸 Ring(Tick, IP 칸), 한 번 만든다 | 가장 오래된 칸을 덮어쓴다. 경기 초기화가 비운다 |
| `GameLoop._allFailedTicks`(리뷰 2차) | 5칸 Ring, 한 번 만든다 | 가장 오래된 칸을 덮어쓴다. 경기 초기화가 비운다 |
| `Match._failedPlayers`(M7) | `MaxPlayers` 용량으로 한 번 만든다 | 플레이어 루프 시작과 실패한 플레이어 처리 뒤에 비운다 |
| `ProjectileSet`(Phase 17 D6) | 32칸 고정 배열(`ProjectileSet.Capacity`), 생성 때 한 번 | 가득 차면 새 투사체를 만들지 않는다(로켓은 탄을 쓰지 않고 불발, 수류탄은 던져지지 않음). 칸은 폭발 때, 그리고 경기 시작·라운드 리셋·경기 끝에 모두 비운다 |
| `Match._explosionPieces`(Phase 17 D8) | 폭발 한 번이 볼 수 있는 조각 상한(7 × 7 칸 × 16 층 × 5 = 3,920 id), 생성 때 한 번 | 폭발마다 처음부터 다시 쓴다. 상한을 넘는 조각은 피해를 받지 않는다(반지름 10 m 상한 안에서는 넘지 않는다) |
| `Match._explosionLog`(Phase 17 D17, QA 관찰) | 16칸 Ring, 생성 때 한 번 | 가장 오래된 칸을 덮어쓴다 |
| `Match._pelletTargets`·`_pelletPieces`(Phase 17 D4) | 16칸(`MaxPellets`) 고정 배열 | 사격마다 처음부터 쓰고, 끝나면 플레이어 참조를 지운다 |

## Lifetime

- Session(`_peers` 항목, `PlayerEntity`): `Connected` 메시지에서 등록, Disconnected 메시지 또는 peer 상태가 Connected가 아니면 제거. `Connected` 메시지는 `OnConnectionRequest`에서 `PeerState` 설정 후 쓴다(`OnPeerConnected`는 `Accept()` 안에서 Tag 설정 전에 호출되기 때문). LiteNetLib DisconnectTimeout이 끊김을 보장한다.
- LiteNetLib는 peer id를 재사용한다. 메시지의 NetPeer 참조가 현재 `_peers`의 peer와 같을 때만 처리한다. 같은 id로 다른 NetPeer가 `Connected`로 들어오면 새 peer를 먼저 `_peers`에 등록하고 이전 세션을 정리한다(`DropSession`. 서버 리뷰 L12: 이전 세션 정리가 던져도 새 연결은 등록되어 Join Timeout이 회수한다). 이전 peer의 늦은 Disconnected는 참조 비교로 무시된다.
- `Match.Leave`는 `PlayerDespawned`를 남은 플레이어에게 보낸다.
- 플레이어별 전투 상태는 `PlayerEntity`에 있고 플레이어와 함께 사라진다. 위치 History는 32칸 고정 링이라 늘어나지 않고, Join·부활 때 새로 시작한다. 인벤토리는 칸 3개·탄약 3종 고정 배열이다. 경기 중에 접속을 끊은 참가자의 인벤토리는 남은 사람들에게 사망 Drop처럼 떨어진다(Phase 5 D10). 경기 밖(대기·결과 화면·`DevRespawn`)에서 끊으면 떨어뜨리지 않고 사라진다.
- 경기 상태(`MatchFlow`의 수, `PlayerEntity`의 Participant·Placement·Kills)는 고정 필드다. 판 재시작(`Closing`)이 월드 아이템을 모두 지우고 모두를 Spawn에 살려 두므로 판이 바뀌어도 아무것도 쌓이지 않는다.
- 월드 아이템은 256개가 상한이고(가장 오래된 Drop부터 지움), Spawn Point 타이머는 Point마다 하나씩 고정 배열이다.
- Session 유예(Phase 10): `Match._graced`(유예 중인 캐릭터 목록)는 최대 `MaxPlayers`개다. 항목은 재접속(Resume)했을 때, 유예 시간이 끝났을 때, 죽었을 때, 판이 재시작할 때 제거된다. Resume이 아닌 세 경우는 `graceExpiries`로 센다.
- Join 거절(Phase 10): `Match`가 Join을 거절하면(`MatchFull`, 유예 캐릭터가 자리를 차지한 경우) 그 peer는 Join한 것으로 치지 않는다(`PeerState.JoinRefused`). Join Timeout·Input Timeout 대상이 아니고, `SweepPeers`가 1초(`SimHz` Tick) 뒤 코드 없이(`None`) 끊는다. 바로 끊지 않는 것은 ReliableOrdered `JoinMatchResponse(MatchFull)`가 먼저 나가게 하기 위해서다. Kick으로 세지 않는다. 전적 요청도 받지 않는다: 수신 스레드는 Game Loop가 `Ok`·`Resumed`일 때만 쓰는 `PeerState.Joined`(volatile, Game Loop만 쓰고 수신 스레드가 읽는 유일한 Game Loop 필드)를 보고 전적 요청을 받는다(Phase 11).
- 종료(D7): Ctrl+C → Host `StopAsync` → 새 연결 거절 시작(`NetworkListener.BeginStopping`, 이후 요청은 `ServerFull`) → Tick 멈춤(Game Loop 스레드 Join, 5초 제한. 넘으면 Critical 로그를 남기고 진행) → 모든 peer에 `DisconnectAll(ServerShutdown)` → peer가 0이 되거나 최대 1초 기다림 → `NetManager.Stop` → `StatsQueryService`(요청 읽기를 멈춘다. 큐에 남은 요청은 답하지 않는다. 받아 줄 Game Loop가 이미 멈췄다) → `MatchHistoryWriter`(큐를 닫고 남은 기록을 `ShutdownDrainSeconds` 동안 저장). 등록 순서가 Writer → `StatsQueryService` → `GameServerService`라 Host가 역순으로 멈춘다: Game Loop → `StatsQueryService` → Writer. Host `ShutdownTimeout`은 90초(`ShutdownDrainSeconds` 최대 60초 + 30초)라 저장 시간 제한이 Host 기본 30초에 잘리지 않는다. `GameServerService.StopAsync`는 이 일을 Thread Pool에서 하고 Host 토큰까지만 기다린다(호출 스레드를 막지 않는다). 진행 중인 판은 끝나지 않았으므로 기록하지 않는다.

## 관측

10초마다 한 줄: `Stats players=… pktIn/s … bytesOut/s … tickMs p50/p95/p99/max … inputDrops bufferDrops badPackets lateTicksSkipped exceptions gc workingSetMB cpu% matchSinkFailures`. `cpu%`는 프로세스 CPU 시간 증가 / (Stats 간격 × 논리 프로세서 수) × 100이다(Phase 7 D10, 코어 하나를 다 쓰면 100 / 코어 수). `matchSinkFailures`는 경기 기록을 만들거나 큐에 넣다가 예외가 난 경기 수(누적)다. 이 예외는 결과 전송 뒤에 잡아서 세기만 하고 Game Loop를 멈추지 않는다(Phase 9). 부하 측정 결과는 `LoadTest.md`.
패킷 단위 로그는 없다. Tick 예외는 통계 주기당 1회만 로그한다.

**Health 줄(Phase 10 D9):** Stats와 같은 간격으로 한 줄 더 남긴다. 성능은 Stats, 상태는 Health로 나눈다. 값은 `peers`·`players`·`graced`·`match`(상태와 판 번호)만 그 시점 값이고, 나머지는 서버 시작부터의 누적이다.

```text
Health peers players graced match=<State>#<Round>
  connections joins resumed graceStarts graceExpiries
  disconnects timeout other
  rejects full badRequest version connectRate perIp penalized accept cookie cookieChallenges
  kicks kicked joinTimeout inputTimeout serverError congested
  badPackets unknownId malformed beforeJoin duplicateJoin inputRate wrongDirection handlerException buildRate markerRate
  inputSeqDrops
  tickFailures loopFailures matchResets stalls movementAnomalies networkErrors playerFailures penalties stallExits callbackErrors
  build pieces cells requests accepted destroyed collapsed duplicates eventPackets syncPackets
  buildRejects noResource outOfRange blocked unsupported occupied rateLimited invalidState invalidRequest budgetFull
  harvest hits envDestroyed syncDeferred
  buildInboxDrops
  squad downs revives reboots bleedOuts cardsDropped cardsExpired wipes channelsCancelled
  map pings enemyConfirmed enemyDemoted refused replaced expired waypoints packets markerDrops markerInboxDrops
  loot containersOpened dropsSpawned dropsLanded dropsOpened items blocked packets
  db saved failed discarded dropped
  stats requests limited busy unavailable undelivered
```

- `peers`: 열린 연결 수. `players`: 경기의 플레이어 수(유예 중 포함). `graced`: 재접속을 기다리는 플레이어 수.
- `joins`·`resumed`: 새 Join과 Resume 수. `graceStarts`: 유예가 시작된 수. `graceExpiries`: Resume 없이 유예가 끝난 수(시간 초과, 유예 중 사망, 판 재시작). 만료마다 DevPlayerId를 넣은 Information 로그가 하나 남는다. 경기 초기화가 버린 유예 캐릭터는 세지 않는다.
- `disconnects timeout`·`other`: 끊긴 연결 수(LiteNetLib 사유가 `Timeout`인지 아닌지).
- `rejects`: 연결 요청 거절(이유별). 종료 중의 거절도 `full`로 센다. `kicks`: 서버가 끊은 수(코드별. `kicked`는 잘못된 패킷 때문에 끊은 수, `serverError`는 경기 초기화와 Control 채널이 가득 차서 끊은 수, `congested`는 아래 "밀린 연결"로 끊은 수). 종료(`ServerShutdown`)와 Join 거절 뒤의 끊기는 Kick이 아니라 세지 않는다.
- 밀린 연결(Phase 13 최종 리뷰 A4): `SweepPeers`가 Tick마다 Join한 연결의 신뢰 대기열(LiteNetLib `GetPacketsCountInReliableQueue`, 채널 0 + 1)을 읽는다. 512개(`GameLoop.MaxReliableBacklog`)를 넘은 채 10초(`CongestedSeconds`)가 지나면 `Congested`로 끊는다. 한 번이라도 그 아래로 내려가면 다시 센다(`PeerState.CongestedSinceTick`, Game Loop만 쓴다). 링크가 게임 트래픽을 받지 못하는 연결이 LiteNetLib 메모리를 끝없이 키우지 않게 한다. Match는 GameLoop가 한 번 만든 질의 delegate로 건설 채널 대기열만 읽어, 32개를 넘은 연결의 그 Tick `BuildSync`를 건너뛴다(`syncDeferred`).
- `badPackets` 9개 항목(Phase 13 `buildRate`, Phase 15 `markerRate` 포함): 잘못된 패킷(이유별, `Networking.md` "Validation").
- `tickFailures`·`loopFailures`·`matchResets`: 예외 복구 카운터. `stalls`: Watchdog이 센 멈춤.
- 서버 리뷰: `rejects connectRate`(M2)는 IP별 연결 빈도를 넘어 거절한 요청(Client에는 `ServerFull`로 가지만 `full`에는 세지 않는다). `networkErrors`(M1)는 LiteNetLib가 알린 소켓 오류. `playerFailures`(M7)는 자기 Tick 부분이 던져 경기에서 빠지고 `ServerError`로 끊긴 플레이어. `stallExits`(M8)는 `FatalStallSeconds`를 넘은 멈춤으로 서버를 멈춘 수(프로세스당 최대 1). `callbackErrors`(L9)는 콜백 경계에서 잡은 예외. 정상이면 `networkErrors` 밖은 모두 0이다.
- 리뷰 수정 A(네트워크 진입): `rejects perIp`는 IP당 동시 연결 수(`MaxConnectionsPerIp`), `penalized`는 벌점 중인 IP, `accept`는 전역 수락 빈도(`AcceptsPerSecond`)로 거절한 요청이다(셋 다 Client에는 `ServerFull`, `full`에는 세지 않는다). `cookie`는 틀리거나 낡은 쿠키를 가진 요청(새 쿠키를 돌려준다). `cookieChallenges`는 쿠키 없는 첫 요청에 쿠키를 돌려준 수로, 정상 접속마다 1씩 오른다(거절이 아니다). `inputSeqDrops`는 Seq 창(`InputSeqWindow`)을 넘어 버린 입력(정상 Client는 0, 경기 초기화를 넘어 합계가 이어진다). `penalties`는 같은 IP 칸의 플레이어 실패가 60초에 3번이 되어 그 칸에 60초 벌점을 준 횟수다(`Networking.md` "Validation"). LiteNetLib 자체 메시지(예: 조각 상한을 넘은 메시지마다 "Invalid FragmentsTotal")는 `Program`이 시작 때 단 `NetDebug.Logger`(`LiteNetLogBridge`)가 ILogger 카테고리 `LiteNetLib`의 Debug로 보낸다. 설정하지 않으면 LiteNetLib가 수신 스레드에서 콘솔에 바로 쓴다.
- `movementAnomalies`(Phase 12 D12): 한 Tick의 이동이 그 모드의 최대 속도 × dt × 1.5를 넘은 수(`MovementLimits`, `Movement.md` "이동 이상 검사"). 서버가 이동을 입력만으로 직접 계산하므로 치트가 아니라 시뮬레이션 버그를 알리는 값이다. 정상이면 언제나 0이다. Phase 13: 건설 조각 안에서 시작한 이동(머리를 가로질러 지은 경사로가 한 번에 2 m 넘게 들어 올리는 경우 등)은 조각이 민 것이라 세지 않는다. 맵 상자·문·채집 대상 안에서 시작한 이동은 그대로 센다.
- `build`(Phase 13): `pieces`·`cells`는 지금 서 있는 조각 수와 조각이 있는 건설 칸 수(공간 색인), 나머지는 누적이다. `requests`는 Game Loop가 처리한 요청, `accepted`는 지어진 수, `destroyed`는 부서진 조각(붕괴 포함), `collapsed`는 그중 지지를 잃어 무너진 수, `duplicates`는 이미 본 번호라 버린 요청, `eventPackets`·`syncPackets`는 보낸 건설 패킷 수다. `buildRejects`는 거절 코드별 수다. `badPackets`에는 `buildRate`(연결당 초당 상한 초과)가 더해졌다.
- `harvest`(Phase 13): 채집 타격 수(`hits`)와 부서진 채집 대상 수(`envDestroyed`). `syncDeferred`: 건설 채널이 밀려 Sync를 건너뛴 (연결, Tick) 수. Meter는 `projecth.build.sync_deferred`.
- 경기 초기화(Phase 13 최종 리뷰 B12): `build`·`harvest`의 누적 값은 새 `Match`에서 0부터 다시 세지만, `HealthCounters`가 버린 경기의 합계를 기준값으로 들고 더하므로 Health 줄과 Meter의 값은 줄지 않는다. `pieces`·`cells`는 지금 값이다.
- `map`(Phase 15): `pings`는 받아들인 Ping(확인된 Enemy 포함), `enemyConfirmed`·`enemyDemoted`는 Enemy 확인 성공·실패(Location으로 바뀜), `refused`는 받지 않은 요청(경기 밖, 살아 있는 팀원이 아님, 맵 밖, 없거나 먼 Item), `replaced`는 상한 때문에 바뀐 Ping, `expired`는 수명이 끝난 Ping, `waypoints`는 Waypoint 설정·삭제, `packets`는 보낸 `TeamMarkers` 수다. `markerDrops`는 수신 스레드의 연결별 Token Bucket이 버린 `MapMarker`(Kick 없음), `markerInboxDrops`는 가득 찬 Marker 채널이 밀어낸 요청이다. `badPackets markerRate`는 1초에 20개를 넘은 `MapMarker`(끊기 기준). 경기 초기화 때 `CarryMapTotals`로 줄지 않는다. Meter는 `projecth.map.events`(event Tag)와 `projecth.map.marker_drops`(where = rate·inbox).
- `loot`(Phase 16, `Loot.md`): `containersOpened`는 열린 Chest·Ammo Box, `dropsSpawned`·`dropsLanded`·`dropsOpened`는 Supply Drop 생성·착지·열림, `items`는 Container·Supply Drop이 월드에 놓은 아이템, `blocked`는 시선에 막혀 열지 못한 E, `packets`는 보낸 `ContainerStates`·`SupplyDrops` 수다. 경기 초기화 때 `CarryLootTotals`로 줄지 않는다. Meter는 `projecth.loot.events`(event Tag).
- 시작할 때 `Server:BuildInfiniteResources`가 켜져 있으면 Warning 로그를 남긴다(부하 측정 전용).
- `buildRejects`의 `rateLimited`(Phase 13): 플레이어 큐(8개)가 가득 차 버린 요청도 센다(`requests`에도 들어간다).
- `buildInboxDrops`(Phase 13): 수신 스레드에서 Game Loop로 가는 유한 채널(`InboundChannels.Build`)이 넘쳐 버린 요청 수. 정상이면 0이다. Meter는 `projecth.build.inbox_drops`.
- `db`: `MatchHistoryWriter`의 `Saved`·`Failed`·`Discarded`와 큐의 `Dropped`(`Database.md`).
- `stats`(Phase 11, 전적 조회): `requests`는 요청 채널에 받아들인 요청 수. `limited`는 답 없이 버린 요청(Join이 성공하지 않은 연결이거나 같은 연결의 앞 요청 뒤 2초 안). `busy`·`unavailable`은 그 상태로 답한 수(`busy`: 요청 채널이 가득 참, `unavailable`: Persistence 꺼짐·큐에서 5초 넘게 기다림(조회하지 않음)·DB 실패·3초 초과). `undelivered`는 나가지 못한 답(응답 채널이 가득 참, 또는 요청한 연결이 이미 없음). `Networking.md` "전적 조회".

**Meter:** 같은 수치를 `System.Diagnostics.Metrics`의 `Meter("ProjectH.Server")`로도 낸다(`ServerMeter`, Observable 계측기만 쓴다. Game Loop에서 기록하지 않고, 읽는 쪽이 부를 때 `HealthCounters`를 읽는다). 새 패키지도 열린 포트도 없다.

```bash
dotnet-counters monitor -n ProjectH.Server --counters ProjectH.Server
```

| 계측기 | 종류 | 태그 |
|---|---|---|
| `projecth.peers`, `projecth.players`, `projecth.graced` | Gauge | |
| `projecth.match_state` | Gauge | 값 = `MatchFlowState`(0 WaitingForPlayers, 1 Starting, 2 Playing, 3 FinalPhase, 4 Finished, 5 Closing) |
| `projecth.connections`, `projecth.joins`, `projecth.resumes`, `projecth.grace_starts`, `projecth.grace_expiries` | Counter | |
| `projecth.disconnects` | Counter | `reason` = `timeout` / `other` |
| `projecth.rejects` | Counter | `reason` = `ServerFull` / `BadRequest` / `VersionMismatch` / `ConnectRate`(서버 리뷰 M2) / `PerIp` / `Penalized` / `AcceptRate` / `Cookie`(리뷰 수정 A2·A3·A6) |
| `projecth.cookie_challenges`, `projecth.penalties`, `projecth.input_seq_drops` | Counter | 리뷰 수정 A3, A6, A4 |
| `projecth.kicks` | Counter | `code` = `Kicked` / `JoinTimeout` / `InputTimeout` / `ServerError`(종료는 Kick이 아니라 `ServerShutdown` 계열이 없다) |
| `projecth.bad_packets` | Counter | `reason` = `BadPacketReason` 7가지 |
| `projecth.tick_failures`, `projecth.loop_failures`, `projecth.match_resets`, `projecth.stalls` | Counter | |
| `projecth.movement_anomalies` | Counter | Phase 12. 정상이면 0 |
| `projecth.network_errors`, `projecth.player_failures`, `projecth.stall_exits`, `projecth.callback_errors` | Counter | 서버 리뷰 M1, M7, M8, L9 |
| `projecth.build.pieces`, `projecth.build.cells` | Gauge | Phase 13. 서 있는 조각, 조각이 있는 건설 칸 |
| `projecth.build.requests` | Counter | `result` = `Ok` / 거절 코드 이름 9개 / `Duplicate` |
| `projecth.build.destroyed` | Counter | `cause` = `damage` / `collapse` |
| `projecth.build.inbox_drops` | Counter | Phase 13. 건설 입력 채널이 넘쳐 버린 요청 |
| `projecth.harvest.hits`, `projecth.harvest.destroyed` | Counter | Phase 13 |
| `projecth.db_records` | Counter | `result` = `saved` / `failed` / `discarded` / `dropped` |
| `projecth.stats_queries` | Counter | `result` = `requests` / `limited` / `busy` / `unavailable` / `undelivered` |

**Stall Watchdog:** `StallWatchdog`가 1초마다 Game Loop의 마지막 Tick 끝 시각을 본다. 2초 넘게 Tick이 없으면 Critical 로그를 한 번 남기고 `stalls`를 센다. Tick이 돌아오면 걸린 시간을 Warning으로 남긴다. Deadlock이나 무한 루프처럼 예외 없이 멈추는 경우를 로그 없이 놓치지 않게 한다. 종료 중과, 일부러 쉬는 경우(서버가 끝나기를 기다리는 동안)는 멈춤으로 보지 않는다.

**로그:** 콘솔 로그에 시각이 붙는다(`appsettings.json`의 `Logging:Console:FormatterOptions:TimestampFormat`, `yyyy-MM-dd HH:mm:ss.fff`). 접속·Join·이탈·재접속·Join/Input Timeout으로 끊기는 연결·유예 만료마다 한 번 Information이다. Kick(잘못된 패킷)은 Warning, 거절된 연결 요청은 Debug다(`Logging:LogLevel`을 Debug로 올려야 보인다).

전적 조회 로그(`StatsQueryService`): 시작할 때 Persistence가 꺼져 있으면 "Stats queries: persistence disabled; every request is answered Unavailable."(Information). DB 실패가 시작될 때 Warning 한 줄("the database failed … answering Unavailable until it recovers"), 회복될 때 Information 한 줄("the database answers again"). 실패가 계속되는 동안은 요청마다 로그하지 않는다.

봇(부하·경기 테스트용 Client)의 실행은 `Bots.md`를 본다. 서버는 봇을 구분하지 않는다.
