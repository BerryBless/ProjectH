# Phase 19 Vehicle — 설계 Spec

## Context

요청서는 `Docs/requests/2026-10-05-roadmap-phase13_5-19-request.md`의 STEP 9(§86–§95)다. 목표는 **기본 4륜 차량 하나**: 타기, 내리기, 운전, 조향, 제동, 피해, 파괴. 서버가 최종 권한을 갖고(§87), 단순하고 예측 가능한 모델을 쓰며(§90), 모든 Frame 상태를 보내지 않고 관심 영역을 쓴다(§94).

**지금 구조에서 확인한 사실(`phase18-audio` 339b923 기준):**

- **Snapshot 엔티티 기록(13 B)은 꽉 찼다.** Flags 8비트, 이동 모드 3비트 8값이 모두 쓰인다(Downed = 7이 마지막). Snapshot에는 엔티티 관심 영역이 없다(모든 플레이어를 모두에게 보냄, 15 Hz Sequenced). 봇은 Snapshot의 다른 id를 모두 플레이어로 본다.
- **입력 u16의 16비트가 모두 쓰인다.** `InputButtons` 주석에 "Phase 19 차량은 기존 비트를 다시 쓴다"고 적혀 있다. 입력에는 MoveX(좌우)·MoveY(앞뒤)·Yaw·Jump·Sprint·Interact가 있다.
- **움직이는 물체에 타는 선례:** `DropTransport.Ride`(Transport 모드: 위치를 경로로 정하고, 행동 불가, 총·폭발·자기장 대상 아님). 예측은 `LocalPlayerPredictor`가 같은 Shared 함수로 한다.
- **충돌은 모두 축 정렬 상자다.** `CollisionWorld.Gather`(정적 상자·닫힌 문·채집 대상 6 m, 조각 칸 반경 1)는 Shared이고 서버와 Client가 같이 쓴다. 지형은 `HeightField.Height/Gradient`.
- **피해 연결 지점:** `TraceShot`(월드 → 조각 → 되감은 플레이어), `Explode`(플레이어 → 조각), 로켓 충돌(`StepProjectile`).
- **매 Tick 바뀌는 비플레이어 물체의 선례가 없다.** 투사체·Supply Drop·수송기는 양쪽이 계산할 수 있는 곡선이라 사건만 보낸다. 운전하는 차량은 계산할 수 없다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 범위 | 요청서 STEP 9(§86–§95) |
| 진행 방식 | 2026-10-08 "페이즈마다 코드리뷰 하고 계획된 페이즈 전부 계속 진행". 추천안으로 진행하고 이유는 이 문서에 남긴다. 입력은 이미 정했다: 타기·내리기 = Interact, 가속 = Sprint, 제동 = Jump, 새 입력 비트 없음. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **차량 하나, 좌석 2개.** 4륜 차량 한 종류. 좌석 0 = 운전석, 좌석 1 = 조수석(타고 내릴 수 있지만 행동 없음). 좌석은 배열이라 더 늘릴 수 있다. 경기당 최대 차량 `VehicleSettings.MaxVehicles = 8`. | §88 "Driver·Passenger 개념을 지원할 구조". 조수석은 좌석 배열 하나로 거의 공짜다. | 좌석 수는 상수. |
| D2 | **운동 모델(Shared `VehicleSimulation.Step`, 서버와 운전자 예측이 같이 쓴다).** 상태 `VehicleMove`: Position, Heading(도), Speed(m/s, 부호 = 앞/뒤), Steer(-1..1). 입력: MoveY = 가속(+)/후진(−), MoveX = 조향, Jump = 제동, Sprint = 부스트.<ul><li>가속 8 m/s², 부스트 12 m/s²·최고 26 m/s, 기본 최고 20 m/s, 후진 최고 6 m/s, 제동 감속 24 m/s², 입력 없음 감속 4 m/s²(굴러감), 반대 방향 입력은 제동과 같다.</li><li>회전 속도 = Steer × 75°/s × min(1, \|Speed\| / 5) × 부호(Speed). 멈춰 있으면 돌지 않는다. Steer는 입력으로 바로 바뀐다(표시용으로만 부드럽게).</li><li>높이: 차체 중심과 앞·뒤 축의 지형 높이 중 가장 높은 값(`HeightField.Height`). 공중 상태·점프 없음. 진행 방향의 지형 기울기가 0.8을 넘으면 막힌다. 맵 경계 ±78 m에서 막힌다.</li><li>막힘: 한 Tick 이동 뒤 차체 발자국(앞·뒤 축 중심의 2.2 m 정사각형 둘, 높이 지형 + 0.5 ~ + 1.8 m)이 `CollisionWorld.Gather`의 상자·경사면 경계와 겹치면 그 Tick의 이동과 **회전을 모두** 취소하고 속도를 0으로(정지). 회전도 되돌려야 벽에 박힌 채로 돈 차체가 다음 Tick에 그 벽을 '이미 겹침'으로 보고 통과하지 않는다. **시작 위치에서 이미 겹친 상자는 무시한다**(끼임 방지).</li><li>운전자가 없으면 매 Tick 제동 입력과 같다(내리거나 끊긴 운전자의 차량이 굴러가지 않는다).</li><li>차량끼리는 서로 막지 않는다(Known issue).</li></ul> | §90 "Position, Heading, Speed, Acceleration, Steering, Ground Check"를 그대로 따른다. 서버가 Unity Physics를 쓰지 않으므로 상자·지형만으로 계산한다. 축 정렬 상자 둘이면 기존 겹침 코드와 같은 성질이라 결정적이다. 공중을 빼면 예측 오차가 크게 줄어든다. | 수치는 Shared 상수. 회전된 상자가 필요하면 발자국 함수만 바꾼다. |
| D3 | **충돌 정책(§91, §93): "정지 + 충격 피해" 하나.** 지형 경사·맵 상자·닫힌 문·채집 대상·**건설 조각** 모두 같은 규칙: 차량이 멈추고, 충돌 직전 속도가 8 m/s를 넘으면 차량이 (속도 − 8) × 6 피해를 받는다. **건설 조각은 피해를 받지 않는다.** 플레이어: 차량과 플레이어는 서로 막지 않는다. 차량이 6 m/s 넘게 움직이며 팀이 아닌 살아 있는 플레이어의 몸 상자와 겹치면 그 플레이어가 속도 × 2 피해(공격자 = 운전자, 같은 플레이어에게 1 s에 한 번)를 받는다. | §93 "단순하고 일관된 방식 하나". 조각을 부수게 하면 차량이 건설 방어를 무력화하고, 조각 피해 복제·붕괴가 차량 예측과 엮인다. 플레이어를 막게 하면 차량 위치가 플레이어 이동 예측에 들어가야 해 오차가 커진다. | 조각 피해를 원하면 서버 규칙 한 곳만 더한다. |
| D4 | **복제: 새 `VehicleStates` 패킷(48, S→C, Unreliable 채널 0), Snapshot과 같은 Tick(15 Hz)에.** Client는 마지막으로 적용한 ServerTick보다 오래된 패킷을 버린다(Sequenced 채널 0을 쓰면 Snapshot과 순번을 공유해 Snapshot 두 번째 조각이 버려질 수 있다). 헤더: PacketId, ServerTick u32, AckInputSeq u32(받는 사람의 것), Count u8 = 10 B. 기록(19 B): Id u8, State u8(Active 0, Wrecked 1), Driver u16, Passenger u16(Entity id, 0 = 빔), Position int16×3(1/256 m), Heading u16, Speed int16(1/256), Steer i8(/127), Health u16. 최대 8대 = 162 B.<br>**관심 영역(§94):** 받는 사람 몸에서 120 m 안의 차량 + 자기가 탄 차량만. 죽은 사람·관전자는 모두. 패킷에 없는 차량은 Client가 1 s 뒤 숨긴다. | Snapshot 기록은 꽉 찼고 모드 비트도 없다. 별도 패킷이면 Snapshot·봇·엔티티 예산을 건드리지 않는다. 상태 전체를 매번 보내 신뢰 전송이 필요 없다(생성·파괴 사건도 따로 없다). **차량 id(u8)는 숨김 창(1 s) 안에 다시 쓰지 않는다:** 쓰는 중인 id를 건너뛰는 카운터(1..255)라, 사라진 잔해가 새 차량으로 보간되거나 소리를 내지 않는다. | 차량 종류가 늘면 기록에 종류 1 B. |
| D5 | **타고 있는 플레이어(MovementMode는 그대로).** 서버 `PlayerEntity`에 `VehicleSlot`·`Seat`. 탄 동안: 서버는 이동 Step을 하지 않고 위치를 매 Tick 좌석 위치로 맞춘다(Snapshot에 그대로 나감), 행동(사격·건설·채집·회복·수류탄·줍기) 불가, 피해는 받지 않는다(차량이 대신 받는다, D7). 자기장 피해는 받는다. Client는 `VehicleStates`의 Driver·Passenger로 누가 탔는지 안다. 원격 플레이어 몸은 좌석 위치에 그린다.<br>**새 모드를 만들지 않는 이유:** 모드 3비트 8값이 꽉 찼고, Transport는 수송기 경로(`DropTransport.Ride`)와 묶여 있다. | Snapshot·이동 Step을 고치지 않고 서버 상태 하나로 끝난다. | 나중에 모드가 필요하면 Snapshot 형식 변경. |
| D6 | **타기·내리기(§89).** Interact 누름. 서버 검증: 차량 존재·Active, 빈 좌석(운전석 먼저, 없으면 조수석), 거리(발에서 차체 상자까지 ≤ 1.5 m, Shared 상수 `VehicleSettings.EnterRange`), 플레이어 살아 있음, Ground·Crouch 모드, 소생·재투입 진행 중 아님. Interact 우선순위: 소생·재투입 → Container·Supply Drop/문(기존) → **차량** → 줍기. 내리기: 탄 상태에서 Interact. 내릴 자리 = 운전석 쪽 옆 2 m → 반대쪽 → 뒤 → 앞 순서로 서 있을 수 있는 첫 자리(`CanStand`). 없으면 내리지 않는다. 자동으로 내리는 경우: 차량 파괴, 연결 끊김(유예 시작 때), 탈락·기절(자기장), 경기 끝·라운드 리셋. **강제로 내릴 때는 자리가 없어도 내린다:** 같은 순서로 찾고, 모두 막혔으면 차량 위(차체 위 2 m)에 내려놓는다(떨어지며 이동 Step이 바로잡는다). 기절·탈락은 내린 **뒤** 처리해 `DropEverything`이 내린 자리에 떨어뜨린다. **내리기는 Interact 처리에서 가장 먼저 본다**(소생·재투입 대상보다 앞; 그러지 않으면 기절한 팀원 옆에서 내릴 수 없다). 타기·내리기는 예측하지 않는다(서버 상태를 기다린다). | §89 목록 그대로. 줍기보다 앞에 두어 차량 옆 아이템이 타기를 막지 않게 한다. | 우선순위는 한 줄. |
| D7 | **피해·파괴(§92).** 체력 400(`vehicles.json`). 히트스캔: `TraceShot`에서 조각 다음, 플레이어와 같이 **되감은** 차량 상자(차량 위치 기록 32칸)와 시선 검사, 피해 = 무기 피해 × 1. 로켓은 차체에 닿으면 폭발, 폭발은 차체 상자까지 거리로 피해(시선 검사). 수류탄은 차량을 통과한다(Known issue). 탄 사람은 직접 맞지 않는다. 체력 0 → Wrecked: 탄 사람을 내리고 각자 25 피해(원인 Explosion), 5 s 뒤 사라짐. **공격자 기록:** 충격 피해는 공격자 없음. 파괴 피해의 공격자는 최근 10 s 안에 차량에 피해를 준 **적**(탄 사람과 팀이 아닌) 중 마지막 사람, 없으면 공격자 없음(자기장 피해와 같은 경로). 자기 팀 차량을 들이받아 부숴도 팀 처치로 기록되지 않는다. 같은 경기에서 다시 생기지 않는다. 쏜 사람에게 맞힘 확인은 보내지 않는다(차량 체력은 탄 사람 HUD와 연기 표시로만, Known issue). | §92. 되감기는 빠른 차량을 지연 있는 사수가 맞힐 수 있게 한다(플레이어와 같은 규칙). | 수치는 `vehicles.json`. |
| D8 | **생성 위치: Shared 맵 상수 `VehicleSpawns`(4곳, 길가·POI 밖, `ClearRadius` 4 m 테스트).** `StartMatch`에서 모두 생성, `CloseRound`·경기 끝에서 지움. 개발 모드(대기실)에는 없고 QA `spawnVehicle`로 만든다. 건설: 차량 상자와 겹치는 조각 설치는 거절(`Blocked`). | 맵 배치 상수는 기존 예외(Loot·Reboot)와 같은 방식. 설치 거절로 차량이 조각 안에 갇히지 않는다. | 위치는 상수. |
| D9 | **운전자 예측(§87).** 운전자 Client는 자기 차량을 `VehicleSimulation.Step`으로 입력마다 예측하고(이동 예측과 같은 기록 64칸·재실행), `VehicleStates`의 자기 차량 기록 + `AckInputSeq`로 맞춘다(위치·Heading·Speed 비교, 2 m 넘으면 바로 이동, 아니면 부드럽게). 충돌 후보는 Client의 같은 `CollisionWorld.Gather`. 탄 동안 이동 예측은 멈추고 플레이어 위치 = 예측 차량 좌석. 조수석·원격 차량은 보간(Render Tick, 이동 보간과 같은 지연). | 운전 조작감(§99 "Vehicle Handling")은 예측 없이는 왕복 지연만큼 늦다. 기존 이동 예측 구조를 그대로 쓴다. | 오차가 크면 보간으로 되돌리는 스위치. |
| D10 | **입력 재사용.** 탄 동안 서버는 MoveX/MoveY/Jump/Sprint를 차량 입력으로, Interact를 내리기로 읽는다. 카메라는 자유(Yaw는 차량에 영향 없음). 새 입력 비트 없음. | 입력 비트가 꽉 찼다. | 없음. |
| D11 | **Client 표시.** 원시 메시 차량(몸통 상자 + 바퀴 4 원기둥, 고정 풀 8), 앞바퀴 조향 표시, 지형 기울기로 차체 기울임(표시만), 체력 30 % 아래 연기(작은 상자 파티클 없이 회색 구), Wrecked는 검게. 카메라: 탄 동안 거리 8 m·높이 2.5 m 3인칭(`ShoulderCameraMath`에 차량 대상). HUD: 속도(km/h)·차량 체력 막대, 안내 "E 탑승" / "E 내리기". 오디오: 타기·내리기(문 소리 재사용), 충돌, 파괴(폭발 재사용). 엔진 연속음은 넣지 않는다(풀 구조가 단발음, Known issue). | 기존 원시 메시·HUD·오디오 구조를 그대로 쓴다. | 에셋은 나중에. |
| D12 | **서버 데이터 `vehicles.json`(서버 전용 값).** maxHealth 400, impactMinSpeed 8, impactDamagePerMps 6, runOverMinSpeed 6, runOverDamagePerMps 2, runOverCooldownSeconds 1, wreckSeconds 5, wreckOccupantDamage 25, interestRange 120. 예측해야 하는 운동 상수·EnterRange·좌석 위치는 Shared `VehicleSettings`. 다른 카탈로그처럼 범위 검사, 틀리면 서버가 시작하지 않는다. | 기존 `SquadCatalog` 방식. 예측 값은 Shared에 둔다는 기존 규칙. | 없음. |
| D13 | **Protocol v18.** `VehicleStates` 48. `PacketReader` 상한 48, 봇은 무시(카운터만), HeadlessActor는 자기 좌석·차량 상태를 Actor 상태로, Fuzz·크기 테스트. 서버 Tick 순서: 입력 → 플레이어(탄 사람 건너뜀) → **차량 Step(운전자 입력)** → 좌석 위치 맞춤 → 치기 판정 → 기록. 서버 핫패스: 차량 8대 × (Gather 1회 + 상자 겹침), 플레이어 치기는 차량마다 플레이어 순회(≤ 100). | 지난 Phase들의 누락(상한·봇·QA 파서) 방지. | 없음. |
| D14 | **QA(§95).** 서버 명령 `spawnVehicle`(x, z, heading → vehicleId), `damageVehicle`(id, amount), `/qa/vehicles`와 경로 루트 `vehicle.*`(count, `vehicle.<id>.health/state/speed/driver/position`). Actor 상태 `vehicleId`·`seat`. 시나리오(`QA/Scenarios/Vehicle/`): `vehicle_spawn`, `vehicle_enter`, `vehicle_drive`(moveVector로 앞으로 → 위치 바뀜, 조향, 제동 → 멈춤), `vehicle_exit`, `vehicle_damage`(사격 → 체력 감소), `vehicle_destroy`(→ Wrecked, 탄 사람 내림·피해, 5 s 뒤 사라짐), `vehicle_disconnect`(운전 중 끊김 → 좌석 비고 차량 멈춤, Resume 뒤 차량 옆), `vehicle_build_block`(조각에 부딪히면 정지·조각 무사). Unity 시나리오 `visual_vehicle`(타기, 운전 화면, HUD, 파괴 스크린샷). 서버 테스트: Step 결정성, 막힘·끼임 방지, 타기 검증 각 조건, 내릴 자리, 관심 영역, 되감은 히트스캔, 폭발, 충격·치기 피해. `stress-quick` + Network 변경이라 `stress-gameplay`(§96). | 요청서 §95 목록 그대로. | 없음. |
| D15 | **'탄 상태' 점검 목록(이동 모드가 Ground 그대로라 모든 Ground 검사가 걷는 것으로 본다).** 서버 `PlayerEntity.InVehicle`, Client는 `VehicleStates` 기준 `IsSeated(entityId)` 하나씩 두고 다음을 모두 고친다.<ul><li>서버: `ActionsAllowed`·`ProcessActions`(행동 불가), Interact(내리기 먼저), `UpdateChannel`·`TryStartChannel`(탄 동안 소생·재투입 시작·계속 불가), `FindReviveTarget`(탄 사람은 소생하는 사람이 될 수 없음), `TraceShot`·`ExplodePlayers`·로켓 충돌·치기(탄 사람 제외), 이상 이동 검사·낙하 피해·위치 기록(탈 때·내릴 때 기준 위치 재설정 — 좌석은 26 m/s로 움직이고 내리기는 2 m 순간이동), 기절·사망(먼저 내림), 자기장(받음).</li><li>Client: 이동 예측(탄 동안 멈춤, 내리면 Snap — 이동 기록이 낡았다), `PredictDoorToggle`(탄 동안 문 예측 없음: 내리는 E가 문 소리를 내지 않게), 발소리(탄 원격 플레이어는 Phase 18 순간이동 보호를 통과하는 속도로 움직인다 → 탄 사람은 발소리 없음), 안내 순서("E 내리기"가 먼저, "E 탑승"은 서버 우선순위의 Client 복사본 + 서버 비교 테스트, `DoorTests.TheClientsCopy_PicksTheSameDoor`처럼), 카메라 모드, 원격 몸 자세.</li><li>Client 오디오: 차량 상태 변화 소리(파괴·타기·내리기)는 Phase 18 기준 상태 규칙을 따른다: Join·Resume·관심 영역 진입·숨김 뒤 다시 보인 첫 기록은 기준일 뿐 변화가 아니다.</li></ul>**운전자 예측 인계:** 나를 Driver로 적은 첫 `VehicleStates` 기록과 그 ack에서 차량 예측을 시작하고, 탄 동안 이동 `Reconcile`을 건너뛰며, 내리면 이동 예측을 Snap한다. | 리뷰 전에 알려진 누락을 막는다(Phase 18에서 기준 상태·재전송 누락이 리뷰로 나왔다). | 없음. |
| D16 | **지금 넣지 않는 것.** 차량 종류 여러 개, 연료, 공중·점프·전복, 조수석 사격, 차량이 조각을 부수기, 플레이어와 차량의 상호 막힘, 엔진 연속음, 차량 맞힘 표시, 수류탄 튕김, 봇 운전, 차량끼리 충돌. | 범위를 지킨다(§86 "처음부터 여러 종류를 만들지 않는다"). | 다음에 더한다. |

## 검증 계획

1. Build, 서버·QA 테스트 두 번, Unity 밖 NUnit, Unity 복사본 EditMode.
2. QA: `suite:vehicle`(D14 시나리오), `smoke`, `pre-push`, 관련 스위트(weapons, building, squad, map, loot), Unity `suite:unity`(audio 포함).
3. `stress-quick`, `stress-gameplay`(기준선 비교, 50 % 넘게 나빠진 지표 없음).
4. 리뷰: safety, server-hotpath, client-hotpath, queue-cache, server-concurrency 없음(GameLoop 스레드만) + 차량 정확성 리뷰 하나.

## 하네스 확인이 필요한 것

`game-core-rules` 4절 Shared 예외에 **차량 운동 계산(`VehicleSimulation`·`VehicleSettings`)과 `VehicleSpawns` 맵 상수**를 더해야 한다(예측과 서버가 같은 차량 결과를 내야 하고, 생성 위치는 맵 배치 상수). 피해·충돌 피해·치기·관심 영역·`vehicles.json`은 서버에만 둔다. 사용자 하네스 파일이라 이 Phase에서는 고치지 않고 마무리 보고의 확인 목록에 넣는다.
