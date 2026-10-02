# QA Scenario Orchestrator

QA Tool은 서버를 띄우고 Headless Client(Actor)를 실제 게임 프로토콜로 움직인다. 서버의 QA Control API로 준비 상태를 만들고 서버의 권위 있는 상태를 검증한 뒤 Report를 남긴다. 시나리오는 JSON이고, 코드를 고치지 않고 작성 → 실행 → 실패 지점 기록 → 같은 Seed로 재실행할 수 있다.

- 요청: `Docs/requests/2026-10-02-qa-scenario-orchestrator-request.md`. § 번호는 이 요청서 기준이다.
- 설계: `Docs/specs/2026-10-02-qa-tool-design.md`. D 번호는 이 설계서의 결정이다.
- 현재 범위: QA-1(MVP), QA-2(Web UI, 아래 "Web UI"), QA-3(Fault Injection, 아래 "Fault Injection"), QA-4(Unity Client 자동화·Screenshot·Manual Check, 아래 "Unity Client Actor"와 "Manual Check"), QA-5(Parameter·Repeat·Seed Sweep·Baseline·Recording 재생·Load 시나리오, 아래 "QA-5").

## Architecture

```text
QA Tool (Server/src/ProjectH.QA, .NET 10 콘솔)
 └ QaCli: run / validate / list / ui / convert-recording
    └ BatchPlanner: 파라미터 세트 × (반복 | Seed) 만큼 순차 실행 → BatchSummary (QA-5)
    └ QaOrchestrator: 시나리오 실행 1회 (RunContext: runId, seed, 변수, 취소)
        ├ ScenarioLoader / ScenarioValidator: JSON → DTO (schemaVersion 1)
        ├ ActionRegistry: action 이름 → IScenarioActionHandler
        ├ LaunchedServer → ServerProcessManager: launch(재시작 가능) | attach. QaServerClient (HTTP)
        ├ Faults: NetworkFaultHub(Actor별 UdpFaultProxy), DbFaultHub(DockerDbController)
        ├ ActorManager → HeadlessActor (ProjectH.Bots 재사용). 테스트용 MockActor
        ├ AssertionEngine + Comparison: 경로 → 값 → 연산자
        ├ EventCursor: /qa/events 폴링 (필요할 때만)
        ├ ReportWriter: report.json + report.html
        └ BaselineHistory: QA/Reports/history/<key>.jsonl + Baseline 비교 (QA-5)

Game Server (ProjectH.Server, QA 모드)
 ├ UDP: 게임 프로토콜 (Actor = 실제 Client와 같은 경로)
 └ HTTP 127.0.0.1: QA Control (ProjectH.Server/Qa/)
```

- **QA Tool은 Client다**(`game-core-rules` §3).
  - `ProjectH.Bots`와 `ProjectH.Shared`, HTTP만 쓰고 `ProjectH.Server`를 참조하지 않는다.
  - Actor는 봇의 `BotConnection`·`BotView`·`BotAim`·`BotSteering`을 그대로 쓴다(D8).
- **Thread Model**(D9):
  - Runner는 Orchestrator의 async 흐름 하나에서 Step을 차례로 실행한다.
  - 모든 Actor는 Actor Pump Thread 하나에서 돈다. 30 Hz이고, 접속한 뒤에는 서버 SimHz를 따른다.
  - 명령은 Bounded Channel(1024)로 Pump에 들어간다.
  - 상태는 Tick마다 새로 만든 불변 `ActorState`로 나온다(Volatile).
  - Lock은 두 개뿐이고 서로 겹치지 않는다: 서버 로그 Ring, 일시정지 Gate.
- **서버 쪽**(D2, D5):
  - HTTP 스레드는 `Match`를 만지지 않는다.
  - 명령과 조회는 Channel(64)에 들어가고, Game Loop가 Tick 끝에 최대 16개를 실행한다.
  - QA 이벤트는 Tick마다 플레이어 상태를 비교해서 만든다(D6). `Match`에 QA 분기나 Hook은 없다.

## QA Mode (서버)

**켜는 방법**(D3): 다음 셋 중 하나가 있고, Host 환경이 Production이 아닐 때만 켜진다.
- 인자 `--qa-mode`. Host Builder에 넘기기 전에 뺀다.
- 환경 변수 `QA_MODE=true`.
- 설정 `Qa:Enabled=true`.

`dotnet ProjectH.Server.dll`의 기본 환경은 Production이다. 그래서 개발 서버에서 직접 켤 때는 `DOTNET_ENVIRONMENT=Development`가 필요하다. Production에서 켜려 하면 Warning만 남기고 끈다. 이때 Port도 열지 않는다.

```bash
DOTNET_ENVIRONMENT=Development dotnet Server/src/ProjectH.Server/bin/Debug/net10.0/ProjectH.Server.dll --qa-mode
```

| 설정 | 기본값 | 의미 |
|---|---|---|
| `Qa:Port` | 7780 | QA HTTP Port. 0이면 빈 Port를 쓰고, 실제 값을 stdout `QA_READY gamePort=<n> qaPort=<n>`과 `/qa/health`로 알린다 |
| `Qa:AllowRemote` | false | true일 때만 모든 Interface에 Bind한다(Warning). 기본은 127.0.0.1 |
| `Qa:Events` | true | false면 Tick diff와 이벤트를 끈다(벤치마크용, §150) |
| `Qa:CommandTimeoutMs` | 2000 | HTTP 요청이 Game Loop 실행을 기다리는 시간. 넘으면 504 |

**실행 환경**: 서버는 이제 ASP.NET Core 10 Runtime(`Microsoft.AspNetCore.App`)이 있어야 실행된다. `ProjectH.Server.csproj`의 `FrameworkReference`는 QA 모드와 상관없이 항상 걸린다. NuGet 패키지가 아니고 .NET SDK에 들어 있다. 서버만 배포하는 머신에는 `dotnet-runtime`이 아니라 `aspnetcore-runtime`을 설치한다.

**QA가 꺼져 있을 때**: QA 서비스 등록도 TCP Listener도 없다. Tick 비용은 null 확인 하나뿐이다.

QA Control API의 전체 명세(경로, 인자 범위, 상태 코드, DTO, 이벤트)는 설계서 "QA Control API" 절과 서버 코드 `Server/src/ProjectH.Server/Qa/`를 본다. 요약은 아래와 같다.

| Method | Path | 내용 |
|---|---|---|
| GET | `/qa/health` | ok, qaMode, gamePort, qaPort, simHz, serverTick, matchState, version, seeds, options |
| POST | `/qa/command` | `{runId?, command, player?, args{}}` → `{ok, error?, result}` |
| GET | `/qa/players`, `/qa/players/{devPlayerId}` | Player DTO. 없으면 404 |
| GET | `/qa/match` | Match DTO(state, alive, winner, zone{phase, center, radius…}, buildPieces, worldItems…) |
| GET | `/qa/build?x=&z=&radius=&max=` | `{count, pieces[]}` |
| GET | `/qa/metrics?windowSeconds=` | tick p50/p95/p99/max, workingSetMB, gc, activeSessions, health 카운터 |
| GET | `/qa/events?after=&max=` | `{next, dropped, events[{seq,tick,utc,type,player,data}]}` |
| POST | `/qa/server/stop` | 정상 종료 |

- 오류 형식은 `{ok:false, error}`이고, 상태 코드는 다음과 같다.
  - 400: 인자
  - 404: 플레이어나 조각 없음
  - 409: 상태가 맞지 않음. 예: 죽은 플레이어, setPosition이 박스와 겹침
  - 413: 16 KB 초과
  - 503: 큐 가득
  - 504: 실행되지 않음. 다시 해도 안전하다.
- 이벤트 종류: PlayerJoined/Resumed/Graced/Left/Damaged/Healed/Killed/Respawned, InventoryChanged, MatchStateChanged, ZoneChanged, BuildPlaced/Destroyed, MatchReset.
  - 명령의 효과는 다음 Tick의 이벤트로 나온다.
  - `ItemPickedUp`은 없다. InventoryChanged로 대신한다.

## Security

- QA Control은 Game Protocol(LiteNetLib)과 완전히 분리되어 있다. 일반 Client가 보내는 패킷으로는 QA 명령을 실행할 수 없다(§6).
- QA 모드는 명시적으로 켰을 때만 동작하고, Production 환경에서는 거부한다(D3). 꺼져 있으면 Port가 없다.
- Bind는 코드의 `IPAddress.Loopback` 하나로 고정이다. `ASPNETCORE_URLS`, `--urls`, `Kestrel:Endpoints`는 Empty Builder라서 끼어들 수 없다. 외부 Bind는 `Qa:AllowRemote=true`일 때만 한다(D4).
- 인증은 없다. Loopback 전용이라는 것이 보호 수단이다. `AllowRemote`는 신뢰하는 네트워크에서만 쓴다.
- 요청 본문은 16 KB로 제한하고, 큐는 64로 제한한다. 모든 인자는 범위를 검사한다(NaN 거부).

## Scenario Format (schemaVersion 1)

파일 위치는 `QA/Scenarios/<Category>/*.json`이다. 주석(`//`)과 끝 쉼표를 허용하고, 파일은 최대 1 MB, Step은 최대 2000개다.

```json
{
  "schemaVersion": 1,
  "name": "Basic Hit And Reconnect",
  "description": "...",
  "tags": ["combat", "smoke"],
  "seed": 12345,
  "timeoutSeconds": 90,
  "server": { "mode": "launch", "options": { "Server:MinPlayers": "2" } },
  "variables": { "minDamage": 1 },
  "actors": [ { "id": "playerA", "type": "HeadlessClient" } ],
  "steps": [
    { "id": "a_connect", "action": "connect", "actor": "playerA" },
    { "action": "waitFor", "condition": "match.state", "equals": "Playing", "timeoutMilliseconds": 15000 },
    { "phase": "arrange", "action": "setPosition", "actor": "playerA", "position": "QA_Combat_A" },
    { "phase": "assert", "assert": "player.health", "actor": "playerA", "lessThan": 100 }
  ]
}
```

**최상위 필드**

| 필드 | 기본값 | 의미 |
|---|---|---|
| `schemaVersion` | (필수) | 1 |
| `name`, `description`, `tags` | 파일 이름, "", [] | 태그 예: smoke, combat, reconnect, building, zone, stress, slow |
| `seed` | 실행마다 무작위 | 서버 Loot·Zone·Spawn Seed에 들어가고 Report에 기록된다. `--seed`가 우선한다 |
| `timeoutSeconds` | 120 | 시나리오 전체 Timeout(§112) |
| `server.mode` | launch | `launch`(Tool이 서버를 띄움) 또는 `attach`(`qaUrl` 필요, `host`·`gamePort`는 생략 가능) |
| `server.options` | {} | 서버 설정 덮어쓰기(`--Key=Value`). Tool 기본값보다 나중에 적용된다 |
| `variables` | {} | `${name}`으로 쓰는 값 |
| `actors` | [] | `{id, type}`. type은 `HeadlessClient`만 지원한다 |
| `parameters` | 없음 | QA-5 D31: 객체 배열(최대 100). 항목마다 `variables` 위에 합쳐 한 번씩 실행한다. 아래 "QA-5" |
| `baseline` | 없음 | QA-5 D33: `{ "values": ["저장한 변수", ...] }`(최대 50). 실행 기록에 남기고 비교한다 |
| `baselineWarnPercent` | 50 | QA-5 D33: Threshold가 없는 지표가 이 비율보다 나빠지면 Warning(실패 아님) |
| `parameters` | 없음 | QA-5 D31: 객체 배열(최대 100). 항목마다 `variables` 위에 합쳐 한 번씩 실행한다. 아래 "QA-5" |
| `baseline` | 없음 | QA-5 D33: `{ "values": ["저장한 변수", ...] }`(최대 50). 실행 기록에 남기고 비교한다 |
| `baselineWarnPercent` | 50 | QA-5 D33: Threshold가 없는 지표가 이 비율보다 나빠지면 Warning(실패 아님) |

