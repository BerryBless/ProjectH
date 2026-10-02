using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProjectH.Server.Game;

namespace ProjectH.Server.Qa;

// What a work item answers: an HTTP status and the JSON body (plain DTOs built on the game loop thread, so serializing
// them on an HTTP thread reads nothing the game loop writes).
public sealed record QaResult(int Status, object Body)
{
    public static QaResult Ok(object? result = null) => new(200, new QaOk(true, result));
    public static QaResult Error(int status, string error) => new(status, new QaError(false, error));
    public static QaResult Data(object body) => new(200, body);
}

public sealed record QaOk(bool Ok, object? Result);
// QA-3: the match history writer as the QA tool sees it (server.dbQueueLength and friends).
public sealed record QaDbStatus(bool Enabled, long Saved, long Failed, long Discarded, long Dropped, int QueueLength);
public sealed record QaError(bool Ok, string Error);

// The game loop's view during one QA tick. One instance, reused every tick, game loop thread only.
internal sealed class QaTick
{
    public GameLoop Loop = null!;
    public Match Match = null!;
    public QaControl Qa = null!;
}

internal delegate QaResult QaWork(QaTick tick);

// One request waiting for the game loop. State: 0 queued, 1 running (or done), 2 abandoned by its HTTP request.
// Exactly one of TryStart (game loop) and TryAbandon (HTTP thread) wins, so a 504 always means "never ran".
internal sealed class QaWorkItem
{
    private int _state;

    public QaWorkItem(QaWork work) => Work = work;

    public QaWork Work { get; }
    // Continuations run on the thread pool, never inline on the game loop thread that completes it.
    public TaskCompletionSource<QaResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
    public bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
}

// QA-1 D2, D5, D6: the QA side of the game loop. HTTP threads put work items into a bounded channel and await them; the
// game loop runs at most MaxItemsPerTick of them at the end of every tick (OnTick), together with the event diff and the
// metrics samples. Everything that touches Match runs there, so there is no lock: the channel and the work item's
// Interlocked state are the only cross-thread points.
//
// Lifetime: created by the host (DI singleton) only in QA mode and attached to the GameLoop before it starts; lives as
// long as the host. The channel holds at most QueueCapacity items; items left when the loop stops are never run and
// their requests end with 504. The event ring and the metrics rings are fixed-size arrays.
public sealed class QaControl
{
    private readonly Channel<QaWorkItem> _queue;
    private readonly ILogger _logger;
    private readonly QaTick _tick = new();
    private GameLoop? _loop;
    private int _qaPort;
    private long _rejected;
    private long _timedOut;
    private long _failed;
    // Game loop thread only.
    private bool _onTickFailureLogged;
    private double _lastQaMs;
    private bool _first = true;

    public QaControl(ServerOptions server, QaOptions options, ILogger logger, string environmentName)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        EnvironmentName = environmentName;
        // Wait mode only makes TryWrite return false when full; nobody ever waits to write.
        _queue = Channel.CreateBounded<QaWorkItem>(new BoundedChannelOptions(QaOptions.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Events = new QaEvents();
        Metrics = new QaMetrics(server.SimHz);
    }

    public ServerOptions Server { get; }
    public QaOptions Options { get; }
    public string EnvironmentName { get; }
    public int GamePort => _loop?.LocalPort ?? 0;
    // Set once the HTTP listener is bound (QaHttpService).
    public int QaPort { get => Volatile.Read(ref _qaPort); internal set => Volatile.Write(ref _qaPort, value); }
    public long Rejected => Interlocked.Read(ref _rejected);
    public long TimedOut => Interlocked.Read(ref _timedOut);
    public long Failed => Interlocked.Read(ref _failed);
    // Game loop thread only.
    internal QaEvents Events { get; }
    internal QaMetrics Metrics { get; }

    internal void Bind(GameLoop loop) => _loop = loop;

    // QA-3: set once at construction by QaSetup (null in tests without a writer). Called on any thread.
    public Func<QaDbStatus>? Database { get; set; }
    // QA-3: connections open now (the game loop's gauge; any thread).
    public int Peers => _loop?.Health.Peers ?? 0;

    // HTTP threads. 503 when the queue is full; 504 when the game loop did not take the item in time (it is dropped then,
    // so it never runs). An item already running is awaited for one more timeout: it ends within its tick.
    internal async Task<QaResult> SubmitAsync(QaWork work, CancellationToken cancellation = default)
    {
        var item = new QaWorkItem(work);
        if (!_queue.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _rejected);
            return QaResult.Error(503, "QA queue full; retry later.");
        }
        TimeSpan timeout = TimeSpan.FromMilliseconds(Options.CommandTimeoutMs);
        try
        {
            return await item.Completion.Task.WaitAsync(timeout, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
        {
            if (item.TryAbandon())
            {
                Interlocked.Increment(ref _timedOut);
                return QaResult.Error(504, "The game loop did not run the request in time; it was not executed.");
            }
        }
        try
        {
            return await item.Completion.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref _timedOut);
            return QaResult.Error(504, "The request started but did not finish in time; its outcome is unknown.");
        }
    }

    // Game loop thread, the end of every tick (GameLoop.RunTick). Never throws: a QA failure must not count as a tick
    // failure. The match is read from the loop every time, because a reset replaces it.
    internal void OnTick(GameLoop loop)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            Match match = loop.Match;
            // The previous tick's duration (it included the previous OnTick) and that OnTick's own time.
            if (!_first) Metrics.Record(loop.LastTickMs, _lastQaMs);
            _first = false;
            Metrics.SampleSecond(loop.Stats.PacketsInTotal, loop.Stats.PacketsOutTotal);
            if (Options.Events) Events.Diff(match);

            _tick.Loop = loop;
            _tick.Match = match;
            _tick.Qa = this;
            for (int i = 0; i < QaOptions.MaxItemsPerTick && _queue.Reader.TryRead(out QaWorkItem? item); i++)
            {
                if (!item.TryStart()) continue;   // its request gave up: never run it
                QaResult result;
                try
                {
                    result = item.Work(_tick);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    result = QaResult.Error(500, $"{ex.GetType().Name}: {ex.Message}");
                }
                item.Completion.TrySetResult(result);
            }
        }
        catch (Exception ex)
        {
            // Only the diff or the samples can get here. Logged once per process: it would repeat every tick.
            Interlocked.Increment(ref _failed);
            if (!_onTickFailureLogged)
            {
                _onTickFailureLogged = true;
                try
                {
                    _logger.LogError(ex, "QA tick work failed (logged once)");
                }
                catch
                {
                    // Nothing left to report it with.
                }
            }
        }
        _lastQaMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
