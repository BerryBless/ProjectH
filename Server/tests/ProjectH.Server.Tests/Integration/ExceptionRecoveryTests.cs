using System;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Server.Game;
using ProjectH.Server.Tests.Diagnostics;
using ProjectH.Shared.Protocol;
using Xunit;
using static ProjectH.Server.Tests.Integration.HardeningIntegrationTests;

namespace ProjectH.Server.Tests.Integration;

// Phase 10 D6, D7: a failing tick is skipped, a failing match is reset, failing resets stop the server; nothing
// outside the tick kills the loop thread; shutdown tells clients and has a time limit.
public sealed class ExceptionRecoveryTests
{
    private const int SimHz = 30;
    private const int TicksBeforeReset = SimHz * 3;

    private static GameLoop Loop(Action? onFatal = null, TimeProvider? time = null) =>
        new(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance, onFatal: onFatal, time: time);

    private static void Fail(GameLoop loop, int ticks)
    {
        loop.TickFaultHook = () => throw new InvalidOperationException("test fault");
        for (int i = 0; i < ticks; i++) loop.RunTickGuarded();
        loop.TickFaultHook = null;
    }

    [Fact]
    public void OneFailedTick_IsSkipped_AndTheMatchGoesOn()
    {
        using GameLoop loop = Loop();
        Match match = loop.Match;
        loop.RunTickGuarded();
        Fail(loop, 1);
        loop.RunTickGuarded();
        Assert.Same(match, loop.Match);
        Assert.Equal(2u, match.ServerTick);   // the failed tick did not run
        Assert.Equal(1, loop.Health.TickFailures);
        Assert.Equal(0, loop.Health.MatchResets);
    }

    [Fact]
    public void FailingForThreeSeconds_ResetsTheMatch_AndASuccessBreaksTheRun()
    {
        using GameLoop loop = Loop();
        Match first = loop.Match;
        Fail(loop, TicksBeforeReset - 1);
        loop.RunTickGuarded();   // a good tick: the run starts over
        Fail(loop, TicksBeforeReset - 1);
        Assert.Same(first, loop.Match);
        Assert.Equal(0, loop.Health.MatchResets);

        Fail(loop, 1);
        Assert.NotSame(first, loop.Match);
        Assert.Equal(0u, loop.Match.ServerTick);
        Assert.Equal(1, loop.Health.MatchResets);
    }

    [Fact]
    public void ThreeResetsWithinTenMinutes_StopTheServer()
    {
        var time = new ManualTime();
        int fatal = 0;
        using GameLoop loop = Loop(() => fatal++, time);

        Fail(loop, TicksBeforeReset);   // t = 0
        time.Advance(TimeSpan.FromMinutes(6));
        Fail(loop, TicksBeforeReset);   // t = 6 min
        time.Advance(TimeSpan.FromMinutes(6));
        Fail(loop, TicksBeforeReset);   // t = 12 min: the first is 12 minutes old, so 2 in the window
        Assert.Equal(0, fatal);
        Assert.Equal(3, loop.Health.MatchResets);

        Assert.False(loop.Listener.IsStopping);
        time.Advance(TimeSpan.FromMinutes(3));
        Fail(loop, TicksBeforeReset);   // t = 15 min: 6, 12 and 15 are within ten minutes
        Assert.Equal(1, fatal);
        Assert.Equal(3, loop.Health.MatchResets);   // B4: the fatal "reset" does not happen, so it is not counted
        Assert.True(loop.Listener.IsStopping);      // B10: no new connection into a stopping server
    }

    // Review round 1: a fault in the match that makes every player's tick throw is caught per player (M7), so the tick
    // itself succeeds. MaxPlayers player failures within the window still count as a failing match: it is reset, and
    // resets that keep coming stop the server, as for failing ticks (D6).
    [Fact]
    public void EveryPlayerFailingEveryTick_ResetsTheMatch_AndRepeatedResetsStopTheServer()
    {
        var time = new ManualTime();
        int fatal = 0;
        using GameLoop loop = Loop(() => fatal++, time);
        int nextPeer = 100;
        void FailEveryone()
        {
            Match match = loop.Match;
            for (int i = 0; i < 4; i++) Assert.Equal(JoinResult.Ok, match.TryJoin(++nextPeer, "p" + nextPeer));
            match.FaultEntityId = -1;
            loop.RunTickGuarded();
        }

        Match first = loop.Match;
        FailEveryone();
        Assert.NotSame(first, loop.Match);
        Assert.Equal(1, loop.Health.MatchResets);
        Assert.Equal(4, loop.Health.PlayerFailures);
        Assert.Equal(0, loop.Health.TickFailures);

        FailEveryone();
        Assert.Equal(2, loop.Health.MatchResets);
        Assert.Equal(0, fatal);
        FailEveryone();                              // the third reset within ten minutes: the fatal stop instead
        Assert.Equal(1, fatal);
        Assert.Equal(2, loop.Health.MatchResets);
        Assert.True(loop.Listener.IsStopping);
    }

