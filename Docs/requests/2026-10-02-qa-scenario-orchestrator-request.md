# QA Scenario Orchestrator (요청 원문, 2026-10-02)

> 사용자가 붙여 넣은 요청서를 그대로 옮겼다. 사용자 지시: "다만들어줘" (MVP → Phase 2~5 전체).

현재 게임 프로젝트를 자동 QA하기 위한 개발 도구를 만든다.

목표는 단순 Unit Test Runner가 아니다. QA 담당자 또는 개발자가 코드 수정 없이 Scenario 작성 → 실행 → 게임 상태 조작 → 결과 검증 → 실패 시점 기록 → 동일 조건 재실행 할 수 있는 개발 도구를 만든다.

## 1. 최종 목표

다음과 같은 테스트를 시나리오로 작성할 수 있어야 한다.

```text
서버 시작 → PlayerA 접속 → PlayerB 접속 → 둘 다 Match 입장 → PlayerA를 특정 위치로 이동
→ PlayerB에게 Assault Rifle 지급 → PlayerB가 PlayerA 사격 → PlayerA HP 60인지 확인
→ PlayerA Disconnect → 5초 대기 → PlayerA Reconnect → 기존 캐릭터로 복귀했는지 확인 → PASS
```

이 과정에서 사람이 실제 게임을 조작할 필요가 없어야 한다.

## 2. 기술 방향

새 QA Tool은 별도 프로젝트로 만든다(예: `/Tools/QA`). QA Tool은 .NET 10 기반. 가능하면 서버와 동일한 C# 생태계를 사용한다.

## 3. QA Tool 구성

Scenario Editor → Scenario Definition → Scenario Runner → Actor Controller → Game Server / Headless Client / Unity Client → Assertion Engine → Report

## 4. 핵심 구성 요소

QA Orchestrator, Scenario Runner, Scenario Parser, Actor Manager, Server Process Manager, Client Process Manager, Bot Controller, Assertion Engine, Fault Injector, Metrics Collector, Report Generator.
처음부터 거대한 Framework를 만들지 않는다. MVP부터 단계적으로 구현한다.

## 5. 가장 중요한 설계 원칙

QA 기능을 실제 Production Game Protocol에 억지로 집어넣지 않는다. 개발 및 테스트 전용 Control Channel을 별도로 둔다. Game Protocol과 QA Control Protocol을 분리한다. QA Control은 Development / Test / Local / CI 환경에서만 활성화한다. Release / Production에서는 기본적으로 비활성화되어야 한다.

## 6. Production 안전성

QA Control 기능 때문에 실제 서버 보안이 약화되면 안 된다. Teleport, GiveWeapon, SetHealth, KillPlayer, SetZone, ForceMatchState 등은 실제 Production Client가 사용할 수 없어야 한다.

## 7. QA Control 활성화 조건

서버 시작 옵션 `--qa-mode` 또는 `QA_MODE=true`. QA Mode가 아닐 때 QA Control Port 자체를 열지 않는 것을 우선한다.

## 8. QA Control Transport

기존 LiteNetLib Game Protocol과 섞지 않는다. 초기 구현은 가장 단순한 구조(HTTP localhost API / WebSocket / Named Pipe). 우선 추천: HTTP(Command, Query) + WebSocket(Event, State Update). 복잡한 RPC Framework를 새로 만들지 않는다.

## 9. Bind 주소

QA Control Server는 기본적으로 127.0.0.1에만 Bind한다. 명시적인 설정 없이는 외부 Network에 공개하지 않는다.

## 10. Scenario Format

기본 Format: JSON (.NET 기본 지원, Parser 의존성 최소화, Validation·Schema 쉬움, UI Editor 연결 쉬움). 추후 필요하면 YAML Import. 처음부터 둘 다 구현하지 않는다.

## 11. Scenario Folder

`/QA/Scenarios`. 카테고리: Smoke, Combat, Movement, Loot, Building, Zone, Reconnect, Network, Persistence, Regression, Stress.

## 12. Scenario 기본 구조

