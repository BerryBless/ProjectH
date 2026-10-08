# 리뷰 지적 수정 (A–D) — 설계 Spec

## Context

2026-10-08 전체 코드리뷰(`_workspace_review/06_full_report.md`, 기준 69a3f63, 공개 서버 기준)가 보안 26건·안정성 17건·반응성 10건을 보고했다. 이 Spec은 그중 코드로 닫을 묶음 네 개(A 네트워크 진입, B 신원·무결성, C 경기 공정성, D 상태 정확성)를 설계한다. 운영·Low(E)와 관심 영역·외부 인증·반응성 측정 항목은 범위 밖이다.

**지금 구조에서 확인한 사실(69a3f63 기준):**

- **LiteNetLib 2.1.4 `PacketLayerBase`는 데이터그램 단위 훅이다.** `ProcessInboundPacket(ref IPEndPoint, ref byte[], ref int length)`, `ProcessOutBoundPacket(ref IPEndPoint, ref byte[], ref int offset, ref int length)`, 생성자 인자 `extraPacketSizeForLayer`. peer 객체가 아니라 endpoint만 받는다. 연결 요청·Ping·Ack 같은 라이브러리 내부 패킷도 모두 지난다. 제공 계층은 Crc32c·XorEncrypt뿐이다. `NetManager.MaxFragmentsCount` 기본값은 65535다(패키지를 로드해 확인).
- **연결 수락.** `NetworkListener.HandleConnectionRequest`(NetworkListener.cs:158-214)는 꽉 참 → IP 빈도 Token Bucket(`ConnectRateLimiter`, 1024칸 고정 해시 표) → `ConnectRequestData.TryRead`(version u16 + DevPlayerId) → 버전 → `Accept` 순서다. 거절은 모두 `Reject(byte[])`(신뢰, 임시 peer)이고 `RejectForce`는 쓰지 않는다. `NetPeer.RoundTripTime`·`Ping`을 읽는 곳이 없다.
- **입력 버퍼.** `PlayerInputBuffer.Add`는 `Seq <= LastTakenSeq`만 거른다. `GameLoop.cs:577`은 패킷을 받기만 하면 `LastInputTick`을 갱신한다.
- **전투.** `HeldWeapon.NextFireTick`은 칸별이고 `WeaponRules.SelectSlot`에 교체 지연이 없다. `CombatRules.ClampViewTick(float viewTick, uint latestTick, int maxRewindTicks)`는 고정 창 12 Tick. `WeaponSpread.Spread(aim, half, shooterId, tick, ray)`의 해시 입력에 비밀이 없다. `InputCommand.ViewTick`은 float.
- **시드.** `ServerOptions.LootSeed/ZoneSeed/SpawnSeed` 기본 1, `Match`가 `seed + Round`로 `new Random`(SafeZone.cs:73, Match.cs:2192-2223, Match.Loot.cs:142-145). Round는 `MatchState`로 전송된다.
- **기록.** `PlayerRecord(DevPlayerId, Placement, Kills, Damage, SurvivalMs)`. `match_player` 열은 placement·kills·damage·survival_ms. 스키마는 `CREATE TABLE IF NOT EXISTS`뿐이고 버전이 없다.
- **이동 경계.** `MovementSimulation` StepAir만 `bound = HalfSize − HalfWidth − Skin`으로 자른다(576-586). StepGround(300)·StepVault·TryStartVault는 자르지 않는다.
- **Client.** `ServerClock.OnSnapshot`은 LatestTick을 최댓값으로만, `VehicleStore.Apply`는 `serverTick <= _lastTick`이면 버린다. `GameClient.cs:668`은 예측기가 없으면 `ClearChanged`만 한다. `NetClient`는 `new NetManager(this, null)`.
- **암호 API 교집합(.NET 10 / Unity Mono netstandard2.1):** HMACSHA256, RSA(OAEP-SHA256), RandomNumberGenerator. AES-GCM·ChaCha20·X25519는 Unity에 없다.

## 사용자가 고른 것

| 질문 | 선택 |
|---|---|
| 범위 | A–D 한 Spec, 구현 계획은 묶음별(A→B→C→D). E·관심 영역·외부 인증은 뒤에 |
| 신원·무결성 깊이 | 서버 RSA 공개키 핀 + Client 생성 세션 키 + 모든 데이터그램 HMAC. 암호화(기밀성)는 넣지 않음 |
| 진행 | "추천대로", 완료 후 푸시. 추천안과 이유는 이 문서에 남긴다 |

