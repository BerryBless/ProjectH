using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// Stress (D38): a group behaviour. It sets one headless actor's intent (move target or vector, aim, held buttons, timed
// presses, build requests) every pump tick; the actor turns that intent into the real input it sends, exactly as for the
// scenario's own actions (steering, fire interval presses, build mode and aim). No thread, no HTTP: everything comes
// from what the actor's client knows (its BotView), plus, for the group's partners, their published ActorState.
//
// Ownership: made on the runner flow, handed to the actor by SetBrainCommand, from then on used by the pump thread only
// (Think). Randomness comes from the brain's own Random seeded from the scenario seed and the actor's index, so the same
// scenario seed gives the same choices (the timing of the world still varies run to run).
public abstract class ActorBrain
{
    public abstract string Role { get; }

    // Called every tick the actor sends an input (joined, with a snapshot, not paused), before the input is built.
    internal abstract void Think(HeadlessActor actor, BotView view, float now);

    // 기능: (시나리오 시드, 액터 index, 용도)를 섞어 안정적인 난수 시드를 만든다.
    // 입력: seed - 시나리오 시드, index - 액터 index, salt - 용도 구분값.
    // 출력: 32비트 시드.
    // A stable seed per (scenario seed, actor index, purpose): string hashes are randomized per process, so mix ints.
    public static int Seed(int seed, int index, int salt) => unchecked((int)(((uint)seed * 2654435761u) ^ ((uint)(index + 1) * 40503u) ^ ((uint)salt * 97u)));

    // 기능: 슬롯 번호를 슬롯 키 버튼으로 바꾼다.
    // 입력: slot - 슬롯 번호(0..2).
    // 출력: 0이면 Slot1, 1이면 Slot2, 그 외 Slot3.
    protected static InputButtons SlotButton(int slot) => slot switch { 0 => InputButtons.Slot1, 1 => InputButtons.Slot2, _ => InputButtons.Slot3 };
}

// ---- moving ----

public enum MovePattern
{
    Clockwise,
    CounterClockwise,
    Radial,
    Random,
}

// §11-13: walk from waypoint to waypoint for the whole run. Waypoints come from the pattern around (Center, Radius); each
// actor starts at its own angle (Index of Count) and on its own ring, so a group spreads over the area instead of
// gathering in one place. A waypoint not reached in time (or the steering gave up) is skipped.
public sealed record MovePlan(MovePattern Pattern, Vector2 Center, float Radius, bool Sprint, float JumpEverySeconds, int Index, int Count, int Seed);

public sealed class MoveBrain : ActorBrain
{
    public const float StepDegrees = 30f;
    private const float Tolerance = 1.5f;
    private const float AssumedSpeed = 3f;   // m/s for the waypoint deadline (walking is ~4.5, sprinting ~6.5)

    private readonly MovePlan _plan;
    private readonly Random _rng;
    private readonly string _role;
    private int _waypoint;
    private Vector3? _target;
    private float _deadline;
    private float _nextJump;

    // 기능: 이동 뇌를 만든다.
    // 입력: plan - 이동 계획, role - 보고용 역할 이름.
    // 출력: 첫 점프 시각이 index로 흩어진 MoveBrain.
    public MoveBrain(MovePlan plan, string role = "move")
    {
        _plan = plan;
        _rng = new Random(Seed(plan.Seed, plan.Index, 11));
        _role = role;
        _nextJump = plan.JumpEverySeconds > 0 ? plan.JumpEverySeconds * (1f + (plan.Index % 7) / 7f) : float.MaxValue;
    }

    public override string Role => _role;

    // 기능: 이 액터의 k번째 Waypoint를 패턴에 따라 계산한다(Random 패턴만 rng를 쓴다).
    // 입력: plan - 이동 계획, k - Waypoint 순번, rng - Random 패턴용 난수.
    // 출력: 맵 안(Margin 포함) 지면 위의 위치.
    // The k-th waypoint of this actor (pure for the patterns, the Random for Random). Inside the map, on the ground.
    public static Vector3 Waypoint(MovePlan plan, int k, Random rng)
    {
        float phase = 360f * plan.Index / Math.Max(1, plan.Count);
        // Rings between 55 % and 100 % of the radius, spread by the golden ratio over the actors.
        float ring = plan.Radius * (0.55f + 0.45f * Frac(plan.Index * 0.618034f + plan.Seed * 0.0001f));
        float angle, r;
        switch (plan.Pattern)
        {
            case MovePattern.Clockwise:
                angle = phase + k * StepDegrees;
                r = ring;
                break;
            case MovePattern.CounterClockwise:
                angle = phase - k * StepDegrees;
                r = ring;
                break;
            case MovePattern.Radial:
                angle = phase + (k / 2) * StepDegrees * 0.5f;
                r = k % 2 == 0 ? ring : plan.Radius * 0.15f;
                break;
            default:
                angle = (float)rng.NextDouble() * 360f;
                r = plan.Radius * MathF.Sqrt((float)rng.NextDouble());
                break;
        }
        float rad = angle * MathF.PI / 180f;
        float limit = GameMap.HalfSize - StressMap.Margin;
        float x = Math.Clamp(plan.Center.X + MathF.Sin(rad) * r, -limit, limit);
        float z = Math.Clamp(plan.Center.Y + MathF.Cos(rad) * r, -limit, limit);
        return StressMap.Ground(x, z);
    }

