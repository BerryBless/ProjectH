using System;
using System.Numerics;

namespace ProjectH.Shared.Simulation
{
    // Phase 16 D1: what a loot container is. Values are stable (the map data, the tests and the client name them).
    public enum LootContainerKind : byte
    {
        Chest = 0,
        AmmoBox = 1,
    }

    // Phase 16 D1: one loot container: its kind, where its bottom centre stands (on the terrain or a box top) and which way
    // it faces (Yaw 0, 90, 180 or 270 degrees, 0 = +Z like a character's yaw). Its id is its index in LootContainers.All.
    public readonly struct LootContainer
    {
        public readonly LootContainerKind Kind;
        public readonly Vector3 Position;
        public readonly float Yaw;

        // 기능: Container 배치 하나를 만든다.
        // 입력: kind - 종류, position - 바닥 중심, yaw - 방향(0·90·180·270).
        // 출력: LootContainer 값.
        public LootContainer(LootContainerKind kind, Vector3 position, float yaw)
        {
            Kind = kind;
            Position = position;
            Yaw = yaw;
        }

        // 기능: 컨테이너가 차지하는 상자를 돌려준다(Yaw 90·270이면 가로·세로가 바뀐다). 충돌체가 아니라 그리기·배치 검사·시선 목표용이다.
        // 입력: 없음.
        // 출력: 바닥 중심이 Position인 축 정렬 상자.
        public Box Bounds
        {
            get
            {
                Vector3 size = LootContainers.SizeOf(Kind);
                bool turned = Yaw == 90f || Yaw == 270f;
                float halfX = (turned ? size.Z : size.X) * 0.5f;
                float halfZ = (turned ? size.X : size.Z) * 0.5f;
                return new Box(new Vector3(Position.X - halfX, Position.Y, Position.Z - halfZ),
                    new Vector3(Position.X + halfX, Position.Y + size.Y, Position.Z + halfZ));
            }
        }

        // 기능: 컨테이너 상자의 가운데(서버 열기 시선 검사의 목표점)를 돌려준다.
        // 입력: 없음.
        // 출력: Position에서 높이의 절반만큼 위.
        public Vector3 Center => Position + new Vector3(0f, LootContainers.SizeOf(Kind).Y * 0.5f, 0f);
    }

    // Phase 16 D1: the loot containers of the map: 20 chests and 14 ammo boxes. Placement constants only (game-core-rules §4
    // exception 2): whether one spawns, what it holds and the open rule are the server's (loot.json, ContainerRules). A
    // container is NOT a collider (like a reboot station): moves, shots and the shared collision gather never see it; the
    // client draws a box there. Never reorder: the index is the id (ContainerStates bit i). At most MaxCount, so one ulong
    // holds each state. Checked by LootContainersTests: inside the walls and outside the plaza, on flat terrain or a box top,
    // clear of every box, door, harvestable, loot point and reboot station, more than DoorInteractRange + 1 m from every door
    // centre, and reachable on foot (a free standing spot in reach with a clear line of sight).
    public static class LootContainers
    {
        public const int MaxCount = 64;

        // Chest 1.0 x 0.7 x 0.6 m, ammo box 0.6 x 0.4 x 0.4 m (X x Y x Z at yaw 0).
        private static readonly Vector3 s_chestSize = new Vector3(1.0f, 0.7f, 0.6f);
        private static readonly Vector3 s_ammoBoxSize = new Vector3(0.6f, 0.4f, 0.4f);

