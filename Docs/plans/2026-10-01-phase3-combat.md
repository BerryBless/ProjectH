# Phase 3 Combat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 두 Client가 테스트 아레나에서 서로를 쏘고, 서버만 명중·피해·사망·부활을 결정하며(Lag Compensation 포함), Client는 즉시 발사 연출과 서버 결과 기반 HUD를 보여 준다.

**Architecture:** 발사는 기존 입력 명령에 실린다(Fire·Reload·Slot 비트 + 조준 Yaw/Pitch + ViewTick, 명령당 29 B). 서버는 입력을 소비하는 Tick에 `WeaponRules`(교체 → 재장전 → 발사)를 적용하고, 발사면 `HitScan`(slab 광선–AABB)으로 아레나 박스·바닥과 `PositionHistory`(플레이어별 32칸 링)에서 ViewTick으로 되감은 다른 플레이어를 판정한다. 결과는 `ShotFired`(Unreliable)·`HitConfirmed`/`DamageTaken`/`PlayerDied`/`PlayerRespawned`(ReliableOrdered)와 Snapshot의 엔티티 생존 비트·수신자 전용 6 B 블록으로 알린다. 무기 수치는 서버의 `weapons.json`에만 있고 Join 직후 `WeaponCatalog` 패킷으로 Client에 간다. Client는 같은 규칙의 표시용 사본(`WeaponState`)으로 궤적과 탄 수를 즉시 보여 주고, 서버 값으로 맞춘다.

**Tech Stack:** .NET 10, C# (Shared·Client는 C# 9 / netstandard2.1), `System.Numerics`, `System.Text.Json`(새 NuGet 없음), xUnit, LiteNetLib 2.1.4, Unity 6000.3.24f1(URP, Input System, UGUI 2.0 Legacy `Text`), NUnit(EditMode).

**Spec:** `Docs/specs/2026-10-01-phase3-combat-design.md` (결정 D1–D15는 확정)

## Global Constraints

- 커밋하지 않는다. 각 Task의 마지막 단계는 "체크포인트"(검증 결과 기록)다. Commit·Push는 사용자가 "푸시"를 입력했을 때만 `github-push` 스킬로 한다.
- 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다. 우리 코드는 Lock을 쓰지 않는다. 전투 상태는 Game Loop 스레드만 만진다(Match·PlayerEntity·WeaponCatalog). 스레드 구조는 바꾸지 않는다.
- 판정 코드는 **Server에만** 둔다: `Server/src/ProjectH.Server/Game/Combat/`. Shared에는 패킷 DTO만 추가한다(`Shared/Runtime/Protocol/CombatPackets.cs`). `game-core-rules` 4절 Shared 예외는 그대로다(spec §4).
- `Shared/Runtime`은 `netstandard2.1` + C# 9에서 컴파일되어야 한다. `UnityEngine` 참조 금지, `System.Numerics`만 사용. `record`·`init`·`Vector3` 인덱서 금지.
- `ProtocolConstants.ProtocolVersion = 3`. 입력 명령 29 B(최대 입력 패킷 2 + 3 × 29 = 89 B). Snapshot = 헤더 11 B + 수신자 블록 6 B + 엔티티 23 B × N, 50명일 때 **1167 B ≤ 1200 B**(`MaxPacketSize`, 분할 없음). 수신자별 값(AckInputSeq, 수신자 블록)은 한 번 쓴 payload를 덮어써서 보낸다(`WorldSnapshotHeader.PatchRecipient`).
- 무기 데이터(D5, `Server/src/ProjectH.Server/weapons.json`): Vesper AR = 피해 20, 0.1 s 간격(3 Tick), 탄창 30, 재장전 2.0 s(60 Tick), 사거리 150 m, 자동. Kestrel LR = 피해 90, 1.25 s(38 Tick), 탄창 5, 재장전 2.5 s(75 Tick), 사거리 300 m, 단발. Spread·Recoil은 0(필드만). 무기 1–8개, 이름 ≤ 16 UTF-8 바이트, Id 1–255 중복 없음. Slot1/Slot2 = 목록의 0/1번째.
- 전투 수치(D6–D9): Health 100, Shield 50, 눈 높이 발 + 1.6 m, 판정 상자 = 이동 AABB(반폭 0.35 m, 높이 1.8 m), Pitch ±89°, 부활 3 s(= `SimHz × 3` Tick), 되감기 최대 0.2 s(30 Hz에서 6 Tick), History 32칸.
- Server Hot Path(Tick, 발사, 판정, 송신)는 할당이 없고(테스트로 고정), 예외를 흐름 제어에 쓰지 않는다. JSON 예외는 시작 시에만 잡는다.
- Phase 0·1 동작을 유지한다: 누락 입력 유예(SimHz/2 동안 직전 입력 반복, 점프 제외), Join 1회, peer별 입력 상한 `SimHz × 2`/s, `MtuOverride = 1232`, 아레나 규칙(중앙 7 m 비움, 박스끼리 옆으로 맞닿지 않음), `ShoulderClearance`, `runInBackground`, D12 좌클릭 규칙(잠그는 클릭은 발사 아님), 발사 연출 고정 링 풀(궤적 16·탄착 32), 재조정 규칙(ack 0, 64칸 히스토리, 비유한 값 무시).
- Unity: Scene·Prefab·`.meta` 파일을 직접 만들거나 수정하지 않는다(새 `.cs`의 `.meta`는 Unity가 만든다. 파일을 지울 때만 짝 `.meta`를 같이 지운다). Unity가 생성한 `Client/*.csproj`는 건드리지 않는다. Hot Path(Update, LateUpdate, 발사, HUD)에서 LINQ·임시 컬렉션·매 프레임 문자열 생성·`renderer.material` 금지. `Physics.Raycast`는 단일 결과 버전만.
- Unity batchmode는 쓸 수 없다(사용자가 Editor를 열어 둔다). Client 검증은 (a) 스크래치 컴파일·NUnit 프로젝트, (b) Editor 자동 import 후 `Editor.log`의 새 `error CS` 확인, (c) 사용자 수동 확인이다.
- Task 1, 7, 8, 9는 중간 Step에서 Client/Shared 코드가 일부러 컴파일되지 않는 구간이 있다(테스트 먼저, 시그니처 변경, 파일 삭제). 그 Task의 Editor.log 확인 Step 전까지 사용자에게 Unity Editor를 포커스하지 말아 달라고 요청한다. 그래도 로그에 `error CS`가 보이면, 그 뒤에 오류 없는 스크립트 컴파일이 다시 있었는지 보고 마지막 컴파일 결과만 판정한다.

## Review Focus

- 벽(기둥) 바로 뒤에 선 상대를 조준점에 두고 쏨 → 서버 광선은 벽에서 멈추고 상대는 피해를 받지 않아야 한다 (Task 4 `Shot_AtTargetBehindPillar_HitsThePillar`).
- 조작된 Client가 NaN/Infinity 조준 각이나 터무니없는 Pitch를 보냄 → 발사 없음, 탄·발사 간격 소모 없음, Pitch는 ±89°로 잘리고 결과 좌표는 유한해야 한다 (Task 3 `AimDirection_NonFinite_IsNoShot`, Task 4 `NonFiniteAim_IsNoShot_AndAmmoUnchanged`, `HugePitch_IsClampedTo89_AndShotStaysFinite`).
- 트리거를 쥔 채 입력 패킷이 늦거나 끊김 → 서버의 누락 입력 반복(Phase 0 유예)이 발사를 만들어 내면 안 된다. 실제로 받은 입력만 발사·교체·재장전한다 (Task 4 `MissedInputTicks_DoNotRepeatFire`).
- Client가 ViewTick에 NaN, 먼 미래, 아주 먼 과거를 넣음 → 되감기는 항상 최근 6 Tick 안이고, 부활 직후 되감기는 시체 위치가 아니라 Spawn 위치를 본다 (Task 5 `UntrustedViewTick_IsClampedToTheLastSixTicks`, `AfterRespawn_RewindFindsSpawnPoint_NotTheBody`).
- 사망·부활 전후 예측 → Seq는 이어지고(새 예측기로 1부터 다시 세면 서버가 모든 입력을 버린다), 사망 중에는 빈 입력만 보내며, 다른 생(生)의 Snapshot으로 재조정하지 않아야 한다 (Task 8 `Respawn_ResetsState_KeepsSeq_AndReconcilesFromSpawn`, `Dead_StopsMoving_AndSendsEmptyInputs`, `Reconcile_IgnoresSnapshotFromTheOtherLife`).

(Snapshot 50명 ≤ 1200 B는 spec §5 테스트로 이미 고정되므로 제외했다: Task 1 `Snapshot_WithMaxEntities_Is1167Bytes_AndFitsOneDatagram`. `PlayerDied`가 `PlayerRespawned`보다 늦게 도착하는 경우는 둘 다 같은 ReliableOrdered 채널로 보내 구조적으로 불가능하므로 제외했다: Task 4 `Respawn_ThreeSecondsLater_AtSpawnWithFullState`가 순서를 확인한다.)

## Spec 해석 (구현 전 확정)

spec을 코드에 옮기며 확정한 점이다. 각 Task의 코드가 이 해석을 따른다.

1. **§5 통합 테스트 "5발이면 B 사망"은 D5·D8 수치와 맞지 않는다**(Vesper 20 × 5 = 100 < 150). 서버 테스트는 메모리 JSON 테스트 카탈로그(`TestWeapons`: 자동 무기 피해 30, 3 Tick, 탄창 6, 재장전 30 Tick / 단발 피해 90, 15 Tick, 탄창 2, 재장전 60 Tick)를 쓰고, 5발 = Shield 50 + Health 100이 정확히 0이 되게 한다. 실제 `weapons.json`의 D5 수치는 별도 테스트(`ShippedWeaponsJson_MatchesSpec`)로 고정한다.
2. **§3 "부활 시 예측을 다시 만든다"를 새 `LocalPlayerPredictor` 생성으로 구현하면 안 된다.** Seq가 1부터 다시 시작되고 `PlayerInputBuffer.Add`가 이미 소비한 Seq 이하를 버리므로 모든 입력이 무시된다. 같은 예측기의 상태만 Spawn 위치로 되돌리고 Seq는 잇는다(`Respawn(MoveState)`).
3. **조준점이 상대 위에 있어야 한다(D2).** Phase 1의 플레이어 뷰는 Collider가 없어 조준 광선이 상대를 지나 뒤의 벽에 맞고, 어깨 시차만큼(최대 0.55 m) 서버 광선이 빗나간다. 원격 뷰에 서버 판정 상자 크기의 CapsuleCollider(반지름 0.35, 높이 1.8)를 두고 내장 레이어 2(`Ignore Raycast`)에 넣는다. 카메라 SphereCast(`DefaultRaycastLayers`)는 이 레이어를 보지 않고, 조준·총구 광선만 `AimRaycastMask`로 포함한다. 죽은 원격 플레이어의 Collider는 끈다. `Physics.autoSyncTransforms`가 꺼져 있으므로 조준 Raycast 전에 `Physics.SyncTransforms()`를 호출한다.
4. **로드아웃은 목록의 앞 두 무기다**(Slot1/Slot2 비트가 둘뿐). `NextFireTick`과 탄은 슬롯별로 둬서 무기를 바꿔 Kestrel 간격을 건너뛰지 못하게 한다.
5. **수신자 블록의 `ReloadRemainingTicks`는 재장전 중이면 최소 1**이다. 마지막 Tick에 0을 보내면 Client가 "재장전 아님, 탄 0"으로 보고 재장전 끝마다 다시 맞춘다.
6. **조준은 카메라가 움직인 뒤에 정한다.** 입력은 `Update`에서 예측하고, `LateUpdate`에서 이번 프레임 조준점으로 새 입력들의 AimYaw/AimPitch/ViewTick을 채운 뒤 전송한다(조준은 이동 계산에 쓰이지 않으므로 예측 결과는 그대로다).
7. **사망자는 서버가 Step하지 않는다**(spec §2 "입력의 Seq만 확인 응답하고 무시"). 공중에서 죽으면 그 자리에 멈춘다. Client도 사망 중에는 Step하지 않고, 이동 0·버튼 없음 입력을 보낸다(부활 직후 서버가 이 입력 일부를 살아 있는 상태로 처리하기 때문).
8. **원격 플레이어의 생존 여부는 Snapshot 비트로 정한다.** 누가 죽어 있는 동안 들어온 Client는 `PlayerDied`를 받은 적이 없다. 내 캐릭터의 사망·부활은 Reliable 이벤트로 정하고, 다른 생의 Snapshot은 재조정에 쓰지 않는다.
9. **부활 카운트다운**은 Client 표시 상수 3 s(`CombatHud.RespawnSeconds`, 서버 `CombatRules.RespawnSeconds`와 같은 값)로 센다. 실제 부활 시각은 서버가 정한다.
10. Phase 1의 `FireRateAccumulator`(고정 10발/s)는 `WeaponState`가 입력 Step마다 서버 규칙으로 발사를 흉내 내므로 지운다(테스트의 `RingCursor` 부분은 `RingCursorTests`로 옮긴다).

---

## File Structure

| 경로 | 책임 |
|---|---|
| `Shared/Runtime/Simulation/InputCommand.cs` | 버튼 비트 Fire·Reload·Slot1·Slot2, `AimYaw`·`AimPitch`·`ViewTick` |
| `Shared/Runtime/Protocol/PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs` | 새 PacketId 7–12, 읽기 상한, ProtocolVersion 3 |
| `Shared/Runtime/Protocol/ClientPackets.cs` | `PlayerInputPacket` 29 B 명령, `CommandSize`/`MaxSize` |
| `Shared/Runtime/Protocol/ServerPackets.cs` | `WorldSnapshotHeader`(17 B, `PatchRecipient`), `SnapshotSelf`(6 B), `SnapshotEntity`(23 B, `Flags`) |
| `Shared/Runtime/Protocol/CombatPackets.cs` (신규) | `WeaponInfo`, `WeaponCatalogPacket`, `ShotFired`, `HitConfirmed`, `DamageTaken`, `PlayerDied`, `PlayerRespawned` |
| `Server/src/ProjectH.Server/weapons.json` (신규) | D5 무기 데이터(출력 폴더로 복사) |
| `Server/src/ProjectH.Server/Game/Combat/WeaponDefinition.cs`, `WeaponCatalog.cs` (신규) | JSON 로드·검증·Tick 변환, 와이어 정보 |
| `Server/src/ProjectH.Server/Game/Combat/HitScan.cs`, `CombatRules.cs` (신규) | slab 광선–AABB, 바닥, 피해·조준·ViewTick 규칙 |
| `Server/src/ProjectH.Server/Game/Combat/WeaponRules.cs` (신규) | 탄창·재장전·발사 간격·교체 |
| `Server/src/ProjectH.Server/Game/Combat/PositionHistory.cs` (신규) | 플레이어별 32칸 위치 링, 보간 샘플 |
| `Server/src/ProjectH.Server/Game/PlayerEntity.cs`, `Match.cs` | 전투 상태, Tick 순서, 발사·피해·사망·부활, 이벤트, Snapshot 수신자 블록 |
| `Server/src/ProjectH.Server/GameLoop.cs`, `GameServerService.cs`, `ProjectH.Server.csproj` | 카탈로그 전달·시작 시 로드, `weapons.json` 복사 |
| `Server/tests/ProjectH.Server.Tests/TestWeapons.cs`, `TestAim.cs` (신규) | 테스트 카탈로그, 조준 각 계산 |
| `Server/tests/ProjectH.Server.Tests/Shared/CombatPacketTests.cs` (신규), `PacketTests.cs`, `ProtocolConstantsTests.cs`, `PacketWriterReaderTests.cs` | 프로토콜 |
| `Server/tests/ProjectH.Server.Tests/Game/WeaponCatalogTests.cs`, `HitScanTests.cs`, `CombatRulesTests.cs`, `WeaponRulesTests.cs`, `CombatMatchTests.cs`, `PositionHistoryTests.cs`, `LagCompensationTests.cs` (신규), `MatchTests.cs` | 서버 규칙 |
| `Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs`, `CombatIntegrationTests.cs` (신규), `GameLoopPeerTests.cs`, `ServerIntegrationTests.cs` | 통합 |
| `Client/Assets/Scripts/Net/NetClient.cs` | 새 패킷 6종 이벤트 |
| `Client/Assets/Scripts/Game/AimSolver.cs`, `WeaponState.cs`, `CombatHud.cs` (신규) | 조준 각, 표시용 무기 규칙, HUD |
| `Client/Assets/Scripts/Game/LocalPlayerPredictor.cs` | 버튼·조준·ViewTick, 사망 정지, 부활 초기화 |
| `Client/Assets/Scripts/Game/RemotePlayers.cs`, `RemotePlayerInterpolator.cs`, `PlayerViewFactory.cs` | Snapshot 생존 비트, 부활 시 보간 초기화, 회색·눕힘, 원격 Collider |
| `Client/Assets/Scripts/Game/LocalFireEffects.cs`, `GameClient.cs` | 무기별 발사 연출·원격 궤적, 전체 연결 |
| `Client/Assets/Scripts/Input/InputReader.cs`, `Bootstrap/DevConnectPanel.cs` | R·1·2 키, 안내 문구 |
| `Client/Assets/Scripts/Game/FireRateAccumulator.cs`, `Client/Assets/Tests/EditMode/FireRateAccumulatorTests.cs` (+`.meta`) | 삭제 |
| `Client/Assets/Tests/EditMode/AimSolverTests.cs`, `WeaponStateTests.cs`, `RingCursorTests.cs` (신규), `LocalPlayerPredictorTests.cs`, `ArenaPredictionTests.cs`, `RemotePlayerInterpolatorTests.cs` | EditMode |
| `Docs/Networking.md`, `Docs/Server.md`, `Docs/Client.md`, `Docs/Architecture.md` | 동작 변경 반영 |

스크래치 검증 프로젝트(저장소 밖, 수정해도 됨):

- 컴파일 확인: `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj` — `Client/Assets/Scripts/**/*.cs`와 `Shared/Runtime/**/*.cs`를 netstandard2.1 / C# 9로 Unity DLL(`UnityEngine.*Module`, `Library/ScriptAssemblies/UnityEngine.UI.dll`, `Unity.InputSystem.dll`, LiteNetLib)에 대해 컴파일한다. 새 스크립트는 glob으로 자동 포함된다.
- NUnit: `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj` — `Client/Assets/Tests/EditMode/*.cs` 전부와 **명시한 Client 소스만** + `Shared/Runtime/**/*.cs`를 `UnityEngine.CoreModule`만 참조해 net10.0에서 실행한다. 그래서 EditMode 테스트는 `Physics`·`GameObject`·`Canvas`·네이티브 `Quaternion` 함수를 쓰면 안 된다. 새 순수 소스(`AimSolver.cs`, `WeaponState.cs`)는 Task 7에서 이 csproj에 `<Compile Include>`를 추가한다. `FireRateAccumulator.cs` 항목은 `Condition="Exists(...)"`라 파일을 지워도 그대로 둔다.

서버 명령은 저장소 루트(`E:/popol/ProjectH`)에서 실행한다.

---

### Task 1: Protocol v3 (입력 29 B, Snapshot 수신자 블록·생존 비트, 전투 패킷)

**Files:**
- Modify: `Shared/Runtime/Simulation/InputCommand.cs` (전체 교체)
- Modify: `Shared/Runtime/Protocol/PacketId.cs` (전체 교체), `PacketReader.cs:100`, `ProtocolConstants.cs:6,20`
- Modify: `Shared/Runtime/Protocol/ClientPackets.cs` (전체 교체), `ServerPackets.cs` (전체 교체)
- Create: `Shared/Runtime/Protocol/CombatPackets.cs`
- Modify: `Server/src/ProjectH.Server/Game/Match.cs:134-155` (`SendSnapshots`)
- Test: `Server/tests/ProjectH.Server.Tests/Shared/PacketTests.cs` (전체 교체), `Shared/CombatPacketTests.cs` (신규), `Shared/ProtocolConstantsTests.cs` (전체 교체), `Shared/PacketWriterReaderTests.cs:77-93`, `Integration/ServerIntegrationTests.cs:174`

**Interfaces:**
- Consumes: 기존 `PacketWriter`/`PacketReader`, `ProtocolConstants.MaxInputsPerPacket = 3`, `MaxPacketSize = 1200`, `MaxSnapshotEntities = 50`
- Produces (네임스페이스 `ProjectH.Shared.Simulation` / `ProjectH.Shared.Protocol`):
  - `enum InputButtons : byte { None = 0, Jump = 1, Sprint = 2, Fire = 4, Reload = 8, Slot1 = 16, Slot2 = 32 }`
  - `struct InputCommand { uint Seq; float MoveX, MoveY, Yaw; InputButtons Buttons; float AimYaw, AimPitch, ViewTick; }`
  - `PlayerInputPacket.CommandSize = 29`, `PlayerInputPacket.MaxSize = 89`
  - `PacketId.WeaponCatalog = 7, ShotFired = 8, HitConfirmed = 9, DamageTaken = 10, PlayerDied = 11, PlayerRespawned = 12`
  - `struct SnapshotSelf { const int Size = 6; byte Health, Shield, WeaponSlot, Ammo; ushort ReloadRemainingTicks; static Write/TryRead }`
  - `struct WorldSnapshotHeader { const int Size = 17, AckInputSeqOffset = 5, SelfOffset = 11; uint ServerTick, AckInputSeq; ushort Count; SnapshotSelf Self; static void PatchRecipient(Span<byte> packet, uint ackInputSeq, in SnapshotSelf self) }` (`PatchAckInputSeq`는 없어진다)
  - `struct SnapshotEntity { const int Size = 23; const byte AliveFlag = 1; ...; byte Flags; bool IsAlive }`
  - `struct WeaponInfo { byte WeaponId; string Name; ushort Damage, FireIntervalTicks; byte MagazineSize; ushort ReloadTicks; float Range; bool Automatic; }`
  - `static class WeaponCatalogPacket { const int MaxWeapons = 8, MaxNameBytes = 16; static void Write(ref PacketWriter, WeaponInfo[]); static bool TryRead(ref PacketReader, out WeaponInfo[]) }`
  - `struct ShotFired { ushort ShooterId; Vector3 Start, End; }`, `HitConfirmed { ushort TargetId, Damage; bool Killed; }`, `DamageTaken { ushort AttackerId, Damage; Vector3 FromDirection; }`, `PlayerDied { ushort VictimId, KillerId; }`, `PlayerRespawned { ushort EntityId; Vector3 Position; float Yaw; }` — 모두 `static void Write(ref PacketWriter, in T)` / `static bool TryRead(ref PacketReader, out T)` (PacketId 바이트는 Write가 쓰고, TryRead는 PacketId 다음부터 읽는다). 벡터가 비유한이면 TryRead가 false.

- [ ] **Step 0: Editor.log 기준선 기록 (코드 수정 전)**

이 Task부터 Unity가 컴파일하는 코드(Shared)를 고친다. `Editor.log`는 세션 동안 누적되므로 수정 전 줄 수를 기록해 두고, Task 8 Step 8에서 이 줄 이후만 검사한다.

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; wc -l < "$LOG"
```

출력값을 `N0`로 체크포인트에 기록한다.

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Shared/PacketTests.cs` 전체를 다음으로 바꾼다. 입력 왕복에 새 필드를 넣고, 개수 오류·잘린 입력 거절(29 B 기준), 최대 입력 패킷 89 B, Snapshot 수신자 블록 패치, 50명 Snapshot 1167 B를 고정한다.

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class PacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static InputCommand FullCommand(uint seq) => new InputCommand
    {
        Seq = seq,
        MoveX = 0.5f,
        MoveY = -1f,
        Yaw = 90f + seq,
        Buttons = InputButtons.Jump | InputButtons.Fire | InputButtons.Slot2,
        AimYaw = 12.5f + seq,
        AimPitch = -30f,
        ViewTick = 1000.25f + seq,
    };

    [Fact]
    public void ConnectRequestData_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = 1, DevPlayerId = "abc" });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(ConnectRequestData.TryRead(ref reader, out var data));
        Assert.Equal((ushort)1, data.ProtocolVersion);
        Assert.Equal("abc", data.DevPlayerId);
    }

    [Fact]
    public void ConnectRequestData_EmptyId_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = 1, DevPlayerId = "" });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.False(ConnectRequestData.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerInput_RoundTrip_KeepsOrderAndAimFields()
    {
        var packet = new PlayerInputPacket { Count = 3 };
        for (int i = 0; i < 3; i++) packet.Set(i, FullCommand((uint)(10 + i)));

        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        Assert.Equal(PlayerInputPacket.MaxSize, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out var read));

        Assert.Equal(3, read.Count);
        for (int i = 0; i < 3; i++)
        {
            InputCommand expected = FullCommand((uint)(10 + i));
            InputCommand actual = read.Get(i);
            Assert.Equal(expected.Seq, actual.Seq);
            Assert.Equal(expected.Yaw, actual.Yaw);
            Assert.Equal(expected.Buttons, actual.Buttons);
            Assert.Equal(expected.AimYaw, actual.AimYaw);
            Assert.Equal(expected.AimPitch, actual.AimPitch);
            Assert.Equal(expected.ViewTick, actual.ViewTick);
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void PlayerInput_Sizes_ArePinned()
    {
        // 29 bytes per command; the largest input packet (3 commands) stays small (D2).
        Assert.Equal(29, PlayerInputPacket.CommandSize);
        Assert.Equal(89, PlayerInputPacket.MaxSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void PlayerInput_InvalidCount_IsRejected(byte count)
    {
        // Enough payload for 4 commands, so only the count rule can reject it.
        var bytes = new byte[2 + PlayerInputPacket.CommandSize * 4];
        bytes[0] = (byte)PacketId.PlayerInput;
        bytes[1] = count;
        var reader = new PacketReader(bytes);
        reader.TryReadPacketId(out _);
        Assert.False(PlayerInputPacket.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 17)]    // a Phase 1 sized command is now one command short
    [InlineData(1, 28)]
    [InlineData(2, 29)]
    [InlineData(3, 86)]
    public void PlayerInput_Truncated_IsRejected(byte count, int payloadBytes)
    {
        var bytes = new byte[2 + payloadBytes];
        bytes[0] = (byte)PacketId.PlayerInput;
        bytes[1] = count;
        var reader = new PacketReader(bytes);
        reader.TryReadPacketId(out _);
        Assert.False(PlayerInputPacket.TryRead(ref reader, out _));
    }

    [Fact]
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
    public void JoinMatchResponse_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        JoinMatchResponse.Write(ref writer, new JoinMatchResponse { Result = JoinResult.Ok, MyEntityId = 5, ServerTick = 99, SimHz = 30, SnapshotHz = 15 });
        var reader = ReaderAfterId(writer.Length, PacketId.JoinMatchResponse);
        Assert.True(JoinMatchResponse.TryRead(ref reader, out var r));
        Assert.Equal(JoinResult.Ok, r.Result);
        Assert.Equal(5, r.MyEntityId);
        Assert.Equal(99u, r.ServerTick);
        Assert.Equal(30, r.SimHz);
        Assert.Equal(15, r.SnapshotHz);
    }

    [Fact]
    public void SpawnAndDespawn_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 3, Position = new Vector3(1, 0, 2), Yaw = 45f });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.True(PlayerSpawned.TryRead(ref reader, out var s));
        Assert.Equal(3, s.EntityId);
        Assert.Equal(new Vector3(1, 0, 2), s.Position);

        writer = new PacketWriter(_buffer);
        PlayerDespawned.Write(ref writer, new PlayerDespawned { EntityId = 3 });
        reader = ReaderAfterId(writer.Length, PacketId.PlayerDespawned);
        Assert.True(PlayerDespawned.TryRead(ref reader, out var d));
        Assert.Equal(3, d.EntityId);
    }

    [Fact]
    public void Snapshot_RoundTrip_AndRecipientPatch()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 7, AckInputSeq = 0, Count = 2 });
        SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = 1, Position = new Vector3(1, 2, 3), VelocityY = -1f, Yaw = 10f, Flags = SnapshotEntity.AliveFlag });
        SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = 2, Position = new Vector3(4, 5, 6), VelocityY = 0f, Yaw = 20f, Flags = 0 });
        Assert.Equal(WorldSnapshotHeader.Size + 2 * SnapshotEntity.Size, writer.Length);

        var self = new SnapshotSelf { Health = 70, Shield = 5, WeaponSlot = 1, Ammo = 3, ReloadRemainingTicks = 300 };
        WorldSnapshotHeader.PatchRecipient(_buffer.AsSpan(0, writer.Length), 42, self);

        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var h));
        Assert.Equal(7u, h.ServerTick);
        Assert.Equal(42u, h.AckInputSeq);
        Assert.Equal(2, h.Count);
        Assert.Equal(70, h.Self.Health);
        Assert.Equal(5, h.Self.Shield);
        Assert.Equal(1, h.Self.WeaponSlot);
        Assert.Equal(3, h.Self.Ammo);
        Assert.Equal(300, h.Self.ReloadRemainingTicks);
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e1));
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e2));
        Assert.Equal(new Vector3(1, 2, 3), e1.Position);
        Assert.Equal(-1f, e1.VelocityY);
        Assert.True(e1.IsAlive);
        Assert.Equal(2, e2.EntityId);
        Assert.False(e2.IsAlive);
        Assert.Equal(0, reader.Remaining);
    }

    // D10: 11 + 6 + 23 * 50 = 1167 bytes must fit one unfragmented datagram (1200).
    [Fact]
    public void Snapshot_WithMaxEntities_Is1167Bytes_AndFitsOneDatagram()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = uint.MaxValue, Count = ProtocolConstants.MaxSnapshotEntities });
        for (int i = 0; i < ProtocolConstants.MaxSnapshotEntities; i++)
        {
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = (ushort)(i + 1),
                Position = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                VelocityY = float.MaxValue,
                Yaw = 359f,
                Flags = SnapshotEntity.AliveFlag,
            });
        }

        Assert.False(writer.Overflowed);
        Assert.Equal(1167, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void Snapshot_CountAboveLimit_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 1, Count = ProtocolConstants.MaxSnapshotEntities + 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void Snapshot_CountLargerThanPayload_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 1, Count = 3 });
        SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void Snapshot_HeaderWithoutSelfBlock_IsRejected()
    {
        // A Phase 1 sized header (11 bytes) must not be read as a v3 header.
        var bytes = new byte[11];
        bytes[0] = (byte)PacketId.WorldSnapshot;
        var reader = new PacketReader(bytes);
        reader.TryReadPacketId(out _);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/CombatPacketTests.cs` (신규):

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class CombatPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static WeaponInfo Weapon(byte id, string name) => new WeaponInfo
    {
        WeaponId = id,
        Name = name,
        Damage = 20,
        FireIntervalTicks = 3,
        MagazineSize = 30,
        ReloadTicks = 60,
        Range = 150f,
        Automatic = true,
    };

    [Fact]
    public void WeaponCatalog_RoundTrip()
    {
        var weapons = new[] { Weapon(1, "Vesper AR"), Weapon(2, "Kestrel LR") };
        weapons[1].Automatic = false;
        weapons[1].Range = 300f;

        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, weapons);
        Assert.False(writer.Overflowed);
        var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.True(WeaponCatalogPacket.TryRead(ref reader, out var read));

        Assert.Equal(2, read.Length);
        Assert.Equal("Vesper AR", read[0].Name);
        Assert.Equal(20, read[0].Damage);
        Assert.Equal(3, read[0].FireIntervalTicks);
        Assert.Equal(30, read[0].MagazineSize);
        Assert.Equal(60, read[0].ReloadTicks);
        Assert.True(read[0].Automatic);
        Assert.Equal(2, read[1].WeaponId);
        Assert.False(read[1].Automatic);
        Assert.Equal(300f, read[1].Range);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void WeaponCatalog_EightMaximumLengthNames_FitsOnePacket()
    {
        var weapons = new WeaponInfo[WeaponCatalogPacket.MaxWeapons];
        for (int i = 0; i < weapons.Length; i++) weapons[i] = Weapon((byte)(i + 1), new string('w', WeaponCatalogPacket.MaxNameBytes));
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, weapons);
        Assert.False(writer.Overflowed);
        Assert.Equal(2 + 8 * 30, writer.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void WeaponCatalog_BadCount_IsRejected(byte count)
    {
        var reader = new PacketReader(new byte[] { count, 0, 0, 0 });
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WeaponCatalog_NonFiniteRange_IsRejected()
    {
        var weapon = Weapon(1, "Bad");
        weapon.Range = float.NaN;
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { weapon });
        var reader = ReaderAfterId(writer.Length, PacketId.WeaponCatalog);
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void WeaponCatalog_Truncated_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        WeaponCatalogPacket.Write(ref writer, new[] { Weapon(1, "Vesper AR") });
        var reader = ReaderAfterId(writer.Length - 1, PacketId.WeaponCatalog);
        Assert.False(WeaponCatalogPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void ShotFired_RoundTrip_AndRejectsNaN()
    {
        var writer = new PacketWriter(_buffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = 4, Start = new Vector3(1, 1.6f, 2), End = new Vector3(1, 1.6f, 40) });
        Assert.Equal(1 + ShotFired.PayloadSize, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.ShotFired);
        Assert.True(ShotFired.TryRead(ref reader, out var shot));
        Assert.Equal(4, shot.ShooterId);
        Assert.Equal(new Vector3(1, 1.6f, 2), shot.Start);
        Assert.Equal(new Vector3(1, 1.6f, 40), shot.End);

        writer = new PacketWriter(_buffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = 4, End = new Vector3(float.NaN, 0, 0) });
        reader = ReaderAfterId(writer.Length, PacketId.ShotFired);
        Assert.False(ShotFired.TryRead(ref reader, out _));
    }

    [Fact]
    public void HitAndDamage_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        HitConfirmed.Write(ref writer, new HitConfirmed { TargetId = 9, Damage = 90, Killed = true });
        var reader = ReaderAfterId(writer.Length, PacketId.HitConfirmed);
        Assert.True(HitConfirmed.TryRead(ref reader, out var hit));
        Assert.Equal(9, hit.TargetId);
        Assert.Equal(90, hit.Damage);
        Assert.True(hit.Killed);

        writer = new PacketWriter(_buffer);
        DamageTaken.Write(ref writer, new DamageTaken { AttackerId = 3, Damage = 20, FromDirection = new Vector3(0, 0, -1) });
        reader = ReaderAfterId(writer.Length, PacketId.DamageTaken);
        Assert.True(DamageTaken.TryRead(ref reader, out var damage));
        Assert.Equal(3, damage.AttackerId);
        Assert.Equal(20, damage.Damage);
        Assert.Equal(new Vector3(0, 0, -1), damage.FromDirection);
    }

    [Fact]
    public void DiedAndRespawned_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = 2, KillerId = 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.True(PlayerDied.TryRead(ref reader, out var died));
        Assert.Equal(2, died.VictimId);
        Assert.Equal(1, died.KillerId);

        writer = new PacketWriter(_buffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = 2, Position = new Vector3(3, 0, 4), Yaw = 90f });
        reader = ReaderAfterId(writer.Length, PacketId.PlayerRespawned);
        Assert.True(PlayerRespawned.TryRead(ref reader, out var respawned));
        Assert.Equal(2, respawned.EntityId);
        Assert.Equal(new Vector3(3, 0, 4), respawned.Position);
        Assert.Equal(90f, respawned.Yaw);
    }

    [Fact]
    public void ShortCombatPackets_AreRejected()
    {
        var empty = new PacketReader(ReadOnlySpan<byte>.Empty);
        Assert.False(HitConfirmed.TryRead(ref empty, out _));
        var shortDamage = new PacketReader(new byte[15]);
        Assert.False(DamageTaken.TryRead(ref shortDamage, out _));
        var shortDied = new PacketReader(new byte[3]);
        Assert.False(PlayerDied.TryRead(ref shortDied, out _));
        var shortRespawn = new PacketReader(new byte[17]);
        Assert.False(PlayerRespawned.TryRead(ref shortRespawn, out _));
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/ProtocolConstantsTests.cs` 전체:

```csharp
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class ProtocolConstantsTests
{
    [Fact]
    public void SnapshotEntityLimit_FitsInOneDatagram()
    {
        // D10: header 11 + self block 6, then 23 bytes per entity.
        Assert.Equal(17, WorldSnapshotHeader.Size);
        Assert.Equal(6, SnapshotSelf.Size);
        Assert.Equal(23, SnapshotEntity.Size);
        Assert.True(WorldSnapshotHeader.Size + ProtocolConstants.MaxSnapshotEntities * SnapshotEntity.Size <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void ProtocolVersion_IsThree()
    {
        // Phase 3 changed the input, snapshot and packet set; v2 clients must be rejected at connect.
        Assert.Equal((ushort)3, ProtocolConstants.ProtocolVersion);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/PacketWriterReaderTests.cs`에서

```csharp
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(255)]
```

를 다음으로 바꾸고,

```csharp
    [InlineData(0)]
    [InlineData(13)]   // one above PacketId.PlayerRespawned
    [InlineData(255)]
```

`PacketId_InRange_IsAccepted` 메서드 전체를 다음으로 바꾼다.

```csharp
    [Theory]
    [InlineData(PacketId.PlayerInput)]
    [InlineData(PacketId.WeaponCatalog)]
    [InlineData(PacketId.PlayerRespawned)]
    public void PacketId_InRange_IsAccepted(PacketId expected)
    {
        var reader = new PacketReader(new[] { (byte)expected });
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
    }
```

`Server/tests/ProjectH.Server.Tests/Integration/ServerIntegrationTests.cs`의 주석 한 줄:

```csharp
        // A 50-entity snapshot is 11 + 22 * 50 = 1111 bytes, sent Sequenced (never fragmented):
```
→
```csharp
        // A 50-entity snapshot is 17 + 23 * 50 = 1167 bytes, sent Sequenced (never fragmented):
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~PacketTests|FullyQualifiedName~ProtocolConstantsTests|FullyQualifiedName~PacketWriterReaderTests"`
Expected: FAIL — `SnapshotSelf`, `WeaponCatalogPacket`, `PlayerInputPacket.CommandSize`, `PacketId.WeaponCatalog` 등 미정의 컴파일 오류

- [ ] **Step 3: 입력 명령과 PacketId**

`Shared/Runtime/Simulation/InputCommand.cs` 전체:

```csharp
using System;

namespace ProjectH.Shared.Simulation
{
    [Flags]
    public enum InputButtons : byte
    {
        None = 0,
        Jump = 1,
        Sprint = 2,
        Fire = 4,
        Reload = 8,
        Slot1 = 16,
        Slot2 = 32,
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

`Shared/Runtime/Protocol/PacketId.cs` 전체:

```csharp
namespace ProjectH.Shared.Protocol
{
    // First byte of every packet. Keep values stable: they are the wire format.
    public enum PacketId : byte
    {
        None = 0,
        JoinMatchRequest = 1,
        JoinMatchResponse = 2,
        PlayerSpawned = 3,
        PlayerDespawned = 4,
        PlayerInput = 5,
        WorldSnapshot = 6,
        WeaponCatalog = 7,
        ShotFired = 8,
        HitConfirmed = 9,
        DamageTaken = 10,
        PlayerDied = 11,
        PlayerRespawned = 12,
    }
}
```

`Shared/Runtime/Protocol/PacketReader.cs`의 `TryReadPacketId` 안:

```csharp
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.WorldSnapshot) return false;
```
→
```csharp
            // Upper bound is the highest id in PacketId; raise it whenever a packet is added.
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.PlayerRespawned) return false;
```

`Shared/Runtime/Protocol/ProtocolConstants.cs`:

```csharp
        public const ushort ProtocolVersion = 2;   // 2: Phase 1 box collision changed movement results
