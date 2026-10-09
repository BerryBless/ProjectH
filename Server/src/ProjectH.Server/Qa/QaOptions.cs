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
    // QA-3: the process id of the QA tool that launched this server (0 = none). When that process is gone the server
    // stops itself, so a tool killed hard never leaves an orphan server holding its ports.
    public int ParentPid { get; set; }

    // 기능: Qa 설정 값의 범위를 검사한다.
    // 입력: 없음.
    // 출력: 첫 문제의 메시지, 모두 올바르면 null.
    public string? Validate()
    {
        if (Port < 0 || Port > 65535) return "Qa:Port must be 0-65535.";
        if (CommandTimeoutMs < 100 || CommandTimeoutMs > 60000) return "Qa:CommandTimeoutMs must be 100-60000.";
        if (ParentPid < 0) return "Qa:ParentPid must be 0 (none) or a process id.";
        return null;
    }
}

// The QA mode decision (D3): requested by `--qa-mode`, QA_MODE=true or Qa:Enabled=true, and refused in the Production
// environment, so a release server launched with the flag by mistake never opens the QA port.
public static class QaMode
{
    public const string Flag = "--qa-mode";
    public const string EnvironmentVariable = "QA_MODE";

    // 기능: 인자에서 `--qa-mode`를 모두 빼고 있었는지 알린다.
    // 입력: args - 명령줄 인자, flag - 플래그가 하나라도 있었는지.
    // 출력: 플래그를 뺀 인자 배열과 flag.
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

    // 기능: 환경 변수 값이 QA 모드를 요청하는지 본다("true" 또는 "1", 대소문자·앞뒤 공백 무시).
    // 입력: value - QA_MODE 값(없으면 null).
    // 출력: 요청하면 true.
    public static bool EnvironmentRequests(string? value) =>
        value != null && (value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1");

    // 기능: 세 스위치 중 하나라도 요청했고 Production이 아니면 QA 모드를 켠다.
    // 입력: flag - `--qa-mode` 여부, environmentVariable - QA_MODE 값, configEnabled - Qa:Enabled, environmentName - 호스트 환경 이름, refused - Production에서 요청됐는지.
    // 출력: QA 모드가 켜지면 true.
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
    // 기능: QA 모드면 Qa 설정을 읽어 검증하고 QaControl 싱글톤과 QaHttpService를 호스트에 등록한다.
    // 입력: builder - 호스트 빌더, flag - `--qa-mode` 여부, environmentVariable - QA_MODE 값, refused - Production에서 요청됐는지.
    // 출력: QA 모드가 켜지면 true(서비스 등록됨), 아니면 false(아무것도 등록하지 않음). 설정이 잘못되면 InvalidOperationException.
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
        builder.Services.AddSingleton(services =>
        {
            var qa = new QaControl(services.GetRequiredService<IOptions<ServerOptions>>().Value, options,
                services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectH.Server.Qa"), environmentName);
            // QA-3: the database writer's totals and queue, for /qa/metrics and /qa/health (any thread: Interlocked and the
            // channel's own count).
            bool persistence = services.GetRequiredService<IOptions<Persistence.PersistenceOptions>>().Value.Enabled;
            var writer = services.GetRequiredService<Persistence.MatchHistoryWriter>();
            var queue = services.GetRequiredService<Persistence.MatchHistoryQueue>();
            qa.Database = () =>
            {
                Persistence.PersistenceCounts c = writer.Counts;
                return new QaDbStatus(persistence, c.Saved, c.Failed, c.Discarded, c.Dropped, queue.Count);
            };
            return qa;
        });
        builder.Services.AddHostedService<QaHttpService>();
        return true;
    }
}
