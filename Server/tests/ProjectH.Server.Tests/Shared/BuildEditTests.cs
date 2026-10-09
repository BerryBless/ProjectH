using System;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13.5 D1-D3: edit states (BuildEdit) and the parts of an edited piece (BuildGrid.PartsOf).
public class BuildEditTests
{
    private const int Window = 1 << 4;                 // the middle tile
    private const int Door = (1 << 1) | (1 << 4);      // the middle column's two lower tiles
    private const int HalfWall = 0b111_000_000;        // the top row
    private const int TallOpening = (1 << 1) | (1 << 4) | (1 << 7);

    [Fact]
    public void WallStates_AreOneConnectedHoleThatLeavesATile()
    {
        foreach (int ok in new[] { 0, Window, Door, HalfWall, TallOpening, 0b011_011_011, 0b111_111_110, 0b100_100_111 })
            Assert.True(BuildEdit.IsValid(BuildPieceType.Wall, ok), ok.ToString());
        foreach (int bad in new[] { 0b111_111_111, 0b101, 0b100_000_001, 1 << 9, -1, 1 << 12 })
            Assert.False(BuildEdit.IsValid(BuildPieceType.Wall, bad), bad.ToString());
    }

    [Fact]
    public void FloorRoofAndRampStates_HaveTheirRanges()
    {
        for (int e = 0; e < 16; e++) Assert.Equal(e < 15, BuildEdit.IsValid(BuildPieceType.Floor, e));
        for (int e = 0; e < 16; e++) Assert.Equal(e <= BuildEdit.RoofPassage, BuildEdit.IsValid(BuildPieceType.Roof, e));
        Assert.True(BuildEdit.IsValid(BuildPieceType.Ramp, 0));
        Assert.False(BuildEdit.IsValid(BuildPieceType.Ramp, 1));
    }

    // Every 12-bit value: the check never throws and accepts exactly the counted states (wall: the connected holes).
    [Fact]
    public void EveryValue_IsCheckedWithoutThrowing()
    {
        int walls = 0;
        for (int e = 0; e <= BuildEdit.Mask; e++)
        {
            if (BuildEdit.IsValid(BuildPieceType.Wall, e)) walls++;
            BuildEdit.IsValid(BuildPieceType.Floor, e);
            BuildEdit.IsValid((BuildPieceType)7, e);
        }
        // 0 and every connected proper subset of the 3 x 3 grid (polyominoes placed on it, up to 8 tiles).
        Assert.True(walls > 100 && walls < 512, walls.ToString());
    }

    [Fact]
    public void ASelection_BecomesTheSameState_ForWallsAndFloors()
    {
        Assert.True(BuildEdit.FromSelection(BuildPieceType.Wall, Door, 1, out int edit, out int rotation));
        Assert.Equal((Door, 1), (edit, rotation));
        Assert.False(BuildEdit.FromSelection(BuildPieceType.Wall, 0b101, 0, out edit, out _));
        Assert.Equal(0, edit);
        Assert.True(BuildEdit.FromSelection(BuildPieceType.Floor, 0b1001, 0, out edit, out _));   // diagonal
        Assert.Equal(0b1001, edit);
        Assert.False(BuildEdit.FromSelection(BuildPieceType.Floor, 15, 0, out _, out _));
        Assert.False(BuildEdit.FromSelection(BuildPieceType.Floor, 16, 0, out _, out _));
        Assert.False(BuildEdit.FromSelection(BuildPieceType.Wall, -1, 0, out _, out _));
    }

    [Theory]
    [InlineData(0b0011, 1)]   // the -Z side low: rising toward +Z (direction 0)
    [InlineData(0b0101, 2)]   // -X low: +X
    [InlineData(0b1100, 3)]   // +Z low: -Z
    [InlineData(0b1010, 4)]   // +X low: -X
    [InlineData(0, BuildEdit.RoofPyramid)]
    [InlineData(15, BuildEdit.RoofFlat)]
    [InlineData(0b1001, BuildEdit.RoofPassage)]
    [InlineData(0b0110, BuildEdit.RoofPassage)]
    public void ARoofSelection_PicksItsVariant(int selection, int expected)
    {
        Assert.True(BuildEdit.FromSelection(BuildPieceType.Roof, selection, 0, out int edit, out int rotation));
        Assert.Equal((expected, 0), (edit, rotation));
    }

