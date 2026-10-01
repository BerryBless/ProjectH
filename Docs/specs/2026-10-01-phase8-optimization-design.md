# Phase 8 Optimization — 설계 Spec

## Context

원래 요청서의 Phase 8은 다음을 구현한다.

```text
실제 Profiling을 기반으로 수행한다.
Client: Frame Time / GC / Draw Call / UI / Physics / Memory
Server: Tick / P95 / P99 / Allocation / GC / Lock / Network
```

관련 조항은 다음과 같다.

- §63 성능 최적화: Measure → Bottleneck 확인 → Optimize → Measure Again. 항상 Micro Optimization을 하지 않는다.
- §7·§56: 10 → 20 → 32 → 64 → 100명으로 단계적으로 측정하고 확장한다.
- §32 Interest Management, §50 Snapshot(Delta Compression, Dirty Flag, Interest Management, Snapshot Compression): 필요해지면 단계적으로 검토한다.
- `game-core-rules`: 성능은 관련 코드에서만 확인하고, 최적화는 문제가 있을 때 한다.

**측정값(Phase 7, `Docs/LoadTest.md`)**

i9-14900K, Release, 서버와 봇을 같은 PC에서 실행했다.

| 봇 | Tick p99 / max (ms) | bytesOut/s | 서버 CPU | GC | inputDrops / badPackets |
|---|---|---|---|---|---|
| 10 | 0.10 / 0.24 | 39 KB | ~0.1 % | 0 | 0 / 0 |
| 50 | 0.94 / 1.81 | 901 KB | ~0.1 % | 0 | 0 / 0 |

**측정이 말하는 것:**

- **서버 CPU·Tick·할당·GC는 문제가 아니다.** 50명에서 Tick p99가 예산(33.3 ms)의 3 %이고 GC는 0번이다. 서버 코드에 Lock은 없다(통계 카운터의 Interlocked뿐).
- **문제는 Snapshot이다. 두 가지다.**
  1. **인원 상한 50명.** Snapshot 하나가 분할 없는 데이터그램 하나(1200 B)에 들어가야 해서 50명이 한계다. 그래서 §56의 64·100명 단계를 측정할 수조차 없다.
  2. **대역폭이 인원의 제곱으로 는다.** 10 → 50명에서 `bytesOut/s`가 23배 늘었다. 측정값 중 가장 크게 늘어난 값이다. 수신자 N명 각각이 N명분 Snapshot을 받기 때문이다.

**성공 기준:**

