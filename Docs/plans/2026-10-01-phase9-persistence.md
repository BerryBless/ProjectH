# Phase 9 Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 경기가 끝나면 참가자 전원의 기록(순위, 처치, 피해, 생존 시간)과 누적 통계를 MySQL에 한 Transaction으로 저장한다.
- Game Loop는 DB를 기다리지 않는다. 기록은 넘치지 않는 큐로 별도 Writer에 넘긴다.
- DB가 없어도 서버는 정상 동작한다.

**Architecture:**
- **Game Loop.** `Match`가 경기 종료 Tick에 `MatchRecord`를 만들어 `Action<MatchRecord>` sink로 넘긴다. 운영에서 sink는 `MatchHistoryQueue.TryEnqueue`이고, 이 큐는 bounded Channel이며 넘치면 버린다.
- **Writer.** Hosted Service `MatchHistoryWriter`가 큐를 읽어 `MatchStore`로 저장한다. `MatchStore`는 MySqlConnector를 쓰고, 한 Transaction에서 계정 upsert → 프로필 → 경기 → 참가자 → 통계 누적 순으로 처리한다.
- **개발 DB.** 개발용 MySQL은 `docker-compose.yml`로 띄운다.

**Tech Stack:** .NET 10, MySqlConnector 2.6.2(새 NuGet, 사용자 확인됨), MySQL 8.4(Docker), xUnit.

**Spec:** `Docs/specs/2026-10-01-phase9-persistence-design.md`

## Global Constraints

- **Commit:** 작업 Branch에서 Task마다 Commit한다. Push는 Phase가 끝난 뒤 `github-push` 스킬로 한다. Force Push는 하지 않는다.
- **공통 규칙:** 모든 코드는 `.claude/skills/game-core-rules/SKILL.md`를 따른다.
  - DB Connection은 `await using`으로 반환한다.
  - Transaction은 경기 하나 단위로 짧게 한다.
  - Lock은 없다. 카운터는 Interlocked다.
- **Game Loop는 DB를 기다리지 않는다(절대 규칙 4).** Game Loop가 하는 것은 sink 호출(`TryEnqueue`, 블록 없음)뿐이다. Join은 DB를 쓰지 않는다.
- **큐(§44):** 최대 `QueueCapacity`(16)이다. 생산자는 Game Loop, 소비자는 Writer 하나다. 넘침 정책은 Reject이고, 버린 수는 `Dropped`로 센다.
- **수치:**
  - 설정: `QueueCapacity` 16(1–1024), `MaxAttempts` 3(1–10), `ShutdownDrainSeconds` 5(0–60)
  - 연결 문자열: `Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev;Connection Timeout=5`
- **테이블:** `account`, `player_profile`, `player_stats`, `game_match`, `match_player`(spec D3)
- **DB 테스트:** 환경 변수 `PROJECTH_TEST_MYSQL`이 있을 때만 돈다. 없으면 건너뛴다.
- **Docker:** 컨테이너 이름은 `projecth-mysql`이고 127.0.0.1:3306이다.
  - 띄우기: `docker compose up -d`. 내리기: `docker compose down`이다. 데이터 볼륨은 지우지 않는다(`-v` 금지). 사용자의 데이터일 수 있다.
  - 다른 컨테이너는 건드리지 않는다.
- **범위:** Unity Client와 Shared는 바꾸지 않는다.
- **명령 실행 위치:** 저장소 루트(`E:/popol/ProjectH`)에서 실행한다. 시작 기준은 서버 테스트 643개다.

## Review Focus

- **DB가 꺼져 있거나 없는 상태로 서버를 켜는 경우.** 서버가 정상 동작해야 한다. 기록은 버리고 센다(Task 2 `TheWriter_WithNoDatabase_DiscardsAndKeepsRunning`).
- **저장 도중 한 행이 실패하는 경우.** 그 경기의 어떤 행도 남지 않아야 한다(Task 2 `AFailingSave_ChangesNothing`).
- **경기 중 이탈자.** 기록에 빠지면 통계가 틀린다(Task 1 `AParticipantWhoLeaves_IsStillInTheRecord`).
- **같은 플레이어의 두 번째 경기.** 통계가 덮어써지지 않고 누적되어야 한다(Task 2 `SavingMatches_WritesHistory_AndAddsUpStatistics`).
- **서버 종료 직전에 끝난 경기.** 남은 기록을 저장하고 멈춰야 한다(Task 2 `TheWriter_SavesWhatTheGameEnqueues`의 Stop 경로, spec D8).

---

### Task 1: Game Loop 경기 기록 (`MatchRecord`, `Match` sink)

**Files:**
- Create: `Server/src/ProjectH.Server/Persistence/MatchRecord.cs`
- Modify: `Server/src/ProjectH.Server/Game/PlayerEntity.cs`, `Game/Match.cs`, `GameLoop.cs`
- Modify (테스트): `Server/tests/ProjectH.Server.Tests/Game/RoyaleHarness.cs`
- Test: `Server/tests/ProjectH.Server.Tests/Persistence/MatchRecordTests.cs`

**Interfaces:**
- Produces:
  - `MatchRecord(int Round, DateTime StartedUtc, DateTime EndedUtc, string? WinnerDevPlayerId, IReadOnlyList<PlayerRecord> Players)`
  - `PlayerRecord(string DevPlayerId, byte Placement, int Kills, int Damage, int SurvivalMs)`
  - `Match(..., Vector3[]? dropPoints = null, Action<MatchRecord>? matchSink = null)`
  - `GameLoop(..., Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null)`
  - `PlayerEntity.DamageDealt`, `PlayerEntity.EliminatedTick`
  - `RoyaleHarness(..., Action<MatchRecord>? matchSink = null)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`Server/src/ProjectH.Server/Persistence/MatchRecord.cs`(테스트가 쓰는 타입이다. 먼저 만든다):

```csharp
using System;
using System.Collections.Generic;

namespace ProjectH.Server.Persistence;

// Phase 9 D4: everything saved about one finished match, built once by the game loop when the match ends and handed
// to the writer. Immutable, so the writer thread can read it while the game loop goes on (§37).
public sealed record MatchRecord(
    int Round,
    DateTime StartedUtc,
    DateTime EndedUtc,
    string? WinnerDevPlayerId,
    IReadOnlyList<PlayerRecord> Players);

