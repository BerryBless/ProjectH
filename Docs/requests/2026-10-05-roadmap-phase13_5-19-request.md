# ProjectH Battle Royale — Next Development Roadmap

현재 ProjectH는 Unity Client + .NET 10 Dedicated Server + MySQL 기반의 Server Authoritative Battle Royale 게임이다.

현재 `origin/main` 기준 핵심 시스템은 Phase 0~13까지 구현되어 있다.

이번 작업의 목표는 다음 순서대로 게임을 확장하는 것이다.

```text
QA Tool → main merge
↓
Editor 수동 검증
↓
Phase 13.5 — Building Edit
↓
Phase 14 — Squad / DBNO / Revive / Reboot
↓
Phase 15 — Minimap / Full Map / Ping
↓
Phase 16 — Loot Chest / Supply Drop
↓
Phase 17 — Weapons / Throwables
↓
Phase 18 — Audio
↓
Phase 19 — Vehicle
```

각 Phase를 한꺼번에 구현하지 않는다.

반드시 하나의 Phase를 완료하고 검증한 다음 다음 Phase로 이동한다.

---

# 0. 개발 공통 원칙

기존 코드와 아키텍처를 먼저 분석한다.

이미 존재하는 기능을 중복 구현하지 않는다.

기존 시스템을 최대한 재사용한다.

다음을 불필요하게 새로 만들지 않는다.

```text
새 Network Framework
새 Entity Framework
Actor Framework
Message Broker
Microservice
새 Event Bus
새 DI Framework
새 Serialization Framework
```

현재 프로젝트 구조 안에서 가장 단순하게 구현한다.

---

# 1. Server Authority

다음 결과는 항상 Server가 최종 결정한다.

```text
Movement

Damage

Hit

Death

Inventory

Loot

Building

Building Edit

Team

DBNO

Revive

Reboot

Zone

Vehicle Gameplay State

Match Result
```

Client는 Prediction / Presentation 역할을 담당할 수 있다.

---

# 2. 기존 성능 기반 보호

현재 서버는:

```text
Simulation:
30 Hz

Snapshot:
15 Hz

Max Players:
100

Player Snapshot:
13 B
```

구조를 사용한다.

기존 성능 기반을 불필요하게 훼손하지 않는다.

특히:

```text
Game Loop Blocking

Tick마다 DB Query

무제한 Queue

전체 Entity O(N²) 검색

불필요한 Snapshot 증가

Hot Path Allocation 증가
```

를 피한다.

---

# 3. 코드 주석 규칙

이번 작업에서 새로 작성하거나 수정한 함수 / 메서드에는 항상 간단한 주석을 작성한다.

형식:

```csharp
// 기능: 이 함수가 수행하는 역할.
// 입력: 주요 Parameter의 의미.
// 출력: 반환값 또는 실행 결과.
```

예:

```csharp
// 기능: 대상 구조물에 편집 상태를 적용한다.
// 입력: player - 편집 요청자, pieceId - 구조물 ID, editMask - 편집 형태.
// 출력: 편집 성공 여부.
private bool ApplyBuildingEdit(
    Player player,
    ulong pieceId,
    ushort editMask)
{
}
```

기존 주석이 코드 변경 후 틀렸다면 반드시 같이 수정한다.

코드를 그대로 읽어주는 내부 주석은 만들지 않는다.

---

# 4. Git 작업 원칙

Phase마다 논리적인 단위로 Commit한다.

예:

```text
feat: add building edit system

feat: add squad and dbno

feat: add minimap and ping

feat: add loot containers

feat: expand weapon and throwable systems
```

사용자가 명시적으로 Push를 요청하지 않았다면 자동 Push하지 않는다.

---

# STEP 1 — QA Tool → Main Merge

현재:

```text
qa-tool
```

Branch에 다음 기능이 구현되어 있고 origin에 Push되어 있다.

