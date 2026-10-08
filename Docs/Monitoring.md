# Monitoring

Game Server를 실시간으로 보는 가벼운 내부 도구다. 요청서 `Docs/requests/2026-10-08-monitoring-request.md`, 설계 `Docs/specs/2026-10-08-monitoring-design.md`(D1–D18), 계획 `Docs/plans/2026-10-08-monitoring.md`.

## Architecture

```text
Game Server (ProjectH.Server)                        Monitoring Server (ProjectH.Monitoring)
┌──────────────────────────────────────────┐         ┌──────────────────────────────────────────┐
│ Game Loop thread                         │         │ Kestrel 127.0.0.1:5080                   │
│  every tick: RecordTick(ms)              │  HTTP   │  POST /api/ingest/metrics                │
│  every 5 s : Publish → MonitoringSlot ───┼─POST───▶│   size → token → JSON → SnapshotValidator│
│ MonitoringSender (BackgroundService)     │ 2 s     │   → MetricStore (one lock, ring/server)  │
│  Take() → HttpClient, best effort        │ timeout │  GET /api/servers, /{id}, /{id}/metrics  │
└──────────────────────────────────────────┘         │  OfflineSweeper (log only)               │
                                                     │  wwwroot (uPlot, polling 2 s / 5 s)      │
                                                     └──────────────────────────────────────────┘
```

- 둘은 별도 프로세스다. `ProjectH.Monitoring.Contracts`(Snapshot DTO와 상수)만 공유하고, Monitoring Server는 `ProjectH.Server`·`ProjectH.Shared`를 참조하지 않는다.
- **Monitoring 장애는 Game Server에 닿지 않는다.**
  - Game Loop는 HTTP를 모른다. 5초마다 Snapshot을 슬롯 하나에 넣을 뿐이다.
  - Snapshot 생성이 예외를 내도 Tick 실패가 아니라 Loop 실패로 센다. Tick 실패가 이어지면 경기가 리셋되기 때문이다.
  - Sender는 실패한 Snapshot을 버리고 다음 주기에 새 것을 보낸다. 로그는 상태가 바뀔 때만 남긴다(`Monitoring connection lost` Warning 1줄, `restored` Information 1줄).
  - 슬롯은 용량 1이라 Monitoring Server가 느리거나 죽어 있어도 Game Server가 들고 있는 것은 Snapshot 하나다.
- **Heartbeat는 Snapshot 자체다.** Sender는 슬롯이 비어 있으면 보내지 않는다. 그래서 Game Loop가 멈추면 Snapshot이 끊기고, Monitoring Server는 `OfflineThresholdSeconds` 뒤 그 서버를 Offline으로 본다.
- **Snapshot 주기는 고정이다.** `Stats` 줄의 `nextStats`처럼 기한을 주기만큼 더해 가므로 창이 늘어나지 않는다. Game Loop가 오래 멈췄다 돌아오면 밀린 것을 몰아서 내지 않고 한 번만 낸다.

## How to Start

```bash
dotnet run --project Server/src/ProjectH.Monitoring                        # http://127.0.0.1:5080
dotnet run --project Server/src/ProjectH.Server -- --Monitoring:Enabled=true
```

PowerShell에서 환경변수로 켤 때: `$env:Monitoring__Enabled='true'; dotnet run --project Server/src/ProjectH.Server`.

브라우저에서 `http://127.0.0.1:5080`을 연다. 두 프로세스는 어느 쪽을 먼저 띄워도 된다.

## Game Server Configuration (`Monitoring` 섹션)

| 키 | 기본 | 범위 | 뜻 |
|---|---|---|---|
| `Enabled` | `false` | | 꺼져 있으면 슬롯·수집기·Sender·HttpClient를 만들지 않는다. Tick당 null 검사만 남는다 |
| `Endpoint` | `http://127.0.0.1:5080` | 절대 http/https URL | Monitoring Server 주소. 끝에 `/api/ingest/metrics`를 붙여 보낸다 |
| `ServerId` | `dev-server-01` | `[A-Za-z0-9._-]{1,64}` | 이 서버의 이름. 서버마다 달라야 한다 |
| `IntervalSeconds` | `5` | 1–60 | Snapshot 주기 |
| `TimeoutSeconds` | `2` | 1–30, ≤ Interval | 요청 하나의 상한. 주기보다 길 수 없으므로 요청이 겹치지 않는다 |
| `Token` | 없음 | | `X-Monitoring-Token` 값. **환경변수 `Monitoring__Token`으로만 넣는다**(appsettings.json에는 키가 없다) |

