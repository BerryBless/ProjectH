# Phase 1 Character Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 박스로 된 테스트 아레나에서 서버와 예측이 같은 캐릭터–박스 충돌을 계산하고, 어깨 너머 카메라(ADS·카메라 충돌), 조준점, Client 전용 발사 연출로 기본 조작감을 확인할 수 있게 한다.

**Architecture:** 지형은 Shared의 코드 상수 `TestArena`(축 정렬 박스 목록)이고, `MovementSimulation.Step`이 이 박스들을 `ReadOnlySpan<Box>`로 받아 밀어내기 → 상태 없는 접지 판정 → X/Z/Y 축 분리 Sweep 순서로 계산한다. 서버 `Match.Tick`과 Client `LocalPlayerPredictor`가 같은 박스로 같은 함수를 호출하므로 Snapshot 형식은 그대로이고 ProtocolVersion만 2로 올린다. 카메라·조준점·발사는 Client 표시 전용이며, 카메라 위치 계산과 발사 속도 계산은 Unity 물리 없이 테스트하는 순수 코드로 분리한다.

**Tech Stack:** .NET 10, C# (Shared·Client는 C# 9 / netstandard2.1), `System.Numerics`, xUnit, Unity 6000.3.24f1 (URP, Input System 1.20, UGUI `com.unity.ugui` 2.0.0, Test Framework 1.6), LiteNetLib 2.1.4(변경 없음).

**Spec:** `Docs/specs/2026-09-30-phase1-character-prototype-design.md` (결정 D1–D15는 확정)

## Global Constraints

- 커밋하지 않는다. 각 Task의 마지막 단계는 "체크포인트"(검증 결과 기록)다. Commit·Push는 사용자가 "푸시"를 입력했을 때만 `github-push` 스킬로 한다.
- 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다. 우리 코드는 Lock을 쓰지 않는다(이번 Phase는 스레드 구조를 바꾸지 않는다).
- `Shared/Runtime`은 `netstandard2.1` + C# 9에서 컴파일되어야 한다(Unity가 이 패키지를 컴파일한다). `UnityEngine` 참조 금지, `System.Numerics`만 사용. `record`·`init`(IsExternalInit 없음), `Vector3` 인덱서(netstandard2.1에 없음) 금지.
- `MovementSimulation.Step`은 할당·예외·잠금이 없고 결정적이다. 박스 수 N에 대해 O(N) × 상수.
- 이동·충돌 수치는 `MoveSettings` 상수만 쓴다: `HalfWidth 0.35`, `Height 1.8`, `Skin 0.001`, `GroundProbe 0.02` (기존 `WalkSpeed 4.5`, `SprintSpeed 7`, `Gravity -20`, `JumpSpeed 7`).
- `TestArena.Boxes`는 `static readonly Box[]`를 감싸는 `ReadOnlySpan<Box>` 속성이다. `=> new Box[] {...}`처럼 호출마다 배열을 만들지 않는다. 박스 30개 이하, 중앙 반경 7 m 비움.
- `ProtocolConstants.ProtocolVersion = 2`. Snapshot·Input 패킷 형식은 바꾸지 않는다.
- 카메라 수치(D8): 기본 거리 3.5 m / 오른쪽 0.55 m / FOV 60, 우클릭 중 거리 1.6 m / 오른쪽 0.65 m / FOV 42 / 감도 ×0.6. 카메라 충돌 SphereCast 반경 0.2 m, 두 번(머리 기준점 → 어깨점, 어깨점 → 카메라).
- 발사(D11–D14): 초당 10발, 궤적 LineRenderer 16개·탄착 표시 32개 고정 링 풀, 서버로 보내지 않는다. 좌클릭은 커서가 풀려 있으면 잠금만, 잠겨 있으면 발사.
- Unity: Scene·Prefab·`.meta` 파일을 직접 만들거나 수정하지 않는다(`.meta`는 Unity가 자동 생성; 파일을 지울 때만 짝 `.meta`를 같이 지운다). Unity가 생성한 `Client/*.csproj`는 건드리지 않는다. Hot Path(Update, LateUpdate, 발사)에서 LINQ·임시 컬렉션·문자열 생성·`renderer.material` 금지. `Physics.Raycast`/`SphereCast`는 단일 결과 버전만.
- Unity batchmode는 쓸 수 없다(사용자가 Editor를 열어 둔다). Client 검증은 (a) 스크래치 컴파일·NUnit 프로젝트, (b) Editor 자동 import 후 `Editor.log`의 새 `error CS` 확인, (c) 사용자 수동 확인이다.

## Review Focus

- 박스 윗면에 서 있는 동안 Snapshot 재조정이 계속 들어옴 → 높이가 정확히 유지되고 보정(떨림)이 생기지 않아야 한다 (Task 2 `StandingOnBox_HeightNeverChanges`, Task 4 `StandingOnBox_ReconcileEveryStep_KeepsExactHeight`).
- 서버 보정(Reconcile)으로 예측 상태가 박스 안으로 순간 이동 → 재적용 Step이 밖으로 밀어내 어떤 박스와도 겹치지 않아야 한다 (Task 2 `StartingInsideBox_IsPushedOutAlongLeastPenetration`, Task 4 `ReconcileIntoPillar_ReplayEndsOutsideEveryBox`).
- 높은 곳에서 긴 낙하(Tick당 수 m) → 얇은 판을 뚫지 않고 그 위에 서야 한다 (Task 2 `FastFall_DoesNotTunnelThroughThinPlate`).
- 오른쪽이 벽에 붙어 어깨점 자체가 벽 안에 있음 → 두 번째 SphereCast가 줄어든 어깨점에서 시작해 카메라가 벽 너머를 보여 주지 않아야 한다 (Task 5 `ShoulderInsideWall_SecondCastStartsFromShortenedShoulder`).
- 좌클릭을 오래 누르거나 프레임이 튐 → 발사 연출 풀은 고정 크기로 재사용되고, 한 프레임 발사 수에 상한이 있어야 한다 (Task 6 `RingCursor_WrapsWithinCapacity`, `HitchFrame_IsCapped_AndBacklogIsDropped`).

(Spawn 위치가 박스 안인 경우는 spec §5의 `TestArena` 테스트가 이미 고정하므로 여기서 제외했다. Task 3 `SpawnPositions_DoNotOverlapAnyBox`.)

---

## File Structure

| 경로 | 책임 |
|---|---|
| `Shared/Runtime/Simulation/Box.cs` (신규) | 축 정렬 박스 `readonly struct Box` |
| `Shared/Runtime/Simulation/TestArena.cs` (신규) | 코드 상수 테스트 아레나 `ReadOnlySpan<Box> Boxes` |
| `Shared/Runtime/Simulation/MoveSettings.cs` | 캐릭터 박스·Skin·GroundProbe 상수 추가 |
| `Shared/Runtime/Simulation/MovementSimulation.cs` | `Step(..., ReadOnlySpan<Box> world)`: 밀어내기, 접지, 축 분리 Sweep, `IsGrounded`, `OverlapsAny` |
| `Shared/Runtime/Protocol/ProtocolConstants.cs` | `ProtocolVersion = 2` |
| `Server/src/ProjectH.Server/Game/Match.cs` | `Step`에 `TestArena.Boxes` 전달, `SpawnPosition` internal |
| `Server/tests/ProjectH.Server.Tests/Shared/BoxTests.cs` (신규) | Box 생성 도우미 |
| `Server/tests/ProjectH.Server.Tests/Shared/CollisionTests.cs` (신규) | spec §5 충돌 테스트 |
| `Server/tests/ProjectH.Server.Tests/Shared/TestArenaTests.cs` (신규) | 아레나 형태·Spawn·틈·결정성 |
| `Server/tests/ProjectH.Server.Tests/Shared/MovementSimulationTests.cs`, `ProtocolConstantsTests.cs` | 새 시그니처, 버전 2 |
| `Client/Assets/Scripts/Game/LocalPlayerPredictor.cs` | `Step`에 `TestArena.Boxes` 전달 |
| `Client/Assets/Scripts/Bootstrap/TestWorld.cs` | 아레나 박스 Cube(BoxCollider, 공유 Material) |
| `Client/Assets/Scripts/Camera/ShoulderCameraMath.cs` (신규) | 순수 카메라 계산, `ISphereCaster`, `ShoulderPose` |
| `Client/Assets/Scripts/Camera/ShoulderCamera.cs` (신규) | Look 입력, ADS 전환, Physics SphereCast, 카메라 Transform·FOV |
| `Client/Assets/Scripts/Camera/ThirdPersonCamera.cs` (+`.meta`) | 삭제 |
| `Client/Assets/Scripts/Input/InputReader.cs` | Aim(우클릭), Fire(좌클릭) |
| `Client/Assets/Scripts/Game/FireRateAccumulator.cs` (신규) | 초당 발사 수 누적기(순수) |
| `Client/Assets/Scripts/Game/RingCursor.cs` (신규) | 고정 크기 링 풀 인덱스(순수) |
| `Client/Assets/Scripts/Game/LocalFireEffects.cs` (신규) | 두 번 광선, 궤적·탄착 링 풀 |
| `Client/Assets/Scripts/Game/Crosshair.cs` (신규) | 코드로 만든 UGUI Canvas 조준점 |
| `Client/Assets/Scripts/Game/GameClient.cs` | 구성·연결·해제, D12 좌클릭 규칙 |
| `Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs` | 조작 안내 문구 |
| `Client/Assets/Scripts/ProjectH.Client.asmdef` | `UnityEngine.UI` 참조 추가 |
| `Client/Assets/Tests/EditMode/ArenaPredictionTests.cs`, `ShoulderCameraMathTests.cs`, `FireRateAccumulatorTests.cs` (신규), `LocalPlayerPredictorTests.cs` | EditMode 테스트 |
| `.claude/skills/game-core-rules/SKILL.md`, `CLAUDE.md` | Shared 예외 확장(D7), 변경 이력 |
| `Docs/Networking.md`, `Docs/Client.md`, `Docs/Server.md`, `Docs/Architecture.md` | 동작 변경 반영 |

스크래치 검증 프로젝트(저장소 밖, 수정해도 됨):

- 컴파일 확인: `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj` — `Client/Assets/Scripts/**/*.cs`와 `Shared/Runtime/**/*.cs`를 netstandard2.1 / C# 9로 Unity DLL(`UnityEngine.*Module`, `Library/ScriptAssemblies/UnityEngine.UI.dll`, `Unity.InputSystem.dll`, LiteNetLib)에 대해 컴파일한다. 테스트 파일은 포함하지 않는다.
- NUnit: `C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj` — `Client/Assets/Tests/EditMode/*.cs` 전부와 **명시한 Client 소스만**(`LocalPlayerPredictor.cs`, `VectorConversions.cs`, `ServerClock.cs`, `RemotePlayerInterpolator.cs`) + `Shared/Runtime/**/*.cs`를 `UnityEngine.CoreModule`만 참조해 net10.0에서 실행한다. 그래서 EditMode 테스트는 `Physics`·`GameObject`·`Canvas`를 쓰면 안 되고, `Quaternion.Euler`/`LookRotation`(네이티브 호출)도 쓰면 안 된다. 새 순수 소스는 이 csproj에 `<Compile Include>`를 추가한다.

---

### Task 1: 하네스 규칙 확장, `Box`, 새 `Step` 시그니처

**Files:**
- Modify: `.claude/skills/game-core-rules/SKILL.md` (4절 예외 문단), `CLAUDE.md` (변경 이력)
- Create: `Shared/Runtime/Simulation/Box.cs`
- Modify: `Shared/Runtime/Simulation/MoveSettings.cs`, `Shared/Runtime/Simulation/MovementSimulation.cs:11`
- Modify: `Server/src/ProjectH.Server/Game/Match.cs:122`, `Client/Assets/Scripts/Game/LocalPlayerPredictor.cs:64,135`
- Test: `Server/tests/ProjectH.Server.Tests/Shared/BoxTests.cs` (신규), `Server/tests/ProjectH.Server.Tests/Shared/MovementSimulationTests.cs`, `Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs:45,60`

**Interfaces:**
- Consumes: 기존 `MoveState`, `InputCommand`, `MoveSettings`
- Produces:
  - `readonly struct Box { readonly Vector3 Min, Max; Box(Vector3 min, Vector3 max); Vector3 Center; Vector3 Size; static Box FromCenterSize(Vector3 center, Vector3 size) }` (네임스페이스 `ProjectH.Shared.Simulation`)
  - `MoveSettings.HalfWidth = 0.35f`, `Height = 1.8f`, `Skin = 0.001f`, `GroundProbe = 0.02f`
  - `MovementSimulation.Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world)` — 이 Task에서는 `world`를 아직 쓰지 않는다(동작은 Phase 0과 같음). Task 2가 충돌을 구현한다.
  - 호출부는 임시로 `ReadOnlySpan<Box>.Empty`를 넘긴다(Match는 Task 3, Predictor는 Task 4에서 `TestArena.Boxes`로 바뀜).

- [ ] **Step 0: Editor.log 기준선 기록 (코드 수정 전)**

이 Task부터 Unity가 컴파일하는 코드(Shared, Client)를 고친다. `Editor.log`는 세션 동안 누적되므로 수정 전 줄 수를 기록해 두고, Task 4 Step 6에서 이 줄 이후만 검사한다(Task 1–4 변경을 한 번에 확인).

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; wc -l < "$LOG"
```

출력값을 `N0`로 체크포인트에 기록한다.

- [ ] **Step 1: 하네스 규칙 4절 예외를 넓힌다 (D7)**

`.claude/skills/game-core-rules/SKILL.md`의 4절에서 아래 문단을

```markdown
예외: `Shared/Runtime/Simulation`의 이동 계산(`MovementSimulation`과 그 입력·상태·상수 타입)만 둔다. Client Prediction과 서버 시뮬레이션이 같은 코드를 실행해야 예측이 어긋나지 않기 때문이다. 이 폴더에는 `System.Numerics`만 쓰는 순수 계산만 두고, 전투·인벤토리 등 다른 게임 규칙은 넣지 않는다.
```

다음으로 바꾼다.

```markdown
예외: `Shared/Runtime/Simulation`의 이동 계산만 둔다. 여기에는 `MovementSimulation`과 그 입력·상태·상수 타입, 이동 계산이 읽는 지형 박스 데이터(`Box`, `TestArena`)와 캐릭터–박스 충돌 계산이 포함된다. Client Prediction과 서버 시뮬레이션이 같은 코드와 같은 지형으로 계산해야 예측이 어긋나지 않기 때문이다. 이 폴더에는 `System.Numerics`만 쓰는 순수 계산과 그 계산이 읽는 상수 데이터만 두고, 전투·인벤토리 등 다른 게임 규칙은 넣지 않는다.
```

`CLAUDE.md` 변경 이력 표 마지막 행 다음에 추가:

```markdown
| 2026-09-30 | Shared 예외에 지형 박스·충돌 계산 추가 | `game-core-rules` 4절 | 서버와 예측이 같은 충돌 결과를 내야 함 (Phase 1 D7) |
```

- [ ] **Step 2: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Shared/BoxTests.cs`:

```csharp
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class BoxTests
{
    [Fact]
    public void FromCenterSize_ComputesMinMax()
    {
        var box = Box.FromCenterSize(new Vector3(0f, 0.5f, 12f), new Vector3(2f, 1f, 2f));
        Assert.Equal(new Vector3(-1f, 0f, 11f), box.Min);
        Assert.Equal(new Vector3(1f, 1f, 13f), box.Max);
        Assert.Equal(new Vector3(0f, 0.5f, 12f), box.Center);
        Assert.Equal(new Vector3(2f, 1f, 2f), box.Size);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/MovementSimulationTests.cs`: 파일 맨 위 `using System.Numerics;` 앞에 `using System;`을 추가하고, 파일 안의 `MovementSimulation.Step(...)` 호출 7곳 모두 마지막 인자로 `ReadOnlySpan<Box>.Empty`를 추가한다. 결과 호출은 정확히 다음과 같다.

```csharp
        for (int i = 0; i < steps; i++) MovementSimulation.Step(ref state, input, Dt, ReadOnlySpan<Box>.Empty);
```
```csharp
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty);
```
```csharp
            MovementSimulation.Step(ref s, new InputCommand(), Dt, ReadOnlySpan<Box>.Empty);
```
```csharp
        MovementSimulation.Step(ref s, jump, Dt, ReadOnlySpan<Box>.Empty);
        float vAfterFirst = s.VelocityY;
        MovementSimulation.Step(ref s, jump, Dt, ReadOnlySpan<Box>.Empty);
```
```csharp
            MovementSimulation.Step(ref a, input, Dt, ReadOnlySpan<Box>.Empty);
            MovementSimulation.Step(ref b, input, Dt, ReadOnlySpan<Box>.Empty);
```

- [ ] **Step 3: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~BoxTests|FullyQualifiedName~MovementSimulationTests"`
Expected: FAIL — `Box` 미정의, `Step` 인자 4개 오버로드 없음 컴파일 오류

- [ ] **Step 4: `Box`와 `MoveSettings` 상수 구현**

`Shared/Runtime/Simulation/Box.cs`:

```csharp
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Axis-aligned solid box of the collision world (D1). Immutable so a ReadOnlySpan<Box> over a
    // static array can be shared by every simulation step without copies or allocation.
    public readonly struct Box
    {
        public readonly Vector3 Min;
        public readonly Vector3 Max;

        public Box(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;

        public static Box FromCenterSize(Vector3 center, Vector3 size)
        {
            Vector3 half = size * 0.5f;
            return new Box(center - half, center + half);
        }
    }
}
```

`Shared/Runtime/Simulation/MoveSettings.cs` 전체:

```csharp
namespace ProjectH.Shared.Simulation
{
    // Single source of truth for client prediction and server simulation. Changing a value on only
    // one side makes every prediction diverge, so these are constants rather than server config.
    public static class MoveSettings
    {
        public const float WalkSpeed = 4.5f;
        public const float SprintSpeed = 7f;
        public const float Gravity = -20f;
        public const float JumpSpeed = 7f;

        // Character collision box (D1): feet at MoveState.Position, HalfWidth on X and Z, Height up.
        public const float HalfWidth = 0.35f;
        public const float Height = 1.8f;

        // Gap kept between the character and a face it was stopped by. Overlaps up to Skin are
        // treated as touching, so float rounding never counts as penetration.
        public const float Skin = 0.001f;

        // A surface within this distance of the feet counts as ground (D3).
        public const float GroundProbe = 0.02f;
    }
}
```

- [ ] **Step 5: `Step` 시그니처 변경과 호출부 갱신**

`Shared/Runtime/Simulation/MovementSimulation.cs`에서

```csharp
        public static void Step(ref MoveState state, in InputCommand input, float deltaTime)
```

를 다음으로 바꾼다(본문은 그대로, `using System;`은 이미 있음).

```csharp
        // world: the solid boxes besides the y = 0 floor. Collision is implemented in Phase 1 Task 2.
        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world)
```

`Server/src/ProjectH.Server/Game/Match.cs`의 `Tick()` 안:

```csharp
            MovementSimulation.Step(ref player.State, input, _tickSeconds);
```
→
```csharp
            MovementSimulation.Step(ref player.State, input, _tickSeconds, ReadOnlySpan<Box>.Empty);
```

`Client/Assets/Scripts/Game/LocalPlayerPredictor.cs` 두 곳(`using System;`은 이미 있음):

```csharp
                MovementSimulation.Step(ref _state, command, _stepSeconds);
```
→
```csharp
                MovementSimulation.Step(ref _state, command, _stepSeconds, ReadOnlySpan<Box>.Empty);
```

```csharp
                MovementSimulation.Step(ref _state, _inputs[slot], _stepSeconds);
```
→
```csharp
                MovementSimulation.Step(ref _state, _inputs[slot], _stepSeconds, ReadOnlySpan<Box>.Empty);
```

`Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs`: 맨 위 `using NUnit.Framework;` 앞에 `using System;`을 추가하고 두 곳을 바꾼다.

```csharp
            for (int i = 0; i < 2; i++) MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, ReadOnlySpan<Box>.Empty);
```
```csharp
            MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, ReadOnlySpan<Box>.Empty);
```

- [ ] **Step 6: 서버 빌드·테스트**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 빌드 성공, 기존 테스트 전부 + `BoxTests` PASS

- [ ] **Step 7: Client 스크래치 검증**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 기존 EditMode 테스트 전부 PASS

- [ ] **Step 8: 체크포인트**

두 명령의 PASS 결과와 하네스 문서 변경을 기록한다. Unity Editor import 확인은 Task 4에서 한 번에 한다. 커밋하지 않는다.

---

### Task 2: 캐릭터–박스 충돌 (밀어내기, 접지, 축 분리 Sweep)

**Files:**
- Modify: `Shared/Runtime/Simulation/MovementSimulation.cs` (전체 교체)
- Test: `Server/tests/ProjectH.Server.Tests/Shared/CollisionTests.cs` (신규)

**Interfaces:**
- Consumes: `Box`, `MoveSettings.HalfWidth/Height/Skin/GroundProbe` (Task 1)
- Produces:
  - `MovementSimulation.Step(ref MoveState, in InputCommand, float, ReadOnlySpan<Box>)` — 충돌 포함
  - `public static bool MovementSimulation.IsGrounded(in MoveState state, ReadOnlySpan<Box> world)`
  - `public static bool MovementSimulation.OverlapsAny(System.Numerics.Vector3 feet, ReadOnlySpan<Box> world)` — 모든 축에서 0보다 크게 겹칠 때만 true(면 접촉은 false)

**계산 규칙(테스트 기대값의 근거):** Dt = 1/30. 점프 Step k의 속도는 `7 - (k-1)·2/3`, n Step 후 발 높이는 `(7n - n(n-1)/3) / 30`. 11 Step 동안 상승해 최고점 `(77 - 110/3)/30 = 1.344 m`(연속식 1.225 m보다 높다: 각 Step이 중력 적용 전 속도를 쓰기 때문). 1 m 박스는 오를 수 있고 1.5 m 박스는 못 오른다.

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Shared/CollisionTests.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Character-vs-box collision (Phase 1 spec §1, §5). Expected numbers follow the discrete 30 Hz step
// rule, not the continuous formulas: with Dt = 1/30 the jump velocity on step k is 7 - (k-1)·(2/3)
// and the feet height after n steps is (7n - n(n-1)/3) / 30.
public class CollisionTests
{
    private const float Dt = 1f / 30f;
    private const float Hw = MoveSettings.HalfWidth;
    private const float Skin = MoveSettings.Skin;

    private static readonly InputCommand Idle = new InputCommand();
    private static readonly InputCommand WalkPlusX = new InputCommand { MoveY = 1f, Yaw = 90f };

    private static Box B(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        => new Box(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));

    // Wall 1 m thick, x 2..3, long along Z.
    private static readonly Box[] Wall = { B(2f, 0f, -50f, 3f, 3f, 50f) };
    // Box 2 x 2 m footprint, x 2..4, 1 m high (reachable) or 1.5 m high (not reachable).
    private static readonly Box[] LowBox = { B(2f, 0f, -2f, 4f, 1f, 2f) };
    private static readonly Box[] HighBox = { B(2f, 0f, -2f, 4f, 1.5f, 2f) };

    [Fact]
    public void WalkIntoWall_StopsAtFace_WithoutOverlap()
    {
        var s = new MoveState();
        for (int i = 0; i < 60; i++)
        {
            MovementSimulation.Step(ref s, WalkPlusX, Dt, Wall);
            Assert.False(MovementSimulation.OverlapsAny(s.Position, Wall));
        }
        // Front face stops Skin before the wall: x = 2 - 0.35 - 0.001.
        Assert.Equal(2f - Hw - Skin, s.Position.X, 3);
        Assert.True(s.Position.X + Hw <= 2f);
    }

    [Fact]
    public void DiagonalIntoWall_SlidesAlongIt()
    {
        var s = new MoveState();
        var diagonal = new InputCommand { MoveY = 1f, Yaw = 45f };
        for (int i = 0; i < 60; i++) MovementSimulation.Step(ref s, diagonal, Dt, Wall);

        Assert.Equal(2f - Hw - Skin, s.Position.X, 3);
        // Z is never blocked: 60 steps at 4.5 · cos 45° m/s = 2 s · 3.182 = 6.364 m.
        Assert.Equal(MoveSettings.WalkSpeed * MathF.Cos(MathF.PI / 4f) * 2f, s.Position.Z, 2);
    }

