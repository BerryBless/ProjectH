# Server

## 실행

```bash
dotnet run --project Server/src/ProjectH.Server
dotnet test Server/ProjectH.Server.slnx
```

MySQL에 경기 기록을 남기려면 먼저 `docker compose up -d`(개발용 컨테이너, `Database.md`)를 실행한다. DB가 없어도 서버는 정상 동작하고 기록만 남지 않는다. DB 설정은 `appsettings.json`의 `Persistence` 절(`Database.md`).

설정: `Server/src/ProjectH.Server/appsettings.json`의 `Server` 섹션. 명령줄로 덮어쓰기: `-- --Server:Port=7778`. 잘못된 값이면 시작 시 종료된다(`ServerOptions.Validate`, `GameLoop` 생성자에서 예외).

| 키 | 기본값 | 범위 / 의미 |
|---|---|---|
| Port | 7777 | 0–65535 (UDP) |
| MaxPlayers | 16 | 1–100 (Snapshot은 패킷당 90명 한도로 분할: 91명 이상이면 2패킷) |
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
| ReconnectGraceSeconds | 10 | 0–60, 0 = 끔. 경기 중 끊긴 참가자가 캐릭터를 지키는 시간(`Networking.md` "끊기와 재접속") |
| JoinTimeoutSeconds | 5 | 1–60. 연결한 뒤 Join해야 하는 시간. 넘으면 `JoinTimeout`으로 끊는다 |
| InputTimeoutSeconds | 10 | 0 = 끔, 아니면 2–300이고 `InputTimeoutSeconds × 1000 ≥ DisconnectTimeoutMs + 2000`(기본 5000이면 7 이상, 최솟값 500이면 3 이상). Join한 peer가 입력을 보내야 하는 간격. 넘으면 `InputTimeout`으로 끊는다. LiteNetLib Timeout보다 먼저 오면 네트워크 끊김이 서버 끊기로 보여 유예를 잃으므로 이 조건을 둔다 |

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
| `GameLoop` 전용 스레드(Background) | 채널 소비, Timeout 검사, `Match.Tick`, Snapshot 송신, 통계·Health 로그. 멈춰도 프로세스 종료를 막지 않는다(종료의 Join 5초 제한이 의미가 있도록) | `Match`, `_peers` 단독 소유(경기 초기화로 `Match`를 바꾸는 것도 이 스레드) |
| `StallWatchdog` Timer 스레드(Phase 10) | 1초마다 Game Loop의 마지막 Tick 시각을 `Volatile`로 읽기만 한다. Timer 콜백이 겹치면 뒤의 것은 바로 돌아간다(`Interlocked` 플래그, Lock 없음) | `GameLoop.LastTickTimestamp`(읽기), `HealthCounters`(`Interlocked`) |
| `MatchHistoryWriter`(Hosted Service, async, Game Loop 밖) | `MatchHistoryQueue`에서 경기 기록을 읽어 MySQL에 저장(Phase 9). Game Loop는 큐에 넣기만 하고 DB를 기다리지 않는다 | `MatchHistoryQueue`(읽기), `MatchStore`, 카운터(`Interlocked`) |

`NetManager`는 `UnsyncedEvents = true`, `AutoRecycle = true`. 그래서 `NetworkListener`가 받은 데이터는 그 자리에서 값 타입 메시지로 복사한다.

우리 코드는 Lock을 쓰지 않는다. 스레드 간 전달은 `System.Threading.Channels`, 카운터는 `Interlocked`. 따라서 Lock Ordering·Deadlock 대상이 없다.
Lock을 추가하게 되면 이 문서에 순서를 적는다.

Tick 루프: `DrainControl` → `DrainInput` → `SweepPeers`(stale peer 정리 + Join·Input Timeout + Join 거절된 peer 끊기) → `Match.Tick`(맨 앞에서 `ExpireGrace`). Tick이 5 Tick 이상 밀리면 밀린 분을 건너뛴다(`lateTicksSkipped`). Tick 예외는 "예외 복구"를 본다.