## 결정과 추천 이유

### 0. 공통

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D0-1 | **ProtocolVersion 18 → 19 한 번.** 바뀌는 것: `ConnectRequestData`(쿠키·세션 키·Resume 증명), `InputCommand.ViewTick` uint, 모든 데이터그램 꼬리 20 B(AuthLayer) | 세 묶음을 따로 올리면 Client·봇·QA를 세 번 맞춰야 한다 | A·B가 끝나기 전에는 Client가 접속하지 못한다. 한 브랜치에서 A→B를 연속으로 한다 |
| D0-2 | **암호 기본 요소는 HMAC-SHA256, RSA-OAEP(SHA-256, 2048), RandomNumberGenerator만.** | 양쪽 런타임에 다 있다 | 기밀성이 필요해지면 AES-CTR+HMAC를 따로 설계한다 |
| D0-3 | **서버 Lock은 여전히 0개.** 새 스레드 경계는 AuthLayer 키 표 `ConcurrentDictionary<IPEndPoint, SessionKeys>` 하나. Server.md 스레드 표에 적는다 | 수신 스레드(검증)와 Game Loop(송신)가 같이 읽는다. Lock 대신 Concurrent Collection 한 곳 | 경합이 측정되면 endpoint 해시 고정 표로 바꾼다 |
| D0-4 | **TDD.** 지적마다 재현 테스트를 먼저 쓰고, 기존 테스트를 지우거나 약화하지 않는다 | 서버 리뷰 하네스 규칙 | — |
| D0-5 | **봇·QA 도구(HeadlessClient)는 Client와 같은 연결 흐름·계층을 쓴다.** | 봇만 통과하는 경로를 만들지 않는다(체크리스트 73) | 봇 코드 변경량이 는다 |
| D0-6 | **함수 주석 규칙(`code-comments`)을 추가·수정 함수에 적용한다.** | 항상 적용 | — |

