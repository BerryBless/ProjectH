# Phase 0 + 네트워크 이동 동기화 — 설계 Spec

## Context

신규 오리지널 3인칭 배틀로얄(Unity Client + .NET 10 Dedicated Authoritative Server)의 첫 구현 단위.
현재 저장소는 Unity 템플릿(Unity 6000.3.24f1, URP, Input System 1.20)과 빈 `Server/`뿐이며 게임 코드는 없다 → Phase 0부터 시작.

이번 범위(사용자 요청 §68 + 예측 추가 합의):

```text
Unity Client → .NET 10 Server 접속 → Player Session 생성 → Player Spawn
→ 두 Client 간 위치 동기화 (내 캐릭터 Client Prediction + Reconciliation, 다른 플레이어 Snapshot Interpolation)
```

성공 기준: 두 Client가 서버에 접속해 서로 움직이는 모습을 보고, 같은 시나리오가 Unity 없이 `dotnet test` 통합 테스트로 자동 검증된다.

제외: DB, 실제 인증(DevPlayerId만), 전투, 맵(평면 바닥만), Aim/Shoulder/Camera Collision, Interest Management, 압축.

적용 규칙: 루트 `CLAUDE.md`, `game-core-rules`(항상), `game-perf-checks` 판단표 — 이번 변경은 Packet/Session/Tick/Worker/Queue/Update를 모두 건드리므로 `safety`, `server-concurrency`, `server-hotpath`, `queue-cache`, `client-hotpath` 점검 대상.

## 합의된 결정

| 항목 | 결정 |
|---|---|
| Transport | LiteNetLib (Server: NuGet, Unity: Git UPM URL, 같은 버전 태그로 고정) |
| Shared 공유 | `/Shared` 로컬 UPM 패키지(`com.projecth.shared`). Unity는 `file:../../Shared`, Server는 `src/ProjectH.Shared` csproj가 같은 .cs를 Compile Include |
| Shared 예외 | 이동 계산 `Shared/Runtime/Simulation/MovementSimulation.cs` 하나만 허용(순수 수학, System.Numerics만). `game-core-rules` 4절에 예외 명시 |
| 서버 스레드 | B: LiteNetLib Network 스레드 + 전용 Game Loop 스레드, 크기 제한 Channel로 전달 |
| Tick | Simulation 30Hz, Snapshot 15Hz(2 Tick마다). appsettings.json으로 변경 가능 |
| 직렬화 | 수기 Span/BinaryPrimitives, `ref struct PacketWriter/PacketReader`, TryRead로 실패 반환(예외 없음) |
| Host | Generic Host(설정·로깅·Ctrl+C). 게임 객체는 직접 생성, DI 그래프 확장 안 함 |
| 테스트 | xUnit, `Server/src` + `Server/tests` 분리 |
| Unity 구성 | Scene/Prefab 편집 없이 `[RuntimeInitializeOnLoadMethod]` Bootstrap이 바닥·조명·카메라·캡슐 생성 |
| 접속 UI | IMGUI 개발용 패널 + `-host -port -devId` 실행 인자 |
| 카메라 | Follow + Mouse Look만 (Aim/Shoulder/Collision은 Phase 1) |
| 2 Client 확인 | 통합 테스트 + Multiplayer Play Mode 패키지 + Standalone Build/Editor |

## 1. 서버 스레드·Queue·Lifetime

```text
[LiteNetLib Network 스레드]                 [Game Loop 전용 스레드 (고정 Tick)]
 수신 → 크기/ID 검사 → Parse → struct 복사 ──▶ Channel ──▶ Tick당 최대 N개 소비 → Simulate
                                                         → 2 Tick마다 Snapshot → peer.Send
```

- 우리 코드의 Lock 없음. 스레드 간 전달은 `System.Threading.Channels`만, 송신은 thread-safe한 `NetPeer.Send`. Deadlock 검토 대상 Lock 없음을 코드 주석과 Docs에 명시.
- Channel 2개:

| Channel | Producer / Consumer | 크기 | Overflow |
|---|---|---|---|
| Control(Connect·Join·Disconnect) | Network → Game Loop | MaxPlayers × 3 | 구조상 불가(ConnectionRequest에서 MaxPlayers 초과 거절, 연결당 이벤트 ≤ 3). 발생 시 Critical 로그 + 해당 peer Disconnect |
| Input | Network → Game Loop | MaxPlayers × 8 | DropOldest + drop 카운터. Game Loop는 Tick당 최대 N개만 소비 |

