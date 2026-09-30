# Phase 4 Inventory / Loot — 설계 Spec

## Context

Phase 3까지 만든 것은 다음과 같다.

- 서버가 판정하는 Hitscan 전투. 무기 2종이 슬롯 2개에 고정돼 있다.
- Shield와 Health, 사망과 테스트용 부활.

지금은 모든 플레이어가 두 무기와 가득 찬 탄을 가진 채 시작한다. 월드에는 아이템이 없다.

원래 요청서의 Phase 4는 다음을 구현한다.

```text
World Item / Pickup / Inventory / Weapon Swap / Ammo / Healing
```

요청서의 관련 조항:

- §22 Loot: 맵에 Loot Spawn Point를 둔다. 종류는 Weapon, Ammo, Healing, Shield, Utility다. Spawn은 Server가 정하고, Client는 Loot 위치를 정하지 않는다.
- §23 Loot Randomization: 등급별 Loot Table을 둔다(Common, Uncommon, Rare, Epic, Legendary, 명칭은 임시). Spawn Point마다 Loot Table을 쓴다.
- §24 Inventory: Weapon Slot 1–3, Utility, Healing으로 구성한다. 상태는 Server가 소유하고 Client는 UI 표현만 한다.
- §25 Pickup: Client Interact → PickupRequest → Server 거리 검사 → Item 존재 검사 → Inventory 검사 → 확정 → World Item 제거 → Result Broadcast 순서로 처리한다. Client가 임의로 아이템을 얻을 수 없어야 한다.
- §21 Death: 죽으면 Inventory를 떨어뜨린다.

**성공 기준:**

- 아레나 곳곳에 무기, 탄약, 회복, Shield 아이템이 등급별 색으로 놓여 있다.
  - 두 Client에 같은 위치와 같은 아이템이 보인다.
  - 이 배치는 서버가 Loot Table로 정한다.
- 아이템에 다가가 E를 누르면 서버가 거리, 존재, 인벤토리를 검사한다.
  - 통과하면 아이템을 얻는다.
  - 모든 Client에서 그 아이템이 사라진다.
- 무기는 3칸에 담는다. 칸이 다 차 있으면 현재 무기와 바꾸고, 원래 무기는 바닥에 떨어진다.
- 탄약은 종류별로 따로 보유한다. 재장전하면 보유 탄약에서 탄창을 채운다.
- 회복은 사용 시간(Channel) 뒤에 적용된다. 발사, 교체, 버리기를 하면 사용이 취소된다.
  - Medkit은 Health를 회복한다.
  - Shield Cell은 Shield를 채운다.
