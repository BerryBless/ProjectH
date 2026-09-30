# Phase 3 Combat — 설계 Spec

## Context

Phase 0에서 접속과 이동 동기화를, Phase 1에서 아레나 충돌·어깨 카메라·Client 전용 발사 연출을 만들었다. 발사는 아직 화면 효과뿐이라 아무도 맞지 않는다.

원래 요청서의 Phase 3은 다음을 구현한다.

```text
Weapon / Fire / Hit / Damage / Health / Death / Respawn for testing
완료 조건: Server가 Hit와 Damage를 결정한다.
```

관련 요청 조항은 다음과 같다.

- §15: 초기 무기는 단순화한다. 이름은 오리지널로 짓는다.
- §16: 무기 수치는 코드에 하드코딩하지 않고 데이터로 분리한다. Damage 판정은 Server가 한다.
- §17: 대부분 Hitscan으로 만든다. 흐름은 Client Fire → FireRequest → Server Validation → Server Raycast → Damage → Result Broadcast이고, Client가 보낸 Hit Player ID를 믿지 않는다.
- §19: Lag Compensation은 고정 크기 History로 만든다.
- §20: Damage는 Shield → Health 순서로 깎는다.
- §21: Health가 0 이하면 Server가 사망을 확정한다. 사망하면 이동과 공격이 불가능하다. Down State는 없다.

**성공 기준:**

- 두 Client가 아레나에서 서로를 쏜다.
  - 조준점에 맞으면 맞은 쪽 Shield와 Health가 줄고, 쏜 쪽 조준점에 명중 표시가 뜬다.
  - Health가 0이 되면 사망한다. 이동과 발사가 막히고 캐릭터가 회색으로 쓰러진 모습이 되며, 3초 뒤 Spawn 위치에서 부활한다.
