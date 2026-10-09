namespace ProjectH.Shared.Protocol
{
    public static class ProtocolConstants
    {
        // Bump whenever any packet layout changes; the server rejects other versions at connect time.
        // 2: Phase 1 box collision changed movement results. 3: Phase 3 combat (aim in inputs, snapshot flags and self block, combat packets).
        // 4: Phase 4 inventory (2-byte buttons, ammo type in the weapon catalog, item packets).
        // 5: Phase 5 battle royale (MatchState, ZoneState, MatchResult, Placement in PlayerDied).
        // 6: Phase 6 map (terrain and the new boxes change movement results; packet layouts are unchanged).
        // 7: Phase 8 snapshots (13-byte quantized entities, a snapshot split into up to MaxSnapshotParts packets).
        // 8: Phase 10 hardening (DisconnectCode in the disconnect data, JoinResult.Resumed).
        // 9: Phase 11 game UI (StatsRequest/StatsResponse, the player's name in PlayerSpawned).
        // 10: Phase 12 deployment and traversal (movement modes in the snapshot flags, a 14-byte self block, the Crouch
        //     button, TransportRoute, DoorStates, the mode in PlayerRespawned, the cause in PlayerDied; movement changed).
        // 11: Phase 13 harvesting and building (the tool in the snapshot flags and the self block, two tool buttons, the
        //     build and harvest packets, the Material world item, LiteNetLib channel 1; movement collides with pieces,
        //     harvestables and slopes).
        // 12: Phase 13.5 building edit (BuildEditRequest, the NotOwner and NotFound results, the edit state in bits 20-31 of
        //     a piece record's grid word, the Edited records and the 9-byte BuildEvents header; edited pieces collide by
        //     their parts).
        // 13: Phase 14 squads (TeamState, PlayerDowned, ChannelState, RebootStations, the Downed movement mode 7, the
        //     InteractHeld button, the RebootCard world item, the card count in InventoryState; downed movement crawls).
        // 14: Phase 15 map pings and waypoints (MapMarker C->S, TeamMarkers S->C).
        // 15: Phase 16 loot containers and supply drops (ContainerStates, SupplyDrops; both S->C).
        // 16: Phase 17 weapons and throwables (the WeaponInfo pellets, spread, recoil and projectile fields and the projectile
        //     list of WeaponCatalog, the Shells and Rockets ammo types, the Grenade consumable, a 27-byte InventoryState, the
        //     ThrowGrenade button, ProjectileSpawned/ProjectileState/ProjectileExploded, DeathCause.Explosion).
        // 17: Phase 18 gameplay audio (the weapon id in ShotFired, the reason byte of a BuildEvents Destroyed record (5 bytes),
        //     the shield flags in DamageTaken, WorldSound).
        // 18: Phase 19 vehicles (VehicleStates S->C, Unreliable on channel 0; seated players keep their movement mode).
        // 19: review fixes A-D (the connect request's flags and cookie, then the session key and resume proof, a uint
        //     ViewTick, the 20-byte authentication tail of every datagram).
        public const ushort ProtocolVersion = 19;

        public const int MaxDevPlayerIdBytes = 32;

        // 기능: 플레이어 이름(DevPlayerId)이 프로토콜의 이름 규칙에 맞는지 검사한다(서버 연결 요청·타이틀 화면·봇 공용).
        // 입력: name - 검사할 이름.
        // 출력: 1..MaxDevPlayerIdBytes 바이트의 UTF-8이고 제어·형식·줄 구분 문자, U+FFFD, 홀로 남은 Surrogate가 없으면 true, 아니면(null·빈 문자열 포함) false.
        // Phase 11: the one player-name (DevPlayerId) rule. The server checks it on every connect request, the title
        // screen and the bots check it before sending. A name is 1-MaxDevPlayerIdBytes bytes of valid UTF-8 without
        // control characters (C0, DEL, C1), format characters or line/paragraph separators. Every other client shows the name in PlayerSpawned, which carries at most
        // MaxDevPlayerIdBytes: invalid bytes decode to U+FFFD (3 bytes each) and would make the name too long to send,
        // and control characters would break the UI's lines. U+FFFD itself is refused because the received name is
        // decoded with replacement, so a refused U+FFFD is exactly "the bytes were not valid UTF-8"; a lone surrogate
        // (a string that has no UTF-8 form) is refused for the same reason. Allocates nothing.
        public static bool IsValidPlayerName(string name)
        {
            if (name == null || name.Length == 0) return false;
            int bytes = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsControl(c) || c == '�' || IsInvisibleOrBreaking(c)) return false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1])) return false;
                    bytes += 4;
                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return false;
                }
                else
                {
                    bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                }
                if (bytes > MaxDevPlayerIdBytes) return false;
            }
            return true;
        }

        // 기능: 문자가 이름에 넣을 수 없는 보이지 않는 형식 문자이거나 줄·문단 구분 문자인지 본다.
        // 입력: c - 검사할 문자.
        // 출력: Unicode 범주가 Format, LineSeparator, ParagraphSeparator 중 하나면 true, 아니면 false.
        // Format characters (zero-width spaces and joiners, bidi overrides, BOM) are invisible, so one name could pose
        // as another; line and paragraph separators break the UI's lines like control characters do.
        private static bool IsInvisibleOrBreaking(char c)
        {
            System.Globalization.UnicodeCategory category = char.GetUnicodeCategory(c);
            return category == System.Globalization.UnicodeCategory.Format
                || category == System.Globalization.UnicodeCategory.LineSeparator
                || category == System.Globalization.UnicodeCategory.ParagraphSeparator;
        }

        public const int MaxInputsPerPacket = 3;

        // Phase 13 D13: LiteNetLib channels. Everything before Phase 13 uses channel 0; the building stream (BuildRequest,
        // BuildResult, BuildEvents, BuildSync, BuildInterest) has channel 1 to itself, so a burst of building never queues
        // ahead of a death or a hit on channel 0's ReliableOrdered window. Server, client and bots set ChannelCount.
        public const byte ReliableChannel = 0;
        public const byte BuildChannel = 1;
        public const int ChannelCount = 2;

        // LiteNetLib does not fragment Unreliable/Sequenced packets, so one snapshot packet must fit one datagram (a snapshot is up to MaxSnapshotParts packets).
        public const int MaxPacketSize = 1200;

        // UDP payload size LiteNetLib may use per datagram (NetManager.MtuOverride). LiteNetLib's
        // default starting MTU is 1024 (1020 bytes of user data), which is below MaxPacketSize.
        // 1232 is the common internet-safe payload (IPv6 minimum MTU 1280 - 48 bytes of headers)
        // and leaves 1228 bytes for a Sequenced packet, so MaxPacketSize always fits.
        public const int Mtu = 1232;

        // Phase 8 D3: the most players a match (and so a snapshot) can hold. A snapshot is split into packets of at most
        // MaxEntitiesPerSnapshotPacket entities: (1200 - 27 header bytes) / 13 bytes per entity = 90 (Phase 12: 1197 bytes,
        // 3 to spare), so 100 players take MaxSnapshotParts = 2 packets (pinned by PacketTests).
        public const int MaxSnapshotEntities = 100;
        public const int MaxEntitiesPerSnapshotPacket = 90;
        public const int MaxSnapshotParts = (MaxSnapshotEntities + MaxEntitiesPerSnapshotPacket - 1) / MaxEntitiesPerSnapshotPacket;
    }
}