- G로 현재 무기를 버린다. 죽으면 가진 것을 모두 그 자리에 떨어뜨린다.
- 판정 로직은 Unity 없이 `dotnet test`로 검증한다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 진행 방식 | 추천안으로 전체 진행. 추천 이유는 이 문서에 남긴다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **시작 시 인벤토리는 비어 있다.** 무기 없음, Shield 0, Health 100. 부활해도 빈 상태로 시작한다. Phase 3의 "무기 2개 + Shield 50" 시작은 없앤다. 서버 테스트는 Match에 시작 Loadout을 주입해 쓴다. | 배틀로얄의 기본 흐름(맨손 → Loot → 전투)을 검증하려면 빈손 시작이어야 한다. 테스트용 주입 옵션이 있으면 전투 테스트가 줍기에 의존하지 않는다. | 아레나에서 바로 교전을 시험하려면 먼저 아이템을 주워야 한다. |
| D2 | **아이템 정의는 서버의 `items.json` 하나에 둔다.** 무기는 기존 `weapons.json`에 탄약 종류 필드만 추가한다. Loot Table은 서버의 `loot.json`에 둔다. 셋 다 서버 시작 시 검증하고, 틀리면 시작하지 않는다. Client에는 Join 직후 `ItemCatalog`(Reliable)로 보낸다. | Phase 3의 Weapon Catalog와 같은 원칙이다. 원본은 서버 하나에 두고, Client는 이름과 표시값만 받는다. 요청서 §16·§23의 데이터 분리를 따른다. | 새 파일 두 개가 늘어난다. 검증 코드가 늘지만 테스트로 고정한다. |
| D3 | **무기 3종, 탄약 3종으로 한다.** 기존 Vesper AR(Medium), Kestrel LR(Heavy)에 새 **Wisp SMG**(Light 탄, 피해 12, 초당 15발, 탄창 25, 재장전 1.6초, 사거리 80 m, 자동)를 더한다. 탄약은 Light, Medium, Heavy다. 보유 한도는 종류별로 Light 180, Medium 150, Heavy 30이다. | 칸 3개를 채우고 교체와 탄약 분리를 검증하려면 무기가 3종 필요하다. 요청서 §15의 예시 중 SMG는 Hitscan 그대로 만들 수 있다. Shotgun은 탄 퍼짐 설계가 필요해서 제외한다(Phase 3 D5). | 무기 다양성이 적다. 데이터로 추가하기 쉽다. |
| D4 | **등급은 5단계(Common–Legendary, 임시 명칭)이고 무기 피해 배율로만 쓴다.** 배율은 1.00, 1.05, 1.10, 1.15, 1.20이다. 무기 아이템은 (무기 Id, 등급, 탄창 잔량)을 가진 개별 개체다. 탄약과 회복 아이템에는 등급이 없다. | 요청서 §23의 등급 구조를 만들되 규칙은 하나만 둔다. 배율 규칙 하나면 서버 판정과 테스트가 단순하다. 등급별 색 표시만으로도 Loot의 의미가 드러난다. | 등급이 체감상 약하다. 밸런스 단계에서 조정한다. |
| D5 | **Loot Table은 가중치 목록 두 단계로 뽑는다.** 먼저 Spawn Point의 Table에서 항목(무기, 탄약, 회복, Shield)을 뽑고, 무기가 나오면 등급을 한 번 더 가중치로 뽑는다. 난수는 서버의 시드 고정 `System.Random` 하나로 뽑고, 시드는 설정값이다. | 가중치 목록은 이해하기 쉽고 데이터로 조정하기 쉽다. 시드를 고정하면 테스트에서 결과가 재현된다. 공정성은 서버가 난수를 소유하는 것으로 충분하다. | 복잡한 규칙(보장 드롭 등)은 나중에 추가한다. |
| D6 | **Spawn Point는 `TestArena`와 같은 방식으로 Shared에 둔다.** 코드 상수 `LootPoints`에 좌표와 Table 이름을 둔다. 서버는 시작할 때 모든 Point에서 아이템을 생성한다. 아레나 박스 위나 옆 빈 공간에 두고, 테스트로 박스와 겹치지 않음을 확인한다. | 맵 데이터(박스)와 같은 곳에 있어야 맵을 바꿀 때 함께 고친다. Client는 위치를 서버 이벤트로만 받으므로, Shared 좌표는 Client가 쓰지 않는다. Shared 예외(이동 계산용 지형 데이터)에는 들어가지 않는 맵 데이터라서, `game-core-rules` §4 예외 문구에 "맵 배치 데이터(좌표 상수)"를 추가한다. | Shared 범위가 조금 넓어진다. 규칙 문구로 범위를 제한한다. |
| D7 | **테스트 아레나에서는 Loot가 다시 생긴다.** 줍힌 Point는 30초 뒤 같은 Table로 다시 생성한다. 이 값은 `ServerOptions.LootRespawnSeconds`이고 0이면 끈다. | 한 번의 Play로 줍기와 교전을 반복해 확인하려면 필요하다. 실제 배틀로얄 규칙(재생성 없음)은 Match Flow 단계에서 이 값을 0으로 둔다. | 없음. 설정값이다. |
| D8 | **줍기:** `Interact` 버튼(E)을 누른 Tick에, 서버는 플레이어 발 위치 기준 수평 2.0 m, 수직 2.0 m 안에서 가장 가까운 아이템을 고른다. Client가 아이템 Id를 보내지 않는다. 검사는 대상 선택(존재·거리) → 인벤토리 여유 순서다. 결과 코드는 Ok, NothingInRange(범위 안에 아이템 없음), Full(받을 수 없음) 셋이다. 대상을 서버가 고르므로 "너무 멂"과 "이미 사라짐"은 따로 구분하지 않는다. | 요청서 §25의 흐름 그대로다. 대상 선택까지 서버가 하므로 Client가 멀리 있는 아이템을 지정하는 조작이 원천적으로 불가능하다. Client는 같은 규칙으로 가장 가까운 아이템에 "[E] Pick up …" 안내를 띄운다(안내 문구는 영어다. 내장 폰트에서 한글 표시를 확인하지 않았다). | 아이템이 겹쳐 있으면 원하는 것을 고르기 어렵다. 간격을 두고 배치한다. |
| D9 | **줍기 규칙:** <ul><li>**무기:** 빈 칸이 있으면 첫 빈 칸에 넣는다. 칸이 모두 차 있으면 현재 칸과 바꾸고, 원래 무기는 그 자리에 떨어뜨린다.</li><li>**탄약:** 한도까지만 받고 남은 양은 바닥에 남긴다(수량을 줄인 채로).</li><li>**회복·Shield:** 칸마다 스택 한도(Medkit 3, Shield Cell 6)까지만 받고 나머지는 남긴다.</li><li>**받을 수 없으면:** 줍기가 실패하고 아이템은 그대로 둔다.</li></ul> | 흔한 BR 규칙이라 사용자가 예상하는 대로 동작한다. 부분 줍기로 탄약과 회복이 낭비되지 않는다. | 없음. |
| D10 | **인벤토리 구성**(요청서 §24): <ul><li>무기 칸 3개(무기 Id, 등급, 탄창 잔량)</li><li>현재 칸</li><li>탄약 보유량 3종</li><li>Healing 칸(Medkit 개수)</li><li>Shield 칸(Shield Cell 개수)</li><li>Utility 칸: 자리만 두고 비워 둔다(투척물은 Projectile 단계).</li></ul> | 요청서의 칸 구성을 따른다. Utility 아이템(수류탄 등)은 요청서 §18에서 Projectile로 분류되므로 이번 범위가 아니다. Shield를 Healing과 별도 칸으로 두면 사용 키가 분명하다. | Utility가 빈 칸으로 남는다. |
| D11 | **회복 사용은 Channel 방식이다.** <ul><li>Medkit: 4 키, 3초, Health +50, 최대 100</li><li>Shield Cell: 5 키, 2초, Shield +25, 최대 100</li><li>사용 중 발사, 교체, 버리기, 다른 회복을 누르면 취소된다. 이동은 가능하다.</li><li>이미 최대치면 사용하지 않는다.</li></ul> | 즉시 회복은 교전 중에 너무 강하다. Channel 방식이 장르 표준이다. 이동을 막지 않으면 이동 예측(Phase 0)을 건드리지 않는다. 취소 규칙은 서버 Tick으로 정한다. | 수치는 밸런스 단계에서 조정한다. |
| D12 | **버리기와 사망 Drop:** G 키로 현재 무기를 발 앞 1 m에 떨어뜨린다. 탄창 잔량은 유지한다. 그 지점으로 가는 길이 박스(얇은 벽 포함)에 막혀 있거나 지점이 박스 안이면(높이는 `Min.Y <= y < Max.Y`로 본다) 발밑에 떨어뜨린다. 아이템은 월드가 받은 뒤에만 인벤토리에서 뺀다. 죽으면 모든 무기, 탄약, 회복을 시체 주변에 원형으로 흩뿌려 떨어뜨린다(요청서 §21). 떨어진 아이템은 재생성 대상이 아니다. | 요청서 §21의 "Inventory Drop"이다. 원형으로 흩뿌리면 아이템이 한 점에 겹치지 않아 줍기 대상 선택이 쉽다. | 없음. |
| D13 | **월드 아이템 수에 상한을 둔다(256).** 초과하면 가장 오래 떨어진(Drop 출처) 아이템부터 제거한다. Spawn Point 아이템은 제거 대상이 아니다. | 무한 증가 Collection 방지 규칙이다. 상한이 있어야 전체 목록 패킷 크기도 제한된다. | 매우 많이 떨어뜨리면 오래된 것이 사라진다. |
| D14 | **Protocol v4:** <ul><li>`InputButtons`를 2 B(`ushort`)로 넓히고 `Slot3`, `Interact`, `Drop`, `UseMedkit`, `UseShieldCell`을 추가한다. 입력 명령은 29 B에서 30 B가 된다.</li><li>월드 아이템은 Snapshot에 넣지 않고 Reliable 이벤트로 보낸다: `WorldItems`(Join 시 전체, 분할), `ItemSpawned`, `ItemRemoved`.</li><li>내 인벤토리는 바뀔 때만 `InventoryState`(Reliable, 본인에게만)로 보낸다.</li><li>Snapshot 형식은 그대로다(1167 B).</li></ul> | 아이템은 거의 변하지 않으므로 매 Snapshot에 넣으면 낭비다. 또 Snapshot 한도 여유가 33 B뿐이다(Phase 3 D10). 변화 이벤트만 Reliable로 보내면 트래픽이 작고 순서가 보장된다. 인벤토리는 본인만 알면 된다. | 새로 들어온 Client에 전체 목록 한 번(최대 256개 × 19 B, 분할)이 필요하다. |
| D15 | **Client 표시:** <ul><li>월드 아이템은 등급 색의 작은 큐브, 탄약은 원통, 회복은 구로 그리고 천천히 회전시킨다. 공유 Material과 고정 크기 풀을 쓴다.</li><li>가장 가까운 줍기 대상에 "[E] Pick up 이름" 안내를 띄운다.</li><li>HUD에 무기 칸 3개(선택 강조, 등급 색, 탄창/보유량), 회복 개수, 사용 진행 막대를 보여 준다.</li></ul> | 모델이 없는 프로토타입에서 모양과 색만으로 종류와 등급을 구분할 수 있다. 풀과 공유 Material로 Hot Path 할당을 없앤다. | 겉모습이 단순하다. |
| D16 | **지금 넣지 않는 것:** Utility·투척물, Shotgun·탄 퍼짐, 배낭 용량, 부착물, 아이템 설명 UI, 인벤토리 드래그 UI, Loot 보장 규칙, 무기 교체 지연. | Projectile·밸런스·정식 UI 단계의 범위다. | 없음. |

