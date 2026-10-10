# Squad (Phase 14)

Phase 19 + 리뷰 수정 기준(2026-10-09). 한 경기 안의 팀(Solo / Duo / Squad), 기절(DBNO), 소생, Reboot 카드와 스테이션. 설계 근거는 `Docs/specs/2026-10-08-phase14-squad-dbno-design.md`(D1–D17), 구현 기록(Spec과 다른 점)은 `Docs/plans/2026-10-08-phase14-squad-dbno.md`, 요청서는 `Docs/requests/2026-10-05-roadmap-phase13_5-19-request.md` §32–§48이다. 이후 닿는 Spec: 폭발 피해 `Docs/specs/2026-10-08-phase17-weapons-throwables-design.md`, 차량(치기·파괴 피해, 탄 사람은 소생·재투입 불가) `Docs/specs/2026-10-08-phase19-vehicle-design.md`. 패킷은 `Networking.md` "분대 (Phase 14)", 기어가기는 `Movement.md` "기절", QA는 `QA.md` "Scenario Library"(`Squad/` 시나리오, `squad` Suite)와 "Phase 14 Unity 검증", Client 표시는 `Client.md` "분대 (Phase 14)"에 있다.

코드: `Server/src/ProjectH.Server/Game/Match.Squad.cs`(Match의 partial, 규칙 전부), `Game/Squad/SquadCatalog.cs`(`squad.json`), `Game/Flow/MatchFlow.cs`(팀 배치), Shared `Protocol/SquadPackets.cs`, `Simulation/RebootStations.cs`.

## 설정

- `ServerOptions.TeamSize`: 1 Solo(기본), 2 Duo, 4 Squad. 1–4만 허용한다(시작 때 검증). QA·명령줄: `--Server:TeamSize=2`.
- `squad.json`(서버 GameData, 시작 때 검증. 틀리면 서버가 시작하지 않는다. 파일 없이 만드는 테스트는 `SquadCatalog.Default`):

| 키 | 기본 | 뜻 |
|---|---|---|
| `friendlyFire` | false | false만 구현했다(true는 거절) |
| `downedHealth` | 100 | 기절 체력 |
| `bleedOutSeconds` | 30 | 기절 체력이 0이 되는 시간 |
| `reviveSeconds` / `reviveRange` / `reviveHealth` | 5 / 2 m / 30 | 소생 시간·거리(발 사이 3D)·완료 체력 |
| `reviveCancelOnDamage` | true | 행위자가 피해를 받으면 소생·재투입 취소 |
| `rebootSeconds` / `rebootRange` | 5 / 3 m | 재투입 시간, 스테이션까지 지면 거리(높이 차는 2 m 이내) |
| `cardLifetimeSeconds` | 90 | 월드에 놓인 카드의 수명 |
| `maxCardsHeld` | 3 | 소지 카드 최대(1–3, `InventoryState.RebootCards`) |
| `stationCooldownSeconds` | 30 | 재투입 뒤 스테이션 대기 |
| `rebootLoadout` | Wisp SMG(id 3) + Light 30, 실드 0 | 재투입 장비. 무기 카탈로그에 권총이 없어 가장 약한 무기로 정했다. 무기 id·탄 상한은 `GameData`가 확인한다 |

Client는 표시용으로 거리·출혈 기본값을 복사해 쓴다(`SquadPrompt`). 서버 테스트 `SquadCatalogTests`가 두 값이 같은지 고정한다. 기어가기 속도·기절 높이는 예측 공용이라 Shared `MovementTuning`에 있다.

## 팀 (D1, D2)