    // 기능: 소수부를 구한다.
    // 입력: v - 실수.
    // 출력: v - floor(v).
    private static float Frac(float v) => v - MathF.Floor(v);

    // 기능: Waypoint에 도달·포기·기한 초과했으면 다음 Waypoint(설 수 없는 곳은 몇 번 건너뜀)를 고르고, 이동 의도와 주기적 점프를 액터에 건다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. 액터의 이동 목표와 점프 스크립트가 갱신된다(죽어 있으면 아무것도 안 함).
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        if (!view.Alive) return;
        if (_target == null || actor.MoveArrived || actor.MoveGaveUp || now > _deadline)
        {
            // A waypoint inside a building is skipped (a few tries; the steering would only give up on it).
            Vector3 next = Waypoint(_plan, _waypoint++, _rng);
            for (int i = 0; i < 4 && !StressMap.CanStand(next); i++) next = Waypoint(_plan, _waypoint++, _rng);
            _target = next;
            _deadline = now + BotAim.HorizontalDistance(view.MyPosition, next) / AssumedSpeed + 4f;
        }
        actor.BrainMoveTo(_target, Tolerance, _plan.Sprint);
        if (now >= _nextJump && actor.ScriptIdle)
        {
            actor.BrainScript(new[] { new InputStep(InputButtons.Jump, 1), new InputStep(InputButtons.None, 1) });
            _nextJump = now + _plan.JumpEverySeconds;
        }
    }
}

// ---- combat ----

// §14-17: real fights. The actor aims at its partner as its own snapshots show it (or walks to it when out of range or
// out of sight), strafes, and fires bursts of single presses that respect the weapon's fire interval (FirePress), with
// a pause between bursts and a reload every ReloadEvery bursts. HitPercent of the bursts aim at the chest; the rest aim
// 3 m above the target's head: those shots still go through the server's hitscan and lag compensation but deal no
// damage, which keeps deaths (and the re-arming after them) to a rate a long run can sustain.
public sealed record CombatPlan(int Slot, int Burst, float PauseSeconds, int ReloadEvery, int HitPercent, float EngageRange, float StrafeSeconds, int Index, int Seed);

public sealed class CombatBrain : ActorBrain
{
    private const float MissHeight = 3.5f;
    private const float ButtonRepeat = 0.5f;

    private readonly CombatPlan _plan;
    private readonly IQaActor _target;
    private readonly Random _rng;
    private float _strafe = 1f;
    private float _nextStrafeFlip;
    private float _nextBurst;
    private float _nextButton;
    private bool _firing;
    private bool _burstHits = true;
    private int _bursts;

    // 기능: 전투 뇌를 만든다.
    // 입력: plan - 전투 계획, target - 상대 액터.
    // 출력: strafe 방향과 첫 burst 시각이 난수로 정해진 CombatBrain.
    public CombatBrain(CombatPlan plan, IQaActor target)
    {
        _plan = plan;
        _target = target;
        _rng = new Random(Seed(plan.Seed, plan.Index, 23));
        if (_rng.Next(2) == 0) _strafe = -1f;
        _nextBurst = (float)_rng.NextDouble() * plan.PauseSeconds;
    }

    public override string Role => "combat";

    // 기능: 상대가 없거나 죽었으면 대기, 멀거나 안 보이면 접근, 사거리 안이면 strafe하며 무기를 들고 burst·reload 스크립트를 건다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. 액터의 이동·조준·입력 스크립트와 burst 상태가 갱신된다.
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        if (!view.Alive)
        {
            _firing = false;
            return;
        }
        ActorState t = _target.State;
        if (!t.Joined || t.EntityId == 0 || !t.Alive)
        {
            Idle(actor);
            return;
        }
        bool visible = view.TryGetOther(t.EntityId, out SnapshotEntity seen);
        if (visible && !seen.IsAlive)
        {
            Idle(actor);
            return;
        }
        Vector3 me = view.MyPosition;
        Vector3 there = visible ? seen.Position : t.Position.ToVector3();
        float distance = BotAim.HorizontalDistance(me, there);
        if (!visible || distance > _plan.EngageRange)
        {
            // Walk towards the partner (the target point moves with it, a few metres at a time).
            Vector3? goal = actor.MoveTarget;
            if (goal == null || BotAim.HorizontalDistance(goal.Value, there) > 3f) goal = there;
            actor.BrainVector(0f, 0f);
            actor.BrainMoveTo(goal, _plan.EngageRange * 0.5f, sprint: true);
            actor.BrainAimActor(t.EntityId, _target);
            return;
        }
        actor.BrainMoveTo(null, 1f, false);
        if (now >= _nextStrafeFlip)
        {
            _strafe = -_strafe;
            _nextStrafeFlip = now + _plan.StrafeSeconds * (0.75f + 0.5f * (float)_rng.NextDouble());
        }
        actor.BrainVector(_plan.StrafeSeconds > 0 ? _strafe : 0f, 0f);
        Aim(actor, t.EntityId, there);

