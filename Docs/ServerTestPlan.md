# 서버 테스트 플랜 (반응성 1순위, 보안 2순위, 피크 20만 동접)

이 문서는 서버를 무엇으로 어떻게 시험하고 무엇을 합격으로 볼지 정한다. 측정 결과는 여기가 아니라 `LoadTest.md`에 쌓는다. 이 문서의 시나리오를 실행하려면 8절의 도구가 먼저 필요하다. 도구 구현은 별도 Phase로 한다.

## 1. 목표와 우선순위

- **1순위 반응성:** 플레이어가 누른 입력이 서버에 반영되어 화면으로 돌아오기까지의 지연이 피크 부하에서도 기준 안에 있어야 한다.
- **2순위 보안:** 지금 있는 방어(입력 검증, Rate Limit, Bad Packet Kick)가 피크 부하에서도 유지되어야 한다. 공격자가 정상 플레이어의 반응성을 떨어뜨리지 못해야 한다.
- 우선순위는 합격 기준으로 표현한다. 보안 시나리오의 합격 조건은 공격자가 막히는 것만이 아니다. 공격이 걸려 있는 동안 정상 플레이어의 반응성 지표(R1)가 허용 범위 안에 머물러야 한다(6절).

### 범위

| 단계 | 내용 | 지금 실행 가능 |
|---|---|---|
| 1단계 | 지금 구조(프로세스 1개 = Match 1개, 최대 100명)로 서버 1대의 한계를 실측한다 | 예(8절 도구 필요) |
| 2단계 | 1단계 결과로 20만 동접에 필요한 서버 대수를 산정한다. 로그인·매치메이킹·다중 서버가 생긴 뒤 실행할 테스트를 정의한다 | 아니오(정의만) |

## 2. 20만 동접 산정

### 가정

- **최악 기준:** 20만 명이 모두 100명이 가득 찬 Match 2,000개에서 동시에 싸운다.
  - 측정은 `DevRespawn=true`로 한다. 죽은 봇도 3초 뒤 살아나 N명이 내내 움직이기 때문이다(`LoadTest.md`).
  - 배틀로얄 후반(생존자 감소)과 로비·대기 인원은 이보다 부하가 낮다. 그래서 이 가정은 위쪽 경계다.
- Match당 비용은 100봇 실측값을 쓴다(`LoadTest.md` Phase 8, i9-14900K, 루프백).

| 항목 | Match 1개 (100명) | 2,000 Match (20만 명) |
|---|---|---|
| 서버 송신 | 2,075 KB/s ≈ 16.6 Mbps | ≈ 33 Gbps |
| 서버 송신 패킷 | 6,143 pkt/s | ≈ 1,230만 pkt/s |
| 서버 수신 패킷 | 3,000 pkt/s (100 × 30 Hz) | ≈ 600만 pkt/s |
| 서버 CPU | 32코어 중 0.29 % ≈ 0.09코어 | ≈ 186코어 |
| 메모리 | ≈ 50 MB | ≈ 100 GB |

### 필요 서버 대수

```text
서버 대수 = ⌈ 2,000 / 머신당 Match 수 × 1.3 ⌉
```

- 머신당 Match 수는 1단계 L2에서 실측한다. R1–R4를 모두 지키는 최대 Match 수다.
- 1.3은 여유분이다. 장애 서버 대체, 배포 중 교체, 피크 쏠림에 쓴다.
- CPU만 보면 머신 한 대에 Match 수백 개가 들어갈 것처럼 보인다. 그러나 Windows UDP 송수신에서는 머신당 pkt/s와 NIC 대역폭이 먼저 한계에 닿을 가능성이 높다.
  - 예: 10 GbE에서 Match 100개면 송신 1.66 Gbps, 61만 pkt/s다.
  - 그래서 1단계는 Match 수와 함께 그때의 pkt/s, Gbps를 기록한다.

## 3. 반응성 지표와 합격 기준

### R1. 입력→반영 지연 (1순위 지표)

**정의:** Probe(8절 T1)가 Input Seq `s`를 보낸 시각부터, Snapshot 헤더의 `AckInputSeq ≥ s`인 첫 Snapshot을 받은 시각까지의 시간에서 그 순간의 RTT를 뺀 값이다. 서버가 더한 지연만 남긴다.

**구조상 범위.** 서버는 Tick마다 플레이어당 Input을 하나만 꺼내고(`Match.TakeInput`), Snapshot은 2 Tick마다 보낸다(`SnapshotEveryTicks=2`). 그래서 서버 지연은 다음 합이다.

