using System;
using System.Collections.Generic;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 13 D6: what a harvestable map object is. Values are stable (the map data and tests name them).
    public enum HarvestKind : byte
    {
        Tree = 0,    // Wood
        Rock = 1,    // Stone
        Wreck = 2,   // Metal
        Crate = 3,   // Wood (a wooden crate of the earlier map)
    }

    // Phase 13 D6: one harvestable object: a solid box like the map's boxes while it stands, gone (from movement, shots
    // and the client's view) once the server destroys it. Its id is its index in GameMap.Harvestables.
    public readonly struct Harvestable
    {
        public readonly Box Bounds;
        public readonly HarvestKind Kind;

        // 기능: 채집 대상 하나를 만든다.
        // 입력: bounds - 서 있는 동안의 충돌 상자, kind - 종류(주는 자원을 정한다).
        // 출력: 두 값을 담은 채집 대상.
        public Harvestable(Box bounds, HarvestKind kind)
        {
            Bounds = bounds;
            Kind = kind;
        }

        // The resource it gives (request §14).
        public BuildMaterialType Material =>
            Kind == HarvestKind.Rock ? BuildMaterialType.Stone : Kind == HarvestKind.Wreck ? BuildMaterialType.Metal : BuildMaterialType.Wood;
    }

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
        // Phase 13 D6: at most 64 harvestables, so one ulong holds which are destroyed (HarvestStates).
        public const int MaxHarvestables = 64;

        private static readonly Box[] s_boxes;
        private static readonly Box[] s_doors;
        private static readonly Harvestable[] s_harvestables;

        // 기능: 맵을 만든다. 언덕으로 지형을 먼저 짓고, 외벽·POI 건물·엄폐물 상자와 문, 채집 대상을 고정 순서로 채운다.
        // 입력: 없음.
        // 출력: 반환값 없음. Terrain·Boxes·Doors·Harvestables가 채워진다(이후 불변).
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
            boxes.Add(Box.FromCenterSize(new Vector3(34f, 0.75f, 60f), new Vector3(2f, 1.5f, 2f)));  // high crate
            // Phase 13 D6: the other high crate (58, 60) is a harvestable now (below).

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
            boxes.Add(Box.FromCenterSize(new Vector3(0f, 1.5f, 70f), new Vector3(6f, WallHeight, WallThickness)));
            boxes.Add(Box.FromCenterSize(new Vector3(20f, 1.5f, -66f), new Vector3(WallThickness, WallHeight, 6f)));

            s_boxes = boxes.ToArray();
            s_doors = doors.ToArray();
            s_harvestables = BuildHarvestables();
        }

        // Phase 13 D6: 41 harvestables on flat ground, clear of the boxes, the doors, the loot and drop points and the plaza
        // (HarvestableMapTests). Trees and rocks are new; four crates without a loot point on top (the high crate at
        // Gearworks and three open-ground crates) moved here from the boxes. Never reorder: the index is the id.
        // 기능: 채집 대상 목록(나무, 바위, 잔해, 상자 순)을 고정 순서로 만든다. 순서가 곧 id라 바꾸지 않는다.
        // 입력: 없음(Terrain이 먼저 만들어져 있어야 한다).
        // 출력: 채집 대상 배열(MaxHarvestables 이하).
        private static Harvestable[] BuildHarvestables()
        {
            var list = new List<Harvestable>(MaxHarvestables);
            // Trees, a 1 x 4 x 1 m trunk: north-west woods, east woods, south-east, west, and a few strays.
            float[] trees =
            {
                -36f, 64f, -30f, 59f, -24f, 66f, -44f, 70f, -56f, 74f, -20f, 74f,
                40f, 30f, 46f, 25f, 52f, 32f, 60f, 28f, 66f, 34f, 72f, 26f,
                48f, -68f, 56f, -73f, 62f, -66f,
                -70f, -26f, -62f, -22f, -54f, -30f, -44f, -24f,
                -20f, -72f, 12f, -74f, 30f, 66f, 64f, 74f,
            };
            for (int i = 0; i < trees.Length; i += 2) list.Add(OnGround(HarvestKind.Tree, trees[i], trees[i + 1], new Vector3(1f, 4f, 1f)));
            // Rocks, 2 x 1.2 x 2 m: by the ruins, the Lookout and the hills.
            float[] rocks = { -60f, -36f, -72f, -52f, -32f, -58f, 24f, -58f, 70f, -52f, 64f, -60f, -30f, -16f, 30f, 24f };
            for (int i = 0; i < rocks.Length; i += 2) list.Add(OnGround(HarvestKind.Rock, rocks[i], rocks[i + 1], new Vector3(2f, 1.2f, 2f)));
            // Wrecks, 3 x 1.5 x 2 m.
            float[] wrecks = { 28f, 44f, 24f, 10f, -40f, 28f, -66f, 28f, -8f, -26f, 18f, 62f };
            for (int i = 0; i < wrecks.Length; i += 2) list.Add(OnGround(HarvestKind.Wreck, wrecks[i], wrecks[i + 1], new Vector3(3f, 1.5f, 2f)));
            // The crates (same boxes as before Phase 13).
            list.Add(new Harvestable(Box.FromCenterSize(new Vector3(58f, 0.75f, 60f), new Vector3(2f, 1.5f, 2f)), HarvestKind.Crate));
            list.Add(new Harvestable(Box.FromCenterSize(new Vector3(8f, 0.5f, -24f), new Vector3(2f, 1f, 2f)), HarvestKind.Crate));
            list.Add(new Harvestable(Box.FromCenterSize(new Vector3(70f, 0.75f, -10f), new Vector3(2f, 1.5f, 2f)), HarvestKind.Crate));
            list.Add(new Harvestable(Box.FromCenterSize(new Vector3(-70f, 0.75f, 20f), new Vector3(2f, 1.5f, 2f)), HarvestKind.Crate));
            return list.ToArray();
        }

        // 기능: 지형 위에 서 있는 채집 대상을 만든다(그 자리는 평평해야 한다: HarvestableMapTests).
        // 입력: kind - 종류, x·z - 바닥 중심의 평면 위치, size - 상자 크기.
        // 출력: 바닥이 지형 높이에 놓인 채집 대상.
        // A harvestable standing on the terrain at (x, z) (flat there: HarvestableMapTests).
        private static Harvestable OnGround(HarvestKind kind, float x, float z, Vector3 size)
        {
            float ground = Terrain.Height(x, z);
            return new Harvestable(Box.FromCenterSize(new Vector3(x, ground + size.Y * 0.5f, z), size), kind);
        }

        public static ReadOnlySpan<Box> Boxes => s_boxes;

        // Phase 12 D9: the doors, in the order above (the three Rustvale houses: south, south, north; then Gearworks: north, south). Not part of
        // Boxes: a door is open or closed, and only the closed ones join the collision world (DoorStates bit i = Doors[i]).
        public static ReadOnlySpan<Box> Doors => s_doors;

        // Phase 13 D6: the harvestable objects in a fixed order (id = index). Not part of Boxes: a destroyed one leaves the
        // collision world (CollisionWorld.Gather skips it).
        public static ReadOnlySpan<Harvestable> Harvestables => s_harvestables;

        public static HeightField Terrain { get; }

        // D6: a one-storey building of width (X) x depth (Z), walls 3 m high and 0.5 m thick, a 1.5 m door in the middle
        // of the south and/or north wall, and a roof slab stacked on the walls. The east and west walls stop CornerSlit
        // short of the north and south walls, so no two walls touch side by side.
        // 기능: 단층 건물 하나(남·북 긴 벽, 동·서 짧은 벽, 지붕 판)를 상자 목록에 넣고 문이 있는 벽은 문 상자도 넣는다(D6).
        // 입력: boxes - 정적 상자 목록, doors - 문 상자 목록, cx·cz - 건물 중심, width·depth - X·Z 크기,
        //   doorSouth·doorNorth - 남·북 벽 가운데에 문을 낼지.
        // 출력: 반환값 없음. boxes에 벽·지붕이, doors에 문이 추가된다(북쪽 문이 남쪽 문보다 먼저).
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

        // 기능: X 방향으로 긴 벽 하나를 넣는다. 문이 있으면 벽을 둘로 나누고 그 틈에 문 상자를 넣는다.
        // 입력: boxes - 정적 상자 목록, doors - 문 상자 목록, cx - 벽 중심 X, z - 벽 중심 Z, halfW - 벽 반폭, door - 문을 낼지.
        // 출력: 반환값 없음. boxes에 벽 1개(문 없음) 또는 2개, door면 doors에 문 1개가 추가된다.
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
