# Phase 11 Game UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 사람이 Client로 한 판을 처음부터 끝까지 이해하고 할 수 있게 한다.
- 게임을 켜면 타이틀 화면이 나온다. 주소·포트·이름을 넣고 접속한다. 접속 중, 실패 이유, 경기가 가득 참이 화면에 보인다.
- 경기 중 Esc는 메뉴(계속하기, 내 전적, 접속 끊기, 게임 종료)를 연다. 메뉴가 열려 있으면 캐릭터는 움직이지 않고 시점도 돌지 않는다.
- 끊기면 이유와 자동 재접속 진행(n/3, 남은 초, 취소) 또는 다시 접속·타이틀로 버튼이 보인다.
- 경기가 끝나면 결과 화면(순위, 처치, 승자, 나를 처치한 플레이어, 다음 판까지 남은 초)이 나온다.
- 내 통계와 최근 10경기를 서버에서 받아 보여 준다. DB가 없으면 "기록을 볼 수 없음"이다.
- 다른 플레이어의 이름이 Kill Feed, 관전 표시, 결과 화면에 나온다. UI 문구는 한국어이고 한글이 보인다.
- Game Loop는 전적 조회 때문에 DB를 기다리지 않는다. 새 UI는 매 프레임 할당이 없다.

**Architecture:**
- **Protocol v9(Shared).** `PacketId` `StatsRequest` 22(C→S, 본문 없음)와 `StatsResponse` 23(S→C)을 더한다. `StatsResponse`는 상태(`Ok`, `NoRecord`, `Unavailable`, `Busy`), 누적 통계(uint32 6개), 최근 최대 10경기(행마다 20B)다. 최대 227B다. `PlayerSpawned`에 `Name`(DevPlayerId, 1–32B UTF-8)을 더한다.
- **Network 스레드(`NetworkListener`).** `StatsRequest`는 Join을 요청한 연결(`PeerState.JoinRequested`)에서만, 연결당 2초에 한 번만 받는다. 나머지는 답 없이 버리고 `limited`로 센다(잘못된 패킷이 아니다). 받은 요청은 `StatsQueryQueue`의 요청 채널(32개, Reject)에 넣는다. 가득 차면 `Busy` 답을 응답 채널에 넣는다.
- **`StatsQueryService`(Hosted Service).** 요청 채널의 유일한 소비자다. 자기 `MatchStore`로 통계와 최근 10경기를 읽고, 조회마다 3초 안에 답한다(`WaitAsync`). Persistence가 꺼져 있거나 DB가 실패하면 `Unavailable`이다. 답은 응답 채널(32개, Reject)에 넣는다. 예외를 밖으로 내보내지 않는다.
- **Game Loop.** Tick마다 `Match.Tick` 앞에서 응답 채널을 비운다(최대 32개). 그 peer가 `_peers`에 같은 NetPeer로 있을 때만 보내고, 아니면 버리고 `undelivered`로 센다. 모든 패킷 송신은 여전히 Game Loop만 한다. Health 줄과 Meter(`projecth.stats_queries`)에 전적 조회 수치를 더한다.
- **Client 순수 코드(UnityEngine 없음).** `UiFlow`(화면 상태 기계, 커서·입력 막기 출력, `StatsWait`), `UiText`(한국어 문구, 이름·포트·주소 검사), `KillFeedModel`(5칸 링, 6초). 서버 테스트 프로젝트가 소스 링크로 시험한다(Phase 5의 `ZoneMath`와 같은 방식).
- **Client UI.** `UiFont`(OS 한글 글꼴), `UiFactory`(Canvas·Scaler·Raycaster·패널·글자·버튼·입력칸, EventSystem), 화면(`TitleScreen`, `MenuScreen`, `DisconnectScreen`, `ResultScreen`, `StatsWindow`), 자기 Canvas를 쓰는 `KillFeed`와 `DebugOverlay`(F1), 이들을 묶는 `UiRoot`(MonoBehaviour). `GameClient`는 이름 표, 끊김 정보, 결과 정보, 전적 답을 읽기 전용으로 보여 주고 UiRoot의 커서·입력 막기를 받는다. `DevConnectPanel`(IMGUI)은 지운다.

**Tech Stack:** Unity 6000.3.24f1 UGUI(Legacy `Text`, `InputField`, `Button`, `CanvasScaler`, `GraphicRaycaster`), Input System 1.20.0(`InputSystemUIInputModule`), .NET 10, C# 9(Shared netstandard2.1, Unity Client), LiteNetLib 2.1.4, MySqlConnector 2.6.2, `System.Threading.Channels`, `System.Diagnostics.Metrics`, xUnit, NUnit(EditMode). 새 패키지는 없다.

**Spec:** `Docs/specs/2026-10-01-phase11-game-ui-design.md`

## Global Constraints

- **Commit:** 작업 Branch(`phase11-game-ui`)에서 Task마다 Commit한다. 각 Task의 **Files**에 적힌 경로만 `git add`한다(`git add -A`를 쓰지 않는다). Push는 Phase가 끝난 뒤 `github-push` 스킬로 한다. Force Push는 하지 않는다. `.claude/settings.json`과 `.superpowers`는 Stage하지 않는다.
- **`.meta` 파일은 만들지 않는다.** Unity가 Editor를 열 때 만든다. 이 Phase에서 생기는 것은 `Client/Assets/Scripts/UI.meta`, `Client/Assets/Scripts/UI/*.cs.meta`(13개), `Shared/Runtime/Protocol/StatsPackets.cs.meta`다. 이미 저장소에 추적되지 않은 채 있는 `Shared/Runtime/Protocol/DisconnectCode.cs.meta`(Phase 10)도 함께 Phase 완료 때 커밋한다(아래 "Phase 완료 확인" 6). 지우는 `DevConnectPanel.cs`는 추적 중인 `.meta`와 함께 `git rm`한다(Task 5).
- **코드 규칙:** 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다.
  - **새 Lock은 없다.**
    - 요청·응답 채널은 bounded `Channel`(쓰는 쪽 여럿, 읽는 쪽 하나)이다. 카운터는 `Interlocked`다.
    - 속도 제한 시각(`PeerState`)은 LiteNetLib 수신 스레드만 쓴다.
    - 응답 송신은 Game Loop만 한다.
    - Client 코드는 모두 Unity 메인 스레드다.
  - **Collection 상한:**
    - 서버: 요청 채널 32, 응답 채널 32(Tick당 최대 32개를 비운다).
    - Client: 이름 표는 플레이어 수(Despawn과 끊김 때 지운다), Kill Feed는 5칸 고정 링, 전적 답은 마지막 하나다.
  - **Shared(`Shared/Runtime/Protocol`)는 netstandard2.1, C# 9다.** `record`·`init`이 없고 LiteNetLib도 UnityEngine도 쓰지 않는다.
  - **소스 링크하는 Client 파일**(`UI/UiFlow.cs`, `UI/UiText.cs`, `UI/KillFeedModel.cs`)은 UnityEngine을 쓰지 않는다. 서버 테스트 프로젝트는 Nullable이 켜져 있으므로 null 문자열을 다루는 두 파일은 맨 위에 `#nullable disable`을 둔다(Unity의 C# 9에서도 된다). `UiText`는 LiteNetLib의 `DisconnectReason`을 쓴다. 테스트 프로젝트는 서버 프로젝트를 거쳐 같은 LiteNetLib 2.1.4를 받는다.
- **수치(spec):**
  - `ProtocolVersion` 9, `PacketId.StatsRequest` 22, `PacketId.StatsResponse` 23
  - `StatsResponse.MaxRows` 10, 행 20B, 요약 24B, `MaxSize` 227B
  - 요청 채널 32, 응답 채널 32, 연결당 2초에 한 번(`StatsQueryQueue.MinRequestIntervalMs` 2000), 조회 3초(`StatsQueryService.QueryTimeout`)
  - Client: 답을 5초 기다린다(`StatsWait.AnswerSeconds`). 2.5초 안에 다시 열면 요청을 다시 보내지 않는다(`StatsWait.ResendSeconds`, Spec 해석 11).
  - Kill Feed: 5줄, 6초. 새 화면 Canvas: `CanvasScaler` 1920×1080, `matchWidthOrHeight` 0.5.
  - 글꼴 후보: "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic". 없으면 `LegacyRuntime.ttf`
  - `PlayerPrefs` 키: `ProjectH.Host`, `ProjectH.Port`, `ProjectH.Name`
- **범위:** spec D12의 항목은 하지 않는다(설정 화면, 키 바꾸기, 서버 목록, 로그인, 미니맵, 소리·애니메이션, 다국어 전환, TextMeshPro, UI Toolkit, Prefab UI, 기존 HUD 배치 변경, 모바일 배치). Scene과 Prefab은 만들거나 고치지 않는다.
- **Docker:** 띄우거나 내리지 않는다. DB 테스트는 `PROJECTH_TEST_MYSQL`이 있을 때만 돈다.
- **부하 측정 프로세스:** 7790 같은 빈 포트를 쓴다. 자기가 띄운 프로세스는 pid로만 끈다. 이름으로 끄지 않는다(`taskkill /IM`, `pkill -f` 금지).
- **명령 실행 위치:** 저장소 루트(`E:/popol/ProjectH`)에서 실행한다.
  - 시작 기준은 서버 테스트 752개다(745 통과, MySQL 7개 건너뜀).
  - 이 계획의 코드는 계획 단계에서 스크래치 복사본에 Task 순서대로 그대로 적용했다. 그 상태에서 Task마다 빌드 경고 0, 아래 테스트 수, `UnityCompile` 경고·오류 0을 확인했다.
  - `WorldItemsTests.AddSearchRemove_AllocateNothing`은 가끔 혼자 실패하는 테스트다(할당 측정). 그것 하나만 실패하면 한 번 더 돌린다. 다시 실패하면 보고한다.
- **스크립트:** Python 스크립트는 저장소 밖에 저장하고 저장소 경로를 인자로 실행한다. `edit()`는 기준 텍스트가 정확히 한 번 나와야 바꾸고, 파일의 줄바꿈(CRLF/LF)을 지킨다. 실패하면 멈춘다. 그때 파일을 손으로 고치지 말고 원인(앞 단계 누락 등)을 먼저 확인한다.
- **Unity 확인 도구(Task 3 Step 1에서 만든다):**
  - `UnityCompile`: Client 스크립트, Shared, EditMode 테스트를 실제 Unity DLL로 컴파일한다.
  - `EditTests`: 이 Phase가 바꾸는 순수 HUD 문구 클래스의 EditMode 테스트를 Unity 밖 NUnit으로 돌린다.
  - 둘 다 저장소 밖 스크래치 폴더에 둔다. 컨트롤러의 원래 `UnityCompile.csproj`는 읽기만 한다.
- **Unity API 확인(계획 단계, Unity 6000.3.24f1 DLL과 패키지 소스):**
  - 컴파일 확인(경고·오류 0): `Font.GetOSInstalledFontNames()`, `Font.CreateDynamicFontFromOSFont(string[], int)`, `Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")`, `CanvasScaler.uiScaleMode`·`referenceResolution`·`screenMatchMode`·`matchWidthOrHeight`, `UnityEngine.UI.InputField`(`characterLimit`, `contentType`, `lineType`, `textComponent`, `placeholder`, `onSubmit`. 어느 것도 Obsolete가 아니다), `Button.onClick`, `Object.FindAnyObjectByType<EventSystem>()`, `PlayerPrefs`, `Application.Quit()`.
  - `InputSystemUIInputModule`(Input System 1.20.0, `Unity.InputSystem` 어셈블리)는 Action이 없으면 `OnEnable`에서 `AssignDefaultActions()`로 기본 UI Action을 붙이고, `OnDisable`에서 그 기본 Action을 뗀다(패키지 소스에서 확인). 그래서 코드로 `AddComponent`만 하면 된다.
  - `ProjectSettings.asset`의 `activeInputHandler`는 1(Input System만)이다.
  - LiteNetLib `DisconnectReason`은 Client DLL과 NuGet 2.1.4가 같은 12개 값이다(ConnectionFailed 0 … PeerNotFound 11). `UiText.Reason`이 모두 다룬다.
  - **`ProjectH.Client.asmdef`는 바꾸지 않는다.** `InputSystemUIInputModule`은 이미 참조하는 `Unity.InputSystem`에, `InputField`·`CanvasScaler`는 이미 참조하는 `UnityEngine.UI`에 있다.

## Review Focus

- **Game Loop는 DB를 기다리지 않는다.** 요청은 Network 스레드가 채널에 넣기만 한다. 조회는 `StatsQueryService`가 하고, 답은 채널로 돌아와 Game Loop가 보낸다. 조회 제한 3초는 토큰만으로는 지켜지지 않는다. MySqlConnector는 서버 인사말을 기다리는 연결을 취소하지 않는다. 계획 단계에서 TCP 연결만 받는 서버에 대해 `Connection Timeout=4`로 재니 4.05초 걸렸다. 그래서 `WaitAsync(QueryTimeout)`로 끊는다. 남겨진 조회는 자기 Connection Timeout에 끝나고 연결을 돌려준다. 그 예외는 `ContinueWith`로 관찰한다(Task 2 `TheService_WhenTheDatabaseDoesNotAnswer_AnswersUnavailable_WithinTheLimit`).
- **LiteNetLib의 peer id 재사용.** 답은 요청한 그 NetPeer에게만 간다. Game Loop는 `_peers[id]`가 답에 실린 NetPeer와 같은 참조일 때만 보낸다(Task 2 `AnAnswerForAConnectionThatLeft_IsDropped`).
- **버튼 연타가 Kick이 되면 안 된다.** 2초 안의 요청과 Join 전 요청은 `OnBadPacket`이 아니라 `limited`로 센다(Task 2 `RequestsWithinTwoSeconds_GetNoAnswer_AreCounted_AndNeverKick`: 30번 보내도 Kick 0). 본문이 있는 요청만 `Malformed`다.
- **`Busy`도 Game Loop가 보낸다.** Network 스레드는 `Busy` 답을 응답 채널에 넣기만 한다(Task 2 `AFullRequestQueue_AnswersBusy`).
- **메뉴가 열려 있어도 입력은 계속 간다.** `GameClient`는 막는 동안에도 예측기를 Step하고, 이동·버튼 0인 입력을 보낸다. 그러지 않으면 Phase 10 Input Timeout(10초)이 메뉴를 연 플레이어를 끊는다. 막는 동안 눌린 키(`QueuedButtons`)는 버린다.
- **Esc의 주인이 바뀐다.** Phase 10까지는 `GameClient`가 Esc로 커서를 풀었다. 이제 `UiFlow`가 정하고, `UiRoot`가 `GameClient.SetUiControl`로 넘긴다. 커서는 다음 프레임에 따라간다. 화면이 떠 있으면 클릭이 커서를 잠그지 않는다.
- **Kill Feed와 관전 알림.** 경기 중 들어온 사람은 Join 때 본인의 `PlayerDied(KillerId 0, Placement 0)`를 받는다. 이것은 죽음이 아니라 관전 시작이다. 그래서 Kill Feed에 넣지 않고 결과 화면의 "나를 처치한 플레이어"도 바꾸지 않는다. 경기 중 Zone 사망은 Placement가 1 이상이라 구별된다.
- **매 프레임 할당.**
  - 끊김 이유 문구는 `UiText`의 상수 문자열이다(참조 비교).
  - 남은 초·재접속 진행·결과 카운트다운은 정수가 바뀔 때만 만든다.
  - Kill Feed 줄은 죽음마다 한 번 만든다. Kill Feed Text는 `Version`이 바뀔 때만 바꾼다.
  - `DebugOverlay`는 보일 때, 값이 바뀔 때만 만든다.
  - `UiFlow.Version`이 바뀔 때만 화면을 다시 배치한다.
- **`InputField`와 Input System 전용 설정.** `ProjectSettings`의 `activeInputHandler`는 1(Input System만)이다. UGUI `InputField`의 IME 조합 문자열은 `BaseInput.compositionString` → `UnityEngine.Input.compositionString`으로 읽힌다. Input System 1.20의 `BaseInputOverride`는 정의만 있고 쓰이지 않는다. 컴파일은 확인했지만 실행 중 한글 IME 입력은 Unity Editor에서 사용자가 확인해야 한다("Phase 완료 확인" 5).

## Spec 해석

1. **"Join한 peer만 받는다"는 `PeerState.JoinRequested`로 판단한다.** 수신 스레드가 아는 "Join"은 이것뿐이다(`Joined`는 Game Loop 전용이다). Join이 거절된 연결(`MatchFull`)도 마지막 1초 동안은 요청할 수 있다. 그 답은 그 연결이 아직 있으면 간다. 해가 없다.
2. **Join 전 요청은 답 없이 버리고 `limited`로 센다.** 잘못된 패킷으로 세지 않는다. 본문이 있는 `StatsRequest`(본문 없음 규칙 위반)만 `Malformed`다.
3. **spec의 "버림"을 둘로 센다.** `limited`(요청을 버림: Join 전, 2초 안)와 `undelivered`(답을 버림: 응답 채널이 가득 참, 요청한 연결이 떠남)다. 그래서 Health 항목은 `stats requests limited busy unavailable undelivered` 다섯 개다.
4. **누적 통계는 uint32로 보낸다.** DB의 피해·생존 시간 합계는 64비트라 서버가 uint32 최댓값으로 자른다. 누적 생존 시간은 초 단위다(uint32 ms는 약 49일에서 넘친다). 최근 경기 행의 생존 시간은 spec대로 ms다.
5. **`StatsRow`의 순위는 인원 이하인지 검사하지 않는다.** Phase 9 저장은 같은 DevPlayerId가 둘이면 한 행만 남기므로, 저장된 인원보다 큰 순위가 있을 수 있다. 0은 순위 없음이다.
6. **봇은 바꿀 코드가 없다.** 봇은 `PlayerSpawned`를 읽지 않고(`BotConnection`) 전적을 요청하지 않는다. `ProtocolVersion` 상수를 쓰므로 다시 빌드하면 v9로 접속한다. 봇 테스트가 그대로 통과하는 것으로 확인한다.
7. **"접속 중" 화면은 타이틀 화면의 접속 중 상태다.** 입력칸을 잠그고 "접속하는 중..."과 취소 버튼을 보여 준다. 새 파일은 없다.
8. **직접 누른 접속이 실패하면(거절, 경기 가득 참, 연결 실패) 끊김 화면으로 간다.** 이유와 다시 접속·타이틀로 버튼이 있다. 타이틀로 돌아가면 타이틀에도 그 이유가 남는다. 메뉴의 접속 끊기와 접속 중 취소로 돌아오면 이유를 지운다.
9. **"모든 UI 글자는 한글"은 UI가 정하는 문구다.** 서버 카탈로그의 무기·아이템·등급 이름("Vesper AR", "Light Rounds", "Rare")과 맵 POI 이름(`MapPoisTests`가 ASCII로 고정)은 데이터라 그대로다. 한글이 보이는지는 글꼴(D2)이 정한다.
10. **Esc는 한 단계 뒤로다.** 전적 창 → 메뉴·결과 → 경기 순서다. 경기에서는 메뉴를 연다. 타이틀·접속 중·끊김 화면에서는 아무것도 하지 않는다. 결과 화면의 Esc는 "계속 관전"과 같다.
11. **전적 창은 열 때 요청하되, 2.5초 안에 다시 열면 앞 요청을 다시 쓴다.** 서버는 2초 안의 두 번째 요청에 답하지 않는다. 그대로 다시 보내면 창이 5초 뒤 "응답 없음"이 된다. 앞 답이 와 있으면 그것을, 오는 중이면 그것을 기다린다(`StatsWait.MaySend`).
12. **새 결과(`MatchResult`)는 메뉴가 열려 있어도 결과 화면을 연다.** 같은 결과로는 다시 열지 않는다(`GameClient.ResultCount`).
13. **커서가 풀려 있으면 게임 입력을 막는다(D5).** 그래서 Join한 뒤 화면을 한 번 클릭해야 움직일 수 있다. Phase 10까지는 커서가 풀려 있어도 WASD로 움직였다.
14. **결과 화면의 탈락 원인은 내 마지막 `PlayerDied`로 정한다.** 이긴 경우와 죽지 않은 경우는 비운다. 처치자 이름은 죽을 때 이름 표에서 찾아 둔다. 처치자가 결과 전에 나가도 이름이 남는다.
15. **`UiFont`의 주인은 `GameClient`다.** 모든 HUD가 쓰고, `GameClient.OnDestroy`가 마지막에 `UiFont.Release`를 부른다. `UiRoot`의 화면은 같은 GameObject와 함께 사라진다.
16. **이름 검사는 `UiText.TryNormalizeName`이다.** 앞뒤 공백을 지우고 UTF-8 1–32바이트다(서버의 `ConnectRequestData` 규칙과 같다). 주소는 앞뒤 공백을 지우고 비어 있지 않아야 한다. 포트는 1–65535다.
17. **EventSystem은 장면에 없을 때만 만든다.** 만든 것만 `UiRoot`가 지운다.

## Spec과 다른 점

계획 단계의 프로토타입에서 spec과 다르게 정한 것이다. 컨트롤러가 spec에 반영한다.

1. **조회 제한은 토큰과 `WaitAsync`로 지킨다(D8 "조회마다 3초 제한" 보완).** MySqlConnector가 연결 단계의 취소를 따르지 않기 때문이다(Review Focus 첫 항목). 제한에 걸린 조회는 뒤에서 자기 Connection Timeout(배포 설정 5초)까지 남을 수 있다. 서비스는 다음 요청으로 넘어가므로 DB가 멈춰 있으면 이런 조회가 동시에 두세 개 생길 수 있다. 개수는 시간 제한으로 묶인다.
2. **Health·Meter 항목이 다섯 개다(Spec 해석 3).** spec은 "조회 수, 버림, Busy, Unavailable" 네 개를 적었다.
3. **접속 중 화면에 취소 버튼을 더한다(Spec 해석 7).** LiteNetLib 기본 연결 시도는 약 5.5초라, 주소를 잘못 넣은 사람이 기다리지 않게 한다.
4. **누적 통계의 형식(Spec 해석 4).** spec은 요약 필드의 크기를 정하지 않았다. 이 계획은 uint32 여섯 개로 정하고 생존 시간은 초다.
5. **전적 요청 재사용(Spec 해석 11).** spec은 "창이 열릴 때 한 번 요청한다"이다.
6. **`KillFeed`의 주인은 `GameClient`다.** 파일은 spec대로 `UI/KillFeed.cs`다. `PlayerDied`와 이름 표가 `GameClient`에 있으므로 `UiRoot`가 이벤트를 따로 구독하지 않게 한다.
7. **`NetClient.LastError`는 그대로 영어다.** 화면은 이제 이것을 쓰지 않고 `UiText.Disconnect`를 쓴다. `LastError`는 로그(`GameClient.OnDisconnected`)에만 남는다.
8. **`DebugOverlay`도 자기 Canvas를 쓴다.** spec D10은 "Kill Feed만 자기 Canvas를 쓴다"이다. F1 줄은 RTT가 자주 바뀌므로 화면 Canvas(`UiScreens`)에 두면 바뀔 때마다 그 Canvas를 다시 만든다. 기본으로 꺼져 있고, 켜도 그 Canvas 하나만 다시 만든다.
9. **재접속 문구의 줄표는 ASCII `-`다.** spec D6의 "재접속 중 (n/3) — k초 뒤 다시 시도"를 "재접속 중 (n/3) - k초 뒤 다시 시도"로 쓴다. 프로토타입에서 이렇게 정했고 뜻은 같다. 바꾸려면 `UiText.Reconnecting`과 `UiTextTests.Reconnecting_ShowsTheAttemptAndTheSeconds`를 함께 고친다.

---

### Task 1: Protocol v9 (`StatsRequest`·`StatsResponse`, `PlayerSpawned.Name`)

**Files:**
- Create: `Shared/Runtime/Protocol/StatsPackets.cs`
- Modify: `Shared/Runtime/Protocol/PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs`, `ServerPackets.cs`
- Modify: `Server/src/ProjectH.Server/Game/Match.cs`(`SendSpawned`에 이름)
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Shared/StatsPacketTests.cs`, `Integration/PlayerNameIntegrationTests.cs`
  - Modify: `Shared/PacketTests.cs`, `Shared/PacketWriterReaderTests.cs`, `Shared/ProtocolConstantsTests.cs`, `Shared/ProtocolFuzzTests.cs`, `Integration/HeadlessClient.cs`

**Interfaces:**
- Produces(Shared):
  - `PacketId.StatsRequest` 22, `PacketId.StatsResponse` 23. `PacketReader.TryReadPacketId`의 상한은 `StatsResponse`다.
  - `ProtocolConstants.ProtocolVersion` 9
  - `enum StatsStatus : byte { Ok, NoRecord, Unavailable, Busy }`
  - `struct StatsSummary { uint Matches, Wins, Kills, Deaths, Damage, SurvivalSeconds }`
  - `struct StatsRow { uint EndedUnixSeconds; uint Round; byte Players; byte Placement; ushort Kills; uint Damage; uint SurvivalMs }`
  - `static class StatsRequest { Write(ref PacketWriter) }`
  - `sealed class StatsResponse { const MaxRows 10, SummarySize 24, RowSize 20, MaxSize 227; Status; Summary; StatsRow[] Rows; static Of(StatsStatus); static Write(ref PacketWriter, StatsResponse); static bool TryRead(ref PacketReader, out StatsResponse) }`
  - `PlayerSpawned.Name`(string, 1–32B. 빈 이름·33B 이상은 읽지 않는다)
- 테스트: `HeadlessClient.SpawnNames`(Entity Id → 이름)

봇은 바꾸지 않는다(Spec 해석 6). Client(`NetClient`)는 `PlayerSpawned`를 구조체째 넘기므로 이 Task에서 고칠 곳이 없다. 이름은 Task 4가 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Shared/StatsPacketTests.cs`:

```csharp
using System;
using System.Numerics;
using System.Text;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 11 spec §2 Protocol: StatsResponse (0 and 10 rows, truncated, more than 10 rows), the name in PlayerSpawned and
// the new packet ids.
public class StatsPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static StatsRow Row(int i) => new StatsRow
    {
        EndedUnixSeconds = 1_790_000_000u + (uint)i,
        Round = 100u + (uint)i,
        Players = (byte)(10 + i),
        Placement = (byte)(1 + i),
        Kills = (ushort)(300 + i),
        Damage = 70_000u + (uint)i,
        SurvivalMs = 4_000_000u + (uint)i,
    };

    private static StatsResponse Full(int rows)
    {
        var r = new StatsResponse
        {
            Status = StatsStatus.Ok,
            Summary = new StatsSummary { Matches = 12, Wins = 3, Kills = 40, Deaths = 9, Damage = 4_000_000_000u, SurvivalSeconds = 86_400 },
            Rows = new StatsRow[rows],
        };
        for (int i = 0; i < rows; i++) r.Rows[i] = Row(i);
        return r;
    }

    [Fact]
    public void Ids_AndTheReaderRange()
    {
        Assert.Equal(22, (byte)PacketId.StatsRequest);
        Assert.Equal(23, (byte)PacketId.StatsResponse);
        var reader = new PacketReader(new byte[] { 23 });
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.StatsResponse, id);
        reader = new PacketReader(new byte[] { 24 });
        Assert.False(reader.TryReadPacketId(out _));
    }

    [Fact]
    public void StatsRequest_IsTheIdAlone()
    {
        var writer = new PacketWriter(_buffer);
        StatsRequest.Write(ref writer);
        Assert.Equal(1, writer.Length);
        Assert.Equal((byte)PacketId.StatsRequest, _buffer[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public void StatsResponse_RoundTrip(int rows)
    {
        StatsResponse sent = Full(rows);
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, sent);
        Assert.False(writer.Overflowed);
        Assert.Equal(1 + 1 + StatsResponse.SummarySize + 1 + rows * StatsResponse.RowSize, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.True(StatsResponse.TryRead(ref reader, out StatsResponse got));
        Assert.Equal(StatsStatus.Ok, got.Status);
        Assert.Equal(sent.Summary, got.Summary);
        Assert.Equal(sent.Rows, got.Rows);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void StatsResponse_MaxSize_Is227_AndFitsOneDatagram()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(StatsResponse.MaxRows));
        Assert.Equal(StatsResponse.MaxSize, writer.Length);
        Assert.Equal(227, StatsResponse.MaxSize);
        Assert.True(StatsResponse.MaxSize <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void StatsResponse_WritesAtMostTenRows()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(12));
        Assert.Equal(StatsResponse.MaxSize, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.True(StatsResponse.TryRead(ref reader, out StatsResponse got));
        Assert.Equal(StatsResponse.MaxRows, got.Rows.Length);
    }

    [Theory]
    [InlineData(StatsStatus.NoRecord)]
    [InlineData(StatsStatus.Unavailable)]
    [InlineData(StatsStatus.Busy)]
    public void StatsResponse_OtherStatuses_CarryNoData(StatsStatus status)
    {
        StatsResponse sent = Full(3);
        sent.Status = status;
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, sent);
        Assert.Equal(1 + 1 + StatsResponse.SummarySize + 1, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.True(StatsResponse.TryRead(ref reader, out StatsResponse got));
        Assert.Equal(status, got.Status);
        Assert.Equal(default(StatsSummary), got.Summary);
        Assert.Empty(got.Rows);
    }

    [Fact]
    public void StatsResponse_Truncated_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(3));
        for (int length = 1; length < writer.Length; length++)
        {
            var reader = ReaderAfterId(length, PacketId.StatsResponse);
            Assert.False(StatsResponse.TryRead(ref reader, out _), $"length {length}");
        }
    }

    [Fact]
    public void StatsResponse_MoreThanTenRows_OrAnUnknownStatus_OrRowsWithoutOk_IsRejected()
    {
        // 11 rows claimed, with enough bytes behind it.
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(0));
        _buffer[1 + 1 + StatsResponse.SummarySize] = StatsResponse.MaxRows + 1;
        int length = writer.Length + (StatsResponse.MaxRows + 1) * StatsResponse.RowSize;
        var reader = ReaderAfterId(length, PacketId.StatsResponse);
        Assert.False(StatsResponse.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(0));
        _buffer[1] = (byte)StatsStatus.Busy + 1;
        reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.False(StatsResponse.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(2));
        _buffer[1] = (byte)StatsStatus.NoRecord;
        reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.False(StatsResponse.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("dev-1234abcd")]
    [InlineData("플레이어")]
    [InlineData("abcdefghijabcdefghijabcdefghij12")]   // 32 bytes
    public void PlayerSpawned_Name_RoundTrips(string name)
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 7, Position = new Vector3(1, 2, 3), Yaw = 90f, Name = name });
        Assert.False(writer.Overflowed);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.True(PlayerSpawned.TryRead(ref reader, out PlayerSpawned s));
        Assert.Equal(name, s.Name);
        Assert.Equal(7, s.EntityId);
        Assert.Equal(new Vector3(1, 2, 3), s.Position);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void PlayerSpawned_EmptyOrTooLongName_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 7, Name = "" });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.False(PlayerSpawned.TryRead(ref reader, out _));

        // 33 bytes: the writer refuses it, so the bytes are laid out by hand.
        writer = new PacketWriter(_buffer);
        writer.WriteByte((byte)PacketId.PlayerSpawned);
        writer.WriteUInt16(7);
        writer.WriteVector3(Vector3.Zero);
        writer.WriteSingle(0f);
        writer.WriteString(new string('x', 33), 64);
        reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.False(PlayerSpawned.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 7, Name = new string('x', 33) });
        Assert.True(writer.Overflowed);
        Assert.Equal(33, Encoding.UTF8.GetByteCount(new string('x', 33)));
    }

    [Fact]
    public void PlayerSpawned_WithoutTheName_IsRejected()
    {
        // A v8 layout (no name byte) must not parse as a v9 spawn.
        var writer = new PacketWriter(_buffer);
        writer.WriteByte((byte)PacketId.PlayerSpawned);
        writer.WriteUInt16(7);
        writer.WriteVector3(Vector3.Zero);
        writer.WriteSingle(0f);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.False(PlayerSpawned.TryRead(ref reader, out _));
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Integration/PlayerNameIntegrationTests.cs`:

```csharp
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 11 D9 over real UDP: every PlayerSpawned carries the player's DevPlayerId, so a client knows the other players'
// names (and its own) from the spawns it gets at join and when someone joins later.
public sealed class PlayerNameIntegrationTests
{
    [Fact]
    public void EveryPlayerSpawned_CarriesTheDevPlayerId()
    {
        using GameLoop server = StartServer();
        using var alice = Join(server, "alice");
        using var bob = Join(server, "밥");   // multi-byte UTF-8 travels as it was typed

        Assert.True(Pump.Until(() => alice.SpawnNames.Count == 2 && bob.SpawnNames.Count == 2, 3000, alice, bob), "spawns");
        Assert.Equal("밥", alice.SpawnNames[bob.MyEntityId]);
        Assert.Equal("alice", alice.SpawnNames[alice.MyEntityId]);
        Assert.Equal("alice", bob.SpawnNames[alice.MyEntityId]);
        Assert.Equal("밥", bob.SpawnNames[bob.MyEntityId]);
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t1_tests.py`로 저장하고 `python <경로>/p11_t1_tests.py E:/popol/ProjectH`로 실행한다. `PacketTests.cs`, `PacketWriterReaderTests.cs`, `ProtocolConstantsTests.cs`, `ProtocolFuzzTests.cs`, `HeadlessClient.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Server/tests/ProjectH.Server.Tests/Shared/PacketTests.cs', [
(r"""        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 3, Position = new Vector3(1, 0, 2), Yaw = 45f });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.True(PlayerSpawned.TryRead(ref reader, out var s));
        Assert.Equal(3, s.EntityId);
        Assert.Equal(new Vector3(1, 0, 2), s.Position);
""",
 r"""        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 3, Position = new Vector3(1, 0, 2), Yaw = 45f, Name = "p3" });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.True(PlayerSpawned.TryRead(ref reader, out var s));
        Assert.Equal(3, s.EntityId);
        Assert.Equal(new Vector3(1, 0, 2), s.Position);
        Assert.Equal("p3", s.Name);
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Shared/PacketWriterReaderTests.cs', [
(r"""    [InlineData(22)]   // one above PacketId.MatchResult
""",
 r"""    [InlineData(24)]   // one above PacketId.StatsResponse (Phase 11)
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Shared/ProtocolConstantsTests.cs', [
(r"""    public void ProtocolVersion_IsEight()
    {
        // Phase 10 added the disconnect codes and JoinResult.Resumed; v7 clients must be rejected at connect.
        Assert.Equal((ushort)8, ProtocolConstants.ProtocolVersion);
""",
 r"""    public void ProtocolVersion_IsNine()
    {
        // Phase 11 added StatsRequest/StatsResponse and the name in PlayerSpawned; v8 clients must be rejected at connect.
        Assert.Equal((ushort)9, ProtocolConstants.ProtocolVersion);
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Shared/ProtocolFuzzTests.cs', [
(r"""            if (length > 0 && random.Next(2) == 0) buffer[0] = (byte)random.Next(1, (int)PacketId.MatchResult + 1);
""",
 r"""            if (length > 0 && random.Next(2) == 0) buffer[0] = (byte)random.Next(1, (int)PacketId.StatsResponse + 1);
"""),
(r"""        if (MatchResult.TryRead(ref r, out _)) ok++;
        return ok;
""",
 r"""        if (MatchResult.TryRead(ref r, out _)) ok++;
        r = new PacketReader(data);
        if (StatsResponse.TryRead(ref r, out _)) ok++;   // Phase 11
        return ok;
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs', [
(r"""    public HashSet<ushort> Spawned { get; } = new();
""",
 r"""    public HashSet<ushort> Spawned { get; } = new();
    // Phase 11 D9: the name each PlayerSpawned carried, by entity id.
    public Dictionary<ushort, string> SpawnNames { get; } = new();
"""),
(r"""                if (PlayerSpawned.TryRead(ref r, out var spawned)) Spawned.Add(spawned.EntityId);
""",
 r"""                if (PlayerSpawned.TryRead(ref r, out var spawned))
                {
                    Spawned.Add(spawned.EntityId);
                    SpawnNames[spawned.EntityId] = spawned.Name;
                }
"""),
])
print('p11_t1_tests ok')
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. `StatsStatus`, `StatsRow`, `StatsResponse`가 없다(CS0103, CS0246). 계획 단계에서 오류는 이 두 종류뿐이었다.

- [ ] **Step 3: Protocol v9를 구현한다**

`PlayerSpawned`는 이름을 Yaw 뒤에 1바이트 길이 + UTF-8로 쓴다(`PacketWriter.WriteString`, 최대 `MaxDevPlayerIdBytes`). 서버는 연결 요청에서 이미 검사한 DevPlayerId를 그대로 넣는다. 그래서 이 Task에서 `Match.SendSpawned`도 함께 고쳐야 Join이 계속 된다(이름 없는 v9 Spawn은 읽기 실패다).

**새 파일** `Shared/Runtime/Protocol/StatsPackets.cs`:

```csharp
using System;

namespace ProjectH.Shared.Protocol
{
    // Phase 11 D8: what the server answers to a StatsRequest. The values are the wire format.
    public enum StatsStatus : byte
    {
        Ok = 0,           // the summary and up to StatsResponse.MaxRows recent matches follow
        NoRecord = 1,     // the database has no saved match for this DevPlayerId
        Unavailable = 2,  // persistence is off, or the database failed or did not answer in time
        Busy = 3,         // the server's query queue was full; asking again later may work
    }

    // Totals over every saved match of one DevPlayerId. uint32 on the wire: the server clamps larger values (the
    // database keeps damage and survival time as 64-bit sums), and survival is whole seconds.
    public struct StatsSummary
    {
        public uint Matches;
        public uint Wins;
        public uint Kills;
        public uint Deaths;
        public uint Damage;
        public uint SurvivalSeconds;
    }

    // One recent match of the player (D8).
    public struct StatsRow
    {
        public uint EndedUnixSeconds;   // UTC
        public uint Round;
        public byte Players;
        public byte Placement;          // 1 = won, 0 = unranked
        public ushort Kills;
        public uint Damage;
        public uint SurvivalMs;
    }

    // C->S, ReliableOrdered, no body: "send me my statistics". The server answers each accepted request with one
    // StatsResponse and silently drops requests above its per-connection rate (D8).
    public static class StatsRequest
    {
        public static void Write(ref PacketWriter writer)
        {
            writer.WriteByte((byte)PacketId.StatsRequest);
        }
    }

    // S->C, ReliableOrdered, the answer to one StatsRequest (D8). Layout after the packet id: status 1, summary 24
    // (six uint32), row count 1, then count x 20-byte rows, newest first. A status other than Ok always carries a zero
    // summary and no rows. A class because it is built off the game loop and handed over whole; the reader allocates
    // it and its rows once per answer (a person pressed a button), never on the per-tick path.
    public sealed class StatsResponse
    {
        public const int MaxRows = 10;
        public const int SummarySize = 24;
        public const int RowSize = 20;
        // 227 bytes with the packet id: far below one datagram.
        public const int MaxSize = 1 + 1 + SummarySize + 1 + MaxRows * RowSize;

        public StatsStatus Status;
        public StatsSummary Summary;
        public StatsRow[] Rows = Array.Empty<StatsRow>();   // newest first; only the first MaxRows are written

        public static StatsResponse Of(StatsStatus status) => new StatsResponse { Status = status };

        public static void Write(ref PacketWriter writer, StatsResponse r)
        {
            bool ok = r.Status == StatsStatus.Ok;
            StatsRow[] rows = r.Rows ?? Array.Empty<StatsRow>();
            int count = ok ? Math.Min(rows.Length, MaxRows) : 0;
            StatsSummary s = ok ? r.Summary : default;

            writer.WriteByte((byte)PacketId.StatsResponse);
            writer.WriteByte((byte)r.Status);
            writer.WriteUInt32(s.Matches);
            writer.WriteUInt32(s.Wins);
            writer.WriteUInt32(s.Kills);
            writer.WriteUInt32(s.Deaths);
            writer.WriteUInt32(s.Damage);
            writer.WriteUInt32(s.SurvivalSeconds);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                StatsRow row = rows[i];
                writer.WriteUInt32(row.EndedUnixSeconds);
                writer.WriteUInt32(row.Round);
                writer.WriteByte(row.Players);
                writer.WriteByte(row.Placement);
                writer.WriteUInt16(row.Kills);
                writer.WriteUInt32(row.Damage);
                writer.WriteUInt32(row.SurvivalMs);
            }
        }

        public static bool TryRead(ref PacketReader reader, out StatsResponse r)
        {
            r = null;
            if (reader.Remaining < 1 + SummarySize + 1) return false;
            reader.TryReadByte(out byte status);
            if (status > (byte)StatsStatus.Busy) return false;
            var s = new StatsSummary();
            reader.TryReadUInt32(out s.Matches);
            reader.TryReadUInt32(out s.Wins);
            reader.TryReadUInt32(out s.Kills);
            reader.TryReadUInt32(out s.Deaths);
            reader.TryReadUInt32(out s.Damage);
            reader.TryReadUInt32(out s.SurvivalSeconds);
            reader.TryReadByte(out byte count);
            if (count > MaxRows) return false;
            if (status != (byte)StatsStatus.Ok && count != 0) return false;
            if (reader.Remaining < count * RowSize) return false;

            StatsRow[] rows = count == 0 ? Array.Empty<StatsRow>() : new StatsRow[count];
            for (int i = 0; i < count; i++)
            {
                reader.TryReadUInt32(out rows[i].EndedUnixSeconds);
                reader.TryReadUInt32(out rows[i].Round);
                reader.TryReadByte(out rows[i].Players);
                reader.TryReadByte(out rows[i].Placement);
                reader.TryReadUInt16(out rows[i].Kills);
                reader.TryReadUInt32(out rows[i].Damage);
                reader.TryReadUInt32(out rows[i].SurvivalMs);
            }
            r = new StatsResponse { Status = (StatsStatus)status, Summary = s, Rows = rows };
            return true;
        }
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t1_impl.py`로 저장하고 `python <경로>/p11_t1_impl.py E:/popol/ProjectH`로 실행한다. `PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs`, `ServerPackets.cs`, `Match.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Shared/Runtime/Protocol/PacketId.cs', [
(r"""        MatchResult = 21,
""",
 r"""        MatchResult = 21,
        // Phase 11 D8: statistics on request.
        StatsRequest = 22,
        StatsResponse = 23,
"""),
])

