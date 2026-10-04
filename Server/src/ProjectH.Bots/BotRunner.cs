using System.Diagnostics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Bots;

// Phase 7 D2, D9: runs every bot on the calling thread. Step() is one tick for all bots: connect the ones due (one
// every ConnectIntervalMs), receive, decide, send. Run() repeats Step() at the server's SimHz until cancelled or the
// duration ends, never running more than MaxCatchUpTicks late ticks in a row, and logs a stats line every
// StatsIntervalSeconds. All bot state is owned by this one thread: no locks. Dispose closes every socket.
// Phase 10 D11: with Reconnect, a bot whose established connection drops for a retryable reason (the client's table,
// DisconnectCodes.ShouldReconnect) gets a new connection and brain at DisconnectCodes.ReconnectOffsetSeconds(n) after
// the drop (1, 3, 7 s, like the Unity client), at most DisconnectCodes.MaxReconnectAttempts times until it has joined
// again. Each attempt has the short connect budget; one still connecting when the next slot comes is replaced.
public sealed class BotRunner : IDisposable
{
    public const int MaxCatchUpTicks = 3;
    private const int LoopSampleCount = 512;

    private readonly BotOptions _options;
    private readonly Action<string> _log;
    private readonly BotConnection[] _connections;
    private readonly BotBrain[] _brains;
    // Phase 13 D17: each bot's building, on top of its brain.
    private readonly BotBuilder[] _builders;
    private readonly bool[] _disconnectLogged;   // each bot's disconnect is logged once, when Step first sees it
    // Phase 10 D11, per bot: when the next attempt is due (NaN = none), when the drop that started the cycle was seen,
    // attempts started since the bot last joined, and whether the current connection was ever established (a first
    // connect that fails is not retried).
    private readonly double[] _reconnectAt;
    private readonly double[] _dropAt;
    private readonly int[] _attempts;
    private readonly bool[] _established;
    private long _reconnects;
    // Totals of the connections a reconnect replaced, so the stats line keeps counting inputs, packets and bytes across
    // them. Build counts (sent, accepted, refused, pieces seen) are not carried over and restart at 0.
    private long _retiredInputs;
    private long _retiredPackets;
    private long _retiredBytes;
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

    // 기능: 옵션을 검증하고 봇 수만큼 연결·두뇌·건설 객체를 시드별로 만든다(접속은 Step에서 시작).
    // 입력: options - 봇 실행 옵션, log - 로그 한 줄을 출력할 함수.
    // 출력: 아직 아무 봇도 접속하지 않았고 예약된 재접속이 없는 러너. 옵션이 틀리면 ArgumentException.
    public BotRunner(BotOptions options, Action<string> log)
    {
        string? error = options.Validate();
        if (error != null) throw new ArgumentException(error, nameof(options));
        _options = options;
        _log = log;
        _connections = new BotConnection[options.Count];
        _brains = new BotBrain[options.Count];
        _builders = new BotBuilder[options.Count];
        _disconnectLogged = new bool[options.Count];
        _reconnectAt = new double[options.Count];
        Array.Fill(_reconnectAt, double.NaN);
        _dropAt = new double[options.Count];
        _attempts = new int[options.Count];
        _established = new bool[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            _connections[i] = new BotConnection();
            _brains[i] = new BotBrain(unchecked(options.Seed + i));
            _builders[i] = new BotBuilder(unchecked(options.Seed + 7919 * (i + 1)), options.BuildSpam);
        }
    }

    public int Count => _connections.Length;
    public long Reconnects => _reconnects;
    // 기능: 봇 하나의 서버 연결을 돌려준다.
    // 입력: index - 봇 번호(0부터).
    // 출력: 그 봇의 BotConnection.
    public BotConnection Connection(int index) => _connections[index];
    // 기능: 봇 하나의 판단(이동·사격 결정) 객체를 돌려준다.
    // 입력: index - 봇 번호(0부터).
    // 출력: 그 봇의 BotBrain.
    public BotBrain Brain(int index) => _brains[index];
    // 기능: 봇 하나의 건설 요청 생성기를 돌려준다.
    // 입력: index - 봇 번호(0부터).
    // 출력: 그 봇의 BotBuilder.
    public BotBuilder Builder(int index) => _builders[index];

