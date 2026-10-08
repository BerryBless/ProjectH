using System.Linq;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 D2 over real UDP: a client that crashes during the match comes back to its own character; a kicked one
// does not get the grace.
public sealed class ReconnectIntegrationTests
{
    private static bool InMatch(HeadlessClient c) =>
        c.MatchStates.Count > 0 && c.MatchStates[^1].State is MatchFlowState.Playing or MatchFlowState.FinalPhase;

    [Fact]
    public void ACrashedClient_RejoinsItsCharacter_AndTheOthersNeverSawItLeave()
    {
        using GameLoop server = StartServer(countdown: 1);
        var a = Join(server, "a");
        try
        {
            using var b = Join(server, "b");
            Assert.True(Pump.Until(() => InMatch(a) && InMatch(b), 5000, a, b), "match running");
            ushort entity = a.MyEntityId;

            a.Kill();   // no disconnect message: the server finds out by its 1 s timeout
            Assert.True(Pump.Until(() => server.Health.GraceStarts == 1, 4000, b), "graced");

            using var back = Join(server, "a", expected: JoinResult.Resumed);
            Assert.Equal(entity, back.MyEntityId);
            Assert.True(Pump.Until(() => back.LastSnapshot.ContainsKey(entity) && back.Spawned.Contains(b.MyEntityId) &&
                                         back.Inventories.Count > 0 && back.MatchStates.Count > 0, 3000, back, b), "full state");
            Assert.True(InMatch(back));
            Assert.DoesNotContain(entity, b.Despawned);
            Assert.Equal(1, server.Health.Resumes);
            Assert.Equal(0, server.Health.GraceExpiries);

            // Its new inputs count from 1 and are acknowledged.
            Assert.True(SendInputsUntil(back, () => back.LastAckInputSeq >= 5, 3000, b), "inputs taken");
        }
        finally
        {
            a.Dispose();   // killed above, but a failed assertion before the kill must not leak its socket
        }
    }

    // Review fix B4 (SEC-2): another client with the same name (no resume key) cannot take the graced character, and holding
    // the name does not keep the owner out: the owner's client resumes with its proof while the impostor is connected.
    [Fact]
    public void AnotherClient_WithTheSameName_CannotTakeTheGracedCharacter()
    {
        using GameLoop server = StartServer(countdown: 1);
        var a = Join(server, "a");
        try
        {
            using var b = Join(server, "b");
            Assert.True(Pump.Until(() => InMatch(a) && InMatch(b), 5000, a, b), "match running");
            ushort entity = a.MyEntityId;
            a.Kill();
            Assert.True(Pump.Until(() => server.Health.GraceStarts == 1, 4000, b), "graced");

            using var impostor = new HeadlessClient();
            impostor.Connect(server.LocalPort, "a", resume: false);
            Assert.True(Pump.Until(() => impostor.Connected, 3000, impostor), "impostor connected");
            impostor.SendJoin();
            Assert.True(Pump.Until(() => impostor.JoinResponse.HasValue, 3000, impostor), "impostor joined");
            Assert.Equal(JoinResult.Ok, impostor.JoinResponse?.Result);
            Assert.NotEqual(entity, impostor.MyEntityId);
            Assert.Equal(0, server.Health.Resumes);

            using var back = Join(server, "a", expected: JoinResult.Resumed);
            Assert.True(back.SentResumeProof);
            Assert.Equal(entity, back.MyEntityId);
            Assert.Equal(1, server.Health.Resumes);
        }
        finally
        {
            a.Dispose();
        }
    }