## 1. 데이터

`weapons.json`의 무기마다 `ammoType`(Light, Medium, Heavy) 필드를 추가하고, Wisp SMG를 새로 넣는다.

`items.json`:

```json
{
  "rarities": [ {"name":"Common","damageMultiplier":1.00}, … 5개 ],
  "ammo": [ {"type":"Light","name":"Light Rounds","pickupAmount":60,"max":180}, … ],
  "consumables": [
    {"id":"Medkit","name":"Medkit","useSeconds":3.0,"heal":50,"shield":0,"maxStack":3},
    {"id":"ShieldCell","name":"Shield Cell","useSeconds":2.0,"heal":0,"shield":25,"maxStack":6}
  ]
}
```

`loot.json`:

```json
{
  "rarityWeights": {"Common":50,"Uncommon":25,"Rare":15,"Epic":7,"Legendary":3},
  "tables": {
    "Floor":  [ {"kind":"Weapon","weight":35}, {"kind":"Ammo","weight":35}, {"kind":"Medkit","weight":15}, {"kind":"ShieldCell","weight":15} ],
    "Tower":  [ {"kind":"Weapon","weight":60}, {"kind":"Ammo","weight":10}, {"kind":"Medkit","weight":15}, {"kind":"ShieldCell","weight":15} ]
  }
}
```

