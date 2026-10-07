# Phase 15 Minimap / Full Map / Ping — 구현 기록 (Spec과 다른 점)

설계: `Docs/specs/2026-10-08-phase15-map-ping-design.md`(D1–D15). 동작 설명: `Docs/Map.md` "지도 UI·Ping (Phase 15)", 패킷: `Docs/Networking.md`. 이 문서에는 구현하면서 Spec에 없던 것을 정했거나 Spec과 다르게 한 것만 적는다.

## 서버·Protocol

| # | 내용 | 이유 |
|---|---|---|
| S1 | 패킷 크기는 필드의 실제 합으로 정했다. `MapMarker` 10 B(Spec 11 B), `TeamMarkers` Ping 한 칸 16 B(Spec 15 B), 최대 163 B. 필드와 순서는 Spec 그대로다. | Spec 숫자가 필드 합과 맞지 않았다. |
| S2 | 리더 규칙: Enemy·Item은 대상 id ≠ 0, Location·Danger·WaypointSet·WaypointClear는 대상 id = 0. 어기면 `Malformed`(끊기 기준). | 잘못된 조합을 받는 쪽에서 막는다. |
| S3 | Enemy Ping 위치는 Ping한 순간 대상의 발 위치다. 대상이 맵(±80 m) 밖(수송기·낙하)이면 보낸 좌표의 Location이 된다. | 맵 밖 좌표가 팀 전체의 `TeamMarkers`를 Client 리더에서 거절당하게 하지 않는다. |
| S4 | Waypoint 지우기는 탈락한 팀원도 할 수 있다. 설정과 Ping은 살아 있거나 기절한 참가자만 된다. `ActionsAllowed`는 보지 않으므로 기절·수송기·낙하 중에도 Ping할 수 있다. | 자기 표시를 남기지 않게 한다. Ping은 Spec D6대로 기절을 허용한다. |
| S5 | 다른 팀의 재투입 카드는 Item Ping 대상이 아니다. | 카드는 주인 팀에게만 보인다(Phase 14). |
| S6 | 경기 시작에 새 팀마다, 경기 끝에 각 팀에 빈 `TeamMarkers`를 보낸다. 라운드 리셋에는 남은 표시가 있을 때만, 팀을 지우기 전에 보낸다. | Client 표시가 다음 판으로 넘어가지 않게 한다. |
| S7 | 속도 제한 수치도 `map.json`에 둔다(pingsPerSecond 2, pingBurst 4, maxMarkerPacketsPerSecond 20). 높이는 [지형 − 1, 지형 + 50 m]로 자른다(16층 × 3 m + 2 m). | 정책을 데이터로 둔다. |
| S8 | QA `FaultActions.IsClientPacket`에 `MapMarker`와 (원래 빠져 있던) `BuildEditRequest`를 넣었다. | 쓰레기 패킷이 우연히 40으로 시작하면 버킷이 조용히 버려 잘못된 패킷 수가 흔들릴 수 있었다. |
| S9 | 봇은 `TeamMarkers`를 자기 임시 배열에 먼저 읽고, 패킷 전체가 맞을 때만 View에 반영한다. | 리뷰 Low: 거절된 패킷이 목록 앞쪽을 덮어쓰고 수는 그대로 남던 문제. |

## Client

| # | 내용 | 이유 |
|---|---|---|
| C1 | Kill Feed를 미니맵 아래(−244 px)로 내렸다. | 오른쪽 위에서 미니맵과 겹쳤다. |
| C2 | Ping 광선은 버튼을 누를 때만 따로 300 m를 쏜다. 아무것도 맞지 않거나 맵 밖이면 보내지 않는다. | 무기 사거리와 무관하게 먼 곳도 Ping한다. |
| C3 | Danger의 위치는 첫 누름의 맥락 Ping 위치이고 대상은 0이다. id 0인 아이템은 Item Ping으로 고르지 않는다. | S2 규칙과 맞춘다. |
| C4 | 오른쪽 클릭 지우기는 내 Waypoint가 있을 때만 보낸다. 지도 클릭 요청은 살아 있을 때만 보낸다(지도 열기는 죽어도 된다). | 불필요한 요청을 보내지 않는다. |
| C5 | 미니맵 창은 맵 가장자리에서도 자르지 않는다. 맵 밖은 검은 테두리가 보인다. 다음 자기장 원은 축소가 끝나기(`ShrinkEndTick`) 전까지만 그린다. | 단순하다. 축소가 끝나면 다음 원을 모른다. |
| C6 | `/qa/status`에 지도 필드(uv)와 그 uv를 `MapProjection`으로 되돌린 월드 좌표 필드(값이 없으면 null)를 둔다. | QA 비교식은 산술을 못 한다. 월드 값으로 서버 값과 바로 비교하고, 같은 함수를 왕복하므로 그려진 위치를 검사한다. |
| C7 | QA 가상 마우스는 포인터 위치를 정할 수 없어 지도 클릭 Waypoint를 Unity에서 재현하지 않는다. Waypoint는 헤드리스 `waypoint` 동작으로 시험한다. | 도구 한계. |

## 측정

`stress-quick` 5/5 PASS(50명). Mixed Match Tick p95 0.334 ms. 지도 Tick 코드는 할당이 없다(`MarkersAndTheirTicks_AllocateNothing`).

**측정 필요(고치지 않음):** 미니맵에서 자기장 고리가 미니맵보다 훨씬 큰 투명 Quad로 그려진다(경기 초반 반지름 115 m → 약 790 px, `RectMask2D`가 잘라 냄). 화면에 실제로 그려지는 부분은 약 0.27 MP이고 대부분 클립에서 버려진다. Frame Debugger와 GPU 프레임 시간으로 초반·후반을 비교해 차이가 있으면 고리를 숨기거나 보이는 호만 그린다(리뷰 Low).

## 함께 고친 것

- `Network/invalid_packet.json`: 서버 리뷰 M5 이후 입력 폭주는 끊지 않고 버리기만 하는데, 시나리오는 여전히 끊기기를 기대해 HEAD에서도 실패했다. 이제 폭주가 잘못된 패킷 수에 더해지고 연결은 유지되는지 본다.
- `full-regression` 스위트에 Squad·Map을 넣었다.

## Known Issues

- 전체 지도에서 팀원 이름이 POI 이름과 겹칠 수 있다. 지도 아래 조작 안내가 무기 칸 번호와 살짝 겹친다.
- 지도를 연 채로는 움직일 수 없다(Spec D4).
