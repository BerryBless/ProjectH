using System;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Persistence;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 11 D8 over real UDP: who may ask, the per-connection limit, Busy, Unavailable without persistence, and an
// answer whose connection left. The database itself is in MySqlTests and StatsQueryTests.
public sealed class StatsQueryIntegrationTests
{
    // 기능: 전적 조회 큐를 단 4인 실제 UDP 서버를 포트 0에 띄운다.
    // 입력: queue - 서버가 요청을 넣고 답을 꺼낼 전적 조회 큐.
    // 출력: 스레드가 시작된 GameLoop(호출자가 Dispose한다).
    private static GameLoop StartServer(StatsQueryQueue queue)
    {
        var loop = new GameLoop(new ServerOptions
        {
            Port = 0,
            MaxPlayers = 4,
            DisconnectTimeoutMs = 1000,
            StatsIntervalSeconds = 60,
        }, TestGameData.Create(), NullLogger.Instance, TestGameData.CombatLoadout, statsQueries: queue);
        loop.Start();
        return loop;
    }

    // 기능: 큐를 읽는 StatsQueryService를 만들어 동기로 시작한다.
    // 입력: queue - 읽을 전적 조회 큐, options - 영속 설정(null = 영속 끔: 모든 요청이 바로 Unavailable).
    // 출력: 시작된 StatsQueryService(호출자가 Stop으로 끝낸다).
    // Persistence off: every request is answered Unavailable at once.
    internal static StatsQueryService StartService(StatsQueryQueue queue, PersistenceOptions? options = null)
    {
        var service = new StatsQueryService(queue, Options.Create(options ?? new PersistenceOptions { Enabled = false }),
            NullLogger<StatsQueryService>.Instance);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return service;
    }

    // 기능: StatsQueryService를 동기로 멈추고 해제한다.
    // 입력: service - 멈출 서비스.
    // 출력: 반환값 없음. 서비스의 작업이 끝나고 자원이 해제된다.
    internal static void Stop(StatsQueryService service)
    {
        service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        service.Dispose();
    }

