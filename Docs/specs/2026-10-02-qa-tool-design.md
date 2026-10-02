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

## QA-2 결정 (Web UI, §45-69, §143-144)

| # | 결정 | 이유 | 틀렸을 때 비용 |
|---|---|---|---|
| D19 | `ProjectH.QA ui [--port 5180]`: 같은 도구 안의 ASP.NET Core(FrameworkReference, NuGet 아님) Minimal API + `wwwroot` 정적 파일(빌드 출력에 복사). HTML/CSS/바닐라 JS, npm·번들러 없음. 127.0.0.1에만 Bind. | §46: 무거운 Framework 금지. 서버와 같은 생태계. 빌드 단계가 하나 늘지 않는다. | UI가 커지면 JS 파일을 나눈다. |
| D20 | 실행은 한 번에 하나(UI 세션의 RunSession). 진행 상황·로그는 SSE(`/api/stream`) 한 개로 보낸다. 브라우저 → 서버 명령은 POST. | 단방향 Push면 충분하다. WebSocket보다 단순하다. | 여러 사람이 동시에 쓰면 같은 실행을 본다(개인 로컬 도구). |
| D21 | Live Log: 도구 쪽 Ring(2000줄) + 카테고리(QA/Server/Actor/Network/Assertion) 필터. SSE는 초당 최대 200줄로 묶어 보내고 넘친 줄 수를 알린다. 브라우저는 최근 1000줄만 DOM에 둔다. | §143 Log Flood. | 넘친 줄은 Ring·리포트에서 본다. |
| D22 | Inspector: Actor 목록은 이름·연결·체력만 한 줄씩(100명 고려), 선택한 Actor만 상세(서버 QA `/qa/players/{id}` + Actor 상태). Match·Server 패널은 1초(설정 가능) 주기 조회, 실행 중이거나 일시정지일 때만. | §144, §148-149: 조회 빈도 제한. | — |
| D23 | 실행 모드: Run, Run From Step(앞 Step은 Skipped로 기록), Run Until Step(그 Step 뒤 Pause), Single Step, Pause/Resume, Stop, Breakpoint(Step의 `breakpoint` 또는 UI 토글, 파일에 저장하지 않아도 됨), Retry Failed Step(경고 표시: 게임 상태가 이미 바뀌었을 수 있음), Retry Scenario(같은 Seed). Run From Step은 서버를 새로 띄우므로 앞 Step의 Arrange가 없다는 경고를 보인다. | §54-63. | — |
| D24 | Debug Run 체크박스: Step 전후 서버 상태(`/qa/match`, `/qa/players`) Snapshot을 리포트에 저장. 기본 꺼짐. | §64. | 리포트가 커진다. |
| D25 | 저장: `QA/Scenarios` 아래 `.json`만, 경로 정규화 후 루트 밖이면 거부. 저장 전 Validation, 오류가 있으면 저장하지 않음(경고는 허용). | §52. Path Traversal 방지. | — |

## QA-4 / QA-5 Client 결정 (Unity, §83-90, §107)

| # | 결정 | 이유 | 틀렸을 때 비용 |
|---|---|---|---|
| D26 | `QaCommandReceiver`(Client/Assets/Scripts/Qa/)는 파일 전체를 `#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD`로 감싼다. 실행 인자 `-qaPort N`이 있을 때만 `System.Net.HttpListener`로 `http://127.0.0.1:N/`(Prefix는 localhost와 127.0.0.1 둘 다 시도)를 연다. Release 빌드에는 코드가 없다. | §86·§165. Unity에는 Kestrel이 없다. HttpListener는 Mono/IL2CPP 모두 있다. | Development Build를 따로 만들어야 한다. |
| D27 | HTTP 스레드는 Unity API를 부르지 않는다. 요청을 bounded 큐(32)에 넣고 메인 스레드 `Update`가 꺼내 처리한 뒤 응답한다(Timeout 5 s). 큐가 가득 차면 503. | Unity API는 메인 스레드 전용. | — |
| D28 | 명령: `GET /qa/status` → `{ok, devPlayerId, connected, joined, screen(현재 UI 화면 이름), alive, health, fps, frame}`; `POST /qa/screenshot {name}` → `QA/Reports/<runId>/` 같은 지정 폴더(`-qaShotDir` 인자, 없으면 persistentDataPath/qa-shots)에 `<name>.png` 저장, `{ok, path}`. name은 `[A-Za-z0-9_-]{1,64}`만. `POST /qa/ui {command: openMenu\|closeMenu\|openStats\|closeStats\|toggleDebug}`. Gameplay 입력 명령은 없다(§87). | §83-87. 경로 주입 방지. | — |
| D29 | 입력 녹화(QA-5, §107): `-qaRecord <path>`가 있으면 Client가 매 시뮬레이션 Step의 InputCommand를 JSON Lines로 쓴다(첫 줄 header: `{"type":"header","version":1,"simHz":..,"devPlayerId":..}`, 이후 `{"t":초,"moveX","moveY","yaw","buttons":숫자,"aimYaw","aimPitch"}`). 파일은 비동기 아닌 버퍼 쓰기(BufferedStream), 최대 30분(54,000줄)에서 멈춘다. QA Tool의 `convert-recording`이 이것을 `playInputs` Step이 있는 시나리오 초안으로 바꾼다. | §106-107: Scenario + Input Timeline 재현. 서버 Replay는 만들지 않는다. | 녹화는 입력만 담는다. 서버 상태 차이로 재생 결과가 다를 수 있다(문서화). |
| D30 | Manual Check(§88-90): Runner가 `manualCheck` Step에서 멈춘다. UI는 PASS/FAIL 버튼과 메모 입력, CLI는 대화형 터미널이면 `p/f` 입력, 비대화형이면 SKIPPED(`--manual fail`이면 FAIL). 결과는 리포트 "Manual Checks"에 기록. | §88-90, D16. | — |