    [Fact]
    public void JumpApex_UnderDiscreteSteps_ClearsOneMetre_ButNotOnePointFive()
    {
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty);
        float peak = s.Position.Y;
        for (int i = 0; i < 40; i++)
        {
            MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty);
            if (s.Position.Y > peak) peak = s.Position.Y;
        }
        // Rises while 7 - (k-1)·2/3 > 0, i.e. 11 steps: (77 - 110/3) / 30 = 1.344 m
        // (the continuous 7² / (2·20) = 1.225 m is lower because each step uses the pre-gravity speed).
        Assert.InRange(peak, 1.30f, 1.40f);
    }

    [Fact]
    public void JumpOntoLowBox_StandsOnTop_Grounded()
    {
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Jump }, Dt, LowBox);
        for (int i = 0; i < 19; i++) MovementSimulation.Step(ref s, WalkPlusX, Dt, LowBox);

        // Feet pass y = 1 on the way up at step 6 (1.067 m) and fall back below it only at step 17,
        // by which time x = 2.55 is over the box: it lands on the top, then the ground snap sets y = 1.
        Assert.Equal(1f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
        Assert.True(MovementSimulation.IsGrounded(s, LowBox));
        Assert.Equal(3f, s.Position.X, 3);   // 20 steps · 0.15 m, never blocked
        Assert.False(MovementSimulation.OverlapsAny(s.Position, LowBox));
    }

    [Fact]
    public void HighBox_CannotBeClimbedByJumping()
    {
        var s = new MoveState();
        var jumpForward = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Jump };
        for (int i = 0; i < 90; i++)
        {
            MovementSimulation.Step(ref s, jumpForward, Dt, HighBox);
            Assert.True(s.Position.X + Hw <= 2f);   // never gets over the 1.5 m edge (apex 1.344 m)
            Assert.False(MovementSimulation.OverlapsAny(s.Position, HighBox));
        }
    }

    // Review Focus: standing on a box must keep an exact height (no grounded/airborne flicker).
    [Fact]
    public void StandingOnBox_HeightNeverChanges()
    {
        var s = new MoveState { Position = new Vector3(3f, 1f, 0f) };
        for (int i = 0; i < 90; i++)
        {
            MovementSimulation.Step(ref s, Idle, Dt, LowBox);
            Assert.Equal(1f, s.Position.Y);
            Assert.Equal(0f, s.VelocityY);
        }
    }

    [Fact]
    public void SlightlyAboveBoxTop_WithinProbe_SnapsOntoIt()
    {
        var s = new MoveState { Position = new Vector3(3f, 1.015f, 0f) };
        MovementSimulation.Step(ref s, Idle, Dt, LowBox);
        Assert.Equal(1f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
    }

    [Fact]
    public void HeadHitsCeiling_StopsRising()
    {
        // Ceiling slab from y 2.5 to 3 over the start position.
        Box[] ceiling = { B(-2f, 2.5f, -2f, 2f, 3f, 2f) };
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ceiling);
        for (int i = 0; i < 3; i++) MovementSimulation.Step(ref s, Idle, Dt, ceiling);

        // Steps 1-3 rise to 0.633 m (head 2.433 m); step 4 wants +0.167 m but only 0.066 m is free.
        Assert.Equal(0f, s.VelocityY);
        Assert.InRange(s.Position.Y, 0.69f, 0.70f);
        Assert.True(s.Position.Y + MoveSettings.Height <= 2.5f);

        MovementSimulation.Step(ref s, Idle, Dt, ceiling);
        Assert.True(s.VelocityY < 0f);   // falls again, does not stick to the ceiling
    }

    // Review Focus: a long fall moves several metres per tick and must not pass through a thin plate.
    [Fact]
    public void FastFall_DoesNotTunnelThroughThinPlate()
    {
        // 0.2 m plate; falling at 200 m/s moves about 6.7 m per tick.
        Box[] plate = { B(-5f, 5f, -5f, 5f, 5.2f, 5f) };
        var s = new MoveState { Position = new Vector3(0f, 20f, 0f), VelocityY = -200f };
        for (int i = 0; i < 10; i++)
        {
            MovementSimulation.Step(ref s, Idle, Dt, plate);
            Assert.True(s.Position.Y >= 5.2f);
        }
        Assert.Equal(5.2f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
    }

    // Review Focus: a reconcile snap can put the player inside a box; the next step must push it out.
    [Fact]
    public void StartingInsideBox_IsPushedOutAlongLeastPenetration()
    {
        // Box x -1..1, y 0..1, z -1..1. Feet at x 0.9, y 0.5: +X needs 0.45 m, +Y 0.5 m, Z 1.35 m.
        Box[] box = { B(-1f, 0f, -1f, 1f, 1f, 1f) };
        var s = new MoveState { Position = new Vector3(0.9f, 0.5f, 0f) };
        MovementSimulation.Step(ref s, Idle, Dt, box);

        Assert.Equal(0.9f + 0.45f + Skin, s.Position.X, 3);
        Assert.False(MovementSimulation.OverlapsAny(s.Position, box));
        Assert.True(s.Position.Y < 0.5f);   // no support under it any more: it falls
    }

    [Fact]
    public void SameInputs_WithBoxes_ProduceIdenticalState()
    {
        Box[] world = { B(2f, 0f, -2f, 4f, 1f, 2f), B(-3f, 0f, 3f, 3f, 3f, 4f), B(-6f, 2.5f, -6f, 6f, 3f, 6f) };
        var a = new MoveState();
        var b = new MoveState();
        for (int i = 0; i < 300; i++)
        {
            var input = new InputCommand { MoveX = (i % 7) / 7f - 0.4f, MoveY = 1f, Yaw = i * 3.3f, Buttons = i % 20 == 0 ? InputButtons.Jump : InputButtons.None };
            MovementSimulation.Step(ref a, input, Dt, world);
            MovementSimulation.Step(ref b, input, Dt, world);
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VelocityY, b.VelocityY);
        Assert.Equal(a.Yaw, b.Yaw);
    }

    [Fact]
    public void NonFiniteInput_NextToWall_StaysFiniteAndOutside()
    {
        var s = new MoveState { Position = new Vector3(2f - Hw - Skin, 0f, 0f) };
        var bad = new InputCommand { MoveX = float.NaN, MoveY = float.PositiveInfinity, Yaw = float.NaN, Buttons = InputButtons.Jump };
        for (int i = 0; i < 30; i++)
        {
            MovementSimulation.Step(ref s, bad, Dt, Wall);
            Assert.True(float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z));
            Assert.True(float.IsFinite(s.VelocityY));
            Assert.False(MovementSimulation.OverlapsAny(s.Position, Wall));
        }
    }

    [Fact]
    public void EmptyWorld_MatchesFloorOnlyBehaviour()
    {
        var s = new MoveState();
        MovementSimulation.Step(ref s, new InputCommand { Buttons = InputButtons.Jump }, Dt, ReadOnlySpan<Box>.Empty);
        for (int i = 0; i < 60; i++) MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty);
        Assert.Equal(0f, s.Position.Y);
        Assert.Equal(0f, s.VelocityY);
        Assert.True(MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty));
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter FullyQualifiedName~CollisionTests`
Expected: FAIL — `MovementSimulation.OverlapsAny`, `MovementSimulation.IsGrounded` 미정의 컴파일 오류

- [ ] **Step 3: 충돌 구현**

`Shared/Runtime/Simulation/MovementSimulation.cs` 전체:

```csharp
using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // The one piece of game logic allowed in Shared (see game-core-rules §4): client prediction and
    // the authoritative server run exactly this code. Pure math, no allocation, no engine types.
    // The character is an axis-aligned box (MoveSettings.HalfWidth / Height) with its feet at
    // MoveState.Position; the world is the y = 0 floor plus the boxes passed in.
    public static class MovementSimulation
    {
        private const float DegToRad = 0.017453292f;
        private const int AxisX = 0;
        private const int AxisY = 1;
        private const int AxisZ = 2;

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world)
        {
            // Untrusted input: non-finite values become 0 and the move vector is clamped to length 1,
            // so no input can exceed the configured speed.
            float moveX = Finite(input.MoveX);
            float moveY = Finite(input.MoveY);
            float lengthSq = moveX * moveX + moveY * moveY;
            if (lengthSq > 1f)
            {
                float inv = 1f / MathF.Sqrt(lengthSq);
                moveX *= inv;
                moveY *= inv;
            }

            if (IsFinite(input.Yaw)) state.Yaw = NormalizeYaw(input.Yaw);

            // Unity convention: yaw rotates around +Y and yaw 0 faces +Z.
            // right = (cos, 0, -sin), forward = (sin, 0, cos).
            float yawRad = state.Yaw * DegToRad;
            float sin = MathF.Sin(yawRad);
            float cos = MathF.Cos(yawRad);
            float speed = (input.Buttons & InputButtons.Sprint) != 0 ? MoveSettings.SprintSpeed : MoveSettings.WalkSpeed;
            float velocityX = (cos * moveX + sin * moveY) * speed;
            float velocityZ = (-sin * moveX + cos * moveY) * speed;

            Vector3 position = state.Position;

            // 1) Leave any box we start inside (reconcile snap, rounding): sweeps assume a free start (D4).
            Depenetrate(ref position, world);

            // 2) Stateless ground check (D3). Snapping Y onto the surface keeps a standing player at an
            //    exact height, so grounded/airborne never alternates from rounding.
            if (state.VelocityY <= 0f && TryFindGround(position, world, out float groundY))
            {
                position.Y = groundY;
                state.VelocityY = (input.Buttons & InputButtons.Jump) != 0 ? MoveSettings.JumpSpeed : 0f;
            }
            else
            {
                state.VelocityY += MoveSettings.Gravity * deltaTime;
            }

            // 3) Axis-separated sweeps X -> Z -> Y (D2): a blocked axis stops, the others keep moving.
            position.X += Sweep(position, AxisX, velocityX * deltaTime, world);
            position.Z += Sweep(position, AxisZ, velocityZ * deltaTime, world);
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, AxisY, wantY, world);
            if (movedY != wantY) state.VelocityY = 0f;   // landed or hit a ceiling
            position.Y += movedY;

            state.Position = position;
        }

        public static bool IsGrounded(in MoveState state, ReadOnlySpan<Box> world)
        {
            return state.VelocityY <= 0f && TryFindGround(state.Position, world, out _);
        }

        // True if the character box at these feet overlaps any box by more than zero on every axis.
        // Touching faces (as after a sweep or a ground snap) do not count.
        public static bool OverlapsAny(Vector3 feet, ReadOnlySpan<Box> world)
        {
            GetBounds(feet, out Vector3 min, out Vector3 max);
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (min.X < box.Max.X && max.X > box.Min.X &&
                    min.Y < box.Max.Y && max.Y > box.Min.Y &&
                    min.Z < box.Max.Z && max.Z > box.Min.Z)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Depenetrate(ref Vector3 feet, ReadOnlySpan<Box> world)
        {
            if (feet.Y < 0f) feet.Y = 0f;

            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                GetBounds(feet, out Vector3 min, out Vector3 max);

                float overlapX = MathF.Min(max.X, box.Max.X) - MathF.Max(min.X, box.Min.X);
                float overlapY = MathF.Min(max.Y, box.Max.Y) - MathF.Max(min.Y, box.Min.Y);
                float overlapZ = MathF.Min(max.Z, box.Max.Z) - MathF.Max(min.Z, box.Min.Z);
                if (overlapX <= MoveSettings.Skin || overlapY <= MoveSettings.Skin || overlapZ <= MoveSettings.Skin) continue;

                // Distance to clear each face. The smallest wins; ties keep the first in this fixed
                // order (-X, +X, -Z, +Z, +Y, -Y), so the result is deterministic.
                float best = max.X - box.Min.X;
                int direction = 0;
                float push = box.Max.X - min.X;
                if (push < best) { best = push; direction = 1; }
                push = max.Z - box.Min.Z;
                if (push < best) { best = push; direction = 2; }
                push = box.Max.Z - min.Z;
                if (push < best) { best = push; direction = 3; }
                push = box.Max.Y - min.Y;
                if (push < best) { best = push; direction = 4; }
                push = max.Y - box.Min.Y;
                // Pushing down is only allowed while the feet stay above the floor.
                if (push < best && feet.Y - push - MoveSettings.Skin >= 0f) { best = push; direction = 5; }

                float distance = best + MoveSettings.Skin;
                switch (direction)
                {
                    case 0: feet.X -= distance; break;
                    case 1: feet.X += distance; break;
                    case 2: feet.Z -= distance; break;
                    case 3: feet.Z += distance; break;
                    case 4: feet.Y += distance; break;
                    default: feet.Y -= distance; break;
                }
            }
        }

        // Highest floor or box top within GroundProbe of the feet, under the character's footprint.
        private static bool TryFindGround(Vector3 feet, ReadOnlySpan<Box> world, out float groundY)
        {
            bool found = feet.Y <= MoveSettings.GroundProbe;
            groundY = 0f;

            float minX = feet.X - MoveSettings.HalfWidth;
            float maxX = feet.X + MoveSettings.HalfWidth;
            float minZ = feet.Z - MoveSettings.HalfWidth;
            float maxZ = feet.Z + MoveSettings.HalfWidth;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (box.Max.X <= minX || box.Min.X >= maxX || box.Max.Z <= minZ || box.Min.Z >= maxZ) continue;

                float top = box.Max.Y;
                if (top < feet.Y - MoveSettings.GroundProbe || top > feet.Y + MoveSettings.GroundProbe) continue;
                if (!found || top > groundY)
                {
                    groundY = top;
                    found = true;
                }
            }
            return found;
        }

        // How far the character may move along one axis (same sign as delta, |result| <= |delta|).
        // Every box that overlaps on the other two axes and lies ahead limits the move to its near
        // face minus Skin, whatever the distance, so a fast fall cannot pass through a thin box.
        private static float Sweep(Vector3 feet, int axis, float delta, ReadOnlySpan<Box> world)
        {
            if (delta == 0f) return 0f;

            GetBounds(feet, out Vector3 min, out Vector3 max);
            float limit = MathF.Abs(delta);
            if (axis == AxisY && delta < 0f && min.Y < limit) limit = min.Y;   // floor plane y = 0, no Skin

            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (!OverlapsOnOtherAxes(min, max, box, axis)) continue;

                float gap;
                if (delta > 0f)
                {
                    float near = Component(box.Min, axis);
                    float front = Component(max, axis);
                    if (near < front - MoveSettings.Skin) continue;   // behind us or already overlapping
                    gap = near - front - MoveSettings.Skin;
                }
                else
                {
                    float near = Component(box.Max, axis);
                    float front = Component(min, axis);
                    if (near > front + MoveSettings.Skin) continue;
                    gap = front - near - MoveSettings.Skin;
                }

                if (gap < 0f) gap = 0f;
                if (gap < limit) limit = gap;
            }
            return delta > 0f ? limit : -limit;
        }

        private static bool OverlapsOnOtherAxes(Vector3 min, Vector3 max, in Box box, int axis)
        {
            bool x = axis == AxisX || (min.X < box.Max.X && max.X > box.Min.X);
            bool y = axis == AxisY || (min.Y < box.Max.Y && max.Y > box.Min.Y);
            bool z = axis == AxisZ || (min.Z < box.Max.Z && max.Z > box.Min.Z);
            return x && y && z;
        }

        private static void GetBounds(Vector3 feet, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
            max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + MoveSettings.Height, feet.Z + MoveSettings.HalfWidth);
        }

        // System.Numerics.Vector3 has no indexer in netstandard2.1.
        private static float Component(Vector3 v, int axis) => axis == AxisX ? v.X : axis == AxisY ? v.Y : v.Z;

        private static float Finite(float value) => IsFinite(value) ? value : 0f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float NormalizeYaw(float yaw)
        {
            yaw %= 360f;
            if (yaw < 0f) yaw += 360f;
            return yaw;
        }
    }
}
```

구현 메모:
- 박스 윗면에 정확히 선 상태(Y = top)는 Y 범위가 겹치지 않는 것으로 본다(엄격한 `<`). 그래서 서 있는 박스가 X/Z Sweep을 막지 않는다.
- 바닥 y=0은 Skin 없이 정확히 0에 착지한다(Phase 0 결과와 같음). 박스는 top + Skin에 착지하고 다음 Step의 접지 판정이 Y를 top으로 맞춘다.

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: `CollisionTests` 13개와 기존 테스트 전부 PASS(빈 지형 `MovementSimulationTests`, `MatchTests` 포함)

- [ ] **Step 5: 체크포인트**

PASS 결과를 기록한다. Hot Path 확인: `Step`·`Sweep`·`Depenetrate`·`TryFindGround`에 `new` 배열·LINQ·예외·클로저가 없다(`Vector3`는 값 타입). 커밋하지 않는다.

---

### Task 3: `TestArena`, 서버 적용, ProtocolVersion 2

**Files:**
- Create: `Shared/Runtime/Simulation/TestArena.cs`
- Modify: `Shared/Runtime/Protocol/ProtocolConstants.cs:6`
- Modify: `Server/src/ProjectH.Server/Game/Match.cs` (`Tick`의 `Step` 호출, `SpawnPosition` 접근 수준)
- Test: `Server/tests/ProjectH.Server.Tests/Shared/TestArenaTests.cs` (신규), `Server/tests/ProjectH.Server.Tests/Shared/ProtocolConstantsTests.cs`

**Interfaces:**
- Consumes: `Box`, `MovementSimulation.Step/OverlapsAny` (Task 1–2)
- Produces:
  - `public static class TestArena { public const float ClearRadius = 7f; public static ReadOnlySpan<Box> Boxes { get; } }` — 20개 박스
  - `internal static Vector3 Match.SpawnPosition(ushort entityId)` (테스트용, `InternalsVisibleTo` 이미 설정)
  - `ProtocolConstants.ProtocolVersion == 2`

아레나 배치(모든 박스는 원점에서 수평 거리 11 m 이상. `MatchTests`는 entity 1(약 (-3.69, 0, 3.38))을 +Z로 약 2.7 m 걷게 하고, 통합 테스트는 최대 약 6.75 m 걷게 한다. +Z 방향 가장 가까운 박스 면은 z 11.75라 막히지 않는다):

| 종류 | 중심 (x, y, z) | 크기 (x, y, z) |
|---|---|---|
| 외곽 벽 4 | (0,1.5,±20), (±20,1.5,0) | (42,3,1), (1,3,39) |
| 벽 조각 5 | (12,1.5,6), (-12,1.5,-6), (6,1.5,-12), (-6,1.5,12), (-14,1.5,15) | (0.5,3,6), (0.5,3,6), (6,3,0.5), (6,3,0.5), (4,3,0.5) |
| 기둥 4 | (±9,1.5,±9) | (1,3,1) |
| 낮은 박스 3 (1 m) | (0,0.5,12), (12,0.5,-3), (-12,0.5,3) | (2,1,2) |
| 높은 박스 2 (1.5 m) | (0,0.75,-12), (14,0.75,14) | (2,1.5,2) |
| 플랫폼 2 | 받침 (-14,0.5,-14) / 윗단 (-14.5,1.5,-14.5) | (4,1,4) / (2,1,2) |

- [ ] **Step 1: 실패하는 테스트 작성**

`Server/tests/ProjectH.Server.Tests/Shared/TestArenaTests.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class TestArenaTests
{
    private const float Dt = 1f / 30f;

    [Fact]
    public void HasAtMost30Boxes()
    {
        Assert.InRange(TestArena.Boxes.Length, 1, 30);
    }

    [Fact]
    public void EveryBox_HasMinBelowMax_AndRestsOnOrAboveTheFloor()
    {
        foreach (Box box in TestArena.Boxes)
        {
            Assert.True(box.Min.X < box.Max.X);
            Assert.True(box.Min.Y < box.Max.Y);
            Assert.True(box.Min.Z < box.Max.Z);
            Assert.True(box.Min.Y >= 0f);
        }
    }

    [Fact]
    public void CentralArea_IsClear()
    {
        foreach (Box box in TestArena.Boxes)
        {
            // Horizontal distance from the origin to the box footprint.
            float dx = MathF.Max(MathF.Max(box.Min.X, -box.Max.X), 0f);
            float dz = MathF.Max(MathF.Max(box.Min.Z, -box.Max.Z), 0f);
            Assert.True(MathF.Sqrt(dx * dx + dz * dz) >= TestArena.ClearRadius);
        }
    }

    [Fact]
    public void SpawnPositions_DoNotOverlapAnyBox()
    {
        for (ushort id = 1; id <= 50; id++)
        {
            Vector3 spawn = Match.SpawnPosition(id);
            Assert.False(MovementSimulation.OverlapsAny(spawn, TestArena.Boxes), $"entity {id} at {spawn}");
        }
    }

    // D4: depenetration in a gap narrower than the character can pick an odd direction, so the
    // arena has none. Touching or overlapping boxes (gap <= 0) are fine.
    [Fact]
    public void GapsBetweenBoxes_AreZeroOrWiderThanTheCharacter()
    {
        ReadOnlySpan<Box> boxes = TestArena.Boxes;
        const float minGap = 2f * MoveSettings.HalfWidth + 2f * MoveSettings.Skin;
        for (int i = 0; i < boxes.Length; i++)
        {
            for (int j = i + 1; j < boxes.Length; j++)
            {
                Box a = boxes[i];
                Box b = boxes[j];
                bool sameHeightBand = a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y;
                if (!sameHeightBand) continue;   // stacked boxes (the platform) are not a gap

                float gapX = MathF.Max(b.Min.X - a.Max.X, a.Min.X - b.Max.X);
                float gapZ = MathF.Max(b.Min.Z - a.Max.Z, a.Min.Z - b.Max.Z);
                float gap = MathF.Max(gapX, gapZ);
                Assert.True(gap <= 0f || gap >= minGap, $"boxes {i} and {j}: gap {gap}");
            }
        }
    }

    [Fact]
    public void LongRandomWalk_InArena_IsDeterministic_AndNeverOverlaps()
    {
        var a = new MoveState { Position = new Vector3(0f, 0f, 5f) };
        var b = a;
        for (int i = 0; i < 600; i++)
        {
            var input = new InputCommand
            {
                MoveX = (i % 7) / 7f - 0.4f,
                MoveY = 1f,
                Yaw = i * 1.7f,
                Buttons = (i % 25 == 0 ? InputButtons.Jump : InputButtons.None) | (i % 3 == 0 ? InputButtons.Sprint : InputButtons.None),
            };
            MovementSimulation.Step(ref a, input, Dt, TestArena.Boxes);
            MovementSimulation.Step(ref b, input, Dt, TestArena.Boxes);
            Assert.False(MovementSimulation.OverlapsAny(a.Position, TestArena.Boxes));
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VelocityY, b.VelocityY);
        Assert.Equal(a.Yaw, b.Yaw);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Shared/ProtocolConstantsTests.cs`의 버전 테스트를 교체:

```csharp
    [Fact]
    public void ProtocolVersion_IsTwo()
    {
        // Phase 1 changed movement results (box collision); v1 clients must be rejected at connect.
        Assert.Equal((ushort)2, ProtocolConstants.ProtocolVersion);
    }
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~TestArenaTests|FullyQualifiedName~ProtocolConstantsTests"`
Expected: FAIL — `TestArena` 미정의, `Match.SpawnPosition` 접근 불가(private) 컴파일 오류

- [ ] **Step 3: `TestArena` 구현**

`Shared/Runtime/Simulation/TestArena.cs`:

```csharp
using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Code-constant test map (D5), used by both the server and client prediction so they collide
    // with the same boxes. Replaced by a real map loader in the map phase.
    // Layout rules checked by TestArenaTests: at most 30 boxes, nothing within ClearRadius of the
    // origin (players spawn on a 5 m ring, Match.SpawnPosition), and no gap between two boxes that is
    // narrower than the character (D4: depenetration in narrow gaps can pick an odd direction).
    public static class TestArena
    {
        public const float ClearRadius = 7f;

        // Static readonly array: the Boxes property wraps it without allocating.
        private static readonly Box[] s_boxes =
        {
            // Outer walls: 40 x 40 m inside, 3 m high, 1 m thick.
            Box.FromCenterSize(new Vector3(0f, 1.5f, 20f), new Vector3(42f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(0f, 1.5f, -20f), new Vector3(42f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(20f, 1.5f, 0f), new Vector3(1f, 3f, 39f)),
            Box.FromCenterSize(new Vector3(-20f, 1.5f, 0f), new Vector3(1f, 3f, 39f)),

            // Wall pieces, 3 m high, for camera collision checks.
            Box.FromCenterSize(new Vector3(12f, 1.5f, 6f), new Vector3(0.5f, 3f, 6f)),
            Box.FromCenterSize(new Vector3(-12f, 1.5f, -6f), new Vector3(0.5f, 3f, 6f)),
            Box.FromCenterSize(new Vector3(6f, 1.5f, -12f), new Vector3(6f, 3f, 0.5f)),
            Box.FromCenterSize(new Vector3(-6f, 1.5f, 12f), new Vector3(6f, 3f, 0.5f)),
            Box.FromCenterSize(new Vector3(-14f, 1.5f, 15f), new Vector3(4f, 3f, 0.5f)),

            // Pillars 1 x 1 x 3 m.
            Box.FromCenterSize(new Vector3(9f, 1.5f, 9f), new Vector3(1f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(-9f, 1.5f, 9f), new Vector3(1f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(9f, 1.5f, -9f), new Vector3(1f, 3f, 1f)),
            Box.FromCenterSize(new Vector3(-9f, 1.5f, -9f), new Vector3(1f, 3f, 1f)),

            // Low boxes, 1 m: reachable by jumping (apex about 1.34 m).
            Box.FromCenterSize(new Vector3(0f, 0.5f, 12f), new Vector3(2f, 1f, 2f)),
            Box.FromCenterSize(new Vector3(12f, 0.5f, -3f), new Vector3(2f, 1f, 2f)),
            Box.FromCenterSize(new Vector3(-12f, 0.5f, 3f), new Vector3(2f, 1f, 2f)),

            // High boxes, 1.5 m: not reachable by jumping.
            Box.FromCenterSize(new Vector3(0f, 0.75f, -12f), new Vector3(2f, 1.5f, 2f)),
            Box.FromCenterSize(new Vector3(14f, 0.75f, 14f), new Vector3(2f, 1.5f, 2f)),

            // Platform: 1 m base with a second 1 m step on top.
            Box.FromCenterSize(new Vector3(-14f, 0.5f, -14f), new Vector3(4f, 1f, 4f)),
            Box.FromCenterSize(new Vector3(-14.5f, 1.5f, -14.5f), new Vector3(2f, 1f, 2f)),
        };

        public static ReadOnlySpan<Box> Boxes => s_boxes;
    }
}
```

(중심·크기 값은 모두 2진 소수로 정확히 표현되므로 `Min.Y`가 정확히 0, 윗면이 정확히 1 / 1.5 / 2가 된다.)

- [ ] **Step 4: 서버 적용과 버전 올리기**

`Shared/Runtime/Protocol/ProtocolConstants.cs`:

```csharp
        public const ushort ProtocolVersion = 1;
```
→
```csharp
        public const ushort ProtocolVersion = 2;   // 2: Phase 1 box collision changed movement results
```

`Server/src/ProjectH.Server/Game/Match.cs` `Tick()` 안:

```csharp
            MovementSimulation.Step(ref player.State, input, _tickSeconds, ReadOnlySpan<Box>.Empty);
```
→
```csharp
            // Same boxes as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, TestArena.Boxes);
```

같은 파일의 Spawn 함수 선언:

```csharp
    // Spread players on a circle (golden angle) so they do not spawn inside each other.
    private static Vector3 SpawnPosition(ushort entityId)
```
→
```csharp
    // Spread players on a circle (golden angle) so they do not spawn inside each other.
    // The 5 m ring lies inside TestArena.ClearRadius (checked by TestArenaTests).
    internal static Vector3 SpawnPosition(ushort entityId)
```

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 전체 PASS. 특히 `TestArenaTests` 6개, `ProtocolVersion_IsTwo`, 기존 `MatchTests`·`ServerIntegrationTests`(`VersionMismatch_IsRejected`는 999를 쓰므로 영향 없음)

- [ ] **Step 6: Client 스크래치 컴파일**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0 (Client `NetClient`가 새 ProtocolVersion 상수를 그대로 사용)

- [ ] **Step 7: 체크포인트**

PASS 결과를 기록한다. `Shared/Runtime/Simulation/Box.cs`, `TestArena.cs`의 `.meta`는 Unity가 Editor import 때 만든다(직접 만들지 않는다). 커밋하지 않는다.

---

### Task 4: Client 예측에 아레나 적용, 아레나 뷰

**Files:**
- Modify: `Client/Assets/Scripts/Game/LocalPlayerPredictor.cs:64,135` (+ 클래스 주석)
- Modify: `Client/Assets/Scripts/Bootstrap/TestWorld.cs` (전체 교체)
- Modify: `Client/Assets/Scripts/Game/GameClient.cs` (World Material 소유)
- Test: `Client/Assets/Tests/EditMode/ArenaPredictionTests.cs` (신규), `Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs:45,60`

**Interfaces:**
- Consumes: `TestArena.Boxes`, `Box.Center/Size`, `MovementSimulation.Step/OverlapsAny`, `MoveSettings.HalfWidth/Skin` (Task 1–3), `VectorConversions.ToUnity`
- Produces: `TestWorld.Build(out Material boxMaterial) : GameObject` — 호출자가 root와 Material을 모두 파괴한다. GameClient 필드 `_worldMaterial`.

- [ ] **Step 1: 실패하는 테스트 작성**

`Client/Assets/Tests/EditMode/ArenaPredictionTests.cs`:

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Prediction against the Shared TestArena. The server replica below runs the same
    // MovementSimulation.Step with the same boxes, as Match.Tick does.
    public class ArenaPredictionTests
    {
        private const int SimHz = 30;
        private const float Step = 1f / SimHz;

        // Low box (0, 0.5, 12) size 2 x 1 x 2: top at y = 1, x -1..1, z 11..13.
        private static readonly Num.Vector3 OnLowBox = new Num.Vector3(0f, 1f, 12f);

        // Exactly one step per call: from a zero accumulator, Step - Step leaves exactly 0, so long
        // loops never gain an extra step from rounding slop (Advance also caps one call at 0.25 s).
        private static void AdvanceOneStep(LocalPlayerPredictor predictor, Vector2 move)
        {
            bool jump = false;
            Assert.AreEqual(1, predictor.Advance(Step, move, 0f, false, ref jump));
        }

        private static SnapshotEntity ToEntity(in MoveState s)
            => new SnapshotEntity { Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw };

        private static Num.Vector3 ToNumerics(Vector3 v) => new Num.Vector3(v.x, v.y, v.z);

        [Test]
        public void WalkingIntoLowBox_PredictionEqualsServer()
        {
            var spawn = new MoveState { Position = new Num.Vector3(0f, 0f, 9f) };
            var predictor = new LocalPlayerPredictor(SimHz, spawn);
            var server = spawn;
            for (int i = 0; i < 30; i++)
            {
                AdvanceOneStep(predictor, Vector2.up);
                MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, TestArena.Boxes);
            }

            Assert.AreEqual(server.Position.X, predictor.PredictedPosition.x, 1e-6f);
            Assert.AreEqual(server.Position.Y, predictor.PredictedPosition.y, 1e-6f);
            Assert.AreEqual(server.Position.Z, predictor.PredictedPosition.z, 1e-6f);
            // Stopped by the box side: z = 11 - 0.35 - 0.001.
            Assert.AreEqual(11f - MoveSettings.HalfWidth - MoveSettings.Skin, predictor.PredictedPosition.z, 1e-3f);
        }

        // Review Focus: standing on a box top must not jitter when snapshots reconcile it.
        [Test]
        public void StandingOnBox_ReconcileEveryStep_KeepsExactHeight()
        {
            var spawn = new MoveState { Position = OnLowBox };
            var predictor = new LocalPlayerPredictor(SimHz, spawn);
            var server = spawn;
            for (uint seq = 1; seq <= 90; seq++)
            {
                AdvanceOneStep(predictor, Vector2.zero);
                MovementSimulation.Step(ref server, new InputCommand { Seq = seq }, Step, TestArena.Boxes);
                predictor.Reconcile(ToEntity(server), seq);

                Assert.AreEqual(1f, predictor.PredictedPosition.y);
                Assert.AreEqual(1f, predictor.RenderPosition.y, 1e-6f);
            }
        }

        // Review Focus: a server correction may place the player inside a box (for example a stale
        // snapshot). Replaying the unacked inputs must push it out, not leave it stuck inside.
        [Test]
        public void ReconcileIntoPillar_ReplayEndsOutsideEveryBox()
        {
            var predictor = new LocalPlayerPredictor(SimHz, new MoveState { Position = new Num.Vector3(7f, 0f, 7f) });
            for (int i = 0; i < 3; i++) AdvanceOneStep(predictor, Vector2.zero);

            // Pillar (9, 1.5, 9) size 1 x 3 x 1: this position is its centre.
            predictor.Reconcile(new SnapshotEntity { Position = new Num.Vector3(9f, 0f, 9f) }, 1);

            Assert.IsFalse(MovementSimulation.OverlapsAny(ToNumerics(predictor.PredictedPosition), TestArena.Boxes));
        }
    }
}
```

`Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs`의 서버 복제 두 곳을 예측기와 같은 지형으로 바꾼다(Task 1에서 추가한 `using System;`은 더 이상 필요 없으니 지운다).

```csharp
            for (int i = 0; i < 2; i++) MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, TestArena.Boxes);
