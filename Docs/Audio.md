# 게임 소리 (Phase 18)

설계 근거: `Docs/specs/2026-10-08-phase18-audio-design.md`(D1–D13), 요청서 STEP 8(§79–§85). 패킷 변경(Protocol v17: `ShotFired` 무기 id, `DamageTaken` 플래그, `BuildEvents` Destroyed 이유, `WorldSound` 47)은 `Networking.md`에 있다. 목표는 BGM이 아니라 정보를 전하는 소리다. 발소리·총성·건설·채집은 3D로 나서 방향을 알 수 있다.

## 구조

모든 코드는 `Client/Assets/Scripts/Game/Audio/`에 있다. 판단 로직은 Unity 없이 돌아가는 순수 코드이고 EditMode 테스트가 고정한다.

| 파일 | 역할 | Unity |
|---|---|---|
| `AudioCatalog.cs` | `SoundKind` 목록과 표 하나(우선순위, 들리는 거리, 중복 간격, 음량, 변형 수). 무기 → 총성, 걸음·재질 → 발소리 매핑 | 없음 |
| `AudioSynth.cs` | 종류·변형별 클립 합성(`float[]`, 22,050 Hz 모노, 결정적 xorshift 시드). 잡음 버스트 + 감쇠·스윕 사인 + 반복 | 없음 |
| `AudioMixerModel.cs` | 큐 64, 거리·중복 버림, 우선순위·거리 정렬, 예산 24, 교체, 프레임당 새 재생 8, 카운터 | 없음 |
| `FootstepModel.cs` | 발소리 판단(`FootstepModel`)과 발밑 재질(`SurfaceProbe`) | 없음 |
| `AudioEventRules.cs` | 출처 표·기준 상태 규칙(문·Container·Supply Drop·자기장 추적, 붕괴 모으기, 재장전 시작, 자기장 밖 틱) | 없음 |
| `AudioClipBank.cs` | 합성 결과를 `AudioClip.Create`로 감싼다. 시작 때 한 번 만들고 `Dispose`에서 파괴 | 있음 |
| `AudioVoicePool.cs` | `AudioSource` 24개(생성 때 한 번). 3D는 spatialBlend 1·Linear 감쇠·종류별 최대 거리, 2D는 spatialBlend 0. Doppler 0, `clip` + `Play`(교체 때 `Stop`) | 있음 |
| `GameAudio.cs` | 위 것을 소유한다. `Play3D`/`Play2D`(큐에 넣기), `TickFootsteps`, `Tick`(섞고 재생), `ResetMatch`, QA 값 | 있음 |
| `UiSound.cs` | 버튼 클릭음 한 곳. `UiFactory.CreateButton`이 모든 버튼에 붙인다 | 없음 |

수명: `GameClient.Awake`가 `GameAudio`를 만들고(`UiSound.Sink` 연결) `OnDestroy`가 해제한다(Sink 해제 → 클립 파괴 → 목소리 GameObject 파괴). 끊김·입장(`ClearMatchState`, `OnJoined`)은 대기 요청·발소리·변화 추적 상태를 지우고, 재생 중인 소리는 끝까지 둔다.

한 프레임: `Update`의 네트워크 처리기와 UI 클릭이 요청을 큐에 넣는다 → `LateUpdate` = `LateUpdateGame`(내 예측 사격 총성 포함) → `TickAudio`(문 변화, 발소리, 재장전, 자기장, 그리고 `GameAudio.Tick`). 듣는 위치는 AudioListener가 있는 어깨 카메라다. 반복 경로에 할당은 없다(고정 배열, 구조체 이벤트, 삽입 정렬).

## 우선순위와 거리 (D3)

숫자가 작을수록 중요하다(요청서 §79). 거리는 3D 소리가 0이 되는 곳이고, 그보다 멀면 믹서가 버린다(§82). Unity 감쇠는 Linear(최소 거리 1 m).

