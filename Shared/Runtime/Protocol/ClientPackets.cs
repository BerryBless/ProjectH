using ProjectH.Shared.Simulation;

namespace ProjectH.Shared.Protocol
{
    // Review fixes A3, B2 (v19): what a connection request carries besides the version, the session key and the name.
    [System.Flags]
    public enum ConnectFlags : byte
    {
        None = 0,
        HasCookie = 1,   // the request repeats the cookie of the server's RejectForce (ProtocolLimits.CookieBytes)
        HasResume = 2,   // review fix B4: a resume nonce and proof follow the name
    }

    // Payload of LiteNetLib's connection request (no PacketId: it is not a regular packet).
    // v19: version u16, flags u8, [cookie 16 when HasCookie], session key blob (u16 length = ProtocolLimits.RsaBlobBytes,
    // then the blob: the client's 32-byte session key RSA-OAEP-SHA1 encrypted with the server's public key), name (1-byte
    // length + UTF-8), [resume nonce u32 + proof 16 when HasResume]. Every request carries the blob, the cookieless first one
    // too. The version stays first in every layout, so a server can tell an older client VersionMismatch before reading the
    // rest.
    public struct ConnectRequestData
    {
        public ushort ProtocolVersion;
        public ConnectFlags Flags;
        // ProtocolLimits.CookieBytes; read and written only when Flags has HasCookie.
        public byte[] Cookie;
        // ProtocolLimits.RsaBlobBytes: the encrypted session key (always present).
        public byte[] SessionKeyBlob;
        public string DevPlayerId;
        // Review fix B4: read and written only when Flags has HasResume. The nonce the client raises for each resume attempt
        // with one resume key, and the proof (SessionAuth.ComputeResumeProof, ProtocolLimits.ResumeProofBytes).
        public uint ResumeNonce;
        public byte[] ResumeProof;

        // 기능: 연결 요청 데이터를 쓴다. HasCookie인데 쿠키가 없거나 짧으면 HasCookie를, HasResume인데 증명이 없거나 짧으면
        //   HasResume을 빼고 쓴다. 세션 키 blob이 RsaBlobBytes가 아니면 길이 0으로 쓴다(서버가 BadRequest로 거절한다).
        // 입력: writer - 쓸 곳, data - 버전·플래그·쿠키·blob·이름·Resume 증명.
        // 출력: 반환값 없음. 이름이 길면 writer가 Overflowed가 된다.
        public static void Write(ref PacketWriter writer, in ConnectRequestData data)
        {
            ConnectFlags flags = data.Flags;
            bool cookie = (flags & ConnectFlags.HasCookie) != 0 && data.Cookie != null && data.Cookie.Length >= ProtocolLimits.CookieBytes;
            if (!cookie) flags &= ~ConnectFlags.HasCookie;
            bool resume = (flags & ConnectFlags.HasResume) != 0 && data.ResumeProof != null && data.ResumeProof.Length >= ProtocolLimits.ResumeProofBytes;
            if (!resume) flags &= ~ConnectFlags.HasResume;
            bool blob = data.SessionKeyBlob != null && data.SessionKeyBlob.Length == ProtocolLimits.RsaBlobBytes;
            writer.WriteUInt16(data.ProtocolVersion);
            writer.WriteByte((byte)flags);
            if (cookie) writer.WriteBytes(new System.ReadOnlySpan<byte>(data.Cookie, 0, ProtocolLimits.CookieBytes));
            writer.WriteUInt16(blob ? (ushort)ProtocolLimits.RsaBlobBytes : (ushort)0);
            if (blob) writer.WriteBytes(data.SessionKeyBlob);
            writer.WriteString(data.DevPlayerId, ProtocolConstants.MaxDevPlayerIdBytes);
            if (resume)
            {
                writer.WriteUInt32(data.ResumeNonce);
                writer.WriteBytes(new System.ReadOnlySpan<byte>(data.ResumeProof, 0, ProtocolLimits.ResumeProofBytes));
            }
        }

