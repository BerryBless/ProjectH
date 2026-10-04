# QA Scenario Orchestrator

QA Tool은 서버를 띄우고 Headless Client(Actor)를 실제 게임 프로토콜로 움직인다. 서버의 QA Control API로 준비 상태를 만들고 서버의 권위 있는 상태를 검증한 뒤 Report를 남긴다. 시나리오는 JSON이고, 코드를 고치지 않고 작성 → 실행 → 실패 지점 기록 → 같은 Seed로 재실행할 수 있다.

- 요청: `Docs/requests/2026-10-02-qa-scenario-orchestrator-request.md`. § 번호는 이 요청서 기준이다.
- 설계: `Docs/specs/2026-10-02-qa-tool-design.md`. D 번호는 이 설계서의 결정이다.
- 현재 범위: QA-1(MVP), QA-2(Web UI, 아래 "Web UI"), QA-3(Fault Injection, 아래 "Fault Injection"), QA-4(Unity Client 자동화·Screenshot·Manual Check, 아래 "Unity Client Actor"와 "Manual Check"), QA-5(Parameter·Repeat·Seed Sweep·Baseline·Recording 재생·Load 시나리오, 아래 "QA-5"), Stress Phase A(측정 구간·Actor Group·Stress 시나리오 8개)와 Phase B(나머지 §18–49, §54–58, §65 시나리오 18개와 그 도구, 아래 "Stress").
- Stress 요청: `Docs/requests/2026-10-02-server-stress-test-request.md`. Stress 절의 § 번호는 이 요청서 기준이고, 결정은 설계서 D36–D44다.

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
        ├ BaselineHistory: QA/Reports/history/<key>.jsonl + Baseline 비교 (QA-5)
        └ Stress: measure(측정 구간), GroupRegistry(Actor Group, Workload), ActorBrain(Actor별 행동, Pump Tick마다),
                  StressMap/BuildSitePool(전투 위치·건설 Site), InputLatencyHistogram(R1), StressReport(요약·Stall·Crash)

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

### QA 모드 관측 비용 (§150, 2026-10-02 측정)

`Stress/load_bots_50.json`(봇 50명이 60초 동안 이동, DevRespawn, Debug 빌드, i9-14900K)을 `Qa:Events`만 바꿔 두 번 돌렸다. `qaCommandMs`는 QA가 Tick 안에서 쓴 시간(명령 처리 + 이벤트 비교)의 60초 평균이다.

| Qa:Events | Tick p95 | Tick p99 | qaCommandMs (Tick당) | Gen0 GC |
|---|---|---|---|---|
| false | 0.256 ms | 0.331 ms | 0.0023 ms | 0 |
| true | 0.291 ms | 0.391 ms | 0.0126 ms | 0 |

- 이벤트 비교는 Tick당 약 0.01 ms를 더한다. 30 Hz Tick 예산(33 ms)의 0.04 %다. p95·p99 차이는 같은 조건 반복 측정의 흔들림과 비슷한 크기다.
- 서버 리뷰의 Low 2건(무거운 조회가 한 Tick에 몰리는 경우, 이벤트마다 작은 할당)은 이 측정으로 수정하지 않기로 했다. 성능 측정 시나리오는 그래도 `Qa:Events=false`로 돌린다(Load 시나리오 기본값).

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
| `stress` | false | Stress D39: true면 Headless Actor만(UnityClient·captureScreenshot·manualCheck는 Validation 오류), 띄우는 서버는 `Qa:Events=false`(`server.options`가 정하면 그 값), 콘솔·UI Live Log는 Step 줄과 경고만. 아래 "Stress" |

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
| `group.<name>.<…>` | Stress: Actor Group(Tool 쪽, 서버 조회 없음) | `group.combat.hitsLanded`, `group.builders.buildResults.Ok`, `group.churn.stats.reconnectFailures`, `group.all.size` |
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
  - `actor.unity.<필드>`(screen, joined, connected, statsOpen, debugVisible, alive, health, fps, frame, tool, preview, cursorLocked)와 `network.connected`·`actor.status`가 이것을 따른다.
  - tool 값: Weapon, Harvest, Build, none(join 전). preview 값: Valid, Invalid, NoResource, none(건설 모드가 아니거나 후보 없음). cursorLocked: 게임이 보는 커서 잠금(QA 가정 포함).
  - 게임 판정은 여전히 서버 상태다(`player.*`).
  - screen 값: Title, Connecting, InGame, Menu, Disconnected, Result.
- **입력은 실제 입력 경로로만**(§87): Unity Actor의 Gameplay 입력은 `unityKey`·`unityClick`·`unityLook`만 쓴다. Player가 Input System 가상 키보드·마우스에 넣으므로 게임의 `InputReader` Binding을 그대로 지난다(Build Preview, Turbo Build, 도구 전환, 채집, 걷기 확인용).
  - Headless용 Gameplay Action(`moveTo`, `fire`, `build`, `pauseInput` 등)은 Unity Actor에 쓰면 Validation 오류다. 게임 코드에 값을 직접 넣는 경로가 없다.
  - 기다림: `async`가 없으면 입력이 끝난 뒤(`holdMs` 또는 `ms`) 50 ms(약 2 Frame)를 더 기다리고 Player 상태를 다시 읽은 뒤 PASS한다. 다음 Step이 결과를 본다. `async: true`면 Player가 받아들인 즉시 PASS한다(W를 누른 채 시점을 돌릴 때 등).
  - 기본 Timeout은 10 s + 입력 시간이다. 기다리는 Step의 `timeoutMilliseconds`가 입력 시간 + 50 ms 이하이면 Validation 오류다.
  - Player가 409(join 전), 400(잘못된 본문), 503(동시 hold·look 16개 초과)으로 답하면 Step이 FAIL한다.
  - 정리: Actor 연결을 닫을 때(disconnect, 새 connect, 실행 Cleanup) 먼저 `{"releaseAll":true}`를 보낸다(최대 1 s, 실패는 무시). up 없는 down이나 남은 async hold가 Attach한 Editor의 다음 시나리오로 새지 않는다. 시나리오 중간에는 `unityReleaseAll`로 뗀다.
- **종료**: Cleanup에서 띄운 Player의 창을 닫고, 3 s 안에 안 끝나면 그 자식 프로세스 트리만 Kill한다. Report Cleanup에 `unity <alias>` 줄이 남는다.
  - **QA Tool이 강제 종료되어도 Player는 남지 않는다(Windows).** 띄운 Player는 Kill-on-close Job Object에 들어가 있다. 도구 프로세스가 어떻게 끝나든 Windows가 Job 핸들을 닫으면서 Player를 끝낸다. 서버는 Job에 넣지 않는다(서버는 `Qa:ParentPid` 감시로 스스로 정상 종료한다).
  - Job에 넣지 못하면 로그에 경고를 남기고 계속한다. Windows가 아닌 환경도 같다. 그때는 정상 종료(Ctrl+C, UI Stop·종료)의 Cleanup만 Player를 닫는다.
- **Action**

| Action | 인자 | 동작 |
|---|---|---|
| `captureScreenshot` | actor, `name`(`[A-Za-z0-9_-]{1,64}`) | `screenshots/<Step 번호>_<alias>_<name>.png`(64자 이내).<br>Step 번호가 있어서, 영문이 아닌 Alias가 같은 문자로 바뀌거나 잘려도 파일이 겹치지 않는다. 다시 실행한 Step은 `_2`가 붙는다.<br>Report에는 썸네일과 링크로 나온다(상대 경로). UI에서 연 Report도 `/reports/<runId>/screenshots/<file>.png`로 이미지를 보인다. 실행당 최대 200장 |
| `uiCommand` | actor, `command`(openMenu, closeMenu, openStats, closeStats, toggleDebug) | 지금 화면에 맞지 않으면(409) 실패한다. 화면 이름이 메시지에 나온다 |
| `waitForUnity` | actor, `condition`(status 필드, 예: `joined`, `screen`, `unity.statsOpen`, `tool`, `preview`), 연산자 하나, `timeoutMilliseconds` | Player 상태를 직접 읽으며 기다린다 |
| `unityKey` | actor, `key`(w a s d space leftShift leftCtrl c q f z x v b t r e g 1 2 3 4 5 escape f1), `holdMs?`(1–10000), `state?`(down\|up), `async?` | 키 입력. 없으면 한 번 누름(이번 Frame 누르고 다음 Frame 뗌), `holdMs`면 그동안 누름, `state`면 누름·뗌만(뗌 없는 down은 Player가 10 s 뒤 뗀다). `holdMs`와 `state`는 함께 쓰지 않는다 |
| `unityClick` | actor, `button?`(left\|right, 기본 left), `holdMs?`, `state?`, `async?` | 마우스 버튼. 규칙은 `unityKey`와 같다 |
| `unityReleaseAll` | actor | 눌린 키·버튼과 진행 중인 시점 이동을 모두 뗀다(`{"releaseAll":true}`). 약 2 Frame 뒤 PASS |
| `unityLook` | actor, `dx`·`dy`(픽셀, 하나 이상, \|값\| ≤ 20000), `ms?`(0–5000, 기본 0 = 한 Frame), `async?` | 마우스 이동량을 `ms` 동안의 Frame에 고르게 나눠 넣는다 |

- **확인한 것**(2026-10-02, Development Build): `Smoke/unity_client.json`과 `UI/kill_feed.json`이 PASS했고 Player와 서버가 남지 않았다.
  - Player 시작부터 join까지 5~13 s 걸렸다.
  - **PC 화면이 잠겨 있으면(LogonUI) PNG는 생기지만 전부 회색이다.** 실제 화면은 잠기지 않은 데스크톱에서 확인한다.
  - 자동 이미지 판정은 하지 않는다(§85).
- **수정**(2026-10-05): Player 수신기 요청의 약 8 %(GET)·3 %(POST)가 "원격 호스트에 의해 강제로 끊겼습니다"로 실패해 Unity 시나리오가 아무 Step에서나 떨어졌다.
  - 원인: 수신기가 응답마다 `KeepAlive = false`로 자기 쪽에서 연결을 닫았고, Mono HttpListener의 그 닫기가 Windows에서 가끔 RST가 됐다. curl만으로도 재현됐다(직렬 GET 100번에 8–9번).
  - 수정: 연결을 유지한다(408·413만 닫는다). Mono가 유휴 연결을 90 s 뒤 닫고, 도구는 Actor가 끝날 때 자기 연결을 닫는다. 수정 뒤 curl 300번 실패 0, `UI/visual_screens.json` 5번 연속 PASS.
  - 도구 쪽 오류 메시지에 안쪽 예외를 붙였다(전송 오류의 실제 이유가 보인다).

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
| `UI/visual_screens.json` | 로드맵 §9 | Unity Player가 3인 경기에 들어가 HUD, Esc 메뉴, F1, Kill Feed, 결과, 전적 창(DB 꺼짐 안내), 서버 Crash 뒤 재접속 화면, 같은 Port로 다시 뜬 서버에 재입장한 화면을 찍는다 | ~20 s |
| `Building/visual_building.json` | 로드맵 §10 | Unity Player가 15 m 앞의 건설을 본다. 실제 입력으로 짓는 나무 벽(짓는 중 → 완성), 돌·나무·금속 벽, 바닥·경사로·지붕, 자원 줄(1600×900), 실제 사격 피해 두 단계, 지지벽 파괴 뒤 다리 붕괴(먼지)와 그 뒤를 찍는다 | ~17 s |
| `Building/unity_build_input.json` | 로드맵 §10, §87 | Unity Player를 가상 키보드·마우스로 움직인다(`unityKey`·`unityClick`·`unityLook`). W 걷기, Q 건축 모드(Player·서버 둘 다 Build), Z 벽 → 미리보기 Valid, 클릭 배치(서버 조각 1, 나무 차감) → 같은 자리 Invalid, 자기 벽에 막힘, F 채집(나무 증가), 버튼을 누른 채 회전하는 Turbo(조각 증가), 1 무기 | ~15 s |
| `Manual/ime_name.json` | 88–90 | 한글 IME 이름 입력 Manual Check 2개(CI에서는 SKIPPED) | 사람 |
| `Manual/editor_ui.json` | 로드맵 §9 | Editor 수동 검증(Phase 11 UI). 서버를 127.0.0.1:7777(Editor 기본 주소)에 띄우고 Headless 상대 2명을 둔다. 사람이 Editor로 접속해 타이틀·한글 글꼴·IME·접속·이름·ESC 메뉴·F1·Kill Feed·결과·전적·끊김·재접속을 PASS/FAIL로 답한다. Kill Feed와 결과 화면은 상대를 `killPlayer`로 제거해 만든다. 끊김은 `stopServer`, 재접속은 같은 Port로 `startServer` | 사람 |
| `Manual/editor_building.json` | 로드맵 §10 | Editor 수동 검증(Phase 13 건설). DevRespawn 서버를 7777에 띄우고 QA_Build_Test 근처에 돌·나무·금속 벽과 두 칸 다리를 놓는다. 사람이 채집·미리보기(유효/무효)·벽·바닥·경사로·지붕·Turbo·건설 중 표시·충돌·피해를 확인한다. 마지막에 도구가 다리 밑 돌벽을 부수고(`damageBuild`) 붕괴 연출을 묻는다 | 사람 |
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

