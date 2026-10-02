using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectH.Server.Qa;
using Xunit;

namespace ProjectH.Server.Tests.Qa;

// QA-1 D3: when QA mode is on, and that `--qa-mode` never reaches the host's command-line provider.
public sealed class QaModeTests
{
    [Fact]
    public void StripFlag_RemovesOnlyTheFlag_AndKeepsTheNextArgument()
    {
        string[] rest = QaMode.StripFlag(new[] { "--qa-mode", "--Server:Port=0", "--Qa:Port=0", "--Persistence:Enabled=false" }, out bool flag);
        Assert.True(flag);
        Assert.Equal(new[] { "--Server:Port=0", "--Qa:Port=0", "--Persistence:Enabled=false" }, rest);

        // A host built from the stripped arguments reads the port (a bare --qa-mode before it would have swallowed it).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = rest });
        Assert.Equal("0", builder.Configuration["Server:Port"]);

        string[] none = QaMode.StripFlag(new[] { "--Server:Port=7777" }, out bool noFlag);
        Assert.False(noFlag);
        Assert.Equal(new[] { "--Server:Port=7777" }, none);
    }

    [Theory]
    // flag, QA_MODE, Qa:Enabled, environment -> on, refused
    [InlineData(false, null, false, "Development", false, false)]
    [InlineData(true, null, false, "Development", true, false)]
    [InlineData(false, "true", false, "Development", true, false)]
    [InlineData(false, "TRUE", false, "Staging", true, false)]
    [InlineData(false, "1", false, "Development", true, false)]
    [InlineData(false, "false", false, "Development", false, false)]
    [InlineData(false, "yes", false, "Development", false, false)]
    [InlineData(false, null, true, "Development", true, false)]
    [InlineData(true, null, false, "Production", false, true)]
    [InlineData(false, "true", false, "Production", false, true)]
    [InlineData(false, null, true, "production", false, true)]
    [InlineData(false, null, false, "Production", false, false)]
    public void Decide_Matrix(bool flag, string? env, bool config, string environment, bool on, bool refused)
    {
        Assert.Equal(on, QaMode.Decide(flag, env, config, environment, out bool wasRefused));
        Assert.Equal(refused, wasRefused);
    }

    [Fact]
    public void Register_Off_AddsNothing_On_AddsTheListener()
    {
        var off = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        int before = off.Services.Count;
        Assert.False(QaSetup.Register(off, flag: false, environmentVariable: null, out _));
        Assert.Equal(before, off.Services.Count);

        var production = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        before = production.Services.Count;
        Assert.False(QaSetup.Register(production, flag: true, environmentVariable: null, out bool refused));
        Assert.True(refused);
        Assert.Equal(before, production.Services.Count);

        var on = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        Assert.True(QaSetup.Register(on, flag: true, environmentVariable: null, out _));
        Assert.Contains(on.Services, d => d.ServiceType == typeof(QaControl));
        Assert.Contains(on.Services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(QaHttpService));
    }

    [Fact]
    public void Options_Validate()
    {
        Assert.Null(new QaOptions().Validate());
        Assert.NotNull(new QaOptions { Port = -1 }.Validate());
        Assert.NotNull(new QaOptions { Port = 70000 }.Validate());
        Assert.NotNull(new QaOptions { CommandTimeoutMs = 10 }.Validate());
    }
}