### A. 네트워크 진입 (SEC-1·3·4·7단기·14·18)

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| A1 | **조각·크기 상한(SEC-1).** 서버·Client·봇 `NetManager.MaxFragmentsCount = 2`. 수신(`NetworkListener.Receive`)에서 `AvailableBytes > ProtocolConstants.MaxClientPacketBytes`(128)면 `Malformed`. `PlayerInputPacket.TryRead`는 `Remaining == count × CommandSize` 정확 비교, `JoinMatchRequest`는 본문 0 B 요구. `PacketTests`가 "모든 S→C 패킷 최대 크기 + 전송 헤더 4 + AuthLayer 20 ≤ MTU 1232"를 고정 | 앱 패킷은 모두 1200 B 이하라 조각이 생기지 않는다. 2는 헤더 계산 오차 여유 | 어떤 패킷이 1212 B를 넘으면 테스트가 막는다 |
| A2 | **IP당 동시 연결 수(SEC-3).** `ConnectRateLimiter` 칸에 `Active`(int)와 `PenaltyUntilMs`(A6)를 더한다. `TryAcquire` 뒤 `Active >= MaxConnectionsPerIp`면 거절. Accept 직후 `Active++`, `OnPeerDisconnected`에서 `Release(address)`로 `Active--`(둘 다 수신 스레드). 설정 `Server:MaxConnectionsPerIp` 기본 4(0 = 끔, 부하 테스트는 200). **전역 수락 Token Bucket**: `AcceptBurst = MaxPlayers`, `AcceptsPerSecond`(기본 20, 설정). Control 채널 용량 = 3 × (MaxPlayers + AcceptBurst + ⌈AcceptsPerSecond / SimHz⌉). 해시 키에 시작 때 `RandomNumberGenerator`로 만든 `uint _hashSalt`를 XOR | 같은 해시 표를 쓰므로 새 표가 없다. 충돌 공유 trade-off는 기존과 같다 | PC방·CGNAT가 4를 넘으면 설정으로 올린다 |
| A3 | **쿠키 단계(SEC-4).** 1차 요청(flags bit0 없음) → `request.RejectForce(cookie 16 B)`. cookie = HMAC(서버 시작 난수 32 B, IP ‖ port ‖ 시간 창 번호(30 s))[0..16]. 2차 요청은 현재·직전 창의 쿠키 중 하나와 같아야 토큰 소비·RSA 복호·Accept로 간다. 빈도 초과·꽉 참·쿠키 불일치는 모두 `RejectForce`(임시 peer 없음, `rejects cookie` 카운터). `Reject(byte[])`(신뢰)는 쿠키 통과 뒤 `BadRequest`·`VersionMismatch`에만 | 위조 출발지는 쿠키를 받지 못해 토큰·슬롯·RSA 비용을 못 쓴다. 반사 트래픽은 16 B 데이터그램 하나뿐 | 접속마다 +1 RTT. 자동 재접속 예산(1.5 s)은 250 ms × 5 안에서 2회 왕복을 흡수한다. Client·봇은 `ConnectionRejected` + 16 B 데이터를 받으면 같은 주소로 즉시 쿠키를 넣어 다시 `Connect`한다(쿠키가 아닌 거절 데이터 1 B는 지금처럼 RejectReason) |
| A4 | **Seq 창(SEC-7 단기).** `PlayerInputBuffer.Add`: `LastTakenSeq > 0`이고 `Seq − LastTakenSeq > ProtocolConstants.MaxInputSeqAhead`(64)면 false + `SeqAheadDrops++`(Health `inputSeqDrops`). `GameLoop.DrainInput`은 `Match.EnqueueInput`이 하나라도 받아들였을 때만 `LastInputTick` 갱신(`EnqueueInput`이 bool 반환) | 정상 Client는 Tick당 1씩 올리고 예측 누적 상한이 7 Step이라 창을 넘지 않는다. 주입당한 플레이어는 InputTimeout으로 끊겨 유예·Resume으로 풀린다 | 창이 너무 작으면 긴 히치 뒤 입력이 버려진다 → 64 Tick(2 s)은 히치 상한(0.25 s)의 8배 |
| A5 | **채널 용량(SEC-18).** `InboundChannels`: Input = MaxPlayers × InputBurst, Build = MaxPlayers × 2 × maxRequestsPerSecond. Networking.md:319 문구 수정 | 메모리 수 KB | — |
| A6 | **실패 출처 집계(SEC-14).** `GameLoop._playerFailureTicks` Ring에 `(tick, slot)`을 적는다(slot = `ConnectRateLimiter.SlotOf(peer.Address)`). 리셋 조건: 10 s에 MaxPlayers번 **이고** 서로 다른 slot ≥ 2. 같은 slot이 60 s에 3번이면 그 칸 `PenaltyUntilMs = now + 60 s`(수신 스레드 표를 Game Loop가 쓰면 안 되므로 `ConnectRateLimiter.Penalize(slot, untilMs)`는 Interlocked로 쓴다 — 칸당 long 하나) | 공격자 한 명이 리셋·종료를 못 일으킨다. 코드 결함(여러 출처)이면 지금처럼 리셋한다 | 정상 플레이어 2명이 같은 결함으로 실패하면 리셋이 지연된다(10 s 창 안에서만) |

