using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using LiteNetLib;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Flow;
using ProjectH.Server.Game.Harvest;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Zone;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

public delegate void SendPacket(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method);

// All player state of one match. Game loop thread only: the network thread never touches it,
// so nothing here takes a lock.
public sealed partial class Match
{
    private const float SpawnRadius = 5f;
    // Phase 6 D9: when there are more participants than drop points, each further lap stands this far east, then
    // west, of the point (inside DropPoints.ClearRadius, so still clear of every box).
    private const float DropLapOffset = 3f;

    // Connected players only. A graced player (Phase 10 D2) is in _players and _graced but not here.
    private readonly Dictionary<int, PlayerEntity> _playersByPeer = new();
    // Players are removed only in RemovePlayer(); both collections are updated together.
    private readonly List<PlayerEntity> _players = new();
    // Phase 10 D2: participants whose connection dropped during the match, oldest first. A subset of _players, so at
    // most MaxPlayers. An entry leaves on a resume, when its grace ends, when it dies, and at the round reset.
    private readonly List<PlayerEntity> _graced = new();
    private readonly uint _graceTicks;
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly SendPacket _send;
    // Phase 13 D13: the building stream's own channel.
    private readonly SendPacket _sendBuild;
    private readonly WeaponCatalog _weapons;
    private readonly ItemCatalog _items;
    private readonly WorldItems _worldItems = new();
    private readonly LootSpawner _loot;
    private readonly MatchFlow _flow;
    private readonly SafeZone _zone;
    private readonly int _lootSeed;
    private readonly int _zoneSeed;
    private readonly int _spawnSeed;
    // Phase 6 D9: the drop points and this match's shuffled order of them. Fixed arrays, filled at the match start.
    private readonly Vector3[] _dropPoints;
    private readonly int[] _dropOrder;
    // Phase 9 D4: where a finished match's record goes (null = nothing is recorded, e.g. most tests). Called on the game
    // loop thread and must not block: production passes MatchHistoryQueue.TryEnqueue.
    private readonly Action<MatchRecord>? _matchSink;
    // Phase 10 D2, D9: told the DevPlayerId of every graced player that leaves without resuming (grace over, died while
    // away, or the round reset). Called on the game loop thread and must not block (GameLoop counts and logs it).
    private readonly Action<string>? _graceExpired;
    // Phase 12 D12: told of every move faster than its mode allows (a simulation bug; GameLoop counts it). Must not block.
    private readonly Action? _movementAnomaly;
    // Server review M7: told of every player whose own part of the tick threw, after that player left the match (the peer
    // id, NoPeer for a graced player, and the exception). Called on the game loop thread; GameLoop counts, logs and closes
    // the connection. Must not call back into this match.
    private readonly Action<int, Exception>? _playerFailed;
    // Server review M7 (game loop thread only): the players whose tick threw in this tick's player loop. Made once with
    // MaxPlayers room (one entry per player at most, so Add never grows it); cleared at the start of every player loop
    // and after the failed players left.
    private readonly List<(PlayerEntity Player, Exception Error)> _failedPlayers;
    // Phase 12 D4, D5: matches start aboard the drop transport. The route is valid while _hasRoute: from such a match's
    // start to the round reset.
    private readonly bool _airDrop;
    private bool _hasRoute;
    private DropRoute _route;
    // Phase 12 D9: the doors and the collision world they make (map boxes + closed doors); every move, shot and drop
    // uses _doors.World. _sentDoors is what every client was last told (DoorStates when it changes).
    private readonly DoorSet _doors = new();
    private byte _sentDoors;
    // Phase 13 D3: the colliders around the player being moved, gathered before each Step (one buffer for everyone).
    private readonly CollisionWorld _collision = new();
    // Phase 13 D4, D6, D7: the building numbers, the harvestables' state and what every client was last told of it.
    private readonly BuildingCatalog _building;
    private readonly HarvestWorld _harvest;
    private ulong _sentHarvest;
    // What shots, swings and item drops stop at: the map boxes, the closed doors and the standing harvestables. Rebuilt
    // only when a door or a harvestable changed since the last use (Blockers).
    private readonly Box[] _blockers = new Box[GameMap.Boxes.Length + GameMap.DoorCount + GameMap.MaxHarvestables];
    private int _blockerCount = -1;
    private byte _blockerDoors;
    private ulong _blockerHarvest;
    // Phase 13 D10, D13: the pieces of this match and this tick's building events.
    private readonly BuildWorld _build;
    private readonly BuildReplication _replication;
    // Phase 13 D12: which pieces hold each other up (lattice edges), and the former neighbours of a destroyed piece.
    private readonly BuildSupport _support;
    // Final review A4: how many reliable packets wait in a peer's building-channel queue (GameLoop asks LiteNetLib; null =
    // never backed up, tests). Above MaxBuildBacklog its sync waits; events, interest and results still go.
    private readonly Func<int, int>? _buildBacklog;
    public const int MaxBuildBacklog = 32;
    // Every result code (Phase 13.5 D9: up to NotFound), placements and edits together (one queue, one sequence).
    private readonly long[] _buildResults = new long[(int)BuildResultCode.NotFound + 1];
    private readonly bool _infiniteResources;
    private readonly BuildCatalogData _buildCatalogWire;
    // Participants who left during the current match, recorded when they left (they are no longer in _players).
    // At most MaxPlayers entries; cleared when a match starts. Phase 14 D6: with its team, so the team's placement (decided
    // later, at the wipe or the finish) is written into the record.
    private readonly List<LeftParticipant> _leftParticipants = new();
    private readonly record struct LeftParticipant(PlayerRecord Record, byte TeamId);
    private DateTime _matchStartedUtc;
    private uint _matchStartTick;
    // What every client was last told (D11): a new MatchState or ZoneState is broadcast at the end of a tick only
    // when it differs. Never sent in the dev sandbox (no match flow there).
    private MatchState _sentMatchState;
    private ZoneState _sentZoneState;
    private readonly StartingLoadout _loadout;
    private readonly int _maxPlayers;
    private readonly int _snapshotEveryTicks;
    private readonly int _inputCapacity;
    private readonly byte _simHz;
    private readonly byte _snapshotHz;
    private readonly float _tickSeconds;
    private readonly uint _respawnTicks;
    private readonly int _maxRewindTicks;
    private ushort _nextEntityId = 1;