**Step 공통 필드**

| 필드 | 의미 |
|---|---|
| `id` | Report에 나오는 Step 이름. 없으면 `NN-action`이다. 중복은 오류 |
| `action` | Action 이름. 축약형 `{ "assert": "path", ... }`는 `action: assert, path`와 같다 |
| `actor` | Actor id. 서버는 `qa-<id>`라는 DevPlayerId로 안다(D7) |
| `phase` | arrange / act / assert. 적은 Step부터 다음 phase가 나올 때까지 유지된다 |
| `timeoutMilliseconds` | Step Timeout. 기본 10 s이고 Action마다 다르다(아래) |
| `continueOnFailure` | true면 실패해도 다음 Step으로 간다. 시나리오 결과는 실패다 |
| `breakpoint` | UI 실행에서 이 Step 앞에서 멈춘다. CLI는 로그만 남기고 멈추지 않는다 |
| `saveAs` | 결과 값을 변수로 저장한다(Action마다 저장하는 값이 다르다) |
| `description` | Report와 콘솔에 Step 제목으로 나온다 |

**변수**
- `${name}`, `${name.field}` 형태로 쓴다.
  - 쓸 수 있는 이름: 시나리오 `variables`, 내장 `runId`·`seed`, 앞 Step의 `saveAs`.
  - 값 전체가 `"${x}"`이면 JSON 타입이 그대로 유지된다. 그래서 `"lessThan": "${hpBefore}"`는 숫자로 비교한다.
  - 긴 문자열 안의 `${x}`는 문자열로 들어간다.
- **위치 값**은 이름 또는 `{x, y?, z, yaw?}`다.
  - 이름은 먼저 QA Marker(`QA/Markers.json`), 다음 맵 POI 이름(`Crossroads`, `Rustvale`…) 순서로 찾는다.
  - `y`가 없으면 지형 높이를 쓴다.
  - 저장한 값도 위치로 쓸 수 있다. 예: `"position": "${zc}"`. 이때 `zc`는 `match.zone.center`를 저장한 값이다.

**Arrange / Act / Assert**(§23, D11)
- 서버 QA 명령은 준비용이다.
- Act·Assert 단계에서 쓰면 Validation 경고가 난다. `damagePlayer`·`killPlayer`는 arrange가 아니면 항상 경고다.
- 검증 대상 동작(사격, 이동, 줍기, 사용)은 Actor의 실제 입력으로 한다.

## Actions

**공통 규칙**
- 기본 Step Timeout은 10 s다.
- 기다리는 Action은 자기 Timeout에 Expected/Actual을 남기고 실패한다. Runner는 Timeout + 2 s에 강제로 취소한다.
- Actor 명령은 Actor가 적용한 뒤의 상태로만 판정한다(command id). 그래서 앞 Step의 상태로 통과하지 않는다.
- 장애 주입 Action(`networkFault`, `dropConnection`, `sendInvalidPackets`, `stopServer`, `startDb` 등)은 아래 "Fault Injection"에 있다.

**연결**

| Action | 인자 | 동작 |
|---|---|---|
| `connect` | actor | Join과 첫 Snapshot까지 기다린다. 연결이 닫히면 바로 실패하고 이유를 남긴다 |
| `disconnect` | actor, `graceful?`(true) | false면 Disconnect 패킷 없이 소켓을 닫는다. 서버는 자기 Timeout(5 s)으로 알아챈다 |
| `reconnect` | actor (기본 Timeout 20 s) | 같은 DevPlayerId로 새 연결을 연다. 먼저 서버가 옛 연결을 `connected`로 보지 않을 때까지 기다린다. 옛 Peer가 살아 있을 때 접속하면 Resume이 아니라 새 플레이어가 된다 |
| `connectAll` / `disconnectAll` | `prefix?`, `graceful?` | 한꺼번에 보내고 한 번 기다린다. connectAll의 기본 Timeout은 30 s |
| `spawnActors` | `count`(1–100), `prefix`, `type?` | `prefix-001`…을 만든다. 아직 접속하지 않은 상태다 |

**이동·조준·입력** (모두 실제 입력이고, 서버가 시뮬레이션하고 검증한다)

| Action | 인자 | 동작 |
|---|---|---|
| `moveTo` | actor, `position`, `tolerance?`(0.75 m), `sprint?` (기본 Timeout 30 s) | 봇 조향으로 걷는다. 막히면 점프하고, 계속 막히면 옆으로 돌아간다. 2 m 안에서는 느려진다. 진전이 없으면 "stuck"으로 실패한다 |
| `moveVector` | actor, `x?`, `y?`(-1..1), `milliseconds?`(1–60000) | 몸 기준 이동 입력이다. 시간이 없으면 바뀔 때까지 유지하고, (0,0)이면 멈춘다 |
| `look` | actor, `yaw`, `pitch?`(-90..90, 양수 = 아래) | 고정 방향 |
| `aim` | actor, `target`(Actor) \| `at`(위치) \| `clear: true` | `target`은 사수의 최신 Snapshot에 있는 그 Entity의 가슴을 눈 높이에서 조준한다. 보일 때까지 최대 3 s 기다린다. `at`에 y가 없으면 그 지점 지면 + 1.2 m를 조준한다 |
| `fire` | actor, `count?`(1–500, 기본 1) \| `holdMilliseconds?`, `slot?`(0–2), `target?` | 무기가 오고(UDP) 무기 Tool이 될 때까지 최대 3 s 기다린다. 필요하면 슬롯 키를 누른다. 한 번 누름 = Fire 1 Tick 후, 무기 FireIntervalTicks만큼 뗀다. 반자동 Edge를 만들고 Cooldown 때문에 누름을 잃지 않게 하려는 것이다. 다 누른 뒤 HitConfirmed 수가 150 ms 동안 그대로일 때까지(최대 500 ms) 기다린 다음 `hits`를 읽는다. saveAs = `{presses, hits, ammoBefore, ammoAfter, weapon}` |
| `build` | actor, `piece`(wall/floor/ramp/roof), `material?`(wood), `cellX`,`level`,`cellZ` 또는 `position`, `rotation?`(0–3), `count?`(1–8), `dx?`,`dz?`(셀 간격), `expect?`(Ok, 또는 `any`나 다른 코드) | 실제 건설 입력이다(BotBuilder와 같은 순서).<br>1. Snapshot이 Build Tool을 보일 때까지 Q(`ToolBuild`)를 누른다(0.5 s 간격).<br>2. 조각마다 2 Tick 동안 조각 중심을 조준하고, 다음 Tick에 Input 뒤에 BuildRequest를 보낸다. 3 Tick = 서버 minimumBuildInterval 0.1 s.<br>3. 마지막 조각 뒤에도 10 Tick 동안 조준을 유지한다.<br>모든 BuildResult를 받으면 끝난다. saveAs = `{sent, accepted, pieceId, pieceIds[], codes[]}`. 끝난 뒤에도 Build Tool에 그대로 있다. `fire`는 슬롯 키로 무기를 다시 꺼낸다 |
| `pauseInput` / `resumeInput` | actor | 연결은 유지하고 Input만 멈춘다. 다시 시작하면 원래대로 보낸다. 서버의 InputTimeout을 확인할 때 쓴다(§75) |
| `playInputs` | actor, `file`(시나리오 옆 `.jsonl`, QA/ 밖 금지), `speed?`(0.25–4, 기본 1) (기본 Timeout 60 s) | QA-5 D34: Unity 녹화의 InputCommand를 Tick마다 하나씩 보낸다. 아래 "QA-5" |
| `moveVectorAll` | `prefix?`, `x?`(0), `y?`(1), `spread?`(true) | QA-5 D35: prefix로 시작하는 모든 Headless Actor가 이 이동 입력을 계속 보낸다. spread면 각자 다른 방향(360°/N)을 본다. Load 시나리오용 |
| `stopFire` | actor | Fire를 떼고, 대기 중인 사격을 버린다 |
| `press` | actor, `button`(`Jump`, `Interact`, `Drop`, `UseMedkit`, `UseShieldCell`, `Reload`, `Slot1`…, `A+B` 조합), `count?`, `hold?` | 누르고 뗀다(Edge). `hold: true`면 계속 누른다 |
| `release` | actor, `button` | 누르고 있던 버튼을 뗀다 |
| `switchWeapon` | actor, `slot`(0–2) | 슬롯 키를 누르고 바뀔 때까지 기다린다. 슬롯은 0부터이고 `player.currentSlot`과 같다 |
| `jump` | actor | 한 번 누른다 |
| `sprint` / `crouch` | actor, `held?`(true) | 누르기·떼기 |

`fire`·`press`가 Timeout이나 취소로 끝나면 남은 누름을 버린다. 그래서 다음 Step으로 새지 않는다.

**대기·검증**

| Action | 인자 | 동작 |
|---|---|---|
| `wait` | `milliseconds` \| `seconds` (최대 600 s) | 단순 대기. 가능하면 `waitFor`를 쓴다(§27) |
| `waitFor` | `condition`(또는 `path`), 연산자 하나, `timeoutMilliseconds` | `assert`와 같은 엔진으로 `--poll-ms`(100 ms)마다 확인한다. 실패하면 마지막 값을 남긴다 |
| `assert` | `path`, 연산자 하나 (+`tolerance`, build용 `pieceId`/`at`/`radius`, server용 `windowSeconds`) | 한 번 확인한다. saveAs = 실제 값 |
| `save` | `path`, `saveAs` | 값을 변수에 저장한다 |
| `waitForEvent` | `event`, `actor?`(그 플레이어만), `where?`({data 필드: 값}) | 아직 쓰지 않은 이벤트 중 첫 일치를 찾는다. 그것까지를 사용한 것으로 표시하므로, 연속 대기는 순서대로 읽는다. saveAs = 이벤트 JSON |

**서버 QA 명령** (Arrange용. `/qa/command`로 보낸다)
- 인자는 `${}` 치환 뒤 그대로 `args`로 간다.
- `position`(이름 또는 {x,y?,z})은 x, (y), z로 바뀐다. setPosition에서는 Marker의 yaw도 들어간다.
- `player`는 `qa-<actor>`다.
- 503·504는 2번까지 다시 보낸다.
- saveAs에는 서버 응답의 `result`가 들어간다.

| Action | 인자 |
|---|---|
| `mark` | `text`. 서버 로그에 runId와 함께 남는다(Tool이 시작·끝에 자동으로도 남긴다) |
| `setPosition` | actor, `position` 또는 `x`,`z`,`y?`,`yaw?`. 서 있을 몸이 박스나 문과 겹치면 409 |
| `setHealth` / `setShield` | actor, `value`(1–100 / 0–100) |
| `giveWeapon` | actor, `weapon`(1–3 또는 "Vesper AR"·"Kestrel LR"·"Wisp SMG"), `rarity?`, `slot?`(0–2), `select?` |
| `giveAmmo` | actor, `type`(light/medium/heavy), `amount` |
| `giveItem` | actor, `item`(medkit/shieldCell), `count` |
| `giveResource` | actor, `material`(wood/stone/metal), `amount` |
| `damagePlayer` | actor, `amount` (Arrange 전용 경고) |
| `killPlayer` | actor (Arrange 전용 경고) |
| `forceMatchState` | `state`: start / finish |
| `setZone` | `zonePhase: n`(서버 `args.phase`로 보낸다. Step의 `phase`는 arrange/act/assert이기 때문이다) 또는 `advance: true` |
| `spawnLoot` | `kind`, `position` 또는 `x`,`z`, `id?`, `rarity?`, `amount?` → `result.itemId` |
| `spawnBuildPiece` | `piece`, `material`, `cellX`,`level`,`cellZ` 또는 `position`, `rotation?` → `result.pieceId` |
| `damageBuild` | `pieceId`, `amount` → `{destroyed, health, standing}` |

