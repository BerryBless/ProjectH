using System;
using System.Collections.Generic;
using System.Reflection;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Qa;

public sealed record QaVec3(float X, float Y, float Z);
public sealed record QaVec2(float X, float Z);
public sealed record QaWeaponDto(int Slot, int WeaponId, string Name, int Rarity, int MagAmmo);
public sealed record QaAmmoDto(int Light, int Medium, int Heavy, int Shells = 0, int Rockets = 0);   // Phase 17: + Shells, Rockets
public sealed record QaResourcesDto(int Wood, int Stone, int Metal);

public sealed record QaPlayerDto(
    string DevPlayerId, int EntityId, bool Connected, bool Graced, bool Alive, bool Participant, int Health, int Shield,
    QaVec3 Position, QaVec3 Velocity, float Yaw, MovementMode Mode, bool Grounded, int CurrentSlot, ToolKind Tool,
    QaWeaponDto? Weapon, QaWeaponDto[] Weapons, QaAmmoDto Ammo, int Medkits, int ShieldCells, QaResourcesDto Resources,
    int Kills, int Placement, int DamageDealt, long LastProcessedSeq, bool Reloading,
    // Phase 14: the team (0 = none), knocked down, who knocked it down, cards held, its revive or reboot in progress, and
    // whether a teammate is reviving it.
    int TeamId = 0, int JoinOrder = 0, bool Downed = false, string? DownedBy = null, int RebootCards = 0, QaChannelDto? Channel = null,
    string? RevivedBy = null,
    // Phase 15: this player's waypoint (null = none), and its team's active pings and waypoints as the server holds them.
    QaVec3? Waypoint = null, int TeamPingCount = 0, QaPingDto[]? TeamPings = null, int TeamWaypointCount = 0, QaWaypointDto[]? TeamWaypoints = null,
    // Phase 17: grenades held and the tick the next throw is allowed.
    int Grenades = 0, long NextGrenadeTick = 0,
    // Phase 19: the vehicle this player sits in (0 = on foot) and its seat (0 driver, 1 passenger, -1 on foot).
    int VehicleId = 0, int Seat = -1);

// Phase 19 D14: a vehicle (state = VehicleState name; driver and passenger as DevPlayerIds, null = empty seat).
public sealed record QaVehicleDto(int Id, string State, int Health, int MaxHealth, QaVec3 Position, float Heading, float Speed, float Steer,
    string? Driver, int DriverId, string? Passenger, int PassengerId, long WreckEndTick);

// Phase 17 D17: a live projectile (kind = ProjectileKind name; owner as DevPlayerId while the owner is in the match).
public sealed record QaProjectileDto(int Id, string Kind, int OwnerId, string? Owner, QaVec3 Position, QaVec3 Velocity, bool Resting, long ExplodeTick);

// Phase 17 D17: a recent explosion (the newest Match.ExplosionLogSize), with how many players and pieces it damaged.
public sealed record QaExplosionDto(int Id, string Kind, QaVec3 Position, long Tick, int OwnerId, int PlayersHit, int PiecesHit);

// Phase 15: a team ping (kind = MapMarkerKind name; owner and target as DevPlayerIds when they are players still here).
public sealed record QaPingDto(int Id, string Kind, int OwnerId, string? Owner, QaVec3 Position, long EndTick, int TargetId, string? Target);

// Phase 15: a team member's waypoint.
public sealed record QaWaypointDto(int OwnerId, string? Owner, QaVec3 Position);

// Phase 14: a revive (target = the downed teammate's DevPlayerId) or reboot (station = the index) in progress.
public sealed record QaChannelDto(string Kind, string? Target, int Station, long EndTick);

// Phase 14: a reboot station and its cooldown.
public sealed record QaStationDto(int Index, QaVec3 Position, bool CoolingDown, long CooldownEndTick);

public sealed record QaZoneDto(int Phase, int PhaseCount, QaVec2 Center, float Radius, QaVec2 TargetCenter, float TargetRadius,
    long ShrinkStartTick, long ShrinkEndTick, int DamagePerSecond);