edit('Shared/Runtime/Protocol/PacketReader.cs', [
(r"""            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.MatchResult) return false;
""",
 r"""            if (raw < (byte)PacketId.JoinMatchRequest || raw > (byte)PacketId.StatsResponse) return false;
"""),
])

edit('Shared/Runtime/Protocol/ProtocolConstants.cs', [
(r"""        // 8: Phase 10 hardening (DisconnectCode in the disconnect data, JoinResult.Resumed).
        public const ushort ProtocolVersion = 8;
""",
 r"""        // 8: Phase 10 hardening (DisconnectCode in the disconnect data, JoinResult.Resumed).
        // 9: Phase 11 game UI (StatsRequest/StatsResponse, the player's name in PlayerSpawned).
        public const ushort ProtocolVersion = 9;
"""),
])

edit('Shared/Runtime/Protocol/ServerPackets.cs', [
(r"""    public struct PlayerSpawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;

        public static void Write(ref PacketWriter writer, in PlayerSpawned s)
        {
            writer.WriteByte((byte)PacketId.PlayerSpawned);
            writer.WriteUInt16(s.EntityId);
            writer.WriteVector3(s.Position);
            writer.WriteSingle(s.Yaw);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerSpawned s)
        {
            s = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out s.EntityId);
            reader.TryReadVector3(out s.Position);
            reader.TryReadSingle(out s.Yaw);
            return true;
        }
    }
""",
 r"""    // Phase 11 D9: Name is the player's DevPlayerId (1-32 bytes of UTF-8, the same limit as the connect request). It
    // comes once per spawn, never with snapshots, so the client can show names in the kill feed, the spectator line
    // and the result. The reader allocates the string: join time only.
    public struct PlayerSpawned
    {
        public ushort EntityId;
        public Vector3 Position;
        public float Yaw;
        public string Name;

        public static void Write(ref PacketWriter writer, in PlayerSpawned s)
        {
            writer.WriteByte((byte)PacketId.PlayerSpawned);
            writer.WriteUInt16(s.EntityId);
            writer.WriteVector3(s.Position);
            writer.WriteSingle(s.Yaw);
            writer.WriteString(s.Name, ProtocolConstants.MaxDevPlayerIdBytes);
        }

        public static bool TryRead(ref PacketReader reader, out PlayerSpawned s)
        {
            s = default;
            if (reader.Remaining < 18) return false;
            reader.TryReadUInt16(out s.EntityId);
            reader.TryReadVector3(out s.Position);
            reader.TryReadSingle(out s.Yaw);
            if (!reader.TryReadString(ProtocolConstants.MaxDevPlayerIdBytes, out s.Name) || s.Name.Length == 0) return false;
            return true;
        }
    }
"""),
])

edit('Server/src/ProjectH.Server/Game/Match.cs', [
(r"""        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw });
""",
 r"""        // Phase 11 D9: the name is the DevPlayerId the connect request already validated (1-32 bytes).
        PlayerSpawned.Write(ref writer, new PlayerSpawned
        {
            EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw, Name = player.DevPlayerId,
        });
"""),
])
print('p11_t1_impl ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~StatsPacket|FullyQualifiedName~PlayerName|FullyQualifiedName~PacketTests|FullyQualifiedName~PacketWriterReader|FullyQualifiedName~ProtocolConstants|FullyQualifiedName~ProtocolFuzz"`
Expected: 모두 통과.

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 771개다(764 통과, MySQL 7개 건너뜀). 시작 기준보다 19개 많다. 계획 단계에서 같은 결과였다.

- [ ] **Step 5: Commit** — `feat: add protocol v9 (StatsRequest/StatsResponse, the player's name in PlayerSpawned)`

Unity는 새 Shared 파일 `StatsPackets.cs`의 `.meta`를 다음에 Editor가 열릴 때 만든다. Unity 컴파일 확인은 Task 3부터 한다.

---

### Task 2: 서버 전적 조회 경로

**Files:**
- Create: `Server/src/ProjectH.Server/Persistence/StatsQueryQueue.cs`, `Persistence/StatsQueryService.cs`
- Modify: `Server/src/ProjectH.Server/Net/PeerState.cs`, `Net/NetworkListener.cs`, `GameLoop.cs`, `Diagnostics/HealthCounters.cs`, `Diagnostics/ServerMeter.cs`, `GameServerService.cs`, `Program.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/Integration/StatsQueryIntegrationTests.cs`, `Persistence/StatsQueryTests.cs`
  - Modify: `Integration/HeadlessClient.cs`, `Diagnostics/MonitoringTests.cs`, `Persistence/MySqlTests.cs`(`[MySqlFact]` 2개)

**Interfaces:**
- Consumes: Task 1의 `StatsRequest`, `StatsResponse`, `StatsStatus`. Phase 9의 `MatchStore.GetStatsAsync`, `GetHistoryAsync`, `PersistenceOptions`.
- Produces:
  - `readonly record struct StatsQuery(int PeerId, NetPeer Peer, string DevPlayerId)`
  - `readonly record struct StatsReply(int PeerId, NetPeer Peer, StatsResponse Response)`
  - `readonly record struct StatsQueryCounts(long Requests, long Limited, long Busy, long Unavailable, long Undelivered)`
  - `StatsQueryQueue`: `DefaultCapacity` 32, `MinRequestIntervalMs` 2000, 생성자 `(int capacity = 32)`, `Capacity`, `Requests`(ChannelReader), `Counts`, `TryEnqueue(in StatsQuery)`, `TryReply(in StatsReply)`, `TryTakeReply(out StatsReply)`, `AddLimited()`, `AddUndelivered()`
  - `StatsQueryService : BackgroundService`: 생성자 `(StatsQueryQueue, IOptions<PersistenceOptions>, ILogger<StatsQueryService>)`, internal `QueryTimeout`(기본 3초, `init`), internal static `BuildResponse(PlayerStats?, IReadOnlyList<MatchHistoryEntry>)`
  - `PeerState.TryCountStatsRequest(long nowMs, int minIntervalMs)`
  - `NetworkListener` 생성자 `(ServerOptions, InboundChannels, ServerStats, HealthCounters, StatsQueryQueue, ILogger)`
  - `GameLoop` 생성자 끝에 `StatsQueryQueue? statsQueries = null`(null이면 아무도 답하지 않는 큐를 만든다), internal `StatsQueries`
  - `HealthCounters.StatsQueries`(`Func<StatsQueryCounts>?`, `GameLoop` 생성자가 넣는다)
  - Health 줄 끝: `stats requests=… limited=… busy=… unavailable=… undelivered=…`. Meter: `projecth.stats_queries`(태그 `result` = requests, limited, busy, unavailable, undelivered)
  - 테스트: `HeadlessClient.SendStatsRequest()`, `HeadlessClient.StatsResponses`, `StatsQueryIntegrationTests.StartService(...)`·`Stop(...)`(internal static)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/Integration/StatsQueryIntegrationTests.cs`:

```csharp
using System;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 11 D8 over real UDP: who may ask, the per-connection limit, Busy, Unavailable without persistence, and an
// answer whose connection left. The database itself is in MySqlTests and StatsQueryTests.
public sealed class StatsQueryIntegrationTests
{
    private static GameLoop StartServer(StatsQueryQueue queue)
    {
        var loop = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout, statsQueries: queue);
        loop.Start();
        return loop;
    }

    // Persistence off: every request is answered Unavailable at once.
    internal static StatsQueryService StartService(StatsQueryQueue queue, PersistenceOptions? options = null)
    {
        var service = new StatsQueryService(queue, Options.Create(options ?? new PersistenceOptions { Enabled = false }),
            NullLogger<StatsQueryService>.Instance);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return service;
    }

    internal static void Stop(StatsQueryService service)
    {
        service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        service.Dispose();
    }

    [Fact]
    public void ARequestBeforeTheJoin_IsIgnored()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQueryService service = StartService(queue);
        try
        {
            using var client = new HeadlessClient();
            client.Connect(server.LocalPort, "early");
            Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
            client.SendStatsRequest();

            Assert.True(Pump.Until(() => queue.Counts.Limited == 1, 3000, client), "counted");
            Assert.False(Pump.Until(() => client.StatsResponses.Count > 0, 500, client), "no answer");
            Assert.Equal(0, queue.Counts.Requests);
            Assert.Equal(0, server.Health.BadPacketsTotal);
            Assert.False(client.Disconnected);
        }
        finally
        {
            Stop(service);
        }
    }

    [Fact]
    public void WithPersistenceOff_TheAnswerIsUnavailable()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQueryService service = StartService(queue);
        try
        {
            using var client = Join(server, "nodb");
            client.SendStatsRequest();
            Assert.True(Pump.Until(() => client.StatsResponses.Count == 1, 3000, client), "answer");
            Assert.Equal(StatsStatus.Unavailable, client.StatsResponses[0].Status);
            Assert.Empty(client.StatsResponses[0].Rows);
            Assert.Equal(new StatsQueryCounts(1, 0, 0, 1, 0), queue.Counts);
        }
        finally
        {
            Stop(service);
        }
    }

    // D8: one request per 2 s per connection. More are dropped and counted, never answered and never a kick, even far
    // above the bad-packet threshold (20).
    [Fact]
    public void RequestsWithinTwoSeconds_GetNoAnswer_AreCounted_AndNeverKick()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQueryService service = StartService(queue);
        try
        {
            using var client = Join(server, "masher");
            for (int i = 0; i < 30; i++) client.SendStatsRequest();
            Assert.True(Pump.Until(() => client.StatsResponses.Count == 1 && queue.Counts.Limited == 29, 3000, client), "one answer");
            Assert.False(Pump.Until(() => client.StatsResponses.Count > 1, 700, client), "no second answer");
            Assert.Equal(1, queue.Counts.Requests);
            Assert.Equal(0, server.Health.BadPacketsTotal);
            Assert.Equal(0, server.Health.Kicks(DisconnectCode.Kicked));
            Assert.False(client.Disconnected);
        }
        finally
        {
            Stop(service);
        }
    }

    [Fact]
    public void AFullRequestQueue_AnswersBusy()
    {
        // Capacity 1 and nobody reading: the first request fills the queue, the next one is answered Busy.
        var queue = new StatsQueryQueue(1);
        using GameLoop server = StartServer(queue);
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        a.SendStatsRequest();
        Assert.True(Pump.Until(() => queue.Counts.Requests == 1, 3000, a, b), "queued");
        b.SendStatsRequest();
        Assert.True(Pump.Until(() => b.StatsResponses.Count == 1, 3000, a, b), "busy answer");
        Assert.Equal(StatsStatus.Busy, b.StatsResponses[0].Status);
        Assert.Empty(a.StatsResponses);
        Assert.Equal(1, queue.Counts.Busy);
    }

    [Fact]
    public void AnAnswerForAConnectionThatLeft_IsDropped()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        var leaver = Join(server, "leaver");
        leaver.SendStatsRequest();
        Assert.True(Pump.Until(() => queue.Counts.Requests == 1, 3000, leaver), "queued");
        Assert.True(queue.Requests.TryRead(out StatsQuery query));

        leaver.Dispose();
        Assert.True(Pump.Until(() => server.Health.Peers == 0, 3000), "the server saw the leave");
        Assert.True(queue.TryReply(new StatsReply(query.PeerId, query.Peer, StatsResponse.Of(StatsStatus.NoRecord))));
        Assert.True(Pump.Until(() => queue.Counts.Undelivered == 1, 3000), "dropped");
    }

    [Fact]
    public void ARequestWithABody_IsAnInvalidPacket()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        using var client = Join(server, "body");
        client.SendRaw(new byte[] { (byte)PacketId.StatsRequest, 1 });
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.Malformed) == 1, 3000, client), "malformed");
        Assert.Equal(0, queue.Counts.Requests);
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/Persistence/StatsQueryTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Persistence;

// Phase 11 D8 without a database: the queue's limits and counters, the wire form of the store's results, and a service
// that never fails the server (persistence off, a database that does not answer).
public class StatsQueryTests
{
    private static StatsQuery Query(string id) => new(1, null!, id);

