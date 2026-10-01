using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using ProjectH.Server.Game;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Tests.Game;

// Phase 8 D3: a snapshot of more than MaxEntitiesPerSnapshotPacket players goes out as several packets of the same tick.
public class SnapshotSplitTests
{
    private sealed record Sent(int PeerId, byte[] Data, DeliveryMethod Method);

    private static (Match match, List<Sent> sent) MatchWith(int players)
    {
        var sent = new List<Sent>();
        var match = new Match(new ServerOptions { MaxPlayers = ProtocolConstants.MaxSnapshotEntities, DevRespawn = true }, TestGameData.Create(),
            (peer, data, method) => { if ((PacketId)data[0] == PacketId.WorldSnapshot) sent.Add(new Sent(peer, data.ToArray(), method)); },
            lootPoints: System.Array.Empty<LootPoint>());
        for (int peer = 1; peer <= players; peer++) Assert.Equal(JoinResult.Ok, match.TryJoin(peer, "p" + peer));
        return (match, sent);
    }

    private static (WorldSnapshotHeader header, List<SnapshotEntity> entities) Read(Sent s)
    {
        var reader = new PacketReader(s.Data);
        Assert.True(reader.TryReadPacketId(out _));
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var header));
        var entities = new List<SnapshotEntity>();
        for (int i = 0; i < header.Count; i++)
        {
            Assert.True(SnapshotEntity.TryRead(ref reader, out var e));
            entities.Add(e);
        }
        Assert.Equal(0, reader.Remaining);
        return (header, entities);
    }

    [Fact]
    public void AHundredPlayers_GetTwoPacketsOfTheSameTick_ThatAddUpToEveryone()
    {
        (Match match, List<Sent> sent) = MatchWith(ProtocolConstants.MaxSnapshotEntities);
        // Ack and Self are per recipient: give every player its own values before the snapshot (no input is queued, so
        // the ticks leave LastProcessedSeq alone).
        for (int peer = 1; peer <= ProtocolConstants.MaxSnapshotEntities; peer++)
        {
            Assert.True(match.TryGetPlayer(peer, out PlayerEntity p));
            p.Health = 1 + peer % 100;
            p.LastProcessedSeq = (uint)(1000 + peer * 7);
        }
        match.Tick();
        match.Tick();   // SnapshotEveryTicks = 2: one snapshot within two ticks
        Assert.NotEmpty(sent);
        var acks = new HashSet<uint>();
        var healths = new HashSet<int>();

        foreach (IGrouping<int, Sent> perPeer in sent.GroupBy(s => s.PeerId))
        {
            List<(WorldSnapshotHeader header, List<SnapshotEntity> entities)> packets = perPeer.Select(Read).ToList();
            Assert.Equal(2, packets.Count);
            Assert.All(perPeer, s => Assert.Equal(DeliveryMethod.Sequenced, s.Method));
            Assert.All(perPeer, s => Assert.True(s.Data.Length <= ProtocolConstants.MaxPacketSize));
            Assert.Equal(packets[0].header.ServerTick, packets[1].header.ServerTick);
            Assert.Equal(new[] { 0, 1 }, packets.Select(p => (int)p.header.Part));
            Assert.All(packets, p => Assert.Equal(2, p.header.PartCount));
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshotPacket, packets[0].header.Count);
            Assert.Equal(ProtocolConstants.MaxSnapshotEntities - ProtocolConstants.MaxEntitiesPerSnapshotPacket, packets[1].header.Count);
            var ids = packets.SelectMany(p => p.entities).Select(e => e.EntityId).ToList();
            Assert.Equal(ProtocolConstants.MaxSnapshotEntities, ids.Distinct().Count());
            Assert.True(match.TryGetPlayer(perPeer.Key, out PlayerEntity me));
            Assert.Contains(me.EntityId, ids);
            Assert.All(packets, p => Assert.Equal(me.LastProcessedSeq, p.header.AckInputSeq));   // the ack is the recipient's own
            Assert.All(packets, p => Assert.Equal(me.Health, p.header.Self.Health));            // so is the self block
            acks.Add(packets[0].header.AckInputSeq);
            healths.Add(packets[0].header.Self.Health);
        }
        Assert.True(acks.Count >= 2);
        Assert.True(healths.Count >= 2);
    }

    [Fact]
    public void UpToNinetyPlayers_StillGetOnePacket()
    {
        (Match match, List<Sent> sent) = MatchWith(ProtocolConstants.MaxEntitiesPerSnapshotPacket);
        match.Tick();
        match.Tick();
        foreach (IGrouping<int, Sent> perPeer in sent.GroupBy(s => s.PeerId))
        {
            var (header, entities) = Read(Assert.Single(perPeer));
            Assert.Equal(0, header.Part);
            Assert.Equal(1, header.PartCount);
            Assert.Equal(ProtocolConstants.MaxEntitiesPerSnapshotPacket, entities.Count);
        }
    }

    [Fact]
    public void SendingASplitSnapshot_AllocatesNothing()
    {
        var match = new Match(new ServerOptions { MaxPlayers = ProtocolConstants.MaxSnapshotEntities, DevRespawn = true }, TestGameData.Create(),
            static (_, _, _) => { }, lootPoints: System.Array.Empty<LootPoint>());
        for (int peer = 1; peer <= ProtocolConstants.MaxSnapshotEntities; peer++) match.TryJoin(peer, "p" + peer);
        for (int i = 0; i < 10; i++) match.Tick();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++) match.Tick();
        Assert.Equal(before, System.GC.GetAllocatedBytesForCurrentThread());
    }
}
