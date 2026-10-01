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
| LootSeed | 1 | ≥ 0. Loot 난수 시드. 경기마다 `LootSeed + 판 번호`로 굴린다(같으면 배치가 같다) |
| LootRespawnSeconds | 30 | 0–3600. 다 가져간 Spawn Point를 다시 채우는 시간, 0이면 끔. `DevRespawn`일 때만 쓴다 |
| MinPlayers | 2 | 2–MaxPlayers (1명이면 경기가 첫 Tick에 끝나 판이 무한 반복되므로 2 이상). 이 인원이 모이면 카운트다운 |
| StartCountdownSeconds | 10 | 1–300. 카운트다운(`Starting`) |
| ResultSeconds | 10 | 1–300. 결과 화면(`Finished`) |
| DevRespawn | false | true = Phase 3·4 테스트 아레나(경기 흐름 없음, 피해 항상, 3초 부활, Loot 처음부터·재생성, 경기 패킷 없음). 운영은 false |
| ZoneSeed | 1 | ≥ 0. Zone 중심 난수 시드. 경기마다 `ZoneSeed + 판 번호` |
| SpawnSeed | 1 | ≥ 0. 투입 지점 섞기 시드. 경기마다 `SpawnSeed + 판 번호` |

데이터 파일: `Server/src/ProjectH.Server/weapons.json`, `items.json`, `loot.json`, `zones.json`(출력 폴더로 복사). 시작 시 `GameData.LoadDirectory`가 넷을 읽고 검증한다.
- `weapons.json`(`WeaponCatalog`): 무기 1–8개, Id 1–255·이름 중복 없음, 이름 1–16 UTF-8 바이트, damage 1–65535, magazineSize 1–255, fireIntervalSeconds·reloadSeconds > 0이고 Tick으로 바꿔 65535 이하, range > 0, spread·recoil ≥ 0, ammoType Light·Medium·Heavy, 모두 유한.
- `items.json`(`ItemCatalog`): 등급 정확히 5개(이름 중복 없음, 배율 0 초과 10 이하), 탄약 Light·Medium·Heavy 각 1개(max 1–65535, pickupAmount 1–max), 소모품 Medkit·ShieldCell 각 1개(useSeconds > 0, heal·shield ≥ 0이고 합 > 0, maxStack 1–255). 이름은 1–16 UTF-8 바이트이고 목록 안에서 중복 없음.
- `loot.json`(`LootTable`): rarityWeights에 5개 등급 이름이 모두 있고 가중치 1–1,000,000, 표 1개 이상, 표마다 항목 1개 이상, kind(Weapon, Ammo, Medkit, ShieldCell) 중복 없음, 가중치 1–1,000,000. Shared `LootPoints`가 쓰는 표 이름이 모두 있어야 한다.
- `zones.json`(`ZoneData`): initialCenter [x, z]는 유한하고 ±arenaHalfSize 안, arenaHalfSize·initialRadius는 0 초과 10000 이하, 단계 1–16개, 단계마다 waitSeconds·shrinkSeconds > 0(Tick으로 65535 이하), targetRadius ≥ 0이고 첫 단계는 initialRadius 이하·그 뒤로는 계속 줄어든다, damagePerSecond 0–65535. 마지막 단계는 targetRadius 0이고 damagePerSecond > 0이어야 한다(모든 경기가 끝나도록).
초 값은 `SimHz`로 반올림해 Tick으로 바꾼다(최소 1). 파일이 없거나 틀리면 `GameServerService` 생성자가 `InvalidOperationException`을 던져 서버가 시작하지 않는다.

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

`Match.Tick`은 플레이어마다 `MovementSimulation.Step(ref state, input, 1/SimHz, GameMap.Boxes, GameMap.Terrain)`를 호출한다(Shared 지형과 충돌. 규칙은 `Networking.md` "이동 충돌"). 박스 58개(128개 이하) × 50명 × 30 Hz, 지형 높이 조회는 칸 하나라 무시할 수준이고 할당이 없다. 대기 Spawn은 중앙 광장(반지름 12 m) 안의 5 m 원 위다(`GameMapTests`). 경기 시작은 투입 지점을 쓴다(`BattleRoyale.md`).

전투(Phase 3, 규칙은 `Networking.md` "전투"·"인벤토리와 Loot"): `Match.Tick`은 (Phase 5: 경기 흐름 전환 → Zone 진행·피해) → (`DevRespawn`만) 부활 → (`DevRespawn`만) Loot 재생성 → 입력·이동 → 재장전 완료 → 실제 입력의 사용 취소·칸 선택·버리기·줍기·재장전·발사(`HitScan`)·사용 시작 → 사용 완료 → (Phase 5: 종료 판정) → `ServerTick++` → 바뀐 인벤토리 전송 → (Phase 5: 바뀐 `MatchState`·`ZoneState` 전송) → History 기록 → Snapshot 순서다. 전투 코드는 `Game/Combat/`(`WeaponCatalog`, `WeaponDefinition`, `WeaponRules`, `HitScan`, `CombatRules`, `PositionHistory`)에 있고 Game Loop 스레드만 쓴다. `WeaponCatalog`는 시작 후 바뀌지 않는다. 발사 한 번은 박스 58개 + 지형 칸 + 플레이어 수만큼의 slab 교차이고, 전송은 `_sendBuffer` 하나를 재사용하므로 발사 Tick도 할당이 없다(`LagCompensationTests.FiringTick_AllocatesNothing`).

