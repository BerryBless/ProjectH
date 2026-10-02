using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectH.Server.Qa;

// QA-1: the "Qa" section. Read only when QA mode is on (QaMode.Decide); production never binds it.
public sealed class QaOptions
{
    public const int DefaultPort = 7780;
    // D5: at most this many work items wait for the game loop; more are refused with 503.
    public const int QueueCapacity = 64;
    // D5: work items one tick runs at most; the rest wait for the next tick.
    public const int MaxItemsPerTick = 16;
    // §39: the newest events kept (older ones are overwritten and counted as dropped).
    public const int EventCapacity = 1000;
    // D4: largest request body accepted.
    public const int MaxBodyBytes = 16 * 1024;
    // The metrics ring covers at most this many seconds of ticks.
    public const int MetricsWindowSeconds = 120;

    public bool Enabled { get; set; }
    // 0 = any free port (the QA tool's choice; the real port is printed in QA_READY and reported by /qa/health).
    public int Port { get; set; } = DefaultPort;
    // D4: false = 127.0.0.1 only. True binds every interface; only for a deliberate remote setup.
    public bool AllowRemote { get; set; }
    // D6, §150: false = no per-tick diff and no events (benchmarks measure without the observation cost).
    public bool Events { get; set; } = true;
    // D5: how long an HTTP request waits for the game loop to run its work item (then 504).
    public int CommandTimeoutMs { get; set; } = 2000;

    public string? Validate()
    {
        if (Port < 0 || Port > 65535) return "Qa:Port must be 0-65535.";
        if (CommandTimeoutMs < 100 || CommandTimeoutMs > 60000) return "Qa:CommandTimeoutMs must be 100-60000.";
        return null;
    }
}

// The QA mode decision (D3): requested by `--qa-mode`, QA_MODE=true or Qa:Enabled=true, and refused in the Production
// environment, so a release server launched with the flag by mistake never opens the QA port.
public static class QaMode
{
    public const string Flag = "--qa-mode";
    public const string EnvironmentVariable = "QA_MODE";

    // Removes every `--qa-mode` from the arguments. Must run before the host builder sees them: its command-line provider
    // would read a bare `--qa-mode` as a key and take the next argument (e.g. `--Server:Port=0`) as its value.
    public static string[] StripFlag(string[] args, out bool flag)
    {
        flag = false;
        var rest = new List<string>(args.Length);
        foreach (string arg in args)
        {
            if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase)) flag = true;
            else rest.Add(arg);
        }
        return rest.ToArray();
    }

    public static bool EnvironmentRequests(string? value) =>
        value != null && (value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1");

    // requested: any of the three switches. Returns whether QA mode is on; refused = asked for but not allowed here.
    public static bool Decide(bool flag, string? environmentVariable, bool configEnabled, string environmentName, out bool refused)
    {
        bool requested = flag || EnvironmentRequests(environmentVariable) || configEnabled;
        bool production = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        refused = requested && production;
        return requested && !production;
    }
}

// QA-1 D2, D3: what QA mode adds to the host. Nothing at all when off.
public static class QaSetup
{
    // Returns whether QA mode is on. refused: requested in the Production environment (the caller logs it once the host
    // has a logger). Must run after GameServerService is registered (hosted services start in registration order).
    public static bool Register(HostApplicationBuilder builder, bool flag, string? environmentVariable, out bool refused)
    {
        bool on = QaMode.Decide(flag, environmentVariable, builder.Configuration.GetValue<bool>("Qa:Enabled"),
            builder.Environment.EnvironmentName, out refused);
        if (!on) return false;
        QaOptions options = builder.Configuration.GetSection("Qa").Get<QaOptions>() ?? new QaOptions();
        string? error = options.Validate();
        if (error != null) throw new InvalidOperationException(error);
        string environmentName = builder.Environment.EnvironmentName;
        builder.Services.AddSingleton(services => new QaControl(services.GetRequiredService<IOptions<ServerOptions>>().Value, options,
            services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectH.Server.Qa"), environmentName));
        builder.Services.AddHostedService<QaHttpService>();
        return true;
    }
}
