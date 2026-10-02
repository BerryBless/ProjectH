using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 13 D1: the four pieces. Values are the wire format: never renumber, never add a piece in this phase.
    public enum BuildPieceType : byte
    {
        Wall = 0,
        Floor = 1,
        Ramp = 2,
        Roof = 3,
    }

    // Phase 13 request §5: the three building materials (and resources). Values are the wire format.
    public enum BuildMaterialType : byte
    {
        Wood = 0,
        Stone = 1,
        Metal = 2,
    }

    // Final review C: how many materials there are (the wire's per-material arrays, the resources).
    public static class BuildMaterials
    {
        public const int Count = 3;
    }

    // D1: the slot a piece takes. A cell owns a floor, a ramp and a roof slot and its south and west edges; its north and
    // east walls are the next cells' south and west walls, so one edge has one key.
    public enum BuildSlotKind : byte
    {
        WallSouth = 0,
        WallWest = 1,
        Floor = 2,
        Ramp = 3,
        Roof = 4,
    }

    // The geometry of one piece in canonical grid coordinates (BuildGrid.TryNormalize). Rotation: a wall's edge (0 south,
    // 1 west), a ramp's rising direction (0 +Z, 1 +X, 2 -Z, 3 -X, the yaw convention); 0 for a floor and a roof.
    public readonly struct BuildPieceShape : IEquatable<BuildPieceShape>
    {
        public readonly BuildPieceType Type;
        public readonly byte X;
        public readonly byte Y;
        public readonly byte Z;
        public readonly byte Rotation;

        public BuildPieceShape(BuildPieceType type, int x, int y, int z, int rotation)
        {
            Type = type;
            X = (byte)x;
            Y = (byte)y;
            Z = (byte)z;
            Rotation = (byte)rotation;
        }

        public bool Equals(BuildPieceShape other) =>
            Type == other.Type && X == other.X && Y == other.Y && Z == other.Z && Rotation == other.Rotation;

        public override bool Equals(object obj) => obj is BuildPieceShape other && Equals(other);

        public override int GetHashCode() => (int)BuildGrid.SlotKey(this) ^ (Rotation << 24);

        public override string ToString() => $"{Type}({X},{Y},{Z} r{Rotation})";
    }

    // Phase 13 D1, D2: the building grid and the shape of every piece, computed from integers so pieces always meet
    // exactly (no accumulated float error). Shared because movement collides with pieces (game-core-rules §4, Phase 13
    // exception): client prediction and the server must see the same boxes and slopes. Which piece may go where
    // (validation, support) is server-only. Pure, no allocation.
    public static class BuildGrid
    {
        public const float CellSize = 5f;
        public const float LevelHeight = 3f;
        public const float OriginX = -GameMap.HalfSize;
        public const float OriginZ = -GameMap.HalfSize;
        public const int CellsX = 32;
        public const int CellsZ = 32;
        public const int Levels = 16;
        public const int SlotKinds = 5;

        public const float WallThickness = 0.25f;
        public const float FloorThickness = 0.25f;
        // D2: a ramp rises one level over one cell (3 / 5 = 0.6 = MoveSettings.MaxSlope); a roof is a pyramid whose top is
        // RoofRise above its eaves, the same slope.
        public const float RampRise = LevelHeight;
        public const float RoofRise = 1.5f;
        // The slab under a ramp's or a roof's surface: walking from below is blocked by it.
        public const float SlopeThickness = 0.25f;

        // D1: validates a piece the client asked for and returns its canonical shape: a north or east wall becomes the
        // next cell's south or west wall. False for an unknown type, a rotation above 3, or a cell or level off the grid.
        public static bool TryNormalize(BuildPieceType type, int x, int y, int z, int rotation, out BuildPieceShape shape)
        {
            shape = default;
            if ((uint)type > (uint)BuildPieceType.Roof || rotation < 0 || rotation > 3) return false;
            if (y < 0 || y >= Levels) return false;
            if (type == BuildPieceType.Wall)
            {
                if (rotation == 2)
                {
                    z += 1;
                    rotation = 0;
                }
                else if (rotation == 3)
                {
                    x += 1;
                    rotation = 1;
                }
            }
            else if (type != BuildPieceType.Ramp)
            {
                rotation = 0;
            }
            if (x < 0 || x >= CellsX || z < 0 || z >= CellsZ) return false;
            shape = new BuildPieceShape(type, x, y, z, rotation);
            return true;
        }

        public static BuildSlotKind SlotOf(in BuildPieceShape shape)
        {
            switch (shape.Type)
            {
                case BuildPieceType.Wall: return shape.Rotation == 0 ? BuildSlotKind.WallSouth : BuildSlotKind.WallWest;
                case BuildPieceType.Floor: return BuildSlotKind.Floor;
                case BuildPieceType.Ramp: return BuildSlotKind.Ramp;
                default: return BuildSlotKind.Roof;
            }
        }

        // D1: one key per slot: x 6 bits, z 6 bits, level 4 bits, slot kind 3 bits.
        public static uint SlotKey(in BuildPieceShape shape) => SlotKey(shape.X, shape.Y, shape.Z, SlotOf(shape));

        public static uint SlotKey(int x, int y, int z, BuildSlotKind slot) =>
            (uint)(x & 63) | ((uint)(z & 63) << 6) | ((uint)(y & 15) << 12) | ((uint)slot << 16);

        public static float CellMinX(int x) => OriginX + x * CellSize;
        public static float CellMinZ(int z) => OriginZ + z * CellSize;
        public static float LevelBase(int y) => y * LevelHeight;

        // The cell or level a world coordinate lies in (may be off the grid: callers check the range).
        public static int CellX(float x) => (int)MathF.Floor((x - OriginX) / CellSize);
        public static int CellZ(float z) => (int)MathF.Floor((z - OriginZ) / CellSize);
        public static int Level(float y) => (int)MathF.Floor(y / LevelHeight);

        public static bool IsSlope(BuildPieceType type) => type == BuildPieceType.Ramp || type == BuildPieceType.Roof;

        // D2: a wall stands on its edge (WallThickness, centred on it, one level high); a floor is a thin box whose top
        // is the level's height. Only for walls and floors.
        public static Box BoxOf(in BuildPieceShape shape)
        {
            float x0 = CellMinX(shape.X);
            float z0 = CellMinZ(shape.Z);
            float y0 = LevelBase(shape.Y);
            const float half = WallThickness * 0.5f;
            if (shape.Type == BuildPieceType.Wall)
            {
                return shape.Rotation == 0
                    ? new Box(new Vector3(x0, y0, z0 - half), new Vector3(x0 + CellSize, y0 + LevelHeight, z0 + half))
                    : new Box(new Vector3(x0 - half, y0, z0), new Vector3(x0 + half, y0 + LevelHeight, z0 + CellSize));
            }
            return new Box(new Vector3(x0, y0 - FloorThickness, z0), new Vector3(x0 + CellSize, y0, z0 + CellSize));
        }

        // D2: a ramp rises from its level's height across the cell in its direction; a roof sits on the level's walls
        // (eaves at the next level's height). Only for ramps and roofs.
        public static Slope SlopeOf(in BuildPieceShape shape)
        {
            float x0 = CellMinX(shape.X);
            float z0 = CellMinZ(shape.Z);
            return shape.Type == BuildPieceType.Ramp
                ? new Slope(x0, z0, x0 + CellSize, z0 + CellSize, LevelBase(shape.Y), SlopeKind.Ramp, shape.Rotation)
                : new Slope(x0, z0, x0 + CellSize, z0 + CellSize, LevelBase(shape.Y + 1), SlopeKind.Roof, 0);
        }

        // The box that holds the whole piece (a slope: its cell from the slab's lowest point to its top).
        public static Box BoundsOf(in BuildPieceShape shape)
        {
            if (!IsSlope(shape.Type)) return BoxOf(shape);
            // The slab's lowest point: under a ramp's low edge, or a roof's flat ceiling.
            Slope slope = SlopeOf(shape);
            return new Box(new Vector3(slope.MinX, slope.BaseY - SlopeThickness, slope.MinZ), new Vector3(slope.MaxX, slope.Top, slope.MaxZ));
        }

        public static Vector3 CenterOf(in BuildPieceShape shape) => BoundsOf(shape).Center;
    }

    public enum SlopeKind : byte
    {
        Ramp = 0,
        Roof = 1,
    }

    // Phase 13 D2: a walkable sloped surface over one cell: a ramp (a plane) or a roof (a four-sided pyramid). Movement
    // treats it like the terrain (the floor height under the feet) plus a thin slab under the surface that blocks from
    // below. Immutable; all queries are pure and allocate nothing.
    public readonly struct Slope
    {
        public readonly float MinX;
        public readonly float MinZ;
        public readonly float MaxX;
        public readonly float MaxZ;
        // Ramp: the height of its low edge. Roof: the height of its eaves (the pyramid's base).
        public readonly float BaseY;
        public readonly SlopeKind Kind;
        // Ramp: the rising direction, 0 +Z, 1 +X, 2 -Z, 3 -X.
        public readonly byte Direction;

        public Slope(float minX, float minZ, float maxX, float maxZ, float baseY, SlopeKind kind, byte direction)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
            BaseY = baseY;
            Kind = kind;
            Direction = direction;
        }

        public float Top => Kind == SlopeKind.Ramp ? BaseY + BuildGrid.RampRise : BaseY + BuildGrid.RoofRise;

        // The surface height at a point (clamped into the cell).
        public float HeightAt(float x, float z)
        {
            x = Clamp(x, MinX, MaxX);
            z = Clamp(z, MinZ, MaxZ);
            if (Kind == SlopeKind.Ramp) return RampHeight(Along(x, z));
            float cx = (MinX + MaxX) * 0.5f;
            float cz = (MinZ + MaxZ) * 0.5f;
            return RoofHeight(MathF.Max(MathF.Abs(x - cx), MathF.Abs(z - cz)));
        }

        // The lowest and highest surface over the rectangle (x0..x1, z0..z1) where it overlaps the cell, and the bottom of
        // the slab there. False when the rectangle does not overlap the cell (touching edges do not count).
        public bool Range(float x0, float z0, float x1, float z1, out float low, out float high, out float bottom)
        {
            low = high = bottom = 0f;
            if (!(x0 < MaxX && x1 > MinX && z0 < MaxZ && z1 > MinZ)) return false;
            float ax = MathF.Max(x0, MinX);
            float bx = MathF.Min(x1, MaxX);
            float az = MathF.Max(z0, MinZ);
            float bz = MathF.Min(z1, MaxZ);
            if (Kind == SlopeKind.Ramp)
            {
                float a = Along(ax, az);
                float b = Along(bx, bz);
                float alongLow = MathF.Min(a, b);
                float alongHigh = MathF.Max(a, b);
                low = RampHeight(alongLow);
                high = RampHeight(alongHigh);
                bottom = low - BuildGrid.SlopeThickness;
                return true;
            }
            float cx = (MinX + MaxX) * 0.5f;
            float cz = (MinZ + MaxZ) * 0.5f;
            // Chebyshev distance from the centre: the nearest point of the rectangle is the highest, its farthest corner
            // the lowest.
            float nearX = cx < ax ? ax - cx : cx > bx ? cx - bx : 0f;
            float nearZ = cz < az ? az - cz : cz > bz ? cz - bz : 0f;
            float farX = MathF.Max(MathF.Abs(ax - cx), MathF.Abs(bx - cx));
            float farZ = MathF.Max(MathF.Abs(az - cz), MathF.Abs(bz - cz));
            high = RoofHeight(MathF.Max(nearX, nearZ));
            low = RoofHeight(MathF.Max(farX, farZ));
            // The roof is a solid pyramid down to its flat ceiling: shots and heads stop at the ceiling.
            bottom = BaseY - BuildGrid.SlopeThickness;
            return true;
        }

        // Distance from the low edge in the rising direction, 0..CellSize.
        private float Along(float x, float z)
        {
            switch (Direction)
            {
                case 0: return z - MinZ;
                case 1: return x - MinX;
                case 2: return MaxZ - z;
                default: return MaxX - x;
            }
        }

        // rise = along x 3 / 5: exact at both ends (0 and 3), so a ramp's top edge is exactly the next level's height,
        // the same float a floor's top has (BuildGrid.LevelBase).
        private float RampHeight(float along)
        {
            if (!(along > 0f)) return BaseY;
            if (along >= BuildGrid.CellSize) return BaseY + BuildGrid.RampRise;
            return BaseY + along * BuildGrid.RampRise / BuildGrid.CellSize;
        }

        // The pyramid: RoofRise at the centre, the eaves (BaseY) at Chebyshev distance CellSize / 2.
        private float RoofHeight(float distance)
        {
            const float half = BuildGrid.CellSize * 0.5f;
            if (!(distance < half)) return BaseY;
            if (!(distance > 0f)) return BaseY + BuildGrid.RoofRise;
            return BaseY + (half - distance) * BuildGrid.RoofRise / half;
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