```text
QA-1
Scenario Runner

QA-2
Web UI

QA-3
UDP Fault Proxy

QA-4
Unity Automation

QA-5
Repeat / Seed Sweep / Baseline / Recording

Stress Scenario 27개

Server Process Management

matchLoop

QA Documentation
```

먼저 qa-tool Branch를 main에 통합한다.

---

# 5. Merge 전 검증

먼저:

```text
main

qa-tool
```

차이를 분석한다.

다음을 확인한다.

```text
Merge Conflict

Server Production Code 변경

QA Mode Protection

Protocol 변경

Build 영향

Test 영향
```

---

# 6. QA 안전성 재검증

QA 기능은 다음 조건을 유지해야 한다.

```text
Production에서는 비활성화

기본 Bind 127.0.0.1

QA 명령 Bounded Queue

Game Loop 영향 최소

QA Tool 종료 시 자식 Process 정리

Production Protocol과 QA Control 분리
```

---

# 7. Merge 후 검증

최소:

```text
dotnet build

Server Tests

QA Tool Tests

suite:smoke

suite:pre-push
```

를 실행한다.

환경 의존 Scenario는 SKIPPED가 정상일 수 있다.

실제로 실행할 수 없는 항목은 실행했다고 보고하지 않는다.

---

# 8. Merge 완료 조건

```text
[ ] main + qa-tool 통합

[ ] Build PASS

[ ] Server Test PASS

[ ] QA Test PASS

[ ] Smoke PASS

[ ] Pre-Push PASS

[ ] Production QA Guard 유지

[ ] Merge 결과 Commit
```

완료 후 다음 단계로 이동한다.

---

# STEP 2 — Editor Manual Validation

새 기능 개발 전에 현재 Client를 실제 Unity Editor에서 검증한다.

자동화로 검증된 내용을 다시 전부 확인하는 것이 목적이 아니다.

현재 자동 검증이 부족한 시각적 기능만 확인한다.

---

# 9. Phase 11 UI 검증

확인:

```text
Title

Connect

ESC Menu

Disconnect

Reconnect

Result

Statistics

Kill Feed

Player Name

Korean Font

Korean IME

F1 Debug
```

---

# 10. Phase 13 Building 검증

확인:

```text
Build Preview

Valid / Invalid Preview

Wall

Floor

Ramp

Roof

Turbo Build

Material HUD

Wood / Stone / Metal

Structure Damage

Construction Visual

Collapse Visual

Build Collision
```

---

# 11. 발견된 문제 처리

Critical / High 수준 Gameplay 문제는 다음 Phase 전에 수정한다.

단순 Visual Polish는 기록하고 다음 단계로 진행할 수 있다.

결과를:

```text
QA/Reports

또는

Docs
```

현재 프로젝트 방식에 맞게 남긴다.

---

# STEP 3 — Phase 13.5 Building Edit

이번 Phase의 목표는 현재 Building 시스템을 실제 전투에서 빠르게 편집할 수 있게 만드는 것이다.

핵심 플레이:

```text
Build
↓
Edit
↓
Open
↓
Shoot
↓
Reset / Rebuild
```

---

# 12. Edit Mode

Player는 자신이 편집 가능한 구조물에 Edit Mode를 시작할 수 있다.

기본적으로:

```text
자신이 만든 Build Piece
```

만 편집할 수 있게 한다.

향후 Squad 공유 편집은 Phase 14에서 확장 가능하도록 한다.

---

# 13. Edit Target

Camera / Aim 방향에 있는 가까운 Build Piece를 선택한다.

Server가 최종 대상과 거리 조건을 검증한다.

Client Target을 그대로 신뢰하지 않는다.

---

# 14. Wall Edit

Wall은 논리적인:

```text
3 x 3
```

Edit Grid를 가진다.

예:

```text
111
111
111
```

선택 영역에 따라 형태가 변한다.

최소 지원:

```text
Window

Door

Half Wall

Large Opening
```

실제 형태는 현재 Mesh 구조에 맞게 설계한다.

---

