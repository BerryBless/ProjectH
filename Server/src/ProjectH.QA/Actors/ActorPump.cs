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

    // 기능: Pump를 만들고 Background Pump 스레드를 바로 시작한다.
    // 입력: log - 로그 출력.
    // 출력: 스레드가 돌고 있는 ActorPump.
    public ActorPump(Action<string> log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "QA actor pump" };
        _thread.Start();
    }

    // Seconds since the pump started (the steering clock). Pump thread.
    internal float Now => (float)_clock.Elapsed.TotalSeconds;

    // 기능: Actor를 Pump에 등록한다(앞서 큐에 든 명령 뒤에 처리되며 그때부터 Tick이 돈다).
    // 입력: actor - 등록할 Headless Actor, token - 취소 토큰.
    // 출력: 반환값 없음. 채널이 가득 차면 자리가 날 때까지 기다리고, Pump가 멈췄으면 예외.
    // Registers an actor; the pump starts ticking it after the commands queued before.
    public ValueTask AddAsync(HeadlessActor actor, CancellationToken token) => _channel.Writer.WriteAsync((actor, null), token);

    // 기능: Actor 명령을 Pump 채널에 넣는다(다음 Tick의 Drain에서 적용).
    // 입력: actor - 대상 Headless Actor, command - 적용할 명령, token - 취소 토큰.
    // 출력: 반환값 없음. 채널이 가득 차면 자리가 날 때까지 기다리고, Pump가 멈췄으면 예외.
    public ValueTask PostAsync(HeadlessActor actor, ActorCommand command, CancellationToken token) =>
        _channel.Writer.WriteAsync((actor, command), token);

    // 기능: 루프 중단을 요청하고 스레드가 모든 연결을 닫고 끝날 때까지 timeout 안에서 기다린다.
    // 입력: timeout - 기다릴 최대 시간.
    // 출력: 시간 안에 스레드가 끝났으면 true, 아니면 false.
    // Ends the loop and waits (bounded) for the thread to close every connection.
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        _stop.Cancel();
        Task done = await Task.WhenAny(_exited.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return done == _exited.Task;
    }

    // 기능: Pump 스레드 본체. Tick마다 명령을 비우고 모든 Actor를 Tick하며(한 Actor의 예외는 그 Actor의 오류로만 기록) Actor의 SimHz에 맞춰 다음 Tick까지 기다린다. 끝나면 모든 Actor를 종료하고 채널을 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음. 루프가 예외로 죽으면 그 예외로 채널이 완료되고, 어떤 경우든 _exited가 설정된다.
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

    // 기능: 채널의 항목을 Tick당 최대 MaxCommandsPerTick개 읽어 적용한다(명령이 null이면 Actor 등록).
    // 입력: 없음.
    // 출력: 반환값 없음. Actor 목록이나 Actor 의도가 바뀌고, 적용 중 예외는 그 Actor의 오류로 기록돼 바로 발행된다.
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

    // 기능: Pump 시계가 target에 이를 때까지 기다린다(2 ms 넘게 남으면 WaitHandle 대기, 아니면 Yield).
    // 입력: target - 목표 시각(Pump 시계 기준 초), token - 중단 토큰.
    // 출력: 반환값 없음. 시각에 이르거나 중단되면 돌아온다.
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
