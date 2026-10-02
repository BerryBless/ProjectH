using System.Collections.Generic;
using System.Numerics;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 13 D1, D2: the building grid, slot keys and piece shapes.
public class BuildGridTests
{
    [Fact]
    public void TheGrid_Covers160By160MetresIn32Cells_And16Levels()
    {
        Assert.Equal(-GameMap.HalfSize, BuildGrid.OriginX);
        Assert.Equal(-GameMap.HalfSize, BuildGrid.OriginZ);
        Assert.Equal(2f * GameMap.HalfSize, BuildGrid.CellsX * BuildGrid.CellSize);
        Assert.Equal(2f * GameMap.HalfSize, BuildGrid.CellsZ * BuildGrid.CellSize);
        Assert.Equal(48f, BuildGrid.Levels * BuildGrid.LevelHeight);
        Assert.Equal(16, BuildGrid.CellX(0.01f));
        Assert.Equal(15, BuildGrid.CellX(-0.01f));
        Assert.Equal(0, BuildGrid.CellZ(-80f));
        Assert.Equal(1, BuildGrid.Level(3f));
    }

    // D1: the ramp's slope is MaxSlope, so climbing it needs no new movement rule.
    [Fact]
    public void ARamp_RisesAtTheMaximumWalkableSlope()
    {
        Assert.Equal(MoveSettings.MaxSlope, BuildGrid.RampRise / BuildGrid.CellSize, 6);
        Assert.Equal(MoveSettings.MaxSlope, BuildGrid.RoofRise / (BuildGrid.CellSize * 0.5f), 6);
    }