        // The weapon in hand: the slot key also brings it back from the build or harvest tool.
        if (view.Self.Tool != ToolKind.Weapon || view.Inventory.CurrentSlot != _plan.Slot)
        {
            if (actor.ScriptIdle && now >= _nextButton)
            {
                actor.BrainScript(new[] { new InputStep(SlotButton(_plan.Slot), 1), new InputStep(InputButtons.None, 2) });
                _nextButton = now + ButtonRepeat;
            }
            return;
        }
        if (view.WeaponInSlot(_plan.Slot) == null) return;   // unarmed (just respawned): the group's re-arm gives it back
        if (_firing)
        {
            if (!actor.ScriptIdle) return;
            _firing = false;
            _nextBurst = now + _plan.PauseSeconds;
        }
        if (view.Self.ReloadRemainingTicks > 0 || !actor.ScriptIdle || now < _nextBurst) return;
        if (view.Self.Ammo == 0 || (_plan.ReloadEvery > 0 && _bursts > 0 && _bursts % _plan.ReloadEvery == 0))
        {
            _bursts++;
            actor.BrainScript(new[] { new InputStep(InputButtons.Reload, 1), new InputStep(InputButtons.None, 1) });
            _nextBurst = now + _plan.PauseSeconds;
            return;
        }
        _bursts++;
        // The burst's aim is decided before its first press, which goes out with this tick's input.
        _burstHits = _rng.Next(100) < _plan.HitPercent;
        Aim(actor, t.EntityId, there);
        int presses = Math.Max(1, Math.Min(_plan.Burst, view.Self.Ammo));
        var steps = new InputStep[presses];
        for (int i = 0; i < presses; i++) steps[i] = new InputStep(InputButtons.Fire, 1, FirePress: true);
        actor.BrainScript(steps);
        _firing = true;
    }

    // 기능: burst 판정에 따라 상대 가슴 또는 MissHeight 위 지점을 조준한다.
    // 입력: actor - 조종할 액터, entity - 상대 Entity ID, there - 상대 위치.
    // 출력: 반환값 없음. 액터의 조준 의도가 바뀐다.
    // At the chest as this client sees the partner, or (a miss) MissHeight above where it stands.
    private void Aim(HeadlessActor actor, ushort entity, Vector3 there)
    {
        if (_burstHits) actor.BrainAimActor(entity, _target);
        else actor.BrainAimPoint(there + new Vector3(0f, MissHeight, 0f));
    }

    // 기능: 이동·strafe·조준 의도를 모두 푼다.
    // 입력: actor - 조종할 액터.
    // 출력: 반환값 없음.
    private static void Idle(HeadlessActor actor)
    {
        actor.BrainMoveTo(null, 1f, false);
        actor.BrainVector(0f, 0f);
        actor.BrainClearAim();
    }
}

// ---- building ----

// §20-23: real build requests (BotBuilder's order: build mode, aim at the piece, send after the input that carries the
// aim) at RatePerSecond at most, on one site at a time from the run's BuildSitePool (so two builders never build on the
// same cells while free sites remain). The actor walks to each site's stand point first; a site it cannot reach in time
// is given up. First: the site claimed at setup (null = claim the nearest free one on the first tick).
// Recycle: instead of moving on, the builder takes its finished site down with its weapon (real shots at its own
// level-0 pieces, nearest first; what stood on them collapses through the server's support rules) and builds it again,
// so a long run keeps placing, destroying and collapsing pieces without running out of free ground.
public sealed record BuildBrainPlan(BuildSitePool Pool, StressMap.BuildSite? First, IReadOnlyCollection<BuildPieceType> Types, BuildMaterialType Material,
    float RatePerSecond, int Index, bool Recycle = false, int Slot = 0);

public sealed class BuildBrain : ActorBrain
{
    private const float StandTolerance = 0.6f;
    private const float MovedAway = 3f;
    private const float ResultWaitSeconds = 2f;
    private const float CollapseWaitSeconds = 1f;
    private const int MaxPressesPerPiece = 40;
    private const int BurstPresses = 4;
    private const float ButtonRepeat = 0.5f;

    private readonly BuildBrainPlan _plan;
    private readonly string _role;
    private StressMap.BuildSite? _site;
    private BuildPlan[] _pieces = Array.Empty<BuildPlan>();
    private int[] _sequences = Array.Empty<int>();
    private int _piece;
    private float _nextRequest;
    private float _siteDeadline = float.MaxValue;
    private bool _started;
    // Recycling: the pieces to shoot (indices into _pieces, level 0, nearest first), the one being shot, and its ids.
    private bool _demolishing;
    private float _phaseUntil;
    private readonly List<(int Index, uint Id)> _targets = new();
    private int _target;
    private int _pressesOnTarget;
    private float _nextButton;

    // 기능: 건설 뇌를 만든다.
    // 입력: plan - 건설 계획, role - 보고용 역할 이름.
    // 출력: 아직 사이트를 잡지 않은 BuildBrain.
    public BuildBrain(BuildBrainPlan plan, string role = "build")
    {
        _plan = plan;
        _role = role;
    }

    public override string Role => _role;

