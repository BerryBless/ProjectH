using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProjectH.Server;
using ProjectH.Server.Persistence;

// Content root = the build output folder, so appsettings.json is found no matter where
// `dotnet run` is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
// Phase 9: match history. The writer is registered before the game server, so on shutdown the host stops the game
// loop first and the writer drains the queue after it.
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("Persistence"));
builder.Services.AddSingleton(services =>
{
    PersistenceOptions persistence = services.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    string? error = persistence.Validate();
    if (error != null) throw new InvalidOperationException(error);
    return new MatchHistoryQueue(persistence.QueueCapacity);
});
// The host's default 30 s ShutdownTimeout is shared by every hosted service's StopAsync, so it would cut the writer's
// drain short: allow the longest drain plus 30 s for GameServerService.StopAsync.
builder.Services.Configure<HostOptions>(host =>
    host.ShutdownTimeout = TimeSpan.FromSeconds(PersistenceOptions.MaxShutdownDrainSeconds + 30));
builder.Services.AddHostedService<MatchHistoryWriter>();
builder.Services.AddHostedService<GameServerService>();

await builder.Build().RunAsync();
