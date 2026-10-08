using System;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 16: loot containers (chests, ammo boxes) and supply drops. Game loop thread only, like the rest of Match: no lock.
// Everything here is a fixed array sized by the map (LootContainers.MaxCount) or the packet (SupplyDropsPacket.MaxSupplyDrops):
// nothing grows during a match. Two Random objects are made per match start (and once in the dev sandbox's constructor),
// never per tick.
public sealed partial class Match
{
    // D2: the container and supply drop seed streams are kept apart from the floor loot's (LootSeed + round), so floor loot
    // does not change when a container is opened (or not). Mixed in after the round, never per tick.
    private const int ContainerSeedSalt = 0x16C0_57A1;
    private const int SupplyDropSeedSalt = 0x16D0_D20B;
    // D6: candidate landing spots inside 0.6 x the next circle's radius, at most this many tries, clamped to +-ArenaClamp,
    // XZ clearance from map things and from living players.
    private const int SupplyDropCandidates = 16;
    private const float SupplyDropRadiusFactor = 0.6f;
    private const float SupplyDropArenaClamp = 60f;
    private const float SupplyDropClear = 2f;
    private const float SupplyDropPlayerClear = 15f;
    // Phase 16 review: the second pass drops the 15 m rule but still never lands right next to a living player.
    private const float SupplyDropPlayerMinClear = 3f;
    // Review fix: a scheduled drop that finds no clear spot for this long is skipped, so a blocked spot (a radius 0 last
    // circle next to a map box, players gathered at the centre) cannot hold it, and every drop after it, until the match ends.
    private const int SupplyDropRetrySeconds = 10;
    // The point a supply drop's line of sight check aims at (above its landing height); the client draws a box about 1 m high.
    private const float SupplyDropCenterHeight = 0.5f;
    // D5: container loot lies on a circle this far around the container (like a death drop).
    private const float ContainerDropRadius = 1f;

    private readonly LootTable _lootTable;
    private readonly int _chestTable;
    private readonly int _ammoBoxTable;
    private readonly int _supplyDropTable;
    private readonly uint _supplyDropFallTicks;
    // D3: which containers spawned this match and which of those are open; what every client was last told.
    private ulong _containerSpawned;
    private ulong _containerOpened;
    private ulong _sentContainerSpawned;
    private ulong _sentContainerOpened;
    // D2: each container's loot, rolled at the match start (LootTable.MaxRolls slots per container). Never sent before it opens.
    private readonly LootRoll[] _containerLoot = new LootRoll[LootContainers.MaxCount * LootTable.MaxRolls];
    private readonly byte[] _containerLootCount = new byte[LootContainers.MaxCount];
    private Random? _containerRng;
    private Random? _supplyDropRng;
    // D6, D7: the match's supply drops (slot = Id = spawn order), their loot (rolled when they spawn), the landing positions
    // FindTarget reads, how many the schedule already made and the tick its clock (the zone clock) started at.
    private readonly SupplyDropInfo[] _drops = new SupplyDropInfo[SupplyDropsPacket.MaxSupplyDrops];
    private readonly Vector3[] _dropPositions = new Vector3[SupplyDropsPacket.MaxSupplyDrops];
    private readonly LootRoll[] _dropLoot = new LootRoll[SupplyDropsPacket.MaxSupplyDrops * LootTable.MaxRolls];
    private readonly byte[] _dropLootCount = new byte[SupplyDropsPacket.MaxSupplyDrops];
    private int _dropCount;
    private int _dropsScheduled;
    private uint _zoneClockStart;
    private bool _dropsDirty;
    private readonly uint[] _dropScheduleTicks;
    // Phase 16 review: one pick's candidates, kept for the second pass (fixed, made once).
    private readonly Vector2[] _spotCandidates = new Vector2[SupplyDropCandidates];
    // Scratch for one container's loot while it is spawned (no allocation per open).
    private readonly LootRoll[] _rollScratch = new LootRoll[LootTable.MaxRolls];

    // Phase 16 counters since this match object was made (GameLoop copies them to the Health line every tick).
    public long ContainersOpened { get; private set; }
    public long SupplyDropsSpawned { get; private set; }
    // Review fix: scheduled drops given up after SupplyDropRetrySeconds without a clear spot.
    public long SupplyDropsSkipped { get; private set; }
    public long SupplyDropsLanded { get; private set; }
    public long SupplyDropsOpened { get; private set; }
    public long ContainerLootItems { get; private set; }
    public long ContainerOpensBlocked { get; private set; }
    public long LootStatePackets { get; private set; }

