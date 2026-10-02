# Troubleshooting — 문제가 터졌을 때 볼 곳

2026-10-02, Phase 12 Task 5까지 기준. 이 문서는 길잡이다. 상세 규칙은 링크한 문서에 있다.

**찾는 순서**

1. 아래 "증상별 위치"에서 해당 줄을 찾는다.
2. 서버 로그의 `Stats`·`Health` 줄로 어느 쪽 문제인지 좁힌다("관측 도구").
3. 의심 코드의 테스트를 돌린다.
4. 언제부터 그랬는지는 "작업 이력"의 Phase와 커밋으로 거슬러 올라간다.

## 1. 작업 이력

| Phase | 한 일 | 커밋 | Protocol | 설계 / 계획 |
|---|---|---|---|---|
| 기반 | 프로젝트 골격, Claude Code 하네스 | `cddcfdf` | – | – |
| 0 | 네트워크 이동 동기화: 서버 Tick, 입력·Snapshot, 예측·보정, 보간 | `a76861f` | 1 | `specs/…phase0-network-sync-design.md` |
| 1 | 캐릭터 프로토타입: 박스 충돌, 어깨 카메라, 로컬 발사 표현 | `9059a5a` | 2 | `specs/…phase1-character-prototype-design.md` |
| 3 | 서버 권한 전투: Hitscan, 지연 보상, 사망·부활, HUD | `5082b3e` | 3 | `specs/…phase3-combat-design.md` |
| 4 | 인벤토리·Loot: 월드 아이템, 줍기·버리기, 소모품 | `3ab3600` | 4 | `specs/…phase4-inventory-loot-design.md` |
| 5 | 배틀로얄 규칙: 경기 흐름, Safe Zone, 영구 사망, 결과 | `8c74caf`, `131adfc` | 5 | `specs/…phase5-battle-royale-design.md` |
| 6 | 맵: 높이 격자 지형, POI, 건물 박스, Loot·투입 지점 | `9e6f018` | 6 | `specs/…phase6-map-design.md` |
| 7 | 봇: Headless 봇 Client, 부하 테스트 | `b464a9c` | 6 | `specs/…phase7-bots-design.md` |
| 8 | Snapshot 최적화: 분할·양자화, 100명 | `ebb3af8` | 7 | `specs/…phase8-optimization-design.md` |
| 9 | 영속화: MySQL 경기 기록·통계, Game Loop 밖 저장 | `bf28afa` | 7 | `specs/…phase9-persistence-design.md` |
| 10 | Hardening: 끊기 코드, 재접속 유예, Timeout, 예외 복구, 관측 | `d0cddbf` | 8 | `specs/…phase10-hardening-design.md` |
| 11 | 게임 UI: 타이틀·메뉴·끊김·결과·전적 화면, 이름, Kill Feed | `560d8ea`, `0e1b3e4` | 9 | `specs/…phase11-game-ui-design.md` |
| 12 (진행 중) | 투입·이동: 이동 모드, 기력, 웅크리기·슬라이드, Vault, 수송기·자유 낙하·글라이더, 낙하 피해, 행동 제한, 모드별 Hit Box | `0841508` → `89ac904` | 10 | `specs/…phase12-deployment-traversal-design.md` |

Phase 2는 따로 없다(번호를 건너뛰었다). 설계 문서의 "결정과 추천 이유" 표에 각 결정의 근거와 "틀렸을 때의 비용"이 있다. 동작이 의도된 것인지 헷갈리면 거기서 D번호를 찾는다.

### Phase 12 진행 상태 (중요)

계획은 `Docs/plans/2026-10-02-phase12-deployment-traversal.md`(Task 1–10)다.

**완료 (Task 1–5)**

- Shared 이동: `MovementMode`, `MovementTuning`, `MovementSimulation`, `DropTransport`
- Protocol v10: `TraversalPackets`, 14바이트 Self 블록, `Crouch` 버튼
- 서버 통합: 수송기 투입, 낙하 피해, 행동 제한, 모드별 Hit Box, 이동 이상 검사

