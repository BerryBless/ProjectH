using System;
using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Items;

// The loot spawn points of the match, the seeded Random that fills them (D5, D6) and the refill timers
// (D7). Owned by Match on the game loop thread, like everything else in the match: the Random is never
// shared with another thread. Fixed arrays, one entry per point: nothing grows.
public sealed class LootSpawner
{
    private readonly GameData _data;
    private readonly LootPoint[] _points;
    private readonly int[] _tables;
    private Random _rng;
    private readonly uint _respawnTicks;    // 0 = a looted point stays empty
    private readonly bool[] _waiting;       // the point's item was taken and a refill is due at _refillAt
    private readonly uint[] _refillAt;

    // 기능: Loot Point 목록과 시드 Random, 재생성 타이머를 만든다. 각 점의 표가 있고 가중치 항목이 1개 이상인지 검사한다(Phase 16 리뷰).
    // 입력: points - Loot Point, data - 게임 데이터, seed - 시드, respawnTicks - 재생성 Tick(0 = 없음).
    // 출력: LootSpawner. 표가 없거나 항목이 없거나 점이 너무 많으면 ArgumentException.
    public LootSpawner(ReadOnlySpan<LootPoint> points, GameData data, int seed, uint respawnTicks)
    {
        _data = data;
        _points = points.ToArray();
        _tables = new int[_points.Length];
        for (int i = 0; i < _points.Length; i++)
        {
            _tables[i] = data.Loot.TableIndex(_points[i].Table);
            if (_tables[i] < 0) throw new ArgumentException($"Loot point {i} names unknown table \"{_points[i].Table}\".", nameof(points));
            // Phase 16 review (Low): Roll picks one weighted entry, so the point's table needs one (not only guaranteed kinds).
            if (data.Loot.EntryCount(_tables[i]) == 0)
                throw new ArgumentException($"Loot point {i} names table \"{_points[i].Table}\" without entries.", nameof(points));
        }
        // Spawn-point items are never evicted (D13), so they must leave most of the store for drops.
        if (_points.Length > WorldItems.Capacity / 2)
            throw new ArgumentException($"At most {WorldItems.Capacity / 2} loot points.", nameof(points));
        _rng = new Random(seed);
        _respawnTicks = respawnTicks;
        _waiting = new bool[_points.Length];
        _refillAt = new uint[_points.Length];
    }

    public int Count => _points.Length;

    public Vector3 Position(int point) => _points[point].Position;

    public LootRoll Roll(int point) => _data.Loot.Roll(_tables[point], _rng, _data.Weapons, _data.Items);

    // The point's item left the world (picked up completely). With respawning on, a new roll is due
    // respawnTicks later (D7); dropped items never come here.
    public void OnTaken(int point, uint now)
    {
        if (_respawnTicks == 0) return;
        _waiting[point] = true;
        _refillAt[point] = now + _respawnTicks;
    }

    public bool IsDue(int point, uint now) => _waiting[point] && now >= _refillAt[point];

    // Phase 5 D3: every match rolls its loot from its own seed (LootSeed + round), with no timers pending.
    // One Random per match start, never per tick.
    public void Restart(int seed)
    {
        _rng = new Random(seed);
        Array.Clear(_waiting);
    }

    public void OnRefilled(int point) => _waiting[point] = false;
}
