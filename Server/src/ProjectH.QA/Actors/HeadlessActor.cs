using System.Numerics;
using ProjectH.Bots;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// D8: a scripted client. It reuses the bots' connection (BotConnection, BotView) and their aim and steering math, but
// instead of a BotBrain it follows an intent the scenario sets: a move target or move vector, a look direction or aim
// target, held buttons and a queue of timed presses. Every field below except _state is owned by the ActorPump
// thread (LiteNetLib manual mode: Update, Connect, Send and Stop all on that one thread); the runner only queues
// commands (SendAsync) and reads State.
public sealed class HeadlessActor : IQaActor
{
    public const int MaxScriptSteps = 4096;   // a fire count is capped well below this (ActorActions.MaxPresses)
    private const float ArrivalSlowRadius = 2f;
    private const float MinApproachInput = 0.3f;

    private enum AimMode : byte { None, Fixed, Point, Actor }

    private readonly ActorPump _pump;
    private readonly Random _rng;
    private readonly BotSteering _steering = new();
    private readonly Queue<InputStep> _script = new();
    private BotConnection? _connection;
    private bool _closed;              // we closed _connection ourselves (no event comes for a local stop)
    private string _closeReason = string.Empty;
    private int _connections;

    private Vector3? _moveTarget;
    private float _moveTolerance;
    private bool _moveSprint;
    private bool _moveArrived;
    private bool _moveGaveUp;
    private float _moveDistance;
    private float _vectorX;
    private float _vectorY;
    private int _vectorTicks;          // > 0: ticks left; -1: until changed; 0: off
    private AimMode _aim;
    private float _aimYaw;
    private float _aimPitch;
    private Vector3 _aimPoint;
    private ushort _aimEntity;
    private IQaActor? _aimFallback;
    private float _bodyYaw;
    private InputButtons _held;
    private InputStep _current;
    private int _currentTicks;
    private int _pendingRelease;
    private long _pressesSent;
    private string? _error;
    private long _lastCommandId;
    private bool _inputPaused;
    private long _rawSent;
    // Building (pump thread): pieces still to send, whether the next one has had its aim tick, the request to send after
    // this tick's input, the per-actor request sequence, and the results seen so far (bounded).
    public const int MaxBuildQueue = 64;
    private const int ToolPressIntervalTicks = 15;   // Q toggles: wait for the snapshot before pressing again (BotBuilder)
    private readonly Queue<BuildPlan> _builds = new();
    private int _buildAimTicks;
    private int _buildHold;
    private Vector3 _buildHoldAim;
    private int _toolPressWait;
    private BuildRequest? _sendBuild;
    private ushort _buildSequence;
    private int _buildFirst;
    private int _buildLast;
    private long _buildResultsSeen;
    private readonly Queue<BuildResultInfo> _buildResults = new();
    // QA-5 playInputs (pump thread): the recording being replayed (null = none; released when it ends or stops), the
    // cursor in recording entries, the last entry sent, and the progress the state publishes.
    private IReadOnlyList<RecordedInput>? _playback;
    private double _playSpeed;
    private double _playCursor;
    private int _playLastIndex;
    private long _playCommandId;
    private int _playSent;
    private bool _playCompleted;

    private ActorState _state;

    internal HeadlessActor(string alias, ActorPump pump, int seed)
    {
        Alias = alias;
        DevPlayerId = ActorManager.DevPlayerIdFor(alias);
        _pump = pump;
        _rng = new Random(seed);
        _state = new ActorState { Alias = alias, DevPlayerId = DevPlayerId, Status = ActorStatus.Idle };
    }

    public string Alias { get; }
    public string DevPlayerId { get; }
    public ActorState State => Volatile.Read(ref _state);

    public ValueTask SendAsync(ActorCommand command, CancellationToken token) => _pump.PostAsync(this, command, token);

    // ---- pump thread only below ----