| 구성 | 범위 (SimHz 30) |
|---|---|
| 다음 Tick까지 대기 | 0–33.3 ms |
| 반영된 Tick부터 다음 Snapshot Tick까지 대기 | 0–33.3 ms |
| 버퍼에 먼저 쌓인 Input 수 × 1 Tick | k × 33.3 ms |
| Tick 처리와 송신 | 1 ms 미만(Tick p99 0.30 ms) |

버퍼가 비어 있으면 최대 약 67 ms다. 그래서 `SnapshotEveryTicks`나 `SimHz`를 바꾸지 않는 한 p99를 67 ms 아래로 요구할 수 없다.

**합격 기준(둘 다 만족):**

| 종류 | 기준 |
|---|---|
| 절대 | p99 ≤ 75 ms, max ≤ 110 ms |
| 상대 | N개 Match 동시 실행 시 p99 ≤ L1(1개 Match) 기준값 + 5 ms |

- 절대 기준은 플레이어가 느끼는 상한이다. 구조상 최대 67 ms에 Tick 시작 지연과 측정 오차 여유를 더한 값이다.
- 상대 기준은 밀도(한 머신에 Match를 많이 올림)가 반응성을 깎는지를 잡는다.

### R2. Input 버퍼 깊이

- **정의:** Tick에서 Input을 꺼내기 직전, 플레이어별 `PlayerInputBuffer.Count`.
- **기준:** 평균 ≤ 1, p99 ≤ 2.
- **이유:** 서버는 Tick당 하나만 꺼내고 밀린 것을 따라잡지 않는다. 클라이언트 Tick 시계가 서버보다 조금이라도 빠르면 버퍼가 천천히 쌓인다. 쌓인 만큼(최대 8칸 × 33.3 ms ≈ 267 ms) 지연이 계속 높게 유지된다. 이것은 이론이 아니라 지금 코드에서 일어날 수 있는 일이므로 R1과 따로 잰다.

### R3. Tick 시작 지연과 Snapshot 간격 지터

- **Tick 시작 지연:** 예정 시각보다 Tick이 늦게 시작한 시간. 기준은 p99 ≤ 2 ms, max ≤ 10 ms다.
- **Snapshot 도착 간격:** Probe에서 본 연속 Snapshot 사이 간격. 기준은 p99 ≤ 70 ms다. 정상값은 66.7 ms(15 Hz)다.
- **이유:**
  - `GameLoop.WaitUntil`은 남은 시간이 2 ms보다 많으면 Sleep하고, 그 이하에서는 `Thread.Yield`로 돈다.
  - 한 머신에 프로세스가 많으면 Tick 처리 시간은 그대로여도 OS 스케줄링 때문에 Tick 시작이 늦어질 수 있다.
  - 지금 지표로는 이를 볼 수 없다. Stats 줄의 Tick 백분위는 처리 시간이고, `lateTicksSkipped`는 5 Tick 넘게 밀려야 올라간다.

### R4. 보조 지표

| 지표 | 기준 |
|---|---|
| Tick 처리 시간 | p99 ≤ 5 ms, max ≤ 16.7 ms (예산 33.3 ms) |
| `lateTicksSkipped`, `inputDrops`, `bufferDrops`, `exceptions`, `tickFailures`, `matchResets`, `stalls` | 모두 0 |
| 비정상 끊김 (`disconnects`, `joinTimeout`, `inputTimeout`, `serverError`) | 0 |
| GC | Gen2 0회(측정 구간) |

### 측정 정밀도

- 부하 봇(`ProjectH.Bots`)은 모든 봇을 한 스레드에서 30 Hz로 돌리며 `PollEvents`로 받는다(`UnsyncedEvents=false`).
  - 그래서 도착 시각이 최대 33 ms 단위로 뭉개진다. 재려는 값과 크기가 같다.
  - 따라서 봇의 시각으로는 R1, R3를 재지 않는다.
- R1, R3는 Match마다 Probe 2개로 잰다. Probe는 수신 이벤트 시각을 1 ms 이하 정밀도로 기록하는 전용 클라이언트다(8절 T1).
- 나머지 98명분 부하는 기존 봇이 만든다.

### 집계

- `LoadTest.md`와 같은 방식이다. 앞 20초(접속 램프와 JIT)를 버리고 나머지 구간으로 계산한다.
- 백분위는 Probe 원시 표본을 합쳐서 계산한다. 10초 줄별 값의 최댓값을 쓰는 서버 Stats와 다르다.
- 같은 조건을 2회 실행한다. 두 실행 차이가 기준 여유보다 크면 3회째를 돌린다.

