# Phase 10 Hardening — 설계 Spec

## Context

원래 요청서의 Phase 10은 다음을 구현한다.

```text
Disconnect 처리 / Timeout / Invalid Packet / Server Shutdown / Exception Recovery / Monitoring
```

앞 Phase에서 이 단계로 미룬 것:

- 재접속: Phase 5 D10(경기 중 이탈은 바로 탈락), Phase 7 D9(봇 재접속)
- DB 재연결: Phase 9 D7·D10(시작할 때 DB가 없으면 그 실행은 저장하지 않는다)

지금 있는 것(Phase 9 기준, `main` bf28afa):

- **연결 끊김:**
  - LiteNetLib `DisconnectTimeout` 5초, Ping은 1초 이하 간격이다.
  - 끊긴 peer는 Control 채널과 stale-peer 정리로 지워지고 `Match.Leave`로 간다.
  - 경기 중이면 바로 탈락하고 Loot를 떨어뜨린다.
- **잘못된 패킷:**
  - 연결 요청은 버전·형식·인원으로 거절한다(`RejectReason`).
  - `PacketReader`는 예외를 던지지 않는다.
  - 잘못된 패킷 20개면 끊는다. 입력은 초당 `SimHz*2`로 제한한다.
  - 입력 값(NaN 등)은 쓰는 곳에서 검사한다(Phase 3 D14).
- **예외:** Tick 하나(`RunTick`)를 try/catch로 감싸 세고, 구간마다 첫 예외만 로그로 남긴다.
- **종료:** Host `StopAsync` → `GameLoop.Stop`(스레드 Join, 시간 제한 없음) → `NetManager.Stop(true)` → DB Writer Drain.
- **관측:** 10초 Stats 한 줄(트래픽, Tick ms, drop, badPackets, exceptions, GC, 메모리, cpu%, matchSinkFailures).

**빈 곳:**

- **연결 끊김:**
  - 서버가 끊을 때 이유를 보내지 않는다. Client는 종료와 Kick을 구분하지 못한다.
  - 재접속이 없다.
- **Timeout:**
  - 접속만 하고 Join하지 않는 peer가 자리를 계속 차지한다.
  - 멈춘 Client(LiteNetLib 스레드만 Pong을 보냄)는 영원히 서 있다.
- **잘못된 패킷:**
  - `badPackets`는 이유 구분이 없다.
  - 거절된 연결 요청은 세지 않는다.
  - 무작위 입력(Fuzz) 테스트가 없다.
- **예외:**
  - Tick 밖(Stats, 대기)의 예외는 Game Loop 스레드를 죽인다. 그러면 프로세스가 죽는다.
  - Tick이 계속 실패해도 멈추지 않는다.
  - Network 스레드 핸들러에는 예외 처리가 없다.
- **종료:** Client에 종료를 알리지 않는다. Game Loop 스레드가 멈추지 않으면 종료도 멈춘다.
- **관측:**
  - 연결·Kick·거절·DB 카운터가 주기 로그에 없다.
  - 로그에 시각이 없다.
  - 외부 도구로 읽을 수치가 없다.
  - Game Loop가 멈춰도 알 수 없다.

**성공 기준:**