public sealed record QaMatchDto(
    MatchFlowState State, int Round, long Tick, double ElapsedSeconds, long StateEndTick, int MinPlayers, int Players, int Connected,
    int Graced, int Participants, int Alive, string? Winner, QaZoneDto Zone, int BuildPieces, int WorldItems, bool AirDropRoute,
    // Phase 14: the team size, the teams of the match, the teams not yet wiped out, reboot cards in the world, and the stations.
    int TeamSize = 1, int Teams = 0, int TeamsAlive = 0, int WorldCards = 0, QaStationDto[]? Stations = null,
    // Phase 16: loot containers spawned and opened, and the match's supply drops (details: GET /qa/loot).
    int ContainersSpawned = 0, int ContainersOpened = 0, int SupplyDrops = 0);

// Phase 16: one rolled loot item (kind = ItemKind name, rarity index 0-4).
public sealed record QaLootItemDto(string Kind, int DefId, int Rarity, int Amount);

// Phase 16: a loot container (kind = LootContainerKind name, state none / closed / open) and the loot it holds or held.
public sealed record QaContainerDto(int Id, string Kind, QaVec3 Position, float Yaw, string State, QaLootItemDto[] Loot);

// Phase 16: a supply drop (state = SupplyDropState name) and its loot. Computed when asked (QA only): the fall in ticks, the
// horizontal distance to the nearest living player now, and whether it lies inside 0.6 x the zone's current target circle
// (the "next circle" D6 picks from; Phase 0 = circle 1).
public sealed record QaSupplyDropDto(int Id, string State, QaVec3 Position, long StartTick, long LandTick, QaLootItemDto[] Loot,
    long FallTicks = 0, float NearestPlayerDistance = -1, bool InsideTargetCircle = false);

// Phase 16: a world item near the asked point (dropped = not a loot point's item).
public sealed record QaWorldItemDto(int ItemId, string Kind, int DefId, int Rarity, int Amount, QaVec3 Position, bool Dropped);

public sealed record QaPieceDto(long Id, BuildPieceType Type, BuildMaterialType Material, int CellX, int Level, int CellZ, int Rotation,
    QaVec3 Center, int Health, int MaxHealth, int Damage, int Owner, long CreatedTick, bool Grounded, int Edit = 0);

// QA-1: read-only views built on the game loop thread (as work items) into plain DTOs, so the HTTP thread serializes
// copies, never live game objects.
internal static class QaQueries
{
    public const int MaxPieces = 500;

    private static readonly string Version =
        typeof(QaQueries).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(QaQueries).Assembly.GetName().Version?.ToString() ?? "unknown";

    // 기능: GET /qa/health 본문을 만든다(Phase 14: options.teamSize, Phase 15: map 수치, Phase 16: loot 수치, 리뷰 수정 C1: seeds는 DeterministicSeeds일 때만, 아니면 null).
    // 입력: t - QA Tick 문맥.
    // 출력: 익명 객체.
    public static object Health(QaTick t)
    {
        QaControl qa = t.Qa;
        ServerOptions o = t.Loop.Options;
        Match m = t.Match;
        QaDbStatus? db = qa.Database?.Invoke();
        return new
        {
            ok = true,
            qaMode = true,
            gamePort = qa.GamePort,
            qaPort = qa.QaPort,
            simHz = o.SimHz,
            serverTick = (long)m.ServerTick,
            loopTicks = t.Loop.LoopTicks,
            matchState = m.Flow.State,
            round = (int)m.Flow.Round,
            activeSessions = t.Loop.PeerCount,
            players = m.PlayerCount,
            pid = Environment.ProcessId,
            version = Version,
            environment = qa.EnvironmentName,
            // Review fix C1: the seeds reproduce a match only with DeterministicSeeds; otherwise the match secret decides (null).
            seeds = o.DeterministicSeeds ? new { loot = o.LootSeed, zone = o.ZoneSeed, spawn = o.SpawnSeed } : null,
            options = new
            {
                maxPlayers = o.MaxPlayers,
                minPlayers = o.MinPlayers,
                startCountdownSeconds = o.StartCountdownSeconds,
                resultSeconds = o.ResultSeconds,
                devRespawn = o.DevRespawn,
                airDrop = o.AirDrop,
                buildInfiniteResources = o.BuildInfiniteResources,
                teamSize = o.TeamSize,
                reconnectGraceSeconds = o.ReconnectGraceSeconds,
                inputTimeoutSeconds = o.InputTimeoutSeconds,
                joinTimeoutSeconds = o.JoinTimeoutSeconds,
                qaEvents = qa.Options.Events,
                qaCommandTimeoutMs = qa.Options.CommandTimeoutMs,
                qaAllowRemote = qa.Options.AllowRemote,
            },
            qa = new { rejected = qa.Rejected, timedOut = qa.TimedOut, failed = qa.Failed },
            map = MapHealth(t.Loop.Health),
            loot = LootHealth(t.Loop.Health),
            db,
            dbQueueLength = db?.QueueLength ?? 0,
            parentPid = qa.Options.ParentPid,
        };
    }