- 입장할 때 `PlayerEntity.JoinOrder`(경기 객체의 입장 카운터, 늘어나기만 한다)를 받는다. EntityId는 비면 다시 쓰이므로 순서와 카드 주인 확인에 쓰지 않는다.
- 경기 시작 Tick(`StartMatch`)에 참가자를 입장 순서(= 플레이어 목록 순서)로 TeamSize씩 묶는다. TeamId는 1부터. 팀은 항상 2개 이상이다: 참가자 수 ≤ TeamSize이면 앞에서부터 절반(올림)과 나머지로 나눈다(Duo 2명 → 1:1, Squad 3명 → 2:1). Solo는 모두 다른 TeamId다. `MatchFlow.SetTeams`가 팀 수를 받는다.
- 개발 모드(`DevRespawn`)는 입장 때 정한다: 지금 있는 플레이어 기준으로 TeamSize칸이 남은 가장 작은 팀 번호, 없으면 아무도 쓰지 않는 가장 작은 번호(재접속이 많아도 번호가 돌아 겹치지 않는다. Solo는 언제나 서로 다른 팀).
- 대기실, 결과 뒤 리셋, 경기 중 늦게 들어온 관전자는 TeamId 0이다. 모든 Gameplay 판정은 `Match.SameTeam(a, b) = a.TeamId != 0 && a.TeamId == b.TeamId` 하나로 한다(0은 자기 자신과도 같은 팀이 아니다).
- `TeamState`는 받는 사람의 자기 팀만 보낸다(적 팀 구성은 보내지 않는다). 경기 시작, 경기 중 Join·Resume, 상태·10 단위로 올린 체력·카드 플래그가 바뀐 Tick 끝. 나간 구성원은 목록에서 빠진다(Count가 줄어든다). Solo에서도 1명짜리 팀으로 간다.

## 아군 사격 (D3)

사격 광선은 같은 팀 플레이어를 건너뛴다(관통). 팀원 뒤의 적은 맞는다. 자기장·낙하 피해는 그대로다. Client는 팀원의 피격 Collider를 꺼서 조준점이 팀원에 멈추지 않게 한다.

## 치명 경로 하나 (D5, D6)

사격(`ApplyHit`, Phase 19 차량 치기 포함), 폭발(`ApplyExplosionHit`, Phase 17 로켓·수류탄과 Phase 19 차량 파괴 피해), 자기장(`UpdateZone`), 낙하(`ApplyFallDamage`, 기절한 채 기어서 떨어져도), QA `damagePlayer`가 체력 0을 만들면 모두 `ApplyFatal`을 지난다.

1. 이미 기절한 사람 → 탈락. 처치 = 마무리한 사람, 공격자가 없으면(자기장·낙하) 기절시킨 사람(`DownedBy`, 아직 경기에 있을 때만).
2. 같은 팀에 서 있는(살아 있고 기절 아닌) 다른 구성원이 있으면 → 기절.
3. 아니면 → 탈락(`Kill`). Solo는 언제나 여기다(기절이 없다).

QA `killPlayer`는 기절 없이 바로 `Kill`이다(시나리오 준비용). `HitConfirmed.Killed`는 탈락일 때만 true다(기절시킨 명중은 false).

## 기절 (D4, D5)

- 이동 모드 `Downed`(7). 서버만 들어가고 나온다. Alive 비트는 켜진 채라 모두가 보고 적이 마무리할 수 있다. 피격 상자 높이 0.9 m.
- 들어갈 때: 체력 = `downedHealth`, 실드 0, `DownedBy`·원인 기록, 진행 중인 재장전·회복·자기 소생/재투입 취소, `PlayerDowned` 방송.
- `ActionsAllowed`(Ground·Crouch·Slide)에서 빠지므로 사격·휘두르기·건설·편집·줍기·아이템 사용·문·Material 줍기가 같은 검사 하나로 막힌다.
- **출혈:** 서버가 센다(Client 타이머를 믿지 않는다). 체력은 정수라 나머지를 쌓는다: 매 Tick `BleedCarry += downedHealth`, `BleedCarry ≥ bleedOutTicks`마다 체력 −1(기본 9 Tick마다 1, 900 Tick에 100). 받는 피해도 이 체력을 깎는다. 소생 중에는 멈춘다. 0이 되면 탈락(처치 = 기절시킨 사람). Self 체력 칸이 기절 체력을 보인다.
- 기절도 `Alive`라 경기의 생존 수(`MatchState.Alive`)에 들어가고, 연결이 끊기면 지금의 유예 규칙을 그대로 받는다(유예 중에도 출혈·피해·소생이 된다).

## 분대 전멸과 배치 (D6)