`Match.Tick`은 플레이어마다 `MovementSimulation.Step(ref state, input, 1/SimHz, GameMap.Boxes, GameMap.Terrain)`를 호출한다(Shared 지형과 충돌. 규칙은 `Networking.md` "이동 충돌"). 박스 58개(128개 이하) × 100명 × 30 Hz, 지형 높이 조회는 칸 하나라 무시할 수준이고 할당이 없다. 대기 Spawn은 중앙 광장(반지름 12 m) 안의 5 m 원 위다(`GameMapTests`). 경기 시작은 투입 지점을 쓴다(`BattleRoyale.md`).

Snapshot 송신(Phase 8): `Match.SendSnapshots`가 플레이어 목록을 90명씩 나눠 Part마다 버퍼 하나를 만들고, 수신자마다 Ack·수신자 블록만 덮어써 보낸다(100명이면 수신자당 2패킷, 할당 없음). 형식은 `Networking.md` "Snapshot 분할과 양자화".

전투(Phase 3, 규칙은 `Networking.md` "전투"·"인벤토리와 Loot"): `Match.Tick`은 (Phase 5: 경기 흐름 전환 → Zone 진행·피해) → (`DevRespawn`만) 부활 → (`DevRespawn`만) Loot 재생성 → 입력·이동 → 재장전 완료 → 실제 입력의 사용 취소·칸 선택·버리기·줍기·재장전·발사(`HitScan`)·사용 시작 → 사용 완료 → (Phase 5: 종료 판정) → `ServerTick++` → 바뀐 인벤토리 전송 → (Phase 5: 바뀐 `MatchState`·`ZoneState` 전송) → History 기록 → Snapshot 순서다. 전투 코드는 `Game/Combat/`(`WeaponCatalog`, `WeaponDefinition`, `WeaponRules`, `HitScan`, `CombatRules`, `PositionHistory`)에 있고 Game Loop 스레드만 쓴다. `WeaponCatalog`는 시작 후 바뀌지 않는다. 발사 한 번은 박스 58개 + 지형 칸 + 플레이어 수만큼의 slab 교차이고, 전송은 `_sendBuffer` 하나를 재사용하므로 발사 Tick도 할당이 없다(`LagCompensationTests.FiringTick_AllocatesNothing`).

경기 흐름(Phase 5, 규칙은 `BattleRoyale.md`): `Game/Flow/MatchFlow`(상태 기계, 판 번호, 참가자·생존자 수, 피해·부활 허용 여부)와 `Game/Zone/`(`ZoneData`, `SafeZone`: 경기마다 시드로 원을 모두 굴려 두고 `Sample`·`IsOutside`는 고정 배열만 읽는다. 반지름 0인 원은 안이 없다). 둘 다 `Match`가 소유하고 Game Loop 스레드만 쓰며 Lock이 없다. 서버는 빈 채로, 경기 전(`WaitingForPlayers`, 월드 아이템 없음)으로 시작한다. 경기 시작의 `System.Random` 생성(Loot·Zone·투입 각 1개)만 할당이고(판 재시작은 할당이 없다), 진행 중인 경기의 Tick은 Zone 피해가 있어도 할당이 없다(`MatchEliminationTests.MatchTicks_WithZoneDamage_DoNotAllocate`). 경기 전에는 피해가 없고(`MatchFlow.DamageAllowed`), 경기 중 사망은 영구적이며(부활·Loot 재생성은 `DevRespawn`일 때만), 이탈은 탈락으로 처리해 인벤토리를 떨어뜨린다. `Match`의 테스트용 접근자(`Flow`, `Zone`, `MatchStartTick`, `WinnerId`)는 `InternalsVisibleTo`로만 보인다.

