using System;
using System.Collections.Generic;
using System.Numerics;
using LiteNetLib;
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
public sealed class Match
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
    private readonly long[] _buildResults = new long[(int)BuildResultCode.BudgetFull + 1];
    private readonly BuildCatalogData _buildCatalogWire;
    // Participants who left during the current match, recorded when they left (they are no longer in _players).
    // At most MaxPlayers entries; cleared when a match starts.
    private readonly List<PlayerRecord> _leftParticipants = new();
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
    public Match(ServerOptions options, GameData data, SendPacket send, StartingLoadout? loadout = null, LootPoint[]? lootPoints = null,
        Vector3[]? dropPoints = null, Action<MatchRecord>? matchSink = null, Action<string>? graceExpired = null,
        Action? movementAnomaly = null)
    {
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
        _graceTicks = (uint)options.ReconnectGraceSeconds * (uint)options.SimHz;
        ArgumentNullException.ThrowIfNull(data);
        if (data.SimHz != options.SimHz)
            throw new ArgumentException($"Game data was built for SimHz {data.SimHz}, the match runs at {options.SimHz}.", nameof(data));
        _weapons = data.Weapons;
        _items = data.Items;
        _building = data.Building;
        _harvest = new HarvestWorld(_building);
        _build = new BuildWorld(_building);
        _replication = new BuildReplication(_build, _building, options.MaxPlayers);
        _buildCatalogWire = BuildCatalogWire(_building);
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
        SendWorldItems(peerId);
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
        SendResources(player);       // Phase 13 D15
        // Phase 12 D16: a newcomer during an air-drop match sees the transport too.
        if (_hasRoute) SendRoute(peerId);
        // Reliable, after its own spawn: the newcomer's client knows it is dead (spectating) before any input.
        // Only to the newcomer; the others see it dead from the snapshot flag.
        if (spectator) SendDied(peerId, new PlayerDied { VictimId = player.EntityId });
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
        _graced.Add(player);
        return true;
    }

    public void Leave(int peerId)
    {
        if (_playersByPeer.Remove(peerId, out var player)) RemovePlayer(player);
    }

    // Leave for a connected or a graced player. Never called inside a loop over _players.
    private void RemovePlayer(PlayerEntity player)
    {
        _players.Remove(player);
        _graced.Remove(player);

        var writer = new PacketWriter(_sendBuffer);
        PlayerDespawned.Write(ref writer, new PlayerDespawned { EntityId = player.EntityId });
        foreach (var other in _players) _send(other.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        // D10: leaving a match is an elimination. What it carried goes to the ground for the others (the death
        // drop, sent to the remaining players only), and it no longer counts as alive. It gets no result.
        if (_flow.InMatch && player.Participant && player.Alive)
        {
            player.Alive = false;
            player.Placement = _flow.Eliminate();
            player.EliminatedTick = ServerTick;
            DropEverything(player);
        }
        // Phase 9: a participant who leaves still belongs to the match record.
        if (_flow.InMatch && player.Participant && _matchSink != null) _leftParticipants.Add(RecordOf(player, ServerTick));
    }

    public void EnqueueInput(int peerId, in PlayerInputPacket packet)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        for (int i = 0; i < packet.Count; i++) player.Inputs.Add(packet.Get(i));
    }

    // Phase 13 D8: a build request from the network (GameLoop.DrainBuild). It waits in the player's queue for the next
    // tick; a full queue refuses it at once (RateLimited), so a flood costs one small answer each and no memory.
    public void EnqueueBuild(int peerId, in BuildRequest request)
    {
        if (!_playersByPeer.TryGetValue(peerId, out var player)) return;
        if (!player.BuildQueue.TryAdd(request)) SendBuildResult(player, request.Sequence, BuildResultCode.RateLimited, 0);
    }

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

        // Phase 13 D8: build requests before the moves and shots, so a wall placed this tick already blocks them (the
        // fastest defence, request §117). Placement uses each player's last input (its aim) and position.
        ProcessBuildRequests(now);

        foreach (var player in _players)
        {
            bool sent = TakeInput(player, out InputCommand input);
            // D9: a dead player's input is still taken and acked (LastProcessedSeq) but moves and fires nothing.
            // A player killed earlier in this loop is already dead here.
            if (!player.Alive) continue;

            // Phase 12 D5: a rider is placed on the route at the tick being simulated (now + 1, the tick its snapshot
            // reports) and may jump. Everyone else steps with the same boxes and terrain as client prediction
            // (LocalPlayerPredictor), so predictions match.
            if (_hasRoute && DropTransport.Ride(ref player.State, input, _route, now + 1)) player.Sprinting = false;
            else if (!Move(player, input)) continue;   // the landing killed it
            WeaponRules.UpdateReload(player, now);
            // Only an input the client really sent can act: the missed-input repeat copies the last input's
            // buttons and must never invent a switch, reload or shot. Phase 12 D12: and only in a mode that allows
            // actions (after this tick's move).
            if (sent && ActionsAllowed(player.State.Mode)) ProcessActions(player, input, now);
            // Gated, the fire button's held state still follows the input: landing with Fire held must not fire a
            // semi-automatic weapon without a new press (the client's WeaponState does the same).
            else if (sent) player.FireHeld = (input.Buttons & InputButtons.Fire) != 0;
            ConsumableRules.Complete(player, _items, now);   // step 9: every tick, input or not
        }

        // Phase 5 step 5: one participant (or none) left ends the match (D9). Deaths of this tick, from the zone
        // and from shots, already have their placements.
        if (_flow.ShouldFinish) FinishMatch(now);

        ServerTick++;
        SendInventoryChanges();
        SendMatchChanges();
        SendDoorChanges();
        SendHarvestChanges();
        SendResourceChanges();
        SendBuildEvents();
        // After every move of this tick, so all players are recorded at the same moment. A snapshot with
        // ServerTick N shows exactly the positions recorded at N, which is what ViewTick refers to.
        foreach (var player in _players) player.History.Record(ServerTick, player.State.Position, player.State.Mode);
        if (ServerTick % (uint)_snapshotEveryTicks == 0) SendSnapshots();
    }

    // One Step and the Phase 12 checks of its result: the movement self-check (D12) and fall damage (D10). Returns false
    // when the landing killed the player.
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
        return !(step.LandingSpeed > 0f && _flow.DamageAllowed && ApplyFallDamage(player, step.LandingSpeed));
    }

    // Phase 13 D3: the colliders a step at these feet may touch: map boxes, closed doors, standing harvestables and the
    // pieces around.
    private CollisionWorld GatherAround(Vector3 feet)
    {
        _collision.Gather(feet, _doors.OpenMask, _harvest.DestroyedMask, _build.Grid);
        return _collision;
    }

    // Phase 13 D8, D9: each player's waiting requests, oldest first. One placement per player per MinBuildInterval: once a
    // piece is placed the rest wait for a later tick (not refused); a refused request does not use the interval. A
    // sequence that is not newer than the last one processed is dropped (a replay or a duplicate, request §149).
    private void ProcessBuildRequests(uint now)
    {
        foreach (var player in _players)
        {
            while (player.BuildQueue.Count > 0 && now >= player.NextBuildTick)
            {
                player.BuildQueue.TryTake(out BuildRequest request);
                if (player.HasBuildSequence && !BuildRequest.IsNewer(request.Sequence, player.LastBuildSequence))
                {
                    BuildDuplicates++;
                    continue;
                }
                player.LastBuildSequence = request.Sequence;
                player.HasBuildSequence = true;
                BuildResultCode code = TryBuild(player, request, now + 1, out uint id);
                _buildResults[(int)code]++;
                SendBuildResult(player, request.Sequence, code, id);
                if (code != BuildResultCode.Ok) continue;
                player.NextBuildTick = now + _building.MinBuildIntervalTicks;
                break;
            }
        }
    }

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

        int cost = _building.Material(material).ResourceCost;
        int have = player.Inventory.Resource(material);
        if (have < cost) return BuildResultCode.NoResource;

        player.Inventory.SetResource(material, have - cost);
        id = _build.Add(shape, material, player.EntityId, tick, grounded: false);
        if (id == 0) return BuildResultCode.BudgetFull;   // unreachable: the budget was checked
        _build.TryGetSlot(id, out int slot);
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

    // D9: a wall or floor through a living character's body centre.
    private bool CutsAPlayer(in BuildPieceShape shape)
    {
        foreach (var p in _players)
        {
            if (p.Alive && BuildRules.HoldsBodyCentre(shape, p.State.Position, MovementSimulation.CollisionHeight(p.State.Mode))) return true;
        }
        return false;
    }

    private static BuildPieceRecord Record(in BuildPiece piece) => new()
    {
        Id = piece.Id, Shape = piece.Shape, Material = piece.Material, Owner = piece.Owner, CreatedTick = piece.CreatedTick, Damage = piece.Damage,
    };

    private void SendBuildResult(PlayerEntity player, ushort sequence, BuildResultCode code, uint id)
    {
        var writer = new PacketWriter(_sendBuffer);
        BuildResult.Write(ref writer, new BuildResult { Sequence = sequence, Code = code, PieceId = id });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D13: this tick's building events to everyone, in as few packets as fit.
    private void SendBuildEvents()
    {
        _replication.Collect();
        if (!_replication.HasEvents) return;
        var cursor = new BuildReplication.Cursor();
        int length;
        while ((length = _replication.NextPacket(_sendBuffer, ref cursor, ulong.MaxValue)) > 0)
            Broadcast(_sendBuffer.AsSpan(0, length), DeliveryMethod.ReliableOrdered);
        _replication.Clear();
    }

    // D10 (request §70, §132): every piece goes at a round reset and a match start; clients clear theirs (a reset sync).
    private void ClearBuilds()
    {
        _build.Clear();
        _replication.Reset();
        foreach (var p in _players)
        {
            p.BuildQueue.Clear();
            p.NextBuildTick = 0;
        }
        var writer = new PacketWriter(_sendBuffer);
        BuildSyncPacket.WriteHeader(ref writer, _replication.Version, reset: true, count: 0);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
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
    private static bool ActionsAllowed(MovementMode mode) =>
        mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;

    // Phase 12 D10: health only (the shield does not stop it, like the zone) and a DamageTaken from nobody; a fatal fall
    // is a death without a killer, cause Fall. Returns true when it killed.
    private bool ApplyFallDamage(PlayerEntity player, float landingSpeed)
    {
        int damage = CombatRules.FallDamage(landingSpeed);
        if (damage <= 0) return false;
        player.Health = Math.Max(0, player.Health - damage);
        var writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken { AttackerId = 0, Damage = (ushort)damage, FromDirection = Vector3.Zero });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        if (player.Health > 0) return false;
        Kill(player, null, DeathCause.Fall);
        return true;
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

    // One real input of a living player, in the spec §2 order: cancel use -> tool -> slot -> drop -> pickup ->
    // reload -> fire -> start use. (Movement came first; finishing a use comes after, every tick.)
    // Phase 13 D5: Fire acts by the tool in hand: a shot (Weapon), a swing (Harvest), nothing (Build: placing is a
    // BuildRequest). Reload is the weapon's only.
    private void ProcessActions(PlayerEntity player, in InputCommand input, uint now)
    {
        ConsumableRules.CancelIfInterrupted(player, input.Buttons);
        HarvestRules.SelectTool(player, input.Buttons);
        WeaponRules.SelectSlot(player, input.Buttons);
        if ((input.Buttons & InputButtons.Drop) != 0) DropCurrentWeapon(player);
        // Phase 12 D9: E acts on a door in front first, an item otherwise.
        if ((input.Buttons & InputButtons.Interact) != 0 && !ToggleDoor(player)) Pickup(player);

        bool aimValid = CombatRules.TryAimDirection(input.AimYaw, input.AimPitch, out Vector3 direction);
        bool fire = (input.Buttons & InputButtons.Fire) != 0;
        switch (player.Inventory.Tool)
        {
            case ToolKind.Weapon:
                if (WeaponRules.Apply(player, input.Buttons, aimValid, now))
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

        ConsumableRules.TryStart(player, _items, input.Buttons, now);
    }

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
        if (target < 0) return;

        HarvestHitResult hit = _harvest.Hit(target, origin + direction * distance, direction);
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
    }

    // D7: from the eye along the aim, the nearest map surface (box, terrain or floor plane) or living player stops the shot. The
    // client only sent a direction; which player is hit is decided here (D12, request §17).
    // D6: other players are tested where the shooter saw them, at ViewTick (clamped to the last
    // _maxRewindTicks ticks). The shooter itself and the arena are not rewound.
    private void FireShot(PlayerEntity shooter, Vector3 direction, float viewTick)
    {
        ref HeldWeapon held = ref shooter.Inventory.Current;
        WeaponDefinition weapon = held.Weapon!;   // Apply only fires a filled slot
        ushort damage = CombatRules.ScaledDamage(weapon.Damage, _items.DamageMultiplier(held.Rarity));
        // Phase 12 D13: crouched or sliding the eye is lower (the client aims from the same height, AimSolver).
        Vector3 origin = shooter.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(shooter.State.Mode), 0f);
        float nearest = HitScan.TraceWorld(origin, direction, weapon.Range, Blockers, GameMap.Terrain);   // a closed door or a tree stops it
        double rewindTick = CombatRules.ClampViewTick(viewTick, ServerTick, _maxRewindTicks);

        PlayerEntity? target = null;
        foreach (var other in _players)
        {
            if (other == shooter || !other.Alive) continue;
            // Phase 12 D13: the hit box has the height of the mode the target was in then; a transport rider is not hit.
            Vector3 feet = other.History.Sample(rewindTick, out MovementMode mode);
            if (mode == MovementMode.Transport) continue;
            if (HitScan.TracePlayer(origin, direction, nearest, feet, MovementSimulation.CollisionHeight(mode), out float distance) &&
                (target == null || distance < nearest))
            {
                nearest = distance;
                target = other;
            }
        }

        var writer = new PacketWriter(_sendBuffer);
        ShotFired.Write(ref writer, new ShotFired { ShooterId = shooter.EntityId, Start = origin, End = origin + direction * nearest });
        Broadcast(writer.WrittenSpan, DeliveryMethod.Unreliable);

        // Phase 5 D2: before (and after) the match a shot still stops at the player it hit (the tracer shows
        // it), but it does no damage and the shooter gets no HitConfirmed.
        if (target != null && _flow.DamageAllowed) ApplyHit(shooter, target, damage);
    }

    private void ApplyHit(PlayerEntity shooter, PlayerEntity target, ushort damage)
    {
        int before = target.Health + target.Shield;
        bool killed = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);
        if (_flow.InMatch && shooter.Participant && shooter != target) shooter.DamageDealt += before - (target.Health + target.Shield);

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
        });
        _send(target.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        if (killed) Kill(target, shooter);
    }

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
            if (player.Health == 0) Kill(player, null);
        }
    }

    // D9: the living participant wins; when the last ones died in the same tick, the one processed last (the
    // only placement 1). A winner who already left is no winner (0).
    private void FinishMatch(uint now)
    {
        _flow.Finish(now);
        PlayerEntity? winner = null;
        foreach (var player in _players)
        {
            if (!player.Participant) continue;
            if (player.Alive) player.Placement = 1;
            if (player.Placement == 1) winner = player;
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

    // Phase 12 D9: E on the door DoorRules picks opens it, or closes it when no living character stands in its place.
    // Returns false when no door is in reach (then E picks up an item).
    private bool ToggleDoor(PlayerEntity player)
    {
        int door = DoorRules.FindTarget(player.State.Position, player.State.Yaw, GameMap.Doors);
        if (door < 0) return false;
        if (!_doors.IsOpen(door)) _doors.Set(door, true);
        else if (!DoorOccupied(door)) _doors.Set(door, false);
        return true;
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

    // D8, D9: the server picks the nearest item in range itself; the client never names one, so it cannot
    // reach for a far item. Items are processed in player order within a tick, so when two players reach
    // for the same item the first one takes it and the second finds it gone.
    private void Pickup(PlayerEntity player)
    {
        int index = _worldItems.FindNearest(player.State.Position, ItemRules.PickupRange, ItemRules.PickupHeight);
        if (index < 0)
        {
            SendPickupResult(player, PickupResultCode.NothingInRange, 0);
            return;
        }

        WorldItemData item = _worldItems[index].Data;
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
            Vector3 dropAt = ItemRules.DropPosition(player.State.Position, dropOffset, GatherAround(player.State.Position).Boxes, GameMap.Terrain);
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
        if (SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, GatherAround(player.State.Position).Boxes, GameMap.Terrain), -1) == 0) return;

        inventory.DroppedFireLockTick = Math.Max(inventory.DroppedFireLockTick, held.NextFireTick);
        held = default;
        player.Reloading = false;
        inventory.Changed = true;
    }

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

        // The rest of Inventory.Clear: with every piece placed, the inventory is exactly a cleared one.
        inventory.CurrentSlot = 0;
        inventory.DroppedFireLockTick = 0;
        inventory.Changed = true;
    }

    // Returns false when the world could not take the item.
    private bool DropAround(PlayerEntity player, int n, int count, in LootRoll roll)
    {
        Vector3 offset = ItemRules.Offset(player.State.Yaw + 360f * n / count, ItemRules.DeathDropRadius);
        return SpawnItem(roll, ItemRules.DropPosition(player.State.Position, offset, GatherAround(player.State.Position).Boxes, GameMap.Terrain), -1) != 0;
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

    private void SendMatchResult(PlayerEntity player)
    {
        var writer = new PacketWriter(_sendBuffer);
        MatchResult.Write(ref writer, new MatchResult
        {
            WinnerId = WinnerId,
            Placement = player.Placement,
            Kills = (byte)Math.Min(player.Kills, byte.MaxValue),
            Participants = (byte)_flow.Participants,
        });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void SendPickupResult(PlayerEntity player, PickupResultCode result, ushort itemId)
    {
        var writer = new PacketWriter(_sendBuffer);
        PickupResult.Write(ref writer, new PickupResult { Result = result, ItemId = itemId });
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // D9: death is decided here. The same ReliableOrdered channel carries DamageTaken, PlayerDied and
    // PlayerRespawned, so every client sees them in that order.
    // killer null = the zone (Phase 5 D8) or a fall (Phase 12 D10, cause): KillerId 0, nobody gets the kill. During a
    // match the death is permanent and takes the next placement (D9); the killer's count rises unless it killed itself (D12).
    private void Kill(PlayerEntity victim, PlayerEntity? killer, DeathCause cause = DeathCause.Zone)
    {
        victim.Alive = false;
        victim.RespawnAtTick = ServerTick + _respawnTicks;
        // The reload dies with the player; otherwise the corpse's snapshots would report it (D10).
        victim.Reloading = false;
        victim.ReloadEndTick = 0;
        // Likewise the heal channel: DropEverything does not call Inventory.Clear, and a late Complete
        // must not heal the corpse or the respawned player.
        ConsumableRules.Cancel(victim.Inventory);

        byte placement = 0;
        if (_flow.InMatch && victim.Participant)
        {
            placement = _flow.Eliminate();
            victim.Placement = placement;
            victim.EliminatedTick = ServerTick;
            if (killer != null && killer != victim) killer.Kills++;
        }

        var writer = new PacketWriter(_sendBuffer);
        PlayerDied.Write(ref writer, new PlayerDied
        {
            VictimId = victim.EntityId, KillerId = killer?.EntityId ?? 0, Placement = placement, Cause = killer == null ? cause : DeathCause.Zone,
        });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        // After PlayerDied, so every client hears of the death before the items appear.
        DropEverything(victim);
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

    // D3: Starting -> Playing, in this one tick: everyone to a drop point (Phase 6 D9), empty-handed with Health 100 and
    // Shield 0 (the loadout), the world cleared and filled with this match's loot, the participants fixed, the
    // zone started. Respawn keeps Seq, so clients re-sync their prediction exactly as after a death.
    // Phase 12 D4, D5: with AirDrop everyone starts aboard the drop transport instead: the route (rolled with this
    // round's spawn seed, starting at the tick being simulated) goes out first, so the same ordered channel delivers it
    // before the PlayerRespawned events in Transport mode; and the zone's clock starts when the route ends.
    private void StartMatch(uint now)
    {
        ClearWorldItems();
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
        if (_zone.IsFinalPhase) _flow.EnterFinalPhase();   // a one-phase zone is final from the start
        _matchStartTick = now;
        WinnerId = 0;
    }

    // D13: Finished -> Closing -> the next round, in this one tick: everyone alive on the spawn ring with an
    // empty inventory, no items in the world (no loot before a match, D2), no zone.
    private void CloseRound(uint now)
    {
        // Phase 10 D2: the grace ends with the round. Before Reopen counts the players for the next countdown.
        while (_graced.Count > 0) ExpireGraced(_graced[0]);
        ClearWorldItems();
        _hasRoute = false;
        // Phase 13 D6, D10: the lobby gets the whole map back, without the match's pieces.
        _harvest.Reset();
        ClearBuilds();
        foreach (var player in _players)
        {
            Respawn(player);
            player.Participant = false;
            player.Placement = 0;
        }
        _zone.Reset();
        _flow.Reopen(now, _players.Count);
    }

    // Removes every world item, telling every client. Spawn-point timers are not involved (the spawner restarts).
    private void ClearWorldItems()
    {
        while (_worldItems.Count > 0)
        {
            int last = _worldItems.Count - 1;
            ushort itemId = _worldItems[last].Data.ItemId;
            _worldItems.RemoveAt(last);
            BroadcastItemRemoved(itemId);
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

    private void SendCatalogs(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        WeaponCatalogPacket.Write(ref writer, _weapons.WireInfos);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        writer = new PacketWriter(_sendBuffer);
        ItemCatalogPacket.Write(ref writer, _items.Wire);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        // Phase 13 D4: what the client needs of the building numbers.
        writer = new PacketWriter(_sendBuffer);
        BuildCatalogPacket.Write(ref writer, _buildCatalogWire);
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

    // D13, D14: the whole list in chunks of WorldItemsPacket.MaxItems (at most 256 items = 6 packets).
    private void SendWorldItems(int peerId)
    {
        for (int start = 0; start < _worldItems.Count; start += WorldItemsPacket.MaxItems)
        {
            int count = Math.Min(WorldItemsPacket.MaxItems, _worldItems.Count - start);
            var writer = new PacketWriter(_sendBuffer);
            WorldItemsPacket.WriteHeader(ref writer, count);
            for (int i = 0; i < count; i++) WorldItemData.Write(ref writer, _worldItems[start + i].Data);
            _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }
    }

    // Every change to the world item list goes through these three, so each one reaches every client.
    // Each finishes its send before returning: none of them keeps a PacketWriter on _sendBuffer open
    // across another send. Returns the new id, or 0 when the store could not take the item.
    internal ushort SpawnItem(in LootRoll roll, Vector3 position, int spawnPoint)
    {
        if (!_worldItems.TryAdd(roll.Kind, roll.DefId, roll.Rarity, roll.Amount, position, spawnPoint, out ushort itemId, out ushort evictedId))
            return 0;
        if (evictedId != 0) BroadcastItemRemoved(evictedId);

        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[_worldItems.Count - 1].Data);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        return itemId;
    }

    internal void RemoveItemAt(int index)
    {
        ushort itemId = _worldItems[index].Data.ItemId;
        int spawnPoint = _worldItems[index].SpawnPoint;
        _worldItems.RemoveAt(index);
        BroadcastItemRemoved(itemId);
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

    // Partial pickup (D9): the rest stays where it was. Sent as ItemSpawned, which clients treat as an upsert.
    internal void SetItemAmount(int index, ushort amount)
    {
        _worldItems.SetAmount(index, amount);
        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[index].Data);
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastItemRemoved(ushort itemId)
    {
        var writer = new PacketWriter(_sendBuffer);
        ItemRemoved.Write(ref writer, new ItemRemoved { ItemId = itemId });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
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
        SendWorldItems(peerId);
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
        SendResources(player);       // Phase 13 D15
        // The match ended while it was away: FinishMatch sent its result to no connection, so it gets it now.
        if (_flow.State == MatchFlowState.Finished && player.Participant) SendMatchResult(player);
    }

    // Phase 13 D4: the BuildCatalog packet's content, built once.
    private static BuildCatalogData BuildCatalogWire(BuildingCatalog c)
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
        for (int m = 0; m < 3; m++)
        {
            BuildMaterialConfig material = c.Material((BuildMaterialType)m);
            data.ResourceCost[m] = (ushort)material.ResourceCost;
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

    // Phase 9 D4: the record of the match that just finished: every participant still here plus those who left. Built
    // once per match on the game loop thread (the only allocation of the finish tick); the sink must not block.
    private MatchRecord BuildRecord(uint now, PlayerEntity? winner)
    {
        var players = new List<PlayerRecord>(_players.Count + _leftParticipants.Count);
        foreach (var player in _players)
        {
            if (player.Participant) players.Add(RecordOf(player, now));
        }
        players.AddRange(_leftParticipants);
        // A winner who left (the last two left before this tick, D9) gets no MatchResult, but the record keeps its win.
        string? winnerId = winner?.DevPlayerId;
        if (winnerId == null)
        {
            foreach (PlayerRecord left in _leftParticipants)
            {
                if (left.Placement == 1) winnerId = left.DevPlayerId;
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
