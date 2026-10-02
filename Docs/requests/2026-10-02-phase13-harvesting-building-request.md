# Phase 13 — Harvesting & Building (사용자 요청서 원문, 2026-10-02 수신)

> Phase 12 완료·푸시 뒤에 시작한다. 아래는 사용자가 보낸 요청서이며, 코드 블록 안의 빈 줄만 줄였다.

현재 프로젝트에 Fortnite의 핵심적인

- 자원 채집
- 환경 파괴
- Build Mode
- Wall / Floor / Ramp / Roof 건설
- 연속 건설
- 구조물 파괴
- 구조 지지
- 건설 네트워크 동기화

플레이 감각을 참고한 독립적인 건설 시스템을 구현한다.

Fortnite의 Asset, 이름, UI, 코드, 그래픽을 복제하지 않는다. 게임 메커니즘과 조작 흐름만 참고한다.

이번 Phase의 가장 중요한 목표는:

```text
전투 중 즉시 건설 가능
빠른 연속 건설
서버 판정
100 Player 대응
기존 Snapshot 성능 유지
구조물이 많아져도 Server Tick 안정성 유지
```

---

# 0. 현재 프로젝트 상태

현재 다음 시스템이 구현되어 있다.

- Network: LiteNetLib UDP, Dedicated Server, Server Authoritative, 30 Hz Server Tick, 15 Hz Player Snapshot, Client Prediction, Reconciliation, Remote Interpolation, Shared Protocol
- Combat: Server Hitscan, Lag Compensation, Damage, Health, Shield, Death, Spectator
- Inventory: Weapon Slot 3, Ammo 3 Type, Consumable, Pickup, Drop, World Item
- Battle Royale: Match Flow, Zone, Elimination, Placement, Kill Count
- Map: Height Grid Terrain, 2 m terrain grid, Buildings, Cover, POI, Loot
- Bot: Headless Bot, 10~50 Player Load Test
- Network Optimization: 현재 Player Entity Snapshot 13 B. 50 Player 기준 Snapshot 최적화 이전보다 송신 대역폭 약 41% 감소. 최대 100 Player를 지원한다.
- Database: MySQL, Game Loop 외부 Writer, Bounded Queue
- Hardening: Reconnect, Timeout, Fuzz Test, Graceful Shutdown, Exception Recovery, Metrics, Watchdog
- 현재 성능: 50 Bots, Server Tick P95 ≈ 0.1 ms. 이 성능 기반을 불필요하게 훼손하지 않는다.

---

# 1. 매우 중요한 아키텍처 원칙

플레이어 건축물을 기존 Player Snapshot에 넣지 않는다.

금지: `15 Hz Player Snapshot + 모든 Build Piece 전체 상태`

건축물은 플레이어와 생명주기가 다르다. 따라서 별도의 `Build State / Build Event Stream`으로 관리한다.

# 2. 건축 네트워크 기본 원칙

건축물은 상태 변화가 있을 때만 전송한다. 예: `BuildPlaced`, `BuildDamaged`, `BuildDestroyed`, `BuildSync`.
매 Snapshot마다 동일한 건축물을 반복 전송하지 않는다. Client Preview는 네트워크로 전송하지 않는다.

# 3. Phase 13 구현 범위

- Harvesting: Harvest Tool, Environment Damage, Wood / Stone / Metal, Resource Yield, Weak Point, Environment Destruction
- Building: Build Mode, Build Preview, Grid Snap, Rotation, Wall, Floor, Ramp, Roof, Material 변경, 빠른 연속 건설, Resource Consumption
- Structure: Structure Health, Construction Progress, Damage, Destruction, Support, Unsupported Collapse
- Network: Server Validation, Build Event Replication, Interest Management, Reconnect Sync
- Performance: Spatial Index, Build Pooling, Structure Budget, Load Test

# 4. 이번 Phase에서 구현하지 않을 것

Building Edit, Window Edit, Door Edit, Floor Edit, Roof Edit, Edit Reset, Edit Confirm, Traps, Repair, Upgrade, Team Ownership, Blueprint Presets, Vehicle Interaction, Advanced Destruction Physics.
건축 Edit는 Phase 13.5에서 별도로 구현한다. Phase 13 구현 중 필요하다는 이유로 미리 만들지 않는다.

# 5. 건설 자원

3종의 건설 자원을 사용한다.

```csharp
enum BuildMaterialType
{
    Wood,
    Stone,
    Metal
}
```

UI 표시 이름은 추후 변경 가능하도록 한다. 코드 곳곳에 문자열을 직접 사용하지 않는다.

# 6. 자원 특성

- Wood: 빠르게 완성, 낮은 내구도, 초반 전투에 유리
- Stone: 중간 건설 속도, 중간 내구도
- Metal: 느린 완성, 높은 내구도, 장기 방어에 유리

정확한 값은 Configuration으로 관리한다.

# 7. Build Material Configuration

예: `BuildMaterialConfig` — Type, ResourceCost, InitialHealth, MaxHealth, ConstructionTime, StructureDamageMultiplier, HarvestToolDamageMultiplier. Magic Number를 코드에 분산시키지 않는다.

# 8. 자원 최대치

각 Resource는 최대치를 가진다(Wood 0~MaxWood, Stone 0~MaxStone, Metal 0~MaxMetal). 정확한 최대치는 Config에서 결정한다. Player마다 자원 수치는 Server가 관리한다. Client가 자원 값을 임의로 변경할 수 없다.

# 9. Harvest Tool

모든 Player는 기본 Harvest Tool을 가진다. 별도의 Inventory Slot을 소비하지 않아도 된다(Weapon Slots 1 2 3 / Harvest Tool 별도). Weapon과 Harvest Tool 전환이 빠르게 가능해야 한다.

# 10. Harvest Tool 역할

Environment Damage, Resource Harvesting, Player Build Damage. 초기에는 Player Damage도 가능하게 만들 수 있으나, 현재 Combat 구조에 자연스럽게 통합할 수 있는 경우에만 추가한다.

# 11. Harvest 공격

`Client Attack → Local Swing Presentation → Harvest Attack Request / Input → Server Raycast → Target Validation → Damage → Resource Reward → Result Replication`. Hit 결과를 Client가 결정하지 않는다.

# 12. Harvest Tool Server Validation

Server가 검사한다: Player Alive, 올바른 Match State, Harvest Tool 사용 가능, Attack Cooldown, Target Range, View Direction, Target 존재, Target Damage 가능, Line Of Sight. Client가 보낸 Object ID만 믿지 않는다. Server가 실제 Hit를 확인한다.

# 13. Harvest Range

Harvest Tool에는 짧은 Range를 사용한다. 정확한 수치는 Config. Server Raycast 또는 현재 Collision System에 적합한 방식으로 판단한다.