- 서버가 끊을 때는 이유 코드(종료, Kick, Timeout, 서버 오류)를 보낸다. Client는 그 이유를 보여 주고, 다시 접속해도 되는 경우에만 자동으로 재접속한다.
- 경기 중 연결이 끊긴 참가자는 10초 안에 같은 DevPlayerId로 다시 들어오면 자기 캐릭터(위치, 체력, 인벤토리, 순위 상태)로 돌아간다.
- Join하지 않는 연결, 입력이 끊긴 플레이어는 정해진 시간 뒤에 끊긴다.
- 무작위 바이트·무작위 입력 값이 서버를 멈추거나 상태를 깨지 못한다. 테스트로 보인다.
- 반복되는 Tick 예외는 경기를 초기화하고, 그래도 계속되면 서버를 오류 코드로 끝낸다. Game Loop 스레드는 예외로 죽지 않는다.
- 종료하면 Client가 "서버 종료"를 받고, 종료는 시간 제한 안에 끝난다.
- 연결·보호·DB 상태가 주기 로그와 `dotnet-counters`로 보인다. Game Loop가 멈추면 Critical 로그가 남는다.
- DB가 서버보다 늦게 떠도 기록이 저장된다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 진행 방식 | 묻지 않고 추천안대로 진행하고, 추천 이유는 이 문서에 남긴다. Phase마다 푸시한다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **끊는 이유 코드(Protocol v8).**<ul><li>Shared에 `DisconnectCode : byte`를 둔다: `None` 0, `ServerShutdown` 1, `Kicked` 2(잘못된 패킷), `JoinTimeout` 3, `InputTimeout` 4, `ServerError` 5(경기 초기화).</li><li>서버가 끊을 때 LiteNetLib 끊기 데이터 1바이트로 보낸다(`peer.Disconnect(byte[])`). Client는 `RemoteConnectionClose`의 추가 데이터에서 읽는다. 데이터가 없거나 모르는 값이면 `None`이다.</li><li>`JoinResult`에 `Resumed` 3을 더한다(D2).</li><li>`ProtocolVersion`은 8이다.</li></ul> | 따로 패킷을 보내면 끊기 직전 패킷이 끊김보다 늦거나 사라질 수 있다. 끊기 데이터는 끊김과 한 메시지라 순서 문제가 없다. 연결 거절의 `RejectReason`과 같은 방식이라 Client 처리도 같다. | 이유를 더 늘리면 enum 값만 더한다. 모르는 값은 `None`으로 읽어 옛 Client도 깨지지 않는다. |
| D2 | **재접속 유예(Grace).**<ul><li>경기 중(`Playing` 또는 `FinalPhase`. `DevRespawn` 서버는 경기 흐름이 없어 해당하지 않는다)에 살아 있는 참가자의 연결이 끊기면 바로 탈락시키지 않는다. 캐릭터는 그 자리에 남는다: 입력 없음, 맞으면 죽음, Zone 피해 있음.</li><li>`ReconnectGraceSeconds`(기본 10, 0–60, 0은 끔) 안에 같은 DevPlayerId로 Join하면 그 캐릭터를 새 peer에 다시 묶는다.<ul><li>응답: `JoinResult.Resumed`, 같은 Entity Id</li><li>그 뒤 늦은 합류와 같은 전체 상태를 보낸다: Catalog, 플레이어 목록, 월드 아이템, 인벤토리, 경기·Zone 상태.</li></ul></li><li>유예 중에 죽으면 보통 죽음과 같다. 순위가 정해지고, 다시 오면 관전자다.</li><li>유예 중에 경기가 끝나면 남은 사람으로 순위를 받는다.</li><li>시간이 다 되면 지금의 `Leave`와 같다: 탈락, Loot Drop, 이탈자 기록.</li><li>서버가 Kick한 연결(`Kicked`, `InputTimeout`)과 경기 밖·사망·관전 상태의 끊김은 유예 없이 바로 `Leave`한다.</li><li>같은 id로 **연결된** 플레이어가 있으면 지금처럼 새 플레이어로 합류한다(빼앗지 않는다). 같은 id의 유예 캐릭터가 여럿이면 먼저 끊긴 것을 쓴다.</li></ul> | 무선 끊김·Client 재시작 한 번으로 그 판을 잃지 않는다. 캐릭터가 남아 맞을 수 있으므로 끊어서 위험을 피하는 수단이 되지 않는다(Phase 5 D10의 취지 유지). DevPlayerId로 찾는 것은 인증이 없는 지금 구조와 같은 신뢰 수준이다. 연결된 플레이어를 빼앗지 않으면, 같은 기본 id로 Client 여러 개를 띄우는 개발 방식이 그대로 된다. | 인증이 없어 남의 id로 유예 캐릭터를 가져갈 수 있다. 개발 단계에서는 받아들이고, 인증 단계에서 세션 토큰으로 바꾼다. Client가 비정상 종료 뒤 서버가 옛 연결의 끊김을 알기 전(최대 `DisconnectTimeoutMs` 5초)에 다시 Join하면 연결된 같은 id로 보여 새 플레이어(관전자)가 된다. 정상 종료는 끊기를 바로 보내므로 해당하지 않는다. Client의 첫 재시도가 1초 뒤라 보통의 네트워크 끊김(양쪽이 같은 5초 Timeout)도 대부분 해당하지 않는다. |
| D3 | **Join Timeout.** 연결한 뒤 `JoinTimeoutSeconds`(기본 5, 1–60) 안에 Join하지 않으면 `JoinTimeout`으로 끊는다. `PeerState`에 연결 시각(Tick)을 둔다. | Client는 연결하자마자 Join을 보낸다. 오래 Join하지 않는 연결은 버그이거나 자리 차지(`MaxPlayers`는 연결 수로 센다)다. | 느린 Client가 끊길 수 있다. 그러면 값을 올린다. |
| D4 | **Input Timeout.** Join한 peer가 `InputTimeoutSeconds`(기본 10, 0은 끔, 아니면 2–300이고 `DisconnectTimeoutMs` + 2초 이상, §5) 동안 `PlayerInput`을 하나도 보내지 않으면 `InputTimeout`으로 끊는다. 대기·관전·사망 중에도 적용한다. | Client는 Join한 동안 살았든 죽었든 매 Tick 입력을 보낸다. LiteNetLib는 자기 스레드에서 Pong을 보내므로, Unity가 멈춰도 연결은 살아 있다. 멈춘 플레이어가 경기 내내 서 있지 않게 한다. | 입력을 안 보내는 도구 Client가 생기면 0으로 끈다. |
| D5 | **잘못된 패킷.**<ul><li>`badPackets`를 이유별로 센다: 모르는 Id, 형식 오류, Join 전 입력, 중복 Join, 입력 초과, 방향이 틀린 패킷(서버→Client 패킷), 처리 중 예외.</li><li>Kick은 `Kicked` 코드로 끊는다.</li><li>거절된 연결 요청을 이유별로 센다(`ServerFull`, `BadRequest`, `VersionMismatch`). 로그는 Debug다. 거절은 폭주할 수 있다.</li><li>`NetworkListener`의 받기 핸들러를 try/catch로 감싼다. 예외는 그 peer의 잘못된 패킷으로 세고, 구간마다 첫 예외만 로그로 남긴다.</li><li>시드 고정 Fuzz 테스트를 넣는다(§2).</li></ul> | 이유별 숫자가 있어야 공격, Client 버그, 버전 문제를 구분한다. 받기 핸들러가 예외로 LiteNetLib 스레드를 흔들면 모든 연결이 영향을 받는다. | 없음. |
| D6 | **예외 복구.**<ul><li>Game Loop의 반복 전체(Stats, 대기 포함)를 감싼다. 스레드는 예외로 죽지 않는다.</li><li>Tick 예외 로그에 Tick 번호와 경기 상태를 넣는다.</li><li>연속으로 `SimHz * 3`번(3초) Tick이 실패하면 경기를 초기화한다.<ul><li>모든 peer를 `ServerError`로 끊는다.</li><li>`Match`를 새로 만든다.</li><li>Client는 자동으로 다시 접속해서 새 판에 들어온다.</li></ul></li><li>10분 안에 초기화가 3번이면 Critical 로그를 남기고 서버를 끝낸다(`StopApplication`, 종료 코드 1).</li><li>sink 예외도 구간마다 첫 하나를 로그로 남긴다.</li><li>`AppDomain.UnhandledException`과 `TaskScheduler.UnobservedTaskException`을 로그로 남긴다.</li></ul> | 한 Tick의 예외는 지금처럼 넘어간다. 계속 실패하는 상태는 일부만 바뀐 경기 상태 때문일 가능성이 크다. 그 판만 버리면 서버는 산다. 초기화로도 안 되면 코드 문제이므로, 계속 도는 것보다 끝내서 감시 도구나 사람이 알게 하는 편이 낫다. | 진행 중인 판 하나를 잃는다(기록 없음). 그 판의 참가자에게는 `ServerError`가 보인다. |
| D7 | **종료.**<ul><li>순서:<ol><li>Game Loop 멈춤(Tick 중단)</li><li>모든 peer에 `ServerShutdown`으로 끊기</li><li>끊기 패킷이 나가도록 최대 1초(peer가 0이 되면 바로) 기다림</li><li>`NetManager.Stop`</li></ol></li><li>스레드 Join은 5초 제한이다. 넘으면 Critical 로그를 남기고 진행한다.</li><li>`GameServerService.StopAsync`는 호출 스레드를 막지 않고 Host 토큰을 따른다.</li><li>진행 중인 판은 끝나지 않았으므로 기록하지 않는다.</li></ul> | Client가 "서버 종료"를 보고 자동 재접속하지 않는다. 멈춘 Game Loop가 종료를 막지 않는다. | 진행 중인 판의 기록이 없다. 끝나지 않은 판의 순위는 의미가 없다. |
| D8 | **DB 재연결(Phase 9 D7에서 넘어옴).**<ul><li>Writer는 시작할 때 DB 연결에 실패해도 포기하지 않는다.</li><li>기록이 오면 스키마가 준비되지 않았으면 먼저 준비를 시도한다. 실패하면 그 기록의 보통 재시도·`Failed` 처리로 간다.</li><li>디스크 보관은 하지 않는다.</li></ul> | 개발자가 서버를 먼저 켜고 Docker를 나중에 띄워도 기록이 남는다. 경기는 몇 분에 한 번이라 기록마다 연결을 시도해도 부담이 없다. | DB가 오래 없으면 기록마다 연결 시간 초과(5초)만큼 Writer가 늦어진다. 큐 16개로 버틴다. |
| D9 | **관측.**<ul><li>**Health 줄:** Stats와 같은 간격으로 하나 더 남긴다. 내용:<ul><li>연결·Join·유예 수, 경기 상태</li><li>거절(이유별), Kick(코드별), 끊김(Timeout·기타), 재접속(Resumed)</li><li>잘못된 패킷(이유별)</li><li>Tick 실패, 경기 초기화, Stall</li><li>DB `saved/failed/discarded/dropped`</li></ul></li><li>**Meter:** `System.Diagnostics.Metrics`의 `Meter("ProjectH.Server")`에 같은 수치를 둔다. `dotnet-counters monitor -n ProjectH.Server --counters ProjectH.Server`로 본다.</li><li>**Stall Watchdog:** 별도 Timer가 1초마다 마지막 Tick 완료 시각을 본다. 2초 넘게 Tick이 없으면 Critical을 한 번 남기고 `stalls`를 센다. 풀리면 걸린 시간을 남긴다.</li><li>**로그:**<ul><li>콘솔 로그에 시각을 넣는다(`appsettings.json`).</li><li>접속·Join·이탈·재접속은 Information이다(연결마다 한 번).</li><li>Kick은 Warning이다.</li></ul></li></ul> | Stats 줄은 이미 길다. 성능(Stats)과 상태(Health)를 나누면 읽기 쉽다. Meter는 .NET 기본 기능이라 새 패키지도 열린 포트도 없다. Watchdog은 Deadlock이나 무한 루프를 로그 없이 놓치지 않게 한다. | HTTP Health Endpoint가 필요해지면(배포 도구) 같은 수치를 그대로 내보낸다. |
| D10 | **Client.**<ul><li>`NetClient`가 `DisconnectCode`를 읽어 사람이 읽을 이유(`LastError`)로 바꾼다.</li><li>자동 재접속은 다시 해도 되는 끊김에만 한다: LiteNetLib `Timeout`·네트워크 오류, `ServerError`.</li><li>같은 주소·id로 최대 3번 시도한다. 끊김을 안 때부터 1초, 3초, 7초 뒤(간격 1·2·4초)에 시작하고, 시도마다 짧은 연결 예산(약 1.5초)을 쓴다(§5).</li><li>`ServerShutdown`, `Kicked`, `JoinTimeout`, `InputTimeout`, 직접 끊기, 연결 거절에는 다시 접속하지 않는다.</li><li>`DevConnectPanel`이 "재접속 중 n/3"을 보여 준다. Connect를 누르면 자동 재접속을 멈추고 새로 접속한다.</li><li>`Resumed`는 Join과 같이 처리한다. 서버가 전체 상태를 다시 보낸다.</li></ul> | 재접속이 D2 유예 안(10초)에 끝나도록 시각을 정했다(마지막 시도가 7초에 시작해 약 8.5초에 끝난다). 서버가 의도적으로 끊은 경우에 다시 붙으면 같은 이유로 또 끊긴다. | 개발용 패널만 바뀐다. 게임 UI는 다음 단계다. |
| D11 | **봇.**<ul><li>`--reconnect` 옵션(기본 끔)을 둔다. 켜면 다시 해도 되는 끊김(D10과 같은 기준)에서 Client와 같은 시각(끊긴 때부터 1·3·7초 뒤)과 같은 연결 예산으로 같은 id로 다시 접속한다. 끊김마다 최대 3번이다.</li><li>봇 Stats 줄에 재접속 수를 넣는다.</li></ul> | D2를 부하 상황에서 시험할 수 있다. 기본 끔이라 부하 측정 결과는 그대로다. | 없음. |
| D12 | **지금 넣지 않는 것:**<ul><li>IP별 제한·차단, DDoS 방어, 연결 키·암호화</li><li>인증과 세션 토큰(D2의 위험)</li><li>실패한 DB 기록의 디스크 보관</li><li>HTTP Health·Prometheus Endpoint</li><li>프로세스 감시자(자동 재시작) 설정</li><li>Anti-cheat 통계·Hit 기록 로그(Phase 3)</li><li>게임 UI의 끊김 화면(개발 패널만)</li></ul> | 원래 요청서의 Phase 10 목록 밖이거나, 운영 배포·인증이 정해져야 의미가 있다. 지금은 개발용 LAN 서버다. | 공개 서버로 열기 전에 IP 제한과 인증이 필요하다. |

