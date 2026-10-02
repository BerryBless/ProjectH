using System;
using System.Collections.Generic;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Server review M7: an exception in one player's part of the tick takes out that player only. The other players move,
// and the tick (and its snapshots) goes on.
public class PlayerFailureTests
{
    [Fact]
    public void APlayerWhoseTickThrows_Leaves_AndTheOthersGoOn()
    {
        var sent = new List<(int Peer, PacketId Id)>();
        var failed = new List<(int Peer, Exception Error)>();
        var match = new Match(new ServerOptions { MaxPlayers = 4, DevRespawn = true }, TestGameData.Create(),
            (peer, data, _) => sent.Add((peer, (PacketId)data[0])), playerFailed: (peer, error) => failed.Add((peer, error)));
        Assert.Equal(JoinResult.Ok, match.TryJoin(101, "a"));
        Assert.Equal(JoinResult.Ok, match.TryJoin(102, "b"));
        match.TryGetPlayer(101, out PlayerEntity a);
        match.TryGetPlayer(102, out PlayerEntity b);
        match.Tick();

        match.FaultEntityId = a.EntityId;
        sent.Clear();
        uint tick = match.ServerTick;
        var forward = new ProjectH.Shared.Simulation.InputCommand { Seq = 1, MoveY = 1f };
        match.EnqueueInput(102, new PlayerInputPacket { Count = 1, Input0 = forward });
        System.Numerics.Vector3 before = b.State.Position;
        match.Tick();

        Assert.Single(failed);
        Assert.Equal(101, failed[0].Peer);
        Assert.Equal("test fault", failed[0].Error.Message);
        Assert.False(match.TryGetPlayer(101, out _));
        Assert.Equal(1, match.PlayerCount);
        Assert.Equal(tick + 1, match.ServerTick);
        Assert.NotEqual(before, b.State.Position);   // b moved in the same tick
        Assert.Contains((102, PacketId.PlayerDespawned), sent);

        for (int i = 0; i < 4; i++) match.Tick();
        Assert.Single(failed);                        // gone: it cannot fail again
        Assert.Contains((102, PacketId.WorldSnapshot), sent);
        Assert.DoesNotContain(sent, s => s.Peer == 101 && s.Id == PacketId.WorldSnapshot);
    }
}