# 14. Harvestable Environment

기존 Map Object 중 일부를 Harvest 가능 Object로 변경한다. 예: Tree → Wood, Wood Furniture → Wood, Fence → Wood, Rock → Stone, Concrete Object → Stone, Metal Fence → Metal, Vehicle Wreck → Metal. 모든 Map Object를 파괴 가능하게 만들 필요는 없다.

# 15. Harvestable Object Data

각 Object는 최소한 HarvestableId, ResourceType, MaxHealth, CurrentHealth, ResourceYield, Position, Bounds를 가진다. Server에서는 Unity GameObject와 같은 무거운 객체를 사용하지 않는다.

# 16. Harvestable ID

Map에 존재하는 Harvestable Object는 안정적인 ID를 가진다. 가능하면 Map 생성 과정에서 deterministic한 ID를 부여한다. Server와 Client가 동일한 Object를 식별할 수 있어야 한다.

# 17. Environment Damage

Harvestable Object에 피해가 들어가면 Server에서 HP를 감소시킨다(`Hit → Damage → HP 감소 → Resource 지급`). Object가 파괴되지 않았더라도 Hit마다 일부 Resource를 지급할 수 있다.

# 18. Resource Yield

자원 지급량은 Config로 관리한다. 예: BaseResourcePerHit, DestroyBonus, WeakPointBonus. 한 번의 공격에서 Resource가 Player 최대치를 넘지 않도록 한다.

# 19. Weak Point

Fortnite와 비슷한 채집 감각을 위해 Weak Point 시스템을 구현한다. Harvestable Object를 처음 공격하면 다음 Weak Point를 생성한다. Client에 시각적으로 표시한다. Player가 Weak Point 근처를 맞히면 추가 Object Damage, 추가 Resource를 줄 수 있다.

# 20. Weak Point Authority

Weak Point 위치는 Server가 결정한다. Client가 `WeakPointHit = true`라고 보내는 것을 신뢰하지 않는다. Server가 Hit 위치와 현재 Weak Point 위치를 비교한다.

# 21. Weak Point 위치

복잡한 Mesh Surface Sampling 시스템을 만들지 않는다. Object Bounds 또는 정의된 Hit Region을 이용하여 단순하고 재현 가능한 위치를 생성한다. Server가 Weak Point 위치를 계산한다.

# 22. Environment Destruction

Object HP가 0 이하라면 Destroyed 상태가 된다. Server: Collision 제거, Harvest 대상 제거, Destroyed 상태 기록. Client: Destruction FX, Object Hide / Destroy, Sound.

# 23. 파괴 Physics

환경 Object를 수십 개 Rigidbody 조각으로 분해하지 않는다. 저사양 Client가 최우선이다. 파괴 효과가 필요하면 짧은 Particle, Debris Effect, Animation 정도로 표현한다. Gameplay Collision은 즉시 제거한다.

# 24. Environment Respawn

Battle Royale Match 중 파괴된 환경은 재생성하지 않는다. 새 Match가 시작하면 원래 상태로 초기화한다. DB에 저장하지 않는다.

# 25. Build Mode

Player가 Build Mode로 전환할 수 있게 한다(예: Q). 실제 Key는 기존 Input Binding 시스템을 따른다. Build Mode 진입 시 현재 Weapon을 숨기거나 비활성화한다.

# 26. Build Mode 종료

다음 경우 Weapon Mode로 돌아갈 수 있다: Build Mode Key, Weapon Slot Key, Harvest Tool, 특정 Input. 전환은 즉각적으로 느껴져야 한다.

# 27. Build Piece 종류

이번 Phase에서는 정확히 4종만 구현한다.

```csharp
enum BuildPieceType
{
    Wall,
    Floor,
    Ramp,
    Roof
}
```

새 Piece를 임의로 추가하지 않는다.

# 28–31. Piece 역할

- Wall: 방어, 시야 차단, 적 공격 차단. Grid 한 변을 차지한다.
- Floor: 수평 이동, 발판, 높은 곳 연결, 낙하 방지.
- Ramp: 높이 확보, 이동, 공격 각도 확보. 한 Grid Cell을 기반으로 일정 높이 상승한다.
- Roof: 피라미드 또는 경사진 Roof 형태를 사용할 수 있다. 이번 단계에서는 기본 형태 하나만 지원한다. Edit는 구현하지 않는다.

# 32. Build Grid

건축물은 World Grid에 Snap한다. Terrain의 2m Height Grid와 Build Grid를 반드시 동일하게 만들 필요는 없다. Build 시스템 전용 Grid를 만든다(예: BuildGridSize, BuildGridHeight). Config로 관리한다.

# 33. Build Grid 목표

Grid Piece들이 서로 정확히 연결되어야 한다. Wall/Floor/Ramp/Roof 조합 시 틈이 최소화되어야 한다. Floating Point 위치 누적으로 구조가 어긋나면 안 된다.

# 34. Grid 좌표

Server 내부에서는 가능한 경우 World Position 대신 정수 Grid Coordinate를 사용한다(GridX, GridY, GridZ, Rotation). Rotation은 자유로운 float가 아니라 제한된 방향(0, 90, 180, 270)을 사용한다.

# 35. Build Slot Key

건축물 위치를 빠르게 검색할 수 있는 Key를 만든다. 개념: `BuildSlotKey = Grid Coordinate + Piece Type / Orientation Slot`. 동일 위치에 불가능한 구조물이 중복 배치되지 않도록 한다.

# 36. Build Preview

Build Mode에서는 Client가 현재 배치될 위치를 즉시 Preview한다. Preview는 Client 전용이다. Server에 매 Frame 보내지 않는다.

# 37. Preview 상태

Preview는 최소한 Valid, Invalid, InsufficientResource 상태를 표현한다. Material 또는 투명도를 다르게 표시한다.

# 38. Preview 계산

Client는 Camera / Aim 방향으로 건설 위치를 계산한다: `Camera Ray → Target Surface → Build Grid Snap → Candidate Transform`.

# 39. Preview 성능

매 Frame 대량 Physics Query를 실행하지 않는다. Preview 한 개에 필요한 최소 Query만 수행한다. GC Allocation이 발생하지 않도록 한다.

# 40. Build Rotation

Player는 Build Piece를 회전할 수 있다(예: R). Rotation: 0°, 90°, 180°, 270°. Ramp 방향 전환에 특히 중요하다.

# 41. Build Material 변경

Build Mode에서 Wood, Stone, Metal을 순환할 수 있다. UI에 현재 Material을 명확하게 표시한다.

# 42. Build Piece 변경

Build Mode에서 빠르게 Wall, Floor, Ramp, Roof를 선택할 수 있어야 한다. Key Binding은 별도 Input 설정으로 관리한다.