## 4. 1단계: 서버 1대 한계 측정

### 환경

- **배치:** 테스트 대상 서버 1대와 부하 생성기 머신을 분리한다. 지금까지의 측정(같은 PC, 루프백)과 구분하기 위해서다.
- **실행 전에 기록할 것:** CPU 모델과 코어 수, 메모리, OS 빌드, NIC 속도, .NET SDK 버전, 전원 설정(고성능), 서버 빌드 Commit.
- **서버 실행:** Release 빌드로, 프로세스마다 다른 포트를 쓴다.
  - 기본 옵션: `--Server:Port=<p> --Server:MaxPlayers=100 --Server:DevRespawn=true`
  - DB 저장은 L7을 빼고 끈다.
- **부하 생성기 규모:**
  - 봇 100명은 코어 약 0.06개를 쓴다(`LoadTest.md` 5.69 %).
  - 다만 `BotConnection`마다 `NetManager`(소켓과 수신 스레드)를 따로 만든다. 그래서 봇 수만큼 스레드가 생기고, `--count`는 100이 상한이다.
  - Match마다 봇 프로세스 하나를 띄운다.
  - 사전 리허설에서 Match 16개 분량을 띄워 부하 생성기의 CPU와 스레드 수를 재고, 필요한 대수를 정한다. 부하 생성기의 CPU가 70 %를 넘으면 그 측정은 무효로 한다.

### 시나리오

| ID | 시나리오 | 방법 | 판정 | 필요 도구 |
|---|---|---|---|---|
| L1 | 기준선 | Match 1개 = 봇 98 + Probe 2, 10분 | R1–R4. R1 p99는 L2 상대 기준의 기준값 | T1, T2, T3 |
| L2 | 밀도 계단 | Match 수 1 → 8 → 16 → 32 → 64 → 128 …, 단계마다 10분 | R1–R4 중 처음 어긋나는 단계를 찾고 그 앞뒤를 이분 탐색한다. 마지막 합격 단계를 "머신당 Match 수"로 기록하고 그때의 pkt/s, Gbps, CPU, 메모리, 스레드 수를 함께 적는다 | T1, T2, T3, T5 |
| L3 | 클라이언트 시계 드리프트 | Probe의 Input 주기를 서버보다 +0.5 %, +2 % 빠르게 한다. Match 1개와 L2 한계의 80 %에서 각 10분 | R2(버퍼 깊이), R1. 버퍼가 줄지 않고 계속 차면 실패로 보고 개선 과제로 올린다 | T1, T2 |
| L4 | 네트워크 조건 | Probe 쪽 경로에만 지연 50 ms, 지터 ±10 ms, 손실 1 %를 넣는다(Linux `tc netem` 또는 Windows clumsy). Match 1개와 L2 한계의 80 % | RTT를 뺀 R1이 L1 + 5 ms 안. Snapshot 2패킷 중 한쪽만 잃었을 때의 Snapshot 간격 p99도 기록한다 | T1 |
| L5 | 접속 폭주 | 빈 서버에 100명을 `--connect-interval-ms 0`으로 한꺼번에 접속시킨다. 같은 머신의 다른 Match들은 경기 중 | 접속부터 Join 완료까지 p99 ≤ 1 s. 다른 Match의 R1이 상대 기준 안 | T1, T3 |
| L6 | 장시간 | L2 한계의 80 %로 2시간. Match 하나는 배틀로얄 모드(`DevRespawn=false`)로 경기 주기를 반복 | R1–R4 유지. 프로세스별 메모리, 핸들 수, 스레드 수가 단조 증가하지 않는다(Collection 무한 증가 방지 규칙). 경기 종료 → 다음 판 전환이 매번 정상 | T2, T3, T5 |
| L7 | DB 동시 쓰기 | L2 한계의 80 %에서 모든 프로세스가 Persistence를 켜고 MySQL 하나를 쓴다. 배틀로얄 모드로 경기 종료가 몰리게 동시에 시작한다 | `MatchHistoryQueue` 드롭 0, 저장 Transaction p99 ≤ 500 ms, MySQL 동시 연결 수가 `max_connections`(기본 151) 아래, 쓰기 폭주 중 R1 상대 기준 유지 | T3, T5 |

## 5. 2단계: 다중 서버 구성 후 (정의만)

