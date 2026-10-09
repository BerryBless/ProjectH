using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Bots;

// Phase 7 D7: can a bot see a point? Samples the segment every Step metres and calls it blocked when a sample lies
// inside a map box or under the terrain. A bot is a client, so this is its own approximation, not the server's
// HitScan: a sample can miss a wall thinner than Step, and then the server simply stops the shot at the wall.
public static class LineOfSight
{
    public const float Step = 0.5f;

    // 기능: from에서 to까지의 선분을 Step 간격으로 표본 삼아 맵 상자 안이나 지형 아래에 걸리는지 본다(봇 자체의 근사, 서버 HitScan이 아니다).
    // 입력: from - 시작점(눈), to - 끝점(표적), boxes - 맵 상자, terrain - 높이 지형.
    // 출력: 어느 표본도 막히지 않으면 true(선분이 Step보다 짧거나 NaN이면 true), 막히면 false.
    public static bool Clear(Vector3 from, Vector3 to, ReadOnlySpan<Box> boxes, HeightField terrain)
    {
        Vector3 delta = to - from;
        float length = delta.Length();
        if (!(length > Step)) return true;   // also NaN: nothing to test
        Vector3 direction = delta / length;
        for (float t = Step; t < length; t += Step)
        {
            Vector3 p = from + direction * t;
            if (p.Y < terrain.Height(p.X, p.Z)) return false;
            for (int i = 0; i < boxes.Length; i++)
            {
                ref readonly Box b = ref boxes[i];
                if (p.X > b.Min.X && p.X < b.Max.X && p.Y > b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z)
                    return false;
            }
        }
        return true;
    }
}