```
→
```csharp
        // 2: Phase 1 box collision changed movement results. 3: Phase 3 combat (aim in inputs, snapshot flags and self block, combat packets).
        public const ushort ProtocolVersion = 3;
```

```csharp
        // (1200 - 11 header bytes) / 22 bytes per entity = 54; 50 leaves headroom.
```
→
```csharp
        // (1200 - 17 header bytes) / 23 bytes per entity = 51; 50 players = 1167 bytes (pinned by PacketTests).
```

- [ ] **Step 4: 입력 패킷 29 B**

`Shared/Runtime/Protocol/ClientPackets.cs` 전체:

```csharp
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Payload of LiteNetLib's connection request (no PacketId: it is not a regular packet).
    public struct ConnectRequestData
    {
        public ushort ProtocolVersion;
        public string DevPlayerId;

        public static void Write(ref PacketWriter writer, in ConnectRequestData data)
        {
            writer.WriteUInt16(data.ProtocolVersion);
            writer.WriteString(data.DevPlayerId, ProtocolConstants.MaxDevPlayerIdBytes);
        }

        public static bool TryRead(ref PacketReader reader, out ConnectRequestData data)
        {
            data = default;
            if (!reader.TryReadUInt16(out data.ProtocolVersion)) return false;
            if (!reader.TryReadString(ProtocolConstants.MaxDevPlayerIdBytes, out string id)) return false;
            if (id.Length == 0) return false;
            data.DevPlayerId = id;
            return true;
        }
    }

    public static class JoinMatchRequest
    {
        public static void Write(ref PacketWriter writer)
        {
            writer.WriteByte((byte)PacketId.JoinMatchRequest);
        }
    }

    // Carries the newest inputs, oldest first. Sent Unreliable: repeating the last few inputs in
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
        public InputCommand Input1;
        public InputCommand Input2;

        public InputCommand Get(int index)
        {
            switch (index)
            {
                case 0: return Input0;
                case 1: return Input1;
                default: return Input2;
            }
        }

        public void Set(int index, in InputCommand command)
        {
            switch (index)
            {
                case 0: Input0 = command; break;
                case 1: Input1 = command; break;
                default: Input2 = command; break;
            }
        }

        public static void Write(ref PacketWriter writer, in PlayerInputPacket packet)
        {
            int count = packet.Count > ProtocolConstants.MaxInputsPerPacket ? ProtocolConstants.MaxInputsPerPacket : packet.Count;
            writer.WriteByte((byte)PacketId.PlayerInput);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                InputCommand c = packet.Get(i);
                writer.WriteUInt32(c.Seq);
                writer.WriteSingle(c.MoveX);
                writer.WriteSingle(c.MoveY);
                writer.WriteSingle(c.Yaw);
                writer.WriteByte((byte)c.Buttons);
                writer.WriteSingle(c.AimYaw);
                writer.WriteSingle(c.AimPitch);
                writer.WriteSingle(c.ViewTick);
            }
        }

        // Only the layout is checked here. Values (NaN aim, huge ViewTick, ...) are checked where they are
        // used: MovementSimulation for movement, the server's combat code for aim and ViewTick (D14).
        public static bool TryRead(ref PacketReader reader, out PlayerInputPacket packet)
        {
            packet = default;
            if (!reader.TryReadByte(out byte count)) return false;
            if (count == 0 || count > ProtocolConstants.MaxInputsPerPacket) return false;
            if (reader.Remaining < count * CommandSize) return false;

            packet.Count = count;
            for (int i = 0; i < count; i++)
            {
                var c = new InputCommand();
                reader.TryReadUInt32(out c.Seq);
                reader.TryReadSingle(out c.MoveX);
                reader.TryReadSingle(out c.MoveY);
                reader.TryReadSingle(out c.Yaw);
                reader.TryReadByte(out byte buttons);
                c.Buttons = (InputButtons)(buttons & KnownButtons);
                reader.TryReadSingle(out c.AimYaw);
                reader.TryReadSingle(out c.AimPitch);
                reader.TryReadSingle(out c.ViewTick);
                packet.Set(i, c);
            }
            return true;
        }
    }
}
```

- [ ] **Step 5: Snapshot 헤더·수신자 블록·엔티티 Flags**

`Shared/Runtime/Protocol/ServerPackets.cs` 전체(`JoinMatchResponse`·`PlayerSpawned`·`PlayerDespawned`는 그대로, 뒤 부분이 바뀐다):

```csharp
using System;
using System.Buffers.Binary;
using System.Numerics;

namespace ProjectH.Shared.Protocol
{
    public struct JoinMatchResponse
    {
        public JoinResult Result;
        public ushort MyEntityId;
        public uint ServerTick;
        public byte SimHz;
        public byte SnapshotHz;

        public static void Write(ref PacketWriter writer, in JoinMatchResponse r)
        {
            writer.WriteByte((byte)PacketId.JoinMatchResponse);
            writer.WriteByte((byte)r.Result);
            writer.WriteUInt16(r.MyEntityId);
            writer.WriteUInt32(r.ServerTick);
            writer.WriteByte(r.SimHz);
            writer.WriteByte(r.SnapshotHz);
        }

        public static bool TryRead(ref PacketReader reader, out JoinMatchResponse r)
        {
            r = default;
            if (reader.Remaining < 9) return false;
            reader.TryReadByte(out byte result);
            r.Result = (JoinResult)result;
            reader.TryReadUInt16(out r.MyEntityId);
            reader.TryReadUInt32(out r.ServerTick);
            reader.TryReadByte(out r.SimHz);
            reader.TryReadByte(out r.SnapshotHz);
            return r.SimHz > 0 && r.SnapshotHz > 0;
        }
    }

    public struct PlayerSpawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;

        public static void Write(ref PacketWriter writer, in PlayerSpawned s)
        {
            writer.WriteByte((byte)PacketId.PlayerSpawned);
            writer.WriteUInt16(s.EntityId);
            writer.WriteVector3(s.Position);
            writer.WriteSingle(s.Yaw);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerSpawned s)
        {
            s = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out s.EntityId);
            reader.TryReadVector3(out s.Position);
            reader.TryReadSingle(out s.Yaw);
            return true;
        }
    }

    public struct PlayerDespawned
    {
        public ushort EntityId;

        public static void Write(ref PacketWriter writer, in PlayerDespawned d)
        {
            writer.WriteByte((byte)PacketId.PlayerDespawned);
            writer.WriteUInt16(d.EntityId);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerDespawned d)
        {
            d = default;
            return reader.TryReadUInt16(out d.EntityId);
        }
    }

    // Layout: [PacketId 1][ServerTick 4][AckInputSeq 4][Count 2][SnapshotSelf 6] then Count x SnapshotEntity.
    // The server writes one payload for everyone and patches AckInputSeq and Self per recipient (D10).
    public struct WorldSnapshotHeader
    {
        public const int Size = 17;
        public const int AckInputSeqOffset = 5;
        public const int SelfOffset = 11;

        public uint ServerTick;
        public uint AckInputSeq;
        public ushort Count;
        public SnapshotSelf Self;

        public static void Write(ref PacketWriter writer, in WorldSnapshotHeader h)
        {
            writer.WriteByte((byte)PacketId.WorldSnapshot);
            writer.WriteUInt32(h.ServerTick);
            writer.WriteUInt32(h.AckInputSeq);
            writer.WriteUInt16(h.Count);
            SnapshotSelf.Write(ref writer, h.Self);
        }

        public static bool TryRead(ref PacketReader reader, out WorldSnapshotHeader h)
        {
            h = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt32(out h.ServerTick);
            reader.TryReadUInt32(out h.AckInputSeq);
            reader.TryReadUInt16(out h.Count);
            SnapshotSelf.TryRead(ref reader, out h.Self);
            if (h.Count > ProtocolConstants.MaxSnapshotEntities) return false;
            return reader.Remaining >= h.Count * SnapshotEntity.Size;
        }

        // packet is the whole written snapshot, starting with its PacketId byte.
        public static void PatchRecipient(Span<byte> packet, uint ackInputSeq, in SnapshotSelf self)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(AckInputSeqOffset, 4), ackInputSeq);
            var writer = new PacketWriter(packet.Slice(SelfOffset, SnapshotSelf.Size));
            SnapshotSelf.Write(ref writer, self);
        }
    }

    // The recipient's own combat values (D10). Sent in every snapshot, so the HUD recovers even when a
    // Reliable combat event is late.
    public struct SnapshotSelf
    {
        public const int Size = 6;

        public byte Health;
        public byte Shield;
        public byte WeaponSlot;             // loadout index: 0 = Slot1, 1 = Slot2
        public byte Ammo;                   // rounds in the current weapon's magazine
        public ushort ReloadRemainingTicks; // 0 = not reloading; at least 1 while a reload is running

        public static void Write(ref PacketWriter writer, in SnapshotSelf s)
        {
            writer.WriteByte(s.Health);
            writer.WriteByte(s.Shield);
            writer.WriteByte(s.WeaponSlot);
            writer.WriteByte(s.Ammo);
            writer.WriteUInt16(s.ReloadRemainingTicks);
        }

        public static bool TryRead(ref PacketReader reader, out SnapshotSelf s)
        {
            s = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadByte(out s.Health);
            reader.TryReadByte(out s.Shield);
            reader.TryReadByte(out s.WeaponSlot);
            reader.TryReadByte(out s.Ammo);
            reader.TryReadUInt16(out s.ReloadRemainingTicks);
            return true;
        }
    }

    public struct SnapshotEntity
    {
        public const int Size = 23; // id 2 + position 12 + velocityY 4 + yaw 4 + flags 1
        public const byte AliveFlag = 1;

        public ushort EntityId;
        public Vector3 Position;
        public float VelocityY;
        public float Yaw;
        public byte Flags;

        public bool IsAlive => (Flags & AliveFlag) != 0;

        public static void Write(ref PacketWriter writer, in SnapshotEntity e)
        {
            writer.WriteUInt16(e.EntityId);
            writer.WriteVector3(e.Position);
            writer.WriteSingle(e.VelocityY);
            writer.WriteSingle(e.Yaw);
            writer.WriteByte(e.Flags);
        }

        public static bool TryRead(ref PacketReader reader, out SnapshotEntity e)
        {
            e = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadUInt16(out e.EntityId);
            reader.TryReadVector3(out e.Position);
            reader.TryReadSingle(out e.VelocityY);
            reader.TryReadSingle(out e.Yaw);
            reader.TryReadByte(out e.Flags);
            return true;
        }
    }
}
```

- [ ] **Step 6: 전투 패킷**

`Shared/Runtime/Protocol/CombatPackets.cs` (신규):

```csharp
using System.Numerics;

namespace ProjectH.Shared.Protocol
{
    // One weapon of the server's catalog (D4). Tick values use the server's SimHz.
    public struct WeaponInfo
    {
        public byte WeaponId;
        public string Name;
        public ushort Damage;
        public ushort FireIntervalTicks;
        public byte MagazineSize;
        public ushort ReloadTicks;
        public float Range;
        public bool Automatic;
    }

    // S->C, ReliableOrdered, once right after a successful Join. Slot1 / Slot2 select entries 0 / 1.
    public static class WeaponCatalogPacket
    {
        public const int MaxWeapons = 8;
        public const int MaxNameBytes = 16;

        // weapons: 1-MaxWeapons entries (the server validates its catalog at startup).
        public static void Write(ref PacketWriter writer, WeaponInfo[] weapons)
        {
            writer.WriteByte((byte)PacketId.WeaponCatalog);
            writer.WriteByte((byte)weapons.Length);
            for (int i = 0; i < weapons.Length; i++)
            {
                WeaponInfo w = weapons[i];
                writer.WriteByte(w.WeaponId);
                writer.WriteString(w.Name, MaxNameBytes);
                writer.WriteUInt16(w.Damage);
                writer.WriteUInt16(w.FireIntervalTicks);
                writer.WriteByte(w.MagazineSize);
                writer.WriteUInt16(w.ReloadTicks);
                writer.WriteSingle(w.Range);
                writer.WriteByte(w.Automatic ? (byte)1 : (byte)0);
            }
        }

        // Allocates the array and the names: read once per join, never on the per-tick path.
        public static bool TryRead(ref PacketReader reader, out WeaponInfo[] weapons)
        {
            weapons = null;
            if (!reader.TryReadByte(out byte count) || count == 0 || count > MaxWeapons) return false;

            var result = new WeaponInfo[count];
            for (int i = 0; i < count; i++)
            {
                var w = new WeaponInfo();
                if (!reader.TryReadByte(out w.WeaponId)) return false;
                if (!reader.TryReadString(MaxNameBytes, out w.Name) || w.Name.Length == 0) return false;
                if (!reader.TryReadUInt16(out w.Damage) || w.Damage == 0) return false;
                if (!reader.TryReadUInt16(out w.FireIntervalTicks) || w.FireIntervalTicks == 0) return false;
                if (!reader.TryReadByte(out w.MagazineSize) || w.MagazineSize == 0) return false;
                if (!reader.TryReadUInt16(out w.ReloadTicks) || w.ReloadTicks == 0) return false;
                if (!reader.TryReadSingle(out w.Range) || !Finite.Check(w.Range) || w.Range <= 0f) return false;
                if (!reader.TryReadByte(out byte automatic) || automatic > 1) return false;
                w.Automatic = automatic == 1;
                result[i] = w;
            }
            weapons = result;
            return true;
        }
    }

    // S->C, Unreliable, to everyone: one processed shot, from the shooter's eye to where it stopped (D11).
    public struct ShotFired
    {
        public const int PayloadSize = 26;

        public ushort ShooterId;
        public Vector3 Start;
        public Vector3 End;

        public static void Write(ref PacketWriter writer, in ShotFired s)
        {
            writer.WriteByte((byte)PacketId.ShotFired);
            writer.WriteUInt16(s.ShooterId);
            writer.WriteVector3(s.Start);
            writer.WriteVector3(s.End);
        }

        public static bool TryRead(ref PacketReader reader, out ShotFired s)
        {
            s = default;
            if (reader.Remaining < PayloadSize) return false;
            reader.TryReadUInt16(out s.ShooterId);
            reader.TryReadVector3(out s.Start);
            reader.TryReadVector3(out s.End);
            return Finite.Check(s.Start) && Finite.Check(s.End);
        }
    }

    // S->C, ReliableOrdered, to the shooter.
    public struct HitConfirmed
    {
        public ushort TargetId;
        public ushort Damage;
        public bool Killed;

        public static void Write(ref PacketWriter writer, in HitConfirmed h)
        {
            writer.WriteByte((byte)PacketId.HitConfirmed);
            writer.WriteUInt16(h.TargetId);
            writer.WriteUInt16(h.Damage);
            writer.WriteByte(h.Killed ? (byte)1 : (byte)0);
        }

        public static bool TryRead(ref PacketReader reader, out HitConfirmed h)
        {
            h = default;
            if (reader.Remaining < 5) return false;
            reader.TryReadUInt16(out h.TargetId);
            reader.TryReadUInt16(out h.Damage);
            reader.TryReadByte(out byte killed);
            h.Killed = killed != 0;
            return true;
        }
    }

    // S->C, ReliableOrdered, to the player who was hit. FromDirection points from the victim towards
    // the attacker (unit length, or zero when they overlap).
    public struct DamageTaken
    {
        public ushort AttackerId;
        public ushort Damage;
        public Vector3 FromDirection;

        public static void Write(ref PacketWriter writer, in DamageTaken d)
        {
            writer.WriteByte((byte)PacketId.DamageTaken);
            writer.WriteUInt16(d.AttackerId);
            writer.WriteUInt16(d.Damage);
            writer.WriteVector3(d.FromDirection);
        }

        public static bool TryRead(ref PacketReader reader, out DamageTaken d)
        {
            d = default;
            if (reader.Remaining < 16) return false;
            reader.TryReadUInt16(out d.AttackerId);
            reader.TryReadUInt16(out d.Damage);
            reader.TryReadVector3(out d.FromDirection);
            return Finite.Check(d.FromDirection);
        }
    }

    // S->C, ReliableOrdered, to everyone. Same channel as PlayerRespawned, so a client always sees a
    // death before the matching respawn.
    public struct PlayerDied
    {
        public ushort VictimId;
        public ushort KillerId;

        public static void Write(ref PacketWriter writer, in PlayerDied d)
        {
            writer.WriteByte((byte)PacketId.PlayerDied);
            writer.WriteUInt16(d.VictimId);
            writer.WriteUInt16(d.KillerId);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerDied d)
        {
            d = default;
            if (reader.Remaining < 4) return false;
            reader.TryReadUInt16(out d.VictimId);
            reader.TryReadUInt16(out d.KillerId);
            return true;
        }
    }

    // S->C, ReliableOrdered, to everyone: the player is alive again at Position with full health,
    // shield and ammo (D9).
    public struct PlayerRespawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;

        public static void Write(ref PacketWriter writer, in PlayerRespawned r)
        {
            writer.WriteByte((byte)PacketId.PlayerRespawned);
            writer.WriteUInt16(r.EntityId);
            writer.WriteVector3(r.Position);
            writer.WriteSingle(r.Yaw);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerRespawned r)
        {
            r = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out r.EntityId);
            reader.TryReadVector3(out r.Position);
            reader.TryReadSingle(out r.Yaw);
            return Finite.Check(r.Position) && Finite.Check(r.Yaw);
        }
    }

    // Values received from the network may be NaN/Infinity; a packet carrying one is rejected.
    internal static class Finite
    {
        public static bool Check(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool Check(Vector3 value) => Check(value.X) && Check(value.Y) && Check(value.Z);
    }
}
```

- [ ] **Step 7: 서버 Snapshot 작성부를 새 형식에 맞춘다**

아직 아무도 죽지 않으므로 생존 비트는 항상 켜고, 수신자 블록은 빈 값으로 둔다(Task 4가 실제 값으로 바꾼다). `Server/src/ProjectH.Server/Game/Match.cs`의 `SendSnapshots()`에서:

```csharp
        // One payload for everyone; only AckInputSeq differs per recipient and is patched in place.
```
→
```csharp
        // One payload for everyone; AckInputSeq and the self block differ per recipient and are patched in place.
```

```csharp
                Yaw = p.State.Yaw,
            });
```
→
```csharp
                Yaw = p.State.Yaw,
                Flags = SnapshotEntity.AliveFlag,
            });
```

```csharp
            WorldSnapshotHeader.PatchAckInputSeq(packet, p.LastProcessedSeq);
```
→
```csharp
            WorldSnapshotHeader.PatchRecipient(packet, p.LastProcessedSeq, default);
```

- [ ] **Step 8: 서버 빌드·테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공(경고 0), 기존 테스트 + 새 프로토콜 테스트 전부 PASS (`FullMatch_SnapshotWithMaxEntities_IsDelivered`가 실제 UDP로 1167 B Snapshot을 전달하는 것도 포함)

- [ ] **Step 9: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0 (Client는 새 필드를 아직 쓰지 않지만 Shared가 바뀌었다)

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 기존 EditMode 테스트 전부 PASS

- [ ] **Step 10: 체크포인트**

`N0`, 두 명령의 PASS 결과를 기록한다. Unity Editor import 확인은 Task 8에서 한 번에 한다. 커밋하지 않는다.

---


### Task 2: 무기 데이터 (`weapons.json`, `WeaponCatalog`, 시작 시 검증, Join 직후 전송)

**Files:**
- Create: `Server/src/ProjectH.Server/weapons.json`
- Create: `Server/src/ProjectH.Server/Game/Combat/WeaponDefinition.cs`, `Server/src/ProjectH.Server/Game/Combat/WeaponCatalog.cs`
- Modify: `Server/src/ProjectH.Server/ProjectH.Server.csproj:25`, `Game/Match.cs` (생성자, `TryJoin`, `SendCatalog`), `GameLoop.cs:40-64`, `GameServerService.cs`
- Test: `Server/tests/ProjectH.Server.Tests/TestWeapons.cs` (신규), `Game/WeaponCatalogTests.cs` (신규), `Game/MatchTests.cs:24`, `Integration/GameLoopPeerTests.cs:27`, `Integration/ServerIntegrationTests.cs:17-23`

**Interfaces:**
- Consumes: `WeaponInfo`, `WeaponCatalogPacket` (Task 1)
- Produces (네임스페이스 `ProjectH.Server.Game.Combat`):
  - `sealed class WeaponDefinition { byte Id; string Name; ushort Damage, FireIntervalTicks; byte MagazineSize; ushort ReloadTicks; float Range; bool Automatic; float Spread, Recoil; WeaponInfo ToWire() }`
  - `sealed class WeaponCatalog { const int SlotCount = 2; int Count; int LoadoutCount; int SimHz; WeaponInfo[] WireInfos; WeaponDefinition this[int]; static bool TryParse(string json, int simHz, out WeaponCatalog? catalog, out string? error); static WeaponCatalog LoadFile(string path, int simHz) /* InvalidOperationException */ }`
  - `Match(ServerOptions options, WeaponCatalog weapons, SendPacket send)` — `weapons.SimHz != options.SimHz`이면 `ArgumentException`
  - `GameLoop(ServerOptions options, WeaponCatalog weapons, ILogger logger)`
  - 테스트용 `internal static class TestWeapons { const int AutoDamage = 30, AutoInterval = 3, AutoMagazine = 6, AutoReload = 30, SemiDamage = 90, SemiInterval = 15, SemiMagazine = 2; static string Json(float autoRange = 100f); static WeaponCatalog Create(int simHz = 30, float autoRange = 100f) }` (네임스페이스 `ProjectH.Server.Tests`)

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/TestWeapons.cs` (신규):

```csharp
using System;
using System.Globalization;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server.Tests;

// In-memory catalog for tests, so no test depends on the shipped weapons.json numbers.
// At 30 Hz: slot 0 "Test Auto" = 30 damage, 3-tick interval, 6 rounds, 30-tick reload;
//           slot 1 "Test Semi" = 90 damage, 15-tick interval, 2 rounds, 60-tick reload.
// 30 damage makes shield 50 + health 100 exactly five hits.
internal static class TestWeapons
{
    public const int AutoDamage = 30;
    public const int AutoInterval = 3;
    public const int AutoMagazine = 6;
    public const int AutoReload = 30;
    public const int SemiDamage = 90;
    public const int SemiInterval = 15;
    public const int SemiMagazine = 2;

    public static string Json(float autoRange = 100f) => $$"""
        {
          "weapons": [
            { "id": 1, "name": "Test Auto", "damage": 30, "fireIntervalSeconds": 0.1, "magazineSize": 6,
              "reloadSeconds": 1.0, "range": {{autoRange.ToString(CultureInfo.InvariantCulture)}}, "automatic": true },
            { "id": 2, "name": "Test Semi", "damage": 90, "fireIntervalSeconds": 0.5, "magazineSize": 2,
              "reloadSeconds": 2.0, "range": 300, "automatic": false }
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

`Server/tests/ProjectH.Server.Tests/Game/WeaponCatalogTests.cs` (신규). 로더는 메모리 문자열로 검사하고, 저장소의 `weapons.json`은 테스트 출력 폴더로 복사된 파일로 D5 수치를 고정한다(서버 프로젝트의 `CopyToOutputDirectory` 항목은 ProjectReference를 통해 테스트 출력에도 복사된다).

```csharp
using System;
using System.IO;
using ProjectH.Server.Game.Combat;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class WeaponCatalogTests
{
    private static string One(string fields) => "{ \"weapons\": [ { " + fields + " } ] }";

    private const string ValidFields =
        "\"id\": 1, \"name\": \"Vesper AR\", \"damage\": 20, \"fireIntervalSeconds\": 0.1, \"magazineSize\": 30, " +
        "\"reloadSeconds\": 2.0, \"range\": 150, \"automatic\": true";

    private static string Parse(string json, int simHz = 30)
    {
        Assert.False(WeaponCatalog.TryParse(json, simHz, out var catalog, out string? error));
        Assert.Null(catalog);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    [Fact]
    public void Valid_ConvertsSecondsToTicks()
    {
        Assert.True(WeaponCatalog.TryParse(TestWeapons.Json(), 30, out var catalog, out string? error), error);
        Assert.Equal(2, catalog!.Count);
        Assert.Equal(2, catalog.LoadoutCount);
        Assert.Equal(30, catalog.SimHz);
        Assert.Equal("Test Auto", catalog[0].Name);
        Assert.Equal(3, catalog[0].FireIntervalTicks);
        Assert.Equal(30, catalog[0].ReloadTicks);
        Assert.Equal(15, catalog[1].FireIntervalTicks);
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
        Assert.Equal(1, catalog.LoadoutCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"weapons\": [] }")]
    [InlineData("{ \"weapons\": [ null ] }")]
    public void EmptyOrMalformed_IsRejected(string json)
    {
        Parse(json);
    }

    [Theory]
    [InlineData("\"damage\": -5")]
    [InlineData("\"damage\": 0")]
    [InlineData("\"damage\": 70000")]
    [InlineData("\"fireIntervalSeconds\": 0")]
    [InlineData("\"fireIntervalSeconds\": -0.1")]
    [InlineData("\"magazineSize\": 0")]
    [InlineData("\"magazineSize\": 256")]
    [InlineData("\"reloadSeconds\": -2")]
    [InlineData("\"reloadSeconds\": 100000")]
    [InlineData("\"range\": 0")]
    [InlineData("\"range\": -1")]
    [InlineData("\"range\": 1e39")]
    [InlineData("\"spread\": -1")]
    [InlineData("\"id\": 0")]
    [InlineData("\"id\": 256")]
    public void BadNumber_IsRejected(string overrideField)
    {
        // A later duplicate key overrides the valid value (System.Text.Json keeps the last one).
        Parse(One(ValidFields + ", " + overrideField));
    }

    [Fact]
    public void MissingField_IsRejected()
    {
        Parse(One("\"id\": 1, \"name\": \"A\", \"damage\": 20, \"magazineSize\": 30, \"reloadSeconds\": 2, \"range\": 150"));
    }

    [Theory]
    [InlineData("12345678901234567")]   // 17 ASCII bytes
    [InlineData("가나다라마바")]           // 6 x 3 = 18 UTF-8 bytes
    [InlineData("")]
    [InlineData("   ")]
    public void BadName_IsRejected(string name)
    {
        Parse(One(ValidFields.Replace("Vesper AR", name)));
    }

    [Fact]
    public void SixteenByteName_IsAccepted()
    {
        Assert.True(WeaponCatalog.TryParse(One(ValidFields.Replace("Vesper AR", "1234567890123456")), 30, out _, out _));
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
        string json = "{ \"weapons\": [ { " + ValidFields + " }, { " + ValidFields.Replace("Vesper AR", "Other") + " } ] }";
        Assert.Contains("duplicate", Parse(json));
    }

    [Fact]
    public void MoreThanEightWeapons_IsRejected()
    {
        var entries = new string[9];
        for (int i = 0; i < 9; i++) entries[i] = "{ " + ValidFields.Replace("\"id\": 1", "\"id\": " + (i + 1)) + " }";
        Parse("{ \"weapons\": [ " + string.Join(", ", entries) + " ] }");
    }

    [Fact]
    public void LoadFile_MissingFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), "projecth-no-such-weapons-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidOperationException>(() => WeaponCatalog.LoadFile(path, 30));
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
        Assert.Equal(30, catalog[0].MagazineSize);
        Assert.Equal(60, catalog[0].ReloadTicks);         // 2.0 s
        Assert.Equal(150f, catalog[0].Range);
        Assert.True(catalog[0].Automatic);

        Assert.Equal("Kestrel LR", catalog[1].Name);
        Assert.Equal(90, catalog[1].Damage);
        Assert.Equal(38, catalog[1].FireIntervalTicks);   // 1.25 s
        Assert.Equal(5, catalog[1].MagazineSize);
        Assert.Equal(75, catalog[1].ReloadTicks);         // 2.5 s
        Assert.Equal(300f, catalog[1].Range);
        Assert.False(catalog[1].Automatic);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/MatchTests.cs`의 생성자:

```csharp
        _match = new Match(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 },
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));
```
→
```csharp
        _match = new Match(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, TestWeapons.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));
```

같은 파일의 `Join_Twice_IsIgnored` 테스트 바로 앞에 추가:

```csharp
    [Fact]
    public void Join_SendsWeaponCatalog_AfterResponse_BeforeSpawns()
    {
        _match.TryJoin(1, "a");

        var toPeer = _sent.Where(s => s.PeerId == 1).ToList();
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

```

`Server/tests/ProjectH.Server.Tests/Integration/GameLoopPeerTests.cs`:

```csharp
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, NullLogger.Instance);
```
→
```csharp
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestWeapons.Create(), NullLogger.Instance);
```

`Server/tests/ProjectH.Server.Tests/Integration/ServerIntegrationTests.cs`의 `StartServer`:

```csharp
            StatsIntervalSeconds = 60,
        }, NullLogger.Instance);
```
→
```csharp
            StatsIntervalSeconds = 60,
        }, TestWeapons.Create(), NullLogger.Instance);
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~WeaponCatalogTests|FullyQualifiedName~MatchTests"`
Expected: FAIL — `ProjectH.Server.Game.Combat` 네임스페이스, `WeaponCatalog`, 3인자 `Match`/`GameLoop` 생성자 없음 컴파일 오류

- [ ] **Step 3: 무기 데이터 파일과 복사 설정**

`Server/src/ProjectH.Server/weapons.json` (신규):

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
      "recoil": 0
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
      "recoil": 0
    }
  ]
}
```

`Server/src/ProjectH.Server/ProjectH.Server.csproj`:

```xml
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
```
→
```xml
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="weapons.json" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 4: `WeaponDefinition`, `WeaponCatalog`**

`Server/src/ProjectH.Server/Game/Combat/WeaponDefinition.cs` (신규):

