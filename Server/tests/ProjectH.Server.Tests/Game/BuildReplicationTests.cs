using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Client.Game;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 13 D13, D14 (request §178): what each client is told of the building, in the dev sandbox. A mirror of one client
// applies the packets in order (as BuildStore does), so the tests compare what a client would hold with the server.
// Interest cells are 20 m (8 x 8); the plaza is interest cell (4, 4) = 36, build cells 16..19. The client's real store
// (BuildStore, linked from the client) is fed the same packets as NetClient feeds it and must hold the same pieces.
public class BuildReplicationTests
{
    private readonly List<(int Peer, byte[] Data, bool BuildChannel)> _sent = new();
    private readonly Match _match;

    // 기능: 일반 채널과 건설 채널로 보낸 패킷을 _sent에 모으는 8인 개발 모드 Match를 만든다.
    // 입력: 없음.
    // 출력: 참가자 없이 시작한 Match를 든 테스트 인스턴스.
    public BuildReplicationTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 8, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => _sent.Add((peer, data.ToArray(), false)), TestGameData.CombatLoadout,
            sendBuild: (peer, data, _) => _sent.Add((peer, data.ToArray(), true)));
    }

    // 기능: 플레이어를 경기에 들여보내고 주어진 자리에 세운다(이동 이력도 그 자리로 맞춘다).
    // 입력: peer - 연결 id, feet - 발 위치.
    // 출력: 들어온 플레이어. 입장이 거절되면 테스트가 실패한다.
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
        public int EditedRecords;

        // 기능: 건설 채널 패킷 하나를 BuildStore가 하듯 거울에 적용한다(Sync·Events·Interest, 그 밖은 무시).
        // 입력: data - 패킷 id를 포함한 바이트열.
        // 출력: 반환값 없음. Pieces·Cells·Version과 계수기가 갱신된다. 읽기에 실패하거나 버전이 줄면 테스트가 실패한다.
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
                    Assert.True(BuildEventsPacket.TryReadHeader(ref r, out version, out int placed, out int edited, out int health, out int destroyed));
                    Assert.True(version >= Version);
                    Version = version;
                    for (int i = 0; i < placed; i++)
                    {
                        Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out BuildPieceRecord p));
                        Pieces[p.Id] = p;
                    }
                    for (int i = 0; i < edited; i++)
                    {
                        Assert.True(BuildEventsPacket.TryReadEdited(ref r, out uint eid, out ushort state));
                        EditedRecords++;
                        if (Pieces.TryGetValue(eid, out var piece))
                        {
                            Assert.True(BuildEdit.TryApply(piece.Shape, state, out BuildPieceShape shape));
                            Pieces[eid] = piece with { Shape = shape };
                        }
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

    // 기능: 조각이 속한 관심 셀 번호를 구한다(건설 셀 4개 = 관심 셀 1개, 8 x 8).
    // 입력: s - 조각 모양.
    // 출력: 관심 셀 번호 0..63.
    private static int Cell(BuildPieceShape s) => s.X / 4 + 8 * (s.Z / 4);

    // 기능: 한 Client가 건설 채널로 받은 패킷을 순서대로 Client의 실제 BuildStore에 적용한다(NetClient와 같은 방식).
    // 입력: peer - 받는 연결 id.
    // 출력: 그 패킷을 모두 적용한 새 BuildStore.
    // What NetClient does with the building stream, into the client's real store.
    private BuildStore StoreOf(int peer)
    {
        var store = new BuildStore();
        foreach (var s in _sent.Where(s => s.Peer == peer && s.BuildChannel)) ApplyToStore(store, s.Data);
        return store;
    }

    // 기능: 건설 채널 패킷 하나를 Client BuildStore에 적용한다(Sync·Events·Interest, 그 밖은 무시).
    // 입력: store - 적용할 저장소, data - 패킷 id를 포함한 바이트열.
    // 출력: 반환값 없음. 저장소의 조각·관심 창이 갱신된다. 읽기에 실패하면 테스트가 실패한다.
    private static void ApplyToStore(BuildStore store, byte[] data)
    {
        var r = new PacketReader(data);
        Assert.True(r.TryReadPacketId(out PacketId id));
        switch (id)
        {
            case PacketId.BuildSync:
                Assert.True(BuildSyncPacket.TryReadHeader(ref r, out uint version, out bool reset, out int count));
                if (reset) store.Reset();
                for (int i = 0; i < count; i++)
                {
                    Assert.True(BuildPieceRecord.TryReadSync(ref r, out BuildPieceRecord p));
                    store.ApplyPiece(p, version);
                }
                break;
            case PacketId.BuildEvents:
                Assert.True(BuildEventsPacket.TryReadHeader(ref r, out version, out int placed, out int edited, out int health, out int destroyed));
                for (int i = 0; i < placed; i++)
                {
                    Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out BuildPieceRecord p));
                    store.ApplyPiece(p, version);
                }
                for (int i = 0; i < edited; i++)
                {
                    Assert.True(BuildEventsPacket.TryReadEdited(ref r, out uint eid, out ushort state));
                    Assert.True(store.ApplyEdited(eid, state, version));
                }
                for (int i = 0; i < health; i++)
                {
                    Assert.True(BuildEventsPacket.TryReadHealth(ref r, out uint hid, out ushort damage));
                    store.ApplyHealth(hid, damage, version);
                }
                for (int i = 0; i < destroyed; i++)
                {
                    Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint did));
                    store.ApplyDestroyed(did, version);
                }
                break;
            case PacketId.BuildInterest:
                Assert.True(BuildInterestPacket.TryRead(ref r, out ulong cells));
                store.ApplyInterest(cells);
                break;
        }
    }

    // 기능: 한 Client가 건설 채널로 받은 패킷을 순서대로 적용한 거울을 만든다.
    // 입력: peer - 받는 연결 id.
    // 출력: 그 Client가 들고 있을 조각·관심 창·버전을 담은 Mirror.
    private Mirror MirrorOf(int peer)
    {
        var m = new Mirror();
        foreach (var s in _sent.Where(s => s.Peer == peer && s.BuildChannel)) m.Apply(s.Data);
        return m;
    }

    // 기능: 서버 격자에서 주어진 관심 셀 창 안의 조각을 모은다.
    // 입력: cells - 관심 셀 비트 집합.
    // 출력: 조각 id → 모양.
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

    // 기능: 한 Client의 거울과 실제 BuildStore가 서버의 관심 창·조각·모양·피해와 같은지 확인한다.
    // 입력: peer - 확인할 연결 id.
    // 출력: 반환값 없음. 창이나 조각이 하나라도 다르면 테스트가 실패한다.
    private void AssertMirrorMatches(int peer)
    {
        Mirror m = MirrorOf(peer);
        _match.TryGetPlayer(peer, out var p);
        Assert.Equal(p.InterestCells, m.Cells);
        Dictionary<uint, BuildPieceShape> server = ServerPieces(m.Cells);
        Assert.Equal(server.Keys.OrderBy(k => k), m.Pieces.Keys.OrderBy(k => k));
        foreach (var kv in server) Assert.Equal(kv.Value, m.Pieces[kv.Key].Shape);
        // The client's real store holds the same window and pieces (and its collision grid the same count).
        BuildStore store = StoreOf(peer);
        Assert.Equal(m.Cells, store.Cells);
        Assert.Equal(server.Count, store.Count);
        Assert.Equal(server.Count, store.Grid.Count);
        foreach (var kv in server)
        {
            // The confirmed record: what the server's bytes said (not a local edit prediction).
            Assert.True(store.TryGetConfirmed(kv.Key, out BuildPieceRecord piece));
            Assert.Equal(kv.Value, piece.Shape);
            Assert.Equal(m.Pieces[kv.Key].Damage, piece.Damage);
        }
    }

    // 기능: 서버 경기에 조각 하나를 바로 세운다(요청·검증 없이).
    // 입력: x·y·z - 격자 칸, type - 조각 종류(기본 바닥), rotation - 회전(기본 0).
    // 출력: 세운 조각의 id.
    private uint Add(int x, int y, int z, BuildPieceType type = BuildPieceType.Floor, int rotation = 0) =>
        SandboxHarness.AddPiece(_match, new BuildPieceShape(type, x, y, z, rotation));

    // Phase 13.5 D8: an edit reaches every client as one Edited record (the mirror and the client's real store hold the
    // edited shape), and a client joining later gets the final state in its sync (the grid word's bits 20-31).
    [Fact]
    public void AnEdit_ReachesTheStore_AndALateJoinerSyncsTheFinalState()
    {
        PlayerEntity p = Join(1, new Vector3(2.5f, 0f, -3f));
        uint wall = SandboxHarness.AddPiece(_match, new BuildPieceShape(BuildPieceType.Wall, 16, 0, 16, 0), owner: p.EntityId);
        uint floor = SandboxHarness.AddPiece(_match, new BuildPieceShape(BuildPieceType.Floor, 17, 1, 16, 0), owner: p.EntityId);
        _match.Tick();
        // The default aim (yaw 0, pitch 0) looks north at the wall.
        const int door = (1 << 1) | (1 << 4);
        _match.EnqueueEdit(1, new BuildEditRequest { Sequence = 1, PieceId = wall, State = BuildEdit.PackState(door, 0) });
        _match.Tick();
        Assert.Equal(door, _match.Build.At(_match.Build.Grid.SlotOf(wall)).Shape.Edit);
        AssertMirrorMatches(1);
        Assert.Equal(1, MirrorOf(1).EditedRecords);
        Assert.True(StoreOf(1).TryGetConfirmed(wall, out BuildPieceRecord stored));
        Assert.Equal(door, stored.Shape.Edit);

        // Several changes of one piece in one tick: one record, the last state.
        int slot = _match.Build.Grid.SlotOf(floor);
        BuildPieceShape f = _match.Build.At(slot).Shape;
        _match.Build.SetShape(slot, f.WithEdit(0b0001, 0));
        _match.Replication.Edited(slot);
        _match.Build.SetShape(slot, f.WithEdit(0b0110, 0));
        _match.Replication.Edited(slot);
        _match.Tick();
        Assert.Equal(2, MirrorOf(1).EditedRecords);
        AssertMirrorMatches(1);

        Join(2, new Vector3(-3f, 0f, -3f));
        for (int i = 0; i < 5; i++) _match.Tick();
        AssertMirrorMatches(2);
        Assert.Equal(0, MirrorOf(2).EditedRecords);
        Assert.Equal(door, MirrorOf(2).Pieces[wall].Shape.Edit);
        Assert.Equal(0b0110, MirrorOf(2).Pieces[floor].Shape.Edit);
    }

    // The client files a piece under the same interest cell as the server, and sees the same slots taken.
    [Fact]
    public void TheClientsStore_UsesTheServersInterestCells_AndOccupiedRule()
    {
        var store = new BuildStore();
        for (int z = 0; z < BuildGrid.CellsZ; z++)
            for (int x = 0; x < BuildGrid.CellsX; x++)
                Assert.Equal(_match.Replication.InterestCell(x, z), store.CellOf(new BuildPieceShape(BuildPieceType.Floor, x, 0, z, 0)));

        Add(17, 1, 17);                                     // a floor on level 1
        Add(18, 0, 17, BuildPieceType.Roof);                // a roof on level 0 (its slab is level 1's floor)
        Add(17, 0, 17, BuildPieceType.Wall);
        Add(16, 0, 16, BuildPieceType.Ramp, 1);
        Join(1, new Vector3(2f, 0f, 2f));
        _match.Tick();
        BuildStore client = StoreOf(1);
        Assert.Equal(4, client.Count);
        for (int type = 0; type < 4; type++)
            for (int y = 0; y < 3; y++)
                for (int z = 15; z < 20; z++)
                    for (int x = 15; x < 20; x++)
                        for (int rotation = 0; rotation < 4; rotation++)
                        {
                            if (!BuildGrid.TryNormalize((BuildPieceType)type, x, y, z, rotation, out BuildPieceShape shape)) continue;
                            Assert.Equal(BuildRules.Occupied(_match.Build, shape), client.Occupied(shape));
                        }
    }

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
        // Every building packet went on the building channel, the catalog first (final review A3).
        Assert.DoesNotContain(_sent, s => !s.BuildChannel && (PacketId)s.Data[0] is PacketId.BuildSync or PacketId.BuildEvents or PacketId.BuildInterest
            or PacketId.BuildCatalog);
        Assert.Equal(PacketId.BuildCatalog, (PacketId)_sent.First(s => s.Peer == 1 && s.BuildChannel).Data[0]);
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

    // Final review B8: a player whose connection dropped keeps no waiting build request through its grace.
    [Fact]
    public void AGracedPlayer_HasNoWaitingBuildRequests()
    {
        var h = new RoyaleHarness(reconnectGraceSeconds: 10);
        PlayerEntity a = h.Join(1);
        h.Join(2);
        h.RunToMatch();
        h.Match.EnqueueBuild(1, new BuildRequest { Sequence = 1, Piece = (byte)BuildPieceType.Wall, X = 16, Y = 0, Z = 16 });
        Assert.Equal(1, a.BuildQueue.Count);
        Assert.True(h.Match.Disconnect(1, allowGrace: true));
        Assert.Equal(0, a.BuildQueue.Count);
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

    // ---- Final review A1: nearest cells first ----

    // Over 4 x 74 pieces in two cells of lower index than the player's own (interest cells 18 and 19, build cells x 8..15,
    // z 8..11): sent lowest index first they would fill the whole first tick. The player's cell comes first.
    [Fact]
    public void TheSync_SendsThePlayersOwnCellFirst()
    {
        for (int level = 0; level < 10; level++)
            for (int z = 8; z < 12; z++)
                for (int x = 8; x < 16; x++) Add(x, level, z);
        uint own = Add(16, 0, 16);
        Join(1, new Vector3(2f, 0f, 2f));
        Assert.Equal(36, _match.Replication.InterestCellAt(new Vector3(2f, 0f, 2f)));
        _match.Tick();
        Assert.Equal(1 + BuildReplication.MaxSyncPacketsPerTick, _sent.Count(s => s.Peer == 1 && (PacketId)s.Data[0] == PacketId.BuildSync));
        Assert.Contains(own, MirrorOf(1).Pieces.Keys);
        for (int i = 0; i < 5; i++) _match.Tick();
        AssertMirrorMatches(1);
    }

    [Fact]
    public void NearestPending_IsTheClosestCell_TheLowestIndexAmongEquals()
    {
        BuildReplication r = _match.Replication;
        Assert.Equal(36, r.NearestPending(ulong.MaxValue, 36));
        Assert.Equal(27, r.NearestPending((1UL << 0) | (1UL << 27) | (1UL << 45), 36));   // 27 and 45 both 1 away
        Assert.Equal(0, r.NearestPending(1UL << 0, 63));
        Assert.Equal(-1, r.NearestPending(0, 36));
    }

    // ---- Final review A4: a backed-up building channel pauses the sync, nothing else ----

    [Fact]
    public void ABackedUpBuildChannel_PausesTheSync_ButEventsAndTheWindowStillGo()
    {
        var sent = new List<(int Peer, byte[] Data)>();
        int backlog = Match.MaxBuildBacklog + 1;
        var match = new Match(new ServerOptions { MaxPlayers = 8, DevRespawn = true }, TestGameData.Create(), (_, _, _) => { },
            TestGameData.CombatLoadout, sendBuild: (peer, data, _) => sent.Add((peer, data.ToArray())), buildBacklog: _ => backlog);
        for (int x = 12; x < 20; x++) SandboxHarness.AddPiece(match, new BuildPieceShape(BuildPieceType.Floor, x, 0, 16, 0));
        Assert.Equal(JoinResult.Ok, match.TryJoin(1, "p1"));
        match.TryGetPlayer(1, out PlayerEntity p);
        p.State.Position = new Vector3(2f, 0f, 2f);
        match.Tick();
        int Count(PacketId id) => sent.Count(s => (PacketId)s.Data[0] == id);
        Assert.Equal(1, Count(PacketId.BuildSync));       // the reset only
        Assert.Equal(1, Count(PacketId.BuildInterest));   // the window goes
        uint placed = SandboxHarness.AddPiece(match, new BuildPieceShape(BuildPieceType.Wall, 16, 1, 16, 0));
        match.Replication.Placed(BuildReplication.Record(match.Build.At(match.Build.Grid.SlotOf(placed))));
        match.Tick();
        Assert.Equal(1, Count(PacketId.BuildEvents));     // events go
        Assert.Equal(1, Count(PacketId.BuildSync));
        Assert.Equal(2, match.SyncsDeferred);
        backlog = Match.MaxBuildBacklog;                  // not above the limit: the sync goes on
        match.Tick();
        Assert.Equal(2, Count(PacketId.BuildSync));
        Assert.Equal(2, match.BuildCounts().SyncDeferred);
    }

    // ---- Final review C: windows that change during a sync ----

    [Fact]
    public void LeavingACellMidSync_AndComingBack_EndsWithTheSamePieces()
    {
        int added = 0;
        for (int level = 0; level < 5 && added < 600; level++)
            for (int z = 12; z < 24 && added < 600; z++)
                for (int x = 12; x < 24 && added < 600; x++, added++) Add(x, level, z);
        PlayerEntity p = Join(1, new Vector3(2f, 0f, 2f));
        _match.Tick();
        Assert.True(p.SyncPending != 0 || p.SyncCell >= 0);   // still syncing
        p.State.Position = new Vector3(-70f, GameMap.Terrain.Height(-70f, -70f), -70f);   // cell (0, 0)
        _match.Tick();
        AssertMirrorMatches(1);
        p.State.Position = new Vector3(2f, 0f, 2f);
        for (int i = 0; i < 8; i++) _match.Tick();
        Assert.Equal(0UL, p.SyncPending);
        AssertMirrorMatches(1);
        Assert.Equal(600, MirrorOf(1).Pieces.Count);
    }

    [Fact]
    public void ADeadPlayerComingBackToLife_ShrinksItsWindow_AndDropsTheFarPieces()
    {
        uint far = Add(1, 0, 1);
        uint near = Add(16, 0, 16);
        PlayerEntity p = Join(1, new Vector3(2f, 0f, 2f));
        p.Alive = false;
        p.RespawnAtTick = uint.MaxValue;
        _match.Tick();
        Assert.Equal(ulong.MaxValue, MirrorOf(1).Cells);
        Assert.Contains(far, MirrorOf(1).Pieces.Keys);
        p.Alive = true;
        _match.Tick();
        Mirror m = MirrorOf(1);
        Assert.NotEqual(ulong.MaxValue, m.Cells);
        Assert.DoesNotContain(far, m.Pieces.Keys);
        Assert.Contains(near, m.Pieces.Keys);
        AssertMirrorMatches(1);
    }
}
