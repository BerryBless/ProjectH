using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13 D5, D6, D7, D15, D19: the harvest packets and the tool on the wire. Sizes include the packet id.
public class HarvestPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader After(int length, PacketId expected)
    {
        var reader = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    [Fact]
    public void ResourcesState_Is7Bytes_AndRoundTrips()
    {
        var writer = new PacketWriter(_buffer);
        ResourcesState.Write(ref writer, new ResourcesState { Wood = 120, Stone = 80, Metal = 35 });
        Assert.Equal(ResourcesState.Size, writer.Length);
        Assert.Equal(7, writer.Length);
        var r = After(writer.Length, PacketId.ResourcesState);
        Assert.True(ResourcesState.TryRead(ref r, out ResourcesState s));
        Assert.Equal(120, s.Get(BuildMaterialType.Wood));
        Assert.Equal(80, s.Get(BuildMaterialType.Stone));
        Assert.Equal(35, s.Get(BuildMaterialType.Metal));
        var shortReader = new PacketReader(new byte[5]);
        Assert.False(ResourcesState.TryRead(ref shortReader, out _));
    }

    [Fact]
    public void HarvestHit_Is18Bytes_RoundTrips_AndRefusesBadValues()
    {
        var hit = new HarvestHit
        {
            TargetId = 3, Health = 75, WeakPoint = new Vector3(1f, 2f, 3f), Gained = 12,
            Flags = HarvestHit.WeakPointHitFlag | HarvestHit.HasWeakPointFlag,
        };
        var writer = new PacketWriter(_buffer);
        HarvestHit.Write(ref writer, hit);
        Assert.Equal(18, writer.Length);
        var r = After(writer.Length, PacketId.HarvestHit);
        Assert.True(HarvestHit.TryRead(ref r, out HarvestHit back));
        Assert.Equal(hit.TargetId, back.TargetId);
        Assert.Equal(hit.Health, back.Health);
        Assert.Equal(hit.WeakPoint, back.WeakPoint);
        Assert.Equal(hit.Gained, back.Gained);
        Assert.True(back.WeakPointHit);
        Assert.True(back.HasWeakPoint);
        Assert.False(back.Destroyed);

        foreach (HarvestHit bad in new[]
        {
            hit with { TargetId = (byte)GameMap.Harvestables.Length },
            hit with { Flags = 8 },
            hit with { WeakPoint = new Vector3(float.NaN, 0f, 0f) },
        })
        {
            writer = new PacketWriter(_buffer);
            HarvestHit.Write(ref writer, bad);
            r = After(writer.Length, PacketId.HarvestHit);
            Assert.False(HarvestHit.TryRead(ref r, out _));
        }
    }

    [Fact]
    public void HarvestStates_Is9Bytes_RoundTrips_AndRefusesUnknownBits()
    {
        ulong mask = (1UL << 0) | (1UL << 17) | (1UL << (GameMap.Harvestables.Length - 1));
        var writer = new PacketWriter(_buffer);
        HarvestStatesPacket.Write(ref writer, mask);
        Assert.Equal(HarvestStatesPacket.Size, writer.Length);
        Assert.Equal(9, writer.Length);
        var r = After(writer.Length, PacketId.HarvestStates);
        Assert.True(HarvestStatesPacket.TryRead(ref r, out ulong back));
        Assert.Equal(mask, back);

        writer = new PacketWriter(_buffer);
        HarvestStatesPacket.Write(ref writer, 1UL << GameMap.Harvestables.Length);
        r = After(writer.Length, PacketId.HarvestStates);
        Assert.False(HarvestStatesPacket.TryRead(ref r, out _));
    }

    // D5: the owner's tool in the weapon slot byte's top bits; the self block stays 14 bytes and the slot is unchanged.
    [Theory]
    [InlineData(0, ToolKind.Weapon)]
    [InlineData(2, ToolKind.Harvest)]
    [InlineData(1, ToolKind.Build)]
    public void TheSelfBlock_CarriesTheTool_In14Bytes(byte slot, ToolKind tool)
    {
        var writer = new PacketWriter(_buffer);
        SnapshotSelf.Write(ref writer, new SnapshotSelf { Health = 90, WeaponSlot = slot, Tool = tool, Ammo = 7, Energy = 500 });
        Assert.Equal(14, writer.Length);
        Assert.Equal(SnapshotSelf.Size, writer.Length);
        var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
        Assert.True(SnapshotSelf.TryRead(ref r, out SnapshotSelf s));
        Assert.Equal(slot, s.WeaponSlot);
        Assert.Equal(tool, s.Tool);
        Assert.Equal(7, s.Ammo);
    }

    [Fact]
    public void TheSelfBlock_RefusesToolThree()
    {
        var writer = new PacketWriter(_buffer);
        SnapshotSelf.Write(ref writer, new SnapshotSelf { Energy = 0 });
        _buffer[2] = 0xC0;   // the weapon slot byte with tool bits 11
        var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
        Assert.False(SnapshotSelf.TryRead(ref r, out _));
    }

    // D5: the tool in entity flag bits 6-7; the entity stays 13 bytes and the other flags are untouched.
    [Fact]
    public void TheEntityFlags_CarryTheTool_In13Bytes()
    {
        foreach (ToolKind tool in new[] { ToolKind.Weapon, ToolKind.Harvest, ToolKind.Build })
        {
            byte flags = SnapshotEntity.MakeFlags(true, MovementMode.Slide, true, true, tool);
            var writer = new PacketWriter(_buffer);
            SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = 4, Flags = flags });
            Assert.Equal(13, writer.Length);
            var r = new PacketReader(new ReadOnlySpan<byte>(_buffer, 0, writer.Length));
            Assert.True(SnapshotEntity.TryRead(ref r, out SnapshotEntity e));
            Assert.Equal(tool, e.Tool);
            Assert.Equal(MovementMode.Slide, e.Mode);
            Assert.True(e.IsAlive && e.IsSprinting && e.IsExhausted);
        }
        Assert.Equal(ToolKind.Weapon, new SnapshotEntity { Flags = 0xC0 }.Tool);   // 3 (a bad packet) reads as Weapon
        Assert.Equal(SnapshotEntity.MakeFlags(true, MovementMode.Ground, false, false), SnapshotEntity.MakeFlags(true, MovementMode.Ground, false, false, ToolKind.Weapon));
    }

    [Fact]
    public void TheToolButtons_AreKnownInputBits()
    {
        Assert.Equal(4096, (int)InputButtons.ToolHarvest);
        Assert.Equal(8192, (int)InputButtons.ToolBuild);
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.ToolHarvest | InputButtons.ToolBuild });
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var r = After(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref r, out PlayerInputPacket read));
        Assert.Equal(InputButtons.ToolHarvest | InputButtons.ToolBuild, read.Get(0).Buttons);
    }

    [Fact]
    public void ThePhase13PacketIds_AreStable()
    {
        Assert.Equal(26, (byte)PacketId.BuildCatalog);
        Assert.Equal(27, (byte)PacketId.BuildRequest);
        Assert.Equal(28, (byte)PacketId.BuildResult);
        Assert.Equal(29, (byte)PacketId.BuildEvents);
        Assert.Equal(30, (byte)PacketId.BuildSync);
        Assert.Equal(31, (byte)PacketId.BuildInterest);
        Assert.Equal(32, (byte)PacketId.ResourcesState);
        Assert.Equal(33, (byte)PacketId.HarvestHit);
        Assert.Equal(34, (byte)PacketId.HarvestStates);
        var reader = new PacketReader(new byte[] { 34 });
        Assert.True(reader.TryReadPacketId(out _));
    }
}
