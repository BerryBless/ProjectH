using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectH.Server;

// Content root = the build output folder, so appsettings.json is found no matter where
// `dotnet run` is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
builder.Services.AddHostedService<GameServerService>();

await builder.Build().RunAsync();