    private static async Task<StatsReply> NextReplyAsync(StatsQueryQueue queue, int timeoutMs = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (queue.TryTakeReply(out StatsReply reply)) return reply;
            await Task.Delay(20);
        }
        throw new TimeoutException("no reply");
    }

    [Fact]
    public void TheQueue_RejectsWhenFull_AndCounts()
    {
        var queue = new StatsQueryQueue(2);
        Assert.Equal(2, queue.Capacity);
        Assert.True(queue.TryEnqueue(Query("a")));
        Assert.True(queue.TryEnqueue(Query("b")));
        Assert.False(queue.TryEnqueue(Query("c")));

        Assert.True(queue.TryReply(new StatsReply(1, null!, StatsResponse.Of(StatsStatus.Busy))));
        Assert.True(queue.TryReply(new StatsReply(1, null!, StatsResponse.Of(StatsStatus.Unavailable))));
        Assert.False(queue.TryReply(new StatsReply(1, null!, StatsResponse.Of(StatsStatus.Ok))));
        queue.AddLimited();
        queue.AddUndelivered();

        Assert.Equal(new StatsQueryCounts(Requests: 2, Limited: 1, Busy: 1, Unavailable: 1, Undelivered: 2), queue.Counts);
        Assert.True(queue.TryTakeReply(out StatsReply first));
        Assert.Equal(StatsStatus.Busy, first.Response.Status);
    }

    [Fact]
    public void BuildResponse_WithoutStatistics_IsNoRecord()
    {
        StatsResponse r = StatsQueryService.BuildResponse(null, Array.Empty<MatchHistoryEntry>());
        Assert.Equal(StatsStatus.NoRecord, r.Status);
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void BuildResponse_ConvertsClampsAndKeepsTenRows()
    {
        var stats = new PlayerStats("p", Matches: 12, Wins: 3, Kills: 40, Deaths: 9, Damage: 5_000_000_000L, SurvivalMs: 3_723_999L);
        var ended = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var history = new List<MatchHistoryEntry>();
        for (int i = 0; i < 12; i++)
            history.Add(new MatchHistoryEntry(100 - i, 50 - i, ended.AddMinutes(-i), 16, (byte)(i + 1), i, 100 * i, 60_000 + i));

        StatsResponse r = StatsQueryService.BuildResponse(stats, history);

        Assert.Equal(StatsStatus.Ok, r.Status);
        Assert.Equal(12u, r.Summary.Matches);
        Assert.Equal(3u, r.Summary.Wins);
        Assert.Equal(40u, r.Summary.Kills);
        Assert.Equal(9u, r.Summary.Deaths);
        Assert.Equal(uint.MaxValue, r.Summary.Damage);      // the 64-bit sum is clamped
        Assert.Equal(3723u, r.Summary.SurvivalSeconds);     // whole seconds
        Assert.Equal(StatsResponse.MaxRows, r.Rows.Length);
        Assert.Equal((uint)new DateTimeOffset(ended).ToUnixTimeSeconds(), r.Rows[0].EndedUnixSeconds);
        Assert.Equal(50u, r.Rows[0].Round);
        Assert.Equal(16, r.Rows[0].Players);
        Assert.Equal(1, r.Rows[0].Placement);
        Assert.Equal(9, r.Rows[9].Kills);
        Assert.Equal(900u, r.Rows[9].Damage);
        Assert.Equal(60_009u, r.Rows[9].SurvivalMs);
    }

    [Fact]
    public async Task TheService_WithPersistenceOff_AnswersUnavailable()
    {
        var queue = new StatsQueryQueue();
        using var service = new StatsQueryService(queue, Options.Create(new PersistenceOptions { Enabled = false }),
            NullLogger<StatsQueryService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(new StatsQuery(7, null!, "x")));
        StatsReply reply = await NextReplyAsync(queue);
        Assert.Equal(7, reply.PeerId);
        Assert.Equal(StatsStatus.Unavailable, reply.Response.Status);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
    }

    // D8: a database that accepts the connection but never answers. Each query ends at its limit with Unavailable, and
    // the service goes on with the next one.
    [Fact]
    public async Task TheService_WhenTheDatabaseDoesNotAnswer_AnswersUnavailable_WithinTheLimit()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var queue = new StatsQueryQueue();
            var options = Options.Create(new PersistenceOptions
            {
                Enabled = true,
                ConnectionString = $"Server=127.0.0.1;Port={port};Database=projecth;User ID=nobody;Password=none;Connection Timeout=30;Pooling=false",
            });
            using var service = new StatsQueryService(queue, options, NullLogger<StatsQueryService>.Instance)
            {
                QueryTimeout = TimeSpan.FromMilliseconds(500),
            };
            await service.StartAsync(CancellationToken.None);

            var clock = Stopwatch.StartNew();
            Assert.True(queue.TryEnqueue(Query("a")));
            Assert.Equal(StatsStatus.Unavailable, (await NextReplyAsync(queue)).Response.Status);
            Assert.True(clock.ElapsedMilliseconds < 5000, $"took {clock.ElapsedMilliseconds} ms");

            Assert.True(queue.TryEnqueue(Query("b")));
            Assert.Equal(StatsStatus.Unavailable, (await NextReplyAsync(queue)).Response.Status);
            Assert.Equal(2, queue.Counts.Unavailable);

            await service.StopAsync(CancellationToken.None);
            Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
        }
        finally
        {
            silent.Stop();
            silent.Dispose();
        }
    }

    [Fact]
    public void TheDefaultLimit_IsThreeSeconds()
    {
        var service = new StatsQueryService(new StatsQueryQueue(), Options.Create(new PersistenceOptions()), NullLogger<StatsQueryService>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(3), service.QueryTimeout);
        Assert.Equal(32, StatsQueryQueue.DefaultCapacity);
        Assert.Equal(2000, StatsQueryQueue.MinRequestIntervalMs);
        service.Dispose();
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t2_tests.py`로 저장하고 `python <경로>/p11_t2_tests.py E:/popol/ProjectH`로 실행한다. `HeadlessClient.cs`, `MonitoringTests.cs`, `MySqlTests.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Server/tests/ProjectH.Server.Tests/Integration/HeadlessClient.cs', [
(r"""    public List<MatchResult> MatchResults { get; } = new();
""",
 r"""    public List<MatchResult> MatchResults { get; } = new();
    // Phase 11 D8: every StatsResponse in arrival order.
    public List<StatsResponse> StatsResponses { get; } = new();
"""),
(r"""    public void SendRaw(byte[] data) => _peer.Send(data, DeliveryMethod.ReliableOrdered);
""",
 r"""    public void SendRaw(byte[] data) => _peer.Send(data, DeliveryMethod.ReliableOrdered);

    public void SendStatsRequest()
    {
        var writer = new PacketWriter(_buffer);
        StatsRequest.Write(ref writer);
        _peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }
"""),
(r"""                if (MatchResult.TryRead(ref r, out var result)) MatchResults.Add(result);
                break;
""",
 r"""                if (MatchResult.TryRead(ref r, out var result)) MatchResults.Add(result);
                break;
            case PacketId.StatsResponse:
                if (StatsResponse.TryRead(ref r, out var stats)) StatsResponses.Add(stats);
                break;
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Diagnostics/MonitoringTests.cs', [
(r"""        loop.Health.AddBadPacket(BadPacketReason.WrongDirection);
        loop.RunTickGuarded();
""",
 r"""        loop.Health.AddBadPacket(BadPacketReason.WrongDirection);
        loop.StatsQueries.AddLimited();
        loop.StatsQueries.AddLimited();
        loop.RunTickGuarded();
"""),
(r"""                     "db saved=3 failed=1 discarded=2 dropped=4",
""",
 r"""                     "db saved=3 failed=1 discarded=2 dropped=4",
                     "stats requests=0 limited=2 busy=0 unavailable=0 undelivered=0",
"""),
(r"""        var health = new HealthCounters { Persistence = () => new PersistenceCounts(5, 0, 0, 1) };
""",
 r"""        var health = new HealthCounters
        {
            Persistence = () => new PersistenceCounts(5, 0, 0, 1),
            StatsQueries = () => new StatsQueryCounts(Requests: 6, Limited: 2, Busy: 1, Unavailable: 3, Undelivered: 4),
        };
"""),
(r"""        Assert.Contains(("projecth.grace_expiries", 1L, ""), seen);
""",
 r"""        Assert.Contains(("projecth.grace_expiries", 1L, ""), seen);
        Assert.Contains(("projecth.stats_queries", 6L, "result=requests"), seen);
        Assert.Contains(("projecth.stats_queries", 2L, "result=limited"), seen);
        Assert.Contains(("projecth.stats_queries", 1L, "result=busy"), seen);
        Assert.Contains(("projecth.stats_queries", 3L, "result=unavailable"), seen);
        Assert.Contains(("projecth.stats_queries", 4L, "result=undelivered"), seen);
"""),
])

edit('Server/tests/ProjectH.Server.Tests/Persistence/MySqlTests.cs', [
(r"""        Assert.Equal(1, writer.Saved);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(1, writer.Discarded);
        Assert.Null(await new MatchStore(MySqlFactAttribute.ConnectionString).GetStatsAsync(blocked, CancellationToken.None));
    }
}
""",
 r"""        Assert.Equal(1, writer.Saved);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(1, writer.Discarded);
        Assert.Null(await new MatchStore(MySqlFactAttribute.ConnectionString).GetStatsAsync(blocked, CancellationToken.None));
    }

    // Phase 11 D8: what a player asks for, through the service as the server runs it.
    private static async Task<StatsResponse> AskAsync(string devPlayerId)
    {
        var queue = new StatsQueryQueue();
        var options = Options.Create(new PersistenceOptions { Enabled = true, ConnectionString = MySqlFactAttribute.ConnectionString });
        using var service = new StatsQueryService(queue, options, NullLogger<StatsQueryService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(new StatsQuery(1, null!, devPlayerId)));
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 10000)
            {
                if (queue.TryTakeReply(out StatsReply reply)) return reply.Response;
                await Task.Delay(20);
            }
            throw new TimeoutException("no reply");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [MySqlFact]
    public async Task AStatsQuery_AfterSavedMatches_AnswersOk_WithTotalsAndTheNewestMatchFirst()
    {
        MatchStore store = await StoreAsync();
        string a = NewId("q"), b = NewId("r");
        await store.SaveAsync(Match(21, new PlayerRecord(a, 1, 2, 300, 240000), new PlayerRecord(b, 2, 1, 150, 200000)), CancellationToken.None);
        await store.SaveAsync(Match(22, new PlayerRecord(a, 2, 0, 50, 90500), new PlayerRecord(b, 1, 3, 400, 250000)), CancellationToken.None);

        StatsResponse r = await AskAsync(a);

        Assert.Equal(StatsStatus.Ok, r.Status);
        Assert.Equal(2u, r.Summary.Matches);
        Assert.Equal(1u, r.Summary.Wins);
        Assert.Equal(2u, r.Summary.Kills);
        Assert.Equal(1u, r.Summary.Deaths);
        Assert.Equal(350u, r.Summary.Damage);
        Assert.Equal(330u, r.Summary.SurvivalSeconds);
        Assert.Equal(2, r.Rows.Length);
        Assert.Equal(22u, r.Rows[0].Round);
        Assert.Equal(2, r.Rows[0].Placement);
        Assert.Equal(90500u, r.Rows[0].SurvivalMs);
        Assert.Equal(21u, r.Rows[1].Round);
        Assert.Equal(1, r.Rows[1].Placement);
        Assert.Equal(2, r.Rows[1].Players);
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - r.Rows[0].EndedUnixSeconds) < 600);
    }

    [MySqlFact]
    public async Task AStatsQuery_ForAnIdWithNoMatch_AnswersNoRecord()
    {
        await StoreAsync();
        StatsResponse r = await AskAsync(NewId("none"));
        Assert.Equal(StatsStatus.NoRecord, r.Status);
        Assert.Empty(r.Rows);
    }
}
"""),
(r"""using ProjectH.Server.Persistence;
""",
 r"""using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
"""),
])
print('p11_t2_tests ok')
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. `StatsQueryQueue`, `StatsQueryService`, `StatsQuery`, `StatsReply`, `StatsQueryCounts`가 없다(CS0246). 계획 단계에서 오류는 이 한 종류였다.

- [ ] **Step 3: 전적 조회 경로를 구현한다**

흐름: 수신 스레드(`NetworkListener`) → 요청 채널 → `StatsQueryService`(DB) → 응답 채널 → Game Loop(`SendStatsReplies`, `Match.Tick` 앞) → Client. 수신 스레드가 만든 `Busy` 답도 응답 채널로 간다. Game Loop가 보내는 일만 하므로 Tick이 DB를 기다리지 않는다.

**새 파일** `Server/src/ProjectH.Server/Persistence/StatsQueryQueue.cs`:

```csharp
using System.Threading;
using System.Threading.Channels;
using LiteNetLib;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Persistence;

// Phase 11 D8: one statistics request of a connection. Peer is carried for the same reason as in the inbound messages:
// LiteNetLib reuses peer ids, so the game loop answers only this very connection.
public readonly record struct StatsQuery(int PeerId, NetPeer Peer, string DevPlayerId);

// The answer to one StatsQuery. Only the game loop sends it (it sends every packet).
public readonly record struct StatsReply(int PeerId, NetPeer Peer, StatsResponse Response);

// Phase 11 D8: what the statistics path did so far (any thread).
//   Requests: accepted into the request queue. Limited: dropped without an answer (before the join, or within
//   MinRequestIntervalMs of the connection's previous request). Busy / Unavailable: answers with that status.
//   Undelivered: answers that never went out (the reply queue was full, or the connection was gone).
public readonly record struct StatsQueryCounts(long Requests, long Limited, long Busy, long Unavailable, long Undelivered);

// Phase 11 D8 (§44): the two bounded queues of the statistics path and its counters. No lock: Channels and Interlocked.
//   Requests: LiteNetLib's threads -> StatsQueryService (one reader). Capacity requests. Full: Reject, and the network
//     thread answers Busy at once instead.
//   Replies: StatsQueryService (results) and LiteNetLib's threads (Busy) -> the game loop, which drains it every tick and
//     sends. Capacity replies. Full: Reject, the reply is dropped and counted (the client shows "no answer" after 5 s).
// Neither writer ever waits. Created once (Program, or GameLoop when none is given) and lives as long as the server.
public sealed class StatsQueryQueue
{
    public const int DefaultCapacity = 32;
    // D8: at most one request per connection per this many milliseconds; the rest are dropped and counted as Limited.
    public const int MinRequestIntervalMs = 2000;

    private readonly Channel<StatsQuery> _requests;
    private readonly Channel<StatsReply> _replies;
    private long _accepted;
    private long _limited;
    private long _busy;
    private long _unavailable;
    private long _undelivered;

    public StatsQueryQueue(int capacity = DefaultCapacity)
    {
        Capacity = capacity;
        // SingleWriter is false: with UnsyncedEvents LiteNetLib may raise receive events from more than one thread, and
        // the reply queue is also written by StatsQueryService.
        _requests = Channel.CreateBounded<StatsQuery>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // with TryWrite: returns false instead of waiting
            SingleReader = true,
            SingleWriter = false,
        });
        _replies = Channel.CreateBounded<StatsReply>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public int Capacity { get; }
    public ChannelReader<StatsQuery> Requests => _requests.Reader;

    public StatsQueryCounts Counts => new(Interlocked.Read(ref _accepted), Interlocked.Read(ref _limited),
        Interlocked.Read(ref _busy), Interlocked.Read(ref _unavailable), Interlocked.Read(ref _undelivered));

    // Network thread. False = the request queue is full: the caller answers Busy.
    public bool TryEnqueue(in StatsQuery query)
    {
        if (!_requests.Writer.TryWrite(query)) return false;
        Interlocked.Increment(ref _accepted);
        return true;
    }

    // StatsQueryService or a network thread. False = the reply queue is full: the reply is dropped and counted.
    public bool TryReply(in StatsReply reply)
    {
        if (reply.Response.Status == StatsStatus.Busy) Interlocked.Increment(ref _busy);
        else if (reply.Response.Status == StatsStatus.Unavailable) Interlocked.Increment(ref _unavailable);
        if (_replies.Writer.TryWrite(reply)) return true;
        Interlocked.Increment(ref _undelivered);
        return false;
    }

    // Game loop only.
    public bool TryTakeReply(out StatsReply reply) => _replies.Reader.TryRead(out reply);

    public void AddLimited() => Interlocked.Increment(ref _limited);
    public void AddUndelivered() => Interlocked.Increment(ref _undelivered);
}
```

**새 파일** `Server/src/ProjectH.Server/Persistence/StatsQueryService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Persistence;

// Phase 11 D8: answers statistics requests off the game loop. The only reader of StatsQueryQueue.Requests: one query at
// a time on its own task, each answered within QueryTimeout (3 s), with its own MatchStore (the same connection string as the
// writer; every call opens a pooled connection and returns it). The answer goes to the reply queue; the game loop sends
// it. Nothing escapes (Phase 9 D7): a database error or a timeout answers Unavailable, and with Persistence:Enabled=false
// every request is answered Unavailable at once. On shutdown the host stops the game loop first (Program registers this
// service before it), so the requests left in the queue have nobody to answer and are simply not read.
public sealed class StatsQueryService : BackgroundService
{
    private readonly StatsQueryQueue _queue;
    private readonly PersistenceOptions _options;
    private readonly ILogger<StatsQueryService> _logger;
    // True while the database fails, so a failure is logged when it starts and the recovery once, not every request.
    // Only the ExecuteAsync task reads or writes it.
    private bool _failing;

    public StatsQueryService(StatsQueryQueue queue, IOptions<PersistenceOptions> options, ILogger<StatsQueryService> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    // D8: how long one query (both statements) may take. Test seam.
    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MatchStore? store = _options.Enabled ? new MatchStore(_options.ConnectionString) : null;
        if (store == null) _logger.LogInformation("Stats queries: persistence disabled; every request is answered Unavailable.");
        try
        {
            await foreach (StatsQuery query in _queue.Requests.ReadAllAsync(stoppingToken))
            {
                StatsResponse response;
                try
                {
                    response = store == null ? StatsResponse.Of(StatsStatus.Unavailable) : await QueryAsync(store, query.DevPlayerId, stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    // QueryAsync handles the database's errors; this is a bug. The request still gets an answer.
                    _logger.LogError(e, "Stats queries: unexpected error");
                    response = StatsResponse.Of(StatsStatus.Unavailable);
                }
                _queue.TryReply(new StatsReply(query.PeerId, query.Peer, response));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception e)
        {
            // A faulted ExecuteAsync would stop the host. Requests are no longer read; the full queue answers Busy.
            _logger.LogError(e, "Stats queries: the service stopped on an unexpected error");
        }
    }

    private async Task<StatsResponse> QueryAsync(MatchStore store, string devPlayerId, CancellationToken stoppingToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        limit.CancelAfter(QueryTimeout);
        Task<StatsResponse> query = ReadAsync(store, devPlayerId, limit.Token);
        try
        {
            // WaitAsync as well as the token: MySqlConnector does not cancel a connect that waits for the server's greeting
            // (measured: it waits for the connection string's Connection Timeout), so the token alone cannot keep the limit.
            StatsResponse response = await query.WaitAsync(QueryTimeout, stoppingToken);
            if (_failing)
            {
                _failing = false;
                _logger.LogInformation("Stats queries: the database answers again.");
            }
            return response;
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            // The time limit, a connection failure or a query error: the player sees "unavailable".
            if (!_failing)
            {
                _failing = true;
                _logger.LogWarning("Stats queries: the database failed ({Message}); answering Unavailable until it recovers.", e.Message);
            }
            return StatsResponse.Of(StatsStatus.Unavailable);
        }
        finally
        {
            // A query left behind by the limit or the shutdown ends on its own (its connect or command times out, and
            // await using returns the connection). Its error is observed here, so it is not reported as unobserved.
            if (!query.IsCompleted) _ = query.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private static async Task<StatsResponse> ReadAsync(MatchStore store, string devPlayerId, CancellationToken token)
    {
        PlayerStats? stats = await store.GetStatsAsync(devPlayerId, token);
        IReadOnlyList<MatchHistoryEntry> history = stats == null
            ? Array.Empty<MatchHistoryEntry>()
            : await store.GetHistoryAsync(devPlayerId, StatsResponse.MaxRows, token);
        return BuildResponse(stats, history);
    }

    // The wire form of what MatchStore returned. The database keeps 64-bit sums; the packet carries uint32 values,
    // clamped, and the total survival time in whole seconds. At most StatsResponse.MaxRows matches, newest first.
    internal static StatsResponse BuildResponse(PlayerStats? stats, IReadOnlyList<MatchHistoryEntry> history)
    {
        if (stats == null) return StatsResponse.Of(StatsStatus.NoRecord);
        int count = Math.Min(history.Count, StatsResponse.MaxRows);
        var rows = new StatsRow[count];
        for (int i = 0; i < count; i++)
        {
            MatchHistoryEntry e = history[i];
            rows[i] = new StatsRow
            {
                EndedUnixSeconds = Clamp(new DateTimeOffset(DateTime.SpecifyKind(e.EndedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()),
                Round = Clamp(e.Round),
                Players = (byte)Math.Clamp(e.Players, 0, byte.MaxValue),
                Placement = e.Placement,
                Kills = (ushort)Math.Clamp(e.Kills, 0, ushort.MaxValue),
                Damage = Clamp(e.Damage),
                SurvivalMs = Clamp(e.SurvivalMs),
            };
        }
        return new StatsResponse
        {
            Status = StatsStatus.Ok,
            Summary = new StatsSummary
            {
                Matches = Clamp(stats.Matches),
                Wins = Clamp(stats.Wins),
                Kills = Clamp(stats.Kills),
                Deaths = Clamp(stats.Deaths),
                Damage = Clamp(stats.Damage),
                SurvivalSeconds = Clamp(stats.SurvivalMs / 1000),
            },
            Rows = rows,
        };
    }

    private static uint Clamp(long value) => value <= 0 ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)value;
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t2_impl.py`로 저장하고 `python <경로>/p11_t2_impl.py E:/popol/ProjectH`로 실행한다. `PeerState.cs`, `NetworkListener.cs`, `HealthCounters.cs`, `ServerMeter.cs`, `GameLoop.cs`, `GameServerService.cs`, `Program.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Server/src/ProjectH.Server/Net/PeerState.cs', [
(r"""//   - BadPackets, Kicked, JoinRequested and the input rate window are touched only on LiteNetLib's receive path, so
""",
 r"""//   - BadPackets, Kicked, JoinRequested, the input rate window and the statistics request time (Phase 11) are touched
//     only on LiteNetLib's receive path, so
"""),
(r"""        return ++_inputPacketsInWindow <= maxPerSecond;
    }
}
""",
 r"""        return ++_inputPacketsInWindow <= maxPerSecond;
    }

    private bool _statsRequested;
    private long _lastStatsRequestMs;

    // Phase 11 D8: at most one statistics request per minIntervalMs. A refused request does not move the window, so
    // pressing the button again and again still gets one answer every minIntervalMs.
    public bool TryCountStatsRequest(long nowMs, int minIntervalMs)
    {
        if (_statsRequested && nowMs - _lastStatsRequestMs < minIntervalMs) return false;
        _statsRequested = true;
        _lastStatsRequestMs = nowMs;
        return true;
    }
}
"""),
])

edit('Server/src/ProjectH.Server/Net/NetworkListener.cs', [
(r"""using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;
""",
 r"""using ProjectH.Server.Diagnostics;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
"""),
(r"""    private readonly HealthCounters _health;
    private readonly ILogger _logger;
""",
 r"""    private readonly HealthCounters _health;
    private readonly StatsQueryQueue _statsQueries;
    private readonly ILogger _logger;
"""),
(r"""    public NetworkListener(ServerOptions options, InboundChannels channels, ServerStats stats, HealthCounters health, ILogger logger)
    {
        _options = options;
        _channels = channels;
        _stats = stats;
        _health = health;
        _logger = logger;
""",
 r"""    public NetworkListener(ServerOptions options, InboundChannels channels, ServerStats stats, HealthCounters health,
        StatsQueryQueue statsQueries, ILogger logger)
    {
        _options = options;
        _channels = channels;
        _stats = stats;
        _health = health;
        _statsQueries = statsQueries;
        _logger = logger;
"""),
(r"""            default:
                // Server-to-client packet ids are never valid from a client.
""",
 r"""            case PacketId.StatsRequest:
                // Phase 11 D8: the request has no body. It is taken only from a connection that asked to join (this
                // thread's view of "joined") and at most once per MinRequestIntervalMs; otherwise it is dropped without
                // an answer and counted as Limited, not as an invalid packet, so pressing the button repeatedly never
                // gets a player kicked. A full request queue is answered Busy at once.
                if (packet.Remaining != 0)
                {
                    OnBadPacket(peer, BadPacketReason.Malformed);
                    break;
                }
                if (peer.Tag is not PeerState statsState || !statsState.JoinRequested ||
                    !statsState.TryCountStatsRequest(Environment.TickCount64, StatsQueryQueue.MinRequestIntervalMs))
                {
                    _statsQueries.AddLimited();
                    break;
                }
                if (!_statsQueries.TryEnqueue(new StatsQuery(peer.Id, peer, statsState.DevPlayerId)))
                    _statsQueries.TryReply(new StatsReply(peer.Id, peer, StatsResponse.Of(StatsStatus.Busy)));
                break;

            default:
                // Server-to-client packet ids are never valid from a client.
"""),
])

edit('Server/src/ProjectH.Server/Diagnostics/HealthCounters.cs', [
(r"""    // Phase 9 counters of the match history writer (null = no writer, e.g. tests). Set once before the loop starts.
    public Func<PersistenceCounts>? Persistence { get; set; }
""",
 r"""    // Phase 9 counters of the match history writer (null = no writer, e.g. tests). Set once before the loop starts.
    public Func<PersistenceCounts>? Persistence { get; set; }
    // Phase 11 D8 counters of the statistics path (StatsQueryQueue). Set once by GameLoop's constructor.
    public Func<StatsQueryCounts>? StatsQueries { get; set; }
"""),
])

edit('Server/src/ProjectH.Server/Diagnostics/ServerMeter.cs', [
(r"""        _meter.CreateObservableCounter("projecth.db_records", () => DbRecords(h));
    }
""",
 r"""        _meter.CreateObservableCounter("projecth.db_records", () => DbRecords(h));
        _meter.CreateObservableCounter("projecth.stats_queries", () => StatsQueries(h));
    }
"""),
(r"""            new Measurement<long>(c.Dropped, Tag("result", "dropped")),
        };
    }
}
""",
 r"""            new Measurement<long>(c.Dropped, Tag("result", "dropped")),
        };
    }

    // Phase 11 D8: the statistics path.
    private static Measurement<long>[] StatsQueries(HealthCounters h)
    {
        if (h.StatsQueries is not { } source) return Array.Empty<Measurement<long>>();
        StatsQueryCounts c = source();
        return new[]
        {
            new Measurement<long>(c.Requests, Tag("result", "requests")),
            new Measurement<long>(c.Limited, Tag("result", "limited")),
            new Measurement<long>(c.Busy, Tag("result", "busy")),
            new Measurement<long>(c.Unavailable, Tag("result", "unavailable")),
            new Measurement<long>(c.Undelivered, Tag("result", "undelivered")),
        };
    }
}
"""),
])

edit('Server/src/ProjectH.Server/GameLoop.cs', [
(r"""    private readonly Action<Persistence.MatchRecord>? _matchSink;
    private Match _match;
""",
 r"""    private readonly Action<Persistence.MatchRecord>? _matchSink;
    // Phase 11 D8: requests in (network threads), answers out (this thread sends them). Buffer for one answer.
    private readonly StatsQueryQueue _statsQueries;
    private readonly byte[] _statsReplyBuffer = new byte[StatsResponse.MaxSize];
    private Match _match;
"""),
(r"""    // onFatal: Phase 10 D6, called once (on the loop thread, must not block) when resets keep failing; production stops
    // the host with exit code 1. time: the clock of the reset window (tests pass a manual one).
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null,
        Action? onFatal = null, TimeProvider? time = null)
    {
""",
 r"""    // onFatal: Phase 10 D6, called once (on the loop thread, must not block) when resets keep failing; production stops
    // the host with exit code 1. time: the clock of the reset window (tests pass a manual one).
    // statsQueries: Phase 11 D8, the statistics path shared with StatsQueryService; null = a queue nobody answers (tests
    // that do not need answers), so requests wait there and, once it is full, are answered Busy.
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null,
        Action? onFatal = null, TimeProvider? time = null, StatsQueryQueue? statsQueries = null)
    {
"""),
(r"""        _listener = new NetworkListener(options, _channels, _stats, _health, logger);
""",
 r"""        _statsQueries = statsQueries ?? new StatsQueryQueue();
        _health.StatsQueries = () => _statsQueries.Counts;
        _listener = new NetworkListener(options, _channels, _stats, _health, _statsQueries, logger);
"""),
(r"""    internal NetworkListener Listener => _listener;
""",
 r"""    internal NetworkListener Listener => _listener;
    internal StatsQueryQueue StatsQueries => _statsQueries;
"""),
(r"""        DrainInput();
        SweepPeers();
        _match.Tick();
""",
 r"""        DrainInput();
        SweepPeers();
        SendStatsReplies();
        _match.Tick();
"""),
(r"""    private void SendToPeer(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method)
""",
 r"""    // Phase 11 D8: the answers StatsQueryService (or a network thread, for Busy) left in the reply queue, at most the
    // queue's capacity per tick, so this never waits. Before Match.Tick, so a failing match does not hold them back. An
    // answer whose connection is gone, or whose peer id now belongs to another connection, is dropped and counted.
    private void SendStatsReplies()
    {
        for (int budget = _statsQueries.Capacity; budget > 0 && _statsQueries.TryTakeReply(out StatsReply reply); budget--)
        {
            if (!_peers.TryGetValue(reply.PeerId, out NetPeer? peer) || !ReferenceEquals(peer, reply.Peer))
            {
                _statsQueries.AddUndelivered();
                continue;
            }
            var writer = new PacketWriter(_statsReplyBuffer);
            StatsResponse.Write(ref writer, reply.Response);
            peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            _stats.AddOut(writer.Length);
        }
    }

    private void SendToPeer(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method)
"""),
(r"""        PersistenceCounts db = h.Persistence?.Invoke() ?? default;
""",
 r"""        PersistenceCounts db = h.Persistence?.Invoke() ?? default;
        StatsQueryCounts sq = h.StatsQueries?.Invoke() ?? default;
"""),
(r"""            "db saved={DbSaved} failed={DbFailed} discarded={DbDiscarded} dropped={DbDropped}",
""",
 r"""            "db saved={DbSaved} failed={DbFailed} discarded={DbDiscarded} dropped={DbDropped} " +
            "stats requests={StatsRequests} limited={StatsLimited} busy={StatsBusy} unavailable={StatsUnavailable} undelivered={StatsUndelivered}",
"""),
(r"""            db.Saved, db.Failed, db.Discarded, db.Dropped);
""",
 r"""            db.Saved, db.Failed, db.Discarded, db.Dropped,
            sq.Requests, sq.Limited, sq.Busy, sq.Unavailable, sq.Undelivered);
"""),
])

edit('Server/src/ProjectH.Server/GameServerService.cs', [
(r"""    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        MatchHistoryWriter writer, IHostApplicationLifetime lifetime)
""",
 r"""    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        MatchHistoryWriter writer, StatsQueryQueue statsQueries, IHostApplicationLifetime lifetime)
"""),
(r"""            onFatal: () =>
            {
                Environment.ExitCode = 1;
                _ = Task.Run(lifetime.StopApplication);
            });
""",
 r"""            onFatal: () =>
            {
                Environment.ExitCode = 1;
                _ = Task.Run(lifetime.StopApplication);
            },
            // Phase 11 D8: statistics requests go to StatsQueryService through this queue; the loop sends the answers.
            statsQueries: statsQueries);
"""),
])

edit('Server/src/ProjectH.Server/Program.cs', [
(r"""builder.Services.AddHostedService(services => services.GetRequiredService<MatchHistoryWriter>());
builder.Services.AddHostedService<GameServerService>();
""",
 r"""builder.Services.AddHostedService(services => services.GetRequiredService<MatchHistoryWriter>());
