using System.Numerics;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Bots;

// Phase 13 D17 (request §133-§137): a bot's building, on top of the brain's input. Three behaviours:
//  - Defence: after a hit (DamageTaken), at most once per DefenceCooldownSeconds and with DefenceChance, a wall on the
//    bot's cell edge towards the attacker.
//  - Height: a fight target at least RampHeight above the bot within RampRange gets a ramp in the cell ahead (towards
//    it), at most once per DefenceCooldownSeconds.
//  - Spam (--build-spam N, load tests): N requests per second cycling through the slots of the cells around the bot.
// One placement goes like a player's: press Q (build mode), aim at the piece for a tick, send the request on the building
// channel, then press 1 to take the weapons out again (spam stays in build mode). The server decides everything; with
// normal resources (no harvesting) most defence attempts are refused NoResource, which is the real rule (§136). Pure
// logic: time and randomness come in; allocates nothing.
public sealed class BotBuilder
{
    public const float DefenceCooldownSeconds = 3f;
    public const float DefenceChance = 0.3f;
    public const float RampHeight = 1.5f;
    public const float RampRange = 15f;
    private const float ButtonRepeatSeconds = 0.5f;
    // Final review C: a placement still aiming (build mode not reached) this long after it started is given up.
    public const float AimTimeoutSeconds = 2f;

    private readonly System.Random _rng;
    private readonly float _chance;
    private readonly int _spamPerSecond;
    private int _damageSeen;
    private float _nextPlan;
    private float _nextSpam;
    private float _nextToolPress;
    private int _spamCursor;
    private ushort _sequence;
    // The placement in progress: 0 none, 1 aiming (build mode pressed), 2 sent (back to weapons next).
    private int _phase;
    private float _phaseStarted;
    private BuildRequest _pending;
    private Vector3 _aimAt;

    // 기능: 봇 하나의 건설 행동 상태를 시드 고정 난수로 만든다.
    // 입력: seed - 난수 시드, spamPerSecond - 초당 스팸 건설 요청 수(0이면 끔), chance - 피격 시 방어벽을 세울 확률.
    // 출력: 진행 중인 배치가 없고 요청 수가 0인 건설 객체.
    public BotBuilder(int seed, int spamPerSecond = 0, float chance = DefenceChance)
    {
        _rng = new System.Random(seed);
        _spamPerSecond = spamPerSecond;
        _chance = chance;
    }

    public int Requests { get; private set; }

    // 기능: 두뇌가 만든 입력에 이번 Tick의 건설 버튼과 조준을 덧붙이고, 진행 중인 배치(조준 → 요청 → 무기 복귀) 단계를 진행하거나 방어벽·경사로·스팸 배치를 새로 시작한다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초), fightTarget - 두뇌의 교전 대상 ID(0이면 없음), command - 두뇌가 만든 이번 Tick 입력.
    // 출력: 이번 Tick에 건설 요청을 보내야 하면 true와 request(Sequence 포함), 아니면 false. command의 버튼·조준과 배치 단계·쿨다운이 갱신된다.
    // Adds this tick's building buttons and aim to the brain's command; request is what to send this tick (on the
    // building channel), if anything.
    public bool Tick(BotView view, float now, ushort fightTarget, ref InputCommand command, out BuildRequest request)
    {
        request = default;
        if (!view.Joined || !view.HasSnapshot) return false;
        // Final review C: every hit is taken at once (one during the cooldown, or while dead, never builds later), and a
        // death drops the placement in progress.
        bool hit = view.DamageTakenCount != _damageSeen;
        _damageSeen = view.DamageTakenCount;
        if (!view.Alive)
        {
            _phase = 0;
            return false;
        }
        if (!ActionsAllowed(view.MyMode)) return false;
        if (_spamPerSecond > 0) return Spam(view, now, ref command, out request);

        switch (_phase)
        {
            case 1:
                if (now - _phaseStarted > AimTimeoutSeconds)
                {
                    _phase = 0;   // build mode never came (a refused tool switch): give up, the cooldown still runs
                    return false;
                }
                // Aimed at the piece for one input; the server places with the last input's aim.
                Aim(view, ref command);
                command.Buttons &= ~InputButtons.Fire;
                if (view.Self.Tool != ToolKind.Build)
                {
                    if (now >= _nextToolPress) Press(ref command, InputButtons.ToolBuild, now);
                    return false;
                }
                request = _pending;
                request.Sequence = ++_sequence;
                Requests++;
                _phase = 2;
                return true;
            case 2:
                Aim(view, ref command);
                command.Buttons |= InputButtons.Slot1;
                _phase = 0;
                return false;
        }

        if (now < _nextPlan) return false;
        if (hit)
        {
            Vector3 from = view.LastDamageDirection;
            if ((from.X != 0f || from.Z != 0f) && _rng.NextDouble() < _chance && Plan(view, BuildPieceType.Wall, from)) return Start(view, now, ref command);
        }
        if (fightTarget != 0 && view.TryGetOther(fightTarget, out SnapshotEntity target))
        {
            Vector3 to = target.Position - view.MyPosition;
            float flat = MathF.Sqrt(to.X * to.X + to.Z * to.Z);
            if (to.Y >= RampHeight && flat <= RampRange && flat > 0.1f && Plan(view, BuildPieceType.Ramp, to)) return Start(view, now, ref command);
        }
        return false;
    }

