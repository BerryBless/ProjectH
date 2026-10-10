# 차량 (Phase 19)

설계: `Docs/specs/2026-10-08-phase19-vehicle-design.md`(D1–D16). 구현 기록: `Docs/plans/2026-10-08-phase19-vehicle.md`. 패킷: `Networking.md` "차량 (Phase 19)".

## 한눈에

- 4륜 차량 한 종류, 좌석 2개(0 운전석, 1 조수석), 경기당 최대 8대. 경기 시작 때 `VehicleSpawns` 4곳에 생기고, 같은 경기에서 다시 생기지 않는다.
- 서버가 최종 권한을 갖는다. 운전자 Client만 자기 차량을 예측하고(`VehiclePredictor`), 나머지는 보간한다(`VehicleStore`).
- 조작: W/S 가속·후진, A/D 조향, Space 제동, Shift 부스트, E 타기·내리기. 새 입력 비트는 없다(`InputCommand`의 MoveX·MoveY·Jump·Sprint·Interact를 다시 쓴다). 카메라는 자유다.

## 운동 (Shared `VehicleSimulation.Step`)

서버와 운전자 예측이 같은 함수를 쓴다. 상수는 `VehicleSettings`.

| 항목 | 값 |
|---|---|
| 가속 / 부스트 가속 | 8 / 12 m/s² |
| 최고 속도 / 부스트 / 후진 | 20 / 26 / 6 m/s |
| 제동 / 굴러감 감속 | 24 / 4 m/s² |
| 회전 | 75°/s × 조향 × min(1, \|속도\| / 5) |
| 높이 | 중심과 앞·뒤 축의 지형 높이 중 가장 높은 값(공중 없음) |
| 막힘 | 경사 0.8 초과, 맵 경계 ±78 m, 차체 발자국(축마다 2.2 m 정사각형, 높이 +0.5 ~ +1.8 m)이 상자·경사면 경계와 겹침 |

- 막힌 Tick은 위치와 Heading을 모두 되돌리고 속도를 0으로 한다. 시작 위치에서 이미 겹친 상자는 무시한다(끼임 방지).
- 운전자가 없으면 매 Tick 제동한다.
- 발자국 정사각형은 회전하지 않는다(축 정렬). 차량끼리는 서로 막지 않는다.

## 충돌과 피해 (서버 전용, `vehicles.json`)

| 항목 | 규칙 |
|---|---|
| 벽·상자·문·채집 대상·건설 조각 | 차량이 멈춘다. 충돌 직전 속도가 8 m/s를 넘으면 차량이 (속도 − 8) × 6 피해. 조각은 피해를 받지 않는다. 공격자 없음 |
| 플레이어 | 서로 막지 않는다. 차량이 6 m/s 넘게 움직이며 팀이 아닌 살아 있는 플레이어와 겹치면 속도 × 2 피해(공격자 = 운전자, 같은 플레이어에게 1 s에 한 번) |
| 총 | 되감은 차량 상자(위치 기록 32칸)를 맞힌다. 피해 = 무기 피해 × 1. 탄 사람은 직접 맞지 않는다. 차량은 차체와 겹쳐 서 있는 플레이어를 가리지 않는다 |
| 로켓·폭발 | 로켓은 차체에 닿으면 터진다. 폭발은 차체 상자까지 거리로 피해를 준다. 수류탄은 차량을 통과한다 |
| 파괴 | 체력 400 → 0이면 Wrecked. 탄 사람을 내리고 각자 25 피해(원인 Explosion). 공격자는 최근 10 s 안에 차량에 피해를 준 적 중 마지막 사람, 없으면 없음. 아군의 사격·로켓으로 팀 차량이 부서져도 탄 팀원은 같은 25 피해를 받는다(의도, 처치 기록 없음. Phase 17–19 리뷰에서 확인). 5 s 뒤 사라진다 |

피해는 경기 상태가 피해를 허용할 때만(사격과 같은 규칙) 준다.

## 타기와 내리기

- **타기(E):** 살아 있음, Ground·Crouch 모드, 소생·재투입 진행 중 아님, 차량 Active, 빈 좌석(운전석 먼저), 발에서 차체까지 1.5 m 이하(`VehicleSettings.EnterRange`). 여러 대면 가장 가까운 것, 같으면 낮은 id. 그 차의 차체(가장 가까운 점)까지 눈에서 맵 상자·닫힌 문·지형·건설 조각이 막지 않아야 탄다(Phase 17–19 리뷰: 닫힌 건물 안에서 벽 너머 차에 타지 않는다).
- **E 우선순위:** 탄 상태면 내리기가 가장 먼저 → 소생·재투입 → Container·Supply Drop·문 → 차량 → 줍기. Client 안내 복사본은 `VehiclePrompt.FindEnterTarget`이고 서버 테스트가 거리 규칙이 같은지 비교한다. 시선 검사는 서버에만 있다: 같은 거리 규칙으로 고른 대상까지 시선이 막히면 그 E는 아무것도 하지 않는다(시선 막힌 Container와 같은 정책: Client가 "[E] 타기"를 띄우고 줍기 안내를 껐으므로 줍기로 넘어가지 않는다. `Match.VehicleEntersBlocked`).
- **내리기(E):** 운전석 쪽 옆 2 m → 반대쪽 → 뒤 → 앞 중 서 있을 수 있고, 좌석에서 그 자리까지 몸 중심 높이(0.9 m)의 선을 맵 상자·닫힌 문·지형·건설 조각이 막지 않는 첫 자리(Phase 17–19 리뷰: 벽 너머로 내리지 않는다). 없으면 내리지 않는다.
- **내린 뒤 Jump:** E·강제 하차 모두, 내린 다음 입력부터 Jump 비트를 지우고 Jump 없는 실제 입력을 한 번 받으면 푼다(`PlayerEntity.JumpLatchedFromVehicle`, 제동으로 누르던 Space가 점프가 되지 않게). 서버가 만든 누락 반복 입력은 풀지 않는다. 다시 타기·부활·Resume에서 끈다. Client 예측도 같은 규칙이다.
- **강제로 내림:** 차량 파괴, 연결 끊김(유예 시작), 기절·탈락, 경기 끝·라운드 리셋, QA setPosition. 자리가 없으면 차 위 2 m에 둔다(낙하 피해 없음). 기절·탈락은 내린 뒤 처리한다.
- 탄 동안: 이동 모드는 Ground 그대로이고 위치는 좌석이다. 행동(사격·건설·채집·회복·수류탄·줍기·문·소생)은 할 수 없고 자기장 피해는 받는다. 타는 순간 같은 입력의 다른 행동도 하지 않는다.