    // 기능: Health·Meter용 Loot Container·Supply Drop 수치를 묶어 돌려준다.
    // 입력: 없음.
    // 출력: LootCounts 값.
    public Diagnostics.LootCounts LootCounts() => new(ContainersOpened, SupplyDropsSpawned, SupplyDropsLanded, SupplyDropsOpened,
        ContainerLootItems, ContainerOpensBlocked, LootStatePackets);

    // Test and QA seams (read only).
    internal ulong ContainerSpawnedMask => _containerSpawned;
    internal ulong ContainerOpenedMask => _containerOpened;
    internal int SupplyDropCount => _dropCount;
    internal SupplyDropInfo SupplyDropAt(int slot) => _drops[slot];
    internal uint ZoneClockStart => _zoneClockStart;

    // 기능: 한 Container의 미리 굴린 Loot를 돌려준다(테스트·QA용, 열기 전에도 읽는다).
    // 입력: id - Container id.
    // 출력: 그 Container의 Loot 칸(생성되지 않았으면 빈 span).
    internal ReadOnlySpan<LootRoll> ContainerLoot(int id) => new(_containerLoot, id * LootTable.MaxRolls, _containerLootCount[id]);

    // 기능: Supply Drop 하나의 미리 굴린 Loot를 돌려준다(테스트·QA용).
    // 입력: slot - Supply Drop 칸.
    // 출력: Loot 칸.
    internal ReadOnlySpan<LootRoll> SupplyDropLoot(int slot) => new(_dropLoot, slot * LootTable.MaxRolls, _dropLootCount[slot]);

    // 기능: 일정(초)을 Tick으로 바꾼다.
    // 입력: loot - Loot 표, simHz - Tick 속도.
    // 출력: 자기장 시계 시작부터의 Tick 배열(생성자에서 한 번).
    private static uint[] ScheduleTicks(LootTable loot, int simHz)
    {
        ReadOnlySpan<double> seconds = loot.SupplyDropSeconds;
        var ticks = new uint[seconds.Length];
        for (int i = 0; i < ticks.Length; i++) ticks[i] = (uint)Math.Round(seconds[i] * simHz, MidpointRounding.AwayFromZero);
        return ticks;
    }

    // 기능: Container마다 생성 여부와 Loot를 굴린다(D2). 이전 상태는 지운다. 표가 없는 종류는 생기지 않는다.
    // 입력: seed - 이 경기의 Container 시드(RoundSeed(LootSeed, LootSalt)에서 섞은 값. 리뷰 수정 C1).
    // 출력: 반환값 없음. 마스크와 Loot 칸이 바뀌고 Tick 끝에 ContainerStates가 간다.
    private void RollContainers(int seed)
    {
        _containerRng = new Random(seed);
        _containerSpawned = 0;
        _containerOpened = 0;
        Array.Clear(_containerLootCount);
        ReadOnlySpan<LootContainer> all = LootContainers.All;
        for (int i = 0; i < all.Length; i++)
        {
            bool chest = all[i].Kind == LootContainerKind.Chest;
            int table = chest ? _chestTable : _ammoBoxTable;
            if (table < 0) continue;
            double chance = chest ? _lootTable.ChestSpawnChance : _lootTable.AmmoBoxSpawnChance;
            if (_containerRng.NextDouble() >= chance) continue;
            _containerSpawned |= 1UL << i;
            _containerLootCount[i] = (byte)_lootTable.RollAll(table, _containerRng, _weapons, _items, _containerLoot.AsSpan(i * LootTable.MaxRolls, LootTable.MaxRolls));
        }
    }

    // 기능: 경기 시작(D2, D3, D6): Container를 이 경기 시드로 굴리고, Supply Drop 목록을 지우고 그 흐름의 Random과 시계를 정한다.
    // 입력: now - 마지막 Tick, zoneClockStart - 자기장 시계가 시작하는 Tick(수송기 경로 끝 또는 now).
    // 출력: 반환값 없음. Tick 끝에 ContainerStates·SupplyDrops가 모두에게 간다.
    private void StartLoot(uint now, uint zoneClockStart)
    {
        int roundSeed = RoundSeed(_lootSeed, LootSalt);   // review fix C1: from the match secret unless DeterministicSeeds
        RollContainers(unchecked(roundSeed * -1640531535 + ContainerSeedSalt));
        ClearSupplyDrops();
        _supplyDropRng = new Random(unchecked(roundSeed * -1640531535 + SupplyDropSeedSalt));
        _zoneClockStart = zoneClockStart;
    }

