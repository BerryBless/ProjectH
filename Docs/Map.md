# Map

Phase 6 기준. 설계 근거와 결정 D1–D14: `Docs/specs/2026-10-01-phase6-map-design.md`. 데이터는 모두 Shared `Shared/Runtime/Simulation`의 코드 상수이고, 서버와 Client가 같은 값을 쓴다.

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

## 박스 (`GameMap.Boxes`, 58개)

- **배치 규칙(`GameMapTests`):**
  - 128개 이하다.
  - 박스 둘레 1 m의 지형은 평평하다. 박스 바닥은 그 높이이거나 다른 박스 위다.
  - 같은 높이대의 두 박스는 옆으로 맞닿지 않는다. 간격은 0.25 m 이하(모서리 틈)이거나 캐릭터 폭 이상이다.
  - 중앙 광장(반지름 12 m)에는 박스가 없다.
- **건물:** 단층이다.
  - 벽은 3 m 높이, 0.5 m 두께다. 문은 1.5 m다.
  - 지붕은 벽 위에 얹는다.
  - 동·서 벽은 남·북 벽에서 0.25 m 떨어진다.

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
4. 넓이가 바뀌면 `zones.json`도 맞춘다. 첫 원이 맵 전체를 덮어야 한다(`ZoneDataTests.ShippedFile_CoversTheWholeMap`).
