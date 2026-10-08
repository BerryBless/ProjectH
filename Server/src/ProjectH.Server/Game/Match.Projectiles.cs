using System;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Vehicles;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 17 D6-D10: grenades, rockets and their explosions (Docs/Weapons.md). Same rules as the rest of Match: game loop
// thread only, no lock, no allocation per tick (the projectiles live in a fixed array, the explosion's piece list and the
// QA explosion log are fixed buffers made once).
public sealed partial class Match
{
    // D8: the build columns one explosion of at most MaxExplosionRadius visits (5 m cells: 2 x 10 / 5 + 1 cells across, plus
    // one neighbour column on each side for walls standing across an edge), times the pieces one column can hold.
    private const int ExplosionColumnsAcross = (int)(2 * WeaponCatalog.MaxExplosionRadius / BuildGrid.CellSize) + 3;
    private const int MaxExplosionPieces = ExplosionColumnsAcross * ExplosionColumnsAcross * BuildGrid.Levels * BuildGrid.SlotKinds;
    // A target's body centre this much nearer than the blocker still counts as seen (the blocker is the target itself).
    private const float ExplosionSightTolerance = 0.05f;
    // QA (D17): the last explosions, oldest overwritten first. Fixed size; read only by QA observation.
    public const int ExplosionLogSize = 16;

    // Phase 17–19 review: how far below a resting grenade its support is looked for. A rest is SurfaceOffset along a normal
    // with Y >= RestNormalY above the face (at most 0.05 / 0.7 = 0.072 m straight up), so this always reaches a face still there.
    private const float RestSupportReach = 0.15f;

    private readonly ProjectileSet _projectiles = new();
    // What the resting grenades' support was last checked against: pieces destroyed or edited (BuildReplication.StructureVersion,
    // which only grows; a placement or damage does not move it), doors and harvestables. Only a change of one of them can take
    // a support away (the terrain and map boxes never change).
    private uint _restCheckBuildVersion;
    private byte _restCheckDoors;
    private ulong _restCheckHarvest;
    private readonly uint[] _explosionPieces = new uint[MaxExplosionPieces];
    private readonly ExplosionRecord[] _explosionLog = new ExplosionRecord[ExplosionLogSize];
    private int _explosionLogNext;

    // Phase 17 counters since this match object was made (QA and the Health line).
    public long ProjectilesLaunched { get; private set; }
    public long ProjectilesRefused { get; private set; }
    public long Explosions { get; private set; }
    public long GrenadesThrown { get; private set; }
    // Phase 17–19 review: resting grenades' support traces done (test seam: they run only after a structural change).
    internal long RestSupportChecks { get; private set; }

    // Test and QA seams: the live projectiles and the explosion log (Match is the only writer).
    internal ProjectileSet Projectiles => _projectiles;
    internal ReadOnlySpan<ExplosionRecord> ExplosionLog => _explosionLog;
    // The total explosions logged; with ExplosionLogSize it tells which entries are filled and which is the newest.
    internal long ExplosionLogCount => Explosions;

    // 기능: 지금 경기 상태에서 투사체를 새로 만들 수 있는지 본다(D6: 결과 화면 Finished·Closing에서는 만들지 않는다). 시작 카운트다운
    //   Starting에서도 만들지 않는다: 서버와 Client가 모두 Starting에 들어가는 Tick에 투사체를 비우므로(Tick, Client는 MatchState로), 그 뒤에 생긴
    //   것은 StartMatch가 서버에서만 말없이 지워 Client에 남을 수 있다. 대기실(WaitingForPlayers)·경기 중·개발 모드는 만든다.
    // 입력: 없음.
    // 출력: 만들 수 있으면 true.
    private bool ProjectilesAllowed =>
        _flow.State != MatchFlowState.Starting && _flow.State != MatchFlowState.Finished && _flow.State != MatchFlowState.Closing;

