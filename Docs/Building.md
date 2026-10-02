# Building

Phase 13 Harvesting & Building 기준. 설계 근거와 결정 D1–D20: `Docs/specs/2026-10-02-phase13-harvesting-building-design.md`, 구현 계획(Spec과 다른 점): `Docs/plans/2026-10-02-phase13-harvesting-building.md`. 채집으로 자원(나무·돌·금속)을 얻고, 그 자원으로 벽·바닥·경사로·지붕을 격자에 짓는다. 모든 결과는 서버가 정한다. Client는 미리보기와 대기 중 표시만 하고, 충돌은 서버가 확정한 구조물로만 예측한다.

## 구조

```mermaid
flowchart LR
    subgraph Client[Unity Client]
        Keys[InputReader: Q F Z X V B T R] --> Tools[ToolState]
        Keys --> Ctl[BuildController: BuildTargeting, TurboGate]
        Store[BuildStore + PieceGrid] --> Ctl
        Store --> Pred[LocalPlayerPredictor: CollisionWorld]
        Store --> Views[BuildPieceViews]
        Ctl --> Ghost[BuildPreview]
    end
    subgraph Shared[/Shared/]
        Grid[BuildGrid, PieceGrid, Slope]
        Coll[CollisionWorld, MovementSimulation]
        Pk[BuildPackets, HarvestPackets]
    end
    subgraph Server[Game Loop thread]
        Q[BuildRequestQueue per player] --> Rules[BuildRules, BuildSupport]
        Rules --> World[BuildWorld + PieceGrid]
        World --> Rep[BuildReplication: events, interest, sync]
        Harvest[HarvestWorld, HarvestRules] --> Inv[Inventory resources]
        Trace[PieceTrace] --> World
    end
    Ctl -->|BuildRequest, channel 1| Q
    Rep -->|BuildResult, BuildEvents, BuildSync, BuildInterest, channel 1| Store
```

| 위치 | 파일 | 역할 |
|---|---|---|
| Shared `Simulation` | `BuildGrid` | 격자 상수, 조각 종류·재료·슬롯, 정규화(`TryNormalize`), 슬롯 키, 상자(`BoxOf`)·경사면(`SlopeOf`) |
| | `PieceGrid` | 조각 id → 칸, 칸(Column)마다 id 순서 목록. 서버와 Client가 같은 공간 색인을 쓴다 |
| | `CollisionWorld` | 이동 한 번의 충돌 후보(맵 상자, 닫힌 문, 서 있는 채집 대상, 가까운 조각) |
| | `GameMap.Harvestables` | 채집 대상 41개(나무 23, 바위 8, 잔해 6, 상자 4) |
| Shared `Protocol` | `BuildPackets`, `HarvestPackets` | v11 패킷(아래 "네트워크") |
| Server `Game/Build` | `BuildingCatalog`(`building.json`), `BuildWorld`, `BuildRules`, `BuildSupport`, `BuildReplication`, `BuildRequestQueue`, `PieceTrace` | 수치, 저장, 배치 검사, 지지, 복제, 요청 큐, 사격·채집 광선 |
| Server `Game/Harvest` | `HarvestWorld`, `HarvestRules` | 채집 대상 상태(체력·약점), 도구 전환, 휘두르기 판정 |
| Client `Game/Build` | `ToolState`, `BuildTargeting`, `BuildStore`, `BuildController`, `BuildPieceLook`, `PieceMeshes`, `BuildPieceViews`, `BuildPreview`, `HarvestEffects`, `BuildHud`, `BuildAudio` | 도구 예측, 미리보기 계산, 확정 조각 저장, 요청·대기, 표시 |
| Bots | `BotBuilder` | 방어 벽, 높은 적을 향한 경사로, `--build-spam`(부하) |

모든 건설 상태는 Game Loop 스레드 하나가 쓴다(단일 Writer). Lock이 없다. 요청은 수신 스레드가 `InboundChannels.Build`(유한 채널)에 넣고 Game Loop가 Tick마다 꺼낸다.

