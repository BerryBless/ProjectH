# 구현된 기능

2026-10-09, main(Phase 19 + 리뷰 수정 A–D, Protocol v19) 기준이다. 한 줄 요약과 상세 문서·주요 코드·검증 수단만 적는다. 규칙과 수치는 링크한 문서에 있다.

경로 약칭:

- `S/` = `Server/src/ProjectH.Server/`
- `Sh/` = `Shared/Runtime/`
- `C/` = `Client/Assets/Scripts/`

QA 시나리오는 `QA/Scenarios/<분류>/`, Suite는 `QA/Suites/`에 있다.

## 기반

| 기능 | 내용 | 문서 | 주요 코드 | 검증 |
|---|---|---|---|---|
| 서버 Tick·네트워크 동기화 (P0) | 30 Hz Simulation, 15 Hz Snapshot, 입력 최근 3개 전송, 내 캐릭터 예측과 보정, 원격 보간 | [Networking](Networking.md) | `S/GameLoop.cs`, `S/Game/Match.cs`, `Sh/Simulation/MovementSimulation.cs`, `C/Game/LocalPlayerPredictor.cs`, `RemotePlayerInterpolator.cs` | `MovementSimulationTests`, `LocalPlayerPredictorTests`, suite `smoke` |
| 박스 충돌·카메라 (P1) | AABB 축 분리 Sweep, 어깨 카메라·ADS | [Networking](Networking.md) "이동 충돌" | `Sh/Simulation/CollisionWorld.cs`, `C/Camera/ShoulderCamera.cs` | `CollisionTests`, `ShoulderCameraMathTests` |
| Snapshot 최적화 (P8) | Entity 13 B 양자화, 패킷당 90명 분할, 100명 | [Networking](Networking.md), [LoadTest](LoadTest.md) | `Sh/Protocol/ServerPackets.cs`, `Match.SendSnapshots` | `SnapshotSplitTests`, suite `stress-quick` |
| 영속화·전적 (P9, P11) | 경기 기록·통계 MySQL 저장(Game Loop 밖), 전적 조회 | [Database](Database.md) | `S/Persistence/*` | `MySqlTests`(DB 있을 때), `StatsQueryTests` |
| Hardening (P10) | 끊는 이유 코드, 자동 재접속, 10초 유예, Join·Input Timeout, Tick 예외 복구, Stall 감시, Health 로그 | [Server](Server.md), [Networking](Networking.md) "끊기와 재접속" | `S/GameLoop.cs`, `S/Diagnostics/*`, `Sh/Protocol/DisconnectCode.cs` | `ReconnectIntegrationTests`, `ExceptionRecoveryTests`, QA `Reconnect/`, suite `faults` |
| 접속 허가·인증 (리뷰 A·B) | 연결 쿠키, IP당 연결 상한, 수락 속도 제한, RSA 서버 키 고정, 세션 키, 데이터그램 HMAC 꼬리·재전송 차단, 재접속 증명 | [Server](Server.md), [Networking](Networking.md) | `S/Net/*`, `Sh/Protocol/SessionAuth.cs`, `C/Net/AuthPacketLayer.cs` | `Server.Tests/Net/*`, QA `Network/` |
| 경기 공정성·상태 정확성 (리뷰 C·D) | 경기마다 비밀값으로 시드 생성, 무기 장착 지연, RTT 기반 되감기 한도, 줍기 시선 검사, anti-cheat 기록 열, 맵 경계 고정, ViewTick uint, 받은 패킷 범위 검사 | [Networking](Networking.md), [Database](Database.md), [Movement](Movement.md) | `S/Game/Match.cs`, `Sh/Protocol/ProtocolLimits.cs`, `MovementSimulation.ClampToMap` | `Docs/plans/2026-10-08-review-fixes.md` 결과 표 |

## 게임플레이

