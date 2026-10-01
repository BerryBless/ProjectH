# Phase 11 Game UI — 설계 Spec

## Context

원래 요청서의 Phase 0–10은 끝났다. 요청서의 첫 번째·두 번째 목표(§58, §59)도 달성했다. §60은 "핵심 게임 플레이가 검증된 이후" 기능을 더한다고 한다. 지금 Client를 사람이 플레이하려면 다음 문제가 있다.

- **접속:** 개발용 IMGUI 패널(`DevConnectPanel`)로만 한다. Phase 0 spec은 이 패널이 게임 UI가 되면 안 된다고 정했다.
- **끊김:** 이유와 재접속 진행이 패널 글자로만 보인다.
- **경기 결과:** 화면 가운데 40pt 한 줄뿐이다.
- **전적:** 저장만 하고 볼 수 없다. Phase 9 D10에서 미뤘다.
- **메뉴:** 없다. Esc는 커서만 풀고, 풀린 상태에서도 시점이 돈다.
- **이름:** 다른 플레이어를 Entity Id로만 알 수 있다.
- **한글:** UI 문구는 영어다. 내장 폰트의 한글 표시를 확인하지 않았기 때문이다(Phase 3 D13).

**사용자가 고른 것(2026-10-01):** Phase 11은 "게임 UI"다. 진행 방식은 앞 Phase와 같다. 묻지 않고 추천안대로 하고, 추천 이유를 이 문서에 남긴다. Phase마다 푸시한다.

**성공 기준:**