    // 기능: 라운드 리셋(D3): 두 마스크와 Supply Drop 목록을 지운다(대기실에는 Container가 없다).
    // 입력: 없음.
    // 출력: 반환값 없음. Tick 끝에 빈 상태가 모두에게 간다.
    private void ResetLoot()
    {
        _containerSpawned = 0;
        _containerOpened = 0;
        Array.Clear(_containerLootCount);
        ClearSupplyDrops();
    }

    // 기능: Supply Drop 목록과 일정 진행을 지운다(전송은 Tick 끝).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void ClearSupplyDrops()
    {
        _dropCount = 0;
        _dropsScheduled = 0;
        Array.Clear(_drops);
        Array.Clear(_dropLootCount);
        _dropsDirty = true;
    }

    // 기능: 경기 중 매 Tick(D6): 일정이 된 Supply Drop을 만들고(놓을 자리가 없으면 일정을 지키고 다음 Tick에 다시 시도, Phase 16 리뷰;
    //   SupplyDropRetrySeconds 동안 자리가 없으면 그 Drop은 건너뛰고 다음 일정으로 간다), 착지 Tick이 된 것을 Landed로 바꾼다.
    //   결과 화면·대기실에서는 부르지 않는다.
    // 입력: now - 마지막 Tick(시뮬레이션 중인 Tick은 now + 1).
    // 출력: 반환값 없음. 바뀌면 Tick 끝에 SupplyDrops가 간다. 할당 없음.
    private void UpdateSupplyDrops(uint now)
    {
        uint tick = now + 1;
        if (_dropsScheduled < _dropScheduleTicks.Length && tick >= _zoneClockStart + _dropScheduleTicks[_dropsScheduled])
        {
            // A pick that finds no clear spot is tried again next tick (only during this match; at most 2 x 16 + 1 checks a tick),
            // for SupplyDropRetrySeconds at most; then this drop is skipped.
            uint due = _zoneClockStart + _dropScheduleTicks[_dropsScheduled];
            if (SpawnSupplyDrop(tick, null) >= 0 || _dropCount >= SupplyDropsPacket.MaxSupplyDrops) _dropsScheduled++;
            else if (tick - due >= (uint)(SupplyDropRetrySeconds * _simHz))
            {
                _dropsScheduled++;
                SupplyDropsSkipped++;
            }
        }
        for (int i = 0; i < _dropCount; i++)
        {
            if (_drops[i].State != SupplyDropState.Falling || tick < _drops[i].LandTick) continue;
            _drops[i].State = SupplyDropState.Landed;
            SupplyDropsLanded++;
            _dropsDirty = true;
        }
    }

    // 기능: Supply Drop 하나를 만든다(D6): 위치(지정 또는 규칙), 착지 높이(지형, 건설 조각은 보지 않는다), 시작·착지 Tick, Loot(같은 흐름).
    // 입력: tick - 시작 Tick, at - QA 지정 XZ(null = 위치 규칙).
    // 출력: 만든 칸, 이미 최대 수이거나 경기가 아니거나(흐름 없음) 위치 규칙이 자리를 찾지 못했으면 -1.
    private int SpawnSupplyDrop(uint tick, Vector2? at)
    {
        if (_dropCount >= SupplyDropsPacket.MaxSupplyDrops || _supplyDropRng == null) return -1;
        Vector2 xz;
        if (at.HasValue) xz = at.Value;
        else if (!TryPickSupplyDropSpot(_supplyDropRng, out xz)) return -1;
        xz = new Vector2(Math.Clamp(xz.X, -SupplyDropArenaClamp, SupplyDropArenaClamp), Math.Clamp(xz.Y, -SupplyDropArenaClamp, SupplyDropArenaClamp));
        int slot = _dropCount++;
        float landY = GameMap.Terrain.Height(xz.X, xz.Y);
        _drops[slot] = new SupplyDropInfo
        {
            Id = (byte)slot,
            State = SupplyDropState.Falling,
            X = xz.X,
            Z = xz.Y,
            LandY = landY,
            StartTick = tick,
            LandTick = tick + _supplyDropFallTicks,
        };
        _dropPositions[slot] = new Vector3(xz.X, landY, xz.Y);
        _dropLootCount[slot] = _supplyDropTable < 0 ? (byte)0
            : (byte)_lootTable.RollAll(_supplyDropTable, _supplyDropRng, _weapons, _items, _dropLoot.AsSpan(slot * LootTable.MaxRolls, LootTable.MaxRolls));
        SupplyDropsSpawned++;
        _dropsDirty = true;
        return slot;
    }