    internal void Apply(ActorCommand command)
    {
        _lastCommandId = command.Id;
        switch (command)
        {
            case ConnectCommand c:
                CloseConnection(graceful: true, "replaced by a new connection");
                ResetIntent();
                // Like BotRunner.Reconnect: each attempt is a new connection (its own Seq from 1, as the server
                // expects after a resume), and a reconnect gets the short connect budget.
                _connection = new BotConnection(c.Reconnect);
                _buildResultsSeen = 0;
                _closed = false;
                _closeReason = string.Empty;
                _connections++;
                _connection.Connect(c.Host, c.Port, DevPlayerId);
                break;
            case DisconnectCommand d:
                CloseConnection(d.Graceful, d.Graceful ? "Closed by QA (graceful)" : "Aborted by QA (network loss)");
                ResetIntent();
                break;
            case MoveToCommand m:
                _moveTarget = m.Target?.ToVector3();
                _moveTolerance = m.Tolerance;
                _moveSprint = m.Sprint;
                _moveArrived = false;
                _moveGaveUp = false;
                if (_moveTarget != null && _connection != null)
                {
                    _steering.Reset(_connection.View.MyPosition, _moveTarget.Value, _pump.Now);
                    _moveDistance = BotAim.HorizontalDistance(_connection.View.MyPosition, _moveTarget.Value);
                }
                break;
            case MoveVectorCommand v:
                _vectorX = Math.Clamp(v.X, -1f, 1f);
                _vectorY = Math.Clamp(v.Y, -1f, 1f);
                _vectorTicks = v.X == 0f && v.Y == 0f ? 0 : v.DurationTicks > 0 ? v.DurationTicks : -1;
                break;
            case LookCommand l:
                _aim = AimMode.Fixed;
                _aimYaw = l.Yaw;
                _aimPitch = l.Pitch;
                break;
            case AimAtPointCommand p:
                _aim = AimMode.Point;
                _aimPoint = p.Point.ToVector3();
                break;
            case AimAtActorCommand a:
                _aim = AimMode.Actor;
                _aimEntity = a.EntityId;
                _aimFallback = a.Fallback;
                break;
            case ClearAimCommand:
                _aim = AimMode.None;
                _aimFallback = null;
                break;
            case ScriptCommand s:
                if (_script.Count + s.Steps.Count > MaxScriptSteps)
                {
                    _error = $"Input queue full ({MaxScriptSteps} steps): command dropped.";
                    break;
                }
                foreach (InputStep step in s.Steps) _script.Enqueue(step);
                break;
            case HoldCommand h:
                _held = h.Down ? _held | h.Buttons : _held & ~h.Buttons;
                break;
            case StopFireCommand:
                _held &= ~InputButtons.Fire;
                DropFireSteps();
                break;
            case ResetIntentCommand:
                ResetIntent();
                break;
            case ClearInputQueueCommand:
                _script.Clear();
                _currentTicks = 0;
                _pendingRelease = 0;
                _builds.Clear();
                _buildAimTicks = 0;
                _buildHold = 0;
                StopPlayback();
                break;
            case PlayInputsCommand play:
                StopPlayback();
                _playback = play.Inputs.Count > 0 ? play.Inputs : null;
                _playSpeed = Math.Clamp(play.Speed, PlayInputsCommand.MinSpeed, PlayInputsCommand.MaxSpeed);
                _playCursor = 0;
                _playLastIndex = -1;
                _playCommandId = play.Id;
                _playSent = 0;
                _playCompleted = play.Inputs.Count == 0;
                break;
            case PauseInputCommand p:
                _inputPaused = p.Paused;
                break;
            case SendRawCommand raw:
            {
                BotConnection? c = _connection;
                if (c == null || _closed || !c.Connected || c.Disconnected)
                {
                    _error = "Not connected: raw packets not sent.";
                    break;
                }
                foreach (byte[] packet in raw.Packets)
                {
                    if (c.SendRaw(packet)) _rawSent++;
                }
                break;
            }
            case BuildCommand b:
                if (_builds.Count + b.Pieces.Count > MaxBuildQueue)
                {
                    _error = $"Build queue full ({MaxBuildQueue}): command dropped.";
                    break;
                }
                _buildFirst = (ushort)(_buildSequence + 1);
                foreach (BuildPlan plan in b.Pieces) _builds.Enqueue(plan);
                _buildLast = (ushort)(_buildSequence + b.Pieces.Count);
                break;
        }
    }

    // Test seam: runs at the start of every Tick (null outside tests).
    internal Action? TickHook;