    // 기능: Phase 15: /qa/health의 지도 표시 수치(수신 스레드 드롭, MarkerRate 잘못된 패킷, 경기 수치 합계)를 만든다.
    // 입력: h - 시작부터의 합계.
    // 출력: 익명 객체.
    private static object MapHealth(Diagnostics.HealthCounters h)
    {
        Diagnostics.MapCounts c = h.Map;
        return new
        {
            markerDrops = h.MarkerDrops,
            markerInboxDrops = h.MarkerInboxDrops,
            markerRateBadPackets = h.BadPackets(Diagnostics.BadPacketReason.MarkerRate),
            pings = c.Pings,
            enemyConfirmed = c.EnemyConfirmed,
            enemyDemoted = c.EnemyDemoted,
            refused = c.Refused,
            replaced = c.Replaced,
            expired = c.Expired,
            waypoints = c.Waypoints,
            packets = c.Packets,
        };
    }

    // 기능: Phase 16: /qa/health의 Loot 수치(경기 수치 합계)를 만든다.
    // 입력: h - 시작부터의 합계.
    // 출력: 익명 객체.
    private static object LootHealth(Diagnostics.HealthCounters h)
    {
        Diagnostics.LootCounts c = h.Loot;
        return new
        {
            containersOpened = c.ContainersOpened,
            dropsSpawned = c.DropsSpawned,
            dropsLanded = c.DropsLanded,
            dropsOpened = c.DropsOpened,
            lootItems = c.LootItems,
            opensBlocked = c.OpensBlocked,
            packets = c.Packets,
        };
    }

    public static QaPlayerDto[] Players(Match m)
    {
        var players = new QaPlayerDto[m.PlayerCount];
        for (int i = 0; i < players.Length; i++) players[i] = Player(m, m.PlayerAt(i));
        return players;
    }