## 1. 구조

**Shared(`Shared/Runtime/Protocol`):**

- `DisconnectCode.cs`(새 파일)
  - D1 enum
  - `TryRead(NetPacketReader 없이 byte[]/span)` → 모르는 값은 `None`. LiteNetLib를 참조하지 않는다.
- `JoinResult.Resumed`
- `ProtocolVersion` 8
- `PacketReader.TryReadPacketId`의 상한은 그대로다(새 패킷 없음).

**Server(`Server/src/ProjectH.Server`):**

| 위치 | 변경 |
|---|---|
| `ServerOptions` | `ReconnectGraceSeconds` 10(0–60), `JoinTimeoutSeconds` 5(1–60), `InputTimeoutSeconds` 10(0 또는 2–300이고 `× 1000 ≥ DisconnectTimeoutMs + 2000`, §5), 검증 |
| `Net/PeerState` | 연결 Tick, 마지막 입력 Tick, 이유별 잘못된 패킷 |
| `Net/NetworkListener` | 이유별 잘못된 패킷과 거절 카운트, Kick 코드, 받기 핸들러 try/catch |
| `Net/ServerStats`(또는 새 `HealthCounters`) | D9 카운터. Network 스레드와 Game Loop가 함께 쓰므로 `Interlocked`를 쓴다. |
| `GameLoop` | Join·Input Timeout 검사, 반복 전체 보호, Tick 연속 실패 → 경기 초기화, 종료 순서(D7), Health 줄, 마지막 Tick 시각, 끊는 이유를 `Match`에 넘김 |
| `Game/Match` | `Disconnect(peerId, grace)`: 유예 상태로 둘지 `Leave`할지 정함. 유예 목록(최대 `MaxPlayers`), 시간이 지나면 `Leave`. `TryJoin`에서 같은 DevPlayerId의 유예 캐릭터를 찾아 다시 묶고 전체 상태를 보냄. 판 재시작 때 유예 목록을 비움. |
| `Diagnostics/ServerMeter`(새 파일) | D9 Meter(Observable 계측기가 카운터를 읽음) |
| `Diagnostics/StallWatchdog`(새 파일) | D9 Watchdog |
| `GameServerService` | 막지 않는 `StopAsync`, Watchdog 수명, 초기화 3번 → `StopApplication` |
| `Program` | 처리되지 않은 예외 로그, Meter 등록 |
| `Persistence/MatchHistoryWriter` | D8: 시작할 때 연결에 실패해도 계속 돌고, 기록마다 준비를 시도 |
| `appsettings.json` | 새 설정, 콘솔 시각 |

