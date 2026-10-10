# Networking

Transport: LiteNetLib 2.1.4 (UDP). 프레이밍 `[PacketId: byte][payload]`, little-endian, 수기 직렬화(`PacketWriter`/`PacketReader`).
`ProtocolVersion`(현재 19. 리뷰 수정 A–D: 연결 요청에 flags·쿠키(묶음 A, 아래 "접속 순서"), 묶음 B에서 세션 키 blob·Resume 증명·데이터그램 인증 꼬리(아래 "접속 순서"), 묶음 C에서 `WeaponCatalog`의 무기 기록마다 끝에 `EquipTicks` u16(교체 대기 Tick, 0–256. 아래 "Validation"), 묶음 D에서 `ViewTick`이 같은 4 B의 uint가 됐다(아래 "전투 (Phase 3)"). Phase 19: 차량 상태 패킷 `VehicleStates` 48(S→C Unreliable, "차량 (Phase 19)", `Vehicles.md`). Phase 18: 게임 오디오용 정보(`ShotFired` 끝의 무기 id 1B, `BuildEvents` Destroyed 기록의 이유 1B(5B), `DamageTaken` 끝의 실드 플래그 1B, 새 패킷 `WorldSound` 47. "게임 오디오 (Phase 18)", `Audio.md`). Phase 17: 무기·투사체(`WeaponInfo`의 산탄 수·퍼짐·반동·투사체 종류와 `WeaponCatalog` 끝의 투사체 목록, 탄 종류 `Shells` 4·`Rockets` 5, 소모품 `Grenade` 3, `InventoryState` 27B, 입력 버튼 `ThrowGrenade` 32768, 투사체 패킷 3종 `ProjectileSpawned` 44·`ProjectileState` 45·`ProjectileExploded` 46, `DeathCause.Explosion` 2. "무기와 투사체 (Phase 17)", `Weapons.md`). Phase 16: Loot Container·Supply Drop 패킷 2종(`ContainerStates` 42, `SupplyDrops` 43, 둘 다 S→C, "Loot Container와 Supply Drop (Phase 16)", `Loot.md`). Phase 15: 지도 표시 패킷 2종(`MapMarker` 40 C→S, `TeamMarkers` 41 S→C, "지도 표시 (Phase 15)", `Map.md` "지도 UI·Ping"). Phase 14: 분대 패킷 4종(`TeamState` 36, `PlayerDowned` 37, `ChannelState` 38, `RebootStations` 39), 이동 모드 `Downed` 7, 입력 버튼 `InteractHeld`, 아이템 종류 `RebootCard`, `InventoryState`의 카드 수("분대 (Phase 14)", `Squad.md`). Phase 13.5: 편집(`BuildEditRequest` 35, v12). Phase 13: 건설 패킷 6종과 채집 패킷 3종(`PacketId` 26–34), 건설 전용 채널 1, 입력 버튼 `ToolHarvest`·`ToolBuild`, Snapshot의 도구(Self 무기 칸 바이트의 위 2비트, Entity `Flags` bit6–7), 아이템 종류 `Material`이 생겼다("건설과 채집 (Phase 13)", `Building.md`). Phase 12: 이동 모드를 Snapshot Entity의 `Flags`에 싣고, 수신자 블록(Self)이 6B에서 14B가 됐고, `Crouch` 버튼, 새 패킷 `TransportRoute`·`DoorStates`, `PlayerRespawned.Mode`, `PlayerDied.Cause`가 생겼다. 이동 규칙도 바뀌었다("투입과 문 (Phase 12)", `Movement.md`). Phase 11: 전적 패킷 `StatsRequest`/`StatsResponse`가 생겼고 `PlayerSpawned`에 이름(`Name`)이 들어갔다("전적 조회 (Phase 11 D8)"). Phase 10: 서버가 끊을 때 이유 코드(`DisconnectCode`)를 보내고 `JoinResult.Resumed`가 생겼다("끊기와 재접속 (Phase 10)"). 패킷 형식은 같다. Phase 8: Snapshot을 여러 패킷으로 나누고 엔티티를 양자화했다("Snapshot 분할과 양자화"). Phase 6: 지형과 새 맵 박스로 이동 결과가 바뀌었다. 패킷 형식은 같다. Phase 3에서 입력 명령·Snapshot 형식이 바뀌고 전투 패킷이 생겼고, Phase 4에서 Buttons가 2B가 되고 무기 카탈로그에 탄약 종류가, 아이템 패킷 6종이 생겼고, Phase 5에서 경기 패킷 3종(`MatchState`, `ZoneState`, `MatchResult`)과 `PlayerDied`의 Placement가 생겼다) 불일치 연결은 접속 단계(`OnConnectionRequest`)에서 `RejectReason.VersionMismatch`로 거절된다. 그 외 거절 사유: `ServerFull`(연결 수 ≥ MaxPlayers, 서버 종료 중, 같은 IP의 연결 요청이 너무 잦음(서버 리뷰 M2), 같은 IP의 연결이 이미 `MaxConnectionsPerIp`개, 그 IP가 벌점 중, 전역 수락 빈도 초과(리뷰 수정 A2·A6)), `BadRequest`(연결 데이터 없음·파싱 실패·DevPlayerId가 이름 규칙에 어긋남. 아래 "Validation"). 버전 확인 뒤 쿠키가 없거나 틀리면 거절 데이터가 16B 쿠키다(리뷰 수정 A3, "접속 순서"). 모든 거절은 `RejectForce`라 서버에 임시 peer가 생기지 않는다. Client는 거절 사유를 끊김 화면이 한국어로 보여 준다(`UiText.Reject`).

## MTU

LiteNetLib의 기본 단일 패킷 한도는 1020B라 Snapshot 한도(1200B)보다 작다. 줄에 나가는 데이터그램의 예산은 `ProtocolConstants.Mtu = 1232`다(IPv6 최소 MTU 1280 − 헤더 48B). 리뷰 수정 B3부터 모든 데이터그램 끝에 인증 꼬리 20B가 붙는데 LiteNetLib는 계층 크기를 MTU에서 빼지 않으므로(MtuOverride 1232에서 Sequenced 1228B가 그대로 나가 줄에는 1252B였다, 실측), 서버·Client·봇·테스트 Client의 `NetManager.MtuOverride`는 모두 `ProtocolLimits.UserMtu = 1212`다. Sequenced 패킷에 1208B가 남아 `MaxPacketSize`(1200)가 들어가고, 줄의 데이터그램은 1232B를 넘지 않는다(`PacketTests`, `AuthPacketLayerTests`). 모든 쪽이 같은 상수를 공유하므로 한쪽만 바꾸지 않는다.

## Packets

| Packet | 방향 | Delivery | 내용 |
|---|---|---|---|
| ConnectRequestData | C→S | 연결 요청 데이터 | v19: ProtocolVersion u16, Flags u8(bit0 `HasCookie`, bit1 `HasResume`), [쿠키 16B], 세션 키 blob(u16 길이 = 256 + 256B), DevPlayerId(1바이트 길이 + 1–32B 올바른 UTF-8, 제어·서식 문자·줄 구분자 없음), [Resume nonce u32 + 증명 16B] (PacketId 없음. 아래 "접속 순서" 1번) |
| JoinMatchRequest | C→S | ReliableOrdered | 없음 (Client는 연결 직후 자동 전송) |
| JoinMatchResponse | S→C | ReliableOrdered | Result(Ok / AlreadyJoined / MatchFull / Resumed(3)), MyEntityId, ServerTick, SimHz, SnapshotHz |
| PlayerSpawned | S→C | ReliableOrdered | EntityId, Position, Yaw, Name(DevPlayerId, 1바이트 길이 + UTF-8 1–32B. 빈 이름·33B 이상은 읽기 실패) |
| PlayerDespawned | S→C | ReliableOrdered | EntityId |
| PlayerInput | C→S | Unreliable | 최근 입력 1–3개(Seq, MoveX, MoveY, Yaw, Buttons u16, AimYaw, AimPitch, ViewTick u32 = 명령 1개 30B), 오래된 것부터. ViewTick은 v19부터 uint Tick이다(`uint.MaxValue` = "지금", 허용보다 오래된 값(0 포함)은 허용 범위의 가장 오래된 Tick으로 잘린다. "전투 (Phase 3)") |
| WorldSnapshot | S→C | Sequenced | ServerTick, AckInputSeq(수신자별), Count, Part, PartCount, 수신자 블록(Health, Shield, WeaponSlot = 현재 인벤토리 칸 0–2, Ammo = 그 칸의 탄창(빈 칸이면 0), ReloadRemainingTicks, 기력 u16(×100, 0–10000), 수평 속도 X·Z i16 ×2(1/256 m/s), `ModeTicks`, `EnergyDelayTicks` = 14B, 수신자별), [EntityId, Position, VelocityY, Yaw, Flags(bit0 생존, bit1–3 이동 모드, bit4 달리는 중, bit5 기진 = 달리기 불가 상태)] (엔티티 13B, 양자화) |
| WeaponCatalog | S→C | ReliableOrdered | Join 응답 직후 1회. 무기 1–8개: WeaponId, Name ≤ 16B, Damage(Phase 17: 산탄 하나당), FireIntervalTicks, MagazineSize, ReloadTicks, Range, Automatic, AmmoType(1 Light, 2 Medium, 3 Heavy, Phase 17: 4 Shells, 5 Rockets), Phase 17: Pellets u8(1–16), SpreadDegrees·RecoilDegrees float(0–30), Projectile u8(0 Hitscan, 1 Grenade, 2 Rocket). 그 뒤 투사체 수 u8(0–2) + 투사체 × (Kind u8, Speed, Gravity, ExplosionRadius float, LifetimeTicks u16 = 15B). 무기가 쏘는 투사체는 목록에 있어야 한다. 수류탄은 무기가 아니어도 목록에 있다 |
| ItemCatalog | S→C | ReliableOrdered | WeaponCatalog 직후 1회. 등급 5개(Name, DamageMultiplier), 탄약 3종(Type, Name, Max), 소모품 2종(Type, Name, UseTicks, Heal, Shield, MaxStack). 최대 219B. Phase 17: 탄약 5종, 소모품 3종(Grenade는 UseTicks·Heal·Shield 0, 회복은 둘 다 필요), 최대 284B |
| WorldItems | S→C(새로 들어온 사람) | ReliableOrdered | ItemCatalog 직후. 월드 아이템 전체를 50개씩 나눠서: Count 1–50 + [ItemId u16, Kind, DefId, Rarity, Amount u16, Position] × Count(개체마다 19B). 패킷 최대 952B, 256개면 6개 패킷 |
| ItemSpawned | S→C(전원) | ReliableOrdered | 아이템 1개(패킷 20B).  새 아이템이거나 수량 변경(ItemId 기준 Upsert) |
| ItemRemoved | S→C(전원) | ReliableOrdered | ItemId |
| InventoryState | S→C(본인) | ReliableOrdered | Join 때와, 인벤토리가 바뀐 Tick 끝에 1회(발사는 제외). 칸 3 × (WeaponId(0 = 빈 칸), Rarity, MagAmmo), CurrentSlot, 탄약 3 × u16, Medkits, ShieldCells, Using(0 없음, 1 Medkit, 2 ShieldCell), UseRemainingTicks, `RebootCards`(Phase 14, 0–3. 3보다 크면 읽기 실패), Phase 17: ShellsAmmo·RocketsAmmo u16, Grenades u8. 28B(본문 27B). Using은 2(ShieldCell)까지(수류탄은 채널 사용이 없다) |
| PickupResult | S→C(누른 사람) | ReliableOrdered | Result(Ok / NothingInRange / Full), ItemId. 안내 표시용 |
| ShotFired | S→C(전원) | Unreliable | ShooterId, Start(눈), End(멈춘 곳), Phase 18: WeaponId u8(카탈로그의 무기 id). 28B. 방아쇠 한 번에 하나 |
| HitConfirmed | S→C(쏜 사람) | ReliableOrdered | TargetId, Damage(무기의 명목 피해. 실제로 깎인 양이 아니다), Killed(탈락시켰을 때만. Phase 14: 기절시킨 명중은 false) |
| DamageTaken | S→C(맞은 사람) | ReliableOrdered | AttackerId, Damage, FromDirection(맞은 쪽 → 쏜 쪽 단위 벡터), Phase 18: Flags u8(bit0 실드 맞음, bit1 실드 깨짐). 18B |
| PlayerDied | S→C(전원) | ReliableOrdered | VictimId, KillerId(0 = 처치자 없음), Placement(경기 중 탈락이면 1 이상: Solo는 남은 생존자 수 + 1, Phase 14 팀 경기는 팀이 전멸했으면 팀 배치, 살아 있으면 그 순간 남은 팀 수(잠정). 경기 밖이면 0), `Cause`(0 자기장·플레이어, 1 낙하, Phase 17: 2 폭발. 2보다 크면 읽기 실패). 7B. 총격 처치는 `Cause` 0이다. Phase 17: 폭발 처치는 `Cause` 2와 처치자(투사체 주인, 경기에 없거나 죽었으면 0)를 함께 싣는다. 경기 중 들어온 사람에게는 본인에게만 `VictimId = 자기, KillerId 0, Placement 0, Cause 0`으로 보낸다(관전 시작) |
| PlayerRespawned | S→C(전원) | ReliableOrdered | EntityId, Position, Yaw, `Mode`(시작 이동 모드: 경기 시작의 공중 투입이면 `Transport` 6, 아니면 `Ground` 0. Phase 14: `Downed` 7까지 읽힌다(재투입은 `Ground`). 7보다 크면 읽기 실패). 20B. 경기 시작·판 재시작 때 모두의 Spawn 이동에도 쓴다 |
| TransportRoute | S→C | ReliableOrdered | 시작 X·Z, 끝 X·Z, 고도(float 5개), 시작 Tick, 길이 Tick(u32 2개) = 29B. 경기 시작 때(`PlayerRespawned`보다 먼저 보낸다. 같은 채널이라 그 순서로 도착한다)와 경기 중 Join·Resume 때. Client는 이 값으로 수송기 위치를 서버 Tick마다 계산한다(`DropRoute.PositionAt`). 좌표가 ±127 밖이거나 고도가 음수이거나 길이가 0 또는 76800 Tick(10분) 초과면 읽기 실패 |
| DoorStates | S→C | ReliableOrdered | 열린 문 비트 마스크(bit i = `GameMap.Doors[i]`, 문 5개) = 2B. 문이 바뀐 Tick의 끝(Tick당 최대 1개), 판 시작(모두 닫힘), Join·Resume 때. 없는 문의 비트가 켜져 있으면 읽기 실패 |
| MatchState | S→C(전원, 바뀐 Tick 끝)·Join | ReliableOrdered | State(0 Waiting, 1 Starting, 2 Playing, 3 FinalPhase, 4 Finished, 5 Closing), StateEndTick u32(0 = 타이머 없음), Alive, Participants, Round u16, MinPlayers. 11B. 경기 전에는 Alive·Participants가 접속자 수다 |
| ZoneState | S→C(전원, 단계가 바뀐 Tick 끝)·Join | ReliableOrdered | Phase(0 = Zone 없음), From(X, Z, Radius), To(X, Z, Radius), ShrinkStartTick, ShrinkEndTick, DamagePerSecond u16. 36B |
| MatchResult | S→C(접속 중인 참가자 본인) | ReliableOrdered | WinnerId(0 = 없음), Placement, Kills, Participants. 6B |
| StatsRequest | C→S | ReliableOrdered | 없음(PacketId만). Join을 요청한 연결만, 연결당 2초에 한 번. 본문이 있으면 잘못된 패킷 |
| BuildCatalog | S→C | ReliableOrdered(채널 1) | Phase 13. Join·Resume 때 1회, 건설 채널의 첫 패킷(reset Sync 바로 앞, 최종 리뷰 A3). 49B. 내용은 `Building.md` "네트워크" |
| ResourcesState · HarvestHit · HarvestStates | S→C | ReliableOrdered(채널 0) | Phase 13. 자원 7B(본인, 바뀐 Tick 끝·Join·Resume), 채집 타격 18B(휘두른 사람), 부서진 채집 대상 마스크 9B(바뀐 Tick 끝·Join·Resume) |
| BuildRequest | C→S | ReliableOrdered(채널 1) | Phase 13. 9B. Join한 연결만, 연결당 초당 20개 |
| BuildEditRequest | C→S | ReliableOrdered(채널 1) | Phase 13.5. 번호 u16(`BuildRequest`와 같은 카운터), 조각 id u32, 상태 u16(Edit 12비트 + 회전 2비트 ≪ 12) = 9B. `BuildRequest`와 같은 큐·간격·초당 상한(`Building.md` "편집 (Phase 13.5)") |
| BuildResult · BuildEvents · BuildSync · BuildInterest | S→C | ReliableOrdered(채널 1) | Phase 13. 8B, 헤더 9B + 기록(Placed 14B, Edited 6B, Health 6B, Destroyed 5B: id + Phase 18 이유), 헤더 7B + 조각 16B × 최대 74(1191B), 9B. `Building.md` "네트워크" |
| TeamState | S→C(자기 팀 구성원) | ReliableOrdered | Phase 14 D2. TeamId, Count 1–4, 구성원 × (EntityId u16, State: Up 0 / Downed 1 / Eliminated 2 / Rebooting 3, 10 단위로 올린 체력, Flags: CardDropped 1 / CardHeld 2) = 최대 23B. 경기 시작, 경기 중 Join·Resume, 바뀐 Tick 끝. 자기 팀만(적 팀 구성은 보내지 않는다) |
| PlayerDowned | S→C(전원) | ReliableOrdered | Phase 14 D5. VictimId, AttackerId(0 = 없음), Cause(0 자기장·총격, 1 낙하, Phase 17: 2 폭발 — 주인이 있으면 AttackerId와 함께). 6B. Kill Feed "A ▸ B 기절" |
| ChannelState | S→C(행위자 팀) | ReliableOrdered | Phase 14 D8. Kind(Revive 0 / Reboot 1), ActorId, Target(소생: 대상 Entity id, 재투입: 스테이션 번호), EndTick u32, Active. 11B. 시작·끝(완료·취소), Resume 때 진행 중인 것 |
| RebootStations | S→C(전원) | ReliableOrdered | Phase 14 D10. 대기 마스크(bit i = `RebootStations.All[i]`) + 끝 Tick u32 × 4 = 18B. 바뀐 Tick 끝, 경기 시작·리셋, Join·Resume |
| MapMarker | C→S | ReliableOrdered(채널 0) | Phase 15 D7. 종류 u8(Location 0 / Enemy 1 / Item 2 / Danger 3 / WaypointSet 4 / WaypointClear 5), x·y·z int16(1/100 m), 대상 id u16(Enemy = Entity id, Item = 아이템 id, 그 밖 0) = 10B. Join한 연결만, 연결당 초당 2개(한 번에 4개) |
| TeamMarkers | S→C(자기 팀 구성원) | ReliableOrdered | Phase 15 D10. Ping 수 + Ping × (id u8, 종류 u8, 주인 Entity id u16, x·y·z int16, 끝 Tick u32, 대상 id u16 = 16B) + Waypoint 수 + Waypoint × (주인 u16, x·y·z int16 = 8B) = 최대 3 + 8 × 16 + 4 × 8 = 163B. 팀 표시가 바뀐 Tick 끝, Join·Resume, 경기 시작·끝(빈 목록), 라운드 리셋(남은 표시가 있었으면 빈 목록) |
| ContainerStates | S→C(전원) | ReliableOrdered | Phase 16 D3. 생성 마스크 u64 + 열림 마스크 u64(bit i = `LootContainers.All[i]`, 각각 u32 두 개 Little Endian) = 17B. 바뀐 Tick 끝(경기 시작·열기·라운드 리셋), Join·Resume. Loot 내용은 보내지 않는다 |
| ProjectileSpawned | S→C(전원) | ReliableOrdered | Phase 17 D7. Id u16(1–65535, 0 없음), Kind u8(1 Grenade, 2 Rocket), OwnerId u16, Position, Velocity, StartTick u32 = 34B. 발사·던지기 때, Join·Resume 때 살아 있는 투사체마다(지금 상태, StartTick = 마지막 Tick) |
| ProjectileState | S→C(전원) | ReliableOrdered | Phase 17 D7. Id, Position, Velocity(0 = 정지), Tick u32 = 31B. 수류탄이 튕기거나 멈춘 Tick에만 |
| ProjectileExploded | S→C(전원) | ReliableOrdered | Phase 17 D7. Id, Position, Kind = 16B. 폭발 Tick. 경기 시작·라운드 리셋·경기 끝(Finished)에 지워지는 투사체는 보내지 않는다 |
| SupplyDrops | S→C(전원) | ReliableOrdered | Phase 16 D7. 수 u8 + Supply Drop × (id u8, 상태 u8: Falling 0 / Landed 1 / Opened 2, x·z·착지 높이 float, 시작 Tick u32, 착지 Tick u32 = 22B) = 최대 2 + 4 × 22 = 90B. 생성·착지·열림이 있었던 Tick 끝, 경기 시작·라운드 리셋(빈 목록), Join·Resume. Snapshot에는 싣지 않는다 |
| WorldSound | S→C(소리 위치 30 m 안의 살아 있는 다른 플레이어, 죽은 사람·관전자는 모두) | Unreliable(채널 0) | Phase 18 D7. Kind u8(HarvestHit 0 / HarvestDestroyed 1), SourceId u16(소리를 낸 플레이어), Position = 16B. 채집 휘두르기가 채집 대상을 맞혔을 때 |
| VehicleStates | S→C(받는 사람 몸에서 120 m 안의 차량 + 탄 차량, 죽은 사람·관전자는 모두) | Unreliable(채널 0) | Phase 19 D4. 헤더 10B(ServerTick u32, AckInputSeq u32 = 받는 사람의 것, Count u8) + 기록 19B × 최대 8(Id u8, State u8, Driver u16, Passenger u16, Position int16×3 1/256 m, Heading u16, Speed int16 1/256, Steer i8, Health u16). Snapshot Tick마다 |
| StatsResponse | S→C(요청한 사람) | ReliableOrdered | Status(0 Ok, 1 NoRecord, 2 Unavailable, 3 Busy), 요약(Matches, Wins, Kills, Deaths, Damage, SurvivalSeconds, 각 u32, 서버가 자른다), Count 0–10, 행(EndedUnixSeconds u32, Round u32, Players, Placement(0 = 순위 없음), Kills u16, Damage u32, SurvivalMs u32 = 20B) × Count, 최신순. Ok가 아니면 요약 0, 행 없음. 최대 227B |