무기가 뽑히면 무기 Id를 균등하게 고르고 등급은 `rarityWeights`로 고른다. 탄창은 가득 찬 상태로 생성한다. 탄약이 뽑히면 종류를 균등하게 고르고 `pickupAmount`만큼 넣는다. 줍는 양은 Light 60, Medium 45, Heavy 10이다(한도 180, 150, 30).

**검증 항목(서버 시작 시):** 이름·Id 중복, 가중치 > 0, 모든 참조 존재, 수치 > 0, 탄약 한도 ≤ 65535. 하나라도 실패하면 시작을 거부한다.

## 2. Server

- **`ItemCatalog`:** 세 파일을 읽고 검증한다. Wire용 배열을 한 번 만든다.
- **`LootSpawner`:** Spawn Point 목록, 시드 난수, 재생성 타이머. 모두 Game Loop 스레드 소유다.
- **`WorldItems`:** 고정 상한 256의 저장소다.
  - Id 발급: `u16`, 재사용 전에 한 바퀴 돈다.
  - 추가, 제거, 수량 변경, 근처 탐색(선형 탐색, 256 이하).
  - 변경될 때마다 이벤트를 보낸다.
- **`Inventory`:** `PlayerEntity` 안에 둔다. 무기 칸 3개, 탄약 3종, 소모품 2종, 현재 칸, 사용 중인 소모품과 끝나는 Tick을 가진다.
  - Phase 3의 `Ammo[]`·`NextFireTick[]`·`WeaponSlot`을 대체한다.
  - 재장전은 보유 탄약에서 탄창으로 옮긴다. 보유량이 0이면 재장전하지 않는다.
