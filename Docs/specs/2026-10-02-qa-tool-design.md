# QA Scenario Orchestrator 설계 (QA-1 ~ QA-5)

요청: `Docs/requests/2026-10-02-qa-scenario-orchestrator-request.md` (§ 번호는 요청서 기준).

## 결정과 추천 이유

| # | 결정 | 이유 | 틀렸을 때 비용 |
|---|---|---|---|
| D1 | QA Tool은 `Server/src/ProjectH.QA`(.NET 10 콘솔 + 내장 Web UI), 테스트는 `Server/tests/ProjectH.QA.Tests`, 시나리오는 저장소 루트 `QA/Scenarios/<Category>/`. 요청서의 `/Tools/QA`는 예시. | 봇(`ProjectH.Bots`)처럼 서버 폴더에 있지만 서버를 참조하지 않는 도구다. 같은 `.slnx`로 빌드·테스트되어 깨지면 바로 보인다. | 폴더 이동뿐(프로젝트 참조 경로 수정). |
| D2 | 서버 QA Control은 서버 어셈블리 안 `ProjectH.Server/Qa/` 폴더. `GameLoop`에는 null 확인 한 줄(`_qa?.OnTick(...)`)만 추가. `Match`에는 분기를 넣지 않는다. | `internal` Match·Flow·Zone 멤버를 `InternalsVisibleTo`를 넓히지 않고 쓸 수 있다. QA 분기를 한 곳에 모은다(§165). | QA 코드가 서버 바이너리에 포함된다. 꺼져 있으면 포트도 열리지 않고 Tick 비용도 없다. |
| D3 | 활성화 조건: (`--qa-mode` 인자 또는 `QA_MODE=true` 또는 `Qa:Enabled=true`) **그리고** Host 환경이 Production이 아님. `--qa-mode`는 Host Builder에 넘기기 전에 `args`에서 뺀다. Production 환경에서 켜려 하면 경고만 남기고 끈다. | §5·§7: 플래그만으로는 Release 서버에서 실수로 켜질 수 있다. `dotnet ProjectH.Server.dll`의 기본 환경은 Production이므로 실수로 켜지지 않는다. QA Tool이 서버를 띄울 때 `DOTNET_ENVIRONMENT=Development`를 준다. | 환경 변수 없이 띄운 개발 서버는 QA 모드가 안 켜진다. 로그에 이유가 나온다. |
| D4 | Transport: Kestrel(ASP.NET Core Minimal API, `FrameworkReference Microsoft.AspNetCore.App`, NuGet 아님) HTTP JSON. Bind는 코드에서 `IPAddress.Loopback` 고정(`ASPNETCORE_URLS`·`--urls` 무시). 외부 Bind는 `Qa:AllowRemote=true`가 있을 때만. Event는 HTTP 폴링(`/qa/events?after=`)으로 받는다. WebSocket은 서버에 두지 않는다. | §8 "가장 단순한 구조". `HttpListener`는 Windows에서 127.0.0.1 Prefix에 URL ACL이 필요하다. 이벤트는 QA Tool이 낮은 빈도로 가져가면 충분하다(§149). WebSocket은 서버 스레드 모델을 하나 더 늘린다. | 이벤트 지연이 폴링 간격(기본 100 ms)만큼 생긴다. 실시간이 필요하면 나중에 SSE를 붙인다. |
| D5 | Thread Model: HTTP 핸들러는 `Match`를 만지지 않는다. 명령과 조회를 bounded Channel(64)에 넣고 `TaskCompletionSource`로 기다린다(2 s Timeout, 가득 차면 503). GameLoop 스레드가 Tick마다 `RunTick` 끝에서 최대 16개를 실행한다. Executor는 모든 예외를 잡아 명령 오류로 돌려준다. `_match`는 매번 다시 읽는다(Reset이 교체함). Lock 없음. | `Match`는 GameLoop 스레드 전용이라는 기존 규칙을 지킨다. QA 예외가 Tick 실패로 세어지면 Match Reset·서버 정지까지 이어진다. | Tick당 최대 16개라 대량 명령은 Tick 몇 개에 나뉜다. |
| D6 | Event: QA 모듈이 Tick마다 플레이어 상태를 이전 Tick과 비교해 이벤트를 만든다(체력 감소 → PlayerDamaged, 생존 → 사망 → PlayerKilled, Join/Resume/Grace/Leave, MatchState 변경, 조각 수 변화 → BuildPlaced/Destroyed, 인벤토리 변화 → InventoryChanged). Ring Buffer 1,000개. `Qa:Events=false`면 끈다. | `Match`에 QA 콜백을 심지 않는다(§165 마지막 줄). 플레이어 ≤ 100명이라 비교 비용이 작다. | 공격자 같은 원인 정보는 같은 Tick의 Kills 증가로 추정한다. 정확한 원인이 필요하면 서버 로그를 본다. |
| D7 | 플레이어 식별: 시나리오 Alias(`playerA`) → DevPlayerId `qa-<alias>`. 서버 QA API는 DevPlayerId로 찾는다. | DevPlayerId는 재접속해도 같다(Grace Resume 키). PeerId·EntityId는 바뀌거나 내부 값이다. | Alias 길이가 DevPlayerId 한도(바이트)를 넘으면 검증에서 막는다. |
| D8 | Headless Actor는 `ProjectH.Bots`의 `BotConnection`·`BotView`·`BotAim`·`BotSteering`을 그대로 쓴다(프로젝트 참조). Actor는 자율 Brain 대신 "의도(Intent)"를 가진다: 이동 목표, 바라볼 대상, 누른 버튼, 사격 횟수. 모든 Actor는 Actor 전용 스레드 하나가 30 Hz로 돌리고, 대기 중에도 매 Tick 입력을 보낸다. | §16·§158: 봇 재사용. LiteNetLib Manual Mode는 Update를 한 스레드에서 불러야 한다. 입력을 멈추면 서버의 InputTimeout에 걸린다. | 봇 코드가 바뀌면 Actor도 같이 바뀐다(의도한 결합). |
| D9 | Runner는 Orchestrator 한 개의 async 흐름에서 Step을 차례로 실행한다. Actor 명령은 Actor 스레드의 큐로, Actor 상태는 Tick마다 새로 만든 불변 객체를 `Volatile`로 게시한다. ConcurrentDictionary 없음. | §142. | — |
| D10 | Assertion은 기본적으로 서버 QA 상태를 읽는다(`player.*`, `match.*`, `build.*`, `server.*`). Actor 쪽 값은 `network.*`, `actor.*`로만 노출한다. | §165 "Server Authoritative State". | — |
| D11 | Arrange/Act/Assert: Step에 `phase`(arrange/act/assert)를 둘 수 있다. Act·Assert 단계에서 서버 QA 명령(`server.*` 명령 계열)을 쓰면 Validation 경고. `damagePlayer`·`killPlayer`는 Arrange 전용 경고 대상. | §23·§24. 금지가 아니라 경고인 이유: 회귀 시나리오에서 Arrange 성격의 명령이 중간에 필요할 수 있다. | — |
| D12 | QA Tool이 띄운 서버는 기본 설정을 덮어쓴다: `Server:Port=0`(빈 포트), `Qa:Port=0`, `Persistence:Enabled=false`(DB 시나리오만 켬), `Server:AirDrop=false`, `StartCountdownSeconds=1`, `ResultSeconds=1`, Seed 3종 = 시나리오 Seed. 실제 포트는 서버가 stdout 첫 줄 `QA_READY gamePort=… qaPort=…`로 알리고 Tool은 `/qa/health`로 확인한다. | 개발 서버(7777)와 충돌하지 않는다. 수송기 탑승 중 순간이동은 의미가 없다. §81: 1초 대기가 아니라 Ready 조건. | 시나리오가 `server.options`로 다시 덮어쓸 수 있다. |
| D13 | Fault Injection(QA-3)은 QA Tool의 UDP Proxy로 한다: Actor → Proxy → 서버. Latency·Jitter·Loss·Duplicate·Block. LiteNetLib 내장 시뮬레이션은 NuGet 빌드에서 동작이 보장되지 않아 쓰지 않는다. DB는 `docker stop/start <container>`(설정한 컨테이너 하나만, Cleanup에서 항상 다시 시작, Docker가 없으면 Skip, `down -v` 금지). | §72: 서버 Network 코드 오염 금지. | Proxy 하나가 Actor 수만큼 소켓을 연다(최대 100). |
| D14 | Invalid Packet: 저장소에 Fuzz 엔진이 없다(확인함). QA Tool에 작은 `sendRawPacket`/`invalidPackets` Action을 새로 만든다. 서버의 기존 방어(Bad Packet 카운터·Kick)를 `server.badPackets`로 검증한다. | §160은 "있으면 재사용". 없으므로 최소한만 만든다. | — |
| D15 | Unity 자동화(QA-4): `#if UNITY_EDITOR || DEVELOPMENT_BUILD`로 감싼 `QaCommandReceiver`. `-qaPort N` 인자가 있을 때만 localhost HTTP를 연다. 명령: status, screenshot, openMenu, closeMenu, openStats, toggleDebug. Gameplay 입력은 보내지 않는다(§87). | Release 빌드에는 코드 자체가 없다. | Development Build를 따로 만들어야 한다. |
| D16 | Manual Check: Runner가 멈추고 Web UI 또는 CLI(대화형 터미널)에서 PASS/FAIL을 받는다. 비대화형(CI)에서는 `SKIPPED`로 기록하고 실패로 치지 않는다(`--manual fail`로 바꿀 수 있음). | §88·§89. CI가 사람을 기다리며 멈추면 안 된다(§112). | — |
| D17 | Report: `QA/Reports/<runId>/report.json`, `report.html`(단일 파일, 외부 리소스 없음), 스크린샷. `QA/Reports/`는 gitignore. Baseline 비교(QA-5)는 `QA/Reports/history/<scenario>.jsonl`(시나리오당 최근 50개). | §97·§135. | — |
| D18 | Exit Code: 0 PASS, 1 Scenario 실패, 2 Tool 오류(잘못된 JSON, 서버 기동 실패, 내부 예외). Cleanup 실패는 결과와 별도로 보고하고 Exit Code를 바꾸지 않는다(단, 서버 프로세스를 못 끈 경우 2). | §96·§114. | — |