### B. 신원·무결성 (SEC-2·7, SEC-24~26의 전제)

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| B1 | **키.** 서버 RSA-2048 개인키: `Server:PrivateKeyPem`(환경 변수 `Server__PrivateKeyPem`) 또는 `Server:PrivateKeyPath`. 둘 다 없으면 저장소의 **개발용 키** `Server/src/ProjectH.Server/keys/dev-server.pem`(시작 Warning "DEV KEY"). Production 환경에서 개발용 키면 시작 거부. 공개키 PEM: Client `Assets/Resources/ServerPublicKey.txt`(개발용 커밋), 봇·QA는 `--server-public-key <path>` 기본값 = 같은 파일. 세션 키 32 B는 Client가 `RandomNumberGenerator`로 만든다 | 핀이 있어야 경로 위 공격자도 막는다. 개발 키 커밋은 DB 개발 비밀번호와 같은 정책(문서에 명시) | 배포가 공개키 파일 교체를 빠뜨리면 접속이 거절된다(BadRequest). 시작 로그에 키 지문을 남긴다 |
| B2 | **`ConnectRequestData` v19.** `version u16 ‖ flags u8(bit0 cookie, bit1 resume) ‖ [cookie 16] ‖ sessionKeyBlob u16 길이 + 256 ‖ devPlayerId(기존) ‖ [resumeNonce u32 ‖ resumeProof 16]`. 서버 순서: 꽉 참 → 쿠키 → 빈도·동시 수 → 복호(실패 BadRequest) → 이름 → 버전 → 키 등록 → Accept. 파생 키 `K_c2s = HMAC(K, "c2s")`, `K_s2c = HMAC(K, "s2c")`, `K_resume = HMAC(K, "resume")` | 복호는 쿠키·빈도 뒤라 CPU 소모 공격이 막힌다 | 연결 요청이 약 300 B가 된다(MTU 안) |
| B3 | **SessionAuth(Shared `Protocol/SessionAuth.cs`, 순수 코드, LiteNetLib 의존 없음) + 쪽마다 얇은 `PacketLayerBase` 어댑터(`Server/Net/AuthPacketLayer.cs`, `Client/Net/AuthPacketLayer.cs`, `Bots/AuthPacketLayer.cs`, 테스트 `HeadlessClient`).** 꼬리 20 B = `counter u32 ‖ HMAC-SHA256(K_dir, counter ‖ payload)[0..16]`(`HMACSHA256.TryComputeHash`, 할당 없음). 방향별 counter 단조 증가(송신), 수신은 64칸 sliding window(비트마스크)로 재전송·역순 거부. 키 표 `ConcurrentDictionary<IPEndPoint, SessionKeys>`. 등록: 서버 Accept 직전, Client `Connect` 직전. 제거: `OnPeerDisconnected`. **키 없는 endpoint**: 서버 수신은 통과(연결 요청 전 단계 — 쿠키·빈도가 지킨다), 서버 송신은 그대로(RejectForce 데이터), Client 수신은 "첫 검증 성공 전"에만 통과(쿠키 RejectForce가 서명 없이 온다). 키 있는 endpoint의 꼬리 없음·불일치·창 밖은 `length = 0`으로 버리고 `authDrops`(Health·Meter). **스레드:** LiteNetLib는 Ack를 수신 스레드에서, 재전송·Ping을 logic 스레드에서 보내므로 송신 봉인은 두 스레드에서 겹칠 수 있다 → `SessionKeys`의 송신 HMAC·counter는 **leaf lock 하나**(`lock (_send)`, 안에서 HMAC만, 콜백·I/O 없음, 중첩 없음 → Deadlock 불가). 수신은 소켓당 스레드 하나라 수신 HMAC·창은 Lock 없음. `ExtraPacketSizeForLayer = 20` → LiteNetLib 사용자 MTU 1212 | 데이터그램 단위라 조각·Ping·Ack까지 보호되고 위조 조각은 재조립 전에 버려진다(SEC-1 보완). Shared에 전송 라이브러리 의존을 넣지 않는다 | 데이터그램당 HMAC 1회(약 1–2 µs) + 짧은 Lock. 100명 × 15 Hz × 2 = 3,000/s. 인증 실패는 끊지 않는다. Server.md "Lock 0개"는 "leaf lock 1개(AuthPacketLayer 송신)"로 고친다 |
| B3-1 | **RSA 형식·패딩.** 키는 `RSA.ToXmlString`/`FromXmlString` XML(Unity Mono에 `ImportFromPem`이 없다). 패딩은 `RSAEncryptionPadding.OaepSHA1`(Mono 관리형 RSA가 OAEP-SHA256을 지원하지 않을 수 있다. 32 B 난수 키를 감싸는 용도라 SHA-1 OAEP로 충분) | 양쪽 런타임 교집합 | Unity에서 Encrypt가 던지면 `RSACryptoServiceProvider.Encrypt(data, fOAEP: true)`로 대체(같은 OAEP-SHA1) |
| B4 | **Resume 증명(SEC-2).** Join Ok 때 `PlayerEntity.ResumeKey = K_resume`, `LastResumeNonce = 0`(유예로 가도 유지). 새 연결 `resumeProof = HMAC(K_resume_old, nonce ‖ devPlayerId)[0..16]`, `nonce`는 Client가 올리는 u32. `FindGraced`: 이름 일치 **그리고** 증명 일치 **그리고** `nonce > LastResumeNonce`. 아니면 새 플레이어(지금 "연결 중이면 새 플레이어"와 같은 동작). Client는 Join 성공 뒤 `K_resume`과 nonce를 들고 자동 재접속 때 쓴다. 전적 계정 키는 그대로 DevPlayerId | 토큰이 평문으로 나가지 않는다(RSA로 보호된 세션 키에서 파생). 외부 인증 없이 캐릭터 탈취를 막는다 | 외부 인증·계정 id는 다음 Phase(Priority 2 후반) |
| B5 | **끊김·거절 코드.** 새 DisconnectCode 없음. 거절 데이터: 16 B = 쿠키, 1 B = RejectReason(기존) | Protocol 변경 최소 | — |