**남음 (Task 6–10)**

- 문(`GameMap.Doors`, `DoorStates` 처리)
- 봇 투입(봇은 아직 `TransportRoute`를 쓰지 않는다)
- Client 예측·표현(Client 코드는 아직 `MovementMode`를 모른다)
- 문서 갱신

따라서 지금은 이런 일이 **예상된 동작**이다.

- Unity Client로 경기를 시작하면 수송기·낙하 구간에서 화면이 서버와 어긋나거나 보정이 계속 일어날 수 있다.
- 봇은 수송기에서 뛰어내리지 않고 경로 끝에서 강제로 떨어진다.
- 문 패킷 Id는 있지만 문은 아직 맵에 없다.

이 구간의 버그를 고치기 전에 해당 Task가 끝났는지 먼저 본다.

## 2. 증상별 위치

경로 약칭:

- `S/` = `Server/src/ProjectH.Server/`
- `Sh/` = `Shared/Runtime/`
- `C/` = `Client/Assets/Scripts/`
- `T/` = `Server/tests/ProjectH.Server.Tests/`

### 접속·끊김

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 접속하자마자 거절 | `S/Net/NetworkListener.cs`(연결 요청 검사), `Sh/Protocol/ProtocolConstants.cs` | Networking "접속 순서", "Validation" | Health `rejects full/badRequest/version`. `version`이 늘면 Client·서버의 `ProtocolVersion`이 다르다. Shared를 바꾼 뒤 한쪽만 빌드했는지 본다 |
| 이름 때문에 거절(`BadRequest`) | `ProtocolConstants.IsValidPlayerName`, `ConnectRequestData.TryRead` | Networking "Validation" | `T/Shared/PacketTests`, `T/Integration/PlayerNameIntegrationTests` |
| 접속은 되는데 경기에 안 들어감 | `S/Game/Match.cs` `TryJoin`(`MatchFull`, `AlreadyJoined`) | Server "Lifetime"(Join 거절) | Health `joins`, `peers` 대비 `players` |
| 5초·10초 뒤 끊김 | `S/GameLoop.cs` `SweepPeers`(Join·Input Timeout) | Networking "Timeout" | Health `kicks joinTimeout/inputTimeout`. 디버거로 Client를 10초 넘게 멈추면 `InputTimeout`이 정상이다 |
| `Kicked`로 끊김 | `NetworkListener` 패킷 검증, `BadPacketReason` | Networking "Validation" | Health `badPackets` 이유별 7개 항목 |
| 재접속이 안 되거나 다른 캐릭터로 돌아옴 | `Match.Disconnect`·`FindGraced`·`Resume`·`ExpireGrace`, `Sh/Protocol/DisconnectCode.cs`(`DisconnectCodes.ShouldReconnect`), `C/Game/GameClient.cs`(재접속 사이클) | Networking "끊기와 재접속", Client "끊김과 자동 재접속" | Health `graced`·`resumed`·`graceExpiries`, `T/Game/ReconnectGraceTests`, `T/Integration/ReconnectIntegrationTests` |
| 모두 `ServerError`로 끊기고 판이 새로 시작 | `GameLoop` `RunTickGuarded`(Tick 3초 연속 실패 → 경기 초기화) | Server "예외 복구" | Health `tickFailures`·`matchResets`. 같은 주기의 첫 예외 로그(Tick 번호·상태·판)를 찾는다 |
| 서버가 혼자 종료(코드 1) | `GameLoop`: 10분 안에 초기화 3번 | Server "예외 복구" | Critical 로그 |
| 서버가 시작하지 않음 | `S/ServerOptions.cs` `Validate`, `S/Game/GameData.cs`(JSON 4개 검증), `PersistenceOptions.Validate` | Server "실행" | 시작 예외 메시지. `weapons/items/loot/zones.json`이 출력 폴더로 복사됐는지 본다 |

