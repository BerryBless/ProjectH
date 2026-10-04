using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using ProjectH.Server.Persistence;

namespace ProjectH.Server;

// The host's configuration, apart from Program so a test can build (never start) the same host and read its options
// (server review M1).
internal static class ServerHost
{
    // 기능: 서버 Host 설정(Content Root, Console Logger, 설정 Binding, Match 기록·전적 조회 Service, 종료 제한 시간, Game Server)을 구성한다. Host를 만들거나 시작하지는 않는다.
    // 입력: args - 명령줄 인수(설정 덮어쓰기 포함).
    // 출력: Service 등록이 끝난 HostApplicationBuilder. 종료 때 Game Server가 Writer·전적 조회 Service보다 먼저 멈추도록 등록 순서가 정해져 있다.
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        // Content root = the build output folder, so appsettings.json is found no matter where
        // `dotnet run` is started from.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Server review M1: the console logger's queue drops lines when it is full instead of making the logging thread
        // wait. A console that takes no output (a QuickEdit selection, a pipe nobody reads) would otherwise stop the game
        // loop and LiteNetLib's thread at their next log call. Also in appsettings.json; this line wins over the file.
        builder.Services.Configure<ConsoleLoggerOptions>(console => console.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite);
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
        return builder;
    }
}