- 서버만 명중을 결정한다. Client가 "누구를 맞혔다"고 보내는 필드가 Protocol에 없다.
- 움직이는 상대도 화면에서 조준점에 걸쳐 있을 때 쏘면 맞는다. Lag Compensation이 이것을 보장한다.
- 탄창, 재장전, 발사 간격, 무기 교체를 서버가 강제한다.
- 판정 로직은 Unity 없이 `dotnet test`로 검증된다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 진행 방식 | 추천안으로 전체 진행. 추천 이유는 이 문서에 남긴다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **발사는 입력 명령에 실어 보낸다.** `InputButtons`에 Fire·Reload·Slot1·Slot2 비트를 추가한다. 서버는 그 입력을 적용하는 Tick에 무기 규칙을 검사한 뒤 발사한다. 별도 FireRequest 패킷은 만들지 않는다. | 입력은 이미 3중 중복 전송, Seq 중복 제거, Tick당 1개 처리 규칙을 따른다. 발사를 여기에 실으면 손실 복구, 순서 보장, 속도 제한을 모두 공짜로 얻는다. 입력보다 빠른 발사도 구조적으로 불가능하다. 요청서의 "FireRequest"는 이 입력 명령이 맡는다. | 입력 패킷이 조금 커진다. 입력과 관계없는 발사(예: 차지 무기)가 생기면 그때 전용 패킷을 추가한다. |
| D2 | **입력 명령에 조준 방향(`AimYaw`, `AimPitch`)과 발사 시점의 화면 시각(`ViewTick`)을 더한다.** 명령 하나가 17 B에서 29 B가 된다. | 서버는 Client 카메라 위치를 모른다. Client가 "눈 위치에서 조준점까지의 방향"을 보내면 서버는 같은 눈 위치에서 같은 방향으로 판정한다. 어깨 카메라 시차가 있어도 조준점이 가리키는 곳을 맞힌다. `ViewTick`은 Lag Compensation(D6)의 기준이 된다. | 없음. 3개를 묶어도 패킷은 약 90 B다. |
| D3 | **Protocol은 v3으로 올린다.** | 입력과 Snapshot 형식이 바뀐다. v2 Client는 접속 단계에서 거절해야 원인이 바로 보인다. | 구 Client는 접속하지 못한다. 의도한 동작이다. |
| D4 | **무기 데이터는 서버의 `weapons.json`에 둔다.** 서버가 시작할 때 읽고 검증해서, 잘못되면 시작하지 않는다. 접속한 Client에는 Join 직후 `WeaponCatalog` 패킷(Reliable)으로 보낸다. | 요청서 §16대로 수치를 데이터로 분리한다. 원본을 서버 하나에 두면 Client와 서버 수치가 어긋날 수 없다. Client는 발사 간격, 탄창, 재장전 시간, 사거리를 HUD와 연출에만 쓴다. Unity가 따로 파일을 읽을 필요가 없다. | Catalog를 받기 전에는 Client가 발사 연출을 할 수 없다. Join 직후 한 번이라 체감되지 않는다. |
| D5 | **무기는 Hitscan 2종이다.** **Vesper AR**(자동, 피해 20, 초당 10발, 탄창 30, 재장전 2.0초, 사거리 150 m)와 **Kestrel LR**(단발, 피해 90, 1.25초당 1발, 탄창 5, 재장전 2.5초, 사거리 300 m). 1·2 키로 교체한다. 탄 퍼짐(Spread)과 반동(Recoil)은 데이터 필드만 두고 0으로 쓴다. | 연사형과 저격형 두 가지면 발사 간격, 탄창, 재장전, 교체를 모두 검증할 수 있다. 무작위 퍼짐을 켜면 Client 궤적과 서버 판정이 어긋나 보인다. 결정적 퍼짐은 무기 밸런스와 함께 설계할 문제다. Projectile(§18)은 요청서대로 필요한 무기가 생길 때 넣는다. | 조작감이 단순하다. 데이터 필드가 있으므로 나중에 코드로 켜기만 하면 된다. |
| D6 | **Lag Compensation을 이번에 넣는다.** 서버는 플레이어별로 32 Tick(약 1초) 위치 History를 고정 크기 링으로 보관한다. 발사를 판정할 때 다른 플레이어를 `ViewTick` 위치로 되감되, 최대 400 ms(12 Tick)까지로 제한한다(SimHz가 높아도 History 링 `Capacity − 1`을 넘지 않는다). | 다른 플레이어는 약 133 ms 과거가 보간되어 보인다(Phase 0). 되감지 않으면 움직이는 상대를 조준점에 정확히 맞춰도 빗나간다. 요청서는 "기본 전투 이후"라고 했지만, 기본 전투가 성립하려면 필요하다. 그래서 구현 순서상 기본 판정 Task 뒤에 둔다. 되감기 상한 덕분에 느린 연결의 이득과 History 크기가 제한된다. 보간 지연(약 133 ms, 4 Tick)과 입력 버퍼 1 Tick이 되감기 창을 먼저 쓰므로, 400 ms여야 왕복 지연 약 200 ms까지 맞힐 수 있다(200 ms 상한이면 왕복 지연 약 35–50 ms만 남아, 100 ms에서 달리는 상대의 중앙을 조준해도 빗나간다). | 벽 뒤로 숨은 직후에 맞는 경우가 최대 약 400 ms 동안 생긴다. 이 장르에서 흔히 받아들이는 비용이다. 왕복 지연이 약 200 ms를 넘으면 빠르게 움직이는 상대를 빗나갈 수 있다. |
| D7 | **판정 대상**은 플레이어 AABB(이동 충돌과 같은 0.7 × 1.8 m)와 아레나 박스다. 광선은 눈 위치(발 + 1.6 m)에서 시작하고 가장 가까운 것에 맞는다. 머리 판정은 없다. 판정 코드는 **Server에만** 둔다. | 이동 충돌과 같은 상자를 쓰면 보이는 캡슐과 판정이 거의 맞는다. Shared 예외(이동 계산)를 넓히지 않도록 Server 전용으로 둔다. Client 연출은 Phase 1의 Unity 물리를 그대로 쓴다. | 캡슐 모서리의 빈 공간도 맞는다. 머리 판정은 캐릭터 모델이 생길 때 추가한다. |
| D8 | **체력 규칙:** Health 100, Shield 50으로 시작한다(테스트용). 피해는 Shield부터 깎고, 남은 만큼 Health를 깎는다. 자기 자신과는 판정하지 않는다. 팀이 없으므로 아군 사격 규칙도 없다. | 요청서 §20의 흐름 그대로다. Shield를 아이템으로 얻는 규칙은 Loot 단계(Phase 4)에서 정한다. | 없음. |
| D9 | **사망**은 서버가 확정한다. 사망하면 이동과 발사 입력을 무시하고(Seq는 계속 확인 응답), 판정 대상에서 뺀다. 3초 뒤 같은 Spawn 위치에서 Health·Shield·탄을 가득 채워 부활한다. 사망과 부활은 Reliable 이벤트(`PlayerDied`, `PlayerRespawned`)로 알린다. | 요청서 §21 그대로다. 테스트용 부활이 있어야 한 번의 Play로 여러 번 교전을 확인할 수 있다. 이벤트를 Reliable로 보내면 Snapshot이 유실돼도 상태가 어긋나지 않는다. 부활은 순간 이동이므로 Client는 예측 상태(위치)와 보간 기록을 새로 시작한다. 예측기는 새로 만들지 않고 Seq를 이어 간다(서버가 이미 소비한 Seq 이하를 버리기 때문). | 부활 위치가 고정이라 같은 자리에서 다시 싸운다. 테스트용이라 괜찮다. |
| D10 | **Snapshot은 두 부분이 커진다.** 엔티티마다 상태 비트 1 B(생존)를 붙여 22 B가 23 B가 된다. 수신자 전용 블록 6 B(Health, Shield, 무기 슬롯, 탄, `ReloadRemainingTicks`)는 기존 Ack처럼 받는 사람마다 덮어쓴다. | 50명일 때 11 + 6 + 23 × 50 = 1167 B로, Phase 0의 한 datagram 한도 1200 B 안에 들어간다. 내 HUD 값은 매 Snapshot으로 확정되므로 Reliable 이벤트가 유실되거나 늦어도 복구된다. | 한도까지 여유가 33 B뿐이다. 필드를 더 넣으려면 그때 압축이나 Delta를 도입한다(Phase 0 제한사항). |
| D11 | **발사 결과 통지:** 서버가 발사를 처리하면 모든 Client에 `ShotFired`(쏜 사람, 시작점, 끝점, Unreliable)를 보내 다른 사람의 궤적도 보이게 한다. 맞힌 사람에게는 `HitConfirmed`(Reliable), 맞은 사람에게는 `DamageTaken`(방향 포함, Reliable)을 보낸다. | 요청서 §17의 "Result Broadcast"다. 궤적은 하나쯤 유실돼도 괜찮아서 Unreliable로 보낸다. 명중과 피격 표시는 놓치면 안 되므로 Reliable로 보낸다. | 초당 발사가 많으면 트래픽이 는다. 10 발/s × 16명 × 약 30 B 정도라 작다. |
| D12 | **Client 예측 범위:** 내 발사 연출(궤적·탄착·탄 감소 표시)은 즉시 보여 주고, 서버 `ShotFired`에서 내 것은 무시한다. 명중, 피해, 사망은 서버 결과만 표시한다. 사망 중에는 이동 예측을 멈춘다. | 발사 반응이 늦으면 조작감이 나쁘다. 반대로 피해를 예측하면 틀렸을 때 되돌려야 한다. 요청서의 "Client는 결과를 최종 결정하지 않는다"를 지킨다. 사망 중 예측을 멈추면 서버와 이동이 어긋나지 않는다. | 서버가 거절한 발사(예: 재장전 중)도 궤적이 잠깐 보일 수 있다. Client도 같은 Catalog로 간격, 탄, 재장전을 흉내 내서 거의 일치한다. |
| D13 | **HUD**는 코드로 만든 UGUI(Legacy `Text`, 내장 폰트)다. Health, Shield, 탄, 무기 이름, 명중 표시, 피격 방향 표시, 사망과 부활 카운트다운을 보여 준다. 문자열은 값이 바뀔 때만 만든다. | TextMeshPro는 Essentials import가 필요해서, Scene·Asset 편집 없이 코드로 구성하는 규칙과 맞지 않는다. 매 프레임 문자열을 만들면 GC가 생긴다. | 글자 품질이 낮다. 정식 UI 단계에서 교체한다. |
| D14 | **입력 검증**을 추가한다. 조준 각이 유한하지 않으면 발사를 무시하고 Pitch는 ±89°로 제한한다. `ViewTick`은 되감기 범위로 제한한다. 슬롯 비트가 둘 다 켜져 있으면 무시한다. 탄이 없거나, 재장전 중이거나, 발사 간격 전이거나, 사망 상태면 발사하지 않는다. | 외부 입력 검증은 항상 적용 규칙이다. 발사 간격을 서버 Tick으로 강제하면 매크로나 조작된 Client도 데이터 이상으로 쏠 수 없다. | 없음. |
| D15 | **지금 넣지 않는 것:** Projectile, Headshot, 탄 퍼짐·반동 적용, Knockdown, 인벤토리 Drop, 생존자 수, 탄약 줍기, 킬로그 UI. | 모두 Phase 4(Loot)·Phase 5(BR 규칙)의 범위이거나 캐릭터 모델이 있어야 의미가 있다. | 없음. |