### 이동·예측

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 내 캐릭터가 계속 튐(보정) | `C/Game/LocalPlayerPredictor.cs`, `Sh/Simulation/MovementSimulation.cs`. 서버와 Client가 같은 `Step`·지형·박스를 쓰는지 본다 | Networking "Movement" | F1 디버그 줄, Client EditMode `LocalPlayerPredictorTests`·`MapPredictionTests`. Shared를 바꿨다면 Unity가 다시 컴파일했는지 본다 |
| 다른 플레이어가 끊기며 움직임 | `C/Game/RemotePlayerInterpolator.cs`, `ServerClock.cs` | Networking "Movement" | `RemotePlayerInterpolatorTests` |
| 벽·박스 통과, 지형에 빠짐 | `MovementSimulation`(충돌), `Sh/Simulation/HeightField.cs`, `GameMap.cs`, `Box.cs` | Networking "이동 충돌", Map | `T/Shared/CollisionTests`, `HeightFieldTests`, `TerrainMovementTests`, `GameMapTests` |
| 입력이 안 먹힘(Client) | `C/Input/InputReader.cs`, `C/UI/UiFlow.cs`(메뉴가 열렸거나 커서가 안 잠김) | Client "프레임 흐름" | 클릭해 커서를 잠갔는지 본다 |
| 기력·웅크리기·슬라이드·달리며 점프가 이상함 (P12) | `MovementSimulation`, `Sh/Simulation/MovementTuning.cs`(모든 수치) | spec P12 D1–D3, D7 | `T/Shared/MovementModesTests` |
| Mantle·Hurdle이 안 되거나 엉뚱한 곳에 섬 (P12) | `MovementSimulation` Vault 판정(앞 상자, 높이 범위, 윗면 공간, 도착 겹침) | spec P12 D8 | `T/Shared/VaultTests` |
| 수송기·자유 낙하·글라이더 (P12) | `Sh/Simulation/DropTransport.cs`(경로, `Ride`), `Match.StartMatch`, `S/Game/Flow/DropPlanner.cs`, `Match.SendRoute` | spec P12 D4–D6 | `T/Shared/DeploymentMovementTests`, `T/Game/MatchDeploymentTests`, `T/Shared/TraversalPacketTests` |
| 낙하 피해가 이상함, 원인이 "낙하"로 안 나옴 (P12) | `Match.ApplyFallDamage`, `Kill(…, DeathCause)`, `PlayerDied.Cause` | spec P12 D10 | `T/Game/FallDamageTests` |
| 서버 로그에 `movementAnomalies` > 0 (P12) | `S/Game/MovementLimits.cs`, `Match.Move`. 정상이면 언제나 0이다. 0이 아니면 시뮬레이션 버그다(치트가 아니다) | spec P12 D12 | Health 줄, Meter `projecth.movement_anomalies` |

### 전투

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 맞혔는데 피해가 없음 | `S/Game/Combat/HitScan.cs`, `PositionHistory.cs`(지연 보상, 32칸), `Match.FireShot`·`ApplyHit`. 경기 전에는 피해가 없다(`MatchFlow.DamageAllowed`) | Networking "전투" | `T/Game/HitScanTests`, `LagCompensationTests`, `TerrainTraceTests` |
| 웅크렸는데 머리가 맞음, 탑승자가 맞음 (P12) | `HitScan`(모드별 높이), `PositionHistory`(모드 기록) | spec P12 D13 | `T/Game/HitBoxModeTests` |
| 발사·재장전 간격, 탄 | `S/Game/Combat/WeaponRules.cs`, `weapons.json`, `C/Game/WeaponState.cs`(Client 표시) | Networking "전투" | `WeaponRulesTests`, `WeaponCatalogTests` |
| 공중·수송기에서 사격·줍기가 안 됨 | `Match.ActionsAllowed`. P12에서 의도한 동작이다 | spec P12 D12 | – |
| 조준점과 실제 탄 방향이 다름 | `C/Game/AimSolver.cs`, `C/Camera/ShoulderCamera*.cs` | Client | `AimSolverTests`, `ShoulderCameraMathTests` |