    // 기능: 계획한 배치를 시작한다. 건설 모드가 아니면 Q를 누르고 계획한 조각을 조준한다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초), command - 채울 이번 Tick 입력.
    // 출력: 항상 false(요청은 다음 단계에서 보낸다). 배치 단계가 조준(1)이 되고 다음 계획 시각이 DefenceCooldownSeconds 뒤로 밀린다.
    private bool Start(BotView view, float now, ref InputCommand command)
    {
        _phase = 1;
        _phaseStarted = now;
        _nextPlan = now + DefenceCooldownSeconds;
        // Q toggles: already in build mode (a spam-free bot left there), pressing it would take the bot out again.
        if (view.Self.Tool != ToolKind.Build) Press(ref command, InputButtons.ToolBuild, now);
        Aim(view, ref command);
        return false;
    }

    // 기능: 봇이 선 격자 칸과 높이에서 방향에 맞는 벽 또는 경사로 배치 요청과 조준점을 계획한다.
    // 입력: view - 봇이 받은 정보, type - 조각 종류(Wall 또는 Ramp), direction - 공격자나 대상 쪽 방향(수평 성분 사용).
    // 출력: 격자 안에 놓을 수 있는 모양이면 true(대기 요청 _pending과 조준점 _aimAt이 설정됨), 아니면 false.
    // A wall on the bot's cell edge facing direction, or a ramp in the next cell that way rising towards it, at the bot's
    // level, in the first material the bot can pay for (any, with infinite resources the server does not ask).
    private bool Plan(BotView view, BuildPieceType type, Vector3 direction)
    {
        int x = BuildGrid.CellX(view.MyPosition.X);
        int z = BuildGrid.CellZ(view.MyPosition.Z);
        int y = System.Math.Clamp(BuildGrid.Level(view.MyPosition.Y + 0.1f), 0, BuildGrid.Levels - 1);
        int rotation;
        if (MathF.Abs(direction.X) >= MathF.Abs(direction.Z)) rotation = direction.X > 0f ? 3 : 1;   // east / west edge
        else rotation = direction.Z > 0f ? 2 : 0;                                                     // north / south edge
        if (type == BuildPieceType.Ramp)
        {
            // The cell ahead, rising away from the bot: wall edges 0 S, 1 W, 2 N, 3 E map to directions -Z, -X, +Z, +X.
            switch (rotation)
            {
                case 0: z--; rotation = 2; break;
                case 1: x--; rotation = 3; break;
                case 2: z++; rotation = 0; break;
                default: x++; rotation = 1; break;
            }
        }
        if (!BuildGrid.TryNormalize(type, x, y, z, rotation, out BuildPieceShape shape)) return false;
        _pending = new BuildRequest
        {
            Piece = (byte)type, Material = (byte)Affordable(view), X = (byte)x, Y = (byte)y, Z = (byte)z, Rotation = (byte)rotation,
        };
        _aimAt = BuildGrid.CenterOf(shape);
        return true;
    }

    // 기능: 현재 자원으로 비용을 낼 수 있는 첫 재료를 고른다.
    // 입력: view - 건설 카탈로그와 자원 상태가 든 봇 정보.
    // 출력: 낼 수 있는 첫 재료. 카탈로그가 없거나 낼 수 있는 재료가 없으면 Wood.
    private static BuildMaterialType Affordable(BotView view)
    {
        if (view.BuildCatalog == null) return BuildMaterialType.Wood;
        for (int m = 0; m < BuildMaterials.Count; m++)
        {
            if (view.Resources.Get((BuildMaterialType)m) >= view.BuildCatalog.ResourceCost[m]) return (BuildMaterialType)m;
        }
        return BuildMaterialType.Wood;
    }

