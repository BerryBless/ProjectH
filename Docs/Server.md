# Server

## 실행

```bash
dotnet run --project Server/src/ProjectH.Server
dotnet test Server/ProjectH.Server.slnx
```

설정: `Server/src/ProjectH.Server/appsettings.json`의 `Server` 섹션. 명령줄로 덮어쓰기: `-- --Server:Port=7778`. 잘못된 값이면 시작 시 종료된다(`ServerOptions.Validate`, `GameLoop` 생성자에서 예외).

| 키 | 기본값 | 범위 / 의미 |
|---|---|---|
| Port | 7777 | 0–65535 (UDP) |
| MaxPlayers | 16 | 1–50 (Snapshot 1200B 한도) |
| SimHz | 30 | 10–128 |
| SnapshotEveryTicks | 2 | 1–SimHz, SimHz의 약수 |
| InputBufferPerPlayer | 8 | 2–64 |
| MaxInputMessagesPerTick | 512 | ≥ 1, Tick당 소비하는 입력 메시지 수 |
| BadPacketDisconnectThreshold | 20 | ≥ 1 |
| DisconnectTimeoutMs | 5000 | ≥ 500 |
| StatsIntervalSeconds | 10 | ≥ 1 |

파생값: `SnapshotHz = SimHz / SnapshotEveryTicks`, `MaxInputPacketsPerSecond = SimHz * 2`(peer별 입력 상한).

## 스레드와 소유권

| 스레드 | 하는 일 | 접근하는 상태 |
|---|---|---|
| LiteNetLib 스레드 | `NetworkListener`: 연결 요청 검사, 패킷 검증·파싱 | `InboundChannels`(쓰기), `PeerState` |
| `GameLoop` 전용 스레드 | 채널 소비, `Match.Tick`, Snapshot 송신, 통계 로그 | `Match`, `_peers` 단독 소유 |

`NetManager`는 `UnsyncedEvents = true`, `AutoRecycle = true`. 그래서 `NetworkListener`가 받은 데이터는 그 자리에서 값 타입 메시지로 복사한다.

우리 코드는 Lock을 쓰지 않는다. 스레드 간 전달은 `System.Threading.Channels`, 카운터는 `Interlocked`. 따라서 Lock Ordering·Deadlock 대상이 없다.
Lock을 추가하게 되면 이 문서에 순서를 적는다.

Tick 루프: `DrainControl` → `DrainInput` → `RemoveStalePeers` → `Match.Tick`. Tick이 5 Tick 이상 밀리면 밀린 분을 건너뛴다(`lateTicksSkipped`). Tick 예외는 삼키고 계속 진행한다.

## Queue

| Queue | 크기 | 가득 찼을 때 |
|---|---|---|
| Control 채널 | MaxPlayers × 3 | TryWrite 실패 → 해당 peer Disconnect(Critical 로그). Disconnected 메시지가 유실되면 stale-peer 정리가 대신 처리 |
| Input 채널 | MaxPlayers × InputBufferPerPlayer | 가장 오래된 입력 폐기(inputDrops) |
| PlayerInputBuffer(플레이어별) | InputBufferPerPlayer(8) | 가장 오래된 입력 폐기(bufferDrops) |

Game Loop는 Tick당 입력 메시지를 최대 `MaxInputMessagesPerTick`개만 소비한다. Control 채널은 연결당 이벤트가 최대 3개(Connected, Join, Disconnected)라는 전제로 크기를 정했고, 이 전제는 "Join은 연결당 1회" 규칙(`PeerState.JoinRequested`)이 지킨다. Input 채널은 모든 peer가 공유하므로, Join 전 입력 거절과 peer별 초당 입력 상한(`SimHz * 2`)으로 한 peer가 채널을 독점하지 못하게 한다.

## Lifetime

- Session(`_peers` 항목, `PlayerEntity`): `Connected` 메시지에서 등록, Disconnected 메시지 또는 peer 상태가 Connected가 아니면 제거. `Connected` 메시지는 `OnConnectionRequest`에서 `PeerState` 설정 후 쓴다(`OnPeerConnected`는 `Accept()` 안에서 Tag 설정 전에 호출되기 때문). LiteNetLib DisconnectTimeout이 끊김을 보장한다.
- LiteNetLib는 peer id를 재사용한다. 메시지의 NetPeer 참조가 현재 `_peers`의 peer와 같을 때만 처리한다. 같은 id로 다른 NetPeer가 `Connected`로 들어오면 이전 세션을 먼저 제거하고(`RemovePeer`) 새 peer로 교체한다. 이전 peer의 늦은 Disconnected는 참조 비교로 무시된다.
- `Match.Leave`는 `PlayerDespawned`를 남은 플레이어에게 보낸다.
- 종료: Ctrl+C → Host `StopAsync` → `GameLoop.Stop`(Game Loop 스레드 Join) → `NetManager.Stop(true)`.

## 관측

10초마다 한 줄: `Stats players=… pktIn/s … bytesOut/s … tickMs p50/p95/p99/max … inputDrops bufferDrops badPackets lateTicksSkipped exceptions gc workingSetMB`.
패킷 단위 로그는 없다. Tick 예외는 통계 주기당 1회만 로그한다.