    [Fact]
    public void ARampSelection_TurnsIt_TheLowSideBeingTheChosenOne()
    {
        Assert.True(BuildEdit.FromSelection(BuildPieceType.Ramp, 0b1100, 0, out int edit, out int rotation));
        Assert.Equal((0, 2), (edit, rotation));
        Assert.True(BuildEdit.FromSelection(BuildPieceType.Ramp, 0, 3, out edit, out rotation));
        Assert.Equal((0, 3), (edit, rotation));
        Assert.False(BuildEdit.FromSelection(BuildPieceType.Ramp, 0b0001, 3, out _, out rotation));
        Assert.Equal(3, rotation);
        Assert.False(BuildEdit.FromSelection(BuildPieceType.Roof, 0b0111, 0, out _, out _));
    }

    [Fact]
    public void TryApply_KeepsTheCell_AndRefusesARotationChangeOutsideRamps()
    {
        var wall = new BuildPieceShape(BuildPieceType.Wall, 5, 1, 6, 1);
        Assert.True(BuildEdit.TryApply(wall, BuildEdit.PackState(Door, 1), out BuildPieceShape doored));
        Assert.Equal((wall.Type, wall.X, wall.Y, wall.Z, wall.Rotation, (ushort)Door), (doored.Type, doored.X, doored.Y, doored.Z, doored.Rotation, doored.Edit));
        Assert.Equal(BuildGrid.SlotKey(wall), BuildGrid.SlotKey(doored));
        Assert.NotEqual(wall, doored);
        Assert.NotEqual(wall.GetHashCode(), doored.GetHashCode());
        Assert.False(BuildEdit.TryApply(wall, BuildEdit.PackState(Door, 0), out _));
        Assert.False(BuildEdit.TryApply(wall, (ushort)(BuildEdit.PackState(Door, 1) | 0x4000), out _));
        Assert.False(BuildEdit.TryApply(wall, BuildEdit.PackState(0b101, 1), out _));

        var ramp = new BuildPieceShape(BuildPieceType.Ramp, 5, 1, 6, 0);
        Assert.True(BuildEdit.TryApply(ramp, BuildEdit.PackState(0, 3), out BuildPieceShape turned));
        Assert.Equal(3, turned.Rotation);
        Assert.Equal(BuildGrid.SlotKey(ramp), BuildGrid.SlotKey(turned));
        Assert.False(BuildEdit.TryApply(ramp, BuildEdit.PackState(1, 0), out _));
    }

