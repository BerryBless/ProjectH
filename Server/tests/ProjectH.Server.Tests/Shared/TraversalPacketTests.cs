using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 12 D11 (spec §2 네트워크): the new packets, the snapshot's mode flags and self block, the mode in PlayerRespawned,
// the cause in PlayerDied and the Crouch button. Every reader refuses truncated data.
public class TraversalPacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];

    // 기능: 테스트 버퍼의 앞 length 바이트로 Reader를 만들고 Packet Id가 expected인지 확인한 뒤 Id 다음 위치의 Reader를 돌려준다.
    // 입력: length - 버퍼에 쓰인 바이트 수, expected - 기대하는 Packet Id.
    // 출력: Packet Id를 읽은 뒤의 PacketReader. Id가 다르면 Assert 실패.
    private PacketReader ReaderAfterId(int length, PacketId expected)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(expected, id);
        return reader;
    }

    // 기능: 맵을 가로지르는 고정 수송기 경로 하나를 만든다.
    // 입력: 없음.
    // 출력: (-100, 3.5)에서 (100, -3.5)로 고도 90에서 300 Tick 동안 가는 DropRoute.
    private static DropRoute Route() => new()
    {
        StartX = -100f, StartZ = 3.5f, EndX = 100f, EndZ = -3.5f, Altitude = 90f, StartTick = 123_456, DurationTicks = 300,
    };

    [Fact]
    public void Ids_AreTwentyFourAndTwentyFive()
    {
        Assert.Equal(24, (byte)PacketId.TransportRoute);
        Assert.Equal(25, (byte)PacketId.DoorStates);
        var reader = new PacketReader(new byte[] { 25 });
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.DoorStates, id);
    }

    [Fact]
    public void TransportRoute_RoundTrip_In29Bytes()
    {
        var writer = new PacketWriter(_buffer);
        TransportRoutePacket.Write(ref writer, Route());
        Assert.Equal(TransportRoutePacket.Size, writer.Length);
        Assert.Equal(29, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.TransportRoute);
        Assert.True(TransportRoutePacket.TryRead(ref reader, out DropRoute read));
        Assert.Equal(Route(), read);
    }

    [Theory]
    [InlineData(float.NaN, 90f, 300u)]
    [InlineData(float.PositiveInfinity, 90f, 300u)]
    [InlineData(200f, 90f, 300u)]       // outside the snapshot's range
    [InlineData(-100f, -1f, 300u)]      // underground
    [InlineData(-100f, 130f, 300u)]
    [InlineData(-100f, 90f, 0u)]        // no duration
    [InlineData(-100f, 90f, 1_000_000u)]
    public void TransportRoute_WithBadValues_IsRefused(float startX, float altitude, uint duration)
    {
        DropRoute route = Route();
        route.StartX = startX;
        route.Altitude = altitude;
        route.DurationTicks = duration;
        var writer = new PacketWriter(_buffer);
        TransportRoutePacket.Write(ref writer, route);
        var reader = ReaderAfterId(writer.Length, PacketId.TransportRoute);
        Assert.False(TransportRoutePacket.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData("EndX", float.NaN, false)]
    [InlineData("StartZ", 128f, false)]
    [InlineData("EndZ", -128f, false)]
    [InlineData("Altitude", float.NaN, false)]
    [InlineData("StartX", 127f, true)]
    [InlineData("StartX", -127f, true)]
    public void TransportRoute_FieldBounds(string field, float value, bool accepted)
    {
        DropRoute route = Route();
        switch (field)
        {
            case "EndX": route.EndX = value; break;
            case "StartZ": route.StartZ = value; break;
            case "EndZ": route.EndZ = value; break;
            case "Altitude": route.Altitude = value; break;
            default: route.StartX = value; break;
        }
        var writer = new PacketWriter(_buffer);
        TransportRoutePacket.Write(ref writer, route);
        var reader = ReaderAfterId(writer.Length, PacketId.TransportRoute);
        Assert.Equal(accepted, TransportRoutePacket.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData(76800u, true)]
    [InlineData(76801u, false)]
    public void TransportRoute_DurationBoundary(uint duration, bool accepted)
    {
        DropRoute route = Route();
        route.DurationTicks = duration;
        var writer = new PacketWriter(_buffer);
        TransportRoutePacket.Write(ref writer, route);
        var reader = ReaderAfterId(writer.Length, PacketId.TransportRoute);
        Assert.Equal(accepted, TransportRoutePacket.TryRead(ref reader, out _));
    }

    [Fact]
    public void SnapshotHeader_WithOutOfRangeEnergy_IsRefused()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 5, Count = 0, Part = 0, PartCount = 1 });
        var self = new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths + 1 };
        WorldSnapshotHeader.PatchRecipient(_buffer.AsSpan(0, writer.Length), 1, self);
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void DoorStates_RoundTrip_InTwoBytes()
    {
        var writer = new PacketWriter(_buffer);
        DoorStatesPacket.Write(ref writer, 0b10101);
        Assert.Equal(DoorStatesPacket.Size, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.DoorStates);
        Assert.True(DoorStatesPacket.TryRead(ref reader, out byte mask));
        Assert.Equal(0b10101, mask);
    }

    [Fact]
    public void TruncatedPackets_AreRefused()
    {
        var shortRoute = new PacketReader(new byte[TransportRoutePacket.Size - 2]);
        Assert.False(TransportRoutePacket.TryRead(ref shortRoute, out _));
        var noMask = new PacketReader(Array.Empty<byte>());
        Assert.False(DoorStatesPacket.TryRead(ref noMask, out _));
        var shortSelf = new PacketReader(new byte[SnapshotSelf.Size - 1]);
        Assert.False(SnapshotSelf.TryRead(ref shortSelf, out _));
        var shortRespawn = new PacketReader(new byte[18]);
        Assert.False(PlayerRespawned.TryRead(ref shortRespawn, out _));
        var shortDied = new PacketReader(new byte[5]);
        Assert.False(PlayerDied.TryRead(ref shortDied, out _));
    }

    [Fact]
    public void EntityFlags_CarryAliveModeSprintAndExhaustion()
    {
        for (int m = 0; m <= (int)MovementMode.Downed; m++)
        {
            var mode = (MovementMode)m;
            foreach (bool alive in new[] { false, true })
            {
                byte flags = SnapshotEntity.MakeFlags(alive, mode, sprinting: m % 2 == 0, exhausted: m % 3 == 0);
                var entity = new SnapshotEntity { EntityId = 7, Flags = flags };
                var writer = new PacketWriter(_buffer);
                SnapshotEntity.Write(ref writer, entity);
                Assert.Equal(SnapshotEntity.Size, writer.Length);
                var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
                Assert.True(SnapshotEntity.TryRead(ref reader, out SnapshotEntity read));
                Assert.Equal(alive, read.IsAlive);
                Assert.Equal(mode, read.Mode);
                Assert.Equal(m % 2 == 0, read.IsSprinting);
                Assert.Equal(m % 3 == 0, read.IsExhausted);
                Assert.Equal(0, read.Flags & 0xC0);
            }
        }
        // Phase 14: mode bits 7 are Downed (every value of the 3 bits is a mode now).
        Assert.Equal(MovementMode.Downed, new SnapshotEntity { Flags = 0x0F }.Mode);
    }

    [Fact]
    public void SnapshotSelf_RoundTrip_In14Bytes_WithQuantizedVelocity()
    {
        var self = new SnapshotSelf
        {
            Health = 77, Shield = 12, WeaponSlot = 2, Ammo = 9, ReloadRemainingTicks = 40,
            Energy = 6543, HorizontalVelocity = new Vector2(12.345f, -6.789f), ModeTicks = 11, EnergyDelayTicks = 29,
        };
        var writer = new PacketWriter(_buffer);
        SnapshotSelf.Write(ref writer, self);
        Assert.Equal(14, writer.Length);
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(SnapshotSelf.TryRead(ref reader, out SnapshotSelf read));
        Assert.Equal(77, read.Health);
        Assert.Equal(40, read.ReloadRemainingTicks);
        Assert.Equal(6543, read.Energy);
        Assert.Equal(SnapshotEntity.Quantize(12.345f), read.HorizontalVelocity.X);
        Assert.Equal(SnapshotEntity.Quantize(-6.789f), read.HorizontalVelocity.Y);
        Assert.Equal(11, read.ModeTicks);
        Assert.Equal(29, read.EnergyDelayTicks);
    }

    [Fact]
    public void SnapshotSelf_AboveFullEnergy_IsRefused()
    {
        var writer = new PacketWriter(_buffer);
        SnapshotSelf.Write(ref writer, new SnapshotSelf { Energy = MoveState.MaxEnergyHundredths + 1 });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.False(SnapshotSelf.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheRecipientPatch_WritesTheWholeSelfBlock()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 5, Count = 0, Part = 0, PartCount = 1 });
        Assert.Equal(WorldSnapshotHeader.Size, writer.Length);
        var self = new SnapshotSelf { Health = 50, Energy = 1234, HorizontalVelocity = new Vector2(3f, 4f), ModeTicks = 2, EnergyDelayTicks = 7 };
        WorldSnapshotHeader.PatchRecipient(_buffer.AsSpan(0, writer.Length), 99, self);
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.True(WorldSnapshotHeader.TryRead(ref reader, out WorldSnapshotHeader header));
        Assert.Equal(99u, header.AckInputSeq);
        Assert.Equal(1234, header.Self.Energy);
        Assert.Equal(new Vector2(3f, 4f), header.Self.HorizontalVelocity);
        Assert.Equal(7, header.Self.EnergyDelayTicks);
    }

    [Fact]
    public void PlayerRespawned_CarriesTheStartMode()
    {
        var writer = new PacketWriter(_buffer);
        PlayerRespawned.Write(ref writer, new PlayerRespawned { EntityId = 3, Position = new Vector3(1f, 90f, 2f), Yaw = 10f, Mode = MovementMode.Transport });
        Assert.Equal(20, writer.Length);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerRespawned);
        Assert.True(PlayerRespawned.TryRead(ref reader, out PlayerRespawned read));
        Assert.Equal(MovementMode.Transport, read.Mode);

        _buffer[writer.Length - 1] = 7;   // Phase 14: Downed reads
        reader = ReaderAfterId(writer.Length, PacketId.PlayerRespawned);
        Assert.True(PlayerRespawned.TryRead(ref reader, out read));
        Assert.Equal(MovementMode.Downed, read.Mode);

        _buffer[writer.Length - 1] = 8;   // no such mode
        reader = ReaderAfterId(writer.Length, PacketId.PlayerRespawned);
        Assert.False(PlayerRespawned.TryRead(ref reader, out _));
    }

    [Fact]
    public void PlayerDied_CarriesTheCause()
    {
        var writer = new PacketWriter(_buffer);
        PlayerDied.Write(ref writer, new PlayerDied { VictimId = 4, Placement = 3, Cause = DeathCause.Fall });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.True(PlayerDied.TryRead(ref reader, out PlayerDied read));
        Assert.Equal(DeathCause.Fall, read.Cause);
        Assert.Equal(0, read.KillerId);

        _buffer[writer.Length - 1] = 2;   // Phase 17: Explosion
        reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.True(PlayerDied.TryRead(ref reader, out read));
        Assert.Equal(DeathCause.Explosion, read.Cause);

        _buffer[writer.Length - 1] = 3;   // no such cause
        reader = ReaderAfterId(writer.Length, PacketId.PlayerDied);
        Assert.False(PlayerDied.TryRead(ref reader, out _));
    }

    [Fact]
    public void TheCrouchButton_GoesThrough_AndUnknownBitsDoNot()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        // Phase 13: 0x1000 and 0x2000 are tools, Phase 14: 0x4000 InteractHeld, Phase 17: 0x8000 ThrowGrenade (no bit is left unknown)
        packet.Set(0, new InputCommand { Seq = 1, Buttons = InputButtons.Crouch | (InputButtons)0x8000 });
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out PlayerInputPacket read));
        Assert.Equal(InputButtons.Crouch | InputButtons.ThrowGrenade, read.Get(0).Buttons);
        Assert.Equal(2048, (int)InputButtons.Crouch);
    }
}