```csharp
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// One validated weapon from weapons.json. Times are already in simulation ticks. Immutable, shared by
// every player: per-player state (ammo, next fire tick, reload) lives in PlayerEntity.
public sealed class WeaponDefinition
{
    public WeaponDefinition(byte id, string name, ushort damage, ushort fireIntervalTicks, byte magazineSize,
        ushort reloadTicks, float range, bool automatic, float spread, float recoil)
    {
        Id = id;
        Name = name;
        Damage = damage;
        FireIntervalTicks = fireIntervalTicks;
        MagazineSize = magazineSize;
        ReloadTicks = reloadTicks;
        Range = range;
        Automatic = automatic;
        Spread = spread;
        Recoil = recoil;
    }

    public byte Id { get; }
    public string Name { get; }
    public ushort Damage { get; }
    public ushort FireIntervalTicks { get; }
    public byte MagazineSize { get; }
    public ushort ReloadTicks { get; }
    public float Range { get; }
    public bool Automatic { get; }

    // D5: data fields only, always 0 in this phase and not applied by the combat code.
    public float Spread { get; }
    public float Recoil { get; }

    public WeaponInfo ToWire() => new WeaponInfo
    {
        WeaponId = Id,
        Name = Name,
        Damage = Damage,
        FireIntervalTicks = FireIntervalTicks,
        MagazineSize = MagazineSize,
        ReloadTicks = ReloadTicks,
        Range = Range,
        Automatic = Automatic,
    };
}
```

`Server/src/ProjectH.Server/Game/Combat/WeaponCatalog.cs` (신규):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Combat;

// Weapon numbers live in data, not code (D4, request §16). Loaded and validated once at startup; a
// bad file stops the server the same way a bad ServerOptions value does. Immutable afterwards, so the
// game loop reads it without locks.
public sealed class WeaponCatalog
{
    // Slot1 / Slot2 select the first two weapons (D5). Extra entries are valid data but not equipped.
    public const int SlotCount = 2;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly WeaponDefinition[] _weapons;

    private WeaponCatalog(WeaponDefinition[] weapons, int simHz)
    {
        _weapons = weapons;
        SimHz = simHz;
        WireInfos = new WeaponInfo[weapons.Length];
        for (int i = 0; i < weapons.Length; i++) WireInfos[i] = weapons[i].ToWire();
    }

    public int Count => _weapons.Length;
    public int LoadoutCount => Math.Min(_weapons.Length, SlotCount);
    // Tick values were converted with this rate; Match refuses a catalog built for another SimHz.
    public int SimHz { get; }
    // Built once for the WeaponCatalog packet sent at every join.
    public WeaponInfo[] WireInfos { get; }

    public WeaponDefinition this[int index] => _weapons[index];

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

        CatalogJson? root;
        try
        {
            root = JsonSerializer.Deserialize<CatalogJson>(json, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            // Startup only: the exception never happens on the game loop.
            error = "invalid JSON: " + ex.Message;
            return false;
        }

        List<WeaponJson?>? list = root?.Weapons;
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
        if (!TryTicks(w.FireIntervalSeconds, simHz, out ushort fireTicks)) return "fireIntervalSeconds must be positive and finite (at most 65535 ticks).";
        if (!TryTicks(w.ReloadSeconds, simHz, out ushort reloadTicks)) return "reloadSeconds must be positive and finite (at most 65535 ticks).";
        float range = (float)w.Range;
        if (!float.IsFinite(range) || range <= 0f) return "range must be positive and finite.";
        float spread = (float)w.Spread;
        float recoil = (float)w.Recoil;
        if (!float.IsFinite(spread) || spread < 0f) return "spread must be 0 or more.";
        if (!float.IsFinite(recoil) || recoil < 0f) return "recoil must be 0 or more.";

        weapon = new WeaponDefinition((byte)w.Id, w.Name, (ushort)w.Damage, fireTicks, (byte)w.MagazineSize,
            reloadTicks, range, w.Automatic, spread, recoil);
        return null;
    }

    // Seconds -> whole ticks at simHz, at least 1: 1.25 s at 30 Hz = 37.5 -> 38.
    private static bool TryTicks(double seconds, int simHz, out ushort ticks)
    {
        ticks = 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return false;
        double value = Math.Round(seconds * simHz, MidpointRounding.AwayFromZero);
        if (value > ushort.MaxValue) return false;
        ticks = (ushort)Math.Max(1, value);
        return true;
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
    }
}
```

- [ ] **Step 5: Match·GameLoop·시작 경로 연결**

`Server/src/ProjectH.Server/Game/Match.cs`:

```csharp
using LiteNetLib;
using ProjectH.Shared.Protocol;
```
→
```csharp
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
```

```csharp
    private readonly SendPacket _send;
    private readonly int _maxPlayers;
```
→
```csharp
    private readonly SendPacket _send;
    private readonly WeaponCatalog _weapons;
    private readonly int _maxPlayers;