// Phase 11 D8: statistics on request. The queue links the network threads, StatsQueryService and the game loop. The
// service is registered before the game server, so on shutdown the game loop stops first (no request comes in and no
// answer goes out after that), then the service.
builder.Services.AddSingleton(_ => new StatsQueryQueue(StatsQueryQueue.DefaultCapacity));
builder.Services.AddHostedService<StatsQueryService>();
builder.Services.AddHostedService<GameServerService>();
"""),
])
print('p11_t2_impl ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~StatsQuery|FullyQualifiedName~Monitoring|FullyQualifiedName~Hardening|FullyQualifiedName~Fuzz"`
Expected: 모두 통과(MySQL 2개 건너뜀).

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 785개다(776 통과, MySQL 9개 건너뜀). Task 1보다 14개(그중 `[MySqlFact]` 2개) 많다. 계획 단계에서 같은 결과였다.

- [ ] **Step 5: 서버 Host를 확인한다**

`Program`의 등록(큐 싱글턴, `StatsQueryService`, `GameServerService`의 새 생성자 인자)은 테스트가 거치지 않는다. 그래서 빌드한 서버를 잠깐 띄운다. Persistence는 끄고 Stats 간격은 2초로 한다.

```bash
dotnet Server/src/ProjectH.Server/bin/Debug/net10.0/ProjectH.Server.dll --Server:Port=7791 --Server:StatsIntervalSeconds=2 --Persistence:Enabled=false > host-check.log 2>&1 &
echo $! > host-check.pid
timeout 30 bash -c 'until grep -q "Health " host-check.log; do sleep 1; done'   # 첫 Health 줄까지 약 3초
grep -m1 "Stats queries: persistence disabled" host-check.log
grep -m1 -o "stats requests=0 limited=0 busy=0 unavailable=0 undelivered=0" host-check.log
kill $(cat host-check.pid)
rm host-check.log host-check.pid
```

Expected: 두 grep이 모두 한 줄씩 찾는다. 서버는 띄운 pid로만 끈다. 계획 단계에서 같은 결과였다.

- [ ] **Step 6: Commit** — `feat(server): answer statistics requests off the game loop (StatsQueryQueue, StatsQueryService, Phase 11 D8)`

---

### Task 3: Client 순수 코드 (`UiFlow`, `UiText`, `KillFeedModel`)

**Files:**
- Create: `Client/Assets/Scripts/UI/UiFlow.cs`, `UI/UiText.cs`, `UI/KillFeedModel.cs`
- Test:
  - Create: `Server/tests/ProjectH.Server.Tests/ClientUi/UiFlowTests.cs`, `ClientUi/UiTextTests.cs`, `ClientUi/KillFeedModelTests.cs`
  - Modify: `Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj`(세 파일 소스 링크)
- 저장소 밖: Unity 확인 도구(`UnityCompile`, `EditTests`)

**Interfaces:**
- Produces(namespace `ProjectH.Client.UI`):
  - `enum UiScreen { Title, Connecting, InGame, Menu, Disconnected, Result }`, `enum UiConnection { Offline, Connecting, Joined }`
  - `UiFlow`: `Screen`, `StatsOpen`, `Reconnecting`, `Version`, `AllowCursorLock`, `BlocksGameInput`, `ConnectRequested()`, `LeaveRequested()`, `EscapePressed()`, `ContinuePressed()`, `OpenStats()`, `CloseStats()`, `Update(UiConnection, bool reconnecting, int results, bool hasMatch, MatchFlowState)`
  - `enum StatsWaitState { Waiting, Answered, NoAnswer }`, `static StatsWait { AnswerSeconds 5, ResendSeconds 2.5, MaySend(now, lastSentAt), Of(now, sentAt, answeredAt) }`
  - `struct DisconnectSummary { StartFailed, Reason(LiteNetLib), Code, Reject, Join }`
  - `static UiText`: 상수 `ZoneName`, `Connecting`, `StatsLoading`, `StatsNoAnswer`, `NameRule`, `PortRule`, `HostRule`. 함수 `Disconnect`, `Reject`, `Code`, `Reason`, `Reconnecting`, `ResultTitle`, `Placement`, `Kills`, `Winner`, `KilledBy`, `NextRound`, `NameOr`, `KillLine`, `StatsStatusText`, `StatsSummaryText`, `StatsRowsText`, `StatsRowText`, `Duration`, `DebugLine`, `TryNormalizeName`, `TryNormalizeHost`, `TryParsePort`
  - `KillFeedModel`: `Capacity` 5, `LineSeconds` 6, `Count`, `Version`, `Line(i)`(0 = 가장 새 줄), `Add(line, now)`, `Expire(now)`, `Clear()`

세 파일은 UnityEngine을 쓰지 않는다. 화면 코드(Task 4–5)는 이 출력만 그린다.

- [ ] **Step 1: Unity 확인 도구를 만든다(저장소 밖, 한 번만)**

이 Phase부터 Client 코드를 바꾸므로 Unity 없이 컴파일과 EditMode 문구 테스트를 확인할 도구를 만든다. 아래 경로의 `<스크래치>`는 저장소 밖 아무 폴더다(예: `%TEMP%/projecth-p11`).

1. `UnityCompile`: 컨트롤러의 원래 `UnityCompile.csproj`(`C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj`)를 읽어 같은 Unity 6000.3.24f1 참조로 새 프로젝트를 만든다. 원래 파일은 고치지 않는다. 새 프로젝트는 저장소 경로를 `RepoRoot` 속성으로 받고(기본 `E:/popol/ProjectH`), EditMode 테스트도 Unity의 `nunit.framework.dll`로 함께 컴파일한다. 원래 파일이 없어졌으면 같은 구성(`Client/Assembly-CSharp.csproj`의 `UnityEngine*`·`Unity.InputSystem` 참조)으로 다시 만든다.

아래 스크립트를 `<스크래치>/make_unitycompile.py`로 저장한다(저장소 밖).

```python
# make_unitycompile.py <existing UnityCompile.csproj> <output folder>
# Writes <output folder>/UnityCompile.csproj: the same Unity 6000.3.24f1 references as the existing project, but the
# checkout is a property (RepoRoot, default E:/popol/ProjectH) and the EditMode tests are compiled too (with Unity's
# nunit.framework.dll). The existing project is only read.
import os, re, sys
src, out = sys.argv[1], sys.argv[2]
text = open(src, encoding='utf-8-sig').read()
refs = re.findall(r'<Reference Include="(UnityEngine[^"]*|Unity\.InputSystem)"><HintPath>([^<]+)</HintPath>', text)
assert len(refs) > 50, 'unexpected source csproj: %d Unity references' % len(refs)
library = 'E:/popol/ProjectH/Client/Library'
lines = []
for name, hint in refs:
    hint = hint.replace(chr(92), '/').replace(library, '$(LibraryRoot)')
    lines.append('    <Reference Include="%s"><HintPath>%s</HintPath><Private>False</Private></Reference>' % (name, hint))
nunit = [p for p in os.listdir(library + '/PackageCache') if p.startswith('com.unity.ext.nunit@')]
assert len(nunit) == 1, nunit
project = '''<Project Sdk="Microsoft.NET.Sdk">
  <!-- Compiles Client/Assets/Scripts, Shared/Runtime and Client/Assets/Tests/EditMode against the real Unity DLLs.
       RepoRoot selects the checkout; the package DLLs come from the real repo's Library (an opened Editor makes it). -->
  <PropertyGroup>
    <RepoRoot Condition="'$(RepoRoot)' == ''">E:/popol/ProjectH</RepoRoot>
    <LibraryRoot>%s</LibraryRoot>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/**/*.cs" />
    <Compile Include="$(RepoRoot)/Shared/Runtime/**/*.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/**/*.cs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="LiteNetLib"><HintPath>$(RepoRoot)/Client/Assets/Plugins/LiteNetLib/LiteNetLib.dll</HintPath></Reference>
    <Reference Include="nunit.framework"><HintPath>$(LibraryRoot)/PackageCache/%s/net40/unity-custom/nunit.framework.dll</HintPath><Private>False</Private></Reference>
%s
  </ItemGroup>
</Project>
''' % (library, nunit[0], '\n'.join(lines))
os.makedirs(out, exist_ok=True)
open(os.path.join(out, 'UnityCompile.csproj'), 'w', encoding='utf-8', newline='\n').write(project)
print('wrote', os.path.join(out, 'UnityCompile.csproj'), len(lines), 'Unity references')
```

Run: `python <스크래치>/make_unitycompile.py C:/Users/aaa/AppData/Local/Temp/claude/e--popol-ProjectH/d2917085-fce9-4150-aedb-13ba1b855314/scratchpad/unitycompile/UnityCompile.csproj <스크래치>/uc`
Expected: `wrote .../UnityCompile.csproj 81 Unity references`

2. `EditTests`: 이 Phase가 바꾸는 순수 HUD 문구 클래스(`MatchHudText`, `InventoryHudText`)의 EditMode 테스트를 Unity 밖 NUnit으로 돌린다. 아래 내용을 `<스크래치>/edittests/EditTests.csproj`로 저장한다.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Runs the EditMode tests of the pure HUD text classes outside Unity (NUnit), against RepoRoot's sources. -->
  <PropertyGroup>
    <RepoRoot Condition="'$(RepoRoot)' == ''">E:/popol/ProjectH</RepoRoot>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NUnit" Version="3.14.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
    <PackageReference Include="LiteNetLib" Version="2.1.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="$(RepoRoot)/Server/src/ProjectH.Shared/ProjectH.Shared.csproj" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/MatchHudTextTests.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Tests/EditMode/InventoryHudTextTests.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/MatchHudText.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/InventoryHudText.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/WeaponState.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/Game/ZoneMath.cs" />
    <Compile Include="$(RepoRoot)/Client/Assets/Scripts/UI/UiText.cs" Condition="Exists('$(RepoRoot)/Client/Assets/Scripts/UI/UiText.cs')" />
  </ItemGroup>
</Project>
```

3. 지금 상태(Task 2까지)를 확인한다.

Run: `dotnet build <스크래치>/uc/UnityCompile.csproj` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/edittests/EditTests.csproj` → 10개 통과(`MatchHudTextTests` 6, `InventoryHudTextTests` 4)

- [ ] **Step 2: 실패하는 테스트를 쓴다**

**새 파일** `Server/tests/ProjectH.Server.Tests/ClientUi/UiFlowTests.cs`:

```csharp
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 11 D3, D5 (spec §2): every transition of the client's screen flow, and the cursor and input outputs. The flow is
// the Unity client's own file, compiled here through a source link (it has no UnityEngine dependency).
public class UiFlowTests
{
    private const MatchFlowState Playing = MatchFlowState.Playing;

    private static UiFlow InGame()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        flow.Update(UiConnection.Connecting, false, 0, false, Playing);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
        return flow;
    }

    [Fact]
    public void Title_Connecting_InGame()
    {
        var flow = new UiFlow();
        Assert.Equal(UiScreen.Title, flow.Screen);
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Title, flow.Screen);   // nothing happens until the player connects

        flow.ConnectRequested();
        Assert.Equal(UiScreen.Connecting, flow.Screen);
        flow.Update(UiConnection.Connecting, false, 0, false, Playing);
        Assert.Equal(UiScreen.Connecting, flow.Screen);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    [Fact]
    public void Escape_OpensAndClosesTheMenu_AndContinueCloses()
    {
        UiFlow flow = InGame();
        flow.EscapePressed();
        Assert.Equal(UiScreen.Menu, flow.Screen);
        flow.EscapePressed();
        Assert.Equal(UiScreen.InGame, flow.Screen);
        flow.EscapePressed();
        flow.ContinuePressed();
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    [Fact]
    public void Stats_OpensOverTheMenu_AndEscapeClosesItFirst()
    {
        UiFlow flow = InGame();
        flow.OpenStats();
        Assert.False(flow.StatsOpen);   // not from the game itself
        flow.EscapePressed();
        flow.OpenStats();
        Assert.True(flow.StatsOpen);
        Assert.Equal(UiScreen.Menu, flow.Screen);

        flow.EscapePressed();
        Assert.False(flow.StatsOpen);
        Assert.Equal(UiScreen.Menu, flow.Screen);
        flow.OpenStats();
        flow.CloseStats();
        Assert.False(flow.StatsOpen);
        Assert.Equal(UiScreen.Menu, flow.Screen);
    }

    [Theory]
    [InlineData(UiScreen.InGame)]
    [InlineData(UiScreen.Menu)]
    [InlineData(UiScreen.Result)]
    public void ALostConnection_ShowsDisconnected_FromAnyGameScreen(UiScreen from)
    {
        UiFlow flow = InGame();
        if (from == UiScreen.Menu) flow.EscapePressed();
        if (from == UiScreen.Result) flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(from, flow.Screen);

        flow.Update(UiConnection.Offline, true, 1, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        Assert.True(flow.Reconnecting);
        Assert.False(flow.StatsOpen);
    }

    [Fact]
    public void AnAutomaticReconnect_GoesBackIntoTheGame()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Offline, true, 0, false, Playing);
        flow.Update(UiConnection.Connecting, true, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        Assert.True(flow.Reconnecting);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
        Assert.False(flow.Reconnecting);
    }

    [Fact]
    public void ADisconnectThatIsNotRetried_OffersRetryAndTitle()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        Assert.False(flow.Reconnecting);

        flow.ConnectRequested();   // "retry"
        Assert.Equal(UiScreen.Connecting, flow.Screen);
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
        flow.LeaveRequested();     // "to title"
        Assert.Equal(UiScreen.Title, flow.Screen);
    }

    // A refused connect (version, full server), an unreachable server and a full match (the server answers MatchFull,
    // then closes) all end the connecting screen the same way; UiText says which.
    [Fact]
    public void AFailedConnect_OrAFullMatch_ShowsDisconnected()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        flow.Update(UiConnection.Offline, false, 0, false, Playing);   // Connect failed at once (bad address)
        Assert.Equal(UiScreen.Disconnected, flow.Screen);

        flow.ConnectRequested();
        flow.Update(UiConnection.Connecting, false, 0, false, Playing);   // connected, the join refused (MatchFull)
        flow.Update(UiConnection.Offline, false, 0, false, Playing);      // the server closes it
        Assert.Equal(UiScreen.Disconnected, flow.Screen);
    }

    [Fact]
    public void TheResult_OpensOnce_AndClosesWithTheNextRound()
    {
        UiFlow flow = InGame();
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Closing);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.WaitingForPlayers);
        Assert.Equal(UiScreen.InGame, flow.Screen);

        // The same result does not open it again; the next one does, and Starting closes it too.
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Playing);
        Assert.Equal(UiScreen.InGame, flow.Screen);
        flow.Update(UiConnection.Joined, false, 2, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.Update(UiConnection.Joined, false, 2, true, MatchFlowState.Starting);
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    [Fact]
    public void TheResult_ClosesByContinueOrEscape_AndOpensOverTheMenu()
    {
        UiFlow flow = InGame();
        flow.EscapePressed();   // menu open when the match ends
        flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished);
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.OpenStats();
        Assert.True(flow.StatsOpen);
        flow.EscapePressed();
        Assert.Equal(UiScreen.Result, flow.Screen);
        flow.ContinuePressed();   // "keep spectating"
        Assert.Equal(UiScreen.InGame, flow.Screen);

        flow.Update(UiConnection.Joined, false, 2, true, MatchFlowState.Finished);
        flow.EscapePressed();
        Assert.Equal(UiScreen.InGame, flow.Screen);
    }

    [Fact]
    public void DisconnectFromTheMenu_GoesToTheTitle_AndStaysThere()
    {
        UiFlow flow = InGame();
        flow.EscapePressed();
        flow.LeaveRequested();
        Assert.Equal(UiScreen.Title, flow.Screen);
        flow.Update(UiConnection.Joined, false, 0, true, Playing);    // the disconnect is not processed yet
        flow.Update(UiConnection.Offline, false, 0, false, Playing);
        Assert.Equal(UiScreen.Title, flow.Screen);
        flow.EscapePressed();
        Assert.Equal(UiScreen.Title, flow.Screen);
    }

    [Fact]
    public void CancelWhileConnecting_GoesToTheTitle()
    {
        var flow = new UiFlow();
        flow.ConnectRequested();
        flow.LeaveRequested();
        Assert.Equal(UiScreen.Title, flow.Screen);
    }

    // D5: only the bare game screen lets a click lock the cursor and lets input through.
    [Theory]
    [InlineData(UiScreen.Title, false)]
    [InlineData(UiScreen.Connecting, false)]
    [InlineData(UiScreen.InGame, true)]
    [InlineData(UiScreen.Menu, false)]
    [InlineData(UiScreen.Disconnected, false)]
    [InlineData(UiScreen.Result, false)]
    public void CursorAndInput_FollowTheScreen(UiScreen screen, bool game)
    {
        var flow = new UiFlow();
        switch (screen)
        {
            case UiScreen.Connecting: flow.ConnectRequested(); break;
            case UiScreen.InGame: flow = InGame(); break;
            case UiScreen.Menu: flow = InGame(); flow.EscapePressed(); break;
            case UiScreen.Disconnected: flow = InGame(); flow.Update(UiConnection.Offline, false, 0, false, Playing); break;
            case UiScreen.Result: flow = InGame(); flow.Update(UiConnection.Joined, false, 1, true, MatchFlowState.Finished); break;
        }
        Assert.Equal(screen, flow.Screen);
        Assert.Equal(game, flow.AllowCursorLock);
        Assert.Equal(!game, flow.BlocksGameInput);
    }

    [Fact]
    public void Version_ChangesOnlyWhenSomethingShownChanges()
    {
        UiFlow flow = InGame();
        int version = flow.Version;
        for (int i = 0; i < 10; i++) flow.Update(UiConnection.Joined, false, 0, true, Playing);
        Assert.Equal(version, flow.Version);
        flow.EscapePressed();
        Assert.NotEqual(version, flow.Version);
        version = flow.Version;
        flow.Update(UiConnection.Offline, true, 0, false, Playing);
        int afterDrop = flow.Version;
        Assert.NotEqual(version, afterDrop);
        flow.Update(UiConnection.Offline, false, 0, false, Playing);   // the reconnect gave up
        Assert.NotEqual(afterDrop, flow.Version);
    }

    [Fact]
    public void StatsWait_WaitsFiveSeconds_AndReusesARecentRequest()
    {
        Assert.Equal(StatsWaitState.NoAnswer, StatsWait.Of(10f, -1f, -1f));     // nothing could be sent
        Assert.Equal(StatsWaitState.Waiting, StatsWait.Of(10f, 8f, -1f));
        Assert.Equal(StatsWaitState.NoAnswer, StatsWait.Of(13f, 8f, -1f));
        Assert.Equal(StatsWaitState.Answered, StatsWait.Of(9f, 8f, 8.2f));
        Assert.Equal(StatsWaitState.Waiting, StatsWait.Of(20f, 19f, 8.2f));     // an older answer does not count

        Assert.True(StatsWait.MaySend(0f, -1f));
        Assert.False(StatsWait.MaySend(10f, 8f));
        Assert.True(StatsWait.MaySend(10.5f, 8f));
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/ClientUi/UiTextTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using LiteNetLib;
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 11 D4, D6-D9, D11 (spec §2): the client's Korean UI strings. Compiled here through a source link like UiFlow.
public class UiTextTests
{
    private static bool HasHangul(string text)
    {
        foreach (char c in text)
        {
            if (c >= '가' && c <= '힣') return true;
        }
        return false;
    }

    // Every reason the client can see has its own Korean sentence, and asking again returns the same constant (no
    // allocation when the screen asks every frame).
    [Fact]
    public void EveryLiteNetLibReason_HasAKoreanText()
    {
        var seen = new HashSet<string>();
        foreach (DisconnectReason reason in Enum.GetValues<DisconnectReason>())
        {
            string text = UiText.Reason(reason);
            Assert.True(HasHangul(text), reason.ToString());
            Assert.Same(text, UiText.Reason(reason));
            seen.Add(text);
        }
        Assert.Equal(Enum.GetValues<DisconnectReason>().Length, seen.Count);
    }

    [Fact]
    public void EveryDisconnectCode_AndRejectReason_HasAKoreanText()
    {
        var codes = new HashSet<string>();
        foreach (DisconnectCode code in Enum.GetValues<DisconnectCode>())
        {
            Assert.True(HasHangul(UiText.Code(code)), code.ToString());
            codes.Add(UiText.Code(code));
        }
        Assert.Equal(Enum.GetValues<DisconnectCode>().Length, codes.Count);

        var rejects = new HashSet<string>();
        foreach (RejectReason reason in Enum.GetValues<RejectReason>())
        {
            Assert.True(HasHangul(UiText.Reject(reason)), reason.ToString());
            rejects.Add(UiText.Reject(reason));
        }
        Assert.Equal(Enum.GetValues<RejectReason>().Length, rejects.Count);
    }

    [Fact]
    public void Disconnect_PicksTheMostSpecificReason()
    {
        Assert.Equal("서버가 종료되었습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.RemoteConnectionClose, Code = DisconnectCode.ServerShutdown,
        }));
        Assert.Equal("서버와 게임 버전이 다릅니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.ConnectionRejected, Reject = RejectReason.VersionMismatch,
        }));
        // A full match: the server answered MatchFull, then closed without a code.
        Assert.Equal("경기가 가득 찼습니다.", UiText.Disconnect(new DisconnectSummary
        {
            Reason = DisconnectReason.RemoteConnectionClose, Join = JoinResult.MatchFull,
        }));
        Assert.Equal("서버의 응답이 끊겼습니다.", UiText.Disconnect(new DisconnectSummary { Reason = DisconnectReason.Timeout }));
        Assert.Equal("접속을 시작할 수 없습니다. 주소와 포트를 확인하세요.", UiText.Disconnect(new DisconnectSummary { StartFailed = true }));
    }

    [Fact]
    public void Reconnecting_ShowsTheAttemptAndTheSeconds()
    {
        Assert.Equal("재접속 중 (1/3) - 2초 뒤 다시 시도", UiText.Reconnecting(1, 3, 2));
        Assert.Equal("재접속 중 (2/3) - 연결하는 중", UiText.Reconnecting(2, 3, 0));
    }

    [Fact]
    public void Result_Texts()
    {
        Assert.Equal("승리!", UiText.ResultTitle(true));
        Assert.Equal("탈락", UiText.ResultTitle(false));
        Assert.Equal("순위 3 / 12명", UiText.Placement(3, 12));
        Assert.Equal("처치 2", UiText.Kills(2));
        Assert.Equal("승자: alice", UiText.Winner("alice"));
        Assert.Equal("승자 없음", UiText.Winner(null!));
        Assert.Equal("나를 처치한 플레이어: bob", UiText.KilledBy(true, false, "bob"));
        Assert.Equal("탈락 원인: 자기장", UiText.KilledBy(true, true, null!));
        Assert.Equal(string.Empty, UiText.KilledBy(false, false, null!));
        Assert.Equal("다음 판까지 7초", UiText.NextRound(7));
        Assert.Equal("다음 판을 준비하는 중", UiText.NextRound(0));
    }

    [Fact]
    public void Names_AndTheKillLine()
    {
        Assert.Equal("alice", UiText.NameOr("alice", 3));
        Assert.Equal("플레이어 3", UiText.NameOr(null!, 3));
        Assert.Equal("alice ▸ bob", UiText.KillLine("alice", "bob"));
        Assert.Equal("자기장 ▸ bob", UiText.KillLine(null!, "bob"));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(245, "4:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-4, "0:00")]
    public void Duration_Format(long seconds, string expected)
    {
        Assert.Equal(expected, UiText.Duration(seconds));
    }

    [Fact]
    public void Stats_Texts()
    {
        Assert.Equal(string.Empty, UiText.StatsStatusText(StatsStatus.Ok));
        Assert.Equal("아직 기록이 없습니다.", UiText.StatsStatusText(StatsStatus.NoRecord));
        Assert.Equal("기록을 볼 수 없음", UiText.StatsStatusText(StatsStatus.Unavailable));
        Assert.True(HasHangul(UiText.StatsStatusText(StatsStatus.Busy)));
        Assert.Equal("응답 없음", UiText.StatsNoAnswer);

        Assert.Equal("경기 12   승리 3   처치 40   사망 9\n피해 12345   생존 시간 1:02:03", UiText.StatsSummaryText(new StatsSummary
        {
            Matches = 12, Wins = 3, Kills = 40, Deaths = 9, Damage = 12345, SurvivalSeconds = 3723,
        }));

        // 2026-10-01 12:00:00 UTC, shown in UTC+9.
        var row = new StatsRow
        {
            EndedUnixSeconds = (uint)new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            Round = 7, Players = 16, Placement = 3, Kills = 2, Damage = 340, SurvivalMs = 245_900,
        };
        Assert.Equal("10-01 21:00   3위 / 16명   처치 2   피해 340   생존 4:05", UiText.StatsRowText(row, TimeSpan.FromHours(9)));
        row.Placement = 0;
        Assert.StartsWith("10-01 12:00   순위 없음", UiText.StatsRowText(row, TimeSpan.Zero));

        Assert.Equal(string.Empty, UiText.StatsRowsText(Array.Empty<StatsRow>(), TimeSpan.Zero));
        string two = UiText.StatsRowsText(new[] { row, row }, TimeSpan.Zero);
        Assert.Equal(2, two.Split('\n').Length);
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("  bob \t", "bob")]
    [InlineData("플레이어", "플레이어")]
    [InlineData("abcdefghijabcdefghijabcdefghij12", "abcdefghijabcdefghijabcdefghij12")]   // 32 bytes
    [InlineData("가나다라마바사아자차", "가나다라마바사아자차")]                                  // 10 x 3 = 30 bytes
    public void Name_IsTrimmed_AndAccepted(string input, string expected)
    {
        Assert.True(UiText.TryNormalizeName(input, out string name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData(null)]
    [InlineData("abcdefghijabcdefghijabcdefghij123")]   // 33 bytes
    [InlineData("가나다라마바사아자차카")]                 // 11 x 3 = 33 bytes
    public void Name_EmptyOrLongerThan32Bytes_IsRefused(string? input)
    {
        Assert.False(UiText.TryNormalizeName(input!, out string name));
        Assert.Null(name);
    }

    [Theory]
    [InlineData("7777", true, 7777)]
    [InlineData(" 1 ", true, 1)]
    [InlineData("65535", true, 65535)]
    [InlineData("0", false, 0)]
    [InlineData("65536", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("77a", false, 0)]
    [InlineData("", false, 0)]
    public void Port_Rule(string input, bool ok, int expected)
    {
        Assert.Equal(ok, UiText.TryParsePort(input, out int port));
        Assert.Equal(expected, port);
    }

    [Fact]
    public void Host_IsTrimmed_AndRequired()
    {
        Assert.True(UiText.TryNormalizeHost(" 127.0.0.1 ", out string host));
        Assert.Equal("127.0.0.1", host);
        Assert.False(UiText.TryNormalizeHost("  ", out _));
    }

    [Fact]
    public void DebugLine()
    {
        Assert.Equal("상태 Joined   RTT 23 ms   Entity 5   (F1)", UiText.DebugLine("Joined", 23, 5));
    }
}
```

**새 파일** `Server/tests/ProjectH.Server.Tests/ClientUi/KillFeedModelTests.cs`:

```csharp
using ProjectH.Client.UI;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 11 D10 (spec §2): the kill feed keeps the newest 5 lines, each for 6 seconds.
public class KillFeedModelTests
{
    [Fact]
    public void TheSixthLine_PushesTheOldestOut()
    {
        var feed = new KillFeedModel();
        for (int i = 1; i <= 5; i++) feed.Add("line " + i, i);
        Assert.Equal(5, feed.Count);
        Assert.Equal("line 5", feed.Line(0));   // newest first
        Assert.Equal("line 1", feed.Line(4));

        feed.Add("line 6", 6f);
        Assert.Equal(KillFeedModel.Capacity, feed.Count);
        Assert.Equal("line 6", feed.Line(0));
        Assert.Equal("line 2", feed.Line(4));
    }

    [Fact]
    public void ALine_GoesAfterSixSeconds()
    {
        var feed = new KillFeedModel();
        feed.Add("a", 10f);
        feed.Add("b", 12f);
        int version = feed.Version;

        Assert.False(feed.Expire(15.9f));
        Assert.Equal(version, feed.Version);
        Assert.True(feed.Expire(16f));
        Assert.Equal(1, feed.Count);
        Assert.Equal("b", feed.Line(0));
        Assert.NotEqual(version, feed.Version);

        Assert.True(feed.Expire(18f));
        Assert.Equal(0, feed.Count);
        Assert.False(feed.Expire(100f));
    }

    [Fact]
    public void TheRing_WrapsAroundForLong()
    {
        var feed = new KillFeedModel();
        for (int i = 0; i < 23; i++)
        {
            feed.Add("k" + i, i);
            feed.Expire(i);
        }
        // At t = 22 the lines from t = 17..22 are younger than 6 s, but only 5 fit.
        Assert.Equal(5, feed.Count);
        Assert.Equal("k22", feed.Line(0));
        Assert.Equal("k18", feed.Line(4));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => feed.Line(5));
    }

    [Fact]
    public void Clear_EmptiesIt()
    {
        var feed = new KillFeedModel();
        feed.Clear();
        Assert.Equal(0, feed.Version);
        feed.Add("a", 0f);
        feed.Clear();
        Assert.Equal(0, feed.Count);
        Assert.Equal(2, feed.Version);
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t3_tests.py`로 저장하고 `python <경로>/p11_t3_tests.py E:/popol/ProjectH`로 실행한다. `ProjectH.Server.Tests.csproj`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj', [
(r"""    <Compile Include="..\..\..\Client\Assets\Scripts\Game\ZoneMath.cs" Link="ClientCopies\ZoneMath.cs" />
""",
 r"""    <Compile Include="..\..\..\Client\Assets\Scripts\Game\ZoneMath.cs" Link="ClientCopies\ZoneMath.cs" />
    <!-- Phase 11 D3, D10, D11: the client's screen flow, UI strings and kill feed ring, tested here the same way (pure C#,
         no UnityEngine). UiText uses LiteNetLib's DisconnectReason, which this project gets through the server. -->
    <Compile Include="..\..\..\Client\Assets\Scripts\UI\UiFlow.cs" Link="ClientCopies\UI\UiFlow.cs" />
    <Compile Include="..\..\..\Client\Assets\Scripts\UI\UiText.cs" Link="ClientCopies\UI\UiText.cs" />
    <Compile Include="..\..\..\Client\Assets\Scripts\UI\KillFeedModel.cs" Link="ClientCopies\UI\KillFeedModel.cs" />
