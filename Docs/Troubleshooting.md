# Troubleshooting — 문제가 터졌을 때 볼 곳

2026-10-09, main(Phase 19 + 리뷰 수정 A–D, Protocol v19) 기준이다. 이 문서는 길잡이이고, 상세 규칙은 링크한 문서에 있다. 기능 목록은 [Features.md](Features.md)에 있다.

**찾는 순서**

1. 아래 "증상별 위치"에서 해당 줄을 찾는다.
2. 서버 로그의 `Stats`·`Health` 줄로 어느 쪽 문제인지 좁힌다("관측 도구").
3. 의심 코드의 테스트나 QA 시나리오를 돌린다.
4. 언제부터 그랬는지는 "작업 이력"의 Phase와 커밋으로 거슬러 올라간다.

## 1. 작업 이력

| Phase | 한 일 | 대표 커밋 | Protocol | 문서 |
|---|---|---|---|---|
| 0 | 네트워크 이동 동기화 | `a76861f` | 1 | Networking |
| 1 | 박스 충돌, 어깨 카메라 | `9059a5a` | 2 | Networking |
| 3 | 서버 권한 전투, 지연 보상 | `5082b3e` | 3 | Networking, Weapons |
| 4 | 인벤토리·Loot | `3ab3600` | 4 | Networking |
| 5 | 배틀로얄 규칙, Safe Zone | `8c74caf` | 5 | BattleRoyale |
| 6 | 맵(지형, POI, 건물) | `9e6f018` | 6 | Map |
| 7 | Headless 봇, 부하 테스트 | `b464a9c` | 6 | Bots, LoadTest |
| 8 | Snapshot 분할·양자화, 100명 | `ebb3af8` | 7 | Networking, LoadTest |
| 9 | MySQL 경기 기록·통계 | `bf28afa` | 7 | Database |
| 10 | Hardening(끊기 코드, 재접속, Timeout, 예외 복구) | `d0cddbf` | 8 | Server, Networking |
| 11 | 게임 UI, 전적 조회 | `560d8ea` | 9 | Client |
| 12 | 이동 모드, 공중 투입, 문, 낙하 피해 | `89ac904`, main `7f9a559` | 10 | Movement |
| 13 | 채집·건설, 지지, 건설 채널 | main `923ff3a` | 11 | Building |
| QA-1–5 | QA 시나리오 도구, 스트레스 | `5a581d3`–`3add189` | – | QA |
| 13.5 | 건설 편집 | `963bed8` | 12 | Building "편집" |
| 14 | 분대·기절·소생·Reboot | `a6ea942` | 13 | Squad |
| 15 | 미니맵·지도·Ping | `2b8eee1` | 14 | Map, Client |
| 16 | Loot 상자·보급 | `c43e07c`, 리뷰 `121ce78` | 15 | Loot |
| 17 | 무기 6종, 탄 퍼짐·반동, 투척·로켓 | `fa78616` | 16 | Weapons |
| 18 | 합성 오디오 | `339b923` | 17 | Audio |
| 19 | 차량 | `2adf601`, 리뷰 `69a3f63` | 18 | Vehicles |
| 리뷰 A–D | 접속 허가, 세션 키·데이터그램 인증, 시드 비밀값·전투 규칙, 맵 경계·수신 검증 | `ee7b65a`, `a74cf24`, `7b40f88`, `39ff83e` | 19 | Server, Networking, `plans/2026-10-08-review-fixes.md` |
| 주석 일괄(2026-10-10) | 전체 코드의 함수 주석(기능/입력/출력) 추가·수정. 코드 변경 없음 | `8c5bdef` | 19 | `code-comments` 스킬 |

- Phase 2는 없다(번호만 건너뛰었다).
- 설계 문서의 "결정과 추천 이유" 표에 결정마다 근거와 "틀렸을 때의 비용"이 있다. 동작이 의도된 것인지 헷갈리면 거기서 D번호를 찾는다.
- 각 Plan의 "Known Issues"·"spec와 다른 점" 절은 알려진 한계다. 버그로 고치기 전에 먼저 본다.
- Monitoring Server는 `monitoring` 브랜치에만 있다(main 미병합).

## 2. 증상별 위치

경로 약칭:

- `S/` = `Server/src/ProjectH.Server/`
- `Sh/` = `Shared/Runtime/`
- `C/` = `Client/Assets/Scripts/`
- `T/` = `Server/tests/ProjectH.Server.Tests/`