    // 기능: 부하 테스트용으로 건설 모드를 유지하며 초당 spamPerSecond개의 건설 요청을 주변 칸을 돌아가며 만든다.
    // 입력: view - 봇이 받은 정보, now - 현재 시각(초), command - 채울 이번 Tick 입력.
    // 출력: 지난 Tick에 조준한 요청을 이번에 보내면 true와 request, 아니면 false. 사격·슬롯 버튼은 지워지고, 새 요청이면 조준과 대기 요청이 설정된다.
    // --build-spam: stay in build mode and send N requests a second, each aimed a tick before it is sent, cycling through
    // walls, floors and ramps of the 3 x 3 cells around the bot on its level and the next.
    private bool Spam(BotView view, float now, ref InputCommand command, out BuildRequest request)
    {
        request = default;
        command.Buttons &= ~(InputButtons.Fire | InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3);
        if (view.Self.Tool != ToolKind.Build)
        {
            if (now >= _nextToolPress) Press(ref command, InputButtons.ToolBuild, now);
            return false;
        }
        if (_phase == 1)
        {
            Aim(view, ref command);
            request = _pending;
            request.Sequence = ++_sequence;
            Requests++;
            _phase = 0;
            return true;
        }
        if (now < _nextSpam) return false;
        // A fixed rate (not "a period after this one"): ticks never line up exactly with the period. A long stall
        // starts over instead of bursting.
        _nextSpam = now - _nextSpam > 1f ? now + 1f / _spamPerSecond : _nextSpam + 1f / _spamPerSecond;
        int cell = _spamCursor % 9;
        int kind = _spamCursor / 9 % 4;
        int level = _spamCursor / 36 % 2;
        _spamCursor++;
        int x = BuildGrid.CellX(view.MyPosition.X) + cell % 3 - 1;
        int z = BuildGrid.CellZ(view.MyPosition.Z) + cell / 3 - 1;
        int y = System.Math.Clamp(BuildGrid.Level(view.MyPosition.Y + 0.1f) + level, 0, BuildGrid.Levels - 1);
        BuildPieceType type;
        int rotation = 0;
        switch (kind)
        {
            case 0: type = BuildPieceType.Wall; break;                    // the cell's south edge
            case 1: type = BuildPieceType.Wall; rotation = 1; break;      // its west edge
            case 2: type = BuildPieceType.Floor; break;
            default: type = BuildPieceType.Ramp; rotation = _spamCursor / 72 % 4; break;
        }
        if (!BuildGrid.TryNormalize(type, x, y, z, rotation, out BuildPieceShape shape)) return false;
        _pending = new BuildRequest
        {
            Piece = (byte)type, Material = (byte)BuildMaterialType.Wood, X = shape.X, Y = shape.Y, Z = shape.Z, Rotation = shape.Rotation,
        };
        _aimAt = BuildGrid.CenterOf(shape);
        _phase = 1;
        Aim(view, ref command);
        return false;
    }

    // 기능: 도구 전환 버튼을 누르고 다음 누름까지의 간격을 잡는다.
    // 입력: command - 채울 이번 Tick 입력, button - 누를 버튼, now - 현재 시각(초).
    // 출력: 반환값 없음. command에 버튼이 추가되고 다음 도구 버튼 시각이 ButtonRepeatSeconds 뒤가 된다.
    private void Press(ref InputCommand command, InputButtons button, float now)
    {
        command.Buttons |= button;
        _nextToolPress = now + ButtonRepeatSeconds;
    }

    // 기능: 눈 높이에서 계획한 조각 중심을 향하도록 조준 각도를 맞춘다.
    // 입력: view - 봇 위치가 든 봇 정보, command - 채울 이번 Tick 입력.
    // 출력: 반환값 없음. command의 AimYaw·AimPitch가 바뀐다. 조준점이 눈 위치와 거의 같으면 그대로 둔다.
    // Aim from the eye (the server's build check) at the planned piece.
    private void Aim(BotView view, ref InputCommand command)
    {
        Vector3 d = _aimAt - (view.MyPosition + new Vector3(0f, 1.6f, 0f));
        float flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        if (flat < 1e-3f && MathF.Abs(d.Y) < 1e-3f) return;
        command.AimYaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        command.AimPitch = -MathF.Atan2(d.Y, flat) * 180f / MathF.PI;
    }

    // 기능: 현재 이동 모드에서 건설 행동을 할 수 있는지 판단한다.
    // 입력: mode - 봇의 이동 모드.
    // 출력: 지상·웅크림·슬라이드면 true, 수송기·낙하·활공 등 그 외 모드면 false.
    private static bool ActionsAllowed(MovementMode mode) =>
        mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;
}