- 게임을 켜면 타이틀 화면이 나온다. 주소·포트·이름을 넣고 접속한다. 접속 중, 실패 이유, 경기가 가득 참이 화면에 보인다.
- 경기 중 Esc를 누르면 메뉴가 열린다(계속, 내 전적, 접속 끊기, 종료). 메뉴가 열려 있는 동안 캐릭터는 움직이지 않고 시점도 돌지 않는다.
- 연결이 끊기면 끊김 화면이 나온다. 이유를 보여 주고, 자동 재접속 중이면 진행(n/3, 다음 시도까지 남은 초)과 취소를, 아니면 다시 접속·타이틀로 버튼을 보여 준다.
- 경기가 끝나면 결과 화면이 나온다. 순위/참가자, 처치, 승자 이름, 나를 죽인 플레이어, 다음 판까지 남은 시간이 보인다.
- 내 통계(경기 수, 승리, 처치, 사망, 피해, 생존 시간)와 최근 10경기를 서버에서 받아 보여 준다. DB가 없으면 "기록을 볼 수 없음"이 보인다.
- 다른 플레이어의 이름이 처치 알림(Kill Feed), 관전 표시, 결과 화면에 나온다.
- 모든 UI 글자는 한글로 보인다.
- Game Loop는 전적 조회 때문에 DB를 기다리지 않는다.
- 새 UI는 매 프레임 할당이 없다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| Phase 11 범위 | 게임 UI(추천): 접속·로비, 끊김·재접속 화면, 경기 결과, 내 전적·통계 |
| 진행 방식 | 묻지 않고 추천안대로 진행하고, 추천 이유는 이 문서에 남긴다. Phase마다 푸시한다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **UI는 코드로 만드는 UGUI다.**<ul><li>지금 HUD와 같은 방식이다: Scene·Prefab·에셋 없이 `Canvas`를 코드로 만든다.</li><li>버튼·입력칸이 있는 화면에만 `GraphicRaycaster`를 붙인다.</li><li>`EventSystem`과 `InputSystemUIInputModule`도 코드로 한 번 만든다(Input System만 켜져 있다).</li><li>새 화면 Canvas에는 `CanvasScaler`(1920×1080 기준, 가로·세로 0.5)를 쓴다. 기존 HUD 배치는 바꾸지 않는다.</li></ul> | 이미 쓰는 기술(Phase 1 D14)이라 새 패키지가 없다. UI Toolkit은 PanelSettings·테마 에셋이 필요하다. TextMeshPro는 Essentials import가 필요하다. 둘 다 "에셋 없이 코드로 구성" 규칙과 맞지 않는다(Phase 3 D13). | 디자이너가 Unity에서 화면을 편집하려면 Prefab으로 옮겨야 한다. 그때 바꾼다. |
| D2 | **한글 폰트는 OS 글꼴을 실행 중에 불러온다.**<ul><li>`Font.CreateDynamicFontFromOSFont`에 다음 이름을 차례로 준다: "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic".</li><li>모두 없으면 내장 `LegacyRuntime.ttf`를 쓴다.</li><li>`UiFont` 한 곳에서 만들고 기존 HUD도 이 글꼴을 쓴다.</li><li>UI 문구는 한국어다.</li></ul> | 에셋을 넣지 않고도 Windows·macOS에서 한글이 나온다. 글꼴을 저장소에 넣으면 라이선스와 용량 문제가 생긴다. | 한글 글꼴이 없는 OS에서는 글자가 네모로 나온다. 그때 라이선스가 맞는 글꼴(예: Noto Sans KR, OFL)을 에셋으로 넣는다. |
| D3 | **화면 흐름은 UnityEngine을 쓰지 않는 상태 기계(`UiFlow`)가 정한다.**<ul><li>상태: `Title`, `Connecting`, `InGame`, `Menu`, `Disconnected`, `Result`. 전적 창은 `Menu`와 `Result` 위에 겹쳐 연다.</li><li>입력: 네트워크 상태, 마지막 끊김 정보, 경기 상태, Esc, 버튼</li><li>출력: 보일 화면, 커서 잠금 허용 여부, 게임 입력을 막을지 여부</li><li>화면 클래스는 이 출력만 그린다.</li></ul> | 흐름 규칙을 Unity 없이 테스트할 수 있다. 서버 테스트 프로젝트가 소스 링크로 돌린다(Phase 6 ZoneMath와 같은 방식). 화면 코드는 그리기만 해서 단순해진다. | 화면이 늘면 상태를 더한다. |
| D4 | **타이틀 화면이 `DevConnectPanel`을 대신한다.**<ul><li>입력칸: 주소, 포트, 이름(= DevPlayerId, 1–32바이트 UTF-8, 앞뒤 공백 제거)</li><li>버튼: 접속, 종료</li><li>마지막 오류를 보여 준다. 마지막 입력값은 `PlayerPrefs`에 저장한다.</li><li>명령줄 자동 접속(`LaunchArgs`)은 그대로 둔다.</li><li>`DevConnectPanel`(IMGUI)은 지운다.</li><li>RTT·Entity Id·상태는 F1 디버그 표시(UGUI 한 줄)로 옮긴다.</li></ul> | UI가 두 벌이면 같은 상태를 두 곳에서 그려 어긋난다. 디버그 정보는 개발에 필요하므로 F1로 남긴다. 이름 검증은 서버 연결 요청 검증(`MaxDevPlayerIdBytes`)과 같은 기준이다. | 없음. |
| D5 | **Esc 메뉴.**<ul><li>경기 중(`InGame`) Esc는 메뉴를 열고 닫는다. 열면 커서가 풀린다.</li><li>버튼: 계속하기, 내 전적, 접속 끊기(타이틀로), 게임 종료</li><li>메뉴·결과·끊김 화면이 떠 있거나 커서가 풀려 있으면 게임 입력을 막는다. 이동·발사·조준은 0이고 시점도 돌지 않는다. 입력 패킷은 계속 보낸다(빈 입력). Phase 10 Input Timeout 때문이다.</li><li>메뉴가 닫혀 있을 때 화면을 클릭하면 커서를 잠근다(지금과 같다).</li></ul> | 서버 권한 게임이라 일시정지는 없다. 메뉴를 연 사람이 그 자리에서 멈추는 것이 예상하는 동작이다. 지금은 커서가 풀려도 시점이 도는 문제가 있다. | 메뉴를 연 채로 공격받을 수 있다. 의도한 동작이다. |
| D6 | **끊김 화면.**<ul><li>이유를 한국어로 보여 준다. 대상: `DisconnectCode`, LiteNetLib 이유(Timeout, 연결 실패 등), 연결 거절(`RejectReason`), 경기 가득 참(`JoinResult.MatchFull`).</li><li>자동 재접속 중이면 "재접속 중 (n/3) — k초 뒤 다시 시도"와 취소 버튼을 보여 준다.</li><li>재접속이 끝났거나 하지 않는 이유면 다시 접속, 타이틀로 버튼을 보여 준다.</li><li>`GameClient`는 끊김 정보(코드, 재접속 가능 여부, 다음 시도 시각, 마지막 Join 결과)를 읽기 전용으로 보여 준다.</li></ul> | Phase 10이 만든 이유 코드와 재접속을 사람이 이해할 수 있게 한다. 경기 가득 참은 지금은 로그 경고뿐이고 사용자에게 보이지 않는다. | 없음. |
| D7 | **결과 화면.**<ul><li>`MatchResult`(순위, 처치, 참가자, 승자 Id)를 보여 준다.</li><li>승자와 나를 죽인 플레이어의 이름도 보여 준다. 나를 죽인 플레이어는 내 `PlayerDied`의 `KillerId`이고, Zone이면 "자기장"이다.</li><li>다음 판까지 남은 초를 보여 준다(`MatchState.StateEndTick`).</li><li>버튼: 계속 관전(결과 창 닫기), 내 전적</li><li>다음 판이 시작하면(대기·시작 상태) 저절로 닫힌다.</li></ul> | 지금 결과 한 줄을 대신한다. 피해량·생존 시간은 결과 패킷에 없으므로 "내 전적"(DB)에서 본다. 결과 패킷은 바꾸지 않는다. | 없음. |
| D8 | **전적 조회(Protocol v9).**<ul><li>새 패킷:<ul><li>`StatsRequest`(Client→서버, 본문 없음)</li><li>`StatsResponse`(서버→Client): 상태(`Ok`, `NoRecord`, `Unavailable`, `Busy`), 누적 통계, 최근 최대 10경기(종료 시각 Unix 초, 판 번호, 인원, 순위, 처치, 피해, 생존 ms)</li></ul></li><li>**받기:** Network 스레드는 받은 요청을 bounded 큐(`StatsQueryQueue`, 32개, Reject)에 넣기만 한다. 큐가 차면 바로 `Busy`를 보낸다.</li><li>**처리:** 별도 Hosted Service(`StatsQueryService`)가 큐를 읽고 DB를 조회한다.<ul><li>자기 `MatchStore`(같은 연결 문자열)로 조회하고, 조회마다 3초 제한을 둔다.</li><li>결과는 Game Loop가 Tick마다 비우는 bounded 응답 큐(32개)에 넣는다. Game Loop가 그 peer가 아직 있으면 보낸다.</li><li>Persistence가 꺼져 있거나 DB 오류면 `Unavailable`이다.</li></ul></li><li>**속도 제한:** peer당 2초에 한 번이다. 넘는 요청은 조용히 버리고 센다. 잘못된 패킷으로 세지 않는다. 버튼을 연타한 사람이 Kick되면 안 된다.</li><li>**Client:** 전적 창이 열릴 때 한 번 요청한다. 응답이 올 때까지 "불러오는 중", 5초 안에 없으면 "응답 없음"을 보여 준다.</li></ul> | Game Loop는 DB를 기다리지 않는다(절대 규칙 4). 요청이 몰려도 메모리가 자라지 않는다(§44). 모든 패킷 송신을 Game Loop가 하는 지금 구조를 지킨다. 조회는 사람이 버튼을 누를 때만이라 부하가 작다. | 동시 조회가 많아지면 응답이 늦어진다. 그때 Service를 여러 개로 늘리거나 캐시를 둔다. |
| D9 | **이름 표시(Protocol v9).**<ul><li>`PlayerSpawned`에 `Name`(DevPlayerId, 최대 32바이트 UTF-8)을 더한다.</li><li>Client는 Entity Id → 이름 표를 둔다. 크기는 플레이어 수만큼이고, Despawn과 경기 상태 초기화 때 지운다.</li><li>이름은 Kill Feed, 관전 표시, 결과 화면에 쓴다.</li></ul> | 서버는 이미 이 값을 검증해서 갖고 있다. 이름이 없으면 결과와 처치 알림이 의미가 없다. Join 때 한 번 보내므로 Snapshot 크기는 그대로다. | 인증 단계에서 표시 이름이 따로 생기면 그 값으로 바꾼다. 형식은 같다. |
| D10 | **Kill Feed.**<ul><li>오른쪽 위에 최근 5개를 보여 준다. 각 줄은 6초 동안 보인다. 내용은 "가해자 ▸ 피해자"다(Zone이면 "자기장 ▸ 피해자").</li><li>`PlayerDied`로 만들고 고정 크기 링(5칸)에 담는다.</li><li>Kill Feed만 자기 Canvas를 쓴다. 다른 UI의 다시 그리기를 일으키지 않는다(ClientPerf 7번).</li></ul> | BR에서 누가 남았는지 아는 기본 정보다. 이미 오는 패킷만 쓴다. | 없음. |
| D11 | **성능.**<ul><li>숨긴 화면은 `SetActive(false)`라 다시 그리지 않는다.</li><li>글자는 바뀔 때만 다시 만든다. 남은 초 표시는 정수 초가 바뀔 때만 바꾼다.</li><li>새 UI 코드는 매 프레임 할당이 없다.</li><li>UI 문구 생성은 UnityEngine을 쓰지 않는 순수 함수(`UiText`)로 둔다. 서버 테스트 프로젝트가 소스 링크로 시험한다.</li></ul> | 기존 HUD와 같은 규칙이다(`MatchHudText`). 저사양 기기의 Frame Time을 지킨다. | 없음. |
| D12 | **지금 넣지 않는 것:**<ul><li>설정 화면(감도, 그래픽, 소리), 키 바꾸기</li><li>서버 목록·매치메이킹, 로그인·인증</li><li>미니맵·전체 지도</li><li>소리·UI 애니메이션, 다국어 전환(한국어만)</li><li>TextMeshPro, UI Toolkit, Prefab 기반 UI</li><li>기존 HUD 배치 변경(글꼴만 바꾼다), 모바일 배치</li></ul> | 사람이 한 판을 처음부터 끝까지 이해하고 할 수 있게 하는 것이 이 Phase의 범위다. 미니맵은 다음 후보로 남긴다. | 없음. |