# 43. Build Request

Client가 실제 배치를 시도할 때만 Server로 요청한다. 개념: BuildRequest — ActionSequence, PieceType, MaterialType, GridCoordinate, Rotation. 불필요한 World float Position을 많이 전송하지 않는다. 현재 Protocol 스타일에 맞춰 압축한다.

# 44. Client를 신뢰하지 않는다

Client가 보낸 GridCoordinate가 유효하다고 가정하지 않는다. Server가 현재 Player 위치와 Aim 및 Build Range를 기준으로 검증한다.

# 45. Build Validation

Server가 최소한 다음을 검증한다: Player Alive, Match Playing, Spectator 아님, Build 가능 상태, Piece Type 유효, Material 유효, 충분한 Resource, Build Cooldown, Build Range, Grid Bounds, Placement Collision, Duplicate Slot, Map Bounds, Support, Illegal State. 하나라도 실패하면 배치를 거부한다.

# 46. Build Range

Player가 멀리 떨어진 위치에 구조물을 생성할 수 없도록 한다. Player Position과 Build Candidate 간 거리를 Server가 검사한다.

# 47. Placement Collision

다음과 심하게 겹치는 구조물은 배치하지 않는다: Terrain 내부, 중요 Static Building 내부, Player 전체를 완전히 막는 비정상 위치, 다른 Build Piece와 불가능한 중복.

# 48. Player와 Build Collision

Player 근처 건설은 허용해야 한다. 그래야 전투 중 벽 세우기, 자신 주변 Box 만들기, Ramp 생성이 가능하다. 단, 구조물이 Player Collision의 중심을 완전히 관통하는 등 명백하게 잘못된 배치는 Server가 거부한다.

# 49. Server Build Piece

Server에서는 Build Piece를 가벼운 데이터로 관리한다. 개념: BuildPiece — Id, OwnerId, PieceType, MaterialType, GridCoordinate, Rotation, Health, ConstructionProgress, CreatedTick. Unity GameObject와 같은 구조를 만들지 않는다.

# 50. BuildPieceId

각 Piece는 Server가 생성한 고유 ID를 가진다(예: ulong 또는 현재 Entity ID 구조 재사용). ID 충돌이 없어야 한다.

# 51. Resource Consumption

배치 검증에 성공한 뒤에 Resource를 차감한다: `Validate → Reserve / Consume Resource → Create Build Piece → Replicate`. 실패한 요청 때문에 자원이 사라지면 안 된다.

# 52. 빠른 건설 반응성

Client는 Server 응답을 기다린 뒤 Preview를 보여주지 않는다. Player가 Click하면 Client는 즉시 Predicted Build Visual을 표시할 수 있다. 그 뒤 Server 결과를 기다린다.

# 53. Predicted Build

Server가 승인하면 Predicted → Confirmed. Server가 거부하면 Predicted → Remove.

# 54. Predicted Collision

로컬 조작감을 위해 필요한 경우 Client에서 예상 Collision을 즉시 활성화할 수 있다. 그러나 Server State와 충돌할 경우 Server 결과가 우선이다. 과도하게 복잡하면 첫 구현에서는 Visual Prediction만 사용해도 된다.

# 55. Build Action Sequence

중복 요청이나 순서 역전을 처리하기 위해 기존 Input Sequence와 비슷한 Action Sequence를 사용한다. Server가 동일 BuildRequest를 두 번 처리하면 안 된다.

# 56. Turbo Build

Build 버튼을 계속 누르고 있으면 연속해서 구조물을 배치할 수 있게 한다(Build Input Hold). Client가 일정 간격으로 새로운 Grid Candidate를 찾는다. Candidate가 이전 Placement와 달라지고 조건이 맞으면 BuildRequest를 보낸다.

# 57. Turbo Build Rate

건설 요청 빈도에는 Server 제한을 둔다(Config: MinimumBuildInterval). Client Packet Spam으로 Server가 과부하되지 않게 한다.

# 58. 중복 Turbo Build 방지

같은 Grid Slot에 BuildRequest를 계속 보내지 않는다. Client와 Server 양쪽에서 중복을 줄인다.

# 59. Build Construction

구조물은 생성 즉시 최종 내구도를 가지지 않는다. 생성 순간 InitialHealth에서 시작하고 일정 시간 동안 건설된다.

# 60. Construction Progress

개념: 0.0 → 1.0. 시간이 지날수록 Health, Visual Completion이 증가한다.

# 61. Material별 건설 속도

Wood 빠름, Stone 중간, Metal 느림. 실제 수치는 Config.

# 62. Construction 상태에서도 피해 가능

완성 중인 구조물도 공격받을 수 있다. 현재 HP가 0이 되면 즉시 파괴한다.

# 63. Construction 업데이트 최적화

모든 Build Piece를 매 Tick 돌면서 ConstructionProgress를 업데이트하지 않는다. 금지: `foreach (allBuildPieces) UpdateConstruction();`. Piece 수가 수천 개가 될 수 있다.

# 64. Construction Progress 계산

가능하면 저장된 CreatedTick, ConstructionDuration으로 현재 진행률을 필요할 때 계산한다: `Progress = (CurrentTick - CreatedTick) / Duration`. 매 Tick 상태 변경이 필요하지 않은 구조를 우선한다.

# 65. Structure Health

Material과 Piece Type에 따라 Max Health를 결정한다. 가능하면 `Base Piece Health × Material Multiplier` 같은 단순한 구조로 한다.

# 66. Structure Damage

기존 Damage System을 최대한 재사용한다. Target Type을 구분한다: Player, Environment, BuildPiece. 각 Weapon은 필요하면 PlayerDamage, StructureDamage를 별도로 가질 수 있다.

# 67. Hitscan과 Build

총알 Raycast가 Player Build를 먼저 만나면 해당 Build Piece가 Damage를 받는다. 뒤에 있는 Player까지 관통시키지 않는다. 관통 무기가 따로 없다면 기본적으로 차단한다.

# 68. Harvest Tool과 Build

Harvest Tool도 Player Build를 파괴할 수 있게 한다. Harvest Tool은 Build Piece에 별도의 Structure Damage를 사용할 수 있다. Player Build를 때린다고 Building Resource가 지급되지는 않게 하는 것을 기본값으로 한다.

# 69. Owner

Build Piece에는 Owner Player ID를 저장한다. 이번 Phase에서는 Ownership이 Edit Permission에 사용되지는 않는다. Phase 13.5를 위한 최소 정보만 보존한다.

# 70. Match 종료

Match 종료 시 Player Build는 전부 제거한다. 다음 Match로 유지하지 않는다. MySQL에 저장하지 않는다.

# 71. Structure Support

공중에 아무 연결 없이 구조물을 생성할 수 없게 한다. 구조물은 최소한 Terrain, Static Foundation, Grounded Build Piece 중 하나와 연결되어야 한다.