인벤토리·Loot(Phase 4): `Game/Items/`(`ItemCatalog`, `LootTable`, `LootSpawner`, `WorldItems`, `Inventory`, `StartingLoadout`, `ItemRules`, `ConsumableRules`)와 `Game/GameData`. 모두 Game Loop 스레드 소유이고 Lock이 없다. `WorldItems`는 256칸 고정 배열이라 선형 탐색(최대 256)이 줍기 한 번의 비용이다. 월드 아이템을 바꾸는 곳은 `Match.SpawnItem`·`RemoveItemAt`·`SetItemAmount` 셋뿐이고, 각자 이벤트 전송을 끝낸 뒤 돌아오므로 `_sendBuffer`를 쓰는 `PacketWriter`가 다른 전송과 겹치지 않는다. 줍기·회복 Tick도 할당이 없다(`PickupDropTests.PickupTick_AllocatesNothing`, `ConsumableTests.UseTicks_AllocateNothing`). `Match`와 `GameLoop` 생성자의 `StartingLoadout`·`LootPoint[]` 인자는 테스트용이고, 운영은 빈손 시작과 Shared `LootPoints`를 쓴다. 아이템은 월드(`SpawnItem`)가 받은 뒤에만 인벤토리에서 빠지고(Drop·교환·사망 Drop), `RefillLootPoints`는 Point에 아이템이 남아 있으면 다시 굴리지 않는다. `Kill`은 재장전과 회복 채널을 직접 취소한다.

## 예외 복구 (Phase 10 D6)

`GameLoop`는 어떤 예외로도 스레드가 죽지 않게 한다. 프로세스는 Game Loop 스레드 하나에 달려 있다.

- 반복 전체를 try/catch로 감싼다. Stats 로그나 대기에서 예외가 나도 스레드는 산다(`loopFailures`로 센다. Stats 주기마다 첫 하나만 로그).
- Tick 예외(`RunTickGuarded`): `tickFailures`로 센다. 로그에는 Tick 번호·경기 상태·판 번호를 넣고, 통계 주기마다 첫 하나만 남긴다(30 Hz로 같은 로그가 쌓이지 않게). 한 번의 실패는 그 Tick을 건너뛸 뿐이다.
- Timeout 검사(`SweepPeers`)에서 난 예외도 한 Tick의 실패로 센다. 경기를 초기화하지 않고 다음 Tick부터 이어 간다(`SweepPeers`는 시작할 때 목록을 비우므로 실패한 Tick의 찌꺼기가 남지 않는다). 실패가 3초 연속되어야 아래 초기화가 된다.
- Tick 예외의 로그 호출도 try/catch로 감싼다(로거나 깨진 경기 상태를 읽다가 던져도 `RunTickGuarded`는 던지지 않는다).
- `SimHz × 3`번 연속 실패(3초)하면 경기를 초기화한다: 모든 peer를 `ServerError`로 끊고 `Match`를 새로 만든다(진행 중인 판은 기록하지 않는다). Client와 봇은 자동으로 다시 접속해서 새 판에 들어온다. `matchResets`로 센다.
- 10분 안에 초기화가 3번이면(3번째로 쳐야 할 때) 코드의 문제로 보고 Critical 로그를 남기고 서버를 끝낸다(`StopApplication`, 종료 코드 1). 이 경우는 초기화가 일어나지 않으므로 `matchResets`로 세지 않는다. 이때는 peer를 먼저 `ServerError`로 끊지 않는다(다시 접속해 올 수 있으므로). 새 연결 요청은 이때부터 `ServerFull`로 거절한다. Game Loop는 Tick을 멈추고 Stop을 기다리며, Stop이 `ServerShutdown`으로 끊는다.
- 경기 기록 sink 예외는 구간마다 첫 하나를 Error 로그로 남긴다(`matchSinkFailures`는 그대로 센다). 초기화가 버리는 `Match`에 아직 로그하지 않은 sink 예외가 있으면 `GameLoop`가 넘겨받아 다음 Stats 때 로그한다.
- `Program`이 `AppDomain.UnhandledException`(Critical 로그, `Console.Error`에도 기록. 로그 큐가 프로세스 종료 전에 비지 않을 수 있어서다)과 `TaskScheduler.UnobservedTaskException`(Error 로그, 관찰한 것으로 처리)을 로그로 남긴다. 처리되지 않은 예외는 런타임이 정한 대로 프로세스를 끝낸다.