값이 틀리면 `PersistenceOptions`처럼 호스트가 시작하지 않는다. 환경변수 이름은 `Monitoring__키`, 명령줄은 `--Monitoring:키=값`이다.

## Monitoring Server Configuration

| 키 | 기본 | 범위 | 뜻 |
|---|---|---|---|
| `Urls`(최상위) | `http://127.0.0.1:5080` | | Bind 주소. 기본으로는 외부에 열지 않는다 |
| `MonitoringServer:HistoryMinutes` | `10` | 1–120 | 서버별로 메모리에 남기는 History 길이 |
| `MonitoringServer:ExpectedIntervalSeconds` | `5` | 1–60 | Game Server의 `IntervalSeconds`. 링 용량 = HistoryMinutes × 60 ÷ 이 값(기본 120개) |
| `MonitoringServer:OfflineThresholdSeconds` | `15` | ≥ 2 × ExpectedInterval, ≤ 3600 | 마지막 수신 뒤 이 시간을 넘으면 Offline이다. 15 s는 주기의 3배라 전송 한 번 실패(Timeout 2 s)로는 Offline이 되지 않는다 |
| `MonitoringServer:SweepIntervalSeconds` | `5` | 1–60 | "offline" 로그를 찾는 주기. 상태 자체는 읽을 때 계산한다 |
| `MonitoringServer:MaxServers` | `32` | 1–1000 | 이보다 많은 새 ServerId는 429로 거절한다 |
| `MonitoringServer:MaxClockSkewSeconds` | `300` | 1–86400 | `observedAt`이 이 서버의 시계와 이보다 크게 어긋나면 400 |
| `MonitoringServer:TickP95WarningMs` | `16.7` | ≥ 0 | 30 Hz Tick 예산 33.3 ms의 절반 |
| `MonitoringServer:MemoryWarningBytes` | `157286400`(150 MB, appsettings) / 코드 기본 `0`(꺼짐) | ≥ 0 | Working Set 경고 기준. 근거는 "측정" 절(50명 측정 최대 73.1 MB의 약 2배) |
| `MonitoringServer:IngestToken` | 없음 | | 설정하면 Token이 없거나 틀린 Ingest는 401이다. **환경변수 `MonitoringServer__IngestToken`으로만 넣는다** |

## Metrics (Snapshot 필드)

모두 Game Server가 이미 가진 값이다. 추정한 값은 없다.

| 필드 | 단위 | 기준 | 출처 |
|---|---|---|---|
| `serverId`, `version`, `protocolVersion` | | | 설정, 어셈블리 InformationalVersion(`1.0.0+커밋`), `ProtocolConstants.ProtocolVersion` |
| `startedAt`, `observedAt` | UTC | Game Server 시계로 잰 시작 시각과 Snapshot을 만든 시각 | 저장할 때 Monitoring Server가 자기 시계로 `receivedAt`을 따로 붙인다 |
| `uptimeSeconds`, `windowSeconds` | s | 창 = 직전 Snapshot부터 실제 경과 | |
| `connectedPeers`, `players`, `graced` | | 지금 값. `players`는 유예(재접속 대기) 중인 플레이어를 포함한다 | Health 줄의 peers/players/graced와 같은 값 |
| `matchState`, `round` | | 서버당 경기 하나 | `MatchFlowState` 이름 |
| `tickSamples`, `tickP50Ms`, `tickP95Ms`, `tickP99Ms`, `tickMaxMs` | ms | 이 창의 Tick. nearest-rank 백분위로, Stats 줄과 같은 `TickMetrics` 클래스다 | Monitoring 전용 링. 용량은 SimHz × (Interval + 1)이다. Stats 줄의 링은 10초마다 리셋되므로 따로 둔다 |
| `cpuPercent` | % | **이 프로세스**의 CPU 시간 ÷ (창 × 논리 코어 수) × 100. 모든 코어를 다 쓰면 100이며 Stats 줄의 `cpu%`와 같은 식이다 | `Environment.CpuUsage` |
| `managedMemoryBytes`, `workingSetBytes` | B | 지금 값 | `GC.GetTotalMemory(false)`, `Environment.WorkingSet` |
| `gcGen0`, `gcGen1`, `gcGen2` | 회 | 시작부터 누적 | `GC.CollectionCount` |
| `packetsIn/OutPerSecond`, `bytesIn/OutPerSecond` | /s | 이 창. LiteNetLib 페이로드 기준이라 IP/UDP 헤더는 빠진다 | `ServerStats` 누적값의 차이. Stats 줄의 `TakeDelta`는 건드리지 않는다 |
| `exceptions` | 회 | 시작부터 누적 = tickFailures + loopFailures + playerFailures + callbackErrors | `HealthCounters` |
| `invalidPackets` | 회 | 시작부터 누적, badPackets 전체 | `HealthCounters.BadPacketsTotal` |
| `disconnects` | 회 | 시작부터 누적 = timeout + other | `HealthCounters` |
| `dbQueueCount`, `dbQueueCapacity` | 건 | 경기 기록 큐(`MatchHistoryQueue`)의 지금 수와 용량 | |

