using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
// One writer instance, both a hosted service and a dependency of GameServerService (Phase 10: its counters go into the
// Health line). Still registered before the game server, so it still stops after it.
builder.Services.AddSingleton<MatchHistoryWriter>();
builder.Services.AddHostedService(services => services.GetRequiredService<MatchHistoryWriter>());
builder.Services.AddHostedService<GameServerService>();

IHost host = builder.Build();   // RunAsync disposes the host

// Phase 10 D6: whatever escapes every other handler is at least logged. An unhandled exception on any thread still
// ends the process (the runtime decides that); an unobserved task exception does not.
ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectH.Server");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    // Written straight to stderr as well: the console logger's queue may not flush before the process ends.
    Console.Error.WriteLine($"Unhandled exception (terminating: {e.IsTerminating}): {e.ExceptionObject}");
    log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    log.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

await host.RunAsync();
// 1 after a fatal stop (GameServerService sets it, D6), otherwise 0.
return Environment.ExitCode;