    // Publishes even when the tick throws, so the error and the applied command id reach State and a waiting step
    // sees them instead of running to its timeout.
    internal void Tick(float elapsedMs, float now)
    {
        try
        {
            TickHook?.Invoke();
            BotConnection? c = _connection;
            if (c != null && !_closed && !c.Disconnected) c.Update(elapsedMs);
            if (c != null) TakeBuildResults(c.View);
            if (c != null && !_closed && c.Connected && !c.Disconnected && c.View.Joined && c.View.HasSnapshot && !_inputPaused)
            {
                // Sent every tick even when idle: the server's InputTimeout closes a joined client that goes silent (D8).
                // pauseInput stops exactly this (and nothing else) to let that timeout happen.
                _sendBuild = null;
                // A replay advances only here, where an input is really sent: a paused or not yet joined actor does
                // not "finish" a recording it never sent.
                c.SendInput(_playback != null ? NextPlaybackInput(c.View) : BuildInput(c.View, now));
                // After the input that carries the aim, like BotRunner: the server places with the last input's aim.
                if (_sendBuild is BuildRequest request) c.SendBuild(request);
            }
        }
        catch (Exception e)
        {
            RecordError(e);
        }
        PublishSafe();
    }

    internal void RecordError(Exception e) => _error = $"{e.GetType().Name}: {e.Message}";

    // Publish, or at least the error and the command id when building the full state fails.
    internal void PublishSafe()
    {
        try
        {
            Publish();
        }
        catch (Exception e)
        {
            RecordError(e);
            Volatile.Write(ref _state, State with { Error = _error, LastCommandId = _lastCommandId });
        }
    }

    // Pump shutdown: a graceful close tells the server at once (a clean leave, or grace in a match).
    internal void Shutdown()
    {
        CloseConnection(graceful: true, "QA run ended");
        PublishSafe();
    }

    internal byte SimHz => _connection != null && _connection.View.Joined ? _connection.View.SimHz : (byte)0;

    private void CloseConnection(bool graceful, string reason)
    {
        if (_connection == null || _closed) return;
        // A connection the server already closed keeps the server's reason.
        _closeReason = _connection.Disconnected ? _connection.DisconnectReason : reason;
        if (graceful) _connection.Dispose();
        else _connection.Abort();
        _closed = true;
    }

    private void ResetIntent()
    {
        _moveTarget = null;
        _moveArrived = false;
        _moveGaveUp = false;
        _vectorTicks = 0;
        _aim = AimMode.None;
        _aimFallback = null;
        _held = InputButtons.None;
        _script.Clear();
        _currentTicks = 0;
        _pendingRelease = 0;
        _builds.Clear();
        _buildAimTicks = 0;
        _buildHold = 0;
        _inputPaused = false;
        StopPlayback();
    }

    // One replay tick (pure, tested): the entry at the cursor (clamped to the last one), with the buttons of every entry
    // skipped since the last sent one (speed > 1), then the cursor advances by speed. Done once the last entry has been
    // sent and the cursor passed the end, so the last entry is always sent (and held 1/speed ticks when speed < 1).
    internal static (int Index, InputButtons Buttons, bool Done) PlaybackStep(IReadOnlyList<RecordedInput> inputs, ref double cursor, ref int lastIndex, double speed)
    {
        int index = Math.Min(inputs.Count - 1, (int)cursor);
        InputButtons buttons = inputs[index].Buttons;
        for (int i = lastIndex + 1; i < index; i++) buttons |= inputs[i].Buttons;
        lastIndex = Math.Max(lastIndex, index);
        cursor += speed;
        return (index, buttons, lastIndex == inputs.Count - 1 && cursor >= inputs.Count);
    }

    // How many ticks a replay of `count` entries takes at `speed` (the same steps as PlaybackStep).
    public static int PlaybackTicks(int count, double speed)
    {
        if (count <= 0) return 0;
        double cursor = 0;
        int last = -1, ticks = 0;
        while (true)
        {
            int index = Math.Min(count - 1, (int)cursor);
            last = Math.Max(last, index);
            cursor += speed;
            ticks++;
            if (last == count - 1 && cursor >= count) return ticks;
        }
    }

    // Ends a replay early (it did not complete) and drops the recording.
    private void StopPlayback() => _playback = null;

