# Phase 12 Deployment & Traversal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 지금 배틀로얄의 플레이 감각을 빠르고 유동적인 3인칭 배틀로얄로 바꾼다.
- 정식 경기는 `수송기 → 뛰어내리기 → 자유 낙하 → 글라이더 → 착지 → 지상` 순서로 시작한다. 개발 모드(`DevRespawn`)는 지금처럼 바로 Spawn한다.
- 지상 이동이 늘어난다.
  - 기력과 달리기, 웅크리기(C 토글, Ctrl 누름), 슬라이드
  - Mantle·Hurdle, 공중 관성과 달리며 점프, 낙하 피해
- 문 5개가 생긴다. E로 열고 닫고, 달리거나 미끄러지며 부딪히면 열린다.
- 모든 판정은 서버가 한다. Client는 같은 Shared 이동 코드로 예측하고 보정한다. 이동마다 따로 만든 네트워크 시스템은 없다.
- Entity Snapshot은 13 B 그대로다. 헤더는 19 B에서 27 B가 되고, 패킷은 최대 1197 B다.
- 봇 50명이 경기 시작부터 착지·전투까지 진행한다.

**Architecture:**
- **Shared Simulation(`Shared/Runtime/Simulation`).**
  - `MoveState`에 `Mode`(`MovementMode`), `HorizontalVelocity`, `EnergySpent`(1/100 단위), `EnergyDelayTicks`, `ModeTicks`, `Exhausted`를 더한다.
  - `MovementSimulation.Step`은 모드로 분기한다.
    - `StepGround`: 지상·웅크리기·슬라이드·점프·낙하·공중 관성·기력
    - `StepVault`: 일정한 속도, 충돌 없음
    - `StepAir`: 자유 낙하·글라이드
    - `Transport`: 시점만 바꾼다
  - `Step`은 `StepResult`(착지 속도, 막은 상자, 달리는 중, 돌진 중)를 돌려준다.
  - 수치는 모두 `MovementTuning`에 있다.
  - 지형: `HeightField.Gradient`. 문 상자: `GameMap.Doors`.
  - 수송기: 경로 `DropRoute`(위치, 뛰어내리기 구간)와 탑승 `DropTransport.Ride`
- **Protocol v10(Shared).**
  - Entity `Flags` 비트: bit1–3은 모드, bit4는 달리기, bit5는 기진.
  - `SnapshotSelf` 14 B: 기력, 수평 속도, `ModeTicks`, `EnergyDelayTicks`
  - 새 패킷: `TransportRoute` 24(29 B), `DoorStates` 25(2 B)
  - 바뀌는 패킷: `PlayerRespawned.Mode`, `PlayerDied.Cause`(`DeathCause`)
  - 새 입력 버튼: `Crouch` 2048
- **Server.**
  - `DropPlanner`가 시드로 경로를 정한다(Shared가 아니다. game-core-rules §4).
  - `Match`
    - 경기 시작 때 경로를 먼저 보내고 모두를 `Transport`로 둔다.
    - Tick마다 탑승자는 `Ride`, 나머지는 `Move`(`Step`, 이동 이상 검사, 문 밀치기, 낙하 피해)를 거친다.
    - 모드에 따라 행동을 막는다.
    - 위치 기록에 모드를 남긴다. 맞는 상자 높이와 눈높이가 모드를 따른다.
    - 문(`DoorSet`, `DoorRules`): E, `DoorStates`, 라운드 시작에 모두 닫기
    - 자기장 시계는 경로 끝에 시작한다.
  - `ServerOptions.AirDrop`(기본 켬)
  - Health 줄과 Meter에 `movementAnomalies`
- **봇.**
  - 경로와 자기 모드를 안다.
  - 착지 목표: POI 또는 받은 아이템
  - 가장 가까이 지나는 Tick에 뛰어내리고, 낙하 중에는 목표 쪽으로 간다.
  - 막히면 달린다(문 밀치기, Hurdle).
- **Client.**
  - 예측(`LocalPlayerPredictor`)
    - `MoveState` 전체를 보정한다.
    - 탑승 중에는 `Snapshot Tick - Ack + Seq`의 Tick으로 경로를 탄다.
    - 문 예측은 `PredictedDoors`, 문 규칙 복사본은 `DoorRule`
  - 입력: C·Ctrl
  - 표현
    - 자세: `PlayerPose`, `PlayerView`
    - 수송기 `TransportView`, 문 `DoorViews`
    - 모드별 카메라 목표와 보간
    - 기력 막대와 안내 문구, F1 이동 줄, 원인 "낙하"

**Tech Stack:** Unity 6000.3.24f1(UGUI Legacy `Text`·`Image`, Input System 1.20.0), .NET 10, C# 9(Shared netstandard2.1, Unity Client), LiteNetLib 2.1.4, xUnit, NUnit(EditMode). 새 패키지는 없다.

**Spec:** `Docs/specs/2026-10-02-phase12-deployment-traversal-design.md`

## Global Constraints

- **Commit:** 작업 Branch(`phase12-traversal`)에서 Task마다 Commit한다.
  - 각 Task의 **Files**에 적힌 경로만 `git add`한다. `git add -A`는 쓰지 않는다.
  - Push는 Phase가 끝난 뒤 `github-push` 스킬로 한다. Force Push는 하지 않는다.
  - `.claude/settings.json`과 `.superpowers`는 Stage하지 않는다.
- **`.meta` 파일은 만들지 않는다.** Unity가 Editor를 열 때 만든다. 이 Phase에서 생기는 것은 다음 11개다. "Phase 완료 확인" 8에서 함께 커밋한다.
  - `Shared/Runtime/Simulation/`: `MovementMode.cs.meta`, `MovementTuning.cs.meta`, `DropTransport.cs.meta`
  - `Shared/Runtime/Protocol/`: `TraversalPackets.cs.meta`
  - `Client/Assets/Scripts/Game/`: `DoorRule.cs.meta`, `PredictedDoors.cs.meta`, `PlayerPose.cs.meta`, `TransportView.cs.meta`, `DoorViews.cs.meta`
  - `Client/Assets/Tests/EditMode/`: `MovementPredictionTests.cs.meta`, `PlayerPoseTests.cs.meta`
- **코드 규칙:** 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다.
  - **새 Lock은 없다.** 문 상태·수송기 경로·이동 이상 카운트는 Game Loop 스레드만 쓴다. `HealthCounters.AddMovementAnomaly`는 `Interlocked`다. Client 코드는 모두 Unity 메인 스레드다.
  - **Collection 상한:** 새 Collection은 모두 고정 크기 배열이다.
    - `DoorSet`과 `PredictedDoors`의 충돌 세계: 상자 수 + 문 5개
    - 문 예측 시각: 5칸
    - 늘어나는 Collection은 없다.
  - **매 Tick·매 프레임 할당이 없다.**
    - 충돌 세계는 문이 바뀔 때만 다시 채운다.
    - Vault 판정은 Jump를 누른 Tick에만 상자를 한 번 훑는다.
    - 자유 낙하의 지면 거리는 상자 63개를 한 번 훑는다.
    - HUD 문구는 상수 문자열이다. F1 이동 줄은 보일 때만, 바뀐 숫자가 있을 때만, 1초에 10번까지 만든다.
  - **Shared(`Shared/Runtime`)는 netstandard2.1, C# 9다.** `record`·`init`이 없고, UnityEngine과 LiteNetLib을 쓰지 않는다.
    - Simulation에는 이동·충돌 계산과 그 상수만 둔다(§4).
    - 시드로 경로를 정하는 것은 서버 `DropPlanner`다.
    - 어느 문에 E가 닿는지 정하는 규칙은 서버 `DoorRules`다. Client 복사본은 `DoorRule`이다.
  - **소스 링크하는 Client 파일(`Game/DoorRule.cs`)은 UnityEngine을 쓰지 않는다.** 서버 테스트 프로젝트가 서버 규칙과 비교한다(`ZoneMath`와 같은 방식).
- **수치(spec):**
  - `ProtocolVersion` 10, `PacketId.TransportRoute` 24, `PacketId.DoorStates` 25, `InputButtons.Crouch` 2048
  - 크기: Entity 13 B, `SnapshotSelf` 14 B, 헤더 27 B, 90명 패킷 1197 B, `TransportRoute` 29 B, `DoorStates` 2 B, `PlayerRespawned` 20 B, `PlayerDied` 7 B
  - 기력
    - 최대 100, 소모 초당 20(Tick당 67/100), 회복 초당 25(Tick당 83/100)
    - 회복 대기 1초, 다시 달리기 20
  - 웅크리기 1.2 m·2.5 m/s
  - 슬라이드: 시작 ≥ 6 m/s에서 max(속도, 9), 마찰 5, 끝 3, 내리막 × 20 × 0.6, 최대 13
  - 달리며 점프 × 1.1, 공중 조작 초당 12
  - Vault
    - 앞 0.8 m, 바닥 허용 0.3 m
    - Hurdle: 0.5–1.1 m, 6 m/s 이상, 0.2초, 깊이 2.2 m까지, 착지 틈 0.1 m
    - Mantle: –2.1 m, 0.4초, 가장자리 안쪽 0.4 m
  - 자유 낙하: 종단 30, 가속 20, 앞 15 / 옆 10 / 뒤 6
  - 글라이더: 30 m 자동, 하강 5, 가속 10, 앞 14 / 옆 10 / 뒤 4
  - 수송기: 90 m, 20 m/s, 밖 20 m, 뛰어내리기 구간은 벽 안쪽 10 m
  - 낙하 피해: 13–30 m/s → 0–100
  - 문
    - 폭 1.5 m, 두께 0.2 m, 높이 3 m
    - E는 2.5 m·±60° 안
    - 예측은 1초 유지
  - 웅크린 눈높이 1.0 m
  - 이동 이상 여유 × 1.5
  - 카메라
    - 달리기 FOV 66, 웅크리기 기준 1.1 m
    - 낙하·글라이드 거리 6 m·FOV 70, 수송기 거리 12 m
    - 보간 날카로움 5
- **범위:** spec D17의 항목은 하지 않는다. 건설·채집, 탈것, 수영, 벽 달리기, DBNO, 지도 UI, 새 애니메이션 에셋이 그 예다. Scene과 Prefab은 만들거나 고치지 않는다.
- **Docker:** 띄우거나 내리지 않는다. DB 테스트는 `PROJECTH_TEST_MYSQL`이 있을 때만 돈다.
- **부하 측정 프로세스:** 7790 같은 빈 포트를 쓴다. 자기가 띄운 프로세스는 pid로만 끈다. 이름으로 끄지 않는다(`taskkill /IM`, `pkill -f` 금지).
- **명령 실행 위치:** 저장소 루트(`E:/popol/ProjectH`)에서 실행한다.
  - 시작 기준은 서버 테스트 898개다(889 통과, MySQL 9개 건너뜀).
  - 이 계획의 코드는 계획 단계에서 스크래치 복사본에 Task 순서대로 그대로 적용했다. 그 상태에서 Task마다 다음을 확인했다.
    - 빌드 경고 0
    - 아래 테스트 수
    - `UnityCompile` 경고·오류 0
    - `EditTests` 테스트 수
  - `WorldItemsTests.AddSearchRemove_AllocateNothing`은 가끔 혼자 실패하는 테스트다(할당 측정). 그것 하나만 실패하면 한 번 더 돌린다. 다시 실패하면 보고한다.
- **스크립트와 수정 방식:** 코드는 아래 표시대로 넣는다.
  - **새 파일**: 내용 그대로 만든다.
  - **파일 전체를 바꾼다**: 내용 그대로 덮어쓴다.
  - **수정**: "변경 전" 블록이 파일에 정확히 한 번 나와야 "변경 후"로 바꾼다. 한 파일의 여러 수정은 적힌 순서대로 한다. 파일의 줄바꿈(CRLF/LF)은 지킨다.
  - 기준 텍스트가 없거나 두 번 이상 나오면 멈춘다. 파일을 손으로 고치지 말고, 앞 단계가 빠졌는지 같은 원인부터 확인한다.
- **Unity 확인 도구(Task 1 Step 1에서 만든다):**
  - `UnityCompile`: Client 스크립트, Shared, EditMode 테스트를 실제 Unity DLL로 컴파일한다. Phase 11 도구를 그대로 복사한다.
  - `EditTests`: Unity 밖 NUnit으로 EditMode 테스트를 돌린다. 이 Phase의 복사본은 Phase 11 범위(HUD 문구)에 다음을 더한다.
    - 예측: `LocalPlayerPredictorTests`, `MapPredictionTests`, 새 `MovementPredictionTests`
    - 카메라: `ShoulderCameraMathTests`
    - 새 자세 테스트: `PlayerPoseTests`
    - 이 코드는 Unity의 관리 코드 멤버만 쓴다(`Vector2/3`, `Mathf`, Quaternion 필드). 그래서 `UnityEngine.CoreModule.dll`을 참조해 Unity 밖에서 돈다.
  - 둘 다 저장소 밖 스크래치 폴더에 둔다. Phase 11 도구 원본은 읽기만 한다.
  - 이 Phase가 바꾸지 않는 EditMode 테스트(`AimSolverTests`, `PickupRuleTests`, `ZoneMathTests`, `WeaponStateTests` 등)는 `EditTests`에 넣지 않는다. 계획 단계에서 일회용 복사본으로 모든 EditMode 테스트 142개(이 Phase 끝 상태)를 Unity 밖에서 돌려 모두 통과했다. Editor의 Test Runner로도 돌린다("Phase 완료 확인" 4).
  - View·HUD 클래스(GameObject, Physics, `Quaternion.Euler` 같은 Native 호출)는 테스트가 없다. 컴파일은 `UnityCompile`로 확인하고, 동작은 Editor 확인 목록과 빌드 캡처로 본다.
- **Unity API 확인(계획 단계, Unity 6000.3.24f1 DLL):** 다음은 모두 컴파일 확인했다(경고·오류 0). `ProjectH.Client.asmdef`는 바꾸지 않는다.
  - `GameObject.CreatePrimitive`, `Object.DestroyImmediate`, `Quaternion.LookRotation`, `Renderer.enabled`
  - `BoxCollider.size`·`center`, `RectTransform.sizeDelta`
  - Input System 경로 `"<Keyboard>/c"`, `"<Keyboard>/leftCtrl"`

## Review Focus

- **예측과 서버가 같은 상태를 본다.**
  - Client는 서버와 같은 `Step`, 같은 `DropTransport.Ride`, 같은 충돌 세계를 쓴다. 충돌 세계는 상자 + 닫힌 문이고 순서도 서버 `DoorSet`과 같다.
  - 보정은 위치·수직 속도·수평 속도(1/256 양자화)와 정확한 값(모드·기력·`EnergyDelayTicks`·`ModeTicks`·`Exhausted`)을 모두 비교한다.
  - 기력은 1/100 정수로 저장하고 Tick 비용도 정수다. 그래서 Snapshot 값이 서버 값과 정확히 같고, 0과 20의 경계가 양쪽에서 같은 Tick에 넘어간다.
  - `MovementPredictionTests`는 서버 복제본과 예측기에 같은 입력을 넣는다. 매 2 Tick마다 실제 Snapshot을 쓰고 읽어(Entity + Self + `DoorStates`) 보정한다. 모드마다 보정이 0번이다(Task 8). 다룬 경우는 달리기와 기진·회복, 슬라이드와 슬라이드 점프, 웅크리기, Hurdle, 탑승·뛰어내리기·낙하·글라이드·착지, 문 밀치기다.
- **탑승 중 Tick 대응.** 서버는 Tick `now + 1`을 시뮬레이션하면서 탑승자를 그 Tick의 경로 위치에 둔다. Snapshot의 `ServerTick`이 그 Tick이다. 그래서 Client는 입력 Seq가 시뮬레이션될 Tick을 `ServerTick - AckInputSeq + Seq`로 안다(`LocalPlayerPredictor.PredictedTick`).
  - Ack가 0이면 최신 입력이 지금 Tick이라고 보고 한 번 맞춘다.
  - 서버 Tick당 입력 수가 흔들리면 탑승 위치에 짧은 보정이 생긴다. 탑승자는 보이지 않고 카메라가 12 m 뒤라서 거의 보이지 않는다.
- **같은 Space가 글라이더까지 펴지 않는다.** `DropTransport.Ride`가 그 Tick을 처리하면 `Step`을 부르지 않는다. 그래서 뛰어내린 Space는 다음 Tick의 `StepAir`에 들어가지 않는다(`TheJumpPress_DoesNotAlsoOpenTheGlider`).
- **모두 벽 안에 착지한다.** 경로의 시작과 끝은 벽 밖 20 m다. 그런데 뛰어내리기는 수송기가 벽 안쪽 10 m 안에 있을 때만 받는다.
  - 그 구간이 끝나면 남은 사람을 떨어뜨린다.
  - 공중 모드는 바깥벽 위치(±80 m)를 넘지 못한다.
  - 빈 입력의 유예 중 탑승자(D16)도 0°·30°·45°·200° 경로에서 벽 안에 착지한다(`ARiderWhoSendsNothing_IsDropped_AndLandsInsideTheWalls`).
- **경기 시작 패킷 순서.** `TransportRoute`를 모든 참가자에게 먼저 보낸 뒤 `PlayerRespawned(Transport)`를 보낸다. 둘 다 ReliableOrdered라 Client는 경로를 알고 나서 탑승한다. Join·Resume도 경로와 `DoorStates`를 보낸다.
- **행동 제한은 이동 뒤의 모드로 본다.** 착지한 Tick에는 바로 행동할 수 있고, Vault를 시작한 Tick에는 못 한다. 진행 중인 재장전과 회복은 계속된다. Client의 무기 예측(`WeaponState.Step`)도 같은 규칙으로 막는다(`ActionsAllowedAt`).
- **맞는 상자.** 웅크리기·슬라이드는 1.2 m다. 위치 기록에 모드가 있어 지연 보상도 그때의 높이를 쓴다.
  - 탑승자는 맞지 않고 자기장 피해도 받지 않는다. 대각선 경로의 끝은 첫 원 밖이다.
  - 웅크린 사수의 눈높이는 1.0 m다(서버 `CombatRules.EyeHeightOf`, Client `AimSolver.EyeHeightOf`).
- **Vault 중에는 충돌하지 않는다(D8).**
  - 경로는 직선이라 Hurdle 중에는 캡슐이 상자를 지나간다. 서버 위치도 상자 안을 지난다. Placeholder로 받아들인다.
  - Vault 중에는 `Depenetrate`도 하지 않는다. 그래서 보정이 Vault 중간 위치로 맞춰도 밀려나지 않는다.
  - 도착점은 표면보다 `Skin` 위다. 다음 Tick의 땅 판정이 정확한 높이에 붙인다.
- **이동 이상 검사(D12)는 `Step` 하나만 잰다.** 경로 배치와 Respawn은 재지 않는다. 모드의 최대 속도는 가장 빠른 수평 속도 + 가장 빠른 수직 속도다. 정상이면 0이어야 하며, 테스트와 계획 단계의 20봇 실행에서 0이었다.
- **Client 할당.**
  - 문 View는 `PredictedDoors.Version`이 바뀔 때만 다시 그린다.
  - 수송기 View는 Transform만 바꾼다.
  - 기력 막대는 픽셀이 바뀔 때만 크기를 바꾼다. 안내 문구는 참조가 바뀔 때만 바꾼다.

## Spec 해석

1. **달리는 중 = Shift + 이동 입력 + `Ground` 모드 + 기진 아님.** 공중에서도 달리는 중이면 기력이 준다. 기력이 0이 되는 Tick은 이미 낸 것으로 보고 그 Tick까지 달린다. 다음 Tick부터 걷는다.
2. **슬라이드는 "웅크리기를 누르는 순간"에 시작한다.** 땅 위에서 `Ground`가 웅크리기 입력을 처음 보는 Tick이다. 착지한 Tick도 포함된다. 그때 수평 속도가 6 m/s 이상이면 슬라이드, 아니면 웅크리기다. 슬라이드는 방향을 바꾸지 않는다.
3. **공중 조작의 상한 = max(그 순간 수평 속도, 걷기 속도).** 그래서 빠르게 뛰면 속도가 늘지 않고, 제자리 점프도 걷기 속도까지는 움직일 수 있다. 입력이 없으면 속도가 그대로 간다(관성).
4. **웅크리기에서 점프하려면 설 자리가 있어야 한다.** 슬라이드 점프도 같다. 설 수 있으면 `Ground`로 바뀌고 슬라이드 속도를 그대로 갖고 뛴다.
5. **C 토글은 Jump·Sprint를 누르거나 죽거나 Respawn하면 꺼진다.** Ctrl은 누르는 동안만이다. 입력 패킷에는 눌린 상태로 간다.
6. **Vault는 바라보는 방향으로, "앞으로 움직이는 중" = 앞 입력 > 0이다.**
   - 앞 0.8 m 안이란 발판(0.7 × 0.7 m)이 그 방향으로 0.8 m 안에서 상자에 닿는다는 뜻이다.
   - 상자 바닥이 발보다 0.3 m 넘게 높으면 장애물이 아니다. 예를 들어 지붕은 장애물이 아니다.
   - 발 중심을 지나는 선이 상자 자체를 지나지 않으면 Vault가 아니다(모서리 스침).
7. **자유 낙하 중 입력이 없으면 수평 속도가 초당 20씩 0으로 준다.** D16의 "곧게 떨어진다"가 이것이다. 수송기에서 뛰어내릴 때 수평 속도는 0이다.
8. **글라이더 자동 전개도 `Step` 안에서 한다.** 서버만 하는 것이 아니다. Client 예측도 같은 Tick에 편다.
9. **낙하 피해는 피해가 허용될 때만 준다(`DamageAllowed`).** 사격과 같아서 경기 전 대기실에서는 없다. 개발 모드에서는 있다. `DamageTaken`의 `FromDirection`은 0이다.
10. **문의 E 판정.**
    - 거리는 발에서 문 중심까지의 수평 거리다.
    - 발 높이는 문 바닥 1 m 아래부터 문 꼭대기까지여야 한다. 지붕 위에서는 안 된다.
    - E가 문에 닿으면 닫기가 거절돼도 아이템을 줍지 않는다.
    - 닫기는 살아 있는 누구의 상자(모드 높이)도 문과 겹치지 않을 때만 된다.
11. **`DoorStates`는 Join·Resume마다 보낸다.** 개발 모드에도 문이 있다. 문이 바뀐 Tick의 끝에 한 번 모두에게 보낸다. 경기 시작 때 닫기도 같은 경로로 간다.
12. **탑승자는 그리지 않는다.** 자기 자신도 그렇다. 카메라는 탑승자 위치(= 수송기 위치)를 12 m 뒤에서 따라간다.
13. **`PlayerSpawned`에는 모드가 없다.** 재접속한 Client의 예측기는 `Ground`로 시작한다. Ack 0 Snapshot의 모드가 다르면 서버 상태를 그대로 받는다(`AResumeInTheAir_TakesTheServersMode_BeforeAnyInputIsAcked`).
14. **봇의 Jump는 0.5초에 한 번까지만 누른다.** 뛰어내린 뒤 Snapshot이 오기 전에 한 번 더 누르면 글라이더가 일찍 펴지기 때문이다. 글라이더는 자동 전개에 맡긴다.
15. **봇은 막히면 달린다(`BotSteering.Stuck`).** 그래야 막힘 탈출 점프가 Hurdle이 되고 닫힌 문이 밀쳐 열린다(D15 "막힌 문은 달리기 밀치기로 자연히 열린다"). 목표까지 10 m 안이면 원래 걸었다.
16. **부하 측정은 두 번이다.**
    - 개발 모드: Phase 11과 같은 조건으로 비교한다.
    - 정식 경기: `MinPlayers=50`, 투입 켬, DB 끔. 경기 시작부터 착지·전투까지 본다(spec §2 부하).

## Spec과 다른 점

계획 단계의 프로토타입에서 spec과 다르게 정한 것이다. 컨트롤러가 spec에 반영한다.

1. **경로를 정하는 코드는 서버 `DropPlanner`에 둔다.** Shared `DropTransport.cs`에는 경로 구조체(`DropRoute`: 위치, 뛰어내리기 구간)와 `Ride`만 둔다. spec §1은 "경로 계산(시드 → 시작·끝)"을 Shared에 두었다. 그런데 game-core-rules §4는 Shared에 난수·규칙을 두지 않게 한다. Client는 `TransportRoute`로 결과만 받으므로 시드 계산이 필요 없다.
2. **경로 길이는 방향에 따라 200–246 m(10–12.3초)다.** "맵 중심을 지나는 무작위 방향의 직선, 시작과 끝은 맵 경계 밖 20 m"는 축 방향에서만 200 m다. 대각선에서 200 m로 자르면 끝이 맵 안(±70.7 m)에 온다. 그래서 끝을 경계를 지나는 점에서 경로를 따라 20 m 밖에 둔다.
3. **강제로 뛰어내리는 때는 경로 끝이 아니라 "뛰어내리기 구간"의 끝이다.** 구간은 수송기가 바깥벽 안쪽 10 m(±70 m) 안에 있는 Tick이다. 그 전의 Jump는 무시한다. spec대로 경로 끝(벽 밖 20 m)에서 떨어뜨리면 빈 입력으로 곧게 떨어지는 유예 중 플레이어(D16)가 4 m 바깥벽 밖에 착지해 영영 못 들어온다. 공중 모드는 바깥벽 위치를 넘지 못한다(`StepAir`의 경계).
4. **기진(`Exhausted`, 0 → 20까지 달리기 금지)을 Entity `Flags` bit5에 싣는다.** D1·D11의 필드 목록에 없다. 그런데 이 상태는 오래 남으므로, 보내지 않으면 예측이 경계마다 어긋난다. Self를 늘리지 않아 헤더 27 B, 패킷 1197 B는 spec 그대로다.
5. **`MoveState`의 기력 필드는 `EnergySpent`(쓴 양, 1/100 정수, `ushort`)다.** spec은 `Energy`다. 그렇게 하는 이유는 두 가지다.
   - 기본값이 "가득 참"이라 기존 코드와 테스트의 `new MoveState()`가 그대로 쉰 상태다.
   - Tick 비용이 정수(67·83)라서 Snapshot의 기력(×100)이 정확하다.
   - 대가로 초당 비용이 20.1·24.9가 되고, 가득 찬 기력으로 150 Tick(5.0초) 달린다. 표시용 `Energy` 속성이 있다.
6. **`ModeTicks`는 Vault에만 쓴다.** 슬라이드는 속도로 끝나므로 남은 Tick이 없다.
7. **문 E 규칙은 서버 `DoorRules`, Client 복사본은 `DoorRule`이다.** 서버 테스트가 소스 링크로 둘을 비교한다(2만 개 무작위 입력). spec은 위치를 정하지 않았다. 줍기 판정처럼 Shared에 둘 수 없는 규칙이라 `PickupRule`·`ZoneMath`와 같은 방식으로 한다.
8. **봇의 착지 목표는 POI 5곳과 봇이 받은 아이템 위치 중에서 고른다.** spec은 "POI 5곳과 Loot 지점 50곳"이다. 그런데 Loot 지점(`LootPoints`)은 서버만 읽는 데이터이고(§4), 봇은 Client다. 경기 시작 때 서버가 Loot 지점마다 아이템을 놓고 그 위치를 보내므로 결과는 같다.
9. **웅크리기·슬라이드의 눈높이를 1.0 m로 낮춘다.** 서버 사격 시작점과 Client 조준이 같이 바뀐다. D13은 맞는 상자 높이만 정했다. 그대로 두면 1.2 m로 웅크린 플레이어가 1.5 m 엄폐물 위로 1.6 m 높이에서 쏘면서 자기는 맞지 않는다.
10. **Vault 판정의 수치를 더한다.** spec에 없는 판정 세부값이다.
    - Hurdle 깊이 상한 2.2 m: 더 깊으면 윗면에 선다. Stonefield의 4 m 받침을 0.2초에 넘으면 27 m/s가 된다.
    - 착지 틈 0.1 m
    - 장애물 바닥 허용 0.3 m
    - 도착점을 `Skin`만큼 띄움
11. **탑승자는 자기장 피해를 받지 않는다.** 대각선 경로의 끝(약 ±94 m)은 첫 원(반지름 115 m) 밖이다. 자기장은 경기 시작 Tick에 1단계로 시작하고, 첫 축소는 경로 끝 Tick + 45초다. 경기 중 0단계가 생기지 않는다.
12. **`ServerOptions.AirDrop`(기본 켬)을 더한다.** 다음 두 곳이 끈다. 운영 설정에는 쓰지 않는다.
    - Phase 5–11 규칙 테스트(`RoyaleHarness` 기본값, `BattleRoyaleIntegrationTests`): 땅에서 바로 싸우는 테스트다.
    - 개발 모드: 원래 수송기가 없다.
13. **행동 제한에 슬롯 바꾸기와 버리기도 넣는다.** spec은 사격·재장전·줍기·상호작용·회복이다. `ProcessActions`를 통째로 막는 편이 단순하다. 탑승·낙하 중에는 무기가 없고, Vault는 0.2–0.4초다.
14. **Client의 문 예측은 다음 `DoorStates`가 오거나 1초가 지나면 끝난다.** spec은 "다음 `DoorStates`가 오면 덮어쓴다"다. 서버가 열지 않은 문(밀치기 실패)은 `DoorStates`가 오지 않는다. 그래서 끝나는 시각이 없으면 예측이 계속 어긋난다.
15. **`PlayerDied.Cause`의 enum은 `DeathCause { Zone = 0, Fall = 1 }`다.** 플레이어가 죽인 경우와 관전 알림도 0이다. 0 = 자기장은 `KillerId`가 0일 때만 뜻이 있다.
    - Client의 "관전 알림" 판별은 `KillerId 0 && Placement 0 && Cause == Zone`이 된다. 그래야 개발 모드의 낙하 사망이 알림으로 오인되지 않는다.
16. **안내 문구는 `CombatHud`(가운데 아래)에 둔다.** spec §1은 `MatchHud`다. 그런데 문은 개발 모드에도 있고, `MatchHud`는 경기 정보가 있을 때만 보인다. 문 안내는 "[E] 문 열기"와 "[E] 문 닫기"로 나눈다.
    - 뛰어내리기 안내는 예측 Tick이 뛰어내리기 구간 안일 때만 보인다.
    - 문 안내가 보이면 아이템 줍기 안내는 숨긴다(E는 문이 먼저다).
17. **수송기 카메라의 FOV는 70이다.** spec은 거리(12 m)만 정했다.
18. **F1에 수송기 경로 줄을 더한다**("수송기 (시작 X, Z) → (끝 X, Z)"). D14의 "F1 디버그 문구(시작·끝 좌표)"다.
19. **바뀐 기존 테스트.** 무엇이 왜 바뀌는지:
    - `MatchTests.Tick_WithoutInput_RepeatsMovement_ButNotJump`(Task 1): 공중 관성. 15 Tick 유예가 끝날 때 점프는 아직 공중이라 착지할 때까지 앞으로 간다. 그래서 착지한 뒤의 위치를 기준으로 "더 움직이지 않는다"를 본다.
    - `CollisionTests.HighBox_CannotBeClimbedByJumping`(Task 2): 1.5 m 상자는 이제 Mantle이 된다(spec §2). 그래서 Mantle 범위(2.1 m) 위인 2.5 m 상자로 "점프로는 못 오른다"를 본다.
    - `GameMapTests.LongRandomWalks_…`(Task 2): Vault 중에는 충돌 검사가 없다(D8). 그래서 Vault 모드인 Tick에는 겹침을 보지 않는다. 끝난 뒤에는 그대로 본다.
    - 패킷 크기와 번호를 고정한 테스트(Task 4): 모두 v10 값으로 바꾼다.
      - `PacketWriterReaderTests.PacketId_OutOfRange_IsRejected`: 24 → 26
      - `StatsPacketTests.Ids_AndTheReaderRange`: 24 → 26
      - `MatchPacketTests.PlayerDied_CarriesPlacement_In7Bytes`: 6 → 7 B
      - `ProtocolConstantsTests`: 헤더 27 B, Self 14 B, `ProtocolVersion_IsTen`
      - `PacketTests.SnapshotPacket_WithMaxEntities_Is1197Bytes_AndFitsOneDatagram`: 1197 B
      - `PacketTests.PlayerInput_UnknownButtonBits_AreMasked`: `Crouch` 포함 `0x0FFF`
      - `ProtocolFuzzTests`: 범위가 `DoorStates`까지, 새 Parser 둘
    - `MonitoringTests`(Task 5): Health 줄의 `movementAnomalies=0`, Meter의 `projecth.movement_anomalies`
    - `RoyaleHarness`·`BattleRoyaleIntegrationTests`(Task 5): 위 12. 둘 다 투입을 끈다. 이유는 땅에서 바로 싸우는 Phase 5 규칙 테스트이기 때문이다. 처음에 투입을 켠 채로 돌렸을 때 30개가 실패했고, 모두 이 가정 때문이었다.
    - `LocalPlayerPredictorTests`·`MapPredictionTests`(Task 8): `Reconcile`이 Self 블록과 Snapshot Tick을 받는다. 예전 두 인자 호출을 서버 상태의 Self 또는 쉰 상태 Self로 바꾼다. 검사하는 내용은 그대로다.

---

### Task 1: Shared 이동 핵심 (모드, 기력, 웅크리기, 슬라이드, 공중 관성, 착지 속도)

**Files:**
- Create: `Shared/Runtime/Simulation/MovementMode.cs`, `Shared/Runtime/Simulation/MovementTuning.cs`
- Modify: `Shared/Runtime/Simulation/MoveState.cs`, `MovementSimulation.cs`(파일 전체), `HeightField.cs`, `InputCommand.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Shared/MovementModesTests.cs`
  - Modify: `Server/tests/ProjectH.Server.Tests/Game/MatchTests.cs`(공중 관성, Spec과 다른 점 19)
- 저장소 밖: Unity 확인 도구(`<스크래치>/p12tools`)

**Interfaces:**
- Produces(Shared, namespace `ProjectH.Shared.Simulation`):
  - `enum MovementMode : byte { Ground = 0, Crouch = 1, Slide = 2, Vault = 3, Freefall = 4, Glide = 5, Transport = 6 }`
  - `MoveState`의 새 필드와 속성
    - 필드: `Mode`, `Vector2 HorizontalVelocity`(X·Z), `ushort EnergySpent`(1/100), `byte EnergyDelayTicks`, `byte ModeTicks`, `bool Exhausted`
    - 속성: `float Energy`, 상수 `MaxEnergyHundredths` 10000
  - `struct StepResult { float LandingSpeed; int BlockedBy; bool Sprinting; bool Charging; }`
  - `MovementSimulation`
    - `Step(ref MoveState, in InputCommand, float, ReadOnlySpan<Box>, HeightField, out StepResult)`(기존 오버로드는 남는다)
    - `CollisionHeight(MovementMode)`, `CanStand(Vector3, ReadOnlySpan<Box>)`, `OverlapsAny(Vector3, float height, ReadOnlySpan<Box>)`
  - `HeightField.Gradient(float x, float z)` → `Vector2`(dh/dx, dh/dz)
  - `InputButtons.Crouch` 2048
  - `MovementTuning`: 기력·웅크리기·슬라이드·공중·Vault·낙하 피해 수치. Vault 수치는 Task 2가 쓰고, 낙하 피해 수치는 Task 5가 쓴다.

`Step`은 아직 지상 모드(`StepGround`)만 있다. Task 2가 Vault, Task 3이 공중 모드를 더한다. Server·Client는 기존 `Step` 오버로드를 쓰므로 이 Task에서 고칠 곳이 없다. Server에는 Task 5, Client에는 Task 8에서 고친다.

- [ ] **Step 1: Unity 확인 도구를 만든다(저장소 밖, 한 번만)**

아래 경로의 `<스크래치>`는 저장소 밖 아무 폴더다(예: `%TEMP%/projecth-p12`).

1. `UnityCompile`: Phase 11 도구를 복사한다. 원본은 고치지 않는다.

   ```bash
   mkdir -p <스크래치>/p12tools/uc <스크래치>/p12tools/edittests
   cp C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/eec88c12-a24f-409c-b8be-9064c2a69779/scratchpad/p11tools/uc/UnityCompile.csproj <스크래치>/p12tools/uc/
   ```

   원본이 없어졌으면 Phase 11 계획(`Docs/plans/2026-10-01-phase11-game-ui.md`) Task 3 Step 1의 `make_unitycompile.py`로 다시 만든다. 저장소 경로는 `RepoRoot` 속성으로 받는다(기본 `E:/popol/ProjectH`).

2. `EditTests`: 아래 내용을 `<스크래치>/p12tools/edittests/EditTests.csproj`로 저장한다. 뒤 Task가 만드는 파일은 `Condition="Exists(...)"`로 넣어 두어, 처음부터 끝까지 같은 파일을 쓴다.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Phase 12 copy of the Phase 11 EditTests tool. Runs EditMode tests outside Unity (NUnit) against RepoRoot's sources:
       the pure HUD text classes (Phase 11) and the prediction / camera math, which use only managed UnityEngine members
       (Vector2/3, Mathf, Quaternion fields), compiled against Unity's CoreModule. Tests that need the Unity runtime
       (Physics, GameObject, native calls) are not listed. -->
  <PropertyGroup>
    <RepoRoot Condition="'$(RepoRoot)' == ''">E:/popol/ProjectH</RepoRoot>
    <UnityManaged>C:/Program Files/Unity/Hub/Editor/6000.3.24f1/Editor/Data/Managed/UnityEngine</UnityManaged>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NoWarn>$(NoWarn);CS0436</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NUnit" Version="3.14.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
    <PackageReference Include="LiteNetLib" Version="2.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="UnityEngine.CoreModule"><HintPath>$(UnityManaged)/UnityEngine.CoreModule.dll</HintPath></Reference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="$(RepoRoot)/Server/src/ProjectH.Shared/ProjectH.Shared.csproj" />
    <!-- Phase 11: HUD texts. -->
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/MatchHudTextTests.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/InventoryHudTextTests.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/MatchHudText.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/InventoryHudText.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/WeaponState.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/ZoneMath.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/UI/UiText.cs" />
    <!-- Phase 12: prediction and camera math. -->
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/MapPredictionTests.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/ShoulderCameraMathTests.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/MovementPredictionTests.cs" Condition="Exists('$(RepoRoot)/Client/Assets/Tests/EditMode/MovementPredictionTests.cs')" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/LocalPlayerPredictor.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/AimSolver.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/PredictedDoors.cs" Condition="Exists('$(RepoRoot)/Client/Assets/Scripts/Game/PredictedDoors.cs')" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/DoorRule.cs" Condition="Exists('$(RepoRoot)/Client/Assets/Scripts/Game/DoorRule.cs')" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Net/VectorConversions.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Camera/ShoulderCameraMath.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/PlayerPoseTests.cs" Condition="Exists('$(RepoRoot)/Client/Assets/Tests/EditMode/PlayerPoseTests.cs')" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/PlayerPose.cs" Condition="Exists('$(RepoRoot)/Client/Assets/Scripts/Game/PlayerPose.cs')" />
  </ItemGroup>
</Project>
```

3. 지금 상태(이 Phase 시작 전)를 확인한다.

   Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
   Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

- [ ] **Step 2: 실패하는 테스트를 쓴다**

**수정** `Server/tests/ProjectH.Server.Tests/Game/MatchTests.cs`:

변경 전:

```csharp
        // After the grace window the player stops walking (a paused client must not keep moving).
        // Keep ticking long enough to land: no second jump either.
        float zAfterGrace = player.State.Position.Z;
        for (int i = 0; i < 45; i++) _match.Tick();
        Assert.Equal(zAfterGrace, player.State.Position.Z);
```

변경 후:

```csharp
        // After the grace window the player stops walking (a paused client must not keep moving). Phase 12 D3: the jump
        // is still in the air there and keeps its momentum until it lands. Keep ticking long enough to land: no second
        // jump either, and no more movement.
        for (int i = 0; i < 45; i++) _match.Tick();
        float zAfterGrace = player.State.Position.Z;
        for (int i = 0; i < 15; i++) _match.Tick();
        Assert.Equal(zAfterGrace, player.State.Position.Z);
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Shared/MovementModesTests.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 spec §2: sprint and energy, crouch, slide, air momentum and the landing speed (D3, D7, D10), on the flat
// floor unless a test builds its own world. Every expectation follows the 30 Hz step rule.
public class MovementModesTests
{
    private const float Dt = 1f / 30f;

    private static readonly InputCommand Sprint = new() { MoveY = 1f, Buttons = InputButtons.Sprint };
    private static readonly InputCommand Walk = new() { MoveY = 1f };
    private static readonly InputCommand Idle = new();

    private static Box B(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        => new(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));

    private static void Run(ref MoveState s, InputCommand input, int steps, ReadOnlySpan<Box> world = default, HeightField? terrain = null)
    {
        for (int i = 0; i < steps; i++) MovementSimulation.Step(ref s, input, Dt, world, terrain ?? HeightField.Flat);
    }

    private static StepResult StepOnce(ref MoveState s, InputCommand input, ReadOnlySpan<Box> world = default, HeightField? terrain = null)
    {
        MovementSimulation.Step(ref s, input, Dt, world, terrain ?? HeightField.Flat, out StepResult result);
        return result;
    }

    private static InputCommand With(InputCommand input, InputButtons extra)
    {
        input.Buttons |= extra;
        return input;
    }

    // ---- Sprint and energy (D3) ----

    [Fact]
    public void ADefaultState_IsRested_InGroundMode()
    {
        var s = new MoveState();
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(MovementTuning.MaxEnergy, s.Energy);
        Assert.False(s.Exhausted);
    }

    [Fact]
    public void Sprinting_DrainsEnergy_AtTwentyPerSecond_AndMovesAtSprintSpeed()
    {
        var s = new MoveState();
        StepResult r = StepOnce(ref s, Sprint);
        Assert.True(r.Sprinting);
        Assert.Equal(MoveSettings.SprintSpeed, s.HorizontalVelocity.Length(), 4);
        Run(ref s, Sprint, 29);
        // 67 hundredths per tick (20 x 100 / 30, rounded) for 30 ticks.
        Assert.Equal(100f - 30 * 0.67f, s.Energy, 3);
        Assert.Equal(MoveSettings.SprintSpeed, s.Position.Z, 3);
    }

    [Fact]
    public void SprintSpeed_IsTheLimit_ForAnyInput()
    {
        var s = new MoveState();
        Run(ref s, new InputCommand { MoveY = 1000f, MoveX = 5f, Buttons = InputButtons.Sprint }, 30);
        Assert.Equal(MoveSettings.SprintSpeed, new Vector2(s.Position.X, s.Position.Z).Length(), 3);
    }

    [Fact]
    public void SprintWithoutMoving_CostsNothing()
    {
        var s = new MoveState();
        StepResult r = StepOnce(ref s, new InputCommand { Buttons = InputButtons.Sprint });
        Assert.False(r.Sprinting);
        Assert.Equal(MovementTuning.MaxEnergy, s.Energy);
    }

    [Fact]
    public void EmptyEnergy_EndsTheSprint_UntilTwentyIsBack()
    {
        var s = new MoveState();
        int ticks = 0;
        while (!s.Exhausted && ticks < 300)
        {
            StepOnce(ref s, Sprint);
            ticks++;
        }
        Assert.True(s.Exhausted);
        Assert.Equal(150, ticks);   // 10000 / 67 rounded up: about 5 s
        Assert.Equal(0f, s.Energy);

        // Shift still held: walking speed, no cost, and the energy comes back after the delay.
        StepResult r = StepOnce(ref s, Sprint);
        Assert.False(r.Sprinting);
        Assert.Equal(MoveSettings.WalkSpeed, s.HorizontalVelocity.Length(), 4);

        int more = 0;
        while (s.Exhausted && more < 300)
        {
            r = StepOnce(ref s, Sprint);
            Assert.False(r.Sprinting);
            more++;
        }
        Assert.True(s.Energy >= MovementTuning.SprintResumeEnergy);
        Assert.True(StepOnce(ref s, Sprint).Sprinting);
    }

    [Fact]
    public void Recovery_WaitsOneSecond_ThenRecoversTwentyFivePerSecond()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 60);
        float afterSprint = s.Energy;
        Run(ref s, Walk, 30);   // the 1 s delay: nothing comes back
        Assert.Equal(afterSprint, s.Energy, 3);
        Run(ref s, Walk, 30);   // then 83 hundredths per tick
        Assert.Equal(afterSprint + 30 * 0.83f, s.Energy, 3);
    }

    [Fact]
    public void Energy_NeverExceedsTheMaximum()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 3);
        Run(ref s, Idle, 200);
        Assert.Equal(MovementTuning.MaxEnergy, s.Energy);
        Assert.Equal(0, s.EnergySpent);
    }

    // ---- Crouch (D7) ----

    [Fact]
    public void Crouch_LowersTheBox_To1Point2_AndWalksAt2Point5()
    {
        var s = new MoveState();
        StepOnce(ref s, With(Walk, InputButtons.Crouch));
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.Equal(1.2f, MovementSimulation.CollisionHeight(s.Mode));
        Run(ref s, With(Walk, InputButtons.Crouch), 29);
        Assert.Equal(MovementTuning.CrouchSpeed, s.Position.Z, 1);
    }

    [Fact]
    public void Crouch_PassesUnderALowGap_ThatStandingCannot()
    {
        // A slab from 1.5 m up across the way: 1.2 m fits under it, 1.8 m does not.
        Box[] slab = { B(-3f, 1.5f, 2f, 3f, 2f, 3f) };
        var standing = new MoveState();
        Run(ref standing, Walk, 60, slab);
        Assert.True(standing.Position.Z < 2f);

        var crouched = new MoveState();
        Run(ref crouched, With(Walk, InputButtons.Crouch), 120, slab);
        Assert.True(crouched.Position.Z > 3f);
        Assert.False(MovementSimulation.OverlapsAny(crouched.Position, 1.2f, slab));
    }

    [Fact]
    public void Crouch_UnderACeiling_StaysDown_UntilThereIsRoom()
    {
        Box[] ceiling = { B(-2f, 1.5f, -2f, 2f, 2f, 2f) };
        var s = new MoveState { Position = new Vector3(0f, 0f, -4f) };
        Run(ref s, With(Walk, InputButtons.Crouch), 50, ceiling);   // crouch-walks in under it
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.False(MovementSimulation.CanStand(s.Position, ceiling));

        Run(ref s, Idle, 10, ceiling);   // released, but no room: still down, and a jump cannot stand it up either
        Assert.Equal(MovementMode.Crouch, s.Mode);
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Jump }, ceiling);
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.Equal(0f, s.VelocityY);

        Run(ref s, new InputCommand { MoveY = 1f }, 60, ceiling);   // walks out from under it and stands up
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void Crouch_DisablesSprint()
    {
        var s = new MoveState();
        StepResult r = StepOnce(ref s, With(Walk, InputButtons.Crouch));   // walking: a crouch, not a slide
        r = StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        Assert.False(r.Sprinting);
        Assert.Equal(MovementTuning.CrouchSpeed, s.HorizontalVelocity.Length(), 4);
    }

    // ---- Slide (D3, D7) ----

    private static MoveState Sprinting()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 5);
        return s;
    }

    [Fact]
    public void Slide_StartsFromASprint_AtNineMetresPerSecond()
    {
        MoveState s = Sprinting();
        StepResult r = StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        Assert.Equal(MovementMode.Slide, s.Mode);
        Assert.False(r.Sprinting);
        Assert.True(r.Charging);
        Assert.Equal(MovementTuning.SlideStartSpeed - MovementTuning.SlideFriction * Dt, s.HorizontalVelocity.Length(), 3);
    }

    [Fact]
    public void CrouchWhileWalking_IsACrouch_NotASlide()
    {
        var s = new MoveState();
        Run(ref s, Walk, 5);
        StepOnce(ref s, With(Walk, InputButtons.Crouch));
        Assert.Equal(MovementMode.Crouch, s.Mode);
    }

    [Fact]
    public void Slide_Slows_AndEndsInACrouch_BelowThreeMetresPerSecond()
    {
        MoveState s = Sprinting();
        var slide = new InputCommand { Buttons = InputButtons.Crouch };   // no steering needed: the slide keeps its direction
        StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        float previous = s.HorizontalVelocity.Length();
        int ticks = 0;
        while (s.Mode == MovementMode.Slide && ticks < 100)
        {
            StepOnce(ref s, slide);
            if (s.Mode == MovementMode.Slide)
            {
                Assert.True(s.HorizontalVelocity.Length() < previous);
                previous = s.HorizontalVelocity.Length();
            }
            ticks++;
        }
        Assert.Equal(MovementMode.Crouch, s.Mode);
        // From 9 m/s at 5 m/s per second down to 3 m/s: 1.2 s.
        Assert.InRange(ticks, 35, 37);
    }

    [Fact]
    public void Slide_Downhill_SlowsLess_OrSpeedsUp()
    {
        // A 0.6 slope (the steepest of the map) falling along +X: 6 m high at x = 0, 0 at x = 10.
        var downhill = new HeightField(-10f, -10f, 10f, 3, 3, new[] { 6f, 6f, 0f, 6f, 6f, 0f, 6f, 6f, 0f });
        Assert.Equal(new Vector2(-0.6f, 0f), downhill.Gradient(5f, 0f));

        var flat = new MoveState { Yaw = 90f };
        var slope = new MoveState { Position = new Vector3(0.5f, downhill.Height(0.5f, 0f), 0f), Yaw = 90f };
        var sprintX = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint };
        Run(ref flat, sprintX, 3);
        Run(ref slope, sprintX, 3, default, downhill);
        var slideX = new InputCommand { Yaw = 90f, Buttons = InputButtons.Crouch };
        Run(ref flat, slideX, 10);
        Run(ref slope, slideX, 10, default, downhill);

        Assert.Equal(MovementMode.Slide, slope.Mode);
        // Flat: -5 m/s per second. Downhill: +0.6 x 20 x 0.6 - 5 = +2.2 m/s per second.
        Assert.True(slope.HorizontalVelocity.Length() > MovementTuning.SlideStartSpeed);
        Assert.True(flat.HorizontalVelocity.Length() < MovementTuning.SlideStartSpeed);
    }

    [Fact]
    public void Slide_NeverExceedsThirteenMetresPerSecond()
    {
        // A long 0.6 slope.
        var hill = new HeightField(-100f, -10f, 100f, 3, 2, new[] { 120f, 60f, 0f, 120f, 60f, 0f });
        var s = new MoveState { Position = new Vector3(-90f, hill.Height(-90f, 0f), 0f), Yaw = 90f };
        Run(ref s, new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint }, 3, default, hill);
        Run(ref s, new InputCommand { Yaw = 90f, Buttons = InputButtons.Crouch }, 150, default, hill);
        Assert.Equal(MovementMode.Slide, s.Mode);
        Assert.Equal(MovementTuning.SlideMaxSpeed, s.HorizontalVelocity.Length(), 3);
    }

    [Fact]
    public void Slide_EndsWithAJump_ThatKeepsTheSlideSpeed()
    {
        MoveState s = Sprinting();
        StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        float speed = s.HorizontalVelocity.Length();
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Crouch | InputButtons.Jump });
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);
        Assert.True(s.HorizontalVelocity.Length() > speed - 0.5f);
    }

    [Fact]
    public void Slide_ReleasingCrouch_StandsUp()
    {
        MoveState s = Sprinting();
        StepOnce(ref s, With(Sprint, InputButtons.Crouch));
        StepOnce(ref s, Sprint);
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void Slide_IntoAWall_EndsInACrouch_AndReportsTheBox()
    {
        Box[] wall = { B(-3f, 0f, 3f, 3f, 3f, 4f) };
        var s = new MoveState();
        Run(ref s, Sprint, 5, wall);
        StepOnce(ref s, With(Sprint, InputButtons.Crouch), wall);
        Assert.Equal(MovementMode.Slide, s.Mode);
        StepResult r = default;
        for (int i = 0; i < 30 && s.Mode == MovementMode.Slide; i++) r = StepOnce(ref s, new InputCommand { Buttons = InputButtons.Crouch }, wall);
        Assert.Equal(MovementMode.Crouch, s.Mode);
        Assert.Equal(0, r.BlockedBy);
        Assert.True(r.Charging);
    }

    // ---- Air momentum (D3) ----

    [Fact]
    public void SprintJump_TakesOffFaster_AtTheSameHeight()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 3);
        StepOnce(ref s, With(Sprint, InputButtons.Jump));
        Assert.Equal(MoveSettings.SprintSpeed * MovementTuning.SprintJumpSpeedScale, s.HorizontalVelocity.Length(), 3);
        Assert.Equal(MoveSettings.JumpSpeed, s.VelocityY);
    }

    [Fact]
    public void InTheAir_TheVelocityCarriesOver_WithoutInput()
    {
        var s = new MoveState();
        Run(ref s, Walk, 3);
        StepOnce(ref s, With(Walk, InputButtons.Jump));
        float zAtTakeoff = s.Position.Z;
        Run(ref s, Idle, 10);
        Assert.Equal(zAtTakeoff + 10 * MoveSettings.WalkSpeed * Dt, s.Position.Z, 3);
    }

    [Fact]
    public void AirControl_SteersAtTwelve_ButNeverAboveTheTakeoffSpeed()
    {
        var s = new MoveState();
        Run(ref s, Sprint, 3);
        StepOnce(ref s, With(Sprint, InputButtons.Jump));
        float takeoff = s.HorizontalVelocity.Length();
        // Turn to the side in the air: the velocity turns, its length never grows.
        for (int i = 0; i < 15; i++)
        {
            StepOnce(ref s, new InputCommand { MoveX = 1f });
            Assert.True(s.HorizontalVelocity.Length() <= takeoff + 1e-4f);
        }
        Assert.True(s.HorizontalVelocity.X > 0f);
    }

    [Fact]
    public void AStandingJump_DriftsAtMostAtWalkingSpeed()
    {
        var s = new MoveState();
        StepOnce(ref s, new InputCommand { Buttons = InputButtons.Jump });
        StepOnce(ref s, Walk);
        Assert.Equal(MovementTuning.AirAcceleration * Dt, s.HorizontalVelocity.Length(), 4);
        Run(ref s, Walk, 15);
        Assert.Equal(MoveSettings.WalkSpeed, s.HorizontalVelocity.Length(), 4);
    }

    [Fact]
    public void AWallInTheAir_StopsThatPartOfTheVelocity()
    {
        Box[] wall = { B(-3f, 0f, 1f, 3f, 3f, 2f) };
        var s = new MoveState();
        StepOnce(ref s, With(Sprint, InputButtons.Jump), wall);
        StepResult r = default;
        for (int i = 0; i < 10; i++) r = StepOnce(ref s, Idle, wall);
        Assert.Equal(0f, s.HorizontalVelocity.Y);
        Assert.False(MovementSimulation.OverlapsAny(s.Position, wall));
    }

    // ---- Landing speed (D10) ----

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    [InlineData(10f)]
    public void AFall_ReportsItsLandingSpeed_Once(float height)
    {
        var s = new MoveState { Position = new Vector3(0f, height, 0f) };
        float landing = 0f;
        int landings = 0;
        for (int i = 0; i < 120; i++)
        {
            StepResult r = StepOnce(ref s, Idle);
            if (r.LandingSpeed > 0f)
            {
                landing = r.LandingSpeed;
                landings++;
            }
        }
        Assert.Equal(1, landings);
        // v = sqrt(2 g h), within one tick of gravity (the step rule).
        Assert.InRange(landing, MathF.Sqrt(40f * height) - 0.7f, MathF.Sqrt(40f * height) + 0.7f);
    }

    [Fact]
    public void Walking_AndStanding_ReportNoLanding()
    {
        var s = new MoveState();
        for (int i = 0; i < 30; i++) Assert.Equal(0f, StepOnce(ref s, Sprint).LandingSpeed);
    }

    // ---- Terrain slope (D7) ----

    [Fact]
    public void Gradient_MatchesTheHeights_OnBothTriangles_AndIsZeroOutside()
    {
        HeightField t = GameMap.Terrain;
        const float e = 0.01f;   // a small step that stays inside the triangle
        for (int i = 0; i < t.VertsX - 1; i += 3)
        {
            for (int j = 0; j < t.VertsZ - 1; j += 3)
            {
                // One point inside each triangle of the cell: (u, v) = (0.75, 0.25) and (0.25, 0.75).
                for (int k = 0; k < 2; k++)
                {
                    float x = t.OriginX + (i + (k == 0 ? 0.75f : 0.25f)) * t.CellSize;
                    float z = t.OriginZ + (j + (k == 0 ? 0.25f : 0.75f)) * t.CellSize;
                    Vector2 g = t.Gradient(x, z);
                    Assert.Equal((t.Height(x + e, z) - t.Height(x, z)) / e, g.X, 0.01);
                    Assert.Equal((t.Height(x, z + e) - t.Height(x, z)) / e, g.Y, 0.01);
                }
            }
        }
        Assert.Equal(Vector2.Zero, t.Gradient(-200f, 0f));
        Assert.Equal(Vector2.Zero, t.Gradient(float.NaN, 0f));
    }
}
```

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 컴파일 실패(`MovementMode`, `StepResult`, `HorizontalVelocity` 등이 없다)

- [ ] **Step 4: 이동 핵심을 구현한다**

**수정** `Shared/Runtime/Simulation/HeightField.cs`:

변경 전:

```csharp
using System;
```

변경 후:

```csharp
using System;
using System.Numerics;
```

변경 전:

```csharp
        // Height inside cell (i, j) at local (u, v) in [0, 1]: triangle (0,0)-(1,0)-(1,1) when u >= v, else
```

변경 후:

```csharp
        // Phase 12 D7: the slope of the triangle under (x, z) as (dh/dx, dh/dz), rise per metre. Same triangles as Height.
        // Outside the grid the surface is flat (Height clamps to the edge), so the slope there is 0. Allocates nothing.
        public Vector2 Gradient(float x, float z)
        {
            float gx = (x - OriginX) * _inverseCell;
            float gz = (z - OriginZ) * _inverseCell;
            // !(g >= 0) also catches NaN.
            if (!(gx >= 0f) || !(gz >= 0f) || gx > VertsX - 1 || gz > VertsZ - 1) return Vector2.Zero;

            int i = (int)gx;
            int j = (int)gz;
            if (i > VertsX - 2) i = VertsX - 2;
            if (j > VertsZ - 2) j = VertsZ - 2;
            float u = gx - i;
            float v = gz - j;
            int k = i + j * VertsX;
            float h00 = _heights[k];
            float h11 = _heights[k + 1 + VertsX];
            if (u >= v)
            {
                float h10 = _heights[k + 1];
                return new Vector2((h10 - h00) * _inverseCell, (h11 - h10) * _inverseCell);
            }
            float h01 = _heights[k + VertsX];
            return new Vector2((h11 - h01) * _inverseCell, (h01 - h00) * _inverseCell);
        }

        // Height inside cell (i, j) at local (u, v) in [0, 1]: triangle (0,0)-(1,0)-(1,1) when u >= v, else
```

**수정** `Shared/Runtime/Simulation/InputCommand.cs`:

변경 전:

```csharp
        UseShieldCell = 1024, // 5
```

변경 후:

```csharp
        UseShieldCell = 1024, // 5
        Crouch = 2048,        // Phase 12 D7: held. C toggles it on the client, Ctrl holds it.
```

**수정** `Shared/Runtime/Simulation/MoveState.cs`:

변경 전:

```csharp
    // Everything MovementSimulation needs to continue from one step to the next.
```

변경 후:

```csharp
    // Everything MovementSimulation needs to continue from one step to the next.
    // Phase 12 D1: the mode and what the new modes carry from tick to tick. The default value is a rested character
    // standing in Ground mode, as before Phase 12.
```

변경 전:

```csharp
        public float Yaw;          // degrees 0..360
```

변경 후:

```csharp
        public float Yaw;          // degrees 0..360

        public MovementMode Mode;
        // X and Z velocity in m/s. On the ground the input sets it every tick; in the air, a slide and a vault it carries over.
        public Vector2 HorizontalVelocity;
        // Energy used, in hundredths (0 = MovementTuning.MaxEnergy left). Spent rather than left, so a default state is
        // rested; integer, so the wire value is exact (Energy).
        public ushort EnergySpent;
        // Ticks to wait before energy recovers (set when a sprint stops).
        public byte EnergyDelayTicks;
        // Vault ticks left (0 in every other mode).
        public byte ModeTicks;
        // D3: the energy ran out; sprint stays off until it is back at MovementTuning.SprintResumeEnergy.
        public bool Exhausted;

        // Energy left, 0..MaxEnergy.
        public float Energy => (MaxEnergyHundredths - EnergySpent) / (float)MovementTuning.EnergyScale;

        public const int MaxEnergyHundredths = (int)(MovementTuning.MaxEnergy * MovementTuning.EnergyScale);
```

**새 파일** `Shared/Runtime/Simulation/MovementMode.cs`:

```csharp
namespace ProjectH.Shared.Simulation
{
    // Phase 12 D1: the one movement state of a character. MovementSimulation.Step branches on it; the server, client
    // prediction and the snapshot (SnapshotEntity flag bits 1-3) all see the same value. Values are the wire format:
    // never renumber. Sprinting and airborne are not modes: they follow from the input and the ground check.
    public enum MovementMode : byte
    {
        Ground = 0,     // standing, walking, sprinting, jumping and falling
        Crouch = 1,
        Slide = 2,
        Vault = 3,      // a mantle or a hurdle in progress (MoveState.ModeTicks left)
        Freefall = 4,
        Glide = 5,
        Transport = 6,  // riding the drop transport (DropTransport.Ride places the character)
    }
}
```

**파일 전체를 바꾼다** `Shared/Runtime/Simulation/MovementSimulation.cs`:

```csharp
using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 12: what one step reports besides the new state. The server acts on it (fall damage D10, shoulder bash D9);
    // client prediction uses the bash only.
    public struct StepResult
    {
        // Vertical speed (m/s, positive) the character hit the ground with this tick in Ground, Crouch or Slide mode.
        // 0 = no landing. Vault, glide and freefall landings report 0 (D10).
        public float LandingSpeed;
        // Index into the world span of the box that stopped a horizontal move this tick, -1 = none.
        public int BlockedBy;
        // Sprinting this tick (snapshot flag, D11).
        public bool Sprinting;
        // Sprinting or sliding: a closed door in BlockedBy is shouldered open (D9).
        public bool Charging;
    }

    // The one piece of game logic allowed in Shared (see game-core-rules §4): client prediction and
    // the authoritative server run exactly this code. Pure math, no allocation, no engine types.
    // The character is an axis-aligned box (MoveSettings.HalfWidth, and a height that depends on the mode) with its feet
    // at MoveState.Position; the world is the terrain (Phase 6 D2-D4) plus the boxes passed in. Every terrain slope is
    // walkable (GameMapTests), so the terrain never blocks a horizontal move: it only sets the floor height under the feet.
    // Phase 12 D1: Step branches on MoveState.Mode into a few plain functions; there is no state object or class
    // hierarchy. Every number is in MovementTuning (new) or MoveSettings (Phase 1-6).
    public static class MovementSimulation
    {
        private const float DegToRad = 0.017453292f;
        private const int AxisX = 0;
        private const int AxisY = 1;
        private const int AxisZ = 2;

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain)
        {
            Step(ref state, input, deltaTime, world, terrain, out _);
        }

        public static void Step(ref MoveState state, in InputCommand input, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain,
            out StepResult result)
        {
            result = new StepResult { BlockedBy = -1 };

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
            var move = new Vector2(cos * moveX + sin * moveY, -sin * moveX + cos * moveY);

            StepGround(ref state, input.Buttons, move, deltaTime, world, terrain, ref result);
        }

        // Ground, Crouch and Slide (D7), walking, sprinting, jumping and falling (D3 air momentum).
        // move: the input direction in world X/Z, length 0..1.
        private static void StepGround(ref MoveState state, InputButtons buttons, Vector2 move, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain, ref StepResult result)
        {
            bool jump = (buttons & InputButtons.Jump) != 0;
            bool crouchHeld = (buttons & InputButtons.Crouch) != 0;
            bool moving = move.X != 0f || move.Y != 0f;
            Vector3 position = state.Position;

            // 1) Leave any box we start inside (reconcile snap, rounding): sweeps assume a free start (D4).
            Depenetrate(ref position, CollisionHeight(state.Mode), world, terrain);

            // 2) Stateless ground check (D3). Snapping Y onto the surface keeps a standing player at an
            //    exact height, so grounded/airborne never alternates from rounding. A fall that ends in the snap is a
            //    landing too (D10).
            bool grounded = false;
            bool onTerrain = false;
            if (state.VelocityY <= 0f && TryFindGround(position, world, terrain, out float groundY, out onTerrain))
            {
                grounded = true;
                if (state.VelocityY < 0f) result.LandingSpeed = -state.VelocityY;
                position.Y = groundY;
                state.VelocityY = 0f;
            }

            // 3) Posture (D7), on the ground only: crouch pressed while sprinting starts a slide, otherwise a crouch;
            //    released, the character stands up where the standing box fits.
            if (grounded) UpdatePosture(ref state, position, crouchHeld, world);

            // 4) Sprint and energy (D3, D7): Shift while moving in Ground mode, with energy.
            bool sprinting = (buttons & InputButtons.Sprint) != 0 && state.Mode == MovementMode.Ground && !state.Exhausted && moving;
            UpdateEnergy(ref state, sprinting, deltaTime);
            result.Sprinting = sprinting;

            // 5) Horizontal velocity: the input on the ground, the slide's own speed, momentum plus air control in the air.
            if (grounded && state.Mode == MovementMode.Slide)
            {
                SlideVelocity(ref state, position, onTerrain, terrain, deltaTime);
            }
            else if (grounded)
            {
                float speed = state.Mode == MovementMode.Crouch ? MovementTuning.CrouchSpeed
                    : sprinting ? MoveSettings.SprintSpeed : MoveSettings.WalkSpeed;
                state.HorizontalVelocity = move * speed;
            }
            else
            {
                AirControl(ref state, move, deltaTime);
            }

            // 6) Jump. From a crouch only where the standing box fits; a slide jump keeps the slide's speed; a sprint jump
            //    takes off faster (D3) at the same height.
            bool walking = grounded;
            if (grounded && jump && TryStartJump(ref state, position, world))
            {
                state.VelocityY = MoveSettings.JumpSpeed;
                if (sprinting) state.HorizontalVelocity = move * (MoveSettings.SprintSpeed * MovementTuning.SprintJumpSpeedScale);
                walking = false;
            }
            else if (!grounded)
            {
                state.VelocityY += MoveSettings.Gravity * deltaTime;
            }
            result.Charging = sprinting || state.Mode == MovementMode.Slide;
            float height = CollisionHeight(state.Mode);

            // 7) Axis-separated sweeps X -> Z (D2): a blocked axis stops, the other keeps moving. The blocked part of the
            //    velocity is gone (it matters in the air and in a slide, which carry it over).
            float startX = position.X;
            float startZ = position.Z;
            bool blocked = false;
            float wantX = state.HorizontalVelocity.X * deltaTime;
            float movedX = Sweep(position, height, AxisX, wantX, world, terrain, out int hitX);
            position.X += movedX;
            if (movedX != wantX)
            {
                state.HorizontalVelocity.X = 0f;
                result.BlockedBy = hitX;
                blocked = true;
            }
            float wantZ = state.HorizontalVelocity.Y * deltaTime;
            float movedZ = Sweep(position, height, AxisZ, wantZ, world, terrain, out int hitZ);
            position.Z += movedZ;
            if (movedZ != wantZ)
            {
                state.HorizontalVelocity.Y = 0f;
                if (result.BlockedBy < 0) result.BlockedBy = hitZ;
                blocked = true;
            }
            // D7: a slide that runs into something ends in a crouch.
            if (blocked && state.Mode == MovementMode.Slide) state.Mode = MovementMode.Crouch;

            // 8) Phase 6 D4: uphill the terrain lifts the feet; downhill a walking character follows the slope instead
            //    of leaving the ground for a tick. MaxSlope bounds the drop over the distance moved, and the Y sweep
            //    stops on a box top on the way down.
            float floor = terrain.Height(position.X, position.Z);
            if (position.Y < floor)
            {
                position.Y = floor;
            }
            else if (walking)
            {
                float dx = position.X - startX;
                float dz = position.Z - startZ;
                float reach = MathF.Sqrt(dx * dx + dz * dz) * MoveSettings.MaxSlope + MoveSettings.GroundProbe;
                float drop = position.Y - floor;
                if (drop > 0f && drop <= reach) position.Y += Sweep(position, height, AxisY, -drop, world, terrain, out _);
            }

            // 9) Y sweep, the floor being the terrain. Stopped on the way down is a landing (D10).
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, height, AxisY, wantY, world, terrain, out _);
            if (movedY != wantY)
            {
                if (wantY < 0f) result.LandingSpeed = -state.VelocityY;
                state.VelocityY = 0f;   // landed or hit a ceiling
            }
            position.Y += movedY;

            state.Position = position;
        }

        // D7: crouch held on the ground: a slide from a sprint-fast move, otherwise a crouch. Released: stand up where
        // the standing box fits (a slide that cannot stand becomes a crouch).
        private static void UpdatePosture(ref MoveState state, Vector3 position, bool crouchHeld, ReadOnlySpan<Box> world)
        {
            switch (state.Mode)
            {
                case MovementMode.Ground:
                    if (!crouchHeld) return;
                    float speed = state.HorizontalVelocity.Length();
                    if (speed >= MovementTuning.SlideMinStartSpeed)
                    {
                        state.Mode = MovementMode.Slide;
                        state.HorizontalVelocity *= MathF.Max(speed, MovementTuning.SlideStartSpeed) / speed;
                    }
                    else
                    {
                        state.Mode = MovementMode.Crouch;
                    }
                    return;

                case MovementMode.Crouch:
                    if (!crouchHeld && CanStand(position, world)) state.Mode = MovementMode.Ground;
                    return;

                case MovementMode.Slide:
                    if (!crouchHeld) state.Mode = CanStand(position, world) ? MovementMode.Ground : MovementMode.Crouch;
                    return;
            }
        }

        // D3: the slide keeps its direction and loses SlideFriction per second; on the terrain a downhill slope adds
        // slope x gravity x SlideSlopeFactor. Below SlideMinSpeed it ends in a crouch.
        private static void SlideVelocity(ref MoveState state, Vector3 position, bool onTerrain, HeightField terrain, float deltaTime)
        {
            Vector2 velocity = state.HorizontalVelocity;
            float speed = velocity.Length();
            if (speed < MovementTuning.SlideMinSpeed)
            {
                state.Mode = MovementMode.Crouch;
                return;
            }
            Vector2 direction = velocity / speed;
            speed -= MovementTuning.SlideFriction * deltaTime;
            if (onTerrain)
            {
                // Rise per metre along the slide; negative is downhill.
                float rise = Vector2.Dot(terrain.Gradient(position.X, position.Z), direction);
                if (rise < 0f) speed += -rise * -MoveSettings.Gravity * MovementTuning.SlideSlopeFactor * deltaTime;
            }
            if (speed > MovementTuning.SlideMaxSpeed) speed = MovementTuning.SlideMaxSpeed;
            if (speed < MovementTuning.SlideMinSpeed) state.Mode = MovementMode.Crouch;
            state.HorizontalVelocity = direction * speed;
        }

        // D3: in the air the velocity carries over. The input accelerates it by AirAcceleration, and the speed never
        // grows above the larger of what it was and the walking speed (a standing jump can still drift).
        private static void AirControl(ref MoveState state, Vector2 move, float deltaTime)
        {
            if (move.X == 0f && move.Y == 0f) return;
            Vector2 velocity = state.HorizontalVelocity;
            float limit = MathF.Max(velocity.Length(), state.Mode == MovementMode.Crouch ? MovementTuning.CrouchSpeed : MoveSettings.WalkSpeed);
            velocity += move * (MovementTuning.AirAcceleration * deltaTime);
            float speed = velocity.Length();
            if (speed > limit) velocity *= limit / speed;
            state.HorizontalVelocity = velocity;
        }

        // A jump from the ground. A crouch stands up first and cannot jump where the standing box does not fit; a slide
        // jump becomes a normal jump with the slide's velocity.
        private static bool TryStartJump(ref MoveState state, Vector3 position, ReadOnlySpan<Box> world)
        {
            if (state.Mode == MovementMode.Crouch || state.Mode == MovementMode.Slide)
            {
                if (!CanStand(position, world)) return false;
                state.Mode = MovementMode.Ground;
            }
            return true;
        }

        // D3: sprinting costs SprintEnergyCostPerSecond and restarts the recovery delay; after the delay the energy
        // recovers at EnergyRecoveryPerSecond. Running out sets Exhausted (the sprint of this tick is already paid), which
        // stays until SprintResumeEnergy is back. Integer hundredths per tick, so both sides and the wire agree exactly.
        private static void UpdateEnergy(ref MoveState state, bool sprinting, float deltaTime)
        {
            if (sprinting)
            {
                int spent = state.EnergySpent + PerTick(MovementTuning.SprintEnergyCostPerSecond, deltaTime);
                if (spent >= MoveState.MaxEnergyHundredths)
                {
                    spent = MoveState.MaxEnergyHundredths;
                    state.Exhausted = true;
                }
                state.EnergySpent = (ushort)spent;
                state.EnergyDelayTicks = TicksOf(MovementTuning.EnergyRecoveryDelaySeconds, deltaTime);
                return;
            }
            if (state.EnergyDelayTicks > 0)
            {
                state.EnergyDelayTicks--;
                return;
            }
            int left = state.EnergySpent - PerTick(MovementTuning.EnergyRecoveryPerSecond, deltaTime);
            state.EnergySpent = (ushort)(left > 0 ? left : 0);
            if (state.Exhausted && state.Energy >= MovementTuning.SprintResumeEnergy) state.Exhausted = false;
        }

        // Energy hundredths per tick for a per-second rate.
        private static int PerTick(float perSecond, float deltaTime) =>
            (int)MathF.Round(perSecond * MovementTuning.EnergyScale * deltaTime);

        // Whole ticks for a duration (at least 1, at most 255).
        private static byte TicksOf(float seconds, float deltaTime)
        {
            float ticks = MathF.Round(seconds / deltaTime);
            if (!(ticks >= 1f)) return 1;
            return ticks > 255f ? (byte)255 : (byte)ticks;
        }

        // D7, D13: the collision (and hit) box height of a mode.
        public static float CollisionHeight(MovementMode mode) =>
            mode == MovementMode.Crouch || mode == MovementMode.Slide ? MovementTuning.CrouchHeight : MoveSettings.Height;

        // D7: the standing box fits at these feet (nothing in the 0.6 m above a crouch).
        public static bool CanStand(Vector3 feet, ReadOnlySpan<Box> world) => !OverlapsAny(feet, MoveSettings.Height, world);

        public static bool IsGrounded(in MoveState state, ReadOnlySpan<Box> world, HeightField terrain)
        {
            return state.VelocityY <= 0f && TryFindGround(state.Position, world, terrain, out _, out _);
        }

        // True if the standing character box at these feet overlaps any box by more than zero on every axis.
        // Touching faces (as after a sweep or a ground snap) do not count.
        public static bool OverlapsAny(Vector3 feet, ReadOnlySpan<Box> world) => OverlapsAny(feet, MoveSettings.Height, world);

        // The same for a character box of this height.
        public static bool OverlapsAny(Vector3 feet, float height, ReadOnlySpan<Box> world)
        {
            GetBounds(feet, height, out Vector3 min, out Vector3 max);
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

        private static void Depenetrate(ref Vector3 feet, float height, ReadOnlySpan<Box> world, HeightField terrain)
        {
            float floor = terrain.Height(feet.X, feet.Z);
            if (feet.Y < floor) feet.Y = floor;

            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                GetBounds(feet, height, out Vector3 min, out Vector3 max);

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
                if (push < best && feet.Y - push - MoveSettings.Skin >= floor) { best = push; direction = 5; }

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

        // Highest floor or box top within GroundProbe of the feet, under the character's footprint. The terrain floor
        // is its height under the feet (Phase 6 D4). onTerrain: the ground found is the terrain, not a box top.
        private static bool TryFindGround(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain, out float groundY, out bool onTerrain)
        {
            float floor = terrain.Height(feet.X, feet.Z);
            bool found = feet.Y <= floor + MoveSettings.GroundProbe;
            groundY = floor;
            onTerrain = found;

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
                    onTerrain = false;
                }
            }
            return found;
        }

        // How far the character may move along one axis (same sign as delta, |result| <= |delta|).
        // Every box that overlaps on the other two axes and lies ahead limits the move to its near
        // face minus Skin, whatever the distance, so a fast fall cannot pass through a thin box.
        // hit: the index of the box that limited the move, -1 = none (the terrain floor or nothing).
        private static float Sweep(Vector3 feet, float height, int axis, float delta, ReadOnlySpan<Box> world, HeightField terrain, out int hit)
        {
            hit = -1;
            if (delta == 0f) return 0f;

            GetBounds(feet, height, out Vector3 min, out Vector3 max);
            float limit = MathF.Abs(delta);
            if (axis == AxisY && delta < 0f)
            {
                // The terrain under the feet is the floor, no Skin.
                float above = min.Y - terrain.Height(feet.X, feet.Z);
                if (above < 0f) above = 0f;
                if (above < limit) limit = above;
            }

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
                if (gap < limit)
                {
                    limit = gap;
                    hit = i;
                }
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

        private static void GetBounds(Vector3 feet, float height, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
            max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
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

**새 파일** `Shared/Runtime/Simulation/MovementTuning.cs`:

```csharp
namespace ProjectH.Shared.Simulation
{
    // Phase 12 D2, D3: every number of the Phase 12 movement in one place. Client prediction and the server read these
    // same constants, so they are code, not server config (a value changed on one side only would make every
    // prediction wrong). Values are per second; MovementSimulation converts them to ticks with its deltaTime. The older
    // walk, sprint, gravity, jump and size values stay in MoveSettings.
    public static class MovementTuning
    {
        // Energy (D3, D7). MoveState keeps the spent energy in hundredths (MoveState.EnergySpent), so a default state is
        // rested and the wire value (SnapshotSelf.Energy, x100) is exact.
        public const float MaxEnergy = 100f;
        public const float SprintEnergyCostPerSecond = 20f;
        public const float EnergyRecoveryPerSecond = 25f;
        public const float EnergyRecoveryDelaySeconds = 1f;
        // After running out, sprint stays off until the energy is back at this level.
        public const float SprintResumeEnergy = 20f;
        public const int EnergyScale = 100;   // hundredths

        // Crouch (D3, D7). Standing up needs the standing box (MoveSettings.Height) free: CanStand.
        public const float CrouchHeight = 1.2f;
        public const float CrouchSpeed = 2.5f;

        // Slide (D3, D7): starts from a sprint (horizontal speed at least SlideMinStartSpeed) when crouch is pressed.
        public const float SlideMinStartSpeed = 6f;
        public const float SlideStartSpeed = 9f;
        public const float SlideFriction = 5f;          // m/s lost per second
        public const float SlideMinSpeed = 3f;          // below this the slide ends in a crouch
        public const float SlideSlopeFactor = 0.6f;     // downhill: slope x gravity x this is added per second
        public const float SlideMaxSpeed = 13f;

        // Air (D3): a sprinting jump takes off at SprintSpeed x this; in the air the input accelerates the horizontal
        // velocity by AirAcceleration, never above the larger of the speed it had and WalkSpeed.
        public const float SprintJumpSpeedScale = 1.1f;
        public const float AirAcceleration = 12f;

        // Vault (D3, D8): only on a jump press, on the ground, moving forward, with an obstacle within VaultReach.
        public const float VaultReach = 0.8f;
        public const float HurdleMinHeight = 0.5f;
        public const float HurdleMaxHeight = 1.1f;
        public const float HurdleMinSpeed = 6f;
        public const float HurdleSeconds = 0.2f;
        // A hurdle clears an obstacle at most this deep along the move, and lands this far past its far side. A deeper
        // obstacle, or no room behind it, puts the character on its top instead.
        public const float HurdleMaxDepth = 2.2f;
        public const float HurdleLandingGap = 0.1f;
        public const float MantleMaxHeight = 2.1f;
        public const float MantleSeconds = 0.4f;
        public const float MantleInset = 0.4f;          // a mantle stands this far inside the top's edge

        // Fall damage (D3, D10): the landing speed decides it. Server rule (CombatRules.FallDamage); the numbers are here
        // with the rest of the movement.
        public const float FallDamageMinSpeed = 13f;
        public const float FallDamageMaxSpeed = 30f;
        public const int FallDamageMax = 100;
    }
}
```

- [ ] **Step 5: 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 927개(918 통과, 9개 건너뜀)
Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

- [ ] **Step 6: Commit** — `feat(shared): add movement modes, energy, crouch, slide, air momentum and the landing speed (Phase 12 D1-D3, D7, D10)`

---

### Task 2: Vault (Mantle·Hurdle)

**Files:**
- Modify: `Shared/Runtime/Simulation/MovementSimulation.cs`, `MovementTuning.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Shared/VaultTests.cs`
  - Modify: `Server/tests/ProjectH.Server.Tests/Shared/CollisionTests.cs`, `Shared/GameMapTests.cs`(Spec과 다른 점 19)

**Interfaces:**
- Produces: `MovementTuning.VaultBaseTolerance` 0.3. `Step`은 `Vault` 모드를 `StepVault`로 처리하고, 지상에서 앞으로 가며 Jump를 누르면 `TryStartVault`를 먼저 해 본다.

D8의 네 가지 검사는 다음과 같다.

1. 앞 0.8 m 안에 장애물이 있다. 바닥이 발 높이에서 0.3 m 안이어야 한다.
2. 윗면이 발 위 0.5–2.1 m다.
3. 도착점에 1.8 m 서 있을 공간이 있다.
4. 도착점이 상자와 겹치지 않고, 지형 위다.

종류는 다음처럼 정한다.

- **Hurdle:** 높이 1.1 m까지이고 수평 속도 6 m/s 이상이다. 반대편 0.1 m 너머로 넘는다. 장애물이 2.2 m보다 깊거나 반대편에 공간이 없으면 윗면에 선다.
- **Mantle:** 윗면 가장자리 안쪽 0.4 m에 선다. 장애물이 0.8 m보다 얇으면 그 가운데에 선다.
- 낮은 장애물인데 느리면 보통 점프다.

Vault는 (도착 − 시작) / 시간의 일정한 속도로 움직이고 충돌 검사를 하지 않는다. 시간은 Hurdle 6 Tick, Mantle 12 Tick이다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**수정** `Server/tests/ProjectH.Server.Tests/Shared/CollisionTests.cs`:

변경 전:

```csharp
    private static readonly Box[] Wall = { B(2f, 0f, -50f, 3f, 3f, 50f) };
    // Box 2 x 2 m footprint, x 2..4, 1 m high (reachable) or 1.5 m high (not reachable).
    private static readonly Box[] LowBox = { B(2f, 0f, -2f, 4f, 1f, 2f) };
    private static readonly Box[] HighBox = { B(2f, 0f, -2f, 4f, 1.5f, 2f) };
```

변경 후:

```csharp
    private static readonly Box[] Wall = { B(2f, 0f, -50f, 3f, 3f, 50f) };
    // Box 2 x 2 m footprint, x 2..4, 1 m high (reachable) or 2.5 m high (not reachable: above the Phase 12 mantle range,
    // MovementTuning.MantleMaxHeight 2.1 m; a 1.5 m box is mantled since Phase 12, VaultTests).
    private static readonly Box[] LowBox = { B(2f, 0f, -2f, 4f, 1f, 2f) };
    private static readonly Box[] HighBox = { B(2f, 0f, -2f, 4f, 2.5f, 2f) };
```

변경 전:

```csharp
            MovementSimulation.Step(ref s, jumpForward, Dt, HighBox, HeightField.Flat);
            Assert.True(s.Position.X + Hw <= 2f);   // never gets over the 1.5 m edge (apex 1.344 m)
```

변경 후:

```csharp
            MovementSimulation.Step(ref s, jumpForward, Dt, HighBox, HeightField.Flat);
            Assert.True(s.Position.X + Hw <= 2f);   // never gets over the 2.5 m edge (apex 1.344 m, no mantle above 2.1 m)
```

**수정** `Server/tests/ProjectH.Server.Tests/Shared/GameMapTests.cs`:

변경 전:

```csharp
                MovementSimulation.Step(ref b, input, Dt, Boxes, Terrain);
                Assert.False(MovementSimulation.OverlapsAny(a.Position, Boxes), $"walk {w} step {i}: overlaps at {a.Position}");
```

변경 후:

```csharp
                MovementSimulation.Step(ref b, input, Dt, Boxes, Terrain);
                // Phase 12 D8: a vault moves without collision (its path may cross the obstacle's edge); it ends clear.
                if (a.Mode != MovementMode.Vault)
                    Assert.False(MovementSimulation.OverlapsAny(a.Position, Boxes), $"walk {w} step {i}: overlaps at {a.Position}");
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Shared/VaultTests.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 D8 (spec §2 Mantle·Hurdle): the four checks, the two kinds and the constant-velocity move. The character
// walks or sprints along +X (yaw 90) towards a box whose near face is at x = 2.
public class VaultTests
{
    private const float Dt = 1f / 30f;
    private const float Hw = MoveSettings.HalfWidth;

    private static readonly InputCommand Walk = new() { MoveY = 1f, Yaw = 90f };
    private static readonly InputCommand Sprint = new() { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Sprint };

    private static Box B(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        => new(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));

    // A 2 x 2 m box (x 2..4) of the given height, and optionally more boxes.
    private static Box[] World(float height, params Box[] more)
    {
        var world = new Box[1 + more.Length];
        world[0] = B(2f, 0f, -1f, 4f, height, 1f);
        more.CopyTo(world, 1);
        return world;
    }

    private static InputCommand Jump(InputCommand input)
    {
        input.Buttons |= InputButtons.Jump;
        return input;
    }

    // Moves with input until the front face is within gap of x = 2 (at full speed from the first step: the ground sets
    // the velocity every tick), then presses jump once. Returns the state right after the jump tick.
    private static MoveState ApproachAndJump(Box[] world, InputCommand input, float gap = 0.5f, float startX = -3f)
    {
        var s = new MoveState { Position = new Vector3(startX, 0f, 0f), Yaw = 90f };
        for (int i = 0; i < 200 && 2f - (s.Position.X + Hw) > gap; i++) MovementSimulation.Step(ref s, input, Dt, world, HeightField.Flat);
        MovementSimulation.Step(ref s, Jump(input), Dt, world, HeightField.Flat);
        return s;
    }

    // The rest of the vault, then one standing tick: the vault ends MoveSettings.Skin above the surface and the ground
    // check of the next tick snaps the feet onto it.
    private static void RunVault(ref MoveState s, Box[] world, InputCommand input)
    {
        for (int i = 0; i < 30 && s.Mode == MovementMode.Vault; i++) MovementSimulation.Step(ref s, input, Dt, world, HeightField.Flat);
        MovementSimulation.Step(ref s, new InputCommand { Yaw = 90f }, Dt, world, HeightField.Flat);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void AMiddleBox_IsMantled_OntoItsTop_FortyCentimetresIn(float height)
    {
        Box[] world = World(height);
        MoveState s = ApproachAndJump(world, Walk);
        Assert.Equal(MovementMode.Vault, s.Mode);
        Assert.Equal(11, s.ModeTicks);   // 12 ticks (0.4 s), the first one already moved

        RunVault(ref s, world, Walk);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(height, s.Position.Y, 3);
        Assert.Equal(2f + MovementTuning.MantleInset, s.Position.X, 3);
        Assert.True(MovementSimulation.IsGrounded(s, world, HeightField.Flat));
        Assert.False(MovementSimulation.OverlapsAny(s.Position, world));
    }

    [Fact]
    public void ALowBox_AtSprintSpeed_IsHurdled_ToTheGroundBehindIt()
    {
        Box[] world = World(1.0f);
        MoveState s = ApproachAndJump(world, Sprint);
        Assert.Equal(MovementMode.Vault, s.Mode);
        Assert.Equal(5, s.ModeTicks);   // 6 ticks (0.2 s)

        RunVault(ref s, world, Sprint);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(0f, s.Position.Y, 3);
        Assert.Equal(4f + Hw + MovementTuning.HurdleLandingGap, s.Position.X, 3);
        Assert.False(MovementSimulation.OverlapsAny(s.Position, world));
    }

    [Fact]
    public void ALowBox_AtWalkingSpeed_IsANormalJump()
    {
        Box[] world = World(1.0f);
        MoveState s = ApproachAndJump(world, Walk);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);
    }

    [Fact]
    public void AThreeMetreWall_CannotBeVaulted()
    {
        Box[] world = World(3.0f);
        MoveState s = ApproachAndJump(world, Sprint);
        Assert.Equal(MovementMode.Ground, s.Mode);
        for (int i = 0; i < 60; i++)
        {
            MovementSimulation.Step(ref s, Jump(Sprint), Dt, world, HeightField.Flat);
            Assert.NotEqual(MovementMode.Vault, s.Mode);
            Assert.True(s.Position.X + Hw <= 2f);
        }
    }

    [Fact]
    public void TooFarAway_IsANormalJump()
    {
        Box[] world = World(1.5f);
        MoveState s = ApproachAndJump(world, Walk, gap: 1.2f);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);
    }

    [Fact]
    public void NoRoomOnTheTop_IsNoMantle()
    {
        // A slab 2.5 m up over the box: standing on the 1.5 m top would need 3.3 m.
        Box[] world = World(1.5f, B(2f, 2.5f, -1f, 4f, 3f, 1f));
        MoveState s = ApproachAndJump(world, Walk);
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void NoRoomBehindAHurdle_StandsOnTheTop()
    {
        // A wall right behind the low box.
        Box[] world = World(1.0f, B(4.2f, 0f, -3f, 5f, 3f, 3f));
        MoveState s = ApproachAndJump(world, Sprint);
        Assert.Equal(MovementMode.Vault, s.Mode);
        RunVault(ref s, world, Sprint);
        Assert.Equal(1f, s.Position.Y, 3);
        Assert.Equal(2f + MovementTuning.MantleInset, s.Position.X, 3);
    }

    [Fact]
    public void ADeepLowBox_IsNotHurdled_ButStoodOn()
    {
        Box[] world = { B(2f, 0f, -1f, 6f, 1f, 1f) };   // 4 m deep
        MoveState s = ApproachAndJump(world, Sprint);
        RunVault(ref s, world, Sprint);
        Assert.Equal(1f, s.Position.Y, 3);
        Assert.InRange(s.Position.X, 2f, 6f);
    }

    [Fact]
    public void InTheAir_OrCrouched_OrWithoutMovingForward_ThereIsNoVault()
    {
        Box[] world = World(1.5f);
        // Crouched: crouch then jump next to the box. The jump stands it up (there is room) and is a normal jump.
        var crouch = new InputCommand { MoveY = 1f, Yaw = 90f, Buttons = InputButtons.Crouch };
        MoveState s = ApproachAndJump(world, crouch, gap: 0.3f);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(s.VelocityY > 0f);

        // In the air: a jump far from the box, then the jump button held while it reaches the box.
        s = new MoveState { Position = new Vector3(0.6f, 0f, 0f), Yaw = 90f };
        MovementSimulation.Step(ref s, Jump(Walk), Dt, world, HeightField.Flat);
        for (int i = 0; i < 10; i++)
        {
            MovementSimulation.Step(ref s, Jump(Walk), Dt, world, HeightField.Flat);
            Assert.NotEqual(MovementMode.Vault, s.Mode);
        }

        // Strafing (no forward input) next to the box.
        s = new MoveState { Position = new Vector3(2f - Hw - 0.3f, 0f, 0f), Yaw = 0f };
        MovementSimulation.Step(ref s, new InputCommand { MoveX = 1f, Yaw = 0f, Buttons = InputButtons.Jump }, Dt, world, HeightField.Flat);
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void DuringAVault_ThePositionMovesTheSameAmountEveryTick_AndInputIsIgnored()
    {
        Box[] world = World(1.5f);
        MoveState s = ApproachAndJump(world, Walk);
        Vector3 previous = s.Position;
        MovementSimulation.Step(ref s, Walk, Dt, world, HeightField.Flat);
        Vector3 delta = s.Position - previous;
        while (s.Mode == MovementMode.Vault)
        {
            previous = s.Position;
            var odd = new InputCommand { MoveX = -1f, MoveY = -1f, Yaw = 200f, Buttons = InputButtons.Jump | InputButtons.Crouch | InputButtons.Sprint };
            MovementSimulation.Step(ref s, odd, Dt, world, HeightField.Flat);
            if (s.Mode == MovementMode.Vault || s.ModeTicks == 0)
            {
                Vector3 step = s.Position - previous;
                Assert.Equal(delta.X, step.X, 4);
                Assert.Equal(delta.Y, step.Y, 4);
                Assert.Equal(delta.Z, step.Z, 4);
            }
        }
        Assert.Equal(1.5f + MoveSettings.Skin, s.Position.Y, 4);
    }

    [Fact]
    public void TheVaultState_ReplaysFromVelocitiesAndModeTicks()
    {
        // What a client gets in a snapshot (position, both velocities, mode, ModeTicks) continues the vault exactly.
        Box[] world = World(1.5f);
        MoveState s = ApproachAndJump(world, Walk);
        MovementSimulation.Step(ref s, Walk, Dt, world, HeightField.Flat);
        var copy = new MoveState
        {
            Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw, Mode = s.Mode,
            HorizontalVelocity = s.HorizontalVelocity, ModeTicks = s.ModeTicks,
        };
        RunVault(ref s, world, Walk);
        RunVault(ref copy, world, Walk);
        Assert.Equal(s.Position, copy.Position);
        Assert.Equal(s.Mode, copy.Mode);
    }

    // The map's own boxes (spec §2: "1.5 m·2.0 m 상자 → Mantle, 1.0 m 상자 → Hurdle").
    [Fact]
    public void OnTheMap_TheGearworksCrates_AreHurdledAndMantled()
    {
        // Low crate (36, 0.5, 40): x 35..37. High crate (34, 0.75, 60): x 33..35. Approach both along +X.
        Assert.True(VaultsOnMap(new Vector3(31f, 0f, 40f), 35f, Sprint, out MoveState low));
        Assert.Equal(MoveSettings.Skin, low.Position.Y - GameMap.Terrain.Height(low.Position.X, low.Position.Z), 4);
        Assert.True(low.Position.X > 37f);

        Assert.True(VaultsOnMap(new Vector3(29f, 0f, 60f), 33f, Walk, out MoveState high));
        Assert.Equal(1.5f + MoveSettings.Skin, high.Position.Y, 4);
        Assert.InRange(high.Position.X, 33f, 35f);
    }

    private static bool VaultsOnMap(Vector3 start, float faceX, InputCommand input, out MoveState s)
    {
        s = new MoveState { Position = new Vector3(start.X, GameMap.Terrain.Height(start.X, start.Z), start.Z), Yaw = 90f };
        bool vaulted = false;
        for (int i = 0; i < 120; i++)
        {
            // Jump every tick within half a metre of the crate's face: the first press starts the vault.
            InputCommand step = faceX - (s.Position.X + Hw) < 0.5f ? Jump(input) : input;
            MovementSimulation.Step(ref s, step, Dt, GameMap.Boxes, GameMap.Terrain);
            if (s.Mode == MovementMode.Vault) vaulted = true;
            if (vaulted && s.Mode != MovementMode.Vault) break;
        }
        return vaulted;
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~VaultTests|FullyQualifiedName~CollisionTests"`
Expected: `VaultTests`의 Vault 테스트가 실패한다(아직 보통 점프다). `CollisionTests`는 통과한다.

- [ ] **Step 3: Vault를 구현한다**

**수정** `Shared/Runtime/Simulation/MovementSimulation.cs`:

변경 전:

```csharp
            StepGround(ref state, input.Buttons, move, deltaTime, world, terrain, ref result);
        }

        // Ground, Crouch and Slide (D7), walking, sprinting, jumping and falling (D3 air momentum).
        // move: the input direction in world X/Z, length 0..1.
        private static void StepGround(ref MoveState state, InputButtons buttons, Vector2 move, float deltaTime, ReadOnlySpan<Box> world, HeightField terrain, ref StepResult result)
        {
```

변경 후:

```csharp
            if (state.Mode == MovementMode.Vault)
            {
                StepVault(ref state, deltaTime);
                return;
            }
            // D8: a vault may start on a jump press while moving forward; it faces where the character looks.
            var forward = new Vector2(sin, cos);
            StepGround(ref state, input.Buttons, move, moveY > 0f ? forward : Vector2.Zero, deltaTime, world, terrain, ref result);
        }

        // Ground, Crouch and Slide (D7), walking, sprinting, jumping and falling (D3 air momentum).
        // move: the input direction in world X/Z, length 0..1. vaultDirection: the facing direction when the input moves
        // forward (a vault may start), else zero.
        private static void StepGround(ref MoveState state, InputButtons buttons, Vector2 move, Vector2 vaultDirection, float deltaTime,
            ReadOnlySpan<Box> world, HeightField terrain, ref StepResult result)
        {
```

변경 전:

```csharp
            // 6) Jump. From a crouch only where the standing box fits; a slide jump keeps the slide's speed; a sprint jump
            //    takes off faster (D3) at the same height.
            bool walking = grounded;
```

변경 후:

```csharp
            // 6) Jump. On the ground, moving forward, an obstacle ahead turns it into a vault (D8), which moves this tick
            //    already. Otherwise a jump: from a crouch only where the standing box fits; a slide jump keeps the slide's
            //    speed; a sprint jump takes off faster (D3) at the same height.
            if (grounded && jump && state.Mode == MovementMode.Ground && vaultDirection != Vector2.Zero &&
                TryStartVault(ref state, position, vaultDirection, world, terrain, deltaTime))
            {
                state.Position = position;
                StepVault(ref state, deltaTime);
                return;
            }
            bool walking = grounded;
```

변경 전:

```csharp
            state.HorizontalVelocity = velocity;
```

변경 후:

```csharp
            state.HorizontalVelocity = velocity;
        }

        // D8: a vault starts only when all four checks pass: (1) an obstacle within VaultReach ahead, standing on the
        // feet's level, (2) its top in hurdle or mantle range, (3) room to stand at the destination, (4) the destination
        // inside no box and above the terrain. A low obstacle is hurdled only at sprint speed (else this is a normal jump);
        // a hurdle lands HurdleLandingGap past the far side, or on the top when the obstacle is deeper than HurdleMaxDepth
        // or there is no room behind it. A mantle stands MantleInset inside the top's edge. The vault then moves at one
        // constant velocity for its ticks, so the snapshot's velocities and ModeTicks are all a replay needs.
        // One linear pass over the boxes, on the jump press only.
        private static bool TryStartVault(ref MoveState state, Vector3 feet, Vector2 direction, ReadOnlySpan<Box> world, HeightField terrain,
            float deltaTime)
        {
            int obstacle = -1;
            float nearest = MovementTuning.VaultReach;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                float rise = box.Max.Y - feet.Y;
                if (rise < MovementTuning.HurdleMinHeight || rise > MovementTuning.MantleMaxHeight) continue;
                if (box.Min.Y > feet.Y + MovementTuning.VaultBaseTolerance) continue;   // not standing on our level
                // When the standing footprint moving along direction would touch the box.
                if (!RayBox2D(feet.X, feet.Z, direction, box.Min.X - MoveSettings.HalfWidth, box.Min.Z - MoveSettings.HalfWidth,
                        box.Max.X + MoveSettings.HalfWidth, box.Max.Z + MoveSettings.HalfWidth, out float enter, out _)) continue;
                if (enter < -MoveSettings.Skin || enter > nearest) continue;
                nearest = enter;
                obstacle = i;
            }
            if (obstacle < 0) return false;

            Box target = world[obstacle];
            float height = target.Max.Y - feet.Y;
            bool hurdle = height <= MovementTuning.HurdleMaxHeight;
            if (hurdle && state.HorizontalVelocity.Length() < MovementTuning.HurdleMinSpeed) return false;
            // Where the line through the feet crosses the obstacle itself (a corner graze is no vault).
            if (!RayBox2D(feet.X, feet.Z, direction, target.Min.X, target.Min.Z, target.Max.X, target.Max.Z, out float face, out float back))
                return false;

            Vector3 destination = default;
            bool found = false;
            if (hurdle && back - face <= MovementTuning.HurdleMaxDepth)
            {
                float reach = back + MoveSettings.HalfWidth + MovementTuning.HurdleLandingGap;
                float x = feet.X + direction.X * reach;
                float z = feet.Z + direction.Y * reach;
                destination = new Vector3(x, terrain.Height(x, z), z);
                found = IsFreeStand(destination, world, terrain);
            }
            if (!found)
            {
                float reach = MathF.Min(face + MovementTuning.MantleInset, (face + back) * 0.5f);
                destination = new Vector3(feet.X + direction.X * reach, target.Max.Y, feet.Z + direction.Y * reach);
                found = IsFreeStand(destination, world, terrain);
            }
            if (!found) return false;
            // Skin above the surface: the constant-velocity sum may land a hair low, and the next ground check snaps the
            // feet onto it anyway.
            destination.Y += MoveSettings.Skin;

            byte ticks = TicksOf(hurdle ? MovementTuning.HurdleSeconds : MovementTuning.MantleSeconds, deltaTime);
            float seconds = ticks * deltaTime;
            state.Mode = MovementMode.Vault;
            state.ModeTicks = ticks;
            state.HorizontalVelocity = new Vector2(destination.X - feet.X, destination.Z - feet.Z) / seconds;
            state.VelocityY = (destination.Y - feet.Y) / seconds;
            return true;
        }

        // The standing box fits at these feet: no box overlaps it and the terrain is not above the feet.
        private static bool IsFreeStand(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain) =>
            feet.Y >= terrain.Height(feet.X, feet.Z) - MoveSettings.GroundProbe && !OverlapsAny(feet, MoveSettings.Height, world);

        // 2D ray from (x, z) along a unit direction against an X/Z rectangle: the distances where it enters and leaves.
        // False when it misses or the rectangle is behind.
        private static bool RayBox2D(float x, float z, Vector2 direction, float minX, float minZ, float maxX, float maxZ, out float enter, out float leave)
        {
            enter = float.NegativeInfinity;
            leave = float.PositiveInfinity;
            if (!Slab(x, direction.X, minX, maxX, ref enter, ref leave)) return false;
            if (!Slab(z, direction.Y, minZ, maxZ, ref enter, ref leave)) return false;
            return leave >= 0f && enter <= leave;
        }

        private static bool Slab(float origin, float direction, float min, float max, ref float enter, ref float leave)
        {
            if (MathF.Abs(direction) < 1e-6f) return origin > min && origin < max;
            float t1 = (min - origin) / direction;
            float t2 = (max - origin) / direction;
            if (t1 > t2)
            {
                float swap = t1;
                t1 = t2;
                t2 = swap;
            }
            if (t1 > enter) enter = t1;
            if (t2 < leave) leave = t2;
            return enter <= leave;
        }

        // D8: one tick of a vault: the constant velocity set at its start, no collision (the path was checked then). The
        // last tick ends it standing in Ground mode.
        private static void StepVault(ref MoveState state, float deltaTime)
        {
            if (state.ModeTicks > 0)
            {
                state.Position += new Vector3(state.HorizontalVelocity.X, state.VelocityY, state.HorizontalVelocity.Y) * deltaTime;
                state.ModeTicks--;
            }
            if (state.ModeTicks > 0) return;
            state.Mode = MovementMode.Ground;
            state.VelocityY = 0f;
            state.HorizontalVelocity = Vector2.Zero;
```

**수정** `Shared/Runtime/Simulation/MovementTuning.cs`:

변경 전:

```csharp
        public const float VaultReach = 0.8f;
```

변경 후:

```csharp
        public const float VaultReach = 0.8f;
        // An obstacle counts when its bottom is at most this far above the feet (it stands on the character's level, also
        // when the character comes up a slope).
        public const float VaultBaseTolerance = 0.3f;
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 940개(931 통과, 9개 건너뜀)
Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

- [ ] **Step 5: Commit** — `feat(shared): add mantle and hurdle (Phase 12 D8)`

---

### Task 3: 투입 이동 (수송기 경로, 탑승, 자유 낙하, 글라이더)

**Files:**
- Create: `Shared/Runtime/Simulation/DropTransport.cs`, `Server/src/ProjectH.Server/Game/Flow/DropPlanner.cs`
- Modify: `Shared/Runtime/Simulation/MovementSimulation.cs`, `MovementTuning.cs`
- Test: Create `Server/tests/ProjectH.Server.Tests/Shared/DeploymentMovementTests.cs`

**Interfaces:**
- Produces(Shared):
  - `struct DropRoute`
    - 필드: `StartX`, `StartZ`, `EndX`, `EndZ`, `Altitude`, `uint StartTick`, `uint DurationTicks`
    - `EndTick`, `Vector3 PositionAt(double tick)`(양 끝에서 멈춘다)
    - `JumpWindow(out uint first, out uint last)`: 수송기가 ±70 m 안에 있는 Tick
  - `static DropTransport.Ride(ref MoveState, in InputCommand, in DropRoute, uint tick)` → `bool`
    - 탑승 중이 아니면 `false`를 돌려주고 아무것도 하지 않는다.
    - 탑승 중이면 시점을 바꾸고 경로 위치에 둔다. 구간 안의 Jump나 구간 끝이면 `Freefall`이 된다.
  - `MovementSimulation.GroundDistance(Vector3, ReadOnlySpan<Box>, HeightField)`
  - `MovementSimulation.ApplyYaw`(internal)
  - `MovementTuning`: 자유 낙하·글라이더·수송기 수치
- Produces(Server): `static DropPlanner.Plan(int seed, uint startTick, int simHz)` → `DropRoute`. 맵 중심을 지나는 무작위 방향이고, 경계를 지나는 점에서 경로를 따라 20 m 밖까지 간다(Spec과 다른 점 1, 2).

호출하는 쪽(서버 `Match`, Task 5. Client 예측, Task 8)은 다음처럼 쓴다. 그래서 뛰어내린 Space가 같은 Tick에 글라이더를 펴지 않는다.

```csharp
if (!DropTransport.Ride(ref state, input, route, tick)) MovementSimulation.Step(...);
```

공중 모드는 바깥벽 위치(±`GameMap.HalfSize`)를 넘지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Shared/DeploymentMovementTests.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Server.Game.Flow;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 D5, D6 (spec §2 공중 투입): the route, riding it, jumping, freefall, the glider and the landing, on the
// shared simulation alone. Match integration is MatchDeploymentTests.
public class DeploymentMovementTests
{
    private const float Dt = 1f / 30f;
    private const int SimHz = 30;

    private static readonly InputCommand Idle = new();
    private static readonly InputCommand JumpPress = new() { Buttons = InputButtons.Jump };

    // A route along +X at 90 m: x -100..100 over 300 ticks, starting at tick 1000.
    private static DropRoute AlongX() => new()
    {
        StartX = -100f, StartZ = 0f, EndX = 100f, EndZ = 0f, Altitude = 90f, StartTick = 1000, DurationTicks = 300,
    };

    private static MoveState Freefalling(Vector3 at) => new() { Position = at, Mode = MovementMode.Freefall };

    // ---- Route (D5) ----

    [Fact]
    public void ThePlannedRoute_GoesThroughTheCentre_FromOutsideToOutside_AtTwentyMetresPerSecond()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            DropRoute route = DropPlanner.Plan(seed, 500, SimHz);
            Vector3 middle = route.PositionAt(route.StartTick + route.DurationTicks / 2.0);
            Assert.InRange(MathF.Abs(middle.X), 0f, 0.5f);
            Assert.InRange(MathF.Abs(middle.Z), 0f, 0.5f);
            Assert.Equal(MovementTuning.TransportAltitude, middle.Y);
            // Each end is TransportOutsideMargin along the route beyond where it crosses the outer wall.
            float length = Vector2.Distance(new Vector2(route.StartX, route.StartZ), new Vector2(route.EndX, route.EndZ));
            float axis = MathF.Max(MathF.Abs(route.EndX), MathF.Abs(route.EndZ)) / (length * 0.5f);   // max(|dx|, |dz|)
            Assert.Equal(GameMap.HalfSize / axis + MovementTuning.TransportOutsideMargin, length * 0.5f, 2);
            Assert.True(MathF.Max(MathF.Abs(route.StartX), MathF.Abs(route.StartZ)) > GameMap.HalfSize + 10f);
            Assert.Equal(length / MovementTuning.TransportSpeed * SimHz, route.DurationTicks, 0.999);
        }
    }

    [Fact]
    public void TheSameSeed_GivesTheSameRoute_AndSeedsDiffer()
    {
        Assert.Equal(DropPlanner.Plan(7, 10, SimHz), DropPlanner.Plan(7, 10, SimHz));
        Assert.NotEqual(DropPlanner.Plan(7, 10, SimHz).EndX, DropPlanner.Plan(8, 10, SimHz).EndX);
    }

    [Fact]
    public void AnAxisRoute_IsTwoHundredMetres_TenSeconds()
    {
        DropRoute route = AlongX();
        Assert.Equal(new Vector3(-100f, 90f, 0f), route.PositionAt(route.StartTick));
        Assert.Equal(new Vector3(0f, 90f, 0f), route.PositionAt(route.StartTick + 150));
        Assert.Equal(new Vector3(100f, 90f, 0f), route.PositionAt(route.EndTick + 50));   // held at the end
        Assert.Equal(new Vector3(-100f, 90f, 0f), route.PositionAt(0));                  // and before the start
    }

    [Fact]
    public void TheJumpWindow_IsWhereTheTransportIsTenMetresInsideTheWalls()
    {
        DropRoute route = AlongX();
        route.JumpWindow(out uint first, out uint last);
        // x = -70 at 30/200 of the route (tick 1045), x = +70 at 170/200 (tick 1255).
        Assert.Equal(1045u, first);
        Assert.Equal(1255u, last);
        Assert.True(route.PositionAt(first - 1).X < -70f);
        Assert.True(route.PositionAt(last + 1).X > 70f);

        // A diagonal route: the square's corner, about 99 m from the centre.
        DropRoute diagonal = DropPlanner.Plan(0, 0, SimHz);
        diagonal.JumpWindow(out first, out last);
        for (uint t = first; t <= last; t++)
        {
            Vector3 p = diagonal.PositionAt(t);
            Assert.True(MathF.Abs(p.X) <= 70.01f && MathF.Abs(p.Z) <= 70.01f);
        }
    }

    // ---- Riding and jumping (D5, D6) ----

    [Fact]
    public void Ride_PlacesTheRiderOnTheRoute_AndIgnoresOtherModes()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        Assert.True(DropTransport.Ride(ref s, new InputCommand { Yaw = 45f }, route, 1100));
        Assert.Equal(route.PositionAt(1100), s.Position);
        Assert.Equal(45f, s.Yaw);
        Assert.Equal(MovementMode.Transport, s.Mode);

        var walker = new MoveState { Position = new Vector3(1f, 0f, 2f) };
        Assert.False(DropTransport.Ride(ref walker, JumpPress, route, 1100));
        Assert.Equal(new Vector3(1f, 0f, 2f), walker.Position);
    }

    [Fact]
    public void AJump_BeforeTheWindow_IsIgnored_InsideItDropsIntoFreefall()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        DropTransport.Ride(ref s, JumpPress, route, 1040);
        Assert.Equal(MovementMode.Transport, s.Mode);
        DropTransport.Ride(ref s, JumpPress, route, 1100);
        Assert.Equal(MovementMode.Freefall, s.Mode);
        Assert.Equal(route.PositionAt(1100), s.Position);
        Assert.Equal(Vector2.Zero, s.HorizontalVelocity);
        Assert.False(DropTransport.Ride(ref s, JumpPress, route, 1101));   // no longer riding: a second jump does nothing here
    }

    [Fact]
    public void TheJumpPress_DoesNotAlsoOpenTheGlider()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        if (!DropTransport.Ride(ref s, JumpPress, route, 1100)) MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Freefall, s.Mode);
    }

    [Fact]
    public void AtTheWindowsEnd_TheRiderIsDropped()
    {
        DropRoute route = AlongX();
        var s = new MoveState { Mode = MovementMode.Transport };
        for (uint tick = 1000; tick < 1255; tick++)
        {
            DropTransport.Ride(ref s, Idle, route, tick);
            Assert.Equal(MovementMode.Transport, s.Mode);
        }
        DropTransport.Ride(ref s, Idle, route, 1255);
        Assert.Equal(MovementMode.Freefall, s.Mode);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(200.0)]
    public void ARiderWhoSendsNothing_IsDropped_AndLandsInsideTheWalls(double degrees)
    {
        // D16: a graced rider coasts on empty input: dropped at the window's end, falls straight, glides down.
        float dx = (float)Math.Sin(degrees * Math.PI / 180.0);
        float dz = (float)Math.Cos(degrees * Math.PI / 180.0);
        float half = GameMap.HalfSize / MathF.Max(MathF.Abs(dx), MathF.Abs(dz)) + MovementTuning.TransportOutsideMargin;
        var route = new DropRoute
        {
            StartX = -dx * half, StartZ = -dz * half, EndX = dx * half, EndZ = dz * half, Altitude = 90f,
            StartTick = 0, DurationTicks = (uint)MathF.Ceiling(2f * half / MovementTuning.TransportSpeed * SimHz),
        };
        var s = new MoveState { Mode = MovementMode.Transport };
        uint tick = 0;
        for (; tick < 2000 && s.Mode != MovementMode.Ground; tick++)
        {
            if (!DropTransport.Ride(ref s, Idle, route, tick)) MovementSimulation.Step(ref s, Idle, Dt, GameMap.Boxes, GameMap.Terrain);
        }
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.True(MathF.Abs(s.Position.X) < GameMap.HalfSize && MathF.Abs(s.Position.Z) < GameMap.HalfSize, $"landed at {s.Position}");
        Assert.False(MovementSimulation.OverlapsAny(s.Position, GameMap.Boxes));
    }

    // ---- Freefall (D6) ----

    [Fact]
    public void Freefall_ReachesThirtyMetresPerSecond_AndNoMore()
    {
        MoveState s = Freefalling(new Vector3(0f, 2000f, 0f));
        for (int i = 0; i < 90; i++) MovementSimulation.Step(ref s, Idle, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(-MovementTuning.FreefallTerminalSpeed, s.VelocityY);
        Assert.Equal(MovementMode.Freefall, s.Mode);
    }

    [Theory]
    [InlineData(0f, 1f, 15f)]    // forward
    [InlineData(1f, 0f, 10f)]    // side
    [InlineData(0f, -1f, 6f)]    // back
    public void Freefall_Steering_HasForwardSideAndBackLimits(float moveX, float moveY, float limit)
    {
        MoveState s = Freefalling(new Vector3(0f, 2000f, 0f));
        var input = new InputCommand { MoveX = moveX, MoveY = moveY, Yaw = 30f };
        MovementSimulation.Step(ref s, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementTuning.FreefallAcceleration * Dt, s.HorizontalVelocity.Length(), 4);
        for (int i = 0; i < 60; i++) MovementSimulation.Step(ref s, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(limit, s.HorizontalVelocity.Length(), 3);
    }

    [Fact]
    public void Freefall_AndGlide_IgnoreCrouch_AndNeverSlide()
    {
        MoveState s = Freefalling(new Vector3(0f, 2000f, 0f));
        var crouch = new InputCommand { MoveY = 1f, Buttons = InputButtons.Crouch | InputButtons.Sprint };
        for (int i = 0; i < 30; i++) MovementSimulation.Step(ref s, crouch, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Freefall, s.Mode);
        MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        for (int i = 0; i < 30; i++) MovementSimulation.Step(ref s, crouch, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Glide, s.Mode);
    }

    // ---- Glide (D6) ----

    [Fact]
    public void AJumpPress_InFreefall_OpensTheGlider_ThatNeverCloses()
    {
        MoveState s = Freefalling(new Vector3(0f, 500f, 0f));
        MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Glide, s.Mode);
        Assert.Equal(-MovementTuning.GlideFallSpeed, s.VelocityY);
        for (int i = 0; i < 30; i++) MovementSimulation.Step(ref s, JumpPress, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(MovementMode.Glide, s.Mode);
    }

    [Theory]
    [InlineData(0f, 1f, 14f)]
    [InlineData(-1f, 0f, 10f)]
    [InlineData(0f, -1f, 4f)]
    public void Glide_Steering_HasForwardSideAndBackLimits(float moveX, float moveY, float limit)
    {
        var s = new MoveState { Position = new Vector3(0f, 500f, 0f), Mode = MovementMode.Glide };
        var input = new InputCommand { MoveX = moveX, MoveY = moveY, Yaw = 300f };
        for (int i = 0; i < 90; i++) MovementSimulation.Step(ref s, input, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat);
        Assert.Equal(limit, s.HorizontalVelocity.Length(), 3);
        Assert.Equal(-MovementTuning.GlideFallSpeed, s.VelocityY);
    }

    [Fact]
    public void TheGlider_OpensItself_ThirtyMetresAboveTheTerrain()
    {
        MoveState s = Freefalling(new Vector3(44f, 90f, -44f));   // over the Lookout plateau (6 m)
        float terrain = GameMap.Terrain.Height(44f, -44f);
        while (s.Mode == MovementMode.Freefall) MovementSimulation.Step(ref s, Idle, Dt, GameMap.Boxes, GameMap.Terrain);
        Assert.Equal(MovementMode.Glide, s.Mode);
        float height = s.Position.Y - terrain;
        Assert.InRange(height, MovementTuning.GlideAutoDeployHeight - 1.1f, MovementTuning.GlideAutoDeployHeight);
    }

    [Fact]
    public void TheGlider_OpensItself_ThirtyMetresAboveABoxTop()
    {
        Box[] tower = { new(new Vector3(-5f, 0f, -5f), new Vector3(5f, 40f, 5f)) };
        MoveState s = Freefalling(new Vector3(0f, 100f, 0f));
        while (s.Mode == MovementMode.Freefall) MovementSimulation.Step(ref s, Idle, Dt, tower, HeightField.Flat);
        Assert.InRange(s.Position.Y - 40f, MovementTuning.GlideAutoDeployHeight - 1.1f, MovementTuning.GlideAutoDeployHeight);
    }

    [Fact]
    public void GlideLanding_IsGroundMode_WithNoHorizontalVelocity_AndNoLandingSpeed()
    {
        var s = new MoveState { Position = new Vector3(0f, 3f, 0f), Mode = MovementMode.Glide, HorizontalVelocity = new Vector2(10f, 0f) };
        StepResult result = default;
        for (int i = 0; i < 40 && s.Mode == MovementMode.Glide; i++)
            MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = 90f }, Dt, ReadOnlySpan<Box>.Empty, HeightField.Flat, out result);
        Assert.Equal(MovementMode.Ground, s.Mode);
        Assert.Equal(Vector2.Zero, s.HorizontalVelocity);
        Assert.Equal(0f, s.Position.Y);
        Assert.Equal(0f, result.LandingSpeed);
        Assert.True(MovementSimulation.IsGrounded(s, ReadOnlySpan<Box>.Empty, HeightField.Flat));
    }

    [Fact]
    public void Gliding_IntoTheOuterWall_StaysInside()
    {
        var s = new MoveState { Position = new Vector3(75f, 20f, 0f), Mode = MovementMode.Glide };
        var east = new InputCommand { MoveY = 1f, Yaw = 90f };
        for (int i = 0; i < 200 && s.Mode == MovementMode.Glide; i++)
        {
            MovementSimulation.Step(ref s, east, Dt, GameMap.Boxes, GameMap.Terrain);
            Assert.True(s.Position.X < GameMap.HalfSize);
        }
        Assert.Equal(MovementMode.Ground, s.Mode);
    }

    [Fact]
    public void ATransportRider_StepsOnlyItsYaw()
    {
        var s = new MoveState { Position = new Vector3(1f, 90f, 2f), Mode = MovementMode.Transport };
        MovementSimulation.Step(ref s, new InputCommand { MoveY = 1f, Yaw = 10f, Buttons = InputButtons.Jump }, Dt, GameMap.Boxes, GameMap.Terrain);
        Assert.Equal(new Vector3(1f, 90f, 2f), s.Position);
        Assert.Equal(10f, s.Yaw);
        Assert.Equal(MovementMode.Transport, s.Mode);
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 컴파일 실패(`DropRoute`, `DropTransport`, `DropPlanner`가 없다)

- [ ] **Step 3: 투입 이동을 구현한다**

**새 파일** `Server/src/ProjectH.Server/Game/Flow/DropPlanner.cs`:

```csharp
using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Flow;

// Phase 12 D5: plans this match's drop transport route. Server only (the seed is a rule, game-core-rules §4); the
// client gets the result in TransportRoute. A straight line through the map's centre in a direction rolled from the
// seed, starting and ending TransportOutsideMargin outside the outer walls (so its length depends on the direction:
// 200 m along an axis, about 246 m on a diagonal), flown at TransportSpeed. One Random per match start.
public static class DropPlanner
{
    public static DropRoute Plan(int seed, uint startTick, int simHz)
    {
        var rng = new Random(seed);
        double angle = rng.NextDouble() * 2.0 * Math.PI;
        float dx = (float)Math.Sin(angle);
        float dz = (float)Math.Cos(angle);
        // Distance from the centre to the outer wall along the direction, plus the margin outside it.
        float half = GameMap.HalfSize / MathF.Max(MathF.Abs(dx), MathF.Abs(dz)) + MovementTuning.TransportOutsideMargin;
        uint duration = (uint)Math.Max(1, (int)Math.Ceiling(2f * half / MovementTuning.TransportSpeed * simHz));
        return new DropRoute
        {
            StartX = -dx * half,
            StartZ = -dz * half,
            EndX = dx * half,
            EndZ = dz * half,
            Altitude = MovementTuning.TransportAltitude,
            StartTick = startTick,
            DurationTicks = duration,
        };
    }
}
```

**새 파일** `Shared/Runtime/Simulation/DropTransport.cs`:

```csharp
using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 12 D5: the drop transport's straight route at a fixed altitude. The server plans it at the match start and
    // sends it once (TransportRoute); from then on both sides compute where the transport is from these values with the
    // same code, so the transport is never sent per tick. Pure math, no allocation.
    public struct DropRoute
    {
        public float StartX;
        public float StartZ;
        public float EndX;
        public float EndZ;
        public float Altitude;
        public uint StartTick;
        public uint DurationTicks;   // at least 1

        public uint EndTick => StartTick + DurationTicks;

        // Where the transport is at a (fractional) server tick: StartTick..EndTick along the line, held at the ends.
        public Vector3 PositionAt(double tick)
        {
            double t = DurationTicks == 0 ? 1.0 : (tick - StartTick) / DurationTicks;
            if (!(t > 0.0)) t = 0.0;   // also NaN
            if (t > 1.0) t = 1.0;
            float s = (float)t;
            return new Vector3(StartX + (EndX - StartX) * s, Altitude, StartZ + (EndZ - StartZ) * s);
        }

        // D5: the ticks while the transport is at least TransportJumpMargin inside the outer walls. A jump counts only
        // from first on; whoever is still aboard at last is dropped. A route that never enters that square (only a
        // broken route can) gives first = last = EndTick.
        public void JumpWindow(out uint first, out uint last)
        {
            const float limit = GameMap.HalfSize - MovementTuning.TransportJumpMargin;
            double enter = 0.0;
            double leave = 1.0;
            Clip(StartX, EndX - StartX, limit, ref enter, ref leave);
            Clip(StartZ, EndZ - StartZ, limit, ref enter, ref leave);
            if (DurationTicks == 0 || enter > leave)
            {
                first = EndTick;
                last = EndTick;
                return;
            }
            first = StartTick + (uint)Math.Ceiling(enter * DurationTicks);
            last = StartTick + (uint)Math.Floor(leave * DurationTicks);
            if (last < first) last = first;
        }

        // Narrows [enter, leave] (fractions of the route) to where start + delta * t is within +-limit.
        private static void Clip(float start, float delta, float limit, ref double enter, ref double leave)
        {
            if (Math.Abs(delta) < 1e-6f)
            {
                if (start < -limit || start > limit) enter = 2.0;   // never inside
                return;
            }
            double a = (-limit - start) / (double)delta;
            double b = (limit - start) / (double)delta;
            if (a > b)
            {
                double swap = a;
                a = b;
                b = swap;
            }
            if (a > enter) enter = a;
            if (b < leave) leave = b;
        }
    }

    // D5, D6: riding the transport. Server and prediction call Ride for a character in Transport mode instead of
    // MovementSimulation.Step, with the server tick that input is simulated at.
    public static class DropTransport
    {
        // Places a rider on the route at tick and lets it look around. A jump press inside the jump window, or the end of
        // the window, drops it into Freefall right there with no velocity; the fall starts with the next tick's Step, so
        // the same press does not also open the glider. Returns false (and does nothing) when the character is not riding.
        public static bool Ride(ref MoveState state, in InputCommand input, in DropRoute route, uint tick)
        {
            if (state.Mode != MovementMode.Transport) return false;
            MovementSimulation.ApplyYaw(ref state, input.Yaw);
            state.Position = route.PositionAt(tick);
            state.VelocityY = 0f;
            state.HorizontalVelocity = Vector2.Zero;
            route.JumpWindow(out uint first, out uint last);
            bool jump = (input.Buttons & InputButtons.Jump) != 0 && tick >= first;
            if (jump || tick >= last) state.Mode = MovementMode.Freefall;
            return true;
        }
    }
}
```

**수정** `Shared/Runtime/Simulation/MovementSimulation.cs`:

변경 전:

```csharp
            }

            if (IsFinite(input.Yaw)) state.Yaw = NormalizeYaw(input.Yaw);
```

변경 후:

```csharp
            }

            ApplyYaw(ref state, input.Yaw);
```

변경 전:

```csharp
            if (state.Mode == MovementMode.Vault)
            {
                StepVault(ref state, deltaTime);
                return;
            }
```

변경 후:

```csharp
            switch (state.Mode)
            {
                case MovementMode.Vault:
                    StepVault(ref state, deltaTime);
                    return;
                case MovementMode.Transport:
                    // D5: the rider only looks around; DropTransport.Ride places it and handles the jump.
                    return;
                case MovementMode.Freefall:
                case MovementMode.Glide:
                    StepAir(ref state, input.Buttons, moveX, moveY, sin, cos, deltaTime, world, terrain);
                    return;
            }
```

변경 전:

```csharp
            state.HorizontalVelocity = velocity;
```

변경 후:

```csharp
            state.HorizontalVelocity = velocity;
        }

        // D6: freefall and glide. A jump press or the ground within GlideAutoDeployHeight opens the glider (never the
        // other way). Freefall accelerates down to its terminal speed, the glider sinks at a steady speed; the input
        // steers the horizontal velocity in the character's frame towards the mode's forward, side and back limits.
        // Crouch is ignored (D12). Touching the ground lands in Ground mode with no horizontal velocity and no fall damage
        // (LandingSpeed stays 0, D10). The outer walls are only 4 m high, so the air is bounded by them too.
        private static void StepAir(ref MoveState state, InputButtons buttons, float moveX, float moveY, float sin, float cos, float deltaTime,
            ReadOnlySpan<Box> world, HeightField terrain)
        {
            Vector3 position = state.Position;
            Depenetrate(ref position, MoveSettings.Height, world, terrain);

            bool freefall = state.Mode == MovementMode.Freefall;
            if (freefall && ((buttons & InputButtons.Jump) != 0 || GroundDistance(position, world, terrain) <= MovementTuning.GlideAutoDeployHeight))
            {
                state.Mode = MovementMode.Glide;
                freefall = false;
            }

            if (freefall)
            {
                state.VelocityY += MoveSettings.Gravity * deltaTime;
                if (state.VelocityY < -MovementTuning.FreefallTerminalSpeed) state.VelocityY = -MovementTuning.FreefallTerminalSpeed;
            }
            else
            {
                state.VelocityY = -MovementTuning.GlideFallSpeed;
            }

            // The horizontal velocity in the character's frame, moved towards the input's target by at most accel x dt.
            var forward = new Vector2(sin, cos);
            var right = new Vector2(cos, -sin);
            float alongForward = Vector2.Dot(state.HorizontalVelocity, forward);
            float alongRight = Vector2.Dot(state.HorizontalVelocity, right);
            float targetForward = moveY >= 0f
                ? moveY * (freefall ? MovementTuning.FreefallForwardSpeed : MovementTuning.GlideForwardSpeed)
                : moveY * (freefall ? MovementTuning.FreefallBackSpeed : MovementTuning.GlideBackSpeed);
            float targetRight = moveX * (freefall ? MovementTuning.FreefallSideSpeed : MovementTuning.GlideSideSpeed);
            var change = new Vector2(targetForward - alongForward, targetRight - alongRight);
            float maxChange = (freefall ? MovementTuning.FreefallAcceleration : MovementTuning.GlideAcceleration) * deltaTime;
            float length = change.Length();
            if (length > maxChange) change *= maxChange / length;
            alongForward += change.X;
            alongRight += change.Y;
            state.HorizontalVelocity = forward * alongForward + right * alongRight;

            float wantX = state.HorizontalVelocity.X * deltaTime;
            float movedX = Sweep(position, MoveSettings.Height, AxisX, wantX, world, terrain, out _);
            position.X += movedX;
            if (movedX != wantX) state.HorizontalVelocity.X = 0f;
            float wantZ = state.HorizontalVelocity.Y * deltaTime;
            float movedZ = Sweep(position, MoveSettings.Height, AxisZ, wantZ, world, terrain, out _);
            position.Z += movedZ;
            if (movedZ != wantZ) state.HorizontalVelocity.Y = 0f;

            const float bound = GameMap.HalfSize - MoveSettings.HalfWidth - MoveSettings.Skin;
            if (position.X > bound || position.X < -bound)
            {
                position.X = position.X > 0f ? bound : -bound;
                state.HorizontalVelocity.X = 0f;
            }
            if (position.Z > bound || position.Z < -bound)
            {
                position.Z = position.Z > 0f ? bound : -bound;
                state.HorizontalVelocity.Y = 0f;
            }

            bool landed = false;
            float floor = terrain.Height(position.X, position.Z);
            if (position.Y < floor)
            {
                position.Y = floor;   // came over rising terrain
                landed = true;
            }
            float wantY = state.VelocityY * deltaTime;
            float movedY = Sweep(position, MoveSettings.Height, AxisY, wantY, world, terrain, out _);
            position.Y += movedY;
            if (landed || movedY != wantY)
            {
                state.Mode = MovementMode.Ground;
                state.VelocityY = 0f;
                state.HorizontalVelocity = Vector2.Zero;
            }
            state.Position = position;
        }

        // D6: height of the feet above the ground under them: the terrain or the highest box top below the feet under
        // the character's footprint.
        public static float GroundDistance(Vector3 feet, ReadOnlySpan<Box> world, HeightField terrain)
        {
            float ground = terrain.Height(feet.X, feet.Z);
            float minX = feet.X - MoveSettings.HalfWidth;
            float maxX = feet.X + MoveSettings.HalfWidth;
            float minZ = feet.Z - MoveSettings.HalfWidth;
            float maxZ = feet.Z + MoveSettings.HalfWidth;
            for (int i = 0; i < world.Length; i++)
            {
                ref readonly Box box = ref world[i];
                if (box.Max.X <= minX || box.Min.X >= maxX || box.Max.Z <= minZ || box.Min.Z >= maxZ) continue;
                if (box.Max.Y <= feet.Y + MoveSettings.GroundProbe && box.Max.Y > ground) ground = box.Max.Y;
            }
            return feet.Y - ground;
```

변경 전:

```csharp
        private static float Finite(float value) => IsFinite(value) ? value : 0f;
```

변경 후:

```csharp
        // The input's camera heading, when it is a number (Step and DropTransport.Ride).
        internal static void ApplyYaw(ref MoveState state, float yaw)
        {
            if (IsFinite(yaw)) state.Yaw = NormalizeYaw(yaw);
        }

        private static float Finite(float value) => IsFinite(value) ? value : 0f;
```

**수정** `Shared/Runtime/Simulation/MovementTuning.cs`:

변경 전:

```csharp
        // Fall damage (D3, D10): the landing speed decides it. Server rule (CombatRules.FallDamage); the numbers are here
```

변경 후:

```csharp
        // Freefall (D6): gravity up to a terminal speed; the input accelerates the horizontal velocity towards these
        // limits in the character's own frame (forward, side, back).
        public const float FreefallTerminalSpeed = 30f;
        public const float FreefallAcceleration = 20f;
        public const float FreefallForwardSpeed = 15f;
        public const float FreefallSideSpeed = 10f;
        public const float FreefallBackSpeed = 6f;

        // Glide (D6): opened by a jump press in freefall, or by the simulation when the ground (terrain or a box top
        // under the feet) is this close. A steady descent; it never closes again.
        public const float GlideAutoDeployHeight = 30f;
        public const float GlideFallSpeed = 5f;
        public const float GlideAcceleration = 10f;
        public const float GlideForwardSpeed = 14f;
        public const float GlideSideSpeed = 10f;
        public const float GlideBackSpeed = 4f;

        // Drop transport (D5): a straight route through the map's centre at this altitude and speed, starting and ending
        // TransportOutsideMargin outside the outer walls. Jumps count only while the transport is at least
        // TransportJumpMargin inside the walls; whoever is still aboard when it leaves that square is dropped there.
        public const float TransportAltitude = 90f;
        public const float TransportSpeed = 20f;
        public const float TransportOutsideMargin = 20f;
        public const float TransportJumpMargin = 10f;

        // Fall damage (D3, D10): the landing speed decides it. Server rule (CombatRules.FallDamage); the numbers are here
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 966개(957 통과, 9개 건너뜀)
Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

- [ ] **Step 5: Commit** — `feat: add the drop transport route, riding, freefall and the glider (Phase 12 D5, D6)`

---

### Task 4: Protocol v10

**Files:**
- Create: `Shared/Runtime/Protocol/TraversalPackets.cs`
- Modify: `Shared/Runtime/Protocol/PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs`, `ClientPackets.cs`, `ServerPackets.cs`, `CombatPackets.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Shared/TraversalPacketTests.cs`
  - Modify(Spec과 다른 점 19): `Shared/PacketTests.cs`, `Shared/PacketWriterReaderTests.cs`, `Shared/ProtocolConstantsTests.cs`, `Shared/MatchPacketTests.cs`, `Shared/StatsPacketTests.cs`, `Shared/ProtocolFuzzTests.cs`

**Interfaces:**
- Produces(namespace `ProjectH.Shared.Protocol`):
  - `ProtocolVersion` 10, `PacketId.TransportRoute` 24, `PacketId.DoorStates` 25. `PacketReader.TryReadPacketId`의 상한은 `DoorStates`다.
  - `SnapshotEntity`
    - 상수: `ModeShift` 1, `ModeMask` 0x0E, `SprintingFlag` 16, `ExhaustedFlag` 32
    - 속성: `Mode`(7은 `Ground`로 읽는다), `IsSprinting`, `IsExhausted`
    - `static MakeFlags(bool alive, MovementMode, bool sprinting, bool exhausted)`
    - `ToFixed`·`FromFixed`가 internal이 된다.
  - `SnapshotSelf.Size` 14: `ushort Energy`(1/100, 10000 이하), `Vector2 HorizontalVelocity`(1/256 양자화), `byte ModeTicks`, `byte EnergyDelayTicks`. `WorldSnapshotHeader.Size` 27
  - `enum DeathCause : byte { Zone = 0, Fall = 1 }`, `PlayerDied.Cause`(7 B), `PlayerRespawned.Mode`(20 B)
  - `TransportRoutePacket { Size 29; Write(ref PacketWriter, in DropRoute); TryRead(ref PacketReader, out DropRoute) }`
    - 좌표·고도는 ±127 m 안, 고도는 0 이상, 길이는 1–76800 Tick이어야 한다.
  - `DoorStatesPacket { Size 2; Write(ref PacketWriter, byte openMask); TryRead(ref PacketReader, out byte) }`. 없는 문의 비트 거부는 Task 6이 더한다.
  - `PlayerInputPacket`이 `Crouch`를 받는다.

서버는 이 Task에서 새 필드를 채우지 않는다. 기본값(모드 `Ground`, 원인 `Zone`, 기력 0)이 나간다. 채우는 것은 Task 5다. 봇과 Client는 바뀐 크기를 같은 `TryRead`로 읽으므로 그대로 컴파일된다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**수정** `Server/tests/ProjectH.Server.Tests/Shared/MatchPacketTests.cs`:

변경 전:

```csharp
    [Fact]
    public void PlayerDied_CarriesPlacement_In6Bytes()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = 2, KillerId = 0, Placement = 4 });
        Assert.Equal(6, writer.Length);
```

변경 후:

```csharp
    [Fact]
    public void PlayerDied_CarriesPlacement_In7Bytes()
    {
        // Phase 12 D10: one more byte, the cause (TraversalPacketTests).
        var writer = new PacketWriter(_buffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = 2, KillerId = 0, Placement = 4 });
        Assert.Equal(7, writer.Length);
```

**수정** `Server/tests/ProjectH.Server.Tests/Shared/PacketTests.cs`:

변경 전:

```csharp
                     InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.Interact |
                     InputButtons.Drop | InputButtons.UseMedkit | InputButtons.UseShieldCell, read.Get(0).Buttons);
        Assert.Equal(0x07FF, (int)read.Get(0).Buttons);
```

변경 후:

```csharp
                     InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.Interact |
                     InputButtons.Drop | InputButtons.UseMedkit | InputButtons.UseShieldCell | InputButtons.Crouch, read.Get(0).Buttons);
        Assert.Equal(0x0FFF, (int)read.Get(0).Buttons);   // Phase 12: Crouch (2048) is known
```

변경 전:

```csharp
    [Fact]
    public void SnapshotPacket_WithMaxEntities_Is1189Bytes_AndFitsOneDatagram()
```

변경 후:

```csharp
    [Fact]
    public void SnapshotPacket_WithMaxEntities_Is1197Bytes_AndFitsOneDatagram()
```

변경 전:

```csharp
        Assert.False(writer.Overflowed);
        Assert.Equal(1189, writer.Length);
```

변경 후:

```csharp
        Assert.False(writer.Overflowed);
        Assert.Equal(1197, writer.Length);   // Phase 12 D11: 27 + 90 x 13
```

**수정** `Server/tests/ProjectH.Server.Tests/Shared/PacketWriterReaderTests.cs`:

변경 전:

```csharp
    [InlineData(0)]
    [InlineData(24)]   // one above PacketId.StatsResponse (Phase 11)
```

변경 후:

```csharp
    [InlineData(0)]
    [InlineData(26)]   // one above PacketId.DoorStates (Phase 12)
```

**수정** `Server/tests/ProjectH.Server.Tests/Shared/ProtocolConstantsTests.cs`:

변경 전:

```csharp
    {
        // Phase 8: header 13 + self block 6, then 13 bytes per entity; 100 players in 2 packets of at most 90.
        Assert.Equal(19, WorldSnapshotHeader.Size);
        Assert.Equal(6, SnapshotSelf.Size);
```

변경 후:

```csharp
    {
        // Phase 8: header 13 + self block, then 13 bytes per entity; 100 players in 2 packets of at most 90. Phase 12 D11:
        // the self block grew from 6 to 14 bytes (header 27); the entity stays 13.
        Assert.Equal(27, WorldSnapshotHeader.Size);
        Assert.Equal(14, SnapshotSelf.Size);
```

변경 전:

```csharp
    [Fact]
    public void ProtocolVersion_IsNine()
    {
        // Phase 11 added StatsRequest/StatsResponse and the name in PlayerSpawned; v8 clients must be rejected at connect.
        Assert.Equal((ushort)9, ProtocolConstants.ProtocolVersion);
```

변경 후:

```csharp
    [Fact]
    public void ProtocolVersion_IsTen()
    {
        // Phase 12 changed the snapshot, two packets and the movement itself; v9 clients must be rejected at connect.
        Assert.Equal((ushort)10, ProtocolConstants.ProtocolVersion);
```

**수정** `Server/tests/ProjectH.Server.Tests/Shared/ProtocolFuzzTests.cs`:

변경 전:

```csharp
            // Half of them start with a valid packet id, so the body parsers also see plausible headers.
            if (length > 0 && random.Next(2) == 0) buffer[0] = (byte)random.Next(1, (int)PacketId.StatsResponse + 1);
```

변경 후:

```csharp
            // Half of them start with a valid packet id, so the body parsers also see plausible headers.
            if (length > 0 && random.Next(2) == 0) buffer[0] = (byte)random.Next(1, (int)PacketId.DoorStates + 1);
```

변경 전:

```csharp
        if (StatsResponse.TryRead(ref r, out _)) ok++;   // Phase 11
```

변경 후:

```csharp
        if (StatsResponse.TryRead(ref r, out _)) ok++;   // Phase 11
        r = new PacketReader(data);
        if (TransportRoutePacket.TryRead(ref r, out _)) ok++;   // Phase 12
        r = new PacketReader(data);
        if (DoorStatesPacket.TryRead(ref r, out _)) ok++;
```

**수정** `Server/tests/ProjectH.Server.Tests/Shared/StatsPacketTests.cs`:

변경 전:

```csharp
        Assert.Equal(PacketId.StatsResponse, id);
        reader = new PacketReader(new byte[] { 24 });
```

변경 후:

```csharp
        Assert.Equal(PacketId.StatsResponse, id);
        reader = new PacketReader(new byte[] { 26 });   // Phase 12 added 24 and 25
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Shared/TraversalPacketTests.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 D11 (spec §2 네트워크): the new packets, the snapshot's mode flags and self block, the mode in PlayerRespawned,
// the cause in PlayerDied and the Crouch button. Every reader refuses truncated data.
public class TraversalPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static DropRoute Route() => new()
    {
        StartX = -100f, StartZ = 3.5f, EndX = 100f, EndZ = -3.5f, Altitude = 90f, StartTick = 123_456, DurationTicks = 300,
    };

    [Fact]
    public void Ids_AreTwentyFourAndTwentyFive()
    {
        Assert.Equal(24, (byte)PacketId.TransportRoute);
        Assert.Equal(25, (byte)PacketId.DoorStates);
        var reader = new PacketReader(new byte[] { 25 });
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.DoorStates, id);
    }

    [Fact]
    public void TransportRoute_RoundTrip_In29Bytes()
    {
        var writer = new PacketWriter(_buffer);
        TransportRoutePacket.Write(ref writer, Route());
        Assert.Equal(TransportRoutePacket.Size, writer.Length);
        Assert.Equal(29, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.TransportRoute);
        Assert.True(TransportRoutePacket.TryRead(ref reader, out DropRoute read));
        Assert.Equal(Route(), read);
    }

    [Theory]
    [InlineData(float.NaN, 90f, 300u)]
    [InlineData(float.PositiveInfinity, 90f, 300u)]
    [InlineData(200f, 90f, 300u)]       // outside the snapshot's range
    [InlineData(-100f, -1f, 300u)]      // underground
    [InlineData(-100f, 130f, 300u)]
    [InlineData(-100f, 90f, 0u)]        // no duration
    [InlineData(-100f, 90f, 1_000_000u)]
    public void TransportRoute_WithBadValues_IsRefused(float startX, float altitude, uint duration)
    {
        DropRoute route = Route();
        route.StartX = startX;
        route.Altitude = altitude;
        route.DurationTicks = duration;
        var writer = new PacketWriter(_buffer);
        TransportRoutePacket.Write(ref writer, route);
        var reader = ReaderAfterId(writer.Length, PacketId.TransportRoute);
        Assert.False(TransportRoutePacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void DoorStates_RoundTrip_InTwoBytes()
    {
        var writer = new PacketWriter(_buffer);
        DoorStatesPacket.Write(ref writer, 0b10101);
        Assert.Equal(DoorStatesPacket.Size, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.DoorStates);
        Assert.True(DoorStatesPacket.TryRead(ref reader, out byte mask));
        Assert.Equal(0b10101, mask);
    }

    [Fact]
    public void TruncatedPackets_AreRefused()
    {
        var shortRoute = new PacketReader(new byte[TransportRoutePacket.Size - 2]);
        Assert.False(TransportRoutePacket.TryRead(ref shortRoute, out _));
        var noMask = new PacketReader(Array.Empty<byte>());
        Assert.False(DoorStatesPacket.TryRead(ref noMask, out _));
        var shortSelf = new PacketReader(new byte[SnapshotSelf.Size - 1]);
        Assert.False(SnapshotSelf.TryRead(ref shortSelf, out _));
        var shortRespawn = new PacketReader(new byte[18]);
        Assert.False(PlayerRespawned.TryRead(ref shortRespawn, out _));
        var shortDied = new PacketReader(new byte[5]);
        Assert.False(PlayerDied.TryRead(ref shortDied, out _));
    }

    [Fact]
    public void EntityFlags_CarryAliveModeSprintAndExhaustion()
    {
        for (int m = 0; m <= (int)MovementMode.Transport; m++)
        {
            var mode = (MovementMode)m;
            foreach (bool alive in new[] { false, true })
            {
                byte flags = SnapshotEntity.MakeFlags(alive, mode, sprinting: m % 2 == 0, exhausted: m % 3 == 0);
                var entity = new SnapshotEntity { EntityId = 7, Flags = flags };
                var writer = new PacketWriter(_buffer);
                SnapshotEntity.Write(ref writer, entity);
                Assert.Equal(SnapshotEntity.Size, writer.Length);
                var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
                Assert.True(SnapshotEntity.TryRead(ref reader, out SnapshotEntity read));
                Assert.Equal(alive, read.IsAlive);
                Assert.Equal(mode, read.Mode);
                Assert.Equal(m % 2 == 0, read.IsSprinting);
                Assert.Equal(m % 3 == 0, read.IsExhausted);
                Assert.Equal(0, read.Flags & 0xC0);
            }
        }
        // Mode bits 7 (no such mode) read as Ground.
        Assert.Equal(MovementMode.Ground, new SnapshotEntity { Flags = 0x0F }.Mode);
    }

    [Fact]
    public void SnapshotSelf_RoundTrip_In14Bytes_WithQuantizedVelocity()
    {
        var self = new SnapshotSelf
        {
            Health = 77, Shield = 12, WeaponSlot = 2, Ammo = 9, ReloadRemainingTicks = 40,
            Energy = 6543, HorizontalVelocity = new Vector2(12.345f, -6.789f), ModeTicks = 11, EnergyDelayTicks = 29,
        };
        var writer = new PacketWriter(_buffer);
        SnapshotSelf.Write(ref writer, self);
        Assert.Equal(14, writer.Length);
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(SnapshotSelf.TryRead(ref reader, out SnapshotSelf read));
        Assert.Equal(77, read.Health);
        Assert.Equal(40, read.ReloadRemainingTicks);
        Assert.Equal(6543, read.Energy);
        Assert.Equal(SnapshotEntity.Quantize(12.345f), read.HorizontalVelocity.X);
        Assert.Equal(SnapshotEntity.Quantize(-6.789f), read.HorizontalVelocity.Y);
        Assert.Equal(11, read.ModeTicks);
        Assert.Equal(29, read.EnergyDelayTicks);
    }

    [Fact]
    public void SnapshotSelf_AboveFullEnergy_IsRefused()
    {
        var writer = new PacketWriter(_buffer);
        SnapshotSelf.Write(ref writer, new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths + 1 });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.False(SnapshotSelf.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheRecipientPatch_WritesTheWholeSelfBlock()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 5, Count = 0, Part = 0, PartCount = 1 });
        Assert.Equal(WorldSnapshotHeader.Size, writer.Length);
        var self = new SnapshotSelf { Health = 50, Energy = 1234, HorizontalVelocity = new Vector2(3f, 4f), ModeTicks = 2, EnergyDelayTicks = 7 };
        WorldSnapshotHeader.PatchRecipient(_buffer.AsSpan(0, writer.Length), 99, self);
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out WorldSnapshotHeader header));
        Assert.Equal(99u, header.AckInputSeq);
        Assert.Equal(1234, header.Self.Energy);
        Assert.Equal(new Vector2(3f, 4f), header.Self.HorizontalVelocity);
        Assert.Equal(7, header.Self.EnergyDelayTicks);
    }

    [Fact]
    public void PlayerRespawned_CarriesTheStartMode()
    {
        var writer = new PacketWriter(_buffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = 3, Position = new Vector3(1f, 90f, 2f), Yaw = 10f, Mode = MovementMode.Transport });
        Assert.Equal(20, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerRespawned);
        Assert.True(PlayerRespawned.TryRead(ref reader, out PlayerRespawned read));
        Assert.Equal(MovementMode.Transport, read.Mode);

        _buffer[writer.Length - 1] = 7;   // no such mode
        reader = ReaderAfterId(writer.Length, PacketId.PlayerRespawned);
        Assert.False(PlayerRespawned.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerDied_CarriesTheCause()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = 4, Placement = 3, Cause = DeathCause.Fall });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.True(PlayerDied.TryRead(ref reader, out PlayerDied read));
        Assert.Equal(DeathCause.Fall, read.Cause);
        Assert.Equal(0, read.KillerId);

        _buffer[writer.Length - 1] = 2;   // no such cause
        reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.False(PlayerDied.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheCrouchButton_GoesThrough_AndUnknownBitsDoNot()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Crouch | (InputButtons)0x1000 | (InputButtons)0x8000 });
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out PlayerInputPacket read));
        Assert.Equal(InputButtons.Crouch, read.Get(0).Buttons);
        Assert.Equal(2048, (int)InputButtons.Crouch);
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 컴파일 실패(`TransportRoutePacket`, `DeathCause`, `SnapshotEntity.MakeFlags` 등이 없다)

- [ ] **Step 3: Protocol v10을 구현한다**

**수정** `Shared/Runtime/Protocol/ClientPackets.cs`:

변경 전:

```csharp
                                                     InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                     InputButtons.UseMedkit | InputButtons.UseShieldCell);
```

변경 후:

```csharp
                                                     InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                     InputButtons.UseMedkit | InputButtons.UseShieldCell | InputButtons.Crouch);
```

**수정** `Shared/Runtime/Protocol/CombatPackets.cs`:

변경 전:

```csharp
using System.Numerics;
```

변경 후:

```csharp
using System.Numerics;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
    // S->C, ReliableOrdered, to everyone. Same channel as PlayerRespawned, so a client always sees a
    // death before the matching respawn.
    // KillerId 0 = no killer (the zone, Phase 5 D8). Placement (Phase 5 D11) = living participants left + 1
    // during a match, 0 outside one (dev respawn mode, or a newcomer told it is spectating).
    public struct PlayerDied
```

변경 후:

```csharp
    // Phase 12 D10: what killed a player when no player did (PlayerDied.KillerId 0). A player's kill and the
    // spectating notice a newcomer gets also carry 0. Values are the wire format.
    public enum DeathCause : byte
    {
        Zone = 0,
        Fall = 1,
    }

    // S->C, ReliableOrdered, to everyone. Same channel as PlayerRespawned, so a client always sees a
    // death before the matching respawn.
    // KillerId 0 = no killer (the zone, Phase 5 D8; a fall, Phase 12 D10: Cause says which). Placement (Phase 5 D11) =
    // living participants left + 1 during a match, 0 outside one (dev respawn mode, or a newcomer told it is spectating).
    public struct PlayerDied
```

변경 전:

```csharp
        public byte Placement;
```

변경 후:

```csharp
        public byte Placement;
        public DeathCause Cause;
```

변경 전:

```csharp
            writer.WriteByte(d.Placement);
```

변경 후:

```csharp
            writer.WriteByte(d.Placement);
            writer.WriteByte((byte)d.Cause);
```

변경 전:

```csharp
            d = default;
            if (reader.Remaining < 5) return false;
            reader.TryReadUInt16(out d.VictimId);
            reader.TryReadUInt16(out d.KillerId);
            reader.TryReadByte(out d.Placement);
```

변경 후:

```csharp
            d = default;
            if (reader.Remaining < 6) return false;
            reader.TryReadUInt16(out d.VictimId);
            reader.TryReadUInt16(out d.KillerId);
            reader.TryReadByte(out d.Placement);
            reader.TryReadByte(out byte cause);
            if (cause > (byte)DeathCause.Fall) return false;
            d.Cause = (DeathCause)cause;
```

변경 전:

```csharp
    // Shield 0 and empty-handed (no weapon, no ammo); it re-arms from loot (D9).
```

변경 후:

```csharp
    // Shield 0 and empty-handed (no weapon, no ammo); it re-arms from loot (D9).
    // Phase 12 D11: Mode is the movement mode it starts in: Transport at a match start (aboard the drop transport),
    // Ground otherwise.
```

변경 전:

```csharp
        public float Yaw;
```

변경 후:

```csharp
        public float Yaw;
        public MovementMode Mode;
```

변경 전:

```csharp
            writer.WriteSingle(r.Yaw);
```

변경 후:

```csharp
            writer.WriteSingle(r.Yaw);
            writer.WriteByte((byte)r.Mode);
```

변경 전:

```csharp
            r = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out r.EntityId);
            reader.TryReadVector3(out r.Position);
            reader.TryReadSingle(out r.Yaw);
```

변경 후:

```csharp
            r = default;
            if (reader.Remaining < 19) return false;
            reader.TryReadUInt16(out r.EntityId);
            reader.TryReadVector3(out r.Position);
            reader.TryReadSingle(out r.Yaw);
            reader.TryReadByte(out byte mode);
            if (mode > (byte)MovementMode.Transport) return false;
            r.Mode = (MovementMode)mode;
```

**수정** `Shared/Runtime/Protocol/PacketId.cs`:

변경 전:

```csharp
        StatsResponse = 23,
```

변경 후:

```csharp
        StatsResponse = 23,
        // Phase 12 D11: deployment and doors.
        TransportRoute = 24,
        DoorStates = 25,
```

**수정** `Shared/Runtime/Protocol/PacketReader.cs`:

변경 전:

```csharp
            // Upper bound is the highest id in PacketId; raise it whenever a packet is added.
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.StatsResponse) return false;
```

변경 후:

```csharp
            // Upper bound is the highest id in PacketId; raise it whenever a packet is added.
            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.DoorStates) return false;
```

**수정** `Shared/Runtime/Protocol/ProtocolConstants.cs`:

변경 전:

```csharp
        // 9: Phase 11 game UI (StatsRequest/StatsResponse, the player's name in PlayerSpawned).
        public const ushort ProtocolVersion = 9;
```

변경 후:

```csharp
        // 9: Phase 11 game UI (StatsRequest/StatsResponse, the player's name in PlayerSpawned).
        // 10: Phase 12 deployment and traversal (movement modes in the snapshot flags, a 14-byte self block, the Crouch
        //     button, TransportRoute, DoorStates, the mode in PlayerRespawned, the cause in PlayerDied; movement changed).
        public const ushort ProtocolVersion = 10;
```

변경 전:

```csharp
        // Phase 8 D3: the most players a match (and so a snapshot) can hold. A snapshot is split into packets of at most
        // MaxEntitiesPerSnapshotPacket entities: (1200 - 19 header bytes) / 13 bytes per entity = 90, so 100 players
        // take MaxSnapshotParts = 2 packets (pinned by PacketTests).
```

변경 후:

```csharp
        // Phase 8 D3: the most players a match (and so a snapshot) can hold. A snapshot is split into packets of at most
        // MaxEntitiesPerSnapshotPacket entities: (1200 - 27 header bytes) / 13 bytes per entity = 90 (Phase 12: 1197 bytes,
        // 3 to spare), so 100 players take MaxSnapshotParts = 2 packets (pinned by PacketTests).
```

**수정** `Shared/Runtime/Protocol/ServerPackets.cs`:

변경 전:

```csharp
using System.Numerics;
```

변경 후:

```csharp
using System.Numerics;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
    // Layout: [PacketId 1][ServerTick 4][AckInputSeq 4][Count 2][Part 1][PartCount 1][SnapshotSelf 6] (19-byte header) then Count x SnapshotEntity.
    // The server writes one payload for everyone and patches AckInputSeq and Self per recipient (D10).
```

변경 후:

```csharp
    // Layout: [PacketId 1][ServerTick 4][AckInputSeq 4][Count 2][Part 1][PartCount 1][SnapshotSelf 14] (27-byte header, Phase 12)
    // then Count x SnapshotEntity.
    // The server writes one payload for everyone and patches AckInputSeq and Self per recipient (D10).
```

변경 전:

```csharp
    {
        public const int Size = 19;
```

변경 후:

```csharp
    {
        public const int Size = 27;
```

변경 전:

```csharp
    // Reliable combat event is late.
    public struct SnapshotSelf
    {
        public const int Size = 6;
```

변경 후:

```csharp
    // Reliable combat event is late.
    // Phase 12 D11: plus the movement state only the owner needs for prediction (the mode, sprint and exhaustion are in
    // its entity's flags, the position and VelocityY in the entity). Energy is the exact value (MoveState.EnergySpent);
    // the horizontal velocity is quantized like the entity's VelocityY.
    public struct SnapshotSelf
    {
        public const int Size = 14;
```

변경 전:

```csharp
        public ushort ReloadRemainingTicks; // 0 = not reloading; at least 1 while a reload is running
```

변경 후:

```csharp
        public ushort ReloadRemainingTicks; // 0 = not reloading; at least 1 while a reload is running
        public ushort Energy;               // hundredths, 0..MoveState.MaxEnergyHundredths
        public Vector2 HorizontalVelocity;  // m/s, X and Z
        public byte ModeTicks;
        public byte EnergyDelayTicks;
```

변경 전:

```csharp
            writer.WriteUInt16(s.ReloadRemainingTicks);
```

변경 후:

```csharp
            writer.WriteUInt16(s.ReloadRemainingTicks);
            writer.WriteUInt16(s.Energy);
            writer.WriteUInt16(SnapshotEntity.ToFixed(s.HorizontalVelocity.X));
            writer.WriteUInt16(SnapshotEntity.ToFixed(s.HorizontalVelocity.Y));
            writer.WriteByte(s.ModeTicks);
            writer.WriteByte(s.EnergyDelayTicks);
```

변경 전:

```csharp
            reader.TryReadUInt16(out s.ReloadRemainingTicks);
            return true;
```

변경 후:

```csharp
            reader.TryReadUInt16(out s.ReloadRemainingTicks);
            reader.TryReadUInt16(out s.Energy);
            reader.TryReadUInt16(out ushort velocityX);
            reader.TryReadUInt16(out ushort velocityZ);
            reader.TryReadByte(out s.ModeTicks);
            reader.TryReadByte(out s.EnergyDelayTicks);
            s.HorizontalVelocity = new Vector2(SnapshotEntity.FromFixed(velocityX), SnapshotEntity.FromFixed(velocityZ));
            return s.Energy <= MoveState.MaxEnergyHundredths;
```

변경 전:

```csharp
    // are clamped; non-finite values are written as 0.
```

변경 후:

```csharp
    // are clamped; non-finite values are written as 0.
    // Phase 12 D11: Flags bit 0 alive, bits 1-3 the MovementMode, bit 4 sprinting, bit 5 exhausted (the owner's prediction
    // needs it; others may show it). Bits 6-7 are 0.
```

변경 전:

```csharp
        public const byte AliveFlag = 1;
```

변경 후:

```csharp
        public const byte AliveFlag = 1;
        public const int ModeShift = 1;
        public const byte ModeMask = 0x0E;
        public const byte SprintingFlag = 16;
        public const byte ExhaustedFlag = 32;
```

변경 전:

```csharp
        public bool IsAlive => (Flags & AliveFlag) != 0;
```

변경 후:

```csharp
        public bool IsAlive => (Flags & AliveFlag) != 0;
        public bool IsSprinting => (Flags & SprintingFlag) != 0;
        public bool IsExhausted => (Flags & ExhaustedFlag) != 0;

        // The mode in the flags. A value above Transport (a bad packet) reads as Ground.
        public MovementMode Mode
        {
            get
            {
                int mode = (Flags & ModeMask) >> ModeShift;
                return mode <= (int)MovementMode.Transport ? (MovementMode)mode : MovementMode.Ground;
            }
        }

        public static byte MakeFlags(bool alive, MovementMode mode, bool sprinting, bool exhausted)
        {
            int flags = ((int)mode << ModeShift) & ModeMask;
            if (alive) flags |= AliveFlag;
            if (sprinting) flags |= SprintingFlag;
            if (exhausted) flags |= ExhaustedFlag;
            return (byte)flags;
        }
```

변경 전:

```csharp
        private static ushort ToFixed(float value)
        {
```

변경 후:

```csharp
        // Signed 16-bit fixed point (1/256), clamped; non-finite is 0. Also the self block's horizontal velocity.
        internal static ushort ToFixed(float value)
        {
```

변경 전:

```csharp
        }

        private static float FromFixed(ushort raw) => unchecked((short)raw) / FixedScale;
```

변경 후:

```csharp
        }

        internal static float FromFixed(ushort raw) => unchecked((short)raw) / FixedScale;
```

**새 파일** `Shared/Runtime/Protocol/TraversalPackets.cs`:

```csharp
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 12 D5, D11: S->C, ReliableOrdered: this match's drop transport route, once at the match start (before the
    // PlayerRespawned events that put everyone aboard) and to a newcomer or a resumed player during the match. Both
    // sides compute the transport's position from it (DropRoute.PositionAt); it is never sent per tick.
    public static class TransportRoutePacket
    {
        public const int Size = 29;   // with the packet id: 5 floats + 2 x uint32
        // Sanity limits for the client: the route stays well inside the snapshot's +-128 m range and lasts at most 10 min.
        public const float MaxCoordinate = 127f;
        public const uint MaxDurationTicks = 128 * 600;

        public static void Write(ref PacketWriter writer, in DropRoute route)
        {
            writer.WriteByte((byte)PacketId.TransportRoute);
            writer.WriteSingle(route.StartX);
            writer.WriteSingle(route.StartZ);
            writer.WriteSingle(route.EndX);
            writer.WriteSingle(route.EndZ);
            writer.WriteSingle(route.Altitude);
            writer.WriteUInt32(route.StartTick);
            writer.WriteUInt32(route.DurationTicks);
        }

        public static bool TryRead(ref PacketReader reader, out DropRoute route)
        {
            route = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadSingle(out route.StartX);
            reader.TryReadSingle(out route.StartZ);
            reader.TryReadSingle(out route.EndX);
            reader.TryReadSingle(out route.EndZ);
            reader.TryReadSingle(out route.Altitude);
            reader.TryReadUInt32(out route.StartTick);
            reader.TryReadUInt32(out route.DurationTicks);
            return InRange(route.StartX) && InRange(route.StartZ) && InRange(route.EndX) && InRange(route.EndZ) &&
                   InRange(route.Altitude) && route.Altitude >= 0f &&
                   route.DurationTicks >= 1 && route.DurationTicks <= MaxDurationTicks;
        }

        // Finite and inside the snapshot range (NaN fails both comparisons).
        private static bool InRange(float value) => value >= -MaxCoordinate && value <= MaxCoordinate;
    }

    // Phase 12 D9, D11: S->C, ReliableOrdered: which doors are open (bit i = GameMap.Doors[i]). To everyone when a door
    // changes (at most once per tick) and at the round start (all closed), and to a newcomer or a resumed player.
    public static class DoorStatesPacket
    {
        public const int Size = 2;   // with the packet id

        public static void Write(ref PacketWriter writer, byte openMask)
        {
            writer.WriteByte((byte)PacketId.DoorStates);
            writer.WriteByte(openMask);
        }

        public static bool TryRead(ref PacketReader reader, out byte openMask)
        {
            return reader.TryReadByte(out openMask);
        }
    }
}
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 984개(975 통과, 9개 건너뜀)
Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

- [ ] **Step 5: Commit** — `feat(shared): protocol v10 (mode flags, 14-byte self block, Crouch, TransportRoute, DoorStates, respawn mode, death cause)`

---

### Task 5: 서버 경기 통합 (투입, 낙하 피해, 행동 제한, 모드별 맞는 상자, 이동 이상 검사)

**Files:**
- Create: `Server/src/ProjectH.Server/Game/MovementLimits.cs`
- Modify:
  - `Server/src/ProjectH.Server/`: `ServerOptions.cs`, `GameLoop.cs`
  - `Game/`: `Match.cs`, `PlayerEntity.cs`, `Combat/CombatRules.cs`, `Combat/HitScan.cs`, `Combat/PositionHistory.cs`
  - `Diagnostics/`: `HealthCounters.cs`, `ServerMeter.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Game/MatchDeploymentTests.cs`, `Game/FallDamageTests.cs`, `Game/HitBoxModeTests.cs`
  - Modify: `Game/RoyaleHarness.cs`, `Integration/BattleRoyaleIntegrationTests.cs`, `Integration/HeadlessClient.cs`, `Integration/ReconnectIntegrationTests.cs`, `Diagnostics/MonitoringTests.cs`, `TestAim.cs`

**Interfaces:**
- Produces(Server):
  - `ServerOptions.AirDrop`(기본 `true`, 개발 모드에서는 무시)
  - `Match`
    - 생성자 끝 인자 `Action? movementAnomaly`
    - `long MovementAnomalies`
    - 테스트용: `internal bool HasRoute`, `internal DropRoute Route`
  - `PlayerEntity.Sprinting`
  - `PositionHistory`
    - `Reset(uint, Vector3, MovementMode = Ground)`, `Record(uint, Vector3, MovementMode = Ground)`
    - `Sample(double, out MovementMode)`(이전 기록의 모드)
  - `HitScan.TracePlayer(origin, direction, maxDistance, feet, float height, out distance)`
  - `CombatRules`: `CrouchEyeHeight` 1.0, `EyeHeightOf(MovementMode)`, `FallDamage(float landingSpeed)`
  - `MovementLimits`: `Slack` 1.5, `MaxSpeed(MovementMode)`
  - `HealthCounters`: `AddMovementAnomaly()`, `MovementAnomalies`
  - Meter `projecth.movement_anomalies`
  - Health 줄: `stalls` 뒤에 `movementAnomalies=N`
- 테스트 도구:
  - `RoyaleHarness(..., bool airDrop = false)`
  - `TestAim.YawPitch(feet, target, eyeHeight, out yaw, out pitch)`
  - `HeadlessClient.TransportRoutes`

`Match.Tick`의 플레이어 처리는 다음 순서다.

1. 탑승 중이면 `DropTransport.Ride(…, now + 1)`
2. 아니면 `Move`
   1. `Step`
   2. 이동 이상 검사
   3. 낙하 피해(피해가 허용될 때만). 피해로 죽으면 이 플레이어는 여기서 끝난다.
3. 재장전
4. 행동: 이동 뒤 모드가 `Ground`·`Crouch`·`Slide`일 때만
5. 회복 완료

경기를 시작할 때는 이렇게 한다.

1. `AirDrop`이면 경로를 정해(`SpawnSeed + Round`, 시작 Tick = `now + 1`) 모두에게 보낸다.
2. 모두를 경로 시작점에 `Transport`로 Respawn한다.
3. 자기장은 `Start(route.EndTick, …)`로 시작한다.

탑승자는 자기장 피해와 총알을 받지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**수정** `Server/tests/ProjectH.Server.Tests/Diagnostics/MonitoringTests.cs`:

변경 전:

```csharp
                     "badPackets unknownId=0", "malformed=", "beforeJoin=", "duplicateJoin=", "inputRate=", "wrongDirection=1", "handlerException=",
                     "tickFailures=0", "loopFailures=0", "matchResets=0", "stalls=0",
```

변경 후:

```csharp
                     "badPackets unknownId=0", "malformed=", "beforeJoin=", "duplicateJoin=", "inputRate=", "wrongDirection=1", "handlerException=",
                     "tickFailures=0", "loopFailures=0", "matchResets=0", "stalls=0", "movementAnomalies=0",
```

변경 전:

```csharp
        health.AddGraceExpiry();
```

변경 후:

```csharp
        health.AddGraceExpiry();
        health.AddMovementAnomaly();
```

변경 전:

```csharp
        Assert.Contains(("projecth.grace_expiries", 1L, ""), seen);
```

변경 후:

```csharp
        Assert.Contains(("projecth.grace_expiries", 1L, ""), seen);
        Assert.Contains(("projecth.movement_anomalies", 1L, ""), seen);   // Phase 12 D12
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Game/FallDamageTests.cs`:

```csharp
using System;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 12 D3, D10 (spec §2 낙하 피해): the rule, and falls in a running match: a safe height, a hurting one, a big one
// and a fatal one (a death without a killer, cause Fall), the shield untouched, a glide landing free, and no damage
// before the match.
public class FallDamageTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(13f, 0)]
    [InlineData(21.5f, 50)]
    [InlineData(30f, 100)]
    [InlineData(80f, 100)]
    [InlineData(float.NaN, 0)]
    [InlineData(-20f, 0)]
    public void TheRule_IsLinear_FromThirteenToThirtyMetresPerSecond(float speed, int damage)
    {
        Assert.Equal(damage, CombatRules.FallDamage(speed));
    }

    // The landing speed of a fall from this height onto the flat plaza (the same Step the match runs).
    private static float LandingSpeed(float height)
    {
        var s = new MoveState { Position = new Vector3(0f, height, -3f) };
        for (int i = 0; i < 300; i++)
        {
            MovementSimulation.Step(ref s, new InputCommand(), 1f / 30f, GameMap.Boxes, GameMap.Terrain, out StepResult r);
            if (r.LandingSpeed > 0f) return r.LandingSpeed;
        }
        throw new InvalidOperationException("never landed");
    }

    private static (RoyaleHarness h, PlayerEntity a, PlayerEntity b) InMatch()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        return (h, a, b);
    }

    private static void DropFrom(RoyaleHarness h, PlayerEntity p, float height)
    {
        h.Place(p, new Vector3(0f, height, -3f));
        h.TickUntil(() => !p.Alive || (p.State.VelocityY == 0f && MovementSimulation.IsGrounded(p.State, GameMap.Boxes, GameMap.Terrain)), 400);
    }

    [Theory]
    [InlineData(3.25f)]   // a roof: no damage (D3)
    [InlineData(4f)]
    public void ASafeHeight_DoesNothing(float height)
    {
        var (h, a, _) = InMatch();
        DropFrom(h, a, height);
        Assert.Equal(CombatRules.MaxHealth, a.Health);
        Assert.Empty(h.SentTo(1, PacketId.DamageTaken));
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(25f)]
    public void AHigherFall_TakesHealthOnly_AndTellsTheVictim(float height)
    {
        var (h, a, _) = InMatch();
        int shield = a.Shield;
        DropFrom(h, a, height);
        int expected = CombatRules.FallDamage(LandingSpeed(height));
        Assert.True(expected > 0);
        Assert.Equal(CombatRules.MaxHealth - expected, a.Health);
        Assert.Equal(shield, a.Shield);
        DamageTaken damage = h.SentTo(1, PacketId.DamageTaken).Select(s =>
        {
            var r = RoyaleHarness.Reader(s);
            Assert.True(DamageTaken.TryRead(ref r, out var d));
            return d;
        }).Single();
        Assert.Equal(0, damage.AttackerId);
        Assert.Equal(expected, damage.Damage);
        Assert.Equal(Vector3.Zero, damage.FromDirection);
    }

    [Fact]
    public void AFatalFall_IsADeathWithoutKiller_CauseFall_WithAPlacement()
    {
        var (h, a, b) = InMatch();
        DropFrom(h, a, 50f);
        Assert.False(a.Alive);
        PlayerDied died = RoyaleHarness.ReadDied(h.SentTo(2, PacketId.PlayerDied).Single());
        Assert.Equal(a.EntityId, died.VictimId);
        Assert.Equal(0, died.KillerId);
        Assert.Equal(DeathCause.Fall, died.Cause);
        Assert.Equal(2, died.Placement);
        Assert.Equal(0, b.Kills);
    }

    [Fact]
    public void AGlideLanding_DoesNoDamage()
    {
        var (h, a, _) = InMatch();
        h.Place(a, new Vector3(0f, 20f, -3f));
        a.State.Mode = MovementMode.Glide;
        h.TickUntil(() => a.State.Mode == MovementMode.Ground, 200);
        h.Ticks(2);
        Assert.Equal(CombatRules.MaxHealth, a.Health);
    }

    [Fact]
    public void BeforeTheMatch_AFallDoesNoDamage()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);
        PlayerEntity a = h.Join(1);
        DropFrom(h, a, 25f);
        Assert.Equal(CombatRules.MaxHealth, a.Health);
    }

    [Fact]
    public void AShotKill_StillCarriesCauseZero()
    {
        var (h, a, b) = InMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        PlayerDied died = RoyaleHarness.ReadDied(h.SentTo(1, PacketId.PlayerDied).Single());
        Assert.Equal(a.EntityId, died.KillerId);
        Assert.Equal(DeathCause.Zone, died.Cause);
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Game/HitBoxModeTests.cs`:

```csharp
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

// Phase 12 D13: the hit box follows the mode (1.2 m crouched or sliding), the lag-compensation history keeps the mode,
// and a crouched shooter's eye is at 1.0 m. In the dev sandbox with the combat loadout (slot 0: 30 damage).
public class HitBoxModeTests
{
    private readonly List<(int Peer, PacketId Id, byte[] Data)> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private readonly Match _match;

    public HitBoxModeTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 3, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => _sent.Add((peer, (PacketId)data[0], data.ToArray())), TestGameData.CombatLoadout);
    }

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

    private int Hits(int peer) => _sent.Count(s => s.Peer == peer && s.Id == PacketId.HitConfirmed);

    // The target crouches (held) for a few ticks, then the shooter fires at a point height above the target's feet.
    private bool ShootAtCrouched(float height)
    {
        PlayerEntity shooter = Join(1, new Vector3(0f, 0f, -6f));
        PlayerEntity target = Join(2, new Vector3(0f, 0f, 0f));
        for (int i = 0; i < 3; i++)
        {
            Send(target, new InputCommand { Buttons = InputButtons.Crouch });
            _match.Tick();
        }
        Assert.Equal(MovementMode.Crouch, target.State.Mode);
        Send(target, new InputCommand { Buttons = InputButtons.Crouch });
        TestAim.YawPitch(shooter.State.Position, target.State.Position + new Vector3(0f, height, 0f), out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.Tick();
        return Hits(1) > 0;
    }

    [Fact]
    public void ACrouchedTarget_IsMissedAboveOnePointTwo()
    {
        Assert.False(ShootAtCrouched(1.5f));
    }

    [Fact]
    public void ACrouchedTarget_IsHitBelowOnePointTwo()
    {
        Assert.True(ShootAtCrouched(0.9f));
    }

    [Fact]
    public void ACrouchedShooter_ShootsFromOneMetre()
    {
        PlayerEntity shooter = Join(1, new Vector3(0f, 0f, -6f));
        Join(2, new Vector3(5f, 0f, 5f));
        for (int i = 0; i < 2; i++)
        {
            Send(shooter, new InputCommand { Buttons = InputButtons.Crouch });
            _match.Tick();
        }
        TestAim.YawPitch(shooter.State.Position, new Vector3(0f, 1f, 10f), CombatRules.CrouchEyeHeight, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Crouch | InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
        _match.Tick();
        var shot = _sent.Last(s => s.Id == PacketId.ShotFired);
        var r = new PacketReader(shot.Data);
        r.TryReadPacketId(out _);
        Assert.True(ShotFired.TryRead(ref r, out ShotFired fired));
        Assert.Equal(shooter.State.Position.Y + CombatRules.CrouchEyeHeight, fired.Start.Y, 4);
        Assert.Equal(CombatRules.CrouchEyeHeight, CombatRules.EyeHeightOf(MovementMode.Slide));
        Assert.Equal(CombatRules.EyeHeight, CombatRules.EyeHeightOf(MovementMode.Ground));
    }

    [Fact]
    public void TheHistory_KeepsTheModeOfEachTick()
    {
        var history = new PositionHistory();
        history.Reset(10, Vector3.Zero, MovementMode.Ground);
        history.Record(11, Vector3.UnitX, MovementMode.Crouch);
        history.Record(12, 2f * Vector3.UnitX, MovementMode.Transport);

        Assert.Equal(new Vector3(0.5f, 0f, 0f), history.Sample(10.5, out MovementMode between));
        Assert.Equal(MovementMode.Ground, between);   // the older record's
        history.Sample(11, out MovementMode at11);
        Assert.Equal(MovementMode.Crouch, at11);
        history.Sample(30, out MovementMode past);
        Assert.Equal(MovementMode.Transport, past);
        history.Sample(1, out MovementMode before);
        Assert.Equal(MovementMode.Ground, before);
    }

    [Fact]
    public void TracePlayer_UsesTheGivenHeight()
    {
        var feet = new Vector3(0f, 0f, 5f);
        Assert.True(HitScan.TracePlayer(new Vector3(0f, 1.1f, 0f), Vector3.UnitZ, 100f, feet, 1.2f, out _));
        Assert.False(HitScan.TracePlayer(new Vector3(0f, 1.3f, 0f), Vector3.UnitZ, 100f, feet, 1.2f, out _));
        Assert.True(HitScan.TracePlayer(new Vector3(0f, 1.3f, 0f), Vector3.UnitZ, 100f, feet, out _));   // standing
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Game/MatchDeploymentTests.cs`:

```csharp
using System;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Flow;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 12 D4-D6, D12, D16 (spec §2 공중 투입, 재접속) through Match.Tick: the match starts aboard the drop transport,
// riders follow the route, jump, fall, glide and land; actions wait for the ground; a dropped rider comes back to the
// same state. The zone is the production one (its first circle covers the whole map), so nobody is hurt on the way down.
public class MatchDeploymentTests
{
    public const string WideZones = """
        {
          "initialCenter": [0, 0],
          "initialRadius": 115,
          "arenaHalfSize": 60,
          "phases": [
            {"waitSeconds":45,"shrinkSeconds":40,"targetRadius":70,"damagePerSecond":1},
            {"waitSeconds":20,"shrinkSeconds":15,"targetRadius":0,"damagePerSecond":20}
          ]
        }
        """;

    private static RoyaleHarness Harness(string zones = WideZones) =>
        new(TestGameData.CombatLoadout, zonesJson: zones, airDrop: true);

    private static (RoyaleHarness h, PlayerEntity a, PlayerEntity b) Started(string zones = WideZones)
    {
        RoyaleHarness h = Harness(zones);
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        return (h, a, b);
    }

    private static void TickUntilMode(RoyaleHarness h, PlayerEntity p, MovementMode mode, int max = 2000) =>
        h.TickUntil(() => p.State.Mode == mode, max);

    // Ticks with an empty input for every player until the window opens (so jump presses count).
    private static void TickToTheWindow(RoyaleHarness h)
    {
        h.Match.Route.JumpWindow(out uint first, out _);
        h.TickUntil(() => h.Match.ServerTick >= first, 1000);
    }

    [Fact]
    public void TheMatchStart_SendsTheRoute_ThenPutsEveryoneAboard()
    {
        var (h, a, b) = Started();
        Assert.True(h.Match.HasRoute);
        DropRoute route = h.Match.Route;
        // Rolled with SpawnSeed (1) + round (1), starting at the start tick.
        Assert.Equal(DropPlanner.Plan(2, route.StartTick, 30), route);
        Assert.Equal(h.Match.ServerTick, route.StartTick);

        foreach (PlayerEntity p in new[] { a, b })
        {
            Assert.Equal(MovementMode.Transport, p.State.Mode);
            Assert.Equal(route.PositionAt(h.Match.ServerTick), p.State.Position);
            var sent = h.Packets.Where(s => s.PeerId == p.PeerId).ToList();
            int routeAt = sent.FindIndex(s => s.Id == PacketId.TransportRoute);
            int respawnAt = sent.FindIndex(s => s.Id == PacketId.PlayerRespawned);
            Assert.True(routeAt >= 0 && routeAt < respawnAt, "the route before the respawns");
            var r = RoyaleHarness.Reader(sent[routeAt]);
            Assert.True(TransportRoutePacket.TryRead(ref r, out DropRoute received));
            Assert.Equal(route, received);
            Assert.All(sent.Where(s => s.Id == PacketId.PlayerRespawned), s => Assert.Equal(MovementMode.Transport, RoyaleHarness.ReadRespawned(s).Mode));
        }
    }

    [Fact]
    public void Riders_FollowTheRoute_EveryTick_AndTheZoneLeavesThemAlone()
    {
        // The small test zone (radius 30): the route starts far outside it.
        var (h, a, _) = Started(TestGameData.ZonesJson);
        for (int i = 0; i < 60; i++)
        {
            h.Ticks(1);
            Assert.Equal(h.Match.Route.PositionAt(h.Match.ServerTick), a.State.Position);
        }
        Assert.Equal(CombatRules.MaxHealth, a.Health);
    }

    [Fact]
    public void TheZoneClock_StartsWhenTheRouteEnds()
    {
        var (h, _, _) = Started();
        Assert.Equal(1, h.Match.Zone.Phase);
        Assert.Equal(h.Match.Route.EndTick + 45u * 30u, h.Match.Zone.ShrinkStartTick);
    }

    [Fact]
    public void AJumpBeforeTheWindow_IsIgnored_InsideItTheRiderFalls_AndNeverBoardsAgain()
    {
        var (h, a, _) = Started();
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        Assert.Equal(MovementMode.Transport, a.State.Mode);

        TickToTheWindow(h);
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        Assert.Equal(MovementMode.Freefall, a.State.Mode);
        Vector3 jumpedAt = a.State.Position;
        Assert.Equal(h.Match.Route.PositionAt(h.Match.ServerTick), jumpedAt);

        // Pressing jump again opens the glider; nothing ever puts it back on the transport.
        h.Ticks(2);
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        Assert.Equal(MovementMode.Glide, a.State.Mode);
        for (int i = 0; i < 30; i++)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
            h.Ticks(1);
            Assert.NotEqual(MovementMode.Transport, a.State.Mode);
        }
        Assert.True(a.State.Position.Y < jumpedAt.Y);
    }

    [Fact]
    public void RidersWhoSendNothing_AreDropped_GlideDown_AndLandUnhurt()
    {
        var (h, a, b) = Started();
        h.Match.Route.JumpWindow(out _, out uint last);
        h.TickUntil(() => b.State.Mode == MovementMode.Freefall, 2000);
        Assert.Equal(last, h.Match.ServerTick);

        TickUntilMode(h, b, MovementMode.Glide);
        TickUntilMode(h, b, MovementMode.Ground);
        Assert.True(b.Alive);
        Assert.Equal(CombatRules.MaxHealth, b.Health);
        Assert.Equal(Vector2.Zero, b.State.HorizontalVelocity);
        Assert.True(MathF.Abs(b.State.Position.X) < GameMap.HalfSize && MathF.Abs(b.State.Position.Z) < GameMap.HalfSize);
        Assert.Equal(0, h.Match.MovementAnomalies);
        Assert.Empty(h.SentTo(2, PacketId.DamageTaken));
    }

    [Fact]
    public void Riders_Fallers_AndGliders_CannotAct_UntilTheyLand()
    {
        var (h, a, _) = Started();
        // Aboard: fire, pick up and heal do nothing.
        for (int i = 0; i < 5; i++)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Fire | InputButtons.Interact | InputButtons.UseMedkit, AimPitch = 10f });
            h.Ticks(1);
        }
        TickToTheWindow(h);
        h.Send(a, new InputCommand { Buttons = InputButtons.Jump });
        h.Ticks(1);
        while (a.State.Mode != MovementMode.Ground)
        {
            h.Send(a, new InputCommand { Buttons = InputButtons.Fire | InputButtons.Interact, AimPitch = 10f });
            h.Ticks(1);
            // The landing tick itself already acts: the gate looks at the mode after the move.
            if (a.State.Mode == MovementMode.Ground) break;
            Assert.Empty(h.SentTo(1, PacketId.ShotFired));
            Assert.Empty(h.SentTo(1, PacketId.PickupResult));
        }

        // On the ground the same buttons act again (the loadout's rifle fires).
        h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimPitch = 10f });
        h.Ticks(1);
        Assert.NotEmpty(h.SentTo(1, PacketId.ShotFired));
    }

    [Fact]
    public void ARider_CannotBeShot()
    {
        var (h, a, b) = Started();
        // a is put on the ground under b's transport position; b rides on.
        a.State.Mode = MovementMode.Ground;
        Vector3 under = b.State.Position with { Y = 0f };
        h.Place(a, under);
        Vector3 target = h.Match.Route.PositionAt(h.Match.ServerTick + 1) + RoyaleHarness.Chest;
        TestAim.YawPitch(a.State.Position, target, out float yaw, out float pitch);
        h.Send(a, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = h.Match.ServerTick + 1 });
        h.Ticks(1);
        Assert.Single(h.SentTo(1, PacketId.ShotFired));
        Assert.Empty(h.SentTo(1, PacketId.HitConfirmed));
        Assert.Equal(CombatRules.MaxHealth, b.Health);
    }

    [Fact]
    public void ANewcomerDuringTheMatch_GetsTheRoute()
    {
        var (h, _, _) = Started();
        h.Join(3);
        Assert.Single(h.SentTo(3, PacketId.TransportRoute));
    }

    [Fact]
    public void Snapshots_CarryTheMode_AndTheOwnersMovementState()
    {
        var (h, a, _) = Started();
        h.Ticks(2);
        var last = h.SentTo(1, PacketId.WorldSnapshot).Last();
        var r = RoyaleHarness.Reader(last);
        Assert.True(WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header));
        Assert.Equal(MoveState.MaxEnergyHundredths, header.Self.Energy);
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref r, out SnapshotEntity e));
            Assert.Equal(MovementMode.Transport, e.Mode);
            Assert.True(e.IsAlive);
        }
    }

    [Fact]
    public void AMoveFasterThanItsMode_IsCounted()
    {
        var (h, a, _) = Started();
        Assert.Equal(0, h.Match.MovementAnomalies);
        // A vault state no Step would make: 1 km/s for one tick.
        a.State.Mode = MovementMode.Vault;
        a.State.ModeTicks = 1;
        a.State.HorizontalVelocity = new Vector2(1000f, 0f);
        h.Ticks(1);
        Assert.Equal(1, h.Match.MovementAnomalies);
    }

    [Fact]
    public void TheNextRound_ClearsTheRoute_AndStartsOnTheGround()
    {
        var (h, a, b) = Started();
        h.Match.Flow.Finish(h.Match.ServerTick);
        h.TickUntil(() => h.Match.Flow.State == MatchFlowState.Starting || h.Match.Flow.State == MatchFlowState.WaitingForPlayers, 200);
        Assert.False(h.Match.HasRoute);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        Assert.Equal(MovementMode.Ground, b.State.Mode);
    }

    // ---- Reconnect (D16) ----

    [Theory]
    [InlineData(MovementMode.Transport)]
    [InlineData(MovementMode.Freefall)]
    [InlineData(MovementMode.Glide)]
    [InlineData(MovementMode.Ground)]
    public void ADropAndAResume_InAnyDeploymentMode_KeepsTheCharactersState_AndResendsTheRoute(MovementMode at)
    {
        var (h, a, _) = Started();
        if (at != MovementMode.Transport) TickUntilMode(h, a, at);
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        h.Ticks(3);   // graced: the server keeps stepping it with empty input
        MoveState kept = a.State;

        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(11, "p1"));
        Assert.Equal(kept.Position, a.State.Position);
        Assert.Equal(kept.Mode, a.State.Mode);
        Assert.Single(h.SentTo(11, PacketId.TransportRoute));

        // Its next snapshot shows the mode it is in, and it still lands.
        h.Ticks(2);
        var r = RoyaleHarness.Reader(h.SentTo(11, PacketId.WorldSnapshot).Last());
        Assert.True(WorldSnapshotHeader.TryRead(ref r, out WorldSnapshotHeader header));
        bool found = false;
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref r, out SnapshotEntity e));
            if (e.EntityId != a.EntityId) continue;
            Assert.Equal(a.State.Mode, e.Mode);
            found = true;
        }
        Assert.True(found);
        TickUntilMode(h, a, MovementMode.Ground);
        Assert.True(a.Alive);
    }

    [Fact]
    public void TheDevSandbox_SpawnsOnTheGround_WithoutARoute()
    {
        var sent = new System.Collections.Generic.List<PacketId>();
        var match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(),
            (_, data, _) => sent.Add((PacketId)data[0]));
        Assert.Equal(JoinResult.Ok, match.TryJoin(1, "a"));
        Assert.Equal(JoinResult.Ok, match.TryJoin(2, "b"));
        for (int i = 0; i < 120; i++) match.Tick();
        match.TryGetPlayer(1, out PlayerEntity a);
        Assert.Equal(MovementMode.Ground, a.State.Mode);
        Assert.False(match.HasRoute);
        Assert.DoesNotContain(PacketId.TransportRoute, sent);
    }
}
```

**수정** `Server/tests/ProjectH.Server.Tests/Game/RoyaleHarness.cs`:

변경 전:

```csharp
// result screen are 1 s (30 ticks) so tests reach every state quickly. Every sent packet is recorded.
```

변경 후:

```csharp
// result screen are 1 s (30 ticks) so tests reach every state quickly. Every sent packet is recorded.
// Phase 12: airDrop false (the default here) starts matches on the drop points as in Phases 5-11, so the rule tests
// stay about their rules; the deployment tests pass true (ServerOptions.AirDrop, on in production).
```

변경 전:

```csharp
        Vector3[]? dropPoints = null, Action<ProjectH.Server.Persistence.MatchRecord>? matchSink = null,
        int reconnectGraceSeconds = 10, Action<string>? graceExpired = null)
```

변경 후:

```csharp
        Vector3[]? dropPoints = null, Action<ProjectH.Server.Persistence.MatchRecord>? matchSink = null,
        int reconnectGraceSeconds = 10, Action<string>? graceExpired = null, bool airDrop = false)
```

변경 전:

```csharp
                MaxPlayers = maxPlayers, MinPlayers = minPlayers, StartCountdownSeconds = 1, ResultSeconds = 1,
                ReconnectGraceSeconds = reconnectGraceSeconds,
```

변경 후:

```csharp
                MaxPlayers = maxPlayers, MinPlayers = minPlayers, StartCountdownSeconds = 1, ResultSeconds = 1,
                ReconnectGraceSeconds = reconnectGraceSeconds, AirDrop = airDrop,
```

**수정** `Server/tests/ProjectH.Server.Tests/Integration/BattleRoyaleIntegrationTests.cs`:

변경 전:

```csharp
            StatsIntervalSeconds = 60,
```

변경 후:

```csharp
            StatsIntervalSeconds = 60,
            // Phase 12: this test fights right after the start on its two drop spots; deployment over UDP is covered by
            // ReconnectIntegrationTests (AirDrop on).
            AirDrop = false,
```

**수정** `Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs`:

변경 전:

```csharp
    public List<StatsResponse> StatsResponses { get; } = new();
```

변경 후:

```csharp
    public List<StatsResponse> StatsResponses { get; } = new();
    // Phase 12: every TransportRoute in arrival order.
    public List<DropRoute> TransportRoutes { get; } = new();
```

변경 전:

```csharp
                if (StatsResponse.TryRead(ref r, out var stats)) StatsResponses.Add(stats);
                break;
```

변경 후:

```csharp
                if (StatsResponse.TryRead(ref r, out var stats)) StatsResponses.Add(stats);
                break;
            case PacketId.TransportRoute:
                if (TransportRoutePacket.TryRead(ref r, out var route)) TransportRoutes.Add(route);
                break;
```

**수정** `Server/tests/ProjectH.Server.Tests/Integration/ReconnectIntegrationTests.cs`:

변경 전:

```csharp
using ProjectH.Shared.Protocol;
```

변경 후:

```csharp
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
            a.Dispose();   // killed above, but a failed assertion before the kill must not leak its socket
```

변경 후:

```csharp
            a.Dispose();   // killed above, but a failed assertion before the kill must not leak its socket
        }
    }

    // Phase 12 D16: a client that crashes while riding the drop transport comes back to the same route, and its next
    // snapshot shows where the server's own steps (empty input) took it meanwhile.
    [Fact]
    public void ACrashedRider_ComesBack_WithTheRoute_AndItsMode()
    {
        using GameLoop server = StartServer(countdown: 1);
        var a = Join(server, "a");
        try
        {
            using var b = Join(server, "b");
            Assert.True(Pump.Until(() => InMatch(a) && InMatch(b) && a.TransportRoutes.Count == 1, 5000, a, b), "aboard");
            ushort entity = a.MyEntityId;

            a.Kill();
            Assert.True(Pump.Until(() => server.Health.GraceStarts == 1, 4000, b), "graced");

            using var back = Join(server, "a", expected: JoinResult.Resumed);
            Assert.True(Pump.Until(() => back.TransportRoutes.Count == 1 && back.LastSnapshot.ContainsKey(entity), 3000, back, b), "route and snapshot");
            Assert.Equal(a.TransportRoutes[0], back.TransportRoutes[0]);
            Assert.Contains(back.LastSnapshot[entity].Mode, new[] { MovementMode.Transport, MovementMode.Freefall, MovementMode.Glide });
        }
        finally
        {
            a.Dispose();
```

**수정** `Server/tests/ProjectH.Server.Tests/TestAim.cs`:

변경 전:

```csharp
{
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, out float yaw, out float pitch)
    {
        Vector3 d = targetPoint - (feet + new Vector3(0f, CombatRules.EyeHeight, 0f));
```

변경 후:

```csharp
{
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, out float yaw, out float pitch) =>
        YawPitch(feet, targetPoint, CombatRules.EyeHeight, out yaw, out pitch);

    // Phase 12: from another eye height (crouched: CombatRules.CrouchEyeHeight).
    public static void YawPitch(Vector3 feet, Vector3 targetPoint, float eyeHeight, out float yaw, out float pitch)
    {
        Vector3 d = targetPoint - (feet + new Vector3(0f, eyeHeight, 0f));
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 컴파일 실패(`ServerOptions.AirDrop`, `Match.Route`, `CombatRules.FallDamage` 등이 없다)

- [ ] **Step 3: 서버를 구현한다**

**수정** `Server/src/ProjectH.Server/Diagnostics/HealthCounters.cs`:

변경 전:

```csharp
    private long _stalls;
```

변경 후:

```csharp
    private long _stalls;
    private long _movementAnomalies;
```

변경 전:

```csharp
    public void AddStall() => Interlocked.Increment(ref _stalls);
```

변경 후:

```csharp
    public void AddStall() => Interlocked.Increment(ref _stalls);
    // Phase 12 D12: a move faster than its mode allows (Match's self-check; should stay 0).
    public void AddMovementAnomaly() => Interlocked.Increment(ref _movementAnomalies);
```

변경 전:

```csharp
    public long Stalls => Interlocked.Read(ref _stalls);
```

변경 후:

```csharp
    public long Stalls => Interlocked.Read(ref _stalls);
    public long MovementAnomalies => Interlocked.Read(ref _movementAnomalies);
```

**수정** `Server/src/ProjectH.Server/Diagnostics/ServerMeter.cs`:

변경 전:

```csharp
        _meter.CreateObservableCounter("projecth.stalls", () => h.Stalls);
```

변경 후:

```csharp
        _meter.CreateObservableCounter("projecth.stalls", () => h.Stalls);
        _meter.CreateObservableCounter("projecth.movement_anomalies", () => h.MovementAnomalies,
            description: "Moves faster than their movement mode allows (a simulation bug; should stay 0)");
```

**수정** `Server/src/ProjectH.Server/Game/Combat/CombatRules.cs`:

변경 전:

```csharp
using System.Numerics;
```

변경 후:

```csharp
using System.Numerics;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
    public const float EyeHeight = 1.6f;
```

변경 후:

```csharp
    public const float EyeHeight = 1.6f;
    // Phase 12 D13: crouched or sliding (a 1.2 m box) the eye is at 1.0 m, so a crouched player behind cover cannot shoot
    // over it while it cannot be hit. Must equal the client's AimSolver.CrouchEyeHeight.
    public const float CrouchEyeHeight = 1.0f;
```

변경 전:

```csharp
    public const float MaxRewindSeconds = 0.4f;
```

변경 후:

```csharp
    public const float MaxRewindSeconds = 0.4f;

    // Phase 12 D13: where a shot of a player in this mode starts above its feet.
    public static float EyeHeightOf(MovementMode mode) =>
        mode == MovementMode.Crouch || mode == MovementMode.Slide ? CrouchEyeHeight : EyeHeight;

    // Phase 12 D10: no damage up to FallDamageMinSpeed, FallDamageMax from FallDamageMaxSpeed on, linear between
    // (rounded half away from zero). Speeds are the landing's vertical speed in m/s; anything not a number is 0.
    public static int FallDamage(float landingSpeed)
    {
        if (!(landingSpeed > MovementTuning.FallDamageMinSpeed)) return 0;
        if (landingSpeed >= MovementTuning.FallDamageMaxSpeed) return MovementTuning.FallDamageMax;
        float share = (landingSpeed - MovementTuning.FallDamageMinSpeed) / (MovementTuning.FallDamageMaxSpeed - MovementTuning.FallDamageMinSpeed);
        return (int)MathF.Round(share * MovementTuning.FallDamageMax, MidpointRounding.AwayFromZero);
    }
```

**수정** `Server/src/ProjectH.Server/Game/Combat/HitScan.cs`:

변경 전:

```csharp
    // Player hit box: the movement AABB (HalfWidth 0.35 m, Height 1.8 m) with its feet at feet.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance)
    {
        var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
        var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + MoveSettings.Height, feet.Z + MoveSettings.HalfWidth);
```

변경 후:

```csharp
    // Player hit box: the movement AABB (HalfWidth 0.35 m, Height 1.8 m) with its feet at feet.
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, out float distance) =>
        TracePlayer(origin, direction, maxDistance, feet, MoveSettings.Height, out distance);

    // Phase 12 D13: the same box with the mode's height (MovementSimulation.CollisionHeight: 1.2 m crouched or sliding).
    public static bool TracePlayer(Vector3 origin, Vector3 direction, float maxDistance, Vector3 feet, float height, out float distance)
    {
        var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
        var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
```

**수정** `Server/src/ProjectH.Server/Game/Combat/PositionHistory.cs`:

변경 전:

```csharp
using System.Numerics;
```

변경 후:

```csharp
using System.Numerics;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
// Record overwrites the oldest of Capacity entries. Game loop thread only.
```

변경 후:

```csharp
// Record overwrites the oldest of Capacity entries. Game loop thread only.
// Phase 12 D13: each record also keeps the movement mode, which sets the hit box height (and a rider is not hit).
```

변경 전:

```csharp
    private readonly Vector3[] _positions = new Vector3[Capacity];
```

변경 후:

```csharp
    private readonly Vector3[] _positions = new Vector3[Capacity];
    private readonly MovementMode[] _modes = new MovementMode[Capacity];
```

변경 전:

```csharp
    // position from before a teleport.
    public void Reset(uint tick, Vector3 position)
    {
        _count = 0;
        _newest = -1;
        Record(tick, position);
    }

    // Ticks must increase; a repeated or older tick is ignored.
    public void Record(uint tick, Vector3 position)
```

변경 후:

```csharp
    // position from before a teleport.
    public void Reset(uint tick, Vector3 position, MovementMode mode = MovementMode.Ground)
    {
        _count = 0;
        _newest = -1;
        Record(tick, position, mode);
    }

    // Ticks must increase; a repeated or older tick is ignored.
    public void Record(uint tick, Vector3 position, MovementMode mode = MovementMode.Ground)
```

변경 전:

```csharp
        _positions[_newest] = position;
        if (_count < Capacity) _count++;
    }

    // Position at a (fractional) tick, interpolated between the two records around it. Past the newest
    // record it holds the newest; before the oldest it uses the oldest (short history, spec §5).
    public Vector3 Sample(double tick)
    {
```

변경 후:

```csharp
        _positions[_newest] = position;
        _modes[_newest] = mode;
        if (_count < Capacity) _count++;
    }

    public Vector3 Sample(double tick) => Sample(tick, out _);

    // Position at a (fractional) tick, interpolated between the two records around it. Past the newest
    // record it holds the newest; before the oldest it uses the oldest (short history, spec §5).
    // mode: the mode of the record at or before the tick (between two records, the older one's).
    public Vector3 Sample(double tick, out MovementMode mode)
    {
        mode = MovementMode.Ground;
```

변경 전:

```csharp
            if (_ticks[index] > tick) continue;
```

변경 후:

```csharp
            if (_ticks[index] > tick) continue;
            mode = _modes[index];
```

변경 전:

```csharp
        int oldest = (_newest - _count + 1 + Capacity) % Capacity;
```

변경 후:

```csharp
        int oldest = (_newest - _count + 1 + Capacity) % Capacity;
        mode = _modes[oldest];
```

**수정** `Server/src/ProjectH.Server/Game/Match.cs`:

변경 전:

```csharp
    private readonly Action<string>? _graceExpired;
```

변경 후:

```csharp
    private readonly Action<string>? _graceExpired;
    // Phase 12 D12: told of every move faster than its mode allows (a simulation bug; GameLoop counts it). Must not block.
    private readonly Action? _movementAnomaly;
    // Phase 12 D4, D5: matches start aboard the drop transport. The route is valid while _hasRoute: from such a match's
    // start to the round reset.
    private readonly bool _airDrop;
    private bool _hasRoute;
    private DropRoute _route;
```

변경 전:

```csharp
    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null,
        Vector3[]? dropPoints = null, Action<MatchRecord>? matchSink = null, Action<string>? graceExpired = null)
    {
        _matchSink = matchSink;
        _graceExpired = graceExpired;
```

변경 후:

```csharp
    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null,
        Vector3[]? dropPoints = null, Action<MatchRecord>? matchSink = null, Action<string>? graceExpired = null,
        Action? movementAnomaly = null)
    {
        _matchSink = matchSink;
        _graceExpired = graceExpired;
        _movementAnomaly = movementAnomaly;
        _airDrop = options.AirDrop && !options.DevRespawn;
```

변경 전:

```csharp
    internal ushort WinnerId { get; private set; }
```

변경 후:

```csharp
    internal ushort WinnerId { get; private set; }
    // Test seams (Phase 12): the current drop transport route, valid while HasRoute.
    internal bool HasRoute => _hasRoute;
    internal DropRoute Route => _route;
    // Phase 12 D12: moves faster than their mode allows since this match object was made (should stay 0).
    public long MovementAnomalies { get; private set; }
```

변경 전:

```csharp
        // Reliable, after its own spawn: the newcomer's client knows it is dead (spectating) before any input.
```

변경 후:

```csharp
        // Phase 12 D16: a newcomer during an air-drop match sees the transport too.
        if (_hasRoute) SendRoute(peerId);
        // Reliable, after its own spawn: the newcomer's client knows it is dead (spectating) before any input.
```

변경 전:

```csharp
            // Same boxes and terrain as client prediction (LocalPlayerPredictor), so predictions match.
            MovementSimulation.Step(ref player.State, input, _tickSeconds, GameMap.Boxes, GameMap.Terrain);
            WeaponRules.UpdateReload(player, now);
            // Only an input the client really sent can act: the missed-input repeat copies the last input's
            // buttons and must never invent a switch, reload or shot.
            if (sent) ProcessActions(player, input, now);
            ConsumableRules.Complete(player, _items, now);   // step 9: every tick, input or not
```

변경 후:

```csharp
            // Phase 12 D5: a rider is placed on the route at the tick being simulated (now + 1, the tick its snapshot
            // reports) and may jump. Everyone else steps with the same boxes and terrain as client prediction
            // (LocalPlayerPredictor), so predictions match.
            if (_hasRoute && DropTransport.Ride(ref player.State, input, _route, now + 1)) player.Sprinting = false;
            else if (!Move(player, input)) continue;   // the landing killed it
            WeaponRules.UpdateReload(player, now);
            // Only an input the client really sent can act: the missed-input repeat copies the last input's
            // buttons and must never invent a switch, reload or shot. Phase 12 D12: and only in a mode that allows
            // actions (after this tick's move).
            if (sent && ActionsAllowed(player.State.Mode)) ProcessActions(player, input, now);
            ConsumableRules.Complete(player, _items, now);   // step 9: every tick, input or not
```

변경 전:

```csharp
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position);
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
```

변경 후:

```csharp
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position, player.State.Mode);
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }

    // One Step and the Phase 12 checks of its result: the movement self-check (D12) and fall damage (D10). Returns false
    // when the landing killed the player.
    private bool Move(PlayerEntity player, in InputCommand input)
    {
        MovementMode before = player.State.Mode;
        Vector3 from = player.State.Position;
        MovementSimulation.Step(ref player.State, input, _tickSeconds, GameMap.Boxes, GameMap.Terrain, out StepResult step);
        player.Sprinting = step.Sprinting;

        float limit = MathF.Max(MovementLimits.MaxSpeed(before), MovementLimits.MaxSpeed(player.State.Mode)) * _tickSeconds * MovementLimits.Slack;
        if (Vector3.DistanceSquared(from, player.State.Position) > limit * limit)
        {
            MovementAnomalies++;
            _movementAnomaly?.Invoke();
        }

        // D10: like shots, a fall hurts only when damage is allowed (the dev sandbox, or during the match).
        return !(step.LandingSpeed > 0f && _flow.DamageAllowed && ApplyFallDamage(player, step.LandingSpeed));
    }

    // Phase 12 D12: riding, falling, gliding and vaulting allow no shot, reload, pickup, interaction, heal, slot switch
    // or drop. A reload or heal already running goes on.
    private static bool ActionsAllowed(MovementMode mode) =>
        mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;

    // Phase 12 D10: health only (the shield does not stop it, like the zone) and a DamageTaken from nobody; a fatal fall
    // is a death without a killer, cause Fall. Returns true when it killed.
    private bool ApplyFallDamage(PlayerEntity player, float landingSpeed)
    {
        int damage = CombatRules.FallDamage(landingSpeed);
        if (damage <= 0) return false;
        player.Health = Math.Max(0, player.Health - damage);
        var writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken { AttackerId = 0, Damage = (ushort)damage, FromDirection = Vector3.Zero });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        if (player.Health > 0) return false;
        Kill(player, null, DeathCause.Fall);
        return true;
```

변경 전:

```csharp
        ushort damage = CombatRules.ScaledDamage(weapon.Damage, _items.DamageMultiplier(held.Rarity));
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeight, 0f);
```

변경 후:

```csharp
        ushort damage = CombatRules.ScaledDamage(weapon.Damage, _items.DamageMultiplier(held.Rarity));
        // Phase 12 D13: crouched or sliding the eye is lower (the client aims from the same height, AimSolver).
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(shooter.State.Mode), 0f);
```

변경 전:

```csharp
            if (other == shooter || !other.Alive) continue;
            Vector3 feet = other.History.Sample(rewindTick);
            if (HitScan.TracePlayer(origin, direction, nearest, feet, out float distance) &&
```

변경 후:

```csharp
            if (other == shooter || !other.Alive) continue;
            // Phase 12 D13: the hit box has the height of the mode the target was in then; a transport rider is not hit.
            Vector3 feet = other.History.Sample(rewindTick, out MovementMode mode);
            if (mode == MovementMode.Transport) continue;
            if (HitScan.TracePlayer(origin, direction, nearest, feet, MovementSimulation.CollisionHeight(mode), out float distance) &&
```

변경 전:

```csharp
        {
            if (!player.Alive || !_zone.IsOutside(player.State.Position, now)) continue;
```

변경 후:

```csharp
        {
            // Phase 12: a transport rider is above the map (its route reaches outside the circle), not in it.
            if (!player.Alive || player.State.Mode == MovementMode.Transport || !_zone.IsOutside(player.State.Position, now)) continue;
```

변경 전:

```csharp
    private void SendDied(int peerId, in PlayerDied died)
```

변경 후:

```csharp
    // Phase 12 D5: the route, to one player (a newcomer or a resumed player during the match) or to everyone (the start).
    private void SendRoute(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        TransportRoutePacket.Write(ref writer, _route);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendDied(int peerId, in PlayerDied died)
```

변경 전:

```csharp
    // PlayerRespawned, so every client sees them in that order.
    // killer null = the zone (Phase 5 D8): KillerId 0, nobody gets the kill. During a match the death is
    // permanent and takes the next placement (D9); the killer's count rises unless it killed itself (D12).
    private void Kill(PlayerEntity victim, PlayerEntity? killer)
```

변경 후:

```csharp
    // PlayerRespawned, so every client sees them in that order.
    // killer null = the zone (Phase 5 D8) or a fall (Phase 12 D10, cause): KillerId 0, nobody gets the kill. During a
    // match the death is permanent and takes the next placement (D9); the killer's count rises unless it killed itself (D12).
    private void Kill(PlayerEntity victim, PlayerEntity? killer, DeathCause cause = DeathCause.Zone)
```

변경 전:

```csharp
        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = victim.EntityId, KillerId = killer?.EntityId ?? 0, Placement = placement });
```

변경 후:

```csharp
        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied
        {
            VictimId = victim.EntityId, KillerId = killer?.EntityId ?? 0, Placement = placement, Cause = killer == null ? cause : DeathCause.Zone,
        });
```

변경 전:

```csharp
    private void Respawn(PlayerEntity player, Vector3 position)
    {
        // Keep the yaw so the camera does not snap; everything else starts over at the given position.
        player.State = new MoveState { Position = position, Yaw = player.State.Yaw };
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position);
        // The missed-input repeat starts over too: a late input right after the respawn must not replay a
```

변경 후:

```csharp
    // Phase 12: mode is the movement mode the player starts in (Transport aboard the drop transport).
    private void Respawn(PlayerEntity player, Vector3 position, MovementMode mode = MovementMode.Ground)
    {
        // Keep the yaw so the camera does not snap; everything else starts over at the given position (rested).
        player.State = new MoveState { Position = position, Yaw = player.State.Yaw, Mode = mode };
        player.Sprinting = false;
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position, mode);
        // The missed-input repeat starts over too: a late input right after the respawn must not replay a
```

변경 전:

```csharp
        var writer = new PacketWriter(_sendBuffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
```

변경 후:

```csharp
        var writer = new PacketWriter(_sendBuffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned
        {
            EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw, Mode = mode,
        });
```

변경 전:

```csharp
    // zone started. Respawn keeps Seq, so clients re-sync their prediction exactly as after a death.
    private void StartMatch(uint now)
    {
        ClearWorldItems();
```

변경 후:

```csharp
    // zone started. Respawn keeps Seq, so clients re-sync their prediction exactly as after a death.
    // Phase 12 D4, D5: with AirDrop everyone starts aboard the drop transport instead: the route (rolled with this
    // round's spawn seed, starting at the tick being simulated) goes out first, so the same ordered channel delivers it
    // before the PlayerRespawned events in Transport mode; and the zone's clock starts when the route ends.
    private void StartMatch(uint now)
    {
        ClearWorldItems();
        _hasRoute = _airDrop;
        if (_hasRoute)
        {
            _route = DropPlanner.Plan(unchecked(_spawnSeed + _flow.Round), now + 1, _simHz);
            foreach (var player in _players) SendRoute(player.PeerId);
        }
```

변경 전:

```csharp
        {
            Respawn(player, DropSpot(dropIndex++));
```

변경 후:

```csharp
        {
            if (_hasRoute) Respawn(player, _route.PositionAt(_route.StartTick), MovementMode.Transport);
            else Respawn(player, DropSpot(dropIndex++));
```

변경 전:

```csharp
        for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
        _zone.Start(now, unchecked(_zoneSeed + _flow.Round));
```

변경 후:

```csharp
        for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
        _zone.Start(_hasRoute ? _route.EndTick : now, unchecked(_zoneSeed + _flow.Round));
```

변경 전:

```csharp
        while (_graced.Count > 0) ExpireGraced(_graced[0]);
        ClearWorldItems();
```

변경 후:

```csharp
        while (_graced.Count > 0) ExpireGraced(_graced[0]);
        ClearWorldItems();
        _hasRoute = false;
```

변경 전:

```csharp
                    Yaw = p.State.Yaw,
                    Flags = p.Alive ? SnapshotEntity.AliveFlag : (byte)0,
                });
            }
            // Cannot overflow: 19 + 13 * 90 = 1189 bytes, and ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities.
```

변경 후:

```csharp
                    Yaw = p.State.Yaw,
                    // Phase 12 D11: alive, the mode, sprinting and exhausted in the one flag byte.
                    Flags = SnapshotEntity.MakeFlags(p.Alive, p.State.Mode, p.Sprinting, p.State.Exhausted),
                });
            }
            // Cannot overflow: 27 + 13 * 90 = 1197 bytes, and ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities.
```

변경 전:

```csharp
            ReloadRemainingTicks = reloadRemaining,
```

변경 후:

```csharp
            ReloadRemainingTicks = reloadRemaining,
            // Phase 12 D11: what the owner's prediction needs beyond the entity.
            Energy = (ushort)(MoveState.MaxEnergyHundredths - p.State.EnergySpent),
            HorizontalVelocity = p.State.HorizontalVelocity,
            ModeTicks = p.State.ModeTicks,
            EnergyDelayTicks = p.State.EnergyDelayTicks,
```

변경 전:

```csharp
        // The match ended while it was away: FinishMatch sent its result to no connection, so it gets it now.
```

변경 후:

```csharp
        // Phase 12 D16: the route, so a rider (or a jumper) predicts from it; the mode comes with the next snapshot.
        if (_hasRoute) SendRoute(peerId);
        // The match ended while it was away: FinishMatch sent its result to no connection, so it gets it now.
```

**새 파일** `Server/src/ProjectH.Server/Game/MovementLimits.cs`:

```csharp
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 12 D12: the movement self-check. The server simulates every move itself from inputs only, so a move faster than
// its mode allows is a bug in the simulation, never a cheat: Match only counts it (movementAnomalies on the Health line).
// Each limit is the fastest horizontal speed of the mode plus the fastest vertical one, from MovementTuning; a tick may
// move at most that x dt x Slack.
public static class MovementLimits
{
    public const float Slack = 1.5f;

    public static float MaxSpeed(MovementMode mode)
    {
        switch (mode)
        {
            case MovementMode.Ground:
            case MovementMode.Crouch:
            case MovementMode.Slide:
                // A slide jump carries the slide's top speed; no fall on this map reaches freefall speed.
                return MovementTuning.SlideMaxSpeed + MovementTuning.FreefallTerminalSpeed;
            case MovementMode.Vault:
                // The longest hurdle (reach, the deepest obstacle, the body and the landing gap) in the shortest vault, plus
                // the highest mantle in that time; a slope behind the obstacle adds at most MaxSlope of the distance.
                float reach = MovementTuning.VaultReach + MovementTuning.HurdleMaxDepth + 2f * MoveSettings.HalfWidth + MovementTuning.HurdleLandingGap;
                return (reach * (1f + MoveSettings.MaxSlope) + MovementTuning.MantleMaxHeight) / MovementTuning.HurdleSeconds;
            case MovementMode.Freefall:
                return MovementTuning.FreefallForwardSpeed + MovementTuning.FreefallTerminalSpeed;
            case MovementMode.Glide:
                return MovementTuning.GlideForwardSpeed + MovementTuning.GlideFallSpeed;
            default:
                return 0f;   // Transport: placed by DropTransport.Ride, never stepped
        }
    }
}
```

**수정** `Server/src/ProjectH.Server/Game/PlayerEntity.cs`:

변경 전:

```csharp
    public MoveState State;
```

변경 후:

```csharp
    public MoveState State;
    // Phase 12 D11: whether the last step sprinted (the snapshot flag).
    public bool Sprinting;
```

**수정** `Server/src/ProjectH.Server/GameLoop.cs`:

변경 전:

```csharp
    private Match NewMatch() => new(_options, _data, SendToPeer, _loadout, dropPoints: _dropPoints, matchSink: _matchSink,
        graceExpired: OnGraceExpired);
```

변경 후:

```csharp
    private Match NewMatch() => new(_options, _data, SendToPeer, _loadout, dropPoints: _dropPoints, matchSink: _matchSink,
        graceExpired: OnGraceExpired, movementAnomaly: _health.AddMovementAnomaly);
```

변경 전:

```csharp
            "inputRate={BadRate} wrongDirection={BadDirection} handlerException={BadHandler} " +
            "tickFailures={TickFailures} loopFailures={LoopFailures} matchResets={Resets} stalls={Stalls} " +
```

변경 후:

```csharp
            "inputRate={BadRate} wrongDirection={BadDirection} handlerException={BadHandler} " +
            "tickFailures={TickFailures} loopFailures={LoopFailures} matchResets={Resets} stalls={Stalls} movementAnomalies={MovementAnomalies} " +
```

변경 전:

```csharp
            h.BadPackets(BadPacketReason.HandlerException),
            h.TickFailures, h.LoopFailures, h.MatchResets, h.Stalls,
```

변경 후:

```csharp
            h.BadPackets(BadPacketReason.HandlerException),
            h.TickFailures, h.LoopFailures, h.MatchResets, h.Stalls, h.MovementAnomalies,
```

**수정** `Server/src/ProjectH.Server/ServerOptions.cs`:

변경 전:

```csharp
    public int SpawnSeed { get; set; } = 1;
```

변경 후:

```csharp
    public int SpawnSeed { get; set; } = 1;
    // Phase 12 D4, D5: a match starts aboard the drop transport (its route is rolled with SpawnSeed + round number) and
    // the zone's clock starts when the route ends. Off = everyone starts on a drop point as in Phases 6-11 (most rule
    // tests, and a load run that compares with them). Never with DevRespawn.
    public bool AirDrop { get; set; } = true;
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 1021개(1012 통과, 9개 건너뜀)
Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

`AirDrop`을 켠 채 기존 테스트를 돌리면 Phase 5–11 규칙 테스트 30개가 실패한다. 땅에서 시작한다고 가정하기 때문이다. 그래서 `RoyaleHarness`의 기본값이 꺼짐이다(Spec과 다른 점 12, 19).

- [ ] **Step 5: Commit** — `feat(server): deploy matches from the drop transport, fall damage, action gating, mode hit boxes and the movement self-check (Phase 12)`

---

### Task 6: 문

**Files:**
- Create: `Server/src/ProjectH.Server/Game/Doors.cs`(`DoorSet`, `DoorRules`), `Client/Assets/Scripts/Game/DoorRule.cs`
- Modify: `Shared/Runtime/Simulation/GameMap.cs`, `MovementTuning.cs`, `Shared/Runtime/Protocol/TraversalPackets.cs`, `Server/src/ProjectH.Server/Game/Match.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Game/DoorTests.cs`
  - Modify: `Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj`(`DoorRule.cs` 소스 링크)

**Interfaces:**
- Produces(Shared):
  - `GameMap`: `Doors`(5개, `Boxes`에 들어가지 않는다), `DoorCount` 5, `DoorThickness` 0.2
    - 문 순서: Rustvale 집 셋(남, 남, 북), Gearworks(북, 남)
  - `MovementTuning.DoorInteractRange` 2.5, `DoorInteractHalfAngle` 60
  - `DoorStatesPacket.TryRead`가 없는 문의 비트를 거부한다.
- Produces(Server):
  - `DoorSet`: `OpenMask`, `World`(상자 + 닫힌 문), `IsOpen`, `DoorAt(worldIndex)`, `Set(door, open)`, `CloseAll()`
  - `DoorRules.FindTarget(Vector3 feet, float yaw, ReadOnlySpan<Box> doors)` → 문 번호 또는 -1
  - `Match.Doors`(테스트용)
- Produces(Client): `ProjectH.Client.Game.DoorRule.FindTarget`. 서버 규칙과 같은 코드이고 UnityEngine을 쓰지 않는다.

`Match`의 이동·사격·떨어뜨리기는 모두 `_doors.World`와 충돌한다. 문이 바뀌는 경우는 다음 셋이다.

- 돌진(달리기·슬라이드)이 닫힌 문에 막히면 연다.
- E는 문이 먼저다. 열린 문은 그 자리에 아무도 없을 때 닫는다.
- 경기를 시작하면 모두 닫는다.

바뀐 Tick의 끝에 `DoorStates`를 모두에게 보낸다. Join·Resume에도 보낸다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Game/DoorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 12 D9 (spec §2 문): the door boxes, the collision world, the E rule (and the client's copy of it), opening and
// closing, the occupied doorway, the shoulder bash, DoorStates and the round start. Door 0 is Rustvale's first house's
// south door: centre (-54, z 50.25), 1.5 m wide; the tests stand south of it, facing north (yaw 0).
public class DoorTests
{
    private const float Dt = 1f / 30f;
    private static readonly Box Door0 = GameMap.Doors[0];
    private static readonly Vector3 SouthOfDoor0 = Ground(-54f, 48.5f);

    private static Vector3 Ground(float x, float z) => new(x, GameMap.Terrain.Height(x, z), z);

    private readonly List<(int Peer, PacketId Id, byte[] Data)> _sent = new();
    private readonly Dictionary<int, uint> _seq = new();
    private readonly Match _match;

    public DoorTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => _sent.Add((peer, (PacketId)data[0], data.ToArray())), TestGameData.CombatLoadout);
    }

    private PlayerEntity Join(int peer, Vector3 feet, float yaw = 0f)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var player);
        player.State.Position = feet;
        player.State.Yaw = yaw;
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

    private void Press(PlayerEntity player, InputButtons buttons, float yaw = 0f)
    {
        Send(player, new InputCommand { Buttons = buttons, Yaw = yaw });
        _match.Tick();
    }

    private List<byte> DoorStatesTo(int peer) => _sent.Where(s => s.Peer == peer && s.Id == PacketId.DoorStates).Select(s =>
    {
        var r = new PacketReader(s.Data);
        r.TryReadPacketId(out _);
        Assert.True(DoorStatesPacket.TryRead(ref r, out byte mask));
        return mask;
    }).ToList();

    // ---- The map's doors ----

    [Fact]
    public void FiveDoors_FillTheirWallGaps_AndTouchNoOtherBox()
    {
        ReadOnlySpan<Box> doors = GameMap.Doors;
        Assert.Equal(GameMap.DoorCount, doors.Length);
        for (int i = 0; i < doors.Length; i++)
        {
            Box d = doors[i];
            Assert.Equal(1.5f, d.Size.X, 4);
            Assert.Equal(GameMap.DoorThickness, d.Size.Z, 4);
            Assert.Equal(3f, d.Size.Y, 4);
            // A wall segment ends exactly at each side of the door, and the door sits inside the wall's thickness.
            int sides = 0;
            foreach (Box b in GameMap.Boxes)
            {
                bool sameWall = b.Min.Z <= d.Min.Z && b.Max.Z >= d.Max.Z && b.Max.Y == d.Max.Y;
                if (sameWall && (MathF.Abs(b.Max.X - d.Min.X) < 1e-4f || MathF.Abs(b.Min.X - d.Max.X) < 1e-4f)) sides++;
            }
            Assert.Equal(2, sides);
            Assert.False(Overlaps(d), $"door {i} overlaps a box");
        }
    }

    private static bool Overlaps(Box d)
    {
        foreach (Box b in GameMap.Boxes)
        {
            if (d.Min.X < b.Max.X && d.Max.X > b.Min.X && d.Min.Y < b.Max.Y && d.Max.Y > b.Min.Y && d.Min.Z < b.Max.Z && d.Max.Z > b.Min.Z)
                return true;
        }
        return false;
    }

    [Fact]
    public void DoorStates_WithABitForNoDoor_IsRefused()
    {
        var buffer = new byte[2];
        var writer = new PacketWriter(buffer);
        DoorStatesPacket.Write(ref writer, 1 << GameMap.DoorCount);
        var reader = new PacketReader(buffer);
        reader.TryReadPacketId(out _);
        Assert.False(DoorStatesPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheDoorSet_PutsClosedDoorsInTheWorld_AndOnlyThose()
    {
        var doors = new DoorSet();
        int boxes = GameMap.Boxes.Length;
        Assert.Equal(boxes + GameMap.DoorCount, doors.World.Length);
        Assert.Equal(3, doors.DoorAt(boxes + 3));
        Assert.Equal(-1, doors.DoorAt(0));

        doors.Set(1, true);
        Assert.Equal(0b10, doors.OpenMask);
        Assert.Equal(boxes + GameMap.DoorCount - 1, doors.World.Length);
        Assert.Equal(2, doors.DoorAt(boxes + 1));   // door 1 left the world: the next closed one moved up
        Assert.Equal(-1, doors.DoorAt(boxes + GameMap.DoorCount - 1));

        doors.CloseAll();
        Assert.Equal(0, doors.OpenMask);
        Assert.Equal(boxes + GameMap.DoorCount, doors.World.Length);
    }

    // ---- The E rule ----

    [Fact]
    public void TheRule_TakesTheNearestDoor_InFront_WithinTwoAndAHalfMetres()
    {
        Assert.Equal(0, DoorRules.FindTarget(SouthOfDoor0, 0f, GameMap.Doors));
        Assert.Equal(0, DoorRules.FindTarget(SouthOfDoor0, 55f, GameMap.Doors));        // within 60 degrees
        Assert.Equal(-1, DoorRules.FindTarget(SouthOfDoor0, 70f, GameMap.Doors));       // beyond
        Assert.Equal(-1, DoorRules.FindTarget(SouthOfDoor0, 180f, GameMap.Doors));      // facing away
        Assert.Equal(-1, DoorRules.FindTarget(Ground(-54f, 47.6f), 0f, GameMap.Doors)); // 2.65 m away
        Assert.Equal(-1, DoorRules.FindTarget(new Vector3(-54f, 3.25f, 50.25f), 0f, GameMap.Doors));   // on the roof
    }

    [Fact]
    public void TheClientsCopy_PicksTheSameDoor()
    {
        var random = new Random(12);
        for (int n = 0; n < 20_000; n++)
        {
            Box door = GameMap.Doors[random.Next(GameMap.DoorCount)];
            var feet = new Vector3(door.Center.X + (float)(random.NextDouble() * 8 - 4), (float)(random.NextDouble() * 5 - 1),
                door.Center.Z + (float)(random.NextDouble() * 8 - 4));
            float yaw = (float)(random.NextDouble() * 720 - 360);
            Assert.Equal(DoorRules.FindTarget(feet, yaw, GameMap.Doors), DoorRule.FindTarget(feet, yaw, GameMap.Doors));
        }
    }

    // ---- Opening and closing (Match) ----

    [Fact]
    public void AClosedDoor_Blocks_E_OpensIt_AndTheDoorwayPasses()
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        for (int i = 0; i < 30; i++)
        {
            Send(a, new InputCommand { MoveY = 1f, Yaw = 0f });
            _match.Tick();
        }
        Assert.True(a.State.Position.Z + MoveSettings.HalfWidth <= Door0.Min.Z);

        Press(a, InputButtons.Interact);
        Assert.True(_match.Doors.IsOpen(0));
        Assert.Equal(new byte[] { 0, 1 }, DoorStatesTo(1));   // at the join, then the change
        for (int i = 0; i < 30; i++)
        {
            Send(a, new InputCommand { MoveY = 1f, Yaw = 0f });
            _match.Tick();
        }
        Assert.True(a.State.Position.Z > Door0.Max.Z + 0.5f);
    }

    [Fact]
    public void E_OnAnOpenDoor_ClosesIt_UnlessSomeoneStandsInTheDoorway()
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        PlayerEntity b = Join(2, Ground(-54f, 50.25f));   // in the doorway (the door is closed: b stands in it)
        _match.Doors.Set(0, true);
        _match.Tick();

        Press(a, InputButtons.Interact);
        Assert.True(_match.Doors.IsOpen(0));   // b is in the way

        b.State.Position = Ground(-54f, 53f);  // inside the house
        Press(a, InputButtons.Interact);
        Assert.False(_match.Doors.IsOpen(0));
    }

    [Fact]
    public void E_ActsOnTheDoorFirst_AndOnItemsWithoutADoorInFront()
    {
        PlayerEntity a = Join(1, SouthOfDoor0);
        Press(a, InputButtons.Interact);
        Assert.True(_match.Doors.IsOpen(0));
        Assert.DoesNotContain(_sent, s => s.Peer == 1 && s.Id == PacketId.PickupResult);

        Press(a, InputButtons.Interact, yaw: 180f);   // facing away: no door, so a pickup (nothing here)
        Assert.True(_match.Doors.IsOpen(0));
        Assert.Single(_sent, s => s.Peer == 1 && s.Id == PacketId.PickupResult);
    }

    [Fact]
    public void SprintingIntoAClosedDoor_ShouldersItOpen_WalkingDoesNot()
    {
        PlayerEntity walker = Join(1, SouthOfDoor0);
        for (int i = 0; i < 30; i++)
        {
            Send(walker, new InputCommand { MoveY = 1f, Yaw = 0f });
            _match.Tick();
        }
        Assert.False(_match.Doors.IsOpen(0));

        walker.State.Position = Ground(-54f, 46f);
        for (int i = 0; i < 30 && !_match.Doors.IsOpen(0); i++)
        {
            Send(walker, new InputCommand { MoveY = 1f, Yaw = 0f, Buttons = InputButtons.Sprint });
            _match.Tick();
        }
        Assert.True(_match.Doors.IsOpen(0));
        for (int i = 0; i < 20; i++)
        {
            Send(walker, new InputCommand { MoveY = 1f, Yaw = 0f, Buttons = InputButtons.Sprint });
            _match.Tick();
        }
        Assert.True(walker.State.Position.Z > Door0.Max.Z + 0.5f);
    }

    [Fact]
    public void AClosedDoor_StopsAShot_AnOpenOneDoesNot()
    {
        PlayerEntity shooter = Join(1, Ground(-54f, 46f));
        PlayerEntity target = Join(2, Ground(-54f, 53f));
        int Hits() => _sent.Count(s => s.Peer == 1 && s.Id == PacketId.HitConfirmed);
        void Shoot()
        {
            TestAim.YawPitch(shooter.State.Position, target.State.Position + new Vector3(0f, 1.2f, 0f), out float yaw, out float pitch);
            Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = _match.ServerTick });
            _match.Tick();
            for (int i = 0; i < 3; i++) _match.Tick();   // the weapon's fire interval
        }
        Shoot();
        Assert.Equal(0, Hits());
        _match.Doors.Set(0, true);
        Shoot();
        Assert.Equal(1, Hits());
    }

    [Fact]
    public void ANewcomer_GetsTheDoors_AndChangesGoToEveryone()
    {
        _match.Doors.Set(3, true);
        Join(1, Ground(0f, 3f));
        Assert.Equal(new byte[] { 0b1000 }, DoorStatesTo(1));
        Join(2, Ground(0f, -3f));
        _match.Doors.Set(4, true);
        _match.Tick();
        Assert.Equal(0b11000, DoorStatesTo(1).Last());
        Assert.Equal(0b11000, DoorStatesTo(2).Last());
        _match.Tick();
        Assert.Equal(2, DoorStatesTo(1).Count);   // nothing more without a change
    }

    [Fact]
    public void TheMatchStart_ClosesEveryDoor()
    {
        var h = new RoyaleHarness();
        h.Join(1);
        h.Join(2);
        h.Match.Doors.Set(0, true);
        h.Match.Doors.Set(2, true);
        h.RunToMatch();
        Assert.Equal(0, h.Match.Doors.OpenMask);
        var last = h.SentTo(1, PacketId.DoorStates).Last();
        Assert.Equal(0, last.Data[1]);
    }
}
```

**수정** `Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj`:

변경 전:

```xml
    <Compile Include="..\..\..\Client\Assets\Scripts\UI\KillFeedModel.cs" Link="ClientCopies\UI\KillFeedModel.cs" />
```

변경 후:

```xml
    <Compile Include="..\..\..\Client\Assets\Scripts\UI\KillFeedModel.cs" Link="ClientCopies\UI\KillFeedModel.cs" />
    <!-- Phase 12 D9: the client's copy of the door rule, compared with the server's DoorRules (DoorTests). -->
    <Compile Include="..\..\..\Client\Assets\Scripts\Game\DoorRule.cs" Link="ClientCopies\DoorRule.cs" />
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 컴파일 실패(`GameMap.Doors`, `DoorSet`, `DoorRules`, `ProjectH.Client.Game.DoorRule`이 없다)

- [ ] **Step 3: 문을 구현한다**

**새 파일** `Client/Assets/Scripts/Game/DoorRule.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Copy of the server's door rule (Server DoorRules.FindTarget, Phase 12 D9), used to predict E on a door and to show
    // the "[E] 문" prompt. The server still decides. Keep the two in step (DoorRuleParityTests compares them): the nearest
    // door whose centre is within DoorInteractRange of the feet across the ground and within DoorInteractHalfAngle of
    // the facing direction, on the door's floor. Pure, no allocation, no UnityEngine (the server tests compile it).
    public static class DoorRule
    {
        public static int FindTarget(Vector3 feet, float yaw, ReadOnlySpan<Box> doors)
        {
            float radians = yaw * (MathF.PI / 180f);
            float forwardX = MathF.Sin(radians);
            float forwardZ = MathF.Cos(radians);
            float cosLimit = MathF.Cos(MovementTuning.DoorInteractHalfAngle * (MathF.PI / 180f));
            int best = -1;
            float bestSq = MovementTuning.DoorInteractRange * MovementTuning.DoorInteractRange;
            for (int i = 0; i < doors.Length; i++)
            {
                ref readonly Box door = ref doors[i];
                if (feet.Y < door.Min.Y - 1f || feet.Y > door.Max.Y) continue;
                float dx = (door.Min.X + door.Max.X) * 0.5f - feet.X;
                float dz = (door.Min.Z + door.Max.Z) * 0.5f - feet.Z;
                float distanceSq = dx * dx + dz * dz;
                if (distanceSq > bestSq) continue;
                float distance = MathF.Sqrt(distanceSq);
                if (distance > 1e-4f && dx * forwardX + dz * forwardZ < cosLimit * distance) continue;
                best = i;
                bestSq = distanceSq;
            }
            return best;
        }
    }
}
```

**새 파일** `Server/src/ProjectH.Server/Game/Doors.cs`:

```csharp
using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 12 D9: which doors are open, and the collision world that makes: GameMap.Boxes followed by the closed doors.
// The world lives in one fixed array; only the door part is rewritten, and only when a door changes, so a tick
// allocates nothing. Owned by Match on the game loop thread.
public sealed class DoorSet
{
    private readonly Box[] _world = new Box[GameMap.Boxes.Length + GameMap.DoorCount];
    // World index - GameMap.Boxes.Length -> door index, for the closed doors in the world.
    private readonly int[] _doorOfSlot = new int[GameMap.DoorCount];
    private int _length;

    public DoorSet()
    {
        GameMap.Boxes.CopyTo(_world);
        Rebuild();
    }

    // Bit i = GameMap.Doors[i] is open (the DoorStates packet).
    public byte OpenMask { get; private set; }

    // What every move, shot and drop of this match collides with.
    public ReadOnlySpan<Box> World => new(_world, 0, _length);

    public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

    // The door whose box is at this index of World, or -1 for a map box (or an index outside World).
    public int DoorAt(int worldIndex)
    {
        int slot = worldIndex - GameMap.Boxes.Length;
        return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
    }

    public void Set(int door, bool open)
    {
        int bit = 1 << door;
        byte mask = (byte)(open ? OpenMask | bit : OpenMask & ~bit);
        if (mask == OpenMask) return;
        OpenMask = mask;
        Rebuild();
    }

    // D9: a round starts with every door closed.
    public void CloseAll()
    {
        if (OpenMask == 0) return;
        OpenMask = 0;
        Rebuild();
    }

    private void Rebuild()
    {
        int length = GameMap.Boxes.Length;
        ReadOnlySpan<Box> doors = GameMap.Doors;
        for (int i = 0; i < doors.Length; i++)
        {
            if (IsOpen(i)) continue;
            _doorOfSlot[length - GameMap.Boxes.Length] = i;
            _world[length++] = doors[i];
        }
        _length = length;
    }
}

// Phase 12 D9: which door E acts on. Server rule; the client keeps a copy (Client/Assets/Scripts/Game/DoorRule.cs) only to
// predict the door and show the prompt, and DoorRuleParityTests keeps the two the same.
public static class DoorRules
{
    // The nearest door whose centre is within DoorInteractRange of the feet across the ground and within
    // DoorInteractHalfAngle of the facing direction (yaw 0 = +Z), on the door's floor (the feet between 1 m below its
    // bottom and its top), or -1. Pure, no allocation.
    public static int FindTarget(Vector3 feet, float yaw, ReadOnlySpan<Box> doors)
    {
        float radians = yaw * (MathF.PI / 180f);
        float forwardX = MathF.Sin(radians);
        float forwardZ = MathF.Cos(radians);
        float cosLimit = MathF.Cos(MovementTuning.DoorInteractHalfAngle * (MathF.PI / 180f));
        int best = -1;
        float bestSq = MovementTuning.DoorInteractRange * MovementTuning.DoorInteractRange;
        for (int i = 0; i < doors.Length; i++)
        {
            ref readonly Box door = ref doors[i];
            if (feet.Y < door.Min.Y - 1f || feet.Y > door.Max.Y) continue;
            float dx = (door.Min.X + door.Max.X) * 0.5f - feet.X;
            float dz = (door.Min.Z + door.Max.Z) * 0.5f - feet.Z;
            float distanceSq = dx * dx + dz * dz;
            if (distanceSq > bestSq) continue;
            float distance = MathF.Sqrt(distanceSq);
            if (distance > 1e-4f && dx * forwardX + dz * forwardZ < cosLimit * distance) continue;
            best = i;
            bestSq = distanceSq;
        }
        return best;
    }
}
```

**수정** `Server/src/ProjectH.Server/Game/Match.cs`:

변경 전:

```csharp
    private DropRoute _route;
```

변경 후:

```csharp
    private DropRoute _route;
    // Phase 12 D9: the doors and the collision world they make (map boxes + closed doors); every move, shot and drop
    // uses _doors.World. _sentDoors is what every client was last told (DoorStates when it changes).
    private readonly DoorSet _doors = new();
    private byte _sentDoors;
```

변경 전:

```csharp
    internal DropRoute Route => _route;
```

변경 후:

```csharp
    internal DropRoute Route => _route;
    internal DoorSet Doors => _doors;
```

변경 전:

```csharp
        // Phase 12 D16: a newcomer during an air-drop match sees the transport too.
```

변경 후:

```csharp
        SendDoors(peerId);   // Phase 12 D9
        // Phase 12 D16: a newcomer during an air-drop match sees the transport too.
```

변경 전:

```csharp
        SendMatchChanges();
```

변경 후:

```csharp
        SendMatchChanges();
        SendDoorChanges();
```

변경 전:

```csharp
        Vector3 from = player.State.Position;
        MovementSimulation.Step(ref player.State, input, _tickSeconds, GameMap.Boxes, GameMap.Terrain, out StepResult step);
        player.Sprinting = step.Sprinting;
```

변경 후:

```csharp
        Vector3 from = player.State.Position;
        MovementSimulation.Step(ref player.State, input, _tickSeconds, _doors.World, GameMap.Terrain, out StepResult step);
        player.Sprinting = step.Sprinting;

        // D9: sprinting or sliding into a closed door shoulders it open; the move goes on next tick.
        if (step.Charging && step.BlockedBy >= 0)
        {
            int door = _doors.DoorAt(step.BlockedBy);
            if (door >= 0) _doors.Set(door, true);
        }
```

변경 전:

```csharp
        if ((input.Buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        if ((input.Buttons & InputButtons.Interact) != 0) Pickup(player);
```

변경 후:

```csharp
        if ((input.Buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        // Phase 12 D9: E acts on a door in front first, an item otherwise.
        if ((input.Buttons & InputButtons.Interact) != 0 && !ToggleDoor(player)) Pickup(player);
```

변경 전:

```csharp
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(shooter.State.Mode), 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, GameMap.Boxes, GameMap.Terrain);
```

변경 후:

```csharp
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(shooter.State.Mode), 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, _doors.World, GameMap.Terrain);   // a closed door stops it
```

변경 전:

```csharp
    // D8, D9: the server picks the nearest item in range itself; the client never names one, so it cannot
```

변경 후:

```csharp
    // Phase 12 D9: E on the door DoorRules picks opens it, or closes it when no living character stands in its place.
    // Returns false when no door is in reach (then E picks up an item).
    private bool ToggleDoor(PlayerEntity player)
    {
        int door = DoorRules.FindTarget(player.State.Position, player.State.Yaw, GameMap.Doors);
        if (door < 0) return false;
        if (!_doors.IsOpen(door)) _doors.Set(door, true);
        else if (!DoorOccupied(door)) _doors.Set(door, false);
        return true;
    }

    private bool DoorOccupied(int door)
    {
        ReadOnlySpan<Box> doorBox = GameMap.Doors.Slice(door, 1);
        foreach (var p in _players)
        {
            if (p.Alive && MovementSimulation.OverlapsAny(p.State.Position, MovementSimulation.CollisionHeight(p.State.Mode), doorBox)) return true;
        }
        return false;
    }

    // D8, D9: the server picks the nearest item in range itself; the client never names one, so it cannot
```

변경 전:

```csharp
            Vector3 dropOffset = ItemRules.Offset(player.State.Yaw, ItemRules.DropDistance);
            Vector3 dropAt = ItemRules.DropPosition(player.State.Position, dropOffset, GameMap.Boxes, GameMap.Terrain);
```

변경 후:

```csharp
            Vector3 dropOffset = ItemRules.Offset(player.State.Yaw, ItemRules.DropDistance);
            Vector3 dropAt = ItemRules.DropPosition(player.State.Position, dropOffset, _doors.World, GameMap.Terrain);
```

변경 전:

```csharp
        // world list only, so the ref into the inventory stays valid.
        if (SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, GameMap.Boxes, GameMap.Terrain), -1) == 0) return;
```

변경 후:

```csharp
        // world list only, so the ref into the inventory stays valid.
        if (SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, _doors.World, GameMap.Terrain), -1) == 0) return;
```

변경 전:

```csharp
        Vector3 offset = ItemRules.Offset(player.State.Yaw + 360f * n / count, ItemRules.DeathDropRadius);
        return SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, GameMap.Boxes, GameMap.Terrain), -1) != 0;
```

변경 후:

```csharp
        Vector3 offset = ItemRules.Offset(player.State.Yaw + 360f * n / count, ItemRules.DeathDropRadius);
        return SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, _doors.World, GameMap.Terrain), -1) != 0;
```

변경 전:

```csharp
            foreach (var p in _players) SendZoneState(p.PeerId, zone);
        }
```

변경 후:

```csharp
            foreach (var p in _players) SendZoneState(p.PeerId, zone);
        }
    }

    // Phase 12 D9: at the end of a tick in which a door changed, the doors to everyone (one small packet however many
    // changed).
    private void SendDoorChanges()
    {
        if (_doors.OpenMask == _sentDoors) return;
        _sentDoors = _doors.OpenMask;
        foreach (var p in _players) SendDoors(p.PeerId);
    }

    private void SendDoors(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        DoorStatesPacket.Write(ref writer, _doors.OpenMask);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
```

변경 전:

```csharp
        _leftParticipants.Clear();
```

변경 후:

```csharp
        _leftParticipants.Clear();
        // Phase 12 D9: every door closed (everyone is aboard or on a drop point, clear of every box); the change goes out
        // at the end of this tick.
        _doors.CloseAll();
```

변경 전:

```csharp
        // The match ended while it was away: FinishMatch sent its result to no connection, so it gets it now.
```

변경 후:

```csharp
        SendDoors(peerId);
        // The match ended while it was away: FinishMatch sent its result to no connection, so it gets it now.
```

**수정** `Shared/Runtime/Protocol/TraversalPackets.cs`:

변경 전:

```csharp
        public static bool TryRead(ref PacketReader reader, out byte openMask)
        {
            return reader.TryReadByte(out openMask);
        }
```

변경 후:

```csharp
        // A bit for a door the map does not have is refused.
        public static bool TryRead(ref PacketReader reader, out byte openMask)
        {
            return reader.TryReadByte(out openMask) && openMask >> GameMap.DoorCount == 0;
        }
```

**수정** `Shared/Runtime/Simulation/GameMap.cs`:

변경 전:

```csharp
        private const float DoorWidth = 1.5f;

        private static readonly Box[] s_boxes;
```

변경 후:

```csharp
        private const float DoorWidth = 1.5f;
        // Phase 12 D9: a door fills its gap: DoorWidth wide, the wall's height, DoorThickness thick, centred in the wall.
        public const float DoorThickness = 0.2f;
        public const int DoorCount = 5;

        private static readonly Box[] s_boxes;
        private static readonly Box[] s_doors;
```

변경 전:

```csharp
            var boxes = new List<Box>(128);
```

변경 후:

```csharp
            var boxes = new List<Box>(128);
            var doors = new List<Box>(DoorCount);
```

변경 전:

```csharp
            // Rustvale (village, north-west): three 8 x 8 m houses.
            AddHouse(boxes, -54f, 54f, 8f, 8f, doorSouth: true);
            AddHouse(boxes, -38f, 52f, 8f, 8f, doorSouth: true);
            AddHouse(boxes, -50f, 38f, 8f, 8f, doorSouth: false, doorNorth: true);

            // Gearworks (depot, north-east): one 16 x 12 m warehouse with a door on each long side, crates around it.
            AddHouse(boxes, 46f, 50f, 16f, 12f, doorSouth: true, doorNorth: true);
```

변경 후:

```csharp
            // Rustvale (village, north-west): three 8 x 8 m houses.
            AddHouse(boxes, doors, -54f, 54f, 8f, 8f, doorSouth: true);
            AddHouse(boxes, doors, -38f, 52f, 8f, 8f, doorSouth: true);
            AddHouse(boxes, doors, -50f, 38f, 8f, 8f, doorSouth: false, doorNorth: true);

            // Gearworks (depot, north-east): one 16 x 12 m warehouse with a door on each long side, crates around it.
            AddHouse(boxes, doors, 46f, 50f, 16f, 12f, doorSouth: true, doorNorth: true);
```

변경 전:

```csharp
            s_boxes = boxes.ToArray();
        }

        public static ReadOnlySpan<Box> Boxes => s_boxes;
```

변경 후:

```csharp
            s_boxes = boxes.ToArray();
            s_doors = doors.ToArray();
        }

        public static ReadOnlySpan<Box> Boxes => s_boxes;

        // Phase 12 D9: the doors, in the order above (the three Rustvale houses: south, south, north; then Gearworks: north, south). Not part of
        // Boxes: a door is open or closed, and only the closed ones join the collision world (DoorStates bit i = Doors[i]).
        public static ReadOnlySpan<Box> Doors => s_doors;
```

변경 전:

```csharp
        // short of the north and south walls, so no two walls touch side by side.
        private static void AddHouse(List<Box> boxes, float cx, float cz, float width, float depth, bool doorSouth, bool doorNorth = false)
        {
            float halfW = width * 0.5f;
            float halfD = depth * 0.5f;
            AddLongWall(boxes, cx, cz + halfD - WallThickness * 0.5f, halfW, doorNorth);
            AddLongWall(boxes, cx, cz - halfD + WallThickness * 0.5f, halfW, doorSouth);
```

변경 후:

```csharp
        // short of the north and south walls, so no two walls touch side by side.
        private static void AddHouse(List<Box> boxes, List<Box> doors, float cx, float cz, float width, float depth, bool doorSouth, bool doorNorth = false)
        {
            float halfW = width * 0.5f;
            float halfD = depth * 0.5f;
            AddLongWall(boxes, doors, cx, cz + halfD - WallThickness * 0.5f, halfW, doorNorth);
            AddLongWall(boxes, doors, cx, cz - halfD + WallThickness * 0.5f, halfW, doorSouth);
```

변경 전:

```csharp
        private static void AddLongWall(List<Box> boxes, float cx, float z, float halfW, bool door)
        {
```

변경 후:

```csharp
        private static void AddLongWall(List<Box> boxes, List<Box> doors, float cx, float z, float halfW, bool door)
        {
```

변경 전:

```csharp
            boxes.Add(new Box(new Vector3(cx + halfDoor, 0f, zMin), new Vector3(cx + halfW, WallHeight, zMax)));
```

변경 후:

```csharp
            boxes.Add(new Box(new Vector3(cx + halfDoor, 0f, zMin), new Vector3(cx + halfW, WallHeight, zMax)));
            doors.Add(new Box(new Vector3(cx - halfDoor, 0f, z - DoorThickness * 0.5f), new Vector3(cx + halfDoor, WallHeight, z + DoorThickness * 0.5f)));
```

**수정** `Shared/Runtime/Simulation/MovementTuning.cs`:

변경 전:

```csharp
        // Fall damage (D3, D10): the landing speed decides it. Server rule (CombatRules.FallDamage); the numbers are here
```

변경 후:

```csharp
        // Doors (D9): E toggles the nearest door within this distance (feet to the door's centre, across the ground) and
        // this angle either side of where the character faces. The server rule is DoorRules, the client's copy DoorRule.
        public const float DoorInteractRange = 2.5f;
        public const float DoorInteractHalfAngle = 60f;

        // Fall damage (D3, D10): the landing speed decides it. Server rule (CombatRules.FallDamage); the numbers are here
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 1033개(1024 통과, 9개 건너뜀)
Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

- [ ] **Step 5: Commit** — `feat: add doors (E to open and close, shoulder bash, DoorStates) (Phase 12 D9)`

---

### Task 7: 봇 투입

**Files:**
- Modify: `Server/src/ProjectH.Bots/BotView.cs`, `BotConnection.cs`, `BotBrain.cs`, `BotSteering.cs`
- Test: Create `Server/tests/ProjectH.Server.Tests/Bots/BotDeployTests.cs`

**Interfaces:**
- Produces(Bots):
  - `BotView`: `MyMode`(자기 Entity의 Flags 또는 `PlayerRespawned`), `HasRoute`, `Route`(`TransportRoute`)
  - `BotGoal.Deploy` 7
  - `BotBrain`
    - `LandingStopDistance` 5, `LandingTarget`, `JumpTick`
    - `static PlanJumpTick(in DropRoute, Vector3 target)`: 목표를 경로에 투영한 Tick. 뛰어내리기 구간 안으로 자른다.
  - `BotSteering.Stuck`

`BotBrain.Tick`은 살아 있으면 먼저 `Deploy`를 본다. 탑승·자유 낙하·글라이드 중에는 다음처럼 하고, 다른 목표는 보지 않는다.

1. 처음 한 번 목표를 고르고 뛰어내릴 Tick을 정한다. 목표는 POI 또는 받은 아이템이고, 봇의 시드 난수로 고른다.
2. 탑승 중에는 그 Tick부터 Jump를 누른다. 0.5초에 한 번까지다.
3. 낙하·글라이드 중에는 목표를 보고 앞으로 간다. 수평 5 m 안이면 멈춘다.

땅에 서면 Phase 7 규칙으로 돌아간다. 걷다가 막히면 달린다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Bots/BotDeployTests.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Bots;

// Phase 12 D15 (spec §2 봇): the landing target, the jump tick, riding, steering the fall, and the stuck sprint.
public class BotDeployTests
{
    // Along +X at 90 m: x -100..100 over ticks 1000..1300; the jump window is 1045..1255 (x -70..70).
    private static DropRoute AlongX() => new()
    {
        StartX = -100f, StartZ = 0f, EndX = 100f, EndZ = 0f, Altitude = 90f, StartTick = 1000, DurationTicks = 300,
    };

    private static BotView Aboard(uint serverTick = 1000)
    {
        BotView view = BotTestView.Create(new Vector3(-100f, 90f, 0f));
        view.MyMode = MovementMode.Transport;
        view.HasRoute = true;
        view.Route = AlongX();
        view.ServerTick = serverTick;
        view.HasMatchState = true;
        view.Match = new MatchState { State = MatchFlowState.Playing, Alive = 2, Participants = 2 };
        return view;
    }

    [Theory]
    [InlineData(0f, 1150u)]     // the centre: halfway
    [InlineData(40f, 1210u)]    // x 40 (its z does not matter)
    [InlineData(-95f, 1045u)]   // before the window opens: the window's first tick
    [InlineData(95f, 1255u)]    // after it closes: its last tick
    public void TheJumpTick_IsWhereTheTransportPassesClosest_InsideTheWindow(float x, uint tick)
    {
        Assert.Equal(tick, BotBrain.PlanJumpTick(AlongX(), new Vector3(x, 0f, 30f)));
    }

    [Fact]
    public void Aboard_TheBotSendsNoMove_AndJumpsFromItsTick_OnceEveryHalfSecond()
    {
        var brain = new BotBrain(3);
        BotView view = Aboard();
        Assert.True(brain.Tick(view, 0f, out InputCommand command));
        Assert.Equal(BotGoal.Deploy, brain.Goal);
        Assert.Equal(0f, command.MoveX);
        Assert.Equal(0f, command.MoveY);
        Assert.Equal(InputButtons.None, command.Buttons);
        uint jumpTick = brain.JumpTick;

        int jumps = 0;
        for (int i = 0; i < 30; i++)
        {
            view.ServerTick = jumpTick - 1 + (uint)i;
            brain.Tick(view, 1f + i / 30f, out command);
            if ((command.Buttons & InputButtons.Jump) != 0) jumps++;
            if (i == 0) Assert.Equal(InputButtons.Jump, command.Buttons);   // the tick before: it arrives in time
        }
        Assert.Equal(2, jumps);   // still aboard after half a second (as far as it knows): pressed again
    }

    [Fact]
    public void Falling_TheBotFacesItsTarget_AndFliesTowardsIt_ThenDropsStraight()
    {
        var brain = new BotBrain(5);
        BotView view = Aboard();
        brain.Tick(view, 0f, out _);
        Vector3 target = brain.LandingTarget;

        view.MyMode = MovementMode.Freefall;
        view.MyPosition = new Vector3(target.X - 30f, 60f, target.Z);
        brain.Tick(view, 1f, out InputCommand command);
        Assert.Equal(90f, command.Yaw, 2);   // the target is along +X
        Assert.Equal(1f, command.MoveY);
        Assert.Equal(InputButtons.None, command.Buttons);   // the glider is left to the server

        view.MyMode = MovementMode.Glide;
        view.MyPosition = new Vector3(target.X + 2f, 20f, target.Z + 2f);
        brain.Tick(view, 2f, out command);
        Assert.Equal(0f, command.MoveY);
        Assert.Equal(BotGoal.Deploy, brain.Goal);
    }

    [Fact]
    public void TheLandingTargets_AreOnTheMap_AndSpreadOverPoisAndItems()
    {
        var targets = new HashSet<Vector3>();
        for (int seed = 0; seed < 20; seed++)
        {
            var brain = new BotBrain(seed);
            BotView view = Aboard();
            BotTestView.AddItem(view, 7, ItemKind.Ammo, (byte)AmmoType.Light, new Vector3(30f, 0f, -20f));
            BotTestView.AddItem(view, 8, ItemKind.Ammo, (byte)AmmoType.Heavy, new Vector3(-12f, 0f, 61f));
            brain.Tick(view, 0f, out _);
            Vector3 t = brain.LandingTarget;
            Assert.True(MathF.Abs(t.X) < GameMap.HalfSize && MathF.Abs(t.Z) < GameMap.HalfSize);
            targets.Add(t);
        }
        Assert.True(targets.Count >= 4, $"only {targets.Count} different targets");
    }

    [Fact]
    public void OnceLanded_TheBotPlaysAsBefore()
    {
        var brain = new BotBrain(1);
        BotView view = Aboard();
        brain.Tick(view, 0f, out _);
        view.MyMode = MovementMode.Ground;
        view.MyPosition = new Vector3(0f, 0f, 0f);
        brain.Tick(view, 1f, out _);
        Assert.NotEqual(BotGoal.Deploy, brain.Goal);
    }

    [Fact]
    public void AboardWithoutARoute_TheBotWaits()
    {
        var brain = new BotBrain(1);
        BotView view = Aboard();
        view.HasRoute = false;
        Assert.True(brain.Tick(view, 0f, out InputCommand command));
        Assert.Equal(InputButtons.None, command.Buttons);
        view.HasRoute = true;
        brain.Tick(view, 0.1f, out _);
        Assert.NotEqual(0u, brain.JumpTick);
    }

    [Fact]
    public void AStuckBot_Sprints()
    {
        var steering = new BotSteering();
        var rng = new Random(1);
        var goal = new Vector3(5f, 0f, 0f);   // within SprintDistance: a walk
        steering.Reset(Vector3.Zero, goal, 0f);
        Assert.False(steering.Stuck);
        steering.Steer(Vector3.Zero, goal, BotSteering.CheckInterval + 0.01f, rng, out _, out bool jump, out _);
        Assert.True(jump);
        Assert.True(steering.Stuck);
        steering.Steer(new Vector3(1f, 0f, 0f), goal, 2f * BotSteering.CheckInterval + 0.02f, rng, out _, out _, out _);
        Assert.False(steering.Stuck);
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 컴파일 실패(`BotView.MyMode`, `BotBrain.PlanJumpTick`, `BotSteering.Stuck` 등이 없다)

- [ ] **Step 3: 봇을 구현한다**

**수정** `Server/src/ProjectH.Bots/BotBrain.cs`:

변경 전:

```csharp
    Wander = 6,    // walk to a random point in the zone (rule 7)
```

변경 후:

```csharp
    Wander = 6,    // walk to a random point in the zone (rule 7)
    Deploy = 7,    // Phase 12 D15: ride the transport, jump, steer the fall to the landing target
```

변경 전:

```csharp
    public const float FarRange = 25f;
```

변경 후:

```csharp
    public const float FarRange = 25f;
    // Phase 12 D15: closer than this to the landing target (across the ground) the bot stops steering and drops straight.
    public const float LandingStopDistance = 5f;
```

변경 전:

```csharp
    private InputButtons _healButton;
```

변경 후:

```csharp
    private InputButtons _healButton;
    // Phase 12 D15: one deployment's plan, made when the bot first sees itself aboard.
    private bool _deploying;
    private Vector3 _landingTarget;
    private uint _jumpTick;
```

변경 전:

```csharp
    public Vector3 GoalPoint { get; private set; }
```

변경 후:

```csharp
    public Vector3 GoalPoint { get; private set; }
    // Phase 12 D15: the current deployment's landing target and the server tick it jumps at.
    public Vector3 LandingTarget => _landingTarget;
    public uint JumpTick => _jumpTick;
```

변경 전:

```csharp
        if (view.HitsLanded != _hitsSeen)
```

변경 후:

```csharp
        // Phase 12 D15: aboard, falling or gliding nothing else is possible (D12): ride, jump, steer the fall.
        if (Deploy(view, now, ref command))
        {
            command.ViewTick = view.ServerTick;
            return true;
        }
        if (view.HitsLanded != _hitsSeen)
```

변경 전:

```csharp
        command.ViewTick = view.ServerTick;
        return true;
```

변경 후:

```csharp
        command.ViewTick = view.ServerTick;
        return true;
    }

    // D15: the plan is made once per deployment, from what a client knows: the route and the places it has heard of
    // (the POIs and the items the server sent; the loot points are server data). The brain's own seeded Random picks
    // one, so bots spread over the map. The jump is pressed from the planned tick on, at most every ButtonRepeatSeconds
    // (a snapshot shows the fall only a little later, and a second press in freefall would open the glider early: the
    // server opens it at GlideAutoDeployHeight anyway). In the air the bot faces the target and flies forward until it
    // is above it. Returns false in any other mode.
    private bool Deploy(BotView view, float now, ref InputCommand command)
    {
        MovementMode mode = view.MyMode;
        if (mode != MovementMode.Transport && mode != MovementMode.Freefall && mode != MovementMode.Glide)
        {
            _deploying = false;
            return false;
        }
        Goal = BotGoal.Deploy;
        if (!_deploying)
        {
            if (!view.HasRoute)
            {
                command.Yaw = _bodyYaw;
                return true;
            }
            _landingTarget = PickLandingTarget(view);
            _jumpTick = PlanJumpTick(view.Route, _landingTarget);
            _deploying = true;
        }

        Vector3 me = view.MyPosition;
        float yaw = BotAim.YawTo(me, _landingTarget);
        command.Yaw = yaw;
        command.AimYaw = yaw;
        _bodyYaw = yaw;
        if (mode == MovementMode.Transport)
        {
            if (view.ServerTick + 1 >= _jumpTick && now >= _nextButton)
            {
                command.Buttons |= InputButtons.Jump;
                _nextButton = now + ButtonRepeatSeconds;
            }
            return true;
        }
        command.MoveY = BotAim.HorizontalDistance(me, _landingTarget) > LandingStopDistance ? 1f : 0f;
        return true;
    }

    private Vector3 PickLandingTarget(BotView view)
    {
        ReadOnlySpan<MapPoi> pois = MapPois.All;
        int pick = _rng.Next(pois.Length + view.Items.Count);
        if (pick < pois.Length)
        {
            MapPoi poi = pois[pick];
            return new Vector3(poi.X, GameMap.Terrain.Height(poi.X, poi.Z), poi.Z);
        }
        int index = pick - pois.Length;
        foreach (KeyValuePair<ushort, WorldItemData> pair in view.Items)
        {
            if (index-- == 0) return pair.Value.Position;
        }
        return Vector3.Zero;   // not reached: pick is below the item count
    }

    // D15: the tick the transport passes closest to the target (the target projected on the route), inside the jump window.
    public static uint PlanJumpTick(in DropRoute route, Vector3 target)
    {
        float dx = route.EndX - route.StartX;
        float dz = route.EndZ - route.StartZ;
        float lengthSq = dx * dx + dz * dz;
        float s = lengthSq > 0f ? ((target.X - route.StartX) * dx + (target.Z - route.StartZ) * dz) / lengthSq : 0f;
        s = Math.Clamp(s, 0f, 1f);
        uint tick = route.StartTick + (uint)MathF.Round(s * route.DurationTicks);
        route.JumpWindow(out uint first, out uint last);
        return Math.Clamp(tick, first, last);
```

변경 전:

```csharp
        command.MoveY = 1f;
        if (BotAim.HorizontalDistance(me, goal) > SprintDistance) command.Buttons |= InputButtons.Sprint;
```

변경 후:

```csharp
        command.MoveY = 1f;
        // Phase 12 D15: stuck, it sprints too, so its unstick jump hurdles a low obstacle and a closed door gives way.
        if (BotAim.HorizontalDistance(me, goal) > SprintDistance || _steering.Stuck) command.Buttons |= InputButtons.Sprint;
```

**수정** `Server/src/ProjectH.Bots/BotConnection.cs`:

변경 전:

```csharp
                break;
        }
```

변경 후:

```csharp
                break;
            case PacketId.TransportRoute:
                if (TransportRoutePacket.TryRead(ref r, out var route))
                {
                    view.Route = route;
                    view.HasRoute = true;
                }
                break;
        }
```

**수정** `Server/src/ProjectH.Bots/BotSteering.cs`:

변경 전:

```csharp
    private float _bestTime;
```

변경 후:

```csharp
    private float _bestTime;

    // Phase 12 D15: the last progress check found no progress. The bot then sprints, so a closed door in the way is
    // shouldered open (D9) and a low obstacle is hurdled when it jumps (D8).
    public bool Stuck => _stuckChecks > 0;
```

**수정** `Server/src/ProjectH.Bots/BotView.cs`:

변경 전:

```csharp
using ProjectH.Shared.Protocol;
```

변경 후:

```csharp
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
    public bool Alive;
```

변경 후:

```csharp
    public bool Alive;
    // Phase 12 D15: our movement mode (our entity's flags, or PlayerRespawned).
    public MovementMode MyMode;
```

변경 전:

```csharp
    public MatchResult LastResult;
```

변경 후:

```csharp
    public MatchResult LastResult;
    // Phase 12 D15: the running match's drop transport route (TransportRoute).
    public bool HasRoute;
    public DropRoute Route;
```

변경 전:

```csharp
            Alive = entity.IsAlive;
```

변경 후:

```csharp
            Alive = entity.IsAlive;
            MyMode = entity.Mode;
```

변경 전:

```csharp
        MyPosition = respawned.Position;
```

변경 후:

```csharp
        MyPosition = respawned.Position;
        MyMode = respawned.Mode;
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 1043개(1034 통과, 9개 건너뜀). `BotIntegrationTests.FourBots_PlayAWholeMatch_AndTheNextRoundStarts`는 투입을 켠 경기를 끝까지 한다.
Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 46개 통과

- [ ] **Step 5: Commit** — `feat(bots): ride, jump towards a landing target, steer the fall, sprint when stuck (Phase 12 D15)`

---

### Task 8: Client 예측 (모든 모드, 수송기 탑승, 문, C·Ctrl)

**Files:**
- Create: `Client/Assets/Scripts/Game/PredictedDoors.cs`
- Modify:
  - `Client/Assets/Scripts/Game/`: `LocalPlayerPredictor.cs`(파일 전체), `AimSolver.cs`, `GameClient.cs`
  - `Client/Assets/Scripts/`: `Input/InputReader.cs`, `Net/NetClient.cs`
- Test:
  - Create: `Client/Assets/Tests/EditMode/MovementPredictionTests.cs`
  - Modify: `Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs`, `MapPredictionTests.cs`(Spec과 다른 점 19)

**Interfaces:**
- Produces(Client, namespace `ProjectH.Client.Game`):
  - `PredictedDoors`
    - 상수 `PredictionSeconds` 1
    - 속성: `OpenMask`, `Version`, `World`
    - 메서드: `IsOpen`, `DoorAt`, `ApplyServer(byte)`, `Predict(door, open, now)`, `Expire(now)`, `Reset()`
  - `LocalPlayerPredictor`
    - 생성: `LocalPlayerPredictor(int simHz, MoveState spawn, PredictedDoors doors = null)`
    - 경로·행동: `SetRoute(in DropRoute)`, `ActionsAllowedAt(uint seq)`, `static ActionsAllowed(MovementMode)`
    - 상태: `Mode`, `Energy`, `Exhausted`, `Sprinting`, `HorizontalSpeed`, `VerticalSpeed`
    - 보정 기록: `LastCorrection`, `Corrections`, `PredictedTick`
    - `Reconcile(in SnapshotEntity, in SnapshotSelf, uint ackSeq, uint serverTick)`. 예전 `Reconcile(entity, ack)`는 없앤다.
  - `AimSolver.CrouchEyeHeight` 1.0, `AimSolver.EyeHeightOf(MovementMode)`
  - `InputReader.CrouchHeld`, `InputReader.ResetCrouch()`
  - `NetClient.TransportRouteReceived(DropRoute)`, `NetClient.DoorStatesReceived(byte)`

`GameClient` 연결은 다음과 같다.

- `Crouch`를 눌린 상태로 보낸다.
- 예측기는 `PredictedDoors`와 경로를 쓴다. `Respawn`은 `PlayerRespawned.Mode`로 한다.
- 보정에는 `header.Self`와 `header.ServerTick`을 넘긴다.
- 무기 예측은 `ActionsAllowedAt`으로 막는다.
- 끊기면 문과 경로를 지운다.

- [ ] **Step 1: EditMode 테스트를 쓰고 고친다**

**수정** `Client/Assets/Tests/EditMode/LocalPlayerPredictorTests.cs`:

변경 전:

```csharp
        [Test]
        public void Advance_ProducesOneSeqPerStep_AndPacketHoldsNewestThree()
```

변경 후:

```csharp
        // Phase 12: the snapshot's self block (the owner's horizontal velocity, energy and tick counters) of a server
        // state, and of a rested player standing still (for hand-made entities).
        private static SnapshotSelf SelfOf(in MoveState s) => new SnapshotSelf
        {
            Energy = (ushort)(MoveState.MaxEnergyHundredths - s.EnergySpent),
            HorizontalVelocity = s.HorizontalVelocity,
            ModeTicks = s.ModeTicks,
            EnergyDelayTicks = s.EnergyDelayTicks,
        };

        private static readonly SnapshotSelf Rested = new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths };

        [Test]
        public void Advance_ProducesOneSeqPerStep_AndPacketHoldsNewestThree()
```

변경 전:

```csharp
            for (int i = 0; i < 2; i++) MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 2);
```

변경 후:

```csharp
            for (int i = 0; i < 2; i++) MovementSimulation.Step(ref server, new InputCommand { MoveY = 1f }, Step, GameMap.Boxes, GameMap.Terrain);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), SelfOf(server), 2, 0);
```

변경 전:

```csharp
            server.Position.X += 1f;
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 1);
```

변경 후:

```csharp
            server.Position.X += 1f;
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), SelfOf(server), 1, 0);
```

변경 전:

```csharp
            var predictor = NewPredictor();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), 0);
```

변경 후:

```csharp
            var predictor = NewPredictor();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), Rested, 0, 0);
```

변경 전:

```csharp
            Vector3 before = predictor.PredictedPosition;

            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), 0);
```

변경 후:

```csharp
            Vector3 before = predictor.PredictedPosition;

            predictor.Reconcile(Alive(new System.Numerics.Vector3(3f, 0f, 4f)), Rested, 0, 0);
```

변경 전:

```csharp
            var entity = Alive(new System.Numerics.Vector3(x, 0f, 0f), velocityY, yaw);
            predictor.Reconcile(entity, 0);
            predictor.Reconcile(entity, 1);
```

변경 후:

```csharp
            var entity = Alive(new System.Numerics.Vector3(x, 0f, 0f), velocityY, yaw);
            predictor.Reconcile(entity, Rested, 0, 0);
            predictor.Reconcile(entity, Rested, 1, 0);
```

변경 전:

```csharp
            var body = new System.Numerics.Vector3(0f, 0f, 0.1f);
            predictor.Reconcile(new SnapshotEntity { Position = body, Flags = 0 }, 1);
```

변경 후:

```csharp
            var body = new System.Numerics.Vector3(0f, 0f, 0.1f);
            predictor.Reconcile(new SnapshotEntity { Position = body, Flags = 0 }, Rested, 1, 0);
```

변경 전:

```csharp
            MovementSimulation.Step(ref server, new InputCommand { Seq = 5 }, Step, GameMap.Boxes, GameMap.Terrain);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), 5);
```

변경 후:

```csharp
            MovementSimulation.Step(ref server, new InputCommand { Seq = 5 }, Step, GameMap.Boxes, GameMap.Terrain);
            predictor.Reconcile(Alive(server.Position, server.VelocityY, server.Yaw), SelfOf(server), 5, 0);
```

변경 전:

```csharp
            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(9f, 0f, 9f), Flags = 0 }, 1);   // dead, but no PlayerDied yet
            Assert.AreEqual(before, predictor.PredictedPosition);

            predictor.SetDead();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(-9f, 0f, -9f)), 2);   // alive, but no PlayerRespawned yet
            Assert.AreEqual(before, predictor.PredictedPosition);
```

변경 후:

```csharp
            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(9f, 0f, 9f), Flags = 0 }, Rested, 1, 0);   // dead, but no PlayerDied yet
            Assert.AreEqual(before, predictor.PredictedPosition);

            predictor.SetDead();
            predictor.Reconcile(Alive(new System.Numerics.Vector3(-9f, 0f, -9f)), Rested, 2, 0);   // alive, but no PlayerRespawned yet
            Assert.AreEqual(before, predictor.PredictedPosition);
```

변경 전:

```csharp
            Vector3 afterRespawn = predictor.PredictedPosition;
            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(9f, 0f, 9f), Flags = 0 }, 4);   // old life's body
```

변경 후:

```csharp
            Vector3 afterRespawn = predictor.PredictedPosition;
            predictor.Reconcile(new SnapshotEntity { Position = new System.Numerics.Vector3(9f, 0f, 9f), Flags = 0 }, Rested, 4, 0);   // old life's body
```

**수정** `Client/Assets/Tests/EditMode/MapPredictionTests.cs`:

변경 전:

```csharp
            => new SnapshotEntity { Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw, Flags = SnapshotEntity.AliveFlag };
```

변경 후:

```csharp
            => new SnapshotEntity { Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw, Flags = SnapshotEntity.AliveFlag };

        // Phase 12: the owner's half of the snapshot (horizontal velocity, energy, tick counters).
        private static SnapshotSelf ToSelf(in MoveState s) => new SnapshotSelf
        {
            Energy = (ushort)(MoveState.MaxEnergyHundredths - s.EnergySpent),
            HorizontalVelocity = s.HorizontalVelocity,
            ModeTicks = s.ModeTicks,
            EnergyDelayTicks = s.EnergyDelayTicks,
        };
```

변경 전:

```csharp
            var server = spawn;
            var buffer = new byte[SnapshotEntity.Size];
```

변경 후:

```csharp
            var server = spawn;
            var buffer = new byte[SnapshotEntity.Size + SnapshotSelf.Size];
```

변경 전:

```csharp
                SnapshotEntity.Write(ref writer, ToEntity(server));
                var reader = new PacketReader(buffer);
                Assert.IsTrue(SnapshotEntity.TryRead(ref reader, out SnapshotEntity wire));
                predictor.Reconcile(wire, seq);
```

변경 후:

```csharp
                SnapshotEntity.Write(ref writer, ToEntity(server));
                SnapshotSelf.Write(ref writer, ToSelf(server));
                var reader = new PacketReader(buffer);
                Assert.IsTrue(SnapshotEntity.TryRead(ref reader, out SnapshotEntity wire));
                Assert.IsTrue(SnapshotSelf.TryRead(ref reader, out SnapshotSelf self));
                predictor.Reconcile(wire, self, seq, seq);
```

변경 전:

```csharp
                    stale.VelocityY += 0.02f;
                    predictor.Reconcile(stale, seq - 2);
                }
                else
                {
                    predictor.Reconcile(ToEntity(server), seq);
```

변경 후:

```csharp
                    stale.VelocityY += 0.02f;
                    predictor.Reconcile(stale, ToSelf(serverHistory[seq - 2]), seq - 2, seq - 2);
                }
                else
                {
                    predictor.Reconcile(ToEntity(server), ToSelf(server), seq, seq);
```

변경 전:

```csharp
            // Ruins pillar (-46, 1.5, -46) size 1 x 3 x 1: this position is its centre.
            predictor.Reconcile(new SnapshotEntity { Position = new Num.Vector3(-46f, 0f, -46f), Flags = SnapshotEntity.AliveFlag }, 1);
```

변경 후:

```csharp
            // Ruins pillar (-46, 1.5, -46) size 1 x 3 x 1: this position is its centre.
            predictor.Reconcile(new SnapshotEntity { Position = new Num.Vector3(-46f, 0f, -46f), Flags = SnapshotEntity.AliveFlag },
                new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths }, 1, 1);
```

**새 파일** `Client/Assets/Tests/EditMode/MovementPredictionTests.cs`:

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using Num = System.Numerics;

namespace ProjectH.Client.Tests
{
    // Phase 12 spec §2 예측: the same inputs give the same results on the server and in the prediction, in every mode. The
    // server replica below runs what Match.Tick runs (DropTransport.Ride at the tick, else Step against the map boxes and
    // the closed doors, a bash opens a door), one input per tick, and every second tick its state goes through a real
    // snapshot write and read (quantized entity, self block, DoorStates) into Reconcile. A prediction that agrees is
    // never corrected. Pure math only: these also run outside Unity (the EditTests tool).
    public class MovementPredictionTests
    {
        private const int SimHz = 30;
        private const float Step = 1f / SimHz;
        private const uint StartTick = 5000;

        private sealed class ServerReplica
        {
            public MoveState State;
            public bool Sprinting;
            public uint Tick = StartTick;
            public bool HasRoute;
            public DropRoute Route;
            public readonly PredictedDoors Doors = new PredictedDoors();   // only its server state is used

            public void Run(in InputCommand input)
            {
                Tick++;
                if (HasRoute && DropTransport.Ride(ref State, input, Route, Tick))
                {
                    Sprinting = false;
                    return;
                }
                MovementSimulation.Step(ref State, input, Step, Doors.World, GameMap.Terrain, out StepResult result);
                Sprinting = result.Sprinting;
                if (result.Charging && result.BlockedBy >= 0)
                {
                    int door = Doors.DoorAt(result.BlockedBy);
                    if (door >= 0) Doors.ApplyServer((byte)(Doors.OpenMask | (1 << door)));
                }
            }
        }

        private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
        private ServerReplica _server;
        private PredictedDoors _doors;
        private LocalPlayerPredictor _predictor;
        private byte _sentDoors;

        private void Start(MoveState spawn, DropRoute? route = null)
        {
            _server = new ServerReplica { State = spawn };
            _doors = new PredictedDoors();
            _predictor = new LocalPlayerPredictor(SimHz, spawn, _doors);
            _sentDoors = 0;
            if (route.HasValue)
            {
                _server.HasRoute = true;
                _server.Route = route.Value;
                _predictor.SetRoute(route.Value);
            }
            Snapshot(0);   // the join snapshot: nothing acked yet, it gives the tick
        }

        // One frame of exactly one step on both sides, then every second tick a snapshot.
        private void Frame(Vector2 move, float yaw, InputButtons held = InputButtons.None, InputButtons press = InputButtons.None)
        {
            InputButtons queued = press;
            Assert.AreEqual(1, _predictor.Advance(Step, move, yaw, held, ref queued));
            InputCommand sent = _predictor.InputAt(_predictor.LastSeq);
            _server.Run(sent);
            if (_server.Tick % 2 == 0) Snapshot(_predictor.LastSeq);
        }

        private void Frames(int count, Vector2 move, float yaw, InputButtons held = InputButtons.None)
        {
            for (int i = 0; i < count; i++) Frame(move, yaw, held);
        }

        private void Snapshot(uint ack)
        {
            MoveState s = _server.State;
            var writer = new PacketWriter(_buffer);
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = 1, Position = s.Position, VelocityY = s.VelocityY, Yaw = s.Yaw,
                Flags = SnapshotEntity.MakeFlags(true, s.Mode, _server.Sprinting, s.Exhausted),
            });
            SnapshotSelf.Write(ref writer, new SnapshotSelf
            {
                Health = 100,
                Energy = (ushort)(MoveState.MaxEnergyHundredths - s.EnergySpent),
                HorizontalVelocity = s.HorizontalVelocity,
                ModeTicks = s.ModeTicks,
                EnergyDelayTicks = s.EnergyDelayTicks,
            });
            var reader = new PacketReader(new System.ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.IsTrue(SnapshotEntity.TryRead(ref reader, out SnapshotEntity entity));
            Assert.IsTrue(SnapshotSelf.TryRead(ref reader, out SnapshotSelf self));
            // DoorStates (Reliable) when the server's doors changed.
            if (_server.Doors.OpenMask != _sentDoors)
            {
                _sentDoors = _server.Doors.OpenMask;
                _doors.ApplyServer(_sentDoors);
            }
            _predictor.Reconcile(entity, self, ack, _server.Tick);
        }

        private void AssertAgrees(string what)
        {
            Assert.AreEqual(0, _predictor.Corrections, what + ": corrections");
            Assert.AreEqual(_server.State.Mode, _predictor.Mode, what + ": mode");
            Assert.AreEqual(_server.State.Position.X, _predictor.PredictedPosition.x, 1e-5f, what);
            Assert.AreEqual(_server.State.Position.Y, _predictor.PredictedPosition.y, 1e-5f, what);
            Assert.AreEqual(_server.State.Position.Z, _predictor.PredictedPosition.z, 1e-5f, what);
        }

        private static MoveState At(float x, float z, float yaw = 0f) =>
            new MoveState { Position = new Num.Vector3(x, GameMap.Terrain.Height(x, z), z), Yaw = yaw };

        [Test]
        public void SprintingUntilExhausted_AndRecovering_Agrees()
        {
            Start(At(-40f, 0f));
            Frames(160, Vector2.up, 90f, InputButtons.Sprint);    // empties the energy (150 ticks)
            Assert.IsTrue(_predictor.Exhausted);
            Frames(80, Vector2.up, 90f);                          // walking: the delay, then back above 20
            Assert.IsFalse(_predictor.Exhausted);
            Frames(10, Vector2.up, 90f, InputButtons.Sprint);
            AssertAgrees("sprint");
        }

        [Test]
        public void CrouchSlideAndSlideJump_Agree()
        {
            Start(At(-40f, 0f));
            Frames(10, Vector2.up, 90f, InputButtons.Sprint);
            Frames(15, Vector2.up, 90f, InputButtons.Sprint | InputButtons.Crouch);   // a slide
            Assert.AreEqual(MovementMode.Slide, _predictor.Mode);
            Frame(Vector2.up, 90f, InputButtons.Crouch, InputButtons.Jump);          // a slide jump
            Frames(30, Vector2.up, 90f);
            Frames(20, Vector2.up, 90f, InputButtons.Crouch);                        // a crouch walk
            Assert.AreEqual(MovementMode.Crouch, _predictor.Mode);
            AssertAgrees("slide");
        }

        [Test]
        public void HurdlingTheGearworksCrate_Agrees()
        {
            // Low crate (36, 0.5, 40): x 35..37. Sprint along +X, jump half a metre before its face.
            Start(At(31f, 40f, 90f));
            while (35f - (_predictor.PredictedPosition.x + MoveSettings.HalfWidth) > 0.5f) Frame(Vector2.up, 90f, InputButtons.Sprint);
            Frame(Vector2.up, 90f, InputButtons.Sprint, InputButtons.Jump);
            Assert.AreEqual(MovementMode.Vault, _predictor.Mode);
            Frames(20, Vector2.up, 90f, InputButtons.Sprint);
            Assert.Greater(_predictor.PredictedPosition.x, 37f);
            AssertAgrees("hurdle");
        }

        [Test]
        public void RidingJumpingFallingAndGliding_Agree()
        {
            var route = new DropRoute
            {
                StartX = -100f, StartZ = 10f, EndX = 100f, EndZ = 10f, Altitude = 90f, StartTick = StartTick + 1, DurationTicks = 300,
            };
            Start(new MoveState { Position = route.PositionAt(route.StartTick), Mode = MovementMode.Transport }, route);
            Frames(60, Vector2.zero, 90f);
            Assert.AreEqual(MovementMode.Transport, _predictor.Mode);
            Assert.AreEqual(route.PositionAt(_server.Tick).X, _predictor.PredictedPosition.x, 1e-4f);

            Frame(Vector2.zero, 90f, InputButtons.None, InputButtons.Jump);
            Assert.AreEqual(MovementMode.Freefall, _predictor.Mode);
            Frames(60, Vector2.up, 45f);                       // steering the fall
            for (int i = 0; i < 600 && _predictor.Mode != MovementMode.Ground; i++) Frame(Vector2.up, 45f);
            Assert.AreEqual(MovementMode.Ground, _predictor.Mode);
            AssertAgrees("deployment");
        }

        [Test]
        public void ShoulderingADoorOpen_Agrees()
        {
            // Door 0 (Rustvale, centre x -54, z 50.25), approached from the south at a sprint.
            Start(At(-54f, 45f));
            Frames(40, Vector2.up, 0f, InputButtons.Sprint);
            Assert.IsTrue(_doors.IsOpen(0));
            Assert.Greater(_predictor.PredictedPosition.z, 51f);
            AssertAgrees("bash");
        }

        [Test]
        public void E_OnADoor_IsPredicted_AndTheServersDoorStatesDecide()
        {
            Start(At(-54f, 48.5f));
            Frame(Vector2.zero, 0f, InputButtons.None, InputButtons.Interact);
            Assert.IsTrue(_doors.IsOpen(0));                     // predicted at once
            _doors.ApplyServer(0);                              // the server says closed (someone else closed it again)
            Assert.IsFalse(_doors.IsOpen(0));

            _doors.Predict(1, true, 10f);
            _doors.Expire(10.5f);
            Assert.IsTrue(_doors.IsOpen(1));
            _doors.Expire(10f + PredictedDoors.PredictionSeconds);   // never confirmed: it goes back
            Assert.IsFalse(_doors.IsOpen(1));
        }

        [Test]
        public void AResumeInTheAir_TakesTheServersMode_BeforeAnyInputIsAcked()
        {
            // The predictor starts from PlayerSpawned (no mode): Ground, mid-air.
            var predictor = new LocalPlayerPredictor(SimHz, new MoveState { Position = new Num.Vector3(0f, 60f, 0f) });
            InputButtons queued = InputButtons.None;
            predictor.Advance(3 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.None, ref queued);
            var entity = new SnapshotEntity
            {
                Position = new Num.Vector3(0f, 61f, 0f), Flags = SnapshotEntity.MakeFlags(true, MovementMode.Freefall, false, false),
            };
            predictor.Reconcile(entity, new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths }, 0, 900);
            Assert.AreEqual(MovementMode.Freefall, predictor.Mode);
            Assert.AreEqual(61f, predictor.PredictedPosition.y, 1e-4f);
        }

        [Test]
        public void NoActionIsPredicted_AboardOrInTheAir()
        {
            var route = new DropRoute { StartX = -100f, EndX = 100f, Altitude = 90f, StartTick = StartTick + 1, DurationTicks = 300 };
            Start(new MoveState { Position = route.PositionAt(route.StartTick), Mode = MovementMode.Transport }, route);
            Frame(Vector2.zero, 0f, InputButtons.Fire);
            Assert.IsFalse(_predictor.ActionsAllowedAt(_predictor.LastSeq));
            Assert.IsTrue(LocalPlayerPredictor.ActionsAllowed(MovementMode.Slide));
            Assert.IsFalse(LocalPlayerPredictor.ActionsAllowed(MovementMode.Glide));
            Assert.IsFalse(LocalPlayerPredictor.ActionsAllowed(MovementMode.Vault));
        }

        [Test]
        public void Crouch_IsHeld_InEveryStep_AndLowersTheAimEye()
        {
            var predictor = new LocalPlayerPredictor(SimHz, At(-40f, 0f));
            InputButtons queued = InputButtons.None;
            predictor.Advance(2 * Step + 0.0005f, Vector2.zero, 0f, InputButtons.Crouch, ref queued);
            Assert.AreEqual(InputButtons.Crouch, predictor.InputAt(1).Buttons);
            Assert.AreEqual(InputButtons.Crouch, predictor.InputAt(2).Buttons);
            Assert.AreEqual(MovementMode.Crouch, predictor.Mode);

            // Aiming level at a point 10 m ahead at 1 m height: from the crouched eye (1.0 m) the pitch is 0.
            Vector3 feet = predictor.PredictedPosition;
            predictor.SetAim(1, feet + new Vector3(0f, AimSolver.CrouchEyeHeight, 10f), 0f, 0f, 0f);
            Assert.AreEqual(0f, predictor.InputAt(2).AimPitch, 1e-3f);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental`
Expected: 컴파일 실패(`PredictedDoors`, `Reconcile`의 새 인자, `LocalPlayerPredictor.Mode` 등이 없다)

- [ ] **Step 3: 예측을 구현한다**

**수정** `Client/Assets/Scripts/Game/AimSolver.cs`:

변경 전:

```csharp
using UnityEngine;
```

변경 후:

```csharp
using ProjectH.Shared.Simulation;
using UnityEngine;
```

변경 전:

```csharp
        public const float EyeHeight = 1.6f;
        // The server clamps pitch to the same range.
        public const float MaxPitch = 89f;
        private const float MinDistance = 0.01f;
```

변경 후:

```csharp
        public const float EyeHeight = 1.6f;
        // Phase 12 D13: crouched or sliding. Must equal the server's CombatRules.CrouchEyeHeight.
        public const float CrouchEyeHeight = 1.0f;
        // The server clamps pitch to the same range.
        public const float MaxPitch = 89f;
        private const float MinDistance = 0.01f;

        // Phase 12 D13: the eye height of a mode, as the server's CombatRules.EyeHeightOf.
        public static float EyeHeightOf(MovementMode mode) =>
            mode == MovementMode.Crouch || mode == MovementMode.Slide ? CrouchEyeHeight : EyeHeight;
```

**수정** `Client/Assets/Scripts/Game/GameClient.cs`:

변경 전:

```csharp
        private readonly SpectatorCamera _spectator = new SpectatorCamera();
```

변경 후:

```csharp
        private readonly SpectatorCamera _spectator = new SpectatorCamera();
        // Phase 12 D9: the doors as predicted (server DoorStates plus our own predicted changes); the predictor moves
        // against them. Phase 12 D5: the match's transport route (TransportRoute), until the next one or a disconnect.
        private readonly PredictedDoors _doors = new PredictedDoors();
        private bool _hasRoute;
        private DropRoute _route;
```

변경 전:

```csharp
            _net.StatsReceived += OnStats;
```

변경 후:

```csharp
            _net.StatsReceived += OnStats;
            _net.TransportRouteReceived += OnTransportRoute;
            _net.DoorStatesReceived += OnDoorStates;
```

변경 전:

```csharp
            if (!blocked && _input.Sprint) held |= InputButtons.Sprint;
```

변경 후:

```csharp
            if (!blocked && _input.Sprint) held |= InputButtons.Sprint;
            if (!blocked && _input.CrouchHeld) held |= InputButtons.Crouch;   // Phase 12 D7
```

변경 전:

```csharp
            _net.StatsReceived -= OnStats;
```

변경 후:

```csharp
            _net.StatsReceived -= OnStats;
            _net.TransportRouteReceived -= OnTransportRoute;
            _net.DoorStatesReceived -= OnDoorStates;
```

변경 전:

```csharp
                uint seq = newest - (uint)i;
                if (_weapons.Step(seq, _predictor.InputAt(seq).Buttons)) shots++;
```

변경 후:

```csharp
                uint seq = newest - (uint)i;
                // Phase 12 D12: riding, falling, gliding or vaulting, the server takes no action from the input.
                InputButtons buttons = _predictor.ActionsAllowedAt(seq) ? _predictor.InputAt(seq).Buttons : InputButtons.None;
                if (_weapons.Step(seq, buttons)) shots++;
```

변경 전:

```csharp
                if (_predictor != null) return;
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw });
```

변경 후:

```csharp
                if (_predictor != null) return;
                // Phase 12: the spawn carries no mode; a resumed player in the air gets it from the next snapshot.
                _predictor = new LocalPlayerPredictor(_simHz, new MoveState { Position = spawned.Position, Yaw = spawned.Yaw }, _doors);
                if (_hasRoute) _predictor.SetRoute(_route);
```

변경 전:

```csharp
                {
                    if (_predictor != null) _predictor.Reconcile(entities[i], header.AckInputSeq);
```

변경 후:

```csharp
                {
                    if (_predictor != null) _predictor.Reconcile(entities[i], header.Self, header.AckInputSeq, header.ServerTick);
```

변경 전:

```csharp
            if (_predictor == null) return;
            // Also the match start and the round reset (Phase 5 D3, D13): the same teleport, Seq continues.
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw });
            _input.QueuedButtons = InputButtons.None;
```

변경 후:

```csharp
            if (_predictor == null) return;
            // Also the match start and the round reset (Phase 5 D3, D13): the same teleport, Seq continues. Phase 12: aboard
            // the drop transport at a match start.
            _predictor.Respawn(new MoveState { Position = respawned.Position, Yaw = respawned.Yaw, Mode = respawned.Mode });
            _input.QueuedButtons = InputButtons.None;
            _input.ResetCrouch();
```

변경 전:

```csharp
            ResultCount++;
```

변경 후:

```csharp
            ResultCount++;
        }

        // Phase 12 D5: sent before the match start's respawns (and at a join or resume during the match).
        private void OnTransportRoute(DropRoute route)
        {
            _route = route;
            _hasRoute = true;
            if (_predictor != null) _predictor.SetRoute(route);
        }

        private void OnDoorStates(byte openMask)
        {
            _doors.ApplyServer(openMask);
```

변경 전:

```csharp
            _statsAnsweredAt = -1f;
```

변경 후:

```csharp
            _statsAnsweredAt = -1f;
            _doors.Reset();
            _hasRoute = false;
```

**파일 전체를 바꾼다** `Client/Assets/Scripts/Game/LocalPlayerPredictor.cs`:

```csharp
using System;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Client-side prediction for the local player (Docs/Networking.md).
    // Moves against Shared GameMap.Boxes plus the predicted closed doors (PredictedDoors) and GameMap.Terrain, the same
    // world the server passes in Match.Tick.
    // Runs MovementSimulation at the server's tick rate, keeps a fixed 64-entry history of inputs and
    // results, and on each snapshot replays the inputs the server has not processed yet.
    // While dead (D9, D12) it predicts nothing: the server acks a dead player's inputs without moving it.
    // Phase 12: the whole MoveState (mode, horizontal velocity, energy, vault ticks) is predicted and reconciled. Aboard the
    // drop transport it rides the route at the server tick each input will be simulated at (snapshot tick - ack + seq),
    // and its own shoulder bashes and E on doors are predicted into PredictedDoors.
    public sealed class LocalPlayerPredictor
    {
        public const int HistorySize = 64;                 // ~2 s at 30 Hz; older unacked input -> snap
        private const float SnapDistance = 2f;             // corrections larger than this are not smoothed
        private const float ErrorDecayPerSecond = 10f;
        private const float MatchEpsilon = 0.01f;
        private const float MaxAccumulatedSeconds = 0.25f; // after a hitch, do not burst-simulate

        // Held buttons go into every step; queued presses only into a frame's last step (see Advance).
        private const InputButtons HeldButtons = InputButtons.Sprint | InputButtons.Fire | InputButtons.Crouch;
        private const InputButtons QueuedButtons = InputButtons.Jump | InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2 |
                                                   InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                   InputButtons.UseMedkit | InputButtons.UseShieldCell;

        private readonly InputCommand[] _inputs = new InputCommand[HistorySize];
        private readonly MoveState[] _results = new MoveState[HistorySize];
        private readonly float _stepSeconds;
        private readonly PredictedDoors _doors;
        private MoveState _state;
        private MoveState _previous;
        private float _accumulator;
        private Vector3 _renderError;
        // Seconds simulated so far: the clock of the door predictions.
        private float _time;
        // Phase 12 D5: the route, and server tick = _tickBase + seq for the input seq.
        private bool _hasRoute;
        private DropRoute _route;
        private long _tickBase;
        private bool _hasTickBase;

        public LocalPlayerPredictor(int simHz, MoveState spawnState, PredictedDoors doors = null)
        {
            _stepSeconds = 1f / simHz;
            _doors = doors ?? new PredictedDoors();
            _state = spawnState;
            _previous = spawnState;
            RenderPosition = spawnState.Position.ToUnity();
        }

        public uint LastSeq { get; private set; }
        public Vector3 RenderPosition { get; private set; }
        public float RenderYaw => _state.Yaw;
        // Newest simulated position: where the server will be after it runs our newest input (SetAim solves
        // each step's eye from that step's own result). RenderPosition trails it by up to one step and carries
        // the reconcile offset; it is for the camera and views only.
        public Vector3 PredictedPosition => _state.Position.ToUnity();
        public bool IsDead { get; private set; }

        // Phase 12: the newest predicted movement state, for the HUD, the camera and the F1 line.
        public MovementMode Mode => _state.Mode;
        public float Energy => _state.Energy;
        public bool Exhausted => _state.Exhausted;
        public bool Sprinting { get; private set; }
        public float HorizontalSpeed => _state.HorizontalVelocity.Length();
        public float VerticalSpeed => _state.VelocityY;
        // How far the newest correction moved the prediction (m), and how many corrections there were.
        public float LastCorrection { get; private set; }
        public int Corrections { get; private set; }
        // The server tick the newest input will be simulated at (valid once a snapshot arrived).
        public uint PredictedTick => (uint)(_tickBase + LastSeq);

        // D5: the match's transport route (TransportRoute). Kept until the next one; it acts only in Transport mode.
        public void SetRoute(in DropRoute route)
        {
            _route = route;
            _hasRoute = true;
        }

        // D12: whether the server lets the input seq act (the mode after its move).
        public bool ActionsAllowedAt(uint seq) => ActionsAllowed(_results[(int)(seq % HistorySize)].Mode);

        public static bool ActionsAllowed(MovementMode mode) =>
            mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;

        // Returns how many simulation steps ran (each generated one input).
        // held: Sprint, Fire and Crouch, applied to every step. queued: Jump, Reload, Slot1-3, Interact, Drop, UseMedkit and
        // UseShieldCell presses; they ride on
        // the last step, because GameClient sends one packet per frame holding only the newest
        // MaxInputsPerPacket inputs, so on a hitch frame an earlier step may never be sent. Consumed bits are cleared.
        public int Advance(float deltaTime, Vector2 move, float yaw, InputButtons held, ref InputButtons queued)
        {
            _accumulator = Mathf.Min(_accumulator + deltaTime, MaxAccumulatedSeconds);
            int steps = 0;
            while (_accumulator >= _stepSeconds)
            {
                _accumulator -= _stepSeconds;
                _time += _stepSeconds;
                _doors.Expire(_time);
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
                    Simulate(ref _state, command, out StepResult result);
                    Sprinting = result.Sprinting;
                    // D9: E on a door, as Match.ToggleDoor does after the move (the doorway check sees only ourselves).
                    if ((buttons & InputButtons.Interact) != 0 && ActionsAllowed(_state.Mode)) PredictDoorToggle();
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

        // One input, exactly as Match.Tick runs it: ride the route aboard, otherwise one Step against the predicted world;
        // a charging move blocked by a closed door opens it (D9).
        private void Simulate(ref MoveState state, in InputCommand command, out StepResult result)
        {
            if (_hasRoute && DropTransport.Ride(ref state, command, _route, (uint)(_tickBase + command.Seq)))
            {
                result = new StepResult { BlockedBy = -1 };
                return;
            }
            MovementSimulation.Step(ref state, command, _stepSeconds, _doors.World, GameMap.Terrain, out result);
            if (result.Charging && result.BlockedBy >= 0)
            {
                int door = _doors.DoorAt(result.BlockedBy);
                if (door >= 0) _doors.Predict(door, true, _time);
            }
        }

        private void PredictDoorToggle()
        {
            int door = DoorRule.FindTarget(_state.Position, _state.Yaw, GameMap.Doors);
            if (door < 0) return;
            if (!_doors.IsOpen(door)) _doors.Predict(door, true, _time);
            else if (!MovementSimulation.OverlapsAny(_state.Position, MovementSimulation.CollisionHeight(_state.Mode), GameMap.Doors.Slice(door, 1)))
                _doors.Predict(door, false, _time);
        }

        // D2/D6: GameClient calls this after the camera moved this frame, so the newest `steps` inputs carry
        // the aim the player saw. Aim does not affect MovementSimulation, so the predicted results stay valid.
        // The server fires each input from its position after that input's step, so each step aims at aimPoint
        // from its own predicted result, not from the newest one (older steps of a multi-step frame are behind).
        // When aimPoint is too close to that eye to give a direction, the fallback (camera) angles are sent.
        // Phase 12 D13: the eye height follows that step's mode (lower crouched or sliding), like the server's.
        public void SetAim(int steps, Vector3 aimPoint, float fallbackYaw, float fallbackPitch, float viewTick)
        {
            if (steps > HistorySize) steps = HistorySize;
            if (steps > LastSeq) steps = (int)LastSeq;
            for (int i = 0; i < steps; i++)
            {
                int slot = (int)((LastSeq - (uint)i) % HistorySize);
                Vector3 eye = _results[slot].Position.ToUnity() + new Vector3(0f, AimSolver.EyeHeightOf(_results[slot].Mode), 0f);
                if (!AimSolver.TrySolve(eye, aimPoint, out float aimYaw, out float aimPitch))
                {
                    aimYaw = fallbackYaw;
                    aimPitch = fallbackPitch;
                }
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

        // PlayerRespawned for us: a teleport. State restarts at the spawn point (Phase 12: in the mode the server says),
        // but Seq continues: the server drops any seq it has already taken, so restarting at 1 would make every later
        // input ignored.
        public void Respawn(MoveState spawn)
        {
            IsDead = false;
            _state = spawn;
            _previous = spawn;
            _renderError = Vector3.zero;
            RenderPosition = spawn.Position.ToUnity();
        }

        // Phase 12: the owner's movement state comes in two parts, the entity (position, VelocityY, mode and flags) and the
        // snapshot's self block (horizontal velocity, energy, tick counters). serverTick: the snapshot's tick; with ackSeq
        // it gives the tick every later input is simulated at (D5).
        public void Reconcile(in SnapshotEntity server, in SnapshotSelf self, uint ackSeq, uint serverTick)
        {
            // Snapshot data is untrusted and NetClient does not validate it. A non-finite value would
            // replace the predicted state and every later step would stay NaN, so the entity is ignored.
            if (!IsFinite(server.Position.X) || !IsFinite(server.Position.Y) || !IsFinite(server.Position.Z) ||
                !IsFinite(server.VelocityY) || !IsFinite(server.Yaw) ||
                !IsFinite(self.HorizontalVelocity.X) || !IsFinite(self.HorizontalVelocity.Y))
            {
                return;
            }

            // Death and respawn switch IsDead through Reliable events. A snapshot from the other side of that
            // switch (Sequenced, can arrive before or after the event) describes the other life: skip it.
            // Only a dead snapshot can trail a respawn: Sequenced drops older ticks, and ~3 s of dead snapshots
            // arrive between the death and the respawn, so no alive snapshot of the old life comes after them.
            if (server.IsAlive == IsDead) return;

            if (ackSeq > 0)
            {
                _tickBase = (long)serverTick - ackSeq;
                _hasTickBase = true;
            }
            else if (!_hasTickBase)
            {
                _tickBase = (long)serverTick - LastSeq;
                _hasTickBase = true;
            }

            var authoritative = new MoveState
            {
                Position = server.Position,
                VelocityY = server.VelocityY,
                Yaw = server.Yaw,
                Mode = server.Mode,
                HorizontalVelocity = self.HorizontalVelocity,
                EnergySpent = (ushort)(MoveState.MaxEnergyHundredths - Math.Min(self.Energy, (ushort)MoveState.MaxEnergyHundredths)),
                EnergyDelayTicks = self.EnergyDelayTicks,
                ModeTicks = self.ModeTicks,
                Exhausted = server.IsExhausted,
            };
            if (IsDead)
            {
                // The server does not move a dead player: its position is final, nothing to replay.
                Snap(authoritative);
                return;
            }

            // Ack 0 says nothing about our inputs: the server has not processed any yet. Once inputs are
            // predicted the local state is newer than this snapshot, so snapping would stutter on join.
            // Phase 12 D16: unless the server is in another mode (a resume mid-air): then its state is the better guess.
            if (ackSeq == 0 && LastSeq > 0)
            {
                if (_state.Mode != authoritative.Mode) Snap(authoritative);
                return;
            }

            if (ackSeq == 0 || ackSeq > LastSeq || LastSeq - ackSeq >= HistorySize)
            {
                // Nothing to replay from (no input sent yet, or history already overwritten).
                Snap(authoritative);
                return;
            }

            MoveState predicted = _results[(int)(ackSeq % HistorySize)];
            if (Matches(predicted, authoritative)) return;

            // Misprediction: restart from the server state and replay unacknowledged inputs.
            System.Numerics.Vector3 oldPosition = _state.Position;
            _state = authoritative;
            _previous = authoritative;
            _results[(int)(ackSeq % HistorySize)] = authoritative;
            for (uint seq = ackSeq + 1; seq <= LastSeq; seq++)
            {
                int slot = (int)(seq % HistorySize);
                _previous = _state;
                Simulate(ref _state, _inputs[slot], out _);
                _results[slot] = _state;
            }

            // Keep the rendered position continuous and let the difference decay, unless it is large.
            Vector3 correction = (oldPosition - _state.Position).ToUnity();
            _renderError = correction.sqrMagnitude > SnapDistance * SnapDistance ? Vector3.zero : _renderError + correction;
            LastCorrection = correction.magnitude;
            Corrections++;
        }

        private void Snap(in MoveState authoritative)
        {
            LastCorrection = (_state.Position - authoritative.Position).Length();
            if (!IsDead && !Matches(_state, authoritative)) Corrections++;
            _state = authoritative;
            _previous = authoritative;
            _renderError = Vector3.zero;
        }

        // Position, VelocityY and the horizontal velocity within the snapshot's quantization (1/256), and the exact mode,
        // energy, tick counters and exhaustion.
        private static bool Matches(in MoveState predicted, in MoveState server)
        {
            return System.Numerics.Vector3.DistanceSquared(predicted.Position, server.Position) < MatchEpsilon * MatchEpsilon &&
                   Mathf.Abs(predicted.VelocityY - server.VelocityY) < MatchEpsilon &&
                   System.Numerics.Vector2.DistanceSquared(predicted.HorizontalVelocity, server.HorizontalVelocity) < MatchEpsilon * MatchEpsilon &&
                   predicted.Mode == server.Mode && predicted.EnergySpent == server.EnergySpent &&
                   predicted.EnergyDelayTicks == server.EnergyDelayTicks && predicted.ModeTicks == server.ModeTicks &&
                   predicted.Exhausted == server.Exhausted;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
```

**새 파일** `Client/Assets/Scripts/Game/PredictedDoors.cs`:

```csharp
using System;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 12 D9: the doors as the local prediction sees them: the server's DoorStates with the local player's own
    // predicted changes (a shoulder bash, E) laid over it, each until the next DoorStates or PredictionSeconds, whichever
    // comes first (a change the server never made then stops mispredicting). The collision world is GameMap.Boxes followed
    // by the closed doors, in the same order as the server's DoorSet, in a fixed array rewritten only on a change.
    // Pure, no allocation after construction, no UnityEngine (EditMode tests run it outside Unity). Main thread only.
    public sealed class PredictedDoors
    {
        public const float PredictionSeconds = 1f;

        private readonly Box[] _world = new Box[GameMap.Boxes.Length + GameMap.DoorCount];
        private readonly int[] _doorOfSlot = new int[GameMap.DoorCount];
        // Per door: when its predicted state stops counting (0 = no prediction), and that state.
        private readonly float[] _predictedUntil = new float[GameMap.DoorCount];
        private byte _predictedOpen;
        private byte _server;
        private int _length;

        public PredictedDoors()
        {
            GameMap.Boxes.CopyTo(_world);
            Rebuild();
        }

        // Bit i = GameMap.Doors[i] is open, as predicted.
        public byte OpenMask { get; private set; }
        // Changes whenever OpenMask does (door views follow it).
        public int Version { get; private set; }
        // What the local prediction moves against.
        public ReadOnlySpan<Box> World => new ReadOnlySpan<Box>(_world, 0, _length);

        public bool IsOpen(int door) => (OpenMask & (1 << door)) != 0;

        // The door whose box is at this index of World, or -1 for a map box.
        public int DoorAt(int worldIndex)
        {
            int slot = worldIndex - GameMap.Boxes.Length;
            return slot >= 0 && worldIndex < _length ? _doorOfSlot[slot] : -1;
        }

        // DoorStates: the server's word replaces every prediction.
        public void ApplyServer(byte openMask)
        {
            _server = openMask;
            Array.Clear(_predictedUntil, 0, _predictedUntil.Length);
            _predictedOpen = 0;
            Update();
        }

        public void Predict(int door, bool open, float now)
        {
            if (door < 0 || door >= GameMap.DoorCount) return;
            _predictedUntil[door] = now + PredictionSeconds;
            int bit = 1 << door;
            _predictedOpen = (byte)(open ? _predictedOpen | bit : _predictedOpen & ~bit);
            Update();
        }

        // Predictions older than PredictionSeconds give way to the server's state.
        public void Expire(float now)
        {
            bool changed = false;
            for (int i = 0; i < _predictedUntil.Length; i++)
            {
                if (_predictedUntil[i] > 0f && now >= _predictedUntil[i])
                {
                    _predictedUntil[i] = 0f;
                    changed = true;
                }
            }
            if (changed) Update();
        }

        // Disconnect: no server state any more (every door closed, as at a round start).
        public void Reset() => ApplyServer(0);

        private void Update()
        {
            int mask = _server;
            for (int i = 0; i < _predictedUntil.Length; i++)
            {
                if (_predictedUntil[i] <= 0f) continue;
                int bit = 1 << i;
                mask = (_predictedOpen & bit) != 0 ? mask | bit : mask & ~bit;
            }
            if (mask == OpenMask) return;
            OpenMask = (byte)mask;
            Version++;
            Rebuild();
        }

        private void Rebuild()
        {
            int length = GameMap.Boxes.Length;
            ReadOnlySpan<Box> doors = GameMap.Doors;
            for (int i = 0; i < doors.Length; i++)
            {
                if (IsOpen(i)) continue;
                _doorOfSlot[length - GameMap.Boxes.Length] = i;
                _world[length++] = doors[i];
            }
            _length = length;
        }
    }
}
```

**수정** `Client/Assets/Scripts/Input/InputReader.cs`:

변경 전:

```csharp
        private readonly InputAction _debugToggle;
```

변경 후:

```csharp
        private readonly InputAction _debugToggle;
        private readonly InputAction _crouchToggle;
        private readonly InputAction _crouchHold;
        // Phase 12 D7: C turns crouch on and off; a jump or a sprint press turns it off again.
        private bool _crouchToggled;
```

변경 전:

```csharp
            _debugToggle = new InputAction("DebugToggle", InputActionType.Button, "<Keyboard>/f1");
```

변경 후:

```csharp
            _debugToggle = new InputAction("DebugToggle", InputActionType.Button, "<Keyboard>/f1");
            // Phase 12 D7: C toggles crouch, Ctrl holds it (the Crouch button is sent as held either way).
            _crouchToggle = new InputAction("CrouchToggle", InputActionType.Button, "<Keyboard>/c");
            _crouchHold = new InputAction("CrouchHold", InputActionType.Button, "<Keyboard>/leftCtrl");
```

변경 전:

```csharp
            _debugToggle.Enable();
```

변경 후:

```csharp
            _debugToggle.Enable();
            _crouchToggle.Enable();
            _crouchHold.Enable();
```

변경 전:

```csharp
        public bool DebugTogglePressed => _debugToggle.WasPressedThisFrame();
```

변경 후:

```csharp
        public bool DebugTogglePressed => _debugToggle.WasPressedThisFrame();
        // Phase 12 D7: the Crouch button: toggled with C or held with Ctrl.
        public bool CrouchHeld => _crouchToggled || _crouchHold.IsPressed();

        // A respawn or a death starts standing.
        public void ResetCrouch() => _crouchToggled = false;
```

변경 전:

```csharp
            if (_jump.WasPressedThisFrame()) QueuedButtons |= InputButtons.Jump;
```

변경 후:

```csharp
            if (_crouchToggle.WasPressedThisFrame()) _crouchToggled = !_crouchToggled;
            if (_jump.WasPressedThisFrame() || _sprint.WasPressedThisFrame()) _crouchToggled = false;
            if (_jump.WasPressedThisFrame()) QueuedButtons |= InputButtons.Jump;
```

변경 전:

```csharp
            _debugToggle.Dispose();
```

변경 후:

```csharp
            _debugToggle.Dispose();
            _crouchToggle.Dispose();
            _crouchHold.Dispose();
```

**수정** `Client/Assets/Scripts/Net/NetClient.cs`:

변경 전:

```csharp
using ProjectH.Shared.Protocol;
```

변경 후:

```csharp
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
        public event Action<StatsResponse> StatsReceived;
```

변경 후:

```csharp
        public event Action<StatsResponse> StatsReceived;
        // Phase 12 D5, D9: the drop transport route, and which doors are open.
        public event Action<DropRoute> TransportRouteReceived;
        public event Action<byte> DoorStatesReceived;
```

변경 전:

```csharp
                    if (StatsResponse.TryRead(ref packet, out var stats)) StatsReceived?.Invoke(stats);
                    break;
```

변경 후:

```csharp
                    if (StatsResponse.TryRead(ref packet, out var stats)) StatsReceived?.Invoke(stats);
                    break;

                case PacketId.TransportRoute:
                    if (TransportRoutePacket.TryRead(ref packet, out var route)) TransportRouteReceived?.Invoke(route);
                    break;

                case PacketId.DoorStates:
                    if (DoorStatesPacket.TryRead(ref packet, out byte doors)) DoorStatesReceived?.Invoke(doors);
                    break;
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 55개 통과(`MovementPredictionTests` 9개 추가)
Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 1043개(1034 통과, 9개 건너뜀)

- [ ] **Step 5: Commit** — `feat(client): predict every movement mode, transport riding and doors; C/Ctrl crouch (Phase 12)`

---

### Task 9: Client 표현 (자세, 수송기, 문, 카메라, 기력 막대, 안내, F1, 원인 "낙하")

**Files:**
- Create: `Client/Assets/Scripts/Game/PlayerPose.cs`, `Game/TransportView.cs`, `Game/DoorViews.cs`
- Modify:
  - `Client/Assets/Scripts/Game/`: `PlayerViewFactory.cs`(파일 전체), `RemotePlayers.cs`, `CombatHud.cs`, `GameClient.cs`
  - `Client/Assets/Scripts/Camera/`: `ShoulderCameraMath.cs`, `ShoulderCamera.cs`
  - `Client/Assets/Scripts/UI/`: `UiText.cs`, `DebugOverlay.cs`, `ResultScreen.cs`, `UiRoot.cs`
- Test:
  - Create: `Client/Assets/Tests/EditMode/PlayerPoseTests.cs`
  - Create: `Server/tests/ProjectH.Server.Tests/ClientUi/UiTextMovementTests.cs`(`UiText` 소스 링크)

**Interfaces:**
- Produces(Client):
  - `struct PlayerPose { BodyHeight, Lean, Prone, Wings, Hidden, HitHeight; static For(MovementMode, bool sprinting, bool alive) }`
  - `PlayerView { Root; SetAlive(bool); Place(Vector3 feet, float yaw, MovementMode, bool sprinting); Destroy() }`
    - 뿌리는 회전하지 않고 원격 맞는 상자를 갖는다. 자식은 캡슐과 날개다.
  - `PlayerViewFactory.Create(name, isLocal)` → `PlayerView`. 예전 `Pose`·`SetAlive`는 없앤다.
  - `TransportView { SetRoute, Clear, Tick(double renderTick) }`, `DoorViews { Tick(PredictedDoors) }`
  - 카메라
    - `struct CameraTargets { PivotHeight, Distance, FieldOfView }`
    - `ShoulderCameraMath`: `Hip`, `TargetsFor(MovementMode, bool sprinting)`, `Approach(CameraTargets, CameraTargets, float)`, `Solve(…, in CameraTargets hip)`
    - `ShoulderCamera.Follow(feet, aiming, dt, MovementMode = Ground, bool sprinting = false)`
  - HUD: `CombatHud.SetEnergy(float fraction, bool visible, bool exhausted)`, `CombatHud.SetHint(string)`
  - `UiText`
    - 상수: `FallName`, `HintJump`, `HintGlide`, `HintDoorOpen`, `HintDoorClose`
    - 함수: `CauseName`, `KillLine(killer, victim, DeathCause)`, `KilledBy(died, noKiller, DeathCause, killerName)`, `MovementLine`, `RouteLine`
    - 예전 오버로드는 남는다.
  - `DebugOverlay.TickMovement(…)`, `ResultScreen.Show(…, bool noKiller, DeathCause cause, string killerName)`
  - `GameClient.LastDeathCause`, `GameClient.TickMovementDebug(DebugOverlay, float now)`

`GameClient`가 매 프레임 하는 일은 다음과 같다.

- 문 View와 수송기 View를 갱신한다.
- 자기 모드로 카메라를 보간한다. 관전 대상은 기본 카메라다.
- 기력 막대는 가득 차 있고 달리지 않으면 숨긴다.
- 안내 문구: 탑승 중(뛰어내리기 구간)은 뛰어내리기, 자유 낙하는 글라이더, 문 앞은 열기·닫기다.
- 문 안내가 있으면 아이템 안내는 숨긴다.

Kill Feed와 결과 화면은 원인 "낙하"를 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Client/Assets/Tests/EditMode/PlayerPoseTests.cs`:

```csharp
using NUnit.Framework;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Game;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Tests
{
    // Phase 12 D13, D14: the placeholder pose of each mode, the remote hit box height, and the camera targets. Pure math:
    // these also run outside Unity (the EditTests tool).
    public class PlayerPoseTests
    {
        [Test]
        public void EachMode_HasItsPose_AndTheServersHitHeight()
        {
            PlayerPose ground = PlayerPose.For(MovementMode.Ground, false, true);
            Assert.AreEqual(PlayerPose.StandingBody, ground.BodyHeight);
            Assert.AreEqual(0f, ground.Lean);
            Assert.AreEqual(1.8f, ground.HitHeight);

            Assert.AreEqual(PlayerPose.SprintLean, PlayerPose.For(MovementMode.Ground, true, true).Lean);
            Assert.AreEqual(PlayerPose.CrouchedBody, PlayerPose.For(MovementMode.Crouch, false, true).BodyHeight);
            Assert.AreEqual(1.2f, PlayerPose.For(MovementMode.Crouch, false, true).HitHeight);
            PlayerPose slide = PlayerPose.For(MovementMode.Slide, false, true);
            Assert.AreEqual(PlayerPose.SlideLean, slide.Lean);
            Assert.AreEqual(1.2f, slide.HitHeight);
            Assert.AreEqual(PlayerPose.VaultLean, PlayerPose.For(MovementMode.Vault, false, true).Lean);
            Assert.IsTrue(PlayerPose.For(MovementMode.Freefall, false, true).Prone);
            Assert.IsTrue(PlayerPose.For(MovementMode.Glide, false, true).Wings);
            Assert.IsTrue(PlayerPose.For(MovementMode.Transport, false, true).Hidden);
        }

        [Test]
        public void TheDead_LieDown_WhateverTheMode()
        {
            PlayerPose dead = PlayerPose.For(MovementMode.Glide, true, false);
            Assert.IsTrue(dead.Prone);
            Assert.IsFalse(dead.Wings);
            Assert.IsFalse(dead.Hidden);
        }

        [Test]
        public void CameraTargets_FollowTheMode()
        {
            CameraTargets hip = ShoulderCameraMath.TargetsFor(MovementMode.Ground, false);
            Assert.AreEqual(ShoulderCameraMath.HipFov, hip.FieldOfView);
            Assert.AreEqual(ShoulderCameraMath.PivotHeight, hip.PivotHeight);
            Assert.AreEqual(ShoulderCameraMath.SprintFov, ShoulderCameraMath.TargetsFor(MovementMode.Ground, true).FieldOfView);
            Assert.AreEqual(ShoulderCameraMath.CrouchPivotHeight, ShoulderCameraMath.TargetsFor(MovementMode.Slide, false).PivotHeight);
            CameraTargets air = ShoulderCameraMath.TargetsFor(MovementMode.Glide, false);
            Assert.AreEqual(ShoulderCameraMath.AirDistance, air.Distance);
            Assert.AreEqual(ShoulderCameraMath.AirFov, air.FieldOfView);
            Assert.AreEqual(ShoulderCameraMath.TransportDistance, ShoulderCameraMath.TargetsFor(MovementMode.Transport, false).Distance);
        }

        [Test]
        public void CameraTargets_AreEased_NotJumpedTo()
        {
            CameraTargets current = ShoulderCameraMath.Hip;
            CameraTargets target = ShoulderCameraMath.TargetsFor(MovementMode.Freefall, false);
            CameraTargets next = ShoulderCameraMath.Approach(current, target, 1f / 60f);
            Assert.Greater(next.Distance, current.Distance);
            Assert.Less(next.Distance, target.Distance);
            for (int i = 0; i < 300; i++) next = ShoulderCameraMath.Approach(next, target, 1f / 60f);
            Assert.AreEqual(target.Distance, next.Distance, 1e-3f);
        }

        [Test]
        public void Solve_UsesTheModesPivotAndFieldOfView()
        {
            CameraTargets crouch = ShoulderCameraMath.TargetsFor(MovementMode.Crouch, false);
            ShoulderPose pose = ShoulderCameraMath.Solve(UnityEngine.Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, new NoHit(), crouch);
            Assert.AreEqual(ShoulderCameraMath.CrouchPivotHeight, pose.Shoulder.y, 1e-4f);
            CameraTargets sprint = ShoulderCameraMath.TargetsFor(MovementMode.Ground, true);
            pose = ShoulderCameraMath.Solve(UnityEngine.Vector3.zero, 0f, 0f, 0f, 3.5f, 1f / 60f, new NoHit(), sprint);
            Assert.AreEqual(ShoulderCameraMath.SprintFov, pose.FieldOfView, 1e-4f);
        }

        private sealed class NoHit : ISphereCaster
        {
            public bool Cast(UnityEngine.Vector3 origin, UnityEngine.Vector3 direction, float maxDistance, out float hitDistance)
            {
                hitDistance = 0f;
                return false;
            }
        }
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/ClientUi/UiTextMovementTests.cs`:

```csharp
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 12 D10, D14: the client's new UI strings (fall as a cause, the hints, the F1 movement and route lines), compiled
// here through the UiText source link.
public class UiTextMovementTests
{
    [Fact]
    public void AFall_IsNamedInTheKillFeed_AndTheResult()
    {
        Assert.Equal("낙하 ▸ bob", UiText.KillLine(null!, "bob", DeathCause.Fall));
        Assert.Equal("자기장 ▸ bob", UiText.KillLine(null!, "bob", DeathCause.Zone));
        Assert.Equal("alice ▸ bob", UiText.KillLine("alice", "bob", DeathCause.Zone));
        Assert.Equal("탈락 원인: 낙하", UiText.KilledBy(true, true, DeathCause.Fall, null!));
        Assert.Equal("탈락 원인: 자기장", UiText.KilledBy(true, true, DeathCause.Zone, null!));
        Assert.Equal("나를 처치한 플레이어: bob", UiText.KilledBy(true, false, DeathCause.Zone, "bob"));
        Assert.Equal(string.Empty, UiText.KilledBy(false, true, DeathCause.Fall, null!));
    }

    [Fact]
    public void TheHints_AreKorean()
    {
        Assert.Equal("[Space] 뛰어내리기", UiText.HintJump);
        Assert.Equal("[Space] 글라이더 펼치기", UiText.HintGlide);
        Assert.Equal("[E] 문 열기", UiText.HintDoorOpen);
        Assert.Equal("[E] 문 닫기", UiText.HintDoorClose);
    }

    [Fact]
    public void TheMovementLine_ShowsTenthsAndHundredths()
    {
        Assert.Equal("이동 Glide   수평 12.3 m/s   수직 -5.0 m/s   기력 87   보정 0.04 m", UiText.MovementLine("Glide", 123, -50, 87, 4));
        Assert.Equal("이동 Ground   수평 0.0 m/s   수직 0.0 m/s   기력 100   보정 1.25 m", UiText.MovementLine("Ground", 0, 0, 100, 125));
    }

    [Fact]
    public void TheRouteLine_ShowsWholeMetres()
    {
        Assert.Equal("수송기 (-100, 4) → (100, -4)", UiText.RouteLine(-100f, 3.6f, 99.8f, -4.4f));
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 컴파일 실패(`UiText.FallName`, `UiText.MovementLine` 등이 없다)

- [ ] **Step 3: 표현을 구현한다**

**수정** `Client/Assets/Scripts/Camera/ShoulderCamera.cs`:

변경 전:

```csharp
using UnityEngine;
```

변경 후:

```csharp
using ProjectH.Shared.Simulation;
using UnityEngine;
```

변경 전:

```csharp
        private float _distance = ShoulderCameraMath.HipDistance;
```

변경 후:

```csharp
        private float _distance = ShoulderCameraMath.HipDistance;
        // Phase 12 D14: the hip camera, eased towards the followed character's mode.
        private CameraTargets _hip = ShoulderCameraMath.Hip;
```

변경 전:

```csharp
        // Call from LateUpdate with the rendered feet position.
        public void Follow(Vector3 targetFeet, bool aiming, float deltaTime)
        {
            _aimBlend = ShoulderCameraMath.Approach(_aimBlend, aiming ? 1f : 0f, AimBlendSharpness, deltaTime);
            ShoulderPose pose = ShoulderCameraMath.Solve(targetFeet, Yaw, Pitch, _aimBlend, _distance, deltaTime, _caster);
            _distance = pose.Distance;
```

변경 후:

```csharp
        // Call from LateUpdate with the rendered feet position, and the mode of whoever is followed (D14).
        public void Follow(Vector3 targetFeet, bool aiming, float deltaTime, MovementMode mode = MovementMode.Ground, bool sprinting = false)
        {
            _aimBlend = ShoulderCameraMath.Approach(_aimBlend, aiming ? 1f : 0f, AimBlendSharpness, deltaTime);
            _hip = ShoulderCameraMath.Approach(_hip, ShoulderCameraMath.TargetsFor(mode, sprinting), deltaTime);
            ShoulderPose pose = ShoulderCameraMath.Solve(targetFeet, Yaw, Pitch, _aimBlend, _distance, deltaTime, _caster, _hip);
            _distance = pose.Distance;
```

**수정** `Client/Assets/Scripts/Camera/ShoulderCameraMath.cs`:

변경 전:

```csharp
using UnityEngine;
```

변경 후:

```csharp
using ProjectH.Shared.Simulation;
using UnityEngine;
```

변경 전:

```csharp
        bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance);
```

변경 후:

```csharp
        bool Cast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance);
    }

    // Phase 12 D14: the hip (not aiming) camera a movement mode asks for. ShoulderCamera eases its current values towards
    // these, so a mode change never jumps.
    public struct CameraTargets
    {
        public float PivotHeight;
        public float Distance;
        public float FieldOfView;
```

변경 전:

```csharp
        public const float ShoulderClearance = 0.02f;
```

변경 후:

```csharp
        public const float ShoulderClearance = 0.02f;
        // Phase 12 D14: the mode targets, and how fast the camera eases to them (no shake anywhere).
        public const float SprintFov = 66f;
        public const float CrouchPivotHeight = 1.1f;
        public const float AirDistance = 6f;
        public const float AirFov = 70f;
        public const float TransportDistance = 12f;
        public const float ModeSharpness = 5f;

        public static CameraTargets Hip => new CameraTargets { PivotHeight = PivotHeight, Distance = HipDistance, FieldOfView = HipFov };

        // D14: sprinting widens the view; crouched or sliding the pivot is lower; falling and gliding the camera backs off
        // with a wider view; aboard it follows the transport from further away.
        public static CameraTargets TargetsFor(MovementMode mode, bool sprinting)
        {
            CameraTargets targets = Hip;
            switch (mode)
            {
                case MovementMode.Crouch:
                case MovementMode.Slide:
                    targets.PivotHeight = CrouchPivotHeight;
                    break;
                case MovementMode.Freefall:
                case MovementMode.Glide:
                    targets.Distance = AirDistance;
                    targets.FieldOfView = AirFov;
                    break;
                case MovementMode.Transport:
                    targets.Distance = TransportDistance;
                    targets.FieldOfView = AirFov;
                    break;
                default:
                    if (sprinting) targets.FieldOfView = SprintFov;
                    break;
            }
            return targets;
        }

        // Frame-rate independent easing of every target value.
        public static CameraTargets Approach(CameraTargets current, CameraTargets target, float deltaTime)
        {
            return new CameraTargets
            {
                PivotHeight = Approach(current.PivotHeight, target.PivotHeight, ModeSharpness, deltaTime),
                Distance = Approach(current.Distance, target.Distance, ModeSharpness, deltaTime),
                FieldOfView = Approach(current.FieldOfView, target.FieldOfView, ModeSharpness, deltaTime),
            };
        }
```

변경 전:

```csharp
        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster)
        {
            float t = Mathf.Clamp01(aimBlend);
            float targetDistance = Mathf.Lerp(HipDistance, AimDistance, t);
            float shoulderOffset = Mathf.Lerp(HipShoulder, AimShoulder, t);
            Vector3 forward = Forward(yaw, pitch);
            Vector3 right = Right(yaw);
            Vector3 pivot = feet + new Vector3(0f, PivotHeight, 0f);
```

변경 후:

```csharp
        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster) => Solve(feet, yaw, pitch, aimBlend, currentDistance, deltaTime, caster, Hip);

        // Phase 12 D14: hip is the mode's (eased) camera; aiming blends from it to the aim camera as before.
        public static ShoulderPose Solve(Vector3 feet, float yaw, float pitch, float aimBlend, float currentDistance,
            float deltaTime, ISphereCaster caster, in CameraTargets hip)
        {
            float t = Mathf.Clamp01(aimBlend);
            float targetDistance = Mathf.Lerp(hip.Distance, AimDistance, t);
            float shoulderOffset = Mathf.Lerp(HipShoulder, AimShoulder, t);
            Vector3 forward = Forward(yaw, pitch);
            Vector3 right = Right(yaw);
            Vector3 pivot = feet + new Vector3(0f, hip.PivotHeight, 0f);
```

변경 전:

```csharp
                Distance = distance,
                FieldOfView = Mathf.Lerp(HipFov, AimFov, t),
```

변경 후:

```csharp
                Distance = distance,
                FieldOfView = Mathf.Lerp(hip.FieldOfView, AimFov, t),
```

**수정** `Client/Assets/Scripts/Game/CombatHud.cs`:

변경 전:

```csharp
        private const int FontSize = 22;
```

변경 후:

```csharp
        private const int FontSize = 22;
        // Phase 12 D14: the energy bar above the vitals line (full width = full energy).
        private const float EnergyBarWidth = 220f;
        private const float EnergyBarHeight = 8f;
```

변경 전:

```csharp
        private readonly Text _center;
```

변경 후:

```csharp
        private readonly Text _center;
        private readonly Text _hint;
        private readonly GameObject _energyBar;
        private readonly RectTransform _energyFill;
        private readonly Image _energyFillImage;
```

변경 전:

```csharp
        private int _countdown = -1;
```

변경 후:

```csharp
        private int _countdown = -1;
        private string _hintText;
        private int _energyPixels = -1;
        private bool _energyExhausted;
```

변경 전:

```csharp
            _center = CreateText("Center", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -80f), TextAnchor.MiddleCenter);
```

변경 후:

```csharp
            _center = CreateText("Center", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -80f), TextAnchor.MiddleCenter);
            // Phase 12 D14: the hint line ("[Space] 뛰어내리기", "[E] 문 열기") under the centre, and the energy bar.
            _hint = CreateText("Hint", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -130f), TextAnchor.MiddleCenter);
            _hint.color = new Color(1f, 0.95f, 0.6f);
            Image back = CreateBar((RectTransform)_root.transform, Vector2.zero, new Vector2(EnergyBarWidth, EnergyBarHeight), 0f);
            back.color = new Color(0f, 0f, 0f, 0.5f);
            var backRect = back.rectTransform;
            backRect.anchorMin = Vector2.zero;
            backRect.anchorMax = Vector2.zero;
            backRect.pivot = Vector2.zero;
            backRect.anchoredPosition = new Vector2(20f, 66f);
            _energyBar = back.gameObject;
            _energyFillImage = CreateBar(backRect, Vector2.zero, new Vector2(EnergyBarWidth, EnergyBarHeight), 0f);
            _energyFill = _energyFillImage.rectTransform;
            _energyFill.anchorMin = Vector2.zero;
            _energyFill.anchorMax = Vector2.zero;
            _energyFill.pivot = Vector2.zero;
            _energyFill.anchoredPosition = Vector2.zero;
            _energyFillImage.color = new Color(0.4f, 0.85f, 1f);
            _energyBar.SetActive(false);
```

변경 전:

```csharp
            _weapon.text = string.Empty;
```

변경 후:

```csharp
            _weapon.text = string.Empty;
        }

        // Phase 12 D14: energy 0..1; hidden when full and not sprinting. Exhausted (no sprint until 20) shows orange. The
        // bar only changes size when a whole pixel changes.
        public void SetEnergy(float fraction, bool visible, bool exhausted)
        {
            if (_root == null) return;
            if (_energyBar.activeSelf != visible) _energyBar.SetActive(visible);
            if (!visible) return;
            int pixels = Mathf.RoundToInt(Mathf.Clamp01(fraction) * EnergyBarWidth);
            if (pixels != _energyPixels)
            {
                _energyPixels = pixels;
                _energyFill.sizeDelta = new Vector2(pixels, EnergyBarHeight);
            }
            if (exhausted != _energyExhausted)
            {
                _energyExhausted = exhausted;
                _energyFillImage.color = exhausted ? new Color(1f, 0.55f, 0.2f) : new Color(0.4f, 0.85f, 1f);
            }
        }

        // Phase 12 D14: one of UiText's constant hints, or null. Set only when the reference changes.
        public void SetHint(string hint)
        {
            if (_root == null || ReferenceEquals(hint, _hintText)) return;
            _hintText = hint;
            _hint.text = hint ?? string.Empty;
```

**새 파일** `Client/Assets/Scripts/Game/DoorViews.cs`:

```csharp
using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 12 D9, D14: one box per Shared GameMap door, shown while the door is closed as predicted (PredictedDoors).
    // Its collider (default layer) stops the camera and the aim ray like the server's shots stop at a closed door; an
    // open door is simply not there. Built once; Tick changes them only when PredictedDoors.Version changes. Dispose
    // destroys the objects and the material.
    public sealed class DoorViews : System.IDisposable
    {
        private readonly GameObject[] _doors = new GameObject[GameMap.DoorCount];
        private readonly Material _material;
        private int _shownVersion = -1;

        public DoorViews()
        {
            Material source = null;
            for (int i = 0; i < _doors.Length; i++)
            {
                Box box = GameMap.Doors[i];
                var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
                door.name = "Door " + i;
                door.transform.position = box.Center.ToUnity();
                door.transform.localScale = box.Size.ToUnity();
                var renderer = door.GetComponent<Renderer>();
                if (source == null) source = LitMaterial.Source(renderer.sharedMaterial);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                _doors[i] = door;
            }
            _material = new Material(source) { color = new Color(0.45f, 0.3f, 0.18f) };
            for (int i = 0; i < _doors.Length; i++) _doors[i].GetComponent<Renderer>().sharedMaterial = _material;
        }

        public void Tick(PredictedDoors doors)
        {
            if (doors.Version == _shownVersion) return;
            _shownVersion = doors.Version;
            for (int i = 0; i < _doors.Length; i++)
            {
                bool closed = !doors.IsOpen(i);
                if (_doors[i] != null && _doors[i].activeSelf != closed) _doors[i].SetActive(closed);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _doors.Length; i++)
            {
                if (_doors[i] != null) Object.Destroy(_doors[i]);
            }
            if (_material != null) Object.Destroy(_material);
        }
    }
}
```

**수정** `Client/Assets/Scripts/Game/GameClient.cs`:

변경 전:

```csharp
        private KillFeed _killFeed;
```

변경 후:

```csharp
        private KillFeed _killFeed;
        // Phase 12 D14: the drop transport and the doors on screen.
        private TransportView _transportView;
        private DoorViews _doorViews;
```

변경 전:

```csharp
        private bool _killedByZone;
```

변경 후:

```csharp
        private bool _killedByZone;
        private DeathCause _deathCause;   // Phase 12 D10: with no killer, the zone or a fall
```

변경 전:

```csharp
        private LocalPlayerPredictor _predictor;
        private Transform _localView;
        private Renderer _localRenderer;
```

변경 후:

```csharp
        private LocalPlayerPredictor _predictor;
        private PlayerView _localView;
```

변경 전:

```csharp
        public bool DiedThisRound => _died;
        public bool KilledByZone => _killedByZone;
```

변경 후:

```csharp
        public bool DiedThisRound => _died;
        // True when nobody killed us (KillerId 0): the zone or, since Phase 12, a fall (LastDeathCause says which).
        public bool KilledByZone => _killedByZone;
        public DeathCause LastDeathCause => _deathCause;

        // Phase 12 D14: the F1 movement line (UiRoot owns the overlay; the values are the local prediction's).
        public void TickMovementDebug(DebugOverlay overlay, float now)
        {
            bool alive = _predictor != null && !_predictor.IsDead;
            if (!alive)
            {
                overlay.TickMovement(now, false, MovementMode.Ground, 0f, 0f, 0f, 0f, _hasRoute, _route);
                return;
            }
            overlay.TickMovement(now, true, _predictor.Mode, _predictor.HorizontalSpeed, _predictor.VerticalSpeed, _predictor.Energy,
                _predictor.LastCorrection, _hasRoute, _route);
        }
```

변경 전:

```csharp
            _killFeed = new KillFeed();
```

변경 후:

```csharp
            _killFeed = new KillFeed();
            _transportView = new TransportView();
            _doorViews = new DoorViews();
```

변경 전:

```csharp
            PlayerViewFactory.Pose(_predictor.RenderPosition, _predictor.RenderYaw, !_predictor.IsDead, out Vector3 position, out Quaternion rotation);
            _localView.SetPositionAndRotation(position, rotation);
        }
```

변경 후:

```csharp
            _localView.Place(_predictor.RenderPosition, _predictor.RenderYaw, _predictor.Mode, _predictor.Sprinting);
        }
```

변경 전:

```csharp
            _killFeed.Tick(Time.unscaledTime);
```

변경 후:

```csharp
            _killFeed.Tick(Time.unscaledTime);
            // Phase 12 D14: the doors as predicted, and the transport at the render tick (also while spectating).
            _doorViews.Tick(_doors);
            _transportView.Tick(_renderTick);
```

변경 전:

```csharp
            _spectator.Update(_remotePlayers);
            Vector3 followFeet = _spectator.TryGetFeet(_remotePlayers, _renderTick, out Vector3 watched) ? watched : _predictor.RenderPosition;
            _camera.Follow(followFeet, _aiming, Time.deltaTime);
```

변경 후:

```csharp
            _spectator.Update(_remotePlayers);
            bool watching = _spectator.TryGetFeet(_remotePlayers, _renderTick, out Vector3 watched);
            Vector3 followFeet = watching ? watched : _predictor.RenderPosition;
            // Phase 12 D14: the camera eases to our own mode (a watched player gets the standard camera).
            MovementMode mode = watching || !alive ? MovementMode.Ground : _predictor.Mode;
            _camera.Follow(followFeet, _aiming, Time.deltaTime, mode, !watching && alive && _predictor.Sprinting);
```

변경 전:

```csharp
            _hud.SetVitals(_health, _shield);
```

변경 후:

```csharp
            _hud.SetVitals(_health, _shield);
            // Phase 12 D14: the energy bar (hidden when full and not sprinting) and the hint line.
            float energy = _predictor.Energy / MovementTuning.MaxEnergy;
            bool onFoot = LocalPlayerPredictor.ActionsAllowed(_predictor.Mode);
            _hud.SetEnergy(energy, alive && onFoot && (energy < 1f || _predictor.Sprinting), _predictor.Exhausted);
            int door = alive && onFoot ? DoorRule.FindTarget(_predictor.PredictedPosition.ToNumerics(), _predictor.RenderYaw, GameMap.Doors) : -1;
            _hud.SetHint(Hint(alive, door));
```

변경 전:

```csharp
            UpdateInventoryHud(alive, now);
            UpdateMatchHud(alive);
        }
```

변경 후:

```csharp
            // D9, D12: E means the door first, and aboard, falling or vaulting it does nothing: no item prompt then.
            UpdateInventoryHud(alive, onFoot && door < 0, now);
            UpdateMatchHud(alive);
        }

        // Phase 12 D14: aboard (inside the jump window) "jump", in freefall "glider", next to a door "open"/"close".
        private string Hint(bool alive, int door)
        {
            if (!alive) return null;
            switch (_predictor.Mode)
            {
                case MovementMode.Transport:
                    if (!_hasRoute) return null;
                    _route.JumpWindow(out uint first, out uint last);
                    uint tick = _predictor.PredictedTick;
                    return tick >= first && tick < last ? UiText.HintJump : null;
                case MovementMode.Freefall:
                    return UiText.HintGlide;
            }
            if (door < 0) return null;
            return _doors.IsOpen(door) ? UiText.HintDoorClose : UiText.HintDoorOpen;
        }
```

변경 전:

```csharp
        // D15: slots, heals, the heal bar and the "[E]" prompt. Strings are rebuilt only on change (InventoryHudText).
        private void UpdateInventoryHud(bool alive, float now)
```

변경 후:

```csharp
        // D15: slots, heals, the heal bar and the "[E]" prompt. Strings are rebuilt only on change (InventoryHudText).
        // Phase 12: prompt false hides the item prompt only (the heal bar stays).
        private void UpdateInventoryHud(bool alive, bool prompt, float now)
```

변경 전:

```csharp
            // Same rule as the server (PickupRule): the prompt names the item E will take.
            int target = alive ? PickupRule.FindNearest(_worldItems.Items, _predictor.PredictedPosition.ToNumerics()) : -1;
```

변경 후:

```csharp
            // Same rule as the server (PickupRule): the prompt names the item E will take.
            int target = alive && prompt ? PickupRule.FindNearest(_worldItems.Items, _predictor.PredictedPosition.ToNumerics()) : -1;
```

변경 전:

```csharp
            _killFeed.Dispose();
```

변경 후:

```csharp
            _killFeed.Dispose();
            _doorViews.Dispose();
            _transportView.Dispose();
```

변경 전:

```csharp
                _localView = PlayerViewFactory.Create($"Player {spawned.EntityId} (you)", true);
                _localRenderer = _localView.GetComponent<Renderer>();
```

변경 후:

```csharp
                _localView = PlayerViewFactory.Create($"Player {spawned.EntityId} (you)", true);
```

변경 전:

```csharp
            // line is built once here, so the feed allocates per death, never per frame.
            bool notice = died.KillerId == 0 && died.Placement == 0;
            string killer = died.KillerId == 0 ? null : UiText.NameOr(NameOf(died.KillerId), died.KillerId);
            if (!notice) _killFeed.Add(UiText.KillLine(killer, UiText.NameOr(NameOf(died.VictimId), died.VictimId)), Time.unscaledTime);
```

변경 후:

```csharp
            // line is built once here, so the feed allocates per death, never per frame.
            // Phase 12 D10: a fall also has no killer and, in the dev sandbox, no placement: its cause tells it apart.
            bool notice = died.KillerId == 0 && died.Placement == 0 && died.Cause == DeathCause.Zone;
            string killer = died.KillerId == 0 ? null : UiText.NameOr(NameOf(died.KillerId), died.KillerId);
            if (!notice) _killFeed.Add(UiText.KillLine(killer, UiText.NameOr(NameOf(died.VictimId), died.VictimId), died.Cause), Time.unscaledTime);
```

변경 전:

```csharp
                _killedByZone = died.KillerId == 0;
                _killerName = killer;
            }
            _predictor.SetDead();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, false);
```

변경 후:

```csharp
                _killedByZone = died.KillerId == 0;
                _deathCause = died.Cause;
                _killerName = killer;
            }
            _predictor.SetDead();
            _input.ResetCrouch();
            _localView.SetAlive(false);
```

변경 전:

```csharp
            _killedByZone = false;
            _killerName = null;
            // Empty until the server's InventoryState for the new life arrives (sent right after this event).
            if (_weapons != null) _weapons.Clear();
            PlayerViewFactory.SetAlive(_localRenderer, null, true, true);
```

변경 후:

```csharp
            _killedByZone = false;
            _deathCause = DeathCause.Zone;
            _killerName = null;
            // Empty until the server's InventoryState for the new life arrives (sent right after this event).
            if (_weapons != null) _weapons.Clear();
            _localView.SetAlive(true);
```

변경 전:

```csharp
            if (_predictor != null) _predictor.SetRoute(route);
```

변경 후:

```csharp
            if (_predictor != null) _predictor.SetRoute(route);
            _transportView.SetRoute(route);
```

변경 전:

```csharp
            _predictor = null;
            if (_localView != null) Destroy(_localView.gameObject);
            _localView = null;
            _localRenderer = null;
```

변경 후:

```csharp
            _predictor = null;
            _localView?.Destroy();
            _localView = null;
```

변경 전:

```csharp
            _killedByZone = false;
            _killerName = null;
```

변경 후:

```csharp
            _killedByZone = false;
            _deathCause = DeathCause.Zone;
            _killerName = null;
```

변경 전:

```csharp
            _hasRoute = false;
```

변경 후:

```csharp
            _hasRoute = false;
            _transportView.Clear();
```

**새 파일** `Client/Assets/Scripts/Game/PlayerPose.cs`:

```csharp
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 12 D14: how a character is drawn in each movement mode, as plain numbers; PlayerView applies them to the
    // placeholder capsule (no new assets). The hit box (the collider the aim ray uses) has the server's height for the
    // mode (D13), whatever the capsule shows. Pure (no UnityEngine), so EditMode tests also run outside Unity.
    public struct PlayerPose
    {
        public const float StandingBody = 2f;     // the capsule mesh's own height
        public const float CrouchedBody = 1.3f;
        public const float SprintLean = 10f;
        public const float SlideLean = -30f;      // leaning back
        public const float VaultLean = 20f;

        public float BodyHeight;   // metres
        public float Lean;         // degrees about the character's right axis; positive leans forward
        public bool Prone;         // lying along the facing direction (freefall, dead)
        public bool Wings;         // the glider over the head
        public bool Hidden;        // aboard the transport: neither drawn nor hit
        public float HitHeight;    // MovementSimulation.CollisionHeight of the mode

        public static PlayerPose For(MovementMode mode, bool sprinting, bool alive)
        {
            var pose = new PlayerPose { BodyHeight = StandingBody, HitHeight = MovementSimulation.CollisionHeight(mode) };
            if (!alive)
            {
                pose.Prone = true;
                return pose;
            }
            switch (mode)
            {
                case MovementMode.Crouch:
                    pose.BodyHeight = CrouchedBody;
                    break;
                case MovementMode.Slide:
                    pose.BodyHeight = CrouchedBody;
                    pose.Lean = SlideLean;
                    break;
                case MovementMode.Vault:
                    pose.Lean = VaultLean;
                    break;
                case MovementMode.Freefall:
                    pose.Prone = true;
                    break;
                case MovementMode.Glide:
                    pose.Wings = true;
                    break;
                case MovementMode.Transport:
                    pose.Hidden = true;
                    break;
                default:
                    if (sprinting) pose.Lean = SprintLean;
                    break;
            }
            return pose;
        }
    }
}
```

**파일 전체를 바꾼다** `Client/Assets/Scripts/Game/PlayerViewFactory.cs`:

```csharp
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // One character on screen (Phase 12 D14). The root sits at the feet, never rotates and carries the remote hit box,
    // so the box stays axis-aligned like the server's AABB (D7) at the mode's height (D13). The capsule child shows the
    // pose (PlayerPose: height, lean, lying down), a flat wing child the glider. Transforms change only when the pose
    // does, besides the per-frame position and facing. Created on spawn and destroyed on despawn.
    public sealed class PlayerView
    {
        private const float WingLift = 0.3f;

        private readonly bool _isLocal;
        private readonly Transform _body;
        private readonly Renderer _bodyRenderer;
        private readonly GameObject _wings;
        private readonly Transform _wingsTransform;
        private readonly BoxCollider _collider;   // null for the local player
        private bool _alive = true;
        private bool _hidden;
        private PlayerPose _pose;
        private bool _hasPose;

        internal PlayerView(Transform root, Transform body, GameObject wings, BoxCollider collider, bool isLocal)
        {
            Root = root;
            _body = body;
            _bodyRenderer = body.GetComponent<Renderer>();
            _wings = wings;
            _wingsTransform = wings.transform;
            _collider = collider;
            _isLocal = isLocal;
        }

        public Transform Root { get; }

        // D13 (Phase 3): dead players are grey and lying down, and a dead remote player's hit box is off.
        public void SetAlive(bool alive)
        {
            if (alive == _alive) return;
            _alive = alive;
            _bodyRenderer.sharedMaterial = PlayerViewFactory.BodyMaterial(_isLocal, alive);
            _hasPose = false;   // the pose depends on it
        }

        // Every frame: where the feet are drawn, the facing, and the mode's pose.
        public void Place(Vector3 feet, float yaw, MovementMode mode, bool sprinting)
        {
            Root.position = feet;
            PlayerPose pose = PlayerPose.For(mode, sprinting, _alive);
            if (!_hasPose || pose.BodyHeight != _pose.BodyHeight || pose.Prone != _pose.Prone || pose.Wings != _pose.Wings ||
                pose.Hidden != _pose.Hidden || pose.HitHeight != _pose.HitHeight)
            {
                ApplyShape(pose);
            }
            _pose = pose;
            _hasPose = true;
            float pitch = pose.Prone ? 90f : pose.Lean;
            _body.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            if (pose.Wings) _wingsTransform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private void ApplyShape(in PlayerPose pose)
        {
            float half = pose.BodyHeight * 0.5f;
            // Lying down, the capsule's middle is half its radius (0.5 m) above the feet.
            _body.localPosition = new Vector3(0f, pose.Prone ? 0.5f : half, 0f);
            _body.localScale = new Vector3(1f, pose.Prone ? 1f : half, 1f);
            if (_hidden != pose.Hidden)
            {
                _hidden = pose.Hidden;
                _bodyRenderer.enabled = !pose.Hidden;
            }
            if (_wings.activeSelf != pose.Wings) _wings.SetActive(pose.Wings);
            _wingsTransform.localPosition = new Vector3(0f, pose.BodyHeight + WingLift, 0f);
            if (_collider != null)
            {
                _collider.size = new Vector3(2f * MoveSettings.HalfWidth, pose.HitHeight, 2f * MoveSettings.HalfWidth);
                _collider.center = new Vector3(0f, pose.HitHeight * 0.5f, 0f);
                _collider.enabled = _alive && !pose.Hidden;
            }
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root.gameObject);
        }
    }

    // Views are created on spawn and destroyed on despawn (rare events), so no pooling.
    // Materials are created once per color and shared through sharedMaterial: never use
    // renderer.material, which silently clones a material per object.
    public static class PlayerViewFactory
    {
        // Built-in "Ignore Raycast" layer. Remote views keep a collider there so the crosshair ray can land on a
        // player (the aim point must be on the target, D2). The camera SphereCast uses DefaultRaycastLayers, which
        // excludes this layer, so other players never push the camera; aim and fire rays add it via AimRaycastMask.
        public const int RemoteHitLayer = 2;
        public const int AimRaycastMask = Physics.DefaultRaycastLayers | (1 << RemoteHitLayer);

        private static readonly Vector3 WingSize = new Vector3(3f, 0.08f, 1f);

        private static Material _localMaterial;
        private static Material _remoteMaterial;
        private static Material _deadMaterial;
        private static Material _wingMaterial;

        public static PlayerView Create(string name, bool isLocal)
        {
            var root = new GameObject(name);
            BoxCollider collider = null;
            if (!isLocal)
            {
                // D7: the server's hit box is the movement AABB, so the aim ray uses the same box (on the root, which never
                // rotates) instead of the primitive's capsule, which would miss the corners. The local view has none: the
                // aim ray starts next to it and must never hit it.
                collider = root.AddComponent<BoxCollider>();
                root.layer = RemoteHitLayer;
            }

            // DestroyImmediate, not Destroy: a deferred destroy would leave the primitives' colliders for this frame's
            // camera and aim rays.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            var bodyRenderer = body.GetComponent<Renderer>();
            EnsureMaterials(LitMaterial.Source(bodyRenderer.sharedMaterial));
            bodyRenderer.sharedMaterial = isLocal ? _localMaterial : _remoteMaterial;

            var wings = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wings.name = "Glider";
            Object.DestroyImmediate(wings.GetComponent<Collider>());
            wings.transform.SetParent(root.transform, false);
            wings.transform.localScale = WingSize;
            wings.GetComponent<Renderer>().sharedMaterial = _wingMaterial;
            wings.SetActive(false);

            var view = new PlayerView(root.transform, body.transform, wings, collider, isLocal);
            view.Place(root.transform.position, 0f, MovementMode.Ground, false);
            return view;
        }

        internal static Material BodyMaterial(bool isLocal, bool alive) =>
            alive ? (isLocal ? _localMaterial : _remoteMaterial) : _deadMaterial;

        // Called by GameClient.OnDestroy: the cached materials live exactly as long as the client.
        public static void ReleaseMaterials()
        {
            if (_localMaterial != null) Object.Destroy(_localMaterial);
            if (_remoteMaterial != null) Object.Destroy(_remoteMaterial);
            if (_deadMaterial != null) Object.Destroy(_deadMaterial);
            if (_wingMaterial != null) Object.Destroy(_wingMaterial);
            _localMaterial = null;
            _remoteMaterial = null;
            _deadMaterial = null;
            _wingMaterial = null;
        }

        private static void EnsureMaterials(Material template)
        {
            // Explicit == null (not ??=): Unity's null check also catches destroyed materials.
            if (_localMaterial == null) _localMaterial = Tinted(template, new Color(0.2f, 0.6f, 1f));
            if (_remoteMaterial == null) _remoteMaterial = Tinted(template, new Color(1f, 0.45f, 0.2f));
            if (_deadMaterial == null) _deadMaterial = Tinted(template, new Color(0.45f, 0.45f, 0.45f));
            if (_wingMaterial == null) _wingMaterial = Tinted(template, new Color(0.95f, 0.85f, 0.25f));
        }

        // Copies the Lit material (LitMaterial), so the shader is guaranteed to be in the build.
        private static Material Tinted(Material template, Color color)
        {
            return new Material(template) { color = color };
        }
    }
}
```

**수정** `Client/Assets/Scripts/Game/RemotePlayers.cs`:

변경 전:

```csharp
using ProjectH.Shared.Protocol;
```

변경 후:

```csharp
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
    // client that joins while someone is dead gets no event for it, but every snapshot carries the flag.
```

변경 후:

```csharp
    // client that joins while someone is dead gets no event for it, but every snapshot carries the flag.
    // Phase 12 D14: so do the movement mode and sprinting (the newest snapshot's), which pick the pose.
```

변경 전:

```csharp
        {
            public Transform View;
            public Renderer Renderer;
            public Collider Collider;
            public RemotePlayerInterpolator Interpolator;
            public bool Alive = true;
```

변경 후:

```csharp
        {
            public PlayerView View;
            public RemotePlayerInterpolator Interpolator;
            public bool Alive = true;
            public MovementMode Mode;
            public bool Sprinting;
```

변경 전:

```csharp
            if (_entries.ContainsKey(spawned.EntityId)) return;
            Transform view = PlayerViewFactory.Create($"Player {spawned.EntityId}", false);
            var entry = new Entry
            {
                View = view,
                Renderer = view.GetComponent<Renderer>(),
                Collider = view.GetComponent<Collider>(),
```

변경 후:

```csharp
            if (_entries.ContainsKey(spawned.EntityId)) return;
            PlayerView view = PlayerViewFactory.Create($"Player {spawned.EntityId}", false);
            var entry = new Entry
            {
                View = view,
```

변경 전:

```csharp
            entry.Interpolator.Push(tick, spawned.Position.ToUnity(), spawned.Yaw);
            if (entry.Interpolator.TrySample(tick, out Vector3 start, out _))
                entry.View.SetPositionAndRotation(start + Vector3.up, Quaternion.identity);   // alive: axis-aligned, see Render
```

변경 후:

```csharp
            entry.Interpolator.Push(tick, spawned.Position.ToUnity(), spawned.Yaw);
            if (entry.Interpolator.TrySample(tick, out Vector3 start, out float yaw)) entry.View.Place(start, yaw, MovementMode.Ground, false);
```

변경 전:

```csharp
        {
            if (_entries.Remove(entityId, out Entry entry)) Object.Destroy(entry.View.gameObject);
```

변경 후:

```csharp
        {
            if (_entries.Remove(entityId, out Entry entry)) entry.View.Destroy();
```

변경 전:

```csharp
                entry.Alive = alive;
                PlayerViewFactory.SetAlive(entry.Renderer, entry.Collider, false, alive);
            }
```

변경 후:

```csharp
                entry.Alive = alive;
                entry.View.SetAlive(alive);
            }
            entry.Mode = entity.Mode;
            entry.Sprinting = entity.IsSprinting;
```

변경 전:

```csharp
                if (!entry.Interpolator.TrySample(renderTick, out Vector3 feet, out float yaw)) continue;
                // Alive: no yaw, so the box collider stays axis-aligned like the server's AABB (D7). The capsule
                // mesh is round about Y, so this looks the same. Dead: lies along the facing direction.
                PlayerViewFactory.Pose(feet, entry.Alive ? 0f : yaw, entry.Alive, out Vector3 position, out Quaternion rotation);
                entry.View.SetPositionAndRotation(position, rotation);
```

변경 후:

```csharp
                if (!entry.Interpolator.TrySample(renderTick, out Vector3 feet, out float yaw)) continue;
                // The hit box stays axis-aligned like the server's AABB (D7); only the capsule turns and leans.
                entry.View.Place(feet, yaw, entry.Mode, entry.Sprinting);
```

변경 전:

```csharp
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.View != null) Object.Destroy(pair.Value.View.gameObject);
            }
```

변경 후:

```csharp
        {
            foreach (var pair in _entries) pair.Value.View.Destroy();
```

**새 파일** `Client/Assets/Scripts/Game/TransportView.cs`:

```csharp
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 12 D14: the drop transport, one long box flying the route (DropRoute.PositionAt at the render tick, the same
    // formula the server places riders with). Shown from the route's start tick to its end tick. Built once; Tick only
    // moves it. No collider: nothing collides with it. Dispose destroys the object and its material.
    public sealed class TransportView : System.IDisposable
    {
        private static readonly Vector3 Size = new Vector3(4f, 2f, 14f);
        private const float Lift = 2.5f;   // drawn above the riders' feet, so a rider's camera sits under its belly

        private readonly GameObject _root;
        private readonly Material _material;
        private bool _hasRoute;
        private DropRoute _route;
        private bool _visible;

        public TransportView()
        {
            _root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _root.name = "DropTransport";
            Object.DestroyImmediate(_root.GetComponent<Collider>());
            _root.transform.localScale = Size;
            var renderer = _root.GetComponent<Renderer>();
            _material = new Material(LitMaterial.Source(renderer.sharedMaterial)) { color = new Color(0.25f, 0.28f, 0.32f) };
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            _root.SetActive(false);
        }

        public void SetRoute(in DropRoute route)
        {
            _route = route;
            _hasRoute = true;
            var direction = new Vector3(route.EndX - route.StartX, 0f, route.EndZ - route.StartZ);
            if (direction.sqrMagnitude > 1e-6f) _root.transform.rotation = Quaternion.LookRotation(direction);
        }

        public void Clear()
        {
            _hasRoute = false;
            SetVisible(false);
        }

        // renderTick: the server tick the world is drawn at this frame.
        public void Tick(double renderTick)
        {
            if (_root == null) return;
            bool flying = _hasRoute && renderTick >= _route.StartTick && renderTick <= _route.EndTick;
            SetVisible(flying);
            if (!flying) return;
            System.Numerics.Vector3 at = _route.PositionAt(renderTick);
            _root.transform.position = new Vector3(at.X, at.Y + Lift, at.Z);
        }

        private void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
```

**수정** `Client/Assets/Scripts/UI/DebugOverlay.cs`:

변경 전:

```csharp
using ProjectH.Client.Net;
```

변경 후:

```csharp
using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
```

변경 전:

```csharp
        private readonly Text _text;
```

변경 후:

```csharp
        private readonly Text _text;
        private readonly Text _movement;
        private readonly Text _route;
```

변경 전:

```csharp
        private ushort _entity;
```

변경 후:

```csharp
        private ushort _entity;
        // Phase 12 D14: the shown movement digits; the line is rebuilt when one changes, at most 10 times a second.
        private MovementMode _mode = (MovementMode)255;
        private int _horizontal;
        private int _vertical;
        private int _energy;
        private int _correction;
        private float _nextMovementAt;
        private bool _hasRoute;
        private DropRoute _shownRoute;
```

변경 전:

```csharp
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
```

변경 후:

```csharp
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _movement = UiFactory.CreateText("Movement", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -90f), new Vector2(900f, 30f));
            _movement.horizontalOverflow = HorizontalWrapMode.Overflow;
            _route = UiFactory.CreateText("Route", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -120f), new Vector2(900f, 30f));
            _route.horizontalOverflow = HorizontalWrapMode.Overflow;
```

변경 전:

```csharp
            _rtt = -1;   // rebuild on the next Tick
```

변경 후:

```csharp
            _rtt = -1;   // rebuild on the next Tick
            _mode = (MovementMode)255;
            _hasRoute = false;
            _route.text = string.Empty;
```

변경 전:

```csharp
        public void Dispose()
```

변경 후:

```csharp
        // Phase 12 D14: the local player's mode, speeds, energy and the last prediction correction, and the transport route.
        public void TickMovement(float now, bool alive, MovementMode mode, float horizontal, float vertical, float energy, float correction,
            bool hasRoute, in DropRoute route)
        {
            if (_root == null || !_visible) return;
            if (hasRoute != _hasRoute || (hasRoute && !SameRoute(route, _shownRoute)))
            {
                _hasRoute = hasRoute;
                _shownRoute = route;
                _route.text = hasRoute ? UiText.RouteLine(route.StartX, route.StartZ, route.EndX, route.EndZ) : string.Empty;
            }
            if (!alive)
            {
                if (_mode != (MovementMode)254) _movement.text = string.Empty;
                _mode = (MovementMode)254;
                return;
            }
            if (now < _nextMovementAt) return;
            int h = Mathf.RoundToInt(horizontal * 10f);
            int v = Mathf.RoundToInt(vertical * 10f);
            int e = Mathf.RoundToInt(energy);
            int c = Mathf.RoundToInt(correction * 100f);
            if (mode == _mode && h == _horizontal && v == _vertical && e == _energy && c == _correction) return;
            _mode = mode;
            _horizontal = h;
            _vertical = v;
            _energy = e;
            _correction = c;
            _nextMovementAt = now + 0.1f;
            _movement.text = UiText.MovementLine(ModeName(mode), h, v, e, c);
        }

        private static bool SameRoute(in DropRoute a, in DropRoute b) =>
            a.StartX == b.StartX && a.StartZ == b.StartZ && a.EndX == b.EndX && a.EndZ == b.EndZ && a.StartTick == b.StartTick;

        // Constant names: no allocation.
        private static string ModeName(MovementMode mode)
        {
            switch (mode)
            {
                case MovementMode.Crouch: return "Crouch";
                case MovementMode.Slide: return "Slide";
                case MovementMode.Vault: return "Vault";
                case MovementMode.Freefall: return "Freefall";
                case MovementMode.Glide: return "Glide";
                case MovementMode.Transport: return "Transport";
                default: return "Ground";
            }
        }

        public void Dispose()
```

**수정** `Client/Assets/Scripts/UI/ResultScreen.cs`:

변경 전:

```csharp
using UnityEngine;
```

변경 후:

```csharp
using ProjectH.Shared.Protocol;
using UnityEngine;
```

변경 전:

```csharp
        // Once when the result screen opens. winnerName null = no winner; killerName is used when died && !byZone.
        public void Show(bool won, int placement, int participants, int kills, string winnerName, bool died, bool byZone, string killerName)
        {
```

변경 후:

```csharp
        // Once when the result screen opens. winnerName null = no winner; killerName is used when died && !noKiller, and the
        // cause (the zone or a fall, Phase 12 D10) when noKiller.
        public void Show(bool won, int placement, int participants, int kills, string winnerName, bool died, bool noKiller,
            DeathCause cause, string killerName)
        {
```

변경 전:

```csharp
            _winner.text = UiText.Winner(winnerName);
            _killedBy.text = UiText.KilledBy(died && !won, byZone, killerName);
```

변경 후:

```csharp
            _winner.text = UiText.Winner(winnerName);
            _killedBy.text = UiText.KilledBy(died && !won, noKiller, cause, killerName);
```

**수정** `Client/Assets/Scripts/UI/UiRoot.cs`:

변경 전:

```csharp
            _debug.Tick(_client.State, _client.RoundTripMs, _client.MyEntityId);
```

변경 후:

```csharp
            _debug.Tick(_client.State, _client.RoundTripMs, _client.MyEntityId);
            _client.TickMovementDebug(_debug, Time.unscaledTime);
```

변경 전:

```csharp
            string winner = r.WinnerId == 0 ? null : UiText.NameOr(_client.NameOf(r.WinnerId), r.WinnerId);
            _result.Show(won, r.Placement, r.Participants, r.Kills, winner, _client.DiedThisRound, _client.KilledByZone, _client.KillerName);
```

변경 후:

```csharp
            string winner = r.WinnerId == 0 ? null : UiText.NameOr(_client.NameOf(r.WinnerId), r.WinnerId);
            _result.Show(won, r.Placement, r.Participants, r.Kills, winner, _client.DiedThisRound, _client.KilledByZone, _client.LastDeathCause,
                _client.KillerName);
```

**수정** `Client/Assets/Scripts/UI/UiText.cs`:

변경 전:

```csharp
        public const string ZoneName = "자기장";
```

변경 후:

```csharp
        public const string ZoneName = "자기장";
        // Phase 12 D10, D14: a fall's name in the kill feed and the result, and the movement hints.
        public const string FallName = "낙하";
        public const string HintJump = "[Space] 뛰어내리기";
        public const string HintGlide = "[Space] 글라이더 펼치기";
        public const string HintDoorOpen = "[E] 문 열기";
        public const string HintDoorClose = "[E] 문 닫기";
```

변경 전:

```csharp
        // Who ended this player's match: a player, the zone, or nobody yet (won, or still alive).
        public static string KilledBy(bool died, bool byZone, string killerName)
        {
            if (!died) return string.Empty;
            if (byZone) return "탈락 원인: " + ZoneName;
            return "나를 처치한 플레이어: " + killerName;
        }
```

변경 후:

```csharp
        // Who ended this player's match: a player, the zone, or nobody yet (won, or still alive).
        public static string KilledBy(bool died, bool byZone, string killerName) => KilledBy(died, byZone, DeathCause.Zone, killerName);

        // Phase 12 D10: noKiller (KillerId 0) is the zone or a fall, as the cause says.
        public static string KilledBy(bool died, bool noKiller, DeathCause cause, string killerName)
        {
            if (!died) return string.Empty;
            if (noKiller) return "탈락 원인: " + CauseName(cause);
            return "나를 처치한 플레이어: " + killerName;
        }

        public static string CauseName(DeathCause cause) => cause == DeathCause.Fall ? FallName : ZoneName;
```

변경 전:

```csharp
        // "가해자 ▸ 피해자"; killer null = the zone (KillerId 0).
        public static string KillLine(string killer, string victim) => (killer ?? ZoneName) + " ▸ " + victim;
```

변경 후:

```csharp
        // "가해자 ▸ 피해자"; killer null = the zone (KillerId 0).
        public static string KillLine(string killer, string victim) => KillLine(killer, victim, DeathCause.Zone);

        // Phase 12 D10: killer null = the zone or a fall ("낙하 ▸ 피해자").
        public static string KillLine(string killer, string victim, DeathCause cause) => (killer ?? CauseName(cause)) + " ▸ " + victim;
```

변경 전:

```csharp
        // ---- Title input (D4) ----
```

변경 후:

```csharp
        // Phase 12 D14: the movement line. Speeds and the correction in tenths and hundredths, so the caller can rebuild it
        // only when a shown digit changes (DebugOverlay).
        public static string MovementLine(string mode, int horizontalTenths, int verticalTenths, int energy, int correctionCentimetres) =>
            "이동 " + mode + "   수평 " + Tenths(horizontalTenths) + " m/s   수직 " + Tenths(verticalTenths) + " m/s   기력 " + Int(energy) +
            "   보정 " + Hundredths(correctionCentimetres) + " m";

        // D14: the drop transport's route (there is no map UI).
        public static string RouteLine(float startX, float startZ, float endX, float endZ) =>
            "수송기 (" + Int(Mathf0(startX)) + ", " + Int(Mathf0(startZ)) + ") → (" + Int(Mathf0(endX)) + ", " + Int(Mathf0(endZ)) + ")";

        private static int Mathf0(float value) => (int)Math.Round(value);

        private static string Tenths(int tenths)
        {
            string sign = tenths < 0 ? "-" : string.Empty;
            int a = Math.Abs(tenths);
            return sign + Int(a / 10) + "." + Int(a % 10);
        }

        private static string Hundredths(int hundredths) => Int(hundredths / 100) + "." + Two(hundredths % 100);

        // ---- Title input (D4) ----
```

- [ ] **Step 4: 확인한다**

Run: `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj` → 60개 통과(`PlayerPoseTests` 5개 추가)
Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx` → 모두 통과. 전체 1047개(1038 통과, 9개 건너뜀)

- [ ] **Step 5: Commit** — `feat(client): poses, transport and door views, mode cameras, energy bar, hints and the F1 movement line (Phase 12 D14)`

---

### Task 10: 문서

**Files:**
- Create: `Docs/Movement.md`
- Modify: `Docs/Networking.md`, `Docs/BattleRoyale.md`, `Docs/Map.md`, `Docs/Client.md`, `Docs/Bots.md`, `Docs/Server.md`, `Docs/Architecture.md`
- `Docs/LoadTest.md`는 "Phase 완료 확인" 3의 측정 뒤에 고친다.

숫자와 이름은 코드와 같게 쓴다. 이 Task의 내용은 Task 1–9의 코드에서 나온 것만이다.

- [ ] **Step 1: `Docs/Movement.md`(새 파일)**

- **첫 문단:**
  - 이동은 Shared `MovementSimulation`(서버와 Client 예측이 같은 코드)이 한다.
  - 상태는 `MoveState`, 수치는 `MovementTuning`과 `MoveSettings`다.
  - 서버는 입력만 받는다(D12).
- **"모드" 표:** `MovementMode` 일곱 값마다 다음을 적는다.
  - 상자 높이: `CollisionHeight`. 1.8 m, 웅크리기·슬라이드는 1.2 m
  - 처리 함수: `StepGround`, `StepVault`, `StepAir`, `DropTransport.Ride`
  - 행동 가능 여부: 지상·웅크리기·슬라이드만
  - Snapshot 표현: `Flags` bit1–3
  - Client 자세: `PlayerPose`
- **"전환 규칙" 절:** Mermaid 상태도로 그린다.

  ```mermaid
  stateDiagram-v2
      [*] --> Ground
      Ground --> Crouch: 웅크리기(땅, 6 m/s 미만)
      Ground --> Slide: 웅크리기(땅, 6 m/s 이상)
      Crouch --> Ground: 놓음 + 설 자리 / Jump + 설 자리
      Slide --> Crouch: 3 m/s 미만 · 막힘 · 놓음(설 자리 없음)
      Slide --> Ground: 놓음 + 설 자리 / Jump(속도 유지)
      Ground --> Vault: Jump + 앞 입력 + 장애물(D8 검사 4개)
      Vault --> Ground: ModeTicks 0
      Transport --> Freefall: 구간 안 Jump / 구간 끝
      Freefall --> Glide: Jump / 지면 30 m
      Glide --> Ground: 착지(수평 속도 0, 피해 없음)
      Freefall --> Ground: 착지
  ```

  - 다음 입력은 무시한다.
    - 공중·웅크리기·슬라이드의 Vault
    - 자유 낙하·글라이드의 웅크리기
    - 탑승 중의 이동
- **"수치" 표:** `MovementTuning`의 상수를 이름·값·단위·뜻으로 모두 적는다. 기력의 Tick 정수 비용(67·83/100, Spec과 다른 점 5)도 적는다.
- **"판정" 절:**
  - Vault 검사 4개와 Hurdle·Mantle 도착점(Task 2의 설명 그대로)
  - 공중 조작 상한(Spec 해석 3)
  - 슬라이드 경사(`HeightField.Gradient`)
  - 글라이더 자동 전개의 지면 거리(`GroundDistance`)
  - 바깥벽 경계
  - 낙하 피해 공식(`CombatRules.FallDamage`, 체력만, 피해 허용 시)
  - 이동 이상 검사(`MovementLimits`)
- **"수송기" 절:**
  - `DropPlanner`의 경로(Spec과 다른 점 2)
  - 뛰어내리기 구간(±70 m, 다른 점 3)
  - `Ride`와 `Step`을 같은 Tick에 부르지 않는 이유
  - Client의 Tick 대응(`ServerTick - Ack + Seq`)
- **"테스트" 절:** `MovementModesTests`, `VaultTests`, `DeploymentMovementTests`, `MatchDeploymentTests`, `FallDamageTests`, `HitBoxModeTests`, `DoorTests`, EditMode `MovementPredictionTests`

- [ ] **Step 2: `Docs/Networking.md`**

- **첫 문단:** `ProtocolVersion` 10(Phase 12: 이동 모드 Flags, 14 B Self, `Crouch`, `TransportRoute`, `DoorStates`, `PlayerRespawned.Mode`, `PlayerDied.Cause`).
- **"Packets" 표:**
  - `WorldSnapshot` 헤더는 27 B이고 Self 14 B다. 필드: 기력 u16 ×100, 수평 속도 X·Z i16 1/256, `ModeTicks`, `EnergyDelayTicks`.
  - Entity `Flags`: bit0 살아 있음, bit1–3 모드, bit4 달리기, bit5 기진. 90명 패킷은 1197 B다.
  - `PlayerRespawned` + `Mode`(20 B), `PlayerDied` + `Cause`(0 자기장·플레이어, 1 낙하. 7 B)
  - 새 행 `TransportRoute` | S→C | ReliableOrdered | 시작 X·Z, 끝 X·Z, 고도(float 5개), 시작 Tick, 길이 Tick(u32 2개) = 29 B. 경기 시작 때 Respawn보다 먼저, Join·Resume 때.
  - 새 행 `DoorStates` | S→C | ReliableOrdered | 열린 문 비트 마스크(문 5개, 없는 비트는 읽기 실패) = 2 B. 바뀐 Tick의 끝, Join·Resume 때.
  - 입력 버튼 표에 `Crouch` 2048(누른 상태)을 더한다.
- **"Movement" 절:** `Docs/Movement.md`를 가리킨다. 보정 비교 항목을 고친다: 위치·수직 속도·수평 속도(0.01), 모드·기력·Tick 값·기진(정확히).
- **새 절 "투입과 문 (Phase 12)":**
  - 탑승 Tick 대응
  - 문 예측(1초 또는 다음 `DoorStates`)
  - Ack 0일 때 모드가 다르면 서버 상태로 맞춘다.
- **"Validation (서버)" 절:**
  - 상태 전환 규칙(D12): 행동 제한, `Ground` → `Glide` 직접 전환 없음
  - Fuzz 테스트가 새 Parser 둘도 거친다.

- [ ] **Step 3: `Docs/BattleRoyale.md`**

- **"경기 시작 (D3)" 절:** Phase 12 순서를 적는다.
  1. 경로를 정해(`SpawnSeed + Round`) 보낸다.
  2. 모두를 `Transport`로 Respawn한다.
  3. 문을 모두 닫는다.
  4. 자기장을 `Start(route.EndTick)`으로 시작한다(첫 축소는 경로 끝 + 45초).

  `AirDrop=false`면 Phase 6의 투입 지점이다.
- **새 절 "공중 투입 (Phase 12 D4–D6)":** 수송기 → 뛰어내리기 구간 → 자유 낙하 → 글라이더(30 m 자동) → 착지. 탑승자는 자기장·총알을 받지 않는다. 재접속(D16) 때의 진행도 적는다.
- **"사망·순위·승자" 절:** 낙하 사망은 처치자 없이 원인 `Fall`이고 순위가 있다. 낙하 피해는 체력만 깎고, 피해가 허용될 때만이다.
- **"아직 없는 것" 절:** 지도 UI(낙하 지점 고르기)를 더한다.

- [ ] **Step 4: `Docs/Map.md`**

- **새 절 "문 (`GameMap.Doors`, 5개, Phase 12 D9)":**
  - 위치(Rustvale 남·남·북, Gearworks 북·남)와 크기
  - `Boxes`에 들어가지 않는 이유(옆이 닿는 상자 금지 규칙)
  - 충돌 세계(상자 + 닫힌 문)와 E 규칙(2.5 m·±60°·층)
  - 닫기 조건, 밀치기, 라운드 시작 때 닫힘
- **"박스" 절:** Vault로 넘는 높이 구분을 적는다.
  - 1.0 m Hurdle(달리는 중)
  - 1.5·2.0 m Mantle
  - 3 m 이상 불가
- **"맵 바꾸기" 절:** 문을 바꾸면 `DoorCount`와 `DoorStatesPacket`의 비트 수를 함께 본다.

- [ ] **Step 5: `Docs/Client.md`**

- **"구조" 표:** 새 파일과 역할을 더한다.
  - `Game/`: `PredictedDoors`, `DoorRule`(서버 규칙 복사본, 소스 링크 테스트), `PlayerPose`, `PlayerView`(`PlayerViewFactory`), `TransportView`, `DoorViews`
  - `Camera/`: 모드 목표(`CameraTargets`)
  - `Input/InputReader`: C 토글, Ctrl 누름
- **새 절 "이동과 투입 (Phase 12)":**
  - 조작: Space 점프·Vault·뛰어내리기·글라이더, Shift 달리기, C·Ctrl 웅크리기, 달리며 C 슬라이드, E 문·줍기
  - 카메라 목표 표(D14)
  - HUD: 기력 막대, 안내 문구
  - F1 두 줄(이동, 수송기 경로)
  - 원격 자세
- **"Lifetime" 절:** 생성 순서에 `TransportView`·`DoorViews`를, 해제 순서에 그 둘을 더한다. 이벤트 구독은 23개가 된다(`TransportRouteReceived`, `DoorStatesReceived`).
- **새 절 "Unity 확인 순서 (Phase 12)":** "Phase 완료 확인" 5의 목록을 그대로 옮긴다.
- **"자동 검사" 절:** `EditTests` 도구가 돌리는 EditMode 테스트 60개와 `MovementPredictionTests`의 방식을 적는다.

- [ ] **Step 6: `Docs/Bots.md`**

- **"판단 규칙" 절** 맨 앞에 투입 규칙을 적는다.
  - 목표: POI 또는 받은 아이템
  - `PlanJumpTick`과 0.5초 재입력
  - 낙하 조종, 5 m 안이면 멈춤
- **"봇이 쓰는 정보":** `TransportRoute`, 자기 Entity의 모드
- **"한계":**
  - 웅크리기·슬라이드·E 문은 쓰지 않는다.
  - 시야 판정(`LineOfSight`)은 문을 모른다. 닫힌 문 너머로 쏘면 서버가 막는다.
- **"테스트":** `BotDeployTests`

- [ ] **Step 7: `Docs/Server.md`**

- **"실행" 절:** `--Server:AirDrop=false`(땅에서 시작, 비교용)를 적는다.
- **"관측" 절:**
  - Health 줄의 `movementAnomalies`(정상이면 0)
  - Meter `projecth.movement_anomalies`
- **"스레드와 소유권":** 문(`DoorSet`)과 경로는 Game Loop만 쓴다.

- [ ] **Step 8: `Docs/Architecture.md`**

- 첫 줄을 "Phase 12 Deployment & Traversal 기준"으로 바꾼다. 설계 근거에 `Docs/specs/2026-10-02-phase12-deployment-traversal-design.md`를 더한다.
- `Shared/` 행: 이동 모드·수송기 경로·문 상자(`Simulation`), v10 패킷(`TraversalPackets`)
- Server 행: `DropPlanner`, `DoorSet`·`DoorRules`, `MovementLimits`
- Client 행: 예측 문(`PredictedDoors`)

- [ ] **Step 9: Commit** — `docs: Phase 12 deployment and traversal`

---

## Phase 완료 확인

1. **빌드·테스트.**
   - `dotnet build Server/ProjectH.Server.slnx --no-incremental`: 경고 0
   - `dotnet test Server/ProjectH.Server.slnx`: 모두 통과. 1047개 중 1038 통과, MySQL 9개 건너뜀.
   - 두세 번 반복한다. UDP 통합 테스트가 Timeout에 민감하다. 한 번이라도 실패하면 원인을 찾는다. 같은 테스트가 다시 실패하면 테스트 서버의 Timeout을 늘리지 말고 보고한다. `WorldItemsTests.AddSearchRemove_AllocateNothing` 단독 실패는 Global Constraints대로 한다.
2. **DB가 있으면 `PROJECTH_TEST_MYSQL`을 켜고 DB 테스트를 돌린다.** 컨테이너는 이 계획이 띄우지 않는다. 꺼져 있으면 "DB 테스트 대기"로 보고한다.

   ```bash
   export PROJECTH_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev"
   dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Persistence"
   dotnet test Server/ProjectH.Server.slnx
   ```

   Expected: 첫 명령은 모두 통과하고 건너뜀 0이다. 둘째 명령은 1047개 모두 통과, 건너뜀 0이다.
3. **봇 50명 부하 측정(Phase 11 비교).** 컨트롤러가 실행한다. 두 번 잰다(Spec 해석 16). 둘 다 Release, 2분이다. 집계는 Stats 줄 12개 중 처음 2개를 버린 10개다.

   ```bash
   dotnet build Server/ProjectH.Server.slnx -c Release

   # A. 개발 모드(Phase 11과 같은 조건)
   dotnet Server/src/ProjectH.Server/bin/Release/net10.0/ProjectH.Server.dll --Server:Port=7790 --Server:MaxPlayers=100 --Server:DevRespawn=true > load12-dev-server.log 2>&1 &
   echo $! > load12-dev-server.pid
   # 4초 뒤
   dotnet Server/src/ProjectH.Bots/bin/Release/net10.0/ProjectH.Bots.dll --port 7790 --count 50 --duration 120 --connect-interval-ms 30 > load12-dev-bots.log 2>&1
   kill $(cat load12-dev-server.pid)   # 자기가 띄운 pid로만 끈다

   # B. 정식 경기(투입 켬, DB 끔): 50명이 모이면 5초 뒤 시작
   dotnet Server/src/ProjectH.Server/bin/Release/net10.0/ProjectH.Server.dll --Server:Port=7791 --Server:MaxPlayers=100 --Server:MinPlayers=50 --Server:StartCountdownSeconds=5 --Persistence:Enabled=false > load12-match-server.log 2>&1 &
   echo $! > load12-match-server.pid
   # 4초 뒤
   dotnet Server/src/ProjectH.Bots/bin/Release/net10.0/ProjectH.Bots.dll --port 7791 --count 50 --duration 120 --connect-interval-ms 30 > load12-match-bots.log 2>&1
   kill $(cat load12-match-server.pid)
   ```

   - **비교 기준(Phase 11, 개발 모드 50명, `Docs/LoadTest.md` "Phase 11 확인"과 그 실행의 Stats 줄):**
     - Tick p50 0.07–0.08 ms
     - Tick p95 0.10–0.15 ms(최댓값 0.15 ms), p99 최댓값 0.23 ms
     - CPU 0.2 %
     - bytesOut/s 약 515–548 kB/s, pktOut/s 1377–2979(사격 양에 따라 다르다)
   - **A에서 기대하는 것:**
     - Tick p95가 같은 수준이다. 0.17 ms를 넘으면 한 번 더 재고, 그래도 넘으면 보고한다.
     - bytesOut/s는 Snapshot 패킷마다 8 B가 는다. 50명 × 15 Hz × 8 B ≈ 6 kB/s, 약 1 %다.
     - pktOut/s는 같은 수준이다.
   - **B에서 기록하는 것:**
     - 투입 구간(경기 시작 뒤 약 30초)과 그 뒤 전투 구간의 Tick p50·p95·p99, CPU, bytesOut/s, pktOut/s
     - 봇 줄의 `alive`와 `match=Playing`
     - 계획 단계에서 같은 서버로 봇 20명을 70초 돌렸다(`MinPlayers=20`). Tick p95는 0.05 ms, `movementAnomalies=0`이었고 경기가 끝까지 진행됐다.
   - **크기:** Entity 13 B, 헤더 27 B, 90명 패킷 1197 B(`PacketTests`가 고정한다). 부하 결과와 함께 적는다.
   - **Health 줄(A·B 모두)에서 다음은 모두 0이어야 한다.**
     - `movementAnomalies`
     - Kick: `kicks`
     - 잘못된 패킷: 이유별 `badPackets`
     - Tick 실패: `tickFailures`, `loopFailures`
     - 경기 초기화·멈춤: `matchResets`, `stalls`
     - 봇 `reconnects=0`
   - 결과는 `Docs/LoadTest.md`에 "Phase 12 확인" 절로 남긴다. 숫자는 실제로 잰 값만 적는다.
4. **Unity 컴파일(실제 저장소).**
   - `dotnet build <스크래치>/p12tools/uc/UnityCompile.csproj --no-incremental`(`RepoRoot` 기본값 = `E:/popol/ProjectH`): 경고 0, 오류 0. EditMode 테스트까지 컴파일된다.
   - `dotnet test <스크래치>/p12tools/edittests/EditTests.csproj`: 60개 통과
   - 컨트롤러의 원래 `UnityCompile.csproj`(테스트 제외)로도 `dotnet build`: 경고 0, 오류 0
   - 그 다음 사용자에게 Unity Editor를 포커스해 달라고 요청한다. 자동 import가 끝나면 `Editor.log`(`%LOCALAPPDATA%/Unity/Editor/Editor.log`)의 새 줄에 `error CS`가 없는지 확인한다. Unity가 Global Constraints의 `.meta`들을 만든다.
   - Editor에서 EditMode 테스트 전체(Test Runner)를 돌릴 수 있으면 돌린다. `EditTests` 밖의 테스트(`AimSolverTests` 등)도 그대로 통과해야 한다. 사용자가 Editor를 열 수 없으면 "Unity 확인 대기"로 기록하고 넘어간다.
5. **Unity Editor 확인 순서(사용자, spec §2 Editor 확인 목록).**
   - 서버를 `dotnet run --project Server/src/ProjectH.Server -- --Persistence:Enabled=false`로 띄우고 Play한다. Multiplayer Play Mode로 Player 2도 켠다(`MinPlayers` 2). `Docs/Client.md`에 같은 목록을 남긴다(Task 10).
   1. **수송기:** 카운트다운이 끝나면 두 캐릭터가 사라진다. 길쭉한 상자(수송기)가 맵 밖에서 들어와 가로지른다.
      - 카메라는 12 m 뒤에서 따라간다.
      - 맵 안쪽에 들어오면 "[Space] 뛰어내리기"가 보인다.
      - F1을 누르면 "수송기 (x, z) → (x, z)" 줄이 보인다.
   2. **자유 낙하:** Space를 누르면 엎드린 캡슐로 떨어진다. 카메라는 6 m 뒤로 물러나고 FOV가 70이 된다. 부드럽게 바뀌어야 한다. "[Space] 글라이더 펼치기"가 보인다. WASD로 앞(빠름)·옆·뒤(느림)로 간다.
   3. **글라이더:** Space를 누르거나 지면 30 m에 오면 머리 위에 노란 날개가 생긴다. 천천히 내려온다. 착지하면 Ground가 되고 체력이 그대로다.
      - 아무것도 누르지 않은 Player 2도 맵 안쪽에서 떨어져 맵 안에 착지한다.
   4. **기력과 달리기:** Shift를 누르면 FOV가 66으로 넓어지고 왼쪽 아래 기력 막대가 줄어든다. 5초 뒤 막대가 주황색이 되고 걷는다. 1초 뒤 차오르기 시작해 20을 넘으면 다시 달린다. 가득 차고 달리지 않으면 막대가 사라진다.
   5. **웅크리기:** C를 누르면 짧은 캡슐이 되고 카메라가 낮아진다(1.1 m). 다시 누르면 선다. Ctrl은 누르는 동안만이다. 웅크린 채 2.5 m/s로 걷는다.
   6. **슬라이드:** 달리며 C를 누르면 뒤로 기운 짧은 캡슐로 미끄러진다. 언덕을 내려가면 더 빨라진다. Space를 누르면 슬라이드 속도로 뛴다. 벽에 막히면 웅크리기가 된다.
   7. **Mantle·Hurdle:**
      - Gearworks의 1.5 m 상자(34, 60)를 향해 Space를 누르면 앞으로 기운 캡슐이 0.4초에 윗면으로 올라간다.
      - 1 m 상자(36, 40)를 달리며 넘으면 0.2초에 반대편 땅에 선다. 상자를 지나는 동안 캡슐이 상자와 겹쳐 보이는 것은 Placeholder로 받아들인다.
      - 3 m 벽은 넘지 않는다.
   8. **착지 느낌과 낙하 피해:** 글라이더로 지붕(3.25 m)에 내린 뒤 걸어서 떨어지면 피해가 없다. 지붕에서 점프해 떨어지면 체력이 약 4 준다(착지 속도 약 13.6 m/s). 빨간 피격 방향 표시는 낙하 피해에는 나오지 않는다(방향 0).
   9. **문:**
      - Rustvale 집 남쪽 문 앞에서 "[E] 문 열기"가 보인다. E로 열고 닫는다. 그 자리의 줍기 안내는 숨는다.
      - 다른 Player가 문 자리에 서 있으면 닫히지 않는다.
      - 달리며 닫힌 문에 부딪히면 열린다.
      - Player 2의 화면에도 같은 상태가 보인다.
      - 닫힌 문 너머의 Player 2를 쏘면 맞지 않는다.
   10. **원격 자세와 결과:** Player 2가 웅크리면 Player 1 화면에서 짧은 캡슐로 보인다. 머리 높이(1.5 m)를 쏘면 빗나간다. 낙하로 죽으면 Kill Feed가 "낙하 ▸ 이름", 결과 화면이 "탈락 원인: 낙하"다.
   11. Console에 `error`·`Exception`이 없다. F1의 "보정" 값이 평소 0.00 m이고, 문을 밀치는 순간만 잠깐 커질 수 있다(D9).
6. **Windows 빌드 화면 확인(컨트롤러).** 컨트롤러의 방식대로 한다.
   1. `Client/Assets`, `Packages`, `ProjectSettings`와 `Shared`를 스크래치로 복사한다.
   2. `Unity.exe -batchmode -quit -buildWindows64Player`로 빌드한다.
   3. 서버(투입 켬, `--Persistence:Enabled=false`)와 봇 몇 명을 띄우고 플레이어를 `-autoConnect -port N -devId X`로 실행한다.
   4. `PrintWindow`로 창을 캡처한다.

   캡처에서 다음을 본다.
   - 수송기 상자와 맵이 분홍색(magenta)이 아니다. 새 재질도 `LitMaterial`을 쓴다.
   - 수송기 탑승 중 화면이다. 안내 문구, 12 m 카메라, 봇 캡슐이 보이지 않는다.
   - 착지 뒤 화면이다. 갈색 문 상자, 기력 막대(달리는 중), 원격 봇의 자세(달리기 기울기, 글라이더 날개)가 보인다.
   - F1 두 줄(이동, 수송기)
   - Player.log에 `Exception`이 없다.
7. **범위 확인.**
   - `git diff --stat main -- Shared`에는 다음만 있어야 한다.
     - `Simulation`: `MovementMode.cs`, `MovementTuning.cs`, `MoveState.cs`, `MovementSimulation.cs`, `HeightField.cs`, `InputCommand.cs`, `DropTransport.cs`, `GameMap.cs`
     - `Protocol`: `PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs`, `ClientPackets.cs`, `ServerPackets.cs`, `CombatPackets.cs`, `TraversalPackets.cs`
   - `git diff --stat main -- Client`에는 다음만 있어야 한다.
     - `Client/Assets/Scripts`의 Task 6·8·9 파일
     - `Client/Assets/Tests/EditMode`의 네 파일
   - `git diff --stat main -- Server/src/ProjectH.Bots`에는 Task 7의 네 파일만 있어야 한다.
8. **`.meta` 커밋.** Unity가 만든 Global Constraints의 `.meta` 11개를 Push 전에 함께 커밋한다(`git add`로 하나씩).
9. **Push.** `github-push` 스킬로 `main`에 Squash Commit·Push한다.
