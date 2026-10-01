# Architecture

Phase 12 Deployment & Traversal 기준. 설계 근거: `Docs/specs/2026-09-30-phase0-network-sync-design.md`, `Docs/specs/2026-09-30-phase1-character-prototype-design.md`, `Docs/specs/2026-10-01-phase3-combat-design.md`, `Docs/specs/2026-10-01-phase4-inventory-loot-design.md`, `Docs/specs/2026-10-01-phase5-battle-royale-design.md`, `Docs/specs/2026-10-01-phase6-map-design.md`, `Docs/specs/2026-10-01-phase7-bots-design.md`, `Docs/specs/2026-10-01-phase8-optimization-design.md`(Snapshot 분할·양자화, 부하 재측정), `Docs/specs/2026-10-01-phase9-persistence-design.md`(MySQL 경기 기록·통계), `Docs/specs/2026-10-01-phase10-hardening-design.md`(끊기 코드, 재접속 유예, Timeout, 예외 복구, 관측), `Docs/specs/2026-10-01-phase11-game-ui-design.md`(게임 UI, 전적 조회, 이름), `Docs/specs/2026-10-02-phase12-deployment-traversal-design.md`(공중 투입, 이동 모드, 문, 낙하 피해).

```mermaid
flowchart LR
    subgraph Client[Unity Client]
        Input[InputReader] --> Predictor[LocalPlayerPredictor]
        Predictor --> Doors[PredictedDoors]
        Input --> Camera[ShoulderCamera]
        Net[NetClient] --> Remote[RemotePlayers]
        Net --> Predictor
        Net --> Hud[CombatHud, WeaponState]
        Net --> Items[WorldItemViews, InventoryHud]
        Net --> MatchC[MatchHud, ZoneView, SpectatorCamera, PoiLabel]
        UI[UiRoot: UiFlow, screens, KillFeed] --> Game[GameClient]
        Net --> UI
        World[MapWorld: terrain mesh, boxes]
    end
    subgraph Shared[/Shared UPM package/]
        Protocol[Protocol: packets, DisconnectCode, DisconnectCodes]
        Sim[Simulation: MovementSimulation, MovementMode, MovementTuning, DropTransport, HeightField, GameMap + Doors, LootPoints, DropPoints, MapPois]
    end
    subgraph Server[.NET 10 Server]
        Listener[NetworkListener] -->|Channels| Loop[GameLoop thread]
        Loop --> Match
        Match --> Combat[Combat: WeaponRules, HitScan, PositionHistory]
        Match --> ItemsS[Items: Inventory, WorldItems, LootSpawner]
        Match --> Flow[Flow: MatchFlow, DropPlanner / Zone: SafeZone]
        Match --> DoorsS[DoorSet, DoorRules, MovementLimits]
        Match -->|MatchRecord, TryEnqueue| History[MatchHistoryQueue] --> Writer[MatchHistoryWriter] --> MySQL[(MySQL)]
        Listener -->|StatsQuery| StatsQ[StatsQueryQueue] --> StatsSvc[StatsQueryService] --> MySQL
        StatsSvc -->|StatsReply| StatsQ
        Loop -->|sends replies| StatsQ
        Loop --> Health[HealthCounters] --> Meter[ServerMeter: ProjectH.Server]
        Watchdog[StallWatchdog] -.reads.-> Loop
    end
    Bots[ProjectH.Bots: headless clients]
    Client <-->|UDP / LiteNetLib| Server
    Bots <-->|UDP / LiteNetLib| Server
    Client -.uses.-> Shared
    Bots -.uses.-> Shared
    Server -.uses.-> Shared
```