**Client(`Client/Assets/Scripts`):**

- `Net/NetClient`: 끊는 코드 해석, `LastDisconnectCode`, 재접속해도 되는지 판단
- `Game/GameClient`: 재접속 상태 기계(시도 수, 다음 시각), `Resumed` 처리
- `Bootstrap/DevConnectPanel`: 재접속 표시

**Bots:** `BotOptions`의 `--reconnect`, `BotConnection`과 `BotRunner`의 재접속, Stats 줄.

**Lock:** 새 Lock은 없다.

- 유예 목록과 Timeout 검사는 Game Loop 스레드만 쓴다.
- Network 스레드와 함께 쓰는 카운터는 `Interlocked`다.
- Watchdog은 `Volatile`로 읽기만 한다.

## 2. 테스트

- **Protocol:**
  - `DisconnectCode` 읽기에서 빈 데이터·모르는 값은 `None`이다.
  - 버전은 8이다.
- **Timeout(통합):**
  - Join하지 않는 연결은 `JoinTimeout`으로 끊긴다.
  - 입력을 멈춘 Join 플레이어는 `InputTimeout`으로 끊긴다.
  - 입력을 보내는 플레이어는 끊기지 않는다.
  - `InputTimeoutSeconds` 0이면 끊지 않는다.
- **재접속:**
  - 경기 중 끊긴 참가자는 유예 동안 남아 있다(다른 플레이어에게 Despawn 없음).
  - 같은 id로 Join하면 `Resumed`와 같은 Entity로 돌아온다(위치·체력·인벤토리 유지). 전체 상태를 받는다.
  - 유예가 지나면 탈락하고 Loot를 떨어뜨리며 이탈자로 기록된다.
  - 유예 중에 죽으면 다시 와도 관전자다.
  - 경기 밖·사망 상태의 끊김과 Kick은 바로 `Leave`한다.
  - 연결된 같은 id는 빼앗지 않는다.
  - 판이 바뀌면 유예 목록이 빈다.
  - 유예 0이면 지금과 같다.