## 1. 구조

**Shared(`Shared/Runtime/Protocol`):**

- `PacketId`
  - `StatsRequest` 22, `StatsResponse` 23
  - `PacketReader.TryReadPacketId`의 상한을 올린다.
- `ProtocolVersion` 9
- `StatsPackets.cs`(새 파일)
  - `StatsStatus` enum
  - `StatsResponse`: 쓰기·읽기, 최대 10행, 크기 검사
- `PlayerSpawned.Name`
  - 쓰기·읽기
  - 읽을 때 1–32바이트를 검증한다.

**Server(`Server/src/ProjectH.Server`):**

| 위치 | 변경 |
|---|---|
| `Net/NetworkListener` | `StatsRequest` 받기: Join한 peer만 받는다. 속도 제한을 하고 `StatsQueryQueue`에 넣는다. 큐가 가득 차면 `Busy` 응답을 응답 큐에 넣는다. |
| `Net/PeerState` | 마지막 전적 요청 시각 |
| `Persistence/StatsQueryQueue.cs`(새 파일) | 요청 큐: 32개, Reject. 요청 = peer id, peer 참조, DevPlayerId |
| `Persistence/StatsQueryService.cs`(새 파일) | Hosted Service. 조회하고 `StatsResponse`를 만들어 응답 큐에 넣는다. 예외를 밖으로 내보내지 않는다(Phase 9 D7과 같다). |
| `GameLoop` | Tick마다 응답 큐를 비운다(Tick당 최대 32개). peer가 같으면 보낸다. |
| `Game/Match` | `PlayerSpawned`에 이름을 넣는다. |
| `Diagnostics` | Health 줄과 Meter에 전적 조회 수, 버림, Busy, Unavailable을 더한다. |
| `Program` | 큐와 Service를 등록한다. 종료 순서는 Game Loop 다음이다. |