### 서버 시작·접속·인증

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 서버가 "The development server key is refused in Production"으로 시작하지 않음 | `S/Net/ServerIdentity.cs` | Server "실행" | 빌드한 dll은 `--environment Development`로 띄운다(`dotnet run`은 `launchSettings.json`이 해 준다). 운영은 `Server:PrivateKeyPem`(환경 변수에는 XML을 넣는다) 또는 `Server:PrivateKeyPath` |
| 서버가 그 밖의 이유로 시작하지 않음 | `S/ServerOptions.cs` `Validate`, `S/Game/GameData.cs`(JSON 8개 검증), `PersistenceOptions.Validate` | Server "실행" | 시작 예외 메시지. `*.json`이 출력 폴더에 복사됐는지 본다 |
| 접속하자마자 거절(`VersionMismatch`) | `Sh/Protocol/ProtocolConstants.cs` | Networking "접속 순서" | Health `rejects version`. Shared를 바꾼 뒤 한쪽만 빌드한 경우다 |
| 접속 거절(`BadRequest`) | `S/Net/NetworkListener.cs`, `Sh/Protocol/SessionAuth.cs` | Networking "Validation" | 공개 키 불일치(Client `Resources/ServerPublicKey.txt`, 봇 `--server-public-key`)나 이름 규칙 위반. `T/Net/*` |
| 같은 PC에서 봇 5개째부터 접속 실패 | `S/Net/ConnectRateLimiter.cs`(IP당 4) | Server "보안·접속 허가", Bots "실행" | Health `perIpRejects`·`acceptRateRejects`. 서버에 `--Server:ConnectBurstPerIp=200 --Server:MaxConnectionsPerIp=200`을 준다 |
| 연결이 쿠키 단계에서 멈춤, 연결이 끊김 반복 | `S/Net/ConnectCookie.cs`, `ConnectRateLimiter`(벌점) | Server | Health `cookieRejects`·`cookieChallenges`·`penaltyRejects` |
| 연결은 되는데 패킷이 무시됨 | `S/Net/AuthPacketLayer.cs`(HMAC 꼬리, 재전송 창 64) | Networking | Health `authDrops`·`authDropsRetired`·`inputSeqDrops` |
| 접속은 되는데 경기에 안 들어감 | `S/Game/Match.cs` `TryJoin`(`MatchFull`, `AlreadyJoined`) | Server "Lifetime" | Health `joins`, `peers` 대비 `players` |
| 5초·10초 뒤 끊김 | `S/GameLoop.cs` `SweepPeers` | Networking "Timeout" | Health `kicks joinTimeout/inputTimeout`. 디버거로 10초 넘게 멈추면 `InputTimeout`이 정상이다 |
| `Kicked`·`Congested`로 끊김 | `NetworkListener` 패킷 검증, 건설 채널 대기열 | Networking "Validation", Building "네트워크" | Health `badPackets` 이유별 항목 |
| 재접속이 안 됨 | `Match.Resume`, 재접속 증명(`SessionAuth`), `C/Game/GameClient.cs` 재접속 사이클 | Networking "끊기와 재접속" | Health `resumes`·`graceExpiries`, QA `Reconnect/`. 같은 이름만으로는 안 되고 증명이 있어야 한다 |
| 모두 `ServerError`로 끊기고 판이 새로 시작 | `GameLoop` `RunTickGuarded`(Tick 3초 연속 실패하면 초기화) | Server "예외 복구" | Health `tickFailures`·`matchResets`, 같은 주기의 첫 예외 로그 |

### 이동·예측·차량

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 내 캐릭터가 계속 튐(보정) | `C/Game/LocalPlayerPredictor.cs`, `Sh/Simulation/MovementSimulation.cs`. 서버와 Client가 같은 Shared를 쓰는지 본다 | Networking "Movement" | F1 디버그 줄(마지막 보정 거리), `LocalPlayerPredictorTests` |
| 벽·조각 통과, 맵 밖으로 나감 | `MovementSimulation`(`ClampToMap`), `CollisionWorld`, `BuildGrid.PartsOf`(편집 모양) | Movement, Building | `CollisionTests`, `PieceCollisionTests`, `EditedPieceCollisionTests` |
| 슬라이드·Vault·글라이더·수송기가 이상함 | `MovementSimulation`, `MovementTuning`, `DropTransport` | Movement | `MovementModesTests`, `VaultTests`, `DeploymentMovementTests` |
| `movementAnomalies` > 0 | `S/Game/MovementLimits.cs`. 정상이면 0이다. 시뮬레이션 버그다(치트가 아니다) | Movement | Health 줄 |
| 차량이 튐, 벽을 뚫고 타고 내림 | `Sh/Simulation/VehicleSimulation.cs`, `S/Game/Match.Vehicles.cs`, `C/Game/VehiclePredictor.cs`, `VehicleStore`(너무 앞선 Tick은 버린다) | Vehicles | `VehicleSimulationTests`, suite `vehicle` |
| 기절 상태 이동 | `MovementSimulation`(Downed 모드 7) | Movement "기절", Squad | `SquadMatchTests` |