## QA-5 결정 (§106-111, §133-136, §156)

| # | 결정 | 이유 | 틀렸을 때 비용 |
|---|---|---|---|
| D31 | Parameterized: 시나리오 최상위 `parameters: [ {..}, {..} ]`. 각 항목은 변수로 합쳐져(`${ping}`) 한 번씩 실행된다. 항목마다 RunId가 따로 생기고, 묶음 요약(파라미터별 PASS/FAIL)을 마지막에 출력한다. 최대 100개. | §108-109. 변수 치환을 이미 쓰고 있어 새 문법이 필요 없다. | — |
| D32 | Repeat / Seed Sweep: CLI `--repeat N`(1..1000), `--seed-sweep A..B`(최대 1000개). 둘 다 같은 시나리오를 차례로 실행하고(병렬 아님: 서버 성능 측정을 오염시키지 않는다), 요약에 실패한 Seed·반복 번호를 모은다. `--stop-on-fail`이면 첫 실패에서 멈춘다. UI에도 같은 입력을 둔다. | §110-111. 순차 실행이 재현성과 측정 정확도에 맞다. | 오래 걸린다(의도). |
| D33 | Baseline: 실행이 끝날 때마다 `QA/Reports/history/<scenario-file-key>.jsonl`에 한 줄(RunId, 시각, Seed, git commit, dirty, status, duration, 서버 지표 tickP50/P95/P99, workingSetMB, 선택한 저장 값)을 덧붙인다. 시나리오당 최근 50줄만 유지. 리포트에 직전 PASS 실행과 비교한 표(Previous / Current / Change %)를 넣고, `metrics` 단계에 Threshold가 없는 지표가 `baselineWarnPercent`(기본 50 %)보다 나빠지면 Warning(FAIL 아님). | §135-136. Threshold가 없으면 경고만 한다는 요청 그대로. | history는 로컬(gitignore). 공유는 나중. |
| D34 | Recording 재생: `convert-recording <file.jsonl> [--out scenario.json]`이 Unity의 `-qaRecord` 파일을 `playInputs` Step 하나가 있는 시나리오 초안으로 바꾼다(입력은 시나리오 옆 `<name>.inputs.jsonl`로 복사, 시나리오에는 경로만). `playInputs {actor, file, speed?}`는 Headless Actor가 Tick마다 기록된 InputCommand를 그대로 보낸다. 기록에 없는 Build 요청·UI는 재생하지 않는다(문서화). | §106-107: 서버 Replay 없이 Scenario + Input Timeline으로 재현. | 서버 상태가 다르면 재생 결과가 달라진다(문서화). |
| D35 | 성능 시나리오는 기존 Load Test를 재사용한다: `spawnActors`가 이미 봇 연결 코드를 쓴다. Docs/LoadTest.md의 측정 절차와 같은 서버 설정(DevRespawn 등)을 `server.options`로 고정한 `Stress/load_bots_<N>.json`(10/50)을 둔다. Threshold는 사용자 결정(§134)이므로 느슨한 값과 설명만 둔다. | §133, §161. | — |
