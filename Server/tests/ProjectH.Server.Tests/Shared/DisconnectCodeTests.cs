using System;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 10 D1, D10: the disconnect code on the wire and the reconnect table the client and the bots share.
public class DisconnectCodeTests
{
    [Fact]
    public void Read_EmptyOrUnknownData_IsNone()
    {
        Assert.Equal(DisconnectCode.None, DisconnectCodes.Read(ReadOnlySpan<byte>.Empty));
        Assert.Equal(DisconnectCode.None, DisconnectCodes.Read(new byte[] { 6 }));
        Assert.Equal(DisconnectCode.None, DisconnectCodes.Read(new byte[] { 255 }));
    }

    [Theory]
    [InlineData(DisconnectCode.ServerShutdown)]
    [InlineData(DisconnectCode.Kicked)]
    [InlineData(DisconnectCode.JoinTimeout)]
    [InlineData(DisconnectCode.InputTimeout)]
    [InlineData(DisconnectCode.ServerError)]
    public void Read_KnownCodes_RoundTrip(DisconnectCode code)
    {
        Assert.Equal(code, DisconnectCodes.Read(new[] { (byte)code }));
        Assert.Equal(code, DisconnectCodes.Read(new[] { (byte)code, (byte)99 }));   // only the first byte counts
    }

    // D10: retry a lost connection and a match reset; never a server decision that would repeat, a local disconnect
    // or a reject.
    [Theory]
    [InlineData(true, DisconnectCode.ServerError, false, true)]
    [InlineData(true, DisconnectCode.ServerShutdown, false, false)]
    [InlineData(true, DisconnectCode.Kicked, false, false)]
    [InlineData(true, DisconnectCode.JoinTimeout, false, false)]
    [InlineData(true, DisconnectCode.InputTimeout, false, false)]
    [InlineData(true, DisconnectCode.None, false, false)]
    [InlineData(false, DisconnectCode.None, true, true)]    // Timeout, ConnectionFailed, unreachable
    [InlineData(false, DisconnectCode.None, false, false)]  // Disconnect() called locally, or rejected
    public void ShouldReconnect_FollowsTheTable(bool remoteClose, DisconnectCode code, bool networkLoss, bool expected)
    {
        Assert.Equal(expected, DisconnectCodes.ShouldReconnect(remoteClose, code, networkLoss));
    }

    [Fact]
    public void JoinResultResumed_IsThree()
    {
        Assert.Equal(3, (byte)JoinResult.Resumed);
        Assert.Equal(3, DisconnectCodes.MaxReconnectAttempts);
    }

    // A1: attempts start 1, 3 and 7 s after the drop (1, 2, 4 s apart), out-of-range attempts clamp.
    [Theory]
    [InlineData(1, 1f)]
    [InlineData(2, 3f)]
    [InlineData(3, 7f)]
    [InlineData(0, 1f)]
    [InlineData(4, 7f)]
    public void ReconnectOffsets_AreCumulativeFromTheDrop(int attempt, float seconds)
    {
        Assert.Equal(seconds, DisconnectCodes.ReconnectOffsetSeconds(attempt));
    }

    // A1: LiteNetLib gives up after (MaxConnectAttempts + 1) * ReconnectDelay. One attempt must end before the next slot,
    // and the last one before the server's default grace (10 s) ends.
    [Fact]
    public void TheConnectBudget_FitsEverySlot_AndTheGrace()
    {
        double budget = (DisconnectCodes.ReconnectRequestAttempts + 1) * DisconnectCodes.ReconnectRequestIntervalMs / 1000.0;
        Assert.True(budget <= 1.5, $"budget {budget} s");
        for (int n = 1; n < DisconnectCodes.MaxReconnectAttempts; n++)
            Assert.True(DisconnectCodes.ReconnectOffsetSeconds(n) + budget < DisconnectCodes.ReconnectOffsetSeconds(n + 1), $"attempt {n}");
        Assert.True(DisconnectCodes.ReconnectOffsetSeconds(DisconnectCodes.MaxReconnectAttempts) + budget < new ServerOptions().ReconnectGraceSeconds);
    }

    // The server's close data carries the code as its first byte, for every code.
    [Fact]
    public void DataOf_StartsWithTheCode_ForEveryCode()
    {
        foreach (DisconnectCode code in Enum.GetValues<DisconnectCode>())
        {
            byte[] data = NetworkListener.DataOf(code);
            Assert.Equal((byte)code, data[0]);
            Assert.Equal(code, DisconnectCodes.Read(data));
        }
    }
}
