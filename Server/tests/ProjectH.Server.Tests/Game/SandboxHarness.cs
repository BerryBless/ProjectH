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

// Phase 13: a dev-sandbox Match (DevRespawn, damage always allowed) driven tick by tick for the harvest and build rule
// tests. Every sent packet is recorded with its delivery method.
internal sealed class SandboxHarness
{
    public sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private readonly Dictionary<int, uint> _seq = new();

    public SandboxHarness(StartingLoadout? loadout = null, ServerOptions? options = null, GameData? data = null)
    {
        options ??= new ServerOptions { MaxPlayers = 8, DevRespawn = true };
        Match = new Match(options, data ?? TestGameData.Create(), (peer, bytes, method) => Packets.Add(new Sent(peer, bytes.ToArray(), method)),
            loadout ?? TestGameData.CombatLoadout);
    }

    public Match Match { get; }
    public List<Sent> Packets { get; } = new();

    public static Vector3 Ground(float x, float z) => new(x, GameMap.Terrain.Height(x, z), z);

    public PlayerEntity Join(int peer, Vector3 feet, float yaw = 0f)
    {
        Assert.Equal(JoinResult.Ok, Match.TryJoin(peer, "p" + peer));
        Match.TryGetPlayer(peer, out var player);
        Place(player, feet, yaw);
        return player;
    }

    public void Place(PlayerEntity player, Vector3 feet, float yaw = 0f)
    {
        player.State.Position = feet;
        player.State.Yaw = yaw;
        player.History.Reset(Match.ServerTick, feet);
    }

    // One input for the next tick (Seq numbered per player).
    public void Send(PlayerEntity player, InputCommand command)
    {
        _seq.TryGetValue(player.PeerId, out uint seq);
        command.Seq = ++seq;
        _seq[player.PeerId] = seq;
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, command);
        Match.EnqueueInput(player.PeerId, packet);
    }

    // An input with these buttons, aimed from the player's eye at a world point, then one tick.
    public void Act(PlayerEntity player, InputButtons buttons, Vector3 aimAt)
    {
        TestAim.YawPitch(player.State.Position, aimAt, out float yaw, out float pitch);
        Send(player, new InputCommand { Buttons = buttons, Yaw = player.State.Yaw, AimYaw = yaw, AimPitch = pitch });
        Match.Tick();
    }

    public void Press(PlayerEntity player, InputButtons buttons)
    {
        Send(player, new InputCommand { Buttons = buttons, Yaw = player.State.Yaw });
        Match.Tick();
    }

    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++) Match.Tick();
    }

    public IEnumerable<Sent> To(int peer, PacketId id) => Packets.Where(p => p.PeerId == peer && p.Id == id);

    public static PacketReader Body(Sent sent)
    {
        var reader = new PacketReader(sent.Data);
        Assert.True(reader.TryReadPacketId(out _));
        return reader;
    }

    public void Clear() => Packets.Clear();
}
