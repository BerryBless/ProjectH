using System.Numerics;

namespace ProjectH.Shared.Protocol
{
    // Phase 17 D6, D7: the server simulates every projectile (a grenade or a rocket); clients only draw them. Between two
    // events a client extrapolates p(t) = Position + Velocity x dt + 0.5 x (0, -Gravity, 0) x dt^2 with dt = (tick - Tick) /
    // SimHz and the kind's Gravity from WeaponCatalog's ProjectileInfo. A zero Velocity means the projectile rests (no
    // gravity until the next event). All three packets are ReliableOrdered on channel 0, to everyone, never in the snapshot.
    // Ids are the server's projectile ids (1..65535, wrapping, never 0); an id lives from its ProjectileSpawned to its
    // ProjectileExploded, a match start, a round reset or the match end (the server sends nothing then: clients clear their
    // projectiles at a reset, a disconnect and when the match finishes).

    // S->C: a projectile was launched (or, to a newcomer or a resumed player, is still flying). Position and Velocity are
    // its state at StartTick. Layout: [PacketId 1][Id 2][Kind 1][OwnerId 2][Position 12][Velocity 12][StartTick 4] = 34 bytes.
    public struct ProjectileSpawned
    {
        public const int Size = 34;   // with the packet id

        public ushort Id;
        public ProjectileKind Kind;
        public ushort OwnerId;        // the launcher's entity id (0 = it is no longer in the match: a resend to a newcomer or a resumed player)
        public Vector3 Position;
        public Vector3 Velocity;
        public uint StartTick;

        // 기능: ProjectileSpawned 패킷을 쓴다.
        // 입력: writer - 대상, p - 투사체 생성 사건.
        // 출력: 반환값 없음. writer에 34바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in ProjectileSpawned p)
        {
            writer.WriteByte((byte)PacketId.ProjectileSpawned);
            writer.WriteUInt16(p.Id);
            writer.WriteByte((byte)p.Kind);
            writer.WriteUInt16(p.OwnerId);
            writer.WriteVector3(p.Position);
            writer.WriteVector3(p.Velocity);
            writer.WriteUInt32(p.StartTick);
        }

        // 기능: ProjectileSpawned 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 사건. 짧거나 id 0이거나 모르는 종류이거나 위치·속도가 유한하지 않으면 false. 리뷰 수정 D3: 위치 성분이
        //   ±ProjectilePositionLimit 밖이거나 속력이 ProjectileSpeedLimit를 넘어도 false.
        public static bool TryRead(ref PacketReader reader, out ProjectileSpawned p)
        {
            p = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out p.Id);
            reader.TryReadByte(out byte kind);
            reader.TryReadUInt16(out p.OwnerId);
            reader.TryReadVector3(out p.Position);
            reader.TryReadVector3(out p.Velocity);
            reader.TryReadUInt32(out p.StartTick);
            if (p.Id == 0 || kind == 0 || kind > (byte)ProjectileKind.Rocket) return false;
            p.Kind = (ProjectileKind)kind;
            return InRange(p.Position, p.Velocity);
        }

        // 기능: 투사체 위치·속도가 서버가 보낼 수 있는 범위인지 본다(리뷰 수정 D3, Spawned·State 공용).
        // 입력: position - 위치, velocity - 속도.
        // 출력: 위치 성분이 ±ProjectilePositionLimit 안이고 속력이 ProjectileSpeedLimit 이하면(모두 유한) true.
        internal static bool InRange(Vector3 position, Vector3 velocity) =>
            ProtocolLimits.Within(position, ProtocolLimits.ProjectilePositionLimit) &&
            ProtocolLimits.Within(velocity, ProtocolLimits.ProjectileSpeedLimit) &&
            velocity.LengthSquared() <= ProtocolLimits.ProjectileSpeedLimit * ProtocolLimits.ProjectileSpeedLimit;
    }

    // S->C: a projectile's flight changed other than by gravity (a grenade bounced or came to rest). Position and Velocity
    // are its state at Tick; a zero Velocity = resting. Layout: [PacketId 1][Id 2][Position 12][Velocity 12][Tick 4] = 31 bytes.
    public struct ProjectileState
    {
        public const int Size = 31;   // with the packet id

        public ushort Id;
        public Vector3 Position;
        public Vector3 Velocity;
        public uint Tick;

        // 기능: ProjectileState 패킷을 쓴다.
        // 입력: writer - 대상, s - 튕김·멈춤 사건.
        // 출력: 반환값 없음. writer에 31바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in ProjectileState s)
        {
            writer.WriteByte((byte)PacketId.ProjectileState);
            writer.WriteUInt16(s.Id);
            writer.WriteVector3(s.Position);
            writer.WriteVector3(s.Velocity);
            writer.WriteUInt32(s.Tick);
        }

        // 기능: ProjectileState 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 사건. 짧거나 id 0이거나 위치·속도가 유한하지 않으면 false. 리뷰 수정 D3: ProjectileSpawned와 같은 범위 밖이어도 false.
        public static bool TryRead(ref PacketReader reader, out ProjectileState s)
        {
            s = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out s.Id);
            reader.TryReadVector3(out s.Position);
            reader.TryReadVector3(out s.Velocity);
            reader.TryReadUInt32(out s.Tick);
            return s.Id != 0 && ProjectileSpawned.InRange(s.Position, s.Velocity);
        }
    }

    // S->C: a projectile exploded at Position (the client removes it and shows the explosion with the kind's radius).
    // Layout: [PacketId 1][Id 2][Position 12][Kind 1] = 16 bytes.
    public struct ProjectileExploded
    {
        public const int Size = 16;   // with the packet id

        public ushort Id;
        public Vector3 Position;
        public ProjectileKind Kind;

        // 기능: ProjectileExploded 패킷을 쓴다.
        // 입력: writer - 대상, e - 폭발 사건.
        // 출력: 반환값 없음. writer에 16바이트가 쓰인다.
        public static void Write(ref PacketWriter writer, in ProjectileExploded e)
        {
            writer.WriteByte((byte)PacketId.ProjectileExploded);
            writer.WriteUInt16(e.Id);
            writer.WriteVector3(e.Position);
            writer.WriteByte((byte)e.Kind);
        }

        // 기능: ProjectileExploded 본문(PacketId 뒤)을 읽는다.
        // 입력: reader - 본문.
        // 출력: 성공하면 true와 사건. 짧거나 id 0이거나 모르는 종류이거나 위치가 유한하지 않으면 false.
        public static bool TryRead(ref PacketReader reader, out ProjectileExploded e)
        {
            e = default;
            if (reader.Remaining < Size - 1) return false;
            reader.TryReadUInt16(out e.Id);
            reader.TryReadVector3(out e.Position);
            reader.TryReadByte(out byte kind);
            if (e.Id == 0 || kind == 0 || kind > (byte)ProjectileKind.Rocket) return false;
            e.Kind = (ProjectileKind)kind;
            return Finite.Check(e.Position);
        }
    }
}