## 1. Protocol v3

| 변경 | 내용 |
|---|---|
| `InputButtons` | 기존 비트에 `Fire = 4`, `Reload = 8`, `Slot1 = 16`, `Slot2 = 32`를 추가한다. |
| `InputCommand` | `AimYaw float`, `AimPitch float`, `ViewTick float`를 추가해 29 B가 된다. |
| `WeaponCatalog` (새로, S→C, Reliable) | 무기 수, 그리고 무기마다 `WeaponId byte`, `Name string ≤ 16 B`, `Damage u16`, `FireIntervalTicks u16`, `MagazineSize byte`, `ReloadTicks u16`, `Range float`, `Automatic byte` |
| `WorldSnapshotHeader` | 뒤에 수신자 전용 블록 6 B를 붙인다: `Health byte`, `Shield byte`, `WeaponSlot byte`, `Ammo byte`, `ReloadRemainingTicks u16`. Ack와 같은 방식으로 받는 사람마다 덮어쓴다. |
| `SnapshotEntity` | `Flags byte`(bit0 = Alive)를 추가해 23 B가 된다. |
| `ShotFired` (새로, S→C, Unreliable) | `ShooterId u16`, `Start Vector3`, `End Vector3` |
| `HitConfirmed` (새로, S→C, Reliable) | `TargetId u16`, `Damage u16`, `Killed byte` |
| `DamageTaken` (새로, S→C, Reliable) | `AttackerId u16`, `Damage u16`, `FromDirection Vector3` |
| `PlayerDied` (새로, S→C, Reliable, 전원) | `VictimId u16`, `KillerId u16` |
| `PlayerRespawned` (새로, S→C, Reliable, 전원) | `EntityId u16`, `Position Vector3`, `Yaw float` |