### Loot·인벤토리

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 줍기가 안 됨 | `Match.Pickup`·`PickupWeapon`, `S/Game/Items/ItemRules.cs`(수평 2 m, 높이 2 m), `C/Game/PickupRule.cs` | Networking "인벤토리와 Loot" | `T/Game/PickupDropTests` |
| 아이템이 안 생기거나 사라짐 | `S/Game/Items/LootSpawner.cs`, `LootTable`, `WorldItems`(256개 상한, 오래된 Drop부터 지움), `Sh/Simulation/LootPoints.cs` | Map "Loot 지점" | `LootTableTests`, `WorldItemsTests`, `MatchWorldItemsTests` |
| 사망·이탈 때 아이템 Drop | `Match.DropEverything`·`DropAround` | Server "Lifetime" | `InventoryMatchTests` |
| 회복이 안 됨 | `S/Game/Items/ConsumableRules.cs` | – | `ConsumableTests` |

### 경기 흐름·Zone

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 경기가 시작하지 않음 | `S/Game/Flow/MatchFlow.cs`(`MinPlayers` 2) | BattleRoyale "상태 기계" | Health `match=<State>#<Round>` |
| 투입 위치가 이상함 | `Match.ShuffleDropOrder`·`DropSpot`, `Sh/Simulation/DropPoints.cs`, P12부터는 수송기 | Map "투입 지점", spec P12 D4 | `DropAssignmentTests`, `DropPointsTests` |
| Zone 원·피해가 이상함, Client 원과 서버가 다름 | `S/Game/Zone/SafeZone.cs`, `ZoneData`(`zones.json`), `C/Game/ZoneMath.cs`(표시 전용) | BattleRoyale "Safe Zone" | `SafeZoneTests`, `ZoneMathParityTests`(서버·Client 식이 같은지) |
| 순위·승자·결과 화면 | `Match.FinishMatch`·`Kill`, `C/UI/ResultScreen.cs` | BattleRoyale "사망·순위·승자" | `MatchEliminationTests`, `BattleRoyaleIntegrationTests` |

### DB·전적

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 경기 기록이 안 남음 | `S/Persistence/MatchHistoryWriter.cs`, `MatchStore.cs`, `MatchHistoryQueue`(16, 넘치면 버림) | Database "실패 처리와 종료" | Health `db saved/failed/discarded/dropped`, 시작 로그 `Match history: …`. `Persistence:Enabled`, `docker compose ps`를 확인한다 |
| 전적 창이 "기록을 볼 수 없음" 또는 "응답 없음" | `S/Persistence/StatsQueryService.cs`, `StatsQueryQueue`, `C/UI/StatsWindow.cs` | Database "조회 경로" | Health `stats requests/limited/busy/unavailable/undelivered` |
| 스키마가 옛 정의 그대로 | `MatchStore.Schema`(`CREATE TABLE IF NOT EXISTS`) | Database "실행" | `docker compose down -v`. **로컬 데이터가 모두 지워진다** |

### Client UI·화면

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 화면 전환이 이상함 | `C/UI/UiFlow.cs`(순수 로직), `UiRoot.cs` | Client "화면과 흐름" | `T/ClientUi/UiFlowTests` |
| 한글이 네모로 보임 | `C/UI/UiFont.cs` | Client "글꼴" | Console "UI font: …" |
| 빌드에서 재질이 분홍색 | `C/Game/LitMaterial.cs`(URP Lit 기반) | 커밋 `0e1b3e4` | – |
| Kill Feed, HUD 문구 | `C/UI/KillFeed*.cs`, `UiText.cs`, `C/Game/MatchHudText.cs`, `InventoryHudText.cs` | Client | `KillFeedModelTests`, `UiTextTests`, `MatchHudTextTests` |

