# Phase 16 Loot Chest / Ammo Box / Supply Drop — 구현 기록 (Spec과 다른 점)

설계: `Docs/specs/2026-10-08-phase16-loot-containers-design.md`(D1–D11). 동작 설명: `Docs/Loot.md`, 패킷: `Docs/Networking.md`. 이 문서에는 구현하면서 Spec에 없던 것을 정했거나 Spec과 다르게 한 것만 적는다.

## 서버

| # | 내용 | 이유 |
|---|---|---|
| S1 | Supply Drop 시작 높이 60 m는 Shared 상수(`SupplyDropFall.StartHeight`)이고, 낙하 속도만 `loot.json` `supplyDrops.fallSpeed`다. | 22 B 패킷에 시작 높이 칸이 없다. 착지 Tick이 속도를 담으므로 Client는 상수와 패킷만으로 높이를 계산한다. |
| S2 | 시선이 막힌 Container가 대상이면 그 E는 아무것도 하지 않는다(`ContainerOpensBlocked`만 센다). | 처음 구현은 문·줍기로 넘어갔는데, Client 안내·예측은 시선을 몰라 안내 없이 아이템을 줍거나 예측 없이 문이 움직였다(정확성 리뷰). |
| S3 | Supply Drop 위치는 1차(장애물 XZ 2 m + 플레이어 15 m) → 2차(장애물 + 플레이어 3 m) → 다음 원 중심(2차 조건) 순서로 고르고, 모두 실패하면 그 Tick에는 만들지 않고 다음 Tick에 다시 시도한다. 일정은 성공했거나 4개가 찼을 때만 넘어간다. 기존 Supply Drop과도 XZ 2 m 떨어진다. | 처음 구현은 후보가 모두 실패하면 검사 없이 중심에 놓아, 후반(작은 원)에 플레이어 바로 옆에 떨어지거나 중심이 상자 안이면 열 수 없는 Supply Drop이 되었다(정확성 리뷰). |
| S4 | 바닥 Loot 지점이 쓰는 표는 항목이 1개 이상이어야 한다(`GameData`·`LootSpawner` 시작 검증, 운영 시작은 실패). 항목 0개 + `guaranteed`만 있는 표는 Container용으로 허용한다. | 리뷰 Low: 바닥 경로가 빈 표를 굴리면 경기 시작마다 IndexOutOfRange. |
| S5 | `GameData` 생성자는 Container 표를 요구하지 않고 운영 시작(`LoadDirectory`)만 요구한다. 표가 없으면 그 Container는 생기지 않는다. | 기존 테스트 JSON을 그대로 쓰기 위해서다. |
| S6 | QA: `setContainer`의 인자 이름은 `container`(Step의 `id`와 겹침). `/qa/loot` 하나로 Container·Supply Drop·주변 아이템을 보여 준다. 위치를 주지 않은 `spawnSupplyDrop`은 다시 시도하지 않고 자리가 없으면 409다. | 시나리오 작성·판독을 단순하게 한다. |

## Client

| # | 내용 | 이유 |
|---|---|---|
| C1 | 안내 문구를 종류별로 나눴다("[E] 상자 열기", "[E] 탄약 상자 열기", "[E] 보급품 열기"). | 무엇을 여는지 보이게 한다. |
| C2 | 같은 E로 서버가 더 가까운 Container를 연다면 `LocalPlayerPredictor`가 문을 예측하지 않는다. | 문 옆 Chest를 열 때 문이 잠깐 열렸다 되돌아가지 않게 한다. |
| C3 | Loot 상태는 서버가 보내는 0 마스크·빈 목록과 끊김 때만 비운다(MatchState 카운트다운에서는 비우지 않는다). | 접속 순서상 MatchState가 Loot 패킷보다 늦게 와서 받은 상태를 지울 수 있다. |
| C4 | `/qa/status`에 `mapSupplyDrops`(미니맵에 그린 수)와 `lootPrompt`(none/chest/ammoBox/supplyDrop)를 더했다. | Unity 시나리오 확인용. |

## 측정

`stress-quick` 5/5 PASS(50명). Mixed Match Tick p95 0.338 ms. Loot Tick 코드는 할당이 없다(`LootTicks_AllocateNothing`). 위치 재시도는 Tick마다 Random 32회·검사 33회 이내, 할당 없음.

## Known Issues

- Container Loot는 떨어진 아이템과 같은 규칙이라 월드 아이템이 256개로 가득 차면 나중의 사망 드롭에 밀려날 수 있다.
- 다음 원 중심 3 m 안에 플레이어가 계속 머물면 그 경기 동안 매 Tick 위치를 다시 고른다(할당 없음, Spec대로).
- Client 이동 예측은 소생·재투입 대상이 범위 안에 있어도 E로 문을 예측한다(Phase 14부터, 이번에는 Container 경우만 막았다).
- 할당 없음 테스트(`BotBrainTests`, `MatchEliminationTests`, `WorldItemsTests`의 Tick 할당 테스트)가 전체 실행에서 드물게 한 번씩 실패하고 다시 재현되지 않는다. 측정 환경에 따른 흔들림으로 보이며 이번 범위 밖이다.