- 팀에 서 있는 구성원이 없어지면 기절한 구성원 모두가 같은 Tick에 탈락한다(처치 = 각자 `DownedBy`). 검사는 Up 수가 줄어드는 모든 곳에서 한다: 기절·탈락(`Kill`), 경기 이탈(`RemovePlayer`), 유예 만료(같은 경로). 소생·재투입 완료는 Up을 늘리므로 전멸이 생기지 않는다.
- `MatchFlow`는 사람 수(`Alive`, 기절 포함)와 남은 팀 수(`TeamsAlive`)를 따로 센다. 팀의 마지막 구성원이 탈락하는 순간 팀 배치 = 그 순간 남은 팀 수이고, 구성원(이미 탈락한 사람, 나간 사람의 기록 포함) 모두 같은 배치를 갖는다. `ShouldFinish` = 남은 팀 ≤ 1.
- **`PlayerDied.Placement`(리더 결정 2026-10-08):** 경기 참가자의 탈락이면 항상 1 이상이다. 팀이 전멸했으면 팀 배치, 팀이 살아 있으면 그 순간 남은 팀 수(자기 팀 포함, 잠정 값)를 보낸다. 0은 늦게 들어온 관전자 안내(`KillerId 0, Placement 0, Cause Zone`)에만 쓴다. 최종 배치는 팀 전멸이나 경기 종료 때 정해지고 `MatchResult`로 간다. Solo는 탈락이 언제나 팀 전멸이라 지금과 같다.
- **결과 화면:** 경기가 끝나면(`Finished`) 진행 중인 소생·재투입을 끊고, 결과 화면 동안 출혈·소생·재투입이 돌지 않는다(배치 1이 정해진 뒤 기절한 우승 팀원이 탈락하거나 재투입이 배치를 바꾸지 않게). 기절한 사람은 라운드 리셋까지 기절한 채로 있다.
- **경기 종료:** 전멸하지 않은 팀의 구성원(서 있음·기절·카드 대기 중, 나간 사람의 기록 포함)은 모두 배치 1이다. 마지막 팀들이 같은 Tick에 전멸하면 나중에 처리된 팀(배치 1)이 이긴다. `WinnerId` = 우승 팀에서 경기에 남은 가장 작은 Entity id. `MatchResult.Participants` = 팀 수, `Placement` = 팀 배치. DB 기록(`MatchRecord`)은 스키마를 바꾸지 않고 팀 배치를 저장한다.
- **Solo 회귀 기준:** TeamSize 1에서 배치·`MatchResult`·DB 기록은 지금과 같다(기존 Solo 테스트를 고치지 않고 통과).

## 소생 (D7, D8)

- 입력 비트 `InteractHeld`(16384): Client는 E가 눌려 있는 동안 매 입력에 켠다. `Interact`(누른 순간)는 줍기·문 그대로다.
- **E 우선순위:** 소생 대상이나 쓸 수 있는 스테이션이 범위 안이면 그 Tick의 E 누름은 문·Container(Phase 16)·차량 타기(Phase 19)·줍기를 하지 않는다(`Map.md` "문"의 E 규칙).
- **시작:** 서 있고 행동 가능 모드인 플레이어가 `InteractHeld`이고, 같은 팀의 기절 구성원이 `reviveRange` 안에 있으며, 두 눈(기절 0.6 m) 사이에 맵 상자·닫힌 문·채집물·지형·조각이 없고, 아직 아무도 그 사람을 소생하지 않으며, 그 자리에서 웅크린 상자(1.2 m)가 무엇에도 박히지 않으면 시작한다. 가장 가까운 대상. 진행 중인 회복은 끊는다. 한 대상에 소생자 한 명, 한 소생자는 한 대상.
- **진행:** `reviveSeconds`. 대상의 출혈이 멈춘다. 서버 Tick으로만 센다.
- **취소:** E를 놓음, 거리 > `reviveRange + 0.5`, 소생자 피해(`reviveCancelOnDamage`), 소생자 기절·사망·연결 끊김(유예 중에는 놓친 입력 반복이 있어도 바로 취소), 대상 탈락, 소생자의 다른 행동(Fire·회복 키를 누르고 있음, 무기 키·도구 키·드롭·재장전 누름, 배치·편집 성공).
- **완료:** 체력 `reviveHealth`, 실드 0. 서 있는 상자(1.8 m)가 박히지 않으면 `Ground`, 아니면 `Crouch`(이후 평소 자세 규칙으로 선다). 완료 순간 웅크린 상자도 들어가지 않으면(소생 중에 기어서 낮은 틈에 들어감) 완료하지 않고 취소한다(일어서며 조각을 뚫지 않게).
- 진행 상태는 `ChannelState`로 행위자 팀 전원(대상 포함)에게 간다: 시작(Active, 끝 Tick)과 끝(완료·취소).