    // The recorded entry for this tick (D34). Entries skipped because of Speed > 1 give their buttons to this one.
    private InputCommand NextPlaybackInput(BotView view)
    {
        IReadOnlyList<RecordedInput> inputs = _playback!;
        (int index, InputButtons buttons, bool done) = PlaybackStep(inputs, ref _playCursor, ref _playLastIndex, _playSpeed);
        RecordedInput entry = inputs[index];
        _playSent++;
        if (done)
        {
            _playback = null;
            _playCompleted = true;
        }
        _bodyYaw = entry.Yaw;
        return new InputCommand
        {
            MoveX = entry.MoveX,
            MoveY = entry.MoveY,
            Yaw = entry.Yaw,
            Buttons = buttons,
            AimYaw = entry.AimYaw,
            AimPitch = entry.AimPitch,
            ViewTick = view.ServerTick,   // as BuildInput: the tick of the latest snapshot this client applied
        };
    }

    private void TakeBuildResults(BotView view)
    {
        // A burst bigger than the ring between two ticks loses the oldest (counted in BuildResultCount anyway).
        long from = Math.Max(_buildResultsSeen, view.BuildResultCount - BotView.RecentBuildResultCount);
        for (long i = from; i < view.BuildResultCount; i++)
        {
            BuildResult r = view.RecentBuildResults[i % BotView.RecentBuildResultCount];
            if (_buildResults.Count >= ActorState.MaxBuildResults) _buildResults.Dequeue();
            _buildResults.Enqueue(new BuildResultInfo(r.Sequence, r.Code.ToString(), r.PieceId));
        }
        _buildResultsSeen = view.BuildResultCount;
    }

    // One build step per tick (BotBuilder's order): Q until the snapshot shows build mode, then per piece AimTicksPerBuild
    // ticks aiming at it and a send tick. The server places a queued request when it processes it, with the aim of the
    // latest input, and at most one per minimumBuildInterval (0.1 s = 3 ticks at 30 Hz): sending one request per 3 ticks
    // keeps each request processed while its own aim is current. After the last one the aim stays on it for
    // HoldAimTicks so a request still waiting server-side is not judged against the walking aim. Returns true while
    // building (aim and buttons are the build's).
    private const int AimTicksPerBuild = 2;
    private const int HoldAimTicks = 10;

    private bool StepBuild(BotView view, ref InputButtons buttons, ref float aimYaw, ref float aimPitch)
    {
        if (_toolPressWait > 0) _toolPressWait--;
        if (!view.Alive) return false;
        if (_builds.Count == 0)
        {
            if (_buildHold <= 0) return false;
            _buildHold--;
            BotAim.Solve(BotAim.Eye(view.MyPosition), _buildHoldAim, out aimYaw, out aimPitch);
            buttons &= ~InputButtons.Fire;
            return true;
        }
        MovementMode mode = view.MyMode;
        if (mode != MovementMode.Ground && mode != MovementMode.Crouch && mode != MovementMode.Slide) return false;
        BuildPlan plan = _builds.Peek();
        BotAim.Solve(BotAim.Eye(view.MyPosition), plan.AimAt.ToVector3(), out aimYaw, out aimPitch);
        buttons &= ~InputButtons.Fire;
        if (view.Self.Tool != ToolKind.Build)
        {
            if (_toolPressWait == 0)
            {
                buttons |= InputButtons.ToolBuild;
                _toolPressWait = ToolPressIntervalTicks;
            }
            _buildAimTicks = 0;
            return true;
        }
        if (_buildAimTicks < AimTicksPerBuild)
        {
            _buildAimTicks++;
            return true;
        }
        _builds.Dequeue();
        _buildAimTicks = 0;
        _buildHold = HoldAimTicks;
        _buildHoldAim = plan.AimAt.ToVector3();
        _sendBuild = new BuildRequest
        {
            Sequence = ++_buildSequence, Piece = (byte)plan.Piece, Material = (byte)plan.Material,
            X = plan.X, Y = plan.Y, Z = plan.Z, Rotation = plan.Rotation,
        };
        return true;
    }

    private void DropFireSteps()
    {
        int count = _script.Count;
        for (int i = 0; i < count; i++)
        {
            InputStep step = _script.Dequeue();
            if (!step.FirePress && (step.Buttons & InputButtons.Fire) == 0) _script.Enqueue(step);
        }
        if ((_current.Buttons & InputButtons.Fire) != 0)
        {
            _currentTicks = 0;
            _pendingRelease = 0;
        }
    }