| 우선순위 | 종류와 거리 |
|---|---|
| 1 총성 | AR·SMG·SG·권총 120 m, 저격·로켓 발사·폭발 150 m, 수류탄 던지기 40 m |
| 2 발소리 | 걷기 25 m, 질주 30 m, 웅크림 12 m(작게), 미끄럼 25 m, 기절 끌림 12 m |
| 3 건설·채집 | 설치·편집·피해·파괴·붕괴 40 m, 채집 타격·약점·파괴·휘두르기 30 m, 거절 2D |
| 4 내 몸 | 체력 맞음·실드 맞음·실드 깨짐·맞힘 확인·처치·내 기절·내 사망·재장전, 모두 2D |
| 5 Loot·상호작용 | 줍기 2D, Container 열림 15 m, 재투입 15 m(내 것 2D), 문 20 m, Supply Drop 착지 150 m |
| 6 자기장 | 축소 경고·밖 피해 틱, 2D |
| 7 UI | 클릭·지도 열기/닫기, 2D |

문 거리(20 m), 수류탄 던지기(40 m), 미끄럼·끌림·재투입 거리는 Spec이 정하지 않아 이번에 정했다(표 한 줄만 바꾸면 된다).

## 믹서 (D2)

1. 요청은 고정 큐(64)에 들어간다. 지난 프레임의 듣는 위치에서 종류의 거리보다 먼 3D 요청은 넣을 때 바로 버린다(`droppedDistance`: 큰 건설 패킷의 먼 피해·파괴가 큐를 채우지 못한다). 큐가 가득 차면 큐에서 가장 덜 중요한 요청(우선순위 숫자가 크고, 같으면 먼 것)보다 새 요청이 더 중요할 때만 바꿔 넣는다. 어느 쪽이든 하나를 잃으므로 `queueOverflow`를 센다.
2. 프레임마다 끝난 목소리를 비운다. 끝 시각은 모델이 클립 길이 ÷ 음높이로 계산한다(Unity에 묻지 않는다).
3. 3D 요청이 종류의 거리보다 멀면 버린다(`droppedDistance`).
4. 같은 종류·같은 소스(Entity id, 조각 id, 투사체 id, 문·Container 번호, 0 = 없음)가 최소 간격 안에 다시 오면 버린다(`droppedDuplicate`). 총성 0.04 s(SMG 66.7 ms 연사보다 짧다), 조각 피해 0.15 s(D6 "조각마다 0.15 s"), 재장전 0.5 s(재조정 깜빡임 흡수) 등. 최근 64쌍을 기억하고, 가득 차면 중복 간격이 이미 끝난 칸, 없으면 간격이 가장 먼저 끝나는 칸을 덮는다.
5. 남은 요청을 우선순위, 같으면 거리로 정렬한다.
6. 빈 목소리를 쓰고, 없으면 가장 덜 중요한 목소리(숫자가 크고, 같으면 먼 것)를 새 소리가 **더 중요할 때만** 끊고 바꾼다. 못 들어가거나 프레임당 8개를 넘은 요청과 끊긴 목소리는 `droppedBudget`이다.
7. 음높이는 ±5 %, 변형은 결정적 난수로 고른다.

## 발소리 (D5)

- 모든 플레이어: 내 발(예측 렌더 위치·예측 수평 속도, 2D, 음량 0.5배)과 듣는 위치 30 m 안의 원격 플레이어(보간 위치의 프레임 간 수평 이동, 3D).
- 간격: 걷기 0.5 s, 질주 0.33 s, 웅크림 0.7 s, 기절 끌림 0.9 s. 미끄럼은 시작할 때 한 번. 0.5 m/s 미만과 공중 모드(Freefall·Glide·Transport·Vault)는 소리 없음.
- 순간이동 보호: 등장·부활(`Forget`)·끊김/재개(`Clear`)·죽음·30 m 밖에서 다시 들어옴 뒤 첫 표본은 시작점만 정한다. 수평 12 m/s 초과 이동은 순간이동으로 보고 무시한다. Transport → Freefall은 공중이다.
- 재질은 발소리를 낼 때만 찾는다(`SurfaceProbe`): 발 높이 +0.3 m 이하에서 가장 높은 표면 — 지형 = Ground, 맵 상자 윗면 = Stone, 발 칸의 확정 조각 = 그 기록의 재질(편집된 바닥은 `PartsOf`, 경사면은 `SlopeOf().HeightAt`). 발이 그 표면보다 0.35 m 넘게 위면 공중(Ground 모드의 점프·낙하)이라 소리를 내지 않는다. Spec의 "원격 Ground 모드에서 수직 이동이 크면 쉰다"를 수직 속도 대신 이 높이 차로 구현했다(언덕·경사로에서 질주해도 끊기지 않는다).
- 상태는 플레이어 id마다 고정 칸 하나(최대 101칸). 한 프레임 동안 표본이 없던 칸은 비운다.