```

```csharp
    public Match(ServerOptions options, SendPacket send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
```
→
```csharp
    public Match(ServerOptions options, WeaponCatalog weapons, SendPacket send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
        if (weapons.SimHz != options.SimHz)
            throw new ArgumentException($"Weapon catalog was built for SimHz {weapons.SimHz}, the match runs at {options.SimHz}.", nameof(weapons));
```

`TryJoin` 안:

```csharp
        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
```
→
```csharp
        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon data before it can show its own weapon (D4).
        SendCatalog(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
```

`SendSpawned` 메서드 바로 앞에 추가:

```csharp
    private void SendCatalog(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

```

`Server/src/ProjectH.Server/GameLoop.cs`:

```csharp
using ProjectH.Server.Game;
using ProjectH.Server.Net;
```
→
```csharp
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Net;
```

```csharp
    public GameLoop(ServerOptions options, ILogger logger)
```
→
```csharp
    public GameLoop(ServerOptions options, WeaponCatalog weapons, ILogger logger)
```

```csharp
        _match = new Match(options, SendToPeer);
```
→
```csharp
        _match = new Match(options, weapons, SendToPeer);
```

`Server/src/ProjectH.Server/GameServerService.cs` 전체:

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

// Bridges the Generic Host lifetime (Ctrl+C, SIGTERM) to the game loop's own thread.
// The host owns this service; this service owns the GameLoop and disposes it.
public sealed class GameServerService : IHostedService, System.IDisposable
{
    private readonly GameLoop _loop;

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger)
    {
        // weapons.json is copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (D4).
        var weapons = WeaponCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "weapons.json"), options.Value.SimHz);
        _loop = new GameLoop(options.Value, weapons, logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Joins the game loop thread (at most one tick) and closes the socket.
        _loop.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _loop.Dispose();
}
```

- [ ] **Step 6: 서버 테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 전체 PASS (`ShippedWeaponsJson_MatchesSpec` 포함. 이 테스트가 "파일 없음"으로 실패하면 `Server/tests/ProjectH.Server.Tests/bin/Debug/net10.0/weapons.json`이 있는지 보고, 없으면 테스트 csproj에 `<None Include="..\..\src\ProjectH.Server\weapons.json" Link="weapons.json" CopyToOutputDirectory="PreserveNewest" />`를 추가한다. 계획 작성 시 스크래치에서는 추가 없이 복사되었다.)

- [ ] **Step 7: 서버 실행 확인 (시간 제한 실행)**

`dotnet run`은 끝나지 않으므로 빌드한 실행 파일을 `timeout`으로 4초만 돌린다.

```bash
dotnet build Server/src/ProjectH.Server
OUT=Server/src/ProjectH.Server/bin/Debug/net10.0
timeout 4 "$OUT/ProjectH.Server.exe" --Server:Port=0 2>&1 | grep "Server listening"
cp "$OUT/weapons.json" "$OUT/weapons.json.bak"
echo '{ "weapons": [] }' > "$OUT/weapons.json"
timeout 4 "$OUT/ProjectH.Server.exe" --Server:Port=0 2>&1 | grep -m1 "Invalid weapon data"
mv "$OUT/weapons.json.bak" "$OUT/weapons.json"
```

Expected: 첫 grep이 `Server listening on UDP ... (SimHz 30, SnapshotHz 15, MaxPlayers 16)`을, 두 번째 grep이 `... Invalid weapon data ...weapons.json: "weapons" must hold 1-8 entries.`를 출력한다(빈 목록이면 서버가 시작하지 않는다). 마지막 줄이 출력 폴더의 파일을 되돌린다.

- [ ] **Step 8: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---


### Task 3: 판정 계산 (slab 광선–AABB, 바닥, 피해·조준 규칙)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Combat/HitScan.cs`, `Server/src/ProjectH.Server/Game/Combat/CombatRules.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Game/HitScanTests.cs` (신규), `Game/CombatRulesTests.cs` (신규)

**Interfaces:**
- Consumes: `Box`, `TestArena.Boxes`, `MoveSettings.HalfWidth/Height` (Shared)
- Produces (네임스페이스 `ProjectH.Server.Game.Combat`):
  - `static class HitScan { static float TraceWorld(Vector3 origin, Vector3 direction, float range, ReadOnlySpan<Box> world); static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance); static bool IntersectAabb(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float maxDistance, out float distance) }`
  - `static class CombatRules { const int MaxHealth = 100, MaxShield = 50; const float EyeHeight = 1.6f, MaxPitch = 89f, RespawnSeconds = 3f; static bool ApplyDamage(ref int health, ref int shield, int damage); static bool TryAimDirection(float yawDegrees, float pitchDegrees, out Vector3 direction); static uint TicksFromSeconds(float seconds, int simHz) }` (Task 5가 `MaxRewindSeconds`와 `ClampViewTick`을 더한다)

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Game/HitScanTests.cs` (신규). spec §5의 광선–AABB 다섯 경우(정면, 비껴감, 내부 시작, 평행, 무한 방향)와 기둥·바닥·사거리·플레이어 상자:

```csharp
using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class HitScanTests
{
    private static readonly Vector3 BoxMin = new(-1f, 0f, 9f);
    private static readonly Vector3 BoxMax = new(1f, 2f, 11f);

    [Fact]
    public void RayAabb_HeadOn_HitsNearFace()
    {
        Assert.True(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out float d));
        Assert.Equal(9f, d, 5);
    }

    [Fact]
    public void RayAabb_PassingBeside_Misses()
    {
        Assert.False(HitScan.IntersectAabb(new Vector3(1.01f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
        var diagonal = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), diagonal, BoxMin, BoxMax, 100f, out _));
    }

    [Fact]
    public void RayAabb_StartingInside_HitsAtZero()
    {
        Assert.True(HitScan.IntersectAabb(new Vector3(0f, 1f, 10f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out float d));
        Assert.Equal(0f, d);
    }

    [Fact]
    public void RayAabb_Parallel_HitsOnlyInsideSlab()
    {
        // Direction has zero X and Y: the X and Y slabs never end, so only the origin decides.
        Assert.True(HitScan.IntersectAabb(new Vector3(0.5f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
        Assert.False(HitScan.IntersectAabb(new Vector3(0.5f, 2.5f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
        // Moving away from the box along a parallel line.
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), -Vector3.UnitZ, BoxMin, BoxMax, 100f, out _));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 1f)]
    [InlineData(0f, float.PositiveInfinity, 1f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void RayAabb_NonFiniteDirection_Misses(float x, float y, float z)
    {
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), new Vector3(x, y, z), BoxMin, BoxMax, 100f, out float d));
        Assert.Equal(0f, d);
    }

    [Fact]
    public void RayAabb_BeyondMaxDistance_Misses()
    {
        Assert.False(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 8.99f, out _));
        Assert.True(HitScan.IntersectAabb(new Vector3(0f, 1f, 0f), Vector3.UnitZ, BoxMin, BoxMax, 9f, out _));
    }

    [Fact]
    public void TraceWorld_StopsAtPillar()
    {
        // Pillar (9, 1.5, 9), 1 x 3 x 1: near face at z = 8.5.
        float d = HitScan.TraceWorld(new Vector3(9f, 1.6f, 6f), Vector3.UnitZ, 100f, TestArena.Boxes);
        Assert.Equal(2.5f, d, 4);
    }

    [Fact]
    public void TraceWorld_DownwardRay_StopsAtFloor()
    {
        var down = Vector3.Normalize(new Vector3(0f, -1f, 1f));
        float d = HitScan.TraceWorld(new Vector3(0f, 1.6f, 0f), down, 100f, TestArena.Boxes);
        Assert.Equal(1.6f * MathF.Sqrt(2f), d, 4);
    }

    [Fact]
    public void TraceWorld_NothingInRange_ReturnsRange()
    {
        // From the centre towards +Z: the low box at z 11..13 is only 1 m high, so a 1.6 m ray passes over it,
        // and the north wall is at z = 19.5, beyond the 10 m range.
        Assert.Equal(10f, HitScan.TraceWorld(new Vector3(0f, 1.6f, 0f), Vector3.UnitZ, 10f, TestArena.Boxes));
    }

    [Fact]
    public void TracePlayer_UsesMovementBox()
    {
        var feet = new Vector3(0f, 0f, 5f);
        Assert.True(HitScan.TracePlayer(new Vector3(0.34f, 1.6f, 0f), Vector3.UnitZ, 100f, feet, out float d));
        Assert.Equal(5f - MoveSettings.HalfWidth, d, 5);
        Assert.False(HitScan.TracePlayer(new Vector3(0.36f, 1.6f, 0f), Vector3.UnitZ, 100f, feet, out _));   // beside
        Assert.False(HitScan.TracePlayer(new Vector3(0f, 1.81f, 0f), Vector3.UnitZ, 100f, feet, out _));     // over the head
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/CombatRulesTests.cs` (신규). 피해 네 경우(Shield만, Shield와 Health, 정확히 0, 초과)와 조준 방향 규약·비유한 입력:

```csharp
using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class CombatRulesTests
{
    [Theory]
    // health, shield, damage -> health, shield, killed
    [InlineData(100, 50, 20, 100, 30, false)]   // shield only
    [InlineData(100, 50, 90, 60, 0, false)]     // shield then health
    [InlineData(100, 50, 150, 0, 0, true)]      // exactly 0
    [InlineData(10, 0, 90, 0, 0, true)]         // overkill stops at 0
    [InlineData(100, 50, 0, 100, 50, false)]    // no damage
    public void ApplyDamage_ShieldFirstThenHealth(int health, int shield, int damage, int expectedHealth, int expectedShield, bool expectedKilled)
    {
        bool killed = CombatRules.ApplyDamage(ref health, ref shield, damage);
        Assert.Equal(expectedHealth, health);
        Assert.Equal(expectedShield, shield);
        Assert.Equal(expectedKilled, killed);
    }

    [Fact]
    public void ApplyDamage_OnDeadPlayer_DoesNotKillAgain()
    {
        int health = 0, shield = 0;
        Assert.False(CombatRules.ApplyDamage(ref health, ref shield, 20));
        Assert.Equal(0, health);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f, 1f)]
    [InlineData(90f, 0f, 1f, 0f, 0f)]
    [InlineData(180f, 0f, 0f, 0f, -1f)]
    [InlineData(450f, 0f, 1f, 0f, 0f)]      // yaw wraps
    [InlineData(-90f, 0f, -1f, 0f, 0f)]
    public void AimDirection_FollowsCameraConvention(float yaw, float pitch, float x, float y, float z)
    {
        Assert.True(CombatRules.TryAimDirection(yaw, pitch, out Vector3 d));
        Assert.Equal(x, d.X, 5);
        Assert.Equal(y, d.Y, 5);
        Assert.Equal(z, d.Z, 5);
    }

    [Fact]
    public void AimDirection_PositivePitchLooksDown_AndIsClampedTo89()
    {
        Assert.True(CombatRules.TryAimDirection(0f, 30f, out Vector3 down));
        Assert.Equal(-0.5f, down.Y, 5);

        Assert.True(CombatRules.TryAimDirection(0f, 1000f, out Vector3 steep));
        Assert.Equal(-MathF.Sin(89f * MathF.PI / 180f), steep.Y, 5);
        Assert.True(steep.Z > 0f);
        Assert.Equal(1f, steep.Length(), 5);
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    public void AimDirection_NonFinite_IsNoShot(float yaw, float pitch)
    {
        Assert.False(CombatRules.TryAimDirection(yaw, pitch, out _));
    }

    [Fact]
    public void AimDirection_HugeFiniteYaw_StaysUnitLength()
    {
        Assert.True(CombatRules.TryAimDirection(3e38f, 0f, out Vector3 d));
        Assert.Equal(1f, d.Length(), 4);
    }

    [Fact]
    public void TicksFromSeconds_RoundsToWholeTicks()
    {
        Assert.Equal(90u, CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, 30));
        Assert.Equal(6u, CombatRules.TicksFromSeconds(0.2f, 30));
        Assert.Equal(1u, CombatRules.TicksFromSeconds(0.001f, 30));
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~HitScanTests|FullyQualifiedName~CombatRulesTests"`
Expected: FAIL — `HitScan`, `CombatRules` 미정의 컴파일 오류

- [ ] **Step 3: 구현**

`Server/src/ProjectH.Server/Game/Combat/HitScan.cs` (신규):

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Hitscan ray math (D7), server only. No allocation, no exceptions, and no infinite or NaN results:
// callers pass a finite unit direction (CombatRules.TryAimDirection), and anything else is a miss.
public static class HitScan
{
    // Below this a direction component counts as parallel to the slab; 1 / d would overflow to infinity.
    private const float ParallelEpsilon = 1e-8f;

    // Distance to the first solid thing along the ray: an arena box or the y = 0 floor. Returns range
    // when nothing is closer.
    public static float TraceWorld(Vector3 origin, Vector3 direction, float range, ReadOnlySpan<Box> world)
    {
        float nearest = range;
        for (int i = 0; i < world.Length; i++)
        {
            ref readonly Box box = ref world[i];
            if (IntersectAabb(origin, direction, box.Min, box.Max, nearest, out float distance)) nearest = distance;
        }
        if (direction.Y < -ParallelEpsilon && origin.Y >= 0f)
        {
            float toFloor = -origin.Y / direction.Y;
            if (toFloor < nearest) nearest = toFloor;
        }
        return nearest;
    }

    // Player hit box: the movement AABB (HalfWidth 0.35 m, Height 1.8 m) with its feet at feet.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance)
    {
        var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
        var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + MoveSettings.Height, feet.Z + MoveSettings.HalfWidth);
        return IntersectAabb(origin, direction, min, max, maxDistance, out distance);
    }

    // Slab test. distance is where the ray enters the box, in [0, maxDistance]; a ray starting inside
    // the box hits at distance 0.
    public static bool IntersectAabb(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float maxDistance, out float distance)
    {
        distance = 0f;
        if (!IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(maxDistance) || maxDistance < 0f) return false;

        float tMin = 0f;
        float tMax = maxDistance;
        if (!Slab(origin.X, direction.X, min.X, max.X, ref tMin, ref tMax)) return false;
        if (!Slab(origin.Y, direction.Y, min.Y, max.Y, ref tMin, ref tMax)) return false;
        if (!Slab(origin.Z, direction.Z, min.Z, max.Z, ref tMin, ref tMax)) return false;
        distance = tMin;
        return true;
    }

    private static bool Slab(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(direction) < ParallelEpsilon)
        {
            // Parallel: inside the slab for the whole ray, or never.
            return origin >= min && origin <= max;
        }

        float inverse = 1f / direction;
        float t1 = (min - origin) * inverse;
        float t2 = (max - origin) * inverse;
        if (t1 > t2)
        {
            float swap = t1;
            t1 = t2;
            t2 = swap;
        }
        if (t1 > tMin) tMin = t1;
        if (t2 < tMax) tMax = t2;
        return tMin <= tMax;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
```

`Server/src/ProjectH.Server/Game/Combat/CombatRules.cs` (신규):

```csharp
using System;
using System.Numerics;

namespace ProjectH.Server.Game.Combat;

// Combat constants and pure rules (D7, D8, D14). Server only: the client never decides a hit.
public static class CombatRules
{
    public const int MaxHealth = 100;   // D8 test values
    public const int MaxShield = 50;
    // Shots start at feet + 1.6 m. The client aims from the same height (AimSolver.EyeHeight).
    public const float EyeHeight = 1.6f;
    public const float MaxPitch = 89f;
    public const float RespawnSeconds = 3f;

    // D8: the shield absorbs first, the rest comes off health (never below 0). Returns true when this
    // damage took health from above 0 to 0.
    public static bool ApplyDamage(ref int health, ref int shield, int damage)
    {
        if (damage <= 0 || health <= 0) return false;
        int absorbed = Math.Min(shield, damage);
        shield -= absorbed;
        health = Math.Max(0, health - (damage - absorbed));
        return health == 0;
    }

    // D14: non-finite angles are no shot. Pitch is clamped to +-89 degrees. Same convention as the
    // client camera (ShoulderCameraMath.Forward): yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down.
    public static bool TryAimDirection(float yawDegrees, float pitchDegrees, out Vector3 direction)
    {
        direction = default;
        if (!float.IsFinite(yawDegrees) || !float.IsFinite(pitchDegrees)) return false;

        // % keeps huge finite yaws in a range where sin/cos stay accurate.
        float yaw = (yawDegrees % 360f) * (MathF.PI / 180f);
        float pitch = Math.Clamp(pitchDegrees, -MaxPitch, MaxPitch) * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitch);
        direction = new Vector3(MathF.Sin(yaw) * cosPitch, -MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch);
        return true;
    }

    // Whole ticks at simHz, at least 1 (3 s at 30 Hz = 90).
    public static uint TicksFromSeconds(float seconds, int simHz)
    {
        return (uint)Math.Max(1, (int)MathF.Round(seconds * simHz, MidpointRounding.AwayFromZero));
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 전체 PASS

- [ ] **Step 5: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---


### Task 4: Match 전투 규칙 (무기 규칙, 발사, 피해, 사망, 부활, 이벤트, Snapshot 수신자 블록)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Combat/WeaponRules.cs`
- Modify: `Server/src/ProjectH.Server/Game/PlayerEntity.cs` (전체 교체), `Server/src/ProjectH.Server/Game/Match.cs` (전체 교체)
- Test: `Server/tests/ProjectH.Server.Tests/TestAim.cs` (신규), `Game/WeaponRulesTests.cs` (신규), `Game/CombatMatchTests.cs` (신규)

**Interfaces:**
- Consumes: `WeaponCatalog`, `WeaponDefinition` (Task 2), `HitScan`, `CombatRules` (Task 3), 전투 패킷·`SnapshotSelf` (Task 1)
- Produces:
  - `PlayerEntity` 필드: `int Health, Shield; bool Alive; uint RespawnAtTick; int WeaponSlot; readonly int[] Ammo; readonly uint[] NextFireTick; bool Reloading; uint ReloadEndTick; bool FireHeld` (배열 길이 `WeaponCatalog.SlotCount`)
  - `static class WeaponRules { static void Equip(PlayerEntity, WeaponCatalog); static void UpdateReload(PlayerEntity, WeaponCatalog, uint now); static bool Apply(PlayerEntity, WeaponCatalog, InputButtons buttons, bool aimValid, uint now) }`
  - `Match.Tick()` 순서: 부활 → 플레이어마다 입력 1개(사망자는 ack만) → 이동 Step → `UpdateReload` → 실제 입력이면 무기 처리·발사 → `ServerTick++` → Snapshot
  - `Match.SpawnPosition(ushort)` (기존, internal)
  - 테스트용 `internal static class TestAim { static void YawPitch(Vector3 feet, Vector3 targetPoint, out float yaw, out float pitch) }` (네임스페이스 `ProjectH.Server.Tests`)

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/TestAim.cs` (신규):

```csharp
using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;

namespace ProjectH.Server.Tests;

// What a client sends: yaw and pitch (camera convention) from the eye of a player standing at feet
// towards a world point. Inverse of CombatRules.TryAimDirection.
internal static class TestAim
{
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, out float yaw, out float pitch)
    {
        Vector3 d = targetPoint - (feet + new Vector3(0f, CombatRules.EyeHeight, 0f));
        yaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        pitch = -MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)) * 180f / MathF.PI;
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/WeaponRulesTests.cs` (신규). spec §5 발사 규칙(간격 전 무시, 탄 0 자동 재장전, 재장전 중 무시, 교체, 단발 버튼 유지):

```csharp
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Rules on one PlayerEntity with the test catalog (TestWeapons): slot 0 auto, 3-tick interval,
// 6 rounds, 30-tick reload; slot 1 semi, 15-tick interval, 2 rounds, 60-tick reload.
public class WeaponRulesTests
{
    private readonly WeaponCatalog _weapons = TestWeapons.Create();
    private readonly PlayerEntity _player = new(1, 1, "a", 8);

    public WeaponRulesTests()
    {
        WeaponRules.Equip(_player, _weapons);
    }

    // One server tick for a living player that sent an input.
    private bool Tick(uint now, InputButtons buttons, bool aimValid = true)
    {
        WeaponRules.UpdateReload(_player, _weapons, now);
        return WeaponRules.Apply(_player, _weapons, buttons, aimValid, now);
    }

    [Fact]
    public void Equip_FillsLoadout()
    {
        Assert.Equal(0, _player.WeaponSlot);
        Assert.Equal(TestWeapons.AutoMagazine, _player.Ammo[0]);
        Assert.Equal(TestWeapons.SemiMagazine, _player.Ammo[1]);
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
        Assert.Equal(TestWeapons.AutoMagazine - 3, _player.Ammo[0]);
    }

    [Fact]
    public void Auto_LastRound_StartsReload_ThatBlocksFireUntilDone()
    {
        uint now = 0;
        for (int i = 0; i < TestWeapons.AutoMagazine; i++, now += TestWeapons.AutoInterval) Assert.True(Tick(now, InputButtons.Fire));
        uint lastShot = now - TestWeapons.AutoInterval;   // 15
        Assert.Equal(0, _player.Ammo[0]);
        Assert.True(_player.Reloading);
        Assert.Equal(lastShot + TestWeapons.AutoReload, _player.ReloadEndTick);

        for (; now < lastShot + TestWeapons.AutoReload; now++) Assert.False(Tick(now, InputButtons.Fire));
        Assert.True(Tick(now, InputButtons.Fire));   // reload finished this tick, then fired
        Assert.Equal(TestWeapons.AutoMagazine - 1, _player.Ammo[0]);
    }

    [Fact]
    public void Semi_HoldingFire_FiresOnce_PressAgainFiresAfterInterval()
    {
        Assert.True(Tick(0, InputButtons.Slot2 | InputButtons.Fire));   // switch, then fire on the press
        Assert.Equal(1, _player.WeaponSlot);
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
    public void ReloadButton_RefillsPartialMagazine_AfterReloadTime()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Assert.False(Tick(1, InputButtons.Reload));
        Assert.True(_player.Reloading);
        Assert.Equal(1u + TestWeapons.AutoReload, _player.ReloadEndTick);
        Assert.False(Tick(10, InputButtons.Fire));   // no fire while reloading

        Tick(1 + TestWeapons.AutoReload, InputButtons.None);
        Assert.False(_player.Reloading);
        Assert.Equal(TestWeapons.AutoMagazine, _player.Ammo[0]);
    }

    [Fact]
    public void ReloadButton_WithFullMagazine_DoesNothing()
    {
        Tick(0, InputButtons.Reload);
        Assert.False(_player.Reloading);
    }

    [Fact]
    public void Switch_CancelsReload_AndKeepsAmmoPerSlot()
    {
        Assert.True(Tick(0, InputButtons.Fire));
        Tick(1, InputButtons.Reload);
        Tick(2, InputButtons.Slot2);
        Assert.Equal(1, _player.WeaponSlot);
        Assert.False(_player.Reloading);

        Tick(3, InputButtons.Slot1);
        Assert.Equal(0, _player.WeaponSlot);
        Assert.Equal(TestWeapons.AutoMagazine - 1, _player.Ammo[0]);   // the cancelled reload did not refill
        Assert.Equal(TestWeapons.SemiMagazine, _player.Ammo[1]);
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

    [Fact]
    public void BothSlotBits_AreIgnored()
    {
        Tick(0, InputButtons.Slot1 | InputButtons.Slot2);
        Assert.Equal(0, _player.WeaponSlot);
        Tick(1, InputButtons.Slot2);
        Tick(2, InputButtons.Slot1 | InputButtons.Slot2);
        Assert.Equal(1, _player.WeaponSlot);
    }

    [Fact]
    public void SlotBeyondLoadout_IsIgnored()
    {
        string json = "{ \"weapons\": [ { \"id\": 1, \"name\": \"Only\", \"damage\": 10, \"fireIntervalSeconds\": 0.1, " +
                      "\"magazineSize\": 3, \"reloadSeconds\": 1, \"range\": 50, \"automatic\": true } ] }";
        Assert.True(WeaponCatalog.TryParse(json, 30, out var single, out _));
        var player = new PlayerEntity(2, 2, "b", 8);
        WeaponRules.Equip(player, single!);

        WeaponRules.Apply(player, single!, InputButtons.Slot2, true, 0);
        Assert.Equal(0, player.WeaponSlot);
        Assert.Equal(0, player.Ammo[1]);
    }

    [Fact]
    public void InvalidAim_DoesNotFire_NorSpendAmmoOrInterval()
    {
        Assert.False(Tick(0, InputButtons.Fire, aimValid: false));
        Assert.Equal(TestWeapons.AutoMagazine, _player.Ammo[0]);
        Assert.True(Tick(1, InputButtons.Fire));
    }

    [Fact]
    public void FireWithEmptyMagazineAndNoReload_StartsReload()
    {
        _player.Ammo[0] = 0;
        Assert.False(Tick(0, InputButtons.Fire));
        Assert.True(_player.Reloading);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/CombatMatchTests.cs` (신규). spec §5 판정(벽, 한 줄의 두 명, 사거리, 자기 자신, 죽은 플레이어), 사망·부활, Review Focus(기둥 뒤 표적, 누락 입력 반복, 잘못된 조준):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Combat through Match.Tick with the test catalog (slot 0: 30 damage, 3-tick interval, 6 rounds).
// Players stand still unless a test moves them; with no input the server repeats a zero move.
public class CombatMatchTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly List<Sent> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private Match _match;

    public CombatMatchTests()
    {
        _match = NewMatch(TestWeapons.Create());
    }

    private Match NewMatch(WeaponCatalog weapons) =>
        new(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, weapons,
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));

    private PlayerEntity Join(int peer, Vector3 feet)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
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

    // An input aimed from the shooter's eye at a world point, rendered at the current tick.
    private void FireAt(PlayerEntity shooter, Vector3 point, InputButtons buttons = InputButtons.Fire)
    {
        TestAim.YawPitch(shooter.State.Position, point, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = buttons, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
    }

    private List<Sent> SentTo(int peer, PacketId id) => _sent.Where(s => s.PeerId == peer && s.Id == id).ToList();

    private static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    private static ShotFired ReadShot(Sent s) { var r = Reader(s); Assert.True(ShotFired.TryRead(ref r, out var v)); return v; }
    private static HitConfirmed ReadHit(Sent s) { var r = Reader(s); Assert.True(HitConfirmed.TryRead(ref r, out var v)); return v; }
    private static DamageTaken ReadDamage(Sent s) { var r = Reader(s); Assert.True(DamageTaken.TryRead(ref r, out var v)); return v; }
    private static PlayerDied ReadDied(Sent s) { var r = Reader(s); Assert.True(PlayerDied.TryRead(ref r, out var v)); return v; }
    private static PlayerRespawned ReadRespawned(Sent s) { var r = Reader(s); Assert.True(PlayerRespawned.TryRead(ref r, out var v)); return v; }

    private (WorldSnapshotHeader header, List<SnapshotEntity> entities) LastSnapshotFor(int peer)
    {
        var reader = Reader(_sent.Last(s => s.PeerId == peer && s.Id == PacketId.WorldSnapshot));
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var header));
        var list = new List<SnapshotEntity>();
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref reader, out var e));
            list.Add(e);
        }
        return (header, list);
    }

    // Five hits of 30 = shield 50 + health 100. The auto weapon fires every 3 ticks.
    private void KillWithFiveHits(PlayerEntity shooter, PlayerEntity target)
    {
        for (int i = 0; i < 5; i++)
        {
            FireAt(shooter, target.State.Position + Chest);
            _match.Tick();
            _match.Tick();
            _match.Tick();
        }
        Assert.False(target.Alive);
    }

    [Fact]
    public void Hit_TakesShieldFirst_AndNotifiesShooterAndTarget()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, b.Shield);

        var hit = ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed)));
        Assert.Equal(b.EntityId, hit.TargetId);
        Assert.Equal(TestWeapons.AutoDamage, hit.Damage);
        Assert.False(hit.Killed);
        Assert.Equal(DeliveryMethod.ReliableOrdered, SentTo(1, PacketId.HitConfirmed)[0].Method);

        var damage = ReadDamage(Assert.Single(SentTo(2, PacketId.DamageTaken)));
        Assert.Equal(a.EntityId, damage.AttackerId);
        Assert.Equal(-1f, damage.FromDirection.Z, 4);   // the attacker is towards -Z

        // Everyone sees the tracer, Unreliable, from the eye to the target's front face.
        foreach (int peer in new[] { 1, 2 })
        {
            var sent = Assert.Single(SentTo(peer, PacketId.ShotFired));
            Assert.Equal(DeliveryMethod.Unreliable, sent.Method);
            var shot = ReadShot(sent);
            Assert.Equal(a.EntityId, shot.ShooterId);
            Assert.Equal(new Vector3(0f, 1.6f, -3f), shot.Start);
            Assert.Equal(3f - MoveSettings.HalfWidth, shot.End.Z, 3);
        }
    }

    // Review Focus: a wall between the eye and the target stops the shot, whatever the client aimed at.
    [Fact]
    public void Shot_AtTargetBehindPillar_HitsThePillar()
    {
        // Pillar (9, 1.5, 9), 1 x 3 x 1: faces at z 8.5 and 9.5. The target stands right behind it.
        var a = Join(1, new Vector3(9f, 0f, 6f));
        var b = Join(2, new Vector3(9f, 0f, 12f));
        _sent.Clear();

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Empty(SentTo(2, PacketId.DamageTaken));
        Assert.Equal(8.5f, ReadShot(SentTo(1, PacketId.ShotFired)[0]).End.Z, 3);
    }

    [Fact]
    public void TwoTargetsInLine_NearerIsHit()
    {
        var a = Join(1, new Vector3(0f, 0f, -4f));
        var near = Join(2, new Vector3(0f, 0f, -1f));
        var far = Join(3, new Vector3(0f, 0f, 3f));

        FireAt(a, far.State.Position + Chest);   // the line passes through the nearer player
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, near.Shield);
        Assert.Equal(CombatRules.MaxShield, far.Shield);
    }

    [Fact]
    public void TargetBeyondRange_IsMissed()
    {
        _match = NewMatch(TestWeapons.Create(autoRange: 5f));
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));   // front face 5.65 m from the eye line

        FireAt(a, b.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield, b.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
    }

    [Fact]
    public void ShooterCannotHitItself()
    {
        var a = Join(1, new Vector3(0f, 0f, 0f));
        FireAt(a, new Vector3(0f, 0f, 0.01f));   // straight down through its own box
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield, a.Shield);
        Assert.Empty(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(0f, ReadShot(SentTo(1, PacketId.ShotFired).Last()).End.Y, 3);   // stopped at the floor
    }

    [Fact]
    public void DeadPlayer_IsNotHit_AndDoesNotBlock()
    {
        var a = Join(1, new Vector3(0f, 0f, -4f));
        var dead = Join(2, new Vector3(0f, 0f, -1f));
        var behind = Join(3, new Vector3(0f, 0f, 3f));
        KillWithFiveHits(a, dead);
        _sent.Clear();

        FireAt(a, behind.State.Position + Chest);
        _match.Tick();

        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, behind.Shield);
        Assert.Equal(behind.EntityId, ReadHit(Assert.Single(SentTo(1, PacketId.HitConfirmed))).TargetId);
    }

    [Fact]
    public void FiveHits_Kill_AndEveryoneIsTold()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        _sent.Clear();

        KillWithFiveHits(a, b);

        Assert.Equal(0, b.Health);
        Assert.Equal(0, b.Shield);
        var hits = SentTo(1, PacketId.HitConfirmed).Select(ReadHit).ToList();
        Assert.Equal(5, hits.Count);
        Assert.Equal(new[] { false, false, false, false, true }, hits.Select(h => h.Killed));
        foreach (int peer in new[] { 1, 2 })
        {
            var died = ReadDied(Assert.Single(SentTo(peer, PacketId.PlayerDied)));
            Assert.Equal(b.EntityId, died.VictimId);
            Assert.Equal(a.EntityId, died.KillerId);
        }
        // The victim hears the last DamageTaken before its PlayerDied (same ReliableOrdered channel).
        var toB = _sent.Where(s => s.PeerId == 2 && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
        Assert.True(toB.LastIndexOf(PacketId.DamageTaken) < toB.IndexOf(PacketId.PlayerDied));

        _match.Tick();   // the kill took 15 ticks; tick 16 sends a snapshot
        var (header, entities) = LastSnapshotFor(2);
        Assert.Equal(0, header.Self.Health);
        Assert.False(entities.Single(e => e.EntityId == b.EntityId).IsAlive);
        Assert.True(entities.Single(e => e.EntityId == a.EntityId).IsAlive);
    }

    [Fact]
    public void DeadPlayer_InputIsAcked_ButMovesAndFiresNothing()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        KillWithFiveHits(a, b);
        Vector3 body = b.State.Position;
        _sent.Clear();

        for (int i = 0; i < 5; i++)
        {
            TestAim.YawPitch(b.State.Position, a.State.Position + Chest, out float yaw, out float pitch);
            Send(b, new InputCommand { MoveY = 1f, Buttons = InputButtons.Fire | InputButtons.Jump, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
            _match.Tick();
        }

        Assert.Equal(body, b.State.Position);
        Assert.Equal(_seq[2], b.LastProcessedSeq);
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.ShotFired);
        Assert.Equal(CombatRules.MaxShield, a.Shield);
    }

    [Fact]
    public void Respawn_ThreeSecondsLater_AtSpawnWithFullState()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));
        b.Ammo[0] = 1;
        KillWithFiveHits(a, b);
        uint diedAt = b.RespawnAtTick - 90;   // 3 s at 30 Hz

        while (_match.ServerTick < diedAt + 90)
        {
            Assert.False(b.Alive);
            _match.Tick();
        }
        Assert.False(b.Alive);   // the respawn happens at the start of the tick where now == RespawnAtTick
        _match.Tick();

        Assert.True(b.Alive);
        Assert.Equal(Match.SpawnPosition(b.EntityId), b.State.Position);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(CombatRules.MaxShield, b.Shield);
        Assert.Equal(TestWeapons.AutoMagazine, b.Ammo[0]);
        Assert.Equal(TestWeapons.SemiMagazine, b.Ammo[1]);
        Assert.False(b.Reloading);

        foreach (int peer in new[] { 1, 2 })
        {
            var respawned = ReadRespawned(Assert.Single(SentTo(peer, PacketId.PlayerRespawned)));
            Assert.Equal(b.EntityId, respawned.EntityId);
            Assert.Equal(Match.SpawnPosition(b.EntityId), respawned.Position);
            var reliable = _sent.Where(s => s.PeerId == peer && s.Method == DeliveryMethod.ReliableOrdered).Select(s => s.Id).ToList();
            Assert.True(reliable.IndexOf(PacketId.PlayerDied) < reliable.IndexOf(PacketId.PlayerRespawned));
        }
    }

    // Review Focus: the server repeats the last input while inputs are missing (grace window).
    // That repeat must never fire, or a held trigger would keep shooting after the client let go.
    [Fact]
    public void MissedInputTicks_DoNotRepeatFire()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        FireAt(a, b.State.Position + Chest);
        for (int i = 0; i < 12; i++) _match.Tick();   // one real input, then 11 repeated ticks

        Assert.Single(SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(TestWeapons.AutoMagazine - 1, a.Ammo[0]);
    }

    // Spec §5: a flood of fire inputs (one per tick, as fast as the server takes them) is limited to the interval.
    [Fact]
    public void FireEveryTick_IsLimitedToWeaponInterval()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        for (int i = 0; i < 9; i++)
        {
            FireAt(a, b.State.Position + Chest);
            _match.Tick();
        }

        Assert.Equal(3, SentTo(1, PacketId.ShotFired).Count);
    }

    // Review Focus: malformed aim from a client is no shot and costs nothing; an out-of-range pitch is clamped.
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    public void NonFiniteAim_IsNoShot_AndAmmoUnchanged(float yaw, float pitch)
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        Join(2, new Vector3(0f, 0f, 3f));

        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.Tick();

        Assert.Empty(SentTo(1, PacketId.ShotFired));
        Assert.Equal(TestWeapons.AutoMagazine, a.Ammo[0]);
        Assert.Equal(0u, a.NextFireTick[0]);
    }

    [Fact]
    public void HugePitch_IsClampedTo89_AndShotStaysFinite()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimYaw = 1e30f, AimPitch = 1e30f, ViewTick = _match.ServerTick });
        _match.Tick();

        var shot = ReadShot(Assert.Single(SentTo(1, PacketId.ShotFired)));
        Assert.True(float.IsFinite(shot.End.X) && float.IsFinite(shot.End.Z));
        Assert.Equal(0f, shot.End.Y, 3);   // 89 degrees down hits the floor right in front
    }

    [Fact]
    public void Snapshot_CarriesEachRecipientsOwnCombatValues()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        var b = Join(2, new Vector3(0f, 0f, 3f));

        FireAt(a, b.State.Position + Chest);
        _match.Tick();
        Send(b, new InputCommand { Buttons = InputButtons.Slot2 });
        _match.Tick();

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

    [Fact]
    public void Snapshot_ReloadRemaining_IsAtLeastOneUntilTheReloadEnds()
    {
        var a = Join(1, new Vector3(0f, 0f, -3f));
        Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f, ViewTick = _match.ServerTick });
        _match.Tick();
        Send(a, new InputCommand { Buttons = InputButtons.Reload, ViewTick = _match.ServerTick });
        _match.Tick();   // reload starts at now = 1, ends at 31

        while (a.Reloading)
        {
            if (_match.ServerTick % 2 == 1)   // the next Tick sends a snapshot
            {
                _match.Tick();
                var (header, _) = LastSnapshotFor(1);
                if (a.Reloading) Assert.True(header.Self.ReloadRemainingTicks >= 1);
            }
            else
            {
                _match.Tick();
            }
        }
        _match.Tick();
        _match.Tick();
        Assert.Equal(0, LastSnapshotFor(1).header.Self.ReloadRemainingTicks);
        Assert.Equal(TestWeapons.AutoMagazine, LastSnapshotFor(1).header.Self.Ammo);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~WeaponRulesTests|FullyQualifiedName~CombatMatchTests"`
Expected: FAIL — `WeaponRules`, `PlayerEntity.Health/Shield/Alive/Ammo/...` 미정의 컴파일 오류

- [ ] **Step 3: `PlayerEntity` 전투 상태**

`Server/src/ProjectH.Server/Game/PlayerEntity.cs` 전체:

```csharp
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// One joined player. Owned by Match on the game loop thread.
public sealed class PlayerEntity
{
    public PlayerEntity(ushort entityId, int peerId, string devPlayerId, int inputCapacity)
    {
        EntityId = entityId;
        PeerId = peerId;
        DevPlayerId = devPlayerId;
        Inputs = new PlayerInputBuffer(inputCapacity);
    }

    public ushort EntityId { get; }
    public int PeerId { get; }
    public string DevPlayerId { get; }
    public PlayerInputBuffer Inputs { get; }

    // Fields, not properties, so MovementSimulation can step State by ref without copies.
    public MoveState State;
    public InputCommand LastInput;
    public uint LastProcessedSeq;
    // Consecutive ticks without a buffered input, reset when one is taken. Match stops counting at
    // SimHz / 2 (the repeat grace window), so it cannot overflow on a long-silent connection.
    public int MissedTicks;

    // Combat (Phase 3). Set by Match.ResetCombat at join and respawn; game loop thread only.
    public int Health;
    public int Shield;
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
    public bool FireHeld;
}
```

- [ ] **Step 4: `WeaponRules`**

`Server/src/ProjectH.Server/Game/Combat/WeaponRules.cs` (신규):

```csharp
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Combat;

// Magazine, reload, fire interval and weapon switch (D14), enforced in server ticks. Pure state
// changes on PlayerEntity: Match decides when to call them and does the ray and the packets.
// The client copies these rules for presentation (Client WeaponState); keep the two in step.
public static class WeaponRules
{
    // Full magazines, slot 0, nothing pending. Used at join and at respawn.
    public static void Equip(PlayerEntity player, WeaponCatalog weapons)
    {
        player.WeaponSlot = 0;
        for (int slot = 0; slot < WeaponCatalog.SlotCount; slot++)
        {
            player.Ammo[slot] = slot < weapons.LoadoutCount ? weapons[slot].MagazineSize : 0;
            player.NextFireTick[slot] = 0;
        }
        player.Reloading = false;
        player.ReloadEndTick = 0;
        player.FireHeld = false;
    }

    // Runs every tick for a living player, whether or not an input arrived.
    public static void UpdateReload(PlayerEntity player, WeaponCatalog weapons, uint now)
    {
        if (player.Reloading && now >= player.ReloadEndTick)
        {
            player.Reloading = false;
            player.Ammo[player.WeaponSlot] = weapons[player.WeaponSlot].MagazineSize;
        }
    }

    // One input the client sent, in the order switch -> reload -> fire. Returns true when it fires a
    // shot; ammo and the fire interval are already spent then. aimValid false (non-finite aim) is no shot.
    public static bool Apply(PlayerEntity player, WeaponCatalog weapons, InputButtons buttons, bool aimValid, uint now)
    {
        bool slot1 = (buttons & InputButtons.Slot1) != 0;
        bool slot2 = (buttons & InputButtons.Slot2) != 0;
        if (slot1 != slot2)   // both bits at once is contradictory and ignored
        {
            int target = slot1 ? 0 : 1;
            if (target < weapons.LoadoutCount && target != player.WeaponSlot)
            {
                player.WeaponSlot = target;
                player.Reloading = false;   // a reload belongs to the weapon being put away
            }
        }

        int current = player.WeaponSlot;
        WeaponDefinition weapon = weapons[current];

        if ((buttons & InputButtons.Reload) != 0 && !player.Reloading && player.Ammo[current] < weapon.MagazineSize)
            StartReload(player, weapon, now);

        bool fireHeld = (buttons & InputButtons.Fire) != 0;
        bool trigger = fireHeld && (weapon.Automatic || !player.FireHeld);
        player.FireHeld = fireHeld;
        if (!trigger || !aimValid || player.Reloading || now < player.NextFireTick[current]) return false;

        if (player.Ammo[current] == 0)
        {
            StartReload(player, weapon, now);
            return false;
        }

        player.Ammo[current]--;
        player.NextFireTick[current] = now + weapon.FireIntervalTicks;
        if (player.Ammo[current] == 0) StartReload(player, weapon, now);
        return true;
    }

    private static void StartReload(PlayerEntity player, WeaponDefinition weapon, uint now)
    {
        player.Reloading = true;
        player.ReloadEndTick = now + weapon.ReloadTicks;
    }
}
```

- [ ] **Step 5: `Match` 전투 흐름**

`Server/src/ProjectH.Server/Game/Match.cs` 전체. Phase 0 입력 규칙(`TakeInput`: 한 Tick에 1개, 누락 시 SimHz/2 동안 점프 없는 반복, 그 뒤 정지)은 그대로 옮기고, 반복 입력은 발사하지 않도록 "실제로 받은 입력인지"를 돌려준다. 모든 전송은 `_sendBuffer` 하나에 써서 `_send`로 넘긴다(`NetPeer.Send`가 복사하므로 할당 없음).

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

public delegate void SendPacket(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method);

// All player state of one match. Game loop thread only: the network thread never touches it,
// so nothing here takes a lock.
public sealed class Match
{
    private const float SpawnRadius = 5f;

    private readonly Dictionary<int, PlayerEntity> _playersByPeer = new();
    // Players are removed only in Leave(); both collections are updated together.
    private readonly List<PlayerEntity> _players = new();
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly SendPacket _send;
    private readonly WeaponCatalog _weapons;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
    private readonly byte _simHz;
    private readonly byte _snapshotHz;
    private readonly float _tickSeconds;
    private readonly uint _respawnTicks;
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
        _simHz = (byte)options.SimHz;
        _snapshotHz = options.SnapshotHz;
        _tickSeconds = 1f / options.SimHz;
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
    }

    public uint ServerTick { get; private set; }
    public int PlayerCount => _players.Count;

    public long TotalBufferDrops
    {
        get
        {
            long total = 0;
            foreach (var player in _players) total += player.Inputs.DroppedCount;
            return total;
        }
    }

    public bool TryGetPlayer(int peerId, out PlayerEntity player) => _playersByPeer.TryGetValue(peerId, out player!);

    public JoinResult TryJoin(int peerId, string devPlayerId)
    {
        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
        if (_players.Count >= _maxPlayers)
        {
            SendJoinResponse(peerId, JoinResult.MatchFull, 0);
            return JoinResult.MatchFull;
        }

        var player = new PlayerEntity(AllocateEntityId(), peerId, devPlayerId, _inputCapacity);
        player.State.Position = SpawnPosition(player.EntityId);
        ResetCombat(player);
        _playersByPeer.Add(peerId, player);
        _players.Add(player);

        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon data before it can show its own weapon (D4).
        SendCatalog(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
        {
            if (other != player) SendSpawned(other.PeerId, player);
        }
        return JoinResult.Ok;
    }

    public void Leave(int peerId)
    {
        if (!_playersByPeer.Remove(peerId, out var player)) return;
        _players.Remove(player);

        var writer = new PacketWriter(_sendBuffer);
        PlayerDespawned.Write(ref writer, new PlayerDespawned { EntityId = player.EntityId });
        foreach (var other in _players) _send(other.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    public void EnqueueInput(int peerId, in PlayerInputPacket packet)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        for (int i = 0; i < packet.Count; i++) player.Inputs.Add(packet.Get(i));
    }

    public void Tick()
    {
        // now = the last completed tick; this call simulates tick now + 1. Weapon timers
        // (NextFireTick, ReloadEndTick, RespawnAtTick) are compared against it.
        uint now = ServerTick;

        foreach (var player in _players)
        {
            if (!player.Alive && now >= player.RespawnAtTick) Respawn(player);
        }

        foreach (var player in _players)
        {
            bool sent = TakeInput(player, out InputCommand input);
            // D9: a dead player's input is still taken and acked (LastProcessedSeq) but moves and fires nothing.
            // A player killed earlier in this loop is already dead here.
            if (!player.Alive) continue;

            // Same boxes as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, TestArena.Boxes);
            WeaponRules.UpdateReload(player, _weapons, now);
            // Only an input the client really sent can fire, switch or reload: the missed-input repeat
            // must never invent shots.
            if (sent) ProcessWeapons(player, input, now);
        }

        ServerTick++;
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }

    // One buffered input per tick (Phase 0). Returns false when the input is made up by the server.
    private bool TakeInput(PlayerEntity player, out InputCommand input)
    {
        if (player.Inputs.TryTake(out input))
        {
            player.LastInput = input;
            player.LastProcessedSeq = input.Seq;
            player.MissedTicks = 0;
            return true;
        }
        if (player.MissedTicks < _simHz / 2)
        {
            player.MissedTicks++;
            // No input arrived in time: keep moving the same way, but never repeat a jump.
            // The ack does not advance, so the client replays its own input over this.
            input = player.LastInput;
            input.Buttons &= ~InputButtons.Jump;
            return false;
        }
        // Input missing for over half a second (paused or backgrounded client): stop walking
        // instead of repeating the last move until the disconnect timeout. Gravity still applies.
        input = new InputCommand { Seq = player.LastInput.Seq, Yaw = player.LastInput.Yaw };
        return false;
    }

    private void ProcessWeapons(PlayerEntity shooter, in InputCommand input, uint now)
    {
        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(shooter, _weapons, input.Buttons, aimValid, now))
            FireShot(shooter, direction);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    private void FireShot(PlayerEntity shooter, Vector3 direction)
    {
        WeaponDefinition weapon = _weapons[shooter.WeaponSlot];
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, TestArena.Boxes);

        PlayerEntity? target = null;
        foreach (var other in _players)
        {
            if (other == shooter || !other.Alive) continue;
            if (HitScan.TracePlayer(origin, direction, nearest, other.State.Position, out float distance) &&
                (target == null || distance < nearest))
            {
                nearest = distance;
                target = other;
            }
        }

        var writer = new PacketWriter(_sendBuffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = shooter.EntityId, Start = origin, End = origin + direction * nearest });
        Broadcast(writer.WrittenSpan, DeliveryMethod.Unreliable);

        if (target != null) ApplyHit(shooter, target, weapon.Damage);
    }

    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
    {
        bool killed = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);

        var writer = new PacketWriter(_sendBuffer);
        HitConfirmed.Write(ref writer, new HitConfirmed { TargetId = target.EntityId, Damage = damage, Killed = killed });
        _send(shooter.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        Vector3 toAttacker = shooter.State.Position - target.State.Position;
        float length = toAttacker.Length();
        writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken
        {
            AttackerId = shooter.EntityId,
            Damage = damage,
            FromDirection = length > 1e-4f ? toAttacker / length : Vector3.Zero,
        });
        _send(target.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        if (killed) Kill(target, shooter);
    }

    // D9: death is decided here. The same ReliableOrdered channel carries DamageTaken, PlayerDied and
    // PlayerRespawned, so every client sees them in that order.
    private void Kill(PlayerEntity victim, PlayerEntity killer)
    {
        victim.Alive = false;
        victim.RespawnAtTick = ServerTick + _respawnTicks;

        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = victim.EntityId, KillerId = killer.EntityId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void Respawn(PlayerEntity player)
    {
        // Keep the yaw so the camera does not snap; everything else starts over at the spawn point.
        player.State = new MoveState { Position = SpawnPosition(player.EntityId), Yaw = player.State.Yaw };
        ResetCombat(player);

        var writer = new PacketWriter(_sendBuffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void ResetCombat(PlayerEntity player)
    {
        player.Alive = true;
        player.Health = CombatRules.MaxHealth;
        player.Shield = CombatRules.MaxShield;
        WeaponRules.Equip(player, _weapons);
    }

    private void Broadcast(ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        foreach (var p in _players) _send(p.PeerId, data, method);
    }

    private void SendSnapshots()
    {
        if (_players.Count == 0) return;

        // One payload for everyone; AckInputSeq and the self block differ per recipient and are patched in place.
        var writer = new PacketWriter(_sendBuffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = ServerTick, AckInputSeq = 0, Count = (ushort)_players.Count });
        foreach (var p in _players)
        {
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = p.EntityId,
                Position = p.State.Position,
                VelocityY = p.State.VelocityY,
                Yaw = p.State.Yaw,
                Flags = p.Alive ? SnapshotEntity.AliveFlag : (byte)0,
            });
        }
        // Cannot overflow: ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities (17 + 23 * 50 = 1167 bytes).
        if (writer.Overflowed) return;

        Span<byte> packet = _sendBuffer.AsSpan(0, writer.Length);
        foreach (var p in _players)
        {
            WorldSnapshotHeader.PatchRecipient(packet, p.LastProcessedSeq, SelfBlock(p));
            _send(p.PeerId, packet, DeliveryMethod.Sequenced);
        }
    }

    private SnapshotSelf SelfBlock(PlayerEntity p)
    {
        // While a reload runs the remaining time is reported as at least 1, even on its last tick: 0 means
        // "not reloading" to the client, and reporting 0 early would make it re-sync at every reload end.
        ushort reloadRemaining = 0;
        if (p.Reloading)
            reloadRemaining = (ushort)Math.Clamp(p.ReloadEndTick > ServerTick ? p.ReloadEndTick - ServerTick : 1u, 1u, ushort.MaxValue);

        return new SnapshotSelf
        {
            Health = (byte)Math.Clamp(p.Health, 0, byte.MaxValue),
            Shield = (byte)Math.Clamp(p.Shield, 0, byte.MaxValue),
            WeaponSlot = (byte)p.WeaponSlot,
            Ammo = (byte)p.Ammo[p.WeaponSlot],
            ReloadRemainingTicks = reloadRemaining,
        };
    }

    private void SendJoinResponse(int peerId, JoinResult result, ushort entityId)
    {
        var writer = new PacketWriter(_sendBuffer);
        JoinMatchResponse.Write(ref writer, new JoinMatchResponse
        {
            Result = result,
            MyEntityId = entityId,
            ServerTick = ServerTick,
            SimHz = _simHz,
            SnapshotHz = _snapshotHz,
        });
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendCatalog(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
        _send(recipientPeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Entity ids are ushort and 0 means "none". With at most 50 players a free id is always found.
    private ushort AllocateEntityId()
    {
        while (_nextEntityId == 0 || IsEntityIdInUse(_nextEntityId)) _nextEntityId++;
        return _nextEntityId++;
    }

    private bool IsEntityIdInUse(ushort id)
    {
        foreach (var p in _players)
        {
            if (p.EntityId == id) return true;
        }
        return false;
    }

    // Spread players on a circle (golden angle) so they do not spawn inside each other.
    // The 5 m ring lies inside TestArena.ClearRadius (checked by TestArenaTests).
    internal static Vector3 SpawnPosition(ushort entityId)
    {
        float angle = entityId * 2.39996f;
        return new Vector3(MathF.Cos(angle) * SpawnRadius, 0f, MathF.Sin(angle) * SpawnRadius);
    }
}
```

- [ ] **Step 6: 테스트 통과 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 전체 PASS (기존 `MatchTests`의 유예·중복·Snapshot 테스트 포함)

- [ ] **Step 7: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---


### Task 5: Lag Compensation (위치 History 32칸, ViewTick 되감기, 할당 없음 고정)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Combat/PositionHistory.cs`
- Modify: `Server/src/ProjectH.Server/Game/Combat/CombatRules.cs`, `Game/PlayerEntity.cs`, `Game/Match.cs` (필드, 생성자, `TryJoin`, `Tick`, `ProcessWeapons`, `FireShot`, `Respawn`)
- Test: `Server/tests/ProjectH.Server.Tests/Game/PositionHistoryTests.cs` (신규), `Game/LagCompensationTests.cs` (신규), `Game/CombatRulesTests.cs`, `Game/CombatMatchTests.cs` (`Join` 도우미)

**Interfaces:**
- Consumes: Task 4의 `Match`, `PlayerEntity`, `CombatRules`
- Produces:
  - `sealed class PositionHistory { const int Capacity = 32; int Count; void Reset(uint tick, Vector3 position); void Record(uint tick, Vector3 position); Vector3 Sample(double tick) }`
  - `PlayerEntity.History` (`readonly PositionHistory`)
  - `CombatRules.MaxRewindSeconds = 0.2f`, `static double CombatRules.ClampViewTick(float viewTick, uint latestTick, int maxRewindTicks)`
  - Match: Join·부활 때 `History.Reset(ServerTick, 위치)`, 매 Tick `ServerTick++` 직후 모든 플레이어 `History.Record(ServerTick, 위치)`, 발사 판정은 다른 플레이어를 `History.Sample(ClampViewTick(input.ViewTick, ServerTick, 6))` 위치로 본다. 쏜 사람과 아레나는 되감지 않는다.

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Game/PositionHistoryTests.cs` (신규):

```csharp
using System.Numerics;
using ProjectH.Server.Game.Combat;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class PositionHistoryTests
{
    private static Vector3 X(float x) => new(x, 0f, 0f);

    [Fact]
    public void Sample_BetweenTicks_Interpolates()
    {
        var history = new PositionHistory();
        history.Reset(10, X(0f));
        history.Record(11, X(2f));

        Assert.Equal(1f, history.Sample(10.5).X, 5);
        Assert.Equal(2f, history.Sample(11).X, 5);
    }

    [Fact]
    public void Sample_PastNewest_HoldsNewest()
    {
        var history = new PositionHistory();
        history.Reset(10, X(0f));
        history.Record(11, X(2f));
        Assert.Equal(2f, history.Sample(50).X);
    }

    // Spec §5: not enough history -> the oldest record.
    [Fact]
    public void Sample_BeforeOldest_UsesOldest()
    {
        var history = new PositionHistory();
        history.Reset(10, X(5f));
        history.Record(11, X(6f));
        Assert.Equal(5f, history.Sample(3).X);
    }

    [Fact]
    public void Ring_KeepsNewest32_AndNeverGrows()
    {
        var history = new PositionHistory();
        history.Reset(1, X(1f));
        for (uint t = 2; t <= 100; t++) history.Record(t, X(t));

        Assert.Equal(PositionHistory.Capacity, history.Count);
        Assert.Equal(69f, history.Sample(0).X);    // oldest kept: tick 100 - 31
        Assert.Equal(100f, history.Sample(100).X);
    }

    [Fact]
    public void Reset_ForgetsOlderPositions()
    {
        var history = new PositionHistory();
        history.Reset(1, X(1f));
        for (uint t = 2; t <= 10; t++) history.Record(t, X(t));
        history.Reset(10, X(-7f));   // respawn: teleport

        Assert.Equal(1, history.Count);
        Assert.Equal(-7f, history.Sample(5).X);
    }

    [Fact]
    public void RepeatedOrOlderTick_IsIgnored()
    {
        var history = new PositionHistory();
        history.Reset(10, X(1f));
        history.Record(10, X(99f));
        history.Record(9, X(99f));
        Assert.Equal(1, history.Count);
        Assert.Equal(1f, history.Sample(10).X);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/LagCompensationTests.cs` (신규). 움직이는 대상을 ViewTick 위치로 쏘면 맞고, 상한을 넘는 ViewTick은 잘리며(spec §5), 조작된 ViewTick(Review Focus)과 부활 직후 되감기, 발사 Tick의 할당 0을 고정한다:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// D6. The target sprints along +X at 7 m/s (0.233 m per tick) across the shooter's view, so its
// positions 4 ticks apart are 0.93 m apart: more than the 0.35 m half-width of its hit box. A shot
// aimed where the target was at tick T only hits if the server rewinds the target to T.
public class LagCompensationTests
{
    private const int MovedTicks = 12;
    private static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    private readonly Match _match;
    private readonly PlayerEntity _shooter;
    private readonly PlayerEntity _target;
    private readonly Dictionary<uint, Vector3> _targetAt = new();
    private uint _shooterSeq;
    private uint _targetSeq;
    private readonly uint _latest;

    public LagCompensationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 2 }, TestWeapons.Create(), (_, _, _) => { });
        _match.TryJoin(1, "shooter");
        _match.TryJoin(2, "target");
        _match.TryGetPlayer(1, out _shooter);
        _match.TryGetPlayer(2, out _target);
        _shooter.State.Position = new Vector3(0f, 0f, -6f);
        _target.State.Position = new Vector3(-2f, 0f, 0f);
        _shooter.History.Reset(_match.ServerTick, _shooter.State.Position);
        _target.History.Reset(_match.ServerTick, _target.State.Position);

        for (int i = 0; i < MovedTicks; i++)
        {
            MoveTarget();
            _match.Tick();
            _targetAt[_match.ServerTick] = _target.State.Position;
        }
        _latest = _match.ServerTick;
    }

    private void MoveTarget()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_targetSeq, MoveX = 1f, Buttons = InputButtons.Sprint });
        _match.EnqueueInput(2, packet);
    }

    // Fires once, aimed at where the target was at aimTick, claiming the shooter saw tick viewTick.
    private bool Shoot(float viewTick, uint aimTick)
    {
        Vector3 aimPoint = _targetAt[aimTick] + Chest;
        TestAim.YawPitch(_shooter.State.Position, aimPoint, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = ++_shooterSeq, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = viewTick });
        _match.EnqueueInput(1, packet);
        MoveTarget();   // the target keeps running during the shot's tick
        int shieldBefore = _target.Shield;
        _match.Tick();
        return _target.Shield < shieldBefore;
    }

    [Fact]
    public void Setup_TargetReallyMoves()
    {
        Assert.True(Vector3.Distance(_targetAt[_latest], _targetAt[_latest - 4]) > 0.9f);
    }

    [Theory]
    [InlineData(-4, -4, true)]    // aimed where it was seen: hit (spec §5)
    [InlineData(0, -4, false)]    // same aim, but the client claims it saw "now": no rewind, miss
    [InlineData(0, 0, true)]
    [InlineData(-6, -6, true)]    // the limit itself
    [InlineData(-10, -10, false)] // older than 6 ticks: clamped to -6, where the target was 0.93 m away (spec §5)
    [InlineData(-10, -6, true)]
    public void Shot_RewindsTargetToViewTick(int viewOffset, int aimOffset, bool expectedHit)
    {
        Assert.Equal(expectedHit, Shoot(_latest + viewOffset, (uint)(_latest + aimOffset)));
    }

    // Review Focus: ViewTick comes from the client. NaN, a far future tick or a huge past must not rewind
    // further than 6 ticks, and must not crash or reach outside the history ring.
    [Theory]
    [InlineData(float.NaN, 0, true)]
    [InlineData(float.NaN, -4, false)]
    [InlineData(float.PositiveInfinity, 0, true)]
    [InlineData(1e9f, 0, true)]
    [InlineData(1e9f, -4, false)]
    [InlineData(-1e9f, -6, true)]
    [InlineData(float.NegativeInfinity, -6, true)]
    [InlineData(float.NegativeInfinity, -10, false)]
    public void UntrustedViewTick_IsClampedToTheLastSixTicks(float viewTick, int aimOffset, bool expectedHit)
    {
        Assert.Equal(expectedHit, Shoot(viewTick, (uint)(_latest + aimOffset)));
    }

    // The shooter's own position is never rewound: the shot starts at its current eye.
    [Fact]
    public void Shooter_FiresFromCurrentPosition()
    {
        var shots = new List<Vector3>();
        var match = new Match(new ServerOptions { MaxPlayers = 1 }, TestWeapons.Create(), (_, data, _) =>
        {
            var reader = new PacketReader(data);
            if (reader.TryReadPacketId(out PacketId id) && id == PacketId.ShotFired && ShotFired.TryRead(ref reader, out var shot))
                shots.Add(shot.Start);
        });
        match.TryJoin(1, "a");
        match.TryGetPlayer(1, out var a);
        a.State.Position = new Vector3(1f, 0f, 1f);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Fire, ViewTick = -1e9f });
        match.EnqueueInput(1, packet);
        match.Tick();

        Assert.Equal(new Vector3(1f, 1.6f, 1f), Assert.Single(shots));
    }

    // A respawn is a teleport. A rewind into the ticks before it must find the spawn point, not the body.
    [Fact]
    public void AfterRespawn_RewindFindsSpawnPoint_NotTheBody()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestWeapons.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
        match.TryGetPlayer(2, out var target);

        // Put the target down far away by hand; the kill itself is covered by CombatMatchTests.
        var body = new Vector3(10f, 0f, -10f);
        target.State.Position = body;
        target.History.Reset(match.ServerTick, body);
        target.Alive = false;
        target.RespawnAtTick = match.ServerTick + 5;
        while (!target.Alive) match.Tick();
        Vector3 spawn = Match.SpawnPosition(target.EntityId);
        Assert.Equal(spawn, target.State.Position);

        TestAim.YawPitch(shooter.State.Position, spawn + Chest, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = match.ServerTick - 6 });
        match.EnqueueInput(1, packet);
        match.Tick();

        Assert.Equal(CombatRules.MaxShield - TestWeapons.AutoDamage, target.Shield);
    }

    // Server hot path: a tick that fires, rewinds, hits and sends allocates nothing (no per-shot garbage).
    [Fact]
    public void FiringTick_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = 2 }, TestWeapons.Create(), static (_, _, _) => { });
        match.TryJoin(1, "shooter");
        match.TryJoin(2, "target");
        match.TryGetPlayer(1, out var shooter);
        match.TryGetPlayer(2, out var target);
        TestAim.YawPitch(shooter.State.Position, target.State.Position + Chest, out float yaw, out float pitch);
        var packet = new PlayerInputPacket { Count = 1 };

        long allocated = 0;
        for (uint shot = 1; shot <= 4; shot++)   // shots 1-3 warm up JIT and first-use paths; 4 is measured
        {
            packet.Set(0, new InputCommand { Seq = shot, Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = match.ServerTick });
            match.EnqueueInput(1, packet);
            int before = target.Shield + target.Health;

            long start = GC.GetAllocatedBytesForCurrentThread();
            match.Tick();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.True(target.Shield + target.Health < before, "every measured tick must include a hit");
            match.Tick();
            match.Tick();
        }
        Assert.Equal(0, allocated);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Game/CombatRulesTests.cs`에서 `TicksFromSeconds_RoundsToWholeTicks` 테스트 바로 앞에 추가:

```csharp
    [Theory]
    [InlineData(100f, 100.0)]
    [InlineData(97.5f, 97.5)]
    [InlineData(94f, 94.0)]              // the 6-tick limit itself
    [InlineData(10f, 94.0)]              // too old
    [InlineData(-5f, 94.0)]
    [InlineData(1e9f, 100.0)]            // future
    [InlineData(float.PositiveInfinity, 100.0)]
    [InlineData(float.NegativeInfinity, 94.0)]
    [InlineData(float.NaN, 100.0)]       // no usable claim: no rewind
    public void ClampViewTick_KeepsTheRewindWithinSixTicks(float viewTick, double expected)
    {
        Assert.Equal(expected, CombatRules.ClampViewTick(viewTick, 100u, 6));
    }

    [Fact]
    public void ClampViewTick_NearMatchStart_NeverGoesBelowZero()
    {
        Assert.Equal(0.0, CombatRules.ClampViewTick(-3f, 2u, 6));
    }

```

`Server/tests/ProjectH.Server.Tests/Game/CombatMatchTests.cs`의 `Join` 도우미. 이제 표적 위치를 History에서 읽으므로, 테스트가 옮긴 위치를 History에도 알린다:

```csharp
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        return player;
```
→
```csharp
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        // Shots rewind targets through their history (lag compensation), so the history must see the move too.
        player.History.Reset(_match.ServerTick, feet);
        return player;
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~PositionHistoryTests|FullyQualifiedName~LagCompensationTests|FullyQualifiedName~CombatRulesTests"`
Expected: FAIL — `PositionHistory`, `PlayerEntity.History`, `CombatRules.ClampViewTick` 미정의 컴파일 오류

- [ ] **Step 3: `PositionHistory`**

`Server/src/ProjectH.Server/Game/Combat/PositionHistory.cs` (신규):

```csharp
using System.Numerics;

namespace ProjectH.Server.Game.Combat;

// Fixed ring of one player's recent feet positions for lag compensation (D6, request §19). Never grows:
// Record overwrites the oldest of Capacity entries. Game loop thread only.
public sealed class PositionHistory
{
    // About 1 s at 30 Hz. Must stay above the rewind limit (MaxRewindSeconds * SimHz = 26 ticks at 128 Hz).
    public const int Capacity = 32;

    private readonly uint[] _ticks = new uint[Capacity];
    private readonly Vector3[] _positions = new Vector3[Capacity];
    private int _count;
    private int _newest = -1;

    public int Count => _count;

    // Forget everything and start from one known position (join, respawn): a rewind must never reach a
    // position from before a teleport.
    public void Reset(uint tick, Vector3 position)
    {
        _count = 0;
        _newest = -1;
        Record(tick, position);
    }

    // Ticks must increase; a repeated or older tick is ignored.
    public void Record(uint tick, Vector3 position)
    {
        if (_count > 0 && tick <= _ticks[_newest]) return;
        _newest = (_newest + 1) % Capacity;
        _ticks[_newest] = tick;
        _positions[_newest] = position;
        if (_count < Capacity) _count++;
    }

    // Position at a (fractional) tick, interpolated between the two records around it. Past the newest
    // record it holds the newest; before the oldest it uses the oldest (short history, spec §5).
    public Vector3 Sample(double tick)
    {
        if (_count == 0) return Vector3.Zero;   // not reached: Match resets the history at join

        for (int i = 0; i < _count; i++)
        {
            int index = (_newest - i + Capacity) % Capacity;
            if (_ticks[index] > tick) continue;
            if (i == 0) return _positions[index];

            int next = (index + 1) % Capacity;
            float t = (float)((tick - _ticks[index]) / (_ticks[next] - _ticks[index]));
            return Vector3.Lerp(_positions[index], _positions[next], t);
        }

        int oldest = (_newest - _count + 1 + Capacity) % Capacity;
        return _positions[oldest];
    }
}
```

`Server/src/ProjectH.Server/Game/PlayerEntity.cs`의 끝:

```csharp
    // Fire bit of the previous input the client sent: a semi-automatic weapon fires on the press only.
    public bool FireHeld;
}
```
→
```csharp
    // Fire bit of the previous input the client sent: a semi-automatic weapon fires on the press only.
    public bool FireHeld;

    // Feet position at the end of each recent tick, for rewinding this player as a target (D6).
    public readonly PositionHistory History = new();
}
```

- [ ] **Step 4: 되감기 규칙**

`Server/src/ProjectH.Server/Game/Combat/CombatRules.cs`:

```csharp
    public const float RespawnSeconds = 3f;
```
→
```csharp
    public const float RespawnSeconds = 3f;
    // D6: a shot may rewind other players by at most this much (6 ticks at 30 Hz).
    public const float MaxRewindSeconds = 0.2f;
```

`TicksFromSeconds` 바로 앞(주석 `// Whole ticks at simHz, ...` 위)에 추가:

```csharp
    // D14: the tick a shot rewinds targets to. The client's ViewTick is untrusted: NaN means "now", and
    // anything outside [latestTick - maxRewindTicks, latestTick] is clamped into it (never below tick 0).
    public static double ClampViewTick(float viewTick, uint latestTick, int maxRewindTicks)
    {
        double latest = latestTick;
        if (float.IsNaN(viewTick)) return latest;
        double oldest = latestTick > (uint)maxRewindTicks ? latestTick - (uint)maxRewindTicks : 0u;
        if (viewTick > latest) return latest;
        if (viewTick < oldest) return oldest;
        return viewTick;
    }

```

- [ ] **Step 5: Match에 기록과 되감기를 넣는다**

`Server/src/ProjectH.Server/Game/Match.cs`에서 차례로 바꾼다.

필드:

```csharp
    private readonly uint _respawnTicks;
```
→
```csharp
    private readonly uint _respawnTicks;
    private readonly int _maxRewindTicks;
```

생성자 끝:

```csharp
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
```
→
```csharp
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
        _maxRewindTicks = (int)CombatRules.TicksFromSeconds(CombatRules.MaxRewindSeconds, options.SimHz);
```

`TryJoin`:

```csharp
        player.State.Position = SpawnPosition(player.EntityId);
        ResetCombat(player);
        _playersByPeer.Add(peerId, player);
```
→
```csharp
        player.State.Position = SpawnPosition(player.EntityId);
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position);
        _playersByPeer.Add(peerId, player);
```

`Tick()` 끝:

```csharp
        ServerTick++;
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }
```
→
```csharp
        ServerTick++;
        // After every move of this tick, so all players are recorded at the same moment. A snapshot with
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position);
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }
```

`ProcessWeapons`:

```csharp
            FireShot(shooter, direction);
    }
```
→
```csharp
            FireShot(shooter, direction, input.ViewTick);
    }
```

`FireShot` 머리:

```csharp
    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    private void FireShot(PlayerEntity shooter, Vector3 direction)
    {
        WeaponDefinition weapon = _weapons[shooter.WeaponSlot];
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, TestArena.Boxes);
```
→
```csharp
    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    // D6: other players are tested where the shooter saw them, at ViewTick (clamped to the last
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        WeaponDefinition weapon = _weapons[shooter.WeaponSlot];
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, TestArena.Boxes);
        double rewindTick = CombatRules.ClampViewTick(viewTick, ServerTick, _maxRewindTicks);
```

`FireShot`의 플레이어 반복:

```csharp
            if (HitScan.TracePlayer(origin, direction, nearest, other.State.Position, out float distance) &&
```
→
```csharp
            Vector3 feet = other.History.Sample(rewindTick);
            if (HitScan.TracePlayer(origin, direction, nearest, feet, out float distance) &&
```

`Respawn`:

```csharp
        player.State = new MoveState { Position = SpawnPosition(player.EntityId), Yaw = player.State.Yaw };
        ResetCombat(player);

```
→
```csharp
        player.State = new MoveState { Position = SpawnPosition(player.EntityId), Yaw = player.State.Yaw };
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position);

```

바꾼 뒤 `Match.cs` 전체는 다음과 같다(확인용).

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

public delegate void SendPacket(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method);

// All player state of one match. Game loop thread only: the network thread never touches it,
// so nothing here takes a lock.
public sealed class Match
{
    private const float SpawnRadius = 5f;

    private readonly Dictionary<int, PlayerEntity> _playersByPeer = new();
    // Players are removed only in Leave(); both collections are updated together.
    private readonly List<PlayerEntity> _players = new();
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly SendPacket _send;
    private readonly WeaponCatalog _weapons;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
    private readonly byte _simHz;
    private readonly byte _snapshotHz;
    private readonly float _tickSeconds;
    private readonly uint _respawnTicks;
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
        _simHz = (byte)options.SimHz;
        _snapshotHz = options.SnapshotHz;
        _tickSeconds = 1f / options.SimHz;
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
        _maxRewindTicks = (int)CombatRules.TicksFromSeconds(CombatRules.MaxRewindSeconds, options.SimHz);
    }

    public uint ServerTick { get; private set; }
    public int PlayerCount => _players.Count;

    public long TotalBufferDrops
    {
        get
        {
            long total = 0;
            foreach (var player in _players) total += player.Inputs.DroppedCount;
            return total;
        }
    }

    public bool TryGetPlayer(int peerId, out PlayerEntity player) => _playersByPeer.TryGetValue(peerId, out player!);

    public JoinResult TryJoin(int peerId, string devPlayerId)
    {
        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
        if (_players.Count >= _maxPlayers)
        {
            SendJoinResponse(peerId, JoinResult.MatchFull, 0);
            return JoinResult.MatchFull;
        }

        var player = new PlayerEntity(AllocateEntityId(), peerId, devPlayerId, _inputCapacity);
        player.State.Position = SpawnPosition(player.EntityId);
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position);
        _playersByPeer.Add(peerId, player);
        _players.Add(player);

        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon data before it can show its own weapon (D4).
        SendCatalog(peerId);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
        {
            if (other != player) SendSpawned(other.PeerId, player);
        }
        return JoinResult.Ok;
    }

    public void Leave(int peerId)
    {
        if (!_playersByPeer.Remove(peerId, out var player)) return;
        _players.Remove(player);

        var writer = new PacketWriter(_sendBuffer);
        PlayerDespawned.Write(ref writer, new PlayerDespawned { EntityId = player.EntityId });
        foreach (var other in _players) _send(other.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    public void EnqueueInput(int peerId, in PlayerInputPacket packet)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        for (int i = 0; i < packet.Count; i++) player.Inputs.Add(packet.Get(i));
    }

    public void Tick()
    {
        // now = the last completed tick; this call simulates tick now + 1. Weapon timers
        // (NextFireTick, ReloadEndTick, RespawnAtTick) are compared against it.
        uint now = ServerTick;

        foreach (var player in _players)
        {
            if (!player.Alive && now >= player.RespawnAtTick) Respawn(player);
        }

        foreach (var player in _players)
        {
            bool sent = TakeInput(player, out InputCommand input);
            // D9: a dead player's input is still taken and acked (LastProcessedSeq) but moves and fires nothing.
            // A player killed earlier in this loop is already dead here.
            if (!player.Alive) continue;

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
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }

    // One buffered input per tick (Phase 0). Returns false when the input is made up by the server.
    private bool TakeInput(PlayerEntity player, out InputCommand input)
    {
        if (player.Inputs.TryTake(out input))
        {
            player.LastInput = input;
            player.LastProcessedSeq = input.Seq;
            player.MissedTicks = 0;
            return true;
        }
        if (player.MissedTicks < _simHz / 2)
        {
            player.MissedTicks++;
            // No input arrived in time: keep moving the same way, but never repeat a jump.
            // The ack does not advance, so the client replays its own input over this.
            input = player.LastInput;
            input.Buttons &= ~InputButtons.Jump;
            return false;
        }
        // Input missing for over half a second (paused or backgrounded client): stop walking
        // instead of repeating the last move until the disconnect timeout. Gravity still applies.
        input = new InputCommand { Seq = player.LastInput.Seq, Yaw = player.LastInput.Yaw };
        return false;
    }

    private void ProcessWeapons(PlayerEntity shooter, in InputCommand input, uint now)
    {
        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        if (WeaponRules.Apply(shooter, _weapons, input.Buttons, aimValid, now))
            FireShot(shooter, direction, input.ViewTick);
    }

    // D7: from the eye along the aim, the nearest arena surface or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    // D6: other players are tested where the shooter saw them, at ViewTick (clamped to the last
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        WeaponDefinition weapon = _weapons[shooter.WeaponSlot];
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, TestArena.Boxes);
        double rewindTick = CombatRules.ClampViewTick(viewTick, ServerTick, _maxRewindTicks);

        PlayerEntity? target = null;
        foreach (var other in _players)
        {
            if (other == shooter || !other.Alive) continue;
            Vector3 feet = other.History.Sample(rewindTick);
            if (HitScan.TracePlayer(origin, direction, nearest, feet, out float distance) &&
                (target == null || distance < nearest))
            {
                nearest = distance;
                target = other;
            }
        }

        var writer = new PacketWriter(_sendBuffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = shooter.EntityId, Start = origin, End = origin + direction * nearest });
        Broadcast(writer.WrittenSpan, DeliveryMethod.Unreliable);

        if (target != null) ApplyHit(shooter, target, weapon.Damage);
    }

    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
    {
        bool killed = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);

        var writer = new PacketWriter(_sendBuffer);
        HitConfirmed.Write(ref writer, new HitConfirmed { TargetId = target.EntityId, Damage = damage, Killed = killed });
        _send(shooter.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        Vector3 toAttacker = shooter.State.Position - target.State.Position;
        float length = toAttacker.Length();
        writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken
        {
            AttackerId = shooter.EntityId,
            Damage = damage,
            FromDirection = length > 1e-4f ? toAttacker / length : Vector3.Zero,
        });
        _send(target.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        if (killed) Kill(target, shooter);
    }

    // D9: death is decided here. The same ReliableOrdered channel carries DamageTaken, PlayerDied and
    // PlayerRespawned, so every client sees them in that order.
    private void Kill(PlayerEntity victim, PlayerEntity killer)
    {
        victim.Alive = false;
        victim.RespawnAtTick = ServerTick + _respawnTicks;

        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = victim.EntityId, KillerId = killer.EntityId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void Respawn(PlayerEntity player)
    {
        // Keep the yaw so the camera does not snap; everything else starts over at the spawn point.
        player.State = new MoveState { Position = SpawnPosition(player.EntityId), Yaw = player.State.Yaw };
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position);

        var writer = new PacketWriter(_sendBuffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void ResetCombat(PlayerEntity player)
    {
        player.Alive = true;
        player.Health = CombatRules.MaxHealth;
        player.Shield = CombatRules.MaxShield;
        WeaponRules.Equip(player, _weapons);
    }

    private void Broadcast(ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        foreach (var p in _players) _send(p.PeerId, data, method);
    }

    private void SendSnapshots()
    {
        if (_players.Count == 0) return;

        // One payload for everyone; AckInputSeq and the self block differ per recipient and are patched in place.
        var writer = new PacketWriter(_sendBuffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = ServerTick, AckInputSeq = 0, Count = (ushort)_players.Count });
        foreach (var p in _players)
        {
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = p.EntityId,
                Position = p.State.Position,
                VelocityY = p.State.VelocityY,
                Yaw = p.State.Yaw,
                Flags = p.Alive ? SnapshotEntity.AliveFlag : (byte)0,
            });
        }
        // Cannot overflow: ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities (17 + 23 * 50 = 1167 bytes).
        if (writer.Overflowed) return;

        Span<byte> packet = _sendBuffer.AsSpan(0, writer.Length);
        foreach (var p in _players)
        {
            WorldSnapshotHeader.PatchRecipient(packet, p.LastProcessedSeq, SelfBlock(p));
            _send(p.PeerId, packet, DeliveryMethod.Sequenced);
        }
    }

    private SnapshotSelf SelfBlock(PlayerEntity p)
    {
        // While a reload runs the remaining time is reported as at least 1, even on its last tick: 0 means
        // "not reloading" to the client, and reporting 0 early would make it re-sync at every reload end.
        ushort reloadRemaining = 0;
        if (p.Reloading)
            reloadRemaining = (ushort)Math.Clamp(p.ReloadEndTick > ServerTick ? p.ReloadEndTick - ServerTick : 1u, 1u, ushort.MaxValue);

        return new SnapshotSelf
        {
            Health = (byte)Math.Clamp(p.Health, 0, byte.MaxValue),
            Shield = (byte)Math.Clamp(p.Shield, 0, byte.MaxValue),
            WeaponSlot = (byte)p.WeaponSlot,
            Ammo = (byte)p.Ammo[p.WeaponSlot],
            ReloadRemainingTicks = reloadRemaining,
        };
    }

    private void SendJoinResponse(int peerId, JoinResult result, ushort entityId)
    {
        var writer = new PacketWriter(_sendBuffer);
        JoinMatchResponse.Write(ref writer, new JoinMatchResponse
        {
            Result = result,
            MyEntityId = entityId,
            ServerTick = ServerTick,
            SimHz = _simHz,
            SnapshotHz = _snapshotHz,
        });
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendCatalog(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
        _send(recipientPeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Entity ids are ushort and 0 means "none". With at most 50 players a free id is always found.
    private ushort AllocateEntityId()
    {
        while (_nextEntityId == 0 || IsEntityIdInUse(_nextEntityId)) _nextEntityId++;
        return _nextEntityId++;
    }

    private bool IsEntityIdInUse(ushort id)
    {
        foreach (var p in _players)
        {
            if (p.EntityId == id) return true;
        }
        return false;
    }

    // Spread players on a circle (golden angle) so they do not spawn inside each other.
    // The 5 m ring lies inside TestArena.ClearRadius (checked by TestArenaTests).
    internal static Vector3 SpawnPosition(ushort entityId)
    {
        float angle = entityId * 2.39996f;
        return new Vector3(MathF.Cos(angle) * SpawnRadius, 0f, MathF.Sin(angle) * SpawnRadius);
    }
}
```

- [ ] **Step 6: 테스트 통과 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 전체 PASS. `FiringTick_AllocatesNothing`은 발사·되감기·명중·전송이 있는 Tick의 `GC.GetAllocatedBytesForCurrentThread()` 차이가 0이어야 통과한다(계획 작성 시 스크래치에서 0 바이트). `AfterRespawn_RewindFindsSpawnPoint_NotTheBody`는 `Respawn`의 `History.Reset`을 빼면 실패하고, `MissedInputTicks_DoNotRepeatFire`는 `if (sent)` 조건을 빼면 실패함을 스크래치에서 확인했다.

- [ ] **Step 7: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---


### Task 6: 네트워크 통합 (HeadlessClient 새 패킷, 두 Client 사격 통합 테스트)

**Files:**
- Modify: `Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs` (전체 교체)
- Create: `Server/tests/ProjectH.Server.Tests/Integration/CombatIntegrationTests.cs`

서버 코드(`NetworkListener`)는 바꾸지 않는다: 새 PacketId 7–12는 서버→클라이언트 전용이라 클라이언트가 보내면 기존 `default` 분기가 잘못된 패킷으로 센다. 입력은 여전히 `PlayerInputPacket.TryRead`의 개수·길이 검사와 peer별 초당 `SimHz × 2` 상한을 거친다.

**Interfaces:**
- Consumes: Task 1–5 전부 (실제 UDP, `GameLoop.Start`)
- Produces (테스트 전용): `HeadlessClient.SendInput(InputCommand)`(Seq는 내부에서 붙임), `Weapons`, `LastSelf`, `SelfHistory`, `LastServerTick`, `Shots`, `Hits`, `DamageEvents`, `Deaths`, `Respawns`. 기존 `SendMove`, `LastSnapshot`, `LastAckInputSeq`, `SnapshotsReceived`는 그대로.

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Integration/CombatIntegrationTests.cs` (신규). spec §5 흐름: A가 B를 조준해 쏜다 → B가 `DamageTaken`을 받고 수신자 블록의 Health가 준다 → A가 `HitConfirmed`를 받는다 → 5발이면 B 사망, 두 Client 모두 `PlayerDied` → 약 3초 뒤 `PlayerRespawned`.

```csharp
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Integration;

// Spec §5: two headless clients over real UDP. The server runs the test catalog, whose automatic
// weapon does 30 damage every 3 ticks, so five hits take shield 50 + health 100 to 0.
public sealed class CombatIntegrationTests : IDisposable
{
    private readonly GameLoop _server;

    public CombatIntegrationTests()
    {
        _server = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestWeapons.Create(), NullLogger.Instance);
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

    [Fact]
    public void Shooting_DamagesThenKills_ThenTargetRespawns()
    {
        using var a = Join("shooter");
        using var b = Join("target");
        Assert.True(Pump.Until(() => a.Weapons != null && b.Weapons != null &&
                                     a.LastSnapshot.ContainsKey(a.MyEntityId) && a.LastSnapshot.ContainsKey(b.MyEntityId) &&
                                     b.SnapshotsReceived > 0, 3000, a, b), "catalog and first snapshots");
        Assert.Equal("Test Auto", a.Weapons![0].Name);
        Assert.Equal(CombatRules.MaxHealth, b.LastSelf.Health);
        Assert.Equal(CombatRules.MaxShield, b.LastSelf.Shield);

        // Both stand on the 5 m spawn ring, which has nothing between its points (TestArena.ClearRadius 7 m).
        Vector3 shooterFeet = a.LastSnapshot[a.MyEntityId].Position;
        Vector3 targetFeet = a.LastSnapshot[b.MyEntityId].Position;
        TestAim.YawPitch(shooterFeet, targetFeet + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);

        // Trigger held, one input per tick, until the target hears of its own death.
        for (int i = 0; i < 90 && b.Deaths.Count == 0; i++)
        {
            a.SendInput(new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = a.LastServerTick });
            Pump.Until(() => false, 33, a, b);
        }
        Assert.True(Pump.Until(() => a.Deaths.Count > 0 && b.Deaths.Count > 0, 3000, a, b), "both clients told of the death");
        var deathSeen = Stopwatch.StartNew();

        // B: five DamageTaken from A, and its own snapshot block showed the loss before the death.
        Assert.Equal(5, b.DamageEvents.Count);
        Assert.All(b.DamageEvents, d => Assert.Equal(a.MyEntityId, d.AttackerId));
        Assert.Contains(b.SelfHistory, s => s.Shield < CombatRules.MaxShield);
        Assert.Contains(b.SelfHistory, s => s.Health > 0 && s.Health < CombatRules.MaxHealth);

        // A: five HitConfirmed, the last one the kill; everyone saw A's tracers.
        Assert.Equal(5, a.Hits.Count);
        Assert.All(a.Hits, h => Assert.Equal(b.MyEntityId, h.TargetId));
        Assert.True(a.Hits.Last().Killed);
        Assert.Contains(b.Shots, s => s.ShooterId == a.MyEntityId);

        foreach (var client in new[] { a, b })
        {
            var died = Assert.Single(client.Deaths);
            Assert.Equal(b.MyEntityId, died.VictimId);
            Assert.Equal(a.MyEntityId, died.KillerId);
        }

        // Respawn about 3 s later at the spawn point, with full health and shield.
        Assert.True(Pump.Until(() => a.Respawns.Count > 0 && b.Respawns.Count > 0, 5000, a, b), "respawn");
        Assert.True(deathSeen.Elapsed.TotalSeconds > 2.5, $"respawned after {deathSeen.Elapsed.TotalSeconds:F2} s");
        var respawned = Assert.Single(b.Respawns);
        Assert.Equal(b.MyEntityId, respawned.EntityId);
        Assert.Equal(Match.SpawnPosition(b.MyEntityId), respawned.Position);
        Assert.True(Pump.Until(() => b.LastSelf.Health == CombatRules.MaxHealth && b.LastSelf.Shield == CombatRules.MaxShield &&
                                     b.LastSnapshot.TryGetValue(b.MyEntityId, out var self) && self.IsAlive, 3000, a, b), "alive again");
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~CombatIntegrationTests"`
Expected: FAIL — `HeadlessClient.Weapons`, `SendInput`, `Deaths` 등 미정의 컴파일 오류

- [ ] **Step 3: HeadlessClient가 새 패킷을 읽는다**

`Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs` 전체:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Integration;

// Minimal client for tests: same wire protocol as the Unity client, no prediction or rendering.
// Every received combat event is kept in a list; a test client lives for one test only.
public sealed class HeadlessClient : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
    private NetPeer _peer = null!;   // set by Connect(); tests always connect first
    private uint _nextSeq = 1;

    public HeadlessClient()
    {
        _net = new NetManager(_listener, null) { UnsyncedEvents = false };
        _listener.PeerConnectedEvent += _ => Connected = true;
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            Disconnected = true;
            DisconnectReason = info.Reason;
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
                RejectReason = (RejectReason)info.AdditionalData.GetByte();
        };
        _listener.NetworkReceiveEvent += OnReceive;
        _net.Start();
    }

    public bool Connected { get; private set; }
    public bool Disconnected { get; private set; }
    public DisconnectReason DisconnectReason { get; private set; }
    public RejectReason RejectReason { get; private set; }
    public JoinMatchResponse? JoinResponse { get; private set; }
    public ushort MyEntityId => JoinResponse?.MyEntityId ?? 0;
    public HashSet<ushort> Spawned { get; } = new();
    public HashSet<ushort> Despawned { get; } = new();
    public Dictionary<ushort, SnapshotEntity> LastSnapshot { get; } = new();
    public uint LastAckInputSeq { get; private set; }
    public uint LastServerTick { get; private set; }
    public int SnapshotsReceived { get; private set; }

    public WeaponInfo[]? Weapons { get; private set; }
    public SnapshotSelf LastSelf { get; private set; }
    public List<SnapshotSelf> SelfHistory { get; } = new();
    public List<ShotFired> Shots { get; } = new();
    public List<HitConfirmed> Hits { get; } = new();
    public List<DamageTaken> DamageEvents { get; } = new();
    public List<PlayerDied> Deaths { get; } = new();
    public List<PlayerRespawned> Respawns { get; } = new();

    public void Connect(int port, string devPlayerId, ushort protocolVersion = ProtocolConstants.ProtocolVersion)
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = protocolVersion, DevPlayerId = devPlayerId });
        var data = new NetDataWriter();
        data.Put(_buffer, 0, writer.Length);
        _peer = _net.Connect("127.0.0.1", port, data);
    }

    public void SendJoin()
    {
        var writer = new PacketWriter(_buffer);
        JoinMatchRequest.Write(ref writer);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    public void SendMove(float moveX, float moveY, float yaw, InputButtons buttons = InputButtons.None)
    {
        SendInput(new InputCommand { MoveX = moveX, MoveY = moveY, Yaw = yaw, Buttons = buttons });
    }

    // Sends one input; Seq is assigned here.
    public void SendInput(InputCommand command)
    {
        command.Seq = _nextSeq++;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.Unreliable);
    }

    public void SendRaw(byte[] data) => _peer.Send(data, DeliveryMethod.ReliableOrdered);

    public void Poll() => _net.PollEvents();

    // Simulates a crash: the socket closes without telling the server.
    public void Kill() => _net.Stop(false);

    public void Dispose() => _net.Stop();

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var r = new PacketReader(reader.GetRemainingBytesSpan());
        if (!r.TryReadPacketId(out PacketId id)) return;
        switch (id)
        {
            case PacketId.JoinMatchResponse:
                if (JoinMatchResponse.TryRead(ref r, out var response)) JoinResponse = response;
                break;
            case PacketId.PlayerSpawned:
                if (PlayerSpawned.TryRead(ref r, out var spawned)) Spawned.Add(spawned.EntityId);
                break;
            case PacketId.PlayerDespawned:
                if (PlayerDespawned.TryRead(ref r, out var despawned)) Despawned.Add(despawned.EntityId);
                break;
            case PacketId.WorldSnapshot:
                if (!WorldSnapshotHeader.TryRead(ref r, out var header)) return;
                LastSnapshot.Clear();
                for (int i = 0; i < header.Count; i++)
                {
                    if (SnapshotEntity.TryRead(ref r, out var e)) LastSnapshot[e.EntityId] = e;
                }
                LastAckInputSeq = header.AckInputSeq;
                LastServerTick = header.ServerTick;
                LastSelf = header.Self;
                SelfHistory.Add(header.Self);
                SnapshotsReceived++;
                break;
            case PacketId.WeaponCatalog:
                if (WeaponCatalogPacket.TryRead(ref r, out var weapons)) Weapons = weapons;
                break;
            case PacketId.ShotFired:
                if (ShotFired.TryRead(ref r, out var shot)) Shots.Add(shot);
                break;
            case PacketId.HitConfirmed:
                if (HitConfirmed.TryRead(ref r, out var hit)) Hits.Add(hit);
                break;
            case PacketId.DamageTaken:
                if (DamageTaken.TryRead(ref r, out var damage)) DamageEvents.Add(damage);
                break;
            case PacketId.PlayerDied:
                if (PlayerDied.TryRead(ref r, out var died)) Deaths.Add(died);
                break;
            case PacketId.PlayerRespawned:
                if (PlayerRespawned.TryRead(ref r, out var respawned)) Respawns.Add(respawned);
                break;
        }
    }
}

public static class Pump
{
    public static bool Until(Func<bool> condition, int timeoutMs, params HeadlessClient[] clients)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var client in clients) client.Poll();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        foreach (var client in clients) client.Poll();
        return condition();
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 전체 PASS. 통합 테스트는 실제 시간(약 4초)을 쓴다. 계획 작성 시 스크래치에서 3회 연속 PASS.

- [ ] **Step 5: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---


### Task 7: Client 네트워크와 순수 상태 (`NetClient` 이벤트, `AimSolver`, `WeaponState`)

**Files:**
- Modify: `Client/Assets/Scripts/Net/NetClient.cs:44-49, 162-172`
- Create: `Client/Assets/Scripts/Game/AimSolver.cs`, `Client/Assets/Scripts/Game/WeaponState.cs`
- Test: `Client/Assets/Tests/EditMode/AimSolverTests.cs` (신규), `Client/Assets/Tests/EditMode/WeaponStateTests.cs` (신규)
- Modify (저장소 밖): `.../scratchpad/predictortests/PredictorTests.csproj`

이 Task의 코드는 아직 아무도 호출하지 않는다(Task 9가 `GameClient`에 연결). 추가만 하므로 Unity 컴파일은 계속 성공한다.

**Interfaces:**
- Consumes: Task 1 패킷 타입, `ShoulderCameraMath.Forward` (테스트)
- Produces (네임스페이스 `ProjectH.Client.Net` / `ProjectH.Client.Game`):
  - `NetClient` 이벤트: `Action<WeaponInfo[]> CatalogReceived`, `Action<ShotFired> ShotReceived`, `Action<HitConfirmed> HitConfirmedReceived`, `Action<DamageTaken> DamageTakenReceived`, `Action<PlayerDied> PlayerDiedReceived`, `Action<PlayerRespawned> PlayerRespawnedReceived`. 수신자 블록은 기존 `SnapshotReceived`의 `header.Self`로 온다.
  - `static class AimSolver { const float EyeHeight = 1.6f, MaxPitch = 89f; static bool TrySolve(Vector3 eye, Vector3 target, out float yaw, out float pitch) }`
  - `sealed class WeaponState { const int SlotCount = 2; WeaponState(WeaponInfo[] catalog); int Slot; bool Reloading; WeaponInfo Current; int Ammo; int ReloadRemainingSteps; void Refill(); bool Step(uint seq, InputButtons buttons); void ApplyServer(in SnapshotSelf server, uint ackSeq) }`

- [ ] **Step 1: 실패하는 테스트 작성**

`Client/Assets/Tests/EditMode/AimSolverTests.cs` (신규). 서버가 같은 식(`ShoulderCameraMath.Forward` = `CombatRules.TryAimDirection`)으로 방향을 되살리므로 왕복을 확인한다:

```csharp
using NUnit.Framework;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Game;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    public class AimSolverTests
    {
        private static readonly Vector3 Eye = new Vector3(2f, 1.6f, -1f);

        [TestCase(0f, 0f, 10f, 0f, 0f)]      // straight ahead (+Z)
        [TestCase(10f, 0f, 0f, 90f, 0f)]     // +X
        [TestCase(0f, 0f, -10f, 180f, 0f)]
        [TestCase(-10f, 0f, 0f, 270f, 0f)]   // yaw stays in 0..360
        [TestCase(0f, -10f, 10f, 0f, 45f)]   // below: positive pitch looks down
        [TestCase(0f, 10f, 10f, 0f, -45f)]
        public void Solve_GivesCameraConventionAngles(float dx, float dy, float dz, float yaw, float pitch)
        {
            Assert.IsTrue(AimSolver.TrySolve(Eye, Eye + new Vector3(dx, dy, dz), out float solvedYaw, out float solvedPitch));
            Assert.AreEqual(yaw, solvedYaw, 1e-3f);
            Assert.AreEqual(pitch, solvedPitch, 1e-3f);
        }

        // The server rebuilds the direction with the camera's formula (CombatRules.TryAimDirection ==
        // ShoulderCameraMath.Forward). Solving and rebuilding must point at the same target.
        [TestCase(3f, -1.2f, 17f)]
        [TestCase(-8f, 0.4f, -2f)]
        [TestCase(0.5f, -1.5f, 0.2f)]
        public void Solve_RoundTripsThroughCameraForward(float dx, float dy, float dz)
        {
            var offset = new Vector3(dx, dy, dz);
            Assert.IsTrue(AimSolver.TrySolve(Eye, Eye + offset, out float yaw, out float pitch));
            Vector3 forward = ShoulderCameraMath.Forward(yaw, pitch);
            Vector3 expected = offset.normalized;
            Assert.AreEqual(expected.x, forward.x, 1e-4f);
            Assert.AreEqual(expected.y, forward.y, 1e-4f);
            Assert.AreEqual(expected.z, forward.z, 1e-4f);
        }

        [Test]
        public void Solve_StraightUp_IsClampedTo89()
        {
            Assert.IsTrue(AimSolver.TrySolve(Eye, Eye + new Vector3(0f, 5f, 0f), out _, out float pitch));
            Assert.AreEqual(-89f, pitch, 1e-4f);
        }

        [Test]
        public void Solve_TargetAtTheEye_ReturnsFalse()
        {
            Assert.IsFalse(AimSolver.TrySolve(Eye, Eye + new Vector3(0.001f, 0f, 0f), out _, out _));
        }
    }
}
```

`Client/Assets/Tests/EditMode/WeaponStateTests.cs` (신규). 서버 `WeaponRulesTests`와 같은 테스트 카탈로그 수치를 쓴다(spec §5 Client: 간격, 탄, 재장전, 서버 값 동기화):

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Same numbers as the server's test catalog (TestWeapons): slot 0 automatic, 3-tick interval,
    // 6 rounds, 30-tick reload; slot 1 semi-automatic, 15-tick interval, 2 rounds, 60-tick reload.
    public class WeaponStateTests
    {
        private static WeaponInfo[] Catalog() => new[]
        {
            new WeaponInfo { WeaponId = 1, Name = "Test Auto", Damage = 30, FireIntervalTicks = 3, MagazineSize = 6, ReloadTicks = 30, Range = 100f, Automatic = true },
            new WeaponInfo { WeaponId = 2, Name = "Test Semi", Damage = 90, FireIntervalTicks = 15, MagazineSize = 2, ReloadTicks = 60, Range = 300f, Automatic = false },
        };

        private uint _seq;

        // NUnit reuses one fixture instance for every test in the class.
        [SetUp]
        public void ResetSeq() => _seq = 0;

        private bool Step(WeaponState state, InputButtons buttons) => state.Step(++_seq, buttons);

        [Test]
        public void Auto_Held_FiresOncePerInterval()
        {
            var state = new WeaponState(Catalog());
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
        public void Auto_EmptyMagazine_Reloads_ThenFiresAgain()
        {
            var state = new WeaponState(Catalog());
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
        }

        [Test]
        public void Semi_Held_FiresOnce()
        {
            var state = new WeaponState(Catalog());
            Assert.IsTrue(Step(state, InputButtons.Slot2 | InputButtons.Fire));
            for (int i = 0; i < 40; i++) Assert.IsFalse(Step(state, InputButtons.Fire));
            Assert.IsFalse(Step(state, InputButtons.None));
            Assert.IsTrue(Step(state, InputButtons.Fire));
        }

        [Test]
        public void ReloadButton_And_Switch_FollowServerOrder()
        {
            var state = new WeaponState(Catalog());
            Assert.IsTrue(Step(state, InputButtons.Fire));
            Step(state, InputButtons.Reload);
            Assert.IsTrue(state.Reloading);
            Step(state, InputButtons.Slot2);                       // switching cancels the reload
            Assert.AreEqual(1, state.Slot);
            Assert.IsFalse(state.Reloading);
            Step(state, InputButtons.Slot1 | InputButtons.Slot2);  // both: ignored
            Assert.AreEqual(1, state.Slot);
            Step(state, InputButtons.Slot1);
            Assert.AreEqual(5, state.Ammo);
            Assert.AreEqual("Test Auto", state.Current.Name);
        }

        [Test]
        public void ApplyServer_MatchingValues_KeepsNewerLocalSteps()
        {
            var state = new WeaponState(Catalog());
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
            var state = new WeaponState(Catalog());
            Step(state, InputButtons.Fire);   // local: 5 left after seq 1

            // The server rejected that shot (e.g. its input was lost) and is reloading slot 1.
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 1, Ammo = 1, ReloadRemainingTicks = 10 }, 1);

            Assert.AreEqual(1, state.Slot);
            Assert.AreEqual(1, state.Ammo);
            Assert.IsTrue(state.Reloading);
            Assert.AreEqual(10, state.ReloadRemainingSteps);
        }

        [Test]
        public void ApplyServer_IgnoresAckZero_AndSlotOutsideLoadout()
        {
            var state = new WeaponState(Catalog());
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 0, Ammo = 1 }, 0);
            Assert.AreEqual(6, state.Ammo);

            Step(state, InputButtons.None);
            state.ApplyServer(new SnapshotSelf { WeaponSlot = 7, Ammo = 1 }, 1);
            Assert.AreEqual(0, state.Slot);
            Assert.AreEqual(6, state.Ammo);
        }

        [Test]
        public void Refill_RestoresFullLoadout()
        {
            var state = new WeaponState(Catalog());
            Step(state, InputButtons.Slot2 | InputButtons.Fire);
            state.Refill();
            Assert.AreEqual(0, state.Slot);
            Assert.AreEqual(6, state.Ammo);
            Assert.IsFalse(state.Reloading);
        }
    }
}
```

스크래치 NUnit 프로젝트 `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj`의 `RingCursor.cs` `<Compile>` 줄 바로 앞에 두 줄을 추가한다(테스트 파일은 glob으로 이미 포함된다):

```xml
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/AimSolver.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/AimSolver.cs')" />
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/WeaponState.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/WeaponState.cs')" />
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: FAIL — `AimSolver`, `WeaponState` 미정의 컴파일 오류

- [ ] **Step 3: `AimSolver`**

`Client/Assets/Scripts/Game/AimSolver.cs` (신규):

```csharp
using UnityEngine;

namespace ProjectH.Client.Game
{
    // D2: the aim sent to the server is the direction from the character's eye to the point under the
    // crosshair, not the camera's own direction. The server shoots from the same eye along it, so the
    // shoulder camera's offset does not move the hit. Pure math (no Physics), testable outside Unity.
    public static class AimSolver
    {
        // Must equal the server's CombatRules.EyeHeight (feet + 1.6 m, also ShoulderCameraMath.PivotHeight).
        public const float EyeHeight = 1.6f;
        // The server clamps pitch to the same range.
        public const float MaxPitch = 89f;
        private const float MinDistance = 0.01f;

        // yaw 0 faces +Z, yaw 90 faces +X, positive pitch looks down (ShoulderCameraMath.Forward).
        // False when the target is too close to the eye to give a direction.
        public static bool TrySolve(Vector3 eye, Vector3 target, out float yaw, out float pitch)
        {
            Vector3 d = target - eye;
            float horizontal = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            if (horizontal < MinDistance && Mathf.Abs(d.y) < MinDistance)
            {
                yaw = 0f;
                pitch = 0f;
                return false;
            }

            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (yaw < 0f) yaw += 360f;
            pitch = Mathf.Clamp(-Mathf.Atan2(d.y, horizontal) * Mathf.Rad2Deg, -MaxPitch, MaxPitch);
            return true;
        }
    }
}
```

- [ ] **Step 4: `WeaponState`**

`Client/Assets/Scripts/Game/WeaponState.cs` (신규). 규칙은 서버 `WeaponRules`와 한 줄씩 대응한다(교체 → 재장전 → 발사, 슬롯별 간격, 단발은 누른 순간만, 탄 0이면 자동 재장전):

```csharp
using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Presentation copy of the server's weapon rules (Server WeaponRules), stepped once per predicted
    // input, so the local tracer and the ammo counter react at once (D12). The server still decides
    // every shot. One Step = one simulation tick, the unit of the catalog's tick values, so intervals and
    // reloads line up with the server as long as no input is lost. Keep the rules identical to WeaponRules.
    public sealed class WeaponState
    {
        public const int SlotCount = 2;           // Slot1 / Slot2 = catalog entries 0 / 1
        private const int HistorySize = 64;       // same as LocalPlayerPredictor: covers every unacked input

        private struct Record
        {
            public uint Seq;
            public int Slot;
            public int Ammo;
            public bool Reloading;
        }

        private readonly WeaponInfo[] _weapons;
        private readonly int[] _ammo = new int[SlotCount];
        private readonly long[] _nextFireStep = new long[SlotCount];
        private readonly Record[] _history = new Record[HistorySize];
        private long _step;
        private long _reloadEndStep;
        private bool _fireHeld;

        public WeaponState(WeaponInfo[] catalog)
        {
            if (catalog == null || catalog.Length == 0) throw new ArgumentException("Empty weapon catalog.", nameof(catalog));
            int count = Math.Min(catalog.Length, SlotCount);
            _weapons = new WeaponInfo[count];
            Array.Copy(catalog, _weapons, count);
            Refill();
        }

        public int Slot { get; private set; }
        public bool Reloading { get; private set; }
        public WeaponInfo Current => _weapons[Slot];
        public int Ammo => _ammo[Slot];
        public int ReloadRemainingSteps => Reloading ? (int)Math.Max(0, _reloadEndStep - _step) : 0;

        // Join and respawn: full magazines, first slot (server WeaponRules.Equip).
        public void Refill()
        {
            Slot = 0;
            for (int i = 0; i < SlotCount; i++)
            {
                _ammo[i] = i < _weapons.Length ? _weapons[i].MagazineSize : 0;
                _nextFireStep[i] = 0;
            }
            Reloading = false;
            _fireHeld = false;
        }

        // One predicted input, oldest first. Returns true when the server is expected to fire it.
        public bool Step(uint seq, InputButtons buttons)
        {
            long now = _step++;
            if (Reloading && now >= _reloadEndStep)
            {
                Reloading = false;
                _ammo[Slot] = Current.MagazineSize;
            }

            bool fired = Apply(buttons, now);
            _history[seq % HistorySize] = new Record { Seq = seq, Slot = Slot, Ammo = _ammo[Slot], Reloading = Reloading };
            return fired;
        }

        // The server's values after it processed input ackSeq (snapshot self block). If they equal what this
        // copy had after the same input, the newer local steps stand; otherwise the server wins and the copy
        // restarts from its values (D12).
        public void ApplyServer(in SnapshotSelf server, uint ackSeq)
        {
            if (ackSeq == 0) return;                               // no input processed yet: nothing to compare
            if (server.WeaponSlot >= _weapons.Length) return;      // untrusted value outside the loadout

            bool serverReloading = server.ReloadRemainingTicks > 0;
            Record local = _history[ackSeq % HistorySize];
            if (local.Seq == ackSeq && local.Slot == server.WeaponSlot && local.Ammo == server.Ammo && local.Reloading == serverReloading)
                return;

            Slot = server.WeaponSlot;
            _ammo[Slot] = Math.Min(server.Ammo, (int)Current.MagazineSize);
            Reloading = serverReloading;
            _reloadEndStep = _step + server.ReloadRemainingTicks;
        }

        // Same order as the server: switch -> reload -> fire.
        private bool Apply(InputButtons buttons, long now)
        {
            bool slot1 = (buttons & InputButtons.Slot1) != 0;
            bool slot2 = (buttons & InputButtons.Slot2) != 0;
            if (slot1 != slot2)
            {
                int target = slot1 ? 0 : 1;
                if (target < _weapons.Length && target != Slot)
                {
                    Slot = target;
                    Reloading = false;
                }
            }

            WeaponInfo weapon = Current;
            if ((buttons & InputButtons.Reload) != 0 && !Reloading && _ammo[Slot] < weapon.MagazineSize)
                StartReload(weapon, now);

            bool fireHeld = (buttons & InputButtons.Fire) != 0;
            bool trigger = fireHeld && (weapon.Automatic || !_fireHeld);
            _fireHeld = fireHeld;
            if (!trigger || Reloading || now < _nextFireStep[Slot]) return false;

            if (_ammo[Slot] == 0)
            {
                StartReload(weapon, now);
                return false;
            }

            _ammo[Slot]--;
            _nextFireStep[Slot] = now + weapon.FireIntervalTicks;
            if (_ammo[Slot] == 0) StartReload(weapon, now);
            return true;
        }

        private void StartReload(WeaponInfo weapon, long now)
        {
            Reloading = true;
            _reloadEndStep = now + weapon.ReloadTicks;
        }
    }
}
```

- [ ] **Step 5: `NetClient`가 새 패킷을 이벤트로 올린다**

`Client/Assets/Scripts/Net/NetClient.cs`:

```csharp
        public event SnapshotHandler SnapshotReceived;
        public event Action<string> Disconnected;
```
→
```csharp
        public event SnapshotHandler SnapshotReceived;
        public event Action<string> Disconnected;
        // Phase 3 combat (D4, D9, D11). Payloads are structs, so raising them does not allocate
        // (the catalog array is allocated once per join by its reader).
        public event Action<WeaponInfo[]> CatalogReceived;
        public event Action<ShotFired> ShotReceived;
        public event Action<HitConfirmed> HitConfirmedReceived;
        public event Action<DamageTaken> DamageTakenReceived;
        public event Action<PlayerDied> PlayerDiedReceived;
        public event Action<PlayerRespawned> PlayerRespawnedReceived;
```

`OnNetworkReceive`의 `switch` 끝:

```csharp
                    SnapshotReceived?.Invoke(header, _snapshotEntities, count);
                    break;
            }
```
→
```csharp
                    SnapshotReceived?.Invoke(header, _snapshotEntities, count);
                    break;

                case PacketId.WeaponCatalog:
                    if (WeaponCatalogPacket.TryRead(ref packet, out var weapons)) CatalogReceived?.Invoke(weapons);
                    break;

                case PacketId.ShotFired:
                    if (ShotFired.TryRead(ref packet, out var shot)) ShotReceived?.Invoke(shot);
                    break;

                case PacketId.HitConfirmed:
                    if (HitConfirmed.TryRead(ref packet, out var hit)) HitConfirmedReceived?.Invoke(hit);
                    break;

                case PacketId.DamageTaken:
                    if (DamageTaken.TryRead(ref packet, out var damage)) DamageTakenReceived?.Invoke(damage);
                    break;

                case PacketId.PlayerDied:
                    if (PlayerDied.TryRead(ref packet, out var died)) PlayerDiedReceived?.Invoke(died);
                    break;

                case PacketId.PlayerRespawned:
                    if (PlayerRespawned.TryRead(ref packet, out var respawned)) PlayerRespawnedReceived?.Invoke(respawned);
                    break;
            }
```

- [ ] **Step 6: Client 스크래치 검증**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 기존 + `AimSolverTests`, `WeaponStateTests` 전부 PASS (`WeaponStateTests`의 `[SetUp]`이 없으면 NUnit이 테스트 사이에 같은 인스턴스를 재사용해 Seq가 이어지므로 `ApplyServer_MatchingValues_KeepsNewerLocalSteps`가 실패한다)

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

- [ ] **Step 7: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---


### Task 8: Client 예측과 뷰 (버튼·조준 입력, 사망 정지·부활 초기화, 원격 생존 표시)

**Files:**
- Modify: `Client/Assets/Scripts/Game/LocalPlayerPredictor.cs` (전체 교체), `Client/Assets/Scripts/Input/InputReader.cs` (전체 교체)
- Modify: `Client/Assets/Scripts/Game/PlayerViewFactory.cs` (전체 교체), `Client/Assets/Scripts/Game/RemotePlayers.cs` (전체 교체), `Client/Assets/Scripts/Game/RemotePlayerInterpolator.cs` (`Clear`)
- Modify: `Client/Assets/Scripts/Game/GameClient.cs:82-84, 159-160` (새 `Advance` 시그니처만. 전체 연결은 Task 9)
- Test: `Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs` (전체 교체), `ArenaPredictionTests.cs:22-29,94`, `RemotePlayerInterpolatorTests.cs`

**Interfaces:**
- Consumes: `InputButtons`, `SnapshotEntity.IsAlive/AliveFlag` (Task 1)
- Produces:
  - `LocalPlayerPredictor`: `const int HistorySize = 64` (public으로), `bool IsDead`, `int Advance(float deltaTime, Vector2 move, float yaw, InputButtons held, ref InputButtons queued)`(held = Sprint·Fire는 모든 Step, queued = Jump·Reload·Slot1·Slot2는 마지막 Step에만 넣고 지움), `void SetAim(int steps, float aimYaw, float aimPitch, float viewTick)`, `InputCommand InputAt(uint seq)`, `void SetDead()`, `void Respawn(MoveState spawn)`. `Reconcile`은 `server.IsAlive == IsDead`인 Snapshot을 무시하고, 사망 중에는 서버 상태로 바로 맞춘다.
  - `InputReader.QueuedButtons` (`InputButtons`, get/set; `JumpQueued`는 없어진다). 키: R = Reload, 1 = Slot1, 2 = Slot2.
  - `PlayerViewFactory`: `const int RemoteHitLayer = 2`, `const int AimRaycastMask`, `Create(string name, bool isLocal)`(원격은 반지름 0.35·높이 1.8 CapsuleCollider, 레이어 2), `static void SetAlive(Renderer renderer, Collider collider, bool isLocal, bool alive)`, `static void Pose(Vector3 feet, float yaw, bool alive, out Vector3 position, out Quaternion rotation)`
  - `RemotePlayerInterpolator.Clear()`
  - `RemotePlayers.Push`가 Snapshot 생존 비트로 뷰를 바꾸고, 죽음 → 생존 전환 때 보간 기록을 비운다.

- [ ] **Step 1: 실패하는 테스트 작성**

(이 Task의 Step 1–6 사이에는 Client 코드가 컴파일되지 않는 구간이 있다. Step 8 전까지 사용자에게 Unity Editor를 포커스하지 말아 달라고 요청한다.)

`Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs` 전체. 기존 테스트는 새 `Advance` 시그니처와 생존 비트가 켜진 Snapshot(`Alive(...)`)으로 옮기고, 버튼·조준·사망·부활 테스트를 더한다(Review Focus 포함):

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    public class LocalPlayerPredictorTests
    {
        private const int SimHz = 30;
        private const float Step = 1f / SimHz;

        private static LocalPlayerPredictor NewPredictor() => new LocalPlayerPredictor(SimHz, new MoveState());

        private static void AdvanceSteps(LocalPlayerPredictor predictor, int steps, Vector2 move, InputButtons held = InputButtons.None)
        {
            InputButtons queued = InputButtons.None;
            // Slightly more than N steps of time so float rounding cannot drop a step.
            predictor.Advance(steps * Step + 0.0005f, move, 0f, held, ref queued);
        }

        // Snapshot entities of a living player (the alive flag is bit 0 of Flags).
        private static SnapshotEntity Alive(System.Numerics.Vector3 position, float velocityY = 0f, float yaw = 0f)
            => new SnapshotEntity { Position = position, VelocityY = velocityY, Yaw = yaw, Flags = SnapshotEntity.AliveFlag };

        [Test]
        public void Advance_ProducesOneSeqPerStep_AndPacketHoldsNewestThree()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 4, Vector2.up);

            Assert.AreEqual(4u, predictor.LastSeq);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(3, packet.Count);
            Assert.AreEqual(2u, packet.Get(0).Seq);
            Assert.AreEqual(4u, packet.Get(2).Seq);
        }

        [Test]
        public void Reconcile_WithMatchingServerState_KeepsPrediction()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 3, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            // Server processed inputs 1..2 exactly as the client did.
            var server = new MoveState();
            for (int i = 0; i < 2; i++) MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, TestArena.Boxes);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 2);

            Assert.AreEqual(before.z, predictor.PredictedPosition.z, 1e-4f);
        }

        [Test]
        public void Reconcile_WithDifferentServerState_ReplaysUnackedInputs()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 3, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            // Server says that after input 1 the player was 1 m further along +X (e.g. pushed).
            var server = new MoveState();
            MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, TestArena.Boxes);
            server.Position.X += 1f;
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 1);

            // Inputs 2..3 are replayed on top of the corrected state.
            Assert.AreEqual(before.x + 1f, predictor.PredictedPosition.x, 1e-4f);
            Assert.AreEqual(before.z, predictor.PredictedPosition.z, 1e-4f);
        }

        [Test]
        public void Reconcile_WithNoAckAndNoInputs_SnapsToServer()
        {
            var predictor = NewPredictor();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), 0);
            Assert.AreEqual(new Vector3(3f, 0f, 4f), predictor.PredictedPosition);
        }

        // Ack 0 only means the server has not processed any of our inputs yet. Once inputs are
        // predicted, snapping to the (older) server state would stutter on every join.
        [Test]
        public void Reconcile_WithNoAckButPredictedInputs_KeepsPrediction()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), 0);

            Assert.AreEqual(before, predictor.PredictedPosition);
        }

        [Test]
        public void JumpQueued_IsConsumedByLastStepOnly()
        {
            var predictor = NewPredictor();
            InputButtons queued = InputButtons.Jump;
            predictor.Advance(2 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.None, ref queued);
            Assert.AreEqual(InputButtons.None, queued);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(2, packet.Count);
            Assert.AreEqual(InputButtons.None, packet.Get(0).Buttons);
            Assert.AreEqual(InputButtons.Jump, packet.Get(1).Buttons);
        }

        // A hitch frame runs more steps than one packet carries (3). The jump must ride on the newest
        // step, otherwise it is never sent and the server never applies it.
        [Test]
        public void JumpQueued_OnHitchFrame_LandsInSentPacket()
        {
            var predictor = NewPredictor();
            InputButtons queued = InputButtons.Jump;
            int steps = predictor.Advance(5 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.None, ref queued);

            Assert.AreEqual(5, steps);
            Assert.AreEqual(InputButtons.None, queued);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(3, packet.Count);
            Assert.AreEqual(InputButtons.None, packet.Get(0).Buttons);
            Assert.AreEqual(InputButtons.None, packet.Get(1).Buttons);
            Assert.AreEqual(5u, packet.Get(2).Seq);
            Assert.AreEqual(InputButtons.Jump, packet.Get(2).Buttons);
        }

        [Test]
        public void HeldAndQueuedButtons_GoToEveryStepAndLastStep()
        {
            var predictor = NewPredictor();
            InputButtons queued = InputButtons.Reload | InputButtons.Slot2;
            predictor.Advance(3 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.Fire | InputButtons.Sprint | InputButtons.Jump, ref queued);

            Assert.AreEqual(InputButtons.Fire | InputButtons.Sprint, predictor.InputAt(1).Buttons);   // Jump is never "held"
            Assert.AreEqual(InputButtons.Fire | InputButtons.Sprint, predictor.InputAt(2).Buttons);
            Assert.AreEqual(InputButtons.Fire | InputButtons.Sprint | InputButtons.Reload | InputButtons.Slot2, predictor.InputAt(3).Buttons);
            Assert.AreEqual(InputButtons.None, queued);
        }

        [Test]
        public void SetAim_WritesAimIntoTheNewestSteps()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            predictor.SetAim(2, 10f, 20f, 300f);
            AdvanceSteps(predictor, 3, Vector2.up);
            predictor.SetAim(3, 11f, -5f, 303.5f);

            Assert.AreEqual(10f, predictor.InputAt(2).AimYaw);
            Assert.AreEqual(300f, predictor.InputAt(2).ViewTick);
            Assert.AreEqual(11f, predictor.InputAt(3).AimYaw);
            Assert.AreEqual(-5f, predictor.InputAt(5).AimPitch);
            Assert.AreEqual(303.5f, predictor.InputAt(5).ViewTick);
            Assert.IsTrue(predictor.TryBuildInputPacket(out PlayerInputPacket packet));
            Assert.AreEqual(303.5f, packet.Get(2).ViewTick);
        }

        // Snapshot values come from the network and are not validated by NetClient: a non-finite
        // entity must be ignored instead of replacing (and poisoning) the predicted state.
        [TestCase(float.NaN, 0f, 0f)]
        [TestCase(0f, 0f, float.NaN)]
        [TestCase(0f, float.NaN, 0f)]
        public void Reconcile_WithNonFiniteServerState_IsIgnored(float x, float velocityY, float yaw)
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            Vector3 before = predictor.PredictedPosition;
            float beforeYaw = predictor.RenderYaw;

            var entity = Alive(new System.Numerics.Vector3(x, 0f, 0f), velocityY, yaw);
            predictor.Reconcile(entity, 0);
            predictor.Reconcile(entity, 1);

            Assert.AreEqual(before, predictor.PredictedPosition);
            Assert.AreEqual(beforeYaw, predictor.RenderYaw);
        }

        // Spec §5: prediction stops while dead. Inputs keep their Seq but carry no move and no buttons.
        [Test]
        public void Dead_StopsMoving_AndSendsEmptyInputs()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            predictor.SetDead();
            Vector3 body = predictor.PredictedPosition;

            InputButtons queued = InputButtons.Jump | InputButtons.Reload;
            predictor.Advance(3 * Step + 0.0005f, Vector2.up, 45f, InputButtons.Fire | InputButtons.Sprint, ref queued);

            Assert.IsTrue(predictor.IsDead);
            Assert.AreEqual(body, predictor.PredictedPosition);
            Assert.AreEqual(5u, predictor.LastSeq);
            Assert.AreEqual(InputButtons.None, queued);
            for (uint seq = 3; seq <= 5; seq++)
            {
                InputCommand c = predictor.InputAt(seq);
                Assert.AreEqual(0f, c.MoveX);
                Assert.AreEqual(0f, c.MoveY);
                Assert.AreEqual(InputButtons.None, c.Buttons);
            }
        }

        [Test]
        public void Dead_ReconcileSnapsToTheBody()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 3, Vector2.up);   // moved on after the server already killed us
            predictor.SetDead();

            var body = new System.Numerics.Vector3(0f, 0f, 0.1f);
            predictor.Reconcile(new SnapshotEntity { Position = body, Flags = 0 }, 1);

            Assert.AreEqual(new Vector3(0f, 0f, 0.1f), predictor.PredictedPosition);
        }

        // Review Focus: a respawn must not restart Seq (the server ignores seqs it already took), and
        // reconciling from the spawn point must land where the local prediction already is.
        [Test]
        public void Respawn_ResetsState_KeepsSeq_AndReconcilesFromSpawn()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);            // seq 1-2 alive
            predictor.SetDead();
            AdvanceSteps(predictor, 3, Vector2.up);            // seq 3-5 dead: empty inputs
            var spawn = new MoveState { Position = new System.Numerics.Vector3(5f, 0f, 5f) };
            predictor.Respawn(spawn);
            Assert.IsFalse(predictor.IsDead);
            Assert.AreEqual(new Vector3(5f, 0f, 5f), predictor.PredictedPosition);
            Assert.AreEqual(new Vector3(5f, 0f, 5f), predictor.RenderPosition);

            AdvanceSteps(predictor, 3, Vector2.up);            // seq 6-8 alive again
            Assert.AreEqual(8u, predictor.LastSeq);
            Vector3 predicted = predictor.PredictedPosition;

            // The server respawned before taking seq 5, stepped seq 5 (empty) at the spawn point and acked it.
            var server = spawn;
            MovementSimulation.Step(ref server, new InputCommand { Seq = 5 }, Step, TestArena.Boxes);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 5);

            Assert.AreEqual(predicted.x, predictor.PredictedPosition.x, 1e-4f);
            Assert.AreEqual(predicted.z, predictor.PredictedPosition.z, 1e-4f);
        }

        // Died and Respawned are Reliable, snapshots Sequenced: a snapshot can arrive from the other life.
        [Test]
        public void Reconcile_IgnoresSnapshotFromTheOtherLife()
        {
            var predictor = NewPredictor();
            AdvanceSteps(predictor, 2, Vector2.up);
            Vector3 before = predictor.PredictedPosition;

            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(9f, 0f, 9f), Flags = 0 }, 1);   // dead, but no PlayerDied yet
            Assert.AreEqual(before, predictor.PredictedPosition);

            predictor.SetDead();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(-9f, 0f, -9f)), 2);   // alive, but no PlayerRespawned yet
            Assert.AreEqual(before, predictor.PredictedPosition);
        }
    }
}
```

`Client/Assets/Tests/EditMode/ArenaPredictionTests.cs` 세 곳. `SnapshotEntity`의 `Flags` 기본값 0은 "사망"이므로 살아 있는 Snapshot에는 생존 비트를 켠다.

```csharp
            bool jump = false;
            Assert.AreEqual(1, predictor.Advance(Step, move, 0f, false, ref jump));