## Queue

| Queue | 크기 | 가득 찼을 때 |
|---|---|---|
| Control 채널 | MaxPlayers × 3 | TryWrite 실패 → 해당 peer를 `ServerError`로 끊는다(Critical 로그, `kicks serverError`로 센다. 서버가 끊은 것이라 유예 없음). Disconnected 메시지가 유실되면 stale-peer 정리가 대신 처리 |
| Input 채널 | MaxPlayers × InputBufferPerPlayer | 가장 오래된 입력 폐기(inputDrops) |
| PlayerInputBuffer(플레이어별) | InputBufferPerPlayer(8) | 가장 오래된 입력 폐기(bufferDrops) |
| `MatchHistoryQueue`(Phase 9) | `Persistence:QueueCapacity`(16) | Reject: 기록을 버리고 `Dropped`를 센다. 생산자 Game Loop(경기당 1회), 소비자 `MatchHistoryWriter` 하나 |

Game Loop는 Tick당 입력 메시지를 최대 `MaxInputMessagesPerTick`개만 소비한다. Control 채널은 연결당 이벤트가 최대 3개(Connected, Join, Disconnected)라는 전제로 크기를 정했고, 이 전제는 "Join은 연결당 1회" 규칙(`PeerState.JoinRequested`)이 지킨다. Input 채널은 모든 peer가 공유하므로, Join 전 입력 거절과 peer별 초당 입력 상한(`SimHz * 2`)으로 한 peer가 채널을 독점하지 못하게 한다.

## Lifetime

- Session(`_peers` 항목, `PlayerEntity`): `Connected` 메시지에서 등록, Disconnected 메시지 또는 peer 상태가 Connected가 아니면 제거. `Connected` 메시지는 `OnConnectionRequest`에서 `PeerState` 설정 후 쓴다(`OnPeerConnected`는 `Accept()` 안에서 Tag 설정 전에 호출되기 때문). LiteNetLib DisconnectTimeout이 끊김을 보장한다.
- LiteNetLib는 peer id를 재사용한다. 메시지의 NetPeer 참조가 현재 `_peers`의 peer와 같을 때만 처리한다. 같은 id로 다른 NetPeer가 `Connected`로 들어오면 이전 세션을 먼저 제거하고(`RemovePeer`) 새 peer로 교체한다. 이전 peer의 늦은 Disconnected는 참조 비교로 무시된다.
- `Match.Leave`는 `PlayerDespawned`를 남은 플레이어에게 보낸다.
- 플레이어별 전투 상태는 `PlayerEntity`에 있고 플레이어와 함께 사라진다. 위치 History는 32칸 고정 링이라 늘어나지 않고, Join·부활 때 새로 시작한다. 인벤토리는 칸 3개·탄약 3종 고정 배열이다. 경기 중에 접속을 끊은 참가자의 인벤토리는 남은 사람들에게 사망 Drop처럼 떨어진다(Phase 5 D10). 경기 밖(대기·결과 화면·`DevRespawn`)에서 끊으면 떨어뜨리지 않고 사라진다.
- 경기 상태(`MatchFlow`의 수, `PlayerEntity`의 Participant·Placement·Kills)는 고정 필드다. 판 재시작(`Closing`)이 월드 아이템을 모두 지우고 모두를 Spawn에 살려 두므로 판이 바뀌어도 아무것도 쌓이지 않는다.
- 월드 아이템은 256개가 상한이고(가장 오래된 Drop부터 지움), Spawn Point 타이머는 Point마다 하나씩 고정 배열이다.
- Session 유예(Phase 10): `Match._graced`(유예 중인 캐릭터 목록)는 최대 `MaxPlayers`개다. 항목은 재접속(Resume)했을 때, 유예 시간이 끝났을 때, 죽었을 때, 판이 재시작할 때 제거된다. Resume이 아닌 세 경우는 `graceExpiries`로 센다.
- Join 거절(Phase 10): `Match`가 Join을 거절하면(`MatchFull`, 유예 캐릭터가 자리를 차지한 경우) 그 peer는 Join한 것으로 치지 않는다(`PeerState.JoinRefused`). Join Timeout·Input Timeout 대상이 아니고, `SweepPeers`가 1초(`SimHz` Tick) 뒤 코드 없이(`None`) 끊는다. 바로 끊지 않는 것은 ReliableOrdered `JoinMatchResponse(MatchFull)`가 먼저 나가게 하기 위해서다. Kick으로 세지 않는다.
- 종료(D7): Ctrl+C → Host `StopAsync` → 새 연결 거절 시작(`NetworkListener.BeginStopping`, 이후 요청은 `ServerFull`) → Tick 멈춤(Game Loop 스레드 Join, 5초 제한. 넘으면 Critical 로그를 남기고 진행) → 모든 peer에 `DisconnectAll(ServerShutdown)` → peer가 0이 되거나 최대 1초 기다림 → `NetManager.Stop` → `MatchHistoryWriter`(큐를 닫고 남은 기록을 `ShutdownDrainSeconds` 동안 저장. Writer를 먼저 등록해서 Host가 역순으로 멈추므로 Game Loop 뒤에 멈춘다). Host `ShutdownTimeout`은 90초(`ShutdownDrainSeconds` 최대 60초 + 30초)라 저장 시간 제한이 Host 기본 30초에 잘리지 않는다. `GameServerService.StopAsync`는 이 일을 Thread Pool에서 하고 Host 토큰까지만 기다린다(호출 스레드를 막지 않는다). 진행 중인 판은 끝나지 않았으므로 기록하지 않는다.

