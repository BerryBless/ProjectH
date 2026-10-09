using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ProjectH.QA.Faults;

// Which way a fault applies. ToServer = actor -> game server, ToClient = game server -> actor.
public enum FaultDirection
{
    Both,
    ToServer,
    ToClient,
}

// Network faults for one direction (D13, request §71). Immutable: the proxy swaps whole objects, so a receive loop
// always sees one consistent set of values without a lock. Values come from scenario JSON later, so Validate() rejects
// anything outside the ranges below instead of clamping silently.
public sealed record NetworkFaultSettings
{
    public const int MaxDelayMs = 10_000;

    public static readonly NetworkFaultSettings None = new();

    public int LatencyMs { get; init; }
    // Each datagram gets LatencyMs + uniform(-JitterMs, +JitterMs), never below 0. Jitter reorders datagrams like a
    // real network would; LiteNetLib's sequencing handles that.
    public int JitterMs { get; init; }
    public double PacketLossPercent { get; init; }
    // Chance that a forwarded datagram is sent twice (both copies get the same delay).
    public double DuplicatePercent { get; init; }
    // Drop every datagram, including ones already queued for delayed delivery.
    public bool Blocked { get; init; }

    public bool IsClean => !Blocked && LatencyMs == 0 && JitterMs == 0 && PacketLossPercent == 0 && DuplicatePercent == 0;

    // 기능: 지연·지터가 0..MaxDelayMs, 손실·중복 확률이 0..100 안에 있는지 검사한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 범위를 벗어난 값이 있으면 ArgumentOutOfRangeException.
    public void Validate()
    {
        if (LatencyMs < 0 || LatencyMs > MaxDelayMs)
            throw new ArgumentOutOfRangeException(nameof(LatencyMs), LatencyMs, $"must be 0..{MaxDelayMs}");
        if (JitterMs < 0 || JitterMs > MaxDelayMs)
            throw new ArgumentOutOfRangeException(nameof(JitterMs), JitterMs, $"must be 0..{MaxDelayMs}");
        if (!IsPercent(PacketLossPercent))
            throw new ArgumentOutOfRangeException(nameof(PacketLossPercent), PacketLossPercent, "must be 0..100");
        if (!IsPercent(DuplicatePercent))
            throw new ArgumentOutOfRangeException(nameof(DuplicatePercent), DuplicatePercent, "must be 0..100");
    }

    // 기능: 값이 유효한 백분율인지 검사한다.
    // 입력: value - 검사할 값.
    // 출력: NaN이 아니고 0..100이면 true.
    private static bool IsPercent(double value) => !double.IsNaN(value) && value >= 0 && value <= 100;
}

// Foreign = datagrams refused because they came from a local endpoint other than the latched actor (not in Dropped).
public readonly record struct UdpFaultProxyCounters(long Forwarded, long Dropped, long Duplicated, long Delayed, long QueueFull, long Foreign);

// QA-3 network fault injection (D13): actor -> proxy -> game server, so the server's network code stays untouched
// (request §72). One proxy per actor: LiteNetLib connects to ListenEndPoint, the proxy forwards from its own upstream
// socket to the target, and replies go back to the single client endpoint it last heard from.
//
// Threads: three loops on the thread pool, all owned by this object.
//   - client receive loop: client socket -> ToServer faults -> send now or enqueue
//   - upstream receive loop: upstream socket -> ToClient faults -> send now or enqueue
//   - delivery loop: the only consumer of the delay queue; sends datagrams when they are due
// Each receive loop owns its own seeded Random (Random is not thread-safe). It draws nothing while its direction is
// clean and exactly three values per datagram while any fault is set, so the same seed and the same faulted datagram
// sequence give the same decisions (§14).
//
// Bounds: the delay queue holds at most maxQueuedDatagrams (default 4096); a datagram that does not fit is dropped and
// counted as QueueFull. Queued copies use pooled arrays sized to the datagram (<= MaxDatagramSize), so the queue holds
// at most maxQueued * 2 KB. Entries leave the queue when they are delivered or dropped, or at dispose.
//
// Lifetime: the constructor binds both sockets and starts the loops. DisposeAsync cancels, closes both sockets, awaits
// the loops (ShutdownTimeout) and returns queued buffers. Owner: whoever created it (later the actor wiring).
//
// Note for reconnect scenarios: the server sees every connection through this proxy from the same upstream endpoint,
// so a reconnect through the same proxy arrives from the same address as the old peer. Use a new proxy per connection
// attempt if the scenario needs a fresh server-side endpoint.
public sealed class UdpFaultProxy : IAsyncDisposable
{
    public const int DefaultMaxQueuedDatagrams = 4096;
    // Larger than ProtocolConstants.Mtu (1232): LiteNetLib MTU probes or a peer without MtuOverride may send more, and
    // on Windows a datagram larger than the buffer throws MessageSize instead of arriving truncated.
    public const int MaxDatagramSize = 2048;
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    // SIO_UDP_CONNRESET: without this, Windows reports an ICMP port-unreachable from an earlier send as a
    // ConnectionReset on the next receive.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly Socket _clientSide;
    private readonly Socket _upstream;
    private readonly IPEndPoint _target;
    private readonly int _maxQueued;
    private readonly Random _toServerRandom;
    private readonly Random _toClientRandom;
    private readonly CancellationTokenSource _cts = new();
    // Wakes the delivery loop when a datagram is queued (it may be due earlier than the current head).
    private readonly SemaphoreSlim _wake = new(0);

