# Phase 18 Gameplay Audio — 설계 Spec

## Context

요청서는 `Docs/requests/2026-10-05-roadmap-phase13_5-19-request.md`의 STEP 8(§79–§85)이다. 목표는 BGM이 아니라 **게임 정보를 전하는 소리**다: 총성 → 발소리 → 건설 → 피해·실드 → Loot → 자기장 → UI 순으로 중요하고, 발소리·총성·건설·채집은 3D 방향으로 적 위치를 알 수 있어야 한다.

**지금 구조에서 확인한 사실(`phase17-weapons` fa78616 기준):**

- **소리가 하나도 없다.** AudioSource·AudioClip을 쓰는 코드가 없고, 오디오 에셋도 없다. `BuildAudio.Play`는 빈 훅이다(설치·거절·파괴·채집·휘두르기 호출 자리만 있다). AudioListener는 어깨 카메라에 있다. `AudioManager`는 Real voice 32, Virtual 512다.
- **이벤트:** 원격 사격 `ShotFired`(쏜 사람, 시작, 끝 — **무기 id 없음**), 내 예측 사격(`StepWeapons`, 무기 정보 있음), `HitConfirmed`, `DamageTaken`(실드·체력 구분 없음, Snapshot의 Self 실드 값으로 추정 가능), `PlayerDied`·`PlayerDowned`(위치 없음), `BuildEvents`(Placed·Edited·Health·Destroyed — Destroyed는 id만이라 **붕괴와 파괴를 구분할 수 없고**, Sync도 같은 Placed 이벤트로 올라온다), `HarvestHit`(휘두른 사람에게만), 투사체 생성·폭발, 마스크로 오는 Container·문·Supply Drop 상태, `ZoneState`(축소 시작은 Client가 Tick으로 판단), UI 버튼은 모두 `UiFactory.CreateButton` 한 곳에서 만든다.
- **이동:** 원격 플레이어는 모드·질주 플래그와 보간 위치가 있다(속도·접지 플래그는 없다). 로컬 예측은 모드·질주·수평 속도가 있다. 발밑 재질: 지형, 맵 상자(재질 없음), 건설 조각(재질은 `BuildStore` 기록).
- **절차 생성 선례:** 메시·텍스처를 코드로 만든다. `AudioClip.Create` + `SetData`로 같은 방식이 가능하다.
- **QA:** `/qa/status`에 선택 필드를 덧붙이는 방식이 있다(`QaMapStatus`).

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 범위 | 요청서 STEP 8(§79–§85) |
| 진행 방식 | 2026-10-08 "페이즈마다 코드리뷰 하고 계획된 페이즈 전부 계속 진행". 추천안으로 진행하고 이유는 이 문서에 남긴다. |