```
→
```csharp
            InputButtons queued = InputButtons.None;
            Assert.AreEqual(1, predictor.Advance(Step, move, 0f, InputButtons.None, ref queued));
```

```csharp
            => new SnapshotEntity { Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw };
```
→
```csharp
            => new SnapshotEntity { Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw, Flags = SnapshotEntity.AliveFlag };
```

```csharp
            predictor.Reconcile(new SnapshotEntity { Position = new Num.Vector3(9f, 0f, 9f) }, 1);
```
→
```csharp
            predictor.Reconcile(new SnapshotEntity { Position = new Num.Vector3(9f, 0f, 9f), Flags = SnapshotEntity.AliveFlag }, 1);
```

`Client/Assets/Tests/EditMode/RemotePlayerInterpolatorTests.cs`의 `ServerClock_RenderTick_NeverGoesBackwards` 테스트 바로 앞에 추가:

```csharp
        [Test]
        public void Clear_DropsHistory_SoNextSampleIsTheNewPosition()
        {
            var interp = new RemotePlayerInterpolator();
            interp.Push(10, new Vector3(9f, 0f, 0f), 0f);
            interp.Push(12, new Vector3(9f, 0f, 0f), 0f);
            interp.Clear();
            Assert.IsFalse(interp.TrySample(11.0, out _, out _));

            interp.Push(20, new Vector3(-5f, 0f, 0f), 0f);
            // Rendering lags behind the newest tick: with one sample it is shown at once, no slide.
            Assert.IsTrue(interp.TrySample(16.0, out Vector3 position, out _));
            Assert.AreEqual(-5f, position.x, 1e-4f);
        }

