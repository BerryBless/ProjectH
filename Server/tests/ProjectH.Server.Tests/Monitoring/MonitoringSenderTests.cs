using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Monitoring;
using ProjectH.Server.Tests.Diagnostics;

namespace ProjectH.Server.Tests.Monitoring;

// Request §65, §91: send, refused, timeout, slow, restored, cancellation, shutdown. Real HTTP on the loopback.
public class MonitoringSenderTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    // 기능: Monitoring 검증을 통과할 만한 Snapshot을 만든다.
    // 입력: round - 구분용 값.
    // 출력: ServerId "dev-server-01"인 Snapshot.
    private static ServerMonitoringSnapshot Snap(int round = 1) => new()
    {
        ServerId = "dev-server-01", Version = "1.0.0", ProtocolVersion = 18, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ObservedAt = DateTimeOffset.UtcNow, WindowSeconds = 5, MatchState = "Playing", Round = round, DbQueueCapacity = 16,
    };

    // 기능: 테스트 주기(50 ms)·Timeout(300 ms)으로 전송기를 만든다.
    // 입력: endpoint - Monitoring 기본 주소, slot - 슬롯, log - 로그 기록기, token - Ingest Token(빈 값 = 헤더 없음).
    // 출력: Start 전의 MonitoringSender.
    private static MonitoringSender Sender(Uri endpoint, MonitoringSlot slot, ListLogger log, string token = "") =>
        new(new MonitoringOptions { Enabled = true, Endpoint = endpoint.ToString(), Token = token }, slot, log, Interval, Timeout);

    // 기능: 조건이 참이 될 때까지 10 ms마다 본다.
    // 입력: condition - 기다릴 조건, milliseconds - 최대 대기.
    // 출력: 조건이 참이 되면 끝나는 Task. 시간 안에 안 되면 Assert 실패.
    private static async Task WaitUntilAsync(Func<bool> condition, int milliseconds = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < milliseconds, "the condition did not come true in time");
            await Task.Delay(10);
        }
    }

    // 기능: 수준과 문구가 맞는 로그 줄 수를 센다.
    // 입력: log - 로그 기록기, level - 수준, text - 포함할 문구(빈 값 = 모두).
    // 출력: 맞는 줄 수.
    private static int Count(ListLogger log, LogLevel level, string text) => log.Entries.Count(e => e.Level == level && e.Message.Contains(text));

    [Fact]
    public async Task Sends_TheSnapshot_WithTheToken_AndEmptiesTheSlot()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, slot, log, token: "t");
        slot.Publish(Snap());
        await sender.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sender.Sent == 1);
        Assert.Equal(1, fake.Received);
        Assert.Equal("t", fake.LastToken);
        Assert.True(fake.LastBodyBytes > 0 && fake.LastBodyBytes < MonitoringContract.MaxBodyBytes, $"payload {fake.LastBodyBytes} bytes");
        Assert.Null(slot.Take());
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnEmptySlot_SendsNothing()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, new MonitoringSlot(), log);
        await sender.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(0, fake.Received);
        Assert.Equal(0, sender.Sent);
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectionRefused_LogsLostOnce_ThenStaysQuiet()
    {
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(FakeMonitoringServer.ClosedPort(), slot, log);
        await sender.StartAsync(CancellationToken.None);
        for (int i = 1; i <= 3; i++)
        {
            slot.Publish(Snap(i));
            int expected = i;
            await WaitUntilAsync(() => sender.Failed >= expected);
        }
        Assert.True(sender.Lost);
        Assert.Equal(1, Count(log, LogLevel.Warning, "Monitoring connection lost"));
        Assert.Equal(0, Count(log, LogLevel.Error, ""));
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ServerError_IsAFailure_AndRecoveryLogsRestoredOnce()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        fake.StatusCode = 500;
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, slot, log);
        await sender.StartAsync(CancellationToken.None);
        slot.Publish(Snap(1));
        await WaitUntilAsync(() => sender.Failed == 1);
        slot.Publish(Snap(2));
        await WaitUntilAsync(() => sender.Failed == 2);
        fake.StatusCode = 202;
        slot.Publish(Snap(3));
        await WaitUntilAsync(() => sender.Sent == 1);
        slot.Publish(Snap(4));
        await WaitUntilAsync(() => sender.Sent == 2);
        Assert.False(sender.Lost);
        Assert.Equal(1, Count(log, LogLevel.Warning, "Monitoring connection lost"));
        Assert.Equal(1, Count(log, LogLevel.Information, "Monitoring connection restored"));
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ASlowServer_FailsWithinTheTimeout_AndTheLoopGoesOn()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        fake.DelayMs = 3000;
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, slot, log);
        await sender.StartAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        slot.Publish(Snap(1));
        await WaitUntilAsync(() => sender.Failed == 1, 2000);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms; the timeout is {Timeout.TotalMilliseconds} ms");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("timeout"));
        fake.DelayMs = 0;
        slot.Publish(Snap(2));
        await WaitUntilAsync(() => sender.Sent == 1);
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_CancelsAnInFlightPost_AndReturnsQuickly()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        fake.DelayMs = 5000;
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        // A long timeout so only Stop can end the post.
        using var slow = new MonitoringSender(new MonitoringOptions { Enabled = true, Endpoint = fake.Endpoint.ToString() }, slot, log, Interval, TimeSpan.FromSeconds(10));
        await slow.StartAsync(CancellationToken.None);
        slot.Publish(Snap());
        await WaitUntilAsync(() => fake.Received == 1);
        var sw = Stopwatch.StartNew();
        using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await slow.StopAsync(stopLimit.Token);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"stop took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, slow.Sent);
        Assert.Equal(0, slow.Failed);                                                   // shutdown is not a failure
        Assert.Equal(0, Count(log, LogLevel.Warning, "Monitoring connection lost"));
    }

    [Fact]
    public async Task AWrongHostName_IsAFailure_NotACrash()
    {
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(new Uri("http://nonexistent.invalid:1/"), slot, log);
        await sender.StartAsync(CancellationToken.None);
        slot.Publish(Snap());
        await WaitUntilAsync(() => sender.Failed == 1, 5000);
        Assert.Equal(1, Count(log, LogLevel.Warning, "Monitoring connection lost"));
        await sender.StopAsync(CancellationToken.None);
    }
}