## Reboot 카드 (D9)

- 팀에 서 있는 구성원이 남아 있는 상태에서 탈락하면 탈락 위치에 `RebootCard` 월드 아이템이 놓인다(`Amount` = 주인 Entity id). 서버만 아는 값: 주인 `JoinOrder`, 팀, 사라지는 Tick(`WorldItem.CardOwner`·`CardTeam`·`ExpireTick`).
- **보이는 사람:** 주인 팀에게만 `ItemSpawned`·`ItemRemoved`·`WorldItems`로 간다. 줍기(`FindNearest`)도 같은 팀 카드만 본다.
- **수명:** 월드에 놓여 있는 동안만 센다(`cardLifetimeSeconds`). 줍고 있는 동안은 사라지지 않는다. 소지자가 탈락해 다시 떨어지면 새 수명으로 놓인다.
- **월드 아이템 상한:** 카드는 지우지 않는다(상한 256, Loot Point 아이템 + 최대 참가자 수 < 256, `SquadMatchTests`가 고정).
- **소지:** 인벤토리 카드 칸(`Inventory.CardOwners`, 최대 `maxCardsHeld`). `InventoryState.RebootCards`로 본인에게 간다.
- **사라지는 때:** 주인이 경기를 떠남(연결 끊김 즉시 제거, 유예 만료) → 월드의 카드와 팀원이 든 카드 모두. 그래서 다시 쓰인 Entity id가 카드를 가로채지 않는다. 팀 전멸 → 떨어뜨리지 않고 들고 있던 것도 사라진다. 소지자 탈락(팀 생존) → 몸 주위에 떨어진다. 라운드 리셋 → 월드 아이템과 함께.

## Reboot Station (D10)

- Shared `RebootStations.All`: 4개(Rustvale −42, 42 / Gearworks 52, 38 / Lookout 40, −38 / Stonefield −50, −54), Y = 지형 높이. 충돌체가 아니다. `RebootStationsTests`가 맵 상자·문·채집물에서 2 m, Loot Point에서 떨어져 있고 재투입 자리(1.2 m)가 평평한지 확인한다.
- **사용(§44 검증):** 카드를 가진 서 있는 플레이어가 스테이션 `rebootRange` 안(높이 차 2 m)에서 `InteractHeld`, 스테이션 대기 끝, 다른 사람이 쓰고 있지 않음, 소생 중 아님(채널은 하나). 진행·취소 규칙은 소생과 같다. 카드는 같은 팀에서만 주울 수 있어 카드 주인 팀 = 사용자 팀이다(완료 때 다시 확인).
- **완료:** 들고 있던 카드의 주인(탈락한 같은 팀 참가자) 모두를 스테이션 둘레 1.2 m 자리에 `Ground`로 되살린다(`PlayerRespawned`). 체력 100, 장비 `rebootLoadout`(원래 인벤토리는 없다), 처치 수는 유지, 배치는 0으로(아직 경기 중), `MatchFlow.RestorePlayer`로 사람 수를 되돌린다. 카드는 쓰이고 스테이션은 `stationCooldownSeconds` 대기.
- 대기 상태는 `RebootStations` 패킷(대기 마스크 + 끝 Tick)으로 바뀐 Tick 끝, 경기 시작·리셋, Join·Resume에 간다.

## 재접속 (D13)

- 기절한 사람도 `Alive`라 유예가 그대로 적용된다(출혈·피해·소생 계속).
- 소생자가 끊기면 바로 취소, 대상이 끊기면 소생이 이어진다.
- Resume은 `TeamState`, `RebootStations`, 팀의 진행 중인 `ChannelState`(Active)를 다시 보낸다. 기절은 Snapshot 모드로 복구된다.
- 카드 대기 중(탈락)인 사람이 끊기면 지금처럼 바로 제거되고 카드가 사라진다.

## 봇 (D15)

같은 팀(`TeamState`)을 표적으로 삼지 않는다. 기절한 적은 표적으로 남고 낮은 몸 가운데(0.45 m)를 겨눈다. 기절한 봇은 가장 가까운 서 있는 팀원 쪽으로 기어가고 1.5 m 안에서 멈춘다. 봇의 소생·재투입은 없다.