```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: FAIL — `Advance(float, Vector2, float, InputButtons, ref InputButtons)`, `SetDead`, `Respawn`, `SetAim`, `InputAt`, `RemotePlayerInterpolator.Clear` 없음 컴파일 오류

- [ ] **Step 3: `LocalPlayerPredictor`**

`Client/Assets/Scripts/Game/LocalPlayerPredictor.cs` 전체:

```csharp
using System;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Client-side prediction for the local player (Docs/Networking.md).
    // Collides with Shared TestArena.Boxes, the same boxes the server passes in Match.Tick.
    // Runs MovementSimulation at the server's tick rate, keeps a fixed 64-entry history of inputs and
    // results, and on each snapshot replays the inputs the server has not processed yet.
    // While dead (D9, D12) it predicts nothing: the server acks a dead player's inputs without moving it.
    public sealed class LocalPlayerPredictor
    {
        public const int HistorySize = 64;                 // ~2 s at 30 Hz; older unacked input -> snap
        private const float SnapDistance = 2f;             // corrections larger than this are not smoothed
        private const float ErrorDecayPerSecond = 10f;
        private const float MatchEpsilon = 0.01f;
        private const float MaxAccumulatedSeconds = 0.25f; // after a hitch, do not burst-simulate

        // Held buttons go into every step; queued presses only into a frame's last step (see Advance).
        private const InputButtons HeldButtons = InputButtons.Sprint | InputButtons.Fire;
        private const InputButtons QueuedButtons = InputButtons.Jump | InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2;

        private readonly InputCommand[] _inputs = new InputCommand[HistorySize];
        private readonly MoveState[] _results = new MoveState[HistorySize];
        private readonly float _stepSeconds;
        private MoveState _state;
        private MoveState _previous;
        private float _accumulator;
        private Vector3 _renderError;

        public LocalPlayerPredictor(int simHz, MoveState spawnState)
        {
            _stepSeconds = 1f / simHz;
            _state = spawnState;
            _previous = spawnState;
            RenderPosition = spawnState.Position.ToUnity();
        }

        public uint LastSeq { get; private set; }
        public Vector3 RenderPosition { get; private set; }
        public float RenderYaw => _state.Yaw;
        public Vector3 PredictedPosition => _state.Position.ToUnity();
        public bool IsDead { get; private set; }

        // Returns how many simulation steps ran (each generated one input).
        // held: Sprint and Fire, applied to every step. queued: Jump, Reload, Slot1, Slot2 presses; they ride on
        // the last step, because GameClient sends one packet per frame holding only the newest
        // MaxInputsPerPacket inputs, so on a hitch frame an earlier step may never be sent. Consumed bits are cleared.
        public int Advance(float deltaTime, Vector2 move, float yaw, InputButtons held, ref InputButtons queued)
        {
            _accumulator = Mathf.Min(_accumulator + deltaTime, MaxAccumulatedSeconds);
            int steps = 0;
            while (_accumulator >= _stepSeconds)
            {
                _accumulator -= _stepSeconds;
                // Same test as the loop condition, so this is true exactly on the frame's final step.
                bool lastStep = _accumulator < _stepSeconds;

                InputCommand command;
                _previous = _state;
                if (IsDead)
                {
                    // Zero move and no buttons: the server may process some of these after the respawn, and
                    // then they must not move or fire. Presses made while dead are dropped.
                    command = new InputCommand { Seq = ++LastSeq, Yaw = yaw };
                    if (lastStep) queued = InputButtons.None;
                }
                else
                {
                    var buttons = held & HeldButtons;
                    if (lastStep)
                    {
                        buttons |= queued & QueuedButtons;
                        queued = InputButtons.None;
                    }
                    command = new InputCommand { Seq = ++LastSeq, MoveX = move.x, MoveY = move.y, Yaw = yaw, Buttons = buttons };
                    MovementSimulation.Step(ref _state, command, _stepSeconds, TestArena.Boxes);
                }

                int slot = (int)(command.Seq % HistorySize);
                _inputs[slot] = command;
                _results[slot] = _state;
                steps++;
            }

            float alpha = _accumulator / _stepSeconds;
            _renderError = Vector3.Lerp(_renderError, Vector3.zero, 1f - Mathf.Exp(-ErrorDecayPerSecond * deltaTime));
            RenderPosition = Vector3.Lerp(_previous.Position.ToUnity(), _state.Position.ToUnity(), alpha) + _renderError;
            return steps;
        }

        // D2/D6: GameClient calls this after the camera moved this frame, so the newest `steps` inputs carry
        // the aim the player saw. Aim does not affect MovementSimulation, so the predicted results stay valid.
        public void SetAim(int steps, float aimYaw, float aimPitch, float viewTick)
        {
            if (steps > HistorySize) steps = HistorySize;
            if (steps > LastSeq) steps = (int)LastSeq;
            for (int i = 0; i < steps; i++)
            {
                int slot = (int)((LastSeq - (uint)i) % HistorySize);
                _inputs[slot].AimYaw = aimYaw;
                _inputs[slot].AimPitch = aimPitch;
                _inputs[slot].ViewTick = viewTick;
            }
        }

        // One of the last HistorySize inputs (seq in LastSeq - 63 .. LastSeq).
        public InputCommand InputAt(uint seq) => _inputs[(int)(seq % HistorySize)];

        // Newest inputs, oldest first (up to 3). Resending recent inputs covers single packet loss.
        public bool TryBuildInputPacket(out PlayerInputPacket packet)
        {
            packet = default;
            if (LastSeq == 0) return false;

            int count = (int)Math.Min(LastSeq, (uint)ProtocolConstants.MaxInputsPerPacket);
            packet.Count = (byte)count;
            for (int i = 0; i < count; i++)
            {
                uint seq = LastSeq - (uint)(count - 1 - i);
                packet.Set(i, _inputs[(int)(seq % HistorySize)]);
            }
            return true;
        }

        // PlayerDied for us (Reliable): stop predicting until the respawn.
        public void SetDead()
        {
            IsDead = true;
        }

        // PlayerRespawned for us: a teleport. State restarts at the spawn point, but Seq continues: the server
        // drops any seq it has already taken, so restarting at 1 would make every later input ignored.
        public void Respawn(MoveState spawn)
        {
            IsDead = false;
            _state = spawn;
            _previous = spawn;
            _renderError = Vector3.zero;
            RenderPosition = spawn.Position.ToUnity();
        }

        public void Reconcile(in SnapshotEntity server, uint ackSeq)
        {
            // Snapshot data is untrusted and NetClient does not validate it. A non-finite value would
            // replace the predicted state and every later step would stay NaN, so the entity is ignored.
            if (!IsFinite(server.Position.X) || !IsFinite(server.Position.Y) || !IsFinite(server.Position.Z) ||
                !IsFinite(server.VelocityY) || !IsFinite(server.Yaw))
            {
                return;
            }

            // Death and respawn switch IsDead through Reliable events. A snapshot from the other side of that
            // switch (Sequenced, can arrive before or after the event) describes the other life: skip it.
            if (server.IsAlive == IsDead) return;

            var authoritative = new MoveState { Position = server.Position, VelocityY = server.VelocityY, Yaw = server.Yaw };

            if (IsDead)
            {
                // The server does not move a dead player: its position is final, nothing to replay.
                _state = authoritative;
                _previous = authoritative;
                _renderError = Vector3.zero;
                return;
            }

            // Ack 0 says nothing about our inputs: the server has not processed any yet. Once inputs are
            // predicted the local state is newer than this snapshot, so snapping would stutter on join.
            if (ackSeq == 0 && LastSeq > 0) return;

            if (ackSeq == 0 || ackSeq > LastSeq || LastSeq - ackSeq >= HistorySize)
            {
                // Nothing to replay from (no input sent yet, or history already overwritten).
                _state = authoritative;
                _previous = authoritative;
                _renderError = Vector3.zero;
                return;
            }

            MoveState predicted = _results[(int)(ackSeq % HistorySize)];
            if (System.Numerics.Vector3.DistanceSquared(predicted.Position, authoritative.Position) < MatchEpsilon * MatchEpsilon &&
                Mathf.Abs(predicted.VelocityY - authoritative.VelocityY) < MatchEpsilon)
            {
                return;
            }

            // Misprediction: restart from the server state and replay unacknowledged inputs.
            System.Numerics.Vector3 oldPosition = _state.Position;
            _state = authoritative;
            _previous = authoritative;
            _results[(int)(ackSeq % HistorySize)] = authoritative;
            for (uint seq = ackSeq + 1; seq <= LastSeq; seq++)
            {
                int slot = (int)(seq % HistorySize);
                _previous = _state;
                MovementSimulation.Step(ref _state, _inputs[slot], _stepSeconds, TestArena.Boxes);
                _results[slot] = _state;
            }

            // Keep the rendered position continuous and let the difference decay, unless it is large.
            Vector3 correction = (oldPosition - _state.Position).ToUnity();
            _renderError = correction.sqrMagnitude > SnapDistance * SnapDistance ? Vector3.zero : _renderError + correction;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
```

`Client/Assets/Scripts/Game/RemotePlayerInterpolator.cs`의 `TrySample` 바로 앞에 추가:

```csharp
        // Forget all samples (respawn teleport): the next Push starts a new history.
        public void Clear()
        {
            _count = 0;
            _newest = -1;
        }

```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 전체 PASS

- [ ] **Step 5: 입력 키와 뷰**

`Client/Assets/Scripts/Input/InputReader.cs` 전체:

```csharp
using System;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectH.Client.Input
{
    // The only place that talks to the Unity Input System; game code reads plain values from here.
    // Actions are created in code (no .inputactions asset dependency) and disposed in Dispose().
    public sealed class InputReader : IDisposable
    {
        private readonly InputAction _move;
        private readonly InputAction _look;
        private readonly InputAction _jump;
        private readonly InputAction _sprint;
        private readonly InputAction _fire;
        private readonly InputAction _aim;
        private readonly InputAction _reload;
        private readonly InputAction _slot1;
        private readonly InputAction _slot2;
        private readonly InputAction _unlockCursor;

        public InputReader()
        {
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            _look = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
            _jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
            _sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            // Left click: locks the cursor while it is free, fires while it is locked (D12, GameClient).
            _fire = new InputAction("Fire", InputActionType.Button, "<Mouse>/leftButton");
            _aim = new InputAction("Aim", InputActionType.Button, "<Mouse>/rightButton");
            _reload = new InputAction("Reload", InputActionType.Button, "<Keyboard>/r");
            _slot1 = new InputAction("Slot1", InputActionType.Button, "<Keyboard>/1");
            _slot2 = new InputAction("Slot2", InputActionType.Button, "<Keyboard>/2");
            _unlockCursor = new InputAction("UnlockCursor", InputActionType.Button, "<Keyboard>/escape");

            _move.Enable();
            _look.Enable();
            _jump.Enable();
            _sprint.Enable();
            _fire.Enable();
            _aim.Enable();
            _reload.Enable();
            _slot1.Enable();
            _slot2.Enable();
            _unlockCursor.Enable();
        }

        public Vector2 Move => _move.ReadValue<Vector2>();
        public Vector2 LookDelta => _look.ReadValue<Vector2>();
        public bool Sprint => _sprint.IsPressed();
        public bool FirePressed => _fire.WasPressedThisFrame();
        public bool FireHeld => _fire.IsPressed();
        public bool AimHeld => _aim.IsPressed();
        public bool UnlockCursorPressed => _unlockCursor.WasPressedThisFrame();

        // Jump, Reload, Slot1 and Slot2 presses since the last simulation step that used them. Rendering runs
        // faster than the fixed simulation, so a press between two steps must be remembered, not lost;
        // LocalPlayerPredictor.Advance clears the bits it puts into an input.
        public InputButtons QueuedButtons { get; set; }

        // Call once per rendered frame.
        public void Update()
        {
            if (_jump.WasPressedThisFrame()) QueuedButtons |= InputButtons.Jump;
            if (_reload.WasPressedThisFrame()) QueuedButtons |= InputButtons.Reload;
            if (_slot1.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot1;
            if (_slot2.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot2;
        }

        public void Dispose()
        {
            _move.Dispose();
            _look.Dispose();
            _jump.Dispose();
            _sprint.Dispose();
            _fire.Dispose();
            _aim.Dispose();
            _reload.Dispose();
            _slot1.Dispose();
            _slot2.Dispose();
            _unlockCursor.Dispose();
        }
    }
}
```

`Client/Assets/Scripts/Game/PlayerViewFactory.cs` 전체:

```csharp
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Capsule views are created on spawn and destroyed on despawn (rare events), so no pooling.
    // Materials are created once per color and shared through sharedMaterial: never use
    // renderer.material, which silently clones a material per object.
    public static class PlayerViewFactory
    {
        // Built-in "Ignore Raycast" layer. Remote views keep a collider there so the crosshair ray can land on a
        // player (the aim point must be on the target, D2). The camera SphereCast uses DefaultRaycastLayers, which
        // excludes this layer, so other players never push the camera; aim and fire rays add it via AimRaycastMask.
        public const int RemoteHitLayer = 2;
        public const int AimRaycastMask = Physics.DefaultRaycastLayers | (1 << RemoteHitLayer);

        private static Material _localMaterial;
        private static Material _remoteMaterial;
        private static Material _deadMaterial;

        public static Transform Create(string name, bool isLocal)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;

            if (isLocal)
            {
                // The aim ray starts at the shoulder next to our own capsule; it must never hit it.
                Object.Destroy(go.GetComponent<Collider>());
            }
            else
            {
                // Sized like the server's hit box (movement AABB: half width 0.35 m, height 1.8 m) with the feet
                // at the bottom. The view's origin is 1 m above the feet (Pose).
                var capsule = go.GetComponent<CapsuleCollider>();
                capsule.radius = MoveSettings.HalfWidth;
                capsule.height = MoveSettings.Height;
                capsule.center = new Vector3(0f, MoveSettings.Height * 0.5f - 1f, 0f);
                go.layer = RemoteHitLayer;
            }

            var renderer = go.GetComponent<Renderer>();
            EnsureMaterials(renderer.sharedMaterial);
            renderer.sharedMaterial = isLocal ? _localMaterial : _remoteMaterial;
            return go.transform;
        }

        // D13: dead players are grey and lying down. A dead remote player's collider is off, so shots and the
        // crosshair go through the body. collider may be null (the local view has none).
        public static void SetAlive(Renderer renderer, Collider collider, bool isLocal, bool alive)
        {
            if (renderer != null) renderer.sharedMaterial = alive ? (isLocal ? _localMaterial : _remoteMaterial) : _deadMaterial;
            if (collider != null) collider.enabled = alive;
        }

        // Upright: capsule centre 1 m above the feet. Dead: on its side along the facing direction.
        public static void Pose(Vector3 feet, float yaw, bool alive, out Vector3 position, out Quaternion rotation)
        {
            if (alive)
            {
                position = feet + Vector3.up;
                rotation = Quaternion.Euler(0f, yaw, 0f);
            }
            else
            {
                position = feet + new Vector3(0f, 0.5f, 0f);
                rotation = Quaternion.Euler(90f, yaw, 0f);
            }
        }

        // Called by GameClient.OnDestroy: the cached materials live exactly as long as the client.
        public static void ReleaseMaterials()
        {
            if (_localMaterial != null) Object.Destroy(_localMaterial);
            if (_remoteMaterial != null) Object.Destroy(_remoteMaterial);
            if (_deadMaterial != null) Object.Destroy(_deadMaterial);
            _localMaterial = null;
            _remoteMaterial = null;
            _deadMaterial = null;
        }

        private static void EnsureMaterials(Material template)
        {
            // Explicit == null (not ??=): Unity's null check also catches destroyed materials.
            if (_localMaterial == null) _localMaterial = Tinted(template, new Color(0.2f, 0.6f, 1f));
            if (_remoteMaterial == null) _remoteMaterial = Tinted(template, new Color(1f, 0.45f, 0.2f));
            if (_deadMaterial == null) _deadMaterial = Tinted(template, new Color(0.45f, 0.45f, 0.45f));
        }

        // Copies the primitive's default material, so the shader is guaranteed to be in the build.
        private static Material Tinted(Material template, Color color)
        {
            return new Material(template) { color = color };
        }
    }
}
```

`Client/Assets/Scripts/Game/RemotePlayers.cs` 전체:

```csharp
using System.Collections.Generic;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Views of other players. An entry is added on PlayerSpawned and removed on PlayerDespawned or
    // Clear() (disconnect / destroy), so the dictionary cannot outlive the match.
    // Alive or dead comes from each snapshot's flag, not from the PlayerDied/PlayerRespawned events: a
    // client that joins while someone is dead gets no event for it, but every snapshot carries the flag.
    public sealed class RemotePlayers
    {
        private sealed class Entry
        {
            public Transform View;
            public Renderer Renderer;
            public Collider Collider;
            public RemotePlayerInterpolator Interpolator;
            public bool Alive = true;
        }

        private readonly Dictionary<ushort, Entry> _entries = new Dictionary<ushort, Entry>();

        public int Count => _entries.Count;

        public void Spawn(in PlayerSpawned spawned, uint tick)
        {
            if (_entries.ContainsKey(spawned.EntityId)) return;
            Transform view = PlayerViewFactory.Create($"Player {spawned.EntityId}", false);
            var entry = new Entry
            {
                View = view,
                Renderer = view.GetComponent<Renderer>(),
                Collider = view.GetComponent<Collider>(),
                Interpolator = new RemotePlayerInterpolator(),
            };
            // A non-finite spawn position is rejected by the interpolator; the view then stays
            // at the origin until the first finite snapshot arrives.
            entry.Interpolator.Push(tick, spawned.Position.ToUnity(), spawned.Yaw);
            if (entry.Interpolator.TrySample(tick, out Vector3 start, out float startYaw))
                entry.View.SetPositionAndRotation(start + Vector3.up, Quaternion.Euler(0f, startYaw, 0f));
            _entries.Add(spawned.EntityId, entry);
        }

        public void Despawn(ushort entityId)
        {
            if (_entries.Remove(entityId, out Entry entry)) Object.Destroy(entry.View.gameObject);
        }

        public void Push(uint tick, in SnapshotEntity entity)
        {
            if (!_entries.TryGetValue(entity.EntityId, out Entry entry)) return;

            bool alive = entity.IsAlive;
            if (alive != entry.Alive)
            {
                // A respawn is a teleport: drop the old samples so the view does not slide from the body to
                // the spawn point. Sequenced snapshots arrive in order, so no older "dead" sample follows.
                if (alive) entry.Interpolator.Clear();
                entry.Alive = alive;
                PlayerViewFactory.SetAlive(entry.Renderer, entry.Collider, false, alive);
            }
            entry.Interpolator.Push(tick, entity.Position.ToUnity(), entity.Yaw);
        }

        public void Render(double renderTick)
        {
            foreach (var pair in _entries)
            {
                Entry entry = pair.Value;
                if (!entry.Interpolator.TrySample(renderTick, out Vector3 feet, out float yaw)) continue;
                PlayerViewFactory.Pose(feet, yaw, entry.Alive, out Vector3 position, out Quaternion rotation);
                entry.View.SetPositionAndRotation(position, rotation);
            }
        }

        public void Clear()
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.View != null) Object.Destroy(pair.Value.View.gameObject);
            }
            _entries.Clear();
        }
    }
}
```

- [ ] **Step 6: `GameClient`를 새 시그니처에 맞춘다 (동작은 Phase 1과 같음)**

`Client/Assets/Scripts/Game/GameClient.cs`의 `Update`:

```csharp
            bool jump = _input.JumpQueued;
            int steps = _predictor.Advance(Time.deltaTime, _input.Move, _camera.Yaw, _input.Sprint, ref jump);
            _input.JumpQueued = jump;
