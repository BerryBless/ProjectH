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
        public const ushort ProtocolVersion = 7;

        public const int MaxDevPlayerIdBytes = 32;
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
