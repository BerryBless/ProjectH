# Client

Unity 6000.3.24f1, URP, Input System. Scene·Prefab 없이 `GameBootstrap`(RuntimeInitializeOnLoadMethod, AfterSceneLoad)가 `GameClient`+`DevConnectPanel`을 만들고, `GameClient.Awake`가 테스트 월드를 만든다.

## 구조

| 파일 | 역할 |
|---|---|
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
| `Game/PlayerViewFactory` | 캡슐 뷰 생성, 공유 Material(내 색, 남 색, 사망 회색). 원격 뷰는 캡슐 Collider 대신 서버 판정 상자와 같은 크기(0.7 × 1.8 × 0.7)의 `BoxCollider`를 `Ignore Raycast` 레이어(2)에 둔다(조준 광선만 이 레이어를 본다). 내 뷰에는 Collider가 없다 |
| `Camera/ShoulderCamera`, `ShoulderCameraMath` | 오른쪽 어깨 카메라, 우클릭 ADS(거리 3.5→1.6 m, 오른쪽 0.55→0.65 m, FOV 60→42, 감도 ×0.6), 두 단계 SphereCast(반경 0.2 m) 충돌. 1단계(머리 기준점 → 어깨점)는 부딪힌 거리에서 0.02 m(`ShoulderClearance`) 덜 나가 2단계가 벽에 붙은 채 시작하지 않게 한다. 계산은 `ShoulderCameraMath` 순수 함수 |

의존성: 어셈블리 `ProjectH.Client`는 `ProjectH.Shared`, `Unity.InputSystem`, `UnityEngine.UI`(조준점)를 참조한다. LiteNetLib은 `Assets/Plugins/LiteNetLib/LiteNetLib.dll`(서버와 같은 버전(NuGet 2.1.4)의 netstandard2.1 빌드. 서버는 같은 패키지의 net8.0 빌드를 쓴다)이다. Git UPM 패키지는 `.meta`가 없어 Unity가 무시하므로 쓰지 않는다. 소스 스텝 실행은 안 된다.

## 프레임 흐름 (`GameClient.Update`)

1. `NetClient.Poll` → 콜백(Joined/Spawned/Snapshot/전투 이벤트)이 메인 스레드에서 실행됨
2. `InputReader.Update`(점프·재장전·슬롯 눌림 큐잉), 커서·버튼 처리: 커서가 풀려 있으면 좌클릭은 잠금만 하고(Join 후), 그 클릭은 버튼을 뗄 때까지 발사로 치지 않는다. 조준·발사는 커서가 잠겨 있을 때만.
3. 원격 플레이어 렌더(`ServerClock.RenderTick`로 보간 대상 Tick 계산, 이 값이 입력의 ViewTick이 된다)
4. 카메라 Look(ADS 중 감도 ×0.6) → `LocalPlayerPredictor.Advance`(고정 스텝 예측, Sprint·Fire는 매 Step, 눌림은 마지막 Step) → 로컬 뷰 자세(사망이면 눕힘)
5. `LateUpdate`: 카메라 Follow(머리 기준점 → 어깨점 → 카메라 두 번 SphereCast, 막히면 즉시 당기고 풀리면 감쇠 복귀) → 조준점(`Physics.SyncTransforms` 후 화면 중앙 Raycast, 원격 플레이어 포함) → 이번 프레임 입력들에 조준(눈은 예측 위치 기준)·ViewTick 기록 → `WeaponState` Step → `PlayerInput` 전송 → 내 발사 연출(카메라가 움직인 뒤라 조준점과 일치) → HUD

`Joined` 응답에서 SimHz·SnapshotHz를 받아 `ServerClock`과 보간 지연(`2 / SnapshotHz`)을 정한다. 자기 `PlayerSpawned`를 받으면 `LocalPlayerPredictor`와 로컬 뷰를 만든다.

## Lifetime

생성 순서: 월드(+박스 Material) → InputReader → ShoulderCamera → Crosshair → CombatHud → LocalFireEffects → NetClient. `GameClient.OnDestroy`는 역순으로 해제한다: 이벤트 구독 해제(11개) → NetClient Dispose(`NetManager.Stop`) → 매치 상태(예측기·로컬 뷰·원격 뷰·ServerClock·WeaponState, 조준점·HUD 숨김, 발사 연출 숨김) → LocalFireEffects Dispose(풀 GameObject·Material) → CombatHud Dispose(Canvas) → Crosshair Dispose(Canvas) → InputAction Dispose → 플레이어 공유 Material(3개) → 월드·박스 Material 파괴. 연결이 끊기면(`OnDisconnected`) 매치 상태를 지운다. 종료 때 Unity가 오브젝트를 먼저 파괴했을 수 있어(OnDestroy 순서는 보장되지 않음) `Crosshair.SetVisible`, `CombatHud`의 메서드, `LocalFireEffects.HideAll`은 루트가 파괴됐으면 아무것도 하지 않고 돌아온다. 예외가 나면 뒤의 해제가 건너뛰어지기 때문이다.
`renderer.material`은 쓰지 않는다(복제됨). 캡슐은 스폰/디스폰 때만 생성·파괴하므로 풀링하지 않는다. `RemotePlayers`는 Spawn/Despawn/Clear로만 증감하고, 보간 히스토리는 플레이어당 8개 고정이다. 발사 연출은 궤적 16·탄착 32개를 생성자에서 한 번 만들고 `RingCursor`로 오래된 것부터 재사용하므로 늘어나지 않는다. 발사·카메라의 Physics 호출은 단일 결과 버전만 쓴다.

## 실행과 두 Client 확인

1. 서버: `dotnet run --project Server/src/ProjectH.Server`
2. Multiplayer Play Mode: Window > Multiplayer > Multiplayer Play Mode에서 Player 2 활성화 → Play → 각 창에서 Connect
3. Standalone: 빌드 후 `ProjectH.exe -autoConnect -devId p2` + Editor Play (`-host`, `-port`도 지정 가능. 기본 127.0.0.1:7777, devId 미지정 시 `dev-<8자리>` 자동 생성)
4. 조작: 좌클릭(커서 잠금, 잠긴 뒤 누르고 있으면 발사), 우클릭(누르는 동안 조준), R(재장전), 1·2(무기 교체), WASD, Shift(달리기), Space(점프), Esc(해제), F1(패널)

## 자동 검사

EditMode 테스트: `Assets/Tests/EditMode`(`LocalPlayerPredictorTests`, `ArenaPredictionTests`, `RemotePlayerInterpolatorTests`, `ShoulderCameraMathTests`, `AimSolverTests`, `WeaponStateTests`, `RingCursorTests`). 이 테스트들은 Physics·GameObject·네이티브 Quaternion 함수를 쓰지 않으므로 Unity 밖 NUnit 프로젝트로도 돌릴 수 있다. 발사 간격은 `WeaponState`가 센다.

```bash
"C:/Program Files/Unity/Hub/Editor/6000.3.24f1/Editor/Unity.exe" -batchmode -nographics -projectPath Client -runTests -testPlatform EditMode -testResults _workspace/editmode-results.xml -logFile _workspace/unity-tests.log
```
(Editor가 프로젝트를 열고 있지 않을 때)