- **잘못된 패킷:**
  - 이유별 카운트가 맞는다.
  - Kick은 `Kicked` 코드다.
  - 거절은 이유별로 센다.
- **Fuzz(시드 고정):**
  - 무작위 바이트 10만 개를 서버가 읽는 모든 파서(`ConnectRequestData`, `PacketReader.TryReadPacketId`, `PlayerInputPacket`)와 Client가 읽는 모든 서버 패킷 파서에 넣는다. 예외가 없다.
  - 무작위 값(NaN, ±Inf, 아주 큰 값, 음수 Seq·ViewTick)이 든 입력으로 `Match`를 수백 Tick 돌린다. 예외가 없고 모든 위치·체력은 유한하다.
  - 통합: 한 peer가 무작위 패킷을 보내면 그 peer만 `Kicked`로 끊기고, 다른 peer는 계속 Snapshot을 받는다.
- **예외 복구:**
  - 시험용 훅으로 Tick 예외를 넣는다.
  - 한 번은 넘어간다.
  - `SimHz*3`번 연속이면 모두 `ServerError`로 끊기고 새 경기가 된다.
  - 10분 안에 3번이면 종료를 요청한다(시계 주입).
  - Stats·대기의 예외에도 스레드가 산다.
- **종료:**
  - 연결된 Client가 `ServerShutdown`을 받는다.
  - 멈춘 Game Loop에서도 `Stop`이 제한 시간 안에 돌아온다.
