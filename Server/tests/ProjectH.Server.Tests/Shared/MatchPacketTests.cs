using System;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 5 spec §3: MatchState, ZoneState, MatchResult and the Placement byte of PlayerDied.
public class MatchPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static ZoneState SampleZone() => new ZoneState
    {
        Phase = 3,
        FromX = 1.5f,
        FromZ = -2.25f,
        FromRadius = 12f,
        ToX = 3f,
        ToZ = -4f,
        ToRadius = 6f,
        ShrinkStartTick = 1000,
        ShrinkEndTick = 1300,
        DamagePerSecond = 5,
    };

    [Fact]
    public void MatchState_RoundTrip_Is11Bytes()
    {
        var writer = new PacketWriter(_buffer);
        MatchState.Write(ref writer, new MatchState
        {
            State = MatchFlowState.Starting, StateEndTick = 123456, Alive = 3, Participants = 5, Round = 7, MinPlayers = 2,
        });
        Assert.Equal(MatchState.Size, writer.Length);
        Assert.Equal(11, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.MatchState);
        Assert.True(MatchState.TryRead(ref reader, out var s));
        Assert.Equal(MatchFlowState.Starting, s.State);
        Assert.Equal(123456u, s.StateEndTick);
        Assert.Equal(3, s.Alive);
        Assert.Equal(5, s.Participants);
        Assert.Equal(7, s.Round);
        Assert.Equal(2, s.MinPlayers);
    }

    [Fact]
    public void MatchState_UnknownStateOrMoreAliveThanParticipants_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        MatchState.Write(ref writer, new MatchState { State = (MatchFlowState)6, Alive = 1, Participants = 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.MatchState);
        Assert.False(MatchState.TryRead(ref reader, out _));

        writer = new PacketWriter(_buffer);
        MatchState.Write(ref writer, new MatchState { State = MatchFlowState.Playing, Alive = 3, Participants = 2 });
        reader = ReaderAfterId(writer.Length, PacketId.MatchState);
        Assert.False(MatchState.TryRead(ref reader, out _));
    }

    [Fact]
    public void ZoneState_RoundTrip_Is36Bytes()
    {
        var writer = new PacketWriter(_buffer);
        ZoneState.Write(ref writer, SampleZone());
        Assert.Equal(ZoneState.Size, writer.Length);
        Assert.Equal(36, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.ZoneState);
        Assert.True(ZoneState.TryRead(ref reader, out var z));
        Assert.True(z.SameAs(SampleZone()));
        Assert.Equal(3, z.Phase);
        Assert.Equal(-2.25f, z.FromZ);
        Assert.Equal(1300u, z.ShrinkEndTick);
        Assert.Equal(5, z.DamagePerSecond);
    }

    [Fact]
    public void ZoneState_NonFiniteNegativeRadiusOrReversedTicks_IsRejected()
    {
        ZoneState[] bad =
        {
            SampleZone(), SampleZone(), SampleZone(), SampleZone(),
        };
        bad[0].FromX = float.NaN;
        bad[1].ToRadius = float.PositiveInfinity;
        bad[2].FromRadius = -1f;
        bad[3].ShrinkEndTick = bad[3].ShrinkStartTick - 1;
        foreach (ZoneState zone in bad)
        {
            var writer = new PacketWriter(_buffer);
            ZoneState.Write(ref writer, zone);
            var reader = ReaderAfterId(writer.Length, PacketId.ZoneState);
            Assert.False(ZoneState.TryRead(ref reader, out _));
        }
    }

    [Fact]
    public void MatchResult_RoundTrip_Is6Bytes()
    {
        var writer = new PacketWriter(_buffer);
        MatchResult.Write(ref writer, new MatchResult { WinnerId = 4, Placement = 2, Kills = 3, Participants = 5 });
        Assert.Equal(MatchResult.Size, writer.Length);
        Assert.Equal(6, writer.Length);

        var reader = ReaderAfterId(writer.Length, PacketId.MatchResult);
        Assert.True(MatchResult.TryRead(ref reader, out var r));
        Assert.Equal(4, r.WinnerId);
        Assert.Equal(2, r.Placement);
        Assert.Equal(3, r.Kills);
        Assert.Equal(5, r.Participants);
    }

    [Theory]
    [InlineData(0, 5)]   // placement 0 is "no placement", never a result
    [InlineData(6, 5)]   // below the last place
    public void MatchResult_PlacementOutsideTheField_IsRejected(byte placement, byte participants)
    {
        var writer = new PacketWriter(_buffer);
        MatchResult.Write(ref writer, new MatchResult { WinnerId = 1, Placement = placement, Participants = participants });
        var reader = ReaderAfterId(writer.Length, PacketId.MatchResult);
        Assert.False(MatchResult.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerDied_CarriesPlacement_In6Bytes()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = 2, KillerId = 0, Placement = 4 });
        Assert.Equal(6, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.True(PlayerDied.TryRead(ref reader, out var died));
        Assert.Equal(2, died.VictimId);
        Assert.Equal(0, died.KillerId);
        Assert.Equal(4, died.Placement);
    }

    [Fact]
    public void TruncatedMatchPackets_AreRejected()
    {
        var shortState = new PacketReader(new byte[MatchState.Size - 2]);
        Assert.False(MatchState.TryRead(ref shortState, out _));
        var shortZone = new PacketReader(new byte[ZoneState.Size - 2]);
        Assert.False(ZoneState.TryRead(ref shortZone, out _));
        var shortResult = new PacketReader(new byte[MatchResult.Size - 2]);
        Assert.False(MatchResult.TryRead(ref shortResult, out _));
        var shortDied = new PacketReader(new byte[4]);   // the v4 layout, without Placement
        Assert.False(PlayerDied.TryRead(ref shortDied, out _));
    }

    // Spec §3: every packet fits one datagram. The new ones are fixed size.
    [Fact]
    public void MatchPackets_FitOneDatagram()
    {
        Assert.True(MatchState.Size <= ProtocolConstants.MaxPacketSize);
        Assert.True(ZoneState.Size <= ProtocolConstants.MaxPacketSize);
        Assert.True(MatchResult.Size <= ProtocolConstants.MaxPacketSize);
    }
}
