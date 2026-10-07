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
    Deploy = 7,    // Phase 12 D15: ride the transport, jump, steer the fall to the landing target
    Crawl = 8,     // Phase 14 D15: knocked down: crawl towards the nearest teammate (bots do not revive yet)
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
    // Phase 12 D15: closer than this to the landing target (across the ground) the bot stops steering and drops straight.
    public const float LandingStopDistance = 5f;
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
    // Phase 12 D15: one deployment's plan, made when the bot first sees itself aboard.
    private bool _deploying;
    private Vector3 _landingTarget;
    private uint _jumpTick;

    public BotBrain(int seed)
    {
        _rng = new Random(seed);
    }

    public BotGoal Goal { get; private set; }
    public ushort Target { get; private set; }
    public ushort GoalItem { get; private set; }
    public Vector3 GoalPoint { get; private set; }
    // Phase 12 D15: the current deployment's landing target and the server tick it jumps at.
    public Vector3 LandingTarget => _landingTarget;
    public uint JumpTick => _jumpTick;

    // 기능: 봇의 이번 Tick 입력을 정한다(죽음, 투입, Phase 14 기절이면 팀원 쪽으로 기어감, 그 외 결정과 행동).
    // 입력: view - 봇이 아는 것, now - 지금 시각(초), command - 이번 입력.
    // 출력: 보낼 입력이 있으면 true.
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
            _deploying = false;   // the next life plans its own deployment
            command.Yaw = _bodyYaw;
            command.ViewTick = view.ServerTick;
            return true;
        }
        // Phase 12 D15: aboard, falling or gliding nothing else is possible (D12): ride, jump, steer the fall.
        if (Deploy(view, now, ref command))
        {
            command.ViewTick = view.ServerTick;
            return true;
        }
        // Phase 14 D15: knocked down nothing but crawling is possible (the server blocks every action).
        if (view.MyMode == MovementMode.Downed)
        {
            Crawl(view, now, ref command);
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

    // D15: the plan is made once per deployment, from what a client knows: the route and the places it has heard of
    // (the POIs and the items the server sent; the loot points are server data). The brain's own seeded Random picks
    // one, so bots spread over the map. The jump is pressed from the planned tick on, at most every ButtonRepeatSeconds
    // (a snapshot shows the fall only a little later, and a second press in freefall would open the glider early: the
    // server opens it at GlideAutoDeployHeight anyway). In the air the bot faces the target and flies forward until it
    // is above it. Returns false in any other mode.
    private bool Deploy(BotView view, float now, ref InputCommand command)
    {
        MovementMode mode = view.MyMode;
        if (mode != MovementMode.Transport && mode != MovementMode.Freefall && mode != MovementMode.Glide)
        {
            _deploying = false;
            return false;
        }
        Goal = BotGoal.Deploy;
        if (!_deploying)
        {
            // A route that ended before this tick belongs to an earlier round (its snapshot can beat the new route here):
            // aboard, wait for the new one.
            if (!view.HasRoute || (mode == MovementMode.Transport && view.ServerTick >= view.Route.EndTick))
            {
                command.Yaw = _bodyYaw;
                return true;
            }
            _landingTarget = PickLandingTarget(view);
            _jumpTick = PlanJumpTick(view.Route, _landingTarget);
            _deploying = true;
        }

        Vector3 me = view.MyPosition;
        float yaw = BotAim.YawTo(me, _landingTarget);
        command.Yaw = yaw;
        command.AimYaw = yaw;
        _bodyYaw = yaw;
        if (mode == MovementMode.Transport)
        {
            if (view.ServerTick + 1 >= _jumpTick && now >= _nextButton)
            {
                command.Buttons |= InputButtons.Jump;
                _nextButton = now + ButtonRepeatSeconds;
            }
            return true;
        }
        command.MoveY = BotAim.HorizontalDistance(me, _landingTarget) > LandingStopDistance ? 1f : 0f;
        return true;
    }

    private Vector3 PickLandingTarget(BotView view)
    {
        ReadOnlySpan<MapPoi> pois = MapPois.All;
        int pick = _rng.Next(pois.Length + view.Items.Count);
        if (pick < pois.Length)
        {
            MapPoi poi = pois[pick];
            return new Vector3(poi.X, GameMap.Terrain.Height(poi.X, poi.Z), poi.Z);
        }
        int index = pick - pois.Length;
        foreach (KeyValuePair<ushort, WorldItemData> pair in view.Items)
        {
            if (index-- == 0) return pair.Value.Position;
        }
        return Vector3.Zero;   // not reached: pick is below the item count
    }

    // D15: the tick the transport passes closest to the target (the target projected on the route), inside the jump window.
    public static uint PlanJumpTick(in DropRoute route, Vector3 target)
    {
        float dx = route.EndX - route.StartX;
        float dz = route.EndZ - route.StartZ;
        float lengthSq = dx * dx + dz * dz;
        float s = lengthSq > 0f ? ((target.X - route.StartX) * dx + (target.Z - route.StartZ) * dz) / lengthSq : 0f;
        s = Math.Clamp(s, 0f, 1f);
        uint tick = route.StartTick + (uint)MathF.Round(s * route.DurationTicks);
        route.JumpWindow(out uint first, out uint last);
        return Math.Clamp(tick, first, last);
    }

    // 기능: 기절한 봇이 가장 가까운 팀원 쪽으로 기어간다(D15). 보이는 팀원이 없으면 멈춘다.
    // 입력: view - 봇이 아는 것, now - 지금 시각(초), command - 이번 입력.
    // 출력: 반환값 없음. command의 이동이 정해진다.
    private void Crawl(BotView view, float now, ref InputCommand command)
    {
        if (Goal != BotGoal.Crawl) _steering.Reset(view.MyPosition, view.MyPosition, now);
        Goal = BotGoal.Crawl;
        Vector3 me = view.MyPosition;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < view.OtherCount; i++)
        {
            ref readonly SnapshotEntity other = ref view.Others[i];
            if (!other.IsAlive || other.Mode == MovementMode.Downed || !view.IsTeammate(other.EntityId)) continue;
            float d = BotAim.HorizontalDistance(me, other.Position);
            if (d >= bestDistance) continue;
            best = i;
            bestDistance = d;
        }
        command.Yaw = _bodyYaw;
        if (best < 0 || bestDistance < CrawlStopDistance) return;
        GoalPoint = view.Others[best].Position;
        Walk(me, GoalPoint, now, ref command, out _);
        command.Buttons &= ~(InputButtons.Jump | InputButtons.Sprint);
        _bodyYaw = command.Yaw;
    }

    // Phase 14 D15: a crawling bot stops this close to its teammate (inside the 2 m revive range).
    private const float CrawlStopDistance = 1.5f;

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

    // 기능: 사거리 안, 보이는, 무시하지 않은 가장 가까운 적을 고른다(Phase 14 D15: 팀원은 제외, 기절한 적은 낮게 겨눠 포함).
    // 입력: view - 봇이 아는 것, now - 지금 시각, target - 고른 Entity id.
    // 출력: 골랐으면 true.
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
                // Phase 14 D15: never a teammate; a knocked-down enemy stays a target (it can be finished).
                if (!other.IsAlive || IsIgnored(other.EntityId, now) || view.IsTeammate(other.EntityId)) continue;
                float d = Vector3.Distance(view.MyPosition, other.Position);
                if (d > range || d <= floor || d >= bestDistance) continue;
                best = i;
                bestDistance = d;
            }
            if (best < 0) return false;
            if (LineOfSight.Clear(eye, BotAim.Chest(view.Others[best].Position, view.Others[best].Mode), GameMap.Boxes, GameMap.Terrain))
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

    // 기능: 가까운 적이 없고(Phase 14: 팀원은 적이 아니다) 체력·실드가 낮으면 회복 버튼을 고른다.
    // 입력: view - 봇이 아는 것, button - 누를 버튼.
    // 출력: 회복할 것이면 true.
    // Rule 5: no enemy close, and Health or Shield low with the matching item. A running heal keeps the goal.
    private static bool TryHeal(BotView view, out InputButtons button)
    {
        button = InputButtons.None;
        if (!view.HasInventory) return false;
        for (int i = 0; i < view.OtherCount; i++)
        {
            if (view.Others[i].IsAlive && !view.IsTeammate(view.Others[i].EntityId) &&
                Vector3.Distance(view.MyPosition, view.Others[i].Position) < HealSafeDistance) return false;
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
        // Phase 12 D15: stuck, it sprints too, so its unstick jump hurdles a low obstacle and a closed door gives way.
        if (BotAim.HorizontalDistance(me, goal) > SprintDistance || _steering.Stuck) command.Buttons |= InputButtons.Sprint;
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

    // 기능: 표적을 겨누고 쏘며 옆으로 움직인다(Phase 14: 기절한 표적은 낮은 몸 가운데를 겨눈다).
    // 입력: view - 봇이 아는 것, now - 지금 시각, command - 이번 입력.
    // 출력: 반환값 없음.
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
        BotAim.Solve(BotAim.Eye(me), BotAim.Chest(enemy, view.Others[index].Mode), out float yaw, out float pitch);
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
