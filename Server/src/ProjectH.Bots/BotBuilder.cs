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

    // 기능: 봇 건축기 하나를 만든다(자기만의 난수).
    // 입력: seed - 난수 씨앗, spamPerSecond - 초당 건설 요청 수(0 = 방어·높이 건설만), chance - 맞았을 때 벽을 세울 확률.
    // 출력: 진행 중인 배치가 없는 BotBuilder.
    public BotBuilder(int seed, int spamPerSecond = 0, float chance = DefenceChance)
    {
        _rng = new System.Random(seed);
        _spamPerSecond = spamPerSecond;
        _chance = chance;
    }

    public int Requests { get; private set; }

    // 기능: 두뇌의 입력 위에 이번 Tick의 건설 버튼·조준을 얹는다. 스팸이면 Spam, 아니면 진행 중인 배치(조준 → 전송 → 무기 복귀)를 잇고, 없으면 맞았을 때의 벽이나 높은 표적 쪽 경사로를 계획한다. 죽으면 배치를 버린다.
    // 입력: view - 봇이 아는 것, now - 지금 시각(초), fightTarget - 두뇌의 교전 표적(0 = 없음), command - 이번 입력, request - 보낼 요청을 받을 곳.
    // 출력: 이번 Tick에 건설 채널로 보낼 요청이 있으면 true와 request, 없으면 false.
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

    // 기능: 계획한 배치를 시작한다(조준 단계로, 쿨다운 시작, 건설 모드가 아니면 Q를 누르고 조각을 겨눈다).
    // 입력: view - 봇이 아는 것, now - 지금 시각(초), command - 이번 입력.
    // 출력: 항상 false(이번 Tick에는 요청을 보내지 않는다).
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

    // 기능: 봇 칸의 direction 쪽 모서리에 벽을, 또는 그 방향 옆 칸에 봇 쪽에서 올라가는 경사로를 봇의 층에 계획한다(살 수 있는 첫 재료).
    // 입력: view - 봇이 아는 것, type - Wall 또는 Ramp, direction - 공격자나 표적 쪽 방향.
    // 출력: 격자에 맞으면 true(_pending·_aimAt이 채워진다), 아니면 false.
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

    // 기능: 자원이 비용을 넘는 첫 재료를 고른다.
    // 입력: view - 봇이 아는 것(자원과 건설 카탈로그).
    // 출력: 살 수 있는 첫 재료, 카탈로그가 없거나 아무것도 못 사면 Wood.
    private static BuildMaterialType Affordable(BotView view)
    {
        if (view.BuildCatalog == null) return BuildMaterialType.Wood;
        for (int m = 0; m < BuildMaterials.Count; m++)
        {
            if (view.Resources.Get((BuildMaterialType)m) >= view.BuildCatalog.ResourceCost[m]) return (BuildMaterialType)m;
        }
        return BuildMaterialType.Wood;
    }

    // 기능: --build-spam: 건설 모드에 머물며 초당 N개 요청을 보낸다. 주변 3×3 칸의 벽·바닥·경사로를 봇의 층과 그 위층에서 돌아가며, 요청마다 한 Tick 먼저 겨눈다.
    // 입력: view - 봇이 아는 것, now - 지금 시각(초), command - 이번 입력, request - 보낼 요청을 받을 곳.
    // 출력: 이번 Tick에 보낼 요청이 있으면 true와 request(겨눈 다음 Tick), 아니면 false.
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

    // 기능: 도구 버튼을 누르고 다음 누름까지 ButtonRepeatSeconds를 기다리게 한다.
    // 입력: command - 이번 입력, button - 누를 버튼, now - 지금 시각(초).
    // 출력: 반환값 없음. command.Buttons에 버튼이 더해지고 다음 누름 시각이 잡힌다.
    private void Press(ref InputCommand command, InputButtons button, float now)
    {
        command.Buttons |= button;
        _nextToolPress = now + ButtonRepeatSeconds;
    }

    // 기능: 눈 높이(서버의 건설 검사 기준)에서 계획한 조각의 중심을 겨눈다.
    // 입력: view - 봇이 아는 것, command - 이번 입력.
    // 출력: 반환값 없음. command의 AimYaw·AimPitch가 채워진다(조각이 눈과 같은 점이면 그대로 둔다).
    private void Aim(BotView view, ref InputCommand command)
    {
        Vector3 d = _aimAt - (view.MyPosition + new Vector3(0f, 1.6f, 0f));
        float flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        if (flat < 1e-3f && MathF.Abs(d.Y) < 1e-3f) return;
        command.AimYaw = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;
        command.AimPitch = -MathF.Atan2(d.Y, flat) * 180f / MathF.PI;
    }

    // 기능: 이 이동 모드에서 건설이 가능한지 본다(지상·앉기·슬라이드만).
    // 입력: mode - 봇의 이동 모드.
    // 출력: 건설할 수 있으면 true.
    private static bool ActionsAllowed(MovementMode mode) =>
        mode == MovementMode.Ground || mode == MovementMode.Crouch || mode == MovementMode.Slide;
}
