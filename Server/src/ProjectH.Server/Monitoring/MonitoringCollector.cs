using System;
using System.Reflection;
using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Monitoring;

// What the game loop hands the collector: its own gauges and the network totals it owns the reading of. A struct: no
// allocation per publish besides the snapshot itself.
public readonly record struct MonitoringSource(int ConnectedPeers, int Players, int Graced, MatchFlowState MatchState, int Round,
    long PacketsIn, long PacketsOut, long BytesIn, long BytesOut, HealthCounters Health);

// Monitoring D3: builds the snapshot on the game loop thread, every IntervalSeconds, from values the loop already has.
// Its own TickMetrics window (the loop's is reset by the Stats line). Rates are deltas of the loop's cumulative network
// totals (never TakeDelta: that belongs to the Stats line). CPU as the Stats line computes it: process time over window
// × cores. Non-finite results (a zero window, no samples) become 0: System.Text.Json refuses NaN and so does the
// monitoring server. No logging and no I/O here; the only lock is the bounded channel's own, inside the dbQueue delegate
// (MatchHistoryQueue.Count), once per interval and never nested.
// Game loop thread only (all fields); the snapshot leaves through MonitoringSlot.
public sealed class MonitoringCollector
{
    private readonly MonitoringOptions _options;
    private readonly MonitoringSlot _slot;
    private readonly TimeProvider _time;
    private readonly Func<(int Count, int Capacity)> _dbQueue;
    private readonly TickMetrics _ticks;
    private readonly long _intervalTicks;   // IntervalSeconds in TimeProvider timestamp units
    private readonly long _startTimestamp;
    private readonly DateTimeOffset _startedAt;
    private long _lastPublish;   // the start of the current window (WindowSeconds)
    // The next publish deadline, fixed-rate like the Stats line's nextStats: a publish one tick late does not push the next
    // one later, so the snapshots keep their phase against the sender's fixed-rate timer.
    private long _nextDue;
    private TimeSpan _cpuAtLast;
    private long _packetsIn, _packetsOut, _bytesIn, _bytesOut;

    // The server build, read once: the assembly's informational version, else its version.
    public string Version { get; }

    // 기능: 수집기를 만든다. Tick 링은 SimHz × (IntervalSeconds + 1) 샘플을 한 번 할당한다(주기 안의 모든 Tick + 기한을 Tick마다
    //       보느라 생기는 여유 1초분).
    // 입력: options - 검증이 끝난 설정, simHz - Tick 주파수, slot - 보낼 슬롯, time - 시계, dbQueue - DB 큐의 (수, 용량)(Game Loop 스레드에서 부른다).
    // 출력: MonitoringCollector(StartedAt·첫 창의 시작 = 지금).
    public MonitoringCollector(MonitoringOptions options, int simHz, MonitoringSlot slot, TimeProvider time, Func<(int Count, int Capacity)> dbQueue)
    {
        _options = options;
        _slot = slot;
        _time = time;
        _dbQueue = dbQueue;
        _intervalTicks = options.IntervalSeconds * time.TimestampFrequency;
        _ticks = new TickMetrics(Math.Max(1, simHz * (options.IntervalSeconds + 1)));
        _startTimestamp = _lastPublish = time.GetTimestamp();
        _nextDue = _startTimestamp + _intervalTicks;
        _startedAt = time.GetUtcNow();
        _cpuAtLast = Environment.CpuUsage.TotalTime;
        Assembly assembly = typeof(MonitoringCollector).Assembly;
        Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }

    // 기능: Tick 하나의 길이를 이 주기의 링에 기록한다. Game Loop 스레드, 매 Tick(고정 배열에 쓰기 한 번, 할당 없음).
    // 입력: milliseconds - Tick 길이.
    // 출력: 반환값 없음. 링이 가득 차면 가장 오래된 샘플을 덮어쓴다.
    public void RecordTick(double milliseconds) => _ticks.Record(milliseconds);

    // 기능: Publish 기한(생성 + IntervalSeconds의 배수, 고정 주기)이 됐는지 본다. Game Loop 스레드, 매 Tick(GetTimestamp 한 번).
    // 입력: 없음.
    // 출력: 기한이 지났으면 true.
    public bool IsDue() => _time.GetTimestamp() >= _nextDue;