### 전투·무기

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 맞혔는데 피해가 없음 | `S/Game/Combat/HitScan.cs`, `PositionHistory`(되감기 한도는 RTT로 정한다), 경기 전 피해 없음, 같은 팀 피해 없음 | Networking "전투", Weapons | `HitScanTests`, `LagCompensationTests`, DB `match_player` 의 anti-cheat 열(`RewindClamped`) |
| 무기를 바꾼 직후 안 쏴짐 | 장착 지연 `equipSeconds`(0.4초, 리뷰 C), `C/Game/WeaponState.cs`가 예측 | Weapons, Networking | `WeaponRulesTests` |
| 산탄·퍼짐·반동 | `S/Game/Combat/WeaponSpread.cs`(서버 결정), `C/Game/SpreadCone.cs`(표시만), `C/Camera/RecoilKick.cs` | Weapons | suite `weapons` |
| 수류탄·로켓이 안 나감 | `S/Game/Combat/Projectiles.cs`(전체 32, 소유자당 4), `Match.Projectiles.cs` | Weapons "투사체" | `ProjectilePhase17Tests` |
| 공중·수송기·기절 중 행동 불가 | `Match.ActionsAllowed`. 의도한 동작이다 | Movement | – |

### Loot·인벤토리·상자

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 줍기가 안 됨 | `Match.Pickup`, `S/Game/Items/ItemRules.cs`(거리, 시선 검사, 행동 간격 0.25초) | Networking "인벤토리와 Loot" | `PickupDropTests` |
| E가 엉뚱한 대상에 반응함 | E 우선순위: 탑승 중이면 내리기 → 소생·Reboot 대상 → 문·상자 중 가까운 것(같으면 문) → 차량 탑승 → 줍기 (`Match.Loot.Interact`) | Squad, Loot | `ContainerRuleTests`, `SquadPromptTests` |
| 상자가 안 열림, 보급이 안 떨어짐 | `S/Game/Match.Loot.cs`, `S/Game/Loot/ContainerRules.cs`, `Sh/Simulation/LootContainers.cs`(`SupplyDropFall`) | Loot | `LootContainerMatchTests`, suite `loot`. 빈자리가 없으면 10초 재시도 뒤 건너뛴다 |
| 바닥 Loot 배치가 바뀜 | 상자·보급은 시드가 따로다 | Loot | `FloorLootRegressionTests` |

### 건설·편집

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 배치가 거절됨 | `S/Game/Build/BuildRules.cs`(이유 코드) | Building "검증" | Health `buildRejected`, F1 건설 줄 |
| 편집이 거절되거나 되돌아감 | `BuildRules.CanEdit`(소유자만), 공유 큐 8칸(`RateLimited`) | Building "편집" | `BuildEditTests`, QA `building_edit_*` |
| 무너져야 하는데 서 있음(또는 반대) | `S/Game/Build/BuildSupport.cs` | Building "지지" | `SupportTests`, Health `buildCollapsed` |
| 조각이 안 보이거나 늦게 보임 | `BuildReplication`(관심 영역, Sync 대기), `C/Game/Build/BuildStore.cs` | Building "네트워크" | Health `buildSyncDeferred` |

### 분대·지도

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 기절·소생·Reboot 이상 | `S/Game/Match.Squad.cs`, `squad.json` | Squad | Health `downs/revives/reboots/bleedOuts`, suite `squad` |
| 팀 순위·경기 종료 이상 | `MatchFlow`(남은 팀 ≤ 1이면 끝) | BattleRoyale, Squad | `MatchFlowTests` |
| Ping이 안 보이거나 위치 Ping으로 바뀜 | `S/Game/Match.Map.cs`(적 Ping은 시선·팀·거리 검사), `C/Game/Map/PingInput.cs` | Map "지도 UI·Ping" | Health `enemyDemoted`·`markersRefused`·`markerDrops`, suite `map` |
| 지도·미니맵 위치가 어긋남 | `C/Game/Map/MapProjection.cs` | Client | `MapProjectionTests`, QA `minimap_position` |

### 경기 흐름·Zone·DB·UI·오디오·봇

