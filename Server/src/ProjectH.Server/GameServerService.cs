using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server.Game;
using ProjectH.Server.Persistence;

namespace ProjectH.Server;

// Bridges the Generic Host lifetime (Ctrl+C, SIGTERM) to the game loop's own thread.
// The host owns this service; this service owns the GameLoop and disposes it.
public sealed class GameServerService : IHostedService, System.IDisposable
{
    private readonly GameLoop _loop;

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory)
    {
        // The data files are copied next to appsettings.json. A missing or invalid file throws here, so the
        // host refuses to start, the same as an invalid ServerOptions value (Phase 3 D4, Phase 4 D2).
        var data = GameData.LoadDirectory(AppContext.BaseDirectory, options.Value.SimHz);
        // Phase 9: finished matches go to the bounded queue; MatchHistoryWriter saves them off the game loop.
        _loop = new GameLoop(options.Value, data, logger, matchSink: record => matchHistory.TryEnqueue(record));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Joins the game loop thread (at most one tick) and closes the socket.
        _loop.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _loop.Dispose();
}
