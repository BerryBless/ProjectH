using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectH.Server;
using ProjectH.Server.Persistence;
using ProjectH.Server.Qa;

// Content root = the build output folder, so appsettings.json is found no matter where
// `dotnet run` is started from.
// QA-1 D3: `--qa-mode` leaves the arguments before the host sees them (its command-line provider would take the next
// argument as the flag's value).
string[] hostArgs = QaMode.StripFlag(args, out bool qaFlag);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = hostArgs,
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
// Phase 11 D8: statistics on request. The queue links the network threads, StatsQueryService and the game loop. The
// service is registered before the game server, so on shutdown the game loop stops first (no request comes in and no
// answer goes out after that), then the service.
builder.Services.AddSingleton(_ => new StatsQueryQueue(StatsQueryQueue.DefaultCapacity));
builder.Services.AddHostedService<StatsQueryService>();
builder.Services.AddHostedService<GameServerService>();

// QA-1 D3: QA mode only outside Production. Off = nothing registered: no QA object, no TCP listener. After the game
// server, so the QA listener starts once the UDP port is bound and stops before the game loop.
QaSetup.Register(builder, qaFlag, Environment.GetEnvironmentVariable(QaMode.EnvironmentVariable), out bool qaRefused);

IHost host = builder.Build();   // RunAsync disposes the host

// Phase 10 D6: whatever escapes every other handler is at least logged. An unhandled exception on any thread still
// ends the process (the runtime decides that); an unobserved task exception does not.
ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectH.Server");
if (qaRefused)
    log.LogWarning("QA mode was requested but the environment is {Environment}: QA mode stays off (set DOTNET_ENVIRONMENT=Development)",
        builder.Environment.EnvironmentName);
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