# 15. Edit Mask

가능하면 Wall Edit 상태를 compact한 Bit Mask로 표현한다.

예:

```text
9-bit Edit Mask
```

또는 현재 구조에 더 적합한 최소 데이터 구조를 선택한다.

---

# 16. Floor Edit

Floor를 부분적으로 제거해서:

```text
Half

Quarter

Diagonal
```

형태를 지원한다.

처음부터 모든 조합을 지원할 필요는 없다.

플레이 가치가 높은 기본 조합부터 구현한다.

---

# 17. Roof Edit

Roof / Cone 편집을 지원한다.

목표:

```text
경사 방향 생성

통과 경로 생성

높이 조절
```

복잡한 모든 Variant보다 핵심 Variant를 먼저 만든다.

---

# 18. Ramp Edit

필요하다면 Ramp 방향 전환을 지원한다.

기존 Rotation과 Edit가 중복되는 경우 가장 단순한 구조를 선택한다.

---

# 19. Edit Preview

Client는 선택 중인 Edit 결과를 즉시 Preview한다.

Network로 Preview Selection을 매 Frame 보내지 않는다.

---

# 20. Confirm

Edit Confirm 시에만 Server로 요청한다.

개념:

```text
BuildEditRequest

PieceId

EditMask / Variant

Sequence
```

---

# 21. Server Validation

Server가 확인한다.

```text
Player Alive

Match Playing

Build 존재

Edit 가능한 Piece

Ownership

Edit Range

Line Of Sight

Edit State Valid

Collision Valid

Sequence Valid
```

---

# 22. Collision Validation

Edit 결과 때문에:

```text
Player가 구조물 안에 갇힘

불가능한 Collision 생성

Terrain 관통
```

등이 발생하지 않는지 검증한다.

---

# 23. Prediction

Client는 Confirm 시 즉시 Edit Visual을 적용할 수 있다.

```text
Predicted Edit
↓
Server Accept
↓
Confirmed
```

또는:

```text
Predicted Edit
↓
Server Reject
↓
Rollback
```

---

# 24. Health 유지

Edit한다고 구조물 HP가 초기화되면 안 된다.

다음을 유지한다.

```text
BuildPieceId

Owner

Material

Current Health

Construction State
```

형태만 변경한다.

---

# 25. Support

Edit 이후 Support 구조가 변경될 수 있다면 필요한 Support 재검증을 한다.

전체 World Build를 다시 검사하지 않는다.

관련 Piece 주변만 검사한다.

---

# 26. Edit Replication

Build 전체를 다시 전송하지 않는다.

가능하면:

```text
BuildEdited
```

Event 하나로 전달한다.

예:

```text
PieceId

EditVariant / Mask

Sequence
```

---

# 27. Reconnect

Reconnect / Interest Area 진입 시 현재 최종 Edit 상태가 정상 동기화되어야 한다.

---

# 28. Race

다음 Race를 테스트한다.

```text
Edit 중 Piece Destroy

Edit + Damage Same Tick

Edit + Collapse

Edit + Disconnect

Edit + Reconnect

Repeated Confirm

Duplicate Request
```

---

# 29. 빠른 전환

다음 플레이가 자연스러워야 한다.

```text
Weapon
→ Build
→ Edit
→ Confirm
→ Weapon
```

입력 전환 때문에 전투 흐름이 끊기지 않아야 한다.

---

# 30. Phase 13.5 QA

Scenario 추가:

```text
building_edit_wall

building_edit_reset

building_edit_damage

building_edit_destroy_race

building_edit_reconnect

building_edit_spam
```

실제 QA Framework 구조에 맞춰 이름을 결정한다.

---

# 31. Phase 13.5 완료 조건

```text
[ ] Wall Edit

[ ] Floor Edit

[ ] Roof Edit

[ ] Preview

[ ] Confirm

[ ] Reset

[ ] Server Validation

[ ] Client Prediction

[ ] Reject Rollback

[ ] Health 유지

[ ] Collision 갱신

[ ] Reconnect Sync

[ ] 관련 QA PASS

[ ] stress-quick PASS
```

