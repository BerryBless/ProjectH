using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Bots;

public enum BotGoal : byte
{
    None = 0,
    Idle = 1,      // before the match or on the result screen: stand still (D4 rule 2)
    Zone = 2,      // walk into the next circle (rule 3)
    Fight = 3,     // aim, fire and strafe at a visible enemy (rule 4)
    Heal = 4,      // use a Medkit or Shield Cell (rule 5)
    Loot = 5,      // walk to a useful item and press E (rule 6)
    Wander = 6,    // walk to a random point in the zone (rule 7)
}

// Phase 7 D4-D7: one bot's decisions. Decide() re-plans every DecideInterval from the BotView; Act() turns the plan
// into this tick's input. Pure logic: time and randomness come in as arguments, nothing is shared with other bots,
// and a tick allocates nothing (fixed arrays, struct enumerators).
public sealed class BotBrain
{
    public const float DecideInterval = 0.1f;
    public const float EngageRange = 60f;
    public const float HealSafeDistance = 15f;
    public const int HealHealthBelow = 60;
    public const int HealShieldBelow = 25;
    public const float LootSearchRange = 80f;
    public const float PickupReach = 1.5f;      // the server allows 2 m; 1.5 m leaves room for the move this tick
    public const float PickupHeight = 1.8f;     // the server allows 2 m up or down
    public const float SprintDistance = 10f;
    public const float ZoneMargin = 3f;
    public const float WanderRadiusNoZone = 60f;
    public const float WanderSeconds = 15f;
    public const float AimErrorRefreshSeconds = 0.3f;
    public const int FireTicksWithoutHit = 45;  // ticks with Fire pressed and no HitConfirmed (D7): ~1.5 s for an automatic; a semi-automatic presses every other tick
    public const float IgnoreTargetSeconds = 5f;
    public const float ItemBlacklistSeconds = 30f;
    public const float ButtonRepeatSeconds = 0.5f;
    public const int MaxInteractTries = 3;
    public const float StrafeMinSeconds = 1f;
    public const float StrafeMaxSeconds = 2f;
    public const float CloseRange = 8f;
    public const float FarRange = 25f;
    private const int Memory = 16;
    private const int MaxSightChecks = 3;

    private readonly Random _rng;
    private readonly BotSteering _steering = new();
    private readonly ushort[] _ignoredTargets = new ushort[Memory];
    private readonly float[] _ignoredUntil = new float[Memory];
    private readonly ushort[] _blacklistedItems = new ushort[Memory];
    private readonly float[] _blacklistedUntil = new float[Memory];
    private int _nextIgnore;
    private int _nextBlacklist;

    private float _nextDecide;
    private float _nextAimRoll;
    private float _aimErrorYaw;
    private float _aimErrorPitch;
    private int _fireTicksSinceHit;
    private int _hitsSeen;
    private float _strafeSign = 1f;
    private float _nextStrafeFlip;
    private float _nextButton;
    private int _interactTries;
    private float _wanderUntil;
    private bool _fireToggle;
    private float _bodyYaw;
    private InputButtons _healButton;

    public BotBrain(int seed)
    {
        _rng = new Random(seed);
    }

    public BotGoal Goal { get; private set; }
    public ushort Target { get; private set; }
    public ushort GoalItem { get; private set; }
    public Vector3 GoalPoint { get; private set; }

    // This tick's input. False = send nothing (not joined, or no snapshot yet). A dead or spectating bot returns true
    // with an empty input (Phase 10 D4), so the server's input timeout does not close it.
    public bool Tick(BotView view, float now, out InputCommand command)
    {
        command = default;
        if (!view.Joined || !view.HasSnapshot)
        {
            Goal = BotGoal.None;
            return false;
        }
        if (!view.Alive)
        {
            // Phase 10 D4: like the Unity client, a dead or spectating bot keeps sending empty inputs (no move, no
            // buttons) while joined, so the server's input timeout does not close it.
            Goal = BotGoal.None;
            command.Yaw = _bodyYaw;
            command.ViewTick = view.ServerTick;
            return true;
        }
        if (view.HitsLanded != _hitsSeen)
        {
            _hitsSeen = view.HitsLanded;
            _fireTicksSinceHit = 0;
        }
        if (now >= _nextDecide)
        {
            Decide(view, now);
            _nextDecide = now + DecideInterval;
        }
        Act(view, now, ref command);
        command.ViewTick = view.ServerTick;
        return true;
    }