```
→
```csharp
            InputButtons held = _input.Sprint ? InputButtons.Sprint : InputButtons.None;
            InputButtons queued = _input.QueuedButtons;
            int steps = _predictor.Advance(Time.deltaTime, _input.Move, _camera.Yaw, held, ref queued);
            _input.QueuedButtons = queued;
```

`OnSpawned`:

```csharp
                // A Space pressed while waiting for the spawn must not fire a jump on the first step.
                _input.JumpQueued = false;
```
→
```csharp
                // A key pressed while waiting for the spawn must not act on the first step.
                _input.QueuedButtons = InputButtons.None;
```

- [ ] **Step 7: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 전체 PASS

- [ ] **Step 8: Unity Editor import 확인 (Task 1–8)**

사용자에게 Unity Editor를 한 번 포커스해 자동 import·컴파일이 끝나게 해 달라고 요청한 뒤, Task 1 Step 0의 `N0` 이후만 본다.

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; tail -n +N0 "$LOG" | grep -n "error CS"; wc -l < "$LOG"
```

Expected: `error CS` 출력 없음. 출력이 있으면 그 줄 뒤에 오류 없는 컴파일(`error CS` 없이 끝난 다음 스크립트 컴파일)이 있는지 보고, 마지막 컴파일에 오류가 없으면 통과로 본다. 마지막 줄 수를 `N8`로 기록한다. (Editor가 Phase 1 상태로 Play 중이면 새 서버와 ProtocolVersion이 달라 접속이 거절된다. Play를 멈추고 확인한다.)

- [ ] **Step 9: 체크포인트**

결과와 `N8`을 기록한다. 커밋하지 않는다.

---


### Task 9: Client 발사 연출·HUD·전체 연결 (`LocalFireEffects`, `CombatHud`, `GameClient`)

**Files:**
- Create: `Client/Assets/Scripts/Game/CombatHud.cs`, `Client/Assets/Tests/EditMode/RingCursorTests.cs`
- Modify: `Client/Assets/Scripts/Game/LocalFireEffects.cs` (전체 교체), `Client/Assets/Scripts/Game/GameClient.cs` (전체 교체), `Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs:61`
- Delete: `Client/Assets/Scripts/Game/FireRateAccumulator.cs` + `.meta`, `Client/Assets/Tests/EditMode/FireRateAccumulatorTests.cs` + `.meta`

**Interfaces:**
- Consumes: Task 7 (`NetClient` 이벤트, `AimSolver`, `WeaponState`), Task 8 (`LocalPlayerPredictor`, `InputReader.QueuedButtons`, `PlayerViewFactory`, `RemotePlayers`)
- Produces:
  - `LocalFireEffects`: `void FireLocal(int shots, Vector3 aimPoint, Vector3 feet, float yaw, float now)`(프레임당 최대 3발), `void ShowRemoteShot(Vector3 start, Vector3 end, float now)`, `void Tick(float now)`, `HideAll()`, `Dispose()`. 풀은 궤적 16·탄착 32 그대로.
  - `CombatHud`: `const float RespawnSeconds = 3f`, `SetVisible(bool)`, `SetVitals(int health, int shield)`, `SetWeapon(string name, int ammo, int magazine, bool reloading)`, `ClearWeapon()`, `ShowHit(bool killed, float now)`, `ShowDamage(Vector3 fromDirection, float now)`, `ShowDeath(float now)`, `HideDeath()`, `Tick(float cameraYaw, float now)`, `Dispose()`
  - `GameClient` 프레임 흐름: `Update` = Poll → 입력 → 커서 → 원격 렌더(렌더 Tick 저장) → Look → `Advance`(held: Sprint·Fire, queued: Jump·Reload·Slot) → 로컬 뷰 자세. `LateUpdate` = 카메라 → 조준점(Raycast, `AimRaycastMask`) → `SetAim` → `WeaponState.Step`(새 입력마다) → 입력 전송 → 발사 연출 → HUD.

- [ ] **Step 1: `RingCursor` 테스트를 옮기고 `FireRateAccumulator`를 지운다**

(Step 1–5 사이에는 Client 코드가 컴파일되지 않는다. Step 7 전까지 사용자에게 Unity Editor를 포커스하지 말아 달라고 요청한다.)

`Client/Assets/Tests/EditMode/RingCursorTests.cs` (신규, `FireRateAccumulatorTests`에 있던 테스트 그대로):

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    public class RingCursorTests
    {
        // The effect pools must not grow however long the trigger is held. LocalFireEffects allocates its
        // arrays once with the cursor capacity (16 tracers, 32 impacts) and only indexes them through Next().
        [Test]
        public void RingCursor_WrapsWithinCapacity()
        {
            var ring = new RingCursor(16);
            for (int i = 0; i < 1000; i++)
            {
                int slot = ring.Next();
                Assert.AreEqual(i % 16, slot);
            }
            Assert.AreEqual(16, ring.Capacity);
        }
    }
}
```

지운다(짝 `.meta` 포함):

```bash
rm -f Client/Assets/Scripts/Game/FireRateAccumulator.cs Client/Assets/Scripts/Game/FireRateAccumulator.cs.meta
rm -f Client/Assets/Tests/EditMode/FireRateAccumulatorTests.cs Client/Assets/Tests/EditMode/FireRateAccumulatorTests.cs.meta
```

- [ ] **Step 2: 컴파일 실패 확인**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: FAIL — `LocalFireEffects.cs`에서 `FireRateAccumulator`를 찾을 수 없음

- [ ] **Step 3: `LocalFireEffects`**

`Client/Assets/Scripts/Game/LocalFireEffects.cs` 전체. 발사 수는 이제 호출자가 `WeaponState`로 정하고, 조준점은 `GameClient`가 한 번 구해 넘긴다(Raycast 중복 제거). 총구 광선은 원격 플레이어 레이어도 본다.

```csharp
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Fire presentation: tracers and impact marks, no damage (D12). Own shots are drawn at once when the
    // local WeaponState says the server will fire them; other players' shots come from ShotFired.
    // Everything is created once in the constructor and reused through fixed ring pools, so firing
    // allocates nothing and memory stays constant. Dispose destroys it all.
    public sealed class LocalFireEffects : System.IDisposable
    {
        public const int TracerPoolSize = 16;
        public const int ImpactPoolSize = 32;
        private const int MaxShotsPerFrame = 3;   // a hitch frame never bursts a pile of effects
        private const float TracerSeconds = 0.05f;
        private const float TracerWidth = 0.02f;
        private const float ImpactSize = 0.1f;
        private const float ImpactLift = 0.01f;   // keeps the mark in front of the surface
        private const float MuzzleHeight = 1.4f;
        // Right and forward offsets of the muzzle from the feet. Their horizontal length
        // (0.24 * sqrt 2 = 0.34 m) is inside the 0.35 m collision half-width at every yaw, and the
        // simulation keeps that box out of every wall, so the muzzle ray normally starts outside a collider
        // (a ray ignores the collider its origin is in). The origin uses RenderPosition, which carries the
        // decaying reconcile offset, so for about 0.1-0.3 s after a misprediction near a wall it can be inside one.
        private const float MuzzleRight = 0.24f;
        private const float MuzzleForward = 0.24f;

        private readonly GameObject _root;
        private readonly Material _material;
        private readonly LineRenderer[] _tracers = new LineRenderer[TracerPoolSize];
        private readonly float[] _tracerHideTime = new float[TracerPoolSize];
        private readonly GameObject[] _impacts = new GameObject[ImpactPoolSize];
        private readonly RingCursor _nextTracer = new RingCursor(TracerPoolSize);
        private readonly RingCursor _nextImpact = new RingCursor(ImpactPoolSize);

        public LocalFireEffects()
        {
            _root = new GameObject("LocalFireEffects");

            for (int i = 0; i < ImpactPoolSize; i++)
            {
                var impact = GameObject.CreatePrimitive(PrimitiveType.Cube);
                impact.name = "Impact";
                // Marks must not block later shots or the camera.
                Object.Destroy(impact.GetComponent<Collider>());
                var renderer = impact.GetComponent<Renderer>();
                // One material for all effects, copied from the primitive's default so its shader is
                // guaranteed to be in the build. Never renderer.material (clones per object).
                if (_material == null) _material = new Material(renderer.sharedMaterial) { color = new Color(1f, 0.85f, 0.2f) };
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                impact.transform.SetParent(_root.transform, false);
                impact.transform.localScale = new Vector3(ImpactSize, ImpactSize, ImpactSize);
                impact.SetActive(false);
                _impacts[i] = impact;
            }

            for (int i = 0; i < TracerPoolSize; i++)
            {
                var tracerObject = new GameObject("Tracer");
                tracerObject.transform.SetParent(_root.transform, false);
                var line = tracerObject.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.startWidth = TracerWidth;
                line.endWidth = TracerWidth;
                line.sharedMaterial = _material;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.enabled = false;
                _tracers[i] = line;
            }
        }

        // Own shots this frame (from WeaponState), drawn after the camera moved (LateUpdate). aimPoint is what
        // the crosshair is on; the first thing between the muzzle and it is where the shot lands (Phase 1 D13).
        public void FireLocal(int shots, Vector3 aimPoint, Vector3 feet, float yaw, float now)
        {
            if (_root == null || shots <= 0) return;   // pool destroyed externally (e.g. scene unload)
            if (shots > MaxShotsPerFrame) shots = MaxShotsPerFrame;
            Vector3 muzzle = MuzzlePosition(feet, yaw);
            for (int i = 0; i < shots; i++) FireOne(aimPoint, muzzle, now);
        }

        // D11: another player's shot as the server resolved it, from its eye to where it stopped.
        public void ShowRemoteShot(Vector3 start, Vector3 end, float now)
        {
            if (_root == null) return;
            ShowTracer(start, end, now);
        }

        // Call once per frame: hides tracers whose time is up.
        public void Tick(float now)
        {
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++)
            {
                if (_tracers[i].enabled && now >= _tracerHideTime[i]) _tracers[i].enabled = false;
            }
        }

        public void HideAll()
        {
            // Unity null: on scene or play-mode teardown the root (and the pooled children with it) can
            // be destroyed before the owner's OnDestroy calls this; throwing would skip its later Dispose calls.
            if (_root == null) return;
            for (int i = 0; i < TracerPoolSize; i++) _tracers[i].enabled = false;
            for (int i = 0; i < ImpactPoolSize; i++) _impacts[i].SetActive(false);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }

        private static Vector3 MuzzlePosition(Vector3 feet, float yawDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(yaw);
            float cos = Mathf.Cos(yaw);
            // right = (cos, 0, -sin), forward = (sin, 0, cos), as in MovementSimulation.
            return new Vector3(
                feet.x + cos * MuzzleRight + sin * MuzzleForward,
                feet.y + MuzzleHeight,
                feet.z - sin * MuzzleRight + cos * MuzzleForward);
        }

        private void FireOne(Vector3 aimPoint, Vector3 muzzle, float now)
        {
            Vector3 toAim = aimPoint - muzzle;
            float distance = toAim.magnitude;
            if (distance < 0.01f) return;
            Vector3 direction = toAim / distance;

            // A little past the aim point so a shot aimed at a surface registers the hit on it. Remote players
            // are on PlayerViewFactory.RemoteHitLayer, so the mask includes that layer.
            if (Physics.Raycast(muzzle, direction, out RaycastHit hit, distance + 0.05f, PlayerViewFactory.AimRaycastMask, QueryTriggerInteraction.Ignore))
            {
                ShowTracer(muzzle, hit.point, now);
                ShowImpact(hit.point, hit.normal);
            }
            else
            {
                ShowTracer(muzzle, aimPoint, now);
            }
        }

        private void ShowTracer(Vector3 from, Vector3 to, float now)
        {
            int slot = _nextTracer.Next();
            LineRenderer line = _tracers[slot];
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.enabled = true;
            _tracerHideTime[slot] = now + TracerSeconds;
        }

        private void ShowImpact(Vector3 point, Vector3 normal)
        {
            GameObject impact = _impacts[_nextImpact.Next()];
            impact.transform.SetPositionAndRotation(point + normal * ImpactLift, Quaternion.LookRotation(normal));
            if (!impact.activeSelf) impact.SetActive(true);
        }
    }
}
```

- [ ] **Step 4: `CombatHud`**

`Client/Assets/Scripts/Game/CombatHud.cs` (신규). Unity 6의 내장 폰트 이름은 `LegacyRuntime.ttf`다(계획 작성 시 `Editor/Data/Resources/unity default resources`에 이 이름이 있음을 확인했다. `Arial.ttf`는 Unity 2022.2부터 내장 리소스가 아니다). 문구는 영어로 둔다(내장 폰트에 한글 글리프가 보장되지 않음).

```csharp
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // D13: combat HUD built in code on one Screen Space Overlay canvas (UGUI legacy Text, built-in font; no
    // TextMeshPro, which needs imported assets). No GraphicRaycaster and nothing is a raycast target.
    // Strings are rebuilt only when a shown value changes, so an idle HUD allocates nothing per frame.
    // Dispose destroys the canvas.
    public sealed class CombatHud : System.IDisposable
    {
        // Display only. Must match the server's CombatRules.RespawnSeconds (the server decides the real time).
        public const float RespawnSeconds = 3f;
        private const float HitMarkerSeconds = 0.15f;
        private const float DamageIndicatorSeconds = 1f;
        private const float DamageIndicatorRadius = 90f;
        private const int FontSize = 22;

        private readonly GameObject _root;
        private readonly Text _vitals;
        private readonly Text _weapon;
        private readonly Text _center;
        private readonly GameObject _hitMarker;
        private readonly Image[] _hitBars = new Image[4];
        private readonly RectTransform _damageIndicator;

        private bool _visible;
        private int _health = -1;
        private int _shield = -1;
        private string _weaponName;
        private int _ammo = -1;
        private int _magazine = -1;
        private bool _reloading;
        private float _hitHideTime;
        private float _damageHideTime;
        private float _damageYaw;       // world yaw of the attacker direction, degrees
        private float _respawnAt = -1f; // local time of the expected respawn; < 0 when alive
        private int _countdown = -1;

        public CombatHud()
        {
            _root = new GameObject("CombatHud");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90;   // under the crosshair (100)

            // Unity 6 built-in font; Arial.ttf is no longer a built-in resource.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _vitals = CreateText("Vitals", font, new Vector2(0f, 0f), new Vector2(20f, 20f), TextAnchor.LowerLeft);
            _weapon = CreateText("Weapon", font, new Vector2(1f, 0f), new Vector2(-20f, 20f), TextAnchor.LowerRight);
            _center = CreateText("Center", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -80f), TextAnchor.MiddleCenter);

            _hitMarker = new GameObject("HitMarker", typeof(RectTransform));
            var markerRect = (RectTransform)_hitMarker.transform;
            markerRect.SetParent(_root.transform, false);
            markerRect.anchorMin = new Vector2(0.5f, 0.5f);
            markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < 4; i++)
            {
                float angle = 45f + 90f * i;
                float rad = angle * Mathf.Deg2Rad;
                _hitBars[i] = CreateBar(markerRect, new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * 14f, new Vector2(10f, 2f), angle);
            }
            _hitMarker.SetActive(false);

            Image indicator = CreateBar((RectTransform)_root.transform, Vector2.zero, new Vector2(40f, 6f), 0f);
            indicator.color = new Color(1f, 0.2f, 0.2f, 0.85f);
            _damageIndicator = indicator.rectTransform;
            _damageIndicator.anchorMin = new Vector2(0.5f, 0.5f);
            _damageIndicator.anchorMax = new Vector2(0.5f, 0.5f);
            _damageIndicator.gameObject.SetActive(false);

            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void SetVitals(int health, int shield)
        {
            if (_root == null || (health == _health && shield == _shield)) return;
            _health = health;
            _shield = shield;
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
        {
            if (_root == null || _weaponName == null) return;
            _weaponName = null;
            _weapon.text = string.Empty;
        }

        // HitConfirmed: white cross, red on a kill.
        public void ShowHit(bool killed, float now)
        {
            if (_root == null) return;
            Color color = killed ? new Color(1f, 0.2f, 0.2f) : Color.white;
            for (int i = 0; i < _hitBars.Length; i++) _hitBars[i].color = color;
            _hitMarker.SetActive(true);
            _hitHideTime = now + HitMarkerSeconds;
        }

        // DamageTaken: a bar around the crosshair pointing to where the shot came from.
        public void ShowDamage(Vector3 fromDirection, float now)
        {
            if (_root == null) return;
            if (fromDirection.x * fromDirection.x + fromDirection.z * fromDirection.z < 1e-6f) return;
            _damageYaw = Mathf.Atan2(fromDirection.x, fromDirection.z) * Mathf.Rad2Deg;
            _damageIndicator.gameObject.SetActive(true);
            _damageHideTime = now + DamageIndicatorSeconds;
        }

        public void ShowDeath(float now)
        {
            _respawnAt = now + RespawnSeconds;
            _countdown = -1;
        }

        public void HideDeath()
        {
            _respawnAt = -1f;
            _countdown = -1;
            if (_center != null) _center.text = string.Empty;
        }

        // Once per frame (LateUpdate, after the camera): timers, indicator direction, countdown.
        public void Tick(float cameraYaw, float now)
        {
            if (_root == null) return;
            if (_hitMarker.activeSelf && now >= _hitHideTime) _hitMarker.SetActive(false);

            if (_damageIndicator.gameObject.activeSelf)
            {
                if (now >= _damageHideTime)
                {
                    _damageIndicator.gameObject.SetActive(false);
                }
                else
                {
                    // Relative to where the camera looks: 0 = in front (top of the screen), 90 = right.
                    float relative = (_damageYaw - cameraYaw) * Mathf.Deg2Rad;
                    _damageIndicator.anchoredPosition = new Vector2(Mathf.Sin(relative), Mathf.Cos(relative)) * DamageIndicatorRadius;
                    _damageIndicator.localRotation = Quaternion.Euler(0f, 0f, -relative * Mathf.Rad2Deg);
                }
            }

            if (_respawnAt >= 0f)
            {
                int seconds = Mathf.Max(0, Mathf.CeilToInt(_respawnAt - now));
                if (seconds != _countdown)
                {
                    _countdown = seconds;
                    _center.text = "DEAD   respawn in " + seconds;
                }
            }
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
            rect.sizeDelta = new Vector2(420f, 40f);

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

        private static Image CreateBar(RectTransform parent, Vector2 offset, Vector2 size, float angle)
        {
            var go = new GameObject("Bar", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
            var image = go.AddComponent<Image>();   // no sprite: draws a solid rectangle
            image.raycastTarget = false;
            return image;
        }
    }
}
```

- [ ] **Step 5: `GameClient` 전체 연결**

`Client/Assets/Scripts/Game/GameClient.cs` 전체. Phase 1의 D12 좌클릭 규칙(`UpdateCursorAndButtons`)은 그대로다. 바뀐 점:
- 입력 전송이 `Update`에서 `LateUpdate`로 옮겨졌다. 카메라가 움직인 뒤의 조준점으로 이번 프레임 입력들의 AimYaw/AimPitch/ViewTick을 채우고 보낸다(Spec 해석 6).
- `ViewTick`은 이번 프레임 원격 플레이어를 그린 `ServerClock.RenderTick` 값이다.
- 내 사망·부활은 `PlayerDied`/`PlayerRespawned`로 예측기와 로컬 뷰에 반영한다(Seq 유지). 다른 플레이어는 `RemotePlayers`가 Snapshot 비트로 처리한다.
- 내 `ShotFired`는 무시한다(D12). HUD의 Health/Shield는 Snapshot 수신자 블록, 탄·무기는 `WeaponState`에서 읽는다.

```csharp
using System;
using ProjectH.Client.Bootstrap;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Input;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Composition root of the client. Everything it creates is released in OnDestroy
    // (which also runs on application quit), in reverse order of creation.
    public sealed class GameClient : MonoBehaviour
    {
        // Render remote players two snapshot intervals in the past so one late or lost snapshot
        // still leaves a sample to interpolate towards.
        private const double InterpolationSnapshots = 2.0;
        // Aim ray length before the weapon catalog arrives.
        private const float DefaultAimRange = 300f;

        private GameObject _world;
        private Material _worldMaterial;
        private InputReader _input;
        private ShoulderCamera _camera;
        private Crosshair _crosshair;
        private CombatHud _hud;
        private LocalFireEffects _fireEffects;
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
        private Transform _localView;
        private Renderer _localRenderer;
        private readonly RemotePlayers _remotePlayers = new RemotePlayers();
        private ServerClock _clock;
        private WeaponState _weapons;
        private int _simHz;
        private double _interpolationDelaySeconds;
        private double _renderTick;       // server tick remote players are drawn at this frame (ViewTick, D6)
        private int _pendingSteps;        // inputs predicted in Update, aimed and sent in LateUpdate
        private int _health;
        private int _shield;
        private bool _aiming;
        private bool _fireHeld;
        // D12: the click that locks the cursor must not also fire; fire waits for that button's release.
        private bool _fireBlockedUntilRelease;

        public ClientState State => _net.State;
        public string LastError => _net.LastError;
        public int RoundTripMs => _net.RoundTripMs;
        public ushort MyEntityId { get; private set; }

        public void Connect(string host, int port, string devPlayerId) => _net.Connect(host, port, devPlayerId);

        public void Disconnect() => _net.Disconnect();

        private void Awake()
        {
            _world = TestWorld.Build(out _worldMaterial);
            _input = new InputReader();

            Camera main = Camera.main;
            if (main == null)
            {
                var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
                main = cameraGo.AddComponent<Camera>();
                cameraGo.AddComponent<AudioListener>();
            }
            _camera = new ShoulderCamera(main);
            _crosshair = new Crosshair();
            _hud = new CombatHud();
            _fireEffects = new LocalFireEffects();

            _net = new NetClient();
            _net.Joined += OnJoined;
            _net.SpawnReceived += OnSpawned;
            _net.DespawnReceived += OnDespawned;
            _net.SnapshotReceived += OnSnapshot;
            _net.Disconnected += OnDisconnected;
            _net.CatalogReceived += OnCatalog;
            _net.ShotReceived += OnShot;
            _net.HitConfirmedReceived += OnHitConfirmed;
            _net.DamageTakenReceived += OnDamageTaken;
            _net.PlayerDiedReceived += OnPlayerDied;
            _net.PlayerRespawnedReceived += OnPlayerRespawned;
        }

        private void Update()
        {
            _net.Poll();
            _input.Update();
            UpdateCursorAndButtons();

            if (_clock != null && _clock.IsReady)
            {
                _renderTick = _clock.RenderTick(Time.unscaledTimeAsDouble, _interpolationDelaySeconds);
                _remotePlayers.Render(_renderTick);
            }

            if (_predictor == null) return;

            _camera.ApplyLook(_input.LookDelta, _aiming);
            InputButtons held = InputButtons.None;
            if (_input.Sprint) held |= InputButtons.Sprint;
            if (_fireHeld) held |= InputButtons.Fire;
            InputButtons queued = _input.QueuedButtons;
            _pendingSteps += _predictor.Advance(Time.deltaTime, _input.Move, _camera.Yaw, held, ref queued);
            _input.QueuedButtons = queued;

            PlayerViewFactory.Pose(_predictor.RenderPosition, _predictor.RenderYaw, !_predictor.IsDead, out Vector3 position, out Quaternion rotation);
            _localView.SetPositionAndRotation(position, rotation);
        }

        private void LateUpdate()
        {
            if (_predictor == null) return;
            float now = Time.time;
            bool alive = !_predictor.IsDead;

            _camera.Follow(_predictor.RenderPosition, _aiming, Time.deltaTime);
            _crosshair.SetVisible(alive);

            // After the camera moved, so aim, tracer and the sent inputs all use the crosshair of this frame.
            Vector3 aimPoint = FindAimPoint();
            int shots = 0;
            if (_pendingSteps > 0)
            {
                Vector3 eye = _predictor.RenderPosition + new Vector3(0f, AimSolver.EyeHeight, 0f);
                if (!AimSolver.TrySolve(eye, aimPoint, out float aimYaw, out float aimPitch))
                {
                    aimYaw = _camera.Yaw;
                    aimPitch = _camera.Pitch;
                }
                _predictor.SetAim(_pendingSteps, aimYaw, aimPitch, (float)_renderTick);
                shots = StepWeapons(_pendingSteps);
                if (_predictor.TryBuildInputPacket(out PlayerInputPacket packet)) _net.SendInput(packet);
                _pendingSteps = 0;
            }

            if (alive && shots > 0) _fireEffects.FireLocal(shots, aimPoint, _predictor.RenderPosition, _camera.Yaw, now);
            _fireEffects.Tick(now);

            _hud.SetVitals(_health, _shield);
            if (_weapons != null) _hud.SetWeapon(_weapons.Current.Name, _weapons.Ammo, _weapons.Current.MagazineSize, _weapons.Reloading);
            else _hud.ClearWeapon();
            _hud.Tick(_camera.Yaw, now);
        }

        private void OnDestroy()
        {
            _net.Joined -= OnJoined;
            _net.SpawnReceived -= OnSpawned;
            _net.DespawnReceived -= OnDespawned;
            _net.SnapshotReceived -= OnSnapshot;
            _net.Disconnected -= OnDisconnected;
            _net.CatalogReceived -= OnCatalog;
            _net.ShotReceived -= OnShot;
            _net.HitConfirmedReceived -= OnHitConfirmed;
            _net.DamageTakenReceived -= OnDamageTaken;
            _net.PlayerDiedReceived -= OnPlayerDied;
            _net.PlayerRespawnedReceived -= OnPlayerRespawned;
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
            _hud.Dispose();
            _crosshair.Dispose();
            _input.Dispose();
            PlayerViewFactory.ReleaseMaterials();
            if (_world != null) Destroy(_world);
            if (_worldMaterial != null) Destroy(_worldMaterial);
        }

        // Left click locks a free cursor (only once joined) and fires while it is locked (D12).
        // Aim and fire only count while the cursor is locked, i.e. while the mouse controls the game.
        private void UpdateCursorAndButtons()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (_input.UnlockCursorPressed)
            {
                Cursor.lockState = CursorLockMode.None;
                locked = false;
            }
            else if (!locked && _input.FirePressed && State == ClientState.Joined)
            {
                Cursor.lockState = CursorLockMode.Locked;
                locked = true;
                _fireBlockedUntilRelease = true;
            }

            if (!_input.FireHeld) _fireBlockedUntilRelease = false;
            _fireHeld = locked && _input.FireHeld && !_fireBlockedUntilRelease;
            _aiming = locked && _input.AimHeld;
        }

        // What the crosshair is on. Remote players have colliders on PlayerViewFactory.RemoteHitLayer, so the
        // point can be on a player; the server then shoots from our eye towards it (D2).
        private Vector3 FindAimPoint()
        {
            float range = _weapons != null ? _weapons.Current.Range : DefaultAimRange;
            Ray ray = _camera.AimRay;
            // Remote views moved in Update. Physics.autoSyncTransforms is off by default, so without this the
            // ray would test their colliders where the last physics step left them.
            Physics.SyncTransforms();
            return Physics.Raycast(ray, out RaycastHit hit, range, PlayerViewFactory.AimRaycastMask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : ray.GetPoint(range);
        }

        // Runs the local weapon copy over this frame's new inputs, oldest first. Returns how many of them
        // the server is expected to fire.
        private int StepWeapons(int steps)
        {
            if (_weapons == null) return 0;
            int count = Math.Min(steps, LocalPlayerPredictor.HistorySize);
            uint newest = _predictor.LastSeq;
            int shots = 0;
            for (int i = count - 1; i >= 0; i--)
            {
                uint seq = newest - (uint)i;
                if (_weapons.Step(seq, _predictor.InputAt(seq).Buttons)) shots++;
            }
            return shots;
        }

        private void OnJoined(JoinMatchResponse response)
        {
            if (response.Result != JoinResult.Ok)
            {
                Debug.LogWarning($"Join failed: {response.Result}");
                return;
            }
            MyEntityId = response.MyEntityId;
            _simHz = response.SimHz;
            _interpolationDelaySeconds = InterpolationSnapshots / response.SnapshotHz;
            _clock = new ServerClock(response.SimHz);
            _clock.OnSnapshot(response.ServerTick, Time.unscaledTimeAsDouble);
            Debug.Log($"Joined as entity {response.MyEntityId} (SimHz {response.SimHz}, SnapshotHz {response.SnapshotHz})");
        }

        private void OnCatalog(WeaponInfo[] weapons)
        {
            _weapons = new WeaponState(weapons);
        }

        private void OnSpawned(PlayerSpawned spawned)
        {
            if (spawned.EntityId == MyEntityId)
            {
                if (_predictor != null) return;
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw });
                // A key pressed while waiting for the spawn must not act on the first step.
                _input.QueuedButtons = InputButtons.None;
                _localView = PlayerViewFactory.Create($"Player {spawned.EntityId} (you)", true);
                _localRenderer = _localView.GetComponent<Renderer>();
                _health = 0;
                _shield = 0;
                _hud.SetVisible(true);
                return;
            }
            _remotePlayers.Spawn(spawned, _clock != null ? _clock.LatestTick : 0);
        }

        private void OnDespawned(ushort entityId)
        {
            _remotePlayers.Despawn(entityId);
        }

        private void OnSnapshot(in WorldSnapshotHeader header, SnapshotEntity[] entities, int count)
        {
            if (_clock == null) return;
            _clock.OnSnapshot(header.ServerTick, Time.unscaledTimeAsDouble);

            // D10: our own health, shield and weapon come with every snapshot.
            _health = header.Self.Health;
            _shield = header.Self.Shield;
            if (_weapons != null) _weapons.ApplyServer(header.Self, header.AckInputSeq);

            for (int i = 0; i < count; i++)
            {
                if (entities[i].EntityId == MyEntityId)
                {
                    if (_predictor != null) _predictor.Reconcile(entities[i], header.AckInputSeq);
                }
                else
                {
                    _remotePlayers.Push(header.ServerTick, entities[i]);
                }
            }
        }

        // D12: our own shots were already drawn when predicted.
        private void OnShot(ShotFired shot)
        {
            if (shot.ShooterId == MyEntityId) return;
            _fireEffects.ShowRemoteShot(shot.Start.ToUnity(), shot.End.ToUnity(), Time.time);
        }

        private void OnHitConfirmed(HitConfirmed hit)
        {
            _hud.ShowHit(hit.Killed, Time.time);
        }

        private void OnDamageTaken(DamageTaken damage)
        {
            _hud.ShowDamage(damage.FromDirection.ToUnity(), Time.time);
        }

        private void OnPlayerDied(PlayerDied died)
        {
            if (died.VictimId != MyEntityId || _predictor == null) return;
            _predictor.SetDead();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, false);
            _hud.ShowDeath(Time.time);
        }

        private void OnPlayerRespawned(PlayerRespawned respawned)
        {
            // Other players' views follow their snapshot flags (RemotePlayers.Push).
            if (respawned.EntityId != MyEntityId || _predictor == null) return;
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw });
            _input.QueuedButtons = InputButtons.None;
            if (_weapons != null) _weapons.Refill();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, true);
            _hud.HideDeath();
        }

        private void OnDisconnected(string reason)
        {
            MyEntityId = 0;
            ClearMatchState();
            Cursor.lockState = CursorLockMode.None;
            Debug.Log($"Disconnected: {reason}");
        }

        private void ClearMatchState()
        {
            _predictor = null;
            if (_localView != null) Destroy(_localView.gameObject);
            _localView = null;
            _localRenderer = null;
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
}
```

`Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs`:

```csharp
            GUILayout.Label("F1: panel   Left click: lock mouse, then fire   Right click: aim   Esc: unlock");
