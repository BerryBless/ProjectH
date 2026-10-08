# 무기와 투척물 (Phase 17)

설계 근거: `Docs/specs/2026-10-08-phase17-weapons-throwables-design.md`(D1–D18), 요청서 STEP 7(§68–§78). 패킷과 접속 순서는 `Networking.md` "무기와 투사체 (Phase 17)", 데이터 검증은 `Server.md`, 표별 Loot는 `Loot.md`다. 모든 판정은 서버가 한다. Client는 표시만 한다.

## 무기 표 (D1, D2, D11)

수치는 서버 `weapons.json`에만 있다(Client는 `WeaponCatalog`으로 받는다). 30 Hz 기준.

| id | 이름 | 피해 | 간격 | 탄창 | 재장전 | 사거리 | 탄 | 산탄 | 퍼짐(반각) | 감쇠(시작 → 사거리에서 비율) | 구조물 배율 | 반동(Client) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | Vesper AR | 20 | 0.1 s(3 Tick) 자동 | 30 | 2.0 s | 150 m | Medium | 1 | 1° | 50 m → 0.7 | 1.0 | 0.6° |
| 2 | Kestrel LR | 90 | 1.25 s(38) 단발 | 5 | 2.5 s | 300 m | Heavy | 1 | 0 | 없음 | 1.5 | 3° |
| 3 | Wisp SMG | 12 | 0.0667 s(2) 자동 | 25 | 1.6 s | 80 m | Light | 1 | 2.5° | 25 m → 0.6 | 0.8 | 0.4° |
| 4 | Brute SG | 11 × 8 | 0.9 s(27) 단발 | 5 | 2.4 s | 35 m | Shells | 8 | 6° | 8 m → 0.3 | 0.6 | 4° |
| 5 | Sparrow P | 24 | 0.2 s(6) 단발 | 12 | 1.4 s | 80 m | Light | 1 | 1.5° | 20 m → 0.6 | 0.8 | 1.5° |
| 6 | Thunder RL | (폭발) | 1.0 s(30) 단발 | 1 | 3.0 s | 160 m | Rockets | 1 | 0 | — | — | 5° |

- 기존 3종은 id·이름·피해·간격·탄창·재장전·사거리·탄이 그대로다. 저격(Kestrel LR)은 퍼짐 0·감쇠 없음으로 Phase 16과 같은 동작이다. AR·SMG는 스펙 D1대로 퍼짐이 생겼다.
- 감쇠 시작 거리와 최소 비율, 권총 사거리(80 m), 각 반동 값은 스펙이 정하지 않아 이번에 정했다(JSON만 바꾼다).
- 로켓의 `damage`(75)는 와이어 검사(1 이상)를 위한 표시값이다. 실제 피해는 투사체 정의의 폭발 피해다.

## 퍼짐 (D3)

- 서버가 한 번 쏠 때마다 광선마다 원뿔 안의 방향을 결정적으로 고른다(`WeaponSpread.Spread`): 시드 = (쏜 사람 Entity id, 시뮬레이션 중인 Tick, 그 Tick 안의 광선 번호 = 산탄 번호). SplitMix64 해시 두 개로 원뿔 입체각 위에 균등하다. `Random`을 쓰지 않고 할당이 없다. 같은 입력을 다시 돌리면 같은 광선이 나온다.
- 반각 0이면 조준 방향 그대로다(저격).
- Client는 서버 Tick·광선 번호를 알 수 없어 같은 시드를 재현할 수 없다. 내 예광탄은 Client 자체 퍼짐(표시용)이고, 다른 사람의 예광탄은 `ShotFired.End`(서버의 실제 끝점)다.
- 반동은 Client 카메라만 움직인다. 서버 조준은 Client가 보낸 조준 그대로다(서버 반동 모의 없음, D18).

## 거리 감쇠 (D2)

