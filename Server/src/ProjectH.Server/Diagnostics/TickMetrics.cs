using System;

namespace ProjectH.Server.Diagnostics;

public readonly struct TickStats
{
    // 기능: Tick 시간 분포 요약값을 담는 값을 만든다.
    // 입력: p50 - 중앙값(ms), p95 - 95 백분위(ms), p99 - 99 백분위(ms), max - 최대(ms), sampleCount - 계산에 쓴 표본 수.
    // 출력: 다섯 값을 그대로 담은 TickStats.
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

    // 기능: 고정 크기 Ring과 정렬용 Scratch 배열을 미리 만든다.
    // 입력: sampleCapacity - 보관할 최근 표본 수(1 이상, 아니면 ArgumentOutOfRangeException).
    // 출력: 표본이 없는 TickMetrics.
    public TickMetrics(int sampleCapacity = 1024)
    {
        if (sampleCapacity < 1) throw new ArgumentOutOfRangeException(nameof(sampleCapacity));
        _samples = new double[sampleCapacity];
        _scratch = new double[sampleCapacity];
    }

    // 기능: Tick 시간 하나를 Ring에 넣는다. 가득 차면 가장 오래된 표본을 덮어쓴다.
    // 입력: milliseconds - 이번 Tick이 걸린 시간(ms).
    // 출력: 반환값 없음.
    public void Record(double milliseconds)
    {
        _samples[_next] = milliseconds;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    // 기능: 보관 중인 표본을 Scratch에 복사·정렬해 백분위와 최대를 구한다(할당 없음).
    // 입력: 없음.
    // 출력: p50·p95·p99·max·표본 수를 담은 TickStats. 표본이 없으면 default(모두 0).
    public TickStats Compute()
    {
        if (_count == 0) return default;
        Array.Copy(_samples, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        return new TickStats(Percentile(0.50), Percentile(0.95), Percentile(0.99), _scratch[_count - 1], _count);
    }

    // 기능: 보관 중인 표본을 모두 버린다(배열은 그대로 재사용).
    // 입력: 없음.
    // 출력: 반환값 없음. 다음 Compute는 default를 돌려준다.
    public void Reset()
    {
        _next = 0;
        _count = 0;
    }

    // 기능: 정렬된 Scratch에서 Nearest-rank 방식의 백분위 값을 읽는다. Compute가 정렬한 뒤에만 부른다.
    // 입력: fraction - 백분위(0~1).
    // 출력: 그 백분위의 Tick 시간(ms).
    // Nearest-rank percentile on the sorted scratch buffer.
    private double Percentile(double fraction)
    {
        int index = (int)Math.Ceiling(fraction * _count) - 1;
        return _scratch[Math.Clamp(index, 0, _count - 1)];
    }
}
