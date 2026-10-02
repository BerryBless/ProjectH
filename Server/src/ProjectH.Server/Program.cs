using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectH.Server;

HostApplicationBuilder builder = ServerHost.CreateBuilder(args);

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