- **관측:**
  - Health 줄에 D9 항목이 있다.
  - Meter 계측기를 `MeterListener`로 읽을 수 있다.
  - Watchdog은 Tick이 멈추면 한 번 알리고, 풀리면 회복을 알린다(시계 주입).
- **DB:**
  - DB 없이 시작한 Writer가 나중에 DB가 생기면 저장한다(`PROJECTH_TEST_MYSQL`).
  - DB 없이는 기록마다 실패로 센다.
- **Client:**
  - Unity 컴파일을 확인한다(scratch UnityCompile).
  - 재접속 판단 표(코드·이유 → 재접속 여부)를 순수 함수로 만들고 서버 테스트 프로젝트에서 소스 링크로 시험한다(Phase 6 ZoneMath와 같은 방식).
- **봇:** `--reconnect` 옵션 파싱, 재접속 판단이 Client와 같은 표를 쓴다.
- **회귀:** 기존 테스트가 모두 통과한다. 봇 50명 부하 측정에서 Tick p95가 Phase 8 기록보다 나빠지지 않는다.

## 3. 문서

- `Docs/Networking.md`: 끊는 코드, 재접속 유예, Timeout, 잘못된 패킷 처리
- `Docs/Server.md`: 새 설정, 종료 순서, 예외 복구, Health 줄, Meter·`dotnet-counters` 사용법, Watchdog
- `Docs/Client.md`: 재접속 동작
- `Docs/Bots.md`: `--reconnect`
- `Docs/Database.md`: DB 재연결
- `Docs/Architecture.md`: Phase 10 기준

