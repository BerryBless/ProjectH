using System;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 6 D2-D3: the terrain grid. Each cell is two triangles split along its (0,0)-(1,1) diagonal.
public class HeightFieldTests
{
    // 기능: 테스트용 3 x 3 정점, 2 m 셀, 원점 (10, 20)의 높이 격자를 만든다.
    // 입력: 없음.
    // 출력: 가운데 정점이 4 m로 가장 높은 고정 높이값의 HeightField.
    // 3 x 3 vertices, 2 m cells, origin (10, 20). Row j = 0 first.
    private static HeightField Sample() => new HeightField(10f, 20f, 2f, 3, 3, new float[]
    {
        0f, 1f, 0f,
        2f, 4f, 0f,
        0f, 0f, 0f,
    });

    [Fact]
    public void AtAVertex_IsThatVertexHeight()
    {
        HeightField f = Sample();
        Assert.Equal(0f, f.Height(10f, 20f));
        Assert.Equal(1f, f.Height(12f, 20f));
        Assert.Equal(2f, f.Height(10f, 22f));
        Assert.Equal(4f, f.Height(12f, 22f));
        Assert.Equal(4f, f.VertexHeight(1, 1));
        Assert.Equal(4f, f.MaxHeight);
    }

    [Fact]
    public void InsideACell_IsLinearOnEachTriangle()
    {
        HeightField f = Sample();
        // Cell (0,0): h00 0, h10 1, h01 2, h11 4. u >= v: h00 + u (h10 - h00) + v (h11 - h10).
        Assert.Equal(0.5f + 0.25f * 3f, f.Height(10f + 2f * 0.5f, 20f + 2f * 0.25f), 6);
        // u < v: h00 + v (h01 - h00) + u (h11 - h01).
        Assert.Equal(0.5f * 2f + 0.25f * 2f, f.Height(10f + 2f * 0.25f, 20f + 2f * 0.5f), 6);
        // On the diagonal both triangles agree.
        Assert.Equal(2f, f.Height(11f, 21f), 6);
        Assert.Equal(f.CellHeight(0, 0, 0.5f, 0.25f), f.Height(11f, 20.5f));
    }

    [Fact]
    public void OutsideTheGrid_ClampsToTheEdge()
    {
        HeightField f = Sample();
        Assert.Equal(f.Height(10f, 22f), f.Height(-500f, 22f));
        Assert.Equal(f.Height(14f, 22f), f.Height(900f, 22f));
        Assert.Equal(f.Height(12f, 20f), f.Height(12f, -1e9f));
        Assert.Equal(f.Height(10f, 20f), f.Height(float.NaN, float.NaN));
    }

    [Fact]
    public void Flat_IsZeroEverywhere()
    {
        Assert.Equal(0f, HeightField.Flat.Height(0f, 0f));
        Assert.Equal(0f, HeightField.Flat.Height(-73.5f, 1234f));
        Assert.Equal(0f, HeightField.Flat.MaxHeight);
    }

    [Fact]
    public void InvalidGrids_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new HeightField(0f, 0f, 2f, 1, 3, new float[3]));
        Assert.Throws<ArgumentException>(() => new HeightField(0f, 0f, 0f, 2, 2, new float[4]));
        Assert.Throws<ArgumentException>(() => new HeightField(0f, 0f, 2f, 2, 2, new float[3]));
        Assert.Throws<ArgumentException>(() => new HeightField(0f, 0f, 2f, 2, 2, new[] { 0f, -1f, 0f, 0f }));
        Assert.Throws<ArgumentException>(() => new HeightField(0f, 0f, 2f, 2, 2, new[] { 0f, float.NaN, 0f, 0f }));
    }

    [Fact]
    public void TheHeightsAreCopied()
    {
        var heights = new float[4];
        var f = new HeightField(0f, 0f, 1f, 2, 2, heights);
        heights[0] = 5f;
        Assert.Equal(0f, f.VertexHeight(0, 0));
    }

    [Fact]
    public void Height_AllocatesNothing()
    {
        HeightField f = Sample();
        f.Height(11f, 21f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0f;
        for (int i = 0; i < 1000; i++) sum += f.Height(10f + i * 0.004f, 20f + i * 0.003f);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(sum > 0f);
    }

    [Fact]
    public void Hill_IsFlatToTheInnerRadius_AndZeroFromTheOuterRadius()
    {
        var hill = new Hill(10, 10, 2, 6, 6 * Hill.UnitsPerMeter);
        Assert.Equal(6 * Hill.UnitsPerMeter, hill.HeightAt(10, 10));
        Assert.Equal(6 * Hill.UnitsPerMeter, hill.HeightAt(12, 10));   // d = 2 = inner radius
        Assert.Equal(0, hill.HeightAt(16, 10));                        // d = 6 = outer radius
        Assert.Equal(0, hill.HeightAt(30, 30));
        // Strictly falling between the radii.
        int previous = hill.HeightAt(12, 10);
        for (int i = 13; i <= 16; i++)
        {
            int h = hill.HeightAt(i, 10);
            Assert.True(h < previous, $"i {i}: {h} >= {previous}");
            previous = h;
        }
    }

    [Fact]
    public void Hill_InIntegerMath_MatchesTheFormula()
    {
        // t = (R^2 - d^2) / (R^2 - r0^2) = (36 - 9) / 36 = 0.75 at d = 3 with r0 = 0; h = 100 * 0.75^2 = 56.25 -> 56.
        var hill = new Hill(0, 0, 0, 6, 100);
        Assert.Equal(56, hill.HeightAt(3, 0));
        Assert.Equal(56, hill.HeightAt(0, -3));
    }

    [Fact]
    public void Build_TakesTheHighestHill_AndQuantizesTo1Over32()
    {
        Hill[] hills =
        {
            new Hill(2, 2, 1, 3, 2 * Hill.UnitsPerMeter),
            new Hill(3, 2, 0, 4, 3 * Hill.UnitsPerMeter),
        };
        HeightField f = Hill.Build(0f, 0f, 2f, 6, 5, hills);
        for (int j = 0; j < 5; j++)
        {
            for (int i = 0; i < 6; i++)
            {
                int expected = Math.Max(hills[0].HeightAt(i, j), hills[1].HeightAt(i, j));
                Assert.Equal(expected / 32f, f.VertexHeight(i, j));
                float units = f.VertexHeight(i, j) * Hill.UnitsPerMeter;
                Assert.Equal(MathF.Floor(units), units);
            }
        }
        HeightField again = Hill.Build(0f, 0f, 2f, 6, 5, hills);
        for (int j = 0; j < 5; j++)
            for (int i = 0; i < 6; i++)
                Assert.Equal(f.VertexHeight(i, j), again.VertexHeight(i, j));
    }
}