// One participant of the match, including players who left mid-match (an elimination, Phase 5 D10).
public sealed record PlayerRecord(
    string DevPlayerId,
    byte Placement,
    int Kills,
    int Damage,
    int SurvivalMs);
```

아래 스크립트를 저장소 밖에 `plan9_harness.py`로 저장하고, `python <경로>/plan9_harness.py E:/popol/ProjectH`로 실행한다.

```python
import os, sys
os.chdir(sys.argv[1])
p = 'Server/tests/ProjectH.Server.Tests/Game/RoyaleHarness.cs'
s = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in s else '\n'
pairs = [
("""        Vector3[]? dropPoints = null)
    {""",
"""        Vector3[]? dropPoints = null, Action<ProjectH.Server.Persistence.MatchRecord>? matchSink = null)
    {"""),
("""            TestGameData.Create(lootJson: lootJson, zonesJson: zonesJson), send, loadout, dropPoints: dropPoints ?? LobbyRingDrops);""",
"""            TestGameData.Create(lootJson: lootJson, zonesJson: zonesJson), send, loadout, dropPoints: dropPoints ?? LobbyRingDrops,
            matchSink: matchSink);"""),
]
for a, b in pairs:
    a2 = a.replace('\n', nl); b2 = b.replace('\n', nl)
    assert s.count(a2) == 1, a[:60]
    s = s.replace(a2, b2)
open(p, 'w', encoding='utf-8', newline='').write(s)
print('harness ok')
```

`Server/tests/ProjectH.Server.Tests/Persistence/MatchRecordTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;
using ProjectH.Server.Tests.Game;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Tests.Persistence;

// Phase 9 D4: what the game loop records when a match finishes (no database involved).
public class MatchRecordTests
{
    private static (RoyaleHarness h, List<MatchRecord> records) Harness()
    {
        var records = new List<MatchRecord>();
        var h = new RoyaleHarness(TestGameData.CombatLoadout, matchSink: records.Add);
        return (h, records);
    }