# 72. Grounded Structure

구조물이 Terrain 또는 유효한 Static Surface에 연결되어 있으면 Grounded로 본다.

# 73. Connected Structure

Grounded Piece와 Build Piece 연결을 통해 이어져 있다면 해당 구조물 역시 Supported 상태다. 예: Terrain → Wall → Floor → Ramp.

# 74. Structure Adjacency

Build Piece마다 연결 가능한 이웃 Slot을 계산한다. Grid 기반이므로 가능한 한 위치 검색을 Dictionary로 처리한다. 모든 Piece를 순회해서 가까운 Piece를 찾지 않는다. 금지: O(N²).

# 75. Support Graph

별도의 거대한 Generic Graph Framework를 만들 필요는 없다. Grid Key를 이용하여 필요한 Neighbor만 조회한다(예: `Dictionary<BuildSlotKey, BuildPiece>`).

# 76. 구조물 파괴 후 지지 판정

Piece가 파괴되면 연결되어 있던 인접 Piece들의 Support 상태를 검사한다. 단, 맵 전체 Build Piece를 다시 검사하지 않는다.

# 77. Local Support Recheck

파괴된 Piece 주변의 Connected Component만 검사한다: `Destroyed Piece → Neighbor Search → BFS / DFS → Ground 연결 확인`.

# 78. Unsupported Collapse

어떤 Connected Component도 Ground 또는 Static Support에 연결되지 않는다면 해당 구조물들을 붕괴시킨다.

# 79. Collapse Delay

즉시 수백 개가 한 프레임에 없어지는 것이 시각적으로 어색하면 아주 짧은 Delay를 사용할 수 있다. Gameplay 판정은 Server가 한다.

# 80. Collapse Networking

수백 개 Piece가 동시에 무너지면 Piece마다 별도 Packet을 보내지 않는 방법을 고려한다(예: BuildDestroyBatch). 실제 기존 Protocol 구조와 측정 결과를 보고 결정한다.

# 81. Static Environment Support

기존 Building이나 Terrain 위에 Player Build를 놓을 수 있다. Static Geometry에 Support Anchor 개념을 둘 수 있다. 복잡한 Mesh Topology 분석은 하지 않는다. Collision 결과로 Ground Support를 판단하는 단순한 방법을 우선한다.

# 82. Build와 Environment 겹침

이번 Phase 기본 정책: 건축물을 놓는다고 Environment Object가 자동 파괴되지는 않는다. 파괴 가능 Object라도 심하게 겹치면 Placement를 거부한다. 추후 설정으로 확장할 수 있게 한다.

# 83. Build Event Stream

별도 Network 메시지를 고려한다: BuildPlaced, BuildDestroyed, BuildDamage / BuildState, BuildBatchSync. 실제 Packet 이름은 기존 Naming Convention을 따른다.

# 84. BuildPlaced 정보

최소한 BuildPieceId, GridCoordinate, PieceType, MaterialType, Rotation, Owner, CreatedTick 등 Client가 Piece를 재구성하는 데 필요한 데이터만 전송한다.

# 85. Build Health Network

건설 Piece HP를 15Hz로 계속 보내지 않는다. Health가 실제 변경됐을 때만 Replication한다. 필요하면 여러 Damage를 일정 짧은 Window 동안 합친다.

# 86. Destroy 중요성

BuildDestroyed는 persistent world state 변경이다. Packet Loss 때문에 Client와 Server의 구조물이 달라지면 안 된다. 따라서 신뢰성 있는 전달이 필요하다. 현재 LiteNetLib Channel / DeliveryMethod 구조를 먼저 확인한 뒤 기존 정책에 맞는 Reliable 방식으로 처리한다.

# 87. BuildPlaced도 중요 상태

BuildPlaced 역시 반드시 일관성이 보장되어야 한다. Client 한 명에게 Build Piece가 누락되어 Collision 상태가 달라지면 안 된다.

# 88. Preview Network 금지

Preview Position, Preview Rotation, Invalid Preview, Material Ghost는 네트워크로 보내지 않는다. 전부 Client Local.

# 89. Interest Management

Build Piece가 많아지면 모든 Client에게 모든 건축 Event를 보내지 않는다. 기존 Map 크기를 고려하여 Build Interest Management를 구현한다.

# 90. Spatial Cell

World를 Build Networking용 Spatial Cell(Cell X, Cell Z)로 나눈다. 필요하면 Height Cell도 사용할 수 있지만 처음부터 3D Spatial Tree를 만들지 않는다.

# 91. Build Spatial Index

Server: `SpatialCell → BuildPiece IDs` 형태로 조회할 수 있게 한다. Player 근처 Cell만 관심 영역으로 처리한다.

# 92. Interest Radius

Client에 현재 Cell, 주변 Cell 범위의 건축물을 보낸다. 정확한 Radius는 Config로 관리한다.

# 93. Cell 진입

Player가 새로운 Interest Cell에 진입하면 해당 Cell에 존재하는 Build Piece 현재 상태를 동기화한다.

# 94. Cell 이탈

멀어진 Build Piece는 Client에서 제거하거나 비활성화할 수 있다. 다시 접근하면 Server 상태를 기준으로 다시 생성한다.

# 95. Reconnect

기존 10초 Reconnect 기능과 Building을 통합한다. 재접속 시 현재 Player 상태 + 현재 주변 Build Piece를 Server로부터 다시 동기화한다. 기존 Client 상태를 믿지 않는다.

# 96. Build Sync Version

필요하면 Match에 BuildWorldVersion 또는 유사한 Sequence를 둔다. Reconnect / Packet Ordering 디버깅에 활용한다. 하지만 필요 이상의 Event Sourcing Framework는 만들지 않는다.

# 97. Idempotency

Client가 이미 가지고 있는 BuildPieceId를 다시 받더라도 중복 GameObject를 생성하면 안 된다: `BuildPlaced(existing id) → Update / Ignore`.

# 98. Client Build Object

Client에서는 실제 Render / Collision Object가 필요하다. 다만 많은 구조물 때문에 GameObject 생성/삭제가 반복될 수 있다.

# 99. Client Pool

Build Piece가 반복적으로 생성/삭제되므로 Object Pool 적용을 검토한다. 이번 경우에는 Pool 사용 근거가 명확하다. 대상: Wall, Floor, Ramp, Roof. Material별로 별도 Prefab을 무제한 생성하지 않는다.

# 100. Build Visual

재료 상태를 시각적으로 구분한다(Wood, Stone, Metal 각각 독립적인 외형). Fortnite Asset을 복제하지 않는다. 단순 Placeholder Mesh / Material로 먼저 완성한다.

# 101. Construction Visual

