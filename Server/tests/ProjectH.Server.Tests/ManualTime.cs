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
    // 기능: 테스트가 올린 만큼의 현재 Timestamp를 읽는다.
    // 입력: 없음.
    // 출력: TimeSpan Tick 단위의 Timestamp.
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    // 기능: 2026-10-01 00:00 UTC에서 현재 Timestamp만큼 지난 시각을 돌려준다.
    // 입력: 없음.
    // 출력: 현재 UTC 시각.
    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

    // 기능: 시계를 주어진 시간만큼 앞으로 옮긴다.
    // 입력: by - 옮길 시간.
    // 출력: 반환값 없음. 이후 GetTimestamp·GetUtcNow가 그만큼 늦은 값을 돌려준다.
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
