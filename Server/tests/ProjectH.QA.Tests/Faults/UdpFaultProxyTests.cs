using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProjectH.QA.Faults;

namespace ProjectH.QA.Tests.Faults;

// Loopback tests: a UdpClient plays the actor, another plays the game server.
public sealed class UdpFaultProxyTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(3);
    private const byte WarmUpByte = 0xEE;

    // On some Windows hosts (seen on the dev machine) the host firewall drops the first datagram(s) of every new UDP
    // flow while it authorizes it. LiteNetLib retries its connect, so a real actor does not care, but a test that sends
    // exactly one datagram would. The harness therefore warms up all four flows (client->proxy, proxy->server,
    // server->proxy, proxy->client) before the test, and the test reads counters relative to the warmed-up state.
    // Warm-up traffic is clean, so the proxy's Random draws nothing during it.
    private sealed class Harness : IAsyncDisposable
    {
        public readonly UdpClient Server = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly UdpClient Client = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly UdpFaultProxy Proxy;
        public IPEndPoint Upstream = null!;   // the proxy's upstream endpoint as the server sees it
        private UdpFaultProxyCounters _baseline;

        // 기능: 가짜 서버 소켓을 향하는 UdpFaultProxy를 만든다.
        // 입력: seed - Proxy 난수 시드, maxQueued - 지연 큐 최대 길이.
        // 출력: Proxy가 가짜 서버 끝점을 바라보는 Harness 객체(아직 워밍업 전).
        private Harness(int seed, int maxQueued)
        {
            Proxy = new UdpFaultProxy((IPEndPoint)Server.Client.LocalEndPoint!, seed, maxQueued);
        }

        // 기능: Harness를 만들고 네 방향 UDP 흐름을 워밍업한다. 워밍업에 실패하면 자원을 해제하고 예외를 다시 던진다.
        // 입력: seed - Proxy 난수 시드, maxQueued - 지연 큐 최대 길이.
        // 출력: 워밍업을 마치고 Counters 기준점이 잡힌 Harness.
        public static async Task<Harness> CreateAsync(int seed = 1, int maxQueued = UdpFaultProxy.DefaultMaxQueuedDatagrams)
        {
            var h = new Harness(seed, maxQueued);
            try
            {
                await h.WarmUpAsync();
            }
            catch
            {
                await h.DisposeAsync();
                throw;
            }
            return h;
        }

        // 기능: 서버와 클라이언트 양쪽으로 워밍업 Datagram이 도달할 때까지 보내고, 남은 것을 비운 뒤 Counters 기준점을 기록한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Upstream 끝점과 _baseline이 설정된다. 40회 안에 도달하지 못하면 TimeoutException.
        private async Task WarmUpAsync()
        {
            for (int i = 0; ; i++)
            {
                if (i == 40) throw new TimeoutException("warm-up: nothing reached the server");
                await SendToServerAsync([WarmUpByte]);
                var r = await TryReceiveAsync(Server, TimeSpan.FromMilliseconds(50));
                if (r is not null)
                {
                    Upstream = r.Value.RemoteEndPoint;
                    break;
                }
            }
            for (int i = 0; ; i++)
            {
                if (i == 40) throw new TimeoutException("warm-up: nothing reached the client");
                await SendToClientAsync([WarmUpByte]);
                if (await TryReceiveAsync(Client, TimeSpan.FromMilliseconds(50)) is not null) break;
            }

            // Let late warm-up datagrams land, then forget them.
            await Task.Delay(100);
            Drain(Server);
            Drain(Client);
            _baseline = Proxy.Counters;
        }

        public UdpFaultProxyCounters Counters
        {
            get
            {
                var c = Proxy.Counters;
                return new UdpFaultProxyCounters(
                    c.Forwarded - _baseline.Forwarded,
                    c.Dropped - _baseline.Dropped,
                    c.Duplicated - _baseline.Duplicated,
                    c.Delayed - _baseline.Delayed,
                    c.QueueFull - _baseline.QueueFull,
                    c.Foreign - _baseline.Foreign);
            }
        }

        // 기능: 클라이언트 소켓에서 Proxy의 수신 끝점으로 Datagram을 보낸다.
        // 입력: data - 보낼 바이트.
        // 출력: 반환값 없음. Proxy를 거쳐 서버 소켓으로 전달될 Datagram이 송신된다.
        public Task SendToServerAsync(byte[] data) => Client.SendAsync(data, data.Length, Proxy.ListenEndPoint);

        // 기능: 서버 소켓에서 Proxy의 Upstream 끝점으로 Datagram을 보낸다.
        // 입력: data - 보낼 바이트.
        // 출력: 반환값 없음. Proxy를 거쳐 클라이언트 소켓으로 전달될 Datagram이 송신된다.
        public Task SendToClientAsync(byte[] data) => Server.SendAsync(data, data.Length, Upstream);

        // 기능: 소켓에 도착해 있는 Datagram을 모두 읽어 버린다.
        // 입력: udp - 비울 UDP 소켓.
        // 출력: 반환값 없음. 소켓 수신 버퍼가 비워진다.
        private static void Drain(UdpClient udp)
        {
            IPEndPoint? any = null;
            while (udp.Available > 0) udp.Receive(ref any);
        }

        // 기능: Proxy를 멈추고 서버·클라이언트 소켓을 닫는다.
        // 입력: 없음.
        // 출력: 반환값 없음. Proxy와 두 소켓이 해제된다.
        public async ValueTask DisposeAsync()
        {
            await Proxy.DisposeAsync();
            Server.Dispose();
            Client.Dispose();
        }
    }

    // 기능: 제한 시간 안에 소켓에서 Datagram 하나를 받는다.
    // 입력: udp - 수신할 UDP 소켓, timeout - 대기 시간.
    // 출력: 받은 Datagram. 제한 시간 안에 오지 않으면 null.
    private static async Task<UdpReceiveResult?> TryReceiveAsync(UdpClient udp, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await udp.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // 기능: 조건이 참이 될 때까지 10 ms 간격으로 기다린다.
    // 입력: condition - 기다릴 조건, timeout - 최대 대기 시간.
    // 출력: 반환값 없음. 제한 시간을 넘기면 TimeoutException.
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ForwardsBothWays()
    {
        await using var h = await Harness.CreateAsync();

        await h.SendToServerAsync([1, 2, 3]);
        var atServer = await TryReceiveAsync(h.Server, ReceiveTimeout);
        Assert.NotNull(atServer);
        Assert.Equal(new byte[] { 1, 2, 3 }, atServer.Value.Buffer);
        Assert.Equal(h.Upstream, atServer.Value.RemoteEndPoint);

        await h.SendToClientAsync([9, 8]);
        var atClient = await TryReceiveAsync(h.Client, ReceiveTimeout);
        Assert.NotNull(atClient);
        Assert.Equal(new byte[] { 9, 8 }, atClient.Value.Buffer);
        Assert.Equal(h.Proxy.ListenEndPoint, atClient.Value.RemoteEndPoint);

        Assert.Equal(2, h.Counters.Forwarded);
        Assert.Null(h.Proxy.LastError);
    }

    // The first sender is latched as the actor; another local sender can neither inject upstream nor take over the
    // server's replies. ResetClient lets the next sender become the actor (reconnect from a new local port).
    [Fact]
    public async Task OtherLocalSendersAreRefusedUntilReset()
    {
        await using var h = await Harness.CreateAsync();
        using var intruder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        // Several sends: the first datagram(s) of a new flow can be lost on this host (see Harness).
        for (int i = 0; i < 5; i++)
        {
            await intruder.SendAsync(new byte[] { 0x66 }, 1, h.Proxy.ListenEndPoint);
            await Task.Delay(20);
        }
        await WaitUntilAsync(() => h.Counters.Foreign >= 1, ReceiveTimeout);
        Assert.Null(await TryReceiveAsync(h.Server, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(0, h.Counters.Forwarded);
        Assert.Equal(0, h.Counters.Dropped);

        // Replies still go to the real actor.
        await h.SendToClientAsync([5]);
        var reply = await TryReceiveAsync(h.Client, ReceiveTimeout);
        Assert.NotNull(reply);
        Assert.Null(await TryReceiveAsync(intruder, TimeSpan.FromMilliseconds(100)));

        // After ResetClient the next sender is the actor.
        h.Proxy.ResetClient();
        await intruder.SendAsync(new byte[] { 0x67 }, 1, h.Proxy.ListenEndPoint);
        var atServer = await TryReceiveAsync(h.Server, ReceiveTimeout);
        Assert.NotNull(atServer);
        Assert.Equal(new byte[] { 0x67 }, atServer.Value.Buffer);
        long foreignBefore = h.Counters.Foreign;
        await h.SendToServerAsync([1]);
        await WaitUntilAsync(() => h.Counters.Foreign == foreignBefore + 1, ReceiveTimeout);
    }

    [Fact]
    public async Task FullLossDropsEverything()
    {
        await using var h = await Harness.CreateAsync();
        h.Proxy.SetFaults(new NetworkFaultSettings { PacketLossPercent = 100 });

        for (int i = 0; i < 20; i++) await h.SendToServerAsync([(byte)i]);
        await WaitUntilAsync(() => h.Counters.Dropped == 20, ReceiveTimeout);

        Assert.Null(await TryReceiveAsync(h.Server, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(0, h.Counters.Forwarded);
    }

    [Fact]
    public async Task BlockAndUnblock()
    {
        await using var h = await Harness.CreateAsync();

        h.Proxy.SetBlocked(true);
        await h.SendToServerAsync([2]);
        await h.SendToClientAsync([3]);
        await WaitUntilAsync(() => h.Counters.Dropped == 2, ReceiveTimeout);
        Assert.Null(await TryReceiveAsync(h.Server, TimeSpan.FromMilliseconds(150)));
        Assert.Null(await TryReceiveAsync(h.Client, TimeSpan.FromMilliseconds(150)));

        h.Proxy.SetBlocked(false);
        Assert.True(h.Proxy.GetFaults(FaultDirection.ToServer).IsClean);
        await h.SendToServerAsync([4]);
        var after = await TryReceiveAsync(h.Server, ReceiveTimeout);
        Assert.NotNull(after);
        Assert.Equal(new byte[] { 4 }, after.Value.Buffer);
        await h.SendToClientAsync([5]);
        var reply = await TryReceiveAsync(h.Client, ReceiveTimeout);
        Assert.NotNull(reply);
        Assert.Equal(new byte[] { 5 }, reply.Value.Buffer);
    }

    [Fact]
    public async Task BlockOneDirectionOnly()
    {
        await using var h = await Harness.CreateAsync();

        h.Proxy.SetBlocked(true, FaultDirection.ToClient);
        await h.SendToServerAsync([2]);
        Assert.NotNull(await TryReceiveAsync(h.Server, ReceiveTimeout));
        await h.SendToClientAsync([3]);
        Assert.Null(await TryReceiveAsync(h.Client, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, h.Counters.Dropped);
    }

    [Fact]
    public async Task LatencyDelaysDelivery()
    {
        await using var h = await Harness.CreateAsync();
        h.Proxy.SetFaults(new NetworkFaultSettings { LatencyMs = 150 }, FaultDirection.ToServer);

        var clock = Stopwatch.StartNew();
        await h.SendToServerAsync([7]);
        var received = await TryReceiveAsync(h.Server, ReceiveTimeout);
        clock.Stop();

        Assert.NotNull(received);
        // Never early (small tolerance for timer/Stopwatch granularity), and not wildly late.
        Assert.True(clock.ElapsedMilliseconds >= 145, $"arrived after {clock.ElapsedMilliseconds} ms");
        Assert.True(clock.ElapsedMilliseconds < 2000, $"arrived after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(1, h.Counters.Delayed);
    }

    [Fact]
    public async Task DuplicationSendsTwoCopies()
    {
        await using var h = await Harness.CreateAsync();
        h.Proxy.SetFaults(new NetworkFaultSettings { DuplicatePercent = 100 });

        const int count = 10;
        for (int i = 0; i < count; i++) await h.SendToServerAsync([(byte)i]);

        int received = 0;
        while (await TryReceiveAsync(h.Server, TimeSpan.FromMilliseconds(500)) is not null) received++;

        Assert.Equal(count * 2, received);
        Assert.Equal(count, h.Counters.Duplicated);
        Assert.Equal(count * 2, h.Counters.Forwarded);
    }

    [Fact]
    public async Task FullDelayQueueDropsAndCounts()
    {
        const int capacity = 8;
        const int sent = 50;
        await using var h = await Harness.CreateAsync(maxQueued: capacity);
        h.Proxy.SetFaults(new NetworkFaultSettings { LatencyMs = 5000 });

        for (int i = 0; i < sent; i++) await h.SendToServerAsync([(byte)i]);
        await WaitUntilAsync(() => h.Counters.Delayed + h.Counters.QueueFull == sent, ReceiveTimeout);

        Assert.Equal(capacity, h.Counters.Delayed);
        Assert.Equal(sent - capacity, h.Counters.QueueFull);
        Assert.Equal(capacity, h.Proxy.QueuedCount);
    }

    [Fact]
    public async Task BlockDropsAlreadyQueuedDatagrams()
    {
        await using var h = await Harness.CreateAsync();
        h.Proxy.SetFaults(new NetworkFaultSettings { LatencyMs = 200 });
        await h.SendToServerAsync([1]);
        await WaitUntilAsync(() => h.Counters.Delayed == 1, ReceiveTimeout);

        h.Proxy.SetBlocked(true);
        Assert.Null(await TryReceiveAsync(h.Server, TimeSpan.FromMilliseconds(500)));
        Assert.Equal(1, h.Counters.Dropped);
        Assert.Equal(0, h.Proxy.QueuedCount);
    }

    [Fact]
    public async Task DisposeStopsPromptlyWithQueuedDatagrams()
    {
        var h = await Harness.CreateAsync();
        h.Proxy.SetFaults(new NetworkFaultSettings { LatencyMs = 10_000 });
        for (int i = 0; i < 20; i++) await h.SendToServerAsync([(byte)i]);
        await WaitUntilAsync(() => h.Counters.Delayed == 20, ReceiveTimeout);

        var clock = Stopwatch.StartNew();
        await h.DisposeAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"dispose took {clock.ElapsedMilliseconds} ms");
        Assert.Null(h.Proxy.LastError);
        Assert.Equal(0, h.Proxy.QueuedCount);
        await h.Proxy.DisposeAsync();   // idempotent
    }

    [Fact]
    public async Task SeededLossIsDeterministic()
    {
        var first = await ArrivalsWithLossAsync(seed: 42);
        var second = await ArrivalsWithLossAsync(seed: 42);
        var other = await ArrivalsWithLossAsync(seed: 7);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.InRange(first.Count, 1, 99);
    }

    // 기능: 손실 50 %인 Proxy로 0~99 번호 Datagram 100개를 보내고 서버에 도착한 번호를 모은다.
    // 입력: seed - Proxy 난수 시드.
    // 출력: 도착한 Datagram 번호를 오름차순으로 정렬한 목록.
    private static async Task<List<int>> ArrivalsWithLossAsync(int seed)
    {
        const int count = 100;
        await using var h = await Harness.CreateAsync(seed);
        h.Proxy.SetFaults(new NetworkFaultSettings { PacketLossPercent = 50 });

        for (int i = 0; i < count; i++) await h.SendToServerAsync([(byte)i]);
        await WaitUntilAsync(() => h.Counters.Forwarded + h.Counters.Dropped == count, ReceiveTimeout);

        var arrivals = new List<int>();
        while (arrivals.Count < h.Counters.Forwarded)
        {
            var r = await TryReceiveAsync(h.Server, ReceiveTimeout);
            Assert.NotNull(r);
            arrivals.Add(r.Value.Buffer[0]);
        }
        arrivals.Sort();
        return arrivals;
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(NetworkFaultSettings.MaxDelayMs + 1, 0, 0, 0)]
    [InlineData(0, -5, 0, 0)]
    [InlineData(0, 0, 100.5, 0)]
    [InlineData(0, 0, double.NaN, 0)]
    [InlineData(0, 0, 0, -1)]
    public async Task InvalidSettingsAreRejectedAndKeepTheOldOnes(int latency, int jitter, double loss, double duplicate)
    {
        await using var proxy = new UdpFaultProxy(new IPEndPoint(IPAddress.Loopback, 9), seed: 1);
        var bad = new NetworkFaultSettings { LatencyMs = latency, JitterMs = jitter, PacketLossPercent = loss, DuplicatePercent = duplicate };

        Assert.Throws<ArgumentOutOfRangeException>(() => proxy.SetFaults(bad));
        Assert.Same(NetworkFaultSettings.None, proxy.GetFaults(FaultDirection.ToServer));
        Assert.Same(NetworkFaultSettings.None, proxy.GetFaults(FaultDirection.ToClient));
    }

    [Fact]
    public void ParseTargetAcceptsLocalhostAndIpOnly()
    {
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 7777), UdpFaultProxy.ParseTarget("localhost", 7777));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1), UdpFaultProxy.ParseTarget("10.0.0.2", 1));
        Assert.Throws<ArgumentException>(() => UdpFaultProxy.ParseTarget("example.com", 7777));
        Assert.Throws<ArgumentOutOfRangeException>(() => UdpFaultProxy.ParseTarget("localhost", 0));
    }
}