## 관측

- Health 줄 `squad downs= revives= reboots= bleedOuts= cardsDropped= cardsExpired= wipes= channelsCancelled=`(시작부터의 합계, 경기 리셋을 넘어 이어진다). Meter `projecth.squad.events`(Tag `event`).
- QA 관찰: 플레이어 `teamId`, `joinOrder`, `downed`, `downedBy`, `revivedBy`, `rebootCards`, `channel`(kind, target, station, endTick), 경기 `teamSize`, `teams`, `teamsAlive`, `worldCards`, `stations`. 사건 `PlayerDowned`, `PlayerRevived`, `PlayerRebooted`.

## 자원과 상한 (queue-cache)

| 저장소 | 크기 | 제거 |
|---|---|---|
| `Match._sentTeams`, `_teamScratch`, `_teamOut` | 팀 id 256칸 고정 | 경기 시작·라운드 리셋에 지운다 |
| `Match._stationEnd` | 4 | 대기 끝 Tick 값, 리셋에 0 |
| `Match._visibleItems` | 256(Join·Resume에서만 씀) | 매번 덮어쓴다 |
| `Inventory.CardOwners` | 3 | 사용·탈락(드롭)·주인 이탈·팀 전멸·`Clear` |
| 월드 카드 | ≤ 참가자 수 | 수명·줍기·주인 이탈·팀 전멸·라운드 리셋 |
| `_leftParticipants` | ≤ MaxPlayers | 경기 시작에 지운다(팀 배치는 기록에 고쳐 쓴다) |
| `PlayerEntity.DownedBy`·`ReviveTarget`·`RevivedBy` | 참조 1개씩 | 소생·탈락·리셋에 지운다. 나간 사람을 가리킬 수 있지만 처치는 경기에 있을 때만 준다(`DownedKiller`) |

Tick마다 하는 일(출혈, 채널 검사, 팀 상태 비교, 카드 수명 검사)은 할당하지 않는다(`SquadMatchTests.SquadTicks_AllocateNothing`). 카드 수명 검사는 카드가 없으면 바로 끝난다. Lock은 없다(Game Loop 스레드 전용).

## 테스트

- Server: `SquadMatchTests`(팀, 아군 사격, 기절·출혈, 분대 전멸·팀 배치, 소생, 카드·스테이션, 할당 없음. 실제 입력으로 `Match.Tick`), `SquadCatalogTests`(`squad.json` 읽기·검증, Client 표시 복사본(`SquadPrompt`)과 기본값 비교, `MatchFlow`의 팀 수), `QaSquadTests`(QA 명령 `downPlayer`·`giveRebootCard`·`setStationCooldown`과 관찰 필드).
- Shared: `SquadSharedTests`(`Downed` 이동 모드, 분대 패킷), `RebootStationsTests`(스테이션 배치).
- Bots: `BotSquadTests`(팀원을 표적으로 삼지 않음, 기절한 적은 표적, 기절하면 팀원 쪽으로 기어감).
- Client EditMode: `SquadStateTests`(`TeamState`·채널 진행, 서버 Tick 기준), `SquadHudTextTests`(분대 HUD 문구), `SquadPromptTests`(소생·재투입 안내 대상, 출혈 초), `SpectatorTargetsTests`(`FollowSquad`: 탈락하면 살아 있는 팀원부터 관전).
- QA: `squad` Suite(헤드리스 8개, TeamSize 2)와 Unity `visual_squad`(`QA.md` "Scenario Library", "Phase 14 Unity 검증").

## Known Issues

- 소생은 대상 자리에 웅크린 상자(1.2 m)가 들어가야 시작되는데(계획 S1) Client 안내에는 이 검사가 없다. 그래서 낮은 틈 같은 드문 자리에서는 안내가 떠도 소생이 시작되지 않는다.
- 출혈 문구가 밝은 배경에서 잘 안 읽힌다.
- 기절 팀원과 문이 함께 있는 곳에서 E를 누르면 Client의 문 예측이 한 번 틀릴 수 있다(E 우선순위는 서버만 안다). 서버 `DoorStates`가 바로 고친다(계획 C6).
- 봇은 소생·재투입을 하지 않는다(D15).