---

# STEP 4 — Phase 14 Squad / DBNO / Revive / Reboot

Solo 시스템 위에 Team 기반 Battle Royale를 추가한다.

처음부터 복잡한 Party Service / Matchmaking Service를 만들지 않는다.

현재 하나의 Match 내부 Team부터 구현한다.

---

# 32. Team

Player에:

```text
TeamId
```

개념을 추가한다.

지원:

```text
Solo

Duo

Squad
```

최대 Team Size는 Config.

---

# 33. Friendly Identification

Client에서 팀원을 구분한다.

```text
Name

Team Marker

HUD Color / Indicator
```

Gameplay Logic은 TeamId를 기준으로 한다.

---

# 34. Friendly Fire

기본 정책은:

```text
Friendly Fire OFF
```

로 한다.

Config로 바꿀 수 있게 할 수 있지만 우선 하나의 정책만 구현해도 된다.

---

# 35. DBNO

Team Member가 살아 있는 상황에서 Health가 0이 되면 바로 탈락시키지 않는다.

```text
Alive
↓
DBNO
↓
Revived

또는

DBNO
↓
Eliminated
```

---

# 36. DBNO 상태

DBNO 중:

```text
이동 제한

공격 불가

Build 제한

Item 사용 제한

Bleed Out
```

이 적용된다.

최소한 기어다니는 이동을 지원한다.

---

# 37. Bleed Out

DBNO에는 제한 시간이 있다.

Server가 관리한다.

Client Timer를 신뢰하지 않는다.

---

# 38. 추가 Damage

DBNO Player가 Damage를 받으면 Bleed Out 또는 DBNO Health가 감소한다.

0이 되면 완전 탈락한다.

---

# 39. Revive

살아 있는 팀원이 가까이에서 Interact를 유지하면 Revive할 수 있다.

Server 검증:

```text
Same Team

Distance

Line Of Sight

Reviver Alive

Target DBNO

Revive Duration
```

---

# 40. Revive 취소

다음에서 취소:

```text
거리 벗어남

Reviver Damage

Reviver Death

Target Elimination

Input Release
```

정책은 데이터화한다.

---

# 41. Squad Elimination

Team 전체가:

```text
Eliminated
```

되었을 때 Squad가 완전히 탈락한다.

DBNO Player가 남아 있다면 Match Alive Count 계산을 일관되게 처리한다.

---

# 42. Reboot Item

완전 탈락한 Player는 Reboot용 Item/Card를 남긴다.

Team Member만 사용할 수 있다.

Lifetime 제한을 둘 수 있다.

---

# 43. Reboot Station

맵에 Reboot Station을 배치한다.

Team Member가 Card를 가지고 Station을 사용하면 일정 시간 후 Player를 다시 투입한다.

---

# 44. Reboot Validation

Server:

```text
Same Team

Card 존재

Station 사용 가능

Range

Cooldown

Revive 중 방해 여부
```

를 검증한다.

---

# 45. Reboot Spawn

Reboot된 Player는 제한된 기본 장비 또는 Config 기반 장비로 복귀한다.

원래 Inventory 전체를 자동 복구하지 않는다.

정책은 Config.

---

# 46. Squad Spectator

죽은 Player는 살아 있는 Team Member를 관전한다.

Team Member가 없으면 기존 Spectator / Result Flow를 따른다.

---

# 47. Team Reconnect

Disconnect Grace와 Team 상태가 정상 유지되어야 한다.

특히:

```text
DBNO 중 Disconnect

Revive 중 Disconnect

Reboot 후 Disconnect
```

를 테스트한다.

---

# 48. Squad QA

추가:

```text
duo_basic

dbno

dbno_bleedout

revive

revive_cancel

squad_elimination

reboot

reconnect_dbno
```

---

