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
    // Totals of the connections a reconnect replaced, so the stats line keeps counting across them.
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

    // 기능: 봇 실행기를 만든다(봇마다 연결·두뇌·건축기, 리뷰 수정 B4: 봇마다 Resume 표 하나, B1: 서버 공개키).
    // 입력: options - 봇 설정, log - 로그 함수.
    // 출력: 아직 접속하지 않은 BotRunner.
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
        _tickets = new ResumeTicket[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            _tickets[i] = new ResumeTicket();
            _connections[i] = new BotConnection(serverPublicKeyXml: options.ServerPublicKeyXml);
            _brains[i] = new BotBrain(unchecked(options.Seed + i));
            _builders[i] = new BotBuilder(unchecked(options.Seed + 7919 * (i + 1)), options.BuildSpam);
        }
    }

    // Review fix B4: each bot's resume key across its connections (made once per bot, lives with the runner).
    private readonly ResumeTicket[] _tickets;

    public int Count => _connections.Length;
    public long Reconnects => _reconnects;
    // 기능: 봇 번호의 지금 연결을 돌려준다(재접속하면 새 객체).
    // 입력: index - 봇 번호.
    // 출력: 그 봇의 BotConnection.
    public BotConnection Connection(int index) => _connections[index];
    // 기능: 봇 번호의 지금 두뇌를 돌려준다(재접속하면 새 객체).
    // 입력: index - 봇 번호.
    // 출력: 그 봇의 BotBrain.
    public BotBrain Brain(int index) => _brains[index];
    // 기능: 봇 번호의 지금 건축기를 돌려준다(재접속하면 새 객체).
    // 입력: index - 봇 번호.
    // 출력: 그 봇의 BotBuilder.
    public BotBuilder Builder(int index) => _builders[index];

    // 기능: 한 걸음: 접속 간격에 맞춰 봇을 접속시키고(리뷰 수정 B4: 봇의 Resume 표와 함께), 재접속 시각이면 재접속하고, 각 봇을 갱신해 입력을 보낸다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Step()
    {
        double start = _clock.Elapsed.TotalSeconds;
        float elapsedMs = (float)((start - _lastStep) * 1000.0);
        _lastStep = start;

        while (_started < _connections.Length && start * 1000.0 >= (double)_started * _options.ConnectIntervalMs)
        {
            _connections[_started].Connect(_options.Host, _options.Port, _options.BotName(_started), _tickets[_started]);
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

    // 기능: 끊긴 봇의 다음 재접속 시각을 잡는다(Phase 10 D11). 재접속이 꺼졌거나 재시도 불가 코드거나 한 번도 맺지 못한 첫 접속이면 잡지 않는다.
    // 입력: i - 봇 번호, connection - 끊긴 연결, now - 끊김을 본 시각(초).
    // 출력: 반환값 없음. _reconnectAt[i]가 다음 시각 또는 NaN이 되고, 주기의 첫 끊김이면 _dropAt[i]가 기록된다.
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

    // 기능: 봇 하나를 새 연결로 다시 접속시킨다(리뷰 수정 B4: Resume 표의 키로 만든 증명을 실어 자기 캐릭터를 되찾는다).
    // 입력: i - 봇 번호.
    // 출력: 반환값 없음.
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
        _connections[i] = new BotConnection(reconnect: true, _options.ServerPublicKeyXml);
        _brains[i] = new BotBrain(unchecked(_options.Seed + i));
        _builders[i] = new BotBuilder(unchecked(_options.Seed + 7919 * (i + 1)), _options.BuildSpam);
        _disconnectLogged[i] = false;
        _established[i] = false;
        _reconnects++;
        _log($"{_options.BotName(i)} reconnecting (attempt {_attempts[i]}/{DisconnectCodes.MaxReconnectAttempts})");
        _connections[i].Connect(_options.Host, _options.Port, _options.BotName(i), _tickets[i]);   // review fix B4: resume with the proof
    }

    // 기능: 취소되거나 duration이 끝나거나 모든 봇이 끝내 끊길 때까지 Step()을 서버 SimHz로 되풀이한다(MaxCatchUpTicks 넘게 늦으면 건너뛴다, StatsIntervalSeconds마다 통계 로그).
    // 입력: token - 중단 토큰(Ctrl+C).
    // 출력: 반환값 없음. 돌아오면 실행이 끝난 것이다(소켓은 Dispose가 닫는다).
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

    // 기능: 모든 봇의 소켓을 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Dispose()
    {
        foreach (BotConnection connection in _connections) connection.Dispose();
    }

    // 기능: 실행 주기를 정한다(Join한 첫 봇이 받은 서버 SimHz).
    // 입력: 없음.
    // 출력: 초당 Tick 수, Join한 봇이 없으면 30.
    private int TickRate()
    {
        for (int i = 0; i < _started; i++)
        {
            if (_connections[i].View.Joined) return _connections[i].View.SimHz;
        }
        return 30;
    }

    // 기능: 통계 한 줄을 로그한다(접속·Join·생존 수, 경기 상태, 초당 입력·수신 패킷·바이트, 루프 p95, 재접속, 건설 요청·수락·거절, 본 조각 수). 재접속으로 버린 연결의 합계도 포함한다.
    // 입력: seconds - 지난 통계 이후 경과 시간(초).
    // 출력: 반환값 없음. 다음 차이를 낼 기준 합계가 기억된다.
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

    // 기능: 모든 봇이 시작됐고 전부 끝내 끊겼는지(재접속 예정 없음) 본다.
    // 입력: 없음.
    // 출력: 더 돌릴 것이 없으면 true.
    private bool AllDisconnected()
    {
        if (_started < _connections.Length) return false;
        for (int i = 0; i < _connections.Length; i++)
        {
            if (!_connections[i].Disconnected || !double.IsNaN(_reconnectAt[i])) return false;
        }
        return true;
    }

    // 기능: 최근 Step 소요 시간(최대 LoopSampleCount개)의 p95를 구한다(nearest rank).
    // 입력: 없음.
    // 출력: p95(ms), 표본이 없으면 0.
    private double LoopP95()
    {
        if (_loopCount == 0) return 0;
        Array.Copy(_loopSamples, _loopScratch, _loopCount);
        Array.Sort(_loopScratch, 0, _loopCount);
        int index = (int)Math.Ceiling(0.95 * _loopCount) - 1;
        return _loopScratch[Math.Clamp(index, 0, _loopCount - 1)];
    }

    // 기능: 목표 시각까지 기다린다(2 ms 넘게 남으면 Sleep, 마지막 1~2 ms는 Yield).
    // 입력: target - 기다릴 시각(실행기 시계, 초), token - 중단 토큰.
    // 출력: 반환값 없음. 시각이 되거나 취소되면 돌아온다.
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
