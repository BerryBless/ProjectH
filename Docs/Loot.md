# Loot Container와 Supply Drop (Phase 16)

설계 근거: `Docs/specs/2026-10-08-phase16-loot-containers-design.md` D1–D11. 패킷은 `Networking.md` "Loot Container와 Supply Drop (Phase 16)", 데이터 형식은 `Server.md`의 `loot.json` 항목이다. 바닥 Loot(`LootPoints`)는 `BattleRoyale.md`와 `Networking.md` "인벤토리와 Loot"다.

## Container 배치 (D1)

- Shared `LootContainers.All`(맵 배치 상수, game-core-rules §4 예외 2): Chest 20개, Ammo Box 14개(합 34, 최대 64). 항목 = 종류, 바닥 중심 위치(지형 높이), 방향(Yaw 0·90·180·270). **Id = 목록 순서**(순서를 바꾸지 않는다). 크기: Chest 1.0 × 0.7 × 0.6 m, Ammo Box 0.6 × 0.4 × 0.4 m(`LootContainers.SizeOf`, Yaw 90·270이면 가로·세로가 바뀐다).
- **충돌체가 아니다**(Reboot Station과 같다). 이동·사격·예측 충돌 수집(`CollisionWorld`)은 Container를 모른다. Client는 충돌체 없는 상자를 그린다.
- 배치 검사(`LootContainersTests`): 벽 안·광장(12 m) + 2 m 밖, 평평한 지형 위, 맵 상자와 0.3 m 이상, 문·채집 대상과 떨어짐, **문 중심과 `DoorInteractRange` + 1 m(3.5 m) 넘게**, Loot 지점과 2 m(줍기 범위), 스테이션과 3 m, Container끼리 3 m, 그리고 걸어서 닿음(E 범위 안에 상자와 겹치지 않는 서 있을 자리가 있고, 눈에서 Container 가운데까지 상자·문·채집 대상·지형이 막지 않는다).

## 생성과 Loot (D2, D5)

- **경기 시작**(`StartMatch`)에 Container마다 생성 여부(`loot.json` `spawnChance`: Chest 0.7, Ammo Box 0.8)와 Loot(Chest·AmmoBox 표)를 미리 굴려 서버에 둔다(Container마다 4칸 고정 배열). 열기 전에는 아무것도 보내지 않는다(생성 마스크만).
- **시드 흐름이 바닥 Loot와 따로다.** 바닥 Loot는 그대로 `LootSeed + 판 번호`의 `Random`, Container는 그 값에 상수를 섞은 시드, Supply Drop은 또 다른 상수다. 그래서 Container를 열지 않으면 바닥 Loot가 Phase 15와 바이트 단위로 같다(`FloorLootRegressionTests`: Phase 15 코드에서 잡은 해시), Container를 열어도 바닥 아이템은 바뀌지 않는다.
- **개발 모드**는 서버 시작 때 같은 방식(판 번호 1)으로 정하고 다시 채우지 않는다. Supply Drop은 없다.
- **표 규칙**(`loot.json`, 수치는 JSON만 바꾼다):
  - Chest: 3개, 무기 1개 보장 + 탄약·회복·실드·자원(나무·돌·금속 중 하나, 30) 중 가중치로 2개.
  - Ammo Box: 2개, 탄약 보장 + 자원 10.
  - Supply Drop: 4개, 무기 1개 보장(등급 Epic·Legendary만, 표별 `rarityWeights`) + 실드·회복·탄약 가중치로 3개.
- 열면 Loot는 Container 둘레 1 m 원 위에 월드 아이템으로 놓인다(첫 아이템은 Container가 바라보는 쪽). **떨어진 아이템과 같은 규칙**이다(256칸이 가득 차면 오래된 것부터 밀려난다. Known Issue: 나중의 사망 드롭에 밀려날 수 있다).

## 상태와 복제 (D3)