## 격자 (`BuildGrid`)

- 칸 5 m × 5 m, 층 3 m. 원점은 맵 모서리(−80, −80)이고 32 × 32칸, 16층이다(맵 160 m 전체).
- 슬롯(`BuildSlotKind`): 칸마다 남쪽 벽, 서쪽 벽, 바닥, 경사로, 지붕. 북쪽·동쪽 벽은 옆 칸의 남쪽·서쪽 벽으로 정규화한다(회전 2 → z+1·회전 0, 회전 3 → x+1·회전 1). 그래서 한 벽 자리에 키가 하나다(`SlotKey` = x | z≪6 | y≪12 | slot≪16).
- 조각 모양:
  - 벽: 칸 가장자리에 서는 0.25 m 두께 상자, 높이 3 m.
  - 바닥: 윗면이 층 높이인 0.25 m 두께 판.
  - 경사로: 낮은 가장자리(층 높이)에서 높은 가장자리(+3 m)까지 칸을 가로지르는 평면, 아래로 0.25 m 판. 회전이 오르는 방향이다(0 +Z, 1 +X, 2 −Z, 3 −X).
  - 지붕: 다음 층 높이(처마)에서 1.5 m 올라가는 사각뿔, 아래로 0.25 m 판(평평한 천장).
- 경사면(`Slope`)은 이동에서 지형처럼 다룬다. 발밑 높이는 발자국 범위의 가장 높은 표면이고 양 끝 높이가 정확하다(경사로 3/5 기울기를 끝점에서 정확히 계산한다). 판은 아래에서 막는다.

## 채집

- 도구(`ToolKind`): 무기(1–3), 채집(F), 건축(Q). Q를 다시 누르면 이전 도구로 돌아간다. 키가 같이 눌리면 무기 키, F, Q 순서다. 도구는 Snapshot의 Self(무기 칸 바이트의 위 2비트)와 Entity `Flags` bit6–7에 실린다. Client는 같은 규칙(`ToolState.Select`)으로 예측하고 서버 테스트가 두 규칙을 비교한다.
- 휘두르기: 채집 도구로 Fire. 간격 0.4초, 사거리 2.5 m. 서버가 눈에서 조준 방향으로 광선을 쏴 가장 가까운 것(맵 상자·닫힌 문·채집 대상·조각)을 맞힌다.
- 채집 대상(`building.json` `harvestables`): 나무 체력 150·한 번에 6, 상자 100·6, 바위 200·5, 잔해 250·4, 부술 때 추가 10. 피해는 25(`environmentDamage`). 나무와 상자는 나무, 바위는 돌, 잔해는 금속을 준다.
- 약점: 첫 타격이 휘두른 쪽 면에 약점을 만든다. 반지름 0.4 m 안을 치면 피해와 자원이 2배이고 약점이 옮겨 간다. 위치는 (id, 타격 수)의 정수 해시라 재현된다. 위치는 `HarvestHit`으로 휘두른 사람에게만 간다.
- 부서진 대상은 충돌·사격에서 빠지고(`HarvestStates` 비트 마스크) 다음 경기·판에 다시 선다.

## 자원

- 재료마다 최대 500(`maxResource`). 넘는 양은 버린다. 인벤토리(`Inventory.Resource`)에 있고 바뀐 Tick 끝에 `ResourcesState`(7 B)로 본인에게 간다.
- 죽으면 자원이 Material 월드 아이템으로 떨어진다(`ItemKind.Material`). Material은 E 없이 1.5 m 안에 들어오면 3 Tick마다 자동으로 줍는다. E 줍기 대상(`FindNearest`)에서는 빠진다.
- `--Server:BuildInfiniteResources=true`면 비용이 0이다(부하 테스트용).

## 배치