- 메시지는 `readonly struct`(할당 없음). NetDataReader 버퍼는 콜백 후 재사용되므로 콜백 안에서 값 복사.
- Session 소유권: Game Loop만 `Dictionary<int peerId, Session>` 접근. 생성=Connect 이벤트, 제거=Disconnect/Timeout 이벤트(LiteNetLib DisconnectTimeout이 이벤트 도달 보장).
- Game Loop: 전용 Thread(ThreadPool 아님). 목표 시각 ~1ms 전까지 Sleep 후 Yield로 맞춤. Tick 소요 시간 기록.
- 종료: Ctrl+C → CancellationToken → Loop 종료 → 모든 peer Disconnect → `NetManager.Stop()` → 스레드 Join.

## 2. Protocol·Packet

프레이밍 `[PacketId: byte][payload]`. float 그대로 전송(압축은 측정 후).

| Packet | 방향 | Delivery | 내용 |
|---|---|---|---|
| (ConnectionRequest data) | C→S | 연결 시 | `ProtocolVersion u16`, `DevPlayerId string ≤ 32B` — 버전 불일치·만원이면 거절 |
| JoinMatchRequest | C→S | ReliableOrdered | 없음 |
| JoinMatchResponse | S→C | ReliableOrdered | `Result`, `MyEntityId u16`, `ServerTick u32`, `SimHz`, `SnapshotHz` |
| PlayerSpawned | S→C | ReliableOrdered | `EntityId`, `Position`, `Yaw` |
| PlayerDespawned | S→C | ReliableOrdered | `EntityId` |
| PlayerInput | C→S | Unreliable | 최근 입력 3개: 각 `Seq u32`, `MoveX`, `MoveY`, `Yaw`, `Buttons`(Jump, Sprint) |
| WorldSnapshot | S→C | Sequenced | `ServerTick u32`, `AckInputSeq u32`(수신 Client 전용), `Count u16`, `[EntityId, Position, Velocity.y, Yaw] × Count` |

검증(서버): NaN/Infinity, 방향 길이 > 1(클램프), Seq 역행/중복 무시, 알 수 없는 PacketId·짧은 패킷 → drop + 연결별 카운터, 임계 초과 시 Disconnect(Warning 로그 1회).

## 3. 이동·예측

- `MovementSimulation.Step(ref MoveState, in InputCommand, float dt, in MoveSettings)`: 속도 = 방향(Yaw 기준) × 걷기/달리기 속도, 중력, 점프, y=0 평면 바닥. 이동 수치(`MoveSettings`: 걷기·달리기 속도, 중력, 점프 속도)는 Shared의 상수 하나만 쓴다. 서버 설정으로 바꾸면 Client 예측과 어긋나므로 이번 단계에서는 설정 파일에 두지 않는다(데이터화는 무기 데이터와 함께 이후 단계).
- 서버: 플레이어별 `PlayerInputBuffer`(8칸 링, Seq 중복·역행 제거, 가득 차면 가장 오래된 것 폐기). Tick마다 1개 소비, 없으면 직전 입력 반복.
- Client: 30Hz 누적기로 입력 샘플 → Step → 64칸 링에 (Seq, Input, 결과 State) 저장 → 최근 3개 전송.
  Snapshot 수신 시 `AckInputSeq` 이후 입력 재적용. 오차 작으면 렌더 오프셋으로 보정 감쇠, 크면(예: 2m) 즉시 스냅.
- 렌더: 예측 State 두 개를 누적기 alpha로 보간 → 가변 FPS에서도 부드럽게.
- 다른 플레이어: 엔티티별 8칸 Snapshot 링, 렌더 시점 = 추정 서버 시간 − 2 Snapshot 간격(≈133ms).

## 4. 구조

