# ProjectH Monitoring Server

ProjectH 게임 서버를 실시간으로 관측하기 위한 별도의 Monitoring Server를 구현하라.

현재 목적은 거대한 운영 플랫폼을 만드는 것이 아니다.

우선 다음 정보를 웹에서 빠르게 확인할 수 있는 가벼운 내부 개발/운영 도구를 만든다.

```text
Server Online / Offline

현재 접속자 수

현재 Match 수

Server Tick

CPU

Memory

Network

Error
```

향후 메트릭과 기능을 쉽게 추가할 수 있도록 구조는 확장 가능하게 만든다.

하지만 미래 요구사항을 예상해 지나치게 복잡한 시스템을 만들지 않는다.

---

# 1. 핵심 목표

Monitoring Server는 Game Server와 별도 프로세스로 실행한다.

구조:

```text
Game Server
    │
    │ Metrics / Heartbeat
    ▼
Monitoring Server
    │
    ▼
Web Dashboard
```

Game Server가 종료되어도 Monitoring Server는 살아 있어야 한다.

Monitoring Server가 종료되어도 Game Server는 정상적으로 계속 동작해야 한다.

Monitoring 기능은 Game Server의 핵심 Gameplay 기능이 아니다.

따라서 Monitoring 장애가 게임 서버 장애로 전파되면 안 된다.

---

# 2. 기술 스택

Monitoring Server:

```text
.NET 10
ASP.NET Core
C#
```

Web UI는 가능한 한 단순한 구조를 선택한다.

권장:

```text
ASP.NET Core
+
Static Web UI
+
JavaScript
+
CSS
```

현재 단계에서는 React, Vue, Angular 같은 별도 대형 Frontend Framework를 반드시 도입하지 않는다.

프로젝트에 이미 적합한 Frontend 환경이 존재하면 재사용할 수 있다.

---

# 3. 프로젝트 위치

예:

```text
Server/src/ProjectH.Monitoring
```

공유 DTO가 필요하면:

```text
Server/src/ProjectH.Monitoring.Contracts
```

또는 기존 Shared 구조에 맞는 최소 DTO 위치를 사용한다.

불필요한 프로젝트를 많이 만들지 않는다.

---

# 4. 절대적인 분리

Monitoring Server가 다음 프로젝트 내부 구현을 직접 참조하지 않게 한다.

```text
ProjectH.Server GameLoop

Player Entity

Match 내부 클래스

Inventory

Combat

Building World
```

Monitoring Server는 Game Server 내부 객체를 직접 읽지 않는다.

둘 사이에는 명확한 Metrics DTO만 존재한다.

---

# 5. 통신 방향

초기 구조는 Game Server가 Monitoring Server로 메트릭을 Push하는 방식으로 구현한다.

```text
Game Server

↓

HTTP

↓

Monitoring Server
```

추천 주기:

```text
1~5초
```

정확한 값은 Configuration으로 관리한다.

기본값은 적절한 값을 하나 정하되 코드에 흩어진 Magic Number를 만들지 않는다.

---

# 6. Push 방식을 사용하는 이유

Game Server가 자신의 상태를 알고 있으므로 필요한 값만 가볍게 만들어 전달한다.

Monitoring Server는 여러 Game Server Instance를 향후 자연스럽게 받을 수 있어야 한다.

예:

```text
GameServer-01
GameServer-02
GameServer-03
```

하지만 지금 당장 Cluster 관리 기능을 만들 필요는 없다.

---

# 7. Monitoring 장애 격리

가장 중요한 규칙이다.

다음 상황에서도 Game Server는 영향을 받지 않아야 한다.

```text
Monitoring Server Down

HTTP Timeout

Network Error

Monitoring Server Slow

잘못된 Monitoring 응답
```

Monitoring 전송 실패는:

```text
Gameplay Failure
```

로 취급하지 않는다.

---

# 8. Game Loop에서 직접 HTTP 호출 금지