- 없음(생성 안 됨) / 닫힘 / 열림. 생성 마스크 + 열림 마스크(u64 두 개)를 `ContainerStates`(42, 17B)로 바뀐 Tick 끝에 모두에게, Join·Resume에 보낸다.
- 열린 Container는 그 경기에서 다시 생기지 않는다. `CloseRound`(라운드 리셋)·`StartMatch`가 두 마스크와 Supply Drop 목록을 지운다.

## 열기 (D4)

- **E 순서:** 소생·재투입 대상 → (문, Container) 중 더 가까운 쪽(같은 거리면 문) → 줍기. Container 후보는 생성되고 닫힌 Chest·Ammo Box와 착지하고 닫힌 Supply Drop이다.
- **대상 고르기:** 서버 순수 함수 `ContainerRules.FindTarget`(문과 같은 기하: 발에서 수평 `DoorInteractRange` 2.5 m, 조준 ±`DoorInteractHalfAngle` 60°, 발 높이가 대상 바닥 ±1 m). 가장 가까운 것, 같은 거리면 Container 먼저·작은 id. 문과의 선택은 `ContainerRules.PreferContainer`. Client에 같은 규칙 복사본(`Client/Assets/Scripts/Game/ContainerRule.cs`, "[E] 열기" 안내용)이 있고 서버 테스트가 source link로 컴파일해 결과를 비교한다(`ContainerRulesTests.TheClientsCopy_PicksTheSameTarget`). Shared에는 두지 않는다(`DoorRules`와 같다).
- **서버 검증:** 경기 중(`InMatch`) 또는 개발 모드(결과 화면·대기실에서는 후보가 없다: E는 문·줍기 그대로), 생성됨, 닫힘(후보 마스크를 누를 때마다 지금 상태로 만든다), 살아 있고 행동 가능한 모드(`ActionsAllowed`: 기절·낙하·탑승 불가), 거리·각도, **시선**(눈 → Container 가운데가 맵 상자·닫힌 문·지형·건설 조각에 막히지 않음. 채집 대상은 보지 않는다). 시선이 막히면 **그 E는 아무것도 하지 않는다**(Health `opensBlocked`만 센다). Client는 시선을 모르고 그 자리에서 "[E] 열기"를 띄우며 줍기 안내·문 예측을 끄므로, 안내 없는 줍기(무기 교체 포함)나 예측 없는 문 움직임이 생기지 않게 하려는 것이다(Phase 16 리뷰).
- 열기는 즉시다(진행 시간 없음). 같은 Tick에 두 사람이 누르면 플레이어 처리 순서상 먼저인 한 명만 연다. 두 번째 사람의 E는 줍기로 간다. 열기 전용 응답 패킷은 없다(`ContainerStates`·`ItemSpawned`로 안다).

## Supply Drop (D6, D7)