| 폴더 | 역할 |
|---|---|
| `Client/` | Unity. 입력·표시·예측·보간. 결과를 확정하지 않는다. 카메라·조준점은 Client 표시 전용이고, 발사는 입력에 조준 방향만 실어 보낸다(누구를 맞혔는지는 보내지 않는다). Zone 원(`ZoneMath`)과 관전은 표시 전용이고 Shared에 두지 않는다(서버 식과 같은지는 테스트로 고정). 화면 흐름은 `UiFlow`(순수, 서버 테스트가 소스 링크로 시험한다). Phase 12: 이동 모드·위치는 예측하되 서버가 확정한다. 문은 예측 문(`PredictedDoors`)이 서버의 `DoorStates` 위에 내 예측을 겹쳐 쓰고, 예측 문 규칙(`DoorRule`)은 서버 규칙의 복사본이다. 카메라 목표·원격 자세·수송기 표시는 Client 전용이다 |
| `Server/` | .NET 10 Dedicated Server. 이동 결과와 명중·피해·사망·부활, Loot 배치·줍기·버리기·회복, 투입 지점 배정, 지형 사격 판정, 경기 상태·Safe Zone·Zone 피해·순위·승자를 결정하고 인벤토리를 소유한다. Phase 12: 수송기 경로(`DropPlanner`)와 탑승·강제 뛰어내리기, 문 상태·충돌 세계(`DoorSet`)와 E 규칙(`DoorRules`), 낙하 피해, 모드별 행동 제한, 이동 이상 검사(`MovementLimits`)도 서버가 한다. 데이터는 `weapons.json`, `items.json`, `loot.json`, `zones.json`. `src/ProjectH.Bots`: 부하·경기 테스트용 Headless 봇 Client(서버를 참조하지 않는다, `Bots.md`) |
| `Shared/` | 패킷 DTO, 프로토콜 상수, 전적 패킷(`StatsPackets`), 끊는 이유 코드(`DisconnectCode`)와 재접속 표(`DisconnectCodes`, Client와 봇이 같이 쓴다), 이동 계산과 그 지형(박스, 높이 격자)·충돌(`Simulation/`, 로직 예외: `game-core-rules` 4절. Phase 12: 이동 모드 `MovementMode`·수치 `MovementTuning`·수송기 경로와 `Ride`(`DropTransport`)·문 상자(`GameMap.Doors`)), v10 패킷(`TraversalPackets`: `TransportRoute`, `DoorStates`. 그 밖에 Snapshot의 `Flags`·Self 블록, `Crouch` 버튼, `PlayerRespawned.Mode`, `PlayerDied.Cause`), 맵 배치 데이터(`LootPoints`, `DropPoints`, `MapPois`, 좌표·이름 상수만: 4절 예외 2) |
| `Docs/` | 이 문서들(맵 데이터와 규칙은 `Map.md`, 이동 모드와 수치는 `Movement.md`) |

## Shared 소비 방식

- `Shared/`는 Unity UPM 패키지(`com.projecth.shared`, `Shared/package.json`)다. Client는 `Packages/manifest.json`의 `file:../../Shared`로 참조하고, asmdef `ProjectH.Shared`(`noEngineReferences`)로 컴파일된다.
- Server는 같은 소스(`Shared/Runtime/**/*.cs`)를 `Server/src/ProjectH.Shared/ProjectH.Shared.csproj`로 컴파일한다. `netstandard2.1`, `LangVersion 9.0`이라 Unity 6에서 안 되는 문법·API는 서버 빌드에서 먼저 실패한다.
- Shared에는 엔진 타입이 없다(위치는 `System.Numerics.Vector3`; Client는 `VectorConversions`로 Unity 타입과 변환).

## LiteNetLib

- Server: NuGet `LiteNetLib` 2.1.4.
- Client: 같은 버전(2.1.4, netstandard2.1)의 DLL을 `Client/Assets/Plugins/LiteNetLib/LiteNetLib.dll`에 둔다. Git UPM 패키지는 `.meta` 파일이 없어 Unity가 무시하므로 쓰지 않는다. `manifest.json`과 `ProjectH.Client.asmdef` 참조에 LiteNetLib 항목이 없고, DLL은 자동 참조된다.

DB(Phase 9): 경기가 끝나면 Game Loop가 `MatchRecord` 하나를 큐에 넣고, 별도 async Writer가 한 Transaction으로 MySQL에 저장한다. Game Loop는 DB를 기다리지 않고, DB가 없어도 서버는 돈다(`Database.md`).
Phase 11: Client가 요청하면 `StatsQueryService`가 읽어 답한다. Game Loop는 답을 보내기만 한다(`Networking.md` "전적 조회", `Database.md` "조회 경로").
