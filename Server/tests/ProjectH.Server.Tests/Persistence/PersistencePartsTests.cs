using System;
using Microsoft.Extensions.Configuration;
using ProjectH.Server.Persistence;

namespace ProjectH.Server.Tests.Persistence;

// Phase 9 D6-D8: the queue between the game loop and the writer, and the options.
public class PersistencePartsTests
{
    // 기능: 참가자도 우승자도 없는 빈 경기 기록을 만든다.
    // 입력: round - 라운드 번호.
    // 출력: 지금 시작해 지금 끝난 MatchRecord.
    private static MatchRecord Record(int round) => new(round, DateTime.UtcNow, DateTime.UtcNow, null, Array.Empty<PlayerRecord>());

    [Fact]
    public void TheQueue_NeverBlocks_AndCountsWhatItDrops()
    {
        var queue = new MatchHistoryQueue(2);
        Assert.True(queue.TryEnqueue(Record(1)));
        Assert.True(queue.TryEnqueue(Record(2)));
        Assert.False(queue.TryEnqueue(Record(3)));   // full: rejected at once
        Assert.Equal(1, queue.Dropped);

        Assert.True(queue.Reader.TryRead(out MatchRecord? first));
        Assert.Equal(1, first!.Round);
        queue.Complete();
        Assert.False(queue.TryEnqueue(Record(4)));   // completed: rejected
        Assert.Equal(2, queue.Dropped);
        Assert.True(queue.Reader.TryRead(out MatchRecord? second));
        Assert.Equal(2, second!.Round);
    }

    [Fact]
    public void Options_Validate()
    {
        Assert.Null(new PersistenceOptions().Validate());
        Assert.Null(new PersistenceOptions { Enabled = true, ConnectionString = "Server=x" }.Validate());
        Assert.NotNull(new PersistenceOptions { Enabled = true, ConnectionString = " " }.Validate());
        Assert.NotNull(new PersistenceOptions { QueueCapacity = 0 }.Validate());
        Assert.NotNull(new PersistenceOptions { QueueCapacity = 1025 }.Validate());
        Assert.NotNull(new PersistenceOptions { MaxAttempts = 0 }.Validate());
        Assert.NotNull(new PersistenceOptions { ShutdownDrainSeconds = 0 }.Validate());   // would always look like a timeout
        Assert.Null(new PersistenceOptions { ShutdownDrainSeconds = 1 }.Validate());
        Assert.Null(new PersistenceOptions { ShutdownDrainSeconds = PersistenceOptions.MaxShutdownDrainSeconds }.Validate());
        Assert.NotNull(new PersistenceOptions { ShutdownDrainSeconds = 61 }.Validate());
    }

    [Fact]
    public void TheShippedSettings_AreValid_AndPointAtTheLocalContainer()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        var options = new PersistenceOptions();
        config.GetSection("Persistence").Bind(options);
        Assert.Null(options.Validate());
        Assert.True(options.Enabled);
        Assert.Contains("Server=127.0.0.1", options.ConnectionString);
        Assert.Contains("Database=projecth", options.ConnectionString);
    }
}
