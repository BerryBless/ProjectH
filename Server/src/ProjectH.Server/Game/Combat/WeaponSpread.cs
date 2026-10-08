using System;
using System.Numerics;

namespace ProjectH.Server.Game.Combat;

// Phase 17 D3: the server decides every shot's spread. Each ray is turned inside a cone around the aim by a deterministic
// hash of (shooter entity id, the server tick being simulated, the ray's number in that tick, review fix C1: the match
// secret), so a replay of the same inputs with the same secret gives the same rays and no Random is shared or allocated.
// Directions are uniform over the cone's solid angle. The secret (Match, made at each match start, never sent) keeps a
// client that knows the tick and its entity id from computing the roll and aiming against it; the client draws its own
// tracers (presentation only).
public static class WeaponSpread
{
    // 기능: 조준 방향을 퍼짐 원뿔 안에서 결정적으로 흔든다.
    // 입력: aim - 단위 조준 방향, halfAngleDegrees - 원뿔 반각(0 이하면 그대로), shooterId - 쏜 사람 Entity id,
    //   tick - 시뮬레이션 중인 서버 Tick, ray - 그 Tick 안의 광선 번호(산탄 번호), secret - 경기 비밀(리뷰 수정 C1, DeterministicSeeds면 0).
    // 출력: 원뿔 안의 단위 방향(같은 입력 = 같은 방향). 할당 없음.
    public static Vector3 Spread(Vector3 aim, float halfAngleDegrees, ushort shooterId, uint tick, int ray, ulong secret)
    {
        if (!(halfAngleDegrees > 0f)) return aim;
        ulong h1 = Mix(((ulong)tick << 32) ^ ((ulong)shooterId << 8) ^ (uint)ray ^ secret);
        ulong h2 = Mix(h1 ^ 0x9E3779B97F4A7C15UL);
        float u = (h1 >> 40) * (1f / 16777216f);   // [0, 1)
        float v = (h2 >> 40) * (1f / 16777216f);

        // Uniform over the cap: cos(theta) uniform in [cos(half), 1].
        float cosMax = MathF.Cos(halfAngleDegrees * (MathF.PI / 180f));
        float cosTheta = 1f - u * (1f - cosMax);
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
        float phi = v * (2f * MathF.PI);

        Vector3 helper = MathF.Abs(aim.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 right = Vector3.Normalize(Vector3.Cross(helper, aim));
        Vector3 up = Vector3.Cross(aim, right);
        Vector3 result = aim * cosTheta + (right * MathF.Cos(phi) + up * MathF.Sin(phi)) * sinTheta;
        return Vector3.Normalize(result);
    }

    // 기능: SplitMix64 마무리 함수. 입력의 모든 비트를 출력에 퍼뜨린다(리뷰 수정 C1: Match의 경기 비밀 시드도 쓴다).
    // 입력: x - 섞을 값.
    // 출력: 섞인 64비트 값.
    internal static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
