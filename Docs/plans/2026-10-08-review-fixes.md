# 리뷰 지적 수정 — 구현 기록 (Spec과 다른 점)

설계: `Docs/specs/2026-10-08-review-fixes-design.md`(A1–A6, B1–B5, C1–C7, D1–D4). 원 지적: 2026-10-08 전체 코드리뷰 보고서(`_workspace_review/06_full_report.md`, 저장소 밖)의 SEC-1~26·STB-0~2. 동작 설명은 `Docs/Networking.md`(접속 순서·인증·파서 범위), `Docs/Server.md`(옵션·스레드·Health), `Docs/Movement.md`(바깥벽 경계), `Docs/Weapons.md`(equipSeconds), `Docs/Database.md`(schema_version), `Docs/Client.md`(접속 인증·받기 검증)에 있다. 이 문서에는 구현하면서 Spec에 없던 것을 정했거나 Spec과 다르게 한 것, 리뷰에서 잡혀 고친 것, 확인하지 못한 것만 적는다.

브랜치 `review-fixes`. 묶음마다 구현 → 리뷰(safety + 관련 점검 키) → 수정 → 재검토 → 빌드·테스트 → Commit 순서로 했다.

| 묶음 | Commit | 리뷰 확정 → 수정 |
|---|---|---|
| A 네트워크 진입 | ee7b65a | 2건 |
| B 신원·무결성 | a74cf24 | 5건(+ 리더 판단 2건) |
| C 경기 공정성 | 7b40f88 | 2건 |
| D 상태 정확성 | (이 문서와 같은 Commit) | 3건(모두 Low) |

## 묶음 A — 네트워크 진입

| # | 내용 | 이유 |
|---|---|---|
| A-S1 | 버전 검사를 쿠키보다 먼저 하고, 거절은 모두 `RejectForce`(임시 peer 없음)다. 신뢰 `Reject`는 쓰지 않는다. | 버전은 모든 배치의 첫 필드라 v18 Client에 `BadRequest`가 아니라 `VersionMismatch`를 보여 줄 수 있다. 1 B 답이라 쿠키 답과 비용이 같다. |
| A-S2 | 틀린 쿠키에도 새 쿠키를 돌려준다. Client·봇의 재시도는 1회다. | 낡은 쿠키를 가진 Client가 회복할 수 있고 고리가 생기지 않는다. |
| A-S3 | Seq 창은 고정 64가 아니라 `ServerOptions.InputSeqWindow = max(64, SimHz × (DisconnectTimeoutMs + 1000) / 1000)`(기본 180 Tick)다. | Client와 봇은 패킷이 사라지는 동안에도 Tick마다 Seq를 올린다. 64면 2–5 s 끊김 뒤 모든 입력이 영영 버려진다. 창은 "끊기지 않는 가장 긴 끊김 + 1 s"다. |
| A-S4 | `EnqueueInput`은 그 연결의 플레이어가 없으면 true다. 거절은 "플레이어가 있고 모두 거절"일 때만이다. | 관전자가 InputTimeout으로 끊기지 않게 한다. |
| A-S5 | 실패 출처를 모르는 경우(-1, 유예 중 플레이어)는 실패마다 다른 출처로 센다. | 기존 예외 복구 테스트가 연결 없이 실패를 만든다. 공격자가 자기 유예 캐릭터를 실패시키는 경로는 찾지 못했다. |
| A-S6 | 카운터 `penalized`·`cookieChallenges`를 더했다. | 벌점 거절과 정상 첫 요청을 거절·perIp와 구분한다. |
| A-S7 | 봇 `SendRaw`와 QA `oversized`는 `TooBigPacketException`을 잡는다. | 보내는 쪽 `MaxFragmentsCount = 2`면 LiteNetLib가 3조각 이상을 던진다. |
| A-C1 | 쿠키 재시도는 이름이 아니라 거절한 peer의 IP:port로 보낸다. | 쿠키가 IP·port에 묶여 있어 DNS가 다른 IP를 주면 맞지 않는다. |
| A-R1 | **리뷰에서 고침:** IP별 `Active` 수를 `Interlocked`로 바꿨다. | 수신 스레드와 끊김 콜백이 겹칠 수 있다. |