    // 기능: Supply Drop 위치를 고른다(D6, Phase 16 리뷰): 다음 원(지금 Phase의 목표 원, Phase 0이면 원 1) 반지름 × 0.6 안에서 후보 16개를
    //   한 번 굴려(±60 m로 자름) 1차로 장애물(맵 상자 발자국·문·채집 대상·Container·스테이션·Loot 지점·다른 Supply Drop과 XZ 2 m)과
    //   살아 있는 플레이어 15 m를 모두 만족하는 첫 후보, 2차로 같은 후보들에서 장애물과 플레이어 3 m만 만족하는 첫 후보, 마지막으로
    //   다음 원 중심(±60 m로 자름)을 2차 조건으로 본다.
    // 입력: rng - Supply Drop 흐름의 Random, spot - 결과.
    // 출력: 자리를 찾았으면 true와 XZ, 모두 실패하면 false(호출자가 다음 Tick에 다시 시도한다). 할당 없음.
    private bool TryPickSupplyDropSpot(Random rng, out Vector2 spot)
    {
        int circle = Math.Clamp(_zone.Phase == 0 ? 1 : _zone.Phase, 0, _zone.PhaseCount);
        float cx = _zone.CenterX(circle);
        float cz = _zone.CenterZ(circle);
        float radius = _zone.Radius(circle) * SupplyDropRadiusFactor;
        for (int i = 0; i < SupplyDropCandidates; i++)
        {
            double angle = rng.NextDouble() * 2.0 * Math.PI;
            double distance = Math.Sqrt(rng.NextDouble()) * radius;
            _spotCandidates[i] = new Vector2(Math.Clamp((float)(cx + Math.Cos(angle) * distance), -SupplyDropArenaClamp, SupplyDropArenaClamp),
                Math.Clamp((float)(cz + Math.Sin(angle) * distance), -SupplyDropArenaClamp, SupplyDropArenaClamp));
        }
        for (int i = 0; i < SupplyDropCandidates; i++)
        {
            spot = _spotCandidates[i];
            if (SupplyDropSpotClear(spot.X, spot.Y)) return true;
        }
        for (int i = 0; i < SupplyDropCandidates; i++)
        {
            spot = _spotCandidates[i];
            if (SpotObstaclesClear(spot.X, spot.Y) && PlayersClear(spot.X, spot.Y, SupplyDropPlayerMinClear)) return true;
        }
        spot = new Vector2(Math.Clamp(cx, -SupplyDropArenaClamp, SupplyDropArenaClamp), Math.Clamp(cz, -SupplyDropArenaClamp, SupplyDropArenaClamp));
        return SpotObstaclesClear(spot.X, spot.Y) && PlayersClear(spot.X, spot.Y, SupplyDropPlayerMinClear);
    }

    // 기능: 후보 지점이 D6 1차 조건(장애물 XZ 2 m, 살아 있는 플레이어 15 m)을 모두 만족하는지 본다(건설 조각은 보지 않는다).
    // 입력: x, z - 후보 좌표.
    // 출력: 만족하면 true.
    internal bool SupplyDropSpotClear(float x, float z) => SpotObstaclesClear(x, z) && PlayersClear(x, z, SupplyDropPlayerClear);

    // 기능: 살아 있는 플레이어가 모두 수평 minDistance 이상 떨어져 있는지 본다.
    // 입력: x, z - 후보 좌표, minDistance - 최소 거리.
    // 출력: 떨어져 있으면 true.
    private bool PlayersClear(float x, float z, float minDistance)
    {
        var p = new Vector3(x, 0f, z);
        foreach (var player in _players)
        {
            if (player.Alive && HorizontalSq(p, player.State.Position) < minDistance * minDistance) return false;
        }
        return true;
    }

