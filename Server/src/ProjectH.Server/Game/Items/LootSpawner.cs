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

    // 기능: 맵의 루트 스폰 지점과 각 지점의 루트 테이블을 확인하고 시드 난수·리필 타이머를 준비한다.
    // 입력: points - 루트 스폰 지점, data - 루트 테이블을 가진 게임 데이터, seed - 루트 난수 시드, respawnTicks - 획득 후 다시 채울 때까지의 Tick(0이면 다시 채우지 않음).
    // 출력: 대기 중인 리필이 없는 LootSpawner. 알 수 없는 테이블 이름이나 WorldItems 용량 절반을 넘는 지점 수면 ArgumentException을 던진다.
    public LootSpawner(ReadOnlySpan<LootPoint> points, GameData data, int seed, uint respawnTicks)
    {
        _data = data;
        _points = points.ToArray();
        _tables = new int[_points.Length];
        for (int i = 0; i < _points.Length; i++)
        {
            _tables[i] = data.Loot.TableIndex(_points[i].Table);
            if (_tables[i] < 0) throw new ArgumentException($"Loot point {i} names unknown table \"{_points[i].Table}\".", nameof(points));
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

    // 기능: 루트 스폰 지점의 위치를 돌려준다.
    // 입력: point - 루트 스폰 지점 번호.
    // 출력: 그 지점의 월드 위치.
    public Vector3 Position(int point) => _points[point].Position;

    // 기능: 지점의 루트 테이블에서 아이템 하나를 굴린다. 매치 난수 상태를 소비한다.
    // 입력: point - 루트 스폰 지점 번호.
    // 출력: 굴려 나온 아이템(종류, 정의 ID, 등급, 수량).
    public LootRoll Roll(int point) => _data.Loot.Roll(_tables[point], _rng, _data.Weapons, _data.Items);

    // 기능: 스폰 지점 아이템이 완전히 획득되었을 때 리필 타이머를 건다.
    // 입력: point - 루트 스폰 지점 번호, now - 현재 서버 Tick.
    // 출력: 반환값 없음. 리스폰이 켜져 있으면 지점이 대기 상태가 되고 리필 Tick이 now + respawnTicks로 정해진다.
    // The point's item left the world (picked up completely). With respawning on, a new roll is due
    // respawnTicks later (D7); dropped items never come here.
    public void OnTaken(int point, uint now)
    {
        if (_respawnTicks == 0) return;
        _waiting[point] = true;
        _refillAt[point] = now + _respawnTicks;
    }

    // 기능: 스폰 지점의 리필 시점이 되었는지 확인한다.
    // 입력: point - 루트 스폰 지점 번호, now - 현재 서버 Tick.
    // 출력: 리필 대기 중이고 리필 Tick에 도달했으면 true, 아니면 false.
    public bool IsDue(int point, uint now) => _waiting[point] && now >= _refillAt[point];

    // 기능: 새 매치 시작 시 루트 난수를 새 시드로 바꾸고 대기 중인 리필을 모두 지운다.
    // 입력: seed - 이번 매치의 루트 시드(LootSeed + 라운드).
    // 출력: 반환값 없음. 난수가 다시 만들어지고 모든 지점의 리필 대기가 해제된다.
    // Phase 5 D3: every match rolls its loot from its own seed (LootSeed + round), with no timers pending.
    // One Random per match start, never per tick.
    public void Restart(int seed)
    {
        _rng = new Random(seed);
        Array.Clear(_waiting);
    }

    // 기능: 지점에 아이템이 다시 채워졌음을 기록한다.
    // 입력: point - 루트 스폰 지점 번호.
    // 출력: 반환값 없음. 그 지점의 보충 대기 표시가 해제된다.
    public void OnRefilled(int point) => _waiting[point] = false;
}