```
```csharp
            MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, TestArena.Boxes);
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: FAIL — `WalkingIntoLowBox_PredictionEqualsServer`(예측기가 빈 지형이라 z가 11.649를 지나감), `StandingOnBox_ReconcileEveryStep_KeepsExactHeight`(빈 지형에서 y=1은 떨어짐), `ReconcileIntoPillar_ReplayEndsOutsideEveryBox`

- [ ] **Step 3: 예측기에 아레나 적용**

`Client/Assets/Scripts/Game/LocalPlayerPredictor.cs` 두 곳:

```csharp
                MovementSimulation.Step(ref _state, command, _stepSeconds, ReadOnlySpan<Box>.Empty);
```
→
```csharp
                MovementSimulation.Step(ref _state, command, _stepSeconds, TestArena.Boxes);
```

```csharp
                MovementSimulation.Step(ref _state, _inputs[slot], _stepSeconds, ReadOnlySpan<Box>.Empty);
```
→
```csharp
                MovementSimulation.Step(ref _state, _inputs[slot], _stepSeconds, TestArena.Boxes);
```

클래스 주석 첫 줄 다음에 한 줄 추가:

```csharp
    // Collides with Shared TestArena.Boxes, the same boxes the server passes in Match.Tick.
```

- [ ] **Step 4: 아레나 뷰와 Material 수명**

