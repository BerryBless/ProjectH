using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class PacketWriterReaderTests
{
    [Fact]
    public void RoundTrip_AllTypes()
    {
        var buffer = new byte[64];
        var writer = new PacketWriter(buffer);
        writer.WriteByte(7);
        writer.WriteUInt16(65000);
        writer.WriteUInt32(4_000_000_000);
        writer.WriteSingle(-1.5f);
        writer.WriteVector3(new Vector3(1f, 2f, 3f));
        writer.WriteString("player-1", 32);
        Assert.False(writer.Overflowed);

        var reader = new PacketReader(buffer.AsSpan(0, writer.Length));
        Assert.True(reader.TryReadByte(out byte b));
        Assert.True(reader.TryReadUInt16(out ushort u16));
        Assert.True(reader.TryReadUInt32(out uint u32));
        Assert.True(reader.TryReadSingle(out float f));
        Assert.True(reader.TryReadVector3(out Vector3 v));
        Assert.True(reader.TryReadString(32, out string s));

        Assert.Equal(7, b);
        Assert.Equal(65000, u16);
        Assert.Equal(4_000_000_000u, u32);
        Assert.Equal(-1.5f, f);
        Assert.Equal(new Vector3(1f, 2f, 3f), v);
        Assert.Equal("player-1", s);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Writer_Overflows_WithoutThrowing_AndStopsWriting()
    {
        var buffer = new byte[3];
        var writer = new PacketWriter(buffer);
        writer.WriteUInt16(1);
        writer.WriteUInt32(2);   // does not fit
        writer.WriteByte(3);     // ignored after overflow

        Assert.True(writer.Overflowed);
        Assert.Equal(2, writer.Length);
    }

    [Fact]
    public void Reader_ReturnsFalse_OnTruncatedData()
    {
        var reader = new PacketReader(new byte[] { 1, 2, 3 });
        Assert.False(reader.TryReadUInt32(out _));
        Assert.False(reader.TryReadVector3(out _));
    }

    [Fact]
    public void String_LongerThanMax_OverflowsWriter_AndIsRejectedByReader()
    {
        var writer = new PacketWriter(new byte[128]);
        writer.WriteString(new string('x', 33), 32);
        Assert.True(writer.Overflowed);

        // Length prefix 40 but only 2 payload bytes: must be rejected, not read past the end.
        var reader = new PacketReader(new byte[] { 40, (byte)'a', (byte)'b' });
        Assert.False(reader.TryReadString(64, out _));

        // Length prefix within data but above the caller's limit.
        var reader2 = new PacketReader(new byte[] { 3, (byte)'a', (byte)'b', (byte)'c' });
        Assert.False(reader2.TryReadString(2, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(47)]   // one above PacketId.ProjectileExploded (Phase 17)
    [InlineData(255)]
    public void PacketId_OutOfRange_IsRejected(byte raw)
    {
        var reader = new PacketReader(new[] { raw });
        Assert.False(reader.TryReadPacketId(out _));
    }

    [Theory]
    [InlineData(PacketId.PlayerInput)]
    [InlineData(PacketId.WeaponCatalog)]
    [InlineData(PacketId.PlayerRespawned)]
    [InlineData(PacketId.ItemCatalog)]
    [InlineData(PacketId.PickupResult)]
    [InlineData(PacketId.MatchState)]
    [InlineData(PacketId.MatchResult)]
    [InlineData(PacketId.MapMarker)]     // Phase 15: the bound was raised with the new packets
    [InlineData(PacketId.TeamMarkers)]
    [InlineData(PacketId.ContainerStates)]   // Phase 16
    [InlineData(PacketId.SupplyDrops)]
    public void PacketId_InRange_IsAccepted(PacketId expected)
    {
        var reader = new PacketReader(new[] { (byte)expected });
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
    }
}
