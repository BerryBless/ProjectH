using System;
using System.Collections.Generic;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 6 map (D1-D7): 160 x 160 m inside the outer walls, terrain with hills and a plateau, four POIs and open
    // ground with cover. Client prediction and the authoritative server move against exactly these boxes and this
    // terrain (game-core-rules §4). Layout rules are checked by GameMapTests: boxes stand on flat terrain, no two
    // boxes touch side by side, gaps are 0, a corner slit (<= CornerSlit) or wider than the character, and the
    // plaza around the origin stays clear.
    public static class GameMap
    {
        public const float HalfSize = 80f;          // inner faces of the outer walls
        public const float PlazaRadius = 12f;       // Crossroads: the lobby, nothing built here
        public const float CornerSlit = 0.25f;      // D6: the gap left at a wall corner, narrower than the character
        public const float CellSize = 2f;
        public const int Verts = 81;                // 80 cells of 2 m

        private const float WallHeight = 3f;
        private const float WallThickness = 0.5f;
        private const float RoofThickness = 0.25f;
        private const float DoorWidth = 1.5f;
        // Phase 12 D9: a door fills its gap: DoorWidth wide, the wall's height, DoorThickness thick, centred in the wall.
        public const float DoorThickness = 0.2f;
        public const int DoorCount = 5;

        private static readonly Box[] s_boxes;
        private static readonly Box[] s_doors;

        // Static constructor: the hills must exist before the terrain is built from them (field initializers would run
        // in textual order).
        static GameMap()
        {
            Terrain = Hill.Build(-HalfSize, -HalfSize, CellSize, Verts, Verts, new[]
            {
                // Grid index = (metres + 80) / 2. Heights in 1/32 m.
                new Hill(62, 18, 4, 11, 6 * Hill.UnitsPerMeter),     // Lookout plateau (44, -44): 6 m, flat 8 m around
                new Hill(40, 63, 0, 9, 4 * Hill.UnitsPerMeter),      // north hill (0, 46)
                new Hill(65, 42, 0, 8, 7 * Hill.UnitsPerMeter / 2),  // east hill (50, 4)
                new Hill(15, 40, 0, 9, 9 * Hill.UnitsPerMeter / 2),  // west hill (-50, 0)
                new Hill(38, 16, 0, 10, 5 * Hill.UnitsPerMeter),     // south hill (-4, -48)
            });

            var boxes = new List<Box>(128);
            var doors = new List<Box>(DoorCount);

            // Outer walls, 4 m high, 1 m thick. North and south run the full width; east and west stop CornerSlit short
            // of them, so each corner is a slit the character cannot enter (CharacterCannotLeaveThroughASlit).
            boxes.Add(Box.FromCenterSize(new Vector3(0f, 2f, HalfSize + 0.5f), new Vector3(2f * HalfSize + 2f, 4f, 1f)));
            boxes.Add(Box.FromCenterSize(new Vector3(0f, 2f, -HalfSize - 0.5f), new Vector3(2f * HalfSize + 2f, 4f, 1f)));
            boxes.Add(Box.FromCenterSize(new Vector3(HalfSize + 0.5f, 2f, 0f), new Vector3(1f, 4f, 2f * (HalfSize - CornerSlit))));
            boxes.Add(Box.FromCenterSize(new Vector3(-HalfSize - 0.5f, 2f, 0f), new Vector3(1f, 4f, 2f * (HalfSize - CornerSlit))));

            // Rustvale (village, north-west): three 8 x 8 m houses.
            AddHouse(boxes, doors, -54f, 54f, 8f, 8f, doorSouth: true);
            AddHouse(boxes, doors, -38f, 52f, 8f, 8f, doorSouth: true);
            AddHouse(boxes, doors, -50f, 38f, 8f, 8f, doorSouth: false, doorNorth: true);

            // Gearworks (depot, north-east): one 16 x 12 m warehouse with a door on each long side, crates around it.
            AddHouse(boxes, doors, 46f, 50f, 16f, 12f, doorSouth: true, doorNorth: true);
            boxes.Add(Box.FromCenterSize(new Vector3(36f, 0.5f, 40f), new Vector3(2f, 1f, 2f)));     // low crate (climbable)
            boxes.Add(Box.FromCenterSize(new Vector3(57f, 0.5f, 40f), new Vector3(2f, 1f, 2f)));     // low crate (climbable)
            boxes.Add(Box.FromCenterSize(new Vector3(58f, 0.75f, 60f), new Vector3(2f, 1.5f, 2f)));  // high crate
            boxes.Add(Box.FromCenterSize(new Vector3(34f, 0.75f, 60f), new Vector3(2f, 1.5f, 2f)));  // high crate

            // Stonefield (ruins, south-west): roofless broken walls, pillars and a two-step platform.
            boxes.Add(Box.FromCenterSize(new Vector3(-56f, 1.5f, -42f), new Vector3(6f, WallHeight, WallThickness)));
            boxes.Add(Box.FromCenterSize(new Vector3(-40f, 1.5f, -52f), new Vector3(WallThickness, WallHeight, 6f)));
            boxes.Add(Box.FromCenterSize(new Vector3(-50f, 1.5f, -58f), new Vector3(6f, WallHeight, WallThickness)));
            boxes.Add(Box.FromCenterSize(new Vector3(-58f, 1.5f, -52f), new Vector3(WallThickness, WallHeight, 4f)));
            boxes.Add(Box.FromCenterSize(new Vector3(-46f, 1.5f, -46f), new Vector3(1f, WallHeight, 1f)));   // pillar
            boxes.Add(Box.FromCenterSize(new Vector3(-52f, 1.5f, -50f), new Vector3(1f, WallHeight, 1f)));   // pillar
            boxes.Add(Box.FromCenterSize(new Vector3(-44f, 0.5f, -40f), new Vector3(2f, 1f, 2f)));           // low box
            boxes.Add(Box.FromCenterSize(new Vector3(-60f, 0.5f, -46f), new Vector3(2f, 1f, 2f)));           // low box
            boxes.Add(Box.FromCenterSize(new Vector3(-48f, 0.5f, -66f), new Vector3(4f, 1f, 4f)));           // platform base
            boxes.Add(Box.FromCenterSize(new Vector3(-48.5f, 1.5f, -66.5f), new Vector3(2f, 1f, 2f)));       // its step (stacked)

            // Lookout (plateau, south-east): 1.5 m cover walls on the flat top at 6 m.
            boxes.Add(Box.FromCenterSize(new Vector3(41f, 6.75f, -44f), new Vector3(WallThickness, 1.5f, 3f)));
            boxes.Add(Box.FromCenterSize(new Vector3(47f, 6.75f, -44f), new Vector3(WallThickness, 1.5f, 3f)));
            boxes.Add(Box.FromCenterSize(new Vector3(44f, 6.75f, -48f), new Vector3(3f, 1.5f, WallThickness)));

            // Open ground between the POIs: scattered cover.
            boxes.Add(Box.FromCenterSize(new Vector3(18f, 0.75f, 18f), new Vector3(4f, 1.5f, WallThickness)));
            boxes.Add(Box.FromCenterSize(new Vector3(-18f, 0.5f, 18f), new Vector3(2f, 1f, 2f)));
            boxes.Add(Box.FromCenterSize(new Vector3(18f, 0.75f, -18f), new Vector3(2f, 1.5f, 2f)));
            boxes.Add(Box.FromCenterSize(new Vector3(-18f, 0.75f, -18f), new Vector3(WallThickness, 1.5f, 4f)));
            boxes.Add(Box.FromCenterSize(new Vector3(26f, 1.5f, 0f), new Vector3(WallThickness, WallHeight, 5f)));
            boxes.Add(Box.FromCenterSize(new Vector3(-26f, 1.5f, 0f), new Vector3(WallThickness, WallHeight, 5f)));
            boxes.Add(Box.FromCenterSize(new Vector3(0f, 0.5f, 22f), new Vector3(2f, 1f, 2f)));
            boxes.Add(Box.FromCenterSize(new Vector3(8f, 0.5f, -24f), new Vector3(2f, 1f, 2f)));
            boxes.Add(Box.FromCenterSize(new Vector3(0f, 1.5f, 70f), new Vector3(6f, WallHeight, WallThickness)));
            boxes.Add(Box.FromCenterSize(new Vector3(70f, 0.75f, -10f), new Vector3(2f, 1.5f, 2f)));
            boxes.Add(Box.FromCenterSize(new Vector3(-70f, 0.75f, 20f), new Vector3(2f, 1.5f, 2f)));
            boxes.Add(Box.FromCenterSize(new Vector3(20f, 1.5f, -66f), new Vector3(WallThickness, WallHeight, 6f)));

            s_boxes = boxes.ToArray();
            s_doors = doors.ToArray();
        }

        public static ReadOnlySpan<Box> Boxes => s_boxes;

        // Phase 12 D9: the doors, in the order above (the three Rustvale houses: south, south, north; then Gearworks: north, south). Not part of
        // Boxes: a door is open or closed, and only the closed ones join the collision world (DoorStates bit i = Doors[i]).
        public static ReadOnlySpan<Box> Doors => s_doors;

        public static HeightField Terrain { get; }

        // D6: a one-storey building of width (X) x depth (Z), walls 3 m high and 0.5 m thick, a 1.5 m door in the middle
        // of the south and/or north wall, and a roof slab stacked on the walls. The east and west walls stop CornerSlit
        // short of the north and south walls, so no two walls touch side by side.
        private static void AddHouse(List<Box> boxes, List<Box> doors, float cx, float cz, float width, float depth, bool doorSouth, bool doorNorth = false)
        {
            float halfW = width * 0.5f;
            float halfD = depth * 0.5f;
            AddLongWall(boxes, doors, cx, cz + halfD - WallThickness * 0.5f, halfW, doorNorth);
            AddLongWall(boxes, doors, cx, cz - halfD + WallThickness * 0.5f, halfW, doorSouth);
            float sideLength = depth - 2f * WallThickness - 2f * CornerSlit;
            boxes.Add(Box.FromCenterSize(new Vector3(cx + halfW - WallThickness * 0.5f, WallHeight * 0.5f, cz), new Vector3(WallThickness, WallHeight, sideLength)));
            boxes.Add(Box.FromCenterSize(new Vector3(cx - halfW + WallThickness * 0.5f, WallHeight * 0.5f, cz), new Vector3(WallThickness, WallHeight, sideLength)));
            boxes.Add(new Box(new Vector3(cx - halfW, WallHeight, cz - halfD), new Vector3(cx + halfW, WallHeight + RoofThickness, cz + halfD)));
        }

        private static void AddLongWall(List<Box> boxes, List<Box> doors, float cx, float z, float halfW, bool door)
        {
            float zMin = z - WallThickness * 0.5f;
            float zMax = z + WallThickness * 0.5f;
            if (!door)
            {
                boxes.Add(new Box(new Vector3(cx - halfW, 0f, zMin), new Vector3(cx + halfW, WallHeight, zMax)));
                return;
            }
            float halfDoor = DoorWidth * 0.5f;
            boxes.Add(new Box(new Vector3(cx - halfW, 0f, zMin), new Vector3(cx - halfDoor, WallHeight, zMax)));
            boxes.Add(new Box(new Vector3(cx + halfDoor, 0f, zMin), new Vector3(cx + halfW, WallHeight, zMax)));
            doors.Add(new Box(new Vector3(cx - halfDoor, 0f, z - DoorThickness * 0.5f), new Vector3(cx + halfDoor, WallHeight, z + DoorThickness * 0.5f)));
        }
    }
}
