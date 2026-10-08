using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectH.Server.Monitoring;

namespace ProjectH.Server.Tests.Monitoring;

// Request §60: off = nothing registered. The host is built (never started: it would bind the game port).
public class MonitoringSetupTests
{
    // 기능: 등록된 Hosted Service 수를 센다.
    // 입력: b - 호스트 빌더.
    // 출력: ServiceType이 IHostedService인 등록 수.
    private static int HostedServices(HostApplicationBuilder b) => b.Services.Count(d => d.ServiceType == typeof(IHostedService));

    [Fact]
    public void Disabled_RegistersNoSlotAndNoSender()
    {
        HostApplicationBuilder b = ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=false" });
        Assert.DoesNotContain(b.Services, d => d.ServiceType == typeof(MonitoringSlot));
        Assert.NotNull(b.Services.Single(d => d.ServiceType == typeof(MonitoringOptions)));
    }

    [Fact]
    public void Enabled_RegistersTheSlot_AndOneMoreHostedService_AfterTheGameServer()
    {
        HostApplicationBuilder off = ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=false" });
        HostApplicationBuilder on = ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=true", "--Monitoring:ServerId=test-01" });
        Assert.Contains(on.Services, d => d.ServiceType == typeof(MonitoringSlot));
        Assert.Equal(HostedServices(off) + 1, HostedServices(on));
        // Hosted services stop in reverse registration order: the sender (last) stops before GameServerService.
        var hosted = on.Services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        Assert.Equal(typeof(GameServerService), hosted[^2].ImplementationType);
        Assert.Null(hosted[^1].ImplementationType);   // the sender is registered through a factory
    }

    [Fact]
    public void InvalidSettings_StopTheHostAtBuild()
    {
        Assert.Throws<InvalidOperationException>(() => ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=true", "--Monitoring:Endpoint=nope" }));
        Assert.Throws<InvalidOperationException>(() => ServerHost.CreateBuilder(new[] { "--Monitoring:IntervalSeconds=0" }));
    }

    [Fact]
    public void TheShippedSettings_HaveMonitoringOff_AndNoTokenKey()
    {
        string json = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        Assert.Contains("\"Monitoring\"", json);
        Assert.Contains("\"Enabled\": false", json);
        Assert.DoesNotContain("\"Token\"", json);
    }
}