    // Fewer than MaxPlayers failures within the window are single players' faults: no reset.
    [Fact]
    public void PlayerFailuresSpreadWiderThanTheWindow_DoNotResetTheMatch()
    {
        using GameLoop loop = Loop();
        Match match = loop.Match;
        void FailTwo(int firstPeer)
        {
            Assert.Equal(JoinResult.Ok, match.TryJoin(firstPeer, "a" + firstPeer));
            Assert.Equal(JoinResult.Ok, match.TryJoin(firstPeer + 1, "b" + firstPeer));
            match.FaultEntityId = -1;
            loop.RunTickGuarded();
            match.FaultEntityId = 0;
        }
        FailTwo(101);
        for (int i = 0; i < 10 * SimHz; i++) loop.RunTickGuarded();   // the window is 10 s
        FailTwo(111);
        Assert.Same(match, loop.Match);
        Assert.Equal(0, loop.Health.MatchResets);
        Assert.Equal(4, loop.Health.PlayerFailures);

        FailTwo(121);                                // 4 within the window
        Assert.NotSame(match, loop.Match);
        Assert.Equal(1, loop.Health.MatchResets);
    }

    // B10: while the server stops, a connection request is refused as ServerFull (no protocol change).
    [Fact]
    public void AStoppingServer_RefusesNewConnections()
    {
        using GameLoop server = StartServer();
        using var before = Join(server, "before");
        server.Listener.BeginStopping();
        using var late = new HeadlessClient();
        late.Connect(server.LocalPort, "late");
        Assert.True(Pump.Until(() => late.Disconnected, 3000, late), "refused");
        Assert.Equal(LiteNetLib.DisconnectReason.ConnectionRejected, late.DisconnectReason);
        Assert.Equal(RejectReason.ServerFull, late.RejectReason);
        Assert.Equal(1, server.Health.Rejects(RejectReason.ServerFull));
        Assert.False(before.Disconnected);   // connections already in are closed by Stop, not here
    }

