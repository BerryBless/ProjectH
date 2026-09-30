using System;

namespace ProjectH.Server.Diagnostics;

public readonly struct TickStats
{
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

    public TickMetrics(int sampleCapacity = 1024)
    {
        if (sampleCapacity < 1) throw new ArgumentOutOfRangeException(nameof(sampleCapacity));
        _samples = new double[sampleCapacity];
        _scratch = new double[sampleCapacity];
    }

    public void Record(double milliseconds)
    {
        _samples[_next] = milliseconds;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    public TickStats Compute()
    {
        if (_count == 0) return default;
        Array.Copy(_samples, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        return new TickStats(Percentile(0.50), Percentile(0.95), Percentile(0.99), _scratch[_count - 1], _count);
    }

    public void Reset()
    {
        _next = 0;
        _count = 0;
    }

    // Nearest-rank percentile on the sorted scratch buffer.
    private double Percentile(double fraction)
    {
        int index = (int)Math.Ceiling(fraction * _count) - 1;
        return _scratch[Math.Clamp(index, 0, _count - 1)];
    }
}