**Client(`Client/Assets/Scripts`):**

| 위치 | 변경 |
|---|---|
| `UI/UiFlow.cs` | D3 상태 기계. UnityEngine을 쓰지 않는다. |
| `UI/UiText.cs` | 끊김 이유, 결과, 전적, Kill Feed, 남은 초 문구. UnityEngine을 쓰지 않는다. |
| `UI/KillFeedModel.cs` | 5칸 링과 만료 시각. UnityEngine을 쓰지 않는다. |
| `UI/UiFont.cs` | D2 |
| `UI/UiFactory.cs` | Canvas, Scaler, Raycaster, 패널, 글자, 버튼, 입력칸 생성 도우미. `EventSystem`을 한 번 만든다. |
| `UI/TitleScreen.cs`, `MenuScreen.cs`, `DisconnectScreen.cs`, `ResultScreen.cs`, `StatsWindow.cs`, `KillFeed.cs`, `DebugOverlay.cs` | 화면 |
| `UI/UiRoot.cs` | MonoBehaviour. 화면을 만들고 `UiFlow` 출력을 화면에 반영하며 해제를 맡는다. |
| `Net/NetClient` | `StatsResponse` 이벤트, `RequestStats()`, `PlayerSpawned.Name` 전달, 마지막 Join 결과 |
| `Game/GameClient` | 끊김 정보와 이름 표를 읽기 전용으로 공개한다. 게임 입력 막기(D5)를 받는다. Esc는 커서를 직접 다루지 않고 `UiFlow`가 정한다. |
| `Game/MatchHud`, `CombatHud`, `InventoryHud`, `PoiLabel` | `UiFont`를 쓴다. 영어 문구는 한국어로 바꾼다. `MatchHud`의 가운데 결과 줄은 결과 화면이 대신한다. |
| `Bootstrap/GameBootstrap` | `DevConnectPanel` 대신 `UiRoot`를 만든다. |
| `Bootstrap/DevConnectPanel.cs` | 지운다. |