    // Test seams: loadout null = StartingLoadout.Empty (the production start, D1); lootPoints null = the
    // map's LootPoints.All; dropPoints null = the map's DropPoints.All (Phase 6 D9).
    // Phase 13 D13: sendBuild sends on the building channel (LiteNetLib channel 1); null = everything through send (tests).
    // buildBacklog: final review A4, a peer's queued reliable packets on the building channel (null = none).
    // 기능: 경기 객체를 만든다(데이터 카탈로그, Phase 15 지도 수치 포함, 개발 모드면 Loot와 Phase 16 Container도 채운다).
    // 입력: options - 서버 설정, data - 게임 데이터, send - 패킷 전송, 나머지 - 위 설명의 테스트용·선택 인자.
    // 출력: 대기 상태의 Match.
    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null,
        Vector3[]? dropPoints = null, Action<MatchRecord>? matchSink = null, Action<string>? graceExpired = null,
        Action? movementAnomaly = null, SendPacket? sendBuild = null, Func<int, int>? buildBacklog = null,
        Action<int, Exception>? playerFailed = null)
    {
        _playerFailed = playerFailed;
        _failedPlayers = new List<(PlayerEntity, Exception)>(options.MaxPlayers);
        _buildBacklog = buildBacklog;
        _matchSink = matchSink;
        _graceExpired = graceExpired;
        _movementAnomaly = movementAnomaly;
        _airDrop = options.AirDrop && !options.DevRespawn;
        SendPacket raw = send ?? throw new ArgumentNullException(nameof(send));
        // Phase 10 D2: the one place packets to a graced player (NoPeer) are dropped. Every send goes through _send.
        _send = (peerId, data, method) =>
        {
            if (peerId != PlayerEntity.NoPeer) raw(peerId, data, method);
        };
        SendPacket rawBuild = sendBuild ?? raw;
        _sendBuild = (peerId, data, method) =>
        {
            if (peerId != PlayerEntity.NoPeer) rawBuild(peerId, data, method);
        };
        _graceTicks = (uint)options.ReconnectGraceSeconds * (uint)options.SimHz;
        ArgumentNullException.ThrowIfNull(data);
        if (data.SimHz != options.SimHz)
            throw new ArgumentException($"Game data was built for SimHz {data.SimHz}, the match runs at {options.SimHz}.", nameof(data));
        _weapons = data.Weapons;
        _items = data.Items;
        _squad = data.Squad;          // Phase 14 D11
        _map = data.Map;              // Phase 15 D9
        _teamSize = options.TeamSize; // Phase 14 D1
        _building = data.Building;
        _infiniteResources = options.BuildInfiniteResources;
        _harvest = new HarvestWorld(_building);
        _build = new BuildWorld(_building);
        _replication = new BuildReplication(_build, _building, options.MaxPlayers);
        _support = new BuildSupport(_build.Capacity);
        _buildCatalogWire = BuildCatalogWire(_building, _infiniteResources);
        _loadout = loadout ?? StartingLoadout.Empty;
        string? loadoutError = _loadout.Validate(data);
        if (loadoutError != null) throw new ArgumentException("Invalid starting loadout: " + loadoutError, nameof(loadout));
        _maxPlayers = options.MaxPlayers;
        _snapshotEveryTicks = options.SnapshotEveryTicks;
        _inputCapacity = options.InputBufferPerPlayer;
        _simHz = (byte)options.SimHz;
        _snapshotHz = options.SnapshotHz;
        _tickSeconds = 1f / options.SimHz;
        _respawnTicks = CombatRules.TicksFromSeconds(CombatRules.RespawnSeconds, options.SimHz);
        _maxRewindTicks = CombatRules.MaxRewindTicks(options.SimHz);
        _flow = new MatchFlow(options.MinPlayers, (uint)options.StartCountdownSeconds * (uint)options.SimHz,
            (uint)options.ResultSeconds * (uint)options.SimHz, options.DevRespawn);
        _zone = new SafeZone(data.Zones);
        _lootSeed = options.LootSeed;
        _zoneSeed = options.ZoneSeed;
        _spawnSeed = options.SpawnSeed;
        _dropPoints = dropPoints is null ? DropPoints.All.ToArray() : (Vector3[])dropPoints.Clone();
        if (_dropPoints.Length == 0) throw new ArgumentException("A match needs at least one drop point.", nameof(dropPoints));
        _dropOrder = new int[_dropPoints.Length];
        // Phase 16 D2, D6: the container tables (-1 = none in this loot data: those containers never spawn), the fall time and the schedule.
        _lootTable = data.Loot;
        _chestTable = data.Loot.TableIndex(LootTable.ChestTable);
        _ammoBoxTable = data.Loot.TableIndex(LootTable.AmmoBoxTable);
        _supplyDropTable = data.Loot.TableIndex(LootTable.SupplyDropTable);
        _supplyDropFallTicks = SupplyDropFall.FallTicks(data.Loot.SupplyDropFallSpeed, options.SimHz);
        _dropScheduleTicks = ScheduleTicks(data.Loot, options.SimHz);
        _sentMatchState = _flow.ToWire(0);
        _sentZoneState = _zone.ToWire();

        // Phase 5 D4: loot refills only in the dev sandbox; a match never refills a looted point.
        uint lootRespawnTicks = options.DevRespawn ? (uint)options.LootRespawnSeconds * (uint)options.SimHz : 0u;
        _loot = new LootSpawner(lootPoints is null ? LootPoints.All : new ReadOnlySpan<LootPoint>(lootPoints), data, options.LootSeed,
            lootRespawnTicks);
        // The dev sandbox fills every spawn point now (Phase 4 D6); clients get the list at join. A battle royale
        // server has no loot before the match (Phase 5 D2): the match start rolls it (D3).
        if (options.DevRespawn)
        {
            for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
            // Phase 16 D2: the sandbox's containers are rolled once now, the same way as a match start (round 1), and never refilled.
            RollContainers(unchecked((_lootSeed + _flow.Round) * -1640531535 + ContainerSeedSalt));
        }
    }

    public uint ServerTick { get; private set; }
    public int PlayerCount => _players.Count;
    // Phase 10 D2: players kept for a reconnect (included in PlayerCount).
    public int GracedCount => _graced.Count;

    public long TotalBufferDrops
    {
        get
        {
            long total = 0;
            foreach (var player in _players) total += player.Inputs.DroppedCount;
            return total;
        }
    }

    public bool TryGetPlayer(int peerId, out PlayerEntity player) => _playersByPeer.TryGetValue(peerId, out player!);

    // QA-1: every player (connected and graced), by index 0..PlayerCount-1, without exposing the list. Read-only use.
    internal PlayerEntity PlayerAt(int index) => _players[index];

    // Test seam (InternalsVisibleTo): the store itself. Match is the only writer.
    internal WorldItems WorldItems => _worldItems;
    // Test seam: the match state machine. Match is the only writer.
    internal MatchFlow Flow => _flow;
    // Test seam: the safe zone of the current match. Match is the only writer.
    internal SafeZone Zone => _zone;
    // The tick the current match started at (zone damage counts whole seconds from it).
    internal uint MatchStartTick => _matchStartTick;
    // The last match's winner (D9), 0 = none among the connected players. Set when the match finishes.
    internal ushort WinnerId { get; private set; }
    // Test seams (Phase 12): the current drop transport route, valid while HasRoute.
    internal bool HasRoute => _hasRoute;
    internal DropRoute Route => _route;
    internal DoorSet Doors => _doors;
    // Review round 2: whether every player the last tick's player loop ran failed (false with no player). Game loop only.
    internal bool EveryPlayerFailed { get; private set; }

    // Test seam (server review M7): the player loop throws for the player with this entity id (0 = none; ids start at 1;
    // -1 = every player, review round 1).
    // Volatile: a test may set it while the loop thread ticks.
    internal int FaultEntityId { get => Volatile.Read(ref _faultEntityId); set => Volatile.Write(ref _faultEntityId, value); }
    private int _faultEntityId;
    // Phase 13 test seams: the harvestables' state and the building numbers.
    internal HarvestWorld Harvest => _harvest;
    internal BuildingCatalog Building => _building;
    // Phase 13 D18: harvest swings that hit a harvestable since this match object was made.
    public long HarvestHits { get; private set; }
    // Phase 13 test seams: the pieces and this tick's events.
    internal BuildWorld Build => _build;
    internal BuildReplication Replication => _replication;
    // Phase 13 D18: build requests by result since this match object was made (Ok = accepted), and the ones dropped
    // without a result (a sequence already processed).
    public long BuildResults(BuildResultCode code) => _buildResults[(int)code];
    public long BuildDuplicates { get; private set; }
    // Phase 13.5: edits that changed a piece (an Ok whose state was already the piece's is not counted here).
    public long BuildEdits { get; private set; }
    // Phase 13 D18: pieces destroyed (by damage or, Phase 13 D12, collapse) since this match object was made, and of
    // those the ones that collapsed.
    public long PiecesDestroyed { get; private set; }
    public long PiecesCollapsed { get; private set; }
    // D13, D14: BuildEvents and BuildSync packets sent (all recipients) since this match object was made.
    public long BuildEventPackets { get; private set; }
    public long BuildSyncPackets { get; private set; }
    // Final review A4: ticks a client's sync waited because its building channel was backed up (all recipients).
    public long SyncsDeferred { get; private set; }

    // D18: the building and harvesting numbers for the Health line and the Meter (GameLoop copies them every tick).
    public BuildCounts BuildCounts()
    {
        long rejected = 0;
        for (int i = 1; i < _buildResults.Length; i++) rejected += _buildResults[i];
        return new BuildCounts(_build.Count, _build.Grid.OccupiedColumns, rejected + _buildResults[0] + BuildDuplicates, _buildResults[0],
            rejected, PiecesDestroyed, PiecesCollapsed, BuildDuplicates, HarvestHits, EnvironmentDestroyed, BuildEventPackets, BuildSyncPackets,
            PiecesDestroyed - PiecesCollapsed, SyncsDeferred, BuildEdits);
    }
    internal BuildSupport Support => _support;
    public int BuildPieces => _build.Count;
    public long EnvironmentDestroyed => _harvest.DestroyedTotal;
    // Phase 12 D12: moves faster than their mode allows since this match object was made (should stay 0).
    public long MovementAnomalies { get; private set; }
    // Phase 9: finished matches whose record could not be built or handed to the sink (it threw). Shown in the stats line.
    public long MatchSinkFailures { get; private set; }
    // Phase 11: PlayerSpawned packets that could not be encoded (a name over the limit) and so were not sent.
    public long SpawnEncodeFailures { get; private set; }
    // Phase 10 D6: the first sink exception since the game loop last took it (logged once per stats interval).
    private Exception? _sinkError;

    internal Exception? TakeSinkError()
    {
        Exception? error = _sinkError;
        _sinkError = null;
        return error;
    }

    // 기능: 연결을 경기에 넣는다(유예 중인 같은 DevPlayerId면 Resume). 새 플레이어는 JoinOrder를 받고(Phase 14 D1, 개발 모드면 팀도), 입장 패킷 묶음
    //   (Phase 16: 채집 상태 뒤에 ContainerStates·SupplyDrops, Phase 17: 살아 있는 투사체)과 끝에 분대 상태, (Phase 15, 팀이 있으면) 팀 지도 표시를 받는다.
    // 입력: peerId - 연결 id, devPlayerId - 검증된 플레이어 이름.
    // 출력: Ok, Resumed, AlreadyJoined 또는 MatchFull.
    public JoinResult TryJoin(int peerId, string devPlayerId)
    {
        if (_playersByPeer.ContainsKey(peerId)) return JoinResult.AlreadyJoined;
        // Phase 10 D2: before the full check, because a graced player's slot is its own.
        PlayerEntity? graced = FindGraced(devPlayerId);
        if (graced != null)
        {
            Resume(peerId, graced);
            return JoinResult.Resumed;
        }
        if (_players.Count >= _maxPlayers)
        {
            SendJoinResponse(peerId, JoinResult.MatchFull, 0);
            return JoinResult.MatchFull;
        }

        var player = new PlayerEntity(AllocateEntityId(), peerId, devPlayerId, _inputCapacity);
        player.JoinOrder = ++_joinCounter;   // Phase 14 D1
        if (_flow.DevRespawn) AssignDevTeam(player);
        player.State.Position = SpawnPosition(player.EntityId);
        ResetCombat(player);
        // D10: a newcomer during a match spectates until the next round (dead, not a participant). It never
        // counts as alive, so it cannot keep the match from ending.
        bool spectator = _flow.InMatch;
        if (spectator) player.Alive = false;
        player.History.Reset(ServerTick, player.State.Position);
        _playersByPeer.Add(peerId, player);
        _players.Add(player);

        SendJoinResponse(peerId, JoinResult.Ok, player.EntityId);
        // Before any spawn: the client needs the weapon and item data before it can show them (D4, D2).
        SendCatalogs(peerId);
        SendWorldItems(player);
        SendInventory(player);
        // Everyone (including itself) to the newcomer, then the newcomer to everyone else.
        foreach (var other in _players) SendSpawned(peerId, other);
        foreach (var other in _players)
        {
            if (other != player) SendSpawned(other.PeerId, player);
        }
        // Phase 5 (D11): where the match and the zone stand, after the spawns so the join order of Phase 4 holds.
        if (!_flow.DevRespawn)
        {
            SendMatchState(peerId, _flow.ToWire(_players.Count));
            SendZoneState(peerId, _zone.ToWire());
        }
        SendDoors(peerId);   // Phase 12 D9
        SendHarvestStates(peerId);   // Phase 13 D6
        SendContainerStates(peerId); // Phase 16 D3
        SendSupplyDrops(peerId);     // Phase 16 D7
        SendProjectilesTo(peerId);   // Phase 17 D7: what is still flying
        SendResources(player);       // Phase 13 D15
        SendBuildCatalog(peerId);    // Phase 13 D4, final review A3: first on the building channel
        StartBuildSync(player);      // Phase 13 D14
        // Phase 12 D16: a newcomer during an air-drop match sees the transport too.
        if (_hasRoute) SendRoute(peerId);
        // Reliable, after its own spawn: the newcomer's client knows it is dead (spectating) before any input.
        // Only to the newcomer; the others see it dead from the snapshot flag.
        if (spectator) SendDied(peerId, new PlayerDied { VictimId = player.EntityId });
        SendSquadStateTo(player);   // Phase 14 D2, D10: the stations (and a dev-mode team) last, after everything above
        SendMarkersTo(player);      // Phase 15 D10: a dev-mode team's pings and waypoints
        return JoinResult.Ok;
    }

    // Phase 10 D2: the connection of peerId is gone. During the match a living participant whose connection was not
    // closed by the server (allowGrace) stays in the world for ReconnectGraceSeconds: no input, it can be shot and the
    // zone still hurts it. Everyone else leaves at once. Returns true when the player was kept.
    public bool Disconnect(int peerId, bool allowGrace)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return false;
        if (!allowGrace || _graceTicks == 0 || !_flow.InMatch || !player.Participant || !player.Alive)
        {
            Leave(peerId);
            return false;
        }
        _playersByPeer.Remove(peerId);
        player.PeerId = PlayerEntity.NoPeer;
        player.GraceEndTick = ServerTick + _graceTicks;
        player.Inputs.Reset();
        player.BuildQueue.Clear();   // final review B8: a graced player places nothing (Resume starts the sequence over)
        _graced.Add(player);
        return true;
    }

    public void Leave(int peerId)
    {
        if (_playersByPeer.Remove(peerId, out var player)) RemovePlayer(player);
    }

    // 기능: 연결된 또는 유예 중인 플레이어를 경기에서 뺀다. _players를 도는 반복 안에서는 부르지 않는다.
    //   D10: 경기 중 이탈은 탈락이다(소지품은 남은 사람들에게 드롭, 결과 없음). Phase 14: 진행 중인 소생·재투입을 끝내고, 팀이 살아
    //   있으면 들고 있던 카드를 떨어뜨리며, 이 사람의 카드는 사라진다(D9). 그 뒤 분대 전멸을 검사한다(D6: 마지막 Up 구성원이 나가면
    //   기절한 팀원이 탈락하고, 팀 배치가 이 사람의 기록에도 들어간다). Phase 15: 이 사람의 Ping과 Waypoint를 지운다(팀은 Tick 끝에 새 목록을 받는다).
    // 입력: player - 떠나는 플레이어.
    // 출력: 반환값 없음.
    private void RemovePlayer(PlayerEntity player)
    {
        _players.Remove(player);
        _graced.Remove(player);

        var writer = new PacketWriter(_sendBuffer);
        PlayerDespawned.Write(ref writer, new PlayerDespawned { EntityId = player.EntityId });
        foreach (var other in _players) _send(other.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        EndChannelsOf(player);
        RemoveMarkersOf(player);   // Phase 15
        bool squad = _flow.InMatch && player.Participant && player.TeamId != 0;
        // D10: leaving a match is an elimination. What it carried goes to the ground for the others (the death
        // drop, sent to the remaining players only), and it no longer counts as alive. It gets no result.
        if (_flow.InMatch && player.Participant && player.Alive)
        {
            player.Alive = false;
            _flow.EliminatePlayer();
            // Phase 14: a provisional placement (the teams left now); the team's own one replaces it at the wipe or finish.
            player.Placement = (byte)_flow.TeamsAlive;
            player.EliminatedTick = ServerTick;
            DropEverything(player);
            if (squad && !_teamOut[player.TeamId] && HasUpMember(player.TeamId, null)) DropHeldCards(player);
        }
        ClearHeldCards(player);
        RemoveCardsOf(player);
        // Phase 9: a participant who leaves still belongs to the match record.
        if (_flow.InMatch && player.Participant && _matchSink != null) _leftParticipants.Add(new LeftParticipant(RecordOf(player, ServerTick), player.TeamId));
        if (squad) CheckTeam(player.TeamId);
    }

    public void EnqueueInput(int peerId, in PlayerInputPacket packet)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        for (int i = 0; i < packet.Count; i++) player.Inputs.Add(packet.Get(i));
    }

    // 기능: 네트워크에서 온 건설 요청(배치 또는 편집, GameLoop.DrainBuild)을 플레이어 큐에 넣는다. 다음 Tick에 처리된다.
    //   큐가 가득 차면 바로 RateLimited로 답한다(홍수는 작은 답 하나씩만 들고 메모리는 늘지 않는다).
    // 입력: peerId - 연결 id, request - 요청.
    // 출력: 반환값 없음. 큐에 들어가거나 RateLimited 결과가 전송된다.
    public void EnqueueBuild(int peerId, in BuildQueueItem request)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        if (!player.BuildQueue.TryAdd(request))
        {
            _buildResults[(int)BuildResultCode.RateLimited]++;   // a dropped request still counts (requests= and rateLimited=)
            SendBuildResult(player, request.Sequence, BuildResultCode.RateLimited, 0);
        }
    }

    // 기능: 배치 요청을 큐에 넣는다(테스트·호환용, EnqueueBuild(BuildQueueItem)과 같다).
    // 입력: peerId - 연결 id, request - 배치 요청.
    // 출력: 반환값 없음.
    public void EnqueueBuild(int peerId, in BuildRequest request) => EnqueueBuild(peerId, new BuildQueueItem(request));

    // 기능: 편집 요청을 큐에 넣는다(Phase 13.5 D4, 배치와 같은 큐).
    // 입력: peerId - 연결 id, request - 편집 요청.
    // 출력: 반환값 없음.
    public void EnqueueEdit(int peerId, in BuildEditRequest request) => EnqueueBuild(peerId, new BuildQueueItem(request));

    // 기능: 경기 한 Tick: 유예 만료, 경기 흐름, 자기장, (Phase 16) Supply Drop 일정·착지, 건설 요청, (Phase 17) 투사체 이동·폭발, 플레이어 Tick,
    //   붕괴, 경기 끝 판정, 그리고 Tick 끝 전송
    //   (인벤토리·경기·문·채집·Container·Supply Drop·자원·팀·스테이션,
    //   Phase 15 지도 표시 만료와 TeamMarkers, 건설 사건, Snapshot).
    // 입력: 없음.
    // 출력: 반환값 없음. Server Authoritative 경기 상태가 한 Tick 진행되고 바뀐 내용이 전송된다.
    public void Tick()
    {
        // now = the last completed tick; this call simulates tick now + 1. Weapon timers
        // (NextFireTick, ReloadEndTick, RespawnAtTick) are compared against it.
        uint now = ServerTick;

        // Phase 10 D2: first, outside every loop over _players, the graced players whose grace is over.
        ExpireGrace(now);

        // Phase 5 step 1: state transitions (D1). The start and the round reset each happen within this one
        // tick, so no client ever sees a half-reset match (D3, D13).
        switch (_flow.Update(now, _players.Count))
        {
            case FlowEvent.MatchStarted:
                StartMatch(now);
                break;
            case FlowEvent.RoundClosed:
                CloseRound(now);
                break;
        }

        // Step 2: the zone's phase and its damage (D8).
        if (_flow.InMatch) UpdateZone(now);
        // Phase 16 D6: supply drops spawn and land only during the match (not on the result screen).
        if (_flow.InMatch) UpdateSupplyDrops(now);

        // D4: death is permanent in a match; the dev sandbox respawns (Phase 3 D9). Refills are off in a match
        // (the spawner was built with 0 respawn ticks).
        if (_flow.RespawnAllowed)
        {
            foreach (var player in _players)
            {
                if (!player.Alive && now >= player.RespawnAtTick) Respawn(player);
            }
        }
        RefillLootPoints(now);
        if (_flow.InMatch) ExpireCards(now);   // Phase 14 D9
        // Phase 13 D15: resources lying in reach are picked up on touch.
        if (now % ItemRules.MaterialPickupEveryTicks == 0) PickUpMaterials();

        // Phase 13 D8: build requests before the moves and shots, so a wall placed this tick already blocks them (the
        // fastest defence, request §117). Placement uses each player's last input (its aim) and position.
        ProcessBuildRequests(now);
        // Phase 17 D6: projectiles move and explode before the players act (a wall placed this tick already stops them; one
        // launched in this tick's actions first moves next tick, matching its StartTick). Explosions' destroys collapse with
        // the rest of the tick's in CollapseUnsupported.
        UpdateProjectiles(now);

        // Server review M7: each player's part is isolated. A player whose part throws (a bad state that would throw every
        // tick) is taken out after the loop, alone; the others move and the tick goes on. A failure outside this loop
        // still fails the whole tick (GameLoop's reset path).
        _failedPlayers.Clear();
        int ticked = _players.Count;
        foreach (var player in _players)
        {
            try
            {
                TickPlayer(player, now);
            }
            catch (Exception ex)
            {
                _failedPlayers.Add((player, ex));
            }
        }
        // Before the collapse and the finish check: the failed player's leave is an elimination of this tick.
        // Review round 2: every player of this tick failed (GameLoop counts such ticks; read after Tick returns).
        EveryPlayerFailed = ticked > 0 && _failedPlayers.Count == ticked;
        if (_failedPlayers.Count > 0) RemoveFailedPlayers();
        // Final review A2: one support search for every piece destroyed this tick.
        CollapseUnsupported();

        // Phase 5 step 5: one participant (or none) left ends the match (D9). Deaths of this tick, from the zone
        // and from shots, already have their placements.
        if (_flow.ShouldFinish) FinishMatch(now);

        ServerTick++;
        SendInventoryChanges();
        SendMatchChanges();
        SendDoorChanges();
        SendHarvestChanges();
        SendLootChanges();       // Phase 16 D3, D7
        SendResourceChanges();
        SendTeamChanges();       // Phase 14 D2
        SendStationChanges();    // Phase 14 D10
        SendMarkerChanges();     // Phase 15 D9, D10: expired pings, then each changed team's TeamMarkers
        SendBuildEvents();
        // After every move of this tick, so all players are recorded at the same moment. A snapshot with
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position, player.State.Mode);
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }

    // 기능: 한 플레이어의 Tick 부분(server review M7: Tick이 격리한다): 입력, (Phase 14 기절이면 출혈), 이동, 재장전, 행동,
    //   (Phase 14 소생·재투입 진행), 회복 완료.
    // 입력: player - 플레이어, now - 마지막으로 끝난 Tick.
    // 출력: 반환값 없음. 플레이어 상태가 갱신된다.
    private void TickPlayer(PlayerEntity player, uint now)
    {
        int fault = FaultEntityId;
        if (fault != 0 && (fault == -1 || fault == player.EntityId)) throw new InvalidOperationException("test fault");
        InputButtons previous = player.LastInput.Buttons;   // final review A5: the last real input's buttons
        bool sent = TakeInput(player, out InputCommand input);
        // D9: a dead player's input is still taken and acked (LastProcessedSeq) but moves and fires nothing.
        // A player killed earlier in this loop is already dead here.
        if (!player.Alive) return;
        // Phase 14 D5: a knocked-down player bleeds every tick, input or not (the server times it, request §37).
        if (player.IsDowned && Bleed(player)) return;

        // Phase 12 D5: a rider is placed on the route at the tick being simulated (now + 1, the tick its snapshot
        // reports) and may jump. Everyone else steps with the same boxes and terrain as client prediction
        // (LocalPlayerPredictor), so predictions match.
        if (_hasRoute && DropTransport.Ride(ref player.State, input, _route, now + 1)) player.Sprinting = false;
        else if (!Move(player, input)) return;   // the landing killed it
        WeaponRules.UpdateReload(player, now);
        // Only an input the client really sent can act: the missed-input repeat copies the last input's
        // buttons and must never invent a switch, reload or shot. Phase 12 D12: and only in a mode that allows
        // actions (after this tick's move).
        if (sent && ActionsAllowed(player.State.Mode)) ProcessActions(player, input, previous, now);
        // Gated, the fire button's held state still follows the input: landing with Fire held must not fire a
        // semi-automatic weapon without a new press (the client's WeaponState does the same).
        else if (sent) player.FireHeld = (input.Buttons & InputButtons.Fire) != 0;
        // Phase 14 D7, D8: a held E revives or reboots (after the actions, so a shot or a key of this input cancels it).
        if (player.Alive) UpdateChannel(player, input, sent, previous, now);
        ConsumableRules.Complete(player, _items, now);   // step 9: every tick, input or not
    }

    // Server review M7: the failed players leave (a connected one through Leave, a graced one directly), then GameLoop
    // is told, even when the leave itself throws (that exception then fails the tick as before).
    private void RemoveFailedPlayers()
    {
        foreach ((PlayerEntity player, Exception error) in _failedPlayers)
        {
            int peerId = player.PeerId;
            try
            {
                if (peerId != PlayerEntity.NoPeer) Leave(peerId);
                else RemovePlayer(player);
            }
            finally
            {
                _playerFailed?.Invoke(peerId, error);
            }
        }
        _failedPlayers.Clear();
    }

    // 기능: 한 번의 Step과 그 결과의 Phase 12 검사(이동 자체 검사 D12, 낙하 피해 D10). 공중에서 기절한 뒤 첫 착지는 낙하 피해가 없다.
    // 입력: player - 플레이어, input - 이 Tick 입력.
    // 출력: 착지로 탈락했으면 false(Phase 14: 기절은 true, 이어지는 행동은 모드가 막는다).
    private bool Move(PlayerEntity player, in InputCommand input)
    {
        MovementMode before = player.State.Mode;
        Vector3 from = player.State.Position;
        // Phase 13 D3: the same gather as the client's prediction (LocalPlayerPredictor.Simulate).
        MovementSimulation.Step(ref player.State, input, _tickSeconds, GatherAround(from), GameMap.Terrain, out StepResult step);
        player.Sprinting = step.Sprinting;

        // D9: sprinting or sliding into a closed door shoulders it open; the move goes on next tick.
        if (step.Charging && !step.BlockedBy.IsNone)
        {
            int door = _doors.DoorBlocking(step);
            if (door >= 0) _doors.Set(door, true);
        }

        // Phase 13: a move that starts inside a building piece (one just built over or under the player, which a ramp lifts
        // by up to 3 m at once) is the piece's push, not the simulation's: it is not an anomaly. Map boxes, doors and
        // harvestables still count. Checked only past the limit.
        float limit = MathF.Max(MovementLimits.MaxSpeed(before), MovementLimits.MaxSpeed(player.State.Mode)) * _tickSeconds * MovementLimits.Slack;
        if (Vector3.DistanceSquared(from, player.State.Position) > limit * limit &&
            !MovementSimulation.Penetrates(from, MovementSimulation.CollisionHeight(before), _collision, piecesOnly: true))
        {
            MovementAnomalies++;
            _movementAnomaly?.Invoke();
        }

        // D10: like shots, a fall hurts only when damage is allowed (the dev sandbox, or during the match).
        // Review fix: the first landing after a knock-down in the air does no fall damage (PlayerEntity.DownedInAir).
        if (step.LandingSpeed > 0f)
        {
            bool spared = player.DownedInAir && player.IsDowned;
            player.DownedInAir = false;
            if (!spared && _flow.DamageAllowed) ApplyFallDamage(player, step.LandingSpeed);
        }
        return player.Alive;
    }

    // Phase 13 D3: the colliders a step at these feet may touch: map boxes, closed doors, standing harvestables and the
    // pieces around.
    private CollisionWorld GatherAround(Vector3 feet)
    {
        _collision.Gather(feet, _doors.OpenMask, _harvest.DestroyedMask, _build.Grid);
        return _collision;
    }

    // 기능: 플레이어마다 기다리는 건설 요청(배치·편집)을 오래된 순서로 처리한다(Phase 13 D8, D9, Phase 13.5 D4).
    //   MinBuildInterval마다 하나만 반영한다: 조각을 놓거나 바꾼 요청 뒤의 요청은 다음 Tick까지 기다린다(거절하지 않는다).
    //   거절된 요청과 상태가 같아 아무것도 바꾸지 않은 편집(Ok)은 간격을 쓰지 않는다. 마지막으로 처리한 순번보다 새롭지
    //   않은 순번은 버린다(재전송·중복, 요청서 §149). 배치와 편집은 같은 순번 공간을 쓴다.
    // 입력: now - 마지막으로 끝난 Tick.
    // 출력: 반환값 없음. 조각이 놓이거나 편집되고 요청마다 BuildResult가 전송된다.
    private void ProcessBuildRequests(uint now)
    {
        foreach (var player in _players)
        {
            while (player.BuildQueue.Count > 0 && now >= player.NextBuildTick)
            {
                player.BuildQueue.TryTake(out BuildQueueItem request);
                ushort sequence = request.Sequence;
                if (player.HasBuildSequence && !BuildRequest.IsNewer(sequence, player.LastBuildSequence))
                {
                    BuildDuplicates++;
                    continue;
                }
                player.LastBuildSequence = sequence;
                player.HasBuildSequence = true;
                BuildResultCode code;
                uint id;
                bool changed;
                if (request.IsEdit)
                {
                    code = TryEdit(player, request.Edit, out id, out changed);
                }
                else
                {
                    code = TryBuild(player, request.Place, now + 1, out id);
                    changed = code == BuildResultCode.Ok;
                }
                _buildResults[(int)code]++;
                SendBuildResult(player, sequence, code, id);
                if (!changed) continue;
                player.NextBuildTick = now + _building.MinBuildIntervalTicks;
                break;
            }
        }
    }

    // 기능: 편집 요청을 검증하고 맞으면 조각 모양을 바꾼다(Phase 13.5 D5). 순서대로 보고 처음 걸린 이유로 답한다:
    //   1 살아 있음·경기 진행·행동 가능 모드(도구는 보지 않는다, §29) → InvalidState, 2 조각 있음 → NotFound,
    //   3 BuildRules.CanEdit → NotOwner, 4 BuildEdit.TryApply(유효한 상태, Ramp 외 회전 불변) → InvalidRequest,
    //   (상태가 지금과 같으면 여기서 Ok, 아무것도 바꾸지 않음), 5 InReach → OutOfRange, 6 맵·문·지형 또는 대상 외 조각이 시선을
    //   막음 → Blocked, 7 새로 막히는 부분이 캐릭터를 가르거나 Vault 경로를 가로지르거나 올린 몸이 들어가지 않음 → Blocked,
    //   8 Ramp 회전 변경 뒤 접지도 이웃도 없음 → Unsupported. 성공하면 id·소유자·재료·진행·피해는 그대로 두고 모양만 바꾼다(D6).
    // 입력: player - 요청한 플레이어, request - 편집 요청, id - 결과 조각 id, changed - 결과 변경 여부.
    // 출력: 결과 코드. Ok면 id = 대상 id(changed = 모양이 실제로 바뀌었는지), 거절이면 id 0과 changed false.
    private BuildResultCode TryEdit(PlayerEntity player, in BuildEditRequest request, out uint id, out bool changed)
    {
        id = 0;
        changed = false;
        if (!player.Alive || _flow.State == MatchFlowState.Finished || _flow.State == MatchFlowState.Closing || !ActionsAllowed(player.State.Mode))
            return BuildResultCode.InvalidState;
        if (request.PieceId == 0 || !_build.TryGetSlot(request.PieceId, out int slot)) return BuildResultCode.NotFound;
        BuildPiece piece = _build.At(slot);
        if (!BuildRules.CanEdit(player.EntityId, piece)) return BuildResultCode.NotOwner;
        BuildPieceShape before = piece.Shape;
        if (!BuildEdit.TryApply(before, request.State, out BuildPieceShape after)) return BuildResultCode.InvalidRequest;
        if (after.Equals(before))
        {
            // D5: confirming the state the piece already has is Ok and changes nothing (no event, no interval): a resent
            // or repeated confirm is idempotent.
            id = piece.Id;
            return BuildResultCode.Ok;
        }

        Vector3 eye = player.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(player.State.Mode), 0f);
        if (!CombatRules.TryAimDirection(player.LastInput.AimYaw, player.LastInput.AimPitch, out Vector3 aim) ||
            !BuildRules.InReach(eye, aim, before, _building.BuildRange, _building.ViewAngleDegrees))
            return BuildResultCode.OutOfRange;
        if (BuildRules.BehindAWall(eye, before, _doors.World, GameMap.Terrain) || BuildRules.BehindAPiece(eye, before, piece.Id, _build) ||
            EditCutsAPlayer(piece.Id, before, after))
            return BuildResultCode.Blocked;

        if (!ApplyEdit(slot, before, after)) return BuildResultCode.Unsupported;
        CancelChannel(player);   // Phase 14 D8: an edit is another action
        id = piece.Id;
        changed = true;
        return BuildResultCode.Ok;
    }

    // 기능: 검증이 끝난 편집을 반영한다(D5-8 지지 확인 포함). 모양만 바꾸고(D6), Ramp 회전이면 지지 모서리·Grounded를 다시
    //   쓰며 잃은 이웃을 Tick 끝 붕괴 탐색에 넣고(D7), Edited 이벤트를 남긴다(D8).
    // 입력: slot - 조각의 slot, before - 지금 모양, after - 편집 뒤 모양(같은 슬롯 키, 다름).
    // 출력: 반영했으면 true. Ramp 회전 뒤 접지도 이웃도 없으면 false(아무것도 바뀌지 않는다).
    private bool ApplyEdit(int slot, in BuildPieceShape before, in BuildPieceShape after)
    {
        bool reshaped = after.Type == BuildPieceType.Ramp && after.Rotation != before.Rotation;
        bool grounded = _build.At(slot).Grounded;
        if (reshaped)
        {
            grounded = BuildSupport.IsGrounded(after, GameMap.Terrain, GameMap.Boxes);
            if (!grounded && !_support.HasNeighbourOtherThan(after, slot)) return false;
        }
        _build.SetShape(slot, after);
        // D7: only a ramp's turn changes its lattice edges; its lost neighbours and itself are searched at the end of the
        // tick (CollapseUnsupported), with this tick's destroys.
        if (_support.Reshape(slot, before, after)) _build.At(slot).Grounded = grounded;
        _replication.Edited(slot);
        BuildEdits++;
        return true;
    }

    // 기능: QA-1 editBuild: 플레이어 없이 조각을 편집한다(소유자·사거리·시선·몸 검사 없음, 상태 유효성과 Ramp 지지는 본다).
    //   Tick 사이에 부르고, 이벤트는 다음 Tick의 BuildEvents로, 붕괴 탐색은 곧바로 돈다(DamagePieceById와 같은 규칙).
    // 입력: id - 조각 id, state - 새 상태(BuildEdit.PackState).
    // 출력: Ok(바뀌었거나 이미 같은 상태), NotFound, InvalidRequest, Unsupported.
    internal BuildResultCode EditPieceById(uint id, ushort state)
    {
        if (!_build.TryGetSlot(id, out int slot)) return BuildResultCode.NotFound;
        BuildPieceShape before = _build.At(slot).Shape;
        if (!BuildEdit.TryApply(before, state, out BuildPieceShape after)) return BuildResultCode.InvalidRequest;
        if (after.Equals(before)) return BuildResultCode.Ok;
        if (!ApplyEdit(slot, before, after)) return BuildResultCode.Unsupported;
        CollapseUnsupported();
        return BuildResultCode.Ok;
    }

    // 기능: 편집으로 새로 막히는 부분이 살아 있는 캐릭터를 가두는지 본다(Phase 13.5 D5-7, 배치의 CutsAPlayer를 새 부분에만).
    //   벽·바닥: 다시 채워지는 칸들(합친 상자)이 몸 중심을 품거나 진행 중인 Vault 경로를 가로지르면 true. 뚫기만 하는 편집은
    //   공간을 넓힐 뿐이라 false. 지붕: 새 모양 전체(평지붕·통로는 어떤 지붕의 부피 안이라 몸 중심 검사만 의미가 있다).
    //   경사면(Ramp 회전, 경사 지붕): 배치처럼 Vault 경로와, 몸을 올린 자리에 새 모양을 넣은 World에서 몸이 들어가는지.
    // 입력: id - 대상 조각 id, before - 지금 모양, after - 편집 뒤 모양.
    // 출력: 누군가를 가두면 true.
    private bool EditCutsAPlayer(uint id, in BuildPieceShape before, in BuildPieceShape after)
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        int count;
        bool slope;
        if (after.Type == BuildPieceType.Wall || after.Type == BuildPieceType.Floor)
        {
            int tileMask = (1 << BuildEdit.TileCount(after.Type)) - 1;
            int refilled = before.Edit & ~after.Edit & tileMask;
            if (refilled == 0) return false;
            // Only the refilled tiles: the shape whose holes are every other tile (PartsOf merges them into few boxes).
            var newPart = new BuildPieceShape(after.Type, after.X, after.Y, after.Z, after.Rotation, ~refilled & tileMask);
            count = BuildGrid.PartsOf(newPart, parts, out slope);
        }
        else
        {
            count = BuildGrid.PartsOf(after, parts, out slope);
        }
        Box bounds = BuildGrid.BoundsOf(after);
        foreach (var p in _players)
        {
            if (!p.Alive) continue;
            float height = MovementSimulation.CollisionHeight(p.State.Mode);
            bool vaulting = p.State.Mode == MovementMode.Vault && p.State.ModeTicks > 0;
            for (int i = 0; i < count; i++)
            {
                if (BuildRules.BoxHoldsBodyCentre(parts[i], p.State.Position, height)) return true;
                if (vaulting && VaultPathOverlaps(p.State, height, parts[i])) return true;
            }
            if (!slope) continue;
            if (vaulting && VaultPathOverlaps(p.State, height, bounds)) return true;
            if (BuildRules.Lifts(after, p.State.Position, height, out Vector3 lifted) && LiftedPenetrates(id, before, after, lifted, height, p.State.Position))
                return true;
        }
        return false;
    }

    // 기능: 편집 뒤 모양을 잠깐 Grid에 넣고 올린 몸이 어딘가에 박히는지 본 뒤 원래 모양으로 되돌린다(옛 경사면 때문에 잘못
    //   거부하지 않게). Game Loop 스레드에서만 부르며 사이에 다른 코드가 Grid를 읽지 않는다.
    // 입력: id - 조각 id, before·after - 지금·편집 뒤 모양, lifted - 올린 발 위치, height - 몸 높이, feet - 원래 발 위치(수집 중심).
    // 출력: 몸이 들어가지 않으면 true.
    private bool LiftedPenetrates(uint id, in BuildPieceShape before, in BuildPieceShape after, Vector3 lifted, float height, Vector3 feet)
    {
        _build.Grid.SetShape(id, after);
        bool penetrates = MovementSimulation.Penetrates(lifted, height, GatherAround(feet));
        _build.Grid.SetShape(id, before);
        return penetrates;
    }

    // 기능: 배치 요청을 순서대로 검증하고 맞으면 조각을 놓는다(Phase 14: 놓으면 진행 중인 소생·재투입이 끊긴다).
    // 입력: player - 요청자, request - 배치 요청, tick - 시뮬레이션 중인 Tick(CreatedTick), id - 새 조각 id.
    // 출력: 결과 코드(Ok면 id가 새 조각).
    // D9 (request §45): every check in order; the first that fails is the answer, and nothing changes then. On success the
    // resources go first, then the piece is made and announced (request §51). tick: the tick being simulated (the piece's
    // CreatedTick).
    private BuildResultCode TryBuild(PlayerEntity player, in BuildRequest request, uint tick, out uint id)
    {
        id = 0;
        if (!player.Alive || _flow.State == MatchFlowState.Finished || _flow.State == MatchFlowState.Closing) return BuildResultCode.InvalidState;
        if (player.Inventory.Tool != ToolKind.Build || !ActionsAllowed(player.State.Mode)) return BuildResultCode.InvalidState;
        if (request.Material > (byte)BuildMaterialType.Metal ||
            !BuildGrid.TryNormalize((BuildPieceType)request.Piece, request.X, request.Y, request.Z, request.Rotation, out BuildPieceShape shape))
            return BuildResultCode.InvalidRequest;
        var material = (BuildMaterialType)request.Material;
        if (_build.Count >= _building.MaxPiecesPerMatch || _build.OwnerCount(player.EntityId) >= _building.MaxPiecesPerPlayer)
            return BuildResultCode.BudgetFull;
        if (BuildRules.Occupied(_build, shape)) return BuildResultCode.Occupied;

        Vector3 eye = player.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(player.State.Mode), 0f);
        if (!CombatRules.TryAimDirection(player.LastInput.AimYaw, player.LastInput.AimPitch, out Vector3 aim) ||
            !BuildRules.InReach(eye, aim, shape, _building.BuildRange, _building.ViewAngleDegrees))
            return BuildResultCode.OutOfRange;
        if (BuildRules.Buried(shape, GameMap.Terrain) || BuildRules.BehindAWall(eye, shape, _doors.World, GameMap.Terrain) || CutsTheMap(shape) ||
            CutsAPlayer(shape))
            return BuildResultCode.Blocked;

        // D12: on the ground or a map box, or held by a standing piece.
        bool grounded = BuildSupport.IsGrounded(shape, GameMap.Terrain, GameMap.Boxes);
        if (!grounded && !_support.HasNeighbour(shape)) return BuildResultCode.Unsupported;

        int cost = _infiniteResources ? 0 : _building.Material(material).ResourceCost;
        int have = player.Inventory.Resource(material);
        if (have < cost) return BuildResultCode.NoResource;

        player.Inventory.SetResource(material, have - cost);
        id = _build.Add(shape, material, player.EntityId, tick, grounded);
        ConsumableRules.Cancel(player.Inventory);   // final review B9: placing interrupts a heal, like firing
        CancelChannel(player);                      // Phase 14 D8: and a revive or reboot
        if (id == 0) return BuildResultCode.BudgetFull;   // unreachable: the budget was checked
        _build.TryGetSlot(id, out int slot);
        _support.Add(slot, shape);
        _replication.Placed(Record(_build.At(slot)));
        return BuildResultCode.Ok;
    }

    // D9: a map box, any door (open or closed: a door must be able to close) or a standing harvestable overlapped too much.
    private bool CutsTheMap(in BuildPieceShape shape)
    {
        foreach (Box box in GameMap.Boxes)
        {
            if (BuildRules.OverlapsTooMuch(shape, box)) return true;
        }
        foreach (Box door in GameMap.Doors)
        {
            if (BuildRules.OverlapsTooMuch(shape, door)) return true;
        }
        ReadOnlySpan<Harvestable> all = GameMap.Harvestables;
        for (int i = 0; i < all.Length; i++)
        {
            if (!_harvest.IsDestroyed(i) && BuildRules.OverlapsTooMuch(shape, all[i].Bounds)) return true;
        }
        return false;
    }

    // D9: a wall or floor through a living character's body centre. Final review B6: also a ramp or roof that would lift a
    // character into something overhead (its body does not fit on the new surface: a floor, roof or map box above), and
    // any piece across the rest of a vault in progress (a vault moves without collision, D8, so it would pass through).
    private bool CutsAPlayer(in BuildPieceShape shape)
    {
        foreach (var p in _players)
        {
            if (!p.Alive) continue;
            float height = MovementSimulation.CollisionHeight(p.State.Mode);
            if (BuildRules.HoldsBodyCentre(shape, p.State.Position, height)) return true;
            if (p.State.Mode == MovementMode.Vault && p.State.ModeTicks > 0 && VaultPathOverlaps(p.State, height, BuildGrid.BoundsOf(shape))) return true;
            // Gathered only for a character the slope would lift (the new piece is not in the world yet).
            if (BuildRules.Lifts(shape, p.State.Position, height, out Vector3 lifted) &&
                MovementSimulation.Penetrates(lifted, height, GatherAround(p.State.Position)))
                return true;
        }
        return false;
    }

    private static BuildPieceRecord Record(in BuildPiece piece) => BuildReplication.Record(piece);

    private void SendBuildResult(PlayerEntity player, ushort sequence, BuildResultCode code, uint id)
    {
        var writer = new PacketWriter(_sendBuffer);
        BuildResult.Write(ref writer, new BuildResult { Sequence = sequence, Code = code, PieceId = id });
        _sendBuild(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D13, D14, end of the tick, per client: its interest window first (BuildInterest when it changed), then this tick's
    // events in its window, then up to MaxSyncPacketsPerTick sync packets for the cells it entered. All on the building
    // channel, in this order.
    private void SendBuildEvents()
    {
        _replication.Collect();
        foreach (var p in _players) UpdateInterest(p);
        if (_replication.HasEvents)
        {
            foreach (var p in _players)
            {
                if (p.IsGraced) continue;
                var cursor = new BuildReplication.Cursor();
                int length;
                while ((length = _replication.NextPacket(_sendBuffer, ref cursor, p.InterestCells)) > 0)
                {
                    _sendBuild(p.PeerId, _sendBuffer.AsSpan(0, length), DeliveryMethod.ReliableOrdered);
                    BuildEventPackets++;
                }
            }
        }
        _replication.Clear();
        foreach (var p in _players)
        {
            if (p.IsGraced || (p.SyncPending == 0 && p.SyncCell < 0)) continue;
            // Final review A4: a client whose building channel is backed up gets no sync this tick (the rest still goes).
            if (_buildBacklog != null && _buildBacklog(p.PeerId) > MaxBuildBacklog)
            {
                SyncsDeferred++;
                continue;
            }
            // Final review A1: the cells nearest to the player first, so its own surroundings arrive before far ones.
            int center = _replication.InterestCellAt(p.State.Position);
            for (int i = 0; i < BuildReplication.MaxSyncPacketsPerTick; i++)
            {
                int length = _replication.NextSyncPacket(_sendBuffer, ref p.SyncPending, ref p.SyncCell, ref p.SyncColumn, ref p.SyncAfterId, center);
                if (length == 0) break;
                _sendBuild(p.PeerId, _sendBuffer.AsSpan(0, length), DeliveryMethod.ReliableOrdered);
                BuildSyncPackets++;
            }
        }
    }

    // D14: a living player keeps the cells around it (radius, plus the keep margin for cells it has); a dead player or a
    // spectator the whole map. Cells it enters go to its sync queue; cells it leaves stop being sent and its client drops
    // their pieces (BuildInterest).
    private void UpdateInterest(PlayerEntity p)
    {
        if (p.IsGraced) return;
        ulong desired = !p.Alive ? _replication.AllCells
            : _replication.WindowFor(_replication.InterestCellAt(p.State.Position), p.InterestCells, _building.InterestRadius, _building.InterestKeepMargin);
        if (desired == p.InterestCells) return;
        ulong entered = desired & ~p.InterestCells;
        p.InterestCells = desired;
        p.SyncPending = (p.SyncPending | entered) & desired;
        if (p.SyncCell >= 0 && (desired & (1UL << p.SyncCell)) == 0) p.SyncCell = -1;
        var writer = new PacketWriter(_sendBuffer);
        BuildInterestPacket.Write(ref writer, desired);
        _sendBuild(p.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Phase 13 D4: what the client needs of the building numbers. Final review A3: on the building channel, right before
    // the join's (or resume's) reset sync, so the channel's first packet is the catalog and no BuildInterest or piece is
    // ever read with a default interest cell size (the channels are independent of each other).
    private void SendBuildCatalog(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        BuildCatalogPacket.Write(ref writer, _buildCatalogWire);
        _sendBuild(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D14: a join or a resume: the client drops whatever it has (a reset sync), and the window and its pieces follow at
    // the end of the tick.
    private void StartBuildSync(PlayerEntity player)
    {
        player.ResetInterest();
        var writer = new PacketWriter(_sendBuffer);
        BuildSyncPacket.WriteHeader(ref writer, _replication.Version, reset: true, count: 0);
        _sendBuild(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D10 (request §70, §132): every piece goes at a round reset and a match start; clients clear theirs (a reset sync).
    private void ClearBuilds()
    {
        _build.Clear();
        _support.Clear();
        _replication.Reset();
        foreach (var p in _players)
        {
            p.BuildQueue.Clear();
            p.NextBuildTick = 0;
            StartBuildSync(p);
        }
    }

    // Phase 13 D6: the map boxes, the closed doors and the standing harvestables, for shots, swings and drops.
    private ReadOnlySpan<Box> Blockers
    {
        get
        {
            if (_blockerCount < 0 || _blockerDoors != _doors.OpenMask || _blockerHarvest != _harvest.DestroyedMask)
            {
                ReadOnlySpan<Box> world = _doors.World;
                world.CopyTo(_blockers);
                int count = world.Length;
                ReadOnlySpan<Harvestable> all = GameMap.Harvestables;
                for (int i = 0; i < all.Length; i++)
                {
                    if (!_harvest.IsDestroyed(i)) _blockers[count++] = all[i].Bounds;
                }
                _blockerCount = count;
                _blockerDoors = _doors.OpenMask;
                _blockerHarvest = _harvest.DestroyedMask;
            }
            return new ReadOnlySpan<Box>(_blockers, 0, _blockerCount);
        }
    }

    // Phase 12 D12: riding, falling, gliding and vaulting allow no shot, reload, pickup, interaction, heal, slot switch
    // or drop. A reload or heal already running goes on.
    // Final review B7: a piece this close to (or level with) the map surface a ray met is hit first.
    private const float PieceTieTolerance = 1e-3f;

    private static bool ActionsAllowed(MovementMode mode) =>
        mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;

    // 기능: 낙하 피해(Phase 12 D10): 체력만 줄이고(실드는 막지 않는다) 공격자 없는 DamageTaken을 보낸다(Phase 18: 실드 플래그 없음). 치명이면 치명 경로 하나
    //   (Phase 14 D6: 기절 또는 탈락, 원인 Fall). 기절한 채 기어서 떨어져도 같다. 진행 중인 소생·재투입은 정책대로 끊긴다.
    // 입력: player - 착지한 플레이어, landingSpeed - 착지 속도(m/s).
    // 출력: 반환값 없음.
    private void ApplyFallDamage(PlayerEntity player, float landingSpeed)
    {
        int damage = CombatRules.FallDamage(landingSpeed);
        if (damage <= 0) return;
        player.Health = Math.Max(0, player.Health - damage);
        var writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken { AttackerId = 0, Damage = (ushort)damage, FromDirection = Vector3.Zero });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        OnDamaged(player);
        if (player.Health == 0) ApplyFatal(player, null, DeathCause.Fall);
    }

    // One buffered input per tick (Phase 0). Returns false when the input is made up by the server.
    private bool TakeInput(PlayerEntity player, out InputCommand input)
    {
        if (player.Inputs.TryTake(out input))
        {
            player.LastInput = input;
            player.LastProcessedSeq = input.Seq;
            player.MissedTicks = 0;
            return true;
        }
        // This also covers a graced player (Phase 10 D2): its buffer was reset at the drop, so it coasts on its last
        // input for up to SimHz / 2 ticks and then stands still for the rest of the grace.
        if (player.MissedTicks < _simHz / 2)
        {
            player.MissedTicks++;
            // No input arrived in time: keep moving the same way, but never repeat a jump.
            // The ack does not advance, so the client replays its own input over this.
            input = player.LastInput;
            input.Buttons &= ~InputButtons.Jump;
            return false;
        }
        // Input missing for over half a second (paused or backgrounded client): stop walking
        // instead of repeating the last move until the disconnect timeout. Gravity still applies.
        input = new InputCommand { Seq = player.LastInput.Seq, Yaw = player.LastInput.Yaw };
        return false;
    }

    // 기능: 살아 있는 플레이어의 실제 입력 하나를 spec §2 순서로 처리한다. Phase 14 D7: 소생·재투입 대상이 범위 안이면 E 누름은 문·줍기를 하지 않는다.
    //   Phase 16 D4: 그 밖의 E는 Interact(문과 Container 중 가까운 쪽, 없으면 줍기)다. Phase 17: 투사체 무기는 빈 칸·경기 상태를 탄 소비 전에
    //   확인하고(D6), 발사 뒤 6 누름이 수류탄을 던진다(D9, 회복 시작 전).
    // 입력: player - 행위자, input - 받은 입력, previous - 직전 실제 입력의 버튼, now - 마지막 Tick.
    // 출력: 반환값 없음.
    // One real input of a living player, in the spec §2 order: cancel use -> tool -> slot -> drop -> pickup ->
    // reload -> fire -> start use. (Movement came first; finishing a use comes after, every tick.)
    // Phase 13 D5: Fire acts by the tool in hand: a shot (Weapon), a swing (Harvest), nothing (Build: placing is a
    // BuildRequest). Reload is the weapon's only.
    // Final review A5: the press keys (EdgeButtons) act only on the input that pressed them: held over several inputs (a
    // modified client) they act once, like the client, which sends each press in one input only. previous: the buttons of
    // the last real input before this one.
    private void ProcessActions(PlayerEntity player, in InputCommand input, InputButtons previous, uint now)
    {
        InputButtons buttons = PressedOnly(input.Buttons, previous);
        ConsumableRules.CancelIfInterrupted(player, buttons);
        HarvestRules.SelectTool(player, buttons);
        WeaponRules.SelectSlot(player, buttons);
        if ((buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        // Phase 12 D9: E acts on a door in front first, an item otherwise. Phase 14 D7: neither while a revive or reboot target
        // is in reach (holding E there starts that instead). Phase 16 D4: a container in reach competes with the door (the nearer wins).
        if ((buttons & InputButtons.Interact) != 0 && !HasChannelTarget(player, now)) Interact(player);

        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        bool fire = (buttons & InputButtons.Fire) != 0;
        switch (player.Inventory.Tool)
        {
            case ToolKind.Weapon:
                // Phase 17 D6: a projectile weapon without a free projectile slot (or while ProjectilesAllowed is false: the start
                // countdown or the result screen) does not fire and spends nothing, the same as an invalid aim.
                if (WeaponRules.Apply(player, buttons, aimValid && CanLaunch(player.Inventory.Current.Weapon), now))
                    FireShot(player, direction, input.ViewTick);
                break;
            case ToolKind.Harvest:
                player.FireHeld = fire;
                if (fire && aimValid) Swing(player, direction, now);
                break;
            default:
                player.FireHeld = fire;
                break;
        }

        if ((buttons & InputButtons.ThrowGrenade) != 0) TryThrowGrenade(player, aimValid, direction, now);   // Phase 17 D9

        ConsumableRules.TryStart(player, _items, buttons, now);
    }

    // Final review A5: the keys that act once per press. Fire (held: automatic fire, harvest swings), the heals, Jump,
    // Sprint and Crouch keep their held meaning.
    // Phase 17 D9: ThrowGrenade too (held, it throws once; the client sends each press in one input).
    internal const InputButtons EdgeButtons = InputButtons.Interact | InputButtons.Drop | InputButtons.ToolHarvest | InputButtons.ToolBuild |
                                              InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.Reload |
                                              InputButtons.ThrowGrenade;

    // The buttons of this input with the press keys that were already down in the previous one taken away.
    internal static InputButtons PressedOnly(InputButtons buttons, InputButtons previous) => buttons & ~(previous & EdgeButtons);

    // 기능: 채집 휘두르기 하나를 처리한다(조각 피해 또는 채집 대상 타격·자원·HarvestHit). Phase 18 D7: 채집 대상을 맞혔으면
    //   맞은 점에서 WorldSoundRange 안의 다른 플레이어에게 WorldSound(타격 또는 파괴)를 보낸다.
    // 입력: player - 휘두른 플레이어, direction - 조준 방향(단위 벡터), now - 이번 Tick.
    // 출력: 반환값 없음. 대상 체력·자원이 바뀌고 HarvestHit(휘두른 사람)과 WorldSound(주변)가 전송된다.
    // Phase 13 D7: a harvest swing (held Fire swings at the cooldown) while damage is allowed (the dev sandbox or the
    // match). The server finds the target along the aim from the eye; a harvestable takes the damage, gives the swinger
    // its material up to the cap, and the swinger hears what happened (HarvestHit). A destroyed one leaves the world at
    // once (shots, moves) and everyone hears it at the end of the tick (HarvestStates).
    private void Swing(PlayerEntity player, Vector3 direction, uint now)
    {
        if (!_flow.DamageAllowed || now < player.NextSwingTick) return;
        player.NextSwingTick = now + _building.HarvestCooldownTicks;
        Vector3 origin = player.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(player.State.Mode), 0f);
        int target = HarvestRules.Trace(origin, direction, _building.HarvestRange, _harvest.DestroyedMask, Blockers, GameMap.Terrain,
            out float distance);
        // Phase 13 D11 (request §68): a piece in front takes the tool's structure damage and gives no resources.
        float pieceRange = target >= 0 ? distance : _building.HarvestRange;
        if (PieceTrace.Trace(origin, direction, pieceRange, _build, out _, out int pieceSlot, out float pieceDistance) &&
            HitScan.TraceWorld(origin, direction, pieceDistance, Blockers, GameMap.Terrain) >= pieceDistance - PieceTieTolerance)
        {
            DamagePiece(pieceSlot, _building.HarvestStructureDamage * _building.Material(_build.At(pieceSlot).Material).HarvestToolDamageMultiplier);
            return;
        }
        if (target < 0) return;

        Vector3 hitPoint = origin + direction * distance;
        HarvestHitResult hit = _harvest.Hit(target, hitPoint, direction);
        HarvestHits++;
        BuildMaterialType material = GameMap.Harvestables[target].Material;
        int have = player.Inventory.Resource(material);
        int gained = Math.Clamp(Math.Min(hit.Resources, _building.MaxResource - have), 0, byte.MaxValue);
        if (gained > 0) player.Inventory.SetResource(material, have + gained);

        byte flags = 0;
        if (hit.WeakPointHit) flags |= HarvestHit.WeakPointHitFlag;
        if (hit.Destroyed) flags |= HarvestHit.DestroyedFlag;
        if (_harvest.HasWeakPoint(target)) flags |= HarvestHit.HasWeakPointFlag;
        var writer = new PacketWriter(_sendBuffer);
        HarvestHit.Write(ref writer, new HarvestHit
        {
            TargetId = (byte)target,
            Health = (ushort)Math.Clamp(hit.HealthLeft, 0, ushort.MaxValue),
            WeakPoint = _harvest.WeakPoint(target),
            Gained = (byte)gained,
            Flags = flags,
        });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        SendWorldSound(player, hit.Destroyed ? WorldSoundKind.HarvestDestroyed : WorldSoundKind.HarvestHit, hitPoint);
    }

    // Phase 18 D7: how far (m) a WorldSound reaches; only players this close to the sound get the packet. Server only: the
    // client's own audio table decides how loud it plays.
    internal const float WorldSoundRange = 30f;

    // 기능: 소리를 낸 플레이어를 뺀, 소리 위치에서 WorldSoundRange 안의 플레이어에게 WorldSound를 Unreliable로 보낸다(Phase 18 D7).
    //   연결이 끊긴 유예 플레이어는 뺀다. 죽은 플레이어·관전자도 자기 몸 위치로 거리를 잰다. 패킷은 한 번만 쓰고 할당하지 않는다(휘두르기 때만, 플레이어 수만큼 거리 비교).
    // 입력: source - 소리를 낸 플레이어(받지 않는다), kind - 소리 종류, position - 소리 위치.
    // 출력: 반환값 없음. 범위 안의 다른 Client에게 WorldSound가 전송된다.
    private void SendWorldSound(PlayerEntity source, WorldSoundKind kind, Vector3 position)
    {
        var writer = new PacketWriter(_sendBuffer);
        WorldSound.Write(ref writer, new WorldSound { Kind = kind, SourceId = source.EntityId, Position = position });
        const float rangeSquared = WorldSoundRange * WorldSoundRange;
        foreach (var p in _players)
        {
            if (p == source || p.IsGraced || Vector3.DistanceSquared(p.State.Position, position) > rangeSquared) continue;
            _send(p.PeerId, writer.WrittenSpan, DeliveryMethod.Unreliable);
        }
    }

    // 기능: 한 번 쏜다. 투사체 무기는 투사체를 띄운다(Phase 17 D10, ShotFired 없음). Hitscan은 산탄마다(Phase 17 D4) 서버 결정적 퍼짐(D3)으로
    //   방향을 흔들어 맵·조각·플레이어(되감은 위치) 중 가장 가까운 것에 맞히고, 거리 감쇠(D2)를 곱한 원 피해를 대상마다 합친 뒤 등급 배율을
    //   한 번 곱해 대상마다 ApplyHit 한 번(HitConfirmed 하나), 조각도 합쳐 한 번(× 무기 structureMultiplier × 재료 배율, D11).
    //   ShotFired는 발사 하나에 하나(Phase 18 D4: 무기 id 포함): 한 발 무기는 퍼진 광선의 실제 끝점, 산탄총은 가운데 조준 광선의 끝점.
    //   Phase 14 D3: 같은 팀은 관통.
    // 입력: shooter - 사수, direction - 조준 방향(단위 벡터), viewTick - 사수가 본 Tick.
    // 출력: 반환값 없음. ShotFired(또는 ProjectileSpawned)가 방송되고 맞은 대상이 피해를 받는다.
    // D7: from the eye along the aim, the nearest map surface (box, terrain or floor plane) or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    // D6: other players are tested where the shooter saw them, at ViewTick (clamped to the last
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        ref HeldWeapon held = ref shooter.Inventory.Current;
        WeaponDefinition weapon = held.Weapon!;   // Apply only fires a filled slot
        float rarity = _items.DamageMultiplier(held.Rarity);
        // Phase 12 D13: crouched or sliding the eye is lower (the client aims from the same height, AimSolver).
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(shooter.State.Mode), 0f);
        if (weapon.IsProjectile)
        {
            LaunchProjectile(shooter, weapon, origin, direction, rarity);
            return;
        }
        double rewindTick = CombatRules.ClampViewTick(viewTick, ServerTick, _maxRewindTicks);
        uint tick = ServerTick + 1;
        int pellets = weapon.Pellets;
        _pelletTargetCount = 0;
        _pelletPieceCount = 0;
        Vector3 end = origin;
        for (int i = 0; i < pellets; i++)
        {
            Vector3 ray = WeaponSpread.Spread(direction, weapon.SpreadDegrees, shooter.EntityId, tick, i);
            float distance = TraceShot(shooter, origin, ray, weapon.Range, rewindTick, out PlayerEntity? target, out int pieceSlot);
            if (pellets == 1) end = origin + ray * distance;
            float raw = weapon.Damage * CombatRules.FalloffMultiplier(distance, weapon.FalloffStart, weapon.Range, weapon.FalloffMinRatio);
            if (target != null) AddPelletHit(target, raw);
            else if (pieceSlot >= 0) AddPelletPiece(pieceSlot, raw);
        }
        // D4: one tracer per trigger pull, along the unspread aim.
        if (pellets > 1) end = origin + direction * TraceShot(shooter, origin, direction, weapon.Range, rewindTick, out _, out _);

        var writer = new PacketWriter(_sendBuffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = shooter.EntityId, Start = origin, End = end, WeaponId = weapon.Id });   // Phase 18 D4
        Broadcast(writer.WrittenSpan, DeliveryMethod.Unreliable);

        // Phase 5 D2: before (and after) the match a shot still stops at the player it hit (the tracer shows
        // it), but it does no damage and the shooter gets no HitConfirmed.
        if (_flow.DamageAllowed)
        {
            for (int i = 0; i < _pelletTargetCount; i++)
            {
                // An earlier hit of this shot may have wiped the target's team (a downed teammate is eliminated with it).
                ushort damage = CombatRules.ScaledDamage(_pelletDamage[i], rarity);
                if (!_pelletTargets[i]!.Alive || damage == 0) continue;   // 0: a falloffMinRatio of 0 at full range
                ApplyHit(shooter, _pelletTargets[i]!, damage);
            }
            // Phase 13 D11, Phase 17 D11: the piece takes the (rarity-scaled) damage x the weapon's and its material's multipliers.
            for (int i = 0; i < _pelletPieceCount; i++)
            {
                int slot = _pelletPieces[i];
                if (_build.At(slot).Id == 0) continue;   // destroyed by an earlier hit of this shot (cannot happen today: one slot each)
                DamagePiece(slot, CombatRules.ScaledDamage(_pelletPieceDamage[i], rarity) * weapon.StructureMultiplier *
                                  _building.Material(_build.At(slot).Material).StructureDamageMultiplier);
            }
        }
        Array.Clear(_pelletTargets, 0, _pelletTargetCount);   // no reference kept past the shot
    }

    // Phase 17 D4: one shot's hits, summed per target before the rarity is applied once. At most MaxPellets entries each.
    private readonly PlayerEntity?[] _pelletTargets = new PlayerEntity?[WeaponCatalogPacket.MaxPellets];
    private readonly float[] _pelletDamage = new float[WeaponCatalogPacket.MaxPellets];
    private int _pelletTargetCount;
    private readonly int[] _pelletPieces = new int[WeaponCatalogPacket.MaxPellets];
    private readonly float[] _pelletPieceDamage = new float[WeaponCatalogPacket.MaxPellets];
    private int _pelletPieceCount;

    // 기능: 산탄 하나의 플레이어 명중을 대상별 합계에 더한다.
    // 입력: target - 맞은 플레이어, raw - 감쇠를 곱한 원 피해.
    // 출력: 반환값 없음.
    private void AddPelletHit(PlayerEntity target, float raw)
    {
        for (int i = 0; i < _pelletTargetCount; i++)
        {
            if (_pelletTargets[i] != target) continue;
            _pelletDamage[i] += raw;
            return;
        }
        _pelletTargets[_pelletTargetCount] = target;
        _pelletDamage[_pelletTargetCount++] = raw;
    }

    // 기능: 산탄 하나의 조각 명중을 조각별 합계에 더한다.
    // 입력: slot - 맞은 조각 slot, raw - 감쇠를 곱한 원 피해.
    // 출력: 반환값 없음.
    private void AddPelletPiece(int slot, float raw)
    {
        for (int i = 0; i < _pelletPieceCount; i++)
        {
            if (_pelletPieces[i] != slot) continue;
            _pelletPieceDamage[i] += raw;
            return;
        }
        _pelletPieces[_pelletPieceCount] = slot;
        _pelletPieceDamage[_pelletPieceCount++] = raw;
    }

    // 기능: 광선 하나의 첫 명중을 찾는다(맵 상자·닫힌 문·채집 대상·지형 → 조각(같은 거리면 조각) → 되감은 다른 팀 플레이어).
    // 입력: shooter - 사수(건너뜀), origin·ray - 광선, range - 사거리, rewindTick - 대상을 되감을 Tick, target·pieceSlot - 결과.
    // 출력: 멈춘 거리. 플레이어에 맞으면 target(pieceSlot -1), 조각에 맞으면 pieceSlot(target null), 아니면 둘 다 없음.
    private float TraceShot(PlayerEntity shooter, Vector3 origin, Vector3 ray, float range, double rewindTick, out PlayerEntity? target, out int pieceSlot)
    {
        float nearest = HitScan.TraceWorld(origin, ray, range, Blockers, GameMap.Terrain);   // a closed door or a tree stops it
        // Phase 13 D11: a piece in front stops the shot too (no rewind: pieces as they are now, like doors). Final review B7:
        // a piece level with what the world trace met wins (a level 0 floor's top is the ground plane, y 0).
        if (!PieceTrace.Trace(origin, ray, nearest + PieceTieTolerance, _build, out _, out pieceSlot, out float pieceDistance)) pieceSlot = -1;
        else nearest = pieceDistance;

        target = null;
        foreach (var other in _players)
        {
            // Phase 14 D3: friendly fire is off: a shot passes through teammates (it can hit an enemy behind one).
            if (other == shooter || !other.Alive || SameTeam(shooter, other)) continue;

            // Phase 12 D13: the hit box has the height of the mode the target was in then; a transport rider is not hit.
            Vector3 feet = other.History.Sample(rewindTick, out MovementMode mode);
            if (mode == MovementMode.Transport) continue;
            if (HitScan.TracePlayer(origin, ray, nearest, feet, MovementSimulation.CollisionHeight(mode), out float distance) &&
                (target == null || distance < nearest))
            {
                nearest = distance;
                target = other;
            }
        }
        if (target != null) pieceSlot = -1;
        return nearest;
    }

    // Phase 13 D11: damage to a piece, standing or under construction (its health is computed from the tick, D10). At 0 it
    // is destroyed at once; otherwise its health goes out once at the end of the tick, whatever the hits (request §85).
    private void DamagePiece(int slot, float amount)
    {
        ref BuildPiece piece = ref _build.At(slot);
        int damage = Math.Max(1, (int)MathF.Round(amount));
        piece.Damage = (ushort)Math.Min(ushort.MaxValue, piece.Damage + damage);
        if (_build.Health(piece, ServerTick + 1) <= 0) DestroyPiece(slot);
        else _replication.Damaged(slot);
    }

    // 기능: 체력이 0이 된 조각을 곧바로 World에서 빼고(이유 Destroyed, Phase 18 D6) 이웃을 Tick 끝 붕괴 탐색에 올린다.
    // 입력: slot - 조각 slot.
    // 출력: 반환값 없음. 조각이 사라지고 Destroyed 기록이 하나 는다.
    // Phase 13 D11: a piece leaves the world now (moves and shots of the rest of this tick no longer meet it) and every
    // client hears of it at the end of the tick. D12: then whatever it held up and nothing else holds collapses in this
    // same tick, in the same BuildEvents (request §80). Final review A2: the collapse search runs once at the end of the
    // tick for every destroy of the tick (CollapseUnsupported), so many destroys share one search; until then the pieces
    // that will fall still stand (they collide and stop shots for the rest of this tick).
    private void DestroyPiece(int slot)
    {
        _support.QueueNeighbours(slot);   // before RemovePiece, which takes the piece's edges away
        RemovePiece(slot, BuildDestroyReason.Destroyed);
    }

    // 기능: 이번 Tick의 파괴·편집으로 지지를 잃은 조각을 모두 무너뜨린다(이유 Collapsed, Phase 18 D6).
    // 입력: 없음(대기 중인 붕괴 탐색 시작점).
    // 출력: 반환값 없음. 무너진 조각마다 Destroyed 기록이 하나 늘고 PiecesCollapsed가 는다.
    // End of the tick (after every action, before the events go out): what this tick's destroys left unsupported falls.
    private void CollapseUnsupported()
    {
        if (_support.QueuedStarts == 0) return;
        ReadOnlySpan<int> fallen = _support.UnsupportedQueued(_build);
        for (int i = 0; i < fallen.Length; i++)
        {
            RemovePiece(fallen[i], BuildDestroyReason.Collapsed);
            PiecesCollapsed++;
        }
    }

    // Test seam: a piece destroyed as if its health reached 0 (support and events included), collapse at once.
    internal void DestroyPiece(uint id)
    {
        if (_build.TryGetSlot(id, out int slot)) DestroyPiece(slot);
        CollapseUnsupported();
    }

    // QA-1 (damageBuild): damage through the same path as a shot (destroy at 0, then the collapse search at once, as the
    // end of a tick does). Called between ticks; the events go out with the next tick's BuildEvents. False = no such piece.
    internal bool DamagePieceById(uint id, float amount, out bool destroyed)
    {
        destroyed = false;
        if (!_build.TryGetSlot(id, out int slot)) return false;
        DamagePiece(slot, amount);
        destroyed = !_build.Contains(id);
        CollapseUnsupported();
        return true;
    }

    // QA-1 (spawnBuildPiece): a piece placed without a player: the slot, the match budget and the support (D12) are
    // checked; reach, view, cost and the players are not (a test setup puts pieces where it needs them). Owner 0 = nobody.
    // Called between ticks, so it is created at the next simulated tick, like a request processed then.
    internal BuildResultCode PlacePiece(in BuildPieceShape shape, BuildMaterialType material, out uint id)
    {
        id = 0;
        if (_build.Count >= _building.MaxPiecesPerMatch) return BuildResultCode.BudgetFull;
        if (BuildRules.Occupied(_build, shape)) return BuildResultCode.Occupied;
        bool grounded = BuildSupport.IsGrounded(shape, GameMap.Terrain, GameMap.Boxes);
        if (!grounded && !_support.HasNeighbour(shape)) return BuildResultCode.Unsupported;
        id = _build.Add(shape, material, 0, ServerTick + 1, grounded);
        if (id == 0) return BuildResultCode.BudgetFull;
        _build.TryGetSlot(id, out int slot);
        _support.Add(slot, shape);
        _replication.Placed(Record(_build.At(slot)));
        return BuildResultCode.Ok;
    }

    // Test seam: several pieces destroyed within one tick, the collapse searched once afterwards (as Tick does).
    internal void DestroyPieces(ReadOnlySpan<uint> ids)
    {
        foreach (uint id in ids)
        {
            if (_build.TryGetSlot(id, out int slot)) DestroyPiece(slot);
        }
        CollapseUnsupported();
    }

    // 기능: 조각을 World·지지 격자에서 빼고 Destroyed 기록을 남긴다.
    // 입력: slot - 조각 slot, reason - 체력 0으로 부서졌는지(DestroyPiece) 지지를 잃고 무너졌는지(CollapseUnsupported, Phase 18 D6).
    // 출력: 반환값 없음. 조각이 사라지고 PiecesDestroyed가 는다.
    private void RemovePiece(int slot, BuildDestroyReason reason)
    {
        ref BuildPiece piece = ref _build.At(slot);
        uint id = piece.Id;
        _replication.Destroyed(slot, id, piece.Shape, reason);
        _support.Remove(slot);
        _build.Remove(id);
        PiecesDestroyed++;
    }

    // 기능: 사격 명중을 반영한다: 피해, 처치 기여, HitConfirmed(사수), DamageTaken(대상), 진행 끊기(Phase 14 D8), 치명이면 치명 경로 하나.
    //   Phase 14: HitConfirmed.Killed는 탈락일 때만 true다(기절시킨 명중은 false, Solo는 지금과 같다).
    //   Phase 18 D8: DamageTaken에 실드 맞음·깨짐 플래그(맞기 전 실드 > 0, 이번 피해로 0).
    // 입력: shooter - 사수, target - 맞은 플레이어(다른 팀), damage - 피해량.
    // 출력: 반환값 없음.
    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
    {
        int before = target.Health + target.Shield;
        int shieldBefore = target.Shield;
        bool fatal = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);
        if (_flow.InMatch && shooter.Participant && shooter != target) shooter.DamageDealt += before - (target.Health + target.Shield);
        bool killed = fatal && (target.IsDowned || !CanBeDowned(target));

        var writer = new PacketWriter(_sendBuffer);
        HitConfirmed.Write(ref writer, new HitConfirmed { TargetId = target.EntityId, Damage = damage, Killed = killed });
        _send(shooter.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        Vector3 toAttacker = shooter.State.Position - target.State.Position;
        float length = toAttacker.Length();
        writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken
        {
            AttackerId = shooter.EntityId,
            Damage = damage,
            FromDirection = length > 1e-4f ? toAttacker / length : Vector3.Zero,
            Flags = DamageTaken.FlagsFor(shieldBefore, target.Shield),
        });
        _send(target.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        OnDamaged(target);
        if (fatal) ApplyFatal(target, shooter, DeathCause.Zone);
    }

    // 기능: QA-1 damagePlayer: 공격자 없는 피해(낙하처럼, 단 실드를 먼저 깎는 사격 규칙). 대상은 DamageTaken을 듣고, 치명이면
    //   치명 경로 하나(Phase 14 D6: 같은 팀에 서 있는 구성원이 있으면 기절, 기절한 사람은 탈락). Phase 18 D8: 실드 플래그는 사격과 같다.
    // 입력: target - 대상, damage - 피해량, killed - 탈락했는지.
    // 출력: 살아 있던 대상이면 true, 아니면 false.
    internal bool DamagePlayer(PlayerEntity target, int damage, out bool killed)
    {
        killed = false;
        if (!target.Alive || damage <= 0) return false;
        int shieldBefore = target.Shield;
        bool fatal = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);
        var writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken
        {
            AttackerId = 0, Damage = (ushort)Math.Min(damage, ushort.MaxValue), FromDirection = Vector3.Zero,
            Flags = DamageTaken.FlagsFor(shieldBefore, target.Shield),
        });
        _send(target.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        OnDamaged(target);
        if (fatal) ApplyFatal(target, null, DeathCause.Zone);
        killed = !target.Alive;
        return true;
    }

    // 기능: QA-1 killPlayer: 진짜 탈락 경로(배치, PlayerDied, 소지품 드롭). Phase 14 D6: 기절 없이 바로 탈락한다(팀이 살아 있으면 카드를
    //   떨어뜨리고, 서 있는 팀원이 없으면 분대 전멸). 종료 검사는 다음 Tick이다.
    // 입력: victim - 대상.
    // 출력: 살아 있던 대상이면 true.
    internal bool KillPlayer(PlayerEntity victim)
    {
        if (!victim.Alive) return false;
        victim.Health = 0;
        Kill(victim, null);
        return true;
    }

    // QA-1 (forceMatchState finish): the match ends now as if one participant were left (every living participant gets
    // placement 1; the winner is the last of them in player order, D9). False outside a match.
    internal bool ForceFinish()
    {
        if (!_flow.InMatch) return false;
        FinishMatch(ServerTick);
        return true;
    }

    // QA-1 (setZone): the current shrink ends now and the next phase starts (its wait from now). Between ticks ServerTick
    // is the next tick's `now`. False outside a match, before the zone starts, or in its last phase.
    internal bool AdvanceZone()
    {
        if (!_flow.InMatch || _zone.Phase == 0 || _zone.IsFinalPhase) return false;
        _zone.EndShrink(ServerTick);
        _zone.Advance(ServerTick);
        if (_zone.IsFinalPhase) _flow.EnterFinalPhase();
        return true;
    }

    // QA-1: the player is standing on something (terrain, a box, a door, a harvestable or a piece), like the simulation sees it.
    internal bool IsGrounded(PlayerEntity player) =>
        MovementSimulation.IsGrounded(player.State, GatherAround(player.State.Position), GameMap.Terrain);

    // 기능: 자기장 단계를 넘기고 매 초 원 밖의 플레이어 체력을 줄인다. 체력 0은 치명 경로 하나(Phase 14: 기절 또는 탈락), 피해는 진행을 끊는다.
    // 입력: now - 마지막 Tick.
    // 출력: 반환값 없음.
    // D7, D8: the next phase when the shrink is over, then, once per second since the start, zone damage to
    // everyone outside the circle at this tick. Health only: the shield does not stop the zone. Players are
    // processed in list order, which fixes the placements of deaths in the same tick (D9).
    private void UpdateZone(uint now)
    {
        if (_zone.Advance(now) && _zone.IsFinalPhase) _flow.EnterFinalPhase();
        if (now == _matchStartTick || (now - _matchStartTick) % _simHz != 0) return;
        ushort damage = _zone.DamagePerSecond;
        if (damage == 0) return;
        foreach (var player in _players)
        {
            // Phase 12: a transport rider is above the map (its route reaches outside the circle), not in it.
            if (!player.Alive || player.State.Mode == MovementMode.Transport || !_zone.IsOutside(player.State.Position, now)) continue;
            player.Health = Math.Max(0, player.Health - damage);
            OnDamaged(player);
            // Phase 14 D6: the one fatal path (a knock-down, or an elimination).
            if (player.Health == 0) ApplyFatal(player, null, DeathCause.Zone);
        }
    }

    // 기능: 경기를 끝낸다(D9, Phase 14 D6). 전멸하지 않은 팀의 구성원(서 있음·기절·카드 대기 중, 나간 사람의 기록 포함)은 모두 배치 1이다.
    //   마지막 팀들이 같은 Tick에 전멸했으면 나중에 처리된 팀(배치 1)이 이긴다. 우승 팀이 여럿이면(QA 강제 종료) 플레이어 순서로 마지막
    //   팀이다. WinnerId = 우승 팀에서 경기에 남은 가장 작은 Entity id(모두 나갔으면 0). Solo는 지금과 같다. 진행 중인 소생·재투입은
    //   끊는다(결과 화면에서는 출혈·채널이 돌지 않는다). Phase 15: 모든 Ping·Waypoint를 지우고 Tick 끝에 각 팀에 빈 목록을 보낸다.
    //   Phase 17: 남은 투사체를 폭발 없이 지운다.
    // 입력: now - 마지막 Tick.
    // 출력: 반환값 없음. 결과가 보내지고 기록이 Sink로 간다.
    private void FinishMatch(uint now)
    {
        _flow.Finish(now);
        CancelAllChannels();   // Phase 14 review: no revive or reboot completes on the result screen
        ClearProjectiles();    // Phase 17 D6: gone without an explosion (clients clear theirs when MatchState changes)
        ClearMarkersAtFinish(); // Phase 15
        byte winnerTeam = 0;
        foreach (var player in _players)
        {
            if (!player.Participant) continue;
            if (player.TeamId != 0 && !_teamOut[player.TeamId]) player.Placement = 1;
            if (player.Placement == 1) winnerTeam = player.TeamId;
        }
        for (int i = 0; i < _leftParticipants.Count; i++)
        {
            LeftParticipant left = _leftParticipants[i];
            if (left.TeamId != 0 && !_teamOut[left.TeamId]) _leftParticipants[i] = left with { Record = left.Record with { Placement = 1 } };
        }
        PlayerEntity? winner = null;
        foreach (var player in _players)
        {
            if (player.Participant && player.TeamId == winnerTeam && winnerTeam != 0 && (winner == null || player.EntityId < winner.EntityId)) winner = player;
        }
        WinnerId = winner?.EntityId ?? 0;

        // D11: each participant still connected gets its own result. Spectators and leavers get none. A graced
        // participant's result is dropped here (NoPeer); Resume sends it again if it comes back before the round reset.
        foreach (var player in _players)
        {
            if (player.Participant) SendMatchResult(player);
        }

        // Phase 9: recorded only after every result is sent, and a failing record is counted, never thrown: the
        // players' results and the rest of this tick must not depend on persistence.
        if (_matchSink == null) return;
        try
        {
            _matchSink(BuildRecord(now, winner));
        }
        catch (Exception e)
        {
            MatchSinkFailures++;
            _sinkError ??= e;
        }
    }

    // 기능: E가 고른 문(DoorRules.FindTarget, Phase 12 D9)을 연다. 열려 있으면 그 자리에 살아 있는 캐릭터가 없을 때 닫는다.
    // 입력: door - 문 번호(0..DoorCount-1).
    // 출력: 반환값 없음. 문 상태가 바뀌면 Tick 끝에 DoorStates가 간다.
    private void ToggleDoor(int door)
    {
        if (!_doors.IsOpen(door)) _doors.Set(door, true);
        else if (!DoorOccupied(door)) _doors.Set(door, false);
    }

    // A living character's box overlaps the door's. A vaulter also occupies the rest of its straight vault path (current
    // position to position + velocity x ModeTicks, swept with its box): the vault moves without collision (D8), so a door
    // closed across that path would be passed through.
    private bool DoorOccupied(int door)
    {
        ReadOnlySpan<Box> doorBox = GameMap.Doors.Slice(door, 1);
        foreach (var p in _players)
        {
            if (!p.Alive) continue;
            float height = MovementSimulation.CollisionHeight(p.State.Mode);
            if (MovementSimulation.OverlapsAny(p.State.Position, height, doorBox)) return true;
            if (p.State.Mode == MovementMode.Vault && p.State.ModeTicks > 0 && VaultPathOverlaps(p.State, height, doorBox[0])) return true;
        }
        return false;
    }

    private bool VaultPathOverlaps(in MoveState state, float height, in Box box)
    {
        Vector3 from = state.Position;
        Vector3 to = from + new Vector3(state.HorizontalVelocity.X, state.VelocityY, state.HorizontalVelocity.Y) * (_tickSeconds * state.ModeTicks);
        Vector3 min = Vector3.Min(from, to) - new Vector3(MoveSettings.HalfWidth, 0f, MoveSettings.HalfWidth);
        Vector3 max = Vector3.Max(from, to) + new Vector3(MoveSettings.HalfWidth, height, MoveSettings.HalfWidth);
        return min.X < box.Max.X && max.X > box.Min.X && min.Y < box.Max.Y && max.Y > box.Min.Y && min.Z < box.Max.Z && max.Z > box.Min.Z;
    }

    // Phase 13 D15 (request §158, §159): every living player on foot takes the Material items within MaterialPickupRange,
    // up to the resource cap; what does not fit stays with the smaller amount. Players in list order, so two reaching
    // for one item: the first takes it. At most WorldItems.Capacity items per player, every few ticks.
    private void PickUpMaterials()
    {
        if (_worldItems.MaterialCount == 0) return;   // final review C: nothing to scan for
        float rangeSq = ItemRules.MaterialPickupRange * ItemRules.MaterialPickupRange;
        foreach (var player in _players)
        {
            if (!player.Alive || !ActionsAllowed(player.State.Mode)) continue;
            for (int i = _worldItems.Count - 1; i >= 0; i--)
            {
                WorldItemData item = _worldItems[i].Data;
                if (item.Kind != ItemKind.Material) continue;
                Vector3 d = item.Position - player.State.Position;
                if (d.X * d.X + d.Z * d.Z > rangeSq || d.Y > ItemRules.PickupHeight || d.Y < -ItemRules.PickupHeight) continue;
                var material = (BuildMaterialType)(item.DefId - 1);
                int take = Math.Min(item.Amount, Math.Max(0, _building.MaxResource - player.Inventory.Resource(material)));
                if (take == 0) continue;
                ItemRules.AddStack(player.Inventory, ItemKind.Material, item.DefId, take);
                if (take == item.Amount) RemoveItemAt(i);
                else SetItemAmount(i, (ushort)(item.Amount - take));
            }
        }
    }

    // 기능: 범위 안 가장 가까운 아이템을 줍는다(서버가 고른다). Phase 14 D9: 카드는 같은 팀 것만 대상이고 카드 칸으로 간다.
    // 입력: player - 줍는 사람.
    // 출력: 반환값 없음. PickupResult가 간다.
    // D8, D9: the server picks the nearest item in range itself; the client never names one, so it cannot
    // reach for a far item. Items are processed in player order within a tick, so when two players reach
    // for the same item the first one takes it and the second finds it gone.
    private void Pickup(PlayerEntity player)
    {
        // Phase 14 D9: only the player's own team's reboot cards are candidates.
        int index = _worldItems.FindNearest(player.State.Position, ItemRules.PickupRange, ItemRules.PickupHeight, player.TeamId);
        if (index < 0)
        {
            SendPickupResult(player, PickupResultCode.NothingInRange, 0);
            return;
        }

        WorldItemData item = _worldItems[index].Data;
        if (item.Kind == ItemKind.RebootCard)
        {
            SendPickupResult(player, PickupCard(player, index), item.ItemId);
            return;
        }
        if (item.Kind == ItemKind.Weapon)
        {
            bool taken = PickupWeapon(player, index, item);
            SendPickupResult(player, taken ? PickupResultCode.Ok : PickupResultCode.Full, item.ItemId);
            return;
        }

        int take = Math.Min(item.Amount, ItemRules.Room(player.Inventory, _items, item.Kind, item.DefId));
        if (take == 0)
        {
            SendPickupResult(player, PickupResultCode.Full, item.ItemId);
            return;
        }
        ItemRules.AddStack(player.Inventory, item.Kind, item.DefId, take);
        player.Inventory.Changed = true;
        // D9: what does not fit stays on the ground, with the smaller amount.
        if (take == item.Amount) RemoveItemAt(index);
        else SetItemAmount(index, (ushort)(item.Amount - take));
        SendPickupResult(player, PickupResultCode.Ok, item.ItemId);
    }

    // D9: the first empty slot, or, with all three full, the current slot; the weapon it held goes on the
    // ground in front of the player (like a G-drop). Empty-handed (current slot empty), the player also takes it in hand.
    // Returns false when nothing changed hands (see the swap below).
    private bool PickupWeapon(PlayerEntity player, int index, in WorldItemData item)
    {
        Inventory inventory = player.Inventory;
        int spawnPoint = _worldItems[index].SpawnPoint;
        _weapons.TryGetById(item.DefId, out WeaponDefinition weapon);   // items are made from this catalog
        var picked = new HeldWeapon
        {
            Weapon = weapon,
            Rarity = item.Rarity,
            MagAmmo = Math.Min(item.Amount, (int)weapon.MagazineSize),
            NextFireTick = inventory.DroppedFireLockTick,
        };
        RemoveItemAt(index);

        int slot = -1;
        for (int i = 0; i < Inventory.SlotCount && slot < 0; i++)
        {
            if (inventory.Slots[i].IsEmpty) slot = i;
        }

        if (slot >= 0)
        {
            inventory.Slots[slot] = picked;
            if (inventory.Current.IsEmpty)
            {
                inventory.CurrentSlot = slot;
                player.Reloading = false;
            }
        }
        else
        {
            // The ground weapon was removed first, so its record is free for the old one: spawning first
            // could evict the very item being picked up (D13). The old weapon leaves the hand only once it
            // lies in the world, so no item is lost (conservation).
            HeldWeapon old = inventory.Current;
            // It is placed like a G-drop (in front of the player), not on the loot point: the point rolls a new
            // item there after its respawn delay and the two would overlap.
            Vector3 dropOffset = ItemRules.Offset(player.State.Yaw, ItemRules.DropDistance);
            Vector3 dropAt = DropAt(player, dropOffset);
            if (SpawnItem(new LootRoll(ItemKind.Weapon, old.Weapon!.Id, old.Rarity, (ushort)old.MagAmmo), dropAt, -1) == 0)
            {
                // Impossible: RemoveItemAt above just freed a record, so the store is below Capacity and
                // TryAdd cannot fail. Kept so a future change to the store cannot lose an item silently:
                // put the ground weapon back as it was and keep the old one in hand.
                SpawnItem(new LootRoll(ItemKind.Weapon, item.DefId, item.Rarity, item.Amount), item.Position, spawnPoint);
                return false;
            }
            inventory.DroppedFireLockTick = Math.Max(inventory.DroppedFireLockTick, old.NextFireTick);
            picked.NextFireTick = inventory.DroppedFireLockTick;
            inventory.Current = picked;
            player.Reloading = false;   // the reload belonged to the weapon that left the hand
        }
        inventory.Changed = true;
        return true;
    }

    // D12: G drops the current weapon 1 m in front of the feet, magazine included.
    private void DropCurrentWeapon(PlayerEntity player)
    {
        Inventory inventory = player.Inventory;
        ref HeldWeapon held = ref inventory.Current;
        if (held.IsEmpty) return;

        var roll = new LootRoll(ItemKind.Weapon, held.Weapon!.Id, held.Rarity, (ushort)held.MagAmmo);
        Vector3 offset = ItemRules.Offset(player.State.Yaw, ItemRules.DropDistance);
        // Conservation: the weapon leaves the hand only once it lies in the world. SpawnItem touches the
        // world list only, so the ref into the inventory stays valid.
        if (SpawnItem(roll, DropAt(player, offset), -1) == 0) return;

        inventory.DroppedFireLockTick = Math.Max(inventory.DroppedFireLockTick, held.NextFireTick);
        held = default;
        player.Reloading = false;
        inventory.Changed = true;
    }

    // 기능: 죽은(나간) 플레이어의 소지품을 몸 둘레에 모두 떨어뜨린다(Phase 17: 다섯 탄 종류와 수류탄 포함).
    // 입력: player - 대상.
    // 출력: 반환값 없음. 인벤토리가 비워진다(월드가 받지 못한 것은 남는다).
    // D12 (request §21): everything the player carried goes on a circle around the body, one item per
    // weapon, per ammo type and per consumable type, so they do not lie on one point. Then the inventory
    // is empty. Dropped items are not respawn points and can be evicted when the world is full (D13).
    // Conservation: each piece leaves the inventory only once it lies in the world. A piece the world
    // refused (unreachable: at most Capacity / 2 records are spawn-point items, so a full store always has
    // a drop to evict) stays with the body until the respawn resets the inventory.
    // Kill has already cancelled the reload, so taking the reserve here cannot feed a reload (WeaponRules).
    private void DropEverything(PlayerEntity player)
    {
        Inventory inventory = player.Inventory;
        int count = 0;
        for (int i = 0; i < Inventory.SlotCount; i++) if (!inventory.Slots[i].IsEmpty) count++;
        for (int t = 1; t <= ItemConstants.AmmoTypeCount; t++) if (inventory.GetAmmo((AmmoType)t) > 0) count++;
        if (inventory.Medkits > 0) count++;
        if (inventory.ShieldCells > 0) count++;
        if (inventory.Grenades > 0) count++;   // Phase 17 D9
        for (int m = 0; m < BuildMaterials.Count; m++) if (inventory.Resource((BuildMaterialType)m) > 0) count++;   // Phase 13 D15

        int n = 0;
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            ref HeldWeapon held = ref inventory.Slots[i];
            if (held.IsEmpty) continue;
            if (DropAround(player, n++, count, new LootRoll(ItemKind.Weapon, held.Weapon!.Id, held.Rarity, (ushort)held.MagAmmo)))
                held = default;
        }
        for (int t = 1; t <= ItemConstants.AmmoTypeCount; t++)
        {
            int rounds = inventory.GetAmmo((AmmoType)t);
            if (rounds > 0 && DropAround(player, n++, count, new LootRoll(ItemKind.Ammo, (byte)t, 0, (ushort)rounds)))
                inventory.SetAmmo((AmmoType)t, 0);
        }
        if (inventory.Medkits > 0 &&
            DropAround(player, n++, count, new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Medkit, 0, (ushort)inventory.Medkits)))
            inventory.Medkits = 0;
        if (inventory.ShieldCells > 0 &&
            DropAround(player, n++, count, new LootRoll(ItemKind.Consumable, (byte)ConsumableType.ShieldCell, 0, (ushort)inventory.ShieldCells)))
            inventory.ShieldCells = 0;
        if (inventory.Grenades > 0 &&
            DropAround(player, n++, count, new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Grenade, 0, (ushort)inventory.Grenades)))
            inventory.Grenades = 0;
        // Phase 13 D15: the building resources too, one item per material (DefId = material + 1).
        for (int m = 0; m < BuildMaterials.Count; m++)
        {
            var material = (BuildMaterialType)m;
            int amount = Math.Min(inventory.Resource(material), ushort.MaxValue);
            if (amount > 0 && DropAround(player, n++, count, new LootRoll(ItemKind.Material, (byte)(m + 1), 0, (ushort)amount)))
                inventory.SetResource(material, 0);
        }

        // The rest of Inventory.Clear: with every piece placed, the inventory is exactly a cleared one.
        inventory.CurrentSlot = 0;
        inventory.DroppedFireLockTick = 0;
        inventory.Changed = true;
    }

    // Where an item dropped at this offset from the player lies (final review B10: on a ramp or roof under it too).
    private Vector3 DropAt(PlayerEntity player, Vector3 offset)
    {
        CollisionWorld world = GatherAround(player.State.Position);
        return ItemRules.DropPosition(player.State.Position, offset, world.Boxes, GameMap.Terrain, world.Slopes);
    }

    // Returns false when the world could not take the item.
    private bool DropAround(PlayerEntity player, int n, int count, in LootRoll roll)
    {
        Vector3 offset = ItemRules.Offset(player.State.Yaw + 360f * n / count, ItemRules.DeathDropRadius);
        return SpawnItem(roll, DropAt(player, offset), -1) != 0;
    }

    // End of tick (D11): the match state when any of its fields changed (state, timer, alive and player counts,
    // round), the zone when its phase changed. Each is one small Reliable packet to everyone.
    private void SendMatchChanges()
    {
        if (_flow.DevRespawn) return;
        MatchState match = _flow.ToWire(_players.Count);
        if (!match.SameAs(_sentMatchState))
        {
            _sentMatchState = match;
            foreach (var p in _players) SendMatchState(p.PeerId, match);
        }
        ZoneState zone = _zone.ToWire();
        if (!zone.SameAs(_sentZoneState))
        {
            _sentZoneState = zone;
            foreach (var p in _players) SendZoneState(p.PeerId, zone);
        }
    }

    // Phase 12 D9: at the end of a tick in which a door changed, the doors to everyone (one small packet however many
    // changed).
    private void SendDoorChanges()
    {
        if (_doors.OpenMask == _sentDoors) return;
        _sentDoors = _doors.OpenMask;
        foreach (var p in _players) SendDoors(p.PeerId);
    }

    private void SendDoors(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        DoorStatesPacket.Write(ref writer, _doors.OpenMask);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Phase 13 D6: at the end of a tick in which a harvestable was destroyed (or all stood up again at a reset), the
    // destroyed set to everyone: one small packet however many changed.
    private void SendHarvestChanges()
    {
        if (_harvest.DestroyedMask == _sentHarvest) return;
        _sentHarvest = _harvest.DestroyedMask;
        foreach (var p in _players) SendHarvestStates(p.PeerId);
    }

    private void SendHarvestStates(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        HarvestStatesPacket.Write(ref writer, _harvest.DestroyedMask);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Phase 13 D15: each owner's resources, only at the end of a tick in which they changed.
    private void SendResourceChanges()
    {
        foreach (var p in _players)
        {
            if (p.Inventory.ResourcesChanged) SendResources(p);
        }
    }

    private void SendResources(PlayerEntity player)
    {
        player.Inventory.ResourcesChanged = false;
        var writer = new PacketWriter(_sendBuffer);
        ResourcesState.Write(ref writer, player.Inventory.ResourcesToWire());
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendMatchState(int peerId, in MatchState state)
    {
        var writer = new PacketWriter(_sendBuffer);
        MatchState.Write(ref writer, state);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendZoneState(int peerId, in ZoneState zone)
    {
        var writer = new PacketWriter(_sendBuffer);
        ZoneState.Write(ref writer, zone);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Phase 12 D5: the route, to one player (a newcomer or a resumed player during the match) or to everyone (the start).
    private void SendRoute(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        TransportRoutePacket.Write(ref writer, _route);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendDied(int peerId, in PlayerDied died)
    {
        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, died);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 참가자에게 경기 결과를 보낸다. Phase 14 D6: Participants는 팀 수, Placement는 팀 배치(Solo는 지금과 같다).
    // 입력: player - 받는 참가자.
    // 출력: 반환값 없음.
    private void SendMatchResult(PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
        MatchResult.Write(ref writer, new MatchResult
        {
            WinnerId = WinnerId,
            Placement = player.Placement,
            Kills = (byte)Math.Min(player.Kills, byte.MaxValue),
            // Phase 14 D6: teams (Solo: one per participant, as before); Placement is the team's.
            Participants = (byte)_flow.Teams,
        });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendPickupResult(PlayerEntity player, PickupResultCode result, ushort itemId)
    {
        var writer = new PacketWriter(_sendBuffer);
        PickupResult.Write(ref writer, new PickupResult { Result = result, ItemId = itemId });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 탈락은 여기서 정해진다(D9). DamageTaken·PlayerDied·PlayerRespawned는 같은 ReliableOrdered 채널이라 모든 Client가 그 순서로 본다.
    //   killer null = 자기장(Phase 5 D8)이나 낙하(Phase 12 D10, cause): KillerId 0, 처치 없음. 경기 중 탈락은 영구다.
    //   Phase 14 D6: 같은 팀에 서 있는 다른 구성원이 없으면 팀이 전멸한다(배치 = 남은 팀 수, 기절한 팀원도 같은 Tick에 탈락).
    //   팀이 살아 있으면 이 사람만 탈락하고 Reboot 카드를 떨어뜨리며, PlayerDied.Placement는 그 순간 남은 팀 수(자기 팀 포함, 잠정,
    //   항상 1 이상, 리더 결정: 0은 늦은 관전자 안내에만 쓴다)다. 최종 배치는 팀 전멸·경기 종료 때 정해지고 MatchResult로 간다.
    //   Solo는 언제나 팀 전멸이므로 지금과 같다.
    // 입력: victim - 탈락자(살아 있음, 기절 포함), killer - 처치자(null = 없음), cause - 처치자가 없을 때의 원인.
    // 출력: 반환값 없음.
    private void Kill(PlayerEntity victim, PlayerEntity? killer, DeathCause cause = DeathCause.Zone)
    {
        byte team = victim.TeamId;
        bool squad = _flow.InMatch && victim.Participant && team != 0 && !_teamOut[team];
        if (squad && !HasUpMember(team, victim))
        {
            WipeTeam(team, victim, killer, cause, announce: true);
            return;
        }
        EliminateOne(victim, killer, cause, squad ? (byte)_flow.TeamsAlive : (byte)0, announce: true, teamSurvives: squad);
    }

    private void Respawn(PlayerEntity player) => Respawn(player, SpawnPosition(player.EntityId));

    // Phase 12: mode is the movement mode the player starts in (Transport aboard the drop transport).
    private void Respawn(PlayerEntity player, Vector3 position, MovementMode mode = MovementMode.Ground)
    {
        // Keep the yaw so the camera does not snap; everything else starts over at the given position (rested).
        player.State = new MoveState { Position = position, Yaw = player.State.Yaw, Mode = mode };
        player.Sprinting = false;
        ResetCombat(player);
        player.History.Reset(ServerTick, player.State.Position, mode);
        // The missed-input repeat starts over too: a late input right after the respawn must not replay a
        // move from the old life. Seq stays (the client's seq continues across the respawn), and so does
        // LastProcessedSeq.
        player.LastInput = new InputCommand { Seq = player.LastInput.Seq, Yaw = player.State.Yaw };
        player.MissedTicks = 0;

        var writer = new PacketWriter(_sendBuffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned
        {
            EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw, Mode = mode,
        });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: Starting -> Playing 시작 리셋(D3). Phase 14: 분대 상태를 지우고 참가자를 입장 순서로 팀에 묶는다(D1). Phase 15: 지도 표시를 지우고
    //   새 팀마다 Tick 끝에 빈 TeamMarkers를 보낸다. Phase 16: Container를 따로 둔 시드로 굴리고 Supply Drop 목록을 비운다. Phase 17: 투사체를 지운다.
    // 입력: now - 마지막 Tick.
    // 출력: 반환값 없음. 경기 세계가 새로 시작된다.
    // D3: Starting -> Playing, in this one tick: everyone to a drop point (Phase 6 D9), empty-handed with Health 100 and
    // Shield 0 (the loadout), the world cleared and filled with this match's loot, the participants fixed, the
    // zone started. Respawn keeps Seq, so clients re-sync their prediction exactly as after a death.
    // Phase 12 D4, D5: with AirDrop everyone starts aboard the drop transport instead: the route (rolled with this
    // round's spawn seed, starting at the tick being simulated) goes out first, so the same ordered channel delivers it
    // before the PlayerRespawned events in Transport mode; and the zone's clock starts when the route ends.
    private void StartMatch(uint now)
    {
        ClearWorldItems();
        ClearProjectiles();                  // Phase 17 D6: nothing of the lobby flies into the match
        ClearMarkers();                      // Phase 15: before the teams are made again
        ResetSquadState(keepTeams: false);   // Phase 14: no channel, knock-down or station cooldown survives into the match
        _hasRoute = _airDrop;
        if (_hasRoute)
        {
            _route = DropPlanner.Plan(unchecked(_spawnSeed + _flow.Round), now + 1, _simHz);
            foreach (var player in _players) SendRoute(player.PeerId);
        }
        // Phase 6 D9: everyone to a drop point, in an order shuffled by this round's seed.
        ShuffleDropOrder(unchecked(_spawnSeed + _flow.Round));
        int dropIndex = 0;
        foreach (var player in _players)
        {
            if (_hasRoute) Respawn(player, _route.PositionAt(_route.StartTick), MovementMode.Transport);
            else Respawn(player, DropSpot(dropIndex++));
            player.Participant = true;
            player.Placement = 0;
            player.Kills = 0;
            player.DamageDealt = 0;
            player.EliminatedTick = 0;
        }
        // Phase 14 D1: the participants (everyone here, in join order) form the teams, at least two.
        AssignTeams();
        MarkAllTeamsDirty();   // Phase 15: every new team starts from an empty TeamMarkers
        _leftParticipants.Clear();
        // Phase 12 D9: every door closed (everyone is aboard or on a drop point, clear of every box); the change goes out
        // at the end of this tick.
        _doors.CloseAll();
        // Phase 13 D6 (request §24): every harvestable stands again (the change goes out at the end of this tick).
        _harvest.Reset();
        // Phase 13 D10: no piece of the lobby survives into the match.
        ClearBuilds();
        _matchStartedUtc = DateTime.UtcNow;
        _loot.Restart(unchecked(_lootSeed + _flow.Round));
        for (int point = 0; point < _loot.Count; point++) SpawnItem(_loot.Roll(point), _loot.Position(point), point);
        _zone.Start(_hasRoute ? _route.EndTick : now, unchecked(_zoneSeed + _flow.Round));
        // Phase 16 D2, D6: the containers from their own seed stream (the floor loot above is unchanged), no supply drop yet,
        // the schedule on the zone clock.
        StartLoot(now, _hasRoute ? _route.EndTick : now);
        if (_zone.IsFinalPhase) _flow.EnterFinalPhase();   // a one-phase zone is final from the start
        _matchStartTick = now;
        WinnerId = 0;
    }

    // 기능: Finished -> Closing -> 다음 라운드 리셋(D13). Phase 14: 팀과 분대 상태를 지운다(대기실에는 팀이 없다). Phase 15: 남은 지도 표시를
    //   팀이 지워지기 전에 지우고 알린다. Phase 16: Container 마스크와 Supply Drop 목록을 지운다. Phase 17: 투사체를 지운다.
    // 입력: now - 마지막 Tick.
    // 출력: 반환값 없음.
    // D13: Finished -> Closing -> the next round, in this one tick: everyone alive on the spawn ring with an
    // empty inventory, no items in the world (no loot before a match, D2), no zone.
    private void CloseRound(uint now)
    {
        // Phase 10 D2: the grace ends with the round. Before Reopen counts the players for the next countdown.
        while (_graced.Count > 0) ExpireGraced(_graced[0]);
        ClearWorldItems();
        ClearProjectiles();   // Phase 17 D6
        _hasRoute = false;
        // Phase 13 D6, D10: the lobby gets the whole map back, without the match's pieces.
        _harvest.Reset();
        ClearBuilds();
        ClearMarkersAtRoundReset();          // Phase 15: while the teams still exist
        ResetLoot();                         // Phase 16 D3: no container or supply drop in the lobby
        ResetSquadState(keepTeams: false);   // Phase 14: the lobby has no teams
        foreach (var player in _players)
        {
            Respawn(player);
            player.Participant = false;
            player.Placement = 0;
        }
        _zone.Reset();
        _flow.Reopen(now, _players.Count);
    }

    // 기능: 월드 아이템을 모두 지우고 알린다(Phase 14: 카드는 그 팀에게만).
    // 입력: 없음.
    // 출력: 반환값 없음.
    // Removes every world item, telling every client (Phase 14: a card only its team). Spawn-point timers are not
    // involved (the spawner restarts).
    private void ClearWorldItems()
    {
        while (_worldItems.Count > 0)
        {
            int last = _worldItems.Count - 1;
            ushort itemId = _worldItems[last].Data.ItemId;
            byte team = _worldItems[last].CardTeam;
            _worldItems.RemoveAt(last);
            BroadcastItemRemoved(itemId, team);
        }
    }

    private void ResetCombat(PlayerEntity player)
    {
        player.Alive = true;
        player.Health = CombatRules.MaxHealth;
        player.Shield = _loadout.Shield;
        _loadout.ApplyTo(player.Inventory, _weapons);
        WeaponRules.ResetState(player);
    }

    private void Broadcast(ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        foreach (var p in _players) _send(p.PeerId, data, method);
    }

    // Phase 8 D3: the snapshot is split into packets of at most MaxEntitiesPerSnapshotPacket players. Each packet is one
    // payload for everyone; AckInputSeq and the self block differ per recipient and are patched in place.
    private void SendSnapshots()
    {
        int total = _players.Count;
        if (total == 0) return;
        int perPart = ProtocolConstants.MaxEntitiesPerSnapshotPacket;
        int parts = (total + perPart - 1) / perPart;

        for (int part = 0; part < parts; part++)
        {
            int first = part * perPart;
            int count = Math.Min(perPart, total - first);
            var writer = new PacketWriter(_sendBuffer);
            WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader
            {
                ServerTick = ServerTick, AckInputSeq = 0, Count = (ushort)count, Part = (byte)part, PartCount = (byte)parts,
            });
            for (int i = first; i < first + count; i++)
            {
                PlayerEntity p = _players[i];
                SnapshotEntity.Write(ref writer, new SnapshotEntity
                {
                    EntityId = p.EntityId,
                    Position = p.State.Position,
                    VelocityY = p.State.VelocityY,
                    Yaw = p.State.Yaw,
                    // Phase 12 D11: alive, the mode, sprinting and exhausted in the one flag byte. Phase 13 D5: and the tool.
                    Flags = SnapshotEntity.MakeFlags(p.Alive, p.State.Mode, p.Sprinting, p.State.Exhausted, p.Inventory.Tool),
                });
            }
            // Cannot overflow: 27 + 13 * 90 = 1197 bytes, and ServerOptions.Validate caps MaxPlayers at MaxSnapshotEntities.
            if (writer.Overflowed) return;

            Span<byte> packet = _sendBuffer.AsSpan(0, writer.Length);
            foreach (var p in _players)
            {
                WorldSnapshotHeader.PatchRecipient(packet, p.LastProcessedSeq, SelfBlock(p));
                _send(p.PeerId, packet, DeliveryMethod.Sequenced);
            }
        }
    }

    private SnapshotSelf SelfBlock(PlayerEntity p)
    {
        // While a reload runs the remaining time is reported as at least 1, even on its last tick: 0 means
        // "not reloading" to the client, and reporting 0 early would make it re-sync at every reload end.
        ushort reloadRemaining = 0;
        if (p.Reloading)
            reloadRemaining = (ushort)Math.Clamp(p.ReloadEndTick > ServerTick ? p.ReloadEndTick - ServerTick : 1u, 1u, ushort.MaxValue);

        return new SnapshotSelf
        {
            Health = (byte)Math.Clamp(p.Health, 0, byte.MaxValue),
            Shield = (byte)Math.Clamp(p.Shield, 0, byte.MaxValue),
            WeaponSlot = (byte)p.Inventory.CurrentSlot,
            Tool = p.Inventory.Tool,                      // Phase 13 D5
            Ammo = (byte)p.Inventory.Current.MagAmmo,   // 0 for an empty slot
            ReloadRemainingTicks = reloadRemaining,
            // Phase 12 D11: what the owner's prediction needs beyond the entity.
            Energy = (ushort)(MoveState.MaxEnergyHundredths - p.State.EnergySpent),
            HorizontalVelocity = p.State.HorizontalVelocity,
            ModeTicks = p.State.ModeTicks,
            EnergyDelayTicks = p.State.EnergyDelayTicks,
        };
    }

    private void SendJoinResponse(int peerId, JoinResult result, ushort entityId)
    {
        var writer = new PacketWriter(_sendBuffer);
        JoinMatchResponse.Write(ref writer, new JoinMatchResponse
        {
            Result = result,
            MyEntityId = entityId,
            ServerTick = ServerTick,
            SimHz = _simHz,
            SnapshotHz = _snapshotHz,
        });
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 무기 카탈로그(Phase 17: 투사체 목록 포함)와 아이템 카탈로그를 한 연결에 보낸다(입장·Resume).
    // 입력: peerId - 받는 연결.
    // 출력: 반환값 없음. WeaponCatalog·ItemCatalog가 전송된다.
    private void SendCatalogs(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos, _weapons.WireProjectiles);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        writer = new PacketWriter(_sendBuffer);
        ItemCatalogPacket.Write(ref writer, _items.Wire);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D14: the owner's inventory, only at the end of a tick in which it changed (shots do not count).
    private void SendInventoryChanges()
    {
        foreach (var p in _players)
        {
            if (p.Inventory.Changed) SendInventory(p);
        }
    }

    private void SendInventory(PlayerEntity player)
    {
        player.Inventory.Changed = false;
        var writer = new PacketWriter(_sendBuffer);
        InventoryState.Write(ref writer, player.Inventory.ToWire(ServerTick));
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 월드 아이템 목록 전체를 WorldItemsPacket.MaxItems 단위로 보낸다(D13, D14, 최대 256개 = 6 패킷). Phase 14 D9: 다른 팀의
    //   카드는 빼고 보낸다.
    // 입력: recipient - 받는 사람(입장·Resume).
    // 출력: 반환값 없음.
    private void SendWorldItems(PlayerEntity recipient)
    {
        int visible = 0;
        for (int i = 0; i < _worldItems.Count; i++)
        {
            ref readonly WorldItem item = ref _worldItems[i];
            if (item.Data.Kind == ItemKind.RebootCard && (recipient.TeamId == 0 || item.CardTeam != recipient.TeamId)) continue;
            _visibleItems[visible++] = i;
        }
        for (int start = 0; start < visible; start += WorldItemsPacket.MaxItems)
        {
            int count = Math.Min(WorldItemsPacket.MaxItems, visible - start);
            var writer = new PacketWriter(_sendBuffer);
            WorldItemsPacket.WriteHeader(ref writer, count);
            for (int i = 0; i < count; i++) WorldItemData.Write(ref writer, _worldItems[_visibleItems[start + i]].Data);
            _send(recipient.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }
    }

    // 기능: 아이템을 월드에 넣고 모두에게 알린다(밀려난 드롭이 있으면 그 제거도). 카드는 SpawnCard가 만든다(Phase 14).
    // 입력: roll - 아이템, position - 위치, spawnPoint - Loot Point(-1 = 드롭).
    // 출력: 새 아이템 id, 넣지 못했으면 0.
    // Every change to the world item list goes through these three, so each one reaches every client.
    // Each finishes its send before returning: none of them keeps a PacketWriter on _sendBuffer open
    // across another send. Returns the new id, or 0 when the store could not take the item.
    internal ushort SpawnItem(in LootRoll roll, Vector3 position, int spawnPoint)
    {
        if (!_worldItems.TryAdd(roll.Kind, roll.DefId, roll.Rarity, roll.Amount, position, spawnPoint, out ushort itemId, out ushort evictedId))
            return 0;
        if (evictedId != 0) BroadcastItemRemoved(evictedId, 0);   // never a card (cards are not evicted)

        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[_worldItems.Count - 1].Data);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        return itemId;
    }

    // 기능: index의 아이템을 지우고 알린다(Phase 14: 카드는 그 팀에게만). Loot Point 아이템이면 그 점의 타이머를 시작한다.
    // 입력: index - 아이템 index.
    // 출력: 반환값 없음.
    internal void RemoveItemAt(int index)
    {
        ushort itemId = _worldItems[index].Data.ItemId;
        int spawnPoint = _worldItems[index].SpawnPoint;
        byte team = _worldItems[index].CardTeam;
        _worldItems.RemoveAt(index);
        BroadcastItemRemoved(itemId, team);
        if (spawnPoint >= 0) _loot.OnTaken(spawnPoint, ServerTick);
    }

    // D7: a looted spawn point gets a new roll from its table once its timer is up. A point never holds two
    // items (the death-drop guarantee relies on at most one live item per point): if one is in the world
    // (a partial-pickup remainder, or the swap rollback that puts the item back), the timer is dropped.
    private void RefillLootPoints(uint now)
    {
        for (int point = 0; point < _loot.Count; point++)
        {
            if (!_loot.IsDue(point, now)) continue;
            if (PointHasItem(point) || SpawnItem(_loot.Roll(point), _loot.Position(point), point) != 0) _loot.OnRefilled(point);
        }
    }

    private bool PointHasItem(int point)
    {
        for (int i = 0; i < _worldItems.Count; i++)
            if (_worldItems[i].SpawnPoint == point) return true;
        return false;
    }

    // 기능: 일부만 주운 아이템의 남은 양을 바꾸고 알린다(ItemSpawned upsert, Phase 14: 카드면 그 팀에게만).
    // 입력: index - 아이템 index, amount - 남은 양.
    // 출력: 반환값 없음.
    // Partial pickup (D9): the rest stays where it was. Sent as ItemSpawned, which clients treat as an upsert.
    internal void SetItemAmount(int index, ushort amount)
    {
        _worldItems.SetAmount(index, amount);
        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[index].Data);
        BroadcastTeam(writer.WrittenSpan, _worldItems[index].CardTeam);
    }

    // 기능: 아이템 제거를 알린다.
    // 입력: itemId - 지운 아이템 id, team - 카드면 그 팀(그 팀만 안다), 아니면 0(모두).
    // 출력: 반환값 없음.
    private void BroadcastItemRemoved(ushort itemId, byte team)
    {
        var writer = new PacketWriter(_sendBuffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = itemId });
        BroadcastTeam(writer.WrittenSpan, team);
    }

    private void SendSpawned(int recipientPeerId, PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
        // Phase 11 D9: the name is the DevPlayerId the connect request already validated (ProtocolConstants.IsValidPlayerName).
        PlayerSpawned.Write(ref writer, new PlayerSpawned
        {
            EntityId = player.EntityId, Position = player.State.Position, Yaw = player.State.Yaw, Name = player.DevPlayerId,
        });
        // Should be unreachable after the connect check. A spawn without its name would be refused by every client (the
        // player invisible but able to shoot), so nothing is sent; GameLoop logs the first failure.
        if (writer.Overflowed)
        {
            SpawnEncodeFailures++;
            return;
        }
        _send(recipientPeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // Phase 10 D2: a graced player leaves when its grace is over, and also once it is dead (killed while away: its
    // placement is fixed and there is nothing left to resume; coming back is a new spectator, like any late join).
    private void ExpireGrace(uint now)
    {
        for (int i = _graced.Count - 1; i >= 0; i--)
        {
            PlayerEntity player = _graced[i];
            if (player.Alive && now < player.GraceEndTick) continue;
            ExpireGraced(player);
        }
    }

    // Every way a graced player leaves without resuming goes through here, so GameLoop counts each one (D9).
    private void ExpireGraced(PlayerEntity player)
    {
        RemovePlayer(player);
        _graceExpired?.Invoke(player.DevPlayerId);
    }

    // D2: the oldest graced, living player with this DevPlayerId, unless a connected player already uses the id (then
    // the newcomer joins as a new player and takes nothing over).
    private PlayerEntity? FindGraced(string devPlayerId)
    {
        if (_graced.Count == 0) return null;
        foreach (var player in _playersByPeer.Values)
        {
            if (player.DevPlayerId == devPlayerId) return null;
        }
        foreach (var player in _graced)
        {
            if (player.Alive && player.DevPlayerId == devPlayerId) return player;
        }
        return null;
    }

    // 기능: 유예 중인 캐릭터를 새 연결로 넘기고 입장 패킷 묶음을 다시 보낸다(Phase 16: Container·Supply Drop 상태, Phase 17: 살아 있는 투사체 포함). Phase 14 D13: 끝에
    //   팀 상태·스테이션·진행 중인 팀 채널도. Phase 15 D10: 그리고 팀 지도 표시.
    // 입력: peerId - 새 연결 id, player - 유예 중인 플레이어.
    // 출력: 반환값 없음.
    // D2: the character goes to the new connection with everything it has (position, health, inventory, placement
    // state). The new client numbers its inputs from 1, so the input state starts over. It gets what a late joiner
    // gets; the others never saw it leave, so they are told nothing.
    private void Resume(int peerId, PlayerEntity player)
    {
        _graced.Remove(player);
        player.PeerId = peerId;
        _playersByPeer.Add(peerId, player);
        player.Inputs.Reset();
        player.LastProcessedSeq = 0;
        player.LastInput = new InputCommand { Yaw = player.State.Yaw };
        player.MissedTicks = 0;
        player.FireHeld = false;
        // Phase 13 D8: the new client numbers its build requests from 1.
        player.BuildQueue.Clear();
        player.HasBuildSequence = false;

        SendJoinResponse(peerId, JoinResult.Resumed, player.EntityId);
        SendCatalogs(peerId);
        SendWorldItems(player);
        SendInventory(player);
        foreach (var other in _players) SendSpawned(peerId, other);
        if (!_flow.DevRespawn)
        {
            SendMatchState(peerId, _flow.ToWire(_players.Count));
            SendZoneState(peerId, _zone.ToWire());
        }
        // Phase 12 D16: the route, so a rider (or a jumper) predicts from it; the mode comes with the next snapshot.
        if (_hasRoute) SendRoute(peerId);
        SendDoors(peerId);
        SendHarvestStates(peerId);   // Phase 13 D6
        SendContainerStates(peerId); // Phase 16 D3
        SendSupplyDrops(peerId);     // Phase 16 D7
        SendProjectilesTo(peerId);   // Phase 17 D7
        SendResources(player);       // Phase 13 D15
        SendBuildCatalog(peerId);    // Phase 13 D4, final review A3
        StartBuildSync(player);      // Phase 13 D14: the client's old pieces are not trusted
        // The match ended while it was away: FinishMatch sent its result to no connection, so it gets it now.
        if (_flow.State == MatchFlowState.Finished && player.Participant) SendMatchResult(player);
        SendSquadStateTo(player);   // Phase 14 D13: the team, the stations and the team's channels in progress
        SendMarkersTo(player);      // Phase 15 D10
    }

    // Phase 13 D4: the BuildCatalog packet's content, built once.
    // infiniteResources: placement costs nothing (TryBuild), so the client is told cost 0; with the real cost its preview
    // would judge NoResource and never send a request.
    private static BuildCatalogData BuildCatalogWire(BuildingCatalog c, bool infiniteResources)
    {
        var data = new BuildCatalogData
        {
            MaxResource = (ushort)c.MaxResource,
            BuildRange = c.BuildRange,
            ViewAngleDegrees = c.ViewAngleDegrees,
            HarvestRange = c.HarvestRange,
            HarvestCooldownTicks = c.HarvestCooldownTicks,
            MinBuildIntervalTicks = c.MinBuildIntervalTicks,
            InterestCellSize = c.InterestCellSize,
            InterestRadius = (byte)c.InterestRadius,
            InterestKeepMargin = (byte)c.InterestKeepMargin,
        };
        for (int m = 0; m < BuildMaterials.Count; m++)
        {
            BuildMaterialConfig material = c.Material((BuildMaterialType)m);
            data.ResourceCost[m] = infiniteResources ? (ushort)0 : (ushort)material.ResourceCost;
            data.MaxHealth[m] = (ushort)material.MaxHealth;
            data.InitialHealth[m] = (ushort)material.InitialHealth;
            data.ConstructionTicks[m] = material.ConstructionTicks;
        }
        return data;
    }

    // Entity ids are ushort and 0 means "none". With at most 50 players a free id is always found.
    private ushort AllocateEntityId()
    {
        while (_nextEntityId == 0 || IsEntityIdInUse(_nextEntityId)) _nextEntityId++;
        return _nextEntityId++;
    }

    private bool IsEntityIdInUse(ushort id)
    {
        foreach (var p in _players)
        {
            if (p.EntityId == id) return true;
        }
        return false;
    }

    // 기능: 끝난 경기의 기록을 만든다(남은 참가자 + 나간 참가자, Phase 14: 나간 사람의 배치는 팀 배치로 고쳐진 값).
    // 입력: now - 마지막 Tick, winner - 우승 플레이어(null = 경기에 없음).
    // 출력: MatchRecord.
    // Phase 9 D4: the record of the match that just finished: every participant still here plus those who left. Built
    // once per match on the game loop thread (the only allocation of the finish tick); the sink must not block.
    private MatchRecord BuildRecord(uint now, PlayerEntity? winner)
    {
        var players = new List<PlayerRecord>(_players.Count + _leftParticipants.Count);
        foreach (var player in _players)
        {
            if (player.Participant) players.Add(RecordOf(player, now));
        }
        foreach (LeftParticipant left in _leftParticipants) players.Add(left.Record);
        // A winner who left (the last two left before this tick, D9) gets no MatchResult, but the record keeps its win.
        string? winnerId = winner?.DevPlayerId;
        if (winnerId == null)
        {
            foreach (LeftParticipant left in _leftParticipants)
            {
                if (left.Record.Placement == 1) winnerId = left.Record.DevPlayerId;
            }
        }

        return new MatchRecord(_flow.Round, _matchStartedUtc, DateTime.UtcNow, winnerId, players);
    }

    // Survival runs from the match start to the elimination, or to `now` for a player still in.
    private PlayerRecord RecordOf(PlayerEntity player, uint now)
    {
        uint end = player.EliminatedTick != 0 ? player.EliminatedTick : now;
        int survivalMs = (int)((ulong)(end - _matchStartTick) * 1000UL / (ulong)_simHz);
        return new PlayerRecord(player.DevPlayerId, player.Placement, player.Kills, player.DamageDealt, survivalMs);
    }

    // Phase 6 D9: resets the order to 0..n-1 and shuffles it (Fisher-Yates), so the result depends on the seed only.
    // One Random per match start, never per tick.
    private void ShuffleDropOrder(int seed)
    {
        for (int i = 0; i < _dropOrder.Length; i++) _dropOrder[i] = i;
        var rng = new Random(seed);
        for (int i = _dropOrder.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_dropOrder[i], _dropOrder[j]) = (_dropOrder[j], _dropOrder[i]);
        }
    }

    // Where the k-th participant (0-based, player list order) starts: drop point order[k % n]. Lap k / n > 0 moves it
    // DropLapOffset times ((lap + 1) / 2) east on odd laps and west on even laps, onto the terrain there (spec
    // interpretation 6).
    private Vector3 DropSpot(int k)
    {
        int n = _dropPoints.Length;
        Vector3 spot = _dropPoints[_dropOrder[k % n]];
        int lap = k / n;
        if (lap > 0)
        {
            float shift = DropLapOffset * ((lap + 1) / 2);
            spot.X += lap % 2 == 1 ? shift : -shift;
            spot.Y = GameMap.Terrain.Height(spot.X, spot.Z);
        }
        return spot;
    }

    // Spread players on a circle (golden angle) so they do not spawn inside each other.
    // The 5 m ring lies inside the plaza, GameMap.PlazaRadius (checked by GameMapTests).
    internal static Vector3 SpawnPosition(ushort entityId)
    {
        float angle = entityId * 2.39996f;
        return new Vector3(MathF.Cos(angle) * SpawnRadius, 0f, MathF.Sin(angle) * SpawnRadius);
    }
}
