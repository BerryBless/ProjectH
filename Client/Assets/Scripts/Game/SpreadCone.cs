using UnityEngine;

namespace ProjectH.Client.Game
{
    // Phase 17 D3: a direction inside a weapon's spread cone, for our own tracers only. The server spreads every shot with
    // its own seeded random numbers (shooter, server tick, shot number), which the client cannot know, so these tracers are
    // a presentation of the cone, not the real pellets; other players' shots come with their real end point (ShotFired).
    // Managed vector math only (no Quaternion), so the EditMode tests also run outside the Editor.
    public static class SpreadCone
    {
        // 기능: 가운데 방향 둘레의 원뿔 안에서 방향 하나를 고른다(원뿔 단면에서 균일: 각도 = 반각 x sqrt(u), 둘레 각 = 2π v).
        // 입력: direction - 가운데 방향(단위 벡터), halfAngleDegrees - 원뿔 반각(0 이하면 가운데 방향 그대로), u·v - 0..1 난수.
        // 출력: 원뿔 안의 단위 방향(가운데 방향과 이루는 각이 반각 이하).
        public static Vector3 Sample(Vector3 direction, float halfAngleDegrees, float u, float v)
        {
            if (!(halfAngleDegrees > 0f)) return direction;
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);
            float theta = halfAngleDegrees * Mathf.Deg2Rad * Mathf.Sqrt(u);
            float phi = 2f * Mathf.PI * v;
            // Any axis not parallel to the direction gives a perpendicular basis.
            Vector3 helper = Mathf.Abs(direction.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 right = Vector3.Cross(helper, direction).normalized;
            Vector3 up = Vector3.Cross(direction, right);
            Vector3 side = right * Mathf.Cos(phi) + up * Mathf.Sin(phi);
            return (direction * Mathf.Cos(theta) + side * Mathf.Sin(theta)).normalized;
        }
    }
}