패킷 크기 한도: Snapshot은 50명일 때 1167 B ≤ 1200 B여야 한다. 이 조건을 테스트로 고정한다.

## 2. Server

- **`WeaponCatalog`:** `weapons.json`을 읽고 검증한다. 무기는 1–8개, 이름은 16 B 이하, 모든 수치가 양수이고 유한해야 한다. 초 단위 값은 SimHz로 Tick으로 바꾼다. 실패하면 서버가 시작을 거부한다(`ServerOptions.Validate`와 같은 방식).
- **`PlayerEntity`에 추가하는 상태:** Health, Shield, Alive, RespawnAtTick, WeaponSlot, 슬롯별 Ammo, NextFireTick, ReloadEndTick, 위치 History 링(32).
- **`Match.Tick` 순서:**
  1. 부활 시각이 된 플레이어를 부활시킨다.
  2. 플레이어마다 입력을 하나 꺼낸다(Phase 0 규칙).
     - 살아 있으면 이동 Step을 실행하고 무기 입력(교체 → 재장전 → 발사)을 처리한다.
     - 죽어 있으면 입력의 Seq만 확인 응답하고 무시한다.
  3. 모든 이동이 끝난 뒤 이번 Tick 위치를 History에 기록한다.
  4. Snapshot을 보낸다.
- **발사 처리 (`CombatResolver`, Server 전용):**
  1. 입력을 검증한다(D14).
  2. 눈 위치에서 조준 방향으로 광선을 만든다.
  3. 다른 생존 플레이어를 `ViewTick` 위치로 되감는다. 되감은 위치는 History에서 두 Tick 사이를 보간하며, 되감기 상한은 12 Tick(400 ms)이다.
  4. 가장 가까운 박스와 플레이어 교차점을 구한다.
  5. 결과를 적용한다: Shield → Health 순서로 피해, 사망이면 사망 처리.
  6. 알림을 보낸다: `ShotFired`, `HitConfirmed`, `DamageTaken`, `PlayerDied`.
  7. 탄을 줄이고 NextFireTick을 정한다. 탄이 0이 되면 자동으로 재장전한다.
- 광선과 AABB의 교차는 slab 방식으로 계산한다. 할당하지 않고, 무한 값이 나오지 않게 방어한다.
- 기존 스레드 구조는 그대로다. 모든 전투 상태는 Game Loop 스레드만 만진다. Lock은 쓰지 않는다.

## 3. Client