    [Fact]
    public void AFinishedMatch_IsRecordedOnce_WithPlacementsKillsDamageAndSurvival()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.Ticks(30);   // one second in
        h.ShootUntilDead(a, b);
        h.Ticks(2);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(1, record.Round);
        Assert.Equal("p1", record.WinnerDevPlayerId);
        Assert.True(record.StartedUtc <= record.EndedUtc);
        Assert.Equal(2, record.Players.Count);
        PlayerRecord winner = record.Players.Single(p => p.DevPlayerId == "p1");
        PlayerRecord loser = record.Players.Single(p => p.DevPlayerId == "p2");
        Assert.Equal(1, winner.Placement);
        Assert.Equal(1, winner.Kills);
        Assert.Equal(CombatRulesMaxPool, winner.Damage);          // shield 50 + health 100, no overkill
        Assert.Equal(2, loser.Placement);
        Assert.Equal(0, loser.Kills);
        Assert.Equal(0, loser.Damage);
        Assert.InRange(loser.SurvivalMs, 1000, 10000);
        Assert.True(winner.SurvivalMs >= loser.SurvivalMs);

        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        Assert.Single(records);   // the result screen and the next countdown record nothing
    }

    private const int CombatRulesMaxPool = 100 + TestGameData.LoadoutShield;

    [Fact]
    public void AParticipantWhoLeaves_IsStillInTheRecord()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.Join(3);
        h.RunToMatch();
        h.Ticks(15);
        h.Match.Leave(3);
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        h.Ticks(2);

        MatchRecord record = Assert.Single(records);
        Assert.Equal(3, record.Players.Count);
        PlayerRecord left = record.Players.Single(p => p.DevPlayerId == "p3");
        Assert.Equal(3, left.Placement);   // the first one out
        Assert.InRange(left.SurvivalMs, 400, 700);   // 15 ticks at 30 Hz after the start, plus the leave tick
    }

    [Fact]
    public void ASpectatorWhoJoinedMidMatch_IsNotInTheRecord()
    {
        (RoyaleHarness h, List<MatchRecord> records) = Harness();
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Join(3);   // spectates (Phase 5 D10)
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        h.Ticks(2);
        Assert.DoesNotContain(Assert.Single(records).Players, p => p.DevPlayerId == "p3");
    }

    [Fact]
    public void TheDevSandbox_RecordsNothing()
    {
        var records = new List<MatchRecord>();
        var match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(), static (_, _, _) => { },
            TestGameData.CombatLoadout, matchSink: records.Add);
        match.TryJoin(1, "p1");
        match.TryJoin(2, "p2");
        for (int i = 0; i < 300; i++) match.Tick();
        Assert.Empty(records);
    }

    [Fact]
    public void WithoutASink_AFinishedMatchAllocatesNoRecord()
    {
        var h = new RoyaleHarness(TestGameData.CombatLoadout);   // no sink: the default for tests and tools
        PlayerEntity a = h.Join(1);
        PlayerEntity b = h.Join(2);
        h.RunToMatch();
        h.Place(a, new Vector3(0f, 0f, -3f));
        h.Place(b, new Vector3(0f, 0f, 3f));
        h.ShootUntilDead(a, b);
        h.Ticks(2);
        Assert.Equal(MatchFlowState.Finished, h.Match.Flow.State);   // finished without a sink, nothing thrown
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~MatchRecordTests"`
Expected: 빌드 실패. `Match`와 `RoyaleHarness`에 `matchSink` 인자가 없다(CS1739).

- [ ] **Step 3: Game Loop 기록을 구현한다**

아래 스크립트를 `plan9_gameloop.py`로 저장하고 `python <경로>/plan9_gameloop.py E:/popol/ProjectH`로 실행한다. 원래 줄바꿈을 지킨다.

```python
import os, sys
os.chdir(sys.argv[1])

def edit(path, pairs):
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl); b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2)
    open(path, 'w', encoding='utf-8', newline='').write(s)

G = 'Server/src/ProjectH.Server/'
edit(G + 'Game/PlayerEntity.cs', [
("""    public bool Participant;
    public byte Placement;
    public int Kills;
""",
"""    public bool Participant;
    public byte Placement;
    public int Kills;
    // Phase 9 (§37): damage this player dealt to others during the match (shield and health actually removed, no
    // overkill), and the tick it was eliminated (0 = still in). Reset when a match starts.
    public int DamageDealt;
    public uint EliminatedTick;
"""),
])

edit(G + 'Game/Match.cs', [
("""using ProjectH.Server.Game.Zone;
""",
"""using ProjectH.Server.Game.Zone;
using ProjectH.Server.Persistence;
"""),
("""    private readonly int[] _dropOrder;
""",
"""    private readonly int[] _dropOrder;
    // Phase 9 D4: where a finished match's record goes (null = nothing is recorded, e.g. most tests). Called on the game
    // loop thread and must not block: production passes MatchHistoryQueue.TryEnqueue.
    private readonly Action<MatchRecord>? _matchSink;
    // Participants who left during the current match, recorded when they left (they are no longer in _players).
    // At most MaxPlayers entries; cleared when a match starts.
    private readonly List<PlayerRecord> _leftParticipants = new();
    private DateTime _matchStartedUtc;
"""),
("""    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null,
        Vector3[]? dropPoints = null)
    {""",
"""    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null,
        Vector3[]? dropPoints = null, Action<MatchRecord>? matchSink = null)
    {
        _matchSink = matchSink;"""),
("""        if (_flow.InMatch && player.Participant && player.Alive)
        {
            player.Alive = false;
            player.Placement = _flow.Eliminate();
            DropEverything(player);
        }
    }""",
"""        if (_flow.InMatch && player.Participant && player.Alive)
        {
            player.Alive = false;
            player.Placement = _flow.Eliminate();
            player.EliminatedTick = ServerTick;
            DropEverything(player);
        }
        // Phase 9: a participant who leaves still belongs to the match record.
        if (_flow.InMatch && player.Participant && _matchSink != null) _leftParticipants.Add(RecordOf(player, ServerTick));
    }"""),
("""    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
    {
        bool killed = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);
""",
"""    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
    {
        int before = target.Health + target.Shield;
        bool killed = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);
        if (_flow.InMatch && shooter.Participant && shooter != target) shooter.DamageDealt += before - (target.Health + target.Shield);
"""),
("""        WinnerId = winner?.EntityId ?? 0;
""",
"""        WinnerId = winner?.EntityId ?? 0;
        if (_matchSink != null) _matchSink(BuildRecord(now, winner));
"""),
("""        if (_flow.InMatch && victim.Participant)
        {
            placement = _flow.Eliminate();
            victim.Placement = placement;""",
"""        if (_flow.InMatch && victim.Participant)
        {
            placement = _flow.Eliminate();
            victim.Placement = placement;
            victim.EliminatedTick = ServerTick;"""),
("""            player.Participant = true;
            player.Placement = 0;
            player.Kills = 0;
        }""",
"""            player.Participant = true;
            player.Placement = 0;
            player.Kills = 0;
            player.DamageDealt = 0;
            player.EliminatedTick = 0;
        }
        _leftParticipants.Clear();
        _matchStartedUtc = DateTime.UtcNow;"""),
("""    // Phase 6 D9: resets the order to 0..n-1 and shuffles it""",
"""    // Phase 9 D4: the record of the match that just finished: every participant still here plus those who left. Built
    // once per match on the game loop thread (the only allocation of the finish tick); the sink must not block.
    private MatchRecord BuildRecord(uint now, PlayerEntity? winner)
    {
        var players = new List<PlayerRecord>(_players.Count + _leftParticipants.Count);
        foreach (var player in _players)
        {
            if (player.Participant) players.Add(RecordOf(player, now));
        }
        players.AddRange(_leftParticipants);
        return new MatchRecord(_flow.Round, _matchStartedUtc, DateTime.UtcNow, winner?.DevPlayerId, players);
    }

    // Survival runs from the match start to the elimination, or to `now` for a player still in.
    private PlayerRecord RecordOf(PlayerEntity player, uint now)
    {
        uint end = player.EliminatedTick != 0 ? player.EliminatedTick : now;
        int survivalMs = (int)((ulong)(end - _matchStartTick) * 1000UL / (ulong)_simHz);
        return new PlayerRecord(player.DevPlayerId, player.Placement, player.Kills, player.DamageDealt, survivalMs);
    }

    // Phase 6 D9: resets the order to 0..n-1 and shuffles it"""),
])

edit(G + 'GameLoop.cs', [
("""    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null)""",
"""    // matchSink: Phase 9, where finished matches are recorded (MatchHistoryQueue.TryEnqueue; null = not recorded).
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null)"""),
("""        _match = new Match(options, data, SendToPeer, loadout, dropPoints: dropPoints);""",
"""        _match = new Match(options, data, SendToPeer, loadout, dropPoints: dropPoints, matchSink: matchSink);"""),
])
print('game loop ok')
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~MatchRecordTests"`
Expected: PASS (5개)

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0. `dotnet test Server/ProjectH.Server.slnx` → 모두 통과(643 + 5). 기존 "Tick 할당 없음" 테스트도 통과해야 한다(sink가 없으면 기록을 만들지 않는다).

- [ ] **Step 5: Commit** — `feat(server): record finished matches on the game loop (Phase 9)`

---

### Task 2: MySQL 저장 (`MatchStore`, `MatchHistoryWriter`, 큐, 설정, Docker)

**Files:**
- Create: `docker-compose.yml`(저장소 루트)
- Modify: `Server/src/ProjectH.Server/ProjectH.Server.csproj`(`MySqlConnector` 2.6.2)
- Create: `Server/src/ProjectH.Server/Persistence/PersistenceOptions.cs`, `MatchHistoryQueue.cs`, `MatchStore.cs`, `MatchHistoryWriter.cs`
- Modify: `Server/src/ProjectH.Server/GameServerService.cs`, `Program.cs`, `appsettings.json`
- Test: `Server/tests/ProjectH.Server.Tests/Persistence/PersistencePartsTests.cs`, `MySqlTests.cs`

**Interfaces:**
- Consumes: Task 1 `MatchRecord`, `PlayerRecord`, `GameLoop(..., matchSink)`
- Produces:
  - `PersistenceOptions`: `Enabled`, `ConnectionString`, `QueueCapacity`, `MaxAttempts`, `ShutdownDrainSeconds`, `Validate()`
  - `MatchHistoryQueue(int)`: `TryEnqueue`, `Reader`, `Complete`, `Dropped`
  - `MatchStore(string)`: `EnsureSchemaAsync`, `SaveAsync`, `GetStatsAsync`, `GetHistoryAsync`
  - `PlayerStats` 레코드, `MatchHistoryEntry` 레코드
  - `MatchHistoryWriter`: `Saved`, `Failed`, `Discarded`

- [ ] **Step 1: 개발 DB와 패키지**

저장소 루트에 `docker-compose.yml`을 만든다.

```yaml
# Phase 9: local MySQL for development and the persistence tests. Development credentials only; a real deployment
# overrides them with environment variables (PROJECTH_DB_PASSWORD, PROJECTH_DB_ROOT_PASSWORD).
# Start: docker compose up -d   Stop: docker compose down   (add -v to drop the data volume)
services:
  mysql:
    image: mysql:8.4
    container_name: projecth-mysql
    environment:
      MYSQL_DATABASE: projecth
      MYSQL_USER: projecth
      MYSQL_PASSWORD: ${PROJECTH_DB_PASSWORD:-projecth_dev}
      MYSQL_ROOT_PASSWORD: ${PROJECTH_DB_ROOT_PASSWORD:-projecth_root_dev}
    ports:
      - "127.0.0.1:3306:3306"
    volumes:
      - projecth-mysql-data:/var/lib/mysql
    healthcheck:
      test: ["CMD", "mysqladmin", "ping", "-h", "127.0.0.1", "-uprojecth", "-p${PROJECTH_DB_PASSWORD:-projecth_dev}"]
      interval: 5s
      timeout: 3s
      retries: 20

volumes:
  projecth-mysql-data:
```

Run: `dotnet add Server/src/ProjectH.Server/ProjectH.Server.csproj package MySqlConnector --version 2.6.2`

Run: `docker compose up -d`. 그런 다음 `docker ps`에서 `projecth-mysql`이 `(healthy)`가 될 때까지 기다린다(첫 실행은 이미지 다운로드로 1–2분).

- [ ] **Step 2: 실패하는 테스트를 쓴다**

`Server/tests/ProjectH.Server.Tests/Persistence/PersistencePartsTests.cs`:

```csharp
using System;
using Microsoft.Extensions.Configuration;
using ProjectH.Server.Persistence;

namespace ProjectH.Server.Tests.Persistence;

// Phase 9 D6-D8: the queue between the game loop and the writer, and the options.
public class PersistencePartsTests
{
    private static MatchRecord Record(int round) => new(round, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>());

    [Fact]
    public void TheQueue_NeverBlocks_AndCountsWhatItDrops()
    {
        var queue = new MatchHistoryQueue(2);
        Assert.True(queue.TryEnqueue(Record(1)));
        Assert.True(queue.TryEnqueue(Record(2)));
        Assert.False(queue.TryEnqueue(Record(3)));   // full: rejected at once
        Assert.Equal(1, queue.Dropped);

        Assert.True(queue.Reader.TryRead(out MatchRecord? first));
        Assert.Equal(1, first!.Round);
        queue.Complete();
        Assert.False(queue.TryEnqueue(Record(4)));   // completed: rejected
        Assert.Equal(2, queue.Dropped);
        Assert.True(queue.Reader.TryRead(out MatchRecord? second));
        Assert.Equal(2, second!.Round);
    }

    [Fact]
    public void Options_Validate()
    {
        Assert.Null(new PersistenceOptions().Validate());
        Assert.Null(new PersistenceOptions { Enabled = true, ConnectionString = "Server=x" }.Validate());
        Assert.NotNull(new PersistenceOptions { Enabled = true, ConnectionString = " " }.Validate());
        Assert.NotNull(new PersistenceOptions { QueueCapacity = 0 }.Validate());
        Assert.NotNull(new PersistenceOptions { QueueCapacity = 1025 }.Validate());
        Assert.NotNull(new PersistenceOptions { MaxAttempts = 0 }.Validate());
        Assert.NotNull(new PersistenceOptions { ShutdownDrainSeconds = 61 }.Validate());
    }

    [Fact]
    public void TheShippedSettings_AreValid_AndPointAtTheLocalContainer()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        var options = new PersistenceOptions();
        config.GetSection("Persistence").Bind(options);
        Assert.Null(options.Validate());
        Assert.True(options.Enabled);
        Assert.Contains("Server=127.0.0.1", options.ConnectionString);
        Assert.Contains("Database=projecth", options.ConnectionString);
    }
}
```

`Server/tests/ProjectH.Server.Tests/Persistence/MySqlTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using ProjectH.Server.Persistence;

namespace ProjectH.Server.Tests.Persistence;

// Runs only when PROJECTH_TEST_MYSQL holds a connection string (Docs/Database.md: `docker compose up -d`, then
// PROJECTH_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev").
// Without it the test is reported as skipped, so `dotnet test` still passes on a machine with no database.
public sealed class MySqlFactAttribute : FactAttribute
{
    public const string Variable = "PROJECTH_TEST_MYSQL";

    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a MySQL connection string to run the database tests.";
    }

    public static string ConnectionString => Environment.GetEnvironmentVariable(Variable)!;
}

// Phase 9 against a real MySQL. Every test uses its own random DevPlayerIds, so tests never see each other's rows.
public class MySqlTests
{
    private static string NewId(string tag) => $"t{tag}-{Guid.NewGuid():N}"[..32];

    private static MatchRecord Match(int round, params PlayerRecord[] players)
    {
        string? winner = null;
        foreach (PlayerRecord p in players) if (p.Placement == 1) winner = p.DevPlayerId;
        DateTime end = DateTime.UtcNow;
        return new MatchRecord(round, end.AddMinutes(-4), end, winner, players);
    }

    private static async Task<MatchStore> StoreAsync()
    {
        var store = new MatchStore(MySqlFactAttribute.ConnectionString);
        await store.EnsureSchemaAsync(CancellationToken.None);
        await store.EnsureSchemaAsync(CancellationToken.None);   // idempotent
        return store;
    }

    [MySqlFact]
    public async Task SavingMatches_WritesHistory_AndAddsUpStatistics()
    {
        MatchStore store = await StoreAsync();
        string a = NewId("a"), b = NewId("b"), c = NewId("c");

        await store.SaveAsync(Match(1, new PlayerRecord(a, 1, 2, 300, 240000), new PlayerRecord(b, 2, 1, 150, 200000),
            new PlayerRecord(c, 3, 0, 20, 60000)), CancellationToken.None);
        await store.SaveAsync(Match(2, new PlayerRecord(a, 2, 0, 50, 90000), new PlayerRecord(b, 1, 3, 400, 250000)),
            CancellationToken.None);

        PlayerStats? statsA = await store.GetStatsAsync(a, CancellationToken.None);
        Assert.NotNull(statsA);
        Assert.Equal(2, statsA!.Matches);
        Assert.Equal(1, statsA.Wins);
        Assert.Equal(2, statsA.Kills);
        Assert.Equal(1, statsA.Deaths);
        Assert.Equal(350, statsA.Damage);
        Assert.Equal(330000, statsA.SurvivalMs);

        PlayerStats? statsC = await store.GetStatsAsync(c, CancellationToken.None);
        Assert.Equal(1, statsC!.Matches);
        Assert.Equal(0, statsC.Wins);

        IReadOnlyList<MatchHistoryEntry> history = await store.GetHistoryAsync(a, 10, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].MatchId > history[1].MatchId);   // newest first
        Assert.Equal(2, history[0].Round);
        Assert.Equal(2, history[0].Placement);
        Assert.Equal(3, history[1].Players);
        Assert.Equal(1, history[1].Placement);
        Assert.Equal(300, history[1].Damage);

        Assert.Null(await store.GetStatsAsync(NewId("nobody"), CancellationToken.None));
    }

    // One bad row rolls the whole match back: no match row, no history and no statistics for anyone in it.
    [MySqlFact]
    public async Task AFailingSave_ChangesNothing()
    {
        MatchStore store = await StoreAsync();
        string good = NewId("g");
        string tooLong = new string('x', 40);   // dev_player_id is VARCHAR(32): strict mode rejects it
        await Assert.ThrowsAsync<MySqlException>(() =>
            store.SaveAsync(Match(1, new PlayerRecord(good, 1, 1, 100, 1000), new PlayerRecord(tooLong, 2, 0, 0, 500)), CancellationToken.None));
        Assert.Null(await store.GetStatsAsync(good, CancellationToken.None));
        Assert.Empty(await store.GetHistoryAsync(good, 10, CancellationToken.None));
    }

    // The hosted writer: start, enqueue, the record lands in the database, stop drains.
    [MySqlFact]
    public async Task TheWriter_SavesWhatTheGameEnqueues()
    {
        var queue = new MatchHistoryQueue(4);
        var options = Options.Create(new PersistenceOptions { Enabled = true, ConnectionString = MySqlFactAttribute.ConnectionString });
        using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        string a = NewId("w"), b = NewId("x");
        Assert.True(queue.TryEnqueue(Match(7, new PlayerRecord(a, 1, 1, 150, 30000), new PlayerRecord(b, 2, 0, 0, 20000))));
        var clock = Stopwatch.StartNew();
        while (writer.Saved == 0 && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.Saved);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(1, (await new MatchStore(MySqlFactAttribute.ConnectionString).GetStatsAsync(a, CancellationToken.None))!.Wins);
    }
}

// Without a database the writer must not fail the server: records are discarded and counted. Needs no MySQL.
public class UnreachableDatabaseTests
{
    [Fact]
    public async Task TheWriter_WithNoDatabase_DiscardsAndKeepsRunning()
    {
        var queue = new MatchHistoryQueue(4);
        var options = Options.Create(new PersistenceOptions
        {
            Enabled = true,
            ConnectionString = "Server=127.0.0.1;Port=1;Database=projecth;User ID=nobody;Password=none;Connection Timeout=1",
            ShutdownDrainSeconds = 5,
        });
        using var writer = new MatchHistoryWriter(queue, options, NullLogger<MatchHistoryWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(new MatchRecord(1, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>())));
        var clock = Stopwatch.StartNew();
        while (writer.Discarded == 0 && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(1, writer.Discarded);
        Assert.Equal(0, writer.Saved);
    }
}
```

Run: `dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Persistence"`
Expected: 빌드 실패. `MatchHistoryQueue`, `PersistenceOptions`, `MatchStore`, `MatchHistoryWriter`가 없다(CS0246).

- [ ] **Step 3: 저장 계층을 만든다**

`Server/src/ProjectH.Server/Persistence/PersistenceOptions.cs`:

```csharp
namespace ProjectH.Server.Persistence;

// Phase 9: bound from the "Persistence" section of appsettings.json. The default connection string is the local
// development container (docker-compose.yml); a deployment overrides it with the environment variable
// Persistence__ConnectionString.
public sealed class PersistenceOptions
{
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
    // D6: bounded queue between the game loop and the writer. Full = the record is dropped and counted.
    public int QueueCapacity { get; set; } = 16;
    // D7: tries per match record before it is given up (logged with its round and players).
    public int MaxAttempts { get; set; } = 3;
    // D8: on shutdown, how long the writer may keep saving what is still queued.
    public int ShutdownDrainSeconds { get; set; } = 5;

    public string? Validate()
    {
        if (QueueCapacity < 1 || QueueCapacity > 1024) return "Persistence:QueueCapacity must be 1-1024.";
        if (MaxAttempts < 1 || MaxAttempts > 10) return "Persistence:MaxAttempts must be 1-10.";
        if (ShutdownDrainSeconds < 0 || ShutdownDrainSeconds > 60) return "Persistence:ShutdownDrainSeconds must be 0-60.";
        if (Enabled && string.IsNullOrWhiteSpace(ConnectionString)) return "Persistence:ConnectionString is required when Persistence:Enabled is true.";
        return null;
    }
}
```

`Server/src/ProjectH.Server/Persistence/MatchHistoryQueue.cs`:

```csharp
using System.Threading;
using System.Threading.Channels;

namespace ProjectH.Server.Persistence;

// Phase 9 D6 (§44): the only link between the game loop and the database writer.
// Maximum size: PersistenceOptions.QueueCapacity records. Producer: the game loop, once per finished match
// (TryEnqueue never blocks and never allocates beyond the channel slot). Consumer: MatchHistoryWriter, one reader.
// Overflow policy: Reject — a record that does not fit is dropped and counted (Dropped); the game never waits for
// the database. Complete() is called on shutdown so the writer can drain what is left.
public sealed class MatchHistoryQueue
{
    private readonly Channel<MatchRecord> _channel;
    private long _dropped;

    public MatchHistoryQueue(int capacity)
    {
        _channel = Channel.CreateBounded<MatchRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // with TryWrite: returns false instead of waiting
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public ChannelReader<MatchRecord> Reader => _channel.Reader;
    public long Dropped => Interlocked.Read(ref _dropped);

    // Game loop thread. False = full or completed: the record is dropped and counted.
    public bool TryEnqueue(MatchRecord record)
    {
        if (_channel.Writer.TryWrite(record)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
```

`Server/src/ProjectH.Server/Persistence/MatchStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace ProjectH.Server.Persistence;

public sealed record PlayerStats(string DevPlayerId, int Matches, int Wins, int Kills, int Deaths, long Damage, long SurvivalMs);

public sealed record MatchHistoryEntry(long MatchId, int Round, DateTime EndedUtc, int Players, byte Placement, int Kills, int Damage, int SurvivalMs);

// Phase 9 (§36-37): the MySQL side. Every call opens a pooled connection and returns it on the way out
// (await using), and a match is saved in one short transaction: accounts, profiles, the match, its players and the
// running statistics, or nothing. Only MatchHistoryWriter calls SaveAsync (one writer, so no row-lock ordering
// issues between transactions); the read methods are for tools and tests. Never called from the game loop.
public sealed class MatchStore
{
    // D3: idempotent schema, created at startup. Table names avoid MATCH (a reserved word).
    private static readonly string[] Schema =
    {
        """
        CREATE TABLE IF NOT EXISTS account (
          id BIGINT NOT NULL AUTO_INCREMENT,
          dev_player_id VARCHAR(32) NOT NULL,
          created_at DATETIME(3) NOT NULL,
          PRIMARY KEY (id),
          UNIQUE KEY ux_account_dev_player_id (dev_player_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS player_profile (
          account_id BIGINT NOT NULL,
          display_name VARCHAR(32) NOT NULL,
          updated_at DATETIME(3) NOT NULL,
          PRIMARY KEY (account_id),
          CONSTRAINT fk_player_profile_account FOREIGN KEY (account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS player_stats (
          account_id BIGINT NOT NULL,
          matches INT NOT NULL,
          wins INT NOT NULL,
          kills INT NOT NULL,
          deaths INT NOT NULL,
          damage BIGINT NOT NULL,
          survival_ms BIGINT NOT NULL,
          updated_at DATETIME(3) NOT NULL,
          PRIMARY KEY (account_id),
          CONSTRAINT fk_player_stats_account FOREIGN KEY (account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS game_match (
          id BIGINT NOT NULL AUTO_INCREMENT,
          round_no INT NOT NULL,
          started_at DATETIME(3) NOT NULL,
          ended_at DATETIME(3) NOT NULL,
          players TINYINT UNSIGNED NOT NULL,
          winner_account_id BIGINT NULL,
          PRIMARY KEY (id),
          KEY ix_game_match_ended_at (ended_at),
          CONSTRAINT fk_game_match_winner FOREIGN KEY (winner_account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
        """,
        """
        CREATE TABLE IF NOT EXISTS match_player (
          match_id BIGINT NOT NULL,
          account_id BIGINT NOT NULL,
          placement TINYINT UNSIGNED NOT NULL,
          kills SMALLINT UNSIGNED NOT NULL,
          damage INT NOT NULL,
          survival_ms INT NOT NULL,
          PRIMARY KEY (match_id, account_id),
          KEY ix_match_player_account (account_id, match_id),
          CONSTRAINT fk_match_player_match FOREIGN KEY (match_id) REFERENCES game_match (id),
          CONSTRAINT fk_match_player_account FOREIGN KEY (account_id) REFERENCES account (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin
        """,
    };

    private readonly string _connectionString;

    public MatchStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string sql in Schema)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // One transaction for the whole match. Returns the new game_match id.
    public async Task<long> SaveAsync(MatchRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        var accountIds = new long[record.Players.Count];
        long? winnerAccountId = null;
        for (int i = 0; i < record.Players.Count; i++)
        {
            PlayerRecord player = record.Players[i];
            accountIds[i] = await EnsureAccountAsync(connection, transaction, player.DevPlayerId, cancellationToken);
            if (player.DevPlayerId == record.WinnerDevPlayerId) winnerAccountId = accountIds[i];
        }

        long matchId;
        await using (var command = new MySqlCommand(
            "INSERT INTO game_match (round_no, started_at, ended_at, players, winner_account_id) " +
            "VALUES (@round, @started, @ended, @players, @winner); SELECT LAST_INSERT_ID();", connection, transaction))
        {
            command.Parameters.AddWithValue("@round", record.Round);
            command.Parameters.AddWithValue("@started", record.StartedUtc);
            command.Parameters.AddWithValue("@ended", record.EndedUtc);
            command.Parameters.AddWithValue("@players", record.Players.Count);
            command.Parameters.AddWithValue("@winner", winnerAccountId.HasValue ? winnerAccountId.Value : DBNull.Value);
            matchId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        for (int i = 0; i < record.Players.Count; i++)
        {
            PlayerRecord player = record.Players[i];
            await using (var command = new MySqlCommand(
                "INSERT INTO match_player (match_id, account_id, placement, kills, damage, survival_ms) " +
                "VALUES (@match, @account, @placement, @kills, @damage, @survival)", connection, transaction))
            {
                command.Parameters.AddWithValue("@match", matchId);
                command.Parameters.AddWithValue("@account", accountIds[i]);
                command.Parameters.AddWithValue("@placement", player.Placement);
                command.Parameters.AddWithValue("@kills", Math.Min(player.Kills, ushort.MaxValue));
                command.Parameters.AddWithValue("@damage", player.Damage);
                command.Parameters.AddWithValue("@survival", player.SurvivalMs);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = new MySqlCommand(
                "INSERT INTO player_stats (account_id, matches, wins, kills, deaths, damage, survival_ms, updated_at) " +
                "VALUES (@account, 1, @win, @kills, @death, @damage, @survival, UTC_TIMESTAMP(3)) AS new " +
                "ON DUPLICATE KEY UPDATE matches = player_stats.matches + 1, wins = player_stats.wins + new.wins, " +
                "kills = player_stats.kills + new.kills, deaths = player_stats.deaths + new.deaths, " +
                "damage = player_stats.damage + new.damage, survival_ms = player_stats.survival_ms + new.survival_ms, " +
                "updated_at = new.updated_at", connection, transaction))
            {
                bool won = player.Placement == 1;
                command.Parameters.AddWithValue("@account", accountIds[i]);
                command.Parameters.AddWithValue("@win", won ? 1 : 0);
                command.Parameters.AddWithValue("@kills", player.Kills);
                command.Parameters.AddWithValue("@death", won ? 0 : 1);
                command.Parameters.AddWithValue("@damage", player.Damage);
                command.Parameters.AddWithValue("@survival", player.SurvivalMs);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return matchId;
    }

    public async Task<PlayerStats?> GetStatsAsync(string devPlayerId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(
            "SELECT s.matches, s.wins, s.kills, s.deaths, s.damage, s.survival_ms FROM player_stats s " +
            "JOIN account a ON a.id = s.account_id WHERE a.dev_player_id = @dev", connection);
        command.Parameters.AddWithValue("@dev", devPlayerId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PlayerStats(devPlayerId, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt64(4), reader.GetInt64(5));
    }

    // Newest first, at most limit (1-100) entries.
    public async Task<IReadOnlyList<MatchHistoryEntry>> GetHistoryAsync(string devPlayerId, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(
            "SELECT m.id, m.round_no, m.ended_at, m.players, p.placement, p.kills, p.damage, p.survival_ms " +
            "FROM match_player p JOIN account a ON a.id = p.account_id JOIN game_match m ON m.id = p.match_id " +
            "WHERE a.dev_player_id = @dev ORDER BY m.id DESC LIMIT @limit", connection);
        command.Parameters.AddWithValue("@dev", devPlayerId);
        command.Parameters.AddWithValue("@limit", limit);
        var entries = new List<MatchHistoryEntry>(limit);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MatchHistoryEntry(reader.GetInt64(0), reader.GetInt32(1), reader.GetDateTime(2), reader.GetByte(3),
                reader.GetByte(4), reader.GetUInt16(5), reader.GetInt32(6), reader.GetInt32(7)));
        }
        return entries;
    }

    // The account id for a DevPlayerId, created (with its profile) on first sight. LAST_INSERT_ID(id) on the duplicate
    // path makes one statement return the existing id too.
    private static async Task<long> EnsureAccountAsync(MySqlConnection connection, MySqlTransaction transaction, string devPlayerId,
        CancellationToken cancellationToken)
    {
        long accountId;
        await using (var command = new MySqlCommand(
            "INSERT INTO account (dev_player_id, created_at) VALUES (@dev, UTC_TIMESTAMP(3)) " +
            "ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id); SELECT LAST_INSERT_ID();", connection, transaction))
        {
            command.Parameters.AddWithValue("@dev", devPlayerId);
            accountId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        await using (var command = new MySqlCommand(
            "INSERT IGNORE INTO player_profile (account_id, display_name, updated_at) VALUES (@account, @name, UTC_TIMESTAMP(3))",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@name", devPlayerId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return accountId;
    }
}
```

`Server/src/ProjectH.Server/Persistence/MatchHistoryWriter.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectH.Server.Persistence;

// Phase 9 D5-D8: the only code that talks to MySQL, on its own async task (never the game loop thread). At start it
// creates the schema; if the database cannot be reached the server keeps running and the records are discarded and
// counted (the game must not depend on the database, §36). Each record is saved with up to MaxAttempts tries. On
// shutdown the queue is completed and whatever is left is saved for at most ShutdownDrainSeconds.
public sealed class MatchHistoryWriter : BackgroundService
{
    private readonly MatchHistoryQueue _queue;
    private readonly PersistenceOptions _options;
    private readonly ILogger<MatchHistoryWriter> _logger;
    private readonly CancellationTokenSource _abort = new();
    private MatchStore? _store;
    private long _saved;
    private long _failed;
    private long _discarded;

    public MatchHistoryWriter(MatchHistoryQueue queue, IOptions<PersistenceOptions> options, ILogger<MatchHistoryWriter> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    public long Saved => Interlocked.Read(ref _saved);
    public long Failed => Interlocked.Read(ref _failed);
    public long Discarded => Interlocked.Read(ref _discarded);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The read loop follows _abort, not stoppingToken: StopAsync completes the queue first and lets it drain.
        CancellationToken token = _abort.Token;
        _store = await OpenStoreAsync(token);
        try
        {
            await foreach (MatchRecord record in _queue.Reader.ReadAllAsync(token))
            {
                if (_store == null)
                {
                    Interlocked.Increment(ref _discarded);
                    continue;
                }
                await SaveWithRetryAsync(_store, record, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Drain timeout: what is still queued is lost, and logged in StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        Task? running = ExecuteTask;
        if (running != null)
        {
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drain.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownDrainSeconds));
            try
            {
                await running.WaitAsync(drain.Token);
            }
            catch (OperationCanceledException)
            {
                _abort.Cancel();
                _logger.LogWarning("Match history: shutdown drain timed out after {Seconds} s; unsaved records are lost.", _options.ShutdownDrainSeconds);
            }
        }
        _logger.LogInformation("Match history: saved={Saved} failed={Failed} discarded={Discarded} droppedQueueFull={Dropped}",
            Saved, Failed, Discarded, _queue.Dropped);
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }

    private async Task<MatchStore?> OpenStoreAsync(CancellationToken token)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Match history: persistence disabled (Persistence:Enabled = false).");
            return null;
        }
        try
        {
            var store = new MatchStore(_options.ConnectionString);
            await store.EnsureSchemaAsync(token);
            _logger.LogInformation("Match history: connected, schema ready.");
            return store;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Match history: database unavailable; this run will not save matches.");
            return null;
        }
    }

    private async Task SaveWithRetryAsync(MatchStore store, MatchRecord record, CancellationToken token)
    {
        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                long matchId = await store.SaveAsync(record, token);
                Interlocked.Increment(ref _saved);
                _logger.LogInformation("Match history: saved match {MatchId} (round {Round}, {Players} players).", matchId, record.Round, record.Players.Count);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (attempt == _options.MaxAttempts)
                {
                    Interlocked.Increment(ref _failed);
                    _logger.LogError(e, "Match history: giving up on round {Round} ({Players} players) after {Attempts} attempts.",
                        record.Round, record.Players.Count, attempt);
                    return;
                }
                _logger.LogWarning("Match history: save of round {Round} failed (attempt {Attempt}): {Message}", record.Round, attempt, e.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt), token);
            }
        }
    }
}
```

- [ ] **Step 4: Host에 연결한다**

아래 스크립트를 `plan9_host.py`로 저장하고 `python <경로>/plan9_host.py E:/popol/ProjectH`로 실행한다. 이 스크립트는 `GameServerService`, `Program`, `appsettings.json`을 고친다.

```python
import os, sys
os.chdir(sys.argv[1])

def edit(path, pairs):
    s = open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in s else '\n'
    for a, b in pairs:
        a2 = a.replace('\n', nl); b2 = b.replace('\n', nl)
        assert s.count(a2) == 1, (path, a[:80], s.count(a2))
        s = s.replace(a2, b2)
    open(path, 'w', encoding='utf-8', newline='').write(s)

G = 'Server/src/ProjectH.Server/'
edit(G + 'GameServerService.cs', [
("""using ProjectH.Server.Game;
""",
"""using ProjectH.Server.Game;
using ProjectH.Server.Persistence;
"""),
("""    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger)
    {
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        _loop = new GameLoop(options.Value, data, logger);
    }""",
"""    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory)
    {
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        // Phase 9: finished matches go to the bounded queue; MatchHistoryWriter saves them off the game loop.
        _loop = new GameLoop(options.Value, data, logger, matchSink: record => matchHistory.TryEnqueue(record));
    }"""),
])

edit(G + 'Program.cs', [
("""using ProjectH.Server;
""",
"""using Microsoft.Extensions.Options;
using ProjectH.Server;
using ProjectH.Server.Persistence;
"""),
("""builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
builder.Services.AddHostedService<GameServerService>();""",
"""builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
// Phase 9: match history. The writer is registered before the game server, so on shutdown the host stops the game
// loop first and the writer drains the queue after it.
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("Persistence"));
builder.Services.AddSingleton(services =>
{
    PersistenceOptions persistence = services.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    string? error = persistence.Validate();
    if (error != null) throw new InvalidOperationException(error);
    return new MatchHistoryQueue(persistence.QueueCapacity);
});
builder.Services.AddHostedService<MatchHistoryWriter>();
builder.Services.AddHostedService<GameServerService>();"""),
])

edit(G + 'appsettings.json', [
("""    "SpawnSeed": 1
  }
}""",
"""    "SpawnSeed": 1
  },
  "Persistence": {
    "Enabled": true,
    "ConnectionString": "Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev;Connection Timeout=5",
    "QueueCapacity": 16,
    "MaxAttempts": 3,
    "ShutdownDrainSeconds": 5
  }
}"""),
])
print('host ok')
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

Run: `dotnet build Server/ProjectH.Server.slnx --no-incremental` → 경고 0

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 통과. MySQL 테스트 3개는 건너뛴다.

Run (bash): `PROJECTH_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=projecth;User ID=projecth;Password=projecth_dev" dotnet test Server/ProjectH.Server.slnx --filter "FullyQualifiedName~Persistence"`
Expected: 모두 통과(12개, 건너뜀 0). 계획 단계에서 같은 결과였다.

- [ ] **Step 6: 서버 Host 확인**

Release로 빌드한 서버를 포트 7792로 6초 띄운다. 로그에 `Match history: connected, schema ready.`가 있는지 확인한 뒤, 그 프로세스를 pid로 끈다. 프로세스를 이름으로 끄지 않는다.

`docker compose down`으로 컨테이너를 내린다(볼륨은 남긴다).

- [ ] **Step 7: Commit** — `feat(server): save match history and stats to MySQL off the game loop`

---

### Task 3: 문서

**Files:**
- Modify: `Docs/Database.md`(전체 다시 쓰기), `Docs/Server.md`, `Docs/Architecture.md`

- [ ] **Step 1: `Docs/Database.md`**

- 실행: `docker compose up -d` / `down`, 개발용 계정, 환경 변수 덮어쓰기(`PROJECTH_DB_PASSWORD`, `Persistence__ConnectionString`)
- 스키마: 표마다 열과 키(spec D3, `MatchStore.Schema`와 같게)
- 저장 흐름(spec D4–D6)
  - Game Loop → 큐(최대 16, Reject) → Writer → 한 Transaction
  - 저장 항목: 피해·생존 시간의 정의, 이탈자 포함, 관전 합류자 제외
- 실패 처리(spec D7)와 종료(D8)
- 카운터와 로그(`Saved`, `Failed`, `Discarded`, `Dropped`)
- 조회 예시 SQL: 내 통계, 최근 경기 10개
- 테스트: `PROJECTH_TEST_MYSQL` 환경 변수로 DB 테스트를 켜는 방법
- 범위 밖(spec D10)

- [ ] **Step 2: `Docs/Server.md`, `Docs/Architecture.md`**

- **`Docs/Server.md`**
  - 실행 절에 "MySQL을 쓰려면 `docker compose up -d`"를 더한다. DB가 없어도 서버는 돈다고 적는다.
  - 스레드 절에 `MatchHistoryWriter`(async, Game Loop 밖)를 더한다.
  - Queue 절에 `MatchHistoryQueue`(16, Reject)를 더한다.
- **`Docs/Architecture.md`**
  - Phase 9 spec을 설계 근거에 더한다.
  - Mermaid에 `Match -->|MatchRecord, TryEnqueue| History[MatchHistoryQueue] --> Writer[MatchHistoryWriter] --> MySQL[(MySQL)]`을 더한다.
  - "DB는 아직 사용하지 않는다" 문장을 지운다.

- [ ] **Step 3: Commit** — `docs: Phase 9 database`

---

## Phase 완료 확인

1. `dotnet build Server/ProjectH.Server.slnx --no-incremental`(경고 0)와 `dotnet test Server/ProjectH.Server.slnx`(모두 통과, MySQL 3개 건너뜀)를 실행한다. 그리고 `PROJECTH_TEST_MYSQL`을 켜고 `Persistence` 테스트를 돌린다(모두 통과, 컨테이너 필요).
2. Unity Client와 Shared가 바뀌지 않았는지 확인한다(`git diff --stat main -- Client Shared`).
3. `github-push` 스킬로 `main`에 Squash Commit·Push한다. 민감정보 검사가 개발용 더미 비밀번호(`projecth_dev`)를 잡으면, 127.0.0.1 전용 개발 컨테이너의 값이라는 근거를 보고에 적는다(spec D9).