Stress Test 시나리오(baseline, movement, combat, building, mixed_match, reconnect_churn, final_zone, soak, soak_match_reset)는 아래 "Stress"에 있다.

**Suite**(`QA/Suites/`)

| Suite | 내용 | 시간 |
|---|---|---|
| `smoke` | connect, move_to, basic_hit | ~16 s |
| `pre-push` | §93: connect, move, shoot, pickup, death, reconnect | ~30 s |
| `full-regression` | 모든 카테고리. Stress, Persistence, ServerProcess, Recorded 포함 | ~6 min |
| `faults` | QA-3: latency_loss_combat, lag_compensation, network_drop, invalid_packet, input_timeout, shutdown, restart, db_down | ~1 min |
| `stress` | QA-5 D35: Load 파일 3개(bots_50, load_bots_10, load_bots_50). 같은 이름의 카테고리가 있으므로 `suite:stress`로 부른다. Stress 시나리오는 아래 `stress-*` | ~3.2 min |
| `stress-quick` 외 6개 | Stress 시나리오 Suite. 아래 "Stress"의 "Suite" | 4.5 min ~ 1 h 이상 |

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
| `--set name=value` | Stress: 시나리오 변수를 덮어쓴다(여러 번 가능). `variables`와 파라미터 세트보다 우선한다. 시나리오에 없는 이름은 오류(종료 코드 2). 값은 숫자·true/false면 그 타입, 아니면 문자열. 예: `--set soakSeconds=1800` |

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
| Batch | QA-5: 묶음 실행의 실행별 줄(번호, 반복, 파라미터 세트, Seed, 결과, Report)과 요약. Stress D40: 측정한 실행이 있으면 Players, Tick P50/P95/P99/Max, CPU, Managed·WS MB, GC, Send/Recv KB/s, DB Queue, Build, Stalls, R1 P95 열이 붙는다 |
| Reports | 최근 실행 50개의 `report.html` 링크 |

- Server 탭은 Stress D36의 `/qa/metrics` 값(bytes/s, managed·할당·GC pause, alive, build pieces, stalls)도 보인다.
- Stress 시나리오(`"stress": true`) 실행 중에는 Inspector가 서버를 View마다 5초에 한 번만 조회하고(브라우저는 1초마다 묻지만 그 사이에는 캐시를 돌려준다), Live Log는 Step 줄과 경고만 보인다(D39).

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
- Stress: 헤더 바로 아래 Stress Summary, Measure phases 표와 구간별 Sample, Stalls, Crash, Groups(아래 "Stress")

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
| GET | `/qa/status` | — | `{ok, devPlayerId, connected, joined, screen, statsOpen, debugVisible, alive, health, fps, frame, tool, preview, cursorLocked}` |
| POST | `/qa/screenshot` | `{"name":"[A-Za-z0-9_-]{1,64}"}` | 파일이 생긴 뒤 `{ok, path}`. Windows 장치 이름(CON, NUL, COM1…)은 400 |
| POST | `/qa/ui` | `{"command":"openMenu\|closeMenu\|openStats\|closeStats\|toggleDebug"}` | `{ok, command, screen, statsOpen, debugVisible}`. 지금 화면에 맞지 않으면 409이고 아무것도 바뀌지 않는다 |
| POST | `/qa/input` | 하나만: `{"key":"q"}`, `{"key":"w","holdMs":1500}`, `{"key":"w","action":"down\|up"}`, `{"button":"left\|right"}`(같은 `holdMs`·`action`), `{"lookX":120,"lookY":-30,"ms":300}`, `{"releaseAll":true}`(join 전에도 200). 숫자는 JSON 숫자 | Input System에 넣은 뒤 `{ok, applied}`(hold는 그 뒤에도 이어진다). 400 잘못된 본문·허용 밖 키, 409 join 전, 503 동시 hold·look 16개 초과. 도구의 `unityKey`·`unityClick`·`unityLook`이 보낸다(도구 인자 `state`가 본문의 `action`이 된다) |

- 모든 POST에는 `Content-Type: application/json`이 있어야 한다. 없으면 415다(브라우저 CSRF 방지).
- 그 밖의 오류 코드:
  - 400: 잘못된 JSON·이름·명령
  - 403: loopback이 아님
  - 404, 405(OPTIONS 포함)
  - 408: Body가 5초 안에 오지 않음
  - 413: Body가 16 KB를 넘음
  - 503: 32개 처리 중, 큐가 가득 참, 종료 중
  - 504: 5초 안에 답이 없음
- 연결은 유지된다(Keep-Alive). 408·413 응답 뒤에만 수신기가 연결을 닫는다(2026-10-05 수정, 위 "Unity Client Actor"의 수정).
- Gameplay 입력은 `/qa/input`뿐이다(§87). 게임 코드에 값을 넣지 않고 Input System 가상 장치로 넣어 `InputReader` Binding을 그대로 지난다.
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

## Stress

서버 Stress Test다(요청 `Docs/requests/2026-10-02-server-stress-test-request.md`, 설계 D36–D44). 새 Framework를 만들지 않고 QA Tool을 넓혔다: 측정 구간 Action(`measure`) 하나, Actor Group Action 몇 개, 시나리오 최상위 `"stress": true`, Report·Batch의 Stress 표. Phase A는 우선순위 시나리오 8개(§108–116)와 그 도구다. Phase B는 나머지 시나리오 18개(§18–49 Lag Compensation별, Turbo Build, Build Count, 파괴, Loot, Zone, Elimination Burst, Join Ramp·Spike, Disconnect Spike, Input Timeout, Invalid Packet, Build Spam, Network Fault Mixed, DB 2개, §56–58 Hotspot 비교, §65 반복 Restart)와 그것에 필요한 Action 5개(`spawnBuildPieces`, `groupFireAt`, `groupConnect`, `groupInvalidPackets`, `groupBuildSpam`)다. 서버 코드는 바꾸지 않았다.

**답하려는 질문**(요청서 첫머리): 몇 명까지 안정적인가, 어떤 Gameplay가 가장 비싼가, Tick이 언제 무너지는가, 무엇이 먼저 병목인가, Churn·장시간에서 누적이 있는가. 판정은 측정값으로만 한다(§122: 측정하지 않은 값을 추측하지 않는다).

### 실행

```bash
dotnet build Server/ProjectH.Server.slnx                       # 서버와 도구를 먼저 빌드
dotnet run --project Server/src/ProjectH.QA -- run suite:stress-quick                                  # 50명, 5개, 약 4.5분
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Stress/baseline.json                   # 10/25/50/75/100명 묶음 + 비교표
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Stress/mixed_match.json --parameter-set 2   # 100명 한 번
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Stress/combat.json --parameter-set 3 --set steadySeconds=120
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Stress/soak.json --set soakSeconds=1800   # 30분 Soak
```

- 실행은 언제나 순차다. 같은 PC에서 Tool(Actor Pump)과 서버가 함께 돈다. Report의 "QA tool CPU"가 Tool 몫이다.
- 파라미터 묶음(Player Count)이 끝나면 콘솔에 비교표가 나오고 `QA/Reports/batch-<id>/summary.html`·`summary.json`이 생긴다(D40). Web UI Batch 탭도 같은 열이다.
- 서버 빌드 구성(Debug/Release)은 Report의 Server command에 나오는 DLL 경로로 안다. 숫자는 같은 PC·같은 구성끼리만 비교한다(§125).

### Stress 모드 (`"stress": true`, D39)

- Headless Actor만 쓴다. UnityClient Actor, `captureScreenshot`, `manualCheck`, `uiCommand`, `waitForUnity`, `unityKey`, `unityClick`, `unityLook`, `unityReleaseAll`은 Validation 오류다(§2, §122).
- 띄우는 서버는 `Qa:Events=false`다(QA Tick diff 비용을 빼기 위해, §150). `server.options`에 `Qa:Events`가 있으면 그 값을 쓴다. 이벤트가 꺼진 시나리오의 `waitForEvent`·`event.*`는 Validation 경고다.
- 콘솔(`--verbose` 포함)과 UI Live Log에는 Step 줄과 경고(warn/fail/error/exception/crit가 든 줄)만 나온다. Report의 서버 로그 Tail은 전부 남는다.
- UI Inspector는 서버를 View마다 5초에 한 번만 조회한다(§87-88).

### 측정 구간: `measure` (D37, D41, D43)

`{ "action": "measure", "name": "steady", "seconds": 60, "sampleSeconds"?: 5, "saveAs"?: "steady" }`

- 시작에 `/qa/metrics`(누적값: GC, 할당, Stall, DB)를 한 번 읽고, `sampleSeconds`(기본 5 s, 1–120)마다 `/qa/metrics?windowSeconds=<간격>`을 하나씩 읽는다. Actor별 HTTP 조회는 없다.
- Sample은 최대 720개다. 긴 구간은 간격을 늘린다(4시간·5초 → 20초).
- 구간이 120 s(서버 Tick Ring) 이하면 끝에 `windowSeconds=<구간>`으로 한 번 더 읽어 그 구간 전체의 Tick p50/p95/p99/max, CPU, 패킷·바이트 평균을 정확히 낸다. 120 s보다 길면 Sample 창들로 근사한다: p50은 Sample p50의 평균, p95/p99/max는 가장 나쁜 Sample 창의 값이다(p95/p99를 실제보다 좋게 보이지 않는다). Report에 `*`와 설명이 붙는다.
- 구간 결과: Players·Sessions·Alive min–max, Tick 4개, 서버 CPU 평균/최대(모든 논리 프로세서 = 100 %), Working Set·Managed 시작/끝/최대, GC 0/1/2 증가, 할당 MB(MB/s), GC Pause ms, Send/Recv KB/s(UDP payload, KB = 1024 B), 패킷/s, QA ms/Tick, Build Piece 시작–끝, DB Queue 최대·저장·실패 증가, Stall·Tick Failure·Bad Packet 증가, 이 구간에 Group이 보낸 Arrange 명령 수, R1, QA Tool CPU(같은 단위와 Core 수).
- `name`은 자유다(warmup, steady, cooldown, match_01...). Report 맨 위 **Stress Summary**는 `steady` 구간, 없으면 가장 긴 구간(같은 길이면 마지막)이다(§103).
- **경고(D42)**: warmup이 아닌 구간의 tickMax가 33 ms(30 Hz Tick 예산)를 넘으면 Warning이다. 실패가 아니다. 이번 시나리오들은 Hard Limit이 없다(§74–75). FAIL은 Crash, Hang(QA API 무응답), 접속 수 감소, Tick Failure, Stall, 시나리오 Assertion 실패다(§81).
- **Stall(D41)**: Sample에서 서버 `health.stalls`가 늘면 그 시각, 구간, Step, Player 수, 그 Sample, 최근 이벤트(이벤트가 켜져 있을 때만)를 Report "Stalls"에 남기고 Warning을 낸다(최대 50개).
- **Crash(D41)**: 띄운 서버가 시나리오의 stopServer/killServer 없이 끝나면 Exit Code, 그때의 Step, Actor 수(Joined 수), 마지막 Sample과 구간, 서버 로그 Tail을 Report "Crash"에 남기고 FAIL로 끝낸다. 측정 중이면 그 `measure`가 바로 실패한다.
- 취소(Ctrl+C, 시나리오 Timeout)되면 그때까지 잰 구간을 "(cut short)"로 남긴다.
- `saveAs`에는 Sample을 뺀 구간 숫자가 들어간다(`var.steady.tickP95Ms`, `var.steady.playersMin`, `var.steady.stalls`...). Baseline 기록에는 Stress Summary의 Tick p95/p99, CPU, Managed, Working Set, Send KB/s가 Warning 대상으로, 나머지가 정보로 들어간다(§79, D42).

**R1 입력 지연(D43)**: Headless Actor가 입력을 보낸 시각을 Seq별로 연결당 256칸 Ring에 두고, Snapshot의 `AckInputSeq`가 그 입력을 확인하면 지연을 Run 전체 Histogram(1 ms 칸 2000개, Pump Thread가 기록)에 더한다. 구간마다 p50/p95/p99/max와 RTT를 뺀 값을 낸다. 서버가 다음 Tick에 입력을 쓰는 시간, 15 Hz Snapshot 간격, Actor Pump의 약 33 ms Tick 해상도가 모두 들어 있다. 서버 Tick 비용이 아니라 Client가 본 왕복 지표다. 입력이 하나도 확인되지 않은 구간은 "Not Available"이다.

### Actor Group (D38)

Group 행동은 Actor Pump가 Tick마다 실행하는 `ActorBrain`이다. Actor별 Thread도, Actor별 HTTP 조회도 없다. Brain은 Actor의 의도(이동 목표, 조준, 누름, 건설 요청)만 정하고, 실제 입력은 기존 Action과 같은 코드(Steering, 무기 Fire Interval을 지키는 FirePress, Build 모드·조준·요청 순서)가 만든다. 결정은 시나리오 Seed와 Actor 번호에서 나온다(같은 Seed = 같은 선택. 세계의 타이밍은 실행마다 조금 다르다). Group의 Arrange 명령(위치, 무기, 자원)은 동시에 8개까지 QA API로 보내고 503/504는 두 번 다시 보낸다.

