# QA Scenario Orchestrator

QA Tool은 서버를 띄우고 Headless Client(Actor)를 실제 게임 프로토콜로 움직인다. 서버의 QA Control API로 준비 상태를 만들고 서버의 권위 있는 상태를 검증한 뒤 Report를 남긴다. 시나리오는 JSON이고, 코드를 고치지 않고 작성 → 실행 → 실패 지점 기록 → 같은 Seed로 재실행할 수 있다.

- 요청: `Docs/requests/2026-10-02-qa-scenario-orchestrator-request.md`. § 번호는 이 요청서 기준이다.
- 설계: `Docs/specs/2026-10-02-qa-tool-design.md`. D 번호는 이 설계서의 결정이다.
- 현재 범위: QA-1(MVP). Web UI(QA-2), Fault Injection(QA-3), Unity 자동화·Manual Check(QA-4), Parameter·Repeat·Baseline(QA-5)은 아직 없다.

## Architecture

```text
QA Tool (Server/src/ProjectH.QA, .NET 10 콘솔)
 └ QaCli: run / validate / list
    └ QaOrchestrator: 시나리오 실행 1회 (RunContext: runId, seed, 변수, 취소)
        ├ ScenarioLoader / ScenarioValidator: JSON → DTO (schemaVersion 1)
        ├ ActionRegistry: action 이름 → IScenarioActionHandler
        ├ ServerProcessManager: launch | attach. QaServerClient (HTTP)
        ├ ActorManager → HeadlessActor (ProjectH.Bots 재사용). 테스트용 MockActor
        ├ AssertionEngine + Comparison: 경로 → 값 → 연산자
        ├ EventCursor: /qa/events 폴링 (필요할 때만)
        └ ReportWriter: report.json + report.html

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

**Step 공통 필드**

| 필드 | 의미 |
|---|---|
| `id` | Report에 나오는 Step 이름. 없으면 `NN-action`이다. 중복은 오류 |
| `action` | Action 이름. 축약형 `{ "assert": "path", ... }`는 `action: assert, path`와 같다 |
| `actor` | Actor id. 서버는 `qa-<id>`라는 DevPlayerId로 안다(D7) |
| `phase` | arrange / act / assert. 적은 Step부터 다음 phase가 나올 때까지 유지된다 |
| `timeoutMilliseconds` | Step Timeout. 기본 10 s이고 Action마다 다르다(아래) |
| `continueOnFailure` | true면 실패해도 다음 Step으로 간다. 시나리오 결과는 실패다 |
| `breakpoint` | QA-2 UI용. CLI는 로그만 남기고 멈추지 않는다 |
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
| `Stress/bots_50.json` | 133–134 | 50 Client가 60 s 경기 → tick p95 < 5 ms(느슨한 기준, 사용자가 조정). 측정값 0.15 ms | ~62 s |

**Suite**(`QA/Suites/`)

| Suite | 내용 | 시간 |
|---|---|---|
| `smoke` | connect, move_to, basic_hit | ~16 s |
| `pre-push` | §93: connect, move, shoot, pickup, death, reconnect | ~30 s |
| `full-regression` | 모든 카테고리. Stress 포함 | ~2.5 min |

**아직 없는 시나리오와 이유**

| 시나리오 | 이유 |
|---|---|
| Lag Compensation(§119) | 지연을 주입할 Proxy가 QA-3이다 |
| Invalid Packet(§127) | `sendRawPacket`이 QA-3이다(D14) |
| DB Down(§128) | Docker 제어가 QA-3이다 |
| Server Shutdown(§129) | 서버 재시작 Action이 QA-3이다 |
| Persistence | DB를 켠 시나리오는 QA-3에서 한다 |

## CLI

저장소 루트에서 실행한다. 서버를 먼저 빌드해야 한다(`dotnet build Server/ProjectH.Server.slnx`).

```bash
dotnet run --project Server/src/ProjectH.QA -- run QA/Scenarios/Combat/basic_hit.json   # 파일
dotnet run --project Server/src/ProjectH.QA -- run Combat                               # QA/Scenarios/Combat 전체
dotnet run --project Server/src/ProjectH.QA -- run pre-push                             # QA/Suites/pre-push.json
dotnet run --project Server/src/ProjectH.QA -- run Smoke --seed 7 --verbose
dotnet run --project Server/src/ProjectH.QA -- run Smoke --attach http://127.0.0.1:7780  # 이미 떠 있는 QA 서버
dotnet run --project Server/src/ProjectH.QA -- validate QA/Scenarios
dotnet run --project Server/src/ProjectH.QA -- list
```

| 옵션 | 의미 |
|---|---|
| `--seed N` | 시나리오 Seed를 덮어쓴다(같은 조건 재실행, §15) |
| `--attach URL` | 서버를 띄우지 않고 그 QA API에 붙는다. 게임 Port는 `/qa/health`에서 읽는다 |
| `--server-dll PATH` | 띄울 서버. 기본은 `Server/src/ProjectH.Server/bin/{Debug,Release}/net10.0/ProjectH.Server.dll` 중 최신 |
| `--report-dir DIR` | Report 위치. 기본 `QA/Reports` |
| `--poll-ms N` | waitFor·waitForEvent의 서버 폴링 간격, 20–5000(기본 100) |
| `--verbose` | Step 상세와 서버 로그를 콘솔에 낸다 |
| `--repo DIR` | 저장소 루트. 기본은 현재 폴더에서 위로 찾는다 |

- **종료 코드**(D18):
  - 0: 모두 PASS.
  - 1: 시나리오 실패, 또는 Ctrl+C로 중지.
  - 2: Tool 오류(잘못된 JSON·Validation 오류, 서버 기동 실패, 내부 예외, 띄운 서버를 끄지 못함, Report를 못 씀).
  - 여러 시나리오를 실행하면 가장 큰 값을 돌려준다.
- **Launch 모드**는 서버를 `DOTNET_ENVIRONMENT=Development`로 띄우고, 아래 인자를 준 뒤 시나리오 `server.options`를 붙인다.
  - `--qa-mode --Server:Port=0 --Qa:Port=0 --Persistence:Enabled=false --Server:AirDrop=false --Server:StartCountdownSeconds=1 --Server:ResultSeconds=1 --Server:LootSeed/ZoneSeed/SpawnSeed=<seed>`
  - Ready 조건은 `QA_READY` 줄과 `/qa/health`(20 s 이내)다. 고정 sleep을 쓰지 않는다(§81).
  - 끝나면 `/qa/server/stop` → 최대 10 s 종료 대기 → 그래도 남으면 자기 자식 프로세스만 Kill한다.
- **Ctrl+C**: 현재 Step을 멈추고 Cleanup(Actor 종료, 서버 정지)을 한 뒤 Report를 쓴다.
- **콘솔 출력**은 Step마다 한 줄(`01 Connect playerA PASS 643 ms`)이다. 실패하면 Expected / Actual이 붙는다.

## Reports

`QA/Reports/<runId>/report.json`과 `report.html`이다. HTML은 외부 리소스가 없는 단일 파일이다. `QA/Reports/`는 gitignore에 있다. runId 형식은 `qa-yyyyMMdd-HHmmss-xxxx`다.

- Summary: 결과, runId, seed, 시나리오 파일, 시작 시각, 시간, 종료 코드, Git commit과 dirty 여부(§99–100), 서버 모드·Port·pid·버전
- Step 표(id, 제목, phase, 결과, ms, 메시지, Expected/Actual)
- 실패: 실패 Step과 Expected / Actual(§102)
- 실패 시 State Dump: Actor 상태, 서버 플레이어 전체, Match(§103–104)
- 최근 이벤트 50개(§105), Metrics Snapshot, 서버 로그 마지막 200줄, 서버 실행 명령
- Cleanup 결과. 결과와 따로 적는다(§114)
- 변수 최종값, Validation 경고, Manual Check·Screenshot(QA-4 전까지 비어 있음)

## Fault Injection

QA-3에서 한다. 아직 구현하지 않았다. 설계(D13)는 다음과 같다.
- QA Tool 쪽 UDP Proxy로 Latency·Jitter·Loss·Duplicate·Block을 준다.
- DB는 Docker 컨테이너를 Stop/Start한다(Docker가 없으면 Skip).
- 서버 네트워크 코드는 바꾸지 않는다(§72).

지금 할 수 있는 장애는 두 가지뿐이다.
- `disconnect`의 `graceful: false`: Client가 갑자기 사라진다.
- `pauseInput`: 연결은 살아 있는데 Input이 끊긴다.

## Adding New Actions

1. **Handler를 쓴다.** 비슷한 파일에 `DelegateAction(new ActionSpec { ... }, RunAsync)`를 추가한다.
   - `Actions/FlowActions.cs`: 대기, 검증, 그룹
   - `Actions/ActorActions.cs`: Actor 입력
   - `Actions/ServerCommandActions.cs`: 서버 명령
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

3. **Handler 규칙**
   - `StepContext`로 인자를 읽는다(`String/Double/Int/Bool/Position`). 이 함수들이 `${}`를 치환한다.
   - 기다릴 때는 `ctx.TimedOut`을 보고, 자기 Timeout에 `StepOutcome.Fail(message, expected, actual)`을 돌려준다.
   - 잘못된 인자는 `QaStepException`을 던진다(Step 실패). 그 밖의 예외는 Tool 오류(종료 코드 2)가 된다.
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
