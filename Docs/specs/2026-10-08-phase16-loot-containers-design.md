# Phase 16 Loot Chest / Ammo Box / Supply Drop — 설계 Spec

## Context

요청서는 `Docs/requests/2026-10-05-roadmap-phase13_5-19-request.md`의 STEP 6(§59–§67)이다. 목표는 지금의 바닥 Loot에 Battle Royale식 Loot Container(Chest, Ammo Box)와 경기 중 Supply Drop을 더하는 것이다.

**지금 구조에서 확인한 사실(`phase15-map` 2b8eee1 기준):**

- **바닥 Loot:** Shared `LootPoints`(약 50개, 표 이름 Floor·Building·Tower)를 서버만 읽는다. `loot.json`은 등급 가중치 하나와 표마다 `{kind, weight}`이고, 종류는 Weapon·Ammo·Medkit·ShieldCell뿐이다(건설 자원 없음, 표별 등급 없음). `LootSpawner`가 `LootSeed + Round` 시드로 경기 시작에 모든 지점을 채운다(경기 중 다시 채우기 없음).
- **월드 아이템:** `WorldItems` 고정 256칸. 가득 차면 떨어진 아이템 중 가장 오래된 것을 밀어낸다(Loot 지점 아이템·카드는 밀지 않는다). `SpawnItem`이 `ItemSpawned`를 모두에게 보낸다. `ItemKind.Material`(건설 자원)은 이미 있다(닿으면 자동 줍기).
- **상호작용:** E 누름 순서는 소생·재투입 대상 → 문 → 줍기다. 문은 `DoorRules.FindTarget`(수평 2.5 m, 조준 ±60°, 높이 범위, 순수 함수)로 고르고 Client에 같은 규칙 복사본이 있다. 행동은 `ActionsAllowed`일 때만 한다.
- **고정 물체 복제:** 문(`DoorStates` byte), 채집 대상(`HarvestStates` u64), Reboot Station 모두 "마스크 패킷을 바뀐 Tick 끝 + Join·Resume에" 방식이다. 경기 시작에 초기화한다.
- **Reboot Station**은 충돌체가 아닌 고정 목록(Shared, 좌표 상수 + 배치 테스트)이다.
- **자기장:** `SafeZone.Start`가 경기 시작에 모든 원을 미리 정한다. 서버는 다음 원의 중심·반지름을 언제나 안다. 자기장 시계는 수송기 경로가 끝날 때 시작한다(전체 약 285초).
- **떨어지는 물체:** 서버 물리 없음. 수송기는 한 번 보낸 값으로 양쪽이 위치를 계산한다(`DropRoute`) — Supply Drop의 본보기다.
- **Client:** 월드 아이템은 공유 Mesh 풀(256, 충돌체 없음), 스테이션은 충돌체 없는 원기둥, 지도 아이콘은 `MapHud` 고정 풀.
- **QA:** `spawnLoot` 명령, `match.worldItems`(개수만). 월드 아이템 목록 조회는 없다.
- **Protocol:** v14, 마지막 PacketId 41.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 범위 | 요청서 STEP 6(§59–§67) |
| 진행 방식 | 사용자가 "다음 페이즈"라고 했다. 추천안으로 진행하고 이유는 이 문서에 남긴다(메모리 규칙). Commit은 하고 Push는 "푸시" 때만 한다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **Container 목록: Shared `LootContainers.All`(맵 배치 상수).** 항목 = 종류(Chest 0, AmmoBox 1), 위치(지형 높이 또는 상자 윗면), 방향(Yaw 4방향). Chest 약 20개, Ammo Box 약 14개(합 ≤ 64, u64 마스크). Id = 목록 위치(순서를 바꾸지 않는다). **충돌체가 아니다**(Reboot Station과 같다): 이동·사격 충돌(`CollisionWorld`, 예측 공용)을 바꾸지 않는다. 배치 테스트: 맵 상자·문·채집 대상·Loot 지점·스테이션과 겹치지 않음, 걸어서 닿음, 광장 규칙. | game-core-rules §4 예외 2와 같은 맵 배치 상수다. 작은 상자를 충돌체로 만들면 예측 공용 충돌 수집과 사격 차단을 모두 고쳐야 하는데 얻는 것이 적다. | 충돌이 필요해지면 `CollisionWorld`에 종류를 하나 더한다. |
| D2 | **경기 시작에 생성과 Loot를 정한다(§59).** `StartMatch`에서 **바닥 Loot와 따로 둔 시드 흐름**(Container는 `LootSeed + Round`에 상수를 섞은 시드, Supply Drop은 또 다른 상수)으로 Container마다 생성 여부(Chest 70 %, Ammo Box 80 %, `loot.json` 값)와 Loot 목록을 미리 굴려 서버에 둔다(Container마다 최대 4칸 고정 배열). 열기 전에는 아무것도 보내지 않는다(Loot 정보 노출 없음). 개발 모드는 서버 시작 때 같은 방식으로 정하고 다시 채우지 않는다. | 요청서 §59 그대로. 시드로 재현된다(QA). 바닥 Loot의 `Random`을 같이 쓰면 기존 바닥 Loot 결과가 모두 바뀌어 스트레스 기준선과 시드 시나리오가 움직인다. **회귀 기준:** Container를 열지 않으면 바닥 Loot는 지금과 바이트 단위로 같다. | 없음. |
| D3 | **상태(§62): 생성 마스크 + 열림 마스크(u64 두 개).** 패킷 `ContainerStates`(42, S→C, 17 B)를 바뀐 Tick 끝에 모두에게, Join·Resume에 보낸다. 상태는 없음 / 닫힘 / 열림 세 가지뿐이다. 열린 Container는 그 경기에서 다시 생기지 않는다. `CloseRound`·`StartMatch`가 두 마스크와 Supply Drop 목록을 지운다. | 문·채집 대상과 같은 방식이다. | 없음. |
| D4 | **열기(§60).** E 누름 순서: 소생·재투입 대상 → 문 → **Container**(Chest·Ammo Box·착지한 Supply Drop 중 가장 가까운 것) → 줍기. 대상 고르기는 **서버** 순수 함수 `ContainerRules.FindTarget`(문과 같은 수평 2.5 m·조준 ±60°·높이 범위, `DoorRules`처럼 Shared에 두지 않는다)이고, Client에 같은 규칙 복사본(안내 문구용, 정적 목록 + 받은 Supply Drop 목록의 착지한 것)과 일치 테스트를 둔다. 문과 Container가 함께 범위에 들면 **더 가까운 쪽**을 고른다(문 근처 Chest가 열리지 않는 일을 막는다. 같은 거리면 문). 배치 테스트도 Container 중심과 문 중심을 `DoorInteractRange` + 1 m 넘게 띄운다. 서버 검증: **경기 중(`InMatch`) 또는 개발 모드**, Container가 생성됨, 아직 닫힘, 살아 있고 행동 가능(기절 불가), 거리·각도, 시선(눈 → Container 중심이 맵 상자·닫힌 문·지형·건설 조각에 막히지 않음). 열기는 즉시다(진행 시간 없음). 시선이 막힌 Container가 대상이면 그 E 누름은 아무것도 하지 않는다(문·줍기로 넘어가지 않는다. Client 안내·예측이 시선을 모르므로 결과를 안내와 맞춘다, 리뷰 반영). 같은 Tick에 두 사람이 누르면 먼저 처리된 한 명만 연다(플레이어 처리 순서). 결과는 `ContainerStates`와 `ItemSpawned`로 알 수 있으므로 열기 전용 응답 패킷은 없다. | 요청서 §60 목록 그대로. 문 규칙을 다시 쓴다. 즉시 열기가 단순하고, 빠른 루팅 흐름에 맞다. | 열기 시간이 필요하면 소생과 같은 진행(Channel)을 쓴다. |
| D5 | **Loot Table(§61).** `loot.json`을 넓힌다: 새 종류 `Material`(건설 자원, 재료·양), 표마다 선택적 `rarityWeights`(없으면 전역 값), 새 표 `Chest`, `AmmoBox`, `SupplyDrop`, 표마다 굴림 수(`rolls`)와 반드시 나오는 종류(`guaranteed`).<ul><li>Chest: 3개, 무기 1개 보장 + 탄약·회복·실드·자원(나무·돌·금속 30) 중 2개</li><li>Ammo Box: 2개, 탄약 + 자원 10</li><li>Supply Drop: 4개, 무기 1개 보장, 등급은 Epic·Legendary만, + 실드·회복·탄약</li></ul>Loot는 열 때 Container 둘레 1 m 원 위에 월드 아이템으로 놓는다(`DropAround`). 이 아이템은 떨어진 아이템과 같은 규칙(가득 차면 오래된 것부터 밀림)이다. | 요청서 §61 예시 그대로. 기존 Loot 굴림·월드 아이템·줍기를 다시 쓴다(새 인벤토리 경로 없음). | 표 수치는 JSON만 바꾼다. |
| D6 | **Supply Drop(§64, §65).** 일정은 `loot.json` `supplyDrops`: 자기장 시계 기준 60초, 150초에 하나씩(경기당 최대 4개).<ul><li>위치: 서버가 아는 **다음 자기장 원** 안(반지름 × 0.6 안)에서 시드로 후보를 최대 16개 고른다. "다음 원"은 그 순간 줄어들고 있는(또는 기다리는) Phase의 목표 원이다: `SafeZone.CenterX/CenterZ/Radius(Phase)`(Phase 0이면 원 1). 마지막 Phase처럼 반지름이 0이면 중심이다. 후보와 결과는 경기장(±60 m) 안으로 자른다. 조건: 맵 상자 발자국·문·채집 대상·Container·스테이션·Loot 지점과 **수평(XZ)으로** 2 m 떨어짐(지붕 아래 지형에 떨어져 지붕을 뚫는 모습을 막는다), 살아 있는 플레이어와 수평 15 m 떨어짐(§65 "플레이어 바로 위 금지")을 만족하는 첫 후보. 없으면 같은 후보를 플레이어 15 m 조건만 빼고(바로 옆 3 m는 계속 피함) 다시 보고, 그래도 없으면 다음 원 중심을 같은 장애물 조건으로 본다. 그것도 실패하면 그 Tick에는 생성하지 않고 다음 Tick에 다시 시도한다(중심이 상자 안이면 열 수 없는 Supply Drop이 되는 것을 막는다, 리뷰 반영).</li><li>낙하: 착지 높이 + 60 m에서 시작해 초속 4 m로 내려온다(15초). 위치는 (시작 Tick, 착지 Tick, x·z, 착지 높이)로 양쪽이 계산한다(서버 물리 없음, 수송기와 같은 방식).</li><li>건설 조각은 보지 않는다. Supply Drop은 플레이어 건물을 지나 지형 높이에 착지한다(결정이다).</li><li>생성·착지·열기는 경기 중(`InMatch`)에만 일어난다. 결과 화면(Finished)에서는 멈춘다(Phase 14 리뷰 Medium과 같은 종류의 버그를 막는다).</li><li>착지 전에는 열 수 없다. 착지 뒤에는 D4 규칙으로 연다. Loot는 열 때 Supply Drop 표로 굴린다(생성 때 미리 정함, D2와 같은 시드 흐름).</li><li>경기가 끝나면 지운다.</li></ul> | 요청서 §64, §65 그대로. 다음 원 안이라 플레이어가 그쪽으로 모인다. 계산식 공유로 Tick마다 보낼 것이 없다(§66). | 일정·높이·속도는 JSON만 바꾼다. |
| D7 | **Supply Drop 복제(§66): `SupplyDrops` 패킷(43, S→C).** 경기의 Supply Drop 전체(최대 4개 × [id u8, 상태 u8(낙하·착지·열림), x·z·착지 높이 float, 시작 Tick u32, 착지 Tick u32] 22 B)를 생성·착지·열림 때 모두에게, Join·Resume에 보낸다. Snapshot에는 싣지 않는다. Client는 받은 목록으로 통째로 바꾼다. | 작은 전체 상태라 사건 단위보다 단순하다(Phase 15 `TeamMarkers`와 같다). | 없음. |
| D8 | **Client 표시.** Chest: 갈색 상자(뚜껑 금색 띠), 열리면 뚜껑을 젖힌 모양·어둡게. Ammo Box: 작은 녹색 상자. Supply Drop: 파란 상자 + 떨어지는 동안 낙하산(원뿔), 착지 뒤 위로 빛기둥, 열리면 회색. 모두 공유 Mesh·Material, 고정 풀(Container 64, Supply Drop 4), 충돌체 없음. 가까우면 "[E] 열기" 안내(Client 규칙 복사본). 지도·미니맵에 Supply Drop 아이콘(낙하·착지·열림 색). | 저사양: 생성 한 번, 바뀔 때만 갱신. | 없음. |
| D9 | **Protocol v15.** `ContainerStates` 42, `SupplyDrops` 43(둘 다 S→C, 신뢰 채널 0). 새 C→S 패킷은 없다(열기는 기존 Interact 입력). 일괄 점검: `PacketReader` 상한 43, BotConnection·BotView·HeadlessActor 파싱, Fuzz 목록. 서버 C→S 수신 switch는 바뀌지 않는다. | Phase 13.5·15에서 상한·수신 case 누락이 실제 사고였다. | 없음. |
| D10 | **QA(§67).** 서버 관찰: Container 상태(생성·열림), Supply Drop 목록, 지점 주변 월드 아이템 목록(종류·등급·양). 명령: `spawnSupplyDrop`(즉시 생성, 위치 선택 규칙을 그대로 쓰거나 좌표 지정), `setContainer`(생성·열림 강제, 시나리오 준비용). 시나리오 `QA/Scenarios/Loot/`: `chest_open`, `chest_duplicate_open`(두 플레이어 같은 Tick + 같은 사람 반복 → Loot 한 번), `chest_loot`(표 규칙: 무기 보장, 개수), `ammo_box`, `supply_drop_spawn`(다음 원 안, 플레이어 15 m 밖, 낙하 시간), `supply_drop_open`(착지 전 거절, 착지 뒤 Epic+ 무기), Unity 스크린샷 `visual_loot`. | 요청서 §67 그대로. | 없음. |
| D11 | **지금 넣지 않는 것.** (Known Issue: Container Loot는 떨어진 아이템과 같은 규칙이라 월드 아이템이 256개로 가득 차면 나중의 사망 드롭에 밀려날 수 있다.) Container 충돌체, 열기 진행 시간·소리(Phase 18), 봇의 Container 사용, 연기 신호탄, Supply Drop에 깔림 피해, Container 위치 무작위, 경기 중 다시 채우기. | 범위를 지킨다. | 필요하면 다음에 더한다. |

## 검증 계획

- Shared: `LootContainers` 배치 규칙(문과 거리 포함), 새 패킷 왕복·Fuzz, Supply Drop 높이 계산.
- Server: `ContainerRules.FindTarget`(문 규칙과 같은 기하, 문과 더 가까운 쪽), Client 복사본 일치 테스트.
- Server: 시드 재현(같은 시드 = 같은 생성·Loot), 바닥 Loot 회귀(Container를 열지 않으면 기존과 같음), 열기 검증 각 조건(결과 화면에서 열리지 않음 포함), 같은 Tick 두 명·반복 열기 = 한 번, E 우선순위(문·소생 대상이 있으면 열지 않음), Loot 표 규칙(보장·굴림 수·등급 범위), Supply Drop 일정·위치 규칙(다음 원 안, 플레이어 15 m 밖, 맵 안)·낙하·착지 전 거절, Join·Resume 재전송, 경기 시작·끝 초기화, Tick 할당 없음.
- Client EditMode: Client 규칙 복사본 일치, Supply Drop 높이 계산, 표시 상태 모델.
- QA: D10 시나리오, `suite:smoke`, `suite:pre-push`, `suite:building`, `suite:squad`, `suite:map`, `stress-quick`, Unity 스위트.