건설 중에는 시각적으로 생성 과정이 보여야 한다. 복잡한 Procedural Mesh는 필요 없다. 예: Scale, Material Fill, Transparency, Simple Build Animation 중 저렴한 방식을 선택한다.

# 102. Construction Visual Authority

Client는 CreatedTick, ConstructionDuration으로 현재 Animation Progress를 계산할 수 있다. Server가 매 Frame Progress를 전송하지 않는다.

# 103. Build Collision

건설 중에도 Collision을 언제 활성화할지 정책을 정의한다. 빠른 방어 감각을 위해 배치 직후 Collision을 사용할 수 있다. 다만 게임 밸런스 값으로 조정 가능하게 한다.

# 104. Build HUD

기존 HUD에 Resource를 표시한다(예: Wood 120 / Stone 80 / Metal 35). Build Mode에서는 더 명확하게 표시한다.

# 105. Build Piece HUD

Build Mode에서 현재 선택된 Piece를 표시한다(Wall, Floor, Ramp, Roof).

# 106. Material HUD

현재 선택 Material을 표시한다. 자원이 부족하면 눈에 띄게 알려준다.

# 107. Resource 감소 표시

Build 성공 시 Resource HUD가 즉시 반응해야 한다. Client Prediction으로 먼저 감소 표시 후 Server 결과에 맞춰 보정할 수 있다. 단, Server가 최종 Authority다.

# 108. Build Reject

Server가 Build를 거부하면 Client가 이유를 알 수 있게 한다. 대표 이유: NoResource, OutOfRange, Blocked, Unsupported, Occupied, RateLimited, InvalidState. Release UI에서 모든 내부 이유를 상세 노출할 필요는 없다. Debug에서는 표시한다.

# 109. Build Sound

Sound Hook을 준비한다: BuildPlace, BuildComplete, BuildHit, BuildDestroy, HarvestHit, HarvestWeakPoint, HarvestDestroy. 실제 Audio Asset이 없다면 Hook만 만들고 Placeholder를 사용한다.

# 110. Damage Feedback

Build Piece를 공격하면 Hit Effect, Damage Feedback, Crack / Damage Stage를 Client에서 표현할 수 있게 한다. Gameplay 로직과 Visual을 분리한다.

# 111. Damage Stage

모든 HP 변경마다 Mesh를 새로 생성하지 않는다. 예: Healthy, Damaged, Critical 몇 단계 정도의 Visual 상태만 사용할 수 있다.

# 112. Build Mode와 Combat

Build Mode에서는 총을 쏘지 않는다. Weapon Slot을 누르면 즉시 Weapon Mode로 돌아간다. 전투와 건설 사이 전환이 빠르게 느껴져야 한다.

# 113. Harvest Tool 전환

Harvest Tool에서도 Build Mode로 빠르게 전환 가능해야 한다. 전체 조작 흐름: Weapon ↔ Build, Harvest ↔ Build, Build → Weapon이 자연스럽게 이어져야 한다.

# 114. Building 중 Movement

Build Mode에서도 기존 Movement 기능(Sprint, Jump, Slide, Crouch, Mantle)을 그대로 사용할 수 있어야 한다. 가능한 조작을 불필요하게 막지 않는다.

# 115. Air Building

Player가 공중에 있어도 Build 조건을 만족한다면 구조물을 배치할 수 있게 한다(예: Jump → Floor 배치). 다만 Support 규칙은 동일하다.

# 116. Ramp Rush

`Sprint → Ramp 연속 설치 → 높은 위치로 이동`이 가능해야 한다. Turbo Build가 이를 방해하지 않아야 한다.

# 117. Defensive Wall

총을 맞는 상황에서 `Build Mode → Wall → 즉시 Placement → Incoming Shot 차단`이 가능한 반응성이 목표다.

# 118. Box Building

`Wall Wall Wall Wall Floor Roof` 같은 빠른 건설이 가능해야 한다. 연속 배치 처리 때문에 Server Packet Queue가 폭증하지 않도록 한다.

# 119. Build Rate Limit

악성 Client가 초당 수백 개 BuildRequest를 보내도 Server 성능에 영향을 크게 주지 않도록 제한한다. Rate Limit은 Server에서 적용한다.

# 120. Resource Exploit 방지

동일 Request Replay, Build 실패 후 Resource duplication, Destroy 후 Resource duplication, Reconnect Resource rollback, Concurrent Build Request race를 방지한다.

# 121. Server Concurrency

현재 Server Threading 모델을 먼저 분석한다. 가능하면 Build World State 수정은 Game Loop Owner Thread 하나에서 처리한다. 불필요한 Lock을 추가하지 않는다.

# 122. Build World Ownership

가능하면 Single Writer 구조를 사용한다. Network Thread는 BuildRequest를 Queue하고 Game Loop가 처리한다. 현재 서버 구조가 이미 비슷하다면 그대로 따른다.

# 123. Lock

Build Dictionary 때문에 단순히 ConcurrentDictionary를 사용하지 않는다. 실제 접근 Thread를 확인한다. Lock이 필요하다면 기존 Harness 규칙대로 Deadlock을 검토한다.

# 124. Structure Data Layout

Server에서 수천 개 BuildPiece를 저장할 수 있음을 고려한다. Piece 하나가 불필요하게 큰 Class가 되지 않게 한다. 가능하면 compact한 데이터 구조를 사용한다. 그러나 처음부터 unsafe / custom allocator를 사용하지 않는다.

# 125. Build Collection

필요한 주요 조회: ID → BuildPiece, GridSlot → BuildPiece, SpatialCell → BuildPiece. 조회 특성에 맞는 Index를 둔다. 한 목적의 Dictionary 하나로 모든 문제를 억지로 해결하지 않는다.

# 126. O(N) 전체 검색 금지

Placement, Collision Candidate, Support Neighbor, Interest Management, Damage Target Lookup에서 전체 Build Piece 순회를 피한다.

# 127. 전체 Build Count

서버에 현재 Build Piece Count Metrics를 추가한다(예: game.build.count).

# 128. Build Rate Metrics

Build Requests/sec, Build Accepted/sec, Build Rejected/sec, Build Destroyed/sec를 관측할 수 있게 한다.

# 129. Harvest Metrics

필요하면 Harvest Hits/sec, Environment Destroyed를 Debug Metrics로 추가한다. Release에서 지나친 Metric Cardinality는 피한다.

# 130. Build Budget

무한히 많은 구조물을 생성하게 하지 않는다. 최소한 MaxBuildPiecesPerMatch, MaxBuildPiecesPerPlayer Limit을 Config로 둔다. 정확한 숫자는 Load Test 후 결정한다.

# 131. Limit 도달

Limit에 도달하면 Server가 새 Placement를 거부한다. 서버가 OOM 날 때까지 허용하지 않는다.

