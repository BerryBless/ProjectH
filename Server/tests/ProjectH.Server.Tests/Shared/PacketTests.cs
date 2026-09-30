using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class PacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    private static InputCommand FullCommand(uint seq) => new InputCommand
    {
        Seq = seq,
        MoveX = 0.5f,
        MoveY = -1f,
        Yaw = 90f + seq,
        Buttons = InputButtons.Jump | InputButtons.Fire | InputButtons.Slot2,
        AimYaw = 12.5f + seq,
        AimPitch = -30f,
        ViewTick = 1000.25f + seq,
    };

    [Fact]
    public void ConnectRequestData_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = 1, DevPlayerId = "abc" });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(ConnectRequestData.TryRead(ref reader, out var data));
        Assert.Equal((ushort)1, data.ProtocolVersion);
        Assert.Equal("abc", data.DevPlayerId);
    }

    [Fact]
    public void ConnectRequestData_EmptyId_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = 1, DevPlayerId = "" });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.False(ConnectRequestData.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerInput_RoundTrip_KeepsOrderAndAimFields()
    {
        var packet = new PlayerInputPacket { Count = 3 };
        for (int i = 0; i < 3; i++) packet.Set(i, FullCommand((uint)(10 + i)));

        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        Assert.Equal(PlayerInputPacket.MaxSize, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out var read));

        Assert.Equal(3, read.Count);
        for (int i = 0; i < 3; i++)
        {
            InputCommand expected = FullCommand((uint)(10 + i));
            InputCommand actual = read.Get(i);
            Assert.Equal(expected.Seq, actual.Seq);
            Assert.Equal(expected.Yaw, actual.Yaw);
            Assert.Equal(expected.Buttons, actual.Buttons);
            Assert.Equal(expected.AimYaw, actual.AimYaw);
            Assert.Equal(expected.AimPitch, actual.AimPitch);
            Assert.Equal(expected.ViewTick, actual.ViewTick);
        }
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void PlayerInput_Sizes_ArePinned()
    {
        // 29 bytes per command; the largest input packet (3 commands) stays small (D2).
        Assert.Equal(29, PlayerInputPacket.CommandSize);
        Assert.Equal(89, PlayerInputPacket.MaxSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void PlayerInput_InvalidCount_IsRejected(byte count)
    {
        // Enough payload for 4 commands, so only the count rule can reject it.
        var bytes = new byte[2 + PlayerInputPacket.CommandSize * 4];
        bytes[0] = (byte)PacketId.PlayerInput;
        bytes[1] = count;
        var reader = new PacketReader(bytes);
        reader.TryReadPacketId(out _);
        Assert.False(PlayerInputPacket.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 17)]    // a Phase 1 sized command is now one command short
    [InlineData(1, 28)]
    [InlineData(2, 29)]
    [InlineData(3, 86)]
    public void PlayerInput_Truncated_IsRejected(byte count, int payloadBytes)
    {
        var bytes = new byte[2 + payloadBytes];
        bytes[0] = (byte)PacketId.PlayerInput;
        bytes[1] = count;
        var reader = new PacketReader(bytes);
        reader.TryReadPacketId(out _);
        Assert.False(PlayerInputPacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerInput_UnknownButtonBits_AreMasked()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = (InputButtons)0xFF });
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out var read));
        Assert.Equal(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire | InputButtons.Reload |
                     InputButtons.Slot1 | InputButtons.Slot2, read.Get(0).Buttons);
    }

    [Fact]
    public void JoinMatchResponse_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        JoinMatchResponse.Write(ref writer, new JoinMatchResponse { Result = JoinResult.Ok, MyEntityId = 5, ServerTick = 99, SimHz = 30, SnapshotHz = 15 });
        var reader = ReaderAfterId(writer.Length, PacketId.JoinMatchResponse);
        Assert.True(JoinMatchResponse.TryRead(ref reader, out var r));
        Assert.Equal(JoinResult.Ok, r.Result);
        Assert.Equal(5, r.MyEntityId);
        Assert.Equal(99u, r.ServerTick);
        Assert.Equal(30, r.SimHz);
        Assert.Equal(15, r.SnapshotHz);
    }

    [Fact]
    public void SpawnAndDespawn_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 3, Position = new Vector3(1, 0, 2), Yaw = 45f });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.True(PlayerSpawned.TryRead(ref reader, out var s));
        Assert.Equal(3, s.EntityId);
        Assert.Equal(new Vector3(1, 0, 2), s.Position);

        writer = new PacketWriter(_buffer);
        PlayerDespawned.Write(ref writer, new PlayerDespawned { EntityId = 3 });
        reader = ReaderAfterId(writer.Length, PacketId.PlayerDespawned);
        Assert.True(PlayerDespawned.TryRead(ref reader, out var d));
        Assert.Equal(3, d.EntityId);
    }

    [Fact]
    public void Snapshot_RoundTrip_AndRecipientPatch()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 7, AckInputSeq = 0, Count = 2 });
        SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = 1, Position = new Vector3(1, 2, 3), VelocityY = -1f, Yaw = 10f, Flags = SnapshotEntity.AliveFlag });
        SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = 2, Position = new Vector3(4, 5, 6), VelocityY = 0f, Yaw = 20f, Flags = 0 });
        Assert.Equal(WorldSnapshotHeader.Size + 2 * SnapshotEntity.Size, writer.Length);

        var self = new SnapshotSelf { Health = 70, Shield = 5, WeaponSlot = 1, Ammo = 3, ReloadRemainingTicks = 300 };
        WorldSnapshotHeader.PatchRecipient(_buffer.AsSpan(0, writer.Length), 42, self);

        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out var h));
        Assert.Equal(7u, h.ServerTick);
        Assert.Equal(42u, h.AckInputSeq);
        Assert.Equal(2, h.Count);
        Assert.Equal(70, h.Self.Health);
        Assert.Equal(5, h.Self.Shield);
        Assert.Equal(1, h.Self.WeaponSlot);
        Assert.Equal(3, h.Self.Ammo);
        Assert.Equal(300, h.Self.ReloadRemainingTicks);
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e1));
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e2));
        Assert.Equal(new Vector3(1, 2, 3), e1.Position);
        Assert.Equal(-1f, e1.VelocityY);
        Assert.True(e1.IsAlive);
        Assert.Equal(2, e2.EntityId);
        Assert.False(e2.IsAlive);
        Assert.Equal(0, reader.Remaining);
    }

    // D10: 11 + 6 + 23 * 50 = 1167 bytes must fit one unfragmented datagram (1200).
    [Fact]
    public void Snapshot_WithMaxEntities_Is1167Bytes_AndFitsOneDatagram()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = uint.MaxValue, Count = ProtocolConstants.MaxSnapshotEntities });
        for (int i = 0; i < ProtocolConstants.MaxSnapshotEntities; i++)
        {
            SnapshotEntity.Write(ref writer, new SnapshotEntity
            {
                EntityId = (ushort)(i + 1),
                Position = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                VelocityY = float.MaxValue,
                Yaw = 359f,
                Flags = SnapshotEntity.AliveFlag,
            });
        }

        Assert.False(writer.Overflowed);
        Assert.Equal(1167, writer.Length);
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void Snapshot_CountAboveLimit_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 1, Count = ProtocolConstants.MaxSnapshotEntities + 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void Snapshot_CountLargerThanPayload_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 1, Count = 3 });
        SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = 1 });
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void Snapshot_HeaderWithoutSelfBlock_IsRejected()
    {
        // A Phase 1 sized header (11 bytes) must not be read as a v3 header.
        var bytes = new byte[11];
        bytes[0] = (byte)PacketId.WorldSnapshot;
        var reader = new PacketReader(bytes);
        reader.TryReadPacketId(out _);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }
}