| 기능 | 내용 | 문서 | 주요 코드 | 검증 |
|---|---|---|---|---|
| 서버 권한 전투 (P3) | 입력 비트로 발사, Hitscan, 지연 보상(32칸 위치 기록, 최대 0.4 s = 12 Tick 되감기), Health·Shield | [Networking](Networking.md) "전투", [Weapons](Weapons.md) | `S/Game/Combat/HitScan.cs`, `PositionHistory.cs` | `HitScanTests`, `LagCompensationTests`, QA `Combat/` |
| 인벤토리·Loot (P4) | 무기 3칸, 탄약, 등급 5단계, 회복 채널, 줍기·버리기 | [Networking](Networking.md) "인벤토리와 Loot" | `S/Game/Items/*` | `PickupDropTests`, `ConsumableTests` |
| 배틀로얄 규칙 (P5) | 경기 상태 기계, Safe Zone, 영구 사망, 관전, 순위 | [BattleRoyale](BattleRoyale.md) | `S/Game/Flow/MatchFlow.cs`, `S/Game/Zone/SafeZone.cs` | `MatchFlowTests`, `SafeZoneTests`, QA `Zone/` |
| 맵 (P6) | 160 m 높이 격자 지형, POI, 건물, Loot·투입 지점 | [Map](Map.md) | `Sh/Simulation/GameMap.cs`, `HeightField.cs` | `GameMapTests`, `HeightFieldTests` |
| 이동·공중 투입 (P12) | 이동 모드(지상·웅크리기·슬라이드·Vault·낙하·글라이드·수송기·기절), 기력, 수송기 투입, 낙하 피해, 문 | [Movement](Movement.md) | `Sh/Simulation/MovementSimulation.cs`, `MovementTuning.cs`, `DropTransport.cs`, `S/Game/Doors.cs` | `MovementModesTests`, `VaultTests`, `DoorTests`, QA `Movement/` |
| 채집·건설 (P13) | 자원 채집, 5 m 격자에 벽·바닥·경사로·지붕, 구조 지지·연쇄 붕괴, 건설 전용 채널, 관심 영역 | [Building](Building.md) | `S/Game/Build/*`, `S/Game/Harvest/*`, `Sh/Simulation/BuildGrid.cs` | `BuildStressTests`, QA `Building/`, suite `building`, `stress-building` |
| 건설 편집 (P13.5) | 확정 조각 편집(구멍, 회전), 편집 모양으로 충돌·사격, 예측 후 거절이면 되돌림 | [Building](Building.md) "편집" | `Sh/Simulation/BuildEdit.cs`, `C/Game/Build/BuildEditController.cs` | `BuildEditTests`, QA `building_edit_*` |
| 분대·기절·소생·Reboot (P14) | 참가 순서로 팀 구성, 기절(이동 모드 7), E를 눌러 소생, Reboot 카드·스테이션, 팀 단위 순위 | [Squad](Squad.md) | `S/Game/Match.Squad.cs`, `Sh/Simulation/RebootStations.cs`, `C/Game/SquadHud.cs` | `SquadMatchTests`, suite `squad` |
| 미니맵·지도·Ping (P15) | 미니맵, 전체 지도(M), Waypoint, 상황별 Ping(적 Ping은 서버가 시선 검사) | [Map](Map.md) "지도 UI·Ping", [Client](Client.md) | `S/Game/Match.Map.cs`, `C/Game/Map/*` | `MapMatchTests`, `MapProjectionTests`, suite `map` |
| Loot 상자·보급 (P16) | 맵 상자(즉시 열기), 보급 상자 낙하(다음 원 안), 바닥 Loot와 분리된 시드 | [Loot](Loot.md) | `S/Game/Match.Loot.cs`, `Sh/Simulation/LootContainers.cs` | `LootContainerMatchTests`, `FloorLootRegressionTests`, suite `loot` |
| 무기 확장·투척 (P17) | 무기 6종(산탄총·권총·로켓 추가), 서버 결정 탄 퍼짐, 반동, 수류탄·로켓 투사체, 폭발 | [Weapons](Weapons.md) | `S/Game/Combat/WeaponSpread.cs`, `Projectiles.cs`, `S/Game/Match.Projectiles.cs` | `ProjectilePhase17Tests`, `ProjectilePacketTests`, suite `weapons` |
| 오디오 (P18) | 에셋 없는 합성 사운드, 우선순위 Mixer(24 Voice), 발소리 Client 생성, `WorldSound` 패킷 | [Audio](Audio.md) | `C/Game/Audio/*` | `AudioMixerModelTests`, QA `Audio/` |
| 차량 (P19) | 2인승 차량, Shared 차량 Simulation(운전자 예측), 충돌 정지와 충격 피해, 관심 거리 120 m | [Vehicles](Vehicles.md) | `Sh/Simulation/VehicleSimulation.cs`, `S/Game/Match.Vehicles.cs`, `C/Game/VehiclePredictor.cs` | `VehicleSimulationTests`, suite `vehicle` |
| 게임 UI (P11) | 타이틀·메뉴·끊김·결과·전적 화면, 한글 이름, Kill Feed, HUD | [Client](Client.md) | `C/UI/UiFlow.cs`, `C/UI/*` | `UiFlowTests`, QA `UI/` |

## 도구

| 기능 | 내용 | 문서 | 주요 코드 | 검증 |
|---|---|---|---|---|
| Headless 봇 (P7) | 실제 UDP로 접속하는 봇. 투입·Loot·교전·Zone·건설·분대 동작 | [Bots](Bots.md) | `Server/src/ProjectH.Bots/*` | `BotBrainTests`, `BotIntegrationTests` |
| 부하 측정 | 봇 인원별 Tick·대역폭·GC, 건설 스트레스 | [LoadTest](LoadTest.md), [QA](QA.md) "Stress" | `QA/Scenarios/Stress/` | suite `stress-*` |
| QA 시나리오 도구 (QA-1–5) | JSON 시나리오로 서버를 띄우고 Actor를 조종해 서버 상태를 검증한다. 장애 주입, Unity Client 자동화·스크린샷, 반복·Seed Sweep·Baseline, Web UI | [QA](QA.md) | `Server/src/ProjectH.QA/`, `S/Qa/`, `C/Qa/` | `ProjectH.QA.Tests`, suite `pre-push`, `full-regression` |
| 진단·관측 | Stats·Health 로그 10초마다, Meter(`dotnet-counters`), F1 디버그 줄 | [Server](Server.md) "관측" | `S/Diagnostics/*`, `C/UI/DebugOverlay.cs` | `DiagnosticsTests`, `MonitoringTests` |
| Monitoring Server | 별도 프로세스 웹 대시보드. 게임 서버가 5초마다 Snapshot을 보낸다(슬롯 1개, 실패하면 버림). **`monitoring` 브랜치, main 미병합** | `monitoring` 브랜치의 `Docs/Monitoring.md` | `Server/src/ProjectH.Monitoring` (그 브랜치) | `ProjectH.Monitoring.Tests` (그 브랜치) |

## 아직 없는 것

- 실제 계정 인증(이름은 아직 자기 신고값이다)
- Client 프레임 측정 값([ClientPerf](ClientPerf.md)에 절차만 있다)
- 공개 서버 전에 막아야 할 보안 항목 일부([ServerTestPlan](ServerTestPlan.md))
- 건설 편집의 팀 공유
- Monitoring의 main 병합