## QA Control API (서버, QA 모드에서만)

Base `http://127.0.0.1:{Qa:Port}`. 요청 본문 최대 16 KB. 모든 응답은 JSON. 오류는 `{ "ok": false, "error": "..." }`와 상태 코드(400 잘못된 인자, 404 플레이어·조각 없음, 409 상태가 맞지 않음, 503 큐 가득, 504 Tick 응답 Timeout).

| Method | Path | 설명 |
|---|---|---|
| GET | `/qa/health` | `{ ok, qaMode, gamePort, qaPort, simHz, serverTick, matchState, round, pid, version, environment, seeds{loot,zone,spawn}, options{…} }` |
| POST | `/qa/command` | `{ runId?, command, player?, args{} }` → `{ ok, error?, result{} }` |
| GET | `/qa/players` | 모든 플레이어 DTO 배열 |
| GET | `/qa/players/{devPlayerId}` | 플레이어 DTO(없으면 404) |
| GET | `/qa/match` | Match DTO |
| GET | `/qa/build?x=&z=&radius=&max=` | `{ count, pieces[] }` (최대 500) |
| GET | `/qa/metrics?windowSeconds=` | Tick p50/p95/p99/max(QA 자체 Ring, 최대 120 s), Health 카운터 요약, gc, workingSetMB, cpu%, pktIn/s, pktOut/s, qaCommandMs |
| GET | `/qa/events?after=&max=` | `{ next, oldest, dropped, events[{seq,tick,utc,type,player?,data{}}] }` |
| POST | `/qa/server/stop` | Host 정지 요청(정상 종료 경로: 클라이언트는 ServerShutdown을 받는다) |

