using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectH.Server;

// Bridges the Generic Host lifetime (Ctrl+C, SIGTERM) to the game loop's own thread.
// The host owns this service; this service owns the GameLoop and disposes it.
public sealed class GameServerService : IHostedService, System.IDisposable
{
    private readonly GameLoop _loop;

    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger)
    {
        _loop = new GameLoop(options.Value, logger);
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