- Snapshot 헤더 13B(`Part`, `PartCount` 포함) + 수신자 블록 14B = 27B(Phase 12. 그 전에는 블록 6B로 19B), 엔티티 13B(Phase 12에서도 그대로. 이동 모드는 빈 `Flags` 비트에 넣었다). LiteNetLib은 Sequenced 패킷을 분할하지 않으므로 패킷 하나가 `MaxPacketSize` 1200B 이내여야 한다 → 패킷당 최대 90명 = 27 + 13 × 90 = 1197B(`PacketTests`가 고정, 3B 여유), 50명 = 27 + 13 × 50 = 677B(Phase 7의 1167B에서 -42.0 %. Phase 8~11은 669B). 수신자 블록을 읽을 때 기력이 가득(10000)보다 크면 헤더 읽기가 실패한다(범위 밖 블록 거절). 한 경기 최대 100명(`MaxSnapshotEntities`), Snapshot은 최대 2패킷(`MaxSnapshotParts`), `MaxPlayers ≤ 100`(기본 16, 시작 시 검증). 서버는 payload를 한 번 쓰고 수신자마다 AckInputSeq와 수신자 블록만 덮어쓴다(`WorldSnapshotHeader.PatchRecipient`).
- **Snapshot 분할과 양자화(Protocol v7, Phase 8):**
  - 분할: 플레이어 목록을 90명씩 나눠 Part마다 패킷 하나를 보낸다(91–100명이면 90 + 나머지, 2패킷). 패킷마다 같은 Tick·Ack·수신자 블록을 가진 완전한 헤더에 `Part`, `PartCount`가 있다. 읽을 때 `Count ≤ 90`, `1 ≤ PartCount ≤ 2`, `Part < PartCount`를 검증한다(어기면 읽기 실패).
  - 양자화: 위치 x·y·z와 VelocityY는 부호 있는 16비트 1/256 단위(±128 m, ±128 m/s, 맵은 ±80 m), Yaw는 16비트로 360°를 나눈다. 최대 오차는 축마다 반 단위(약 0.002 m)다. 범위 밖 값은 잘라 넣고 NaN·무한대는 0이다. 구조체 필드는 float 그대로이고 Write/Read에서만 바뀐다(`SnapshotEntity.Quantize`). 내 엔티티도 같은 값을 받고, 오차가 재조정 허용 오차(0.01 m)보다 작아 보정이 일어나지 않는다(Client EditMode 테스트로 고정). 박스 윗면과 지형 꼭짓점은 1/256의 배수라 그대로 전달된다. 실제 예측 불일치로 재조정할 때는 Client가 양자화된 서버 상태에서 다시 시작하므로, 다음 보정까지 재적용한 예측에 축마다 최대 약 0.002 m의 오차가 실릴 수 있다(허용 오차 0.01 m 이내).
  - 수신 쪽 규칙: 패킷마다 독립적으로 적용한다(재조립·대기 버퍼 없음). 한 패킷을 잃으면 그 안의 플레이어가 그 Tick의 표본 하나를 못 받을 뿐이고 보간이 흡수한다. 원격 플레이어 제거는 Snapshot에 없다는 이유가 아니라 `PlayerDespawned` 이벤트로 한다. 봇·테스트 Client는 같은 Tick의 패킷을 더하고 새 Tick이 오면 처음부터 다시 모은다.
- Buttons(u16)는 알려진 비트(`PlayerInputPacket.KnownButtons`)만 남기고 나머지는 버린다. Phase 12까지는 Jump, Sprint, Fire, Reload, Slot1, Slot2, Slot3, Interact, Drop, UseMedkit, UseShieldCell, Crouch = 0x0FFF였고, Phase 17의 `ThrowGrenade`로 16비트가 모두 알려진 비트(0xFFFF)가 됐다(아래 표). 입력 패킷은 최대 2 + 3 × 30 = 92B다.

  | 비트 | 값 | 버튼 | 비고 |
  |---|---|---|---|
  | 0–10 | 1–1024 | Jump, Sprint, Fire, Reload, Slot1–3, Interact, Drop, UseMedkit, UseShieldCell | Phase 4까지 |
  | 11 | 2048 | `Crouch` | Phase 12. 누르고 있는 상태(토글은 Client가 만든다). 뛰어내리기·글라이더·Vault는 Jump, 문은 Interact를 다시 쓴다 |
  | 12 | 4096 | `ToolHarvest` | Phase 13. F(누름). 알려진 비트는 0x3FFF가 된다 |
  | 13 | 8192 | `ToolBuild` | Phase 13. Q(누름). 건축 모드에서는 Fire가 배치 대신 아무것도 쏘지 않고, 배치는 `BuildRequest`로 간다 |
  | 14 | 16384 | `InteractHeld` | Phase 14 D7. E가 눌려 있는 동안 매 입력에 켠다(누르고 있는 상태). 소생·재투입은 이 비트가 오는 동안만 이어진다. 줍기·문은 `Interact`(누름) 그대로. 알려진 비트는 0x7FFF가 된다 |
  | 15 | 32768 | `ThrowGrenade` | Phase 17 D9. 키 6(누름). 서버는 누름 키(`Match.EdgeButtons`)로 다뤄 쥐고 있어도 한 번만 던진다. 알려진 비트는 0xFFFF(u16 전부)가 된다. Phase 19 차량은 기존 비트를 다시 쓴다 |
- 이 표의 패킷 크기(`ItemCatalog` 284B(Phase 16까지 219B), `WorldItems` 952B, `ItemSpawned` 20B, `InventoryState` 28B(Phase 4의 22B에서 Phase 14 카드 수, Phase 17 Shells·Rockets·수류탄이 더해졌다), 입력 패킷 92B, `MatchState` 11B, `ZoneState` 36B, `MatchResult` 6B, `StatsResponse` 227B 등)는 모두 PacketId 1B를 포함한 전체 바이트 수다. 새 패킷은 모두 1200B 이하다(`ItemPacketTests`, `MatchPacketTests`가 고정). Snapshot 크기는 위 Snapshot 항목을 본다(Phase 7까지는 50명 1167B, v7은 669B, v10은 677B). Phase 12의 `TransportRoute` 29B, `DoorStates` 2B, `PlayerRespawned` 20B, `PlayerDied` 7B도 `TraversalPacketTests`·`MatchPacketTests`가 고정한다. Zone 원은 Snapshot에 싣지 않는다(시작·끝 값과 Tick으로 양쪽이 같은 식으로 보간한다).
- Client가 "누구를 맞혔다"고 보내는 필드는 없다. 명중은 서버가 조준 방향으로 판정한다.
- Join 결과: Resumed는 끊겼던 참가자가 같은 Entity로 돌아온 것이다("끊기와 재접속"). 늦은 합류와 같은 전체 상태가 이어진다. MatchFull이면 응답만 보내고, 그 연결은 Join한 것으로 치지 않는다. 1초 뒤(응답이 먼저 나가도록) 코드 없이(`None`) 끊는다. Client는 Join 실패를 받으면 자동 재접속을 멈춘다(Phase 10). 이미 참가한 peer의 중복 Join은 서버 Match에 도달하지 않는다(아래 Validation).

## 접속 순서

1. Client `Connect` → 연결 요청 데이터 전송(v19 `ConnectRequestData`: version u16 ‖ flags u8(bit0 `HasCookie`, bit1 `HasResume`) ‖ [쿠키 16B, `HasCookie`일 때] ‖ 세션 키 blob(u16 길이 = 256 ‖ 256B) ‖ DevPlayerId ‖ [Resume nonce u32 ‖ 증명 16B, `HasResume`일 때]. 모르는 flag 비트·길이가 256이 아닌 blob·잘린 증명·남는 바이트는 `BadRequest`). 첫 요청에는 쿠키가 없다. blob은 모든 요청에 있다.
   - **세션 키(리뷰 수정 B1·B2):** Client는 접속마다 32B 세션 키 K를 `RandomNumberGenerator`로 만들고 서버 공개키(`RSA.FromXmlString`, 개발용은 `DevServerPublicKey.Xml`·Unity `Resources/ServerPublicKey.txt`)로 RSA-OAEP-SHA1 암호화해 blob으로 보낸다. 그 K로 `SessionKeys`(Client 쪽)를 만들어 인증 계층에 넣은 **뒤** `Connect`한다(쿠키 재시도도 같은 K). 방향별 키 `K_c2s = HMAC(K, "c2s")`, `K_s2c = HMAC(K, "s2c")`, Resume 키 `K_resume = HMAC(K, "resume")`.
   - **데이터그램 인증(리뷰 수정 B3, `SessionAuth`):** 모든 데이터그램(연결 요청, LiteNetLib의 Ack·Ping·끊기 포함) 끝에 꼬리 20B = counter u32 LE ‖ HMAC-SHA256(K_방향, payload ‖ counter)[0..16]. 키를 가진 쪽은 봉인하고 검증한다(64칸 재전송 창, MAC이 맞은 것만 창을 움직인다). 키가 없는 쪽은 0 꼬리를 붙이고, 받은 꼬리는 검증 없이 벗긴다: 서버는 Accept 전(연결 요청, 쿠키·거절 응답)에 키가 없다. Client·봇·테스트 Client는 첫 데이터그램이 열릴 때까지 열리지 않는 것을 검증 없이 벗기고(서버의 쿠키 응답은 0 꼬리로 온다), 그 뒤로는 버리고 센다. 서버는 키가 있는 endpoint의 틀린 꼬리를 버리고 Health `authDrops`로 센다(끊지 않는다). 암호화는 하지 않는다(무결성·재전송 방지만).
   - **쿠키 단계(리뷰 수정 A3, SEC-4):** 서버는 쿠키 없는 요청에 `RejectForce`(서버에 임시 peer 없음, 재전송 없음)로 16B 쿠키를 돌려준다. 쿠키 = HMAC-SHA256(서버 시작 때 만든 32B 비밀, 주소 ‖ 포트 ‖ 30초 창 번호)의 앞 16B(`ConnectCookie`). Client·봇·테스트 `HeadlessClient`는 거절 데이터가 16B면 같은 요청을 쿠키와 `HasCookie`를 넣어 **바로 한 번** 다시 보낸다(1B면 지금처럼 `RejectReason`). 지금 창이나 바로 전 창의 쿠키만 통과하므로 창 경계에서 받은 쿠키도 쓸 수 있다. 위조 출발지는 쿠키를 받지 못해 아래 Token·연결 수·(묶음 B) 복호 비용을 쓰게 하지 못한다. 틀린 쿠키는 Health `rejects cookie`로 세고 새 쿠키를 돌려준다(쿠키가 낡은 Client가 다시 시도할 수 있게). 쿠키 없는 첫 요청은 정상 흐름이라 `cookieChallenges`로 따로 센다. 접속마다 왕복 1번이 는다.
