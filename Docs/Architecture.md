# Architecture

Phase 7 Bots 기준. 설계 근거: `Docs/specs/2026-09-30-phase0-network-sync-design.md`, `Docs/specs/2026-09-30-phase1-character-prototype-design.md`, `Docs/specs/2026-10-01-phase3-combat-design.md`, `Docs/specs/2026-10-01-phase4-inventory-loot-design.md`, `Docs/specs/2026-10-01-phase5-battle-royale-design.md`, `Docs/specs/2026-10-01-phase6-map-design.md`, `Docs/specs/2026-10-01-phase7-bots-design.md`.

```mermaid
flowchart LR
    subgraph Client[Unity Client]
        Input[InputReader] --> Predictor[LocalPlayerPredictor]
        Input --> Camera[ShoulderCamera]
        Net[NetClient] --> Remote[RemotePlayers]
        Net --> Predictor
        Net --> Hud[CombatHud, WeaponState]
        Net --> Items[WorldItemViews, InventoryHud]
        Net --> MatchC[MatchHud, ZoneView, SpectatorCamera, PoiLabel]
        World[MapWorld: terrain mesh, boxes]
    end
    subgraph Shared[/Shared UPM package/]
        Protocol[Protocol: packets]
        Sim[Simulation: MovementSimulation, HeightField, GameMap, LootPoints, DropPoints, MapPois]
    end
    subgraph Server[.NET 10 Server]
        Listener[NetworkListener] -->|Channels| Loop[GameLoop thread]
        Loop --> Match
        Match --> Combat[Combat: WeaponRules, HitScan, PositionHistory]
        Match --> ItemsS[Items: Inventory, WorldItems, LootSpawner]
        Match --> Flow[Flow: MatchFlow / Zone: SafeZone]
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
| `Client/` | Unity. 입력·표시·예측·보간. 결과를 확정하지 않는다. 카메라·조준점은 Client 표시 전용이고, 발사는 입력에 조준 방향만 실어 보낸다(누구를 맞혔는지는 보내지 않는다). Zone 원(`ZoneMath`)과 관전은 표시 전용이고 Shared에 두지 않는다(서버 식과 같은지는 테스트로 고정) |
| `Server/` | .NET 10 Dedicated Server. 이동 결과와 명중·피해·사망·부활, Loot 배치·줍기·버리기·회복, 투입 지점 배정, 지형 사격 판정, 경기 상태·Safe Zone·Zone 피해·순위·승자를 결정하고 인벤토리를 소유한다. 데이터는 `weapons.json`, `items.json`, `loot.json`, `zones.json`. `src/ProjectH.Bots`: 부하·경기 테스트용 Headless 봇 Client(서버를 참조하지 않는다, `Bots.md`) |
| `Shared/` | 패킷 DTO, 프로토콜 상수, 이동 계산과 그 지형(박스, 높이 격자)·충돌(`Simulation/`, 로직 예외: `game-core-rules` 4절), 맵 배치 데이터(`LootPoints`, `DropPoints`, `MapPois`, 좌표·이름 상수만: 4절 예외 2) |
| `Docs/` | 이 문서들(맵 데이터와 규칙은 `Map.md`) |

## Shared 소비 방식

- `Shared/`는 Unity UPM 패키지(`com.projecth.shared`, `Shared/package.json`)다. Client는 `Packages/manifest.json`의 `file:../../Shared`로 참조하고, asmdef `ProjectH.Shared`(`noEngineReferences`)로 컴파일된다.
- Server는 같은 소스(`Shared/Runtime/**/*.cs`)를 `Server/src/ProjectH.Shared/ProjectH.Shared.csproj`로 컴파일한다. `netstandard2.1`, `LangVersion 9.0`이라 Unity 6에서 안 되는 문법·API는 서버 빌드에서 먼저 실패한다.
- Shared에는 엔진 타입이 없다(위치는 `System.Numerics.Vector3`; Client는 `VectorConversions`로 Unity 타입과 변환).

## LiteNetLib

- Server: NuGet `LiteNetLib` 2.1.4.
- Client: 같은 버전(2.1.4, netstandard2.1)의 DLL을 `Client/Assets/Plugins/LiteNetLib/LiteNetLib.dll`에 둔다. Git UPM 패키지는 `.meta` 파일이 없어 Unity가 무시하므로 쓰지 않는다. `manifest.json`과 `ProjectH.Client.asmdef` 참조에 LiteNetLib 항목이 없고, DLL은 자동 참조된다.

DB는 아직 사용하지 않는다(`Database.md`).
