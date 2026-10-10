# Architecture

Phase 19 + 리뷰 수정 A–D 기준(2026-10-09, Protocol v19).

- **설계 근거:** Phase별 `Docs/specs/*-design.md`의 결정 표(D번호).
- **기능별 문서 지도:** [Features.md](Features.md)
- **증상별로 볼 곳:** [Troubleshooting.md](Troubleshooting.md)

```mermaid
flowchart LR
    subgraph Client[Unity Client]
        Input[InputReader] --> Predictor[LocalPlayerPredictor + PredictedDoors]
        Input --> VehP[VehiclePredictor]
        Input --> Camera[ShoulderCamera, RecoilKick]
        Net[NetClient + AuthPacketLayer] --> Remote[RemotePlayers]
        Net --> Predictor
        Net --> Hud[CombatHud, WeaponState, InventoryHud, SquadHud]
        Net --> World[WorldItemViews, ContainerViews, SupplyDropViews, ProjectileViews, VehicleViews]
        Net --> BuildC[BuildStore, BuildController, BuildEditController, BuildPieceViews]
        Net --> MapC[MapSystem: MapHud, WorldMarkers, PingInput]
        Net --> Audio[GameAudio: AudioSynth, AudioMixerModel, FootstepModel]
        BuildC --> Predictor
        UI[UiRoot: UiFlow, screens, KillFeed] --> Game[GameClient]
        Net --> UI
        Qa[Qa: QaCommandReceiver, editor/dev only]
    end
    subgraph Shared[/Shared UPM package/]
        Protocol[Protocol: packets per feature, SessionAuth, ProtocolLimits, DisconnectCode]
        Sim[Simulation: MovementSimulation, VehicleSimulation, CollisionWorld, BuildGrid/BuildEdit, GameMap + map data]
    end
    subgraph Server[.NET 10 Server]
        Listener[NetworkListener: cookie, per-IP cap, auth tail] -->|Channels| Loop[GameLoop thread]
        Loop --> Match[Match + partials: Squad, Map, Loot, Projectiles, Vehicles]
        Match --> Systems[Combat, Items, Build, Harvest, Flow, Zone, Doors]
        Match -->|MatchRecord| History[MatchHistoryQueue] --> Writer[MatchHistoryWriter] --> MySQL[(MySQL)]
        Listener -->|StatsQuery| StatsQ[StatsQueryQueue] --> StatsSvc[StatsQueryService] --> MySQL
        Loop --> Health[HealthCounters] --> Meter[ServerMeter]
        Watchdog[StallWatchdog] -.reads.-> Loop
        QaS[Qa: HTTP 127.0.0.1, QA mode only] -->|bounded queue| Loop
    end
    Bots[ProjectH.Bots]
    QaTool[ProjectH.QA: scenarios, Web UI]
    Client <-->|UDP, ch0 state, ch1 build| Server
    Bots <-->|UDP| Server
    QaTool -->|uses| Bots
    QaTool -->|HTTP| QaS
    Client -.-> Shared
    Server -.-> Shared
    Bots -.-> Shared
```

## 원칙

| 원칙 | 어디서 지키나 |
|---|---|
| Client는 입력만 보낸다. 결과는 서버가 정한다 | 이동·차량은 Shared Simulation을 양쪽에서 같은 입력으로 돌린다. 명중·피해·Loot·건설·분대 규칙은 서버에만 있다 |
| Shared는 예측에 필요한 계산과 맵 배치 상수만 둔다 | 범위는 `game-core-rules` 4절 예외 1–8에 정해 둔다. 규칙의 Client 복사본(`DoorRule`, `ContainerRule`, `SquadPrompt`, `VehiclePrompt`, `ToolState`)은 서버 비교 테스트로 묶는다 |
| 경기 상태는 Game Loop 스레드 하나가 소유한다 | 다른 스레드는 크기가 정해진 Channel이나 큐로만 넘긴다(수신, DB, 전적, QA). Lock은 `SessionKeys._sendLock` 하나(leaf)뿐이다 |
| Game Loop는 아무것도 기다리지 않는다 | DB 저장·전적 조회·QA 명령은 별도 서비스가 처리한다 |
| 모든 Collection에 상한이 있다 | 플레이어 100, 월드 아이템 256, 투사체 32(소유자당 4), 차량 8, Ping(사람당 3, 팀당 8), 큐 크기는 `Server.md` "Queue" |
| 함수마다 기능·입력·출력 주석이 있고 코드와 어긋나지 않는다 | 메서드·생성자·Handler·로직 있는 local function 위 세 줄(`.claude/skills/code-comments`). 2026-10-10 전체 일괄 적용(8c5bdef). 제외: record 선언, 프로퍼티, 한 줄 local function, 테스트 메서드 |

## 코드 배치