        private static readonly LootContainer[] s_all =
        {
            // Rustvale: a chest at the back of each house, one outside.
            Chest(-51.5f, 56.5f, 180f),
            Chest(-40.5f, 54.5f, 180f),
            Chest(-47.5f, 35.5f, 0f),
            Chest(-34f, 40f, 270f),
            // Gearworks: two chests and an ammo box inside the warehouse, a chest and an ammo box outside.
            Chest(40f, 50f, 90f),
            Chest(52f, 50f, 270f),
            Ammo(46f, 50f, 0f),
            Chest(60f, 50f, 270f),
            Ammo(34f, 48f, 90f),
            // Stonefield: chests behind the broken walls, an ammo box south of the middle wall.
            Chest(-56f, -44f, 0f),
            Chest(-42f, -52f, 90f),
            Ammo(-50f, -60f, 180f),
            // Lookout: a chest and an ammo box on the plateau.
            Chest(40f, -48f, 0f),
            Ammo(48f, -41f, 270f),
            // Open ground next to the cover.
            Chest(15f, 16f, 180f),
            Chest(-16f, -20f, 0f),
            Chest(28f, 4f, 270f),
            Chest(-28f, -4f, 90f),
            Ammo(-16f, 20f, 90f),
            Ammo(20f, -16f, 0f),
            Ammo(8f, -20f, 180f),
            // Corners.
            Chest(64f, 64f, 180f),
            Chest(-64f, -64f, 0f),
            Chest(66f, -64f, 0f),
            Ammo(-64f, 64f, 180f),
            // The woods and the edges.
            Chest(-28f, 64f, 180f),
            Chest(-66f, 32f, 90f),
            Chest(28f, -64f, 0f),
            Ammo(56f, 28f, 0f),
            Ammo(70f, 10f, 270f),
            Ammo(-70f, -10f, 90f),
            Ammo(-12f, -72f, 0f),
            Ammo(12f, 74f, 180f),
            Ammo(36f, 70f, 180f),
        };

        public static ReadOnlySpan<LootContainer> All => s_all;

        public static int Count => s_all.Length;

        // 기능: 종류별 크기를 돌려준다.
        // 입력: kind - 컨테이너 종류.
        // 출력: Yaw 0 기준 X·Y·Z 크기(m).
        public static Vector3 SizeOf(LootContainerKind kind) => kind == LootContainerKind.Chest ? s_chestSize : s_ammoBoxSize;

        // 기능: 지형 위의 Chest를 만든다(언덕을 옮기면 함께 움직인다).
        // 입력: x, z - 지면 좌표, yaw - 방향(0·90·180·270).
        // 출력: 지형 높이에 놓인 Chest.
        private static LootContainer Chest(float x, float z, float yaw) =>
            new LootContainer(LootContainerKind.Chest, new Vector3(x, GameMap.Terrain.Height(x, z), z), yaw);

        // 기능: 지형 위의 Ammo Box를 만든다.
        // 입력: x, z - 지면 좌표, yaw - 방향(0·90·180·270).
        // 출력: 지형 높이에 놓인 Ammo Box.
        private static LootContainer Ammo(float x, float z, float yaw) =>
            new LootContainer(LootContainerKind.AmmoBox, new Vector3(x, GameMap.Terrain.Height(x, z), z), yaw);
    }

    // Phase 16 D6: a supply drop's fall, computed the same way on both sides from what the SupplyDrops packet carries (start
    // tick, landing tick, landing height): no server physics, nothing sent per tick (like the drop transport's route).
    public static class SupplyDropFall
    {
        // How far above its landing height a supply drop appears. A shared constant because the packet does not carry it;
        // the fall speed (loot.json supplyDrops.fallSpeed) sets the landing tick.
        public const float StartHeight = 60f;

        // 기능: tick 시점의 Supply Drop 높이를 구한다(시작 Tick에 착지 높이 + StartHeight, 착지 Tick까지 일정한 속도로 내려와 그 뒤 착지 높이).
        // 입력: landY - 착지 높이, startTick - 생성 Tick, landTick - 착지 Tick, tick - 구할 시점(소수 Tick 가능).
        // 출력: 그 시점의 Y. landTick <= startTick이면 착지 높이.
        public static float HeightAt(float landY, uint startTick, uint landTick, double tick)
        {
            if (landTick <= startTick || tick >= landTick) return landY;
            if (tick <= startTick) return landY + StartHeight;
            double left = (landTick - tick) / (landTick - (double)startTick);
            return landY + (float)(StartHeight * left);
        }

        // 기능: 낙하 시간(Tick)을 구한다(StartHeight를 fallSpeed로 나눈 초 × simHz, 반올림, 최소 1).
        // 입력: fallSpeed - 초당 낙하 거리(m/s, 0보다 큼), simHz - Tick 속도.
        // 출력: 생성부터 착지까지의 Tick 수.
        public static uint FallTicks(float fallSpeed, int simHz)
        {
            double ticks = Math.Round(StartHeight / fallSpeed * simHz, MidpointRounding.AwayFromZero);
            return ticks < 1 ? 1u : (uint)ticks;
        }
    }
}
