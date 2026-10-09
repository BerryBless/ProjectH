using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

public class PacketTests
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

    // 기능: 모든 필드가 채워진 시험용 입력 명령을 만든다.
    // 입력: seq - 입력 순번(Yaw·AimYaw·ViewTick에도 더해져 명령마다 값이 달라진다).
    // 출력: Seq·이동·버튼·조준·ViewTick이 채워진 InputCommand.
    private static InputCommand FullCommand(uint seq) => new InputCommand
    {
        Seq = seq,
        MoveX = 0.5f,
        MoveY = -1f,
        Yaw = 90f + seq,
        Buttons = InputButtons.Jump | InputButtons.Fire | InputButtons.Slot2 | InputButtons.Interact | InputButtons.UseShieldCell,
        AimYaw = 12.5f + seq,
        AimPitch = -30f,
        ViewTick = 1000u + seq,   // review fix D2: a uint tick
    };

    // Review fix B2: every request carries the session key blob (RsaBlobBytes).
    private static readonly byte[] Blob = MakeBlob();

    // 기능: 시험용 세션 키 blob(256 B, 값은 상관없다)을 만든다.
    // 입력: 없음.
    // 출력: RsaBlobBytes 크기 배열.
    private static byte[] MakeBlob()
    {
        var blob = new byte[ProtocolLimits.RsaBlobBytes];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i * 3);
        return blob;
    }

    [Fact]
    public void ConnectRequestData_RoundTrip()
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = 1, SessionKeyBlob = Blob, DevPlayerId = "abc" });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(ConnectRequestData.TryRead(ref reader, out var data));
        Assert.Equal((ushort)1, data.ProtocolVersion);
        Assert.Equal("abc", data.DevPlayerId);
        Assert.Equal(Blob, data.SessionKeyBlob);
    }

    [Fact]
    public void ConnectRequestData_EmptyId_IsRejected()
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = 1, SessionKeyBlob = Blob, DevPlayerId = "" });
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.False(ConnectRequestData.TryRead(ref reader, out _));
    }

    // Review fixes A3, B2 (v19): version, flags, the cookie when flags say so, the blob, the name, the resume proof when
    // flags say so.
    [Fact]
    public void ConnectRequestData_WithACookieAndAResume_RoundTrips_IntoTheCallersBuffers()
    {
        var cookie = new byte[ProtocolLimits.CookieBytes];
        for (int i = 0; i < cookie.Length; i++) cookie[i] = (byte)(200 + i);
        var proof = new byte[ProtocolLimits.ResumeProofBytes];
        for (int i = 0; i < proof.Length; i++) proof[i] = (byte)(100 + i);
        var buffer = new byte[ProtocolConstants.MaxPacketSize];
        var writer = new PacketWriter(buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion, Flags = ConnectFlags.HasCookie | ConnectFlags.HasResume, Cookie = cookie,
            SessionKeyBlob = Blob, DevPlayerId = "abc", ResumeNonce = 7, ResumeProof = proof,
        });
        Assert.Equal(2 + 1 + ProtocolLimits.CookieBytes + 2 + ProtocolLimits.RsaBlobBytes + 1 + 3 + 4 + ProtocolLimits.ResumeProofBytes, writer.Length);

        var cookieInto = new byte[ProtocolLimits.CookieBytes];
        var blobInto = new byte[ProtocolLimits.RsaBlobBytes];
        var proofInto = new byte[ProtocolLimits.ResumeProofBytes];
        var reader = new PacketReader(buffer.AsSpan(0, writer.Length));
        Assert.True(ConnectRequestData.TryRead(ref reader, out var data, cookieInto, blobInto, proofInto));
        Assert.Equal(ConnectFlags.HasCookie | ConnectFlags.HasResume, data.Flags);
        Assert.Same(cookieInto, data.Cookie);   // the caller's buffers: no allocation per request on the server
        Assert.Same(blobInto, data.SessionKeyBlob);
        Assert.Same(proofInto, data.ResumeProof);
        Assert.Equal(cookie, cookieInto);
        Assert.Equal(Blob, blobInto);
        Assert.Equal(proof, proofInto);
        Assert.Equal(7u, data.ResumeNonce);
        Assert.Equal("abc", data.DevPlayerId);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ConnectRequestData_WithoutACookieOrResume_HasNeither()
    {
        var writer = new PacketWriter(_buffer);
        ConnectRequestData.Write(ref writer, new ConnectRequestData { ProtocolVersion = 19, SessionKeyBlob = Blob, DevPlayerId = "abc" });
        Assert.Equal(2 + 1 + 2 + ProtocolLimits.RsaBlobBytes + 1 + 3, writer.Length);
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(ConnectRequestData.TryRead(ref reader, out var data));
        Assert.Equal(ConnectFlags.None, data.Flags);
        Assert.Null(data.Cookie);
        Assert.Null(data.ResumeProof);
    }

    [Fact]
    public void ConnectRequestData_ACutCookie_UnknownFlags_NoBlob_ACutProof_AndTrailingBytes_AreRejected()
    {
        // HasCookie with only 15 cookie bytes.
        var cut = new byte[] { 19, 0, (byte)ConnectFlags.HasCookie, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var reader = new PacketReader(cut);
        Assert.False(ConnectRequestData.TryRead(ref reader, out _));

        Assert.False(TryReadLayout(0x80, Blob, "a", 0));                    // unknown flag
        Assert.False(TryReadLayout(0, null, "a", 0));                       // no blob (length 0)
        Assert.False(TryReadLayout(0, new byte[128], "a", 0));              // a blob of another size
        Assert.False(TryReadLayout(0, Blob, "a", 1));                       // a byte after the name
        Assert.False(TryReadLayout((byte)ConnectFlags.HasResume, Blob, "a", 19));   // HasResume with a cut proof (4 + 15)
        Assert.True(TryReadLayout((byte)ConnectFlags.HasResume, Blob, "a", 20));    // nonce 4 + proof 16
    }

    // 기능: 손으로 v19 배치를 만들어 읽어 본다(쿠키 없음).
    // 입력: flags - 플래그 바이트, blob - blob(null = 길이 0), name - 이름, after - 이름 뒤에 붙일 0 바이트 수.
    // 출력: TryRead 결과.
    private bool TryReadLayout(byte flags, byte[]? blob, string name, int after)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var bytes = new byte[2 + 1 + 2 + (blob?.Length ?? 0) + 1 + nameBytes.Length + after];
        int at = 0;
        bytes[at++] = 19;
        bytes[at++] = 0;
        bytes[at++] = flags;
        bytes[at++] = (byte)((blob?.Length ?? 0) & 0xFF);
        bytes[at++] = (byte)((blob?.Length ?? 0) >> 8);
        if (blob != null) { blob.CopyTo(bytes, at); at += blob.Length; }
        bytes[at++] = (byte)nameBytes.Length;
        nameBytes.CopyTo(bytes, at);
        var reader = new PacketReader(bytes);
        return ConnectRequestData.TryRead(ref reader, out _);
    }

    // 기능: 이름 바이트를 그대로 넣은 Connect 요청(버전 1, v19 배치)을 만들어 읽어 본다.
    // 입력: name - 이름 필드에 넣을 원시 UTF-8 바이트, id - 읽힌 DevPlayerId(실패하면 기본값).
    // 출력: TryRead가 받아들이면 true, 거부하면 false.
    // Phase 11: the raw name bytes of a connect request (version 1; v19 layout: flags 0, no cookie, a 256-byte blob).
    private bool TryReadConnectName(byte[] name, out string id)
    {
        var bytes = new byte[2 + 1 + 2 + ProtocolLimits.RsaBlobBytes + 1 + name.Length];
        bytes[0] = 1;
        bytes[3] = ProtocolLimits.RsaBlobBytes & 0xFF;
        bytes[4] = ProtocolLimits.RsaBlobBytes >> 8;
        bytes[5 + ProtocolLimits.RsaBlobBytes] = (byte)name.Length;
        name.CopyTo(bytes, 6 + ProtocolLimits.RsaBlobBytes);
        var reader = new PacketReader(bytes);
        bool ok = ConnectRequestData.TryRead(ref reader, out var data);
        id = data.DevPlayerId;
        return ok;
    }

    // Phase 11: a name every other client can be sent (it still fits PlayerSpawned after decoding) and can show.
    [Theory]
    [InlineData(new byte[] { 0xFF })]                                       // never valid in UTF-8
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]   // 11 bytes -> 33 decoded
    [InlineData(new byte[] { 0x61, 0xEA, 0xB0 })]                           // "a" + a cut 3-byte sequence
    [InlineData(new byte[] { 0xF0, 0x90, 0x80, 0x41 })]                     // a cut 4-byte sequence: 4 bytes in and out
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]                           // an encoded surrogate (CESU)
    [InlineData(new byte[] { 0xC0, 0xAF })]                                 // an overlong "/"
    [InlineData(new byte[] { 0xEF, 0xBF, 0xBD })]                           // U+FFFD itself
    [InlineData(new byte[] { 0x61, 0x00 })]                                 // C0: NUL
    [InlineData(new byte[] { 0x61, 0x09, 0x62 })]                           // C0: tab
    [InlineData(new byte[] { 0x7F })]                                       // DEL
    [InlineData(new byte[] { 0xC2, 0x9B })]                                 // C1: U+009B
    public void ConnectRequestData_InvalidUtf8OrControlCharacters_AreRejected(byte[] name)
    {
        Assert.False(TryReadConnectName(name, out _));
    }

    [Fact]
    public void ConnectRequestData_ValidNames_UpTo32Bytes_AreAccepted()
    {
        // 10 Hangul syllables (30 bytes) + "ab" = 32 bytes.
        byte[] korean = System.Text.Encoding.UTF8.GetBytes("가나다라마바사아자차ab");
        Assert.Equal(32, korean.Length);
        Assert.True(TryReadConnectName(korean, out string id));
        Assert.Equal("가나다라마바사아자차ab", id);

        byte[] emoji = System.Text.Encoding.UTF8.GetBytes("a😀");   // a surrogate pair: 4 bytes
        Assert.True(TryReadConnectName(emoji, out id));
        Assert.Equal("a😀", id);
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("밥 a-b_c.d", true)]
    [InlineData("abcdefghijabcdefghijabcdefghij12", true)]    // 32 bytes
    [InlineData("abcdefghijabcdefghijabcdefghij123", false)]  // 33 bytes
    [InlineData("가나다라마바사아자차카", false)]                // 33 bytes
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a\u0007", false)]
    [InlineData("a\u0085", false)]
    [InlineData("a�", false)]
    [InlineData("a\uD800", false)]          // a lone high surrogate
    [InlineData("a\uDC00b", false)]         // a lone low surrogate
    [InlineData("a\u200Bb", false)]         // zero-width space (format)
    [InlineData("a\u202Eb", false)]         // right-to-left override (format)
    [InlineData("a\uFEFFb", false)]         // BOM / zero-width no-break space (format)
    [InlineData("a\u2028b", false)]         // line separator
    [InlineData("a\u2029b", false)]         // paragraph separator
    [InlineData("a b", true)]               // an ordinary space is a separator, not a format character
    [InlineData("😀", true)]      // a pair
    public void IsValidPlayerName(string? name, bool valid)
    {
        Assert.Equal(valid, ProtocolConstants.IsValidPlayerName(name!));
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
        // Phase 4 (D14): buttons are 2 bytes, so 30 bytes per command; 3 commands = 92 bytes.
        Assert.Equal(30, PlayerInputPacket.CommandSize);
        Assert.Equal(92, PlayerInputPacket.MaxSize);
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
    [InlineData(1, 29)]    // a Phase 3 sized command (1-byte buttons) is one byte short
    [InlineData(2, 30)]
    [InlineData(3, 89)]
    public void PlayerInput_Truncated_IsRejected(byte count, int payloadBytes)
    {
        var bytes = new byte[2 + payloadBytes];
        bytes[0] = (byte)PacketId.PlayerInput;
        bytes[1] = count;
        var reader = new PacketReader(bytes);
        reader.TryReadPacketId(out _);
        Assert.False(PlayerInputPacket.TryRead(ref reader, out _));
    }

    // Review fix A1 (SEC-1): a PlayerInput is exactly count commands long; bytes after the last command are refused, so
    // a 64 KB packet whose first 92 bytes are valid is Malformed instead of processed.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void PlayerInput_TrailingBytes_AreRejected(byte count)
    {
        var packet = new PlayerInputPacket { Count = count };
        for (int i = 0; i < count; i++) packet.Set(i, FullCommand((uint)(1 + i)));
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        _buffer[writer.Length] = 0;
        var reader = ReaderAfterId(writer.Length + 1, PacketId.PlayerInput);
        Assert.False(PlayerInputPacket.TryRead(ref reader, out _));
    }

    // Review fix A1: every client-to-server packet fits the server's size cap, which the listener checks before parsing.
    [Fact]
    public void EveryClientPacket_FitsTheClientPacketCap()
    {
        Assert.True(PlayerInputPacket.MaxSize <= ProtocolLimits.MaxClientPacketBytes);
        Assert.True(BuildRequest.Size <= ProtocolLimits.MaxClientPacketBytes);
        Assert.True(BuildEditRequest.Size <= ProtocolLimits.MaxClientPacketBytes);
        Assert.True(MapMarker.Size <= ProtocolLimits.MaxClientPacketBytes);
        Assert.True(1 <= ProtocolLimits.MaxClientPacketBytes);   // JoinMatchRequest and StatsRequest: the id only
    }

    // Review fix A1 (and B3): a server packet is at most MaxPacketSize; with LiteNetLib's header and the authentication
    // tail it still fits one datagram of the MTU, so nothing the server sends is ever fragmented (MaxFragments 2 is room
    // to spare, not a need).
    [Fact]
    public void EveryServerPacket_FitsTheMtuWithTransportAndAuthOverhead()
    {
        int[] largest =
        {
            ItemCatalogPacket.MaxSize, WorldItemsPacket.MaxSize, SupplyDropsPacket.MaxSize, TeamMarkersPacket.MaxSize,
            TeamState.MaxSize, StatsResponse.MaxSize, VehicleStatesPacket.MaxSize, BuildCatalogPacket.Size,
            BuildInterestPacket.Size, BuildResult.Size,
            WorldSnapshotHeader.Size + ProtocolConstants.MaxEntitiesPerSnapshotPacket * SnapshotEntity.Size,
        };
        foreach (int size in largest) Assert.True(size <= ProtocolConstants.MaxPacketSize, $"{size} B");
        Assert.True(ProtocolConstants.MaxPacketSize + ProtocolLimits.TransportHeaderBytes + ProtocolLimits.AuthTagBytes <= ProtocolConstants.Mtu);
        // Review fix B3: LiteNetLib's MtuOverride is the user MTU (it does not count the layer's tail), so every packet with
        // its transport header must fit UserMtu.
        Assert.Equal(ProtocolConstants.Mtu - ProtocolLimits.AuthTagBytes, ProtocolLimits.UserMtu);
        Assert.True(ProtocolConstants.MaxPacketSize + ProtocolLimits.TransportHeaderBytes <= ProtocolLimits.UserMtu);
    }

    [Fact]
    public void PlayerInput_UnknownButtonBits_AreMasked()
    {
        var packet = new PlayerInputPacket { Count = 1 };
        packet.Set(0, new InputCommand { Seq = 1, Buttons = (InputButtons)0xFFFF });
        var writer = new PacketWriter(_buffer);
        PlayerInputPacket.Write(ref writer, packet);
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerInput);
        Assert.True(PlayerInputPacket.TryRead(ref reader, out var read));
        Assert.Equal(InputButtons.Jump | InputButtons.Sprint | InputButtons.Fire | InputButtons.Reload |
                     InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.Interact |
                     InputButtons.Drop | InputButtons.UseMedkit | InputButtons.UseShieldCell | InputButtons.Crouch |
                     InputButtons.ToolHarvest | InputButtons.ToolBuild | InputButtons.InteractHeld | InputButtons.ThrowGrenade, read.Get(0).Buttons);
        // Phase 13: ToolHarvest (4096) and ToolBuild (8192); Phase 14: InteractHeld (16384); Phase 17: ThrowGrenade (32768), every bit
        Assert.Equal(0xFFFF, (int)read.Get(0).Buttons);
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
        PlayerSpawned.Write(ref writer, new PlayerSpawned { EntityId = 3, Position = new Vector3(1, 0, 2), Yaw = 45f, Name = "p3" });
        var reader = ReaderAfterId(writer.Length, PacketId.PlayerSpawned);
        Assert.True(PlayerSpawned.TryRead(ref reader, out var s));
        Assert.Equal(3, s.EntityId);
        Assert.Equal(new Vector3(1, 0, 2), s.Position);
        Assert.Equal("p3", s.Name);

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
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 7, AckInputSeq = 0, Count = 2, Part = 1, PartCount = 2 });
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
        Assert.Equal(1, h.Part);
        Assert.Equal(2, h.PartCount);
        Assert.Equal(70, h.Self.Health);
        Assert.Equal(5, h.Self.Shield);
        Assert.Equal(1, h.Self.WeaponSlot);
        Assert.Equal(3, h.Self.Ammo);
        Assert.Equal(300, h.Self.ReloadRemainingTicks);
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e1));
        Assert.True(SnapshotEntity.TryRead(ref reader, out var e2));
        Assert.Equal(new Vector3(1, 2, 3), e1.Position);   // whole numbers are exact in 1/256 fixed point
        Assert.Equal(-1f, e1.VelocityY);
        Assert.True(e1.IsAlive);
        Assert.Equal(2, e2.EntityId);
        Assert.False(e2.IsAlive);
        Assert.Equal(0, reader.Remaining);
    }

    // Phase 8 D3: 19 + 13 * 90 = 1189 bytes must fit one unfragmented datagram (1200).
    [Fact]
    public void SnapshotPacket_WithMaxEntities_Is1197Bytes_AndFitsOneDatagram()
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader
        {
            ServerTick = uint.MaxValue, Count = ProtocolConstants.MaxEntitiesPerSnapshotPacket, Part = 1, PartCount = ProtocolConstants.MaxSnapshotParts,
        });
        for (int i = 0; i < ProtocolConstants.MaxEntitiesPerSnapshotPacket; i++)
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
        Assert.Equal(1197, writer.Length);   // Phase 12 D11: 27 + 90 x 13
        Assert.True(writer.Length <= ProtocolConstants.MaxPacketSize);
    }

    [Theory]
    [InlineData(ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1, 0, 1)]   // too many entities in one packet
    [InlineData(1, 0, 0)]                                                       // no parts
    [InlineData(1, 0, ProtocolConstants.MaxSnapshotParts + 1)]                  // more parts than 100 players need
    [InlineData(1, 1, 1)]                                                       // part index outside the count
    public void SnapshotHeader_OutOfRange_IsRejected(int count, int part, int partCount)
    {
        var writer = new PacketWriter(_buffer);
        WorldSnapshotHeader.Write(ref writer, new WorldSnapshotHeader { ServerTick = 1, Count = (ushort)count, Part = (byte)part, PartCount = (byte)partCount });
        for (int i = 0; i < count && i < ProtocolConstants.MaxEntitiesPerSnapshotPacket + 1; i++)
            SnapshotEntity.Write(ref writer, new SnapshotEntity { EntityId = (ushort)(i + 1) });
        var reader = ReaderAfterId(writer.Length, PacketId.WorldSnapshot);
        Assert.False(WorldSnapshotHeader.TryRead(ref reader, out _));
    }

    // Phase 8 D4: positions and VelocityY within +-128 come back within half a step (1/512); outside they are clamped.
    [Fact]
    public void SnapshotEntity_Quantization_StaysWithinHalfAStep_AndClamps()
    {
        var rng = new Random(4);
        for (int i = 0; i < 2000; i++)
        {
            var original = new SnapshotEntity
            {
                EntityId = 9,
                Position = new Vector3((float)(rng.NextDouble() * 250 - 125), (float)(rng.NextDouble() * 30), (float)(rng.NextDouble() * 250 - 125)),
                VelocityY = (float)(rng.NextDouble() * 60 - 30),
                Yaw = (float)(rng.NextDouble() * 360),
                Flags = SnapshotEntity.AliveFlag,
            };
            SnapshotEntity back = RoundTrip(original);
            Assert.Equal(9, back.EntityId);
            Assert.True(back.IsAlive);
            Assert.InRange(back.Position.X - original.Position.X, -1f / 512f, 1f / 512f);
            Assert.InRange(back.Position.Y - original.Position.Y, -1f / 512f, 1f / 512f);
            Assert.InRange(back.Position.Z - original.Position.Z, -1f / 512f, 1f / 512f);
            Assert.InRange(back.VelocityY - original.VelocityY, -1f / 512f, 1f / 512f);
            float yawError = MathF.Abs(back.Yaw - original.Yaw);
            yawError = MathF.Min(yawError, 360f - yawError);
            Assert.True(yawError <= 360f / 65536f, $"yaw {original.Yaw} -> {back.Yaw}");
        }

        // Box tops and terrain vertices are multiples of 1/256 and arrive exactly.
        Assert.Equal(new Vector3(-46.75f, 6f, 1.03125f), RoundTrip(new SnapshotEntity { Position = new Vector3(-46.75f, 6f, 1.03125f) }).Position);
        Assert.Equal(SnapshotEntity.Quantize(12.3456f), RoundTrip(new SnapshotEntity { Position = new Vector3(12.3456f, 0f, 0f) }).Position.X);

        SnapshotEntity far = RoundTrip(new SnapshotEntity { Position = new Vector3(500f, -500f, float.NaN), VelocityY = float.PositiveInfinity, Yaw = float.NaN });
        Assert.Equal(short.MaxValue / 256f, far.Position.X);
        Assert.Equal(short.MinValue / 256f, far.Position.Y);
        Assert.Equal(0f, far.Position.Z);
        Assert.Equal(0f, far.VelocityY);
        Assert.Equal(0f, far.Yaw);

        Assert.InRange(RoundTrip(new SnapshotEntity { Yaw = -90f }).Yaw, 270f - 0.01f, 270f + 0.01f);
        float almostFull = RoundTrip(new SnapshotEntity { Yaw = 359.999f }).Yaw;
        Assert.True(almostFull < 0.01f || almostFull > 359.99f, $"359.999 -> {almostFull}");
    }

    // 기능: Snapshot Entity 하나를 쓰고 다시 읽어 양자화를 거친 값을 얻는다.
    // 입력: e - 쓸 Entity.
    // 출력: 다시 읽힌 SnapshotEntity. 읽기에 실패하면 Assert 실패.
    private SnapshotEntity RoundTrip(SnapshotEntity e)
    {
        var writer = new PacketWriter(_buffer);
        SnapshotEntity.Write(ref writer, e);
        var reader = new PacketReader(_buffer.AsSpan(0, writer.Length));
        Assert.True(SnapshotEntity.TryRead(ref reader, out SnapshotEntity back));
        return back;
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