## Client

- `VehicleStore`: 고정 8칸. 마지막으로 적용한 Tick 이하 패킷은 버리고, 1 s 동안 패킷에 없는 차량은 숨긴다. 렌더 Tick에서 보간한다.
  - 리뷰 수정 D3: 마지막 적용 Tick보다 `SimHz × (10초 + 마지막 적용 뒤 지난 로컬 시간)`(`MaxAheadSeconds` 10)을 넘게 앞선 패킷도 버리고 `TickRejects`를 센다(F1 "Tick 거절"은 Snapshot과 차량의 합). 버린 패킷은 마지막 Tick을 바꾸지 않아 다음 정상 패킷이 그대로 적용된다. 근처에 차량이 없으면 서버가 보내지 않으므로 조용했던 시간만큼 창이 넓어진다. 남은 한계: `Reset`(끊김·입장) 바로 뒤 첫 패킷은 기준이 없어 그대로 받는다(`Client.md` "받기 검증").
- `VehiclePredictor`: 나를 운전자로 적은 첫 기록과 그 ack에서 시작해 입력마다 예측하고, 2 m 넘게 틀리면 바로 옮기고 아니면 부드럽게 맞춘다.
- `LocalPlayerPredictor`: 탄 동안 이동 예측·문 예측·Reconcile을 멈추고, 내리면 Snap한다. 내리기 입력 뒤의 입력에는 제동(Jump)을 싣지 않는다(내린 뒤 점프 방지). 서버가 내리기를 거절하면 다시 싣는다.
- 표시(`VehicleViews`): 원시 메시(몸통 + 바퀴 4), 앞바퀴 조향, 지형 기울기, 체력 30 % 아래 연기, Wrecked는 검게. 탄 원격 플레이어는 좌석에 앉은 자세로 그리고 발소리를 내지 않는다.
- 카메라: 탄 동안 뒤 8 m·위 2.5 m. HUD: 속도(km/h)·차량 체력 막대, 안내 "[E] 탑승" / "[E] 내리기".
- 오디오: `VehicleEnter`, `VehicleExit`, `VehicleImpact`(기록 사이 속도가 제동으로 설명되지 않게 줄었을 때), 파괴는 `Explosion`. Join·Resume·관심 영역 진입·숨김 뒤 첫 기록은 기준일 뿐 소리를 내지 않는다.

## QA

- 서버 명령: `spawnVehicle`(x, z, heading → vehicleId; 맵·조각과 겹치면 409), `damageVehicle`(vehicleId, amount).
- 경로: `vehicle.count`, `vehicle.<id>.state/health/speed/driverId/passengerId/position`, 또는 `vehicleId` 인자. 누계 `vehicle.enters/exits/impacts/runOvers/wrecked`. 플레이어 `player.vehicleId`·`player.seat`, Actor `actor.vehicleId`·`actor.seat`·`actor.vehicleCount`.
- Unity `/qa/status`: `vehicle.seated/vehicleId/seat/speed/health/visibleVehicles`.
- 시나리오: `QA/Scenarios/Vehicle/`(spawn, enter, drive, exit, damage, destroy, disconnect, build_block → `suite:vehicle`), Unity `visual_vehicle`(`suite:unity`).

## Known Issues

- 차량에는 Collider가 없어 Client의 조준 광선·Ping·로컬 예광탄이 차체를 지나간다(서버 히트스캔은 맞힌다).
- 쏜 사람에게 차량 맞힘 표시가 없다.
- 수류탄은 차량을 통과한다. 차량끼리는 서로 막지 않는다. 플레이어는 차체를 지나갈 수 있다.
- 엔진 연속음이 없다(오디오 풀이 단발음 구조).
- 발자국이 회전하지 않는 정사각형 두 개라 대각선으로 놓인 차는 실제 모양보다 조금 넓게 막힌다.
- 조수석의 카메라·좌석은 렌더 Tick 기준이라 탄 직후 약 130 ms 늦게 따라간다. 잔해는 기록이 끊긴 뒤 1 s 더 보인다.
- 봇은 운전하지 않는다(탔으면 바로 내린다). Stress Brain은 차량을 모른다.