    // 기능: 사이트를 잡고 stand 지점까지 걸어간 뒤 RatePerSecond로 조각 요청을 보내며, 끝나면 다음 사이트로 가거나(Recycle) 철거 단계로 넘어간다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. 사이트·조각 진행 상태와 액터 의도가 갱신된다(죽으면 진행이 초기화된다).
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        if (!view.Alive)
        {
            // A death drops the site in progress; the next life starts the same site over.
            _demolishing = false;
            _piece = 0;
            return;
        }
        if (_site == null) Take(_started ? null : _plan.First, view.MyPosition);
        if (_site == null) return;
        StressMap.BuildSite site = _site;
        float away = BotAim.HorizontalDistance(view.MyPosition, site.Stand);
        // Moved off the stand point mid-site (a match reset put everyone on a drop point, a push): start the site over
        // from walking there, instead of sending requests from out of range.
        if (away > MovedAway && actor.BuildsQueued == 0 && (_piece > 0 || _demolishing))
        {
            _demolishing = false;
            _phaseUntil = 0;
            _piece = 0;
            actor.BrainClearAim();
        }
        if (_demolishing)
        {
            Demolish(actor, view, now);
            return;
        }
        if (_piece == 0 && away > StandTolerance)
        {
            if (actor.MoveTarget == null) _siteDeadline = now + away / 3f + 6f;
            actor.BrainMoveTo(site.Stand, StandTolerance * 0.5f, sprint: away > 10f);
            if (actor.MoveGaveUp || now > _siteDeadline) Next(actor, view.MyPosition, gaveUp: true);
            return;
        }
        actor.BrainMoveTo(null, 1f, false);
        if (_piece >= _pieces.Length)
        {
            if (actor.BuildsQueued > 0) return;
            if (!_plan.Recycle)
            {
                Next(actor, view.MyPosition);
                return;
            }
            // Every answer in (or ResultWaitSeconds), then take down what was placed.
            if (_phaseUntil == 0) _phaseUntil = now + ResultWaitSeconds;
            bool answered = _sequences.All(seq => seq < 0 || actor.TryBuildResult(seq, out _));
            if (!answered && now < _phaseUntil) return;
            StartDemolish(actor, view, now);
            return;
        }
        if (actor.BuildsQueued > 0 || now < _nextRequest) return;
        _sequences[_piece] = actor.BrainBuild(_pieces[_piece]);
        if (_sequences[_piece] >= 0) _piece++;
        _nextRequest = now + 1f / MathF.Max(0.05f, _plan.RatePerSecond);
    }

    // 기능: 응답이 Ok인 level-0 조각들을 가까운 순으로 철거 목표로 잡고 철거 단계를 시작한다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(사용하지 않음).
    // 출력: 반환값 없음. _targets가 채워지고 _demolishing이 켜진다.
    private void StartDemolish(HeadlessActor actor, BotView view, float now)
    {
        _targets.Clear();
        Vector3 eye = BotAim.Eye(view.MyPosition);
        for (int i = 0; i < _pieces.Length; i++)
        {
            if (_pieces[i].Y != 0 || _sequences[i] < 0) continue;
            if (actor.TryBuildResult(_sequences[i], out BuildResultInfo r) && r.Code == "Ok" && r.PieceId != 0) _targets.Add((i, r.PieceId));
        }
        _targets.Sort((a, b) => Vector3.DistanceSquared(eye, _pieces[a.Index].AimAt.ToVector3()).CompareTo(Vector3.DistanceSquared(eye, _pieces[b.Index].AimAt.ToVector3())));
        _target = 0;
        _pressesOnTarget = 0;
        _demolishing = true;
        _phaseUntil = 0;
    }

    // 기능: 철거 목표를 하나씩 조준해 무기로 짧은 burst를 쏘고, 모두 사라지거나 한도에 닿으면 잠시 기다린 뒤 같은 사이트를 다시 짓게 한다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. 목표 index·press 수·단계 상태와 액터 의도가 갱신된다.
    // One target at a time: weapon out (slot key), aim at the piece, short bursts until the client hears it destroyed
    // (BotView.Pieces drops it) or MaxPressesPerPiece; then a moment for the collapse, then the same site again.
    private void Demolish(HeadlessActor actor, BotView view, float now)
    {
        actor.BrainMoveTo(null, 1f, false);
        while (_target < _targets.Count && (!view.Pieces.Contains(_targets[_target].Id) || _pressesOnTarget >= MaxPressesPerPiece))
        {
            _target++;
            _pressesOnTarget = 0;
        }
        if (_target >= _targets.Count)
        {
            if (_phaseUntil == 0) _phaseUntil = now + CollapseWaitSeconds;
            if (now < _phaseUntil || !actor.ScriptIdle) return;
            actor.BrainClearAim();
            _demolishing = false;
            _phaseUntil = 0;
            _piece = 0;
            return;
        }
        actor.BrainAimPoint(_pieces[_targets[_target].Index].AimAt.ToVector3());
        if (view.Self.Tool != ToolKind.Weapon || view.Inventory.CurrentSlot != _plan.Slot)
        {
            if (actor.ScriptIdle && now >= _nextButton)
            {
                actor.BrainScript(new[] { new InputStep(SlotButton(_plan.Slot), 1), new InputStep(InputButtons.None, 2) });
                _nextButton = now + ButtonRepeat;
            }
            return;
        }
        if (view.WeaponInSlot(_plan.Slot) == null || !actor.ScriptIdle || view.Self.ReloadRemainingTicks > 0) return;
        if (view.Self.Ammo == 0)
        {
            if (now >= _nextButton)
            {
                actor.BrainScript(new[] { new InputStep(InputButtons.Reload, 1), new InputStep(InputButtons.None, 1) });
                _nextButton = now + ButtonRepeat;
            }
            return;
        }
        int presses = Math.Min(BurstPresses, (int)view.Self.Ammo);
        var steps = new InputStep[presses];
        for (int i = 0; i < presses; i++) steps[i] = new InputStep(InputButtons.Fire, 1, FirePress: true);
        actor.BrainScript(steps);
        _pressesOnTarget += presses;
    }

    // 기능: 사이트를 정하고(first가 있으면 그것, 없으면 pool에서 가까운 것을 claim) 조각 목록과 진행 상태를 초기화한다.
    // 입력: first - 지정 사이트(null이면 claim), me - 내 위치, avoid - claim에서 피할 사이트.
    // 출력: 반환값 없음. _site·_pieces·_sequences가 다시 잡힌다(pool이 비면 _site는 null).
    private void Take(StressMap.BuildSite? first, Vector3 me, StressMap.BuildSite? avoid = null)
    {
        _started = true;
        _site = first ?? _plan.Pool.Claim(new Vector2(me.X, me.Z), avoid);
        _pieces = _site != null ? BuildSitePool.PiecesOf(_site, _plan.Types, _plan.Material) : Array.Empty<BuildPlan>();
        _sequences = new int[_pieces.Length];
        _piece = 0;
        _phaseUntil = 0;
        _demolishing = false;
        _siteDeadline = float.MaxValue;
    }

    // 기능: 현재 사이트를 떠나 다음 사이트를 잡는다(포기한 사이트는 pool에 돌려주고 다음 claim에서 피한다).
    // 입력: actor - 조종할 액터, me - 내 위치, gaveUp - 도달하지 못해 포기했으면 true.
    // 출력: 반환값 없음. 이동 목표가 풀리고 새 사이트가 잡힌다.
    // A finished site stays held (its pieces stand there); one the builder could not reach goes back to the pool, and the
    // next claim avoids it.
    private void Next(HeadlessActor actor, Vector3 me, bool gaveUp = false)
    {
        actor.BrainMoveTo(null, 1f, false);
        StressMap.BuildSite? old = _site;
        if (gaveUp && old != null) _plan.Pool.Release(old);
        Take(null, me, gaveUp ? old : null);
    }
}

