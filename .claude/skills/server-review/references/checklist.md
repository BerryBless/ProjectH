# 서버 리뷰 체크리스트 (영역별)

사용자가 준 Server Code Review 지침 93절을 리뷰 영역 6개로 묶은 것이다. 괄호 안 숫자는 원 지침의 절 번호다. review 모드의 리뷰어는 자기 영역 절만 읽는다. map 모드는 0절을 따른다.

## 목차

- 0. 구조 파악 (map)
- 1. concurrency — Thread·Ownership·Lock·Deadlock·Async
- 2. gameloop — Tick·Complexity·Allocation·Snapshot·Time
- 3. lifetime — Leak·Collection·Queue·Session·Match Reset
- 4. network — Packet·Validation·Authority·Replication·Reconnect·Security
- 5. database — DB 경로·Connection·Query·Transaction·DB Queue
- 6. resilience — Exception·Logging·Metrics·Watchdog·Shutdown·Tests

리뷰 우선순위(1): Correctness → Thread Safety → Deadlock → Memory/Lifetime → Tick 안정성 → Network → Queue/Backpressure → Database → Error Handling → Security/Validation → Performance → Maintainability. 치명적인 구조 문제를 Micro Optimization보다 먼저 찾는다.

---

## 0. 구조 파악 (2, 3, 4, 87)

- 특정 파일만 보고 결론 내리지 않는다. Repository 구조 → Entry Point → Thread Model → Game Loop → Network Receive → Session Lifecycle → Game Logic → Network Send → DB → Shutdown → Tests → Metrics 순서로 읽는다.
- 흐름: Network Receive → Packet Parse → Session/Connection → Game Loop/Worker Queue → Game Logic → Snapshot/Event → Network Send. DB: Game Logic → Persistence Queue → DB Worker → MySQL. 실제가 다르면 코드 기준으로 다시 정리한다.
- Owner 표: Session, Player, Match, Inventory, World, BuildPiece, Zone, Snapshot State, DB Queue 각각 — 누가 생성, 어느 Thread가 수정, 어느 Thread가 읽음, 누가 제거, 언제 Dispose.
- Thread Model: Network Thread, Game Loop Thread, DB Writer, Background Worker, Timer Callback 중 **실제로 있는 것만** 기록한다.

## 1. concurrency

**Ownership (3):** Owner가 불명확하면 문제로 기록한다.

**Shared Mutable State (5):** 둘 이상 Thread가 닿는 Dictionary, List, Queue, Session, Player, Match, Cache, Metrics, Network Peer, DB State를 모두 찾고 방식(Single Writer / Lock / Concurrent Collection / Immutable Snapshot / Message Passing / Unsafe Access)을 판정한다. Unsafe Access는 우선순위를 높인다.

**Lock (6):** lock, Monitor, SemaphoreSlim, Mutex, ReaderWriterLockSlim, SpinLock, Interlocked 기반 복합 상태를 모두 찾는다. 각각: 무엇을 보호하나, 획득 순서, 잡는 시간, Nested, 다른 Lock과 동시, await와 함께, 외부 Callback 호출, Lock 안 Network/DB.

**Deadlock (7):** Lock이 하나라도 있으면 반드시 본다. A→B / B→A, Lock → Callback → 같은·다른 Lock 재진입, Thread A가 Lock을 잡고 Task.Wait인데 Thread B가 그 Lock을 기다려 Task가 완료 못 하는 경우.

**Lock Ordering (8):** 둘 이상이면 현재 획득 순서를 정리하고 역순 경로가 하나라도 있으면 문제다.

**Contention (9):** Game Loop가 기다리는 Lock, 모든 Session 공유 Lock, Global Dictionary Lock, Network↔Game Loop 공유 Lock, DB Worker↔Game Loop 공유 Lock.

**Lock 내부 작업 (10):** DB Query, Network Send, File I/O, await, Task.Wait/Result, Thread.Sleep, 복잡한 Loop, Serialization, Logging, 외부 함수 호출 → Critical Section 축소 검토.

**Async (11):** async void, .Result, .Wait(), GetAwaiter().GetResult(), 불필요한 Task.Run, Task 누적, Cancellation 없는 장기 Task, 관찰되지 않는 Exception.

**Fire-and-forget (12):** `_ = SomeAsync();`를 안전하다고 가정하지 않는다. Exception은 누가 처리하나, 종료 시 끝나나, 무한 누적되나.

**ThreadPool Starvation (13):** Blocking I/O, 긴 CPU 작업, Task.Run 남용, Wait/Result, 긴 Callback, 동기 DB 접근.

**Timer (39):** Dispose, 중복 시작, Callback 재진입, 긴 Callback, Timer가 객체를 붙잡음. Game Loop Tick으로 될 일을 Timer로 만들지 않았나.

**CancellationToken (40):** CTS Dispose, LinkedTokenSource 누수, Cancel 후 Task 종료, Shutdown 전달.

