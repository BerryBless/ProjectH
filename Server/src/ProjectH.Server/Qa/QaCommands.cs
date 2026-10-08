using System;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Qa;

// QA-1 §22: the Arrange commands of POST /qa/command. Game loop thread only (run as work items). Every argument is
// checked before anything changes, so a refused command (400, 404, 409) leaves the match as it was. They go through
// Match's own paths where one exists (death, damage, item spawn, piece damage, match finish), so eliminations,
// placements, drops, collapses and replication behave as in play; otherwise they set the state the way Respawn does.
internal static class QaCommands
{
    public const int MaxMarkLength = 500;
    private static readonly string[] AmmoNames = { "light", "medium", "heavy", "shells", "rockets" };   // AmmoType - 1 (Phase 17: + shells, rockets)
    private static readonly string[] ItemNames = { "medkit", "shieldCell", "grenade" };                  // ConsumableType - 1 (Phase 17: + grenade)
    private static readonly string[] MaterialNames = { "wood", "stone", "metal" };          // BuildMaterialType
    private static readonly string[] PieceNames = { "wall", "floor", "ramp", "roof" };      // BuildPieceType
    private static readonly string[] LootKinds = { "weapon", "ammo", "medkit", "shieldCell", "material", "grenade" };   // Phase 17: + grenade

    public static readonly string[] Names =
    {
        "mark", "setPosition", "setHealth", "setShield", "giveWeapon", "giveAmmo", "giveItem", "giveResource",
        "damagePlayer", "killPlayer", "forceMatchState", "setZone", "spawnLoot", "spawnBuildPiece", "damageBuild", "editBuild",
        "downPlayer", "giveRebootCard", "setStationCooldown", "spawnSupplyDrop", "setContainer",
        "giveGrenade",   // Phase 17 D17
    };
    private static readonly string[] ContainerStates = { "none", "closed", "open" };   // Match.QaSetContainer state

    // 기능: QA Arrange 명령 하나를 실행한다(Phase 14: downPlayer, giveRebootCard, setStationCooldown, Phase 16: spawnSupplyDrop, setContainer,
    //   Phase 17: giveGrenade, giveAmmo의 shells·rockets, giveItem·spawnLoot의 grenade).
    // 입력: t - QA Tick 문맥, command - 이름, player - 대상 DevPlayerId, runId - 실행 id, args - 인자, logger - 로그.
    // 출력: QaResult(200, 400, 404, 409).
    public static QaResult Execute(QaTick t, string command, string? player, string? runId, JsonElement args, ILogger logger)
    {
        var a = new QaArgs(args);
        Match m = t.Match;
        switch (command)
        {
            case "mark": return Mark(t, a, runId, logger);
            case "forceMatchState": return ForceMatchState(m, a);
            case "setZone": return SetZone(m, a);
            case "spawnLoot": return SpawnLoot(t, a);
            case "spawnBuildPiece": return SpawnBuildPiece(m, a);
            case "damageBuild": return DamageBuild(m, a);
            case "editBuild": return EditBuild(m, a);
            case "setStationCooldown": return SetStationCooldown(m, a);
            case "spawnSupplyDrop": return SpawnSupplyDrop(m, a);
            case "setContainer": return SetContainer(m, a);
        }

        if (Array.IndexOf(Names, command) < 0)
            return QaResult.Error(400, $"Unknown command '{command}'. Known: {string.Join(", ", Names)}.");
        if (string.IsNullOrEmpty(player)) return QaResult.Error(400, $"'{command}' needs 'player' (a DevPlayerId).");
        PlayerEntity? p = FindPlayer(m, player);
        if (p == null) return QaResult.Error(404, $"No player '{player}' in the match.");

        switch (command)
        {
            case "setPosition": return SetPosition(m, p, a);
            case "setHealth": return SetHealth(p, a);
            case "setShield": return SetShield(p, a);
            case "giveWeapon": return GiveWeapon(t, p, a);
            case "giveAmmo": return GiveAmmo(t, p, a);
            case "giveItem": return GiveItem(t, p, a);
            case "giveResource": return GiveResource(m, p, a);
            case "damagePlayer": return DamagePlayer(m, p, a);
            case "downPlayer": return DownPlayer(m, p);
            case "giveRebootCard": return GiveRebootCard(m, p, a);
            case "giveGrenade": return GiveGrenade(t, p, a);
            default: return KillPlayer(m, p);
        }
    }