## 묶음 B — 신원·무결성

| # | 내용 | 이유 |
|---|---|---|
| B-S1 | 모든 데이터그램에 20 B 꼬리(counter u32 + HMAC 16)를 단다. 키가 없으면 0 꼬리를 보내고, Client는 첫 검증 성공 전까지만 검증 없이 벗긴다. `MtuOverride = ProtocolLimits.UserMtu`(1212). | 쿠키 `RejectForce`는 서명 없이 온다. 조각·Ping·Ack까지 보호된다. |
| B-S2 | RSA 블록은 모든 연결 요청에 넣는다(256 B). 복호 실패는 `RejectForce(BadRequest)`다. | 요청 형식이 하나뿐이면 파서가 단순하다. |
| B-S3 | Resume 증명 = HMAC(옛 Resume 키, nonce ‖ **새 세션 키** ‖ 이름)[0..16]. 옛 키(`PrevResumeKey`)는 첫 입력이 받아들여질 때까지 둔다. | 증명이 새 연결에 묶여 재사용되지 않는다. 증명이 든 요청이 유실돼도 다시 시도할 수 있다. |
| B-S4 | `FindGraced`의 "같은 이름으로 연결된 플레이어가 있으면 찾지 않음" 규칙을 없앴다. 증명이 맞으면 연결된 플레이어도 넘겨받고, 그때 죽어 있으면 `PlayerDied`를 다시 보낸다. | 그 규칙은 이름을 먼저 차지한 사람이 주인의 Resume을 막는 수단만 남긴다. |
| B-S5 | 세션 키는 지우지 않고 은퇴시켰다가 DisconnectTimeout + 1 s 뒤 비운다. 은퇴 상한 `MaxPlayers + AcceptBurst + AcceptsPerSecond × ⌈RetireMs/1000⌉`(152). | 끊김 직후 늦게 온 데이터그램이 0 꼬리로 통과하지 않게 한다. |
| B-S6 | `NetPeer`는 `IPEndPoint`를 상속하지만 `GetHashCode`를 덮어쓴다. 키 표에 주소·포트 비교자를 달았다. | 기본 비교자로는 송신 콜백의 `NetPeer`로 키를 찾지 못해 서버 송신이 모두 0 꼬리로 나갔다. 실제 `NetManager` 테스트가 잡았다. |
| B-S7 | 개발용 RSA 키 쌍을 `Server/src/ProjectH.Server/keys/`에 둔다. Production에서 개발 키면 시작 실패다. 개발 서버는 `--environment Development`로 띄운다. | Spec B1. |
| B-C1 | 검증 표시는 bool이 아니라 "검증된 키 객체"다. | 재접속으로 키가 바뀌면 자동으로 미검증 상태가 된다. |
| B-C2 | 공개키는 `Resources/ServerPublicKey.txt`에서 한 번 읽는다. 읽기 실패면 Connect가 그 이유로 실패하고 개발 키로 대신하지 않는다. | 핀 고정의 목적. |
| B-R1 | **리뷰에서 고침:** Resume 때 Resume 키를 돌리면서 옛 키를 잃어 캐릭터를 못 찾던 경로, 죽은 캐릭터 인수 때 `PlayerDied` 누락, 은퇴한 키의 통과, 복호 실패 벌점(60 s에 3회째부터), 은퇴 표 상한. 리더 판단으로 `authDropsRetired`를 `authDrops`와 분리했다. | 재검토 5건 해소, 신규 0. |

## 묶음 C — 경기 공정성