    // B5: the sink error of a match a reset throws away is not lost, and a sink that fails in every match is logged once
    // per stats interval.
    [Fact]
    public void AThrowingMatchSink_IsLoggedOncePerInterval_AcrossAReset()
    {
        var log = new ListLogger();
        int calls = 0;
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4, StartCountdownSeconds = 1 }, TestGameData.Create(),
            log, matchSink: _ =>
            {
                calls++;
                throw new InvalidOperationException("sink fault");
            });
        int SinkLogs() => log.Entries.Count(e => e.Level == LogLevel.Error && e.Message.StartsWith("Recording a finished match failed"));

        FinishAMatch(loop);
        Fail(loop, TicksBeforeReset);   // the reset throws the failed match away
        Assert.Equal(1, loop.Health.MatchResets);
        FinishAMatch(loop);             // and the new match fails as well, in the same interval
        Assert.Equal(2, calls);

        loop.LogPeriodic();
        Assert.Equal(1, SinkLogs());
        loop.LogPeriodic();             // nothing new in this interval
        Assert.Equal(1, SinkLogs());
    }

    // Two players start a match (1 s countdown), then one leaves: the other wins and the match is recorded.
    private static void FinishAMatch(GameLoop loop)
    {
        Match match = loop.Match;
        Assert.Equal(JoinResult.Ok, match.TryJoin(101, "x"));
        Assert.Equal(JoinResult.Ok, match.TryJoin(102, "y"));
        for (int i = 0; i < 3 * SimHz && !match.Flow.InMatch; i++) loop.RunTickGuarded();
        Assert.True(match.Flow.InMatch);
        match.Leave(101);
        loop.RunTickGuarded();
        Assert.Equal(MatchFlowState.Finished, match.Flow.State);
    }

    // B6: a logger that throws while the tick failure is reported does not break "RunTickGuarded never throws".
    [Fact]
    public void ATickFailure_WithAThrowingLogger_StillDoesNotThrow()
    {
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), new ThrowingLogger());
        Fail(loop, TicksBeforeReset);   // the first failure logs (throws), the last one resets (logs, throws)
        Assert.Equal(TicksBeforeReset, loop.Health.TickFailures);
        Assert.Equal(1, loop.Health.MatchResets);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger fault");
    }

    [Fact]
    public void AMatchReset_ClosesEveryClientWithServerError_AndTheyCanJoinTheNewMatch()
    {
        using GameLoop server = StartServer();
        using var a = Join(server, "a");
        using var b = Join(server, "b");

        server.TickFaultHook = () => throw new InvalidOperationException("test fault");
        Assert.True(Pump.Until(() => a.Disconnected && b.Disconnected, 6000, a, b), "closed");
        server.TickFaultHook = null;
        Assert.Equal(DisconnectCode.ServerError, a.DisconnectCode);
        Assert.Equal(DisconnectCode.ServerError, b.DisconnectCode);
        Assert.True(DisconnectCodes.ShouldReconnect(remoteClose: true, a.DisconnectCode, networkLoss: false));
        Assert.Equal(1, server.Health.MatchResets);

        using var again = Join(server, "a");
        Assert.True(SpinUntil(() => server.Health.Players == 1, 3000), "joined the new match");
    }

    // A fault while removing a timed-out connection must cost at most that tick, not poison every later one.
    [Fact]
    public void AFaultInTheTimeoutSweep_IsRecoveredFrom_WithoutAReset()
    {
        using GameLoop server = StartServer(joinTimeout: 1);
        int thrown = 0;
        server.RemoveFaultHook = () =>
        {
            if (Interlocked.Exchange(ref thrown, 1) == 0) throw new InvalidOperationException("test fault");
        };
        using var first = new HeadlessClient();
        first.Connect(server.LocalPort, "first");
        Assert.True(Pump.Until(() => first.Disconnected, 5000, first), "first timed out");
        using var second = new HeadlessClient();
        second.Connect(server.LocalPort, "second");
        Assert.True(Pump.Until(() => second.Disconnected, 5000, second), "second timed out");

        Assert.Equal(1, server.Health.TickFailures);
        Assert.Equal(0, server.Health.MatchResets);
        Assert.Equal(2, server.Health.Kicks(DisconnectCode.JoinTimeout));
    }

    [Fact]
    public void ExceptionsOutsideTheTick_DoNotEndTheLoopThread()
    {
        using GameLoop server = StartServer();
        server.LoopFaultHook = () => throw new InvalidOperationException("test fault");
        Assert.True(SpinUntil(() => server.Health.LoopFailures >= 5, 3000), "failures seen");
        long ticks = server.LoopTicks;
        server.LoopFaultHook = null;
        Assert.True(SpinUntil(() => server.LoopTicks > ticks + 10, 3000), "still ticking");
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task Shutdown_TellsConnectedClients_ServerShutdown()
    {
        GameLoop server = StartServer();
        using var a = Join(server, "a");
        var stopping = Task.Run(server.Dispose);
        Assert.True(Pump.Until(() => a.Disconnected, 3000, a), "closed");
        Assert.Equal(DisconnectCode.ServerShutdown, a.DisconnectCode);
        Assert.False(DisconnectCodes.ShouldReconnect(remoteClose: true, a.DisconnectCode, networkLoss: false));
        Assert.Same(stopping, await Task.WhenAny(stopping, Task.Delay(3000)));
    }

    [Fact]
    public void Stop_ReturnsWithinItsLimit_WhenTheLoopIsStuck()
    {
        GameLoop server = StartServer();
        using var stuck = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        server.TickFaultHook = () =>
        {
            entered.Set();
            stuck.Wait();
        };
        Assert.True(entered.Wait(3000), "loop stuck");

        var clock = Stopwatch.StartNew();
        server.Stop(TimeSpan.FromMilliseconds(300));
        Assert.True(clock.ElapsedMilliseconds < 2500, $"Stop took {clock.ElapsedMilliseconds} ms");
        Assert.True(server.IsRunning);   // left behind, as a background thread

        stuck.Set();   // let it finish: it sees the cancelled token and exits
        Assert.True(SpinUntil(() => !server.IsRunning, 3000), "thread ended");
        server.Dispose();
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs) => SpinWait.SpinUntil(condition, timeoutMs);
}