다음 항목은 해당 구성 요소가 생긴 뒤 실행한다. 지금은 선행 조건과 판정만 정한다.

| ID | 시나리오 | 선행 조건 | 판정 |
|---|---|---|---|
| M1 | 20만 접속 폭주: 10분에 걸쳐 램프(초당 약 330명) | 로그인, 매치메이킹 | 로그인 응답 p99, 매치 배정까지 대기 p99, 실패율. 이미 경기 중인 Match의 R1 상대 기준 유지 |
| M2 | 매치메이킹 큐 | 매치메이킹 | 큐 길이 상한(무한 증가 금지), 대기 시간 p99, Match 배정 실패율 |
| M3 | 공유 DB | 서버 대수만큼의 프로세스, 공유 MySQL | 연결 수 상한(프로세스마다 MySqlConnector 풀이 따로라 2,000개 프로세스면 `max_connections`를 크게 넘는다), 쓰기 처리량(경기 종료 초당 약 2건 × 행 100여 개), 저장 실패율 |
| M4 | 서버 1대 장애·재시작 | 다중 서버, 재접속 경로 | 영향받는 플레이어가 그 서버의 Match로 한정되는지, 재접속 성공률 |
| M5 | 실제 WAN | 지역별 Probe 머신 | 지역별 RTT를 뺀 R1이 L1 + 5 ms 안 |

## 6. 보안: 부하 중 테스트

L2 한계의 80 % 부하에서, Match 하나에 공격 클라이언트를 넣어 실행한다. 각 10분이다.

**공통 합격 기준:**
1. 공격 중 같은 Match와 같은 머신의 정상 Probe R1 p99 증가 ≤ 5 ms다(공격 없는 같은 부하 대비).
2. 정상 클라이언트의 R4 드롭과 끊김이 0이다.
3. 공격자는 아래 "기대 동작"대로 막히거나 Kick된다. 해당 Health 카운터가 오른다.

| ID | 공격 | 기대 동작 (현재 방어) | 비고 |
|---|---|---|---|
| S1 | 잘못된 패킷 폭주: 알 수 없는 PacketId, 잘린 형식, 서버→클라이언트 방향 패킷 | 이유별 `badPackets` 증가, 20건이면 Kick(`BadPacketDisconnectThreshold`) | 공격자 수 1, 10, 50 |
| S2 | Input 속도 초과: 초당 60패킷 초과, 패킷당 Input 3개 초과, 먼 미래 Seq | 공유 Input 채널에 넣기 전에 피어별 Rate Limit이 걸린다(`NetworkListener` `TryCountInputPacket`). 정상 플레이어의 Input이 밀려나지 않는다 | 지금 코드로 막히는 것이 확인된 경로다. 회귀 방지 케이스로 둔다 |
| S3 | 스피드 핵: Tick보다 빠른 Input 주기 | 공격자 자신의 버퍼 8칸에서 잘리고(`PlayerInputBuffer` DropOldest), 이동 결과는 서버가 정한다. 다른 플레이어의 R1, R2는 변화 없음 | L3와 같은 장치로 공격자에게만 +50 % 적용 |
| S4 | 접속 슬롯 고갈: 접속 후 Join하지 않고 5초 Join Timeout 직전에 끊고 다시 접속하기를 반복 | 현재는 막는 장치가 없다. 공격자 수가 남은 슬롯 이상이면 정상 접속이 `ServerFull`로 거부될 수 있다 | 정상 접속 성공률을 측정한다. 실패하면 7절 G2로 옮긴다 |
| S5 | MaxPlayers 초과 접속 폭주, 연결 전 쓰레기 UDP 데이터그램 | LiteNetLib 단계에서 거부, GameLoop까지 오지 않음 | 거부 처리 비용(서버 CPU, 다른 Match R1)을 측정한다 |
| S6 | 큰 패킷, MTU 경계(1,200 B / 1,232 B 초과) | 거부 또는 `Malformed`, 다른 피어 정상 | |
| S7 | 불가능한 좌표, NaN/±Inf, 범위 밖 Id | 검증에서 거부. 서버 상태 오염 없음 | 기존 `ProtocolFuzzTests`, `InputFuzzTests`, `FuzzIntegrationTests` 입력 생성기를 부하 중 공격 클라이언트로 재사용 |
| S8 | StatsRequest 폭주: 정상 범위 간격으로 다수 피어가 동시에 요청 | 피어별 최소 간격, 큐가 차면 `Busy` 응답 | MySQL 부하와 GameLoop R1 영향을 측정한다(L7과 함께) |