| Action | 인자 | 동작 |
|---|---|---|
| `actorGroup` | `groups: [{name, percent \| count \| rest, proxy?}]`, `prefix?`, `seed?`(기본 Run Seed) | Headless Actor(이름 순)를 Seed로 섞어 Group에 나눈다. 비율은 최대 잔여 방식으로 반올림(33/33/34 % of 10 = 3/3/4), `rest`는 남은 전부, 어느 Group에도 없는 Actor도 있을 수 있다. `proxy: true` Group은 Fault Proxy를 쓴다(`connectAll` 전에 만든다). saveAs = `{actors, seed, groups{name: 수}, ungrouped}` |
| `groupMove` | `group`, `pattern`(mixed·clockwise·counterClockwise·radial·random, 기본 mixed), `center?`, `radius?`(60), `sprint?`, `sprintPercent?`, `jumpEverySeconds?` | Waypoint를 계속 걷는다. Actor마다 시작 각도와 원 반지름이 달라 한곳에 모이지 않는다. mixed는 번호 순으로 4가지 패턴(§12). 건물 안 Waypoint는 건너뛰고, 제때 못 가거나 Steering이 포기하면 다음으로 간다 |
| `groupCombat` | `group`, `pairing`(pairs·groups), `groupSize?`(groups일 때 3), `weapon?`(Vesper AR), `ammo?`(300), `burst?`(3), `pauseMs?`(2000), `reloadEvery?`(5), `hitPercent?`(25), `engageRange?`(40), `strafeSeconds?`(1.5), `arrange?`(true), `rearm?`(true), `center?`, `radius?`(70, 0 초과 120 m 이하), `spacing?`(12, 2–50 m), `pairDistance?`(10, 2–50 m), `shield?`(100) | 멤버를 짝(또는 고리)으로 묶어 서로 쏜다. 한 사람이 모두의 목표가 되지 않는다(§17). Arrange: 맵 데이터로 고른 빈 자리(서로 보이고 설 수 있는 곳)에 마주 보게 놓고 무기·탄약·실드를 준다. 행동: 짝을 조준(자기 Snapshot), 멀거나 안 보이면 걸어간다, 좌우 Strafe, `burst`번 단발(무기 Fire Interval 준수) 후 `pauseMs` 쉼, `reloadEvery`번마다 Reload. `hitPercent`만 가슴을, 나머지는 머리 위 3 m를 조준한다(빗나간 탄도 Hitscan·Lag Compensation을 거친다. 사망률을 장시간 버틸 수 있게 낮춘다) |
| `groupBuild` | `group`, `pieces?`(wall·floor·ramp·roof), `material?`(wood), `ratePerSecond?`(1, 최대 10), `recycle?`(false), `arrange?`(true), `resources?`(500), `center?`, `weapon?`, `ammo?`, `role?` | 멤버마다 자기 Site(맵의 평평하고 빈 3×2 셀, 서로 겹치지 않음. Run의 Site Pool에서 가장 적게 잡힌 가까운 곳을 받는다. 가지 못해 포기한 Site는 Pool에 돌려준다. 모두 잡혀 있으면 함께 쓰고 그 수를 `group.<name>.buildSites.shared`로 보인다)를 가져 그 남쪽 칸에 서서 짓는다. Site당 13조각: 양옆 Ramp, 바닥, 서·동·북 벽 2층, 지붕, 2층 바닥, 남쪽 벽 2층(서버의 시야 검사를 위해 먼 것부터). `recycle`이면 다 지은 Site를 자기 무기로 실제 사격해 1층 조각을 부수고(위층은 서버 지지 규칙으로 붕괴) 다시 짓는다. 4/s 이상은 role `turbo` |
| `groupLoot` | `group`, `searchRange?`(40), `dropEvery?`(3) | 아는 아이템으로 걸어가 Interact, 몇 번 주울 때마다 Drop, Medkit이 있고 체력이 덜 찼거나 Shield Cell이 있고 실드가 덜 찼으면 사용 버튼(4 s에 한 번). 없으면 주변을 걷는다. Phase B: Group 통계에 `lootPickups`(간 아이템이 사라짐), `lootDrops`, `lootUses`(사용 누름. 서버가 거절한 누름도 센다) |
| `groupRoles` | `group`, `roles: [{role, percent}]`(move·sprint·jump·idle·loot·build·turbo), `switchSeconds?`(20), `center?`, `radius?`, `ratePerSecond?`, `turboRatePerSecond?`, `resources?` | 멤버마다 `switchSeconds`마다 역할을 바꾼다. 역할은 (Seed, 번호, 구간)으로 정해지는 가중 선택이다(§52). 전투는 짝이 필요해서 역할에 없다 |
| `groupChurn` | `group`, `percent?`(15), `cycleSeconds?`(8), `cycles?`(20), `mode?`(graceful·drop·mixed), `offlineMs?`(500), `reconnectTimeoutMs?`(15000) | Background Workload. 매 Cycle Seed로 고른 비율이 끊기고(mixed는 반은 정상 종료, 반은 끊김 패킷 없이), 서버가 옛 연결을 놓을 때까지 `/qa/players`를 한 번씩 읽어 기다린 뒤 같은 DevPlayerId로 다시 접속한다. 재접속 시간, 실패, 서버 목록의 중복 DevPlayerId를 센다. Proxy Actor는 쓸 수 없다 |
| `groupNetworkFault` | `group`, `latencyMs?`, `jitterMs?`, `lossPercent?`, `duplicatePercent?`, `direction?` | `proxy: true` Group의 모든 멤버 Proxy에 같은 장애 |
| `groupFireAt` | `group`, `at`(위치, y를 주면 그 높이), `presses?`(10, 최대 500), `burst?`(3), `slot?`(0) | Phase B: 서서 한 점을 조준하고 무기 Fire Interval을 지키는 단발 `presses`번을 쏜다(필요하면 슬롯 키, 탄창이 비면 Reload). 실제 사격이라 무엇에 맞는지는 서버 Hitscan이 정한다. measure와 함께 돌게 Pump의 Brain으로 쏜다(build_destruction의 Foundation 파괴) |
| `groupConnect` | `group`, `perSecond?`(0 = 한 번에, 최대 100), `timeoutMs?`(15000) | Phase B: 아직 게임에 없는 멤버를 멤버 순서로 초당 `perSecond`명씩(0이면 한 번에) 접속시키는 Background Workload. Step은 바로 끝나서 뒤의 measure가 Ramp 동안 잰다. 첫 Snapshot까지를 `connects`·`connectMsAvg/Max`로, 닫히거나 시간 안에 Snapshot이 없으면 `connectFailures`로 센다(다시 시도하지 않는다). 접속 전에 준 Brain(groupMove)은 접속 뒤에도 남는다 |
| `groupInvalidPackets` | `group`, `ratePerSecond?`(5, 최대 200), `kinds?`(unknownId·truncated·garbage, oversized 가능), `rejoin?`(true) | Phase B(D38): 정상 입력을 계속 보내면서 같은 연결로 `sendInvalidPackets`와 같은 바이트를 초당 `ratePerSecond`개(Actor별 Seed, Tick당 최대 20개) 보낸다. 서버는 하나하나 badPackets로 세고 20개에서 그 연결을 Kick한다. `rejoin`이면 Rejoin Workload가 Kick된(서버가 닫은) 멤버를 1 s 뒤 새 플레이어로 다시 접속시킨다. 통계 `abuseSent`, `kicks`, `rejoins`. `inputFlood`는 초당 입력 한도를 보는 것이라 지속 Stream으로 쓰지 않는다(Validation 오류) |
| `groupBuildSpam` | `group`, `ratePerSecond?`(15, 최대 200), `material?`, `resources?`(500, Arrange giveResource), `rejoin?`(true) | Phase B(D38): Build 모드를 켜고(Q) 조준 Tick·간격 없이 Build Request를 초당 `ratePerSecond`개 바로 보낸다(주변 셀의 무작위 벽·바닥·Ramp, Actor의 요청 번호를 써서 응답이 Code별로 `buildResults`에 쌓인다). 20/s(maxRequestsPerSecond) 넘는 요청은 서버가 Bad Packet(BuildRate)으로 세고 Kick한다. Rejoin은 groupInvalidPackets와 같다 |
| `stopGroup` | `group`(이름 또는 `all`) | Workload를 끝내고(최대 10 s, 기본 Step Timeout 20 s) 행동을 지운다(멤버는 서서 입력만 보낸다). saveAs = 통계. 그 Group의 Workload가 stop 전에 오류로 끝났으면 실패다(측정 중 일부를 일하지 않았다는 뜻) |

- **새 행동은 옛 Workload를 대신한다**: 행동 Action(groupMove·Combat·Build·Loot·Roles)은 그 Group의 Re-arm Workload를 먼저 끝내고, 새 groupChurn은 옛 Churn을 끝낸다. Churn은 다른 행동과 같이 돈다.
- **Churn의 실패**: `/qa/players`는 503/504·자체 Timeout에 두 번 다시 묻는다. 그래도 실패하거나 다른 오류로 Churn이 끝나면, 어느 경로든(정지·오류) 떠나 있던 멤버를 다시 접속시킨 뒤 끝낸다(최대 8 s: 패킷 없이 끊긴 Peer를 서버가 놓는 DisconnectTimeoutMs 기본 5 s보다 길게). 오류로 끝난 Workload는 stopGroup 실패와 Run 경고로 남는다.
- **Workload와 정리**: Re-arm(전투·Recycle 건설)과 Churn은 Runner 쪽 Background Task다. Actor State만 읽고 Actor 명령과 QA 명령만 보낸다(RunContext는 건드리지 않는다). Run당 Workload 최대 64개, Group 최대 32개. Run이 끝나면 Cleanup이 다른 정리보다 먼저 모든 Workload를 끝낸다(Report Cleanup의 `groups` 줄). Ctrl+C·Timeout에도 같다.
- **Re-arm(Arrange)**: 부활한 멤버의 무기 칸이 비면 무기·탄약(전투 Group은 자기 자리도)을 다시 주고, 예비 탄약이 60발 아래면 채운다. 이 QA 명령은 실제 게임이라면 줍기로 얻는 것을 대신하는 Arrange이고, 측정 구간마다 "Arrange cmds"로 세며 서버 `qaCommandMs`에 들어간다. 피해·사망 자체는 언제나 실제 사격이다(`damagePlayer` 없음).
- `group.<name>.size·joined·alive·role·workloads·pressesSent·hitsLanded·damageTaken·buildResults.<Code>·stats.<…>`로 검증한다(stats: arrangeCommands, commandFailures, rearms, deaths, refills, churnCycles, disconnects, reconnects, reconnectFailures, reconnectMsAvg/Max, duplicates, faultsSet, errorCount, errors).
  - Phase B에서 더한 값: `rttMsAvg`·`rttMsMax`(접속 중인 멤버 연결의 RTT), `healthMin`·`healthAvg`(살아 있는 멤버가 자기 Snapshot에서 본 체력. Zone 피해는 DamageTaken 패킷이 없어서 `damageTaken`에 안 잡히고 이것으로 본다), `kicked`(마지막 연결이 Kicked로 닫힌 멤버 수), stats의 `lootPickups·lootDrops·lootUses`, `connects·connectFailures·connectMsAvg/Max`, `abuseSent·kicks·rejoins`.
- **Phase B Workload**: groupConnect(`connect`)와 Abuse Group의 Rejoin(`rejoin`)도 Re-arm·Churn과 같은 Group Workload다. 같은 상한(Run당 64개)과 정리(stopGroup·Cleanup이 먼저 끝냄)를 따르고, Actor State만 읽고 Actor 명령만 보낸다(Rejoin은 QA 조회도 하지 않는다). 새 행동은 같은 종류의 옛 Workload를 끝내고 시작한다.

### 경기 반복: `matchLoop` (Soak, §59–64)

`{ "action": "matchLoop", "matches": 1-500, "matchSeconds": 1-240, "sampleEveryMatches"?: 1 (1-500), "sampleSeconds"? (1-120), "startTimeoutMs"?: 30000 (1000-600000), "finishTimeoutMs"?: 10000 (1000-600000), "trendMatches"?: 5 (2-100), "trendPercent"?: 10 (0-1000), "saveAs"? }`

