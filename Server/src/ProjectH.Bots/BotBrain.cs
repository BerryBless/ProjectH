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

    // 기능: 봇 하나의 판단 상태를 시드 고정 난수로 만든다.
    // 입력: seed - 이 봇의 난수 시드(같은 시드는 같은 판단을 낸다).
    // 출력: 목표가 None이고 배치(Deploy) 계획이 없는 두뇌.
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

    // 기능: View를 보고 이번 Tick 입력을 만든다. 낙하 배치 중이면 Deploy, 아니면 DecideInterval마다 Decide로 목표를 다시 정하고 Act로 입력을 채운다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초).
    // 출력: 보낼 입력이 있으면 true와 command, 참가 전이거나 스냅샷이 없으면 false. 목표·조준·버튼 타이머 등 두뇌 상태가 갱신된다.
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

    // 기능: 수송기·자유낙하·활공 중이면 착지 목표와 점프 Tick을 한 번 계획하고, 점프와 낙하 방향 조종 입력을 만든다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초), command - 채울 이번 Tick 입력.
    // 출력: 배치 모드(Transport, Freefall, Glide)면 true와 채워진 command, 다른 모드면 false(배치 계획은 지워진다).
    // D15: the plan is made once per deployment, from what a client knows: the route and the places it has heard of
    // (the POIs and the items the server sent; the loot points are server data). The brain's own seeded Random picks
    // one, so bots spread over the map. The jump is pressed from one tick before the planned tick on, at most every ButtonRepeatSeconds
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

    // 기능: 맵 POI와 알고 있는 월드 아이템 위치 중 하나를 봇 난수로 골라 착지 목표로 정한다.
    // 입력: view - 알고 있는 아이템 목록이 든 봇 정보.
    // 출력: 착지 목표 위치(POI면 지형 높이 위). 이론상 도달하지 않는 경우 Vector3.Zero.
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

    // 기능: 착지 목표를 수송기 경로에 수평 투영해 가장 가까이 지나는 Tick을 구한다.
    // 입력: route - 이번 라운드 수송기 경로, target - 착지 목표 위치.
    // 출력: 점프할 서버 Tick. 경로의 점프 허용 구간 안으로 잘린다.
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

    // 기능: D4 규칙 순서(매치 밖 대기 → 자기장 이동 → 교전 → 치료 → 파밍 → 배회)로 이번 목표를 정한다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초).
    // 출력: 반환값 없음. Goal과 관련 상태(Target, GoalItem, GoalPoint, 치료 버튼, 배회 종료 시각)가 바뀌고, 필요하면 조향 상태가 초기화된다.
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

    // 기능: 이동 목표를 설정한다. 목표 종류가 바뀌었거나 지점이 2 m 넘게 옮겨졌거나 강제면 조향 상태를 새로 시작한다.
    // 입력: goal - 새 목표 종류, point - 목표 지점, me - 봇 현재 위치, now - 현재 시각(초), force - 항상 조향을 초기화할지 여부.
    // 출력: 반환값 없음. Goal·GoalPoint가 바뀌고 조건에 따라 끼임·포기 상태가 초기화된다.
    private void SetGoal(BotGoal goal, Vector3 point, Vector3 me, float now, bool force = false)
    {
        bool moved = BotAim.HorizontalDistance(point, GoalPoint) > 2f;
        if (force || goal != Goal || moved) _steering.Reset(me, point, now);
        Goal = goal;
        GoalPoint = point;
    }

    // 기능: 봇이 다음 안전 구역(여유 거리를 뺀 반경, 최소 2 m) 밖에 있으면 구역 중심을 이동 목표로 준다.
    // 입력: view - 자기장 상태가 든 봇 정보, me - 봇 현재 위치.
    // 출력: 구역 밖이면 true와 지형 높이 위의 구역 중심, 자기장이 없거나 안쪽이면 false.
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

    // 기능: 사거리 안에서 살아 있고 무시 목록에 없으며 시야가 트인 가장 가까운 적을 고른다(가까운 순으로 최대 MaxSightChecks명 시야 검사). 오래 쏴도 맞지 않은 현재 대상은 무시 목록에 넣는다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초).
    // 출력: 대상이 있으면 true와 그 엔티티 ID, 쏠 수 있는 무기가 없거나 대상이 없으면 false.
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

    // 기능: 탄이 남은 보유 무기 중 가장 긴 사거리를 교전 거리로 정한다(EngageRange 상한).
    // 입력: view - 인벤토리·무기 정보가 든 봇 정보.
    // 출력: 교전 거리(m). 쏠 수 있는 무기가 없으면 0.
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

    // 기능: 슬롯의 무기에 탄창 또는 예비 탄약이 남았는지 확인한다(현재 슬롯은 스냅샷의 탄창 값을 쓴다).
    // 입력: view - 봇이 받은 정보, slot - 무기 슬롯 번호, weapon - 그 슬롯의 무기 정보.
    // 출력: 탄창이나 예비 탄약이 하나라도 있으면 true, 없으면 false.
    private static bool HasRounds(BotView view, int slot, WeaponInfo weapon)
    {
        int magazine = slot == view.Inventory.CurrentSlot ? view.Self.Ammo : view.Inventory.GetSlot(slot).MagAmmo;
        return magazine > 0 || view.Reserve(weapon.AmmoType) > 0;
    }

    // 기능: HealSafeDistance 안에 살아 있는 적이 없을 때 체력·실드가 낮고 맞는 회복 아이템이 있으면 치료를 고른다.
    // 입력: view - 봇이 받은 정보.
    // 출력: 치료할 때 true와 누를 버튼(Medkit 또는 Shield Cell; 이미 사용 중이면 None), 아니면 false.
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

    // 기능: LootSearchRange 안에서 쓸모 있고 블랙리스트에 없는 가장 가까운 아이템을 고른다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초, 블랙리스트 만료 판정용).
    // 출력: 찾으면 true와 아이템 ID·위치, 없으면 false.
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

    // 기능: 월드 아이템이 지금 봇에게 쓸모 있는지 판단한다.
    // 입력: view - 인벤토리·카탈로그가 든 봇 정보, item - 판단할 월드 아이템.
    // 출력: 무기는 빈 슬롯이 있을 때, 탄약은 그 탄을 쓰는 무기가 있고 예비가 최대치 미만일 때, 소모품은 최대 개수 미만일 때 true. 그 외 false.
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

    // 기능: 아이템 카탈로그에서 탄종별 최대 보유량을 찾는다.
    // 입력: view - 카탈로그가 든 봇 정보, type - 탄약 종류.
    // 출력: 최대 보유량. 카탈로그가 없거나 항목이 없으면 int.MaxValue(제한 없음으로 취급).
    private static int AmmoMax(BotView view, AmmoType type)
    {
        if (view.Catalog == null) return int.MaxValue;
        foreach (AmmoInfo info in view.Catalog.Ammo)
        {
            if (info.Type == type) return info.Max;
        }
        return int.MaxValue;
    }

    // 기능: 아이템 카탈로그에서 소모품별 최대 소지 개수를 찾는다.
    // 입력: view - 카탈로그가 든 봇 정보, type - 소모품 종류.
    // 출력: 최대 소지 개수. 카탈로그가 없거나 항목이 없으면 int.MaxValue(제한 없음으로 취급).
    private static int ConsumableMax(BotView view, ConsumableType type)
    {
        if (view.Catalog == null) return int.MaxValue;
        foreach (ConsumableInfo info in view.Catalog.Consumables)
        {
            if (info.Type == type) return info.MaxStack;
        }
        return int.MaxValue;
    }

    // 기능: 배회할 무작위 지점을 고른다(다음 안전 구역 반경의 80 % 안, 자기장이 없으면 맵 중심에서 WanderRadiusNoZone 안).
    // 입력: view - 자기장 상태가 든 봇 정보.
    // 출력: 맵 경계 5 m 안쪽으로 잘린, 지형 높이 위의 배회 지점.
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

    // 기능: 현재 목표(교전, 치료, 파밍, 자기장·배회 이동)에 맞게 이번 Tick 입력을 채운다. 교전이 아니면 정면을 조준한다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초), command - 채울 이번 Tick 입력.
    // 출력: 반환값 없음. command의 이동·조준·버튼이 채워지고 몸 방향(_bodyYaw)이 갱신된다. 이동 포기 시 배회 목표를 바꾸거나 자기장 목표의 조향을 다시 시작한다.
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

    // 기능: 조향 결과로 목표를 향해 전진하는 입력을 만든다. 멀거나 끼었으면 달리고, 조향이 원하면 점프한다.
    // 입력: me - 봇 현재 위치, goal - 목표 지점, now - 현재 시각(초), command - 채울 이번 Tick 입력.
    // 출력: 반환값 없음. command의 방향·전진·Sprint·Jump가 채워지고, giveUp - 목표에 도달할 수 없어 보이면 true.
    private void Walk(Vector3 me, Vector3 goal, float now, ref InputCommand command, out bool giveUp)
    {
        _steering.Steer(me, goal, now, _rng, out float yaw, out bool jump, out giveUp);
        command.Yaw = yaw;
        command.MoveY = 1f;
        // Phase 12 D15: stuck, it sprints too, so its unstick jump hurdles a low obstacle and a closed door gives way.
        if (BotAim.HorizontalDistance(me, goal) > SprintDistance || _steering.Stuck) command.Buttons |= InputButtons.Sprint;
        if (jump) command.Buttons |= InputButtons.Jump;
    }

    // 기능: 목표 아이템까지 걸어가고, 집을 수 있는 거리에 들면 ButtonRepeatSeconds 간격으로 Interact를 최대 MaxInteractTries번 누른다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초), command - 채울 이번 Tick 입력.
    // 출력: 반환값 없음. command에 이동 또는 Interact가 채워진다. 아이템이 사라지면 목표가 None이 되고, 도달 포기·반복 거절이면 아이템이 블랙리스트에 오른다.
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

    // 기능: 대상의 가슴을 오차를 섞어 조준하고 좌우로 움직이며 거리를 맞춘다. 탄이 없으면 무기를 바꾸거나 재장전하고, 아니면 사격한다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초), command - 채울 이번 Tick 입력.
    // 출력: 반환값 없음. command의 조준·이동·버튼(Fire, Reload, Slot)이 채워지고 조준 오차·좌우 이동·사격 횟수 상태가 갱신된다. 대상이 사라졌거나 죽었으면 목표가 None이 된다.
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

    // 기능: 최신 스냅샷의 다른 플레이어 배열에서 엔티티 ID의 위치를 찾는다.
    // 입력: view - 봇이 받은 정보, entityId - 찾을 엔티티 ID.
    // 출력: Others 배열 안의 인덱스. 없으면 -1.
    private static int FindOther(BotView view, ushort entityId)
    {
        for (int i = 0; i < view.OtherCount; i++)
        {
            if (view.Others[i].EntityId == entityId) return i;
        }
        return -1;
    }

    // 기능: 대상을 IgnoreTargetSeconds 동안 무시 목록에 넣는다(고정 크기 Memory 링 버퍼, 가장 오래된 칸을 덮어씀).
    // 입력: entityId - 무시할 엔티티 ID, now - 현재 시각(초).
    // 출력: 반환값 없음. 무시 목록 한 칸이 갱신된다.
    private void Ignore(ushort entityId, float now)
    {
        _ignoredTargets[_nextIgnore] = entityId;
        _ignoredUntil[_nextIgnore] = now + IgnoreTargetSeconds;
        _nextIgnore = (_nextIgnore + 1) % Memory;
    }

    // 기능: 대상이 아직 무시 기간 안에 있는지 확인한다.
    // 입력: entityId - 확인할 엔티티 ID, now - 현재 시각(초).
    // 출력: 만료되지 않은 무시 항목이 있으면 true, 없으면 false.
    private bool IsIgnored(ushort entityId, float now)
    {
        for (int i = 0; i < Memory; i++)
        {
            if (_ignoredTargets[i] == entityId && now < _ignoredUntil[i]) return true;
        }
        return false;
    }

    // 기능: 아이템을 ItemBlacklistSeconds 동안 파밍 대상에서 뺀다(고정 크기 Memory 링 버퍼, 가장 오래된 칸을 덮어씀).
    // 입력: itemId - 제외할 아이템 ID, now - 현재 시각(초).
    // 출력: 반환값 없음. 블랙리스트 한 칸이 갱신된다.
    private void Blacklist(ushort itemId, float now)
    {
        _blacklistedItems[_nextBlacklist] = itemId;
        _blacklistedUntil[_nextBlacklist] = now + ItemBlacklistSeconds;
        _nextBlacklist = (_nextBlacklist + 1) % Memory;
    }

    // 기능: 아이템이 아직 블랙리스트 기간 안에 있는지 확인한다.
    // 입력: itemId - 확인할 아이템 ID, now - 현재 시각(초).
    // 출력: 만료되지 않은 블랙리스트 항목이 있으면 true, 없으면 false.
    private bool IsBlacklisted(ushort itemId, float now)
    {
        for (int i = 0; i < Memory; i++)
        {
            if (_blacklistedItems[i] == itemId && now < _blacklistedUntil[i]) return true;
        }
        return false;
    }
}