```json
{
  "name": "Reconnect During Match",
  "description": "Player reconnects during active match.",
  "seed": 12345,
  "timeoutSeconds": 60,
  "actors": [
    { "id": "playerA", "type": "HeadlessClient" },
    { "id": "playerB", "type": "HeadlessClient" }
  ],
  "steps": [
    { "action": "connect", "actor": "playerA" },
    { "action": "connect", "actor": "playerB" },
    { "action": "waitFor", "condition": "match.playing", "timeout": 10 },
    { "action": "disconnect", "actor": "playerA" },
    { "action": "wait", "seconds": 5 },
    { "action": "reconnect", "actor": "playerA" },
    { "assert": "player.connected", "actor": "playerA" }
  ]
}
```

실제 Schema는 구현 중 프로젝트 구조에 맞춰 정리한다.

## 13. Scenario Version — `"schemaVersion": 1`. 향후 Format 변경 대비.

## 14. Scenario Seed — 선택적 Seed. Random이 필요한 시스템(Loot, Zone, Bot Target, Deployment Route, Weak Point, Spawn)에 가능한 범위에서 주입.

## 15. 재현성 — 실패 Report에 Seed, Scenario, RunId(예: qa-20261002-00142)를 반드시 기록. 같은 Scenario + Seed로 최대한 동일 재현.

## 16. Actor — 초기 Type: HeadlessClient, UnityClient. MVP는 HeadlessClient 우선. 기존 Headless Bot Client를 확장해 재사용.

## 17. Headless Client — 기존 Bot을 QA Actor로. 자율 행동뿐 아니라 QA Mode에서 외부 명령 기반 행동 지원.

## 18. Bot Controller — Connect, Disconnect, Reconnect, Move, Look, Aim, Fire, StopFire, Jump, Sprint, Crouch, Slide, Interact, Pickup, Drop, UseItem, SwitchWeapon, SwitchBuildMode, Build. 현재 구현된 기능만 우선. 미구현 기능 때문에 Framework가 막히지 않게.

## 19. High-Level Action — moveTo, pickupItem, attackPlayer, buildWall, enterZone, leaveZone.

## 20. Low-Level Action — press, release, look, moveVector.

## 21. Action Layer — Scenario High-Level Action → Actor Command → Game Input. Scenario가 Input 구조와 강하게 결합되지 않게.

## 22. 서버 직접 조작 — SetPlayerPosition, SetHealth, SetShield, GiveWeapon, GiveAmmo, GiveItem, GiveResource, SetZone, ForceMatchState, SpawnLoot, SpawnBuildPiece, DamagePlayer, DamageBuild, KillPlayer. 단 Gameplay 검증과 Test Setup을 명확히 구분.

## 23. Arrange / Act / Assert — 내부적으로 세 단계. QA Command로 상태를 만든 것은 Arrange에만 권장. 실제 검증 대상 동작은 Game Protocol로.

## 24. Gameplay 우회 금지 — 총격 테스트에서 DamagePlayer로 "사격 성공" 판단 금지. Aim → Fire → Packet → Server Hit Validation → Damage 경로.

## 25. Scenario Step 공통 필드 — id, action, actor, timeout, continueOnFailure.

## 26. Step ID — Report에서 어느 Step에서 실패했는지 표시.

## 27. Wait — `{ "action": "wait", "milliseconds": 500 }`. 가능하면 Condition Wait 선호.

## 28. WaitFor — `{ "action": "waitFor", "condition": "player.state", "actor": "playerA", "equals": "Alive", "timeoutMilliseconds": 3000 }`

## 29. Polling — 너무 높은 빈도로 확인하지 않는다. Config 가능한 Poll Interval.

## 30. Assertion Engine — equals, notEquals, greaterThan, lessThan, between, exists, notExists, approximately.

## 31. Player Assertion — player.connected, alive, health, shield, position, ammo, weapon, inventory, resources, kills, placement.

## 32. Match Assertion — match.state, playerCount, aliveCount, zonePhase, winner.