**봇:** `PlayerSpawned` 형식 변경에 맞춘다. 봇은 전적을 요청하지 않는다.

**Lock:** 새 Lock은 없다.

- 요청 큐와 응답 큐는 bounded Channel이다.
- 속도 제한 시각은 Network 스레드만 쓴다.
- 응답 송신은 Game Loop만 한다.

## 2. 테스트

- **Protocol:**
  - `StatsResponse`를 썼다 읽으면 같은 값이다(0행, 10행).
  - 잘린 데이터는 읽지 않는다.
  - 행 수 10을 넘으면 거부한다.
  - `PlayerSpawned.Name`을 썼다 읽으면 같다. 빈 이름과 33바이트 이름은 거부한다.
  - 버전은 9다.
  - Phase 10 Fuzz에 새 파서를 넣는다.
- **서버(통합, UDP):**
  - Join 전 요청은 무시한다.
  - Persistence가 꺼져 있으면 `Unavailable`이다.
  - 2초 안의 두 번째 요청은 응답이 없고 센다. Kick은 없다.
  - 큐가 차면 `Busy`다(시험용 큐 크기로).
  - 요청한 peer가 떠났으면 응답을 버린다.
  - 다른 플레이어의 `PlayerSpawned`에 이름이 있다.
- **MySQL(`PROJECTH_TEST_MYSQL`이 있을 때만):**
  - 경기를 저장한 뒤 요청하면 `Ok`와 통계·최근 경기가 온다.
  - 기록 없는 id는 `NoRecord`다.
