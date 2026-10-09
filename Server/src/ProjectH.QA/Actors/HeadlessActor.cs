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
    // Phase 13.5 buildEdit (pump thread): edits still to send (paced; bounded with the builds by MaxBuildQueue), the
    // request to send after this tick's input, the watched piece ids (at most ActorState.MaxWatchedPieces, kept for the
    // actor's life so a reconnect can be checked) and the view version they were last published at.
    private readonly Queue<EditPlan> _edits = new();
    private BuildEditRequest? _sendEdit;
    private readonly List<uint> _watched = new();
    private long _watchedVersion = -1;
    private IReadOnlyDictionary<string, WatchedPiece> _watchedPublished = new Dictionary<string, WatchedPiece>();
    // QA-5 playInputs (pump thread): the recording being replayed (null = none; released when it ends or stops), the
    // cursor in recording entries, the last entry sent, and the progress the state publishes.
    private IReadOnlyList<RecordedInput>? _playback;
    private double _playSpeed;
    private double _playCursor;
    private int _playLastIndex;
    private long _playCommandId;
    private int _playSent;
    private bool _playCompleted;

    // Stress (D38): the group behaviour that sets this actor's intent every tick (null = scenario steps only). Handed
    // over by SetBrainCommand and from then on touched by the pump thread only. Kept across connect / disconnect (churn
    // reconnects an actor without losing its role); ResetIntentCommand and a new SetBrainCommand end it.
    private ActorBrain? _brain;
    // D43 R1: when each input was sent (Stopwatch timestamps by Seq % LatencyRing) for the current connection, the
    // newest Seq sent and the newest acknowledged. Reset with every new connection (Seq starts at 1 again).
    private const int LatencyRing = 256;
    private readonly long[] _inputSentAt = new long[LatencyRing];
    private uint _latencySent;
    private uint _latencyAcked;
    private readonly InputLatencyHistogram? _latency;
    // Build results by code over this actor's life (published as a new array only when a result arrives).
    private readonly long[] _buildCodeCounts = new long[(int)BuildResultCode.NotFound + 1];
    private long[] _buildCodesPublished = new long[(int)BuildResultCode.NotFound + 1];

    private ActorState _state;
    // Phase 15 (pump thread): the marker lists last published and the TeamMarkers count they were built at (rebuilt only
    // when a new TeamMarkers arrived, so an idle actor allocates nothing for them).
    private long _markersVersion = -1;
    private ActorPing[] _pingsPublished = Array.Empty<ActorPing>();
    private ActorWaypoint[] _waypointsPublished = Array.Empty<ActorWaypoint>();
    // Phase 16 (pump thread): the supply drop states last published and the SupplyDrops count they were built at.
    private long _dropsVersion = -1;
    private string[] _dropStatesPublished = Array.Empty<string>();

    // 기능: Headless Actor를 연결 없는 Idle 상태로 만든다.
    // 입력: alias - 시나리오 별칭, pump - 이 Actor를 Tick할 Pump, seed - 조향 난수 Seed, latency - 입력 지연 Histogram(null이면 기록 안 함).
    // 출력: Idle 상태가 발행된 HeadlessActor.
    internal HeadlessActor(string alias, ActorPump pump, int seed, InputLatencyHistogram? latency = null)
    {
        Alias = alias;
        DevPlayerId = ActorManager.DevPlayerIdFor(alias);
        _pump = pump;
        _latency = latency;
        _rng = new Random(seed);
        _state = new ActorState { Alias = alias, DevPlayerId = DevPlayerId, Status = ActorStatus.Idle };
    }

    // Review fix B4: this actor's resume key across its connections (QA servers run the development key, the default of
    // BotConnection).
    private readonly ProjectH.Bots.ResumeTicket _ticket = new();

    public string Alias { get; }
    public string DevPlayerId { get; }
    public ActorState State => Volatile.Read(ref _state);

    // 기능: 명령을 Pump 채널에 넣는다(다음 Tick의 Drain에서 적용).
    // 입력: command - 적용할 명령, token - 취소 토큰.
    // 출력: 반환값 없음. 채널이 가득 차면 자리가 날 때까지 기다리고, Pump가 멈췄으면 예외.
    public ValueTask SendAsync(ActorCommand command, CancellationToken token) => _pump.PostAsync(this, command, token);

    // ---- pump thread only below ----

    // 기능: 명령 하나를 이 배우의 의도에 반영한다(Pump 스레드, Phase 15: 지도 표시 요청은 바로 보낸다).
    // 입력: command - 적용할 명령.
    // 출력: 반환값 없음. 의도·연결 상태가 바뀌고 실패는 _error에 남는다.
    internal void Apply(ActorCommand command)
    {
        _lastCommandId = command.Id;
        switch (command)
        {
            case ConnectCommand c:
                CloseConnection(graceful: true, "replaced by a new connection");
                ResetIntent();
                _latencySent = 0;
                _latencyAcked = 0;
                // Like BotRunner.Reconnect: each attempt is a new connection (its own Seq from 1, as the server
                // expects after a resume), and a reconnect gets the short connect budget.
                _connection = new BotConnection(c.Reconnect);
                _buildResultsSeen = 0;
                _closed = false;
                _closeReason = string.Empty;
                _connections++;
                _connection.Connect(c.Host, c.Port, DevPlayerId, _ticket);   // review fix B4: a reconnect resumes with the proof
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
                SetBrain(null);
                ResetIntent();
                break;
            case SetBrainCommand b:
                SetBrain(b.Brain);
                break;
            case ClearInputQueueCommand:
                _script.Clear();
                _currentTicks = 0;
                _pendingRelease = 0;
                _builds.Clear();
                _edits.Clear();
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
            case BuildEditCommand e:
                ApplyEdit(e);
                break;
            case MapMarkerCommand marker:
            {
                // Phase 15: sent at once (no aim needed: the request carries its own position and target).
                BotConnection? c = _connection;
                if (c == null || _closed || !c.Connected || c.Disconnected || !c.View.Joined)
                {
                    _error = "Not joined: map markers not sent.";
                    break;
                }
                foreach (MapMarker m in marker.Markers) c.SendMapMarker(m);
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
    // 기능: 한 Pump Tick. 연결을 갱신하고 건설 결과·입력 Ack를 거둔 뒤, Join·Snapshot·입력 재개 상태면 Brain을 생각시키고 입력 하나(재생 또는 의도)와 뒤따르는 건설·편집 요청을 보낸다. 끝에 항상 상태를 발행한다.
    // 입력: elapsedMs - 지난 Tick 이후 경과 시간(ms), now - Pump 시계(초).
    // 출력: 반환값 없음. 연결·의도가 진행되고 State가 새로 발행된다(예외는 Error로 실린다).
    internal void Tick(float elapsedMs, float now)
    {
        try
        {
            TickHook?.Invoke();
            BotConnection? c = _connection;
            if (c != null && !_closed && !c.Disconnected) c.Update(elapsedMs);
            if (c != null) TakeBuildResults(c.View);
            if (c != null && !_closed && !c.Disconnected) TakeAcks(c);
            if (c != null && !_closed && c.Connected && !c.Disconnected && c.View.Joined && c.View.HasSnapshot && !_inputPaused)
            {
                // Sent every tick even when idle: the server's InputTimeout closes a joined client that goes silent (D8).
                // pauseInput stops exactly this (and nothing else) to let that timeout happen.
                _sendBuild = null;
                _sendEdit = null;
                if (_playback == null && _brain != null) Think(c.View, now);
                // A replay advances only here, where an input is really sent: a paused or not yet joined actor does
                // not "finish" a recording it never sent.
                long sentBefore = c.InputsSent;
                c.SendInput(_playback != null ? NextPlaybackInput(c.View) : BuildInput(c.View, now));
                // The Seq BotConnection gave this input: it numbers inputs from 1 per connection and counts each send.
                if (c.InputsSent != sentBefore)
                {
                    _latencySent = (uint)c.InputsSent;
                    _inputSentAt[_latencySent % LatencyRing] = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                // After the input that carries the aim, like BotRunner: the server places with the last input's aim.
                if (_sendBuild is BuildRequest request) c.SendBuild(request);
                if (_sendEdit is BuildEditRequest edit) c.SendBuildEdit(edit);
            }
        }
        catch (Exception e)
        {
            RecordError(e);
        }
        PublishSafe();
    }

    // 기능: 예외를 이 Actor의 오류 문자열로 기록한다.
    // 입력: e - 발생한 예외.
    // 출력: 반환값 없음. 다음 발행에 실릴 _error가 바뀐다.
    internal void RecordError(Exception e) => _error = $"{e.GetType().Name}: {e.Message}";

    // 기능: 상태를 발행하고, 전체 상태 만들기가 실패하면 오류와 명령 id만이라도 발행한다.
    // 입력: 없음.
    // 출력: 반환값 없음. State가 바뀐다.
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

    // 기능: Pump 종료 시 연결을 정상 종료(서버에 알림)하고 상태를 발행한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 연결이 닫히고 State가 바뀐다.
    // Pump shutdown: a graceful close tells the server at once (a clean leave, or grace in a match).
    internal void Shutdown()
    {
        CloseConnection(graceful: true, "QA run ended");
        PublishSafe();
    }

    internal byte SimHz => _connection != null && _connection.View.Joined ? _connection.View.SimHz : (byte)0;

    // 기능: 현재 연결을 닫는다(graceful이면 Dispose로 Disconnect를 보내고, 아니면 Abort). 연결이 없거나 이미 닫았으면 아무것도 하지 않는다.
    // 입력: graceful - 서버에 알리고 닫을지, reason - 기록할 종료 사유(서버가 먼저 닫았으면 서버 사유를 유지).
    // 출력: 반환값 없음. _closed와 _closeReason이 바뀐다.
    private void CloseConnection(bool graceful, string reason)
    {
        if (_connection == null || _closed) return;
        // A connection the server already closed keeps the server's reason.
        _closeReason = _connection.Disconnected ? _connection.DisconnectReason : reason;
        if (graceful) _connection.Dispose();
        else _connection.Abort();
        _closed = true;
    }

    // A brain that throws is dropped (its error published) so the actor keeps sending plain inputs instead of failing
    // every tick.
    // 기능: Brain에 이번 Tick의 의도를 정하게 한다. Brain이 예외를 던지면 오류를 기록하고 Brain을 버리며 의도를 초기화한다.
    // 입력: view - 이 Client의 상태, now - Pump 시계(초).
    // 출력: 반환값 없음. 의도가 바뀌거나, 실패 시 _brain이 null이 되고 _error가 남는다.
    private void Think(BotView view, float now)
    {
        ActorBrain brain = _brain!;
        try
        {
            brain.Think(this, view, now);
        }
        catch (Exception e)
        {
            _error = $"{brain.Role} behaviour stopped: {e.GetType().Name}: {e.Message}";
            _brain = null;
            ResetIntent();
        }
    }

    // 기능: Brain을 바꾼다(기존 Brain이 있으면 그 의도를 먼저 초기화한다).
    // 입력: brain - 새 Brain(null이면 없음).
    // 출력: 반환값 없음. _brain과 의도가 바뀐다.
    // The old brain's intent goes with it; the new one sets its own from the next tick.
    private void SetBrain(ActorBrain? brain)
    {
        if (_brain != null)
        {
            _brain = null;
            ResetIntent();
        }
        _brain = brain;
    }

    // D43: every input up to the snapshot's AckInputSeq has been applied by the server. Inputs older than the ring (a
    // long stall) are skipped rather than mismeasured.
    // 기능: Snapshot의 AckInputSeq까지 새로 확인된 입력마다 보낸 시각으로 왕복 지연을 Histogram에 기록한다(링보다 오래된 입력은 건너뜀)(D43).
    // 입력: c - 현재 연결.
    // 출력: 반환값 없음. Histogram과 _latencyAcked가 갱신된다(Histogram이 없으면 아무것도 안 함).
    private void TakeAcks(BotConnection c)
    {
        uint ack = c.View.AckInputSeq;
        if (_latency == null || ack <= _latencyAcked || ack > _latencySent) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double rtt = c.RoundTripTimeMs;
        uint oldest = _latencySent >= LatencyRing ? _latencySent - LatencyRing + 1 : 1;
        for (uint seq = Math.Max(_latencyAcked + 1, oldest); seq <= ack; seq++)
        {
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(_inputSentAt[seq % LatencyRing], now).TotalMilliseconds;
            _latency.Record(ms, rtt);
        }
        _latencyAcked = ack;
    }

    // ---- intent for brains (pump thread only, called from ActorBrain.Think) ----

    internal bool ViewAlive => _connection != null && _connection.View.Alive;
    internal bool MoveActive => _moveTarget != null;
    internal bool MoveArrived => _moveArrived;
    internal bool MoveGaveUp => _moveGaveUp;
    internal Vector3? MoveTarget => _moveTarget;
    internal bool ScriptIdle => _script.Count == 0 && _currentTicks <= 0 && _pendingRelease <= 0;
    internal int BuildsQueued => _builds.Count + _edits.Count;
    internal float PumpNow => _pump.Now;

    // 기능: Brain용 이동 목표를 둔다(새 목표면 조향을 다시 시작, 같은 목표면 유지, null이면 멈춤).
    // 입력: target - 목표 위치(null이면 정지), tolerance - 도착 허용 거리(m), sprint - 달릴지.
    // 출력: 반환값 없음. 이동 의도가 바뀐다.
    // A new target restarts steering; the same target keeps it going. Null stops walking.
    internal void BrainMoveTo(Vector3? target, float tolerance, bool sprint)
    {
        _moveSprint = sprint;
        _moveTolerance = tolerance;
        if (target == _moveTarget) return;
        _moveTarget = target;
        _moveArrived = false;
        _moveGaveUp = false;
        if (target != null && _connection != null)
        {
            _steering.Reset(_connection.View.MyPosition, target.Value, _pump.Now);
            _moveDistance = BotAim.HorizontalDistance(_connection.View.MyPosition, target.Value);
        }
    }

    // 기능: Brain용 로컬 이동 벡터를 바뀔 때까지 유지하게 둔다((0, 0)은 정지). 이동 목표가 없을 때만 쓰인다.
    // 입력: x - 옆 이동(-1..1), y - 앞 이동(-1..1).
    // 출력: 반환값 없음. 이동 벡터 의도가 바뀐다.
    // Local move input until changed; (0, 0) stops. Used only while there is no move target.
    internal void BrainVector(float x, float y)
    {
        _vectorX = Math.Clamp(x, -1f, 1f);
        _vectorY = Math.Clamp(y, -1f, 1f);
        _vectorTicks = x == 0f && y == 0f ? 0 : -1;
    }

    // 기능: Brain용 조준 대상을 다른 Actor의 Entity로 둔다.
    // 입력: entity - 대상 Entity id, fallback - Snapshot에 대상이 없을 때 위치를 읽을 Actor.
    // 출력: 반환값 없음. 조준 의도가 바뀐다.
    internal void BrainAimActor(ushort entity, IQaActor fallback)
    {
        _aim = AimMode.Actor;
        _aimEntity = entity;
        _aimFallback = fallback;
    }

    // 기능: Brain용 조준점을 둔다.
    // 입력: point - 조준할 월드 지점.
    // 출력: 반환값 없음. 조준 의도가 바뀐다.
    internal void BrainAimPoint(Vector3 point)
    {
        _aim = AimMode.Point;
        _aimPoint = point;
    }

    // 기능: Brain용 조준을 지운다.
    // 입력: 없음.
    // 출력: 반환값 없음. 조준 의도가 없어진다.
    internal void BrainClearAim()
    {
        _aim = AimMode.None;
        _aimFallback = null;
    }

    // 기능: Brain용으로 버튼을 계속 누르거나 뗀다.
    // 입력: buttons - 대상 버튼 Flag, down - true면 누름, false면 뗌.
    // 출력: 반환값 없음. _held가 바뀐다.
    internal void BrainHold(InputButtons buttons, bool down) => _held = down ? _held | buttons : _held & ~buttons;

    // 기능: Brain용 시간제 입력을 큐 뒤에 더한다(MaxScriptSteps를 넘으면 버린다).
    // 입력: steps - 더할 입력 단계들.
    // 출력: 반환값 없음. 입력 큐가 바뀐다.
    // Timed inputs after the ones already queued (fire presses with the weapon's interval, a jump, a slot key).
    internal void BrainScript(ReadOnlySpan<InputStep> steps)
    {
        if (_script.Count + steps.Length > MaxScriptSteps) return;
        foreach (InputStep step in steps) _script.Enqueue(step);
    }

    // One more piece in the build queue (bounded by MaxBuildQueue). Returns the request sequence it will be sent with
    // (requests go out in queue order), or -1 when the queue is full.
    // 기능: 건설 큐에 조각 하나를 더한다(MaxBuildQueue 한도).
    // 입력: plan - 놓을 조각 계획.
    // 출력: 그 요청이 보내질 순번, 큐가 가득 차면 -1.
    internal int BrainBuild(in BuildPlan plan)
    {
        if (_builds.Count >= MaxBuildQueue) return -1;
        if (_builds.Count == 0) _buildFirst = (ushort)(_buildSequence + 1);
        _builds.Enqueue(plan);
        _buildLast = (ushort)(_buildSequence + _builds.Count);
        return _buildLast;
    }

    // Stress abuse brains (D38 groupInvalidPackets): bytes as they are on this connection, now (BotConnection.SendRaw,
    // reliable). False when not connected. Counted in RawPacketsSent like sendInvalidPackets.
    // 기능: 바이트를 지금 바로 이 연결로 보낸다(D38 groupInvalidPackets).
    // 입력: packet - 보낼 바이트.
    // 출력: 보냈으면 true(RawPacketsSent 증가), 연결이 없거나 보내지 못하면 false.
    internal bool BrainSendRaw(byte[] packet)
    {
        BotConnection? c = _connection;
        if (c == null || _closed || !c.Connected || c.Disconnected || !c.SendRaw(packet)) return false;
        _rawSent++;
        return true;
    }

    // Stress abuse brains (D38 groupBuildSpam): a build request sent at once, without the aim ticks and spacing of the
    // build queue (a client that ignores the build rules). Takes the next request sequence, so the server's answers are
    // counted by code like every other build result. Returns the sequence, or -1 when not connected.
    // 기능: 건설 요청을 조준 Tick·간격 없이 지금 바로 보낸다(D38 groupBuildSpam).
    // 입력: piece - 조각 종류, material - 재료, x·y·z - 칸, rotation - 회전.
    // 출력: 요청에 쓴 순번, 연결이 없으면 -1.
    internal int BrainSendBuildNow(BuildPieceType piece, BuildMaterialType material, byte x, byte y, byte z, byte rotation)
    {
        BotConnection? c = _connection;
        if (c == null || _closed || !c.Connected || c.Disconnected) return -1;
        var request = new BuildRequest
        {
            Sequence = ++_buildSequence, Piece = (byte)piece, Material = (byte)material, X = x, Y = y, Z = z, Rotation = rotation,
        };
        c.SendBuild(request);
        return request.Sequence;
    }

    // 기능: 순번에 대한 서버 응답을 최근 건설 결과(최대 ActorState.MaxBuildResults)에서 찾는다.
    // 입력: sequence - 찾을 요청 순번, result - 찾은 결과(out).
    // 출력: 있으면 true와 결과, 없으면 false.
    // The server's answer to the request with this sequence, among the latest ActorState.MaxBuildResults.
    internal bool TryBuildResult(int sequence, out BuildResultInfo result)
    {
        foreach (BuildResultInfo r in _buildResults)
        {
            if (r.Sequence != sequence) continue;
            result = r;
            return true;
        }
        result = default;
        return false;
    }

    // 기능: 모든 의도(이동 목표·벡터, 조준, 누름, 입력 큐, 건설·편집 큐, 입력 멈춤, 재생)를 지운다. Brain은 유지한다.
    // 입력: 없음.
    // 출력: 반환값 없음. Actor가 가만히 서서 빈 입력만 보내게 된다.
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
        _edits.Clear();
        _inputPaused = false;
        StopPlayback();
    }

    // 기능: 편집 명령을 적용한다(Phase 13.5 D13). Burst면 지금 모두 보내고, 아니면 큐에 넣어 StepBuild가 하나씩 조준 뒤 보낸다.
    //   지정한 조각 id는 감시 목록에 넣어 ActorState.WatchedPieces로 Client가 아는 상태를 내보낸다.
    // 입력: e - 편집 명령.
    // 출력: 반환값 없음. 순번 범위(BuildFirstSequence..BuildLastSequence)가 정해지고, 넘치거나 연결이 없으면 _error가 남는다.
    private void ApplyEdit(BuildEditCommand e)
    {
        if (e.Edits.Count == 0 || e.Edits.Count > BuildEditCommand.MaxEdits || _builds.Count + _edits.Count + e.Edits.Count > MaxBuildQueue)
        {
            _error = $"Edit command of {e.Edits.Count} dropped (at most {BuildEditCommand.MaxEdits}, queue {MaxBuildQueue}).";
            return;
        }
        foreach (EditPlan plan in e.Edits) Watch(plan.PieceId);
        if (!e.Burst)
        {
            _buildFirst = (ushort)(_buildSequence + 1);
            foreach (EditPlan plan in e.Edits) _edits.Enqueue(plan);
            _buildLast = (ushort)(_buildSequence + e.Edits.Count);
            return;
        }
        BotConnection? c = _connection;
        if (c == null || _closed || !c.Connected || c.Disconnected || !c.View.Joined)
        {
            _error = "Not joined: edit requests not sent.";
            return;
        }
        _buildFirst = (ushort)(e.ReuseSequence ? _buildSequence : _buildSequence + 1);
        foreach (EditPlan plan in e.Edits)
        {
            ushort sequence = e.ReuseSequence ? _buildSequence : ++_buildSequence;
            c.SendBuildEdit(EditRequest(c.View, plan, sequence, out _));
        }
        _buildLast = _buildSequence;
    }

    // 기능: 감시할 조각 id를 더한다(같은 id는 한 번, 최대 ActorState.MaxWatchedPieces).
    // 입력: id - 조각 id(0은 무시).
    // 출력: 반환값 없음.
    private void Watch(uint id)
    {
        if (id == 0 || _watched.Contains(id) || _watched.Count >= ActorState.MaxWatchedPieces) return;
        _watched.Add(id);
        _watchedVersion = -1;
    }

    // 기능: 편집 계획을 이 Client가 아는 조각에 맞춰 실제 요청으로 만든다(대상 찾기, 상태 묶기, 조준점).
    // 입력: view - 이 Client의 상태, plan - 편집 계획, sequence - 보낼 순번, aimAt - 결과 조준점(조각 중심, 모르면 null).
    // 출력: 보낼 BuildEditRequest.
    private static BuildEditRequest EditRequest(BotView view, in EditPlan plan, ushort sequence, out Vector3? aimAt)
    {
        uint id = plan.PieceId;
        BuildPieceShape? known = null;
        if (id != 0 && view.PieceShapes.TryGetValue(id, out BuildPieceShape byId)) known = byId;
        if (id == 0 && plan.Slot is BuildPieceShape slot)
        {
            uint key = BuildGrid.SlotKey(slot);
            foreach (var pair in view.PieceShapes)
            {
                if (pair.Value.Type != slot.Type || BuildGrid.SlotKey(pair.Value) != key) continue;
                id = pair.Key;
                known = pair.Value;
                break;
            }
        }
        aimAt = known is BuildPieceShape shape ? BuildGrid.BoundsOf(shape).Center : null;
        ushort state = plan.RawState >= 0
            ? (ushort)plan.RawState
            : BuildEdit.PackState(plan.Edit, plan.Rotation >= 0 ? plan.Rotation : known?.Rotation ?? 0);
        return new BuildEditRequest { Sequence = sequence, PieceId = id, State = state };
    }

    // 기능: 감시 중인 조각을 이 Client의 상태에서 읽어 내보낼 표를 만든다. 보기가 바뀌었을 때만 새로 만든다.
    // 입력: view - 이 Client의 상태.
    // 출력: id(문자열) → 상태 표.
    private IReadOnlyDictionary<string, WatchedPiece> WatchedOf(BotView view)
    {
        if (_watchedVersion == view.PieceVersion) return _watchedPublished;
        var result = new Dictionary<string, WatchedPiece>(_watched.Count);
        foreach (uint id in _watched)
        {
            result[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = view.PieceShapes.TryGetValue(id, out BuildPieceShape s)
                ? new WatchedPiece(true, s.Edit, s.Rotation)
                : new WatchedPiece(false, 0, 0);
        }
        _watchedPublished = result;
        _watchedVersion = view.PieceVersion;
        return result;
    }

    // One replay tick (pure, tested): the entry at the cursor (clamped to the last one), with the buttons of every entry
    // skipped since the last sent one (speed > 1), then the cursor advances by speed. Done once the last entry has been
    // sent and the cursor passed the end, so the last entry is always sent (and held 1/speed ticks when speed < 1).
    // 기능: 재생 한 Tick(순수 함수). cursor 위치의 항목(마지막으로 제한)에 지난번 이후 건너뛴 항목의 버튼을 합치고 cursor를 speed만큼 전진시킨다.
    // 입력: inputs - 녹화 항목들, cursor - 재생 위치(ref, 전진), lastIndex - 마지막으로 보낸 index(ref), speed - Tick당 전진량.
    // 출력: (보낼 항목 index, 합친 버튼, 마지막 항목을 보냈고 cursor가 끝을 지났으면 true).
    internal static (int Index, InputButtons Buttons, bool Done) PlaybackStep(IReadOnlyList<RecordedInput> inputs, ref double cursor, ref int lastIndex, double speed)
    {
        int index = Math.Min(inputs.Count - 1, (int)cursor);
        InputButtons buttons = inputs[index].Buttons;
        for (int i = lastIndex + 1; i < index; i++) buttons |= inputs[i].Buttons;
        lastIndex = Math.Max(lastIndex, index);
        cursor += speed;
        return (index, buttons, lastIndex == inputs.Count - 1 && cursor >= inputs.Count);
    }

    // 기능: count개 항목을 speed로 재생할 때 걸리는 Tick 수를 PlaybackStep과 같은 규칙으로 센다.
    // 입력: count - 녹화 항목 수, speed - Tick당 전진량.
    // 출력: 재생 Tick 수(count가 0 이하면 0).
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

    // 기능: 재생을 일찍 끝내고 녹화를 버린다(완료로 표시하지 않는다).
    // 입력: 없음.
    // 출력: 반환값 없음. _playback이 null이 된다.
    // Ends a replay early (it did not complete) and drops the recording.
    private void StopPlayback() => _playback = null;

    // 기능: 이번 Tick에 보낼 녹화 입력을 만든다(D34). 마지막 항목을 보내고 끝을 지나면 재생을 끝내고 완료로 표시한다.
    // 입력: view - 이 Client의 상태(ViewTick용).
    // 출력: 보낼 InputCommand. _playSent·_bodyYaw가 갱신된다.
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

    // 기능: 연결 View에 새로 온 BuildResult를 코드별 계수와 최근 결과 큐(최대 MaxBuildResults)에 거둔다.
    // 입력: view - 이 Client의 상태.
    // 출력: 반환값 없음. 새 결과가 있었으면 발행용 코드 계수 배열을 새로 만든다.
    private void TakeBuildResults(BotView view)
    {
        // A burst bigger than the ring between two ticks loses the oldest (counted in BuildResultCount anyway).
        long from = Math.Max(_buildResultsSeen, view.BuildResultCount - BotView.RecentBuildResultCount);
        for (long i = from; i < view.BuildResultCount; i++)
        {
            BuildResult r = view.RecentBuildResults[i % BotView.RecentBuildResultCount];
            if ((int)r.Code < _buildCodeCounts.Length) _buildCodeCounts[(int)r.Code]++;
            if (_buildResults.Count >= ActorState.MaxBuildResults) _buildResults.Dequeue();
            _buildResults.Enqueue(new BuildResultInfo(r.Sequence, r.Code.ToString(), r.PieceId));
        }
        if (view.BuildResultCount != _buildResultsSeen) _buildCodesPublished = (long[])_buildCodeCounts.Clone();
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

    // 기능: 건설 한 단계(BotBuilder 순서). 지상 모드에서 건설 도구가 아니면 Q를 누르고, 다음 조각을 AimTicksPerBuild Tick 겨눈 뒤 이번 입력 다음에 요청을 보내게 하며, 마지막 조각 뒤 HoldAimTicks 동안 조준을 유지한다. 건설 큐가 비면 편집 큐를 처리한다.
    // 입력: view - 이 Client의 상태, buttons·aimYaw·aimPitch - 이번 입력(ref; 겨눔, Fire 제거, 도구 키가 반영된다).
    // 출력: 건설·편집·조준 유지 중이면 true, 할 일이 없거나 죽었거나 지상 모드가 아니면 false.
    private bool StepBuild(BotView view, ref InputButtons buttons, ref float aimYaw, ref float aimPitch)
    {
        if (_toolPressWait > 0) _toolPressWait--;
        if (!view.Alive) return false;
        if (_builds.Count == 0 && _edits.Count > 0 && view.Alive) return StepEdit(view, ref buttons, ref aimYaw, ref aimPitch);
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

    // 기능: 편집 한 단계(Phase 13.5): 다음 편집의 조각을 AimTicksPerBuild Tick 겨눈 뒤 이번 입력 다음에 요청을 보내게 한다.
    //   도구는 바꾸지 않는다(§29). 지상 모드가 아니면 기다린다.
    // 입력: view - 이 Client의 상태, buttons·aimYaw·aimPitch - 이번 입력(겨눔과 Fire 제거가 반영된다).
    // 출력: 편집 중이면 true.
    private bool StepEdit(BotView view, ref InputButtons buttons, ref float aimYaw, ref float aimPitch)
    {
        MovementMode mode = view.MyMode;
        if (mode != MovementMode.Ground && mode != MovementMode.Crouch && mode != MovementMode.Slide) return false;
        EditPlan plan = _edits.Peek();
        BuildEditRequest request = EditRequest(view, plan, (ushort)(_buildSequence + 1), out Vector3? aimAt);
        if (aimAt is Vector3 at) BotAim.Solve(BotAim.Eye(view.MyPosition), at, out aimYaw, out aimPitch);
        buttons &= ~InputButtons.Fire;
        if (_buildAimTicks < AimTicksPerBuild)
        {
            _buildAimTicks++;
            return true;
        }
        _edits.Dequeue();
        _buildAimTicks = 0;
        if (aimAt is Vector3 hold)
        {
            _buildHold = HoldAimTicks;
            _buildHoldAim = hold;
        }
        _buildSequence++;
        _sendEdit = request;
        return true;
    }

    // 기능: 입력 큐와 진행 중인 단계에서 Fire가 든 것을 버린다(stopFire).
    // 입력: 없음.
    // 출력: 반환값 없음. 입력 큐가 바뀌고 진행 중인 Fire 단계가 끝난다.
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

    // 기능: 시나리오 의도(이동·조준·버튼)로 이번 입력을 만든다(Phase 14: 기절한 표적은 낮은 몸 가운데를 겨눈다).
    // 입력: view - 이 Client가 아는 것, now - 지금 시각(초).
    // 출력: 보낼 InputCommand.
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
                // Phase 14: a knocked-down target is aimed at the middle of its low body.
                MovementMode targetMode = MovementMode.Ground;
                if (view.TryGetOther(_aimEntity, out SnapshotEntity other))
                {
                    target = other.Position;
                    targetMode = other.Mode;
                }
                else if (_aimFallback != null) target = _aimFallback.State.Position.ToVector3();
                else target = me;
                BotAim.Solve(BotAim.Eye(me), BotAim.Chest(target, targetMode), out aimYaw, out aimPitch);
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

    // 기능: 이번 Tick의 시간제 입력 버튼을 정한다(진행 중인 단계 → 뗌 대기 → 큐의 다음 단계. FirePress는 1 Tick 누른 뒤 현재 무기의 발사 간격만큼 뗀다).
    // 입력: view - 현재 무기의 발사 간격을 읽을 Client 상태.
    // 출력: 이번 Tick에 누를 버튼(없으면 None). 새 단계를 시작하면 PressesSent가 는다.
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

    // 기능: 이번 Pump Tick의 ActorState를 만들어 발행한다(Phase 14: 팀, 구성원 상태, 카드, 기절 소식, 채널, 스테이션, Phase 15: 팀 Ping·Waypoint,
    //   Phase 16: Container 수·Supply Drop 상태, Phase 17: 수류탄 수와 들은 투사체 사건 수, Phase 18: ShotFired·실드 플래그·붕괴·WorldSound 수, Phase 19: 탄 차량·좌석·VehicleStates 수).
    // 입력: 없음.
    // 출력: 반환값 없음. State가 바뀐다.
    private void Publish()
    {
        BotConnection? c = _connection;
        ActorState state;
        if (c == null)
        {
            state = new ActorState
            {
                Alias = Alias, DevPlayerId = DevPlayerId, Status = ActorStatus.Idle, LastCommandId = _lastCommandId, Error = _error,
                InputPaused = _inputPaused, BuildFirstSequence = _buildFirst, BuildLastSequence = _buildLast, BuildsQueued = _builds.Count + _edits.Count,
                PlaybackCommandId = _playCommandId, PlaybackActive = _playback != null, PlaybackSent = _playSent, PlaybackCompleted = _playCompleted,
                Role = _brain?.Role,
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
                ReserveAmmo = weapon != null ? v.Reserve(weapon.Value.AmmoType) : 0,
                CurrentSlot = v.Inventory.CurrentSlot,
                Tool = v.Self.Tool.ToString(),
                HasInventory = v.HasInventory,
                WeaponId = weapon?.WeaponId ?? 0,
                WeaponName = weapon?.Name ?? string.Empty,
                WeaponAutomatic = weapon?.Automatic ?? false,
                SlotEquipTicks = SlotEquipTicks(v),   // review fix C2
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
                Grenades = v.Inventory.Grenades,                // Phase 17
                ProjectilesSpawned = v.ProjectilesSpawned,
                ProjectileStates = v.ProjectileStates,
                ProjectilesExploded = v.ProjectilesExploded,
                ShotsSeen = v.ShotsSeen,                        // Phase 18
                LastShotShooterId = v.LastShotShooterId,
                LastShotWeaponId = v.LastShotWeaponId,
                ShieldHitsTaken = v.ShieldHitsTaken,
                ShieldBreaksTaken = v.ShieldBreaksTaken,
                CollapsesSeen = v.CollapsesSeen,
                WorldSounds = v.WorldSoundsReceived,
                LastWorldSoundKind = v.WorldSoundsReceived > 0 ? v.LastWorldSoundKind.ToString() : string.Empty,
                LastWorldSoundSource = v.LastWorldSoundSource,
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
                BuildsQueued = _builds.Count + _edits.Count,
                BuildResults = _buildResults.ToArray(),
                WatchedPieces = WatchedOf(v),
                PlaybackCommandId = _playCommandId,
                PlaybackActive = _playback != null,
                PlaybackSent = _playSent,
                PlaybackCompleted = _playCompleted,
                Role = _brain?.Role,
                BuildCodeCounts = _buildCodesPublished,
                TeamId = v.HasTeam ? v.Team.TeamId : 0,
                TeamIds = TeamIdsOf(v),
                TeamStates = TeamStatesOf(v),
                RebootCards = v.HasInventory ? v.Inventory.RebootCards : 0,
                DownsSeen = v.DownsSeen,
                ChannelActive = v.HasChannel && v.LastChannel.Active,
                ChannelKind = v.HasChannel ? v.LastChannel.Kind.ToString() : string.Empty,
                ChannelActor = v.HasChannel ? v.LastChannel.ActorId : 0,
                StationsCooling = v.Stations.CooldownMask,
                PingCount = v.PingCount,
                Pings = PingsOf(v),
                WaypointCount = v.WaypointCount,
                Waypoints = _waypointsPublished,
                TeamMarkersReceived = v.TeamMarkersReceived,
                MapMarkersSent = c.MapMarkersSent,
                ContainersSpawned = System.Numerics.BitOperations.PopCount(v.ContainersSpawned),
                ContainersOpened = System.Numerics.BitOperations.PopCount(v.ContainersOpened),
                ContainerStatesReceived = v.ContainerStatesReceived,
                SupplyDropCount = v.SupplyDropCount,
                SupplyDropStates = SupplyDropStatesOf(v),
                SupplyDropsReceived = v.SupplyDropsReceived,
                VehicleId = v.MyVehicleId,
                Seat = v.MySeat,
                VehicleCount = v.VehicleCount,
                VehicleStatesReceived = v.VehicleStatesReceived,
                Error = _error,
            };
        }
        Volatile.Write(ref _state, state);
    }

    // 기능: Phase 16: 마지막 SupplyDrops의 상태 이름 목록을 발행용 배열로 만든다. 새 SupplyDrops가 왔을 때만 새로 만든다.
    // 입력: v - 봇 View.
    // 출력: 칸 순서의 SupplyDropState 이름 배열.
    private string[] SupplyDropStatesOf(BotView v)
    {
        if (_dropsVersion == v.SupplyDropsReceived) return _dropStatesPublished;
        var states = new string[v.SupplyDropCount];
        for (int i = 0; i < states.Length; i++) states[i] = v.SupplyDrops[i].State.ToString();
        _dropStatesPublished = states;
        _dropsVersion = v.SupplyDropsReceived;
        return states;
    }

    // 기능: Phase 15: 마지막 TeamMarkers의 Ping·Waypoint 목록을 발행용 배열로 만든다. 새 TeamMarkers가 왔을 때만 새로 만든다
    //   (Waypoint 배열도 같이 갱신한다).
    // 입력: v - 봇 View.
    // 출력: Ping 배열(_waypointsPublished도 같은 버전으로 맞춰진다).
    private ActorPing[] PingsOf(BotView v)
    {
        if (_markersVersion == v.TeamMarkersReceived && _pingsPublished.Length == v.PingCount && _waypointsPublished.Length == v.WaypointCount) return _pingsPublished;
        var pings = new ActorPing[v.PingCount];
        for (int i = 0; i < pings.Length; i++)
        {
            MarkerPing p = v.Pings[i];
            pings[i] = new ActorPing(p.Id, p.Kind.ToString(), p.OwnerId, Vec3.From(p.Position), p.EndTick, p.TargetId);
        }
        var waypoints = new ActorWaypoint[v.WaypointCount];
        for (int i = 0; i < waypoints.Length; i++) waypoints[i] = new ActorWaypoint(v.Waypoints[i].OwnerId, Vec3.From(v.Waypoints[i].Position));
        _pingsPublished = pings;
        _waypointsPublished = waypoints;
        _markersVersion = v.TeamMarkersReceived;
        return pings;
    }

    // 기능: Phase 14: 마지막 TeamState의 구성원 Entity id 목록을 만든다(상태 발행용, 최대 4개).
    // 입력: v - 봇 View.
    // 출력: id 배열(팀이 없으면 빈 배열).
    private static ushort[] TeamIdsOf(BotView v)
    {
        if (!v.HasTeam) return Array.Empty<ushort>();
        var ids = new ushort[v.Team.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = v.Team.Get(i).EntityId;
        return ids;
    }

    // 기능: Phase 14: 마지막 TeamState의 구성원 상태 이름 목록을 만든다(TeamIds와 같은 순서).
    // 입력: v - 봇 View.
    // 출력: 상태 이름 배열(팀이 없으면 빈 배열).
    private static string[] TeamStatesOf(BotView v)
    {
        if (!v.HasTeam) return Array.Empty<string>();
        var states = new string[v.Team.Count];
        for (int i = 0; i < states.Length; i++) states[i] = v.Team.Get(i).State.ToString();
        return states;
    }

    // 기능: 인벤토리 칸마다 든 무기의 교체 대기 Tick을 받은 카탈로그에서 읽는다(리뷰 수정 C2, QA fire·switchWeapon이 칸 교체 뒤 기다린다).
    // 입력: v - 봇 화면 상태.
    // 출력: 칸 수만큼의 EquipTicks(빈 칸·모르는 무기는 0). 카탈로그가 없으면 빈 배열.
    private static int[] SlotEquipTicks(BotView v)
    {
        if (v.Weapons == null) return Array.Empty<int>();
        var ticks = new int[ItemConstants.WeaponSlotCount];
        for (int i = 0; i < ticks.Length; i++) ticks[i] = v.WeaponInSlot(i)?.EquipTicks ?? 0;
        return ticks;
    }

    // 기능: 최신 Snapshot에 보이는 다른 플레이어의 Entity id 목록을 만든다.
    // 입력: v - 봇 View.
    // 출력: Entity id 배열(없으면 빈 배열).
    private static ushort[] VisibleIds(BotView v)
    {
        if (v.OtherCount == 0) return Array.Empty<ushort>();
        var ids = new ushort[v.OtherCount];
        for (int i = 0; i < ids.Length; i++) ids[i] = v.Others[i].EntityId;
        return ids;
    }
}