// ---- looting ----

// §30: walk to the nearest item this client knows of, press Interact next to it (the server picks the nearest item),
// every DropEvery pickups press Drop (the current weapon) and, hurt with a Medkit, use it (with a Shield Cell and the
// shield below full, use that). Items that do not go away after a few tries are skipped for a while. With nothing in
// reach the actor wanders around where it is. Stats (optional): the group's loot counters (Interlocked, pump thread).
public sealed record LootPlan(float SearchRange, int DropEvery, int Index, int Seed, GroupStats? Stats = null);

public sealed class LootBrain : ActorBrain
{
    private const float Reach = 1.2f;
    private const float ButtonRepeat = 0.5f;
    private const int MaxTries = 3;
    private const int Memory = 16;
    private const float SkipSeconds = 30f;
    private const float UseRepeat = 4f;   // longer than a medkit's use time, so a use is not cancelled by the next press

    private readonly LootPlan _plan;
    private float _nextUse;
    private readonly Random _rng;
    private readonly ushort[] _skipped = new ushort[Memory];
    private readonly float[] _skippedUntil = new float[Memory];
    private int _nextSkip;
    private ushort _item;
    private float _itemDeadline;
    private int _tries;
    private float _nextButton;
    private int _pickups;
    private Vector3? _wander;

    // 기능: 루팅 뇌를 만든다.
    // 입력: plan - 루팅 계획.
    // 출력: LootBrain.
    public LootBrain(LootPlan plan)
    {
        _plan = plan;
        _rng = new Random(Seed(plan.Seed, plan.Index, 37));
    }

    public override string Role => "loot";