2. Server `OnConnectionRequest` 검사 순서: 정지·꽉 참(`ServerFull`) → 버전(첫 필드라 v18 Client도 `VersionMismatch`를 받는다) → 형식·이름(`BadRequest`) → 쿠키 → IP별 빈도·벌점·동시 연결 수 → 전역 수락 빈도 → 세션 키 복호(서버 개인키, 실패하거나 32B가 아니면 `BadRequest`. RSA는 쿠키와 빈도를 통과한 요청만 쓴다. 리뷰 B 1·2차: 같은 IP 칸에서 60초 안 세 번째 복호 실패부터 그 칸에 60초 벌점을 준다(`penalties`). 한 주소가 쓰레기 blob으로 수신 스레드의 복호를 계속 시키지 못하게 하면서, 잘못 설정된 Client 하나가 첫 시도로 같은 NAT 주소 전체를 막지 않게) → 그 endpoint에 키 등록 → Accept(수락 응답부터 봉인된다). 모든 거절은 `RejectForce`다(1B `RejectReason` 또는 16B 쿠키). Accept 뒤 `PeerState`(그 IP 칸 번호, 세션 키, Resume 증명 포함)를 `peer.Tag`에 설정하고 그 칸의 연결 수를 올린 다음 `Connected` 제어 메시지를 Control 채널에 쓴다. LiteNetLib이 `Accept()` 안에서 `OnPeerConnected`를 동기 호출하는데 그 시점엔 Tag가 아직 없으므로, `OnPeerConnected`에서는 아무것도 하지 않는다. 끊기면(`OnPeerDisconnected`) 그 칸의 연결 수를 내리고 키를 은퇴시킨다(지우지 않는다: 그 이벤트 뒤에도 LiteNetLib가 끊기 패킷을 다시 보내므로 봉인이 이어져야 Client가 끊김 코드를 받는다. `DisconnectTimeoutMs + 1000` ms 뒤 지운다. 은퇴 중에 같은 endpoint가 새로 접속하면 새 연결 요청(LiteNetLib `ConnectRequest`, 첫 바이트 하위 5비트 = 6)만 검증 없이 벗기고, 새 키가 덮어쓴다. 리뷰 B 1차: 은퇴 항목에서 열리지 않는 다른 데이터그램은 버린다(위조 `ShutdownOk`가 끊기 패킷 재전송을 일찍 끝내지 못하게). 리뷰 B 2차: 이것은 `authDropsRetired`로 따로 세고, `authDrops`(열린 연결의 키)는 정상이면 0으로 남는다. 그래서 은퇴 시간 안에 같은 포트로 다시 접속하면, 쿠키 거절(LiteNetLib Disconnect 패킷)에 대한 새 Client의 `ShutdownOk` 하나가 버려지고 세어진다. 거절된 요청에는 LiteNetLib peer가 없어 그 응답을 기다리는 것이 없다).
3. Client `OnPeerConnected`에서 `JoinMatchRequest` 전송.
4. Server가 `JoinMatchResponse` → `WeaponCatalog` → `ItemCatalog` → `WorldItems`(분할, 아이템이 없으면 보내지 않는다. 운영 서버는 경기 전에 월드가 비어 있다) → `InventoryState` → 새 플레이어에게 전원의 `PlayerSpawned`, 기존 플레이어에게 새 플레이어의 `PlayerSpawned` → (`DevRespawn`이 꺼져 있으면) `MatchState` → `ZoneState` → (Phase 12) `DoorStates` → (Phase 13) `HarvestStates` → (Phase 16) `ContainerStates` → `SupplyDrops` → (Phase 17) 살아 있는 투사체마다 `ProjectileSpawned` → (공중 투입 경기 중이면) `TransportRoute` → (경기 중이면) 본인의 `PlayerDied`(관전). Phase 13 이후 건설 채널(1)로 `BuildCatalog` → reset `BuildSync`, Phase 14 이후 팀이 있으면 `TeamState`·`RebootStations`(진행 중이면 `ChannelState`), Phase 15 이후 팀이 있으면 `TeamMarkers`가 이어진다(세부 순서는 `Squad.md`, `Map.md`).
5. Resume(Phase 10, 리뷰 수정 B4): 경기 중 끊긴 참가자가 유예 안에 같은 DevPlayerId와 맞는 Resume 증명(연결 요청의 `HasResume`, 아래 "재접속 유예")으로 Join하면 `JoinMatchResponse(Resumed, 같은 Entity)`(이름만 같고 증명이 없거나 틀리면 새 플레이어다. 증명이 맞으면 서버가 아직 끊김을 모르는, 연결 중인 같은 이름의 캐릭터도 넘겨받는다) → `WeaponCatalog` → `ItemCatalog` → `WorldItems` → `InventoryState` → 전원의 `PlayerSpawned`(자기 포함, 지금 위치) → `MatchState` → `ZoneState` → (Phase 12: 공중 투입 경기면) `TransportRoute` → `DoorStates` → (Phase 13) `HarvestStates` → (Phase 16) `ContainerStates` → `SupplyDrops` → (Phase 17) 살아 있는 투사체마다 `ProjectileSpawned` → (유예 중에 경기가 끝났으면, 즉 `Finished`이면) 본인의 `MatchResult`. Phase 14·15 이후 `TeamState`·`RebootStations`·진행 중 `ChannelState`·`TeamMarkers`도 다시 보낸다. 경기 끝에 보낸 결과는 연결이 없어 사라졌으므로 다시 보낸다. 다른 플레이어에게는 아무것도 보내지 않는다(떠난 적이 없다).

## 끊기와 재접속 (Phase 10)

설계 근거: `Docs/specs/2026-10-01-phase10-hardening-design.md`.

**끊는 코드(`DisconnectCode`, D1):** 서버가 끊을 때 LiteNetLib 끊기 데이터 1바이트로 보낸다. 끊김과 한 메시지라 순서 문제가 없다. 데이터가 없거나 모르는 값은 `None`이다(`DisconnectCodes.Read`). Client는 `RemoteConnectionClose`의 추가 데이터에서 읽는다.

| 값 | 코드 | 뜻 | Client 자동 재접속 |
|---|---|---|---|
| 0 | `None` | 코드 없음(서버가 이유를 보내지 않았거나 모르는 값) | 안 한다 |
| 1 | `ServerShutdown` | 서버 종료 | 안 한다 |
| 2 | `Kicked` | 잘못된 패킷이 `BadPacketDisconnectThreshold`(20)개 | 안 한다 |
| 3 | `JoinTimeout` | 연결하고 Join하지 않음 | 안 한다 |
| 4 | `InputTimeout` | Join하고 입력을 보내지 않음 | 안 한다 |
| 5 | `ServerError` | Tick이 계속 실패해 경기를 초기화함. 또는 서버 Control 채널이 가득 차 연결·Join을 받지 못함 | 한다 |
| 6 | `Congested` | (Phase 13 최종 리뷰) Join한 연결의 신뢰 대기열(채널 0 + 1)이 512개를 넘은 채 10초 지남. 연결이 게임 트래픽을 받지 못한다. Client 문구 "연결이 너무 느려 끊겼습니다." | 안 한다 |

**재접속 표(Shared `DisconnectCodes.ShouldReconnect(remoteClose, code, networkLoss)`, Client와 봇이 같이 쓴다):**

- 원격 종료(`RemoteConnectionClose`)는 `ServerError`만 다시 한다.
- `Timeout`·`ConnectionFailed`·`HostUnreachable`·`NetworkUnreachable`은 다시 한다.
- 직접 끊기·연결 거절·코드 없는 원격 종료는 다시 하지 않는다.
- 처음 연결이 실패한 것은 사이클을 시작하지 않는다(호출한 쪽이 정한다. Client 동작은 `Client.md`).
- 한 끊김에 최대 `DisconnectCodes.MaxReconnectAttempts` = 3번이다.
- 시도 시각(Client와 봇이 같다): n번째 시도는 끊김을 안 때부터 `DisconnectCodes.ReconnectOffsetSeconds(n)` = 1·3·7초 뒤에 시작한다(간격 1·2·4초, 앞 시도가 실패한 때가 아니라 끊긴 때부터 잰다).
- 자동 시도의 연결 예산: LiteNetLib `ReconnectDelay` 250 ms × `MaxConnectAttempts` 5(`DisconnectCodes.ReconnectRequestIntervalMs`·`ReconnectRequestAttempts`). 한 시도는 (5 + 1) × 250 ms = 약 1.5초 안에 포기한다(측정 1.53초). 기본값(500 ms × 10)이면 약 5.5초 걸려 둘째 시도가 8초쯤, 셋째가 17초쯤으로 밀려 유예(10초)를 넘긴다. 직접 Connect는 기본값을 쓴다.
- 다음 시각이 왔는데 앞 시도가 아직 연결 중이면 그 시도를 버리고 다음 시도를 시작한다. 셋째 시도는 약 8.5초에 끝나 기본 유예 10초 안이다.

**재접속 유예(D2):**

- 대상: 경기 중(`Playing`·`FinalPhase`) 살아 있는 참가자이고, 서버가 끊지 않은 연결(Client 종료·비정상 종료·네트워크 끊김)이다. 기본 `ReconnectGraceSeconds` 10초(0이면 끈다).
- 유예 중: 캐릭터는 그 자리에 남는다. 입력이 없다(0.5초 뒤 정지, 위 "Movement"). 맞으면 죽고 Zone 피해도 받는다. 다른 플레이어에게 `PlayerDespawned`를 보내지 않는다.
- **Resume 증명(리뷰 수정 B4, SEC-2):** 같은 DevPlayerId에 더해, 연결 요청에 `HasResume`과 증명이 있고 그 증명이 맞을 때만 새 연결에 그 캐릭터(위치, 체력, 인벤토리, 순위 상태)를 다시 묶고 위 접속 순서 5번을 보낸다. 증명 = HMAC(그 캐릭터가 마지막으로 Join·Resume한 연결의 `K_resume`, nonce u32 LE ‖ **새 연결의 세션 키 32B** ‖ UTF-8 이름)[0..16], nonce는 그 캐릭터의 지난 nonce보다 커야 한다. 증명이 평문으로 지나가도 새 세션 키에 묶여 있어 다른 연결(다른 세션 키)은 쓸 수 없다(Spec B4의 nonce ‖ 이름보다 강하게 했다). Join이 Ok·Resumed가 될 때마다 서버는 그 플레이어의 Resume 키를 이 연결의 것으로 바꾸고(nonce 0부터), Client는 그 연결의 `SessionKeys.ResumeKey`를 갖고 있다가 자동 재접속에 쓴다. 리뷰 B 1차: Client는 `JoinMatchResponse`를 받아야 새 키를 쓰므로, 서버는 Resumed 때 증명에 쓰인 키를 `PrevResumeKey`(그 nonce와 함께)로 남기고 새 연결의 첫 입력이 수락될 때 지운다. 그 전까지는 그 키로 만든 증명(그 키의 nonce보다 큰 nonce)도 받으므로, 응답이 끊김으로 유실돼도(여러 번이어도) 캐릭터를 되찾는다. 아직 연결 중인 사망·관전 캐릭터를 넘겨받으면 자기 `PlayerSpawned` 뒤에 `PlayerDied`를 받는다(Ok의 관전 입장과 같다). 증명이 없거나 틀리면 새 플레이어(경기 중이면 관전자)이고 유예 캐릭터는 그대로 남는다. 같은 이름으로 연결된 다른 플레이어가 있어도 증명이 맞으면 Resume한다(이름을 먼저 차지해 주인을 막을 수 없다). 새 Client는 Seq를 1부터 센다. 같은 id의 유예 캐릭터가 여럿이면 증명이 맞는 것 중 먼저 끊긴 것이다.
- 나가는 경우: 유예 중에 죽으면 다음 Tick에 나간다(다시 오면 보통의 늦은 합류, 관전자다). 시간이 다 되어도 나간다. 시간이 다 된 경우는 탈락, 사망 Drop, 이탈자 기록으로 보통의 이탈과 같다. 판 재시작(`Closing`)에서 유예 목록을 비운다. 세 경우 모두 유예 만료로 센다(Health `graceExpiries`, Information 로그에 DevPlayerId).
- 유예 중에 경기가 끝나면(`Finished`) 결과 화면 동안은 아직 돌아올 수 있고, 돌아오면 자기 `MatchResult`를 다시 받는다(접속 순서 5번).
- 서버가 끊은 연결(`Kicked`·`InputTimeout` 등 모든 코드)과 경기 밖·사망·관전 상태의 끊김은 유예 없이 바로 나간다.
- 남은 위험: 계정 인증이 없어 전적은 여전히 자기 신고 DevPlayerId에 쌓인다(외부 인증은 다음 단계). 유예 캐릭터 탈취는 Resume 증명이 막는다.
- **끊김을 알기 전의 재접속(리뷰 수정 B4):** Client가 비정상 종료한 뒤 서버가 옛 연결의 끊김을 알기 전(최대 `DisconnectTimeoutMs`)에 다시 접속해도, 증명이 아직 연결 중인 같은 이름 캐릭터와 맞으면 그 캐릭터를 넘겨받는다(`Resumed`, 같은 Entity, 접속 순서 5번). 옛 연결은 Match에서 떼어 내고 1초 뒤 코드 없이(`None`, Join 거절과 같은 경로) 닫는다. 서버가 닫은 것이라 유예가 생기지 않고, 옛 Client는 이미 죽었거나 코드 없는 끊김을 재시도하지 않으므로 재접속 고리가 생기지 않는다. 증명이 틀리면 새 플레이어(관전자)다.

**Timeout(D3, D4):**

- Join Timeout: 연결한 뒤 `JoinTimeoutSeconds`(5초) 안에 Join하지 않으면 `JoinTimeout`으로 끊는다.
- Input Timeout: Join한 peer가 `InputTimeoutSeconds`(10초, 0이면 끔) 동안 `PlayerInput`을 하나도 보내지 않으면 `InputTimeout`으로 끊는다. Join이 입력 하나로 센다. 죽음·관전·대기 중에도 적용한다. Client와 봇은 Join한 동안 늘 입력을 보낸다.
- 둘 다 Game Loop의 Tick 수(`SimHz`)로 잰다. 경기를 초기화해도 이어진다.
- `InputTimeoutSeconds` × 1000은 `DisconnectTimeoutMs` + 2000 이상이어야 한다(시작할 때 검사). 네트워크가 끊기면 입력도 끊긴다. Input Timeout이 LiteNetLib Timeout보다 먼저 오면 서버가 끊은 것(`InputTimeout`)이 되어 유예를 잃기 때문이다.
- 디버거로 Client를 10초 넘게 멈추면 `InputTimeout`으로 끊긴다(재접속하지 않는다). LiteNetLib 스레드는 Pong을 보내 연결은 살아 있어도 입력은 오지 않기 때문이다.

**서버 종료·경기 초기화:** 종료하면 모든 peer를 `ServerShutdown`으로 끊는다. 종료가 시작되면(`Stop`, 또는 경기 초기화가 거듭 실패해 서버를 멈출 때) 새 연결 요청은 `ServerFull`로 거절한다(프로토콜 변경 없음, Client는 거절에 재접속하지 않는다). Tick이 계속 실패하면 경기를 초기화하면서 `ServerError`로 끊는다(`Server.md` "예외 복구").

## Tick

