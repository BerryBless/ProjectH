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

무기 데이터: `Server/src/ProjectH.Server/weapons.json`(출력 폴더로 복사). 시작 시 `WeaponCatalog.LoadFile`이 읽고 검증한다(무기 1–8개, Id 1–255 중복 없음, 이름 1–16 UTF-8 바이트, damage 1–65535, magazineSize 1–255, fireIntervalSeconds·reloadSeconds > 0이고 Tick으로 바꿔 65535 이하, range > 0, spread·recoil ≥ 0, 모두 유한). 초 값은 `SimHz`로 반올림해 Tick으로 바꾼다(최소 1). 파일이 없거나 틀리면 `GameServerService` 생성자가 `InvalidOperationException`을 던져 서버가 시작하지 않는다.

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

`Match.Tick`은 플레이어마다 `MovementSimulation.Step(ref state, input, 1/SimHz, TestArena.Boxes)`를 호출한다(Shared 지형 박스와 충돌. 규칙은 `Networking.md` "이동 충돌"). 박스 20개 × 50명 × 30 Hz라 비용은 무시할 수준이고 할당이 없다. Spawn은 반경 5 m 원 위이고 아레나는 중앙 반경 7 m를 비워 둔다(`TestArenaTests`).

전투(Phase 3, 규칙은 `Networking.md` "전투"): `Match.Tick`은 부활 → 입력·이동 → 재장전 완료 → 실제 입력의 무기 처리(`WeaponRules`)·발사(`HitScan`) → `ServerTick++` → History 기록 → Snapshot 순서다. 전투 코드는 `Game/Combat/`(`WeaponCatalog`, `WeaponDefinition`, `WeaponRules`, `HitScan`, `CombatRules`, `PositionHistory`)에 있고 Game Loop 스레드만 쓴다. `WeaponCatalog`는 시작 후 바뀌지 않는다. 발사 한 번은 박스 20개 + 플레이어 수만큼의 slab 교차이고, 전송은 `_sendBuffer` 하나를 재사용하므로 발사 Tick도 할당이 없다(`LagCompensationTests.FiringTick_AllocatesNothing`).

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
- 플레이어별 전투 상태는 `PlayerEntity`에 있고 플레이어와 함께 사라진다. 위치 History는 32칸 고정 링이라 늘어나지 않고, Join·부활 때 새로 시작한다. 탄·발사 간격 배열은 슬롯 수(2) 고정이다.
- 종료: Ctrl+C → Host `StopAsync` → `GameLoop.Stop`(Game Loop 스레드 Join) → `NetManager.Stop(true)`.

## 관측

10초마다 한 줄: `Stats players=… pktIn/s … bytesOut/s … tickMs p50/p95/p99/max … inputDrops bufferDrops badPackets lateTicksSkipped exceptions gc workingSetMB`.
패킷 단위 로그는 없다. Tick 예외는 통계 주기당 1회만 로그한다.