    private void Decide(BotView view, float now)
    {
        Vector3 me = view.MyPosition;
        if (!view.InMatch)
        {
            SetGoal(BotGoal.Idle, me, me, now);
            return;
        }
        if (TryZonePoint(view, me, out Vector3 zonePoint))
        {
            SetGoal(BotGoal.Zone, zonePoint, me, now);
            return;
        }
        if (TryPickTarget(view, now, out ushort target))
        {
            if (Goal != BotGoal.Fight || Target != target) _fireTicksSinceHit = 0;
            Goal = BotGoal.Fight;
            Target = target;
            return;
        }
        if (TryHeal(view, out InputButtons healButton))
        {
            Goal = BotGoal.Heal;
            _healButton = healButton;
            return;
        }
        if (TryPickItem(view, now, out ushort itemId, out Vector3 itemPosition))
        {
            if (Goal != BotGoal.Loot || GoalItem != itemId)
            {
                _interactTries = 0;
                GoalItem = itemId;
                SetGoal(BotGoal.Loot, itemPosition, me, now, force: true);
            }
            return;
        }
        if (Goal != BotGoal.Wander || now >= _wanderUntil || BotAim.HorizontalDistance(me, GoalPoint) < 3f)
        {
            SetGoal(BotGoal.Wander, PickWanderPoint(view), me, now, force: true);
            _wanderUntil = now + WanderSeconds;
        }
    }

    private void SetGoal(BotGoal goal, Vector3 point, Vector3 me, float now, bool force = false)
    {
        bool moved = BotAim.HorizontalDistance(point, GoalPoint) > 2f;
        if (force || goal != Goal || moved) _steering.Reset(me, point, now);
        Goal = goal;
        GoalPoint = point;
    }

    // Rule 3: outside the next circle (less a margin; the last circle has radius 0) -> its centre.
    private static bool TryZonePoint(BotView view, Vector3 me, out Vector3 point)
    {
        point = default;
        ZoneState zone = view.Zone;
        if (zone.Phase == 0) return false;
        float dx = me.X - zone.ToX;
        float dz = me.Z - zone.ToZ;
        float safe = MathF.Max(zone.ToRadius - ZoneMargin, 2f);
        if (dx * dx + dz * dz <= safe * safe) return false;
        point = new Vector3(zone.ToX, GameMap.Terrain.Height(zone.ToX, zone.ToZ), zone.ToZ);
        return true;
    }

