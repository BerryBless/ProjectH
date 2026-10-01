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
            // Phase 11: valid UTF-8 without control characters, so the name always fits PlayerSpawned (see the rule).
            if (!ProtocolConstants.IsValidPlayerName(id)) return false;
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
        // seq 4 + moveX 4 + moveY 4 + yaw 4 + buttons 2 + aimYaw 4 + aimPitch 4 + viewTick 4
        public const int CommandSize = 30;
        // PacketId 1 + count 1 + 3 commands = 92 bytes, far below one datagram.
        public const int MaxSize = 2 + ProtocolConstants.MaxInputsPerPacket * CommandSize;

        private const ushort KnownButtons = (ushort)(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire |
                                                     InputButtons.Reload | InputButtons.Slot1 | InputButtons.Slot2 |
                                                     InputButtons.Slot3 | InputButtons.Interact | InputButtons.Drop |
                                                     InputButtons.UseMedkit | InputButtons.UseShieldCell);

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
                writer.WriteUInt16((ushort)c.Buttons);
                writer.WriteSingle(c.AimYaw);
                writer.WriteSingle(c.AimPitch);
                writer.WriteSingle(c.ViewTick);
            }
        }

        // Only the layout is checked here. Values (NaN aim, huge ViewTick, ...) are checked where they are
        // used: MovementSimulation for movement, the server's combat code for aim and ViewTick (D14).
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
                reader.TryReadUInt16(out ushort buttons);
                c.Buttons = (InputButtons)(buttons & KnownButtons);
                reader.TryReadSingle(out c.AimYaw);
                reader.TryReadSingle(out c.AimPitch);
                reader.TryReadSingle(out c.ViewTick);
                packet.Set(i, c);
            }
            return true;
        }
    }
}