    [Fact]
    public void ARequestBeforeTheJoin_IsIgnored()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQueryService service = StartService(queue);
        try
        {
            using var client = new HeadlessClient();
            client.Connect(server.LocalPort, "early");
            Assert.True(Pump.Until(() => client.Connected, 3000, client), "connect");
            client.SendStatsRequest();

            Assert.True(Pump.Until(() => queue.Counts.Limited == 1, 3000, client), "counted");
            Assert.False(Pump.Until(() => client.StatsResponses.Count > 0, 500, client), "no answer");
            Assert.Equal(0, queue.Counts.Requests);
            Assert.Equal(0, server.Health.BadPacketsTotal);
            Assert.False(client.Disconnected);
        }
        finally
        {
            Stop(service);
        }
    }

    [Fact]
    public void WithPersistenceOff_TheAnswerIsUnavailable()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQueryService service = StartService(queue);
        try
        {
            using var client = Join(server, "nodb");
            client.SendStatsRequest();
            Assert.True(Pump.Until(() => client.StatsResponses.Count == 1, 3000, client), "answer");
            Assert.Equal(StatsStatus.Unavailable, client.StatsResponses[0].Status);
            Assert.Empty(client.StatsResponses[0].Rows);
            Assert.Equal(new StatsQueryCounts(1, 0, 0, 1, 0), queue.Counts);
        }
        finally
        {
            Stop(service);
        }
    }

    // D8: one request per 2 s per connection. More are dropped and counted, never answered and never a kick, even far
    // above the bad-packet threshold (20).
    [Fact]
    public void RequestsWithinTwoSeconds_GetNoAnswer_AreCounted_AndNeverKick()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQueryService service = StartService(queue);
        try
        {
            using var client = Join(server, "masher");
            for (int i = 0; i < 30; i++) client.SendStatsRequest();
            Assert.True(Pump.Until(() => client.StatsResponses.Count == 1 && queue.Counts.Limited == 29, 3000, client), "one answer");
            Assert.False(Pump.Until(() => client.StatsResponses.Count > 1, 700, client), "no second answer");
            Assert.Equal(1, queue.Counts.Requests);
            Assert.Equal(0, server.Health.BadPacketsTotal);
            Assert.Equal(0, server.Health.Kicks(DisconnectCode.Kicked));
            Assert.False(client.Disconnected);
        }
        finally
        {
            Stop(service);
        }
    }

    [Fact]
    public void AFullRequestQueue_AnswersBusy()
    {
        // Capacity 1 and nobody reading: the first request fills the queue, the next one is answered Busy.
        var queue = new StatsQueryQueue(1);
        using GameLoop server = StartServer(queue);
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        a.SendStatsRequest();
        Assert.True(Pump.Until(() => queue.Counts.Requests == 1, 3000, a, b), "queued");
        b.SendStatsRequest();
        Assert.True(Pump.Until(() => b.StatsResponses.Count == 1, 3000, a, b), "busy answer");
        Assert.Equal(StatsStatus.Busy, b.StatsResponses[0].Status);
        Assert.Empty(a.StatsResponses);
        Assert.Equal(1, queue.Counts.Busy);
    }

    [Fact]
    public void AnAnswerForAConnectionThatLeft_IsDropped()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQuery query;
        var leaver = Join(server, "leaver");
        try
        {
            leaver.SendStatsRequest();
            Assert.True(Pump.Until(() => queue.Counts.Requests == 1, 3000, leaver), "queued");
            Assert.True(queue.Requests.TryRead(out query));
        }
        finally
        {
            leaver.Dispose();
        }
        Assert.True(Pump.Until(() => server.Health.Peers == 0, 3000), "the server saw the leave");
        Assert.True(queue.TryReply(new StatsReply(query.PeerId, query.Peer, StatsResponse.Of(StatsStatus.NoRecord))));
        Assert.True(Pump.Until(() => queue.Counts.Undelivered == 1, 3000), "dropped");
    }

    // LiteNetLib reuses peer ids: the answer to a connection that left must not go to the new connection that got its
    // id. The game loop sends a reply only to the very NetPeer that asked.
    [Fact]
    public void AnAnswerForAConnectionThatLeft_IsNotSentToTheConnectionThatReusedItsId()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        StatsQuery query;
        var leaver = Join(server, "leaver");
        try
        {
            leaver.SendStatsRequest();
            Assert.True(Pump.Until(() => queue.Counts.Requests == 1, 3000, leaver), "queued");
            Assert.True(queue.Requests.TryRead(out query));
        }
        finally
        {
            leaver.Dispose();
        }
        Assert.True(Pump.Until(() => server.Health.Peers == 0, 3000), "the server saw the leave");

        // LiteNetLib hands a freed id out again only once it dropped the old peer (after its disconnect handshake), so
        // connect until a connection gets it; ids freed meanwhile queue behind it.
        HeadlessClient? next = null;
        try
        {
            for (int attempt = 0; attempt < 20 && next == null; attempt++)
            {
                HeadlessClient candidate = Join(server, "next" + attempt);
                if (candidate.ServerPeerId == query.PeerId)
                {
                    next = candidate;
                }
                else
                {
                    candidate.Dispose();
                    Pump.Until(() => false, 250);
                }
            }
            Assert.True(next != null, "a new connection got the old id");

            Assert.True(queue.TryReply(new StatsReply(query.PeerId, query.Peer, StatsResponse.Of(StatsStatus.NoRecord))));
            Assert.True(Pump.Until(() => queue.Counts.Undelivered == 1, 3000, next!), "dropped");
            Assert.False(Pump.Until(() => next!.StatsResponses.Count > 0, 500, next!), "nothing delivered");
        }
        finally
        {
            next?.Dispose();
        }
    }

    // Phase 11: only a join that succeeded makes a connection that may ask. A join the match refused (MatchFull: the
    // graced player keeps its slot) gets no answer while it waits for its close.
    [Fact]
    public void ARefusedJoin_GetsNoStatistics()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = HardeningIntegrationTests.StartServer(maxPlayers: 2, countdown: 1, statsQueries: queue);
        StatsQueryService service = StartService(queue);
        var a = Join(server, "a");
        try
        {
            using var b = Join(server, "b");
            Assert.True(Pump.Until(() => b.MatchStates.Count > 0 &&
                                         b.MatchStates[^1].State is MatchFlowState.Playing or MatchFlowState.FinalPhase, 5000, a, b),
                "match running");
            a.Kill();
            Assert.True(Pump.Until(() => server.Health.GraceStarts == 1, 4000, b), "graced");

            using var c = Join(server, "c", expected: JoinResult.MatchFull);
            c.SendStatsRequest();
            Assert.True(Pump.Until(() => queue.Counts.Limited == 1, 3000, c, b), "counted");
            Assert.Equal(0, queue.Counts.Requests);
            Assert.Empty(c.StatsResponses);
        }
        finally
        {
            a.Dispose();
            Stop(service);
        }
    }

    [Fact]
    public void ARequestWithABody_IsAnInvalidPacket()
    {
        var queue = new StatsQueryQueue();
        using GameLoop server = StartServer(queue);
        using var client = Join(server, "body");
        client.SendRaw(new byte[] { (byte)PacketId.StatsRequest, 1 });
        Assert.True(Pump.Until(() => server.Health.BadPackets(BadPacketReason.Malformed) == 1, 3000, client), "malformed");
        Assert.Equal(0, queue.Counts.Requests);
    }
}
