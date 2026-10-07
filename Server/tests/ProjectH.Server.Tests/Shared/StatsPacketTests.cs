using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 11 spec §2 Protocol: StatsResponse (0 and 10 rows, truncated, more than 10 rows), the name in PlayerSpawned and
// the new packet ids.
public class StatsPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static StatsRow Row(int i) => new StatsRow
    {
        EndedUnixSeconds = 1_790_000_000u + (uint)i,
        Round = 100u + (uint)i,
        Players = (byte)(10 + i),
        Placement = (byte)(1 + i),
        Kills = (ushort)(300 + i),
        Damage = 70_000u + (uint)i,
        SurvivalMs = 4_000_000u + (uint)i,
    };

    private static StatsResponse Full(int rows)
    {
        var r = new StatsResponse
        {
            Status = StatsStatus.Ok,
            Summary = new StatsSummary { Matches = 12, Wins = 3, Kills = 40, Deaths = 9, Damage = 4_000_000_000u, SurvivalSeconds = 86_400 },
            Rows = new StatsRow[rows],
        };
        for (int i = 0; i < rows; i++) r.Rows[i] = Row(i);
        return r;
    }

    [Fact]
    public void Ids_AndTheReaderRange()
    {
        Assert.Equal(22, (byte)PacketId.StatsRequest);
        Assert.Equal(23, (byte)PacketId.StatsResponse);
        var reader = new PacketReader(new byte[] { 23 });
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.StatsResponse, id);
        reader = new PacketReader(new byte[] { 36 });   // Phase 12 added 24 and 25, Phase 13 26-34, Phase 13.5 35
        Assert.False(reader.TryReadPacketId(out _));
    }

    [Fact]
    public void StatsRequest_IsTheIdAlone()
    {
        var writer = new PacketWriter(_buffer);
        StatsRequest.Write(ref writer);
        Assert.Equal(1, writer.Length);
        Assert.Equal((byte)PacketId.StatsRequest, _buffer[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public void StatsResponse_RoundTrip(int rows)
    {
        StatsResponse sent = Full(rows);
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, sent);
        Assert.False(writer.Overflowed);
        Assert.Equal(1 + 1 + StatsResponse.SummarySize + 1 + rows * StatsResponse.RowSize, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.True(StatsResponse.TryRead(ref reader, out StatsResponse got));
        Assert.Equal(StatsStatus.Ok, got.Status);
        Assert.Equal(sent.Summary, got.Summary);
        Assert.Equal(sent.Rows, got.Rows);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void StatsResponse_MaxSize_Is227_AndFitsOneDatagram()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(StatsResponse.MaxRows));
        Assert.Equal(StatsResponse.MaxSize, writer.Length);
        Assert.Equal(227, StatsResponse.MaxSize);
        Assert.True(StatsResponse.MaxSize <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void StatsResponse_WritesAtMostTenRows()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(12));
        Assert.Equal(StatsResponse.MaxSize, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.True(StatsResponse.TryRead(ref reader, out StatsResponse got));
        Assert.Equal(StatsResponse.MaxRows, got.Rows.Length);
    }

    [Theory]
    [InlineData(StatsStatus.NoRecord)]
    [InlineData(StatsStatus.Unavailable)]
    [InlineData(StatsStatus.Busy)]
    public void StatsResponse_OtherStatuses_CarryNoData(StatsStatus status)
    {
        StatsResponse sent = Full(3);
        sent.Status = status;
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, sent);
        Assert.Equal(1 + 1 + StatsResponse.SummarySize + 1, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.True(StatsResponse.TryRead(ref reader, out StatsResponse got));
        Assert.Equal(status, got.Status);
        Assert.Equal(default(StatsSummary), got.Summary);
        Assert.Empty(got.Rows);
    }

    [Fact]
    public void StatsResponse_Truncated_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(3));
        for (int length = 1; length < writer.Length; length++)
        {
            var reader = ReaderAfterId(length, PacketId.StatsResponse);
            Assert.False(StatsResponse.TryRead(ref reader, out _), $"length {length}");
        }
    }

    [Fact]
    public void StatsResponse_MoreThanTenRows_IsRejected()
    {
        // 11 rows claimed, with enough bytes behind it.
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(0));
        _buffer[1 + 1 + StatsResponse.SummarySize] = StatsResponse.MaxRows + 1;
        int length = writer.Length + (StatsResponse.MaxRows + 1) * StatsResponse.RowSize;
        var reader = ReaderAfterId(length, PacketId.StatsResponse);
        Assert.False(StatsResponse.TryRead(ref reader, out _));
    }

    [Fact]
    public void StatsResponse_AnUnknownStatus_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(0));
        _buffer[1] = (byte)StatsStatus.Busy + 1;
        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.False(StatsResponse.TryRead(ref reader, out _));
    }

    [Fact]
    public void StatsResponse_RowsWithoutOk_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        StatsResponse.Write(ref writer, Full(2));
        _buffer[1] = (byte)StatsStatus.NoRecord;
        var reader = ReaderAfterId(writer.Length, PacketId.StatsResponse);
        Assert.False(StatsResponse.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("dev-1234abcd")]
    [InlineData("플레이어")]
    [InlineData("abcdefghijabcdefghijabcdefghij12")]   // 32 bytes
    public void PlayerSpawned_Name_RoundTrips(string name)
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 7, Position = new Vector3(1, 2, 3), Yaw = 90f, Name = name });
        Assert.False(writer.Overflowed);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.True(PlayerSpawned.TryRead(ref reader, out PlayerSpawned s));
        Assert.Equal(name, s.Name);
        Assert.Equal(7, s.EntityId);
        Assert.Equal(new Vector3(1, 2, 3), s.Position);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void PlayerSpawned_EmptyName_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 7, Name = "" });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.False(PlayerSpawned.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerSpawned_A33ByteName_IsRejected()
    {
        // The writer refuses it, so the bytes are laid out by hand.
        var writer = new PacketWriter(_buffer);
        writer.WriteByte((byte)PacketId.PlayerSpawned);
        writer.WriteUInt16(7);
        writer.WriteVector3(Vector3.Zero);
        writer.WriteSingle(0f);
        writer.WriteString(new string('x', 33), 64);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.False(PlayerSpawned.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerSpawned_A33ByteName_OverflowsTheWriter()
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 7, Name = new string('x', 33) });
        Assert.True(writer.Overflowed);
    }

    [Fact]
    public void PlayerSpawned_WithoutTheName_IsRejected()
    {
        // A v8 layout (no name byte) must not parse as a v9 spawn.
        var writer = new PacketWriter(_buffer);
        writer.WriteByte((byte)PacketId.PlayerSpawned);
        writer.WriteUInt16(7);
        writer.WriteVector3(Vector3.Zero);
        writer.WriteSingle(0f);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.False(PlayerSpawned.TryRead(ref reader, out _));
    }
}