- Step을 복사하지 않고 경기를 반복한다. 경기마다: 경기가 Playing이 될 때까지 기다림 → `matchSeconds` 동안 measure 구간 하나(`match_01`…, 100경기 이상이면 `match_001`…; Report에는 앞의 200개 구간만 남는다) → `forceMatchState finish`(흐름 제어 Arrange, 503/504 두 번 재시도) → 경기가 끝나고 다음 경기가 Playing이 될 때까지(서버의 Reset·Countdown) → `/qa/metrics` 한 번 → 추세 행 하나.
- 그 사이 Group Workload(Re-arm, Churn, Rejoin…)와 Brain은 그대로 돈다(Loop는 건드리지 않는다).
- 추세 행(`MatchRow`, Report의 `stress.matchLoop.rows`, 최대 520행, 나머지는 개수만): "start"(Loop 전)와 경기마다(`sampleEveryMatches`마다, 이정표와 마지막 경기는 항상). 값: 경기 끝(Reset 뒤)의 Managed·Working Set, 그 경기 구간의 Managed 최소, **Post-GC floor**, GC 0/1/2(누적과 경기 안 증가), 할당 MB, GC Pause, 그 경기 구간의 Tick p95/p99/max, Build Piece(놀이 끝), Session·Player·끝의 생존자.
- **Post-GC floor**: 한 경기의 범위(그 measure 구간의 시작 읽기·Sample·Reset 뒤 읽기) 안에서 GC가 일어난 뒤(누적 gen0+1+2가 늘어난 뒤)의 가장 낮은 Managed MB. 그 범위에 GC가 없으면 비어 있다. 5 s Sample이라 GC 직후 값을 놓칠 수 있다(그 경우 floor는 실제보다 높게 나온다).
- **추세 경고(D42, 실패 아님)**: GC가 있었던 경기들의 마지막 `trendMatches`(기본 5)개 floor가 경기마다 매번 올라가고, 마지막 floor가 그중 첫 floor보다 `trendPercent`(기본 10) %보다 많이 높으면 Run 경고 한 번(`Stress: matchLoop post-GC managed floor rose ...`)과 Report 표 위의 Trend warning. 한 번이라도 내려가면 경고하지 않는다. 경고는 누적을 확인하라는 신호이지 Leak 판정이 아니다.
- Report HTML "Match loop trend": start와 이정표(경기 1/5/10/25/50/100/250/500 뒤)·마지막 경기의 작은 표, 그 아래 접힌 전체 행 표. saveAs = `{matches, matchesDone, rows, rowsDropped, trendWarning, first, last, milestones}`.
- **`matchSeconds` 상한 240 s**: 경기는 Zone이 닫히면 스스로 끝난다(zones.json의 Phase wait + shrink 합계 285 s, AirDrop이면 수송기 경로가 끝난 뒤부터). 그보다 짧게 둬서 보통은 Loop의 finish가 경기를 끝낸다.
- **스스로 끝난 경기**: finish 전에 `/qa/match`를 읽어 같은 Round(`round`)가 아직 Playing인지 본다. 아니면(Zone, 마지막 참가자, 이미 다음 Round 시작) finish를 보내지 않고 그 행에 `endedNaturally`를 표시하고 계속한다(Loop 합계 `endedNaturally`). 끝남은 "Playing이 아님 또는 Round가 바뀜"으로 기다린다.
- **finish 재시도**: 503/504는 두 번 다시 보낸다. 504는 서버에서 이미 실행됐을 수 있어서, 거절(409 등)이 오면 `/qa/match`를 다시 읽고 그 Round가 더 이상 Playing이 아니면 끝난 것으로 본다(504 뒤면 Loop가 끝낸 것, 아니면 스스로 끝난 것).
- **구간 상한**: Loop는 Report의 measure 구간을 150개(`MatchLoopKeptPhases`)까지만 채우고 그 뒤 경기는 행만 남긴다(`phasesNotKept`). 그래서 뒤의 measure Step(cooldown 등)도 자리가 있다. Report 전체 상한(200)을 넘은 구간은 `stress.phasesDropped`로 세고 report.html "Measure phases"에 표시한다(전에는 말없이 버렸다).
- **Tick 예산 경고**: Loop 안의 경기 구간은 구간마다 경고하지 않고, 끝에 "N of M match phase(s) had a tickMax over the 33 ms" 경고 하나로 모은다(Run 경고 200개 상한이 추세 경고를 밀어내지 않게).
- **Timeout**: Step 기본 Timeout = 경기 수 × (matchSeconds + 2 × startTimeoutMs + finishTimeoutMs + 10 s) + 30 s. Validator는 `matches × (matchSeconds + 15 s)`(15 s = finish·Result 10 s·Countdown 3 s·대기의 추정치)가 시나리오 `timeoutSeconds`보다 크면 경고한다(리터럴과 변수 기본값으로 계산하고 `--set` 값은 보지 못한다).
- 실패: 경기가 시간 안에 Playing이 되지 않음, 끝나지 않음, finish 거절(그 Round가 아직 Playing인데), 서버 무응답. 그때까지의 행은 Report에 남는다.

### 대량 Build Piece: `spawnBuildPieces` (Phase B, §24–29)

`{ "action": "spawnBuildPieces", "count": 20010, "layout": "field" | "hanging", "center"?: {x,z}, "side"?: 3-10, "material"?: "wood", "parallel"?: 1-16, "saveAs": "b" }`

- 새 서버 경로가 아니다. 조각마다 기존 QA 명령 `spawnBuildPiece`(`/qa/command` 하나)를 보낸다(§24, §122). 동시에 최대 16개(서버가 Tick마다 QA 명령 16개를 실행하고 큐는 64개). 503/504는 두 번 다시 보낸다. Arrange 명령이라 arrange가 아닌 phase에서는 D11 경고다.
- Layout(`Stress/BuildLayouts.cs`, 순수·결정적)은 Wave의 목록이다. 같은 Wave의 조각은 서로 기대지 않고 앞 Wave의 조각이나 땅에만 기댄다. 그래서 Wave 안은 병렬, Wave 사이는 순서대로 보낸다.
  - `field`: `center`에 가까운 셀부터 64셀씩 묶어 기둥을 세운다. 기둥은 셀 중심 아래 지형 Level에서 시작하고(그 바닥은 땅에 묻히거나 닿아 Grounded), Level마다 바닥 → 그 남·서쪽 벽, 다음 Level 바닥은 그 벽 위에 선다. 최대 32×32셀 × 16 Level × 3 = 49,152개라 Match 예산(20,000)보다 많다. 거절(Occupied·Unsupported)된 조각은 다시 보내지 않고 다음 조각으로 `count`를 채운다. `BudgetFull`이 오면 멈춘다.
  - `hanging`: 정확히 `count`개로 된 구조물 하나. 한 셀의 남쪽 벽 기둥(맨 아래 벽 = Foundation, 땅에 닿는 유일한 조각) 위에, 그 아래 가장 높은 땅보다 두 Level 위에서 시작하는 `side`×`side`셀 블록(바닥, 그 위 남·서쪽 벽, 다음 바닥…)을 얹는다. Foundation 말고는 서버의 Grounded 규칙(`BuildSupport.IsGrounded`를 따라 쓴 `BuildLayouts.WouldRest`)으로 땅·맵 상자에 닿지 않는 것을 확인하고, Foundation 남쪽 4 m에 사수가 설 수 있고 Foundation 중심이 보이는 자리만 고른다. 하나라도 거절되면 Step이 실패한다(구조물이 불완전).
- saveAs: `{requested, placed, serverPieces(/qa/metrics buildPieces), budgetFull, refused{Code: n}, errors(앞 5개), layout, seconds, perSecond, structure?{pieceId(Foundation), foundation{x,y,z}(Foundation 중심), shooter{x,y,z}, side, firstLevel}}`.
- 서버는 QA 명령마다 Information 로그 한 줄을 쓴다. 그래서 시나리오는 이 Step을 측정 구간 밖에 둔다.

### 시나리오 (`QA/Scenarios/Stress/`, D44)

모두 `"stress": true`, Tag `stress`, `server`. 구간 길이는 변수(`warmupSeconds`, `steadySeconds`, `cooldownSeconds`)이고 `--set`으로 바꾼다. 공통 순서(§98): Spawn → 접속 → (Arrange) → Group 행동 → `mark warmup_start` → warmup → `mark measurement_start` → steady → `mark measurement_end` → 검증(server.running, 기대 Player 수, Stall 0, Tick Failure 0, 행동이 실제로 일어났는지) → `mark cooldown` → stopGroup → 퇴장 → cooldown → 모두 나갔는지.

| 파일 | § | 내용 | 서버 설정 | Players |
|---|---|---|---|---|
| `baseline.json` | 10, 109 | 접속만(Keepalive 입력) | DevRespawn, MaxPlayers 100 | 10/25/50/75/100 |
| `movement.json` | 11–13, 110 | 4가지 이동 패턴, 40 % Sprint, 6 s마다 Jump | DevRespawn | 10/25/50/75/100 |
| `combat.json` | 14–17, 111 | 모두 짝 전투(Vesper AR, 25 % 명중 Burst) | DevRespawn | 10/25/50/75/100 |
| `building.json` | 20–23, 112 | 60 % 건설(1/s), 10 % Turbo(6/s), 둘 다 Recycle, 30 % 걷기 | DevRespawn, BuildInfiniteResources(이유는 description: 자원 상한 500 = 나무 50조각 < 60 s 구간) | 10/25/50(맵의 평평한 Site가 약 40개라 75/100은 Site를 나눠 쓰게 된다) |
| `mixed_match.json` | 50–53, 113 | 전투 30 %, 건설 10 %, Turbo 10 %, 고지연 10 %(양방향 50 ms), Churn 5 %, 나머지는 걷기·달리기·줍기 역할 전환 | DevRespawn, BuildInfiniteResources | 50/100 먼저, 10/25/75 |
| `reconnect_churn.json` | 36–38, 114 | 경기 중 6 s마다 15 % 끊김(반은 패킷 없이) → Grace 안 재접속 × 20, 모두 걷기. Zone이 사람을 죽이기 전(약 2분)에 끝낸다 | 경기(Grace), StartCountdownSeconds 8, DisconnectTimeoutMs 2000 | 25/50/100 |
| `final_zone.json` | 54–55, 115 | 반경 25 m에 몰림: 전투 50 %(6 m 짝), 건설 20 %, 나머지 달리기 | DevRespawn, BuildInfiniteResources | 30/40/50 |
| `soak.json` | 59–64, 116 | mixed 작업을 `soakSeconds`(기본 300) 동안, 10 s Sample. start/soak/end/cooldown 구간 | DevRespawn | 50 (`--set players=`) |
| `soak_match_reset.json` | 59–64 | 같은 Client로 경기 `matches`번(기본 10, `matchLoop`: Start → `matchSeconds`(30) s → finish → Reset), 경기마다 구간과 추세 행. 누적 판단은 `--set matches=50`(약 36분)·`100`(약 72분, 추정: 경기당 30 s + Result 10 s + Countdown 3 s). timeoutSeconds 21600으로 matchSeconds 30이면 480경기까지(그 이상은 Validator 경고) | 경기, StartCountdownSeconds 3 | 50 |
| `lag_compensation.json` | 18–19 | 4개 Group(25 %씩)이 각자 Proxy로 0/50/100/200 ms(한 방향, latency_sweep 관례) + 5 ms Jitter, Group마다 맵 사분면에서 짝 전투(60 % 가슴 조준). Group별 hitsLanded·pressesSent·RTT 저장. 0/50/100은 명중 단언, 200은 기록만(왕복이 되감기 창 0.4 s 경계) | DevRespawn | 20/50/100 |
| `turbo_build.json` | 23 | 75 %가 9/s Turbo 건설(Recycle), 나머지 걷기. RateLimited·Bad Packet 0 단언(한도 안) | DevRespawn, BuildInfiniteResources | 10/25/40 |
| `build_count.json` | 24–26 | `spawnBuildPieces` field로 1,000/2,500/5,000/10,000/20,010개 → 서 있는 조각 수별 구간. 마지막 세트는 20,000에서 BudgetFull 10개를 확인(실제 상한 = building.json maxBuildPiecesPerMatch). 'empty' 구간(조각 0)과 비교. 50명이 중앙 60 m 안을 걷는다 | DevRespawn | 50 (조각 수가 파라미터) |
| `build_destruction.json` | 27–29 | `spawnBuildPieces` hanging으로 Foundation 하나에 기댄 100/500/1,000조각 구조물 → 사수 1명이 Kestrel LR로 Foundation을 실제 사격(`groupFireAt`, damageBuild 아님) → 서버가 나머지를 한 Tick에 붕괴. 'collapse' 구간 1 s Sample(시작 = N, 끝 = 0 단언). 관찰자 49명이 20 m 안을 걷는다 | DevRespawn | 50 (조각 수가 파라미터) |
| `loot.json` | 30 | 모두 줍기 → 2번마다 Drop → 회복 아이템 사용 반복(`groupLoot`). lootPickups·lootDrops·lootUses > 0 | DevRespawn | 25/50/100 |
| `zone.json` | 31 | 실제 경기(AirDrop 끔). `setZone 2`(반경 70, 2/s) 뒤 50 %는 바깥 고리, 20 %는 안팎을 오가고, 나머지는 안. 바깥은 체력이 줄고 안쪽은 steady 동안 그대로, 아무도 죽지 않음 | 경기, AirDrop false | 25/50/100 |
| `elimination_burst.json` | 32 | 실제 경기. 생존자 4명은 중앙, 나머지는 55–100 m 고리 → `setZone 4`(반경 20, 10/s) → 바깥 전원이 같은 Zone Tick에 탈락(실제 Zone 경로, killPlayer 없음). 'burst' 1 s Sample로 alive가 players → ≤ players−50 | 경기, AirDrop false, Seed 8600(Zone 중심이 맵 중앙 근처) | 60/80/100 |
| `join_ramp.json` | 33 | 100명이 초당 5/10/20명 접속(`groupConnect`)하는 동안 'ramp' 1 s Sample, 그 뒤 steady. 접속 시간·실패 | DevRespawn | 100 (속도가 파라미터) |
| `join_spike.json` | 34 | 50/100명이 한 번에 접속, 'spike' 1 s Sample | DevRespawn | 50/100 |
| `disconnect_spike.json` | 35 | 전원이 한 번에 나감(Churn 100 % 1 Cycle, mixed/drop/graceful) → 2 s 뒤 같은 DevPlayerId로 돌아옴. 'spike' 1 s Sample, 잔류·중복 0 | DevRespawn, DisconnectTimeoutMs 2000 | 50/100 (+drop, graceful) |
| `input_timeout.json` | 39 | 42명이 걷는 중 8명(idle-001..008)이 한꺼번에 입력을 멈춤 → 7 s 뒤 서버가 InputTimeout으로 닫음 | DevRespawn, InputTimeoutSeconds 7 | 50 |
| `invalid_packets.json` | 40–42 | 10 % Abuser가 초당 5/50개 Invalid Packet(Kick → 1 s 뒤 Rejoin), 40 % 짝 전투, 나머지 걷기. 정상 Player Kick·끊김 0 | DevRespawn | 50 (5/s, 50/s), 100 (50/s) |
| `build_spam.json` | 43 | 10 % Spammer가 15/s 또는 40/s Build Request, 40 % 정상 건설(1/s Recycle), 나머지 걷기. 정상 건설이 계속 놓이고 Kick 0 | DevRespawn, BuildInfiniteResources | 50 (15/s, 40/s) |
| `network_fault_mixed.json` | 45–46 | 60 % 정상(20 % 짝 전투 + 40 % 역할 전환), 15 % 100 ms, 10 % 200 ms, 10 % 5 % Loss, 5 % 100 ms + 20 ms Jitter + 5 % Loss(Proxy, 한 방향 값). 아무도 끊기지 않음 | DevRespawn | 50/100 |
| `db_persistence.json` | 47 | 짧은 경기 8번(10 s → forceMatchState finish → 다음), 기록마다 50명 참가자. db.saved = 8, failed·dropped 0, Queue 0으로 비움 | 경기, Persistence:Enabled, ResultSeconds 2, StartCountdownSeconds 3 | 50 (`--set players=100`) |
| `db_down.json` | 48–49 | 경기 1 저장 → `stopDb` → 2 s 경기 6번(기록이 실패·Queue 거절) → 'outage_tail' → `startDb` → 다음 기록 저장. Queue ≤ 용량, failed > 0, Tick·접속 그대로 | 경기, Persistence:Enabled, QueueCapacity 1 | 50 (`--set players=100`) |
| `hotspot.json` | 56–58 | 같은 50명·같은 작업(전투 40 %, 건설 10 %, 걷기)을 맵 전체(반경 70) vs Crossroads 20 m 안에. 두 세트 묶음 비교표 | DevRespawn, BuildInfiniteResources | 50 (distributed, hotspot) |
| `restart_repeat.json` | 65 | 서버 재시작 5번(정상 종료 → 같은 인자로 새 프로세스), 매번 20명 재접속 후 10 s 측정. Exit Code 0, Kill 아님 | DevRespawn | 20 |