```mermaid
flowchart LR
    Preview[Client Preview: BuildTargeting + Judge] --> Req[BuildRequest seq, piece, material, x y z, rotation]
    Req --> Queue[Server: per-player queue, 8]
    Queue --> Val[Server Validation: state, normalize, budget, occupied, reach, blocked, support]
    Val -->|refused| Res[BuildResult code]
    Val --> Pay[Resource Consume]
    Pay --> World[BuildWorld.Add + BuildSupport.Add]
    World --> Placed[BuildEvents Placed at tick end]
    Placed --> Interested[Clients whose interest window holds the cell]
    Pay --> Res
```

- 미리보기(`BuildTargeting`, 물리 질의 없음): 발이 있는 칸과 시선 방향(Yaw의 가장 가까운 축)의 다음 칸. 수평(±35°)이면 벽은 그 사이 가장자리, 바닥·경사로·지붕은 다음 칸. 아래를 보면(피치 35° 이상) 내 칸, 위를 보면(피치 -20° 이하, 카메라 한계 -30° 안쪽) 한 층 위. 층은 발 + 1.5 m로 정해 경사로 중간을 넘으면 다음 층을 겨눈다(경사로 달리기). R은 벽(내 칸 둘레)과 경사로(제자리)를 90°씩 돌린다.
- 미리보기 색(`BuildPreview`, 충돌 없는 반투명): 파랑 가능, 빨강 불가(사거리 밖, 이미 있음, 대기 중인 자리), 주황 자원 부족. 대기 중인 배치는 흰색으로 보인다(최대 8개, 서버 큐와 같다). 받아들여진 배치는 확정 조각이 도착할 때까지(또는 시간 초과까지) 대기로 계속 보이고, 거절되면 바로 사라진다.
- Turbo(`TurboGate`): 누르는 순간 한 번, 누르고 있으면 `minimumBuildInterval`(0.1초)마다 후보를 다시 보고 마지막으로 보낸 자리와 다를 때만 보낸다. 가만히 누르고 있으면 같은 자리를 다시 보내지 않는다.
- Client는 요청을 초당 20개(`BuildController.MaxRequestsPerSecond`, 서버 상한과 같다)까지만 보낸다.
- 요청 번호는 연결마다 1부터 시작하는 u16이다. 서버는 이미 본 번호 이하(감김 비교)를 다시 처리하지 않는다(중복 요청 = 자원 이중 차감 없음).
- 서버 처리: 이동보다 먼저(그 Tick의 입력 위치·조준으로), 플레이어마다 Tick당 최대 1개를 받아들인다. 큐가 가득 차면 `RateLimited`로 답하고, 이렇게 버린 요청도 `requests`와 `rateLimited`에 센다. 수신 스레드에서 Game Loop로 가는 유한 채널(`InboundChannels.Build`)이 넘쳐 버린 요청은 Health 줄 `buildInboxDrops`와 Meter `projecth.build.inbox_drops`로 센다. 받아들인 조각의 생성 Tick은 다음 Tick이다.

## 검증 (`Match.TryBuild`)

순서대로 보고 처음 걸린 이유로 답한다(`BuildResultCode`).

| 순서 | 검사 | 코드 |
|---|---|---|
| 1 | 살아 있고 경기가 끝나지 않았다. 도구가 건축이고 지상 모드(행동 가능)다. 관전자·죽은 사람은 여기서 막힌다 | `InvalidState` 7 |
| 2 | 재료 0–2, 조각 0–3, 회전 0–3, 칸·층이 격자 안 | `InvalidRequest` 8 |
| 3 | 경기 상한 20,000개, 플레이어 상한 500개 | `BudgetFull` 9 |
| 4 | 같은 슬롯에 조각이 있다. 바닥과 그 아래층 지붕은 판을 같이 써서 함께 있을 수 없다 | `Occupied` 5 |
| 5 | 눈에서 7 m(조각 크기의 절반을 더한 거리) 안이고 조준에서 75° 안 | `OutOfRange` 2 |
| 6 | 지형에 묻힘, 눈과 조각 사이에 벽(맵 상자·닫힌 문·지형), 맵 상자·문·서 있는 채집 대상과 너무 겹침(축마다 min(0.3 m, 조각 반 크기) 넘게), 살아 있는 캐릭터의 몸 중심을 벽·바닥이 가름 | `Blocked` 3 |
| 7 | 땅이나 맵 상자 위에 있거나 이웃 조각이 있다 | `Unsupported` 4 |
| 8 | 자원이 비용(재료마다 10) 이상 | `NoResource` 1 |