| # | 내용 | 이유 |
|---|---|---|
| C-S1 | 시드 섞기는 `Mix(secret ^ ((ulong)salt << 32) ^ round)`다. | Spec의 `secret ^ salt ^ round`는 salt와 Round가 같은 비트에 겹친다(1^2 = 2^1). |
| C-S2 | 되감기 허용 = clamp(RttMs × SimHz / **1000** + 2 × SnapshotEveryTicks + 2, 2, MaxRewind). 20 ms → 6, 100 ms → 9, 200 ms → 12 Tick. | LiteNetLib `RoundTripTime`은 왕복 전체다. ViewTick은 보간 4 Tick + 왕복 1회 + Drain 1 Tick만큼 뒤처지므로 RTT 전체를 센다(Spec은 /2000). |
| C-S3 | `_movementAnomaly`는 `Action<ushort>`(Entity id)다. 플레이어별 수는 `PlayerEntity.MovementAnomalies`, 합계는 Health 그대로다. | Spec의 `HealthCounters.AddMovementAnomaly(ushort)`는 받은 id를 쓸 곳이 없다. |
| C-S4 | 테스트 무기 카탈로그와 Phase 17 테스트 데이터는 `equipSeconds = 0`이다. 교체 대기는 운영 값(0.4 s)과 전용 테스트로 본다. | 기존 테스트는 Slot과 Fire를 한 입력에 넣어 바로 쏘는 동작을 본다. |
| C-S5 | QA Actor는 칸 변경 뒤 `2 + EquipTicks` Tick을 기다린 뒤 쏜다. | 리뷰: 대기가 교체 지연보다 짧아 QA 사격 시나리오가 깨졌다. |
| C-C1 | 서버가 든 칸이 예측과 다르면(빈손에서 첫 빈 칸으로 줍기 등) 그 무기가 ack Step에 손에 들어온 것으로 본다(`local.Step + EquipTicks`). | 예측과 서버의 교체 대기가 같은 Step에서 끝나야 한다. 서버 비교 테스트 3개가 같은 입력열로 고정한다. |
| C-R1 | **리뷰에서 고침:** 조준 회전 기준을 `LastInput`과 분리했다(`PlayerEntity.LastAimYaw`·`HasAimBaseline`, `Match.TrackAim`). Respawn·인수·끊김·입력 공백(0.5 s)·Resume 뒤 첫 입력은 기준만 잡는다. NaN 입력은 기준을 바꾸지 않아 A → NaN → B가 A → B로 측정된다. | Respawn이 `LastInput.AimYaw`를 0으로 두어 경기 첫 입력에서 모든 참가자의 `max_aim_turn`이 부풀려졌다. 기존 테스트가 값을 손으로 0으로 덮어 가리고 있었다(그 대입을 뺐다). |
| C-R2 | **리뷰에서 고침:** 자원 자동 줍기는 "담을 자리 없음"을 시선 검사보다 먼저 본다. | 꽉 찬 플레이어가 3 Tick마다 전체 광선 검사를 하고 결과를 버렸다. |

## 묶음 D — 상태 정확성