# 132. Memory Leak

Match 종료 후 반드시 Build Pieces, Spatial Index, Support Index, Build Events, Harvest State, Weak Point State, Client Build Objects가 사라져야 한다. 다음 Match에서 Count가 누적되면 안 된다.

# 133. Bot Building

기존 Headless Bot에 최소 Build 행동을 추가한다. 고급 Fortnite Bot을 만들 필요는 없다.

# 134. Bot Defensive Build

Bot이 Damage를 받으면 일정 확률 또는 조건으로 자신과 공격 방향 사이에 Wall을 설치할 수 있다.

# 135. Bot Ramp

Bot이 높은 위치로 이동해야 할 때 단순 Ramp를 만들 수 있다. Navigation 전체를 Building 기반으로 다시 만들지는 않는다.

# 136. Bot Resource

Load Test Bot은 Config에 따라 Infinite Build Resource 모드를 사용할 수 있게 해도 된다. 실제 Battle Royale Bot은 정상 Resource 규칙을 따른다.

# 137. Synthetic Build Load Test

실제 AI 행동과 별도로 Server 성능 측정을 위한 Build Stress Test를 만든다(예: 50 Players, 각 Player 연속 Build).

# 138. Build Count Test

단계적으로 테스트한다: 1,000 / 5,000 / 10,000 / 20,000 Build Pieces. 프로젝트에서 현실적으로 가능한 범위까지 측정한다. 목표는 특정 숫자를 무조건 통과하는 것이 아니라 병목이 어디서 생기는지 확인하는 것이다.

# 139. Metrics

각 Build Count에서 Tick P50, Tick P95, Tick P99, CPU, Memory, GC Allocation, Packets/sec, Bytes/sec를 측정한다.

# 140. Network 테스트

특히 50명이 동시에 건설, 100명이 동시에 건설, 연속 Turbo Build, 대량 Collapse를 측정한다.

# 141. 기존 Player Snapshot

Phase 13 이후에도 기존 Player Snapshot 크기를 확인한다. 건축 시스템 때문에 Player Entity 13 B가 불필요하게 커지면 안 된다. Build Mode 여부 같은 정말 필요한 Bit 정도만 추가한다.

# 142. Player Snapshot 목표

가능하면 13 B 유지 또는 아주 작은 증가만 허용한다. 변경 전후 크기를 보고한다.

# 143. Build Packet 측정

BuildPlaced 한 개의 실제 Payload 크기를 측정해 보고한다(BuildPlaced: XX bytes, BuildDestroyed: XX bytes, BuildSync Piece: XX bytes).

# 144. Batching

근접한 시간에 다수 Build Event가 발생하면 Packet Header 비용이 커질 수 있다. 측정 결과 필요하면 BuildEventBatch를 도입한다. 무조건 처음부터 복잡한 Batch Framework를 만들지는 않는다.

# 145. MTU

LiteNetLib UDP 사용 중이므로 Packet이 MTU를 불필요하게 초과하지 않도록 한다. 대량 BuildSync는 기존 Snapshot 분할 경험을 활용한다. 한 Packet에 무제한 Build Piece를 넣지 않는다.

# 146. BuildSync Chunk

Reconnect나 새로운 Interest Cell 진입 시 많은 Piece를 받아야 한다면 BuildSyncChunk 형태로 분할한다. 최대 Payload 크기를 명시한다.

# 147. Packet Validation

잘못된 PieceType, MaterialType, GridCoordinate, Rotation, Sequence를 보내도 Server가 Crash하지 않아야 한다. 기존 Fuzz Test에 Build Protocol을 추가한다.

# 148. Extreme Coordinate

int.MinValue, int.MaxValue, NaN이 가능한 필드, Overflow, Map 밖 좌표 Packet을 테스트한다. Server가 안전하게 Reject해야 한다.

# 149. Duplicate Packet

같은 BuildRequest가 중복 도착해도 Piece가 두 개 생성되거나 Resource가 두 번 차감되지 않게 한다.

# 150. Out-of-order

Build Packet이 지연되거나 순서가 바뀌는 상황을 테스트한다. 특히 Place, Damage, Destroy 순서가 꼬여도 Client가 영구적으로 잘못된 상태에 머무르지 않게 한다.

# 151. Reconnect 테스트

건설 직후 Disconnect, 건설 중 Disconnect, 대량 Build 주변에서 Reconnect, 구조물 파괴 중 Reconnect, Collapse 직후 Reconnect를 테스트한다. Reconnect Client는 Server 상태와 동일해져야 한다.

# 152. Late Event

Client가 Interest Area에서 나간 뒤 오래된 Build Event가 도착할 수 있다. Build ID / Sequence를 이용하여 잘못된 재생성을 막는다.

# 153. Spectator

Spectator는 Build할 수 없다. 그러나 관전 대상 근처 Build Piece는 정상적으로 볼 수 있어야 한다. Interest Center를 Spectator Camera 또는 Target Player 기준으로 처리하는 방식을 기존 Spectator 구조에 맞춰 선택한다.

# 154. Zone

Storm / Zone은 Build Piece 자체를 파괴하지 않는다. Player에게만 기존 Zone Damage를 적용한다.

# 155. Match Finish

Match가 Finished 상태가 되면 새로운 BuildRequest를 받지 않는다.

# 156. Player Elimination

Player가 탈락해도 이미 만든 구조물은 기본적으로 Match가 끝날 때까지 유지한다. Config로 변경할 수 있게 할 필요는 없다. 이번 Phase에서는 하나의 정책으로 유지한다.

# 157. Player Resource Drop

Player가 죽으면 남은 Building Resource를 어떻게 처리할지 결정한다. 기존 World Item 시스템과 자연스럽게 통합 가능한 경우 Wood / Stone / Metal Resource Drop을 지원한다.

# 158. Resource Pickup

드롭된 Building Resource는 기존 Pickup 시스템을 재사용한다. 새로운 별도 Item 시스템을 만들지 않는다.

# 159. Resource Auto Pickup

Building Resource는 근접 시 자동 획득하도록 구현할 수 있다. 현재 Item Pickup 구조와 충돌하지 않는 방식으로 최소 구현한다.

# 160. Harvest Resource Drop

환경을 때릴 때 자원이 World Item으로 매번 튀어나올 필요는 없다. Harvesting Resource는 Player에게 직접 지급한다. Client에서는 시각 효과만 보여준다.

# 161. UI Performance

Build HUD가 매 Frame 문자열을 새로 생성하지 않게 한다. Resource 값이 변경될 때만 UI를 갱신한다.

# 162. Build Preview Performance

Preview Mesh는 매 Frame Instantiate / Destroy하지 않는다. 하나의 Preview Object를 재사용한다.

