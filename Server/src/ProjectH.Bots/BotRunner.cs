using System.Diagnostics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Bots;

// Phase 7 D2, D9: runs every bot on the calling thread. Step() is one tick for all bots: connect the ones due (one
// every ConnectIntervalMs), receive, decide, send. Run() repeats Step() at the server's SimHz until cancelled or the
// duration ends, never running more than MaxCatchUpTicks late ticks in a row, and logs a stats line every
// StatsIntervalSeconds. All bot state is owned by this one thread: no locks. Dispose closes every socket.
public sealed class BotRunner : IDisposable
{
    public const int MaxCatchUpTicks = 3;
    private const int LoopSampleCount = 512;

    private readonly BotOptions _options;
    private readonly Action<string> _log;
    private readonly BotConnection[] _connections;
    private readonly BotBrain[] _brains;
    private readonly bool[] _disconnectLogged;   // each bot's disconnect is logged once, when Step first sees it
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _loopSamples = new double[LoopSampleCount];
    private readonly double[] _loopScratch = new double[LoopSampleCount];
    private int _loopNext;
    private int _loopCount;
    private int _started;
    private double _lastStep;
    private long _lastInputs;
    private long _lastPackets;
    private long _lastBytes;
    private double _lastStatsAt;

    public BotRunner(BotOptions options, Action<string> log)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));
        _options = options;
        _log = log;
        _connections = new BotConnection[options.Count];
        _brains = new BotBrain[options.Count];
        _disconnectLogged = new bool[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            _connections[i] = new BotConnection();
            _brains[i] = new BotBrain(unchecked(options.Seed + i));
        }
    }

    public int Count => _connections.Length;
    public BotConnection Connection(int index) => _connections[index];
    public BotBrain Brain(int index) => _brains[index];

    public void Step()
    {
        double start = _clock.Elapsed.TotalSeconds;
        float elapsedMs = (float)((start - _lastStep) * 1000.0);
        _lastStep = start;

        while (_started < _connections.Length && start * 1000.0 >= (double)_started * _options.ConnectIntervalMs)
        {
            _connections[_started].Connect(_options.Host, _options.Port, _options.BotName(_started));
            _started++;
        }

        float now = (float)start;
        for (int i = 0; i < _started; i++)
        {
            BotConnection connection = _connections[i];
            if (!connection.Disconnected) connection.Update(elapsedMs);
            if (connection.Disconnected)
            {
                if (!_disconnectLogged[i])
                {
                    _disconnectLogged[i] = true;
                    _log($"{_options.BotName(i)} disconnected: {connection.DisconnectReason}");
                }
                continue;
            }
            if (_brains[i].Tick(connection.View, now, out var command)) connection.SendInput(command);
        }

        _loopSamples[_loopNext] = (_clock.Elapsed.TotalSeconds - start) * 1000.0;
        _loopNext = (_loopNext + 1) % LoopSampleCount;
        if (_loopCount < LoopSampleCount) _loopCount++;
    }

    public void Run(CancellationToken token)
    {
        double next = _clock.Elapsed.TotalSeconds;
        _lastStatsAt = next;
        while (!token.IsCancellationRequested)
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (_options.DurationSeconds > 0 && now >= _options.DurationSeconds) break;
            Step();
            if (AllDisconnected())
            {
                _log("All bots disconnected.");
                break;
            }

            double tick = 1.0 / TickRate();
            next += tick;
            now = _clock.Elapsed.TotalSeconds;
            if (now - next > MaxCatchUpTicks * tick) next = now;   // too far behind: skip, do not burst
            if (now - _lastStatsAt >= _options.StatsIntervalSeconds)
            {
                LogStats(now - _lastStatsAt);
                _lastStatsAt = now;
            }
            WaitUntil(next, token);
        }
    }

    public void Dispose()
    {
        foreach (BotConnection connection in _connections) connection.Dispose();
    }

    private int TickRate()
    {
        for (int i = 0; i < _started; i++)
        {
            if (_connections[i].View.Joined) return _connections[i].View.SimHz;
        }
        return 30;
    }

    private void LogStats(double seconds)
    {
        int connected = 0, joined = 0, alive = 0;
        long inputs = 0, packets = 0, bytes = 0;
        string match = "none";
        for (int i = 0; i < _connections.Length; i++)
        {
            BotConnection c = _connections[i];
            if (c.Connected && !c.Disconnected) connected++;
            if (c.View.Joined) joined++;
            if (c.View.Alive) alive++;
            if (c.View.HasMatchState && match == "none") match = $"{c.View.Match.State}#{c.View.Match.Round}";
            inputs += c.InputsSent;
            packets += c.PacketsIn;
            bytes += c.BytesIn;
        }
        _log($"Bots connected={connected}/{_connections.Length} joined={joined} alive={alive} match={match} " +
             $"inputs/s={(inputs - _lastInputs) / seconds:F0} pktIn/s={(packets - _lastPackets) / seconds:F0} " +
             $"bytesIn/s={(bytes - _lastBytes) / seconds:F0} loopMs p95={LoopP95():F2}");
        _lastInputs = inputs;
        _lastPackets = packets;
        _lastBytes = bytes;
    }

    // True once every bot has been started and every one of them is gone: nothing is left to run.
    private bool AllDisconnected()
    {
        if (_started < _connections.Length) return false;
        for (int i = 0; i < _connections.Length; i++)
        {
            if (!_connections[i].Disconnected) return false;
        }
        return true;
    }

    private double LoopP95()
    {
        if (_loopCount == 0) return 0;
        Array.Copy(_loopSamples, _loopScratch, _loopCount);
        Array.Sort(_loopScratch, 0, _loopCount);
        int index = (int)Math.Ceiling(0.95 * _loopCount) - 1;
        return _loopScratch[Math.Clamp(index, 0, _loopCount - 1)];
    }

    private void WaitUntil(double target, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            double remainingMs = (target - _clock.Elapsed.TotalSeconds) * 1000.0;
            if (remainingMs <= 0) return;
            if (remainingMs > 2.0) Thread.Sleep((int)(remainingMs - 1.0));
            else Thread.Yield();
        }
    }
}