## 관측

10초마다 한 줄: `Stats players=… pktIn/s … bytesOut/s … tickMs p50/p95/p99/max … inputDrops bufferDrops badPackets lateTicksSkipped exceptions gc workingSetMB cpu% matchSinkFailures`. `cpu%`는 프로세스 CPU 시간 증가 / (Stats 간격 × 논리 프로세서 수) × 100이다(Phase 7 D10, 코어 하나를 다 쓰면 100 / 코어 수). `matchSinkFailures`는 경기 기록을 만들거나 큐에 넣다가 예외가 난 경기 수(누적)다. 이 예외는 결과 전송 뒤에 잡아서 세기만 하고 Game Loop를 멈추지 않는다(Phase 9). 부하 측정 결과는 `LoadTest.md`.
패킷 단위 로그는 없다. Tick 예외는 통계 주기당 1회만 로그한다.

**Health 줄(Phase 10 D9):** Stats와 같은 간격으로 한 줄 더 남긴다. 성능은 Stats, 상태는 Health로 나눈다. 값은 `peers`·`players`·`graced`·`match`(상태와 판 번호)만 그 시점 값이고, 나머지는 서버 시작부터의 누적이다.

```text
Health peers players graced match=<State>#<Round>
  connections joins resumed graceStarts graceExpiries
  disconnects timeout other
  rejects full badRequest version
  kicks kicked joinTimeout inputTimeout serverError
  badPackets unknownId malformed beforeJoin duplicateJoin inputRate wrongDirection handlerException
  tickFailures loopFailures matchResets stalls
  db saved failed discarded dropped
```