## Assertions

| 경로 | 출처 | 예 |
|---|---|---|
| `player.<필드…>` | `GET /qa/players/qa-<actor>` | `player.health`, `player.position.x`, `player.weapon.name`, `player.ammo.medium`, `player.weapons.0.name`, `player.graced`, `player.placement` |
| `player.exists` | 위. 404면 false | |
| `player.state` | 위에서 계산 | Alive / Dead / Graced / Disconnected |
| `match.<필드…>` | `GET /qa/match` | `match.state`, `match.alive`, `match.winner`, `match.zone.phase`, `match.zone.center` |
| `match.playerCount`·`aliveCount`·`zonePhase`·`playing` | 별칭 | `playing` = Playing 또는 FinalPhase |
| `build.count`, `build.<필드>` | `GET /qa/build`(`at`·`radius` 선택) | |
| `build.exists`, `build.piece.<필드>` | 위 + `pieceId` | `build.piece.health` |
| `server.running` | `/qa/health` ok. 응답이 없으면 false | |
| `server.tickP50Ms`·`tickP95Ms`·`tickP99Ms`·`tickMaxMs`·`memoryMB`·`activeSessions` | `/qa/metrics`(`windowSeconds` 선택) | |
| `server.health.<…>`, `server.metrics.<…>` | 원본 | `server.metrics.health.tickFailures` |
| `network.connected`·`joined`·`disconnected`·`rtt`·`packetsIn`·`bytesIn`·`inputsSent`·`disconnectReason`·`disconnectCode` | Actor 연결 | |
| `actor.<ActorState 필드>` | Actor가 받은 것(Client 시점) | `actor.hitsLanded`, `actor.weaponName`, `actor.position.x` |
| `event.<Type>` | 이번 실행에서 받은 이벤트 수(최근 200개, `actor`가 있으면 그 플레이어만) | |
| `var.<name…>` | 변수 | `var.shots.hits` |
| `network.proxy.<…>` | Actor의 Fault Proxy(QA-3). 지금 연결 시도의 카운터와 설정 | `network.proxy.dropped`, `network.proxy.delayed`, `network.proxy.attempts`, `network.proxy.toServer.latencyMs`, `network.proxy.toClient.blocked`. 손실·중복 설정 경로는 `toServer.packetLossPercent`·`duplicatePercent`다(Step 인자는 `lossPercent`). Proxy가 없으면 `enabled` = false |

- 게임 결과는 `player.*`·`match.*`·`build.*`로 검증한다. 이것은 서버의 권위 있는 상태다(§165). `actor.*`·`network.*`는 Client 쪽 사실(받은 HitConfirmed, RTT 등)이다.
- **연산자**: `equals`, `notEquals`, `greaterThan`, `lessThan`, `between: [a, b]`(양 끝 포함), `exists: true`, `notExists: true`, `approximately` + `tolerance`(기본 0.01), `contains`(부분 문자열 또는 배열 원소). Step에는 하나만 쓴다.
- **비교 규칙**: 숫자는 숫자로 비교한다(숫자 문자열 포함). bool은 bool로 비교한다. 문자열은 대소문자를 무시한다. 숫자 enum 값은 이름으로 바꿔서 비교한다(match.state, player.mode, player.tool).

## Actors

- **HeadlessClient**(`HeadlessActor`)는 봇과 같은 UDP Client다.
  - 의도(이동 목표·벡터, 조준, 누른 버튼, 시간 입력 큐)를 갖는다.
  - Pump Tick마다 InputCommand를 만들어 보낸다. 가만히 있어도 보낸다(서버 InputTimeout).
  - ViewTick은 마지막 Snapshot Tick이다(BotBrain과 같다).
- **DevPlayerId**는 `qa-<alias>`다. 32바이트를 넘거나 이름 규칙(`ProtocolConstants.IsValidPlayerName`)에 어긋나면 Validation 오류다.
- **최대 100명**(Snapshot 한도). 서버 `Server:MaxPlayers`(기본 16)도 함께 올려야 한다.
- 실행이 끝나면 Pump가 모든 연결을 정상 종료한다.
- `MockActor`(테스트 프로젝트)는 Runner 단위 테스트용이다(§138).
- **UnityClient**(`UnityActor`, QA-4)는 실제 Unity Development Player다. 아래 "Unity Client Actor"를 본다.
- **`"network": { "proxy": true }`**(QA-3): 그 Actor는 자기 UDP Fault Proxy를 거쳐 접속한다(`{ "id": "playerB", "network": { "proxy": true } }`). Network Fault Step(`networkFault` 등)은 이 Actor에만 쓸 수 있다(Validation 오류). 장애를 걸기 전에는 지연·손실이 없다. 다른 키나 bool이 아닌 값은 Validation 오류다.

## Unity Client Actor (QA-4)

`{ "id": "viewer", "type": "UnityClient", "unity": { "exe"?, "attachPort"?, "width"?, "height"? } }`

- **실행(launch)**: `connect`가 Development Player를 띄운다.
  - Player 경로는 `unity.exe`(저장소 루트 기준), 없으면 CLI `--unity-exe`다.
  - 인자: `-host 127.0.0.1 -port <gamePort> -devId qa-<alias> -autoConnect -qaPort <빈 Port> -qaShotDir QA/Reports/<runId>/screenshots -logFile QA/Reports/<runId>/unity-<alias>.log -screen-fullscreen 0 -screen-width 800 -screen-height 450`
  - `-batchmode`는 쓰지 않는다. Screenshot에 렌더링이 필요하기 때문이다.
  - 준비 조건은 Player의 `GET /qa/status`가 응답하는 것이다(최대 60 s). 고정 대기는 없다.
  - 그 뒤 일반 `connect`처럼 join까지 기다린다. Unity Actor의 `connect` 기본 Timeout은 90 s다.
  - Player의 stdout은 버린다. 로그는 `-logFile`에 남는다.
- **Player가 없을 때**: Player가 지정되지 않았거나 파일이 없으면 `connect`가 나머지 Step을 **SKIPPED**로 만든다. 실행 결과는 Skipped, 종료 코드는 0이고 `--fail-on-skip`이면 1이다. Unity 빌드가 없는 CI는 실패하지 않는다.
- **연결(attach)**: `unity.attachPort`는 `PROJECTH_QA_PORT`(Editor)나 `-qaPort`로 이미 떠 있는 Player에 붙는다.
  - 아무것도 띄우거나 닫지 않는다.
  - 그 Player는 스스로 이번 실행의 서버에 접속해 있어야 한다. 보통 `server.mode: attach`와 같이 쓴다.
- **상태**: Player의 `/qa/status`를 250 ms마다 읽는다(별도 스레드 없는 async Loop).
  - `actor.unity.<필드>`(screen, joined, connected, statsOpen, debugVisible, alive, health, fps, frame)와 `network.connected`·`actor.status`가 이것을 따른다.
  - 게임 판정은 여전히 서버 상태다(`player.*`).
  - screen 값: Title, Connecting, InGame, Menu, Disconnected, Result.
- **입력 없음**(§87): 이동·사격·건설·입력 정지 같은 Gameplay Action은 Unity Actor에 쓰면 Validation 오류다. 그런 동작은 Headless Actor로 한다. Unity Actor에는 UI 명령과 Screenshot만 보낸다.
- **종료**: Cleanup에서 띄운 Player의 창을 닫고, 3 s 안에 안 끝나면 그 자식 프로세스 트리만 Kill한다. Report Cleanup에 `unity <alias>` 줄이 남는다.
  - **QA Tool이 강제 종료되어도 Player는 남지 않는다(Windows).** 띄운 Player는 Kill-on-close Job Object에 들어가 있다. 도구 프로세스가 어떻게 끝나든 Windows가 Job 핸들을 닫으면서 Player를 끝낸다. 서버는 Job에 넣지 않는다(서버는 `Qa:ParentPid` 감시로 스스로 정상 종료한다).
  - Job에 넣지 못하면 로그에 경고를 남기고 계속한다. Windows가 아닌 환경도 같다. 그때는 정상 종료(Ctrl+C, UI Stop·종료)의 Cleanup만 Player를 닫는다.
- **Action**

| Action | 인자 | 동작 |
|---|---|---|
| `captureScreenshot` | actor, `name`(`[A-Za-z0-9_-]{1,64}`) | `screenshots/<Step 번호>_<alias>_<name>.png`(64자 이내).<br>Step 번호가 있어서, 영문이 아닌 Alias가 같은 문자로 바뀌거나 잘려도 파일이 겹치지 않는다. 다시 실행한 Step은 `_2`가 붙는다.<br>Report에는 썸네일과 링크로 나온다(상대 경로). UI에서 연 Report도 `/reports/<runId>/screenshots/<file>.png`로 이미지를 보인다. 실행당 최대 200장 |
| `uiCommand` | actor, `command`(openMenu, closeMenu, openStats, closeStats, toggleDebug) | 지금 화면에 맞지 않으면(409) 실패한다. 화면 이름이 메시지에 나온다 |
| `waitForUnity` | actor, `condition`(status 필드, 예: `joined`, `screen`, `unity.statsOpen`), 연산자 하나, `timeoutMilliseconds` | Player 상태를 직접 읽으며 기다린다 |

- **확인한 것**(2026-10-02, Development Build): `Smoke/unity_client.json`과 `UI/kill_feed.json`이 PASS했고 Player와 서버가 남지 않았다.
  - Player 시작부터 join까지 5~13 s 걸렸다.
  - **PC 화면이 잠겨 있으면(LogonUI) PNG는 생기지만 전부 회색이다.** 실제 화면은 잠기지 않은 데스크톱에서 확인한다.
  - 자동 이미지 판정은 하지 않는다(§85).

## Manual Check (QA-4, D30)

`{ "id": "ime_compose", "action": "manualCheck", "description": "확인할 내용" }`. 확인할 내용은 Step의 `description` 필드에 쓴다. `actor`는 선택이다.

- **Web UI**: 실행이 멈추고 PASS / FAIL 버튼과 메모 칸이 나온다. 기다리는 동안 시나리오 Timeout은 멈춘다. FAIL은 다른 실패 Step처럼 보류되고(Retry가 다시 묻는다), Resume이나 Stop은 실패로 끝낸다.
- **CLI, 대화형 터미널**: `p`(PASS) / `f`(FAIL) / `s`(건너뜀)와 메모를 입력한다.
- **CLI, 비대화형(CI, 입력 리디렉션)**: 그 Step만 **SKIPPED**이고 실행은 계속된다.
  - `--manual fail`이면 FAIL이다.
  - `--manual skip`이면 대화형이어도 묻지 않는다.
- Report "Manual checks" 표에 Step, 내용, 결과(PASS/FAIL/SKIPPED), 누가(ui/cli/auto), 메모가 남는다.
- 예: `QA/Scenarios/Manual/ime_name.json`(한글 IME 이름 입력, §88). 태그는 `manual`이다.