**Static / Event (65, 66):** static Collection·Event 누적. 짧은 Lifetime(Session/Player)이 긴 Lifetime에 Subscribe했다면 Unsubscribe 경로.

## 2. gameloop

**Game Loop (14):** Tick Frequency, Duration, Drift, Overrun, Sleep/Delay 방식, 시간 계산, Exception 처리, Slow Tick 대응. 가장 중요하게 본다.

**Tick 안 금지에 가까운 작업 (15):** DB Query, File I/O, Blocking Network, Task.Wait, 긴 Lock 대기, 대량 Allocation, 전체 Collection Full Scan, 복잡한 LINQ, Reflection, 동기 Logging → 높은 우선순위.

**Complexity (16, 17, 79):** O(N²), Player×Player, Player×BuildPiece, Player×Loot, Player×Session, 매 Tick 전체 World Scan, Player마다 전체 World Scan, 전역 Lock. 100 Player에서 문제가 되는지 평가한다. Spatial Index/Lookup 제안은 실제 병목 가능성이 있을 때만.

**Allocation / GC (18, 19):** Hot Path의 new List/Dictionary/Array, ToList/ToArray, LINQ, string interpolation, closure, boxing, delegate·Task allocation, 임시 DTO. Packet/sec, Player Input, Snapshot, Serialization, Broadcast, Movement Tick, Combat, Build, Bot 경로에서 호출 빈도까지 곱해 본다.

**Pooling (20):** 없다고 무조건 문제 아님. 반복 Allocation이 많을 때만 ArrayPool/MemoryPool/Object Pool/Reusable Buffer를 검토하고, Lifetime 복잡성 증가도 함께 평가한다.

**Snapshot / Broadcast (31, 32):** 매번 전체 상태 생성, 불필요한 Copy, Allocation, Player별 중복 Serialization, Interest Management, 모든 Player×모든 Player 전송, 불필요한 전체 전송.

**Time / Tick Counter (70, 71):** Cooldown, Tick, Timeout, RTT, Reconnect Grace에 wall clock(DateTime.Now) 대신 monotonic clock을 쓰는지. Tick 카운터 타입의 overflow/wraparound(장시간 실행).

**성능 판단 (77, 78):** 명백한 Hot Path 문제 / 잠재적 문제 / 측정 필요를 구분한다. Hot Path가 아니면 작은 struct copy, LINQ 한 번, Dictionary lookup 한 번, 미세 branch는 우선순위를 낮춘다(보통 보고하지 않는다).

## 3. lifetime

**Memory Leak (21):** 장시간 실행 전제. Session, Disconnected Player, Reconnect State, Task, Timer, CTS, Event Handler, Queue, Cache, Dictionary, Packet Buffer, DB Connection이 제거되는가.

**Collection Lifetime (22):** 모든 장기 Collection — 언제 추가, 언제 제거, 제거 안 될 경로, 상한.

**Queue (23):** Network/Input/Game Command/DB/Logging/Background Queue 모두. Bounded인가, Producer가 Consumer보다 빠를 수 있나, Full 정책(Drop/Reject/Backpressure/Disconnect), 무한 증가.

**Queue Starvation (24):** 한 Player·한 종류 작업의 독점 — Packet Spam, 대량 Build Request, Stats Request Spam, Reconnect Spam.

**Session Lifecycle / Disconnect (37, 38):** Connect → Join → Active → Disconnect → Grace → Reconnect 또는 Remove 각 단계 정리. Disconnect 후 남을 수 있는 Timer, Task, Peer/Player/Match Reference, Queue Entry, Event Subscription.

**Match Lifecycle / Reset (67, 68):** 시작 → 종료 → Reset → 다음 Match에서 이전 Player State, Loot, BuildPiece, Zone, Timer, History, Kill Feed, Queue, Metrics State가 남는가. Reset이 일부 Collection만 Clear하고 다른 State를 놓치는가.

**ID (72):** PlayerId, SessionId, MatchId, EntityId, BuildPieceId의 중복·overflow·재사용.

**Cache / Pool / Span / IDisposable (61–64):** Cache는 Max Size, Eviction, Expiration, Invalidation, Thread Safety(무제한 Dictionary Cache는 문제). Pool은 반환 누락, Double Return, 반환 후 참조, 다른 Session 데이터 노출, 무한 증가. Span/Memory는 Lifetime 오류. IDisposable(Socket, Peer, Timer, CTS, DB, Stream, Buffer Owner) 사용·구현.

## 4. network

**Receive (25):** Packet Size 제한, 잘못된 Packet, Unknown ID, Malformed, Truncated, Oversized, Duplicate, Replay, Out-of-order. 잘못된 Client가 Server Crash를 유발할 수 없는가.

**Parsing (26, 58, 59):** Bounds Check 누락, Length 신뢰, Enum Validation 누락, Overflow/Underflow, Negative Length, Huge Count, Invalid UTF-8·String Length, Exception 기반 Parsing. NaN·Infinity(비교를 우회한다), int overflow, 음수 count, 거대 좌표, invalid enum. Serialization의 Allocation, Copy, Length Validation, Version Compatibility, Unknown Field, Packet Size.

