using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Monitoring;

// Monitoring D5, D6: what monitoring adds to the host. Off = the validated options only (GameServerService then gets no
// slot and builds no collector). On = the slot and the sender. Must run after GameServerService is registered (hosted
// services stop in reverse order: the sender first, then the game loop).
public static class MonitoringSetup
{
    public const string LoggerName = "ProjectH.Server.Monitoring";

    // 기능: Monitoring 설정을 읽고 검증해 서비스를 등록한다. 꺼져 있으면 설정만 등록한다.
    // 입력: builder - 호스트 빌더(GameServerService 등록 뒤).
    // 출력: 검증이 끝난 설정. 켜져 있으면 MonitoringSlot(Singleton)과 MonitoringSender(Hosted Service)가 등록된다.
    //       설정이 틀리면 InvalidOperationException(호스트가 시작하지 않는다).
    public static MonitoringOptions Register(HostApplicationBuilder builder)
    {
        MonitoringOptions options = builder.Configuration.GetSection("Monitoring").Get<MonitoringOptions>() ?? new MonitoringOptions();
        string? error = options.Validate();
        if (error != null) throw new InvalidOperationException(error);
        builder.Services.AddSingleton(options);
        if (!options.Enabled) return options;
        builder.Services.AddSingleton<MonitoringSlot>();
        // The container creates the sender and disposes it (and its HttpClient) when the host is disposed.
        builder.Services.AddHostedService(services => new MonitoringSender(options, services.GetRequiredService<MonitoringSlot>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerName)));
        return options;
    }
}
