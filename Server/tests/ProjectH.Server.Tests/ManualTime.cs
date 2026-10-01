using System;
using System.Threading;

namespace ProjectH.Server.Tests;

// Phase 10: a clock that moves only when a test says so (the reset window of D6, the stall watchdog of D9).
// Timestamps are TimeSpan ticks; GetElapsedTime works from them through TimestampFrequency.
public sealed class ManualTime : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