## QA Markers

`QA/Markers.json`은 QA Tool만 읽는다. 게임 빌드에는 없다. 좌표를 고른 근거는 파일 머리 주석에 있다.

- 평지 영역: `|x|<30, |z|<26`. 언덕이 모두 이 밖에서 끝나서 높이가 0이다.
- 마커 목록

| Marker | 좌표 | 용도 |
|---|---|---|
| `QA_Combat_A` / `QA_Combat_B` | (-5,0,0) / (5,0,0) | 10 m 사격 쌍 |
| `QA_Combat_20m_A` / `B` | (∓10,0,-6) | 20 m 사격 쌍 |
| `QA_Move_Start` / `QA_Move_End` | (-10,0,6) / (10,0,6) | 20 m 직선 이동 |
| `QA_Spawn_A` / `QA_Spawn_B` | (0,0,±4) | |
| `QA_Build_Test` | (12.5,0,-12.5) | 건설 셀 (18,13)의 중심 |
| `QA_Far_SW` | (-68,~,-70) | 남서쪽 구석의 투입 지점 |

`MarkersTests`가 다음을 검사한다.
- 쌍마다 거리, 평지인지, 박스·문·채집물에 대한 시선(0.3/1.2/1.6 m 높이).
- 모든 마커가 벽 안에 있고 박스에서 1 m 이상 떨어져 있는지.

## Scenario Library

모두 실제 서버에서 실행해 통과를 확인했다(2026-10-02). 시간은 실행 1회 기준이다.

| 파일 | § | 내용 | 시간 |
|---|---|---|---|
| `Smoke/connect.json` | 115–116 | 서버 기동 → 접속 → 서버에 보임 → 정상 퇴장 | ~1.5 s |
| `Movement/move_to.json` | 117 | 실제 입력으로 20 m 이동 → 서버 위치 ≈ 목표. 이동 이상 0 | ~6 s |
| `Combat/basic_hit.json` | 118, 152 | B가 A를 실제 사격 → A 체력 감소(B HitConfirmed) → A 끊김 → Grace → 재접속 → 같은 Entity·체력 | ~8.5 s |
| `Combat/elimination.json` | 118, 124 | B가 A를 사격으로 제거 → PlayerKilled(killer B), placement 2, B kills 1 → Finished, winner B | ~4 s |
| `Reconnect/reconnect.json` | 74, 125 | Grace 중 재접속 → 같은 Entity, 체력, 실드, 무기, 탄약, Medkit | ~6 s |
| `Reconnect/reconnect_expired.json` | 126 | Grace(2 s)가 지나면 PlayerLeft. 다시 오면 새 Entity로 관전(죽음, 참가자 아님). 경기는 계속된다 | ~5 s |
| `Loot/pickup_drop.json` | 119–120 | spawnLoot → 실제 Interact로 줍기(InventoryChanged) → 실제 Drop으로 무기 버리기 | ~3 s |
| `Healing/medkit.json` | 121 | damagePlayer(arrange) → 실제 UseMedkit → 체력 +50 | ~6 s |
| `Shield/shield_cell.json` | 122 | 실제 UseShieldCell → 실드 +25. 다음 피해는 실드가 먼저 받는다 | ~5 s |
| `Zone/zone_damage.json` | 123 | Zone을 phase 3으로 → 밖에서 체력 감소 → 중심(`match.zone.center`)에서 멈춤 | ~10 s |
| `Building/basic_wall.json` | 130 | 실제 건설 입력으로 나무 벽(비용 지불) → 실제 사격으로 체력 감소 → 파괴(BuildDestroyed) | ~8 s |
| `Building/turbo_build.json` | 132 | 벽 3개를 연속 요청 → 3개 모두 Ok, 조각 수 3, 나무 정확히 30 감소 → 같은 줄을 다시 요청하면 Occupied이고 비용 없음 | ~3.5 s |
| `Building/support_collapse.json` | 131 | 벽 하나 위 바닥 3개 → 벽 파괴 → 바닥 3개 붕괴(collapsed 3) | ~3 s |
| `Network/input_timeout.json` | 75 | pauseInput → 연결은 유지된 채 7 s 뒤 서버가 InputTimeout으로 닫음 → Grace 없이 PlayerLeft | ~10 s |
| `Smoke/unity_client.json` | 83–87 | Unity Player(`--unity-exe`)가 join → HUD·Menu·Stats Screenshot, UI 명령 | ~8 s |
| `UI/kill_feed.json` | 84 | Headless B가 A를 실제 사격으로 제거하는 동안 Unity Player가 보고, Kill Feed Screenshot | ~18 s |
| `Manual/ime_name.json` | 88–90 | 한글 IME 이름 입력 Manual Check 2개(CI에서는 SKIPPED) | 사람 |
| `Stress/bots_50.json` | 133–134 | 50 Client가 60 s 경기 → tick p95 < 5 ms(느슨한 기준, 사용자가 조정). 측정값 0.15 ms | ~62 s |
| `Network/latency_loss_combat.json` | 73 | B가 Proxy로 양방향 200 ms 지연 + 10% 손실(RTT 측정 ≈330 ms) → 정지한 A를 4발 사격 → 4발 모두 명중(서버 피해). B는 끊기지 않고 재접속도 없음(connections 1, graceStarts 0). 장애를 지우면 RTT가 돌아온다 | ~11 s |
| `Combat/lag_compensation.json` | 119 | B RTT ≈150 ms(양방향 75 ms). A가 B 시야를 가로질러 달리고(≈5 m/s) B가 자기 Client가 본 A 위치를 쏜다 → 5발 중 5발 명중(4발 이상 기준). 대조: RTT ≈650–800 ms(되감기 창 0.4 s 밖)에서는 같은 사격이 1/5만 맞는다(2발 이하 기준) | ~15 s |
| `Network/latency_sweep.json` | 108–109 | QA-5 D31: latency_loss_combat을 ping 0/100/200(파라미터 3세트)으로. 세트마다 RunId가 따로 생기고 요약이 나온다. 모든 세트에서 4발 명중, 연결 유지 | ~7 s × 3 |
| `Recorded/sample.json` | 106–107 | QA-5 D34: 손으로 만든 녹화(`sample.inputs.jsonl`, 100 입력)를 재생 → 북쪽으로 2 s 걸음(z 6 → >12), 동쪽을 봄(yaw 90), 소총 발사(magAmmo 감소) | ~5 s |
| `Stress/load_bots_10.json`, `load_bots_50.json` | 133–135 | QA-5 D35: LoadTest.md 조건(DevRespawn, MaxPlayers 100)으로 10/50 Client가 60 s 걸음 → tick p95 < 5 ms(느슨한 기준, 사용자가 조정), 60 s p95/p99·메모리를 Baseline에 기록. 측정: 10명 p95 0.06 ms, 50명 0.15 ms | ~62 s |
| `Reconnect/network_drop.json` | 74 | A(Proxy)가 알림 없이 끊김(`dropConnection`) → 1 s 뒤 서버는 아직 connected → DisconnectTimeout 뒤 Graced → 새 Proxy로 재접속 → 같은 Entity·체력·실드·무기·Medkit·위치 | ~9 s |
| `Network/invalid_packet.json` | 127 | 잘못된 패킷 9개(unknownId, truncated, oversized) → badPackets 9, 연결 유지 → garbage 15개 더(합 24 ≥ 20) → A만 Kicked. C의 Input Flood 120개 → C Kicked. 서버 계속 실행, tickFailures 0, B는 계속 Snapshot 수신 | ~3.5 s |
| `ServerProcess/shutdown.json` | 129 | 2명 접속 중 `stopServer` → 두 Client 모두 `ServerShutdown` 수신, 종료 코드 0, 종료 시간 측정값 ≈0.1 s(기준 8 s) → `server.running` false | ~4 s |
| `ServerProcess/restart.json` | 78 | 경기 중 `restartServer`(정상 종료 → 같은 인자·Seed로 새 프로세스, 새 Port) → 빈 Match → 두 Actor 재접속(새 플레이어) → 새 경기 Playing → 사격 피해 | ~5 s |
| `Persistence/db_down.json` | 77, 128 | DB 정상 확인 → `stopDb` → 경기 종료 → 저장 3회 실패 후 `db.failed` +1, saved 그대로, 서버 계속, 다음 경기 시작 → `startDb` → 다음 경기 종료 → `db.saved` +1. Docker나 `projecth-mysql` 컨테이너가 없거나 멈춰 있으면 첫 Step에서 나머지를 SKIPPED로 끝낸다(결과 SKIPPED, 종료 코드 0. `--fail-on-skip`이면 1) | 아래 표 참고 |

**Suite**(`QA/Suites/`)

| Suite | 내용 | 시간 |
|---|---|---|
| `smoke` | connect, move_to, basic_hit | ~16 s |
| `pre-push` | §93: connect, move, shoot, pickup, death, reconnect | ~30 s |
| `full-regression` | 모든 카테고리. Stress, Persistence, ServerProcess, Recorded 포함 | ~6 min |
| `faults` | QA-3: latency_loss_combat, lag_compensation, network_drop, invalid_packet, input_timeout, shutdown, restart, db_down | ~1 min |
| `stress` | QA-5 D35: `Stress` 전체(bots_50, load_bots_10, load_bots_50). 같은 이름의 카테고리가 있으므로 `suite:stress`로 부른다 | ~3.2 min |

**아직 없는 시나리오와 이유**

| 시나리오 | 이유 |
|---|---|
| `db_down`의 DB 정지·복구(실제 실행) | 이 개발 PC에는 `projecth-mysql` 컨테이너가 없다. 만드는 일(`docker compose up -d mysql`)은 권한 승인을 받지 못해 하지 않았다. 그래서 실제 실행은 SKIPPED다. 장애 절반(DB 없이 경기 2번 종료 → 각각 failed +1, saved 그대로, 서버 계속, 다음 경기 시작)은 DB가 없는 상태에서 Docker Step을 뺀 사본으로 실행해 확인했다(실패까지 각 ≈9 s). 정지·복구 절반은 Fake로만 확인했다 |

## CLI

저장소 루트에서 실행한다. 서버를 먼저 빌드해야 한다(`dotnet build Server/ProjectH.Server.slnx`).

```bash
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Combat/basic_hit.json   # 파일
dotnet run --project Server/src/ProjectH.QA -- run category:Combat                      # QA/Scenarios/Combat 전체
dotnet run --project Server/src/ProjectH.QA -- run suite:smoke                          # QA/Suites/smoke.json
dotnet run --project Server/src/ProjectH.QA -- run suite:pre-push                       # QA/Suites/pre-push.json
dotnet run --project Server/src/ProjectH.QA -- run pre-push                             # 같은 이름의 카테고리가 없으므로 suite
dotnet run --project Server/src/ProjectH.QA -- run category:Smoke --seed 7 --verbose
dotnet run --project Server/src/ProjectH.QA -- run category:Smoke --attach http://127.0.0.1:7780  # 이미 떠 있는 QA 서버
dotnet run --project Server/src/ProjectH.QA -- validate QA/Scenarios
dotnet run --project Server/src/ProjectH.QA -- list
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Smoke/connect.json --repeat 3          # QA-5
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Combat/basic_hit.json --seed-sweep 1..100 --stop-on-fail
dotnet run --project Server/src/ProjectH.QA -- run suite:stress
dotnet run --project Server/src/ProjectH.QA -- convert-recording rec.jsonl --out QA/Scenarios/Recorded/my_run.json
```

**대상 지정**