`Client/Assets/Scripts/Bootstrap/TestWorld.cs` 전체:

```csharp
using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Bootstrap
{
    // Throwaway test map: 100 x 100 m ground, a light and one cube per Shared TestArena box (D5, D10).
    // The cubes keep their BoxColliders for camera collision and fire rays only; movement collision is
    // MovementSimulation with the same TestArena boxes, so there is a single source for the map.
    // Replaced by a real map in Phase 6.
    public static class TestWorld
    {
        // boxMaterial is shared by every cube; the caller destroys it together with the returned root.
        public static GameObject Build(out Material boxMaterial)
        {
            var root = new GameObject("TestWorld");

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(10f, 1f, 10f);

            // Copy of the primitive's default material, so the shader is guaranteed to be in the build.
            boxMaterial = new Material(ground.GetComponent<Renderer>().sharedMaterial) { color = new Color(0.55f, 0.56f, 0.6f) };
            foreach (Box box in TestArena.Boxes)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "ArenaBox";
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = box.Center.ToUnity();
                cube.transform.localScale = box.Size.ToUnity();
                cube.GetComponent<Renderer>().sharedMaterial = boxMaterial;
            }

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                var sun = new GameObject("Sun");
                sun.transform.SetParent(root.transform, false);
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                var light = sun.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.None;   // low-spec default until the real map exists
            }
            return root;
        }
    }
}
```

`Client/Assets/Scripts/Game/GameClient.cs` 세 곳:

```csharp
        private GameObject _world;
```
→
```csharp
        private GameObject _world;
        private Material _worldMaterial;
```

```csharp
            _world = TestWorld.Build();
```
→
```csharp
            _world = TestWorld.Build(out _worldMaterial);
```

```csharp
            if (_world != null) Destroy(_world);
```
→
```csharp
            if (_world != null) Destroy(_world);
            if (_worldMaterial != null) Destroy(_worldMaterial);
```

- [ ] **Step 5: 스크래치 검증**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 기존 테스트 + `ArenaPredictionTests` 3개 PASS

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

- [ ] **Step 6: Unity Editor import 확인**

Task 1 Step 0에서 기록한 `N0` 이후만 본다(로그는 세션 동안 누적되므로). Task 1–3의 Shared·Predictor 변경도 이 import에서 함께 컴파일된다.