| # | 내용 | 이유 |
|---|---|---|
| D-S1 | `JoinMatchResponse`의 id 0 거절은 `Ok`·`Resumed`에만 적용한다. `Result > Resumed`는 거절한다. | 서버는 `MatchFull`에 id 0을 보낸다. Spec대로면 Client가 "꽉 참"을 못 받는다. |
| D-S2 | `weapons.json` 검사에 `speed + gravity × lifetimeSeconds ≤ 200`을 더했다. 운영 값은 수류탄 47.4, 로켓 40이다. | 낙하로 빨라진 투사체의 `ProjectileState` 속도가 Client 한계 200을 넘으면 안 된다. |
| D-S3 | ViewTick 0은 "가장 오래된 주장"(허용 밖이면 자르고 `RewindClamped`로 셈), `uint.MaxValue`는 "지금"(최신 Tick으로 자르고 세지 않음)이다. 봇과 QA Actor는 첫 Snapshot 전에 0을 보내므로 봇 경기 기록의 `rewind_clamped`가 0보다 클 수 있다. | 기존 float 사례는 NaN·+∞ → `uint.MaxValue`, −∞·음수 → 0, 97.5 → 97로 옮겼다. |
| D-S4 | 지상 경계 때문에 기존 테스트 3개의 시작 위치를 맵 안으로 옮겼다(슬라이드 속도 x −90 → −75, 수송기 탑승자 사격은 중심 쪽 30 m, 모서리 틈은 안쪽에서 나가는 경우만). 단언은 그대로다. | 바깥에 설 수 있는 경로가 없어졌다. |
| D-C1 | 큰 Tick 거부 창은 `SimHz × (10 s + 마지막 Tick 뒤 지난 로컬 초)`다. 같은 순간의 큰 Tick 위조는 그대로 거부된다. | 서버는 근처에 차량이 없으면 `VehicleStates`를 보내지 않는다. 고정 10 s면 차량에서 멀어졌다 돌아온 플레이어의 패킷이 계속 거부된다. |
| D-C2 | 시계가 거부한 Snapshot은 통째로 버린다. | 시계만 안 올리면 그 Tick이 원격 보간기와 예측 교정에 그대로 들어간다. |
| D-C3 | id 0이나 참가 전의 내 id로 온 Spawn은 원격 플레이어로도 만들지 않는다. 상한 상수는 `ProtocolConstants.MaxSnapshotEntities`(100)다. | — |
| D-C4 | ViewTick 변환은 `ServerClock.ToViewTick(double)`이다. NaN·음수·∞·범위 밖은 `uint.MaxValue`("지금"), 정상 값은 소수점을 버린다. | `(uint)Math.Max(0, x)`는 NaN에서 결과가 정의되지 않는다. |
| D-C5 | 조각 뷰 갱신은 static `GameClient.ApplyPieceChanges`로 뺐고, 예측기가 있을 때와 없을 때 둘 다 이 함수를 부른다. | EditMode에서 호출 경로를 테스트한다. |
| D-C6 | 새 헬퍼 `UnityObjects.Destroy`(Play 중 `Destroy`, 아니면 `DestroyImmediate`)를 `PlayerView`·`PlayerViewFactory`·`BuildPieceViews`·`PieceMeshes`에 적용했다. Play 중 동작은 그대로다. | Edit 모드에서 `Object.Destroy`는 오류 로그만 남겨 테스트를 실패시키고 실제로 지우지 않는다. |
| D-C7 | `VehicleStore.Reset` 바로 뒤의 첫 패킷과 시계의 첫 표본은 비교할 기준이 없어 그대로 받는다. | 경로 위 위조는 묶음 B의 인증이 막는다. |
| D-C8 | `ServerClock.cs`를 서버 테스트 프로젝트에 소스 링크했다(컴파일 확인). `ServerClockTests.cs`는 NUnit이라 링크하지 못했다(서버 테스트는 xUnit). | — |
| D-R1 | **리뷰에서 고침(Low 3건):** (1) `StepGround`가 바닥 따라가기·Y Sweep 뒤에야 경계를 당겨, 맵 끝 칸의 2층 이상 경사로에서 발이 판 안으로 들어간 위치가 기록됐다 → `StepAir`처럼 수평 Sweep 직후에 당기고 끝의 호출은 안전망으로 남김(테스트: 40 Tick 미는 동안 매 Tick 발 높이 ≥ 판 높이). (2) `ProtocolLimits` 주석의 "서버는 Client가 거절할 값을 보내지 않는다"가 투사체 위치 ±512에는 성립하지 않았다 → `ValidateProjectile`에 비행 거리 검사를 더함. 거리 상한은 `speed × lifetime + ½ × gravity × lifetime²`다(재검토 지적: 튕김은 속력을 늘리지 않지만 낙하 속력을 옆으로 돌릴 수 있어 `speed × lifetime`만으로는 상한이 아니다). 최고 발사 높이 상수는 지형 최고 + 최상층 지붕 + 눈높이(약 57 m, 실제 최고 약 52 m). 운영 값은 로켓 160 m, 수류탄 98 m. (3) `TryStartVault` 머리 주석에 맵 경계 조건 추가. | 재검토: 3건 해소(2번은 2차에서 상한 식을 고침), 신규 0. |

