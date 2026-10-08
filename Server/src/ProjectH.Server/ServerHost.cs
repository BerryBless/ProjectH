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
    // 기능: 서버 호스트 빌더를 만든다: 설정 섹션 바인딩, DB 큐·Writer·통계 서비스·GameServerService 등록, Monitoring이 켜져 있으면
    //       그 슬롯과 Sender를 GameServerService 뒤에 등록한다.
    // 입력: args - 명령줄 인자(--Section:Key=Value로 설정을 덮어쓴다).
    // 출력: Build 전의 HostApplicationBuilder. Monitoring 설정이 틀리면 InvalidOperationException.
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
        // Monitoring D5: after the game server, so the sender stops before the game loop (and starts after it).
        Monitoring.MonitoringSetup.Register(builder);
        return builder;
    }
}