```text
ProjectH/
├─ Shared/                          로컬 UPM 패키지
│   ├─ package.json                 com.projecth.shared
│   └─ Runtime/ProjectH.Shared.asmdef (noEngineReferences)
│       ├─ Protocol/  PacketId, ProtocolConstants, 패킷 struct, PacketWriter, PacketReader
│       └─ Simulation/MovementSimulation.cs, MoveState, InputCommand, MoveSettings
├─ Server/
│   ├─ ProjectH.Server.slnx
│   ├─ src/ProjectH.Shared/         netstandard2.1, LangVersion 9, Compile Include ../../../Shared/Runtime/**/*.cs
│   ├─ src/ProjectH.Server/         net10.0 console
│   │   ├─ Program.cs, appsettings.json, ServerOptions.cs
│   │   ├─ Net/NetworkListener.cs, Net/InboundMessage.cs
│   │   ├─ Game/GameLoop.cs, Game/Match.cs, Game/Session.cs, Game/PlayerInputBuffer.cs
│   │   └─ Diagnostics/TickMetrics.cs, Diagnostics/ServerStats.cs
│   └─ tests/ProjectH.Server.Tests/ xUnit (+ HeadlessClient 테스트 헬퍼)
├─ Client/
│   ├─ Packages/manifest.json       + com.projecth.shared(file:../../Shared), LiteNetLib(Git, 태그 고정), com.unity.multiplayer.playmode
│   └─ Assets/Scripts/ProjectH.Client.asmdef
│       ├─ Bootstrap/GameBootstrap.cs, Bootstrap/DevConnectPanel.cs
│       ├─ Net/NetClient.cs
│       ├─ Input/InputReader.cs
│       ├─ Game/GameClient.cs, LocalPlayerPredictor.cs, RemotePlayerInterpolator.cs, EntityViews.cs
│       └─ Camera/ThirdPersonCamera.cs
└─ Docs/  Architecture.md, Networking.md, Server.md, Client.md, Database.md(예정), BattleRoyale.md(예정)
```

- 서버 쪽 Shared csproj를 netstandard2.1 + C# 9로 빌드해 Unity 비호환 API/문법을 `dotnet build`에서 먼저 잡는다.
- Unity: 매 프레임 할당 없음(재사용 버퍼·링 배열, LINQ 없음). NetClient는 메인 스레드 전용(`UnsyncedEvents=false`, Update에서 PollEvents). GameClient OnDestroy/OnApplicationQuit에서 Disconnect → Stop.
- Shared 폴더에 Unity가 만드는 `.meta`는 커밋 대상. Server 빌드 산출물은 `Server/` 아래에만 생기므로 Unity가 import하지 않는다.

## 5. 관측·로깅

10초마다 Information 1줄: 접속자, Packets/s, Bytes/s, Tick P50/P95/P99/Max, Input drop, Bad packet, GC(0/1/2), Working set. 패킷 단위 로그 없음. TickMetrics는 고정 크기 샘플 버퍼(무한 증가 없음).

## 6. 하네스 반영

- `.claude/skills/game-core-rules/SKILL.md` 4절에 Shared 예외(`Shared/Runtime/Simulation` 순수 이동 수학만) 추가.
- `CLAUDE.md` 변경 이력에 한 줄.
- 구현은 `game-dev-orchestrator` 흐름(server-engineer → Shared 먼저, client-engineer, game-reviewer 리뷰)으로 진행 가능. 실행 방식은 구현 계획 작성 후 선택.

## Verification

1. `dotnet build Server/ProjectH.Server.slnx` (Shared netstandard2.1 포함)
2. `dotnet test Server/ProjectH.Server.slnx`
   - 단위: 패킷 왕복·잘림/쓰레기 입력 TryRead=false, MovementSimulation 결정성·속도·중력·점프·NaN, PlayerInputBuffer 중복/역행/오버플로/누락 반복, TickMetrics 백분위
   - 통합: 인프로세스 서버 + HeadlessClient 2개 → Join → 서로 Spawn 수신 → A 이동 → B Snapshot에서 A 위치 변화 + A의 AckInputSeq 증가 → A 종료 → B Despawn 수신. 버전 불일치·만원 거절, 잘못된 패킷 무크래시, Shutdown 후 스레드·소켓 정리
3. `dotnet run --project Server/src/ProjectH.Server` 실행 후 10초 통계 로그 확인
4. Unity batchmode 컴파일: `"C:/Program Files/Unity/Hub/Editor/6000.3.24f1/Editor/Unity.exe" -batchmode -quit -projectPath Client -logFile -` (Editor가 닫혀 있을 때)
5. 수동: Multiplayer Play Mode 2 Player, Standalone Build + Editor로 서로 이동 확인

## 다음 단계 (이 Spec 승인 후)

1. 이 Spec을 `Docs/specs/2026-09-30-phase0-network-sync-design.md`로 저장 (Commit은 "푸시" 요청 시)
2. writing-plans 스킬로 구현 계획 작성 → 실행 방식 선택
