using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Qa;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// QA-1 D5, D6: the work queue between HTTP threads and the game loop, the event ring and the metrics ring.
public sealed class QaControlTests
{
    [Fact]
    public async Task AThrowingWorkItem_IsACommandError_NotATickFailure()
    {
        using var h = new QaHarness();
        var (status, body) = await h.Run(_ => throw new InvalidOperationException("boom"));
        Assert.Equal(500, status);
        Assert.Contains("boom", body.GetProperty("error").GetString());
        h.Ticks(3);
        Assert.Equal(0, h.Loop.Health.TickFailures);
        Assert.Equal(1, h.Qa.Failed);
        // The queue still works afterwards.
        Assert.Equal(200, (await h.Command("mark", args: new { text = "after" })).Status);
    }

    [Fact]
    public async Task AFullQueue_Refuses_With503_AndATickRunsAtMost16()
    {
        using var h = new QaHarness();
        int ran = 0;
        var tasks = Enumerable.Range(0, QaOptions.QueueCapacity)
            .Select(_ => h.Qa.SubmitAsync(_ => { ran++; return QaResult.Ok(); })).ToList();
        QaResult refused = await h.Qa.SubmitAsync(_ => QaResult.Ok());
        Assert.Equal(503, refused.Status);
        Assert.Equal(1, h.Qa.Rejected);

        h.Loop.RunTick();
        Assert.Equal(QaOptions.MaxItemsPerTick, ran);
        h.Ticks(QaOptions.QueueCapacity / QaOptions.MaxItemsPerTick);
        Assert.Equal(QaOptions.QueueCapacity, ran);
        foreach (var task in tasks) Assert.Equal(200, (await task).Status);
    }

    [Fact]
    public async Task ATimedOutItem_Answers504_AndNeverRuns()
    {
        using var h = new QaHarness(new QaOptions { CommandTimeoutMs = 100 });
        bool ran = false;
        QaResult result = await h.Qa.SubmitAsync(_ => { ran = true; return QaResult.Ok(); });
        Assert.Equal(504, result.Status);
        h.Ticks(2);
        Assert.False(ran);
        Assert.Equal(1, h.Qa.TimedOut);
    }

    [Fact]
    public async Task Queries_ReturnPlainDtos()
    {
        using var h = new QaHarness();
        h.Join(1, "qa-a");
        h.Join(2, "qa-b");
        var (status, players) = await h.Run(t => QaResult.Data(QaQueries.Players(t.Match)));
        Assert.Equal(200, status);
        Assert.Equal(2, players.GetArrayLength());
        JsonElement a = players[0];
        Assert.Equal("qa-a", a.GetProperty("devPlayerId").GetString());
        Assert.Equal("Ground", a.GetProperty("mode").GetString());
        Assert.True(a.GetProperty("connected").GetBoolean());
        Assert.Equal(100, a.GetProperty("health").GetInt32());

        var (_, match) = await h.Run(t => QaResult.Data(QaQueries.Match(t.Match, 30)));
        Assert.Equal("Starting", match.GetProperty("state").GetString());
        Assert.Equal(2, match.GetProperty("players").GetInt32());

        var (_, health) = await h.Run(t => QaResult.Data(QaQueries.Health(t)));
        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.Equal(30, health.GetProperty("simHz").GetInt32());
    }

