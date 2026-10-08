using System.Threading;

namespace ProjectH.Monitoring.Tests;

// A clock that moves only when a test says so. Timestamps are TimeSpan ticks.
public sealed class ManualTime : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

    // 기능: 시계를 앞으로 돌린다.
    // 입력: by - 더할 시간.
    // 출력: 반환값 없음. GetTimestamp와 GetUtcNow가 by만큼 늘어난다.
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