## 해소 표

보고서 번호별 상태. "범위 밖"은 Spec "범위 밖(이유)" 절과 같다(다음 Spec E 묶음).

| 번호 | 등급 | 내용 | 상태 | 어디서 |
|---|---|---|---|---|
| SEC-1 | Critical | 조각 재조립 상한·C→S 크기 상한 | 해소 | A1 (ee7b65a) |
| SEC-2 | High | 인증 없이 유예 캐릭터 Resume | 해소 | B4 Resume 증명 (a74cf24) |
| SEC-3 | High | IP당 동시 연결·전역 수락 상한 없음 | 해소 | A2 |
| SEC-4 | High | 위조 출발지로 슬롯·Bucket 소모 | 해소 | A3 쿠키 |
| SEC-5 | High | 되감기 Tick을 RTT와 무관하게 선택 | 해소 | C3 (7b40f88) |
| SEC-6 | High | 고정 시드 + Round 전송 | 해소 | C1 |
| SEC-7 | Medium | 변조·재전송 방지 없음, Seq 앞쪽 상한 없음 | 해소 | B3 HMAC 꼬리 + A4 Seq 창 |
| SEC-8 | Medium | 칸 교체로 발사 간격 우회 | 해소 | C2 |
| SEC-9 | Medium | 줍기 시선 검사 없음 | 해소 | C4 |
| SEC-10 | Medium | Anti-cheat 기록 없음 | 해소 | C7 (match_player 8열, schema v2) |
| SEC-11 | Medium | 퍼짐 시드 예측 가능 | 해소 | C1 비밀을 퍼짐 해시에 섞음 |
| SEC-12 | Medium | QA HTTP Production 판정·AllowRemote 인증 | 범위 밖 | E 묶음 |
| SEC-13 | Medium | 투사체 소유자별 상한 없음 | 해소 | C5 |
| SEC-14 | Medium | 출처 구분 없는 실패 집계 → 리셋·종료 | 해소 | A6 |
| SEC-15 | Medium | 관심 영역 없음(ESP·O(N²)) | 범위 밖 | 설계 결정·측정 선행 |
| SEC-16 | Low | DB 평문 자격 증명 | 범위 밖 | E 묶음 |
| SEC-17 | Low | SslMode 없음 | 범위 밖 | E 묶음 |
| SEC-18 | Low | 공유 채널 용량 < burst 합 | 해소 | A5 |
| SEC-19 | Low | AimYaw·Yaw 차이 무제한 | 기록만 | C7 `max_aim_turn`(각도 제한은 정상 입력을 깨서 범위 밖) |
| SEC-20 | Low | 버리기·줍기 빈도 상한 없음 | 해소 | C6 |
| SEC-21 | Low | QA HTTP Host·Origin·Content-Type | 범위 밖 | E 묶음 |
| SEC-22 | Low | 볼륨형 DDoS 인프라 | 범위 밖 | 배포 문서 |
| SEC-23 | Low | QA 조작 경기 저장 | 범위 밖 | E 묶음 |
| SEC-24 | Low | Client 소비자 검증 공백 4건 | 해소 | D3 |
| SEC-25 | Medium | Join 전·id 0·비유한 Spawn | 해소 | D3 `IsMySpawn`·파서 |
| SEC-26 | Medium | 큰 ServerTick 고정 | 해소 | D3 Tick 창 |
| STB-0 | High | 지상·Vault 맵 경계 없음 | 해소 | D1 |
| STB-1 | Medium | ViewTick float 정밀도 | 해소 | D2 |
| STB-2 | Medium | 내 Spawn 전 조각 뷰 없음 | 해소 | D4 |
| STB-3~18 | — | 안정성 Medium·Low 나머지 | 범위 밖 | 다음 Spec |

## 검증 기록

