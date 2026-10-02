using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D13, D14 (request §178): what each client is told of the building, in the dev sandbox. A mirror of one client
// applies the packets in order (as BuildStore does), so the tests compare what a client would hold with the server.
// Interest cells are 20 m (8 x 8); the plaza is interest cell (4, 4) = 36, build cells 16..19.
public class BuildReplicationTests
{
    private readonly List<(int Peer, byte[] Data, bool BuildChannel)> _sent = new();
    private readonly Match _match;

    public BuildReplicationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 8, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => _sent.Add((peer, data.ToArray(), false)), TestGameData.CombatLoadout,
            sendBuild: (peer, data, _) => _sent.Add((peer, data.ToArray(), true)));
    }

    private PlayerEntity Join(int peer, Vector3 feet)
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(peer, "p" + peer));
        _match.TryGetPlayer(peer, out var p);
        p.State.Position = feet;
        p.History.Reset(_match.ServerTick, feet);
        return p;
    }

    private sealed class Mirror
    {
        public readonly Dictionary<uint, BuildPieceRecord> Pieces = new();
        public ulong Cells;
        public uint Version;
        public int SyncPackets;
        public int Resets;

        public void Apply(byte[] data)
        {
            var r = new PacketReader(data);
            Assert.True(r.TryReadPacketId(out PacketId id));
            switch (id)
            {
                case PacketId.BuildSync:
                    Assert.True(BuildSyncPacket.TryReadHeader(ref r, out uint version, out bool reset, out int count));
                    Assert.True(data.Length <= ProtocolConstants.MaxPacketSize);
                    Version = version;
                    if (reset)
                    {
                        Pieces.Clear();
                        Resets++;
                    }
                    else SyncPackets++;
                    for (int i = 0; i < count; i++)
                    {
                        Assert.True(BuildPieceRecord.TryReadSync(ref r, out BuildPieceRecord p));
                        Pieces[p.Id] = p;
                    }
                    break;
                case PacketId.BuildEvents:
                    Assert.True(BuildEventsPacket.TryReadHeader(ref r, out version, out int placed, out int health, out int destroyed));
                    Assert.True(version >= Version);
                    Version = version;
                    for (int i = 0; i < placed; i++)
                    {
                        Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out BuildPieceRecord p));
                        Pieces[p.Id] = p;
                    }
                    for (int i = 0; i < health; i++)
                    {
                        Assert.True(BuildEventsPacket.TryReadHealth(ref r, out uint hid, out ushort damage));
                        if (Pieces.TryGetValue(hid, out var piece)) Pieces[hid] = piece with { Damage = damage };
                    }
                    for (int i = 0; i < destroyed; i++)
                    {
                        Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint did));
                        Pieces.Remove(did);
                    }
                    break;
                case PacketId.BuildInterest:
                    Assert.True(BuildInterestPacket.TryRead(ref r, out ulong cells));
                    Cells = cells;
                    foreach (uint gone in Pieces.Where(kv => (cells & (1UL << Cell(kv.Value.Shape))) == 0).Select(kv => kv.Key).ToList())
                        Pieces.Remove(gone);
                    break;
            }
        }
    }

    private static int Cell(BuildPieceShape s) => s.X / 4 + 8 * (s.Z / 4);

    private Mirror MirrorOf(int peer)
    {
        var m = new Mirror();
        foreach (var s in _sent.Where(s => s.Peer == peer && s.BuildChannel)) m.Apply(s.Data);
        return m;
    }

    // The server's pieces in a window of cells.
    private Dictionary<uint, BuildPieceShape> ServerPieces(ulong cells)
    {
        var result = new Dictionary<uint, BuildPieceShape>();
        PieceGrid grid = _match.Build.Grid;
        for (int z = 0; z < BuildGrid.CellsZ; z++)
            for (int x = 0; x < BuildGrid.CellsX; x++)
                for (int slot = grid.First(x, z); slot >= 0; slot = grid.Next(slot))
                {
                    if ((cells & (1UL << Cell(grid.ShapeAt(slot)))) != 0) result[grid.IdAt(slot)] = grid.ShapeAt(slot);
                }
        return result;
    }

    private void AssertMirrorMatches(int peer)
    {
        Mirror m = MirrorOf(peer);
        _match.TryGetPlayer(peer, out var p);
        Assert.Equal(p.InterestCells, m.Cells);
        Dictionary<uint, BuildPieceShape> server = ServerPieces(m.Cells);
        Assert.Equal(server.Keys.OrderBy(k => k), m.Pieces.Keys.OrderBy(k => k));
        foreach (var kv in server) Assert.Equal(kv.Value, m.Pieces[kv.Key].Shape);
    }

    private uint Add(int x, int y, int z, BuildPieceType type = BuildPieceType.Floor, int rotation = 0) =>
        SandboxHarness.AddPiece(_match, new BuildPieceShape(type, x, y, z, rotation));

    [Fact]
    public void TheInterestGrid_Is8By8_AndAWindowIs5By5()
    {
        BuildReplication r = _match.Replication;
        Assert.Equal(8, r.InterestPerSide);
        Assert.Equal(36, r.InterestCellAt(new Vector3(2f, 0f, 2f)));
        Assert.Equal(0, r.InterestCellAt(new Vector3(-200f, 0f, -200f)));
        Assert.Equal(63, r.InterestCellAt(new Vector3(79.9f, 0f, 79.9f)));
        Assert.Equal(25, System.Numerics.BitOperations.PopCount(r.Window(36, 2)));
        Assert.Equal(9, System.Numerics.BitOperations.PopCount(r.Window(0, 2)));   // a corner
        Assert.Equal(ulong.MaxValue, r.AllCells);
    }

    [Fact]
    public void AJoin_ResetsTheClient_ThenSyncsItsWindow()
    {
        uint near = Add(16, 0, 16);
        uint far = Add(1, 0, 1);    // interest cell 0, far from the plaza
        Join(1, new Vector3(2f, 0f, 2f));
        _match.Tick();
        Mirror m = MirrorOf(1);
        Assert.Equal(1, m.Resets);
        Assert.Contains(near, m.Pieces.Keys);
        Assert.DoesNotContain(far, m.Pieces.Keys);
        AssertMirrorMatches(1);
        // Every building packet went on the building channel; the catalog on channel 0.
        Assert.DoesNotContain(_sent, s => !s.BuildChannel && (PacketId)s.Data[0] is PacketId.BuildSync or PacketId.BuildEvents or PacketId.BuildInterest);
        Assert.Contains(_sent, s => !s.BuildChannel && (PacketId)s.Data[0] == PacketId.BuildCatalog);
    }

    [Fact]
    public void Events_GoOnlyToClientsWhoseWindowHasThem_AndADeadPlayerWatchesEverything()
    {
        Join(1, new Vector3(2f, 0f, 2f));
        PlayerEntity dead = Join(2, new Vector3(2f, 0f, 2f));
        dead.Alive = false;
        dead.RespawnAtTick = uint.MaxValue;
        _match.Tick();
        uint far = Add(1, 0, 1);
        _match.Replication.Placed(BuildReplication.Record(_match.Build.At(_match.Build.Grid.SlotOf(far))));
        uint near = Add(16, 0, 16);
        _match.Replication.Placed(BuildReplication.Record(_match.Build.At(_match.Build.Grid.SlotOf(near))));
        _match.Tick();
        Mirror alive = MirrorOf(1);
        Mirror watcher = MirrorOf(2);
        Assert.Contains(near, alive.Pieces.Keys);
        Assert.DoesNotContain(far, alive.Pieces.Keys);
        Assert.Contains(far, watcher.Pieces.Keys);
        Assert.Equal(ulong.MaxValue, watcher.Cells);
        AssertMirrorMatches(1);
        AssertMirrorMatches(2);
    }

    [Fact]
    public void WalkingIntoACell_SyncsIt_AndWalkingFarAway_DropsIt()
    {
        PlayerEntity p = Join(1, new Vector3(2f, 0f, 2f));
        uint far = Add(1, 0, 1);    // interest cell 0
        _match.Tick();
        Assert.DoesNotContain(far, MirrorOf(1).Pieces.Keys);
        // Interest cell (1, 1) is 2 cells from cell 0: (−60..−40, −60..−40).
        p.State.Position = new Vector3(-50f, GameMap.Terrain.Height(-50f, -50f), -50f);
        _match.Tick();
        Assert.Contains(far, MirrorOf(1).Pieces.Keys);
        AssertMirrorMatches(1);
        // Back to the plaza: cell 0 is 4 away (beyond 2 + the keep margin 1): dropped.
        p.State.Position = new Vector3(2f, 0f, 2f);
        _match.Tick();
        Assert.DoesNotContain(far, MirrorOf(1).Pieces.Keys);
        AssertMirrorMatches(1);
    }

    [Fact]
    public void ACellJustOutsideTheRadius_IsKept_ThenDroppedFurther()
    {
        PlayerEntity p = Join(1, new Vector3(2f, 0f, 2f));   // cell (4, 4): window x 2..6
        uint west = Add(9, 0, 16);                            // interest cell (2, 4)
        _match.Tick();
        Assert.Contains(west, MirrorOf(1).Pieces.Keys);
        int syncs = MirrorOf(1).SyncPackets;
        p.State.Position = new Vector3(22f, 0f, 2f);         // cell (5, 4): (2, 4) is 3 away, within 2 + 1
        _match.Tick();
        Assert.Contains(west, MirrorOf(1).Pieces.Keys);
        p.State.Position = new Vector3(2f, 0f, 2f);
        _match.Tick();
        Assert.Equal(syncs, MirrorOf(1).SyncPackets);         // never dropped, so never synced again
        p.State.Position = new Vector3(42f, 0f, 2f);         // cell (6, 4): 4 away
        _match.Tick();
        Assert.DoesNotContain(west, MirrorOf(1).Pieces.Keys);
    }

    [Fact]
    public void ALargeWindow_IsSyncedInChunks_AtMostFourPacketsATick_EveryPieceOnce()
    {
        // 600 pieces: floors on levels 0..4 over the 144 build cells around the plaza (interest cells 3..5 x 3..5).
        int added = 0;
        for (int level = 0; level < 5 && added < 600; level++)
            for (int z = 12; z < 24 && added < 600; z++)
                for (int x = 12; x < 24 && added < 600; x++, added++) Add(x, level, z);
        Join(1, new Vector3(2f, 0f, 2f));
        _match.Tick();
        int perTick = _sent.Count(s => s.Peer == 1 && (PacketId)s.Data[0] == PacketId.BuildSync) - 1;   // minus the reset
        Assert.Equal(BuildReplication.MaxSyncPacketsPerTick, perTick);
        for (int i = 0; i < 5; i++) _match.Tick();
        var counts = new Dictionary<uint, int>();
        foreach (var s in _sent.Where(s => s.Peer == 1 && (PacketId)s.Data[0] == PacketId.BuildSync))
        {
            Assert.True(s.Data.Length <= ProtocolConstants.MaxPacketSize);
            var r = new PacketReader(s.Data);
            r.TryReadPacketId(out _);
            Assert.True(BuildSyncPacket.TryReadHeader(ref r, out _, out _, out int count));
            for (int i = 0; i < count; i++)
            {
                BuildPieceRecord.TryReadSync(ref r, out var p);
                counts[p.Id] = counts.TryGetValue(p.Id, out int n) ? n + 1 : 1;
            }
        }
        Assert.Equal(600, counts.Count);
        Assert.All(counts.Values, n => Assert.Equal(1, n));
        AssertMirrorMatches(1);
    }

    [Fact]
    public void APieceDestroyedDuringTheSync_IsNotSentLater()
    {
        var ids = new List<uint>();
        for (int level = 0; level < 4; level++)
            for (int z = 12; z < 24; z++)
                for (int x = 12; x < 24; x++) ids.Add(Add(x, level, z));
        Join(1, new Vector3(2f, 0f, 2f));
        _match.Tick();
        // 296 went out in the first tick; destroy pieces of the cells not sent yet.
        foreach (uint id in ids.Skip(400)) _match.DestroyPiece(id);
        for (int i = 0; i < 5; i++) _match.Tick();
        AssertMirrorMatches(1);
    }

    [Fact]
    public void AResume_ResetsAndSyncsAgain()
    {
        var h = new RoyaleHarness(reconnectGraceSeconds: 10);
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        uint id = SandboxHarness.AddPiece(h.Match, new BuildPieceShape(BuildPieceType.Floor, 16, 0, 16, 0));
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        h.Ticks(3);
        int before = h.Packets.Count;
        Assert.Equal(JoinResult.Resumed, h.Match.TryJoin(7, "p1"));
        h.Ticks(1);
        var after = h.Packets.Skip(before).Where(s => s.PeerId == 7).ToList();
        var resetPacket = after.First(s => s.Id == PacketId.BuildSync);
        var r = new PacketReader(resetPacket.Data);
        r.TryReadPacketId(out _);
        Assert.True(BuildSyncPacket.TryReadHeader(ref r, out _, out bool reset, out _));
        Assert.True(reset);
        bool synced = false;
        foreach (var s in after.Where(s => s.Id == PacketId.BuildSync))
        {
            r = new PacketReader(s.Data);
            r.TryReadPacketId(out _);
            BuildSyncPacket.TryReadHeader(ref r, out _, out _, out int count);
            for (int i = 0; i < count; i++)
            {
                BuildPieceRecord.TryReadSync(ref r, out var p);
                synced |= p.Id == id;
            }
        }
        Assert.True(synced);
        Assert.True(a.Alive);
    }

    [Fact]
    public void Versions_OnlyGrow()
    {
        Join(1, new Vector3(2f, 0f, 2f));
        uint last = 0;
        for (int i = 0; i < 5; i++)
        {
            uint id = Add(16, i, 16, BuildPieceType.Wall);
            _match.Replication.Placed(BuildReplication.Record(_match.Build.At(_match.Build.Grid.SlotOf(id))));
            _match.Tick();
            Assert.True(_match.Replication.Version > last);
            last = _match.Replication.Version;
        }
        Assert.Equal(last, MirrorOf(1).Version);
    }

    [Fact]
    public void TheCounts_ReportPiecesAndCells()
    {
        Add(16, 0, 16);
        Add(16, 1, 16);
        Add(1, 0, 1);
        var c = _match.BuildCounts();
        Assert.Equal(3, c.Pieces);
        Assert.Equal(2, c.Cells);
    }
}