**Client 신뢰 (27, 28, 29):** Position, Velocity, Damage, Hit Target, Ammo, Health, Inventory, Build Position, Pickup Distance, Fire Rate, Cooldown, Match Result를 서버가 최종 검증하는가. Movement: Speed Hack, Teleport, 불가능한 가속, 잘못된 Movement State, Out-of-bounds, NaN/Infinity. Combat: Hit Target ID, Damage, Critical, Ammo, Fire Rate, Reload, Cooldown을 Client가 결정하지 않는가.

**Lag Compensation (30):** History Size 고정, Memory 상한, Timestamp 정확성, Old History 제거, Interpolation, Rewind 범위, 악성 Timestamp.

**MTU / Delivery / Sequence (33, 34, 35):** Snapshot·BuildSync의 MTU 초과, Fragmentation 의존. 실제 DeliveryMethod가 Packet 성격에 맞는가(Snapshot Unreliable 가능, MatchResult·Inventory·BuildPlaced Reliable 필요). Sequence의 Overflow, Duplicate, Old, Out-of-order, Reconnect 후 초기화.

**Reconnect (36):** Disconnected Session 제거, Grace 상태, 중복 Session, 동일 Player 중복 접속, Old Peer 생존, Reconnect Race, Timeout Race.

**Security / Rate Limit (56, 57):** 게임 서버 기준 — Packet Spam, Invalid Packet, Replay, State Manipulation, Resource Exhaustion, Connection Flood, Build Spam, Stats Spam. Join, Stats Query, Build, Interact, Chat, Inventory Action처럼 Rate Limit이 필요한 기능을 Movement Input처럼 고빈도가 정상인 Packet과 구분한다.

**Determinism (69):** Movement, Grid, Build Placement, Zone, Timer에서 Server와 Client(Shared 공유 코드 포함) 계산이 달라질 수 있는가.

**Bot (73):** Headless Bot이 실제 Client와 다른 Protocol 동작을 해서, Bot만 통과하고 실제 Client는 실패하는 상황이 있는가.

## 5. database

**DB 경로 (41):** 모든 DB 접근 경로. **Game Loop가 DB를 기다리는가**가 가장 중요하다(그렇다면 높은 우선순위).

**Connection (42):** Pool 사용, Connection·Reader·Command·Transaction Dispose, Exception 경로에서도 반환.

**Query (43):** SELECT *, N+1, 반복 Query, 불필요한 Column, Full Scan, Missing Index, ORDER BY 비용, JOIN. Schema·Index를 확인하지 못했으면 확정하지 않는다.

**Transaction (44):** 범위, Transaction 안 Network 호출, 긴 Transaction, Exception 시 Rollback, Deadlock Retry 정책.

**DB Queue (45):** Bounded, Full 정책, DB Down 시 증가, Retry 폭증, Poison Item, Writer가 죽었을 때 감지.

**DB 장애 (46):** 정책 "DB 없어도 서버는 돈다"가 깨지는 곳.

**Retry (47):** `while(true){ try{} catch{} }` 형태, Backoff, Maximum Retry, Cancellation, Jitter, Retry Storm.

## 6. resilience

**Exception (48, 49):** `catch { }`, catch(Exception) 후 무시, 로그만 남기고 State 복구 안 함, 같은 Exception 무한 반복. 실패 수준(해당 요청만 / Player Kick / Match Reset / Server Shutdown)이 일관된가.

**Logging (50, 51):** Packet마다 Information, Tick마다 Log, 로그 레벨 확인 전 string 생성, Exception Flood. 악성 Client 하나가 초당 수천 개 Warning을 만들 수 있는가 → Rate Limit·집계 필요성.

**Metrics (52):** Tick P50/P95/P99, Active Players/Matches, Packets/s, Bytes/s, Queue Length, GC, Memory, Invalid Packet, Reconnect, DB Queue, Exceptions가 장애 탐지에 충분한가.

**Watchdog (53):** False Positive, 실제 Hang 감지, Watchdog 자체 Exception, Shutdown과의 Race.

**Shutdown (54, 55):** 신규 Connection 차단 → Client 종료 안내 → Game Loop Stop → Worker 종료 → DB Queue Flush(제한 시간) → Dispose → Process 종료 순서가 실제 구현과 맞는가. Task, DB, Thread Join, Lock, Queue Drain에서 무한히 기다릴 수 있는가, Timeout이 있는가.

**Tests (74, 75, 76):** 숫자보다 품질. Happy Path, Invalid Input, Boundary, Race, Duplicate Packet, Out-of-order, Reconnect, Timeout, Shutdown, DB Down, Fuzz가 있는가. 동시성 코드는 deterministic unit test만으로 부족할 수 있으니 Stress·반복 Test를 제안한다. 테스트 삭제·Assert 제거·과도한 Timeout 증가는 해결책으로 제안하지 않는다.