- 묶음마다 game-reviewer 워크플로(safety + 관련 키)로 리뷰하고, 확정 지적을 고친 뒤 새 리뷰어가 재검토했다. 재검토는 네 묶음 모두 "해소, 신규 없음"이다.
- 리더 빌드·테스트(마지막): `dotnet build` 경고 0. Server.Tests와 QA.Tests 결과는 아래 "측정" 절의 최종 실행 수치다.
- 서버 쪽 테스트 프로젝트가 Client 파일 `NetClient.cs`·`AuthPacketLayer.cs`·`WeaponState.cs`·`ServerClock.cs`를 소스 링크해 컴파일을 확인하고, `WeaponState`는 서버 규칙과 같은 입력열로 비교 테스트한다.

## 측정

묶음 D Commit(39ff83e) 뒤 QA 도구로 돌렸다. QA 도구는 서버를 `DOTNET_ENVIRONMENT=Development`로 띄우므로 개발 키·쿠키·HMAC 경로를 그대로 지난다.

| Suite | 결과 | Baseline 비교 |
|---|---|---|
| `smoke` | 3/3 PASS | 2026-10-08 실행 대비 50 % 넘게 나빠진 지표 없음 |
| `pre-push` | 6/6 PASS | 같음 |
| `weapons` | 7/7 PASS | 같음 |
| `building` | 9/9 PASS | 같음 |
| `squad`(Resume 포함) | 8/8 PASS | 같음 |
| `vehicle` | 8/8 PASS | 같음 |

- 접속·재접속 시나리오가 통과했으므로 쿠키 1회 왕복, RSA 세션 키, 데이터그램 HMAC, Resume 증명이 정상 흐름에서 동작한다(한 QA Actor는 서버 테스트의 HeadlessClient와 같은 구현이다).
- 무기 Suite는 교체 지연(0.4 s)을 포함해 통과했다(QA Actor가 칸 변경 뒤 `2 + EquipTicks`를 기다린다).

`stress-quick`(봇 50명, baseline·movement·combat·building·mixed, steady 30 s): 5/5 PASS.

- 정상 흐름 카운터는 다섯 실행 모두 0이다: `authDrops`, `authDropsRetired`, `inputSeqDrops`, `rejects cookie`, `perIp`, `penalized`, `badRequest`, `malformed`, `penalties`. 연결 50(mixed는 56)이 모두 쿠키 요청 1회(`cookieChallenges` = 연결 수)를 거쳐 들어왔다.
- Baseline Warning(실패 아님): `server.tickP50Ms` 0.010–0.011 → 0.016–0.019 ms(+51–85 %, 다섯 실행 중 넷), movement `stress.tickP99Ms`(steady) 0.171 → 0.284 ms(+66 %), combat `stress.managedMB`(steady) 7.4 → 11.3 MB(+53 %). 기준선은 2026-10-08 Phase 19 실행이다.
- 같은 Suite를 한 번 더 돌렸다(5/5 PASS, 카운터 모두 0). 도구는 직전 실행과 비교하므로 Warning이 없었지만, 2026-10-08 기준선과 직접 비교하면 결과가 반복된다. steady 구간: movement p50 0.108 → 0.134 / 0.137 ms(+24–27 %), p99 0.171 → 0.284 / 0.289 ms(+66–69 %); combat p50 0.114 → 0.140 / 0.146 ms, p99 0.604 → 0.648 / 0.690 ms(+7–14 %). **잡음이 아니라 재현되는 비용이다.**
- 해석: 수신 HMAC은 수신 스레드에서 돌지만, Snapshot·이벤트 송신의 봉인(데이터그램마다 HMAC-SHA256, 1.2 KB 기준 수 µs)은 Game Loop 스레드에서 돈다. 50명 × 15 Hz Snapshot ≈ Tick당 25회 봉인이면 Tick당 약 0.1 ms가 더해지고, Snapshot을 보내는 Tick에 몰리므로 p99에 더 크게 나타난다. Tick마다 RTT 갱신·카운터·ClampToMap은 그보다 훨씬 작다. 관리 메모리 +4 MB는 세션 키 표(연결당 키 2개와 은퇴 항목)·쿠키·RSA 버퍼다.
- 판정: Spec 검증 계획의 "Tick p99 기준선 대비 50 % 넘게 나빠지지 않음"을 movement에서 넘었다(비율 기준). 절대값은 p99 0.29 ms로 Tick 예산 33 ms의 1 % 아래이고, 데이터그램마다 서명하기로 한 설계(B3)의 직접 비용이다. 그대로 두고 여기 적는다. 줄이려면 봉인을 송신 스레드로 옮기거나(LiteNetLib PacketLayer 구조상 호출 스레드에서 돌아 어렵다) 100명 기준으로 다시 재서 설계를 다시 본다. 100명 `stress`(bots_50·load_bots_50)는 이번에 돌리지 않았다.