    // 기능: 손에 든 무기가 이번 입력에 쏠 수 있는지 본다(D6: 투사체 무기는 빈 칸과 경기 상태를 탄 소비 전에 확인한다. 리뷰 수정 C5:
    //   주인 개인 상한도).
    // 입력: owner - 쏘는 사람, weapon - 현재 칸의 무기(null = 빈 칸).
    // 출력: Hitscan 무기·빈 칸이면 true, 투사체 무기는 ProjectilesAllowed가 true이고(시작 카운트다운·결과 화면 아님) HasRoom(owner)이면 true.
    private bool CanLaunch(PlayerEntity owner, WeaponDefinition? weapon)
    {
        if (weapon == null || !weapon.IsProjectile) return true;
        return ProjectilesAllowed && HasRoom(owner);
    }

    // 기능: 이 사람의 투사체가 하나 더 들어갈 자리가 있는지 본다: 빈 칸이 있고(32칸 공유) 그 사람의 살아 있는 투사체가 MaxPerOwner보다 적음(리뷰 수정 C5).
    // 입력: owner - 주인.
    // 출력: 들어가면 true.
    private bool HasRoom(PlayerEntity owner) =>
        _projectiles.HasFree && _projectiles.CountOwnedBy(owner.JoinOrder) < ProjectileRules.MaxPerOwner;

    // 기능: 수류탄을 던진다(D9). 행동 가능 모드는 호출자(ProcessActions)가 보장한다. 조건: 조준 유효, ProjectilesAllowed가 true(시작 카운트다운·결과 화면 아님), 수류탄 정의 있음,
    //   소지 ≥ 1, 간격이 지남, 빈 투사체 칸. 칸을 먼저 확보한 뒤 수를 줄이고, 진행 중인 회복·소생·재투입을 끊는다.
    // 입력: player - 던지는 사람, aimValid·aim - 조준(단위 방향), now - 마지막으로 끝난 Tick.
    // 출력: 던졌으면 true.
    private bool TryThrowGrenade(PlayerEntity player, bool aimValid, Vector3 aim, uint now)
    {
        ProjectileDefinition? grenade = _weapons.Projectile(ProjectileKind.Grenade);
        if (!aimValid || grenade == null || !ProjectilesAllowed) return false;
        Inventory inventory = player.Inventory;
        if (inventory.Grenades < 1 || now < player.NextGrenadeTick) return false;

        Vector3 direction = PitchUp(aim, grenade.ThrowUpDegrees);
        Vector3 origin = player.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(player.State.Mode), 0f);
        if (!SpawnProjectile(player, grenade, origin, direction * grenade.Speed, 1f)) return false;