    // 기능: 모든 봇의 한 Tick을 처리한다. 접속 순서가 된 봇을 접속시키고, 예약된 재접속을 실행하고, 각 봇의 수신·판단·입력/건설 송신을 한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 봇 연결·View·두뇌 상태가 갱신되고 서버에 입력·건설 패킷이 전송되며, 이번 루프 소요 시간이 표본에 기록된다. 새로 끊긴 봇은 한 번 로그를 남기고 재접속이 예약될 수 있다.
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
            if (!double.IsNaN(_reconnectAt[i]) && start >= _reconnectAt[i]) Reconnect(i);
            BotConnection connection = _connections[i];
            if (!connection.Disconnected) connection.Update(elapsedMs);
            if (connection.Connected && !connection.Disconnected)
            {
                _established[i] = true;
                _reconnectAt[i] = double.NaN;   // this attempt got through: no next slot unless it drops again
            }
            if (connection.View.Joined) _attempts[i] = 0;
            if (connection.Disconnected)
            {
                if (!_disconnectLogged[i])
                {
                    _disconnectLogged[i] = true;
                    _log($"{_options.BotName(i)} disconnected: {connection.DisconnectReason}");
                    ScheduleReconnect(i, connection, start);
                }
                continue;
            }
            if (_brains[i].Tick(connection.View, now, out var command))
            {
                BuildRequest request = default;
                bool build = _options.Build && _builders[i].Tick(connection.View, now, _brains[i].Target, ref command, out request);
                connection.SendInput(command);
                // After the input that carries the aim, so the server places with it (it uses the last input's aim).
                if (build) connection.SendBuild(request);
            }
        }

        _loopSamples[_loopNext] = (_clock.Elapsed.TotalSeconds - start) * 1000.0;
        _loopNext = (_loopNext + 1) % LoopSampleCount;
        if (_loopCount < LoopSampleCount) _loopCount++;
    }

    // 기능: 끊긴 봇의 다음 재접속 시각을 정한다. 재접속 옵션이 꺼졌거나, 재시도할 수 없는 끊김이거나, 한 번도 성립하지 않은 첫 접속이면 예약하지 않는다.
    // 입력: i - 봇 번호, connection - 끊긴 연결, now - 끊김을 본 시각(초).
    // 출력: 반환값 없음. _reconnectAt[i]가 다음 시도 시각 또는 NaN(재접속 없음)이 되고, 새 주기면 _dropAt[i]가 기록된다. 이미 예약된 시도가 있거나 횟수를 다 썼으면 예약을 바꾸지 않는다.
    // A drop starts a cycle (its time is the base of every slot); a failed attempt waits for the slot already armed.
    private void ScheduleReconnect(int i, BotConnection connection, double now)
    {
        bool cycle = _established[i] || _attempts[i] > 0;
        if (!_options.Reconnect || !connection.Retryable || !cycle)
        {
            _reconnectAt[i] = double.NaN;
            return;
        }
        if (_attempts[i] == 0) _dropAt[i] = now;
        if (!double.IsNaN(_reconnectAt[i]) || _attempts[i] >= DisconnectCodes.MaxReconnectAttempts) return;
        _reconnectAt[i] = _dropAt[i] + DisconnectCodes.ReconnectOffsetSeconds(_attempts[i] + 1);
    }

    // 기능: 봇 하나의 재접속 시도를 시작한다. 이전 연결을 닫고 같은 이름으로 새 연결·두뇌·건설 객체를 만들어 접속한다.
    // 입력: i - 재접속할 봇 번호.
    // 출력: 반환값 없음. 시도 횟수와 다음 시도 시각이 갱신되고, 이전 연결의 입력 수·수신 패킷·바이트 통계는 누적값으로 옮겨진 뒤(건설 통계는 새 연결에서 0부터) 연결이 닫히며, 새 연결이 접속을 시작한다.
    // A fresh connection (its own Seq from 1, as the server expects after a resume) and a fresh brain, same name. The
    // previous connection, still connecting or already failed, is closed. The next slot is armed at once, so an attempt
    // that has not connected by then is replaced.
    private void Reconnect(int i)
    {
        _attempts[i]++;
        _reconnectAt[i] = _attempts[i] < DisconnectCodes.MaxReconnectAttempts
            ? _dropAt[i] + DisconnectCodes.ReconnectOffsetSeconds(_attempts[i] + 1)
            : double.NaN;
        BotConnection old = _connections[i];
        _retiredInputs += old.InputsSent;
        _retiredPackets += old.PacketsIn;
        _retiredBytes += old.BytesIn;
        old.Dispose();
        _connections[i] = new BotConnection(reconnect: true);
        _brains[i] = new BotBrain(unchecked(_options.Seed + i));
        _builders[i] = new BotBuilder(unchecked(_options.Seed + 7919 * (i + 1)), _options.BuildSpam);
        _disconnectLogged[i] = false;
        _established[i] = false;
        _reconnects++;
        _log($"{_options.BotName(i)} reconnecting (attempt {_attempts[i]}/{DisconnectCodes.MaxReconnectAttempts})");
        _connections[i].Connect(_options.Host, _options.Port, _options.BotName(i));
    }

    // 기능: 취소되거나, 실행 시간이 끝나거나, 모든 봇이 완전히 끊길 때까지 서버 SimHz로 Step을 반복하고 주기적으로 통계를 남긴다.
    // 입력: token - 실행 중단 요청(Ctrl+C).
    // 출력: 반환값 없음. 호출 스레드를 막고 돌며, 끝나면 루프를 빠져나온다(소켓은 Dispose가 닫는다). 밀린 Tick이 MaxCatchUpTicks를 넘으면 몰아 돌지 않고 건너뛴다.
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

    // 기능: 모든 봇 연결을 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음. 현재 연결들의 소켓이 정리된다.
    public void Dispose()
    {
        foreach (BotConnection connection in _connections) connection.Dispose();
    }

    // 기능: 루프 주기로 쓸 Tick 속도를 정한다.
    // 입력: 없음.
    // 출력: 참가한 첫 봇이 받은 서버 SimHz. 참가한 봇이 없으면 30.
    private int TickRate()
    {
        for (int i = 0; i < _started; i++)
        {
            if (_connections[i].View.Joined) return _connections[i].View.SimHz;
        }
        return 30;
    }

    // 기능: 연결·참가·생존 수, 매치 상태, 초당 입력·수신량, 루프 p95, 재접속, 건설 결과를 한 줄 로그로 남긴다.
    // 입력: seconds - 이전 통계 이후 지난 시간(초), 초당 값 계산에 쓴다.
    // 출력: 반환값 없음. 통계 로그가 출력되고 다음 구간 계산용 직전 누적값이 갱신된다.
    private void LogStats(double seconds)
    {
        int connected = 0, joined = 0, alive = 0;
        long inputs = _retiredInputs, packets = _retiredPackets, bytes = _retiredBytes;
        long builds = 0, accepted = 0, refused = 0, pieces = 0;
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
            builds += c.BuildsSent;
            accepted += c.View.BuildResults[0];
            for (int code = 1; code < c.View.BuildResults.Length; code++) refused += c.View.BuildResults[code];
            pieces = Math.Max(pieces, c.View.Pieces.Count);
        }
        _log($"Bots connected={connected}/{_connections.Length} joined={joined} alive={alive} match={match} " +
             $"inputs/s={(inputs - _lastInputs) / seconds:F0} pktIn/s={(packets - _lastPackets) / seconds:F0} " +
             $"bytesIn/s={(bytes - _lastBytes) / seconds:F0} loopMs p95={LoopP95():F2} reconnects={_reconnects} " +
             $"builds sent={builds} accepted={accepted} refused={refused} piecesSeen={pieces}");
        _lastInputs = inputs;
        _lastPackets = packets;
        _lastBytes = bytes;
    }

    // 기능: 더 돌릴 봇이 남았는지 확인한다.
    // 입력: 없음.
    // 출력: 모든 봇이 접속을 시작했고 전부 끊겼으며 예약된 재접속도 없으면 true, 아니면 false.
    // True once every bot has been started and every one of them is gone for good (no reconnect pending): nothing is
    // left to run.
    private bool AllDisconnected()
    {
        if (_started < _connections.Length) return false;
        for (int i = 0; i < _connections.Length; i++)
        {
            if (!_connections[i].Disconnected || !double.IsNaN(_reconnectAt[i])) return false;
        }
        return true;
    }

    // 기능: 최근 최대 LoopSampleCount개 Step 소요 시간의 95 백분위를 구한다(원본 표본은 건드리지 않고 복사본을 정렬).
    // 입력: 없음.
    // 출력: Step 한 번의 p95 소요 시간(ms). 표본이 없으면 0.
    private double LoopP95()
    {
        if (_loopCount == 0) return 0;
        Array.Copy(_loopSamples, _loopScratch, _loopCount);
        Array.Sort(_loopScratch, 0, _loopCount);
        int index = (int)Math.Ceiling(0.95 * _loopCount) - 1;
        return _loopScratch[Math.Clamp(index, 0, _loopCount - 1)];
    }

    // 기능: 목표 시각까지 기다린다. 2 ms보다 많이 남으면 Sleep하고, 그 이하는 Yield로 맞춘다.
    // 입력: target - 깨어날 시각(러너 시계 기준 초), token - 대기 중단 요청.
    // 출력: 반환값 없음. 목표 시각이 되거나 취소되면 돌아온다.
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
