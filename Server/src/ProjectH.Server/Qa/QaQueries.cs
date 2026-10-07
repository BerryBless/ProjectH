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
public sealed record QaAmmoDto(int Light, int Medium, int Heavy);
public sealed record QaResourcesDto(int Wood, int Stone, int Metal);

public sealed record QaPlayerDto(
    string DevPlayerId, int EntityId, bool Connected, bool Graced, bool Alive, bool Participant, int Health, int Shield,
    QaVec3 Position, QaVec3 Velocity, float Yaw, MovementMode Mode, bool Grounded, int CurrentSlot, ToolKind Tool,
    QaWeaponDto? Weapon, QaWeaponDto[] Weapons, QaAmmoDto Ammo, int Medkits, int ShieldCells, QaResourcesDto Resources,
    int Kills, int Placement, int DamageDealt, long LastProcessedSeq, bool Reloading);

public sealed record QaZoneDto(int Phase, int PhaseCount, QaVec2 Center, float Radius, QaVec2 TargetCenter, float TargetRadius,
    long ShrinkStartTick, long ShrinkEndTick, int DamagePerSecond);

public sealed record QaMatchDto(
    MatchFlowState State, int Round, long Tick, double ElapsedSeconds, long StateEndTick, int MinPlayers, int Players, int Connected,
    int Graced, int Participants, int Alive, string? Winner, QaZoneDto Zone, int BuildPieces, int WorldItems, bool AirDropRoute);

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
            seeds = new { loot = o.LootSeed, zone = o.ZoneSeed, spawn = o.SpawnSeed },
            options = new
            {
                maxPlayers = o.MaxPlayers,
                minPlayers = o.MinPlayers,
                startCountdownSeconds = o.StartCountdownSeconds,
                resultSeconds = o.ResultSeconds,
                devRespawn = o.DevRespawn,
                airDrop = o.AirDrop,
                buildInfiniteResources = o.BuildInfiniteResources,
                reconnectGraceSeconds = o.ReconnectGraceSeconds,
                inputTimeoutSeconds = o.InputTimeoutSeconds,
                joinTimeoutSeconds = o.JoinTimeoutSeconds,
                qaEvents = qa.Options.Events,
                qaCommandTimeoutMs = qa.Options.CommandTimeoutMs,
                qaAllowRemote = qa.Options.AllowRemote,
            },
            qa = new { rejected = qa.Rejected, timedOut = qa.TimedOut, failed = qa.Failed },
            db,
            dbQueueLength = db?.QueueLength ?? 0,
            parentPid = qa.Options.ParentPid,
        };
    }

    public static QaPlayerDto[] Players(Match m)
    {
        var players = new QaPlayerDto[m.PlayerCount];
        for (int i = 0; i < players.Length; i++) players[i] = Player(m, m.PlayerAt(i));
        return players;
    }

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
        return new QaPlayerDto(
            p.DevPlayerId, p.EntityId, !p.IsGraced, p.IsGraced, p.Alive, p.Participant, p.Health, p.Shield,
            new QaVec3(s.Position.X, s.Position.Y, s.Position.Z), new QaVec3(s.HorizontalVelocity.X, s.VelocityY, s.HorizontalVelocity.Y),
            s.Yaw, s.Mode, m.IsGrounded(p), inv.CurrentSlot, inv.Tool, current, weapons.ToArray(),
            new QaAmmoDto(inv.GetAmmo(AmmoType.Light), inv.GetAmmo(AmmoType.Medium), inv.GetAmmo(AmmoType.Heavy)),
            inv.Medkits, inv.ShieldCells,
            new QaResourcesDto(inv.Resource(BuildMaterialType.Wood), inv.Resource(BuildMaterialType.Stone), inv.Resource(BuildMaterialType.Metal)),
            p.Kills, p.Placement, p.DamageDealt, p.LastProcessedSeq, p.Reloading);
    }

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
            connected, m.GracedCount, wire.Participants, wire.Alive, winner, zone, m.BuildPieces, m.WorldItems.Count, m.HasRoute);
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
