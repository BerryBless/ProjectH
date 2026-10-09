using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Phase 19 D4: a vehicle's state. Values are the wire format.
    public enum VehicleState : byte
    {
        Active = 0,
        Wrecked = 1,   // destroyed: no seats, no motion; the server removes it after its wreck time
    }

    // Phase 19 D4: one vehicle as VehicleStates carries it. 19 bytes on the wire: [Id 1][State 1][Driver 2][Passenger 2]
    // [Position 3 x int16, 1/256 m][Heading u16, 360/65536 deg][Speed int16, 1/256 m/s][Steer int8, /127][Health 2].
    // Driver and Passenger are entity ids (0 = empty seat). Id is 1..255; the server never reuses an id within a second of
    // the vehicle's removal, so a client that hides a missing vehicle after 1 s never mistakes a new one for an old one.
    public struct VehicleRecord
    {
        public const int Size = 19;
        public const float FixedScale = 256f;
        public const float HeadingScale = 65536f / 360f;
        public const float SteerScale = 127f;

        public byte Id;
        public VehicleState State;
        public ushort Driver;
        public ushort Passenger;
        public Vector3 Position;
        public float Heading;
        public float Speed;
        public float Steer;
        public ushort Health;

        // 기능: 기록 하나를 쓴다(위치·속도는 1/256 고정소수점, 방향 16비트, 조향 /127, 범위 밖은 자르고 유한하지 않으면 0).
        // 입력: writer - 대상, r - 기록.
        // 출력: 반환값 없음. writer에 19바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in VehicleRecord r)
        {
            writer.WriteByte(r.Id);
            writer.WriteByte((byte)r.State);
            writer.WriteUInt16(r.Driver);
            writer.WriteUInt16(r.Passenger);
            writer.WriteUInt16(SnapshotEntity.ToFixed(r.Position.X));
            writer.WriteUInt16(SnapshotEntity.ToFixed(r.Position.Y));
            writer.WriteUInt16(SnapshotEntity.ToFixed(r.Position.Z));
            writer.WriteUInt16(ToHeading(r.Heading));
            writer.WriteUInt16(SnapshotEntity.ToFixed(r.Speed));
            writer.WriteByte(unchecked((byte)ToSteer(r.Steer)));
            writer.WriteUInt16(r.Health);
        }

        // 기능: 기록 하나(19바이트)를 읽어 양자화를 되돌린다(조향은 -1..1로 자른다).
        // 입력: reader - 기록 위치의 본문, r - 결과.
        // 출력: 성공하면 true와 기록. 짧거나, Id 0이거나, 모르는 상태면 false.
        public static bool TryRead(ref PacketReader reader, out VehicleRecord r)
        {
            r = default;
            if (reader.Remaining < Size) return false;
            reader.TryReadByte(out r.Id);
            reader.TryReadByte(out byte state);
            reader.TryReadUInt16(out r.Driver);
            reader.TryReadUInt16(out r.Passenger);
            reader.TryReadUInt16(out ushort x);
            reader.TryReadUInt16(out ushort y);
            reader.TryReadUInt16(out ushort z);
            reader.TryReadUInt16(out ushort heading);
            reader.TryReadUInt16(out ushort speed);
            reader.TryReadByte(out byte steer);
            reader.TryReadUInt16(out r.Health);
            if (r.Id == 0 || state > (byte)VehicleState.Wrecked) return false;
            r.State = (VehicleState)state;
            r.Position = new Vector3(SnapshotEntity.FromFixed(x), SnapshotEntity.FromFixed(y), SnapshotEntity.FromFixed(z));
            r.Heading = heading / HeadingScale;
            r.Speed = SnapshotEntity.FromFixed(speed);
            r.Steer = Math.Max(-1f, unchecked((sbyte)steer) / SteerScale);
            return true;
        }

        // 기능: 받는 쪽이 읽을 위치·속도 값을 돌려준다(테스트와 예측 비교용).
        // 입력: value - 원래 값.
        // 출력: 1/256 고정소수점으로 왕복한 값.
        public static float QuantizeFixed(float value) => SnapshotEntity.Quantize(value);

        // 기능: 받는 쪽이 읽을 방향 값을 돌려준다.
        // 입력: heading - 도.
        // 출력: 16비트로 왕복한 도(0..360).
        public static float QuantizeHeading(float heading) => ToHeading(heading) / HeadingScale;

        // 기능: 방향을 16비트로 바꾼다(0..360, 유한하지 않으면 0).
        // 입력: heading - 도.
        // 출력: 16비트 값.
        private static ushort ToHeading(float heading)
        {
            if (float.IsNaN(heading) || float.IsInfinity(heading)) return 0;
            float degrees = heading % 360f;
            if (degrees < 0f) degrees += 360f;
            return unchecked((ushort)(int)Math.Round(degrees * HeadingScale));
        }

        // 기능: 조향을 -127..127로 바꾼다(유한하지 않으면 0).
        // 입력: steer - -1..1.
        // 출력: 부호 있는 8비트 값.
        private static sbyte ToSteer(float steer)
        {
            if (float.IsNaN(steer) || float.IsInfinity(steer)) return 0;
            float scaled = (float)Math.Round(steer * SteerScale);
            if (scaled > 127f) scaled = 127f;
            if (scaled < -127f) scaled = -127f;
            return (sbyte)scaled;
        }
    }

    // Phase 19 D4: S->C, Unreliable on channel 0 (not Sequenced: that would share the snapshot's sequence and could drop a
    // snapshot part), with every snapshot (same ServerTick). Per recipient: the vehicles within the interest range of its body
    // plus the one it sits in (dead players and spectators get all). A client drops a packet older than the newest it applied
    // and hides a vehicle missing from the packets for 1 s. AckInputSeq is the recipient's last processed input, for the
    // driver's prediction. Layout: [PacketId 1][ServerTick 4][AckInputSeq 4][Count 1] Count x VehicleRecord (19 bytes).
    public static class VehicleStatesPacket
    {
        public const int HeaderSize = 10;   // with the packet id
        public const int MaxSize = HeaderSize + VehicleSettings.MaxVehicles * VehicleRecord.Size;   // 162 bytes

        // 기능: 헤더를 쓴다(뒤에 count개의 VehicleRecord.Write가 와야 한다).
        // 입력: writer - 대상, serverTick - Snapshot과 같은 Tick, ackInputSeq - 받는 사람의 마지막 처리 입력, count - 기록 수(최대 MaxVehicles).
        // 출력: 반환값 없음. writer에 10바이트가 쓰인다.
        public static void WriteHeader(ref PacketWriter writer, uint serverTick, uint ackInputSeq, int count)
        {
            writer.WriteByte((byte)PacketId.VehicleStates);
            writer.WriteUInt32(serverTick);
            writer.WriteUInt32(ackInputSeq);
            writer.WriteByte((byte)Math.Min(Math.Max(count, 0), VehicleSettings.MaxVehicles));
        }

        // 기능: 본문(PacketId 뒤)을 호출자의 고정 배열에 읽는다(할당 없음).
        // 입력: reader - 본문, records - MaxVehicles 칸 이상, serverTick·ackInputSeq·count - 결과.
        // 출력: 성공하면 true(앞쪽 count칸이 채워진다). 길이가 정확히 맞지 않거나, 수가 상한·배열을 넘거나, Id 0·모르는 상태·같은 Id가
        //   두 번이면 false(배열은 쓰다 만 상태일 수 있다).
        public static bool TryRead(ref PacketReader reader, Span<VehicleRecord> records, out uint serverTick, out uint ackInputSeq, out int count)
        {
            serverTick = 0;
            ackInputSeq = 0;
            count = 0;
            if (!reader.TryReadUInt32(out serverTick) || !reader.TryReadUInt32(out ackInputSeq) || !reader.TryReadByte(out byte n)) return false;
            if (n > VehicleSettings.MaxVehicles || n > records.Length || reader.Remaining != n * VehicleRecord.Size) return false;
            for (int i = 0; i < n; i++)
            {
                if (!VehicleRecord.TryRead(ref reader, out VehicleRecord r)) return false;
                for (int j = 0; j < i; j++)
                {
                    if (records[j].Id == r.Id) return false;
                }
                records[i] = r;
            }
            count = n;
            return true;
        }
    }
}