    // A connected player first; otherwise a graced one with that DevPlayerId (D7: the id survives a reconnect).
    public static PlayerEntity? FindPlayer(Match m, string devPlayerId)
    {
        PlayerEntity? graced = null;
        for (int i = 0; i < m.PlayerCount; i++)
        {
            PlayerEntity p = m.PlayerAt(i);
            if (!string.Equals(p.DevPlayerId, devPlayerId, StringComparison.Ordinal)) continue;
            if (!p.IsGraced) return p;
            graced ??= p;
        }
        return graced;
    }

    private static QaResult Bad(QaArgs a) => QaResult.Error(400, a.Error!);
    private static QaResult NotAlive(PlayerEntity p) => QaResult.Error(409, $"Player '{p.DevPlayerId}' is not alive.");

    private static QaResult Mark(QaTick t, QaArgs a, string? runId, ILogger logger)
    {
        string text = a.RequiredText("text", MaxMarkLength);
        if (a.Error != null) return Bad(a);
        logger.LogInformation("QA mark run={RunId} tick={Tick}: {Text}", runId ?? "-", t.Match.ServerTick, text);
        return QaResult.Ok(new { tick = t.Match.ServerTick });
    }

    // 기능: 플레이어를 Tick 사이에 옮긴다(맵 상자·닫힌 문과 겹치면 거절). Phase 14: 기절한 사람은 기절 모드 그대로.
    // 입력: m - 경기, p - 대상, a - 인자(x, z, y·yaw 선택).
    // 출력: Ok면 새 위치.
    // A teleport between ticks: the next Step starts from here, so the movement self-check (which compares one Step's
    // start and end) never sees the jump; the lag compensation history starts over here (a rewind must not reach the old
    // place), and the move state is a rested Ground one, as Respawn makes it. A client's next input sets the yaw again.
    private static QaResult SetPosition(Match m, PlayerEntity p, QaArgs a)
    {
        double x = a.RequiredNumber("x", -GameMap.HalfSize, GameMap.HalfSize);
        double z = a.RequiredNumber("z", -GameMap.HalfSize, GameMap.HalfSize);
        double? y = a.Number("y", -100, 1000);
        double? yaw = a.Number("yaw", -360, 720);
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);