- `peers`: 열린 연결 수. `players`: 경기의 플레이어 수(유예 중 포함). `graced`: 재접속을 기다리는 플레이어 수.
- `joins`·`resumed`: 새 Join과 Resume 수. `graceStarts`: 유예가 시작된 수. `graceExpiries`: Resume 없이 유예가 끝난 수(시간 초과, 유예 중 사망, 판 재시작). 만료마다 DevPlayerId를 넣은 Information 로그가 하나 남는다. 경기 초기화가 버린 유예 캐릭터는 세지 않는다.
- `disconnects timeout`·`other`: 끊긴 연결 수(LiteNetLib 사유가 `Timeout`인지 아닌지).
- `rejects`: 연결 요청 거절(이유별). 종료 중의 거절도 `full`로 센다. `kicks`: 서버가 끊은 수(코드별. `kicked`는 잘못된 패킷 때문에 끊은 수, `serverError`는 경기 초기화와 Control 채널이 가득 차서 끊은 수). 종료(`ServerShutdown`)와 Join 거절 뒤의 끊기는 Kick이 아니라 세지 않는다.
- `badPackets` 7개 항목: 잘못된 패킷(이유별, `Networking.md` "Validation").
- `tickFailures`·`loopFailures`·`matchResets`: 예외 복구 카운터. `stalls`: Watchdog이 센 멈춤.
- `db`: `MatchHistoryWriter`의 `Saved`·`Failed`·`Discarded`와 큐의 `Dropped`(`Database.md`).

**Meter:** 같은 수치를 `System.Diagnostics.Metrics`의 `Meter("ProjectH.Server")`로도 낸다(`ServerMeter`, Observable 계측기만 쓴다. Game Loop에서 기록하지 않고, 읽는 쪽이 부를 때 `HealthCounters`를 읽는다). 새 패키지도 열린 포트도 없다.

```bash
dotnet-counters monitor -n ProjectH.Server --counters ProjectH.Server
```

| 계측기 | 종류 | 태그 |
|---|---|---|
| `projecth.peers`, `projecth.players`, `projecth.graced` | Gauge | |
| `projecth.match_state` | Gauge | 값 = `MatchFlowState`(0 WaitingForPlayers, 1 Starting, 2 Playing, 3 FinalPhase, 4 Finished, 5 Closing) |
| `projecth.connections`, `projecth.joins`, `projecth.resumes`, `projecth.grace_starts`, `projecth.grace_expiries` | Counter | |
| `projecth.disconnects` | Counter | `reason` = `timeout` / `other` |
| `projecth.rejects` | Counter | `reason` = `ServerFull` / `BadRequest` / `VersionMismatch` |
| `projecth.kicks` | Counter | `code` = `Kicked` / `JoinTimeout` / `InputTimeout` / `ServerError`(종료는 Kick이 아니라 `ServerShutdown` 계열이 없다) |
| `projecth.bad_packets` | Counter | `reason` = `BadPacketReason` 7가지 |
| `projecth.tick_failures`, `projecth.loop_failures`, `projecth.match_resets`, `projecth.stalls` | Counter | |
| `projecth.db_records` | Counter | `result` = `saved` / `failed` / `discarded` / `dropped` |

**Stall Watchdog:** `StallWatchdog`가 1초마다 Game Loop의 마지막 Tick 끝 시각을 본다. 2초 넘게 Tick이 없으면 Critical 로그를 한 번 남기고 `stalls`를 센다. Tick이 돌아오면 걸린 시간을 Warning으로 남긴다. Deadlock이나 무한 루프처럼 예외 없이 멈추는 경우를 로그 없이 놓치지 않게 한다. 종료 중과, 일부러 쉬는 경우(서버가 끝나기를 기다리는 동안)는 멈춤으로 보지 않는다.

**로그:** 콘솔 로그에 시각이 붙는다(`appsettings.json`의 `Logging:Console:FormatterOptions:TimestampFormat`, `yyyy-MM-dd HH:mm:ss.fff`). 접속·Join·이탈·재접속·Join/Input Timeout으로 끊기는 연결·유예 만료마다 한 번 Information이다. Kick(잘못된 패킷)은 Warning, 거절된 연결 요청은 Debug다(`Logging:LogLevel`을 Debug로 올려야 보인다).

봇(부하·경기 테스트용 Client)의 실행은 `Bots.md`를 본다. 서버는 봇을 구분하지 않는다.
