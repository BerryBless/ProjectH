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
        public const ushort ProtocolVersion = 9;

        public const int MaxDevPlayerIdBytes = 32;

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

        // LiteNetLib does not fragment Unreliable/Sequenced packets, so one snapshot packet must fit one datagram (a snapshot is up to MaxSnapshotParts packets).
        public const int MaxPacketSize = 1200;

        // UDP payload size LiteNetLib may use per datagram (NetManager.MtuOverride). LiteNetLib's
        // default starting MTU is 1024 (1020 bytes of user data), which is below MaxPacketSize.
        // 1232 is the common internet-safe payload (IPv6 minimum MTU 1280 - 48 bytes of headers)
        // and leaves 1228 bytes for a Sequenced packet, so MaxPacketSize always fits.
        public const int Mtu = 1232;

        // Phase 8 D3: the most players a match (and so a snapshot) can hold. A snapshot is split into packets of at most
        // MaxEntitiesPerSnapshotPacket entities: (1200 - 19 header bytes) / 13 bytes per entity = 90, so 100 players
        // take MaxSnapshotParts = 2 packets (pinned by PacketTests).
        public const int MaxSnapshotEntities = 100;
        public const int MaxEntitiesPerSnapshotPacket = 90;
        public const int MaxSnapshotParts = (MaxSnapshotEntities + MaxEntitiesPerSnapshotPacket - 1) / MaxEntitiesPerSnapshotPacket;
    }
}