- mixed_match의 요청 비율은 겹친다(70 % 이동, 40 % Sprint, 30 % 전투, 20 % 줍기, 20 % 건설 중 10 % Turbo). 여기서는 겹치지 않는 Group으로 나누고, 행동이 겹치는 부분을 덮는다: 이동은 걷기·줍기·전투(Strafe·추격)를 합쳐 70 %를 넘고, Sprint는 sprint 역할·줍기·추격이다. 비율은 변수다.
- DevRespawn 시나리오에는 Zone 피해가 없다(Zone은 경기 흐름에서만 돈다). Zone 피해·대량 탈락은 경기 시나리오(soak_match_reset, zone, elimination_burst)가 맡는다.
- Phase B의 경기 시나리오(zone, elimination_burst, db_*)는 `Server:AirDrop=false`다. 수송기 탑승자는 Zone 피해를 받지 않고, 내려오는 시간이 실행마다 달라서다. `server.options`에는 변수를 쓸 수 없어서 Countdown·QueueCapacity는 고정값이다.
- Zone 중심은 Seed로 정해진다. `setZone N` 뒤의 원은 이전 Phase의 목표 원이고 피해는 Phase N의 값이다(`match.zone.targetCenter`가 다음 원의 중심). elimination_burst는 그 원의 중심이 맵 중앙에서 13 m인 Seed 8600을 쓴다(다른 `--seed`면 생존자도 바깥에서 시작할 수 있다).
- Arrange와 Act의 구분(§122): build_count·build_destruction의 조각, groupBuildSpam의 자원, setZone·forceMatchState(흐름 제어)는 Arrange다. 측정 대상(파괴·탈락·접속·Packet·Build Request·DB 기록)은 실제 입력이나 서버 경로로 일어난다. build_destruction의 Foundation은 실제 사격으로 부서진다(damageBuild를 쓰지 않았다).

### Suite

| Suite | 내용 | 시간 |
|---|---|---|
| `stress-quick` | baseline·movement·combat·building·mixed를 50명, steady 30 s | 4 min 20 s(실측, 5개 PASS, 2026-10-02 Phase B 코드) |
| `stress-gameplay` | movement·combat·building 50명, mixed 50·100명, final_zone 40명, loot 50, zone 50, elimination_burst 60, hotspot 두 세트 | 약 20 min(추정: 각 파일을 따로 잰 시간의 합) |
| `stress-soak` | soak(5분) + soak_match_reset(10경기, `matchLoop`) | 약 12 min(추정: 따로 잰 5.6 + 6.0 min) |
| `stress-network` | reconnect_churn 25/50/100, lag_compensation 50, join_ramp 5/10/20 per s, join_spike 100, disconnect_spike 50, invalid_packets 5/s·50/s, network_fault_mixed 50 | 약 25 min(추정) |
| `stress-building` | building 10/25/50, final_zone 50, turbo_build 40, build_count 1,000–20,000, build_destruction 100/500/1,000, build_spam 15/s·40/s | 약 25 min(추정) |
| `stress-fault` | reconnect_churn 50, disconnect_spike 50, input_timeout, invalid_packets 50/s, network_fault_mixed 50, db_persistence, db_down(DB 컨테이너가 없으면 SKIPPED) | 약 13 min(추정) |
| `stress-full` | 모든 Stress 시나리오의 모든 파라미터 세트(restart_repeat 제외) | 여러 시간(추정), nightly |
| `stress-restart` | restart_repeat(§65: 재시작 5번). Gameplay가 아니라서 따로 둔다 | 1 min 7 s(실측: 파일 단독 실행) |

- Suite 항목은 문자열 경로 말고 `{ "path": "Stress/baseline.json", "parameterSet": 3, "variables": { "steadySeconds": 30 } }`도 된다. 그 세트 하나를 그 변수로 돌린다(CLI의 `--parameter-set`·`--set`이 이긴다). 같은 파일을 다른 옵션으로 여러 번 넣을 수 있다.
- pre-push에는 넣지 않는다(§97). `stress` Suite와 `full-regression`은 예전 Load 파일 3개만 돈다.

### 측정 (2026-10-02, Phase A)

- 환경: i9-14900K(논리 32), Windows 11, .NET 10, **Debug 빌드**(`bin/Debug/net10.0/ProjectH.Server.dll`), 서버와 QA Tool(Actor Pump)이 같은 PC, 루프백. Git 8638cff + 이 작업(dirty). 다른 Agent가 같은 PC에서 일하고 있었을 수 있다.
- 모두 steady 60 s(churn 120 s, soak 300 s), Tick 백분위는 구간 전체 창(정확값), soak만 Sample 근사. CPU는 32 논리 프로세서 = 100 %. KB = 1024 B UDP payload.
- 모두 PASSED, Stall 0, Tick Failure 0, Bad Packet 0, Crash 없음. baseline·movement·building·baseline 100·묶음은 첫 번째 실행, 전투가 들어간 것(combat, mixed, final_zone, soak)과 reconnect_churn은 Burst 조준 수정과 Churn 길이 조정 뒤 다시 잰 값이다(전투가 없는 작업량은 그 수정과 관계없다).

| 시나리오 | Players | Tick p50 | p95 | p99 | max (ms) | 서버 CPU % | Managed 끝/최대 MB | WS 최대 MB | GC 0/1/2 | 할당 MB/s | Send KB/s | Recv KB/s | pkt out/s | R1 p50/p95/p99 ms | Tool CPU % | Report |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| baseline | 50 | 0.121 | 0.185 | 0.236 | 0.578 | 0.154 | 5.9/5.9 | 64.5 | 0/0/0 | 0.002 | 496.1 | 134.8 | 750 | 91/136/138 | 0.114 | qa-20261002-201140-2fc6 |
| movement | 50 | 0.138 | 0.211 | 0.262 | 0.679 | 0.115 | 5.9/5.9 | 64.9 | 0/0/0 | 0.002 | 496.0 | 134.8 | 762 | 90/107/109 | 0.117 | qa-20261002-201303-d741 |
| combat | 50 | 0.148 | 0.285 | 0.536 | 2.480 | 0.158 | 8.2/17.9 | 84.0 | 1/0/0 | 0.146 | 558.0 | 134.8 | 3199 | 90/107/108 | 0.140 | qa-20261002-204410-02bb |
| building | 50 | 0.192 | 0.569 | 0.749 | 4.531 | 0.214 | 15.6/20.9 | 93.8 | 3/1/0 | 0.803 | 650.7 | 134.9 | 6063 | 90/108/136 | 0.161 | qa-20261002-201549-639d |
| mixed_match | 50 | 0.237 | 0.390 | 0.529 | 2.363 | 0.189 | 9.3/9.3 | 73.2 | 0/0/0 | 0.028 | 572.0 | 134.6 | 3859 | 91/199/233 | 0.229 | qa-20261002-204533-a735 |
| final_zone | 40 | 0.197 | 0.332 | 0.419 | 0.670 | 0.222 | 8.3/8.3 | 71.3 | 0/0/0 | 0.016 | 376.6 | 107.9 | 2845 | 90/107/108 | 0.139 | qa-20261002-204820-abab |
| reconnect_churn | 50 | 0.146 | 0.248 | 0.341 | 1.027 | 0.186 | 15.6/15.6 | 74.6 | 0/0/0 | 0.071 | 485.2 | 126.9 | 823 | 90/107/109 | 0.135 | qa-20261002-204943-2fc1 |
| baseline | 100 | 0.242 | 0.321 | 0.444 | 0.601 | 0.227 | 8.8/8.8 | 66.7 | 0/0/0 | 0.002 | 1984.5 | 269.5 | 3002 | 108/138/140 | 0.239 | qa-20261002-201835-ca6d |
| combat | 100 | 0.264 | 0.673 | 1.120 | 5.860 | 0.329 | 24.3/28.5 | 99.9 | 6/5/2 | 1.314 | 2234.5 | 269.5 | 12730 | 106/138/139 | 0.278 | qa-20261002-205213-d81f |
| mixed_match | 100 | 0.451 | 0.786 | 0.995 | 6.796 | 0.392 | 28.7/34.3 | 105.4 | 7/6/2 | 1.511 | 2282.4 | 269.0 | 14195 | 108/199/233 | 0.515 | qa-20261002-204656-31c8 |
| soak (300 s, 근사) | 50 | 0.196 | 0.485 | 1.201 | 7.403 | 0.174 | 12.8/12.8 | 74.4 | 0/0/0 | 0.020 | 540.7 | 134.7 | - | -/107/- | 0.135 | qa-20261002-205338-9fd4 |

Baseline Player Count 비교(D40 묶음, steady 30 s, `batch-20261002-202122-4170`):

| Players | Tick p50 | p95 | p99 | max | CPU % | Managed MB | Send KB/s | Recv KB/s |
|---|---|---|---|---|---|---|---|---|
| 10 | 0.050 | 0.076 | 0.105 | 0.188 | 0.119 | 4.5 | 23.0 | 27.0 |
| 25 | 0.077 | 0.125 | 0.145 | 0.258 | 0.152 | 4.9 | 128.9 | 67.4 |
| 50 | 0.120 | 0.183 | 0.231 | 0.282 | 0.163 | 5.8 | 495.8 | 134.8 |
| 75 | 0.171 | 0.257 | 0.323 | 0.558 | 0.177 | 7.1 | 1100.8 | 202.1 |
| 100 | 0.240 | 0.323 | 0.454 | 0.773 | 0.240 | 8.4 | 1985.7 | 269.5 |