    private InputCommand BuildInput(BotView view, float now)
    {
        var command = new InputCommand();
        Vector3 me = view.MyPosition;
        bool alive = view.Alive;

        // Aim (yaw 0 faces +Z, positive pitch looks down: BotAim's convention, the server's too).
        bool aiming = true;
        float aimYaw = _bodyYaw, aimPitch = 0f;
        switch (_aim)
        {
            case AimMode.Fixed:
                aimYaw = _aimYaw;
                aimPitch = _aimPitch;
                break;
            case AimMode.Point:
                BotAim.Solve(BotAim.Eye(me), _aimPoint, out aimYaw, out aimPitch);
                break;
            case AimMode.Actor:
                // The target as this client sees it (its latest snapshot), chest height, like the bots aim.
                Vector3 target;
                if (view.TryGetOther(_aimEntity, out SnapshotEntity other)) target = other.Position;
                else if (_aimFallback != null) target = _aimFallback.State.Position.ToVector3();
                else target = me;
                BotAim.Solve(BotAim.Eye(me), BotAim.Chest(target), out aimYaw, out aimPitch);
                break;
            default:
                aiming = false;
                break;
        }

        // Move: world direction from steering, turned into local input relative to the body yaw.
        bool jump = false;
        bool moving = false;
        float moveYaw = _bodyYaw;
        float worldX = 0f, worldZ = 0f;
        if (_moveTarget != null && alive)
        {
            Vector3 goal = _moveTarget.Value;
            _moveDistance = BotAim.HorizontalDistance(me, goal);
            if (_moveDistance <= _moveTolerance)
            {
                _moveArrived = true;
            }
            else
            {
                _moveArrived = false;
                _steering.Steer(me, goal, now, _rng, out moveYaw, out jump, out bool giveUp);
                if (giveUp) _moveGaveUp = true;
                // Slow down near the goal: positions come from snapshots a few ticks old, full speed would overshoot.
                float speed = _moveDistance < ArrivalSlowRadius ? MathF.Max(MinApproachInput, _moveDistance / ArrivalSlowRadius) : 1f;
                float rad = moveYaw * (MathF.PI / 180f);
                worldX = MathF.Sin(rad) * speed;
                worldZ = MathF.Cos(rad) * speed;
                moving = true;
            }
        }

        float bodyYaw = aiming ? aimYaw : moving ? moveYaw : _bodyYaw;
        if (moving)
        {
            // MovementSimulation: world = (cos*mx + sin*my, -sin*mx + cos*my); inverted for the body yaw.
            float rad = bodyYaw * (MathF.PI / 180f);
            float sin = MathF.Sin(rad), cos = MathF.Cos(rad);
            command.MoveX = cos * worldX - sin * worldZ;
            command.MoveY = sin * worldX + cos * worldZ;
        }
        else if (_vectorTicks != 0 && alive)
        {
            command.MoveX = _vectorX;
            command.MoveY = _vectorY;
            if (_vectorTicks > 0) _vectorTicks--;
        }

        InputButtons buttons = _held | NextScripted(view);
        if (moving && (_moveSprint || _steering.Stuck)) buttons |= InputButtons.Sprint;
        if (jump) buttons |= InputButtons.Jump;
        StepBuild(view, ref buttons, ref aimYaw, ref aimPitch);

        command.Yaw = bodyYaw;
        command.AimYaw = aimYaw;
        command.AimPitch = aimPitch;
        command.Buttons = buttons;
        command.ViewTick = view.ServerTick;   // as BotBrain: the tick of the latest snapshot this client applied
        _bodyYaw = bodyYaw;
        return command;
    }

    private InputButtons NextScripted(BotView view)
    {
        if (_currentTicks <= 0)
        {
            if (_pendingRelease > 0)
            {
                _current = new InputStep(InputButtons.None, _pendingRelease);
                _currentTicks = _pendingRelease;
                _pendingRelease = 0;
            }
            else if (_script.Count > 0)
            {
                InputStep next = _script.Dequeue();
                if (next.FirePress)
                {
                    WeaponInfo? weapon = view.WeaponInSlot(view.Inventory.CurrentSlot);
                    _pendingRelease = Math.Max(1, (int)(weapon?.FireIntervalTicks ?? 1));
                    next = new InputStep(InputButtons.Fire, 1);
                }
                _current = next;
                _currentTicks = Math.Max(1, next.Ticks);
                if (next.Buttons != InputButtons.None) _pressesSent++;
            }
            else
            {
                return InputButtons.None;
            }
        }
        _currentTicks--;
        return _current.Buttons;
    }

