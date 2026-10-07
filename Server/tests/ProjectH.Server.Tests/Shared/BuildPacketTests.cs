using System;
using System.Collections.Generic;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13 D8, D13, D14, D19: the build packets. Sizes include the packet id (records are counted without it).
public class BuildPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader After(int length, PacketId expected)
    {
        var reader = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static BuildPieceRecord Piece(uint id, BuildPieceType type, int x, int y, int z, int rotation, BuildMaterialType material) =>
        new() { Id = id, Shape = new BuildPieceShape(type, x, y, z, rotation), Material = material, Owner = 77, CreatedTick = 123456, Damage = 40 };

    [Fact]
    public void BuildRequest_Is9Bytes_AndRoundTrips()
    {
        var request = new BuildRequest { Sequence = 65000, Piece = 2, Material = 1, X = 31, Y = 15, Z = 0, Rotation = 3 };
        var writer = new PacketWriter(_buffer);
        BuildRequest.Write(ref writer, request);
        Assert.Equal(9, writer.Length);
        Assert.Equal(BuildRequest.Size, writer.Length);
        var r = After(writer.Length, PacketId.BuildRequest);
        Assert.True(BuildRequest.TryRead(ref r, out BuildRequest back));
        Assert.Equal(request, back);
        // Any byte values parse (the server's rules refuse them); a wrong length does not.
        var shortReader = new PacketReader(new byte[7]);
        Assert.False(BuildRequest.TryRead(ref shortReader, out _));
        var longReader = new PacketReader(new byte[9]);
        Assert.False(BuildRequest.TryRead(ref longReader, out _));
    }

    [Fact]
    public void BuildResult_Is8Bytes_AndAnOkHasAnId()
    {
        var writer = new PacketWriter(_buffer);
        BuildResult.Write(ref writer, new BuildResult { Sequence = 9, Code = BuildResultCode.Ok, PieceId = 4242 });
        Assert.Equal(8, writer.Length);
        var r = After(writer.Length, PacketId.BuildResult);
        Assert.True(BuildResult.TryRead(ref r, out BuildResult back));
        Assert.Equal(4242u, back.PieceId);
        foreach (BuildResult bad in new[]
        {
            new BuildResult { Code = BuildResultCode.Ok, PieceId = 0 },
            new BuildResult { Code = BuildResultCode.Occupied, PieceId = 3 },
            new BuildResult { Code = (BuildResultCode)12 },
        })
        {
            writer = new PacketWriter(_buffer);
            BuildResult.Write(ref writer, bad);
            r = After(writer.Length, PacketId.BuildResult);
            Assert.False(BuildResult.TryRead(ref r, out _));
        }
    }

    // D13: Placed 14 bytes, Sync 16; every type, material, level and rotation survives.
    [Fact]
    public void APieceRecord_Is14Bytes_16InASync_AndRoundTrips()
    {
        foreach (BuildPieceRecord p in new[]
        {
            Piece(1, BuildPieceType.Wall, 31, 15, 31, 1, BuildMaterialType.Metal),
            Piece(uint.MaxValue, BuildPieceType.Ramp, 0, 0, 0, 3, BuildMaterialType.Wood),
            Piece(70000, BuildPieceType.Roof, 12, 7, 30, 0, BuildMaterialType.Stone),
            Piece(5, BuildPieceType.Floor, 3, 2, 1, 0, BuildMaterialType.Wood),
        })
        {
            var writer = new PacketWriter(_buffer);
            BuildPieceRecord.WritePlaced(ref writer, p);
            Assert.Equal(BuildPieceRecord.PlacedSize, writer.Length);
            Assert.Equal(14, writer.Length);
            var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out BuildPieceRecord back));
            Assert.Equal((p.Id, p.Shape, p.Material, p.Owner, p.CreatedTick), (back.Id, back.Shape, back.Material, back.Owner, back.CreatedTick));

            writer = new PacketWriter(_buffer);
            BuildPieceRecord.WriteSync(ref writer, p);
            Assert.Equal(16, writer.Length);
            r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.True(BuildPieceRecord.TryReadSync(ref r, out back));
            Assert.Equal(40, back.Damage);
        }
    }

    [Fact]
    public void ARecord_RefusesIdZero_AndNonCanonicalShapes()
    {
        foreach (BuildPieceRecord bad in new[]
        {
            Piece(0, BuildPieceType.Wall, 1, 1, 1, 0, BuildMaterialType.Wood),
            Piece(3, BuildPieceType.Wall, 1, 1, 1, 2, BuildMaterialType.Wood),      // a north wall is never sent: it is the next cell's south
            Piece(3, BuildPieceType.Floor, 1, 1, 1, 1, BuildMaterialType.Wood),     // a floor has no rotation
            Piece(3, BuildPieceType.Floor, 1, 1, 1, 0, (BuildMaterialType)3),
        })
        {
            var writer = new PacketWriter(_buffer);
            BuildPieceRecord.WritePlaced(ref writer, bad);
            var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.False(BuildPieceRecord.TryReadPlaced(ref r, out _));
        }
        // Phase 13.5 D8: bits 20-31 are the edit state; one the piece's type does not allow is refused.
        var unused = new byte[14];
        unused[0] = 1;
        unused[6] = 0x02;   // type Ramp (grid bits 16-17)
        unused[7] = 0x10;   // grid bit 28: a ramp's edit is always 0
        var reader = new PacketReader(unused);
        Assert.False(BuildPieceRecord.TryReadPlaced(ref reader, out _));
        foreach ((BuildPieceType type, int edit) in new[]
        {
            (BuildPieceType.Floor, 15), (BuildPieceType.Roof, 7), (BuildPieceType.Wall, 0b101), (BuildPieceType.Wall, 511), (BuildPieceType.Wall, 512),
        })
        {
            var writer = new PacketWriter(_buffer);
            var shape = new BuildPieceShape(type, 1, 1, 1, 0, edit);
            BuildPieceRecord.WritePlaced(ref writer, new BuildPieceRecord { Id = 3, Shape = shape });
            // 512 does not fit the 9 wall tiles: it is written as bit 9 of the 12-bit field.
            var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.False(BuildPieceRecord.TryReadPlaced(ref r, out _));
        }
    }

    // Phase 13.5 D8: an edited piece's state rides in the grid word and survives the trip, in Placed and in Sync.
    [Fact]
    public void AnEditedRecord_RoundTrips_WithTheSameSize()
    {
        foreach (BuildPieceShape shape in new[]
        {
            new BuildPieceShape(BuildPieceType.Wall, 4, 2, 5, 1, 0b000_010_010),   // a door
            new BuildPieceShape(BuildPieceType.Wall, 4, 2, 5, 0, 0b111_000_000),   // a half wall
            new BuildPieceShape(BuildPieceType.Floor, 4, 2, 5, 0, 0b0110),
            new BuildPieceShape(BuildPieceType.Roof, 4, 2, 5, 0, BuildEdit.RoofPassage),
            new BuildPieceShape(BuildPieceType.Roof, 4, 2, 5, 0, BuildEdit.RoofSlopeLast),
        })
        {
            var p = new BuildPieceRecord { Id = 9, Shape = shape, Material = BuildMaterialType.Stone, Owner = 3, CreatedTick = 77, Damage = 12 };
            var writer = new PacketWriter(_buffer);
            BuildPieceRecord.WritePlaced(ref writer, p);
            Assert.Equal(14, writer.Length);
            var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out BuildPieceRecord back));
            Assert.Equal(shape, back.Shape);
            writer = new PacketWriter(_buffer);
            BuildPieceRecord.WriteSync(ref writer, p);
            Assert.Equal(16, writer.Length);
            r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.True(BuildPieceRecord.TryReadSync(ref r, out back));
            Assert.Equal((shape, (ushort)12), (back.Shape, back.Damage));
        }
    }

    // Phase 13.5 D4: the edit request is 9 bytes and round-trips; any other length is refused.
    [Fact]
    public void BuildEditRequest_Is9Bytes_AndRoundTrips()
    {
        var writer = new PacketWriter(_buffer);
        BuildEditRequest.Write(ref writer, new BuildEditRequest { Sequence = 65535, PieceId = 123456, State = BuildEdit.PackState(16, 2) });
        Assert.Equal(BuildEditRequest.Size, writer.Length);
        Assert.Equal(9, writer.Length);
        Assert.Equal((byte)PacketId.BuildEditRequest, _buffer[0]);
        Assert.Equal(35, (byte)PacketId.BuildEditRequest);
        var r = After(writer.Length, PacketId.BuildEditRequest);
        Assert.True(BuildEditRequest.TryRead(ref r, out BuildEditRequest back));
        Assert.Equal(((ushort)65535, 123456u, BuildEdit.PackState(16, 2)), (back.Sequence, back.PieceId, back.State));
        for (int length = 1; length < 12; length++)
        {
            if (length == 9) continue;
            var reader = new PacketReader(new ReadOnlySpan<byte>(_buffer, 1, length - 1));
            Assert.False(BuildEditRequest.TryRead(ref reader, out _));
        }
    }

    // Phase 13.5 D9: the two new codes read back; a refusal still carries id 0 and an Ok an id.
    [Fact]
    public void TheEditResults_ReadBack_AndKeepTheIdRule()
    {
        foreach (BuildResultCode code in new[] { BuildResultCode.NotOwner, BuildResultCode.NotFound })
        {
            var writer = new PacketWriter(_buffer);
            BuildResult.Write(ref writer, new BuildResult { Sequence = 4, Code = code, PieceId = 0 });
            var r = After(writer.Length, PacketId.BuildResult);
            Assert.True(BuildResult.TryRead(ref r, out BuildResult back));
            Assert.Equal(code, back.Code);
            writer = new PacketWriter(_buffer);
            BuildResult.Write(ref writer, new BuildResult { Sequence = 4, Code = code, PieceId = 8 });
            r = After(writer.Length, PacketId.BuildResult);
            Assert.False(BuildResult.TryRead(ref r, out _));
        }
    }

    // Phase 13.5 D8: an Edited record refuses id 0 and bits 14-15.
    [Fact]
    public void AnEditedRecord_RefusesIdZero_AndTheTopBits()
    {
        foreach ((uint id, ushort state, bool ok) in new[] { (5u, (ushort)0x3FFF, true), (0u, (ushort)1, false), (5u, (ushort)0x4000, false), (5u, (ushort)0x8000, false) })
        {
            var writer = new PacketWriter(_buffer);
            BuildEventsPacket.WriteEdited(ref writer, id, state);
            var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.Equal(ok, BuildEventsPacket.TryReadEdited(ref r, out _, out _));
        }
    }

    [Fact]
    public void Destroyed_Is4Bytes_Edited6_Health6_AndTheHeader9()
    {
        var writer = new PacketWriter(_buffer);
        BuildEventsPacket.WriteHeader(ref writer, 99);
        Assert.Equal(9, BuildEventsPacket.HeaderSize);
        Assert.Equal(BuildEventsPacket.HeaderSize, writer.Length);
        int before = writer.Length;
        BuildEventsPacket.WriteEdited(ref writer, 5, BuildEdit.PackState(16, 1));
        Assert.Equal(6, writer.Length - before);
        before = writer.Length;
        BuildEventsPacket.WriteHealth(ref writer, 7, 120);
        Assert.Equal(6, writer.Length - before);
        before = writer.Length;
        BuildEventsPacket.WriteDestroyed(ref writer, 8);
        Assert.Equal(4, writer.Length - before);
        BuildEventsPacket.Patch(_buffer, 0, 1, 1, 1);
        var r = After(writer.Length, PacketId.BuildEvents);
        Assert.True(BuildEventsPacket.TryReadHeader(ref r, out uint version, out int placed, out int edited, out int health, out int destroyed));
        Assert.Equal((99u, 0, 1, 1, 1), (version, placed, edited, health, destroyed));
        Assert.True(BuildEventsPacket.TryReadEdited(ref r, out uint eid, out ushort state));
        Assert.Equal((5u, BuildEdit.PackState(16, 1)), (eid, state));
        Assert.True(BuildEventsPacket.TryReadHealth(ref r, out uint hid, out ushort damage));
        Assert.Equal((7u, (ushort)120), (hid, damage));
        Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint did));
        Assert.Equal(8u, did);

        // Counts that do not match the bytes are refused.
        BuildEventsPacket.Patch(_buffer, 1, 1, 1, 1);
        r = After(writer.Length, PacketId.BuildEvents);
        Assert.False(BuildEventsPacket.TryReadHeader(ref r, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void SyncAndInterest_HaveTheirSizes()
    {
        Assert.Equal(74, BuildSyncPacket.MaxRecords);
        Assert.True(BuildSyncPacket.HeaderSize + BuildSyncPacket.MaxRecords * BuildPieceRecord.SyncSize <= ProtocolConstants.MaxPacketSize);
        var writer = new PacketWriter(_buffer);
        BuildSyncPacket.WriteHeader(ref writer, 5, true, 1);
        BuildPieceRecord.WriteSync(ref writer, Piece(9, BuildPieceType.Ramp, 4, 1, 4, 2, BuildMaterialType.Stone));
        Assert.Equal(7 + 16, writer.Length);
        var r = After(writer.Length, PacketId.BuildSync);
        Assert.True(BuildSyncPacket.TryReadHeader(ref r, out uint version, out bool reset, out int count));
        Assert.Equal((5u, true, 1), (version, reset, count));

        writer = new PacketWriter(_buffer);
        BuildInterestPacket.Write(ref writer, 0x8000_0000_0000_0001UL);
        Assert.Equal(9, writer.Length);
        r = After(writer.Length, PacketId.BuildInterest);
        Assert.True(BuildInterestPacket.TryRead(ref r, out ulong cells));
        Assert.Equal(0x8000_0000_0000_0001UL, cells);
    }

    [Fact]
    public void BuildCatalog_Is49Bytes_AndCarriesTheNumbers()
    {
        var c = new BuildCatalogData
        {
            MaxResource = 500, BuildRange = 7f, ViewAngleDegrees = 75f, HarvestRange = 2.5f, HarvestCooldownTicks = 12, MinBuildIntervalTicks = 3,
            InterestCellSize = 20f, InterestRadius = 2, InterestKeepMargin = 1,
        };
        for (int m = 0; m < 3; m++)
        {
            c.ResourceCost[m] = 10;
            c.MaxHealth[m] = (ushort)(150 + 100 * m);
            c.InitialHealth[m] = 45;
            c.ConstructionTicks[m] = (ushort)(45 * (m + 1));
        }
        var writer = new PacketWriter(_buffer);
        BuildCatalogPacket.Write(ref writer, c);
        Assert.Equal(49, writer.Length);
        var r = After(writer.Length, PacketId.BuildCatalog);
        Assert.True(BuildCatalogPacket.TryRead(ref r, out BuildCatalogData back));
        Assert.Equal(350, back.MaxHealth[2]);
        Assert.Equal(135, back.ConstructionTicks[2]);
        Assert.Equal(7f, back.BuildRange);
        Assert.Equal(2, back.InterestRadius);

        c.BuildRange = float.NaN;
        writer = new PacketWriter(_buffer);
        BuildCatalogPacket.Write(ref writer, c);
        r = After(writer.Length, PacketId.BuildCatalog);
        Assert.False(BuildCatalogPacket.TryRead(ref r, out _));
    }

    // D13: a tick's events split into packets of at most MaxPacketSize bytes, in order (placed, health, destroyed), and a
    // client's window filters them by interest cell.
    [Fact]
    public void ATicksEvents_SplitIntoPackets_InOrder_AndFilterByCell()
    {
        BuildingCatalog catalog = BuildingCatalog.Default(30);
        var world = new BuildWorld(catalog);
        var replication = new BuildReplication(world, catalog, 100);
        var shapes = new List<BuildPieceShape>();
        for (int x = 0; x < 32; x++)
            for (int z = 0; z < 32; z++) shapes.Add(new BuildPieceShape(BuildPieceType.Floor, x, 0, z, 0));
        var ids = new List<uint>();
        foreach (BuildPieceShape s in shapes) ids.Add(world.Add(s, BuildMaterialType.Wood, 1, 0, true));
        for (int i = 0; i < 100; i++) replication.Placed(new BuildPieceRecord { Id = ids[i], Shape = shapes[i] });
        for (int i = 0; i < 300; i++)
        {
            world.TryGetSlot(ids[i], out int slot);
            world.At(slot).Damage = (ushort)i;
            replication.Damaged(slot);
        }
        for (int i = 300; i < 1024; i++)
        {
            world.TryGetSlot(ids[i], out int slot);
            replication.Destroyed(slot, ids[i], shapes[i]);
        }
        replication.Collect();

        foreach (ulong cells in new[] { ulong.MaxValue, 1UL })
        {
            var cursor = new BuildReplication.Cursor();
            int length;
            int placed = 0, health = 0, destroyed = 0, packets = 0;
            var buffer = new byte[ProtocolConstants.MaxPacketSize];
            uint lastDestroyed = 0;
            while ((length = replication.NextPacket(buffer, ref cursor, cells)) > 0)
            {
                packets++;
                Assert.True(length <= ProtocolConstants.MaxPacketSize);
                var r = new PacketReader(new ReadOnlySpan<byte>(buffer, 0, length));
                Assert.True(r.TryReadPacketId(out _));
                Assert.True(BuildEventsPacket.TryReadHeader(ref r, out uint version, out int p, out int e, out int h, out int d));
                Assert.Equal(replication.Version, version);
                // Inside one packet, and across packets, placed come before health before destroyed.
                if (h > 0 || d > 0) Assert.Equal(cells == ulong.MaxValue ? 100 : 16, placed + p);
                for (int i = 0; i < p; i++) Assert.True(BuildPieceRecord.TryReadPlaced(ref r, out _));
                Assert.Equal(0, e);
                for (int i = 0; i < h; i++) Assert.True(BuildEventsPacket.TryReadHealth(ref r, out _, out _));
                for (int i = 0; i < d; i++)
                {
                    Assert.True(BuildEventsPacket.TryReadDestroyed(ref r, out uint id));
                    Assert.True(id > lastDestroyed);
                    lastDestroyed = id;
                }
                placed += p;
                health += h;
                destroyed += d;
            }
            if (cells == ulong.MaxValue)
            {
                Assert.Equal((100, 300, 724), (placed, health, destroyed));
                Assert.True(packets >= 4);
            }
            else
            {
                // Interest cell 0 = build cells x 0..3, z 0..3 (index x * 32 + z): 16 of the placed and damaged, none destroyed.
                Assert.Equal((16, 16, 0), (placed, health, destroyed));
            }
        }
    }
}