절대 다음 구조로 만들지 않는다.

```text
Game Loop
↓
await HTTP
↓
Monitoring Server
```

Monitoring 전송은 Game Loop 밖에서 처리한다.

---

# 9. Metric Snapshot

Game Loop 또는 Server 상태에서 필요한 값은 가벼운 Snapshot으로 추출한다.

개념:

```text
Game Server State
↓
Monitoring Snapshot
↓
Bounded / Latest-only Buffer
↓
Background Sender
↓
Monitoring Server
```

---

# 10. Latest-only 우선

Monitoring 정보는 과거 모든 샘플을 반드시 전송해야 하는 데이터가 아니다.

Monitoring Server가 느려졌다고:

```text
Metric Snapshot
Metric Snapshot
Metric Snapshot
Metric Snapshot
...
```

이 무한히 Queue에 쌓이면 안 된다.

가능하면:

```text
최신 Snapshot 우선
```

방식을 사용한다.

예:

```text
capacity = 1
```

또는 아주 작은 bounded buffer.

오래된 Monitoring Snapshot은 버려도 된다.

---

# 11. Game Server Monitoring Collector

Game Server에 최소한의 Monitoring Collector를 추가한다.

역할:

```text
서버 상태 읽기

Metrics Snapshot 생성

Background Sender에 전달
```

Collector 자체는 Game Loop에 부담을 거의 주지 않아야 한다.

---

# 12. 초기 Metric

초기에는 다음 정도만 수집한다.

## Server

```text
ServerId

ServerVersion

StartTime

Uptime

Online
```

## Gameplay

```text
ConnectedPlayers

ActivePlayers

ActiveMatches
```

현재 구조상 한 Match만 있다면 실제 존재하는 값만 사용한다.

없는 개념을 만들지 않는다.

---

# 13. Tick

초기 중요 지표:

```text
Tick P50

Tick P95

Tick P99

Tick Max
```

이미 Health / Metrics에 계산되는 값이 있다면 반드시 재사용한다.

Monitoring용으로 같은 통계를 다시 계산하지 않는다.

---

# 14. CPU

표시:

```text
CPU %
```

가능하면 현재 Process 기준 CPU 사용률을 사용한다.

시스템 전체 CPU와 Process CPU를 혼동하지 않는다.

UI에 어떤 기준인지 명확하게 표시한다.

---

# 15. Memory

초기:

```text
Managed Memory

Process Working Set
```

를 표시한다.

둘을 구분한다.

예:

```text
Managed
420 MB

Working Set
610 MB
```

---

# 16. GC

초기에는 복잡하게 만들지 않는다.

가능하면:

```text
Gen0 Count

Gen1 Count

Gen2 Count
```

정도를 보여준다.

향후 Allocation Rate 등의 Metric을 추가할 수 있게 한다.

---

# 17. Network

초기:

```text
Packets Received / sec

Packets Sent / sec

Bytes Received / sec

Bytes Sent / sec
```

가능한 값만 사용한다.

누적 Counter만 존재한다면 Monitoring Snapshot 생성 단계에서 Rate 계산 구조를 명확히 한다.

---

# 18. Error

초기 Error Metric:

```text
Exceptions

Invalid Packets

Disconnects
```

현재 Server에서 이미 제공되는 Counter가 있다면 재사용한다.

---

# 19. Queue

기존 서버에 중요한 bounded queue가 이미 노출되어 있다면 초기 Dashboard에 최대 1~2개만 표시한다.

우선순위 예:

```text
DB Persistence Queue
```

모든 내부 Queue를 첫 버전부터 노출하지 않는다.

---

# 20. Monitoring Snapshot DTO

예시 개념:

```csharp
public sealed record ServerMonitoringSnapshot
{
    ...
}
```

필드는 실제 코드 구조를 분석한 후 결정한다.

최소한:

```text
Timestamp

ServerId

Version

UptimeSeconds

ConnectedPlayers

ActivePlayers

ActiveMatches

TickP50Ms

TickP95Ms

TickP99Ms

TickMaxMs

CpuPercent

ManagedMemoryBytes

WorkingSetBytes

GcGen0

GcGen1

GcGen2

PacketsReceivedPerSecond

PacketsSentPerSecond

BytesReceivedPerSecond

BytesSentPerSecond

ExceptionCount

InvalidPacketCount
```

정도를 고려한다.

실제 존재하지 않는 값을 억지로 만든다거나 추정하지 않는다.

---

# 21. Timestamp

Game Server가 Snapshot을 생성한 시간을 포함한다.

Monitoring Server 수신 시간을 별도로 기록한다.

두 값을 혼동하지 않는다.

```text
ObservedAt
ReceivedAt
```

---

# 22. ServerId

향후 여러 Game Server를 Monitoring할 수 있도록 ServerId 개념을 둔다.

예:

```text
dev-server-01
```

Configuration으로 지정한다.

자동으로 복잡한 Service Discovery를 만들지 않는다.

---

# 23. Heartbeat

Metric Snapshot 자체를 Heartbeat로 사용해도 된다.

Monitoring Server는:

```text
마지막 Snapshot 수신 시간
```

으로 Online / Offline을 판단한다.

---

# 24. Offline 판단

Config:

```text
OfflineThreshold
```

을 둔다.

예:

```text
마지막 Metric 수신 후 N초 이상
→ Offline
```

정확한 값은 Configuration에서 관리한다.

---

# 25. Monitoring Server API

초기 API:

```text
POST /api/ingest/metrics
```

Game Server가 Snapshot을 보낸다.

Web UI:

```text
GET /api/servers
GET /api/servers/{serverId}
GET /api/servers/{serverId}/metrics
```

정도로 시작한다.

필요에 따라 실제 REST 구조를 정리한다.

---

# 26. Ingest Validation

Monitoring Server는 Game Server가 보내는 데이터도 검증한다.

예:

```text
ServerId 없음

잘못된 Timestamp

음수 Player Count

NaN

Infinity

너무 큰 Payload
```

등을 안전하게 처리한다.

잘못된 요청 때문에 Monitoring Server가 Crash하면 안 된다.

---

# 27. Payload Size

Monitoring Snapshot은 작아야 한다.

로그 전체나 Player 목록을 Metric Push에 포함하지 않는다.

금지:

```text
전체 Player Entity

전체 Match State

전체 Building State

전체 Log
```

---

# 28. 초기 저장

첫 버전에서는 Database를 사용하지 않는다.

Monitoring Server Memory에 최근 샘플만 저장한다.

---

# 29. Ring Buffer

Server별로 최근 Metric Sample을 bounded Ring Buffer에 저장한다.

예:

```text
최근 N분
```

정확한 Sample Count는:

```text
Sampling Interval
×
History Duration
```

을 기준으로 Config한다.

무제한 List를 사용하지 않는다.

---

# 30. Restart

Monitoring Server가 Restart되면 과거 Memory History가 사라져도 현재 단계에서는 정상이다.

장기 History 저장은 이후 확장 기능이다.

---

# 31. Storage abstraction

향후 장기 저장을 붙일 수 있도록 경계는 둘 수 있다.

예:

```text
MetricStore
```

정도의 간단한 추상화.

하지만:

```text
Repository + UnitOfWork + 여러 Layer
```

같은 과도한 구조를 만들지 않는다.

---

# 32. Web Dashboard

첫 화면에는 복잡한 그래프를 많이 넣지 않는다.

상단:

```text
ProjectH Monitoring

Server:
ONLINE / OFFLINE

Uptime:
...

Players:
...

Match:
...
```

---

# 33. 기본 Dashboard Layout

예:

```text
┌──────────────────────────────────────────────────────┐
│ ProjectH Monitoring          Server-01    ONLINE     │
├────────────┬────────────┬────────────┬───────────────┤
│ Players    │ Tick P95   │ CPU        │ Memory        │
│ 82         │ 0.23 ms    │ 32 %       │ 480 MB        │
├────────────┴────────────┴────────────┴───────────────┤
│ Tick                                                   │
│ [ Graph ]                                              │
├───────────────────────────┬──────────────────────────┤
│ Network                   │ Server                   │
│ Send                       │ Exceptions               │
│ Receive                    │ Invalid Packets          │
└───────────────────────────┴──────────────────────────┘
```

---

# 34. Dashboard 핵심 Card

초기에는 다음 Card만 만든다.

```text
Status

Players

Tick P95

CPU

Memory
```

추가적으로:

```text
Network

Errors
```

정도.

카드 수를 과도하게 늘리지 않는다.

---

# 35. Tick Graph

첫 번째 핵심 Graph:

```text
Tick P50
Tick P95
Tick P99
```

최근 몇 분을 보여준다.

Tick Max는 필요하면 별도 숫자로 표시한다.

---

# 36. Resource Graph

두 번째:

```text
CPU

Memory
```

CPU와 Memory 단위가 다르므로 동일 Y축에 억지로 겹치지 않는다.

UI 구현 방식에 따라 별도 그래프를 사용한다.

---

# 37. Network Graph

필요하면:

```text
Send KB/s

Receive KB/s
```

정도를 보여준다.

초기 버전에서 Packet과 Byte 그래프를 모두 네 개 만들 필요는 없다.

---

# 38. 그래프 Library

가벼운 Client-side Chart Library 하나를 사용할 수 있다.

새로운 무거운 UI Framework 때문에 프로젝트가 커지지 않게 한다.

Library를 사용한다면 이유와 Dependency를 문서화한다.

---

# 39. 실시간 갱신

초기에는:

```text
HTTP Polling
```

을 사용해도 된다.

예:

```text
1~2초
```

주기.

처음부터 WebSocket / SignalR가 꼭 필요하지 않다.

---

# 40. 향후 Realtime

필요해지면:

```text
SignalR
```

등으로 바꿀 수 있는 구조면 충분하다.

첫 버전부터 반드시 구현하지 않는다.

---

# 41. Server 목록

Server가 하나뿐이어도 UI 구조상 Server 목록을 지원한다.

예:

```text
Server-01       ONLINE
Server-02       OFFLINE
```

향후 여러 서버를 볼 수 있게 한다.

---

# 42. Server Detail

Server를 클릭하면 해당 Server 상세 Dashboard로 이동한다.

초기에는 Single Page에서도 괜찮다.

UI 복잡도가 더 낮은 방식을 선택한다.

---

# 43. Offline UI

Server가 Offline이면 명확하게 표시한다.

```text
OFFLINE

Last Seen:
2026-...
```

마지막 Metric은 보여줄 수 있다.

현재 값처럼 오해하지 않게 한다.

---

# 44. Warning

초기에는 복잡한 Alert Engine을 만들지 않는다.

단순 UI Warning 정도만 허용한다.

예:

```text
Tick P95가 Config Threshold 이상

Memory가 Config Threshold 이상

Server Offline
```

---

# 45. Threshold

Threshold는 Monitoring Server Config에서 관리한다.

예:

```text
TickP95WarningMs

MemoryWarningBytes
```

근거 없는 수치를 코드에 박지 않는다.

---

# 46. Alert 기능

다음은 초기 범위에서 제외한다.

```text
Discord 알림

Slack

Email

SMS

PagerDuty
```

확장 포인트만 있으면 충분하다.

---

# 47. Authentication

초기 Monitoring Server가 localhost / 내부 개발 환경에서만 사용된다면 복잡한 Login을 먼저 만들지 않는다.

그러나 외부 네트워크 공개를 기본값으로 하지 않는다.

기본 Bind:

```text
127.0.0.1
```

또는 명시된 내부 Bind.