### C. 경기 공정성 (SEC-5·6·8·9·10·11·13·19·20)

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| C1 | **경기 비밀(SEC-6·11).** `Match.StartMatch`(Round 시작)마다 `MatchSecret`(ulong, RandomNumberGenerator). 모든 시드 = `(int)Mix(MatchSecret ^ 용도 salt ^ Round)`(SplitMix64, WeaponSpread.Mix 재사용). 새 옵션 `Server:DeterministicSeeds`(기본 false). true면 지금처럼 `LootSeed/ZoneSeed/SpawnSeed + Round`(테스트·QA 재현용). Production에서 true면 Warning. `WeaponSpread.Spread`에 `ulong secret` 인자(해시 입력에 XOR), 주석 수정. `/qa/health`의 seeds는 DeterministicSeeds일 때만 | 비밀이 Client에 가지 않으면 재현이 불가능하다. 테스트는 Deterministic으로 지금 결과를 유지 | 기존 Match 테스트는 `DeterministicSeeds = true` 옵션을 줘야 한다(테스트 GameData 기본값으로 둔다) |
| C2 | **무기 교체 지연(SEC-8).** `PlayerEntity.SwitchReadyTick`. `SelectSlot`이 칸을 바꾸면, 줍기(교환)·버리기 뒤에도 `= now + weapon.EquipTicks`. `weapons.json` 선택 필드 `equipSeconds`(기본 0.4, 0–2), Tick 변환. `Apply`는 `now < SwitchReadyTick`이면 쏘지 않는다(탄·간격 소모 없음). `DroppedFireLockTick` 유지. Client `WeaponState` 복사본에 같은 규칙(예측), 서버 비교 테스트 | 정상 Client로 되는 밸런스 붕괴를 막는다. 0.4 s는 Kestrel 간격 1.25 s의 1/3 | 체감이 느리면 데이터 값만 바꾼다 |
| C3 | **되감기 RTT 제한(SEC-5).** `GameLoop.SweepPeers`가 Tick마다 `PeerState.RttMs = peer.RoundTripTime`(Game Loop 전용). Match는 기존 Backlog 질의 delegate처럼 `Func<int, int> rttOf`를 받는다. 허용 Tick = clamp(RttMs × SimHz / 2000 + 2 × SnapshotEveryTicks + 2, 2, MaxRewindTicks). `ClampViewTick(uint viewTick, uint latestTick, int allowedTicks)`. 넘으면 상한으로 자르고 `RewindClamped++`(C7) | 지연 200 ms 플레이어는 지금처럼(≈ 3+4+2 = 9 Tick), 10 ms 플레이어는 6 Tick | RTT 측정 잡음으로 간헐적 잘림 → 여유 2 Tick |
| C4 | **줍기 시선(SEC-9).** `Pickup`·`PickUpMaterials`: `FindNearest` 결과에 `ClearSight(눈, 아이템 위치 + (0, 0.2, 0), pieces: true)`. 막히면 그 E는 `NothingInRange`(다음 후보로 넘어가지 않음 — Client `PickupRule` 안내와 일치, Phase 16 S2 정책) | Container·차량과 같은 함수·정책 | 열린 문 틈 같은 경계 사례는 안내가 뜨고 거절될 수 있다 |
| C5 | **투사체 개인 상한(SEC-13).** `ProjectileRules.MaxPerOwner = 4`. `CanLaunch`가 32칸을 훑어 같은 `OwnerJoinOrder` 수를 센다. 거부면 탄·간격 소모 없음(지금 가득 찼을 때와 같은 분기) | 32칸 전역 공유는 유지 | 4가 적으면 데이터 값 |
| C6 | **아이템 행동 간격(SEC-20).** `PlayerEntity.NextItemActionTick = now + ItemRules.ActionIntervalTicks`(0.25 s). G(버리기)·E(줍기)·자원 자동 줍기 제외 | Reliable 폭주 상한 | 연타 느낌이 둔해지면 값 조정 |
| C7 | **Anti-cheat 기록(SEC-10·19).** `PlayerEntity` 고정 카운터: `ShotsFired, PelletsFired, PelletsHit, RewindTicksSum, RewindClamped, MaxHitDistanceCm, MovementAnomalies, MaxAimTurnDeg`(Tick 사이 AimYaw 변화 최댓값 × 10 정수). `_movementAnomaly`에 EntityId 전달(`Action<ushort>`). `PlayerRecord`에 8개 int, `match_player`에 8열(`shots, pellets, hits, rewind_ticks, rewind_clamped, max_hit_distance_cm, movement_anomalies, max_aim_turn`). **`schema_version` 표(v2)**: `EnsureSchemaAsync`가 CREATE IF NOT EXISTS 뒤 버전을 읽고, v1(표 없음 = 기존 DB)이면 `information_schema.COLUMNS`로 열 존재를 확인하며 `ALTER TABLE match_player ADD COLUMN …`을 실행하고 v2를 기록(두 번 실행 안전). Silent Aim은 `MaxAimTurnDeg` 기록만 | 사후 탐지 원천 데이터. 모두 정수 증가, 할당 없음 | DB 열 8개 |

