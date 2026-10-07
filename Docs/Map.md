# Map

Phase 12 기준(문 5개 추가). 설계 근거와 결정 D1–D14: `Docs/specs/2026-10-01-phase6-map-design.md`, 문: `Docs/specs/2026-10-02-phase12-deployment-traversal-design.md` D9. 데이터는 모두 Shared `Shared/Runtime/Simulation`의 코드 상수이고, 서버와 Client가 같은 값을 쓴다.

## 크기와 경계

- 160 × 160 m. 바깥벽 안쪽 면이 ±80 m에 있다. 벽은 높이 4 m, 두께 1 m다.
- 모서리는 0.25 m 틈(`GameMap.CornerSlit`)이다. 캐릭터(0.7 m)보다 좁아 빠져나갈 수 없다.

## 지형 (`HeightField`, `Hill`, `GameMap.Terrain`)

- **격자:** 원점 (−80, −80), 칸 2 m, 꼭짓점 81 × 81이다. 칸마다 (0,0)–(1,1) 대각선으로 삼각형 2개를 만들고, 높이는 삼각형 위에서 선형이다. 이동, 서버 사격(`HitScan.TraceTerrain`), Client Mesh·MeshCollider(`TerrainMesh`)가 같은 삼각형을 쓴다.
- **높이 계산:** 꼭짓점 높이는 언덕 목록에서 정수(1/32 m)로 계산한다. 언덕끼리 겹치면 큰 값을 쓴다. Client와 서버가 비트까지 같은 높이를 얻는다.
- **언덕:**

  | 이름 | 중심 (m) | 높이 | 평지 반지름 | 바깥 반지름 |
  |---|---|---|---|---|
  | Lookout 고지대 | (44, −44) | 6 m | 8 m | 22 m |
  | 북쪽 언덕 | (0, 46) | 4 m | 0 | 18 m |
  | 동쪽 언덕 | (50, 4) | 3.5 m | 0 | 16 m |
  | 서쪽 언덕 | (−50, 0) | 4.5 m | 0 | 18 m |
  | 남쪽 언덕 | (−4, −48) | 5 m | 0 | 20 m |

- **규칙(`GameMapTests`):**
  - 모든 삼각형의 기울기는 `MoveSettings.MaxSlope` 0.6 이하다. 지형은 걸음을 막지 않는다.
  - 가장자리 10 m와 중앙 광장은 높이 0이다.
  - 높이는 0 이상 12 m 이하다.
- **이동:** 오르막에서는 발을 지형 위로 올린다. 걷는 중의 내리막에서는 땅에 붙인다(`Networking.md` "이동 충돌").

## 박스 (`GameMap.Boxes`, 54개)

- **배치 규칙(`GameMapTests`):**
  - 128개 이하다.
  - 박스 둘레 1 m의 지형은 평평하다. 박스 바닥은 그 높이이거나 다른 박스 위다.
  - 같은 높이대의 두 박스는 옆으로 맞닿지 않는다. 간격은 0.25 m 이하(모서리 틈)이거나 캐릭터 폭 이상이다.
  - 중앙 광장(반지름 12 m)에는 박스가 없다.
- **건물:** 단층이다.
  - 벽은 3 m 높이, 0.5 m 두께다. 문 틈은 1.5 m다. 틈에는 문이 들어간다("문").
  - 지붕은 벽 위에 얹는다.
  - 동·서 벽은 남·북 벽에서 0.25 m 떨어진다.
- **Vault로 넘는 높이(Phase 12, `Movement.md` "판정"):** 박스 윗면의 높이(발 기준)로 정해진다. 장애물 앞 0.8 m 안에서 Jump를 누르면 다음과 같다.

  | 윗면 − 발 | 동작 | 맵의 예 |
  |---|---|---|
  | 0.5–1.1 m | Hurdle(달리는 중일 때만. 걸으면 보통 점프) | 1.0 m 낮은 상자(Gearworks (36, 40)·(57, 40), Stonefield 낮은 박스 등) |
  | 1.1 초과–2.1 m | Mantle(윗면 가장자리 안쪽 0.4 m에 선다) | 1.5 m 상자(Gearworks (34, 60)·(58, 60), 들판 엄폐물). 2.0 m도 같다 |
  | 2.1 m 초과 | 불가(보통 점프) | 3 m 벽, 3.25 m 지붕, 4 m 외곽벽 |

  도착점에 설 공간이 없거나, 도착점까지의 직선에 다른 상자가 있으면(문·얇은 벽 너머) Vault는 없다. 지형 위 박스는 발이 선 층에서만 센다(바닥이 발보다 0.3 m 넘게 높은 박스는 장애물이 아니다).

## 문 (`GameMap.Doors`, 5개, Phase 12 D9)

