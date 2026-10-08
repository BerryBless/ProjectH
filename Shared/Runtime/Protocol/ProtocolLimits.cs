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
        // D3: ranges a client accepts from the server. The server's data checks use the same constants (WeaponCatalog for the
        // projectile kinds, BuildingCatalog for the interest cell), so a correct server never sends what a client refuses: the
        // zone stays in the map, and WeaponCatalog.ValidateProjectile bounds a projectile's speed, its speed after falling and
        // its flight distance (speed x lifetime + 1/2 x gravity x lifetime^2 from a launch inside the walls: a bounce can turn
        // the fallen speed sideways but never adds speed) against these limits.
        public const float ZoneCoordLimit = Simulation.GameMap.HalfSize + 10000f;
        public const float ZoneRadiusLimit = 10000f;
        // A projectile is launched at an eye inside the map; 512 m leaves room for a flight out over the walls before it
        // explodes, and WeaponCatalog.ValidateProjectile refuses a kind whose flight could reach further.
        public const float ProjectilePositionLimit = 512f;
        // The launch speed, and the speed a projectile can reach before it explodes (WeaponCatalog checks speed + gravity x
        // lifetime against it).
        public const float ProjectileSpeedLimit = 200f;
        public const float ProjectileGravityLimit = 50f;
        public const float ProjectileRadiusLimit = 10f;
        // The interest cell sizes that split the 160 m map into whole build cells, at most 8 x 8 (BuildingCatalog).
        public static readonly int[] InterestCellSizes = { 20, 40, 80, 160 };

        // 기능: 관심 영역 칸 크기가 서버가 쓸 수 있는 값(InterestCellSizes)인지 본다(리뷰 수정 D3).
        // 입력: size - 칸 크기(m).
        // 출력: 20·40·80·160 중 하나면 true(NaN은 false).
        public static bool IsInterestCellSize(float size)
        {
            for (int i = 0; i < InterestCellSizes.Length; i++)
            {
                if (size == InterestCellSizes[i]) return true;
            }
            return false;
        }

        // 기능: 값이 유한하고 절댓값이 limit 이하인지 본다(리뷰 수정 D3, 받은 좌표·속도 검사).
        // 입력: value - 값, limit - 한계(양수).
        // 출력: 범위 안이면 true(NaN·Infinity는 false).
        public static bool Within(float value, float limit) => value >= -limit && value <= limit;

        // 기능: 벡터의 세 성분이 모두 유한하고 절댓값이 limit 이하인지 본다(리뷰 수정 D3).
        // 입력: v - 벡터, limit - 한계.
        // 출력: 범위 안이면 true.
        public static bool Within(System.Numerics.Vector3 v, float limit) => Within(v.X, limit) && Within(v.Y, limit) && Within(v.Z, limit);
    }
}