## 소리 출처 표 (D9, 중복 방지)

| 사건 | 소리를 내는 곳 | 소리를 내지 않는 곳 |
|---|---|---|
| 내 사격(총) | 예측 사격(`StepWeapons`가 쏜 발, 2D) | 내 `ShotFired` |
| 내 로켓 | 예측 사격(2D) | 내 `ProjectileSpawned` |
| 남의 사격 | `ShotFired`(무기 id → 총성, 사수 눈 위치 3D, 모르는 id는 AR) | |
| 수류탄(나·남) | `ProjectileSpawned`(내 것 2D) | 예측 없음 |
| 폭발 | `ProjectileExploded`(3D) | |
| 내 설치 | 배치의 `BuildResult` Ok(2D) | 내 사건 Placed, 편집의 Ok |
| 남의 설치 | 사건 Placed(주인 ≠ 나, 3D) | Sync(관심 칸 진입·입장) |
| 내 편집 | 확정·Reset을 보낼 때(2D) | 내 Edited, 편집의 Ok |
| 남의 편집 | Edited(주인 ≠ 나, 3D) | |
| 조각 피해 | Health 기록에서 피해가 늘 때(조각마다 0.15 s) | |
| 파괴 / 붕괴 | Destroyed(이유 0) / Collapsed(이유 1, 한 프레임에 여러 개여도 가장 가까운 한 번) | |
| 내 채집 | 휘두르기 예측(2D), `HarvestHit`(3D) | |
| 남의 채집 | `WorldSound`(3D) | |
| 내 문 | 예측으로 마스크가 바뀔 때 | 예측과 같은 서버 `DoorStates`, 1초 안에 예측과 반대로 잠깐 보이는 상태 |
| 남의 문 | 서버 `DoorStates`로 마스크가 바뀔 때 | |
| 피해 | `DamageTaken` 플래그: 실드 깨짐 > 실드 맞음 > 체력 | |
| 맞힘·처치 | `HitConfirmed`(Killed면 처치음) | |
| 내 기절·사망 | `PlayerDowned`·`PlayerDied`(나, 입장 알림 제외) | |
| 줍기 | `PickupResult` Ok | |
| 재투입 | `PlayerRespawned`: 경기 중(Playing·FinalPhase) Ground 모드이고 탈락 상태였거나 스테이션 3 m 안 | 경기 시작(Transport·경기 전), 라운드 리셋(Finished 뒤) |
| 재장전 | 내 `WeaponState.Reloading`이 꺼짐 → 켜짐 | |
| UI | `UiFactory.CreateButton` 클릭, 전체 지도 열기·닫기 | |

## 기준 상태 규칙 (D9)

Join·Resume·라운드 리셋·관심 칸 진입 뒤 처음 받은 상태는 기준일 뿐 변화가 아니다.