| 파일 | 변경 |
|---|---|
| `Net/NetClient.cs` | 새 패킷 6종을 읽어 이벤트로 올리고, Snapshot의 수신자 블록을 읽는다. |
| `Game/WeaponState.cs` (새로) | Catalog 기준으로 로컬 발사 간격, 탄, 재장전을 흉내 낸다(연출용, D12). 서버 Snapshot 값이 오면 그 값으로 맞춘다. |
| `Game/LocalFireEffects.cs` | 고정 10 발/s 대신 현재 무기의 간격과 자동/단발 여부를 쓴다. 다른 플레이어 궤적(`ShotFired`)도 같은 풀로 그린다. |
| `Game/AimSolver.cs` (새로, 순수 계산) | 카메라 조준점과 눈 위치로 `AimYaw`와 `AimPitch`를 계산한다. |
| `Game/LocalPlayerPredictor.cs` | 입력 명령에 조준, `ViewTick`, 버튼을 담는다. 사망 중에는 이동을 0으로 예측하고, 부활하면 상태만 초기화한다(Seq 유지). |
| `Game/RemotePlayers.cs`, `Game/PlayerViewFactory.cs` | 생존 여부에 따라 뷰를 바꾼다(사망 시 회색, 눕힘). 부활하면 보간 기록을 비운다. 클라이언트 조준이 원격 플레이어 몸에 닿도록 원격 뷰에 서버 판정 상자(0.7 × 1.8 × 0.7)와 같은 크기의 Collider를 `Ignore Raycast` 레이어(2)에 둔다. 살아 있는 뷰는 회전하지 않는다. |
| `Game/CombatHud.cs` (새로) | D13 |
| `Input/InputReader.cs` | R(재장전), 1·2(무기 교체)를 추가한다. |
| `Game/GameClient.cs` | 위 구성 요소를 연결하고, 부활 시 예측기 상태만 초기화한다(Seq 유지). |

`ViewTick`은 발사한 프레임에 원격 플레이어를 그리는 데 쓴 `ServerClock` 렌더 Tick이다(Phase 0의 보간 시각).

## 4. 하네스

변경 없음. 판정 코드는 Server에만 있어 Shared 예외는 그대로다.

## 5. 테스트

**Server (xUnit):**
- `WeaponCatalog` 로드와 검증: 정상 파일, 빈 파일, 음수 값, 긴 이름, 중복 Id.
- 광선–AABB 교차: 정면, 비껴감, 내부 시작, 평행, 무한 방향.
- 판정:
  - 벽이 가로막으면 빗나간다.
  - 두 플레이어가 한 줄에 서 있으면 가까운 쪽이 맞는다.
  - 사거리 밖이면 빗나간다.
  - 자기 자신은 맞지 않는다.
  - 죽은 플레이어는 맞지 않는다.
- 피해: Shield만, Shield와 Health, 정확히 0, 초과 피해.
- 발사 규칙: 간격 전 발사 무시, 탄 0이면 자동 재장전, 재장전 중 무시, 교체, 단발 무기의 버튼 유지.
- 사망과 부활: 이동과 발사 무시, Seq 확인 응답은 계속, 3초 뒤 가득 채워 Spawn 위치로.
- Lag Compensation:
  - 움직이는 대상을 `ViewTick` 위치로 쏘면 맞는다.
  - 상한을 넘는 `ViewTick`은 잘린다.
  - History가 부족하면 가장 오래된 기록을 쓴다.
- Protocol: 새 패킷 왕복, 잘린 입력, Snapshot 50명 ≤ 1200 B.
- 통합(HeadlessClient 2개): A가 B를 조준해 쏜다 → B가 `DamageTaken`을 받고 Snapshot 수신자 블록의 Health가 준다 → A가 `HitConfirmed`를 받는다 → B 사망(Vesper 피해 20이면 Shield 50 + Health 100 = 8발. 통합 테스트는 피해 30짜리 테스트 Catalog를 써서 5발), 두 Client 모두 `PlayerDied`를 받는다 → 3초 뒤 `PlayerRespawned`.

**Client EditMode:**
- `AimSolver` 각도 계산.
- `WeaponState`의 간격, 탄, 재장전, 서버 값 동기화.
- Predictor가 사망 중 정지하고 부활 시 초기화되는지.

**Unity 수동 확인 (사용자):**
- 두 Client로 서로 사격하고, 명중 표시와 피격 표시가 뜨는지.
- 사망하면 회색으로 쓰러지고 3초 뒤 부활하는지.
- 무기 교체, 재장전, HUD 값.
- 움직이는 상대를 맞힐 수 있는지.

## 6. 범위 밖

D15의 항목, 그리고 Anti-cheat 통계와 서버 권한 Hit 기록 로그. 이것들은 Hardening 단계에서 다룬다.