    // Phase 12 D16: a client that crashes while riding the drop transport comes back to the same route, and its next
    // snapshot shows where the server's own steps (empty input) took it meanwhile.
    [Fact]
    public void ACrashedRider_ComesBack_WithTheRoute_AndItsMode()
    {
        using GameLoop server = StartServer(countdown: 1);
        var a = Join(server, "a");
        try
        {
            using var b = Join(server, "b");
            Assert.True(Pump.Until(() => InMatch(a) && InMatch(b) && a.TransportRoutes.Count == 1, 5000, a, b), "aboard");
            ushort entity = a.MyEntityId;

            a.Kill();
            Assert.True(Pump.Until(() => server.Health.GraceStarts == 1, 4000, b), "graced");

            using var back = Join(server, "a", expected: JoinResult.Resumed);
            Assert.True(Pump.Until(() => back.TransportRoutes.Count == 1 && back.LastSnapshot.ContainsKey(entity), 3000, back, b), "route and snapshot");
            Assert.Equal(a.TransportRoutes[0], back.TransportRoutes[0]);
            Assert.Contains(back.LastSnapshot[entity].Mode, new[] { MovementMode.Transport, MovementMode.Freefall, MovementMode.Glide });
        }
        finally
        {
            a.Dispose();
        }
    }

    // B2: a grace that runs out is counted (Health line, Meter).
    [Fact]
    public void AGraceThatRunsOut_IsCounted()
    {
        using GameLoop server = StartServer(countdown: 1, grace: 1);
        using var a = Join(server, "a");
        using var b = Join(server, "b");
        using var c = Join(server, "c");
        Assert.True(Pump.Until(() => InMatch(a) && InMatch(b) && InMatch(c), 5000, a, b, c), "match running");

        a.Kill();
        Assert.True(Pump.Until(() => server.Health.GraceExpiries == 1, 5000, b, c), "grace expired");
        Assert.Equal(1, server.Health.GraceStarts);
        Assert.True(Pump.Until(() => b.Despawned.Contains(a.MyEntityId), 3000, b, c), "despawned for the others");
    }

    // B12: a Join the match refuses (MatchFull: the graced player keeps its slot) does not make a joined peer. The
    // client gets the MatchFull response, then the server closes the connection without a code (not retried).
    [Fact]
    public void ARefusedJoin_GetsMatchFull_AndIsClosedWithoutACode()
    {
        using GameLoop server = StartServer(maxPlayers: 2, countdown: 1);
        var a = Join(server, "a");
        try
        {
            using var b = Join(server, "b");
            Assert.True(Pump.Until(() => InMatch(a) && InMatch(b), 5000, a, b), "match running");
            a.Kill();
            Assert.True(Pump.Until(() => server.Health.GraceStarts == 1, 4000, b), "graced");

            using var c = Join(server, "c", expected: JoinResult.MatchFull);
            Assert.True(Pump.Until(() => c.Disconnected, 4000, c, b), "closed");
            Assert.Equal(LiteNetLib.DisconnectReason.RemoteConnectionClose, c.DisconnectReason);
            Assert.Equal(DisconnectCode.None, c.DisconnectCode);
            Assert.False(DisconnectCodes.ShouldReconnect(remoteClose: true, c.DisconnectCode, networkLoss: false));
            Assert.Equal(2, server.Health.Joins);
            Assert.Equal(0, server.Health.Kicks(DisconnectCode.JoinTimeout));
            Assert.Equal(0, server.Health.Kicks(DisconnectCode.InputTimeout));
            Assert.False(b.Disconnected);
        }
        finally
        {
            a.Dispose();
        }
    }

    [Fact]
    public void AKickedClient_GetsNoGrace()
    {
        using GameLoop server = StartServer(countdown: 1);
        using var a = Join(server, "a");
        using var b = Join(server, "b");
        Assert.True(Pump.Until(() => InMatch(a) && InMatch(b), 5000, a, b), "match running");

        for (int i = 0; i < 25; i++) a.SendRaw(new byte[] { 0xFF });
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a, b), "kicked");
        Assert.True(Pump.Until(() => b.Despawned.Contains(a.MyEntityId), 3000, b), "despawned at once");
        Assert.Equal(0, server.Health.GraceStarts);
        // a was eliminated at once, so b is the last one in.
        Assert.True(Pump.Until(() => b.MatchStates.Any(s => s.State == MatchFlowState.Finished), 3000, b), "match over");
    }
}