통과하면 자원을 빼고 저장한다. 실패하면 아무것도 바뀌지 않는다. Client가 보낸 좌표는 격자 번호뿐이고 서버가 모양을 만든다.

## 체력과 파괴

- 재료(`building.json` `materials`): 나무 최대 150·건설 1.5초, 돌 240·3초, 금속 360·5초. 처음 체력은 최대의 30 %. 체력 = 처음 + (최대 − 처음) × 진행도 − 받은 피해이고(표시 단계는 지금까지 자란 체력에 대한 피해 비율이라, 피해 없는 짓는 중 조각은 Healthy로 보인다), 진행도와 체력은 저장하지 않고 필요할 때 계산한다(매 Tick 전체 순회가 없다).
- 짓는 중에도 맞는다. 총알은 조각에서 멈춘다(`PieceTrace`: 광선이 지나는 칸만 2D DDA로 걷는다). 무기 피해 그대로, 채집 도구는 50. 조각을 쳐도 자원은 없다.
- 한 Tick에 여러 번 맞으면 Tick 끝에 Health 기록 하나로 보낸다. 체력이 0 이하면 파괴하고 지지를 다시 계산한다.
- 라운드 재시작과 경기 시작에 모두 지운다(`ClearBuilds`). Client는 reset Sync로 모두 버린다.

## 지지 (`BuildSupport`)

```mermaid
flowchart TB
    Terrain[Terrain / map box top] --> Wall[Wall: grounded]
    Wall -->|shares its top edge| Floor[Floor]
    Floor -->|low edge on the floor edge| Ramp[Ramp]
    Wall -->|diagonal under the ramp side| Ramp
    subgraph Collapse[A piece is destroyed]
        D[Destroy piece] --> N[Each former neighbour]
        N --> B[BFS through shared lattice edges]
        B -->|reaches a grounded piece or a node already proved supported| Keep[Stays]
        B -->|component has no grounded piece| Fall[Whole component destroyed in the same tick]
    end
```

- 연결: 두 조각이 격자 모서리(칸 꼭짓점을 층마다 이은 선분)를 같이 쓰면 이웃이다. 벽은 네 변과 두 대각선, 바닥·지붕은 네 변, 경사로는 낮은·높은 가장자리와 두 옆 경사선. 그래서 벽 위에 바닥이 얹히고, 벽끼리 모서리에서 만나고 쌓이며, 경사로는 바닥 가장자리에 닿고, 경사로 옆면은 옆 벽의 대각선 위에 놓인다.
- 접지: 조각 바닥이 지형이나 맵 상자 윗면에서 0.5 m 안이다(채집 대상은 부서지므로 아니다). 조각이 있는 동안 바뀌지 않는다.
- 배치는 접지되었거나 이웃이 있어야 한다.
- 파괴 뒤: 옛 이웃마다 연결 요소를 너비 우선으로 찾는다. 앞선 탐색이 이미 지지된다고 밝힌 노드에 닿으면 바로 멈춘다(검색마다 표식). 접지 조각이 없는 요소는 같은 Tick에 모두 무너진다. 비용은 무너진 조각과 닿은 모서리 수에 비례한다(전체 맵 순회가 없다, O(N²) 없음). 모서리 색인(모서리 → 조각)이 있어 이웃 찾기는 모서리마다 사전 조회 하나다.