- **위치:** 문은 건물 벽의 문 틈(1.5 m)을 채운다. `Doors`의 순서가 `DoorStates`의 비트 번호다(bit i = `Doors[i]`).

  | 번호 | 건물 | 벽 | 중심 (X, Z) |
  |---|---|---|---|
  | 0 | Rustvale 집 1 | 남 | (−54, 50.25) |
  | 1 | Rustvale 집 2 | 남 | (−38, 48.25) |
  | 2 | Rustvale 집 3 | 북 | (−50, 41.75) |
  | 3 | Gearworks 창고 | 북 | (46, 55.75) |
  | 4 | Gearworks 창고 | 남 | (46, 44.25) |

- **크기:** 폭 1.5 m, 높이 3 m(벽과 같다), 두께 0.2 m(`GameMap.DoorThickness`). 벽 두께(0.5 m) 한가운데에 선다. 바닥은 높이 0이다.
- **`Boxes`에 들어가지 않는 이유:** 문은 열려 있을 수 있고, 닫힌 문은 양옆의 벽 조각에 옆으로 닿는다. `Boxes`에는 "옆으로 닿는 상자가 없다"는 규칙이 있다(박스를 하나씩 밀어내므로 면을 공유하는 두 상자 사이에서 캐릭터가 갇힐 수 있다). 그래서 문은 따로 두고, 닫혀 있을 때만 충돌 세계에 넣는다.
- **충돌 세계:** 이동·사격·Vault 판정 모두 `Boxes` 뒤에 닫힌 문을 붙인 배열을 쓴다. 서버는 `DoorSet`이, Client 예측은 `PredictedDoors`가 같은 순서로 만든다. 문이 바뀔 때만 배열의 문 부분을 다시 쓴다(Tick마다 할당이 없다). 열린 문은 세계에 없다.
- **E 규칙(`DoorRules.FindTarget`, 서버. Client 복사본은 `DoorRule`):** E를 누르면 문이 줍기보다 먼저다. 발에서 문 중심까지 수평 2.5 m 안, 바라보는 방향에서 좌우 ±60° 안, 같은 층(발이 문 바닥 1 m 아래부터 문 윗면까지)의 가장 가까운 문이 대상이다. 닫혀 있으면 열고, 열려 있으면 닫는다. 대상 문이 없으면 줍기다.
- **닫기 조건:** 문 자리에 살아 있는 캐릭터가 하나라도 겹쳐 있으면(겹침은 그 캐릭터의 모드 높이로 본다) 닫히지 않는다. Vault 중인 캐릭터는 남은 직선 경로(현재 위치 → 위치 + 속도 × 남은 Tick, 서 있는 상자로 쓸어 낸 공간)도 문 자리를 차지한 것으로 본다. Vault는 충돌 없이 움직이기 때문이다(최종 검토 B5).
- **지연 보상:** 문은 되감지 않는다. 사격은 지금 닫혀 있는 문에 막힌다.
- **밀치기:** 달리는 중이거나 슬라이드 중에 닫힌 문에 막히면 그 문이 열린다. `Step`이 막은 상자를 알려 주고(`StepResult.BlockedBy`, Z 축은 `BlockedByZ`, `Charging`) 둘 중 하나가 문이면 서버가 연다(`DoorSet.DoorBlocking`, Client `PredictedDoors.DoorBlocking`). 문틀 쪽으로 비스듬히 달려 들어가 X 축은 문틀, Z 축은 문에 막혀도 열린다. 공중에서는 달리기가 아니라 밀치지 못한다. 걷기로는 안 열린다. 그 Tick의 이동은 막힌 채로 끝나고 다음 Tick부터 이어진다. 봇은 달리기로 자연히 연다.
- **전달:** 문이 바뀐 Tick의 끝에 `DoorStates`를 모두에게 보낸다. Join·Resume 때 새로 온 사람에게도 보낸다. 사격은 현재 문 상태로 추적하고 문을 되감지 않는다(고정 박스와 같다). 닫힌 문은 총알을 막는다.
- **라운드 시작:** 경기 시작 Tick에 모든 문이 닫힌다(그 전에 열려 있던 문도).

## 채집 대상 (`GameMap.Harvestables`, 41개, Phase 13 D6)

