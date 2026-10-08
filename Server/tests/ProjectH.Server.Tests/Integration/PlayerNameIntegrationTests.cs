using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 11 D9 over real UDP: every PlayerSpawned carries the player's DevPlayerId, so a client knows the other players'
// names (and its own) from the spawns it gets at join and when someone joins later.
public sealed class PlayerNameIntegrationTests
{
    [Fact]
    public void EveryPlayerSpawned_CarriesTheDevPlayerId()
    {
        using GameLoop server = StartServer();
        using var alice = Join(server, "alice");
        using var bob = Join(server, "밥");   // multi-byte UTF-8 travels as it was typed

        Assert.True(Pump.Until(() => alice.SpawnNames.Count == 2 && bob.SpawnNames.Count == 2, 3000, alice, bob), "spawns");
        Assert.Equal("밥", alice.SpawnNames[bob.MyEntityId]);
        Assert.Equal("alice", alice.SpawnNames[alice.MyEntityId]);
        Assert.Equal("alice", bob.SpawnNames[alice.MyEntityId]);
        Assert.Equal("밥", bob.SpawnNames[bob.MyEntityId]);
    }

    // A name the other clients could not be sent (invalid UTF-8 grows past 32 bytes when decoded with replacement
    // characters) or could not show (control characters) is refused at connect as BadRequest, so no player is ever
    // invisible to the others.
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]   // 12 invalid bytes
    [InlineData(new byte[] { 0x61, 0xC3 })]                                                            // "a" + a cut 2-byte sequence
    [InlineData(new byte[] { 0x61, 0x0A, 0x62 })]                                                      // "a\nb"
    [InlineData(new byte[] { 0x61, 0xC2, 0x85 })]                                                      // "a" + U+0085 (C1)
    public void ABadName_IsRejectedAsBadRequest(byte[] name)
    {
        using GameLoop server = StartServer();
        // v19 layout (review fix A3): version, flags (0: no cookie; the name is read before the cookie step), name.
        var payload = new byte[4 + name.Length];
        payload[0] = (byte)(ProtocolConstants.ProtocolVersion & 0xFF);
        payload[1] = (byte)(ProtocolConstants.ProtocolVersion >> 8);
        payload[2] = 0;
        payload[3] = (byte)name.Length;
        name.CopyTo(payload, 4);

        using var bad = new HeadlessClient();
        bad.ConnectRaw(server.LocalPort, payload);
        Assert.True(Pump.Until(() => bad.Disconnected, 3000, bad), "rejected");
        Assert.Equal(LiteNetLib.DisconnectReason.ConnectionRejected, bad.DisconnectReason);
        Assert.Equal(RejectReason.BadRequest, bad.RejectReason);
        Assert.Equal(1, server.Health.Rejects(RejectReason.BadRequest));
    }
}