---

# 48. 외부 공개

향후 운영에서 외부 접속이 필요하면:

```text
Authentication

TLS

Reverse Proxy
```

를 별도 Phase로 추가한다.

현재 Phase에서 임시 비밀번호 시스템 등을 대충 만들지 않는다.

---

# 49. Game Server Monitoring Sender

Monitoring Sender는 BackgroundService 또는 현재 서버 Background Worker 구조에 맞는 가벼운 방식으로 구현한다.

GameLoop와 Lifetime을 분리한다.

---

# 50. Sender Failure

전송 실패 시:

```text
Error Log
```

를 매번 남발하지 않는다.

Monitoring Server가 10분간 죽어있다고 초당 로그가 발생하면 안 된다.

Rate Limit 또는 상태 변화 기반 로그를 사용한다.

예:

```text
Monitoring connection lost

Monitoring connection restored
```

---

# 51. Retry

재시도는 간단하게 한다.

```text
다음 Sampling Interval에 다시 시도
```

정도로 충분하다.

복잡한 Retry Framework는 필요 없다.

---

# 52. HTTP Timeout

Monitoring 요청에는 짧은 Timeout을 둔다.

Monitoring 요청이 오래 대기하지 않는다.

---

# 53. HttpClient

매 Snapshot마다 새로운 HttpClient를 생성하지 않는다.

현재 .NET 구조에 맞는 HttpClient Lifetime을 사용한다.

---

# 54. Shutdown

Game Server 종료 시 Monitoring Sender도 정상 종료한다.

하지만 마지막 Metric을 반드시 성공적으로 전송해야 종료할 수 있는 구조로 만들지 않는다.

Monitoring은 Best Effort다.

---

# 55. Monitoring Server Shutdown

정상 종료할 수 있어야 한다.

```text
HTTP Stop

Background Work Stop

Dispose
```

를 수행한다.

---

# 56. Logging

Monitoring Server에는 기본적인 Structured Logging을 사용한다.

초기 중요 로그:

```text
Server First Seen

Server Offline

Server Online Again

Invalid Ingest

Monitoring Server Start / Stop
```

Metric 하나 받을 때마다 Information 로그를 남기지 않는다.

---

# 57. Metrics API 자체 모니터링

첫 버전에서는 Monitoring Server를 위한 Monitoring Server를 만들지 않는다.

필요한 기본 Health 정도만 추가한다.

예:

```text
GET /health
```

---

# 58. API Health

Health 응답은 단순하게:

```text
OK
```

또는 기본 상태 정보 정도.

---

# 59. Config

예:

```text
Monitoring:
  Enabled
  Endpoint
  ServerId
  IntervalSeconds
  TimeoutSeconds
```

Monitoring Server:

```text
HistoryMinutes
OfflineThresholdSeconds
```

실제 프로젝트 Configuration 방식에 맞춘다.

---

# 60. Monitoring Disabled

Game Server 설정:

```text
Monitoring:Enabled=false
```

이면 Monitoring Sender 자체를 실행하지 않거나 최소 비용만 사용한다.

---

# 61. Production 영향

Monitoring 기능 때문에 Game Server가:

```text
ASP.NET Server를 직접 실행
```

할 필요는 없다.

Game Server는 Monitoring Server로 outbound 요청만 보내는 구조를 우선한다.

---

# 62. Security

Ingest Endpoint를 무방비하게 인터넷에 공개하지 않는다.

초기 내부 환경에서는 간단한 Shared Token을 옵션으로 둘 수 있다.

예:

```text
X-Monitoring-Token
```

단, 실제 Secret은 Repository에 Commit하지 않는다.

환경변수 또는 Config Secret으로 받는다.

---

# 63. Token

Token이 설정된 Monitoring Server는 올바른 Token이 없는 Ingest를 거부한다.

Web UI 인증과 Ingest 인증은 서로 다른 문제다.