- **Client 순수 코드(서버 테스트 프로젝트가 소스 링크로 돌린다):**
  - `UiFlow`의 모든 전이: 타이틀 → 접속 중 → 경기 → 메뉴 ↔ 경기, 끊김(재접속 중/아님), 결과 → 다음 판, 접속 실패, 가득 참, 메뉴에서 접속 끊기
  - 커서·입력 막기 출력
  - `UiText`의 이유별 문구(모든 `DisconnectCode`·`RejectReason`·LiteNetLib 이유), 결과·전적 형식(시간 형식, 기록 없음, 볼 수 없음)
  - `KillFeedModel`: 6개째에 가장 오래된 줄을 밀어낸다. 6초가 지나면 사라진다.
  - 이름 검증: 공백 제거, 바이트 길이
- **Unity:**
  - scratch UnityCompile로 Client 전체를 컴파일한다(경고·오류 0).
  - Editor 실행 확인(타이틀 → 접속 → 메뉴 → 끊김 → 결과 → 전적, 한글 표시)은 사용자에게 받는다. `Docs/Client.md`에 확인 순서를 적는다.
- **회귀:**
  - 기존 테스트가 모두 통과한다.
  - 봇 50명 부하에서 Tick p95가 Phase 10 기록(0.11–0.13 ms)과 같은 수준이다.

## 3. 문서

- `Docs/Client.md`: UI 구조, 화면 흐름(Mermaid 상태도), 글꼴, Unity 확인 순서
- `Docs/Networking.md`: Protocol v9(`StatsRequest`/`StatsResponse`, `PlayerSpawned.Name`)
- `Docs/Server.md`: 전적 조회 큐와 Service, Health 항목
- `Docs/Database.md`: 조회 경로
- `Docs/Architecture.md`: Phase 11 기준

## 4. 범위 밖

D12의 항목.

## 5. 계획 단계 변경

프로토타입(계획 `Docs/plans/2026-10-01-phase11-game-ui.md`)에서 정한 것이다.

