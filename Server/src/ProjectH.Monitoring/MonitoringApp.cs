using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProjectH.Monitoring.Api;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Ingest;
using ProjectH.Monitoring.Status;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring;

// Monitoring D7: the whole app, apart from Program so a test can start the same app on a loopback port with its own
// clock. Content root = the build output (appsettings.json and wwwroot are copied there), as in ProjectH.Server.
public static class MonitoringApp
{
    // 기능: 설정을 읽고 검증해 서비스·경로·정적 파일이 붙은 앱을 만든다(시작은 하지 않는다).
    // 입력: args - 명령줄(--Urls=…, --MonitoringServer:키=값), time - 시계(null = 시스템).
    // 출력: Run/StartAsync 전의 WebApplication. 설정이 틀리면 InvalidOperationException.
    public static WebApplication Create(string[] args, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.Configure<MonitoringServerOptions>(builder.Configuration.GetSection("MonitoringServer"));
        builder.Services.AddSingleton(services =>
        {
            MonitoringServerOptions options = services.GetRequiredService<IOptions<MonitoringServerOptions>>().Value;
            string? error = options.Validate();
            if (error != null) throw new InvalidOperationException(error);
            return options;
        });
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton<MetricStore>();
        builder.Services.AddSingleton<IngestLog>();
        builder.Services.AddHostedService<OfflineSweeper>();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        // D8 ①: the ingest body limit. The UI only GETs, so the limit is global.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MonitoringContract.MaxBodyBytes);

        WebApplication app = builder.Build();
        app.Services.GetRequiredService<MonitoringServerOptions>();   // validate now, not at the first request
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapIngest();
        app.MapServersApi();
        return app;
    }
}