## 33. Build Assertion — build.exists, health, material, pieceType, supported, count.

## 34. Server Assertion — server.running, tickP95, tickP99, memory, activeSessions, dbQueueLength.

## 35. Network Assertion — network.connected, rtt, packetCount, disconnectReason.

## 36. Approximately — `{ "assert": "player.position.x", "actor": "playerA", "approximately": 100.0, "tolerance": 0.2 }`

## 37. Event Assertion — PlayerDamaged, PlayerKilled, ItemPickedUp, BuildPlaced, BuildDestroyed, PlayerDisconnected, PlayerReconnected.

## 38. Event Wait — `{ "action": "waitForEvent", "event": "PlayerKilled", "timeoutMilliseconds": 3000 }`

## 39. Event History — QA Mode에서 제한된 Event History(예: 최근 1,000개 또는 시간 Window). 무제한 저장 금지.

## 40. Variables — `"variables": { "targetHealth": 60 }`

## 41. Runtime Variable — 실행 중 얻은 ID(ItemId, PlayerId, BuildPieceId)를 변수로 저장.

## 42. Actor Reference — Scenario에서는 playerA/playerB 같은 Alias. Runtime에 실제 ID와 매핑.

## 43. Position Alias — POI_Center, TestArena_A, TestArena_B 등. Map QA Marker와 연동 가능.

## 44. QA Marker — QA_Spawn_A, QA_Combat_10m, QA_Combat_50m, QA_Build_Test. Release Build에서는 필요하지 않으면 제거/비활성화.

## 45. Timeline UI — Step을 위에서 아래로(01 Start Server … 12 Assert State).

## 46. Scenario Editor — 로컬 Web UI. .NET 10 ASP.NET Core. Electron 같은 무거운 Framework 우선 도입 금지. `http://localhost:<port>`.

## 47. UI Layout — 왼쪽 Scenarios(카테고리), 오른쪽 Scenario Editor(Step 목록), 아래 Run / Stop / Step / Retry Failed, 그 아래 Live Log / Actors / Server Metrics.

## 48. 편집 방식 — MVP: Raw JSON Editor + Form Editor. Form Editor에서 Step 추가/삭제/순서 변경.

## 49. Drag & Drop — 가능하면. UI 때문에 Runner 개발이 지연되면 후순위.

## 50. Action 추가 — `+ Add Step` → Action 종류 선택(Connect, MoveTo, GiveItem, Fire, WaitFor, Assert, Disconnect).

## 51. JSON Preview — Form Editor 결과가 JSON으로 어떻게 저장되는지 표시.

## 52. Schema Validation — 저장 전: Unknown Action, Actor 없음, 잘못된 Parameter, 음수 Timeout, 존재하지 않는 Variable.

## 53. 자동완성 — 가능하면 JSON Schema 기반. MVP 필수 아님.

## 54. Run — Run, Run From Step, Run Until Step, Single Step.

## 55. Pause — 중간 Pause. Pause 상태에서 Player / Match 상태 확인.

## 56. Manual Intervention — Pause 중 실제 Unity Client에서 움직여도 됨. 그 뒤 계속 실행.

## 57. Stop — Scenario Cancel, Headless Client 종료, 필요하면 Server 종료, Resource Cleanup.

## 58. Cancellation — Runner 전체 CancellationToken 기반. Stop 후 계속 실행되면 안 됨.

## 59. 실패 시 중지 — 기본: Assertion 실패 → Scenario Stop.

## 60. Continue On Failure — Step 단위 continueOnFailure.

## 61. Retry Failed Step — 실패 Step 재실행. 게임 상태가 이미 변경되어 의미 없으면 경고.

## 62. Retry Scenario — 같은 Scenario, Seed로 전체 재실행 버튼.

## 63. Breakpoint — Step에 Breakpoint. 해당 Step 직전 Pause.

## 64. Debug Run — 각 Step 전후 Server State Snapshot 기록. 모든 테스트에서 무조건 기록하지 않음.