    // 기능: 목표 아이템이 사라지면 pickup으로 세고(DropEvery마다 Drop), 소모품이 있으면 쓰고, 가장 가까운 아이템으로 가 Interact를 누르며, 없으면 주변을 배회한다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. 목표 아이템·skip 목록·Stats 카운터와 액터 의도가 갱신된다.
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        if (!view.Alive) return;
        Vector3 me = view.MyPosition;
        if (_item != 0 && !view.Items.ContainsKey(_item))
        {
            // Gone: most likely picked up by us (or by someone else; either way the world changed through real input).
            _item = 0;
            _pickups++;
            if (_plan.Stats != null) Interlocked.Increment(ref _plan.Stats.LootPickups);
            if (_plan.DropEvery > 0 && _pickups % _plan.DropEvery == 0 && Press(actor, InputButtons.Drop, now, force: true) && _plan.Stats != null)
                Interlocked.Increment(ref _plan.Stats.LootDrops);
        }
        // A consumable in the bag and something to restore: use it (one press per ButtonRepeat; the server applies it
        // over its use time and refuses a use at full health or shield).
        InputButtons use = view.Inventory.Medkits > 0 && view.Self.Health < 100 ? InputButtons.UseMedkit
            : view.Inventory.ShieldCells > 0 && view.Self.Shield < 100 ? InputButtons.UseShieldCell : InputButtons.None;
        if (use != InputButtons.None && now >= _nextUse && Press(actor, use, now, force: true))
        {
            _nextUse = now + UseRepeat;
            if (_plan.Stats != null) Interlocked.Increment(ref _plan.Stats.LootUses);
        }
        if (_item != 0 && now > _itemDeadline) Skip(now);
        if (_item == 0) Pick(view, me, now);
        if (_item == 0)
        {
            if (_wander == null || actor.MoveArrived || actor.MoveGaveUp)
            {
                float a = (float)_rng.NextDouble() * MathF.PI * 2f;
                float limit = GameMap.HalfSize - StressMap.Margin;
                _wander = StressMap.Ground(Math.Clamp(me.X + MathF.Sin(a) * 15f, -limit, limit), Math.Clamp(me.Z + MathF.Cos(a) * 15f, -limit, limit));
            }
            actor.BrainMoveTo(_wander, 1.5f, false);
            return;
        }
        _wander = null;
        Vector3 at = view.Items[_item].Position;
        if (BotAim.HorizontalDistance(me, at) > Reach)
        {
            actor.BrainMoveTo(at, Reach * 0.6f, sprint: true);
            return;
        }
        actor.BrainMoveTo(null, 1f, false);
        if (Press(actor, InputButtons.Interact, now, force: false) && ++_tries > MaxTries) Skip(now);
    }

    // 기능: 스크립트가 비어 있고(force가 아니면 ButtonRepeat도 지났을 때) 버튼을 한 번 누르는 스크립트를 건다.
    // 입력: actor - 조종할 액터, button - 누를 버튼, now - 현재 시각, force - 반복 간격을 무시할지.
    // 출력: 눌렀으면 true, 아니면 false.
    private bool Press(HeadlessActor actor, InputButtons button, float now, bool force)
    {
        if (!actor.ScriptIdle || (!force && now < _nextButton)) return false;
        actor.BrainScript(new[] { new InputStep(button, 1), new InputStep(InputButtons.None, 1) });
        _nextButton = now + ButtonRepeat;
        return true;
    }

    // 기능: skip되지 않은 아이템 중 SearchRange 안에서 가장 가까운 것을 목표로 고른다.
    // 입력: view - 액터 Client가 아는 월드, me - 내 위치, now - 현재 시각.
    // 출력: 반환값 없음. _item(없으면 0)·_tries·_itemDeadline이 설정된다.
    private void Pick(BotView view, Vector3 me, float now)
    {
        float best = _plan.SearchRange;
        ushort pick = 0;
        foreach (KeyValuePair<ushort, WorldItemData> pair in view.Items)
        {
            if (IsSkipped(pair.Key, now)) continue;
            float d = BotAim.HorizontalDistance(me, pair.Value.Position);
            if (d < best)
            {
                best = d;
                pick = pair.Key;
            }
        }
        _item = pick;
        _tries = 0;
        _itemDeadline = now + best / 3f + 5f;
    }

    // 기능: 현재 목표 아이템을 SkipSeconds 동안 건너뛰도록 기록하고 목표를 비운다.
    // 입력: now - 현재 시각.
    // 출력: 반환값 없음. skip 목록(오래된 항목 덮어씀)과 _item이 바뀐다.
    private void Skip(float now)
    {
        _skipped[_nextSkip] = _item;
        _skippedUntil[_nextSkip] = now + SkipSeconds;
        _nextSkip = (_nextSkip + 1) % Memory;
        _item = 0;
    }

    // 기능: 아이템이 아직 skip 기간인지 본다.
    // 입력: item - 아이템 ID, now - 현재 시각.
    // 출력: skip 기한 안이면 true.
    private bool IsSkipped(ushort item, float now)
    {
        for (int i = 0; i < Memory; i++) if (_skipped[i] == item && _skippedUntil[i] > now) return true;
        return false;
    }
}

// ---- idle ----

// §10: only the keepalive input (the actor sends one every tick anyway).
public sealed class IdleBrain : ActorBrain
{
    public override string Role => "idle";

    // 기능: 아무 의도도 걸지 않는다(keepalive 입력만 나간다).
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(모두 사용하지 않음).
    // 출력: 반환값 없음.
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
    }
}

// ---- roles ----

// §51-52: an actor that changes role every SwitchSeconds. The role of period p is a weighted pick from (seed, index, p),
// so the same seed gives the same schedule. Each role keeps its own brain (its waypoints, its build site) between turns.
public sealed record RoleShare(string Role, double Weight);

public sealed class RoleBrain : ActorBrain
{
    private readonly IReadOnlyList<RoleShare> _roles;
    private readonly Func<string, ActorBrain> _make;
    private readonly Dictionary<string, ActorBrain> _brains = new(StringComparer.Ordinal);   // at most one per role
    private readonly float _switchSeconds;
    private readonly int _seed;
    private readonly int _index;
    private int _period = -1;
    private ActorBrain? _current;
    private float _start = -1f;

    // 기능: 역할 전환 뇌를 만든다.
    // 입력: roles - 역할과 가중치, switchSeconds - 전환 주기(0 이하면 전환 없음), seed - 시나리오 시드, index - 액터 index, make - 역할 이름으로 뇌를 만드는 함수.
    // 출력: 아직 역할이 없는 RoleBrain.
    public RoleBrain(IReadOnlyList<RoleShare> roles, float switchSeconds, int seed, int index, Func<string, ActorBrain> make)
    {
        _roles = roles;
        _switchSeconds = switchSeconds;
        _seed = seed;
        _index = index;
        _make = make;
    }