        float fy = y.HasValue ? (float)y.Value : GameMap.Terrain.Height((float)x, (float)z);
        float fyaw = yaw.HasValue ? (float)((yaw.Value % 360 + 360) % 360) : p.State.Yaw;
        var position = new Vector3((float)x, fy, (float)z);
        // A body inside a map box or a closed door would be pushed out by the next Step, and that push would count as a
        // movement anomaly (only pieces are exempt there): refuse instead. Pieces are allowed (the simulation lifts out of them).
        if (MovementSimulation.OverlapsAny(position, MoveSettings.Height, m.Doors.World))
            return QaResult.Error(409, $"({x}, {fy}, {z}) overlaps the map (a box or a closed door); pick a free spot or pass y.");
        // Phase 14: a knocked-down player stays down (only the server's revive or elimination leaves the Downed mode).
        MovementMode mode = p.IsDowned ? MovementMode.Downed : MovementMode.Ground;
        p.State = new MoveState { Position = position, Yaw = fyaw, Mode = mode };
        p.Sprinting = false;
        p.History.Reset(m.ServerTick, position, mode);
        return QaResult.Ok(new { x = position.X, y = position.Y, z = position.Z, yaw = fyaw });
    }

    private static QaResult SetHealth(PlayerEntity p, QaArgs a)
    {
        long value = a.RequiredInteger("value", 1, CombatRules.MaxHealth);
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);
        p.Health = (int)value;
        return QaResult.Ok(new { health = p.Health });
    }

    private static QaResult SetShield(PlayerEntity p, QaArgs a)
    {
        long value = a.RequiredInteger("value", 0, CombatRules.MaxShield);
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);
        p.Shield = (int)value;
        return QaResult.Ok(new { shield = p.Shield });
    }

    // A weapon by id or name (weapons.json), or the error naming the choices.
    private static WeaponDefinition? FindWeapon(WeaponCatalog weapons, QaArgs a, string name, bool required)
    {
        string? text = required ? a.RequiredText(name) : a.Text(name);
        if (text == null || a.Error != null) return null;
        text = text.Trim();
        for (int i = 0; i < weapons.Count; i++)
        {
            WeaponDefinition w = weapons[i];
            if (string.Equals(w.Name, text, StringComparison.OrdinalIgnoreCase) || w.Id.ToString() == text) return w;
        }
        var names = new string[weapons.Count];
        for (int i = 0; i < weapons.Count; i++) names[i] = $"{weapons[i].Id} \"{weapons[i].Name}\"";
        a.Invalid($"Unknown weapon '{text}'. Known: {string.Join(", ", names)}.");
        return null;
    }

    // A rarity by index or name (items.json); 0 when missing.
    private static byte Rarity(ItemCatalog items, QaArgs a)
    {
        string? text = a.Text("rarity");
        if (text == null) return 0;
        int count = items.Wire.Rarities.Length;
        if (int.TryParse(text, out int index) && index >= 0 && index < count) return (byte)index;
        for (int i = 0; i < count; i++)
        {
            if (string.Equals(items.RarityName(i), text.Trim(), StringComparison.OrdinalIgnoreCase)) return (byte)i;
        }
        var names = new string[count];
        for (int i = 0; i < count; i++) names[i] = items.RarityName(i);
        a.Invalid($"'rarity' must be 0-{count - 1} or one of: {string.Join(", ", names)}.");
        return 0;
    }

    private static QaResult GiveWeapon(QaTick t, PlayerEntity p, QaArgs a)
    {
        GameData data = t.Loop.Data;
        WeaponDefinition? weapon = FindWeapon(data.Weapons, a, "weapon", required: true);
        byte rarity = Rarity(data.Items, a);
        long? slotArg = a.Integer("slot", 0, Inventory.SlotCount - 1);
        bool select = a.Bool("select") ?? true;
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);

        Inventory inv = p.Inventory;
        int slot = (int)(slotArg ?? -1);
        for (int i = 0; i < Inventory.SlotCount && slot < 0; i++)
        {
            if (inv.Slots[i].IsEmpty) slot = i;
        }
        if (slot < 0) slot = inv.CurrentSlot;
        // A reload in progress belonged to the weapon in hand; replacing or switching it cancels the reload (as a pickup does).
        if (slot == inv.CurrentSlot || (select && slot != inv.CurrentSlot)) p.Reloading = false;
        inv.Slots[slot] = new HeldWeapon { Weapon = weapon, Rarity = rarity, MagAmmo = weapon!.MagazineSize };
        if (select)
        {
            inv.CurrentSlot = slot;
            inv.Tool = ToolKind.Weapon;
        }
        inv.Changed = true;
        return QaResult.Ok(new { slot, weaponId = (int)weapon.Id, name = weapon.Name, rarity = (int)rarity, magAmmo = (int)weapon.MagazineSize });
    }

    private static QaResult GiveAmmo(QaTick t, PlayerEntity p, QaArgs a)
    {
        int type = a.RequiredChoice("type", AmmoNames);
        long amount = a.RequiredInteger("amount", 1, 10000);
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);
        var ammo = (AmmoType)(type + 1);
        int value = (int)Math.Min(t.Loop.Data.Items.Ammo(ammo).Max, p.Inventory.GetAmmo(ammo) + amount);
        p.Inventory.SetAmmo(ammo, value);
        p.Inventory.Changed = true;
        return QaResult.Ok(new { ammo = value });
    }

    // 기능: 소모품을 준다(maxStack까지. Phase 17: grenade).
    // 입력: t - QA Tick 문맥, p - 대상, a - 인자(item, count).
    // 출력: Ok면 새 소지 수.
    private static QaResult GiveItem(QaTick t, PlayerEntity p, QaArgs a)
    {
        int item = a.RequiredChoice("item", ItemNames);
        long count = a.RequiredInteger("count", 1, 100);
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);
        return GiveConsumable(t, p, (ConsumableType)(item + 1), count);
    }

    // 기능: Phase 17 D17 giveGrenade: 수류탄을 준다(giveItem item=grenade와 같다).
    // 입력: t - QA Tick 문맥, p - 대상, a - 인자(count, 기본 1).
    // 출력: Ok면 새 소지 수.
    private static QaResult GiveGrenade(QaTick t, PlayerEntity p, QaArgs a)
    {
        long? count = a.Integer("count", 1, 100);
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);
        return GiveConsumable(t, p, ConsumableType.Grenade, count ?? 1);
    }

    // 기능: 소모품 수를 maxStack까지 늘린다.
    // 입력: t - QA Tick 문맥, p - 대상, type - 종류, count - 더할 수.
    // 출력: Ok와 새 소지 수.
    private static QaResult GiveConsumable(QaTick t, PlayerEntity p, ConsumableType type, long count)
    {
        int max = t.Loop.Data.Items.Consumable(type).MaxStack;
        Inventory inv = p.Inventory;
        int value = (int)Math.Min(max, ItemRules.ConsumableCount(inv, type) + count);
        switch (type)
        {
            case ConsumableType.Medkit: inv.Medkits = value; break;
            case ConsumableType.ShieldCell: inv.ShieldCells = value; break;
            default: inv.Grenades = value; break;
        }
        inv.Changed = true;
        return QaResult.Ok(new { count = value });
    }

    private static QaResult GiveResource(Match m, PlayerEntity p, QaArgs a)
    {
        int material = a.RequiredChoice("material", MaterialNames);
        long amount = a.RequiredInteger("amount", 1, ushort.MaxValue);
        if (a.Error != null) return Bad(a);
        if (!p.Alive) return NotAlive(p);
        var type = (BuildMaterialType)material;
        int value = (int)Math.Min(m.Building.MaxResource, p.Inventory.Resource(type) + amount);
        p.Inventory.SetResource(type, value);   // sets ResourcesChanged: the owner gets ResourcesState at the next tick's end
        return QaResult.Ok(new { amount = value });
    }

    // 기능: 공격자 없는 피해를 준다(Match.DamagePlayer, Phase 14: 치명이면 기절 또는 탈락).
    // 입력: m - 경기, p - 대상, a - 인자(amount).
    // 출력: Ok면 { health, shield, killed, downed }.
    private static QaResult DamagePlayer(Match m, PlayerEntity p, QaArgs a)
    {
        long amount = a.RequiredInteger("amount", 1, 10000);
        if (a.Error != null) return Bad(a);
        if (!m.DamagePlayer(p, (int)amount, out bool killed)) return NotAlive(p);
        return QaResult.Ok(new { health = p.Health, shield = p.Shield, killed, downed = p.IsDowned });
    }

    // 기능: Phase 14 downPlayer: 플레이어를 바로 기절시킨다(시나리오 준비, Match.DownPlayer). 같은 팀에 서 있는 구성원이 있어야 한다.
    // 입력: m - 경기, p - 대상.
    // 출력: Ok면 { health, downed }, 조건이 맞지 않으면 409.
    private static QaResult DownPlayer(Match m, PlayerEntity p)
    {
        if (!m.DownPlayer(p))
            return QaResult.Error(409, $"Player '{p.DevPlayerId}' cannot be knocked down (alive {p.Alive}, downed {p.IsDowned}, team {p.TeamId}: a knock-down needs a standing teammate).");
        return QaResult.Ok(new { health = p.Health, downed = true });
    }

    // 기능: Phase 14 giveRebootCard: 같은 팀의 탈락한 참가자(owner)의 카드를 플레이어에게 준다(월드에 있던 그 카드는 지운다).
    // 입력: m - 경기, p - 받는 사람, a - 인자(owner: 카드 주인 DevPlayerId).
    // 출력: Ok면 { cards }, 주인이 없으면 404, 조건이 맞지 않으면 409.
    private static QaResult GiveRebootCard(Match m, PlayerEntity p, QaArgs a)
    {
        string ownerId = a.RequiredText("owner");
        if (a.Error != null) return Bad(a);
        PlayerEntity? owner = FindPlayer(m, ownerId);
        if (owner == null) return QaResult.Error(404, $"No player '{ownerId}' in the match.");
        if (!m.GiveCard(p, owner))
            return QaResult.Error(409, $"'{ownerId}' must be an eliminated teammate of '{p.DevPlayerId}', and '{p.DevPlayerId}' alive with room for a card.");
        return QaResult.Ok(new { cards = p.Inventory.CardCount });
    }

    // 기능: Phase 14 setStationCooldown: 스테이션 대기를 바꾼다(0 = 바로 사용 가능). 변경은 Tick 끝 RebootStations로 간다.
    // 입력: m - 경기, a - 인자(station 0-3, seconds 0-3600).
    // 출력: Ok면 { station, cooldownEndTick }.
    private static QaResult SetStationCooldown(Match m, QaArgs a)
    {
        long station = a.RequiredInteger("station", 0, RebootStations.Count - 1);
        long seconds = a.RequiredInteger("seconds", 0, 3600);
        if (a.Error != null) return Bad(a);
        m.SetStationCooldown((int)station, (int)seconds);
        return QaResult.Ok(new { station, cooldownEndTick = (long)m.StationEndTick((int)station) });
    }


    // 기능: Phase 16 spawnSupplyDrop: 경기 중 Supply Drop을 바로 만든다(x·z를 주면 그 자리, 없으면 서버 위치 규칙).
    // 입력: m - 경기, a - 인자(x·z 선택, 함께).
    // 출력: Ok면 칸·위치·시작·착지 Tick, 경기가 아니거나 가득 찼거나 위치 규칙이 자리를 못 찾았으면 409.
    private static QaResult SpawnSupplyDrop(Match m, QaArgs a)
    {
        double? x = a.Number("x", -GameMap.HalfSize, GameMap.HalfSize);
        double? z = a.Number("z", -GameMap.HalfSize, GameMap.HalfSize);
        if (a.Error == null && x.HasValue != z.HasValue) a.Invalid("x and z go together.");
        if (a.Error != null) return Bad(a);
        int slot = m.QaSpawnSupplyDrop(x.HasValue ? new Vector2((float)x.Value, (float)z!.Value) : null);
        if (slot < 0) return QaResult.Error(409, $"No supply drop: outside the match, already {SupplyDropsPacket.MaxSupplyDrops} this match, or no clear spot now.");
        SupplyDropInfo d = m.SupplyDropAt(slot);
        return QaResult.Ok(new { id = slot, x = d.X, z = d.Z, y = d.LandY, startTick = (long)d.StartTick, landTick = (long)d.LandTick });
    }

    // 기능: Phase 16 setContainer: 시나리오 준비용으로 Container 상태를 강제한다(closed는 Loot가 없으면 굴린다, open은 Loot를 놓지 않는다).
    // 입력: m - 경기, a - 인자(container = id, state = none|closed|open).
    // 출력: Ok면 id·state·Loot 수, 경기·개발 모드가 아니면 409.
    private static QaResult SetContainer(Match m, QaArgs a)
    {
        long id = a.RequiredInteger("container", 0, LootContainers.Count - 1);
        int state = a.RequiredChoice("state", ContainerStates);
        if (a.Error != null) return Bad(a);
        if (!m.QaSetContainer((int)id, state)) return QaResult.Error(409, "Containers can be set only during the match or in the dev sandbox.");
        return QaResult.Ok(new { container = id, state = ContainerStates[state], loot = m.ContainerLoot((int)id).Length });
    }

    private static QaResult KillPlayer(Match m, PlayerEntity p)
    {
        if (!m.KillPlayer(p)) return NotAlive(p);
        return QaResult.Ok(new { placement = (int)p.Placement });
    }

    private static QaResult ForceMatchState(Match m, QaArgs a)
    {
        int state = a.RequiredChoice("state", "start", "finish");
        if (a.Error != null) return Bad(a);
        if (state == 0)
        {
            if (m.Flow.InMatch) return QaResult.Error(409, $"The match is already running ({m.Flow.State}).");
            // Between ticks ServerTick is the next tick's `now`: the next tick starts the match.
            if (!m.Flow.SkipCountdown(m.ServerTick, m.PlayerCount))
                return QaResult.Error(409, $"Cannot start from {m.Flow.State} with {m.PlayerCount} players (MinPlayers {m.Flow.MinPlayers}, DevRespawn {m.Flow.DevRespawn}).");
            return QaResult.Ok(new { state = m.Flow.State.ToString(), startsAtTick = m.Flow.StateEndTick });
        }
        if (!m.ForceFinish()) return QaResult.Error(409, $"No match is running ({m.Flow.State}).");
        return QaResult.Ok(new { state = m.Flow.State.ToString() });
    }

    private static QaResult SetZone(Match m, QaArgs a)
    {
        bool advance = a.Bool("advance") ?? false;
        long? phase = a.Integer("phase", 1, m.Zone.PhaseCount);
        if (a.Error == null && !advance && phase == null) a.Invalid("setZone needs 'advance': true or 'phase': n.");
        if (a.Error != null) return Bad(a);
        if (!m.Flow.InMatch || m.Zone.Phase == 0) return QaResult.Error(409, $"The zone runs only during a match ({m.Flow.State}, zone phase {m.Zone.Phase}).");
        int target = phase.HasValue ? (int)phase.Value : m.Zone.Phase + 1;
        if (target <= m.Zone.Phase) return QaResult.Error(409, $"The zone is already in phase {m.Zone.Phase}; it cannot go back.");
        if (target > m.Zone.PhaseCount) return QaResult.Error(409, $"The zone is in its last phase ({m.Zone.Phase}).");
        while (m.Zone.Phase < target)
        {
            if (!m.AdvanceZone()) return QaResult.Error(409, $"The zone stopped at phase {m.Zone.Phase}.");
        }
        return QaResult.Ok(new { phase = m.Zone.Phase, shrinkStartTick = m.Zone.ShrinkStartTick, shrinkEndTick = m.Zone.ShrinkEndTick });
    }

    // 기능: QA spawnLoot: 플레이어 없이 월드 아이템 하나를 놓는다(Phase 17: grenade 종류, 탄 shells·rockets).
    // 입력: t - QA Tick 문맥, a - 인자(kind, 위치, id·rarity·amount 선택).
    // 출력: Ok면 새 아이템 id, 월드가 가득이면 409.
    private static QaResult SpawnLoot(QaTick t, QaArgs a)
    {
        GameData data = t.Loop.Data;
        int kind = a.RequiredChoice("kind", LootKinds);
        double x = a.RequiredNumber("x", -GameMap.HalfSize, GameMap.HalfSize);
        double z = a.RequiredNumber("z", -GameMap.HalfSize, GameMap.HalfSize);
        double? y = a.Number("y", -100, 1000);
        if (a.Error != null) return Bad(a);

        LootRoll roll;
        switch (kind)
        {
            case 0:
            {
                WeaponDefinition? weapon = a.Has("id") ? FindWeapon(data.Weapons, a, "id", required: false) : data.Weapons[0];
                byte rarity = Rarity(data.Items, a);
                long? rounds = weapon == null ? null : a.Integer("amount", 0, weapon.MagazineSize);
                if (a.Error != null) return Bad(a);
                roll = new LootRoll(ItemKind.Weapon, weapon!.Id, rarity, (ushort)(rounds ?? weapon.MagazineSize));
                break;
            }
            case 1:
            {
                int type = a.RequiredChoice("id", AmmoNames);
                long? amount = a.Integer("amount", 1, ushort.MaxValue);
                if (a.Error != null) return Bad(a);
                var ammo = (AmmoType)(type + 1);
                roll = new LootRoll(ItemKind.Ammo, (byte)ammo, 0, (ushort)(amount ?? data.Items.Ammo(ammo).PickupAmount));
                break;
            }
            case 2:
            case 3:
            {
                long? amount = a.Integer("amount", 1, byte.MaxValue);
                if (a.Error != null) return Bad(a);
                byte defId = (byte)(kind == 2 ? ConsumableType.Medkit : ConsumableType.ShieldCell);
                roll = new LootRoll(ItemKind.Consumable, defId, 0, (ushort)(amount ?? 1));
                break;
            }
            case 5:
            {
                // Phase 17 D9: grenades on the ground (amount = count).
                long? amount = a.Integer("amount", 1, byte.MaxValue);
                if (a.Error != null) return Bad(a);
                roll = new LootRoll(ItemKind.Consumable, (byte)ConsumableType.Grenade, 0, (ushort)(amount ?? 1));
                break;
            }
            default:
            {
                int material = a.RequiredChoice("id", MaterialNames);
                long? amount = a.Integer("amount", 1, ushort.MaxValue);
                if (a.Error != null) return Bad(a);
                roll = new LootRoll(ItemKind.Material, (byte)(material + 1), 0, (ushort)(amount ?? 10));
                break;
            }
        }

        float fy = y.HasValue ? (float)y.Value : GameMap.Terrain.Height((float)x, (float)z);
        // A dropped item (no spawn point): the store may evict it like any drop when full.
        ushort itemId = t.Match.SpawnItem(roll, new Vector3((float)x, fy, (float)z), -1);
        if (itemId == 0) return QaResult.Error(409, "The world item store is full.");
        return QaResult.Ok(new { itemId = (int)itemId });
    }

    private static QaResult SpawnBuildPiece(Match m, QaArgs a)
    {
        int piece = a.RequiredChoice("piece", PieceNames);
        int material = a.RequiredChoice("material", MaterialNames);
        long rotation = a.Integer("rotation", 0, 3) ?? 0;
        int cellX, level, cellZ;
        if (a.Has("cellX") || a.Has("cellZ") || a.Has("level"))
        {
            cellX = (int)a.RequiredInteger("cellX", 0, BuildGrid.CellsX - 1);
            level = (int)a.RequiredInteger("level", 0, BuildGrid.Levels - 1);
            cellZ = (int)a.RequiredInteger("cellZ", 0, BuildGrid.CellsZ - 1);
        }
        else
        {
            double x = a.RequiredNumber("x", -GameMap.HalfSize, GameMap.HalfSize);
            double z = a.RequiredNumber("z", -GameMap.HalfSize, GameMap.HalfSize);
            double? y = a.Number("y", 0, BuildGrid.Levels * BuildGrid.LevelHeight);
            float fy = y.HasValue ? (float)y.Value : GameMap.Terrain.Height((float)x, (float)z);
            cellX = BuildGrid.CellX((float)x);
            cellZ = BuildGrid.CellZ((float)z);
            level = Math.Max(0, BuildGrid.Level(fy));
        }
        if (a.Error != null) return Bad(a);
        if (!BuildGrid.TryNormalize((BuildPieceType)piece, cellX, level, cellZ, (int)rotation, out BuildPieceShape shape))
            return QaResult.Error(400, $"The piece does not fit the grid at cell ({cellX}, {cellZ}) level {level} rotation {rotation}.");

        BuildResultCode code = m.PlacePiece(shape, (BuildMaterialType)material, out uint id);
        if (code != BuildResultCode.Ok) return QaResult.Error(409, $"The piece was refused: {code}.");
        return QaResult.Ok(new { pieceId = (long)id, cellX = (int)shape.X, level = (int)shape.Y, cellZ = (int)shape.Z, rotation = (int)shape.Rotation });
    }

    // 기능: Phase 13.5 editBuild: 조각의 편집 상태를 바꾼다(Match.EditPieceById, 소유자·사거리 검사 없음). 회전을 안 주면 지금 회전.
    // 입력: m - 경기, a - 인자(pieceId, edit 0-4095, rotation 0-3 선택).
    // 출력: Ok면 { code, edit, rotation }, 없는 조각 404, 거절 409.
    private static QaResult EditBuild(Match m, QaArgs a)
    {
        long id = a.RequiredInteger("pieceId", 1, uint.MaxValue);
        long edit = a.RequiredInteger("edit", 0, ProjectH.Shared.Simulation.BuildEdit.Mask);
        long? rotation = a.Integer("rotation", 0, 3);
        if (a.Error != null) return Bad(a);
        if (!m.Build.TryGetSlot((uint)id, out int slot)) return QaResult.Error(404, $"No piece {id}.");
        int turn = (int)(rotation ?? m.Build.At(slot).Shape.Rotation);
        BuildResultCode code = m.EditPieceById((uint)id, ProjectH.Shared.Simulation.BuildEdit.PackState((int)edit, turn));
        if (code != BuildResultCode.Ok) return QaResult.Error(409, $"The edit was refused: {code}.");
        ProjectH.Shared.Simulation.BuildPieceShape shape = m.Build.At(slot).Shape;
        return QaResult.Ok(new { code = code.ToString(), edit = (int)shape.Edit, rotation = (int)shape.Rotation });
    }

    private static QaResult DamageBuild(Match m, QaArgs a)
    {
        long id = a.RequiredInteger("pieceId", 1, uint.MaxValue);
        long amount = a.RequiredInteger("amount", 1, 100000);
        if (a.Error != null) return Bad(a);
        if (!m.DamagePieceById((uint)id, amount, out bool destroyed)) return QaResult.Error(404, $"No piece {id}.");
        int health = 0;
        if (!destroyed && m.Build.TryGetSlot((uint)id, out int slot)) health = m.Build.Health(m.Build.At(slot), m.ServerTick + 1);
        return QaResult.Ok(new { destroyed, health, standing = m.Build.Contains((uint)id) });
    }
}