## 65. State Inspector — PlayerId, Connection, Position, Velocity, Health, Shield, Weapon, Ammo, Inventory, Resources, MovementState.

## 66. Match Inspector — State, Tick, Players, Alive, Zone, Elapsed Time.

## 67. Server Inspector — 기존 Metrics 연결: CPU, Memory, GC, Tick P50/P95/P99, Packets/sec, Bytes/sec, Queue Length.

## 68. Live Log — 필터: QA, Server, Actor, Network, Assertion.

## 69. Log Correlation — 실행마다 RunId. QA Log에 RunId. 가능하면 Server QA Command에도 RunId 전달.

## 70. Fault Injection — Disconnect Client, Stop Server, Stop MySQL, Network Latency, Packet Loss, Packet Duplication, Temporary Network Block. 단계적으로.

## 71. Network Fault — Client 측 QA Network Wrapper: LatencyMs, JitterMs, PacketLossPercent, DuplicatePercent.

## 72. 서버 Network Code 오염 금지 — Production Network Path를 복잡하게 하지 않는다. QA Mode Adapter 또는 Headless Client 쪽에서 우선 구현.

## 73. Network Scenario — 접속 → Latency 200ms → Packet Loss 10% → 전투 → Reconnect 발생 여부 확인.

## 74. Disconnect Scenario — Match 진행 → A 강제 Disconnect → 5초 → Reconnect → 같은 Player State 복구 확인.

## 75. Timeout Scenario — Client Input 중단 → Input Timeout → Disconnect Reason 확인.

## 76. DB Fault — MySQL Docker 관리 가능하면 Stop DB / Start DB. Docker가 없으면 Skip.

## 77. DB Down Scenario — DB 중단 → Match 완료 → Server 계속 실행 → DB Queue 확인 → DB 복구 → Writer 재연결 → 데이터 저장 확인.

## 78. Server Restart — 개발 Server Process 재시작.

## 79. Process Manager — 선택적으로 Server 시작/종료, Bot 시작/종료, Unity Client 시작.

## 80. 외부 Server 지원 — Attach Existing Server 모드.

## 81. Server Readiness — 단순 1초 대기 금지. QA API의 /health 등으로 Ready 대기.

## 82. Actor Readiness — Connected, Joined, Spawned를 조건 기반으로 대기.

## 83. Screenshot — Unity Client 사용 시 captureScreenshot. Editor / Development Client만 지원해도 됨.

## 84. Screenshot 목적 — UI, HUD, Kill Feed, Result Screen, Build Preview, Reconnect Screen.

## 85. 자동 이미지 판정 — 초기 구현 안 함. 저장까지만.

## 86. Unity Client Automation — Development 전용 Command Receiver: OpenMenu, CloseMenu, OpenStats, ToggleDebug, CaptureScreenshot.

## 87. 게임 입력과 UI 명령 분리 — Gameplay는 실제 Input Path. UI 자동화는 별도 QA Command.

## 88. IME — `{ "action": "manualCheck", "description": "한글 이름 입력 후 IME 조합 상태 확인" }`. Runner Pause, QA 담당자가 PASS / FAIL 선택.

## 89. 수동 QA와 자동 QA 혼합 — Automated / Manual / Automated Step 혼합.

## 90. Manual Check Report — 수동 검증 결과도 최종 Report에 포함.

## 91. Scenario Tags — smoke, combat, reconnect, building, network, db, manual, slow.

## 92. Test Suite — Smoke, Combat, Reconnect, Pre-Push, Nightly Suite.

## 93. Pre-Push Suite — Server Start, Connect, Move, Shoot, Pickup, Death, Reconnect.

## 94. Full Regression — 모든 Scenario. 오래 걸려도 됨.

## 95. CI Mode — UI 없이 CLI: `dotnet run --project Tools/QA -- run Smoke` 또는 `qa run scenario.json`.

## 96. Exit Code — 0 = PASS, 1 = Scenario Failure, 2 = Tool Error.

## 97. Report — JSON, HTML.