비유한값(창이 0초일 때의 비율 등)은 0으로 보낸다. 대시보드의 Offline 판단과 그래프 X축은 `receivedAt` 기준이다.

## API

| 경로 | 응답 |
|---|---|
| `POST /api/ingest/metrics` | 202(본문 없음). 413 본문 16 KB 초과, 401 Token 틀림, 400 JSON·값 오류(`{"error": "..."}`)나 잘린 본문·깨진 chunked 인코딩, 429 MaxServers 초과 |
| `GET /health` | `{"status":"ok","servers":n}` |
| `GET /api/servers` | `[{serverId, status, lastReceivedAt, lastObservedAt, warnings[], latest}]`, 이름순. `status`는 `online`/`warning`/`offline`, `latest`는 Snapshot 그대로다 |
| `GET /api/servers/{id}` | 위 항목 하나 + `thresholds{tickP95WarningMs, memoryWarningBytes, offlineThresholdSeconds, historyMinutes}`. 모르는 id는 404 |
| `GET /api/servers/{id}/metrics?minutes=5` | `{serverId, minutes, samples[{receivedAt, snapshot}]}`, 오래된 순. `minutes`는 1–HistoryMinutes로 잘린다. 숫자가 아니면 기본값 5를 쓴다 |

손으로 하나 보내 보기(`observedAt`은 지금 시각 ±300 s 안이어야 한다):

```bash
curl -s -X POST http://127.0.0.1:5080/api/ingest/metrics -H 'Content-Type: application/json' \
  -d '{"serverId":"dev-server-01","version":"1.0.0","protocolVersion":18,"startedAt":"2026-10-08T12:00:00Z","observedAt":"2026-10-08T12:05:00Z","uptimeSeconds":300,"windowSeconds":5,"matchState":"Playing","players":2,"tickP95Ms":0.4,"dbQueueCapacity":16}'
curl -s http://127.0.0.1:5080/api/servers
```

## Web UI

정적 파일 세 개(`wwwroot/index.html`, `app.js`, `app.css`)로 되어 있고 빌드 단계가 없다. Dark 테마다.

- **화면 구성:** 왼쪽에 서버 목록(상태 점, Players), 오른쪽에 선택한 서버가 나온다. 선택은 URL `#serverId`로 남는다.
- **카드:**
  - Status(Uptime, 버전, protocol)
  - Players(Peers, Graced)
  - Match(State, Round)
  - Tick P95(P50, P99, Max, 샘플 수)
  - CPU("process, all cores = 100 %")
  - Memory(Managed, Working Set, GC)
  - Network(Send/Receive KB/s, pkt/s)
  - Errors(Exceptions, Invalid packets, Disconnects)
  - DB Queue
- **그래프:** 4개이고 각자 자기 Y축을 쓴다. Tick P50/P95/P99(ms), CPU(%), Memory(MB), Network(KB/s)이며 최근 5분을 보여 준다.
- **갱신:** 목록과 카드는 `/api/servers`를 2 s마다, 그래프는 `/api/servers/{id}/metrics?minutes=5`를 5 s마다 Polling한다.
- **Offline:** 빨간 배너 "OFFLINE · last seen …"이 뜨고 카드 값이 흐려진다. 마지막으로 받은 값이지 지금 값이 아니라는 표시다.
- **경고:** 경고 문장이 나오고 해당 카드 테두리가 amber로 바뀐다.

**차트 라이브러리는 uPlot 1.6.31이다.**
- MIT, 의존성 없음, `wwwroot/lib/uPlot.iife.min.js` 50,312 B.
- 캔버스 기반이라 선 그래프 4개를 몇 초마다 다시 그려도 가볍고, 빌드 체인이 필요 없다.
- 저장소에 넣어 두었으므로 인터넷이 없어도 동작한다. 라이선스는 `wwwroot/lib/uPlot.LICENSE`에 있다.

