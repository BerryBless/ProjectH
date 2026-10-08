using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectH.Server;
using ProjectH.Server.Qa;

// QA-1 D3: `--qa-mode` leaves the arguments before the host sees them (its command-line provider would take the next
// argument as the flag's value).
string[] hostArgs = QaMode.StripFlag(args, out bool qaFlag);
HostApplicationBuilder builder = ServerHost.CreateBuilder(hostArgs);

// QA-1 D3: QA mode only outside Production. Off = nothing registered: no QA object, no TCP listener. After the game
// server, so the QA listener starts once the UDP port is bound and stops before the game loop.
QaSetup.Register(builder, qaFlag, Environment.GetEnvironmentVariable(QaMode.EnvironmentVariable), out bool qaRefused);

IHost host = builder.Build();   // RunAsync disposes the host

// Phase 10 D6: whatever escapes every other handler is at least logged. An unhandled exception on any thread still
// ends the process (the runtime decides that); an unobserved task exception does not.
ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectH.Server");
// Review fix A1: LiteNetLib's own messages (one per dropped over-fragmented datagram, among others) go to ILogger at Debug
// instead of a synchronous console write on the receive thread. Process-wide, set once before the server starts.
LiteNetLib.NetDebug.Logger = new ProjectH.Server.Diagnostics.LiteNetLogBridge(
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LiteNetLib"));
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
