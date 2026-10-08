using System;
using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game.Vehicles;

// Phase 19: one vehicle slot of a match. Match owns a fixed array of VehicleSettings.MaxVehicles of these, made once; a slot
// is in use from SpawnVehicle to its removal (wreck time over, match start, match end, round reset). Game loop thread only.
// Seats hold PlayerEntity references: every path that removes, resets, knocks down or eliminates a player unseats it first
// (Match.ForceExit), so a seat never outlives its player's place in the match.
public sealed class Vehicle
{
    public byte Id;
    public bool InUse;
    public VehicleState State;
    public VehicleMove Move;
    public int Health;
    // Index = seat (0 driver, 1 passenger); null = empty.
    public readonly PlayerEntity?[] Seats = new PlayerEntity?[VehicleSettings.SeatCount];
    // Wrecked: the tick the wreck is removed at.
    public uint WreckEndTick;
    // D7 rewind: the pose at the end of each recent tick.
    public readonly VehicleHistory History = new();
    // D7 credit: the last attackers (by JoinOrder and team at the time, like projectile owners: no PlayerEntity reference is
    // kept), newest overwrites the oldest of AttackerSlots.
    public const int AttackerSlots = 8;
    public readonly VehicleAttacker[] Attackers = new VehicleAttacker[AttackerSlots];
    public int AttackerNext;

    public bool IsActive => InUse && State == VehicleState.Active;
    public PlayerEntity? Driver => Seats[VehicleSettings.DriverSeat];

    // 기능: 칸을 새 차량으로 채운다(좌석·공격자 기록·위치 기록을 비운다).
    // 입력: id - 차량 id, move - 시작 상태, health - 체력, tick - 위치 기록 시작 Tick.
    // 출력: 반환값 없음. 칸이 Active로 쓰인다.
    public void Reset(byte id, in VehicleMove move, int health, uint tick)
    {
        Id = id;
        InUse = true;
        State = VehicleState.Active;
        Move = move;
        Health = health;
        WreckEndTick = 0;
        Array.Clear(Seats);
        Array.Clear(Attackers);
        AttackerNext = 0;
        History.Reset(tick, move.Position, move.Heading);
    }

    // 기능: 칸을 비운다(좌석 참조와 공격자 기록도 지운다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Clear()
    {
        InUse = false;
        Array.Clear(Seats);
        Array.Clear(Attackers);
        AttackerNext = 0;
    }

    // 기능: 공격자를 기록한다(가장 오래된 칸을 덮어쓴다).
    // 입력: joinOrder - 공격자 JoinOrder, team - 그때의 팀, tick - 피해 Tick.
    // 출력: 반환값 없음.
    public void RecordAttacker(uint joinOrder, byte team, uint tick)
    {
        Attackers[AttackerNext] = new VehicleAttacker(joinOrder, team, tick, true);
        AttackerNext = (AttackerNext + 1) % AttackerSlots;
    }

    // 기능: 이 플레이어가 앉은 좌석 번호를 찾는다.
    // 입력: player - 플레이어.
    // 출력: 좌석 번호, 없으면 -1.
    public int SeatOf(PlayerEntity player)
    {
        for (int i = 0; i < Seats.Length; i++)
        {
            if (Seats[i] == player) return i;
        }
        return -1;
    }
}

// Phase 19 D7: one attacker of a vehicle (Valid false = empty slot).
public readonly record struct VehicleAttacker(uint JoinOrder, byte Team, uint Tick, bool Valid);

// Phase 19 D7: a vehicle's recent poses for lag compensation, like PositionHistory: a fixed ring of Capacity records, never
// grows. Heading is interpolated along the shorter arc (359 to 1 passes 0, not 180).
public sealed class VehicleHistory
{
    public const int Capacity = 32;

    private readonly uint[] _ticks = new uint[Capacity];
    private readonly Vector3[] _positions = new Vector3[Capacity];
    private readonly float[] _headings = new float[Capacity];
    private int _count;
    private int _newest = -1;

    // 기능: 기록을 지우고 한 자세에서 다시 시작한다(생성).
    // 입력: tick - Tick, position - 위치, heading - 방향.
    // 출력: 반환값 없음.
    public void Reset(uint tick, Vector3 position, float heading)
    {
        _count = 0;
        _newest = -1;
        Record(tick, position, heading);
    }

    // 기능: Tick 끝 자세를 기록한다(같거나 이전 Tick은 무시).
    // 입력: tick - Tick, position - 위치, heading - 방향.
    // 출력: 반환값 없음.
    public void Record(uint tick, Vector3 position, float heading)
    {
        if (_count > 0 && tick <= _ticks[_newest]) return;
        _newest = (_newest + 1) % Capacity;
        _ticks[_newest] = tick;
        _positions[_newest] = position;
        _headings[_newest] = heading;
        if (_count < Capacity) _count++;
    }

    // 기능: (소수) Tick의 자세를 두 기록 사이에서 보간한다. 최신보다 뒤면 최신, 가장 오래된 것보다 앞이면 가장 오래된 것.
    // 입력: tick - Tick, heading - 결과 방향.
    // 출력: 위치(기록이 없으면 0).
    public Vector3 Sample(double tick, out float heading)
    {
        heading = 0f;
        if (_count == 0) return Vector3.Zero;
        for (int i = 0; i < _count; i++)
        {
            int index = (_newest - i + Capacity) % Capacity;
            if (_ticks[index] > tick) continue;
            heading = _headings[index];
            if (i == 0) return _positions[index];
            int next = (index + 1) % Capacity;
            float t = (float)((tick - _ticks[index]) / (_ticks[next] - _ticks[index]));
            float delta = _headings[next] - _headings[index];
            if (delta > 180f) delta -= 360f;
            else if (delta < -180f) delta += 360f;
            heading = VehicleSimulation.NormalizeHeading(_headings[index] + delta * t);
            return Vector3.Lerp(_positions[index], _positions[next], t);
        }
        int oldest = (_newest - _count + 1 + Capacity) % Capacity;
        heading = _headings[oldest];
        return _positions[oldest];
    }
}
