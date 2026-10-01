using System;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 §2: one client sends seeded random packets over real UDP. Only that client is closed (Kicked); the other
// keeps getting snapshots.
public sealed class FuzzIntegrationTests
{
    [Fact]
    public void APeerSendingRandomPackets_IsKicked_AndTheOtherPlaysOn()
    {
        using GameLoop server = StartServer();
        using var fuzzer = Join(server, "fuzzer");
        using var other = Join(server, "other");
        var random = new Random(7);

        for (int i = 0; i < 200 && !fuzzer.Disconnected; i++)
        {
            var packet = new byte[random.Next(1, 65)];
            random.NextBytes(packet);
            fuzzer.SendRaw(packet);
            if (i % 20 == 0) Pump.Until(() => false, 10, fuzzer, other);
        }
        Assert.True(Pump.Until(() => fuzzer.Disconnected, 3000, fuzzer, other), "fuzzer kicked");
        Assert.Equal(DisconnectCode.Kicked, fuzzer.DisconnectCode);
        Assert.Equal(1, server.Health.Kicks(DisconnectCode.Kicked));

        int before = other.SnapshotsReceived;
        Assert.True(SendInputsUntil(other, () => other.SnapshotsReceived > before + 5, 3000), "the other keeps getting snapshots");
        Assert.False(other.Disconnected);
        // 1, not 2 with a graced fuzzer: a server close (Kicked) never gets the reconnect grace, so the fuzzer left at once.
        Assert.Equal(1, server.Match.PlayerCount);
    }
}