    [Theory]
    [InlineData(2, 4, 3, 7, 0)]    // north edge of (4, 7) = south edge of (4, 8)
    [InlineData(3, 4, 3, 7, 1)]    // east edge of (4, 7) = west edge of (5, 7)
    public void NorthAndEastWalls_AreTheNextCellsSouthAndWestWalls(int rotation, int x, int y, int z, int expectedRotation)
    {
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Wall, x, y, z, rotation, out BuildPieceShape shape));
        Assert.Equal(expectedRotation, shape.Rotation);
        Assert.Equal(rotation == 2 ? x : x + 1, shape.X);
        Assert.Equal(rotation == 2 ? z + 1 : z, shape.Z);
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Wall, shape.X, shape.Y, shape.Z, shape.Rotation, out BuildPieceShape again));
        Assert.Equal(shape, again);
        Assert.Equal(BuildGrid.BoxOf(shape).Center, BuildGrid.BoxOf(again).Center);
    }

    [Theory]
    [InlineData(4, 0, 0, 0, 0)]       // no such piece
    [InlineData(255, 0, 0, 0, 0)]
    [InlineData(0, -1, 0, 0, 0)]
    [InlineData(0, 32, 0, 0, 0)]
    [InlineData(1, 0, 16, 0, 0)]      // level 16
    [InlineData(1, 0, -1, 0, 0)]
    [InlineData(2, 0, 0, 0, 4)]       // rotation 4
    [InlineData(0, 0, 0, 31, 2)]      // the north edge of the last row: off the grid
    [InlineData(0, 31, 0, 0, 3)]      // the east edge of the last column
    [InlineData(1, 0, 0, int.MaxValue, 0)]
    [InlineData(1, int.MinValue, 0, 0, 0)]
    public void OffTheGrid_OrUnknown_IsRefused(int type, int x, int y, int z, int rotation)
    {
        Assert.False(BuildGrid.TryNormalize((BuildPieceType)type, x, y, z, rotation, out _));
    }

    [Fact]
    public void FloorsAndRoofs_DropTheirRotation_AndRampsKeepIt()
    {
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Floor, 3, 1, 3, 2, out BuildPieceShape floor));
        Assert.Equal(0, floor.Rotation);
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Roof, 3, 1, 3, 3, out BuildPieceShape roof));
        Assert.Equal(0, roof.Rotation);
        Assert.True(BuildGrid.TryNormalize(BuildPieceType.Ramp, 3, 1, 3, 3, out BuildPieceShape ramp));
        Assert.Equal(3, ramp.Rotation);
    }

    [Fact]
    public void EverySlot_HasItsOwnKey()
    {
        var keys = new HashSet<uint>();
        for (int y = 0; y < BuildGrid.Levels; y++)
            for (int z = 0; z < BuildGrid.CellsZ; z++)
                for (int x = 0; x < BuildGrid.CellsX; x++)
                    for (int slot = 0; slot < BuildGrid.SlotKinds; slot++)
                        Assert.True(keys.Add(BuildGrid.SlotKey(x, y, z, (BuildSlotKind)slot)));
        Assert.Equal(BuildGrid.Levels * BuildGrid.CellsX * BuildGrid.CellsZ * BuildGrid.SlotKinds, keys.Count);
    }

    [Fact]
    public void AllRampRotations_ShareTheRampSlot()
    {
        uint key = 0;
        for (int r = 0; r < 4; r++)
        {
            Assert.True(BuildGrid.TryNormalize(BuildPieceType.Ramp, 5, 2, 6, r, out BuildPieceShape ramp));
            if (r == 0) key = BuildGrid.SlotKey(ramp);
            Assert.Equal(key, BuildGrid.SlotKey(ramp));
        }
    }

    // D1: pieces meet exactly: neighbouring walls touch end to end, a floor's top is its level's height, a ramp's top edge
    // and a roof's eaves are exactly the next level's height (the same float a floor has), for every level.
    [Fact]
    public void PiecesMeetExactly_AtEveryLevel()
    {
        for (int y = 0; y < BuildGrid.Levels; y++)
        {
            Box a = BuildGrid.BoxOf(new BuildPieceShape(BuildPieceType.Wall, 3, y, 4, 0));
            Box b = BuildGrid.BoxOf(new BuildPieceShape(BuildPieceType.Wall, 4, y, 4, 0));
            Assert.Equal(a.Max.X, b.Min.X);
            Assert.Equal(BuildGrid.LevelBase(y), a.Min.Y);
            Assert.Equal(BuildGrid.LevelBase(y + 1), a.Max.Y);
            Assert.Equal(BuildGrid.WallThickness, a.Size.Z);

            Box floor = BuildGrid.BoxOf(new BuildPieceShape(BuildPieceType.Floor, 3, y, 4, 0));
            Assert.Equal(BuildGrid.LevelBase(y), floor.Max.Y);
            Assert.Equal(BuildGrid.FloorThickness, floor.Size.Y);

            for (byte r = 0; r < 4; r++)
            {
                Slope ramp = BuildGrid.SlopeOf(new BuildPieceShape(BuildPieceType.Ramp, 3, y, 4, r));
                Assert.Equal(BuildGrid.LevelBase(y + 1), ramp.Top);
                Assert.True(ramp.Range(ramp.MinX, ramp.MinZ, ramp.MaxX, ramp.MaxZ, out float low, out float high, out _));
                Assert.Equal(BuildGrid.LevelBase(y), low);
                Assert.Equal(BuildGrid.LevelBase(y + 1), high);
            }
            Slope roof = BuildGrid.SlopeOf(new BuildPieceShape(BuildPieceType.Roof, 3, y, 4, 0));
            Assert.Equal(BuildGrid.LevelBase(y + 1), roof.BaseY);
            Assert.Equal(BuildGrid.LevelBase(y + 1), roof.HeightAt(roof.MinX, roof.MinZ));
            Assert.Equal(BuildGrid.LevelBase(y + 1) + BuildGrid.RoofRise, roof.HeightAt((roof.MinX + roof.MaxX) * 0.5f, (roof.MinZ + roof.MaxZ) * 0.5f));
        }
    }

    [Theory]
    [InlineData(0, 0f, 2.5f, 1.5f)]   // rising +Z: z = 2.5 m in
    [InlineData(1, 2.5f, 0f, 1.5f)]   // rising +X
    [InlineData(2, 0f, 4f, 0.6f)]     // rising -Z: 1 m from the +Z edge
    [InlineData(3, 4f, 0f, 0.6f)]     // rising -X: 1 m from the +X edge
    public void ARamp_RisesInItsDirection(int rotation, float x, float z, float expected)
    {
        Slope ramp = BuildGrid.SlopeOf(new BuildPieceShape(BuildPieceType.Ramp, 16, 0, 16, rotation));
        Assert.Equal(expected, ramp.HeightAt(ramp.MinX + x, ramp.MinZ + z), 5);
    }

    [Fact]
    public void ASlopesRange_UnderAFootprint_IsItsHighestAndLowestPoint()
    {
        Slope ramp = BuildGrid.SlopeOf(new BuildPieceShape(BuildPieceType.Ramp, 16, 0, 16, 0));   // x 0..5, z 0..5, +Z
        Assert.True(ramp.Range(2f, 1f, 3f, 2f, out float low, out float high, out float bottom));
        Assert.Equal(0.6f, low, 5);
        Assert.Equal(1.2f, high, 5);
        Assert.Equal(0.6f - BuildGrid.SlopeThickness, bottom, 5);
        Assert.False(ramp.Range(5f, 1f, 6f, 2f, out _, out _, out _));   // touching the edge only
        Assert.True(ramp.Range(-1f, 4.5f, 1f, 6f, out _, out high, out _));
        Assert.Equal(3f, high);                                             // clipped to the cell

        Slope roof = BuildGrid.SlopeOf(new BuildPieceShape(BuildPieceType.Roof, 16, 0, 16, 0));
        Assert.True(roof.Range(2f, 2f, 3f, 3f, out low, out high, out bottom));
        Assert.Equal(4.5f, high);
        Assert.Equal(3f + (2.5f - 0.5f) * 0.6f, low, 5);
        Assert.Equal(3f - BuildGrid.SlopeThickness, bottom);
    }

    [Fact]
    public void Bounds_HoldEveryPiece()
    {
        var wall = new BuildPieceShape(BuildPieceType.Wall, 3, 2, 4, 1);
        Assert.Equal(BuildGrid.BoxOf(wall).Min, BuildGrid.BoundsOf(wall).Min);
        Box ramp = BuildGrid.BoundsOf(new BuildPieceShape(BuildPieceType.Ramp, 3, 2, 4, 0));
        Assert.Equal(new Vector3(BuildGrid.CellMinX(3), 6f - BuildGrid.SlopeThickness, BuildGrid.CellMinZ(4)), ramp.Min);
        Assert.Equal(9f, ramp.Max.Y);
        Box roof = BuildGrid.BoundsOf(new BuildPieceShape(BuildPieceType.Roof, 3, 2, 4, 0));
        Assert.Equal(9f - BuildGrid.SlopeThickness, roof.Min.Y);
        Assert.Equal(10.5f, roof.Max.Y);
    }
}