# STEP 5 — Phase 15 Minimap / Full Map / Ping

정보 전달 시스템을 구현한다.

Squad 플레이에 필수적인 기능이다.

---

# 49. Minimap

HUD에 Minimap을 추가한다.

표시:

```text
Player

Team Member

Zone

POI

Ping
```

---

# 50. Full Map

Map Key로 전체 지도를 열 수 있게 한다.

표시:

```text
현재 위치

Team

현재 Zone

다음 Zone

수송기 Route

POI

Waypoint

Ping
```

---

# 51. Map Coordinate

World Coordinate와 Map UI Coordinate 변환을 하나의 공통 시스템으로 만든다.

여러 UI에서 별도 계산식을 만들지 않는다.

---

# 52. Map 성능

매 Frame 전체 Icon을 새로 생성하지 않는다.

Icon Object를 재사용한다.

변경된 정보만 갱신한다.

---

# 53. Personal Waypoint

Player가 Map에 개인 Waypoint를 지정할 수 있다.

Server 또는 Team Replication 정책에 따라 필요한 정보만 공유한다.

---

# 54. Team Ping

World에서 Ping할 수 있다.

종류:

```text
Location

Enemy

Item

Danger
```

처음에는 너무 많은 Ping Type을 만들지 않는다.

---

# 55. Ping Server Validation

Spam을 막기 위해 Rate Limit을 둔다.

Client가 임의의 거대한 좌표를 보내지 못하게 한다.

---

# 56. Ping Lifetime

일정 시간이 지나면 자동 제거한다.

Team별 Ping Count에도 상한을 둔다.

---

# 57. Context Ping

Aim 대상에 따라:

```text
Enemy

Item

World
```

를 구분할 수 있다.

Server가 최종 유효성을 검증해야 하는 중요한 정보만 검증한다.

---

# 58. Phase 15 QA

```text
minimap_position

map_zone

map_team

ping_world

ping_enemy

ping_rate_limit
```

---

# STEP 6 — Phase 16 Loot Chest / Supply Drop

현재 Ground Loot에 Battle Royale 특유의 Loot Container를 추가한다.

---

# 59. Loot Chest

맵에 Chest Spawn Point를 둔다.

Chest는 Match 시작 시 Server가 생성 여부와 Loot를 결정한다.

---

# 60. Chest Interaction

Player가 가까이에서 Interact하면 연다.

Server 검증:

```text
Chest 존재

아직 열리지 않음

Player Alive

Distance

Line Of Sight
```

---

# 61. Chest Loot

Loot Table을 사용한다.

예:

```text
Weapon

Ammo

Healing

Shield

Build Resource
```

Client가 Loot 결과를 결정하지 않는다.

---

# 62. Chest State

```text
Closed

Opened
```

정도로 단순하게 유지한다.

열린 Chest는 같은 Match에서 다시 생성하지 않는다.

---

# 63. Ammo Box

필요하다면 Chest 시스템을 재사용하여 작은 Container를 구현한다.

주로:

```text
Ammo

Small Resource
```

를 제공한다.

---

# 64. Supply Drop

경기 중 Server가 특정 시점에 Supply Drop을 생성한다.

Flow:

```text
Spawn Event
↓
Air Drop
↓
Landing
↓
Interact
↓
High Tier Loot
```

---

# 65. Supply Drop 위치

Safe Zone 또는 현재 경기 상태를 고려해서 유효 위치를 선택한다.

플레이어 바로 위에 불합리하게 Spawn하지 않는다.

---

# 66. Supply Drop Networking

전체 World Snapshot에 매 Tick 싣지 않는다.

Spawn / State Change Event 기반으로 관리한다.

---

# 67. Loot QA

```text
chest_open

chest_duplicate_open

chest_loot

ammo_box

supply_drop_spawn

supply_drop_open
```

---

# STEP 7 — Phase 17 Weapons / Throwables

전투 콘텐츠를 확장한다.

기존 Weapon System을 재사용한다.

