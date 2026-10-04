using System;
using System.Numerics;
using ProjectH.Server.Game.Build;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Harvest;

// What one hit on a harvestable did (HarvestWorld.Hit).
public struct HarvestHitResult
{
    public int Damage;
    public int HealthLeft;
    public bool WeakPointHit;
    public bool Destroyed;
    // Resources the hit is worth before the player's cap (the per-hit yield, doubled on the weak point, plus the
    // destroy bonus).
    public int Resources;
}

// Phase 13 D6, D7: the state of the map's harvestable objects in the current match: health, which are destroyed, and
// each one's weak point. Fixed arrays of GameMap.Harvestables.Length; nothing allocates after construction. Reset at
// every match start and round reset (and a new Match starts reset). Owned by Match on the game loop thread.
public sealed class HarvestWorld
{
    private readonly BuildingCatalog _catalog;
    private readonly int[] _health;
    private readonly int[] _hits;
    private readonly bool[] _hasWeakPoint;
    private readonly int[] _face;
    private readonly Vector3[] _weakPoint;

    // 기능: 맵의 채집 대상 수만큼 상태 배열을 만들고 초기화한다.
    // 입력: catalog - 채집 대상 체력·약점·자원 수치를 담은 건설 데이터.
    // 출력: 모든 채집 대상이 최대 체력이고 약점이 없는 HarvestWorld.
    public HarvestWorld(BuildingCatalog catalog)
    {
        _catalog = catalog;
        int count = GameMap.Harvestables.Length;
        _health = new int[count];
        _hits = new int[count];
        _hasWeakPoint = new bool[count];
        _face = new int[count];
        _weakPoint = new Vector3[count];
        Reset();
    }

    public int Count => _health.Length;
    // Bit i = GameMap.Harvestables[i] is destroyed (HarvestStates, CollisionWorld.Gather).
    public ulong DestroyedMask { get; private set; }
    // Harvestables destroyed since this world was made (counters).
    public long DestroyedTotal { get; private set; }

    // 기능: 채집 대상이 파괴되었는지 DestroyedMask에서 확인한다.
    // 입력: id - GameMap.Harvestables 안의 채집 대상 번호.
    // 출력: 파괴되었으면 true, 서 있으면 false.
    public bool IsDestroyed(int id) => (DestroyedMask & (1UL << id)) != 0;
    // 기능: 채집 대상의 남은 체력을 돌려준다.
    // 입력: id - GameMap.Harvestables 안의 채집 대상 번호.
    // 출력: 남은 체력.
    public int Health(int id) => _health[id];
    // 기능: 채집 대상에 지금 약점이 있는지 확인한다.
    // 입력: id - GameMap.Harvestables 안의 채집 대상 번호.
    // 출력: 약점이 있으면 true, 없으면 false.
    public bool HasWeakPoint(int id) => _hasWeakPoint[id];
    // 기능: 채집 대상의 약점 위치를 돌려준다.
    // 입력: id - GameMap.Harvestables 안의 채집 대상 번호.
    // 출력: 약점의 월드 위치. HasWeakPoint가 false면 의미 없는 이전 값.
    public Vector3 WeakPoint(int id) => _weakPoint[id];

    // 기능: 모든 채집 대상을 경기 시작 상태로 되돌린다.
    // 입력: 없음.
    // 출력: 반환값 없음. 체력이 최대가 되고 맞은 횟수·약점·DestroyedMask가 초기화된다. DestroyedTotal은 유지된다.
    // D6 (request §24): every object stands again at full health, without a weak point.
    public void Reset()
    {
        ReadOnlySpan<Harvestable> all = GameMap.Harvestables;
        for (int i = 0; i < all.Length; i++)
        {
            _health[i] = _catalog.Harvestable(all[i].Kind).Health;
            _hits[i] = 0;
            _hasWeakPoint[i] = false;
        }
        DestroyedMask = 0;
    }