사용자에게 Unity Editor 창을 한 번 클릭(포커스)해 자동 import·컴파일을 일으켜 달라고 요청한다. 컴파일이 끝나면(`N0`를 기록한 숫자로 바꿔서):

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; tail -n +N0 "$LOG" | grep -n "error CS"
```

Expected: 출력 없음. Shared 새 파일(`Box.cs`, `TestArena.cs`)의 `.meta`가 Unity에 의해 생성됐는지 `ls Shared/Runtime/Simulation/*.meta`로 확인한다.

- [ ] **Step 7: 사용자 수동 확인 요청**

서버 실행(`dotnet run --project Server/src/ProjectH.Server`) 후 Editor Play + Multiplayer Play Mode Player 2로 두 Client 접속:
- 회색 박스 20개(외곽 벽, 벽 조각, 기둥, 낮은/높은 박스, 2단 플랫폼)가 보인다.
- 벽으로 걸으면 멈추고, 비스듬히 걸으면 벽을 따라 미끄러진다.
- 낮은 박스(1 m)는 점프로 올라서고, 높은 박스(1.5 m)는 못 오른다. 박스 위에 서 있을 때 떨리지 않는다.
- 상대 Client 화면에서도 같은 충돌이 떨림 없이 보인다.
(이 시점의 카메라는 아직 Phase 0 추적 카메라다.)

- [ ] **Step 8: 체크포인트**

NUnit·컴파일·Editor.log 결과와 수동 확인 결과(사용자 응답)를 기록한다. 커밋하지 않는다.

---

### Task 5: 어깨 너머 카메라, ADS, 카메라 충돌

**Files:**
- Create: `Client/Assets/Scripts/Camera/ShoulderCameraMath.cs`, `Client/Assets/Scripts/Camera/ShoulderCamera.cs`
- Delete: `Client/Assets/Scripts/Camera/ThirdPersonCamera.cs`, `Client/Assets/Scripts/Camera/ThirdPersonCamera.cs.meta`
- Modify: `Client/Assets/Scripts/Input/InputReader.cs` (Aim 추가), `Client/Assets/Scripts/Game/GameClient.cs` (카메라 연결)
- Modify(스크래치): `.../scratchpad/predictortests/PredictorTests.csproj` (Compile Include 추가)
- Test: `Client/Assets/Tests/EditMode/ShoulderCameraMathTests.cs` (신규)

**Interfaces:**
- Consumes: 없음(순수 Unity 수학), `InputReader.LookDelta`
- Produces:
  - `interface ISphereCaster { bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance); }` (네임스페이스 `ProjectH.Client.CameraControl`)
  - `struct ShoulderPose { Vector3 Shoulder, Position, Forward; float Distance, FieldOfView; }`
  - `static class ShoulderCameraMath { const PivotHeight 1.6, HipDistance 3.5, HipShoulder 0.55, HipFov 60, AimDistance 1.6, AimShoulder 0.65, AimFov 42, AimSensitivityScale 0.6, ReturnSharpness 6; Vector3 Forward(float yaw, float pitch); Vector3 Right(float yaw); float Approach(float current, float target, float sharpness, float dt); float ResolveDistance(float current, float allowed, float dt); ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance, float dt, ISphereCaster caster); }`
  - `sealed class ShoulderCamera { ShoulderCamera(Camera camera); float Yaw; float Pitch; Ray AimRay; void ApplyLook(Vector2 lookDelta, bool aiming); void Follow(Vector3 targetFeet, bool aiming, float deltaTime); }` — `AimRay`는 Task 6 발사가 쓴다.
  - `InputReader.AimHeld : bool`

- [ ] **Step 0: Editor.log 기준선 기록 (코드 수정 전)**

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; wc -l < "$LOG"
```

출력값을 `N5`로 체크포인트에 기록한다(Step 8에서 이 줄 이후만 검사).

- [ ] **Step 1: 스크래치 NUnit 프로젝트에 순수 파일 등록**

`C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj`의 `RemotePlayerInterpolator.cs` Compile 줄 다음에 추가:

```xml
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Camera/ShoulderCameraMath.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Camera/ShoulderCameraMath.cs')" />
```

- [ ] **Step 2: 실패하는 테스트 작성**

`Client/Assets/Tests/EditMode/ShoulderCameraMathTests.cs`:

```csharp
using NUnit.Framework;
using ProjectH.Client.CameraControl;
using UnityEngine;

namespace ProjectH.Client.Tests
{
    public class ShoulderCameraMathTests
    {
        private const float Eps = 1e-4f;

        // Scripted sphere casts: the first call is the pivot -> shoulder cast, the second the
        // shoulder -> camera cast. A negative distance means "no hit".
        private sealed class FakeCaster : ISphereCaster
        {
            public float ShoulderHit = -1f;
            public float BackHit = -1f;
            public int Calls;
            public Vector3 SecondOrigin;

            public bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance)
            {
                Calls++;
                float scripted = Calls == 1 ? ShoulderHit : BackHit;
                if (Calls == 2) SecondOrigin = origin;
                if (scripted >= 0f && scripted < maxDistance)
                {
                    hitDistance = scripted;
                    return true;
                }
                hitDistance = 0f;
                return false;
            }
        }

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.AreEqual(expected.x, actual.x, Eps, "x");
            Assert.AreEqual(expected.y, actual.y, Eps, "y");
            Assert.AreEqual(expected.z, actual.z, Eps, "z");
        }

        [Test]
        public void HipPose_Yaw0_IsBehindRightShoulder()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, new FakeCaster());

            AssertVector(new Vector3(0.55f, 1.6f, 0f), pose.Shoulder);
            AssertVector(new Vector3(0.55f, 1.6f, -3.5f), pose.Position);
            AssertVector(Vector3.forward, pose.Forward);
            Assert.AreEqual(60f, pose.FieldOfView, Eps);
        }

        [Test]
        public void AimPose_IsCloserWithNarrowerFov()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 1f, 3.5f, 1f / 60f, new FakeCaster());

            // Moving in is never delayed: 1.6 < 3.5 applies at once.
            AssertVector(new Vector3(0.65f, 1.6f, -1.6f), pose.Position);
            Assert.AreEqual(42f, pose.FieldOfView, Eps);
        }

        [Test]
        public void HipPose_Yaw90_RotatesOffsets()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(new Vector3(10f, 0f, 0f), 90f, 0f, 0f, 3.5f, 1f / 60f, new FakeCaster());

            // Facing +X: right is -Z, behind is -X.
            AssertVector(new Vector3(10f, 1.6f, -0.55f), pose.Shoulder);
            AssertVector(new Vector3(6.5f, 1.6f, -0.55f), pose.Position);
        }

        [Test]
        public void PitchDown_RaisesCamera()
        {
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 30f, 0f, 3.5f, 1f / 60f, new FakeCaster());

            // Forward (0, -0.5, 0.866): the camera sits 3.5 * 0.5 = 1.75 m above the shoulder.
            Assert.AreEqual(1.6f + 1.75f, pose.Position.y, Eps);
        }

        [Test]
        public void WallBehind_PullsCameraInImmediately()
        {
            var caster = new FakeCaster { BackHit = 1f };
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, caster);

            Assert.AreEqual(1f, pose.Distance, Eps);
            AssertVector(new Vector3(0.55f, 1.6f, -1f), pose.Position);
        }

        [Test]
        public void WallCleared_CameraEasesBackOut()
        {
            var caster = new FakeCaster();
            float distance = 1f;
            float previous = distance;
            for (int frame = 0; frame < 120; frame++)
            {
                caster.Calls = 0;
                distance = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, distance, 1f / 60f, caster).Distance;
                Assert.Greater(distance, previous);   // strictly moving out, no jump
                Assert.LessOrEqual(distance, 3.5f);
                if (frame == 0) Assert.Less(distance, 1.5f);
                previous = distance;
            }
            Assert.AreEqual(3.5f, distance, 0.01f);   // 2 s at sharpness 6: e^-12 of the gap remains
        }

        // Review Focus: standing against a wall on the right puts the shoulder point inside the wall.
        // The second cast must start from the shortened shoulder, or it would miss that wall.
        [Test]
        public void ShoulderInsideWall_SecondCastStartsFromShortenedShoulder()
        {
            var caster = new FakeCaster { ShoulderHit = 0.1f };
            ShoulderPose pose = ShoulderCameraMath.Solve(Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, caster);

            Assert.AreEqual(2, caster.Calls);
            AssertVector(new Vector3(0.1f, 1.6f, 0f), caster.SecondOrigin);
            AssertVector(new Vector3(0.1f, 1.6f, 0f), pose.Shoulder);
            AssertVector(new Vector3(0.1f, 1.6f, -3.5f), pose.Position);
        }

        [Test]
        public void Forward_MatchesUnityEulerConvention()
        {
            // Quaternion.Euler(0, 90, 0) * forward = +X; Euler(90, 0, 0) * forward = -Y.
            AssertVector(Vector3.right, ShoulderCameraMath.Forward(90f, 0f));
            AssertVector(Vector3.down, ShoulderCameraMath.Forward(0f, 90f));
            AssertVector(new Vector3(0f, 0f, -1f), ShoulderCameraMath.Right(90f));
        }
    }
}
```

- [ ] **Step 3: 테스트 실패 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: FAIL — `ProjectH.Client.CameraControl` 네임스페이스·`ShoulderCameraMath`·`ISphereCaster` 미정의 컴파일 오류

- [ ] **Step 4: 순수 카메라 계산 구현**

`Client/Assets/Scripts/Camera/ShoulderCameraMath.cs`:

```csharp
using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Casts a sphere of the camera's collision radius. ShoulderCamera implements it with
    // Physics.SphereCast; tests use a fake, so the two-stage solve runs without Unity physics.
    public interface ISphereCaster
    {
        bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance);
    }

    public struct ShoulderPose
    {
        public Vector3 Shoulder;     // shoulder point after collision; the aim ray starts here
        public Vector3 Position;     // camera position
        public Vector3 Forward;      // camera forward (screen centre)
        public float Distance;       // camera distance behind the shoulder after collision
        public float FieldOfView;
    }

    // Pure over-the-shoulder camera math (D8, D10). No Physics and no Quaternion.Euler/LookRotation:
    // those are native engine calls, and EditMode tests of this class also run outside Unity.
    // All tuning values live here.
    public static class ShoulderCameraMath
    {
        public const float PivotHeight = 1.6f;
        public const float HipDistance = 3.5f;
        public const float HipShoulder = 0.55f;
        public const float HipFov = 60f;
        public const float AimDistance = 1.6f;
        public const float AimShoulder = 0.65f;
        public const float AimFov = 42f;
        public const float AimSensitivityScale = 0.6f;
        public const float ReturnSharpness = 6f;   // how fast the camera moves back out after a wall

        // Same convention as Quaternion.Euler(pitch, yaw, 0) * Vector3.forward: positive pitch looks down.
        public static Vector3 Forward(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * cosPitch, -Mathf.Sin(pitch), Mathf.Cos(yaw) * cosPitch);
        }

        // Horizontal right of the yaw heading (matches MovementSimulation's right vector).
        public static Vector3 Right(float yawDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
        }

        // Frame-rate independent exponential approach.
        public static float Approach(float current, float target, float sharpness, float deltaTime)
        {
            return current + (target - current) * (1f - Mathf.Exp(-sharpness * deltaTime));
        }

        // A wall closer than the current distance pulls the camera in at once (never show the wall's
        // inside); when the way clears, the camera eases back out.
        public static float ResolveDistance(float current, float allowed, float deltaTime)
        {
            return allowed < current ? allowed : Approach(current, allowed, ReturnSharpness, deltaTime);
        }

        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster)
        {
            float t = Mathf.Clamp01(aimBlend);
            float targetDistance = Mathf.Lerp(HipDistance, AimDistance, t);
            float shoulderOffset = Mathf.Lerp(HipShoulder, AimShoulder, t);
            Vector3 forward = Forward(yaw, pitch);
            Vector3 right = Right(yaw);
            Vector3 pivot = feet + new Vector3(0f, PivotHeight, 0f);

            // Stage 1: pivot -> shoulder. Against a wall on the right the shoulder point itself would be
            // inside the wall, and a cast starting there would not see it (D10).
            float reach = caster.Cast(pivot, right, shoulderOffset, out float hit) ? hit : shoulderOffset;
            Vector3 shoulder = pivot + right * reach;

            // Stage 2: (possibly shortened) shoulder -> wanted camera position.
            float allowed = caster.Cast(shoulder, -forward, targetDistance, out hit) ? hit : targetDistance;
            float distance = ResolveDistance(currentDistance, allowed, deltaTime);

            return new ShoulderPose
            {
                Shoulder = shoulder,
                Position = shoulder - forward * distance,
                Forward = forward,
                Distance = distance,
                FieldOfView = Mathf.Lerp(HipFov, AimFov, t),
            };
        }
    }
}
```

- [ ] **Step 5: 순수 테스트 통과 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: `ShoulderCameraMathTests` 8개 포함 전체 PASS

- [ ] **Step 6: `ShoulderCamera` 구현, 이전 카메라 삭제**

`Client/Assets/Scripts/Camera/ShoulderCamera.cs`:

```csharp
using UnityEngine;

namespace ProjectH.Client.CameraControl
{
    // Presentation only (D8, D10): over-the-right-shoulder camera with aim (ADS) zoom and wall
    // collision. It never writes simulation or network state; its Yaw is copied into each
    // InputCommand, the only way camera input reaches the server. Pitch stays local (D15).
    public sealed class ShoulderCamera
    {
        private const float Sensitivity = 0.1f;
        private const float MinPitch = -30f;
        private const float MaxPitch = 70f;
        private const float AimBlendSharpness = 12f;
        private const float CollisionRadius = 0.2f;
        // Below CollisionRadius, so the near plane cannot reach into a wall the sphere stopped at.
        private const float NearClip = 0.05f;

        private readonly Camera _camera;
        private readonly Transform _transform;
        private readonly PhysicsSphereCaster _caster = new PhysicsSphereCaster();
        private float _aimBlend;
        private float _distance = ShoulderCameraMath.HipDistance;

        public ShoulderCamera(Camera camera)
        {
            _camera = camera;
            _transform = camera.transform;
            _camera.nearClipPlane = NearClip;
        }

        public float Yaw { get; private set; }
        public float Pitch { get; private set; } = 10f;

        // Screen-centre ray. It starts at the shoulder point, which lies on the camera's forward axis,
        // so geometry between the camera and the player is never picked as the aim point.
        public Ray AimRay { get; private set; }

        public void ApplyLook(Vector2 lookDelta, bool aiming)
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            float sensitivity = aiming ? Sensitivity * ShoulderCameraMath.AimSensitivityScale : Sensitivity;
            Yaw = Mathf.Repeat(Yaw + lookDelta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - lookDelta.y * sensitivity, MinPitch, MaxPitch);
        }

        // Call from LateUpdate with the rendered feet position.
        public void Follow(Vector3 targetFeet, bool aiming, float deltaTime)
        {
            _aimBlend = ShoulderCameraMath.Approach(_aimBlend, aiming ? 1f : 0f, AimBlendSharpness, deltaTime);
            ShoulderPose pose = ShoulderCameraMath.Solve(targetFeet, Yaw, Pitch, _aimBlend, _distance, deltaTime, _caster);
            _distance = pose.Distance;
            _transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(Pitch, Yaw, 0f));
            _camera.fieldOfView = pose.FieldOfView;
            AimRay = new Ray(pose.Shoulder, pose.Forward);
        }

        // Single-result SphereCast: no allocation. Player views have no colliders, so only the world is hit.
        // The pivot (feet + 1.6 m) is inside the character's collision box, which the simulation keeps
        // out of every box, and 0.2 m < 0.35 m half-width, so stage 1 never starts inside a wall.
        private sealed class PhysicsSphereCaster : ISphereCaster
        {
            public bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance)
            {
                if (Physics.SphereCast(origin, CollisionRadius, direction, out RaycastHit hit, maxDistance,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    hitDistance = hit.distance;
                    return true;
                }
                hitDistance = 0f;
                return false;
            }
        }
    }
}
```

삭제(짝 `.meta`도 같이):

```bash
rm Client/Assets/Scripts/Camera/ThirdPersonCamera.cs Client/Assets/Scripts/Camera/ThirdPersonCamera.cs.meta
```

- [ ] **Step 7: Aim 입력과 GameClient 연결**

`Client/Assets/Scripts/Input/InputReader.cs`에 Aim을 추가한다.

필드 `private readonly InputAction _unlockCursor;` 앞에:

```csharp
        private readonly InputAction _aim;
```

생성자에서 `_unlockCursor = new InputAction(...)` 줄 앞에:

```csharp
            _aim = new InputAction("Aim", InputActionType.Button, "<Mouse>/rightButton");
```

`_unlockCursor.Enable();` 앞에 `_aim.Enable();`, 속성 `public bool UnlockCursorPressed ...` 앞에:

```csharp
        public bool AimHeld => _aim.IsPressed();
```

`Dispose()`의 `_unlockCursor.Dispose();` 앞에 `_aim.Dispose();`.

`Client/Assets/Scripts/Game/GameClient.cs`:

```csharp
        private ThirdPersonCamera _camera;
```
→
```csharp
        private ShoulderCamera _camera;
```

`private double _interpolationDelaySeconds;` 다음에:

```csharp
        private bool _aiming;
```

```csharp
            _camera = new ThirdPersonCamera(main.transform);
```
→
```csharp
            _camera = new ShoulderCamera(main);
```

`Update()`의 `UpdateCursorLock();` 다음 줄에:

```csharp
            // Aim only while the mouse controls the game.
            _aiming = Cursor.lockState == CursorLockMode.Locked && _input.AimHeld;
```

```csharp
            _camera.ApplyLook(_input.LookDelta);
```
→
```csharp
            _camera.ApplyLook(_input.LookDelta, _aiming);
```

```csharp
            if (_predictor != null) _camera.Follow(_predictor.RenderPosition);
```
→
```csharp
            if (_predictor != null) _camera.Follow(_predictor.RenderPosition, _aiming, Time.deltaTime);
```

- [ ] **Step 8: 스크래치 컴파일과 Editor 확인**

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 전체 PASS

Editor.log: Step 0의 `N5` 이후만 본다. 사용자가 Editor를 포커스해 import가 끝난 뒤:

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; tail -n +N5 "$LOG" | grep -n "error CS"
```

Expected: 출력 없음

- [ ] **Step 9: 사용자 수동 확인 요청**

- 카메라가 캐릭터 오른쪽 어깨 너머에 있다.
- 우클릭을 누르는 동안 가까이 당겨지고 시야각이 줄며 마우스 감도가 낮아진다. 떼면 부드럽게 돌아온다.
- 벽 조각·기둥에 오른쪽으로 붙은 채 카메라를 360° 돌려도 벽 안쪽(뒷면)이 보이지 않는다. 벽에서 떨어지면 카메라가 부드럽게 원래 거리로 돌아간다.

- [ ] **Step 10: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 6: 조준점, 로컬 발사 연출, 좌클릭 규칙

**Files:**
- Create: `Client/Assets/Scripts/Game/FireRateAccumulator.cs`, `Client/Assets/Scripts/Game/RingCursor.cs`, `Client/Assets/Scripts/Game/LocalFireEffects.cs`, `Client/Assets/Scripts/Game/Crosshair.cs`
- Modify: `Client/Assets/Scripts/Input/InputReader.cs` (전체 교체), `Client/Assets/Scripts/Game/GameClient.cs` (전체 교체), `Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs:61`, `Client/Assets/Scripts/ProjectH.Client.asmdef`
- Modify(스크래치): `.../scratchpad/predictortests/PredictorTests.csproj`
- Test: `Client/Assets/Tests/EditMode/FireRateAccumulatorTests.cs` (신규)

**Interfaces:**
- Consumes: `ShoulderCamera.AimRay/Yaw`, `LocalPlayerPredictor.RenderPosition` (Task 4–5)
- Produces:
  - `sealed class FireRateAccumulator(float shotsPerSecond, int maxShotsPerFrame) { int Consume(float deltaTime, bool triggerHeld); }`
  - `sealed class RingCursor(int capacity) { int Capacity; int Next(); }`
  - `sealed class LocalFireEffects : IDisposable { const int TracerPoolSize = 16, ImpactPoolSize = 32; void Tick(float deltaTime, bool triggerHeld, Ray aimRay, Vector3 feet, float yaw, float now); void HideAll(); void Dispose(); }`
  - `sealed class Crosshair : IDisposable { void SetVisible(bool visible); void Dispose(); }`
  - `InputReader.FirePressed`, `FireHeld`, `AimHeld` (`LockCursorPressed`는 제거되어 `FirePressed`로 대체)

- [ ] **Step 0: Editor.log 기준선 기록 (코드 수정 전)**

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; wc -l < "$LOG"
```

출력값을 `N6`로 체크포인트에 기록한다(Step 8에서 이 줄 이후만 검사).

- [ ] **Step 1: 스크래치 NUnit 프로젝트에 순수 파일 등록**

`.../scratchpad/predictortests/PredictorTests.csproj`의 `ShoulderCameraMath.cs` 줄 다음에 추가:

```xml
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/FireRateAccumulator.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/FireRateAccumulator.cs')" />
    <Compile Include="E:/popol/ProjectH/Client/Assets/Scripts/Game/RingCursor.cs" Condition="Exists('E:/popol/ProjectH/Client/Assets/Scripts/Game/RingCursor.cs')" />
```

(`LocalFireEffects.cs`·`Crosshair.cs`는 Physics·UGUI를 쓰므로 이 프로젝트에 넣지 않는다.)

- [ ] **Step 2: 실패하는 테스트 작성**

`Client/Assets/Tests/EditMode/FireRateAccumulatorTests.cs`:

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;

namespace ProjectH.Client.Tests
{
    public class FireRateAccumulatorTests
    {
        private static FireRateAccumulator NewRate() => new FireRateAccumulator(10f, 3);

        [Test]
        public void HeldForOneSecond_FiresTenShots()
        {
            var rate = NewRate();
            int shots = 0;
            // 100 frames of 10 ms: shots at about 0, 0.1, ..., 0.9 s; the 11th would be at 1.0 s (frame 101).
            for (int frame = 0; frame < 100; frame++) shots += rate.Consume(0.01f, true);
            Assert.AreEqual(10, shots);
        }

        [Test]
        public void FirstShot_FiresOnThePressFrame()
        {
            Assert.AreEqual(1, NewRate().Consume(0.016f, true));
        }

        [Test]
        public void Released_FiresNothing()
        {
            var rate = NewRate();
            for (int frame = 0; frame < 100; frame++) Assert.AreEqual(0, rate.Consume(0.01f, false));
        }

        // Review Focus: a hitch frame must not burst a pile of effects.
        [Test]
        public void HitchFrame_IsCapped_AndBacklogIsDropped()
        {
            var rate = NewRate();
            Assert.AreEqual(3, rate.Consume(1f, true));        // 1 s frame would be 10 shots
            Assert.LessOrEqual(rate.Consume(0.01f, true), 1);  // no burst of the dropped 7 next frame
        }

        [Test]
        public void TappingEveryOtherFrame_CannotBeatTheRate()
        {
            var rate = NewRate();
            int shots = 0;
            for (int frame = 0; frame < 100; frame++) shots += rate.Consume(0.01f, frame % 2 == 0);
            Assert.LessOrEqual(shots, 10);
            Assert.Greater(shots, 0);
        }

        // Review Focus: the effect pools must not grow however long the trigger is held. LocalFireEffects
        // allocates its arrays once with the cursor capacity (16 tracers, 32 impacts) and only indexes them
        // through RingCursor.Next().
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

- [ ] **Step 3: 테스트 실패 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: FAIL — `FireRateAccumulator`, `RingCursor` 미정의 컴파일 오류

- [ ] **Step 4: 순수 누적기와 링 인덱스 구현**

`Client/Assets/Scripts/Game/FireRateAccumulator.cs`:

```csharp
namespace ProjectH.Client.Game
{
    // Turns "trigger held" into a whole number of shots per frame at a fixed rate (D11: 10/s).
    // A long frame fires at most maxShotsPerFrame and drops the rest of the backlog, so a hitch never
    // bursts a pile of effects. Releasing the trigger does not bank shots: tapping cannot beat the rate.
    public sealed class FireRateAccumulator
    {
        private readonly float _interval;
        private readonly int _maxShotsPerFrame;
        private float _cooldown;   // seconds until the next shot; 0 means ready

        public FireRateAccumulator(float shotsPerSecond, int maxShotsPerFrame)
        {
            _interval = 1f / shotsPerSecond;
            _maxShotsPerFrame = maxShotsPerFrame;
        }

        public int Consume(float deltaTime, bool triggerHeld)
        {
            _cooldown -= deltaTime;
            int shots = 0;
            if (triggerHeld)
            {
                while (_cooldown <= 0f && shots < _maxShotsPerFrame)
                {
                    shots++;
                    _cooldown += _interval;
                }
            }
            if (_cooldown < 0f) _cooldown = 0f;
            return shots;
        }
    }
}
```

`Client/Assets/Scripts/Game/RingCursor.cs`:

```csharp
namespace ProjectH.Client.Game
{
    // Slot index for a fixed-size ring pool (D14): hands out 0..Capacity-1 in order and then reuses
    // the oldest slot. The pool arrays are allocated once with Capacity entries and never grow.
    public sealed class RingCursor
    {
        private int _next;

        public RingCursor(int capacity)
        {
            if (capacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }

        public int Next()
        {
            int slot = _next;
            _next = slot + 1 == Capacity ? 0 : slot + 1;
            return slot;
        }
    }
}
```

- [ ] **Step 5: 순수 테스트 통과 확인**

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: `FireRateAccumulatorTests` 6개 포함 전체 PASS

- [ ] **Step 6: 발사 연출과 조준점 구현**

`Client/Assets/Scripts/Game/LocalFireEffects.cs`:

```csharp
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Client-only fire presentation (D11, D13, D14): tracers and impact marks, no packet, no damage.
    // Everything is created once in the constructor and reused through fixed ring pools, so firing
    // 10 times a second allocates nothing and memory stays constant. Dispose destroys it all.
    public sealed class LocalFireEffects : System.IDisposable
    {
        public const int TracerPoolSize = 16;
        public const int ImpactPoolSize = 32;
        private const float ShotsPerSecond = 10f;
        private const int MaxShotsPerFrame = 3;
        private const float Range = 200f;
        private const float TracerSeconds = 0.05f;
        private const float TracerWidth = 0.02f;
        private const float ImpactSize = 0.1f;
        private const float ImpactLift = 0.01f;   // keeps the mark in front of the surface
        private const float MuzzleHeight = 1.4f;
        // Right and forward offsets of the muzzle from the feet. Their horizontal length
        // (0.24 * sqrt 2 = 0.34 m) is inside the 0.35 m collision half-width at every yaw, and the
        // simulation keeps that box out of every wall, so the muzzle ray never starts inside a collider
        // (a ray ignores the collider its origin is in).
        private const float MuzzleRight = 0.24f;
        private const float MuzzleForward = 0.24f;

        private readonly FireRateAccumulator _rate = new FireRateAccumulator(ShotsPerSecond, MaxShotsPerFrame);
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

        // Call once per frame after the camera has moved (LateUpdate).
        public void Tick(float deltaTime, bool triggerHeld, Ray aimRay, Vector3 feet, float yaw, float now)
        {
            int shots = _rate.Consume(deltaTime, triggerHeld);
            if (shots > 0)
            {
                Vector3 muzzle = MuzzlePosition(feet, yaw);
                for (int i = 0; i < shots; i++) FireOne(aimRay, muzzle, now);
            }

            for (int i = 0; i < TracerPoolSize; i++)
            {
                if (_tracers[i].enabled && now >= _tracerHideTime[i]) _tracers[i].enabled = false;
            }
        }

        public void HideAll()
        {
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

        private void FireOne(Ray aimRay, Vector3 muzzle, float now)
        {
            // 1) Screen-centre ray: what the crosshair is on.
            Vector3 aimPoint = Physics.Raycast(aimRay, out RaycastHit aimHit, Range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? aimHit.point
                : aimRay.GetPoint(Range);

            // 2) Muzzle -> aim point: the first thing in between is where the shot lands (D13).
            Vector3 toAim = aimPoint - muzzle;
            float distance = toAim.magnitude;
            if (distance < 0.01f) return;
            Vector3 direction = toAim / distance;

            // A little past the aim point so a shot aimed at a surface registers the hit on it.
            if (Physics.Raycast(muzzle, direction, out RaycastHit hit, distance + 0.05f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
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

`Client/Assets/Scripts/Game/Crosshair.cs`:

```csharp
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // Screen-centre aim mark (D14): one Screen Space Overlay canvas built in code. It has no
    // GraphicRaycaster and its images are not raycast targets (never clicked), and nothing on it
    // changes after creation, so it costs no per-frame UI rebuild. Dispose destroys it.
    public sealed class Crosshair : System.IDisposable
    {
        private const float Gap = 5f;
        private const float Length = 8f;
        private const float Thickness = 2f;

        private readonly GameObject _root;
        private bool _visible;

        public Crosshair()
        {
            _root = new GameObject("Crosshair");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            float barOffset = Gap + Length * 0.5f;
            AddBar(new Vector2(0f, barOffset), new Vector2(Thickness, Length));
            AddBar(new Vector2(0f, -barOffset), new Vector2(Thickness, Length));
            AddBar(new Vector2(-barOffset, 0f), new Vector2(Length, Thickness));
            AddBar(new Vector2(barOffset, 0f), new Vector2(Length, Thickness));
            AddBar(Vector2.zero, new Vector2(Thickness, Thickness));

            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        private void AddBar(Vector2 offset, Vector2 size)
        {
            var bar = new GameObject("Bar");
            var rect = bar.AddComponent<RectTransform>();
            rect.SetParent(_root.transform, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;

            var image = bar.AddComponent<Image>();   // no sprite: draws a solid rectangle
            image.color = new Color(1f, 1f, 1f, 0.9f);
            image.raycastTarget = false;
        }
    }
}
```

`Client/Assets/Scripts/ProjectH.Client.asmdef`의 `references`를 다음으로 바꾼다(UGUI 패키지의 런타임 asmdef 이름이 `UnityEngine.UI`다: `Library/PackageCache/com.unity.ugui@*/Runtime/UGUI/UnityEngine.UI.asmdef`).

```json
  "references": [
    "ProjectH.Shared",
    "Unity.InputSystem",
    "UnityEngine.UI"
  ],
