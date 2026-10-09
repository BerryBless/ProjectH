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

    // 기능: Loot Point의 World 위치를 돌려준다.
    // 입력: point - Loot Point 색인(0..Count-1).
    // 출력: 그 점의 위치.
    public Vector3 Position(int point) => _points[point].Position;

    // 기능: Loot Point의 표에서 아이템 하나를 이 경기의 Random으로 굴린다.
    // 입력: point - Loot Point 색인.
    // 출력: 굴린 LootRoll(Random 상태가 앞으로 간다).
    public LootRoll Roll(int point) => _data.Loot.Roll(_tables[point], _rng, _data.Weapons, _data.Items);

    // 기능: Loot Point의 아이템이 World를 떠났음을(완전히 주워짐) 기록한다. 재생성이 켜져 있으면 respawnTicks 뒤 새 굴림이 예정된다(D7).
    // 입력: point - Loot Point 색인, now - 마지막 Tick.
    // 출력: 반환값 없음. 재생성이 꺼져 있으면(respawnTicks 0) 아무것도 바뀌지 않는다.
    public void OnTaken(int point, uint now)
    {
        if (_respawnTicks == 0) return;
        _waiting[point] = true;
        _refillAt[point] = now + _respawnTicks;
    }

    // 기능: Loot Point의 재생성 시각이 되었는지 본다.
    // 입력: point - Loot Point 색인, now - 마지막 Tick.
    // 출력: 재생성을 기다리고 있고 그 Tick이 지났으면 true.
    public bool IsDue(int point, uint now) => _waiting[point] && now >= _refillAt[point];

    // 기능: 새 경기의 Seed로 Random을 다시 만들고 대기 중인 재생성을 모두 지운다(Phase 5 D3: LootSeed + round, 경기 시작마다 한 번).
    // 입력: seed - 이 경기의 Loot Seed.
    // 출력: 반환값 없음. Random과 대기 상태가 초기화된다.
    public void Restart(int seed)
    {
        _rng = new Random(seed);
        Array.Clear(_waiting);
    }

    // 기능: Loot Point가 다시 채워졌음을 기록한다.
    // 입력: point - Loot Point 색인.
    // 출력: 반환값 없음. 그 점의 재생성 대기가 풀린다.
    public void OnRefilled(int point) => _waiting[point] = false;
}
