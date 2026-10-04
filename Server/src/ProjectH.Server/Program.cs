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
// 기능: 어떤 Thread에서든 처리되지 않은 예외를 기록한다. Process 종료 여부는 Runtime이 정한다.
// 입력: e - 예외 객체와 종료 여부(IsTerminating).
// 출력: 반환값 없음. 예외와 종료 여부를 stderr에 직접 쓰고 Critical 로그로도 남긴다.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    // Written straight to stderr as well: the console logger's queue may not flush before the process ends.
    Console.Error.WriteLine($"Unhandled exception (terminating: {e.IsTerminating}): {e.ExceptionObject}");
    log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
};
// 기능: 관찰되지 않은 Task 예외를 기록하고 관찰된 것으로 표시한다.
// 입력: e - 관찰되지 않은 예외 정보.
// 출력: 반환값 없음. 예외를 Error 로그("Unobserved task exception")로 남기고 SetObserved로 처리 완료 표시한다.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    log.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

await host.RunAsync();
// 1 after a fatal stop (GameServerService sets it, D6), otherwise 0.
return Environment.ExitCode;
