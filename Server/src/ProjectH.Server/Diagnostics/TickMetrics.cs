using System;

namespace ProjectH.Server.Diagnostics;

public readonly struct TickStats
{
    // 기능: Tick 소요 시간 통계를 묶는다.
    // 입력: p50·p95·p99 - 백분위 Tick 시간(ms), max - 최대 Tick 시간(ms), sampleCount - 계산에 쓴 Sample 수.
    // 출력: 받은 값을 그대로 담은 TickStats.
    public TickStats(double p50, double p95, double p99, double max, int sampleCount)
    {
        P50 = p50;
        P95 = p95;
        P99 = p99;
        Max = max;
        SampleCount = sampleCount;
    }

    public double P50 { get; }
    public double P95 { get; }
    public double P99 { get; }
    public double Max { get; }
    public int SampleCount { get; }
}

// Keeps the most recent tick durations in a fixed ring, so memory does not grow with uptime.
// Percentiles are computed on a preallocated scratch copy: reporting does not allocate.
// Game loop thread only.
public sealed class TickMetrics
{
    private readonly double[] _samples;
    private readonly double[] _scratch;
    private int _next;
    private int _count;

    // 기능: 고정 크기 Tick 시간 Ring Buffer와 계산용 Scratch Buffer를 만든다.
    // 입력: sampleCapacity - 보관할 최근 Tick 수(1 이상).
    // 출력: Sample이 비어 있는 TickMetrics 객체. sampleCapacity가 1 미만이면 ArgumentOutOfRangeException.
    public TickMetrics(int sampleCapacity = 1024)
    {
        if (sampleCapacity < 1) throw new ArgumentOutOfRangeException(nameof(sampleCapacity));
        _samples = new double[sampleCapacity];
        _scratch = new double[sampleCapacity];
    }

    // 기능: Tick 하나의 소요 시간을 Ring에 기록한다. 가득 차면 가장 오래된 값을 덮어쓴다.
    // 입력: milliseconds - Tick 소요 시간(ms).
    // 출력: 반환값 없음. Sample Ring과 Sample 수가 갱신된다.
    public void Record(double milliseconds)
    {
        _samples[_next] = milliseconds;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    // 기능: 보관 중인 Tick 시간의 p50·p95·p99·최댓값을 계산한다.
    // 입력: 없음.
    // 출력: 계산된 TickStats. Sample이 없으면 모든 값이 0인 default.
    public TickStats Compute()
    {
        if (_count == 0) return default;
        Array.Copy(_samples, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        return new TickStats(Percentile(0.50), Percentile(0.95), Percentile(0.99), _scratch[_count - 1], _count);
    }

    // 기능: 보관 중인 Sample을 모두 비운다.
    // 입력: 없음.
    // 출력: 반환값 없음. Sample 수가 0이 된다.
    public void Reset()
    {
        _next = 0;
        _count = 0;
    }

    // 기능: 정렬된 Scratch Buffer에서 주어진 비율의 Percentile 값을 고른다.
    // 입력: fraction - 0~1 사이의 Percentile 비율(예: 0.95).
    // 출력: 해당 Percentile의 Tick 시간(ms).
    // Nearest-rank percentile on the sorted scratch buffer.
    private double Percentile(double fraction)
    {
        int index = (int)Math.Ceiling(fraction * _count) - 1;
        return _scratch[Math.Clamp(index, 0, _count - 1)];
    }
}
