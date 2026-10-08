# Server 성능 점검 기준 (.NET 10)

`server-concurrency`, `server-hotpath`, `queue-cache` 점검이 켜졌을 때만 읽는다.

## 29. Server 성능 점검 자동 활성화 조건

다음 코드를 수정할 때만 Server 성능 점검을 활성화한다.

- Packet
- Network
- Socket
- Session
- Broadcast
- Game Loop
- Tick
- Worker
- Queue
- Serialization
- Thread
- async / await
- Concurrent Collection
- 대량 데이터 처리
- Hot Path

## 30. Server 성능 목표

Server 성능 점검이 활성화된 경우 우선순위는 다음과 같다.

```text
Latency
↓
Tail Latency
↓
Throughput
↓
Allocation
↓
CPU
```

가능하면 다음 지표를 확인한다.

```text
Average
P50
P95
P99
Max
```

평균 Latency만 보고 판단하지 않는다. 게임에서는 소수의 느린 응답이 체감 끊김을 만들기 때문이다.

## 31. Server Hot Path 점검

다음은 Hot Path 후보로 본다.

- Packet Parsing
- Packet Dispatch
- Session Lookup
- Movement
- Combat
- Game Tick
- Broadcast
- Serialization
- Queue Processing

이 경우에만 다음을 집중적으로 확인한다.

- Allocation
- Lock
- Blocking
- Task 생성
- LINQ
- Reflection
- Boxing
- Copy
- Dictionary Lookup
- Serialization Cost

## 32. Allocation 최적화

Allocation이 실제 Hot Path에 있을 때만 다음을 고려한다.

- Span
- ReadOnlySpan
- Memory
- ArrayPool
- Pooled Buffer
- Reusable Collection

단순히 Allocation이 있다는 이유만으로 Pooling하지 않는다. Pool은 반환 누락·이중 반환 시 데이터 오염을 만들 수 있다.

## 33. ThreadPool 점검

Server Latency 문제가 있거나 Blocking 가능성이 있는 작업에서만 다음을 확인한다.

- ThreadPool Starvation
- 긴 Callback
- Blocking I/O
- 긴 Lock
- Task.Wait
- Task.Result
- Thread.Sleep

## 34. Lock Contention 감시

Lock이 성능에 영향을 줄 가능성이 있을 때만 Contention 감시를 추가한다.

가능하면 Development 환경에서 다음을 측정한다.

```text
Lock Wait Time
Lock Hold Time
Thread
Lock Name
```

예:

```text
Lock contention detected

Lock: PlayerInventory
Wait: 120ms
Thread: 14
```

단순한 Lock 하나 때문에 복잡한 감시 Framework를 만들지 않는다.

## 35. Lock-Free 검토

다음 조건일 때만 Lock-Free를 검토한다.

```text
실제 Contention이 확인됨
Lock이 실제 병목임
Lock-Free 구조를 검증할 수 있음
복잡도 증가가 감당 가능함
```

추측만으로 Lock-Free로 변경하지 않는다.

## 36. Queue 성능 점검

대량 작업 Queue를 만들거나 수정할 때만 다음을 검토한다.

- Queue Length
- 처리 속도
- 생산 속도
- Consumer 수
- Backpressure
- 최대 크기
- Drop / Reject 정책

## 37. Network 성능 점검

Packet / Broadcast 관련 작업일 때만 확인한다.

- Packet Size
- Serialization Cost
- 반복 전송
- Broadcast 범위
- 불필요한 데이터
- Copy 비용

몇 Byte를 줄이기 위해 구조를 과도하게 복잡하게 만들지 않는다.

## 42. Benchmark가 필요한 경우

다음처럼 성능 민감 코드일 때만 Benchmark를 고려한다.

- Serialization
- Packet Parsing
- Compression
- Collection Algorithm
- Scheduler
- High-frequency Game Logic

Server에서는 필요하면 BenchmarkDotNet을 사용할 수 있다. Benchmark는 Release 빌드로 실행한다.

일반 로직에는 Benchmark를 강제하지 않는다.

## 44. Server Profiling이 필요한 경우

Server 성능 문제가 있을 때만 다음을 관측한다.

- CPU
- Memory
- Allocation Rate
- GC Count
- GC Pause
- ThreadPool Queue
- Active Session
- Packet/sec
- Request/sec
- Queue Length
- DB Latency
- Lock Wait
- Lock Hold Time

.NET 런타임 지표(CPU, Allocation Rate, GC, ThreadPool Queue)는 `dotnet-counters`로 관측할 수 있다. 게임 지표(Active Session, Packet/sec, Queue Length, Lock Wait)는 서버가 직접 측정해 노출해야 한다.