- 서버 Simulation 30Hz(`SimHz`), Snapshot 15Hz(`SnapshotEveryTicks = 2`, `SnapshotHz = SimHz / SnapshotEveryTicks`). `appsettings.json`에서 변경. 값은 `JoinMatchResponse`로 Client에 전달된다.
- Client 렌더는 가변 FPS. 시뮬레이션은 서버 Tick과 같은 고정 스텝(`1/SimHz`)이다.
- 연결 유지: 서버 `DisconnectTimeout = DisconnectTimeoutMs`(기본 5000), `PingInterval = min(1000, DisconnectTimeoutMs / 4)`. 타임아웃은 클라이언트가 보낸 패킷으로만 갱신되므로, 짧은 타임아웃에서도 대기 중인 클라이언트가 pong으로 살아있도록 Ping을 타임아웃당 4번 이상 보낸다. Client는 `DisconnectTimeout = 5000`, 기본 PingInterval(1000).

## Movement

```mermaid
sequenceDiagram
    participant C as Client
    participant S as Server
    C->>C: Step(input seq n) 예측, 히스토리 저장
    C->>S: PlayerInput(n-2, n-1, n)
    S->>S: Tick마다 입력 1개 소비, Step
    S->>C: WorldSnapshot(AckInputSeq = n-k)
    C->>C: ack 상태 비교 → 다르면 서버 상태에서 n-k+1..n 재적용
```

- 내 캐릭터: Client Prediction + Reconciliation (`LocalPlayerPredictor`).
  - 프레임당 누적 시간으로 고정 스텝을 0~N회 실행(누적은 0.25초로 제한, 히치 후 폭주 방지). 스텝마다 Seq를 1 올리고 입력·결과를 64칸 링 히스토리에 저장한다.
  - 패킷은 프레임당 1개, 최신 입력 최대 3개(`MaxInputsPerPacket`)만 담는다. 스텝이 여러 번 돈 프레임에서는 그보다 앞선 입력이 전송되지 않을 수 있다.
  - 점프는 프레임의 마지막 예측 스텝에서 소비한다(항상 최신 3개 패킷에 들어가도록).
  - Phase 12: 예측하고 보정하는 상태는 `MoveState` 전체다(모드, 수평 속도, 기력, Tick 값, 기진). 이동 모드별 규칙과 수치는 `Movement.md`다. 서버 상태는 Entity(위치, VelocityY, 모드, 달리기·기진 플래그)와 수신자 블록(수평 속도, 기력, `ModeTicks`, `EnergyDelayTicks`)에서 합쳐 만든다.
  - Snapshot 수신 시 ack 시점의 예측과 서버 상태를 비교한다. 위치·VelocityY·수평 속도는 0.01(양자화 오차보다 크다), 모드·기력·`EnergyDelayTicks`·`ModeTicks`·기진은 정확히 같아야 한다. 다르면 서버 상태에서 ack 이후 입력을 재적용한다. 화면 위치는 오차를 렌더 오프셋으로 유지하며 감쇠(`exp(-10·dt)`)시키고, 보정량이 2m를 초과하면 오프셋 없이 즉시 스냅한다. ack가 0이면 아직 서버가 입력을 처리하지 않았다는 뜻이므로, 보낸 입력이 없을 때만 서버 상태로 교체하고 이미 예측 중이면 무시한다(Phase 12: 단, 서버의 모드가 예측과 다르면 서버 상태로 맞춘다. 공중에서의 재접속 D16). ack가 보낸 입력보다 크거나 히스토리(64)를 벗어나면 서버 상태로 바로 교체한다.
  - 서버가 보낸 값이 NaN/Infinity이면 그 엔티티는 무시한다(예측 상태 오염 방지).
- 다른 플레이어: Snapshot 보간, 2 Snapshot 간격(`2 / SnapshotHz` ≈ 133ms) 과거를 렌더 (`RemotePlayerInterpolator`, `ServerClock`). 플레이어당 8개 샘플 링버퍼, 최신 샘플 이후는 외삽 없이 마지막 위치 유지. Phase 12: 샘플마다 이동 모드·달리기·기력 소진도 저장하고, 렌더 Tick 이하의 가장 새 샘플 것을 자세·조준 Collider 높이에 쓴다(서버 `PositionHistory.Sample`과 같은 규칙, 최종 검토 A1). 비유한(non-finite) 샘플은 버린다. `ServerClock`은 렌더 Tick이 뒤로 가지 않게 한다.
- 입력이 제때 오지 않으면 서버는 직전 입력을 반복(점프 제외)하고 ack는 올리지 않는다 → 패킷 손실 시 작은 보정이 생길 수 있다. 입력 없는 Tick이 `SimHz / 2`(0.5초)를 넘으면 이동 입력 0(직전 Yaw 유지, 버튼 없음)으로 멈춘다. 멈춘 클라이언트가 Disconnect 전까지 계속 걷지 않게 하기 위해서다. 새 입력이 오면 다시 반복 허용 구간이 시작된다.

## 이동 충돌 (Phase 1)

서버(`Match.Tick`)와 예측(`LocalPlayerPredictor`)은 같은 `MovementSimulation.Step(ref state, input, dt, 세계, GameMap.Terrain)`를 호출한다. 세계는 `GameMap.Boxes` 뒤에 닫힌 문을 붙인 배열이다(Phase 12. 서버 `DoorSet.World`, Client `PredictedDoors.World`. `Map.md` "문"). 탑승 중에는 `Step` 대신 `DropTransport.Ride`다(`Movement.md` "수송기"). 지형은 Shared 상수 `GameMap`: 높이 격자(`HeightField`) + 축 정렬 박스이고, 캐릭터는 AABB(반폭 0.35 m, 높이 1.8 m)다.

Step 순서:
1. 입력 검증(비유한 값 0, 이동 길이 ≤ 1), Yaw·수평 속도 계산.
2. 박스와 `Skin`(0.001 m)보다 깊게 겹치면 가장 적게 겹친 축으로 밀어낸다(동률은 -X, +X, -Z, +Z, +Y, -Y 순. 아래로는 바닥 위일 때만). 박스를 하나씩 한 번만 밀어낸다.
3. 접지 판정은 상태 없이 매 Step: `VelocityY ≤ 0`이고 발밑 ±0.02 m(`GroundProbe`) 안에 바닥이나 박스 윗면이 있으면 Y를 그 면에 정확히 맞춘다. 접지면 `VelocityY = Jump ? 7 : 0`, 아니면 중력.
4. X → Z → Y 축 분리 Sweep. 각 축에서 나머지 두 축이 겹치는 박스 중 진행 방향 가장 가까운 면까지(Skin만큼 띄움) 이동한다(바닥 y=0은 Skin 없이 정확히 0). 거리에 상관없이 모든 앞쪽 박스를 보므로 빠른 낙하도 판을 뚫지 않는다. Y가 막히면 `VelocityY = 0`.

위 순서는 지상 이동(`Ground`·`Crouch`·`Slide`)의 것이다. Phase 12에서 `Step`은 `MoveState.Mode`로 지상, `Vault`, 공중(`Freefall`·`Glide`)으로 갈라지고, 수평 속도·기력도 상태로 이어진다(`Movement.md`). 접지 여부는 여전히 상태에 저장하지 않고 매 Step 다시 계산한다. 박스 위에 서 있으면 Y가 윗면 값으로 고정되어 예측과 서버가 같은 값을 내고, 재조정 떨림이 없다. 서버 보정으로 예측이 박스 안에 들어가도 재적용 첫 Step이 밀어낸다. 점프 최고점은 30 Hz 이산 적분으로 약 1.34 m(연속식은 1.225 m)라 1 m 박스는 점프로 오르고 1.5 m 박스는 점프로 못 오른다(Phase 12부터 1.5·2 m 박스는 Mantle로 오르고 1 m 박스는 달리며 Hurdle로 넘는다. `Movement.md`). 접지 판정이 부동소수 오차(약 1e-7 m)를 접지로 보므로 점프가 Phase 0보다 한 Tick 일찍 나갈 수 있다. 서버와 Client가 같은 판정을 쓰므로 서로 어긋나지 않는다.

지형(Phase 6 D4): 모든 경사가 0.6 이하라 지형은 수평 이동을 막지 않는다. 바닥 판정·Y Sweep·밀어내기의 바닥은 발밑 지형 높이다. 수평 이동 뒤 발이 지형보다 낮으면 올리고, 이번 Step을 땅에서 시작했고 점프하지 않았으면 `이동 거리 × MaxSlope + GroundProbe` 이내의 내리막에 Y Sweep으로 붙인다(박스 윗면에서 멈춘다).

`GameMap` 규칙(`GameMapTests`가 검사, 자세한 내용은 `Map.md`): 박스 128개 이하, 중앙 광장 12 m 비움, 두 박스 사이 틈은 0.25 m 이하이거나 캐릭터 폭 이상, **어떤 두 박스도 옆으로 맞닿거나 겹치지 않는다(위로 쌓는 것은 허용)**, 박스 아래는 평지. 박스를 하나씩 밀어내므로 면을 공유하는 두 박스 사이에서는 캐릭터가 갇힐 수 있기 때문이다. 외곽 벽 모서리는 0.25 m 틈으로 두는데, 캐릭터(0.7 m)보다 좁아 맵은 막힌 채로 유지된다(`Character_CannotPassThroughAnyCornerSlit`).

## 전투 (Phase 3)

발사는 입력 명령에 실린다(Phase 3 D1): Fire 비트 + 조준(AimYaw, AimPitch) + ViewTick. 따라서 입력의 중복 전송·Seq 중복 제거·Tick당 1개 규칙을 그대로 따르고, 입력보다 빨리 쏠 수 없다.

- **조준(Phase 3 D2):** Client는 카메라 광선이 맞은 점(원격 플레이어는 서버 판정 상자 크기의 `BoxCollider`가 있어 조준점이 몸 위에 온다)과 캐릭터 눈(발 + 1.6 m. Phase 12: 웅크리기·슬라이드는 발 + 1.0 m, 서버 `CombatRules.EyeHeightOf`와 Client `AimSolver`가 같다)을 이어 Yaw/Pitch를 구한다. 눈의 발 위치는 렌더 위치가 아니라 입력마다 그 입력의 Step을 마친 예측 위치다(`LocalPlayerPredictor.SetAim`). 서버가 그 입력을 처리한 뒤의 위치이기 때문이다. 한 프레임에 Step이 여럿이면 앞선 입력은 각자 자기 위치에서 조준한다. 규약은 카메라와 같다(Yaw 0 = +Z, Yaw 90 = +X, 양의 Pitch = 아래). 서버는 같은 눈에서 같은 방향으로 쏘므로 어깨 카메라 시차가 있어도 조준점이 가리키는 곳을 맞힌다.
- **무기(Phase 3 D4, D5):** 수치는 서버 `weapons.json`에만 있다(Vesper AR: 20 / 3 Tick / 30발 / 60 Tick / 150 m / 자동 / Medium 탄, Kestrel LR: 90 / 38 Tick / 5발 / 75 Tick / 300 m / 단발 / Heavy 탄. 1.25 s × 30 Hz = 37.5는 38로 반올림. Phase 4에서 Wisp SMG가 추가됐다: "인벤토리와 Loot"). 시작 시 검증하고 틀리면 서버가 뜨지 않는다. Join 직후 `WeaponCatalog`로 Client에 간다. Slot1/2/3 = 무기 목록이 아니라 인벤토리 칸 0/1/2다(칸에 든 무기는 줍기로 정해진다).
- **서버 Tick 순서(`Match.Tick`, Phase 19 + 리뷰 수정 기준):**
  1. 유예가 끝난 캐릭터 정리(`ExpireGrace`) → 경기 흐름 전환(경기 시작·판 재시작은 이 Tick 안에서 끝난다. `Starting`에 들어서면 투사체를 지운다) → (경기 중) Zone 진행·피해 → (경기 중) Supply Drop 생성·착지 → (`DevRespawn`만) 부활 시각이 된 플레이어 부활 → Loot 재생성(`DevRespawn`만 실제로 채운다) → (경기 중) 재투입 카드 만료 → (`MaterialPickupEveryTicks`마다) 자원 자동 줍기 → 건설·편집 요청 처리(이번 Tick에 지은 벽이 이동·사격을 막는다) → 투사체 이동·폭발(`UpdateProjectiles`).
  2. 플레이어마다(`TickPlayer`, 플레이어별 격리): 입력 1개(죽어 있으면 Seq만 확인 응답, 이동·발사 없음, 중력도 없음) → (기절이면) 출혈 → (Phase 19: 차에 탔으면 `TickSeated`(차량 입력, E로 내리기)만 하고 끝) → 이동(탑승 중이면 `Ride`, 아니면 `Step`과 그 결과 처리: 문 밀치기, 이동 이상 검사, 낙하 피해) → 재장전 완료 확인 → **실제로 받은 입력이고 행동할 수 있는 모드일 때만** 행동(`ProcessActions`: 사용 취소 → 도구 선택 → 칸 선택(교체 대기 시작) → 버리기 → E(문·Container·차량·줍기, 소생·재투입 대상이 닿으면 하지 않는다) → 도구별 재장전·발사 또는 채집 휘두르기 → 수류탄 → 사용 시작) → 소생·재투입 진행(`InteractHeld`) → (매 Tick) 사용 완료. 실패한 플레이어는 루프 뒤에 내보낸다.
  3. 차량 이동·충돌·좌석 갱신(`UpdateVehicles`, 운전자의 이번 Tick 입력) → 지지를 잃은 조각 붕괴(한 번의 검색) → (경기 중) 종료 판정 → `ServerTick++` → 바뀐 것 전송(인벤토리 → `MatchState`·`ZoneState` → `DoorStates` → `HarvestStates` → `ContainerStates`·`SupplyDrops` → 자원 → `TeamState` → `RebootStations` → 팀 표시 → 건설 사건) → 모든 플레이어 위치와 이동 모드를 History에 기록 → 차량 자세 기록(`RecordVehicles`) → (Snapshot Tick이면) Snapshot과 `VehicleStates`. (실제로 받은 입력의) 사용 취소·칸 선택·버리기·줍기·재장전·발사·사용 시작은 이동을 마친 뒤의 모드가 지상·웅크리기·슬라이드일 때만 한다(Phase 12 D12). 막힌 동안에도 Fire를 누른 상태(`FireHeld`)는 입력을 따른다. 누른 채 착지한 반자동 무기는 새로 눌러야 쏜다(최종 검토 C9, Client `WeaponState`도 같다). 누락 입력 반복(Phase 0 유예)은 이동만 반복하고 줍기·버리기·사용·교체·재장전·발사는 하지 않는다. 줍기·버리기·사용은 "인벤토리와 Loot"에 있다.
