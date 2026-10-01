using System;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 10 §2: seeded inputs full of hostile values (NaN, ±Infinity, huge numbers, random Seq and ViewTick, every
// button) through the real wire parser into Match for hundreds of ticks. Nothing may throw, and every position and
// health value must stay finite and in range.
public class InputFuzzTests
{
    private static readonly float[] Hostile =
    {
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, 1e30f, -1e30f,
        float.Epsilon, -0f, 0f, 1f, -1f, 360f, -720f, 1e6f,
    };

    private static float Value(Random random) =>
        random.Next(3) == 0 ? (float)(random.NextDouble() * 4 - 2) : Hostile[random.Next(Hostile.Length)];

    // Through PlayerInputPacket.Write and TryRead, as NetworkListener sees it (unknown buttons are masked there).
    // Seq: mostly the next one (so most inputs are taken and acted on), sometimes old or repeated ones; near the end
    // also the top of the uint range ("negative" Seq), after which this player's normal inputs are refused.
    private static PlayerInputPacket RandomPacket(Random random, byte[] buffer, ref uint nextSeq, bool endgame)
    {
        var packet = new PlayerInputPacket { Count = (byte)random.Next(1, ProtocolConstants.MaxInputsPerPacket + 1) };
        for (int i = 0; i < packet.Count; i++)
        {
            packet.Set(i, new InputCommand
            {
                Seq = endgame && random.Next(20) == 0 ? uint.MaxValue - (uint)random.Next(3)
                    : random.Next(5) == 0 ? nextSeq - (uint)Math.Min(nextSeq, (uint)random.Next(10))
                    : nextSeq++,
                MoveX = Value(random),
                MoveY = Value(random),
                Yaw = Value(random),
                Buttons = (InputButtons)random.Next(ushort.MaxValue + 1),
                AimYaw = Value(random),
                AimPitch = Value(random),
                ViewTick = Value(random),
            });
        }
        var writer = new PacketWriter(buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = new PacketReader(writer.WrittenSpan);
        Assert.True(reader.TryReadPacketId(out _));
        Assert.True(PlayerInputPacket.TryRead(ref reader, out PlayerInputPacket read));
        return read;
    }

    private static void AssertSane(Match match, int peers)
    {
        for (int peer = 1; peer <= peers; peer++)
        {
            if (!match.TryGetPlayer(peer, out PlayerEntity p)) continue;
            Assert.True(float.IsFinite(p.State.Position.X) && float.IsFinite(p.State.Position.Y) && float.IsFinite(p.State.Position.Z),
                $"position {p.State.Position} at tick {match.ServerTick}");
            Assert.True(float.IsFinite(p.State.VelocityY) && float.IsFinite(p.State.Yaw), $"velocity/yaw at tick {match.ServerTick}");
            Assert.InRange(p.Health, 0, 100);
            Assert.InRange(p.Shield, 0, 100);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void HostileInputs_NeverBreakTheMatch(bool devRespawn, int seed)
    {
        const int players = 4;
        var random = new Random(seed);
        var buffer = new byte[ProtocolConstants.MaxPacketSize];
        var match = new Match(new ServerOptions
            {
                MaxPlayers = players, MinPlayers = 2, StartCountdownSeconds = 1, ResultSeconds = 1, DevRespawn = devRespawn,
            },
            TestGameData.Create(zonesJson: TestGameData.ShortZonesJson), static (_, _, _) => { }, TestGameData.CombatLoadout,
            dropPoints: RoyaleHarness.LobbyRingDrops);
        for (int peer = 1; peer <= players; peer++) Assert.Equal(JoinResult.Ok, match.TryJoin(peer, "f" + peer));
        var nextSeq = new uint[players + 1];
        Array.Fill(nextSeq, 1u);

        uint mostTaken = 0;
        bool reachedPlaying = false;
        for (int tick = 0; tick < 900; tick++)
        {
            for (int peer = 1; peer <= players; peer++)
            {
                if (random.Next(4) != 0) match.EnqueueInput(peer, RandomPacket(random, buffer, ref nextSeq[peer], tick > 800));
            }
            match.Tick();
            reachedPlaying |= match.Flow.State == MatchFlowState.Playing;
            AssertSane(match, players);
            for (int peer = 1; peer <= players; peer++)
            {
                if (match.TryGetPlayer(peer, out PlayerEntity p) && p.LastProcessedSeq < uint.MaxValue - 3)
                    mostTaken = Math.Max(mostTaken, p.LastProcessedSeq);
            }
        }
        Assert.True(mostTaken > 300, $"the hostile inputs were hardly taken ({mostTaken})");
        // The battle royale run must really have played (damage, zone, deaths); the sandbox has no flow states.
        if (!devRespawn) Assert.True(reachedPlaying, "the match never reached Playing");
    }
}
