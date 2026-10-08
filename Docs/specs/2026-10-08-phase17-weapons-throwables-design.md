# Phase 17 Weapons / Throwables — 설계 Spec

## Context

요청서는 `Docs/requests/2026-10-05-roadmap-phase13_5-19-request.md`의 STEP 7(§68–§78)이다. 목표는 기존 무기 시스템을 재사용해 전투 콘텐츠를 넓히는 것이다: 무기 5종 역할, 무기 데이터, Shotgun 산탄, Sniper, 최소 투사체 시스템, 수류탄, 폭발, 로켓, 구조물 피해 분리, 등급 연결.

**지금 구조에서 확인한 사실(`phase16-loot` 121ce78 기준):**

- **무기 데이터:** `weapons.json` 3종(Vesper AR, Kestrel LR, Wisp SMG), 최대 8종(`WeaponCatalogPacket.MaxWeapons`). 필드: damage, fireIntervalSeconds, magazineSize, reloadSeconds, range, automatic, spread, recoil, ammoType. spread·recoil은 데이터에만 있고 쓰이지 않는다(항상 0). Shotgun·Pistol·구조물 피해·산탄 수·투사체 종류 필드가 없다.
- **등급:** 피해 배율만(`items.json` 1.00–1.20, `CombatRules.ScaledDamage`).
- **사격:** `Match.FireShot`은 Hitscan 하나(맵 상자·문·채집 대상·지형 → 건설 조각 → 지연 보상한 플레이어, 팀원 통과). 퍼짐·난수·반동·거리 감쇠·산탄 없음. 구조물 피해 = 무기 피해 × 재료 배율(`building.json`, 모두 1.0). `ShotFired`(Unreliable)를 모두에게 보낸다.
- **피해:** `ApplyHit` → `HitConfirmed`·`DamageTaken` → `ApplyFatal`(기절/탈락, Phase 14). `DeathCause`는 Zone·Fall 둘뿐.
- **인벤토리·입력:** 무기 칸 3, 탄 종류 Light·Medium·Heavy, 소모품 Medkit(4)·ShieldCell(5). `InputButtons`(u16)는 32768 하나만 비어 있다. `InventoryState` 22 B.
- **투사체:** 없다. 가장 가까운 선례는 Supply Drop(고정 배열 + 계산식 공유 + 이벤트 패킷). 광선 판정(`HitScan`, `PieceTrace`, `IntersectAabb`)은 할당 없는 순수 함수다.
- **Protocol:** v15, 마지막 PacketId 43.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 범위 | 요청서 STEP 7(§68–§78) |
| 진행 방식 | 2026-10-08 "페이즈마다 코드리뷰 하고 계획된 페이즈 전부 계속 진행". 추천안으로 진행하고 이유는 이 문서에 남긴다. Phase마다 Commit, Push 없음. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **무기 6종(§68), 기존 3종 유지.**<ul><li>1 Vesper AR(돌격소총): 20, 0.1초, 탄창 30, Medium, 퍼짐 1°</li><li>2 Kestrel LR(저격): 90, 1.25초, 탄창 5, Heavy, 퍼짐 0, Hitscan</li><li>3 Wisp SMG: 12, 0.067초, 탄창 25, Light, 퍼짐 2.5°</li><li>4 Brute SG(산탄총): 산탄 8 × 11, 0.9초, 탄창 5, **Shells**, 퍼짐 6°, 사거리 35</li><li>5 Sparrow P(권총): 24, 0.2초 반자동, 탄창 12, Light, 퍼짐 1.5°</li><li>6 Thunder RL(로켓): 투사체, 탄창 1, 재장전 3초, **Rockets**</li></ul>수치는 `weapons.json`에만 있다. | 요청서 §68 최소 5종 + §75 로켓. 기존 무기 id·이름을 바꾸지 않아 기존 테스트·Loot·QA가 그대로다. | 수치는 JSON만 바꾼다. |
| D2 | **무기 데이터(§69).** 새 필드: `pellets`(기본 1), `spreadDegrees`(기존 spread를 각도로), `falloffStart`·`falloffMinRatio`(거리 감쇠: 시작 거리부터 사거리까지 선형으로 최소 비율까지), `structureMultiplier`(무기별 구조물 배율, 기본 1.0), `recoilDegrees`(Client 카메라 반동, 서버는 쓰지 않음), `projectile`(없음 = Hitscan, 또는 투사체 정의 이름). 투사체 정의(`projectiles` 절): 속도, 중력, 수명, 폭발 반지름·중심 피해·구조물 피해, 튕김 계수, 퓨즈. 시작 검증. `WeaponInfo` 와이어에 Client가 쓰는 값(산탄 수, 퍼짐, 반동, 투사체 종류, 사거리)을 더한다. | 한 곳(JSON)에서 정하고 Client는 카탈로그로 받아 값이 어긋나지 않는다(§69). | 없음. |
| D3 | **퍼짐(서버 결정).** 한 번 쏠 때 서버가 결정적 난수(시드 = 쏜 사람 Entity id, 서버 Tick, 그 Tick의 발 번호)로 조준 방향을 원뿔 안에서 흔든다. Client는 서버 Tick·발 번호를 알 수 없어 같은 시드를 재현할 수 없다. 그래서 **내 예광탄은 Client 자체 퍼짐으로 그리는 표시용**이고, `ShotFired`가 서버의 실제 끝점을 실어 다른 사람에게는 실제 방향이 보인다. 결정성은 서버 테스트로만 고정한다. 반동은 Client 카메라만 움직인다(서버 조준은 Client가 보낸 조준을 그대로 쓴다). | 퍼짐을 서버가 정해야 공정하다(§70 "Server가 Spread를 생성"). 반동을 서버에서 모의하면 입력 지연이 커진다. | 반동 서버 모의가 필요하면 다음에 더한다. |
| D4 | **Shotgun(§70).** Fire 한 번에 서버가 산탄 8개의 방향을 만들어 각각 Hitscan한다. 같은 대상의 피해는 합쳐서 대상마다 `ApplyHit` 한 번(`HitConfirmed` 하나), 조각도 합쳐서 한 번. `ShotFired`는 발사 하나에 하나(가운데 방향). 산탄마다 패킷을 보내지 않는다. | 요청서 §70 그대로. 패킷 수가 산탄 수에 비례하지 않는다. | 없음. |
| D5 | **Sniper = Hitscan(§71).** 지금처럼 지연 보상한 Hitscan이다. 맵이 160 m라 탄 낙하·비행 시간이 의미가 작고, Hitscan은 기존 지연 보상을 그대로 쓴다. | 현재 규모·Network 구조에 맞다. | 탄도가 필요하면 투사체 정의 하나를 연결한다. |
| D6 | **투사체 시스템(§72, 서버 권위).** 서버 고정 배열 32칸(종류: Grenade, Rocket). Tick마다 위치를 적분하고(중력은 정의값), 이번 Tick 이동 선분을 맵 상자·닫힌 문·서 있는 채집 대상·지형(`HitScan`), 건설 조각(`PieceTrace`), 플레이어(로켓만, 현재 위치, 지연 보상 없음)와 판정한다. 맞으면: 로켓은 폭발, 수류탄은 맞은 면의 법선으로 반사(튕김 계수 0.4)하고 속도가 1 m/s 아래면 멈춘다. 수명·퓨즈가 끝나면 폭발. 꽉 차면 새 투사체를 만들지 않는다(발사 실패, 탄은 쓰지 않음). 그래서 **빈 칸을 먼저 확보한 뒤** 탄창·수류탄을 줄인다(`WeaponRules.Apply`는 지금 `FireShot` 전에 탄을 줄이므로 투사체 무기는 칸 확인을 먼저 한다).<br>**수명 규칙:** 생성 때 주인의 Entity id와 함께 `JoinOrder`·`TeamId`를 저장해 처치 기록·아군·자폭 판정에 쓴다(Entity id는 다시 쓰이고 팀은 경기 시작에 다시 정해진다). 주인이 나갔거나 죽었어도 폭발은 일어나고 처치 기록은 없다. `StartMatch`·`CloseRound`가 모든 투사체를 지운다. 결과 화면(Finished)에서는 새로 만들지 않고 피해도 없으며, 남아 있던 투사체는 경기가 끝나는 순간 지우고 `ProjectileExploded`를 보내지 않는다(Client는 리셋·끊김 때 비운다). 모든 총을 투사체로 바꾸지 않는다. | 요청서 §72 "최소 시스템". 고정 배열·할당 없음. 이동 판정은 사격 판정 함수를 다시 쓴다. | 투사체가 많아지면 칸 수만 늘린다. |
| D7 | **투사체 복제.** `ProjectileSpawned`(44: id, 종류, 주인, 위치, 속도, 시작 Tick), `ProjectileState`(45: id, 위치, 속도, Tick — 튕김·멈춤 때만), `ProjectileExploded`(46: id, 위치, 종류). 모두 신뢰 전송, 모두에게. Client는 사건 사이를 같은 중력으로 탄도 외삽해 그린다(표시만). Snapshot에 싣지 않는다. Join·Resume에는 살아 있는 투사체를 `ProjectileSpawned`로 다시 보낸다. | 이벤트 기반(§75 "Client는 Presentation을 보간"). 튕김이 있는 수류탄만 중간 사건을 보낸다. | 없음. |
| D8 | **폭발(§74).** 반지름 안의 대상마다 피해 = 중심 피해 × (1 − 거리/반지름)(선형 감쇠, 0 아래면 0) × 등급 배율(쏜 무기·수류탄은 Common).<ul><li>플레이어: 폭발점 → 몸 중심 시선이 맵 상자·닫힌 문·지형·건설 조각에 막히면 피해 없음. 팀원과 **자기 자신**은 피해 없음(아군 사격 OFF, 자폭 없음).</li><li>건설 조각: 폭발이 닿는 칸의 조각마다(시선 검사 없음) 구조물 피해 × 감쇠 × 재료 배율. 한 Tick의 여러 폭발도 기존 피해·붕괴 경로(`DamagePiece`, Tick 끝 붕괴 탐색 한 번)를 쓴다.</li><li>처치 기록: 투사체 주인. 새 `DeathCause.Explosion`(2).</li><li>경기 중(`DamageAllowed`)일 때만 피해.</li></ul> | 요청서 §74 그대로. 조각에 시선 검사를 하면 벽 하나가 뒤의 벽을 모두 지켜 로켓이 건물을 부수지 못한다. 자폭 없음은 근접 오폭으로 기절·탈락하는 일을 막는 단순한 정책이다. | 자폭이 필요하면 정책 하나를 켠다. |
| D9 | **수류탄(§73).** 새 소모품 `Grenade`(소지 최대 6, Loot·바닥에서 줍는다). 키 **6**(새 입력 비트 `ThrowGrenade` 32768, 누르는 순간). 서버: 살아 있고 행동 가능, 소지 ≥ 1, 간격 1초. 눈에서 조준 방향으로 18 m/s(위로 약간 올림), 중력, 튕김 0.4, 퓨즈 3초, 반지름 5 m, 중심 피해 80, 구조물 피해 120. 던지기는 진행 중인 회복을 끊는다. 투사체 칸을 먼저 확보한 뒤 수류탄 수를 줄인다. | 기존 소모품 칸 방식을 다시 쓴다(새 무기 칸 없음). 남은 입력 비트 하나를 쓴다. | 키는 Client 설정만 바꾼다. |
| D10 | **로켓(§75).** Thunder RL: 속도 40 m/s, 중력 0, 수명 4초(160 m), 반지름 4 m, 중심 피해 75, 구조물 피해 300(나무 벽 하나를 부순다). 맞는 순간 폭발. | 건설 전투의 대응 수단(§76). | 수치는 JSON만 바꾼다. |
| D11 | **구조물 피해 분리(§76).** 총기 구조물 피해 = 무기 피해 × 무기 `structureMultiplier` × 재료 배율. AR 1.0, SMG 0.8, SG 0.6(산탄 합계가 크므로), Sniper 1.5, Pistol 0.8. 폭발은 투사체 정의의 구조물 피해. | Player 피해와 Structure 피해를 따로 조정한다. 건설 전투가 무너지지 않게 산탄총·SMG를 낮춘다. | JSON만 바꾼다. |
| D12 | **등급(§77).** 기존 `items.json` 피해 배율을 그대로 모든 총기 피해(산탄 하나하나 포함)와 로켓 폭발 피해에 곱한다. 무기 정의는 하나(등급별 복제 없음). | 요청서 §77 그대로. | 등급별 다른 수치가 필요하면 배율 표에 필드를 더한다. |
| D13 | **탄약·인벤토리.** 새 탄 종류 `Shells`(4)·`Rockets`(5), `InventoryState`에 두 예비탄과 수류탄 수(→ 27 B). `items.json`에 탄 줍기 양(Shells 10, Rockets 2)·수류탄.<br>**탄 종류 일괄 점검:** 탄 종류 3개를 전제한 모든 곳을 고친다 — `Inventory` 탄 배열, `InventoryState` 예비탄, `ItemCatalog.TryParseAmmoType`, `items.json` 줍기 양, `DropEverything`의 탄 떨어뜨리기, Client `WeaponState` 예비탄·HUD, `BotView`/`BotConnection`, `HeadlessActor`, 탄을 읽는 QA 관찰. `type - 1` 색인을 확인한다.<br>**Loot(의도한 변경):** 무기 굴림이 전체 무기 중 균등이라 무기 6종·탄 5종이 되면 모든 바닥 Loot 결과가 바뀐다. 그래서 표마다 무기 목록(`weapons`)을 명시한다: 바닥(Floor·Building·Tower)과 Ammo Box는 기존 3종 + Shotgun·Pistol(로켓 제외), 탄은 Light·Medium·Heavy·Shells(로켓 탄 제외). Chest·Supply Drop 표는 로켓과 로켓 탄을 포함한다. Phase 16 `FloorLootRegressionTests`의 해시는 **이 결정 때문에 새로 잡는다**(테스트에 이유를 적는다). 시나리오의 시드별 기대값도 같은 이유로 다시 확인한다. | 기존 줍기·인벤토리 경로를 다시 쓴다. | 없음. |
| D14 | **Client.** 카탈로그로 새 값 사용: 산탄 예광탄(Client 자체 퍼짐, 표시용 — D3), 반동 카메라 킥(`recoilDegrees`, 몇 프레임에 걸쳐 복귀), 투사체 표시(수류탄 구, 로켓 원기둥 + 꼬리, 풀 32, 탄도 외삽), 폭발 효과(커지는 반투명 구, 풀 8), 키 6과 HUD 수류탄 수, 새 탄 종류 표시. 무기 이름은 카탈로그 그대로. | 저사양: 풀과 공유 Mesh. | 없음. |
| D15 | **봇.** 새 무기를 지금 규칙(가장 긴 사거리)으로 쓴다. 로켓·수류탄은 쓰지 않는다. | 부하 시험에 충분하다. | 다음에 더한다. |
| D16 | **Protocol v16.** `WeaponInfo` 확장, `InventoryState` 27 B, `AmmoType` Shells·Rockets, `ConsumableType.Grenade`, `InputButtons.ThrowGrenade` 32768(`KnownButtons`), 패킷 44–46, `DeathCause.Explosion`(`PlayerDied` 리더 상한). 일괄 점검: `PacketReader` 상한 46, 봇·HeadlessActor 파싱, Fuzz, Client `QaInput` 키 "6"과 QA `UnityActions.InputKeys` 양쪽. **입력 비트:** `ThrowGrenade`가 u16의 마지막 비트를 쓴다. Phase 19 탈것은 타기·내리기 = Interact, 가속 = Sprint, 브레이크 = Jump로 기존 비트를 다시 쓰므로 u32로 넓히지 않는다. | Phase 13.5·15의 누락 사고를 반복하지 않는다. | 없음. |
| D17 | **QA(§78).** `weapon_ar`(퍼짐 안에서 명중·감쇠), `weapon_shotgun`(한 번 Fire = 대상당 HitConfirmed 하나, 가까울수록 큰 피해), `weapon_sniper`, `grenade`(던지기·튕김·퓨즈 3초·폭발), `explosion_falloff`(거리별 피해, 벽 뒤 0), `rocket`(비행·충돌 폭발·조각 파괴), `structure_damage`(무기별 구조물 피해 비율), Unity 스크린샷 `visual_weapons`. | 요청서 §78 그대로. | 없음. |
| D18 | **지금 넣지 않는 것.** 투사체 지연 보상, 서버 반동, 부착물, 저격 탄도, 연막·섬광, 봇 투척, 무기 소리(Phase 18), 탄 종류별 패키지 무게. | 범위를 지킨다. | 다음에 더한다. |

## 검증 계획

- Shared: 새 패킷·`WeaponInfo`·`InventoryState` 왕복과 Fuzz, 입력 비트.
- Server: 무기 카탈로그 새 필드 검증, 퍼짐 결정성(같은 시드 = 같은 방향, 원뿔 안), 감쇠 계산, 산탄 합산(대상당 한 번), 구조물 배율, 투사체 적분·충돌·튕김·멈춤·수명·퓨즈·칸 부족, 폭발 감쇠·시선·팀원·자폭 없음·조각 피해와 붕괴·경기 밖 피해 없음·처치 기록, 수류탄 던지기 조건·간격·소지, 로켓, 기존 무기 3종 회귀(퍼짐 0이었던 저격은 그대로), Tick 할당 없음.
- Client EditMode: 표시용 퍼짐 원뿔 범위(서버 시드는 재현하지 않는다, D3), 투사체 외삽, 반동 복귀, 키 6.
- QA: D17, `suite:smoke`, `suite:pre-push`, `suite:squad`, `suite:map`, `suite:loot`, `suite:building`, `stress-quick`, Unity 스위트.