광선이 멈춘 거리 d에서 비율 = d ≤ start이면 1, start < d < range이면 1 − (1 − min) × (d − start) / (range − start), d ≥ range이면 min(`CombatRules.FalloffMultiplier`). 산탄도 하나하나에 곱한다.

## 산탄과 등급 (D4, D12)

- 방아쇠 한 번 = 탄창 1발 = 광선 `pellets`개. 광선마다 맵 상자·닫힌 문·서 있는 채집 대상·지형 → 조각(같은 거리면 조각) → 지연 보상한 다른 팀 플레이어 중 가장 가까운 것에 맞는다(`Match.TraceShot`).
- 대상마다 원 피해(피해 × 감쇠)를 합친 뒤 등급 배율을 **한 번** 곱해 반올림한다(`CombatRules.ScaledDamage(float, float)`, 최소 1). 대상마다 `ApplyHit` 한 번 → `HitConfirmed` 하나, `DamageTaken` 하나. 앞선 명중이 대상의 팀을 전멸시켜 이미 탈락했으면 건너뛴다.
- 조각도 조각마다 합친다: 구조물 피해 = 합 × 등급(반올림) × 무기 `structureMultiplier` × 재료 배율. 붕괴는 Tick 끝 한 번(`CollapseUnsupported`).
- `ShotFired`는 발사 하나에 하나: 한 발 무기는 퍼진 광선의 끝점, 산탄총은 퍼짐 없는 가운데 조준 광선의 끝점. 산탄마다 패킷을 보내지 않는다.
- 등급 배율은 `items.json`의 1.00–1.20을 모든 총기 피해(산탄 포함)와 로켓 폭발 피해에 곱한다. 수류탄은 Common(1.0).

## 투사체 (D6, D7)

- **저장소:** `ProjectileSet` 고정 32칸(종류 Grenade·Rocket). 꽉 차면 새로 만들지 않는다. 그래서 투사체 무기는 **칸을 먼저 확인한 뒤** 탄을 쓴다: `WeaponRules.Apply(..., aimValid && CanLaunch(weapon), ...)`. 칸이 없거나 결과 화면이면 조준 무효와 같아서 탄·간격·자동 재장전이 모두 그대로다. 수류탄도 칸을 확보한 뒤 수를 줄인다.
- **저장하는 주인:** Entity id(패킷용), `JoinOrder`(처치 기록·자기 판정. Entity id는 다시 쓰이고 JoinOrder는 다시 쓰이지 않는다), 생성 때의 `TeamId`(아군 판정. 팀은 경기 시작에 다시 정해지고 그때 투사체도 지워진다), 피해 배율(무기 등급).
- **이동:** Tick 시작 쪽에서(건설 요청 다음, 플레이어 행동 앞) 정확한 등가속 적분 `p += v·dt + ½·a·dt²; v += a·dt`(a = (0, −gravity, 0)). 이번 Tick에 생긴 투사체는 다음 Tick부터 움직인다(StartTick = 생긴 Tick). Client 외삽 공식과 같은 궤적이다.
- **충돌:** 이번 Tick 이동 선분을 맵 상자·닫힌 문·서 있는 채집 대상·지형·y = 0 바닥(`ProjectileRules.TraceWorld`, 법선 포함), 건설 조각(`PieceTrace`, 같은 거리면 조각), (로켓만) 플레이어의 **현재** 위치(지연 보상 없음, 주인과 주인 팀원·죽은 사람·수송기 탑승자는 지나간다)와 판정한다.
- **로켓:** 맞는 순간 그 자리(면 법선으로 0.05 m 밖)에서 폭발. 수명 4 s가 끝나도 폭발. 데이터 검증: 로켓은 `bounce` 0이어야 하고 수류탄은 0보다 커야 한다(`WeaponCatalog`).
- **수류탄:** 맞은 면의 법선으로 반사하고 속도 전체에 튕김 계수(0.4)를 곱한다. 법선은 상자면 들어간 면, 지형이면 `HeightField.Gradient`의 (−gx, 1, −gz), 바닥면이면 위, Ramp·한쪽 경사 지붕이면 경사 평면, 사각뿔 지붕이면 맞은 쪽 면이다. 위를 향한 면(법선 Y ≥ 0.7: 바닥·지형·Ramp·지붕 경사)에서 튕긴 속도가 1 m/s 아래면 정지한다(그 뒤 중력·이동 없음). 벽·천장에서는 느려도 계속 튕기며 떨어진다. 튕김·정지마다 `ProjectileState`. 퓨즈 3 s(90 Tick)가 끝난 Tick에 폭발한다(던진 Tick + 90).
- **정지한 수류탄의 받침:** 조각이 부서지거나 편집됐을 때(`BuildReplication.StructureVersion`, 설치·피해만으로는 오르지 않는다)나 문 마스크·채집 마스크가 달라졌을 때만, 그 뒤 첫 투사체 갱신에서 정지한 수류탄 아래 0.15 m를 다시 본다(편집은 같은 Tick, 폭발·사격·붕괴로 인한 파괴는 다음 Tick)(맵 상자·닫힌 문·서 있는 채집 대상·지형·바닥면·조각). 받침이 없으면 정지를 풀고 그 Tick부터 다시 떨어지며, 움직인 뒤의 위치·속도를 `ProjectileState`로 보낸다(속도 0은 Client가 정지로 읽으므로). Phase 17–19 리뷰.
- **수명 규칙:** 주인이 나갔거나 죽었어도 폭발은 일어나고 처치 기록은 없다. 시작 카운트다운(Starting)에 들어가는 Tick(Client가 `MatchState` Starting으로 비우는 시점과 같다)과 `StartMatch`·`CloseRound`·`FinishMatch`가 모두 지우고 아무것도 보내지 않는다(`ProjectileExploded` 없음). 시작 카운트다운(Starting)과 결과 화면(Finished·Closing)에서는 새로 만들지 않는다. 대기실(Waiting)·경기 중·개발 모드는 만든다. Join·Resume에는 살아 있는 투사체를 지금 상태로 `ProjectileSpawned`(StartTick = 마지막 Tick) 보낸다.