### D. 상태 정확성 (STB-0·1·2, SEC-24·25·26)

| # | 결정 | 추천 이유 | 틀렸을 때의 비용 |
|---|---|---|---|
| D1 | **맵 경계(STB-0).** `MovementSimulation.ClampToMap(ref Vector3 position, ref MoveState state)` private static(StepAir의 576-586을 옮김). StepGround 끝(`state.Position = position` 앞), StepVault 위치 갱신 뒤, StepAir에서 호출. `TryStartVault`는 착지점 `|x|,|z| > bound`면 시작하지 않는다. Movement.md '바깥벽 경계'에 지상·Vault 추가 | 양쪽이 같은 Shared 함수를 쓴다 | 경계에 붙은 플레이어가 밀려 보정 1회 |
| D2 | **ViewTick uint(STB-1).** `InputCommand.ViewTick` float → uint(4 B 그대로). Client `(uint)renderTick`, "지금" = `uint.MaxValue`(서버는 latest로 자름). `ClampViewTick(uint, uint, int)` | 장시간 가동 정밀도 | Protocol v19에 포함 |
| D3 | **Client 수신 검증(SEC-24·25·26).** Shared 파서: `PlayerSpawned` Finite + `IsValidPlayerName`; `JoinMatchResponse` `MyEntityId != 0`, `Result ≤ Resumed`; `BuildCatalogPacket` InterestCellSize ∈ {20, 40, 80, 160}; `ZoneState` 좌표 ≤ ±(HalfSize + 10000)·반지름 ≤ 10000; `ProjectileSpawned/State` 위치 ±512·속도 ≤ 200, `WeaponCatalog` 투사체 speed ≤ 200·gravity ≤ 50·radius ≤ 10 — 상수는 Shared `ProtocolLimits`에 두고 서버 검증도 같은 상수를 쓴다. Client 소비자: `OnSpawned`는 `State == Joined && MyEntityId != 0`일 때만 내 Spawn; `ServerClock.OnSnapshot`·`VehicleStore.Apply`는 `tick > LatestTick + SimHz × 10`이면 버리고 `TickRejects++`; `RemotePlayers.Spawn`은 `Count ≥ MaxSnapshotEntities`면 무시·`SpawnRejects++`; `_names`는 RemotePlayers에 있거나 내 id일 때만 | 정상 서버는 유발하지 않지만 9절 규칙과 G4 하위 증상 차단 | 파서 테스트 거절 사례 추가 |
| D4 | **조각 뷰(STB-2).** `GameClient.LateUpdateGame` 668: 예측기가 없어도 `_pieceViews.Apply(_buildStore, _build.Catalog, EstimatedServerTick())` 뒤 `ClearChanged` | 뷰 갱신에 예측기가 필요 없다 | — |