```
→
```csharp
            GUILayout.Label("F1: panel   Left click: lock mouse, then fire   Right click: aim   R: reload   1/2: weapon   Esc: unlock");
```

- [ ] **Step 6: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 전체 PASS (`FireRateAccumulatorTests` 7개가 빠지고 `RingCursorTests` 1개가 더해진다)

- [ ] **Step 7: Unity Editor import 확인**

사용자에게 Editor 포커스를 요청한 뒤 Task 8의 `N8` 이후만 본다.

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; tail -n +N8 "$LOG" | grep -n -E "error CS|LegacyRuntime|Font"
```

Expected: `error CS` 없음(있으면 Task 8 Step 8과 같이 마지막 컴파일 결과만 판정한다). Play 시작 후 다시 실행해 `LegacyRuntime.ttf`를 찾지 못했다는 오류가 없는지도 본다(있으면 `Resources.GetBuiltinResource<Font>` 이름을 Console 오류에 나온 이름으로 고친다).

- [ ] **Step 8: 사용자 수동 확인 요청**

서버를 새로 빌드해 실행하고(`dotnet run --project Server/src/ProjectH.Server`), 두 Client(Multiplayer Play Mode 또는 Standalone + Editor)로 접속한다.
- HUD 왼쪽 아래 `HP 100   SH 50`, 오른쪽 아래 `Vesper AR   30 / 30`. 좌클릭을 누르고 있으면 초당 약 10발, 탄 수가 줄고, 0이 되면 `reloading...` 후 2초 뒤 30으로 돌아온다. R은 탄이 줄었을 때만 재장전한다.
- 2를 누르면 `Kestrel LR   5 / 5`. 누르고 있어도 한 발만 나가고, 다시 눌러도 1.25초 안에는 나가지 않는다. 1로 돌아오면 AR의 남은 탄이 그대로다.
- 상대를 조준점에 두고 쏘면 쏜 쪽 조준점에 흰 X가 잠깐 뜨고, 맞은 쪽 HUD의 SH가 먼저 줄고 그다음 HP가 준다. 맞은 쪽 조준점 주위에 공격 방향을 가리키는 빨간 막대가 1초 보인다. 상대의 궤적도 보인다.
- 기둥 뒤 상대의 몸이 조준점에 보이지 않는 한 맞지 않는다(벽 관통 없음).
- HP가 0이 되면 쏜 쪽 X가 빨간색, 맞은 쪽은 회색으로 누운 모습, 조준점이 사라지고 `DEAD   respawn in 3` → 2 → 1, 이동·발사가 되지 않는다. 약 3초 뒤 Spawn 위치에 원래 색으로 서고, HP 100·SH 50·탄 가득.
- 부활 직후 바로 걷고 쏠 수 있다(입력이 무시되거나 제자리로 끌려가지 않는다).
- 움직이는 상대(달리기)를 조준점에 걸고 쏴도 맞는다. (로컬·LAN 기준이다. 보간 지연 약 133 ms에 되감기 상한 200 ms(D6)라, 왕복 지연이 약 100 ms를 넘으면 빠르게 움직이는 상대의 가장자리는 빗나갈 수 있다. 버그가 아니라 D6의 비용이다.)
- 한쪽이 죽어 있는 동안 세 번째 Client가 접속하면 그 Client에서도 죽은 캐릭터가 회색으로 누워 있다.
- (선택) Profiler에서 GC Alloc: 가만히 있거나 걷는 프레임은 0, 연사 중에는 탄 수가 바뀌는 프레임에만 HUD 문자열 하나(수십 바이트)가 생긴다.

- [ ] **Step 9: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---


### Task 10: 문서 갱신

**Files:**
- Modify: `Docs/Networking.md`, `Docs/Server.md`, `Docs/Client.md`, `Docs/Architecture.md`

**Interfaces:**
- Consumes: Task 1–9의 동작
- Produces: 없음

- [ ] **Step 1: `Docs/Networking.md`**

4번째 줄의 `` `ProtocolVersion`(현재 2. Phase 1에서 박스 충돌로 이동 결과가 바뀌어 올렸다. 패킷 형식은 Phase 0과 같다) 불일치 연결은`` 을 `` `ProtocolVersion`(현재 3. Phase 3에서 입력 명령·Snapshot 형식이 바뀌고 전투 패킷이 생겼다) 불일치 연결은`` 으로 바꾼다.

`## Packets` 표의 `PlayerInput`, `WorldSnapshot` 행을 바꾸고 그 아래에 6행을 추가한다.

```markdown
| PlayerInput | C→S | Unreliable | 최근 입력 1–3개(Seq, MoveX, MoveY, Yaw, Buttons, AimYaw, AimPitch, ViewTick = 29B), 오래된 것부터 |
| WorldSnapshot | S→C | Sequenced | ServerTick, AckInputSeq(수신자별), Count, 수신자 블록(Health, Shield, WeaponSlot, Ammo, ReloadRemainingTicks = 6B, 수신자별), [EntityId, Position, VelocityY, Yaw, Flags(bit0 생존)] |
| WeaponCatalog | S→C | ReliableOrdered | Join 응답 직후 1회. 무기 1–8개: WeaponId, Name ≤ 16B, Damage, FireIntervalTicks, MagazineSize, ReloadTicks, Range, Automatic |
| ShotFired | S→C(전원) | Unreliable | ShooterId, Start(눈), End(멈춘 곳) |
| HitConfirmed | S→C(쏜 사람) | ReliableOrdered | TargetId, Damage, Killed |
| DamageTaken | S→C(맞은 사람) | ReliableOrdered | AttackerId, Damage, FromDirection(맞은 쪽 → 쏜 쪽 단위 벡터) |
| PlayerDied | S→C(전원) | ReliableOrdered | VictimId, KillerId |
| PlayerRespawned | S→C(전원) | ReliableOrdered | EntityId, Position, Yaw |
```

표 아래 첫 두 bullet을 다음으로 바꾼다.

```markdown
- Snapshot 헤더 11B + 수신자 블록 6B + 엔티티 23B. 분할되지 않으므로 `MaxPacketSize` 1200B 이내여야 한다 → 50명 = 1167B(`PacketTests`가 고정), 최대 50 엔티티(`MaxSnapshotEntities`), `MaxPlayers ≤ 50`(기본 16, 시작 시 검증). 서버는 payload를 한 번 쓰고 수신자마다 AckInputSeq와 수신자 블록만 덮어쓴다(`WorldSnapshotHeader.PatchRecipient`).
- Buttons는 알려진 비트(Jump, Sprint, Fire, Reload, Slot1, Slot2)만 남기고 나머지는 버린다. 입력 패킷은 최대 2 + 3 × 29 = 89B다.
- Client가 "누구를 맞혔다"고 보내는 필드는 없다. 명중은 서버가 조준 방향으로 판정한다.
```

`## Validation (서버)` 제목 바로 앞에 다음 절을 추가한다.

```markdown
## 전투 (Phase 3)

발사는 입력 명령에 실린다(D1): Fire 비트 + 조준(AimYaw, AimPitch) + ViewTick. 따라서 입력의 중복 전송·Seq 중복 제거·Tick당 1개 규칙을 그대로 따르고, 입력보다 빨리 쏠 수 없다.

- **조준(D2):** Client는 카메라 광선이 맞은 점(원격 플레이어는 서버 판정 상자 크기의 Collider가 있어 조준점이 몸 위에 온다)과 캐릭터 눈(발 + 1.6 m)을 이어 Yaw/Pitch를 구한다. 규약은 카메라와 같다(Yaw 0 = +Z, Yaw 90 = +X, 양의 Pitch = 아래). 서버는 같은 눈에서 같은 방향으로 쏘므로 어깨 카메라 시차가 있어도 조준점이 가리키는 곳을 맞힌다.
- **무기(D4, D5):** 수치는 서버 `weapons.json`에만 있다(Vesper AR: 20 / 3 Tick / 30발 / 60 Tick / 150 m / 자동, Kestrel LR: 90 / 38 Tick / 5발 / 75 Tick / 300 m / 단발). 시작 시 검증하고 틀리면 서버가 뜨지 않는다. Join 직후 `WeaponCatalog`로 Client에 간다. Slot1/Slot2 = 목록의 0/1번째.
- **서버 Tick 순서:** 부활 시각이 된 플레이어 부활 → 플레이어마다 입력 1개(죽어 있으면 Seq만 확인 응답, 이동·발사 없음, 중력도 없음) → 이동 Step → 재장전 완료 확인 → **실제로 받은 입력일 때만** 교체 → 재장전 → 발사 → `ServerTick++` → 모든 플레이어 위치를 History에 기록 → Snapshot. 누락 입력 반복(Phase 0 유예)은 이동만 반복하고 발사·교체·재장전은 하지 않는다.
- **무기 규칙(D14, 서버 Tick 기준):** 교체는 Slot 비트가 하나만 켜졌을 때(둘 다면 무시), 교체하면 재장전 취소. 재장전은 탄이 가득이 아닐 때만. 발사는 탄 > 0, 재장전 중 아님, `now ≥ NextFireTick[슬롯]`, 조준 각이 유한할 때만. 단발은 Fire를 새로 누른 입력에서만. 마지막 탄을 쏘거나 빈 탄창으로 쏘려 하면 자동 재장전. 탄과 발사 간격은 슬롯별이다.
- **판정(D7):** 눈에서 조준 방향으로 사거리까지, 아레나 박스와 y=0 바닥(slab 교차), 그리고 쏜 사람을 뺀 살아 있는 플레이어의 이동 AABB(반폭 0.35 m, 높이 1.8 m) 중 가장 가까운 것에 맞는다. 머리 판정·탄 퍼짐·반동은 없다.
- **Lag Compensation(D6):** 플레이어마다 32칸 위치 링(`PositionHistory`)에 매 Tick 끝 위치를 기록한다. 발사 판정은 다른 플레이어를 ViewTick 위치로 되감는다(두 기록 사이 보간). ViewTick은 `[최신 Tick − 6, 최신 Tick]`(0.2 s)으로 잘리고, NaN이면 최신 Tick이다. 기록이 모자라면 가장 오래된 기록을 쓴다. Join·부활 때 History를 새로 시작하므로 되감기가 시체 위치에 닿지 않는다. 쏜 사람 자신은 되감지 않는다. 원격 플레이어는 약 133 ms(4 Tick) 과거로 보이므로 지연·입력 버퍼에 남는 여유는 약 2 Tick이다. 왕복 지연이 약 100 ms를 넘으면 빠르게 움직이는 상대의 가장자리를 빗나갈 수 있다(D6의 비용).
- **피해·사망·부활(D8, D9):** Health 100, Shield 50. 피해는 Shield부터, 남은 만큼 Health, 0 아래로 내려가지 않는다. Health가 0이 되면 사망: `PlayerDied`(전원), 판정 대상에서 빠지고 입력은 무시된다. `SimHz × 3` Tick 뒤 `Match.SpawnPosition(id)`에서 Health·Shield·탄 가득, 슬롯 0으로 부활하고 `PlayerRespawned`(전원). `DamageTaken` → `PlayerDied` → `PlayerRespawned`는 같은 ReliableOrdered 채널이라 이 순서로 도착한다.
- **Client 예측 범위(D12):** 내 발사 연출은 `WeaponState`(서버 규칙의 표시용 사본, 예측 입력마다 1 Step)가 "서버가 쏠 것"이라고 할 때 바로 그린다. 서버 `ShotFired` 중 내 것은 무시한다. Snapshot 수신자 블록이 오면 ack 시점의 로컬 값과 비교해 다르면 서버 값으로 맞춘다. 명중·피해·사망은 서버 이벤트만 표시한다.
- **사망 중 예측:** `PlayerDied`(내 것)를 받으면 예측기는 이동하지 않고, Seq는 계속 올리되 이동 0·버튼 없음 입력을 보낸다(부활 직후 서버가 이 입력 일부를 살아 있는 상태로 처리하기 때문). 사망 중 Snapshot은 서버 위치로 바로 맞춘다. `PlayerRespawned`를 받으면 같은 예측기의 상태를 Spawn 위치로 되돌린다(Seq를 1부터 다시 세면 서버가 이미 소비한 Seq 이하를 버려 모든 입력이 무시된다). 생존 비트가 예측기 상태와 다른 Snapshot(다른 생의 것)은 재조정에 쓰지 않는다.
- **원격 플레이어:** 생존 여부는 Snapshot의 생존 비트로 정한다(죽어 있는 동안 들어온 Client는 `PlayerDied`를 받지 않았다). 죽으면 회색으로 눕고 Collider가 꺼진다. 다시 살아나면 보간 기록을 비워 시체 자리에서 미끄러지지 않고 Spawn 위치에 바로 나타난다.
```

`## Validation (서버)`의 마지막 bullet(`- 위치는 서버가 계산하므로 ...`) 앞에 추가한다.

```markdown
- 전투 입력: 조준 각이 NaN/Infinity면 그 입력은 발사하지 않는다(탄·간격 소모 없음). Pitch는 ±89°로 자른다. ViewTick은 되감기 범위로 자른다. Slot 비트가 둘 다 켜져 있으면 교체하지 않는다. 명중 대상은 Client가 정하지 않는다.
```

- [ ] **Step 2: `Docs/Server.md`**

`설정:` 문단(10번째 줄) 다음에 추가:

```markdown
무기 데이터: `Server/src/ProjectH.Server/weapons.json`(출력 폴더로 복사). 시작 시 `WeaponCatalog.LoadFile`이 읽고 검증한다(무기 1–8개, Id 1–255 중복 없음, 이름 1–16 UTF-8 바이트, damage 1–65535, magazineSize 1–255, fireIntervalSeconds·reloadSeconds > 0이고 Tick으로 바꿔 65535 이하, range > 0, spread·recoil ≥ 0, 모두 유한). 초 값은 `SimHz`로 반올림해 Tick으로 바꾼다(최소 1). 파일이 없거나 틀리면 `GameServerService` 생성자가 `InvalidOperationException`을 던져 서버가 시작하지 않는다.
```

`` `Match.Tick`은 플레이어마다 `MovementSimulation.Step(...)` `` 로 시작하는 문단 다음에 추가:

```markdown
전투(Phase 3, 규칙은 `Networking.md` "전투"): `Match.Tick`은 부활 → 입력·이동 → 재장전 완료 → 실제 입력의 무기 처리(`WeaponRules`)·발사(`HitScan`) → `ServerTick++` → History 기록 → Snapshot 순서다. 전투 코드는 `Game/Combat/`(`WeaponCatalog`, `WeaponDefinition`, `WeaponRules`, `HitScan`, `CombatRules`, `PositionHistory`)에 있고 Game Loop 스레드만 쓴다. `WeaponCatalog`는 시작 후 바뀌지 않는다. 발사 한 번은 박스 20개 + 플레이어 수만큼의 slab 교차이고, 전송은 `_sendBuffer` 하나를 재사용하므로 발사 Tick도 할당이 없다(`LagCompensationTests.FiringTick_AllocatesNothing`).
```

`## Lifetime`의 `- `Match.Leave`는 ...` bullet 다음에 추가:

```markdown
- 플레이어별 전투 상태는 `PlayerEntity`에 있고 플레이어와 함께 사라진다. 위치 History는 32칸 고정 링이라 늘어나지 않고, Join·부활 때 새로 시작한다. 탄·발사 간격 배열은 슬롯 수(2) 고정이다.
```

- [ ] **Step 3: `Docs/Client.md`**

구조 표에서 다음 행을 바꾸거나 추가한다(`Game/LocalFireEffects, FireRateAccumulator, RingCursor` 행은 지우고 아래 `Game/LocalFireEffects, RingCursor` 행으로 바꾼다).

```markdown
| `Net/NetClient` | LiteNetLib, 메인 스레드 전용(`UnsyncedEvents = false`, `Update`에서 Poll). 전투 패킷 6종을 이벤트로 올린다 |
| `Input/InputReader` | Input System 격리. Move, Look, Jump, Sprint, Fire(좌클릭), Aim(우클릭), Reload(R), Slot1·Slot2(1·2), Esc. 누름은 `QueuedButtons`에 모았다가 다음 예측 Step이 가져간다 |
| `Game/LocalPlayerPredictor` | 예측·재조정. `TestArena.Boxes`와 충돌(서버와 같은 박스). 입력에 버튼·조준·ViewTick을 담는다. 사망 중 정지, 부활 시 상태만 초기화(Seq 유지) |
| `Game/AimSolver` | 눈(발 + 1.6 m) → 조준점 방향을 Yaw/Pitch로(순수 계산) |
| `Game/WeaponState` | 서버 무기 규칙의 표시용 사본. 예측 입력마다 Step, Snapshot 수신자 블록과 다르면 서버 값으로 맞춘다 |
| `Game/CombatHud` | 코드로 만든 UGUI(Legacy `Text`, 내장 `LegacyRuntime.ttf`). HP·SH, 무기·탄, 명중 표시, 피격 방향, 사망 카운트다운. 값이 바뀔 때만 문자열을 만든다 |
| `Game/LocalFireEffects`, `RingCursor` | 발사 연출: 내 발사는 `WeaponState`가 쏜다고 한 입력마다(프레임당 최대 3발) 총구 → 조준점 광선, 다른 사람 발사는 `ShotFired`의 시작 → 끝. 궤적 16·탄착 32 고정 링 풀 |
| `Game/RemotePlayers`, `RemotePlayerInterpolator`, `ServerClock` | 다른 플레이어 보간. Snapshot 생존 비트로 회색·눕힘, 부활하면 보간 기록을 비운다 |
| `Game/PlayerViewFactory` | 캡슐 뷰 생성, 공유 Material(내 색, 남 색, 사망 회색). 원격 뷰에는 서버 판정 상자 크기의 CapsuleCollider를 `Ignore Raycast` 레이어(2)에 둔다(조준 광선만 이 레이어를 본다) |
```

`## 프레임 흐름` 목록을 다음으로 바꾼다.

```markdown
1. `NetClient.Poll` → 콜백(Joined/Spawned/Snapshot/전투 이벤트)이 메인 스레드에서 실행됨
2. `InputReader.Update`(점프·재장전·슬롯 눌림 큐잉), 커서·버튼 처리: 커서가 풀려 있으면 좌클릭은 잠금만 하고(Join 후), 그 클릭은 버튼을 뗄 때까지 발사로 치지 않는다. 조준·발사는 커서가 잠겨 있을 때만.
3. 원격 플레이어 렌더(`ServerClock.RenderTick`로 보간 대상 Tick 계산, 이 값이 입력의 ViewTick이 된다)
4. 카메라 Look(ADS 중 감도 ×0.6) → `LocalPlayerPredictor.Advance`(고정 스텝 예측, Sprint·Fire는 매 Step, 눌림은 마지막 Step) → 로컬 뷰 자세(사망이면 눕힘)
5. `LateUpdate`: 카메라 Follow → 조준점(`Physics.SyncTransforms` 후 화면 중앙 Raycast, 원격 플레이어 포함) → 이번 프레임 입력들에 조준·ViewTick 기록 → `WeaponState` Step → `PlayerInput` 전송 → 내 발사 연출 → HUD
```

`## Lifetime` 첫 문단을 다음으로 바꾼다.

```markdown
생성 순서: 월드(+박스 Material) → InputReader → ShoulderCamera → Crosshair → CombatHud → LocalFireEffects → NetClient. `GameClient.OnDestroy`는 역순으로 해제한다: 이벤트 구독 해제(11개) → NetClient Dispose(`NetManager.Stop`) → 매치 상태(예측기·로컬 뷰·원격 뷰·ServerClock·WeaponState, 조준점·HUD 숨김, 발사 연출 숨김) → LocalFireEffects Dispose(풀 GameObject·Material) → CombatHud Dispose(Canvas) → Crosshair Dispose(Canvas) → InputAction Dispose → 플레이어 공유 Material(3개) → 월드·박스 Material 파괴. 연결이 끊기면(`OnDisconnected`) 매치 상태를 지운다. 종료 때 Unity가 오브젝트를 먼저 파괴했을 수 있어(OnDestroy 순서는 보장되지 않음) `Crosshair.SetVisible`, `CombatHud`의 메서드, `LocalFireEffects.HideAll`은 루트가 파괴됐으면 아무것도 하지 않고 돌아온다. 예외가 나면 뒤의 해제가 건너뛰어지기 때문이다.
```

`## 실행과 두 Client 확인`의 4번을 다음으로 바꾼다.

```markdown
4. 조작: 좌클릭(커서 잠금, 잠긴 뒤 누르고 있으면 발사), 우클릭(누르는 동안 조준), R(재장전), 1·2(무기 교체), WASD, Shift(달리기), Space(점프), Esc(해제), F1(패널)
```

`## 자동 검사` 첫 줄을 다음으로 바꾼다.

```markdown
EditMode 테스트: `Assets/Tests/EditMode`(`LocalPlayerPredictorTests`, `ArenaPredictionTests`, `RemotePlayerInterpolatorTests`, `ShoulderCameraMathTests`, `AimSolverTests`, `WeaponStateTests`, `RingCursorTests`). 이 테스트들은 Physics·GameObject·네이티브 Quaternion 함수를 쓰지 않으므로 Unity 밖 NUnit 프로젝트로도 돌릴 수 있다.
```

- [ ] **Step 4: `Docs/Architecture.md`**

3번째 줄을 다음으로 바꾼다.

```markdown
Phase 3 Combat 기준. 설계 근거: `Docs/specs/2026-09-30-phase0-network-sync-design.md`, `Docs/specs/2026-09-30-phase1-character-prototype-design.md`, `Docs/specs/2026-10-01-phase3-combat-design.md`.
```

mermaid에서 `Loop --> Match` 다음 줄에 `Match --> Combat[Combat: WeaponRules, HitScan, PositionHistory]`를, Client subgraph의 `Net --> Predictor` 다음 줄에 `Net --> Hud[CombatHud, WeaponState]`를 추가한다.

폴더 표의 Client·Server 행을 다음으로 바꾼다.

```markdown
| `Client/` | Unity. 입력·표시·예측·보간. 결과를 확정하지 않는다. 카메라·조준점은 Client 표시 전용이고, 발사는 입력에 조준 방향만 실어 보낸다(누구를 맞혔는지는 보내지 않는다) |
| `Server/` | .NET 10 Dedicated Server. 이동 결과와 명중·피해·사망·부활을 결정한다. 무기 수치는 `weapons.json` |
```

- [ ] **Step 5: 체크포인트**

네 문서의 변경을 다시 읽고 코드의 상수(29B, 17 + 23 × 50 = 1167B, ProtocolVersion 3, 100/50, 1.6 m, ±89°, 6 Tick, 32칸, 3 s, 무기 수치, 풀 크기 16/32)와 일치하는지 확인해 기록한다. `dotnet test Server/ProjectH.Server.slnx`와 스크래치 NUnit을 마지막으로 한 번 더 실행해 PASS를 기록한다. 커밋하지 않는다.

---


## Self-Review 결과

- **Spec 대응:**
  - §1 Protocol v3의 모든 행(InputButtons, InputCommand 29B, WeaponCatalog, 수신자 블록 6B, SnapshotEntity Flags, ShotFired, HitConfirmed, DamageTaken, PlayerDied, PlayerRespawned)과 1167B 고정 → Task 1.
  - §2 Server: `WeaponCatalog` 로드·검증·Tick 변환·시작 거부 → Task 2. PlayerEntity 상태 → Task 4·5. `Match.Tick` 순서(부활 → 입력 → 이동·무기 → History → Snapshot) → Task 4·5. `CombatResolver` 단계 1–7은 `Match.ProcessWeapons/FireShot/ApplyHit/Kill`(Task 4)과 `HitScan`(Task 3), 되감기(Task 5)로 나눴다. slab·할당 없음·무한값 방어 → Task 3, 할당 0 테스트 → Task 5. Lock 없음 → Global Constraints.
  - §3 Client 표의 모든 파일: `NetClient`·`WeaponState`·`AimSolver` → Task 7, `LocalPlayerPredictor`·`RemotePlayers`·`InputReader` → Task 8, `LocalFireEffects`·`CombatHud`·`GameClient` → Task 9. ViewTick = 렌더 Tick → Task 9.
  - §4 하네스 변경 없음 → 어느 Task도 `game-core-rules`를 고치지 않는다.
  - §5 테스트: 카탈로그 5종 → Task 2, 광선–AABB 5종 → Task 3, 판정 5종 → Task 4, 피해 4종 → Task 3, 발사 규칙 5종 → Task 4, 사망·부활 → Task 4, Lag Compensation 3종 → Task 5, Protocol 3종 → Task 1, 통합 → Task 6, Client EditMode 3종 → Task 7·8, Unity 수동 → Task 9.
  - D15(넣지 않는 것)는 어느 Task에도 없다. Spread·Recoil은 데이터 필드만 있다.
- **스크래치 사전 검증:** 저장소의 `Shared/`, `Server/`, `Client/Assets/Scripts`, `Client/Assets/Tests`를 스크래치(`scratchpad/phase3repo`, git으로 Task마다 기록)에 복사하고 이 계획의 코드를 Task 순서대로 적용했다. 각 Task 끝에서 `dotnet test`(서버 테스트 96 → 248개 PASS, 통합 테스트 3회 연속 PASS), Unity DLL 대상 Client 전체 컴파일(경고·오류 0), `UnityEngine.CoreModule`만 참조한 NUnit(EditMode 39 → 59개 PASS)을 확인했다. 계획의 코드 블록은 그 스크래치 파일에서 그대로 옮겼다. 추가로 확인한 것: `weapons.json`이 ProjectReference로 테스트 출력에 복사됨, 실제 서버가 `weapons.json`으로 시작하고 빈 목록이면 시작을 거부함, 발사·명중 Tick의 할당이 0 바이트, `Respawn`의 `History.Reset`이나 `if (sent)`를 빼면 해당 Review Focus 테스트가 실패함, `unity default resources`에 `LegacyRuntime` 이름이 있음. 확인하지 못한 것: Unity Editor 안의 실제 import·Play 동작(Task 8·9의 Editor.log와 수동 확인 단계).
- **타입 일관성:** `InputCommand.AimYaw/AimPitch/ViewTick`, `PlayerInputPacket.CommandSize/MaxSize`, `WorldSnapshotHeader.PatchRecipient(Span<byte>, uint, in SnapshotSelf)`, `SnapshotEntity.Flags/AliveFlag/IsAlive`, `WeaponCatalog.TryParse/LoadFile/SlotCount/LoadoutCount/SimHz/WireInfos`, `Match(ServerOptions, WeaponCatalog, SendPacket)`, `GameLoop(ServerOptions, WeaponCatalog, ILogger)`, `WeaponRules.Equip/UpdateReload/Apply`, `HitScan.TraceWorld/TracePlayer/IntersectAabb`, `CombatRules.ApplyDamage/TryAimDirection/TicksFromSeconds/ClampViewTick`, `PositionHistory.Reset/Record/Sample`, `AimSolver.TrySolve`, `WeaponState.Step/ApplyServer/Refill`, `LocalPlayerPredictor.Advance(float, Vector2, float, InputButtons, ref InputButtons)/SetAim/InputAt/SetDead/Respawn/HistorySize`, `InputReader.QueuedButtons`, `PlayerViewFactory.AimRaycastMask/SetAlive/Pose`, `LocalFireEffects.FireLocal/ShowRemoteShot/Tick`, `CombatHud.*`가 정의한 Task와 사용하는 Task에서 같다.
- **Placeholder 검사:** TBD·TODO·"적절히" 없음. 모든 코드 단계는 전체 파일 또는 정확한 before → after를 담는다.
