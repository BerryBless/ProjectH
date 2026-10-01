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
using ProjectH.Shared.Protocol;

namespace ProjectH.Server;

// Owns the NetManager, the Match and the dedicated simulation thread.
//
// Threads:
//   - LiteNetLib threads run NetworkListener and write to InboundChannels.
//   - The "GameLoop" thread is the only reader of the channels and the only thread that touches
//     Match, _peers and _stalePeers.
// This class takes no locks, so there is no lock ordering to keep.
public sealed class GameLoop : IDisposable
{
    private readonly ServerOptions _options;
    private readonly ILogger _logger;
    private readonly ServerStats _stats = new();
    private readonly TickMetrics _tickMetrics = new();
    private readonly InboundChannels _channels;
    private readonly NetManager _net;
    private readonly Match _match;
    // Removed on Disconnected messages and by the stale-peer sweep; never outlives the connection.
    private readonly Dictionary<int, NetPeer> _peers = new();
    private readonly List<int> _stalePeers = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private long _exceptionCount;
    private long _exceptionsSinceStats;
    // Phase 7 D10: process CPU time at the previous stats line, for cpu% (game loop thread only).
    private TimeSpan _cpuAtLastStats = CurrentCpuTime();
    private long _lateTicksSkipped;
    private bool _disposed;

    // loadout: test seam (D1); null = StartingLoadout.Empty, the production start. dropPoints: test seam (Phase 6 D9);
    // null = the map's DropPoints.All.
    // matchSink: Phase 9, where finished matches are recorded (MatchHistoryQueue.TryEnqueue; null = not recorded).
    public GameLoop(ServerOptions options, GameData data, ILogger logger, StartingLoadout? loadout = null,
        System.Numerics.Vector3[]? dropPoints = null, Action<Persistence.MatchRecord>? matchSink = null)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));

        _options = options;
        _logger = logger;
        _channels = new InboundChannels(options, _stats);
        var listener = new NetworkListener(options, _channels, _stats, logger);
        _net = new NetManager(listener, null)
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
            UnconnectedMessagesEnabled = false,
            IPv6Enabled = false,
        };
        listener.Manager = _net;
        _match = new Match(options, data, SendToPeer, loadout, dropPoints: dropPoints, matchSink: matchSink);
    }

    public int LocalPort => _net.LocalPort;
    // Test seams (InternalsVisibleTo ProjectH.Server.Tests): tests that call RunTick() directly
    // must not Start() the loop, so the calling test thread is the only one touching Match.
    internal InboundChannels Channels => _channels;
    internal Match Match => _match;
    public bool IsRunning => _thread != null && _thread.IsAlive;

    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("GameLoop already started.");
        if (!_net.Start(_options.Port)) throw new InvalidOperationException($"Failed to bind UDP port {_options.Port}.");

        _thread = new Thread(Run) { Name = "GameLoop", IsBackground = false };
        _thread.Start();
        _logger.LogInformation("Server listening on UDP {Port} (SimHz {SimHz}, SnapshotHz {SnapshotHz}, MaxPlayers {MaxPlayers})",
            _net.LocalPort, _options.SimHz, _options.SnapshotHz, _options.MaxPlayers);
    }

    public void Stop()
    {
        if (_thread != null)
        {
            _stop.Cancel();
            _thread.Join();
        }
        // The loop has exited, so nothing sends anymore: tell clients and release the socket.
        if (_net.IsRunning) _net.Stop(true);
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
            long tickStart = clock.ElapsedTicks;
            try
            {
                RunTick();
            }
            catch (Exception ex)
            {
                // One bad tick must not stop the server. Log the first of each stats interval only,
                // so a repeating failure cannot flood the log 30 times per second.
                _exceptionCount++;
                if (++_exceptionsSinceStats == 1)
                    _logger.LogError(ex, "Unhandled exception in game tick {Tick}", _match.ServerTick);
            }
            _tickMetrics.Record((clock.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency);

            if (clock.ElapsedTicks >= nextStats)
            {
                LogStats();
                nextStats += statsTicks;
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
    }

    internal void RunTick()
    {
        DrainControl();
        DrainInput();
        RemoveStalePeers();
        _match.Tick();
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
                    break;

                case ControlKind.JoinRequested:
                    if (_peers.TryGetValue(message.PeerId, out var peer) && ReferenceEquals(peer, message.Peer))
                        _match.TryJoin(message.PeerId, ((PeerState)peer.Tag).DevPlayerId);
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
                _match.EnqueueInput(message.PeerId, message.Packet);
        }
    }

    private void RemoveStalePeers()
    {
        // Safety net for lost Disconnected messages. ConnectionState is written by LiteNetLib's
        // thread; a stale read only delays removal by one tick.
        foreach (var pair in _peers)
        {
            if (pair.Value.ConnectionState != ConnectionState.Connected) _stalePeers.Add(pair.Key);
        }
        foreach (int peerId in _stalePeers) RemovePeer(peerId);
        _stalePeers.Clear();
    }

    private void RemovePeer(int peerId)
    {
        _peers.Remove(peerId);
        _match.Leave(peerId);
    }

    private void SendToPeer(int peerId, ReadOnlySpan<byte> data, DeliveryMethod method)
    {
        if (!_peers.TryGetValue(peerId, out var peer)) return;
        // NetPeer.Send is thread-safe and copies the data into LiteNetLib's own packet.
        peer.Send(data, method);
        _stats.AddOut(data.Length);
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