경기 흐름(Phase 5, 규칙은 `BattleRoyale.md`): `Game/Flow/MatchFlow`(상태 기계, 판 번호, 참가자·생존자 수, 피해·부활 허용 여부)와 `Game/Zone/`(`ZoneData`, `SafeZone`: 경기마다 시드로 원을 모두 굴려 두고 `Sample`·`IsOutside`는 고정 배열만 읽는다. 반지름 0인 원은 안이 없다). 둘 다 `Match`가 소유하고 Game Loop 스레드만 쓰며 Lock이 없다. 서버는 빈 채로, 경기 전(`WaitingForPlayers`, 월드 아이템 없음)으로 시작한다. 경기 시작의 `System.Random` 생성(Loot·Zone·투입 각 1개)만 할당이고(판 재시작은 할당이 없다), 진행 중인 경기의 Tick은 Zone 피해가 있어도 할당이 없다(`MatchEliminationTests.MatchTicks_WithZoneDamage_DoNotAllocate`). 경기 전에는 피해가 없고(`MatchFlow.DamageAllowed`), 경기 중 사망은 영구적이며(부활·Loot 재생성은 `DevRespawn`일 때만), 이탈은 탈락으로 처리해 인벤토리를 떨어뜨린다. `Match`의 테스트용 접근자(`Flow`, `Zone`, `MatchStartTick`, `WinnerId`)는 `InternalsVisibleTo`로만 보인다.

인벤토리·Loot(Phase 4): `Game/Items/`(`ItemCatalog`, `LootTable`, `LootSpawner`, `WorldItems`, `Inventory`, `StartingLoadout`, `ItemRules`, `ConsumableRules`)와 `Game/GameData`. 모두 Game Loop 스레드 소유이고 Lock이 없다. `WorldItems`는 256칸 고정 배열이라 선형 탐색(최대 256)이 줍기 한 번의 비용이다. 월드 아이템을 바꾸는 곳은 `Match.SpawnItem`·`RemoveItemAt`·`SetItemAmount` 셋뿐이고, 각자 이벤트 전송을 끝낸 뒤 돌아오므로 `_sendBuffer`를 쓰는 `PacketWriter`가 다른 전송과 겹치지 않는다. 줍기·회복 Tick도 할당이 없다(`PickupDropTests.PickupTick_AllocatesNothing`, `ConsumableTests.UseTicks_AllocateNothing`). `Match`와 `GameLoop` 생성자의 `StartingLoadout`·`LootPoint[]` 인자는 테스트용이고, 운영은 빈손 시작과 Shared `LootPoints`를 쓴다. 아이템은 월드(`SpawnItem`)가 받은 뒤에만 인벤토리에서 빠지고(Drop·교환·사망 Drop), `RefillLootPoints`는 Point에 아이템이 남아 있으면 다시 굴리지 않는다. `Kill`은 재장전과 회복 채널을 직접 취소한다.

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
- 플레이어별 전투 상태는 `PlayerEntity`에 있고 플레이어와 함께 사라진다. 위치 History는 32칸 고정 링이라 늘어나지 않고, Join·부활 때 새로 시작한다. 인벤토리는 칸 3개·탄약 3종 고정 배열이다. 경기 중에 접속을 끊은 참가자의 인벤토리는 남은 사람들에게 사망 Drop처럼 떨어진다(Phase 5 D10). 경기 밖(대기·결과 화면·`DevRespawn`)에서 끊으면 떨어뜨리지 않고 사라진다.
- 경기 상태(`MatchFlow`의 수, `PlayerEntity`의 Participant·Placement·Kills)는 고정 필드다. 판 재시작(`Closing`)이 월드 아이템을 모두 지우고 모두를 Spawn에 살려 두므로 판이 바뀌어도 아무것도 쌓이지 않는다.
- 월드 아이템은 256개가 상한이고(가장 오래된 Drop부터 지움), Spawn Point 타이머는 Point마다 하나씩 고정 배열이다.
- 종료: Ctrl+C → Host `StopAsync` → `GameLoop.Stop`(Game Loop 스레드 Join) → `NetManager.Stop(true)`.

## 관측

10초마다 한 줄: `Stats players=… pktIn/s … bytesOut/s … tickMs p50/p95/p99/max … inputDrops bufferDrops badPackets lateTicksSkipped exceptions gc workingSetMB`.
패킷 단위 로그는 없다. Tick 예외는 통계 주기당 1회만 로그한다.