| 형식 | 의미 |
|---|---|
| `suite:<name>` | `QA/Suites/<name>.json`에 적힌 시나리오들. Suite 항목은 `QA/Scenarios` 아래에서만 찾는다(저장소 루트는 보지 않는다. `Server`가 소스 폴더 `Server/`를 고르지 않게 하기 위해서다. `..`로 밖에 나갈 수도 없다) |
| `category:<name>` | `QA/Scenarios/<name>/` 아래 모든 시나리오(대소문자 무시) |
| `.json` 파일, 폴더 경로 | 그대로 찾고, 다음 `QA/Scenarios` 아래, 다음 저장소 루트 아래에서 찾는다 |
| 접두어 없는 이름 | 같은 이름의 카테고리나 Suite. **둘 다 있으면 Tool 오류(종료 코드 2)이고, 두 선택지를 보여 준다** |

- 예: `smoke`는 카테고리 `Smoke`(1개)와 Suite `smoke`(3개)가 둘 다 있으므로 `suite:smoke`나 `category:Smoke`로 고른다.
- `run`·`validate`는 첫 줄에 무엇을 골랐는지 출력한다. 예: `suite smoke: 3 scenarios`, `category Smoke: 1 scenario`.

| 옵션 | 의미 |
|---|---|
| `--seed N` | 시나리오 Seed를 덮어쓴다(같은 조건 재실행, §15) |
| `--attach URL` | 서버를 띄우지 않고 그 QA API에 붙는다. 게임 Port는 `/qa/health`에서 읽는다 |
| `--server-dll PATH` | 띄울 서버. 기본은 `Server/src/ProjectH.Server/bin/{Debug,Release}/net10.0/ProjectH.Server.dll` 중 최신 |
| `--report-dir DIR` | Report 위치. 기본 `QA/Reports` |
| `--poll-ms N` | waitFor·waitForEvent의 서버 폴링 간격, 20–5000(기본 100) |
| `--verbose` | Step 상세와 서버 로그를 콘솔에 낸다 |
| `--repo DIR` | 저장소 루트. 기본은 현재 폴더에서 위로 찾는다 |
| `--unity-exe PATH` | UnityClient Actor의 기본 Development Player(`ProjectH.exe`, QA-4) |
| `--manual ask\|skip\|fail` | Manual Check: ask(기본: 터미널이면 묻고 아니면 SKIPPED), skip(묻지 않음), fail(답할 사람이 없으면 FAIL) |
| `--fail-on-skip` | SKIPPED 시나리오를 종료 코드 1로 친다(기본은 0). Docker가 꼭 있어야 하는 CI에서 쓴다 |
| `--repeat N` | QA-5: 시나리오마다 N번 연속 실행(1–1000). Seed가 있으면 같은 Seed |
| `--seed-sweep A..B` | QA-5: Seed A..B로 한 번씩(최대 1000개). `--seed`·`--repeat`와 같이 쓸 수 없다 |
| `--stop-on-fail` | QA-5: 반복·Sweep·파라미터 묶음을 첫 실패에서 멈춘다(Suite도 거기서 멈춘다) |
| `--parameter-set N` | QA-5: 파라미터 세트 N(1부터)만 실행한다. 요약의 재현 명령이 쓴다 |

- **종료 코드**(D18):
  - 0: 모두 PASS(또는 SKIPPED).
  - 1: 시나리오 실패, Ctrl+C로 중지, 또는 `--fail-on-skip`일 때 SKIPPED.
  - 2: Tool 오류(잘못된 JSON·Validation 오류, 서버 기동 실패, 내부 예외, 띄운 서버를 끄지 못함, Report를 못 씀).
  - 여러 시나리오를 실행하면 가장 큰 값을 돌려준다. 마지막 줄에 결과별 수를 낸다: `8 scenarios: 7 passed, 0 failed, 1 skipped, 0 errors; exit code 0.`
- **SKIPPED**(QA-3): 환경 때문에 시나리오의 나머지를 할 수 없을 때(예: Docker나 DB 컨테이너가 없음)의 결과다. 실행된 Step은 모두 통과했지만 끝까지 실행된 것은 아니므로 PASSED로 보고하지 않는다. 콘솔 요약 줄, Report 제목, Web UI 배지에 SKIPPED와 이유가 나온다.
- **Launch 모드**는 서버를 `DOTNET_ENVIRONMENT=Development`로 띄우고, 아래 인자를 준 뒤 시나리오 `server.options`를 붙인다.
  - `--qa-mode --Server:Port=0 --Qa:Port=0 --Persistence:Enabled=false --Server:AirDrop=false --Server:StartCountdownSeconds=1 --Server:ResultSeconds=1 --Server:LootSeed/ZoneSeed/SpawnSeed=<seed>`
  - Ready 조건은 `QA_READY` 줄과 `/qa/health`(20 s 이내)다. 고정 sleep을 쓰지 않는다(§81).
  - 끝나면 `/qa/server/stop` → 최대 10 s 종료 대기 → 그래도 남으면 자기 자식 프로세스만 Kill한다.
- **Ctrl+C**: 현재 Step을 멈추고 Cleanup(Actor 종료, 서버 정지)을 한 뒤 Report를 쓴다.
- **콘솔 출력**은 Step마다 한 줄(`01 Connect playerA PASS 643 ms`)이다. 실패하면 Expected / Actual이 붙는다.

## Web UI (QA-2)

```bash
dotnet run --project Server/src/ProjectH.QA -- ui                 # http://127.0.0.1:5180/
dotnet run --project Server/src/ProjectH.QA -- ui --port 5190
dotnet run --project Server/src/ProjectH.QA -- ui --attach http://127.0.0.1:7780   # 이미 떠 있는 QA 서버로 실행
```

- 구성(D19): 같은 도구 안의 ASP.NET Core Minimal API와 `wwwroot`(HTML/CSS/바닐라 JS, npm 없음)다. 브라우저를 자동으로 열지 않고 URL만 출력한다.
- `--server-dll`, `--report-dir`, `--poll-ms`, `--repo`는 CLI와 같다.
- **Ctrl+C**는 실행 중인 시나리오를 먼저 멈추고 Cleanup(Actor, 띄운 서버, Report)을 기다린 뒤(최대 60 s) UI를 닫는다.

**화면 배치**(§47)
- 왼쪽: `QA/Scenarios` 카테고리 트리와 Suite. Suite 안의 시나리오를 눌러도 열린다.
- 가운데: 편집기. 탭은 Form / Raw JSON / JSON Preview이고, 버튼은 New / Validate / Save다.
  - Form은 시나리오 필드(name, description, seed, timeoutSeconds, tags, server·variables·actors는 JSON 칸)와 Step 목록이다.
  - Step 목록의 한 줄은 `01 Connect playerA`이고, 결과 배지와 시간이 붙는다.
  - Step을 누르면 선택되고 편집 칸이 열린다. action 선택, 인자 수정·삭제, `+ parameter`로 Action Spec의 인자, 공통 필드, 연산자를 넣는다. 위치 인자는 Marker 이름을 자동완성한다.
  - ▲▼ 또는 Drag & Drop으로 순서를 바꾸고, ✕로 지운다. `+ Add Step`은 Registry의 모든 Action에서 고르고, 선택한 Step 뒤에 넣는다.
  - 왼쪽 점은 Breakpoint다. UI 실행에만 쓰고 파일에 저장하지 않는다. 파일의 `"breakpoint": true`도 UI에서는 멈춘다.
  - Form은 주석이나 끝 쉼표가 없는 JSON에서만 열린다. 그런 파일은 Raw JSON에서 고친다. Form이 저장하는 형식은 저장소 시나리오와 같다(Step 한 줄에 하나).
- 아래 탭

| 탭 | 내용 |
|---|---|
| Live Log | 카테고리 QA / Server / Actor / Network / Assertion 필터(D21) |
| Actors | 이름·상태·체력 목록. 선택한 Actor는 서버 Player DTO와 Actor 상태를 보인다: PlayerId, 연결, 위치, 속도, 체력, 실드, 무기, 탄약, 인벤토리, 자원, 이동 모드 |
| Match | 상태, Tick, 플레이어, 생존, Zone, 경과 시간 |
| Server | CPU, 메모리, GC, tick p50/p95/p99/max, 패킷/s, QA 명령 시간과 큐 카운터 |
| Batch | QA-5: 묶음 실행의 실행별 줄(번호, 반복, 파라미터 세트, Seed, 결과, Report)과 요약 |
| Reports | 최근 실행 50개의 `report.html` 링크 |

- Server 탭에 bytes/s는 없다. `/qa/metrics`가 내지 않기 때문이다.

**실행**(D20, D23): 한 번에 하나만 실행한다. 편집기의 현재 텍스트를 실행하므로 저장하지 않은 내용도 돌릴 수 있다. 이때 Report에 `unsavedText`가 표시된다. Validation 오류가 있으면 시작하지 않는다.

| 버튼 | 동작 |
|---|---|
| Run | 처음부터 실행 |
| Run From | 선택한 Step부터 실행한다. 서버를 새로 띄우므로 앞 Step은 Skipped이고, 그 Arrange와 saveAs 변수가 없다. 시작 전에 확인 창과 Report 경고가 나온다 |
| Run Until | 선택한 Step 뒤에서 Pause |
| Single Step | 대기 중이면 첫 Step 앞에서 멈춘 채로 시작한다. 멈춰 있으면 한 Step만 실행한다 |
| Pause / Resume | Pause는 다음 Step 경계에서 멈춘다. 실행 중인 Step은 끝까지 간다 |
| Stop | 바로 취소한 뒤 Cleanup(§57–58) |
| Retry Failed Step | UI 실행에서 Step이 실패하면 Cleanup하지 않고 그 Step에서 멈춘다. 이 버튼을 누르면 같은 게임에서 그 Step을 다시 실행한다(경고: 실패 뒤 게임 상태가 바뀌었을 수 있다. Report에 시도 횟수와 경고를 남긴다). Resume이나 Stop은 그 실패로 끝내고(Failed, Expected/Actual 유지) Cleanup한다 |
| Retry Scenario | 마지막 시나리오를 같은 Seed로 처음부터 다시 실행 |
| Seed | 덮어쓸 Seed. 비우면 시나리오 Seed를 쓴다 |
| Debug Run | Step 앞뒤로 `/qa/match`·`/qa/players` Snapshot을 Report `debugSnapshots`에 남긴다(D24, 최대 400개) |
| Repeat / Seed sweep / Stop on fail | QA-5: Run에만 쓴다(Run From·Until·Single Step이면 거부). 파라미터가 있는 시나리오의 Run도 묶음이다. 묶음은 실패를 보류하지 않고 다음 실행으로 간다. 아래 "Batch" 탭에 실행마다 파라미터 세트·Seed·결과·Report 링크가 한 줄씩(최근 200줄) 나오고 끝에 요약이 나온다. 배지는 `run 2/3 parameters[2] {...}`. Retry Scenario는 같은 묶음을 다시 실행한다. Run From·Until·Single Step은 파라미터 세트 1만 쓴다 |

**멈춰 있을 때**
- 시나리오 Timeout은 실행 시간만 센다. Pause, Breakpoint, 실패 보류 중에는 멈춘다.
- **Pause는 Runner만 멈춘다. 게임 서버는 계속 시뮬레이션한다.** Zone이 줄어들고, Grace가 끝나고, 끝난 라운드는 `ResultSeconds` 뒤에 리셋된다.
- Actor는 계속 입력을 보낸다. 그래서 InputTimeout에는 걸리지 않는다.