    private void Publish()
    {
        BotConnection? c = _connection;
        ActorState state;
        if (c == null)
        {
            state = new ActorState
            {
                Alias = Alias, DevPlayerId = DevPlayerId, Status = ActorStatus.Idle, LastCommandId = _lastCommandId, Error = _error,
                InputPaused = _inputPaused, BuildFirstSequence = _buildFirst, BuildLastSequence = _buildLast, BuildsQueued = _builds.Count,
                PlaybackCommandId = _playCommandId, PlaybackActive = _playback != null, PlaybackSent = _playSent, PlaybackCompleted = _playCompleted,
            };
        }
        else
        {
            BotView v = c.View;
            bool gone = _closed || c.Disconnected;
            WeaponInfo? weapon = v.WeaponInSlot(v.Inventory.CurrentSlot);
            int queued = _script.Count + (_currentTicks > 0 ? 1 : 0) + (_pendingRelease > 0 ? 1 : 0);
            state = new ActorState
            {
                Alias = Alias,
                DevPlayerId = DevPlayerId,
                Status = gone ? ActorStatus.Disconnected : v.Joined ? ActorStatus.Joined : ActorStatus.Connecting,
                Connected = c.Connected && !gone,
                Joined = v.Joined && !gone,
                HasSnapshot = v.HasSnapshot,
                Disconnected = gone,
                DisconnectReason = gone ? (_closed ? _closeReason : c.DisconnectReason) : string.Empty,
                DisconnectCode = c.Code.ToString(),
                Connections = _connections,
                EntityId = v.MyId,
                Alive = v.Alive,
                Position = Vec3.From(v.MyPosition),
                Yaw = _bodyYaw,
                Mode = v.MyMode.ToString(),
                Health = v.Self.Health,
                Shield = v.Self.Shield,
                Ammo = v.Self.Ammo,
                CurrentSlot = v.Inventory.CurrentSlot,
                Tool = v.Self.Tool.ToString(),
                HasInventory = v.HasInventory,
                WeaponId = weapon?.WeaponId ?? 0,
                WeaponName = weapon?.Name ?? string.Empty,
                WeaponAutomatic = weapon?.Automatic ?? false,
                ReloadTicks = v.Self.ReloadRemainingTicks,
                ServerTick = v.ServerTick,
                MatchState = v.HasMatchState ? v.Match.State.ToString() : string.Empty,
                VisibleEntityIds = VisibleIds(v),
                RttMs = gone ? 0 : c.RoundTripTimeMs,
                PacketsIn = c.PacketsIn,
                BytesIn = c.BytesIn,
                InputsSent = c.InputsSent,
                HitsLanded = v.HitsLanded,
                DamageTaken = v.DamageTakenCount,
                MoveActive = _moveTarget != null,
                MoveArrived = _moveArrived,
                MoveGaveUp = _moveGaveUp,
                MoveDistance = _moveDistance,
                ScriptSteps = queued,
                PressesSent = _pressesSent,
                HeldButtons = _held.ToString(),
                LastCommandId = _lastCommandId,
                InputPaused = _inputPaused,
                RawPacketsSent = _rawSent,
                BuildFirstSequence = _buildFirst,
                BuildLastSequence = _buildLast,
                BuildsQueued = _builds.Count,
                BuildResults = _buildResults.ToArray(),
                PlaybackCommandId = _playCommandId,
                PlaybackActive = _playback != null,
                PlaybackSent = _playSent,
                PlaybackCompleted = _playCompleted,
                Error = _error,
            };
        }
        Volatile.Write(ref _state, state);
    }

    private static ushort[] VisibleIds(BotView v)
    {
        if (v.OtherCount == 0) return Array.Empty<ushort>();
        var ids = new ushort[v.OtherCount];
        for (int i = 0; i < ids.Length; i++) ids[i] = v.Others[i].EntityId;
        return ids;
    }
}