작업량(같은 실행, warmup부터 끝까지의 Group 합계): combat 50은 입력 누름 3693(사격·Reload·슬롯 키), HitConfirmed 646, 사망 62, Re-arm 57. combat 100은 누름 7543, HitConfirmed 1048, 사망 89. building 50은 배치 1495건 모두 Ok, 조각 수가 짓기·부수기로 140–340 사이를 오간다. mixed 100은 건설 Ok 1078, 전투 Group HitConfirmed 280, Churn 재접속 20. reconnect_churn 50은 20 Cycle, 끊김 160·재접속 160(실패 0, 중복 0), 서버 resumes 160, 재접속 평균 137 ms·최대 364 ms.

**측정으로 확인된 것**
- **가장 먼저 크게 자라는 것은 송신 대역폭이다.** 놀고 있는 Client만으로 10 → 100명에서 23 → 1986 KB/s(약 86배, 인원의 제곱에 가깝게). 같은 구간에서 Tick p95는 0.08 → 0.32 ms, CPU는 0.12 → 0.24 %다. LoadTest Phase 8의 100봇 2075 KB/s와 같은 크기다.
- **가장 비싼 Gameplay는 건설(50명)과 100명 전투·혼합이다.** 50명에서 building의 p95 0.57 ms·max 4.5 ms·할당 0.8 MB/s·GC 3/1/0이 가장 높다. 100명에서는 combat과 mixed가 할당 1.3–1.5 MB/s, GC 6–7/5–6/2(60 s에 Gen2 2번, 합계 Pause 20–24 ms), p99 1.0–1.1 ms, max 5.9–6.8 ms다. 같은 작업량의 50명 combat은 할당 0.15 MB/s·Gen2 0이다. 할당이 어디서 나오는지는 이번에 재지 않았다(Profiling은 다음 단계).
- **Tick 예산(33 ms)에 가까운 구간은 없다.** 가장 큰 단일 Tick은 7.7 ms(soak_match_reset match_01, soak 7.4 ms), 가장 높은 p99는 1.2 ms(soak, 근사)다. D42 경고(tickMax > 33 ms)는 한 번도 나지 않았다.
- **Churn**: 50명이 경기 중 20 Cycle 동안 매번 15 %가 끊겨도(반은 패킷 없이) 모두 같은 캐릭터로 돌아왔고(resumes = 재접속 수), 중복·잔류 Player가 없었다. 첫 시도(180 s)는 Zone이 마지막 30 s에 50명 중 43명을 죽여, 죽은 사람이 관전자로 다시 들어왔다(resume 226/240, join 14). 그래서 시나리오를 Zone 피해 전에 끝나게 줄였다.
- **경기 반복(soak_match_reset, 10경기)**: 경기당 Tick p95 0.30–0.41 ms로 일정하다. Working Set은 63.8 → 91.4 MB(첫 시도 63.9 → 86.1 MB)로 올랐고 Managed는 GC 뒤 6.9 MB(4경기 시작), 9.0 MB(10경기 시작)였다(처음 6.1 MB). GC는 경기 사이(측정 구간 밖)에 일어나 구간 GC 수는 0이다. 10경기(약 6분)로는 누적인지 아닌지 판단할 수 없다. 더 긴 Soak가 필요하다.
- **soak 300 s**: Managed가 6.7 → 12.8 MB로 거의 직선으로 늘었지만 그 사이 GC가 한 번도 일어나지 않았다(할당 0.02 MB/s). GC 뒤 기준선이 오르는지는 5분으로는 볼 수 없다(`--set soakSeconds=1800` 이상).
- **R1**: 혼잡 없는 루프백에서 p50 약 90 ms, p95 107–138 ms. 서버의 다음 Tick, 15 Hz Snapshot 간격, Pump Tick 해상도(약 33 ms)가 들어 있다. mixed의 p95 199 ms는 양방향 50 ms 지연 Group 때문이다. 이 값은 같은 조건 비교용이고 절대 지연이 아니다.
- **QA Tool 자신**: CPU 0.11–0.52 %(Core 0.04–0.17개). 서버와 비슷한 크기라 같은 PC 측정을 크게 흔들지 않는다.
- **LoadTest와 비교(§125)**: LoadTest Phase 8의 50봇(Release, 봇 Brain) p95 0.11 ms, 이번 baseline 50(Debug, 정지) p95 0.185 ms, combat 50 p95 0.285 ms. 빌드 구성과 작업량이 달라서 Regression으로 판단하지 않는다.
- **Crash 경로 확인**: 측정 중인 실행(10명)의 서버를 자기 PID로 강제 종료했더니 `measure`가 바로 실패하고(Exit Code -1, "the server process exited"), Report에 Crash(Step, Actor 수, 마지막 Sample, 로그 63줄)가 남았다.

**알려진 제한**
- 서버와 Actor가 같은 PC·루프백이다. 실제 네트워크의 지연·손실·대역폭은 mixed의 고지연 Group(Fault Proxy) 말고는 없다.
- DevRespawn 시나리오에는 Zone 피해가 없다. 경기 시나리오(reconnect_churn, soak_match_reset)는 Zone을 끌 수 없어서 길이를 Zone 피해 전으로 제한했다.
- 전투 사망 뒤 Re-arm은 QA 명령(Arrange)이다. 구간별로 세고(`Arrange cmds`, 50명 combat 60 s에 약 170개) 서버 `qaCommandMs`(Tick당 0.004–0.005 ms)에 들어간다.
- 건설 Site는 맵에 약 40개다. building은 50명(Turbo 포함 35명 건설)까지만 파라미터로 둔다.
- 120 s보다 긴 구간의 Tick 백분위는 Sample 근사다(p95/p99는 실제보다 좋게 나오지 않는다).
- GC 수는 구간 안의 것만 센다. 구간 사이(경기 Reset 등)의 GC는 Managed 시작값 변화로만 보인다.
- R1은 Client 쪽 왕복 지표이고 Pump Tick(약 33 ms) 해상도다.
- Brain의 선택은 Seed로 정해지지만 세계의 타이밍(Steering, 사망 시점)은 실행마다 조금 다르다.
- 서버 GC를 강제로 일으킬 수 없다(QA API에 없고, 넣으면 측정이 바뀐다). "GC 뒤 기준선"은 자연 GC 시점에만 보인다.

### 측정 (2026-10-02, Phase B)

- 환경은 Phase A와 같다: i9-14900K(논리 32), Windows 11, .NET 10, **Debug 빌드**, 서버와 QA Tool이 같은 PC·루프백. Git 0d9b96c + 이 작업(dirty). 다른 Agent가 같은 PC에서 일하고 있었을 수 있다. CPU는 32 논리 프로세서 = 100 %, KB = 1024 B UDP payload.
- 표는 각 시나리오의 판정 구간이다(steady가 없는 것은 그 시나리오의 측정 구간: collapse, burst, ramp, spike, timeout, match_08, outage_tail, run_05). 120 s 이하 구간이라 Tick 백분위는 구간 전체 창의 정확값이다. 모두 PASSED, Stall 0, Tick Failure 0, Crash 없음.
- "Send KB/s (최대)"의 괄호는 Sample 창(5 s, 1 s Sample 시나리오는 1 s)의 최댓값이다. **1 s Sample의 Send·CPU는 정확하지 않다**: 서버가 초 단위로 남기는 누적값 기록 중 창 안에서 가장 오래된 것부터 계산해서(`Qa/QaObservation`), 1 s 창은 기록 간격에 따라 0이나 실제보다 큰 값이 나올 수 있다(build_destruction 100에서 1 s Send 0 KB/s가 나왔다). 그래서 Burst의 송신량은 구간 평균의 차이로 본다. Tick 값은 Tick마다 기록되므로 1 s Sample도 정확하다.
- 판정 구간 밖에서 바뀐 것: zone 50은 마지막 파일로 다시 잰 값이다(100명 실행에서 안쪽 Group의 늦은 1명 때문에 warmup을 10 → 15 s로, 평균 체력 비교에 허용 오차 1을 더함). Rejoin Workload를 Group당 하나로 고친 것(같은 Group에 Abuse Action을 두 번 부를 때만 다르다)은 50명 표의 abuse 실행 뒤다. 고친 뒤 invalid_packets 100명과 build_spam 40/s(qa-20261002-225621-e474)를 다시 돌려 PASS를 확인했다.

50명(또는 그 시나리오의 자연 크기):

