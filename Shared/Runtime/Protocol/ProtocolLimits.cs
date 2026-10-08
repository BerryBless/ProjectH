namespace ProjectH.Shared.Protocol
{
    // Review fixes A1, A4, B3, D3: limits the server's checks and the client's parsers share. Changing one changes both
    // sides at once (that is why they live here and not in each side's code).
    public static class ProtocolLimits
    {
        // A1: the largest client-to-server packet is PlayerInput (92 B); anything above this is Malformed before parsing.
        public const int MaxClientPacketBytes = 128;
        // A4: the smallest input Seq window (64 ticks = 2 s at 30 Hz). The server's window is ServerOptions.InputSeqWindow,
        // which covers a network outage up to the disconnect timeout and is never below this.
        public const int MaxInputSeqAhead = 64;
        // A1: NetManager.MaxFragmentsCount on the server, the client and the bots. No game packet needs fragments
        // (every one is at most ProtocolConstants.MaxPacketSize); 2 leaves room for a header miscount.
        public const int MaxFragments = 2;
        // LiteNetLib's reliable/sequenced header (the largest one a game packet gets).
        public const int TransportHeaderBytes = 4;
        // B3: the datagram tail of the authentication layer: counter 4 + MAC 16.
        public const int AuthTagBytes = 20;
        // B3: NetManager.MtuOverride on every side. LiteNetLib does not count the layer's tail in the MTU (measured: with
        // MtuOverride 1232 and a 20-byte layer a Sequenced packet could still be 1228 bytes), so the user MTU is the wire
        // budget minus the tail: a datagram on the wire stays within ProtocolConstants.Mtu, and a Sequenced packet still has
        // 1212 - 4 = 1208 >= MaxPacketSize bytes.
        public const int UserMtu = ProtocolConstants.Mtu - AuthTagBytes;
        public const int MacBytes = 16;
        // A3: the connect cookie the server sends back in a RejectForce and the client repeats in its next request.
        public const int CookieBytes = 16;
        public const int SessionKeyBytes = 32;
        public const int RsaBlobBytes = 256;           // RSA-2048
        public const int ResumeProofBytes = 16;
        // D3: ranges a client accepts from the server.
        public const float ZoneCoordLimit = Simulation.GameMap.HalfSize + 10000f;
        public const float ZoneRadiusLimit = 10000f;
        public const float ProjectilePositionLimit = 512f;
        public const float ProjectileSpeedLimit = 200f;
        public const float ProjectileGravityLimit = 50f;
        public const float ProjectileRadiusLimit = 10f;
        public static readonly int[] InterestCellSizes = { 20, 40, 80, 160 };
    }
}