## 폭발 (D8)

- 칸을 먼저 비우고 모두에게 `ProjectileExploded`를 보낸 뒤, 피해가 허용될 때(`DamageAllowed`: 경기 중 또는 개발 모드)만 피해를 준다. 처리 순서는 플레이어 → 조각이다(그 폭발이 부순 벽도 그 폭발의 시선 검사에서는 아직 서 있다).
- **플레이어:** 거리 = 폭발점에서 그 사람의 몸 상자(이동 모드 높이)까지(안이면 0). 피해 = 중심 피해 × (1 − 거리 / 반지름) × 배율, 반올림, 0이면 맞지 않음. 폭발점 → 몸 중심 시선이 맵 상자·닫힌 문·서 있는 채집 대상·지형·조각에 막히면 피해 없음. 주인 자신(자폭 없음)과 생성 때 주인 팀원(아군 피해 없음)은 제외한다. 처치 = 아직 경기에 살아 있는 주인(없으면 처치 없음). `DamageTaken.FromDirection` = 피해자 → 폭발점. 치명이면 기절·탈락 경로 하나(원인 `DeathCause.Explosion`, `PlayerDied`·`PlayerDowned`에 주인 id와 함께).
- **조각:** 폭발이 닿는 칸(반지름 + 이웃 한 칸)의 조각마다 조각 경계 상자까지의 거리로 구조물 피해 × (1 − 거리 / 반지름) × 재료 배율. 시선 검사 없음(벽 하나가 뒤의 벽을 모두 지키면 로켓이 건물을 부수지 못한다). id를 고정 버퍼(3,920칸)에 먼저 모은 뒤 `DamagePiece`로 준다(부서진 조각이 칸 목록을 바꾸므로). 붕괴는 Tick 끝 한 번.