### 범위 밖(이유)

관심 영역(설계 결정·측정 선행), 외부 인증·계정 id(로그인 서버 없음), 패킷 암호화(기밀성 이득 작음), SEC-19 각도 차이 제한(정상 입력 파괴), E 묶음(G6 비밀 분리·QA allow-list·SslMode·Low·문서 동기화)은 다음 Spec.

## 검증 계획

1. **서버 테스트(Server.Tests)**: AuthLayer 왕복·꼬리 없음·불일치·재전송·창 밖 거부·키 없는 endpoint 통과; 쿠키 흐름(1차 RejectForce 16 B → 2차 Accept, 틀린 쿠키 RejectForce); IP 동시 연결 상한(상한+1 거절, 다른 IP 수락, 해제 뒤 회복); 전역 수락 상한; 패킷 크기 상한(129 B Malformed); 조각 상한; Seq 창(먼 미래 Seq 뒤 정상 처리 지속, LastInputTick 미갱신→InputTimeout); 채널 용량 ≥ burst 합; 실패 출처 리셋 규칙(같은 slot 16회 → 리셋 없음·벌점, 두 slot → 리셋); Resume 증명(없음·틀림 → 새 플레이어, 맞음 → Resumed, nonce 재사용 거부); 시드 비밀(두 Match의 Zone·Container 다름, Deterministic이면 같음); 퍼짐 비밀; 교체 지연 입력열(Kestrel×3 4 Tick 안 1발); RTT 되감기(RTT 20 ms → 6 Tick, 200 ms → 9 Tick); 줍기 시선(벽·문·건설 벽 너머 NothingInRange, 문 열면 Ok); 투사체 개인 상한; 아이템 간격; 카운터 값과 DB 열(MySqlFact, v1 DB에서 v2 마이그레이션); 맵 경계(칸 31 Ramp Sprint+Jump 60 Tick, 2층 Floor 걷기); ViewTick uint; MTU 예산(모든 패킷 + 4 + 20 ≤ 1232). 통합(HeadlessClient가 쿠키·AuthLayer·Resume 구현): Fuzz·Hardening·Reconnect·StatsQuery 통합 테스트 그대로 통과.
2. **Client EditMode**: WeaponState 교체 지연 = 서버(비교 테스트), ServerClock·VehicleStore 큰 Tick 거부, RemotePlayers 상한, Join 전·id 0·NaN Spawn 무시, 파서 거절 사례.
3. **QA**: `smoke`, `pre-push`, `suite:weapons`, `suite:building`, `suite:squad`(Resume), `suite:vehicle`; 봇 50명 `stress-quick`에서 authDrops·inputSeqDrops·rejects cookie = 0(정상 흐름), Tick p99 기준선 대비 50 % 넘게 나빠지지 않음.
4. **리뷰**: 묶음마다 game-reviewer `safety` + `server-hotpath`(AuthLayer·Snapshot 경로) + `server-concurrency`(ConcurrentDictionary·Interlocked 벌점) + `db-query`(C7). 보고서 SEC/STB 번호별 해소 확인.
5. **문서**: Networking.md(v19, 접속 순서에 쿠키·AuthLayer·Resume 증명, Validation), Server.md(옵션: MaxConnectionsPerIp, AcceptsPerSecond, DeterministicSeeds, PrivateKeyPem/Path; 스레드 표에 AuthLayer; Health에 authDrops·inputSeqDrops·rejects cookie), Movement.md, Weapons.md(equipSeconds), Database.md(schema_version·열 8개), ServerTestPlan.md 7절 G1·G2·G4·G5 상태, Bots.md(공개키 옵션), Client.md(Resources 공개키·Resume 키).

## 하네스 확인이 필요한 것

`game-core-rules` 4절 Shared 예외에 **AuthLayer·ConnectRequestData 인증 블록·ProtocolLimits 상수**를 더한다(Client·봇·서버가 같은 계층과 같은 한계값을 써야 한다. 키 생성·검증 정책은 서버·Client 각자). Server.md "Lock을 쓰지 않는다" 문장에 ConcurrentDictionary 한 곳을 명시한다.