- **일정:** `loot.json` `supplyDrops.times`(자기장 시계 기준 60초, 150초, 최대 4개). 자기장 시계는 수송기 경로가 끝날 때(공중 투입이 아니면 경기 시작 Tick) 시작한다.
- **위치:** "다음 원" = 지금 Phase의 목표 원(`SafeZone.CenterX/CenterZ/Radius(Phase)`, Phase 0이면 원 1). 반지름 × 0.6 안에서 Supply Drop 흐름의 시드로 후보를 최대 16개 고르고(±60 m로 자른다), 맵 상자 발자국·문·채집 대상·Container·스테이션·Loot 지점·이미 있는 Supply Drop과 XZ 2 m, 살아 있는 플레이어와 수평 15 m 떨어진 첫 후보(1차). 없으면 같은 후보들을 플레이어 15 m 조건만 빼고 다시 본다(2차: 장애물 2 m는 유지, 플레이어와 3 m는 여전히 띄운다). 그래도 없으면 다음 원 중심(±60 m로 자름)을 2차 조건으로 본다. 그것도 실패하면 **이번 Tick에는 만들지 않고 다음 Tick에 다시 시도한다**(일정 순서는 그대로, 그 경기 동안만, Tick당 후보 16개 굴림 + 검사 최대 33번, 할당 없음). QA `spawnSupplyDrop`(좌표 없음)은 다시 시도하지 않고 409로 답한다(Phase 16 리뷰: 예전에는 검사 없이 중심으로 가서 §65를 어기거나 상자 안에 떨어져 열 수 없었다). 착지 높이는 지형 높이다. **건설 조각은 보지 않는다**(플레이어 건물을 지나 지형에 착지한다).
- **낙하:** 착지 높이 + 60 m(`SupplyDropFall.StartHeight`, Shared 상수)에서 `fallSpeed`(4 m/s)로 15초. 위치는 (시작 Tick, 착지 Tick, x·z, 착지 높이)로 양쪽이 `SupplyDropFall.HeightAt`으로 계산한다(서버 물리 없음, Tick마다 보내지 않는다).
- **상태:** Falling → (착지 Tick 끝) Landed → (E) Opened. 착지 전에는 열 수 없다. Loot는 생성 때 Supply Drop 표로 미리 굴려 둔다(같은 흐름). 열면 둘레 1 m에 4개.
- **생성·착지·열기는 경기 중(`InMatch`)에만** 일어난다. 결과 화면에서는 멈추고, 라운드 리셋에 지운다. 경기당 최대 4개(QA 명령 포함).
- **복제:** `SupplyDrops`(43, 최대 90B)로 경기의 Supply Drop 전체를 생성·착지·열림이 있었던 Tick 끝에 모두에게, 경기 시작·라운드 리셋(빈 목록), Join·Resume에 보낸다. Snapshot에는 싣지 않는다.

## 서버 구조와 비용

- 코드: `Game/Match.Loot.cs`(상태·열기·Supply Drop·전송), `Game/Loot/ContainerRules.cs`(대상 규칙), `Game/Items/LootTable.cs`(표·굴림·스키마). 모두 Game Loop 스레드 소유이고 Lock이 없다.
- 컬렉션은 모두 고정 크기다: Container Loot 64 × 4칸, Supply Drop 4칸(+ Loot 4 × 4), 위치 4칸. 경기 시작에 `Random` 두 개(Container, Supply Drop)를 만든다(개발 모드는 생성자에서 Container 하나). Tick 경로(일정 확인, 착지 확인, E 대상 찾기, 전송)는 할당이 없다(`LootContainerMatchTests.LootTicks_AllocateNothing`).
- E 한 번의 비용: 문 5개 + Container 34개 + Supply Drop 4개 거리 계산, 대상을 고르면 광선 하나(맵·문·지형 + 조각).
- 카운터: Health 줄 `loot containersOpened dropsSpawned dropsLanded dropsOpened items blocked packets`, Meter `projecth.loot.events`(event Tag), `/qa/health` `loot`.

## QA (D10)

- 관찰: `GET /qa/loot`(Container 상태·Loot, Supply Drop 목록, 지점 주변 월드 아이템 목록: 종류·등급·양과 종류별 수), `/qa/match`의 `containersSpawned`·`containersOpened`·`supplyDrops`.
- 명령: `spawnSupplyDrop`(위치 규칙 또는 좌표), `setContainer`(none/closed/open 강제).
- 시나리오 `QA/Scenarios/Loot/`와 `suite:loot`(`QA.md` "Scenario Library"). Unity 스크린샷 `visual_loot`(리더가 작성)는 `unity.json`에 들어갈 예정이다.

## 지금 넣지 않은 것 (D11)

Container 충돌체, 열기 진행 시간·소리(Phase 18), 봇의 Container 사용, 연기 신호탄, Supply Drop 깔림 피해, Container 위치 무작위, 경기 중 다시 채우기.
