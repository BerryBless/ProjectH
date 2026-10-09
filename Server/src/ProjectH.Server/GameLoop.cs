using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Net;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server;

// Owns the NetManager, the Match and the dedicated simulation thread.
//
// Threads:
//   - LiteNetLib threads run NetworkListener and write to InboundChannels.
//   - The "GameLoop" thread is the only reader of the channels and the only thread that touches
//     Match, _peers and _stalePeers (and replaces Match on a reset, D6).
//   - Stop() runs on the host's thread once the loop thread has stopped (or after its join limit, D7).
// This class takes no locks, so there is no lock ordering to keep.
public sealed class GameLoop : IDisposable
{
    // Phase 10 D6: a match that fails this many seconds of ticks in a row is reset; three resets within ResetWindow
    // stop the server. D7: how long Stop waits for the loop thread, and for clients to take the shutdown notice.
    private const int FailingSecondsBeforeReset = 3;
    private const int MaxResetsInWindow = 3;
    private static readonly TimeSpan ResetWindow = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ThreadJoinTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ShutdownNoticeTimeout = TimeSpan.FromSeconds(1);
    // Phase 13 final review A4: a joined connection whose reliable queues (channels 0 and 1) hold more than this many
    // packets for CongestedSeconds in a row is closed with Congested: its link cannot take the game's traffic, and the
    // queue (LiteNetLib memory) would only grow.
    public const int MaxReliableBacklog = 512;
    public const int CongestedSeconds = 10;

    private readonly ServerOptions _options;
    private readonly ILogger _logger;
    private readonly ServerStats _stats = new();
    private readonly TickMetrics _tickMetrics = new();
    // Phase 10 D9: totals for the Health line and the Meter, shared with the network threads (Interlocked).
    private readonly HealthCounters _health = new();
    private readonly InboundChannels _channels;
    private readonly NetworkListener _listener;
    private readonly NetManager _net;
    // What a new Match is built from (D6 reset).
    private readonly GameData _data;
    private readonly StartingLoadout? _loadout;
    private readonly System.Numerics.Vector3[]? _dropPoints;
    private readonly Action<Persistence.MatchRecord>? _matchSink;
    // Phase 11 D8: requests in (network threads), answers out (this thread sends them). Buffer for one answer.
    private readonly StatsQueryQueue _statsQueries;
    private readonly byte[] _statsReplyBuffer = new byte[StatsResponse.MaxSize];
    private Match _match;
    // Phase 13 D18: the current match's refusals by reason, for HealthCounters (one delegate, made once).
    private readonly Func<ProjectH.Shared.Protocol.BuildResultCode, long> _buildRejects;
    // Removed on Disconnected messages and by the stale-peer sweep; never outlives the connection.
    private readonly Dictionary<int, NetPeer> _peers = new();
    private readonly List<int> _stalePeers = new();
    // Phase 10 D3, D4: peers the sweep found past a timeout, closed after the sweep. Cleared every tick.
    private readonly List<(int PeerId, DisconnectCode Code)> _timedOut = new();
    // Ticks run by this loop. Timeouts count on it, not on Match.ServerTick, so they survive a match reset (D6).
    private long _loopTick;
    private readonly long _joinTimeoutTicks;
    private readonly long _inputTimeoutTicks;   // 0 = off
    private readonly long _congestedTicks;
    // Final review A4: Match asks this (one delegate, made once) for a peer's building-channel backlog.
    private readonly Func<int, int> _buildBacklog;
    // Review fixes C3, C7: made once (NewMatch runs again on every reset): a peer's RTT for the rewind allowance, and the
    // movement anomaly report.
    private readonly Func<int, int> _rttOf;
    private readonly Action<ushort> _movementAnomaly;
    private volatile Func<NetPeer, byte, int>? _queueProbe;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private int _stopping;   // Stop runs once (Interlocked), even when Dispose calls it again while it runs
    private long _exceptionCount;
    private long _exceptionsSinceStats;
    // Phase 10 D6 (game loop thread only): failed ticks in a row, the times of the latest resets, and whether the
    // server gave up (then the loop only waits for Stop).
    private readonly int _failuresBeforeReset;
    private int _consecutiveTickFailures;
    private readonly long[] _resetTimestamps = new long[MaxResetsInWindow];
    private int _resetCount;
    private bool _fatal;
    // The sink error a reset took from the match it threw away, until the next stats line logs it (D6).
    private Exception? _carriedSinkError;
    private long _loopFailuresSinceStats;
    private long _playerFailuresSinceStats;   // server review M7: the first player failure of an interval is logged
    // Review round 1, review fix A6 (game loop thread only): the loop tick and the source (ConnectRateLimiter slot, -1 =
    // unknown) of the latest player failures, a ring of max(MaxPlayers, PenaltyFailures) made once; nothing else grows.
    // MaxPlayers of them within PlayerFailureWindowSeconds from at least two sources are taken as a fault in the match,
    // not in one player (every player failing every tick, reconnecting and failing again): the match is reset like after
    // failing ticks (D6). From one source they are that source's doing (SEC-14: one attacker must not reset or stop the
    // server): PenaltyFailures of them within PenaltyWindowSeconds penalize the address (refused for PenaltySeconds). An
    // unknown source (a graced player, a test without a connection) counts as a source of its own. Emptied by every reset.
    private const int PlayerFailureWindowSeconds = 10;
    private const int PenaltyFailures = 3;
    private const int PenaltyWindowSeconds = 60;
    private const int PenaltySeconds = 60;
    private readonly long _playerFailureWindowTicks;
    private readonly long _penaltyWindowTicks;
    private readonly FailureMark[] _playerFailureMarks;
    private int _playerFailureCount;
    private int _playerFailureNext;

    private struct FailureMark
    {
        public long Tick;
        public int Slot;
    }
    private bool _resetForPlayerFailures;
    // Review round 2 (game loop thread only): the loop ticks of the latest ticks in which every player failed, a ring of
    // AllFailedTicksBeforeReset made once. A player's own bad state goes with it, so a fresh player (a rejoin, a newcomer)
    // failing again and again with everyone else is a fault in the match, even when too few clients are connected to
    // reach MaxPlayers failures (one client failing at every reconnect fails about 9 times in 10 s). 5 such ticks within
    // the window: enough rejoins that a single unlucky player state is ruled out, still well inside the window at a
    // normal reconnect pace. Emptied by every reset.
    private const int AllFailedTicksBeforeReset = 5;
    private readonly long[] _allFailedTicks = new long[AllFailedTicksBeforeReset];
    private int _allFailedCount;
    private int _allFailedNext;
    private bool _spawnEncodeFailureLogged;   // Phase 11: the first PlayerSpawned encode failure was logged
    private readonly Action? _onFatal;
    private readonly TimeProvider _time;
    // Phase 10 D9: when the last tick attempt ended (TimeProvider timestamp), read by StallWatchdog on a timer thread.
    private long _lastTickTimestamp;
    // Test seams (D6): run at the start of every tick / after every tick, outside it. Set by tests only.
    private volatile Action? _tickFaultHook;
    private volatile Action? _loopFaultHook;
    private volatile Action? _removeFaultHook;   // runs in DropSession (after the peer left _peers, or was replaced there)
    // Phase 7 D10: process CPU time at the previous stats line, for cpu% (game loop thread only).
    private TimeSpan _cpuAtLastStats = CurrentCpuTime();
    private long _lateTicksSkipped;
    private bool _disposed;
    // QA-1 D2, D5: the QA Control executor, attached before Start only in QA mode (null otherwise: one null check per
    // tick). It runs on this thread at the end of every tick and never throws. _lastTickMs: the duration of the latest
    // tick attempt (game loop thread only), which the QA metrics ring records.
    private Qa.QaControl? _qa;
    private double _lastTickMs;