    // D3: Edit 0 gives exactly the Phase 13 shape (no regression).
    [Fact]
    public void EditZero_IsThePhase13Shape()
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        foreach (var shape in new[]
        {
            new BuildPieceShape(BuildPieceType.Wall, 3, 2, 4, 0), new BuildPieceShape(BuildPieceType.Wall, 3, 2, 4, 1),
            new BuildPieceShape(BuildPieceType.Floor, 3, 2, 4, 0),
        })
        {
            Assert.Equal(1, BuildGrid.PartsOf(shape, parts, out bool slope));
            Assert.False(slope);
            Assert.Equal(BuildGrid.BoxOf(shape), parts[0]);
        }
        foreach (var shape in new[] { new BuildPieceShape(BuildPieceType.Ramp, 3, 2, 4, 2), new BuildPieceShape(BuildPieceType.Roof, 3, 2, 4, 0) })
        {
            Assert.Equal(0, BuildGrid.PartsOf(shape, parts, out bool slope));
            Assert.True(slope);
        }
        Assert.Equal(SlopeKind.Roof, BuildGrid.SlopeOf(new BuildPieceShape(BuildPieceType.Roof, 3, 2, 4, 0)).Kind);
    }

    [Theory]
    [InlineData(HalfWall, 1)]
    [InlineData(Door, 3)]
    [InlineData(Window, 4)]
    [InlineData(TallOpening, 2)]
    [InlineData(0b010_000_010, 5)]
    public void AnEditedWall_IsFewMergedBoxes(int edit, int expected)
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        var shape = new BuildPieceShape(BuildPieceType.Wall, 3, 2, 4, 0, edit);
        Assert.Equal(expected, BuildGrid.PartsOf(shape, parts, out _));
    }

    // Every valid wall and floor state: at most MaxPartsPerPiece boxes, inside the frame, not overlapping, and covering
    // exactly the solid tiles (volume and every tile centre).
    [Fact]
    public void EveryValidState_CoversExactlyItsSolidTiles()
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        foreach (BuildPieceType type in new[] { BuildPieceType.Wall, BuildPieceType.Floor })
        {
            for (int rotation = 0; rotation < (type == BuildPieceType.Wall ? 2 : 1); rotation++)
            {
                for (int edit = 0; edit <= BuildEdit.Mask; edit++)
                {
                    if (!BuildEdit.IsValid(type, edit)) continue;
                    var shape = new BuildPieceShape(type, 7, 1, 9, rotation, edit);
                    int count = BuildGrid.PartsOf(shape, parts, out bool slope);
                    Assert.False(slope);
                    Assert.InRange(count, 1, BuildGrid.MaxPartsPerPiece);
                    Box frame = BuildGrid.BoxOf(shape);
                    float volume = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        Assert.True(Inside(parts[i], frame));
                        volume += Volume(parts[i]);
                        for (int j = i + 1; j < count; j++) Assert.False(Overlap(parts[i], parts[j]));
                    }
                    int tiles = BuildEdit.TileCount(type);
                    int solid = tiles - BuildEdit.PopCount(edit);
                    Assert.Equal(Volume(frame) * solid / tiles, volume, 3);
                    for (int t = 0; t < tiles; t++)
                    {
                        Vector3 c = TileCentre(shape, t);
                        bool covered = false;
                        for (int i = 0; i < count; i++) covered |= Contains(parts[i], c);
                        Assert.Equal((edit & (1 << t)) == 0, covered);
                    }
                }
            }
        }
    }

    [Fact]
    public void FlatRoofs_AreSlabsAtTheEaves_AndThePassageHasItsHole()
    {
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        var flat = new BuildPieceShape(BuildPieceType.Roof, 3, 2, 4, 0, BuildEdit.RoofFlat);
        Assert.Equal(1, BuildGrid.PartsOf(flat, parts, out bool slope));
        Assert.False(slope);
        Assert.False(BuildGrid.HasSlope(flat));
        Assert.Equal(BuildGrid.LevelBase(3), parts[0].Max.Y);
        Assert.Equal(BuildGrid.LevelBase(3) - BuildGrid.FloorThickness, parts[0].Min.Y, 5);
        Assert.Equal(BuildGrid.BoundsOf(flat), parts[0]);

        var passage = new BuildPieceShape(BuildPieceType.Roof, 3, 2, 4, 0, BuildEdit.RoofPassage);
        Assert.Equal(4, BuildGrid.PartsOf(passage, parts, out _));
        float x0 = BuildGrid.CellMinX(3), z0 = BuildGrid.CellMinZ(4);
        var centre = new Vector3(x0 + 2.5f, BuildGrid.LevelBase(3) - 0.1f, z0 + 2.5f);
        float volume = 0f;
        for (int i = 0; i < 4; i++)
        {
            Assert.False(Contains(parts[i], centre));
            volume += Volume(parts[i]);
        }
        Assert.Equal((25f - BuildGrid.RoofHoleSize * BuildGrid.RoofHoleSize) * BuildGrid.FloorThickness, volume, 3);
        // The hole is wide enough for the character's footprint (0.7 m) with room to spare.
        Assert.True(BuildGrid.RoofHoleSize > 2f * MoveSettings.HalfWidth + 1f);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public void AOneWayRoof_RisesRoofRiseTowardItsDirection(int edit, int direction)
    {
        var shape = new BuildPieceShape(BuildPieceType.Roof, 16, 0, 16, 0, edit);
        Assert.True(BuildGrid.HasSlope(shape));
        Slope s = BuildGrid.SlopeOf(shape);
        Assert.Equal((SlopeKind.RoofSlope, (byte)direction), (s.Kind, s.Direction));
        float eaves = BuildGrid.LevelBase(1);
        Vector3 low, high;
        switch (direction)
        {
            case 0: low = new Vector3(2.5f, 0, 0.01f); high = new Vector3(2.5f, 0, 4.99f); break;
            case 1: low = new Vector3(0.01f, 0, 2.5f); high = new Vector3(4.99f, 0, 2.5f); break;
            case 2: low = new Vector3(2.5f, 0, 4.99f); high = new Vector3(2.5f, 0, 0.01f); break;
            default: low = new Vector3(4.99f, 0, 2.5f); high = new Vector3(0.01f, 0, 2.5f); break;
        }
        Assert.Equal(eaves, s.HeightAt(low.X, low.Z), 1);
        Assert.Equal(eaves + BuildGrid.RoofRise, s.HeightAt(high.X, high.Z), 1);
        Assert.True(s.Range(1f, 1f, 2f, 2f, out _, out _, out float bottom));
        Assert.Equal(eaves - BuildGrid.SlopeThickness, bottom, 5);
    }

    [Fact]
    public void TileAt_AndTileBox_Agree()
    {
        foreach (var shape in new[]
        {
            new BuildPieceShape(BuildPieceType.Wall, 7, 1, 9, 0), new BuildPieceShape(BuildPieceType.Wall, 7, 1, 9, 1),
            new BuildPieceShape(BuildPieceType.Floor, 7, 1, 9, 0), new BuildPieceShape(BuildPieceType.Roof, 7, 1, 9, 0),
            new BuildPieceShape(BuildPieceType.Ramp, 7, 1, 9, 2),
        })
        {
            for (int t = 0; t < BuildEdit.TileCount(shape.Type); t++)
                Assert.Equal(t, BuildEdit.TileAt(shape, BuildEdit.TileBox(shape, t).Center));
        }
    }

    // 기능: 조각의 지정 타일 상자 중심 좌표를 구한다.
    // 입력: shape - 대상 조각 모양, tile - 타일 번호.
    // 출력: 해당 타일 상자의 중심 좌표.
    private static Vector3 TileCentre(in BuildPieceShape shape, int tile) => BuildEdit.TileBox(shape, tile).Center;

    // 기능: 상자 a가 frame 안에 (1e-4 오차 허용) 완전히 들어가는지 검사한다.
    // 입력: a - 검사할 상자, frame - 바깥 틀 상자.
    // 출력: a의 모든 면이 frame 안이면 true, 하나라도 벗어나면 false.
    private static bool Inside(in Box a, in Box frame) =>
        a.Min.X >= frame.Min.X - 1e-4f && a.Min.Y >= frame.Min.Y - 1e-4f && a.Min.Z >= frame.Min.Z - 1e-4f &&
        a.Max.X <= frame.Max.X + 1e-4f && a.Max.Y <= frame.Max.Y + 1e-4f && a.Max.Z <= frame.Max.Z + 1e-4f;

    // 기능: 두 상자의 부피가 (1e-4 이상) 실제로 겹치는지 검사한다. 면만 맞닿은 경우는 겹침이 아니다.
    // 입력: a - 첫 상자, b - 둘째 상자.
    // 출력: 세 축 모두 겹치면 true, 아니면 false.
    private static bool Overlap(in Box a, in Box b) =>
        a.Min.X < b.Max.X - 1e-4f && b.Min.X < a.Max.X - 1e-4f && a.Min.Y < b.Max.Y - 1e-4f && b.Min.Y < a.Max.Y - 1e-4f &&
        a.Min.Z < b.Max.Z - 1e-4f && b.Min.Z < a.Max.Z - 1e-4f;

    // 기능: 점이 상자의 내부(경계 제외)에 있는지 검사한다.
    // 입력: b - 대상 상자, p - 검사할 점.
    // 출력: 점이 상자 내부에 있으면 true, 경계 위이거나 바깥이면 false.
    private static bool Contains(in Box b, Vector3 p) =>
        p.X > b.Min.X && p.X < b.Max.X && p.Y > b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z;

    // 기능: 상자의 부피를 계산한다.
    // 입력: b - 대상 상자.
    // 출력: 세 변 길이의 곱(부피).
    private static float Volume(in Box b) => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z);
}