- **`Match.Tick` 순서(살아 있는 플레이어의 입력 처리):**
  1. 이동 Step
  2. 사용 취소 판정
  3. 칸 선택
  4. 버리기
  5. 줍기
  6. 재장전
  7. 발사
  8. 사용 시작
  9. 사용 완료 판정
  - 인벤토리가 바뀌면 그 Tick 끝에 `InventoryState`를 한 번 보낸다.
- 발사 피해는 무기 피해에 등급 배율을 곱해 반올림한다.
- 사망하면 인벤토리 전체를 Drop하고 비운다. 부활하면 빈 인벤토리다.
- 여전히 Lock은 없다. 모든 상태를 Game Loop 스레드가 소유한다. 줍기와 사용에 할당이 없어야 한다.

## 3. Protocol v4

| 패킷 | 방향·Delivery | 내용 |
|---|---|---|
| `PlayerInput` | C→S, Unreliable | 명령마다 `Buttons`가 `u16`이 되어 30 B |
| `ItemCatalog` | S→C, Reliable, Join 직후 | 등급(이름, 배율), 탄약(종류, 이름, 한도), 소모품(Id, 이름, 사용 Tick, 회복량, Shield량, 스택) |
| `WeaponCatalog` | S→C, Reliable | 무기마다 `AmmoType byte` 추가 |
| `WorldItems` | S→C, Reliable, Join 시 | 개수 + 개체마다 `ItemId u16`, `Kind byte`, `DefId byte`, `Rarity byte`, `Amount u16`, `Position Vector3` (19 B). 한 패킷 최대 50개, 여러 패킷으로 나눈다. |
| `ItemSpawned` | S→C, Reliable, 전원 | 위와 같은 한 개체. 수량 변경도 이 패킷으로 보낸다(Upsert). |
| `ItemRemoved` | S→C, Reliable, 전원 | `ItemId u16` |
| `InventoryState` | S→C, Reliable, 본인 | 무기 칸 3 × (`DefId`, `Rarity`, `MagAmmo byte`), `CurrentSlot`, 탄약 3 × `u16`, `Medkits`, `ShieldCells`, `UsingKind byte`, `UseRemainingTicks u16` |
| `PickupResult` | S→C, Reliable, 본인 | `Result byte`(Ok, NothingInRange, Full), `ItemId u16`. 실패 시 안내 표시용이다. |

`ProtocolVersion = 4`. 어떤 패킷도 1200 B를 넘지 않는다. 이 조건을 테스트로 고정한다.

## 4. Client