## 98. Report 내용 — Scenario, RunId, Start Time, Duration, Seed, Server Version, Git Commit, Steps, PASS / FAIL, Failure Step, Failure Reason, Metrics, Logs, Screenshots.

## 99. Git Commit 기록 — `git rev-parse HEAD`.

## 100. Dirty Working Tree — Dirty 여부 기록.

## 101. Step Report — `01 Connect PlayerA PASS 121 ms` …

## 102. Failure Report — Expected / Actual 명확히.

## 103. State Dump — 실패 시 관련 Actor 상태 자동 Dump(Position, Health, Shield, Weapon, Ammo).

## 104. Server State Dump — 필요하면 Match State, Tick, Alive Players, Queues, Build Count.

## 105. 실패 직전 Event — 최근 N개 QA Event를 Report에 포함.

## 106. Replay Log — 초기에는 전체 네트워크 Replay 시스템 안 만듦. Scenario + Seed + Input Timeline 재현 우선.

## 107. Scenario Recording — 실제 플레이 입력을 Record해서 Scenario 초안으로. 확장 가능하게 설계. MVP에는 넣지 않음.

## 108. Parameterized Scenario — 같은 Scenario를 여러 Parameter(Ping 0/50/100/200)로.

## 109. Data Set — `"parameters": [ { "ping": 0 }, { "ping": 100 }, { "ping": 200 } ]`. 기본 Runner 안정 후 구현.

## 110. 반복 실행 — Repeat(예: 100회).

## 111. Random Seed Sweep — Seed 1~100.

## 112. Timeout — Scenario 전체 Timeout과 Step Timeout 둘 다. 무한 대기 금지.

## 113. Cleanup — 성공/실패/취소와 관계없이: Bot 종료, 임시 Process 종료, Network Fault 해제, DB 복구, Temp File 정리.

## 114. Cleanup 실패 — Report에 기록. Main 결과와 구분.

## 115~132. Scenario Library — 최소: Smoke Server Connect, Movement, Combat, Lag Compensation, Inventory, Healing, Shield, Zone, Elimination, Reconnect, Reconnect Expired(현재 정책에 맞춰), Invalid Packet(기존 Fuzz 호출/통합), DB Down, Server Shutdown, Building(Phase 13), Building Support, Turbo Build.

## 133. Performance Scenario — Spawn 50 Bots → Wait 60s → Assert TickP95 < Threshold.

## 134. 성능 Threshold — Scenario마다 명시(`"assert": "server.tickP95Ms", "lessThan": 2.0`). 실제 목표치는 Baseline 기반으로 사용자가 정함. 임의로 엄격한 수치 금지.

## 135. Regression Baseline — 이전 결과와 비교 가능하게 저장. 초기에는 최근 결과만.

## 136. 성능 변동 경고 — Previous / Current / Change. Threshold 없으면 FAIL 아닌 Warning.

## 137. 툴 자체 테스트 — Scenario Parsing, Validation, Timeout, Cancellation, Assertion, Actor Mapping, Failure Reporting.

## 138. Mock Actor — Runner Unit Test용 MockActor. QA Framework 테스트 용도로만.

## 139. 확장 구조 — `IScenarioActionHandler` 정도의 단순 확장. 모든 것에 Interface 금지.

## 140. Action Registry — "connect" → ConnectAction, "moveTo" → MoveToAction, "assert" → AssertAction.

## 141. Scenario DTO와 Runtime 분리 — Scenario 파일을 실행 도중 수정하지 않음.

## 142. Thread Safety — Runner State는 한 Orchestrator Context에서. 무분별한 ConcurrentDictionary 금지.

## 143. Log Flood 방지 — Ring Buffer, Filter, Rate Limit, Virtualized Log View.

## 144. Actor 수 — 1, 2, 10, 50, 100 고려. UI에 100명을 무겁게 렌더링하지 않음.

## 145. Actor Group — 확장 가능하게. MVP는 개별 Actor + Spawn Count.

## 146. Spawn Actors — `{ "action": "spawnActors", "type": "HeadlessClient", "count": 50, "prefix": "bot" }`