"""),
])
print('p11_t3_tests ok')
```

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx`
Expected: 빌드 실패. 링크한 세 파일이 없다(CS2001). 계획 단계에서 오류는 이 한 종류였다.

- [ ] **Step 4: 순수 코드를 구현한다**

**새 파일** `Client/Assets/Scripts/UI/UiFlow.cs`:

```csharp
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.UI
{
    // Phase 11 D3: the screens. Stats is not a screen: it opens over Menu or Result (UiFlow.StatsOpen).
    public enum UiScreen : byte
    {
        Title,
        Connecting,
        InGame,
        Menu,
        Disconnected,
        Result,
    }

    // What the flow needs to know about the connection: NetClient's Disconnected, Connecting/Connected and Joined.
    public enum UiConnection : byte
    {
        Offline,
        Connecting,
        Joined,
    }

    // Phase 11 D3: which screen shows, whether the cursor may be locked and whether game input is blocked. Pure (no
    // UnityEngine) so every transition is tested by the server test project (source link, like ZoneMath). UiRoot calls
    // Update once per frame with what GameClient reports, and the command methods from buttons and Esc; the screens only
    // draw the result. Version changes whenever Screen, StatsOpen or Reconnecting changes, so UiRoot redraws only then.
    //
    //   Title --connect--> Connecting --joined--> InGame <--Esc/continue--> Menu
    //   Connecting --failed (refused, full, unreachable)--> Disconnected --retry--> Connecting
    //   InGame/Menu/Result --connection lost--> Disconnected --automatic reconnect joined--> InGame
    //   InGame/Menu --new MatchResult--> Result --continue, Esc, or the next round's WaitingForPlayers/Starting--> InGame
    //   Menu "disconnect", Disconnected "to title", Connecting "cancel" --> Title
    public sealed class UiFlow
    {
        private int _seenResults;

        public UiScreen Screen { get; private set; } = UiScreen.Title;
        public bool StatsOpen { get; private set; }
        // On the Disconnected screen: an automatic reconnect is running (show its progress and Cancel, not Retry).
        public bool Reconnecting { get; private set; }
        public int Version { get; private set; }

        // D5: a click may lock the cursor only in the game with no screen up.
        public bool AllowCursorLock => Screen == UiScreen.InGame;
        // D5: movement, fire, aim and look are zero while any screen is up (GameClient also blocks them while the cursor is
        // free). Inputs keep going to the server, empty, so the Phase 10 input timeout never closes a player in a menu.
        public bool BlocksGameInput => Screen != UiScreen.InGame;

        // The player asked to connect from the title or the disconnected screen (UiRoot already called Connect).
        public void ConnectRequested()
        {
            if (Screen == UiScreen.Title || Screen == UiScreen.Disconnected) Set(UiScreen.Connecting);
        }

        // The player left on purpose (UiRoot already disconnected): Menu "disconnect", Disconnected "to title",
        // Connecting "cancel". Nothing that happens to the connection afterwards moves the title.
        public void LeaveRequested()
        {
            Set(UiScreen.Title);
        }

        // Esc goes back one level: the stats window, then the menu or the result. In the game it opens the menu.
        public void EscapePressed()
        {
            if (StatsOpen)
            {
                CloseStats();
                return;
            }
            switch (Screen)
            {
                case UiScreen.InGame: Set(UiScreen.Menu); break;
                case UiScreen.Menu:
                case UiScreen.Result: Set(UiScreen.InGame); break;
            }
        }

        // Menu "continue", Result "keep spectating".
        public void ContinuePressed()
        {
            if (Screen == UiScreen.Menu || Screen == UiScreen.Result) Set(UiScreen.InGame);
        }

        public void OpenStats()
        {
            if (StatsOpen || (Screen != UiScreen.Menu && Screen != UiScreen.Result)) return;
            StatsOpen = true;
            Version++;
        }

        public void CloseStats()
        {
            if (!StatsOpen) return;
            StatsOpen = false;
            Version++;
        }

        // Once per frame. reconnecting: GameClient's automatic reconnect is running. results: how many MatchResults
        // arrived so far (a new one opens the result screen once). hasMatch / matchState: the newest MatchState (a dev
        // server sends none); the result screen closes by itself when the next round begins.
        public void Update(UiConnection connection, bool reconnecting, int results, bool hasMatch, MatchFlowState matchState)
        {
            if (reconnecting != Reconnecting)
            {
                Reconnecting = reconnecting;
                Version++;
            }
            bool newResult = results != _seenResults;
            _seenResults = results;

            switch (Screen)
            {
                case UiScreen.Title:
                    break;
                case UiScreen.Connecting:
                    if (connection == UiConnection.Joined) Set(UiScreen.InGame);
                    else if (connection == UiConnection.Offline) Set(UiScreen.Disconnected);
                    break;
                case UiScreen.Disconnected:
                    if (connection == UiConnection.Joined) Set(UiScreen.InGame);
                    break;
                default:   // InGame, Menu, Result
                    if (connection != UiConnection.Joined) Set(UiScreen.Disconnected);
                    else if (newResult && Screen != UiScreen.Result) Set(UiScreen.Result);
                    else if (Screen == UiScreen.Result && hasMatch &&
                             (matchState == MatchFlowState.WaitingForPlayers || matchState == MatchFlowState.Starting))
                        Set(UiScreen.InGame);
                    break;
            }
        }

        private void Set(UiScreen screen)
        {
            if (screen == Screen && !StatsOpen) return;
            Screen = screen;
            StatsOpen = false;
            Version++;
        }
    }

    public enum StatsWaitState : byte
    {
        Waiting,    // "loading"
        Answered,   // show the newest StatsResponse
        NoAnswer,   // nothing within AnswerSeconds (or nothing could be sent)
    }

    // Phase 11 D8: the stats window's wait for its answer. Times are the caller's clock in seconds; negative = never.
    public static class StatsWait
    {
        // D8: the window gives up after this long.
        public const float AnswerSeconds = 5f;
        // The server answers one request per 2 s per connection and drops the rest without an answer. A window opened
        // again sooner reuses the previous request (its answer is here or on the way) instead of sending one that would
        // be dropped; the extra half second covers network jitter between the two clocks.
        public const float ResendSeconds = 2.5f;

        public static bool MaySend(float now, float lastSentAt) => lastSentAt < 0f || now - lastSentAt >= ResendSeconds;

        public static StatsWaitState Of(float now, float sentAt, float answeredAt)
        {
            if (sentAt < 0f) return StatsWaitState.NoAnswer;
            if (answeredAt >= sentAt) return StatsWaitState.Answered;
            return now - sentAt >= AnswerSeconds ? StatsWaitState.NoAnswer : StatsWaitState.Waiting;
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/UiText.cs`:

```csharp
#nullable disable
// (Nullable is off for the Unity client; the server test project compiles this file with nullable on.)
using System;
using System.Globalization;
using System.Text;
using LiteNetLib;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.UI
{
    // Phase 11 D6: what ended the last connection, as GameClient reports it.
    public struct DisconnectSummary
    {
        public bool StartFailed;          // Connect failed before a connection existed (no socket, bad address)
        public DisconnectReason Reason;   // LiteNetLib's reason
        public DisconnectCode Code;       // the server's code, with RemoteConnectionClose
        public RejectReason Reject;       // the server's reason, with ConnectionRejected
        public JoinResult Join;           // the server's answer to the last join (MatchFull: it closes a second later)
    }

    // Phase 11 D11: every UI string that depends on a value, in Korean. Pure functions (no UnityEngine), tested by the
    // server test project through a source link. The reason texts are constants, so asking every frame allocates nothing;
    // the others build a string, so callers call them only when a shown value changed (a whole second, a new answer).
    public static class UiText
    {
        public const string ZoneName = "자기장";
        public const string Connecting = "접속하는 중...";
        public const string StatsLoading = "불러오는 중...";
        public const string StatsNoAnswer = "응답 없음";
        public const string NameRule = "이름은 1-32바이트여야 합니다 (한글은 한 글자에 3바이트).";
        public const string PortRule = "포트는 1-65535 사이의 숫자여야 합니다.";
        public const string HostRule = "주소를 입력하세요.";

        // ---- Disconnects (D6) ----

        public static string Disconnect(in DisconnectSummary s)
        {
            if (s.StartFailed) return "접속을 시작할 수 없습니다. 주소와 포트를 확인하세요.";
            if (s.Join == JoinResult.MatchFull) return "경기가 가득 찼습니다.";
            switch (s.Reason)
            {
                case DisconnectReason.ConnectionRejected: return Reject(s.Reject);
                case DisconnectReason.RemoteConnectionClose: return Code(s.Code);
                default: return Reason(s.Reason);
            }
        }

        public static string Reject(RejectReason reason)
        {
            switch (reason)
            {
                case RejectReason.VersionMismatch: return "서버와 게임 버전이 다릅니다.";
                case RejectReason.ServerFull: return "서버가 가득 찼습니다.";
                case RejectReason.BadRequest: return "서버가 접속 요청을 받지 않았습니다.";
                default: return "서버가 접속을 거절했습니다.";
            }
        }

        public static string Code(DisconnectCode code)
        {
            switch (code)
            {
                case DisconnectCode.ServerShutdown: return "서버가 종료되었습니다.";
                case DisconnectCode.Kicked: return "잘못된 패킷이 많아 연결이 끊겼습니다.";
                case DisconnectCode.JoinTimeout: return "경기 참가가 늦어 연결이 끊겼습니다.";
                case DisconnectCode.InputTimeout: return "입력이 오래 없어 연결이 끊겼습니다.";
                case DisconnectCode.ServerError: return "서버 오류로 경기가 초기화되었습니다.";
                default: return "서버가 연결을 끊었습니다.";
            }
        }

        public static string Reason(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.ConnectionFailed: return "서버에 연결할 수 없습니다.";
                case DisconnectReason.Timeout: return "서버의 응답이 끊겼습니다.";
                case DisconnectReason.HostUnreachable: return "서버에 닿을 수 없습니다.";
                case DisconnectReason.NetworkUnreachable: return "네트워크에 연결되어 있지 않습니다.";
                case DisconnectReason.RemoteConnectionClose: return "서버가 연결을 끊었습니다.";
                case DisconnectReason.DisconnectPeerCalled: return "연결을 끊었습니다.";
                case DisconnectReason.ConnectionRejected: return "서버가 접속을 거절했습니다.";
                case DisconnectReason.InvalidProtocol: return "서버와 통신 형식이 맞지 않습니다.";
                case DisconnectReason.UnknownHost: return "서버 주소를 찾을 수 없습니다.";
                case DisconnectReason.Reconnect: return "같은 주소에서 다시 연결되어 끊겼습니다.";
                case DisconnectReason.PeerToPeerConnection: return "다른 연결로 바뀌어 끊겼습니다.";
                case DisconnectReason.PeerNotFound: return "서버가 이 연결을 찾지 못했습니다.";
                default: return "연결이 끊겼습니다.";
            }
        }

        // "재접속 중 (1/3) - 2초 뒤 다시 시도", or "... - 연결하는 중" while an attempt runs (secondsLeft <= 0).
        public static string Reconnecting(int attempt, int maxAttempts, int secondsLeft)
        {
            string head = "재접속 중 (" + Int(attempt) + "/" + Int(maxAttempts) + ") - ";
            return secondsLeft > 0 ? head + Int(secondsLeft) + "초 뒤 다시 시도" : head + "연결하는 중";
        }

        // ---- Result (D7) ----

        public static string ResultTitle(bool won) => won ? "승리!" : "탈락";

        public static string Placement(int placement, int participants) => "순위 " + Int(placement) + " / " + Int(participants) + "명";

        public static string Kills(int kills) => "처치 " + Int(kills);

        // winnerName null = no winner among the connected players (WinnerId 0).
        public static string Winner(string winnerName) => winnerName == null ? "승자 없음" : "승자: " + winnerName;

        // Who ended this player's match: a player, the zone, or nobody yet (won, or still alive).
        public static string KilledBy(bool died, bool byZone, string killerName)
        {
            if (!died) return string.Empty;
            if (byZone) return "탈락 원인: " + ZoneName;
            return "나를 처치한 플레이어: " + killerName;
        }

        public static string NextRound(int secondsLeft) =>
            secondsLeft > 0 ? "다음 판까지 " + Int(secondsLeft) + "초" : "다음 판을 준비하는 중";

        // ---- Names (D9, D10) ----

        // A name from PlayerSpawned, or "플레이어 3" when that player's spawn is not known (it already left).
        public static string NameOr(string name, ushort entityId) => name ?? "플레이어 " + Int(entityId);

        // "가해자 ▸ 피해자"; killer null = the zone (KillerId 0).
        public static string KillLine(string killer, string victim) => (killer ?? ZoneName) + " ▸ " + victim;

        // ---- Statistics (D8) ----

        // Ok has no status line; the summary and the rows say it all.
        public static string StatsStatusText(StatsStatus status)
        {
            switch (status)
            {
                case StatsStatus.Ok: return string.Empty;
                case StatsStatus.NoRecord: return "아직 기록이 없습니다.";
                case StatsStatus.Unavailable: return "기록을 볼 수 없음";
                case StatsStatus.Busy: return "서버가 바쁩니다. 잠시 뒤 다시 열어 주세요.";
                default: return StatsNoAnswer;
            }
        }

        public static string StatsSummaryText(in StatsSummary s) =>
            "경기 " + Int(s.Matches) + "   승리 " + Int(s.Wins) + "   처치 " + Int(s.Kills) + "   사망 " + Int(s.Deaths) +
            "\n피해 " + Int(s.Damage) + "   생존 시간 " + Duration(s.SurvivalSeconds);

        // One line per match, newest first: "10-01 21:00   3위 / 16명   처치 2   피해 340   생존 4:05".
        // utcOffset: the viewer's time zone (UiRoot passes the local one).
        public static string StatsRowsText(StatsRow[] rows, TimeSpan utcOffset)
        {
            if (rows == null || rows.Length == 0) return string.Empty;
            var text = new StringBuilder(rows.Length * 64);
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) text.Append('\n');
                text.Append(StatsRowText(rows[i], utcOffset));
            }
            return text.ToString();
        }

        public static string StatsRowText(in StatsRow row, TimeSpan utcOffset)
        {
            string when = DateTimeOffset.FromUnixTimeSeconds(row.EndedUnixSeconds).ToOffset(utcOffset)
                .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            string place = row.Placement == 0 ? "순위 없음" : Int(row.Placement) + "위 / " + Int(row.Players) + "명";
            return when + "   " + place + "   처치 " + Int(row.Kills) + "   피해 " + Int(row.Damage) +
                   "   생존 " + Duration(row.SurvivalMs / 1000);
        }

        // "4:05" below an hour, "1:02:03" from an hour on.
        public static string Duration(long totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            long hours = totalSeconds / 3600;
            long minutes = totalSeconds / 60 % 60;
            long seconds = totalSeconds % 60;
            return hours > 0
                ? Int(hours) + ":" + Two(minutes) + ":" + Two(seconds)
                : Int(minutes) + ":" + Two(seconds);
        }

        // ---- Debug line (D4, F1) ----

        public static string DebugLine(string state, int roundTripMs, ushort entityId) =>
            "상태 " + state + "   RTT " + Int(roundTripMs) + " ms   Entity " + Int(entityId) + "   (F1)";

        // ---- Title input (D4) ----

        // The name typed on the title screen: trimmed, then 1-32 bytes of UTF-8, the same rule as the server's connect
        // request (ProtocolConstants.MaxDevPlayerIdBytes).
        public static bool TryNormalizeName(string input, out string name)
        {
            name = input == null ? string.Empty : input.Trim();
            int bytes = Encoding.UTF8.GetByteCount(name);
            if (bytes >= 1 && bytes <= ProtocolConstants.MaxDevPlayerIdBytes) return true;
            name = null;
            return false;
        }

        public static bool TryNormalizeHost(string input, out string host)
        {
            host = input == null ? string.Empty : input.Trim();
            if (host.Length > 0 && host.Length <= 253) return true;
            host = null;
            return false;
        }

        public static bool TryParsePort(string input, out int port)
        {
            if (int.TryParse(input == null ? null : input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                port >= 1 && port <= 65535)
                return true;
            port = 0;
            return false;
        }

        private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Two(long value) => value < 10 ? "0" + Int(value) : Int(value);
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/KillFeedModel.cs`:

```csharp
#nullable disable
// (Nullable is off for the Unity client; the server test project compiles this file with nullable on.)
using System;

namespace ProjectH.Client.UI
{
    // Phase 11 D10: the kill feed's newest lines in a fixed ring of Capacity. Each line shows for LineSeconds; a sixth
    // line pushes the oldest out. Pure (no UnityEngine): KillFeed redraws when Version changed. Times are the caller's
    // clock in seconds. Lines are built once per death (UiText.KillLine), never per frame.
    public sealed class KillFeedModel
    {
        public const int Capacity = 5;
        public const float LineSeconds = 6f;

        private readonly string[] _lines = new string[Capacity];
        private readonly float[] _expires = new float[Capacity];
        private int _oldest;
        private int _count;

        public int Count => _count;
        public int Version { get; private set; }

        // i = 0 is the newest line, Count - 1 the oldest.
        public string Line(int i)
        {
            if (i < 0 || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
            return _lines[(_oldest + _count - 1 - i) % Capacity];
        }

        public void Add(string line, float now)
        {
            if (_count == Capacity)
            {
                _lines[_oldest] = null;
                _oldest = (_oldest + 1) % Capacity;
                _count--;
            }
            int slot = (_oldest + _count) % Capacity;
            _lines[slot] = line;
            _expires[slot] = now + LineSeconds;
            _count++;
            Version++;
        }

        // Lines expire in the order they came, so only the oldest ones are checked. True when a line went.
        public bool Expire(float now)
        {
            bool changed = false;
            while (_count > 0 && now >= _expires[_oldest])
            {
                _lines[_oldest] = null;
                _oldest = (_oldest + 1) % Capacity;
                _count--;
                changed = true;
            }
            if (changed) Version++;
            return changed;
        }

        public void Clear()
        {
            if (_count == 0) return;
            Array.Clear(_lines, 0, Capacity);
            _oldest = 0;
            _count = 0;
            Version++;
        }
    }
}
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~ClientUi"`
Expected: 모두 통과(58개).

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 843개다(834 통과, MySQL 9개 건너뜀). Task 2보다 58개 많다. 계획 단계에서 같은 결과였다.

Run: `dotnet build <스크래치>/uc/UnityCompile.csproj` → 경고 0, 오류 0(새 파일이 Unity의 C# 9와 LiteNetLib로 컴파일된다)

- [ ] **Step 6: Commit** — `feat(client): add the UI flow, Korean UI texts and the kill feed ring (pure, tested through source links)`

---

### Task 4: Client 기반 (`GameClient`·`NetClient` 공개, 입력 막기, 글꼴, Kill Feed, HUD 한글)

**Files:**
- Create: `Client/Assets/Scripts/UI/UiFont.cs`, `UI/UiFactory.cs`, `UI/KillFeed.cs`
- Modify: `Client/Assets/Scripts/Net/NetClient.cs`, `Input/InputReader.cs`, `Game/GameClient.cs`, `Game/MatchHud.cs`, `Game/MatchHudText.cs`(전체 교체), `Game/CombatHud.cs`, `Game/InventoryHud.cs`, `Game/InventoryHudText.cs`, `Game/PoiLabel.cs`, `Bootstrap/DevConnectPanel.cs`(Task 5에서 지울 때까지 Esc로 커서 풀기)
- Test:
  - Modify: `Client/Assets/Tests/EditMode/MatchHudTextTests.cs`(전체 교체), `Client/Assets/Tests/EditMode/InventoryHudTextTests.cs`

**Interfaces:**
- Consumes: Task 1의 `StatsRequest`·`StatsResponse`·`PlayerSpawned.Name`, Task 3의 `UiText`, `StatsWait`, `KillFeedModel`, `DisconnectSummary`
- Produces:
  - `NetClient`: `event Action<StatsResponse> StatsReceived`, `bool RequestStats()`, `LastDisconnectReason`, `LastRejectReason`, `LastJoinResult`, `LastConnectStartFailed`
  - `InputReader`: `EscapePressed`(`UnlockCursorPressed` 대신), `DebugTogglePressed`(F1)
  - `GameClient`(읽기 전용): `LastDisconnect`(`DisconnectSummary`), `NextReconnectIn`, `NameOf(ushort)`, `HasMatch`, `Match`, `HasResult`, `Result`, `ResultCount`, `DiedThisRound`, `KilledByZone`, `KillerName`, `StateSecondsLeft`, `EscapePressed`, `DebugTogglePressed`, `LastStats`, `StatsSentAt`, `StatsAnsweredAt`
  - `GameClient`(명령): `SetUiControl(bool allowCursorLock, bool blockInput)`, `float RequestStats()`, `StopReconnecting()`
  - `UiFont.Get()`, `UiFont.Release()`
  - `UiFactory`: 색 상수, `EnsureEventSystem()`, `CreateCanvas(name, sortingOrder, interactive)`, `CreateRect`, `CreateScreen(name, parent, dim)`, `CreatePanel`, `CreateText`, `CreateButton`, `CreateInputField`
  - `KillFeed`: `Add(line, now)`, `Clear()`, `Tick(now)`, `Dispose()`
  - `MatchHudText.SetSpectating(ushort, string)`, `MatchHud.SetSpectating(ushort, string)`. 결과 줄(`SetResult`, `Result`)은 없어진다.

`GameClient`는 이 Task부터 Esc로 커서를 풀지 않는다. Task 5의 `UiRoot`가 오기 전까지는 `DevConnectPanel`이 Esc로 커서를 푼다(이 Task의 한 줄). `SetUiControl`의 기본값은 "잠글 수 있음, 막지 않음"이라 `UiRoot` 없이도 예전처럼 클릭으로 잠그고 움직인다. 다만 커서가 풀려 있으면 입력을 막는다(D5, Spec 해석 13).

Unity 코드는 이 저장소의 테스트로 돌릴 수 없다. 순수 규칙은 Task 3의 서버 테스트가, 문구는 `EditTests`가, 나머지는 `UnityCompile`과 Editor 확인("Phase 완료 확인" 5)이 본다.

- [ ] **Step 1: EditMode 테스트를 고친다**

HUD 문구가 한국어가 되고, 관전 표시가 이름을 받고, 결과 줄이 없어진다.

**전체 교체** `Client/Assets/Tests/EditMode/MatchHudTextTests.cs`:

```csharp
using NUnit.Framework;
using ProjectH.Client.Game;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Tests
{
    // D14 / Client Hot Path: MatchHud calls every Set* each frame; a string is built only when a shown value changed.
    // Phase 11: Korean texts, the spectated player's name, and no result line (the result screen shows it).
    public class MatchHudTextTests
    {
        [Test]
        public void Status_Texts()
        {
            var text = new MatchHudText();
            text.SetStatus(MatchFlowState.WaitingForPlayers, 0, 1, 1, 2);
            Assert.AreEqual("플레이어를 기다리는 중 1/2", text.Status);
            text.SetStatus(MatchFlowState.Starting, 7, 2, 2, 2);
            Assert.AreEqual("시작까지 7초", text.Status);
            text.SetStatus(MatchFlowState.Playing, 0, 3, 5, 2);
            Assert.AreEqual("생존 3/5", text.Status);
            text.SetStatus(MatchFlowState.FinalPhase, 0, 2, 5, 2);
            Assert.AreEqual("생존 2/5", text.Status);
            text.SetStatus(MatchFlowState.Finished, 0, 1, 5, 2);
            Assert.AreEqual("경기 종료", text.Status);
        }

        [Test]
        public void Status_BuildsOnlyOnChange()
        {
            var text = new MatchHudText();
            Assert.IsTrue(text.SetStatus(MatchFlowState.Playing, 0, 3, 5, 2));
            string built = text.Status;
            int rebuilds = text.Rebuilds;
            // Values the current state does not show (the countdown while playing) change nothing.
            for (int frame = 0; frame < 100; frame++) Assert.IsFalse(text.SetStatus(MatchFlowState.Playing, frame, 3, 5, 2));
            Assert.AreEqual(rebuilds, text.Rebuilds);
            Assert.AreSame(built, text.Status);
            Assert.IsTrue(text.SetStatus(MatchFlowState.Playing, 0, 2, 5, 2));   // someone died
        }

        [Test]
        public void Countdown_RebuildsOncePerSecond()
        {
            var text = new MatchHudText();
            int before = text.Rebuilds;
            for (int frame = 0; frame < 600; frame++)
                text.SetStatus(MatchFlowState.Starting, 10 - frame / 60, 2, 2, 2);   // 60 frames per second
            Assert.AreEqual(before + 10, text.Rebuilds);
        }

        [Test]
        public void Zone_Texts_AndBuildsOnlyOnChange()
        {
            var text = new MatchHudText();
            Assert.IsTrue(text.SetZone(ZoneHint.ShrinksIn, 12));
            Assert.AreEqual("자기장 축소까지 12초", text.Zone);
            Assert.IsFalse(text.SetZone(ZoneHint.ShrinksIn, 12));
            Assert.IsTrue(text.SetZone(ZoneHint.Closing, 0));
            Assert.AreEqual("자기장 축소 중", text.Zone);
            Assert.IsFalse(text.SetZone(ZoneHint.Closing, 5));   // seconds do not matter while closing
            Assert.IsTrue(text.SetZone(ZoneHint.None, 0));
            Assert.AreEqual(string.Empty, text.Zone);
        }

        [Test]
        public void Spectating_Texts_AndBuildsOnlyOnChange()
        {
            var text = new MatchHudText();
            const string alice = "alice";
            Assert.IsFalse(text.SetSpectating(0, null));
            Assert.IsTrue(text.SetSpectating(3, alice));
            Assert.AreEqual("관전 중: alice", text.Spectating);
            Assert.IsFalse(text.SetSpectating(3, alice));
            Assert.IsTrue(text.SetSpectating(4, null));
            Assert.AreEqual("관전 중: 플레이어 4", text.Spectating);
            Assert.IsTrue(text.SetSpectating(0, null));
            Assert.AreEqual(string.Empty, text.Spectating);
        }
    }
}
```

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t4_tests.py`로 저장하고 `python <경로>/p11_t4_tests.py E:/popol/ProjectH`로 실행한다. `InventoryHudTextTests.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Client/Assets/Tests/EditMode/InventoryHudTextTests.cs', [
(r"""            Assert.AreEqual("[4] Medkit x2    [5] Shield Cell x3", text.Consumables);
""",
 r"""            Assert.AreEqual("[4] 구급상자 x2    [5] 실드 셀 x3", text.Consumables);
"""),
(r"""            Assert.AreEqual("[E] Pick up Vesper AR [Rare]", text.Prompt);
""",
 r"""            Assert.AreEqual("[E] 줍기: Vesper AR [Rare]", text.Prompt);
"""),
(r"""            Assert.AreEqual("[E] Pick up Light Rounds x60", text.Prompt);
            Assert.IsTrue(text.SetPrompt(9, 12, "Light Rounds", null));   // partial pickup left 12
            Assert.AreEqual("[E] Pick up Light Rounds x12", text.Prompt);
""",
 r"""            Assert.AreEqual("[E] 줍기: Light Rounds x60", text.Prompt);
            Assert.IsTrue(text.SetPrompt(9, 12, "Light Rounds", null));   // partial pickup left 12
            Assert.AreEqual("[E] 줍기: Light Rounds x12", text.Prompt);