- 건설: Sync 기록은 소리가 없다(`NetClient.BuildPlacedEventReceived`는 사건 Placed에만 올라온다).
- 문: 입장·재개 때 서버가 늘 `DoorStates`를 보내므로 다음 하나를 기준으로 삼는다. 경기 시작(수송기 탑승 부활 또는 MatchState Starting → Playing)에는 서버가 문을 모두 닫으므로(`CloseAll`) 1초 안에 온 `DoorStates`만 기준이다(문이 모두 닫혀 있었으면 서버가 보내지 않으므로 기다리지 않고 풀린다). 라운드 리셋과 개발 모드 부활은 문을 건드리지 않아 기준을 걸지 않는다.
- Container: 이전 상태에서 이미 생성되어 있던 것의 열림 비트가 0 → 1일 때만. 입장·재개 뒤 첫 상태는 기억만 한다. 라운드 리셋은 마스크를 비우므로 소리가 없다.
- Supply Drop: 이전 목록에서 Falling이던 Id가 착지했을 때만. 처음 본 착지 상태와 리셋(빈 목록)은 소리가 없다.
- 자기장 축소: `ZoneState`를 받은 순간 `ShrinkStartTick`이 아직 앞일 때만 준비하고, Client의 추정 서버 Tick이 그것을 지나는 프레임에 한 번(경기 중일 때만).
- 투사체 생성: `OwnerId == 0`이거나 `StartTick`이 이번 연결의 입장·재개 응답 `ServerTick` 이하면 재전송으로 보고 소리를 내지 않는다. 서버는 입장·재개 때 날고 있는 투사체를 StartTick = 마지막으로 끝난 Tick(= 응답의 ServerTick)으로 다시 보내고, 실제 발사는 시뮬레이션 중인 Tick(ServerTick + 1)을 싣기 때문이다.
- 자기장 축소 준비는 이벤트 처리기 안에서 추정 Tick과 받은 가장 새 Tick(입장 응답의 ServerTick) 중 큰 쪽을 쓴다(입장 직후의 Poll에서는 렌더 Tick이 아직 0이다).

## QA (D12)

`/qa/status`의 지도 필드 끝(`projectiles` 뒤)에 선택 필드 `audio`가 붙는다(`QaAudioStatus`, Editor·Development Build만):

```json
"audio":{"plays":{"GunAR":3,...모든 종류...},"active":2,"droppedBudget":0,"droppedDuplicate":1,"droppedDistance":4,"queueOverflow":0}
```

`plays`의 키는 `SoundKind` 이름이다. 카운터는 Client 시작부터 늘기만 한다. 소리가 좋은지는 판단하지 않는다(사람 확인은 `QA/Scenarios/Manual/audio_listen.json`).

## Known Issues

- Unity Editor에서 실제 재생(`AudioClip.Create`, `AudioSource`)은 이 Phase의 자동 테스트가 확인하지 않는다. 순수 로직만 EditMode 테스트로 고정했다.
- 합성음은 정보 전달용이라 실제 에셋보다 거칠다. 에셋으로 바꿀 때는 `AudioClipBank`만 바꾼다.
- 원격 재투입 판단은 `PlayerRespawned` 위치가 스테이션 3 m 안인지로 보조한다. Snapshot의 생존 비트가 이벤트보다 먼저 바뀔 수 있어서다.
- 붕괴는 패킷이 아니라 프레임 단위로 모은다. 한 프레임에 두 패킷의 붕괴가 오면 소리는 하나다.
- 키 한 번 = 문 소리 하나: 내 예측 뒤 다른 문의 `DoorStates`가 먼저 오면 `PredictedDoors.ApplyServer`가 예측을 지워 내 문이 잠깐 이전 상태로 보인다. 소리 추적기는 1초(예측 시간) 안에 내 예측과 반대로 바뀐 문을 듣지 않는다. 서버가 끝내 확인하지 않은 예측은 그 1초가 지난 뒤 되돌아가는 소리가 한 번 난다(화면의 문도 되돌아간다).
- 내 사격 여러 발이 한 프레임에 예측되면(낮은 FPS) 총성은 한 번이다(같은 시각·같은 소스라 중복으로 버린다).
- 가림(Occlusion)·잔향, BGM, 음성, 볼륨 메뉴, 원격 재장전 소리는 넣지 않았다(D13).