- **무기 규칙(Phase 3 D14, 서버 Tick 기준):** 교체는 Slot 비트가 하나만 켜졌을 때(둘 이상이면 무시), 교체하면 재장전 취소. 재장전은 탄창이 가득이 아니고 그 무기 탄약 종류의 보유량이 1 이상일 때만 시작하고, 끝나면 보유량에서 탄창으로 옮긴다(보유량 0이면 시작하지 않는다). 발사는 칸이 비어 있지 않고, 탄 > 0, 재장전 중 아님, `now ≥ 그 칸의 NextFireTick`, 조준 각이 유한할 때만. 리뷰 수정 C2: 손에 든 무기가 바뀌면(칸 교체, 손으로 줍기·교환, 버리기) 그 무기의 `EquipTicks`(`weapons.json` `equipSeconds`, 기본 0.4 s = 12 Tick) 동안 쏘지 않는다(`SwitchReadyTick`). Client `WeaponState`도 카탈로그의 같은 값으로 기다린 뒤 발사를 예측한다. 단발은 Fire를 새로 누른 입력에서만. 마지막 탄을 쏘거나 빈 탄창으로 쏘려 하면 자동 재장전(보유량이 있을 때). 탄창과 발사 간격은 칸별이다. Snapshot의 `ReloadRemainingTicks`는 재장전 중이면 마지막 Tick에도 1 이상이다(0은 "재장전 아님").
- **판정(Phase 3 D7):** 눈에서 조준 방향으로 사거리까지, 맵 박스와 닫힌 문(Phase 12. 열린 문은 없는 것과 같다), 지형 삼각형(광선이 지나는 칸만 검사, `HitScan.TraceTerrain`), y=0 평면(격자 밖으로 나간 광선용. 박스는 slab 교차), 그리고 쏜 사람을 뺀 살아 있는 플레이어의 이동 AABB(반폭 0.35 m, 높이 1.8 m. Phase 12: 그 사람의 모드로 정한다. 웅크리기·슬라이드는 1.2 m, `Transport` 탑승자는 맞지 않는다) 중 가장 가까운 것에 맞는다. 머리 판정은 없다. Phase 17부터 탄 퍼짐은 서버가 정한다: 조준 방향을 `spreadDegrees` 원뿔 안에서 결정적 해시(SplitMix64, 쏜 사람 Entity id·Tick·광선 번호·경기 비밀. `WeaponSpread`)로 흔든다. 반동(`recoilDegrees`)은 Client 카메라 연출만이고 서버 판정에 쓰지 않는다(아래 "무기와 투사체 (Phase 17)", `Weapons.md`).
- **Lag Compensation(Phase 3 D6):** 플레이어마다 32칸 위치 링(`PositionHistory`)에 매 Tick 끝 위치와 이동 모드(Phase 12. 되감은 시점의 모드로 맞는 높이를 정한다)를 기록한다. 문은 되감지 않는다. 사격은 지금의 문 상태로 추적한다(고정 박스와 같다). 발사 판정은 다른 플레이어를 ViewTick 위치로 되감는다(두 기록 사이 보간). ViewTick은 `[최신 Tick − 허용, 최신 Tick]`으로 잘리고, 최신 Tick보다 크면 최신 Tick이다. 허용은 리뷰 수정 C3부터 사수마다 RTT로 정한다: clamp(RTT × SimHz / 1000 + 2 × SnapshotEveryTicks + 2, 2, `MaxRewindTicks`)(아래 "Validation"의 "되감기 RTT 제한"). 상한 `MaxRewindTicks`는 0.4 s(30 Hz에서 12 Tick, SimHz가 높으면 링 `Capacity − 1` = 31 Tick으로 한 번 더 잘린다)이고, RTT를 모르는 시험(`rttOf` 없음)은 이 고정 상한을 쓴다. **리뷰 수정 D2(STB-1):** ViewTick은 uint Tick이다(같은 4 B, Protocol v19). float는 30 Hz에서 약 6일 뒤 정수 Tick을 잃었다. Client가 아직 그린 것이 없거나 되감기를 원하지 않으면 `uint.MaxValue`("지금")를 보내고 서버는 최신 Tick으로 자른다(잘림으로 세지 않는다). 소수 Tick(두 기록 사이 보간 위치)은 더 이상 오지 않는다. 기록이 모자라면 가장 오래된 기록을 쓴다. Join·부활 때 History를 새로 시작하므로 되감기가 시체 위치에 닿지 않는다. 쏜 사람 자신은 되감지 않는다. 원격 플레이어는 약 133 ms(4 Tick) 과거로 보이고 입력 Drain이 1 Tick을 더 쓰므로, 30 Hz·Snapshot 2 Tick마다에서 허용은 6 Tick + RTT × 0.03 Tick이고 RTT 약 200 ms에서 상한 12 Tick에 닿는다(RTT 20 ms → 6, 100 ms → 9, 200 ms → 12). 왕복 지연이 약 200 ms를 넘으면 빠르게 움직이는 상대를 빗나갈 수 있고, 벽 뒤로 숨은 뒤 최대 약 400 ms(상한) 동안 맞을 수 있다(D6의 비용. 지연이 작은 사수는 그만큼 짧다).
- **피해·사망·부활(Phase 3 D8, D9):** Health 최대 100, Shield 최대 100(Phase 4부터 시작 Shield는 0이고 채우려면 Shield Cell을 쓴다). 피해는 Shield부터, 남은 만큼 Health, 0 아래로 내려가지 않는다. Health가 0이 되면 사망: `PlayerDied`(전원), 판정 대상에서 빠지고 입력은 무시되며, 진행 중이던 재장전과 회복은 취소되고 가진 것은 떨어진다("인벤토리와 Loot"). 부활은 `DevRespawn` 서버(Phase 3·4 테스트 아레나)에서만 한다: `SimHz × 3` Tick 뒤 `Match.SpawnPosition(id)`에서 Health 100·Shield 0·빈손(빈 인벤토리, 칸 0)으로 부활하고 `PlayerRespawned`(전원). 운영(`DevRespawn = false`)에서는 경기 중 사망이 영구적이다(Phase 5 D4. `BattleRoyale.md`). 부활 때 누락 입력 반복(LastInput·MissedTicks)도 새로 시작해 이전 삶의 이동을 되풀이하지 않는다. `DamageTaken` → `PlayerDied` → `PlayerRespawned`는 같은 ReliableOrdered 채널이라 이 순서로 도착한다.
- **Client 예측 범위(Phase 3 D12):** 내 발사 연출은 `WeaponState`(서버 규칙의 표시용 사본, 예측 입력마다 1 Step)가 "서버가 쏠 것"이라고 할 때 바로 그린다. 서버 `ShotFired` 중 내 것은 무시한다. `WeaponState`는 입력마다의 결과(Phase 4부터 탄약 보유량 포함)를 64칸 링에 저장하고, Snapshot 수신자 블록이 오면 ack 시점의 기록과 비교한다. 다르면 그 시점을 서버 값으로 맞추고 ack 이후의 입력(아직 서버가 처리하지 않은 것)을 다시 적용한다(이동 재조정과 같은 방식). 기록보다 오래된 ack면 재적용 없이 서버 값에서 다시 시작한다. 명중·피해·사망은 서버 이벤트만 표시한다.
- **사망 중 예측:** `PlayerDied`(내 것)를 받으면 예측기는 이동하지 않고, Seq는 계속 올리되 이동 0·버튼 없음 입력을 보낸다(부활 직후 서버가 이 입력 일부를 살아 있는 상태로 처리하기 때문). 사망 중 Snapshot은 서버 위치로 바로 맞춘다. `PlayerRespawned`를 받으면 예측기를 새로 만들지 않고 같은 예측기의 상태만 Spawn 위치로 되돌린다. **Seq는 유지한다**(1부터 다시 세면 서버가 이미 소비한 Seq 이하를 버려 모든 입력이 무시된다). 생존 비트가 예측기 상태와 다른 Snapshot(다른 생의 것)은 재조정에 쓰지 않는다.
- **원격 플레이어:** 생존 여부는 Snapshot의 생존 비트로 정한다(죽어 있는 동안 들어온 Client는 `PlayerDied`를 받지 않았다). 살아 있는 동안 뷰는 회전하지 않는다(서버 AABB와 같은 축 정렬 `BoxCollider` 0.7 × 1.8 × 0.7을 유지하기 위해서이고, 캡슐은 Y축 둘레로 둥글어 보이는 모습은 같다). 이 Collider는 `Ignore Raycast` 레이어(2)에 있어 조준 광선만 본다. 죽으면 회색으로 눕고(진행 방향으로) Collider가 꺼진다. 다시 살아나면 보간 기록을 비워 시체 자리에서 미끄러지지 않고 Spawn 위치에 바로 나타난다. 경기 시작·판 재시작은 살아 있는 채로 Spawn에 옮기는 것이라 생존 비트가 바뀌지 않으므로, 다른 플레이어의 `PlayerRespawned`도 그 플레이어의 보간 기록을 정리한다(`RemotePlayerInterpolator.Teleport`): Spawn 위치에서 5 m 안에 있는 가장 최근 샘플들만 남기고(이벤트보다 늦게 도착한 이동 후 샘플을 지우지 않기 위해서다) 그보다 오래된 샘플은 버린다. 남는 샘플이 없으면 전부 비운다. 같은 이벤트를 다시 받아도 결과가 같다.

## 인벤토리와 Loot (Phase 4)

- **데이터(D2–D5):** 서버의 `weapons.json`(무기 3종: Vesper AR Medium, Kestrel LR Heavy, Wisp SMG Light = 12 / 2 Tick / 25발 / 48 Tick / 80 m / 자동), `items.json`(등급 5개와 피해 배율 1.00–1.20, 탄약 Light 180·Medium 150·Heavy 30 한도와 줍는 양 60·45·10, Medkit 3 s +50 스택 3, Shield Cell 2 s +25 스택 6), `loot.json`(Floor·Tower 가중치 표와 등급 가중치 50/25/15/7/3). 시작 시 셋 다 검증하고 틀리면 서버가 뜨지 않는다. Client에는 `WeaponCatalog`·`ItemCatalog`로 간다.
- **Loot(D5–D7):** Spawn Point는 Shared `LootPoints`(50곳, 테이블 `Floor`·`Building`·`Tower`, `GameMap` 옆. `Map.md`)다. 서버는 모든 Point에서 표를 굴려 아이템을 만든다. 운영 서버는 경기 시작 Tick에 굴리고 그 전(대기·카운트다운·결과 화면)에는 월드에 아이템이 없다. `DevRespawn` 서버는 Match를 만들 때 굴린다. 시드는 리뷰 수정 C1부터 경기 비밀(경기 시작마다 만드는 8B 난수, 보내지 않는다)에 용도(Loot)와 판 번호를 섞은 값이다. `Server:DeterministicSeeds`(시험·QA 재현)일 때만 예전처럼 `LootSeed + 판 번호`(`DevRespawn`은 `LootSeed`)다(`Server.md`). 난수는 Game Loop 스레드가 가진 `System.Random` 하나(경기마다 새로 만든다)라서 `DeterministicSeeds`에서 시드가 같으면 배치가 같다. 무기는 Id를 균등하게, 등급은 가중치로, 탄창은 가득. 탄약은 종류를 균등하게 줍는 양만큼. 다 가져간 Point는 `DevRespawn`일 때만 `LootRespawnSeconds`(기본 30) 뒤 다시 굴린다(0이면 끔). 경기 중에는 다시 생기지 않는다(Phase 5 D4). 타이머는 Point의 아이템이 완전히 사라질 때만 시작한다(부분 줍기는 타이머를 시작하지도 버리지도 않는다: 남은 수량이 월드에 있는 동안 Point는 비어 있지 않다). 타이머가 끝났을 때 그 Point의 아이템이 아직 월드에 있으면 다시 굴리지 않고 타이머를 버린다(Spawn Point 표식이 붙은 아이템은 Point마다 살아 있는 것이 최대 1개, `RefillLootPoints`). 떨어뜨린 아이템은 다시 생기지 않고, 칸이 다 찬 교환으로 나온 무기도 G처럼 플레이어 앞에 떨어지므로 Point 위에 겹치지 않는다.
- **월드 아이템(D13):** 최대 256개. 가득 차면 가장 오래 전에 떨어진 아이템부터 지우고(`ItemRemoved`를 먼저 보낸 뒤 새 `ItemSpawned`), Spawn Point 아이템은 지우지 않는다. ItemId는 1–65535를 한 바퀴 돈 뒤에야 다시 쓴다.
- **인벤토리(D1, D10):** 무기 칸 3개(무기, 등급, 탄창, 칸별 다음 발사 Tick), 현재 칸, 탄약 3종 보유량, Medkit·Shield Cell 개수. 서버가 소유하고 Client는 `InventoryState`를 표시만 한다. 시작과 부활은 빈손(Shield 0, Health 100)이다. 빈 칸도 선택할 수 있고, 빈 칸에서는 발사·재장전하지 않는다. 아이템은 월드가 받은 뒤에만 인벤토리에서 빠진다(Drop·교환·사망 Drop 모두. 월드가 거절하면 인벤토리에 남는다). 재장전은 보유량에서 탄창으로 옮기고, 보유량이 0이면 시작하지 않는다(Snapshot에 끝나지 않는 재장전이 나오지 않는다). 피해 = 무기 피해 × 등급 배율, 소수점은 0에서 멀어지게 반올림(`decimal` 계산), 최소 1. Shield 최대는 100이다.
- **서버 Tick 순서(살아 있는 플레이어, Phase 4 당시. 지금 순서는 위 "전투 (Phase 3)"의 서버 Tick 순서):** 이동(Phase 12: 탑승 중이면 `Ride`) → 재장전 완료 → (**실제로 받은 입력일 때만**) 사용 취소 → 칸 선택 → 버리기 → 줍기 → 재장전 → 발사 → 사용 시작 → (매 Tick) 사용 완료. Tick 전체 앞에는 (`DevRespawn`일 때만) 부활과 Loot 재생성이, 끝에는 `ServerTick++` → 바뀐 인벤토리의 `InventoryState` → 바뀐 `MatchState`·`ZoneState` → History → Snapshot이 온다. 누락 입력 반복은 이동만 반복하고, 줍기·버리기·사용·교체는 반복하지 않는다.
- **줍기(D8, D9):** E를 누른 입력에서 서버가 발 기준 수평 2.0 m·수직 2.0 m 안의 가장 가까운(3D 거리, 같으면 작은 ItemId) 아이템을 고른다. Client는 ItemId를 보내지 않는다. 무기: 첫 빈 칸에(빈손이면 그 칸을 손에 든다), 칸이 다 차 있으면 현재 칸과 바꾸고 원래 무기는 G처럼 플레이어 앞 1 m에 떨어진다(주운 자리가 아니다). 탄약·소모품: 한도까지만 받고 나머지는 수량을 줄여 바닥에 남긴다(`ItemSpawned` Upsert). 받을 수 없으면 `Full`, 범위 안에 없으면 `NothingInRange`. 같은 Tick에 둘이 누르면 먼저 처리된 플레이어만 얻는다.
- **버리기·사망 Drop(D12):** G는 현재 무기를 발 앞 1 m에 탄창째 떨어뜨린다. 발 높이 0.5 m에서 그 방향으로 박스에 막히면(지형은 막지 않는다. 얇은 벽 너머 포함), 또는 계산한 지점이 박스 안이면(수평은 엄밀히 안쪽, 높이는 `Min.Y <= y < Max.Y`라 바닥 박스 안의 y = 0도 안쪽이다) 발밑에 떨어뜨린다. 떨어진 곳은 그 위치의 지형 높이, 또는 발 높이 + `GroundProbe` 이하에서 더 높은 박스 윗면이다(아이템은 나중에 떨어지지 않는다). 사망 Drop의 원 위 지점에도 같은 규칙이 적용된다. 죽으면 무기·탄약 종류·소모품 종류마다 1개씩 시체 둘레 1 m 원 위에 `PlayerDied` 뒤에 떨어뜨리고 인벤토리를 비운다. 무기를 버렸다 다시 주워 발사 간격을 건너뛸 수 없다(버린 무기의 다음 발사 Tick이 새로 주운 무기에 걸린다).
- **회복(D11):** 4 = Medkit(90 Tick, Health +50), 5 = Shield Cell(60 Tick, Shield +25), 둘 다 최대 100. 이미 최대면 시작하지 않는다. 발사(Fire 비트)·칸 선택 비트·G·다른 회복을 누르면 취소되고(다른 회복은 같은 Tick에 새로 시작), 이동은 취소하지 않는다. 죽으면 회복과 재장전이 함께 취소된다(사망 Drop은 인벤토리를 `Clear`하지 않으므로 `Kill`이 직접 취소한다). 사용 중 상태는 `InventoryState`의 Using·UseRemainingTicks로 Client에 간다.
- **Client:** `WorldItemViews`가 아이템을 모양(무기 큐브·탄약 원통·회복 구)과 색(등급 5색, 탄약·Medkit·Shield Cell)으로 그린다. `PickupRule`(서버 규칙 사본)이 E가 집을 아이템에 "[E] Pick up …"을, `PickupResult`가 실패하면 "Inventory full"·"Nothing to pick up"을 띄운다(내장 폰트에서 한글 표시를 확인하지 않아 안내 문구는 영어다). `WeaponState`는 인벤토리 칸 3개를 기준으로 발사를 흉내 낸다. 칸의 내용물과 보유량은 `InventoryState`에서, 현재 칸과 그 탄창은 Snapshot에서 받는다(두 패킷은 채널이 달라 순서가 섞이므로, 현재 칸 탄창을 `InventoryState`로 덮어쓰면 ack 비교가 그 값을 고치지 못한다). 보유량이 바뀌면 64칸 기록의 보유량도 종류별 차이만큼 같이 옮긴다(rebase). 그렇지 않으면 이후 불일치 재적용이 줍기 같은 서버 확정 값을 되돌린다.

## Battle Royale (Phase 5)

규칙과 상태 전환은 `BattleRoyale.md`에 있다. 여기에는 전송 규칙만 적는다.