    // loadout: test seam (D1); null = StartingLoadout.Empty, the production start. dropPoints: test seam (Phase 6 D9);
    // null = the map's DropPoints.All.
    // matchSink: Phase 9, where finished matches are recorded (MatchHistoryQueue.TryEnqueue; null = not recorded).
    // onFatal: Phase 10 D6, called once (on the loop thread, must not block) when resets keep failing; production stops
    // the host with exit code 1. time: the clock of the reset window (tests pass a manual one).
    // statsQueries: Phase 11 D8, the statistics path shared with StatsQueryService; null = a queue nobody answers (tests
    // that do not need answers), so requests wait there and, once it is full, are answered Busy.
    // identity: review fix B1, the server key (GameServerService loads it with the environment); null = the development key
    // next to the server (tests). The loop owns it and disposes it.
    // 기능: Game Loop와 NetManager·채널·수신 처리기·첫 경기를 만든다(Phase 15: Marker 채널과 map.json 속도 제한 수치를 넘긴다.
    //   리뷰 수정 A1: 조각 상한 MaxFragments, A5: Build 채널에 building.json 초당 요청 수, A6: 출처를 적는 실패 Ring,
    //   B1·B3: 서버 키와 데이터그램 인증 계층, MtuOverride = UserMtu).
    // 입력: options - 서버 설정, data - 게임 데이터, logger - 로그, 나머지 - 위 설명의 테스트용·선택 인자.
    // 출력: Start 전의 GameLoop.
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null,
        Action? onFatal = null, TimeProvider? time = null, StatsQueryQueue? statsQueries = null, ServerIdentity? identity = null)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));

        _options = options;
        _logger = logger;
        _data = data;
        _loadout = loadout;
        _dropPoints = dropPoints;
        _matchSink = matchSink;
        _onFatal = onFatal;
        _time = time ?? TimeProvider.System;
        _failuresBeforeReset = options.SimHz * FailingSecondsBeforeReset;
        _playerFailureWindowTicks = (long)PlayerFailureWindowSeconds * options.SimHz;
        _penaltyWindowTicks = (long)PenaltyWindowSeconds * options.SimHz;
        _playerFailureMarks = new FailureMark[Math.Max(options.MaxPlayers, PenaltyFailures)];
        _channels = new InboundChannels(options, _stats, _health.AddBuildInboxDrop, _health.AddMarkerInboxDrop, data.Building.MaxRequestsPerSecond);
        _joinTimeoutTicks = (long)options.JoinTimeoutSeconds * options.SimHz;
        _inputTimeoutTicks = (long)options.InputTimeoutSeconds * options.SimHz;
        _congestedTicks = (long)CongestedSeconds * options.SimHz;
        _buildBacklog = BuildBacklog;
        _rttOf = RttOf;
        _movementAnomaly = OnMovementAnomaly;
        _playerFailed = OnPlayerFailed;
        _statsQueries = statsQueries ?? new StatsQueryQueue();
        _health.StatsQueries = () => _statsQueries.Counts;
        _identity = identity ?? ServerIdentity.Load(options, isProduction: false, AppContext.BaseDirectory);
        // Review fix B3: keys of a closed connection stay DisconnectTimeout + 1 s (its disconnect resends stay sealed). Review B
        // round 1: the cap holds every connection the accept buckets can let in within that window (ServerOptions.MaxRetiredAuthKeys).
        _auth = new AuthPacketLayer(_health, options.AuthKeyRetireMs, options.MaxRetiredAuthKeys);
        _listener = new NetworkListener(options, _channels, _stats, _health, _statsQueries, logger, _identity, _auth,
            data.Building.MaxRequestsPerSecond, data.Map);
        _net = new NetManager(_listener, _auth)
        {
            UnsyncedEvents = true,
            AutoRecycle = true,
            DisconnectTimeout = options.DisconnectTimeoutMs,
            // The timeout is reset only by packets from the client. An idle client (e.g. waiting in
            // a menu) sends little besides pongs to our pings, so ping at least 4 times per timeout;
            // with LiteNetLib's default 1000 ms and a short timeout, live clients were timed out.
            PingInterval = Math.Min(1000, options.DisconnectTimeoutMs / 4),
            // Snapshots are sent Sequenced (never fragmented) and can be MaxPacketSize bytes. Review fix B3: LiteNetLib does not
            // count the layer's 20-byte tail in the MTU, so the user MTU leaves room for it (ProtocolLimits.UserMtu).
            MtuOverride = ProtocolLimits.UserMtu,
            // Phase 13 D13: channel 0 as before, channel 1 the building stream.
            ChannelsCount = ProtocolConstants.ChannelCount,
            UnconnectedMessagesEnabled = false,
            IPv6Enabled = false,
            // Review fix A1 (SEC-1): no client packet needs fragments, so a message of more fragments is refused by
            // LiteNetLib before reassembly (the default, 65535, let one connection hold tens of MB per message).
            MaxFragmentsCount = ProtocolLimits.MaxFragments,
        };
        _listener.Manager = _net;
        _match = NewMatch();
        _buildRejects = code => _match.BuildResults(code);
    }

    // Review fixes B1, B3: the server key and the datagram authentication layer (see the constructor).
    private readonly ServerIdentity _identity;
    private readonly AuthPacketLayer _auth;
    internal AuthPacketLayer Auth => _auth;
    internal ServerIdentity Identity => _identity;

    // Server review M7: made once (NewMatch runs again on every reset).
    private readonly Action<int, Exception> _playerFailed;

    // 기능: 새 경기 객체를 만든다(경기 초기화마다. 리뷰 수정 C3: 연결 RTT 질의, C7: 이동 이상 보고 포함).
    // 입력: 없음.
    // 출력: 대기 상태의 Match.
    private Match NewMatch() => new(_options, _data, SendToPeer, _loadout, dropPoints: _dropPoints, matchSink: _matchSink,
        graceExpired: OnGraceExpired, movementAnomaly: _movementAnomaly, sendBuild: SendToPeerBuild, buildBacklog: _buildBacklog,
        playerFailed: _playerFailed, rttOf: _rttOf);

    // 기능: 연결의 RTT를 돌려준다(리뷰 수정 C3, Game Loop 스레드). SweepPeers가 Tick마다 PeerState.RttMs를 갱신한다.
    // 입력: peerId - 연결 id.
    // 출력: RTT(ms). 모르는 연결이면 0(가장 좁은 되감기).
    private int RttOf(int peerId) => _peers.TryGetValue(peerId, out NetPeer? peer) && peer.Tag is PeerState state ? state.RttMs : 0;

    // 기능: Match가 알린 이동 이상 하나를 Health에 세고 그 Entity id를 Debug로 남긴다(리뷰 수정 C7, Game Loop 스레드).
    // 입력: entityId - 이상이 난 플레이어의 Entity id(기록에는 그 플레이어의 MovementAnomalies로 남는다).
    // 출력: 반환값 없음.
    private void OnMovementAnomaly(ushort entityId)
    {
        _health.AddMovementAnomaly();
        _logger.LogDebug("Movement anomaly: entity {EntityId}", entityId);
    }

    // Server review M7: Match took a player whose own tick threw out of the match. Its connection is closed with ServerError
    // (the client may reconnect and join as a new player) and forgotten here at once, without calling back into Match (it
    // is mid-tick). A graced player (NoPeer) has no connection. Counted every time, logged once per interval.
    // Review round 1: also recorded in the failure window; RunTickGuarded resets the match after this tick when it is full.
    // 기능: Match가 자기 Tick에서 예외가 난 플레이어를 뺐을 때 부른다. 실패를 출처(연결의 ConnectRateLimiter 칸)와 함께 기록하고(리뷰 수정 A6),
    //   리셋 조건이면 Tick 뒤 리셋을 예약하고, 연결을 ServerError로 닫는다.
    // 입력: peerId - 실패한 플레이어의 연결 id(유예 중이면 NoPeer), error - 예외.
    // 출력: 반환값 없음. 실패 Ring·벌점·연결 상태가 바뀐다.
    private void OnPlayerFailed(int peerId, Exception error)
    {
        _health.AddPlayerFailure();
        int slot = peerId != PlayerEntity.NoPeer && _peers.TryGetValue(peerId, out NetPeer? failedPeer) && failedPeer.Tag is PeerState failedState
            ? failedState.ConnectSlot : -1;
        if (RecordPlayerFailure(slot)) _resetForPlayerFailures = true;
        if (++_playerFailuresSinceStats == 1)
        {
            try
            {
                _logger.LogError(error, "A player's tick failed; peer {PeerId} left the match and is closed with ServerError (first of this interval)", peerId);
            }
            catch
            {
                // The failure is counted; a throwing logger must not stop the close below.
            }
        }
        if (peerId == PlayerEntity.NoPeer || !_peers.Remove(peerId, out NetPeer? peer)) return;
        _health.AddKick(DisconnectCode.ServerError);
        NetworkListener.Close(peer, DisconnectCode.ServerError);
    }

    // 기능: 플레이어 실패 하나를 (지금 loop tick, 출처 칸)으로 Ring에 적고 판단한다(리뷰 1차, 리뷰 수정 A6). 같은 출처가
    //   PenaltyWindowSeconds 안에 PenaltyFailures번이면 그 주소에 벌점을 준다(ConnectRateLimiter.Penalize, Interlocked).
    // 입력: slot - 실패한 연결의 ConnectRateLimiter 칸(-1 = 모름: 각자 다른 출처로 센다).
    // 출력: 창 안 실패가 MaxPlayers번 이상이고 출처가 둘 이상이면 true(경기 리셋), 아니면 false.
    private bool RecordPlayerFailure(int slot)
    {
        FailureMark[] ring = _playerFailureMarks;
        ring[_playerFailureNext] = new FailureMark { Tick = _loopTick, Slot = slot };
        _playerFailureNext = (_playerFailureNext + 1) % ring.Length;
        if (_playerFailureCount < ring.Length) _playerFailureCount++;

        int inWindow = 0;
        int sameSource = 0;
        for (int i = 0; i < _playerFailureCount; i++)
        {
            FailureMark mark = ring[(_playerFailureNext - 1 - i + ring.Length) % ring.Length];
            long age = _loopTick - mark.Tick;
            if (age < _playerFailureWindowTicks) inWindow++;
            if (slot >= 0 && mark.Slot == slot && age < _penaltyWindowTicks) sameSource++;
        }
        if (sameSource >= PenaltyFailures)
        {
            _listener.ConnectRate.Penalize(slot, Environment.TickCount64 + PenaltySeconds * 1000L);
            _health.AddPenalty();
        }
        return inWindow >= _options.MaxPlayers && HasTwoSourcesInWindow();
    }

    // 기능: 실패 Ring의 창 안 항목에 서로 다른 출처가 둘 이상 있는지 본다(모르는 출처 -1은 각자 다른 출처). 리셋 판단 때만 불린다
    //   (항목 수 ≤ MaxPlayers ≤ 100이라 이중 반복이어도 짧다).
    // 입력: 없음.
    // 출력: 둘 이상이면 true.
    private bool HasTwoSourcesInWindow()
    {
        FailureMark[] ring = _playerFailureMarks;
        int first = int.MinValue;
        for (int i = 0; i < _playerFailureCount; i++)
        {
            FailureMark mark = ring[(_playerFailureNext - 1 - i + ring.Length) % ring.Length];
            if (_loopTick - mark.Tick >= _playerFailureWindowTicks) continue;
            if (mark.Slot < 0) return true;   // an unknown source is one of its own; with any other entry that makes two
            if (first == int.MinValue) first = mark.Slot;
            else if (mark.Slot != first) return true;
        }
        return false;
    }

    // 기능: 지금 loop tick을 Ring에 적는다(가득 차면 가장 오래된 항목을 덮어쓴다).
    // 입력: ring - Tick을 적는 Ring, count - Ring에 든 항목 수(갱신), next - 다음 쓸 자리(갱신).
    // 출력: Ring이 가득 찼고 가장 오래된 항목이 PlayerFailureWindowSeconds 안이면 true, 아니면 false.
    // Writes the current loop tick into the ring; true when the ring is full and its oldest entry is within the window.
    private bool RecordInWindow(long[] ring, ref int count, ref int next)
    {
        ring[next] = _loopTick;
        next = (next + 1) % ring.Length;
        if (count < ring.Length) count++;
        return count == ring.Length && _loopTick - ring[next] < _playerFailureWindowTicks;
    }

    // 기능: 재접속 없이 유예가 끝난 플레이어를 Health에 세고 Information으로 남긴다(Match가 부른다, Game Loop 스레드).
    // 입력: devPlayerId - 떠난 플레이어의 개발용 id.
    // 출력: 반환값 없음.
    // D2, D9: a graced player left without resuming (at most MaxPlayers per round, so logging each is cheap).
    private void OnGraceExpired(string devPlayerId)
    {
        _health.AddGraceExpiry();
        _logger.LogInformation("Reconnect grace of {DevPlayerId} ended without a resume; the character left the match", devPlayerId);
    }

    public int LocalPort => _net.LocalPort;
    // Test seams (InternalsVisibleTo ProjectH.Server.Tests): tests that call RunTick() directly
    // must not Start() the loop, so the calling test thread is the only one touching Match.
    internal InboundChannels Channels => _channels;
    internal Match Match => _match;
    internal NetworkListener Listener => _listener;
    internal StatsQueryQueue StatsQueries => _statsQueries;
    // QA-1: read by the QA executor on this loop's thread only.
    internal GameData Data => _data;
    internal ServerOptions Options => _options;
    internal ServerStats Stats => _stats;
    internal int PeerCount => _peers.Count;
    internal double LastTickMs => _lastTickMs;

    // 기능: QA Control 실행기를 붙이고 이 Loop에 묶는다(QA-1 D2). Start 전에만 되고, 이미 시작했거나 붙어 있으면 InvalidOperationException.
    // 입력: qa - 붙일 QA 실행기(null이면 ArgumentNullException).
    // 출력: 반환값 없음. 이후 매 Tick 끝에 qa.OnTick이 불린다.
    // QA-1 D2: attaches the QA executor. Before Start only, so the loop thread never sees the field change.
    internal void AttachQa(Qa.QaControl qa)
    {
        ArgumentNullException.ThrowIfNull(qa);
        if (_thread != null) throw new InvalidOperationException("The QA executor must be attached before the game loop starts.");
        if (_qa != null) throw new InvalidOperationException("A QA executor is already attached.");
        _qa = qa;
        qa.Bind(this);
    }
    internal Action? TickFaultHook { get => _tickFaultHook; set => _tickFaultHook = value; }
    internal Action? RemoveFaultHook { get => _removeFaultHook; set => _removeFaultHook = value; }
    internal Action? LoopFaultHook { get => _loopFaultHook; set => _loopFaultHook = value; }
    // Test seam (final review A4): the reliable packets queued for a peer on a channel; null = LiteNetLib's count.
    internal Func<NetPeer, byte, int>? QueueProbe { get => _queueProbe; set => _queueProbe = value; }
    // Ticks run so far; readable from any thread (tests).
    internal long LoopTicks => Interlocked.Read(ref _loopTick);
    public HealthCounters Health => _health;
    public TimeProvider Time => _time;
    // D9: any thread. Set when the loop starts and after every tick attempt, failed or not.
    public long LastTickTimestamp => Volatile.Read(ref _lastTickTimestamp);
    public bool IsRunning => _thread != null && _thread.IsAlive;

    // 기능: UDP 포트를 열고 "GameLoop" 배경 스레드를 시작한다. 이미 시작했거나 포트를 못 열면 InvalidOperationException.
    // 입력: 없음.
    // 출력: 반환값 없음. 연결을 받기 시작하고 Tick이 돈다.
    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("GameLoop already started.");
        if (!_net.Start(_options.Port)) throw new InvalidOperationException($"Failed to bind UDP port {_options.Port}.");

        // Background: a loop thread that never returns (a deadlock, an endless loop) must not keep the process alive
        // after the host has finished shutting down (D7). Stop still joins it first.
        _thread = new Thread(Run) { Name = "GameLoop", IsBackground = true };
        Volatile.Write(ref _lastTickTimestamp, _time.GetTimestamp());
        _thread.Start();
        _logger.LogInformation("Server listening on UDP {Port} (SimHz {SimHz}, SnapshotHz {SnapshotHz}, MaxPlayers {MaxPlayers})",
            _net.LocalPort, _options.SimHz, _options.SnapshotHz, _options.MaxPlayers);
        // Phase 13 final review C: a load-test switch must never go unnoticed on a real server.
        if (_options.BuildInfiniteResources)
            _logger.LogWarning("Server:BuildInfiniteResources is on: building costs no resources (load tests only)");
    }

    // 기능: 기본 대기 시간(ThreadJoinTimeout 5초)으로 Stop(joinTimeout)을 부른다.
    // 입력: 없음.
    // 출력: 반환값 없음. Loop 스레드·연결·소켓이 닫힌다.
    public void Stop() => Stop(ThreadJoinTimeout);

    // 기능: 서버를 멈춘다(Phase 10 D7): 새 연결 거부 → Tick 중단과 스레드 Join → 모든 연결에 ServerShutdown → 통지 확인을 최대 ShutdownNoticeTimeout 대기 → 소켓 닫기. 한 번만 실행된다.
    // 입력: joinTimeout - Loop 스레드가 끝나길 기다리는 최대 시간(넘기면 Critical을 남기고 배경 스레드로 버려둔다).
    // 출력: 반환값 없음. 호출자를 최대 joinTimeout + ShutdownNoticeTimeout 동안 막는다.
    // Phase 10 D7: 1. stop the ticks, 2. close every connection with ServerShutdown, 3. give those notices up to
    // ShutdownNoticeTimeout to be acknowledged (at once when no peer is left), 4. close the socket. A loop thread that
    // does not stop within joinTimeout is logged and left behind (it is a background thread). Blocks the caller for
    // at most joinTimeout + ShutdownNoticeTimeout; GameServerService runs it off the host's thread.
    internal void Stop(TimeSpan joinTimeout)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        // First, so nobody connects while the loop thread is joined (up to joinTimeout).
        _listener.BeginStopping();
        if (_thread != null)
        {
            _stop.Cancel();
            if (!_thread.Join(joinTimeout))
                _logger.LogCritical("The game loop thread did not stop within {Seconds} s; shutting down without it", joinTimeout.TotalSeconds);
        }
        if (!_net.IsRunning) return;

        _net.DisconnectAll(NetworkListener.DataOf(DisconnectCode.ServerShutdown), 0, 1);
        long deadline = Environment.TickCount64 + (long)ShutdownNoticeTimeout.TotalMilliseconds;
        // A peer stays ShutdownRequested until its client acknowledges the disconnect (or LiteNetLib gives up).
        while (_net.GetPeersCount(ConnectionState.Connected | ConnectionState.ShutdownRequested) > 0 &&
               Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }
        _net.Stop(true);
    }

    // 기능: 멈추고 자원을 해제한다(리뷰 수정 B1: 서버 키 포함).
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _stop.Dispose();
        _identity.Dispose();
    }

    // 기능: Loop 스레드 본체. 취소될 때까지 SimHz 간격으로 보호된 Tick을 돌리고(치명 상태면 Timestamp만 갱신), 주기마다 Stats·Health를 기록하며, 5 Tick 넘게 밀리면 밀린 Tick을 건너뛴다. 반복 전체가 예외에 보호된다.
    // 입력: 없음.
    // 출력: 반환값 없음. Stop이 취소하면 돌아온다.
    private void Run()
    {
        using var timerResolution = WindowsTimerResolution.Begin();
        long tickTicks = Stopwatch.Frequency / _options.SimHz;
        long statsTicks = Stopwatch.Frequency * _options.StatsIntervalSeconds;
        var clock = Stopwatch.StartNew();
        long nextTick = clock.ElapsedTicks;
        long nextStats = nextTick + statsTicks;
        CancellationToken token = _stop.Token;

        while (!token.IsCancellationRequested)
        {
            // Phase 10 D6: the whole iteration is guarded, not only the tick. An exception in the stats line or the
            // wait must not end this thread (it would take the server down with it).
            try
            {
                long tickStart = clock.ElapsedTicks;
                if (!_fatal) RunTickGuarded();
                else Volatile.Write(ref _lastTickTimestamp, _time.GetTimestamp());   // idle on purpose until Stop: no stall
                _lastTickMs = (clock.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency;
                _tickMetrics.Record(_lastTickMs);
                _loopFaultHook?.Invoke();

                if (clock.ElapsedTicks >= nextStats)
                {
                    nextStats += statsTicks;   // first, so a stats line that throws is not retried every tick
                    LogPeriodic();
                }

                nextTick += tickTicks;
                long behind = clock.ElapsedTicks - nextTick;
                if (behind > tickTicks * 5)
                {
                    // Far behind (debugger pause, machine stall): skip the backlog instead of bursting ticks.
                    _lateTicksSkipped += behind / tickTicks;
                    nextTick = clock.ElapsedTicks;
                }
                WaitUntil(clock, nextTick, token);
            }
            catch (Exception ex)
            {
                _health.AddLoopFailure();
                if (++_loopFailuresSinceStats == 1) _logger.LogError(ex, "Unhandled exception in the game loop outside the tick");
                // One tick of rest, so a failure that repeats every iteration cannot spin; then pace from now.
                Thread.Sleep((int)(1000 / _options.SimHz));
                nextTick = clock.ElapsedTicks;
            }
        }
    }

    // 기능: 절대 던지지 않는 Tick 한 번(D6). 실패는 세고 구간당 한 번 로그하며, FailingSecondsBeforeReset초 연속 실패나 플레이어 실패 예약이 있으면 Tick 뒤 경기를 리셋한다. 끝에 LastTickTimestamp를 갱신한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 경기 상태가 한 Tick 진행되거나(실패 시 건너뜀) 리셋된다.
    // One tick that never throws (D6). A failure is counted and logged (the first of each stats interval, so a
    // repeating one cannot flood the log 30 times per second). One bad tick is skipped; FailingSecondsBeforeReset
    // seconds of failures in a row reset the match.
    internal void RunTickGuarded()
    {
        try
        {
            _tickFaultHook?.Invoke();
            RunTick();
            _consecutiveTickFailures = 0;
            // Review round 1: the tick went through, but its players kept failing (see _playerFailureTicks). After the
            // tick, so Match is not replaced while it runs. A reset that throws (only its logger can) fails this tick.
            if (_resetForPlayerFailures) ResetMatch(PlayerFailuresCause);
        }
        catch (Exception ex)
        {
            _exceptionCount++;
            _health.AddTickFailure();
            if (++_exceptionsSinceStats == 1)
            {
                // The log call reads the broken match and runs logger code: neither may break "never throws".
                try
                {
                    _logger.LogError(ex, "Unhandled exception in game tick {Tick} (match {State}, round {Round})",
                        _match.ServerTick, _match.Flow.State, _match.Flow.Round);
                }
                catch
                {
                    // Nothing left to report it with; the failure is still counted above.
                }
            }
            if (++_consecutiveTickFailures >= _failuresBeforeReset)
            {
                try
                {
                    ResetMatch(TickFailuresCause);
                }
                catch
                {
                    // ResetMatch guards its own risky work (closing peers, building the match); only a throwing
                    // logger reaches here. Its counters and the fatal decision come before any log call.
                }
            }
        }
        finally
        {
            Volatile.Write(ref _lastTickTimestamp, _time.GetTimestamp());
        }
    }

    // Phase 10 D6: the match state is most likely broken half-way, so it is thrown away (no record) and every client
    // is closed with ServerError; their clients reconnect into the new match. Three resets within ResetWindow mean the
    // fault is in the code, not in one match: the server stops (onFatal) instead of failing forever.
    private const string TickFailuresCause = "ticks failed in a row";
    private const string PlayerFailuresCause = "players' ticks failed (MaxPlayers failures, or 5 ticks with every player failing, within 10 s)";

    // 기능: 경기 객체를 버리고 새로 만든다(모든 연결은 ServerError로 닫는다). 짧은 시간에 너무 많으면 서버를 멈춘다. 건설·분대·지도(Phase 15)·Loot(Phase 16)·
    //   Seq 창 드롭(리뷰 수정 A4) 합계는 기준값으로 넘겨 줄지 않게 한다.
    // 입력: cause - 리셋 이유(로그).
    // 출력: 반환값 없음. 새 경기가 생기거나 치명 정지 경로로 간다.
    private void ResetMatch(string cause)
    {
        _consecutiveTickFailures = 0;
        _resetForPlayerFailures = false;
        _playerFailureCount = 0;
        _allFailedCount = 0;
        _stalePeers.Clear();
        _timedOut.Clear();

        // The newest MaxResetsInWindow reset times, oldest first. Recorded and judged before any risky work, so a reset
        // that keeps failing still reaches the fatal path.
        if (_resetCount == MaxResetsInWindow)
        {
            Array.Copy(_resetTimestamps, 1, _resetTimestamps, 0, MaxResetsInWindow - 1);
            _resetCount--;
        }
        _resetTimestamps[_resetCount++] = _time.GetTimestamp();
        bool fatal = _resetCount == MaxResetsInWindow && _time.GetElapsedTime(_resetTimestamps[0]) <= ResetWindow;

        if (fatal)
        {
            // Clients are not closed with ServerError here: they would reconnect into a stopping server. Stop tells
            // them ServerShutdown, and from now on no new connection is accepted. Not counted as a reset: none happens.
            _fatal = true;
            _listener.BeginStopping();
            _logger.LogCritical("{Count} match resets within {Minutes} minutes: stopping the server", MaxResetsInWindow, ResetWindow.TotalMinutes);
            try
            {
                _onFatal?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "The fatal-stop callback failed");
            }
            return;
        }

        _health.AddMatchReset();
        // Final review B12: the thrown-away match's building totals stay in the counters (they never go back).
        _health.CarryBuildTotals();
        _health.CarrySquadTotals();   // Phase 14
        _health.CarryMapTotals();     // Phase 15
        _health.CarryLootTotals();    // Phase 16
        _health.CarryInputSeqDrops(); // review fix A4
        // The old match's unlogged sink failure would go with it; LogPeriodic logs it with the next stats line.
        _carriedSinkError ??= _match.TakeSinkError();
        try
        {
            _logger.LogError("{Cause}: resetting the match (round {Round}) and closing {Peers} connections with ServerError",
                cause, _match.Flow.Round, _peers.Count);
            foreach (NetPeer peer in _peers.Values)
            {
                try
                {
                    _health.AddKick(DisconnectCode.ServerError);
                    NetworkListener.Close(peer, DisconnectCode.ServerError);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Closing a connection during a match reset failed");
                }
            }
            _peers.Clear();
            _match = NewMatch();
        }
        catch (Exception ex)
        {
            // The old match stays; the next run of failures resets again, and repeated resets end in the fatal stop.
            _logger.LogError(ex, "Match reset failed");
        }
    }

    // 기능: 한 Tick: 들어온 메시지 처리(Control·Input·Build·Marker 채널, Phase 15 지도 표시 요청 포함), 연결 정리(SweepPeers), 통계 응답 전송,
    //   경기 Tick, 모두 실패한 Tick 기록(리뷰 수정 A6: 출처 둘 이상일 때만 리셋 예약), Health 수치(Phase 14 분대, Phase 15 지도, Phase 16 Loot,
    //   리뷰 수정 A4 Seq 창 드롭 수치 포함) 갱신, QA 작업.
    // 입력: 없음.
    // 출력: 반환값 없음. 경기 상태가 한 Tick 진행되고 Health 수치가 갱신된다.
    internal void RunTick()
    {
        Interlocked.Increment(ref _loopTick);   // read by tests from another thread (LoopTicks)
        DrainControl();
        DrainInput();
        DrainBuild();
        DrainMarkers();
        SweepPeers();
        SendStatsReplies();
        _match.Tick();
        // Review round 2: a tick in which every player failed (see _allFailedTicks). Review fix A6: only when the failures of
        // the window came from two sources or more, so one address alone on the server, joining and failing again and again
        // (it may hold several connections open, which the penalty on new ones does not stop), cannot reset the match.
        if (_match.EveryPlayerFailed && RecordInWindow(_allFailedTicks, ref _allFailedCount, ref _allFailedNext)
            && HasTwoSourcesInWindow()) _resetForPlayerFailures = true;
        _health.SetGauges(_peers.Count, _match.PlayerCount, _match.GracedCount, _match.Flow.State);
        _health.SetBuild(_match.BuildCounts(), _buildRejects);
        _health.SetSquad(_match.SquadCounts());   // Phase 14
        _health.SetMap(_match.MapCounts());       // Phase 15
        _health.SetLoot(_match.LootCounts());     // Phase 16
        _health.SetInputSeqDrops(_match.InputSeqDrops);   // review fix A4
        // QA-1 D5: last, so QA commands act between ticks on a finished tick. OnTick catches everything itself: a QA
        // failure must never count as a tick failure (that path resets the match).
        _qa?.OnTick(this);
    }

    // 기능: Control 채널을 비우며 연결 등록(같은 id의 옛 세션은 떨군다), Join 요청, 연결 종료를 처리한다. 메시지의 Peer 참조가 등록된 것과 다르면 무시한다.
    // 입력: 없음.
    // 출력: 반환값 없음. _peers와 경기 참가 상태가 바뀐다.
    private void DrainControl()
    {
        // Bounded by the channel capacity (ServerOptions.ControlChannelCapacity), so draining fully is safe.
        var reader = _channels.Control.Reader;
        while (reader.TryRead(out ControlMessage message))
        {
            switch (message.Kind)
            {
                case ControlKind.Connected:
                    // LiteNetLib reuses peer ids. If the previous peer's Disconnected was lost or is
                    // still in flight, its session must go now; otherwise the new client's Join gets
                    // AlreadyJoined and it waits forever. Its late Disconnected is then ignored by the
                    // reference check below.
                    // Server review L12: the new connection is registered first, so a throw while the old session is
                    // dropped (it fails this tick) cannot leave the new one outside _peers, where no sweep would find it.
                    _peers.TryGetValue(message.PeerId, out NetPeer? previous);
                    _peers[message.PeerId] = message.Peer;
                    ((PeerState)message.Peer.Tag).ConnectedTick = _loopTick;
                    if (previous != null && !ReferenceEquals(previous, message.Peer)) DropSession(message.PeerId, previous);
                    break;

                case ControlKind.JoinRequested:
                    if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer))
                        Join(message.PeerId, (PeerState)peer.Tag);
                    break;

                case ControlKind.Disconnected:
                    if (_peers.TryGetValue(message.PeerId, out var known) && ReferenceEquals(known, message.Peer))
                        RemovePeer(message.PeerId);
                    break;
            }
        }
    }

    // 기능: Input 채널의 입력 패킷을 경기에 넘긴다(Tick당 MaxInputMessagesPerTick까지, 홍수가 한 Tick을 늘리지 않게).
    //   리뷰 수정 A4(SEC-7): 경기가 입력을 하나라도 받았을 때만 그 연결의 입력 시간(LastInputTick)을 갱신한다. 모두 거절된 패킷은
    //   입력이 아니므로 InputTimeout을 미루지 않는다.
    // 입력: 없음.
    // 출력: 반환값 없음. 플레이어 입력 버퍼와 연결의 LastInputTick이 바뀐다.
    private void DrainInput()
    {
        var reader = _channels.Input.Reader;
        int budget = _options.MaxInputMessagesPerTick;
        while (budget-- > 0 && reader.TryRead(out InputMessage message))
        {
            if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer)
                && _match.EnqueueInput(message.PeerId, message.Packet))
            {
                ((PeerState)peer.Tag).LastInputTick = _loopTick;
            }
        }
    }

    // 기능: Build 채널의 건설 요청을 경기에 넘긴다(Phase 13 D8). Tick당 MaxPlayers × BuildRequestQueue.Capacity까지만 읽고 나머지는 다음 Tick에 넘긴다.
    // 입력: 없음.
    // 출력: 반환값 없음. 플레이어별 건설 요청 큐가 채워진다.
    // Phase 13 D8: build requests, bounded per tick like inputs. Review fix A5: the channel holds more (two one-second windows
    // per player); what is left waits for the next tick (the per-connection rate keeps the steady flow far below this).
    private void DrainBuild()
    {
        var reader = _channels.Build.Reader;
        int budget = _options.MaxPlayers * Game.Build.BuildRequestQueue.Capacity;
        while (budget-- > 0 && reader.TryRead(out BuildMessage message))
        {
            if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer))
                _match.EnqueueBuild(message.PeerId, message.Request);
        }
    }

    // 기능: Marker 채널의 지도 표시 요청을 경기에 넘긴다(Phase 15 D7). 채널 용량만큼만 읽어 한 Tick이 늘어나지 않는다.
    //   요청은 바로 검증·반영되고, 바뀐 팀의 TeamMarkers는 이 Tick 끝에 간다.
    // 입력: 없음.
    // 출력: 반환값 없음. 경기의 Ping·Waypoint 상태가 바뀐다.
    private void DrainMarkers()
    {
        var reader = _channels.Marker.Reader;
        int budget = _options.MaxPlayers * InboundChannels.MarkersPerPlayer;
        while (budget-- > 0 && reader.TryRead(out MarkerMessage message))
        {
            if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer))
                _match.HandleMarker(message.PeerId, message.Marker);
        }
    }

    // 기능: Join 요청을 경기에 넘긴다(리뷰 수정 B4: 이 연결의 Resume 키와 요청의 Resume 증명을 함께. 연결 중인 캐릭터를 넘겨받았으면 옛 연결을
    //   1초 뒤 코드 없이 닫는다). Ok·Resumed면 입력 시간을 시작하고 센다.
    // 입력: peerId - 연결 id, state - 연결 상태(이름, 세션 키, Resume 증명).
    // 출력: 반환값 없음. 연결 상태가 Joined 또는 JoinRefused가 된다.
    // Only Ok and Resumed make a joined peer (input timeout from now). A refused Join (MatchFull; AlreadyJoined cannot
    // happen, the listener forwards one Join per connection) already got its JoinMatchResponse from Match. The
    // connection has nothing left to do, so SweepPeers closes it SimHz ticks later, after the response went out;
    // until then the join timeout does not apply to it.
    private void Join(int peerId, PeerState state)
    {
        // Review fix B4: the connection's resume key (kept by the player for a later resume) and its resume claim.
        JoinResult result = _match.TryJoin(peerId, state.DevPlayerId, state.ResumeKey, state.SessionKey, state.ResumeNonce, state.ResumeProof,
            out int takenOverPeer);
        // Review fix B4: the character was taken over from a connection the server had not yet seen drop (its client crashed
        // and came back). That connection is closed a second later without a code, like a refused join: no grace (Match no
        // longer maps it to a player), and a client that is somehow still there does not retry a close without a code.
        if (takenOverPeer != PlayerEntity.NoPeer && _peers.TryGetValue(takenOverPeer, out NetPeer? oldPeer) && oldPeer.Tag is PeerState oldState)
        {
            oldState.JoinRefused = true;
            oldState.RefusedTick = _loopTick;
            _logger.LogDebug("Peer {PeerId} ({DevPlayerId}) took its character over from peer {OldPeerId}", peerId, state.DevPlayerId, takenOverPeer);
        }
        if (result == JoinResult.Ok || result == JoinResult.Resumed)
        {
            state.Joined = true;
            state.LastInputTick = _loopTick;   // the input timeout starts at the join (D4)
            if (result == JoinResult.Ok) _health.AddJoin();
            else _health.AddResume();
        }
        else
        {
            state.JoinRefused = true;
            state.RefusedTick = _loopTick;
        }
        // Server review M1: Debug (joins= and resumed= count them).
        _logger.LogDebug("Peer {PeerId} ({DevPlayerId}) join: {Result}", peerId, state.DevPlayerId, result);
    }

    // Once per tick over at most MaxPlayers peers, no allocation.
    // 1. Safety net for lost Disconnected messages. ConnectionState is written by LiteNetLib's thread; a stale read
    //    only delays removal by one tick.
    // 2. Phase 10 D3, D4: a connection that has not joined within JoinTimeoutSeconds, and a joined player that sent no
    //    input for InputTimeoutSeconds (dead and spectating players included: the client sends input while joined).
    // 3. A connection whose Join was refused, one second after the refusal (see Join).
    // 4. Review fix C3: copies each live connection's RoundTripTime into PeerState.RttMs (the rewind allowance).
    // 기능: 매 Tick 모든 연결을 훑어 끊긴 연결을 제거하고, Join·Input Timeout, 거절된 Join의 1초 경과, 혼잡(Congested)에 해당하는 연결을 코드와 함께 닫은 뒤 제거하며, 살아 있는 연결의 RTT를 PeerState에 적는다.
    // 입력: 없음.
    // 출력: 반환값 없음. _peers·경기 참가 상태·Kick 합계가 바뀐다.
    private void SweepPeers()
    {
        // Cleared first: a fault in an earlier sweep (RemovePeer threw) must not leave entries behind for every later tick.
        _stalePeers.Clear();
        _timedOut.Clear();
        foreach (var pair in _peers)
        {
            NetPeer peer = pair.Value;
            if (peer.ConnectionState != ConnectionState.Connected)
            {
                _stalePeers.Add(pair.Key);
                continue;
            }
            var state = (PeerState)peer.Tag;
            state.RttMs = peer.RoundTripTime;   // review fix C3: read once per tick for the rewind allowance (an int read)
            if (state.JoinRefused)
            {
                // No code: the JoinMatchResponse said why, and a remote close without a code is never retried (D10).
                if (_loopTick - state.RefusedTick >= _options.SimHz) _timedOut.Add((pair.Key, DisconnectCode.None));
            }
            else if (!state.Joined)
            {
                if (_loopTick - state.ConnectedTick >= _joinTimeoutTicks) _timedOut.Add((pair.Key, DisconnectCode.JoinTimeout));
            }
            else if (_inputTimeoutTicks > 0 && _loopTick - state.LastInputTick >= _inputTimeoutTicks)
            {
                _timedOut.Add((pair.Key, DisconnectCode.InputTimeout));
            }
            else if (Congested(peer, state))
            {
                _timedOut.Add((pair.Key, DisconnectCode.Congested));
            }
        }
        foreach (int peerId in _stalePeers) RemovePeer(peerId);
        _stalePeers.Clear();

        foreach ((int peerId, DisconnectCode code) in _timedOut)
        {
            if (!_peers.TryGetValue(peerId, out NetPeer? peer)) continue;
            if (code == DisconnectCode.None)
            {
                // Server review M1: Debug, here and below (kicks= counts the timeouts).
                _logger.LogDebug("Closing peer {PeerId} ({DevPlayerId}): its join was refused", peerId, ((PeerState)peer.Tag).DevPlayerId);
            }
            else
            {
                _health.AddKick(code);
                _logger.LogDebug("Disconnecting peer {PeerId} ({DevPlayerId}): {Code}", peerId, ((PeerState)peer.Tag).DevPlayerId, code);
            }
            NetworkListener.Close(peer, code);
            RemovePeer(peerId);
        }
        _timedOut.Clear();
    }

    // 기능: 연결의 Reliable 큐(채널 0·1 합)가 MaxReliableBacklog를 넘긴 채 CongestedSeconds 이상 이어졌는지 판정하고, 넘긴 시작 Tick을 PeerState에 기록·해제한다.
    // 입력: peer - 검사할 연결, state - 그 연결의 상태(CongestedSinceTick 갱신).
    // 출력: 혼잡이 CongestedSeconds 이상 이어졌으면 true, 아니면 false.
    // Final review A4: the peer's reliable queues (both channels) have held more than MaxReliableBacklog packets for
    // CongestedSeconds in a row. Two queue reads per joined peer per tick; no allocation.
    private bool Congested(NetPeer peer, PeerState state)
    {
        int queued = Queued(peer, ProtocolConstants.ReliableChannel) + Queued(peer, ProtocolConstants.BuildChannel);
        if (queued <= MaxReliableBacklog)
        {
            state.CongestedSinceTick = -1;
            return false;
        }
        if (state.CongestedSinceTick < 0) state.CongestedSinceTick = _loopTick;
        return _loopTick - state.CongestedSinceTick >= _congestedTicks;
    }

    // 기능: 연결의 한 채널에 쌓인 ReliableOrdered 패킷 수를 읽는다(테스트 Probe가 있으면 그것을 쓴다).
    // 입력: peer - 연결, channel - 채널 번호.
    // 출력: 그 채널의 전송 대기 Reliable 패킷 수.
    private int Queued(NetPeer peer, byte channel) =>
        _queueProbe is { } probe ? probe(peer, channel) : peer.GetPacketsCountInReliableQueue(channel, ordered: true);

    // 기능: Match가 묻는 연결의 Build 채널 적체량을 돌려준다(최종 리뷰 A4).
    // 입력: peerId - 연결 id.
    // 출력: Build 채널에 쌓인 패킷 수. 없는 연결이면 0.
    // Final review A4: Match's question, a peer's building-channel backlog (0 for a peer not here).
    private int BuildBacklog(int peerId) => _peers.TryGetValue(peerId, out NetPeer? peer) ? Queued(peer, ProtocolConstants.BuildChannel) : 0;

    // 기능: 연결을 _peers에서 빼고 그 세션을 경기에서 떨군다. 없는 id면 아무것도 하지 않는다.
    // 입력: peerId - 제거할 연결 id.
    // 출력: 반환값 없음. 연결과 경기 참가 상태가 정리된다(유예 자격은 Match가 정한다).
    // Phase 10 D2: a connection the server did not close itself (a client quit, crash or network loss) may keep its
    // character for the reconnect grace; Match decides whether the player qualifies.
    private void RemovePeer(int peerId)
    {
        if (_peers.Remove(peerId, out NetPeer? peer)) DropSession(peerId, peer);
    }

    // 기능: 제거된 연결의 경기 쪽 정리: 서버가 코드 없이 잃은 연결이면 유예를 허용해 Match.Disconnect를 부르고, 유예가 시작됐으면 세고 Debug로 남긴다.
    // 입력: peerId - 연결 id, peer - 제거된 연결(Tag의 CloseCode로 유예 허용 여부를 정한다).
    // 출력: 반환값 없음. 플레이어가 유예 상태가 되거나 경기에서 빠진다.
    // The match side of a removed connection (server review L12: apart from _peers, so a replaced peer's session can be
    // dropped after the new connection took its id).
    private void DropSession(int peerId, NetPeer peer)
    {
        _removeFaultHook?.Invoke();
        var state = (PeerState)peer.Tag;
        if (_match.Disconnect(peerId, allowGrace: state.CloseCode == DisconnectCode.None))
        {
            _health.AddGraceStart();
            // Server review M1: Debug (graceStarts= counts it).
            _logger.LogDebug("Peer {PeerId} ({DevPlayerId}) dropped mid-match; character kept for {Seconds} s",
                peerId, state.DevPlayerId, _options.ReconnectGraceSeconds);
        }
    }

    // 기능: 응답 큐에 쌓인 StatsResponse를 큐 용량만큼 꺼내 해당 연결에 ReliableOrdered로 보낸다(Phase 11 D8). 연결이 사라졌거나 다른 연결이 된 응답은 버리고 Undelivered로 센다.
    // 입력: 없음.
    // 출력: 반환값 없음. 대상 Client에 StatsResponse 패킷이 전송된다.
    // Phase 11 D8: the answers StatsQueryService (or a network thread, for Busy) left in the reply queue, at most the
    // queue's capacity per tick, so this never waits. Before Match.Tick, so a failing match does not hold them back. An
    // answer whose connection is gone, or whose peer id now belongs to another connection, is dropped and counted.
    private void SendStatsReplies()
    {
        for (int budget = _statsQueries.Capacity; budget > 0 && _statsQueries.TryTakeReply(out StatsReply reply); budget--)
        {
            if (!_peers.TryGetValue(reply.PeerId, out NetPeer? peer) || !ReferenceEquals(peer, reply.Peer))
            {
                _statsQueries.AddUndelivered();
                continue;
            }
            var writer = new PacketWriter(_statsReplyBuffer);
            StatsResponse.Write(ref writer, reply.Response);
            peer.Send(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            _stats.AddOut(writer.Length);
        }
    }

    // 기능: 패킷 하나를 연결의 기본 채널(0)로 보내고 송신 통계에 더한다(Match의 송신 위임). 없는 연결이면 버린다.
    // 입력: peerId - 받을 연결 id, data - 보낼 패킷 바이트, method - LiteNetLib 전달 방식.
    // 출력: 반환값 없음. 대상 Client에 패킷이 전송된다.
    private void SendToPeer(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        if (!_peers.TryGetValue(peerId, out var peer)) return;
        // NetPeer.Send is thread-safe and copies the data into LiteNetLib's own packet.
        peer.Send(data, method);
        _stats.AddOut(data.Length);
    }

    // 기능: 건설 패킷 하나를 연결의 Build 채널(1)로 보내고 송신 통계에 더한다(Phase 13 D13). 없는 연결이면 버린다.
    // 입력: peerId - 받을 연결 id, data - 보낼 패킷 바이트, method - LiteNetLib 전달 방식.
    // 출력: 반환값 없음. 대상 Client에 건설 패킷이 전송된다.
    // Phase 13 D13: the building stream on its own channel.
    private void SendToPeerBuild(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        if (!_peers.TryGetValue(peerId, out var peer)) return;
        peer.Send(data, ProtocolConstants.BuildChannel, method);
        _stats.AddOut(data.Length);
    }

    // 기능: StatsIntervalSeconds마다 Stats 줄과 Health 줄을 기록하고, 경기 기록 실패(리셋이 넘긴 것 우선)와 PlayerSpawned 인코딩 실패(프로세스당 한 번)를 Error로 남긴다. 테스트가 부를 수 있게 internal이다.
    // 입력: 없음.
    // 출력: 반환값 없음. 구간 수치가 0으로 돌아가고 로그가 남는다.
    // Every StatsIntervalSeconds: the Stats line (performance of the interval), then the Health line (state and totals
    // since the start, D9). Internal so tests can read both lines.
    internal void LogPeriodic()
    {
        LogStats();
        LogHealth();
        // Both are taken every interval, so neither leaks into the next one: the carried error (from a match a reset
        // threw away) comes first, being the older.
        Exception? current = _match.TakeSinkError();
        Exception? sinkError = _carriedSinkError ?? current;
        _carriedSinkError = null;
        if (sinkError != null) _logger.LogError(sinkError, "Recording a finished match failed (first failure of this interval)");
        // Phase 11: should never happen (the connect request checks the name), so once per process is enough.
        if (!_spawnEncodeFailureLogged && _match.SpawnEncodeFailures > 0)
        {
            _spawnEncodeFailureLogged = true;
            _logger.LogError("{Count} PlayerSpawned packets could not be encoded and were not sent (a player name over the limit)",
                _match.SpawnEncodeFailures);
        }
    }

    // 기능: 지난 구간의 패킷·바이트 속도, Tick 시간 백분위, 드롭·예외·GC·메모리·CPU%를 Stats 줄 한 줄로 기록하고 구간별 로그 제한(첫 예외만 로그)을 되돌린다.
    // 입력: 없음.
    // 출력: 반환값 없음. Tick 표본과 구간 수치가 비워진다.
    private void LogStats()
    {
        StatsCounters c = _stats.TakeDelta();
        TickStats t = _tickMetrics.Compute();
        _tickMetrics.Reset();
        double seconds = _options.StatsIntervalSeconds;
        TimeSpan cpu = CurrentCpuTime();
        double cpuPercent = (cpu - _cpuAtLastStats).TotalSeconds / (seconds * Environment.ProcessorCount) * 100.0;
        _cpuAtLastStats = cpu;

        _logger.LogInformation(
            "Stats players={Players} pktIn/s={PktIn:F0} bytesIn/s={BytesIn:F0} pktOut/s={PktOut:F0} bytesOut/s={BytesOut:F0} " +
            "tickMs p50={P50:F2} p95={P95:F2} p99={P99:F2} max={Max:F2} inputDrops={Drops} bufferDrops={BufferDrops} " +
            "badPackets={Bad} lateTicksSkipped={Late} exceptions={Exceptions} gc={Gc0}/{Gc1}/{Gc2} workingSetMB={WorkingSet:F0} cpu%={Cpu:F1} " +
            "matchSinkFailures={SinkFailures}",
            _match.PlayerCount, c.PacketsIn / seconds, c.BytesIn / seconds, c.PacketsOut / seconds, c.BytesOut / seconds,
            t.P50, t.P95, t.P99, t.Max, c.InputDrops, _match.TotalBufferDrops,
            c.BadPackets, _lateTicksSkipped, _exceptionCount,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Environment.WorkingSet / 1048576.0, cpuPercent,
            _match.MatchSinkFailures);
        _exceptionsSinceStats = 0;
        _loopFailuresSinceStats = 0;
        _playerFailuresSinceStats = 0;
        _listener.ResetLogLimits();
    }

    // 기능: 연결·보호(리뷰 수정 A2–A4·A6: IP별 동시 연결·벌점·전역 수락·쿠키 거절, 쿠키 응답, Seq 창 드롭, 벌점 수)·건설·분대(Phase 14)·
    //   지도 표시(Phase 15)·Loot(Phase 16)·DB 수치를 한 줄로 기록한다(시작부터의 합계).
    // 입력: 없음.
    // 출력: 반환값 없음. Health 로그 한 줄.
    // Phase 10 D9: connections, protection and database in one line, as totals since the start.
    private void LogHealth()
    {
        HealthCounters h = _health;
        BuildCounts b = h.Build;
        SquadCounts sc = h.Squad;
        MapCounts mc = h.Map;
        LootCounts lc = h.Loot;
        PersistenceCounts db = h.Persistence?.Invoke() ?? default;
        StatsQueryCounts sq = h.StatsQueries?.Invoke() ?? default;
        _logger.LogInformation(
            "Health peers={Peers} players={Players} graced={Graced} match={State}#{Round} " +
            "connections={Connections} joins={Joins} resumed={Resumed} graceStarts={GraceStarts} graceExpiries={GraceExpiries} " +
            "disconnects timeout={DisconnectTimeouts} other={DisconnectOthers} " +
            "rejects full={RejectFull} badRequest={RejectBad} version={RejectVersion} connectRate={RejectConnectRate} " +
            "perIp={RejectPerIp} penalized={RejectPenalized} accept={RejectAccept} cookie={RejectCookie} cookieChallenges={CookieChallenges} " +
            "kicks kicked={KickBad} joinTimeout={KickJoin} inputTimeout={KickInput} serverError={KickError} congested={KickCongested} " +
            "badPackets unknownId={BadUnknown} malformed={BadMalformed} beforeJoin={BadBeforeJoin} duplicateJoin={BadDuplicate} " +
            "inputRate={BadRate} wrongDirection={BadDirection} handlerException={BadHandler} buildRate={BadBuildRate} markerRate={BadMarkerRate} " +
            "inputSeqDrops={InputSeqDrops} authDrops={AuthDrops} authDropsRetired={AuthDropsRetired} " +
            "tickFailures={TickFailures} loopFailures={LoopFailures} matchResets={Resets} stalls={Stalls} movementAnomalies={MovementAnomalies} " +
            "networkErrors={NetworkErrors} playerFailures={PlayerFailures} penalties={Penalties} stallExits={StallExits} callbackErrors={CallbackErrors} " +
            "build pieces={BuildPieces} cells={BuildCells} requests={BuildRequests} accepted={BuildAccepted} destroyed={BuildDestroyed} " +
            "collapsed={BuildCollapsed} duplicates={BuildDuplicates} edits={BuildEdits} eventPackets={BuildEventPackets} syncPackets={BuildSyncPackets} " +
            "buildRejects noResource={RejectNoResource} outOfRange={RejectRange} blocked={RejectBlocked} unsupported={RejectUnsupported} " +
            "occupied={RejectOccupied} rateLimited={RejectRate} invalidState={RejectState} invalidRequest={RejectRequest} budgetFull={RejectBudget} " +
            "notOwner={RejectNotOwner} notFound={RejectNotFound} " +
            "harvest hits={HarvestHits} envDestroyed={HarvestDestroyed} syncDeferred={BuildSyncDeferred} " +
            "buildInboxDrops={BuildInboxDrops} " +
            "squad downs={SquadDowns} revives={SquadRevives} reboots={SquadReboots} bleedOuts={SquadBleedOuts} cardsDropped={SquadCardsDropped} " +
            "cardsExpired={SquadCardsExpired} wipes={SquadWipes} channelsCancelled={SquadChannelsCancelled} " +
            "map pings={MapPings} enemyConfirmed={MapEnemyConfirmed} enemyDemoted={MapEnemyDemoted} refused={MapRefused} replaced={MapReplaced} " +
            "expired={MapExpired} waypoints={MapWaypoints} packets={MapPackets} markerDrops={MapMarkerDrops} markerInboxDrops={MapMarkerInboxDrops} " +
            "loot containersOpened={LootContainersOpened} dropsSpawned={LootDropsSpawned} dropsLanded={LootDropsLanded} dropsOpened={LootDropsOpened} " +
            "items={LootItems} blocked={LootBlocked} packets={LootPackets} " +
            "db saved={DbSaved} failed={DbFailed} discarded={DbDiscarded} dropped={DbDropped} " +
            "stats requests={StatsRequests} limited={StatsLimited} busy={StatsBusy} unavailable={StatsUnavailable} undelivered={StatsUndelivered}",
            _peers.Count, _match.PlayerCount, _match.GracedCount, _match.Flow.State, _match.Flow.Round,
            h.Connections, h.Joins, h.Resumes, h.GraceStarts, h.GraceExpiries,
            h.DisconnectTimeouts, h.DisconnectOthers,
            h.Rejects(RejectReason.ServerFull), h.Rejects(RejectReason.BadRequest), h.Rejects(RejectReason.VersionMismatch), h.ConnectRateRejects,
            h.PerIpRejects, h.PenaltyRejects, h.AcceptRateRejects, h.CookieRejects, h.CookieChallenges,
            h.Kicks(DisconnectCode.Kicked), h.Kicks(DisconnectCode.JoinTimeout), h.Kicks(DisconnectCode.InputTimeout), h.Kicks(DisconnectCode.ServerError),
            h.Kicks(DisconnectCode.Congested),
            h.BadPackets(BadPacketReason.UnknownId), h.BadPackets(BadPacketReason.Malformed), h.BadPackets(BadPacketReason.InputBeforeJoin),
            h.BadPackets(BadPacketReason.DuplicateJoin), h.BadPackets(BadPacketReason.InputRate), h.BadPackets(BadPacketReason.WrongDirection),
            h.BadPackets(BadPacketReason.HandlerException), h.BadPackets(BadPacketReason.BuildRate), h.BadPackets(BadPacketReason.MarkerRate),
            h.InputSeqDrops, h.AuthDrops, h.AuthDropsRetired,
            h.TickFailures, h.LoopFailures, h.MatchResets, h.Stalls, h.MovementAnomalies,
            h.NetworkErrors, h.PlayerFailures, h.Penalties, h.StallExits, h.CallbackErrors,
            b.Pieces, b.Cells, b.Requests, b.Accepted, b.Destroyed, b.Collapsed, b.Duplicates, b.Edits, b.EventPackets, b.SyncPackets,
            h.BuildRejects(BuildResultCode.NoResource), h.BuildRejects(BuildResultCode.OutOfRange), h.BuildRejects(BuildResultCode.Blocked),
            h.BuildRejects(BuildResultCode.Unsupported), h.BuildRejects(BuildResultCode.Occupied), h.BuildRejects(BuildResultCode.RateLimited),
            h.BuildRejects(BuildResultCode.InvalidState), h.BuildRejects(BuildResultCode.InvalidRequest), h.BuildRejects(BuildResultCode.BudgetFull),
            h.BuildRejects(BuildResultCode.NotOwner), h.BuildRejects(BuildResultCode.NotFound),
            b.HarvestHits, b.EnvironmentDestroyed, b.SyncDeferred,
            h.BuildInboxDrops,
            sc.Downs, sc.Revives, sc.Reboots, sc.BleedOuts, sc.CardsDropped, sc.CardsExpired, sc.Wipes, sc.ChannelsCancelled,
            mc.Pings, mc.EnemyConfirmed, mc.EnemyDemoted, mc.Refused, mc.Replaced, mc.Expired, mc.Waypoints, mc.Packets, h.MarkerDrops, h.MarkerInboxDrops,
            lc.ContainersOpened, lc.DropsSpawned, lc.DropsLanded, lc.DropsOpened, lc.LootItems, lc.OpensBlocked, lc.Packets,
            db.Saved, db.Failed, db.Discarded, db.Dropped,
            sq.Requests, sq.Limited, sq.Busy, sq.Unavailable, sq.Undelivered);
    }

    // 기능: 이 프로세스의 모든 스레드가 쓴 누적 CPU 시간을 읽는다(Stats 줄마다 한 번, Tick 경로 아님).
    // 입력: 없음.
    // 출력: 프로세스 누적 CPU 시간.
    // Total processor time of this process (all threads), every StatsIntervalSeconds: not on the tick path.
    private static TimeSpan CurrentCpuTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    // 기능: Stopwatch가 목표 Tick에 이를 때까지 기다린다(2ms 넘게 남으면 1ms 모자라게 Sleep, 그 아래는 Yield). 취소되면 바로 돌아온다.
    // 입력: clock - Loop의 Stopwatch, targetTicks - 기다릴 목표 ElapsedTicks, token - 중단 토큰.
    // 출력: 반환값 없음.
    // Dedicated thread (not the ThreadPool), so sleeping here cannot starve other work.
    private static void WaitUntil(Stopwatch clock, long targetTicks, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            long remaining = targetTicks - clock.ElapsedTicks;
            if (remaining <= 0) return;
            double remainingMs = remaining * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0) Thread.Sleep((int)(remainingMs - 1.0));
            else Thread.Yield();
        }
    }
}