## 결정과 추천 이유

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **소리 원본: 실행 중에 합성하는 짧은 클립(에셋 없음).** 시작할 때 `AudioClip.Create`로 종류별 클립을 한 번 만든다(잡음 버스트·감쇠 사인·필터한 잡음 조합, 결정적 시드). 소리 종류 → 클립 목록은 표 하나(`AudioCatalog`)에 모아, 나중에 실제 에셋으로 바꿀 때 그 표만 고친다. 종류마다 2–3개의 변형과 ±5 % 음높이 흔들기로 반복감을 줄인다. | 에셋 저작권·용량 문제가 없고 맵·메시처럼 코드로 재현된다. 정보 전달(방향·거리·종류 구분)이 목표라 합성음으로 충분하다. | 실제 에셋을 넣을 때 표만 바꾼다. |
| D2 | **재생 구조: 순수 모델 + 고정 풀.**<ul><li>`AudioEvent`(종류, 위치 또는 2D, 소스 id, 우선순위)를 Client 이벤트 처리기가 **고정 크기 큐(64)**에 넣는다. 넘치면 버리고 센다(무한 증가 방지).</li><li>순수 모델 `AudioMixerModel`이 프레임마다: 들을 수 있는 거리 밖은 버림 → 같은 소스·같은 종류의 최소 간격 안 중복은 버림 → 우선순위·거리로 정렬 → 예산 안에서 재생, 꽉 차면 더 낮은 우선순위·더 먼 소리를 끊고 바꾼다.</li><li>Unity 쪽 `AudioVoicePool`: AudioSource **24개 고정**(생성 때 한 번), 3D(spatialBlend 1, 선형 감쇠, 최대 거리는 종류별) 또는 2D(내 몸·UI).</li><li>프레임당 새 재생 최대 8개.</li></ul> | 요청서 §84 "무한히 생성하지 않는다, 동시 재생 예산". 판단 로직이 순수 모델이라 EditMode 테스트로 고정한다. | 예산은 상수 하나. |
| D3 | **우선순위(§79)와 들리는 거리(§82).** 총성 1(120 m, 저격·로켓 150 m), 발소리 2(25 m, 질주 30 m, 웅크림 12 m), 건설 3(40 m), 피해·실드 4(내 몸, 2D), Loot 5(15 m), 자기장 6(2D), UI 7(2D). 폭발은 총성과 같은 1(150 m). 거리 감쇠는 선형(Unity Rolloff Linear, 최대 거리에서 0). 먼 총성 별도 클립은 넣지 않는다(§82 "첫 구현은 단순하게"). | 요청서 우선순위 그대로. | 수치는 표만 바꾼다. |
| D4 | **원격 총성에 무기 구분: `ShotFired`에 무기 id 1바이트**(Protocol v17). 무기 종류별 총성(AR·SMG·SG·저격·권총). 내 사격은 2D에 가까운 가까운 소리로 예측 사격 때 바로(서버 확인을 기다리지 않음). 로켓 발사는 `ProjectileSpawned`(주인 ≠ 0)로, 수류탄 던지기는 생성 사건으로, 폭발은 `ProjectileExploded`로. | 무기 소리로 상대 장비를 알 수 있어야 정보가 된다. 1바이트라 비용이 작다. | 없음. |
| D5 | **발소리(§80): Client가 모든 플레이어에 대해 만든다.** 서버 패킷 없음.<ul><li>속도: 로컬은 예측 수평 속도, 원격은 보간 위치의 프레임 간 수평 이동.</li><li>간격: 걷기 0.5 s, 질주 0.33 s, 웅크림 0.7 s(작게), 미끄러지기는 시작할 때 한 번 미끄럼 소리. 공중(Freefall·Glide·Transport·Vault·기절 기어가기 제외: 기절은 0.9 s 끌림 소리) 모드와 0.5 m/s 미만은 소리 없음. 원격 Ground 모드에서 수직 이동이 크면(점프·낙하) 쉰다.</li><li>재질: 발밑 높이를 지형·맵 상자 윗면·건설 조각과 비교해 가장 가까운 것 — 지형 = Ground, 맵 상자 = Stone, 조각 = 그 기록의 재질(Wood·Stone·Metal). 네 가지만(§80 "너무 많은 Surface를 만들지 않는다").</li><li>계산은 들리는 거리(30 m) 안의 플레이어만, 플레이어마다 다음 발소리 시각 하나(고정 배열).</li><li>**순간이동 보호:** 등장·부활·Resume 뒤 첫 표본은 건너뛰고, 수평 속도 12 m/s 초과(질주+미끄럼보다 빠름)는 이동이 아니라 순간이동으로 보고 무시한다. Transport → Freefall은 공중이다(QA가 순간이동을 자주 쓴다).</li><li>발밑 재질 조회는 발소리를 낼 때만 한다(매 프레임 아님). 모두 Client 코드이고 Shared에 넣지 않는다.</li></ul> | 발소리는 이동 정보에서 결정적으로 만들 수 있어 패킷이 필요 없다. 거리 안에서만 계산해 비용을 묶는다. | 서버 확인이 필요해지면 다음에 이벤트를 더한다. |
| D6 | **건설 소리(§83).** Placed(Sync가 아닌 사건만 — `NetClient`가 사건 Placed와 Sync를 다른 이벤트로 올린다. **내 조각은 `BuildResult` Ok 때 이미 소리를 내므로 사건 Placed는 주인 ≠ 나일 때만**), Edited(**내 편집은 확정 예측 때 소리, Edited 기록은 주인 ≠ 나일 때만**), Health 감소(조각마다 0.15 s 간격), Destroyed, **Collapse**. 붕괴 구분을 위해 `BuildEvents`의 Destroyed 기록에 이유 바이트를 더한다(0 파괴, 1 붕괴, 5 B). 붕괴는 한 패킷에 여러 개여도 칸마다 한 번만 소리(가까운 것). | 요청서 §83 다섯 가지 구분. 붕괴는 서버만 알아 와이어에 실어야 한다. | 없음. |
| D7 | **채집 소리: 원격도 들리게 `WorldSound` 패킷(47, S→C, Unreliable).** 종류(채집 타격·채집 대상 파괴), 소스 Entity id, 위치. 서버가 휘두르기 결과 때 **소리 거리(30 m) 안의 다른 플레이어에게만** 보낸다(휘두른 사람은 지금처럼 `HarvestHit`). | 요청서 §81이 채집 방향을 중요하게 본다. 범위 안에만 보내 대역폭을 묶는다. | 다른 원격 행동 소리가 필요하면 종류를 더한다. |
| D8 | **피해·실드(§79-4).** `DamageTaken`에 플래그 1바이트(실드 맞음, 실드 깨짐)를 더한다(v17). Snapshot 실드 값으로 추정하면 `DamageTaken`(채널 0)과 Snapshot 사이에 순서 보장이 없어 가장 중요한 실드 깨짐을 잘못 분류한다. 실드 맞음·체력 맞음·실드 깨짐(2D). `HitConfirmed`는 맞힘 확인음(처치면 다른 소리). 내 기절·사망 소리. | 서버가 정확히 안다. | 없음. |
| D9 | **Loot·자기장·UI(§79-5–7).** 줍기 성공(`PickupResult` Ok), Container 열림(열림 마스크 비교, 3D), Supply Drop 착지(상태 변화, 3D 넓은 범위 150 m), 재투입 완료. 자기장: 축소 시작(Client가 서버 Tick이 `ShrinkStartTick`을 지나는 순간) 경고음, 밖에 있을 때 피해 틱. UI: `UiFactory.CreateButton`에 클릭음 한 곳, 지도 열기·닫기. 문 열림·닫힘(마스크 비교, 3D; **내 문은 예측 때 소리, 서버 마스크가 예측과 같으면 소리 없음**). 재장전 시작(내 무기).<br>**기준 상태 규칙(모든 상태 비교 소리):** Join·Resume·라운드 리셋·관심 칸 진입 뒤 처음 받은 상태는 기준일 뿐 변화가 아니다. 소리는 이미 그 상태를 가진 뒤에 관찰한 **변화**에만 낸다(Container 열림, 문, Supply Drop 착지, 자기장 축소 시작은 Client가 Tick이 지나는 것을 **직접 본** 경우만). 투사체 생성 소리는 `OwnerId == 0`이거나 `StartTick`이 지금 추정 서버 Tick보다 0.2 s 넘게 지났으면 내지 않는다(재전송).<br>**소리 출처 표(자기 vs 남, 중복 방지):** 내 사격 = 예측 사격(서버 `ShotFired`는 원래 내 것을 무시), 내 로켓 = 예측 사격만(내 `ProjectileSpawned` 무시), 수류탄 = `ProjectileSpawned`만(예측 없음), 내 설치 = `BuildResult` Ok, 내 편집 = 확정 예측, 내 문 = 예측. `audio_no_duplicate`가 이 표를 시험한다. | 이미 있는 상태 변화에서 꺼낸다. | 없음. |
| D10 | **설정.** 마스터 볼륨 상수 하나(ESC 메뉴 슬라이더는 넣지 않음), `AudioManager` Real voice 32는 풀 24보다 커서 그대로 둔다. | 범위를 지킨다. | 다음에 메뉴에 더한다. |
| D11 | **Protocol v17.** `ShotFired`에 무기 id(+1 B), `BuildEvents` Destroyed 기록에 이유(+1 B), `DamageTaken`에 플래그(+1 B), `WorldSound` 47. 일괄 점검: `PacketReader` 상한 47, 봇·HeadlessActor·QA 파싱(`ShotFired`·`DamageTaken`·Destroyed 크기), Fuzz, 서버 `ShotFired`를 만드는 모든 곳(산탄·Hitscan). **Destroyed 4 → 5 B:** `BuildEventsPacket.DestroyedSize`와 `TryReadHeader` 정확한 길이 검사, `BuildReplication.NextPacket`의 남은 자리 계산, Client `NetClient`·`BuildStore`, source link된 `BuildReplicationTests.ApplyToStore`, `_replication.Destroyed`를 부르는 모든 곳에 이유(피해 파괴, 붕괴, QA `damageBuild`, 편집으로 인한 붕괴). | 지난 Phase들의 누락 사고를 반복하지 않는다. | 없음. |
| D12 | **QA(§85).** 소리가 "좋은지"는 판단하지 않고 로직만 본다. `/qa/status`에 `audio`: 종류별 재생 수, 지금 재생 중인 소스 수, 예산 때문에 버린 수, 중복으로 버린 수, 거리 밖으로 버린 수. Unity 시나리오(`QA/Scenarios/Audio/`): `audio_gunshot`(가까운 원격 사격 → 총성 3D 재생, 먼 사격 → 거리 밖), `audio_footstep`(걷는 원격 → 발소리, 공중 → 없음, 조각 위 → 그 재질), `audio_build`(설치·편집·피해·파괴·붕괴 각각), `audio_budget`(사격 폭주 → 재생 ≤ 24, 버림 > 0), `audio_no_duplicate`(사건 하나 = 소리 하나). 서버: `ShotFired` 무기 id, Destroyed 이유, `WorldSound` 범위 테스트. 청각 품질은 `QA/Scenarios/Manual/audio_listen.json`(사람 확인 목록). | 요청서 §85 그대로. | 없음. |
| D13 | **지금 넣지 않는 것.** 실제 오디오 에셋, 가림(Occlusion)·잔향, BGM, 음성, 원격 재장전·발걸음 서버 이벤트, 볼륨 메뉴. | 범위를 지킨다. | 다음에 더한다. |

## 검증 계획

- 클립 합성은 순수 `float[]` 함수로 만들고 `AudioClip.Create`로 감싸, "결정적이고 무음이 아님"을 EditMode에서 Unity 오디오 API 없이 시험한다.
- Client EditMode: `AudioMixerModel`(큐 상한·넘침 셈, 거리 버림, 중복 간격, 우선순위·거리 정렬, 예산 교체, 프레임당 상한), 발소리 판단(모드·속도·간격·공중·재질 선택), 클립 합성(길이·결정성·무음 아님), 이벤트 → 소리 종류 매핑(사건 Placed vs Sync, 붕괴, 실드/체력, 로켓 OwnerId 0은 소리 없음).
- Shared·Server: 패킷 왕복·Fuzz, `ShotFired` 무기 id(산탄은 한 번), Destroyed 이유(붕괴 = 1), `WorldSound` 범위(30 m 안 다른 플레이어만).
- QA: D12 시나리오, `suite:smoke`, `suite:pre-push`, `suite:weapons`, `suite:building`, `suite:squad`, `suite:map`, `suite:loot`, `stress-quick`, Unity 스위트.