    // 기능: 플레이어 하나의 QA DTO를 만든다(Phase 14: 팀, 기절, 기절시킨 사람, 카드, 진행, 소생자, Phase 15: Waypoint와 팀 Ping·Waypoint,
    //   Phase 17: Shells·Rockets 예비탄, 수류탄 수와 다음 던지기 Tick, Phase 19: 탄 차량 id와 좌석).
    // 입력: m - 경기, p - 플레이어.
    // 출력: QaPlayerDto.
    public static QaPlayerDto Player(Match m, PlayerEntity p)
    {
        Inventory inv = p.Inventory;
        var weapons = new List<QaWeaponDto>(Inventory.SlotCount);
        QaWeaponDto? current = null;
        for (int i = 0; i < Inventory.SlotCount; i++)
        {
            ref HeldWeapon held = ref inv.Slots[i];
            if (held.IsEmpty) continue;
            var dto = new QaWeaponDto(i, held.Weapon!.Id, held.Weapon.Name, held.Rarity, held.MagAmmo);
            weapons.Add(dto);
            if (i == inv.CurrentSlot) current = dto;
        }
        MoveState s = p.State;
        (MarkerPing[] pings, MarkerWaypoint[] waypoints) = m.TeamMarkersView(p.TeamId);
        return new QaPlayerDto(
            p.DevPlayerId, p.EntityId, !p.IsGraced, p.IsGraced, p.Alive, p.Participant, p.Health, p.Shield,
            new QaVec3(s.Position.X, s.Position.Y, s.Position.Z), new QaVec3(s.HorizontalVelocity.X, s.VelocityY, s.HorizontalVelocity.Y),
            s.Yaw, s.Mode, m.IsGrounded(p), inv.CurrentSlot, inv.Tool, current, weapons.ToArray(),
            new QaAmmoDto(inv.GetAmmo(AmmoType.Light), inv.GetAmmo(AmmoType.Medium), inv.GetAmmo(AmmoType.Heavy), inv.GetAmmo(AmmoType.Shells),
                inv.GetAmmo(AmmoType.Rockets)),
            inv.Medkits, inv.ShieldCells,
            new QaResourcesDto(inv.Resource(BuildMaterialType.Wood), inv.Resource(BuildMaterialType.Stone), inv.Resource(BuildMaterialType.Metal)),
            p.Kills, p.Placement, p.DamageDealt, p.LastProcessedSeq, p.Reloading,
            p.TeamId, (int)p.JoinOrder, p.IsDowned, p.IsDowned ? p.DownedBy?.DevPlayerId : null, inv.CardCount, ChannelView(p),
            p.IsDowned ? p.RevivedBy?.DevPlayerId : null,
            p.HasWaypoint ? new QaVec3(p.Waypoint.X, p.Waypoint.Y, p.Waypoint.Z) : null,
            pings.Length, Array.ConvertAll(pings, ping => new QaPingDto(ping.Id, ping.Kind.ToString(), ping.OwnerId, NameOf(m, ping.OwnerId),
                new QaVec3(ping.Position.X, ping.Position.Y, ping.Position.Z), ping.EndTick, ping.TargetId,
                ping.Kind == MapMarkerKind.Enemy ? NameOf(m, ping.TargetId) : null)),
            waypoints.Length, Array.ConvertAll(waypoints, w => new QaWaypointDto(w.OwnerId, NameOf(m, w.OwnerId), new QaVec3(w.Position.X, w.Position.Y, w.Position.Z))),
            inv.Grenades, p.NextGrenadeTick,
            p.Vehicle?.Id ?? 0, p.InVehicle ? p.Seat : -1);
    }

