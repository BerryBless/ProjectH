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
    private static readonly TimeSpan ThreadJoinTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownNoticeTimeout = TimeSpan.FromSeconds(1);
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
    private bool _spawnEncodeFailureLogged;   // Phase 11: the first PlayerSpawned encode failure was logged
    private readonly Action? _onFatal;
    private readonly TimeProvider _time;
    // Phase 10 D9: when the last tick attempt ended (TimeProvider timestamp), read by StallWatchdog on a timer thread.
    private long _lastTickTimestamp;
    // Test seams (D6): run at the start of every tick / after every tick, outside it. Set by tests only.
    private volatile Action? _tickFaultHook;
    private volatile Action? _loopFaultHook;
    private volatile Action? _removeFaultHook;   // runs in RemovePeer after the peer left _peers
    // Phase 7 D10: process CPU time at the previous stats line, for cpu% (game loop thread only).
    private TimeSpan _cpuAtLastStats = CurrentCpuTime();
    private long _lateTicksSkipped;
    private bool _disposed;

    // loadout: test seam (D1); null = StartingLoadout.Empty, the production start. dropPoints: test seam (Phase 6 D9);
    // null = the map's DropPoints.All.
    // matchSink: Phase 9, where finished matches are recorded (MatchHistoryQueue.TryEnqueue; null = not recorded).
    // onFatal: Phase 10 D6, called once (on the loop thread, must not block) when resets keep failing; production stops
    // the host with exit code 1. time: the clock of the reset window (tests pass a manual one).
    // statsQueries: Phase 11 D8, the statistics path shared with StatsQueryService; null = a queue nobody answers (tests
    // that do not need answers), so requests wait there and, once it is full, are answered Busy.
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null,
        Action? onFatal = null, TimeProvider? time = null, StatsQueryQueue? statsQueries = null)
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
        _channels = new InboundChannels(options, _stats, _health.AddBuildInboxDrop);
        _joinTimeoutTicks = (long)options.JoinTimeoutSeconds * options.SimHz;
        _inputTimeoutTicks = (long)options.InputTimeoutSeconds * options.SimHz;
        _congestedTicks = (long)CongestedSeconds * options.SimHz;
        _buildBacklog = BuildBacklog;
        _statsQueries = statsQueries ?? new StatsQueryQueue();
        _health.StatsQueries = () => _statsQueries.Counts;
        _listener = new NetworkListener(options, _channels, _stats, _health, _statsQueries, logger, data.Building.MaxRequestsPerSecond);
        _net = new NetManager(_listener, null)
        {
            UnsyncedEvents = true,
            AutoRecycle = true,
            DisconnectTimeout = options.DisconnectTimeoutMs,
            // The timeout is reset only by packets from the client. An idle client (e.g. waiting in
            // a menu) sends little besides pongs to our pings, so ping at least 4 times per timeout;
            // with LiteNetLib's default 1000 ms and a short timeout, live clients were timed out.
            PingInterval = Math.Min(1000, options.DisconnectTimeoutMs / 4),
            // Snapshots are sent Sequenced (never fragmented) and can be MaxPacketSize bytes.
            MtuOverride = ProtocolConstants.Mtu,
            // Phase 13 D13: channel 0 as before, channel 1 the building stream.
            ChannelsCount = ProtocolConstants.ChannelCount,
            UnconnectedMessagesEnabled = false,
            IPv6Enabled = false,
        };
        _listener.Manager = _net;
        _match = NewMatch();
        _buildRejects = code => _match.BuildResults(code);
    }

    private Match NewMatch() => new(_options, _data, SendToPeer, _loadout, dropPoints: _dropPoints, matchSink: _matchSink,
        graceExpired: OnGraceExpired, movementAnomaly: _health.AddMovementAnomaly, sendBuild: SendToPeerBuild, buildBacklog: _buildBacklog);

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

    public void Stop() => Stop(ThreadJoinTimeout);

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _stop.Dispose();
    }

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
                _tickMetrics.Record((clock.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency);
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
                    ResetMatch();
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
    private void ResetMatch()
    {
        _consecutiveTickFailures = 0;
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
        // The old match's unlogged sink failure would go with it; LogPeriodic logs it with the next stats line.
        _carriedSinkError ??= _match.TakeSinkError();
        try
        {
            _logger.LogError("{Count} ticks failed in a row: resetting the match (round {Round}) and closing {Peers} connections with ServerError",
                _failuresBeforeReset, _match.Flow.Round, _peers.Count);
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

    internal void RunTick()
    {
        Interlocked.Increment(ref _loopTick);   // read by tests from another thread (LoopTicks)
        DrainControl();
        DrainInput();
        DrainBuild();
        SweepPeers();
        SendStatsReplies();
        _match.Tick();
        _health.SetGauges(_peers.Count, _match.PlayerCount, _match.GracedCount, _match.Flow.State);
        _health.SetBuild(_match.BuildCounts(), _buildRejects);
    }

    private void DrainControl()
    {
        // Bounded by the channel capacity (MaxPlayers * 3), so draining fully is safe.
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
                    if (_peers.TryGetValue(message.PeerId, out var previous) && !ReferenceEquals(previous, message.Peer))
                        RemovePeer(message.PeerId);
                    _peers[message.PeerId] = message.Peer;
                    ((PeerState)message.Peer.Tag).ConnectedTick = _loopTick;
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

    private void DrainInput()
    {
        // Budgeted so an input flood cannot stretch one tick; the rest waits for the next tick.
        var reader = _channels.Input.Reader;
        int budget = _options.MaxInputMessagesPerTick;
        while (budget-- > 0 && reader.TryRead(out InputMessage message))
        {
            if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer))
            {
                ((PeerState)peer.Tag).LastInputTick = _loopTick;
                _match.EnqueueInput(message.PeerId, message.Packet);
            }
        }
    }

    // Phase 13 D8: build requests, bounded per tick like inputs (the channel holds at most this many).
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

    // Only Ok and Resumed make a joined peer (input timeout from now). A refused Join (MatchFull; AlreadyJoined cannot
    // happen, the listener forwards one Join per connection) already got its JoinMatchResponse from Match. The
    // connection has nothing left to do, so SweepPeers closes it SimHz ticks later, after the response went out;
    // until then the join timeout does not apply to it.
    private void Join(int peerId, PeerState state)
    {
        JoinResult result = _match.TryJoin(peerId, state.DevPlayerId);
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

    private int Queued(NetPeer peer, byte channel) =>
        _queueProbe is { } probe ? probe(peer, channel) : peer.GetPacketsCountInReliableQueue(channel, ordered: true);

    // Final review A4: Match's question, a peer's building-channel backlog (0 for a peer not here).
    private int BuildBacklog(int peerId) => _peers.TryGetValue(peerId, out NetPeer? peer) ? Queued(peer, ProtocolConstants.BuildChannel) : 0;

    // Phase 10 D2: a connection the server did not close itself (a client quit, crash or network loss) may keep its
    // character for the reconnect grace; Match decides whether the player qualifies.
    private void RemovePeer(int peerId)
    {
        if (!_peers.Remove(peerId, out NetPeer? peer)) return;
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

    private void SendToPeer(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        if (!_peers.TryGetValue(peerId, out var peer)) return;
        // NetPeer.Send is thread-safe and copies the data into LiteNetLib's own packet.
        peer.Send(data, method);
        _stats.AddOut(data.Length);
    }

    // Phase 13 D13: the building stream on its own channel.
    private void SendToPeerBuild(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        if (!_peers.TryGetValue(peerId, out var peer)) return;
        peer.Send(data, ProtocolConstants.BuildChannel, method);
        _stats.AddOut(data.Length);
    }

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
        _listener.ResetLogLimits();
    }

    // Phase 10 D9: connections, protection and database in one line, as totals since the start.
    private void LogHealth()
    {
        HealthCounters h = _health;
        BuildCounts b = h.Build;
        PersistenceCounts db = h.Persistence?.Invoke() ?? default;
        StatsQueryCounts sq = h.StatsQueries?.Invoke() ?? default;
        _logger.LogInformation(
            "Health peers={Peers} players={Players} graced={Graced} match={State}#{Round} " +
            "connections={Connections} joins={Joins} resumed={Resumed} graceStarts={GraceStarts} graceExpiries={GraceExpiries} " +
            "disconnects timeout={DisconnectTimeouts} other={DisconnectOthers} " +
            "rejects full={RejectFull} badRequest={RejectBad} version={RejectVersion} " +
            "kicks kicked={KickBad} joinTimeout={KickJoin} inputTimeout={KickInput} serverError={KickError} congested={KickCongested} " +
            "badPackets unknownId={BadUnknown} malformed={BadMalformed} beforeJoin={BadBeforeJoin} duplicateJoin={BadDuplicate} " +
            "inputRate={BadRate} wrongDirection={BadDirection} handlerException={BadHandler} buildRate={BadBuildRate} " +
            "tickFailures={TickFailures} loopFailures={LoopFailures} matchResets={Resets} stalls={Stalls} movementAnomalies={MovementAnomalies} " +
            "networkErrors={NetworkErrors} " +
            "build pieces={BuildPieces} cells={BuildCells} requests={BuildRequests} accepted={BuildAccepted} destroyed={BuildDestroyed} " +
            "collapsed={BuildCollapsed} duplicates={BuildDuplicates} eventPackets={BuildEventPackets} syncPackets={BuildSyncPackets} " +
            "buildRejects noResource={RejectNoResource} outOfRange={RejectRange} blocked={RejectBlocked} unsupported={RejectUnsupported} " +
            "occupied={RejectOccupied} rateLimited={RejectRate} invalidState={RejectState} invalidRequest={RejectRequest} budgetFull={RejectBudget} " +
            "harvest hits={HarvestHits} envDestroyed={HarvestDestroyed} syncDeferred={BuildSyncDeferred} " +
            "buildInboxDrops={BuildInboxDrops} " +
            "db saved={DbSaved} failed={DbFailed} discarded={DbDiscarded} dropped={DbDropped} " +
            "stats requests={StatsRequests} limited={StatsLimited} busy={StatsBusy} unavailable={StatsUnavailable} undelivered={StatsUndelivered}",
            _peers.Count, _match.PlayerCount, _match.GracedCount, _match.Flow.State, _match.Flow.Round,
            h.Connections, h.Joins, h.Resumes, h.GraceStarts, h.GraceExpiries,
            h.DisconnectTimeouts, h.DisconnectOthers,
            h.Rejects(RejectReason.ServerFull), h.Rejects(RejectReason.BadRequest), h.Rejects(RejectReason.VersionMismatch),
            h.Kicks(DisconnectCode.Kicked), h.Kicks(DisconnectCode.JoinTimeout), h.Kicks(DisconnectCode.InputTimeout), h.Kicks(DisconnectCode.ServerError),
            h.Kicks(DisconnectCode.Congested),
            h.BadPackets(BadPacketReason.UnknownId), h.BadPackets(BadPacketReason.Malformed), h.BadPackets(BadPacketReason.InputBeforeJoin),
            h.BadPackets(BadPacketReason.DuplicateJoin), h.BadPackets(BadPacketReason.InputRate), h.BadPackets(BadPacketReason.WrongDirection),
            h.BadPackets(BadPacketReason.HandlerException), h.BadPackets(BadPacketReason.BuildRate),
            h.TickFailures, h.LoopFailures, h.MatchResets, h.Stalls, h.MovementAnomalies,
            h.NetworkErrors,
            b.Pieces, b.Cells, b.Requests, b.Accepted, b.Destroyed, b.Collapsed, b.Duplicates, b.EventPackets, b.SyncPackets,
            h.BuildRejects(BuildResultCode.NoResource), h.BuildRejects(BuildResultCode.OutOfRange), h.BuildRejects(BuildResultCode.Blocked),
            h.BuildRejects(BuildResultCode.Unsupported), h.BuildRejects(BuildResultCode.Occupied), h.BuildRejects(BuildResultCode.RateLimited),
            h.BuildRejects(BuildResultCode.InvalidState), h.BuildRejects(BuildResultCode.InvalidRequest), h.BuildRejects(BuildResultCode.BudgetFull),
            b.HarvestHits, b.EnvironmentDestroyed, b.SyncDeferred,
            h.BuildInboxDrops,
            db.Saved, db.Failed, db.Discarded, db.Dropped,
            sq.Requests, sq.Limited, sq.Busy, sq.Unavailable, sq.Undelivered);
    }

    // Total processor time of this process (all threads), every StatsIntervalSeconds: not on the tick path.
    private static TimeSpan CurrentCpuTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

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