        // 기능: 연결 요청 데이터를 읽는다(모르는 플래그·잘린 쿠키·blob 길이가 RsaBlobBytes가 아님·이름 규칙 위반·잘린 증명·남는 바이트는 거절).
        // 입력: reader - 요청 데이터, cookieBuffer·blobBuffer·proofBuffer - 쿠키 16 B·blob 256 B·증명 16 B를 받을 버퍼(null이면
        //   필요할 때 새로 만든다. 서버는 수신 스레드의 재사용 버퍼를 넘겨 요청마다 할당하지 않는다).
        // 출력: 맞으면 true와 데이터(Cookie·SessionKeyBlob·ResumeProof는 받은 버퍼), 아니면 false.
        public static bool TryRead(ref PacketReader reader, out ConnectRequestData data, byte[] cookieBuffer = null,
            byte[] blobBuffer = null, byte[] proofBuffer = null)
        {
            data = default;
            if (!reader.TryReadUInt16(out data.ProtocolVersion)) return false;
            if (!reader.TryReadByte(out byte flags)) return false;
            data.Flags = (ConnectFlags)flags;
            if ((flags & ~(byte)(ConnectFlags.HasCookie | ConnectFlags.HasResume)) != 0) return false;
            if ((data.Flags & ConnectFlags.HasCookie) != 0)
            {
                byte[] cookie = Buffer(cookieBuffer, ProtocolLimits.CookieBytes);
                if (!reader.TryReadBytes(new System.Span<byte>(cookie, 0, ProtocolLimits.CookieBytes))) return false;
                data.Cookie = cookie;
            }
            if (!reader.TryReadUInt16(out ushort blobLength) || blobLength != ProtocolLimits.RsaBlobBytes) return false;
            byte[] keyBlob = Buffer(blobBuffer, ProtocolLimits.RsaBlobBytes);
            if (!reader.TryReadBytes(new System.Span<byte>(keyBlob, 0, ProtocolLimits.RsaBlobBytes))) return false;
            data.SessionKeyBlob = keyBlob;
            if (!reader.TryReadString(ProtocolConstants.MaxDevPlayerIdBytes, out string id)) return false;
            // Phase 11: valid UTF-8 without control characters, so the name always fits PlayerSpawned (see the rule).
            if (!ProtocolConstants.IsValidPlayerName(id)) return false;
            data.DevPlayerId = id;
            if ((data.Flags & ConnectFlags.HasResume) != 0)
            {
                if (!reader.TryReadUInt32(out data.ResumeNonce)) return false;
                byte[] proof = Buffer(proofBuffer, ProtocolLimits.ResumeProofBytes);
                if (!reader.TryReadBytes(new System.Span<byte>(proof, 0, ProtocolLimits.ResumeProofBytes))) return false;
                data.ResumeProof = proof;
            }
            return reader.Remaining == 0;
        }

        // 기능: 넘겨받은 버퍼가 충분하면 그것을, 아니면 새 배열을 돌려준다.
        // 입력: given - 호출자 버퍼(null 가능), size - 필요한 크기.
        // 출력: size 이상인 배열.
        private static byte[] Buffer(byte[] given, int size) => given != null && given.Length >= size ? given : new byte[size];
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
                                                     InputButtons.UseMedkit | InputButtons.UseShieldCell | InputButtons.Crouch |
                                                     InputButtons.ToolHarvest | InputButtons.ToolBuild | InputButtons.InteractHeld |
                                                     InputButtons.ThrowGrenade);

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

        // 기능: PacketId 뒤의 입력 본문을 읽는다. 배치만 검사한다. 값(NaN 조준, 큰 ViewTick 등)은 쓰는 곳이 검사한다:
        //   이동은 MovementSimulation, 조준과 ViewTick은 서버 전투 코드(D14).
        // 입력: reader - PacketId 다음 위치의 읽기 도구.
        // 출력: 개수가 1–MaxInputsPerPacket이고 본문이 정확히 count × CommandSize면 true와 입력들, 아니면 false.
        //   리뷰 수정 A1(SEC-1): 마지막 명령 뒤에 남는 바이트가 있어도 false(앞 92 B만 맞는 큰 패킷을 처리하지 않는다).
        public static bool TryRead(ref PacketReader reader, out PlayerInputPacket packet)
        {
            packet = default;
            if (!reader.TryReadByte(out byte count)) return false;
            if (count == 0 || count > ProtocolConstants.MaxInputsPerPacket) return false;
            if (reader.Remaining != count * CommandSize) return false;

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