    // Lock: guards _queue and _sequence only. It is the only lock in this class, never nested, and nothing inside it
    // does I/O, awaits or calls out (only PriorityQueue operations), so it cannot deadlock.
    private readonly object _queueGate = new();
    private readonly PriorityQueue<Pending, (long Due, long Sequence)> _queue = new();
    private long _sequence;

    // The actor endpoint, latched by the first datagram (CAS from null); read by the upstream loop. ResetClient clears it.
    private IPEndPoint? _client;
    private FaultPair _faults = new(NetworkFaultSettings.None, NetworkFaultSettings.None);   // Volatile / CAS
    private string? _lastError;

    private long _forwarded;
    private long _dropped;
    private long _duplicated;
    private long _delayed;
    private long _queueFull;
    private long _foreign;

    private readonly Task _clientLoop;
    private readonly Task _upstreamLoop;
    private readonly Task _deliveryLoop;
    private int _disposed;

    // 기능: 클라이언트 쪽(127.0.0.1 임시 포트)과 업스트림 소켓을 바인딩하고 수신 루프 2개와 지연 전달 루프를 시작한다.
    // 입력: target - 게임 서버 endpoint, seed - 양방향 난수 시드의 바탕, maxQueuedDatagrams - 지연 큐 상한.
    // 출력: 장애 없이 전달 중인 프록시. 포트·상한이 잘못되면 ArgumentOutOfRangeException, 바인딩 실패면 소켓을 정리하고 예외 전파.
    public UdpFaultProxy(IPEndPoint target, int seed, int maxQueuedDatagrams = DefaultMaxQueuedDatagrams)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(target), "target port must be 1..65535");
        if (maxQueuedDatagrams < 1) throw new ArgumentOutOfRangeException(nameof(maxQueuedDatagrams));

        _target = target;
        _maxQueued = maxQueuedDatagrams;
        _toServerRandom = new Random(seed);
        _toClientRandom = new Random(unchecked(seed * 31 + 17));

        Socket? clientSide = null;
        Socket? upstream = null;
        try
        {
            clientSide = CreateSocket(AddressFamily.InterNetwork);
            clientSide.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            upstream = CreateSocket(target.AddressFamily);
            upstream.Bind(new IPEndPoint(LocalAddressFor(target.Address), 0));
        }
        catch
        {
            clientSide?.Dispose();
            upstream?.Dispose();
            _cts.Dispose();
            _wake.Dispose();
            throw;
        }

        _clientSide = clientSide;
        _upstream = upstream;
        ListenEndPoint = (IPEndPoint)_clientSide.LocalEndPoint!;

        _clientLoop = Task.Run(ClientReceiveLoopAsync);
        _upstreamLoop = Task.Run(UpstreamReceiveLoopAsync);
        _deliveryLoop = Task.Run(DeliveryLoopAsync);
    }

    // The endpoint the actor's LiteNetLib client connects to (127.0.0.1, ephemeral port).
    public IPEndPoint ListenEndPoint { get; }

    public IPEndPoint Target => _target;

    // Set when a receive loop stopped on an unexpected socket error (the proxy then no longer forwards that way).
    public string? LastError => Volatile.Read(ref _lastError);

    public UdpFaultProxyCounters Counters => new(
        Interlocked.Read(ref _forwarded),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _duplicated),
        Interlocked.Read(ref _delayed),
        Interlocked.Read(ref _queueFull),
        Interlocked.Read(ref _foreign));

    public int QueuedCount
    {
        get { lock (_queueGate) return _queue.Count; }
    }

    // 기능: 현재 적용 중인 한 방향의 장애 설정을 읽는다(락 없이 일관된 한 객체).
    // 입력: direction - ToClient면 서버→액터 설정, 그 외는 액터→서버 설정.
    // 출력: 해당 방향의 장애 설정.
    public NetworkFaultSettings GetFaults(FaultDirection direction)
    {
        var pair = Volatile.Read(ref _faults);
        return direction == FaultDirection.ToClient ? pair.ToClient : pair.ToServer;
    }

    // 기능: 지정 방향의 장애 설정을 CAS로 교체한다. 다음 datagram부터 적용되고, Blocked는 이미 큐에 있는 것에도 적용된다.
    // 입력: settings - 새 장애 설정, direction - 적용 방향(Both면 양쪽 모두 같은 설정).
    // 출력: 반환값 없음. 값이 범위 밖이면 ArgumentOutOfRangeException이 나고 설정은 그대로다.
    // Thread-safe; takes effect for the next datagram (Blocked also for queued ones). Throws ArgumentOutOfRangeException
    // for invalid values, leaving the current settings unchanged.
    public void SetFaults(NetworkFaultSettings settings, FaultDirection direction = FaultDirection.Both)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        while (true)
        {
            var current = Volatile.Read(ref _faults);
            var next = direction switch
            {
                FaultDirection.ToServer => current with { ToServer = settings },
                FaultDirection.ToClient => current with { ToClient = settings },
                _ => new FaultPair(settings, settings),
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _faults, next, current), current)) return;
        }
    }

    // 기능: 양방향 장애 설정을 모두 없앤다.
    // 입력: 없음.
    // 출력: 반환값 없음. 양방향이 None이 된다.
    public void ClearFaults() => SetFaults(NetworkFaultSettings.None);

    // 기능: 고정된 액터 endpoint를 잊어 다음에 보내오는 쪽이 새 액터가 되게 한다(새 로컬 포트로 재접속하는 액터용).
    // 입력: 없음.
    // 출력: 반환값 없음. 다음 datagram이 올 때까지 서버 응답은 버려진다.
    // Forget the latched actor endpoint so the next sender becomes the actor (an actor that reconnects from a new
    // local port). Replies from the server are dropped until that next datagram arrives.
    public void ResetClient() => Volatile.Write(ref _client, null);

    // 기능: 다른 장애 값은 유지한 채 지정 방향의 차단(Blocked)만 켜거나 끈다.
    // 입력: blocked - 차단 여부, direction - 적용 방향.
    // 출력: 반환값 없음. 해당 방향의 Blocked가 바뀐다.
    public void SetBlocked(bool blocked, FaultDirection direction = FaultDirection.Both)
    {
        // Read-modify-write per direction; a concurrent SetFaults may win the race, which is acceptable for QA steps
        // that run from a single orchestrator flow.
        if (direction != FaultDirection.ToClient)
            SetFaults(GetFaults(FaultDirection.ToServer) with { Blocked = blocked }, FaultDirection.ToServer);
        if (direction != FaultDirection.ToServer)
            SetFaults(GetFaults(FaultDirection.ToClient) with { Blocked = blocked }, FaultDirection.ToClient);
    }

    // 기능: 호스트 문자열과 포트를 DNS 조회 없이 endpoint로 만든다.
    // 입력: host - "localhost" 또는 IP 리터럴, port - 1..65535.
    // 출력: 게임 서버 endpoint. 호스트가 그 외 이름이면 ArgumentException, 포트가 범위 밖이면 ArgumentOutOfRangeException.
    // "localhost" and IP literals only: QA servers run locally, and this avoids a blocking DNS lookup.
    public static IPEndPoint ParseTarget(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "must be 1..65535");
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return new IPEndPoint(IPAddress.Loopback, port);
        if (IPAddress.TryParse(host, out var address)) return new IPEndPoint(address, port);
        throw new ArgumentException($"host must be 'localhost' or an IP address: {host}", nameof(host));
    }

    // 기능: 클라이언트 쪽 소켓을 수신해 첫 송신자를 액터로 고정하고, 다른 로컬 송신자는 거부(Foreign)하며 액터의 datagram을 ToServer 장애를 거쳐 서버로 보낸다.
    // 입력: 없음.
    // 출력: 반환값 없음. 취소·폐기로 끝나면 조용히, 예상 밖 오류로 멈추면 LastError에 기록된다.
    private async Task ClientReceiveLoopAsync()
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint anyRemote = new IPEndPoint(IPAddress.Any, 0);
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await _clientSide.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote, ct).ConfigureAwait(false);
                }
                catch (SocketException e) when (!ct.IsCancellationRequested && IsTransient(e))
                {
                    if (e.SocketErrorCode == SocketError.MessageSize) Interlocked.Increment(ref _dropped);
                    continue;
                }

                var from = (IPEndPoint)received.RemoteEndPoint;
                // The first sender is latched as this proxy's actor; any other local sender is refused so it can
                // neither inject traffic upstream nor redirect the server's replies to itself.
                var client = Interlocked.CompareExchange(ref _client, from, null) ?? from;
                if (!client.Equals(from))
                {
                    Interlocked.Increment(ref _foreign);
                    continue;
                }
                Handle(buffer.AsSpan(0, received.ReceivedBytes), _target, FaultDirection.ToServer, _toServerRandom);
            }
        }
        catch (Exception e) when (IsShutdown(e, ct))
        {
        }
        catch (Exception e)
        {
            Volatile.Write(ref _lastError, $"client receive loop stopped: {e.GetType().Name}: {e.Message}");
        }
    }

    // 기능: 업스트림 소켓을 수신해 게임 서버에서 온 datagram만 ToClient 장애를 거쳐 고정된 액터에게 보낸다. 액터가 아직 없으면 버린다(Dropped).
    // 입력: 없음.
    // 출력: 반환값 없음. 취소·폐기로 끝나면 조용히, 예상 밖 오류로 멈추면 LastError에 기록된다.
    private async Task UpstreamReceiveLoopAsync()
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint anyRemote = new IPEndPoint(_target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await _upstream.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote, ct).ConfigureAwait(false);
                }
                catch (SocketException e) when (!ct.IsCancellationRequested && IsTransient(e))
                {
                    if (e.SocketErrorCode == SocketError.MessageSize) Interlocked.Increment(ref _dropped);
                    continue;
                }

                // Only the game server may talk to the actor through this socket.
                if (!_target.Equals(received.RemoteEndPoint)) continue;

                var client = Volatile.Read(ref _client);
                if (client is null)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }
                Handle(buffer.AsSpan(0, received.ReceivedBytes), client, FaultDirection.ToClient, _toClientRandom);
            }
        }
        catch (Exception e) when (IsShutdown(e, ct))
        {
        }
        catch (Exception e)
        {
            Volatile.Write(ref _lastError, $"upstream receive loop stopped: {e.GetType().Name}: {e.Message}");
        }
    }

    // 기능: datagram 하나에 방향의 장애(차단·손실·중복·지연+지터)를 적용해 즉시 보내거나 지연 큐에 넣거나 버린다. 장애가 없으면 난수를 쓰지 않고, 있으면 정확히 3번 뽑는다.
    // 입력: datagram - 수신한 바이트, destination - 보낼 endpoint, direction - 적용할 장애 방향, random - 그 수신 루프 소유의 난수.
    // 출력: 반환값 없음. 카운터(Forwarded/Dropped/Duplicated/Delayed)가 갱신된다.
    // Runs on the receive loop that owns `random`.
    private void Handle(ReadOnlySpan<byte> datagram, IPEndPoint destination, FaultDirection direction, Random random)
    {
        var settings = GetFaults(direction);
        if (settings.IsClean)
        {
            // No draws while clean: how much clean traffic came before the faults (connect retries and so on)
            // must not shift the random sequence the faulted datagrams see.
            SendNow(datagram, destination, direction);
            return;
        }

        // Exactly three draws per faulted datagram, whatever the fault values are, so the same seed and the same
        // datagram sequence give the same decisions.
        double lossRoll = random.NextDouble() * 100;
        double duplicateRoll = random.NextDouble() * 100;
        double jitterRoll = random.NextDouble();

        if (settings.Blocked || lossRoll < settings.PacketLossPercent)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        int copies = 1;
        if (duplicateRoll < settings.DuplicatePercent)
        {
            copies = 2;
            Interlocked.Increment(ref _duplicated);
        }

        int delayMs = settings.LatencyMs;
        if (settings.JitterMs > 0) delayMs += (int)Math.Round((jitterRoll * 2 - 1) * settings.JitterMs);
        if (delayMs < 0) delayMs = 0;

        for (int i = 0; i < copies; i++)
        {
            if (delayMs == 0) SendNow(datagram, destination, direction);
            else Enqueue(datagram, destination, direction, delayMs);
        }
    }

    // 기능: datagram을 풀 배열에 복사해 만기 시각 순 지연 큐에 넣고 전달 루프를 깨운다. 큐가 가득 차면 버린다(QueueFull).
    // 입력: datagram - 보낼 바이트, destination - 보낼 endpoint, direction - 방향, delayMs - 지연 시간(ms).
    // 출력: 반환값 없음. 큐와 Delayed/QueueFull 카운터가 갱신된다.
    private void Enqueue(ReadOnlySpan<byte> datagram, IPEndPoint destination, FaultDirection direction, int delayMs)
    {
        long due = Stopwatch.GetTimestamp() + delayMs * Stopwatch.Frequency / 1000;
        var copy = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(copy);

        bool queued = false;
        lock (_queueGate)
        {
            if (_queue.Count < _maxQueued)
            {
                _queue.Enqueue(new Pending(copy, datagram.Length, destination, direction), (due, _sequence++));
                queued = true;
            }
        }

        if (!queued)
        {
            ArrayPool<byte>.Shared.Return(copy);
            Interlocked.Increment(ref _queueFull);
            return;
        }

        Interlocked.Increment(ref _delayed);
        // At most a couple of pending releases (racing producers); the delivery loop re-checks the queue on every wake.
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    // 기능: 지연 큐의 유일한 소비자. 만기가 된 datagram을 꺼내 보내고(전달 시점에 Blocked면 버림) 버퍼를 풀에 돌려주며, 다음 만기나 새 enqueue까지 기다린다.
    // 입력: 없음.
    // 출력: 반환값 없음. 취소·폐기로 끝나면 조용히, 예상 밖 오류로 멈추면 LastError에 기록된다.
    private async Task DeliveryLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Pending pending = default;
                bool due = false;
                int waitMs = Timeout.Infinite;
                lock (_queueGate)
                {
                    if (_queue.TryPeek(out var head, out var priority))
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (priority.Due <= now)
                        {
                            _queue.Dequeue();
                            pending = head;
                            due = true;
                        }
                        else
                        {
                            // Round up so we never wake before the due time; the check above runs again anyway.
                            long ticks = priority.Due - now;
                            waitMs = (int)Math.Max(1, (ticks * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
                        }
                    }
                }

                if (due)
                {
                    try
                    {
                        // Blocked is checked again at delivery so datagrams queued before a block are dropped too.
                        if (GetFaults(pending.Direction).Blocked) Interlocked.Increment(ref _dropped);
                        else SendNow(pending.Buffer.AsSpan(0, pending.Length), pending.Destination, pending.Direction);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pending.Buffer);
                    }
                    continue;
                }

                await _wake.WaitAsync(waitMs, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (IsShutdown(e, ct))
        {
        }
        catch (Exception e)
        {
            Volatile.Write(ref _lastError, $"delivery loop stopped: {e.GetType().Name}: {e.Message}");
        }
    }

    // 기능: 방향에 맞는 소켓으로 datagram을 동기 전송한다.
    // 입력: datagram - 보낼 바이트, destination - 보낼 endpoint, direction - ToServer면 업스트림 소켓, 아니면 클라이언트 쪽 소켓.
    // 출력: 반환값 없음. 성공하면 Forwarded, 소켓 오류면 Dropped가 증가하고 폐기 중이면 조용히 버려진다.
    private void SendNow(ReadOnlySpan<byte> datagram, IPEndPoint destination, FaultDirection direction)
    {
        var socket = direction == FaultDirection.ToServer ? _upstream : _clientSide;
        try
        {
            // UDP send on loopback does not block meaningfully; a synchronous send keeps ordering simple.
            socket.SendTo(datagram, SocketFlags.None, destination);
            Interlocked.Increment(ref _forwarded);
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref _dropped);
        }
        catch (ObjectDisposedException)
        {
            // Disposing: the datagram is lost with the proxy.
        }
    }

    // 기능: 한 번만 실행되는 폐기. 취소 후 두 소켓을 닫고 세 루프를 ShutdownTimeout까지 기다린 뒤, 모두 멈췄으면 큐 버퍼를 풀에 돌려주고 동기화 객체를 해제한다.
    // 입력: 없음.
    // 출력: 반환값 없음. 루프가 제때 멈추지 않으면 LastError에 기록하고 큐·세마포어·토큰은 GC에 맡긴다.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        _clientSide.Dispose();
        _upstream.Dispose();

        bool stopped;
        try
        {
            await Task.WhenAll(_clientLoop, _upstreamLoop, _deliveryLoop).WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            stopped = true;
        }
        catch (TimeoutException)
        {
            stopped = false;
            Volatile.Write(ref _lastError, "proxy loops did not stop within the shutdown timeout");
        }

        // If a loop is still running it may still touch the queue, the semaphore or the token: leave them to the GC.
        if (!stopped) return;

        lock (_queueGate)
        {
            while (_queue.TryDequeue(out var pending, out _)) ArrayPool<byte>.Shared.Return(pending.Buffer);
        }
        _wake.Dispose();
        _cts.Dispose();
    }

    // 기능: UDP 소켓을 만들고 Windows에서는 SIO_UDP_CONNRESET을 꺼 ICMP 도달 불가가 ConnectionReset으로 올라오지 않게 한다.
    // 입력: family - 주소 체계(IPv4/IPv6).
    // 출력: 바인딩되지 않은 UDP 소켓. IOControl 실패는 무시한다.
    private static Socket CreateSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
            }
            catch (SocketException)
            {
                // Not fatal: ConnectionReset is also caught as transient in the receive loops.
            }
        }
        return socket;
    }

    // 기능: 업스트림 소켓을 바인딩할 로컬 주소를 대상 주소에 맞춰 고른다.
    // 입력: target - 게임 서버 주소.
    // 출력: 대상이 loopback이면 같은 체계의 loopback, 아니면 같은 체계의 Any 주소.
    private static IPAddress LocalAddressFor(IPAddress target)
    {
        if (IPAddress.IsLoopback(target))
            return target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        return target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
    }

    // 기능: 소켓이 망가지지 않은 채 datagram 하나에서만 날 수 있는 수신 오류인지 판정한다.
    // 입력: e - 수신 중 난 소켓 예외.
    // 출력: ConnectionReset·MessageSize·NetworkReset이면 true.
    // Errors a UDP receive can report for one bad datagram without the socket being broken.
    private static bool IsTransient(SocketException e) => e.SocketErrorCode is
        SocketError.ConnectionReset or SocketError.MessageSize or SocketError.NetworkReset;

    // 기능: 루프를 끝낸 예외가 정상 종료(취소·폐기) 때문인지 판정한다.
    // 입력: e - 루프를 끝낸 예외, ct - 프록시의 취소 토큰.
    // 출력: 취소·폐기 예외이거나 취소 후의 소켓 예외면 true.
    private static bool IsShutdown(Exception e, CancellationToken ct) =>
        e is OperationCanceledException or ObjectDisposedException
        || (ct.IsCancellationRequested && e is SocketException);

    private sealed record FaultPair(NetworkFaultSettings ToServer, NetworkFaultSettings ToClient);

    private readonly record struct Pending(byte[] Buffer, int Length, IPEndPoint Destination, FaultDirection Direction);
}