## 네트워크

Protocol v11. 건설 패킷은 채널 1(`ProtocolConstants.BuildChannel`, ReliableOrdered)로만 다닌다. 플레이어 Snapshot(채널 0, Sequenced)에는 건설을 싣지 않는다.

```mermaid
flowchart LR
    subgraph Ch0[Channel 0: player state]
        Snap[WorldSnapshot: Sequenced, every 2 ticks, 13 B per entity]
        Rel[Reliable events: items, deaths, match, ResourcesState, HarvestHit, HarvestStates]
    end
    subgraph Ch1[Channel 1: build stream, ReliableOrdered]
        Res[BuildResult]
        Ev[BuildEvents: placed, health, destroyed, only on change]
        Int[BuildInterest: window mask]
        Sync[BuildSync: pieces of entered cells, paced]
    end
    Server --> Ch0
    Server --> Ch1
    Ch0 --> Client
    Ch1 --> Client
```

섞지 않는 이유: Snapshot은 최신 하나만 의미가 있어 잃어도 되고(Sequenced) 크기가 플레이어 수에 묶여 있다(패킷당 90명, 1197 B). 건설은 수천 개가 될 수 있고 하나라도 잃으면 상태가 틀어지므로 순서 있는 신뢰 전송이 필요하다. 같은 채널에 두면 큰 Sync가 Snapshot과 이벤트를 막는다(Head-of-line). 채널을 나누면 건설 재전송이 이동·전투를 기다리게 하지 않는다. Player Entity는 13 B 그대로다(도구는 빈 `Flags` 비트).

| Packet | 방향 | 크기 | 내용 |
|---|---|---|---|
| `BuildCatalog` (26) | S→C | 49 B | 재료 3개(비용·최대·처음 체력·건설 Tick), 최대 자원, 사거리, 시야각, 채집 사거리·간격, 최소 간격 Tick, 관심 칸 크기·반지름·여유. Join 때 아이템 카탈로그 뒤 |
| `BuildRequest` (27) | C→S | 9 B | 번호 u16, 조각, 재료, x, y, z, 회전 |
| `BuildResult` (28) | S→C 본인 | 8 B | 번호, 코드, 조각 id |
| `BuildEvents` (29) | S→C | 헤더 8 B + Placed 14 B / Health 6 B / Destroyed 4 B | Tick 끝, 받는 사람의 관심 칸에 든 것만. 1200 B로 나눈다 |
| `BuildSync` (30) | S→C | 헤더 7 B + 조각 16 B × 최대 74 = 1191 B | 새로 들어온 관심 칸의 조각(체력 포함). reset 플래그는 모두 버리라는 뜻 |
| `BuildInterest` (31) | S→C | 9 B | 관심 칸 64비트 마스크 |
| `ResourcesState` (32) | S→C 본인 | 7 B | 나무·돌·금속 |
| `HarvestHit` (33) | S→C 본인 | 18 B | 대상, 남은 체력, 약점 위치, 얻은 양, 플래그 |
| `HarvestStates` (34) | S→C | 9 B | 부서진 채집 대상 마스크 |

- 받는 쪽 규칙(`BuildStore`): 모두 id로 적용한다. 같은 Placed가 두 번 와도 하나다. 모르는 id의 Health·Destroyed는 무시한다. 관심 칸 밖의 조각은 저장하지 않는다. 그래서 순서가 어긋나거나 늦은 이벤트가 사라진 조각을 되살리지 않는다.
- 재접속(Resume)과 늦은 합류: reset Sync → 다음 Tick 끝에 `BuildInterest` → 그 칸들의 Sync. 요청 번호도 1부터 다시 센다.
- 보안: 연결마다 초당 20개(`maxRequestsPerSecond`)를 넘는 요청은 잘못된 패킷 `BuildRate`로 센다(잘못된 패킷이 연결마다 20개 쌓이면 `Kicked`로 끊는다, `BadPacketDisconnectThreshold`). Fuzz 테스트가 새 파서 모두를 거친다.