### 봇·부하

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 봇이 멈춤·벽에 낌 | `Server/src/ProjectH.Bots/BotBrain.cs`, `BotSteering.cs` | Bots "판단 규칙" | `T/Bots/BotBrainTests` |
| 봇이 끊김 | `BotConnection.cs`, `--reconnect` 옵션 | Bots "실행" | 봇 통계 줄 `reconnects` |
| Tick이 밀림, CPU·메모리 증가 | `GameLoop`, `Match.SendSnapshots` | Server "관측", LoadTest | Stats `tickMs p95/p99`, `lateTicksSkipped`, `gc`, `workingSetMB`. Health `stalls`(`StallWatchdog`) |

## 3. 관측 도구

**서버 로그(10초마다)**

- `Stats …`: 성능 지표다. 인원, 패킷, 바이트, `tickMs` 분위수, 입력 Drop, 잘못된 패킷, GC, CPU, `matchSinkFailures`.
- `Health …`: 상태 지표다. 연결·Join·유예, 끊김과 Kick 이유, 잘못된 패킷 이유, Tick 실패·초기화·Stall, `movementAnomalies`, DB, 전적 조회. 대부분 서버 시작부터의 누적값이다.

필드의 뜻은 `Server.md` "관측"에 있다.

**나머지 도구**

- **Meter:** `ServerMeter`(이름 `ProjectH.Server`). `dotnet-counters monitor -n ProjectH.Server --counters ProjectH.Server`로 본다.
- **Client:** F1 디버그 줄(상태, RTT, Entity).
- **봇:** `--stats-interval` 초마다 한 줄을 남긴다.
- **재현:** `LootSeed`·`ZoneSeed`·`SpawnSeed` + 판 번호가 같으면 Loot 배치·Zone·투입 순서가 같다. 봇은 `--seed`로 고정한다.
- **아레나 모드:** `DevRespawn=true`이면 경기 흐름 없이 피해·3초 부활·Loot 재생성만 돈다. 전투·Loot만 볼 때 쓴다.

**테스트**

- `dotnet test Server/ProjectH.Server.slnx`가 서버, Shared, 봇, Client UI 순수 코드를 시험한다.
- 한 묶음만 돌리려면 `--filter FullyQualifiedName~VaultTests`처럼 쓴다.
- `MySqlTests`는 환경 변수 `PROJECTH_TEST_MYSQL`(연결 문자열)이 있을 때만 돈다. 없으면 Skip된다.
- Client EditMode 테스트 명령은 `Client.md` "자동 검사"에 있다.

## 4. 바꿀 때 자주 터지는 곳

- **패킷 형식을 바꿨다:** `ProtocolConstants.ProtocolVersion`을 올린다(주석에 이력이 있다). 서버와 Client(Unity 재컴파일)를 함께 갱신한다. 하나만 바뀌면 `VersionMismatch`로 접속이 거절된다.
- **Shared를 바꿨다:** 서버는 `netstandard2.1`·C# 9로 컴파일하므로 Unity에서 안 되는 문법이 서버 빌드에서 먼저 실패한다. 새 `.cs`에는 Unity `.meta` 파일도 커밋한다.
- **맵을 바꿨다:** 박스, 지형, Loot·투입 지점, POI, 문을 함께 고친다(`Map.md` "맵 바꾸기"). 이동 결과가 바뀌면 Protocol 버전도 올린다(Phase 6 선례).
- **이동 수치를 바꿨다:** `MovementTuning`, `MoveSettings`는 서버와 Client 예측이 같이 쓴다. 둘이 다르게 빌드되면 보정이 계속된다.
- **Snapshot에 필드를 더한다:** 90명 패킷에 남은 공간이 3바이트다. Self를 더 늘리면 패킷당 인원을 89로 줄여야 한다(spec P12 D11).
- **Game Loop에서 DB·I/O를 부르면 안 된다:** 큐(`MatchHistoryQueue`, `StatsQueryQueue`)로 넘긴다. 우리 코드에는 Lock이 없다. 추가하면 `Server.md`에 순서를 적는다.
- **Tick 경로에 할당을 넣으면 안 된다:** `…_AllocatesNothing`, `…_DoNotAllocate` 테스트가 깨진다.