    // 기능: Snapshot을 만들어 슬롯에 넣고, 다음 기한을 한 주기 뒤로(멈춤 뒤면 지금부터 한 주기 뒤로) 옮기고, 창의 기준값(시각·CPU·네트워크
    //       누적)을 지금으로 옮긴다. Game Loop 스레드, 주기마다.
    // 입력: s - 루프의 게이지와 누적값.
    // 출력: 슬롯에 넣은 Snapshot. 예외가 나도 기한과 기준값은 이미 옮겨져 다음 시도는 다음 기한이다.
    public ServerMonitoringSnapshot Publish(in MonitoringSource s)
    {
        long now = _time.GetTimestamp();
        double window = _time.GetElapsedTime(_lastPublish, now).TotalSeconds;
        // The deadline and the baselines move first, so a publish that throws below is not retried every tick and the next
        // window starts here (the same rule as the Stats line's "nextStats first"). After a stall past the next deadline, the
        // next one is a full interval from now: one publish, no catch-up burst.
        _lastPublish = now;
        _nextDue += _intervalTicks;
        if (now >= _nextDue) _nextDue = now + _intervalTicks;
        TimeSpan cpu = Environment.CpuUsage.TotalTime;
        double cpuPercent = (cpu - _cpuAtLast).TotalSeconds / (window * Environment.ProcessorCount) * 100.0;
        _cpuAtLast = cpu;
        double packetsIn = Rate(s.PacketsIn - _packetsIn, window);
        double packetsOut = Rate(s.PacketsOut - _packetsOut, window);
        double bytesIn = Rate(s.BytesIn - _bytesIn, window);
        double bytesOut = Rate(s.BytesOut - _bytesOut, window);
        _packetsIn = s.PacketsIn;
        _packetsOut = s.PacketsOut;
        _bytesIn = s.BytesIn;
        _bytesOut = s.BytesOut;
        TickStats t = _ticks.Compute();
        _ticks.Reset();
        (int dbCount, int dbCapacity) = _dbQueue();
        HealthCounters h = s.Health;

        var snapshot = new ServerMonitoringSnapshot
        {
            ServerId = _options.ServerId,
            Version = Version,
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            StartedAt = _startedAt,
            ObservedAt = _time.GetUtcNow(),
            UptimeSeconds = Finite(_time.GetElapsedTime(_startTimestamp, now).TotalSeconds),
            WindowSeconds = Finite(window),
            ConnectedPeers = s.ConnectedPeers,
            Players = s.Players,
            Graced = s.Graced,
            MatchState = s.MatchState.ToString(),
            Round = s.Round,
            TickSamples = t.SampleCount,
            TickP50Ms = Finite(t.P50),
            TickP95Ms = Finite(t.P95),
            TickP99Ms = Finite(t.P99),
            TickMaxMs = Finite(t.Max),
            CpuPercent = Finite(cpuPercent),
            ManagedMemoryBytes = GC.GetTotalMemory(false),
            WorkingSetBytes = Environment.WorkingSet,
            GcGen0 = GC.CollectionCount(0),
            GcGen1 = GC.CollectionCount(1),
            GcGen2 = GC.CollectionCount(2),
            PacketsInPerSecond = packetsIn,
            PacketsOutPerSecond = packetsOut,
            BytesInPerSecond = bytesIn,
            BytesOutPerSecond = bytesOut,
            Exceptions = h.TickFailures + h.LoopFailures + h.PlayerFailures + h.CallbackErrors,
            InvalidPackets = h.BadPacketsTotal,
            Disconnects = h.DisconnectTimeouts + h.DisconnectOthers,
            DbQueueCount = Math.Max(0, dbCount),       // -1 when the channel cannot count
            DbQueueCapacity = Math.Max(0, dbCapacity),
        };

        _slot.Publish(snapshot);
        return snapshot;
    }

    // 기능: NaN·Infinity·음수를 0으로 만든다.
    // 입력: v - 값.
    // 출력: 유한한 0 이상의 값.
    private static double Finite(double v) => double.IsFinite(v) && v >= 0 ? v : 0;

    // 기능: 누적 차이를 초당 값으로 만든다.
    // 입력: delta - 누적 차이, window - 창 길이(초).
    // 출력: 초당 값. 창이 0 이하이거나 차이가 0 이하이면 0.
    private static double Rate(long delta, double window) => window > 0 && delta > 0 ? delta / window : 0;
}