- 한 경기 100명. 봇 64·100명 부하를 측정해 `Docs/LoadTest.md`에 더한다.
- 같은 50명에서 서버 송신 대역폭이 Phase 7보다 줄었음을 측정으로 보인다.
- Client 예측은 서버와 계속 일치한다. Snapshot 양자화 오차로 재조정(보정)이 일어나지 않는다.
- Client 성능 측정 절차(Unity Profiler + 봇)를 문서로 남긴다. Client 최적화는 그 측정에서 문제가 나온 뒤에 한다.
- 모든 동작은 `dotnet test`와 Client EditMode 테스트로 검증한다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 진행 방식 | 추천안대로 진행하고 추천 이유는 이 문서에 남긴다. Phase마다 푸시한다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **서버 CPU·할당·GC·Lock 최적화는 하지 않는다.** | 측정값이 모두 여유 범위다(50명 Tick p99 0.94 ms, GC 0, Lock 없음). 문제 없는 코드를 고치면 복잡해지기만 하고 얻는 것이 없다(§63, `game-core-rules`). | 없음. 인원이 더 늘어 수치가 나빠지면 그때 측정하고 고친다. |
| D2 | **병목은 Snapshot이다.** 인원 상한(50)과 대역폭(인원의 제곱)을 고친다. | Phase 7 측정에서 가장 크게 는 값이 `bytesOut/s`다. §56의 다음 단계(64·100명)를 막는 것도 Snapshot 크기다. | 없음. |
| D3 | **Snapshot을 여러 패킷으로 나눈다.**<ul><li>패킷 하나에 최대 90명이고, 한 경기 최대 100명이다. 그래서 최대 2개다.</li><li>패킷마다 같은 Tick·Ack·Self 블록을 가진 완전한 헤더에 `Part`, `PartCount`를 더한다(헤더 19 B).</li><li>각 패킷은 혼자서도 적용된다. 한 패킷을 잃으면 그 안의 플레이어가 그 Tick 표본을 하나 못 받을 뿐이다. 원격 플레이어 보간은 이미 표본 누락을 견딘다.</li><li>원격 플레이어 제거는 원래 `PlayerDespawned` 이벤트로 하므로, Snapshot에 없다고 지우지 않는다.</li></ul> | LiteNetLib는 Unreliable·Sequenced 패킷을 분할하지 않는다. 그래서 크기를 넘기려면 나눠 보내야 한다. 패킷마다 독립적으로 적용되면 재조립·대기 버퍼가 필요 없다. 그러면 지연이 늘지 않고 상태도 늘지 않는다. Unity Client는 엔티티를 하나씩 보간기에 넣으므로 바뀌는 곳이 거의 없다. | 패킷이 둘이면 한쪽만 잃는 일이 생긴다. 보간 지연(2 Snapshot 간격)이 흡수한다. |
| D4 | **Snapshot 엔티티를 양자화한다(23 B → 13 B).**<ul><li>위치·VelocityY: 부호 있는 16비트, 1/256 단위(±128 m, ±128 m/s). 맵은 ±80 m다.</li><li>Yaw: 16비트로 360°를 나눈다.</li><li>범위 밖 값은 잘라 넣는다. NaN·무한대는 0이다.</li><li>Client 예측 상태(내 엔티티)도 같은 값을 받는다.<ul><li>최대 오차는 축마다 반 단위(약 0.002 m)다.</li><li>Client 재조정 허용 오차(0.01 m)보다 작아서, 양자화 때문에 보정이 일어나지 않는다. 테스트로 고정한다.</li><li>박스 윗면(정수·1/4 m)과 지형 꼭짓점(1/32 m)은 1/256의 배수라 그대로 전달된다.</li></ul></li></ul> | 대역폭을 43 % 줄인다. 계산은 비트 연산과 반올림뿐이라 비용이 없다. 내 엔티티를 따로 정밀하게 보내는 방법(Self 블록에 float 20 B)은, 오차가 허용 범위 안이라 필요 없다. 그렇게 하면 코드만 갈라진다. | 경사면에서 받은 Y가 지면보다 최대 0.002 m 낮을 수 있다. 다음 Step의 밀어내기가 지면으로 올린다. 서버 판정은 정밀한 서버 값을 쓰므로 영향이 없다. |
| D5 | **Interest Management(가까운 플레이어만 보내기)는 지금 넣지 않는다.** | 계획 단계에서 100명을 실측했다(DevRespawn, 프로토타입).<ul><li>서버 송신 2.07 MB/s(약 16.6 Mbps), Client당 약 20.7 KB/s</li><li>Tick p99 0.30 ms, 드롭 0</li></ul>요청서 범위(최대 100명)에서 감당할 만한 값이다. Interest Management를 넣으면 다음을 새로 정해야 한다.<ul><li>관전자가 먼 사람을 따라가는 규칙</li><li>Zone 밖 판정 표시</li><li>멀리서 쏜 궤적을 누가 보는지</li></ul>측정된 문제 없이 넣을 이유가 없다. | 인터넷 서버 비용이 문제가 되면 그때 거리 기반으로 넣는다. 입력은 이 문서의 측정값이다. |
| D6 | **Client는 측정 절차를 먼저 만들고, 코드는 측정 결과가 나온 뒤에 고친다.** 측정은 다음 순서다.<ol><li>`Docs/ClientPerf.md`에 Unity Profiler와 봇 50·100명으로 Frame Time·GC·Draw Call·UI·Physics·Memory를 재는 절차를 적는다.</li><li>Client Hot Path를 코드 리뷰(정적 점검)로 한 번 훑는다. 찾은 것은 "측정 필요" 목록으로 남긴다.</li></ol> | 이 세션에서는 Unity를 실행해 프로파일링할 수 없다(사용자 Editor). 측정 없이 Client를 고치는 것은 §63 위반이다. 봇이 생겨서 이제 사용자가 혼자서 100명 상황을 재현할 수 있다. | Client 쪽 개선이 다음 측정까지 미뤄진다. |
| D7 | **Protocol 버전을 7로 올린다.** | Snapshot 형식이 바뀌었다. | 없음. |
| D8 | **봇과 테스트 Client도 여러 패킷 Snapshot을 받는다.** 같은 Tick의 패킷은 더하고, 새 Tick이 오면 처음부터 다시 모은다. 봇 상한은 100명이다(`MaxSnapshotEntities`). | 봇이 Part 하나만 보면, 100명 경기에서 적의 절반을 못 본다. | 없음. |
| D9 | **부하 측정을 다시 한다.** 50·64·100명(DevRespawn), 각 2분이다. Phase 7과 같은 절차를 따르고 결과를 `Docs/LoadTest.md`에 더한다. 50명 행으로 이전과 비교한다. | §63의 "Measure Again"이다. | 없음. |
| D10 | **지금 넣지 않는 것:**<ul><li>Delta Compression, Dirty Flag(변하지 않은 플레이어 생략), Interest Management</li><li>Snapshot 주기 변경</li><li>측정 없는 Client 최적화</li><li>100명 초과</li></ul> | D1·D5·D6의 이유와 같다. 모두 측정된 문제가 생길 때 단계적으로 검토한다(§50). | 없음. |