| 시나리오 | Players | 구간 | Tick p50 | p95 | p99 | max (ms) | 서버 CPU % | Managed 끝/최대 MB | WS 최대 MB | GC 0/1/2 | 할당 MB/s | Send KB/s (최대) | Recv KB/s | R1 p50/p95/p99 ms | Tool CPU % | Report |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| lag_compensation | 50 | steady 60 s | 0.222 | 0.453 | 0.644 | 1.087 | 0.199 | 10.8/10.8 | 72.9 | 0/0/0 | 0.040 | 570.7 (600.7) | 134.8 | 242/534/540 | 0.869 | qa-20261002-223352-e21a |
| turbo_build (40) | 40 | steady 60 s | 0.240 | 0.674 | 0.801 | 7.731 | 0.230 | 6.2/17.1 | 83.5 | 1/0/0 | 0.082 | 478.1 (514.3) | 108.0 | 105/138/139 | 0.152 | qa-20261002-223517-c88d |
| build_count 0 pieces | 50 | empty 10 s | 0.151 | 0.290 | 0.658 | 1.473 | 0.266 | 5.8/5.8 | 64.6 | 0/0/0 | 0.016 | 497.6 (496.0) | 134.8 | 91/109/137 | 0.189 | qa-20261002-223640-48d9 |
| build_count 1,000 | 50 | steady 30 s | 0.244 | 0.418 | 0.504 | 0.957 | 0.225 | 12.0/12.0 | 75.6 | 0/0/0 | 0.002 | 496.3 (500.4) | 134.8 | 91/109/138 | 0.130 | qa-20261002-223640-48d9 |
| build_count 2,500 | 50 | steady 30 s | 0.265 | 0.466 | 0.617 | 1.272 | 0.152 | 6.4/6.4 | 83.1 | 0/0/0 | 0.002 | 496.8 (501.0) | 134.8 | 90/107/109 | 0.117 | qa-20261002-223735-1ce5 |
| build_count 5,000 | 50 | steady 30 s | 0.410 | 0.720 | 0.801 | 0.944 | 0.295 | 19.6/19.6 | 85.2 | 0/0/0 | 0.002 | 504.8 (510.7) | 134.8 | 107/138/139 | 0.138 | qa-20261002-223833-fe20 |
| build_count 10,000 | 50 | steady 30 s | 0.631 | 1.093 | 1.167 | 1.323 | 0.255 | 16.6/16.6 | 91.4 | 0/0/0 | 0.001 | 512.1 (522.1) | 134.8 | 90/108/109 | 0.153 | qa-20261002-223936-6184 |
| build_count 20,000 (cap) | 50 | steady 30 s | 0.827 | 1.448 | 1.672 | 1.926 | 0.254 | 14.3/14.3 | 96.1 | 0/0/0 | 0.001 | 495.9 (499.2) | 134.8 | 89/108/109 | 0.143 | qa-20261002-215908-ee21 |
| build_destruction 100 | 50 | collapse 10 s | 0.174 | 0.325 | 0.453 | 2.930 | 0.141 | 7.3/7.3 | 70.5 | 0/0/0 | 0.010 | 500.9 (497.9) | 134.8 | 89/107/109 | 0.218 | qa-20261002-224050-b883 |
| build_destruction 500 | 50 | collapse 10 s | 0.154 | 0.332 | 0.416 | 2.663 | 0.215 | 9.6/9.6 | 73.8 | 0/0/0 | 0.011 | 508.6 (632.0) | 134.8 | 89/108/109 | 0.160 | qa-20261002-224113-feb9 |
| build_destruction 1,000 | 50 | collapse 10 s | 0.186 | 0.341 | 0.445 | 3.831 | 0.224 | 12.4/12.4 | 76.9 | 0/0/0 | 0.027 | 519.6 (544.3) | 134.8 | 78/108/109 | 0.146 | qa-20261002-215834-1545 |
| loot | 50 | steady 60 s | 0.156 | 0.292 | 0.389 | 1.140 | 0.159 | 6.6/6.6 | 64.8 | 0/0/0 | 0.010 | 499.2 (508.5) | 134.8 | 89/107/108 | 0.154 | qa-20261002-220858-725a |
| zone | 50 | steady 30 s | 0.141 | 0.220 | 0.276 | 0.729 | 0.158 | 7.0/7.0 | 65.7 | 0/0/0 | 0.002 | 495.9 (499.5) | 134.8 | 89/107/108 | 0.119 | qa-20261002-225905-f470 |
| elimination_burst (60) | 60 | burst 20 s | 0.142 | 0.311 | 0.399 | 1.627 | 0.163 | 8.2/8.2 | 67.6 | 0/0/0 | 0.032 | 710.8 (769.9) | 161.7 | 90/108/109 | 0.131 | qa-20261002-220255-622e |
| join_ramp 5/s | 5–100 | ramp 25 s | 0.201 | 0.514 | 0.737 | 13.973 | 0.272 | 6.8/6.8 | 66.7 | 0/0/0 | 0.110 | 970.9 (2180.5) | 165.6 | 106/138/140 | 0.263 | qa-20261002-224256-d146 |
| join_ramp 10/s | 10–100 | ramp 15 s | 0.245 | 0.547 | 1.169 | 11.601 | 0.383 | 6.9/6.9 | 67.0 | 0/0/0 | 0.185 | 1181.0 (2163.2) | 185.9 | 92/136/138 | 0.399 | qa-20261002-224351-fdf9 |
| join_ramp 20/s | 20–100 | ramp 10 s | 0.271 | 0.614 | 1.183 | 13.743 | 0.369 | 7.0/7.0 | 67.3 | 0/0/0 | 0.282 | 1453.1 (2164.1) | 212.2 | 91/111/136 | 0.388 | qa-20261002-220456-288b |
| join_ramp steady 100 | 100 | steady 20 s | 0.326 | 0.572 | 0.642 | 0.843 | 0.347 | 7.2/7.2 | 67.4 | 0/0/0 | 0.010 | 1983.6 (1998.3) | 269.5 | 93/137/139 | 0.324 | qa-20261002-220456-288b |
| join_spike 100 | 100 | spike 10 s | 0.285 | 0.572 | 1.104 | 18.906 | 0.275 | 8.6/8.6 | 67.5 | 0/0/0 | 0.442 | 1983.6 (2164.7) | 269.6 | 91/109/137 | 0.403 | qa-20261002-220536-84da |
| disconnect_spike mixed | 0–50 | spike 20 s | 0.150 | 0.287 | 0.490 | 1.557 | 0.306 | 7.6/7.6 | 69.4 | 0/0/0 | 0.097 | 478.3 (500.7) | 125.2 | 89/107/109 | 0.277 | qa-20261002-220616-78e2 |
| disconnect_spike drop | 0–50 | spike 20 s | 0.148 | 0.283 | 0.527 | 1.482 | 0.320 | 7.8/7.8 | 69.1 | 0/0/0 | 0.099 | 499.5 (1000.2) | 126.0 | 93/137/139 | 0.173 | qa-20261002-224436-5f18 |
| disconnect_spike graceful | 0–50 | spike 20 s | 0.159 | 0.293 | 0.534 | 1.708 | 0.197 | 7.7/7.7 | 70.4 | 0/0/0 | 0.097 | 474.5 (496.5) | 126.1 | 106/138/139 | 0.187 | qa-20261002-224509-527d |
| input_timeout | 42–50 | timeout 15 s | 0.140 | 0.260 | 0.379 | 2.042 | 0.183 | 6.0/6.0 | 65.0 | 0/0/0 | 0.018 | 415.1 (496.1) | 113.2 | 90/108/109 | 0.156 | qa-20261002-220648-576f |
| invalid_packets 5/s | 45–50 | steady 60 s | 0.182 | 0.333 | 0.421 | 0.770 | 0.246 | 9.7/9.7 | 72.6 | 0/0/0 | 0.036 | 497.5 (509.3) | 130.9 | 93/137/138 | 0.197 | qa-20261002-221113-d197 |
| invalid_packets 50/s | 45–49 | steady 60 s | 0.172 | 0.345 | 0.530 | 1.058 | 0.217 | 13.3/13.3 | 75.9 | 0/0/0 | 0.085 | 448.0 (460.8) | 122.7 | 89/107/108 | 0.165 | qa-20261002-224138-d57f |
| build_spam 15/s | 50 | steady 60 s | 0.283 | 0.683 | 1.018 | 5.676 | 0.231 | 5.1/17.5 | 85.8 | 1/0/0 | 0.196 | 586.3 (637.4) | 135.5 | 90/107/109 | 0.173 | qa-20261002-221507-7a4e |
| build_spam 40/s | 45–50 | steady 60 s | 0.273 | 0.665 | 0.987 | 2.958 | 0.216 | 6.3/17.6 | 86.6 | 1/0/0 | 0.173 | 540.7 (601.5) | 126.7 | 92/137/139 | 0.119 | qa-20261002-221624-34bc |
| network_fault_mixed | 50 | steady 60 s | 0.174 | 0.310 | 0.403 | 1.025 | 0.189 | 7.6/7.6 | 70.3 | 0/0/0 | 0.011 | 510.0 (518.6) | 133.9 | 136/518/536 | 0.475 | qa-20261002-221742-9a19 |
| hotspot: distributed | 50 | steady 60 s | 0.257 | 0.450 | 0.644 | 1.535 | 0.224 | 8.3/8.3 | 72.0 | 0/0/0 | 0.015 | 545.6 (573.1) | 134.8 | 107/138/139 | 0.170 | qa-20261002-221900-7907 |
| hotspot: hotspot | 50 | steady 60 s | 0.242 | 0.441 | 0.638 | 1.023 | 0.214 | 8.9/8.9 | 72.6 | 0/0/0 | 0.019 | 544.9 (558.7) | 134.8 | 92/135/138 | 0.156 | qa-20261002-222018-d072 |
| db_persistence (match_08) | 50 | match_08 10 s | 0.147 | 0.267 | 0.377 | 0.626 | 0.181 | 10.0/10.0 | 93.2 | 0/0/0 | 0.022 | 497.7 (496.0) | 134.8 | 110/138/139 | 0.131 | qa-20261002-222426-2d50 |
| db_down outage_tail | 50 | outage_tail 10 s | 0.150 | 0.271 | 0.406 | 1.112 | 0.131 | 7.3/7.3 | 98.2 | 0/0/0 | 0.210 | 515.3 (879.1) | 134.8 | 92/136/138 | 0.097 | qa-20261002-222633-6416 |
| restart_repeat (20, run_05) | 20 | run_05 10 s | 0.080 | 0.138 | 0.397 | 1.107 | 0.144 | 4.5/4.5 | 59.2 | 0/0/0 | 0.012 | 84.1 (84.7) | 53.9 | 106/137/138 | 0.058 | qa-20261002-222136-549e |

100명(실행한 것만):

| 시나리오 | Players | 구간 | Tick p50 | p95 | p99 | max (ms) | 서버 CPU % | Managed 끝/최대 MB | WS 최대 MB | GC 0/1/2 | 할당 MB/s | Send KB/s (최대) | Recv KB/s | R1 p50/p95/p99 ms | Tool CPU % | Report |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| lag_compensation | 100 | steady 60 s | 0.499 | 0.801 | 0.953 | 1.493 | 0.490 | 28.0/32.8 | 99.9 | 4/3/1 | 0.850 | 2255.4 (2326.6) | 269.5 | 276/534/538 | 2.238 | qa-20261002-224634-f583 |
| loot | 100 | steady 60 s | 0.309 | 0.571 | 0.714 | 2.870 | 0.320 | 10.3/10.3 | 68.5 | 0/0/0 | 0.018 | 1993.4 (2012.0) | 269.5 | 91/123/138 | 0.269 | qa-20261002-224800-8f93 |
| zone | 100 | steady 30 s | 0.282 | 0.420 | 0.534 | 0.769 | 0.229 | 12.1/12.1 | 70.5 | 0/0/0 | 0.006 | 1985.8 (1983.7) | 269.5 | 90/108/136 | 0.227 | qa-20261002-225802-8638 |
| elimination_burst | 100 | burst 20 s | 0.228 | 0.529 | 0.605 | 2.598 | 0.260 | 12.9/12.9 | 72.5 | 0/0/0 | 0.083 | 1987.1 (2173.0) | 269.5 | 92/134/138 | 0.287 | qa-20261002-225015-599b |
| invalid_packets 50/s (10 abusers) | 90 | steady 60 s | 0.296 | 0.676 | 1.299 | 4.886 | 0.332 | 21.3/26.2 | 98.8 | 4/2/1 | 1.202 | 1749.5 (1792.2) | 245.4 | 108/139/167 | 0.288 | qa-20261002-225112-aa09 |
| network_fault_mixed | 100 | steady 60 s | 0.327 | 0.612 | 0.934 | 2.056 | 0.303 | 9.6/23.5 | 91.1 | 2/1/0 | 0.311 | 2040.7 (2056.3) | 267.5 | 108/532/564 | 0.853 | qa-20261002-225230-bfc2 |
| disconnect_spike mixed | 0–100 | spike 20 s | 0.254 | 0.391 | 0.529 | 3.829 | 0.299 | 13.5/13.5 | 74.1 | 0/0/0 | 0.244 | 1916.3 (1988.9) | 252.1 | 90/108/137 | 0.248 | qa-20261002-225349-ae8d |
| build_destruction 1,000 (99 observers) | 100 | collapse 10 s | 0.280 | 0.661 | 0.742 | 3.663 | 0.276 | 15.4/15.4 | 79.8 | 0/0/0 | 0.028 | 2034.5 (1992.3) | 269.5 | 107/138/167 | 0.301 | qa-20261002-225421-1f58 |
| build_count 20,000 (cap) | 100 | steady 30 s | 1.509 | 2.484 | 2.687 | 3.014 | 0.417 | 22.8/22.8 | 96.6 | 0/0/0 | 0.002 | 1988.4 (1992.2) | 269.5 | 106/138/139 | 0.208 | qa-20261002-225447-333b |
| build_count 0 pieces | 100 | empty 10 s | 0.298 | 0.538 | 0.737 | 1.283 | 0.299 | 8.6/8.6 | 67.1 | 0/0/0 | 0.036 | 1990.2 (1983.6) | 269.5 | 106/137/139 | 0.252 | qa-20261002-225447-333b |
| db_persistence (match_08) | 100 | match_08 10 s | 0.291 | 0.496 | 0.559 | 0.616 | 0.389 | 12.2/12.2 | 102.1 | 0/0/0 | 0.046 | 1983.7 (1998.5) | 269.5 | 106/138/139 | 0.291 | qa-20261002-222806-5f02 |

