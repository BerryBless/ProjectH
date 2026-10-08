using System;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Vehicles;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 19: vehicles (spec Docs/specs/2026-10-08-phase19-vehicle-design.md D1-D16). Same rules as the rest of Match: game loop
// thread only, no lock, no allocation per tick. The vehicles live in a fixed array of VehicleSettings.MaxVehicles slots made
// once; a slot is used from SpawnVehicle to RemoveVehicle (wreck time over, match start, match end, round reset). Ids are a
// u8 counter (1..255) that skips ids in use and ids removed less than a second ago (_vehicleRemovedTick), so a client that
// hides a missing vehicle after 1 s never mistakes a new vehicle for an old one.
public sealed partial class Match
{
    // D6: the exit spots, from the car's centre: across the side (driver's side first), then behind, then in front.
    private const float ExitSideDistance = 2f;
    private const float ExitEndDistance = 3.2f;
    // D6: a forced exit with every spot blocked puts the player this high above the car's height (it falls from there).
    private const float ForcedExitHeight = 2f;
    // Phase 17–19 review: the way out is checked from the seat to the exit spot at this height above both feet (the body's
    // middle), so a wall between them refuses the spot while a low kerb or step does not.
    private const float ExitPathHeight = MoveSettings.Height * 0.5f;

    private readonly VehicleCatalog _vehicleData;
    private readonly Vehicle[] _vehicles = CreateVehicleSlots();
    private int _vehicleCount;
    // Set when the last vehicle is removed: the next snapshot tick sends one empty VehicleStates (Count 0), then clears it.
    private bool _vehicleStatesEmptyPending;
    private byte _nextVehicleId = 1;
    // Index = vehicle id: the tick it was removed at, valid while _vehicleRemoved[id]. Fixed size (every u8 id).
    private readonly uint[] _vehicleRemovedTick = new uint[256];
    private readonly bool[] _vehicleRemoved = new bool[256];
    // D8 test seam: where StartMatch places the vehicles (null in the constructor = VehicleSpawns).
    private readonly Vector3[] _vehicleSpawnPoints;
    private readonly float[] _vehicleSpawnHeadings;
    // VehicleStates: this tick's records, built once and filtered per recipient.
    private readonly VehicleRecord[] _vehicleRecords = new VehicleRecord[VehicleSettings.MaxVehicles];
    private readonly Vehicle?[] _vehicleRecordOwners = new Vehicle?[VehicleSettings.MaxVehicles];
    // FireShot: one shot's vehicle hits, summed per vehicle (at most MaxPellets).
    private readonly Vehicle?[] _pelletVehicles = new Vehicle?[WeaponCatalogPacket.MaxPellets];
    private readonly float[] _pelletVehicleDamage = new float[WeaponCatalogPacket.MaxPellets];
    private int _pelletVehicleCount;

    // Phase 19 counters since this match object was made (QA and the Health line).
    public long VehiclesSpawned { get; private set; }
    public long VehiclesWrecked { get; private set; }
    public long VehicleEnters { get; private set; }
    // Phase 17–19 review: E presses whose target car was behind a wall (nothing happened).
    public long VehicleEntersBlocked { get; private set; }
    public long VehicleExits { get; private set; }
    public long VehicleImpacts { get; private set; }
    public long VehicleRunOvers { get; private set; }
    public long VehicleStatesSent { get; private set; }

    // Test and QA seams.
    internal int VehicleCount => _vehicleCount;
    internal VehicleCatalog VehicleData => _vehicleData;
    internal ReadOnlySpan<Vehicle> VehicleSlots => _vehicles;

    // 기능: 차량 칸 배열을 한 번 만든다(칸 객체도 미리 만든다).
    // 입력: 없음.
    // 출력: MaxVehicles개의 빈 칸.
    private static Vehicle[] CreateVehicleSlots()
    {
        var slots = new Vehicle[VehicleSettings.MaxVehicles];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Vehicle();
        return slots;
    }

    // 기능: 플레이어가 지금 행동(사격·건설·채집·회복·수류탄·줍기·문·소생 시작)할 수 있는지 본다(Phase 12 D12 모드 + Phase 19 D15 탄 상태).
    //   탄 사람의 모드는 Ground라 모드만 보는 검사는 걷는 것으로 본다: 플레이어 문맥의 모든 행동 검사는 이것을 쓴다.
    // 입력: player - 플레이어.
    // 출력: 행동 가능한 모드이고 차량에 타지 않았으면 true.
    private static bool CanAct(PlayerEntity player) => !player.InVehicle && ActionsAllowed(player.State.Mode);

    // 기능: 차량을 하나 만든다(D8: 경기 시작, QA spawnVehicle). 높이는 지형으로 맞춘다.
    // 입력: x·z - 중심, heading - 방향(도), vehicle - 결과.
    // 출력: 만들었으면 true. 칸이 가득 찼거나 쓸 id가 없으면 false.
    internal bool SpawnVehicle(float x, float z, float heading, out Vehicle? vehicle)
    {
        vehicle = null;
        if (_vehicleCount >= _vehicles.Length || !AllocateVehicleId(out byte id)) return false;
        foreach (Vehicle slot in _vehicles)
        {
            if (slot.InUse) continue;
            float h = VehicleSimulation.NormalizeHeading(heading);
            var move = new VehicleMove { Position = new Vector3(x, VehicleSimulation.GroundHeight(x, z, h, GameMap.Terrain), z), Heading = h };
            slot.Reset(id, move, _vehicleData.MaxHealth, ServerTick);
            _vehicleCount++;
            VehiclesSpawned++;
            vehicle = slot;
            return true;
        }
        return false;   // unreachable: _vehicleCount < Length means a free slot
    }