```

- [ ] **Step 7: 입력, 좌클릭 규칙(D12), GameClient 연결**

`Client/Assets/Scripts/Input/InputReader.cs` 전체:

```csharp
using System;
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
            _unlockCursor = new InputAction("UnlockCursor", InputActionType.Button, "<Keyboard>/escape");

            _move.Enable();
            _look.Enable();
            _jump.Enable();
            _sprint.Enable();
            _fire.Enable();
            _aim.Enable();
            _unlockCursor.Enable();
        }

        public Vector2 Move => _move.ReadValue<Vector2>();
        public Vector2 LookDelta => _look.ReadValue<Vector2>();
        public bool Sprint => _sprint.IsPressed();
        public bool FirePressed => _fire.WasPressedThisFrame();
        public bool FireHeld => _fire.IsPressed();
        public bool AimHeld => _aim.IsPressed();
        public bool UnlockCursorPressed => _unlockCursor.WasPressedThisFrame();

        // Set when Jump is pressed, cleared by the simulation step that uses it. Rendering runs faster
        // than the fixed simulation, so a press between two steps must be remembered, not lost.
        public bool JumpQueued { get; set; }

        // Call once per rendered frame.
        public void Update()
        {
            if (_jump.WasPressedThisFrame()) JumpQueued = true;
        }

        public void Dispose()
        {
            _move.Dispose();
            _look.Dispose();
            _jump.Dispose();
            _sprint.Dispose();
            _fire.Dispose();
            _aim.Dispose();
            _unlockCursor.Dispose();
        }
    }
}
```

`Client/Assets/Scripts/Game/GameClient.cs` 전체. Phase 0 동작(스텝이 있을 때만 전송, 예측기 생성 시 `JumpQueued = false`, Snapshot 비유한 값 무시는 `Reconcile` 안, Join 후에만 커서 잠금)을 그대로 유지한다.

```csharp
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

        private GameObject _world;
        private Material _worldMaterial;
        private InputReader _input;
        private ShoulderCamera _camera;
        private Crosshair _crosshair;
        private LocalFireEffects _fireEffects;
        private NetClient _net;
        private LocalPlayerPredictor _predictor;
        private Transform _localView;
        private readonly RemotePlayers _remotePlayers = new RemotePlayers();
        private ServerClock _clock;
        private int _simHz;
        private double _interpolationDelaySeconds;
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
            _fireEffects = new LocalFireEffects();

            _net = new NetClient();
            _net.Joined += OnJoined;
            _net.SpawnReceived += OnSpawned;
            _net.DespawnReceived += OnDespawned;
            _net.SnapshotReceived += OnSnapshot;
            _net.Disconnected += OnDisconnected;
        }

        private void Update()
        {
            _net.Poll();
            _input.Update();
            UpdateCursorAndButtons();

            if (_clock != null && _clock.IsReady)
                _remotePlayers.Render(_clock.RenderTick(Time.unscaledTimeAsDouble, _interpolationDelaySeconds));

            if (_predictor == null) return;

            _camera.ApplyLook(_input.LookDelta, _aiming);
            bool jump = _input.JumpQueued;
            int steps = _predictor.Advance(Time.deltaTime, _input.Move, _camera.Yaw, _input.Sprint, ref jump);
            _input.JumpQueued = jump;
            if (steps > 0 && _predictor.TryBuildInputPacket(out PlayerInputPacket packet)) _net.SendInput(packet);

            _localView.SetPositionAndRotation(_predictor.RenderPosition + Vector3.up, Quaternion.Euler(0f, _predictor.RenderYaw, 0f));
        }

        private void LateUpdate()
        {
            if (_predictor == null) return;
            _camera.Follow(_predictor.RenderPosition, _aiming, Time.deltaTime);
            _crosshair.SetVisible(true);
            // After the camera moved, so the shot goes where the crosshair is this frame.
            _fireEffects.Tick(Time.deltaTime, _fireHeld, _camera.AimRay, _predictor.RenderPosition, _camera.Yaw, Time.time);
        }

        private void OnDestroy()
        {
            _net.Joined -= OnJoined;
            _net.SpawnReceived -= OnSpawned;
            _net.DespawnReceived -= OnDespawned;
            _net.SnapshotReceived -= OnSnapshot;
            _net.Disconnected -= OnDisconnected;
            _net.Dispose();
            ClearMatchState();
            _fireEffects.Dispose();
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

        private void OnSpawned(PlayerSpawned spawned)
        {
            if (spawned.EntityId == MyEntityId)
            {
                if (_predictor != null) return;
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw });
                // A Space pressed while waiting for the spawn must not fire a jump on the first step.
                _input.JumpQueued = false;
                _localView = PlayerViewFactory.Create($"Player {spawned.EntityId} (you)", true);
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
            _remotePlayers.Clear();
            _clock = null;
            _crosshair.SetVisible(false);
            _fireEffects.HideAll();
        }
    }
}
```

`Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs`:

```csharp
            GUILayout.Label("F1: panel   Left click: lock mouse   Esc: unlock");
```
→
```csharp
            GUILayout.Label("F1: panel   Left click: lock mouse, then fire   Right click: aim   Esc: unlock");
```

- [ ] **Step 8: 스크래치 컴파일과 Editor 확인**

`UnityCompile.csproj`에 `UnityEngine.UI` 참조가 있는지 먼저 확인한다(없으면 `Client/Assembly-CSharp.csproj`의 HintPath `Library\ScriptAssemblies\UnityEngine.UI.dll`을 참고해 아래 줄을 Unity 참조 ItemGroup에 추가).

```bash
grep -n "UnityEngine.UI" "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"
```
```xml
    <Reference Include="UnityEngine.UI"><HintPath>E:/popol/ProjectH/Client/Library/ScriptAssemblies/UnityEngine.UI.dll</HintPath><Private>False</Private></Reference>