# 163. Build Object Rendering

멀리 있는 Build Piece는 필요하면 Rendering 비용을 줄일 수 있게 한다. 이번 Phase에서 복잡한 LOD를 반드시 만들 필요는 없다. 단, 수천 개 Renderer가 문제가 되는지 Profile한다.

# 164. Collider

멀리 있는 Structure Collider를 Client에서 무조건 제거하지 않는다. Network gameplay와 관련 있으므로 현재 Interest Management 범위 안에서는 Collision 정확성을 우선한다.

# 165. Server Collision

Server는 Build Piece Collider를 Unity Physics에 의존할 수 없으므로 현재 Server Collision 시스템과 통합한다. 기존 Box / Map Collision 구조를 먼저 분석한다. Build Piece별 단순 Collision Shape를 정의한다.

# 166–169. Collision Shape

- Wall: 가능하면 단순 Box.
- Floor: 얇은 Box.
- Ramp: 기존 Movement와 호환되는 Ramp Collision을 구현한다. 복잡한 Triangle Mesh Collision보다 단순한 Plane / Ramp 수학을 우선 검토한다.
- Roof: 기본 Roof Shape에 맞는 단순 Collision을 사용한다.

# 170. Lag Compensation

Player Build는 전투 중 생성/파괴될 수 있다. 기존 Hitscan Lag Compensation과 충돌 가능성을 반드시 검토한다. 핵심 질문: 과거 시점에 Wall이 있었는가? 과거 시점에 Wall이 없었는가?

# 171. Build와 Lag Compensation 정책

처음부터 모든 구조물 상태를 과거로 Rewind하는 거대한 History를 만들지 않는다. 현재 Lag Compensation 구현을 분석한 뒤 최소한의 일관된 정책을 정한다. 권장: 현재 Build Collision 기준으로 우선 구현하고, 실제 플레이에서 불공정 문제가 확인되면 Build History Rewind를 별도 최적화한다.

# 172. History 무한 증가 금지

Build History를 추가할 경우 반드시 고정 Window만 유지한다. 하지만 이번 Phase에서는 성능 근거 없이 추가하지 않는다.

# 173. Tests — Harvest

Valid Harvest Hit, Out Of Range, Wrong Direction, Cooldown, Environment Damage, Resource Gain, Resource Cap, Destroy, Destroyed Object Re-hit, Weak Point, Weak Point Miss.

# 174. Tests — Build Validation

Valid Wall, Valid Floor, Valid Ramp, Valid Roof, No Resource, Out Of Range, Blocked, Occupied, Unsupported, Invalid Rotation, Invalid Piece, Invalid Material, Spectator Build, Dead Player Build, Wrong Match State.

# 175. Tests — Resource

Consume Resource, Failed Build No Consume, Duplicate Request No Double Consume, Maximum Resource, Resource Drop, Resource Pickup.

# 176. Tests — Structure Damage

Weapon Damage, Harvest Tool Damage, Health, Construction Health, Destroy, Already Destroyed, Multiple Damage Same Tick.

# 177. Tests — Support

Ground Wall, Wall + Floor, Wall + Ramp, Multiple Connection, Destroy Foundation, Partial Support, Unsupported Collapse, Large Connected Component.

# 178. Tests — Networking

Build Placed Replication, Build Destroyed Replication, Duplicate Event, Out Of Order Event, Build Batch, Interest Enter, Interest Leave, Reconnect Sync, Large BuildSync.

# 179. Tests — Security

Packet Spam, Replay Request, Impossible Coordinate, Invalid ID, Resource Cheat, Build Distance Cheat, Build Rate Cheat, Build Through Wall.

# 180. Fuzz Test

기존 Fuzz Test 범위에 새로운 Build Packet을 포함한다. Random Payload 때문에 Server가 Crash, OOM, Infinite Loop 상태가 되면 안 된다.

# 181. Regression Test

기존 테스트를 전부 실행한다. 현재 약 898 tests가 있으므로 신규 Test가 추가된 상태에서 전부 통과해야 한다. 기존 테스트를 삭제하거나 약화시켜 통과시키지 않는다.

# 182. Editor 확인

자동으로 검증하기 어려운 것은 별도 목록으로 남긴다. 최소: Build Preview 위치, Wall 연결, Floor 연결, Ramp 이동, Roof Collision, Material 시각 차이, Turbo Build Feel, Weapon ↔ Build 전환, Harvest Feel, Weak Point 표시, Build Animation, Structure Destruction.

# 183. 성능 검증

Phase 완료 후 반드시 Load Test를 진행한다. 기존 기준(50 Bots, Tick P95 ≈ 0.1 ms)과 비교한다.

# 184. 기본 Load Scenario

- Scenario A: 50 Players, No Building (기존 Baseline)
- Scenario B: 50 Players, 각 Player 건설
- Scenario C: 50 Players, Turbo Build
- Scenario D: 대량 Structure Destruction

# 185. 100 Player 검증

가능하다면 100 Headless Clients에서도 Build Request를 발생시켜 측정한다. 100 Player에서 모든 Bot이 고급 AI를 할 필요는 없다. Build Traffic 생성 목적이면 된다.

# 186. 성능 보고

다음 형식으로 결과를 남긴다.

```text
Baseline
Players: 50
Build Pieces: 0
Tick P50 / Tick P95 / Tick P99
CPU / Memory / GC
Network Send / Network Receive
```

```text
Building Load
Players: 50
Build Pieces: XXXX
Build Requests/sec
Tick P50 / Tick P95 / Tick P99
CPU / Memory / GC
Network Send / Network Receive
```

# 187. Build Piece Memory

가능하면 Server Build Piece 한 개당 대략적인 관리 비용도 확인한다. 정확한 값 측정이 어려우면 추측하지 않는다.

# 188. Performance Regression

Tick P95, Tick P99, GC Allocation, Network Bandwidth, Server Memory, Client Frame Time 중 하나라도 크게 악화되면 원인을 분석한다.

# 189. 최적화 우선순위

문제가 있으면 순서: `Measure → 큰 O(N) / O(N²) 문제 → Network 중복 → Allocation → Data Layout → Micro Optimization`. Span이나 Pool을 먼저 적용하지 않는다.

# 190. Debug Overlay

기존 F1 Debug에 가능하면 추가한다: Build Mode, Selected Piece, Selected Material, Wood, Stone, Metal, Build Piece Count, Nearby Build Count, Build Requests/sec, Build Reject Count. Development Build에서만 사용한다.

# 191. Server Debug

Health / Metric에 Active Build Pieces, Build Spatial Cells, Build Request Rate, Build Reject Rate를 추가할 수 있다.

# 192. 문서

Phase 완료 후 `/Docs`를 업데이트한다. 최소 `Building.md`를 추가한다. 내용: Architecture, Grid, Harvesting, Resources, Placement, Validation, Networking, Support, Interest Management, Performance.