- `MatchState`·`ZoneState`는 Tick 끝에 마지막으로 보낸 값과 다를 때만 전원에게 보낸다(상태·타이머·생존자 수·인원·판 번호가 바뀔 때, Zone 단계가 바뀔 때). Join 때는 새로 온 사람에게 바로 보낸다. `DevRespawn` 서버는 둘 다 보내지 않는다. 그래서 Client는 `MatchState`를 받은 적이 없으면 Phase 4처럼(부활 카운트다운, 경기 HUD·Zone 없음) 동작한다.
- `MatchState`에는 `MinPlayers`(1B)가 있다. HUD가 "플레이어를 기다리는 중 1/2"의 2를 알기 위해서다.
- `MatchResult`는 경기가 끝난 Tick에 아직 접속해 있는 참가자에게 한 번씩 간다. 경기 중 들어온 관전자와 이탈자는 받지 않는다.
- **서버 Tick 순서(Phase 5 당시. 지금 순서는 위 "전투 (Phase 3)"의 서버 Tick 순서):** `MatchFlow` 전환(경기 시작·판 재시작은 이 Tick 안에서 끝난다) → Zone 단계 진행과 Zone 피해(경기 중, 시작부터 1초마다) → (`DevRespawn`만) 부활 → Loot 재생성(`DevRespawn`만) → 플레이어마다 입력·이동·행동(사망하면 순위 기록) → 종료 판정(생존자 ≤ 1) → `ServerTick++` → 바뀐 인벤토리 → 바뀐 `MatchState`·`ZoneState` → History → Snapshot.
- **경기 전 피해 차단(D2):** 경기 전(대기·카운트다운)과 결과 화면에서는 발사·궤적(`ShotFired`, 맞은 사람에서 멈춘 끝점)은 그대로지만 피해가 없고 `HitConfirmed`·`DamageTaken`도 없다.
- **Zone 원(D11):** Client는 `ZoneState`의 From·To와 ShrinkStart·End Tick으로 서버 `SafeZone.Sample`과 같은 식(`ZoneMath.Sample`)으로 원을 그린다. 반지름 0인 원은 안이 없다(`IsOutside`는 서버·Client 모두 `radius <= 0`이면 밖이다). 두 식이 같은지는 서버 테스트(`ZoneMathParityTests`)가 Client 파일을 컴파일해 고정한다. Client의 시각은 "렌더 Tick + 보간 지연"(서버 현재 Tick 추정)이다.
- **판 시작·재시작의 이동:** 서버는 모두의 Spawn 이동을 `PlayerRespawned`(전원, 내 Seq 유지)로 알린다. 내 예측기는 부활과 같은 경로로 상태만 되돌리고, 다른 사람의 보간 기록은 위 "원격 플레이어"의 `Teleport`로 정리한다.

## 투입과 문 (Phase 12)

설계 근거: `Docs/specs/2026-10-02-phase12-deployment-traversal-design.md` D5, D9, D16. 이동 규칙은 `Movement.md`, 흐름은 `BattleRoyale.md` "공중 투입", 문은 `Map.md` "문"이다.

- **탑승 Tick 대응:** 수송기는 Snapshot에 실리지 않는다. `TransportRoute`가 한 번 가고, 양쪽이 같은 `DropRoute.PositionAt(tick)`으로 위치를 구한다. 서버는 탑승자를 그 Tick에 시뮬레이션되는 서버 Tick(`ServerTick + 1`)의 경로 위치에 둔다. Client 예측은 입력 Seq의 서버 Tick을 `ServerTick − Ack + Seq`로 구한다. `ServerTick − Ack`는 Ack가 0보다 큰 Snapshot마다 새로 잡는다. Ack가 0인 처음에는 기준이 없으므로 탑승을 예측하지 않고 탑승자를 Ack 0 Snapshot의 서버 위치에 둔다(보정으로 세지 않는다). 첫 Ack 뒤의 다시 계산도 보정으로 세지 않는다(최종 검토 B6). Seq와 서버 Tick은 한 입력에 Tick 하나씩이라 둘의 차이가 일정하기 때문에, 예측한 탑승 위치가 서버가 그 입력을 처리하는 Tick의 위치와 같다. 수송기 상자를 그리는 Tick은 렌더 Tick(원격 플레이어와 같은 보간 지연)이다. 내가 타고 있는 동안만 내 예측 Tick으로 그린다(탑승 위치·카메라와 같이 움직이게). 판이 `WaitingForPlayers`·`Starting`으로 돌아가면 Client와 봇은 지난 경로를 지운다(최종 검토 B2, C14).
- **뛰어내리기·글라이더·Vault·문은 새 패킷이 없다.** Jump(`Transport`에서는 뛰어내리기, `Freefall`에서는 글라이더, 지상에서는 점프 또는 Vault)와 Interact(문, 없으면 줍기)를 다시 쓴다. 서버가 모드를 보고 거른다(중복 뛰어내리기는 무시).
- **문 예측:** 예측은 서버의 `DoorStates`에 자기 예측(내가 밀친 문, 내가 E로 연 문)을 겹쳐 쓴다(`PredictedDoors`). 예측은 다음 `DoorStates`가 오거나 1초(`PredictionSeconds`)가 지나면, 둘 중 먼저 오는 쪽에서 서버 상태로 돌아간다. 서버가 거부한 예측이 남지 않게 하기 위해서다. 문을 밀치는 순간 Client가 서버보다 한 왕복 먼저 통과해 짧은 보정이 생길 수 있다(받아들인다). 연결이 끊기면 모든 문이 닫힌 것으로 되돌린다.
- **재접속과 Ack 0:** Resume이나 늦은 합류의 첫 Snapshot은 Ack가 0이다. 이미 예측하고 있어도 서버의 모드가 예측과 다르면(공중에서 돌아온 경우) 서버 상태로 맞춘다. 같으면 무시한다(Phase 3의 규칙). 끊긴 동안 서버는 빈 입력으로 이동을 이어 가므로(0.5초 뒤 정지, 탑승자는 구간 끝에서 강제로 뛰어내림) 서버의 모드가 앞서 있을 수 있다.
- **재접속 때 전송:** `TransportRoute`(공중 투입 경기 중일 때), `DoorStates`는 접속 순서에 들어 있다. 모드와 Self는 다음 Snapshot이 알려 준다.

## 건설과 채집 (Phase 13)

설계 근거: `Docs/specs/2026-10-02-phase13-harvesting-building-design.md` D5–D8, D13–D15. 규칙과 패킷의 자세한 내용은 `Building.md`다.

- **채널:** `ProtocolConstants.ChannelCount` = 2. 채널 0(`ReliableChannel`)은 지금까지의 모든 패킷이고, 채널 1(`BuildChannel`)은 `BuildRequest`와 건설 스트림(`BuildResult`, `BuildEvents`, `BuildSync`, `BuildInterest`)만이다. 서버·Client·봇·테스트 Client가 모두 `ChannelsCount = 2`로 연다. 건설 스트림이 커져도 채널 0의 이벤트와 Snapshot이 기다리지 않는다.
- **Snapshot은 그대로다:** Entity 13B, Self 14B. 도구는 빈 비트에 넣었다. 건설 상태는 Snapshot에 싣지 않는다.
- **요청 검사(수신 스레드):** Join 전 요청은 `InputBeforeJoin`, 본문이 틀리면 `Malformed`, 연결당 초당 20개를 넘으면 `BuildRate`(잘못된 패킷, 고정 1초 창). 통과한 요청은 유한 채널(`InboundChannels.Build`)로 Game Loop에 가고, 플레이어마다 큐 8개다(가득 차면 `RateLimited`로 답한다).
- **받는 쪽:** `BuildStore`가 id로 적용한다(중복·늦은 이벤트 무시, 관심 칸 밖 조각은 저장하지 않음). reset Sync를 받으면 모두 버린다(Join·Resume·라운드).
- **카탈로그 채널(최종 리뷰 A3):** `BuildCatalog`는 채널 1로, Join·Resume의 reset Sync 바로 앞에 간다. 두 채널은 서로 순서를 지키지 않으므로, 같은 채널의 첫 패킷이어야 관심 칸 크기를 모른 채 `BuildInterest`·조각을 읽는 일이 없다. 그래서 접속 순서 4·5번의 채널 0 목록에는 `BuildCatalog`가 없다.
- **밀린 건설 채널(최종 리뷰 A4):** 건설 채널의 신뢰 대기열이 32개를 넘은 연결은 그 Tick의 `BuildSync`를 건너뛴다. 사건·창·결과는 계속 간다. 오래 밀리면 `Congested`로 끊는다(위 표).
- **누르는 키(최종 리뷰 A5):** 서버는 E(Interact), G(Drop), F(ToolHarvest), Q(ToolBuild), 1–3(Slot), R(Reload)을 직전 실제 입력에 없던 때만 처리한다. 키를 쥔 채 보내는 수정 Client도 줍기·버리기·문·도구 전환을 Tick마다 되풀이하지 못한다. Client와 봇은 이미 누름을 한 입력에만 싣는다(`LocalPlayerPredictor`의 queued 버튼). Fire·회복·점프·달리기·웅크리기는 쥔 상태 그대로다.
- **Fuzz:** 새 파서 9개 모두 `ProtocolFuzzTests`에 들어 있다.

## 분대 (Phase 14)

설계 근거: `Docs/specs/2026-10-08-phase14-squad-dbno-design.md` D2, D4, D5, D7–D10, D13, D16. 규칙은 `Squad.md`다.

- **새 패킷 4종(36–39)은 모두 S→C, 채널 0 ReliableOrdered다.** `PacketReader.TryReadPacketId`의 상한은 `RebootStations`(39)다. 새 리더 4개 모두 `ProtocolFuzzTests`에 들어 있다.
- **Snapshot은 그대로다(Entity 13B).** 기절은 비어 있던 이동 모드 값 7(`Downed`)이다. 3비트의 8개 값이 모두 모드가 됐다. Alive 비트는 켜진 채다. 피격 상자 높이는 0.9 m.
- **TeamState:** 받는 사람의 자기 팀만. 바뀔 때만(상태·10 단위 체력·카드 플래그). 나간 구성원은 목록에서 빠진다(Count가 줄어든다). 경기 밖(대기실·리셋 뒤)에는 오지 않는다. Solo도 1명짜리 팀으로 온다.
- **RebootCard 월드 아이템:** `ItemKind.RebootCard` 5, DefId 0, Rarity 0, `Amount` = 주인 Entity id. 주인 팀에게만 `ItemSpawned`·`ItemRemoved`·`WorldItems`로 간다. 다른 팀은 카드가 있는 줄 모른다. 줍기는 `Interact`(누름)이고 서버가 같은 팀 카드만 고른다.
- **진행률:** `ChannelState.EndTick`과 서버 Tick으로만 계산한다(Client 타이머 금지). 시작 Tick은 받은 때를 기억해 쓴다(길이는 서버 설정).
- **접속 순서:** Join·Resume 때 기존 묶음의 끝에 `RebootStations`, (팀이 있으면) `TeamState`, (Resume이면) 팀의 진행 중인 `ChannelState`가 붙는다.
- **HitConfirmed.Killed:** 탈락시켰을 때만 true다. 기절시킨 명중은 false(Client 명중 표시도 그렇게 맞춘다).

## 지도 표시 (Phase 15)

설계 근거: `Docs/specs/2026-10-08-phase15-map-ping-design.md` D7–D10, D13. 규칙은 `Map.md` "지도 UI·Ping"이다.

- **크기:** Spec은 `MapMarker` 11B, Ping 칸 15B라고 적었지만 필드 합이 각각 10B(1 + 1 + 6 + 2), 16B(1 + 1 + 2 + 6 + 4 + 2)라 코드는 10B·16B다(필드와 순서는 Spec 그대로). `TeamMarkers` 최대는 163B다(`MapSharedTests`가 고정).
- **위치:** 1/100 m int16(±327.67 m)이다. Client가 거대한 좌표를 보낼 수 없고, 서버가 맵 안(|x|, |z| ≤ 80)인지 다시 본다. 높이는 서버가 [그 점 지형 높이 − 1 m, 지형 높이 + 16 × 3 m + 2 m]로 자른다.
- **Reader 검사(`MapMarker.TryRead`):** 길이가 정확히 10B, 종류 ≤ 5, Enemy·Item은 대상 id ≠ 0, 그 밖 종류는 대상 id = 0. 어기면 `Malformed`(잘못된 패킷)다. 맵 안인지·대상이 맞는지는 Match가 본다(아래 Validation).
- **Reader 검사(`TeamMarkersPacket.TryRead`, Client·봇):** Ping 수 ≤ 8, Waypoint 수 ≤ 4, 호출자 배열 크기 이하, Ping 종류 ≤ 3(Danger), 주인 id ≠ 0, 수평 좌표가 맵 안(±80.01), 남거나 모자란 바이트가 없어야 한다. 호출자가 가진 고정 배열에 읽는다(할당 없음). 서버는 저장하는 모든 위치를 맵 안으로 확인하므로(Enemy 대상이 맵 밖이면 Location으로 바꾼다) 정상 서버의 목록은 항상 읽힌다.
- **수신 스레드 순서(`NetworkListener`, `MapMarker` case):** Join 안 함 → `InputBeforeJoin` / 1초 창에서 `maxMarkerPacketsPerSecond`(20)개 초과 → `MarkerRate` 잘못된 패킷(끊기 기준 공유) / 연결별 Token Bucket(초당 `pingsPerSecond` 2개, 한 번에 `pingBurst` 4개) 초과 → 버리고 Health `markerDrops`로 센다(Kick 없음) / 본문이 틀림 → `Malformed` / `InboundChannels.Marker`(MaxPlayers × 4, DropOldest, 밀려난 것은 `markerInboxDrops`)에 넣는다. 이 case가 없으면 `MapMarker`는 `WrongDirection` 잘못된 패킷이 되어 Ping마다 끊기 기준에 쌓인다(Phase 13.5의 상한 누락과 같은 함정, `MarkerRateTests`가 고정).
- **멱등:** `TeamMarkers`는 팀 표시 전체다. Client는 받은 목록으로 통째로 바꾼다. 순서는 의미가 없다. Ping `id`는 팀 단위 일련번호(1–255 순환)라 새 Ping을 구분하는 데 쓴다.
- **접속 순서:** Join·Resume 때 기존 묶음의 끝(Phase 14 분대 상태 뒤)에 (팀이 있으면) `TeamMarkers`가 붙는다. 경기 중 새로 들어온 관전자는 팀이 없어 받지 않는다.
- **Fuzz:** 두 리더 모두 `ProtocolFuzzTests`에 들어 있다. `PacketReader.TryReadPacketId`의 상한은 Phase 15 때 `TeamMarkers`(41)였고 Phase 16에서 `SupplyDrops`(43), Phase 17에서 `ProjectileExploded`(46)가 됐다.

## Loot Container와 Supply Drop (Phase 16)

설계 근거: `Docs/specs/2026-10-08-phase16-loot-containers-design.md` D3, D4, D6, D7, D9. 규칙은 `Loot.md`다.

- **새 C→S 패킷은 없다.** 열기는 기존 `Interact`(누름) 입력이다. 서버 C→S 수신 switch는 바뀌지 않았다(42·43이 오면 `WrongDirection`).
- **Reader 검사(`ContainerStatesPacket.TryRead`):** 16B 이상, 맵에 없는 Container 비트가 없고(`LootContainers.Count` 이상), 열림 ⊆ 생성. 어기면 읽기 실패(이전 상태 유지).
- **Reader 검사(`SupplyDropsPacket.TryRead`, Client·봇):** 수 ≤ 4, 호출자 배열 크기 이하, 남거나 모자란 바이트 없음, id < 4이고 오름차순, 상태 ≤ 2, 좌표가 유한하고 수평이 맵 안(±80.01), 착지 Tick ≥ 시작 Tick. 호출자가 가진 고정 배열에 읽는다(할당 없음).
- **낙하 계산(공용, `SupplyDropFall`):** 위치 = (x, `HeightAt(착지 높이, 시작 Tick, 착지 Tick, tick)`, z). `HeightAt`은 시작 Tick 전에는 착지 높이 + `StartHeight`(60 m, Shared 상수), 착지 Tick까지 일정한 속도로 내려오고 그 뒤 착지 높이다. 속도는 `loot.json` `supplyDrops.fallSpeed`가 착지 Tick으로 담는다(패킷에 시작 높이가 없어 시작 높이만 Shared 상수다. Spec과 다른 점, 리더 승인). 서버 물리는 없다(수송기 `DropRoute`와 같다).
- **멱등:** 두 패킷 모두 전체 상태다. Client는 받은 값으로 통째로 바꾼다. 상태 `Landed`는 서버가 착지 Tick 끝에 바꿔 보낸다.
- **접속 순서:** 위 접속 순서 4·5번의 `HarvestStates` 바로 뒤에 `ContainerStates` → `SupplyDrops`.
- **Fuzz:** 두 리더 모두 `ProtocolFuzzTests`에 들어 있다. 봇(`BotConnection`)·QA Actor(봇 연결 재사용)·서버 테스트 `HeadlessClient`가 둘 다 읽는다.