    // 기능: 채집 대상 하나에 휘두르기 한 번의 피해를 적용하고 약점을 만들거나 옮긴다.
    // 입력: id - 맞은 채집 대상 번호, point - 대상 표면의 맞은 위치, direction - 휘두르기 방향.
    // 출력: 피해량, 남은 체력, 약점 적중·파괴 여부, 획득 자원(플레이어 상한 적용 전)을 담은 결과. 체력·약점·DestroyedMask가 갱신된다.
    // D7: one hit at point (on the object's surface) by a swing along direction. The first hit makes the weak point on the
    // side facing the swinger; a hit within WeakPointRadius of it does WeakPointMultiplier x the damage and resources and
    // moves it. Positions come from (id, hits), so they are reproducible (request §21).
    public HarvestHitResult Hit(int id, Vector3 point, Vector3 direction)
    {
        Harvestable target = GameMap.Harvestables[id];
        HarvestableConfig config = _catalog.Harvestable(target.Kind);
        bool weak = _hasWeakPoint[id] && Vector3.Distance(point, _weakPoint[id]) <= _catalog.WeakPointRadius;
        int multiplier = weak ? _catalog.WeakPointMultiplier : 1;
        int damage = Math.Min(_health[id], _catalog.EnvironmentDamage * multiplier);
        _health[id] -= damage;
        _hits[id]++;
        if (!_hasWeakPoint[id])
        {
            _face[id] = FacingSide(direction);
            _hasWeakPoint[id] = true;
            _weakPoint[id] = PlaceWeakPoint(id, _face[id], _hits[id]);
        }
        else if (weak)
        {
            _weakPoint[id] = PlaceWeakPoint(id, _face[id], _hits[id]);
        }
        bool destroyed = _health[id] <= 0;
        if (destroyed)
        {
            DestroyedMask |= 1UL << id;
            DestroyedTotal++;
            _hasWeakPoint[id] = false;
        }
        return new HarvestHitResult
        {
            Damage = damage,
            HealthLeft = _health[id],
            WeakPointHit = weak,
            Destroyed = destroyed,
            Resources = config.BaseResourcePerHit * multiplier + (destroyed ? config.DestroyBonus : 0),
        };
    }

    // 기능: 휘두르기 방향이 들어오는 박스 옆면을 고른다.
    // 입력: direction - 휘두르기 방향.
    // 출력: 옆면 번호(0 -X, 1 +X, 2 -Z, 3 +Z).
    // The side of a box that faces a swing along direction: 0 -X, 1 +X, 2 -Z, 3 +Z (the side the swing comes in through).
    private static int FacingSide(Vector3 direction)
    {
        if (MathF.Abs(direction.X) >= MathF.Abs(direction.Z)) return direction.X > 0f ? 0 : 1;
        return direction.Z > 0f ? 2 : 3;
    }

    // 기능: 채집 대상 옆면 위의 약점 위치를 (id, hits)로 재현 가능하게 정한다.
    // 입력: id - 채집 대상 번호, side - 약점을 둘 옆면 번호, hits - 지금까지 맞은 횟수.
    // 출력: 그 옆면 위의 약점 월드 위치.
    // A point on that side: 15-85 % across it, and between 0.4 m above the base and 2 m (or 0.1 m under the top), where a
    // standing swing reaches.
    private static Vector3 PlaceWeakPoint(int id, int side, int hits)
    {
        Box box = GameMap.Harvestables[id].Bounds;
        uint h = Hash(id, hits);
        float across = 0.15f + 0.7f * (h & 0xFFFF) / 65535f;
        float up = (h >> 16) / 65535f;
        float bottom = box.Min.Y + 0.4f;
        float top = MathF.Min(box.Max.Y - 0.1f, box.Min.Y + 2f);
        float y = bottom + (top - bottom) * up;
        switch (side)
        {
            case 0: return new Vector3(box.Min.X, y, box.Min.Z + across * (box.Max.Z - box.Min.Z));
            case 1: return new Vector3(box.Max.X, y, box.Min.Z + across * (box.Max.Z - box.Min.Z));
            case 2: return new Vector3(box.Min.X + across * (box.Max.X - box.Min.X), y, box.Min.Z);
            default: return new Vector3(box.Min.X + across * (box.Max.X - box.Min.X), y, box.Max.Z);
        }
    }

    // 기능: (id, hits)를 섞어 결정적인 32비트 해시를 만든다.
    // 입력: id - 채집 대상 번호, hits - 맞은 횟수.
    // 출력: 같은 입력에 항상 같은 해시 값.
    // An integer mix of (id, hits): the same inputs always give the same point.
    private static uint Hash(int id, int hits)
    {
        uint h = (uint)id * 0x9E3779B1u + (uint)hits * 0x85EBCA77u + 0x27D4EB2Fu;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }
}