## 간헐 실패

- 할당 측정 테스트가 전체 실행에서 가끔 한 번 실패하고 다시 돌리면 통과한다: 묶음 A 때 `BotBuilderTests.ATick_AllocatesNothing`·`TerrainTraceTests.TraceWorld_OnTerrain_AllocatesNothing`, 묶음 D 때 `LootContainerTests.LootTicks_AllocateNothing`(단독 3회·전체 재실행 모두 통과). 해당 묶음은 그 경로를 바꾸지 않았다. Phase 17·19 기록과 같은 종류(병렬 부하에서의 첫 호출 JIT 할당)로 본다.
- `HardeningIntegrationTests.TheThirdBlobThatDoesNotDecrypt_PenalizesTheSource`(묶음 B에서 추가, 실제 UDP 소켓과 3 s 대기)가 묶음 D 마지막 전체 실행에서 한 번 실패했다. 단독 3회·전체 재실행 1회 모두 통과했고, 그 실행의 변경은 `WeaponCatalog` 검증뿐이다. 병렬 부하에서의 시간 의존으로 보며 원인은 확인하지 못했다.

## 확인하지 못한 것

- **Unity Editor 컴파일과 EditMode 실행.** 여기서는 Unity를 돌리지 못했다. Client 코드는 서버 테스트 프로젝트의 소스 링크와 Unity 모듈 dll로의 scratch 컴파일(UGUI·InputSystem 형식 제외)로만 확인했다. Mono의 `RSA.FromXmlString`·OAEP-SHA1·`HMACSHA256.TryComputeHash` 동작도 실제 기기에서 확인해야 한다.
- 묶음 D의 새 Client 파일 5개(`Scripts/Game/UnityObjects.cs`, `Tests/EditMode/ServerClockTests.cs`·`GameClientSpawnTests.cs`·`RemotePlayersTests.cs`·`BuildPieceViewsTests.cs`)는 `.meta`가 없다. Unity가 만든다.
- MySQL `schema_version` v2 마이그레이션 테스트는 DB가 없어 건너뛰었다(`MySqlFact`). 개발 DB에서 한 번 돌리면 `match_player`에 열 8개가 생긴다.
- 조각 2개짜리 미완성 메시지의 fragment id 보류 수 상한은 LiteNetLib 소스로 확인하지 못했다(SEC-1 원래 "확실하지 않음").
- NAT가 1차·2차 연결 요청 사이에 포트를 바꾸면 쿠키가 맞지 않는다(새 쿠키로 1회 재시도까지). 실망 측정은 없다.
- Codex 2차 검증(전체 리뷰 5단계)은 사용량 한도로 아직 실행하지 않았다.
- 하네스 확인 항목(`game-core-rules` 4절 Shared 예외에 SessionAuth·ConnectRequestData 인증 블록·ProtocolLimits·DevServerPublicKey 추가, Server.md "Lock 0개" 문구)은 사용자 소유 하네스 파일이라 고치지 않고 여기 적어 둔다. Server.md 문구는 묶음 B에서 "leaf lock 1개(AuthPacketLayer 송신)"로 고쳤다.
