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

    // 기능: 맵 채집물 수만큼 고정 배열을 만들고 Reset한다.
    // 입력: catalog - 채집물 체력과 도구 수치를 담은 건설 데이터.
    // 출력: 모든 채집물이 서 있고 만피인 HarvestWorld.
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

    // 기능: 채집물이 파괴되었는지 본다.
    // 입력: id - GameMap.Harvestables 색인.
    // 출력: 파괴되었으면 true.
    public bool IsDestroyed(int id) => (DestroyedMask & (1UL << id)) != 0;

    // 기능: 채집물의 남은 체력을 돌려준다.
    // 입력: id - GameMap.Harvestables 색인.
    // 출력: 남은 체력(파괴되면 0).
    public int Health(int id) => _health[id];

    // 기능: 채집물에 약점이 있는지 본다(첫 타격 뒤 파괴 전까지).
    // 입력: id - GameMap.Harvestables 색인.
    // 출력: 약점이 있으면 true.
    public bool HasWeakPoint(int id) => _hasWeakPoint[id];

    // 기능: 채집물의 약점 위치를 돌려준다.
    // 입력: id - GameMap.Harvestables 색인.
    // 출력: 약점 World 위치(HasWeakPoint가 false면 이전 값).
    public Vector3 WeakPoint(int id) => _weakPoint[id];

    // 기능: 모든 채집물을 만피로 다시 세우고 약점을 지운다(D6, 요청서 §24).
    // 입력: 없음.
    // 출력: 반환값 없음. 체력·타격 수·약점·DestroyedMask가 초기화된다(DestroyedTotal은 그대로).
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

    // 기능: 채집물에 타격 하나를 적용한다(D7). 첫 타격은 휘두른 쪽 면에 약점을 만들고, 약점 WeakPointRadius 안 타격은 피해·자원을
    //   WeakPointMultiplier배로 하고 약점을 옮긴다. 위치는 (id, 타격 수)로 재현된다(요청서 §21).
    // 입력: id - GameMap.Harvestables 색인, point - 표면의 맞은 점, direction - 휘두른 단위 방향.
    // 출력: 피해·남은 체력·약점 여부·파괴 여부·자원량을 담은 HarvestHitResult. 파괴되면 DestroyedMask·DestroyedTotal이 바뀐다.
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

    // 기능: 휘두른 방향이 들어오는 상자 면을 고른다.
    // 입력: direction - 휘두른 단위 방향.
    // 출력: 0 = -X 면, 1 = +X 면, 2 = -Z 면, 3 = +Z 면.
    private static int FacingSide(Vector3 direction)
    {
        if (MathF.Abs(direction.X) >= MathF.Abs(direction.Z)) return direction.X > 0f ? 0 : 1;
        return direction.Z > 0f ? 2 : 3;
    }

    // 기능: 그 면 위의 약점 위치를 정한다: 가로 15-85 %, 높이는 바닥 위 0.4 m부터 2 m(또는 꼭대기 0.1 m 아래)까지(선 채로 닿는 높이).
    // 입력: id - GameMap.Harvestables 색인, side - FacingSide의 면, hits - 지금까지의 타격 수(Hash 입력).
    // 출력: 약점 World 위치.
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

    // 기능: (id, hits)를 섞은 정수 Hash(같은 입력은 늘 같은 값).
    // 입력: id - 채집물 색인, hits - 타격 수.
    // 출력: 32비트 Hash.
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
