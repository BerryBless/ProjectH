---
name: game-core-rules
description: "Unity Client / .NET 10 Server / MySQL 게임 프로젝트에서 모든 코드 작성·수정·리뷰에 항상 적용하는 기본 규칙(정확성 우선, 과도한 설계 금지, Client/Server 경계, Shared 최소화, Resource Lifetime, Collection 제거 조건, 무한 증가 방지, 외부 입력 검증, Blocking 금지, 종료 경로, Lock·Deadlock·Lock Ordering, DB Connection·Transaction, 주석·TODO). Client/·Server/·Shared 코드를 쓰거나 고치거나 검토할 때 반드시 먼저 읽는다. 성능 최적화 점검 기준은 이 스킬이 아니라 game-perf-checks를 사용한다."
---

# 게임 프로젝트 기본 규칙 (항상 적용)

이 규칙은 기능 종류나 성능 중요도와 관계없이 모든 개발 작업에 적용한다.
성능 점검은 관련 코드에서만 켜지지만(`game-perf-checks`), 이 문서의 안전성 규칙은 꺼지지 않는다.

기술 스택: Client는 Unity / C#, Server는 .NET 10 / C#, Database는 MySQL.
Client와 Server는 별도 프로젝트로 분리한다. 필요한 경우 Protocol / DTO 수준의 Shared 프로젝트만 사용한다.

작업 대상 경로:

- `Client/` — Unity 프로젝트. `Client/Library`, `Client/Temp`, `Client/Logs`, `Client/obj`, `Client/UserSettings`는 Unity 생성물이므로 읽거나 수정하지 않는다.
- `Server/` — .NET 10 서버.
- `Server/src/ProjectH.Bots` — 테스트·부하용 Headless 봇. 서버 프로젝트를 참조하지 않고 Network Protocol로만 서버와 통신하는 Client다(§3의 Client 규칙을 따른다). Client 코드도 링크하지 않는다.
- `Shared/` — Client와 Server가 함께 쓰는 로컬 UPM 패키지. Unity는 `manifest.json`에서 `file:../../Shared`로 참조하고, Server는 같은 소스를 `Server/src/ProjectH.Shared/ProjectH.Shared.csproj`(netstandard2.1, C# 9)로 컴파일한다. 게임 로직 예외는 `Shared/Runtime/Simulation`에만 허용한다(§4).

---

## 1. 정확성과 안정성을 우선한다

항상 다음 순서를 기본으로 한다.

```text
Correctness
↓
Stability
↓
Readability
↓
Maintainability
↓
Performance
```

성능을 이유로 코드의 정확성이나 안정성을 희생하지 않는다.

## 2. 과도한 설계를 하지 않는다

현재 게임의 상세 기획은 확정되지 않았다. 따라서 미래 요구를 예상해 복잡한 구조를 미리 만들지 않는다.

다음을 필요 없이 추가하지 않는다.

- 불필요한 Interface
- 의미 없는 Layer
- 과도한 Generic
- 필요 없는 Design Pattern
- Event Bus
- Message Broker
- Actor Framework
- ECS
- 복잡한 Job System
- Sharding
- Distributed Server
- 범용 Framework

현재 요구사항을 가장 단순하게 해결하는 구조를 우선한다.

## 3. Client / Server 경계를 유지한다

기본 구조는 다음과 같다.

```text
Client
↓
Network Protocol
↓
Server
↓
Database
```

Client는 다음을 직접 알지 않는다.

- Server 내부 Entity
- Server 내부 서비스
- DB 구조
- DB Connection
- Server 전용 구현

Server는 Unity 관련 코드에 의존하지 않는다.

## 4. Shared 프로젝트는 최소화한다

Shared가 필요한 경우 다음 정도만 공유한다.

- Packet DTO
- Protocol ID
- Network Enum
- 공통 데이터 구조

Shared에 게임 로직을 넣지 않는다.

예외: `Shared/Runtime/Simulation`의 이동 계산만 둔다. 여기에는 `MovementSimulation`과 그 입력·상태·상수 타입, 이동 계산이 읽는 지형(박스 `Box`·`GameMap.Boxes`, 높이 격자 `HeightField`·`Hill`·`GameMap.Terrain`)과 캐릭터–지형 충돌·높이 조회가 포함된다. Client Prediction과 서버 시뮬레이션이 같은 코드와 같은 지형으로 계산해야 예측이 어긋나지 않기 때문이다. 이 폴더에는 `System.Numerics`만 쓰는 순수 계산과 그 계산이 읽는 상수 데이터만 두고, 전투·인벤토리 등 다른 게임 규칙(사격 광선 판정 포함)은 넣지 않는다.

예외 2: 맵 배치 데이터(좌표 상수). Loot Spawn Point(`LootPoints`), 투입 지점(`DropPoints`), POI 이름·중심·반지름(`MapPois`)처럼 맵과 함께 바뀌어야 하는 상수는 `GameMap` 옆 `Shared/Runtime/Simulation`에 둔다. 맵을 바꿀 때 박스·지형과 함께 고치기 위해서다. 좌표와 이름 같은 상수만 두고, Loot Table·난수·투입 순서·줍기 판정 같은 규칙은 넣지 않는다. Loot와 투입 지점은 서버만 읽는다(Client는 위치를 서버 이벤트로만 받는다). POI는 Client가 이름 표시에만 쓴다.

예외 3(Phase 12): 이동 모드(웅크리기·슬라이드·Vault·자유 낙하·글라이드·수송기 탑승)와 그 판정(`CanStand`, Mantle·Hurdle 후보 검사, 지면 거리), 수송기 경로에서 위치를 구하는 순수 계산(`DropRoute`·`Ride`), 문 상자(`GameMap.Doors`)는 이동 계산의 일부라 `Shared/Runtime/Simulation`에 둔다. 예측과 서버가 같은 결과를 내야 하기 때문이다. 경로를 시드로 고르는 난수(`DropPlanner`)와 문 상호작용 규칙(`DoorRules`, Client는 같은 규칙의 복사본과 일치 테스트)은 서버·Client 쪽에 두고 Shared에 넣지 않는다. 문 상호작용 거리·각도 상수는 두 규칙 복사본이 같이 읽으므로 Shared 상수로 둔다(`MovementTuning.DoorInteractRange`·`DoorInteractHalfAngle`). 낙하 피해처럼 서버만 쓰는 규칙의 상수는 Shared에 두지 않는다(`CombatRules.FallDamage*`).

## 5. 기존 구조를 먼저 확인한다

코드를 변경하기 전에 관련 코드를 먼저 읽는다.

다음을 하지 않는다.

- 기존 기능 중복 구현
- 기존 구조를 무시하고 새로운 구조 추가
- 요청하지 않은 대규모 리팩터링
- 필요 없는 파일 대량 생성

변경 범위는 가능한 작게 유지한다.

## 6. Resource Ownership을 명확하게 한다

Resource를 생성하면 반드시 다음을 알 수 있어야 한다.

```text
누가 생성하는가?
누가 소유하는가?
언제 종료되는가?
누가 해제하는가?
```

특히 다음은 Lifetime을 명확히 한다.

- Client: Event Subscription, Coroutine, Task, CancellationToken, Addressable Handle, Texture, Material, RenderTexture, ComputeBuffer, Native Resource
- Server: Session, Socket, Task, Timer, CancellationTokenSource, Queue, Buffer, DB Connection, IDisposable

## 7. Collection은 제거 조건이 있어야 한다

Dictionary, List, Queue, Cache 등에 장시간 객체를 저장하는 경우 항상 확인한다.

```text
이 데이터는 언제 제거되는가?
```

제거 조건이 없는 Collection을 장기 저장소처럼 사용하지 않는다.

## 8. 무한 증가 구조를 만들지 않는다

다음은 기본적으로 무제한 증가하지 않도록 한다.

- Queue
- Cache
- Pending Request
- Session Registry
- Buffer
- History
- Log Buffer
- Retry Queue

필요하면 다음 정책 중 하나를 정의한다: Maximum Size, Expiration, Reject, Drop, Backpressure, Disconnect, Retry 제한.

## 9. 외부 입력을 신뢰하지 않는다

Client Packet, Network Data, 사용자 입력은 항상 검증한다.

잘못된 입력 때문에 다음이 발생해서는 안 된다.

- Server Crash
- Exception Loop
- Memory 폭증
- Queue 폭증
- 잘못된 State
- DB 손상

## 10. Blocking을 불필요하게 만들지 않는다

Server에서는 기본적으로 다음을 피한다.

```text
Task.Wait()
Task.Result
Thread.Sleep()
불필요한 Blocking I/O
```

I/O는 가능한 경우 비동기로 처리한다.

## 11. Exception을 일반 흐름으로 사용하지 않는다

Exception은 정상적인 제어 흐름으로 사용하지 않는다.
반복 실행되는 코드에서 Exception이 계속 발생하도록 설계하지 않는다.

## 12. 종료 경로가 있어야 한다

장시간 실행되는 작업은 종료할 수 있어야 한다.

특히 Server에서는 다음을 정상적으로 종료할 수 있어야 한다: Network Listener, Session, Worker, Timer, Queue Consumer, BackgroundService, DB 작업.

필요하면 CancellationToken을 사용한다.

## 13. Lock 기본 규칙

Lock 자체를 금지하지 않는다. 가장 단순하고 안전한 방법이 Lock이라면 사용할 수 있다.

하지만 Lock을 사용하는 경우 항상 다음 규칙을 지킨다.

- Critical Section을 짧게 유지
- Lock 안에서 긴 작업 금지
- Lock 안에서 Network I/O 금지
- Lock 안에서 DB I/O 금지
- Lock 안에서 File I/O 금지
- Lock 안에서 await 금지
- 가능하면 외부 Callback 호출 금지

## 14. Lock을 사용하면 Deadlock 검토는 항상 한다

이 규칙은 조건부가 아니다. Lock을 추가하거나 수정한다면 반드시 Deadlock 가능성을 확인한다.

```text
다른 Lock과 동시에 잡힐 수 있는가?
Nested Lock이 존재하는가?
Lock 획득 순서가 일정한가?
역순 획득이 가능한가?
Callback을 통해 재진입할 가능성이 있는가?
Lock 내부에서 외부 코드를 호출하는가?
```

## 15. Lock Ordering을 지킨다

둘 이상의 Lock을 함께 획득할 수 있다면 순서를 정의한다.

```text
SessionLock
↓
PlayerLock
↓
InventoryLock
```

반대 순서로 획득하지 않는다. 정의한 순서는 해당 Lock 선언부 주석에 남겨, 다음 작업자가 코드만 보고도 순서를 알 수 있게 한다.

## 16. DB Connection은 반드시 반환한다

DB Connection, Command, Reader 등은 정상적으로 Dispose 또는 반환되어야 한다.
Connection Leak은 허용하지 않는다.

## 17. Transaction은 짧게 유지한다

Transaction 안에서 다음을 하지 않는다.

- Network I/O
- 외부 API
- 긴 계산
- 사용자 응답 대기
- 불필요한 대량 작업

## 18. 성능을 위해 불필요하게 복잡하게 만들지 않는다

다음을 근거 없이 도입하지 않는다: Object Pool, Cache, Lock-Free, Span 남용, Concurrent Collection 남용, Update Manager, Custom Scheduler, Custom Allocator.

문제가 있을 때만 도입한다.

## 19. 주석은 이유를 설명한다

다음과 같은 주석은 피한다.

```csharp
// Player를 가져온다.
var player = GetPlayer();
```

다음과 같은 주석은 허용한다.

```csharp
// PlayerLock must be acquired before InventoryLock.
// Reversing this order can cause a deadlock.
```

주석은 다음을 설명할 때 사용한다: 왜 이렇게 구현했는가, Thread Safety 이유, Lock Ordering, Performance 이유, Lifetime 제약, Protocol 제약.

## 20. TODO는 구체적으로 작성한다

잘못된 예:

```text
TODO: optimize
```

좋은 예:

```text
TODO:
Current broadcast iterates all sessions.
Profile when concurrent users exceed 5,000.
Consider spatial partitioning if P99 latency becomes unacceptable.
```

---

## 항상 하지 말아야 하는 것

다음은 명확한 이유가 없으면 하지 않는다.

```text
성능 문제 없는 코드를 억지로 최적화
모든 객체에 Object Pool 적용
모든 Collection을 Concurrent Collection으로 변경
모든 코드를 Span 기반으로 변경
Lock을 무조건 Lock-Free로 변경
모든 Update 제거
작은 Allocation까지 무조건 제거
미래 기능을 예상해서 복잡한 시스템 구축
사용자 요청과 관계없는 대규모 리팩터링
```

## 핵심 원칙

```text
안전성은 항상 확인한다.
성능은 관련된 코드에서만 확인한다.
최적화는 문제가 있을 때 한다.
Lock을 썼다면 Deadlock은 항상 확인한다.
```
