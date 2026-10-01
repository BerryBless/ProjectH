using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Persistence;

// Phase 11 D8 without a database: the queue's limits and counters, the wire form of the store's results, and a service
// that never fails the server (persistence off, a database that does not answer).
public class StatsQueryTests
{
    private static StatsQuery Query(string id) => new(1, null!, id, Environment.TickCount64);

    private static async Task<StatsReply> NextReplyAsync(StatsQueryQueue queue, int timeoutMs = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (queue.TryTakeReply(out StatsReply reply)) return reply;
            await Task.Delay(20);
        }
        throw new TimeoutException("no reply");
    }

    [Fact]
    public void TheQueue_RejectsWhenFull_AndCounts()
    {
        var queue = new StatsQueryQueue(2);
        Assert.Equal(2, queue.Capacity);
        Assert.True(queue.TryEnqueue(Query("a")));
        Assert.True(queue.TryEnqueue(Query("b")));
        Assert.False(queue.TryEnqueue(Query("c")));

        Assert.True(queue.TryReply(new StatsReply(1, null!, StatsResponse.Of(StatsStatus.Busy))));
        Assert.True(queue.TryReply(new StatsReply(1, null!, StatsResponse.Of(StatsStatus.Unavailable))));
        Assert.False(queue.TryReply(new StatsReply(1, null!, StatsResponse.Of(StatsStatus.Ok))));
        queue.AddLimited();
        queue.AddUndelivered();

        Assert.Equal(new StatsQueryCounts(Requests: 2, Limited: 1, Busy: 1, Unavailable: 1, Undelivered: 2), queue.Counts);
        Assert.True(queue.TryTakeReply(out StatsReply first));
        Assert.Equal(StatsStatus.Busy, first.Response.Status);
    }

    [Fact]
    public void BuildResponse_WithoutStatistics_IsNoRecord()
    {
        StatsResponse r = StatsQueryService.BuildResponse(null, Array.Empty<MatchHistoryEntry>());
        Assert.Equal(StatsStatus.NoRecord, r.Status);
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void BuildResponse_ConvertsClampsAndKeepsTenRows()
    {
        var stats = new PlayerStats("p", Matches: 12, Wins: 3, Kills: 40, Deaths: 9, Damage: 5_000_000_000L, SurvivalMs: 3_723_999L);
        var ended = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var history = new List<MatchHistoryEntry>();
        for (int i = 0; i < 12; i++)
            history.Add(new MatchHistoryEntry(100 - i, 50 - i, ended.AddMinutes(-i), 16, (byte)(i + 1), i, 100 * i, 60_000 + i));

        StatsResponse r = StatsQueryService.BuildResponse(stats, history);

        Assert.Equal(StatsStatus.Ok, r.Status);
        Assert.Equal(12u, r.Summary.Matches);
        Assert.Equal(3u, r.Summary.Wins);
        Assert.Equal(40u, r.Summary.Kills);
        Assert.Equal(9u, r.Summary.Deaths);
        Assert.Equal(uint.MaxValue, r.Summary.Damage);      // the 64-bit sum is clamped
        Assert.Equal(3723u, r.Summary.SurvivalSeconds);     // whole seconds
        Assert.Equal(StatsResponse.MaxRows, r.Rows.Length);
        Assert.Equal((uint)new DateTimeOffset(ended).ToUnixTimeSeconds(), r.Rows[0].EndedUnixSeconds);
        Assert.Equal(50u, r.Rows[0].Round);
        Assert.Equal(16, r.Rows[0].Players);
        Assert.Equal(1, r.Rows[0].Placement);
        Assert.Equal(9, r.Rows[9].Kills);
        Assert.Equal(900u, r.Rows[9].Damage);
        Assert.Equal(60_009u, r.Rows[9].SurvivalMs);
    }

    [Fact]
    public async Task TheService_WithPersistenceOff_AnswersUnavailable()
    {
        var queue = new StatsQueryQueue();
        using var service = new StatsQueryService(queue, Options.Create(new PersistenceOptions { Enabled = false }),
            NullLogger<StatsQueryService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(new StatsQuery(7, null!, "x", Environment.TickCount64)));
        StatsReply reply = await NextReplyAsync(queue);
        Assert.Equal(7, reply.PeerId);
        Assert.Equal(StatsStatus.Unavailable, reply.Response.Status);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
    }

    // D8: a database that accepts the connection but never answers. Each query ends at its limit with Unavailable, and
    // the service goes on with the next one.
    [Fact]
    public async Task TheService_WhenTheDatabaseDoesNotAnswer_AnswersUnavailable_WithinTheLimit()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var queue = new StatsQueryQueue();
            var options = Options.Create(new PersistenceOptions
            {
                Enabled = true,
                ConnectionString = $"Server=127.0.0.1;Port={port};Database=projecth;User ID=nobody;Password=none;Connection Timeout=30;Pooling=false",
            });
            using var service = new StatsQueryService(queue, options, NullLogger<StatsQueryService>.Instance)
            {
                QueryTimeout = TimeSpan.FromMilliseconds(500),
            };
            await service.StartAsync(CancellationToken.None);

            var clock = Stopwatch.StartNew();
            Assert.True(queue.TryEnqueue(Query("a")));
            Assert.Equal(StatsStatus.Unavailable, (await NextReplyAsync(queue)).Response.Status);
            Assert.True(clock.ElapsedMilliseconds < 5000, $"took {clock.ElapsedMilliseconds} ms");

            Assert.True(queue.TryEnqueue(Query("b")));
            Assert.Equal(StatsStatus.Unavailable, (await NextReplyAsync(queue)).Response.Status);
            Assert.Equal(2, queue.Counts.Unavailable);

            await service.StopAsync(CancellationToken.None);
            Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
        }
        finally
        {
            silent.Stop();
            silent.Dispose();
        }
    }

    // A request that waited longer than the client waits (5 s) is answered Unavailable without touching the database: the
    // silent listener never sees a connection for it, and the answer comes long before the query limit. A fresh request
    // after it does reach the database.
    [Fact]
    public async Task ARequestOlderThanTheClientWaits_IsAnsweredUnavailable_WithoutAQuery()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var queue = new StatsQueryQueue();
            var options = Options.Create(new PersistenceOptions
            {
                Enabled = true,
                ConnectionString = $"Server=127.0.0.1;Port={port};Database=projecth;User ID=nobody;Password=none;Connection Timeout=30;Pooling=false",
            });
            using var service = new StatsQueryService(queue, options, NullLogger<StatsQueryService>.Instance)
            {
                QueryTimeout = TimeSpan.FromSeconds(3),
            };
            await service.StartAsync(CancellationToken.None);

            var clock = Stopwatch.StartNew();
            long old = Environment.TickCount64 - StatsQueryQueue.MaxQueueAgeMs - 1000;
            Assert.True(queue.TryEnqueue(new StatsQuery(3, null!, "old", old)));
            StatsReply reply = await NextReplyAsync(queue);
            Assert.Equal(3, reply.PeerId);
            Assert.Equal(StatsStatus.Unavailable, reply.Response.Status);
            Assert.True(clock.ElapsedMilliseconds < 2000, $"took {clock.ElapsedMilliseconds} ms");
            Assert.False(silent.Pending(), "no connection was opened for the stale request");

            Assert.True(queue.TryEnqueue(Query("fresh")));
            var waited = Stopwatch.StartNew();
            while (!silent.Pending() && waited.ElapsedMilliseconds < 3000) await Task.Delay(20);
            Assert.True(silent.Pending(), "the fresh request queried the database");

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            silent.Stop();
            silent.Dispose();
        }
    }

    [Fact]
    public void TheDefaultLimit_IsThreeSeconds()
    {
        var service = new StatsQueryService(new StatsQueryQueue(), Options.Create(new PersistenceOptions()), NullLogger<StatsQueryService>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(3), service.QueryTimeout);
        Assert.Equal(32, StatsQueryQueue.DefaultCapacity);
        Assert.Equal(2000, StatsQueryQueue.MinRequestIntervalMs);
        // The server stops caring about a request when the client's window stops waiting for it.
        Assert.Equal((int)(ProjectH.Client.UI.StatsWait.AnswerSeconds * 1000), StatsQueryQueue.MaxQueueAgeMs);
        service.Dispose();
    }
}
