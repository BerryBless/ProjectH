using System.Diagnostics;
using System.Threading.Channels;

namespace ProjectH.QA;

// D8/D9: the one thread that runs every HeadlessActor at the server's SimHz (30 Hz until an actor has joined).
// LiteNetLib's manual mode needs Update, Connect, Send and Stop on one thread, so the actors and their connections are
// touched only here. The runner talks to it through a bounded channel (commands in) and reads each actor's published
// ActorState (state out). No locks.
// Lifetime: created by ActorManager with the first headless actor, stopped by ActorManager.DisposeAsync (end of a
// run): the loop exits, every connection is closed gracefully on this thread, the channel is completed, the thread
// ends. If the loop dies, the channel is completed with the error so a runner waiting in PostAsync fails instead of
// hanging.
internal sealed class ActorPump
{
    public const int ChannelCapacity = 1024;          // runner waits (backpressure) when the pump falls this far behind
    private const int MaxCommandsPerTick = 512;
    private const int MaxCatchUpTicks = 3;            // like BotRunner: too far behind skips ticks instead of bursting
    private const int DefaultHz = 30;

    private readonly Channel<(HeadlessActor Actor, ActorCommand? Command)> _channel =
        Channel.CreateBounded<(HeadlessActor, ActorCommand?)>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly List<HeadlessActor> _actors = new();   // pump thread only; at most ActorManager.MaxActors
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Action<string> _log;

    public ActorPump(Action<string> log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "QA actor pump" };
        _thread.Start();
    }

    // Seconds since the pump started (the steering clock). Pump thread.
    internal float Now => (float)_clock.Elapsed.TotalSeconds;

    // Registers an actor; the pump starts ticking it after the commands queued before.
    public ValueTask AddAsync(HeadlessActor actor, CancellationToken token) => _channel.Writer.WriteAsync((actor, null), token);

    public ValueTask PostAsync(HeadlessActor actor, ActorCommand command, CancellationToken token) =>
        _channel.Writer.WriteAsync((actor, command), token);

    // Ends the loop and waits (bounded) for the thread to close every connection.
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        _stop.Cancel();
        Task done = await Task.WhenAny(_exited.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == _exited.Task;
    }

    private void Run()
    {
        Exception? failure = null;
        try
        {
            double next = _clock.Elapsed.TotalSeconds;
            double last = next;
            CancellationToken token = _stop.Token;
            while (!token.IsCancellationRequested)
            {
                Drain();
                double start = _clock.Elapsed.TotalSeconds;
                float elapsedMs = (float)((start - last) * 1000.0);
                last = start;
                float now = (float)start;
                int hz = DefaultHz;
                foreach (HeadlessActor actor in _actors)
                {
                    try
                    {
                        actor.Tick(elapsedMs, now);
                    }
                    catch (Exception e)
                    {
                        // One actor's failure is that actor's error (visible in its state), not the end of the run.
                        actor.RecordError(e);
                    }
                    if (actor.SimHz > 0) hz = actor.SimHz;
                }

                double tick = 1.0 / hz;
                next += tick;
                double after = _clock.Elapsed.TotalSeconds;
                if (after - next > MaxCatchUpTicks * tick) next = after;
                WaitUntil(next, token);
            }
        }
        catch (Exception e)
        {
            failure = e;
            _log($"Actor pump failed: {e}");
        }
        finally
        {
            foreach (HeadlessActor actor in _actors)
            {
                try
                {
                    actor.Shutdown();
                }
                catch (Exception e)
                {
                    _log($"Actor {actor.Alias} shutdown failed: {e.Message}");
                }
            }
            _channel.Writer.TryComplete(failure ?? new ObjectDisposedException("ActorPump", "The actor pump has stopped."));
            _exited.TrySetResult();
        }
    }

    private void Drain()
    {
        for (int i = 0; i < MaxCommandsPerTick && _channel.Reader.TryRead(out var item); i++)
        {
            if (item.Command == null)
            {
                _actors.Add(item.Actor);
                continue;
            }
            try
            {
                item.Actor.Apply(item.Command);
            }
            catch (Exception e)
            {
                // Still counts as applied (LastCommandId is set first); published at once so a waiting handler sees
                // the error, not a hang.
                item.Actor.RecordError(e);
                item.Actor.PublishSafe();
            }
        }
    }

    // A tool thread, not the server: sleeping until the next tick is the simplest correct wait (as BotRunner).
    private void WaitUntil(double target, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            double remainingMs = (target - _clock.Elapsed.TotalSeconds) * 1000.0;
            if (remainingMs <= 0) return;
            if (remainingMs > 2.0) token.WaitHandle.WaitOne((int)(remainingMs - 1.0));
            else Thread.Yield();
        }
    }
}