## 1. Protocol v7

| 구조 | 변경 |
|---|---|
| `WorldSnapshotHeader` | `Count` 뒤에 `Part byte`, `PartCount byte`를 넣는다(Size 17 → 19, `SelfOffset` 11 → 13). 읽을 때 검증한다.<ul><li>`Count ≤ 90`</li><li>`1 ≤ PartCount ≤ 2`</li><li>`Part < PartCount`</li></ul> |
| `SnapshotEntity` | id 2, x·y·z 각 2, VelocityY 2, Yaw 2, Flags 1 = 13 B. 구조체 필드는 float 그대로다. 양자화는 Write/Read에서만 한다. `Quantize(float)` 보조 함수를 둔다. |
| `ProtocolConstants` | `MaxSnapshotEntities = 100`(한 경기 최대 인원), `MaxEntitiesPerSnapshotPacket = 90`, `MaxSnapshotParts = 2`, `ProtocolVersion = 7` |

## 2. Server

- `Match.SendSnapshots`
  - 플레이어 목록을 90명씩 나눠 Part마다 하나의 버퍼를 만든다. 이전처럼 수신자별로 Ack·Self 블록만 고쳐 보낸다.
  - 할당이 없다. 기존 할당 없음 테스트를 유지한다.
- `ServerOptions`: `MaxPlayers` 상한은 100이다. 메시지는 "최대 2개의 분할 없는 데이터그램"이다.

## 3. Client·봇·테스트 Client

- **Unity Client 코드는 바꾸지 않는다.**
  - `NetClient`의 엔티티 배열은 `MaxSnapshotEntities`(이제 100)라 패킷당 최대 90명을 담는다.
  - `GameClient.OnSnapshot`은 패킷마다 내 엔티티가 있으면 재조정하고, 나머지는 보간기에 넣는다. 그래서 여러 패킷이어도 그대로 동작한다.
  - 원격 플레이어 제거는 이벤트로 한다.
  - 계획 단계에서 Client EditMode 테스트 전체(127개)와 양자화 재조정 테스트가 새 Protocol로 통과했다.
- **봇 `BotView.ApplySnapshot`**: 새 Tick이면 다른 플레이어 목록을 비우고, 같은 Tick이면 이어 붙인다.
- **테스트 `HeadlessClient`**: 같은 규칙을 따른다.

## 4. 테스트

- **Protocol**
  - 헤더 왕복과 검증 실패 경우(Count 91, PartCount 0·3, Part ≥ PartCount)
  - 90명 패킷이 1189 B(≤ 1200)
  - 양자화: 범위 안 오차 ≤ 1/512, 범위 밖은 잘림, NaN은 0, Yaw 경계(0, 359.99, 음수)
- **Server**
  - 100명 Match의 Snapshot Tick마다 수신자당 패킷 2개다(90 + 10).
  - 두 패킷의 플레이어 합집합이 전원이다.
  - 같은 Tick이고, Ack·Self가 수신자별 값이다.
  - Snapshot 송신에 할당이 없다.
- **봇**: 두 Part를 합친다. 새 Tick에서 비운다.
- **Client EditMode**: 언덕을 넘어 걷는 동안 양자화된 서버 상태로 매 Tick 재조정해도 예측 위치가 서버의 정밀한 위치와 비트까지 같다(보정이 한 번도 없음).
- **통합**: 기존 UDP 통합 테스트가 모두 통과한다.
- **부하**: D9.

## 5. 범위 밖

D10의 항목.