    public override string Role => _current?.Role ?? "roles";

    // 기능: period번째 구간의 역할을 가중치에 따라 결정적으로 뽑는다.
    // 입력: roles - 역할과 가중치, seed - 시나리오 시드, index - 액터 index, period - 구간 번호.
    // 출력: 역할 이름(가중치 합이 0 이하면 첫 역할).
    // Pure: the role for period p.
    public static string RoleFor(IReadOnlyList<RoleShare> roles, int seed, int index, int period)
    {
        double total = roles.Sum(r => r.Weight);
        if (total <= 0) return roles[0].Role;
        var rng = new Random(Seed(seed, index, 1000 + period));
        double pick = rng.NextDouble() * total;
        foreach (RoleShare r in roles)
        {
            pick -= r.Weight;
            if (pick < 0) return r.Role;
        }
        return roles[^1].Role;
    }

    // 기능: 구간이 바뀌면 역할을 다시 뽑아 뇌를 바꾸고(바뀔 때 의도 초기화), 현재 뇌의 Think를 부른다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. 현재 뇌와 액터 의도가 갱신된다.
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        if (_start < 0) _start = now;
        int period = _switchSeconds > 0 ? (int)((now - _start) / _switchSeconds) : 0;
        if (period != _period)
        {
            _period = period;
            string role = RoleFor(_roles, _seed, _index, period);
            if (!_brains.TryGetValue(role, out ActorBrain? brain)) _brains[role] = brain = _make(role);
            if (!ReferenceEquals(brain, _current))
            {
                actor.BrainMoveTo(null, 1f, false);
                actor.BrainVector(0f, 0f);
                actor.BrainClearAim();
                actor.BrainHold(InputButtons.Sprint, false);
                _current = brain;
            }
        }
        _current?.Think(actor, view, now);
    }
}

// ---- Phase B: firing at a point ----

// §27-29 (build destruction): stand, aim at a fixed point (a piece's centre) and fire Presses single presses in bursts
// that respect the weapon's fire interval (FirePress), reloading when the magazine is empty. Real shots: the server's
// hitscan decides what they hit (a piece, a player, nothing). Done after Presses; the actor then just stands there.
public sealed record FireAtPlan(Vector3 Point, int Presses, int Burst, int Slot = 0);

public sealed class FireAtBrain : ActorBrain
{
    private const float ButtonRepeat = 0.5f;

    private readonly FireAtPlan _plan;
    private int _sent;
    private float _nextButton;

    // 기능: 지점 사격 뇌를 만든다.
    // 입력: plan - 사격 계획.
    // 출력: FireAtBrain.
    public FireAtBrain(FireAtPlan plan) => _plan = plan;

    public override string Role => "fireAt";

    public int Sent => _sent;

    // 기능: 제자리에서 지점을 조준하고 무기를 든 뒤 Presses번이 될 때까지 burst 사격·reload 스크립트를 건다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. Sent가 늘고 액터 의도가 갱신된다.
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        if (!view.Alive) return;
        actor.BrainMoveTo(null, 1f, false);
        actor.BrainVector(0f, 0f);
        actor.BrainAimPoint(_plan.Point);
        if (_sent >= _plan.Presses || !actor.ScriptIdle) return;
        if (view.Self.Tool != ToolKind.Weapon || view.Inventory.CurrentSlot != _plan.Slot)
        {
            if (now >= _nextButton)
            {
                actor.BrainScript(new[] { new InputStep(SlotButton(_plan.Slot), 1), new InputStep(InputButtons.None, 2) });
                _nextButton = now + ButtonRepeat;
            }
            return;
        }
        if (view.WeaponInSlot(_plan.Slot) == null || view.Self.ReloadRemainingTicks > 0) return;
        if (view.Self.Ammo == 0)
        {
            if (now >= _nextButton)
            {
                actor.BrainScript(new[] { new InputStep(InputButtons.Reload, 1), new InputStep(InputButtons.None, 1) });
                _nextButton = now + ButtonRepeat;
            }
            return;
        }
        int presses = Math.Min(Math.Min(_plan.Burst, (int)view.Self.Ammo), _plan.Presses - _sent);
        var steps = new InputStep[presses];
        for (int i = 0; i < presses; i++) steps[i] = new InputStep(InputButtons.Fire, 1, FirePress: true);
        actor.BrainScript(steps);
        _sent += presses;
    }
}

// ---- Phase B: abusive clients (§40-43) ----

// Spreads RatePerSecond sends over the pump's ticks (a fraction carries over), at most MaxPerTick in one tick so a
// stalled pump does not burst. Pure (tests).
public struct RateBudget
{
    public const int MaxPerTick = 20;
    private float _carry;
    private float _last;
    private bool _started;

