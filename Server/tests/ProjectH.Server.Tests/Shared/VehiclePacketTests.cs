using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 19 D4, D13: VehicleStates (48): a 10-byte header and 19-byte records, at most 8 (162 bytes).
public class VehiclePacketTests
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly VehicleRecord[] _records = new VehicleRecord[VehicleSettings.MaxVehicles];

    // 기능: 시험용 차량 기록을 만든다.
    // 입력: id - 차량 id.
    // 출력: 값이 채워진 VehicleRecord.
    private static VehicleRecord Sample(byte id) => new()
    {
        Id = id, State = VehicleState.Active, Driver = 7, Passenger = 0, Position = new Vector3(-31.37f, 2.25f, 77.9f),
        Heading = 271.3f, Speed = -5.4321f, Steer = -0.5f, Health = 399,
    };

    // 기능: 기록들로 VehicleStates 패킷을 버퍼에 쓴다(Tick 1234, ack 56).
    // 입력: records - 기록.
    // 출력: 쓴 길이.
    private int Write(params VehicleRecord[] records)
    {
        var writer = new PacketWriter(_buffer);
        VehicleStatesPacket.WriteHeader(ref writer, 1234u, 56u, records.Length);
        foreach (VehicleRecord r in records) VehicleRecord.Write(ref writer, r);
        Assert.False(writer.Overflowed);
        return writer.Length;
    }

    // 기능: 버퍼 앞 length바이트를 VehicleStates로 읽는다(id는 반드시 48).
    // 입력: length - 길이, tick·ack·count - 결과.
    // 출력: 본문을 받아들였으면 true.
    private bool Read(int length, out uint tick, out uint ack, out int count)
    {
        var reader = new PacketReader(_buffer.AsSpan(0, length));
        Assert.True(reader.TryReadPacketId(out PacketId id));
        Assert.Equal(PacketId.VehicleStates, id);
        return VehicleStatesPacket.TryRead(ref reader, _records, out tick, out ack, out count);
    }

    [Fact]
    public void Sizes_AndTheId()
    {
        Assert.Equal(48, (byte)PacketId.VehicleStates);
        Assert.Equal(10, VehicleStatesPacket.HeaderSize);
        Assert.Equal(19, VehicleRecord.Size);
        Assert.Equal(162, VehicleStatesPacket.MaxSize);
        Assert.Equal(10, Write());
        var all = new VehicleRecord[VehicleSettings.MaxVehicles];
        for (int i = 0; i < all.Length; i++) all[i] = Sample((byte)(i + 1));
        Assert.Equal(VehicleStatesPacket.MaxSize, Write(all));
    }

    [Fact]
    public void RoundTrip_WithinTheQuantization()
    {
        VehicleRecord a = Sample(3);
        VehicleRecord b = Sample(200) with { State = VehicleState.Wrecked, Driver = 0, Passenger = 9, Steer = 1f, Heading = 359.999f };
        int length = Write(a, b);
        Assert.True(Read(length, out uint tick, out uint ack, out int count));
        Assert.Equal(1234u, tick);
        Assert.Equal(56u, ack);
        Assert.Equal(2, count);
        VehicleRecord r = _records[0];
        Assert.Equal(3, r.Id);
        Assert.Equal(VehicleState.Active, r.State);
        Assert.Equal(7, r.Driver);
        Assert.Equal(0, r.Passenger);
        Assert.Equal(VehicleRecord.QuantizeFixed(a.Position.X), r.Position.X);
        Assert.True(Vector3.Distance(a.Position, r.Position) < 0.004f);
        Assert.Equal(VehicleRecord.QuantizeHeading(a.Heading), r.Heading);
        Assert.Equal(a.Heading, r.Heading, 2);
        Assert.Equal(a.Speed, r.Speed, 2);
        Assert.Equal(-0.5f, r.Steer, 2);
        Assert.Equal(399, r.Health);
        Assert.Equal(VehicleState.Wrecked, _records[1].State);
        Assert.Equal(1f, _records[1].Steer);
        Assert.Equal(0f, _records[1].Heading, 2);   // 360 wraps to 0
    }

    [Fact]
    public void BadPackets_AreRefused()
    {
        int length = Write(Sample(1));
        Assert.False(Read(length - 1, out _, out _, out _));       // short
        _buffer[length] = 0;
        Assert.False(Read(length + 1, out _, out _, out _));       // trailing byte
        length = Write(Sample(1), Sample(1));
        Assert.False(Read(length, out _, out _, out _));           // the same id twice
        length = Write(Sample(1) with { Id = 0 });
        Assert.False(Read(length, out _, out _, out _));           // id 0
        length = Write(Sample(1));
        _buffer[11] = 2;                                           // unknown state
        Assert.False(Read(length, out _, out _, out _));
        length = Write();
        _buffer[9] = VehicleSettings.MaxVehicles + 1;              // count over the limit
        Assert.False(Read(length, out _, out _, out _));
    }

    [Fact]
    public void NonFiniteValues_AreWrittenAsZero()
    {
        int length = Write(Sample(5) with { Position = new Vector3(float.NaN, 1f, 2f), Heading = float.PositiveInfinity, Speed = float.NaN, Steer = float.NaN });
        Assert.True(Read(length, out _, out _, out _));
        Assert.Equal(0f, _records[0].Position.X);
        Assert.Equal(0f, _records[0].Heading);
        Assert.Equal(0f, _records[0].Speed);
        Assert.Equal(0f, _records[0].Steer);
    }
}