| 영역 | 변경 |
|---|---|
| `NetClient` | 새 패킷을 이벤트로 올린다. |
| `WorldItemViews` (새로) | Id → 뷰 매핑. 고정 풀 256, 공유 Material(등급 색 5개와 종류별 모양), 회전 연출. |
| `PickupPrompt` (새로, 순수 계산 + UI) | 서버와 같은 규칙으로 가장 가까운 대상을 찾아 안내한다. |
| `InventoryHud` (새로) | 칸 3개, 탄창/보유량, 회복 개수, 사용 진행 막대. 값이 바뀔 때만 문자열을 만든다. |
| `WeaponState` | 무기 칸 3개와 서버 `InventoryState`를 기준으로 동작한다. 빈 칸이면 발사 연출을 하지 않는다. |
| `InputReader` | E(줍기), G(버리기), 3(칸), 4(Medkit), 5(Shield Cell). |
| `GameClient` | 연결, 해제, 사망·부활 처리. |

## 5. 하네스

`game-core-rules` §4의 Shared 예외에 "맵 배치 데이터(Loot Spawn Point 좌표 상수)"를 추가하고, `CLAUDE.md` 변경 이력에 한 줄을 남긴다.

## 6. 테스트

**Server(xUnit):**

- 데이터: 세 파일 로드와 검증(정상 파일, 누락 참조, 0 가중치, 중복), 실제 파일이 이 Spec의 값과 같은지.
- Loot: 시드가 같으면 결과가 같다. 가중치 분포를 대략 확인한다(1만 회). 무기 등급이 뽑히는지. Spawn Point가 박스와 겹치지 않고 중앙 반경 7 m 밖이나 박스 위에 있는지.
- 월드 아이템: 상한 256, 오래된 Drop부터 제거하고 Spawn Point 아이템은 남는다. Id를 재사용하지 않는다(한 바퀴 안).
- 줍기:
  - 거리: 2.0 m 경계 안쪽은 되고 바깥은 안 된다.
  - 없는 아이템은 줍지 못한다.
  - 같은 Tick에 두 명이 줍으면 먼저 처리된 한 명만 얻는다.
  - 무기: 빈 칸이 있으면 첫 빈 칸에, 가득 차 있으면 현재 칸과 교환하고 원래 무기를 Drop한다.
  - 탄약: 부분 줍기를 한다.
  - 소모품: 스택 한도를 지킨다.
- 재장전: 보유 탄약에서 옮기고, 보유량이 부족하면 가진 만큼만 채우고, 0이면 불가다.
- 발사: 빈 칸이면 불가다. 등급 배율이 피해에 적용된다.
- 사용:
  - 3초 뒤 +50, 최대 100을 넘지 않는다.
  - 발사·교체·버리기를 하면 취소된다.
  - 최대치면 시작하지 않는다.
  - 죽으면 취소된다.
- 버리기와 사망 Drop: 수량과 탄창이 보존된다. 원형으로 흩뿌린다.
- 재생성: 30초 뒤 다시 생긴다. 0으로 두면 끈다.
- Protocol: 새 패킷 왕복, 잘린 패킷, 최대 크기 ≤ 1200 B, 버전 4.
- 통합(HeadlessClient 2개):
  1. A가 무기 아이템 옆으로 이동해 줍는다. 두 Client 모두 `ItemRemoved`를 받는다.
  2. A가 B를 쏜다. B가 죽는다.
  3. B의 Drop이 두 Client에 `ItemSpawned`로 온다.
- 기존 전투 테스트는 시작 Loadout 주입으로 계속 통과한다.

**Client EditMode:** 줍기 대상 선택(서버 규칙과 동일), 인벤토리 기반 `WeaponState`, HUD 표시 문자열 캐시.

**Unity 수동 확인(사용자):** 두 Client로 아이템 보이기, 줍기, 교환, 버리기, 회복 사용, 사망 Drop, 재생성.

## 7. 범위 밖

D16의 항목과 DB 저장(영속 인벤토리)은 이번에 하지 않는다. DB 저장은 Persistence 단계에서 다룬다.