## 무기와 투사체 (Phase 17)

설계 근거: `Docs/specs/2026-10-08-phase17-weapons-throwables-design.md` D2, D3, D6, D7, D16. 규칙과 수치는 `Weapons.md`다.

- **새 C→S 패킷은 없다.** 수류탄은 입력 버튼 `ThrowGrenade`(32768, u16의 마지막 빈 비트, 키 6, 누름)로 던진다. `PlayerInputPacket.KnownButtons`에 들어갔다. 서버는 누름 키(`Match.EdgeButtons`)로 다뤄 여러 입력에 눌려 있어도 한 번만 던진다. Phase 19 탈것은 기존 비트를 다시 쓰므로 u32로 넓히지 않는다.
- **퍼짐은 서버가 정한다:** 시드(쏜 사람 Entity id, 시뮬레이션 중인 Tick, 그 Tick의 광선 번호, 리뷰 수정 C1: 경기 비밀. `DeterministicSeeds`면 0)를 Client는 알 수 없으므로 내 예광탄은 Client 자체 퍼짐이고, 다른 사람의 `ShotFired.End`는 서버가 퍼뜨린 광선의 실제 끝점이다. 산탄총은 방아쇠 한 번에 `ShotFired` 하나(가운데 조준 광선의 끝점), 산탄마다 보내지 않는다. 로켓은 `ShotFired` 없이 `ProjectileSpawned`만 보낸다.
- **투사체 표시:** Snapshot에 싣지 않는다. Client는 마지막 사건(`ProjectileSpawned`의 StartTick, `ProjectileState`의 Tick)부터 `p = Position + Velocity·dt + ½·(0, −Gravity, 0)·dt²`(dt = Tick 차 / SimHz, Gravity = 카탈로그의 `ProjectileInfo`)로 외삽한다. 서버도 Tick마다 같은 등가속 적분을 하므로 사건 사이 궤적이 일치한다. Velocity 0 = 정지.
- **지워지는 때:** 서버는 경기 시작·라운드 리셋·경기 끝(Finished)에 투사체를 아무것도 보내지 않고 지운다. 시작 카운트다운(Starting)·결과 화면(Finished·Closing)에서는 새로 만들지 않는다. Client는 `MatchState`가 바뀔 때, 끊길 때, Join·Resume 리셋 때 비운다(대기실에서 던진 수류탄이 Starting까지 날면 서버는 피해 없이 `ProjectileExploded`를 보낸다. Client는 모르는 id를 무시한다).
- **Reader 검사:** `ProjectileSpawned`·`ProjectileExploded`는 id 0·모르는 종류·유한하지 않은 값을, `ProjectileState`는 id 0·유한하지 않은 값을 거절한다. `WeaponCatalog`은 산탄 0·17 이상, 퍼짐·반동 범위 밖, 모르는 투사체, 같은 투사체 종류 둘, 목록에 없는 투사체를 쏘는 무기, (리뷰 수정 C2) `EquipTicks` 256 초과를 거절한다.
- **상한:** `PacketReader.TryReadPacketId`의 상한은 Phase 16 `SupplyDrops`(43)에서 Phase 17 `ProjectileExploded`(46)가 됐다. 세 리더와 3인자 `WeaponCatalogPacket.TryRead`는 `ProtocolFuzzTests`에 있다. 봇(`BotConnection`)·QA Actor는 세 패킷을 세기만 한다.

## 게임 오디오 (Phase 18)

설계 근거: `Docs/specs/2026-10-08-phase18-audio-design.md` D4, D6, D7, D8, D11. 소리 재생 규칙은 Client 쪽 `Audio.md`다. 서버는 소리를 내지 않고, Client가 다른 패킷에서 알 수 없는 정보만 싣는다.

- **`ShotFired.WeaponId`(D4):** 쏜 무기의 카탈로그 id(`WeaponInfo.WeaponId`). 방아쇠 한 번에 `ShotFired` 하나(산탄총도 하나)라 산탄 수만큼 총성이 나지 않는다. 리더는 id 범위를 검사하지 않는다(카탈로그에 없으면 Client가 기본 소리를 쓴다). 로켓은 여전히 `ShotFired`가 없다.
- **Destroyed 이유(D6):** `BuildEvents`의 Destroyed 기록이 id 4B + 이유 1B(`BuildDestroyReason`: 0 `Destroyed` = 체력 0(사격·폭발·채집 도구·QA `damageBuild`·QA 파괴), 1 `Collapsed` = 같은 Tick의 파괴나 편집 뒤 지지를 잃고 무너짐) = 5B다. `TryReadHeader`의 정확한 길이 검사와 `BuildReplication.NextPacket`의 남은 자리 계산은 `DestroyedSize`를 그대로 쓴다. 이유가 1보다 크면 읽기 실패. 이유가 필요 없는 받는 쪽을 위해 이유를 버리는 2인자 `TryReadDestroyed`도 남겼다(5B 전체를 읽는다).
- **`DamageTaken.Flags`(D8):** bit0 `ShieldHitFlag` = 이 피해 전 실드 > 0이었고 줄었다, bit1 `ShieldBrokenFlag` = 이 피해로 실드가 0이 됐다(항상 bit0과 함께). 둘 다 없으면 체력만 맞았다. 사격(산탄은 대상마다 합친 한 번), 폭발, QA `damagePlayer`가 같은 규칙(`DamageTaken.FlagsFor`)을 쓰고, 낙하 피해는 실드를 건드리지 않으므로 항상 0이다. 모르는 비트, bit0 없는 bit1은 읽기 실패. Snapshot의 실드 값으로 추정하지 않는 이유: `DamageTaken`(채널 0)과 Snapshot 사이에 순서 보장이 없다.
- **`WorldSound`(D7, 47):** 서버가 휘두르기 결과를 정할 때, 채집 대상을 맞혔으면 맞은 점에서 30 m(`Match.WorldSoundRange`, 서버 상수) 안의 다른 플레이어에게 Unreliable로 하나 보낸다(부서뜨렸으면 `HarvestDestroyed`만, 아니면 `HarvestHit`). 휘두른 사람은 지금처럼 `HarvestHit`(33)만 받는다. 연결이 끊긴 유예 플레이어는 빼고, 죽은 플레이어·관전자는 거리를 재지 않고 모두 받는다(관전 카메라로 들으므로 Client 믹서가 카메라 기준 거리로 거른다. Phase 17–19 리뷰). 건설 조각을 친 휘두르기는 보내지 않는다(`BuildEvents`의 Health·Destroyed로 알 수 있다). 패킷은 한 번 쓰고 플레이어 수만큼 거리 제곱만 비교한다(할당 없음, 휘두르기 때만).
- **상한:** `PacketReader.TryReadPacketId`의 상한이 `ProjectileExploded`(46)에서 `WorldSound`(47)가 됐다. 새 리더와 바뀐 리더는 `ProtocolFuzzTests`에 있다. 봇(`BotConnection`)·QA Actor는 `ShotFired`(수, 마지막 쏜 사람·무기 id), `DamageTaken` 실드 플래그 수, Collapsed 수, `WorldSound`(수, 마지막 종류·소스)를 세기만 한다(`ActorModel`의 `shotsSeen`·`lastShotWeaponId`·`shieldHitsTaken`·`shieldBreaksTaken`·`collapsesSeen`·`worldSounds`·`lastWorldSoundKind`·`lastWorldSoundSource`).

## 차량 (Phase 19)

- **`VehicleStates`(D4, 48):** Snapshot을 보내는 Tick마다 받는 사람별로 만든다(관심 영역이 다르고 `AckInputSeq`가 받는 사람의 것이다). 경기에 차량이 하나라도 있으면 연결된 모든 플레이어가 매번 받는다(그 사람의 Count가 0일 수 있다). 마지막 차량이 사라진 뒤에는 Count 0 패킷을 한 번 보내고 멈춘다.
- **Unreliable인 이유:** Sequenced 채널 0은 Snapshot과 순번을 공유해, 차량 패킷이 Snapshot 두 번째 조각보다 먼저 도착하면 그 조각이 버려질 수 있다. Client는 마지막으로 적용한 ServerTick 이하의 패킷을 버리고, 1 s 동안 패킷에 없는 차량은 숨긴다. 차량 id(1..255)는 사라진 뒤 1 s 안에 다시 쓰지 않는다.
- **운전자 예측:** `AckInputSeq = N`인 기록은 입력 N의 Step 뒤 상태다(이동 Reconcile과 같은 규칙). 탄 플레이어의 Snapshot 위치는 좌석 위치이고 이동 모드는 Ground다. 누가 탔는지는 기록의 Driver·Passenger가 기준이다.
- **상한:** `PacketReader.TryReadPacketId`의 상한이 `WorldSound`(47)에서 `VehicleStates`(48)가 됐다. 리더는 길이·Count(≤ 8)·id 0·모르는 상태·중복 id를 거절한다(`ProtocolFuzzTests`, `VehiclePacketTests`). 봇은 차량 목록을 기록만 하고(탔으면 내린다), QA Actor는 `vehicleId`·`seat`·`vehicleCount`를 보여 준다.

## 전적 조회 (Phase 11 D8)

설계 근거: `Docs/specs/2026-10-01-phase11-game-ui-design.md` D8, 5절. Game Loop는 DB를 기다리지 않는다.

- 흐름: Client가 전적 창을 열 때 `StatsRequest` → 수신 스레드(`NetworkListener`)가 검사하고 요청 채널(32)에 넣는다 → `StatsQueryService`가 하나씩 읽어 DB를 조회한다(`Database.md` "조회 경로") → 응답 채널(32) → Game Loop가 Tick마다(`SendStatsReplies`, 최대 32개) 요청한 연결에 `StatsResponse`를 보낸다. 모든 송신은 Game Loop가 한다.
- 버리는 경우: Join이 성공하지 않은 연결(Join 전, Join 처리 전, `MatchFull`로 거절됨)의 요청, 같은 연결의 앞 요청 뒤 2초(`StatsQueryQueue.MinRequestIntervalMs`) 안의 요청은 답 없이 버리고 `limited`로 센다. 잘못된 패킷이 아니라서 버튼을 연타해도 Kick되지 않는다. 거절된 요청은 2초 창을 옮기지 않으므로, 계속 눌러도 2초마다 한 번은 답을 받는다.
- `Busy`: 요청 채널이 가득 차면 수신 스레드가 바로 `Busy`를 답 채널에 넣는다.
- `Unavailable`: Persistence가 꺼져 있거나(요청마다 바로), 요청이 큐에서 5초(`StatsQueryQueue.MaxQueueAgeMs`, Client가 기다리는 시간)보다 오래 기다렸거나(조회하지 않고 바로), DB가 실패하거나, 조회가 3초를 넘었을 때. 3초를 넘긴 조회는 그 자리에서 취소한다.
- 요청한 연결이 떠났거나 그 peer id가 다른 연결로 바뀌었으면 답을 버리고 `undelivered`로 센다. 응답 채널이 가득 차서 못 넣은 것도 `undelivered`다.
- Client는 5초 안에 답이 없으면 "응답 없음"을 보여 준다(`Client.md` "화면과 흐름"). 2.5초 안에 다시 열면 새로 요청하지 않고 앞 요청의 답을 기다린다.
- 카운터는 Health 줄과 Meter(`Server.md` "관측")에 있다.

## Validation (서버)