- 나무 23개(1 × 4 × 1 m), 바위 8개(2 × 1.2 × 2 m), 잔해 6개(3 × 1.5 × 2 m), 상자 4개. 상자 4개는 Phase 12까지 `Boxes`에 있던 엄폐물((58, 60), (8, −24), (70, −10), (−70, 20))을 옮긴 것이다. 그래서 `Boxes`는 54개다.
- 배열 순서가 id이고 `HarvestStates`의 비트 번호다(최대 64개, `MaxHarvestables`).
- 서 있는 동안 이동·사격·채집·건설 검사에서 박스처럼 막는다. 부서지면 빠지고 다음 경기·판에 다시 선다.
- 배치 규칙(`HarvestableMapTests`): 64개 이하이고 종류마다 하나 이상, 외곽벽 안·중앙 광장 밖의 평평한 지형에 서고, 같은 높이대의 박스·문·서로와 캐릭터가 지나갈 간격을 두며, Loot·투입 지점·문과 떨어져 있다. 상자 4개는 Loot가 없는 엄폐물이었던 것이다.
- 건설 격자(`BuildGrid`): 맵 전체가 5 m 칸 32 × 32, 3 m 층 16개다(`Building.md`).

## POI (`MapPois`)

| 이름 | 중심 | 반지름 | 내용 |
|---|---|---|---|
| Crossroads | (0, 0) | 12 m | 대기 장소. 평지, 박스 없음 |
| Rustvale | (−46, 46) | 17 m | 집 3채 |
| Gearworks | (46, 50) | 16 m | 창고 1동(문 2개), 상자 4개 |
| Lookout | (44, −44) | 22 m | 6 m 고지대, 위에 1.5 m 엄폐 벽 3개 |
| Stonefield | (−50, −54) | 17 m | 지붕 없는 벽 4개, 기둥 2개, 낮은 박스 2개, 2단 받침대 |

들판에는 엄폐물이 12개 있다. Client `PoiLabel`이 따라가는 발이 있는 POI 이름을 왼쪽 위에 띄운다(`PoiLookup`: 원 안에서 중심이 가장 가까운 것).

## Loot 지점 (`LootPoints`, 50곳)

- **테이블:**
  - `Floor`: 들판, 언덕
  - `Building`: 건물 안. 무기 50%다.
  - `Tower`: 박스 위, 고지대 위
- **고정 지점:** 중앙 8 m 원의 4곳은 통합 테스트가 걸어가는 지점이라 남겨 둔다.
- **높이:** 지형 위 지점은 정적 초기화 때 지형 높이로 정한다.
- **규칙(`LootPointsTests`):**
  - 박스와 겹치지 않는다.
  - 지형 위이거나 오를 수 있는 박스 위에 있다.
  - 서로 2 m 밖에 있다.
  - `Building` 지점은 지붕 아래에 있다.

## 투입 지점 (`DropPoints`, 24곳)

- **조건:**
  - 서로 20 m 이상 떨어진다.
  - 반경 4 m 안에 박스가 없다.
  - 벽에서 6 m, 중앙에서 20 m 이상 떨어진다.
  - 높이는 지형 높이다.
- **사용:** 경기 시작에 서버가 쓴다(`BattleRoyale.md` "경기 시작").

## 맵 바꾸기

1. `GameMap`(박스·언덕), `LootPoints`, `DropPoints`, `MapPois`를 함께 고친다.
2. `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~GameMapTests|FullyQualifiedName~LootPointsTests|FullyQualifiedName~DropPointsTests|FullyQualifiedName~MapPoisTests"`가 통과해야 한다.
3. 맵이 바뀌면 이동 결과가 바뀐다. `ProtocolConstants.ProtocolVersion`을 올린다.
   - 문을 바꾸면(개수나 순서) `GameMap.DoorCount`와 `DoorStatesPacket`의 비트 수(마스크 1바이트라 8개까지, 없는 비트는 읽기 실패)를 함께 본다. `Doors` 순서가 비트 번호이므로 순서를 바꾸면 프로토콜이 달라진다. `DoorTests`가 문 5개가 벽 틈을 채우고 다른 상자와 닿지 않는지 검사한다.
   - Vault 높이 구분(`MovementTuning`의 Hurdle·Mantle 높이)에 걸리는 박스 높이를 바꾸면 `VaultTests`의 맵 테스트(Gearworks 상자)도 본다.
   - 채집 대상을 바꾸면 `HarvestableMapTests`를 돌린다. 개수나 순서를 바꾸면 `HarvestStates` 비트가 달라지므로 프로토콜이 달라진다.
4. 넓이가 바뀌면 `zones.json`도 맞춘다. 첫 원이 맵 전체를 덮어야 한다(`ZoneDataTests.ShippedFile_CoversTheWholeMap`).
   - 넓이(`GameMap.HalfSize`)가 바뀌면 지도 표시의 맵 안 검사(서버 `Match.Map`의 `InMap`, `TeamMarkersPacket`의 ±`MapMarkerConstants.MaxHorizontal`)도 같이 바뀐다. int16 1/100 m 좌표는 ±327 m까지라 맵이 그보다 넓어지면 패킷 형식을 바꿔야 한다.

## 지도 UI·Ping (Phase 15)

