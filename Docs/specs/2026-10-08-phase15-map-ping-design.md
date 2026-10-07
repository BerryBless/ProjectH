# Phase 15 Minimap / Full Map / Ping — 설계 Spec

## Context

요청서는 `Docs/requests/2026-10-05-roadmap-phase13_5-19-request.md`의 STEP 5(§49–§58)다. 목표는 정보 전달 시스템이다: 미니맵, 전체 지도, 지도 좌표 공통 변환, 개인 Waypoint, 팀 Ping.

**지금 구조에서 확인한 사실(`phase14-squad` a6ea942 기준):**

- **UI:** 모든 UI는 코드로 만드는 uGUI다(`UiFactory`, 프리팹 없음). HUD는 `IDisposable` 클래스가 생성자에서 한 번 만들고 값이 바뀔 때만 갱신한다(`SquadHud`). 월드 표지 풀 선례는 `TeammateMarkers`(고정 3개)다.
- **입력:** M, Tab, 마우스 가운데 버튼, 휠은 비어 있다. UI 전용 키는 `WasPressedThisFrame`로 읽는다. 화면 상태는 순수 `UiFlow`(서버 테스트에 source link)가 정하고, 커서 잠금은 `SetUiControl`이 받는다.
- **자기장:** `ZoneState`에 현재(From)와 다음(To) 원, 축소 Tick이 있다. Client는 `ZoneMath.Sample`로 지금 원을 구한다. 축소가 끝나면 다음 원은 다음 Phase 패킷이 올 때까지 모른다.
- **수송기 경로:** `TransportRoute`(시작·끝 좌표, Tick)와 `DropRoute`가 Client에 있다. 대기실에서 지운다.
- **맵 데이터:** `GameMap`(160 m, `HeightField` 81 × 81, 상자, 문, 채집 대상), `MapPois` 5개, `RebootStations` 4개가 모두 Shared에 있고 결정적이다. 지도 그림을 실행 중에 만들 수 있다.
- **팀원 위치:** 관련성 필터가 없다. 모든 플레이어가 모든 Snapshot에 있다(15 Hz). 그래서 멀리 있는 팀원도 지도에 그릴 수 있다. 팀 구성은 `SquadState`에 있다.
- **서버 수신:** 패킷마다 수신 스레드에서 속도를 세고(`PeerState`), 유한 채널로 Game Loop에 넘긴다. 일반용 채널은 없다. `BroadcastTeam`이 팀에게만 보낸다. 사격 판정(`HitScan.TraceWorld`, `TracePlayer`)과 `WorldItems.IndexOf`를 검증에 쓸 수 있다.
- **Protocol:** v13, 마지막 PacketId 39.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 범위 | 요청서 STEP 5(§49–§58) |
| 진행 방식 | 2026-10-08 "페이즈 15까지 알아서 해줘". 추천안으로 진행하고 이유는 이 문서에 남긴다. Commit은 하고 Push는 하지 않는다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **지도 좌표 공통 변환(§51): Client 순수 클래스 `MapProjection`.** 월드 (x, z) ↔ 지도 정규 좌표 (u, v) ∈ [0, 1](북쪽 = +Z = 위), 그리고 정규 좌표 ↔ 지도 사각형(RectTransform 크기) 픽셀. 미니맵, 전체 지도, 지도 클릭 Waypoint 모두 이 함수만 쓴다. 범위는 `GameMap.HalfSize`에서 계산한다. 서버는 월드 좌표만 쓰므로 Shared에 두지 않는다. | 요청서 §51 그대로. 계산식이 한 곳이라 UI마다 어긋나지 않는다. 순수 클래스라 EditMode 테스트로 고정한다. | 없음. |
| D2 | **지도 그림: 실행 중 한 번 만드는 Texture2D(256 × 256).** `HeightField` 높이로 녹색 음영, 상자·문·채집 대상 발자국은 어두운 회색, 맵 경계 밖은 검정. 경기 내내 바뀌지 않으므로 캐시하고 Dispose에서 파괴한다. 순수 래스터 함수(높이 → 색 배열)와 Texture 생성을 나눈다. | 에셋 없이 맵과 항상 일치한다(맵을 바꾸면 그림도 바뀐다). 한 번만 만든다(저사양). | 그림이 단조로우면 색만 바꾼다. |
| D3 | **미니맵(§49).** 오른쪽 위 200 px 정사각형, 북쪽 위 고정. 플레이어 주변 60 m를 보여 준다(`RawImage.uvRect`를 움직임, 텍스처 재생성 없음). 표시: 나(흰 화살표, Yaw로 회전), 팀원(초록 점, 기절 빨강), 자기장 현재 원(흰 고리)·다음 원(파랑 고리), POI 점, Ping, Waypoint, Reboot Station. 미니맵 밖으로 나간 아이콘은 테두리에 붙인다(Ping·Waypoint·팀원만). `RectMask2D`로 자른다. | 요청서 §49 항목 그대로. 북쪽 고정이 구현·판독이 단순하다. | 회전 미니맵이 필요하면 `MapProjection`에 회전을 더한다. |
| D4 | **전체 지도(§50).** M으로 연다·닫는다. 화면 가운데 900 px 정사각형에 맵 전체를 그린다. 표시: 나, 팀원(이름), 현재·다음 자기장, 수송기 경로(선, 경로가 있을 때), POI 이름, Waypoint, Ping, Reboot Station(대기 중 회색).<ul><li>지도가 열려 있으면 커서를 풀고 게임 입력을 막는다(ESC 메뉴와 같은 방식, `UiFlow`의 `MapOpen` 상태). 이동도 멈춘다.</li><li>왼쪽 클릭: 그 지점에 내 Waypoint를 둔다(이미 있으면 옮긴다). 오른쪽 클릭: 내 Waypoint를 지운다.</li><li>Esc나 M으로 닫는다. 죽어도 열 수 있다(관전 중 팀 확인).</li></ul> | 요청서 §50 항목 그대로. 커서 처리를 기존 화면 흐름 하나로 한다. | 지도를 연 채 움직이고 싶다는 요구가 있으면 `BlocksGameInput`을 이동만 허용하게 나눈다. |
| D5 | **개인 Waypoint(§53).** 플레이어마다 하나. 지도 클릭으로 정하고 지운다. 서버에 보내고(D7) 서버가 같은 팀에 알린다(D8). 팀원의 Waypoint도 지도·미니맵·월드(작은 기둥)에 보인다. 경기가 끝나거나 라운드가 바뀌면 지운다. | "필요한 정보만 공유": 팀만 받는다. | 없음. |
| D6 | **팀 Ping(§54, §57).** 마우스 가운데 버튼.<ul><li>Client가 조준 Raycast(`PlayerViewFactory.AimRaycastMask`, 적 Collider 층 포함)로 맥락을 고른다: 적 플레이어 Collider → Enemy. 월드 아이템에는 Collider가 없으므로, 맞은 점에서 2 m 안의 가장 가까운 월드 아이템(`WorldItemList`) → Item. 그 밖(지형·건물·조각) → Location. 팀원 Collider는 Phase 14에서 꺼져 있다.</li><li>**Danger:** 가운데 버튼을 누르면 0.3초 기다린다. 그 안에 다시 누르면 Danger 하나를, 아니면 맥락 Ping 하나를 보낸다. 그래서 Danger도 요청·활성 칸을 하나만 쓴다.</li><li>종류는 네 가지뿐이다(§54 "너무 많은 Ping Type을 만들지 않는다").</li><li>살아 있거나 기절한 참가자만 Ping할 수 있다(관전자·탈락자 불가). **기절도 Ping할 수 있으므로** Ping 입력은 Client의 `canAct`·`ActionsAllowed` 검사와 서버의 `ActionsAllowed`를 지나지 않는다(별도 조건: 살아 있음 + 참가자). 테스트로 고정한다.</li></ul> | 요청서 §54, §57 그대로. 맥락은 Client가 고르고 서버가 확인한다. | 없음. |
| D7 | **C→S `MapMarker` 패킷(40, 11 B).** 종류(Location 0, Enemy 1, Item 2, Danger 3, WaypointSet 4, WaypointClear 5), x·y·z(int16, 1/100 m), 대상 id(u16: Enemy = Entity id, Item = 아이템 id(u16, `WorldItemData.ItemId`와 같은 폭), 그 밖 0). 11 B. 높이는 건물·조각 위 Ping 때문에 보낸다(탑·지붕 위가 주된 쓰임). 서버는 y를 [그 점 지형 높이 − 1 m, 지형 높이 + `BuildGrid.Levels × LevelHeight` + 2 m]로 자른다. 지도 클릭 Waypoint는 Client가 지형 높이를 넣는다.<ul><li>채널: 신뢰 채널 0, 새 유한 채널 `InboundChannels.Marker`(MaxPlayers × 4, DropOldest).</li><li>수신 스레드 속도 제한: 연결마다 토큰 버킷(초당 2, 한 번에 4). 넘으면 버리고 센다(`markerRate`). 초당 20을 넘으면 잘못된 패킷으로 센다(끊기 기준 공유).</li></ul> | 요청서 §55 "Client가 임의의 거대한 좌표를 보내지 못하게": int16 1/100 m라 ±327 m가 상한이고 서버가 맵 안(±80 m)으로 다시 확인한다. 기존 입력·통계 속도 제한과 같은 방식이다. | 없음. |
| D8 | **서버 검증(§55, §57).** 중요한 것만 확인한다.<ul><li>공통: 경기 중(또는 개발 모드), 보낸 사람이 참가자이고 살아 있거나 기절, TeamId ≠ 0, 좌표가 맵 안(|x|, |z| ≤ HalfSize).</li><li>Enemy: 대상이 살아 있는 **다른 팀** 플레이어이고, 눈에서 대상 몸 중심까지 150 m 안이고, 맵 상자·닫힌 문·지형에 막히지 않음(`HitScan.TraceWorld`). 실패하면 Location으로 바꾼다(대상 위치가 아니라 보낸 좌표). Ping 위치는 Ping한 순간의 대상 위치다(따라다니지 않는다).</li><li>Item: 아이템이 있고 보낸 사람에게서 60 m 안. 실패하면 버린다. 위치는 아이템 위치다.</li><li>Location·Danger·Waypoint: 맵 안이면 받는다. 높이는 위 범위로 자른다.</li><li>Enemy 시선은 맵 상자·문·지형만 본다. 건설 조각은 보지 않는다(Phase 13.5 배치 시선과 같은 규칙).</li><li>Client 맥락을 그대로 믿지 않는다(적이 아니면 Enemy가 되지 않는다).</li></ul> | 요청서 §57 "Server가 최종 유효성을 검증해야 하는 중요한 정보만": 적 표시는 팀에 정보를 주므로 확인한다. 위치 표시는 잘못돼도 해가 없다. | 없음. |
| D9 | **수명과 상한(§56).** Ping 수명 Location·Item·Danger 8초, Enemy 4초. 플레이어당 활성 Ping 3개(넘으면 가장 오래된 것을 바꾼다), 팀당 8개(같음). Waypoint는 플레이어당 1개, 수명 없음. 수치는 `map.json`(서버 GameData)에 둔다. 서버는 팀마다 고정 크기 배열(8)을 쓴다. | 무한 증가 방지(game-core-rules). 고정 배열이라 할당이 없다. | 수치는 JSON만 바꾼다. |
| D10 | **S→C `TeamMarkers` 패킷(41).** 받는 사람의 팀 표시 전체를 보낸다: Ping 수 + Ping마다 [id u8, 종류 u8, 주인 Entity id u16, x·y·z int16(1/100 m), 끝 Tick u32, 대상 id u16] 15 B, Waypoint 수 + Waypoint마다 [주인 u16, x·y·z int16] 8 B. 최대 3 + 8 × 15 + 4 × 8 = 155 B.<ul><li>팀의 표시가 바뀐 Tick 끝에만 그 팀에 보낸다(Ping 추가·만료·Waypoint 변경). Join·Resume에 보낸다.</li><li>Client는 받은 목록으로 통째로 바꾼다(멱등, 순서 무관).</li></ul> | 상태 전체가 작아서 사건 단위 동기화보다 단순하고 틀릴 일이 없다. `TeamState`와 같은 방식이다. | 팀 인원·Ping 상한을 크게 늘리면 사건 단위로 바꾼다. |
| D11 | **Client 월드 표시.** Ping마다 월드 표지(종류별 색: Location 노랑, Enemy 빨강, Item 하늘, Danger 주황)와 화면 거리 문구("23 m"). 풀 8개, 공유 Mesh·Material. 화면 밖이면 화면 가장자리 화살표. Waypoint는 같은 풀 방식의 기둥 4개. | 저사양: 매 프레임 생성 없음, 문자열은 거리가 정수 m로 바뀔 때만. | 없음. |
| D12 | **지도 성능(§52).** 아이콘은 종류별 고정 풀이다(팀원 3, Ping 8, Waypoint 4, POI 5, 스테이션 4). 매 프레임 위치만 갱신하고, anchoredPosition은 0.5 px 넘게 바뀔 때만 쓴다. 자기장 고리는 원 텍스처 한 장을 크기만 바꾼다. 경로 선은 경로가 바뀔 때 한 번 계산한다. 전체 지도가 닫혀 있으면 그 갱신을 건너뛴다. | 요청서 §52 그대로. | 없음. |
| D13 | **Protocol v14와 일괄 점검.** `MapMarker` 40(C→S), `TeamMarkers` 41(S→C). 빠뜨리면 안 되는 곳: `PacketReader.TryReadPacketId` 상한 41, `NetworkListener.Receive`에 `MapMarker` case(`JoinRequested` 확인, 없으면 `WrongDirection` 잘못된 패킷이 되어 Ping마다 끊기 기준에 쌓인다), `BadPacketReason`에 `MarkerRate`(Count 앞), BotConnection·BotView·HeadlessActor의 `TeamMarkers` 파싱, Fuzz 목록, `m`과 마우스 가운데 버튼을 Client `QaInput.KeyNames`와 QA `UnityActions.InputKeys`·`unityClick` 양쪽에. Phase 13.5에서 상한 하나를 빠뜨려 편집 요청이 모두 끊겼고, H 키가 한쪽에만 있어 QA가 거절했다. | 지금 규칙과 같다. | 없음. |
| D14 | **QA(§58).** 헤드리스 동작 `ping`(종류, 좌표 또는 대상), `waypoint`. Observation에 팀 Ping·Waypoint. 시나리오 `QA/Scenarios/Map/`: `minimap_position`(Unity: 미니맵 내 아이콘 정규 좌표가 실제 위치와 맞음, `/qa/status`에 지도 필드), `map_zone`(Unity: 지도 자기장 고리 크기·중심), `map_team`(팀원 Ping·Waypoint가 팀에게만), `ping_world`, `ping_enemy`(보이는 적 = Enemy, 벽 뒤·같은 팀 = Location), `ping_rate_limit`(초당 상한, 활성 상한, 수명 만료), 그리고 Unity 스크린샷 `visual_map`. | 요청서 §58 그대로. | 없음. |
| D15 | **지금 넣지 않는 것.** 지도 확대·이동(전체 지도는 고정 배율), 회전 미니맵, 탐험 안 한 지역 가리기, Ping 음성·채팅·Ping 응답("확인"), 적 Ping 추적, Ping 효과음 에셋(Phase 18), 봇의 Ping. | 범위를 지킨다. | 필요하면 다음 Phase에 더한다. |

## 검증 계획

- Client EditMode: `MapProjection`(경계·왕복·북쪽 위), 지도 래스터(크기·경계 밖 검정·상자 어두움), 미니맵 창 계산(가장자리 붙이기), Ping 맥락 선택(Collider → 종류, 두 번 누름 Danger), 거리 문구 갱신 조건, `UiFlow` `MapOpen`(Esc 우선순위, 커서).
- Shared: 패킷 왕복·경계·Fuzz.
- Server: 검증(맵 밖·관전자·Enemy 같은 팀·벽 뒤·거리, Item 없음·거리), 속도 제한, 플레이어·팀 상한(오래된 것 교체), 수명 만료, 팀에게만 전송, Join·Resume 재전송, 라운드 리셋 정리, 할당 없음.
- QA: D14 시나리오, `suite:smoke`, `suite:pre-push`, `suite:squad`, `suite:building`, `stress-quick`.