**스트림과 조회 빈도**
- 진행 상황(state, step, run)과 로그는 SSE `/api/stream` 하나로 받는다(D20).
- 로그는 다음처럼 제한한다(D21, §143).
  - 도구 쪽 Ring은 2000줄이다.
  - SSE는 250 ms마다 최대 50줄을 보낸다(초당 200줄). 밀린 줄은 건너뛰고 그 수를 알린다.
  - 브라우저는 최근 1000줄만 남긴다.
- Inspector는 실행 중이거나 멈춰 있을 때만, 보이는 탭 하나를 1초마다 조회한다(D22).
  - Run 전용 QA 클라이언트를 따로 쓴다.
  - Actor 목록은 Actor Manager가 게시한 Snapshot에서만 읽는다.

**보안**(로컬 도구, 로그인 없음)
- 127.0.0.1에만 Bind한다. `ASPNETCORE_URLS`는 무시한다.
- 모든 요청은 `Host` 헤더가 `127.0.0.1`/`localhost`/`[::1]`과 UI Port여야 한다. DNS Rebinding으로 다른 이름에서 시나리오·리포트를 읽지 못하게 하려는 것이다.
- POST는 다음을 모두 만족해야 한다. 다른 웹사이트가 브라우저를 통해 도구를 움직이지 못하게 하려는 것이다.
  - `Content-Type: application/json`이다.
  - `Origin`이 있으면 같은 Origin이다.
  - `Sec-Fetch-Site`가 있으면 `same-origin`/`none`이다.
- CORS 헤더는 보내지 않는다.
- **파일 접근**
  - 저장(D25)과 열기는 `QA/Scenarios` 아래 `.json`만 된다. 경로를 정규화한 뒤 루트 밖(`..`, 절대 경로, 드라이브)이면 거부한다.
  - 저장 전에 Validation하고, 오류가 있으면 저장하지 않는다(경고는 허용).
  - Report는 runId 형식(`qa-yyyyMMdd-HHmmss-xxxx`)의 `report.html`만 연다.
- 요청 본문은 4 MB, SSE 동시 연결은 8개로 제한한다.

**API**(브라우저가 쓰는 것. curl로도 쓸 수 있다)

| Method | Path | 내용 |
|---|---|---|
| GET | `/api/tree`, `/api/actions`, `/api/markers` | 트리, Action Spec, Marker 이름 |
| GET | `/api/scenario?path=Combat/basic_hit.json` | 파일 내용 |
| POST | `/api/validate` `{text}`, `/api/save` `{path, text}` | |
| GET / POST | `/api/run` | 상태 / 시작 `{path?, text?, mode: run\|from\|until\|single, stepIndex, seed?, debug, breakpoints[], repeat?, seedSweep?, stopOnFail?}` |
| POST | `/api/run/pause`, `resume`, `step`, `stop`, `retry`, `retry-scenario`, `breakpoints` `{indices}` | |
| GET | `/api/stream` | SSE: `state`, `step`, `run`, `log` |
| GET | `/api/inspect/actors`, `/api/inspect/actor?alias=`, `/api/inspect/match`, `/api/inspect/server` | 실행 중일 때만 |
| GET | `/api/reports`, `/reports/<runId>/report.html` | |

```bash
curl -s -X POST -H "Content-Type: application/json" -d '{"path":"Combat/basic_hit.json"}' http://127.0.0.1:5180/api/run
curl -s http://127.0.0.1:5180/api/run
```

## Reports

`QA/Reports/<runId>/report.json`과 `report.html`이다. HTML은 외부 리소스가 없는 단일 파일이다. `QA/Reports/`는 gitignore에 있다. runId 형식은 `qa-yyyyMMdd-HHmmss-xxxx`다.

- Summary: 결과, runId, seed, 시나리오 파일, 시작 시각, 시간, 종료 코드, Git commit과 dirty 여부(§99–100), 서버 모드·Port·pid·버전
- Step 표(id, 제목, phase, 결과, ms, 메시지, Expected/Actual)
- 실패: 실패 Step과 Expected / Actual(§102)
- 실패 시 State Dump: Actor 상태, 서버 플레이어 전체, Match(§103–104)
- 최근 이벤트 50개(§105), Metrics Snapshot, 서버 로그 마지막 200줄, 서버 실행 명령
- Cleanup 결과. 결과와 따로 적는다(§114)
- 변수 최종값, Validation 경고, Manual Check·Screenshot(QA-4 전까지 비어 있음)
- QA-5: Parameters·Batch 줄(Summary), Baseline 표(아래 "QA-5")

## Fault Injection

QA-3(D13, D14, §70–78, §119, §127–129). 서버 코드는 바꾸지 않는다(§72). 장애는 Tool 쪽에서 만들고, 결과는 서버의 권위 있는 상태와 QA Metrics로 검증한다.

### Network: Actor별 UDP Fault Proxy

```text
Actor(LiteNetLib) ── 127.0.0.1:<proxy> ── UdpFaultProxy ── upstream socket ── 게임 서버
```

- `"network": { "proxy": true }`인 Actor만 Proxy를 거친다. 장애를 걸기 전에는 그대로 전달한다(Random도 쓰지 않는다).
- **연결 시도마다 새 Proxy**를 만든다(connect, reconnect, connectAll). 그래서 재접속은 새 Client Port와 새 upstream endpoint로 서버에 간다. Actor의 장애 설정은 새 Proxy에 그대로 넘어간다(`clearNetworkFault` 전까지 유지). 서버를 재시작한 뒤에는 새 Proxy가 새 게임 Port를 향한다.
- 방향: `toServer`(Actor → 서버), `toClient`(서버 → Actor), `both`(기본).
- 지연은 전달 루프 하나가 시각 순서 큐로 보낸다. 일찍 보내지 않는다. 큐는 최대 4096 datagram이고, 넘치면 버리고 `queueFull`로 센다. Jitter는 순서를 바꾼다. 장애를 지울 때 큐에 남은 datagram이 새 datagram보다 늦게 도착할 수 있다(LiteNetLib이 처리한다).
- 손실·중복·Jitter 결정은 시나리오 Seed와 Actor 이름에서 만든 Seed로 정해진다(§14). 같은 순서의 datagram이면 같은 결정이다.
- Proxy는 처음 보낸 Client endpoint만 받는다. 다른 로컬 송신자는 `foreign`으로 세고 버린다.
- Cleanup은 항상 장애를 지우고 모든 Proxy를 닫는다(Report의 `network` 줄).

| Action | 인자 | 동작 |
|---|---|---|
| `networkFault` | actor, `latencyMs?`(0–10000), `jitterMs?`(0–10000), `lossPercent?`(0–100), `duplicatePercent?`(0–100), `direction?` | 그 방향의 설정을 바꾼다(안 준 값은 0). Block 상태는 유지한다. saveAs = Proxy 상태 |
| `clearNetworkFault` | actor | 양방향 장애와 Block을 지운다 |
| `blockNetwork` / `unblockNetwork` | actor, `direction?` | 모든 datagram을 버린다/되돌린다. 큐에 있던 것도 버린다 |
| `dropConnection` | actor | 연결을 알림 없이 버린다(`BotConnection.Abort`, 끊김 패킷 없음). Proxy가 없어도 된다. 서버는 DisconnectTimeoutMs(5 s) 뒤에 알아채고 살아 있는 참가자를 Grace한다 |

- 측정(이 PC): 양방향 200 ms일 때 `network.rtt` ≈330 ms(LiteNetLib 값이 천천히 따라온다). 75 ms일 때 ≈130–150 ms. 장애를 지우면 ≈4 s 안에 100 ms 밑으로 돌아온다.
- **Lag Compensation 판정**: Headless Actor는 마지막 Snapshot의 상대 위치를 조준하고 그 Tick을 ViewTick으로 보낸다. 서버가 그 Tick으로 되감으므로 RTT가 되감기 창(12 Tick = 0.4 s) 안이면 달리는 상대도 결정적으로 맞는다(5/5). 창 밖(양방향 400 ms)에서는 거의 빗나간다(1/5, 달리기 시작 직후의 한 발). 그래서 시나리오는 4/5 이상과 2/5 이하로 판정한다.
- **주의**: 이 PC에서는 새로 bind한 UDP 소켓이 새 상대에게 보내는 첫 datagram 한두 개가 사라지는 일이 관찰됐다(원인 미확인, Host Firewall 추정). LiteNetLib은 재시도하므로 Actor에는 영향이 없다. 단일 datagram을 보내는 테스트는 먼저 Warm-up을 해야 한다(`UdpFaultProxyTests` 참고).

### Invalid Packet (D14)

| Action | 인자 | 동작 |
|---|---|---|
| `sendInvalidPackets` | actor(접속 중), `kinds`: [`unknownId`, `truncated`, `oversized`, `garbage`, `inputFlood`], `count?`(종류마다 1–500, 기본 1) | Actor의 살아 있는 연결로 바이트를 그대로 보낸다(ReliableOrdered라 전부 도착한다). 바이트는 Seed로 정해진다. saveAs = `{sent: {kind: n}, total, handedToConnection}` |

서버가 보는 것(`Net/NetworkListener`)은 다음과 같다.
- `unknownId`: 첫 바이트 0xFF → UnknownId.
- `truncated`: PlayerInput에 개수만 있고 내용이 없음 → Malformed.
- `oversized`: 3600바이트(MaxPacketSize × 3), 불가능한 입력 개수 → Malformed.
- `garbage`: 무작위 바이트. 첫 바이트는 Client→서버 id가 아니다 → UnknownId 또는 WrongDirection.
- `inputFlood`: 형식은 맞는 입력(Seq 0이라 적용되지 않음)을 한 Tick에 몰아 보낸다 → 초당 한도(SimHz × 2)를 넘는 것은 InputRate.

나쁜 패킷은 하나하나 `server.metrics.health.badPackets`에 더해진다. 한 연결이 `Server:BadPacketDisconnectThreshold`(20)에 이르면 그 연결만 `Kicked`로 끊긴다(`network.disconnectCode`). Kick 수는 QA Metrics에 없어서 Actor의 DisconnectCode로 확인한다. 전송은 `BotConnection.SendRaw`(Bots에 추가만 한 QA 전용 함수)로 한다.

### Server Process (launch 모드만)

| Action | 결과(saveAs) | 동작 |
|---|---|---|
| `stopServer` | `{exited, exitMs, exitCode, killed, message}` | `POST /qa/server/stop`(Ctrl+C·SIGTERM과 같은 경로) 후 프로세스 종료까지 잰다. 10 s 안에 안 끝나면 이 Tool의 자식만 kill하고 실패로 보고한다 |
| `killServer` | 같음 | 이 Tool이 띄운 프로세스 트리만 kill한다(Crash 시험) |
| `startServer` | `{gamePort, qaPort, pid, startMs}` | 같은 인자와 Seed로 다시 띄운다(새 빈 Port). 이후 Step과 Actor의 다음 접속은 새 서버로 간다 |
| `restartServer` | `{exit, start}` | 돌고 있으면 stopServer, 그다음 startServer |

