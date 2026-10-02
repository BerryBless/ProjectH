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

    public bool IsDestroyed(int id) => (DestroyedMask & (1UL << id)) != 0;
    public int Health(int id) => _health[id];
    public bool HasWeakPoint(int id) => _hasWeakPoint[id];
    public Vector3 WeakPoint(int id) => _weakPoint[id];

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

    // The side of a box that faces a swing along direction: 0 -X, 1 +X, 2 -Z, 3 +Z (the side the swing comes in through).
    private static int FacingSide(Vector3 direction)
    {
        if (MathF.Abs(direction.X) >= MathF.Abs(direction.Z)) return direction.X > 0f ? 0 : 1;
        return direction.Z > 0f ? 2 : 3;
    }

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