## 4. 범위 밖

D12의 항목.

## 5. 계획 단계 변경

프로토타입(계획 `Docs/plans/2026-10-01-phase10-hardening.md`)에서 정한 것이다.

| 변경 | 이유 |
|---|---|
| 재접속 판단 표를 Shared `DisconnectCodes.ShouldReconnect(remoteClose, code, networkLoss)`에 둔다. 소스 링크로 시험하지 않는다(§2). | Client와 봇이 같은 표를 써야 한다. 봇이 Client 소스를 링크하면 CS0436이 난다(Phase 7). LiteNetLib에 의존하지 않는 Protocol 의미 규칙이다. |
| `DisconnectCode` 읽기는 `DisconnectCodes.Read(span)`이다. 언제나 코드를 돌려주며, 비었거나 모르는 값이면 `None`이다(§1의 `TryRead`). | 실패 경우가 없으므로 Try 형태가 필요 없다. |
| 코드 없는 원격 끊기(`None`)는 재접속하지 않는다(D10). | 이유를 모르는 서버 쪽 끊기에 다시 붙으면 같은 일이 반복될 수 있다. |
| Meter는 `GameServerService`가 만들고 해제한다(§1의 `Program` 등록 대신). | Game Loop와 같은 수명이다. `dotnet-counters`는 프로세스의 모든 Meter를 본다. |
| Game Loop 스레드를 Background 스레드로 바꾼다(D7). | 멈춘 Loop가 프로세스를 붙잡으면 Join 5초 제한이 의미가 없다. |
| 죽은 봇도 빈 입력을 보낸다(D4, D11). | Unity Client와 같게 한다. 그러지 않으면 배틀로얄에서 죽은 봇이 Input Timeout으로 끊긴다. |
| DB 없이 받은 기록은 `Discarded`가 아니라 기록마다 `Failed`다. 시작 때 연결 실패 로그는 Error가 아니라 Warning이다(D8). | 이제 기록마다 연결을 다시 시도하므로 저장 실패와 같다. 시작 때 DB가 없는 것은 정상 경우다. |
| Join·Input Timeout으로 끊는 로그는 Information이고 `Kicked`만 Warning이다(D9). | Timeout은 보통의 Client 상태다. 잘못된 패킷만 조사할 일이다. |
| 유예 중에 죽은 플레이어는 다음 Tick에 `Leave`한다. 다시 오면 보통의 늦은 합류(관전자)다. 죽은 캐릭터를 다시 묶지 않는다(D2). | 죽은 캐릭터를 묶어도 할 수 있는 것이 관전뿐이고, 늦은 합류와 같은 결과다. 유예 목록이 짧아진다. |
| (최종 리뷰) 재접속 시도는 끊김을 안 때부터 1·3·7초 뒤에 시작한다(`DisconnectCodes.ReconnectOffsetSeconds`). 자동 시도는 LiteNetLib `ReconnectDelay` 250 ms × `MaxConnectAttempts` 5로 약 1.5초 안에 포기한다. 다음 시각에 아직 연결 중인 시도는 버리고 다음 시도를 시작한다. 직접 Connect는 LiteNetLib 기본값을 쓴다. 봇도 같다(D10, D11). | 앞 실패부터 1·2·4초를 재면, 연결 실패에 LiteNetLib 기본값으로 약 5.5초가 걸려 둘째 시도가 8초쯤, 셋째가 17초쯤에 시작해 유예(10초)를 넘긴다. 두 값은 LiteNetLib 2.1.4에서 연결 중인 peer를 갱신할 때마다 읽는 public 필드라 접속마다 바꿀 수 있다. |
| (최종 리뷰) `InputTimeoutSeconds`는 0이거나 `InputTimeoutSeconds × 1000 ≥ DisconnectTimeoutMs + 2000`이어야 한다(D4). | 네트워크가 끊기면 입력도 끊긴다. Input Timeout이 먼저 오면 서버가 끊은 것(`InputTimeout`)이 되어 유예(D2)를 잃는다. |
| (최종 리뷰) 유예 중에 경기가 끝난 뒤(`Finished`) 돌아오면 Resume 순서 끝에 자기 `MatchResult`를 다시 보낸다(D2, D11). | 경기 끝에 보낸 결과는 연결이 없어(`NoPeer`) 사라졌다. 우승자가 잠깐 끊겼다가 돌아와도 결과를 본다. |
| (최종 리뷰) 종료가 시작되면(`Stop`, 또는 초기화가 거듭 실패한 치명 경로) 새 연결 요청을 `ServerFull`로 거절한다(D6, D7). 치명 경로는 초기화를 하지 않으므로 `matchResets`로 세지 않는다. | 멈추는 서버에 새로 받은 peer는 곧바로 끊긴다. 프로토콜을 바꾸지 않고, Client는 거절에 재접속하지 않는다. |
| (최종 리뷰) `Match`가 Join을 거절하면(`MatchFull`) 그 연결은 Join한 것으로 치지 않고, 1초 뒤 코드 없이(`None`) 끊는다. Client는 Join 실패를 받으면 자동 재접속을 멈춘다(D2, D3, D10). | 전에는 Join한 것으로 표시되어 Input Timeout까지 10초 동안 남았다. 응답(ReliableOrdered)이 끊기보다 먼저 나가도록 1초를 둔다. 코드 없는 원격 종료는 재접속 표에서 다시 하지 않는다. |
| (최종 리뷰) Health 줄의 Kick 항목 `badPackets`를 `kicked`로 바꾸고 `graceExpiries`(시간 초과·유예 중 사망·판 재시작)를 더한다. Meter에 `projecth.match_state`·`grace_starts`·`grace_expiries`를 더하고 `kicks{code=ServerShutdown}`은 내지 않는다(D9). Control 채널이 가득 차서 끊을 때는 `ServerError` 코드를 보낸다(D1). | 같은 이름이 두 묶음에 있으면 읽을 때 헷갈린다. 유예가 끝난 것이 로그·수치에 보이지 않았다. 종료는 Kick이 아니다. 코드 없는 끊기는 원인을 알 수 없다. |
