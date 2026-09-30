# Phase 4 Inventory / Loot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 테스트 아레나에 서버가 Loot Table로 정한 무기·탄약·회복·Shield 아이템이 놓이고, 플레이어는 빈손으로 시작해 E로 줍고(서버가 대상·거리·인벤토리를 판정), 3칸 인벤토리로 교체·재장전·버리기·회복을 하며, 죽으면 가진 것을 모두 떨어뜨린다. 두 Client가 같은 아이템을 같은 자리에서 본다.

**Architecture:** 아이템 정의는 서버의 `weapons.json`(탄약 종류 추가)·`items.json`·`loot.json`에 있고, 시작 시 `GameData`로 한 번 읽고 검증한다. `Match`는 Game Loop 스레드에서 월드 아이템 저장소(`WorldItems`, 256칸 고정), Loot Spawn Point와 시드 고정 난수(`LootSpawner`), 플레이어마다 `Inventory`(Phase 3의 고정 2칸 로드아웃을 대체)를 소유한다. 줍기·버리기·회복은 입력 버튼 비트(u16, 명령 30 B)로 오고, 서버가 Tick 순서(사용 취소 → 칸 → 버리기 → 줍기 → 재장전 → 발사 → 사용 시작 → 사용 완료)대로 처리한다. 월드 아이템은 Snapshot이 아니라 Reliable 이벤트(`WorldItems` 분할, `ItemSpawned` Upsert, `ItemRemoved`)로, 내 인벤토리는 바뀐 Tick 끝에 `InventoryState`로 간다. Client는 아이템 뷰 풀·"[E]" 안내(서버와 같은 선택 규칙)·인벤토리 HUD를 그리고, `WeaponState`가 칸 3개 기준으로 발사를 흉내 낸다.

**Tech Stack:** .NET 10, C# (Shared·Client는 C# 9 / netstandard2.1), `System.Numerics`, `System.Text.Json`(새 NuGet 없음), xUnit, LiteNetLib 2.1.4, Unity 6000.3.24f1(URP, Input System, UGUI 2.0 Legacy `Text`), NUnit(EditMode).

**Spec:** `Docs/specs/2026-10-01-phase4-inventory-loot-design.md` (결정 D1–D16은 확정. 아래 "Spec 해석"의 수정 사항은 이 계획이 따른다)

## Global Constraints

- 커밋하지 않는다. 각 Task의 마지막 단계는 "체크포인트"(검증 결과 기록)다. Commit·Push는 사용자가 "푸시"를 입력했을 때만 `github-push` 스킬로 한다. Force Push는 하지 않는다.
- 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다. 우리 코드는 Lock을 쓰지 않는다. 월드 아이템·인벤토리·Loot 난수는 Game Loop 스레드만 만진다(`Match` 소유). 스레드 구조는 바꾸지 않는다.
- 판정 코드는 **Server에만** 둔다: `Server/src/ProjectH.Server/Game/Items/`, `Game/Combat/`. Shared에는 패킷 DTO(`Shared/Runtime/Protocol/ItemPackets.cs`)와 맵 배치 좌표 상수(`Shared/Runtime/Simulation/LootPoints.cs`, spec D6, `game-core-rules` 4절 예외 2를 Task 3에서 추가)만 둔다. Client의 `PickupRule`·`WeaponState`는 표시용 사본이다.
- `Shared/Runtime`은 `netstandard2.1` + C# 9에서 컴파일되어야 한다. `UnityEngine` 참조 금지, `System.Numerics`만 사용. `record`·`init`·`Vector3` 인덱서 금지.
- `ProtocolConstants.ProtocolVersion = 4`. 입력 명령 30 B(Buttons u16, 알려진 비트 0x07FF 외는 버림), 최대 입력 패킷 2 + 3 × 30 = 92 B. Snapshot은 Phase 3 그대로다: 헤더 11 B + 수신자 블록 6 B + 엔티티 23 B × N, 50명 **1167 B**. 수신자 블록의 `WeaponSlot`은 현재 인벤토리 칸(0–2), `Ammo`는 그 칸의 탄창(빈 칸 0)이다. 새 패킷은 모두 1200 B 이하(테스트로 고정): `ItemCatalog` 최대 219 B, `WorldItems` 50개 952 B(19 B × 50 + 2), `ItemSpawned` 20 B, `ItemRemoved` 3 B, `InventoryState` 22 B, `PickupResult` 4 B.
- 데이터(D2–D5, D11): Wisp SMG = id 3, 피해 12, 0.0667 s(2 Tick), 탄창 25, 재장전 1.6 s(48 Tick), 80 m, 자동, Light. Vesper AR = Medium, Kestrel LR = Heavy(나머지 수치는 Phase 3 그대로). 등급 Common/Uncommon/Rare/Epic/Legendary = 1.00/1.05/1.10/1.15/1.20. 탄약 한도 Light 180, Medium 150, Heavy 30, 줍는 양 60/45/10. Medkit 3.0 s(90 Tick) +50 Health 스택 3, Shield Cell 2.0 s(60 Tick) +25 Shield 스택 6, 둘 다 최대 100. loot.json = spec §1 그대로(rarityWeights 50/25/15/7/3, Floor 35/35/15/15, Tower 60/10/15/15).
- 인벤토리·줍기(D1, D8–D13): 시작·부활은 빈손(무기 없음, Shield 0, Health 100). 무기 칸 3, 현재 칸, 탄약 3종, Medkit, Shield Cell. 줍기 범위 발 기준 수평 2.0 m, 수직 2.0 m(경계 포함), 가장 가까운 것(3D 거리), 같으면 작은 ItemId. 버리기 1 m 앞, 사망 Drop 반경 1 m 원. 월드 아이템 최대 256(`WorldItems.Capacity`), 가득 차면 가장 오래 떨어진 Drop부터 제거, Spawn Point 아이템은 제거하지 않는다. `LootRespawnSeconds` 기본 30(0 = 끔), `LootSeed` 기본 1.
- Server Hot Path(Tick, 발사, 줍기, 회복, 아이템 이벤트 송신)는 할당이 없고(테스트로 고정), 예외를 흐름 제어에 쓰지 않는다. JSON 예외는 시작 시에만 잡는다. `_sendBuffer`를 쓰는 `PacketWriter`를 다른 송신을 부를 수 있는 호출 너머로 들고 있지 않는다.
- Phase 0·1·3 동작을 유지한다: 누락 입력 유예(SimHz/2 동안 직전 입력 반복, 점프 제외. **줍기·버리기·회복·교체·재장전·발사는 실제로 받은 입력에서만**), Join 1회, peer별 입력 상한 `SimHz × 2`/s, `MtuOverride = 1232`, 아레나 규칙(중앙 7 m 비움, 박스끼리 옆으로 맞닿지 않음), `ShoulderClearance`, `runInBackground`, D12 좌클릭 규칙(잠그는 클릭은 발사 아님), Lag Compensation(되감기 최대 400 ms, History 용량 − 1로 잘림), 조준은 입력마다 그 Step의 `PredictedPosition` 기준, `WeaponState` 재조정(ack 기록 비교 후 재적용), 부활 때 Seq 유지, 원격 뷰 `BoxCollider`는 레이어 2, 칸별 발사 간격(`NextFireTick`).
- Unity: Scene·Prefab·`.meta` 파일을 직접 만들거나 수정하지 않는다(새 `.cs`의 `.meta`는 Unity가 만든다. Shared UPM 패키지(`file:`)의 새 파일도 Unity가 `.meta`를 만든다). Unity가 생성한 `Client/*.csproj`는 건드리지 않는다. Hot Path(Update, LateUpdate, HUD, 아이템 뷰)에서 LINQ·임시 컬렉션·캡처 람다·매 프레임 문자열 생성·`renderer.material`·`GetComponent` 금지(뷰를 처음 꾸밀 때만 `GetComponent`). `Physics.Raycast`는 단일 결과 버전만.
- Unity batchmode는 쓸 수 없다(사용자가 Editor를 열어 둔다). Client 검증은 (a) 스크래치 컴파일·NUnit 프로젝트, (b) Editor 자동 import 후 `Editor.log`의 새 줄에서 `error CS` 확인, (c) 사용자 수동 확인이다.
- 여러 Task가 중간 Step에서 일부러 컴파일되지 않는 구간을 가진다(테스트 먼저, 시그니처 변경). Unity가 컴파일하는 코드(Shared·Client)는 Task 1·2·3·9·10이 바꾼다. Task 1 Step 0부터 Task 9의 Editor.log 확인 Step 전까지, 그리고 Task 10 Step 1부터 Task 10의 Editor.log 확인 Step 전까지 사용자에게 Unity Editor를 포커스하지 말아 달라고 요청한다. 그래도 로그에 `error CS`가 보이면, 그 뒤에 오류 없는 스크립트 컴파일이 다시 있었는지 보고 마지막 컴파일 결과만 판정한다.
- 명령은 저장소 루트(`E:/popol/ProjectH`)에서 실행한다. 서버 테스트 명령은 `dotnet test Server/ProjectH.Server.slnx`다. 각 Task의 "Expected" 테스트 수는 이 계획대로 적용했을 때의 수다(시작 기준 서버 253, Client NUnit 64).

## Review Focus

- 두 플레이어가 같은 Tick에 같은 아이템에 E를 누름 → 먼저 처리된 한 명만 얻고, 다른 한 명은 `NothingInRange`, `ItemRemoved`는 한 번만 간다 (Task 6 `TwoPlayers_SameTick_SameItem_OnlyTheFirstGetsIt`).
- 재장전이 탄을 만들어 냄 → 발사·재장전·교체를 아무렇게나 섞어도 종류별 (탄창 + 보유량)은 발사한 만큼만 줄고 절대 늘지 않는다. 보유량 0이면 재장전이 시작되지 않는다 (Task 5 `RoundsAreConserved_AcrossFireReloadAndSwitches`, `EmptyReserve_NoReload_EvenAfterTheLastRound`).
- 버리기·교체 Drop·사망 Drop·다시 줍기가 아이템을 복제하거나 잃음 → 무기 수, 탄약 종류별 탄 수, 소모품 수가 (인벤토리 + 월드) 합계로 보존된다 (Task 6 `Items_AreConserved_ThroughDropSwapDeathAndPickup`).
- 월드가 가득 찬 상태(256개)에서 새 Client가 들어옴 → 6개 패킷(50개씩)으로, 각 1200 B 이하, 256개가 한 번씩 간다 (Task 4 `Join_WithAFullWorld_SendsSixChunks_AndEveryItemOnce`, Task 1 `WorldItems_FullChunk_Is952Bytes_AndRoundTrips`).
- 사망이 계속 반복됨(사망마다 8개 Drop) → 월드 아이템은 256에서 멈추고 Spawn Point 아이템은 하나도 지워지지 않는다 (Task 6 `RepeatedDeaths_KeepTheWorldAtTheCap_AndSpawnPointItemsSurvive`).

(spec §6이 이미 고정하는 경우는 제외했다: 범위 밖 줍기(Task 4·6 경계 테스트), 죽었거나 최대치에서 회복(Task 7 `AtMax_NothingStarts`, `Death_CancelsTheUse`). Snapshot 50명 1167 B는 Phase 3의 `Snapshot_WithMaxEntities_Is1167Bytes_AndFitsOneDatagram`이 바뀌지 않은 채로 계속 고정한다.)

## Spec 해석 (구현 전 확정)

spec을 코드에 옮기며 확정한 점이다. 1–5는 spec의 빈틈이나 충돌을 고친 것이다.

1. **`PickupResult`의 `TooFar`·`Gone`은 `NothingInRange` 하나로 합친다**(`Ok`, `NothingInRange`, `Full`). D8에서 서버가 대상을 직접 고르므로 Client가 먼 아이템이나 없는 아이템을 지정할 방법이 없다. "존재 → 거리" 검사는 범위 안 기존 아이템 중에서 고르는 것 자체다. 같은 Tick에 먼저 집힌 아이템은 두 번째 플레이어의 탐색에서 이미 빠져 있다.
2. **줍는 양:** spec §1은 Light(60)만 적었다. Medium 45(Vesper 탄창 1.5개), Heavy 10(Kestrel 탄창 2개)으로 둔다. 소모품 아이템은 1개씩 생긴다(`items.json`에 줍는 양 필드가 없다). 사망 Drop은 가진 수량 그대로 한 아이템이다.
3. **버리기 위치:** "발 앞 1 m"가 벽 안이나 얇은 벽 너머, 공중일 수 있다. 발 높이 + 0.5 m에서 그 방향으로 박스에 막히면 발밑에 놓고, 높이는 발 높이 이하에서 가장 높은 바닥·박스 윗면으로 한다(아이템은 떨어지지 않으므로 지금 닿는 곳에 놓아야 한다). 사망 Drop 원의 각 점에도 같은 규칙을 쓴다.
4. **Shield 최대 100:** D11("Shield +25, 최대 100")에 맞춰 `CombatRules.MaxShield`를 50에서 100으로 올린다. 시작 Shield는 로드아웃 값(운영 0, 테스트 50)이다. Phase 3 전투 테스트에서 `MaxShield`를 "시작 Shield" 뜻으로 쓴 곳은 `TestGameData.LoadoutShield`(50)로 바꾼다. 그래서 "30 × 5발 = Shield 50 + Health 100"이 그대로 성립한다.
5. **안내 문구는 영어로 쓴다:** "[E] Pick up Vesper AR [Rare]". 기존 HUD 문구가 영어이고, 내장 `LegacyRuntime.ttf`의 한글 표시는 확인되지 않았다(spec의 "E 줍기: 이름"은 뜻으로 따른다).
6. **테스트 주입점(D1):** `Match(ServerOptions, GameData, SendPacket, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null)`, `GameLoop(ServerOptions, GameData, ILogger, StartingLoadout? loadout = null)`. null이면 운영 값(`StartingLoadout.Empty`, Shared `LootPoints.All`)이다. 전투 테스트는 `TestGameData.CombatLoadout`(Test Auto·Test Semi, Common, Shield 50, Medium 60·Heavy 30)을 주입한다.
7. **`InventoryState`를 보내는 때:** Join, 부활, 그리고 줍기·버리기·사망·재장전 완료(보유량 변화)·회복 시작/취소/완료로 바뀐 Tick의 끝. 발사와 재장전 시작, 칸 선택은 보내지 않는다(Snapshot 수신자 블록이 현재 칸·탄창·재장전을 싣는다). 발사마다 Reliable을 보내면 트래픽이 늘고 Client 예측과 충돌한다.
8. **빈 칸 선택:** 1·2·3 중 비트 하나면 그 칸을 고른다(비어 있어도). 빈 칸에서는 발사·재장전하지 않는다. Phase 3의 `SlotBeyondLoadout_IsIgnored`는 이 결정을 고정하는 `EmptySlot_CanBeSelected_ButNeverFiresOrReloads`로 바뀐다. 빈손(현재 칸이 빔)으로 무기를 주우면 그 무기가 든 칸을 현재 칸으로 한다(D9의 "첫 빈 칸" 규칙은 그대로).
9. **칸별 발사 간격 유지:** 무기를 버리고(G) 다른 칸으로 다시 주워 Kestrel 간격을 건너뛰지 못하게, 인벤토리에 `DroppedFireLockTick`(버린 무기들의 `NextFireTick` 최댓값)을 두고 새로 든 무기는 그 Tick 전에는 쏘지 못한다. D16의 "무기 교체 지연"은 여전히 없다(버린 무기가 막 쏜 경우만 영향).
10. **등급 피해 계산은 `decimal`:** `1.15f`는 1.1499999…라서 `float`/`double`로는 90 × 1.15가 103으로 내려간다. `(decimal)1.15f` = 1.15로 103.5 → 104(0에서 멀어지게 반올림), 최소 1.
11. **회복 취소 조건(D11):** 실제 입력의 Fire 비트(발사 여부와 무관), Slot1–3 비트(현재 칸이어도), Drop 비트, 다른 회복 비트. 다른 회복은 같은 Tick의 "사용 시작"에서 바로 시작된다. Reload·Interact·이동은 취소하지 않는다. Medkit·Shield Cell 비트가 동시에 오면 둘 다 무시한다.
12. **Loot 난수:** `LootSpawner`가 `new Random(ServerOptions.LootSeed)` 하나를 가지고 `Match`(Game Loop 스레드)가 소유한다. 초기 배치·재생성 모두 이 난수를 쓴다. 테스트는 결과를 하드코딩하지 않고 같은 시드의 두 인스턴스를 비교한다.
13. **Leave(접속 끊김)는 인벤토리를 떨어뜨리지 않는다**(spec에 없음). 사망 Drop만 있다.
14. **데이터 검증 범위:** 등급 정확히 5개, 탄약 Light·Medium·Heavy 정확히 1개씩, 소모품 Medkit·ShieldCell 정확히 1개씩(패킷 형식이 이 개수로 고정). 무기 이름 중복도 거절한다(spec §1 "이름·Id 중복"). 표 이름 참조는 Shared `LootPoints`가 쓰는 이름이 `loot.json`에 모두 있는지 `GameData`가 확인한다.
15. **Client 이름:** spec §4의 `PickupPrompt`(순수 계산 + UI)는 순수 선택 규칙 `PickupRule`(EditMode 테스트 대상)과 `InventoryHud`의 안내 문구(UI)로 나눈다. 문자열 캐시는 `InventoryHudText`가 맡는다.
16. **Spawn Point 검사(spec §6):** "박스와 겹치지 않음" = 그 자리에 선 캐릭터 AABB가 박스와 겹치지 않음(`MovementSimulation.OverlapsAny`). "닿을 수 있음" = 바닥(y 0)이면 중앙 7 m 밖, 박스 위면 그 박스 높이가 받침(바닥 또는 닿을 수 있는 박스 윗면)에서 1 m 이하(점프 최고점 약 1.34 m). 통합 테스트가 걸어가는 안쪽 4곳(반경 8 m)은 모든 Spawn 위치에서 직선으로 막힘이 없어야 한다.

---

## File Structure

| 경로 | 책임 |
|---|---|
| `Shared/Runtime/Simulation/InputCommand.cs` | `InputButtons : ushort`, 새 비트 Slot3·Interact·Drop·UseMedkit·UseShieldCell |
| `Shared/Runtime/Protocol/ClientPackets.cs`, `PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs` | 명령 30 B, PacketId 13–18, 읽기 상한, ProtocolVersion 4 |
| `Shared/Runtime/Protocol/CombatPackets.cs` | `WeaponInfo.AmmoType`, 카탈로그 무기당 1 B |
| `Shared/Runtime/Protocol/ItemPackets.cs` (신규) | 아이템 enum·상수, `ItemCatalogPacket`, `WorldItemData`, `WorldItemsPacket`, `ItemSpawnedPacket`, `ItemRemoved`, `InventoryState`, `PickupResult` |
| `Shared/Runtime/Simulation/LootPoints.cs` (신규) | Loot Spawn Point 좌표 상수 17곳 (D6) |
| `Server/src/ProjectH.Server/weapons.json`, `items.json`(신규), `loot.json`(신규), `ProjectH.Server.csproj`, `appsettings.json` | 데이터 파일과 출력 폴더 복사, Loot 설정 |
| `Server/src/ProjectH.Server/Game/DataJson.cs`, `GameData.cs` (신규) | JSON 공용 옵션·Tick 변환, 세 파일 로드·교차 검증 |
| `Server/src/ProjectH.Server/Game/Combat/WeaponCatalog.cs`, `WeaponDefinition.cs`, `WeaponRules.cs`, `CombatRules.cs` | 탄약 종류, Id 조회, 인벤토리 기반 교체·재장전·발사, 등급 피해, MaxShield 100 |
| `Server/src/ProjectH.Server/Game/Items/ItemCatalog.cs` (신규) | `items.json` 로드·검증, Wire 데이터 |
| `Server/src/ProjectH.Server/Game/Items/LootTable.cs`, `LootSpawner.cs` (신규) | `loot.json`, 두 단계 가중치 뽑기, Spawn Point·시드 난수·재생성 타이머 |
| `Server/src/ProjectH.Server/Game/Items/WorldItems.cs` (신규) | 256칸 저장소, Id 발급, 오래된 Drop 제거, 근처 탐색 |
| `Server/src/ProjectH.Server/Game/Items/Inventory.cs`, `StartingLoadout.cs` (신규) | 인벤토리 상태·Wire 변환, 시작 로드아웃(운영 빈손, 테스트 주입) |
| `Server/src/ProjectH.Server/Game/Items/ItemRules.cs`, `ConsumableRules.cs` (신규) | 줍기 범위·스택 여유·Drop 위치, 회복 Channel |
| `Server/src/ProjectH.Server/Game/PlayerEntity.cs`, `Match.cs`, `GameLoop.cs`, `GameServerService.cs`, `ServerOptions.cs` | 인벤토리 보유, Tick 순서, 아이템 이벤트, Join 송신, 주입점, 설정 |
| `Server/tests/ProjectH.Server.Tests/TestWeapons.cs`, `TestGameData.cs` (신규) | 테스트 무기 3종, 테스트 아이템·Loot·전투 로드아웃 |
| `Server/tests/ProjectH.Server.Tests/Shared/ItemPacketTests.cs`, `LootPointsTests.cs` (신규), `PacketTests.cs`, `PacketWriterReaderTests.cs`, `ProtocolConstantsTests.cs`, `CombatPacketTests.cs` | 프로토콜, Spawn Point 규칙 |
| `Server/tests/ProjectH.Server.Tests/Game/ItemCatalogTests.cs`, `LootTableTests.cs`, `WorldItemsTests.cs`, `MatchWorldItemsTests.cs`, `InventoryMatchTests.cs`, `PickupDropTests.cs`, `ConsumableTests.cs`, `LootRespawnTests.cs` (신규), `WeaponRulesTests.cs`, `WeaponCatalogTests.cs`, `CombatMatchTests.cs`, `LagCompensationTests.cs`, `MatchTests.cs`, `ServerOptionsTests.cs` | 서버 규칙 |
| `Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs`, `ItemIntegrationTests.cs` (신규), `CombatIntegrationTests.cs`, `GameLoopPeerTests.cs`, `ServerIntegrationTests.cs` | 통합 |
| `Client/Assets/Scripts/Net/NetClient.cs`, `VectorConversions.cs` | 아이템 이벤트 5종, `ToNumerics` |
| `Client/Assets/Scripts/Game/WeaponState.cs`, `WorldItemList.cs`(신규), `PickupRule.cs`(신규), `InventoryHudText.cs`(신규) | 순수 상태: 칸 3개 무기 규칙, 아이템 목록, 줍기 대상 규칙, HUD 문자열 캐시 |
| `Client/Assets/Scripts/Game/WorldItemViews.cs`(신규), `InventoryHud.cs`(신규), `CombatHud.cs`, `GameClient.cs` | 아이템 뷰 풀, 인벤토리 HUD·안내, 탄창/보유량 표시, 전체 연결 |
| `Client/Assets/Scripts/Input/InputReader.cs`, `Game/LocalPlayerPredictor.cs`, `Bootstrap/DevConnectPanel.cs` | E·G·3·4·5 키, 눌림 마스크, 안내 문구 |
| `Client/Assets/Tests/EditMode/WeaponStateTests.cs`, `PickupRuleTests.cs`, `WorldItemListTests.cs`, `InventoryHudTextTests.cs` (신규 3), `LocalPlayerPredictorTests.cs` | EditMode |
| `.claude/skills/game-core-rules/SKILL.md`, `CLAUDE.md` | Shared 예외 2(맵 배치 데이터), 변경 이력 |
| `Docs/Networking.md`, `Docs/Server.md`, `Docs/Client.md`, `Docs/Architecture.md`, `Docs/BattleRoyale.md` | 동작 변경 반영 |

스크래치 검증 프로젝트(저장소 밖, 수정해도 됨):

- 컴파일 확인: `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj` — `Client/Assets/Scripts/**/*.cs`와 `Shared/Runtime/**/*.cs`를 netstandard2.1 / C# 9로 Unity DLL에 대해 컴파일한다. 새 스크립트는 glob으로 자동 포함된다.
- NUnit: `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj` — `Client/Assets/Tests/EditMode/*.cs` 전부와 **명시한 Client 소스만** + `Shared/Runtime/**/*.cs`를 `UnityEngine.CoreModule`만 참조해 net10.0에서 실행한다. EditMode 테스트는 `Physics`·`GameObject`·`Canvas`·네이티브 `Quaternion` 함수를 쓰면 안 된다. 새 순수 소스(`WorldItemList.cs`, `PickupRule.cs`, `InventoryHudText.cs`)는 Task 9·10에서 이 csproj에 `<Compile Include>`를 추가하고, 지워진 `FireRateAccumulator.cs` 항목은 Task 9에서 뺀다.

서버 명령은 저장소 루트(`E:/popol/ProjectH`)에서 실행한다.

---

### Task 1: Protocol v4 (Buttons u16·명령 30 B, 아이템 패킷 6종, 버전 4)

**Files:**
- Modify: `Shared/Runtime/Simulation/InputCommand.cs` (전체 교체)
- Modify: `Shared/Runtime/Protocol/ClientPackets.cs`, `PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs`
- Create: `Shared/Runtime/Protocol/ItemPackets.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Shared/ItemPacketTests.cs` (신규), `Shared/PacketTests.cs`, `Shared/PacketWriterReaderTests.cs`, `Shared/ProtocolConstantsTests.cs`

**Interfaces:**
- Consumes: 기존 `PacketWriter`/`PacketReader`, `Finite.Check`(CombatPackets.cs, internal), `ProtocolConstants.MaxPacketSize = 1200`
- Produces (네임스페이스 `ProjectH.Shared.Simulation` / `ProjectH.Shared.Protocol`):
  - `enum InputButtons : ushort { None = 0, Jump = 1, Sprint = 2, Fire = 4, Reload = 8, Slot1 = 16, Slot2 = 32, Slot3 = 64, Interact = 128, Drop = 256, UseMedkit = 512, UseShieldCell = 1024 }`
  - `PlayerInputPacket.CommandSize = 30`, `PlayerInputPacket.MaxSize = 92`
  - `PacketId.ItemCatalog = 13, WorldItems = 14, ItemSpawned = 15, ItemRemoved = 16, InventoryState = 17, PickupResult = 18`; `ProtocolConstants.ProtocolVersion = 4`
  - `enum AmmoType : byte { None, Light, Medium, Heavy }`, `enum ItemKind : byte { None, Weapon, Ammo, Consumable }`, `enum ConsumableType : byte { None, Medkit, ShieldCell }`, `enum PickupResultCode : byte { Ok, NothingInRange, Full }`
  - `static class ItemConstants { RarityCount = 5, AmmoTypeCount = 3, ConsumableTypeCount = 2, WeaponSlotCount = 3, MaxNameBytes = 16 }`
  - `struct RarityInfo { string Name; float DamageMultiplier; }`, `struct AmmoInfo { AmmoType Type; string Name; ushort Max; }`, `struct ConsumableInfo { ConsumableType Type; string Name; ushort UseTicks, Heal, Shield; byte MaxStack; }`, `sealed class ItemCatalogData { RarityInfo[] Rarities; AmmoInfo[] Ammo; ConsumableInfo[] Consumables; }` (Ammo·Consumables는 값 − 1 순서)
  - `static class ItemCatalogPacket { MaxSize = 219; Write(ref PacketWriter, ItemCatalogData); bool TryRead(ref PacketReader, out ItemCatalogData) }`
  - `struct WorldItemData { Size = 19; ushort ItemId; ItemKind Kind; byte DefId; byte Rarity; ushort Amount; Vector3 Position; static Write/TryRead }` (PacketId 없음)
  - `static class WorldItemsPacket { MaxItems = 50; MaxSize = 952; WriteHeader(ref PacketWriter, int count); bool TryReadHeader(ref PacketReader, out int count) }`
  - `static class ItemSpawnedPacket { Size = 20; Write(ref PacketWriter, in WorldItemData); TryRead(ref PacketReader, out WorldItemData) }`
  - `struct ItemRemoved { ushort ItemId; }`, `struct PickupResult { PickupResultCode Result; ushort ItemId; }` — `static Write(ref PacketWriter, in T)` / `static bool TryRead(ref PacketReader, out T)`
  - `struct InventorySlotState { byte WeaponId, Rarity, MagAmmo; bool IsEmpty }`, `struct InventoryState { PayloadSize = 21; Slot0, Slot1, Slot2; byte CurrentSlot; ushort LightAmmo, MediumAmmo, HeavyAmmo; byte Medkits, ShieldCells; ConsumableType Using; ushort UseRemainingTicks; GetSlot/SetSlot(int), GetAmmo/SetAmmo(AmmoType); static Write/TryRead }`
  - PacketId 바이트는 Write가 쓰고, TryRead는 PacketId 다음부터 읽는다(Phase 3와 같은 규칙).

- [ ] **Step 0: Editor.log 기준선 기록 (코드 수정 전)**

이 Task부터 Unity가 컴파일하는 코드(Shared)를 고친다. `Editor.log`는 세션 동안 누적되므로 수정 전 줄 수를 기록해 두고, Task 9의 Editor.log 확인 Step에서 이 줄 이후만 검사한다. 사용자에게 Task 9의 확인 Step 전까지 Unity Editor를 포커스하지 말아 달라고 요청한다.

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; wc -l < "$LOG"
```

출력값을 `N0`로 체크포인트에 기록한다.

- [ ] **Step 1: 실패하는 테스트 작성 — 기존 프로토콜 테스트 수정**

`Server/tests/ProjectH.Server.Tests/Shared/PacketTests.cs`: 입력 왕복에 새 버튼을 넣고, 30 B·92 B 크기, 잘린 입력(30 B 기준), 알려진 비트 마스크(0x07FF)를 고정한다.

변경 1/4 — 찾을 코드:

```csharp
        MoveX = 0.5f,
        MoveY = -1f,
        Yaw = 90f + seq,
        Buttons = InputButtons.Jump | InputButtons.Fire | InputButtons.Slot2,
        AimYaw = 12.5f + seq,
        AimPitch = -30f,
        ViewTick = 1000.25f + seq,
```

바꿀 코드:

```csharp
        MoveX = 0.5f,
        MoveY = -1f,
        Yaw = 90f + seq,
        Buttons = InputButtons.Jump | InputButtons.Fire | InputButtons.Slot2 | InputButtons.Interact | InputButtons.UseShieldCell,
        AimYaw = 12.5f + seq,
        AimPitch = -30f,
        ViewTick = 1000.25f + seq,
```

변경 2/4 — 찾을 코드:

```csharp
    [Fact]
    public void PlayerInput_Sizes_ArePinned()
    {
        // 29 bytes per command; the largest input packet (3 commands) stays small (D2).
        Assert.Equal(29, PlayerInputPacket.CommandSize);
        Assert.Equal(89, PlayerInputPacket.MaxSize);
    }

    [Theory]
```

바꿀 코드:

```csharp
    [Fact]
    public void PlayerInput_Sizes_ArePinned()
    {
        // Phase 4 (D14): buttons are 2 bytes, so 30 bytes per command; 3 commands = 92 bytes.
        Assert.Equal(30, PlayerInputPacket.CommandSize);
        Assert.Equal(92, PlayerInputPacket.MaxSize);
    }

    [Theory]
```

변경 3/4 — 찾을 코드:

```csharp
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 17)]    // a Phase 1 sized command is now one command short
    [InlineData(1, 28)]
    [InlineData(2, 29)]
    [InlineData(3, 86)]
    public void PlayerInput_Truncated_IsRejected(byte count, int payloadBytes)
    {
        var bytes = new byte[2 + payloadBytes];
```

바꿀 코드:

```csharp
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 17)]    // a Phase 1 sized command is now one command short
    [InlineData(1, 29)]    // a Phase 3 sized command (1-byte buttons) is one byte short
    [InlineData(2, 30)]
    [InlineData(3, 89)]
    public void PlayerInput_Truncated_IsRejected(byte count, int payloadBytes)
    {
        var bytes = new byte[2 + payloadBytes];
```

변경 4/4 — 찾을 코드:

```csharp
    public void PlayerInput_UnknownButtonBits_AreMasked()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = (InputButtons)0xFF });
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out var read));
        Assert.Equal(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire | InputButtons.Reload |
                     InputButtons.Slot1 | InputButtons.Slot2, read.Get(0).Buttons);
    }

    [Fact]
```

바꿀 코드:

```csharp
    public void PlayerInput_UnknownButtonBits_AreMasked()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = (InputButtons)0xFFFF });
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out var read));
        Assert.Equal(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire | InputButtons.Reload |
                     InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.Interact |
                     InputButtons.Drop | InputButtons.UseMedkit | InputButtons.UseShieldCell, read.Get(0).Buttons);
        Assert.Equal(0x07FF, (int)read.Get(0).Buttons);
    }

    [Fact]
```

`Server/tests/ProjectH.Server.Tests/Shared/ProtocolConstantsTests.cs`:

변경 — 찾을 코드:

```csharp
    }

    [Fact]
    public void ProtocolVersion_IsThree()
    {
        // Phase 3 changed the input, snapshot and packet set; v2 clients must be rejected at connect.
        Assert.Equal((ushort)3, ProtocolConstants.ProtocolVersion);
    }
}
```

바꿀 코드:

```csharp
    }

    [Fact]
    public void ProtocolVersion_IsFour()
    {
        // Phase 4 changed the input command and the weapon catalog and added item packets; v3 clients
        // must be rejected at connect.
        Assert.Equal((ushort)4, ProtocolConstants.ProtocolVersion);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/PacketWriterReaderTests.cs`:

변경 1/2 — 찾을 코드:

```csharp

    [Theory]
    [InlineData(0)]
    [InlineData(13)]   // one above PacketId.PlayerRespawned
    [InlineData(255)]
    public void PacketId_OutOfRange_IsRejected(byte raw)
    {
```

바꿀 코드:

```csharp

    [Theory]
    [InlineData(0)]
    [InlineData(19)]   // one above PacketId.PickupResult
    [InlineData(255)]
    public void PacketId_OutOfRange_IsRejected(byte raw)
    {
```

변경 2/2 — 찾을 코드:

```csharp
    [InlineData(PacketId.PlayerInput)]
    [InlineData(PacketId.WeaponCatalog)]
    [InlineData(PacketId.PlayerRespawned)]
    public void PacketId_InRange_IsAccepted(PacketId expected)
    {
        var reader = new PacketReader(new[] { (byte)expected });
```

바꿀 코드:

```csharp
    [InlineData(PacketId.PlayerInput)]
    [InlineData(PacketId.WeaponCatalog)]
    [InlineData(PacketId.PlayerRespawned)]
    [InlineData(PacketId.ItemCatalog)]
    [InlineData(PacketId.PickupResult)]
    public void PacketId_InRange_IsAccepted(PacketId expected)
    {
        var reader = new PacketReader(new[] { (byte)expected });
```

- [ ] **Step 2: 실패하는 테스트 작성 — 아이템 패킷**

`Server/tests/ProjectH.Server.Tests/Shared/ItemPacketTests.cs`를 만든다. 왕복, 잘린 패킷, 잘못된 값 거절, 최대 크기(ItemCatalog 219 B, WorldItems 50개 952 B ≤ 1200 B)를 고정한다.

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ItemPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static ItemCatalogData Catalog(string name = "Name") => new ItemCatalogData
    {
        Rarities = new[]
        {
            new RarityInfo { Name = name, DamageMultiplier = 1f },
            new RarityInfo { Name = name, DamageMultiplier = 1.05f },
            new RarityInfo { Name = name, DamageMultiplier = 1.1f },
            new RarityInfo { Name = name, DamageMultiplier = 1.15f },
            new RarityInfo { Name = name, DamageMultiplier = 1.2f },
        },
        Ammo = new[]
        {
            new AmmoInfo { Type = AmmoType.Light, Name = name, Max = 180 },
            new AmmoInfo { Type = AmmoType.Medium, Name = name, Max = 150 },
            new AmmoInfo { Type = AmmoType.Heavy, Name = name, Max = 30 },
        },
        Consumables = new[]
        {
            new ConsumableInfo { Type = ConsumableType.Medkit, Name = name, UseTicks = 90, Heal = 50, Shield = 0, MaxStack = 3 },
            new ConsumableInfo { Type = ConsumableType.ShieldCell, Name = name, UseTicks = 60, Heal = 0, Shield = 25, MaxStack = 6 },
        },
    };

    private static WorldItemData Weapon(ushort id) => new WorldItemData
    {
        ItemId = id,
        Kind = ItemKind.Weapon,
        DefId = 2,
        Rarity = 4,
        Amount = 0,   // an empty magazine is a valid weapon item
        Position = new Vector3(1.5f, 1f, -14.5f),
    };

    [Fact]
    public void ItemCatalog_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        ItemCatalogPacket.Write(ref writer, Catalog());
        Assert.False(writer.Overflowed);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemCatalog);
        Assert.True(ItemCatalogPacket.TryRead(ref reader, out var read));

        Assert.Equal(5, read.Rarities.Length);
        Assert.Equal(1.2f, read.Rarities[4].DamageMultiplier);
        Assert.Equal(AmmoType.Heavy, read.Ammo[2].Type);
        Assert.Equal(30, read.Ammo[2].Max);
        Assert.Equal(ConsumableType.ShieldCell, read.Consumables[1].Type);
        Assert.Equal(60, read.Consumables[1].UseTicks);
        Assert.Equal(25, read.Consumables[1].Shield);
        Assert.Equal(6, read.Consumables[1].MaxStack);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ItemCatalog_WithMaximumLengthNames_Is219Bytes()
    {
        var writer = new PacketWriter(_buffer);
        ItemCatalogPacket.Write(ref writer, Catalog(new string('n', ItemConstants.MaxNameBytes)));
        Assert.False(writer.Overflowed);
        Assert.Equal(ItemCatalogPacket.MaxSize, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void ItemCatalog_Truncated_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        ItemCatalogPacket.Write(ref writer, Catalog());
        for (int cut = 1; cut < writer.Length; cut++)
        {
            var reader = ReaderAfterId(cut, PacketId.ItemCatalog);
            Assert.False(ItemCatalogPacket.TryRead(ref reader, out _), $"cut at {cut}");
        }
    }

    [Fact]
    public void ItemCatalog_BadValues_AreRejected()
    {
        var wrongOrder = Catalog();
        (wrongOrder.Ammo[0], wrongOrder.Ammo[1]) = (wrongOrder.Ammo[1], wrongOrder.Ammo[0]);
        var noEffect = Catalog();
        noEffect.Consumables[0].Heal = 0;
        var zeroMultiplier = Catalog();
        zeroMultiplier.Rarities[2].DamageMultiplier = 0f;
        var nanMultiplier = Catalog();
        nanMultiplier.Rarities[2].DamageMultiplier = float.NaN;
        var fourRarities = Catalog();
        fourRarities.Rarities = fourRarities.Rarities[..4];

        foreach (var bad in new[] { wrongOrder, noEffect, zeroMultiplier, nanMultiplier, fourRarities })
        {
            var writer = new PacketWriter(_buffer);
            ItemCatalogPacket.Write(ref writer, bad);
            var reader = ReaderAfterId(writer.Length, PacketId.ItemCatalog);
            Assert.False(ItemCatalogPacket.TryRead(ref reader, out _));
        }
    }

    [Fact]
    public void ItemSpawned_RoundTrip_Is20Bytes()
    {
        var writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, Weapon(513));
        Assert.Equal(ItemSpawnedPacket.Size, writer.Length);
        Assert.Equal(20, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemSpawned);
        Assert.True(ItemSpawnedPacket.TryRead(ref reader, out var item));
        Assert.Equal(513, item.ItemId);
        Assert.Equal(ItemKind.Weapon, item.Kind);
        Assert.Equal(2, item.DefId);
        Assert.Equal(4, item.Rarity);
        Assert.Equal(0, item.Amount);
        Assert.Equal(new Vector3(1.5f, 1f, -14.5f), item.Position);
    }

    [Theory]
    [InlineData(0, ItemKind.Weapon, 1, 0, 5)]        // id 0 is "none"
    [InlineData(1, ItemKind.None, 1, 0, 5)]
    [InlineData(1, (ItemKind)4, 1, 0, 5)]
    [InlineData(1, ItemKind.Weapon, 0, 0, 5)]        // weapon id 0
    [InlineData(1, ItemKind.Weapon, 1, 5, 5)]        // rarity out of range
    [InlineData(1, ItemKind.Ammo, 4, 0, 60)]         // no such ammo type
    [InlineData(1, ItemKind.Ammo, 1, 1, 60)]         // ammo has no rarity
    [InlineData(1, ItemKind.Ammo, 1, 0, 0)]          // empty stack
    [InlineData(1, ItemKind.Consumable, 3, 0, 1)]
    [InlineData(1, ItemKind.Consumable, 1, 0, 0)]
    public void WorldItem_InvalidValues_AreRejected(ushort id, ItemKind kind, byte defId, byte rarity, ushort amount)
    {
        var writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, new WorldItemData { ItemId = id, Kind = kind, DefId = defId, Rarity = rarity, Amount = amount });
        var reader = ReaderAfterId(writer.Length, PacketId.ItemSpawned);
        Assert.False(ItemSpawnedPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WorldItem_NonFinitePosition_OrTruncated_IsRejected()
    {
        var item = Weapon(1);
        item.Position = new Vector3(0f, float.PositiveInfinity, 0f);
        var writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, item);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemSpawned);
        Assert.False(ItemSpawnedPacket.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        ItemSpawnedPacket.Write(ref writer, Weapon(1));
        reader = ReaderAfterId(writer.Length - 1, PacketId.ItemSpawned);
        Assert.False(ItemSpawnedPacket.TryRead(ref reader, out _));
    }

    // Review Focus (D13, D14): a full chunk of 50 items is 952 bytes, well under one datagram.
    [Fact]
    public void WorldItems_FullChunk_Is952Bytes_AndRoundTrips()
    {
        var writer = new PacketWriter(_buffer);
        WorldItemsPacket.WriteHeader(ref writer, WorldItemsPacket.MaxItems);
        for (int i = 0; i < WorldItemsPacket.MaxItems; i++) WorldItemData.Write(ref writer, Weapon((ushort)(i + 1)));

        Assert.False(writer.Overflowed);
        Assert.Equal(WorldItemsPacket.MaxSize, writer.Length);
        Assert.Equal(952, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);

        var reader = ReaderAfterId(writer.Length, PacketId.WorldItems);
        Assert.True(WorldItemsPacket.TryReadHeader(ref reader, out int count));
        Assert.Equal(50, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(WorldItemData.TryRead(ref reader, out var item));
            Assert.Equal(i + 1, item.ItemId);
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(51, 51)]
    [InlineData(3, 2)]   // count says 3, only 2 items follow
    public void WorldItems_BadCountOrShortPayload_IsRejected(int count, int items)
    {
        var writer = new PacketWriter(_buffer);
        WorldItemsPacket.WriteHeader(ref writer, count);
        for (int i = 0; i < items; i++) WorldItemData.Write(ref writer, Weapon((ushort)(i + 1)));
        var reader = ReaderAfterId(writer.Length, PacketId.WorldItems);
        Assert.False(WorldItemsPacket.TryReadHeader(ref reader, out _));
    }

    [Fact]
    public void ItemRemoved_RoundTrip_AndRejectsIdZero()
    {
        var writer = new PacketWriter(_buffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = 65535 });
        Assert.Equal(3, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ItemRemoved);
        Assert.True(ItemRemoved.TryRead(ref reader, out var removed));
        Assert.Equal(65535, removed.ItemId);

        writer = new PacketWriter(_buffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = 0 });
        reader = ReaderAfterId(writer.Length, PacketId.ItemRemoved);
        Assert.False(ItemRemoved.TryRead(ref reader, out _));
    }

    [Fact]
    public void InventoryState_RoundTrip_Is22Bytes()
    {
        var state = new InventoryState
        {
            Slot0 = new InventorySlotState { WeaponId = 1, Rarity = 2, MagAmmo = 30 },
            Slot2 = new InventorySlotState { WeaponId = 3, Rarity = 0, MagAmmo = 0 },
            CurrentSlot = 2,
            LightAmmo = 180,
            MediumAmmo = 7,
            HeavyAmmo = 30,
            Medkits = 3,
            ShieldCells = 6,
            Using = ConsumableType.ShieldCell,
            UseRemainingTicks = 59,
        };
        var writer = new PacketWriter(_buffer);
        InventoryState.Write(ref writer, state);
        Assert.Equal(1 + InventoryState.PayloadSize, writer.Length);
        Assert.Equal(22, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.InventoryState);
        Assert.True(InventoryState.TryRead(ref reader, out var read));
        Assert.Equal(30, read.Slot0.MagAmmo);
        Assert.Equal(2, read.Slot0.Rarity);
        Assert.True(read.Slot1.IsEmpty);
        Assert.Equal(3, read.GetSlot(2).WeaponId);
        Assert.Equal(2, read.CurrentSlot);
        Assert.Equal(7, read.GetAmmo(AmmoType.Medium));
        Assert.Equal(180, read.GetAmmo(AmmoType.Light));
        Assert.Equal(6, read.ShieldCells);
        Assert.Equal(ConsumableType.ShieldCell, read.Using);
        Assert.Equal(59, read.UseRemainingTicks);
    }

    [Fact]
    public void InventoryState_BadValues_OrTruncated_AreRejected()
    {
        var badSlot = new InventoryState { CurrentSlot = 3 };
        var badRarity = new InventoryState { Slot1 = new InventorySlotState { WeaponId = 1, Rarity = 5 } };
        var ammoInEmptySlot = new InventoryState { Slot1 = new InventorySlotState { WeaponId = 0, MagAmmo = 4 } };
        var badUse = new InventoryState { Using = (ConsumableType)3 };
        foreach (var bad in new[] { badSlot, badRarity, ammoInEmptySlot, badUse })
        {
            var writer = new PacketWriter(_buffer);
            InventoryState.Write(ref writer, bad);
            var reader = ReaderAfterId(writer.Length, PacketId.InventoryState);
            Assert.False(InventoryState.TryRead(ref reader, out _));
        }

        var w = new PacketWriter(_buffer);
        InventoryState.Write(ref w, new InventoryState());
        var shortReader = ReaderAfterId(w.Length - 1, PacketId.InventoryState);
        Assert.False(InventoryState.TryRead(ref shortReader, out _));
    }

    [Fact]
    public void PickupResult_RoundTrip_AndRejectsUnknownResult()
    {
        var writer = new PacketWriter(_buffer);
        PickupResult.Write(ref writer, new PickupResult { Result = PickupResultCode.Full, ItemId = 9 });
        Assert.Equal(4, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PickupResult);
        Assert.True(PickupResult.TryRead(ref reader, out var result));
        Assert.Equal(PickupResultCode.Full, result.Result);
        Assert.Equal(9, result.ItemId);

        writer = new PacketWriter(_buffer);
        PickupResult.Write(ref writer, new PickupResult { Result = (PickupResultCode)3 });
        reader = ReaderAfterId(writer.Length, PacketId.PickupResult);
        Assert.False(PickupResult.TryRead(ref reader, out _));
    }
}
```

- [ ] **Step 3: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 실패 — `ItemCatalogData`, `WorldItemData`, `InputButtons.Interact` 등이 없다는 CS0246/CS0117 오류

- [ ] **Step 4: 입력 버튼 u16**

`Shared/Runtime/Simulation/InputCommand.cs` 전체를 다음으로 바꾼다.

```csharp
using System;

namespace ProjectH.Shared.Simulation
{
    // 2 bytes on the wire since Phase 4 (D14). Values are the wire format: never renumber.
    [Flags]
    public enum InputButtons : ushort
    {
        None = 0,
        Jump = 1,
        Sprint = 2,
        Fire = 4,
        Reload = 8,
        Slot1 = 16,
        Slot2 = 32,
        Slot3 = 64,
        Interact = 128,       // E: pick up the nearest item (the server chooses it, D8)
        Drop = 256,           // G: drop the current weapon (D12)
        UseMedkit = 512,      // 4
        UseShieldCell = 1024, // 5
    }

    // One fixed-tick input. Seq increases by one per client simulation step and is how the
    // server acknowledges inputs back to the client for reconciliation.
    public struct InputCommand
    {
        public uint Seq;
        public float MoveX;   // strafe, -1..1 (sanitized by MovementSimulation)
        public float MoveY;   // forward, -1..1
        public float Yaw;     // degrees, camera heading
        public InputButtons Buttons;

        // Phase 3 (D2): direction from the eye (feet + 1.6 m) to the crosshair target, in degrees.
        // Same convention as the camera: yaw 0 faces +Z, positive pitch looks down. Only the server's
        // combat code reads these; MovementSimulation ignores them.
        public float AimYaw;
        public float AimPitch;

        // Server tick the client was rendering remote players at when this input was made (D6).
        public float ViewTick;
    }
}
```

`Shared/Runtime/Protocol/ClientPackets.cs`:

변경 1/3 — 찾을 코드:

```csharp
    // every packet means one lost datagram does not lose an input. The server drops seqs it already has.
    public struct PlayerInputPacket
    {
        // seq 4 + moveX 4 + moveY 4 + yaw 4 + buttons 1 + aimYaw 4 + aimPitch 4 + viewTick 4
        public const int CommandSize = 29;
        // PacketId 1 + count 1 + 3 commands = 89 bytes, far below one datagram.
        public const int MaxSize = 2 + ProtocolConstants.MaxInputsPerPacket * CommandSize;

        private const byte KnownButtons = (byte)(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire |
                                                 InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2);

        public byte Count;
        public InputCommand Input0;
```

바꿀 코드:

```csharp
    // every packet means one lost datagram does not lose an input. The server drops seqs it already has.
    public struct PlayerInputPacket
    {
        // seq 4 + moveX 4 + moveY 4 + yaw 4 + buttons 2 + aimYaw 4 + aimPitch 4 + viewTick 4
        public const int CommandSize = 30;
        // PacketId 1 + count 1 + 3 commands = 92 bytes, far below one datagram.
        public const int MaxSize = 2 + ProtocolConstants.MaxInputsPerPacket * CommandSize;

        private const ushort KnownButtons = (ushort)(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire |
                                                     InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2 |
                                                     InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                     InputButtons.UseMedkit | InputButtons.UseShieldCell);

        public byte Count;
        public InputCommand Input0;
```

변경 2/3 — 찾을 코드:

```csharp
                writer.WriteSingle(c.MoveX);
                writer.WriteSingle(c.MoveY);
                writer.WriteSingle(c.Yaw);
                writer.WriteByte((byte)c.Buttons);
                writer.WriteSingle(c.AimYaw);
                writer.WriteSingle(c.AimPitch);
                writer.WriteSingle(c.ViewTick);
```

바꿀 코드:

```csharp
                writer.WriteSingle(c.MoveX);
                writer.WriteSingle(c.MoveY);
                writer.WriteSingle(c.Yaw);
                writer.WriteUInt16((ushort)c.Buttons);
                writer.WriteSingle(c.AimYaw);
                writer.WriteSingle(c.AimPitch);
                writer.WriteSingle(c.ViewTick);
```

변경 3/3 — 찾을 코드:

```csharp
                reader.TryReadSingle(out c.MoveX);
                reader.TryReadSingle(out c.MoveY);
                reader.TryReadSingle(out c.Yaw);
                reader.TryReadByte(out byte buttons);
                c.Buttons = (InputButtons)(buttons & KnownButtons);
                reader.TryReadSingle(out c.AimYaw);
                reader.TryReadSingle(out c.AimPitch);
```

바꿀 코드:

```csharp
                reader.TryReadSingle(out c.MoveX);
                reader.TryReadSingle(out c.MoveY);
                reader.TryReadSingle(out c.Yaw);
                reader.TryReadUInt16(out ushort buttons);
                c.Buttons = (InputButtons)(buttons & KnownButtons);
                reader.TryReadSingle(out c.AimYaw);
                reader.TryReadSingle(out c.AimPitch);
```

- [ ] **Step 5: PacketId, 읽기 상한, 버전**

`Shared/Runtime/Protocol/PacketId.cs`:

변경 — 찾을 코드:

```csharp
        DamageTaken = 10,
        PlayerDied = 11,
        PlayerRespawned = 12,
    }
}
```

바꿀 코드:

```csharp
        DamageTaken = 10,
        PlayerDied = 11,
        PlayerRespawned = 12,
        ItemCatalog = 13,
        WorldItems = 14,
        ItemSpawned = 15,
        ItemRemoved = 16,
        InventoryState = 17,
        PickupResult = 18,
    }
}
```

`Shared/Runtime/Protocol/PacketReader.cs`:

변경 — 찾을 코드:

```csharp
            id = PacketId.None;
            if (!TryReadByte(out byte raw)) return false;
            // Upper bound is the highest id in PacketId; raise it whenever a packet is added.
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.PlayerRespawned) return false;
            id = (PacketId)raw;
            return true;
        }
```

바꿀 코드:

```csharp
            id = PacketId.None;
            if (!TryReadByte(out byte raw)) return false;
            // Upper bound is the highest id in PacketId; raise it whenever a packet is added.
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.PickupResult) return false;
            id = (PacketId)raw;
            return true;
        }
```

`Shared/Runtime/Protocol/ProtocolConstants.cs`:

변경 — 찾을 코드:

```csharp
    {
        // Bump whenever any packet layout changes; the server rejects other versions at connect time.
        // 2: Phase 1 box collision changed movement results. 3: Phase 3 combat (aim in inputs, snapshot flags and self block, combat packets).
        public const ushort ProtocolVersion = 3;

        public const int MaxDevPlayerIdBytes = 32;
        public const int MaxInputsPerPacket = 3;
```

바꿀 코드:

```csharp
    {
        // Bump whenever any packet layout changes; the server rejects other versions at connect time.
        // 2: Phase 1 box collision changed movement results. 3: Phase 3 combat (aim in inputs, snapshot flags and self block, combat packets).
        // 4: Phase 4 inventory (2-byte buttons, ammo type in the weapon catalog, item packets).
        public const ushort ProtocolVersion = 4;

        public const int MaxDevPlayerIdBytes = 32;
        public const int MaxInputsPerPacket = 3;
```

- [ ] **Step 6: 아이템 패킷**

`Shared/Runtime/Protocol/ItemPackets.cs`를 만든다.

```csharp
using System.Numerics;

namespace ProjectH.Shared.Protocol
{
    // Phase 4 item enums. The values are the wire format, and 0 always means "none".
    public enum AmmoType : byte
    {
        None = 0,
        Light = 1,
        Medium = 2,
        Heavy = 3,
    }

    public enum ItemKind : byte
    {
        None = 0,
        Weapon = 1,       // DefId = weapon id, Rarity 0-4, Amount = rounds in the magazine (may be 0)
        Ammo = 2,         // DefId = AmmoType, Amount = rounds
        Consumable = 3,   // DefId = ConsumableType, Amount = count
    }

    public enum ConsumableType : byte
    {
        None = 0,
        Medkit = 1,
        ShieldCell = 2,
    }

    // D8: the server picks the target itself, so "too far" and "gone" are one case for the client:
    // nothing it could pick up was in range.
    public enum PickupResultCode : byte
    {
        Ok = 0,
        NothingInRange = 1,
        Full = 2,
    }

    public static class ItemConstants
    {
        public const int RarityCount = 5;          // D4: Common, Uncommon, Rare, Epic, Legendary (index 0-4)
        public const int AmmoTypeCount = 3;        // D3: Light, Medium, Heavy
        public const int ConsumableTypeCount = 2;  // D10: Medkit, ShieldCell
        public const int WeaponSlotCount = 3;      // D10
        public const int MaxNameBytes = 16;
    }

    public struct RarityInfo
    {
        public string Name;
        public float DamageMultiplier;
    }

    public struct AmmoInfo
    {
        public AmmoType Type;
        public string Name;
        public ushort Max;
    }

    public struct ConsumableInfo
    {
        public ConsumableType Type;
        public string Name;
        public ushort UseTicks;
        public ushort Heal;
        public ushort Shield;
        public byte MaxStack;
    }

    // Everything the client shows about items (D2). Arrays are indexed by value - 1 for ammo and
    // consumables, by rarity for rarities.
    public sealed class ItemCatalogData
    {
        public RarityInfo[] Rarities;
        public AmmoInfo[] Ammo;
        public ConsumableInfo[] Consumables;
    }

    // S->C, ReliableOrdered, once right after the WeaponCatalog.
    // Largest possible packet: 1 + (1 + 5 x 21) + (1 + 3 x 20) + (1 + 2 x 25) = 219 bytes.
    public static class ItemCatalogPacket
    {
        public const int MaxSize = 219;

        public static void Write(ref PacketWriter writer, ItemCatalogData data)
        {
            writer.WriteByte((byte)PacketId.ItemCatalog);
            writer.WriteByte((byte)data.Rarities.Length);
            for (int i = 0; i < data.Rarities.Length; i++)
            {
                writer.WriteString(data.Rarities[i].Name, ItemConstants.MaxNameBytes);
                writer.WriteSingle(data.Rarities[i].DamageMultiplier);
            }
            writer.WriteByte((byte)data.Ammo.Length);
            for (int i = 0; i < data.Ammo.Length; i++)
            {
                writer.WriteByte((byte)data.Ammo[i].Type);
                writer.WriteString(data.Ammo[i].Name, ItemConstants.MaxNameBytes);
                writer.WriteUInt16(data.Ammo[i].Max);
            }
            writer.WriteByte((byte)data.Consumables.Length);
            for (int i = 0; i < data.Consumables.Length; i++)
            {
                ConsumableInfo c = data.Consumables[i];
                writer.WriteByte((byte)c.Type);
                writer.WriteString(c.Name, ItemConstants.MaxNameBytes);
                writer.WriteUInt16(c.UseTicks);
                writer.WriteUInt16(c.Heal);
                writer.WriteUInt16(c.Shield);
                writer.WriteByte(c.MaxStack);
            }
        }

        // Allocates the arrays and names: read once per join, never on the per-tick path.
        public static bool TryRead(ref PacketReader reader, out ItemCatalogData data)
        {
            data = null;
            if (!reader.TryReadByte(out byte rarityCount) || rarityCount != ItemConstants.RarityCount) return false;
            var rarities = new RarityInfo[rarityCount];
            for (int i = 0; i < rarityCount; i++)
            {
                if (!reader.TryReadString(ItemConstants.MaxNameBytes, out rarities[i].Name) || rarities[i].Name.Length == 0) return false;
                if (!reader.TryReadSingle(out rarities[i].DamageMultiplier) || !Finite.Check(rarities[i].DamageMultiplier) ||
                    rarities[i].DamageMultiplier <= 0f) return false;
            }

            if (!reader.TryReadByte(out byte ammoCount) || ammoCount != ItemConstants.AmmoTypeCount) return false;
            var ammo = new AmmoInfo[ammoCount];
            for (int i = 0; i < ammoCount; i++)
            {
                if (!reader.TryReadByte(out byte type) || type != i + 1) return false;
                ammo[i].Type = (AmmoType)type;
                if (!reader.TryReadString(ItemConstants.MaxNameBytes, out ammo[i].Name) || ammo[i].Name.Length == 0) return false;
                if (!reader.TryReadUInt16(out ammo[i].Max) || ammo[i].Max == 0) return false;
            }

            if (!reader.TryReadByte(out byte consumableCount) || consumableCount != ItemConstants.ConsumableTypeCount) return false;
            var consumables = new ConsumableInfo[consumableCount];
            for (int i = 0; i < consumableCount; i++)
            {
                if (!reader.TryReadByte(out byte type) || type != i + 1) return false;
                consumables[i].Type = (ConsumableType)type;
                if (!reader.TryReadString(ItemConstants.MaxNameBytes, out consumables[i].Name) || consumables[i].Name.Length == 0) return false;
                if (!reader.TryReadUInt16(out consumables[i].UseTicks) || consumables[i].UseTicks == 0) return false;
                if (!reader.TryReadUInt16(out consumables[i].Heal)) return false;
                if (!reader.TryReadUInt16(out consumables[i].Shield)) return false;
                if (consumables[i].Heal == 0 && consumables[i].Shield == 0) return false;
                if (!reader.TryReadByte(out consumables[i].MaxStack) || consumables[i].MaxStack == 0) return false;
            }

            data = new ItemCatalogData { Rarities = rarities, Ammo = ammo, Consumables = consumables };
            return true;
        }
    }

    // One item lying in the world (D14): 19 bytes, no PacketId. Used by WorldItems and ItemSpawned.
    public struct WorldItemData
    {
        public const int Size = 19;   // id 2 + kind 1 + defId 1 + rarity 1 + amount 2 + position 12

        public ushort ItemId;
        public ItemKind Kind;
        public byte DefId;
        public byte Rarity;
        public ushort Amount;
        public Vector3 Position;

        public static void Write(ref PacketWriter writer, in WorldItemData item)
        {
            writer.WriteUInt16(item.ItemId);
            writer.WriteByte((byte)item.Kind);
            writer.WriteByte(item.DefId);
            writer.WriteByte(item.Rarity);
            writer.WriteUInt16(item.Amount);
            writer.WriteVector3(item.Position);
        }

        // Rejects values the server never sends: id 0, an unknown kind or type, a rarity on a
        // non-weapon, an empty ammo or consumable stack, a non-finite position.
        public static bool TryRead(ref PacketReader reader, out WorldItemData item)
        {
            item = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadUInt16(out item.ItemId);
            reader.TryReadByte(out byte kind);
            item.Kind = (ItemKind)kind;
            reader.TryReadByte(out item.DefId);
            reader.TryReadByte(out item.Rarity);
            reader.TryReadUInt16(out item.Amount);
            reader.TryReadVector3(out item.Position);

            if (item.ItemId == 0 || !Finite.Check(item.Position)) return false;
            switch (item.Kind)
            {
                case ItemKind.Weapon:
                    return item.DefId != 0 && item.Rarity < ItemConstants.RarityCount;
                case ItemKind.Ammo:
                    return item.DefId >= 1 && item.DefId <= ItemConstants.AmmoTypeCount && item.Rarity == 0 && item.Amount > 0;
                case ItemKind.Consumable:
                    return item.DefId >= 1 && item.DefId <= ItemConstants.ConsumableTypeCount && item.Rarity == 0 && item.Amount > 0;
                default:
                    return false;
            }
        }
    }

    // S->C, ReliableOrdered, at join: the whole world item list, MaxItems per packet (D13, D14).
    // Layout: [PacketId 1][Count 1] then Count x WorldItemData.
    public static class WorldItemsPacket
    {
        public const int MaxItems = 50;
        public const int MaxSize = 2 + MaxItems * WorldItemData.Size;   // 952 bytes

        // Follow with exactly count WorldItemData.Write calls.
        public static void WriteHeader(ref PacketWriter writer, int count)
        {
            writer.WriteByte((byte)PacketId.WorldItems);
            writer.WriteByte((byte)count);
        }

        public static bool TryReadHeader(ref PacketReader reader, out int count)
        {
            count = 0;
            if (!reader.TryReadByte(out byte raw) || raw == 0 || raw > MaxItems) return false;
            if (reader.Remaining < raw * WorldItemData.Size) return false;
            count = raw;
            return true;
        }
    }

    // S->C, ReliableOrdered, to everyone: an item appeared, or its amount changed (upsert by ItemId).
    public static class ItemSpawnedPacket
    {
        public const int Size = 1 + WorldItemData.Size;   // 20 bytes

        public static void Write(ref PacketWriter writer, in WorldItemData item)
        {
            writer.WriteByte((byte)PacketId.ItemSpawned);
            WorldItemData.Write(ref writer, item);
        }

        public static bool TryRead(ref PacketReader reader, out WorldItemData item) => WorldItemData.TryRead(ref reader, out item);
    }

    // S->C, ReliableOrdered, to everyone.
    public struct ItemRemoved
    {
        public ushort ItemId;

        public static void Write(ref PacketWriter writer, in ItemRemoved r)
        {
            writer.WriteByte((byte)PacketId.ItemRemoved);
            writer.WriteUInt16(r.ItemId);
        }

        public static bool TryRead(ref PacketReader reader, out ItemRemoved r)
        {
            r = default;
            return reader.TryReadUInt16(out r.ItemId) && r.ItemId != 0;
        }
    }

    public struct InventorySlotState
    {
        public byte WeaponId;   // 0 = empty slot
        public byte Rarity;
        public byte MagAmmo;

        public bool IsEmpty => WeaponId == 0;
    }

    // S->C, ReliableOrdered, to its owner only, at the end of a tick in which the inventory changed
    // (D14). Shots do not count as a change: the snapshot self block carries the current magazine.
    public struct InventoryState
    {
        public const int PayloadSize = 21;   // 3 x 3 + 1 + 3 x 2 + 1 + 1 + 1 + 2

        public InventorySlotState Slot0;
        public InventorySlotState Slot1;
        public InventorySlotState Slot2;
        public byte CurrentSlot;
        public ushort LightAmmo;
        public ushort MediumAmmo;
        public ushort HeavyAmmo;
        public byte Medkits;
        public byte ShieldCells;
        public ConsumableType Using;        // None when no heal is being used
        public ushort UseRemainingTicks;    // 0 when Using is None

        public InventorySlotState GetSlot(int slot)
        {
            switch (slot)
            {
                case 0: return Slot0;
                case 1: return Slot1;
                default: return Slot2;
            }
        }

        public void SetSlot(int slot, in InventorySlotState value)
        {
            switch (slot)
            {
                case 0: Slot0 = value; break;
                case 1: Slot1 = value; break;
                default: Slot2 = value; break;
            }
        }

        public ushort GetAmmo(AmmoType type)
        {
            switch (type)
            {
                case AmmoType.Light: return LightAmmo;
                case AmmoType.Medium: return MediumAmmo;
                case AmmoType.Heavy: return HeavyAmmo;
                default: return 0;
            }
        }

        public void SetAmmo(AmmoType type, ushort value)
        {
            switch (type)
            {
                case AmmoType.Light: LightAmmo = value; break;
                case AmmoType.Medium: MediumAmmo = value; break;
                case AmmoType.Heavy: HeavyAmmo = value; break;
            }
        }

        public static void Write(ref PacketWriter writer, in InventoryState s)
        {
            writer.WriteByte((byte)PacketId.InventoryState);
            for (int i = 0; i < ItemConstants.WeaponSlotCount; i++)
            {
                InventorySlotState slot = s.GetSlot(i);
                writer.WriteByte(slot.WeaponId);
                writer.WriteByte(slot.Rarity);
                writer.WriteByte(slot.MagAmmo);
            }
            writer.WriteByte(s.CurrentSlot);
            writer.WriteUInt16(s.LightAmmo);
            writer.WriteUInt16(s.MediumAmmo);
            writer.WriteUInt16(s.HeavyAmmo);
            writer.WriteByte(s.Medkits);
            writer.WriteByte(s.ShieldCells);
            writer.WriteByte((byte)s.Using);
            writer.WriteUInt16(s.UseRemainingTicks);
        }

        public static bool TryRead(ref PacketReader reader, out InventoryState s)
        {
            s = default;
            if (reader.Remaining < PayloadSize) return false;
            for (int i = 0; i < ItemConstants.WeaponSlotCount; i++)
            {
                var slot = new InventorySlotState();
                reader.TryReadByte(out slot.WeaponId);
                reader.TryReadByte(out slot.Rarity);
                reader.TryReadByte(out slot.MagAmmo);
                if (slot.IsEmpty ? slot.Rarity != 0 || slot.MagAmmo != 0 : slot.Rarity >= ItemConstants.RarityCount) return false;
                s.SetSlot(i, slot);
            }
            reader.TryReadByte(out s.CurrentSlot);
            reader.TryReadUInt16(out s.LightAmmo);
            reader.TryReadUInt16(out s.MediumAmmo);
            reader.TryReadUInt16(out s.HeavyAmmo);
            reader.TryReadByte(out s.Medkits);
            reader.TryReadByte(out s.ShieldCells);
            reader.TryReadByte(out byte usingKind);
            s.Using = (ConsumableType)usingKind;
            reader.TryReadUInt16(out s.UseRemainingTicks);
            return s.CurrentSlot < ItemConstants.WeaponSlotCount && usingKind <= ItemConstants.ConsumableTypeCount;
        }
    }

    // S->C, ReliableOrdered, to the player who pressed Interact. Only for feedback: the inventory
    // itself changes through InventoryState.
    public struct PickupResult
    {
        public PickupResultCode Result;
        public ushort ItemId;   // 0 with NothingInRange

        public static void Write(ref PacketWriter writer, in PickupResult r)
        {
            writer.WriteByte((byte)PacketId.PickupResult);
            writer.WriteByte((byte)r.Result);
            writer.WriteUInt16(r.ItemId);
        }

        public static bool TryRead(ref PacketReader reader, out PickupResult r)
        {
            r = default;
            if (reader.Remaining < 3) return false;
            reader.TryReadByte(out byte result);
            r.Result = (PickupResultCode)result;
            reader.TryReadUInt16(out r.ItemId);
            return result <= (byte)PickupResultCode.Full;
        }
    }
}
```

- [ ] **Step 7: 서버 빌드·테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 279개 PASS (`Snapshot_WithMaxEntities_Is1167Bytes_AndFitsOneDatagram` 포함, Snapshot은 바뀌지 않았다)

- [ ] **Step 8: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0 (Client는 아직 새 버튼을 쓰지 않지만 Shared가 바뀌었다)

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 기존 EditMode 테스트 64개 PASS

- [ ] **Step 9: 체크포인트**

`N0`, 두 명령의 PASS 결과를 기록한다. Unity Editor import 확인은 Task 9에서 한 번에 한다. 커밋하지 않는다.

---

### Task 2: 아이템 데이터 (`weapons.json` 탄약 종류·Wisp SMG, `items.json`, `ItemCatalog`, `GameData`, Join 직후 전송)

**Files:**
- Modify: `Server/src/ProjectH.Server/weapons.json` (전체 교체), `ProjectH.Server.csproj`
- Create: `Server/src/ProjectH.Server/items.json`, `Game/DataJson.cs`, `Game/GameData.cs`, `Game/Items/ItemCatalog.cs`
- Modify: `Game/Combat/WeaponCatalog.cs` (전체 교체), `Game/Combat/WeaponDefinition.cs`, `Game/Match.cs`, `GameLoop.cs`, `GameServerService.cs`
- Modify: `Shared/Runtime/Protocol/CombatPackets.cs`
- Test: `Server/tests/ProjectH.Server.Tests/TestWeapons.cs` (전체 교체), `TestGameData.cs` (신규), `Game/ItemCatalogTests.cs` (신규), `Game/WeaponCatalogTests.cs`, `Shared/CombatPacketTests.cs`, `Game/MatchTests.cs`, `Game/CombatMatchTests.cs`, `Game/LagCompensationTests.cs`, `Game/WeaponRulesTests.cs`, `Integration/CombatIntegrationTests.cs`, `Integration/GameLoopPeerTests.cs`, `Integration/ServerIntegrationTests.cs`, `Integration/HeadlessClient.cs`

**Interfaces:**
- Consumes: Task 1의 `AmmoType`, `ItemConstants`, `ItemCatalogData`/`RarityInfo`/`AmmoInfo`/`ConsumableInfo`, `ItemCatalogPacket`
- Produces:
  - `WeaponInfo.AmmoType` (Shared, 카탈로그 무기당 1 B 추가, 1–3만 유효)
  - `WeaponDefinition.AmmoType`; `WeaponCatalog.TryGetById(byte id, out WeaponDefinition weapon)`. `SlotCount`·`LoadoutCount`는 Task 5까지 남는다.
  - `internal static class DataJson { Options; bool TryTicks(double seconds, int simHz, out ushort ticks); bool TryDeserialize<T>(string json, out T? value, out string? error) }` (namespace `ProjectH.Server.Game`)
  - `ProjectH.Server.Game.Items.AmmoDefinition { AmmoType Type; string Name; ushort PickupAmount, Max }`, `ConsumableDefinition { ConsumableType Type; string Name; ushort UseTicks, Heal, Shield; byte MaxStack }`
  - `ItemCatalog { int SimHz; ItemCatalogData Wire; float DamageMultiplier(int rarity); string RarityName(int rarity); int RarityIndex(string name); AmmoDefinition Ammo(AmmoType); ConsumableDefinition Consumable(ConsumableType); static LoadFile(path, simHz); static bool TryParse(json, simHz, out ItemCatalog?, out string? error); static bool TryParseAmmoType(string?, out AmmoType) }`
  - `GameData { GameData(WeaponCatalog, ItemCatalog); WeaponCatalog Weapons; ItemCatalog Items; int SimHz; static GameData LoadDirectory(string directory, int simHz) }` (Task 3에서 `LootTable Loot`가 더해진다)
  - `Match(ServerOptions options, GameData data, SendPacket send)`, `GameLoop(ServerOptions options, GameData data, ILogger logger)`
  - 테스트: `TestWeapons` (Test Auto id 1 Medium, Test Semi id 2 Heavy, Test Light id 3 Light: 10 피해·3 Tick·10발·15 Tick·50 m·자동; 상수 `AutoId`, `SemiId`, `LightId`, `LightMagazine`), `TestGameData.Create(int simHz = 30, float autoRange = 100f)`, `TestGameData.Items(int simHz = 30)`, `TestGameData.ItemsJson`, 상수 `LightMax`·`LightPickup`·`MediumMax`·`MediumPickup`·`HeavyMax`·`HeavyPickup`·`MedkitUseTicks`·`MedkitHeal`·`MedkitMaxStack`·`ShieldCellUseTicks`·`ShieldCellShield`·`ShieldCellMaxStack`
  - `HeadlessClient.Items` (`ItemCatalogData?`)

- [ ] **Step 1: 실패하는 테스트 작성 — 테스트 데이터**

`Server/tests/ProjectH.Server.Tests/TestWeapons.cs` 전체를 다음으로 바꾼다(탄약 종류와 세 번째 무기).

```csharp
using System;
using System.Globalization;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server.Tests;

// In-memory catalog for tests, so no test depends on the shipped weapons.json numbers.
// At 30 Hz: id 1 "Test Auto"  = 30 damage, 3-tick interval, 6 rounds, 30-tick reload, Medium ammo;
//           id 2 "Test Semi"  = 90 damage, 15-tick interval, 2 rounds, 60-tick reload, Heavy ammo;
//           id 3 "Test Light" = 10 damage, 3-tick interval, 10 rounds, 15-tick reload, Light ammo.
// 30 damage makes shield 50 + health 100 exactly five hits.
internal static class TestWeapons
{
    public const byte AutoId = 1;
    public const int AutoDamage = 30;
    public const int AutoInterval = 3;
    public const int AutoMagazine = 6;
    public const int AutoReload = 30;
    public const byte SemiId = 2;
    public const int SemiDamage = 90;
    public const int SemiInterval = 15;
    public const int SemiMagazine = 2;
    public const byte LightId = 3;
    public const int LightMagazine = 10;

    public static string Json(float autoRange = 100f) => $$"""
        {
          "weapons": [
            { "id": 1, "name": "Test Auto", "damage": 30, "fireIntervalSeconds": 0.1, "magazineSize": 6,
              "reloadSeconds": 1.0, "range": {{autoRange.ToString(CultureInfo.InvariantCulture)}}, "automatic": true, "ammoType": "Medium" },
            { "id": 2, "name": "Test Semi", "damage": 90, "fireIntervalSeconds": 0.5, "magazineSize": 2,
              "reloadSeconds": 2.0, "range": 300, "automatic": false, "ammoType": "Heavy" },
            { "id": 3, "name": "Test Light", "damage": 10, "fireIntervalSeconds": 0.1, "magazineSize": 10,
              "reloadSeconds": 0.5, "range": 50, "automatic": true, "ammoType": "Light" }
          ]
        }
        """;

    public static WeaponCatalog Create(int simHz = 30, float autoRange = 100f)
    {
        if (!WeaponCatalog.TryParse(Json(autoRange), simHz, out var catalog, out string? error))
            throw new InvalidOperationException("Test catalog is invalid: " + error);
        return catalog!;
    }
}
```

`Server/tests/ProjectH.Server.Tests/TestGameData.cs`를 만든다.

```csharp
using System;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;

namespace ProjectH.Server.Tests;

// In-memory game data for tests (TestWeapons + the item numbers of spec D3/D11), so no test depends on
// the shipped data files.
internal static class TestGameData
{
    public const int LightMax = 180;
    public const int LightPickup = 60;
    public const int MediumMax = 150;
    public const int MediumPickup = 45;
    public const int HeavyMax = 30;
    public const int HeavyPickup = 10;
    public const int MedkitUseTicks = 90;       // 3 s at 30 Hz
    public const int MedkitHeal = 50;
    public const int MedkitMaxStack = 3;
    public const int ShieldCellUseTicks = 60;   // 2 s at 30 Hz
    public const int ShieldCellShield = 25;
    public const int ShieldCellMaxStack = 6;

    public const string ItemsJson = """
        {
          "rarities": [
            { "name": "Common", "damageMultiplier": 1.00 },
            { "name": "Uncommon", "damageMultiplier": 1.05 },
            { "name": "Rare", "damageMultiplier": 1.10 },
            { "name": "Epic", "damageMultiplier": 1.15 },
            { "name": "Legendary", "damageMultiplier": 1.20 }
          ],
          "ammo": [
            { "type": "Light", "name": "Light Rounds", "pickupAmount": 60, "max": 180 },
            { "type": "Medium", "name": "Medium Rounds", "pickupAmount": 45, "max": 150 },
            { "type": "Heavy", "name": "Heavy Rounds", "pickupAmount": 10, "max": 30 }
          ],
          "consumables": [
            { "id": "Medkit", "name": "Medkit", "useSeconds": 3.0, "heal": 50, "shield": 0, "maxStack": 3 },
            { "id": "ShieldCell", "name": "Shield Cell", "useSeconds": 2.0, "heal": 0, "shield": 25, "maxStack": 6 }
          ]
        }
        """;

    public static ItemCatalog Items(int simHz = 30)
    {
        if (!ItemCatalog.TryParse(ItemsJson, simHz, out var items, out string? error))
            throw new InvalidOperationException("Test items are invalid: " + error);
        return items!;
    }

    public static GameData Create(int simHz = 30, float autoRange = 100f)
    {
        return new GameData(TestWeapons.Create(simHz, autoRange), Items(simHz));
    }
}
```

- [ ] **Step 2: 실패하는 테스트 작성 — 카탈로그**

`Server/tests/ProjectH.Server.Tests/Game/ItemCatalogTests.cs`를 만든다. 정상 로드, 순서가 달라도 종류별 저장, 잘못된 항목(0 배율·중복 이름·중복 종류·모르는 종류·범위 밖 수치), 실제 `items.json`이 spec 값인지, `GameData` 로드를 확인한다.

```csharp
using System;
using System.IO;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class ItemCatalogTests
{
    private static string Parse(string json, int simHz = 30)
    {
        Assert.False(ItemCatalog.TryParse(json, simHz, out var catalog, out string? error));
        Assert.Null(catalog);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    // Replaces one exact piece of the valid test file, so each case breaks exactly one rule.
    private static string Broken(string from, string to)
    {
        Assert.Contains(from, TestGameData.ItemsJson);
        return TestGameData.ItemsJson.Replace(from, to);
    }

    [Fact]
    public void Valid_ConvertsSecondsToTicks_AndBuildsWireData()
    {
        Assert.True(ItemCatalog.TryParse(TestGameData.ItemsJson, 30, out var items, out string? error), error);
        Assert.Equal(30, items!.SimHz);
        Assert.Equal(1.2f, items.DamageMultiplier(4));
        Assert.Equal("Rare", items.RarityName(2));
        Assert.Equal(3, items.RarityIndex("Epic"));
        Assert.Equal(-1, items.RarityIndex("Mythic"));
        Assert.Equal(TestGameData.MediumPickup, items.Ammo(AmmoType.Medium).PickupAmount);
        Assert.Equal(TestGameData.HeavyMax, items.Ammo(AmmoType.Heavy).Max);
        Assert.Equal(TestGameData.MedkitUseTicks, items.Consumable(ConsumableType.Medkit).UseTicks);
        Assert.Equal(TestGameData.ShieldCellShield, items.Consumable(ConsumableType.ShieldCell).Shield);
        Assert.Equal(TestGameData.ShieldCellMaxStack, items.Consumable(ConsumableType.ShieldCell).MaxStack);

        Assert.Equal(5, items.Wire.Rarities.Length);
        Assert.Equal(AmmoType.Light, items.Wire.Ammo[0].Type);
        Assert.Equal(TestGameData.LightMax, items.Wire.Ammo[0].Max);
        Assert.Equal(ConsumableType.ShieldCell, items.Wire.Consumables[1].Type);
        Assert.Equal(TestGameData.ShieldCellUseTicks, items.Wire.Consumables[1].UseTicks);
    }

    [Fact]
    public void EntriesInAnyOrder_AreStoredByType()
    {
        string swapped = TestGameData.ItemsJson
            .Replace("\"type\": \"Light\"", "\"type\": \"TMP\"")
            .Replace("\"type\": \"Heavy\"", "\"type\": \"Light\"")
            .Replace("\"type\": \"TMP\"", "\"type\": \"Heavy\"");
        Assert.True(ItemCatalog.TryParse(swapped, 30, out var items, out string? error), error);
        Assert.Equal("Heavy Rounds", items!.Ammo(AmmoType.Light).Name);
        Assert.Equal("Light Rounds", items.Ammo(AmmoType.Heavy).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void EmptyOrMalformed_IsRejected(string json)
    {
        Parse(json);
    }

    [Theory]
    // rarities
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"Legendary\", \"damageMultiplier\": 0 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"Epic\", \"damageMultiplier\": 1.20 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"\", \"damageMultiplier\": 1.20 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"12345678901234567\", \"damageMultiplier\": 1.20 }")]
    [InlineData("{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }", "{ \"name\": \"Legendary\", \"damageMultiplier\": 1.20 }, { \"name\": \"Mythic\", \"damageMultiplier\": 1.25 }")]
    // ammo
    [InlineData("\"type\": \"Heavy\"", "\"type\": \"Medium\"")]
    [InlineData("\"type\": \"Heavy\"", "\"type\": \"Rockets\"")]
    [InlineData("\"pickupAmount\": 10, \"max\": 30", "\"pickupAmount\": 31, \"max\": 30")]
    [InlineData("\"pickupAmount\": 10, \"max\": 30", "\"pickupAmount\": 0, \"max\": 30")]
    [InlineData("\"pickupAmount\": 10, \"max\": 30", "\"pickupAmount\": 10, \"max\": 65536")]
    [InlineData("\"name\": \"Heavy Rounds\"", "\"name\": \"Light Rounds\"")]
    // consumables
    [InlineData("\"id\": \"ShieldCell\"", "\"id\": \"Medkit\"")]
    [InlineData("\"id\": \"ShieldCell\"", "\"id\": \"Grenade\"")]
    [InlineData("\"useSeconds\": 2.0", "\"useSeconds\": 0")]
    [InlineData("\"heal\": 0, \"shield\": 25", "\"heal\": 0, \"shield\": 0")]
    [InlineData("\"heal\": 50", "\"heal\": -1")]
    [InlineData("\"maxStack\": 6", "\"maxStack\": 0")]
    [InlineData("\"maxStack\": 6", "\"maxStack\": 256")]
    public void BadEntry_IsRejected(string from, string to)
    {
        Parse(Broken(from, to));
    }

    [Fact]
    public void LoadFile_MissingFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), "projecth-no-such-items-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidOperationException>(() => ItemCatalog.LoadFile(path, 30));
    }

    // The file shipped next to the server must load and match spec D3, D4 and D11.
    [Fact]
    public void ShippedItemsJson_MatchesSpec()
    {
        var items = ItemCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "items.json"), 30);

        string[] names = { "Common", "Uncommon", "Rare", "Epic", "Legendary" };
        float[] multipliers = { 1.00f, 1.05f, 1.10f, 1.15f, 1.20f };
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(names[i], items.RarityName(i));
            Assert.Equal(multipliers[i], items.DamageMultiplier(i));
        }
        Assert.Equal(180, items.Ammo(AmmoType.Light).Max);
        Assert.Equal(150, items.Ammo(AmmoType.Medium).Max);
        Assert.Equal(30, items.Ammo(AmmoType.Heavy).Max);
        Assert.Equal(60, items.Ammo(AmmoType.Light).PickupAmount);

        var medkit = items.Consumable(ConsumableType.Medkit);
        Assert.Equal(90, medkit.UseTicks);   // 3 s
        Assert.Equal(50, medkit.Heal);
        Assert.Equal(0, medkit.Shield);
        Assert.Equal(3, medkit.MaxStack);
        var cell = items.Consumable(ConsumableType.ShieldCell);
        Assert.Equal(60, cell.UseTicks);     // 2 s
        Assert.Equal(0, cell.Heal);
        Assert.Equal(25, cell.Shield);
        Assert.Equal(6, cell.MaxStack);
    }

    [Fact]
    public void GameData_LoadDirectory_LoadsTheShippedFiles()
    {
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, 30);
        Assert.Equal(30, data.SimHz);
        Assert.Equal(3, data.Weapons.Count);
        Assert.Equal("Legendary", data.Items.RarityName(4));
    }

    [Fact]
    public void GameData_RejectsCatalogsBuiltForDifferentSimHz()
    {
        Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(simHz: 30), TestGameData.Items(simHz: 60)));
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/WeaponCatalogTests.cs` (ammoType 필드, 이름 중복, Wisp SMG):

변경 1/10 — 찾을 코드:

```csharp
using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using Xunit;

namespace ProjectH.Server.Tests.Game;
```

바꿀 코드:

```csharp
using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;
```

변경 2/10 — 찾을 코드:

```csharp

    private const string ValidFields =
        "\"id\": 1, \"name\": \"Vesper AR\", \"damage\": 20, \"fireIntervalSeconds\": 0.1, \"magazineSize\": 30, " +
        "\"reloadSeconds\": 2.0, \"range\": 150, \"automatic\": true";

    private static string Parse(string json, int simHz = 30)
    {
```

바꿀 코드:

```csharp

    private const string ValidFields =
        "\"id\": 1, \"name\": \"Vesper AR\", \"damage\": 20, \"fireIntervalSeconds\": 0.1, \"magazineSize\": 30, " +
        "\"reloadSeconds\": 2.0, \"range\": 150, \"automatic\": true, \"ammoType\": \"Medium\"";

    private static string Parse(string json, int simHz = 30)
    {
```

변경 3/10 — 찾을 코드:

```csharp
    public void Valid_ConvertsSecondsToTicks()
    {
        Assert.True(WeaponCatalog.TryParse(TestWeapons.Json(), 30, out var catalog, out string? error), error);
        Assert.Equal(2, catalog!.Count);
        Assert.Equal(2, catalog.LoadoutCount);
        Assert.Equal(30, catalog.SimHz);
        Assert.Equal("Test Auto", catalog[0].Name);
```

바꿀 코드:

```csharp
    public void Valid_ConvertsSecondsToTicks()
    {
        Assert.True(WeaponCatalog.TryParse(TestWeapons.Json(), 30, out var catalog, out string? error), error);
        Assert.Equal(3, catalog!.Count);
        Assert.Equal(2, catalog.LoadoutCount);
        Assert.Equal(30, catalog.SimHz);
        Assert.Equal("Test Auto", catalog[0].Name);
```

변경 4/10 — 찾을 코드:

```csharp
        Assert.Equal(60, catalog[1].ReloadTicks);
        Assert.False(catalog[1].Automatic);
        Assert.Equal(0f, catalog[1].Spread);
        Assert.Equal(2, catalog.WireInfos.Length);
        Assert.Equal("Test Semi", catalog.WireInfos[1].Name);
    }

    [Fact]
    public void HalfTick_RoundsAwayFromZero_AndTinyTimesBecomeOneTick()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"A\", \"damage\": 1, \"fireIntervalSeconds\": 1.25, " +
                      "\"magazineSize\": 1, \"reloadSeconds\": 0.001, \"range\": 1 } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var catalog, out _));
        Assert.Equal(38, catalog![0].FireIntervalTicks);   // 37.5
        Assert.Equal(1, catalog[0].ReloadTicks);
```

바꿀 코드:

```csharp
        Assert.Equal(60, catalog[1].ReloadTicks);
        Assert.False(catalog[1].Automatic);
        Assert.Equal(0f, catalog[1].Spread);
        Assert.Equal(3, catalog.WireInfos.Length);
        Assert.Equal("Test Semi", catalog.WireInfos[1].Name);
        Assert.Equal(AmmoType.Heavy, catalog[1].AmmoType);
        Assert.Equal(AmmoType.Heavy, catalog.WireInfos[1].AmmoType);
    }

    [Fact]
    public void TryGetById_FindsEveryWeapon_AndNothingElse()
    {
        var catalog = TestWeapons.Create();
        Assert.True(catalog.TryGetById(TestWeapons.LightId, out var light));
        Assert.Equal("Test Light", light.Name);
        Assert.Equal(AmmoType.Light, light.AmmoType);
        Assert.False(catalog.TryGetById(0, out _));
        Assert.False(catalog.TryGetById(4, out _));
        Assert.False(catalog.TryGetById(255, out _));
    }

    [Fact]
    public void HalfTick_RoundsAwayFromZero_AndTinyTimesBecomeOneTick()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"A\", \"damage\": 1, \"fireIntervalSeconds\": 1.25, " +
                      "\"magazineSize\": 1, \"reloadSeconds\": 0.001, \"range\": 1, \"ammoType\": \"Light\" } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var catalog, out _));
        Assert.Equal(38, catalog![0].FireIntervalTicks);   // 37.5
        Assert.Equal(1, catalog[0].ReloadTicks);
```

변경 5/10 — 찾을 코드:

```csharp
    [InlineData("\"spread\": -1")]
    [InlineData("\"id\": 0")]
    [InlineData("\"id\": 256")]
    public void BadNumber_IsRejected(string overrideField)
    {
        // A later duplicate key overrides the valid value (System.Text.Json keeps the last one).
```

바꿀 코드:

```csharp
    [InlineData("\"spread\": -1")]
    [InlineData("\"id\": 0")]
    [InlineData("\"id\": 256")]
    [InlineData("\"ammoType\": \"None\"")]
    [InlineData("\"ammoType\": \"light\"")]
    [InlineData("\"ammoType\": \"2\"")]
    [InlineData("\"ammoType\": null")]
    public void BadNumber_IsRejected(string overrideField)
    {
        // A later duplicate key overrides the valid value (System.Text.Json keeps the last one).
```

변경 6/10 — 찾을 코드:

```csharp
    [Fact]
    public void MissingField_IsRejected()
    {
        Parse(One("\"id\": 1, \"name\": \"A\", \"damage\": 20, \"magazineSize\": 30, \"reloadSeconds\": 2, \"range\": 150"));
    }

    [Theory]
```

바꿀 코드:

```csharp
    [Fact]
    public void MissingField_IsRejected()
    {
        Parse(One("\"id\": 1, \"name\": \"A\", \"damage\": 20, \"magazineSize\": 30, \"reloadSeconds\": 2, \"range\": 150, \"ammoType\": \"Light\""));
        Parse(One(ValidFields.Replace(", \"ammoType\": \"Medium\"", "")));
    }

    [Theory]
```

변경 7/10 — 찾을 코드:

```csharp
        Assert.True(WeaponCatalog.TryParse(One(ValidFields.Replace("Vesper AR", "1234567890123456")), 30, out _, out _));
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
```

바꿀 코드:

```csharp
        Assert.True(WeaponCatalog.TryParse(One(ValidFields.Replace("Vesper AR", "1234567890123456")), 30, out _, out _));
    }

    [Fact]
    public void DuplicateName_IsRejected()
    {
        string json = "{ \"weapons\": [ { " + ValidFields + " }, { " + ValidFields.Replace("\"id\": 1", "\"id\": 2") + " } ] }";
        Assert.Contains("duplicate name", Parse(json));
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
```

변경 8/10 — 찾을 코드:

```csharp
    }

    // The file shipped next to the server (copied to this test's output through the project reference)
    // must load and match spec D5.
    [Fact]
    public void ShippedWeaponsJson_MatchesSpec()
    {
        var catalog = WeaponCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "weapons.json"), 30);

        Assert.Equal(2, catalog.Count);
        Assert.Equal("Vesper AR", catalog[0].Name);
        Assert.Equal(20, catalog[0].Damage);
        Assert.Equal(3, catalog[0].FireIntervalTicks);    // 10 rounds/s
```

바꿀 코드:

```csharp
    }

    // The file shipped next to the server (copied to this test's output through the project reference)
    // must load and match Phase 3 spec D5 and Phase 4 spec D3.
    [Fact]
    public void ShippedWeaponsJson_MatchesSpec()
    {
        var catalog = WeaponCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "weapons.json"), 30);

        Assert.Equal(3, catalog.Count);
        Assert.Equal("Vesper AR", catalog[0].Name);
        Assert.Equal(20, catalog[0].Damage);
        Assert.Equal(3, catalog[0].FireIntervalTicks);    // 10 rounds/s
```

변경 9/10 — 찾을 코드:

```csharp
        Assert.Equal(60, catalog[0].ReloadTicks);         // 2.0 s
        Assert.Equal(150f, catalog[0].Range);
        Assert.True(catalog[0].Automatic);

        Assert.Equal("Kestrel LR", catalog[1].Name);
        Assert.Equal(90, catalog[1].Damage);
```

바꿀 코드:

```csharp
        Assert.Equal(60, catalog[0].ReloadTicks);         // 2.0 s
        Assert.Equal(150f, catalog[0].Range);
        Assert.True(catalog[0].Automatic);
        Assert.Equal(AmmoType.Medium, catalog[0].AmmoType);

        Assert.Equal("Kestrel LR", catalog[1].Name);
        Assert.Equal(90, catalog[1].Damage);
```

변경 10/10 — 찾을 코드:

```csharp
        Assert.Equal(75, catalog[1].ReloadTicks);         // 2.5 s
        Assert.Equal(300f, catalog[1].Range);
        Assert.False(catalog[1].Automatic);
    }
}
```

바꿀 코드:

```csharp
        Assert.Equal(75, catalog[1].ReloadTicks);         // 2.5 s
        Assert.Equal(300f, catalog[1].Range);
        Assert.False(catalog[1].Automatic);
        Assert.Equal(AmmoType.Heavy, catalog[1].AmmoType);

        // Wisp SMG: 12 damage, 15 rounds/s (2 ticks at 30 Hz), 25 rounds, 1.6 s reload, 80 m, automatic, Light.
        Assert.Equal(3, catalog[2].Id);
        Assert.Equal("Wisp SMG", catalog[2].Name);
        Assert.Equal(12, catalog[2].Damage);
        Assert.Equal(2, catalog[2].FireIntervalTicks);
        Assert.Equal(25, catalog[2].MagazineSize);
        Assert.Equal(48, catalog[2].ReloadTicks);
        Assert.Equal(80f, catalog[2].Range);
        Assert.True(catalog[2].Automatic);
        Assert.Equal(AmmoType.Light, catalog[2].AmmoType);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/CombatPacketTests.cs` (무기당 31 B, 모르는 탄약 종류 거절):

변경 1/4 — 찾을 코드:

```csharp
        ReloadTicks = 60,
        Range = 150f,
        Automatic = true,
    };

    [Fact]
```

바꿀 코드:

```csharp
        ReloadTicks = 60,
        Range = 150f,
        Automatic = true,
        AmmoType = AmmoType.Medium,
    };

    [Fact]
```

변경 2/4 — 찾을 코드:

```csharp
        Assert.Equal(2, read[1].WeaponId);
        Assert.False(read[1].Automatic);
        Assert.Equal(300f, read[1].Range);
        Assert.Equal(0, reader.Remaining);
    }
```

바꿀 코드:

```csharp
        Assert.Equal(2, read[1].WeaponId);
        Assert.False(read[1].Automatic);
        Assert.Equal(300f, read[1].Range);
        Assert.Equal(AmmoType.Medium, read[1].AmmoType);
        Assert.Equal(0, reader.Remaining);
    }
```

변경 3/4 — 찾을 코드:

```csharp
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, weapons);
        Assert.False(writer.Overflowed);
        Assert.Equal(2 + 8 * 30, writer.Length);
    }

    [Theory]
```

바꿀 코드:

```csharp
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, weapons);
        Assert.False(writer.Overflowed);
        Assert.Equal(2 + 8 * 31, writer.Length);   // Phase 4: one more byte (ammo type) per weapon
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Theory]
```

변경 4/4 — 찾을 코드:

```csharp
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WeaponCatalog_Truncated_IsRejected()
    {
```

바꿀 코드:

```csharp
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(AmmoType.None)]
    [InlineData((AmmoType)4)]
    public void WeaponCatalog_UnknownAmmoType_IsRejected(AmmoType ammoType)
    {
        var weapon = Weapon(1, "Bad");
        weapon.AmmoType = ammoType;
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { weapon });
        var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WeaponCatalog_Truncated_IsRejected()
    {
```

- [ ] **Step 3: 실패하는 테스트 작성 — `GameData` 생성자로 바꾸기**

`Match`와 `GameLoop`가 `GameData`를 받는다. 모든 생성 지점을 `TestGameData.Create(...)`로 바꾼다.

`Server/tests/ProjectH.Server.Tests/Game/MatchTests.cs` (Join 순서에 ItemCatalog 추가):

변경 1/3 — 찾을 코드:

```csharp

    public MatchTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, TestWeapons.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));
    }
```

바꿀 코드:

```csharp

    public MatchTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));
    }
```

변경 2/3 — 찾을 코드:

```csharp
    }

    [Fact]
    public void Join_SendsWeaponCatalog_AfterResponse_BeforeSpawns()
    {
        _match.TryJoin(1, "a");
```

바꿀 코드:

```csharp
    }

    [Fact]
    public void Join_SendsCatalogs_AfterResponse_BeforeSpawns()
    {
        _match.TryJoin(1, "a");
```

변경 3/3 — 찾을 코드:

```csharp
        Assert.Equal(PacketId.JoinMatchResponse, toPeer[0].Id);
        Assert.Equal(PacketId.WeaponCatalog, toPeer[1].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[1].Method);
        Assert.Equal(PacketId.PlayerSpawned, toPeer[2].Id);

        var reader = new PacketReader(toPeer[1].Data);
        reader.TryReadPacketId(out _);
        Assert.True(WeaponCatalogPacket.TryRead(ref reader, out var weapons));
        Assert.Equal("Test Auto", weapons[0].Name);
        Assert.Equal(TestWeapons.AutoInterval, weapons[0].FireIntervalTicks);
    }

    [Fact]
    public void Constructor_RejectsCatalogBuiltForOtherSimHz()
    {
        Assert.Throws<ArgumentException>(() =>
            new Match(new ServerOptions { SimHz = 60 }, TestWeapons.Create(simHz: 30), (_, _, _) => { }));
    }

    [Fact]
```

바꿀 코드:

```csharp
        Assert.Equal(PacketId.JoinMatchResponse, toPeer[0].Id);
        Assert.Equal(PacketId.WeaponCatalog, toPeer[1].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[1].Method);
        Assert.Equal(PacketId.ItemCatalog, toPeer[2].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[2].Method);
        Assert.Equal(PacketId.PlayerSpawned, toPeer[3].Id);

        var reader = new PacketReader(toPeer[1].Data);
        reader.TryReadPacketId(out _);
        Assert.True(WeaponCatalogPacket.TryRead(ref reader, out var weapons));
        Assert.Equal("Test Auto", weapons[0].Name);
        Assert.Equal(TestWeapons.AutoInterval, weapons[0].FireIntervalTicks);
        Assert.Equal(AmmoType.Medium, weapons[0].AmmoType);

        reader = new PacketReader(toPeer[2].Data);
        reader.TryReadPacketId(out _);
        Assert.True(ItemCatalogPacket.TryRead(ref reader, out var items));
        Assert.Equal("Legendary", items.Rarities[4].Name);
        Assert.Equal(TestGameData.MedkitUseTicks, items.Consumables[0].UseTicks);
    }

    [Fact]
    public void Constructor_RejectsDataBuiltForOtherSimHz()
    {
        Assert.Throws<ArgumentException>(() =>
            new Match(new ServerOptions { SimHz = 60 }, TestGameData.Create(simHz: 30), (_, _, _) => { }));
    }

    [Fact]
```

`Server/tests/ProjectH.Server.Tests/Game/CombatMatchTests.cs`:

변경 1/2 — 찾을 코드:

```csharp

    public CombatMatchTests()
    {
        _match = NewMatch(TestWeapons.Create());
    }

    private Match NewMatch(WeaponCatalog weapons) =>
        new(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, weapons,
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));

    private PlayerEntity Join(int peer, Vector3 feet)
```

바꿀 코드:

```csharp

    public CombatMatchTests()
    {
        _match = NewMatch(TestGameData.Create());
    }

    private Match NewMatch(GameData data) =>
        new(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, data,
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));

    private PlayerEntity Join(int peer, Vector3 feet)
```

변경 2/2 — 찾을 코드:

```csharp
    [Fact]
    public void TargetBeyondRange_IsMissed()
    {
        _match = NewMatch(TestWeapons.Create(autoRange: 5f));
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));   // front face 5.65 m from the eye line
```

바꿀 코드:

```csharp
    [Fact]
    public void TargetBeyondRange_IsMissed()
    {
        _match = NewMatch(TestGameData.Create(autoRange: 5f));
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));   // front face 5.65 m from the eye line
```

`Server/tests/ProjectH.Server.Tests/Game/LagCompensationTests.cs`:

변경 1/4 — 찾을 코드:

```csharp

    public LagCompensationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2 }, TestWeapons.Create(), (_, _, _) => { });
        _match.TryJoin(1, "shooter");
        _match.TryJoin(2, "target");
        _match.TryGetPlayer(1, out _shooter);
```

바꿀 코드:

```csharp

    public LagCompensationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), (_, _, _) => { });
        _match.TryJoin(1, "shooter");
        _match.TryJoin(2, "target");
        _match.TryGetPlayer(1, out _shooter);
```

변경 2/4 — 찾을 코드:

```csharp
    public void Shooter_FiresFromCurrentPosition()
    {
        var shots = new List<Vector3>();
        var match = new Match(new ServerOptions { MaxPlayers = 1 }, TestWeapons.Create(), (_, data, _) =>
        {
            var reader = new PacketReader(data);
            if (reader.TryReadPacketId(out PacketId id) && id == PacketId.ShotFired && ShotFired.TryRead(ref reader, out var shot))
```

바꿀 코드:

```csharp
    public void Shooter_FiresFromCurrentPosition()
    {
        var shots = new List<Vector3>();
        var match = new Match(new ServerOptions { MaxPlayers = 1 }, TestGameData.Create(), (_, data, _) =>
        {
            var reader = new PacketReader(data);
            if (reader.TryReadPacketId(out PacketId id) && id == PacketId.ShotFired && ShotFired.TryRead(ref reader, out var shot))
```

변경 3/4 — 찾을 코드:

```csharp
    [Fact]
    public void AfterRespawn_RewindFindsSpawnPoint_NotTheBody()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestWeapons.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

바꿀 코드:

```csharp
    [Fact]
    public void AfterRespawn_RewindFindsSpawnPoint_NotTheBody()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

변경 4/4 — 찾을 코드:

```csharp
    [Fact]
    public void FiringTick_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestWeapons.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

바꿀 코드:

```csharp
    [Fact]
    public void FiringTick_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

`Server/tests/ProjectH.Server.Tests/Game/WeaponRulesTests.cs` (한 무기짜리 JSON에 ammoType):

변경 — 찾을 코드:

```csharp
    public void SlotBeyondLoadout_IsIgnored()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"Only\", \"damage\": 10, \"fireIntervalSeconds\": 0.1, " +
                      "\"magazineSize\": 3, \"reloadSeconds\": 1, \"range\": 50, \"automatic\": true } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var single, out _));
        var player = new PlayerEntity(2, 2, "b", 8);
        WeaponRules.Equip(player, single!);
```

바꿀 코드:

```csharp
    public void SlotBeyondLoadout_IsIgnored()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"Only\", \"damage\": 10, \"fireIntervalSeconds\": 0.1, " +
                      "\"magazineSize\": 3, \"reloadSeconds\": 1, \"range\": 50, \"automatic\": true, \"ammoType\": \"Light\" } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var single, out _));
        var player = new PlayerEntity(2, 2, "b", 8);
        WeaponRules.Equip(player, single!);
```

`Server/tests/ProjectH.Server.Tests/Integration/CombatIntegrationTests.cs`:

변경 1/2 — 찾을 코드:

```csharp
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestWeapons.Create(), NullLogger.Instance);
        _server.Start();
    }
```

바꿀 코드:

```csharp
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance);
        _server.Start();
    }
```

변경 2/2 — 찾을 코드:

```csharp
    {
        using var a = Join("shooter");
        using var b = Join("target");
        Assert.True(Pump.Until(() => a.Weapons != null && b.Weapons != null &&
                                     a.LastSnapshot.ContainsKey(a.MyEntityId) && a.LastSnapshot.ContainsKey(b.MyEntityId) &&
                                     b.SnapshotsReceived > 0, 3000, a, b), "catalog and first snapshots");
        Assert.Equal("Test Auto", a.Weapons![0].Name);
```

바꿀 코드:

```csharp
    {
        using var a = Join("shooter");
        using var b = Join("target");
        Assert.True(Pump.Until(() => a.Weapons != null && b.Weapons != null && a.Items != null && b.Items != null &&
                                     a.LastSnapshot.ContainsKey(a.MyEntityId) && a.LastSnapshot.ContainsKey(b.MyEntityId) &&
                                     b.SnapshotsReceived > 0, 3000, a, b), "catalog and first snapshots");
        Assert.Equal("Test Auto", a.Weapons![0].Name);
```

`Server/tests/ProjectH.Server.Tests/Integration/GameLoopPeerTests.cs`:

변경 — 찾을 코드:

```csharp
        newPeer.Tag = new PeerState("new");
        const int reusedId = 7;

        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestWeapons.Create(), NullLogger.Instance);
        var control = loop.Channels.Control.Writer;

        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, reusedId, oldPeer, "old")));
```

바꿀 코드:

```csharp
        newPeer.Tag = new PeerState("new");
        const int reusedId = 7;

        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance);
        var control = loop.Channels.Control.Writer;

        Assert.True(control.TryWrite(new ControlMessage(ControlKind.Connected, reusedId, oldPeer, "old")));
```

`Server/tests/ProjectH.Server.Tests/Integration/ServerIntegrationTests.cs`:

변경 — 찾을 코드:

```csharp
            MaxPlayers = maxPlayers,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestWeapons.Create(), NullLogger.Instance);
        loop.Start();
        return loop;
    }
```

바꿀 코드:

```csharp
            MaxPlayers = maxPlayers,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance);
        loop.Start();
        return loop;
    }
```

`Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs`:

변경 1/2 — 찾을 코드:

```csharp
    public int SnapshotsReceived { get; private set; }

    public WeaponInfo[]? Weapons { get; private set; }
    public SnapshotSelf LastSelf { get; private set; }
    public List<SnapshotSelf> SelfHistory { get; } = new();
    public List<ShotFired> Shots { get; } = new();
```

바꿀 코드:

```csharp
    public int SnapshotsReceived { get; private set; }

    public WeaponInfo[]? Weapons { get; private set; }
    public ItemCatalogData? Items { get; private set; }
    public SnapshotSelf LastSelf { get; private set; }
    public List<SnapshotSelf> SelfHistory { get; } = new();
    public List<ShotFired> Shots { get; } = new();
```

변경 2/2 — 찾을 코드:

```csharp
            case PacketId.WeaponCatalog:
                if (WeaponCatalogPacket.TryRead(ref r, out var weapons)) Weapons = weapons;
                break;
            case PacketId.ShotFired:
                if (ShotFired.TryRead(ref r, out var shot)) Shots.Add(shot);
                break;
```

바꿀 코드:

```csharp
            case PacketId.WeaponCatalog:
                if (WeaponCatalogPacket.TryRead(ref r, out var weapons)) Weapons = weapons;
                break;
            case PacketId.ItemCatalog:
                if (ItemCatalogPacket.TryRead(ref r, out var items)) Items = items;
                break;
            case PacketId.ShotFired:
                if (ShotFired.TryRead(ref r, out var shot)) Shots.Add(shot);
                break;
```

- [ ] **Step 4: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 실패 — `GameData`, `ItemCatalog`, `WeaponInfo.AmmoType`, `TryGetById` 등이 없다는 오류

- [ ] **Step 5: 카탈로그 패킷에 탄약 종류**

`Shared/Runtime/Protocol/CombatPackets.cs`:

변경 1/3 — 찾을 코드:

```csharp
        public ushort ReloadTicks;
        public float Range;
        public bool Automatic;
    }

    // S->C, ReliableOrdered, once right after a successful Join. Slot1 / Slot2 select entries 0 / 1.
    public static class WeaponCatalogPacket
    {
        public const int MaxWeapons = 8;
```

바꿀 코드:

```csharp
        public ushort ReloadTicks;
        public float Range;
        public bool Automatic;
        public AmmoType AmmoType;   // Phase 4 (D3): Light, Medium or Heavy
    }

    // S->C, ReliableOrdered, once right after a successful Join. Lists every weapon that can exist as an
    // item; which weapon sits in which inventory slot comes with InventoryState (Phase 4).
    public static class WeaponCatalogPacket
    {
        public const int MaxWeapons = 8;
```

변경 2/3 — 찾을 코드:

```csharp
                writer.WriteUInt16(w.ReloadTicks);
                writer.WriteSingle(w.Range);
                writer.WriteByte(w.Automatic ? (byte)1 : (byte)0);
            }
        }
```

바꿀 코드:

```csharp
                writer.WriteUInt16(w.ReloadTicks);
                writer.WriteSingle(w.Range);
                writer.WriteByte(w.Automatic ? (byte)1 : (byte)0);
                writer.WriteByte((byte)w.AmmoType);
            }
        }
```

변경 3/3 — 찾을 코드:

```csharp
                if (!reader.TryReadSingle(out w.Range) || !Finite.Check(w.Range) || w.Range <= 0f) return false;
                if (!reader.TryReadByte(out byte automatic) || automatic > 1) return false;
                w.Automatic = automatic == 1;
                result[i] = w;
            }
            weapons = result;
```

바꿀 코드:

```csharp
                if (!reader.TryReadSingle(out w.Range) || !Finite.Check(w.Range) || w.Range <= 0f) return false;
                if (!reader.TryReadByte(out byte automatic) || automatic > 1) return false;
                w.Automatic = automatic == 1;
                if (!reader.TryReadByte(out byte ammoType) || ammoType == 0 || ammoType > ItemConstants.AmmoTypeCount) return false;
                w.AmmoType = (AmmoType)ammoType;
                result[i] = w;
            }
            weapons = result;
```

- [ ] **Step 6: 데이터 파일과 복사 설정**

`Server/src/ProjectH.Server/weapons.json` 전체를 다음으로 바꾼다(D3: ammoType, Wisp SMG. 15발/s = 30 Hz에서 2 Tick이므로 0.0667 s).

```json
{
  "weapons": [
    {
      "id": 1,
      "name": "Vesper AR",
      "damage": 20,
      "fireIntervalSeconds": 0.1,
      "magazineSize": 30,
      "reloadSeconds": 2.0,
      "range": 150,
      "automatic": true,
      "spread": 0,
      "recoil": 0,
      "ammoType": "Medium"
    },
    {
      "id": 2,
      "name": "Kestrel LR",
      "damage": 90,
      "fireIntervalSeconds": 1.25,
      "magazineSize": 5,
      "reloadSeconds": 2.5,
      "range": 300,
      "automatic": false,
      "spread": 0,
      "recoil": 0,
      "ammoType": "Heavy"
    },
    {
      "id": 3,
      "name": "Wisp SMG",
      "damage": 12,
      "fireIntervalSeconds": 0.0667,
      "magazineSize": 25,
      "reloadSeconds": 1.6,
      "range": 80,
      "automatic": true,
      "spread": 0,
      "recoil": 0,
      "ammoType": "Light"
    }
  ]
}
```

`Server/src/ProjectH.Server/items.json`을 만든다(spec §1, 줍는 양은 "Spec 해석" 2).

```json
{
  "rarities": [
    { "name": "Common", "damageMultiplier": 1.00 },
    { "name": "Uncommon", "damageMultiplier": 1.05 },
    { "name": "Rare", "damageMultiplier": 1.10 },
    { "name": "Epic", "damageMultiplier": 1.15 },
    { "name": "Legendary", "damageMultiplier": 1.20 }
  ],
  "ammo": [
    { "type": "Light", "name": "Light Rounds", "pickupAmount": 60, "max": 180 },
    { "type": "Medium", "name": "Medium Rounds", "pickupAmount": 45, "max": 150 },
    { "type": "Heavy", "name": "Heavy Rounds", "pickupAmount": 10, "max": 30 }
  ],
  "consumables": [
    { "id": "Medkit", "name": "Medkit", "useSeconds": 3.0, "heal": 50, "shield": 0, "maxStack": 3 },
    { "id": "ShieldCell", "name": "Shield Cell", "useSeconds": 2.0, "heal": 0, "shield": 25, "maxStack": 6 }
  ]
}
```

`Server/src/ProjectH.Server/ProjectH.Server.csproj`:

변경 — 찾을 코드:

```xml
  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="weapons.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

바꿀 코드:

```xml
  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="weapons.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="items.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 7: JSON 공용 도우미, `WeaponDefinition`, `WeaponCatalog`**

`Server/src/ProjectH.Server/Game/DataJson.cs`를 만든다. `WeaponCatalog`의 JSON 옵션과 Tick 변환을 세 로더가 함께 쓴다.

```csharp
using System;
using System.Text.Json;

namespace ProjectH.Server.Game;

// Shared by the data file loaders (weapons.json, items.json, loot.json). Startup only.
internal static class DataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Seconds -> whole ticks at simHz, at least 1: 1.25 s at 30 Hz = 37.5 -> 38.
    public static bool TryTicks(double seconds, int simHz, out ushort ticks)
    {
        ticks = 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return false;
        double value = Math.Round(seconds * simHz, MidpointRounding.AwayFromZero);
        if (value > ushort.MaxValue) return false;
        ticks = (ushort)Math.Max(1, value);
        return true;
    }

    // Deserialize without throwing past the loader: a JsonException becomes an error string.
    public static bool TryDeserialize<T>(string json, out T? value, out string? error) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            error = value == null ? "the file is empty or null." : null;
            return value != null;
        }
        catch (JsonException ex)
        {
            value = null;
            error = "invalid JSON: " + ex.Message;
            return false;
        }
    }
}
```

`Server/src/ProjectH.Server/Game/Combat/WeaponDefinition.cs`:

변경 1/4 — 찾을 코드:

```csharp
public sealed class WeaponDefinition
{
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
        ushort reloadTicks, float range, bool automatic, float spread, float recoil)
    {
        Id = id;
        Name = name;
```

바꿀 코드:

```csharp
public sealed class WeaponDefinition
{
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
        ushort reloadTicks, float range, bool automatic, float spread, float recoil, AmmoType ammoType)
    {
        Id = id;
        Name = name;
```

변경 2/4 — 찾을 코드:

```csharp
        Automatic = automatic;
        Spread = spread;
        Recoil = recoil;
    }

    public byte Id { get; }
```

바꿀 코드:

```csharp
        Automatic = automatic;
        Spread = spread;
        Recoil = recoil;
        AmmoType = ammoType;
    }

    public byte Id { get; }
```

변경 3/4 — 찾을 코드:

```csharp
    public ushort ReloadTicks { get; }
    public float Range { get; }
    public bool Automatic { get; }

    // D5: data fields only, always 0 in this phase and not applied by the combat code.
    public float Spread { get; }
```

바꿀 코드:

```csharp
    public ushort ReloadTicks { get; }
    public float Range { get; }
    public bool Automatic { get; }
    // Phase 4 (D3): the reserve a reload draws from.
    public AmmoType AmmoType { get; }

    // D5: data fields only, always 0 in this phase and not applied by the combat code.
    public float Spread { get; }
```

변경 4/4 — 찾을 코드:

```csharp
        ReloadTicks = ReloadTicks,
        Range = Range,
        Automatic = Automatic,
    };
}
```

바꿀 코드:

```csharp
        ReloadTicks = ReloadTicks,
        Range = Range,
        Automatic = Automatic,
        AmmoType = AmmoType,
    };
}
```

`Server/src/ProjectH.Server/Game/Combat/WeaponCatalog.cs` 전체를 다음으로 바꾼다(ammoType 검증, 이름 중복 거절, Id 조회 표, `DataJson` 사용).

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// Weapon numbers live in data, not code (D4, request §16). Loaded and validated once at startup; a
// bad file stops the server the same way a bad ServerOptions value does. Immutable afterwards, so the
// game loop reads it without locks.
public sealed class WeaponCatalog
{
    // Slot1 / Slot2 select the first two weapons (D5). Extra entries are valid data but not equipped.
    public const int SlotCount = 2;

    private readonly WeaponDefinition[] _weapons;
    // Weapon id -> index in _weapons, -1 when unknown. Ids are bytes, so 256 entries cover every id.
    private readonly int[] _indexById = new int[256];

    private WeaponCatalog(WeaponDefinition[] weapons, int simHz)
    {
        _weapons = weapons;
        SimHz = simHz;
        Array.Fill(_indexById, -1);
        WireInfos = new WeaponInfo[weapons.Length];
        for (int i = 0; i < weapons.Length; i++)
        {
            _indexById[weapons[i].Id] = i;
            WireInfos[i] = weapons[i].ToWire();
        }
    }

    public int Count => _weapons.Length;
    public int LoadoutCount => Math.Min(_weapons.Length, SlotCount);
    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the WeaponCatalog packet sent at every join.
    public WeaponInfo[] WireInfos { get; }

    public WeaponDefinition this[int index] => _weapons[index];

    // Weapon items and inventory slots store the weapon id (Phase 4).
    public bool TryGetById(byte id, out WeaponDefinition weapon)
    {
        int index = _indexById[id];
        weapon = index >= 0 ? _weapons[index] : null!;
        return index >= 0;
    }

    public static WeaponCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Weapon data not found: {path}");
        string json = File.ReadAllText(path);
        if (!TryParse(json, simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid weapon data {path}: {error}");
        return catalog!;
    }

    public static bool TryParse(string json, int simHz, out WeaponCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1)
        {
            error = "SimHz must be positive.";
            return false;
        }

        if (!DataJson.TryDeserialize(json, out CatalogJson? root, out error)) return false;

        List<WeaponJson?>? list = root!.Weapons;
        if (list == null || list.Count < 1 || list.Count > WeaponCatalogPacket.MaxWeapons)
        {
            error = $"\"weapons\" must hold 1-{WeaponCatalogPacket.MaxWeapons} entries.";
            return false;
        }

        var weapons = new WeaponDefinition[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            string? problem = Validate(list[i], simHz, out WeaponDefinition? weapon);
            if (problem != null)
            {
                error = $"weapons[{i}]: {problem}";
                return false;
            }
            weapons[i] = weapon!;
            for (int j = 0; j < i; j++)
            {
                if (weapons[j].Id == weapons[i].Id)
                {
                    error = $"weapons[{i}]: duplicate id {weapons[i].Id}.";
                    return false;
                }
                if (weapons[j].Name == weapons[i].Name)
                {
                    error = $"weapons[{i}]: duplicate name \"{weapons[i].Name}\".";
                    return false;
                }
            }
        }

        catalog = new WeaponCatalog(weapons, simHz);
        error = null;
        return true;
    }

    private static string? Validate(WeaponJson? w, int simHz, out WeaponDefinition? weapon)
    {
        weapon = null;
        if (w == null) return "entry is null.";
        if (w.Id < 1 || w.Id > byte.MaxValue) return "id must be 1-255.";
        if (string.IsNullOrWhiteSpace(w.Name)) return "name is required.";
        if (Encoding.UTF8.GetByteCount(w.Name) > WeaponCatalogPacket.MaxNameBytes)
            return $"name must be at most {WeaponCatalogPacket.MaxNameBytes} UTF-8 bytes.";
        if (w.Damage < 1 || w.Damage > ushort.MaxValue) return "damage must be 1-65535.";
        if (w.MagazineSize < 1 || w.MagazineSize > byte.MaxValue) return "magazineSize must be 1-255.";
        if (!DataJson.TryTicks(w.FireIntervalSeconds, simHz, out ushort fireTicks)) return "fireIntervalSeconds must be positive and finite (at most 65535 ticks).";
        if (!DataJson.TryTicks(w.ReloadSeconds, simHz, out ushort reloadTicks)) return "reloadSeconds must be positive and finite (at most 65535 ticks).";
        float range = (float)w.Range;
        if (!float.IsFinite(range) || range <= 0f) return "range must be positive and finite.";
        float spread = (float)w.Spread;
        float recoil = (float)w.Recoil;
        if (!float.IsFinite(spread) || spread < 0f) return "spread must be 0 or more.";
        if (!float.IsFinite(recoil) || recoil < 0f) return "recoil must be 0 or more.";
        if (!ItemCatalog.TryParseAmmoType(w.AmmoType, out AmmoType ammoType)) return "ammoType must be Light, Medium or Heavy.";

        weapon = new WeaponDefinition((byte)w.Id, w.Name, (ushort)w.Damage, fireTicks, (byte)w.MagazineSize,
            reloadTicks, range, w.Automatic, spread, recoil, ammoType);
        return null;
    }

    private sealed class CatalogJson
    {
        public List<WeaponJson?>? Weapons { get; set; }
    }

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class WeaponJson
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Damage { get; set; }
        public double FireIntervalSeconds { get; set; }
        public int MagazineSize { get; set; }
        public double ReloadSeconds { get; set; }
        public double Range { get; set; }
        public bool Automatic { get; set; }
        public double Spread { get; set; }
        public double Recoil { get; set; }
        public string? AmmoType { get; set; }
    }
}
```

- [ ] **Step 8: `ItemCatalog`, `GameData`**

`Server/src/ProjectH.Server/Game/Items/ItemCatalog.cs`를 만든다.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

public sealed class AmmoDefinition
{
    public AmmoDefinition(AmmoType type, string name, ushort pickupAmount, ushort max)
    {
        Type = type;
        Name = name;
        PickupAmount = pickupAmount;
        Max = max;
    }

    public AmmoType Type { get; }
    public string Name { get; }
    public ushort PickupAmount { get; }   // rounds in one loot ammo item
    public ushort Max { get; }            // reserve limit of this type (D3)
}

public sealed class ConsumableDefinition
{
    public ConsumableDefinition(ConsumableType type, string name, ushort useTicks, ushort heal, ushort shield, byte maxStack)
    {
        Type = type;
        Name = name;
        UseTicks = useTicks;
        Heal = heal;
        Shield = shield;
        MaxStack = maxStack;
    }

    public ConsumableType Type { get; }
    public string Name { get; }
    public ushort UseTicks { get; }   // channel time (D11), already in simulation ticks
    public ushort Heal { get; }
    public ushort Shield { get; }
    public byte MaxStack { get; }
}

// items.json (D2): rarities, ammo types and consumables. Loaded and validated once at startup; a bad
// file stops the server. Immutable afterwards, so the game loop reads it without locks.
public sealed class ItemCatalog
{
    private readonly float[] _damageMultipliers;
    private readonly string[] _rarityNames;
    private readonly AmmoDefinition[] _ammo;              // index = AmmoType - 1
    private readonly ConsumableDefinition[] _consumables; // index = ConsumableType - 1

    private ItemCatalog(string[] rarityNames, float[] damageMultipliers, AmmoDefinition[] ammo,
        ConsumableDefinition[] consumables, int simHz)
    {
        _rarityNames = rarityNames;
        _damageMultipliers = damageMultipliers;
        _ammo = ammo;
        _consumables = consumables;
        SimHz = simHz;

        var wire = new ItemCatalogData
        {
            Rarities = new RarityInfo[rarityNames.Length],
            Ammo = new AmmoInfo[ammo.Length],
            Consumables = new ConsumableInfo[consumables.Length],
        };
        for (int i = 0; i < rarityNames.Length; i++)
            wire.Rarities[i] = new RarityInfo { Name = rarityNames[i], DamageMultiplier = damageMultipliers[i] };
        for (int i = 0; i < ammo.Length; i++)
            wire.Ammo[i] = new AmmoInfo { Type = ammo[i].Type, Name = ammo[i].Name, Max = ammo[i].Max };
        for (int i = 0; i < consumables.Length; i++)
        {
            ConsumableDefinition c = consumables[i];
            wire.Consumables[i] = new ConsumableInfo
            {
                Type = c.Type, Name = c.Name, UseTicks = c.UseTicks, Heal = c.Heal, Shield = c.Shield, MaxStack = c.MaxStack,
            };
        }
        Wire = wire;
    }

    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the ItemCatalog packet sent at every join.
    public ItemCatalogData Wire { get; }

    public float DamageMultiplier(int rarity) => _damageMultipliers[rarity];
    public string RarityName(int rarity) => _rarityNames[rarity];
    public AmmoDefinition Ammo(AmmoType type) => _ammo[(int)type - 1];
    public ConsumableDefinition Consumable(ConsumableType type) => _consumables[(int)type - 1];

    // Index of a rarity name, or -1 (loot.json refers to rarities by name).
    public int RarityIndex(string name)
    {
        for (int i = 0; i < _rarityNames.Length; i++)
        {
            if (_rarityNames[i] == name) return i;
        }
        return -1;
    }

    public static ItemCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Item data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid item data {path}: {error}");
        return catalog!;
    }

    public static bool TryParse(string json, int simHz, out ItemCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1)
        {
            error = "SimHz must be positive.";
            return false;
        }
        if (!DataJson.TryDeserialize(json, out ItemsJson? root, out error)) return false;

        error = ParseRarities(root!.Rarities, out string[]? names, out float[]? multipliers);
        if (error != null) return false;
        error = ParseAmmo(root.Ammo, out AmmoDefinition[]? ammo);
        if (error != null) return false;
        error = ParseConsumables(root.Consumables, simHz, out ConsumableDefinition[]? consumables);
        if (error != null) return false;

        catalog = new ItemCatalog(names!, multipliers!, ammo!, consumables!, simHz);
        return true;
    }

    // Enum names only: "1" or "None" are not ammo types.
    public static bool TryParseAmmoType(string? text, out AmmoType type)
    {
        type = AmmoType.None;
        switch (text)
        {
            case "Light": type = AmmoType.Light; return true;
            case "Medium": type = AmmoType.Medium; return true;
            case "Heavy": type = AmmoType.Heavy; return true;
            default: return false;
        }
    }

    private static string? ParseRarities(List<RarityJson?>? list, out string[]? names, out float[]? multipliers)
    {
        names = null;
        multipliers = null;
        if (list == null || list.Count != ItemConstants.RarityCount)
            return $"\"rarities\" must hold exactly {ItemConstants.RarityCount} entries.";

        var n = new string[list.Count];
        var m = new float[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            RarityJson? r = list[i];
            if (r == null) return $"rarities[{i}]: entry is null.";
            string? nameProblem = CheckName(r.Name);
            if (nameProblem != null) return $"rarities[{i}]: {nameProblem}";
            float multiplier = (float)r.DamageMultiplier;
            if (!float.IsFinite(multiplier) || multiplier <= 0f || multiplier > 10f)
                return $"rarities[{i}]: damageMultiplier must be above 0 and at most 10.";
            if (Array.IndexOf(n, r.Name, 0, i) >= 0) return $"rarities[{i}]: duplicate name \"{r.Name}\".";
            n[i] = r.Name!;
            m[i] = multiplier;
        }
        names = n;
        multipliers = m;
        return null;
    }

    private static string? ParseAmmo(List<AmmoJson?>? list, out AmmoDefinition[]? ammo)
    {
        ammo = null;
        if (list == null || list.Count != ItemConstants.AmmoTypeCount)
            return $"\"ammo\" must hold exactly {ItemConstants.AmmoTypeCount} entries (Light, Medium, Heavy).";

        var result = new AmmoDefinition[ItemConstants.AmmoTypeCount];
        for (int i = 0; i < list.Count; i++)
        {
            AmmoJson? a = list[i];
            if (a == null) return $"ammo[{i}]: entry is null.";
            if (!TryParseAmmoType(a.Type, out AmmoType type)) return $"ammo[{i}]: type must be Light, Medium or Heavy.";
            if (result[(int)type - 1] != null) return $"ammo[{i}]: duplicate type {type}.";
            string? nameProblem = CheckName(a.Name);
            if (nameProblem != null) return $"ammo[{i}]: {nameProblem}";
            if (a.Max < 1 || a.Max > ushort.MaxValue) return $"ammo[{i}]: max must be 1-65535.";
            if (a.PickupAmount < 1 || a.PickupAmount > a.Max) return $"ammo[{i}]: pickupAmount must be 1-max.";
            foreach (AmmoDefinition? other in result)
            {
                if (other != null && other.Name == a.Name) return $"ammo[{i}]: duplicate name \"{a.Name}\".";
            }
            result[(int)type - 1] = new AmmoDefinition(type, a.Name!, (ushort)a.PickupAmount, (ushort)a.Max);
        }
        ammo = result;
        return null;
    }

    private static string? ParseConsumables(List<ConsumableJson?>? list, int simHz, out ConsumableDefinition[]? consumables)
    {
        consumables = null;
        if (list == null || list.Count != ItemConstants.ConsumableTypeCount)
            return $"\"consumables\" must hold exactly {ItemConstants.ConsumableTypeCount} entries (Medkit, ShieldCell).";

        var result = new ConsumableDefinition[ItemConstants.ConsumableTypeCount];
        for (int i = 0; i < list.Count; i++)
        {
            ConsumableJson? c = list[i];
            if (c == null) return $"consumables[{i}]: entry is null.";
            ConsumableType type = c.Id switch
            {
                "Medkit" => ConsumableType.Medkit,
                "ShieldCell" => ConsumableType.ShieldCell,
                _ => ConsumableType.None,
            };
            if (type == ConsumableType.None) return $"consumables[{i}]: id must be Medkit or ShieldCell.";
            if (result[(int)type - 1] != null) return $"consumables[{i}]: duplicate id {c.Id}.";
            string? nameProblem = CheckName(c.Name);
            if (nameProblem != null) return $"consumables[{i}]: {nameProblem}";
            if (!DataJson.TryTicks(c.UseSeconds, simHz, out ushort useTicks))
                return $"consumables[{i}]: useSeconds must be positive and finite (at most 65535 ticks).";
            if (c.Heal < 0 || c.Heal > ushort.MaxValue || c.Shield < 0 || c.Shield > ushort.MaxValue)
                return $"consumables[{i}]: heal and shield must be 0-65535.";
            if (c.Heal == 0 && c.Shield == 0) return $"consumables[{i}]: heal or shield must be above 0.";
            if (c.MaxStack < 1 || c.MaxStack > byte.MaxValue) return $"consumables[{i}]: maxStack must be 1-255.";
            foreach (ConsumableDefinition? other in result)
            {
                if (other != null && other.Name == c.Name) return $"consumables[{i}]: duplicate name \"{c.Name}\".";
            }
            result[(int)type - 1] = new ConsumableDefinition(type, c.Name!, useTicks, (ushort)c.Heal, (ushort)c.Shield, (byte)c.MaxStack);
        }
        consumables = result;
        return null;
    }

    private static string? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "name is required.";
        if (Encoding.UTF8.GetByteCount(name) > ItemConstants.MaxNameBytes)
            return $"name must be at most {ItemConstants.MaxNameBytes} UTF-8 bytes.";
        return null;
    }

    private sealed class ItemsJson
    {
        public List<RarityJson?>? Rarities { get; set; }
        public List<AmmoJson?>? Ammo { get; set; }
        public List<ConsumableJson?>? Consumables { get; set; }
    }

    // Missing numbers stay 0 and fail validation, so every required field must be written.
    private sealed class RarityJson
    {
        public string? Name { get; set; }
        public double DamageMultiplier { get; set; }
    }

    private sealed class AmmoJson
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public int PickupAmount { get; set; }
        public int Max { get; set; }
    }

    private sealed class ConsumableJson
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public double UseSeconds { get; set; }
        public int Heal { get; set; }
        public int Shield { get; set; }
        public int MaxStack { get; set; }
    }
}
```

`Server/src/ProjectH.Server/Game/GameData.cs`를 만든다.

```csharp
using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;

namespace ProjectH.Server.Game;

// All data files of the server (D2), loaded and cross-checked once at startup. Immutable afterwards.
public sealed class GameData
{
    public const string WeaponsFile = "weapons.json";
    public const string ItemsFile = "items.json";

    public GameData(WeaponCatalog weapons, ItemCatalog items)
    {
        Weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
        Items = items ?? throw new ArgumentNullException(nameof(items));
        if (weapons.SimHz != items.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, items for {items.SimHz}.", nameof(items));
    }

    public WeaponCatalog Weapons { get; }
    public ItemCatalog Items { get; }
    public int SimHz => Weapons.SimHz;

    // The files are copied next to the server executable. A missing or invalid file throws, so the host
    // refuses to start (same as an invalid ServerOptions value).
    public static GameData LoadDirectory(string directory, int simHz)
    {
        return new GameData(
            WeaponCatalog.LoadFile(Path.Combine(directory, WeaponsFile), simHz),
            ItemCatalog.LoadFile(Path.Combine(directory, ItemsFile), simHz));
    }
}
```

- [ ] **Step 9: Match·GameLoop·시작 경로 연결**

`Server/src/ProjectH.Server/Game/Match.cs` (생성자가 `GameData`를 받고, Join 때 WeaponCatalog 다음에 ItemCatalog를 보낸다):

변경 1/5 — 찾을 코드:

```csharp
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
```

바꿀 코드:

```csharp
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
```

변경 2/5 — 찾을 코드:

```csharp
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly SendPacket _send;
    private readonly WeaponCatalog _weapons;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
```

바꿀 코드:

```csharp
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly SendPacket _send;
    private readonly WeaponCatalog _weapons;
    private readonly ItemCatalog _items;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
```

변경 3/5 — 찾을 코드:

```csharp
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    public Match(ServerOptions options, WeaponCatalog weapons, SendPacket send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
        if (weapons.SimHz != options.SimHz)
            throw new ArgumentException($"Weapon catalog was built for SimHz {weapons.SimHz}, the match runs at {options.SimHz}.", nameof(weapons));
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
```

바꿀 코드:

```csharp
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    public Match(ServerOptions options, GameData data, SendPacket send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        ArgumentNullException.ThrowIfNull(data);
        if (data.SimHz != options.SimHz)
            throw new ArgumentException($"Game data was built for SimHz {data.SimHz}, the match runs at {options.SimHz}.", nameof(data));
        _weapons = data.Weapons;
        _items = data.Items;
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
```

변경 4/5 — 찾을 코드:

```csharp
        _players.Add(player);

        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon data before it can show its own weapon (D4).
        SendCatalog(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
```

바꿀 코드:

```csharp
        _players.Add(player);

        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon and item data before it can show them (D4, D2).
        SendCatalogs(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
```

변경 5/5 — 찾을 코드:

```csharp
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendCatalog(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
```

바꿀 코드:

```csharp
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendCatalogs(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        writer = new PacketWriter(_sendBuffer);
        ItemCatalogPacket.Write(ref writer, _items.Wire);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
```

`Server/src/ProjectH.Server/GameLoop.cs`:

변경 1/3 — 찾을 코드:

```csharp
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
```

바꿀 코드:

```csharp
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
```

변경 2/3 — 찾을 코드:

```csharp
    private long _lateTicksSkipped;
    private bool _disposed;

    public GameLoop(ServerOptions options, WeaponCatalog weapons, ILogger logger)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));
```

바꿀 코드:

```csharp
    private long _lateTicksSkipped;
    private bool _disposed;

    public GameLoop(ServerOptions options, GameData data, ILogger logger)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));
```

변경 3/3 — 찾을 코드:

```csharp
            IPv6Enabled = false,
        };
        listener.Manager = _net;
        _match = new Match(options, weapons, SendToPeer);
    }

    public int LocalPort => _net.LocalPort;
```

바꿀 코드:

```csharp
            IPv6Enabled = false,
        };
        listener.Manager = _net;
        _match = new Match(options, data, SendToPeer);
    }

    public int LocalPort => _net.LocalPort;
```

`Server/src/ProjectH.Server/GameServerService.cs`:

변경 1/2 — 찾을 코드:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server;
```

바꿀 코드:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server.Game;

namespace ProjectH.Server;
```

변경 2/2 — 찾을 코드:

```csharp

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger)
    {
        // weapons.json is copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (D4).
        var weapons = WeaponCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "weapons.json"), options.Value.SimHz);
        _loop = new GameLoop(options.Value, weapons, logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
```

바꿀 코드:

```csharp

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger)
    {
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        _loop = new GameLoop(options.Value, data, logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
```

- [ ] **Step 10: 서버 테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 315개 PASS (`ShippedWeaponsJson_MatchesSpec`, `ShippedItemsJson_MatchesSpec`, `GameData_LoadDirectory_LoadsTheShippedFiles` 포함)

- [ ] **Step 11: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 64개 PASS

- [ ] **Step 12: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 3: Loot Table·시드 난수·Spawn Point (`loot.json`, `LootTable`, Shared `LootPoints`, 하네스 규칙, `ServerOptions`)

**Files:**
- Create: `Server/src/ProjectH.Server/loot.json`, `Game/Items/LootTable.cs`, `Shared/Runtime/Simulation/LootPoints.cs`
- Modify: `Server/src/ProjectH.Server/ProjectH.Server.csproj`, `Game/GameData.cs` (전체 교체), `ServerOptions.cs`, `appsettings.json`
- Modify: `.claude/skills/game-core-rules/SKILL.md` (4절), `CLAUDE.md` (변경 이력)
- Test: `Server/tests/ProjectH.Server.Tests/Game/LootTableTests.cs` (신규), `Shared/LootPointsTests.cs` (신규), `TestGameData.cs`, `Game/ItemCatalogTests.cs`, `ServerOptionsTests.cs`

**Interfaces:**
- Consumes: Task 2의 `DataJson`, `ItemCatalog.RarityIndex/RarityName/Ammo`, `WeaponCatalog.Count`/인덱서, `GameData`
- Produces:
  - Shared `ProjectH.Shared.Simulation.LootPoint { readonly Vector3 Position; readonly string Table; }`, `static class LootPoints { const string FloorTable = "Floor", TowerTable = "Tower"; static ReadOnlySpan<LootPoint> All }` (17곳)
  - `ProjectH.Server.Game.Items.LootKind { Weapon, Ammo, Medkit, ShieldCell }`, `readonly struct LootRoll(ItemKind kind, byte defId, byte rarity, ushort amount)` (속성 `Kind`, `DefId`, `Rarity`, `Amount`)
  - `LootTable { const int MaxWeight = 1_000_000; int TableCount; int TableIndex(string name); LootRoll Roll(int table, Random rng, WeaponCatalog weapons, ItemCatalog items); static LoadFile(path, ItemCatalog); static bool TryParse(json, ItemCatalog, out LootTable?, out string? error) }`
  - `GameData(WeaponCatalog, ItemCatalog, LootTable)`, `GameData.Loot`, `GameData.LootFile = "loot.json"`. 생성자는 `LootPoints.All`의 표 이름이 모두 있는지 확인한다(없으면 `ArgumentException`, `LoadDirectory`에서는 `InvalidOperationException`).
  - `ServerOptions.LootSeed` (기본 1, ≥ 0), `ServerOptions.LootRespawnSeconds` (기본 30, 0–3600)
  - 테스트: `TestGameData.LootJson`, `TestGameData.WeaponsOnlyLootJson`, `TestGameData.Loot(ItemCatalog items, string json = LootJson)`, `TestGameData.Create(int simHz = 30, float autoRange = 100f, string lootJson = LootJson)`

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/TestGameData.cs`:

변경 1/2 — 찾을 코드:

```csharp
        }
        """;

    public static ItemCatalog Items(int simHz = 30)
    {
        if (!ItemCatalog.TryParse(ItemsJson, simHz, out var items, out string? error))
```

바꿀 코드:

```csharp
        }
        """;

    // Spec §1 loot.json.
    public const string LootJson = """
        {
          "rarityWeights": { "Common": 50, "Uncommon": 25, "Rare": 15, "Epic": 7, "Legendary": 3 },
          "tables": {
            "Floor": [ { "kind": "Weapon", "weight": 35 }, { "kind": "Ammo", "weight": 35 },
                       { "kind": "Medkit", "weight": 15 }, { "kind": "ShieldCell", "weight": 15 } ],
            "Tower": [ { "kind": "Weapon", "weight": 60 }, { "kind": "Ammo", "weight": 10 },
                       { "kind": "Medkit", "weight": 15 }, { "kind": "ShieldCell", "weight": 15 } ]
          }
        }
        """;

    // Every spawn point rolls a weapon: tests that must find a weapon on the floor use this.
    public const string WeaponsOnlyLootJson = """
        {
          "rarityWeights": { "Common": 50, "Uncommon": 25, "Rare": 15, "Epic": 7, "Legendary": 3 },
          "tables": { "Floor": [ { "kind": "Weapon", "weight": 1 } ], "Tower": [ { "kind": "Weapon", "weight": 1 } ] }
        }
        """;

    public static ItemCatalog Items(int simHz = 30)
    {
        if (!ItemCatalog.TryParse(ItemsJson, simHz, out var items, out string? error))
```

변경 2/2 — 찾을 코드:

```csharp
        return items!;
    }

    public static GameData Create(int simHz = 30, float autoRange = 100f)
    {
        return new GameData(TestWeapons.Create(simHz, autoRange), Items(simHz));
    }
}
```

바꿀 코드:

```csharp
        return items!;
    }

    public static LootTable Loot(ItemCatalog items, string json = LootJson)
    {
        if (!LootTable.TryParse(json, items, out var loot, out string? error))
            throw new InvalidOperationException("Test loot is invalid: " + error);
        return loot!;
    }

    public static GameData Create(int simHz = 30, float autoRange = 100f, string lootJson = LootJson)
    {
        ItemCatalog items = Items(simHz);
        return new GameData(TestWeapons.Create(simHz, autoRange), items, Loot(items, lootJson));
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/LootTableTests.cs`를 만든다. 검증(0 가중치·모르는 등급·누락 등급·중복 kind·빈 표), 시드 재현(두 `Random` 비교, 결과 하드코딩 없음), 1만 회 분포, Tower의 무기 비율, 할당 없음을 확인한다.

```csharp
using System;
using System.IO;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class LootTableTests
{
    private readonly GameData _data = TestGameData.Create();

    private string Parse(string json)
    {
        Assert.False(LootTable.TryParse(json, _data.Items, out var loot, out string? error));
        Assert.Null(loot);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    private static string Broken(string from, string to)
    {
        Assert.Contains(from, TestGameData.LootJson);
        return TestGameData.LootJson.Replace(from, to);
    }

    private LootRoll Roll(string table, Random rng) => _data.Loot.Roll(_data.Loot.TableIndex(table), rng, _data.Weapons, _data.Items);

    [Fact]
    public void Valid_HasBothTables()
    {
        Assert.Equal(2, _data.Loot.TableCount);
        Assert.True(_data.Loot.TableIndex("Floor") >= 0);
        Assert.True(_data.Loot.TableIndex("Tower") >= 0);
        Assert.Equal(-1, _data.Loot.TableIndex("Basement"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"rarityWeights\": { \"Common\": 1, \"Uncommon\": 1, \"Rare\": 1, \"Epic\": 1, \"Legendary\": 1 }, \"tables\": {} }")]
    public void EmptyOrMalformed_IsRejected(string json)
    {
        Parse(json);
    }

    [Theory]
    [InlineData("\"Legendary\": 3", "\"Legendary\": 0")]              // zero weight
    [InlineData("\"Legendary\": 3", "\"Mythic\": 3")]                 // unknown rarity, Legendary missing
    [InlineData(", \"Legendary\": 3", "")]                            // missing rarity
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Ammo\", \"weight\": 0 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Ammo\", \"weight\": -1 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Grenade\", \"weight\": 10 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Weapon\", \"weight\": 10 }")]   // duplicate kind
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "{ \"kind\": \"Ammo\", \"weight\": 1000001 }")]
    [InlineData("{ \"kind\": \"Ammo\", \"weight\": 10 }", "null")]
    public void BadEntry_IsRejected(string from, string to)
    {
        Parse(Broken(from, to));
    }

    [Fact]
    public void EmptyTable_IsRejected()
    {
        Parse("{ \"rarityWeights\": { \"Common\": 1, \"Uncommon\": 1, \"Rare\": 1, \"Epic\": 1, \"Legendary\": 1 }, \"tables\": { \"Floor\": [] } }");
    }

    [Fact]
    public void LoadFile_MissingFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), "projecth-no-such-loot-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidOperationException>(() => LootTable.LoadFile(path, _data.Items));
    }

    // Shipped loot.json = spec §1 (same rolls as the test copy for the same seed).
    [Fact]
    public void ShippedLootJson_RollsLikeTheSpecTable()
    {
        var shipped = LootTable.LoadFile(Path.Combine(AppContext.BaseDirectory, "loot.json"), _data.Items);
        var a = new Random(7);
        var b = new Random(7);
        foreach (string table in new[] { "Floor", "Tower" })
        {
            for (int i = 0; i < 200; i++)
            {
                LootRoll x = shipped.Roll(shipped.TableIndex(table), a, _data.Weapons, _data.Items);
                LootRoll y = Roll(table, b);
                Assert.Equal((x.Kind, x.DefId, x.Rarity, x.Amount), (y.Kind, y.DefId, y.Rarity, y.Amount));
            }
        }
    }

    // D5: the same seed gives the same loot (two Random instances, never hard-coded rolls).
    [Fact]
    public void SameSeed_SameRolls_OtherSeed_OtherRolls()
    {
        var a = new Random(12345);
        var b = new Random(12345);
        var c = new Random(54321);
        int differences = 0;
        for (int i = 0; i < 100; i++)
        {
            LootRoll x = Roll("Floor", a);
            LootRoll y = Roll("Floor", b);
            LootRoll z = Roll("Floor", c);
            Assert.Equal((x.Kind, x.DefId, x.Rarity, x.Amount), (y.Kind, y.DefId, y.Rarity, y.Amount));
            if ((x.Kind, x.DefId, x.Rarity, x.Amount) != (z.Kind, z.DefId, z.Rarity, z.Amount)) differences++;
        }
        Assert.True(differences > 0);
    }

    // Spec §6: 10 000 Floor rolls roughly follow 35 / 35 / 15 / 15 and weapon rarities 50/25/15/7/3.
    [Fact]
    public void Distribution_FollowsTheWeights()
    {
        var rng = new Random(2026);
        int weapons = 0, ammo = 0, medkits = 0, cells = 0;
        var rarities = new int[5];
        var ammoTypes = new int[4];
        const int rolls = 10_000;
        for (int i = 0; i < rolls; i++)
        {
            LootRoll r = Roll("Floor", rng);
            switch (r.Kind)
            {
                case ItemKind.Weapon:
                    weapons++;
                    rarities[r.Rarity]++;
                    Assert.True(_data.Weapons.TryGetById(r.DefId, out var weapon));
                    Assert.Equal(weapon.MagazineSize, r.Amount);   // full magazine
                    break;
                case ItemKind.Ammo:
                    ammo++;
                    ammoTypes[r.DefId]++;
                    Assert.Equal(_data.Items.Ammo((AmmoType)r.DefId).PickupAmount, r.Amount);
                    break;
                default:
                    Assert.Equal(ItemKind.Consumable, r.Kind);
                    Assert.Equal(1, r.Amount);
                    if (r.DefId == (byte)ConsumableType.Medkit) medkits++; else cells++;
                    break;
            }
        }

        Assert.InRange(weapons, 3200, 3800);
        Assert.InRange(ammo, 3200, 3800);
        Assert.InRange(medkits, 1250, 1750);
        Assert.InRange(cells, 1250, 1750);
        Assert.InRange(rarities[0] / (double)weapons, 0.45, 0.55);
        Assert.InRange(rarities[4] / (double)weapons, 0.01, 0.05);
        Assert.All(rarities, count => Assert.True(count > 0));   // every rarity appears
        for (int t = 1; t <= 3; t++) Assert.InRange(ammoTypes[t] / (double)ammo, 0.28, 0.39);
    }

    [Fact]
    public void Tower_RollsMoreWeapons()
    {
        var rng = new Random(9);
        int weapons = 0;
        for (int i = 0; i < 2000; i++)
        {
            if (Roll("Tower", rng).Kind == ItemKind.Weapon) weapons++;
        }
        Assert.InRange(weapons, 1080, 1320);   // 60 %
    }

    [Fact]
    public void Roll_AllocatesNothing()
    {
        var rng = new Random(1);
        int floor = _data.Loot.TableIndex("Floor");
        _data.Loot.Roll(floor, rng, _data.Weapons, _data.Items);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) _data.Loot.Roll(floor, rng, _data.Weapons, _data.Items);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/LootPointsTests.cs`를 만든다. 박스와 겹치지 않음, 바닥이면 중앙 7 m 밖·박스 위면 닿을 수 있는 높이, Point끼리 줍기 범위 밖, 모든 Spawn 위치에서 안쪽 4곳까지 직선으로 막힘 없음을 확인한다.

```csharp
using System;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// D6: the spawn points are map data next to TestArena. They must stay valid whenever either changes.
public class LootPointsTests
{
    // Jump apex at 30 Hz is about 1.34 m (Networking.md), so a 1 m step is climbable and 1.5 m is not.
    private const float MaxStep = 1f;
    private const float PickupRange = 2f;   // D8 horizontal range

    [Fact]
    public void Count_IsSmall_AndTablesAreNamed()
    {
        Assert.InRange(LootPoints.All.Length, 1, 64);
        foreach (LootPoint p in LootPoints.All)
            Assert.True(p.Table == LootPoints.FloorTable || p.Table == LootPoints.TowerTable, p.Table);
    }

    [Fact]
    public void EveryPoint_StandsClearOfTheBoxes()
    {
        foreach (LootPoint p in LootPoints.All)
            Assert.False(MovementSimulation.OverlapsAny(p.Position, TestArena.Boxes), $"{p.Position} overlaps a box");
    }

    // Spec §6: on the floor outside the 7 m centre, or on top of a box a player can climb to.
    [Fact]
    public void EveryPoint_IsOnTheFloorOutsideTheCentre_OrOnAReachableBoxTop()
    {
        foreach (LootPoint p in LootPoints.All)
        {
            Vector3 pos = p.Position;
            if (pos.Y == 0f)
            {
                float fromCentre = MathF.Sqrt(pos.X * pos.X + pos.Z * pos.Z);
                Assert.True(fromCentre >= TestArena.ClearRadius, $"{pos}: floor point inside the clear centre");
                continue;
            }
            int top = BoxUnder(pos);
            Assert.True(top >= 0, $"{pos}: not on a box top");
            Assert.True(IsReachable(top), $"{pos}: box top out of reach");
        }
    }

    // D8: two points within pickup range of each other would make "nearest" ambiguous.
    [Fact]
    public void NoTwoPoints_AreWithinPickupRange()
    {
        ReadOnlySpan<LootPoint> points = LootPoints.All;
        for (int i = 0; i < points.Length; i++)
        {
            for (int j = i + 1; j < points.Length; j++)
            {
                Vector3 d = points[i].Position - points[j].Position;
                Assert.True(d.X * d.X + d.Z * d.Z > PickupRange * PickupRange, $"points {i} and {j}");
            }
        }
    }

    // The integration test walks straight from its spawn point to the nearest inner-ring item. That walk
    // must never be blocked, for any spawn position.
    [Fact]
    public void StraightWalk_FromEverySpawn_ToEveryInnerPoint_IsClear()
    {
        for (ushort id = 1; id <= 50; id++)
        {
            Vector3 spawn = Match.SpawnPosition(id);
            foreach (LootPoint p in LootPoints.All)
            {
                if (p.Position.Y != 0f || p.Position.Length() > 8.01f) continue;
                for (int step = 0; step <= 100; step++)
                {
                    Vector3 at = Vector3.Lerp(spawn, p.Position, step / 100f);
                    Assert.False(MovementSimulation.OverlapsAny(at, TestArena.Boxes), $"spawn {id} -> {p.Position} blocked at {at}");
                }
            }
        }
    }

    // Index of the box whose top the point stands on (footprint contains it, top at its height), or -1.
    private static int BoxUnder(Vector3 pos)
    {
        ReadOnlySpan<Box> boxes = TestArena.Boxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            Box b = boxes[i];
            if (b.Max.Y == pos.Y && pos.X > b.Min.X && pos.X < b.Max.X && pos.Z > b.Min.Z && pos.Z < b.Max.Z) return i;
        }
        return -1;
    }

    // A box top is reachable if its height above what it stands on is at most one jump, and what it
    // stands on (the floor or another box top) is reachable.
    private static bool IsReachable(int index)
    {
        Box box = TestArena.Boxes[index];
        if (box.Max.Y - box.Min.Y > MaxStep) return false;
        if (box.Min.Y == 0f) return true;
        ReadOnlySpan<Box> boxes = TestArena.Boxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            Box below = boxes[i];
            bool supports = i != index && below.Max.Y == box.Min.Y &&
                            below.Min.X < box.Max.X && below.Max.X > box.Min.X &&
                            below.Min.Z < box.Max.Z && below.Max.Z > box.Min.Z;
            if (supports && IsReachable(i)) return true;
        }
        return false;
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/ItemCatalogTests.cs` (`GameData`에 Loot, 표 참조 검사):

변경 — 찾을 코드:

```csharp
        Assert.Equal(30, data.SimHz);
        Assert.Equal(3, data.Weapons.Count);
        Assert.Equal("Legendary", data.Items.RarityName(4));
    }

    [Fact]
    public void GameData_RejectsCatalogsBuiltForDifferentSimHz()
    {
        Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(simHz: 30), TestGameData.Items(simHz: 60)));
    }
}
```

바꿀 코드:

```csharp
        Assert.Equal(30, data.SimHz);
        Assert.Equal(3, data.Weapons.Count);
        Assert.Equal("Legendary", data.Items.RarityName(4));
        Assert.True(data.Loot.TableIndex("Floor") >= 0);
        Assert.True(data.Loot.TableIndex("Tower") >= 0);
    }

    [Fact]
    public void GameData_RejectsCatalogsBuiltForDifferentSimHz()
    {
        var items = TestGameData.Items(simHz: 60);
        Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(simHz: 30), items, TestGameData.Loot(items)));
    }

    // D5 "all references exist": a table named by a LootPoint must be in loot.json.
    [Fact]
    public void GameData_RejectsLootWithoutATableTheSpawnPointsUse()
    {
        var items = TestGameData.Items();
        var noTower = TestGameData.Loot(items, """
            { "rarityWeights": { "Common": 1, "Uncommon": 1, "Rare": 1, "Epic": 1, "Legendary": 1 },
              "tables": { "Floor": [ { "kind": "Ammo", "weight": 1 } ] } }
            """);
        var ex = Assert.Throws<ArgumentException>(() => new GameData(TestWeapons.Create(), items, noTower));
        Assert.Contains("Tower", ex.Message);
    }
}
```

`Server/tests/ProjectH.Server.Tests/ServerOptionsTests.cs`:

변경 — 찾을 코드:

```csharp
        Assert.NotNull(new ServerOptions { SimHz = simHz, SnapshotEveryTicks = snapshotEveryTicks }.Validate());
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(30, 3)]
```

바꿀 코드:

```csharp
        Assert.NotNull(new ServerOptions { SimHz = simHz, SnapshotEveryTicks = snapshotEveryTicks }.Validate());
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(1, -1)]
    [InlineData(1, 3601)]
    public void Validate_RejectsBadLootSettings(int seed, int respawnSeconds)
    {
        Assert.NotNull(new ServerOptions { LootSeed = seed, LootRespawnSeconds = respawnSeconds }.Validate());
    }

    [Fact]
    public void LootDefaults_MatchSpec_AndZeroRespawnIsAllowed()
    {
        Assert.Equal(30, new ServerOptions().LootRespawnSeconds);   // D7
        Assert.Null(new ServerOptions { LootRespawnSeconds = 0, LootSeed = 0 }.Validate());
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(30, 3)]
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 실패 — `LootTable`, `LootPoints`, `ServerOptions.LootSeed` 등이 없다는 오류

- [ ] **Step 3: Spawn Point 좌표 (Shared)**

`Shared/Runtime/Simulation/LootPoints.cs`를 만든다. 바닥 12곳(안쪽 반경 8 m 4곳 포함), 1 m 박스 3개·플랫폼 받침·계단 위 5곳이다. 1.5 m 박스(점프로 못 오름)에는 두지 않는다.

```csharp
using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // One loot spawn point: where the item lies and which loot.json table fills it.
    public readonly struct LootPoint
    {
        public readonly Vector3 Position;
        public readonly string Table;

        public LootPoint(Vector3 position, string table)
        {
            Position = position;
            Table = table;
        }
    }

    // Map placement data of the test arena (Phase 4 D6), kept next to TestArena so both change together
    // when the map changes. Only the server reads it: clients learn item positions from server events.
    // game-core-rules §4 allows this (map placement constants, no rules). Checked by LootPointsTests:
    // every point stands clear of the boxes, either on the floor outside TestArena.ClearRadius or on a
    // box top a player can reach, and no two points are within pickup range of each other.
    public static class LootPoints
    {
        public const string FloorTable = "Floor";
        public const string TowerTable = "Tower";

        private static readonly LootPoint[] s_points =
        {
            // Inner ring, 8 m from the centre: the straight walk from any spawn point is clear.
            new LootPoint(new Vector3(0f, 0f, 8f), FloorTable),
            new LootPoint(new Vector3(8f, 0f, 0f), FloorTable),
            new LootPoint(new Vector3(0f, 0f, -8f), FloorTable),
            new LootPoint(new Vector3(-8f, 0f, 0f), FloorTable),

            // Outer floor, between the walls and pillars.
            new LootPoint(new Vector3(15f, 0f, -15f), FloorTable),
            new LootPoint(new Vector3(-16f, 0f, 8f), FloorTable),
            new LootPoint(new Vector3(16f, 0f, 3f), FloorTable),
            new LootPoint(new Vector3(-4f, 0f, 16f), FloorTable),
            new LootPoint(new Vector3(5f, 0f, 16f), FloorTable),
            new LootPoint(new Vector3(-16f, 0f, -4f), FloorTable),
            new LootPoint(new Vector3(4f, 0f, -16f), FloorTable),
            new LootPoint(new Vector3(16f, 0f, -10f), FloorTable),

            // Box tops: the three 1 m boxes, the platform base and its step.
            new LootPoint(new Vector3(0f, 1f, 12f), TowerTable),
            new LootPoint(new Vector3(12f, 1f, -3f), TowerTable),
            new LootPoint(new Vector3(-12f, 1f, 3f), TowerTable),
            new LootPoint(new Vector3(-12.75f, 1f, -12.75f), TowerTable),
            new LootPoint(new Vector3(-14.5f, 2f, -14.5f), TowerTable),
        };

        public static ReadOnlySpan<LootPoint> All => s_points;
    }
}
```

- [ ] **Step 4: `loot.json`과 `LootTable`**

`Server/src/ProjectH.Server/loot.json`을 만든다(spec §1 그대로).

```json
{
  "rarityWeights": { "Common": 50, "Uncommon": 25, "Rare": 15, "Epic": 7, "Legendary": 3 },
  "tables": {
    "Floor": [
      { "kind": "Weapon", "weight": 35 },
      { "kind": "Ammo", "weight": 35 },
      { "kind": "Medkit", "weight": 15 },
      { "kind": "ShieldCell", "weight": 15 }
    ],
    "Tower": [
      { "kind": "Weapon", "weight": 60 },
      { "kind": "Ammo", "weight": 10 },
      { "kind": "Medkit", "weight": 15 },
      { "kind": "ShieldCell", "weight": 15 }
    ]
  }
}
```

`Server/src/ProjectH.Server/ProjectH.Server.csproj`:

변경 — 찾을 코드:

```xml
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="weapons.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="items.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

바꿀 코드:

```xml
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="weapons.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="items.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="loot.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`Server/src/ProjectH.Server/Game/Items/LootTable.cs`를 만든다. 뽑기는 `rng.Next(total)` 한 번과 누적 가중치 순회라 할당이 없다.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

// What a loot table entry produces (D5). Ammo picks its type uniformly; Weapon picks the weapon uniformly
// and the rarity from rarityWeights.
public enum LootKind : byte
{
    Weapon,
    Ammo,
    Medkit,
    ShieldCell,
}

// One rolled item, before it has an id or a position.
public readonly struct LootRoll
{
    public LootRoll(ItemKind kind, byte defId, byte rarity, ushort amount)
    {
        Kind = kind;
        DefId = defId;
        Rarity = rarity;
        Amount = amount;
    }

    public ItemKind Kind { get; }
    public byte DefId { get; }
    public byte Rarity { get; }
    public ushort Amount { get; }
}

// loot.json (D5): weighted tables per spawn point and the weapon rarity weights. Loaded and validated
// once at startup against items.json; immutable afterwards. Roll takes the caller's Random, which the
// game loop thread owns (seeded from ServerOptions.LootSeed), so results repeat for the same seed.
public sealed class LootTable
{
    public const int MaxWeight = 1_000_000;   // keeps every table total far below int.MaxValue

    private readonly int[] _rarityWeights;
    private readonly int _rarityTotal;
    private readonly string[] _names;
    private readonly LootKind[][] _kinds;
    private readonly int[][] _weights;
    private readonly int[] _totals;

    private LootTable(int[] rarityWeights, string[] names, LootKind[][] kinds, int[][] weights)
    {
        _rarityWeights = rarityWeights;
        _names = names;
        _kinds = kinds;
        _weights = weights;
        _totals = new int[names.Length];
        foreach (int w in rarityWeights) _rarityTotal += w;
        for (int t = 0; t < names.Length; t++)
        {
            foreach (int w in weights[t]) _totals[t] += w;
        }
    }

    public int TableCount => _names.Length;

    // Index of a table name, or -1. Resolved once when the spawner is built, not per roll.
    public int TableIndex(string name) => Array.IndexOf(_names, name);

    public LootRoll Roll(int table, Random rng, WeaponCatalog weapons, ItemCatalog items)
    {
        switch (Pick(_kinds[table], _weights[table], _totals[table], rng))
        {
            case LootKind.Weapon:
                WeaponDefinition weapon = weapons[rng.Next(weapons.Count)];
                byte rarity = (byte)PickIndex(_rarityWeights, _rarityTotal, rng);
                return new LootRoll(ItemKind.Weapon, weapon.Id, rarity, weapon.MagazineSize);
            case LootKind.Ammo:
                var type = (AmmoType)(1 + rng.Next(ItemConstants.AmmoTypeCount));
                return new LootRoll(ItemKind.Ammo, (byte)type, 0, items.Ammo(type).PickupAmount);
            case LootKind.Medkit:
                return new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, 1);
            default:
                return new LootRoll(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 0, 1);
        }
    }

    public static LootTable LoadFile(string path, ItemCatalog items)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Loot data not found: {path}");
        if (!TryParse(File.ReadAllText(path), items, out var loot, out string? error))
            throw new InvalidOperationException($"Invalid loot data {path}: {error}");
        return loot!;
    }

    public static bool TryParse(string json, ItemCatalog items, out LootTable? loot, out string? error)
    {
        loot = null;
        if (!DataJson.TryDeserialize(json, out LootJson? root, out error)) return false;

        Dictionary<string, int>? rarityJson = root!.RarityWeights;
        if (rarityJson == null)
        {
            error = "\"rarityWeights\" is required.";
            return false;
        }
        var rarityWeights = new int[ItemConstants.RarityCount];
        foreach (var pair in rarityJson)
        {
            int index = items.RarityIndex(pair.Key);
            if (index < 0)
            {
                error = $"rarityWeights: unknown rarity \"{pair.Key}\" (items.json).";
                return false;
            }
            if (pair.Value < 1 || pair.Value > MaxWeight)
            {
                error = $"rarityWeights.{pair.Key}: weight must be 1-{MaxWeight}.";
                return false;
            }
            rarityWeights[index] = pair.Value;
        }
        for (int i = 0; i < rarityWeights.Length; i++)
        {
            if (rarityWeights[i] == 0)
            {
                error = $"rarityWeights: \"{items.RarityName(i)}\" is missing.";
                return false;
            }
        }

        Dictionary<string, List<EntryJson?>?>? tablesJson = root.Tables;
        if (tablesJson == null || tablesJson.Count == 0)
        {
            error = "\"tables\" must hold at least one table.";
            return false;
        }

        var names = new string[tablesJson.Count];
        var kinds = new LootKind[tablesJson.Count][];
        var weights = new int[tablesJson.Count][];
        int t = 0;
        foreach (var pair in tablesJson)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                error = "tables: a table name is empty.";
                return false;
            }
            List<EntryJson?>? entries = pair.Value;
            if (entries == null || entries.Count == 0)
            {
                error = $"tables.{pair.Key}: must hold at least one entry.";
                return false;
            }
            names[t] = pair.Key;
            kinds[t] = new LootKind[entries.Count];
            weights[t] = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                EntryJson? e = entries[i];
                if (e == null || !TryParseKind(e.Kind, out LootKind kind))
                {
                    error = $"tables.{pair.Key}[{i}]: kind must be Weapon, Ammo, Medkit or ShieldCell.";
                    return false;
                }
                if (Array.IndexOf(kinds[t], kind, 0, i) >= 0)
                {
                    error = $"tables.{pair.Key}[{i}]: duplicate kind {kind}.";
                    return false;
                }
                if (e.Weight < 1 || e.Weight > MaxWeight)
                {
                    error = $"tables.{pair.Key}[{i}]: weight must be 1-{MaxWeight}.";
                    return false;
                }
                kinds[t][i] = kind;
                weights[t][i] = e.Weight;
            }
            t++;
        }

        loot = new LootTable(rarityWeights, names, kinds, weights);
        error = null;
        return true;
    }

    private static bool TryParseKind(string? text, out LootKind kind)
    {
        switch (text)
        {
            case "Weapon": kind = LootKind.Weapon; return true;
            case "Ammo": kind = LootKind.Ammo; return true;
            case "Medkit": kind = LootKind.Medkit; return true;
            case "ShieldCell": kind = LootKind.ShieldCell; return true;
            default: kind = LootKind.Weapon; return false;
        }
    }

    private static LootKind Pick(LootKind[] kinds, int[] weights, int total, Random rng) => kinds[PickIndex(weights, total, rng)];

    // Weighted choice: one Next(total), then walk the cumulative weights. No allocation.
    private static int PickIndex(int[] weights, int total, Random rng)
    {
        int roll = rng.Next(total);
        for (int i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll < 0) return i;
        }
        return weights.Length - 1;   // not reached: roll < total
    }

    private sealed class LootJson
    {
        public Dictionary<string, int>? RarityWeights { get; set; }
        public Dictionary<string, List<EntryJson?>?>? Tables { get; set; }
    }

    private sealed class EntryJson
    {
        public string? Kind { get; set; }
        public int Weight { get; set; }
    }
}
```

- [ ] **Step 5: `GameData`에 Loot 추가**

`Server/src/ProjectH.Server/Game/GameData.cs` 전체를 다음으로 바꾼다.

```csharp
using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// All data files of the server (D2), loaded and cross-checked once at startup. Immutable afterwards.
public sealed class GameData
{
    public const string WeaponsFile = "weapons.json";
    public const string ItemsFile = "items.json";
    public const string LootFile = "loot.json";

    public GameData(WeaponCatalog weapons, ItemCatalog items, LootTable loot)
    {
        Weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Loot = loot ?? throw new ArgumentNullException(nameof(loot));
        if (weapons.SimHz != items.SimHz)
            throw new ArgumentException($"Weapons were built for SimHz {weapons.SimHz}, items for {items.SimHz}.", nameof(items));
        // Every table the map's spawn points name must exist (D5, "all references exist").
        foreach (LootPoint point in LootPoints.All)
        {
            if (loot.TableIndex(point.Table) < 0)
                throw new ArgumentException($"loot.json has no table \"{point.Table}\" used by LootPoints.", nameof(loot));
        }
    }

    public WeaponCatalog Weapons { get; }
    public ItemCatalog Items { get; }
    public LootTable Loot { get; }
    public int SimHz => Weapons.SimHz;

    // The files are copied next to the server executable. A missing or invalid file throws, so the host
    // refuses to start (same as an invalid ServerOptions value).
    public static GameData LoadDirectory(string directory, int simHz)
    {
        var weapons = WeaponCatalog.LoadFile(Path.Combine(directory, WeaponsFile), simHz);
        var items = ItemCatalog.LoadFile(Path.Combine(directory, ItemsFile), simHz);
        var loot = LootTable.LoadFile(Path.Combine(directory, LootFile), items);
        try
        {
            return new GameData(weapons, items, loot);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Invalid game data: " + ex.Message, ex);
        }
    }
}
```

- [ ] **Step 6: 설정**

`Server/src/ProjectH.Server/ServerOptions.cs`:

변경 1/2 — 찾을 코드:

```csharp
    public int BadPacketDisconnectThreshold { get; set; } = 20;
    public int DisconnectTimeoutMs { get; set; } = 5000;
    public int StatsIntervalSeconds { get; set; } = 10;

    // Each connection produces at most Connected + JoinRequested + Disconnected.
    public int ControlChannelCapacity => MaxPlayers * 3;
```

바꿀 코드:

```csharp
    public int BadPacketDisconnectThreshold { get; set; } = 20;
    public int DisconnectTimeoutMs { get; set; } = 5000;
    public int StatsIntervalSeconds { get; set; } = 10;
    // Phase 4 (D5, D7): seed of the game loop's loot Random (same seed = same loot), and how long a
    // looted spawn point stays empty. 0 turns respawning off (the battle royale rule, Match Flow phase).
    public int LootSeed { get; set; } = 1;
    public int LootRespawnSeconds { get; set; } = 30;

    // Each connection produces at most Connected + JoinRequested + Disconnected.
    public int ControlChannelCapacity => MaxPlayers * 3;
```

변경 2/2 — 찾을 코드:

```csharp
        if (BadPacketDisconnectThreshold < 1) return "BadPacketDisconnectThreshold must be positive.";
        if (DisconnectTimeoutMs < 500) return "DisconnectTimeoutMs must be at least 500.";
        if (StatsIntervalSeconds < 1) return "StatsIntervalSeconds must be positive.";
        return null;
    }
}
```

바꿀 코드:

```csharp
        if (BadPacketDisconnectThreshold < 1) return "BadPacketDisconnectThreshold must be positive.";
        if (DisconnectTimeoutMs < 500) return "DisconnectTimeoutMs must be at least 500.";
        if (StatsIntervalSeconds < 1) return "StatsIntervalSeconds must be positive.";
        if (LootSeed < 0) return "LootSeed must be 0 or more.";
        if (LootRespawnSeconds < 0 || LootRespawnSeconds > 3600) return "LootRespawnSeconds must be 0-3600 (0 = off).";
        return null;
    }
}
```

`Server/src/ProjectH.Server/appsettings.json`:

변경 — 찾을 코드:

```json
    "MaxInputMessagesPerTick": 512,
    "BadPacketDisconnectThreshold": 20,
    "DisconnectTimeoutMs": 5000,
    "StatsIntervalSeconds": 10
  }
}
```

바꿀 코드:

```json
    "MaxInputMessagesPerTick": 512,
    "BadPacketDisconnectThreshold": 20,
    "DisconnectTimeoutMs": 5000,
    "StatsIntervalSeconds": 10,
    "LootSeed": 1,
    "LootRespawnSeconds": 30
  }
}
```

- [ ] **Step 7: 하네스 규칙 (D6, spec §5)**

`.claude/skills/game-core-rules/SKILL.md` 4절:

변경 — 찾을 코드:

```markdown

예외: `Shared/Runtime/Simulation`의 이동 계산만 둔다. 여기에는 `MovementSimulation`과 그 입력·상태·상수 타입, 이동 계산이 읽는 지형 박스 데이터(`Box`, `TestArena`)와 캐릭터–박스 충돌 계산이 포함된다. Client Prediction과 서버 시뮬레이션이 같은 코드와 같은 지형으로 계산해야 예측이 어긋나지 않기 때문이다. 이 폴더에는 `System.Numerics`만 쓰는 순수 계산과 그 계산이 읽는 상수 데이터만 두고, 전투·인벤토리 등 다른 게임 규칙은 넣지 않는다.

## 5. 기존 구조를 먼저 확인한다

코드를 변경하기 전에 관련 코드를 먼저 읽는다.
```

바꿀 코드:

```markdown

예외: `Shared/Runtime/Simulation`의 이동 계산만 둔다. 여기에는 `MovementSimulation`과 그 입력·상태·상수 타입, 이동 계산이 읽는 지형 박스 데이터(`Box`, `TestArena`)와 캐릭터–박스 충돌 계산이 포함된다. Client Prediction과 서버 시뮬레이션이 같은 코드와 같은 지형으로 계산해야 예측이 어긋나지 않기 때문이다. 이 폴더에는 `System.Numerics`만 쓰는 순수 계산과 그 계산이 읽는 상수 데이터만 두고, 전투·인벤토리 등 다른 게임 규칙은 넣지 않는다.

예외 2: 맵 배치 데이터(좌표 상수). Loot Spawn Point 좌표처럼 맵과 함께 바뀌어야 하는 좌표 상수(`LootPoints`)는 `TestArena` 옆 `Shared/Runtime/Simulation`에 둔다. 맵을 바꿀 때 박스와 함께 고치기 위해서다. 좌표와 이름 같은 상수만 두고, Loot Table·난수·줍기 판정 같은 규칙은 넣지 않는다. 서버만 읽고, Client는 위치를 서버 이벤트로만 받는다.

## 5. 기존 구조를 먼저 확인한다

코드를 변경하기 전에 관련 코드를 먼저 읽는다.
```

`CLAUDE.md` 변경 이력 표 끝에 한 줄:

변경 — 찾을 코드:

```markdown
| 2026-09-30 | GitHub 푸시 하네스 추가 | `github-push` 스킬, `scan_secrets.sh` | 명시적 호출 시에만 검증 후 Commit·Push |
| 2026-09-30 | Shared 이동 계산 예외 추가 | `game-core-rules` 4절 | Client Prediction과 서버가 같은 이동 코드를 써야 함 |
| 2026-09-30 | Shared 예외에 지형 박스·충돌 계산 추가 | `game-core-rules` 4절 | 서버와 예측이 같은 충돌 결과를 내야 함 (Phase 1 D7) |
```

바꿀 코드:

```markdown
| 2026-09-30 | GitHub 푸시 하네스 추가 | `github-push` 스킬, `scan_secrets.sh` | 명시적 호출 시에만 검증 후 Commit·Push |
| 2026-09-30 | Shared 이동 계산 예외 추가 | `game-core-rules` 4절 | Client Prediction과 서버가 같은 이동 코드를 써야 함 |
| 2026-09-30 | Shared 예외에 지형 박스·충돌 계산 추가 | `game-core-rules` 4절 | 서버와 예측이 같은 충돌 결과를 내야 함 (Phase 1 D7) |
| 2026-10-01 | Shared 예외에 맵 배치 데이터(Loot Spawn Point 좌표 상수) 추가 | `game-core-rules` 4절 | 맵을 바꿀 때 박스와 함께 고쳐야 함. 규칙은 넣지 않음 (Phase 4 D6) |
```

- [ ] **Step 8: 서버 테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 346개 PASS (`Distribution_FollowsTheWeights`, `StraightWalk_FromEverySpawn_ToEveryInnerPoint_IsClear` 포함)

- [ ] **Step 9: Client 스크래치 컴파일**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0 (Shared에 `LootPoints.cs`가 생겼다. Client는 쓰지 않는다)

- [ ] **Step 10: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 4: 월드 아이템 저장소 (`WorldItems` 256칸, 초기 Loot 배치, 이벤트, Join 목록 분할 전송)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Items/WorldItems.cs`, `Game/Items/LootSpawner.cs`
- Modify: `Server/src/ProjectH.Server/Game/Match.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Game/WorldItemsTests.cs` (신규), `Game/MatchWorldItemsTests.cs` (신규), `Game/MatchTests.cs`

**Interfaces:**
- Consumes: Task 1의 `WorldItemData`, `WorldItemsPacket`, `ItemSpawnedPacket`, `ItemRemoved`; Task 3의 `LootPoint`, `LootPoints.All`, `LootRoll`, `LootTable.Roll/TableIndex`, `GameData`, `ServerOptions.LootSeed`
- Produces:
  - `ProjectH.Server.Game.Items.WorldItem { WorldItemData Data; int SpawnPoint; ulong Order; bool IsDropped }` (SpawnPoint −1 = Drop)
  - `WorldItems { const int Capacity = 256; int Count; ref readonly WorldItem this[int]; bool TryAdd(ItemKind kind, byte defId, byte rarity, ushort amount, Vector3 position, int spawnPoint, out ushort itemId, out ushort evictedId); int IndexOf(ushort itemId); void RemoveAt(int index); void SetAmount(int index, ushort amount); int FindNearest(Vector3 feet, float horizontalRange, float verticalRange) }` — 제거는 마지막 항목을 빈자리로 옮긴다(인덱스는 다음 변경 전까지만 유효)
  - `LootSpawner(ReadOnlySpan<LootPoint> points, GameData data, int seed)` — `int Count; Vector3 Position(int point); LootRoll Roll(int point)`. Point 수는 `Capacity / 2` 이하. Task 8에서 재생성 인자가 더해진다.
  - `Match(ServerOptions, GameData, SendPacket, LootPoint[]? lootPoints = null)` (Task 5에서 `loadout`이 이 앞에 끼어들고, Task 5가 이 Task의 테스트 호출을 이름 붙은 인자 `lootPoints:`로 바꾼다), `internal WorldItems WorldItems`, `internal ushort SpawnItem(in LootRoll roll, Vector3 position, int spawnPoint)` (0 = 못 넣음), `internal void RemoveItemAt(int index)`, `internal void SetItemAmount(int index, ushort amount)`. 월드 아이템을 바꾸는 곳은 이 셋뿐이고, 각각 이벤트를 전원에게 보낸 뒤 돌아온다.

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Game/WorldItemsTests.cs`를 만든다. Id 발급(한 바퀴 안에서 재사용 없음, 65535 다음 0을 건너뛰고 사용 중 Id도 건너뜀), 가득 차면 가장 오래된 Drop 제거·Spawn Point 아이템 보존, 범위 경계(수평·수직 2.0 m, 대각선은 이진 소수로 정확히 표현되지 않으므로 경계 안팎으로 확인), 동률은 작은 Id, 할당 없음.

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class WorldItemsTests
{
    private readonly WorldItems _items = new();

    private ushort Add(Vector3 position, int spawnPoint = -1, ItemKind kind = ItemKind.Ammo, byte defId = 1, ushort amount = 10)
    {
        Assert.True(_items.TryAdd(kind, defId, 0, amount, position, spawnPoint, out ushort id, out ushort evicted));
        Assert.Equal(0, evicted);
        return id;
    }

    [Fact]
    public void Add_GivesIdsFromOne_AndStoresTheData()
    {
        ushort a = Add(new Vector3(1f, 0f, 2f), kind: ItemKind.Weapon, defId: 3, amount: 0);
        ushort b = Add(new Vector3(4f, 1f, 5f), spawnPoint: 7);
        Assert.Equal(1, a);
        Assert.Equal(2, b);
        Assert.Equal(2, _items.Count);

        ref readonly WorldItem first = ref _items[_items.IndexOf(a)];
        Assert.Equal(ItemKind.Weapon, first.Data.Kind);
        Assert.Equal(3, first.Data.DefId);
        Assert.Equal(0, first.Data.Amount);
        Assert.True(first.IsDropped);
        Assert.Equal(7, _items[_items.IndexOf(b)].SpawnPoint);
    }

    [Fact]
    public void Remove_And_SetAmount()
    {
        ushort a = Add(Vector3.Zero);
        ushort b = Add(Vector3.One);
        _items.SetAmount(_items.IndexOf(b), 3);
        _items.RemoveAt(_items.IndexOf(a));

        Assert.Equal(1, _items.Count);
        Assert.Equal(-1, _items.IndexOf(a));
        Assert.Equal(3, _items[_items.IndexOf(b)].Data.Amount);
    }

    // Spec §6: ids are not reused within one lap of the 16-bit counter.
    [Fact]
    public void Ids_AreNotReusedWithinOneLap()
    {
        var seen = new HashSet<ushort>();
        for (int i = 0; i < 5000; i++)
        {
            ushort id = Add(Vector3.Zero);
            Assert.True(seen.Add(id), $"id {id} reused");
            _items.RemoveAt(_items.IndexOf(id));
        }
    }

    [Fact]
    public void Ids_WrapAfter65535_SkipZero_AndSkipIdsStillInUse()
    {
        ushort keep = Add(Vector3.Zero);                      // id 1 stays in the world for the whole lap
        for (int i = 2; i <= ushort.MaxValue; i++)
        {
            ushort id = Add(Vector3.Zero);
            _items.RemoveAt(_items.IndexOf(id));
        }
        Assert.Equal(1, keep);
        Assert.Equal(2, Add(Vector3.Zero));                   // 65535 -> 1 (in use) -> 2
    }

    // Review Focus / D13: the list never grows past 256; the oldest drop goes first, spawn-point items stay.
    [Fact]
    public void Full_EvictsTheOldestDrop_NeverASpawnPointItem()
    {
        var spawnIds = new List<ushort>();
        for (int i = 0; i < 20; i++) spawnIds.Add(Add(new Vector3(i, 0f, 0f), spawnPoint: i));
        ushort oldestDrop = Add(new Vector3(0f, 0f, 1f));
        ushort secondDrop = Add(new Vector3(0f, 0f, 2f));
        while (_items.Count < WorldItems.Capacity) Add(new Vector3(0f, 0f, 3f));

        Assert.True(_items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out ushort newId, out ushort evicted));
        Assert.Equal(oldestDrop, evicted);
        Assert.Equal(WorldItems.Capacity, _items.Count);
        Assert.True(_items.IndexOf(newId) >= 0);

        Assert.True(_items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out _, out evicted));
        Assert.Equal(secondDrop, evicted);

        // Many more drops: the count stays at the cap and every spawn-point item survives.
        for (int i = 0; i < 1000; i++) _items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out _, out _);
        Assert.Equal(WorldItems.Capacity, _items.Count);
        foreach (ushort id in spawnIds) Assert.True(_items.IndexOf(id) >= 0, $"spawn item {id} evicted");
    }

    [Fact]
    public void Full_OfSpawnPointItems_RefusesTheAdd()
    {
        for (int i = 0; i < WorldItems.Capacity; i++) Add(Vector3.Zero, spawnPoint: i);
        Assert.False(_items.TryAdd(ItemKind.Ammo, 1, 0, 5, Vector3.Zero, -1, out ushort id, out ushort evicted));
        Assert.Equal(0, id);
        Assert.Equal(0, evicted);
        Assert.Equal(WorldItems.Capacity, _items.Count);
    }

    // D8 range: 2.0 m on the ground plane and 2.0 m up or down, both inclusive.
    [Theory]
    [InlineData(2.0f, 0f, 0f, true)]
    [InlineData(2.001f, 0f, 0f, false)]
    [InlineData(1.4f, 0f, 1.4f, true)]      // 1.98 m on the diagonal
    [InlineData(1.42f, 0f, 1.42f, false)]   // 2.008 m
    [InlineData(0f, 2.0f, 0f, true)]
    [InlineData(0f, 2.01f, 0f, false)]
    [InlineData(0f, -2.0f, 0f, true)]
    [InlineData(0f, -2.01f, 0f, false)]
    public void FindNearest_RangeBoundary(float dx, float dy, float dz, bool expectedFound)
    {
        var feet = new Vector3(5f, 1f, -3f);
        Add(feet + new Vector3(dx, dy, dz));
        Assert.Equal(expectedFound, _items.FindNearest(feet, 2f, 2f) >= 0);
    }

    [Fact]
    public void FindNearest_PicksTheClosest_TiesGoToTheLowerId()
    {
        var feet = Vector3.Zero;
        ushort far = Add(new Vector3(1.5f, 0f, 0f));
        ushort tieHigh = Add(new Vector3(0f, 0f, 1f));
        ushort tieLow = Add(new Vector3(0f, 0f, -1f));
        Assert.True(tieLow > tieHigh);   // the later add has the higher id

        Assert.Equal(tieHigh, _items[_items.FindNearest(feet, 2f, 2f)].Data.ItemId);
        _items.RemoveAt(_items.IndexOf(tieHigh));
        Assert.Equal(tieLow, _items[_items.FindNearest(feet, 2f, 2f)].Data.ItemId);
        _items.RemoveAt(_items.IndexOf(tieLow));
        Assert.Equal(far, _items[_items.FindNearest(feet, 2f, 2f)].Data.ItemId);
        _items.RemoveAt(_items.IndexOf(far));
        Assert.Equal(-1, _items.FindNearest(feet, 2f, 2f));
    }

    [Fact]
    public void AddSearchRemove_AllocateNothing()
    {
        for (int i = 0; i < WorldItems.Capacity; i++) Add(new Vector3(i % 16, 0f, i / 16));
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _items.TryAdd(ItemKind.Ammo, 1, 0, 5, new Vector3(3f, 0f, 3f), -1, out _, out _);
            int nearest = _items.FindNearest(new Vector3(3f, 0f, 3f), 2f, 2f);
            _items.RemoveAt(nearest);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/MatchWorldItemsTests.cs`를 만든다. 모든 Point가 채워짐, 같은 시드 = 같은 배치, Join 목록이 두 Client에 같고 Spawn보다 먼저, **꽉 찬 월드(256개)는 6개 패킷(각 ≤ 1200 B)으로 모두 한 번씩**(Review Focus), 생성·수량 변경·제거 이벤트, 가득 찬 월드에서 제거 이벤트가 새 아이템보다 먼저, 할당 없음.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// World items through Match: initial loot, the join list, and the events every change sends.
public class MatchWorldItemsTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private readonly List<Sent> _sent = new();

    private Match NewMatch(int seed = 1, LootPoint[]? points = null) =>
        new(new ServerOptions { MaxPlayers = 4, LootSeed = seed }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), points);

    private static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    // Every item the WorldItems packets to one peer carried, in order.
    private List<WorldItemData> ListSentTo(int peer)
    {
        var items = new List<WorldItemData>();
        foreach (Sent s in _sent.Where(s => s.PeerId == peer && s.Id == PacketId.WorldItems))
        {
            Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method);
            Assert.True(s.Data.Length <= ProtocolConstants.MaxPacketSize, $"chunk of {s.Data.Length} bytes");
            var reader = Reader(s);
            Assert.True(WorldItemsPacket.TryReadHeader(ref reader, out int count));
            for (int i = 0; i < count; i++)
            {
                Assert.True(WorldItemData.TryRead(ref reader, out var item));
                items.Add(item);
            }
            Assert.Equal(0, reader.Remaining);
        }
        return items;
    }

    private static LootRoll Ammo(ushort amount = 10) => new(ItemKind.Ammo, (byte)AmmoType.Light, 0, amount);

    [Fact]
    public void NewMatch_FillsEveryLootPoint()
    {
        var match = NewMatch();
        Assert.Equal(LootPoints.All.Length, match.WorldItems.Count);
        for (int point = 0; point < LootPoints.All.Length; point++)
        {
            int index = Enumerable.Range(0, match.WorldItems.Count).Single(i => match.WorldItems[i].SpawnPoint == point);
            Assert.Equal(LootPoints.All[point].Position, match.WorldItems[index].Data.Position);
        }
    }

    // D5: the seed decides the loot, so a restart with the same seed lays out the same items.
    [Fact]
    public void SameSeed_SameLoot_OtherSeed_OtherLoot()
    {
        static string Describe(Match m) => string.Join(";", Enumerable.Range(0, m.WorldItems.Count)
            .Select(i => m.WorldItems[i].Data).Select(d => $"{d.Kind}/{d.DefId}/{d.Rarity}/{d.Amount}@{d.Position}"));

        Assert.Equal(Describe(NewMatch(seed: 42)), Describe(NewMatch(seed: 42)));
        Assert.NotEqual(Describe(NewMatch(seed: 42)), Describe(NewMatch(seed: 43)));
    }

    [Fact]
    public void Constructor_RejectsAPointWithAnUnknownTable()
    {
        var points = new[] { new LootPoint(new Vector3(0f, 0f, 8f), "Basement") };
        Assert.Throws<ArgumentException>(() => NewMatch(points: points));
    }

    // Success criterion: both clients see the same items at the same places.
    [Fact]
    public void Join_SendsTheWholeList_SameForEveryPlayer_BeforeSpawns()
    {
        var match = NewMatch();
        match.TryJoin(1, "a");
        match.TryJoin(2, "b");

        var toA = ListSentTo(1);
        var toB = ListSentTo(2);
        Assert.Equal(LootPoints.All.Length, toA.Count);
        Assert.Equal(toA, toB);

        var order = _sent.Where(s => s.PeerId == 1).Select(s => s.Id).ToList();
        Assert.True(order.IndexOf(PacketId.WorldItems) < order.IndexOf(PacketId.PlayerSpawned));
    }

    // Review Focus: a full world (256 items) goes out in 6 chunks of at most 50, each under 1200 bytes.
    [Fact]
    public void Join_WithAFullWorld_SendsSixChunks_AndEveryItemOnce()
    {
        var match = NewMatch();
        for (int i = match.WorldItems.Count; i < WorldItems.Capacity; i++) match.SpawnItem(Ammo(), new Vector3(i % 20, 0f, i / 20), -1);
        Assert.Equal(WorldItems.Capacity, match.WorldItems.Count);

        match.TryJoin(1, "a");

        Assert.Equal(6, _sent.Count(s => s.PeerId == 1 && s.Id == PacketId.WorldItems));
        var items = ListSentTo(1);
        Assert.Equal(WorldItems.Capacity, items.Count);
        Assert.Equal(WorldItems.Capacity, items.Select(item => item.ItemId).Distinct().Count());
    }

    [Fact]
    public void Spawn_Amount_Remove_AreSentToEveryone()
    {
        var match = NewMatch();
        match.TryJoin(1, "a");
        match.TryJoin(2, "b");
        _sent.Clear();

        ushort id = match.SpawnItem(Ammo(30), new Vector3(3f, 0f, 3f), -1);
        match.SetItemAmount(match.WorldItems.IndexOf(id), 12);
        match.RemoveItemAt(match.WorldItems.IndexOf(id));

        foreach (int peer in new[] { 1, 2 })
        {
            var events = _sent.Where(s => s.PeerId == peer).ToList();
            Assert.Equal(new[] { PacketId.ItemSpawned, PacketId.ItemSpawned, PacketId.ItemRemoved }, events.Select(s => s.Id));
            Assert.All(events, s => Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method));

            var r = Reader(events[0]);
            Assert.True(ItemSpawnedPacket.TryRead(ref r, out var spawned));
            Assert.Equal(id, spawned.ItemId);
            Assert.Equal(30, spawned.Amount);
            r = Reader(events[1]);
            Assert.True(ItemSpawnedPacket.TryRead(ref r, out var updated));
            Assert.Equal(id, updated.ItemId);
            Assert.Equal(12, updated.Amount);
            r = Reader(events[2]);
            Assert.True(ItemRemoved.TryRead(ref r, out var removed));
            Assert.Equal(id, removed.ItemId);
        }
        Assert.Equal(-1, match.WorldItems.IndexOf(id));
    }

    // D13: the eviction is announced before the new item, so a client's list never holds 257.
    [Fact]
    public void SpawnIntoAFullWorld_RemovesTheOldestDropFirst()
    {
        var match = NewMatch();
        ushort oldest = match.SpawnItem(Ammo(), Vector3.Zero, -1);
        while (match.WorldItems.Count < WorldItems.Capacity) match.SpawnItem(Ammo(), Vector3.Zero, -1);
        match.TryJoin(1, "a");
        _sent.Clear();

        ushort newest = match.SpawnItem(Ammo(), Vector3.One, -1);

        Assert.Equal(new[] { PacketId.ItemRemoved, PacketId.ItemSpawned }, _sent.Select(s => s.Id));
        var r = Reader(_sent[0]);
        Assert.True(ItemRemoved.TryRead(ref r, out var removed));
        Assert.Equal(oldest, removed.ItemId);
        r = Reader(_sent[1]);
        Assert.True(ItemSpawnedPacket.TryRead(ref r, out var spawned));
        Assert.Equal(newest, spawned.ItemId);
        Assert.Equal(WorldItems.Capacity, match.WorldItems.Count);
    }

    [Fact]
    public void SpawnAndRemove_AllocateNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { });
        match.TryJoin(1, "a");
        LootRoll roll = Ammo();
        ushort warm = match.SpawnItem(roll, Vector3.Zero, -1);
        match.RemoveItemAt(match.WorldItems.IndexOf(warm));

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            ushort id = match.SpawnItem(roll, Vector3.Zero, -1);
            int index = match.WorldItems.IndexOf(id);
            match.SetItemAmount(index, 3);
            match.RemoveItemAt(index);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/MatchTests.cs` (Join 순서: 17곳이라 WorldItems 패킷 1개):

변경 — 찾을 코드:

```csharp
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[1].Method);
        Assert.Equal(PacketId.ItemCatalog, toPeer[2].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[2].Method);
        Assert.Equal(PacketId.PlayerSpawned, toPeer[3].Id);

        var reader = new PacketReader(toPeer[1].Data);
        reader.TryReadPacketId(out _);
```

바꿀 코드:

```csharp
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[1].Method);
        Assert.Equal(PacketId.ItemCatalog, toPeer[2].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[2].Method);
        Assert.Equal(PacketId.WorldItems, toPeer[3].Id);   // 17 loot points: one chunk
        Assert.Equal(PacketId.PlayerSpawned, toPeer[4].Id);

        var reader = new PacketReader(toPeer[1].Data);
        reader.TryReadPacketId(out _);
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 실패 — `WorldItems`, `Match.SpawnItem` 등이 없다는 오류

- [ ] **Step 3: `WorldItems`**

`Server/src/ProjectH.Server/Game/Items/WorldItems.cs`를 만든다.

```csharp
using System.Numerics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

// One item lying in the world: what clients see (Data) plus server bookkeeping.
public struct WorldItem
{
    public WorldItemData Data;
    public int SpawnPoint;   // index of the loot point that made it, -1 for a dropped item
    public ulong Order;      // add order; the dropped item with the lowest Order is evicted first (D13)

    public bool IsDropped => SpawnPoint < 0;
}

// Every item in the world (D13): a fixed array of Capacity records, so the list, the join packets and
// the search cost are bounded. Game loop thread only; nothing here allocates after construction.
// Removal swaps the last record into the hole, so indexes are only valid until the next change.
public sealed class WorldItems
{
    public const int Capacity = 256;

    private readonly WorldItem[] _items = new WorldItem[Capacity];
    private int _count;
    private ushort _lastId;
    private ulong _nextOrder;

    public int Count => _count;

    // index 0..Count-1
    public ref readonly WorldItem this[int index] => ref _items[index];

    // Adds an item and gives it the next free id. When the store is full the oldest dropped item is
    // evicted first and its id returned in evictedId (0 when nothing was evicted). Spawn-point items are
    // never evicted; if every record is one (impossible while spawn points < Capacity) nothing is added.
    public bool TryAdd(ItemKind kind, byte defId, byte rarity, ushort amount, Vector3 position, int spawnPoint,
        out ushort itemId, out ushort evictedId)
    {
        itemId = 0;
        evictedId = 0;
        if (_count == Capacity)
        {
            int oldest = -1;
            for (int i = 0; i < _count; i++)
            {
                if (_items[i].IsDropped && (oldest < 0 || _items[i].Order < _items[oldest].Order)) oldest = i;
            }
            if (oldest < 0) return false;
            evictedId = _items[oldest].Data.ItemId;
            RemoveAt(oldest);
        }

        itemId = NextId();
        _items[_count] = new WorldItem
        {
            Data = new WorldItemData { ItemId = itemId, Kind = kind, DefId = defId, Rarity = rarity, Amount = amount, Position = position },
            SpawnPoint = spawnPoint,
            Order = _nextOrder++,
        };
        _count++;
        return true;
    }

    public int IndexOf(ushort itemId)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_items[i].Data.ItemId == itemId) return i;
        }
        return -1;
    }

    public void RemoveAt(int index)
    {
        _count--;
        _items[index] = _items[_count];
        _items[_count] = default;
    }

    public void SetAmount(int index, ushort amount)
    {
        _items[index].Data.Amount = amount;
    }

    // D8: the item nearest to feet (3D distance) among those within horizontalRange on the ground plane
    // and verticalRange up or down, or -1. Ties go to the lower ItemId, so the result does not depend on
    // the storage order (the client prompt applies the same rule to its own list).
    public int FindNearest(Vector3 feet, float horizontalRange, float verticalRange)
    {
        int best = -1;
        float bestDistance = 0f;
        float rangeSq = horizontalRange * horizontalRange;
        for (int i = 0; i < _count; i++)
        {
            Vector3 d = _items[i].Data.Position - feet;
            float horizontalSq = d.X * d.X + d.Z * d.Z;
            if (horizontalSq > rangeSq || d.Y > verticalRange || d.Y < -verticalRange) continue;
            float distance = horizontalSq + d.Y * d.Y;
            if (best < 0 || distance < bestDistance ||
                (distance == bestDistance && _items[i].Data.ItemId < _items[best].Data.ItemId))
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    // Ids go 1, 2, ... 65535, 1, ... skipping ids still in use, so a freed id is reused only after a full
    // lap (a late ItemRemoved can never name a newer item). At most Capacity ids are in use, so this ends.
    private ushort NextId()
    {
        while (true)
        {
            _lastId = _lastId == ushort.MaxValue ? (ushort)1 : (ushort)(_lastId + 1);
            if (IndexOf(_lastId) < 0) return _lastId;
        }
    }
}
```

- [ ] **Step 4: `LootSpawner`**

`Server/src/ProjectH.Server/Game/Items/LootSpawner.cs`를 만든다. 난수는 이 객체가 가진 `System.Random(seed)` 하나이고, 이 객체는 `Match`(Game Loop 스레드)가 소유한다.

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// The loot spawn points of the match and the seeded Random that fills them (D5, D6). Owned by Match on
// the game loop thread, like everything else in the match: the Random is never shared with another thread.
public sealed class LootSpawner
{
    private readonly GameData _data;
    private readonly LootPoint[] _points;
    private readonly int[] _tables;
    private readonly Random _rng;

    public LootSpawner(ReadOnlySpan<LootPoint> points, GameData data, int seed)
    {
        _data = data;
        _points = points.ToArray();
        _tables = new int[_points.Length];
        for (int i = 0; i < _points.Length; i++)
        {
            _tables[i] = data.Loot.TableIndex(_points[i].Table);
            if (_tables[i] < 0) throw new ArgumentException($"Loot point {i} names unknown table \"{_points[i].Table}\".", nameof(points));
        }
        // Spawn-point items are never evicted (D13), so they must leave most of the store for drops.
        if (_points.Length > WorldItems.Capacity / 2)
            throw new ArgumentException($"At most {WorldItems.Capacity / 2} loot points.", nameof(points));
        _rng = new Random(seed);
    }

    public int Count => _points.Length;

    public Vector3 Position(int point) => _points[point].Position;

    public LootRoll Roll(int point) => _data.Loot.Roll(_tables[point], _rng, _data.Weapons, _data.Items);
}
```

- [ ] **Step 5: `Match` 연결**

`Server/src/ProjectH.Server/Game/Match.cs` (생성자에서 모든 Point를 채움, Join 때 목록 분할 전송, 변경 3종과 이벤트):

변경 1/6 — 찾을 코드:

```csharp
    private readonly SendPacket _send;
    private readonly WeaponCatalog _weapons;
    private readonly ItemCatalog _items;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
```

바꿀 코드:

```csharp
    private readonly SendPacket _send;
    private readonly WeaponCatalog _weapons;
    private readonly ItemCatalog _items;
    private readonly WorldItems _worldItems = new();
    private readonly LootSpawner _loot;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
```

변경 2/6 — 찾을 코드:

```csharp
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    public Match(ServerOptions options, GameData data, SendPacket send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        ArgumentNullException.ThrowIfNull(data);
```

바꿀 코드:

```csharp
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    // lootPoints: test seam; null uses the map's LootPoints.All.
    public Match(ServerOptions options, GameData data, SendPacket send, LootPoint[]? lootPoints = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        ArgumentNullException.ThrowIfNull(data);
```

변경 3/6 — 찾을 코드:

```csharp
        _tickSeconds = 1f / options.SimHz;
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
        _maxRewindTicks = CombatRules.MaxRewindTicks(options.SimHz);
    }

    public uint ServerTick { get; private set; }
```

바꿀 코드:

```csharp
        _tickSeconds = 1f / options.SimHz;
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
        _maxRewindTicks = CombatRules.MaxRewindTicks(options.SimHz);

        // D6: the server fills every spawn point when the match starts; clients get the list at join.
        _loot = new LootSpawner(lootPoints ?? LootPoints.All, data, options.LootSeed);
        for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
    }

    public uint ServerTick { get; private set; }
```

변경 4/6 — 찾을 코드:

```csharp

    public bool TryGetPlayer(int peerId, out PlayerEntity player) => _playersByPeer.TryGetValue(peerId, out player!);

    public JoinResult TryJoin(int peerId, string devPlayerId)
    {
        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
```

바꿀 코드:

```csharp

    public bool TryGetPlayer(int peerId, out PlayerEntity player) => _playersByPeer.TryGetValue(peerId, out player!);

    // Test seam (InternalsVisibleTo): the store itself. Match is the only writer.
    internal WorldItems WorldItems => _worldItems;

    public JoinResult TryJoin(int peerId, string devPlayerId)
    {
        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
```

변경 5/6 — 찾을 코드:

```csharp
        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon and item data before it can show them (D4, D2).
        SendCatalogs(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
```

바꿀 코드:

```csharp
        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon and item data before it can show them (D4, D2).
        SendCatalogs(peerId);
        SendWorldItems(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
```

변경 6/6 — 찾을 코드:

```csharp
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
```

바꿀 코드:

```csharp
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D13, D14: the whole list in chunks of WorldItemsPacket.MaxItems (at most 256 items = 6 packets).
    private void SendWorldItems(int peerId)
    {
        for (int start = 0; start < _worldItems.Count; start += WorldItemsPacket.MaxItems)
        {
            int count = Math.Min(WorldItemsPacket.MaxItems, _worldItems.Count - start);
            var writer = new PacketWriter(_sendBuffer);
            WorldItemsPacket.WriteHeader(ref writer, count);
            for (int i = 0; i < count; i++) WorldItemData.Write(ref writer, _worldItems[start + i].Data);
            _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }
    }

    // Every change to the world item list goes through these three, so each one reaches every client.
    // Each finishes its send before returning: none of them keeps a PacketWriter on _sendBuffer open
    // across another send. Returns the new id, or 0 when the store could not take the item.
    internal ushort SpawnItem(in LootRoll roll, Vector3 position, int spawnPoint)
    {
        if (!_worldItems.TryAdd(roll.Kind, roll.DefId, roll.Rarity, roll.Amount, position, spawnPoint, out ushort itemId, out ushort evictedId))
            return 0;
        if (evictedId != 0) BroadcastItemRemoved(evictedId);

        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[_worldItems.Count - 1].Data);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        return itemId;
    }

    internal void RemoveItemAt(int index)
    {
        ushort itemId = _worldItems[index].Data.ItemId;
        _worldItems.RemoveAt(index);
        BroadcastItemRemoved(itemId);
    }

    // Partial pickup (D9): the rest stays where it was. Sent as ItemSpawned, which clients treat as an upsert.
    internal void SetItemAmount(int index, ushort amount)
    {
        _worldItems.SetAmount(index, amount);
        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[index].Data);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastItemRemoved(ushort itemId)
    {
        var writer = new PacketWriter(_sendBuffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = itemId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
```

- [ ] **Step 6: 서버 테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 370개 PASS (`FullMatch_SnapshotWithMaxEntities_IsDelivered`도 50명이 각자 목록을 받은 채 통과)

- [ ] **Step 7: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 5: 인벤토리 (고정 로드아웃 대체, 보유량 재장전, 등급 피해, `InventoryState`, 전투 테스트 이전)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Items/Inventory.cs`, `Game/Items/StartingLoadout.cs`
- Modify: `Server/src/ProjectH.Server/Game/Combat/WeaponRules.cs` (전체 교체), `Game/Combat/CombatRules.cs`, `Game/Combat/WeaponCatalog.cs`, `Game/Combat/WeaponDefinition.cs`, `Game/PlayerEntity.cs`, `Game/Match.cs`, `GameLoop.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Game/WeaponRulesTests.cs` (전체 교체), `Game/InventoryMatchTests.cs` (신규), `TestGameData.cs`, `Game/CombatMatchTests.cs`, `Game/LagCompensationTests.cs`, `Game/MatchTests.cs`, `Game/MatchWorldItemsTests.cs`, `Game/WeaponCatalogTests.cs`, `Integration/CombatIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1의 `InventoryState`/`InventorySlotState`, `ItemConstants.WeaponSlotCount`; Task 2의 `WeaponCatalog.TryGetById`, `ItemCatalog.DamageMultiplier/Ammo/Consumable`, `GameData`
- Produces:
  - `ProjectH.Server.Game.Items.HeldWeapon { WeaponDefinition? Weapon; byte Rarity; int MagAmmo; uint NextFireTick; bool IsEmpty }`
  - `Inventory { const int SlotCount = 3; readonly HeldWeapon[] Slots; int CurrentSlot; int Medkits; int ShieldCells; bool Changed; ref HeldWeapon Current; int GetAmmo(AmmoType); void SetAmmo(AmmoType, int); void Clear(); InventoryState ToWire() }` (Task 6·7에서 필드가 더해지고 `ToWire(uint now)`가 된다)
  - `readonly record struct LoadoutWeapon(byte WeaponId, byte Rarity)`, `StartingLoadout { static Empty; int Shield; LoadoutWeapon[] Weapons; int LightAmmo, MediumAmmo, HeavyAmmo, Medkits, ShieldCells; string? Validate(GameData); void ApplyTo(Inventory, WeaponCatalog) }` (모두 `init`)
  - `WeaponRules.ResetState(PlayerEntity)`, `UpdateReload(PlayerEntity, uint now)`, `bool SelectSlot(PlayerEntity, InputButtons)`, `bool Apply(PlayerEntity, InputButtons, bool aimValid, uint now)` (`Equip` 삭제)
  - `CombatRules.MaxShield = 100`, `CombatRules.ScaledDamage(ushort damage, float multiplier)`
  - `PlayerEntity.Inventory` (`WeaponSlot`·`Ammo[]`·`NextFireTick[]` 삭제; `Reloading`·`ReloadEndTick`·`FireHeld`는 남는다). `WeaponCatalog.SlotCount`·`LoadoutCount` 삭제.
  - `Match(ServerOptions, GameData, SendPacket, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null)` — 잘못된 로드아웃은 `ArgumentException`. `GameLoop(ServerOptions, GameData, ILogger, StartingLoadout? loadout = null)`
  - 테스트: `TestGameData.CombatLoadout`, `LoadoutShield = 50`, `LoadoutMediumAmmo = 60`, `LoadoutHeavyAmmo = 30`

- [ ] **Step 1: 실패하는 테스트 작성 — 전투 로드아웃과 규칙**

`Server/tests/ProjectH.Server.Tests/TestGameData.cs`:

변경 — 찾을 코드:

```csharp
        return items!;
    }

    public static LootTable Loot(ItemCatalog items, string json = LootJson)
    {
        if (!LootTable.TryParse(json, items, out var loot, out string? error))
```

바꿀 코드:

```csharp
        return items!;
    }

    // D1: combat tests start armed instead of picking weapons up. Slot 0 = Test Auto, slot 1 = Test Semi
    // (both Common, so damage is unscaled), shield 50: five Test Auto hits (30) take 50 + 100 to 0.
    // Reserves are large enough that no Phase 3 combat test runs out of rounds.
    public const int LoadoutShield = 50;
    public const int LoadoutMediumAmmo = 60;
    public const int LoadoutHeavyAmmo = 30;

    public static StartingLoadout CombatLoadout => new()
    {
        Shield = LoadoutShield,
        Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 0) },
        MediumAmmo = LoadoutMediumAmmo,
        HeavyAmmo = LoadoutHeavyAmmo,
    };

    public static LootTable Loot(ItemCatalog items, string json = LootJson)
    {
        if (!LootTable.TryParse(json, items, out var loot, out string? error))
```

`Server/tests/ProjectH.Server.Tests/Game/WeaponRulesTests.cs` 전체를 다음으로 바꾼다. Phase 3 테스트는 모두 남기고(탄창 → `Slots[i].MagAmmo`), 보유량에서 재장전·부족한 보유량·보유량 0이면 재장전 없음, **탄 보존**(Review Focus), 빈 칸 선택(Phase 3 `SlotBeyondLoadout_IsIgnored`를 대신하는 결정 고정), Slot3를 더한다.

```csharp
using System;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Rules on one PlayerEntity with the test catalog and the combat loadout (TestGameData.CombatLoadout):
// slot 0 Test Auto (3-tick interval, 6 rounds, 30-tick reload, Medium), slot 1 Test Semi (15-tick interval,
// 2 rounds, 60-tick reload, Heavy), slot 2 empty; 60 Medium and 30 Heavy in reserve.
public class WeaponRulesTests
{
    private readonly GameData _data = TestGameData.Create();
    private readonly PlayerEntity _player = new(1, 1, "a", 8);

    public WeaponRulesTests()
    {
        TestGameData.CombatLoadout.ApplyTo(_player.Inventory, _data.Weapons);
        WeaponRules.ResetState(_player);
    }

    private ref HeldWeapon Slot(int index) => ref _player.Inventory.Slots[index];

    private int Reserve(AmmoType type) => _player.Inventory.GetAmmo(type);

    // One server tick for a living player that sent an input, in Match order: reload done -> slot -> reload/fire.
    private bool Tick(uint now, InputButtons buttons, bool aimValid = true)
    {
        WeaponRules.UpdateReload(_player, now);
        WeaponRules.SelectSlot(_player, buttons);
        return WeaponRules.Apply(_player, buttons, aimValid, now);
    }

    [Fact]
    public void Loadout_FillsSlots_AndReserves()
    {
        Assert.Equal(0, _player.Inventory.CurrentSlot);
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.Equal(TestWeapons.SemiMagazine, Slot(1).MagAmmo);
        Assert.True(Slot(2).IsEmpty);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));
        Assert.False(_player.Reloading);
    }

    [Fact]
    public void Auto_FireEveryTick_IsLimitedToInterval()
    {
        int shots = 0;
        for (uint now = 0; now < 9; now++)
        {
            bool fired = Tick(now, InputButtons.Fire);
            Assert.Equal(now % TestWeapons.AutoInterval == 0, fired);
            if (fired) shots++;
        }
        Assert.Equal(3, shots);
        Assert.Equal(TestWeapons.AutoMagazine - 3, Slot(0).MagAmmo);
    }

    [Fact]
    public void Auto_LastRound_StartsReload_ThatBlocksFireUntilDone_AndTakesFromTheReserve()
    {
        uint now = 0;
        for (int i = 0; i < TestWeapons.AutoMagazine; i++, now += TestWeapons.AutoInterval) Assert.True(Tick(now, InputButtons.Fire));
        uint lastShot = now - TestWeapons.AutoInterval;   // 15
        Assert.Equal(0, Slot(0).MagAmmo);
        Assert.True(_player.Reloading);
        Assert.Equal(lastShot + TestWeapons.AutoReload, _player.ReloadEndTick);

        for (; now < lastShot + TestWeapons.AutoReload; now++) Assert.False(Tick(now, InputButtons.Fire));
        Assert.True(Tick(now, InputButtons.Fire));   // reload finished this tick, then fired
        Assert.Equal(TestWeapons.AutoMagazine - 1, Slot(0).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo - TestWeapons.AutoMagazine, Reserve(AmmoType.Medium));
    }

    [Fact]
    public void Semi_HoldingFire_FiresOnce_PressAgainFiresAfterInterval()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));   // switch, then fire on the press
        Assert.Equal(1, _player.Inventory.CurrentSlot);
        for (uint now = 1; now < 40; now++) Assert.False(Tick(now, InputButtons.Fire));

        Assert.False(Tick(40, InputButtons.None));
        Assert.True(Tick(41, InputButtons.Fire));
    }

    [Fact]
    public void Semi_PressDuringInterval_IsLost()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));
        Assert.False(Tick(1, InputButtons.None));
        Assert.False(Tick(5, InputButtons.Fire));        // too early: the press is used up
        Assert.False(Tick(20, InputButtons.Fire));       // still held: no new press
        Assert.False(Tick(21, InputButtons.None));
        Assert.True(Tick(22, InputButtons.Fire));
    }

    [Fact]
    public void ReloadButton_RefillsPartialMagazine_FromTheReserve_AfterReloadTime()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Assert.False(Tick(1, InputButtons.Reload));
        Assert.True(_player.Reloading);
        Assert.Equal(1u + TestWeapons.AutoReload, _player.ReloadEndTick);
        Assert.False(Tick(10, InputButtons.Fire));   // no fire while reloading
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));   // nothing moves before the end

        Tick(1 + TestWeapons.AutoReload, InputButtons.None);
        Assert.False(_player.Reloading);
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo - 1, Reserve(AmmoType.Medium));
        Assert.True(_player.Inventory.Changed);   // the owner must hear about the new reserve
    }

    [Fact]
    public void ReloadButton_WithFullMagazine_DoesNothing()
    {
        Tick(0, InputButtons.Reload);
        Assert.False(_player.Reloading);
    }

    // Spec §6: with fewer reserve rounds than the magazine needs, the reload fills what it can.
    [Fact]
    public void Reload_WithAShortReserve_FillsOnlyWhatTheReserveHas()
    {
        Slot(0).MagAmmo = 1;
        _player.Inventory.SetAmmo(AmmoType.Medium, 2);
        Tick(0, InputButtons.Reload);
        Tick(TestWeapons.AutoReload, InputButtons.None);

        Assert.Equal(3, Slot(0).MagAmmo);
        Assert.Equal(0, Reserve(AmmoType.Medium));
    }

    // Spec §6: with an empty reserve there is no reload, neither by the button nor after the last round,
    // so the snapshot never reports a reload that could not finish.
    [Fact]
    public void EmptyReserve_NoReload_EvenAfterTheLastRound()
    {
        _player.Inventory.SetAmmo(AmmoType.Medium, 0);
        Slot(0).MagAmmo = 1;
        Tick(0, InputButtons.Reload);
        Assert.False(_player.Reloading);

        Assert.True(Tick(1, InputButtons.Fire));
        Assert.Equal(0, Slot(0).MagAmmo);
        Assert.False(_player.Reloading);
        for (uint now = 2; now < 60; now++) Assert.False(Tick(now, InputButtons.Fire));
        Assert.False(_player.Reloading);
        Assert.Equal(0, Slot(0).MagAmmo);
    }

    // Review Focus: across any mix of fire, reload and slot switches, magazine + reserve of an ammo type
    // only ever goes down, by exactly one per shot of a weapon using it. A reload never makes rounds.
    [Fact]
    public void RoundsAreConserved_AcrossFireReloadAndSwitches()
    {
        var rng = new Random(77);
        InputButtons[] choices =
        {
            InputButtons.Fire, InputButtons.Fire, InputButtons.None, InputButtons.Reload,
            InputButtons.Slot1, InputButtons.Slot2, InputButtons.Slot3, InputButtons.Fire | InputButtons.Reload,
        };
        int Total(AmmoType type)
        {
            int total = _player.Inventory.GetAmmo(type);
            foreach (HeldWeapon held in _player.Inventory.Slots)
                if (!held.IsEmpty && held.Weapon!.AmmoType == type) total += held.MagAmmo;
            return total;
        }

        int medium = Total(AmmoType.Medium);
        int heavy = Total(AmmoType.Heavy);
        for (uint now = 0; now < 3000; now++)
        {
            bool fired = Tick(now, choices[rng.Next(choices.Length)]);
            // A shot comes from the slot that is current after the tick's switch.
            AmmoType shotType = fired ? _player.Inventory.Current.Weapon!.AmmoType : AmmoType.None;
            int expectedMedium = medium - (shotType == AmmoType.Medium ? 1 : 0);
            int expectedHeavy = heavy - (shotType == AmmoType.Heavy ? 1 : 0);
            medium = Total(AmmoType.Medium);
            heavy = Total(AmmoType.Heavy);
            Assert.Equal(expectedMedium, medium);
            Assert.Equal(expectedHeavy, heavy);
        }
        Assert.True(medium < TestGameData.LoadoutMediumAmmo + TestWeapons.AutoMagazine, "the test must actually shoot");
    }

    [Fact]
    public void Switch_CancelsReload_AndKeepsAmmoPerSlot()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Tick(1, InputButtons.Reload);
        Tick(2, InputButtons.Slot2);
        Assert.Equal(1, _player.Inventory.CurrentSlot);
        Assert.False(_player.Reloading);

        Tick(3, InputButtons.Slot1);
        Assert.Equal(0, _player.Inventory.CurrentSlot);
        Assert.Equal(TestWeapons.AutoMagazine - 1, Slot(0).MagAmmo);   // the cancelled reload did not refill
        Assert.Equal(TestWeapons.SemiMagazine, Slot(1).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));
    }

    [Fact]
    public void Switch_DoesNotResetTheOtherWeaponsInterval()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));   // semi: next shot at 15
        Tick(1, InputButtons.Slot1);
        Tick(2, InputButtons.Slot2);
        Assert.False(Tick(3, InputButtons.Fire));
        Assert.False(Tick(4, InputButtons.None));
        Assert.True(Tick(15, InputButtons.Fire));
    }

    [Theory]
    [InlineData(InputButtons.Slot1 | InputButtons.Slot2)]
    [InlineData(InputButtons.Slot2 | InputButtons.Slot3)]
    [InlineData(InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3)]
    public void SeveralSlotBits_AreIgnored(InputButtons bits)
    {
        Tick(0, bits);
        Assert.Equal(0, _player.Inventory.CurrentSlot);
        Tick(1, InputButtons.Slot2);
        Tick(2, bits);
        Assert.Equal(1, _player.Inventory.CurrentSlot);
    }

    // D10 decision (replaces Phase 3's "slot beyond the loadout is ignored"): an empty slot can be selected
    // (no weapon out), and then nothing fires, reloads or spends anything.
    [Fact]
    public void EmptySlot_CanBeSelected_ButNeverFiresOrReloads()
    {
        Assert.True(WeaponRules.SelectSlot(_player, InputButtons.Slot3));
        Assert.Equal(2, _player.Inventory.CurrentSlot);

        for (uint now = 0; now < 10; now++) Assert.False(Tick(now, InputButtons.Fire | InputButtons.Reload));
        Assert.False(_player.Reloading);
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, Reserve(AmmoType.Medium));
    }

    [Fact]
    public void EmptyInventory_NeverFires()
    {
        var bare = new PlayerEntity(2, 2, "b", 8);
        StartingLoadout.Empty.ApplyTo(bare.Inventory, _data.Weapons);
        for (uint now = 0; now < 10; now++) Assert.False(WeaponRules.Apply(bare, InputButtons.Fire, true, now));
        Assert.False(bare.Reloading);
    }

    [Fact]
    public void InvalidAim_DoesNotFire_NorSpendAmmoOrInterval()
    {
        Assert.False(Tick(0, InputButtons.Fire, aimValid: false));
        Assert.Equal(TestWeapons.AutoMagazine, Slot(0).MagAmmo);
        Assert.True(Tick(1, InputButtons.Fire));
    }

    [Fact]
    public void FireWithEmptyMagazineAndNoReload_StartsReload()
    {
        Slot(0).MagAmmo = 0;
        Assert.False(Tick(0, InputButtons.Fire));
        Assert.True(_player.Reloading);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/InventoryMatchTests.cs`를 만든다. D1 빈손 시작, Join 때 로드아웃의 `InventoryState`, 등급 피해 배율, `ScaledDamage` 반올림, 발사는 `InventoryState`를 보내지 않고 재장전 완료는 한 번 보냄, 빈 칸의 Snapshot 값, 부활 때 로드아웃 복원, 잘못된 로드아웃 거절.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// The inventory through Match: the D1 start, InventoryState, rarity damage, respawn.
public class InventoryMatchTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly List<Sent> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private Match _match;

    public InventoryMatchTests()
    {
        _match = NewMatch(TestGameData.CombatLoadout);
    }

    private Match NewMatch(StartingLoadout? loadout) =>
        new(new ServerOptions { MaxPlayers = 3 }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), loadout);

    private PlayerEntity Join(int peer, Vector3 feet)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        player.History.Reset(_match.ServerTick, feet);
        return player;
    }

    private void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        _match.EnqueueInput(player.PeerId, packet);
    }

    private void FireAt(PlayerEntity shooter, Vector3 point)
    {
        TestAim.YawPitch(shooter.State.Position, point, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
    }

    private List<InventoryState> InventoriesSentTo(int peer)
    {
        var list = new List<InventoryState>();
        foreach (Sent s in _sent.Where(s => s.PeerId == peer && s.Id == PacketId.InventoryState))
        {
            Assert.Equal(DeliveryMethod.ReliableOrdered, s.Method);
            var reader = new PacketReader(s.Data);
            reader.TryReadPacketId(out _);
            Assert.True(InventoryState.TryRead(ref reader, out var state));
            list.Add(state);
        }
        return list;
    }

    // D1: production starts empty-handed: no weapon, no shield, full health; the trigger does nothing.
    [Fact]
    public void DefaultStart_IsEmpty_AndFiresNothing()
    {
        _match = NewMatch(loadout: null);
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        Assert.Equal(CombatRules.MaxHealth, a.Health);
        Assert.Equal(0, a.Shield);
        Assert.All(a.Inventory.Slots, held => Assert.True(held.IsEmpty));
        var joined = Assert.Single(InventoriesSentTo(1));
        Assert.True(joined.Slot0.IsEmpty && joined.Slot1.IsEmpty && joined.Slot2.IsEmpty);
        Assert.Equal(0, joined.MediumAmmo);

        FireAt(a, b.State.Position + Chest);
        _match.Tick();
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ShotFired);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
    }

    [Fact]
    public void Join_SendsTheLoadout_AfterTheWorldList_BeforeSpawns()
    {
        Join(1, Vector3.Zero);

        var order = _sent.Where(s => s.PeerId == 1).Select(s => s.Id).ToList();
        Assert.True(order.IndexOf(PacketId.WorldItems) < order.IndexOf(PacketId.InventoryState));
        Assert.True(order.IndexOf(PacketId.InventoryState) < order.IndexOf(PacketId.PlayerSpawned));

        var state = Assert.Single(InventoriesSentTo(1));
        Assert.Equal(TestWeapons.AutoId, state.Slot0.WeaponId);
        Assert.Equal(TestWeapons.AutoMagazine, state.Slot0.MagAmmo);
        Assert.Equal(TestWeapons.SemiId, state.Slot1.WeaponId);
        Assert.True(state.Slot2.IsEmpty);
        Assert.Equal(0, state.CurrentSlot);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, state.MediumAmmo);
        Assert.Equal(TestGameData.LoadoutHeavyAmmo, state.HeavyAmmo);
        Assert.Equal(ConsumableType.None, state.Using);
    }

    // D4: damage = weapon damage x rarity multiplier, rounded. Rare (1.10) Test Auto: 30 -> 33.
    [Fact]
    public void Rarity_ScalesTheDamage()
    {
        _match = NewMatch(new StartingLoadout
        {
            Shield = TestGameData.LoadoutShield,
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 2) },
            MediumAmmo = 10,
        });
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - 33, b.Shield);
        var reader = new PacketReader(_sent.Single(s => s.PeerId == 1 && s.Id == PacketId.HitConfirmed).Data);
        reader.TryReadPacketId(out _);
        Assert.True(HitConfirmed.TryRead(ref reader, out var hit));
        Assert.Equal(33, hit.Damage);
    }

    [Theory]
    [InlineData(20, 1.05f, 21)]
    [InlineData(12, 1.2f, 14)]    // 14.4
    [InlineData(90, 1.15f, 104)]  // 103.5 rounds away from zero
    [InlineData(1, 0.1f, 1)]      // never below 1
    public void ScaledDamage_RoundsHalfAwayFromZero(int damage, float multiplier, int expected)
    {
        Assert.Equal(expected, CombatRules.ScaledDamage((ushort)damage, multiplier));
    }

    // D14: shots alone send no InventoryState (the snapshot carries the magazine); a finished reload does,
    // once, with the new reserve.
    [Fact]
    public void Shots_SendNoInventoryState_AFinishedReloadSendsOne()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        _sent.Clear();

        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f, ViewTick = _match.ServerTick });
        _match.Tick();
        Assert.Empty(InventoriesSentTo(1));

        Send(a, new InputCommand { Buttons = InputButtons.Reload });
        _match.Tick();
        Assert.True(a.Reloading);
        Assert.Empty(InventoriesSentTo(1));   // starting a reload changes nothing the owner cannot see
        while (a.Reloading) _match.Tick();

        var state = Assert.Single(InventoriesSentTo(1));
        Assert.Equal(TestWeapons.AutoMagazine, state.Slot0.MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo - 1, state.MediumAmmo);
    }

    [Fact]
    public void Snapshot_WithAnEmptyCurrentSlot_ReportsThatSlotAndNoAmmo()
    {
        var a = Join(1, Vector3.Zero);
        Send(a, new InputCommand { Buttons = InputButtons.Slot3 });
        _match.Tick();
        _match.Tick();

        var reader = new PacketReader(_sent.Last(s => s.PeerId == 1 && s.Id == PacketId.WorldSnapshot).Data);
        reader.TryReadPacketId(out _);
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var header));
        Assert.Equal(2, header.Self.WeaponSlot);
        Assert.Equal(0, header.Self.Ammo);
        Assert.Equal(0, header.Self.ReloadRemainingTicks);
    }

    [Fact]
    public void Respawn_RestoresTheLoadout_AndTellsTheOwner()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.Slots[0].MagAmmo = 1;
        a.Inventory.SetAmmo(AmmoType.Medium, 3);
        a.Alive = false;
        a.RespawnAtTick = _match.ServerTick;
        _sent.Clear();

        _match.Tick();

        Assert.True(a.Alive);
        Assert.Equal(TestGameData.LoadoutShield, a.Shield);
        var state = Assert.Single(InventoriesSentTo(1));
        Assert.Equal(TestWeapons.AutoMagazine, state.Slot0.MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, state.MediumAmmo);
        var reliable = _sent.Where(s => s.PeerId == 1 && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
        Assert.True(reliable.IndexOf(PacketId.PlayerRespawned) < reliable.IndexOf(PacketId.InventoryState));
    }

    public static IEnumerable<object[]> BadLoadouts()
    {
        yield return new object[] { new StartingLoadout { Shield = CombatRules.MaxShield + 1 } };
        yield return new object[] { new StartingLoadout { Weapons = new[] { new LoadoutWeapon(99, 0) } } };
        yield return new object[] { new StartingLoadout { Weapons = new[] { new LoadoutWeapon(1, 5) } } };
        yield return new object[] { new StartingLoadout { Weapons = new[] { new LoadoutWeapon(1, 0), new LoadoutWeapon(2, 0), new LoadoutWeapon(3, 0), new LoadoutWeapon(1, 0) } } };
        yield return new object[] { new StartingLoadout { HeavyAmmo = TestGameData.HeavyMax + 1 } };
        yield return new object[] { new StartingLoadout { LightAmmo = -1 } };
        yield return new object[] { new StartingLoadout { Medkits = TestGameData.MedkitMaxStack + 1 } };
    }

    [Theory]
    [MemberData(nameof(BadLoadouts))]
    public void Constructor_RejectsAnInvalidLoadout(StartingLoadout loadout)
    {
        Assert.Throws<ArgumentException>(() => NewMatch(loadout));
    }
}
```

- [ ] **Step 2: 실패하는 테스트 작성 — Phase 3 전투 테스트를 주입 로드아웃으로 옮기기**

테스트 내용은 약해지지 않는다. `MaxShield`를 "시작 Shield"로 쓰던 곳만 `LoadoutShield`(50)로 바꾸고, 탄창 배열을 인벤토리 칸으로 바꾼다.

`Server/tests/ProjectH.Server.Tests/Game/CombatMatchTests.cs`:

변경 1/16 — 찾을 코드:

```csharp

namespace ProjectH.Server.Tests.Game;

// Combat through Match.Tick with the test catalog (slot 0: 30 damage, 3-tick interval, 6 rounds).
// Players stand still unless a test moves them; with no input the server repeats a zero move.
public class CombatMatchTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
```

바꿀 코드:

```csharp

namespace ProjectH.Server.Tests.Game;

// Combat through Match.Tick with the test catalog and the combat loadout (TestGameData.CombatLoadout,
// slot 0: 30 damage, 3-tick interval, 6 rounds; shield 50). Players stand still unless a test moves them;
// with no input the server repeats a zero move.
public class CombatMatchTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
```

변경 2/16 — 찾을 코드:

```csharp

    private Match NewMatch(GameData data) =>
        new(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, data,
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));

    private PlayerEntity Join(int peer, Vector3 feet)
    {
```

바꿀 코드:

```csharp

    private Match NewMatch(GameData data) =>
        new(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, data,
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), TestGameData.CombatLoadout);

    private PlayerEntity Join(int peer, Vector3 feet)
    {
```

변경 3/16 — 찾을 코드:

```csharp
        _match.Tick();

        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, b.Shield);

        var hit = ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed)));
        Assert.Equal(b.EntityId, hit.TargetId);
```

바꿀 코드:

```csharp
        _match.Tick();

        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, b.Shield);

        var hit = ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed)));
        Assert.Equal(b.EntityId, hit.TargetId);
```

변경 4/16 — 찾을 코드:

```csharp
        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Empty(SentTo(2, PacketId.DamageTaken));
        Assert.Equal(8.5f, ReadShot(SentTo(1, PacketId.ShotFired)[0]).End.Z, 3);
```

바꿀 코드:

```csharp
        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Empty(SentTo(2, PacketId.DamageTaken));
        Assert.Equal(8.5f, ReadShot(SentTo(1, PacketId.ShotFired)[0]).End.Z, 3);
```

변경 5/16 — 찾을 코드:

```csharp
        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(8.5f, ReadShot(SentTo(1, PacketId.ShotFired)[0]).End.Z, 3);
    }
```

바꿀 코드:

```csharp
        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(8.5f, ReadShot(SentTo(1, PacketId.ShotFired)[0]).End.Z, 3);
    }
```

변경 6/16 — 찾을 코드:

```csharp
        FireAt(a, far.State.Position + Chest);   // the line passes through the nearer player
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, near.Shield);
        Assert.Equal(CombatRules.MaxShield, far.Shield);
    }

    [Fact]
```

바꿀 코드:

```csharp
        FireAt(a, far.State.Position + Chest);   // the line passes through the nearer player
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, near.Shield);
        Assert.Equal(TestGameData.LoadoutShield, far.Shield);
    }

    [Fact]
```

변경 7/16 — 찾을 코드:

```csharp
        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
    }
```

바꿀 코드:

```csharp
        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
    }
```

변경 8/16 — 찾을 코드:

```csharp
        FireAt(a, new Vector3(0f, 0f, 0.01f));   // straight down through its own box
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield, a.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(0f, ReadShot(SentTo(1, PacketId.ShotFired).Last()).End.Y, 3);   // stopped at the floor
    }
```

바꿀 코드:

```csharp
        FireAt(a, new Vector3(0f, 0f, 0.01f));   // straight down through its own box
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield, a.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(0f, ReadShot(SentTo(1, PacketId.ShotFired).Last()).End.Y, 3);   // stopped at the floor
    }
```

변경 9/16 — 찾을 코드:

```csharp
        FireAt(a, behind.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, behind.Shield);
        Assert.Equal(behind.EntityId, ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed))).TargetId);
    }
```

바꿀 코드:

```csharp
        FireAt(a, behind.State.Position + Chest);
        _match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, behind.Shield);
        Assert.Equal(behind.EntityId, ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed))).TargetId);
    }
```

변경 10/16 — 찾을 코드:

```csharp
        Assert.Equal(body, b.State.Position);
        Assert.Equal(_seq[2], b.LastProcessedSeq);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ShotFired);
        Assert.Equal(CombatRules.MaxShield, a.Shield);
    }

    [Fact]
```

바꿀 코드:

```csharp
        Assert.Equal(body, b.State.Position);
        Assert.Equal(_seq[2], b.LastProcessedSeq);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ShotFired);
        Assert.Equal(TestGameData.LoadoutShield, a.Shield);
    }

    [Fact]
```

변경 11/16 — 찾을 코드:

```csharp
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Ammo[0] = 1;
        KillWithFiveHits(a, b);
        uint diedAt = b.RespawnAtTick - 90;   // 3 s at 30 Hz
```

바꿀 코드:

```csharp
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Inventory.Slots[0].MagAmmo = 1;
        KillWithFiveHits(a, b);
        uint diedAt = b.RespawnAtTick - 90;   // 3 s at 30 Hz
```

변경 12/16 — 찾을 코드:

```csharp
        Assert.True(b.Alive);
        Assert.Equal(Match.SpawnPosition(b.EntityId), b.State.Position);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(CombatRules.MaxShield, b.Shield);
        Assert.Equal(TestWeapons.AutoMagazine, b.Ammo[0]);
        Assert.Equal(TestWeapons.SemiMagazine, b.Ammo[1]);
        Assert.False(b.Reloading);

        foreach (int peer in new[] { 1, 2 })
```

바꿀 코드:

```csharp
        Assert.True(b.Alive);
        Assert.Equal(Match.SpawnPosition(b.EntityId), b.State.Position);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(TestGameData.LoadoutShield, b.Shield);
        Assert.Equal(TestWeapons.AutoMagazine, b.Inventory.Slots[0].MagAmmo);
        Assert.Equal(TestWeapons.SemiMagazine, b.Inventory.Slots[1].MagAmmo);
        Assert.Equal(TestGameData.LoadoutMediumAmmo, b.Inventory.GetAmmo(AmmoType.Medium));
        Assert.False(b.Reloading);

        foreach (int peer in new[] { 1, 2 })
```

변경 13/16 — 찾을 코드:

```csharp
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Ammo[0] = 1;
        Send(b, new InputCommand { Buttons = InputButtons.Reload });
        _match.Tick();
        Assert.True(b.Reloading);
```

바꿀 코드:

```csharp
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Inventory.Slots[0].MagAmmo = 1;
        Send(b, new InputCommand { Buttons = InputButtons.Reload });
        _match.Tick();
        Assert.True(b.Reloading);
```

변경 14/16 — 찾을 코드:

```csharp
        for (int i = 0; i < 12; i++) _match.Tick();   // one real input, then 11 repeated ticks

        Assert.Single(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(TestWeapons.AutoMagazine - 1, a.Ammo[0]);
    }

    // Spec §5: a flood of fire inputs (one per tick, as fast as the server takes them) is limited to the interval.
```

바꿀 코드:

```csharp
        for (int i = 0; i < 12; i++) _match.Tick();   // one real input, then 11 repeated ticks

        Assert.Single(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(TestWeapons.AutoMagazine - 1, a.Inventory.Slots[0].MagAmmo);
    }

    // Spec §5: a flood of fire inputs (one per tick, as fast as the server takes them) is limited to the interval.
```

변경 15/16 — 찾을 코드:

```csharp
        _match.Tick();

        Assert.Empty(SentTo(1, PacketId.ShotFired));
        Assert.Equal(TestWeapons.AutoMagazine, a.Ammo[0]);
        Assert.Equal(0u, a.NextFireTick[0]);
    }

    [Fact]
```

바꿀 코드:

```csharp
        _match.Tick();

        Assert.Empty(SentTo(1, PacketId.ShotFired));
        Assert.Equal(TestWeapons.AutoMagazine, a.Inventory.Slots[0].MagAmmo);
        Assert.Equal(0u, a.Inventory.Slots[0].NextFireTick);
    }

    [Fact]
```

변경 16/16 — 찾을 코드:

```csharp

        var (selfA, _) = LastSnapshotFor(1);
        Assert.Equal(CombatRules.MaxHealth, selfA.Self.Health);
        Assert.Equal(CombatRules.MaxShield, selfA.Self.Shield);
        Assert.Equal(0, selfA.Self.WeaponSlot);
        Assert.Equal(TestWeapons.AutoMagazine - 1, selfA.Self.Ammo);
        Assert.Equal(0, selfA.Self.ReloadRemainingTicks);

        var (selfB, _) = LastSnapshotFor(2);
        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, selfB.Self.Shield);
        Assert.Equal(1, selfB.Self.WeaponSlot);
        Assert.Equal(TestWeapons.SemiMagazine, selfB.Self.Ammo);
    }
```

바꿀 코드:

```csharp

        var (selfA, _) = LastSnapshotFor(1);
        Assert.Equal(CombatRules.MaxHealth, selfA.Self.Health);
        Assert.Equal(TestGameData.LoadoutShield, selfA.Self.Shield);
        Assert.Equal(0, selfA.Self.WeaponSlot);
        Assert.Equal(TestWeapons.AutoMagazine - 1, selfA.Self.Ammo);
        Assert.Equal(0, selfA.Self.ReloadRemainingTicks);

        var (selfB, _) = LastSnapshotFor(2);
        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, selfB.Self.Shield);
        Assert.Equal(1, selfB.Self.WeaponSlot);
        Assert.Equal(TestWeapons.SemiMagazine, selfB.Self.Ammo);
    }
```

`Server/tests/ProjectH.Server.Tests/Game/LagCompensationTests.cs` (Match 4곳 모두 로드아웃 주입. 없으면 쏠 무기가 없다):

변경 1/4 — 찾을 코드:

```csharp

    public LagCompensationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), (_, _, _) => { });
        _match.TryJoin(1, "shooter");
        _match.TryJoin(2, "target");
        _match.TryGetPlayer(1, out _shooter);
```

바꿀 코드:

```csharp

    public LagCompensationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), (_, _, _) => { }, TestGameData.CombatLoadout);
        _match.TryJoin(1, "shooter");
        _match.TryJoin(2, "target");
        _match.TryGetPlayer(1, out _shooter);
```

변경 2/4 — 찾을 코드:

```csharp
            var reader = new PacketReader(data);
            if (reader.TryReadPacketId(out PacketId id) && id == PacketId.ShotFired && ShotFired.TryRead(ref reader, out var shot))
                shots.Add(shot.Start);
        });
        match.TryJoin(1, "a");
        match.TryGetPlayer(1, out var a);
        a.State.Position = new Vector3(1f, 0f, 1f);
```

바꿀 코드:

```csharp
            var reader = new PacketReader(data);
            if (reader.TryReadPacketId(out PacketId id) && id == PacketId.ShotFired && ShotFired.TryRead(ref reader, out var shot))
                shots.Add(shot.Start);
        }, TestGameData.CombatLoadout);
        match.TryJoin(1, "a");
        match.TryGetPlayer(1, out var a);
        a.State.Position = new Vector3(1f, 0f, 1f);
```

변경 3/4 — 찾을 코드:

```csharp
    [Fact]
    public void AfterRespawn_RewindFindsSpawnPoint_NotTheBody()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

바꿀 코드:

```csharp
    [Fact]
    public void AfterRespawn_RewindFindsSpawnPoint_NotTheBody()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { }, TestGameData.CombatLoadout);
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

변경 4/4 — 찾을 코드:

```csharp
        match.EnqueueInput(1, packet);
        match.Tick();

        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, target.Shield);
    }

    // Server hot path: a tick that fires, rewinds, hits and sends allocates nothing (no per-shot garbage).
    [Fact]
    public void FiringTick_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

바꿀 코드:

```csharp
        match.EnqueueInput(1, packet);
        match.Tick();

        Assert.Equal(TestGameData.LoadoutShield - TestWeapons.AutoDamage, target.Shield);
    }

    // Server hot path: a tick that fires, rewinds, hits and sends allocates nothing (no per-shot garbage).
    [Fact]
    public void FiringTick_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { }, TestGameData.CombatLoadout);
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
```

`Server/tests/ProjectH.Server.Tests/Integration/CombatIntegrationTests.cs`:

변경 1/5 — 찾을 코드:

```csharp

namespace ProjectH.Server.Tests.Integration;

// Spec §5: two headless clients over real UDP. The server runs the test catalog, whose automatic
// weapon does 30 damage every 3 ticks, so five hits take shield 50 + health 100 to 0.
public sealed class CombatIntegrationTests : IDisposable
{
    private readonly GameLoop _server;
```

바꿀 코드:

```csharp

namespace ProjectH.Server.Tests.Integration;

// Phase 3 spec §5: two headless clients over real UDP. The server runs the test catalog and the combat
// loadout (TestGameData.CombatLoadout), whose automatic weapon does 30 damage every 3 ticks, so five hits
// take shield 50 + health 100 to 0.
public sealed class CombatIntegrationTests : IDisposable
{
    private readonly GameLoop _server;
```

변경 2/5 — 찾을 코드:

```csharp
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance);
        _server.Start();
    }
```

바꿀 코드:

```csharp
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout);
        _server.Start();
    }
```

변경 3/5 — 찾을 코드:

```csharp
                                     b.SnapshotsReceived > 0, 3000, a, b), "catalog and first snapshots");
        Assert.Equal("Test Auto", a.Weapons![0].Name);
        Assert.Equal(CombatRules.MaxHealth, b.LastSelf.Health);
        Assert.Equal(CombatRules.MaxShield, b.LastSelf.Shield);

        // Both stand on the 5 m spawn ring, which has nothing between its points (TestArena.ClearRadius 7 m).
        Vector3 shooterFeet = a.LastSnapshot[a.MyEntityId].Position;
```

바꿀 코드:

```csharp
                                     b.SnapshotsReceived > 0, 3000, a, b), "catalog and first snapshots");
        Assert.Equal("Test Auto", a.Weapons![0].Name);
        Assert.Equal(CombatRules.MaxHealth, b.LastSelf.Health);
        Assert.Equal(TestGameData.LoadoutShield, b.LastSelf.Shield);

        // Both stand on the 5 m spawn ring, which has nothing between its points (TestArena.ClearRadius 7 m).
        Vector3 shooterFeet = a.LastSnapshot[a.MyEntityId].Position;
```

변경 4/5 — 찾을 코드:

```csharp
        // B: five DamageTaken from A, and its own snapshot block showed the loss before the death.
        Assert.Equal(5, b.DamageEvents.Count);
        Assert.All(b.DamageEvents, d => Assert.Equal(a.MyEntityId, d.AttackerId));
        Assert.Contains(b.SelfHistory, s => s.Shield < CombatRules.MaxShield);
        Assert.Contains(b.SelfHistory, s => s.Health > 0 && s.Health < CombatRules.MaxHealth);

        // A: five HitConfirmed, the last one the kill; everyone saw A's tracers.
```

바꿀 코드:

```csharp
        // B: five DamageTaken from A, and its own snapshot block showed the loss before the death.
        Assert.Equal(5, b.DamageEvents.Count);
        Assert.All(b.DamageEvents, d => Assert.Equal(a.MyEntityId, d.AttackerId));
        Assert.Contains(b.SelfHistory, s => s.Shield < TestGameData.LoadoutShield);
        Assert.Contains(b.SelfHistory, s => s.Health > 0 && s.Health < CombatRules.MaxHealth);

        // A: five HitConfirmed, the last one the kill; everyone saw A's tracers.
```

변경 5/5 — 찾을 코드:

```csharp
        var respawned = Assert.Single(b.Respawns);
        Assert.Equal(b.MyEntityId, respawned.EntityId);
        Assert.Equal(Match.SpawnPosition(b.MyEntityId), respawned.Position);
        Assert.True(Pump.Until(() => b.LastSelf.Health == CombatRules.MaxHealth && b.LastSelf.Shield == CombatRules.MaxShield &&
                                     b.LastSnapshot.TryGetValue(b.MyEntityId, out var self) && self.IsAlive, 3000, a, b), "alive again");
    }
}
```

바꿀 코드:

```csharp
        var respawned = Assert.Single(b.Respawns);
        Assert.Equal(b.MyEntityId, respawned.EntityId);
        Assert.Equal(Match.SpawnPosition(b.MyEntityId), respawned.Position);
        Assert.True(Pump.Until(() => b.LastSelf.Health == CombatRules.MaxHealth && b.LastSelf.Shield == TestGameData.LoadoutShield &&
                                     b.LastSnapshot.TryGetValue(b.MyEntityId, out var self) && self.IsAlive, 3000, a, b), "alive again");
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/MatchTests.cs` (Join 순서에 InventoryState):

변경 — 찾을 코드:

```csharp
        Assert.Equal(PacketId.ItemCatalog, toPeer[2].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[2].Method);
        Assert.Equal(PacketId.WorldItems, toPeer[3].Id);   // 17 loot points: one chunk
        Assert.Equal(PacketId.PlayerSpawned, toPeer[4].Id);

        var reader = new PacketReader(toPeer[1].Data);
        reader.TryReadPacketId(out _);
```

바꿀 코드:

```csharp
        Assert.Equal(PacketId.ItemCatalog, toPeer[2].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[2].Method);
        Assert.Equal(PacketId.WorldItems, toPeer[3].Id);   // 17 loot points: one chunk
        Assert.Equal(PacketId.InventoryState, toPeer[4].Id);
        Assert.Equal(PacketId.PlayerSpawned, toPeer[5].Id);

        var reader = new PacketReader(toPeer[1].Data);
        reader.TryReadPacketId(out _);
```

`Server/tests/ProjectH.Server.Tests/Game/MatchWorldItemsTests.cs` (생성자 인자 순서가 바뀌므로 이름 붙은 인자):

변경 — 찾을 코드:

```csharp

    private Match NewMatch(int seed = 1, LootPoint[]? points = null) =>
        new(new ServerOptions { MaxPlayers = 4, LootSeed = seed }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), points);

    private static PacketReader Reader(Sent s)
    {
```

바꿀 코드:

```csharp

    private Match NewMatch(int seed = 1, LootPoint[]? points = null) =>
        new(new ServerOptions { MaxPlayers = 4, LootSeed = seed }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), lootPoints: points);

    private static PacketReader Reader(Sent s)
    {
```

`Server/tests/ProjectH.Server.Tests/Game/WeaponCatalogTests.cs` (`LoadoutCount` 삭제):

변경 1/2 — 찾을 코드:

```csharp
    {
        Assert.True(WeaponCatalog.TryParse(TestWeapons.Json(), 30, out var catalog, out string? error), error);
        Assert.Equal(3, catalog!.Count);
        Assert.Equal(2, catalog.LoadoutCount);
        Assert.Equal(30, catalog.SimHz);
        Assert.Equal("Test Auto", catalog[0].Name);
        Assert.Equal(3, catalog[0].FireIntervalTicks);
```

바꿀 코드:

```csharp
    {
        Assert.True(WeaponCatalog.TryParse(TestWeapons.Json(), 30, out var catalog, out string? error), error);
        Assert.Equal(3, catalog!.Count);
        Assert.Equal(30, catalog.SimHz);
        Assert.Equal("Test Auto", catalog[0].Name);
        Assert.Equal(3, catalog[0].FireIntervalTicks);
```

변경 2/2 — 찾을 코드:

```csharp
        Assert.True(WeaponCatalog.TryParse(json, 30, out var catalog, out _));
        Assert.Equal(38, catalog![0].FireIntervalTicks);   // 37.5
        Assert.Equal(1, catalog[0].ReloadTicks);
        Assert.Equal(1, catalog.LoadoutCount);
    }

    [Theory]
```

바꿀 코드:

```csharp
        Assert.True(WeaponCatalog.TryParse(json, 30, out var catalog, out _));
        Assert.Equal(38, catalog![0].FireIntervalTicks);   // 37.5
        Assert.Equal(1, catalog[0].ReloadTicks);
        Assert.Equal(1, catalog.Count);
    }

    [Theory]
```

- [ ] **Step 3: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 실패 — `Inventory`, `StartingLoadout`, `WeaponRules.SelectSlot` 등이 없다는 오류

- [ ] **Step 4: `Inventory`, `StartingLoadout`**

`Server/src/ProjectH.Server/Game/Items/Inventory.cs`를 만든다.

```csharp
using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

// One weapon slot. Weapon null = empty slot.
public struct HeldWeapon
{
    public WeaponDefinition? Weapon;
    public byte Rarity;
    public int MagAmmo;
    // Per slot (Phase 3 D14): switching slots never skips a weapon's fire interval.
    public uint NextFireTick;

    public bool IsEmpty => Weapon == null;
}

// A player's inventory (D10), owned by the server: 3 weapon slots, the current slot, ammo reserves per
// type, Medkits and Shield Cells. Utility has no slot content yet (Projectile phase). Game loop thread only;
// fixed-size arrays, so nothing here allocates after construction.
public sealed class Inventory
{
    public const int SlotCount = ItemConstants.WeaponSlotCount;

    public readonly HeldWeapon[] Slots = new HeldWeapon[SlotCount];
    private readonly int[] _ammo = new int[ItemConstants.AmmoTypeCount];   // index = AmmoType - 1

    public int CurrentSlot;
    public int Medkits;
    public int ShieldCells;
    // Set by every change the owner must hear about (not by shots: the snapshot carries the magazine).
    // Match sends one InventoryState at the end of a tick in which it was set, then clears it (D14).
    public bool Changed;

    public ref HeldWeapon Current => ref Slots[CurrentSlot];

    public int GetAmmo(AmmoType type) => _ammo[(int)type - 1];

    public void SetAmmo(AmmoType type, int value) => _ammo[(int)type - 1] = value;

    public void Clear()
    {
        Array.Clear(Slots);
        Array.Clear(_ammo);
        CurrentSlot = 0;
        Medkits = 0;
        ShieldCells = 0;
        Changed = true;
    }

    public InventoryState ToWire()
    {
        var state = new InventoryState
        {
            CurrentSlot = (byte)CurrentSlot,
            LightAmmo = (ushort)GetAmmo(AmmoType.Light),
            MediumAmmo = (ushort)GetAmmo(AmmoType.Medium),
            HeavyAmmo = (ushort)GetAmmo(AmmoType.Heavy),
            Medkits = (byte)Medkits,
            ShieldCells = (byte)ShieldCells,
        };
        for (int i = 0; i < SlotCount; i++)
        {
            ref HeldWeapon held = ref Slots[i];
            if (held.IsEmpty) continue;
            state.SetSlot(i, new InventorySlotState { WeaponId = held.Weapon!.Id, Rarity = held.Rarity, MagAmmo = (byte)held.MagAmmo });
        }
        return state;
    }
}
```

`Server/src/ProjectH.Server/Game/Items/StartingLoadout.cs`를 만든다.

```csharp
using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Items;

public readonly record struct LoadoutWeapon(byte WeaponId, byte Rarity);

// What a player has at join and after every respawn (D1). Production uses Empty: no weapon, Shield 0,
// Health 100. Tests inject a loadout so combat tests do not depend on picking anything up.
public sealed class StartingLoadout
{
    public static readonly StartingLoadout Empty = new();

    public int Shield { get; init; }
    public LoadoutWeapon[] Weapons { get; init; } = Array.Empty<LoadoutWeapon>();   // slots 0, 1, 2 with full magazines
    public int LightAmmo { get; init; }
    public int MediumAmmo { get; init; }
    public int HeavyAmmo { get; init; }
    public int Medkits { get; init; }
    public int ShieldCells { get; init; }

    // Null when the loadout fits the data and the inventory limits.
    public string? Validate(GameData data)
    {
        if (Shield < 0 || Shield > CombatRules.MaxShield) return $"Shield must be 0-{CombatRules.MaxShield}.";
        if (Weapons.Length > Inventory.SlotCount) return $"At most {Inventory.SlotCount} weapons.";
        foreach (LoadoutWeapon w in Weapons)
        {
            if (!data.Weapons.TryGetById(w.WeaponId, out _)) return $"Unknown weapon id {w.WeaponId}.";
            if (w.Rarity >= ItemConstants.RarityCount) return $"Rarity must be 0-{ItemConstants.RarityCount - 1}.";
        }
        if (LightAmmo < 0 || LightAmmo > data.Items.Ammo(AmmoType.Light).Max ||
            MediumAmmo < 0 || MediumAmmo > data.Items.Ammo(AmmoType.Medium).Max ||
            HeavyAmmo < 0 || HeavyAmmo > data.Items.Ammo(AmmoType.Heavy).Max)
            return "Ammo must be 0 to the max of its type.";
        if (Medkits < 0 || Medkits > data.Items.Consumable(ConsumableType.Medkit).MaxStack ||
            ShieldCells < 0 || ShieldCells > data.Items.Consumable(ConsumableType.ShieldCell).MaxStack)
            return "Medkits and ShieldCells must be 0 to their maxStack.";
        return null;
    }

    // Replaces the whole inventory (Clear marks it changed, so the owner hears of it).
    public void ApplyTo(Inventory inventory, WeaponCatalog weapons)
    {
        inventory.Clear();
        for (int i = 0; i < Weapons.Length; i++)
        {
            weapons.TryGetById(Weapons[i].WeaponId, out WeaponDefinition weapon);
            inventory.Slots[i] = new HeldWeapon { Weapon = weapon, Rarity = Weapons[i].Rarity, MagAmmo = weapon.MagazineSize };
        }
        inventory.SetAmmo(AmmoType.Light, LightAmmo);
        inventory.SetAmmo(AmmoType.Medium, MediumAmmo);
        inventory.SetAmmo(AmmoType.Heavy, HeavyAmmo);
        inventory.Medkits = Medkits;
        inventory.ShieldCells = ShieldCells;
    }
}
```

- [ ] **Step 5: 무기 규칙을 인벤토리 기준으로**

`Server/src/ProjectH.Server/Game/Combat/WeaponRules.cs` 전체를 다음으로 바꾼다. 재장전은 보유량이 있어야 시작하고(없으면 `Reloading`을 켜지 않는다), 끝날 때 보유량에서 옮긴다.

```csharp
using System;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Slot switch, magazine, reload and fire interval (Phase 3 D14, Phase 4 D10), enforced in server ticks.
// Pure state changes on PlayerEntity and its Inventory: Match decides when to call them and does the ray
// and the packets. The client copies these rules for presentation (Client WeaponState); keep the two in step.
public static class WeaponRules
{
    // Nothing pending. Used at join and at respawn, after the inventory was filled.
    public static void ResetState(PlayerEntity player)
    {
        player.Reloading = false;
        player.ReloadEndTick = 0;
        player.FireHeld = false;
    }

    // Runs every tick for a living player, whether or not an input arrived. A finished reload moves rounds
    // from the reserve into the magazine; it never makes rounds (Review Focus).
    public static void UpdateReload(PlayerEntity player, uint now)
    {
        if (!player.Reloading || now < player.ReloadEndTick) return;
        player.Reloading = false;

        Inventory inventory = player.Inventory;
        ref HeldWeapon held = ref inventory.Current;
        if (held.IsEmpty) return;
        AmmoType type = held.Weapon!.AmmoType;
        int take = Math.Min(held.Weapon.MagazineSize - held.MagAmmo, inventory.GetAmmo(type));
        if (take <= 0) return;
        held.MagAmmo += take;
        inventory.SetAmmo(type, inventory.GetAmmo(type) - take);
        inventory.Changed = true;
    }

    // Exactly one of Slot1/Slot2/Slot3 selects that slot, empty or not (an empty slot means no weapon out);
    // several bits at once are contradictory and ignored. A switch cancels the reload, which belongs to the
    // weapon being put away. Returns true when the current slot changed.
    public static bool SelectSlot(PlayerEntity player, InputButtons buttons)
    {
        int target;
        switch (buttons & (InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3))
        {
            case InputButtons.Slot1: target = 0; break;
            case InputButtons.Slot2: target = 1; break;
            case InputButtons.Slot3: target = 2; break;
            default: return false;
        }
        if (target == player.Inventory.CurrentSlot) return false;
        player.Inventory.CurrentSlot = target;
        player.Reloading = false;
        return true;
    }

    // One input the client sent, in the order reload -> fire. Returns true when it fires a shot; the round
    // and the fire interval are already spent then. aimValid false (non-finite aim) is no shot, and an empty
    // slot never fires.
    public static bool Apply(PlayerEntity player, InputButtons buttons, bool aimValid, uint now)
    {
        bool fireHeld = (buttons & InputButtons.Fire) != 0;
        ref HeldWeapon held = ref player.Inventory.Current;
        if (held.IsEmpty)
        {
            player.FireHeld = fireHeld;
            return false;
        }
        WeaponDefinition weapon = held.Weapon!;

        if ((buttons & InputButtons.Reload) != 0 && !player.Reloading && held.MagAmmo < weapon.MagazineSize)
            TryStartReload(player, weapon, now);

        bool trigger = fireHeld && (weapon.Automatic || !player.FireHeld);
        player.FireHeld = fireHeld;
        if (!trigger || !aimValid || player.Reloading || now < held.NextFireTick) return false;

        if (held.MagAmmo == 0)
        {
            TryStartReload(player, weapon, now);
            return false;
        }

        held.MagAmmo--;
        held.NextFireTick = now + weapon.FireIntervalTicks;
        if (held.MagAmmo == 0) TryStartReload(player, weapon, now);
        return true;
    }

    // A reload needs reserve rounds of the weapon's type. Without them it does not start at all, so the
    // snapshot never reports a reload that cannot finish.
    private static void TryStartReload(PlayerEntity player, WeaponDefinition weapon, uint now)
    {
        if (player.Inventory.GetAmmo(weapon.AmmoType) == 0) return;
        player.Reloading = true;
        player.ReloadEndTick = now + weapon.ReloadTicks;
    }
}
```

`Server/src/ProjectH.Server/Game/Combat/CombatRules.cs` ("Spec 해석" 4, 10):

변경 1/2 — 찾을 코드:

```csharp
public static class CombatRules
{
    public const int MaxHealth = 100;   // D8 test values
    public const int MaxShield = 50;
    // Shots start at feet + 1.6 m. The client aims from the same height (AimSolver.EyeHeight).
    public const float EyeHeight = 1.6f;
    public const float MaxPitch = 89f;
```

바꿀 코드:

```csharp
public static class CombatRules
{
    public const int MaxHealth = 100;   // D8 test values
    // Phase 4 D11: Shield Cells fill up to 100. Players start with the loadout's shield (0 in production, D1).
    public const int MaxShield = 100;
    // Shots start at feet + 1.6 m. The client aims from the same height (AimSolver.EyeHeight).
    public const float EyeHeight = 1.6f;
    public const float MaxPitch = 89f;
```

변경 2/2 — 찾을 코드:

```csharp
        return health == 0;
    }

    // D14: non-finite angles are no shot. Pitch is clamped to +-89 degrees. Same convention as the
    // client camera (ShoulderCameraMath.Forward): yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down.
    public static bool TryAimDirection(float yawDegrees, float pitchDegrees, out Vector3 direction)
```

바꿀 코드:

```csharp
        return health == 0;
    }

    // Phase 4 D4: a weapon's damage times its rarity multiplier, rounded half away from zero, at least 1.
    // decimal, not float: 1.15f is 1.1499999..., and 90 x 1.15 must round to 104 as written in the data.
    // Casting the float to decimal keeps its 7 significant digits (1.15).
    public static ushort ScaledDamage(ushort damage, float multiplier)
    {
        decimal scaled = Math.Round(damage * (decimal)multiplier, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(scaled, 1m, ushort.MaxValue);
    }

    // D14: non-finite angles are no shot. Pitch is clamped to +-89 degrees. Same convention as the
    // client camera (ShoulderCameraMath.Forward): yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down.
    public static bool TryAimDirection(float yawDegrees, float pitchDegrees, out Vector3 direction)
```

`Server/src/ProjectH.Server/Game/Combat/WeaponCatalog.cs`:

변경 1/2 — 찾을 코드:

```csharp
// game loop reads it without locks.
public sealed class WeaponCatalog
{
    // Slot1 / Slot2 select the first two weapons (D5). Extra entries are valid data but not equipped.
    public const int SlotCount = 2;

    private readonly WeaponDefinition[] _weapons;
    // Weapon id -> index in _weapons, -1 when unknown. Ids are bytes, so 256 entries cover every id.
    private readonly int[] _indexById = new int[256];
```

바꿀 코드:

```csharp
// game loop reads it without locks.
public sealed class WeaponCatalog
{
    private readonly WeaponDefinition[] _weapons;
    // Weapon id -> index in _weapons, -1 when unknown. Ids are bytes, so 256 entries cover every id.
    private readonly int[] _indexById = new int[256];
```

변경 2/2 — 찾을 코드:

```csharp
    }

    public int Count => _weapons.Length;
    public int LoadoutCount => Math.Min(_weapons.Length, SlotCount);
    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the WeaponCatalog packet sent at every join.
```

바꿀 코드:

```csharp
    }

    public int Count => _weapons.Length;
    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the WeaponCatalog packet sent at every join.
```

`Server/src/ProjectH.Server/Game/Combat/WeaponDefinition.cs`:

변경 — 찾을 코드:

```csharp
namespace ProjectH.Server.Game.Combat;

// One validated weapon from weapons.json. Times are already in simulation ticks. Immutable, shared by
// every player: per-player state (ammo, next fire tick, reload) lives in PlayerEntity.
public sealed class WeaponDefinition
{
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
```

바꿀 코드:

```csharp
namespace ProjectH.Server.Game.Combat;

// One validated weapon from weapons.json. Times are already in simulation ticks. Immutable, shared by
// every player: per-player state (magazine, next fire tick) lives in the player's Inventory.
public sealed class WeaponDefinition
{
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
```

`Server/src/ProjectH.Server/Game/PlayerEntity.cs`:

변경 1/2 — 찾을 코드:

```csharp
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;
```

바꿀 코드:

```csharp
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;
```

변경 2/2 — 찾을 코드:

```csharp
    public bool Alive;
    public uint RespawnAtTick;

    // Loadout slot (0 = Slot1, 1 = Slot2). Ammo and NextFireTick are per slot, so switching weapons
    // neither refills a magazine nor skips the other weapon's fire interval.
    public int WeaponSlot;
    public readonly int[] Ammo = new int[WeaponCatalog.SlotCount];
    public readonly uint[] NextFireTick = new uint[WeaponCatalog.SlotCount];
    public bool Reloading;
    public uint ReloadEndTick;
    // Fire bit of the previous input the client sent: a semi-automatic weapon fires on the press only.
```

바꿀 코드:

```csharp
    public bool Alive;
    public uint RespawnAtTick;

    // Phase 4 (D10): weapons, magazines, per-slot fire intervals, ammo reserves and consumables. Replaced
    // by the starting loadout at join and respawn.
    public readonly Inventory Inventory = new();
    // The reload of the current slot (a switch cancels it).
    public bool Reloading;
    public uint ReloadEndTick;
    // Fire bit of the previous input the client sent: a semi-automatic weapon fires on the press only.
```

- [ ] **Step 6: `Match`와 `GameLoop`**

`Server/src/ProjectH.Server/Game/Match.cs` (로드아웃 주입·검증, Join 때 InventoryState, Tick의 칸 선택 → 재장전 → 발사, 등급 피해, Tick 끝 InventoryState, Snapshot 수신자 블록):

변경 1/11 — 찾을 코드:

```csharp
    private readonly ItemCatalog _items;
    private readonly WorldItems _worldItems = new();
    private readonly LootSpawner _loot;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
```

바꿀 코드:

```csharp
    private readonly ItemCatalog _items;
    private readonly WorldItems _worldItems = new();
    private readonly LootSpawner _loot;
    private readonly StartingLoadout _loadout;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
```

변경 2/11 — 찾을 코드:

```csharp
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    // lootPoints: test seam; null uses the map's LootPoints.All.
    public Match(ServerOptions options, GameData data, SendPacket send, LootPoint[]? lootPoints = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        ArgumentNullException.ThrowIfNull(data);
```

바꿀 코드:

```csharp
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    // Test seams: loadout null = StartingLoadout.Empty (the production start, D1); lootPoints null = the
    // map's LootPoints.All.
    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        ArgumentNullException.ThrowIfNull(data);
```

변경 3/11 — 찾을 코드:

```csharp
            throw new ArgumentException($"Game data was built for SimHz {data.SimHz}, the match runs at {options.SimHz}.", nameof(data));
        _weapons = data.Weapons;
        _items = data.Items;
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
```

바꿀 코드:

```csharp
            throw new ArgumentException($"Game data was built for SimHz {data.SimHz}, the match runs at {options.SimHz}.", nameof(data));
        _weapons = data.Weapons;
        _items = data.Items;
        _loadout = loadout ?? StartingLoadout.Empty;
        string? loadoutError = _loadout.Validate(data);
        if (loadoutError != null) throw new ArgumentException("Invalid starting loadout: " + loadoutError, nameof(loadout));
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
```

변경 4/11 — 찾을 코드:

```csharp
        // Before any spawn: the client needs the weapon and item data before it can show them (D4, D2).
        SendCatalogs(peerId);
        SendWorldItems(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
```

바꿀 코드:

```csharp
        // Before any spawn: the client needs the weapon and item data before it can show them (D4, D2).
        SendCatalogs(peerId);
        SendWorldItems(peerId);
        SendInventory(player);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
```

변경 5/11 — 찾을 코드:

```csharp

            // Same boxes as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, TestArena.Boxes);
            WeaponRules.UpdateReload(player, _weapons, now);
            // Only an input the client really sent can fire, switch or reload: the missed-input repeat
            // must never invent shots.
            if (sent) ProcessWeapons(player, input, now);
        }

        ServerTick++;
        // After every move of this tick, so all players are recorded at the same moment. A snapshot with
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position);
```

바꿀 코드:

```csharp

            // Same boxes as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, TestArena.Boxes);
            WeaponRules.UpdateReload(player, now);
            // Only an input the client really sent can act: the missed-input repeat copies the last input's
            // buttons and must never invent a switch, reload or shot.
            if (sent) ProcessActions(player, input, now);
        }

        ServerTick++;
        SendInventoryChanges();
        // After every move of this tick, so all players are recorded at the same moment. A snapshot with
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position);
```

변경 6/11 — 찾을 코드:

```csharp
        return false;
    }

    private void ProcessWeapons(PlayerEntity shooter, in InputCommand input, uint now)
    {
        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(shooter, _weapons, input.Buttons, aimValid, now))
            FireShot(shooter, direction, input.ViewTick);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
```

바꿀 코드:

```csharp
        return false;
    }

    // One real input of a living player, in the spec §2 order: slot -> reload -> fire.
    private void ProcessActions(PlayerEntity player, in InputCommand input, uint now)
    {
        WeaponRules.SelectSlot(player, input.Buttons);

        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(player, input.Buttons, aimValid, now))
            FireShot(player, direction, input.ViewTick);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
```

변경 7/11 — 찾을 코드:

```csharp
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        WeaponDefinition weapon = _weapons[shooter.WeaponSlot];
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, TestArena.Boxes);
        double rewindTick = CombatRules.ClampViewTick(viewTick, ServerTick, _maxRewindTicks);
```

바꿀 코드:

```csharp
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        ref HeldWeapon held = ref shooter.Inventory.Current;
        WeaponDefinition weapon = held.Weapon!;   // Apply only fires a filled slot
        ushort damage = CombatRules.ScaledDamage(weapon.Damage, _items.DamageMultiplier(held.Rarity));
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, TestArena.Boxes);
        double rewindTick = CombatRules.ClampViewTick(viewTick, ServerTick, _maxRewindTicks);
```

변경 8/11 — 찾을 코드:

```csharp
        ShotFired.Write(ref writer, new ShotFired { ShooterId = shooter.EntityId, Start = origin, End = origin + direction * nearest });
        Broadcast(writer.WrittenSpan, DeliveryMethod.Unreliable);

        if (target != null) ApplyHit(shooter, target, weapon.Damage);
    }

    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
```

바꿀 코드:

```csharp
        ShotFired.Write(ref writer, new ShotFired { ShooterId = shooter.EntityId, Start = origin, End = origin + direction * nearest });
        Broadcast(writer.WrittenSpan, DeliveryMethod.Unreliable);

        if (target != null) ApplyHit(shooter, target, damage);
    }

    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
```

변경 9/11 — 찾을 코드:

```csharp
    {
        player.Alive = true;
        player.Health = CombatRules.MaxHealth;
        player.Shield = CombatRules.MaxShield;
        WeaponRules.Equip(player, _weapons);
    }

    private void Broadcast(ReadOnlySpan<byte> data, DeliveryMethod method)
```

바꿀 코드:

```csharp
    {
        player.Alive = true;
        player.Health = CombatRules.MaxHealth;
        player.Shield = _loadout.Shield;
        _loadout.ApplyTo(player.Inventory, _weapons);
        WeaponRules.ResetState(player);
    }

    private void Broadcast(ReadOnlySpan<byte> data, DeliveryMethod method)
```

변경 10/11 — 찾을 코드:

```csharp
        {
            Health = (byte)Math.Clamp(p.Health, 0, byte.MaxValue),
            Shield = (byte)Math.Clamp(p.Shield, 0, byte.MaxValue),
            WeaponSlot = (byte)p.WeaponSlot,
            Ammo = (byte)p.Ammo[p.WeaponSlot],
            ReloadRemainingTicks = reloadRemaining,
        };
    }
```

바꿀 코드:

```csharp
        {
            Health = (byte)Math.Clamp(p.Health, 0, byte.MaxValue),
            Shield = (byte)Math.Clamp(p.Shield, 0, byte.MaxValue),
            WeaponSlot = (byte)p.Inventory.CurrentSlot,
            Ammo = (byte)p.Inventory.Current.MagAmmo,   // 0 for an empty slot
            ReloadRemainingTicks = reloadRemaining,
        };
    }
```

변경 11/11 — 찾을 코드:

```csharp
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D13, D14: the whole list in chunks of WorldItemsPacket.MaxItems (at most 256 items = 6 packets).
    private void SendWorldItems(int peerId)
    {
```

바꿀 코드:

```csharp
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D14: the owner's inventory, only at the end of a tick in which it changed (shots do not count).
    private void SendInventoryChanges()
    {
        foreach (var p in _players)
        {
            if (p.Inventory.Changed) SendInventory(p);
        }
    }

    private void SendInventory(PlayerEntity player)
    {
        player.Inventory.Changed = false;
        var writer = new PacketWriter(_sendBuffer);
        InventoryState.Write(ref writer, player.Inventory.ToWire());
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D13, D14: the whole list in chunks of WorldItemsPacket.MaxItems (at most 256 items = 6 packets).
    private void SendWorldItems(int peerId)
    {
```

`Server/src/ProjectH.Server/GameLoop.cs`:

변경 1/3 — 찾을 코드:

```csharp
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
```

바꿀 코드:

```csharp
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
```

변경 2/3 — 찾을 코드:

```csharp
    private long _lateTicksSkipped;
    private bool _disposed;

    public GameLoop(ServerOptions options, GameData data, ILogger logger)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));
```

바꿀 코드:

```csharp
    private long _lateTicksSkipped;
    private bool _disposed;

    // loadout: test seam (D1); null = StartingLoadout.Empty, the production start.
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));
```

변경 3/3 — 찾을 코드:

```csharp
            IPv6Enabled = false,
        };
        listener.Manager = _net;
        _match = new Match(options, data, SendToPeer);
    }

    public int LocalPort => _net.LocalPort;
```

바꿀 코드:

```csharp
            IPv6Enabled = false,
        };
        listener.Manager = _net;
        _match = new Match(options, data, SendToPeer, loadout);
    }

    public int LocalPort => _net.LocalPort;
```

- [ ] **Step 7: 서버 테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 393개 PASS. Phase 3 전투 테스트(`FiveHits_Kill_AndEveryoneIsTold`, `FiringTick_AllocatesNothing`, `Shot_RewindsTargetToViewTick` 등)와 `Shooting_DamagesThenKills_ThenTargetRespawns`가 주입 로드아웃으로 그대로 통과한다.

- [ ] **Step 8: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 6: 줍기·버리기·사망 Drop (서버 대상 선택, 무기 교환, 부분 줍기, 원형 Drop, 보존)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Items/ItemRules.cs`
- Modify: `Server/src/ProjectH.Server/Game/Items/Inventory.cs`, `Game/Match.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Game/PickupDropTests.cs` (신규)

**Interfaces:**
- Consumes: Task 4의 `WorldItems.FindNearest`, `Match.SpawnItem/RemoveItemAt/SetItemAmount`; Task 5의 `Inventory`, `HeldWeapon`, `StartingLoadout`; Phase 3의 `HitScan.TraceWorld`; Task 1의 `PickupResult`
- Produces:
  - `ProjectH.Server.Game.Items.ItemRules { const float PickupRange = 2f, PickupHeight = 2f, DropDistance = 1f, DeathDropRadius = 1f; static int Room(Inventory, ItemCatalog, ItemKind, byte defId); static void AddStack(Inventory, ItemKind, byte defId, int amount); static Vector3 Offset(float yawDegrees, float distance); static Vector3 DropPosition(Vector3 feet, Vector3 offset, ReadOnlySpan<Box> world) }`
  - `Inventory.DroppedFireLockTick` ("Spec 해석" 9, `Clear`가 0으로)
  - `Match`의 Tick 순서: 칸 선택 → **버리기(Drop 비트)** → **줍기(Interact 비트)** → 재장전 → 발사. 사망(`Kill`)은 `PlayerDied`를 보낸 뒤 인벤토리 전체를 원형으로 떨어뜨리고 비운다. 줍기는 `PickupResult`를 누른 사람에게 보낸다.

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Game/PickupDropTests.cs`를 만든다. 월드는 Loot Point 없이 시작하고 테스트가 아이템을 놓는다. 범위 밖이면 `NothingInRange`, 2.0 m 경계, **같은 Tick 두 명**(Review Focus), 첫 빈 칸, 빈손이면 손에 듦, 칸이 다 차면 교환하고 원래 무기는 그 자리에, 탄약 부분 줍기(수량 줄인 Upsert), 한도면 `Full`, 소모품 스택 한도, G는 1 m 앞에 탄창째, 빈손 G는 무시, 벽 안·얇은 벽 너머면 발밑, 박스 가장자리 너머면 바닥, 버렸다 다시 주워도 발사 간격 유지, 누락 입력 반복이 줍기·버리기를 반복하지 않음, 사망 Drop(원형·수량·`PlayerDied` 뒤), **아이템 보존**(Review Focus), **반복 사망에도 256 유지·Spawn Point 아이템 보존**(Review Focus), 줍기 Tick 할당 없음.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// D8, D9, D12 through Match.Tick. The world starts empty (no loot points) unless a test says otherwise,
// so every item in it was put there by the test.
public class PickupDropTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly List<Sent> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private Match _match;

    public PickupDropTests()
    {
        _match = NewMatch(TestGameData.CombatLoadout);
    }

    private Match NewMatch(StartingLoadout loadout, LootPoint[]? lootPoints = null) =>
        new(new ServerOptions { MaxPlayers = 3 }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)), loadout,
            lootPoints ?? Array.Empty<LootPoint>());

    private PlayerEntity Join(int peer, Vector3 feet, float yaw = 0f)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        player.State.Yaw = yaw;
        player.History.Reset(_match.ServerTick, feet);
        return player;
    }

    private void Send(PlayerEntity player, InputButtons buttons, float yaw = float.NaN)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        _seq[player.PeerId] = ++seq;
        var packet = new PlayerInputPacket { Count = 1 };
        // Yaw NaN keeps the player's facing (MovementSimulation ignores a non-finite yaw).
        packet.Set(0, new InputCommand { Seq = seq, Buttons = buttons, Yaw = yaw, AimPitch = 10f });
        _match.EnqueueInput(player.PeerId, packet);
    }

    private void Press(PlayerEntity player, InputButtons buttons)
    {
        Send(player, buttons);
        _match.Tick();
    }

    private ushort Put(ItemKind kind, byte defId, ushort amount, Vector3 position, byte rarity = 0) =>
        _match.SpawnItem(new LootRoll(kind, defId, rarity, amount), position, -1);

    private int IndexOf(ushort itemId) => _match.WorldItems.IndexOf(itemId);

    private List<WorldItemData> WorldList() =>
        Enumerable.Range(0, _match.WorldItems.Count).Select(i => _match.WorldItems[i].Data).ToList();

    private static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    private List<PickupResult> ResultsTo(int peer) => _sent.Where(s => s.PeerId == peer && s.Id == PacketId.PickupResult)
        .Select(s => { var r = Reader(s); Assert.True(PickupResult.TryRead(ref r, out var v)); return v; }).ToList();

    private InventoryState LastInventoryTo(int peer)
    {
        var r = Reader(_sent.Last(s => s.PeerId == peer && s.Id == PacketId.InventoryState));
        Assert.True(InventoryState.TryRead(ref r, out var state));
        return state;
    }

    private List<WorldItemData> SpawnedTo(int peer) => _sent.Where(s => s.PeerId == peer && s.Id == PacketId.ItemSpawned)
        .Select(s => { var r = Reader(s); Assert.True(ItemSpawnedPacket.TryRead(ref r, out var v)); return v; }).ToList();

    [Fact]
    public void Pickup_WithNothingInRange_ReportsIt_AndChangesNothing()
    {
        var a = Join(1, Vector3.Zero);
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 30, new Vector3(5f, 0f, 0f));
        _sent.Clear();

        Press(a, InputButtons.Interact);

        var result = Assert.Single(ResultsTo(1));
        Assert.Equal(PickupResultCode.NothingInRange, result.Result);
        Assert.Equal(0, result.ItemId);
        Assert.Equal(1, _match.WorldItems.Count);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.InventoryState);
    }

    // Review Focus / spec §6: 2.0 m is in range, just beyond is not. The client cannot ask for a far item:
    // it sends no item id at all (D8).
    [Theory]
    [InlineData(2.0f, true)]
    [InlineData(2.05f, false)]
    public void Pickup_RangeBoundary(float distance, bool expectedTaken)
    {
        var a = Join(1, new Vector3(-3f, 0f, 0f));
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Light, 30, new Vector3(-3f + distance, 0f, 0f));

        Press(a, InputButtons.Interact);

        Assert.Equal(expectedTaken, IndexOf(id) < 0);
        Assert.Equal(expectedTaken ? 30 : 0, a.Inventory.GetAmmo(AmmoType.Light));
        Assert.Equal(expectedTaken ? PickupResultCode.Ok : PickupResultCode.NothingInRange, ResultsTo(1).Single().Result);
    }

    // Review Focus: two players press E for the same item in the same tick. The first processed takes it;
    // the second finds nothing, and the item is removed exactly once.
    [Fact]
    public void TwoPlayers_SameTick_SameItem_OnlyTheFirstGetsIt()
    {
        var a = Join(1, new Vector3(-1f, 0f, 0f));
        var b = Join(2, new Vector3(1f, 0f, 0f));
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Light, 30, Vector3.Zero);
        _sent.Clear();

        Send(a, InputButtons.Interact);
        Send(b, InputButtons.Interact);
        _match.Tick();

        Assert.Equal(30, a.Inventory.GetAmmo(AmmoType.Light));
        Assert.Equal(0, b.Inventory.GetAmmo(AmmoType.Light));
        Assert.Equal(PickupResultCode.Ok, ResultsTo(1).Single().Result);
        Assert.Equal(PickupResultCode.NothingInRange, ResultsTo(2).Single().Result);
        foreach (int peer in new[] { 1, 2 })
        {
            var removed = Assert.Single(_sent, s => s.PeerId == peer && s.Id == PacketId.ItemRemoved);
            var r = Reader(removed);
            Assert.True(ItemRemoved.TryRead(ref r, out var rm));
            Assert.Equal(id, rm.ItemId);
        }
    }

    [Fact]
    public void Weapon_GoesIntoTheFirstEmptySlot_CurrentSlotStays()
    {
        var a = Join(1, Vector3.Zero);   // slots: Auto, Semi, empty
        ushort id = Put(ItemKind.Weapon, TestWeapons.LightId, 7, new Vector3(1f, 0f, 0f), rarity: 3);
        _sent.Clear();

        Press(a, InputButtons.Interact);

        Assert.Equal(-1, IndexOf(id));
        Assert.Equal(TestWeapons.LightId, a.Inventory.Slots[2].Weapon!.Id);
        Assert.Equal(3, a.Inventory.Slots[2].Rarity);
        Assert.Equal(7, a.Inventory.Slots[2].MagAmmo);
        Assert.Equal(0, a.Inventory.CurrentSlot);
        var state = LastInventoryTo(1);
        Assert.Equal(TestWeapons.LightId, state.Slot2.WeaponId);
        Assert.Equal(7, state.Slot2.MagAmmo);
    }

    // Empty-handed (the current slot is empty), the picked weapon also goes into the hand.
    [Fact]
    public void Weapon_PickedUpEmptyHanded_IsTakenInHand()
    {
        _match = NewMatch(new StartingLoadout { Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) } });
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Slot3);
        Assert.Equal(2, a.Inventory.CurrentSlot);
        Put(ItemKind.Weapon, TestWeapons.SemiId, 2, new Vector3(1f, 0f, 0f));

        Press(a, InputButtons.Interact);

        Assert.Equal(TestWeapons.SemiId, a.Inventory.Slots[1].Weapon!.Id);   // first empty slot
        Assert.Equal(1, a.Inventory.CurrentSlot);                             // and in hand
    }

    // D9: all slots full -> swap with the current slot; the old weapon lies where the new one was, with its
    // magazine and rarity.
    [Fact]
    public void Weapon_AllSlotsFull_SwapsWithTheCurrentSlot_OldWeaponDropsInItsPlace()
    {
        _match = NewMatch(new StartingLoadout
        {
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 4), new LoadoutWeapon(TestWeapons.LightId, 0) },
            HeavyAmmo = 10,
        });
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Slot2);
        a.Inventory.Slots[1].MagAmmo = 1;
        a.Reloading = true;
        a.ReloadEndTick = _match.ServerTick + 50;
        var spot = new Vector3(0f, 0f, 1.5f);
        Put(ItemKind.Weapon, TestWeapons.AutoId, 4, spot, rarity: 2);
        _sent.Clear();

        Press(a, InputButtons.Interact);

        Assert.Equal(TestWeapons.AutoId, a.Inventory.Slots[1].Weapon!.Id);
        Assert.Equal(2, a.Inventory.Slots[1].Rarity);
        Assert.Equal(4, a.Inventory.Slots[1].MagAmmo);
        Assert.False(a.Reloading);   // the reload belonged to the weapon that left the hand
        var dropped = Assert.Single(WorldList());
        Assert.Equal(ItemKind.Weapon, dropped.Kind);
        Assert.Equal(TestWeapons.SemiId, dropped.DefId);
        Assert.Equal(4, dropped.Rarity);
        Assert.Equal(1, dropped.Amount);
        Assert.Equal(spot, dropped.Position);
        Assert.True(_match.WorldItems[0].IsDropped);
    }

    // D9: ammo is taken up to the type's max; the rest stays on the ground with the smaller amount.
    [Fact]
    public void Ammo_PartialPickup_LeavesTheRest()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.SetAmmo(AmmoType.Heavy, TestGameData.HeavyMax - 4);
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Heavy, 10, new Vector3(1f, 0f, 0f));
        _sent.Clear();

        Press(a, InputButtons.Interact);

        Assert.Equal(TestGameData.HeavyMax, a.Inventory.GetAmmo(AmmoType.Heavy));
        Assert.Equal(6, _match.WorldItems[IndexOf(id)].Data.Amount);
        var update = Assert.Single(SpawnedTo(1));   // the amount change is an upsert
        Assert.Equal(id, update.ItemId);
        Assert.Equal(6, update.Amount);
        Assert.Equal(TestGameData.HeavyMax, LastInventoryTo(1).HeavyAmmo);
        Assert.Equal(PickupResultCode.Ok, ResultsTo(1).Single().Result);
    }

    [Fact]
    public void Ammo_AtMax_IsFull_AndTheItemStays()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.SetAmmo(AmmoType.Heavy, TestGameData.HeavyMax);
        ushort id = Put(ItemKind.Ammo, (byte)AmmoType.Heavy, 10, new Vector3(1f, 0f, 0f));
        _sent.Clear();

        Press(a, InputButtons.Interact);

        var result = ResultsTo(1).Single();
        Assert.Equal(PickupResultCode.Full, result.Result);
        Assert.Equal(id, result.ItemId);
        Assert.Equal(10, _match.WorldItems[IndexOf(id)].Data.Amount);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ItemSpawned || s.Id == PacketId.ItemRemoved || s.Id == PacketId.InventoryState);
    }

    // D9: stack limits Medkit 3, Shield Cell 6.
    [Fact]
    public void Consumables_StopAtTheStackLimit()
    {
        var a = Join(1, Vector3.Zero);
        a.Inventory.Medkits = TestGameData.MedkitMaxStack - 1;
        ushort medkits = Put(ItemKind.Consumable, (byte)ConsumableType.Medkit, 2, new Vector3(1f, 0f, 0f));
        Press(a, InputButtons.Interact);
        Assert.Equal(TestGameData.MedkitMaxStack, a.Inventory.Medkits);
        Assert.Equal(1, _match.WorldItems[IndexOf(medkits)].Data.Amount);

        _match.RemoveItemAt(IndexOf(medkits));
        ushort cells = Put(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 9, new Vector3(1f, 0f, 0f));
        Press(a, InputButtons.Interact);
        Assert.Equal(TestGameData.ShieldCellMaxStack, a.Inventory.ShieldCells);
        Assert.Equal(3, _match.WorldItems[IndexOf(cells)].Data.Amount);
        Assert.Equal(TestGameData.ShieldCellMaxStack, LastInventoryTo(1).ShieldCells);
    }

    // D12: G drops the current weapon 1 m in front, magazine included.
    [Fact]
    public void Drop_PutsTheCurrentWeapon1mInFront_WithItsMagazine()
    {
        var a = Join(1, new Vector3(2f, 0f, 3f), yaw: 90f);   // facing +X
        a.Inventory.Slots[0].MagAmmo = 4;
        a.Reloading = true;
        a.ReloadEndTick = _match.ServerTick + 50;
        _sent.Clear();

        Press(a, InputButtons.Drop);

        Assert.True(a.Inventory.Slots[0].IsEmpty);
        Assert.False(a.Reloading);
        var item = Assert.Single(SpawnedTo(1));
        Assert.Equal(ItemKind.Weapon, item.Kind);
        Assert.Equal(TestWeapons.AutoId, item.DefId);
        Assert.Equal(4, item.Amount);
        Assert.Equal(3f, item.Position.X, 3);
        Assert.Equal(0f, item.Position.Y);
        Assert.Equal(3f, item.Position.Z, 3);
        Assert.True(LastInventoryTo(1).Slot0.IsEmpty);
    }

    [Fact]
    public void Drop_EmptyHanded_DoesNothing()
    {
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Slot3);
        _sent.Clear();
        Press(a, InputButtons.Drop);
        Assert.Equal(0, _match.WorldItems.Count);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ItemSpawned || s.Id == PacketId.InventoryState);
    }

    // Placement fallback: 1 m in front is behind (or inside) a wall, so the weapon lands at the feet. The
    // wall piece (12, 1.5, 6) is 0.5 m thick (x 11.75-12.25): 1 m from x 11.3 is already past it.
    [Theory]
    [InlineData(11.3f)]
    [InlineData(11.0f)]
    public void Drop_IntoOrThroughAWall_LandsAtTheFeet(float x)
    {
        var a = Join(1, new Vector3(x, 0f, 6f), yaw: 90f);
        Press(a, InputButtons.Drop);
        var item = Assert.Single(WorldList());
        Assert.Equal(new Vector3(x, 0f, 6f), item.Position);
    }

    // Placement: a weapon dropped from a box edge lands on the floor, where it can be picked up.
    [Fact]
    public void Drop_OverABoxEdge_LandsOnTheFloor()
    {
        // Low box (0, 0.5, 12), 2 x 1 x 2: top at y 1, north edge at z 13.
        var a = Join(1, new Vector3(0f, 1f, 12.8f), yaw: 0f);
        Press(a, InputButtons.Drop);
        var item = Assert.Single(WorldList());
        Assert.Equal(0f, item.Position.Y);
        Assert.Equal(13.8f, item.Position.Z, 3);
    }

    // Keep the per-slot fire interval: fire the semi, drop it, pick it up into another slot, and the next
    // shot still waits for the interval.
    [Fact]
    public void DropAndPickUpAgain_DoesNotSkipTheFireInterval()
    {
        var a = Join(1, Vector3.Zero);
        Press(a, InputButtons.Drop);                        // Auto out of slot 0: slot 0 is now free
        _match.RemoveItemAt(0);                             // (keep only the semi on the ground later)
        Press(a, InputButtons.Slot2);
        Send(a, InputButtons.Fire);
        _match.Tick();
        uint firedAt = _match.ServerTick - 1;               // the tick's "now"
        Assert.Equal(TestWeapons.SemiMagazine - 1, a.Inventory.Slots[1].MagAmmo);
        Press(a, InputButtons.Drop);                        // semi out of slot 1
        Press(a, InputButtons.Interact);                    // back in, into slot 0 (the first empty one)
        Assert.Equal(TestWeapons.SemiId, a.Inventory.Slots[0].Weapon!.Id);
        Assert.Equal(0, a.Inventory.CurrentSlot);           // empty-handed: taken in hand

        int shots = 0;
        while (_match.ServerTick <= firedAt + TestWeapons.SemiInterval + 2)
        {
            uint now = _match.ServerTick;
            Send(a, (now % 2 == 0) ? InputButtons.Fire : InputButtons.None);
            int before = a.Inventory.Slots[0].MagAmmo;
            _match.Tick();
            if (a.Inventory.Slots[0].MagAmmo < before)
            {
                shots++;
                Assert.True(now >= firedAt + TestWeapons.SemiInterval, $"fired at {now}, interval ends at {firedAt + TestWeapons.SemiInterval}");
            }
        }
        Assert.Equal(1, shots);   // and it does fire once the interval is over
    }

    // The missed-input repeat copies the last input's buttons. It must never repeat a drop or a pickup.
    [Fact]
    public void MissedInputTicks_DoNotRepeatDropOrPickup()
    {
        var a = Join(1, Vector3.Zero);
        // Nearer than the weapon G drops 1 m in front, so each pickup takes ammo.
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 10, new Vector3(0f, 0f, 0.5f));
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 10, new Vector3(0f, 0f, -0.6f));
        _sent.Clear();

        Send(a, InputButtons.Drop | InputButtons.Interact);
        for (int i = 0; i < 12; i++) _match.Tick();   // one real input, then 11 repeated ticks

        Assert.Single(SpawnedTo(1));                   // one weapon dropped
        Assert.False(a.Inventory.Slots[1].IsEmpty);    // the semi stayed
        Assert.Single(ResultsTo(1));                   // one pickup
        Assert.Equal(10, a.Inventory.GetAmmo(AmmoType.Light));
    }

    // D12: death drops everything on a circle around the body, after PlayerDied, amounts and magazines kept.
    [Fact]
    public void Death_DropsEverything_OnACircle_AndEmptiesTheInventory()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Inventory.Slots[0].MagAmmo = 5;
        b.Inventory.Medkits = 2;
        b.Health = 1;
        b.Shield = 0;
        _sent.Clear();

        TestAim.YawPitch(a.State.Position, b.State.Position + Chest, out float yaw, out float pitch);
        _seq.TryGetValue(1, out uint seq);
        _seq[1] = ++seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = seq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.EnqueueInput(1, packet);
        _match.Tick();

        Assert.False(b.Alive);
        var drops = SpawnedTo(1);
        Assert.Equal(5, drops.Count);   // Auto, Semi, Medium, Heavy, Medkit
        Assert.Equal(drops, SpawnedTo(2));
        Assert.Contains(drops, d => d.Kind == ItemKind.Weapon && d.DefId == TestWeapons.AutoId && d.Amount == 5);
        Assert.Contains(drops, d => d.Kind == ItemKind.Weapon && d.DefId == TestWeapons.SemiId && d.Amount == TestWeapons.SemiMagazine);
        Assert.Contains(drops, d => d.Kind == ItemKind.Ammo && d.DefId == (byte)AmmoType.Medium && d.Amount == TestGameData.LoadoutMediumAmmo);
        Assert.Contains(drops, d => d.Kind == ItemKind.Ammo && d.DefId == (byte)AmmoType.Heavy && d.Amount == TestGameData.LoadoutHeavyAmmo);
        Assert.Contains(drops, d => d.Kind == ItemKind.Consumable && d.DefId == (byte)ConsumableType.Medkit && d.Amount == 2);
        foreach (var d in drops)
        {
            Vector3 fromBody = d.Position - b.State.Position;
            Assert.Equal(1f, MathF.Sqrt(fromBody.X * fromBody.X + fromBody.Z * fromBody.Z), 3);
            Assert.Equal(0f, d.Position.Y);
        }
        Assert.Equal(5, drops.Select(d => d.Position).Distinct().Count());

        var toB = _sent.Where(s => s.PeerId == 2 && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
        Assert.True(toB.IndexOf(PacketId.PlayerDied) < toB.IndexOf(PacketId.ItemSpawned));
        Assert.All(b.Inventory.Slots, held => Assert.True(held.IsEmpty));
        var empty = LastInventoryTo(2);
        Assert.True(empty.Slot0.IsEmpty && empty.Slot1.IsEmpty);
        Assert.Equal(0, empty.MediumAmmo);
        Assert.Equal(0, empty.Medkits);
    }

    // Review Focus: G, swap-drop, death drop and pickups move items around but never make or lose any.
    [Fact]
    public void Items_AreConserved_ThroughDropSwapDeathAndPickup()
    {
        _match = NewMatch(new StartingLoadout
        {
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 1), new LoadoutWeapon(TestWeapons.LightId, 2) },
            LightAmmo = 50, MediumAmmo = 40, HeavyAmmo = 20, Medkits = 1, ShieldCells = 2,
        });
        var a = Join(1, Vector3.Zero, yaw: 90f);
        var b = Join(2, new Vector3(0f, 0f, 6f));
        Put(ItemKind.Weapon, TestWeapons.AutoId, 3, new Vector3(0f, 0f, 1f), rarity: 4);
        Put(ItemKind.Ammo, (byte)AmmoType.Light, 60, new Vector3(0f, 0f, -1f));

        (int weapons, int light, int medium, int heavy, int medkits, int cells) Count()
        {
            int w = 0, l = 0, m = 0, h = 0, mk = 0, c = 0;
            void Weapon(byte id, int mag)
            {
                w++;
                if (id == TestWeapons.LightId) l += mag; else if (id == TestWeapons.AutoId) m += mag; else h += mag;
            }
            foreach (var p in new[] { a, b })
            {
                foreach (HeldWeapon held in p.Inventory.Slots) if (!held.IsEmpty) Weapon(held.Weapon!.Id, held.MagAmmo);
                l += p.Inventory.GetAmmo(AmmoType.Light);
                m += p.Inventory.GetAmmo(AmmoType.Medium);
                h += p.Inventory.GetAmmo(AmmoType.Heavy);
                mk += p.Inventory.Medkits;
                c += p.Inventory.ShieldCells;
            }
            foreach (var d in WorldList())
            {
                if (d.Kind == ItemKind.Weapon) Weapon(d.DefId, d.Amount);
                else if (d.Kind == ItemKind.Ammo)
                {
                    if (d.DefId == (byte)AmmoType.Light) l += d.Amount; else if (d.DefId == (byte)AmmoType.Medium) m += d.Amount; else h += d.Amount;
                }
                else if (d.DefId == (byte)ConsumableType.Medkit) mk += d.Amount;
                else c += d.Amount;
            }
            return (w, l, m, h, mk, c);
        }

        var start = Count();
        Press(a, InputButtons.Interact);                       // full: swaps the Auto in hand for the ground Auto
        Assert.Equal(start, Count());
        Press(a, InputButtons.Drop);                           // G
        Assert.Equal(start, Count());
        Press(a, InputButtons.Interact);                       // takes the nearest thing back
        Assert.Equal(start, Count());
        Press(a, InputButtons.Interact);
        Assert.Equal(start, Count());

        // Death: everything a carried goes to the ground; b walks over and picks up what it can.
        a.Health = 1;
        a.Shield = 0;
        b.Inventory.Slots[0].MagAmmo = 6;
        TestAim.YawPitch(b.State.Position, a.State.Position + Chest, out float yaw, out float pitch);
        _seq.TryGetValue(2, out uint seq);
        _seq[2] = ++seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = seq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.EnqueueInput(2, packet);
        _match.Tick();
        Assert.False(a.Alive);
        var afterShot = Count();
        Assert.Equal(start.medium - 1, afterShot.medium);      // b's Auto fired one Medium round, nothing else changed
        Assert.Equal((start.weapons, start.light, start.heavy, start.medkits, start.cells),
                     (afterShot.weapons, afterShot.light, afterShot.heavy, afterShot.medkits, afterShot.cells));

        b.State.Position = a.State.Position;
        for (int i = 0; i < 12; i++) Press(b, InputButtons.Interact);
        Assert.Equal(afterShot, Count());
    }

    // Review Focus / D13: many deaths never push the world past 256 items, and the loot at the spawn points
    // is never evicted to make room for drops.
    [Fact]
    public void RepeatedDeaths_KeepTheWorldAtTheCap_AndSpawnPointItemsSurvive()
    {
        _match = NewMatch(new StartingLoadout
        {
            Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0), new LoadoutWeapon(TestWeapons.SemiId, 0), new LoadoutWeapon(TestWeapons.LightId, 0) },
            LightAmmo = TestGameData.LightMax, MediumAmmo = TestGameData.MediumMax, HeavyAmmo = TestGameData.HeavyMax,
            Medkits = TestGameData.MedkitMaxStack, ShieldCells = TestGameData.ShieldCellMaxStack,
        }, LootPoints.All.ToArray());
        var spawnItems = Enumerable.Range(0, _match.WorldItems.Count).Select(i => _match.WorldItems[i].Data.ItemId).ToList();
        Assert.Equal(LootPoints.All.Length, spawnItems.Count);
        var a = Join(1, new Vector3(0f, 0f, -4f));
        var b = Join(2, new Vector3(0f, 0f, 4f));

        for (int death = 0; death < 40; death++)   // 8 items per death: far past the cap
        {
            b.Health = 1;
            b.Shield = 0;
            a.Inventory.Slots[0].MagAmmo = TestWeapons.AutoMagazine;
            a.Inventory.Slots[0].NextFireTick = 0;
            TestAim.YawPitch(a.State.Position, b.State.Position + Chest, out float yaw, out float pitch);
            _seq.TryGetValue(1, out uint seq);
            _seq[1] = ++seq;
            var packet = new PlayerInputPacket { Count = 1 };
            packet.Set(0, new InputCommand { Seq = seq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
            _match.EnqueueInput(1, packet);
            _match.Tick();
            Assert.False(b.Alive, $"death {death}");
            Assert.True(_match.WorldItems.Count <= WorldItems.Capacity);
            while (!b.Alive) _match.Tick();
        }

        Assert.Equal(WorldItems.Capacity, _match.WorldItems.Count);
        foreach (ushort id in spawnItems) Assert.True(_match.WorldItems.IndexOf(id) >= 0, $"spawn item {id} was evicted");
    }

    // Server hot path: a tick with a pickup (and its events) allocates nothing.
    [Fact]
    public void PickupTick_AllocatesNothing()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { },
            TestGameData.CombatLoadout, Array.Empty<LootPoint>());
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out var a);
        var packet = new PlayerInputPacket { Count = 1 };
        long allocated = 0;
        for (uint i = 1; i <= 4; i++)   // 1-3 warm up; 4 is measured
        {
            _match.SpawnItem(new LootRoll(ItemKind.Ammo, (byte)AmmoType.Light, 0, 1), a.State.Position, -1);
            packet.Set(0, new InputCommand { Seq = i, Buttons = InputButtons.Interact });
            _match.EnqueueInput(1, packet);
            long start = GC.GetAllocatedBytesForCurrentThread();
            _match.Tick();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal((int)i, a.Inventory.GetAmmo(AmmoType.Light));
        }
        Assert.Equal(0, allocated);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공, 19개 FAIL·395개 PASS. 실패는 모두 `PickupDropTests`다(줍기·버리기·사망 Drop이 아직 없다. 예: `Pickup_WithNothingInRange_ReportsIt_AndChangesNothing`에서 `PickupResult`가 오지 않는다).

- [ ] **Step 3: `ItemRules`**

`Server/src/ProjectH.Server/Game/Items/ItemRules.cs`를 만든다. 버리기 위치는 "Spec 해석" 3을 따른다.

```csharp
using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// Pickup and drop rules (D8, D9, D12) that do not need the match: ranges, stack room, drop placement.
// Pure functions, no allocation. The client's pickup prompt copies the range rule (PickupRule); keep the
// two in step.
public static class ItemRules
{
    public const float PickupRange = 2f;       // D8: horizontal, from the feet
    public const float PickupHeight = 2f;      // D8: up or down, from the feet
    public const float DropDistance = 1f;      // D12: G drops the weapon 1 m in front
    public const float DeathDropRadius = 1f;   // D12: a dead player's items lie on a 1 m circle
    // Knee height of the "is a box in the way" ray: sees 1 m boxes, passes over the box being stood on.
    private const float BlockCheckHeight = 0.5f;

    // How many more of this ammo type or consumable the inventory can hold (D3, D9). Weapons have no stack.
    public static int Room(Inventory inventory, ItemCatalog items, ItemKind kind, byte defId)
    {
        switch (kind)
        {
            case ItemKind.Ammo:
                var type = (AmmoType)defId;
                return Math.Max(0, items.Ammo(type).Max - inventory.GetAmmo(type));
            case ItemKind.Consumable:
                var consumable = (ConsumableType)defId;
                int have = consumable == ConsumableType.Medkit ? inventory.Medkits : inventory.ShieldCells;
                return Math.Max(0, items.Consumable(consumable).MaxStack - have);
            default:
                return 0;
        }
    }

    public static void AddStack(Inventory inventory, ItemKind kind, byte defId, int amount)
    {
        if (kind == ItemKind.Ammo)
        {
            var type = (AmmoType)defId;
            inventory.SetAmmo(type, inventory.GetAmmo(type) + amount);
        }
        else if ((ConsumableType)defId == ConsumableType.Medkit)
        {
            inventory.Medkits += amount;
        }
        else
        {
            inventory.ShieldCells += amount;
        }
    }

    // Offset on the ground plane for a yaw in degrees (yaw 0 = +Z, 90 = +X, the camera convention).
    public static Vector3 Offset(float yawDegrees, float distance)
    {
        float radians = yawDegrees * (MathF.PI / 180f);
        return new Vector3(MathF.Sin(radians) * distance, 0f, MathF.Cos(radians) * distance);
    }

    // Where a dropped item lies: feet + offset, unless a box is in the way (a wall, even a thin one, or a
    // box the offset would end inside), then under the feet. Always on the highest surface at or below the
    // feet (floor or a box top): items do not fall later, so a drop in mid-air or over a box edge must land
    // now, where a player can reach it.
    public static Vector3 DropPosition(Vector3 feet, Vector3 offset, ReadOnlySpan<Box> world)
    {
        Vector3 p = feet + offset;
        float distance = offset.Length();
        if (distance > 0f)
        {
            Vector3 origin = feet + new Vector3(0f, BlockCheckHeight, 0f);
            if (HitScan.TraceWorld(origin, offset / distance, distance, world) < distance) p = feet;
        }
        p.Y = GroundHeight(p.X, p.Z, feet.Y, world);
        return p;
    }

    private static float GroundHeight(float x, float z, float feetY, ReadOnlySpan<Box> world)
    {
        float ground = 0f;
        for (int i = 0; i < world.Length; i++)
        {
            ref readonly Box b = ref world[i];
            if (x >= b.Min.X && x <= b.Max.X && z >= b.Min.Z && z <= b.Max.Z &&
                b.Max.Y <= feetY + MoveSettings.GroundProbe && b.Max.Y > ground)
                ground = b.Max.Y;
        }
        return ground;
    }
}
```

- [ ] **Step 4: `Inventory`에 버린 무기의 발사 잠금**

`Server/src/ProjectH.Server/Game/Items/Inventory.cs`:

변경 1/2 — 찾을 코드:

```csharp
    public int CurrentSlot;
    public int Medkits;
    public int ShieldCells;
    // Set by every change the owner must hear about (not by shots: the snapshot carries the magazine).
    // Match sends one InventoryState at the end of a tick in which it was set, then clears it (D14).
    public bool Changed;
```

바꿀 코드:

```csharp
    public int CurrentSlot;
    public int Medkits;
    public int ShieldCells;
    // The latest NextFireTick of any weapon this player dropped. A weapon picked up cannot fire before it,
    // so dropping a weapon and picking it up again (maybe into another slot) never skips its fire interval.
    public uint DroppedFireLockTick;
    // Set by every change the owner must hear about (not by shots: the snapshot carries the magazine).
    // Match sends one InventoryState at the end of a tick in which it was set, then clears it (D14).
    public bool Changed;
```

변경 2/2 — 찾을 코드:

```csharp
        CurrentSlot = 0;
        Medkits = 0;
        ShieldCells = 0;
        Changed = true;
    }
```

바꿀 코드:

```csharp
        CurrentSlot = 0;
        Medkits = 0;
        ShieldCells = 0;
        DroppedFireLockTick = 0;
        Changed = true;
    }
```

- [ ] **Step 5: `Match`의 줍기·버리기·사망 Drop**

`Server/src/ProjectH.Server/Game/Match.cs`:

변경 1/3 — 찾을 코드:

```csharp
        return false;
    }

    // One real input of a living player, in the spec §2 order: slot -> reload -> fire.
    private void ProcessActions(PlayerEntity player, in InputCommand input, uint now)
    {
        WeaponRules.SelectSlot(player, input.Buttons);

        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(player, input.Buttons, aimValid, now))
```

바꿀 코드:

```csharp
        return false;
    }

    // One real input of a living player, in the spec §2 order: slot -> drop -> pickup -> reload -> fire.
    private void ProcessActions(PlayerEntity player, in InputCommand input, uint now)
    {
        WeaponRules.SelectSlot(player, input.Buttons);
        if ((input.Buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        if ((input.Buttons & InputButtons.Interact) != 0) Pickup(player);

        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(player, input.Buttons, aimValid, now))
```

변경 2/3 — 찾을 코드:

```csharp
        if (killed) Kill(target, shooter);
    }

    // D9: death is decided here. The same ReliableOrdered channel carries DamageTaken, PlayerDied and
    // PlayerRespawned, so every client sees them in that order.
    private void Kill(PlayerEntity victim, PlayerEntity killer)
```

바꿀 코드:

```csharp
        if (killed) Kill(target, shooter);
    }

    // D8, D9: the server picks the nearest item in range itself; the client never names one, so it cannot
    // reach for a far item. Items are processed in player order within a tick, so when two players reach
    // for the same item the first one takes it and the second finds it gone.
    private void Pickup(PlayerEntity player)
    {
        int index = _worldItems.FindNearest(player.State.Position, ItemRules.PickupRange, ItemRules.PickupHeight);
        if (index < 0)
        {
            SendPickupResult(player, PickupResultCode.NothingInRange, 0);
            return;
        }

        WorldItemData item = _worldItems[index].Data;
        if (item.Kind == ItemKind.Weapon)
        {
            PickupWeapon(player, index, item);
            SendPickupResult(player, PickupResultCode.Ok, item.ItemId);
            return;
        }

        int take = Math.Min(item.Amount, ItemRules.Room(player.Inventory, _items, item.Kind, item.DefId));
        if (take == 0)
        {
            SendPickupResult(player, PickupResultCode.Full, item.ItemId);
            return;
        }
        ItemRules.AddStack(player.Inventory, item.Kind, item.DefId, take);
        player.Inventory.Changed = true;
        // D9: what does not fit stays on the ground, with the smaller amount.
        if (take == item.Amount) RemoveItemAt(index);
        else SetItemAmount(index, (ushort)(item.Amount - take));
        SendPickupResult(player, PickupResultCode.Ok, item.ItemId);
    }

    // D9: the first empty slot, or, with all three full, the current slot; the weapon it held goes on the
    // ground where the new one lay. Empty-handed (current slot empty), the player also takes it in hand.
    private void PickupWeapon(PlayerEntity player, int index, in WorldItemData item)
    {
        Inventory inventory = player.Inventory;
        _weapons.TryGetById(item.DefId, out WeaponDefinition weapon);   // items are made from this catalog
        var picked = new HeldWeapon
        {
            Weapon = weapon,
            Rarity = item.Rarity,
            MagAmmo = Math.Min(item.Amount, (int)weapon.MagazineSize),
            NextFireTick = inventory.DroppedFireLockTick,
        };
        RemoveItemAt(index);

        int slot = -1;
        for (int i = 0; i < Inventory.SlotCount && slot < 0; i++)
        {
            if (inventory.Slots[i].IsEmpty) slot = i;
        }

        if (slot >= 0)
        {
            inventory.Slots[slot] = picked;
            if (inventory.Current.IsEmpty)
            {
                inventory.CurrentSlot = slot;
                player.Reloading = false;
            }
        }
        else
        {
            HeldWeapon old = inventory.Current;
            inventory.DroppedFireLockTick = Math.Max(inventory.DroppedFireLockTick, old.NextFireTick);
            picked.NextFireTick = inventory.DroppedFireLockTick;
            inventory.Current = picked;
            player.Reloading = false;   // the reload belonged to the weapon that left the hand
            SpawnItem(new LootRoll(ItemKind.Weapon, old.Weapon!.Id, old.Rarity, (ushort)old.MagAmmo), item.Position, -1);
        }
        inventory.Changed = true;
    }

    // D12: G drops the current weapon 1 m in front of the feet, magazine included.
    private void DropCurrentWeapon(PlayerEntity player)
    {
        Inventory inventory = player.Inventory;
        ref HeldWeapon held = ref inventory.Current;
        if (held.IsEmpty) return;

        var roll = new LootRoll(ItemKind.Weapon, held.Weapon!.Id, held.Rarity, (ushort)held.MagAmmo);
        inventory.DroppedFireLockTick = Math.Max(inventory.DroppedFireLockTick, held.NextFireTick);
        held = default;
        player.Reloading = false;
        inventory.Changed = true;

        Vector3 offset = ItemRules.Offset(player.State.Yaw, ItemRules.DropDistance);
        SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, TestArena.Boxes), -1);
    }

    // D12 (request §21): everything the player carried goes on a circle around the body, one item per
    // weapon, per ammo type and per consumable type, so they do not lie on one point. Then the inventory
    // is empty. Dropped items are not respawn points and can be evicted when the world is full (D13).
    private void DropEverything(PlayerEntity player)
    {
        Inventory inventory = player.Inventory;
        int count = 0;
        for (int i = 0; i < Inventory.SlotCount; i++) if (!inventory.Slots[i].IsEmpty) count++;
        for (int t = 1; t <= ItemConstants.AmmoTypeCount; t++) if (inventory.GetAmmo((AmmoType)t) > 0) count++;
        if (inventory.Medkits > 0) count++;
        if (inventory.ShieldCells > 0) count++;

        int n = 0;
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            ref HeldWeapon held = ref inventory.Slots[i];
            if (held.IsEmpty) continue;
            DropAround(player, n++, count, new LootRoll(ItemKind.Weapon, held.Weapon!.Id, held.Rarity, (ushort)held.MagAmmo));
        }
        for (int t = 1; t <= ItemConstants.AmmoTypeCount; t++)
        {
            int rounds = inventory.GetAmmo((AmmoType)t);
            if (rounds > 0) DropAround(player, n++, count, new LootRoll(ItemKind.Ammo, (byte)t, 0, (ushort)rounds));
        }
        if (inventory.Medkits > 0)
            DropAround(player, n++, count, new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, (ushort)inventory.Medkits));
        if (inventory.ShieldCells > 0)
            DropAround(player, n++, count, new LootRoll(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 0, (ushort)inventory.ShieldCells));

        inventory.Clear();
    }

    private void DropAround(PlayerEntity player, int n, int count, in LootRoll roll)
    {
        Vector3 offset = ItemRules.Offset(player.State.Yaw + 360f * n / count, ItemRules.DeathDropRadius);
        SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, TestArena.Boxes), -1);
    }

    private void SendPickupResult(PlayerEntity player, PickupResultCode result, ushort itemId)
    {
        var writer = new PacketWriter(_sendBuffer);
        PickupResult.Write(ref writer, new PickupResult { Result = result, ItemId = itemId });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D9: death is decided here. The same ReliableOrdered channel carries DamageTaken, PlayerDied and
    // PlayerRespawned, so every client sees them in that order.
    private void Kill(PlayerEntity victim, PlayerEntity killer)
```

변경 3/3 — 찾을 코드:

```csharp
        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = victim.EntityId, KillerId = killer.EntityId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void Respawn(PlayerEntity player)
```

바꿀 코드:

```csharp
        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = victim.EntityId, KillerId = killer.EntityId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        // After PlayerDied, so every client hears of the death before the items appear.
        DropEverything(victim);
    }

    private void Respawn(PlayerEntity player)
```

- [ ] **Step 6: 서버 테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 414개 PASS

- [ ] **Step 7: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 7: 회복 Channel (Medkit·Shield Cell 사용, 취소, 완료)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Items/ConsumableRules.cs`
- Modify: `Server/src/ProjectH.Server/Game/Items/Inventory.cs`, `Game/Match.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Game/ConsumableTests.cs` (신규)

**Interfaces:**
- Consumes: Task 5의 `Inventory`, `CombatRules.MaxHealth/MaxShield`; Task 2의 `ItemCatalog.Consumable`
- Produces:
  - `Inventory.Using` (`ConsumableType`), `Inventory.UseEndTick`; `Clear`가 둘 다 지운다. `Inventory.ToWire(uint now)` — 사용 중이면 `UseRemainingTicks = max(1, UseEndTick − now)`.
  - `ProjectH.Server.Game.Items.ConsumableRules { static bool CancelIfInterrupted(PlayerEntity, InputButtons); static bool TryStart(PlayerEntity, ItemCatalog, InputButtons, uint now); static bool Complete(PlayerEntity, ItemCatalog, uint now); static void Cancel(Inventory) }`
  - `Match` Tick: 실제 입력의 처음에 사용 취소, 발사 다음에 사용 시작, 그리고 입력이 없어도 매 Tick 사용 완료. 사망은 `Inventory.Clear()`로 사용을 취소한다.

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Game/ConsumableTests.cs`를 만든다. 90 Tick 뒤 +50(최대 100), 60 Tick 뒤 +25(최대 100), 시작·끝의 `InventoryState`, 최대치·없음·두 버튼 동시면 시작하지 않음, 발사·교체(현재 칸 포함)·버리기로 취소, 다른 회복은 취소하고 시작, 이동과 같은 버튼 다시 누름은 취소하지 않음, 누락 입력 반복은 시작·취소하지 않지만 완료는 제때, 사망하면 취소, 할당 없음.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// D11 through Match.Tick: Medkit 90 ticks +50 health, Shield Cell 60 ticks +25 shield, both capped at 100.
public class ConsumableTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private readonly List<Sent> _sent = new();
    private readonly Match _match;
    private readonly PlayerEntity _a;
    private uint _seq;

    public ConsumableTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)),
            new StartingLoadout
            {
                Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) },
                MediumAmmo = 30,
                Medkits = 2,
                ShieldCells = 3,
            },
            Array.Empty<LootPoint>());
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out _a);
        _sent.Clear();
    }

    private void Press(InputButtons buttons, float moveY = 0f)
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_seq, Buttons = buttons, MoveY = moveY, AimPitch = 10f });
        _match.EnqueueInput(1, packet);
        _match.Tick();
    }

    private void Idle(int ticks)
    {
        for (int i = 0; i < ticks; i++) Press(InputButtons.None);
    }

    private List<InventoryState> Inventories() => _sent.Where(s => s.PeerId == 1 && s.Id == PacketId.InventoryState)
        .Select(s =>
        {
            var r = new PacketReader(s.Data);
            r.TryReadPacketId(out _);
            Assert.True(InventoryState.TryRead(ref r, out var state));
            return state;
        }).ToList();

    // Spec §6: 3 s later +50, never above 100.
    [Theory]
    [InlineData(30, 80)]
    [InlineData(70, 100)]
    public void Medkit_After90Ticks_Heals50_CappedAt100(int health, int expected)
    {
        _a.Health = health;
        Press(InputButtons.UseMedkit);
        Assert.Equal(ConsumableType.Medkit, _a.Inventory.Using);

        Idle(TestGameData.MedkitUseTicks - 1);
        Assert.Equal(health, _a.Health);            // nothing until the channel ends
        Assert.Equal(2, _a.Inventory.Medkits);

        Idle(1);
        Assert.Equal(expected, _a.Health);
        Assert.Equal(1, _a.Inventory.Medkits);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);

        var states = Inventories();
        Assert.Equal(2, states.Count);              // one at the start, one at the end
        Assert.Equal(ConsumableType.Medkit, states[0].Using);
        Assert.Equal(TestGameData.MedkitUseTicks - 1, states[0].UseRemainingTicks);
        Assert.Equal(ConsumableType.None, states[1].Using);
        Assert.Equal(0, states[1].UseRemainingTicks);
        Assert.Equal(1, states[1].Medkits);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(90, 100)]
    public void ShieldCell_After60Ticks_Adds25_CappedAt100(int shield, int expected)
    {
        _a.Shield = shield;
        Press(InputButtons.UseShieldCell);
        Idle(TestGameData.ShieldCellUseTicks - 1);
        Assert.Equal(shield, _a.Shield);
        Idle(1);
        Assert.Equal(expected, _a.Shield);
        Assert.Equal(2, _a.Inventory.ShieldCells);
    }

    // Review Focus / spec §6: at the maximum a heal does not start and nothing is used up.
    [Fact]
    public void AtMax_NothingStarts()
    {
        _a.Health = CombatRules.MaxHealth;
        _a.Shield = CombatRules.MaxShield;
        Press(InputButtons.UseMedkit);
        Press(InputButtons.UseShieldCell);
        Idle(TestGameData.MedkitUseTicks + 5);

        Assert.Equal(ConsumableType.None, _a.Inventory.Using);
        Assert.Equal(2, _a.Inventory.Medkits);
        Assert.Equal(3, _a.Inventory.ShieldCells);
        Assert.Empty(Inventories());
    }

    [Fact]
    public void WithoutOne_NothingStarts()
    {
        _a.Health = 10;
        _a.Inventory.Medkits = 0;
        Press(InputButtons.UseMedkit);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);
    }

    [Fact]
    public void BothHealButtons_AtOnce_AreIgnored()
    {
        _a.Health = 10;
        _a.Shield = 0;
        Press(InputButtons.UseMedkit | InputButtons.UseShieldCell);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);
    }

    // Spec §6: fire, switch and drop cancel the use; nothing is used up and nothing is healed.
    [Theory]
    [InlineData(InputButtons.Fire)]
    [InlineData(InputButtons.Slot2)]
    [InlineData(InputButtons.Slot1)]   // even the slot already in hand
    [InlineData(InputButtons.Drop)]
    public void Interrupt_CancelsTheUse(InputButtons interrupt)
    {
        _a.Health = 10;
        Press(InputButtons.UseMedkit);
        Idle(30);
        Press(interrupt);
        Assert.Equal(ConsumableType.None, _a.Inventory.Using);

        Idle(TestGameData.MedkitUseTicks);
        Assert.Equal(10, _a.Health);
        Assert.Equal(2, _a.Inventory.Medkits);
        Assert.Equal(ConsumableType.None, Inventories().Last().Using);
    }

    // D11: pressing the other heal cancels the running one and starts the other in the same tick.
    [Fact]
    public void OtherHeal_CancelsAndStartsItself()
    {
        _a.Health = 10;
        _a.Shield = 0;
        Press(InputButtons.UseMedkit);
        Idle(30);
        Press(InputButtons.UseShieldCell);
        Assert.Equal(ConsumableType.ShieldCell, _a.Inventory.Using);

        Idle(TestGameData.ShieldCellUseTicks);
        Assert.Equal(10, _a.Health);
        Assert.Equal(TestGameData.ShieldCellShield, _a.Shield);
        Assert.Equal(2, _a.Inventory.Medkits);
        Assert.Equal(2, _a.Inventory.ShieldCells);
    }

    // D11: moving does not interrupt (so movement prediction is untouched), and pressing the same heal
    // again does not restart the channel.
    [Fact]
    public void Moving_AndPressingTheSameHealAgain_DoNotInterrupt()
    {
        _a.Health = 10;
        Press(InputButtons.UseMedkit);
        for (int i = 1; i < TestGameData.MedkitUseTicks; i++)
            Press(i % 10 == 0 ? InputButtons.UseMedkit | InputButtons.Sprint : InputButtons.Sprint, moveY: 1f);
        Assert.Equal(10, _a.Health);
        Press(InputButtons.Sprint, moveY: 1f);
        Assert.Equal(10 + TestGameData.MedkitHeal, _a.Health);
    }

    // The missed-input repeat carries the last input's buttons; it must neither start nor cancel a use, but
    // the channel still ends on time.
    [Fact]
    public void MissedInputTicks_NeitherStartNorCancel_ButTheChannelEnds()
    {
        _a.Health = 10;
        Press(InputButtons.UseMedkit | InputButtons.Fire);   // cancel (nothing running) -> fire -> start
        Assert.Equal(ConsumableType.Medkit, _a.Inventory.Using);
        for (int i = 1; i < TestGameData.MedkitUseTicks; i++) _match.Tick();   // no inputs arrive: the repeat holds Fire
        Assert.Equal(ConsumableType.Medkit, _a.Inventory.Using);
        _match.Tick();
        Assert.Equal(10 + TestGameData.MedkitHeal, _a.Health);
        Assert.Equal(1, _a.Inventory.Medkits);
    }

    // Review Focus / spec §6: death cancels the use; the respawned player is not healed by it later.
    [Fact]
    public void Death_CancelsTheUse()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestGameData.Create(), static (_, _, _) => { },
            new StartingLoadout { Weapons = new[] { new LoadoutWeapon(TestWeapons.AutoId, 0) }, MediumAmmo = 30, Medkits = 2 },
            Array.Empty<LootPoint>());
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "healer");
        match.TryGetPlayer(1, out var shooter);
        match.TryGetPlayer(2, out var healer);
        shooter.State.Position = new Vector3(0f, 0f, -3f);
        healer.State.Position = new Vector3(0f, 0f, 3f);
        healer.History.Reset(match.ServerTick, healer.State.Position);
        healer.Health = 20;

        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.UseMedkit });
        match.EnqueueInput(2, packet);
        match.Tick();
        Assert.Equal(ConsumableType.Medkit, healer.Inventory.Using);

        TestAim.YawPitch(shooter.State.Position, healer.State.Position + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = match.ServerTick });
        match.EnqueueInput(1, packet);
        match.Tick();
        Assert.False(healer.Alive);
        Assert.Equal(ConsumableType.None, healer.Inventory.Using);

        while (!healer.Alive) match.Tick();
        for (int i = 0; i < TestGameData.MedkitUseTicks; i++) match.Tick();
        Assert.Equal(CombatRules.MaxHealth, healer.Health);   // the respawn's health, not a late medkit
        Assert.Equal(ConsumableType.None, healer.Inventory.Using);
        Assert.Equal(2, healer.Inventory.Medkits);            // the loadout again
    }

    // Server hot path: starting, running and finishing a use allocates nothing.
    [Fact]
    public void UseTicks_AllocateNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 1 }, TestGameData.Create(), static (_, _, _) => { },
            new StartingLoadout { Medkits = 3 }, Array.Empty<LootPoint>());
        match.TryJoin(1, "a");
        match.TryGetPlayer(1, out var a);
        var packet = new PlayerInputPacket { Count = 1 };
        long allocated = 0;
        for (uint use = 1; use <= 2; use++)   // the first use warms up the path; the second is measured
        {
            a.Health = 1;
            long start = GC.GetAllocatedBytesForCurrentThread();
            packet.Set(0, new InputCommand { Seq = use, Buttons = InputButtons.UseMedkit });
            match.EnqueueInput(1, packet);
            for (int i = 0; i <= TestGameData.MedkitUseTicks; i++) match.Tick();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(1 + TestGameData.MedkitHeal, a.Health);
        }
        Assert.Equal(0, allocated);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 실패 — `Inventory.Using`이 없다는 오류

- [ ] **Step 3: `ConsumableRules`**

`Server/src/ProjectH.Server/Game/Items/ConsumableRules.cs`를 만든다. 취소 조건은 "Spec 해석" 11을 따른다.

```csharp
using System;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// D11: Medkit (4) and Shield Cell (5) are used over a channel time, decided in server ticks. Moving does
// not interrupt; firing, switching slots, dropping or pressing the other heal does. Pure state changes on
// PlayerEntity; Match calls them in the spec §2 order (cancel = step 2, start = step 8, finish = step 9).
public static class ConsumableRules
{
    private const InputButtons Interrupts = InputButtons.Fire | InputButtons.Slot1 | InputButtons.Slot2 |
                                            InputButtons.Slot3 | InputButtons.Drop;

    // Step 2, real inputs only. Returns true when a running use was cancelled.
    public static bool CancelIfInterrupted(PlayerEntity player, InputButtons buttons)
    {
        Inventory inventory = player.Inventory;
        if (inventory.Using == ConsumableType.None) return false;
        InputButtons other = inventory.Using == ConsumableType.Medkit ? InputButtons.UseShieldCell : InputButtons.UseMedkit;
        if ((buttons & (Interrupts | other)) == 0) return false;
        Cancel(inventory);
        return true;
    }

    // Step 8, real inputs only: exactly one heal button, none running, one in the inventory, and not
    // already at the maximum it restores. Pressing the running heal again changes nothing.
    public static bool TryStart(PlayerEntity player, ItemCatalog items, InputButtons buttons, uint now)
    {
        ConsumableType type;
        switch (buttons & (InputButtons.UseMedkit | InputButtons.UseShieldCell))
        {
            case InputButtons.UseMedkit: type = ConsumableType.Medkit; break;
            case InputButtons.UseShieldCell: type = ConsumableType.ShieldCell; break;
            default: return false;
        }

        Inventory inventory = player.Inventory;
        if (inventory.Using != ConsumableType.None) return false;
        if (Count(inventory, type) == 0) return false;
        ConsumableDefinition definition = items.Consumable(type);
        bool healsSomething = (definition.Heal > 0 && player.Health < CombatRules.MaxHealth) ||
                              (definition.Shield > 0 && player.Shield < CombatRules.MaxShield);
        if (!healsSomething) return false;

        inventory.Using = type;
        inventory.UseEndTick = now + definition.UseTicks;
        inventory.Changed = true;
        return true;
    }

    // Step 9, every tick of a living player: the channel ends, one item is used up, the values rise to
    // at most the maximum.
    public static bool Complete(PlayerEntity player, ItemCatalog items, uint now)
    {
        Inventory inventory = player.Inventory;
        if (inventory.Using == ConsumableType.None || now < inventory.UseEndTick) return false;

        ConsumableType type = inventory.Using;
        Cancel(inventory);
        if (Count(inventory, type) == 0) return false;   // not reached: nothing removes a stack mid-use but death

        if (type == ConsumableType.Medkit) inventory.Medkits--;
        else inventory.ShieldCells--;
        ConsumableDefinition definition = items.Consumable(type);
        player.Health = Math.Min(CombatRules.MaxHealth, player.Health + definition.Heal);
        player.Shield = Math.Min(CombatRules.MaxShield, player.Shield + definition.Shield);
        return true;
    }

    public static void Cancel(Inventory inventory)
    {
        if (inventory.Using == ConsumableType.None) return;
        inventory.Using = ConsumableType.None;
        inventory.UseEndTick = 0;
        inventory.Changed = true;
    }

    private static int Count(Inventory inventory, ConsumableType type) =>
        type == ConsumableType.Medkit ? inventory.Medkits : inventory.ShieldCells;
}
```

- [ ] **Step 4: `Inventory`의 사용 상태**

`Server/src/ProjectH.Server/Game/Items/Inventory.cs`:

변경 1/2 — 찾을 코드:

```csharp
    // The latest NextFireTick of any weapon this player dropped. A weapon picked up cannot fire before it,
    // so dropping a weapon and picking it up again (maybe into another slot) never skips its fire interval.
    public uint DroppedFireLockTick;
    // Set by every change the owner must hear about (not by shots: the snapshot carries the magazine).
    // Match sends one InventoryState at the end of a tick in which it was set, then clears it (D14).
    public bool Changed;
```

바꿀 코드:

```csharp
    // The latest NextFireTick of any weapon this player dropped. A weapon picked up cannot fire before it,
    // so dropping a weapon and picking it up again (maybe into another slot) never skips its fire interval.
    public uint DroppedFireLockTick;
    // The heal being used (D11) and the tick its channel ends; None when nothing is being used.
    public ConsumableType Using;
    public uint UseEndTick;
    // Set by every change the owner must hear about (not by shots: the snapshot carries the magazine).
    // Match sends one InventoryState at the end of a tick in which it was set, then clears it (D14).
    public bool Changed;
```

변경 2/2 — 찾을 코드:

```csharp
        Medkits = 0;
        ShieldCells = 0;
        DroppedFireLockTick = 0;
        Changed = true;
    }

    public InventoryState ToWire()
    {
        var state = new InventoryState
        {
            CurrentSlot = (byte)CurrentSlot,
            LightAmmo = (ushort)GetAmmo(AmmoType.Light),
            MediumAmmo = (ushort)GetAmmo(AmmoType.Medium),
```

바꿀 코드:

```csharp
        Medkits = 0;
        ShieldCells = 0;
        DroppedFireLockTick = 0;
        Using = ConsumableType.None;
        UseEndTick = 0;
        Changed = true;
    }

    // now = the current server tick; the client counts the rest of a running use down from it.
    public InventoryState ToWire(uint now)
    {
        var state = new InventoryState
        {
            Using = Using,
            UseRemainingTicks = Using == ConsumableType.None ? (ushort)0
                : (ushort)Math.Clamp(UseEndTick > now ? UseEndTick - now : 1u, 1u, ushort.MaxValue),
            CurrentSlot = (byte)CurrentSlot,
            LightAmmo = (ushort)GetAmmo(AmmoType.Light),
            MediumAmmo = (ushort)GetAmmo(AmmoType.Medium),
```

- [ ] **Step 5: `Match` Tick 순서**

`Server/src/ProjectH.Server/Game/Match.cs`:

변경 1/4 — 찾을 코드:

```csharp
            // Only an input the client really sent can act: the missed-input repeat copies the last input's
            // buttons and must never invent a switch, reload or shot.
            if (sent) ProcessActions(player, input, now);
        }

        ServerTick++;
```

바꿀 코드:

```csharp
            // Only an input the client really sent can act: the missed-input repeat copies the last input's
            // buttons and must never invent a switch, reload or shot.
            if (sent) ProcessActions(player, input, now);
            ConsumableRules.Complete(player, _items, now);   // step 9: every tick, input or not
        }

        ServerTick++;
```

변경 2/4 — 찾을 코드:

```csharp
        return false;
    }

    // One real input of a living player, in the spec §2 order: slot -> drop -> pickup -> reload -> fire.
    private void ProcessActions(PlayerEntity player, in InputCommand input, uint now)
    {
        WeaponRules.SelectSlot(player, input.Buttons);
        if ((input.Buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        if ((input.Buttons & InputButtons.Interact) != 0) Pickup(player);
```

바꿀 코드:

```csharp
        return false;
    }

    // One real input of a living player, in the spec §2 order: cancel use -> slot -> drop -> pickup ->
    // reload -> fire -> start use. (Movement came first; finishing a use comes after, every tick.)
    private void ProcessActions(PlayerEntity player, in InputCommand input, uint now)
    {
        ConsumableRules.CancelIfInterrupted(player, input.Buttons);
        WeaponRules.SelectSlot(player, input.Buttons);
        if ((input.Buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        if ((input.Buttons & InputButtons.Interact) != 0) Pickup(player);
```

변경 3/4 — 찾을 코드:

```csharp
        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(player, input.Buttons, aimValid, now))
            FireShot(player, direction, input.ViewTick);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
```

바꿀 코드:

```csharp
        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(player, input.Buttons, aimValid, now))
            FireShot(player, direction, input.ViewTick);

        ConsumableRules.TryStart(player, _items, input.Buttons, now);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
```

변경 4/4 — 찾을 코드:

```csharp
    {
        player.Inventory.Changed = false;
        var writer = new PacketWriter(_sendBuffer);
        InventoryState.Write(ref writer, player.Inventory.ToWire());
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }
```

바꿀 코드:

```csharp
    {
        player.Inventory.Changed = false;
        var writer = new PacketWriter(_sendBuffer);
        InventoryState.Write(ref writer, player.Inventory.ToWire(ServerTick));
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }
```

- [ ] **Step 6: 서버 테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 430개 PASS

- [ ] **Step 7: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 8: Loot 재생성·HeadlessClient 아이템 이벤트·두 Client 통합 테스트

**Files:**
- Modify: `Server/src/ProjectH.Server/Game/Items/LootSpawner.cs`, `Game/Match.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Game/LootRespawnTests.cs` (신규), `Integration/ItemIntegrationTests.cs` (신규), `Integration/HeadlessClient.cs`

**Interfaces:**
- Consumes: Task 3의 `ServerOptions.LootRespawnSeconds`, `TestGameData.WeaponsOnlyLootJson`; Task 4의 `LootSpawner`, `Match.RemoveItemAt`; Task 5의 `GameLoop(..., StartingLoadout? loadout)`; Task 1의 아이템 패킷
- Produces:
  - `LootSpawner(ReadOnlySpan<LootPoint> points, GameData data, int seed, uint respawnTicks)` — `void OnTaken(int point, uint now)`, `bool IsDue(int point, uint now)`, `void OnRefilled(int point)`. `respawnTicks = LootRespawnSeconds × SimHz`, 0이면 다시 채우지 않는다.
  - `Match.Tick`은 부활 다음에 다 가져간 Point를 다시 채운다(`ItemSpawned`). `RemoveItemAt`은 Spawn Point 아이템이면 타이머를 건다. 부분 줍기와 Drop 아이템은 타이머를 걸지 않는다.
  - `HeadlessClient.WorldItems` (`Dictionary<ushort, WorldItemData>`, Join 목록 + Upsert + 제거), `ItemsSpawned`, `ItemsRemoved`, `Inventories`, `PickupResults`

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Game/LootRespawnTests.cs`를 만든다. 가져간 Point는 900 Tick(30 s) 뒤에 다시 채워지고, 0이면 끄고, 부분 줍기는 Point를 비우지 않으며, Drop 아이템을 주워도 아무것도 다시 생기지 않는다.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// D7: a looted spawn point is refilled LootRespawnSeconds later (30 s = 900 ticks at 30 Hz); 0 turns it off.
public class LootRespawnTests
{
    private static readonly Vector3 PointPosition = new(0f, 0f, 8f);

    private readonly List<(int Peer, PacketId Id)> _sent = new();
    private Match _match = null!;
    private PlayerEntity _a = null!;
    private uint _seq;

    private void Start(int respawnSeconds, string lootJson = TestGameData.LootJson)
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2, LootRespawnSeconds = respawnSeconds }, TestGameData.Create(lootJson: lootJson),
            (peer, data, _) => _sent.Add((peer, (PacketId)data[0])), TestGameData.CombatLoadout,
            new[] { new LootPoint(PointPosition, LootPoints.FloorTable) });
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out _a);
        _a.State.Position = PointPosition + new Vector3(0f, 0f, -1f);
        _sent.Clear();
    }

    private void Press(InputButtons buttons)
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_seq, Buttons = buttons, Yaw = 180f });
        _match.EnqueueInput(1, packet);
        _match.Tick();
    }

    private bool PointFilled() => Enumerable.Range(0, _match.WorldItems.Count).Any(i => _match.WorldItems[i].SpawnPoint == 0);

    [Fact]
    public void TakenPoint_IsRefilled_After30Seconds()
    {
        Start(respawnSeconds: 30, TestGameData.WeaponsOnlyLootJson);   // a weapon is always taken whole
        Press(InputButtons.Interact);
        Assert.False(PointFilled());
        uint takenAt = _match.ServerTick - 1;
        _sent.Clear();

        while (_match.ServerTick < takenAt + 900) _match.Tick();
        Assert.False(PointFilled());
        _match.Tick();

        Assert.True(PointFilled());
        int index = Enumerable.Range(0, _match.WorldItems.Count).Single(i => _match.WorldItems[i].SpawnPoint == 0);
        Assert.Equal(PointPosition, _match.WorldItems[index].Data.Position);
        Assert.Contains(_sent, s => s.Peer == 1 && s.Id == PacketId.ItemSpawned);
    }

    [Fact]
    public void ZeroSeconds_NeverRefills()
    {
        Start(respawnSeconds: 0, TestGameData.WeaponsOnlyLootJson);
        Press(InputButtons.Interact);
        for (int i = 0; i < 2000; i++) _match.Tick();
        Assert.False(PointFilled());
    }

    // A partly taken stack stays at its point, so the point is not looted and gets no second item.
    private const string AmmoOnlyLootJson = """
        {
          "rarityWeights": { "Common": 1, "Uncommon": 1, "Rare": 1, "Epic": 1, "Legendary": 1 },
          "tables": { "Floor": [ { "kind": "Ammo", "weight": 1 } ], "Tower": [ { "kind": "Ammo", "weight": 1 } ] }
        }
        """;

    [Fact]
    public void PartialPickup_KeepsThePointFilled_WithoutAnExtraItem()
    {
        Start(respawnSeconds: 1, AmmoOnlyLootJson);
        var type = (AmmoType)_match.WorldItems[0].Data.DefId;
        int max = TestGameData.Items().Ammo(type).Max;
        _match.SetItemAmount(0, 1000);
        _a.Inventory.SetAmmo(type, max - 5);

        Press(InputButtons.Interact);
        for (int i = 0; i < 100; i++) _match.Tick();

        Assert.Equal(max, _a.Inventory.GetAmmo(type));
        Assert.Equal(1, _match.WorldItems.Count);
        Assert.True(PointFilled());
        Assert.Equal(995, _match.WorldItems[0].Data.Amount);
    }

    // D12: dropped items are not spawn points; taking one refills nothing.
    [Fact]
    public void TakingADroppedItem_RefillsNothing()
    {
        Start(respawnSeconds: 1, TestGameData.WeaponsOnlyLootJson);
        _match.RemoveItemAt(0);                            // the point is now waiting
        for (int i = 0; i < 31; i++) _match.Tick();        // ... and refilled
        Assert.True(PointFilled());
        int before = _match.WorldItems.Count;

        _a.State.Position = new Vector3(8f, 0f, 0f);
        Press(InputButtons.Drop);                          // G: a dropped weapon, not a spawn item
        Assert.Equal(before + 1, _match.WorldItems.Count);
        Press(InputButtons.Interact);
        for (int i = 0; i < 100; i++) _match.Tick();
        Assert.Equal(before, _match.WorldItems.Count);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs` (아이템 패킷 수신):

변경 1/3 — 찾을 코드:

```csharp
namespace ProjectH.Server.Tests.Integration;

// Minimal client for tests: same wire protocol as the Unity client, no prediction or rendering.
// Every received combat event is kept in a list; a test client lives for one test only.
public sealed class HeadlessClient : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
```

바꿀 코드:

```csharp
namespace ProjectH.Server.Tests.Integration;

// Minimal client for tests: same wire protocol as the Unity client, no prediction or rendering.
// Every received combat and item event is kept in a list; a test client lives for one test only.
public sealed class HeadlessClient : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
```

변경 2/3 — 찾을 코드:

```csharp
    public List<PlayerDied> Deaths { get; } = new();
    public List<PlayerRespawned> Respawns { get; } = new();

    public void Connect(int port, string devPlayerId, ushort protocolVersion = ProtocolConstants.ProtocolVersion)
    {
        var writer = new PacketWriter(_buffer);
```

바꿀 코드:

```csharp
    public List<PlayerDied> Deaths { get; } = new();
    public List<PlayerRespawned> Respawns { get; } = new();

    // Phase 4: the world item list as this client sees it (WorldItems at join, then upserts and removals),
    // plus every item event in arrival order.
    public Dictionary<ushort, WorldItemData> WorldItems { get; } = new();
    public List<WorldItemData> ItemsSpawned { get; } = new();
    public List<ushort> ItemsRemoved { get; } = new();
    public List<InventoryState> Inventories { get; } = new();
    public List<PickupResult> PickupResults { get; } = new();

    public void Connect(int port, string devPlayerId, ushort protocolVersion = ProtocolConstants.ProtocolVersion)
    {
        var writer = new PacketWriter(_buffer);
```

변경 3/3 — 찾을 코드:

```csharp
            case PacketId.PlayerRespawned:
                if (PlayerRespawned.TryRead(ref r, out var respawned)) Respawns.Add(respawned);
                break;
        }
    }
}
```

바꿀 코드:

```csharp
            case PacketId.PlayerRespawned:
                if (PlayerRespawned.TryRead(ref r, out var respawned)) Respawns.Add(respawned);
                break;
            case PacketId.WorldItems:
                if (!WorldItemsPacket.TryReadHeader(ref r, out int count)) return;
                for (int i = 0; i < count; i++)
                {
                    if (WorldItemData.TryRead(ref r, out var listed)) WorldItems[listed.ItemId] = listed;
                }
                break;
            case PacketId.ItemSpawned:
                if (ItemSpawnedPacket.TryRead(ref r, out var item))
                {
                    WorldItems[item.ItemId] = item;
                    ItemsSpawned.Add(item);
                }
                break;
            case PacketId.ItemRemoved:
                if (ItemRemoved.TryRead(ref r, out var removed))
                {
                    WorldItems.Remove(removed.ItemId);
                    ItemsRemoved.Add(removed.ItemId);
                }
                break;
            case PacketId.InventoryState:
                if (InventoryState.TryRead(ref r, out var inventory)) Inventories.Add(inventory);
                break;
            case PacketId.PickupResult:
                if (PickupResult.TryRead(ref r, out var pickup)) PickupResults.Add(pickup);
                break;
        }
    }
}
```

`Server/tests/ProjectH.Server.Tests/Integration/ItemIntegrationTests.cs`를 만든다(spec §6 통합, 두 HeadlessClient, 실제 UDP). 모든 Point가 무기(`WeaponsOnlyLootJson`), 모두 Medkit 1개로 시작(B가 떨어뜨릴 것), 재생성 끔. A는 가장 가까운 안쪽 무기까지 직선으로 걷고, 멈춘 뒤(누락 입력 반복이 계속 걷지 않게 이동 0 입력) E를 누른다 → 두 Client 모두 `ItemRemoved`. A가 B를 쏜다(Fire/None을 번갈아 보내 단발 무기도 쏜다. 어느 테스트 무기든 한 탄창으로 Health 100을 0으로 만든다) → B 사망. B의 Medkit이 시체 1.1 m 안에 두 Client 모두 `ItemSpawned`로 온다.

```csharp
using System;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Spec §6: two headless clients over real UDP. Every spawn point rolls a weapon (WeaponsOnlyLootJson), and
// everyone starts with one Medkit and nothing else, so the loser has something to drop. Refills are off,
// so every ItemSpawned the test sees is a drop.
public sealed class ItemIntegrationTests : IDisposable
{
    private readonly GameLoop _server;

    public ItemIntegrationTests()
    {
        _server = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
            LootRespawnSeconds = 0,
        }, TestGameData.Create(lootJson: TestGameData.WeaponsOnlyLootJson), NullLogger.Instance,
            new StartingLoadout { Medkits = 1 });
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    private HeadlessClient Join(string devId)
    {
        var client = new HeadlessClient();
        client.Connect(_server.LocalPort, devId);
        Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
        client.SendJoin();
        Assert.True(Pump.Until(() => client.JoinResponse.HasValue, 3000, client), "join response");
        Assert.Equal(JoinResult.Ok, client.JoinResponse?.Result);
        return client;
    }

    private static Vector3 Feet(HeadlessClient viewer, ushort entityId) => viewer.LastSnapshot[entityId].Position;

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        Vector3 d = a - b;
        return MathF.Sqrt(d.X * d.X + d.Z * d.Z);
    }

    [Fact]
    public void PickUp_Shoot_Kill_DeathDrop_SeenByBothClients()
    {
        using var a = Join("looter");
        using var b = Join("target");
        Assert.True(Pump.Until(() => a.WorldItems.Count == LootPoints.All.Length && b.WorldItems.Count == LootPoints.All.Length &&
                                     a.Inventories.Count > 0 && a.LastSnapshot.ContainsKey(a.MyEntityId) &&
                                     a.LastSnapshot.ContainsKey(b.MyEntityId), 3000, a, b), "world list and first snapshots");
        // Both clients see the same items at the same places (success criterion).
        Assert.Equal(a.WorldItems.OrderBy(p => p.Key), b.WorldItems.OrderBy(p => p.Key));
        Assert.True(a.Inventories.Last().Slot0.IsEmpty);   // D1: empty-handed
        Assert.Equal(1, a.Inventories.Last().Medkits);

        // 1. A walks to the nearest inner-ring weapon (LootPointsTests: the straight walk is clear) and presses E.
        Vector3 start = Feet(a, a.MyEntityId);
        WorldItemData target = a.WorldItems.Values
            .Where(i => i.Position.Y == 0f && i.Position.Length() < 8.01f)
            .OrderBy(i => Horizontal(i.Position, start)).First();
        Assert.Equal(ItemKind.Weapon, target.Kind);
        for (int i = 0; i < 150 && Horizontal(Feet(a, a.MyEntityId), target.Position) > 1f; i++)
        {
            Vector3 to = target.Position - Feet(a, a.MyEntityId);
            a.SendMove(0f, 1f, MathF.Atan2(to.X, to.Z) * 180f / MathF.PI);
            Pump.Until(() => false, 33, a, b);
        }
        for (int i = 0; i < 6; i++)   // stop: the server's missed-input repeat would keep walking otherwise
        {
            a.SendMove(0f, 0f, 0f);
            Pump.Until(() => false, 33, a, b);
        }
        Assert.True(Horizontal(Feet(a, a.MyEntityId), target.Position) <= 2f, "A stands within pickup range");

        a.SendInput(new InputCommand { Buttons = InputButtons.Interact });
        Assert.True(Pump.Until(() => a.PickupResults.Count > 0, 3000, a, b), "pickup result");
        Assert.Equal(PickupResultCode.Ok, a.PickupResults[0].Result);
        Assert.Equal(target.ItemId, a.PickupResults[0].ItemId);
        Assert.True(Pump.Until(() => a.ItemsRemoved.Contains(target.ItemId) && b.ItemsRemoved.Contains(target.ItemId), 3000, a, b),
            "both clients told the item is gone");
        Assert.True(Pump.Until(() => a.Inventories.Last().Slot0.WeaponId == target.DefId, 3000, a, b), "A holds the weapon");

        // 2. A shoots B until B dies. Fire / no fire alternates, so the semi-automatic test weapon fires too;
        //    every test weapon kills 100 health (shield 0) within one magazine.
        Vector3 shooter = Feet(a, a.MyEntityId);
        Vector3 body = Feet(a, b.MyEntityId);
        TestAim.YawPitch(shooter, body + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);
        for (int i = 0; i < 150 && b.Deaths.Count == 0; i++)
        {
            a.SendInput(new InputCommand { Buttons = i % 2 == 0 ? InputButtons.Fire : InputButtons.None, AimYaw = yaw, AimPitch = pitch, ViewTick = a.LastServerTick });
            Pump.Until(() => false, 33, a, b);
        }
        Assert.True(Pump.Until(() => a.Deaths.Count > 0 && b.Deaths.Count > 0, 3000, a, b), "B died");
        Assert.Equal(b.MyEntityId, a.Deaths[0].VictimId);

        // 3. B's Medkit drops next to the body, and both clients get it as ItemSpawned.
        bool IsTheDrop(WorldItemData item) => item.Kind == ItemKind.Consumable && item.DefId == (byte)ConsumableType.Medkit &&
                                               item.Amount == 1 && Horizontal(item.Position, body) < 1.1f;
        Assert.True(Pump.Until(() => a.ItemsSpawned.Any(IsTheDrop) && b.ItemsSpawned.Any(IsTheDrop), 3000, a, b), "death drop seen by both");
        Assert.Equal(a.ItemsSpawned.Single(IsTheDrop), b.ItemsSpawned.Single(IsTheDrop));
        Assert.True(Pump.Until(() => b.Inventories.Last().Medkits == 0, 3000, a, b), "B's inventory emptied");
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공, 2개 FAIL(`TakenPoint_IsRefilled_After30Seconds`, `TakingADroppedItem_RefillsNothing`: 재생성이 아직 없다)·433개 PASS. `ItemIntegrationTests`는 Task 4–7의 서버 동작을 끝에서 끝까지 확인하므로 이 시점에 이미 PASS다.

- [ ] **Step 3: 재생성 타이머**

`Server/src/ProjectH.Server/Game/Items/LootSpawner.cs`:

변경 1/3 — 찾을 코드:

```csharp

namespace ProjectH.Server.Game.Items;

// The loot spawn points of the match and the seeded Random that fills them (D5, D6). Owned by Match on
// the game loop thread, like everything else in the match: the Random is never shared with another thread.
public sealed class LootSpawner
{
    private readonly GameData _data;
    private readonly LootPoint[] _points;
    private readonly int[] _tables;
    private readonly Random _rng;

    public LootSpawner(ReadOnlySpan<LootPoint> points, GameData data, int seed)
    {
        _data = data;
        _points = points.ToArray();
```

바꿀 코드:

```csharp

namespace ProjectH.Server.Game.Items;

// The loot spawn points of the match, the seeded Random that fills them (D5, D6) and the refill timers
// (D7). Owned by Match on the game loop thread, like everything else in the match: the Random is never
// shared with another thread. Fixed arrays, one entry per point: nothing grows.
public sealed class LootSpawner
{
    private readonly GameData _data;
    private readonly LootPoint[] _points;
    private readonly int[] _tables;
    private readonly Random _rng;
    private readonly uint _respawnTicks;    // 0 = a looted point stays empty
    private readonly bool[] _waiting;       // the point's item was taken and a refill is due at _refillAt
    private readonly uint[] _refillAt;

    public LootSpawner(ReadOnlySpan<LootPoint> points, GameData data, int seed, uint respawnTicks)
    {
        _data = data;
        _points = points.ToArray();
```

변경 2/3 — 찾을 코드:

```csharp
        if (_points.Length > WorldItems.Capacity / 2)
            throw new ArgumentException($"At most {WorldItems.Capacity / 2} loot points.", nameof(points));
        _rng = new Random(seed);
    }

    public int Count => _points.Length;
```

바꿀 코드:

```csharp
        if (_points.Length > WorldItems.Capacity / 2)
            throw new ArgumentException($"At most {WorldItems.Capacity / 2} loot points.", nameof(points));
        _rng = new Random(seed);
        _respawnTicks = respawnTicks;
        _waiting = new bool[_points.Length];
        _refillAt = new uint[_points.Length];
    }

    public int Count => _points.Length;
```

변경 3/3 — 찾을 코드:

```csharp
    public Vector3 Position(int point) => _points[point].Position;

    public LootRoll Roll(int point) => _data.Loot.Roll(_tables[point], _rng, _data.Weapons, _data.Items);
}
```

바꿀 코드:

```csharp
    public Vector3 Position(int point) => _points[point].Position;

    public LootRoll Roll(int point) => _data.Loot.Roll(_tables[point], _rng, _data.Weapons, _data.Items);

    // The point's item left the world (picked up completely). With respawning on, a new roll is due
    // respawnTicks later (D7); dropped items never come here.
    public void OnTaken(int point, uint now)
    {
        if (_respawnTicks == 0) return;
        _waiting[point] = true;
        _refillAt[point] = now + _respawnTicks;
    }

    public bool IsDue(int point, uint now) => _waiting[point] && now >= _refillAt[point];

    public void OnRefilled(int point) => _waiting[point] = false;
}
```

`Server/src/ProjectH.Server/Game/Match.cs`:

변경 1/3 — 찾을 코드:

```csharp
        _maxRewindTicks = CombatRules.MaxRewindTicks(options.SimHz);

        // D6: the server fills every spawn point when the match starts; clients get the list at join.
        _loot = new LootSpawner(lootPoints ?? LootPoints.All, data, options.LootSeed);
        for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
    }
```

바꿀 코드:

```csharp
        _maxRewindTicks = CombatRules.MaxRewindTicks(options.SimHz);

        // D6: the server fills every spawn point when the match starts; clients get the list at join.
        _loot = new LootSpawner(lootPoints ?? LootPoints.All, data, options.LootSeed,
            (uint)options.LootRespawnSeconds * (uint)options.SimHz);
        for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
    }
```

변경 2/3 — 찾을 코드:

```csharp
        {
            if (!player.Alive && now >= player.RespawnAtTick) Respawn(player);
        }

        foreach (var player in _players)
        {
```

바꿀 코드:

```csharp
        {
            if (!player.Alive && now >= player.RespawnAtTick) Respawn(player);
        }
        RefillLootPoints(now);

        foreach (var player in _players)
        {
```

변경 3/3 — 찾을 코드:

```csharp
    internal void RemoveItemAt(int index)
    {
        ushort itemId = _worldItems[index].Data.ItemId;
        _worldItems.RemoveAt(index);
        BroadcastItemRemoved(itemId);
    }

    // Partial pickup (D9): the rest stays where it was. Sent as ItemSpawned, which clients treat as an upsert.
```

바꿀 코드:

```csharp
    internal void RemoveItemAt(int index)
    {
        ushort itemId = _worldItems[index].Data.ItemId;
        int spawnPoint = _worldItems[index].SpawnPoint;
        _worldItems.RemoveAt(index);
        BroadcastItemRemoved(itemId);
        if (spawnPoint >= 0) _loot.OnTaken(spawnPoint, ServerTick);
    }

    // D7: a looted spawn point gets a new roll from its table once its timer is up.
    private void RefillLootPoints(uint now)
    {
        for (int point = 0; point < _loot.Count; point++)
        {
            if (!_loot.IsDue(point, now)) continue;
            if (SpawnItem(_loot.Roll(point), _loot.Position(point), point) != 0) _loot.OnRefilled(point);
        }
    }

    // Partial pickup (D9): the rest stays where it was. Sent as ItemSpawned, which clients treat as an upsert.
```

- [ ] **Step 4: 서버 테스트 (반복 실행으로 통합 테스트 흔들림 확인)**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 435개 PASS

Run (3회): `dotnet test Server/ProjectH.Server.slnx --no-build`
Expected: 매번 435개 PASS (`PickUp_Shoot_Kill_DeathDrop_SeenByBothClients`가 흔들리지 않는다)

- [ ] **Step 5: 서버 실행 확인 (시간 제한 실행)**

```bash
timeout 12 dotnet run --project Server/src/ProjectH.Server -- --Server:Port=7791
```

Expected: `Server listening on UDP 7791 (SimHz 30, SnapshotHz 15, MaxPlayers 16)`가 찍히고 예외 없이 12초 뒤 종료된다(세 데이터 파일이 출력 폴더에 복사되어 로드되었다는 뜻). `ls Server/src/ProjectH.Server/bin/Debug/net10.0/*.json`에 `items.json`, `loot.json`, `weapons.json`이 있다.

- [ ] **Step 6: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 9: Client 네트워크와 순수 상태 (`NetClient` 아이템 이벤트, `WorldItemList`, `PickupRule`, 칸 3개 `WeaponState`, 새 키)

**Files:**
- Create: `Client/Assets/Scripts/Game/WorldItemList.cs`, `Client/Assets/Scripts/Game/PickupRule.cs`
- Modify: `Client/Assets/Scripts/Game/WeaponState.cs` (전체 교체), `Client/Assets/Scripts/Net/NetClient.cs`, `Client/Assets/Scripts/Input/InputReader.cs`, `Client/Assets/Scripts/Game/LocalPlayerPredictor.cs`, `Client/Assets/Scripts/Game/GameClient.cs` (컴파일 유지와 `InventoryState` 연결만)
- Test: `Client/Assets/Tests/EditMode/WeaponStateTests.cs` (전체 교체), `PickupRuleTests.cs` (신규), `WorldItemListTests.cs` (신규), `LocalPlayerPredictorTests.cs`
- Modify (저장소 밖): 스크래치 `PredictorTests.csproj`

**Interfaces:**
- Consumes: Task 1의 아이템 패킷·`InputButtons` 새 비트·`ItemConstants`; Task 2의 `WeaponInfo.AmmoType`
- Produces (namespace `ProjectH.Client.Game` / `ProjectH.Client.Net`):
  - `WorldItemList { const int Capacity = 256; int Count; WorldItemData this[int]; bool Upsert(in WorldItemData, out int index, out bool added); bool Remove(ushort itemId, out int removedIndex, out int movedFrom); void Clear(); int IndexOf(ushort) }` (movedFrom = 빈자리로 옮겨 온 항목의 원래 인덱스, 없으면 −1)
  - `static class PickupRule { const float Range = 2f, Height = 2f; static int FindNearest(WorldItemList items, System.Numerics.Vector3 feet) }` (서버 `WorldItems.FindNearest`와 같은 규칙)
  - `WeaponState(WeaponInfo[] catalog)` — `const int SlotCount = 3; int Slot; bool Reloading; bool HasWeapon; WeaponInfo Current; int Ammo; int Reserve; int ReloadRemainingSteps; int GetReserve(AmmoType); bool TryGetSlot(int slot, out WeaponInfo weapon, out int rarity, out int ammo); void Clear(); void ApplyInventory(in InventoryState); bool Step(uint seq, InputButtons); void ApplyServer(in SnapshotSelf, uint ackSeq)` (`Refill` 삭제)
  - `NetClient` 이벤트: `ItemCatalogReceived(ItemCatalogData)`, `ItemReceived(WorldItemData)`(WorldItems 목록과 ItemSpawned 모두), `ItemRemovedReceived(ushort)`, `InventoryReceived(InventoryState)`, `PickupResultReceived(PickupResult)`
  - `InputReader`: 3 → `Slot3`, E → `Interact`, G → `Drop`, 4 → `UseMedkit`, 5 → `UseShieldCell` (모두 `QueuedButtons`의 눌림). `LocalPlayerPredictor`의 눌림 마스크에 5개 비트 추가.

- [ ] **Step 1: 실패하는 테스트 작성**

`Client/Assets/Tests/EditMode/WeaponStateTests.cs` 전체를 다음으로 바꾼다. Phase 3 테스트(자동·단발·교체·재조정 4종)를 모두 유지하고, 빈손 시작, 보유량 재장전·부족·0, Slot3 빈 칸, `ApplyInventory`(칸·보유량, **현재 칸의 탄창은 Snapshot에 맡김**, 새 무기면 탄창을 받고 재장전 중지), 재적용이 보유량을 두 번 빼지 않음, 빈 칸의 서버 값을 더한다.

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Same numbers as the server's test catalog (TestWeapons) and combat loadout (TestGameData.CombatLoadout):
    // slot 0 "Test Auto" (automatic, 3-tick interval, 6 rounds, 30-tick reload, Medium), slot 1 "Test Semi"
    // (15-tick interval, 2 rounds, 60-tick reload, Heavy), slot 2 empty; reserves Medium 60, Heavy 30.
    public class WeaponStateTests
    {
        private static WeaponInfo[] Catalog() => new[]
        {
            new WeaponInfo { WeaponId = 1, Name = "Test Auto", Damage = 30, FireIntervalTicks = 3, MagazineSize = 6, ReloadTicks = 30, Range = 100f, Automatic = true, AmmoType = AmmoType.Medium },
            new WeaponInfo { WeaponId = 2, Name = "Test Semi", Damage = 90, FireIntervalTicks = 15, MagazineSize = 2, ReloadTicks = 60, Range = 300f, Automatic = false, AmmoType = AmmoType.Heavy },
            new WeaponInfo { WeaponId = 3, Name = "Test Light", Damage = 10, FireIntervalTicks = 3, MagazineSize = 10, ReloadTicks = 15, Range = 50f, Automatic = true, AmmoType = AmmoType.Light },
        };

        private static InventoryState Loadout(int medium = 60, int heavy = 30) => new InventoryState
        {
            Slot0 = new InventorySlotState { WeaponId = 1, MagAmmo = 6 },
            Slot1 = new InventorySlotState { WeaponId = 2, MagAmmo = 2 },
            MediumAmmo = (ushort)medium,
            HeavyAmmo = (ushort)heavy,
        };

        private uint _seq;

        // NUnit reuses one fixture instance for every test in the class.
        [SetUp]
        public void ResetSeq() => _seq = 0;

        private bool Step(WeaponState state, InputButtons buttons) => state.Step(++_seq, buttons);

        private static WeaponState Armed(int medium = 60, int heavy = 30)
        {
            var state = new WeaponState(Catalog());
            state.ApplyInventory(Loadout(medium, heavy));
            return state;
        }

        [Test]
        public void New_IsEmptyHanded_AndNeverFires()
        {
            var state = new WeaponState(Catalog());
            Assert.IsFalse(state.HasWeapon);
            Assert.AreEqual(0, state.Ammo);
            for (int i = 0; i < 10; i++) Assert.IsFalse(Step(state, InputButtons.Fire | InputButtons.Reload));
            Assert.IsFalse(state.Reloading);
        }

        [Test]
        public void Auto_Held_FiresOncePerInterval()
        {
            var state = Armed();
            int shots = 0;
            for (int i = 0; i < 9; i++)
            {
                bool fired = Step(state, InputButtons.Fire);
                Assert.AreEqual(i % 3 == 0, fired, "step " + i);
                if (fired) shots++;
            }
            Assert.AreEqual(3, shots);
            Assert.AreEqual(3, state.Ammo);
        }

        [Test]
        public void Auto_EmptyMagazine_ReloadsFromTheReserve_ThenFiresAgain()
        {
            var state = Armed();
            int shots = 0;
            // 6 shots at steps 0..15, reload from 15 to 45, then the next shot at step 45.
            for (int i = 0; i <= 45; i++)
            {
                if (Step(state, InputButtons.Fire)) shots++;
                if (i == 15) Assert.IsTrue(state.Reloading);
                if (i > 15 && i < 45) Assert.AreEqual(0, state.Ammo);
            }
            Assert.AreEqual(7, shots);
            Assert.IsFalse(state.Reloading);
            Assert.AreEqual(5, state.Ammo);
            Assert.AreEqual(54, state.Reserve);
        }

        [Test]
        public void EmptyReserve_NoReload()
        {
            var state = Armed(medium: 0);
            for (int i = 0; i <= 60; i++) Step(state, InputButtons.Fire);
            Assert.AreEqual(0, state.Ammo);
            Assert.IsFalse(state.Reloading);
        }

        [Test]
        public void ShortReserve_FillsWhatItHas()
        {
            var state = Armed(medium: 2);
            for (int i = 0; i <= 45; i++) Step(state, InputButtons.Fire);   // 6 shots, then a reload of 2
            Assert.AreEqual(1, state.Ammo);                                 // 2 in, one fired at step 45
            Assert.AreEqual(0, state.Reserve);
        }

        [Test]
        public void Semi_Held_FiresOnce()
        {
            var state = Armed();
            Assert.IsTrue(Step(state, InputButtons.Slot2 | InputButtons.Fire));
            for (int i = 0; i < 40; i++) Assert.IsFalse(Step(state, InputButtons.Fire));
            Assert.IsFalse(Step(state, InputButtons.None));
            Assert.IsTrue(Step(state, InputButtons.Fire));
        }

        [Test]
        public void ReloadButton_And_Switch_FollowServerOrder()
        {
            var state = Armed();
            Assert.IsTrue(Step(state, InputButtons.Fire));
            Step(state, InputButtons.Reload);
            Assert.IsTrue(state.Reloading);
            Step(state, InputButtons.Slot2);                       // switching cancels the reload
            Assert.AreEqual(1, state.Slot);
            Assert.IsFalse(state.Reloading);
            Step(state, InputButtons.Slot1 | InputButtons.Slot2);  // several: ignored
            Assert.AreEqual(1, state.Slot);
            Step(state, InputButtons.Slot1);
            Assert.AreEqual(5, state.Ammo);
            Assert.AreEqual("Test Auto", state.Current.Name);
        }

        // D10 decision, same as the server: an empty slot can be selected, and nothing fires there.
        [Test]
        public void Slot3_Empty_CanBeSelected_ButNeverFires()
        {
            var state = Armed();
            Assert.IsFalse(Step(state, InputButtons.Slot3 | InputButtons.Fire));
            Assert.AreEqual(2, state.Slot);
            Assert.IsFalse(state.HasWeapon);
            Assert.IsFalse(Step(state, InputButtons.Fire));
        }

        [Test]
        public void ApplyInventory_FillsSlots_AndReserves()
        {
            var state = Armed();
            Assert.IsTrue(state.TryGetSlot(1, out WeaponInfo semi, out int rarity, out int ammo));
            Assert.AreEqual("Test Semi", semi.Name);
            Assert.AreEqual(0, rarity);
            Assert.AreEqual(2, ammo);
            Assert.IsFalse(state.TryGetSlot(2, out _, out _, out _));
            Assert.AreEqual(30, state.GetReserve(AmmoType.Heavy));

            var withLight = Loadout();
            withLight.Slot2 = new InventorySlotState { WeaponId = 3, Rarity = 4, MagAmmo = 7 };
            state.ApplyInventory(withLight);
            Assert.IsTrue(state.TryGetSlot(2, out WeaponInfo light, out rarity, out ammo));
            Assert.AreEqual("Test Light", light.Name);
            Assert.AreEqual(4, rarity);
            Assert.AreEqual(7, ammo);
        }

        // Review trap: the InventoryState (Reliable) and snapshots (Sequenced) arrive in any order. If an
        // InventoryState set the current slot's magazine, a later matching snapshot would never correct it,
        // so the current slot's magazine is left to the snapshot while the weapon in it is unchanged.
        [Test]
        public void ApplyInventory_LeavesTheCurrentMagazineToTheSnapshot_TakesTheOthers()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);   // local: 5 left in slot 0
            var older = Loadout();
            older.Slot0.MagAmmo = 6;          // an inventory from before that shot
            older.Slot1.MagAmmo = 1;
            state.ApplyInventory(older);

            Assert.AreEqual(5, state.Ammo);
            Assert.IsTrue(state.TryGetSlot(1, out _, out _, out int semiAmmo));
            Assert.AreEqual(1, semiAmmo);
        }

        [Test]
        public void ApplyInventory_NewWeaponInTheCurrentSlot_TakesItsMagazine_AndStopsTheReload()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);
            Step(state, InputButtons.Reload);
            Assert.IsTrue(state.Reloading);

            var swapped = Loadout();
            swapped.Slot0 = new InventorySlotState { WeaponId = 3, Rarity = 1, MagAmmo = 4 };   // swap pickup
            state.ApplyInventory(swapped);

            Assert.AreEqual("Test Light", state.Current.Name);
            Assert.AreEqual(4, state.Ammo);
            Assert.IsFalse(state.Reloading);

            var dropped = Loadout();
            dropped.Slot0 = default;                                                          // G
            state.ApplyInventory(dropped);
            Assert.IsFalse(state.HasWeapon);
            Assert.IsFalse(Step(state, InputButtons.Fire));
        }

        [Test]
        public void ApplyServer_MatchingValues_KeepsNewerLocalSteps()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);   // seq 1: 5 left
            Step(state, InputButtons.None);
            Step(state, InputButtons.None);
            Step(state, InputButtons.Fire);   // seq 4: 4 left, not acked yet

            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 5 }, 1);

            Assert.AreEqual(4, state.Ammo);
        }

        [Test]
        public void ApplyServer_DifferentValues_TakesTheServers()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);   // local: 5 left after seq 1

            // The server rejected that shot (e.g. its input was lost) and is reloading slot 1.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 1, Ammo = 1, ReloadRemainingTicks = 10 }, 1);

            Assert.AreEqual(1, state.Slot);
            Assert.AreEqual(1, state.Ammo);
            Assert.IsTrue(state.Reloading);
            Assert.AreEqual(10, state.ReloadRemainingSteps);
        }

        [Test]
        public void ApplyServer_Mismatch_SnapsOnce_AndReplaysNewerSteps()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);   // seq 1: fires, 5 left
            Step(state, InputButtons.None);   // seq 2
            Step(state, InputButtons.None);   // seq 3
            Step(state, InputButtons.Fire);   // seq 4: fires, 4 left

            // The server did not fire seq 1 (input lost): it still has 6 after ack 1.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 6 }, 1);
            Assert.AreEqual(5, state.Ammo);   // server 6 minus the shot fired locally after the ack

            // Later snapshots with the server's (still unfired) values must not snap again.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 6 }, 2);
            Assert.AreEqual(5, state.Ammo);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 6 }, 3);
            Assert.AreEqual(5, state.Ammo);
        }

        [Test]
        public void ApplyServer_SnapDuringReload_EndsReloadOnTheServersStep()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);     // seq 1
            Step(state, InputButtons.Reload);   // seq 2
            for (int i = 0; i < 3; i++) Step(state, InputButtons.None);   // seq 3..5, unacked

            // After tick of seq 2 the server has 20 reload ticks left (and one round less than this copy).
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 4, ReloadRemainingTicks = 20 }, 2);
            Assert.IsTrue(state.Reloading);

            // Ticks of seq 3..22 are the 20 remaining ticks; the reload completes at the tick of seq 23.
            while (_seq < 22) Step(state, InputButtons.None);
            Assert.IsTrue(state.Reloading);
            Step(state, InputButtons.None);     // seq 23
            Assert.IsFalse(state.Reloading);
            Assert.AreEqual(6, state.Ammo);
            Assert.AreEqual(58, state.Reserve);
        }

        // A replay that re-runs a reload must not take its rounds from the reserve twice.
        [Test]
        public void ApplyServer_ReplayAcrossAReload_TakesTheReserveOnce()
        {
            var state = Armed();
            Step(state, InputButtons.Fire);                                 // seq 1: 5 left
            Step(state, InputButtons.Reload);                               // seq 2: reload until step 31
            for (int i = 0; i < 31; i++) Step(state, InputButtons.None);    // seq 3..33: the reload finished locally
            Assert.AreEqual(6, state.Ammo);
            Assert.AreEqual(59, state.Reserve);

            // Ack 1 with a different magazine forces a replay of seq 2..33, reload included.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 4 }, 1);
            Assert.AreEqual(6, state.Ammo);
            Assert.AreEqual(58, state.Reserve);   // the server's 4 + 2 from the reserve, once
        }

        [Test]
        public void ApplyServer_IgnoresAckZero_AndSlotOutsideTheInventory()
        {
            var state = Armed();
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 1 }, 0);
            Assert.AreEqual(6, state.Ammo);

            Step(state, InputButtons.None);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 3, Ammo = 1 }, 1);
            Assert.AreEqual(0, state.Slot);
            Assert.AreEqual(6, state.Ammo);
        }

        [Test]
        public void ApplyServer_OnAnEmptySlot_DoesNotTouchAWeapon()
        {
            var state = Armed();
            Step(state, InputButtons.None);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 2, Ammo = 0 }, 1);   // the server is on slot 3
            Assert.AreEqual(2, state.Slot);
            Assert.IsFalse(state.HasWeapon);
            Assert.IsFalse(state.Reloading);
        }

        [Test]
        public void Clear_EmptiesEverything()
        {
            var state = Armed();
            Step(state, InputButtons.Slot2 | InputButtons.Fire);
            state.Clear();
            Assert.AreEqual(0, state.Slot);
            Assert.IsFalse(state.HasWeapon);
            Assert.AreEqual(0, state.GetReserve(AmmoType.Medium));
            Assert.IsFalse(state.Reloading);
        }
    }
}
```

`Client/Assets/Tests/EditMode/PickupRuleTests.cs`를 만든다(서버 `WorldItemsTests.FindNearest_*`와 같은 경우).

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    // The prompt must point at the item the server will take: same cases as the server's
    // WorldItemsTests.FindNearest_* (2 m on the ground plane, 2 m up or down, nearest, ties to the lower id).
    public class PickupRuleTests
    {
        private static WorldItemData Item(ushort id, NVector3 position) => new WorldItemData
        {
            ItemId = id,
            Kind = ItemKind.Ammo,
            DefId = (byte)AmmoType.Light,
            Amount = 10,
            Position = position,
        };

        private static WorldItemList ListOf(params WorldItemData[] items)
        {
            var list = new WorldItemList();
            foreach (var item in items) list.Upsert(item, out _, out _);
            return list;
        }

        [TestCase(2.0f, 0f, 0f, true)]
        [TestCase(2.001f, 0f, 0f, false)]
        [TestCase(1.4f, 0f, 1.4f, true)]
        [TestCase(1.42f, 0f, 1.42f, false)]
        [TestCase(0f, 2.0f, 0f, true)]
        [TestCase(0f, 2.01f, 0f, false)]
        [TestCase(0f, -2.0f, 0f, true)]
        [TestCase(0f, -2.01f, 0f, false)]
        public void RangeBoundary_MatchesTheServer(float dx, float dy, float dz, bool expectedFound)
        {
            var feet = new NVector3(5f, 1f, -3f);
            var list = ListOf(Item(1, feet + new NVector3(dx, dy, dz)));
            Assert.AreEqual(expectedFound, PickupRule.FindNearest(list, feet) >= 0);
        }

        [Test]
        public void Nearest_TiesGoToTheLowerId_WhateverTheListOrder()
        {
            var feet = NVector3.Zero;
            // Stored high id first: the rule must not depend on storage order.
            var list = ListOf(Item(9, new NVector3(0f, 0f, 1f)), Item(4, new NVector3(0f, 0f, -1f)), Item(2, new NVector3(1.5f, 0f, 0f)));
            Assert.AreEqual(4, list[PickupRule.FindNearest(list, feet)].ItemId);

            list.Remove(4, out _, out _);
            Assert.AreEqual(9, list[PickupRule.FindNearest(list, feet)].ItemId);
            list.Remove(9, out _, out _);
            Assert.AreEqual(2, list[PickupRule.FindNearest(list, feet)].ItemId);
            list.Remove(2, out _, out _);
            Assert.AreEqual(-1, PickupRule.FindNearest(list, feet));
        }
    }
}
```

`Client/Assets/Tests/EditMode/WorldItemListTests.cs`를 만든다.

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using NVector3 = System.Numerics.Vector3;

namespace ProjectH.Client.Tests
{
    public class WorldItemListTests
    {
        private static WorldItemData Item(ushort id, ushort amount = 10) => new WorldItemData
        {
            ItemId = id,
            Kind = ItemKind.Ammo,
            DefId = (byte)AmmoType.Light,
            Amount = amount,
            Position = new NVector3(id, 0f, 0f),
        };

        [Test]
        public void Upsert_AddsNew_UpdatesKnown()
        {
            var list = new WorldItemList();
            Assert.IsTrue(list.Upsert(Item(5), out int index, out bool added));
            Assert.IsTrue(added);
            Assert.AreEqual(0, index);

            Assert.IsTrue(list.Upsert(Item(5, amount: 3), out index, out added));   // ItemSpawned as an amount change
            Assert.IsFalse(added);
            Assert.AreEqual(0, index);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(3, list[0].Amount);
        }

        // The views array follows the list through movedFrom.
        [Test]
        public void Remove_MovesTheLastIntoTheHole_AndSaysSo()
        {
            var list = new WorldItemList();
            for (ushort id = 1; id <= 3; id++) list.Upsert(Item(id), out _, out _);

            Assert.IsTrue(list.Remove(1, out int removed, out int movedFrom));
            Assert.AreEqual(0, removed);
            Assert.AreEqual(2, movedFrom);
            Assert.AreEqual(3, list[0].ItemId);

            Assert.IsTrue(list.Remove(2, out removed, out movedFrom));
            Assert.AreEqual(1, removed);
            Assert.AreEqual(-1, movedFrom);   // it was the last one
            Assert.AreEqual(1, list.Count);

            Assert.IsFalse(list.Remove(42, out _, out _));   // a late or unknown id is ignored
        }

        // D13: never more than the server's 256.
        [Test]
        public void Full_RefusesANewId_ButStillUpdatesKnownOnes()
        {
            var list = new WorldItemList();
            for (int i = 1; i <= WorldItemList.Capacity; i++) Assert.IsTrue(list.Upsert(Item((ushort)i), out _, out _));
            Assert.IsFalse(list.Upsert(Item(1000), out _, out _));
            Assert.AreEqual(WorldItemList.Capacity, list.Count);
            Assert.IsTrue(list.Upsert(Item(7, amount: 1), out int index, out bool added));
            Assert.IsFalse(added);
            Assert.AreEqual(1, list[index].Amount);
        }

        [Test]
        public void Clear_EmptiesTheList()
        {
            var list = new WorldItemList();
            list.Upsert(Item(1), out _, out _);
            list.Clear();
            Assert.AreEqual(0, list.Count);
            Assert.AreEqual(-1, list.IndexOf(1));
        }
    }
}
```

`Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs` (새 눌림 5개가 마지막 Step에 실린다):

변경 — 찾을 코드:

```csharp
            Assert.AreEqual(InputButtons.None, queued);
        }

        // PredictedPosition (and each step's result SetAim aims from) must be the state the server's Step
        // produces after the newest input, not the interpolated RenderPosition that trails it mid-step.
        [Test]
```

바꿀 코드:

```csharp
            Assert.AreEqual(InputButtons.None, queued);
        }

        // Phase 4: the new presses (3, E, G, 4, 5) are queued presses too. A mask that forgot one would drop
        // the key silently.
        [Test]
        public void Phase4Presses_RideOnTheLastStep()
        {
            var predictor = NewPredictor();
            InputButtons presses = InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                   InputButtons.UseMedkit | InputButtons.UseShieldCell;
            InputButtons queued = presses;
            predictor.Advance(2 * Step + 0.0005f, Vector2.zero, 0f, presses, ref queued);

            Assert.AreEqual(InputButtons.None, predictor.InputAt(1).Buttons);   // never "held"
            Assert.AreEqual(presses, predictor.InputAt(2).Buttons);
            Assert.AreEqual(InputButtons.None, queued);
        }

        // PredictedPosition (and each step's result SetAim aims from) must be the state the server's Step
        // produces after the newest input, not the interpolated RenderPosition that trails it mid-step.
        [Test]
```

- [ ] **Step 2: 스크래치 NUnit 프로젝트에 새 순수 소스 추가**

`C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj`에서 지워진 파일을 가리키는 다음 줄을 지운다.

```xml
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/FireRateAccumulator.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/FireRateAccumulator.cs')" />
```

그리고 `<Compile Include="E:/popol/ProjectH/Shared/Runtime/**/*.cs" />` 줄 바로 앞에 다음 두 줄을 넣는다.

```xml
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/WorldItemList.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/WorldItemList.cs')" />
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/PickupRule.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/PickupRule.cs')" />
```

- [ ] **Step 3: 테스트 실패 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 빌드 실패 — `WorldItemList`, `PickupRule`, `WeaponState.ApplyInventory` 등이 없다는 오류

- [ ] **Step 4: `WorldItemList`, `PickupRule`**

`Client/Assets/Scripts/Game/WorldItemList.cs`를 만든다.

```csharp
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // The client's copy of the world item list (D14): filled by WorldItems at join, then changed only by
    // ItemSpawned (upsert) and ItemRemoved. Fixed arrays of Capacity, the server's limit (D13), so it never
    // grows and nothing here allocates. Removal moves the last item into the hole; Remove reports that
    // move so a parallel array of views (WorldItemViews) can follow it. Main thread only.
    public sealed class WorldItemList
    {
        public const int Capacity = 256;   // server WorldItems.Capacity

        private readonly WorldItemData[] _items = new WorldItemData[Capacity];

        public int Count { get; private set; }

        public WorldItemData this[int index] => _items[index];

        // Returns false only when a new id arrives while the list is full, which the server never causes
        // (it evicts before it adds, and the events arrive in order).
        public bool Upsert(in WorldItemData item, out int index, out bool added)
        {
            index = IndexOf(item.ItemId);
            added = index < 0;
            if (added)
            {
                if (Count == Capacity) return false;
                index = Count++;
            }
            _items[index] = item;
            return true;
        }

        // movedFrom: the old index of the item now at removedIndex, or -1 when the removed one was last.
        public bool Remove(ushort itemId, out int removedIndex, out int movedFrom)
        {
            removedIndex = IndexOf(itemId);
            movedFrom = -1;
            if (removedIndex < 0) return false;
            Count--;
            if (removedIndex != Count)
            {
                _items[removedIndex] = _items[Count];
                movedFrom = Count;
            }
            _items[Count] = default;
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < Count; i++) _items[i] = default;
            Count = 0;
        }

        public int IndexOf(ushort itemId)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_items[i].ItemId == itemId) return i;
            }
            return -1;
        }
    }
}
```

`Client/Assets/Scripts/Game/PickupRule.cs`를 만든다.

```csharp
using System.Numerics;

namespace ProjectH.Client.Game
{
    // Copy of the server's pickup target rule (Server ItemRules + WorldItems.FindNearest, D8), used only to
    // show the "[E]" prompt on the item the server will pick. The server still decides. Keep the two in step:
    // within 2 m on the ground plane and 2 m up or down from the feet, nearest by 3D distance, ties to the
    // lower ItemId. Pure, no allocation.
    public static class PickupRule
    {
        public const float Range = 2f;
        public const float Height = 2f;

        // Index into the list, or -1.
        public static int FindNearest(WorldItemList items, Vector3 feet)
        {
            int best = -1;
            float bestDistance = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                Vector3 d = items[i].Position - feet;
                float horizontalSq = d.X * d.X + d.Z * d.Z;
                if (horizontalSq > Range * Range || d.Y > Height || d.Y < -Height) continue;
                float distance = horizontalSq + d.Y * d.Y;
                if (best < 0 || distance < bestDistance ||
                    (distance == bestDistance && items[i].ItemId < items[best].ItemId))
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }
}
```

- [ ] **Step 5: `WeaponState` 칸 3개**

`Client/Assets/Scripts/Game/WeaponState.cs` 전체를 다음으로 바꾼다. 기록(Record)에 보유량 3종을 넣어 재적용 전에 되돌린다.

```csharp
using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Presentation copy of the server's weapon rules (Server WeaponRules), stepped once per predicted input,
    // so the local tracer and the ammo counter react at once (Phase 3 D12). The server still decides every
    // shot. One Step = one simulation tick, the unit of the catalog's tick values.
    // Phase 4: three inventory slots, any of them empty, and reloads that draw on the ammo reserve. What sits
    // in each slot and the reserves come from the server's InventoryState (ApplyInventory); the current slot
    // and its magazine come from the snapshot self block (ApplyServer). Keep the rules identical to WeaponRules.
    public sealed class WeaponState
    {
        public const int SlotCount = ItemConstants.WeaponSlotCount;
        private const int HistorySize = 64;       // same as LocalPlayerPredictor: covers every unacked input

        private struct Held
        {
            public int Catalog;         // index into _catalog, -1 = empty slot
            public byte Rarity;
            public int Ammo;
            public long NextFireStep;
        }

        // Everything needed to compare with the server and to replay the step (same idea as the movement
        // predictor's history): the input and the result it produced.
        private struct Record
        {
            public uint Seq;
            public long Step;
            public InputButtons Buttons;
            public int Slot;
            public int Ammo;
            public bool Reloading;
            public long NextFireStep;   // of Slot, after this step
            public int Light;           // reserves after this step, restored before a replay
            public int Medium;
            public int Heavy;
        }

        private readonly WeaponInfo[] _catalog;
        private readonly Held[] _slots = new Held[SlotCount];
        private readonly int[] _reserve = new int[ItemConstants.AmmoTypeCount];   // index = AmmoType - 1
        private readonly Record[] _history = new Record[HistorySize];
        private long _step;
        private long _reloadEndStep;
        private bool _fireHeld;

        // catalog: every weapon of the server's WeaponCatalog (weapon items and slots refer to them by id).
        public WeaponState(WeaponInfo[] catalog)
        {
            if (catalog == null || catalog.Length == 0) throw new ArgumentException("Empty weapon catalog.", nameof(catalog));
            _catalog = (WeaponInfo[])catalog.Clone();
            Clear();
        }

        public int Slot { get; private set; }
        public bool Reloading { get; private set; }
        public bool HasWeapon => _slots[Slot].Catalog >= 0;
        // Only meaningful while HasWeapon.
        public WeaponInfo Current => _catalog[Math.Max(0, _slots[Slot].Catalog)];
        public int Ammo => HasWeapon ? _slots[Slot].Ammo : 0;
        public int Reserve => HasWeapon ? GetReserve(Current.AmmoType) : 0;
        public int ReloadRemainingSteps => Reloading ? (int)Math.Max(0, _reloadEndStep - _step) : 0;

        public int GetReserve(AmmoType type) => type == AmmoType.None ? 0 : _reserve[(int)type - 1];

        // For the HUD: false for an empty slot.
        public bool TryGetSlot(int slot, out WeaponInfo weapon, out int rarity, out int ammo)
        {
            Held held = _slots[slot];
            weapon = held.Catalog >= 0 ? _catalog[held.Catalog] : default;
            rarity = held.Rarity;
            ammo = held.Ammo;
            return held.Catalog >= 0;
        }

        // Join, death and respawn: empty-handed (D1) until the server's InventoryState says otherwise.
        public void Clear()
        {
            Slot = 0;
            for (int i = 0; i < SlotCount; i++) _slots[i] = new Held { Catalog = -1 };
            Array.Clear(_reserve, 0, _reserve.Length);
            Reloading = false;
            _fireHeld = false;
        }

        // The server's inventory (Reliable, only when it changed). Slot contents and reserves are taken as they
        // are. A slot's magazine is taken when the weapon in it changed or the slot is not the current one;
        // the current slot's magazine belongs to the snapshot, whose ack-based check would otherwise never
        // correct a value set here (the two packets arrive in any order). The current slot index also comes
        // from the snapshot.
        public void ApplyInventory(in InventoryState server)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                InventorySlotState s = server.GetSlot(i);
                int catalog = s.IsEmpty ? -1 : IndexOf(s.WeaponId);
                bool same = catalog == _slots[i].Catalog && (catalog < 0 || s.Rarity == _slots[i].Rarity);
                if (!same)
                {
                    _slots[i] = new Held { Catalog = catalog, Rarity = s.Rarity, Ammo = catalog < 0 ? 0 : s.MagAmmo };
                    if (i == Slot) Reloading = false;
                }
                else if (i != Slot && catalog >= 0)
                {
                    _slots[i].Ammo = s.MagAmmo;
                }
            }
            _reserve[0] = server.LightAmmo;
            _reserve[1] = server.MediumAmmo;
            _reserve[2] = server.HeavyAmmo;
        }

        // One predicted input, oldest first. Returns true when the server is expected to fire it.
        public bool Step(uint seq, InputButtons buttons)
        {
            return Run(seq, buttons, _step++);
        }

        private bool Run(uint seq, InputButtons buttons, long now)
        {
            if (Reloading && now >= _reloadEndStep)
            {
                Reloading = false;
                FinishReload();
            }

            bool fired = Apply(buttons, now);
            _history[seq % HistorySize] = new Record
            {
                Seq = seq, Step = now, Buttons = buttons, Slot = Slot, Ammo = Ammo, Reloading = Reloading,
                NextFireStep = _slots[Slot].NextFireStep, Light = _reserve[0], Medium = _reserve[1], Heavy = _reserve[2],
            };
            return fired;
        }

        // The server's values after it processed input ackSeq (snapshot self block). If they equal what this
        // copy had after the same input, the newer local steps stand. Otherwise the server wins at that
        // point: the state restarts from its values and the inputs after ackSeq are replayed, rewriting their
        // history, so the next snapshot compares against consistent records and ReloadRemainingTicks is
        // measured from the ack point (Phase 3 D12).
        public void ApplyServer(in SnapshotSelf server, uint ackSeq)
        {
            if (ackSeq == 0) return;                                  // no input processed yet: nothing to compare
            if (server.WeaponSlot >= SlotCount) return;              // untrusted value outside the inventory

            bool serverReloading = server.ReloadRemainingTicks > 0;
            Record local = _history[ackSeq % HistorySize];
            bool known = local.Seq == ackSeq;
            if (known && local.Slot == server.WeaponSlot && local.Ammo == server.Ammo && local.Reloading == serverReloading)
                return;

            Slot = server.WeaponSlot;
            if (HasWeapon) _slots[Slot].Ammo = Math.Min(server.Ammo, (int)Current.MagazineSize);
            Reloading = serverReloading && HasWeapon;

            if (!known)
            {
                // The ack is older than the history: no replay possible, restart from the server's values.
                _reloadEndStep = _step + server.ReloadRemainingTicks;
                return;
            }

            long stepAfterAck = local.Step + 1;
            _reloadEndStep = stepAfterAck + server.ReloadRemainingTicks;
            if (local.Slot == Slot) _slots[Slot].NextFireStep = local.NextFireStep;
            _reserve[0] = local.Light;
            _reserve[1] = local.Medium;
            _reserve[2] = local.Heavy;
            _fireHeld = (local.Buttons & InputButtons.Fire) != 0;
            _history[ackSeq % HistorySize] = new Record
            {
                Seq = ackSeq, Step = local.Step, Buttons = local.Buttons, Slot = Slot, Ammo = Ammo, Reloading = Reloading,
                NextFireStep = _slots[Slot].NextFireStep, Light = local.Light, Medium = local.Medium, Heavy = local.Heavy,
            };

            long end = _step;
            _step = stepAfterAck;
            for (uint seq = ackSeq + 1; _step < end; seq++)
            {
                Record r = _history[seq % HistorySize];
                if (r.Seq != seq) break;                               // cannot happen while the ack record is valid
                Run(seq, r.Buttons, _step++);
            }
            _step = end;
        }

        // Same order as the server: switch -> reload -> fire.
        private bool Apply(InputButtons buttons, long now)
        {
            int target = -1;
            switch (buttons & (InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3))
            {
                case InputButtons.Slot1: target = 0; break;
                case InputButtons.Slot2: target = 1; break;
                case InputButtons.Slot3: target = 2; break;
            }
            if (target >= 0 && target != Slot)
            {
                Slot = target;
                Reloading = false;
            }

            bool fireHeld = (buttons & InputButtons.Fire) != 0;
            if (!HasWeapon)
            {
                _fireHeld = fireHeld;
                return false;
            }

            WeaponInfo weapon = Current;
            ref Held held = ref _slots[Slot];
            if ((buttons & InputButtons.Reload) != 0 && !Reloading && held.Ammo < weapon.MagazineSize)
                TryStartReload(weapon, now);

            bool trigger = fireHeld && (weapon.Automatic || !_fireHeld);
            _fireHeld = fireHeld;
            if (!trigger || Reloading || now < held.NextFireStep) return false;

            if (held.Ammo == 0)
            {
                TryStartReload(weapon, now);
                return false;
            }

            held.Ammo--;
            held.NextFireStep = now + weapon.FireIntervalTicks;
            if (held.Ammo == 0) TryStartReload(weapon, now);
            return true;
        }

        private void TryStartReload(WeaponInfo weapon, long now)
        {
            if (GetReserve(weapon.AmmoType) == 0) return;
            Reloading = true;
            _reloadEndStep = now + weapon.ReloadTicks;
        }

        private void FinishReload()
        {
            if (!HasWeapon) return;
            WeaponInfo weapon = Current;
            int index = (int)weapon.AmmoType - 1;
            if (index < 0) return;
            int take = Math.Min(weapon.MagazineSize - _slots[Slot].Ammo, _reserve[index]);
            if (take <= 0) return;
            _slots[Slot].Ammo += take;
            _reserve[index] -= take;
        }

        private int IndexOf(byte weaponId)
        {
            for (int i = 0; i < _catalog.Length; i++)
            {
                if (_catalog[i].WeaponId == weaponId) return i;
            }
            return -1;
        }
    }
}
```

- [ ] **Step 6: 키와 눌림 마스크**

`Client/Assets/Scripts/Input/InputReader.cs`:

변경 1/6 — 찾을 코드:

```csharp
        private readonly InputAction _reload;
        private readonly InputAction _slot1;
        private readonly InputAction _slot2;
        private readonly InputAction _unlockCursor;

        public InputReader()
```

바꿀 코드:

```csharp
        private readonly InputAction _reload;
        private readonly InputAction _slot1;
        private readonly InputAction _slot2;
        private readonly InputAction _slot3;
        private readonly InputAction _interact;
        private readonly InputAction _drop;
        private readonly InputAction _useMedkit;
        private readonly InputAction _useShieldCell;
        private readonly InputAction _unlockCursor;

        public InputReader()
```

변경 2/6 — 찾을 코드:

```csharp
            _reload = new InputAction("Reload", InputActionType.Button, "<Keyboard>/r");
            _slot1 = new InputAction("Slot1", InputActionType.Button, "<Keyboard>/1");
            _slot2 = new InputAction("Slot2", InputActionType.Button, "<Keyboard>/2");
            _unlockCursor = new InputAction("UnlockCursor", InputActionType.Button, "<Keyboard>/escape");

            _move.Enable();
```

바꿀 코드:

```csharp
            _reload = new InputAction("Reload", InputActionType.Button, "<Keyboard>/r");
            _slot1 = new InputAction("Slot1", InputActionType.Button, "<Keyboard>/1");
            _slot2 = new InputAction("Slot2", InputActionType.Button, "<Keyboard>/2");
            // Phase 4 (D8, D11, D12): 3 = third slot, E = pick up, G = drop, 4 = Medkit, 5 = Shield Cell.
            _slot3 = new InputAction("Slot3", InputActionType.Button, "<Keyboard>/3");
            _interact = new InputAction("Interact", InputActionType.Button, "<Keyboard>/e");
            _drop = new InputAction("Drop", InputActionType.Button, "<Keyboard>/g");
            _useMedkit = new InputAction("UseMedkit", InputActionType.Button, "<Keyboard>/4");
            _useShieldCell = new InputAction("UseShieldCell", InputActionType.Button, "<Keyboard>/5");
            _unlockCursor = new InputAction("UnlockCursor", InputActionType.Button, "<Keyboard>/escape");

            _move.Enable();
```

변경 3/6 — 찾을 코드:

```csharp
            _reload.Enable();
            _slot1.Enable();
            _slot2.Enable();
            _unlockCursor.Enable();
        }
```

바꿀 코드:

```csharp
            _reload.Enable();
            _slot1.Enable();
            _slot2.Enable();
            _slot3.Enable();
            _interact.Enable();
            _drop.Enable();
            _useMedkit.Enable();
            _useShieldCell.Enable();
            _unlockCursor.Enable();
        }
```

변경 4/6 — 찾을 코드:

```csharp
        public bool AimHeld => _aim.IsPressed();
        public bool UnlockCursorPressed => _unlockCursor.WasPressedThisFrame();

        // Jump, Reload, Slot1 and Slot2 presses since the last simulation step that used them. Rendering runs
        // faster than the fixed simulation, so a press between two steps must be remembered, not lost;
        // LocalPlayerPredictor.Advance clears the bits it puts into an input.
        public InputButtons QueuedButtons { get; set; }
```

바꿀 코드:

```csharp
        public bool AimHeld => _aim.IsPressed();
        public bool UnlockCursorPressed => _unlockCursor.WasPressedThisFrame();

        // Jump, Reload, Slot1-3, Interact, Drop and the two heal presses since the last simulation step that
        // used them. Rendering runs
        // faster than the fixed simulation, so a press between two steps must be remembered, not lost;
        // LocalPlayerPredictor.Advance clears the bits it puts into an input.
        public InputButtons QueuedButtons { get; set; }
```

변경 5/6 — 찾을 코드:

```csharp
            if (_reload.WasPressedThisFrame()) QueuedButtons |= InputButtons.Reload;
            if (_slot1.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot1;
            if (_slot2.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot2;
        }

        public void Dispose()
```

바꿀 코드:

```csharp
            if (_reload.WasPressedThisFrame()) QueuedButtons |= InputButtons.Reload;
            if (_slot1.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot1;
            if (_slot2.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot2;
            if (_slot3.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot3;
            if (_interact.WasPressedThisFrame()) QueuedButtons |= InputButtons.Interact;
            if (_drop.WasPressedThisFrame()) QueuedButtons |= InputButtons.Drop;
            if (_useMedkit.WasPressedThisFrame()) QueuedButtons |= InputButtons.UseMedkit;
            if (_useShieldCell.WasPressedThisFrame()) QueuedButtons |= InputButtons.UseShieldCell;
        }

        public void Dispose()
```

변경 6/6 — 찾을 코드:

```csharp
            _reload.Dispose();
            _slot1.Dispose();
            _slot2.Dispose();
            _unlockCursor.Dispose();
        }
    }
```

바꿀 코드:

```csharp
            _reload.Dispose();
            _slot1.Dispose();
            _slot2.Dispose();
            _slot3.Dispose();
            _interact.Dispose();
            _drop.Dispose();
            _useMedkit.Dispose();
            _useShieldCell.Dispose();
            _unlockCursor.Dispose();
        }
    }
```

`Client/Assets/Scripts/Game/LocalPlayerPredictor.cs`:

변경 1/2 — 찾을 코드:

```csharp

        // Held buttons go into every step; queued presses only into a frame's last step (see Advance).
        private const InputButtons HeldButtons = InputButtons.Sprint | InputButtons.Fire;
        private const InputButtons QueuedButtons = InputButtons.Jump | InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2;

        private readonly InputCommand[] _inputs = new InputCommand[HistorySize];
        private readonly MoveState[] _results = new MoveState[HistorySize];
```

바꿀 코드:

```csharp

        // Held buttons go into every step; queued presses only into a frame's last step (see Advance).
        private const InputButtons HeldButtons = InputButtons.Sprint | InputButtons.Fire;
        private const InputButtons QueuedButtons = InputButtons.Jump | InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2 |
                                                   InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                   InputButtons.UseMedkit | InputButtons.UseShieldCell;

        private readonly InputCommand[] _inputs = new InputCommand[HistorySize];
        private readonly MoveState[] _results = new MoveState[HistorySize];
```

변경 2/2 — 찾을 코드:

```csharp
        public bool IsDead { get; private set; }

        // Returns how many simulation steps ran (each generated one input).
        // held: Sprint and Fire, applied to every step. queued: Jump, Reload, Slot1, Slot2 presses; they ride on
        // the last step, because GameClient sends one packet per frame holding only the newest
        // MaxInputsPerPacket inputs, so on a hitch frame an earlier step may never be sent. Consumed bits are cleared.
        public int Advance(float deltaTime, Vector2 move, float yaw, InputButtons held, ref InputButtons queued)
```

바꿀 코드:

```csharp
        public bool IsDead { get; private set; }

        // Returns how many simulation steps ran (each generated one input).
        // held: Sprint and Fire, applied to every step. queued: Jump, Reload, Slot1-3, Interact, Drop, UseMedkit and
        // UseShieldCell presses; they ride on
        // the last step, because GameClient sends one packet per frame holding only the newest
        // MaxInputsPerPacket inputs, so on a hitch frame an earlier step may never be sent. Consumed bits are cleared.
        public int Advance(float deltaTime, Vector2 move, float yaw, InputButtons held, ref InputButtons queued)
```

- [ ] **Step 7: `NetClient` 아이템 이벤트**

`Client/Assets/Scripts/Net/NetClient.cs`:

변경 1/2 — 찾을 코드:

```csharp
        public event Action<DamageTaken> DamageTakenReceived;
        public event Action<PlayerDied> PlayerDiedReceived;
        public event Action<PlayerRespawned> PlayerRespawnedReceived;

        public ClientState State { get; private set; } = ClientState.Disconnected;
        public string LastError { get; private set; }
```

바꿀 코드:

```csharp
        public event Action<DamageTaken> DamageTakenReceived;
        public event Action<PlayerDied> PlayerDiedReceived;
        public event Action<PlayerRespawned> PlayerRespawnedReceived;
        // Phase 4 items (D14). WorldItems chunks and ItemSpawned both arrive as ItemReceived (an upsert).
        // The item catalog is allocated once per join by its reader; the rest are structs.
        public event Action<ItemCatalogData> ItemCatalogReceived;
        public event Action<WorldItemData> ItemReceived;
        public event Action<ushort> ItemRemovedReceived;
        public event Action<InventoryState> InventoryReceived;
        public event Action<PickupResult> PickupResultReceived;

        public ClientState State { get; private set; } = ClientState.Disconnected;
        public string LastError { get; private set; }
```

변경 2/2 — 찾을 코드:

```csharp
                case PacketId.PlayerRespawned:
                    if (PlayerRespawned.TryRead(ref packet, out var respawned)) PlayerRespawnedReceived?.Invoke(respawned);
                    break;
            }
        }
```

바꿀 코드:

```csharp
                case PacketId.PlayerRespawned:
                    if (PlayerRespawned.TryRead(ref packet, out var respawned)) PlayerRespawnedReceived?.Invoke(respawned);
                    break;

                case PacketId.ItemCatalog:
                    if (ItemCatalogPacket.TryRead(ref packet, out var items)) ItemCatalogReceived?.Invoke(items);
                    break;

                case PacketId.WorldItems:
                    if (!WorldItemsPacket.TryReadHeader(ref packet, out int itemCount)) return;
                    for (int i = 0; i < itemCount; i++)
                    {
                        if (!WorldItemData.TryRead(ref packet, out var listed)) break;
                        ItemReceived?.Invoke(listed);
                    }
                    break;

                case PacketId.ItemSpawned:
                    if (ItemSpawnedPacket.TryRead(ref packet, out var item)) ItemReceived?.Invoke(item);
                    break;

                case PacketId.ItemRemoved:
                    if (ItemRemoved.TryRead(ref packet, out var removed)) ItemRemovedReceived?.Invoke(removed.ItemId);
                    break;

                case PacketId.InventoryState:
                    if (InventoryState.TryRead(ref packet, out var inventory)) InventoryReceived?.Invoke(inventory);
                    break;

                case PacketId.PickupResult:
                    if (PickupResult.TryRead(ref packet, out var pickup)) PickupResultReceived?.Invoke(pickup);
                    break;
            }
        }
```

- [ ] **Step 8: `GameClient`를 새 `WeaponState`에 맞춘다**

뷰·HUD 연결은 Task 10에서 한다. 여기서는 컴파일을 유지하고, 인벤토리를 `WeaponState`에 넣고, 부활 때 빈손으로 되돌린다(서버가 바로 뒤에 `InventoryState`를 보낸다).

`Client/Assets/Scripts/Game/GameClient.cs`:

변경 1/6 — 찾을 코드:

```csharp
            _net.DamageTakenReceived += OnDamageTaken;
            _net.PlayerDiedReceived += OnPlayerDied;
            _net.PlayerRespawnedReceived += OnPlayerRespawned;
        }

        private void Update()
```

바꿀 코드:

```csharp
            _net.DamageTakenReceived += OnDamageTaken;
            _net.PlayerDiedReceived += OnPlayerDied;
            _net.PlayerRespawnedReceived += OnPlayerRespawned;
            _net.InventoryReceived += OnInventory;
        }

        private void Update()
```

변경 2/6 — 찾을 코드:

```csharp
            _fireEffects.Tick(now);

            _hud.SetVitals(_health, _shield);
            if (_weapons != null) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Current.MagazineSize, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);
        }
```

바꿀 코드:

```csharp
            _fireEffects.Tick(now);

            _hud.SetVitals(_health, _shield);
            if (_weapons != null && _weapons.HasWeapon) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Current.MagazineSize, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);
        }
```

변경 3/6 — 찾을 코드:

```csharp
            _net.DamageTakenReceived -= OnDamageTaken;
            _net.PlayerDiedReceived -= OnPlayerDied;
            _net.PlayerRespawnedReceived -= OnPlayerRespawned;
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
```

바꿀 코드:

```csharp
            _net.DamageTakenReceived -= OnDamageTaken;
            _net.PlayerDiedReceived -= OnPlayerDied;
            _net.PlayerRespawnedReceived -= OnPlayerRespawned;
            _net.InventoryReceived -= OnInventory;
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
```

변경 4/6 — 찾을 코드:

```csharp
        // point can be on a player; the server then shoots from our eye towards it (D2).
        private Vector3 FindAimPoint()
        {
            float range = _weapons != null ? _weapons.Current.Range : DefaultAimRange;
            Ray ray = _camera.AimRay;
            // Remote views moved in Update. Physics.autoSyncTransforms is off by default, so without this the
            // ray would test their colliders where the last physics step left them.
```

바꿀 코드:

```csharp
        // point can be on a player; the server then shoots from our eye towards it (D2).
        private Vector3 FindAimPoint()
        {
            float range = _weapons != null && _weapons.HasWeapon ? _weapons.Current.Range : DefaultAimRange;
            Ray ray = _camera.AimRay;
            // Remote views moved in Update. Physics.autoSyncTransforms is off by default, so without this the
            // ray would test their colliders where the last physics step left them.
```

변경 5/6 — 찾을 코드:

```csharp
            _weapons = new WeaponState(weapons);
        }

        private void OnSpawned(PlayerSpawned spawned)
        {
            if (spawned.EntityId == MyEntityId)
```

바꿀 코드:

```csharp
            _weapons = new WeaponState(weapons);
        }

        // Phase 4: what sits in each slot and the reserves (D14). The current slot comes with snapshots.
        private void OnInventory(InventoryState inventory)
        {
            if (_weapons != null) _weapons.ApplyInventory(inventory);
        }

        private void OnSpawned(PlayerSpawned spawned)
        {
            if (spawned.EntityId == MyEntityId)
```

변경 6/6 — 찾을 코드:

```csharp
            if (respawned.EntityId != MyEntityId || _predictor == null) return;
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw });
            _input.QueuedButtons = InputButtons.None;
            if (_weapons != null) _weapons.Refill();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, true);
            _hud.HideDeath();
        }
```

바꿀 코드:

```csharp
            if (respawned.EntityId != MyEntityId || _predictor == null) return;
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw });
            _input.QueuedButtons = InputButtons.None;
            // Empty until the server's InventoryState for the new life arrives (sent right after this event).
            if (_weapons != null) _weapons.Clear();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, true);
            _hud.HideDeath();
        }
```

- [ ] **Step 9: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 87개 PASS

- [ ] **Step 10: Unity Editor import 확인 (Task 1–9)**

사용자에게 Unity Editor를 한 번 포커스해 자동 import·컴파일이 끝나게 해 달라고 요청한 뒤, Task 1 Step 0의 `N0` 이후만 본다.

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; tail -n +N0 "$LOG" | grep -n "error CS"; wc -l < "$LOG"
```

Expected: `error CS` 출력 없음. 출력이 있으면 그 줄 뒤에 오류 없는 컴파일(`error CS` 없이 끝난 다음 스크립트 컴파일)이 있는지 보고, 마지막 컴파일에 오류가 없으면 통과로 본다. `Shared/Runtime/Protocol/ItemPackets.cs.meta`, `Shared/Runtime/Simulation/LootPoints.cs.meta`, 새 Client 스크립트·테스트의 `.meta`가 생겼는지 `git status`로 확인한다(직접 만들지 않는다). 마지막 줄 수를 `N9`로 기록한다. (Editor가 이전 버전으로 Play 중이면 새 서버와 ProtocolVersion이 달라 접속이 거절된다. Play를 멈추고 확인한다.)

- [ ] **Step 11: 체크포인트**

결과와 `N9`를 기록한다. 커밋하지 않는다.

---

### Task 10: Client 아이템 뷰·안내·인벤토리 HUD·전체 연결 (`WorldItemViews`, `InventoryHud`, `GameClient`)

**Files:**
- Create: `Client/Assets/Scripts/Game/InventoryHudText.cs`, `Client/Assets/Scripts/Game/InventoryHud.cs`, `Client/Assets/Scripts/Game/WorldItemViews.cs`
- Modify: `Client/Assets/Scripts/Game/GameClient.cs`, `Client/Assets/Scripts/Game/CombatHud.cs`, `Client/Assets/Scripts/Net/VectorConversions.cs`, `Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs`
- Test: `Client/Assets/Tests/EditMode/InventoryHudTextTests.cs` (신규)
- Modify (저장소 밖): 스크래치 `PredictorTests.csproj`

**Interfaces:**
- Consumes: Task 9의 `WorldItemList`, `PickupRule`, `WeaponState.TryGetSlot/GetReserve/Slot/HasWeapon/Reserve`, `NetClient` 아이템 이벤트; Task 1의 `ItemCatalogData`, `InventoryState`, `PickupResult`
- Produces:
  - `InventoryHudText { int Rebuilds; string Slot(int); string Consumables; string Prompt; bool SetSlot(int slot, bool selected, string weapon, string rarity, int ammo, int reserve); bool SetConsumables(int medkits, int shieldCells); bool SetPrompt(ushort itemId, ushort amount, string name, string rarity) }` (순수, 값이 바뀔 때만 문자열을 만든다)
  - `InventoryHud : IDisposable { static Color[] RarityColors; SetVisible(bool); SetSlot(int, bool, string, string, int rarity, int ammo, int reserve); SetConsumables(int, int); SetPrompt(ushort, ushort, string, string); SetUseProgress(float progress /* <0 숨김 */); ShowNotice(string message, float now); Tick(float now) }`
  - `WorldItemViews : IDisposable { WorldItemList Items; Upsert(in WorldItemData); Remove(ushort); Clear(); Tick(float time) }` — 최대 256개 풀, 공유 Material 8개, 내장 Mesh 3개, Collider 없음
  - `VectorConversions.ToNumerics(this UnityEngine.Vector3)`; `CombatHud.SetWeapon(string name, int ammo, int reserve, bool reloading)` (탄창 / 보유량)

- [ ] **Step 1: 실패하는 테스트 작성**

`Client/Assets/Tests/EditMode/InventoryHudTextTests.cs`를 만든다. 같은 값이면 다시 만들지 않고(같은 문자열 인스턴스), 바뀌면 만든다.

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    // D15 / Client Hot Path: the HUD calls every Set* each frame; strings may only be built when a value
    // changed, so an unchanged HUD allocates nothing.
    public class InventoryHudTextTests
    {
        private const string Vesper = "Vesper AR";
        private const string Rare = "Rare";

        [Test]
        public void Slot_BuildsOnce_ForTheSameValues()
        {
            var text = new InventoryHudText();
            Assert.IsTrue(text.SetSlot(0, true, Vesper, Rare, 30, 120));
            Assert.AreEqual("> 1  Vesper AR [Rare]  30 / 120", text.Slot(0));
            string built = text.Slot(0);
            int rebuilds = text.Rebuilds;

            for (int frame = 0; frame < 100; frame++) Assert.IsFalse(text.SetSlot(0, true, Vesper, Rare, 30, 120));
            Assert.AreEqual(rebuilds, text.Rebuilds);
            Assert.AreSame(built, text.Slot(0));
        }

        [Test]
        public void Slot_RebuildsOnEachChange()
        {
            var text = new InventoryHudText();
            text.SetSlot(1, false, Vesper, Rare, 30, 120);
            Assert.IsTrue(text.SetSlot(1, false, Vesper, Rare, 29, 120));   // a shot
            Assert.IsTrue(text.SetSlot(1, true, Vesper, Rare, 29, 120));    // selected
            Assert.IsTrue(text.SetSlot(1, true, null, null, 0, 0));         // dropped
            Assert.AreEqual("> 2  -", text.Slot(1));
            Assert.AreEqual(4, text.Rebuilds);
        }

        [Test]
        public void Consumables_BuildOnlyOnChange()
        {
            var text = new InventoryHudText();
            Assert.IsTrue(text.SetConsumables(2, 3));
            Assert.AreEqual("[4] Medkit x2    [5] Shield Cell x3", text.Consumables);
            Assert.IsFalse(text.SetConsumables(2, 3));
            Assert.IsTrue(text.SetConsumables(1, 3));
            Assert.AreEqual(2, text.Rebuilds);
        }

        [Test]
        public void Prompt_FollowsTheTargetAndItsAmount()
        {
            var text = new InventoryHudText();
            Assert.IsFalse(text.SetPrompt(0, 0, null, null));   // nothing in reach and nothing shown yet
            Assert.AreEqual(string.Empty, text.Prompt);

            Assert.IsTrue(text.SetPrompt(7, 30, Vesper, Rare));
            Assert.AreEqual("[E] Pick up Vesper AR [Rare]", text.Prompt);
            Assert.IsFalse(text.SetPrompt(7, 30, Vesper, Rare));

            Assert.IsTrue(text.SetPrompt(9, 60, "Light Rounds", null));
            Assert.AreEqual("[E] Pick up Light Rounds x60", text.Prompt);
            Assert.IsTrue(text.SetPrompt(9, 12, "Light Rounds", null));   // partial pickup left 12
            Assert.AreEqual("[E] Pick up Light Rounds x12", text.Prompt);

            Assert.IsTrue(text.SetPrompt(0, 0, null, null));
            Assert.AreEqual(string.Empty, text.Prompt);
        }
    }
}
```

스크래치 `PredictorTests.csproj`의 `<Compile Include="E:/popol/ProjectH/Shared/Runtime/**/*.cs" />` 줄 바로 앞에 다음 줄을 넣는다.

```xml
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/InventoryHudText.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/InventoryHudText.cs')" />
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 빌드 실패 — `InventoryHudText`가 없다는 오류

- [ ] **Step 3: `InventoryHudText`**

`Client/Assets/Scripts/Game/InventoryHudText.cs`를 만든다. 이름은 카탈로그에서 받은 문자열이라 참조로 비교한다. 캡처 람다를 받지 않는다(매 프레임 호출되므로).

```csharp
namespace ProjectH.Client.Game
{
    // The strings of the inventory HUD and the pickup prompt (D15), built only when a shown value changes, so
    // a HUD that shows the same thing every frame allocates nothing. Pure (no UnityEngine): InventoryHud puts
    // the strings on screen when a Set* call returns true. Names are the catalog's strings (received once per
    // join), compared by reference.
    public sealed class InventoryHudText
    {
        private struct SlotValues
        {
            public bool Selected;
            public string Weapon;   // null = empty slot
            public string Rarity;
            public int Ammo;
            public int Reserve;
        }

        private readonly SlotValues[] _slots = new SlotValues[WeaponState.SlotCount];
        private readonly string[] _slotText = new string[WeaponState.SlotCount];
        private int _medkits = -1;
        private int _shieldCells = -1;
        private ushort _promptItem;
        private ushort _promptAmount;

        public InventoryHudText()
        {
            for (int i = 0; i < _slotText.Length; i++) _slotText[i] = string.Empty;
            // Force the first Set of each slot to build its text.
            for (int i = 0; i < _slots.Length; i++) _slots[i].Ammo = -1;
        }

        // How many strings were built so far (tests check that unchanged values build nothing).
        public int Rebuilds { get; private set; }
        public string Consumables { get; private set; } = string.Empty;
        public string Prompt { get; private set; } = string.Empty;

        public string Slot(int slot) => _slotText[slot];

        // "> 1  Vesper AR [Rare]  30 / 120" for the slot in hand, "  3  -" for an empty one.
        public bool SetSlot(int slot, bool selected, string weapon, string rarity, int ammo, int reserve)
        {
            ref SlotValues v = ref _slots[slot];
            if (v.Selected == selected && ReferenceEquals(v.Weapon, weapon) && ReferenceEquals(v.Rarity, rarity) &&
                v.Ammo == ammo && v.Reserve == reserve)
                return false;
            v.Selected = selected;
            v.Weapon = weapon;
            v.Rarity = rarity;
            v.Ammo = ammo;
            v.Reserve = reserve;
            string mark = selected ? "> " : "  ";
            _slotText[slot] = weapon == null
                ? mark + (slot + 1) + "  -"
                : mark + (slot + 1) + "  " + weapon + " [" + rarity + "]  " + ammo + " / " + reserve;
            Rebuilds++;
            return true;
        }

        public bool SetConsumables(int medkits, int shieldCells)
        {
            if (medkits == _medkits && shieldCells == _shieldCells) return false;
            _medkits = medkits;
            _shieldCells = shieldCells;
            Consumables = "[4] Medkit x" + medkits + "    [5] Shield Cell x" + shieldCells;
            Rebuilds++;
            return true;
        }

        // itemId 0 = nothing in reach. rarity null = a stack (ammo, heal), shown with its amount:
        // "[E] Pick up Vesper AR [Rare]", "[E] Pick up Light Rounds x60". The text changes with the target
        // or its amount (a partial pickup leaves a smaller stack).
        public bool SetPrompt(ushort itemId, ushort amount, string name, string rarity)
        {
            if (itemId == _promptItem && amount == _promptAmount) return false;
            _promptItem = itemId;
            _promptAmount = amount;
            if (itemId == 0) Prompt = string.Empty;
            else if (rarity != null) Prompt = "[E] Pick up " + name + " [" + rarity + "]";
            else Prompt = "[E] Pick up " + name + " x" + amount;
            Rebuilds++;
            return true;
        }
    }
}
```

- [ ] **Step 4: `InventoryHud`**

`Client/Assets/Scripts/Game/InventoryHud.cs`를 만든다. `CombatHud`와 같은 방식(코드로 만든 Canvas, `LegacyRuntime.ttf`, raycast target 없음)이다.

```csharp
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // D15: inventory HUD and pickup prompt on one Screen Space Overlay canvas built in code (UGUI legacy Text
    // with the built-in font, like CombatHud). No GraphicRaycaster; nothing is a raycast target. Text is set
    // only when InventoryHudText rebuilt a string, so an unchanged HUD allocates nothing per frame; the heal
    // bar changes only a RectTransform size. Dispose destroys the canvas.
    public sealed class InventoryHud : System.IDisposable
    {
        private const int FontSize = 18;
        private const float BarWidth = 200f;
        private const float NoticeSeconds = 1.5f;

        // Rarity colors (D15): Common, Uncommon, Rare, Epic, Legendary. Shared with WorldItemViews.
        public static readonly Color[] RarityColors =
        {
            new Color(0.8f, 0.8f, 0.8f),
            new Color(0.35f, 0.85f, 0.35f),
            new Color(0.3f, 0.55f, 1f),
            new Color(0.7f, 0.35f, 0.95f),
            new Color(1f, 0.65f, 0.15f),
        };

        private readonly InventoryHudText _text = new InventoryHudText();
        private readonly GameObject _root;
        private readonly Text[] _slots = new Text[WeaponState.SlotCount];
        private readonly Text _consumables;
        private readonly Text _prompt;
        private readonly Text _notice;
        private readonly GameObject _bar;
        private readonly RectTransform _barFill;
        private bool _visible;
        private float _noticeHideTime = -1f;

        public InventoryHud()
        {
            _root = new GameObject("InventoryHud");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 91;   // above CombatHud (90), under the crosshair (100)

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = CreateText("Slot" + (i + 1), font, new Vector2(0.5f, 0f), new Vector2(0f, 88f - 22f * i), TextAnchor.LowerCenter);
            _consumables = CreateText("Consumables", font, new Vector2(0.5f, 0f), new Vector2(0f, 20f), TextAnchor.LowerCenter);
            _prompt = CreateText("Prompt", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -120f), TextAnchor.MiddleCenter);
            _notice = CreateText("Notice", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -145f), TextAnchor.MiddleCenter);
            _notice.color = new Color(1f, 0.8f, 0.3f);

            // Heal channel bar (D11): a dark frame and a fill whose width is the progress.
            _bar = new GameObject("UseBar", typeof(RectTransform));
            var barRect = (RectTransform)_bar.transform;
            barRect.SetParent(_root.transform, false);
            barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0.5f);
            barRect.anchoredPosition = new Vector2(0f, -60f);
            barRect.sizeDelta = new Vector2(BarWidth + 4f, 12f);
            var frame = _bar.AddComponent<Image>();
            frame.color = new Color(0f, 0f, 0f, 0.6f);
            frame.raycastTarget = false;
            var fill = new GameObject("Fill", typeof(RectTransform));
            _barFill = (RectTransform)fill.transform;
            _barFill.SetParent(barRect, false);
            _barFill.anchorMin = _barFill.anchorMax = _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.anchoredPosition = new Vector2(2f, 0f);
            _barFill.sizeDelta = new Vector2(0f, 8f);
            var fillImage = fill.AddComponent<Image>();
            fillImage.color = new Color(0.4f, 0.9f, 0.5f);
            fillImage.raycastTarget = false;
            _bar.SetActive(false);

            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void SetSlot(int slot, bool selected, string weapon, string rarityName, int rarity, int ammo, int reserve)
        {
            if (_root == null || !_text.SetSlot(slot, selected, weapon, rarityName, ammo, reserve)) return;
            _slots[slot].text = _text.Slot(slot);
            _slots[slot].color = weapon == null ? new Color(1f, 1f, 1f, 0.5f) : RarityColors[rarity];
        }

        public void SetConsumables(int medkits, int shieldCells)
        {
            if (_root == null || !_text.SetConsumables(medkits, shieldCells)) return;
            _consumables.text = _text.Consumables;
        }

        public void SetPrompt(ushort itemId, ushort amount, string name, string rarity)
        {
            if (_root == null || !_text.SetPrompt(itemId, amount, name, rarity)) return;
            _prompt.text = _text.Prompt;
        }

        // progress < 0 hides the bar.
        public void SetUseProgress(float progress)
        {
            if (_root == null) return;
            bool show = progress >= 0f;
            if (_bar.activeSelf != show) _bar.SetActive(show);
            if (show) _barFill.sizeDelta = new Vector2(BarWidth * Mathf.Clamp01(progress), 8f);
        }

        // PickupResult feedback. message must be a constant string (no per-call allocation).
        public void ShowNotice(string message, float now)
        {
            if (_root == null) return;
            _notice.text = message;
            _noticeHideTime = now + NoticeSeconds;
        }

        public void Tick(float now)
        {
            if (_root == null || _noticeHideTime < 0f || now < _noticeHideTime) return;
            _noticeHideTime = -1f;
            _notice.text = string.Empty;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        private Text CreateText(string name, Font font, Vector2 anchor, Vector2 offset, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root.transform, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(520f, 24f);

            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = FontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.text = string.Empty;
            return text;
        }
    }
}
```

- [ ] **Step 5: `WorldItemViews`**

`Client/Assets/Scripts/Game/WorldItemViews.cs`를 만든다. 뷰 배열은 `WorldItemList`의 인덱스와 나란히 움직인다(`Remove`의 `movedFrom`). 임시 Primitive는 Collider가 첫 프레임에 남지 않게 `DestroyImmediate`로 지운다.

```csharp
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // D15: every world item as a small spinning shape: weapon = cube in its rarity color, ammo = cylinder,
    // Medkit / Shield Cell = sphere. Holds the client's item list (WorldItemList) and one view per entry,
    // in the same index order. Views come from a pool that never exceeds WorldItemList.Capacity (256) and
    // is reused, never destroyed, until Dispose; all views share 8 materials and 3 built-in meshes
    // (sharedMaterial / sharedMesh only). No colliders: items never block the camera or the aim ray.
    public sealed class WorldItemViews : System.IDisposable
    {
        private const float Hover = 0.3f;
        private const float SpinDegreesPerSecond = 90f;

        private readonly WorldItemList _items = new WorldItemList();
        private readonly Transform[] _views = new Transform[WorldItemList.Capacity];   // parallel to _items
        private readonly Transform[] _free = new Transform[WorldItemList.Capacity];
        private readonly GameObject _root;
        private readonly Mesh _cube;
        private readonly Mesh _cylinder;
        private readonly Mesh _sphere;
        private readonly Material[] _rarityMaterials = new Material[ItemConstants.RarityCount];
        private readonly Material _ammoMaterial;
        private readonly Material _medkitMaterial;
        private readonly Material _shieldCellMaterial;
        private int _freeCount;
        private int _created;

        public WorldItemViews()
        {
            _root = new GameObject("WorldItems");
            Material template = null;
            _cube = BuiltinMesh(PrimitiveType.Cube, ref template);
            _cylinder = BuiltinMesh(PrimitiveType.Cylinder, ref template);
            _sphere = BuiltinMesh(PrimitiveType.Sphere, ref template);
            for (int i = 0; i < _rarityMaterials.Length; i++) _rarityMaterials[i] = new Material(template) { color = InventoryHud.RarityColors[i] };
            _ammoMaterial = new Material(template) { color = new Color(0.95f, 0.85f, 0.3f) };
            _medkitMaterial = new Material(template) { color = new Color(0.95f, 0.3f, 0.3f) };
            _shieldCellMaterial = new Material(template) { color = new Color(0.3f, 0.85f, 1f) };
        }

        public WorldItemList Items => _items;

        // WorldItems at join and ItemSpawned (a new item or an amount change).
        public void Upsert(in WorldItemData item)
        {
            if (!_items.Upsert(item, out int index, out bool added)) return;
            if (added)
            {
                Transform view = Rent();
                if (view == null) return;   // not reached: the list and the pool have the same capacity
                _views[index] = view;
                Dress(view, item);
            }
            _views[index].localPosition = item.Position.ToUnity() + new Vector3(0f, Hover, 0f);
        }

        public void Remove(ushort itemId)
        {
            if (!_items.Remove(itemId, out int removed, out int movedFrom)) return;
            Return(_views[removed]);
            _views[removed] = null;
            if (movedFrom >= 0)
            {
                _views[removed] = _views[movedFrom];
                _views[movedFrom] = null;
            }
        }

        // Disconnect: the next join sends the whole list again.
        public void Clear()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Return(_views[i]);
                _views[i] = null;
            }
            _items.Clear();
        }

        // Once per frame: one rotation for all (no per-item state, no allocation).
        public void Tick(float time)
        {
            Quaternion spin = Quaternion.Euler(0f, time * SpinDegreesPerSecond, 0f);
            for (int i = 0; i < _items.Count; i++) _views[i].localRotation = spin;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);   // every pooled view is its child
            foreach (Material m in _rarityMaterials) if (m != null) Object.Destroy(m);
            if (_ammoMaterial != null) Object.Destroy(_ammoMaterial);
            if (_medkitMaterial != null) Object.Destroy(_medkitMaterial);
            if (_shieldCellMaterial != null) Object.Destroy(_shieldCellMaterial);
        }

        private void Dress(Transform view, in WorldItemData item)
        {
            var filter = view.GetComponent<MeshFilter>();
            var renderer = view.GetComponent<MeshRenderer>();
            switch (item.Kind)
            {
                case ItemKind.Weapon:
                    filter.sharedMesh = _cube;
                    renderer.sharedMaterial = _rarityMaterials[item.Rarity < _rarityMaterials.Length ? item.Rarity : 0];
                    view.localScale = new Vector3(0.6f, 0.2f, 0.2f);
                    break;
                case ItemKind.Ammo:
                    filter.sharedMesh = _cylinder;
                    renderer.sharedMaterial = _ammoMaterial;
                    view.localScale = new Vector3(0.25f, 0.15f, 0.25f);
                    break;
                default:
                    filter.sharedMesh = _sphere;
                    renderer.sharedMaterial = item.DefId == (byte)ConsumableType.Medkit ? _medkitMaterial : _shieldCellMaterial;
                    view.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                    break;
            }
        }

        private Transform Rent()
        {
            Transform view;
            if (_freeCount > 0)
            {
                view = _free[--_freeCount];
            }
            else
            {
                if (_created == WorldItemList.Capacity) return null;
                var go = new GameObject("Item", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(_root.transform, false);
                view = go.transform;
                _created++;
            }
            view.gameObject.SetActive(true);
            return view;
        }

        private void Return(Transform view)
        {
            if (view == null) return;
            view.gameObject.SetActive(false);
            _free[_freeCount++] = view;
        }

        // The primitive's mesh is a built-in asset that outlives the temporary object; its material is the
        // template (its shader is guaranteed to be in the build).
        private static Mesh BuiltinMesh(PrimitiveType type, ref Material template)
        {
            var go = GameObject.CreatePrimitive(type);
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            if (template == null) template = go.GetComponent<Renderer>().sharedMaterial;
            // Immediate: a deferred destroy would leave its collider in the world for the first frame.
            Object.DestroyImmediate(go);
            return mesh;
        }
    }
}
```

- [ ] **Step 6: 작은 변경 — 벡터 변환, 무기 표시, 안내 문구**

`Client/Assets/Scripts/Net/VectorConversions.cs`:

변경 — 찾을 코드:

```csharp
        {
            return new UnityEngine.Vector3(value.X, value.Y, value.Z);
        }
    }
}
```

바꿀 코드:

```csharp
        {
            return new UnityEngine.Vector3(value.X, value.Y, value.Z);
        }

        public static System.Numerics.Vector3 ToNumerics(this UnityEngine.Vector3 value)
        {
            return new System.Numerics.Vector3(value.x, value.y, value.z);
        }
    }
}
```

`Client/Assets/Scripts/Game/CombatHud.cs` (탄창 / 보유량):

변경 1/2 — 찾을 코드:

```csharp
        private int _shield = -1;
        private string _weaponName;
        private int _ammo = -1;
        private int _magazine = -1;
        private bool _reloading;
        private float _hitHideTime;
        private float _damageHideTime;
```

바꿀 코드:

```csharp
        private int _shield = -1;
        private string _weaponName;
        private int _ammo = -1;
        private int _reserve = -1;
        private bool _reloading;
        private float _hitHideTime;
        private float _damageHideTime;
```

변경 2/2 — 찾을 코드:

```csharp
            _vitals.text = "HP " + health + "   SH " + shield;
        }

        public void SetWeapon(string name, int ammo, int magazine, bool reloading)
        {
            if (_root == null) return;
            if (ReferenceEquals(name, _weaponName) && ammo == _ammo && magazine == _magazine && reloading == _reloading) return;
            _weaponName = name;
            _ammo = ammo;
            _magazine = magazine;
            _reloading = reloading;
            _weapon.text = reloading ? name + "   reloading..." : name + "   " + ammo + " / " + magazine;
        }

        public void ClearWeapon()
```

바꿀 코드:

```csharp
            _vitals.text = "HP " + health + "   SH " + shield;
        }

        // Phase 4: magazine / reserve rounds of the weapon's ammo type.
        public void SetWeapon(string name, int ammo, int reserve, bool reloading)
        {
            if (_root == null) return;
            if (ReferenceEquals(name, _weaponName) && ammo == _ammo && reserve == _reserve && reloading == _reloading) return;
            _weaponName = name;
            _ammo = ammo;
            _reserve = reserve;
            _reloading = reloading;
            _weapon.text = reloading ? name + "   reloading..." : name + "   " + ammo + " / " + reserve;
        }

        public void ClearWeapon()
```

`Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs`:

변경 — 찾을 코드:

```csharp
                _client.Disconnect();
            }

            GUILayout.Label("F1: panel   Left click: lock mouse, then fire   Right click: aim   R: reload   1/2: weapon   Esc: unlock");
            GUILayout.EndArea();
        }
```

바꿀 코드:

```csharp
                _client.Disconnect();
            }

            GUILayout.Label("F1: panel   Left click: lock mouse, then fire   Right click: aim   R: reload   1/2/3: weapon   Esc: unlock");
            GUILayout.Label("E: pick up   G: drop weapon   4: Medkit   5: Shield Cell");
            GUILayout.EndArea();
        }
```

- [ ] **Step 7: `GameClient` 전체 연결**

생성(`Awake`) → 이벤트 구독 → 프레임마다 아이템 회전·인벤토리 HUD·"[E]" 안내(예측 위치 `PredictedPosition` 기준 `PickupRule`, 죽어 있으면 숨김) → 해제(`OnDestroy` 역순, `ClearMatchState`에서 목록·카탈로그·마지막 인벤토리 비우기). 회복 막대는 `InventoryState.UseRemainingTicks`를 받은 순간부터 로컬 시간으로 센다(결정은 서버).

`Client/Assets/Scripts/Game/GameClient.cs`:

변경 1/9 — 찾을 코드:

```csharp
        private ShoulderCamera _camera;
        private Crosshair _crosshair;
        private CombatHud _hud;
        private LocalFireEffects _fireEffects;
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
```

바꿀 코드:

```csharp
        private ShoulderCamera _camera;
        private Crosshair _crosshair;
        private CombatHud _hud;
        private InventoryHud _inventoryHud;
        private WorldItemViews _worldItems;
        private LocalFireEffects _fireEffects;
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
```

변경 2/9 — 찾을 코드:

```csharp
        private readonly RemotePlayers _remotePlayers = new RemotePlayers();
        private ServerClock _clock;
        private WeaponState _weapons;
        private int _simHz;
        private double _interpolationDelaySeconds;
        private double _renderTick;       // server tick remote players are drawn at this frame (ViewTick, D6)
```

바꿀 코드:

```csharp
        private readonly RemotePlayers _remotePlayers = new RemotePlayers();
        private ServerClock _clock;
        private WeaponState _weapons;
        private WeaponInfo[] _weaponCatalog;
        private ItemCatalogData _itemCatalog;
        private InventoryState _inventory;   // the newest server InventoryState (heals and the use channel)
        private float _useEndTime;           // local time the running heal ends (from UseRemainingTicks)
        private float _useSeconds;           // its whole channel time
        private int _simHz;
        private double _interpolationDelaySeconds;
        private double _renderTick;       // server tick remote players are drawn at this frame (ViewTick, D6)
```

변경 3/9 — 찾을 코드:

```csharp
            _camera = new ShoulderCamera(main);
            _crosshair = new Crosshair();
            _hud = new CombatHud();
            _fireEffects = new LocalFireEffects();

            _net = new NetClient();
```

바꿀 코드:

```csharp
            _camera = new ShoulderCamera(main);
            _crosshair = new Crosshair();
            _hud = new CombatHud();
            _inventoryHud = new InventoryHud();
            _worldItems = new WorldItemViews();
            _fireEffects = new LocalFireEffects();

            _net = new NetClient();
```

변경 4/9 — 찾을 코드:

```csharp
            _net.PlayerDiedReceived += OnPlayerDied;
            _net.PlayerRespawnedReceived += OnPlayerRespawned;
            _net.InventoryReceived += OnInventory;
        }

        private void Update()
```

바꿀 코드:

```csharp
            _net.PlayerDiedReceived += OnPlayerDied;
            _net.PlayerRespawnedReceived += OnPlayerRespawned;
            _net.InventoryReceived += OnInventory;
            _net.ItemCatalogReceived += OnItemCatalog;
            _net.ItemReceived += OnItem;
            _net.ItemRemovedReceived += OnItemRemoved;
            _net.PickupResultReceived += OnPickupResult;
        }

        private void Update()
```

변경 5/9 — 찾을 코드:

```csharp
            _fireEffects.Tick(now);

            _hud.SetVitals(_health, _shield);
            if (_weapons != null && _weapons.HasWeapon) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Current.MagazineSize, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);
        }

        private void OnDestroy()
```

바꿀 코드:

```csharp
            _fireEffects.Tick(now);

            _hud.SetVitals(_health, _shield);
            if (_weapons != null && _weapons.HasWeapon) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Reserve, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);

            _worldItems.Tick(now);
            UpdateInventoryHud(alive, now);
        }

        // D15: slots, heals, the heal bar and the "[E]" prompt. Strings are rebuilt only on change (InventoryHudText).
        private void UpdateInventoryHud(bool alive, float now)
        {
            _inventoryHud.Tick(now);
            if (_weapons == null || _itemCatalog == null) return;

            for (int i = 0; i < WeaponState.SlotCount; i++)
            {
                if (_weapons.TryGetSlot(i, out WeaponInfo weapon, out int rarity, out int ammo))
                    _inventoryHud.SetSlot(i, i == _weapons.Slot, weapon.Name, _itemCatalog.Rarities[rarity].Name, rarity, ammo, _weapons.GetReserve(weapon.AmmoType));
                else
                    _inventoryHud.SetSlot(i, i == _weapons.Slot, null, null, 0, 0, 0);
            }
            _inventoryHud.SetConsumables(_inventory.Medkits, _inventory.ShieldCells);

            bool channel = alive && _inventory.Using != ConsumableType.None && _useSeconds > 0f;
            _inventoryHud.SetUseProgress(channel ? 1f - Mathf.Clamp01((_useEndTime - now) / _useSeconds) : -1f);

            // Same rule as the server (PickupRule): the prompt names the item E will take.
            int target = alive ? PickupRule.FindNearest(_worldItems.Items, _predictor.PredictedPosition.ToNumerics()) : -1;
            if (target < 0)
            {
                _inventoryHud.SetPrompt(0, 0, null, null);
                return;
            }
            WorldItemData item = _worldItems.Items[target];
            switch (item.Kind)
            {
                case ItemKind.Weapon:
                    _inventoryHud.SetPrompt(item.ItemId, item.Amount, WeaponName(item.DefId), _itemCatalog.Rarities[item.Rarity].Name);
                    break;
                case ItemKind.Ammo:
                    _inventoryHud.SetPrompt(item.ItemId, item.Amount, _itemCatalog.Ammo[item.DefId - 1].Name, null);
                    break;
                default:
                    _inventoryHud.SetPrompt(item.ItemId, item.Amount, _itemCatalog.Consumables[item.DefId - 1].Name, null);
                    break;
            }
        }

        private string WeaponName(byte weaponId)
        {
            for (int i = 0; i < _weaponCatalog.Length; i++)
            {
                if (_weaponCatalog[i].WeaponId == weaponId) return _weaponCatalog[i].Name;
            }
            return "?";
        }

        private void OnDestroy()
```

변경 6/9 — 찾을 코드:

```csharp
            _net.PlayerDiedReceived -= OnPlayerDied;
            _net.PlayerRespawnedReceived -= OnPlayerRespawned;
            _net.InventoryReceived -= OnInventory;
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
            _hud.Dispose();
            _crosshair.Dispose();
            _input.Dispose();
```

바꿀 코드:

```csharp
            _net.PlayerDiedReceived -= OnPlayerDied;
            _net.PlayerRespawnedReceived -= OnPlayerRespawned;
            _net.InventoryReceived -= OnInventory;
            _net.ItemCatalogReceived -= OnItemCatalog;
            _net.ItemReceived -= OnItem;
            _net.ItemRemovedReceived -= OnItemRemoved;
            _net.PickupResultReceived -= OnPickupResult;
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
            _worldItems.Dispose();
            _inventoryHud.Dispose();
            _hud.Dispose();
            _crosshair.Dispose();
            _input.Dispose();
```

변경 7/9 — 찾을 코드:

```csharp

        private void OnCatalog(WeaponInfo[] weapons)
        {
            _weapons = new WeaponState(weapons);
        }

        // Phase 4: what sits in each slot and the reserves (D14). The current slot comes with snapshots.
        private void OnInventory(InventoryState inventory)
        {
            if (_weapons != null) _weapons.ApplyInventory(inventory);
        }

        private void OnSpawned(PlayerSpawned spawned)
```

바꿀 코드:

```csharp

        private void OnCatalog(WeaponInfo[] weapons)
        {
            _weaponCatalog = weapons;
            _weapons = new WeaponState(weapons);
        }

        private void OnItemCatalog(ItemCatalogData items)
        {
            _itemCatalog = items;
        }

        private void OnItem(WorldItemData item)
        {
            _worldItems.Upsert(item);
        }

        private void OnItemRemoved(ushort itemId)
        {
            _worldItems.Remove(itemId);
        }

        // Feedback only (the inventory changes through InventoryState). Constant strings: no allocation.
        private void OnPickupResult(PickupResult result)
        {
            if (result.Result == PickupResultCode.Full) _inventoryHud.ShowNotice("Inventory full", Time.time);
            else if (result.Result == PickupResultCode.NothingInRange) _inventoryHud.ShowNotice("Nothing to pick up", Time.time);
        }

        // Phase 4: what sits in each slot, the reserves, heals and the heal channel (D14). The current slot
        // comes with snapshots. The channel is shown from the remaining ticks at arrival; the server decides it.
        private void OnInventory(InventoryState inventory)
        {
            _inventory = inventory;
            if (_weapons != null) _weapons.ApplyInventory(inventory);
            _useSeconds = 0f;
            if (inventory.Using != ConsumableType.None && _itemCatalog != null && _simHz > 0)
            {
                _useSeconds = (float)_itemCatalog.Consumables[(int)inventory.Using - 1].UseTicks / _simHz;
                _useEndTime = Time.time + (float)inventory.UseRemainingTicks / _simHz;
            }
        }

        private void OnSpawned(PlayerSpawned spawned)
```

변경 8/9 — 찾을 코드:

```csharp
                _health = 0;
                _shield = 0;
                _hud.SetVisible(true);
                return;
            }
            _remotePlayers.Spawn(spawned, _clock != null ? _clock.LatestTick : 0);
```

바꿀 코드:

```csharp
                _health = 0;
                _shield = 0;
                _hud.SetVisible(true);
                _inventoryHud.SetVisible(true);
                return;
            }
            _remotePlayers.Spawn(spawned, _clock != null ? _clock.LatestTick : 0);
```

변경 9/9 — 찾을 코드:

```csharp
            _remotePlayers.Clear();
            _clock = null;
            _weapons = null;
            _pendingSteps = 0;
            _renderTick = 0;
            _crosshair.SetVisible(false);
            _hud.HideDeath();
            _hud.SetVisible(false);
            _fireEffects.HideAll();
        }
    }
```

바꿀 코드:

```csharp
            _remotePlayers.Clear();
            _clock = null;
            _weapons = null;
            _weaponCatalog = null;
            _itemCatalog = null;
            _inventory = default;
            _useSeconds = 0f;
            _worldItems.Clear();
            _pendingSteps = 0;
            _renderTick = 0;
            _crosshair.SetVisible(false);
            _hud.HideDeath();
            _hud.SetVisible(false);
            _inventoryHud.SetUseProgress(-1f);
            _inventoryHud.SetPrompt(0, 0, null, null);
            _inventoryHud.SetVisible(false);
            _fireEffects.HideAll();
        }
    }
```

- [ ] **Step 8: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 91개 PASS

- [ ] **Step 9: Unity Editor import 확인**

사용자에게 Editor 포커스를 요청한 뒤 Task 9의 `N9` 이후만 본다.

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; tail -n +N9 "$LOG" | grep -n -E "error CS|LegacyRuntime|Font"
```

Expected: `error CS` 없음(있으면 Task 9 Step 10과 같이 마지막 컴파일 결과만 판정한다). Play 시작 후 다시 실행해 폰트 오류가 없는지도 본다.

- [ ] **Step 10: 사용자 수동 확인 요청**

서버를 새로 빌드해 실행하고(`dotnet run --project Server/src/ProjectH.Server`), 두 Client(Multiplayer Play Mode 또는 Standalone + Editor)로 접속한다.
- 아레나 곳곳(바닥 12곳, 1 m 박스 3개와 플랫폼 위 5곳)에 작은 도형이 천천히 돈다: 무기 = 등급 색 막대(회색·초록·파랑·보라·주황), 탄약 = 노란 원통, Medkit = 빨간 구, Shield Cell = 하늘색 구. 두 Client에서 같은 자리에 같은 모양·색이다. 서버를 같은 설정으로 다시 켜면 배치가 같다.
- 시작은 빈손: 오른쪽 아래 무기 표시 없음, 아래 가운데 `> 1  -`, `  2  -`, `  3  -`, `[4] Medkit x0    [5] Shield Cell x0`, HUD `HP 100   SH 0`. 좌클릭해도 궤적이 없다.
- 아이템에 2 m 안으로 다가가면 화면 가운데 아래 `[E] Pick up Vesper AR [Rare]`(또는 `Light Rounds x60` 등). E를 누르면 두 Client 모두에서 그 아이템이 사라지고 HUD 칸에 무기가 들어온다(빈손이었으면 그 칸이 선택된다). 멀어지면 안내가 사라진다.
- 무기 셋을 채운 뒤 네 번째 무기를 주우면 현재 칸과 바뀌고, 원래 무기가 그 자리에 떨어진다.
- 탄약을 줍고 쏘다가 R: 탄창이 보유량에서 채워지고 보유량이 준다(`Vesper AR   30 / 15` 등). 보유량 0이면 재장전하지 않는다. 한도가 찬 탄약을 주우면 `Inventory full`이 잠깐 뜨고, 일부만 들어가면 바닥 아이템의 안내 수량이 줄어든다.
- 3으로 세 번째 칸, G로 현재 무기를 발 앞에 떨어뜨림(벽을 보고 G면 발밑).
- Health가 줄었을 때 4: 가운데 아래 녹색 막대가 3초 동안 차고 HP +50. 쓰는 중 좌클릭·1/2/3·G면 막대가 사라지고 회복되지 않는다. 걸어도 계속된다. Shield Cell(5)은 2초, SH +25, 최대 100.
- 상대를 죽이면 그 자리 둘레 1 m 원에 무기·탄약·회복이 흩어지고 두 Client 모두에 보인다. 죽은 쪽은 3초 뒤 빈손으로 부활한다.
- 아무도 없는 Spawn Point에서 아이템을 주운 뒤 30초가 지나면 그 자리에 새 아이템이 생긴다.
- (선택) Profiler에서 GC Alloc: 가만히 있거나 걷는 프레임은 0. 안내·HUD 값이 바뀌는 프레임에만 문자열 하나.

- [ ] **Step 11: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 11: 문서 갱신

**Files:**
- Modify: `Docs/Networking.md`, `Docs/Server.md`, `Docs/Client.md`, `Docs/Architecture.md`, `Docs/BattleRoyale.md`

**Interfaces:**
- Consumes: Task 1–10의 동작
- Produces: 없음

- [ ] **Step 1: `Docs/Networking.md`** (버전 4, 패킷 표, Join 순서, "인벤토리와 Loot" 절, 입력 검증)

변경 1/5 — 찾을 코드:

```markdown
# Networking

Transport: LiteNetLib 2.1.4 (UDP). 프레이밍 `[PacketId: byte][payload]`, little-endian, 수기 직렬화(`PacketWriter`/`PacketReader`).
`ProtocolVersion`(현재 3. Phase 3에서 입력 명령·Snapshot 형식이 바뀌고 전투 패킷이 생겼다) 불일치 연결은 접속 단계(`OnConnectionRequest`)에서 `RejectReason.VersionMismatch`로 거절된다. 그 외 거절 사유: `ServerFull`(연결 수 ≥ MaxPlayers), `BadRequest`(연결 데이터 없음·파싱 실패·DevPlayerId 빈 문자열). Client는 거절 사유를 `Rejected: <사유>`로 표시한다.

## MTU
```

바꿀 코드:

```markdown
# Networking

Transport: LiteNetLib 2.1.4 (UDP). 프레이밍 `[PacketId: byte][payload]`, little-endian, 수기 직렬화(`PacketWriter`/`PacketReader`).
`ProtocolVersion`(현재 4. Phase 3에서 입력 명령·Snapshot 형식이 바뀌고 전투 패킷이 생겼고, Phase 4에서 Buttons가 2B가 되고 무기 카탈로그에 탄약 종류가, 아이템 패킷 6종이 생겼다) 불일치 연결은 접속 단계(`OnConnectionRequest`)에서 `RejectReason.VersionMismatch`로 거절된다. 그 외 거절 사유: `ServerFull`(연결 수 ≥ MaxPlayers), `BadRequest`(연결 데이터 없음·파싱 실패·DevPlayerId 빈 문자열). Client는 거절 사유를 `Rejected: <사유>`로 표시한다.

## MTU
```

변경 2/5 — 찾을 코드:

```markdown
| JoinMatchResponse | S→C | ReliableOrdered | Result(Ok / AlreadyJoined / MatchFull), MyEntityId, ServerTick, SimHz, SnapshotHz |
| PlayerSpawned | S→C | ReliableOrdered | EntityId, Position, Yaw |
| PlayerDespawned | S→C | ReliableOrdered | EntityId |
| PlayerInput | C→S | Unreliable | 최근 입력 1–3개(Seq, MoveX, MoveY, Yaw, Buttons, AimYaw, AimPitch, ViewTick = 29B), 오래된 것부터 |
| WorldSnapshot | S→C | Sequenced | ServerTick, AckInputSeq(수신자별), Count, 수신자 블록(Health, Shield, WeaponSlot, Ammo, ReloadRemainingTicks = 6B, 수신자별), [EntityId, Position, VelocityY, Yaw, Flags(bit0 생존)] |
| WeaponCatalog | S→C | ReliableOrdered | Join 응답 직후 1회. 무기 1–8개: WeaponId, Name ≤ 16B, Damage, FireIntervalTicks, MagazineSize, ReloadTicks, Range, Automatic |
| ShotFired | S→C(전원) | Unreliable | ShooterId, Start(눈), End(멈춘 곳) |
| HitConfirmed | S→C(쏜 사람) | ReliableOrdered | TargetId, Damage(무기의 명목 피해. 실제로 깎인 양이 아니다), Killed |
| DamageTaken | S→C(맞은 사람) | ReliableOrdered | AttackerId, Damage, FromDirection(맞은 쪽 → 쏜 쪽 단위 벡터) |
```

바꿀 코드:

```markdown
| JoinMatchResponse | S→C | ReliableOrdered | Result(Ok / AlreadyJoined / MatchFull), MyEntityId, ServerTick, SimHz, SnapshotHz |
| PlayerSpawned | S→C | ReliableOrdered | EntityId, Position, Yaw |
| PlayerDespawned | S→C | ReliableOrdered | EntityId |
| PlayerInput | C→S | Unreliable | 최근 입력 1–3개(Seq, MoveX, MoveY, Yaw, Buttons u16, AimYaw, AimPitch, ViewTick = 30B), 오래된 것부터 |
| WorldSnapshot | S→C | Sequenced | ServerTick, AckInputSeq(수신자별), Count, 수신자 블록(Health, Shield, WeaponSlot = 현재 인벤토리 칸 0–2, Ammo = 그 칸의 탄창(빈 칸이면 0), ReloadRemainingTicks = 6B, 수신자별), [EntityId, Position, VelocityY, Yaw, Flags(bit0 생존)] |
| WeaponCatalog | S→C | ReliableOrdered | Join 응답 직후 1회. 무기 1–8개: WeaponId, Name ≤ 16B, Damage, FireIntervalTicks, MagazineSize, ReloadTicks, Range, Automatic, AmmoType(1 Light, 2 Medium, 3 Heavy) |
| ItemCatalog | S→C | ReliableOrdered | WeaponCatalog 직후 1회. 등급 5개(Name, DamageMultiplier), 탄약 3종(Type, Name, Max), 소모품 2종(Type, Name, UseTicks, Heal, Shield, MaxStack). 최대 219B |
| WorldItems | S→C(새로 들어온 사람) | ReliableOrdered | ItemCatalog 직후. 월드 아이템 전체를 50개씩 나눠서: Count 1–50 + [ItemId u16, Kind, DefId, Rarity, Amount u16, Position] × Count(19B씩). 최대 952B, 256개면 6개 패킷 |
| ItemSpawned | S→C(전원) | ReliableOrdered | 아이템 1개(19B). 새 아이템이거나 수량 변경(ItemId 기준 Upsert) |
| ItemRemoved | S→C(전원) | ReliableOrdered | ItemId |
| InventoryState | S→C(본인) | ReliableOrdered | Join 때와, 인벤토리가 바뀐 Tick 끝에 1회(발사는 제외). 칸 3 × (WeaponId(0 = 빈 칸), Rarity, MagAmmo), CurrentSlot, 탄약 3 × u16, Medkits, ShieldCells, Using(0 없음, 1 Medkit, 2 ShieldCell), UseRemainingTicks. 22B |
| PickupResult | S→C(누른 사람) | ReliableOrdered | Result(Ok / NothingInRange / Full), ItemId. 안내 표시용 |
| ShotFired | S→C(전원) | Unreliable | ShooterId, Start(눈), End(멈춘 곳) |
| HitConfirmed | S→C(쏜 사람) | ReliableOrdered | TargetId, Damage(무기의 명목 피해. 실제로 깎인 양이 아니다), Killed |
| DamageTaken | S→C(맞은 사람) | ReliableOrdered | AttackerId, Damage, FromDirection(맞은 쪽 → 쏜 쪽 단위 벡터) |
```

변경 3/5 — 찾을 코드:

```markdown
| PlayerRespawned | S→C(전원) | ReliableOrdered | EntityId, Position, Yaw |

- Snapshot 헤더 11B + 수신자 블록 6B + 엔티티 23B. 분할되지 않으므로 `MaxPacketSize` 1200B 이내여야 한다 → 50명 = 17 + 23 × 50 = 1167B(`PacketTests`가 고정), 최대 50 엔티티(`MaxSnapshotEntities`), `MaxPlayers ≤ 50`(기본 16, 시작 시 검증). 서버는 payload를 한 번 쓰고 수신자마다 AckInputSeq와 수신자 블록만 덮어쓴다(`WorldSnapshotHeader.PatchRecipient`).
- Buttons는 알려진 비트(Jump, Sprint, Fire, Reload, Slot1, Slot2)만 남기고 나머지는 버린다. 입력 패킷은 최대 2 + 3 × 29 = 89B다.
- Client가 "누구를 맞혔다"고 보내는 필드는 없다. 명중은 서버가 조준 방향으로 판정한다.
- Join 결과: MatchFull이면 응답만 보낸다. 이미 참가한 peer의 중복 Join은 서버 Match에 도달하지 않는다(아래 Validation).
```

바꿀 코드:

```markdown
| PlayerRespawned | S→C(전원) | ReliableOrdered | EntityId, Position, Yaw |

- Snapshot 헤더 11B + 수신자 블록 6B + 엔티티 23B. 분할되지 않으므로 `MaxPacketSize` 1200B 이내여야 한다 → 50명 = 17 + 23 × 50 = 1167B(`PacketTests`가 고정), 최대 50 엔티티(`MaxSnapshotEntities`), `MaxPlayers ≤ 50`(기본 16, 시작 시 검증). 서버는 payload를 한 번 쓰고 수신자마다 AckInputSeq와 수신자 블록만 덮어쓴다(`WorldSnapshotHeader.PatchRecipient`).
- Buttons(u16)는 알려진 비트(Jump, Sprint, Fire, Reload, Slot1, Slot2, Slot3, Interact, Drop, UseMedkit, UseShieldCell = 0x07FF)만 남기고 나머지는 버린다. 입력 패킷은 최대 2 + 3 × 30 = 92B다.
- 새 패킷은 모두 1200B 이하다(`ItemPacketTests`가 고정). Snapshot 형식은 Phase 3과 같다(50명 1167B).
- Client가 "누구를 맞혔다"고 보내는 필드는 없다. 명중은 서버가 조준 방향으로 판정한다.
- Join 결과: MatchFull이면 응답만 보낸다. 이미 참가한 peer의 중복 Join은 서버 Match에 도달하지 않는다(아래 Validation).
```

변경 4/5 — 찾을 코드:

```markdown
1. Client `Connect` → 연결 요청 데이터 전송.
2. Server `OnConnectionRequest`에서 검사 후 Accept, `PeerState`를 `peer.Tag`에 설정하고 그 다음에 `Connected` 제어 메시지를 Control 채널에 쓴다. LiteNetLib이 `Accept()` 안에서 `OnPeerConnected`를 동기 호출하는데 그 시점엔 Tag가 아직 없으므로, `OnPeerConnected`에서는 아무것도 하지 않는다.
3. Client `OnPeerConnected`에서 `JoinMatchRequest` 전송.
4. Server가 `JoinMatchResponse` → 새 플레이어에게 전원의 `PlayerSpawned`, 기존 플레이어에게 새 플레이어의 `PlayerSpawned`.

## Tick
```

바꿀 코드:

```markdown
1. Client `Connect` → 연결 요청 데이터 전송.
2. Server `OnConnectionRequest`에서 검사 후 Accept, `PeerState`를 `peer.Tag`에 설정하고 그 다음에 `Connected` 제어 메시지를 Control 채널에 쓴다. LiteNetLib이 `Accept()` 안에서 `OnPeerConnected`를 동기 호출하는데 그 시점엔 Tag가 아직 없으므로, `OnPeerConnected`에서는 아무것도 하지 않는다.
3. Client `OnPeerConnected`에서 `JoinMatchRequest` 전송.
4. Server가 `JoinMatchResponse` → `WeaponCatalog` → `ItemCatalog` → `WorldItems`(분할) → `InventoryState` → 새 플레이어에게 전원의 `PlayerSpawned`, 기존 플레이어에게 새 플레이어의 `PlayerSpawned`.

## Tick
```

변경 5/5 — 찾을 코드:

```markdown
- **사망 중 예측:** `PlayerDied`(내 것)를 받으면 예측기는 이동하지 않고, Seq는 계속 올리되 이동 0·버튼 없음 입력을 보낸다(부활 직후 서버가 이 입력 일부를 살아 있는 상태로 처리하기 때문). 사망 중 Snapshot은 서버 위치로 바로 맞춘다. `PlayerRespawned`를 받으면 예측기를 새로 만들지 않고 같은 예측기의 상태만 Spawn 위치로 되돌린다. **Seq는 유지한다**(1부터 다시 세면 서버가 이미 소비한 Seq 이하를 버려 모든 입력이 무시된다). 생존 비트가 예측기 상태와 다른 Snapshot(다른 생의 것)은 재조정에 쓰지 않는다.
- **원격 플레이어:** 생존 여부는 Snapshot의 생존 비트로 정한다(죽어 있는 동안 들어온 Client는 `PlayerDied`를 받지 않았다). 살아 있는 동안 뷰는 회전하지 않는다(서버 AABB와 같은 축 정렬 `BoxCollider` 0.7 × 1.8 × 0.7을 유지하기 위해서이고, 캡슐은 Y축 둘레로 둥글어 보이는 모습은 같다). 이 Collider는 `Ignore Raycast` 레이어(2)에 있어 조준 광선만 본다. 죽으면 회색으로 눕고(진행 방향으로) Collider가 꺼진다. 다시 살아나면 보간 기록을 비워 시체 자리에서 미끄러지지 않고 Spawn 위치에 바로 나타난다.

## Validation (서버)

- 입력: NaN/Infinity → 0, 이동 벡터 길이 > 1 → 정규화(`MovementSimulation.Step`), Yaw가 비유한이면 이전 Yaw 유지. Seq 중복·역행(이미 소비한 Seq 이하) 무시, Tick당 플레이어별 1스텝. 시작 위치가 박스와 겹치면 밀어낸 뒤 이동한다.
- Join은 연결당 한 번만 처리한다. 두 번째부터는 잘못된 패킷으로 세고 Match에 전달하지 않는다(Control 채널 이벤트 ≤ 3/연결 유지).
- Join하지 않은 peer의 PlayerInput은 거절(잘못된 패킷). peer별 입력 패킷은 초당 `SimHz * 2`개(기본 60)까지만 받고 초과분은 잘못된 패킷으로 센다(고정 1초 창).
- 알 수 없는 PacketId, 클라이언트가 보낼 수 없는 PacketId(서버→클라이언트 패킷), 잘리거나 개수가 범위 밖인 PlayerInput → drop하고 잘못된 패킷으로 센다. 연결별 `BadPacketDisconnectThreshold`(20) 이상이면 Disconnect.
- 전투 입력: 조준 각이 NaN/Infinity면 그 입력은 발사하지 않는다(탄·간격 소모 없음). Pitch는 ±89°로 자른다. ViewTick은 되감기 범위로 자른다. Slot 비트가 둘 다 켜져 있으면 교체하지 않는다. 명중 대상은 Client가 정하지 않는다.
- 위치는 서버가 계산하므로 순간이동·속도 조작은 구조적으로 불가능하다.
```

바꿀 코드:

```markdown
- **사망 중 예측:** `PlayerDied`(내 것)를 받으면 예측기는 이동하지 않고, Seq는 계속 올리되 이동 0·버튼 없음 입력을 보낸다(부활 직후 서버가 이 입력 일부를 살아 있는 상태로 처리하기 때문). 사망 중 Snapshot은 서버 위치로 바로 맞춘다. `PlayerRespawned`를 받으면 예측기를 새로 만들지 않고 같은 예측기의 상태만 Spawn 위치로 되돌린다. **Seq는 유지한다**(1부터 다시 세면 서버가 이미 소비한 Seq 이하를 버려 모든 입력이 무시된다). 생존 비트가 예측기 상태와 다른 Snapshot(다른 생의 것)은 재조정에 쓰지 않는다.
- **원격 플레이어:** 생존 여부는 Snapshot의 생존 비트로 정한다(죽어 있는 동안 들어온 Client는 `PlayerDied`를 받지 않았다). 살아 있는 동안 뷰는 회전하지 않는다(서버 AABB와 같은 축 정렬 `BoxCollider` 0.7 × 1.8 × 0.7을 유지하기 위해서이고, 캡슐은 Y축 둘레로 둥글어 보이는 모습은 같다). 이 Collider는 `Ignore Raycast` 레이어(2)에 있어 조준 광선만 본다. 죽으면 회색으로 눕고(진행 방향으로) Collider가 꺼진다. 다시 살아나면 보간 기록을 비워 시체 자리에서 미끄러지지 않고 Spawn 위치에 바로 나타난다.

## 인벤토리와 Loot (Phase 4)

- **데이터(D2–D5):** 서버의 `weapons.json`(무기 3종: Vesper AR Medium, Kestrel LR Heavy, Wisp SMG Light = 12 / 2 Tick / 25발 / 48 Tick / 80 m / 자동), `items.json`(등급 5개와 피해 배율 1.00–1.20, 탄약 Light 180·Medium 150·Heavy 30 한도와 줍는 양 60·45·10, Medkit 3 s +50 스택 3, Shield Cell 2 s +25 스택 6), `loot.json`(Floor·Tower 가중치 표와 등급 가중치 50/25/15/7/3). 시작 시 셋 다 검증하고 틀리면 서버가 뜨지 않는다. Client에는 `WeaponCatalog`·`ItemCatalog`로 간다.
- **Loot(D5–D7):** Spawn Point는 Shared `LootPoints`(바닥 12곳·박스 위 5곳, `TestArena` 옆)다. 서버는 Match를 만들 때 모든 Point에서 표를 굴려 아이템을 만든다. 난수는 Game Loop 스레드가 가진 `System.Random(LootSeed)` 하나라서 시드가 같으면 배치가 같다. 무기는 Id를 균등하게, 등급은 가중치로, 탄창은 가득. 탄약은 종류를 균등하게 줍는 양만큼. 다 가져간 Point는 `LootRespawnSeconds`(기본 30) 뒤 다시 굴린다(0이면 끔). 떨어뜨린 아이템은 다시 생기지 않는다.
- **월드 아이템(D13):** 최대 256개. 가득 차면 가장 오래 전에 떨어진 아이템부터 지우고(`ItemRemoved`를 먼저 보낸 뒤 새 `ItemSpawned`), Spawn Point 아이템은 지우지 않는다. ItemId는 1–65535를 한 바퀴 돈 뒤에야 다시 쓴다.
- **인벤토리(D1, D10):** 무기 칸 3개(무기, 등급, 탄창, 칸별 다음 발사 Tick), 현재 칸, 탄약 3종 보유량, Medkit·Shield Cell 개수. 서버가 소유하고 Client는 `InventoryState`를 표시만 한다. 시작과 부활은 빈손(Shield 0, Health 100)이다. 빈 칸도 선택할 수 있고, 빈 칸에서는 발사·재장전하지 않는다. 재장전은 보유량에서 탄창으로 옮기고, 보유량이 0이면 시작하지 않는다(Snapshot에 끝나지 않는 재장전이 나오지 않는다). 피해 = 무기 피해 × 등급 배율, 소수점은 0에서 멀어지게 반올림(`decimal` 계산), 최소 1. Shield 최대는 100이다.
- **서버 Tick 순서(살아 있는 플레이어):** 이동 → 재장전 완료 → (**실제로 받은 입력일 때만**) 사용 취소 → 칸 선택 → 버리기 → 줍기 → 재장전 → 발사 → 사용 시작 → (매 Tick) 사용 완료. Tick 전체 앞에는 부활과 Loot 재생성이, 끝에는 `ServerTick++` → History → 바뀐 인벤토리의 `InventoryState` → Snapshot이 온다. 누락 입력 반복은 이동만 반복하고, 줍기·버리기·사용·교체는 반복하지 않는다.
- **줍기(D8, D9):** E를 누른 입력에서 서버가 발 기준 수평 2.0 m·수직 2.0 m 안의 가장 가까운(3D 거리, 같으면 작은 ItemId) 아이템을 고른다. Client는 ItemId를 보내지 않는다. 무기: 첫 빈 칸에(빈손이면 그 칸을 손에 든다), 칸이 다 차 있으면 현재 칸과 바꾸고 원래 무기는 주운 자리에 떨어진다. 탄약·소모품: 한도까지만 받고 나머지는 수량을 줄여 바닥에 남긴다(`ItemSpawned` Upsert). 받을 수 없으면 `Full`, 범위 안에 없으면 `NothingInRange`. 같은 Tick에 둘이 누르면 먼저 처리된 플레이어만 얻는다.
- **버리기·사망 Drop(D12):** G는 현재 무기를 발 앞 1 m에 탄창째 떨어뜨린다. 발 높이 0.5 m에서 그 방향으로 박스에 막히면(얇은 벽 너머 포함) 발밑에 떨어뜨린다. 떨어진 곳은 발 높이 이하에서 가장 높은 바닥·박스 윗면이다(아이템은 나중에 떨어지지 않는다). 죽으면 무기·탄약 종류·소모품 종류마다 1개씩 시체 둘레 1 m 원 위에 `PlayerDied` 뒤에 떨어뜨리고 인벤토리를 비운다. 무기를 버렸다 다시 주워 발사 간격을 건너뛸 수 없다(버린 무기의 다음 발사 Tick이 새로 주운 무기에 걸린다).
- **회복(D11):** 4 = Medkit(90 Tick, Health +50), 5 = Shield Cell(60 Tick, Shield +25), 둘 다 최대 100. 이미 최대면 시작하지 않는다. 발사(Fire 비트)·칸 선택 비트·G·다른 회복을 누르면 취소되고(다른 회복은 같은 Tick에 새로 시작), 이동은 취소하지 않는다. 죽으면 취소된다. 사용 중 상태는 `InventoryState`의 Using·UseRemainingTicks로 Client에 간다.
- **Client:** `WorldItemViews`가 아이템을 모양(무기 큐브·탄약 원통·회복 구)과 색(등급 5색, 탄약·Medkit·Shield Cell)으로 그린다. `PickupRule`(서버 규칙 사본)이 E가 집을 아이템에 "[E] Pick up …"을 띄운다. `WeaponState`는 인벤토리 칸 3개를 기준으로 발사를 흉내 낸다. 칸의 내용물과 보유량은 `InventoryState`에서, 현재 칸과 그 탄창은 Snapshot에서 받는다(두 패킷은 채널이 달라 순서가 섞이므로, 현재 칸 탄창을 `InventoryState`로 덮어쓰면 ack 비교가 그 값을 고치지 못한다).

## Validation (서버)

- 입력: NaN/Infinity → 0, 이동 벡터 길이 > 1 → 정규화(`MovementSimulation.Step`), Yaw가 비유한이면 이전 Yaw 유지. Seq 중복·역행(이미 소비한 Seq 이하) 무시, Tick당 플레이어별 1스텝. 시작 위치가 박스와 겹치면 밀어낸 뒤 이동한다.
- Join은 연결당 한 번만 처리한다. 두 번째부터는 잘못된 패킷으로 세고 Match에 전달하지 않는다(Control 채널 이벤트 ≤ 3/연결 유지).
- Join하지 않은 peer의 PlayerInput은 거절(잘못된 패킷). peer별 입력 패킷은 초당 `SimHz * 2`개(기본 60)까지만 받고 초과분은 잘못된 패킷으로 센다(고정 1초 창).
- 알 수 없는 PacketId, 클라이언트가 보낼 수 없는 PacketId(서버→클라이언트 패킷), 잘리거나 개수가 범위 밖인 PlayerInput → drop하고 잘못된 패킷으로 센다. 연결별 `BadPacketDisconnectThreshold`(20) 이상이면 Disconnect.
- 전투 입력: 조준 각이 NaN/Infinity면 그 입력은 발사하지 않는다(탄·간격 소모 없음). Pitch는 ±89°로 자른다. ViewTick은 되감기 범위로 자른다. Slot 비트가 둘 이상 켜져 있으면 교체하지 않는다. 명중 대상은 Client가 정하지 않는다.
- 아이템 입력: 줍기 대상·위치·수량은 Client가 보내지 않는다(Interact 비트뿐). Medkit·Shield Cell 비트가 함께 켜져 있으면 사용하지 않는다. 받은 패킷의 아이템 값(Kind, DefId, 등급, 수량, 비유한 위치)은 Client의 `TryRead`가 거른다.
- 위치는 서버가 계산하므로 순간이동·속도 조작은 구조적으로 불가능하다.
```

- [ ] **Step 2: `Docs/Server.md`** (설정 2개, 데이터 파일 검증, Tick 순서, 소유·할당, Lifetime)

변경 1/3 — 찾을 코드:

```markdown
| BadPacketDisconnectThreshold | 20 | ≥ 1 |
| DisconnectTimeoutMs | 5000 | ≥ 500 |
| StatsIntervalSeconds | 10 | ≥ 1 |

무기 데이터: `Server/src/ProjectH.Server/weapons.json`(출력 폴더로 복사). 시작 시 `WeaponCatalog.LoadFile`이 읽고 검증한다(무기 1–8개, Id 1–255 중복 없음, 이름 1–16 UTF-8 바이트, damage 1–65535, magazineSize 1–255, fireIntervalSeconds·reloadSeconds > 0이고 Tick으로 바꿔 65535 이하, range > 0, spread·recoil ≥ 0, 모두 유한). 초 값은 `SimHz`로 반올림해 Tick으로 바꾼다(최소 1). 파일이 없거나 틀리면 `GameServerService` 생성자가 `InvalidOperationException`을 던져 서버가 시작하지 않는다.

파생값: `SnapshotHz = SimHz / SnapshotEveryTicks`, `MaxInputPacketsPerSecond = SimHz * 2`(peer별 입력 상한).
```

바꿀 코드:

```markdown
| BadPacketDisconnectThreshold | 20 | ≥ 1 |
| DisconnectTimeoutMs | 5000 | ≥ 500 |
| StatsIntervalSeconds | 10 | ≥ 1 |
| LootSeed | 1 | ≥ 0. Loot 난수 시드(같으면 배치가 같다) |
| LootRespawnSeconds | 30 | 0–3600. 다 가져간 Spawn Point를 다시 채우는 시간, 0이면 끔 |

데이터 파일: `Server/src/ProjectH.Server/weapons.json`, `items.json`, `loot.json`(출력 폴더로 복사). 시작 시 `GameData.LoadDirectory`가 셋을 읽고 검증한다.
- `weapons.json`(`WeaponCatalog`): 무기 1–8개, Id 1–255·이름 중복 없음, 이름 1–16 UTF-8 바이트, damage 1–65535, magazineSize 1–255, fireIntervalSeconds·reloadSeconds > 0이고 Tick으로 바꿔 65535 이하, range > 0, spread·recoil ≥ 0, ammoType Light·Medium·Heavy, 모두 유한.
- `items.json`(`ItemCatalog`): 등급 정확히 5개(이름 중복 없음, 배율 0 초과 10 이하), 탄약 Light·Medium·Heavy 각 1개(max 1–65535, pickupAmount 1–max), 소모품 Medkit·ShieldCell 각 1개(useSeconds > 0, heal·shield ≥ 0이고 합 > 0, maxStack 1–255). 이름은 1–16 UTF-8 바이트이고 목록 안에서 중복 없음.
- `loot.json`(`LootTable`): rarityWeights에 5개 등급 이름이 모두 있고 가중치 1–1,000,000, 표 1개 이상, 표마다 항목 1개 이상, kind(Weapon, Ammo, Medkit, ShieldCell) 중복 없음, 가중치 1–1,000,000. Shared `LootPoints`가 쓰는 표 이름이 모두 있어야 한다.
초 값은 `SimHz`로 반올림해 Tick으로 바꾼다(최소 1). 파일이 없거나 틀리면 `GameServerService` 생성자가 `InvalidOperationException`을 던져 서버가 시작하지 않는다.

파생값: `SnapshotHz = SimHz / SnapshotEveryTicks`, `MaxInputPacketsPerSecond = SimHz * 2`(peer별 입력 상한).
```

변경 2/3 — 찾을 코드:

```markdown

`Match.Tick`은 플레이어마다 `MovementSimulation.Step(ref state, input, 1/SimHz, TestArena.Boxes)`를 호출한다(Shared 지형 박스와 충돌. 규칙은 `Networking.md` "이동 충돌"). 박스 20개 × 50명 × 30 Hz라 비용은 무시할 수준이고 할당이 없다. Spawn은 반경 5 m 원 위이고 아레나는 중앙 반경 7 m를 비워 둔다(`TestArenaTests`).

전투(Phase 3, 규칙은 `Networking.md` "전투"): `Match.Tick`은 부활 → 입력·이동 → 재장전 완료 → 실제 입력의 무기 처리(`WeaponRules`)·발사(`HitScan`) → `ServerTick++` → History 기록 → Snapshot 순서다. 전투 코드는 `Game/Combat/`(`WeaponCatalog`, `WeaponDefinition`, `WeaponRules`, `HitScan`, `CombatRules`, `PositionHistory`)에 있고 Game Loop 스레드만 쓴다. `WeaponCatalog`는 시작 후 바뀌지 않는다. 발사 한 번은 박스 20개 + 플레이어 수만큼의 slab 교차이고, 전송은 `_sendBuffer` 하나를 재사용하므로 발사 Tick도 할당이 없다(`LagCompensationTests.FiringTick_AllocatesNothing`).

## Queue
```

바꿀 코드:

```markdown

`Match.Tick`은 플레이어마다 `MovementSimulation.Step(ref state, input, 1/SimHz, TestArena.Boxes)`를 호출한다(Shared 지형 박스와 충돌. 규칙은 `Networking.md` "이동 충돌"). 박스 20개 × 50명 × 30 Hz라 비용은 무시할 수준이고 할당이 없다. Spawn은 반경 5 m 원 위이고 아레나는 중앙 반경 7 m를 비워 둔다(`TestArenaTests`).

전투(Phase 3, 규칙은 `Networking.md` "전투"·"인벤토리와 Loot"): `Match.Tick`은 부활 → Loot 재생성 → 입력·이동 → 재장전 완료 → 실제 입력의 사용 취소·칸 선택·버리기·줍기·재장전·발사(`HitScan`)·사용 시작 → 사용 완료 → `ServerTick++` → History 기록 → 바뀐 인벤토리 전송 → Snapshot 순서다. 전투 코드는 `Game/Combat/`(`WeaponCatalog`, `WeaponDefinition`, `WeaponRules`, `HitScan`, `CombatRules`, `PositionHistory`)에 있고 Game Loop 스레드만 쓴다. `WeaponCatalog`는 시작 후 바뀌지 않는다. 발사 한 번은 박스 20개 + 플레이어 수만큼의 slab 교차이고, 전송은 `_sendBuffer` 하나를 재사용하므로 발사 Tick도 할당이 없다(`LagCompensationTests.FiringTick_AllocatesNothing`).

인벤토리·Loot(Phase 4): `Game/Items/`(`ItemCatalog`, `LootTable`, `LootSpawner`, `WorldItems`, `Inventory`, `StartingLoadout`, `ItemRules`, `ConsumableRules`)와 `Game/GameData`. 모두 Game Loop 스레드 소유이고 Lock이 없다. `WorldItems`는 256칸 고정 배열이라 선형 탐색(최대 256)이 줍기 한 번의 비용이다. 월드 아이템을 바꾸는 곳은 `Match.SpawnItem`·`RemoveItemAt`·`SetItemAmount` 셋뿐이고, 각자 이벤트 전송을 끝낸 뒤 돌아오므로 `_sendBuffer`를 쓰는 `PacketWriter`가 다른 전송과 겹치지 않는다. 줍기·회복 Tick도 할당이 없다(`PickupDropTests.PickupTick_AllocatesNothing`, `ConsumableTests.UseTicks_AllocateNothing`). `Match`와 `GameLoop` 생성자의 `StartingLoadout`·`LootPoint[]` 인자는 테스트용이고, 운영은 빈손 시작과 Shared `LootPoints`를 쓴다.

## Queue
```

변경 3/3 — 찾을 코드:

```markdown
- Session(`_peers` 항목, `PlayerEntity`): `Connected` 메시지에서 등록, Disconnected 메시지 또는 peer 상태가 Connected가 아니면 제거. `Connected` 메시지는 `OnConnectionRequest`에서 `PeerState` 설정 후 쓴다(`OnPeerConnected`는 `Accept()` 안에서 Tag 설정 전에 호출되기 때문). LiteNetLib DisconnectTimeout이 끊김을 보장한다.
- LiteNetLib는 peer id를 재사용한다. 메시지의 NetPeer 참조가 현재 `_peers`의 peer와 같을 때만 처리한다. 같은 id로 다른 NetPeer가 `Connected`로 들어오면 이전 세션을 먼저 제거하고(`RemovePeer`) 새 peer로 교체한다. 이전 peer의 늦은 Disconnected는 참조 비교로 무시된다.
- `Match.Leave`는 `PlayerDespawned`를 남은 플레이어에게 보낸다.
- 플레이어별 전투 상태는 `PlayerEntity`에 있고 플레이어와 함께 사라진다. 위치 History는 32칸 고정 링이라 늘어나지 않고, Join·부활 때 새로 시작한다. 탄·발사 간격 배열은 슬롯 수(2) 고정이다.
- 종료: Ctrl+C → Host `StopAsync` → `GameLoop.Stop`(Game Loop 스레드 Join) → `NetManager.Stop(true)`.

## 관측
```

바꿀 코드:

```markdown
- Session(`_peers` 항목, `PlayerEntity`): `Connected` 메시지에서 등록, Disconnected 메시지 또는 peer 상태가 Connected가 아니면 제거. `Connected` 메시지는 `OnConnectionRequest`에서 `PeerState` 설정 후 쓴다(`OnPeerConnected`는 `Accept()` 안에서 Tag 설정 전에 호출되기 때문). LiteNetLib DisconnectTimeout이 끊김을 보장한다.
- LiteNetLib는 peer id를 재사용한다. 메시지의 NetPeer 참조가 현재 `_peers`의 peer와 같을 때만 처리한다. 같은 id로 다른 NetPeer가 `Connected`로 들어오면 이전 세션을 먼저 제거하고(`RemovePeer`) 새 peer로 교체한다. 이전 peer의 늦은 Disconnected는 참조 비교로 무시된다.
- `Match.Leave`는 `PlayerDespawned`를 남은 플레이어에게 보낸다.
- 플레이어별 전투 상태는 `PlayerEntity`에 있고 플레이어와 함께 사라진다. 위치 History는 32칸 고정 링이라 늘어나지 않고, Join·부활 때 새로 시작한다. 인벤토리는 칸 3개·탄약 3종 고정 배열이다. 접속을 끊은 플레이어의 인벤토리는 떨어뜨리지 않고 사라진다.
- 월드 아이템은 256개가 상한이고(가장 오래된 Drop부터 지움), Spawn Point 타이머는 Point마다 하나씩 고정 배열이다.
- 종료: Ctrl+C → Host `StopAsync` → `GameLoop.Stop`(Game Loop 스레드 Join) → `NetManager.Stop(true)`.

## 관측
```

- [ ] **Step 3: `Docs/Client.md`** (NetClient 이벤트, 키, 새 파일, 프레임 흐름, Lifetime, 조작, EditMode 테스트)

변경 1/3 — 찾을 코드:

```markdown
| `Bootstrap/GameBootstrap` | GameClient 1개 생성(DontDestroyOnLoad) |
| `Bootstrap/TestWorld` | 100×100m 바닥, 조명, Shared `TestArena` 박스마다 Cube(BoxCollider, 공유 Material 1개). Collider는 카메라 충돌·발사 광선용이고 이동 충돌은 `MovementSimulation`이 한다 (Phase 6에서 교체) |
| `Bootstrap/DevConnectPanel`, `LaunchArgs` | 개발용 접속 UI(IMGUI), 실행 인자 |
| `Net/NetClient` | LiteNetLib, 메인 스레드 전용(`UnsyncedEvents = false`, `Update`에서 Poll). 전투 패킷 6종(WeaponCatalog, ShotFired, HitConfirmed, DamageTaken, PlayerDied, PlayerRespawned)을 이벤트로 올린다 |
| `Net/VectorConversions` | System.Numerics ↔ UnityEngine 벡터 변환 |
| `Input/InputReader` | Input System 격리. Move, Look, Jump, Sprint, Fire(좌클릭), Aim(우클릭), Reload(R), Slot1·Slot2(1·2), Esc. 누름은 `QueuedButtons`에 모았다가 다음 예측 Step이 가져간다 |
| `Game/GameClient` | 구성 루트, 생성·해제 책임 |
| `Game/LocalPlayerPredictor` | 예측·재조정. `TestArena.Boxes`와 충돌(서버와 같은 박스). 입력에 버튼·조준·ViewTick을 담는다. 사망 중 정지, 부활 시 상태만 초기화(Seq 유지, 예측기는 새로 만들지 않는다). `SetAim`은 이번 프레임의 입력마다 그 Step의 예측 결과 위치를 눈으로 삼아 조준 각을 구하고, `RenderPosition`은 카메라·뷰에 쓴다 |
| `Game/AimSolver` | 눈(발 + 1.6 m) → 조준점 방향을 Yaw/Pitch로(순수 계산) |
| `Game/WeaponState` | 서버 무기 규칙의 표시용 사본. 예측 입력마다 Step하고 결과를 64칸 링에 저장한다. Snapshot 수신자 블록이 ack 시점 기록과 다르면 서버 값으로 맞춘 뒤 ack 이후 입력을 다시 적용한다(이동 재조정과 같은 방식) |
| `Game/CombatHud` | 코드로 만든 UGUI(Legacy `Text`, 내장 `LegacyRuntime.ttf`). HP·SH, 무기·탄, 명중 표시, 피격 방향, 사망 카운트다운. 값이 바뀔 때만 문자열을 만든다 |
| `Game/Crosshair` | 코드로 만든 Screen Space Overlay Canvas 조준점(UGUI, GraphicRaycaster 없음) |
| `Game/LocalFireEffects`, `RingCursor` | 발사 연출: 내 발사는 `WeaponState`가 쏜다고 한 입력마다(프레임당 최대 3발) 총구 → 조준점 광선, 다른 사람 발사는 `ShotFired`의 시작 → 끝. 궤적 16·탄착 32 고정 링 풀 |
| `Game/RemotePlayers`, `RemotePlayerInterpolator`, `ServerClock` | 다른 플레이어 보간. Snapshot 생존 비트로 회색·눕힘, 부활하면 보간 기록을 비운다. 살아 있는 뷰는 회전하지 않는다(서버 AABB와 같은 축 정렬 Collider 유지) |
```

바꿀 코드:

```markdown
| `Bootstrap/GameBootstrap` | GameClient 1개 생성(DontDestroyOnLoad) |
| `Bootstrap/TestWorld` | 100×100m 바닥, 조명, Shared `TestArena` 박스마다 Cube(BoxCollider, 공유 Material 1개). Collider는 카메라 충돌·발사 광선용이고 이동 충돌은 `MovementSimulation`이 한다 (Phase 6에서 교체) |
| `Bootstrap/DevConnectPanel`, `LaunchArgs` | 개발용 접속 UI(IMGUI), 실행 인자 |
| `Net/NetClient` | LiteNetLib, 메인 스레드 전용(`UnsyncedEvents = false`, `Update`에서 Poll). 전투 패킷 6종(WeaponCatalog, ShotFired, HitConfirmed, DamageTaken, PlayerDied, PlayerRespawned)과 아이템 패킷(ItemCatalog, WorldItems·ItemSpawned → `ItemReceived`, ItemRemoved, InventoryState, PickupResult)을 이벤트로 올린다 |
| `Net/VectorConversions` | System.Numerics ↔ UnityEngine 벡터 변환 |
| `Input/InputReader` | Input System 격리. Move, Look, Jump, Sprint, Fire(좌클릭), Aim(우클릭), Reload(R), Slot1–3(1·2·3), Interact(E), Drop(G), UseMedkit(4), UseShieldCell(5), Esc. 누름은 `QueuedButtons`에 모았다가 다음 예측 Step이 가져간다 |
| `Game/GameClient` | 구성 루트, 생성·해제 책임 |
| `Game/LocalPlayerPredictor` | 예측·재조정. `TestArena.Boxes`와 충돌(서버와 같은 박스). 입력에 버튼·조준·ViewTick을 담는다. 사망 중 정지, 부활 시 상태만 초기화(Seq 유지, 예측기는 새로 만들지 않는다). `SetAim`은 이번 프레임의 입력마다 그 Step의 예측 결과 위치를 눈으로 삼아 조준 각을 구하고, `RenderPosition`은 카메라·뷰에 쓴다 |
| `Game/AimSolver` | 눈(발 + 1.6 m) → 조준점 방향을 Yaw/Pitch로(순수 계산) |
| `Game/WeaponState` | 서버 무기 규칙의 표시용 사본. 인벤토리 칸 3개(빈 칸 가능)와 탄약 보유량 기준. 예측 입력마다 Step하고 결과(보유량 포함)를 64칸 링에 저장한다. Snapshot 수신자 블록이 ack 시점 기록과 다르면 서버 값으로 맞춘 뒤 ack 이후 입력을 다시 적용한다(이동 재조정과 같은 방식). 칸 내용물과 보유량은 `InventoryState`로 받고, 현재 칸의 탄창은 무기가 그대로면 Snapshot에 맡긴다 |
| `Game/WorldItemList`, `WorldItemViews` | 월드 아이템 목록(256칸 고정, Upsert·Remove)과 뷰. 뷰는 최대 256개 풀에서 빌려 쓰고 Dispose 때만 파괴한다. 공유 Material 8개(등급 5색, 탄약, Medkit, Shield Cell)와 내장 Mesh 3개(무기 큐브, 탄약 원통, 회복 구), Collider 없음, 프레임마다 회전 하나를 모두에 적용 |
| `Game/PickupRule` | 서버 줍기 대상 규칙의 사본(수평·수직 2 m, 가장 가까운 것, 같으면 작은 ItemId). "[E]" 안내용이고 결정은 서버가 한다 |
| `Game/InventoryHud`, `InventoryHudText` | 인벤토리 HUD(칸 3개·선택 표시·등급 색·탄창/보유량, 회복 개수, 회복 진행 막대, "[E] Pick up …" 안내, 줍기 실패 안내). 문자열은 `InventoryHudText`가 값이 바뀔 때만 만든다 |
| `Game/CombatHud` | 코드로 만든 UGUI(Legacy `Text`, 내장 `LegacyRuntime.ttf`). HP·SH, 현재 무기·탄창/보유량, 명중 표시, 피격 방향, 사망 카운트다운. 값이 바뀔 때만 문자열을 만든다 |
| `Game/Crosshair` | 코드로 만든 Screen Space Overlay Canvas 조준점(UGUI, GraphicRaycaster 없음) |
| `Game/LocalFireEffects`, `RingCursor` | 발사 연출: 내 발사는 `WeaponState`가 쏜다고 한 입력마다(프레임당 최대 3발) 총구 → 조준점 광선, 다른 사람 발사는 `ShotFired`의 시작 → 끝. 궤적 16·탄착 32 고정 링 풀 |
| `Game/RemotePlayers`, `RemotePlayerInterpolator`, `ServerClock` | 다른 플레이어 보간. Snapshot 생존 비트로 회색·눕힘, 부활하면 보간 기록을 비운다. 살아 있는 뷰는 회전하지 않는다(서버 AABB와 같은 축 정렬 Collider 유지) |
```

변경 2/3 — 찾을 코드:

```markdown
2. `InputReader.Update`(점프·재장전·슬롯 눌림 큐잉), 커서·버튼 처리: 커서가 풀려 있으면 좌클릭은 잠금만 하고(Join 후), 그 클릭은 버튼을 뗄 때까지 발사로 치지 않는다. 조준·발사는 커서가 잠겨 있을 때만.
3. 원격 플레이어 렌더(`ServerClock.RenderTick`로 보간 대상 Tick 계산, 이 값이 입력의 ViewTick이 된다)
4. 카메라 Look(ADS 중 감도 ×0.6) → `LocalPlayerPredictor.Advance`(고정 스텝 예측, Sprint·Fire는 매 Step, 눌림은 마지막 Step) → 로컬 뷰 자세(사망이면 눕힘)
5. `LateUpdate`: 카메라 Follow(머리 기준점 → 어깨점 → 카메라 두 번 SphereCast, 막히면 즉시 당기고 풀리면 감쇠 복귀) → 조준점(`Physics.SyncTransforms` 후 화면 중앙 Raycast, 원격 플레이어 포함) → 이번 프레임 입력들에 조준(눈은 예측 위치 기준)·ViewTick 기록 → `WeaponState` Step → `PlayerInput` 전송 → 내 발사 연출(카메라가 움직인 뒤라 조준점과 일치) → HUD

`Joined` 응답에서 SimHz·SnapshotHz를 받아 `ServerClock`과 보간 지연(`2 / SnapshotHz`)을 정한다. 자기 `PlayerSpawned`를 받으면 `LocalPlayerPredictor`와 로컬 뷰를 만든다.

## Lifetime

생성 순서: 월드(+박스 Material) → InputReader → ShoulderCamera → Crosshair → CombatHud → LocalFireEffects → NetClient. `GameClient.OnDestroy`는 역순으로 해제한다: 이벤트 구독 해제(11개) → NetClient Dispose(`NetManager.Stop`) → 매치 상태(예측기·로컬 뷰·원격 뷰·ServerClock·WeaponState, 조준점·HUD 숨김, 발사 연출 숨김) → LocalFireEffects Dispose(풀 GameObject·Material) → CombatHud Dispose(Canvas) → Crosshair Dispose(Canvas) → InputAction Dispose → 플레이어 공유 Material(3개) → 월드·박스 Material 파괴. 연결이 끊기면(`OnDisconnected`) 매치 상태를 지운다. 종료 때 Unity가 오브젝트를 먼저 파괴했을 수 있어(OnDestroy 순서는 보장되지 않음) `Crosshair.SetVisible`, `CombatHud`의 메서드, `LocalFireEffects.HideAll`은 루트가 파괴됐으면 아무것도 하지 않고 돌아온다. 예외가 나면 뒤의 해제가 건너뛰어지기 때문이다.
`renderer.material`은 쓰지 않는다(복제됨). 캡슐은 스폰/디스폰 때만 생성·파괴하므로 풀링하지 않는다. `RemotePlayers`는 Spawn/Despawn/Clear로만 증감하고, 보간 히스토리는 플레이어당 8개 고정이다. 발사 연출은 궤적 16·탄착 32개를 생성자에서 한 번 만들고 `RingCursor`로 오래된 것부터 재사용하므로 늘어나지 않는다. 발사·카메라의 Physics 호출은 단일 결과 버전만 쓴다.

## 실행과 두 Client 확인
```

바꿀 코드:

```markdown
2. `InputReader.Update`(점프·재장전·슬롯 눌림 큐잉), 커서·버튼 처리: 커서가 풀려 있으면 좌클릭은 잠금만 하고(Join 후), 그 클릭은 버튼을 뗄 때까지 발사로 치지 않는다. 조준·발사는 커서가 잠겨 있을 때만.
3. 원격 플레이어 렌더(`ServerClock.RenderTick`로 보간 대상 Tick 계산, 이 값이 입력의 ViewTick이 된다)
4. 카메라 Look(ADS 중 감도 ×0.6) → `LocalPlayerPredictor.Advance`(고정 스텝 예측, Sprint·Fire는 매 Step, 눌림은 마지막 Step) → 로컬 뷰 자세(사망이면 눕힘)
5. `LateUpdate`: 카메라 Follow(머리 기준점 → 어깨점 → 카메라 두 번 SphereCast, 막히면 즉시 당기고 풀리면 감쇠 복귀) → 조준점(`Physics.SyncTransforms` 후 화면 중앙 Raycast, 원격 플레이어 포함) → 이번 프레임 입력들에 조준(눈은 예측 위치 기준)·ViewTick 기록 → `WeaponState` Step → `PlayerInput` 전송 → 내 발사 연출(카메라가 움직인 뒤라 조준점과 일치) → HUD → 아이템 회전 → 인벤토리 HUD·"[E]" 안내(예측 위치 기준 `PickupRule`)

`Joined` 응답에서 SimHz·SnapshotHz를 받아 `ServerClock`과 보간 지연(`2 / SnapshotHz`)을 정한다. 자기 `PlayerSpawned`를 받으면 `LocalPlayerPredictor`와 로컬 뷰를 만든다.

## Lifetime

생성 순서: 월드(+박스 Material) → InputReader → ShoulderCamera → Crosshair → CombatHud → InventoryHud → WorldItemViews → LocalFireEffects → NetClient. `GameClient.OnDestroy`는 역순으로 해제한다: 이벤트 구독 해제(16개) → NetClient Dispose(`NetManager.Stop`) → 매치 상태(예측기·로컬 뷰·원격 뷰·ServerClock·WeaponState·카탈로그·마지막 InventoryState·월드 아이템 목록과 뷰 반납, 조준점·HUD·인벤토리 HUD 숨김, 발사 연출 숨김) → LocalFireEffects Dispose(풀 GameObject·Material) → WorldItemViews Dispose(루트와 풀 전체, Material 8개) → InventoryHud Dispose(Canvas) → CombatHud Dispose(Canvas) → Crosshair Dispose(Canvas) → InputAction Dispose → 플레이어 공유 Material(3개) → 월드·박스 Material 파괴. 연결이 끊기면(`OnDisconnected`) 매치 상태를 지운다. 종료 때 Unity가 오브젝트를 먼저 파괴했을 수 있어(OnDestroy 순서는 보장되지 않음) `Crosshair.SetVisible`, `CombatHud`의 메서드, `LocalFireEffects.HideAll`은 루트가 파괴됐으면 아무것도 하지 않고 돌아온다. 예외가 나면 뒤의 해제가 건너뛰어지기 때문이다.
`renderer.material`은 쓰지 않는다(복제됨). 캡슐은 스폰/디스폰 때만 생성·파괴하므로 풀링하지 않는다. `RemotePlayers`는 Spawn/Despawn/Clear로만 증감하고, 보간 히스토리는 플레이어당 8개 고정이다. 발사 연출은 궤적 16·탄착 32개를 생성자에서 한 번 만들고 `RingCursor`로 오래된 것부터 재사용하므로 늘어나지 않는다. 발사·카메라의 Physics 호출은 단일 결과 버전만 쓴다.

## 실행과 두 Client 확인
```

변경 3/3 — 찾을 코드:

````markdown
1. 서버: `dotnet run --project Server/src/ProjectH.Server`
2. Multiplayer Play Mode: Window > Multiplayer > Multiplayer Play Mode에서 Player 2 활성화 → Play → 각 창에서 Connect
3. Standalone: 빌드 후 `ProjectH.exe -autoConnect -devId p2` + Editor Play (`-host`, `-port`도 지정 가능. 기본 127.0.0.1:7777, devId 미지정 시 `dev-<8자리>` 자동 생성)
4. 조작: 좌클릭(커서 잠금, 잠긴 뒤 누르고 있으면 발사), 우클릭(누르는 동안 조준), R(재장전), 1·2(무기 교체), WASD, Shift(달리기), Space(점프), Esc(해제), F1(패널)

## 자동 검사

EditMode 테스트: `Assets/Tests/EditMode`(`LocalPlayerPredictorTests`, `ArenaPredictionTests`, `RemotePlayerInterpolatorTests`, `ShoulderCameraMathTests`, `AimSolverTests`, `WeaponStateTests`, `RingCursorTests`). 이 테스트들은 Physics·GameObject·네이티브 Quaternion 함수를 쓰지 않으므로 Unity 밖 NUnit 프로젝트로도 돌릴 수 있다. 발사 간격은 `WeaponState`가 센다.

```bash
"C:/Program Files/Unity/Hub/Editor/6000.3.24f1/Editor/Unity.exe" -batchmode -nographics -projectPath Client -runTests -testPlatform EditMode -testResults _workspace/editmode-results.xml -logFile _workspace/unity-tests.log
````

바꿀 코드:

````markdown
1. 서버: `dotnet run --project Server/src/ProjectH.Server`
2. Multiplayer Play Mode: Window > Multiplayer > Multiplayer Play Mode에서 Player 2 활성화 → Play → 각 창에서 Connect
3. Standalone: 빌드 후 `ProjectH.exe -autoConnect -devId p2` + Editor Play (`-host`, `-port`도 지정 가능. 기본 127.0.0.1:7777, devId 미지정 시 `dev-<8자리>` 자동 생성)
4. 조작: 좌클릭(커서 잠금, 잠긴 뒤 누르고 있으면 발사), 우클릭(누르는 동안 조준), R(재장전), 1·2·3(무기 칸), E(줍기), G(현재 무기 버리기), 4(Medkit), 5(Shield Cell), WASD, Shift(달리기), Space(점프), Esc(해제), F1(패널). 시작은 빈손이라 먼저 아이템을 주워야 쏠 수 있다.

## 자동 검사

EditMode 테스트: `Assets/Tests/EditMode`(`LocalPlayerPredictorTests`, `ArenaPredictionTests`, `RemotePlayerInterpolatorTests`, `ShoulderCameraMathTests`, `AimSolverTests`, `WeaponStateTests`, `RingCursorTests`, `PickupRuleTests`, `WorldItemListTests`, `InventoryHudTextTests`). 이 테스트들은 Physics·GameObject·네이티브 Quaternion 함수를 쓰지 않으므로 Unity 밖 NUnit 프로젝트로도 돌릴 수 있다. 발사 간격은 `WeaponState`가 센다.

```bash
"C:/Program Files/Unity/Hub/Editor/6000.3.24f1/Editor/Unity.exe" -batchmode -nographics -projectPath Client -runTests -testPlatform EditMode -testResults _workspace/editmode-results.xml -logFile _workspace/unity-tests.log
````

- [ ] **Step 4: `Docs/Architecture.md`**

변경 1/3 — 찾을 코드:

````markdown
# Architecture

Phase 3 Combat 기준. 설계 근거: `Docs/specs/2026-09-30-phase0-network-sync-design.md`, `Docs/specs/2026-09-30-phase1-character-prototype-design.md`, `Docs/specs/2026-10-01-phase3-combat-design.md`.

```mermaid
flowchart LR
````

바꿀 코드:

````markdown
# Architecture

Phase 4 Inventory / Loot 기준. 설계 근거: `Docs/specs/2026-09-30-phase0-network-sync-design.md`, `Docs/specs/2026-09-30-phase1-character-prototype-design.md`, `Docs/specs/2026-10-01-phase3-combat-design.md`, `Docs/specs/2026-10-01-phase4-inventory-loot-design.md`.

```mermaid
flowchart LR
````

변경 2/3 — 찾을 코드:

```markdown
        Net[NetClient] --> Remote[RemotePlayers]
        Net --> Predictor
        Net --> Hud[CombatHud, WeaponState]
    end
    subgraph Shared[/Shared UPM package/]
        Protocol[Protocol: packets]
        Sim[Simulation: MovementSimulation, TestArena]
    end
    subgraph Server[.NET 10 Server]
        Listener[NetworkListener] -->|Channels| Loop[GameLoop thread]
        Loop --> Match
        Match --> Combat[Combat: WeaponRules, HitScan, PositionHistory]
    end
    Client <-->|UDP / LiteNetLib| Server
    Client -.uses.-> Shared
```

바꿀 코드:

```markdown
        Net[NetClient] --> Remote[RemotePlayers]
        Net --> Predictor
        Net --> Hud[CombatHud, WeaponState]
        Net --> Items[WorldItemViews, InventoryHud]
    end
    subgraph Shared[/Shared UPM package/]
        Protocol[Protocol: packets]
        Sim[Simulation: MovementSimulation, TestArena, LootPoints]
    end
    subgraph Server[.NET 10 Server]
        Listener[NetworkListener] -->|Channels| Loop[GameLoop thread]
        Loop --> Match
        Match --> Combat[Combat: WeaponRules, HitScan, PositionHistory]
        Match --> ItemsS[Items: Inventory, WorldItems, LootSpawner]
    end
    Client <-->|UDP / LiteNetLib| Server
    Client -.uses.-> Shared
```

변경 3/3 — 찾을 코드:

```markdown
| 폴더 | 역할 |
|---|---|
| `Client/` | Unity. 입력·표시·예측·보간. 결과를 확정하지 않는다. 카메라·조준점은 Client 표시 전용이고, 발사는 입력에 조준 방향만 실어 보낸다(누구를 맞혔는지는 보내지 않는다) |
| `Server/` | .NET 10 Dedicated Server. 이동 결과와 명중·피해·사망·부활을 결정한다. 무기 수치는 `weapons.json` |
| `Shared/` | 패킷 DTO, 프로토콜 상수, 이동 계산과 그 지형 박스·충돌(`Simulation/`, 유일한 로직 예외: `game-core-rules` 4절) |
| `Docs/` | 이 문서들 |

## Shared 소비 방식
```

바꿀 코드:

```markdown
| 폴더 | 역할 |
|---|---|
| `Client/` | Unity. 입력·표시·예측·보간. 결과를 확정하지 않는다. 카메라·조준점은 Client 표시 전용이고, 발사는 입력에 조준 방향만 실어 보낸다(누구를 맞혔는지는 보내지 않는다) |
| `Server/` | .NET 10 Dedicated Server. 이동 결과와 명중·피해·사망·부활, Loot 배치·줍기·버리기·회복을 결정하고 인벤토리를 소유한다. 데이터는 `weapons.json`, `items.json`, `loot.json` |
| `Shared/` | 패킷 DTO, 프로토콜 상수, 이동 계산과 그 지형 박스·충돌(`Simulation/`, 로직 예외: `game-core-rules` 4절), 맵 배치 데이터(`LootPoints`, 좌표 상수만: 4절 예외 2) |
| `Docs/` | 이 문서들 |

## Shared 소비 방식
```

- [ ] **Step 5: `Docs/BattleRoyale.md`**

변경 — 찾을 코드:

```markdown
# Battle Royale

아직 구현되지 않았다. 현재는 한 Match에 접속한 플레이어가 평면 위를 이동하는 단계다.
Match State Machine, Safe Zone, 탈락·승자 판정은 Phase 5에서 이 문서에 설계와 함께 추가한다.
```

바꿀 코드:

```markdown
# Battle Royale

아직 구현되지 않았다. 현재는 한 Match에서 아이템을 주워 싸우고, 죽으면 가진 것을 떨어뜨리고 3초 뒤 빈손으로 부활하는 테스트 아레나 단계다(Phase 4).
Loot 재생성(`LootRespawnSeconds`, 기본 30초)은 테스트 아레나용이다. 배틀로얄 규칙에서는 0으로 둔다.
Match State Machine, Safe Zone, 탈락·승자 판정은 다음 단계에서 이 문서에 설계와 함께 추가한다.
```

- [ ] **Step 6: 문서와 코드 일치 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 435개 PASS(문서만 바뀌었다). 문서의 수치(30 B, 92 B, 219 B, 952 B, 22 B, 256, 2.0 m, 90·60 Tick, 30 s)가 코드 상수와 같은지 눈으로 확인한다.

- [ ] **Step 7: 체크포인트**

결과를 기록한다. 커밋하지 않는다. 전체 작업이 끝났으므로 사용자에게 수동 확인 결과(Task 10 Step 10)와 함께 알리고, 커밋은 사용자가 "푸시"를 입력할 때 `github-push` 스킬로 한다.

---

## Self-Review

**1. Spec 대응:**
- §1 데이터(weapons/items/loot, 검증 항목): Task 2(`weapons.json`, `items.json`, `ItemCatalog`, `GameData`), Task 3(`loot.json`, `LootTable`, 표 참조 검사).
- §2 Server: `ItemCatalog` Task 2, `LootSpawner` Task 4·8, `WorldItems` Task 4, `Inventory`·재장전·등급 피해·InventoryState Task 5, 줍기·버리기·사망 Drop Task 6, 회복과 Tick 순서 2·8·9단계 Task 7, 재생성 Task 8. Lock 없음·할당 없음 테스트는 Task 3·4·6·7.
- §3 Protocol v4: Task 1(카탈로그 AmmoType은 Task 2). `PickupResult` 코드는 "Spec 해석" 1.
- §4 Client: NetClient·WeaponState·InputReader Task 9, WorldItemViews·안내·InventoryHud·GameClient Task 10.
- §5 하네스: Task 3 Step 7.
- §6 테스트: 데이터 Task 2·3, Loot·Spawn Point Task 3·4, 월드 아이템 Task 4, 줍기·버리기·사망 Drop Task 6, 재장전·발사 Task 5, 사용 Task 7, 재생성 Task 8, 프로토콜 Task 1·2, 통합 Task 8, 기존 전투 테스트 이전 Task 5, EditMode Task 9·10, 수동 확인 Task 10 Step 10.

**2. Placeholder 검사:** 코드 블록 밖에서 `TBD`, `TODO`, `implement later`, `Similar to Task`, `fill in`을 찾았다. 0건이다.

**3. 타입·시그니처 일치:** 모든 코드 Step은 저장소 밖 사본에 Task 1부터 순서대로 적용하고, Task마다 서버 빌드·테스트와 Client 스크래치 컴파일·NUnit을 돌린 결과에서 만들었다. 이 문서의 모든 "찾을 코드 → 바꿀 코드"와 전체 파일(110개 파일 변경)을 이전 Task 상태에 다시 적용해 다음 Task 상태와 글자 단위로 같음을 확인했다. "찾을 코드"는 각 파일 안에서 한 번씩만 나온다.

**4. Review Focus:** 같은 Tick 두 명 줍기 → Task 6 `TwoPlayers_SameTick_SameItem_OnlyTheFirstGetsIt`. 재장전이 탄을 만듦 → Task 5 `RoundsAreConserved_AcrossFireReloadAndSwitches`, `EmptyReserve_NoReload_EvenAfterTheLastRound`. Drop 복제·손실 → Task 6 `Items_AreConserved_ThroughDropSwapDeathAndPickup`. 꽉 찬 월드 Join 분할 → Task 4 `Join_WithAFullWorld_SendsSixChunks_AndEveryItemOnce`, Task 1 `WorldItems_FullChunk_Is952Bytes_AndRoundTrips`. 반복 사망에도 256 유지 → Task 6 `RepeatedDeaths_KeepTheWorldAtTheCap_AndSpawnPointItemsSurvive`.
