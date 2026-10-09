using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using LiteNetLib;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Net;
using ProjectH.Shared.Protocol;
using Xunit;
using ClientLayer = ProjectH.Bots.AuthPacketLayer;

namespace ProjectH.Server.Tests.Net;

// Review fix B3: the server's datagram authentication layer (keys per endpoint, sealed and opened with the Shared rule) and
// the bots' client layer that follows the Unity client's rule.
public class AuthPacketLayerTests
{
    private static readonly IPEndPoint Alice = new(IPAddress.Parse("10.0.0.1"), 5000);
    private static readonly IPEndPoint Bob = new(IPAddress.Parse("10.0.0.2"), 5000);

    // 기능: seed로 정해지는 세션 키 바이트를 만든다(같은 seed면 같은 키).
    // 입력: seed - 키를 정하는 값.
    // 출력: SessionKeyBytes 길이의 키.
    private static byte[] Key(byte seed)
    {
        var key = new byte[ProtocolLimits.SessionKeyBytes];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed * 7 + i);
        return key;
    }

    // 기능: 보낼 데이터그램 버퍼를 만든다(꼬리 자리 포함).
    // 입력: n - 페이로드 길이.
    // 출력: n + AuthTagBytes 크기 버퍼.
    private static byte[] Payload(int n)
    {
        var data = new byte[n + ProtocolLimits.AuthTagBytes];
        for (int i = 0; i < n; i++) data[i] = (byte)(i + 1);
        return data;
    }

    // 기능: 계층 하나로 보낸 뒤 다른 계층으로 받는다.
    // 입력: from - 보내는 계층, to - 받는 계층, fromSees·toSees - 각 계층이 보는 상대 주소, n - 페이로드 길이,
    //   header - 첫 바이트(LiteNetLib 헤더, null = Payload의 1 = Channeled).
    // 출력: 받은 뒤의 길이(0 = 버림). 보낸 뒤 길이가 n + AuthTagBytes가 아니면 테스트가 실패한다.
    private static int Send(PacketLayerBaseAdapter from, PacketLayerBaseAdapter to, IPEndPoint fromSees, IPEndPoint toSees, int n = 40,
        byte? header = null)
    {
        byte[] data = Payload(n);
        if (header.HasValue) data[0] = header.Value;
        int offset = 0, length = n;
        from.Out(fromSees, ref data, ref offset, ref length);
        Assert.Equal(n + ProtocolLimits.AuthTagBytes, length);
        to.In(toSees, ref data, ref length);
        return length;
    }

    // Thin wrapper so both layer types are driven the same way (their callbacks take refs).
    private sealed class PacketLayerBaseAdapter
    {
        private readonly LiteNetLib.Layers.PacketLayerBase _layer;
        // 기능: 감쌀 패킷 계층을 받아 둔다.
        // 입력: layer - 서버 또는 Client 인증 계층.
        // 출력: 그 계층의 콜백을 부르는 Adapter.
        public PacketLayerBaseAdapter(LiteNetLib.Layers.PacketLayerBase layer) => _layer = layer;

        // 기능: 송신 콜백을 부른다.
        // 입력: endPoint·data·offset·length - 콜백 인자.
        // 출력: 반환값 없음. 계층이 data에 꼬리를 붙이고 length를 늘린다.
        public void Out(IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            IPEndPoint ep = endPoint;
            _layer.ProcessOutBoundPacket(ref ep, ref data, ref offset, ref length);
        }

        // 기능: 수신 콜백을 부른다.
        // 입력: endPoint·data·length - 콜백 인자.
        // 출력: 반환값 없음. 계층이 꼬리를 검사해 length를 줄이거나(통과) 0으로 만든다(버림).
        public void In(IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            IPEndPoint ep = endPoint;
            _layer.ProcessInboundPacket(ref ep, ref data, ref length);
        }
    }

    // 기능: 새 HealthCounters를 단 서버 인증 계층을 만든다(퇴역 키 보관 6초).
    // 입력: maxRetired - 보관할 퇴역 키 수의 상한.
    // 출력: 서버 계층과 그 계층이 세는 HealthCounters.
    private static (AuthPacketLayer Server, HealthCounters Health) Server(int maxRetired = 8)
    {
        var health = new HealthCounters();
        return (new AuthPacketLayer(health, retireMs: 6000, maxRetired), health);
    }

    [Fact]
    public void ASignedDatagram_RoundTrips_BetweenTheServerAndAClient()
    {
        var (server, health) = Server();
        var client = new ClientLayer();
        server.Register(Alice, new SessionKeys(Key(1), isServer: true), 0);
        client.Use(new SessionKeys(Key(1), isServer: false));
        var s = new PacketLayerBaseAdapter(server);
        var c = new PacketLayerBaseAdapter(client);

        Assert.Equal(40, Send(c, s, Alice, Alice));
        Assert.Equal(40, Send(s, c, Alice, Alice));
        Assert.True(client.Verified);
        Assert.Equal(0, health.AuthDrops);
        Assert.Equal(0, client.AuthDrops);
    }

    [Fact]
    public void AnUnsignedDatagram_ToASignedEndpoint_IsDroppedAndCounted()
    {
        var (server, health) = Server();
        server.Register(Alice, new SessionKeys(Key(2), isServer: true), 0);
        var forger = new ClientLayer();   // no keys: a zero tail
        Assert.Equal(0, Send(new PacketLayerBaseAdapter(forger), new PacketLayerBaseAdapter(server), Alice, Alice));
        Assert.Equal(1, health.AuthDrops);

        var wrongKey = new ClientLayer();
        wrongKey.Use(new SessionKeys(Key(3), isServer: false));
        Assert.Equal(0, Send(new PacketLayerBaseAdapter(wrongKey), new PacketLayerBaseAdapter(server), Alice, Alice));
        Assert.Equal(2, health.AuthDrops);
    }

    [Fact]
    public void AnUnknownEndpoint_IsStrippedUnverified_AndGetsAZeroTail()
    {
        var (server, health) = Server();
        var client = new ClientLayer();
        client.Use(new SessionKeys(Key(4), isServer: false));   // the connect request: sealed, the server has no keys yet
        var s = new PacketLayerBaseAdapter(server);
        var c = new PacketLayerBaseAdapter(client);
        Assert.Equal(40, Send(c, s, Bob, Bob));

        // The cookie answer: the server has no keys, so a zero tail; the client is not verified yet and strips it.
        Assert.Equal(16, Send(s, c, Bob, Bob, n: 16));
        Assert.False(client.Verified);
        Assert.Equal(0, health.AuthDrops);
        int tiny = ProtocolLimits.AuthTagBytes - 1;
        byte[] data = new byte[tiny];
        s.In(Bob, ref data, ref tiny);
        Assert.Equal(0, tiny);
    }

    // After the first datagram that opens, the client drops one that does not (a forged one from the server's address).
    [Fact]
    public void AVerifiedClient_DropsAnUnsignedDatagram()
    {
        var (server, _) = Server();
        var client = new ClientLayer();
        server.Register(Alice, new SessionKeys(Key(5), isServer: true), 0);
        client.Use(new SessionKeys(Key(5), isServer: false));
        var s = new PacketLayerBaseAdapter(server);
        var c = new PacketLayerBaseAdapter(client);
        Assert.Equal(40, Send(s, c, Alice, Alice));
        var forger = new PacketLayerBaseAdapter(new ClientLayer());
        Assert.Equal(0, Send(forger, c, Alice, Alice));
        Assert.Equal(1, client.AuthDrops);
    }

    // A retired entry keeps sealing (the disconnect resends) and opening; a ConnectRequest that does not open on it is taken as
    // a new connection from the same endpoint and stripped unverified. Review B round 1: any other datagram that does not open
    // on it (a forged ShutdownOk, for one) is dropped and counted (review B round 2: as authDropsRetired, apart from authDrops).
    [Fact]
    public void ARetiredEntry_KeepsSealing_AndLetsANewConnectionFromTheSameEndpointIn()
    {
        var (server, health) = Server();
        var keys = new SessionKeys(Key(6), isServer: true);
        server.Register(Alice, keys, 0);
        server.Retire(Alice, keys, 0);
        var old = new ClientLayer();
        old.Use(new SessionKeys(Key(6), isServer: false));
        var s = new PacketLayerBaseAdapter(server);
        var o = new PacketLayerBaseAdapter(old);
        Assert.Equal(40, Send(s, o, Alice, Alice));   // still sealed
        Assert.True(old.Verified);

        var fresh = new ClientLayer();
        fresh.Use(new SessionKeys(Key(7), isServer: false));
        var f = new PacketLayerBaseAdapter(fresh);
        // The property bits are the low 5; the connection number and the fragment flag above them do not matter.
        Assert.Equal(40, Send(f, s, Alice, Alice, header: AuthPacketLayer.ConnectRequestProperty));
        Assert.Equal(40, Send(f, s, Alice, Alice, header: (byte)(AuthPacketLayer.ConnectRequestProperty | 0x60)));
        Assert.Equal(0, health.AuthDropsRetired);
        Assert.Equal(0, Send(f, s, Alice, Alice, header: 14));   // ShutdownOk
        Assert.Equal(0, Send(f, s, Alice, Alice));                // Channeled
        Assert.Equal(0, Send(new PacketLayerBaseAdapter(new ClientLayer()), s, Alice, Alice, header: 8));   // an unsigned Disconnect
        Assert.Equal(3, health.AuthDropsRetired);   // review B round 2: counted apart from the live-key drops
        Assert.Equal(0, health.AuthDrops);
        Assert.Equal(40, Send(o, s, Alice, Alice, header: 14));   // the old client's own ShutdownOk still opens

        // Retiring with another endpoint's keys, or keys that were replaced, changes nothing.
        server.Register(Alice, new SessionKeys(Key(8), isServer: true), 1);
        server.Retire(Alice, keys, 1);
        var live = new ClientLayer();
        live.Use(new SessionKeys(Key(9), isServer: false));
        Assert.Equal(0, Send(new PacketLayerBaseAdapter(live), s, Alice, Alice, header: AuthPacketLayer.ConnectRequestProperty));   // the live entry drops a stranger
        Assert.Equal(1, health.AuthDrops);
        Assert.Equal(3, health.AuthDropsRetired);
    }

    // Review B round 1: the ConnectRequest property the retired entry lets through is LiteNetLib's, not remembered. The first
    // datagram a fresh client sends is its ConnectRequest; the server layer sees it first, and its low 5 bits are the constant
    // (LiteNetLib 2.1.4 reads NetPacket.Property as data[0] & 0x1F). Cross-checked with LiteNetLib's internal enum.
    [Fact]
    public void TheConnectRequestProperty_MatchesACapturedConnectRequest()
    {
        var health = new HealthCounters();
        var recorder = new FirstByteRecorder(new AuthPacketLayer(health, 6000, 8));
        var serverEvents = new EventBasedNetListener();
        var server = new NetManager(serverEvents, recorder) { MtuOverride = ProtocolLimits.UserMtu };
        bool requested = false;
        serverEvents.ConnectionRequestEvent += r => { requested = true; r.Reject(); };
        server.Start(0);
        var clientLayer = new ClientLayer();
        var client = new NetManager(new EventBasedNetListener(), clientLayer) { MtuOverride = ProtocolLimits.UserMtu };
        client.Start();
        try
        {
            clientLayer.Use(new SessionKeys(Key(14), isServer: false));
            client.Connect("127.0.0.1", server.LocalPort, "x");
            var clock = Stopwatch.StartNew();
            while (!requested && clock.ElapsedMilliseconds < 3000)
            {
                server.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }
            Assert.True(requested, "connection request");
            Assert.True(recorder.Inbound.TryPeek(out byte first));
            Assert.Equal((int)AuthPacketLayer.ConnectRequestProperty, first & AuthPacketLayer.PropertyMask);

            Type? property = typeof(NetManager).Assembly.GetType("LiteNetLib.PacketProperty");
            Assert.NotNull(property);
            Assert.Equal((int)AuthPacketLayer.ConnectRequestProperty, Convert.ToInt32(Enum.Parse(property!, "ConnectRequest")));
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }

    // Records the first byte of every datagram the server receives, then hands the datagram to the wrapped layer.
    private sealed class FirstByteRecorder : LiteNetLib.Layers.PacketLayerBase
    {
        private readonly LiteNetLib.Layers.PacketLayerBase _inner;
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte> Inbound = new();

        // 기능: 감쌀 계층을 받아 두고 꼬리 크기를 AuthTagBytes로 신고한다.
        // 입력: inner - 실제 처리를 맡길 계층.
        // 출력: 수신 첫 바이트를 Inbound에 쌓는 Recorder.
        public FirstByteRecorder(LiteNetLib.Layers.PacketLayerBase inner) : base(ProtocolLimits.AuthTagBytes) => _inner = inner;

        // 기능: 첫 바이트를 기록하고 감싼 계층의 수신 처리를 부른다.
        // 입력: endPoint·data·length - 콜백 인자.
        // 출력: 반환값 없음. 감싼 계층이 length를 바꾼다.
        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (length > 0) Inbound.Enqueue(data[0]);
            _inner.ProcessInboundPacket(ref endPoint, ref data, ref length);
        }

        // 기능: 감싼 계층의 송신 처리를 그대로 부른다.
        // 입력: endPoint·data·offset·length - 콜백 인자.
        // 출력: 반환값 없음. 감싼 계층이 꼬리를 붙인다.
        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length) =>
            _inner.ProcessOutBoundPacket(ref endPoint, ref data, ref offset, ref length);
    }

    // Retired entries are removed once their time is up (at the next Register) and never pile up: 1,000 connects and
    // disconnects leave at most the live connections + MaxRetired entries.
    [Fact]
    public void AThousandConnectsAndDisconnects_LeaveABoundedTable()
    {
        const int maxRetired = 8;
        var (server, _) = Server(maxRetired);
        for (int i = 0; i < 1000; i++)
        {
            var ep = new IPEndPoint(IPAddress.Parse("10.1.0.1"), 10_000 + i);
            var keys = new SessionKeys(Key((byte)i), isServer: true);
            server.Register(ep, keys, i);
            server.Retire(ep, keys, i);
        }
        Assert.True(server.Count <= 1 + maxRetired, $"{server.Count} entries");

        // Past the retire time, the next Register sweeps them all.
        server.Register(Alice, new SessionKeys(Key(1), isServer: true), 1000 + 6000);
        Assert.Equal(1, server.Count);
    }

    // Review B round 1: every accepted connection retires its keys once, and the global accept bucket lets in its burst plus
    // AcceptsPerSecond for every second of the retire window, so the cap holds them all and none is evicted before its time.
    [Theory]
    [InlineData(16, 20, 5000, 152)]    // the defaults: 16 + 16 + 20 * ceil(6000 / 1000)
    [InlineData(100, 20, 5000, 320)]   // 100 players: 100 + 100 + 20 * 6
    [InlineData(16, 20, 1500, 92)]     // a retire window of 2.5 s counts 3 seconds: 16 + 16 + 20 * 3
    public void TheRetiredKeyCap_HoldsEveryAcceptWithinTheRetireWindow(int maxPlayers, int acceptsPerSecond, int disconnectTimeoutMs, int cap)
    {
        var options = new ServerOptions
        {
            MaxPlayers = maxPlayers, AcceptsPerSecond = acceptsPerSecond, DisconnectTimeoutMs = disconnectTimeoutMs,
        };
        Assert.Null(options.Validate());
        Assert.Equal(disconnectTimeoutMs + 1000L, options.AuthKeyRetireMs);
        Assert.Equal(cap, options.MaxRetiredAuthKeys);
    }

    [Fact]
    public void ANewRegister_ForTheSameEndpoint_ReplacesTheEntry()
    {
        var (server, health) = Server();
        server.Register(Alice, new SessionKeys(Key(10), isServer: true), 0);
        server.Register(Alice, new SessionKeys(Key(11), isServer: true), 0);
        Assert.Equal(1, server.Count);
        var client = new ClientLayer();
        client.Use(new SessionKeys(Key(11), isServer: false));
        Assert.Equal(40, Send(new PacketLayerBaseAdapter(client), new PacketLayerBaseAdapter(server), Alice, Alice));
        Assert.Equal(0, health.AuthDrops);
    }

    [Fact]
    public void BothCallbacks_AllocateNothing()
    {
        var (server, _) = Server();
        server.Register(Alice, new SessionKeys(Key(12), isServer: true), 0);
        var client = new ClientLayer();
        client.Use(new SessionKeys(Key(12), isServer: false));
        IPEndPoint ep = Alice;
        byte[] data = Payload(100);
        // 기능: 같은 버퍼로 Client → 서버, 서버 → Client 한 바퀴를 보내고 받는다.
        // 입력: 없음(바깥의 client·server·ep·data를 쓴다).
        // 출력: 반환값 없음. data가 봉인·개봉을 두 번 거친다.
        void RoundTrip()
        {
            int offset = 0, length = 100;
            client.ProcessOutBoundPacket(ref ep, ref data, ref offset, ref length);
            server.ProcessInboundPacket(ref ep, ref data, ref length);
            offset = 0;
            server.ProcessOutBoundPacket(ref ep, ref data, ref offset, ref length);
            client.ProcessInboundPacket(ref ep, ref data, ref length);
        }
        RoundTrip();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) RoundTrip();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // With the layers on real NetManagers at the user MTU, a Sequenced packet still holds MaxPacketSize, and the largest one
    // plus its header and the tail stays within the wire MTU (1232).
    [Fact]
    public void AtTheUserMtu_ASnapshotFits_AndTheWireDatagramStaysWithinTheMtu()
    {
        var health = new HealthCounters();
        var serverLayer = new AuthPacketLayer(health, 6000, 8);
        var serverEvents = new EventBasedNetListener();
        var server = new NetManager(serverEvents, serverLayer) { MtuOverride = ProtocolLimits.UserMtu };
        NetPeer? accepted = null;
        serverEvents.ConnectionRequestEvent += r =>
        {
            serverLayer.Register(r.RemoteEndPoint, new SessionKeys(Key(13), isServer: true), Environment.TickCount64);
            accepted = r.Accept();
        };
        server.Start(0);
        var clientLayer = new ClientLayer();
        var client = new NetManager(new EventBasedNetListener(), clientLayer) { MtuOverride = ProtocolLimits.UserMtu };
        client.Start();
        try
        {
            clientLayer.Use(new SessionKeys(Key(13), isServer: false));
            client.Connect("127.0.0.1", server.LocalPort, "x");
            var clock = Stopwatch.StartNew();
            while ((accepted == null || client.FirstPeer?.ConnectionState != ConnectionState.Connected) && clock.ElapsedMilliseconds < 3000)
            {
                server.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }
            Assert.NotNull(accepted);
            int max = accepted!.GetMaxSinglePacketSize(DeliveryMethod.Sequenced);
            Assert.True(max >= ProtocolConstants.MaxPacketSize, $"max {max}");
            Assert.True(max + ProtocolLimits.TransportHeaderBytes + ProtocolLimits.AuthTagBytes <= ProtocolConstants.Mtu, $"max {max}");
            Assert.True(clientLayer.Verified);
            Assert.Equal(0, health.AuthDrops);
        }
        finally
        {
            client.Stop();
            server.Stop();
        }
    }
}