    // 기능: 후보 지점이 맵 상자 발자국·문·채집 대상·Container·스테이션·Loot 지점·다른 Supply Drop과 XZ 2 m 떨어졌는지 본다.
    // 입력: x, z - 후보 좌표.
    // 출력: 떨어졌으면 true.
    internal bool SpotObstaclesClear(float x, float z)
    {
        var p = new Vector3(x, 0f, z);
        foreach (Box box in GameMap.Boxes) if (FootprintDistance(p, box) < SupplyDropClear) return false;
        foreach (Box door in GameMap.Doors) if (FootprintDistance(p, door) < SupplyDropClear) return false;
        foreach (Harvestable h in GameMap.Harvestables) if (FootprintDistance(p, h.Bounds) < SupplyDropClear) return false;
        foreach (LootContainer c in LootContainers.All) if (FootprintDistance(p, c.Bounds) < SupplyDropClear) return false;
        foreach (Vector3 s in RebootStations.All) if (HorizontalSq(p, s) < SupplyDropClear * SupplyDropClear) return false;
        foreach (LootPoint l in LootPoints.All) if (HorizontalSq(p, l.Position) < SupplyDropClear * SupplyDropClear) return false;
        for (int i = 0; i < _dropCount; i++) if (HorizontalSq(p, _dropPositions[i]) < SupplyDropClear * SupplyDropClear) return false;
        return true;
    }