## 관심 영역

- 관심 칸은 20 m(건설 칸 4 × 4)이고 맵은 8 × 8 = 64칸이라 마스크 하나(u64)다.
- 창: 플레이어가 있는 관심 칸에서 반지름 2(5 × 5칸)를 원한다. 이미 가진 칸은 반지름 3까지 유지한다(여유 1, 경계에서 들락날락하지 않게). 죽은 사람과 관전자는 맵 전체다.
- 창이 바뀐 Tick에만 `BuildInterest`를 보낸다. 새로 들어온 칸은 Sync 대기열에 넣어 Tick마다 최대 4패킷(`MaxSyncPacketsPerTick`)씩 보낸다(칸 → 칸 안 열 → id 순서로 이어 간다). 나간 칸의 조각은 Client가 지운다.
- Client 예측은 관심 창 안의 확정 조각만으로 충돌한다. 이동 한 번의 후보는 발 주변 ±1칸, ±2층의 조각이다(최대 225개, id 순서). 슬롯을 같이 쓰는 조각 때문에 넘치면 id가 낮은 쪽을 남긴다.

## 성능

- 매 Tick 전체 조각 순회가 없다. Tick 비용은 그 Tick의 요청·피해·파괴 수와 플레이어 주변 칸의 조각 수에 비례한다.
- 저장은 칸 색인(`PieceGrid`)과 슬롯 배열이고 필요할 때 두 배로 늘린다(처음 256, 경기 상한까지). 빈 경기가 큰 배열을 미리 잡지 않는다.
- 붕괴는 연결 요소에 비례한다(위 "지지").
- 이벤트는 Tick 끝에 모아 받는 사람마다 관심 칸으로 걸러 보낸다. Health는 Tick당 조각마다 하나로 합친다.
- Client: 바뀐 조각만 다시 그린다(`BuildStore.Changed`), 짓는 중인 조각만 프레임마다 높이를 바꾼다. 조각 뷰는 종류별 풀(최대 256)과 공유 Mesh 3개·Material 9개(재료 3 × 손상 3단계)를 쓴다. 미리보기는 고정된 유령 4개와 대기 8개를 재사용한다(매 프레임 Instantiate 없음).
- 측정: `LoadTest.md` "Phase 13 확인"(시나리오 A–D, 조각 수 단계, 100명). 스트레스 테스트는 환경 변수가 있을 때만 돈다:

```bash
PROJECTH_BUILD_STRESS=1 dotnet test Server/ProjectH.Server.slnx -c Release --filter "FullyQualifiedName~BuildStressTests" --logger "console;verbosity=detailed"
```

## 테스트

- Shared: `BuildGridTests`, `CollisionWorldTests`, `PieceCollisionTests`(맞닿은 조각 사이 이동, 무작위 1000건 이상), `BuildPacketTests`, `HarvestPacketTests`, `HarvestableMapTests`
- Server: `HarvestTests`, `ToolTests`(Client 복사본 비교 포함), `BuildPlacementTests`, `StructureDamageTests`, `SupportTests`, `BuildReplicationTests`(서버가 보낸 실제 바이트를 Client의 `BuildStore`에 넣어 같은 조각·관심 칸·자리 판정을 갖는지 본다), `BuildIntegrationTests`(실제 UDP 채널 1, 속도 제한), `MaterialItemTests`, `BuildingCatalogTests`, `BuildStressTests`(환경 변수), `UiTextBuildTests`
- Bots: `BotBuilderTests`
- Client EditMode: `ToolStateTests`, `BuildTargetingTests`(격자 미리보기, Turbo), `BuildStoreTests`(중복, 늦은 이벤트, 관심 칸 나감·들어옴, reset), `BuildControllerTests`(대기, 시간 초과, 번호), `BuildPieceLookTests`, `MovementPredictionTests`(조각 위 예측 일치)