    // 기능: 지난 tick 이후 시간만큼 전송 예산을 쌓고 정수 개수를 꺼낸다(첫 호출은 0, 한 tick에 MaxPerTick 이하).
    // 입력: ratePerSecond - 초당 전송 수, now - 현재 시각.
    // 출력: 이번 tick에 보낼 개수.
    public int Take(float ratePerSecond, float now)
    {
        if (!_started)
        {
            _started = true;
            _last = now;
            return 0;
        }
        float dt = Math.Clamp(now - _last, 0f, 1f);
        _last = now;
        _carry = MathF.Min(_carry + ratePerSecond * dt, MaxPerTick);
        int n = (int)_carry;
        _carry -= n;
        return n;
    }
}

// §40-42: a connected client that also sends invalid packets (FaultActions.InvalidPacket bytes, kinds in turn, seeded
// per actor) at RatePerSecond on its live connection, while it keeps sending its normal input. The server counts each
// one and kicks the connection at its BadPacketDisconnectThreshold; the group's rejoin workload brings it back.
public sealed record InvalidPacketPlan(IReadOnlyList<string> Kinds, float RatePerSecond, int Index, int Seed, GroupStats Stats);

public sealed class InvalidPacketBrain : ActorBrain
{
    private readonly InvalidPacketPlan _plan;
    private readonly Random _rng;
    private RateBudget _budget;
    private int _next;

    // 기능: 잘못된 패킷 전송 뇌를 만든다.
    // 입력: plan - 전송 계획.
    // 출력: InvalidPacketBrain.
    public InvalidPacketBrain(InvalidPacketPlan plan)
    {
        _plan = plan;
        _rng = new Random(Seed(plan.Seed, plan.Index, 41));
    }

    public override string Role => "invalidPackets";

    // 기능: 예산만큼 종류를 돌아가며 잘못된 패킷을 보내고 AbuseSent를 센다(전송 실패면 중단).
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드(사용하지 않음), now - 현재 시각(초).
    // 출력: 반환값 없음. Stats.AbuseSent가 는다.
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        int n = _budget.Take(_plan.RatePerSecond, now);
        for (int i = 0; i < n; i++)
        {
            string kind = _plan.Kinds[_next++ % _plan.Kinds.Count];
            if (!actor.BrainSendRaw(FaultActions.InvalidPacket(kind, _rng))) return;
            Interlocked.Increment(ref _plan.Stats.AbuseSent);
        }
    }
}

// §43: build mode on (Q, as a real builder), then build requests at RatePerSecond sent at once, without the aim ticks
// and spacing a correct client keeps: random walls, floors and ramps in the cells around the player at its level. The
// server answers each one (placed, refused for aim, range, occupancy or resources, or RateLimited when the player's
// 8-request queue is full); above its network limit (maxRequestsPerSecond) a request is a bad packet and counts toward
// the kick. The group's rejoin workload brings a kicked spammer back.
public sealed record BuildSpamPlan(float RatePerSecond, BuildMaterialType Material, int Index, int Seed, GroupStats Stats);

public sealed class BuildSpamBrain : ActorBrain
{
    private const float ButtonRepeat = 0.5f;
    private static readonly BuildPieceType[] Types = { BuildPieceType.Wall, BuildPieceType.Floor, BuildPieceType.Ramp };

    private readonly BuildSpamPlan _plan;
    private readonly Random _rng;
    private RateBudget _budget;
    private float _nextButton;

    // 기능: 건설 요청 spam 뇌를 만든다.
    // 입력: plan - spam 계획.
    // 출력: BuildSpamBrain.
    public BuildSpamBrain(BuildSpamPlan plan)
    {
        _plan = plan;
        _rng = new Random(Seed(plan.Seed, plan.Index, 43));
    }

    public override string Role => "buildSpam";

    // 기능: 건설 모드가 아니면 ToolBuild를 누르고, 모드면 예산만큼 주변 셀에 무작위 조각 요청을 즉시 보내며 AbuseSent를 센다.
    // 입력: actor - 조종할 액터, view - 액터 Client가 아는 월드, now - 현재 시각(초).
    // 출력: 반환값 없음. Stats.AbuseSent가 늘고 액터 스크립트가 걸릴 수 있다.
    internal override void Think(HeadlessActor actor, BotView view, float now)
    {
        if (!view.Alive) return;
        if (view.Self.Tool != ToolKind.Build)
        {
            if (actor.ScriptIdle && now >= _nextButton)
            {
                actor.BrainScript(new[] { new InputStep(InputButtons.ToolBuild, 1), new InputStep(InputButtons.None, 1) });
                _nextButton = now + ButtonRepeat;
            }
            return;
        }
        int n = _budget.Take(_plan.RatePerSecond, now);
        Vector3 me = view.MyPosition;
        int cx = BuildGrid.CellX(me.X), cz = BuildGrid.CellZ(me.Z), level = Math.Clamp(BuildGrid.Level(me.Y + 0.1f), 0, BuildGrid.Levels - 1);
        for (int i = 0; i < n; i++)
        {
            int x = Math.Clamp(cx + _rng.Next(-1, 2), 0, BuildGrid.CellsX - 1);
            int z = Math.Clamp(cz + _rng.Next(-1, 2), 0, BuildGrid.CellsZ - 1);
            BuildPieceType type = Types[_rng.Next(Types.Length)];
            if (actor.BrainSendBuildNow(type, _plan.Material, (byte)x, (byte)level, (byte)z, (byte)_rng.Next(4)) < 0) return;
            Interlocked.Increment(ref _plan.Stats.AbuseSent);
        }
    }
}