    // 기능: 점에서 상자 바닥면까지의 지면 거리를 잰다.
    // 입력: p - 점, box - 상자.
    // 출력: 거리(안이면 0).
    private static float FootprintDistance(Vector3 p, in Box box)
    {
        float dx = MathF.Max(0f, MathF.Max(box.Min.X - p.X, p.X - box.Max.X));
        float dz = MathF.Max(0f, MathF.Max(box.Min.Z - p.Z, p.Z - box.Max.Z));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // 기능: 두 점의 수평(XZ) 거리 제곱을 구한다.
    // 입력: a, b - 점.
    // 출력: 거리 제곱.
    private static float HorizontalSq(Vector3 a, Vector3 b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    // D4: containers open only during the match or in the dev sandbox (never in the lobby or on the result screen).
    private bool ContainersActive => _flow.InMatch || _flow.DevRespawn;

    // 기능: 착지했고 닫힌 Supply Drop의 비트 마스크(D4, D6: 착지 전과 경기 밖은 0).
    // 입력: 없음.
    // 출력: 비트 k = 칸 k.
    private int LandedClosedDropMask()
    {
        if (!_flow.InMatch) return 0;
        int mask = 0;
        for (int i = 0; i < _dropCount; i++)
        {
            if (_drops[i].State == SupplyDropState.Landed) mask |= 1 << i;
        }
        return mask;
    }

    // 기능: E 누름(D4): 소생·재투입 대상이 없을 때 부른다. 문과 Container(착지한 Supply Drop 포함) 중 더 가까운 쪽(같은 거리면 문)을 쓴다.
    //   Container가 대상인데 시선에 막히면 그 E는 아무것도 하지 않는다(Phase 16 리뷰: Client는 시선을 모르고 "[E] 열기"를 띄우며 줍기 안내·문
    //   예측을 끄므로, 그 상태에서 줍기나 문이 움직이면 안 된다). 둘 다 없으면 (Phase 19 D6) 탈 수 있는 차량(Phase 17–19 리뷰: 그 차가 시선에 막히면 Container처럼 아무것도 하지 않는다), 그것도 없으면 줍기.
    // 입력: player - 행동 가능한 살아 있는 플레이어(차량에 타지 않음).
    // 출력: 반환값 없음. Container가 열리거나 문이 바뀌거나 차량에 타거나 PickupResult가 간다.
    private void Interact(PlayerEntity player)
    {
        Vector3 feet = player.State.Position;
        int door = DoorRules.FindTarget(feet, player.State.Yaw, GameMap.Doors);
        int target = FindContainerTarget(player, out float distanceSq);
        if (target >= 0 && ContainerRules.PreferContainer(feet, door, GameMap.Doors, distanceSq))
        {
            TryOpenTarget(player, target);   // blocked: counted, nothing else happens
            return;
        }
        if (door >= 0)
        {
            ToggleDoor(door);
            return;
        }
        if (TryEnterVehicle(player)) return;   // Phase 19 D6: before a pickup, so an item by the car never blocks entering
        Pickup(player);
    }

    // 기능: 지금 열 수 있는 대상 중 E가 가리키는 것을 찾는다(마스크는 누를 때마다 지금 상태로 만든다: 같은 Tick의 두 번째 사람은 열린 것을 못 고른다).
    // 입력: player - 행위자, distanceSq - 고른 대상의 수평 거리 제곱.
    // 출력: Container id, Supply Drop은 ContainerRules.SupplyDropTargetBase + 칸, 없으면 -1.
    private int FindContainerTarget(PlayerEntity player, out float distanceSq)
    {
        distanceSq = 0f;
        if (!ContainersActive) return -1;
        ulong closed = _containerSpawned & ~_containerOpened;
        int drops = LandedClosedDropMask();
        if (closed == 0 && drops == 0) return -1;
        return ContainerRules.FindTarget(player.State.Position, player.State.Yaw, LootContainers.All, closed, _dropPositions, drops, out distanceSq);
    }

    // 기능: 고른 대상을 연다(D4): 시선(눈 → 대상 가운데가 맵 상자·닫힌 문·지형에 막히지 않음, Container는 건설 조각도)을 보고 열린 상태로 바꾼 뒤
    //   미리 굴린 Loot를 둘레 1 m 원에 월드 아이템으로 놓는다(D5).
    // 입력: player - 행위자, target - FindContainerTarget 결과.
    // 출력: 열었으면 true, 시선이 막혔으면 false(아무것도 바뀌지 않는다).
    private bool TryOpenTarget(PlayerEntity player, int target)
    {
        Vector3 eye = player.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(player.State.Mode), 0f);
        if (target >= ContainerRules.SupplyDropTargetBase)
        {
            int slot = target - ContainerRules.SupplyDropTargetBase;
            Vector3 at = _dropPositions[slot];
            // Review fix: a drop lands through building pieces (D6), so pieces do not block opening it (a floor over it would
            // otherwise lock it and swallow every E nearby until the piece breaks).
            if (!ClearSight(eye, at + new Vector3(0f, SupplyDropCenterHeight, 0f), pieces: false))
            {
                ContainerOpensBlocked++;
                return false;
            }
            _drops[slot].State = SupplyDropState.Opened;
            _dropsDirty = true;
            SupplyDropsOpened++;
            SpawnContainerLoot(at, 0f, SupplyDropLoot(slot));
            return true;
        }
        LootContainer container = LootContainers.All[target];
        if (!ClearSight(eye, container.Center, pieces: true))
        {
            ContainerOpensBlocked++;
            return false;
        }
        _containerOpened |= 1UL << target;
        ContainersOpened++;
        SpawnContainerLoot(container.Position, container.Yaw, ContainerLoot(target));
        return true;
    }

    // 기능: 눈에서 점까지 맵 상자·닫힌 문·지형(pieces면 건설 조각도)이 막지 않는지 본다(채집 대상은 보지 않는다).
    // 입력: eye - 눈 위치, target - 목표점, pieces - 건설 조각도 볼지(Supply Drop은 false).
    // 출력: 보이면 true.
    private bool ClearSight(Vector3 eye, Vector3 target, bool pieces)
    {
        Vector3 delta = target - eye;
        float distance = delta.Length();
        if (distance < 1e-3f) return true;
        Vector3 direction = delta / distance;
        if (HitScan.TraceWorld(eye, direction, distance, _doors.World, GameMap.Terrain) < distance - 1e-3f) return false;
        return !pieces || !PieceTrace.Trace(eye, direction, distance, _build, out _, out _, out _);
    }

    // 기능: Loot를 기준점 둘레 1 m 원 위에 월드 아이템(떨어진 아이템 규칙: spawnPoint -1, 가득 차면 오래된 것부터 밀림)으로 놓는다.
    // 입력: origin - 기준 바닥 위치, yaw - 첫 아이템 방향(도), loot - 놓을 아이템들.
    // 출력: 반환값 없음. 아이템마다 ItemSpawned가 간다.
    private void SpawnContainerLoot(Vector3 origin, float yaw, ReadOnlySpan<LootRoll> loot)
    {
        int count = loot.Length;
        if (count == 0) return;
        // Copied first: SpawnItem never touches these arrays, but the span must not depend on that.
        loot.CopyTo(_rollScratch);
        CollisionWorld world = GatherAround(origin);
        for (int n = 0; n < count; n++)
        {
            Vector3 offset = ItemRules.Offset(yaw + 360f * n / count, ContainerDropRadius);
            Vector3 at = ItemRules.DropPosition(origin, offset, world.Boxes, GameMap.Terrain, world.Slopes);
            if (SpawnItem(_rollScratch[n], at, -1) != 0) ContainerLootItems++;
        }
    }

    // 기능: Tick 끝에 Container 마스크가 바뀌었으면 ContainerStates, Supply Drop이 바뀌었으면 SupplyDrops를 모두에게 보낸다(D3, D7).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void SendLootChanges()
    {
        if (_containerSpawned != _sentContainerSpawned || _containerOpened != _sentContainerOpened)
        {
            _sentContainerSpawned = _containerSpawned;
            _sentContainerOpened = _containerOpened;
            foreach (var p in _players) SendContainerStates(p.PeerId);
        }
        if (_dropsDirty)
        {
            _dropsDirty = false;
            foreach (var p in _players) SendSupplyDrops(p.PeerId);
        }
    }

    // 기능: 한 연결에 ContainerStates를 보낸다.
    // 입력: peerId - 연결 id.
    // 출력: 반환값 없음.
    private void SendContainerStates(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        ContainerStatesPacket.Write(ref writer, _containerSpawned, _containerOpened);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        LootStatePackets++;
    }

    // 기능: 한 연결에 SupplyDrops(경기의 Supply Drop 전체)를 보낸다.
    // 입력: peerId - 연결 id.
    // 출력: 반환값 없음.
    private void SendSupplyDrops(int peerId)
    {
        var writer = new PacketWriter(_sendBuffer);
        SupplyDropsPacket.Write(ref writer, new ReadOnlySpan<SupplyDropInfo>(_drops, 0, _dropCount));
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        LootStatePackets++;
    }

    // 기능: QA spawnSupplyDrop: 경기 중 Supply Drop을 바로 만든다(위치 규칙 또는 지정 XZ, 일정과 별개, 최대 수는 같다).
    // 입력: at - 지정 XZ(null = 규칙).
    // 출력: 만든 칸, 경기가 아니거나 가득 찼거나 위치 규칙이 이번에 자리를 찾지 못했으면 -1(QA 명령은 다시 시도하지 않는다).
    internal int QaSpawnSupplyDrop(Vector2? at) => _flow.InMatch ? SpawnSupplyDrop(ServerTick + 1, at) : -1;

    // 기능: QA setContainer: 시나리오 준비용으로 Container 상태를 강제한다. closed로 바꾸면 Loot가 없을 때 그 경기 흐름으로 굴린다.
    //   open은 Loot를 놓지 않고 열린 상태만 만든다. 경기 중 또는 개발 모드에서만.
    // 입력: id - Container id, state - 0 없음, 1 닫힘, 2 열림.
    // 출력: 바꿨으면 true.
    internal bool QaSetContainer(int id, int state)
    {
        if (!ContainersActive || id < 0 || id >= LootContainers.Count || state < 0 || state > 2) return false;
        ulong bit = 1UL << id;
        if (state == 0)
        {
            _containerSpawned &= ~bit;
            _containerOpened &= ~bit;
            _containerLootCount[id] = 0;
            return true;
        }
        if ((_containerSpawned & bit) == 0 || _containerLootCount[id] == 0)
        {
            int table = LootContainers.All[id].Kind == LootContainerKind.Chest ? _chestTable : _ammoBoxTable;
            if (table < 0) return false;
            _containerRng ??= new Random(unchecked(Seed(_lootSeed, LootSalt) * -1640531535 + ContainerSeedSalt));   // review fix C1
            _containerLootCount[id] = (byte)_lootTable.RollAll(table, _containerRng, _weapons, _items, _containerLoot.AsSpan(id * LootTable.MaxRolls, LootTable.MaxRolls));
        }
        _containerSpawned |= bit;
        if (state == 2) _containerOpened |= bit;
        else _containerOpened &= ~bit;
        return true;
    }
}