처음부터 복잡하게 합치지 않는다.

---

# 64. 테스트

Monitoring Server Unit Test를 작성한다.

최소:

```text
Valid Metric Ingest

Invalid Metric Rejection

Server Registration

Latest Metric

Ring Buffer Limit

Offline Detection

Multiple Server Separation
```

---

# 65. Game Server Sender Test

가능한 범위에서:

```text
Snapshot Generation

Disabled Monitoring

Latest-only Buffer

Send Failure

Cancellation

Shutdown
```

을 테스트한다.

---

# 66. Integration Test

간단한 End-to-End 테스트를 만든다.

```text
Monitoring Server Start

↓

Fake Game Server Metric POST

↓

API 조회

↓

값 확인
```

---

# 67. 실제 Game Server Integration

가능하면 개발 서버를 실제 실행해 Monitoring Server가 데이터를 받는 것도 한 번 검증한다.

자동화 환경상 불가능하면 수동 확인 항목으로 남긴다.

---

# 68. UI Test

최소:

```text
Server 목록 표시

ONLINE 표시

Players 표시

Tick 표시

CPU 표시

Memory 표시

Server Offline 표시
```

를 확인한다.

복잡한 Browser Automation은 첫 Phase 필수가 아니다.

---

# 69. 개발용 Fake Metric

Monitoring UI 개발을 위해 Monitoring Server에 Fake Data Generator를 Production 기본 기능으로 넣지 않는다.

필요하면:

```text
Development 전용
```

으로 명확히 격리한다.

---

# 70. 코드 주석

새로 추가하거나 수정한 함수에는 기존 프로젝트 규칙대로:

```text
기능
입력
출력
```

주석을 작성한다.

예:

```csharp
// 기능: 현재 서버 상태를 Monitoring Snapshot으로 생성한다.
// 입력: 현재 Health 및 Network Metric.
// 출력: Monitoring Server로 전송할 ServerMonitoringSnapshot.
private ServerMonitoringSnapshot CreateSnapshot(...)
{
}
```

---

# 71. 프로젝트 구조 예시

실제 Repository 구조를 먼저 확인한 후 조정한다.

개념:

```text
Server/
└─ src/
   ├─ ProjectH.Server/
   │  └─ Monitoring/
   │     ├─ MonitoringCollector.cs
   │     ├─ MonitoringSender.cs
   │     └─ MonitoringOptions.cs
   │
   └─ ProjectH.Monitoring/
      ├─ Program.cs
      ├─ Ingest/
      ├─ Storage/
      ├─ Api/
      └─ wwwroot/
```

구조를 기계적으로 강제하지 않는다.

---

# 72. 초기 Dashboard에서 보여줄 것

정확히 우선 구현할 화면:

```text
Server Status

Uptime

Players

Active Match

Tick P50 / P95 / P99 / Max

CPU

Managed Memory

Working Set

Network Send / Receive

Exception Count
```

그 이상은 첫 버전 필수가 아니다.

---

# 73. 이번 Phase에서 하지 말 것

다음을 구현하지 않는다.

```text
Prometheus

Grafana

InfluxDB

ElasticSearch

Kafka

Redis

Microservice Discovery

Kubernetes Dashboard

Distributed Tracing

Log Search System

Discord Alert

Email Alert

Authentication Service

Role Management

Complex Alert Rule Engine

Long-term Metric Database
```

현재 필요한 규모에 비해 과도하다.

---

# 74. 향후 확장 가능성

구조적으로 다음을 나중에 추가할 수 있으면 충분하다.

```text
Long-term Metrics

Alerts

Logs

Match 상세 Monitoring

Player 상세 Monitoring

Multiple Server Instances

Historical Comparison

Prometheus Export

Discord Alert

Deployment Status
```

지금 구현하지 않는다.

---

# 75. 추가 Metric 원칙

새 Metric 추가 시:

```text
Game Server Snapshot DTO

↓

Monitoring Ingest

↓

Storage

↓

API

↓

UI
```