---

# 68. Weapon Archetypes

최소:

```text
Assault Rifle

SMG

Shotgun

Sniper / Marksman

Pistol
```

각 역할이 명확하도록 한다.

---

# 69. Weapon Data

Server JSON 또는 현재 Weapon Data 구조에:

```text
Damage

FireRate

Magazine

Reload

Range

Spread

Recoil

StructureDamage

AmmoType

ProjectileType
```

등을 둔다.

Client와 Server 값이 불일치하지 않도록 한다.

---

# 70. Shotgun

다수 Pellet을 Server가 판정한다.

한 발당 Packet 여러 개를 보내지 않는다.

하나의 Fire Action에서 Server가 Pellet Spread를 생성한다.

---

# 71. Sniper

Projectile 또는 Hitscan 중 현재 게임 규모와 Network 구조에 적합한 것을 선택한다.

Projectile을 도입한다면 Server Authoritative다.

---

# 72. Projectile Framework

Projectile이 필요한 무기에 공통으로 사용할 최소 시스템을 만든다.

대상:

```text
Rocket

Grenade

일부 특수 무기
```

모든 총기를 Projectile로 강제로 바꾸지 않는다.

---

# 73. Grenade

투척물 기본형:

```text
Throw

Server Projectile

Bounce / Collision

Fuse

Explosion

Radial Damage
```

---

# 74. Explosion

Server가:

```text
Distance

Line Of Sight

Damage Falloff
```

를 계산한다.

Player와 Build Piece 양쪽에 피해를 줄 수 있다.

---

# 75. Rocket

Rocket Launcher 같은 느린 Projectile 무기를 추가할 수 있다.

Server가 Projectile 위치와 Collision을 관리한다.

Client는 Presentation을 보간한다.

---

# 76. Structure Damage

무기별:

```text
Player Damage

Structure Damage
```

밸런스를 분리할 수 있다.

Building Combat가 무너지지 않도록 한다.

---

# 77. Weapon Rarity

기존 등급 시스템과 Weapon Stat을 연결한다.

불필요한 Item Class 복제를 만들지 않는다.

---

# 78. Weapon QA

```text
weapon_ar

weapon_shotgun

weapon_sniper

grenade

explosion_falloff

rocket

structure_damage
```

---

# STEP 8 — Phase 18 Audio

Gameplay 정보 전달을 위한 Audio를 추가한다.

단순 BGM Phase가 아니다.

---

# 79. Audio 우선순위

1.

```text
Gunshot
```

2.

```text
Footstep
```

3.

```text
Building
```

4.

```text
Damage / Shield
```

5.

```text
Loot
```

6.

```text
Zone
```

7.

```text
UI
```

순으로 중요하게 본다.

---

# 80. Footstep

Movement State와 Surface에 따라 Footstep을 재생한다.

예:

```text
Walk

Sprint

Crouch

Slide
```

Surface:

```text
Ground

Wood

Stone

Metal
```

처음부터 너무 많은 Surface Type을 만들지 않는다.

---

# 81. Directional Audio

3D Spatial Audio를 사용해서 적 위치를 소리로 판단할 수 있어야 한다.

특히:

```text
Footstep

Gunshot

Build

Harvest
```

가 중요하다.

---

# 82. Distance

거리에 따라:

```text
Volume

Attenuation
```

을 적용한다.

필요하면 먼 총성은 다른 Clip / Filter를 사용할 수 있으나 첫 구현에서는 단순하게 한다.

---

# 83. Build Audio

```text
Place

Edit

Damage

Destroy

Collapse
```

를 구분한다.

---

# 84. Audio 성능

AudioSource를 무한히 생성하지 않는다.

동시 재생 수에 Budget을 둔다.

멀리 있거나 중요도가 낮은 Audio를 제한할 수 있다.

---

# 85. Audio QA

자동으로 소리가 '좋은지' 판단하려 하지 않는다.

QA에서는:

```text
Audio Event Trigger

Audio Source 생성 여부

잘못된 반복

중복 Trigger
```

등 로직만 검증한다.

실제 청각 품질은 Manual Check로 둔다.

---

# STEP 9 — Phase 19 Vehicle

게임 플레이를 충분히 안정화한 후 Vehicle을 구현한다.

---

# 86. 첫 Vehicle

처음부터 여러 종류를 만들지 않는다.

기본 4륜 Vehicle 하나만 구현한다.

목표:

```text
Enter

Exit

Drive

Steer

Brake

Damage

Destroy
```

---

# 87. Vehicle Authority

Vehicle Simulation의 최종 권한은 Server가 가진다.

Client Prediction이 필요하면 기존 Movement Prediction 경험을 활용한다.

---

# 88. Occupants

최소:

```text
Driver

Passenger
```

개념을 지원할 수 있는 구조로 만든다.

첫 구현은 Driver만으로 시작해도 된다.

---

# 89. Enter / Exit

Server 검증:

```text
Vehicle 존재

Seat 비어 있음

Distance

Player Alive

Vehicle 상태
```

---

# 90. Vehicle Physics

AAA Vehicle Physics를 만들지 않는다.

현재 Game Server가 Unity Physics를 사용하지 않는 점을 고려하여 단순하고 예측 가능한 Vehicle 모델을 사용한다.

예:

```text
Position

Heading

Speed

Acceleration

Steering

Ground Check
```

---

# 91. Vehicle Collision

최소:

```text
Terrain

Static Building

Player Build

Player
```

와의 상호작용을 정의한다.

처음부터 완전한 물리 시뮬레이션을 만들지 않는다.

---

# 92. Vehicle Damage

Weapon과 충돌로 Damage를 받을 수 있다.

HP가 0이면 Destroyed 상태가 된다.

---

# 93. Player Build

Vehicle이 Build Piece와 충돌할 때 정책을 정의한다.

예:

```text
Vehicle 정지

또는

충돌 Damage
```

초기에는 단순하고 일관된 방식 하나를 선택한다.

---

# 94. Vehicle Network

모든 Frame 상태 전송 금지.

Server Tick / Snapshot 구조에 맞게 Replication한다.

Interest Management를 사용한다.

---

# 95. Vehicle QA

```text
vehicle_spawn

vehicle_enter

vehicle_drive

vehicle_exit

vehicle_damage

vehicle_destroy

vehicle_disconnect
```

---

# STEP 10 — 각 Phase 공통 QA Gate

각 Phase를 완료한 뒤 반드시 다음 순서로 검증한다.

```text
Build
↓
Unit Tests
↓
관련 QA Scenario
↓
suite:smoke
↓
suite:pre-push
↓
필요 시 stress-quick
```

Server Hot Path 또는 Network 변경이 크다면:

```text
stress-gameplay
```

또는 관련 Stress Suite를 실행한다.

---

# 96. 성능 회귀

다음이 변경된 Phase에서는 반드시 성능을 확인한다.

```text
Building Edit

Squad

Projectile

Vehicle

Interest Management
```

확인:

```text
Tick P50

Tick P95

Tick P99

Tick Max

CPU

Memory

GC

Network Send

Network Receive
```

기존 Baseline과 Workload가 같은 경우에만 직접 비교한다.

---

# 97. Memory

Match Restart 후 다음 상태가 누적되지 않는지 확인한다.

```text
Edit State

Team

DBNO

Revive

Reboot

Ping

Map Marker

Loot Chest

Supply Drop

Projectile

Audio Runtime State

Vehicle
```

---

# 98. Queue

새 Queue를 추가할 경우 반드시:

```text
Maximum Size

Producer

Consumer

Full Policy
```

가 있어야 한다.

무제한 Queue를 만들지 않는다.

---

# 99. Network

새 Replication을 추가할 때:

```text
매 Snapshot에 반드시 필요한가?
```

부터 확인한다.