    // Rule 4: the nearest living, visible, not ignored enemy within range, if this bot has a weapon with rounds.
    private bool TryPickTarget(BotView view, float now, out ushort target)
    {
        target = 0;
        float range = FightRange(view);
        if (range <= 0f) return false;

        if (Goal == BotGoal.Fight && _fireTicksSinceHit >= FireTicksWithoutHit)
        {
            Ignore(Target, now);
            _fireTicksSinceHit = 0;
        }

        Vector3 eye = BotAim.Eye(view.MyPosition);
        float floor = -1f;
        for (int check = 0; check < MaxSightChecks; check++)
        {
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < view.OtherCount; i++)
            {
                ref readonly SnapshotEntity other = ref view.Others[i];
                if (!other.IsAlive || IsIgnored(other.EntityId, now)) continue;
                float d = Vector3.Distance(view.MyPosition, other.Position);
                if (d > range || d <= floor || d >= bestDistance) continue;
                best = i;
                bestDistance = d;
            }
            if (best < 0) return false;
            if (LineOfSight.Clear(eye, BotAim.Chest(view.Others[best].Position), GameMap.Boxes, GameMap.Terrain))
            {
                target = view.Others[best].EntityId;
                return true;
            }
            floor = bestDistance;
        }
        return false;
    }

    // The longest range among held weapons that still have rounds (capped at EngageRange); 0 = cannot fight.
    private static float FightRange(BotView view)
    {
        float range = 0f;
        for (int slot = 0; slot < ItemConstants.WeaponSlotCount; slot++)
        {
            WeaponInfo? weapon = view.WeaponInSlot(slot);
            if (weapon == null || !HasRounds(view, slot, weapon.Value)) continue;
            range = MathF.Max(range, MathF.Min(weapon.Value.Range, EngageRange));
        }
        return range;
    }

    private static bool HasRounds(BotView view, int slot, WeaponInfo weapon)
    {
        int magazine = slot == view.Inventory.CurrentSlot ? view.Self.Ammo : view.Inventory.GetSlot(slot).MagAmmo;
        return magazine > 0 || view.Reserve(weapon.AmmoType) > 0;
    }

    // Rule 5: no enemy close, and Health or Shield low with the matching item. A running heal keeps the goal.
    private static bool TryHeal(BotView view, out InputButtons button)
    {
        button = InputButtons.None;
        if (!view.HasInventory) return false;
        for (int i = 0; i < view.OtherCount; i++)
        {
            if (view.Others[i].IsAlive && Vector3.Distance(view.MyPosition, view.Others[i].Position) < HealSafeDistance) return false;
        }
        if (view.Inventory.Using != ConsumableType.None) return true;   // wait for it to finish
        if (view.Self.Health < HealHealthBelow && view.Inventory.Medkits > 0) button = InputButtons.UseMedkit;
        else if (view.Self.Shield < HealShieldBelow && view.Inventory.ShieldCells > 0) button = InputButtons.UseShieldCell;
        return button != InputButtons.None;
    }

    // Rule 6: the nearest useful item within LootSearchRange that is not blacklisted.
    private bool TryPickItem(BotView view, float now, out ushort itemId, out Vector3 position)
    {
        itemId = 0;
        position = default;
        float bestDistance = LootSearchRange;
        foreach (KeyValuePair<ushort, WorldItemData> pair in view.Items)
        {
            WorldItemData item = pair.Value;
            if (!IsUseful(view, item) || IsBlacklisted(item.ItemId, now)) continue;
            float d = BotAim.HorizontalDistance(view.MyPosition, item.Position);
            if (d >= bestDistance) continue;
            bestDistance = d;
            itemId = item.ItemId;
            position = item.Position;
        }
        return itemId != 0;
    }

    public static bool IsUseful(BotView view, in WorldItemData item)
    {
        InventoryState inv = view.Inventory;
        switch (item.Kind)
        {
            case ItemKind.Weapon:
                return inv.Slot0.IsEmpty || inv.Slot1.IsEmpty || inv.Slot2.IsEmpty;
            case ItemKind.Ammo:
            {
                var type = (AmmoType)item.DefId;
                bool used = false;
                for (int slot = 0; slot < ItemConstants.WeaponSlotCount; slot++)
                {
                    WeaponInfo? weapon = view.WeaponInSlot(slot);
                    if (weapon != null && weapon.Value.AmmoType == type) used = true;
                }
                return used && view.Reserve(type) < AmmoMax(view, type);
            }
            case ItemKind.Consumable:
            {
                var type = (ConsumableType)item.DefId;
                int have = type == ConsumableType.Medkit ? inv.Medkits : inv.ShieldCells;
                return have < ConsumableMax(view, type);
            }
            default:
                return false;
        }
    }

    private static int AmmoMax(BotView view, AmmoType type)
    {
        if (view.Catalog == null) return int.MaxValue;
        foreach (AmmoInfo info in view.Catalog.Ammo)
        {
            if (info.Type == type) return info.Max;
        }
        return int.MaxValue;
    }

    private static int ConsumableMax(BotView view, ConsumableType type)
    {
        if (view.Catalog == null) return int.MaxValue;
        foreach (ConsumableInfo info in view.Catalog.Consumables)
        {
            if (info.Type == type) return info.MaxStack;
        }
        return int.MaxValue;
    }

    // Rule 7: a random point in the next circle (80 % of its radius), or within WanderRadiusNoZone of the centre.
    private Vector3 PickWanderPoint(BotView view)
    {
        float cx = 0f;
        float cz = 0f;
        float radius = WanderRadiusNoZone;
        if (view.Zone.Phase > 0)
        {
            cx = view.Zone.ToX;
            cz = view.Zone.ToZ;
            radius = view.Zone.ToRadius * 0.8f;
        }
        double angle = _rng.NextDouble() * Math.PI * 2.0;
        float r = radius * MathF.Sqrt((float)_rng.NextDouble());
        float limit = GameMap.HalfSize - 5f;
        float x = Math.Clamp(cx + r * (float)Math.Cos(angle), -limit, limit);
        float z = Math.Clamp(cz + r * (float)Math.Sin(angle), -limit, limit);
        return new Vector3(x, GameMap.Terrain.Height(x, z), z);
    }

    private void Act(BotView view, float now, ref InputCommand command)
    {
        Vector3 me = view.MyPosition;
        command.Yaw = _bodyYaw;
        switch (Goal)
        {
            case BotGoal.Fight:
                ActFight(view, now, ref command);
                break;
            case BotGoal.Heal:
                if (_healButton != InputButtons.None && view.Inventory.Using == ConsumableType.None && now >= _nextButton)
                {
                    command.Buttons |= _healButton;
                    _nextButton = now + ButtonRepeatSeconds;
                }
                break;
            case BotGoal.Loot:
                ActLoot(view, now, ref command);
                break;
            case BotGoal.Zone:
            case BotGoal.Wander:
                Walk(me, GoalPoint, now, ref command, out bool giveUp);
                if (giveUp)
                {
                    if (Goal == BotGoal.Wander) _wanderUntil = 0f;   // pick another point next decision
                    else _steering.Reset(me, GoalPoint, now);       // the zone cannot be given up: keep trying
                }
                break;
        }
        if (Goal != BotGoal.Fight)
        {
            command.AimYaw = command.Yaw;
            command.AimPitch = 0f;
        }
        _bodyYaw = command.Yaw;
    }

    private void Walk(Vector3 me, Vector3 goal, float now, ref InputCommand command, out bool giveUp)
    {
        _steering.Steer(me, goal, now, _rng, out float yaw, out bool jump, out giveUp);
        command.Yaw = yaw;
        command.MoveY = 1f;
        if (BotAim.HorizontalDistance(me, goal) > SprintDistance) command.Buttons |= InputButtons.Sprint;
        if (jump) command.Buttons |= InputButtons.Jump;
    }

    private void ActLoot(BotView view, float now, ref InputCommand command)
    {
        if (!view.Items.TryGetValue(GoalItem, out WorldItemData item))
        {
            Goal = BotGoal.None;   // taken (by us or someone else): decide again next time
            return;
        }
        Vector3 me = view.MyPosition;
        bool inReach = BotAim.HorizontalDistance(me, item.Position) <= PickupReach && MathF.Abs(me.Y - item.Position.Y) <= PickupHeight;
        if (!inReach)
        {
            Walk(me, item.Position, now, ref command, out bool giveUp);
            if (giveUp) Blacklist(item.ItemId, now);
            return;
        }
        if (now < _nextButton) return;
        if (++_interactTries > MaxInteractTries)
        {
            Blacklist(item.ItemId, now);   // the server keeps refusing it (full, or someone else's pick won)
            Goal = BotGoal.None;
            return;
        }
        command.Buttons |= InputButtons.Interact;
        _nextButton = now + ButtonRepeatSeconds;
    }

    private void ActFight(BotView view, float now, ref InputCommand command)
    {
        int index = FindOther(view, Target);
        if (index < 0 || !view.Others[index].IsAlive)
        {
            Goal = BotGoal.None;
            return;
        }
        Vector3 me = view.MyPosition;
        Vector3 enemy = view.Others[index].Position;
        float distance = Vector3.Distance(me, enemy);
        if (now >= _nextAimRoll)
        {
            _aimErrorYaw = BotAim.RollError(_rng, distance);
            _aimErrorPitch = BotAim.RollError(_rng, distance);
            _nextAimRoll = now + AimErrorRefreshSeconds;
        }
        BotAim.Solve(BotAim.Eye(me), BotAim.Chest(enemy), out float yaw, out float pitch);
        command.AimYaw = yaw + _aimErrorYaw;
        command.AimPitch = pitch + _aimErrorPitch;
        command.Yaw = yaw;

        if (now >= _nextStrafeFlip)
        {
            _strafeSign = -_strafeSign;
            _nextStrafeFlip = now + StrafeMinSeconds + (float)_rng.NextDouble() * (StrafeMaxSeconds - StrafeMinSeconds);
        }
        command.MoveX = _strafeSign;
        command.MoveY = distance > FarRange ? 1f : distance < CloseRange ? -1f : 0f;

        int current = view.Inventory.CurrentSlot;
        WeaponInfo? weapon = view.WeaponInSlot(current);
        if (weapon == null || !HasRounds(view, current, weapon.Value))
        {
            // Switch to a slot that still has rounds.
            for (int slot = 0; slot < ItemConstants.WeaponSlotCount; slot++)
            {
                WeaponInfo? other = view.WeaponInSlot(slot);
                if (slot == current || other == null || !HasRounds(view, slot, other.Value) || now < _nextButton) continue;
                command.Buttons |= slot == 0 ? InputButtons.Slot1 : slot == 1 ? InputButtons.Slot2 : InputButtons.Slot3;
                _nextButton = now + ButtonRepeatSeconds;
                break;
            }
            return;
        }
        if (view.Self.ReloadRemainingTicks > 0) return;
        if (view.Self.Ammo == 0)
        {
            if (now >= _nextButton)
            {
                command.Buttons |= InputButtons.Reload;
                _nextButton = now + ButtonRepeatSeconds;
            }
            return;
        }
        // A semi-automatic weapon needs the trigger released between shots: press every other tick.
        _fireToggle = !_fireToggle;
        if (weapon.Value.Automatic || _fireToggle)
        {
            command.Buttons |= InputButtons.Fire;
            _fireTicksSinceHit++;
        }
    }

    private static int FindOther(BotView view, ushort entityId)
    {
        for (int i = 0; i < view.OtherCount; i++)
        {
            if (view.Others[i].EntityId == entityId) return i;
        }
        return -1;
    }

    private void Ignore(ushort entityId, float now)
    {
        _ignoredTargets[_nextIgnore] = entityId;
        _ignoredUntil[_nextIgnore] = now + IgnoreTargetSeconds;
        _nextIgnore = (_nextIgnore + 1) % Memory;
    }

    private bool IsIgnored(ushort entityId, float now)
    {
        for (int i = 0; i < Memory; i++)
        {
            if (_ignoredTargets[i] == entityId && now < _ignoredUntil[i]) return true;
        }
        return false;
    }

    private void Blacklist(ushort itemId, float now)
    {
        _blacklistedItems[_nextBlacklist] = itemId;
        _blacklistedUntil[_nextBlacklist] = now + ItemBlacklistSeconds;
        _nextBlacklist = (_nextBlacklist + 1) % Memory;
    }

    private bool IsBlacklisted(ushort itemId, float now)
    {
        for (int i = 0; i < Memory; i++)
        {
            if (_blacklistedItems[i] == itemId && now < _blacklistedUntil[i]) return true;
        }
        return false;
    }
}