| 증상 | 먼저 볼 코드 | 문서 | 확인 수단 |
|---|---|---|---|
| 경기가 시작하지 않음 | `S/Game/Flow/MatchFlow.cs`(`MinPlayers`) | BattleRoyale | Health `match=<State>#<Round>` |
| 재현이 안 됨(시드가 매번 다름) | 시드는 경기마다 비밀값으로 만든다(리뷰 C). 재현은 `DeterministicSeeds=true`일 때만 된다 | Server, BattleRoyale | QA 도구는 이 옵션을 켜고 띄운다 |
| Zone 원이 서버와 다름 | `S/Game/Zone/SafeZone.cs`, `C/Game/ZoneMath.cs` | BattleRoyale | `ZoneMathParityTests` |
| 경기 기록이 안 남음 | `S/Persistence/*`, `schema_version` v2 마이그레이션 | Database | Health `db saved/failed/dropped`, 시작 로그 |
| 화면 흐름·한글·Kill Feed | `C/UI/UiFlow.cs`, `UiFont.cs`, `KillFeed*.cs` | Client | `UiFlowTests`, QA `UI/` |
| 소리가 안 나거나 겹침 | `C/Game/Audio/*`(Voice 24, 프레임당 새 소리 8) | Audio | `AudioMixerModelTests`, QA `Audio/` |
| 봇이 멈춤·낌·끊김 | `Server/src/ProjectH.Bots/BotBrain.cs`, `BotSteering.cs`, `BotConnection.cs` | Bots | `BotBrainTests`, 봇 통계 줄 |
| Tick이 밀림, 메모리 증가 | `GameLoop`, `Match.SendSnapshots`, 건설 복제 | Server "관측", LoadTest, QA "Stress" | Stats `tickMs p95/p99`, `gc`, `workingSetMB`, Health `stalls`, suite `stress-quick` |

## 3. 관측 도구

| 도구 | 쓰는 법 |
|---|---|
| 서버 로그(10초마다) | `Stats`는 성능(Tick 분위수, 패킷, 바이트, GC, CPU), `Health`는 상태(연결·거절·Kick·인증·건설·분대·Ping·Loot·DB). 필드 뜻은 `Server.md` "관측" |
| Meter | `dotnet-counters monitor -n ProjectH.Server --counters ProjectH.Server` |
| Client | F1 디버그 줄(상태, RTT, 모드, 속도, 보정 거리, 건설 줄) |
| QA 도구 | `dotnet run --project Server/src/ProjectH.QA -- run <시나리오>` 또는 `ui`(Web UI). Suite `smoke`, `pre-push`, `full-regression`, `stress-quick`. 사용법은 `QA.md` |
| 재현 | `--Server:DeterministicSeeds=true`이면 시드 + 판 번호로 Loot·Zone·투입이 같아진다. 봇은 `--seed` |
| 아레나 | `DevRespawn=true`이면 경기 흐름 없이 피해·부활·Loot 재생성만 돈다 |
| 테스트 | `dotnet test Server/ProjectH.Server.slnx`(`--filter FullyQualifiedName~클래스`). `MySqlTests`는 `PROJECTH_TEST_MYSQL`이 있을 때만 돈다. Client EditMode는 `Client.md` "자동 검사" |

## 4. 바꿀 때 자주 터지는 곳

- **패킷 형식을 바꿨다**
  - `ProtocolVersion`을 올린다. 주석에 v1–v19 이력이 있다.
  - Client(Unity 재컴파일), 봇, QA 도구를 함께 갱신한다.
  - 받는 쪽 범위 검사(`ProtocolLimits`)도 맞춘다.
- **Shared를 바꿨다**
  - 서버는 `netstandard2.1`·C# 9로 컴파일한다.
  - 새 `.cs`에는 `.meta`를 커밋한다.
  - Shared에 넣어도 되는 범위는 `game-core-rules` 4절 예외 1–8이다.
- **규칙 복사본이 있는 곳**
  - `DoorRule`, `ContainerRule`, `SquadPrompt`, `VehiclePrompt`, `ToolState`는 서버 규칙의 Client 복사본이다.
  - 한쪽만 바꾸면 비교 테스트가 깨진다.
- **맵을 바꿨다**
  - 다음을 함께 고친다(`Map.md` "맵 바꾸기"): 박스, 지형, 문, 채집 대상, Loot 지점, 상자, Reboot 스테이션, 차량 생성 위치, POI.
  - 상자·스테이션 목록 순서는 프로토콜 비트 순서다.
- **이동·차량 수치를 바꿨다**
  - `MovementTuning`, `MoveSettings`, `VehicleSettings`는 서버와 예측이 같이 쓴다.
- **Snapshot에 필드를 더한다**
  - 90명 패킷은 MTU에 거의 꽉 찬다(Entity 13 B).
  - 상태는 빈 플래그 비트나 별도 패킷으로 보낸 선례를 따른다(모드, 도구, `VehicleStates`).
- **Game Loop에서 DB·I/O를 부르면 안 된다**
  - 큐로 넘긴다.
  - 새 Lock을 쓰면 `Server.md`에 순서를 적는다(지금은 `SessionKeys._sendLock` 하나).
- **Tick 경로에 할당을 넣으면 안 된다**
  - `…_AllocatesNothing` 테스트가 잡는다.