- attach 시나리오에서는 Validation 오류다. `--attach`로 바꾼 실행에서는 Step이 "needs a server the tool launched"로 실패한다.
- 재시작한 서버에는 Match 상태가 없다. Actor는 `reconnect`(또는 `connect`)로 새 플레이어가 된다.
- 재시작하면 이벤트 Cursor를 새로 만든다(서버 이벤트 번호가 처음부터 시작한다). `event.*` 수는 지금 서버 것만 센다.
- 서버 로그는 한 Ring에 이어서 쌓인다. Report의 서버 Cleanup 줄에 시작 횟수가 붙는다.
- 시나리오가 서버를 멈춘 채 끝나면 Cleanup은 Metrics·이벤트 조회를 건너뛰고 "already exited"로 기록한다.
- Web UI Inspector는 처음 서버의 QA URL을 계속 본다. 재시작 뒤 Inspector의 서버 값은 갱신되지 않는다.
- 측정: 피어 2명, Persistence 꺼짐에서 stop 요청부터 종료까지 ≈0.1 s. 상한은 Loop join 5 s + 종료 통지 1 s + DB drain(기본 5 s) + Host 시간이다.

### DB (Docker)

| Action | 인자 | 동작 |
|---|---|---|
| `waitDbHealthy` | `container?`(기본 `projecth-mysql`) | 컨테이너가 돌고 있으면 Health가 healthy(Healthcheck가 없으면 running)가 될 때까지 기다린다. 있지만 멈춰 있으면 시작하지 않고 나머지를 SKIPPED로 끝낸다(사용자가 멈춰 둔 DB를 Tool이 켜지 않는다) |
| `stopDb` | `container?` | 돌고 있으면 `docker stop --time 10`을 하고, 이번 실행이 멈췄다고 기록한다(Cleanup이 다시 시작한다). 이미 멈춰 있으면 아무것도 하지 않고 기록도 하지 않는다 |
| `startDb` | `container?` | `docker start` 후 healthy까지 기다린다 |

- `docker compose down`, `rm`, `-v`는 절대 쓰지 않는다. `docker inspect`가 돌려준 이름이 정확히 `/<container>`일 때만 다룬다(ID 앞부분 일치는 거부한다).
- **Docker나 컨테이너가 없으면** 그 Step은 SKIPPED이고 이 시나리오의 나머지 Step도 SKIPPED다(이유가 각 Step과 경고에 남는다). 실행 결과는 SKIPPED(종료 코드 0, `--fail-on-skip`이면 1)이고 요약 줄·Report·Web UI에 `from step NN (id): 이유`가 나온다. DB 장애 뒤의 단언은 DB 장애 없이는 의미가 없기 때문이다.
- Cleanup은 이번 실행이 멈춘 컨테이너만 자기 Timeout(시작 30 s + healthy 60 s)으로 다시 시작한다(Report의 `db <container>` 줄). 사용자가 일부러 멈춰 둔 컨테이너는 건드리지 않는다.
- DB 시나리오는 `server.options`에 `"Persistence:Enabled": "true"`를 주고, 첫 Step에서 `waitDbHealthy`로 DB가 떠 있는지 확인한다.
- 검증 경로: `server.metrics.db.<enabled|saved|failed|discarded|dropped|queueLength>`, `server.metrics.dbQueueLength`.
- **서버의 실제 정책**(`Persistence/MatchHistoryWriter`): Writer 하나가 경기 기록마다 최대 3번 시도한다(시도마다 새 Connection, 실패 뒤 1 s, 2 s 대기). 모두 실패하면 `failed` +1이고 그 기록은 잃는다(재시도 큐 없음). 게임은 DB를 기다리지 않는다. DB가 돌아오면 다음 기록부터 저장된다(서버 재시작 불필요). 이 PC에서 DB가 없을 때(포트가 닫힘) 경기 종료부터 `failed` +1까지 ≈9 s였다.

## Unity Client (QA-4 명령 수신기, QA-5 입력 녹화)

Client 쪽 세부 사항(스레드, 상태 코드 전체, 녹화 형식)은 `Docs/Client.md`의 "QA 자동화"에 있다. 이 코드는 **Development Build와 Editor에만 있다.** Release 빌드에는 없다.

실행 인자:

| 인자 | Editor 환경 변수 | 뜻 |
|---|---|---|
| `-qaPort N` | `PROJECTH_QA_PORT` | `127.0.0.1:N`에 QA HTTP를 연다. 없으면 꺼진다 |
| `-qaShotDir <dir>` | `PROJECTH_QA_SHOT_DIR` | 스크린샷 폴더. 예: `QA/Reports/<runId>`. 없으면 `persistentDataPath/qa-shots` |
| `-qaRecord <file>` | `PROJECTH_QA_RECORD` | 입력 녹화 JSON Lines 파일 |

- 환경 변수는 Editor에서만 읽고, 명령줄에 값이 없을 때만 쓴다.
- Unity/Hub를 시작하기 전에 설정해야 한다.
- MPPM 복제본도 이 값을 물려받는다. Port는 먼저 연 하나만 쓴다.

예: `ProjectH.exe -qaPort 18777 -qaShotDir QA/Reports/qa-20261002-00142 -autoConnect -port <gamePort> -devId qa1 -screen-fullscreen 0 -screen-width 800 -screen-height 450`

| Method | Path | Body | 응답 |
|---|---|---|---|
| GET | `/qa/status` | — | `{ok, devPlayerId, connected, joined, screen, statsOpen, debugVisible, alive, health, fps, frame}` |
| POST | `/qa/screenshot` | `{"name":"[A-Za-z0-9_-]{1,64}"}` | 파일이 생긴 뒤 `{ok, path}`. Windows 장치 이름(CON, NUL, COM1…)은 400 |
| POST | `/qa/ui` | `{"command":"openMenu\|closeMenu\|openStats\|closeStats\|toggleDebug"}` | `{ok, command, screen, statsOpen, debugVisible}`. 지금 화면에 맞지 않으면 409이고 아무것도 바뀌지 않는다 |

- 모든 POST에는 `Content-Type: application/json`이 있어야 한다. 없으면 415다(브라우저 CSRF 방지).
- 그 밖의 오류 코드:
  - 400: 잘못된 JSON·이름·명령
  - 403: loopback이 아님
  - 404, 405(OPTIONS 포함)
  - 408: Body가 5초 안에 오지 않음
  - 413: Body가 16 KB를 넘음
  - 503: 32개 처리 중, 큐가 가득 참, 종료 중
  - 504: 5초 안에 답이 없음
- 모든 응답은 `Connection: close`다.
- Gameplay 입력 명령은 없다(§87). 총격 같은 동작은 Headless Actor로 확인한다.
- 화면이 잠겨 있거나 창이 그려지지 않으면, 스크린샷 파일은 생겨도 내용이 비어 있을 수 있다. 잠기지 않은 데스크톱에서 확인한다.

녹화 형식(`convert-recording`의 입력):

- BOM 없는 UTF-8 JSON Lines다.
- 첫 줄은 `{"type":"header","version":1,"simHz":30,"devPlayerId":"qa1"}`다. 한 번도 Spawn하지 않으면 파일이 비어 있다.
- 이후 Step마다 `{"t","moveX","moveY","yaw","buttons","aimYaw","aimPitch"}` 한 줄이다.
- `t` = Step 번호 / simHz다. Spawn 전이나 끊긴 동안의 시간은 빠진다. Step 번호는 `round(t*simHz)`로 구한다.
- `buttons`는 `InputButtons` 비트다. 건설 요청과 접속은 녹화에 없다.
- 최대 54,000줄이다.

## QA-5

D31–D35, 요청서 §106–111, §133–136, §156. 실행은 언제나 순차다(병렬 없음). 성능 측정이 다른 실행과 CPU를 나눠 쓰지 않게 하기 위해서다.

### Parameterized Scenario (D31)

```json
"variables": { "limit": 150 },
"parameters": [ { "ping": 0, "rttMin": -1 }, { "ping": 100, "rttMin": 150 }, { "ping": 200, "rttMin": 300 } ]
```

- 항목마다 `variables` 위에 합쳐서(같은 이름은 파라미터가 이긴다) 한 번씩 실행한다. 실행마다 RunId·Report·history 줄이 따로 생긴다.
- **Validation**: 항목은 객체여야 하고(최대 100), 이름은 변수 이름 규칙을 따르며 `runId`·`seed`는 쓸 수 없다. `${x}`는 모든 항목에 있거나 `variables`에 기본값이 있어야 한다. 일부 항목에만 있으면 빠진 항목을 오류에 적는다.
- 서버 설정(`server.options`)에는 변수가 들어가지 않는다. 파라미터는 Step 인자에만 쓴다.
- 콘솔 첫 줄과 Report Summary에 `parameters[2] {"ping":100}`이 나온다. 끝에 묶음 요약이 나온다. 요약에는 파라미터 세트별 PASS 수, 실패 Seed, 재현 명령(`run <file> --seed S --parameter-set N`)이 있다.
- 예: `Network/latency_sweep.json`.

### Repeat / Seed Sweep (D32)

- `--repeat N`(1–1000): 같은 시나리오를 N번 실행한다. Seed는 `--seed`, 그다음 시나리오 Seed다. 둘 다 없으면 실행마다 새 무작위 Seed를 쓴다. 요약은 그 실행의 실제 Seed를 적는다.
- `--seed-sweep A..B`: Seed A부터 B까지 한 번씩(최대 1000개, 음수 가능).
- 파라미터와 함께 쓰면 파라미터 세트마다 반복이나 Sweep 전체를 돈다(세트 1의 모든 Seed, 그다음 세트 2…).
- `--stop-on-fail`: 첫 FAILED·ERROR에서 남은 실행을 시작하지 않는다. SKIPPED는 실패가 아니다.
- **Ctrl+C**: 현재 실행을 멈추고 Cleanup한 뒤, 남은 실행은 시작하지 않는다(종료 코드 1).
- 종료 코드는 모든 실행 중 가장 큰 값이다. 마지막 줄은 `1 scenario, 3 runs: 3 passed, ...`다.

```text
== Batch Basic Hit And Reconnect: 3 runs: 2 passed, 1 failed, 0 skipped, 0 errors; exit code 1.
   failing seeds: 2
   run 2/3 seed 2 FAILED at step 05 (a_damaged)  runId qa-...
   reproduce: dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Combat/basic_hit.json --seed 2
```

- RunId는 같은 초에 여러 개 만들어도 겹치지 않는다(이 프로세스가 이번 초와 직전 초에 낸 id 집합 + Report 폴더 존재 확인). 형식은 그대로다.
- Web UI: 툴바의 Repeat / Seed sweep / Stop on fail과 "Batch" 탭(위 "Web UI").

### Baseline (D33, §135–136)

- 실행이 끝날 때마다 `QA/Reports/history/<key>.jsonl`에 한 줄을 덧붙인다(`--report-dir`이면 그 아래 `history/`).
  - key는 `QA/Scenarios` 기준 경로(`.json` 제외)에서 `[A-Za-z0-9_-]` 밖의 문자를 `_`로 바꾼 읽기용 부분 + `_` + 경로(소문자)의 SHA-256 앞 8자리다(`Stress/load_bots_10.json` → `Stress_load_bots_10_<8 hex>`). 읽기용 부분이 같아지는 두 파일(`Stress/a.json`, `Stress_a.json`)도 history가 섞이지 않는다. 밖의 파일은 `external_<이름>_<경로 해시>`. 각 줄에 `scenarioFile`(상대 경로)도 남기고, 직전 PASSED 실행은 같은 `scenarioFile`에서만 찾는다.
  - 줄: `runId, utc, scenario, scenarioFile, seed, parameters, parametersKey, gitCommit, gitDirty, status, durationMs, metrics{tickP50Ms, tickP95Ms, tickP99Ms, workingSetMB}, values{...}`.
  - `metrics`는 실행 끝의 `/qa/metrics`(기본 창)다. 시나리오가 서버를 멈춘 채 끝나면 없다.
  - `values`는 시나리오 `baseline.values`에 적은 변수의 끝 값이다. 숫자·문자열(200자)·bool만 남긴다. 객체는 "(object, not recorded)".
  - 시나리오 파일마다 최근 50줄만 남긴다. FAILED·ERROR·CANCELLED 실행도 남는다(비교 대상은 PASSED만).