## 7. 보안 갭 목록 (공개 서버 전 차단 항목)

Phase 10(spec D12)에서 "개발용 LAN 서버"라는 이유로 미룬 항목이다. 지금은 실패가 예상되며, 구현이 들어오면 합격으로 바뀌어야 한다. 공개 서버로 열기 전에 모두 합격해야 한다.

| ID | 갭 | 테스트 케이스 | 합격 조건 (구현 후) |
|---|---|---|---|
| G1 | 인증 없음 | 다른 플레이어의 DevPlayerId로 접속해 재접속 유예 중인 캐릭터를 가져간다(`Networking.md` 알려진 위험) | 세션 토큰 없이 유예 캐릭터를 재개할 수 없다 |
| G2 | IP별 연결 수·속도 제한 없음 | 한 IP에서 S4, S5를 반복한다 | IP당 연결 수와 초당 연결 시도 상한, 초과 시 차단 |
| G3 | 볼륨형 DDoS | 서버 앱으로는 막을 수 없다. 인프라 요구사항(상류 필터링, 스크러빙)으로 기록하고 테스트 대상에서 뺀다 | 인프라 계약·구성 문서로 확인 |
| G4 | 암호화·변조 방지 없음 | 경로 중간에서 Input을 바꿔 다시 보낸다(Replay) | 변조·재전송 패킷 거부 |
| G5 | Anti-cheat 통계·Hit 기록 없음 | 비정상 명중률 봇을 돌린다 | 사후 판정에 쓸 기록이 남는다 |
| G6 | DB 자격 증명이 `appsettings.json`에 평문(개발용) | 배포 산출물에 비밀번호가 있는지 검사한다 | 환경 변수나 비밀 저장소로만 주입 |

상태(2026-10-08 리뷰 수정, `Docs/specs/2026-10-08-review-fixes-design.md`):

- G1: **세션 토큰 구현(외부 인증은 다음).** 유예 캐릭터는 이전 연결의 Resume 키로 만든 증명(새 세션 키에 묶임)이 있어야 재개된다(묶음 B4, `ReconnectGraceTests`, `ReconnectIntegrationTests.AnotherClient_WithTheSameName_CannotTakeTheGracedCharacter`, `BotIntegrationTests.ABot_ThatAbortsAndReconnects_ResumesWithItsProof_AndAnotherBotCannotTakeIt`). 전적 계정은 아직 자기 신고 DevPlayerId다.
- G2: IP당 동시 연결 수·전역 수락 빈도·쿠키 단계 구현(묶음 A).
- G4: **HMAC·재전송 창 구현(암호화 없음).** 모든 데이터그램에 counter + HMAC 꼬리, 64칸 재전송 창, 서버 RSA 공개키 핀으로 세션 키 교환(묶음 B3, `SessionAuthTests`, `AuthPacketLayerTests`). 경로 위 변조·재전송은 버려지고 `authDrops`로 센다. 내용은 평문이다.

## 8. 필요한 도구 (별도 Phase로 구현)

| ID | 도구 | 하는 일 | 쓰는 시나리오 |
|---|---|---|---|
| T1 | Probe 클라이언트 | Seq별 송신 시각을 기록하고, 수신 이벤트를 1 ms 이하 정밀도로 받아(UnsyncedEvents 또는 1 ms 폴링) R1, R3, RTT 원시 표본을 파일로 남긴다. Input 주기 배율 옵션(L3, S3) | L1–L5, S* |
| T2 | 서버 지표 추가 | Tick 시작 지연 p99/max, 플레이어별 Input 버퍼 깊이 평균/p99를 Stats 줄과 Meter에 추가 | L1–L3, L6 |
| T3 | 다중 프로세스 실행기 | 서버 N개와 봇 N개를 포트별로 띄우고 끄며 로그를 모은다. 자기가 띄운 PID만 끈다. 프로세스별 메모리, 핸들, 스레드 수 샘플링 | L1–L7 |
| T4 | 공격 클라이언트 모드 | S1–S8 각각의 공격을 옵션으로 실행 | S1–S8 |
| T5 | 집계 스크립트 | 앞 20초를 버리고 Probe 표본 백분위, 서버 Stats·Health 합계, 기준 대비 합격/불합격 표를 만든다 | 전체 |

## 9. 결정과 이유