    // 기능: 새 차량 id를 고른다(1..255, 쓰는 중이거나 1초 안에 사라진 id는 건너뛴다). 최대 255번만 본다.
    // 입력: id - 결과.
    // 출력: 찾았으면 true.
    private bool AllocateVehicleId(out byte id)
    {
        for (int tries = 0; tries < 255; tries++)
        {
            byte candidate = _nextVehicleId;
            _nextVehicleId = candidate == 255 ? (byte)1 : (byte)(candidate + 1);
            if (VehicleIdInUse(candidate)) continue;
            if (_vehicleRemoved[candidate] && ServerTick - _vehicleRemovedTick[candidate] < _simHz) continue;
            id = candidate;
            return true;
        }
        id = 0;
        return false;
    }

    // 기능: 이 id를 쓰는 차량이 있는지 본다.
    // 입력: id - 차량 id.
    // 출력: 있으면 true.
    private bool VehicleIdInUse(byte id)
    {
        foreach (Vehicle v in _vehicles)
        {
            if (v.InUse && v.Id == id) return true;
        }
        return false;
    }

    // 기능: id로 차량을 찾는다(QA).
    // 입력: id - 차량 id.
    // 출력: 쓰는 중인 칸 또는 null.
    internal Vehicle? FindVehicle(int id)
    {
        foreach (Vehicle v in _vehicles)
        {
            if (v.InUse && v.Id == id) return v;
        }
        return null;
    }

    // 기능: 차량을 지운다. 탄 사람을 먼저 강제로 내리고 id의 제거 Tick을 남긴다. 마지막 차량이면 다음 Snapshot Tick에 빈 VehicleStates를 보내게 한다.
    // 입력: v - 쓰는 중인 칸.
    // 출력: 반환값 없음.
    private void RemoveVehicle(Vehicle v)
    {
        for (int seat = 0; seat < v.Seats.Length; seat++)
        {
            if (v.Seats[seat] is PlayerEntity p) ForceExit(p);
        }
        _vehicleRemoved[v.Id] = true;
        _vehicleRemovedTick[v.Id] = ServerTick;
        v.Clear();
        _vehicleCount--;
        // Review fix: the last one gone: one empty VehicleStates tells every client (a seated one learns it is out at once).
        if (_vehicleCount == 0) _vehicleStatesEmptyPending = true;
    }

    // 기능: 모든 차량을 지운다(D8: 경기 시작·경기 끝·라운드 리셋). 탄 사람은 먼저 내린다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void ClearVehicles()
    {
        foreach (Vehicle v in _vehicles)
        {
            if (v.InUse) RemoveVehicle(v);
        }
    }

    // 기능: 경기 시작 차량을 생성 지점마다 만든다(D8).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void SpawnMatchVehicles()
    {
        for (int i = 0; i < _vehicleSpawnPoints.Length; i++) SpawnVehicle(_vehicleSpawnPoints[i].X, _vehicleSpawnPoints[i].Z, _vehicleSpawnHeadings[i], out _);
    }

    // 기능: 한 Tick의 차량 갱신(D13 순서: 플레이어 다음): 잔해 제거, 운전자 입력으로 Step, 충격 피해, 좌석 위치 맞춤, 치기 판정.
    // 입력: now - 마지막으로 끝난 Tick.
    // 출력: 반환값 없음. 차량과 탄 사람의 위치, 피해가 바뀐다.
    private void UpdateVehicles(uint now)
    {
        if (_vehicleCount == 0) return;
        foreach (Vehicle v in _vehicles)
        {
            if (!v.InUse) continue;
            if (v.State == VehicleState.Wrecked)
            {
                if (now + 1 >= v.WreckEndTick) RemoveVehicle(v);
                continue;
            }
            PlayerEntity? driver = v.Driver;
            // A parked car (no driver, standing) cannot change: skip the gather.
            if (driver == null && v.Move.Speed == 0f && v.Move.Steer == 0f) continue;
            InputCommand input = driver?.VehicleInput ?? default;
            VehicleSimulation.Step(ref v.Move, input, driver != null, _tickSeconds, GatherAround(v.Move.Position), GameMap.Terrain,
                out VehicleStepResult step);
            if (step.Blocked && step.ImpactSpeed > _vehicleData.ImpactMinSpeed)
            {
                VehicleImpacts++;
                // D3: no attacker; a building piece takes nothing.
                if (_flow.DamageAllowed) DamageVehicle(v, (step.ImpactSpeed - _vehicleData.ImpactMinSpeed) * _vehicleData.ImpactDamagePerMps, null);
                if (!v.IsActive) continue;   // wrecked by the impact: its occupants are out
            }
            SyncSeats(v);
            if (driver != null && MathF.Abs(v.Move.Speed) > _vehicleData.RunOverMinSpeed && _flow.DamageAllowed) RunOver(v, driver, now);
        }
    }