작업량과 결과(같은 실행, 실측):
- **build_count**: 조각 배치 속도 472–480 조각/s(QA 명령 16개 동시, 서버가 Tick당 16개 = 480/s가 상한), 20,010 요청 → 20,000 서 있음 + BudgetFull 10(**실제 상한 = building.json maxBuildPiecesPerMatch 20,000**). 20,000개에 41.8 s. 50명에서 Tick p50 0.15(조각 0) → 0.24(1,000) → 0.27(2,500) → 0.41(5,000) → 0.63(10,000) → 0.83 ms(20,000), p99 0.66 → 1.67 ms. 100명은 0.30 → 1.51 ms(p99 2.69, max 3.01). Working Set 64.6 → 96.1 MB(50명). 구간 GC 0, 할당 0.001–0.002 MB/s, Send는 그대로였다(추정: 조각은 배치될 때 한 번 복제되고 서 있는 동안은 보내지 않는 것으로 보인다. 복제 코드로 확인하지 않았다).
- **build_destruction**: 100/500/1,000조각 구조물이 Foundation 한 발(Kestrel LR 실제 사격)로 모두 무너졌다(구간 시작 = N, 끝 = 0, damageBuild 미사용). 붕괴 Tick이 든 1 s Sample의 Tick max: 2.93(100), 2.66(500), 3.83 ms(1,000), 100명 관찰 1,000조각 3.66 ms. 구간 GC 0. 1,000조각의 구간 평균 Send는 붕괴 전 5 s 495.9 → 붕괴 구간 10 s 519.6 KB/s(차이로 약 240 KB가 더 나갔다).
- **elimination_burst**: 60명 중 50명이 한 1 s Sample 안에 탈락(60 → 10, 그 뒤 6 s에 걸쳐 6명 더), 그 Sample의 Tick max 1.63 ms. 100명은 100 → 8(92명) 한 Sample에, Tick max 2.60 ms. 경기는 생존자로 계속됐다. 실제 Zone 피해 경로(setZone은 Arrange, killPlayer·damagePlayer 없음).
- **join_ramp / join_spike**: 100명 접속, 실패 0. 접속(명령 → 첫 Snapshot) 평균 130–134 ms, 최대 593–660 ms(5/10/20 per s 모두 비슷). 100명 동시 접속 첫 1 s의 Tick max 18.9 ms(이번 Stress 전체에서 가장 큰 단일 Tick, 예산 33 ms 안), Ramp는 11.6–14.0 ms.
- **disconnect_spike**: 50명 전원 이탈·복귀 실패 0, 중복 0, 잔류 0(mixed 재접속 평균 135 ms, 최대 389 ms; graceful 91 ms). 100명 mixed Tick max 3.83 ms.
- **input_timeout**: 8명이 7 s 뒤 같은 1 s 안에 InputTimeout으로 닫혔다(그 Sample Tick max 2.04 ms). 나머지 42명 그대로.
- **lag_compensation**(Proxy 한 방향 0/50/100/200 ms + 5 ms Jitter, RTT 29/133/224/422 ms): HitConfirmed / 누름(사격·Reload·슬롯 키 포함, warmup부터) = 50명 259/1021, 118/1202, 134/1062, 102/1076; 100명 638/1800, 518/1889, 419/2003, 380/1973. 200 ms Group도 맞혔다. 비율은 두 실행 모두 0 ms Group이 가장 높다(50명 약 25 %, 그 밖 9.5–13 %). Ping 순서대로 낮아진 것은 100명 실행뿐이다(50명에서는 100 ms Group이 50 ms Group보다 높았다).
- **invalid_packets**: 50명 중 5명이 5/s → 60 s 구간 badPackets 1,370, Kick 80, Rejoin 75; 50/s → 보낸 5,724, Kick 270, Rejoin 265. 100명(10명 50/s) → 보낸 11,441, Kick 540, Rejoin 530, 이때 Tick p99 1.30·max 4.89 ms, GC 4/2/1(할당 출처는 재지 않았다). 표의 Players가 90인 것은 Abuser가 접속 뒤 0.4 s 안에 Kick되고 1 s 뒤 다시 들어와서 5 s Sample 시점에 거의 잡히지 않기 때문이다(Kick·Rejoin 수로 접속은 확인된다). 정상 Player의 Kick·끊김 0.
- **build_spam**: 15/s(Network 한도 20/s 아래)는 RateLimited가 **0**이었다(OutOfRange 1,807·Occupied 3,300·Ok 51). 서버 코드로 확인: `Match.ProcessBuildRequests`(`Server/src/ProjectH.Server/Game/Match.cs` 536–538행)는 거절된 요청 뒤 `continue`로 다음 요청을 같은 Tick에 처리하고, 최소 간격(`NextBuildTick`)은 배치된(Ok) 요청 뒤에만 건다. 그래서 거절되는 Spam은 8칸 Queue를 채우지 않는다. 40/s는 BuildRate Bad Packet 3,333으로 Kick 180·Rejoin 175, RateLimited 1,523. 두 경우 모두 정상 건설 Group은 계속 놓았다(Ok 716/721, Kick 0).
- **loot**: 50명 60 s + warmup에 줍기 1,721, Drop 848, 사용 누름 14(100명: 3,411 / 1,666 / 12). 사용이 적은 이유는 재지 않았다(추정: 회복 아이템이 드물고 체력·실드가 이미 차 있는 경우가 많다).
- **zone**(50명): 바깥 Group 평균 체력 53, 안팎 92, 안쪽 96.7(steady 동안 그대로). 100명도 같은 판정 통과.
- **network_fault_mixed**: Group RTT 정상 29, 100 ms 236, 200 ms 431, 5 % Loss 65, 복합 280 ms. 아무도 끊기지 않았다. R1 p95 518 ms(50명)는 지연 Group이 끌어올린 값이다.
- **hotspot vs distributed**(50명, 같은 작업): Tick p95 0.441 vs 0.450, Send 545 vs 546 KB/s — 측정으로 차이가 없다.
- **db_persistence**: 8경기 기록(50명·100명 참가) 모두 저장, failed 0, dropped 0, Queue 최대 0, 경기 구간 Tick p95 0.26–0.32 ms(50명). 경기 사이 Managed는 GC로 내려갔다(17.0 → 5.0 MB).
- **db_down**(QueueCapacity 1): DB 정지 중 6경기 → failed 4 + dropped 1(Queue가 차서 거절) + 진행 중 1(뒤에 failed 5). Queue 최대 1(용량). 정지 중 Tick p95 0.26–0.29 ms(DB 있을 때와 같은 범위), Tick Failure·Stall 0, 접속 그대로. `startDb` 뒤 다음 경기 기록이 저장됐다(saved 1 → 2, 서버 재시작 없음). 50명·100명 같은 결과.
- **restart_repeat**(20명): 5번 모두 Exit Code 0(Kill 아님), 종료 117–138 ms, 시작 258–265 ms, 새 프로세스마다 Working Set 59 MB·Managed 4.5 MB(누적 없음).

**측정으로 확인된 것**
- **서 있는 조각 수가 Tick을 가장 크게 올린다.** 50명에서 0 → 20,000조각에 p50이 0.15 → 0.83 ms(5.5배), 100명에서 0.30 → 1.51 ms. 할당·GC·Send는 늘지 않는다. 원인 분석(이동 충돌 후보 수집인지, 다른 것인지)은 이번에 재지 않았다(Profiling 후보). Match 예산 20,000이 상한이라 그 이상은 시험할 수 없다.
- **가장 큰 단일 Tick은 100명 동시 접속(18.9 ms)과 접속 Ramp(11.6–14.0 ms)다.** 30 Hz 예산(33 ms) 안이고 D42 경고는 없었다.
- **대량 붕괴와 대량 탈락은 한 Tick에 몰려도 싸다**: 1,000조각 붕괴 3.8 ms, 92명 동시 탈락 2.6 ms.
- **Abuse는 격리된다**: Invalid Packet·Build Spam Client는 Kick되고, 정상 Client는 끊기지 않고 건설·사격을 계속했다. 100명 50/s Invalid Packet 실행은 할당 1.2 MB/s·Gen2 1번이었다(그 실행의 Rejoin은 530번이다. 할당이 거기서 나오는지는 재지 않았다).
- **DB 장애가 Game Loop에 닿지 않는다**: Queue가 용량에서 거절하고, 재시도는 기록마다 3번에서 끝나며(failed 증가로 확인), Tick·접속은 그대로, DB가 돌아오면 다음 기록부터 저장된다.
- **밀도(Hotspot)는 50명에서 비용 차이를 만들지 않았다.**

**알려진 제한**(Phase A 제한에 더해)
- 1 s Sample의 Send·CPU는 부정확하다(위). Burst 송신량은 구간 평균 차이로만 말한다.
- lag_compensation의 명중 수는 Group 크기·사망·Strafe가 섞인 값이다. 되감기 정확도 자체가 아니라 같은 조건 비교용이다.
- zone·elimination_burst는 Seed 8600의 Zone 중심을 쓴다. build_destruction의 구조물은 맵 중앙(Crossroads 광장)에 선다.
- build_spam 15/s는 거절이 빨라 Queue 경로(RateLimited)를 거의 지나지 않는다. RateLimited는 40/s에서만 보였다.
- input_timeout의 침묵 Client 8명, db_down의 QueueCapacity 1 등 서버 설정은 `server.options`에 고정이다(변수를 쓸 수 없다).

**Regression**(§125, §79)
- 새 시나리오 18개는 이번이 첫 실행이라 비교할 이전 값이 없다(history에 첫 줄이 생겼다).
- HeadlessActor·LootBrain을 바꾼 뒤 `suite:stress-quick`을 다시 돌렸다(5/5 PASS, 4 min 20 s, Baseline Warning 없음). steady 30 s 값(Phase A 표는 60 s라 같은 조건이 아니다): baseline p95 0.207(Phase A 0.185), movement 0.236(0.211), combat 0.294(0.285), building 0.554(0.569), mixed 0.414(0.390) ms. 같은 크기의 흔들림이고 Regression으로 판단하지 않는다.
- Warning 1건: build_spam 40/s 재실행(qa-20261002-225621-e474)에서 `stress.managedMB (steady) 6.253 → 16.427 (+162.7 %)` Baseline Warning. 그 시나리오는 구간 안에 GC가 한 번 일어나 Managed 끝값이 GC 시점에 따라 크게 달라진다(첫 실행도 6.3/17.6 MB 끝/최대). 실패가 아니고 GC 시점 차이로 본다(§136).

**다음 후보**: 조각 수에 따른 Tick 증가의 원인 Profiling(이동 충돌·관심 영역 계산 중 어느 것인지), 100명 전투·Rejoin의 할당 출처, 동시 접속 첫 Tick 18.9 ms의 내역, 30분 이상 Soak, Release 빌드로 같은 표 다시 재기.

## Editor 검증 (로드맵 STEP 2, 2026-10-05)

로드맵(`Docs/requests/2026-10-05-roadmap-phase13_5-19-request.md`) §9·§10의 화면 항목. Development Player(main 0492bb0 + 아래 수신기 수정)로 Unity 시나리오 2개를 돌리고 스크린샷을 한 장씩 보고 판정했다. 화면이 잠기지 않은 데스크톱에서 찍었다(실제 렌더링). 판정은 사람이 아니라 스크린샷을 읽은 에이전트가 했다.

| 항목 | 결과 | 근거 |
|---|---|---|
| Title, 한글 글꼴 | PASS | 타이틀(주소·포트·이름·버튼·키 안내) 한글이 모두 보인다 |
| Connect, In Game HUD | PASS | `visual_screens` hud_names: 생존 수, 자기장 시간, POI 이름, 체력·실드, 슬롯 |
| Player Name | PASS(설계대로) | 머리 위 이름은 없다(Phase 11 D9). Kill Feed와 결과의 승자 이름에 나온다 |
| ESC Menu | PASS | 메뉴 4버튼(계속하기, 내 전적, 접속 끊기, 게임 종료) |
| F1 Debug | PASS | 상태·이동·건설 세 줄, POI 이름과 겹치지 않는다 |
| Kill Feed | PASS | "자기장 ▸ qa-rival1"(QA killPlayer는 처치자가 없어 자기장으로 표시된다. 규칙대로) |
| Result | PASS | 승리, 순위 1/3, 처치, 승자, 다음 판까지 60초, 버튼 2개 |
| Statistics | PASS | DB가 꺼져 있어 "기록을 볼 수 없음"과 닫기(무한 대기 아님) |
| Disconnect / Reconnect | PASS | 서버 Crash 6초 뒤 "연결이 끊겼습니다 · 서버의 응답이 끊겼습니다 · 재접속 중 (1/3)"; 같은 Port로 다시 띄우자 재입장(대기 1/3) |
| Construction Visual | PASS | 실제 입력으로 지은 나무 벽이 낮게 보였다가 2초 뒤 다 자랐다. 피해로 보이지 않는다 |
| Wall / Floor / Ramp / Roof, Wood / Stone / Metal | PASS | 재료 3색이 구분되고 magenta가 없다. 지붕은 그 층을 덮는다(벽 위 level 0) |
| Material HUD | PASS(다듬기 필요) | "나무 120 돌 80 금속 40"이 오른쪽 아래에 나온다. Development Build 워터마크와 겹친다(Editor·Release에는 워터마크가 없다) |
| Structure Damage | PASS(다듬기 필요) | 실제 사격 3발(150→90): 25 % 어두워짐(대비가 약하다). 6발(→30): 붉은 단계가 뚜렷하다 |
| Collapse Visual | PASS | 지지벽 파괴 150 ms 뒤 다리 두 칸이 사라지고 먼지가 보인다. 1.5초 뒤 공중에 남은 조각이 없다 |
| Build Preview(유효/무효), Turbo Build, 채집, Build Collision, 도구 전환 | PASS(실제 입력) | `Building/unity_build_input.json`(가상 Input System 장치, 2026-10-05 추가): 파란 유효 유령 → 배치 → 같은 자리 빨간 무효 유령, 자기 벽에 막힘(z < -15), Turbo로 칸 둘레 벽, 나무 채집, Q·F·1 전환. 3번 연속 PASS |
| Korean IME, 조작감 | 사람 확인 남음 | IME는 OS 입력기를 거쳐서 가상 장치로는 검증되지 않는다. `Manual/editor_ui.json`·`Manual/editor_building.json` |

발견한 문제:
- **수정함(High, QA 도구):** Unity 수신기의 무작위 연결 끊김(위 "Unity Client Actor"의 수정). Unity 시나리오가 아무 Step에서나 실패했다.
- **다듬기(Known Issue):** 타이틀 키 안내에 Phase 12·13 키(C 웅크리기, Q 건축, F 채집, Z/X/V/B 조각, T 재료)가 없다. 자원 줄이 Development 빌드 워터마크와 겹친다. 손상 1단계(25 % 어둡게)의 대비가 약하다.
- Critical·High 게임플레이 문제는 없다.

## Adding New Actions

1. **Handler를 쓴다.** 비슷한 파일에 `DelegateAction(new ActionSpec { ... }, RunAsync)`를 추가한다.
   - `Actions/FlowActions.cs`: 대기, 검증, 그룹
   - `Actions/ActorActions.cs`: Actor 입력
   - `Actions/ServerCommandActions.cs`: 서버 명령
   - `Actions/FaultActions.cs`: 장애 주입(QA-3)
   - `Actions/StressActions.cs`: 측정 구간(`measure`)과 Actor Group(Stress). Group 행동은 `Stress/Brains.cs`의 `ActorBrain`이다(Pump Thread에서 Tick마다 의도를 정한다)
   - `Actions/StressPhaseBActions.cs`: Stress Phase B(`spawnBuildPieces`, `groupFireAt`, `groupConnect`, `groupInvalidPackets`, `groupBuildSpam`). 조각 Layout은 `Stress/BuildLayouts.cs`
   - `Actions/MatchLoopAction.cs`: `matchLoop`(Soak 경기 반복). 추세 계산은 `Stress/Measurement.cs`의 `MatchTrend`(순수)
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