**수동 확인(요청 §68).** 브라우저 자동화는 없다. 확인 결과는 "측정" 절의 표에 있다.

## Security

- 기본 Bind는 127.0.0.1이다. 외부 공개, TLS, Reverse Proxy, Web UI 인증은 이 버전에 없고 별도 Phase로 둔다.
- Ingest Token은 선택이다(`MonitoringServer__IngestToken` ↔ `Monitoring__Token`). 비교는 고정 시간으로 한다. Secret은 Repository에 없다.
- Ingest 검증은 다음 순서다: 본문 16 KB 상한 → Token → JSON → 값.
  - 값 검증 항목은 ServerId 규칙, 시각, 음수, NaN/Infinity, Version·MatchState의 길이와 문자다.
  - 서버 수는 MaxServers로 제한한다.
  - 잘못된 요청 로그는 분당 1줄로 묶는다. 검증에 실패한 ServerId는 로그에 쓰지 않는다.

## Adding a New Metric

1. `ServerMonitoringSnapshot`(Contracts)에 속성 하나를 더한다.
2. `MonitoringCollector.Publish`에 값 한 줄을 더한다. Game Loop 스레드가 이미 가진 값만 쓰고, 비유한값은 `Finite()`로 거른다.
3. 범위 검사가 필요하면 `SnapshotValidator`에 한 줄을 더한다(double은 `Check`, 정수는 음수 검사).
4. `app.js`에 카드나 그래프를 더한다.

Store와 API는 Snapshot을 그대로 통과시키므로 고칠 필요가 없다. 경고 규칙은 `ServerStatus.Of`, 임계값은 `MonitoringServerOptions`에 있다.

## Known Limitations

- History는 메모리에만 있다(기본 10분). Monitoring Server를 재시작하면 비고, 장기 저장과 Downsampling은 없다.
- **한 번 등록된 ServerId는 재시작할 때까지 남는다.** 서로 다른 ID가 `MaxServers`(32)개 쌓이면 새 ID는 429를 받는다. 죽은 서버의 "OFFLINE · last seen"을 계속 보여 주려고 지우지 않는다.
- Realtime이 아니다(Polling 2 s / 5 s). SignalR은 없다.
- 알림(Discord/Slack/Email)이 없다. UI 경고만 있다.
- Web UI에 인증과 TLS가 없으므로 내부 네트워크에서만 쓴다.
- 서버 하나에 경기 하나다. `activeMatches` 필드는 없다.
- Sender는 실패한 Snapshot을 버린다. Monitoring Server가 내려가 있던 동안의 값은 남지 않는다.
- Game Server의 Snapshot 주기와 Sender의 전송 주기는 서로 독립된 타이머다. 그래서 도착한 Snapshot이 최대 한 주기만큼 오래된 것일 수 있다.
- Network 값은 LiteNetLib 페이로드 기준이라 OS 수준 트래픽보다 작다.
- 브라우저 화면은 자동 테스트가 없다(수동 확인).
- Windows에서 닫힌 localhost 포트는 연결 거부까지 약 2 s가 걸린다. 그래서 Monitoring Server가 꺼져 있으면 사유가 `refused`가 아니라 `timeout after 2 s`로 로그에 남는다.

## 측정 (2026-10-08, i9-14900K 24코어/32스레드, Windows 11, Release)

### 성능 비교 (요청 §90) — QA `Stress/baseline.json` 50명, steady 60 s

| 실행 | Tick P50 | Tick P95 | Tick P99 | Tick Max | CPU (process) | Managed | Working Set |
|---|---|---|---|---|---|---|---|
| Monitoring OFF #1 | 0.036 ms | 0.051 ms | 0.069 ms | 0.12 ms | 0.107 % | 6.95 MB | 67.9 MB |
| Monitoring ON #1 | 0.036 ms | 0.058 ms | 0.096 ms | 0.314 ms | 0.170 % | 7.37 MB | 73.1 MB |
| Monitoring OFF #2 | 0.035 ms | 0.054 ms | 0.085 ms | 0.145 ms | 0.129 % | 6.94 MB | 67.9 MB |
| Monitoring ON #2 | 0.033 ms | 0.052 ms | 0.068 ms | 0.113 ms | 0.123 % | 7.34 MB | 72.6 MB |