```text
Server/
  src/ProjectH.Server/        Dedicated Server (GameLoop, ServerHost, GameServerService, ServerOptions)
    Net/                      NetworkListener, InboundChannels/Messages, PeerState,
                              ConnectCookie, ConnectRateLimiter, ServerIdentity, AuthPacketLayer (리뷰 A·B)
    Game/                     Match (+ Match.Squad/.Map/.Loot/.Projectiles/.Vehicles), PlayerEntity,
                              PlayerInputBuffer, Doors, MovementLimits, GameData/DataJson
      Build/ Harvest/         건설·편집·지지·복제 / 채집 (Phase 13, 13.5)
      Combat/                 HitScan, PositionHistory, WeaponCatalog/Rules/Spread, Projectiles (Phase 3, 17)
      Items/ Loot/            인벤토리·월드 아이템·Loot 표 / 상자·보급 (Phase 4, 16)
      Flow/ Zone/             MatchFlow, DropPlanner / SafeZone (Phase 5, 12)
      Squad/ Map/ Vehicles/   SquadCatalog (14) / MapCatalog: Ping (15) / Vehicle, VehicleCatalog (19)
    Persistence/              MatchHistory*, MatchStore, StatsQuery* (Phase 9, 11, 리뷰 C)
    Diagnostics/              HealthCounters, ServerMeter, ServerStats, StallWatchdog, TickMetrics
    Qa/                       QA 모드 HTTP 제어 (QA-1)
    keys/                     개발용 서버 키 (리뷰 B, 운영에서는 거부)
    *.json                    weapons, items, loot, zones, building, squad, map, vehicles (시작 때 검증)
  src/ProjectH.Bots/          Headless 봇 Client
  src/ProjectH.QA/            QA 시나리오 실행기 + Web UI (봇 코드 재사용)
  src/ProjectH.Shared/        Shared 소스를 netstandard2.1로 컴파일하는 csproj
  tests/ProjectH.Server.Tests Game, Shared, Integration, Net, Bots, ClientUi, Qa, Persistence, Diagnostics
  tests/ProjectH.QA.Tests     QA 도구 테스트
Shared/Runtime/
  Protocol/                   기능별 패킷(Build, Combat, Harvest, Item, Loot, Map, Match, Projectile, Squad,
                              Stats, Traversal, Vehicle), PacketReader/Writer, SessionAuth, ProtocolLimits
  Simulation/                 MovementSimulation/Mode/Tuning, MoveState/MoveSettings, VehicleSimulation/Settings, CollisionWorld,
                              BuildGrid/PieceGrid/BuildEdit, GameMap, HeightField,
                              맵 배치(LootPoints, LootContainers, DropPoints, MapPois, RebootStations, VehicleSpawns)
Client/Assets/Scripts/
  Bootstrap/ Camera/ Input/ Net/ UI/ Qa/
  Game/                       예측·보간·HUD·무기·Loot·분대·차량·Zone·관전
    Audio/ Build/ Map/        합성 오디오 (18) / 건설·편집 (13, 13.5) / 지도·Ping (15)
Client/Assets/Tests/EditMode  EditMode 테스트
QA/                           Scenarios/<분류>/*.json, Suites/*.json
```

Monitoring Server(`Server/src/ProjectH.Monitoring`)는 `monitoring` 브랜치에 있고 아직 main에 합치지 않았다.

## Shared 소비 방식

- `Shared/`는 Unity UPM 패키지(`com.projecth.shared`, `Shared/package.json`)다.
  - Client는 `Packages/manifest.json`의 `file:../../Shared`로 참조한다.
  - asmdef `ProjectH.Shared`(`noEngineReferences`)로 컴파일된다.
- Server는 같은 소스(`Shared/Runtime/**/*.cs`)를 `Server/src/ProjectH.Shared/ProjectH.Shared.csproj`로 컴파일한다.
  - `netstandard2.1`, `LangVersion 9.0`이라 Unity 6에서 안 되는 문법·API는 서버 빌드에서 먼저 실패한다.
- Shared에는 엔진 타입이 없다. 위치는 `System.Numerics.Vector3`이고, Client는 `VectorConversions`로 Unity 타입과 변환한다.
- 새 `.cs`에는 Unity `.meta`를 함께 커밋한다.

## LiteNetLib

- **Server:** NuGet `LiteNetLib` 2.1.4.
- **Client:** 같은 버전(2.1.4, netstandard2.1)의 DLL을 `Client/Assets/Plugins/LiteNetLib/LiteNetLib.dll`에 둔다. Git UPM 패키지는 `.meta` 파일이 없어 Unity가 무시하므로 쓰지 않는다.
- **채널 2개:** 0은 상태다(Snapshot Sequenced, 이벤트 Reliable, `VehicleStates` Unreliable). 1은 건설 전용 ReliableOrdered다.
- **데이터그램 인증(리뷰 B):** 모든 데이터그램 끝에 20 B 인증 꼬리(카운터 + HMAC)를 붙인다. 연결 요청에는 쿠키와 세션 키가 들어간다(`Networking.md`, `Server.md`).

## 영속 데이터

- 경기가 끝나면 Game Loop가 `MatchRecord` 하나를 큐에 넣는다. 별도 async Writer가 한 Transaction으로 MySQL에 저장한다.
- Game Loop는 DB를 기다리지 않는다. DB가 없어도 서버는 돈다.
- 스키마는 `schema_version` v2다. 리뷰 C에서 anti-cheat 기록 열이 추가됐다(`Database.md`).
- 전적은 Client가 요청하면 `StatsQueryService`가 읽어 답한다. Game Loop는 답을 보내기만 한다.
