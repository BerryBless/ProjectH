using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// A battle royale Match (no DevRespawn) driven tick by tick, for the Phase 5 rule tests. The countdown and the
// result screen are 1 s (30 ticks) so tests reach every state quickly. Every sent packet is recorded.
// Phase 12: airDrop false (the default here) starts matches on the drop points as in Phases 5-11, so the rule tests
// stay about their rules; the deployment tests pass true (ServerOptions.AirDrop, on in production).
internal sealed class RoyaleHarness
{
    public sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    public const int CountdownTicks = 30;
    public const int ResultTicks = 30;
    public static readonly Vector3 Chest = new(0f, 1.2f, 0f);

    // Phase 6 spec interpretation 8: the default drop points are the six lobby ring spots in the plaza, so the
    // Phase 5 rule tests still start close enough to shoot each other, inside the small test zone.
    public static readonly Vector3[] LobbyRingDrops = Enumerable.Range(1, 6).Select(id => Match.SpawnPosition((ushort)id)).ToArray();

    private readonly Dictionary<int, uint> _seq = new();

    // record false: sent packets are dropped instead of copied, for allocation tests.
    public RoyaleHarness(StartingLoadout? loadout = null, string zonesJson = TestGameData.ZonesJson, int maxPlayers = 6,
        int minPlayers = 2, string lootJson = TestGameData.LootJson, bool record = true,
        Vector3[]? dropPoints = null, Action<ProjectH.Server.Persistence.MatchRecord>? matchSink = null,
        int reconnectGraceSeconds = 10, Action<string>? graceExpired = null, bool airDrop = false)
    {
        SendPacket send = record ? (peer, data, method) => Packets.Add(new Sent(peer, data.ToArray(), method)) : static (_, _, _) => { };
        Match = new Match(new ServerOptions
            {
                MaxPlayers = maxPlayers, MinPlayers = minPlayers, StartCountdownSeconds = 1, ResultSeconds = 1,
                ReconnectGraceSeconds = reconnectGraceSeconds, AirDrop = airDrop,
            },
            TestGameData.Create(lootJson: lootJson, zonesJson: zonesJson), send, loadout, dropPoints: dropPoints ?? LobbyRingDrops,
            matchSink: matchSink, graceExpired: graceExpired);
    }

    public Match Match { get; }
    public List<Sent> Packets { get; } = new();

    public PlayerEntity Join(int peer)
    {
        Assert.Equal(JoinResult.Ok, Match.TryJoin(peer, "p" + peer));
        Match.TryGetPlayer(peer, out var player);
        return player;
    }

    // Ticks until the match is running (Playing or FinalPhase).
    public void RunToMatch() => TickUntil(() => Match.Flow.InMatch, 2 * CountdownTicks + 5);

    public void TickUntil(Func<bool> condition, int maxTicks)
    {
        for (int i = 0; i < maxTicks && !condition(); i++) Match.Tick();
        Assert.True(condition(), $"condition not reached within {maxTicks} ticks (state {Match.Flow.State})");
    }

    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++) Match.Tick();
    }

    // Moves a player (and its lag compensation history) to feet.
    public void Place(PlayerEntity player, Vector3 feet)
    {
        player.State.Position = feet;
        player.History.Reset(Match.ServerTick, feet);
    }

    public void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        // Continue after the client's last seq (a respawn or a match start keeps it).
        seq = Math.Max(seq, player.LastProcessedSeq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        Match.EnqueueInput(player.PeerId, packet);
    }

    // One tick with the shooter firing at the target's chest (as rendered now).
    public void ShootOnce(PlayerEntity shooter, PlayerEntity target)
    {
        TestAim.YawPitch(shooter.State.Position, target.State.Position + Chest, out float yaw, out float pitch);
        Send(shooter, new InputCommand { Buttons = InputButtons.Fire, AimYaw = yaw, AimPitch = pitch, ViewTick = Match.ServerTick });
        Match.Tick();
    }

    // Fires until the target is dead (the combat loadout's automatic weapon: 30 damage every 3 ticks).
    public void ShootUntilDead(PlayerEntity shooter, PlayerEntity target, int maxTicks = 120)
    {
        for (int i = 0; i < maxTicks && target.Alive; i++) ShootOnce(shooter, target);
        Assert.False(target.Alive, "target still alive");
    }

    public List<Sent> SentTo(int peer, PacketId id) => Packets.Where(s => s.PeerId == peer && s.Id == id).ToList();

    public static PacketReader Reader(Sent s)
    {
        var reader = new PacketReader(s.Data);
        reader.TryReadPacketId(out _);
        return reader;
    }

    public static PlayerDied ReadDied(Sent s) { var r = Reader(s); Assert.True(PlayerDied.TryRead(ref r, out var v)); return v; }
    public static PlayerRespawned ReadRespawned(Sent s) { var r = Reader(s); Assert.True(PlayerRespawned.TryRead(ref r, out var v)); return v; }
    public static ShotFired ReadShot(Sent s) { var r = Reader(s); Assert.True(ShotFired.TryRead(ref r, out var v)); return v; }
    public static ItemRemoved ReadRemoved(Sent s) { var r = Reader(s); Assert.True(ItemRemoved.TryRead(ref r, out var v)); return v; }
}