| 투사체 | 속도 | 중력 | 수명/퓨즈 | 반지름 | 중심 피해 | 구조물 피해 | 튕김 |
|---|---|---|---|---|---|---|---|
| Grenade | 18 m/s(조준보다 8° 위) | 9.81 | 3 s | 5 m | 80 | 120 | 0.4 |
| Rocket | 40 m/s | 0 | 4 s(160 m) | 4 m | 75 | 300(나무 벽 하나) | 0 |

## 수류탄 던지기 (D9)

- 키 6 = 입력 비트 `ThrowGrenade`(32768). 누르는 순간에만 던진다(서버 `EdgeButtons`).
- 조건: 살아 있고 행동 가능 모드(Ground·Crouch·Slide, 기절 제외), 조준이 유효, 결과 화면·카운트다운이 아님, 소지 ≥ 1, 간격 1 s(`NextGrenadeTick`, 새 생명마다 0), 빈 투사체 칸. 도구·재장전과 무관하고 탄창·발사 간격에 영향이 없다.
- 성공하면 수 −1, 진행 중인 회복(Medkit·Shield Cell)과 소생·재투입을 끊는다(6 누름은 채널 취소 키이기도 하다).
- 소지 최대 6(`items.json` Grenade maxStack). 줍기·사망 드롭·QA `giveGrenade`는 같은 칸을 쓴다(수류탄이 Shield Cell 칸으로 들어가지 않는다).

## 탄약과 인벤토리 (D13)

- 탄 종류 5개(Light 1, Medium 2, Heavy 3, Shells 4, Rockets 5), 색인 = 종류 − 1. 줍기 양·최대: Shells 10·40, Rockets 2·6.
- 일괄 점검한 곳: `Inventory` 탄 배열, `InventoryState`(27 B), `ItemCatalog.TryParseAmmoType`, `items.json`, `DropEverything`(종류마다 한 아이템, 수류탄 포함), `StartingLoadout`, `BotView.Reserve`, QA `giveAmmo`·`spawnLoot`·플레이어 DTO·인벤토리 해시, Stress `AmmoTypeOf`.

## 봇 (D15)

봇은 가장 긴 사거리 규칙 그대로 새 무기(산탄총·권총)를 쓴다. 투사체 무기는 쓰지 않는다: 로켓은 줍지 않고(`IsUseful`), 들고 있어도 고르지 않으며(`HasRounds`가 false), 수류탄은 줍지도 던지지도 않는다. 로켓 탄도 줍지 않는다(그 탄을 쓰는 무기가 없으므로).

## 서버 비용 (server-hotpath, queue-cache)

- Tick 할당 없음: 투사체 배열·산탄 합계·폭발 조각 버퍼·폭발 기록은 생성 때 한 번 만든다. `ProjectileTicks_AllocateNothing`이 수류탄 튕김·정지·폭발, 로켓의 조각 폭발, 산탄 8발을 할당 0으로 고정한다.
- 산탄총 한 발 = 광선 9개(산탄 8 + 예광탄 1). 투사체 하나 = Tick당 선분 판정 1회(+ 로켓은 플레이어 수만큼 상자 검사). 폭발 하나 = 플레이어 수만큼 시선 검사 + 최대 7 × 7 칸의 조각.

## QA (D17)

`QA/Scenarios/Weapons/`(weapon_ar, weapon_shotgun, weapon_sniper, grenade, explosion_falloff, rocket, structure_damage)와 `suite:weapons`. 관찰은 `GET /qa/projectiles`, 사건 `ProjectileLaunched`·`Explosion`, 명령 `giveGrenade`·`giveAmmo shells/rockets`. 표는 `QA.md`.

## 지금 넣지 않은 것 (D18)

투사체 지연 보상, 서버 반동, 부착물, 저격 탄도, 연막·섬광, 봇 투척, 무기 소리(Phase 18), 탄 종류별 무게.