- **기록하지 않는 경우**: 파일이 없는 UI 텍스트, 저장하지 않은 편집기 텍스트, Run From Step. Report Baseline에 이유가 나온다. Report를 쓰지 않는 실행(테스트)도 기록하지 않는다.
- **두 Tool 프로세스가 동시에 쓸 때**: `<key>.lock`을 `FileShare.None`으로 열어(최대 3 s 재시도) 잠근 뒤 읽기 → 추가 → 50줄로 자르기 → 같은 폴더의 임시 파일에 쓰기 → `File.Move(overwrite)`로 바꾼다. 읽는 쪽은 옛 파일이나 새 파일을 통째로 본다. 잠금을 못 얻거나 I/O가 실패하면 Warning만 남기고 결과·종료 코드는 그대로다. `.lock` 파일은 빈 표시 파일로 남는다.
- **Report "Baseline"**: 같은 시나리오, 같은 파라미터 세트의 가장 최근 PASSED 실행과 비교한 Previous / Current / Change % 표.
  - 행: durationMs(정보만, 경고 없음), server.tickP50Ms·tickP95Ms·tickP99Ms·workingSetMB, `baseline.values`.
  - **Threshold가 있는 지표**(그 경로를 연산자로 검사하는 `assert`·`waitFor`가 있음, 또는 그런 `assert`의 `saveAs` 값)는 그 Step이 판정한다. 표에는 "assert in scenario"로 나온다.
  - **Threshold가 없는 지표**가 `baselineWarnPercent`(기본 50 %)보다 커지면(값이 클수록 나쁘다고 본다) Warning이다. Report Warnings와 콘솔 `WARNING Baseline: ...`에 나온다. **FAIL은 아니다**(§136).
  - 이전 값이 0이면 Change %를 계산하지 않는다. 0.05 ms 같은 작은 Tick 값은 실행 사이 편차가 커서 Warning이 날 수 있다. Warning은 확인할 것을 알리는 것이지 회귀 판정이 아니다.
- history는 로컬이다(`QA/Reports`는 gitignore).

### Recording 재생 (D34, §106–107)

```bash
ProjectH.exe -qaRecord C:/tmp/run1.jsonl ...          # Unity Development Build에서 녹화 (위 "Unity Client")
dotnet run --project Server/src/ProjectH.QA -- convert-recording C:/tmp/run1.jsonl [--out QA/Scenarios/Recorded/run1.json] [--actor playerA] [--force]
```

- `convert-recording`은 녹화를 검사하고, 시나리오 옆에 `<name>.inputs.jsonl`로 복사하고, 초안 시나리오를 쓴다.
  - 검사: header(`type: header`, version 1, simHz 1–240 정수), 줄 길이 1024자, 파일 16 MB, 입력 54,000줄, 모든 숫자가 유한하고 범위 안(moveX·moveY −1..1, aimPitch −90..90, buttons 0–65535 정수), `t`가 줄마다 증가. 빈 파일은 "한 번도 Spawn하지 않음"으로 알린다. 모르는 버튼 비트는 지우고 수를 알린다.
  - `--out`은 저장소 루트 기준 경로(절대 경로도 가능)이고 `QA/Scenarios` 아래 `.json`이어야 한다. 초안을 먼저 쓰고 입력을 복사한다. 실패하면 이번 호출이 만든 파일(초안·입력)을 지운다(`--force`로 덮어쓰던 기존 파일은 그대로). 기본은 `QA/Scenarios/Recorded/<녹화 이름>.json`. 이미 있으면 `--force` 없이는 쓰지 않는다(종료 코드 2).
  - 초안: `Server:DevRespawn=true`(Client 하나로 움직이고 쏠 수 있다), connect → `player.alive` 대기 → `playInputs`(녹화 길이 + 15 s Timeout) → 자리표시 assert 2개(연결·생존). description의 TODO에 바꿀 assert 예가 있다.
- `playInputs {actor, file, speed?}`
  - `file`은 시나리오 파일 기준 상대 경로이고 정규화 뒤 `QA/` 밖이면 실패한다. 저장되지 않은 UI 텍스트에서는 쓸 수 없다.
  - Headless Actor가 Pump Tick마다(서버 SimHz) 기록된 입력 하나를 보낸다. MoveX·MoveY·Yaw·Buttons·AimYaw·AimPitch는 기록 그대로, Seq는 BotConnection이 매기고 ViewTick은 그 Actor의 최신 Snapshot Tick이다.
  - 실제로 입력을 보낸 Tick에만 진행한다(입력 정지·접속 전에는 멈춰 있다). 모두 보내면 Step이 끝난다. saveAs = `{inputs, sent, simHz, speed, seconds, ms}`.
  - `speed`(0.25–4): Tick마다 녹화 커서를 speed만큼 옮긴다. 1보다 크면 건너뛴 입력의 버튼을 보낸 입력에 합친다(한 Tick 누름을 잃지 않게). 마지막 입력은 어느 속도에서도 반드시 보낸다(100개를 speed 2로 재생하면 51 Tick). 1보다 작으면 같은 입력을 여러 Tick 보낸다(누름이 길어진다).
  - 녹화 길이가 Step Timeout보다 길면 보내기 전에 실패하고 필요한 `timeoutMilliseconds`를 알린다. 기본 Timeout은 60 s다.
  - Timeout·취소·연결 끊김이면 남은 재생을 지운다(다음 Step으로 새지 않는다). 재생 중에는 Actor의 다른 의도(moveTo, aim 등)를 쓰지 않는다.
  - 메모리: 녹화 전체를 한 배열(최대 54,000개, 약 1.5 MB)로 읽는다. 재생이 끝나거나 멈추면 Actor가 놓는다.
- **한계**: 녹화는 입력만 담는다. Build 요청, UI, 접속, 녹화 때 서버 상태(Spawn 위치, Loot, 다른 플레이어)는 없다. 그래서 같은 녹화도 다른 결과가 날 수 있다. 시작 상태가 중요하면 재생 전에 Arrange(setPosition, giveWeapon)로 만든다(`Recorded/sample.json`).

### Load 시나리오 (D35, §133–134)

- `Stress/load_bots_10.json`, `load_bots_50.json`: Docs/LoadTest.md 조건을 `server.options`로 고정한다.
  - `Server:DevRespawn=true`(경기 흐름 없음, 죽어도 3 s 뒤 부활), `Server:MaxPlayers=100`, `Qa:Events=false`(QA Tick diff 비용을 빼서 LoadTest와 같게).
  - `spawnActors` → `connectAll` → `moveVectorAll`(각자 다른 방향으로 걷기) → 60 s → `tickP95Ms`(60 s 창) < `${tickP95LimitMs}`(5 ms).
  - **5 ms는 느슨한 기본값이다.** 실제 목표는 Baseline을 보고 사용자가 `variables.tickP95LimitMs`로 정한다(§134).
  - 60 s p95·p99·메모리를 `baseline.values`로 남긴다. 두 번째 실행부터 Report에 Baseline 표가 나온다.
  - LoadTest의 봇과 다른 점: 봇 Brain(사격·줍기)이 아니라 걷기만 한다. 같은 PC에서 Tool·Actor Pump·서버가 같이 돈다.
- 측정(2026-10-02, 이 PC, Debug): 10명 p95 0.062 ms·p99 0.097 ms·메모리 60 MB(2회째, 1회째 대비 +4 %), 50명 p95 0.151 ms·p99 0.197 ms·61 MB.
- Suite `suite:stress`(카테고리 `Stress`와 이름이 같아서 접두어가 필요하다).

## Adding New Actions

1. **Handler를 쓴다.** 비슷한 파일에 `DelegateAction(new ActionSpec { ... }, RunAsync)`를 추가한다.
   - `Actions/FlowActions.cs`: 대기, 검증, 그룹
   - `Actions/ActorActions.cs`: Actor 입력
   - `Actions/ServerCommandActions.cs`: 서버 명령
   - `Actions/FaultActions.cs`: 장애 주입(QA-3)
2. **`ActionSpec`을 채운다.** Validator와 QA-2 Editor가 이것을 읽는다.

| 필드 | 의미 |
|---|---|
| `Actor` | None / Required / Optional |
| `Required` | 대안은 `"a\|b"` |
| `Optional` | 모르는 인자는 경고 |
| `PositionParams` | Marker 이름 검사 |
| `ServerCommand`, `ArrangeOnly` | D11 경고 |
| `DefaultTimeout` | 기본 Timeout |
| `Check` | 리터럴 값 검사(오류 문자열) |
| `CreatesActors` | 이 Step이 만드는 Actor |
| `NeedsProxy` | Actor에 `network.proxy`가 있어야 한다(QA-3) |
| `LaunchOnly` | attach 시나리오에서 Validation 오류(QA-3) |

3. **Handler 규칙**
   - `StepContext`로 인자를 읽는다(`String/Double/Int/Bool/Position`). 이 함수들이 `${}`를 치환한다.
   - 기다릴 때는 `ctx.TimedOut`을 보고, 자기 Timeout에 `StepOutcome.Fail(message, expected, actual)`을 돌려준다.
   - 잘못된 인자는 `QaStepException`을 던진다(Step 실패). 그 밖의 예외는 Tool 오류(종료 코드 2)가 된다.
   - 환경 때문에 할 수 없는 Step(예: Docker 없음)은 `StepOutcome.Skip(reason, skipRest)`를 돌려준다. 실패가 아니다.
   - Actor 명령은 `SendAsync` 뒤에 `ActorState.LastCommandId >= command.Id`를 확인하고 상태를 판정한다.
   - 중간에 끝날 수 있는 입력은 `finally`에서 정리한다(`moveTo`, `fire` 참고).
4. **새 Actor 의도가 필요하면** 다음 순서로 추가한다.
   - `ActorCommand` record를 추가한다(`Actors/ActorModel.cs`).
   - `HeadlessActor.Apply`에 처리를 넣는다. Pump Thread 전용이다.
   - 필요하면 `ActorState`에 필드를 넣는다.
   - `MockActor`에도 반영한다.
5. **새 Assertion 경로**는 `AssertionEngine.ResolveAsync`에 추가한다.
6. **테스트**: `Server/tests/ProjectH.QA.Tests`에 `FakeQaServer`와 `MockActor`로 추가한다. 실제 서버로 확인하는 일은 시나리오가 맡는다.

## 알려진 제한

- **`fire` 결과의 `hits`는 짧은 안정 대기(최대 500 ms) 뒤의 HitConfirmed 수다.** 지연이 큰 환경에서는 확인이 더 늦게 올 수 있다. 그때는 `waitFor actor.hitsLanded`를 쓴다.
- **위치에 계산(예: "Zone 중심 + 반경 + 5 m")을 쓸 수 없다.** 그래서 `zone_damage`는 Zone 중심이 빈 땅에 오는 Seed에 기대고 있다.
- **`build`는 한 번에 최대 8개다**(서버가 플레이어당 기다리게 하는 요청 수). 조각이 사거리 7 m 안에 있고, 시야에 있어야 한다.
- **`moveTo`는 직선 조향과 막힘 탈출만 한다.** 길찾기는 없다. 건물 안팎을 오가는 이동은 실패할 수 있다.
- **PlayerKilled의 killer는 서버가 같은 Tick의 kills 증가로 추정한 값이다**(D6). 동시 사망에서는 null이다.