"""),
])
print('p11_t4_tests ok')
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet build <스크래치>/uc/UnityCompile.csproj`
Expected: 빌드 실패. `MatchHudText.SetSpectating`이 인자 2개를 받지 않는다(CS1501). 계획 단계에서 오류는 이 한 종류였다.

- [ ] **Step 3: Client 기반을 구현한다**

**새 파일** `Client/Assets/Scripts/UI/UiFont.cs`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectH.Client.UI
{
    // Phase 11 D2: the one font of every UI text, HUDs included. Made once at run time from an OS font that has Hangul
    // (no font asset in the repository: license and size). The installed candidates are passed together, so the first
    // one renders and the rest are fallbacks for glyphs it lacks. With none installed it falls back to Unity's built-in
    // LegacyRuntime.ttf, where Korean shows as boxes; the log line says which font was chosen.
    // Owned by GameClient: it calls Release in OnDestroy, after its HUDs are gone.
    public static class UiFont
    {
        private static readonly string[] Candidates =
        {
            "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Noto Sans KR", "NanumGothic",
        };

        // The dynamic font's base size; every Text sets its own fontSize.
        private const int BaseSize = 16;

        private static Font s_font;
        private static bool s_ownsFont;

        public static Font Get()
        {
            // Unity null: a released font is made again.
            if (s_font != null) return s_font;

            string[] installed = Font.GetOSInstalledFontNames();
            var found = new List<string>(Candidates.Length);
            foreach (string candidate in Candidates)
            {
                foreach (string name in installed)
                {
                    if (!string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(candidate);
                    break;
                }
            }

            if (found.Count > 0)
            {
                s_font = Font.CreateDynamicFontFromOSFont(found.ToArray(), BaseSize);
                s_ownsFont = true;
                Debug.Log("UI font: " + string.Join(", ", found) + " (OS font)");
            }
            else
            {
                s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                s_ownsFont = false;
                Debug.LogWarning("UI font: no Hangul OS font found (" + string.Join(", ", Candidates) +
                                 "); using the built-in LegacyRuntime.ttf, so Korean text may show as boxes.");
            }
            return s_font;
        }

        // Destroys the font Get made from the OS (the built-in one belongs to Unity).
        public static void Release()
        {
            if (s_ownsFont && s_font != null) Object.Destroy(s_font);
            s_font = null;
            s_ownsFont = false;
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/UiFactory.cs`:

```csharp
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D1: builds the code-made UGUI pieces of the new UI (no scene, prefab or asset). Every object is a child of
    // a canvas its caller owns, so destroying that canvas destroys the pieces and their button listeners with it. Only
    // what the player clicks or types into is a raycast target.
    public static class UiFactory
    {
        public static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.11f, 0.94f);
        public static readonly Color DimColor = new Color(0f, 0f, 0f, 0.55f);
        public static readonly Color ButtonColor = new Color(0.24f, 0.27f, 0.33f, 1f);
        public static readonly Color FieldColor = new Color(0.15f, 0.16f, 0.19f, 1f);
        public static readonly Color TextColor = new Color(0.95f, 0.95f, 0.95f, 1f);
        public static readonly Color ErrorColor = new Color(1f, 0.55f, 0.45f, 1f);
        public static readonly Color AccentColor = new Color(1f, 0.85f, 0.3f, 1f);

        // D1: the EventSystem the buttons and fields need, with the Input System's UI module (the project runs the Input
        // System only). The module assigns its default UI actions in OnEnable when it has none and releases them in
        // OnDisable (Input System 1.20). Returns the object it made, or null when one already exists (then it is not the
        // caller's to destroy).
        public static GameObject EnsureEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null) return null;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            Object.DontDestroyOnLoad(go);
            return go;
        }

        // D1: a Screen Space Overlay canvas scaled from 1920 x 1080 (width and height weighted equally). interactive adds
        // the GraphicRaycaster: only screens with buttons or fields have one.
        public static GameObject CreateCanvas(string name, int sortingOrder, bool interactive)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            if (interactive) go.AddComponent<GraphicRaycaster>();
            return go;
        }

        // A child rectangle: anchor is also its pivot; position is from that anchor.
        public static RectTransform CreateRect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        // A full-screen dim layer. It is a raycast target, so a click beside a panel does not reach anything under it.
        public static RectTransform CreateScreen(string name, Transform parent, bool dim)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            if (dim)
            {
                var image = go.AddComponent<Image>();   // no sprite: a solid rectangle
                image.color = DimColor;
            }
            return rect;
        }

        public static RectTransform CreatePanel(string name, Transform parent, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = PanelColor;
            image.raycastTarget = false;
            return rect;
        }

        public static Text CreateText(string name, Transform parent, string text, int fontSize, TextAnchor alignment,
            Vector2 anchor, Vector2 position, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent, anchor, position, size);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = UiFont.Get();
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = TextColor;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.text = text;
            return label;
        }

        // A button centred at position in its parent. onClick runs on the main thread from the EventSystem's update.
        public static Button CreateButton(string name, Transform parent, string label, Vector2 position, Vector2 size, UnityAction onClick)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), position, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = ButtonColor;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            Text text = CreateText("Label", rect, label, 24, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return button;
        }

        // The legacy UGUI InputField (D1: no TextMeshPro), laid out like DefaultControls.CreateInputField.
        public static InputField CreateInputField(string name, Transform parent, Vector2 position, Vector2 size, int characterLimit,
            InputField.ContentType contentType)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), position, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = FieldColor;
            var field = rect.gameObject.AddComponent<InputField>();
            field.targetGraphic = image;

            Text text = CreateFieldText("Text", rect, 24);
            text.supportRichText = false;   // InputField requires it
            Text placeholder = CreateFieldText("Placeholder", rect, 24);
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);

            field.textComponent = text;
            field.placeholder = placeholder;
            field.characterLimit = characterLimit;
            field.contentType = contentType;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        private static Text CreateFieldText(string name, RectTransform parent, int fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(12f, 6f);
            rect.offsetMax = new Vector2(-12f, -6f);
            var text = go.AddComponent<Text>();
            text.font = UiFont.Get();
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = TextColor;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.text = string.Empty;
            return text;
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/KillFeed.cs`:

```csharp
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D10: the kill feed, top right: the newest KillFeedModel.Capacity lines, newest on top, each for 6 s. Its
    // own canvas (no GraphicRaycaster, nothing is a raycast target), so a new or expiring line rebuilds only this canvas,
    // never the HUDs or the screens (ClientPerf 7). Texts are set only when the model's Version changed: an unchanged feed
    // allocates nothing per frame. Owned by GameClient; Dispose destroys the canvas.
    public sealed class KillFeed : System.IDisposable
    {
        private const int FontSize = 22;
        private const float LineHeight = 30f;

        private readonly KillFeedModel _model = new KillFeedModel();
        private readonly GameObject _root;
        private readonly Text[] _lines = new Text[KillFeedModel.Capacity];
        private int _shownVersion;

        public KillFeed()
        {
            _root = UiFactory.CreateCanvas("KillFeed", 94, interactive: false);   // above PoiLabel (93), under the crosshair (100)
            for (int i = 0; i < _lines.Length; i++)
            {
                _lines[i] = UiFactory.CreateText("Line" + i, _root.transform, string.Empty, FontSize, TextAnchor.UpperRight,
                    new Vector2(1f, 1f), new Vector2(-24f, -24f - LineHeight * i), new Vector2(640f, LineHeight));
                _lines[i].horizontalOverflow = HorizontalWrapMode.Overflow;
            }
        }

        // line: built once per death (UiText.KillLine). now: unscaled seconds.
        public void Add(string line, float now) => _model.Add(line, now);

        public void Clear() => _model.Clear();

        // Once per frame.
        public void Tick(float now)
        {
            // Unity null: the canvas can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null) return;
            _model.Expire(now);
            if (_model.Version == _shownVersion) return;
            _shownVersion = _model.Version;
            for (int i = 0; i < _lines.Length; i++) _lines[i].text = i < _model.Count ? _model.Line(i) : string.Empty;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
```

**전체 교체** `Client/Assets/Scripts/Game/MatchHudText.cs`:

```csharp
using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // The strings of the match HUD (D14), built only when a shown value changes, so a HUD that shows the same
    // thing every frame allocates nothing. Pure (no UnityEngine): MatchHud puts a string on screen when its Set*
    // call returns true. Phase 11 D2, D11: Korean (UiFont has Hangul); the match result moved to the result screen.
    public sealed class MatchHudText
    {
        private MatchFlowState _state = (MatchFlowState)255;   // forces the first SetStatus to build
        private int _statusA = -1;
        private int _statusB = -1;
        private ZoneHint _zoneHint;
        private int _zoneSeconds = -1;
        private ushort _spectating;
        private string _spectatingName;

        // How many strings were built so far (tests check that unchanged values build nothing).
        public int Rebuilds { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public string Zone { get; private set; } = string.Empty;
        public string Spectating { get; private set; } = string.Empty;

        // The top line: "플레이어를 기다리는 중 1/2", "시작까지 7초", "생존 3/5", "경기 종료". secondsLeft is used while
        // Starting; alive / participants / minPlayers come from MatchState.
        public bool SetStatus(MatchFlowState state, int secondsLeft, int alive, int participants, int minPlayers)
        {
            int a;
            int b;
            switch (state)
            {
                case MatchFlowState.WaitingForPlayers: a = participants; b = minPlayers; break;
                case MatchFlowState.Starting: a = secondsLeft; b = 0; break;
                case MatchFlowState.Playing:
                case MatchFlowState.FinalPhase: a = alive; b = participants; break;
                default: a = 0; b = 0; break;
            }
            if (state == _state && a == _statusA && b == _statusB) return false;
            _state = state;
            _statusA = a;
            _statusB = b;
            switch (state)
            {
                case MatchFlowState.WaitingForPlayers: Status = "플레이어를 기다리는 중 " + a + "/" + b; break;
                case MatchFlowState.Starting: Status = "시작까지 " + a + "초"; break;
                case MatchFlowState.Playing:
                case MatchFlowState.FinalPhase: Status = "생존 " + a + "/" + b; break;
                case MatchFlowState.Finished: Status = "경기 종료"; break;
                default: Status = string.Empty; break;
            }
            Rebuilds++;
            return true;
        }

        // "자기장 축소까지 12초" / "자기장 축소 중" / nothing.
        public bool SetZone(ZoneHint hint, int seconds)
        {
            if (hint != ZoneHint.ShrinksIn) seconds = 0;
            if (hint == _zoneHint && seconds == _zoneSeconds) return false;
            _zoneHint = hint;
            _zoneSeconds = seconds;
            switch (hint)
            {
                case ZoneHint.ShrinksIn: Zone = "자기장 축소까지 " + seconds + "초"; break;
                case ZoneHint.Closing: Zone = "자기장 축소 중"; break;
                default: Zone = string.Empty; break;
            }
            Rebuilds++;
            return true;
        }

        // Phase 11 D9: "관전 중: alice", or "관전 중: 플레이어 3" when that player's name is not known. 0 hides it. The name
        // is PlayerSpawned's string, compared by reference.
        public bool SetSpectating(ushort entityId, string name)
        {
            if (entityId == _spectating && ReferenceEquals(name, _spectatingName)) return false;
            _spectating = entityId;
            _spectatingName = name;
            Spectating = entityId == 0 ? string.Empty : "관전 중: " + UiText.NameOr(name, entityId);
            Rebuilds++;
            return true;
        }
    }
}
```

`NetClient`는 끊김을 설명할 값(LiteNetLib 이유, 거절 이유, 마지막 Join 결과, 시작 실패)을 남기고 `StatsResponse`를 이벤트로 올린다. `InputReader`는 Esc와 F1을 읽는다.

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t4_net.py`로 저장하고 `python <경로>/p11_t4_net.py E:/popol/ProjectH`로 실행한다. `NetClient.cs`, `InputReader.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Client/Assets/Scripts/Net/NetClient.cs', [
(r"""        public event Action<MatchResult> MatchResultReceived;
""",
 r"""        public event Action<MatchResult> MatchResultReceived;
        // Phase 11 D8: the answer to RequestStats (a class allocated by its reader, once per answer).
        public event Action<StatsResponse> StatsReceived;
"""),
(r"""        public bool LastDisconnectRetryable { get; private set; }
""",
 r"""        public bool LastDisconnectRetryable { get; private set; }
        // Phase 11 D6: the rest of what the disconnected screen explains (UiText.Disconnect). Connect resets the reject,
        // the join answer and the start failure; a disconnect sets the reason (and the reject, when refused).
        public DisconnectReason LastDisconnectReason { get; private set; }
        public RejectReason LastRejectReason { get; private set; }
        public JoinResult LastJoinResult { get; private set; }
        public bool LastConnectStartFailed { get; private set; }
"""),
(r"""            if (_disposed || State != ClientState.Disconnected) return;
            _net.ReconnectDelay = reconnectAttempt ? DisconnectCodes.ReconnectRequestIntervalMs : _defaultReconnectDelay;
""",
 r"""            if (_disposed || State != ClientState.Disconnected) return;
            LastRejectReason = RejectReason.None;
            LastJoinResult = JoinResult.Ok;
            LastConnectStartFailed = true;   // until the connect below is under way
            _net.ReconnectDelay = reconnectAttempt ? DisconnectCodes.ReconnectRequestIntervalMs : _defaultReconnectDelay;
"""),
(r"""            LastError = null;
            State = _server != null ? ClientState.Connecting : ClientState.Disconnected;
""",
 r"""            LastError = null;
            LastConnectStartFailed = _server == null;
            State = _server != null ? ClientState.Connecting : ClientState.Disconnected;
"""),
(r"""        public void Disconnect()
        {
            if (_server != null) _net.DisconnectPeer(_server);
        }
""",
 r"""        public void Disconnect()
        {
            if (_server != null) _net.DisconnectPeer(_server);
        }

        // Phase 11 D8: asks for this player's statistics; the answer comes as StatsReceived. False when not joined.
        public bool RequestStats()
        {
            if (State != ClientState.Joined) return false;
            var writer = new PacketWriter(_sendBuffer);
            StatsRequest.Write(ref writer);
            _server.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            return true;
        }
