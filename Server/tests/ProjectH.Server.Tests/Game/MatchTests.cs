using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

public class MatchTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method)
    {
        public PacketId Id => (PacketId)Data[0];
    }

    private readonly List<Sent> _sent = new();
    private readonly Match _match;

    public MatchTests()
    {
        _match = new Match(new ServerOptions { MaxPlayers = 3, SnapshotEveryTicks = 2 }, TestWeapons.Create(),
            (peer, data, method) => _sent.Add(new Sent(peer, data.ToArray(), method)));
    }

    private static PlayerInputPacket Inputs(params InputCommand[] commands)
    {
        var packet = new PlayerInputPacket { Count = (byte)commands.Length };
        for (int i = 0; i < commands.Length; i++) packet.Set(i, commands[i]);
        return packet;
    }

    private static InputCommand Forward(uint seq) => new InputCommand { Seq = seq, MoveY = 1f };

    private ushort SpawnedEntityIdFor(int recipientPeer, int index)
    {
        var spawns = _sent.Where(s => s.PeerId == recipientPeer && s.Id == PacketId.PlayerSpawned).ToList();
        var reader = new PacketReader(spawns[index].Data);
        reader.TryReadPacketId(out _);
        PlayerSpawned.TryRead(ref reader, out var spawned);
        return spawned.EntityId;
    }

    private (WorldSnapshotHeader header, List<SnapshotEntity> entities) LastSnapshotFor(int peer)
    {
        var data = _sent.Last(s => s.PeerId == peer && s.Id == PacketId.WorldSnapshot).Data;
        var reader = new PacketReader(data);
        reader.TryReadPacketId(out _);
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var header));
        var list = new List<SnapshotEntity>();
        for (int i = 0; i < header.Count; i++)
        {
            SnapshotEntity.TryRead(ref reader, out var e);
            list.Add(e);
        }
        return (header, list);
    }

    [Fact]
    public void Join_SendsResponse_AndSpawnsPlayersToEachOther()
    {
        Assert.Equal(JoinResult.Ok, _match.TryJoin(1, "a"));
        Assert.Equal(JoinResult.Ok, _match.TryJoin(2, "b"));

        Assert.Contains(_sent, s => s.PeerId == 1 && s.Id == PacketId.JoinMatchResponse && s.Method == DeliveryMethod.ReliableOrdered);
        // Peer 1: own spawn, then spawn of peer 2. Peer 2: spawns of 1 and 2.
        Assert.Equal(2, _sent.Count(s => s.PeerId == 1 && s.Id == PacketId.PlayerSpawned));
        Assert.Equal(2, _sent.Count(s => s.PeerId == 2 && s.Id == PacketId.PlayerSpawned));
        Assert.Equal(2, _match.PlayerCount);
    }

    [Fact]
    public void Join_SendsWeaponCatalog_AfterResponse_BeforeSpawns()
    {
        _match.TryJoin(1, "a");

        var toPeer = _sent.Where(s => s.PeerId == 1).ToList();
        Assert.Equal(PacketId.JoinMatchResponse, toPeer[0].Id);
        Assert.Equal(PacketId.WeaponCatalog, toPeer[1].Id);
        Assert.Equal(DeliveryMethod.ReliableOrdered, toPeer[1].Method);
        Assert.Equal(PacketId.PlayerSpawned, toPeer[2].Id);

        var reader = new PacketReader(toPeer[1].Data);
        reader.TryReadPacketId(out _);
        Assert.True(WeaponCatalogPacket.TryRead(ref reader, out var weapons));
        Assert.Equal("Test Auto", weapons[0].Name);
        Assert.Equal(TestWeapons.AutoInterval, weapons[0].FireIntervalTicks);
    }

    [Fact]
    public void Constructor_RejectsCatalogBuiltForOtherSimHz()
    {
        Assert.Throws<ArgumentException>(() =>
            new Match(new ServerOptions { SimHz = 60 }, TestWeapons.Create(simHz: 30), (_, _, _) => { }));
    }

    [Fact]
    public void Join_Twice_IsIgnored()
    {
        _match.TryJoin(1, "a");
        int sentBefore = _sent.Count;
        Assert.Equal(JoinResult.AlreadyJoined, _match.TryJoin(1, "a"));
        Assert.Equal(sentBefore, _sent.Count);
        Assert.Equal(1, _match.PlayerCount);
    }

    [Fact]
    public void Join_BeyondMaxPlayers_ReturnsMatchFull()
    {
        _match.TryJoin(1, "a");
        _match.TryJoin(2, "b");
        _match.TryJoin(3, "c");
        Assert.Equal(JoinResult.MatchFull, _match.TryJoin(4, "d"));
        Assert.Equal(3, _match.PlayerCount);
        Assert.Contains(_sent, s => s.PeerId == 4 && s.Id == PacketId.JoinMatchResponse);
    }

    [Fact]
    public void Leave_BroadcastsDespawn_ToRemainingPlayers()
    {
        _match.TryJoin(1, "a");
        _match.TryJoin(2, "b");
        ushort entityOf1 = SpawnedEntityIdFor(1, 0);

        _match.Leave(1);

        var despawn = _sent.Last(s => s.PeerId == 2 && s.Id == PacketId.PlayerDespawned);
        var reader = new PacketReader(despawn.Data);
        reader.TryReadPacketId(out _);
        PlayerDespawned.TryRead(ref reader, out var d);
        Assert.Equal(entityOf1, d.EntityId);
        Assert.Equal(1, _match.PlayerCount);
    }

    [Fact]
    public void Tick_AppliesAtMostOneInputPerTick()
    {
        _match.TryJoin(1, "a");
        _match.TryGetPlayer(1, out var player);
        float startZ = player.State.Position.Z;

        // A client flooding 3 inputs at once must still move only one step per tick.
        _match.EnqueueInput(1, Inputs(Forward(1), Forward(2), Forward(3)));
        _match.Tick();

        float oneStep = MoveSettings.WalkSpeed / 30f;
        Assert.Equal(startZ + oneStep, player.State.Position.Z, 4);
        Assert.Equal(1u, player.LastProcessedSeq);
    }

    [Fact]
    public void DuplicateInputs_AreAppliedOnce()
    {
        _match.TryJoin(1, "a");
        var packet = Inputs(Forward(1), Forward(2), Forward(3));
        _match.EnqueueInput(1, packet);
        _match.EnqueueInput(1, packet);   // redundant resend

        for (int i = 0; i < 3; i++) _match.Tick();
        _match.TryGetPlayer(1, out var player);
        Assert.Equal(3u, player.LastProcessedSeq);
        Assert.Equal(0, player.Inputs.Count);

        _match.Tick();   // no new input: ack must not advance
        Assert.Equal(3u, player.LastProcessedSeq);
    }

    [Fact]
    public void Tick_WithoutInput_RepeatsMovement_ButNotJump()
    {
        _match.TryJoin(1, "a");
        _match.EnqueueInput(1, Inputs(new InputCommand { Seq = 1, MoveY = 1f, Buttons = InputButtons.Jump }));
        _match.Tick();
        _match.TryGetPlayer(1, out var player);

        // Grace window (SimHz / 2 = 15 missed ticks): the last movement repeats every tick.
        for (int i = 0; i < 15; i++)
        {
            float zBefore = player.State.Position.Z;
            _match.Tick();
            Assert.True(player.State.Position.Z > zBefore);
        }

        // After the grace window the player stops walking (a paused client must not keep moving).
        // Keep ticking long enough to land: no second jump either.
        float zAfterGrace = player.State.Position.Z;
        for (int i = 0; i < 45; i++) _match.Tick();
        Assert.Equal(zAfterGrace, player.State.Position.Z);
        Assert.Equal(0f, player.State.Position.Y);
        Assert.Equal(1u, player.LastProcessedSeq);

        // A new input resumes movement and advances the ack.
        _match.EnqueueInput(1, Inputs(Forward(2)));
        _match.Tick();
        Assert.True(player.State.Position.Z > zAfterGrace);
        Assert.Equal(2u, player.LastProcessedSeq);

        // The miss counter was reset: the next missed tick repeats movement again.
        float zAfterResume = player.State.Position.Z;
        _match.Tick();
        Assert.True(player.State.Position.Z > zAfterResume);
    }

    [Fact]
    public void NonFiniteInput_KeepsStateFinite()
    {
        _match.TryJoin(1, "a");
        _match.EnqueueInput(1, Inputs(new InputCommand { Seq = 1, MoveX = float.NaN, MoveY = float.PositiveInfinity, Yaw = float.NegativeInfinity }));
        _match.Tick();
        _match.TryGetPlayer(1, out var player);
        Assert.True(float.IsFinite(player.State.Position.X));
        Assert.True(float.IsFinite(player.State.Position.Z));
        Assert.True(float.IsFinite(player.State.Yaw));
    }

    [Fact]
    public void Snapshot_IsSentEveryNTicks_WithPerRecipientAck()
    {
        _match.TryJoin(1, "a");
        _match.TryJoin(2, "b");
        _match.EnqueueInput(1, Inputs(Forward(1)));
        _sent.Clear();

        _match.Tick();
        Assert.DoesNotContain(_sent, s => s.Id == PacketId.WorldSnapshot);

        _match.Tick();
        var (h1, e1) = LastSnapshotFor(1);
        var (h2, _) = LastSnapshotFor(2);
        Assert.Equal(2u, h1.ServerTick);
        Assert.Equal(1u, h1.AckInputSeq);
        Assert.Equal(0u, h2.AckInputSeq);
        Assert.Equal(2, e1.Count);
        Assert.All(_sent.Where(s => s.Id == PacketId.WorldSnapshot), s => Assert.Equal(DeliveryMethod.Sequenced, s.Method));
    }
}