    // 기능: Phase 19 D14: 차량 목록과 수치를 보여 준다(GET /qa/vehicles). 차량은 vehicles 배열과 id 키("3": {...}) 둘 다로 넣어
    //   vehicle.<id>.health 같은 경로를 바로 읽게 한다.
    // 입력: m - 경기.
    // 출력: 사전(count, vehicles, 차량 id 키들, spawned, wrecked, enters, exits, impacts, runOvers, statesSent).
    public static Dictionary<string, object?> Vehicles(Match m)
    {
        var list = new List<QaVehicleDto>(m.VehicleCount);
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (ProjectH.Server.Game.Vehicles.Vehicle v in m.VehicleSlots)
        {
            if (!v.InUse) continue;
            PlayerEntity? driver = v.Seats[VehicleSettings.DriverSeat];
            PlayerEntity? passenger = v.Seats[VehicleSettings.PassengerSeat];
            var dto = new QaVehicleDto(v.Id, v.State.ToString(), v.Health, m.VehicleData.MaxHealth,
                new QaVec3(v.Move.Position.X, v.Move.Position.Y, v.Move.Position.Z), v.Move.Heading, v.Move.Speed, v.Move.Steer,
                driver?.DevPlayerId, driver?.EntityId ?? 0, passenger?.DevPlayerId, passenger?.EntityId ?? 0, v.WreckEndTick);
            list.Add(dto);
            root[v.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = dto;
        }
        root["count"] = list.Count;
        root["vehicles"] = list;
        root["spawned"] = m.VehiclesSpawned;
        root["wrecked"] = m.VehiclesWrecked;
        root["enters"] = m.VehicleEnters;
        root["exits"] = m.VehicleExits;
        root["impacts"] = m.VehicleImpacts;
        root["runOvers"] = m.VehicleRunOvers;
        root["statesSent"] = m.VehicleStatesSent;
        return root;
    }

    // 기능: Phase 17 D17: 살아 있는 투사체와 최근 폭발, 투사체 수치를 보여 준다(GET /qa/projectiles).
    // 입력: m - 경기.
    // 출력: 익명 객체(projectiles, explosions(오래된 것부터), launched, refused, explosionsTotal, grenadesThrown).
    public static object Projectiles(Match m)
    {
        var projectiles = new List<QaProjectileDto>(m.Projectiles.Count);
        for (int i = 0; i < ProjectH.Server.Game.Combat.ProjectileSet.Capacity; i++)
        {
            ref ProjectH.Server.Game.Combat.Projectile p = ref m.Projectiles[i];
            if (!p.Active) continue;
            projectiles.Add(new QaProjectileDto(p.Id, p.Definition.Kind.ToString(), p.OwnerEntityId, NameOf(m, p.OwnerEntityId),
                new QaVec3(p.Position.X, p.Position.Y, p.Position.Z), new QaVec3(p.Velocity.X, p.Velocity.Y, p.Velocity.Z), p.Resting, p.ExplodeTick));
        }
        long total = m.ExplosionLogCount;
        int size = ProjectH.Server.Game.Match.ExplosionLogSize;
        int filled = (int)Math.Min(total, size);
        var explosions = new QaExplosionDto[filled];
        ReadOnlySpan<ExplosionRecord> log = m.ExplosionLog;
        for (int k = 0; k < filled; k++)
        {
            ExplosionRecord e = log[(int)((total - filled + k) % size)];
            explosions[k] = new QaExplosionDto(e.Id, e.Kind.ToString(), new QaVec3(e.Position.X, e.Position.Y, e.Position.Z), e.Tick, e.OwnerId,
                e.PlayersHit, e.PiecesHit);
        }
        return new
        {
            projectiles, explosions, launched = m.ProjectilesLaunched, refused = m.ProjectilesRefused, explosionsTotal = total,
            grenadesThrown = m.GrenadesThrown,
        };
    }

    // 기능: Phase 15: Entity id의 DevPlayerId를 찾는다(QA 관찰용).
    // 입력: m - 경기, entityId - id.
    // 출력: 경기에 있는 플레이어면 DevPlayerId, 아니면 null.
    private static string? NameOf(Match m, ushort entityId)
    {
        for (int i = 0; i < m.PlayerCount; i++)
        {
            if (m.PlayerAt(i).EntityId == entityId) return m.PlayerAt(i).DevPlayerId;
        }
        return null;
    }

    // 기능: Phase 14: 플레이어의 진행 중인 소생·재투입을 DTO로 만든다.
    // 입력: p - 플레이어.
    // 출력: 진행 중이면 QaChannelDto, 아니면 null.
    private static QaChannelDto? ChannelView(PlayerEntity p)
    {
        if (!p.ChannelActive) return null;
        return p.Channel == ChannelKind.Revive
            ? new QaChannelDto("revive", p.ReviveTarget?.DevPlayerId, -1, p.ChannelEndTick)
            : new QaChannelDto("reboot", null, p.ChannelStation, p.ChannelEndTick);
    }

    // 기능: Phase 14: 스테이션 위치와 대기 상태를 DTO로 만든다.
    // 입력: m - 경기.
    // 출력: 스테이션 DTO 배열(RebootStations 순서).
    private static QaStationDto[] Stations(Match m)
    {
        var stations = new QaStationDto[RebootStations.Count];
        for (int i = 0; i < stations.Length; i++)
        {
            System.Numerics.Vector3 at = RebootStations.All[i];
            uint end = m.StationEndTick(i);
            stations[i] = new QaStationDto(i, new QaVec3(at.X, at.Y, at.Z), end > m.ServerTick, end > m.ServerTick ? end : 0);
        }
        return stations;
    }

    // 기능: 경기의 QA DTO를 만든다(Phase 14: 팀 크기·팀 수·남은 팀·월드 카드·스테이션, Phase 16: Container 생성·열림 수, Supply Drop 수).
    // 입력: m - 경기, simHz - Tick 속도.
    // 출력: QaMatchDto.
    public static QaMatchDto Match(Match m, int simHz)
    {
        int connected = m.PlayerCount - m.GracedCount;
        string? winner = null;
        if (m.WinnerId != 0)
        {
            for (int i = 0; i < m.PlayerCount; i++)
            {
                if (m.PlayerAt(i).EntityId == m.WinnerId) winner = m.PlayerAt(i).DevPlayerId;
            }
        }
        bool started = m.Flow.State is MatchFlowState.Playing or MatchFlowState.FinalPhase or MatchFlowState.Finished;
        double elapsed = started ? (m.ServerTick - m.MatchStartTick) / (double)simHz : 0;
        SafeZoneView(m, out QaZoneDto zone);
        MatchState wire = m.Flow.ToWire(m.PlayerCount);
        return new QaMatchDto(m.Flow.State, m.Flow.Round, m.ServerTick, elapsed, m.Flow.StateEndTick, m.Flow.MinPlayers, m.PlayerCount,
            connected, m.GracedCount, wire.Participants, wire.Alive, winner, zone, m.BuildPieces, m.WorldItems.Count, m.HasRoute,
            m.TeamSize, m.Flow.Teams, m.Flow.TeamsAlive, m.WorldItems.CardCount, Stations(m),
            System.Numerics.BitOperations.PopCount(m.ContainerSpawnedMask), System.Numerics.BitOperations.PopCount(m.ContainerOpenedMask), m.SupplyDropCount);
    }

    public const int MaxLootItems = 256;

    // 기능: Phase 16 GET /qa/loot 본문: Container 전체(상태와 Loot), Supply Drop 목록, (x, z, radius를 주면) 그 원 안의 월드 아이템 목록과
    //   종류별 수·무기 등급 범위.
    // 입력: m - 경기, x·z·radius - 아이템을 볼 원(모두 null이면 모든 아이템), max - 아이템 목록 상한.
    // 출력: 익명 객체.
    public static object Loot(Match m, float? x, float? z, float? radius, int max)
    {
        var containers = new QaContainerDto[LootContainers.Count];
        for (int i = 0; i < containers.Length; i++)
        {
            LootContainer c = LootContainers.All[i];
            ulong bit = 1UL << i;
            string state = (m.ContainerSpawnedMask & bit) == 0 ? "none" : (m.ContainerOpenedMask & bit) != 0 ? "open" : "closed";
            containers[i] = new QaContainerDto(i, c.Kind.ToString(), new QaVec3(c.Position.X, c.Position.Y, c.Position.Z), c.Yaw, state,
                LootOf(m.ContainerLoot(i)));
        }
        var drops = new QaSupplyDropDto[m.SupplyDropCount];
        for (int i = 0; i < drops.Length; i++)
        {
            SupplyDropInfo d = m.SupplyDropAt(i);
            drops[i] = new QaSupplyDropDto(d.Id, d.State.ToString(), new QaVec3(d.X, d.LandY, d.Z), d.StartTick, d.LandTick, LootOf(m.SupplyDropLoot(i)),
                (long)d.LandTick - d.StartTick, NearestLivingPlayer(m, d.X, d.Z), InsideTargetCircle(m, d.X, d.Z));
        }
        var items = new List<QaWorldItemDto>();
        int count = 0, weapons = 0, ammo = 0, consumables = 0, materials = 0, minRarity = -1, maxRarity = -1;
        for (int i = 0; i < m.WorldItems.Count; i++)
        {
            ref readonly WorldItem item = ref m.WorldItems[i];
            WorldItemData d = item.Data;
            if (radius.HasValue)
            {
                float dx = d.Position.X - x!.Value, dz = d.Position.Z - z!.Value;
                if (dx * dx + dz * dz > radius.Value * radius.Value) continue;
            }
            count++;
            switch (d.Kind)
            {
                case ItemKind.Weapon:
                    weapons++;
                    minRarity = minRarity < 0 ? d.Rarity : Math.Min(minRarity, d.Rarity);
                    maxRarity = Math.Max(maxRarity, d.Rarity);
                    break;
                case ItemKind.Ammo: ammo++; break;
                case ItemKind.Consumable: consumables++; break;
                case ItemKind.Material: materials++; break;
            }
            if (items.Count < max)
                items.Add(new QaWorldItemDto(d.ItemId, d.Kind.ToString(), d.DefId, d.Rarity, d.Amount, new QaVec3(d.Position.X, d.Position.Y, d.Position.Z), item.IsDropped));
        }
        return new
        {
            containersSpawned = System.Numerics.BitOperations.PopCount(m.ContainerSpawnedMask),
            containersOpened = System.Numerics.BitOperations.PopCount(m.ContainerOpenedMask),
            containers,
            supplyDropCount = drops.Length,
            supplyDrops = drops,
            items = new { count, truncated = count > items.Count, weapons, ammo, consumables, materials, minWeaponRarity = minRarity, maxWeaponRarity = maxRarity, list = items },
        };
    }

    // 기능: 점에서 가장 가까운 살아 있는 플레이어까지의 수평 거리를 잰다(QA 관찰용).
    // 입력: m - 경기, x·z - 점.
    // 출력: 거리, 살아 있는 플레이어가 없으면 -1.
    private static float NearestLivingPlayer(Match m, float x, float z)
    {
        float best = -1f;
        for (int i = 0; i < m.PlayerCount; i++)
        {
            PlayerEntity p = m.PlayerAt(i);
            if (!p.Alive) continue;
            float dx = p.State.Position.X - x, dz = p.State.Position.Z - z;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (best < 0f || d < best) best = d;
        }
        return best;
    }

    // 기능: 점이 지금 자기장 목표 원(Phase 0이면 원 1) 반지름 × 0.6 안인지 본다(Supply Drop 위치 규칙 확인용, 반올림 여유 1 cm).
    // 입력: m - 경기, x·z - 점.
    // 출력: 안이면 true.
    private static bool InsideTargetCircle(Match m, float x, float z)
    {
        var zone = m.Zone;
        int circle = Math.Clamp(zone.Phase == 0 ? 1 : zone.Phase, 0, zone.PhaseCount);
        float dx = x - zone.CenterX(circle), dz = z - zone.CenterZ(circle);
        float limit = zone.Radius(circle) * 0.6f + 0.01f;
        return dx * dx + dz * dz <= limit * limit;
    }

    // 기능: 굴린 Loot 칸을 DTO 배열로 바꾼다.
    // 입력: loot - Loot 칸.
    // 출력: DTO 배열.
    private static QaLootItemDto[] LootOf(ReadOnlySpan<LootRoll> loot)
    {
        var list = new QaLootItemDto[loot.Length];
        for (int i = 0; i < list.Length; i++) list[i] = new QaLootItemDto(loot[i].Kind.ToString(), loot[i].DefId, loot[i].Rarity, loot[i].Amount);
        return list;
    }

    private static void SafeZoneView(Match m, out QaZoneDto zone)
    {
        var z = m.Zone;
        z.Sample(m.ServerTick, out float x, out float cz, out float radius);
        int phase = z.Phase;
        zone = new QaZoneDto(phase, z.PhaseCount, new QaVec2(x, cz), radius, new QaVec2(z.CenterX(phase), z.CenterZ(phase)), z.Radius(phase),
            z.ShrinkStartTick, z.ShrinkEndTick, z.DamagePerSecond);
    }

    // Pieces whose centre lies within radius (across the ground) of (x, z); all of them without a centre. At most max.
    public static object Build(Match m, float? x, float? z, float? radius, int max)
    {
        BuildWorld world = m.Build;
        var pieces = new List<QaPieceDto>();
        int count = 0;
        for (int slot = 0; slot < world.Grid.SlotCount; slot++)
        {
            ref BuildPiece piece = ref world.At(slot);
            if (piece.Id == 0 || world.Grid.SlotOf(piece.Id) != slot) continue;
            System.Numerics.Vector3 center = BuildGrid.CenterOf(piece.Shape);
            if (x.HasValue && z.HasValue && radius.HasValue)
            {
                float dx = center.X - x.Value, dz = center.Z - z.Value;
                if (dx * dx + dz * dz > radius.Value * radius.Value) continue;
            }
            count++;
            if (pieces.Count >= max) continue;
            int maxHealth = m.Building.Material(piece.Material).MaxHealth;
            pieces.Add(new QaPieceDto(piece.Id, piece.Shape.Type, piece.Material, piece.Shape.X, piece.Shape.Y, piece.Shape.Z, piece.Shape.Rotation,
                new QaVec3(center.X, center.Y, center.Z), world.Health(piece, m.ServerTick), maxHealth, piece.Damage, piece.Owner, piece.CreatedTick,
                piece.Grounded, piece.Shape.Edit));
        }
        return new { count, truncated = count > pieces.Count, total = world.Count, pieces };
    }
}