## 147. Group Action — connectAll, disconnectAll.

## 148. Dev Tool 성능 — Server 성능 측정 결과를 오염시키지 않게. Metrics Polling 과도 금지.

## 149. QA API Query — 매 Frame 조회 금지. Event Push 또는 낮은 빈도 Query.

## 150. QA Mode 관측 비용 — 가능한 측정. Benchmark에서는 상세 Event 비활성화 가능.

## 151. MVP 범위 — Scenario JSON, Scenario Runner, Headless Actor, Server QA Control, Connect, Disconnect, MoveTo, GiveWeapon, SetPosition, Fire, Wait, WaitFor, Assert Player State, Assert Match State, Run / Stop, HTML Report, CLI Run.

## 152. MVP 완료 조건 — 서버 실행 → A/B 접속 → 위치 설정 → B에게 Weapon 지급 → B가 실제 Fire Input → Server Combat → A Health 감소 확인 → A Disconnect → 5초 후 Reconnect → A State 복구 확인 → PASS Report 생성. QA Tool의 첫 E2E 검증.

## 153. Phase 2 — Web Scenario Editor, Step-by-Step, Breakpoint, Inspector, Live Log, Metrics.

## 154. Phase 3 — Fault Injection, Network Latency, Packet Loss, DB Stop / Start, Server Restart.

## 155. Phase 4 — Unity Client Automation, Screenshot, Manual Check, UI QA.

## 156. Phase 5 — Parameterized Scenario, Repeat, Seed Sweep, Baseline Comparison, Scenario Recording.

## 157~161. 구현 전 확인 / 재사용 — Headless Bot, Server Entry, Game Loop, Session, Protocol, Metrics, Reconnect, Fuzz, Load Test 구조 확인. 기존 기능 중복 구현 금지. Headless Bot·Metrics·Fuzz·Load Test 재사용(Adapter만).

## 162. 문서 — `/Docs/QA.md`: Architecture, Scenario Format, Actions, Assertions, Actors, QA Mode, Security, Fault Injection, Reports, CLI, Adding New Actions.

## 163. Example Scenarios — 최소 `/QA/Scenarios/Smoke/connect.json`, `Combat/basic_hit.json`, `Reconnect/reconnect.json`, `Zone/zone_damage.json`, `Building/basic_wall.json`. 현재 실제 구현된 기능에 맞는 Scenario만.

## 164. 최종 보고 형식 — QA Tool Architecture, 추가된 프로젝트, Scenario Format, 지원 Action, 지원 Assertion, QA Control API, End-to-End Test, 결과, 사용 방법(실제 명령만), UI, 알려진 제한사항(실제 제한만), 다음 단계.

## 165. 절대 규칙

```text
QA Tool 때문에 Production Protocol 보안을 약화하지 않는다.
QA Command는 QA Mode에서만 활성화한다.
QA Command Port는 기본 localhost 전용이다.
Gameplay 검증을 QA Cheat Command로 우회하지 않는다.
QA Command는 Arrange 용도로 주로 사용한다.
Act는 가능한 실제 Gameplay 경로를 사용한다.
Assertion에는 Server Authoritative State를 사용한다.
무제한 Event History를 만들지 않는다.
Scenario가 무한 대기하지 않게 한다.
Stop / Cancellation이 항상 동작해야 한다.
실패한 Scenario는 Seed와 Step을 기록한다.
기존 Headless Bot을 최대한 재사용한다.
기존 Load Test와 Fuzz Test를 재사용한다.
Production 코드에 QA 전용 분기를 무분별하게 흩뿌리지 않는다.
```

## 166. 최종 사용자 경험

QA Tool 실행 → Combat / Reconnect Scenario 선택 → 필요하면 Step 수정 → Run → 서버 자동 시작 → Bot 자동 접속 → 진행 상황 실시간 표시 → `Step 8 FAILED / Expected Health: 60 / Actual Health: 100` → 해당 Step에서 Pause → 상태 확인 → 같은 Seed로 Retry.