```

Run: `dotnet build "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj"`
Expected: 경고 0, 오류 0

Run: `dotnet test "C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/predictortests/PredictorTests.csproj"`
Expected: 전체 PASS

Editor.log: Step 0의 `N6` 이후만 본다. 사용자가 Editor를 포커스해 import가 끝난 뒤

```bash
LOG="$LOCALAPPDATA/Unity/Editor/Editor.log"; tail -n +N6 "$LOG" | grep -n "error CS"
```

Expected: 출력 없음(asmdef 참조 누락이면 `UnityEngine.UI` 네임스페이스 오류가 여기 나온다)

- [ ] **Step 9: 사용자 수동 확인 요청**

- 접속 전 패널에서 클릭해도 커서가 잠기지 않는다. 접속 후 첫 좌클릭은 커서만 잠그고 발사하지 않는다(버튼을 떼고 다시 눌러야 발사).
- 화면 중앙에 조준점이 보인다. 좌클릭을 누르고 있으면 초당 약 10발, 어깨 앞에서 조준점 방향으로 짧은 궤적과 탄착 표시가 나온다.
- 조준점은 벽 너머를 가리키지만 캐릭터와 조준점 사이에 기둥이 있으면 탄착이 기둥에 찍힌다(D13).
- 오래 연사해도 탄착 표시는 최대 32개이고 오래된 것부터 다른 위치로 옮겨진다. Hierarchy의 `LocalFireEffects` 자식 수가 48개로 일정하다.
- Esc로 커서를 풀면 발사·조준이 멈춘다. Disconnect 시 조준점·궤적·탄착이 사라진다. Play 종료 시 `LocalFireEffects`, `Crosshair`, `TestWorld` 오브젝트가 남지 않는다.
- (선택) Profiler에서 연사 중 GC Alloc이 프레임마다 0인지 확인한다.

- [ ] **Step 10: 체크포인트**

결과를 기록한다. 커밋하지 않는다.

---

### Task 7: 문서 갱신

**Files:**
- Modify: `Docs/Networking.md`, `Docs/Client.md`, `Docs/Server.md`, `Docs/Architecture.md`

**Interfaces:**
- Consumes: Task 1–6의 동작
- Produces: 없음

- [ ] **Step 1: `Docs/Networking.md`**

4번째 줄 `` `ProtocolVersion`(현재 1) 불일치 연결은`` 을 `` `ProtocolVersion`(현재 2. Phase 1에서 박스 충돌로 이동 결과가 바뀌어 올렸다. 패킷 형식은 Phase 0과 같다) 불일치 연결은`` 으로 바꾼다.

`## Validation (서버)` 제목 바로 앞에 다음 절을 추가한다.

```markdown
## 이동 충돌 (Phase 1)

서버(`Match.Tick`)와 예측(`LocalPlayerPredictor`)은 같은 `MovementSimulation.Step(ref state, input, dt, TestArena.Boxes)`를 호출한다. 지형은 Shared 코드 상수 `TestArena`(축 정렬 박스 20개 + y=0 바닥)이고, 캐릭터는 AABB(반폭 0.35 m, 높이 1.8 m)다.

Step 순서:
1. 입력 검증(비유한 값 0, 이동 길이 ≤ 1), Yaw·수평 속도 계산.
2. 박스와 `Skin`(0.001 m)보다 깊게 겹치면 가장 적게 겹친 축으로 밀어낸다(동률은 -X, +X, -Z, +Z, +Y, -Y 순. 아래로는 바닥 위일 때만).
3. 접지 판정은 상태 없이 매 Step: `VelocityY ≤ 0`이고 발밑 ±0.02 m 안에 바닥이나 박스 윗면이 있으면 Y를 그 면에 정확히 맞춘다. 접지면 `VelocityY = Jump ? 7 : 0`, 아니면 중력.
4. X → Z → Y 축 분리 Sweep. 각 축에서 나머지 두 축이 겹치는 박스 중 진행 방향 가장 가까운 면까지(Skin만큼 띄움) 이동한다. 거리에 상관없이 모든 앞쪽 박스를 보므로 빠른 낙하도 판을 뚫지 않는다. Y가 막히면 `VelocityY = 0`.

접지 여부를 상태에 저장하지 않으므로 Snapshot 형식은 그대로다. 박스 위에 서 있으면 Y가 윗면 값으로 고정되어 예측과 서버가 같은 값을 내고, 재조정 떨림이 없다. 서버 보정으로 예측이 박스 안에 들어가도 재적용 첫 Step이 밀어낸다. 점프 최고점은 30 Hz 이산 적분으로 약 1.34 m라 1 m 박스는 오르고 1.5 m 박스는 못 오른다.
```

`## Validation (서버)`의 첫 bullet 끝에 ` 시작 위치가 박스와 겹치면 밀어낸 뒤 이동한다.`를 덧붙인다.

- [ ] **Step 2: `Docs/Client.md`**

구조 표에서 다음 행들을 바꾸거나 추가한다.

```markdown
| `Bootstrap/TestWorld` | 100×100m 바닥, 조명, Shared `TestArena` 박스마다 Cube(BoxCollider, 공유 Material 1개). Collider는 카메라 충돌·발사 광선용이고 이동 충돌은 `MovementSimulation`이 한다 (Phase 6에서 교체) |
| `Bootstrap/DevConnectPanel`, `LaunchArgs` | 개발용 접속 UI(IMGUI), 실행 인자 |
| `Input/InputReader` | Input System 격리. Move, Look, Jump, Sprint, Fire(좌클릭), Aim(우클릭), Esc |
| `Game/LocalPlayerPredictor` | 예측·재조정. `TestArena.Boxes`와 충돌(서버와 같은 박스) |
| `Game/Crosshair` | 코드로 만든 Screen Space Overlay Canvas 조준점(UGUI, GraphicRaycaster 없음) |
| `Game/LocalFireEffects`, `FireRateAccumulator`, `RingCursor` | Client 전용 발사 연출: 초당 10발(프레임당 최대 3발), 두 번 광선(화면 중앙 → 조준점, 총구 → 조준점), 궤적 16·탄착 32 고정 링 풀 |
| `Camera/ShoulderCamera`, `ShoulderCameraMath` | 오른쪽 어깨 카메라, 우클릭 ADS(거리 3.5→1.6 m, 오른쪽 0.55→0.65 m, FOV 60→42, 감도 ×0.6), 두 단계 SphereCast(반경 0.2 m) 충돌. 계산은 `ShoulderCameraMath` 순수 함수 |
```

(기존 `Camera/ThirdPersonCamera` 행은 지운다.)

`의존성:` 문단의 `` `ProjectH.Shared`, `Unity.InputSystem`을 참조한다`` 를 `` `ProjectH.Shared`, `Unity.InputSystem`, `UnityEngine.UI`(조준점)를 참조한다`` 로 바꾼다.

`## 프레임 흐름` 목록을 다음으로 바꾼다.

```markdown
1. `NetClient.Poll` → 콜백(Joined/Spawned/Snapshot 등)이 메인 스레드에서 실행됨
2. `InputReader.Update`(점프 눌림 큐잉), 커서·버튼 처리: 커서가 풀려 있으면 좌클릭은 잠금만 하고(Join 후), 그 클릭은 버튼을 뗄 때까지 발사로 치지 않는다. 조준·발사는 커서가 잠겨 있을 때만.
3. 원격 플레이어 렌더(`ServerClock.RenderTick`로 보간 대상 Tick 계산)
4. 카메라 Look(ADS 중 감도 ×0.6) → `LocalPlayerPredictor.Advance`(고정 스텝 예측) → 스텝이 있었다면 `PlayerInput` 전송 → 로컬 뷰 위치 갱신
5. `LateUpdate`: 카메라 Follow(머리 기준점 → 어깨점 → 카메라 두 번 SphereCast, 막히면 즉시 당기고 풀리면 감쇠 복귀) → 조준점 표시 → 발사 연출(카메라가 움직인 뒤라 조준점과 일치)
```

`## Lifetime` 첫 문단을 다음으로 바꾼다.

```markdown
생성 순서: 월드(+박스 Material) → InputReader → ShoulderCamera → Crosshair → LocalFireEffects → NetClient. `GameClient.OnDestroy`는 역순으로 해제한다: 이벤트 구독 해제 → NetClient Dispose(`NetManager.Stop`) → 매치 상태(예측기·로컬 뷰·원격 뷰·ServerClock, 조준점 숨김, 발사 연출 숨김) → LocalFireEffects Dispose(풀 GameObject·Material) → Crosshair Dispose(Canvas) → InputAction Dispose → 플레이어 공유 Material → 월드·박스 Material 파괴. 연결이 끊기면(`OnDisconnected`) 매치 상태를 지운다.
```

같은 절 두 번째 문단 끝에 ` 발사 연출은 궤적 16·탄착 32개를 생성자에서 한 번 만들고 `RingCursor`로 오래된 것부터 재사용하므로 늘어나지 않는다. 발사·카메라의 Physics 호출은 단일 결과 버전만 쓴다.`를 덧붙인다.

`## 실행과 두 Client 확인`의 4번을 다음으로 바꾼다.

```markdown
4. 조작: 좌클릭(커서 잠금, 잠긴 뒤 누르고 있으면 발사), 우클릭(누르는 동안 조준), WASD, Shift(달리기), Space(점프), Esc(해제), F1(패널)
```

`## 자동 검사` 첫 줄을 다음으로 바꾼다.

```markdown
EditMode 테스트: `Assets/Tests/EditMode`(`LocalPlayerPredictorTests`, `ArenaPredictionTests`, `RemotePlayerInterpolatorTests`, `ShoulderCameraMathTests`, `FireRateAccumulatorTests`). 이 테스트들은 Physics·GameObject·네이티브 Quaternion 함수를 쓰지 않으므로 Unity 밖 NUnit 프로젝트로도 돌릴 수 있다.
```

- [ ] **Step 3: `Docs/Server.md`**

`Tick 루프: ...` 문단 다음에 추가:

```markdown
`Match.Tick`은 플레이어마다 `MovementSimulation.Step(ref state, input, 1/SimHz, TestArena.Boxes)`를 호출한다(Shared 지형 박스와 충돌. 규칙은 `Networking.md` "이동 충돌"). 박스 20개 × 50명 × 30 Hz라 비용은 무시할 수준이고 할당이 없다. Spawn은 반경 5 m 원 위이고 아레나는 중앙 반경 7 m를 비워 둔다(`TestArenaTests`).
```

- [ ] **Step 4: `Docs/Architecture.md`**

3번째 줄 `Phase 0 + 네트워크 이동 동기화 기준. 설계 근거: ...` 를 다음으로 바꾼다.

```markdown
Phase 1 Character Prototype 기준. 설계 근거: `Docs/specs/2026-09-30-phase0-network-sync-design.md`, `Docs/specs/2026-09-30-phase1-character-prototype-design.md`.
```

mermaid의 `Sim[Simulation: MovementSimulation]` 를 `Sim[Simulation: MovementSimulation, TestArena]` 로, Client subgraph에 `Input --> Camera[ShoulderCamera]` 한 줄을 추가한다.

폴더 표의 Shared 행을 다음으로 바꾼다.

```markdown
| `Shared/` | 패킷 DTO, 프로토콜 상수, 이동 계산과 그 지형 박스·충돌(`Simulation/`, 유일한 로직 예외: `game-core-rules` 4절) |
```

Client 행 끝에 ` 카메라·조준점·발사 연출은 Client 표시 전용이다(서버로 가지 않음)` 를 덧붙인다.

- [ ] **Step 5: 체크포인트**

네 문서의 변경을 다시 읽고, 코드의 상수(0.35 / 1.8 / 0.001 / 0.02, 카메라 수치, 풀 크기, ProtocolVersion 2)와 일치하는지 확인해 기록한다. `dotnet test Server/ProjectH.Server.slnx`를 마지막으로 한 번 더 실행해 PASS를 기록한다. 커밋하지 않는다.

---

## Self-Review 결과

- **Spec 대응:** §1 Box/TestArena/MoveSettings/Step 순서 → Task 1–3. §2 Server(Match, ProtocolVersion 2, Snapshot 그대로) → Task 3. §3 Client 표의 모든 파일 → Task 4(TestWorld, Predictor), Task 5(ShoulderCamera, InputReader Aim), Task 6(InputReader Fire, Crosshair, LocalFireEffects, GameClient). Lifetime·Hot Path → Task 6 코드와 Task 7 문서. §4 하네스 → Task 1. §5 테스트: Shared 충돌 11항목 → Task 2(빈 지형·벽 멈춤·대각선·1 m·1.5 m·서 있기·천장·빠른 낙하·밀어내기·결정성·NaN), TestArena 3항목 + 틈·아레나 결정성 → Task 3, Server → Task 3, Client EditMode 3항목 → Task 4·5·6, Unity 확인 → Task 4·5·6의 Editor.log·수동 단계. D15(넣지 않는 것)는 어느 Task에도 없다.
- **스크래치 사전 검증:** 이 계획의 Shared 코드와 `CollisionTests`·`TestArenaTests`(Spawn 함수는 같은 식의 복사본)·이전 `MovementSimulationTests`는 netstandard2.1 / C# 9 라이브러리 + net10 xUnit으로, Client 순수 코드와 EditMode 테스트 전부는 `UnityEngine.CoreModule`만 참조한 NUnit으로 실행해 모두 통과했다. Client 전체 스크립트는 Unity DLL 대상 컴파일에서 경고·오류 0이었다. 저장소의 `Server/`·`Shared/` 복사본에 Task 1–3 변경을 적용한 뒤 `dotnet test`로 실제 서버 테스트 전체(기존 `MatchTests`·통합 테스트 포함, 94개)가 통과했다.
- **타입 일관성:** `Step(..., ReadOnlySpan<Box> world)`, `OverlapsAny(Vector3, ReadOnlySpan<Box>)`, `IsGrounded(in MoveState, ReadOnlySpan<Box>)`, `TestArena.Boxes/ClearRadius`, `Match.SpawnPosition(ushort)`, `TestWorld.Build(out Material)`, `ShoulderCamera(Camera)/ApplyLook(Vector2,bool)/Follow(Vector3,bool,float)/AimRay/Yaw`, `LocalFireEffects.Tick(float,bool,Ray,Vector3,float,float)/HideAll/Dispose`, `Crosshair.SetVisible/Dispose`, `InputReader.FirePressed/FireHeld/AimHeld`가 정의한 Task와 사용하는 Task에서 같다.