### 명령 (`/qa/command`의 `command`)

| 명령 | args | 비고 |
|---|---|---|
| `mark` | `text` | 서버 로그에 RunId와 함께 남김(§69) |
| `setPosition` | `x,y?,z, yaw?` | y 없으면 지형 높이. 속도 0, 위치 기록(Lag Compensation) 초기화, 이동 이상 감지에 걸리지 않게 |
| `setHealth` / `setShield` | `value` | 범위 검증 |
| `giveWeapon` | `weapon`(id 또는 이름), `rarity?`, `slot?`, `select?`(기본 true) | 탄창 가득 |
| `giveAmmo` | `type`(light/medium/heavy), `amount` | |
| `giveItem` | `item`(medkit/shieldCell), `count` | |
| `giveResource` | `material`(wood/stone/metal), `amount` | |
| `damagePlayer` | `amount` | Arrange 전용 |
| `killPlayer` | — | Arrange 전용 |
| `forceMatchState` | `state`: `start`(카운트다운 즉시 종료) / `finish` | |
| `setZone` | `phase?`, `advance?` | QA-1에서는 구현 가능한 범위만 |
| `spawnLoot` | `kind`, `id?`, `amount?`, `x,y?,z` | `result.itemId` |
| `spawnBuildPiece` | `piece`, `material`, `x,y,z` 또는 셀 좌표, `rotation?` | `result.pieceId` |
| `damageBuild` | `pieceId`, `amount` | |