| 변경 | 이유 |
|---|---|
| 조회 3초 제한은 토큰과 함께 `WaitAsync`로도 건다(D8). | MySqlConnector는 서버 인사(greeting)를 기다리는 동안 취소를 무시한다(측정: `Connection Timeout=4`에서 4.05초). 제한을 넘은 조회는 연결 시간 제한까지 뒤에서 끝나며, 그 수도 연결 시간 제한으로 묶인다. |
| 전적 카운터는 넷이 아니라 다섯이다. 버림을 요청 버림(`limited`)과 응답 버림(`undelivered`)으로 나눈다(D8). | 속도 제한으로 버린 요청과 peer가 떠나 못 보낸 응답은 원인이 다르다. |
| 접속 중 화면에 취소 버튼을 둔다(D3, D4). | 닿지 않는 주소에 접속하면 실패까지 몇 초가 걸린다. 그동안 할 수 있는 일이 있어야 한다. |
| 누적 통계는 uint32 6개이고, 총 생존 시간은 초 단위다(D8). | ms 누적은 32비트를 쉽게 넘는다. 화면은 초 단위로 충분하다. |
| 전적 창을 2.5초 안에 다시 열면 직전 응답을 다시 쓴다(D8). | 서버의 2초 속도 제한에 걸려 응답 없이 5초 뒤 "응답 없음"이 뜨는 일을 막는다. |
| Kill Feed는 `GameClient`가 갖는다(파일은 `UI/KillFeed.cs`)(D10). | `PlayerDied`와 이름 표를 갖고 있는 곳이 `GameClient`다. |
| `NetClient.LastError`는 로그용 영어로 둔다. 화면 문구는 `UiText`가 만든다(D6). | 화면 문구를 한 곳(`UiText`)에서만 만든다. |
| F1 디버그 표시도 자기 Canvas를 쓴다(D10, D11). | 매 초 바뀌는 RTT가 다른 UI를 다시 그리게 하지 않는다. |
| 재접속 줄의 구분 기호는 `—` 대신 ASCII `-`다(D6). | OS 글꼴에 따라 `—`가 없을 수 있다. |
| 접속 직후 한 번 클릭해야 움직인다(D5). | 커서가 풀려 있는 동안 게임 입력을 막는 규칙의 결과다. Phase 10까지는 커서가 풀려 있어도 움직였다. |
| 이름 규칙은 "UTF-8 1–32바이트"가 아니라 "올바른 UTF-8 1–32바이트, 제어 문자(C0, DEL, C1)·서식 문자(Unicode Format: 폭 0 문자, 방향 제어, BOM)·줄/문단 구분자 없음"이다. 규칙은 Shared `ProtocolConstants.IsValidPlayerName` 하나이고, 서버의 연결 요청 검사(`ConnectRequestData.TryRead`, 어기면 `BadRequest`), 타이틀(`UiText.TryNormalizeName`), 봇 옵션이 함께 쓴다. `Match`는 `PlayerSpawned`가 넘치면 보내지 않고 세며(`SpawnEncodeFailures`) 첫 번만 Error 로그를 남긴다(최종 리뷰 A1). | 깨진 바이트는 대체 문자(3바이트)로 읽혀 11바이트만 깨져도 33바이트가 된다. 그러면 `PlayerSpawned`에 이름을 못 써 모든 Client가 그 Spawn을 버리고, 그 플레이어는 안 보이는데 쏠 수 있었다. 제어 문자와 줄 구분자는 화면의 줄을 깬다. 서식 문자는 보이지 않아 다른 사람의 이름을 흉내 낼 수 있다(최종 재검토에서 더함). |
| 모든 UI Text는 Rich Text를 끈다(`supportRichText = false`)(최종 리뷰 A2). | 이름이 Kill Feed·결과·관전 줄에 그대로 나온다. `<color=red>` 같은 이름이 태그로 해석되면 안 된다. 태그를 쓰는 문구는 없다. |
| 접속 중·끊김 화면에서 Join과 새 `MatchResult`를 같은 프레임에 보면 결과 화면으로 간다. 새 결과는 지난번보다 큰 수일 때만이다(최종 리뷰 A3). | `Finished` 중 Resume은 `JoinMatchResponse`와 `MatchResult`가 한 Tick에 온다. 게임 화면으로 가면 결과를 잃었다. |
| 전적 요청은 Join이 성공한 연결(`Ok`, `Resumed`)만 받는다. Game Loop가 `PeerState.Joined`(volatile)를 쓰고 수신 스레드가 읽는다(최종 리뷰 B4). | 전에는 Join을 보낸 것만 봤다. `MatchFull`로 거절된 연결도 닫히기 전 1초 동안 DB 조회를 할 수 있었다. |
| 큐에서 5초(`StatsQueryQueue.MaxQueueAgeMs`)보다 오래 기다린 전적 요청은 DB를 조회하지 않고 `Unavailable`로 답한다(최종 리뷰 B5). | Client는 5초 뒤 "응답 없음"을 띄우고 그 답을 기다리지 않는다. 밀린 요청이 빨리 빠진다. |
| 제한을 넘겨 버린 전적 조회는 그 자리에서 취소한다(`CancelAsync`). 토큰 원천은 조회가 끝난 뒤 지운다. | 전에는 `using`이 토큰 원천을 먼저 지워 `CancelAfter`가 울리지 않았고, 이미 실행 중인 명령이 자기 시간 제한까지 돌았다. 취소 콜백(MySqlConnector의 KILL QUERY)이 조회 Task를 막지 않게 비동기로 부른다. |
| 끊김 화면의 재접속 취소는 연결됐지만 Join 답을 기다리는 시도도 끊김 이벤트 없이 버린다(`NetClient.CancelConnect`). 다시 접속은 연결이 완전히 끊긴 뒤에만 눌린다. 명령줄 자동 접속은 `PlayerPrefs`에 저장하지 않는다. | 끊김 이벤트가 나면 화면의 이유가 "연결을 끊었습니다."로 바뀌었다. 닫히는 중에 누른 다시 접속은 무시되는데 화면만 "접속하는 중"이 됐다. 테스트용 실행이 사람이 마지막에 친 값을 덮으면 안 된다. |
