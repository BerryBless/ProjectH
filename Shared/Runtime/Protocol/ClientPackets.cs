using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Payload of LiteNetLib's connection request (no PacketId: it is not a regular packet).
    public struct ConnectRequestData
    {
        public ushort ProtocolVersion;
        public string DevPlayerId;

        public static void Write(ref PacketWriter writer, in ConnectRequestData data)
        {
            writer.WriteUInt16(data.ProtocolVersion);
            writer.WriteString(data.DevPlayerId, ProtocolConstants.MaxDevPlayerIdBytes);
        }

        public static bool TryRead(ref PacketReader reader, out ConnectRequestData data)
        {
            data = default;
            if (!reader.TryReadUInt16(out data.ProtocolVersion)) return false;
            if (!reader.TryReadString(ProtocolConstants.MaxDevPlayerIdBytes, out string id)) return false;
            if (id.Length == 0) return false;
            data.DevPlayerId = id;
            return true;
        }
    }

    public static class JoinMatchRequest
    {
        public static void Write(ref PacketWriter writer)
        {
            writer.WriteByte((byte)PacketId.JoinMatchRequest);
        }
    }

    // Carries the newest inputs, oldest first. Sent Unreliable: repeating the last few inputs in
    // every packet means one lost datagram does not lose an input. The server drops seqs it already has.
    public struct PlayerInputPacket
    {
        private const int CommandSize = 17; // seq 4 + moveX 4 + moveY 4 + yaw 4 + buttons 1
        private const byte KnownButtons = (byte)(InputButtons.Jump | InputButtons.Sprint);

        public byte Count;
        public InputCommand Input0;
        public InputCommand Input1;
        public InputCommand Input2;

        public InputCommand Get(int index)
        {
            switch (index)
            {
                case 0: return Input0;
                case 1: return Input1;
                default: return Input2;
            }
        }

        public void Set(int index, in InputCommand command)
        {
            switch (index)
            {
                case 0: Input0 = command; break;
                case 1: Input1 = command; break;
                default: Input2 = command; break;
            }
        }

        public static void Write(ref PacketWriter writer, in PlayerInputPacket packet)
        {
            int count = packet.Count > ProtocolConstants.MaxInputsPerPacket ? ProtocolConstants.MaxInputsPerPacket : packet.Count;
            writer.WriteByte((byte)PacketId.PlayerInput);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                InputCommand c = packet.Get(i);
                writer.WriteUInt32(c.Seq);
                writer.WriteSingle(c.MoveX);
                writer.WriteSingle(c.MoveY);
                writer.WriteSingle(c.Yaw);
                writer.WriteByte((byte)c.Buttons);
            }
        }

        public static bool TryRead(ref PacketReader reader, out PlayerInputPacket packet)
        {
            packet = default;
            if (!reader.TryReadByte(out byte count)) return false;
            if (count == 0 || count > ProtocolConstants.MaxInputsPerPacket) return false;
            if (reader.Remaining < count * CommandSize) return false;

            packet.Count = count;
            for (int i = 0; i < count; i++)
            {
                var c = new InputCommand();
                reader.TryReadUInt32(out c.Seq);
                reader.TryReadSingle(out c.MoveX);
                reader.TryReadSingle(out c.MoveY);
                reader.TryReadSingle(out c.Yaw);
                reader.TryReadByte(out byte buttons);
                c.Buttons = (InputButtons)(buttons & KnownButtons);
                packet.Set(i, c);
            }
            return true;
        }
    }
}
