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

    public NetworkFaultSettings GetFaults(FaultDirection direction)
    {
        var pair = Volatile.Read(ref _faults);
        return direction == FaultDirection.ToClient ? pair.ToClient : pair.ToServer;
    }

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

    public void ClearFaults() => SetFaults(NetworkFaultSettings.None);

    // Forget the latched actor endpoint so the next sender becomes the actor (an actor that reconnects from a new
    // local port). Replies from the server are dropped until that next datagram arrives.
    public void ResetClient() => Volatile.Write(ref _client, null);

    public void SetBlocked(bool blocked, FaultDirection direction = FaultDirection.Both)
    {
        // Read-modify-write per direction; a concurrent SetFaults may win the race, which is acceptable for QA steps
        // that run from a single orchestrator flow.
        if (direction != FaultDirection.ToClient)
            SetFaults(GetFaults(FaultDirection.ToServer) with { Blocked = blocked }, FaultDirection.ToServer);
        if (direction != FaultDirection.ToServer)
            SetFaults(GetFaults(FaultDirection.ToClient) with { Blocked = blocked }, FaultDirection.ToClient);
    }

    // "localhost" and IP literals only: QA servers run locally, and this avoids a blocking DNS lookup.
    public static IPEndPoint ParseTarget(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "must be 1..65535");
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return new IPEndPoint(IPAddress.Loopback, port);
        if (IPAddress.TryParse(host, out var address)) return new IPEndPoint(address, port);
        throw new ArgumentException($"host must be 'localhost' or an IP address: {host}", nameof(host));
    }

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

    private static IPAddress LocalAddressFor(IPAddress target)
    {
        if (IPAddress.IsLoopback(target))
            return target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        return target.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
    }

    // Errors a UDP receive can report for one bad datagram without the socket being broken.
    private static bool IsTransient(SocketException e) => e.SocketErrorCode is
        SocketError.ConnectionReset or SocketError.MessageSize or SocketError.NetworkReset;

    private static bool IsShutdown(Exception e, CancellationToken ct) =>
        e is OperationCanceledException or ObjectDisposedException
        || (ct.IsCancellationRequested && e is SocketException);

    private sealed record FaultPair(NetworkFaultSettings ToServer, NetworkFaultSettings ToClient);

    private readonly record struct Pending(byte[] Buffer, int Length, IPEndPoint Destination, FaultDirection Direction);
}