- 실행 순서는 OFF, ON, OFF, ON이다. 모두 PASS했다(Stall 0, Tick Failure 0, 50명 유지). 네트워크는 네 번 모두 send 496.1 KB/s, recv 134.8 KB/s였다.
- 실행 직전에 같은 기계의 다른 dotnet 테스트·빌드 프로세스를 셌다. OFF #2 시작 때만 2개가 있었고(machine CPU 9 %) 나머지는 0개였다.
- **Tick:** ON #1의 P99·Max가 가장 컸지만 ON #2는 OFF 두 번과 같은 범위다. P95 차이(0.051–0.058 ms)는 OFF 두 실행 사이의 차이(0.051 vs 0.054)와 크기가 비슷하다. 이 측정으로는 Tick 회귀가 보이지 않는다. Tick 예산은 33.3 ms다.
- **Memory:** ON에서 Working Set이 약 5 MB, Managed가 약 0.4 MB 더 컸다. Kestrel 없이 HttpClient·SocketsHttpHandler와 Monitoring 링을 더한 비용으로 보인다.
- **수신 확인:** ON 실행의 steady 중간(60 s)에 `/api/servers`를 읽었다. `players 50, peers 50, send 495.8 KB/s`가 Online으로 나왔다.

### 네트워크 (요청 §92)

- Interval: 5 s(설정). Snapshot 창은 실제로 5.000 s였다(고정 주기 기한).
- Payload: Snapshot JSON 734 B(실제 수신값을 같은 camelCase로 직렬화한 크기). 한도는 16 KB다.
- Timeout: 2 s(설정). 응답 본문은 읽지 않는다.

### 장애 검증 (요청 §91) — 실제 프로세스, Game 7793 / Monitoring 5183

| 상황 | Game Server 로그 | Stats 줄 | tick/loop Failures | Monitoring Server |
|---|---|---|---|---|
| 정상 | 경고 없음 | 10 s마다 | 0 / 0 | `first seen`, `online`, tickSamples 150, window 5.000 s |
| Monitoring 종료(35 s) | `Monitoring connection lost (timeout after 2 s)` **1줄** | 10 s마다 계속 | 0 / 0 | — |
| Monitoring 재시작 | `Monitoring connection restored after 7 failed posts` 1줄 | 10 s마다 계속 | 0 / 0 | `first seen`(재시작이라 History가 비어 있다), 다시 `online` |
| Game Server 종료 | — | — | — | 15 s 뒤 `offline (last seen …)` 1줄. API는 `offline`과 마지막 값, 경고 `offline: no snapshot for 25 s` |
| 잘못된 Endpoint(`nonexistent.invalid`) | `lost (알려진 호스트가 없습니다 …)` 1줄 | 10 s마다 계속 | 0 / 0 | — |
| Connection Refused(`127.0.0.1:1`) | `lost (timeout after 2 s)` 1줄. Windows에서는 연결 거부가 Timeout보다 늦다 | 10 s마다 계속 | 0 / 0 | — |
| Timeout·느린 응답 | 자동 테스트(`MonitoringSenderTests`): 3 s 지연은 Timeout(300 ms) 안에 실패, 20 MB 느린 응답 본문은 읽지 않고 상태 코드로 495 ms에 판정 | | | |

- 프로세스는 `Stop-Process`로 강제 종료했다. Ctrl+C 정상 종료는 이 환경에서 보낼 수 없어 확인하지 못했다. 종료 순서(Sender가 Game Loop보다 먼저 멈춘다)는 `MonitoringSetupTests`가 고정한다.

### MemoryWarningBytes

위 측정의 Working Set 최대는 73.1 MB다. 그 약 2배인 **150 MB(157,286,400 B)**를 `appsettings.json` 기본값으로 둔다(코드 기본값은 0 = 꺼짐). 이 기준은 접속만 한 50명 기준이다. 건설이 많은 경기나 100명에서는 더 클 수 있으므로, 그 측정을 한 뒤 다시 정한다.

### Web UI 수동 확인 (요청 §68)

| # | 확인 | 결과 |
|---|---|---|
| 1–11 | 서버 목록, ONLINE, Players, Tick, CPU, Memory, Network, 그래프, OFFLINE 배너, 재시작, 900 px | **미확인(브라우저 필요)**. 대신 다음을 확인했다. ① 정적 파일 6개가 200이다. ② app.js가 읽는 필드 45개가 실제 API JSON에 모두 있다(node 스크립트로 대조). ③ `node --check` 문법 통과. ④ `online`·`warning`·`offline` 상태와 경고 문장이 app.js의 분기와 맞는다 |
