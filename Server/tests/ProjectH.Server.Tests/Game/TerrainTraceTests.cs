using System;
using System.Numerics;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 6 D11: shots stop at the terrain, and dropped items lie on it.
public class TerrainTraceTests
{
    private static HeightField Terrain => GameMap.Terrain;

    // 기능: 광선을 1 cm씩 전진시켜 지형 아래로 들어가는 첫 구간을 이분법으로 좁혀 교차 거리를 구한다(검증용 기준값).
    // 입력: o - 광선 시작점, d - 방향(단위 벡터), range - 최대 거리.
    // 출력: 지형과 만나는 거리. 맵 밖으로 나가거나 range 안에 만나지 않으면 range.
    // Reference answer: march the ray in 1 cm steps and bisect the first step that ends at or under the surface.
    private static float DenseSample(Vector3 o, Vector3 d, float range)
    {
        float previous = 0f;
        for (float s = 0f; s <= range; s += 0.01f)
        {
            Vector3 p = o + d * s;
            if (MathF.Abs(p.X) > GameMap.HalfSize || MathF.Abs(p.Z) > GameMap.HalfSize) return range;
            if (p.Y <= Terrain.Height(p.X, p.Z))
            {
                float a = previous, b = s;
                for (int k = 0; k < 30; k++)
                {
                    float m = (a + b) * 0.5f;
                    Vector3 q = o + d * m;
                    if (q.Y <= Terrain.Height(q.X, q.Z)) b = m; else a = m;
                }
                return b;
            }
            previous = s;
        }
        return range;
    }

    // Review Focus: grazing a crest, rays along a grid axis and along the cell diagonal.
    [Fact]
    public void TraceTerrain_MatchesDenseSampling_On1000Rays()
    {
        var rng = new Random(3);
        int hits = 0;
        for (int n = 0; n < 1000; n++)
        {
            float ox = rng.Next(-78, 78) + (float)rng.NextDouble();
            float oz = rng.Next(-78, 78) + (float)rng.NextDouble();
            var o = new Vector3(ox, Terrain.Height(ox, oz) + 0.2f + (float)rng.NextDouble() * 8f, oz);
            Vector3 d = Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 0.6f - 0.4f, (float)rng.NextDouble() * 2f - 1f));
            if (n % 10 == 0) d = Vector3.Normalize(new Vector3(1f, -0.05f, 0f));
            if (n % 10 == 1) d = Vector3.Normalize(new Vector3(0f, -0.05f, -1f));
            if (n % 10 == 2) d = Vector3.Normalize(new Vector3(1f, -0.05f, 1f));
            float exact = HitScan.TraceTerrain(o, d, 300f, Terrain);
            float reference = DenseSample(o, d, 300f);
            Assert.True(MathF.Abs(exact - reference) <= 0.01f, $"ray {n} from {o} along {d}: {exact} vs {reference}");
            if (exact < 300f) hits++;
        }
        Assert.InRange(hits, 300, 700);   // both hits and misses are exercised
    }

    [Fact]
    public void TraceTerrain_AboveTheHighestPoint_Misses()
    {
        Assert.Equal(300f, HitScan.TraceTerrain(new Vector3(-70f, Terrain.MaxHeight + 0.5f, 0f), Vector3.UnitX, 300f, Terrain));
        Assert.Equal(300f, HitScan.TraceTerrain(new Vector3(0f, 1f, 0f), Vector3.Normalize(new Vector3(1f, 1f, 0f)), 300f, Terrain));
    }

    [Fact]
    public void TraceTerrain_StraightDown_RespectsTheRange()
    {
        var o = new Vector3(44f, Terrain.Height(44f, -44f) + 1f, -44f);
        Assert.Equal(0.5f, HitScan.TraceTerrain(o, -Vector3.UnitY, 0.5f, Terrain));
        Assert.Equal(1f, HitScan.TraceTerrain(o, -Vector3.UnitY, 5f, Terrain), 1e-4f);
    }

    [Fact]
    public void TraceTerrain_StartingUnderTheSurface_HitsAtZero()
    {
        Assert.Equal(0f, HitScan.TraceTerrain(new Vector3(0f, 1f, 46f), Vector3.UnitX, 50f, Terrain));
    }

    [Fact]
    public void TraceTerrain_NonFiniteInput_IsAMiss()
    {
        Assert.Equal(50f, HitScan.TraceTerrain(new Vector3(float.NaN, 1f, 0f), Vector3.UnitX, 50f, Terrain));
        Assert.Equal(50f, HitScan.TraceTerrain(new Vector3(0f, 1f, 0f), new Vector3(float.PositiveInfinity, 0f, 0f), 50f, Terrain));
    }

    // Through the north hill (4 m at (0, 46)): a chest-high ray from the west side stops on the near slope.
    [Fact]
    public void TraceWorld_StopsAtAHill()
    {
        var origin = new Vector3(-30f, 1.6f, 46f);
        float d = HitScan.TraceWorld(origin, Vector3.UnitX, 100f, GameMap.Boxes, Terrain);
        Vector3 end = origin + Vector3.UnitX * d;
        Assert.InRange(end.X, -18f, 0f);
        Assert.Equal(1.6f, Terrain.Height(end.X, end.Z), 3);
    }

    // Review Focus: from the Lookout plateau (6 m) a shallow shot clears the 4 m outer wall and leaves the grid;
    // the y = 0 plane still stops it.
    [Fact]
    public void TraceWorld_LeavingTheGridOverTheWall_StopsAtTheFloorPlane()
    {
        var origin = new Vector3(44f, 7.6f, -40.5f);
        Vector3 d = Vector3.Normalize(new Vector3(1f, -0.08f, 0f));
        float distance = HitScan.TraceWorld(origin, d, 300f, GameMap.Boxes, Terrain);
        Assert.Equal(-origin.Y / d.Y, distance, 3);
        Assert.True((origin + d * distance).X > GameMap.HalfSize + 1f, "should have cleared the wall");
    }

    [Fact]
    public void TraceWorld_OnTerrain_AllocatesNothing()
    {
        var origin = new Vector3(-30f, 1.6f, 46f);
        HitScan.TraceWorld(origin, Vector3.UnitX, 100f, GameMap.Boxes, Terrain);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0f;
        for (int i = 0; i < 200; i++) sum += HitScan.TraceWorld(origin, Vector3.Normalize(new Vector3(1f, -0.01f * (i % 5), 0.1f)), 300f, GameMap.Boxes, Terrain);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(sum > 0f);
    }

    // Review Focus: a drop on a slope lies on the terrain ahead, uphill or downhill, not at the feet.
    [Theory]
    [InlineData(0f)]     // uphill, towards the north hill's top
    [InlineData(180f)]   // downhill
    [InlineData(90f)]    // across the slope
    public void Drop_OnASlope_LiesOnTheTerrain(float yaw)
    {
        var feet = new Vector3(0f, Terrain.Height(0f, 36f), 36f);
        Assert.True(feet.Y > 0.5f, "the test point must be on the slope");
        Vector3 p = ItemRules.DropPosition(feet, ItemRules.Offset(yaw, ItemRules.DropDistance), GameMap.Boxes, Terrain);
        Vector3 expected = feet + ItemRules.Offset(yaw, ItemRules.DropDistance);
        Assert.Equal(expected.X, p.X, 5);
        Assert.Equal(expected.Z, p.Z, 5);
        Assert.Equal(Terrain.Height(p.X, p.Z), p.Y);
    }
}