설계 근거와 결정 D1–D15: `Docs/specs/2026-10-08-phase15-map-ping-design.md`. 패킷은 `Networking.md` "지도 표시 (Phase 15)". 이 절은 서버 규칙이다. 미니맵·전체 지도·월드 표지·입력(Client)은 Client 문서를 본다.

- **종류(D6, D7):** 팀 Ping 4가지(Location, Enemy, Item, Danger)와 개인 Waypoint 설정·삭제. Client가 맥락(조준 Raycast)으로 종류를 고르고 `MapMarker`로 보낸다. 서버는 중요한 것만 확인한다(D8).
- **누가(D6, D8):** 경기 중(또는 개발 모드)의 팀이 있는 참가자이고 살아 있거나 기절한 사람. 기절도 Ping할 수 있다(`ActionsAllowed`를 보지 않는다. 수송기·낙하 중에도 된다). 관전자·탈락자의 Ping과 Waypoint 설정은 버린다. Waypoint 지우기는 탈락한 팀원도 할 수 있다(전체 지도는 죽어서도 열린다).
- **위치(D7):** 수평 좌표가 맵 안(|x|, |z| ≤ 80)이 아니면 버린다. 높이는 [그 점 지형 높이 − 1 m, 지형 높이 + 건설 최고 높이 48 m + 2 m]로 자른다(탑·지붕 위 Ping).
- **Enemy(D8):** 대상이 다른 팀의 살아 있는 플레이어이고, 보낸 사람의 눈에서 대상 몸 가운데까지 `enemyPingRange`(150 m) 안이며, 맵 상자·닫힌 문·지형에 막히지 않아야 한다(`HitScan.TraceWorld`, `DoorSet.World`. 건설 조각·채집 대상은 보지 않는다: Phase 13.5 배치 시선과 같은 규칙). 대상 발이 맵 밖(수송기·자유 낙하)이어도 실패다. 확인되면 Ping 위치는 그 순간 대상의 발 위치다(따라다니지 않는다). 실패하면 보낸 좌표의 Location(대상 0)이 된다.
- **Item(D8):** 아이템이 월드에 있고(다른 팀의 재투입 카드는 그 팀만 보이므로 없는 것으로 본다) 보낸 사람 발에서 `itemPingRange`(60 m) 안이어야 한다. 위치는 아이템 위치다. 실패하면 버린다.
- **상한(D9):** 플레이어당 활성 Ping `pingsPerPlayer`(3), 팀당 `pingsPerTeam`(8, `TeamMarkers` 상한). 넘으면 보낸 사람의 가장 오래된 Ping을, 팀이 가득이면 팀의 가장 오래된 Ping을 바꾼다. Waypoint는 플레이어당 하나(새로 두면 옮긴다), 수명 없음.
- **수명(D9):** Location·Item·Danger `pingSeconds`(8초), Enemy `enemyPingSeconds`(4초). 끝 Tick은 `TeamMarkers`에 실려 Client가 같은 값을 안다.
- **전송(D10):** 팀 표시가 바뀐 Tick 끝에만 그 팀원에게 `TeamMarkers`(팀 전체 목록)를 보낸다. Join·Resume 때 받는 사람에게 보낸다. 다른 팀에는 절대 보내지 않는다.
- **정리:** 플레이어가 나가면 그 사람의 Ping과 Waypoint가 지워진다(유예 중에는 남는다). 경기 시작에 모두 지우고 새 팀마다 빈 목록을 보낸다. 경기 끝(`Finished`)에 모두 지우고 빈 목록을 보낸다(결과 화면에서는 새 요청을 받지 않는다). 라운드 리셋에 남은 표시가 있으면 팀이 지워지기 전에 빈 목록을 보낸다.
- **수치:** `map.json`(서버 GameData, 시작 때 검증, 틀리면 서버가 시작하지 않는다. `Server.md` "데이터 파일").
- **저장 구조와 비용:** 팀마다 고정 배열 8칸(`Match.Map`의 256 × 8 배열, 생성 때 한 번), 플레이어의 Waypoint는 `PlayerEntity` 필드. 요청 처리·만료·전송 모두 할당이 없다(`MapMatchTests.MarkersAndTheirTicks_AllocateNothing`). 만료 검사는 활성 Ping이 있을 때만, 전송은 바뀐 팀만 돈다.
- **관측:** Health 줄 `map pings enemyConfirmed enemyDemoted refused replaced expired waypoints packets markerDrops markerInboxDrops`와 `badPackets markerRate`, Meter `projecth.map.events`·`projecth.map.marker_drops`(`Server.md` "관측"). QA는 `/qa/players`의 `waypoint`·`teamPingCount`·`teamPings`·`teamWaypointCount`·`teamWaypoints`와 `/qa/health`의 `map`(`QA.md`).
