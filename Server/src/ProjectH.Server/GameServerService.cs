using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;

namespace ProjectH.Server;

// Bridges the Generic Host lifetime (Ctrl+C, SIGTERM) to the game loop's own thread.
// The host owns this service; this service owns the GameLoop, the Meter and the stall watchdog and disposes them.
public sealed class GameServerService : IHostedService, System.IDisposable
{
    private readonly GameLoop _loop;
    private readonly ILogger _logger;
    private readonly ServerMeter _meter;
    private StallWatchdog? _watchdog;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeSpan _fatalStall;   // server review M8 (Zero = off)

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        MatchHistoryWriter writer, StatsQueryQueue statsQueries, IHostApplicationLifetime lifetime, Qa.QaControl? qa = null)
    {
        _logger = logger;
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        // Phase 9: finished matches go to the bounded queue; MatchHistoryWriter saves them off the game loop.
        // Phase 10 D6: when match resets keep failing, the server stops with exit code 1 so a supervisor or a person
        // notices. StopApplication runs on the thread pool: the game loop thread must not wait on the host.
        _lifetime = lifetime;
        _fatalStall = TimeSpan.FromSeconds(options.Value.FatalStallSeconds);
        _loop = new GameLoop(options.Value, data, logger, matchSink: record => matchHistory.TryEnqueue(record),
            onFatal: StopWithError,
            // Phase 11 D8: statistics requests go to StatsQueryService through this queue; the loop sends the answers.
            statsQueries: statsQueries);
        // Phase 10 D9: the writer's totals go into the Health line and the Meter.
        _loop.Health.Persistence = () => writer.Counts;
        // QA-1 D2: registered only in QA mode (Program.cs); otherwise null and the loop pays one null check per tick.
        if (qa != null) _loop.AttachQa(qa);
        _meter = new ServerMeter(_loop.Health);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop.Start();
        // Server review M8: a stall past FatalStallSeconds refuses new connections and stops the process with exit code 1
        // (the loop is hung; GameLoop.Stop leaves its thread behind after the join limit).
        _watchdog = new StallWatchdog(() => _loop.LastTickTimestamp, _loop.Time, _loop.Health, _logger, fatalAfter: _fatalStall,
            onFatalStall: () =>
            {
                _loop.Listener.BeginStopping();
                StopWithError();
            });
        _watchdog.Start();
        return Task.CompletedTask;
    }

    // Phase 10 D6, server review M8: the fatal path of failing resets and of a hung loop. Exit code 1 tells a supervisor or
    // a person; StopApplication runs on the thread pool, so neither the game loop thread nor the watchdog's timer waits on
    // the host. Any thread; calling it twice only asks the host to stop twice.
    private void StopWithError()
    {
        Environment.ExitCode = 1;
        _ = Task.Run(_lifetime.StopApplication);
    }

    // Phase 10 D7: GameLoop.Stop blocks (thread join up to 5 s, shutdown notices up to 1 s), so it runs on the thread
    // pool and the host's thread only awaits it, for as long as the host's shutdown token allows.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A stopping loop does not tick: that is no stall.
        _watchdog?.Dispose();
        try
        {
            await Task.Run(_loop.Stop, CancellationToken.None).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The host shutdown timeout ended before the game loop finished stopping");
        }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        _loop.Dispose();
        _meter.Dispose();
    }
}