"""),
(r"""            LastDisconnectRetryable = DisconnectCodes.ShouldReconnect(remoteClose, LastDisconnectCode, networkLoss);

            string reason = why.ToString();
            if (why == DisconnectReason.ConnectionRejected &&
                disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
            {
                reason = "Rejected: " + (RejectReason)disconnectInfo.AdditionalData.GetByte();
            }
""",
 r"""            LastDisconnectRetryable = DisconnectCodes.ShouldReconnect(remoteClose, LastDisconnectCode, networkLoss);
            LastDisconnectReason = why;
            LastRejectReason = RejectReason.None;

            string reason = why.ToString();
            if (why == DisconnectReason.ConnectionRejected &&
                disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.AvailableBytes > 0)
            {
                LastRejectReason = (RejectReason)disconnectInfo.AdditionalData.GetByte();
                reason = "Rejected: " + LastRejectReason;
            }
"""),
(r"""                    if (JoinMatchResponse.TryRead(ref packet, out var response))
                    {
""",
 r"""                    if (JoinMatchResponse.TryRead(ref packet, out var response))
                    {
                        LastJoinResult = response.Result;
"""),
(r"""                case PacketId.MatchResult:
                    if (MatchResult.TryRead(ref packet, out var result)) MatchResultReceived?.Invoke(result);
                    break;
""",
 r"""                case PacketId.MatchResult:
                    if (MatchResult.TryRead(ref packet, out var result)) MatchResultReceived?.Invoke(result);
                    break;

                case PacketId.StatsResponse:
                    if (StatsResponse.TryRead(ref packet, out var stats)) StatsReceived?.Invoke(stats);
                    break;
"""),
])

edit('Client/Assets/Scripts/Input/InputReader.cs', [
(r"""        private readonly InputAction _unlockCursor;
""",
 r"""        private readonly InputAction _escape;
        private readonly InputAction _debugToggle;
"""),
(r"""            _unlockCursor = new InputAction("UnlockCursor", InputActionType.Button, "<Keyboard>/escape");
""",
 r"""            // Phase 11 D5: Esc opens and closes the menu (UiFlow decides; the cursor follows it). F1: the debug line (D4).
            _escape = new InputAction("Escape", InputActionType.Button, "<Keyboard>/escape");
            _debugToggle = new InputAction("DebugToggle", InputActionType.Button, "<Keyboard>/f1");
"""),
(r"""            _unlockCursor.Enable();
""",
 r"""            _escape.Enable();
            _debugToggle.Enable();
"""),
(r"""        public bool UnlockCursorPressed => _unlockCursor.WasPressedThisFrame();
""",
 r"""        public bool EscapePressed => _escape.WasPressedThisFrame();
        public bool DebugTogglePressed => _debugToggle.WasPressedThisFrame();
"""),
(r"""            _unlockCursor.Dispose();
""",
 r"""            _escape.Dispose();
            _debugToggle.Dispose();
"""),
])
print('p11_t4_net ok')
```

`GameClient`는 이름 표, 끊김·결과·전적 정보를 읽기 전용으로 보여 주고, `SetUiControl`로 커서와 입력 막기를 받는다. `KillFeed`를 만들고 해제한다. 해제 맨 끝에 `UiFont.Release`를 부른다.

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t4_gameclient.py`로 저장하고 `python <경로>/p11_t4_gameclient.py E:/popol/ProjectH`로 실행한다. `GameClient.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Client/Assets/Scripts/Game/GameClient.cs', [
(r"""using System;
using ProjectH.Client.Bootstrap;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Input;
using ProjectH.Client.Net;
""",
 r"""using System;
using System.Collections.Generic;
using ProjectH.Client.Bootstrap;
using ProjectH.Client.CameraControl;
using ProjectH.Client.Input;
using ProjectH.Client.Net;
using ProjectH.Client.UI;
"""),
(r"""        private PoiLabel _poiLabel;
""",
 r"""        private PoiLabel _poiLabel;
        private KillFeed _killFeed;
        // Phase 11 D9: entity id -> name from PlayerSpawned. At most one entry per player in the match: removed on
        // despawn, cleared with the match state (disconnect).
        private readonly Dictionary<ushort, string> _names = new Dictionary<ushort, string>();
        // Phase 11 D5, set by UiRoot every frame from UiFlow: may a click lock the cursor, and is game input blocked.
        private bool _cursorLockAllowed = true;
        private bool _inputBlocked;
        // Phase 11 D7: how this round ended for us (from our own PlayerDied); the killer's name is kept because the killer
        // may leave before the result. Reset by our respawn (the next round) and with the match state.
        private bool _died;
        private bool _killedByZone;
        private string _killerName;
        // Phase 11 D8: the newest statistics answer, when the request in effect was sent and when an answer came
        // (unscaled seconds, negative = never). Cleared with the match state.
        private StatsResponse _stats;
        private float _statsSentAt = -1f;
        private float _statsAnsweredAt = -1f;
"""),
(r"""        public int ReconnectAttempt =>
            _reconnectPending && _net.State == ClientState.Disconnected ? _reconnectAttempt + 1 : _reconnectAttempt;
""",
 r"""        public int ReconnectAttempt =>
            _reconnectPending && _net.State == ClientState.Disconnected ? _reconnectAttempt + 1 : _reconnectAttempt;

        // ---- Phase 11: read-only state for the UI (UiRoot draws it; nothing here changes the game) ----

        // D6: why the last connection ended.
        public DisconnectSummary LastDisconnect => new DisconnectSummary
        {
            StartFailed = _net.LastConnectStartFailed,
            Reason = _net.LastDisconnectReason,
            Code = _net.LastDisconnectCode,
            Reject = _net.LastRejectReason,
            Join = _net.LastJoinResult,
        };

        // D6: seconds until the next automatic attempt starts; 0 while an attempt is connecting or none is running.
        public float NextReconnectIn =>
            _reconnectPending && _net.State == ClientState.Disconnected ? Mathf.Max(0f, _reconnectAt - Time.unscaledTime) : 0f;

        // D9: the name a PlayerSpawned gave this entity, or null when it is not known.
        public string NameOf(ushort entityId) => _names.TryGetValue(entityId, out string name) ? name : null;

        // D7: the match as the HUD sees it, and how the round ended for us.
        public bool HasMatch => _hasMatch;
        public MatchState Match => _match;
        public bool HasResult => _hasResult;
        public MatchResult Result => _result;
        // Counts MatchResults since the start (UiFlow opens the result screen once per new one).
        public int ResultCount { get; private set; }
        public bool DiedThisRound => _died;
        public bool KilledByZone => _killedByZone;
        public string KillerName => _killerName;

        // D7: whole seconds until the current state's timer ends (Starting, Finished), 0 without one.
        public int StateSecondsLeft
        {
            get
            {
                if (!_hasMatch || _clock == null || !_clock.IsReady || _simHz <= 0) return 0;
                double tick = _renderTick + _interpolationDelaySeconds * _simHz;
                return _match.StateEndTick > tick ? (int)Math.Ceiling((_match.StateEndTick - tick) / _simHz) : 0;
            }
        }

        // D4, D5: Esc and F1 of this frame, read by UiRoot (InputReader stays the only Input System user).
        public bool EscapePressed => _input.EscapePressed;
        public bool DebugTogglePressed => _input.DebugTogglePressed;

        // D5: UiRoot passes UiFlow's outputs every frame. Without a screen up a click locks the cursor as before; with
        // one up the cursor is freed and movement, fire, aim and look are zero (inputs still go out, empty).
        public void SetUiControl(bool allowCursorLock, bool blockInput)
        {
            _cursorLockAllowed = allowCursorLock;
            _inputBlocked = blockInput;
        }

        // D8: the newest answer and the times the stats window compares (StatsWait).
        public StatsResponse LastStats => _stats;
        public float StatsSentAt => _statsSentAt;
        public float StatsAnsweredAt => _statsAnsweredAt;

        // D8: sends a request unless one went out less than StatsWait.ResendSeconds ago (the server would drop it; its
        // answer is here or on the way). Returns when the request in effect was sent (negative = none could be sent).
        public float RequestStats()
        {
            float now = Time.unscaledTime;
            if (StatsWait.MaySend(now, _statsSentAt) && _net.RequestStats()) _statsSentAt = now;
            return _statsSentAt;
        }

        // D6: the disconnected screen's Cancel. Stops the automatic reconnect; an attempt still connecting is given up
        // without a disconnect event, so the reason on screen stays the one that started the cycle.
        public void StopReconnecting()
        {
            CancelReconnect();
            if (_net.State == ClientState.Connecting) _net.CancelConnect();
            else if (_net.State == ClientState.Connected) _net.Disconnect();
        }
"""),
(r"""            _poiLabel = new PoiLabel();
""",
 r"""            _poiLabel = new PoiLabel();
            _killFeed = new KillFeed();
"""),
(r"""            _net.MatchResultReceived += OnMatchResult;
        }
""",
 r"""            _net.MatchResultReceived += OnMatchResult;
            _net.StatsReceived += OnStats;
        }
"""),
(r"""            _camera.ApplyLook(_input.LookDelta, _aiming);
            InputButtons held = InputButtons.None;
            if (_input.Sprint) held |= InputButtons.Sprint;
            if (_fireHeld) held |= InputButtons.Fire;
            InputButtons queued = _input.QueuedButtons;
            _pendingSteps += _predictor.Advance(Time.deltaTime, _input.Move, _camera.Yaw, held, ref queued);
""",
 r"""            // Phase 11 D5: with a screen up or the cursor free, no look, move, sprint or fire, and keys pressed meanwhile
            // are dropped. The predictor still steps, so empty inputs keep going out (the Phase 10 input timeout).
            bool blocked = _inputBlocked || Cursor.lockState != CursorLockMode.Locked;
            _camera.ApplyLook(blocked ? Vector2.zero : _input.LookDelta, _aiming);
            InputButtons held = InputButtons.None;
            if (!blocked && _input.Sprint) held |= InputButtons.Sprint;
            if (_fireHeld) held |= InputButtons.Fire;
            if (blocked) _input.QueuedButtons = InputButtons.None;
            InputButtons queued = _input.QueuedButtons;
            _pendingSteps += _predictor.Advance(Time.deltaTime, blocked ? Vector2.zero : _input.Move, _camera.Yaw, held, ref queued);
"""),
(r"""            // Before the early return: items spin (and are visible) before the local player has spawned. No allocation.
            _worldItems.Tick(Time.time);
""",
 r"""            // Before the early return: items spin (and are visible) before the local player has spawned. No allocation.
            _worldItems.Tick(Time.time);
            _killFeed.Tick(Time.unscaledTime);
"""),
(r"""            _crosshair.SetVisible(alive);
""",
 r"""            _crosshair.SetVisible(alive && !_inputBlocked);
"""),
(r"""        // D14: state line, zone line and circle, red edges outside the zone, result, spectating. Strings are rebuilt
        // only on change (MatchHudText). Times use the estimated current server tick: the render tick plus the
        // interpolation delay.
        private void UpdateMatchHud(bool alive)
        {
            if (!_hasMatch || _clock == null || !_clock.IsReady || _simHz <= 0) return;
            double tick = _renderTick + _interpolationDelaySeconds * _simHz;

            int secondsLeft = _match.StateEndTick > tick ? (int)Math.Ceiling((_match.StateEndTick - tick) / _simHz) : 0;
            _matchHud.SetStatus(_match.State, secondsLeft, _match.Alive, _match.Participants, _match.MinPlayers);
""",
 r"""        // D14: state line, zone line and circle, red edges outside the zone, spectating. Strings are rebuilt only on
        // change (MatchHudText). Times use the estimated current server tick: the render tick plus the interpolation
        // delay. Phase 11 D7: the result is the result screen's (UiRoot), no longer a HUD line.
        private void UpdateMatchHud(bool alive)
        {
            if (!_hasMatch || _clock == null || !_clock.IsReady || _simHz <= 0) return;
            double tick = _renderTick + _interpolationDelaySeconds * _simHz;

            _matchHud.SetStatus(_match.State, StateSecondsLeft, _match.Alive, _match.Participants, _match.MinPlayers);
"""),
(r"""            if (_hasResult) _matchHud.SetResult(_result.WinnerId == MyEntityId, _result.Placement, _result.Kills);
            else _matchHud.SetResult(false, 0, 0);
            _matchHud.SetSpectating(_spectator.Active ? _spectator.Target : (ushort)0);
""",
 r"""            ushort watched = _spectator.Active ? _spectator.Target : (ushort)0;
            _matchHud.SetSpectating(watched, NameOf(watched));
"""),
(r"""            _net.MatchResultReceived -= OnMatchResult;
            _net.Dispose();
            ClearMatchState();
""",
 r"""            _net.MatchResultReceived -= OnMatchResult;
            _net.StatsReceived -= OnStats;
            _net.Dispose();
            ClearMatchState();
            _killFeed.Dispose();
"""),
(r"""            if (_terrainMesh != null) Destroy(_terrainMesh);
        }
""",
 r"""            if (_terrainMesh != null) Destroy(_terrainMesh);
            // Last: every HUD that used the font is gone (UiRoot's screens go with this GameObject too).
            UiFont.Release();
        }
"""),
(r"""        // Left click locks a free cursor (only once joined) and fires while it is locked (D12).
        // Aim and fire only count while the cursor is locked, i.e. while the mouse controls the game.
        // Phase 5 D5: while spectating, a left click on a locked cursor moves to the next player and never fires;
        // the button must be released before it fires again (a click held into the next round does not shoot).
        // The click that locks the cursor neither fires nor cycles.
        private void UpdateCursorAndButtons()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (_input.UnlockCursorPressed)
            {
                Cursor.lockState = CursorLockMode.None;
                locked = false;
            }
""",
 r"""        // Left click locks a free cursor (only once joined) and fires while it is locked (D12).
        // Aim and fire only count while the cursor is locked, i.e. while the mouse controls the game.
        // Phase 5 D5: while spectating, a left click on a locked cursor moves to the next player and never fires;
        // the button must be released before it fires again (a click held into the next round does not shoot).
        // The click that locks the cursor neither fires nor cycles.
        // Phase 11 D5: Esc no longer unlocks here; it opens the menu (UiFlow), and with any screen up the cursor is
        // freed for its buttons and a click does not lock it.
        private void UpdateCursorAndButtons()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (!_cursorLockAllowed)
            {
                if (locked) Cursor.lockState = CursorLockMode.None;
                locked = false;
            }
"""),
(r"""            _fireHeld = locked && _input.FireHeld && !_fireBlockedUntilRelease && !_spectator.Active;
            _aiming = locked && _input.AimHeld;
""",
 r"""            _fireHeld = locked && !_inputBlocked && _input.FireHeld && !_fireBlockedUntilRelease && !_spectator.Active;
            _aiming = locked && !_inputBlocked && _input.AimHeld;
"""),
(r"""        // Feedback only (the inventory changes through InventoryState). Constant strings: no allocation.
        private void OnPickupResult(PickupResult result)
        {
            if (result.Result == PickupResultCode.Full) _inventoryHud.ShowNotice("Inventory full", Time.time);
            else if (result.Result == PickupResultCode.NothingInRange) _inventoryHud.ShowNotice("Nothing to pick up", Time.time);
        }
""",
 r"""        // Feedback only (the inventory changes through InventoryState). Constant strings: no allocation.
        private void OnPickupResult(PickupResult result)
        {
            if (result.Result == PickupResultCode.Full) _inventoryHud.ShowNotice("가방이 가득 찼습니다", Time.time);
            else if (result.Result == PickupResultCode.NothingInRange) _inventoryHud.ShowNotice("주울 수 있는 물건이 없습니다", Time.time);
        }
"""),
(r"""        private void OnSpawned(PlayerSpawned spawned)
        {
            if (spawned.EntityId == MyEntityId)
""",
 r"""        private void OnSpawned(PlayerSpawned spawned)
        {
            _names[spawned.EntityId] = spawned.Name;
            if (spawned.EntityId == MyEntityId)
"""),
(r"""        private void OnDespawned(ushort entityId)
        {
            _remotePlayers.Despawn(entityId);
        }
""",
 r"""        private void OnDespawned(ushort entityId)
        {
            _remotePlayers.Despawn(entityId);
            _names.Remove(entityId);
        }
"""),
(r"""        private void OnPlayerDied(PlayerDied died)
        {
            if (died.VictimId != MyEntityId || _predictor == null) return;
""",
 r"""        private void OnPlayerDied(PlayerDied died)
        {
            // Phase 11 D10: every death but the "you are spectating" notice a newcomer gets at join (Match sends it with
            // no killer and no placement; a zone death in a match has a placement, and the dev sandbox has no zone). The
            // line is built once here, so the feed allocates per death, never per frame.
            bool notice = died.KillerId == 0 && died.Placement == 0;
            string killer = died.KillerId == 0 ? null : UiText.NameOr(NameOf(died.KillerId), died.KillerId);
            if (!notice) _killFeed.Add(UiText.KillLine(killer, UiText.NameOr(NameOf(died.VictimId), died.VictimId)), Time.unscaledTime);

            if (died.VictimId != MyEntityId || _predictor == null) return;
            if (!notice)
            {
                _died = true;
                _killedByZone = died.KillerId == 0;
                _killerName = killer;
            }
"""),
(r"""            _input.QueuedButtons = InputButtons.None;
            _spectator.End();
            // Empty until the server's InventoryState for the new life arrives (sent right after this event).
""",
 r"""            _input.QueuedButtons = InputButtons.None;
            _spectator.End();
            _died = false;
            _killedByZone = false;
            _killerName = null;
            // Empty until the server's InventoryState for the new life arrives (sent right after this event).
"""),
(r"""        private void OnMatchResult(MatchResult result)
        {
            _result = result;
            _hasResult = true;
        }
""",
 r"""        private void OnMatchResult(MatchResult result)
        {
            _result = result;
            _hasResult = true;
            ResultCount++;
        }

        private void OnStats(StatsResponse response)
        {
            _stats = response;
            _statsAnsweredAt = Time.unscaledTime;
        }
"""),
(r"""            _hasResult = false;
            _spectator.End();
            _zoneView.Clear();
            _matchHud.SetOutside(false);
            _matchHud.SetResult(false, 0, 0);
            _matchHud.SetSpectating(0);
""",
 r"""            _hasResult = false;
            _names.Clear();
            _killFeed.Clear();
            _died = false;
            _killedByZone = false;
            _killerName = null;
            _stats = null;
            _statsSentAt = -1f;
            _statsAnsweredAt = -1f;
            _spectator.End();
            _zoneView.Clear();
            _matchHud.SetOutside(false);
            _matchHud.SetSpectating(0, null);
"""),
])
print('p11_t4_gameclient ok')
```

HUD는 `UiFont`를 쓰고 문구를 한국어로 바꾼다. 배치는 그대로다(D12). `MatchHud`의 가운데 결과 줄은 없앤다.

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t4_hud.py`로 저장하고 `python <경로>/p11_t4_hud.py E:/popol/ProjectH`로 실행한다. `MatchHud.cs`, `CombatHud.cs`, `InventoryHud.cs`, `InventoryHudText.cs`, `PoiLabel.cs`, `DevConnectPanel.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Client/Assets/Scripts/Game/MatchHud.cs', [
(r"""using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;
""",
 r"""using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;
"""),
(r"""    // D14: the match HUD on one Screen Space Overlay canvas built in code (UGUI legacy Text with the built-in font,
    // like CombatHud): the state line and the zone line at the top, the result in the middle, "Spectating" below
    // it, and red screen edges while the local player stands outside the zone. No GraphicRaycaster; nothing is a
""",
 r"""    // D14: the match HUD on one Screen Space Overlay canvas built in code (UGUI legacy Text with UiFont, Phase 11 D2):
    // the state line and the zone line at the top, "관전 중" near the bottom (the result is the result screen's, Phase 11
    // D7), and red screen edges while the local player stands outside the zone. No GraphicRaycaster; nothing is a
"""),
(r"""        private const int FontSize = 22;
        private const int ResultFontSize = 40;
        private const float EdgeThickness = 28f;
""",
 r"""        private const int FontSize = 22;
        private const float EdgeThickness = 28f;
"""),
(r"""        private readonly Text _zone;
        private readonly Text _result;
        private readonly Text _spectating;
""",
 r"""        private readonly Text _zone;
        private readonly Text _spectating;
"""),
(r"""            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _status = CreateText("Status", font, FontSize, new Vector2(0.5f, 1f), new Vector2(0f, -20f));
            _zone = CreateText("Zone", font, FontSize, new Vector2(0.5f, 1f), new Vector2(0f, -50f));
            _zone.color = new Color(0.6f, 0.85f, 1f);
            _result = CreateText("Result", font, ResultFontSize, new Vector2(0.5f, 0.5f), new Vector2(0f, 60f));
            _result.color = new Color(1f, 0.85f, 0.3f);
            _spectating = CreateText("Spectating", font, FontSize, new Vector2(0.5f, 0f), new Vector2(0f, 130f));
""",
 r"""            Font font = UiFont.Get();
            _status = CreateText("Status", font, FontSize, new Vector2(0.5f, 1f), new Vector2(0f, -20f));
            _zone = CreateText("Zone", font, FontSize, new Vector2(0.5f, 1f), new Vector2(0f, -50f));
            _zone.color = new Color(0.6f, 0.85f, 1f);
            _spectating = CreateText("Spectating", font, FontSize, new Vector2(0.5f, 0f), new Vector2(0f, 130f));
"""),
(r"""        // placement 0 hides the result.
        public void SetResult(bool won, int placement, int kills)
        {
            if (_root != null && _text.SetResult(won, placement, kills)) _result.text = _text.Result;
        }

        // 0 hides the line.
        public void SetSpectating(ushort entityId)
        {
            if (_root != null && _text.SetSpectating(entityId)) _spectating.text = _text.Spectating;
        }
""",
 r"""        // 0 hides the line. name: PlayerSpawned's name of that player, null when not known.
        public void SetSpectating(ushort entityId, string name)
        {
            if (_root != null && _text.SetSpectating(entityId, name)) _spectating.text = _text.Spectating;
        }
"""),
])

edit('Client/Assets/Scripts/Game/CombatHud.cs', [
(r"""using UnityEngine;
using UnityEngine.UI;
""",
 r"""using ProjectH.Client.UI;
using UnityEngine;
using UnityEngine.UI;
"""),
(r"""    // D13: combat HUD built in code on one Screen Space Overlay canvas (UGUI legacy Text, built-in font; no
    // TextMeshPro, which needs imported assets). No GraphicRaycaster and nothing is a raycast target.
""",
 r"""    // D13: combat HUD built in code on one Screen Space Overlay canvas (UGUI legacy Text with UiFont, Phase 11 D2; no
    // TextMeshPro, which needs imported assets). No GraphicRaycaster and nothing is a raycast target.
"""),
(r"""            // Unity 6 built-in font; Arial.ttf is no longer a built-in resource.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
""",
 r"""            Font font = UiFont.Get();
"""),
(r"""            _vitals.text = "HP " + health + "   SH " + shield;
""",
 r"""            _vitals.text = "체력 " + health + "   실드 " + shield;
"""),
(r"""            _weapon.text = reloading ? name + "   reloading..." : name + "   " + ammo + " / " + reserve;
""",
 r"""            _weapon.text = reloading ? name + "   재장전 중..." : name + "   " + ammo + " / " + reserve;
"""),
(r"""                    _center.text = "DEAD   respawn in " + seconds;
""",
 r"""                    _center.text = "사망   " + seconds + "초 뒤 부활";
"""),
])

edit('Client/Assets/Scripts/Game/InventoryHud.cs', [
(r"""using UnityEngine;
using UnityEngine.UI;
""",
 r"""using ProjectH.Client.UI;
using UnityEngine;
using UnityEngine.UI;
"""),
(r"""    // D15: inventory HUD and pickup prompt on one Screen Space Overlay canvas built in code (UGUI legacy Text
    // with the built-in font, like CombatHud). No GraphicRaycaster; nothing is a raycast target. Text is set
""",
 r"""    // D15: inventory HUD and pickup prompt on one Screen Space Overlay canvas built in code (UGUI legacy Text
    // with UiFont, like CombatHud). No GraphicRaycaster; nothing is a raycast target. Text is set
"""),
(r"""            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            for (int i = 0; i < _slots.Length; i++)
""",
 r"""            Font font = UiFont.Get();
            for (int i = 0; i < _slots.Length; i++)
"""),
])

edit('Client/Assets/Scripts/Game/InventoryHudText.cs', [
(r"""            Consumables = "[4] Medkit x" + medkits + "    [5] Shield Cell x" + shieldCells;
""",
 r"""            Consumables = "[4] 구급상자 x" + medkits + "    [5] 실드 셀 x" + shieldCells;
"""),
(r"""        // itemId 0 = nothing in reach. rarity null = a stack (ammo, heal), shown with its amount:
        // "[E] Pick up Vesper AR [Rare]", "[E] Pick up Light Rounds x60". The text changes with the target
        // or its amount (a partial pickup leaves a smaller stack).
""",
 r"""        // itemId 0 = nothing in reach. rarity null = a stack (ammo, heal), shown with its amount:
        // "[E] 줍기: Vesper AR [Rare]", "[E] 줍기: Light Rounds x60" (item names are the server catalog's). The text
        // changes with the target or its amount (a partial pickup leaves a smaller stack).
"""),
(r"""            else if (rarity != null) Prompt = "[E] Pick up " + name + " [" + rarity + "]";
            else Prompt = "[E] Pick up " + name + " x" + amount;
""",
 r"""            else if (rarity != null) Prompt = "[E] 줍기: " + name + " [" + rarity + "]";
            else Prompt = "[E] 줍기: " + name + " x" + amount;
"""),
])

edit('Client/Assets/Scripts/Game/PoiLabel.cs', [
(r"""using ProjectH.Shared.Simulation;
using UnityEngine;
""",
 r"""using ProjectH.Client.UI;
using ProjectH.Shared.Simulation;
using UnityEngine;
"""),
(r"""    // Phase 6 D7: the name of the place the followed player stands in, top left (UGUI legacy Text with the built-in
    // font, like MatchHud). The text is set only when the place changes, from the constant names in MapPois, so an
""",
 r"""    // Phase 6 D7: the name of the place the followed player stands in, top left (UGUI legacy Text with UiFont, like
    // MatchHud). The text is set only when the place changes, from the constant names in MapPois, so an
"""),
(r"""            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
""",
 r"""            _text.font = UiFont.Get();
"""),
])

edit('Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs', [
(r"""            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame) _visible = !_visible;
""",
 r"""            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame) _visible = !_visible;
            // Until UiRoot replaces this panel (Phase 11 Task 5): Esc frees the cursor, which GameClient no longer does.
            if (_client.EscapePressed) Cursor.lockState = CursorLockMode.None;
"""),
])
print('p11_t4_hud ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet build <스크래치>/uc/UnityCompile.csproj` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/edittests/EditTests.csproj` → 9개 통과(`MatchHudTextTests` 5, `InventoryHudTextTests` 4. 결과 줄 테스트가 없어져 하나 줄었다)

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 843개(834 통과, 9개 건너뜀)로 Task 3과 같다.

- [ ] **Step 5: Commit** — `feat(client): expose disconnect, name, result and stats state, block game input for the UI, Korean HUD with an OS font, kill feed`

---

### Task 5: Client 화면 (`UiRoot`와 화면들), `DevConnectPanel` 제거

**Files:**
- Create: `Client/Assets/Scripts/UI/TitleScreen.cs`, `UI/MenuScreen.cs`, `UI/DisconnectScreen.cs`, `UI/ResultScreen.cs`, `UI/StatsWindow.cs`, `UI/DebugOverlay.cs`, `UI/UiRoot.cs`
- Modify: `Client/Assets/Scripts/Bootstrap/GameBootstrap.cs`
- Delete: `Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs`, `Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs.meta`

**Interfaces:**
- Consumes: Task 3의 `UiFlow`, `UiText`, `StatsWait`. Task 4의 `GameClient` 공개 값과 명령, `UiFactory`, `UiFont`. `LaunchArgs`(그대로).
- Produces:
  - `UiRoot : MonoBehaviour`(`[RequireComponent(typeof(GameClient))]`). `GameBootstrap`이 `GameClient` 다음에 붙인다.
  - `TitleScreen(canvas, onConnect(host, port, name), onCancel, onQuit)`: `Fill`, `SetVisible`, `SetMessage`, `SetConnecting`, `SetConnectEnabled`
  - `MenuScreen(canvas, onContinue, onStats, onDisconnect, onQuit)`: `SetVisible`
  - `DisconnectScreen(canvas, onCancel, onRetry, onToTitle)`: `SetVisible`, `SetReason`, `SetReconnecting`, `SetProgress`
  - `ResultScreen(canvas, onContinue, onStats)`: `SetVisible`, `Show(...)`, `SetSecondsLeft`
  - `StatsWindow(canvas, onClose)`: `SetVisible`, `Tick(now, sentAt, answeredAt, latest, utcOffset)`
  - `DebugOverlay`: `Toggle`, `Tick(state, rtt, entity)`, `Dispose`

Canvas와 정렬 순서: 화면 Canvas `UiScreens` 110(GraphicRaycaster 있음, 모든 HUD와 조준점 100 위), `KillFeed` 94, `DebugOverlay` 120. 화면 Canvas 안의 그리기 순서는 만든 순서다. 전적 창을 맨 나중에 만들어 메뉴·결과 위에 그린다. 숨긴 화면은 `SetActive(false)`라 다시 그리지 않는다(D11).

- [ ] **Step 1: 화면과 `UiRoot`를 만든다**

**새 파일** `Client/Assets/Scripts/UI/TitleScreen.cs`:

```csharp
using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D4: the title screen, which replaces the development IMGUI panel: address, port and name, Connect and Quit,
    // the last error, and the controls. While connecting (UiScreen.Connecting) the same screen shows "접속하는 중..."
    // and a Cancel button instead of Connect. Built once under the screens canvas; hidden with SetActive (D11). Input is
    // checked here with UiText's rules (the server's rules) before onConnect is called.
    public sealed class TitleScreen
    {
        private const string Controls =
            "WASD 이동   Shift 달리기   Space 점프   왼쪽 클릭 사격   오른쪽 클릭 조준   R 재장전\n" +
            "1/2/3 무기   E 줍기   G 무기 버리기   4 구급상자   5 실드 셀   Esc 메뉴   F1 디버그 정보";

        private readonly GameObject _root;
        private readonly InputField _host;
        private readonly InputField _port;
        private readonly InputField _name;
        private readonly Text _message;
        private readonly Button _connect;
        private readonly Button _cancel;
        private readonly Action<string, int, string> _onConnect;
        private bool _visible = true;
        private bool _connecting;
        private bool _connectEnabled = true;

        public TitleScreen(Transform canvas, Action<string, int, string> onConnect, UnityAction onCancel, UnityAction onQuit)
        {
            _onConnect = onConnect;
            _root = UiFactory.CreateScreen("Title", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(760f, 640f));
            Vector2 center = new Vector2(0.5f, 0.5f);

            Text title = UiFactory.CreateText("Title", panel, "ProjectH", 60, TextAnchor.MiddleCenter, center, new Vector2(0f, 250f), new Vector2(700f, 80f));
            title.color = UiFactory.AccentColor;

            UiFactory.CreateText("HostLabel", panel, "주소", 26, TextAnchor.MiddleRight, center, new Vector2(-230f, 140f), new Vector2(140f, 50f));
            _host = UiFactory.CreateInputField("Host", panel, new Vector2(70f, 140f), new Vector2(420f, 50f), 253, InputField.ContentType.Standard);
            UiFactory.CreateText("PortLabel", panel, "포트", 26, TextAnchor.MiddleRight, center, new Vector2(-230f, 75f), new Vector2(140f, 50f));
            _port = UiFactory.CreateInputField("Port", panel, new Vector2(70f, 75f), new Vector2(420f, 50f), 5, InputField.ContentType.IntegerNumber);
            UiFactory.CreateText("NameLabel", panel, "이름", 26, TextAnchor.MiddleRight, center, new Vector2(-230f, 10f), new Vector2(140f, 50f));
            _name = UiFactory.CreateInputField("Name", panel, new Vector2(70f, 10f), new Vector2(420f, 50f), ProjectH.Shared.Protocol.ProtocolConstants.MaxDevPlayerIdBytes, InputField.ContentType.Standard);
            ((Text)_name.placeholder).text = "1-32바이트";

            _message = UiFactory.CreateText("Message", panel, string.Empty, 22, TextAnchor.MiddleCenter, center, new Vector2(0f, -60f), new Vector2(700f, 60f));
            _message.color = UiFactory.ErrorColor;

            _connect = UiFactory.CreateButton("Connect", panel, "접속", new Vector2(-110f, -140f), new Vector2(200f, 60f), OnConnectClicked);
            _cancel = UiFactory.CreateButton("Cancel", panel, "취소", new Vector2(-110f, -140f), new Vector2(200f, 60f), onCancel);
            _cancel.gameObject.SetActive(false);
            UiFactory.CreateButton("Quit", panel, "종료", new Vector2(110f, -140f), new Vector2(200f, 60f), onQuit);

            Text help = UiFactory.CreateText("Controls", panel, Controls, 18, TextAnchor.MiddleCenter, center, new Vector2(0f, -250f), new Vector2(720f, 70f));
            help.color = new Color(1f, 1f, 1f, 0.6f);
        }

        public void Fill(string host, int port, string name)
        {
            _host.text = host ?? string.Empty;
            _port.text = port > 0 ? port.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            _name.text = name ?? string.Empty;
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // An error to show (a refused input, or why the last connection ended), or empty.
        public void SetMessage(string message)
        {
            _message.color = UiFactory.ErrorColor;
            _message.text = message ?? string.Empty;
        }

        // UiScreen.Connecting: fields locked, Cancel instead of Connect.
        public void SetConnecting(bool connecting)
        {
            if (connecting == _connecting) return;
            _connecting = connecting;
            _connect.gameObject.SetActive(!connecting);
            _cancel.gameObject.SetActive(connecting);
            _host.interactable = !connecting;
            _port.interactable = !connecting;
            _name.interactable = !connecting;
            if (connecting)
            {
                _message.color = UiFactory.TextColor;
                _message.text = UiText.Connecting;
            }
        }

        // Off while the previous connection is still closing (GameClient.Connect would ignore the click).
        public void SetConnectEnabled(bool enabled)
        {
            if (enabled == _connectEnabled) return;
            _connectEnabled = enabled;
            _connect.interactable = enabled;
        }

        private void OnConnectClicked()
        {
            if (!UiText.TryNormalizeHost(_host.text, out string host))
            {
                SetMessage(UiText.HostRule);
                return;
            }
            if (!UiText.TryParsePort(_port.text, out int port))
            {
                SetMessage(UiText.PortRule);
                return;
            }
            if (!UiText.TryNormalizeName(_name.text, out string name))
            {
                SetMessage(UiText.NameRule);
                return;
            }
            _name.text = name;
            _onConnect(host, port, name);
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/MenuScreen.cs`:

```csharp
using UnityEngine;
using UnityEngine.Events;

namespace ProjectH.Client.UI
{
    // Phase 11 D5: the Esc menu. No pause (the server keeps running the match): the character stands still while it is
    // open because UiRoot blocks game input. Nothing on it changes after creation.
    public sealed class MenuScreen
    {
        private readonly GameObject _root;
        private bool _visible = true;

        public MenuScreen(Transform canvas, UnityAction onContinue, UnityAction onStats, UnityAction onDisconnect, UnityAction onQuit)
        {
            _root = UiFactory.CreateScreen("Menu", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(480f, 460f));
            UiFactory.CreateText("Title", panel, "메뉴", 40, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0f, 170f), new Vector2(440f, 60f));
            UiFactory.CreateButton("Continue", panel, "계속하기", new Vector2(0f, 80f), new Vector2(320f, 60f), onContinue);
            UiFactory.CreateButton("Stats", panel, "내 전적", new Vector2(0f, 5f), new Vector2(320f, 60f), onStats);
            UiFactory.CreateButton("Disconnect", panel, "접속 끊기", new Vector2(0f, -70f), new Vector2(320f, 60f), onDisconnect);
            UiFactory.CreateButton("Quit", panel, "게임 종료", new Vector2(0f, -145f), new Vector2(320f, 60f), onQuit);
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/DisconnectScreen.cs`:

```csharp
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D6: why the connection ended, in Korean. While GameClient reconnects on its own it shows the attempt and
    // the seconds to the next one, and Cancel; otherwise Retry and To Title. Texts change only when what they show
    // changes: the reason is one of UiText's constant strings (compared by reference), the progress line is rebuilt only
    // when the attempt or the whole second changes.
    public sealed class DisconnectScreen
    {
        private readonly GameObject _root;
        private readonly Text _reason;
        private readonly Text _progress;
        private readonly GameObject _cancel;
        private readonly GameObject _retry;
        private readonly GameObject _toTitle;
        private bool _visible = true;
        private bool _reconnecting = true;   // forces the first SetReconnecting(false) to lay the buttons out
        private string _shownReason;
        private int _shownAttempt = -1;
        private int _shownSeconds = -1;

        public DisconnectScreen(Transform canvas, UnityAction onCancel, UnityAction onRetry, UnityAction onToTitle)
        {
            _root = UiFactory.CreateScreen("Disconnected", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(760f, 420f));
            Vector2 center = new Vector2(0.5f, 0.5f);
            Text title = UiFactory.CreateText("Title", panel, "연결이 끊겼습니다", 40, TextAnchor.MiddleCenter, center, new Vector2(0f, 140f), new Vector2(700f, 60f));
            title.color = UiFactory.AccentColor;
            _reason = UiFactory.CreateText("Reason", panel, string.Empty, 28, TextAnchor.MiddleCenter, center, new Vector2(0f, 55f), new Vector2(700f, 80f));
            _progress = UiFactory.CreateText("Progress", panel, string.Empty, 24, TextAnchor.MiddleCenter, center, new Vector2(0f, -25f), new Vector2(700f, 50f));
            _cancel = UiFactory.CreateButton("Cancel", panel, "재접속 취소", new Vector2(0f, -125f), new Vector2(260f, 60f), onCancel).gameObject;
            _retry = UiFactory.CreateButton("Retry", panel, "다시 접속", new Vector2(-140f, -125f), new Vector2(240f, 60f), onRetry).gameObject;
            _toTitle = UiFactory.CreateButton("ToTitle", panel, "타이틀로", new Vector2(140f, -125f), new Vector2(240f, 60f), onToTitle).gameObject;
            SetReconnecting(false);
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // reason: one of UiText's constants (UiText.Disconnect).
        public void SetReason(string reason)
        {
            if (ReferenceEquals(reason, _shownReason)) return;
            _shownReason = reason;
            _reason.text = reason;
        }

        public void SetReconnecting(bool reconnecting)
        {
            if (reconnecting == _reconnecting) return;
            _reconnecting = reconnecting;
            _cancel.SetActive(reconnecting);
            _retry.SetActive(!reconnecting);
            _toTitle.SetActive(!reconnecting);
            if (!reconnecting)
            {
                _progress.text = string.Empty;
                _shownAttempt = -1;
                _shownSeconds = -1;
            }
        }

        // secondsLeft 0 = the attempt is connecting now.
        public void SetProgress(int attempt, int maxAttempts, int secondsLeft)
        {
            if (!_reconnecting || (attempt == _shownAttempt && secondsLeft == _shownSeconds)) return;
            _shownAttempt = attempt;
            _shownSeconds = secondsLeft;
            _progress.text = UiText.Reconnecting(attempt, maxAttempts, secondsLeft);
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/ResultScreen.cs`:

```csharp
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D7: the match result (MatchResult plus the names and our own PlayerDied). Show builds its strings once per
    // result; the countdown to the next round is rebuilt only when the whole second changes. Damage and survival time are
    // not in the result packet: "내 전적" opens the statistics from the database.
    public sealed class ResultScreen
    {
        private readonly GameObject _root;
        private readonly Text _title;
        private readonly Text _placement;
        private readonly Text _kills;
        private readonly Text _winner;
        private readonly Text _killedBy;
        private readonly Text _nextRound;
        private bool _visible = true;
        private int _shownSeconds = -1;

        public ResultScreen(Transform canvas, UnityAction onContinue, UnityAction onStats)
        {
            _root = UiFactory.CreateScreen("Result", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(760f, 560f));
            Vector2 center = new Vector2(0.5f, 0.5f);
            _title = UiFactory.CreateText("Title", panel, string.Empty, 56, TextAnchor.MiddleCenter, center, new Vector2(0f, 205f), new Vector2(700f, 80f));
            _title.color = UiFactory.AccentColor;
            _placement = UiFactory.CreateText("Placement", panel, string.Empty, 30, TextAnchor.MiddleCenter, center, new Vector2(0f, 125f), new Vector2(700f, 50f));
            _kills = UiFactory.CreateText("Kills", panel, string.Empty, 30, TextAnchor.MiddleCenter, center, new Vector2(0f, 75f), new Vector2(700f, 50f));
            _winner = UiFactory.CreateText("Winner", panel, string.Empty, 26, TextAnchor.MiddleCenter, center, new Vector2(0f, 20f), new Vector2(700f, 50f));
            _killedBy = UiFactory.CreateText("KilledBy", panel, string.Empty, 26, TextAnchor.MiddleCenter, center, new Vector2(0f, -30f), new Vector2(700f, 50f));
            _nextRound = UiFactory.CreateText("NextRound", panel, string.Empty, 24, TextAnchor.MiddleCenter, center, new Vector2(0f, -95f), new Vector2(700f, 50f));
            UiFactory.CreateButton("Continue", panel, "계속 관전", new Vector2(-140f, -200f), new Vector2(240f, 60f), onContinue);
            UiFactory.CreateButton("Stats", panel, "내 전적", new Vector2(140f, -200f), new Vector2(240f, 60f), onStats);
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // Once when the result screen opens. winnerName null = no winner; killerName is used when died && !byZone.
        public void Show(bool won, int placement, int participants, int kills, string winnerName, bool died, bool byZone, string killerName)
        {
            _title.text = UiText.ResultTitle(won);
            _placement.text = UiText.Placement(placement, participants);
            _kills.text = UiText.Kills(kills);
            _winner.text = UiText.Winner(winnerName);
            _killedBy.text = UiText.KilledBy(died && !won, byZone, killerName);
            _shownSeconds = -1;
        }

        public void SetSecondsLeft(int seconds)
        {
            if (seconds == _shownSeconds) return;
            _shownSeconds = seconds;
            _nextRound.text = UiText.NextRound(seconds);
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/StatsWindow.cs`:

```csharp
using System;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D8: "내 전적" over the menu or the result. UiRoot asks GameClient for a request when it opens; this window
    // shows "불러오는 중..." until the answer, "응답 없음" after StatsWait.AnswerSeconds, then the status, the totals and up
    // to 10 recent matches. Its texts are rebuilt only when the wait state or the answer changes.
    public sealed class StatsWindow
    {
        private readonly GameObject _root;
        private readonly Text _status;
        private readonly Text _summary;
        private readonly Text _rows;
        private bool _visible = true;
        private StatsWaitState _shownState;
        private StatsResponse _shownResponse;
        private bool _shownAny;

        public StatsWindow(Transform canvas, UnityAction onClose)
        {
            _root = UiFactory.CreateScreen("Stats", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(1000f, 700f));
            Vector2 center = new Vector2(0.5f, 0.5f);
            Text title = UiFactory.CreateText("Title", panel, "내 전적", 40, TextAnchor.MiddleCenter, center, new Vector2(0f, 300f), new Vector2(940f, 60f));
            title.color = UiFactory.AccentColor;
            _status = UiFactory.CreateText("Status", panel, string.Empty, 26, TextAnchor.MiddleCenter, center, new Vector2(0f, 235f), new Vector2(940f, 50f));
            _summary = UiFactory.CreateText("Summary", panel, string.Empty, 26, TextAnchor.UpperCenter, center, new Vector2(0f, 175f), new Vector2(940f, 80f));
            _rows = UiFactory.CreateText("Rows", panel, string.Empty, 22, TextAnchor.UpperCenter, center, new Vector2(0f, 60f), new Vector2(940f, 300f));
            _rows.verticalOverflow = VerticalWrapMode.Overflow;
            // Top-aligned blocks: with the pivot on the top edge, the anchored position is where the first line starts.
            ((RectTransform)_rows.transform).pivot = new Vector2(0.5f, 1f);
            ((RectTransform)_summary.transform).pivot = new Vector2(0.5f, 1f);
            UiFactory.CreateButton("Close", panel, "닫기", new Vector2(0f, -295f), new Vector2(220f, 60f), onClose);
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
            if (visible) _shownAny = false;   // redraw on the first Tick after opening
        }

        // Every frame while open. sentAt / answeredAt: GameClient's request and answer times (unscaled seconds).
        public void Tick(float now, float sentAt, float answeredAt, StatsResponse latest, TimeSpan utcOffset)
        {
            StatsWaitState state = StatsWait.Of(now, sentAt, answeredAt);
            StatsResponse response = state == StatsWaitState.Answered ? latest : null;
            if (_shownAny && state == _shownState && ReferenceEquals(response, _shownResponse)) return;
            _shownAny = true;
            _shownState = state;
            _shownResponse = response;

            switch (state)
            {
                case StatsWaitState.Waiting:
                    Set(UiText.StatsLoading, string.Empty, string.Empty);
                    break;
                case StatsWaitState.NoAnswer:
                    Set(UiText.StatsNoAnswer, string.Empty, string.Empty);
                    break;
                default:
                    if (response == null) Set(UiText.StatsNoAnswer, string.Empty, string.Empty);
                    else if (response.Status != StatsStatus.Ok) Set(UiText.StatsStatusText(response.Status), string.Empty, string.Empty);
                    else Set(string.Empty, UiText.StatsSummaryText(response.Summary), UiText.StatsRowsText(response.Rows, utcOffset));
                    break;
            }
        }

        private void Set(string status, string summary, string rows)
        {
            _status.text = status;
            _summary.text = summary;
            _rows.text = rows;
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/DebugOverlay.cs`:

```csharp
using ProjectH.Client.Net;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D4: the development line that used to be in the IMGUI panel (connection state, RTT, entity id), toggled with
    // F1 and hidden at start. One Text on its own canvas (no GraphicRaycaster), so the RTT changing redraws only this
    // canvas. The string is rebuilt only when a shown value changes, and nothing is built while it is hidden.
    public sealed class DebugOverlay : System.IDisposable
    {
        private readonly GameObject _root;
        private readonly Text _text;
        private bool _visible;
        private ClientState _state = (ClientState)(-1);
        private int _rtt = -1;
        private ushort _entity;

        public DebugOverlay()
        {
            _root = UiFactory.CreateCanvas("DebugOverlay", 120, interactive: false);
            _text = UiFactory.CreateText("Line", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -60f), new Vector2(900f, 30f));
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _root.SetActive(false);
        }

        public void Toggle()
        {
            if (_root == null) return;
            _visible = !_visible;
            _root.SetActive(_visible);
            _rtt = -1;   // rebuild on the next Tick
        }

        public void Tick(ClientState state, int roundTripMs, ushort entityId)
        {
            if (_root == null || !_visible) return;
            if (state == _state && roundTripMs == _rtt && entityId == _entity) return;
            _state = state;
            _rtt = roundTripMs;
            _entity = entityId;
            _text.text = UiText.DebugLine(state.ToString(), roundTripMs, entityId);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
```

**새 파일** `Client/Assets/Scripts/UI/UiRoot.cs`:

```csharp
using System;
using ProjectH.Client.Bootstrap;
using ProjectH.Client.Game;
using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using UnityEngine;

namespace ProjectH.Client.UI
{
    // Phase 11: the game UI. Builds the EventSystem (when the scene has none), one interactive canvas for the screens
    // (title, menu, disconnected, result, statistics) and the F1 debug line; every frame it feeds UiFlow what GameClient
    // reports, shows the screen UiFlow chose (only when its Version changed), passes the cursor and input outputs to
    // GameClient (D5) and updates the one visible screen. Button handlers call GameClient and the flow.
    // Lifetime: lives on GameClient's GameObject for the whole session; OnDestroy destroys everything it made. The font
    // belongs to GameClient (UiFont.Release in its OnDestroy).
    [RequireComponent(typeof(GameClient))]
    public sealed class UiRoot : MonoBehaviour
    {
        // D4: the last address, port and name typed on the title screen.
        private const string HostKey = "ProjectH.Host";
        private const string PortKey = "ProjectH.Port";
        private const string NameKey = "ProjectH.Name";

        private readonly UiFlow _flow = new UiFlow();
        private GameClient _client;
        private GameObject _eventSystem;   // made here (destroyed here), or null when the scene had one
        private GameObject _canvas;
        private TitleScreen _title;
        private MenuScreen _menu;
        private DisconnectScreen _disconnect;
        private ResultScreen _result;
        private StatsWindow _stats;
        private DebugOverlay _debug;
        private int _shownVersion = -1;
        private UiScreen _shownScreen = UiScreen.Title;
        private bool _shownStats;
        // The address of the last connect, for Retry.
        private string _host;
        private int _port;
        private string _name;
        private float _statsSentAt = -1f;
        private TimeSpan _utcOffset;

        private void Awake()
        {
            _client = GetComponent<GameClient>();
            _eventSystem = UiFactory.EnsureEventSystem();
            _canvas = UiFactory.CreateCanvas("UiScreens", 110, interactive: true);   // above every HUD and the crosshair (100)
            Transform root = _canvas.transform;
            // Creation order is drawing order: the statistics window last, over the menu and the result.
            _title = new TitleScreen(root, Connect, CancelConnect, Quit);
            _menu = new MenuScreen(root, _flow.ContinuePressed, _flow.OpenStats, Leave, Quit);
            _disconnect = new DisconnectScreen(root, _client.StopReconnecting, Retry, Leave);
            _result = new ResultScreen(root, _flow.ContinuePressed, _flow.OpenStats);
            _stats = new StatsWindow(root, _flow.CloseStats);
            _debug = new DebugOverlay();
        }

        // After every Awake on this GameObject, so GameClient is ready when the command line connects at once.
        private void Start()
        {
            LaunchArgs args = LaunchArgs.FromCommandLine();
            if (args.AutoConnect)
            {
                _title.Fill(args.Host, args.Port, args.DevPlayerId);
                Connect(args.Host, args.Port, args.DevPlayerId);
            }
            else
            {
                _title.Fill(PlayerPrefs.GetString(HostKey, args.Host), PlayerPrefs.GetInt(PortKey, args.Port),
                    PlayerPrefs.GetString(NameKey, args.DevPlayerId));
            }
            Apply();
        }

        private void Update()
        {
            if (_client.DebugTogglePressed) _debug.Toggle();
            if (_client.EscapePressed) _flow.EscapePressed();

            UiConnection connection = _client.State == ClientState.Joined ? UiConnection.Joined
                : _client.State == ClientState.Disconnected ? UiConnection.Offline
                : UiConnection.Connecting;
            _flow.Update(connection, _client.ReconnectAttempt > 0, _client.ResultCount, _client.HasMatch, _client.Match.State);
            if (_flow.Version != _shownVersion) Apply();
            _client.SetUiControl(_flow.AllowCursorLock, _flow.BlocksGameInput);

            switch (_flow.Screen)
            {
                case UiScreen.Title:
                    _title.SetConnectEnabled(_client.State == ClientState.Disconnected);
                    break;
                case UiScreen.Disconnected:
                    // Constant strings: no allocation. The progress line is rebuilt once per second at most.
                    _disconnect.SetReason(UiText.Disconnect(_client.LastDisconnect));
                    if (_flow.Reconnecting)
                        _disconnect.SetProgress(_client.ReconnectAttempt, DisconnectCodes.MaxReconnectAttempts, Mathf.CeilToInt(_client.NextReconnectIn));
                    break;
                case UiScreen.Result:
                    _result.SetSecondsLeft(_client.StateSecondsLeft);
                    break;
            }
            if (_flow.StatsOpen) _stats.Tick(Time.unscaledTime, _statsSentAt, _client.StatsAnsweredAt, _client.LastStats, _utcOffset);
            _debug.Tick(_client.State, _client.RoundTripMs, _client.MyEntityId);
        }

        // Shows what UiFlow chose. Runs only when its Version changed (or at start).
        private void Apply()
        {
            UiScreen screen = _flow.Screen;
            UiScreen previous = _shownScreen;

            _title.SetVisible(screen == UiScreen.Title || screen == UiScreen.Connecting);
            _title.SetConnecting(screen == UiScreen.Connecting);
            if (screen == UiScreen.Title && previous != UiScreen.Title)
            {
                // Back on the title: why the connection ended, unless the player left on purpose.
                _title.SetMessage(previous == UiScreen.Disconnected ? UiText.Disconnect(_client.LastDisconnect) : string.Empty);
            }
            _menu.SetVisible(screen == UiScreen.Menu);
            _disconnect.SetVisible(screen == UiScreen.Disconnected);
            _disconnect.SetReconnecting(_flow.Reconnecting);
            _result.SetVisible(screen == UiScreen.Result);
            if (screen == UiScreen.Result && previous != UiScreen.Result) ShowResult();

            _stats.SetVisible(_flow.StatsOpen);
            if (_flow.StatsOpen && !_shownStats)
            {
                // D8: one request per opening (GameClient reuses one younger than the server's limit).
                _statsSentAt = _client.RequestStats();
                _utcOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            }

            _shownScreen = screen;
            _shownStats = _flow.StatsOpen;
            _shownVersion = _flow.Version;
        }

        private void ShowResult()
        {
            MatchResult r = _client.Result;
            bool won = r.WinnerId != 0 && r.WinnerId == _client.MyEntityId;
            string winner = r.WinnerId == 0 ? null : UiText.NameOr(_client.NameOf(r.WinnerId), r.WinnerId);
            _result.Show(won, r.Placement, r.Participants, r.Kills, winner, _client.DiedThisRound, _client.KilledByZone, _client.KillerName);
        }

        private void Connect(string host, int port, string name)
        {
            _host = host;
            _port = port;
            _name = name;
            PlayerPrefs.SetString(HostKey, host);
            PlayerPrefs.SetInt(PortKey, port);
            PlayerPrefs.SetString(NameKey, name);
            PlayerPrefs.Save();
            _client.Connect(host, port, name);
            _flow.ConnectRequested();
        }

        private void Retry()
        {
            if (_host == null) return;
            _client.Connect(_host, _port, _name);
            _flow.ConnectRequested();
        }

        // Connecting "cancel".
        private void CancelConnect()
        {
            _client.Disconnect();
            _flow.LeaveRequested();
        }

        // Menu "disconnect", Disconnected "to title": also stops an automatic reconnect.
        private void Leave()
        {
            _client.Disconnect();
            _flow.LeaveRequested();
        }

        // A built player closes. In the Editor Application.Quit does nothing (stop Play Mode instead).
        private static void Quit()
        {
            Application.Quit();
        }

        private void OnDestroy()
        {
            _debug?.Dispose();
            if (_canvas != null) Destroy(_canvas);
            if (_eventSystem != null) Destroy(_eventSystem);
        }
    }
}
```

- [ ] **Step 2: `GameBootstrap`이 `UiRoot`를 붙이게 한다**

아래 스크립트를 저장소 밖(예: 스크래치 폴더)에 `p11_t5_impl.py`로 저장하고 `python <경로>/p11_t5_impl.py E:/popol/ProjectH`로 실행한다. `GameBootstrap.cs`을 고친다. 각 바꾸기는 기준 텍스트가 정확히 한 번 나올 때만 적용되고, 파일의 줄바꿈(CRLF/LF)을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])


def edit(path, pairs):
    # Keeps the file's own line endings (the Windows checkout is CRLF) and fails loudly if an anchor is not unique.
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl)
        b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2, 1)
    open(path, 'w', encoding='utf-8', newline='').write(s)
    print('edited', path)

edit('Client/Assets/Scripts/Bootstrap/GameBootstrap.cs', [
(r"""using ProjectH.Client.Game;
using UnityEngine;
""",
 r"""using ProjectH.Client.Game;
using ProjectH.Client.UI;
using UnityEngine;
"""),
(r"""        // Runs after the first scene loads, in any scene, so the prototype needs no scene or prefab
        // edits. Creates exactly one GameClient that lives for the whole session.
""",
 r"""        // Runs after the first scene loads, in any scene, so the prototype needs no scene or prefab
        // edits. Creates exactly one GameClient that lives for the whole session, and its game UI (Phase 11).
"""),
(r"""            go.AddComponent<GameClient>();
            go.AddComponent<DevConnectPanel>();
""",
 r"""            go.AddComponent<GameClient>();
            go.AddComponent<UiRoot>();
"""),
])
print('p11_t5_impl ok')
```

- [ ] **Step 3: `DevConnectPanel`을 지운다**

Run:

```bash
git rm Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs Client/Assets/Scripts/Bootstrap/DevConnectPanel.cs.meta
```

Expected: 두 파일이 지워지고 Stage된다. `.meta`는 Unity가 만든 파일이지만 이미 추적 중이라 함께 지운다.

명령줄 자동 접속(`-autoConnect`, `LaunchArgs`)은 이제 `UiRoot.Start`가 한다.

- [ ] **Step 4: 컴파일과 테스트를 확인한다**

Run: `dotnet build <스크래치>/uc/UnityCompile.csproj` → 경고 0, 오류 0
Run: `dotnet test <스크래치>/edittests/EditTests.csproj` → 9개 통과

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0
Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. 전체 843개(834 통과, 9개 건너뜀)다.

Run: `git status --short Client` → 이 Task의 새 파일 7개, `GameBootstrap.cs` 수정, `DevConnectPanel.cs`·`.meta` 삭제만 있다.

- [ ] **Step 5: Commit** — `feat(client): add the game UI (title, menu, disconnected, result, stats, F1 debug line) and remove the dev IMGUI panel`

---

### Task 6: 문서

**Files:**
- Modify: `Docs/Client.md`, `Docs/Networking.md`, `Docs/Server.md`, `Docs/Database.md`, `Docs/Architecture.md`

숫자와 이름은 코드와 같게 쓴다. 이 Task의 내용은 Task 1–5의 코드에서 나온 것만이다.

- [ ] **Step 1: `Docs/Client.md`**

- **첫 문단:** `GameBootstrap`이 `GameClient`+`UiRoot`를 만든다로 고친다.
- **"구조" 표:**
  - `Bootstrap/DevConnectPanel` 행을 지우고 `LaunchArgs`(실행 인자, `-autoConnect`면 `UiRoot.Start`가 바로 접속) 행으로 바꾼다.
  - `Input/InputReader`: Esc(메뉴), F1(디버그 줄)을 적는다.
  - `Game/CombatHud`, `InventoryHud`, `MatchHud`, `PoiLabel`: 글꼴은 `UiFont`, 문구는 한국어다("체력 100   실드 50", "재장전 중...", "사망   3초 뒤 부활", "[E] 줍기: …", "[4] 구급상자 x2    [5] 실드 셀 x3", "플레이어를 기다리는 중 1/2", "시작까지 7초", "생존 3/5", "경기 종료", "자기장 축소까지 12초", "자기장 축소 중", "관전 중: <이름>"). `MatchHud`의 결과 줄은 결과 화면으로 옮겼다.
  - 새 행 `UI/`: `UiFlow`(D3 상태 기계, `StatsWait`), `UiText`(한국어 문구, 이름·포트·주소 검사), `KillFeedModel`(5칸 링, 6초). 세 파일은 UnityEngine을 쓰지 않고 서버 테스트 프로젝트가 소스 링크로 시험한다(`Server/tests/ProjectH.Server.Tests/ClientUi`).
  - 새 행 `UI/UiFont`, `UiFactory`, `KillFeed`, `DebugOverlay`, 화면 5개(`TitleScreen`, `MenuScreen`, `DisconnectScreen`, `ResultScreen`, `StatsWindow`), `UiRoot`. Canvas 정렬 순서(KillFeed 94, UiScreens 110(GraphicRaycaster), DebugOverlay 120)를 적는다.
  - 의존성 문장: `UnityEngine.UI`는 조준점·HUD·화면, `Unity.InputSystem`은 입력과 `InputSystemUIInputModule`에 쓴다.
- **새 절 "화면과 흐름 (Phase 11)":**
  - Mermaid 상태도. `UiFlow` 주석의 전이를 그대로 그린다.

    ```mermaid
    stateDiagram-v2
        [*] --> Title
        Title --> Connecting: 접속
        Connecting --> InGame: Join
        Connecting --> Disconnected: 거절·가득 참·연결 실패
        Connecting --> Title: 취소
        InGame --> Menu: Esc
        Menu --> InGame: Esc·계속하기
        InGame --> Result: 새 MatchResult
        Menu --> Result: 새 MatchResult
        Result --> InGame: 계속 관전·Esc·다음 판(대기·시작)
        InGame --> Disconnected: 연결 끊김
        Menu --> Disconnected: 연결 끊김
        Result --> Disconnected: 연결 끊김
        Disconnected --> InGame: 자동 재접속 Join
        Disconnected --> Connecting: 다시 접속
        Disconnected --> Title: 타이틀로
        Menu --> Title: 접속 끊기
    ```

  - 전적 창은 화면이 아니라 메뉴·결과 위에 연다(Esc는 창부터 닫는다).
  - 커서와 입력(D5): 경기 화면에서만 클릭으로 커서를 잠근다. 다른 화면이 떠 있거나 커서가 풀려 있으면 이동·발사·조준·시점이 0이다. 입력 패킷은 빈 입력으로 계속 간다(Input Timeout). Join한 뒤 화면을 한 번 클릭해야 움직인다.
  - 타이틀: 주소·포트·이름, 마지막 입력값은 `PlayerPrefs`(`ProjectH.Host`, `ProjectH.Port`, `ProjectH.Name`). `-autoConnect`면 명령줄 값으로 바로 접속한다. 이름 규칙: 앞뒤 공백 제거, UTF-8 1–32바이트(한글은 한 글자 3바이트).
  - 끊김 화면: `UiText.Disconnect`의 문구(시작 실패 → 경기 가득 참 → 거절 이유 → 서버 끊기 코드 → LiteNetLib 이유 순서). 자동 재접속 중이면 "재접속 중 (n/3) - k초 뒤 다시 시도"와 재접속 취소, 아니면 다시 접속·타이틀로.
  - 결과 화면: 승리/탈락, 순위/인원, 처치, 승자, 탈락 원인(처치자 이름 또는 자기장), 다음 판까지 남은 초. 다음 판 대기·시작이 오면 저절로 닫힌다.
  - 내 전적: 열 때 요청 1개(2.5초 안에 다시 열면 앞 요청을 쓴다), 5초 안에 답이 없으면 "응답 없음". 상태별 문구("아직 기록이 없습니다.", "기록을 볼 수 없음", Busy 문구), 요약 두 줄, 최근 경기 최대 10줄(현지 시각).
  - Kill Feed: 오른쪽 위, 최근 5줄, 6초. "가해자 ▸ 피해자", Zone이면 "자기장 ▸ 피해자". 경기 중 합류 알림은 넣지 않는다.
  - 이름 표: `PlayerSpawned.Name`. Despawn과 끊김 때 지운다. 모르는 이름은 "플레이어 <id>".
  - F1: `DebugOverlay`(상태, RTT, Entity).
- **새 절 "글꼴 (Phase 11 D2)":** 후보 6개와 순서, `Font.GetOSInstalledFontNames()`에 있는 것만 `Font.CreateDynamicFontFromOSFont(string[], 16)`에 넘긴다(첫 글꼴이 그리고 나머지는 없는 글자의 대체). 없으면 `LegacyRuntime.ttf`와 Warning 로그. 시작 로그 "UI font: …"로 무엇을 골랐는지 본다. `GameClient.OnDestroy`가 마지막에 `UiFont.Release`로 파괴한다. 한글 글꼴이 없는 OS에서는 네모로 나온다(그때 OFL 글꼴을 에셋으로 넣는다).
- **"Lifetime" 절:**
  - 생성 순서 끝에 `KillFeed`를 더하고, 이벤트 구독 수를 21개로 고친다(`StatsReceived` 추가).
  - 해제 순서에 `KillFeed` Dispose와 마지막 `UiFont.Release`를 더한다.
  - `UiRoot`: EventSystem(장면에 없을 때만 만든다, `InputSystemUIInputModule`은 기본 UI Action을 OnEnable에서 붙이고 OnDisable에서 뗀다), 화면 Canvas, DebugOverlay를 만들고 OnDestroy에서 지운다. 버튼 리스너는 Canvas와 함께 사라진다.
- **"끊김과 자동 재접속 (Phase 10)" 절:** `DevConnectPanel`의 "Reconnecting n/3" 문장을 끊김 화면으로 바꾼다. "Connect·Disconnect를 누르면" 문장을 "다시 접속·타이틀로·접속 끊기를 누르면 재접속을 멈춘다. 재접속 취소는 진행 중인 시도를 끊김 이벤트 없이 버린다(`GameClient.StopReconnecting`)"로 바꾼다.
- **"실행과 두 Client 확인" 절:** 2·4·5·6번의 영어 문구와 "Connect"를 새 화면 기준으로 고친다(타이틀에서 접속, Esc = 메뉴, F1 = 디버그 줄). 아래 "Unity 확인 순서"를 새 7번으로 더한다.
- **새 절 "Unity 확인 순서 (Phase 11)":** "Phase 완료 확인" 5의 목록을 그대로 옮긴다.
- **"자동 검사" 절:** `MatchHudTextTests`가 결과 줄 테스트를 잃고(5개) 문구가 한국어가 된 것, 서버 테스트의 `ClientUi`(UiFlow·UiText·KillFeedModel)를 적는다.

- [ ] **Step 2: `Docs/Networking.md`**

- **첫 문단:** `ProtocolVersion` 9(Phase 11: `StatsRequest`/`StatsResponse`, `PlayerSpawned`의 이름). Client 거절 표시 문장("Rejected: <사유>")을 "끊김 화면이 한국어로 보여 준다(`UiText.Reject`)"로 바꾼다.
- **"Packets" 표:**
  - `PlayerSpawned`: EntityId, Position, Yaw, Name(DevPlayerId, 1바이트 길이 + UTF-8 1–32B. 빈 이름·33B 이상은 읽기 실패)
  - 새 행 `StatsRequest` | C→S | ReliableOrdered | 없음(PacketId만). Join을 요청한 연결만, 연결당 2초에 한 번. 본문이 있으면 잘못된 패킷
  - 새 행 `StatsResponse` | S→C(요청한 사람) | ReliableOrdered | Status(0 Ok, 1 NoRecord, 2 Unavailable, 3 Busy), 요약(Matches, Wins, Kills, Deaths, Damage, SurvivalSeconds, 각 u32, 서버가 자른다), Count 0–10, 행(EndedUnixSeconds u32, Round u32, Players, Placement(0 = 순위 없음), Kills u16, Damage u32, SurvivalMs u32 = 20B) × Count, 최신순. Ok가 아니면 요약 0, 행 없음. 최대 227B
  - 크기 문단에 `StatsResponse` 227B를 더한다.
- **새 절 "전적 조회 (Phase 11 D8)":** 흐름(수신 스레드 → 요청 채널 32 → `StatsQueryService` → 응답 채널 32 → Game Loop 송신), 버리는 경우(Join 전·2초 안: 답 없음, `limited`), `Busy`(요청 채널이 가득 참), `Unavailable`(Persistence 꺼짐, DB 실패, 3초 초과), 요청한 연결이 떠났으면 답을 버린다(`undelivered`). Client는 5초 기다린다.
- **"Validation (서버)" 절:** `StatsRequest` 규칙(본문 있음 → `Malformed`, Join 전·속도 초과는 잘못된 패킷이 아니다)을 더한다. Fuzz 테스트가 `StatsResponse` 파서도 거친다.

- [ ] **Step 3: `Docs/Server.md`**

- **"스레드와 소유권" 절:** `StatsQueryService`(async, 요청 채널의 유일한 소비자, DB 조회, 예외를 밖으로 내보내지 않는다)를 표에 더한다. 응답 송신은 Game Loop다.
- **Tick 루프 문장:** `DrainControl` → `DrainInput` → `SweepPeers` → `SendStatsReplies`(최대 32개) → `Match.Tick`.
- **"Queue" 표:** `StatsQueryQueue` 요청 채널(32, Reject → 수신 스레드가 `Busy` 답), 응답 채널(32, Reject → `undelivered`)을 더한다.
- **"Lifetime"의 종료 항목:** 등록 순서 Writer → `StatsQueryService` → `GameServerService`라, Host는 Game Loop → `StatsQueryService` → Writer 순서로 멈춘다. 멈출 때 요청 채널에 남은 요청은 답하지 않는다.
- **"관측" 절:**
  - Health 줄 코드 블록 끝에 `stats requests limited busy unavailable undelivered`를 더하고 뜻을 적는다(Task 2 `StatsQueryCounts` 주석 그대로).
  - Meter 표에 `projecth.stats_queries` | Counter | `result` = `requests` / `limited` / `busy` / `unavailable` / `undelivered`를 더한다.
  - 로그: "Stats queries: persistence disabled…"(시작), DB 실패가 시작될 때 Warning 한 줄, 회복될 때 Information 한 줄.

- [ ] **Step 4: `Docs/Database.md`**

- **새 절 "조회 경로 (Phase 11 D8)":** `StatsQueryService`가 자기 `MatchStore`(같은 연결 문자열)로 `GetStatsAsync` → (기록이 있으면) `GetHistoryAsync(10)`를 부른다. 조회마다 3초(토큰 + `WaitAsync`. MySqlConnector는 연결 인사말을 기다리는 동안 취소를 따르지 않아, 남겨진 조회는 Connection Timeout(5초)까지 남을 수 있다). 결과를 `StatsResponse`로 바꾼다(u32로 자른다, 누적 생존 시간은 초). 기록 없으면 `NoRecord`, 실패·시간 초과·Persistence 꺼짐은 `Unavailable`. 두 쿼리는 Transaction 없이 따로 읽는다(그 사이 저장되면 요약과 행이 한 경기 어긋날 수 있다).
- **"조회 예시" 절:** 첫 문장을 "서버는 Client 요청에 이 두 쿼리로 답한다(위 절). SQL로 직접 볼 때:"로 바꾼다.
- **"테스트" 절:** `[MySqlFact]`를 9개로 고치고 두 테스트(`AStatsQuery_AfterSavedMatches_AnswersOk_WithTotalsAndTheNewestMatchFirst`, `AStatsQuery_ForAnIdWithNoMatch_AnswersNoRecord`)와 DB 없는 `StatsQueryTests`(멈춘 DB에 대한 시간 제한 포함)를 더한다.
- **"범위 밖" 절:** "Client에 통계·전적 보여 주기" 줄을 지운다.

- [ ] **Step 5: `Docs/Architecture.md`**

- 첫 줄을 "Phase 11 Game UI 기준"으로 바꾸고, 설계 근거에 `Docs/specs/2026-10-01-phase11-game-ui-design.md`(게임 UI, 전적 조회, 이름)를 더한다.
- Mermaid:
  - Client에 `UI[UiRoot: UiFlow, screens, KillFeed] --> Game[GameClient]`와 `Net --> UI`를 더한다.
  - Server에 `Listener -->|StatsQuery| StatsQ[StatsQueryQueue] --> StatsSvc[StatsQueryService] --> MySQL`, `StatsSvc -->|StatsReply| StatsQ`, `Loop -->|sends replies| StatsQ`를 더한다.
- `Client/` 행: 화면 흐름은 `UiFlow`(순수, 서버 테스트가 소스 링크로 시험).
- `Shared/` 행: 전적 패킷(`StatsPackets`).
- 마지막 DB 문단에 "Phase 11: Client가 요청하면 `StatsQueryService`가 읽어 답한다. Game Loop는 답을 보내기만 한다"를 더한다.

- [ ] **Step 6: Commit** — `docs: Phase 11 game UI`

---

## Phase 완료 확인

1. **빌드·테스트.**
   - `dotnet build Server/ProjectH.Server.slnx --no-incremental`: 경고 0
   - `dotnet test Server/ProjectH.Server.slnx`: 모두 통과. 843개 중 834 통과, MySQL 9개 건너뜀.
   - 두세 번 반복한다. UDP 통합 테스트가 Timeout에 민감하다. 한 번이라도 실패하면 원인을 찾는다. 같은 테스트가 다시 실패하면 테스트 서버의 Timeout을 늘리지 말고 보고한다(`WorldItemsTests.AddSearchRemove_AllocateNothing` 단독 실패는 Global Constraints대로).
2. **DB가 있으면 `PROJECTH_TEST_MYSQL`을 켜고 DB 테스트를 돌린다.** 컨테이너는 이 계획이 띄우지 않는다. 꺼져 있으면 "DB 테스트 대기"로 보고한다.

   ```bash
   export PROJECTH_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev"
   dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Persistence"
   dotnet test Server/ProjectH.Server.slnx
   ```

   Expected: 첫 명령은 모두 통과하고 건너뜀 0이다. 둘째 명령은 843개 모두 통과, 건너뜀 0이다.
3. **봇 50명 부하 측정(Phase 10 비교).** 컨트롤러가 실행한다. `Docs/LoadTest.md` "Phase 10 확인"과 같은 조건이다.
   - Release
   - 서버: `--Server:Port=7790 --Server:MaxPlayers=100 --Server:DevRespawn=true`(DevRespawn은 DB 저장이 없다)
   - 봇: `--port 7790 --count 50 --duration 120 --connect-interval-ms 30`. `--reconnect`는 기본 false로 둔다.
   - 2분 실행. 집계는 Stats 줄 12개 중 처음 2개를 버린 10개다.

   ```bash
   dotnet build Server/ProjectH.Server.slnx -c Release
   dotnet Server/src/ProjectH.Server/bin/Release/net10.0/ProjectH.Server.dll --Server:Port=7790 --Server:MaxPlayers=100 --Server:DevRespawn=true > load-server-50.log 2>&1 &
   echo $! > load-server.pid
   # 4초 뒤
   dotnet Server/src/ProjectH.Bots/bin/Release/net10.0/ProjectH.Bots.dll --port 7790 --count 50 --duration 120 --connect-interval-ms 30 > load-bots-50.log 2>&1
   kill $(cat load-server.pid)   # 자기가 띄운 pid로만 끈다
   ```

   **비교 기준:** Phase 10 50명 Tick p95는 2회 측정에서 0.13 ms(10줄 범위 0.11–0.13 ms), 1회 측정에서 0.17 ms였다. 이번 Tick p95(10개 줄의 최댓값)가 이와 같은 수준이어야 한다(spec §2 회귀).
   - 이 Phase가 Tick에 더한 일은 빈 응답 채널을 한 번 읽는 것뿐이다(`SendStatsReplies`). 봇은 전적을 요청하지 않는다.
   - 측정은 0.01 ms 단위이고 실행 사이 편차가 있다(Phase 10은 같은 조건에서 0.04 ms 차이). 0.17 ms를 넘으면 한 번 더 잰다. 그래도 넘으면 보고한다.
   - Health 줄도 확인한다: `stats requests=0 limited=0 busy=0 unavailable=0 undelivered=0`, `kicks ... inputTimeout=0`, `joinTimeout=0`, `tickFailures=0`, `stalls=0`, `badPackets` 모두 0.
   - 결과는 `Docs/LoadTest.md`에 "Phase 11 확인" 짧은 절로 남긴다. 숫자는 실제로 잰 값만 적는다.
4. **Unity 컴파일(실제 저장소).**
   - `dotnet build <스크래치>/uc/UnityCompile.csproj`(`RepoRoot` 기본값 = `E:/popol/ProjectH`): 경고 0, 오류 0. EditMode 테스트까지 컴파일된다.
   - `dotnet test <스크래치>/edittests/EditTests.csproj`: 9개 통과.
   - 컨트롤러의 원래 `UnityCompile.csproj`(테스트 제외)로도 `dotnet build`: 경고 0, 오류 0.
   - 그 다음 사용자에게 Unity Editor를 포커스해 달라고 요청한다. 자동 import가 끝나면 `Editor.log`(`%LOCALAPPDATA%/Unity/Editor/Editor.log`)의 새 줄에 `error CS`가 없는지 확인한다. Unity가 Global Constraints의 `.meta`들을 만든다. 사용자가 Editor를 열 수 없으면 "Unity 확인 대기"로 기록하고 넘어간다.
5. **Unity Editor 확인 순서(사용자).** 서버를 `dotnet run --project Server/src/ProjectH.Server`로 띄우고 Play한다. Multiplayer Play Mode로 Player 2도 켠다. `Docs/Client.md`에 같은 목록을 남긴다(Task 6).
   1. Console에 "UI font: Malgun Gothic …"(또는 다른 한글 글꼴)이 나온다. 타이틀·HUD·Kill Feed의 한글이 네모가 아니다.
   2. 타이틀: 주소·포트·이름 입력칸에 영어와 한글(IME 조합 포함)을 칠 수 있다. Console에 `InvalidOperationException`(Input 클래스)이 없다. 이름을 비우거나 한글 11자를 넣고 접속을 누르면 이름 규칙 문구가 나온다.
   3. 서버를 끈 채 접속: "접속하는 중..."과 취소가 보이고, 약 5초 뒤 끊김 화면에 "서버에 연결할 수 없습니다."와 다시 접속·타이틀로가 나온다. 취소를 누르면 바로 타이틀로 간다.
   4. 서버를 켜고 접속: 경기 화면. 클릭 전에는 움직이지 않고, 클릭하면 커서가 잠기고 움직인다.
   5. Esc: 메뉴가 열리고 커서가 풀린다. 메뉴가 열린 동안 WASD·마우스로 캐릭터와 시점이 움직이지 않는다. 10초 넘게 열어 두어도 끊기지 않는다(Input Timeout). Esc·계속하기로 닫힌다.
   6. 메뉴 → 내 전적: "불러오는 중..." 뒤에 기록 또는 "아직 기록이 없습니다."(DB 있음), "기록을 볼 수 없음"(`Persistence:Enabled=false` 또는 DB 없음). 닫고 바로 다시 열어도 "응답 없음"이 되지 않는다.
   7. 두 Client로 한 판(Player 2의 타이틀에 같은 이름이 미리 채워져 있으면 다른 이름으로 바꾼다. `PlayerPrefs`를 같이 쓸 수 있다): Kill Feed(오른쪽 위)에 "이름 ▸ 이름", 관전 줄 "관전 중: <이름>", 결과 화면(승리/탈락, 순위, 처치, 승자, 탈락 원인, 다음 판까지 남은 초). 다음 판 카운트다운이 오면 결과 화면이 닫힌다.
   8. 경기 중 서버를 Ctrl+C로 끈다: 끊김 화면 "서버가 종료되었습니다.", 재접속하지 않는다. 타이틀로를 누르면 타이틀에 같은 이유가 보인다.
   9. 자동 재접속: 경기 중 서버 프로세스를 강제로 끝낸다(작업 관리자. 끊기 코드가 없어 Client는 5초 뒤 Timeout으로 안다). 끊김 화면에 "서버의 응답이 끊겼습니다."와 "재접속 중 (1/3) - k초 뒤 다시 시도", 재접속 취소가 보인다. 바로 서버를 다시 켜면 새 경기 화면으로 돌아간다. 다시 해 보고 이번에는 재접속 취소를 누르면 다시 접속·타이틀로가 나오고 이유 문구는 그대로다.
   10. 메뉴 → 접속 끊기: 타이틀로 가고 이유 문구가 없다. F1이 디버그 줄(상태, RTT, Entity)을 켜고 끈다. 게임 종료는 빌드에서만 창을 닫는다(Editor에서는 아무 일도 없다).
6. **범위 확인.**
   - `git diff --stat main -- Shared`에는 `Protocol`의 `PacketId.cs`, `PacketReader.cs`, `ProtocolConstants.cs`, `ServerPackets.cs`, `StatsPackets.cs`만 있어야 한다. Unity가 만든 `StatsPackets.cs.meta`와, Phase 10부터 추적되지 않은 `DisconnectCode.cs.meta`가 있으면 Push 전에 함께 커밋한다.
   - `git diff --stat main -- Client`에는 `Client/Assets/Scripts`(`UI/` 13개 새 파일, `Net/NetClient.cs`, `Input/InputReader.cs`, `Game/` 7개, `Bootstrap/GameBootstrap.cs`, `Bootstrap/DevConnectPanel.cs`·`.meta` 삭제)와 `Client/Assets/Tests/EditMode`의 두 파일만 있어야 한다. Unity가 만든 `UI.meta`와 `UI/*.cs.meta`도 Push 전에 함께 커밋한다.
   - `git diff --stat main -- Server/src/ProjectH.Bots`는 비어 있어야 한다(Spec 해석 6).
7. **Push.** `github-push` 스킬로 `main`에 Squash Commit·Push한다.
