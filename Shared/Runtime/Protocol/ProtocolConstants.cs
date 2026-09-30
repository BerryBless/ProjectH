namespace ProjectH.Shared.Protocol
{
    public static class ProtocolConstants
    {
        // Bump whenever any packet layout changes; the server rejects other versions at connect time.
        public const ushort ProtocolVersion = 2;   // 2: Phase 1 box collision changed movement results

        public const int MaxDevPlayerIdBytes = 32;
        public const int MaxInputsPerPacket = 3;

        // LiteNetLib does not fragment Unreliable/Sequenced packets, so one snapshot must fit one datagram.
        public const int MaxPacketSize = 1200;

        // UDP payload size LiteNetLib may use per datagram (NetManager.MtuOverride). LiteNetLib's
        // default starting MTU is 1024 (1020 bytes of user data), which is below MaxPacketSize.
        // 1232 is the common internet-safe payload (IPv6 minimum MTU 1280 - 48 bytes of headers)
        // and leaves 1228 bytes for a Sequenced packet, so MaxPacketSize always fits.
        public const int Mtu = 1232;

        // (1200 - 11 header bytes) / 22 bytes per entity = 54; 50 leaves headroom.
        public const int MaxSnapshotEntities = 50;
    }
}