    // 기능: 탄 사람의 위치를 좌석 위치로 맞추고 이동 상태를 쉬는 Ground로 둔다(D5: 이동 Step을 하지 않는다).
    // 입력: v - 차량.
    // 출력: 반환값 없음.
    private static void SyncSeats(Vehicle v)
    {
        for (int seat = 0; seat < v.Seats.Length; seat++)
        {
            PlayerEntity? p = v.Seats[seat];
            if (p == null) continue;
            p.State.Position = VehicleSimulation.SeatPosition(v.Move.Position, v.Move.Heading, seat);
            p.State.VelocityY = 0f;
            p.State.HorizontalVelocity = Vector2.Zero;
            p.State.Mode = MovementMode.Ground;
            p.State.ModeTicks = 0;
            p.Sprinting = false;
        }
    }

    // 기능: 치기(D3): 빠르게 움직이는 차량의 발자국과 겹친 살아 있는 다른 팀 플레이어(타지 않음, 수송기 아님)가 속도 × 배율 피해를 받는다
    //   (공격자 = 운전자, 같은 플레이어에게 쿨다운에 한 번). 차량은 막히지 않는다.
    // 입력: v - 차량, driver - 운전자, now - 마지막 Tick.
    // 출력: 반환값 없음.
    private void RunOver(Vehicle v, PlayerEntity driver, uint now)
    {
        Box front = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 0);
        Box rear = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 1);
        ushort damage = (ushort)Math.Clamp((int)MathF.Round(MathF.Abs(v.Move.Speed) * _vehicleData.RunOverDamagePerMps), 0, ushort.MaxValue);
        if (damage == 0) return;
        foreach (var p in _players)
        {
            if (!p.Alive || p.InVehicle || p == driver || p.State.Mode == MovementMode.Transport || SameTeam(driver, p) || now < p.RunOverReadyTick) continue;
            Vector3 feet = p.State.Position;
            float height = MovementSimulation.CollisionHeight(p.State.Mode);
            var body = new Box(new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth),
                new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth));
            if (!VehicleSimulation.Touches(front, body) && !VehicleSimulation.Touches(rear, body)) continue;
            p.RunOverReadyTick = now + _vehicleData.RunOverCooldownTicks;
            VehicleRunOvers++;
            ApplyHit(driver, p, damage);
        }
    }

    // 기능: 차량에 피해를 준다(D7). 공격자가 있으면 기록하고, 체력 0이면 잔해가 된다.
    // 입력: v - Active 차량, amount - 피해(반올림, 최소 1), attacker - 공격자(null = 충격·QA·없음).
    // 출력: 반환값 없음.
    private void DamageVehicle(Vehicle v, float amount, PlayerEntity? attacker)
    {
        if (!v.IsActive || !(amount > 0f)) return;
        int damage = Math.Max(1, (int)MathF.Round(MathF.Min(amount, 1_000_000f)));
        v.Health = Math.Max(0, v.Health - damage);
        if (attacker != null) v.RecordAttacker(attacker.JoinOrder, attacker.TeamId, ServerTick + 1);
        if (v.Health == 0) Wreck(v);
    }

    // 기능: 차량을 잔해로 만든다(D7): 멈추고, 탄 사람을 모두 내린 뒤 각자 파괴 피해(원인 Explosion, 공격자 = 최근 적 공격자)를 준다.
    //   wreckSeconds 뒤 사라진다.
    // 입력: v - 체력 0이 된 차량.
    // 출력: 반환값 없음.
    private void Wreck(Vehicle v)
    {
        v.State = VehicleState.Wrecked;
        v.Move.Speed = 0f;
        v.Move.Steer = 0f;
        v.WreckEndTick = ServerTick + 1 + _vehicleData.WreckTicks;
        VehiclesWrecked++;
        // At most SeatCount (2) occupants: unseat them all first, then hurt them (a knock-down or an elimination drops the items
        // where the player now stands).
        PlayerEntity? first = v.Seats[0], second = v.Seats.Length > 1 ? v.Seats[1] : null;
        if (first != null) ForceExit(first);
        if (second != null) ForceExit(second);
        if (!_flow.DamageAllowed || _vehicleData.WreckOccupantDamage <= 0) return;
        Vector3 blast = v.Move.Position + new Vector3(0f, (VehicleSettings.BodyBottom + VehicleSettings.BodyTop) * 0.5f, 0f);
        ushort damage = (ushort)Math.Min(_vehicleData.WreckOccupantDamage, ushort.MaxValue);
        if (first != null && first.Alive) ApplyExplosionHit(WreckCredit(v, first), first, damage, blast);
        if (second != null && second.Alive) ApplyExplosionHit(WreckCredit(v, second), second, damage, blast);
    }

    // 기능: 파괴 피해의 공격자를 고른다(D7): 최근 wreckCreditSeconds 안에 차량에 피해를 준, 이 탑승자와 팀이 아닌 사람 중 마지막 사람
    //   (아직 경기에 살아 있어야 한다).
    // 입력: v - 차량, occupant - 내린 탑승자.
    // 출력: 공격자 또는 null(자기장 피해와 같은 경로).
    private PlayerEntity? WreckCredit(Vehicle v, PlayerEntity occupant)
    {
        uint now = ServerTick + 1;
        for (int k = 1; k <= Vehicle.AttackerSlots; k++)
        {
            VehicleAttacker a = v.Attackers[(v.AttackerNext - k + Vehicle.AttackerSlots) % Vehicle.AttackerSlots];
            if (!a.Valid || now - a.Tick > _vehicleData.WreckCreditTicks) continue;
            if (a.JoinOrder == occupant.JoinOrder || (a.Team != 0 && a.Team == occupant.TeamId)) continue;
            PlayerEntity? attacker = FindLivingByJoinOrder(a.JoinOrder);
            if (attacker != null) return attacker;
        }
        return null;
    }

    // 기능: 타기 대상을 고른다(D6, Client 안내 복사본 VehiclePrompt.FindEnterTarget과 같은 규칙): Active이고 빈 좌석이 있으며
    //   발에서 차체까지 거리 ≤ EnterRange인 것 중 가장 가까운 것, 같으면 id가 작은 것.
    // 입력: feet - 발 위치, candidates - 후보(Id·위치·방향·탈 수 있는지).
    // 출력: 후보 번호, 없으면 -1.
    internal static int FindEnterTarget(Vector3 feet, ReadOnlySpan<VehicleCandidate> candidates)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            VehicleCandidate c = candidates[i];
            if (!c.Enterable) continue;
            float d = VehicleSimulation.DistanceToBody(feet, c.Position, c.Heading);
            if (d > VehicleSettings.EnterRange) continue;
            if (d < bestDistance || (d == bestDistance && c.Id < candidates[best].Id))
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }

    // 기능: E 누름으로 차량에 탄다(D6 검증: 서 있음(기절 아님), Ground·Crouch, 진행 중인 소생·재투입 없음, 타지 않음, 대상 있음).
    //   Phase 17–19 리뷰: 대상(Client 안내 VehiclePrompt와 같은 거리 규칙으로 고른 차)까지 눈에서 벽(맵 상자·닫힌 문·지형·건설 조각)이 막으면
    //   그 E는 아무것도 하지 않는다(시선 막힌 Container와 같은 정책: Client는 시선을 모르고 "[E] 타기"를 띄우며 줍기 안내를 끄므로 줍기가 일어나면 안 된다).
    //   운전석이 비었으면 운전석, 아니면 조수석. 회복과 채널을 끊고 이동 상태를 좌석의 쉬는 Ground로 바꾼다(위치 기록 재설정).
    // 입력: player - E를 누른 플레이어.
    // 출력: 탔거나 대상이 시선에 막혀 E를 소비했으면 true(호출자는 줍기를 하지 않는다). 대상이 없으면 false(호출자는 줍기로 넘어간다).
    private bool TryEnterVehicle(PlayerEntity player)
    {
        if (_vehicleCount == 0 || !player.IsUp || player.InVehicle || player.ChannelActive) return false;
        if (player.State.Mode != MovementMode.Ground && player.State.Mode != MovementMode.Crouch) return false;
        Span<VehicleCandidate> candidates = stackalloc VehicleCandidate[VehicleSettings.MaxVehicles];
        for (int i = 0; i < _vehicles.Length; i++)
        {
            Vehicle v = _vehicles[i];
            bool free = v.IsActive && (v.Seats[VehicleSettings.DriverSeat] == null || v.Seats[VehicleSettings.PassengerSeat] == null);
            candidates[i] = new VehicleCandidate(v.Id, v.Move.Position, v.Move.Heading, free);
        }
        int target = FindEnterTarget(player.State.Position, candidates);
        if (target < 0) return false;
        Vehicle vehicle = _vehicles[target];
        if (!CanReachVehicle(player, vehicle))
        {
            VehicleEntersBlocked++;
            return true;   // blocked: nothing else happens (like a blocked container, Match.Interact)
        }
        int seat = vehicle.Seats[VehicleSettings.DriverSeat] == null ? VehicleSettings.DriverSeat : VehicleSettings.PassengerSeat;
        EnterVehicle(player, vehicle, seat);
        return true;
    }

    // 기능: 플레이어 눈에서 차체(눈에 가까운 발자국 상자의 가장 가까운 점)까지 맵 상자·닫힌 문·지형·건설 조각이 막지 않는지 본다
    //   (Phase 17–19 리뷰: 닫힌 건물 안에서 벽 너머 차에 타지 않게).
    // 입력: player - 타려는 플레이어, v - Active 차량.
    // 출력: 막히지 않았으면 true.
    private bool CanReachVehicle(PlayerEntity player, Vehicle v)
    {
        Vector3 eye = player.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(player.State.Mode), 0f);
        Box front = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 0);
        Box rear = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 1);
        Box nearest = VehicleSimulation.DistanceToBox(eye, front) <= VehicleSimulation.DistanceToBox(eye, rear) ? front : rear;
        return ClearSight(eye, Vector3.Clamp(eye, nearest.Min, nearest.Max), pieces: true);
    }

    // 기능: 플레이어를 좌석에 앉힌다(검증은 호출자). 회복·채널을 끊고 좌석 위치에서 위치 기록을 다시 시작한다(D15: 좌석은 순간이동).
    //   하차 Jump 래치는 끈다(좌석에서 Jump는 제동이다).
    // 입력: player - 플레이어, vehicle - 차량, seat - 빈 좌석.
    // 출력: 반환값 없음.
    private void EnterVehicle(PlayerEntity player, Vehicle vehicle, int seat)
    {
        vehicle.Seats[seat] = player;
        player.Vehicle = vehicle;
        player.Seat = seat;
        player.VehicleInput = default;
        player.JumpLatchedFromVehicle = false;
        player.FireHeld = false;
        ConsumableRules.Cancel(player.Inventory);
        CancelChannel(player);
        Vector3 at = VehicleSimulation.SeatPosition(vehicle.Move.Position, vehicle.Move.Heading, seat);
        player.State = new MoveState
        {
            Position = at, Yaw = player.State.Yaw, Mode = MovementMode.Ground, EnergySpent = player.State.EnergySpent,
            EnergyDelayTicks = player.State.EnergyDelayTicks, Exhausted = player.State.Exhausted,
        };
        player.Sprinting = false;
        player.History.Reset(ServerTick, at, MovementMode.Ground);
        VehicleEnters++;
    }

    // 기능: 탄 사람의 E 누름으로 내린다(D6): 서 있을 수 있고 좌석에서 벽에 막히지 않은 첫 내릴 자리(운전석 쪽 옆 → 반대쪽 → 뒤 → 앞, FindExitSpot).
    //   자리가 없으면 내리지 않는다.
    // 입력: player - 탄 플레이어.
    // 출력: 내렸으면 true.
    private bool TryExitVehicle(PlayerEntity player)
    {
        Vehicle? v = player.Vehicle;
        if (v == null) return false;
        if (!FindExitSpot(v, player.Seat, out Vector3 spot)) return false;
        Unseat(player, spot);
        return true;
    }

    // 기능: 강제로 내린다(D6: 파괴, 연결 끊김, 기절·탈락, 경기 끝·리셋, 떠남). 자리가 모두 막혔으면(겹침 또는 좌석에서 가는 길이 벽에 막힘,
    //   FindExitSpot) 차량 위 ForcedExitHeight에 둔다.
    // 입력: player - 플레이어(타지 않았으면 아무것도 하지 않는다).
    // 출력: 실제로 내렸으면 true, 타고 있지 않았으면 false.
    private bool ForceExit(PlayerEntity player)
    {
        Vehicle? v = player.Vehicle;
        if (v == null) return false;
        if (!FindExitSpot(v, player.Seat, out Vector3 spot)) spot = v.Move.Position + new Vector3(0f, ForcedExitHeight, 0f);
        Unseat(player, spot);
        return true;
    }

    // 기능: 내릴 자리를 찾는다(D6 순서, 맵 안, 서 있는 상자가 맵 상자·문·채집 대상·조각·경사면에 박히지 않음). Phase 17–19 리뷰: 좌석의 몸 중심
    //   높이에서 그 자리의 몸 중심 높이까지 맵 상자·닫힌 문·지형·건설 조각이 막으면 그 자리는 건너뛴다(벽 너머로 내리지 않는다).
    // 입력: v - 차량, seat - 좌석(운전석은 왼쪽, 조수석은 오른쪽이 먼저), spot - 결과(발 위치, 지형 높이).
    // 출력: 찾았으면 true.
    private bool FindExitSpot(Vehicle v, int seat, out Vector3 spot)
    {
        float rad = v.Move.Heading * (MathF.PI / 180f);
        var right = new Vector2(MathF.Cos(rad), -MathF.Sin(rad));
        var forward = new Vector2(MathF.Sin(rad), MathF.Cos(rad));
        float side = VehicleSettings.SeatOffset(seat).X < 0f ? -1f : 1f;
        Span<Vector2> offsets = stackalloc Vector2[4];
        offsets[0] = right * (side * ExitSideDistance);
        offsets[1] = right * (-side * ExitSideDistance);
        offsets[2] = forward * -ExitEndDistance;
        offsets[3] = forward * ExitEndDistance;
        const float bound = GameMap.HalfSize - MoveSettings.HalfWidth - 0.01f;
        var bodyUp = new Vector3(0f, ExitPathHeight, 0f);
        Vector3 seatBody = VehicleSimulation.SeatPosition(v.Move.Position, v.Move.Heading, seat) + bodyUp;
        for (int i = 0; i < offsets.Length; i++)
        {
            float x = v.Move.Position.X + offsets[i].X;
            float z = v.Move.Position.Z + offsets[i].Y;
            if (x < -bound || x > bound || z < -bound || z > bound) continue;
            var feet = new Vector3(x, GameMap.Terrain.Height(x, z), z);
            if (MovementSimulation.Penetrates(feet, MoveSettings.Height, GatherAround(feet))) continue;
            // Phase 17–19 review: a free spot on the far side of a wall (a map wall, a closed door, a building piece, the terrain)
            // is not an exit: the way from the seat to it must be open.
            if (!ClearSight(seatBody, feet + bodyUp, pieces: true)) continue;
            spot = feet;
            return true;
        }
        spot = default;
        return false;
    }

    // 기능: 좌석을 비우고 플레이어를 자리에 쉬는 Ground 상태로 둔다(위치 기록 재설정: 내리기는 순간이동이다, D15). 하차 Jump 래치를 켠다
    //   (E·강제 하차 모두: 제동으로 누르던 Jump가 내린 직후 점프가 되지 않게, PlayerEntity.JumpLatchedFromVehicle).
    // 입력: player - 탄 플레이어, spot - 발 위치.
    // 출력: 반환값 없음.
    private void Unseat(PlayerEntity player, Vector3 spot)
    {
        Vehicle v = player.Vehicle!;
        if (player.Seat >= 0 && player.Seat < v.Seats.Length && v.Seats[player.Seat] == player) v.Seats[player.Seat] = null;
        player.Vehicle = null;
        player.Seat = 0;
        player.VehicleInput = default;
        player.JumpLatchedFromVehicle = true;
        player.State = new MoveState
        {
            Position = spot, Yaw = player.State.Yaw, Mode = MovementMode.Ground, EnergySpent = player.State.EnergySpent,
            EnergyDelayTicks = player.State.EnergyDelayTicks, Exhausted = player.State.Exhausted,
        };
        player.Sprinting = false;
        player.History.Reset(ServerTick, spot, MovementMode.Ground);
        VehicleExits++;
    }

    // 기능: 탄 플레이어의 한 Tick(D5, D10): 이 Tick 입력을 차량 입력으로 두고, 실제 입력의 E 누름이면 내린다(행동 금지와 상관없이).
    //   진행 중인 재장전은 계속되고 Fire 누름 상태는 입력을 따른다. 다른 행동은 하지 않는다.
    // 입력: player - 탄 플레이어, input - 이 Tick 입력, sent - 실제 입력인지, previous - 직전 실제 입력의 버튼, now - 마지막 Tick.
    // 출력: 반환값 없음.
    private void TickSeated(PlayerEntity player, in InputCommand input, bool sent, InputButtons previous, uint now)
    {
        player.VehicleInput = input;
        if (sent)
        {
            player.FireHeld = (input.Buttons & InputButtons.Fire) != 0;
            if ((PressedOnly(input.Buttons, previous) & InputButtons.Interact) != 0) TryExitVehicle(player);
        }
        WeaponRules.UpdateReload(player, now);
    }

    // 기능: 광선이 처음 맞히는 차량을 찾는다(D7: Active 차량의 두 발자국 상자, rewindTick으로 되감은 자세). 광선 시작점을 품은 상자는 건너뛴다
    //   (차 안에 서 있는 사람의 사격이 0 m에서 막히지 않게). 잔해는 막지 않는다.
    // 입력: origin·ray - 광선, range - 이보다 가까운 것만, rewindTick - 되감을 Tick(음수 = 지금 자세).
    // 출력: 맞힌 차량(없으면 null)과 거리.
    private Vehicle? TraceVehicles(Vector3 origin, Vector3 ray, float range, double rewindTick, out float distance)
    {
        distance = range;
        Vehicle? hit = null;
        if (_vehicleCount == 0) return null;
        foreach (Vehicle v in _vehicles)
        {
            if (!v.IsActive) continue;
            Vector3 position = v.Move.Position;
            float heading = v.Move.Heading;
            if (rewindTick >= 0) position = v.History.Sample(rewindTick, out heading);
            for (int axle = 0; axle < 2; axle++)
            {
                Box box = VehicleSimulation.FootprintBox(position, heading, axle);
                if (VehicleSimulation.DistanceToBox(origin, box) == 0f) continue;
                if (HitScan.IntersectAabb(origin, ray, box.Min, box.Max, distance, out float d) && d < distance)
                {
                    distance = d;
                    hit = v;
                }
            }
        }
        return hit;
    }

    // 기능: 산탄 하나의 차량 명중을 차량별 합계에 더한다.
    // 입력: v - 맞은 차량, raw - 감쇠를 곱한 원 피해.
    // 출력: 반환값 없음.
    private void AddPelletVehicle(Vehicle v, float raw)
    {
        for (int i = 0; i < _pelletVehicleCount; i++)
        {
            if (_pelletVehicles[i] != v) continue;
            _pelletVehicleDamage[i] += raw;
            return;
        }
        _pelletVehicles[_pelletVehicleCount] = v;
        _pelletVehicleDamage[_pelletVehicleCount++] = raw;
    }

    // 기능: 폭발의 차량 피해(D7): Active 차량마다 두 발자국 상자 중 가까운 것까지 거리로 감쇠 피해, 폭발점 → 그 상자 중심 시선이 맵·문·지형·조각에
    //   막히지 않을 때만. 처치 기록은 살아 있는 주인.
    // 입력: p - 터진 투사체, position - 폭발 위치.
    // 출력: 피해를 받은 차량 수.
    private int ExplodeVehicles(in Projectile p, Vector3 position)
    {
        ProjectileDefinition definition = p.Definition;
        if (_vehicleCount == 0 || definition.ExplosionDamage == 0) return 0;
        PlayerEntity? owner = FindLivingByJoinOrder(p.OwnerJoinOrder);
        int hits = 0;
        foreach (Vehicle v in _vehicles)
        {
            if (!v.IsActive) continue;
            Box front = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 0);
            Box rear = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 1);
            float df = VehicleSimulation.DistanceToBox(position, front);
            float dr = VehicleSimulation.DistanceToBox(position, rear);
            Box nearest = df <= dr ? front : rear;
            ushort damage = CombatRules.ExplosionDamage(definition.ExplosionDamage, MathF.Min(df, dr), definition.ExplosionRadius, p.DamageMultiplier);
            if (damage == 0 || ExplosionBlocked(position, nearest.Center)) continue;
            DamageVehicle(v, damage, owner);
            hits++;
        }
        return hits;
    }

    // 기능: 조각이 차량 발자국과 겹치는지 본다(D8: 차량이 조각 안에 갇히지 않게 설치를 거절한다).
    // 입력: shape - 놓을 조각.
    // 출력: 쓰는 중인 차량(잔해 포함)의 지금 발자국과 겹치면 true.
    private bool CutsAVehicle(in BuildPieceShape shape)
    {
        if (_vehicleCount == 0) return false;
        Box bounds = BuildGrid.BoundsOf(shape);
        foreach (Vehicle v in _vehicles)
        {
            if (!v.InUse) continue;
            if (VehicleSimulation.Touches(bounds, VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 0)) ||
                VehicleSimulation.Touches(bounds, VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 1)))
                return true;
        }
        return false;
    }

    // 기능: 편집으로 새로 막히는 부분이 쓰는 중인 차량 발자국과 겹치는지 본다(Phase 19 리뷰: 열린 칸을 다시 채워 차를 가두지 않게).
    //   벽·바닥: 다시 채워지는 칸들만(뚫기만 하면 false). 그 밖(Ramp 회전, 지붕 편집): 편집 뒤 모양 전체(경사면은 경계 상자).
    // 입력: before - 지금 모양, after - 편집 뒤 모양.
    // 출력: 겹치면 true.
    private bool EditCutsAVehicle(in BuildPieceShape before, in BuildPieceShape after)
    {
        if (_vehicleCount == 0) return false;
        Span<Box> parts = stackalloc Box[BuildGrid.MaxPartsPerPiece];
        int count;
        if (after.Type == BuildPieceType.Wall || after.Type == BuildPieceType.Floor)
        {
            int tileMask = (1 << BuildEdit.TileCount(after.Type)) - 1;
            int refilled = before.Edit & ~after.Edit & tileMask;
            if (refilled == 0) return false;
            var newPart = new BuildPieceShape(after.Type, after.X, after.Y, after.Z, after.Rotation, ~refilled & tileMask);
            count = BuildGrid.PartsOf(newPart, parts, out _);
        }
        else
        {
            parts[0] = BuildGrid.BoundsOf(after);
            count = 1;
        }
        foreach (Vehicle v in _vehicles)
        {
            if (!v.InUse) continue;
            Box front = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 0);
            Box rear = VehicleSimulation.FootprintBox(v.Move.Position, v.Move.Heading, 1);
            for (int i = 0; i < count; i++)
            {
                if (VehicleSimulation.Touches(parts[i], front) || VehicleSimulation.Touches(parts[i], rear)) return true;
            }
        }
        return false;
    }

    // 기능: 플레이어 몸 상자가 차량의 (되감은) 발자국과 겹치는지 본다(사격에서 차 발자국 안에 선 사람을 차가 가리지 않게).
    // 입력: v - 차량, rewindTick - 되감을 Tick(음수 = 지금 자세), feet - 몸의 발 위치, height - 몸 높이.
    // 출력: 겹치면 true.
    private static bool BodyTouchesVehicle(Vehicle v, double rewindTick, Vector3 feet, float height)
    {
        Vector3 position = v.Move.Position;
        float heading = v.Move.Heading;
        if (rewindTick >= 0) position = v.History.Sample(rewindTick, out heading);
        var body = new Box(new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth),
            new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth));
        return VehicleSimulation.Touches(body, VehicleSimulation.FootprintBox(position, heading, 0)) ||
               VehicleSimulation.Touches(body, VehicleSimulation.FootprintBox(position, heading, 1));
    }

    // 기능: Tick 끝에 쓰는 중인 차량의 자세를 위치 기록에 남긴다(플레이어 기록과 같은 Tick).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void RecordVehicles()
    {
        if (_vehicleCount == 0) return;
        foreach (Vehicle v in _vehicles)
        {
            if (v.InUse) v.History.Record(ServerTick, v.Move.Position, v.Move.Heading);
        }
    }

    // 기능: 차량을 VehicleStates 기록으로 만든다.
    // 입력: v - 쓰는 중인 차량.
    // 출력: VehicleRecord.
    private static VehicleRecord RecordOf(Vehicle v) => new()
    {
        Id = v.Id,
        State = v.State,
        Driver = v.Seats[VehicleSettings.DriverSeat]?.EntityId ?? 0,
        Passenger = v.Seats[VehicleSettings.PassengerSeat]?.EntityId ?? 0,
        Position = v.Move.Position,
        Heading = v.Move.Heading,
        Speed = v.Move.Speed,
        Steer = v.Move.Steer,
        Health = (ushort)Math.Clamp(v.Health, 0, ushort.MaxValue),
    };

    // 기능: Snapshot과 같은 Tick에 연결된 플레이어마다 VehicleStates를 보낸다(D4, Unreliable 채널 0). 받는 사람 몸에서 interestRange 안의 차량과
    //   자기가 탄 차량만, 죽은 사람·관전자는 모두. 경기에 차량이 없으면 보내지 않는다(마지막 차량이 사라진 뒤 첫 번째는 빈 목록으로 한 번 보낸다).
    //   기록은 한 번 만들고 받는 사람마다 골라 쓴다(할당 없음).
    // 입력: 없음.
    // 출력: 반환값 없음. Client들에게 VehicleStates가 전송된다.
    private void SendVehicleStates()
    {
        if (_vehicleCount == 0 && !_vehicleStatesEmptyPending) return;
        _vehicleStatesEmptyPending = false;
        int total = 0;
        foreach (Vehicle v in _vehicles)
        {
            if (!v.InUse) continue;
            _vehicleRecords[total] = RecordOf(v);
            _vehicleRecordOwners[total++] = v;
        }
        float rangeSq = _vehicleData.InterestRange * _vehicleData.InterestRange;
        foreach (var p in _players)
        {
            if (p.IsGraced) continue;
            int count = 0;
            for (int i = 0; i < total; i++)
            {
                if (SeesVehicle(p, _vehicleRecordOwners[i]!, rangeSq)) count++;
            }
            var writer = new PacketWriter(_sendBuffer);
            VehicleStatesPacket.WriteHeader(ref writer, ServerTick, p.LastProcessedSeq, count);
            for (int i = 0; i < total; i++)
            {
                if (SeesVehicle(p, _vehicleRecordOwners[i]!, rangeSq)) VehicleRecord.Write(ref writer, _vehicleRecords[i]);
            }
            _send(p.PeerId, writer.WrittenSpan, DeliveryMethod.Unreliable);
            VehicleStatesSent++;
        }
        Array.Clear(_vehicleRecordOwners);   // no reference kept past the send
    }

    // 기능: 받는 사람이 이 차량을 VehicleStates로 받는지 본다(D4 관심 영역).
    // 입력: p - 받는 사람, v - 차량, rangeSq - 관심 거리 제곱.
    // 출력: 받으면 true.
    private static bool SeesVehicle(PlayerEntity p, Vehicle v, float rangeSq) =>
        !p.Alive || p.Vehicle == v || Vector3.DistanceSquared(p.State.Position, v.Move.Position) <= rangeSq;

    // 기능: QA setPosition: 탄 사람을 강제로 내린다(순간이동 전에).
    // 입력: p - 플레이어.
    // 출력: 반환값 없음.
    internal void QaUnseat(PlayerEntity p) => ForceExit(p);

    // 기능: QA spawnVehicle: 이 자세의 발자국이 맵 상자·닫힌 문·서 있는 채집 대상·건설 조각(경사면 경계 포함)과 겹치는지 본다
    //   (겹친 채 생기면 시작 겹침 무시로 통과한다). Step과 같은 수집(GatherAround)을 쓴다.
    // 입력: x·z - 중심, heading - 방향(도).
    // 출력: 겹치면 true.
    internal bool QaVehicleBlocked(float x, float z, float heading)
    {
        float h = VehicleSimulation.NormalizeHeading(heading);
        var position = new Vector3(x, VehicleSimulation.GroundHeight(x, z, h, GameMap.Terrain), z);
        Box front = VehicleSimulation.FootprintBox(position, h, 0);
        Box rear = VehicleSimulation.FootprintBox(position, h, 1);
        CollisionWorld world = GatherAround(position);
        foreach (Box box in world.Boxes)
        {
            if (VehicleSimulation.Touches(front, box) || VehicleSimulation.Touches(rear, box)) return true;
        }
        foreach (Slope slope in world.Slopes)
        {
            Box bounds = VehicleSimulation.BoundsOf(slope);
            if (VehicleSimulation.Touches(front, bounds) || VehicleSimulation.Touches(rear, bounds)) return true;
        }
        return false;
    }

    // 기능: QA damageVehicle: 공격자 없는 피해(충격 피해와 같은 경로, 0이면 잔해).
    // 입력: v - 차량, amount - 피해.
    // 출력: Active였으면 true.
    internal bool QaDamageVehicle(Vehicle v, int amount)
    {
        if (!v.IsActive) return false;
        DamageVehicle(v, amount, null);
        return true;
    }
}

// Phase 19 D6: one vehicle as the enter rule sees it (the server's own slots, or a client's decoded records in the comparison test).
internal readonly record struct VehicleCandidate(byte Id, Vector3 Position, float Heading, bool Enterable);