        inventory.Grenades--;
        inventory.Changed = true;
        player.NextGrenadeTick = now + grenade.ThrowIntervalTicks;
        GrenadesThrown++;
        ConsumableRules.Cancel(inventory);   // D9: a throw interrupts a heal
        CancelChannel(player);               // and a revive or a reboot (Phase 14 D8: another action)
        return true;
    }

    // 기능: 조준 방향을 위로 몇 도 올린다(수평 방향은 그대로, 수직이면 그대로).
    // 입력: aim - 단위 방향, degrees - 올릴 각도(음수 = 내림).
    // 출력: 단위 방향.
    private static Vector3 PitchUp(Vector3 aim, float degrees)
    {
        var flat = new Vector2(aim.X, aim.Z);
        float horizontal = flat.Length();
        if (degrees == 0f || horizontal < 1e-5f) return aim;
        float pitch = MathF.Atan2(aim.Y, horizontal) + degrees * (MathF.PI / 180f);
        pitch = Math.Clamp(pitch, -CombatRules.MaxPitch * (MathF.PI / 180f), CombatRules.MaxPitch * (MathF.PI / 180f));
        flat /= horizontal;
        float cos = MathF.Cos(pitch);
        return new Vector3(flat.X * cos, MathF.Sin(pitch), flat.Y * cos);
    }

    // 기능: 투사체 무기를 쏜다(D10): 눈에서 조준 방향으로 정의 속도. 칸은 CanLaunch가 탄 소비 전에 확인했다.
    // 입력: shooter - 쏜 사람, weapon - 투사체 무기, origin - 눈 위치, aim - 조준 방향, multiplier - 무기 등급 배율.
    // 출력: 반환값 없음. 투사체가 생기고 ProjectileSpawned가 방송된다.
    private void LaunchProjectile(PlayerEntity shooter, WeaponDefinition weapon, Vector3 origin, Vector3 aim, float multiplier)
    {
        ProjectileDefinition definition = weapon.Projectile!;
        SpawnProjectile(shooter, definition, origin, aim * definition.Speed, multiplier);
    }

    // 기능: 투사체 하나를 만든다. 주인의 Entity id·JoinOrder·팀을 저장하고 모두에게 ProjectileSpawned를 보낸다.
    //   StartTick = 시뮬레이션 중인 Tick이고, 첫 이동은 다음 Tick의 UpdateProjectiles에서 한다.
    // 입력: owner - 주인, definition - 정의, origin - 시작 위치, velocity - 시작 속도, multiplier - 폭발 피해 배율(등급).
    // 출력: 만들었으면 true, 칸이 없거나 주인의 개인 상한(리뷰 수정 C5)이면 false.
    private bool SpawnProjectile(PlayerEntity owner, ProjectileDefinition definition, Vector3 origin, Vector3 velocity, float multiplier)
    {
        if (!HasRoom(owner))
        {
            ProjectilesRefused++;
            return false;
        }
        uint tick = ServerTick + 1;
        var projectile = new Projectile
        {
            Definition = definition,
            Position = origin,
            Velocity = velocity,
            ExplodeTick = tick + definition.LifetimeTicks,
            OwnerEntityId = owner.EntityId,
            OwnerJoinOrder = owner.JoinOrder,
            OwnerTeam = owner.TeamId,
            DamageMultiplier = multiplier,
        };
        if (!_projectiles.TryAdd(projectile, out int slot))
        {
            ProjectilesRefused++;
            return false;
        }
        ProjectilesLaunched++;
        var writer = new PacketWriter(_sendBuffer);
        ProjectileSpawned.Write(ref writer, SpawnedOf(_projectiles[slot], tick));
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        return true;
    }

    // 기능: 투사체의 지금 상태를 ProjectileSpawned 값으로 만든다. 주인이 경기에 없으면(JoinOrder로 찾지 못하면) OwnerId 0
    //   (나간 주인의 Entity id는 다른 사람에게 다시 쓰일 수 있다).
    // 입력: p - 투사체, tick - Position·Velocity가 맞는 Tick.
    // 출력: ProjectileSpawned 값.
    private ProjectileSpawned SpawnedOf(in Projectile p, uint tick) => new()
    {
        Id = p.Id,
        Kind = p.Definition.Kind,
        OwnerId = FindByJoinOrder(p.OwnerJoinOrder) != null ? p.OwnerEntityId : (ushort)0,
        Position = p.Position,
        Velocity = p.Resting ? Vector3.Zero : p.Velocity,
        StartTick = tick,
    };

    // 기능: 입장·Resume한 사람에게 살아 있는 투사체를 ProjectileSpawned로 다시 보낸다(D7). 상태는 마지막으로 끝난 Tick의 것이다.
    // 입력: peerId - 받는 연결.
    // 출력: 반환값 없음.
    private void SendProjectilesTo(int peerId)
    {
        if (_projectiles.Count == 0) return;
        for (int i = 0; i < ProjectileSet.Capacity; i++)
        {
            ref Projectile p = ref _projectiles[i];
            if (!p.Active) continue;
            var writer = new PacketWriter(_sendBuffer);
            ProjectileSpawned.Write(ref writer, SpawnedOf(p, ServerTick));
            _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }
    }

    // 기능: 모든 투사체를 아무것도 보내지 않고 지운다(D6: 시작 카운트다운 진입·경기 시작·라운드 리셋·경기 끝. Client는 MatchState가
    //   Waiting·Starting·Finished로 바뀌면 비운다).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void ClearProjectiles() => _projectiles.Clear();

    // 기능: 한 Tick의 투사체 갱신(D6): 퓨즈·수명이 끝난 것은 폭발, 나머지는 정확한 등가속 적분 후 이번 이동 선분을 맵 상자·닫힌 문·
    //   서 있는 채집 대상·지형(+바닥면), 건설 조각, (로켓만) 플레이어 현재 위치(Phase 19: 탄 사람 제외)와 Active 차량 발자국과 판정한다. 로켓은 맞으면 폭발, 수류탄은 맞은 면 법선으로
    //   튕기고(위를 향한 면(법선 Y ≥ RestNormalY)에서 속도 < RestSpeed면 정지) ProjectileState를 보낸다. 이번 Tick에 생긴 투사체는 다음 Tick부터 움직인다(Tick 앞에서 부른다).
    //   Phase 17–19 리뷰: 지난 호출 뒤 조각이 부서지거나 편집됐거나(StructureVersion, 설치·피해만으로는 오르지 않는다) 문이 열리고 닫혔거나
    //   채집 대상 상태가 바뀌었을 때만 멈춘 수류탄 아래 받침을 다시 보고, 받침이 사라졌으면 멈춤을 풀어 이번 Tick부터 다시 떨어뜨린다
    //   (ProjectileState로 알린다). 이 함수 뒤에 생긴 변화(폭발·사격·붕괴로 인한 파괴)는 다음 Tick에 본다.
    // 입력: now - 마지막으로 끝난 Tick(이번에 now + 1을 시뮬레이션한다).
    // 출력: 반환값 없음. 투사체가 움직이거나 폭발하고 사건이 방송된다.
    private void UpdateProjectiles(uint now)
    {
        if (_projectiles.Count == 0) return;
        uint tick = now + 1;
        bool supportChanged = _restCheckBuildVersion != _replication.StructureVersion || _restCheckDoors != _doors.OpenMask ||
                              _restCheckHarvest != _harvest.DestroyedMask;
        _restCheckBuildVersion = _replication.StructureVersion;
        _restCheckDoors = _doors.OpenMask;
        _restCheckHarvest = _harvest.DestroyedMask;
        for (int i = 0; i < ProjectileSet.Capacity; i++)
        {
            ref Projectile p = ref _projectiles[i];
            if (!p.Active) continue;
            if (tick >= p.ExplodeTick)
            {
                Explode(i, p.Position, tick);
                continue;
            }
            if (p.Resting)
            {
                if (!supportChanged || RestSupported(p.Position)) continue;
                p.Resting = false;   // its support is gone: it falls from rest (Velocity is zero)
                if (!StepProjectile(i, tick)) SendProjectileState(i, tick);
                continue;
            }
            StepProjectile(i, tick);
        }
    }

    // 기능: 멈춘 수류탄 바로 아래(RestSupportReach)에 받침(맵 상자·닫힌 문·서 있는 채집 대상·지형·바닥면·건설 조각)이 있는지 본다.
    // 입력: position - 멈춘 위치.
    // 출력: 받침이 있으면 true. RestSupportChecks가 하나 는다.
    private bool RestSupported(Vector3 position)
    {
        RestSupportChecks++;
        var down = new Vector3(0f, -1f, 0f);
        if (HitScan.TraceWorld(position, down, RestSupportReach, Blockers, GameMap.Terrain) < RestSupportReach) return true;
        return PieceTrace.Trace(position, down, RestSupportReach, _build, out _, out _, out _);
    }

    // 기능: 투사체의 지금 위치·속도를 ProjectileState로 모두에게 보낸다(Velocity 0 = 멈춤으로 Client가 외삽하지 않는다).
    // 입력: slot - 투사체 칸, tick - 위치·속도가 맞는 Tick.
    // 출력: 반환값 없음. ProjectileState가 방송된다.
    private void SendProjectileState(int slot, uint tick)
    {
        ref Projectile p = ref _projectiles[slot];
        var writer = new PacketWriter(_sendBuffer);
        ProjectileState.Write(ref writer, new ProjectileState { Id = p.Id, Position = p.Position, Velocity = p.Velocity, Tick = tick });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 투사체 하나를 한 Tick 움직이고 이동 선분의 첫 충돌을 처리한다.
    // 입력: slot - 투사체 칸, tick - 시뮬레이션 중인 Tick.
    // 출력: 충돌로 폭발했거나 튕겨 ProjectileState를 보냈으면 true, 막힘 없이 움직였으면 false.
    private bool StepProjectile(int slot, uint tick)
    {
        ref Projectile p = ref _projectiles[slot];
        ProjectileDefinition definition = p.Definition;
        Vector3 from = p.Position;
        Vector3 startVelocity = p.Velocity;
        Vector3 to = from;
        Vector3 endVelocity = startVelocity;
        ProjectileRules.Advance(ref to, ref endVelocity, definition.Gravity, _tickSeconds);
        Vector3 segment = to - from;
        float length = segment.Length();
        if (!(length > 1e-6f))
        {
            p.Position = to;
            p.Velocity = endVelocity;
            return false;
        }
        Vector3 direction = segment / length;

        float nearest = ProjectileRules.TraceWorld(from, direction, length, Blockers, GameMap.Terrain, out Vector3 normal);
        bool hit = nearest < length;
        // Like a shot (final review B7): a piece level with what the world trace met is hit first.
        if (PieceTrace.Trace(from, direction, nearest + PieceTieTolerance, _build, out _, out int pieceSlot, out float pieceDistance) &&
            pieceDistance <= length)
        {
            nearest = pieceDistance;
            normal = ProjectileRules.PieceNormal(_build.At(pieceSlot).Shape, from, direction, pieceDistance);
            hit = true;
        }
        // D6: only a rocket meets players, where they are now (no rewind); its owner and the owner's team are passed through.
        // Phase 19 D7: and the vehicles (any vehicle: it hits the car its target sits in); a seated player is not hit itself.
        if (definition.ExplodesOnImpact)
        {
            if (TraceVehicles(from, direction, hit ? nearest : length, -1, out float vehicleDistance) != null)
            {
                nearest = vehicleDistance;
                normal = -direction;
                hit = true;
            }
            foreach (var other in _players)
            {
                if (!other.Alive || other.InVehicle || other.State.Mode == MovementMode.Transport || IsOwnerOrTeammate(p, other)) continue;
                if (HitScan.TracePlayer(from, direction, nearest, other.State.Position, MovementSimulation.CollisionHeight(other.State.Mode),
                        out float distance) && (!hit || distance < nearest))
                {
                    nearest = distance;
                    normal = -direction;
                    hit = true;
                }
            }
        }

        if (!hit)
        {
            p.Position = to;
            p.Velocity = endVelocity;
            return false;
        }

        Vector3 impact = from + direction * nearest + normal * ProjectileRules.SurfaceOffset;
        if (definition.ExplodesOnImpact)
        {
            Explode(slot, impact, tick);
            return true;
        }

        // A grenade bounces off the face it met: its speed there (linear in time over the tick), reflected and damped.
        Vector3 impactVelocity = Vector3.Lerp(startVelocity, endVelocity, nearest / length);
        Vector3 bounced = ProjectileRules.Reflect(impactVelocity, normal, definition.Bounce);
        p.Position = impact;
        // Review fix: only a floor or a gentle slope (normal facing up) can hold it; off a wall or a ceiling a slow grenade keeps
        // its (small) bounce and gravity takes it down.
        if (ProjectileRules.CanRest(normal, bounced))
        {
            p.Resting = true;
            p.Velocity = Vector3.Zero;
        }
        else
        {
            p.Velocity = bounced;
        }
        SendProjectileState(slot, tick);
        return true;
    }

    // 기능: 대상이 투사체의 주인이거나 생성 때 주인의 팀원인지 본다(D8: 자폭·아군 피해 없음, 로켓은 그들을 지나간다).
    // 입력: p - 투사체, target - 대상.
    // 출력: 주인 또는 팀원이면 true.
    private static bool IsOwnerOrTeammate(in Projectile p, PlayerEntity target) =>
        target.JoinOrder == p.OwnerJoinOrder || (p.OwnerTeam != 0 && target.TeamId == p.OwnerTeam);

    // 기능: 투사체를 터뜨린다(D8): 칸을 먼저 비우고 모두에게 ProjectileExploded를 보낸 뒤, 피해가 허용될 때만 플레이어(시선 검사)와
    //   조각(시선 없이 경계까지 거리), (Phase 19) 차량(시선 검사)에 감쇠 피해를 준다. 처치 = 아직 경기에 살아 있는 주인, 없으면 처치 없음.
    // 입력: slot - 투사체 칸, position - 폭발 위치, tick - 시뮬레이션 중인 Tick.
    // 출력: 반환값 없음.
    private void Explode(int slot, Vector3 position, uint tick)
    {
        Projectile p = _projectiles[slot];
        _projectiles.Remove(slot);
        Explosions++;

        var writer = new PacketWriter(_sendBuffer);
        ProjectileExploded.Write(ref writer, new ProjectileExploded { Id = p.Id, Position = position, Kind = p.Definition.Kind });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        int players = 0;
        int pieces = 0;
        if (_flow.DamageAllowed)
        {
            players = ExplodePlayers(p, position);
            pieces = ExplodePieces(p.Definition, position);
            ExplodeVehicles(p, position);   // Phase 19 D7
        }
        _explosionLog[_explosionLogNext] = new ExplosionRecord(p.Id, p.Definition.Kind, position, tick, p.OwnerEntityId, players, pieces);
        _explosionLogNext = (_explosionLogNext + 1) % ExplosionLogSize;
    }

    // 기능: 폭발의 플레이어 피해(D8): 반지름 안(몸 상자까지 거리), 주인·주인 팀이 아님, (Phase 19) 차량에 타지 않음, 폭발점 → 몸 중심 시선이
    //   맵·문·지형·조각에 막히지 않음. 피해 = 중심 피해 × 선형 감쇠 × 등급 배율.
    // 입력: p - 터진 투사체(칸은 이미 비었다), position - 폭발 위치.
    // 출력: 피해를 받은 플레이어 수.
    private int ExplodePlayers(in Projectile p, Vector3 position)
    {
        ProjectileDefinition definition = p.Definition;
        if (definition.ExplosionDamage == 0) return 0;
        PlayerEntity? owner = FindLivingByJoinOrder(p.OwnerJoinOrder);
        int hits = 0;
        foreach (var target in _players)
        {
            if (!target.Alive || target.InVehicle || target.State.Mode == MovementMode.Transport || IsOwnerOrTeammate(p, target)) continue;
            Vector3 feet = target.State.Position;
            float height = MovementSimulation.CollisionHeight(target.State.Mode);
            var min = new Vector3(feet.X - MoveSettings.HalfWidth, feet.Y, feet.Z - MoveSettings.HalfWidth);
            var max = new Vector3(feet.X + MoveSettings.HalfWidth, feet.Y + height, feet.Z + MoveSettings.HalfWidth);
            float distance = CombatRules.DistanceToBox(position, min, max);
            ushort damage = CombatRules.ExplosionDamage(definition.ExplosionDamage, distance, definition.ExplosionRadius, p.DamageMultiplier);
            if (damage == 0) continue;
            Vector3 centre = feet + new Vector3(0f, height * 0.5f, 0f);
            if (ExplosionBlocked(position, centre)) continue;
            ApplyExplosionHit(owner, target, damage, position);
            hits++;
        }
        return hits;
    }

    // 기능: 폭발점에서 몸 중심까지의 시선이 막혔는지 본다(맵 상자·닫힌 문·서 있는 채집 대상·지형·조각).
    // 입력: from - 폭발점, to - 몸 중심.
    // 출력: 막혔으면 true.
    private bool ExplosionBlocked(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float distance = delta.Length();
        if (distance < 1e-4f) return false;
        Vector3 direction = delta / distance;
        float reach = distance - ExplosionSightTolerance;
        if (reach <= 0f) return false;
        if (HitScan.TraceWorld(from, direction, reach, Blockers, GameMap.Terrain) < reach) return true;
        return PieceTrace.Trace(from, direction, reach, _build, out _, out _, out _);
    }

    // 기능: 폭발의 조각 피해(D8): 폭발이 닿는 칸들의 조각마다(시선 검사 없음) 조각 경계 상자까지 거리로 구조물 피해 × 감쇠 × 재료 배율.
    //   먼저 id를 고정 버퍼에 모은 뒤 피해를 준다(부서진 조각이 칸 목록을 바꾸므로). 붕괴는 Tick 끝 CollapseUnsupported가 한 번 찾는다.
    // 입력: definition - 투사체 정의, position - 폭발 위치.
    // 출력: 피해를 받은 조각 수.
    private int ExplodePieces(ProjectileDefinition definition, Vector3 position)
    {
        if (definition.StructureDamage == 0 || _build.Count == 0) return 0;
        float radius = definition.ExplosionRadius;
        PieceGrid grid = _build.Grid;
        int x0 = Math.Max(0, (int)MathF.Floor((position.X - radius - BuildGrid.OriginX) / BuildGrid.CellSize) - 1);
        int x1 = Math.Min(BuildGrid.CellsX - 1, (int)MathF.Floor((position.X + radius - BuildGrid.OriginX) / BuildGrid.CellSize) + 1);
        int z0 = Math.Max(0, (int)MathF.Floor((position.Z - radius - BuildGrid.OriginZ) / BuildGrid.CellSize) - 1);
        int z1 = Math.Min(BuildGrid.CellsZ - 1, (int)MathF.Floor((position.Z + radius - BuildGrid.OriginZ) / BuildGrid.CellSize) + 1);
        int count = 0;
        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int s = grid.First(x, z); s >= 0 && count < _explosionPieces.Length; s = grid.Next(s))
                {
                    Box bounds = BuildGrid.BoundsOf(grid.ShapeAt(s));
                    if (CombatRules.DistanceToBox(position, bounds.Min, bounds.Max) < radius) _explosionPieces[count++] = grid.IdAt(s);
                }
            }
        }
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            if (!_build.TryGetSlot(_explosionPieces[i], out int slot)) continue;
            ref BuildPiece piece = ref _build.At(slot);
            Box bounds = BuildGrid.BoundsOf(piece.Shape);
            float damage = CombatRules.ExplosionStructureDamage(definition.StructureDamage, CombatRules.DistanceToBox(position, bounds.Min, bounds.Max),
                radius, _building.Material(piece.Material).StructureDamageMultiplier);
            if (damage <= 0f) continue;
            DamagePiece(slot, damage);
            hits++;
        }
        return hits;
    }

    // 기능: 이 JoinOrder의 살아 있는 플레이어를 찾는다(투사체 주인의 처치 기록. 나갔거나 죽었으면 없다).
    // 입력: joinOrder - 주인의 JoinOrder.
    // 출력: 플레이어 또는 null.
    private PlayerEntity? FindLivingByJoinOrder(uint joinOrder)
    {
        foreach (var p in _players)
        {
            if (p.JoinOrder == joinOrder) return p.Alive ? p : null;
        }
        return null;
    }

    // 기능: 폭발 명중을 반영한다: 피해, (주인이 있으면) 처치 기여와 HitConfirmed, 대상의 DamageTaken(방향 = 대상 → 폭발점), 진행 끊기,
    //   치명이면 치명 경로 하나(원인 Explosion, 처치 = 주인). Phase 18 D8: DamageTaken에 실드 맞음·깨짐 플래그.
    // 입력: owner - 살아 있는 주인(null = 없음), target - 대상, damage - 피해, blast - 폭발 위치.
    // 출력: 반환값 없음.
    private void ApplyExplosionHit(PlayerEntity? owner, PlayerEntity target, ushort damage, Vector3 blast)
    {
        int before = target.Health + target.Shield;
        int shieldBefore = target.Shield;
        bool fatal = CombatRules.ApplyDamage(ref target.Health, ref target.Shield, damage);
        if (owner != null && _flow.InMatch && owner.Participant) owner.DamageDealt += before - (target.Health + target.Shield);
        bool killed = fatal && (target.IsDowned || !CanBeDowned(target));

        PacketWriter writer;
        if (owner != null)
        {
            writer = new PacketWriter(_sendBuffer);
            HitConfirmed.Write(ref writer, new HitConfirmed { TargetId = target.EntityId, Damage = damage, Killed = killed });
            _send(owner.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }

        Vector3 toBlast = blast - (target.State.Position + new Vector3(0f, MovementSimulation.CollisionHeight(target.State.Mode) * 0.5f, 0f));
        float length = toBlast.Length();
        writer = new PacketWriter(_sendBuffer);
        DamageTaken.Write(ref writer, new DamageTaken
        {
            AttackerId = owner?.EntityId ?? 0,
            Damage = damage,
            FromDirection = length > 1e-4f ? toBlast / length : Vector3.Zero,
            Flags = DamageTaken.FlagsFor(shieldBefore, target.Shield),
        });
        _send(target.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);

        OnDamaged(target);
        if (fatal) ApplyFatal(target, owner, DeathCause.Explosion);
    }
}

// Phase 17 D17: one explosion as QA sees it (the newest ExplosionLogSize are kept).
internal readonly record struct ExplosionRecord(ushort Id, ProjectileKind Kind, Vector3 Position, uint Tick, ushort OwnerId, int PlayersHit, int PiecesHit);