# 193. Mermaid Architecture

Building.md에 최소 다음 흐름을 Mermaid로 표현한다: `Client Preview → BuildRequest → Server Validation → Resource Consume → Build World → BuildPlaced → Interested Clients`.

# 194. Support Diagram

`Terrain → Wall → Floor → Ramp`도 문서화한다. Foundation 파괴 시 Support 재계산 흐름도 포함한다.

# 195. Network Diagram

`Player Snapshot Stream` / 별도 `Build Event Stream` 구조를 문서화한다. 둘을 섞지 않는 이유도 기록한다.

# 196. 구현 순서

반드시 다음 순서로 진행한다.

- Phase 13A — Harvest: Resource Data → Harvest Tool → Harvestable Environment → Resource Gain → Environment Destruction → Weak Point → Tests. 완료 후 다음으로 이동한다.
- Phase 13B — Basic Building: Build Grid → Preview → Wall → Floor → Ramp → Roof → Resource Consumption → Server Validation → BuildPlaced Replication
- Phase 13C — Combat Building: Structure Health → Weapon Damage → Harvest Damage → Construction Progress → Turbo Build → Predicted Visual → Destruction
- Phase 13D — Support: Adjacency → Ground Support → Connected Support → Foundation Destruction → Unsupported Collapse → Batch Destroy
- Phase 13E — Network / Scale: Spatial Index → Interest Management → BuildSync → Reconnect → Batching → Metrics → 50 Player Load Test → 100 Player Stress Test
- Phase 13F — Polish: HUD → Sounds → Effects → Weak Point Visual → Construction Visual → Debug Overlay → Documentation

# 197. 단계별 Commit

가능하면 내부 작업을 의미 있는 단위로 Commit한다. 예: `feat: add harvesting and building resources`, `feat: add server-authoritative build placement`, `feat: add structure damage and destruction`, `feat: add building support system`, `perf: add building interest management`, `test: add building load tests`. 사용자가 Git Push를 요청하지 않았다면 Push하지 않는다.

# 198. 절대 하지 말 것

건축 Edit 구현, Microservice 추가, Redis 추가, Message Broker 추가, Build 데이터를 DB에 저장, 모든 Build를 Player Snapshot에 포함, 매 Tick 모든 Build 순회, 매 Frame Preview Instantiate, Client Build 결과 신뢰, 무제한 Build 허용, 무제한 Build Event Queue, O(N²) Support 탐색, 건설마다 Thread 생성, 건설마다 Task.Run, 건설마다 DB Query.

# 199. 핵심 성능 규칙

Player는 최대 100명이고, Build Piece는 Player보다 훨씬 많아질 수 있다. 따라서 Architecture를 100 Players 기준만으로 설계하면 안 된다. 최소 수천 개의 Player Build가 존재하는 Match를 전제로 한다.

# 200. Phase 13 완료 조건

```text
[ ] Harvest Tool이 있다.
[ ] 환경 Object를 파괴할 수 있다.
[ ] Wood / Stone / Metal을 얻을 수 있다.
[ ] Weak Point가 동작한다.
[ ] Resource 최대치가 적용된다.
[ ] Build Mode가 있다.
[ ] Wall을 설치할 수 있다.
[ ] Floor를 설치할 수 있다.
[ ] Ramp를 설치할 수 있다.
[ ] Roof를 설치할 수 있다.
[ ] Grid Snap이 정확하다.
[ ] Build Rotation이 가능하다.
[ ] Material 변경이 가능하다.
[ ] Resource가 Server에서 차감된다.
[ ] Preview가 즉각적이다.
[ ] Server가 Placement를 검증한다.
[ ] Turbo Build가 가능하다.
[ ] 구조물은 생성 중에도 피해를 받을 수 있다.
[ ] Wood / Stone / Metal 특성이 다르다.
[ ] 총으로 구조물을 파괴할 수 있다.
[ ] Harvest Tool로 구조물을 파괴할 수 있다.
[ ] Support가 없는 구조물은 설치할 수 없다.
[ ] Foundation 파괴 시 Unsupported Structure가 붕괴한다.
[ ] Build State가 Player Snapshot과 분리되어 있다.
[ ] Build Event가 필요할 때만 전송된다.
[ ] Build Interest Management가 있다.
[ ] Reconnect 후 Build 상태가 정확하다.
[ ] Spectator에서 Build할 수 없다.
[ ] Match 종료 시 Build가 모두 정리된다.
[ ] 다음 Match에 이전 Build가 남지 않는다.
[ ] 기존 테스트가 모두 통과한다.
[ ] 신규 테스트가 추가된다.
[ ] Fuzz Test가 통과한다.
[ ] 50 Player Build Load Test가 수행된다.
[ ] Build Piece 수 증가에 따른 Tick P95/P99를 측정한다.
[ ] Network Bandwidth를 측정한다.
[ ] Memory 증가량을 측정한다.
[ ] Editor 확인 항목을 보고한다.
```

# 201. 최종 보고 형식

Phase 13 완료 후 다음 형식으로 보고한다(실제 결과만, 숫자를 추측하지 않는다. 실행하지 못했다면 실행했다고 쓰지 않는다).

- 구현: Harvest / Building 구현 내용
- Harvesting: Harvest Tool, Weak Point, Environment Destruction, Resource
- Building: Wall, Floor, Ramp, Roof, Turbo Build
- Server: Validation, Structure Storage, Support, Spatial Index
- Network: BuildPlaced XX B, BuildDestroyed XX B, BuildSync Piece XX B, Player Snapshot Before 13 B / After XX B
- 테스트: 기존 898, 신규 XX, 전체 XXX passed
- 성능: Players 50, Build Pieces, Build Requests/sec, Tick P50/P95/P99, CPU, Memory, GC, Network Send/Receive
- Stress Test: Players 100, Build Pieces, 결과
- Editor 확인 필요
- 발견된 문제(실제 발견한 문제만)
- 다음 Phase: Phase 13.5 — Building Edit (아직 구현하지 않는다)

# 202. 최종 목표 플레이

```text
착륙 → Harvest Tool로 나무/돌/금속 채집 → 적 발견 → 총격 → 즉시 Build Mode → Wall 배치 → Ramp 배치
→ 높이 확보 → Floor 연결 → 적이 구조물 파괴 → Foundation이 깨지면 연결 구조물 붕괴 → 다시 건설하며 전투
```

이 루프가 지연 없이 자연스럽게 연결되어야 한다. 건축 시스템의 최종 기준은 "기능이 존재한다"가 아니라 "총격 도중 즉시 건설하여 방어하고 높이를 확보할 수 있을 정도로 빠르게 반응한다"이다.