흐름으로 간단하게 확장할 수 있어야 한다.

새 Metric 하나 때문에 여러 곳을 복잡하게 수정하는 구조는 피한다.

---

# 76. Metric Category

향후 확장을 고려해 개념적으로 다음 Category를 사용할 수 있다.

```text
Server

Gameplay

Tick

Memory

GC

Network

Database

Errors
```

하지만 모든 Category 화면을 지금 만들지는 않는다.

---

# 77. Monitoring Server 성능

Monitoring Server 자체는 초당 수천 Request가 필요한 시스템이 아니다.

현재 규모에서는 단순한 구현을 우선한다.

하지만 다음은 피한다.

```text
무제한 History

Metric마다 Task 생성 남발

Metric마다 파일 쓰기

Metric마다 DB 저장

매 요청 전체 History 복사
```

---

# 78. Thread Safety

여러 Game Server가 동시에 Metric을 보낼 수 있음을 고려한다.

Server별 Metric Store 접근은 Thread Safe해야 한다.

하지만 단순히 모든 곳에 ConcurrentDictionary를 사용하는 식으로 구현하지 않는다.

실제 접근 방식을 분석해서 가장 단순한 안전한 구조를 선택한다.

---

# 79. History API

그래프를 위해 최근 History를 조회할 수 있게 한다.

예:

```text
GET /api/servers/{serverId}/metrics?minutes=5
```

실제 API 형태는 현재 구조에 맞게 정한다.

요청자가 전체 무제한 History를 받을 수 없게 한다.

---

# 80. Downsampling

첫 버전에서는 복잡한 Downsampling이 필요 없다.

History 크기를 제한하면 충분하다.

향후 장기 저장이 생길 때 검토한다.

---

# 81. 단위

UI에서 단위를 명확히 표시한다.

예:

```text
Tick
0.23 ms

CPU
32 %

Memory
482 MB

Network
320 KB/s
```

Raw Byte 숫자를 그대로 보여주지 않는다.

---

# 82. 상태 색상

UI에서는 최소한:

```text
ONLINE

WARNING

OFFLINE
```

상태를 구분하기 쉽게 한다.

화려한 디자인보다 읽기 쉬운 Dashboard를 우선한다.

---

# 83. 반응형

Desktop 모니터링이 우선이다.

기본적인 Browser 창 크기 대응만 한다.

Mobile 전용 UI를 만들지 않는다.

---

# 84. Dark Mode

운영 Dashboard 특성상 Dark UI를 기본으로 사용할 수 있다.

하지만 UI 디자인에 많은 시간을 쓰지 않는다.

정보 전달이 최우선이다.

---

# 85. README

다음 문서를 추가한다.

```text
Docs/Monitoring.md
```

또는 Repository 문서 구조에 맞는 위치.

내용:

```text
Architecture

How to Start

Game Server Configuration

Monitoring Server Configuration

Metrics

API

Web UI

Security

Adding a New Metric

Known Limitations
```

---

# 86. 실행 방법

최종적으로 가능한 형태:

```text
dotnet run --project Server/src/ProjectH.Monitoring
```

Game Server:

```text
Monitoring__Enabled=true
Monitoring__Endpoint=http://127.0.0.1:<port>
Monitoring__ServerId=dev-server-01
```

실제 Config naming convention에 맞춰 작성한다.

---

# 87. 개발 흐름

다음 순서로 구현한다.

```text
기존 Metrics 구조 분석

↓

Monitoring Contracts

↓

Game Server Snapshot Collector

↓

Background Sender

↓

Monitoring Server Ingest

↓

In-Memory Bounded History

↓

Server Online / Offline

↓

REST API

↓

Basic Web Dashboard

↓

Charts

↓

Tests

↓

Documentation
```

---

# 88. 구현 우선순위

## Monitoring Phase 1 — Backend MVP