| ID | 결정 | 이유 | 틀렸을 때 비용 |
|---|---|---|---|
| D1 | 단계형: 1단계는 서버 1대 실측, 2단계는 정의만 | 로그인·매치메이킹·다중 서버가 없어 20만 실측은 지금 불가능하다. 서버 1대 한계가 대수 산정의 입력이다 | 2단계 구성이 바뀌면 M1–M5를 다시 써야 한다(문서 비용) |
| D2 | 1순위 지표를 입력→반영 지연(R1)으로 | 플레이어가 느끼는 반응성은 Tick 처리 시간이 아니라 큐 대기, Tick 정렬, Snapshot 주기, 송신을 모두 합친 값이다 | 측정 도구(T1)가 더 필요하다. Tick 시간만 쓰면 밀도 증가로 인한 저하를 놓친다 |
| D3 | 절대 기준(p99 ≤ 75 ms)과 상대 기준(L1 + 5 ms)을 함께 | 절대 기준은 구조상 상한(67 ms)에 맞춰 정했다. 상대 기준은 밀도가 깎는 만큼을 잡는다 | 기준이 느슨하면 한계를 높게 잡아 서버 대수가 모자란다. 빡빡하면 대수를 많이 잡아 비용이 는다 |
| D4 | R1, R3는 전용 Probe로 재고 부하는 기존 봇으로 | 봇은 30 Hz 폴링이라 도착 시각이 33 ms 단위로 뭉개진다 | Probe 2개가 Match 상황을 대표하지 못할 수 있다. 그러면 Probe 수를 늘린다 |
| D5 | 최악 기준을 DevRespawn 100명 가득 찬 Match로 | 배틀로얄은 인원이 줄어 부하가 낮다. 위쪽 경계로 잡아야 대수 부족이 없다 | 실제보다 서버를 많이 잡는다. 2단계에서 실제 경기 분포로 보정한다 |
| D6 | 여유분 1.3 | 장애 대체, 배포 교체, 피크 쏠림 | 부족하면 피크에 접속 거부. 과하면 유휴 비용 |
| D7 | 보안 합격에 정상 Probe R1 저하(≤ 5 ms)를 포함 | "반응성 1순위, 보안 2순위"를 테스트로 확인하려면 공격이 반응성을 깎는지를 봐야 한다 | 공격자를 막아도 반응성이 떨어지면 불합격이 된다. 의도한 동작이다 |
| D8 | 없는 방어(G1–G6)는 케이스만 정의 | 지금은 개발용 LAN 서버이고 구현 범위 밖이다. 공개 전 차단 목록으로 남긴다 | 공개 일정이 당겨지면 G1, G2가 바로 선행 작업이 된다 |
| D9 | R2(버퍼 깊이)를 별도 지표로 | Tick당 Input 1개, 따라잡기 없음이라 시계 드리프트로 지연이 최대 267 ms까지 늘 수 있다 | 측정 결과 문제가 있으면 서버 Input 처리 개선이 별도 과제로 생긴다 |
| D10 | 서버와 부하 생성기를 다른 머신에 | 루프백은 지연, 손실, NIC 한계가 없고 CPU를 나눠 쓴다 | 머신이 더 필요하다 |

## 10. 근거

- `Server/src/ProjectH.Server/GameLoop.cs`
  - `WaitUntil`: 남은 시간 2 ms 초과면 Sleep, 이하면 `Thread.Yield`
  - `RunTick` 순서: `DrainControl` → `DrainInput` → `SweepPeers` → `SendStatsReplies` → `Match.Tick`
- `Server/src/ProjectH.Server/Game/Match.cs` `TakeInput`: Tick당 Input 1개
- `Server/src/ProjectH.Server/Game/PlayerInputBuffer.cs`: 용량 `InputBufferPerPlayer`(8). 가득 차면 가장 오래된 것을 버린다
- `Server/src/ProjectH.Server/Net/NetworkListener.cs`: `TryCountInputPacket` 확인 뒤 공유 Input 채널에 넣는다
- `Shared/Runtime/Protocol/ServerPackets.cs`: Snapshot 헤더의 `AckInputSeq`(오프셋 5)
- `Server/src/ProjectH.Bots/BotRunner.cs`, `BotConnection.cs`: 30 Hz 단일 스레드, 연결마다 `NetManager`
- `Docs/LoadTest.md`: 100봇 기준 수치
- `Docs/Networking.md`: 인증 없음에 따른 알려진 위험
- `Docs/specs/2026-10-01-phase10-hardening-design.md` D12: 미룬 보안 항목