    [Fact]
    public async Task Events_FollowTheMatch()
    {
        using var h = new QaHarness();
        var (a, b) = h.StartMatch();
        await h.Command("damagePlayer", "qa-a", new { amount = 30 });
        await h.Command("giveAmmo", "qa-b", new { type = "light", amount = 10 });
        h.Match.Disconnect(b.PeerId, allowGrace: true);
        h.Ticks(1);
        await h.Command("killPlayer", "qa-a");
        h.Ticks(2);

        var (_, body) = await h.Run(t => QaResult.Data(t.Qa.Events.Query(0, 1000)));
        string[] types = body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToArray();
        Assert.Contains("PlayerJoined", types);
        Assert.Contains("MatchStateChanged", types);
        Assert.Contains("PlayerDamaged", types);
        Assert.Contains("InventoryChanged", types);
        Assert.Contains("PlayerGraced", types);
        Assert.Contains("PlayerKilled", types);
        JsonElement damaged = body.GetProperty("events").EnumerateArray().First(e => e.GetProperty("type").GetString() == "PlayerDamaged");
        Assert.Equal("qa-a", damaged.GetProperty("player").GetString());
        Assert.Equal(30, damaged.GetProperty("data").GetProperty("amount").GetInt32());

        long next = body.GetProperty("next").GetInt64();
        var (_, later) = await h.Run(t => QaResult.Data(t.Qa.Events.Query(next, 1000)));
        Assert.Equal(next, later.GetProperty("next").GetInt64());
        Assert.Equal(0, later.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void EventRing_IsBounded_AndReportsWhatWasMissed()
    {
        var events = new QaEvents();
        for (int i = 0; i < 1500; i++) events.Add((uint)i, "Test", null);
        Assert.Equal(QaOptions.EventCapacity, events.Count);
        Assert.Equal(501, events.Oldest);
        Assert.Equal(500, events.DroppedTotal);

        JsonElement body = JsonSerializer.SerializeToElement(events.Query(0, 10), QaHttpService.Json);
        Assert.Equal(500, body.GetProperty("dropped").GetInt64());
        Assert.Equal(501, body.GetProperty("events")[0].GetProperty("seq").GetInt64());
        Assert.Equal(510, body.GetProperty("next").GetInt64());
    }

    [Fact]
    public async Task EventsOff_NoDiff()
    {
        using var h = new QaHarness(new QaOptions { Events = false });
        h.Join(1, "qa-a");
        h.Ticks(3);
        Assert.Equal(0, h.Qa.Events.Count);
        await Task.CompletedTask;
    }

    [Fact]
    public void MetricsRing_WindowAndBound()
    {
        var metrics = new QaMetrics(simHz: 30);
        for (int i = 0; i < 30 * 200; i++) metrics.Record(i < 30 * 195 ? 50.0 : 1.0, 0.1);
        Assert.Equal(30 * QaOptions.MetricsWindowSeconds, metrics.Count);   // never more than the window
        QaTickMetrics last5 = metrics.Snapshot(5, default);
        Assert.Equal(150, last5.Samples);
        Assert.Equal(1.0, last5.TickMaxMs);
        QaTickMetrics last10 = metrics.Snapshot(10, default);
        Assert.Equal(300, last10.Samples);
        Assert.Equal(50.0, last10.TickMaxMs);
        Assert.Equal(0.1, last10.QaCommandMs, 6);
        QaTickMetrics empty = new QaMetrics(30).Snapshot(10, default);
        Assert.Equal(0, empty.Samples);
        Assert.False(double.IsNaN(empty.TickP99Ms));
    }
}

// Changes a process environment variable, so it does not run in parallel with any other test collection.
[CollectionDefinition("QaHttp", DisableParallelization = true)]
public sealed class QaHttpCollection { }

[Collection("QaHttp")]
public sealed class QaHttpTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public int StopRequests;
        public void StopApplication() => Interlocked.Increment(ref StopRequests);
    }

    [Fact]
    public async Task Listener_IsLoopbackOnly_EvenWhenAspNetCoreUrlsSaysAnyAddress()
    {
        string? urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        string? ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:5099");
        Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "5098");
        using var h = new QaHarness(new QaOptions { Port = 0 });
        var lifetime = new Lifetime();
        var service = new QaHttpService(h.Qa, lifetime, NullLogger<QaHttpService>.Instance);
        using var pumpStop = new CancellationTokenSource();
        Task? pump = null;
        try
        {
            await service.StartAsync(CancellationToken.None);
            string address = Assert.Single(service.BoundAddresses);
            var uri = new Uri(address);
            Assert.Equal("127.0.0.1", uri.Host);
            Assert.NotEqual(5099, uri.Port);
            Assert.Equal(uri.Port, h.Qa.QaPort);

            // The test thread pool plays the game loop: it is the only thread that ticks (and so touches the match).
            pump = Task.Run(() =>
            {
                while (!pumpStop.IsCancellationRequested)
                {
                    h.Loop.RunTick();
                    Thread.Sleep(5);
                }
            });
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{uri.Port}") };
            using JsonDocument health = JsonDocument.Parse(await http.GetStringAsync("/qa/health"));
            Assert.True(health.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(uri.Port, health.RootElement.GetProperty("qaPort").GetInt32());

            HttpResponseMessage bad = await http.PostAsync("/qa/command", new StringContent("{nope", Encoding.UTF8, "application/json"));
            Assert.Equal(400, (int)bad.StatusCode);
            // Over 16 KB: refused with 413 before the body is read. Kestrel may close the connection while the client is still
            // uploading, so a reset is the other acceptable outcome; either way nothing runs.
            // Its own client: a reset connection must not be reused by the next request.
            using var bigClient = new HttpClient { BaseAddress = http.BaseAddress };
            try
            {
                HttpResponseMessage big = await bigClient.PostAsync("/qa/command",
                    new StringContent("{\"command\":\"mark\",\"args\":{\"text\":\"" + new string('x', 20000) + "\"}}", Encoding.UTF8, "application/json"));
                Assert.Equal(413, (int)big.StatusCode);
            }
            catch (HttpRequestException)
            {
            }
            HttpResponseMessage mark = await http.PostAsync("/qa/command",
                new StringContent("{\"command\":\"mark\",\"runId\":\"r\",\"args\":{\"text\":\"hi\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(200, (int)mark.StatusCode);
            HttpResponseMessage metrics = await http.GetAsync("/qa/metrics?windowSeconds=500");
            Assert.Equal(400, (int)metrics.StatusCode);
            HttpResponseMessage stop = await http.PostAsync("/qa/server/stop", null);
            Assert.Equal(200, (int)stop.StatusCode);
            for (int i = 0; i < 100 && Volatile.Read(ref lifetime.StopRequests) == 0; i++) await Task.Delay(10);
            Assert.Equal(1, Volatile.Read(ref lifetime.StopRequests));

        }
        finally
        {
            pumpStop.Cancel();
            if (pump != null) await pump;
            await service.StopAsync(CancellationToken.None);
            await service.DisposeAsync();
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", urls);
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", ports);
        }
    }
}