```text
Monitoring Server 생성

Metric Ingest

Server 목록

Latest Metric

History

Online / Offline
```

---

## Monitoring Phase 2 — Game Server Integration

```text
Monitoring Collector

Background Sender

Failure Isolation

Latest-only Queue
```

---

## Monitoring Phase 3 — Web UI

```text
Status

Players

Tick

CPU

Memory

Network

Errors
```

---

## Monitoring Phase 4 — Polish

```text
Basic Warning

Server Selection

Responsive Layout

README
```

---

# 89. 완료 조건

다음을 전부 만족해야 첫 버전 완료로 본다.

```text
[ ] Monitoring Server가 별도 .NET 10 Process로 실행된다.

[ ] Game Server와 Monitoring Server가 직접 코드 의존하지 않는다.

[ ] Game Server가 Metric Snapshot을 전송한다.

[ ] Monitoring Server 장애가 Game Server에 영향을 주지 않는다.

[ ] Game Loop에서 HTTP 요청을 기다리지 않는다.

[ ] Monitoring Queue가 무한 증가하지 않는다.

[ ] 여러 ServerId를 받을 수 있다.

[ ] Server Online / Offline 판단이 가능하다.

[ ] 최근 Metric History가 bounded 구조다.

[ ] Web에서 Server 상태를 볼 수 있다.

[ ] Players를 볼 수 있다.

[ ] Tick P50/P95/P99/Max를 볼 수 있다.

[ ] CPU를 볼 수 있다.

[ ] Memory를 볼 수 있다.

[ ] Network Send / Receive를 볼 수 있다.

[ ] Exception 수를 볼 수 있다.

[ ] 최근 Tick Graph가 보인다.

[ ] CPU / Memory 추이를 볼 수 있다.

[ ] Monitoring Disabled 상태에서 Game Server가 정상 동작한다.

[ ] Monitoring Server가 꺼진 상태에서도 Game Server가 정상 동작한다.

[ ] Unit Test가 통과한다.

[ ] Integration Test가 통과한다.

[ ] Docs가 작성된다.
```

---

# 90. 성능 검증

Monitoring을 끈 상태와 켠 상태에서 Server 성능을 비교한다.

가능하면 기존 QA Stress Tool을 사용한다.

예:

```text
stress_baseline
50 players
```

비교:

```text
Monitoring OFF

Tick P50
Tick P95
Tick P99
CPU
Memory
```

그리고:

```text
Monitoring ON

Tick P50
Tick P95
Tick P99
CPU
Memory
```

Monitoring 기능 때문에 의미 있는 Tick Regression이 발생하지 않는지 확인한다.

실제 측정값만 보고한다.

---

# 91. 장애 검증

다음 상황을 반드시 테스트한다.

```text
Monitoring Server 정상

Monitoring Server 종료

Monitoring Server 재시작

잘못된 Endpoint

Connection Refused

Timeout

Monitoring Server 느린 응답
```

어떤 경우에도 Game Server Gameplay가 중단되면 안 된다.

---

# 92. 최종 보고

구현 완료 후 다음 형식으로 보고한다.

## Architecture

실제 구현 구조.

## Projects

추가/변경한 프로젝트.

## Game Server

Collector / Sender 구조.

## Monitoring Server

Ingest / Storage / API 구조.

## Web UI

표시되는 Metric.

## Network

```text
Metric Interval:
Payload Size:
Timeout:
```

실제 측정값.

## Performance

```text
50 Players

Monitoring OFF
Tick P95:
CPU:
Memory:

Monitoring ON
Tick P95:
CPU:
Memory:
```

실제 측정값만 작성한다.

## Tests

실행한 테스트와 결과.

## Failure Test

Monitoring Server Down 상태 테스트 결과.

## Run

실제 실행 명령.

## Configuration

실제 설정 방법.

## Known Limitations

실제 남은 제한.

## Future

아직 구현하지 않은 확장 포인트만 간단히 기록한다.