- 입력: NaN/Infinity → 0, 이동 벡터 길이 > 1 → 정규화(`MovementSimulation.Step`), Yaw가 비유한이면 이전 Yaw 유지. Seq 중복·역행(이미 소비한 Seq 이하) 무시, Tick당 플레이어별 1스텝. 시작 위치가 박스와 겹치면 밀어낸 뒤 이동한다.
- **Seq 창(리뷰 수정 A4, SEC-7):** 이미 가져간 입력이 있으면 `LastTakenSeq`보다 `ServerOptions.InputSeqWindow`(기본 180 Tick = SimHz × (DisconnectTimeout + 1 s), 최소 `ProtocolLimits.MaxInputSeqAhead` 64) 넘게 앞선 Seq는 버리고 Health `inputSeqDrops`로 센다. 끼워 넣은 입력 하나(예: Seq 0xFFFFFFF0)가 그 뒤 정상 입력을 모두 막지 못한다. 창은 연결이 끊기지 않는 가장 긴 통신 끊김(DisconnectTimeout)에 1초를 더한 만큼이다: Client는 패킷이 사라지는 동안에도 Tick마다 Seq를 올리므로, 64 Tick 창이면 2초 넘는 끊김 뒤의 입력이 모두 버려졌다. 처음 입력(새 연결·Resume)은 기준이 없어 창으로 거르지 않는다. 입력 시간(`InputTimeout`)은 패킷의 입력이 **하나라도 받아들여졌을 때만** 갱신한다(모두 거절된 패킷은 입력이 아니다).
- Join은 연결당 한 번만 처리한다. 두 번째부터는 잘못된 패킷으로 세고 Match에 전달하지 않는다(Control 채널 이벤트 ≤ 3/연결 유지). 본문이 있는 Join은 `Malformed`이고 Join으로 치지 않는다(리뷰 수정 A1).
- Join하지 않은 peer의 PlayerInput은 거절(잘못된 패킷). peer별 입력 패킷은 Token Bucket으로 제한한다: 한 번에 `SimHz`개(기본 30), 그 뒤로 초당 `SimHz * 2`개(기본 60). 초과분은 버리고 `InputRate`로만 센다. Kick 카운터에는 더하지 않는다(서버 리뷰 M5, L5. 나쁜 회선이 몇 초치 입력을 한꺼번에 전하는 정상 Player가 끊기지 않게). 공유 Input 채널은 모든 Player의 burst를 한꺼번에 담는다(`MaxPlayers × InputBurst`, 리뷰 수정 A5·SEC-18: 여러 연결이 burst를 같이 써도 남의 입력을 밀어내지 않는다). Build 채널은 Player마다 1초 창 두 개만큼(`MaxPlayers × 2 × maxRequestsPerSecond`)이다.
- **크기 상한(리뷰 수정 A1, SEC-1):** Client 패킷은 `ProtocolLimits.MaxClientPacketBytes`(128B, 가장 큰 정상 패킷은 PlayerInput 92B)를 넘으면 파싱하지 않고 `Malformed`로 센다. 서버·Client·봇의 `NetManager.MaxFragmentsCount`는 `ProtocolLimits.MaxFragments`(2)라 그보다 조각이 많은 메시지는 LiteNetLib이 재조립 전에 버린다(기본 65535는 연결 하나가 메시지당 수십 MB를 붙잡게 했다). 게임 패킷은 모두 `MaxPacketSize` 이하라 조각이 생기지 않는다(`PacketTests`가 S→C 최대 크기 + 전송 헤더 4B + 인증 꼬리 20B ≤ MTU 1232, C→S 최대 ≤ 128B를 고정). PlayerInput은 길이가 정확히 `개수 × 30B`여야 한다(남는 바이트도 거절).
- 알 수 없는 PacketId, 클라이언트가 보낼 수 없는 PacketId(서버→클라이언트 패킷), 잘리거나 개수가 범위 밖이거나 남는 바이트가 있는 PlayerInput → drop하고 잘못된 패킷으로 센다. 연결별 `BadPacketDisconnectThreshold`(20) 이상이면 `Kicked` 코드로 끊는다(Warning 로그).
- 잘못된 패킷은 이유별로 센다(`BadPacketReason` 9가지, Health 줄과 Meter. Phase 13: `BuildRate` = 연결당 초당 건설 요청 상한 초과. Phase 15: `MarkerRate` = 연결당 1초에 `MapMarker` 20개 초과): `UnknownId`(빈 패킷·모르는 첫 바이트), `Malformed`(아는 Id인데 본문이 틀림), `InputBeforeJoin`, `DuplicateJoin`, `InputRate`(입력 상한 초과. 버리기만 하고 Kick 카운터에는 더하지 않는다), `WrongDirection`(서버→Client 패킷 Id), `HandlerException`(받기 핸들러가 던진 예외).
- 받기 핸들러(`OnNetworkReceive`)는 try/catch로 감싼다. 예외는 그 peer의 잘못된 패킷(`HandlerException`)으로 세고 통계 주기마다 첫 하나만 로그(Error)로 남긴다. LiteNetLib 스레드는 모든 연결을 맡으므로 한 패킷이 그 스레드를 흔들지 못한다.
- `MapMarker`(Phase 15 D8, Match가 검증): 경기 중(또는 개발 모드), 팀이 있는 참가자(개발 모드는 누구나), 살아 있음(기절 포함. `ActionsAllowed`는 보지 않는다), 좌표가 맵 안. Waypoint 지우기만 탈락한 팀원도 할 수 있다. Enemy: 대상이 다른 팀의 살아 있는 플레이어, 눈에서 대상 몸 가운데까지 150 m 안, 맵 상자·닫힌 문·지형에 막히지 않음(건설 조각·채집 대상은 보지 않는다), 대상 발이 맵 안. 실패하면 보낸 좌표의 Location이 된다(Client의 맥락을 믿지 않는다). Item: 아이템이 있고(다른 팀의 재투입 카드는 없는 것으로 본다) 발에서 60 m 안. 실패하면 버린다. 받지 않은 요청은 답 없이 버리고 Health `map refused`로 센다(잘못된 패킷이 아니다).
- `StatsRequest`: 본문이 있으면 `Malformed`(잘못된 패킷). Join이 성공하지 않은 연결의 요청이나 연결당 2초 안의 요청은 잘못된 패킷이 아니라 `limited`로만 센다(Kick 없음).
- 연결 요청의 DevPlayerId(이름)는 1–32바이트의 올바른 UTF-8이고 제어 문자(C0, DEL, C1), 서식 문자(폭 0 문자, 방향 제어, BOM), 줄·문단 구분자가 없어야 한다. 아니면 `BadRequest`로 거절한다. 규칙은 Shared `ProtocolConstants.IsValidPlayerName` 하나이고 `ConnectRequestData.TryRead`가 검사한다(타이틀과 봇도 같은 규칙을 쓴다). 깨진 바이트는 대체 문자(3바이트)로 읽혀 32바이트를 넘을 수 있고, 그러면 그 이름을 `PlayerSpawned`에 못 써 다른 Client가 그 플레이어를 못 본다(Phase 11). `Match`는 그래도 넘친 `PlayerSpawned`를 보내지 않고 센다(`Server.md`).
- 거절된 연결 요청(`ServerFull`, `BadRequest`, `VersionMismatch`)을 이유별로 센다. `ServerFull`로 보내는 IP별·전역 거절은 `connectRate`·`perIp`·`penalized`·`accept`로, 틀린 쿠키는 `cookie`로 따로 센다. 로그는 Debug다(요청 폭주가 로그를 채우지 않게).
- 연결 요청 빈도(서버 리뷰 M2): 원격 IP마다 Token Bucket(`ConnectBurstPerIp` 20개, 그 뒤 초당 `ConnectsPerIpPerSecond` 5개)을 둔다. 넘는 요청은 `Accept` 전에 `ServerFull`로 거절하고(프로토콜 변경 없음) Health 줄 `rejects connectRate`로 센다(`full`과 따로). 서버가 꽉 찼거나 종료 중이면 그 검사가 먼저라 Token을 쓰지 않는다. 리뷰 수정 A3부터 쿠키가 맞는 요청만 Token을 쓴다(쿠키 없는 첫 요청은 쓰지 않는다). 표는 1024칸 고정이고 IP 해시로 칸을 고른다. 해시에는 서버 시작 때 `RandomNumberGenerator`로 만든 salt를 섞어(리뷰 수정 A2) 어떤 주소들이 같은 칸에 오는지 코드만으로 알 수 없다. 한 칸에 오는 IP들은 그 칸의 Bucket을 나눠 쓴다. 빈 칸만 가득 찬 Bucket으로 시작하므로, 충돌하는 두 IP를 번갈아 써도 Bucket 하나 이상을 얻지 못한다(리뷰 1차). 대가로 정상 IP가 바쁜 IP와 충돌하면 함께 거절될 수 있지만 1024칸이라 드물다. 연결과 해제를 빠르게 반복해 공유 Control 채널을 채우는 것을 막는다.
- **IP당 동시 연결 수(리뷰 수정 A2, SEC-3):** 같은 칸에 지금 열린 연결이 `MaxConnectionsPerIp`(기본 4, 0 = 끔)개면 `ServerFull`로 거절하고 `rejects perIp`로 센다. 연결 수는 Accept 직후 수신 스레드가 올리고 `OnPeerDisconnected`에서 내린다(그 콜백은 Game Loop의 `Close`나 LiteNetLib 스레드에서도 오므로 `Interlocked`, `PeerState.ConnectSlot`). 한 주소가 슬롯을 모두 잡지 못한다. PC방·CGNAT처럼 한 주소에 사람이 많으면 설정으로 올린다.
- **전역 수락 빈도(리뷰 수정 A2):** 모든 주소를 합쳐 Token Bucket(`AcceptBurst` = MaxPlayers개, 그 뒤 초당 `AcceptsPerSecond` 20개). 넘는 요청은 `ServerFull`, `rejects accept`. Control 채널은 모든 Player의 메시지에 이 Bucket이 한 drain 사이에 들일 수 있는 연결 전부(연결마다 Connected·Join·Disconnected 3개)를 더한 크기다(`3 × (MaxPlayers + AcceptBurst + ⌈AcceptsPerSecond / SimHz⌉)`, `Server.md` "Queue"). 여러 주소가 함께 연결·해제를 반복해도 다른 Player의 메시지가 밀려 `ServerError`로 끊기지 않는다.
- **실패 출처 벌점(리뷰 수정 A6, SEC-14):** 플레이어 Tick 예외(서버 리뷰 M7)는 그 연결의 IP 칸과 함께 기록한다. 같은 칸이 60초 안에 3번이면 그 칸을 60초 동안 거절하고(`rejects penalized`, Health `penalties`. 리뷰 B 1·2차: 세션 키 blob 복호에 60초 안 세 번 실패한 칸도 60초 벌점), 10초 안 MaxPlayers번 실패의 경기 리셋은 출처가 둘 이상일 때만 한다(공격자 한 명이 리셋·서버 정지를 일으키지 못한다).
- 한 PC에서 봇 여러 명을 붙이는 부하 테스트는 `--Server:ConnectBurstPerIp=200 --Server:MaxConnectionsPerIp=200`을 준다(`LoadTest.md`).
- Fuzz 테스트(시드 고정): 무작위 바이트 10만 개를 모든 파서에(`StatsResponse`와 Phase 12의 `TransportRoute`·`DoorStates` 포함, `ProtocolFuzzTests`), NaN·±Inf·큰 값·음수 Seq·ViewTick이 든 입력으로 `Match`를 수백 Tick 돌려 예외가 없고 위치·체력이 유한함을(`InputFuzzTests`), 무작위 패킷을 보내는 peer 하나만 `Kicked`로 끊기고 다른 peer는 계속 Snapshot을 받음을(`FuzzIntegrationTests`) 확인한다.
- **Client 파서 범위(리뷰 수정 D3, SEC-24·25·26):** 정상 서버가 보내지 않는 값은 Shared 파서가 거절한다(Client·봇·QA 모두 같은 파서). 한계는 Shared `ProtocolLimits`에 있고, 서버 데이터 검사도 같은 상수를 쓰므로 서버가 보낸 값이 거절되는 일은 없다.

  | 패킷 | 거절하는 값 | 서버 쪽 보장 |
  |---|---|---|
  | `PlayerSpawned` | 위치·Yaw가 NaN·Infinity, 이름이 `IsValidPlayerName`(접속 이름 규칙)에 어긋남 | 이름은 접속 때 같은 규칙으로 검사한다 |
  | `JoinMatchResponse` | 결과 > `Resumed`, Ok·Resumed인데 내 Entity id 0 | MatchFull만 id 0으로 보낸다(그래서 MatchFull의 0은 받는다) |
  | `BuildCatalog` | 관심 영역 칸 크기가 20·40·80·160 m(`InterestCellSizes`)가 아님 | `building.json` 검사가 같은 목록을 쓴다 |
  | `ZoneState` | 중심 좌표 절댓값 > `HalfSize + 10000`, 반지름 < 0 또는 > 10000, NaN·Infinity | 원은 맵 안이다 |
  | `ProjectileSpawned`·`ProjectileState` | 위치 성분 절댓값 > 512 m, 속력 > 200 m/s, NaN·Infinity | 투사체는 맵 안의 눈에서 생기고 맵 안 면에서만 튄다. `weapons.json` 검사가 발사 속력과 `속력 + 중력 × 수명`을 200 이하로 막는다 |
  | `WeaponCatalog` 투사체 목록 | 속력 > 200, 중력 > 50, 폭발 반경 > 10 | `weapons.json` 검사가 같은 상수(`WeaponCatalog.MaxProjectile*` = `ProtocolLimits`)를 쓴다 |

  테스트: `ProtocolFuzzTests`의 거절 사례(`PlayerSpawned` NaN·제어 문자, `JoinMatchResponse` id 0, `BuildCatalog` 5 m, `ZoneState` 3e38, 투사체 속도 1e6 등)와 기존 왕복 테스트(정상 값 통과). Client 쪽 소비자 검사(Tick 창, 원격 플레이어 상한, 내 Spawn 조건)는 `Client.md`.
- 전투 입력: 조준 각이 NaN/Infinity면 그 입력은 발사하지 않는다(탄·간격 소모 없음). Pitch는 ±89°로 자른다. ViewTick(uint, 리뷰 수정 D2)은 되감기 범위로 자른다. Slot 비트가 둘 이상 켜져 있으면 교체하지 않는다. 명중 대상은 Client가 정하지 않는다.
- **되감기 RTT 제한(리뷰 수정 C3, SEC-5):** 되감기 폭은 사수마다 그 연결의 RTT로 정한다. `GameLoop.SweepPeers`가 Tick마다 LiteNetLib `RoundTripTime`(왕복 전체, ms)을 `PeerState.RttMs`에 옮기고, Match는 `rttOf`로 읽는다. ViewTick은 서버보다 보간(Snapshot 간격 2개 = 2 × `SnapshotEveryTicks`), Snapshot이 Client로 가는 길과 입력이 돌아오는 길(합쳐 RTT 하나), 입력 Drain 1 Tick만큼 뒤처진다. 그래서 허용 = clamp(RTT × SimHz / 1000 + 2 × SnapshotEveryTicks + 2, 2, MaxRewindTicks)다(+2 = Drain 1 + 흔들림 1). 30 Hz·Snapshot 2 Tick마다: RTT 20 ms → 6 Tick, 100 ms → 9, 200 ms → 12(예전 고정 상한과 같다, 그 위는 12로 잘린다). 허용보다 오래된 ViewTick은 그 끝으로 자르고 사수의 `RewindClamped`를 올린다(경기 기록 `rewind_clamped`). 지연이 작은 사수가 ViewTick을 조작해 0.4 s 전 위치를 맞히는 길이 막힌다.
- **무기 교체 대기(리뷰 수정 C2, SEC-8):** 손에 든 것이 바뀌면 `EquipTicks`(기본 0.4 s) 동안 쏘지 않는다(`Weapons.md` "무기 표").
- **줍기 시선(리뷰 수정 C4, SEC-9):** E 줍기는 가장 가까운 아이템이 눈(발 + 모드별 눈높이)에서 아이템 위 0.2 m까지 맵 상자·닫힌 문·지형·건설 조각에 막히지 않을 때만 된다(Container와 같은 `ClearSight`). 막히면 `NothingInRange`이고 다음 후보로 넘어가지 않는다(Client 줍기 안내와 같은 대상). 자원 자동 줍기도 보이는 것만 줍는다. 열린 문 틈 같은 경계에서는 안내가 뜨고 거절될 수 있다.
- **투사체 개인 상한(리뷰 수정 C5, SEC-13):** 한 사람(JoinOrder)이 동시에 가진 살아 있는 투사체(로켓·수류탄)는 `ProjectileRules.MaxPerOwner` 4개까지다. 넘으면 칸이 가득 찼을 때처럼 쏘지 않고 탄·수류탄을 쓰지 않는다(`ProjectilesRefused`). 32칸은 모두가 함께 쓴다. Client는 이 상한을 모르므로 그때 예측 예광탄이 한 번 보이고 다음 Snapshot이 탄 수를 고친다.
- **아이템 행동 간격(리뷰 수정 C6, SEC-20):** G 버리기와 E 줍기는 0.25 s(`ItemRules.ActionIntervalSeconds`, 30 Hz에서 8 Tick) 간격 하나를 같이 쓴다. 시도마다(성공·실패 무관) 간격이 새로 시작하고, 간격 안의 G·E 줍기는 답 없이 무시한다. 한 입력에 G와 E가 함께 있으면 G만 된다. 문·Container·차량 E와 자원 자동 줍기는 이 간격을 쓰지 않는다. 그래서 연타 Client도 Reliable 아이템 변경은 초당 4번 이하다.
- 아이템 입력: 줍기 대상·위치·수량은 Client가 보내지 않는다(Interact 비트뿐). Medkit·Shield Cell 비트가 함께 켜져 있으면 사용하지 않는다. 받은 패킷의 아이템 값(Kind, DefId, 등급, 수량, 비유한 위치)은 Client의 `TryRead`가 거른다.
- 위치는 서버가 계산하므로 순간이동·속도 조작은 구조적으로 불가능하다. Phase 12의 이동 모드도 같다. Client는 모드·속도·기력을 보내지 않고 입력만 보낸다.
- **상태 전환 규칙(Phase 12 D12):** `Step`의 규칙으로 서버가 정한다. 모르는 버튼 비트는 버린다(`Crouch` 비트는 통과한다).
  - 죽음·관전: 이동 없음.
  - `Transport`·`Freefall`·`Glide`·`Vault`: 사격·재장전·줍기·상호작용(문)·회복·칸 바꾸기·버리기가 안 된다. 이미 진행 중인 재장전·회복은 계속된다. 제한은 이동이 끝난 뒤의 모드로 판단한다.
  - `Freefall`·`Glide`: 웅크리기·슬라이드가 안 된다. 입력은 무시한다.
  - `Ground` → `Glide`·`Freefall` 직접 전환은 없다. 공중 모드는 `Transport`에서만 들어간다. `Transport`에서는 구간 앞의 Jump를 무시한다.
  - 서버의 이동 이상 검사(`movementAnomalies`)는 규칙이 아니라 자기 점검이다. 정상이면 0이다(`Movement.md`).
- 경기 패킷(Client의 `TryRead`): 알 수 없는 State, Alive > Participants, 비유한·음수 반지름, ShrinkEnd < ShrinkStart, Placement 0 또는 Participants 초과인 결과는 버린다.
- Phase 12 패킷(Client와 봇의 `TryRead`):
  - `TransportRoute`: 좌표가 ±127 밖(NaN 포함), 고도가 음수, 길이 0 Tick 또는 76800 Tick 초과면 읽기 실패.
  - `DoorStates`: 문 5개를 넘는 비트(bit5 이상)가 켜져 있으면 읽기 실패.
  - `PlayerRespawned`: `Mode`가 7(`Downed`, Phase 14. Phase 12에서는 6 `Transport`)보다 크면 읽기 실패. `PlayerDied`: `Cause`가 2(`Explosion`, Phase 17. Phase 12에서는 1)보다 크면 읽기 실패.
  - Snapshot 헤더: 수신자 블록의 기력이 10000(가득)보다 크면 헤더 읽기 실패(범위 밖 블록 거절).
  - Entity `Flags`의 모드가 `Downed`(7)보다 크면 읽기 실패가 아니라 `Ground`로 읽는다(Phase 14부터 3비트의 8개 값이 모두 모드라 지금은 일어나지 않는다. 앞으로 모드가 늘 때를 위한 경계다).