상태 변화 이벤트로 충분하다면 Event Replication을 우선한다.

---

# 100. Protocol

Protocol 변경 시 반드시 확인한다.

```text
Client

Server

Headless Bot

QA Actor
```

모두 동일 변경을 반영해야 한다.

---

# 101. QA Tool 업데이트

새 Gameplay Phase를 추가할 때 QA Tool도 같이 확장한다.

예:

```text
새 Input
→ Headless Actor Action 추가

새 Server State
→ 필요한 Assertion 추가

새 Gameplay Event
→ 필요하면 QA Event 추가
```

하지만 QA API를 Gameplay 시스템과 1:1로 무분별하게 확장하지 않는다.

---

# 102. Manual Validation

자동화하기 어려운 Visual / Feel은 각 Phase 완료 후 Manual Checklist를 남긴다.

예:

```text
Building Edit Feel

Map UX

Ping Visibility

Loot Chest FX

Grenade Trajectory Feel

Footstep Direction

Vehicle Handling
```

---

# 103. Phase 완료 보고

각 Phase 완료 후 다음 형식으로 보고한다.

```text
## Phase

Phase 번호 / 이름

## 구현

구현 내용

## Client

Client 변경

## Server

Server 변경

## Protocol

Protocol 변경

## QA

추가한 Scenario

실행 결과

## Tests

Server:
QA:
Client:

## Performance

성능에 영향이 있는 Phase만 작성

## Manual Check

직접 확인해야 하는 항목

## Known Issues

실제 발견된 문제

## Commit

Commit Hash / Message

## Next

다음 Phase
```

---

# 104. 다음 Phase 자동 진행 조건

현재 Phase에서:

```text
Critical Bug

High Severity Regression

Build Failure

Test Failure

핵심 QA Failure
```

가 남아 있다면 다음 Phase로 넘어가지 않는다.

문제를 먼저 해결한다.

단순 Cosmetic Issue는 Known Issue로 남기고 진행할 수 있다.

---

# 105. 전체 개발 순서

반드시 다음 순서를 기본으로 한다.

```text
1.
qa-tool → main merge

2.
현재 Client Editor 수동 검증

3.
Phase 13.5
Building Edit

4.
Phase 14
Squad / DBNO / Revive / Reboot

5.
Phase 15
Minimap / Full Map / Ping

6.
Phase 16
Loot Chest / Ammo Box / Supply Drop

7.
Phase 17
Weapon Expansion / Projectile / Throwables

8.
Phase 18
Gameplay Audio

9.
Phase 19
Vehicle
```

사용자가 우선순위를 변경하지 않는 한 이 순서를 따른다.

---

# 106. 지금 시작할 작업

먼저 새로운 Gameplay Phase를 구현하지 않는다.

현재 작업 시작 시:

```text
git 상태 확인

↓

origin/main 확인

↓

qa-tool Branch 확인

↓

main과 qa-tool 차이 분석

↓

Merge 위험 확인

↓

qa-tool을 main에 통합

↓

Build / Tests

↓

QA Smoke / Pre-Push

↓

Editor Manual Validation 항목 정리
```

순서로 진행한다.

QA Tool 통합과 현재 Client 검증이 완료된 다음에만:

```text
Phase 13.5 — Building Edit
```

구현을 시작한다.

Phase 13.5가 완료되기 전에 Phase 14 코드를 선제적으로 구현하지 않는다.

---

# 최종 원칙

```text
기능을 많이 만드는 것보다
각 Phase가 실제 플레이 가능한 상태로 끝나는 것을 우선한다.

기존 시스템을 재사용한다.

Server Authority를 유지한다.

Client는 반응성을 위해 Prediction할 수 있다.

Hot Path를 불필요하게 무겁게 만들지 않는다.

새 Gameplay 기능에는 QA Scenario를 같이 추가한다.

Visual 기능은 Editor에서 직접 확인한다.

Phase 하나를 검증한 뒤 다음 Phase로 넘어간다.
```