정확한 이름·범위는 구현이 `weapons.json`·`items.json`·`building.json`에 맞춰 정하고 `Docs/QA.md`에 적는다.

### Player DTO

`devPlayerId, entityId, connected, graced, alive, participant, health, shield, position{x,y,z}, velocity{x,y,z}, yaw, mode, grounded, currentSlot, weapon{slot,weaponId,name,rarity,magAmmo}, weapons[], ammo{light,medium,heavy}, medkits, shieldCells, resources{wood,stone,metal}, kills, placement, damageDealt, lastProcessedSeq`

### Match DTO

`state, round, tick, elapsedSeconds, players, connected, graced, participants, alive, winner(devPlayerId|null), zone{phase, center{x,z}, radius, ...}, buildPieces, worldItems`

## QA Tool 구성 (`ProjectH.QA`)

```text
Program (CLI: run / validate / list / ui)
 └ QaOrchestrator ── RunContext(runId, seed, variables, cancellation)
     ├ ScenarioLoader / ScenarioValidator (System.Text.Json DTO, schemaVersion 1)
     ├ ActionRegistry: name → IScenarioActionHandler
     ├ ServerProcessManager (launch | attach) + QaServerClient(HTTP)
     ├ ActorManager → HeadlessActor(BotConnection 재사용) / MockActor(테스트) / UnityActor(QA-4)
     ├ AssertionEngine (경로 → 값 → 연산자)
     ├ EventCursor (서버 이벤트 폴링, 100 ms 기본)
     └ ReportWriter (JSON + HTML)
```

## 시나리오 형식 (schemaVersion 1)

```json
{
  "schemaVersion": 1,
  "name": "Basic Hit",
  "description": "...",
  "tags": ["combat", "smoke"],
  "seed": 12345,
  "timeoutSeconds": 60,
  "server": { "mode": "launch", "options": { "Server:MinPlayers": "2" } },
  "variables": { "minDamage": 1 },
  "actors": [ { "id": "playerA", "type": "HeadlessClient" } ],
  "steps": [
    { "id": "a_connect", "action": "connect", "actor": "playerA" },
    { "action": "waitFor", "condition": "match.state", "equals": "Playing", "timeoutMilliseconds": 10000 },
    { "phase": "arrange", "action": "setPosition", "actor": "playerA", "position": "QA_Combat_A" },
    { "phase": "assert", "action": "assert", "path": "player.health", "actor": "playerA", "lessThan": 100 }
  ]
}
```

- 공통 필드: `id, action, actor, phase, timeoutMilliseconds, continueOnFailure, breakpoint, saveAs, description`.
- 축약형 `{ "assert": "player.health", ... }`도 받는다(= `action: assert, path: ...`).
- `${name}`은 변수(시나리오 변수 + `saveAs`로 저장한 실행 중 값)로 바꾼다. 없는 변수는 Validation 오류.
- 위치: `{x,y,z}` 또는 이름(QA Marker `QA/Markers.json`, 맵 POI 이름).
