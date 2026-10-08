using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProjectH.QA;

// Last N lines of a text stream (request §143: no unbounded log buffer). Lines arrive on the Process's reader threads
// and are read by the run flow, so a lock guards the queue. Lock: this one only, never nested, nothing called inside
// it but Queue operations (no deadlock possible).
public sealed class LogRing
{
    public const int DefaultCapacity = 2000;
    private const int MaxLineLength = 4000;

    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private long _dropped;

    public LogRing(int capacity = DefaultCapacity)
    {
        _capacity = capacity;
    }

    public void Add(string line)
    {
        if (line.Length > MaxLineLength) line = line[..MaxLineLength] + "...";
        lock (_gate)
        {
            if (_lines.Count >= _capacity)
            {
                _lines.Dequeue();
                _dropped++;
            }
            _lines.Enqueue(line);
        }
    }

    public string[] Tail(int count)
    {
        lock (_gate)
        {
            return _lines.Skip(Math.Max(0, _lines.Count - count)).ToArray();
        }
    }

    public long Dropped
    {
        get { lock (_gate) return _dropped; }
    }
}

// Launch mode (D12): runs `dotnet ProjectH.Server.dll` with QA mode on, free ports and the scenario's seeds, waits for
// the `QA_READY gamePort=… qaPort=…` line and then for /qa/health (never a fixed sleep, request §81), and stops it at
// the end: POST /qa/server/stop, wait for exit, and only then kill — only this child's process tree.
// Lifetime: one per scenario run, created and disposed by the orchestrator. Dispose kills the child if it still runs
// (a tool crash path) and releases the Process handle.
public sealed partial class ServerProcessManager : IDisposable
{
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private readonly Action<string>? _liveLog;
    private readonly TaskCompletionSource<(int Game, int Qa)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;

    // log: a ring shared with an earlier process of the same run (a restart keeps one server log); null = a new one.
    public ServerProcessManager(Action<string>? liveLog, LogRing? log = null)
    {
        _liveLog = liveLog;
        Log = log ?? new LogRing();
    }

    public LogRing Log { get; }
    public int? Pid => _process?.Id;
    public bool HasExited => _process == null || _process.HasExited;
    // D41: the exit code once the process has exited (null while it runs or when it cannot be read).
    public int? ExitCode => _process != null && _process.HasExited ? ExitCodeOrNull(_process) : null;

    [GeneratedRegex(@"QA_READY gamePort=(\d+) qaPort=(\d+)")]
    private static partial Regex ReadyLine();

    // 기능: QA가 띄우는 서버의 명령줄 인자를 만든다(QA 기본값 위에 시나리오 override, 마지막에 부모 PID).
    // 입력: dllPath - 서버 DLL, seed - Loot·Zone·Spawn 시드, overrides - 시나리오가 바꾸는 설정.
    // 출력: dotnet에 넘길 인자 목록.
    public static IReadOnlyList<string> BuildArguments(string dllPath, int seed, IReadOnlyDictionary<string, string> overrides)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Server:Port"] = "0",
            ["Qa:Port"] = "0",
            ["Persistence:Enabled"] = "false",
            ["Server:AirDrop"] = "false",
            ["Server:StartCountdownSeconds"] = "1",
            ["Server:ResultSeconds"] = "1",
            ["Server:LootSeed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Server:ZoneSeed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Server:SpawnSeed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Every QA actor connects from 127.0.0.1, so the server's per-IP connect limit (default burst 20) would refuse a
            // 50-100 player stress join. A burst larger than any scenario's joins and reconnects keeps the limiter on
            // (its path still runs) without refusing them; a scenario may still override it.
            ["Server:ConnectBurstPerIp"] = "1000",
            // Review fix A2: the same for the connections one IP holds at once (default 4) and for the accepts per second
            // over all addresses (default 20, burst MaxPlayers): both stay on, above any scenario's joins and reconnects.
            ["Server:MaxConnectionsPerIp"] = "1000",
            ["Server:AcceptsPerSecond"] = "1000",
        };
        foreach (var pair in overrides) options[pair.Key] = pair.Value;
        // Backstop for every launch path (CLI and UI): the server's QA watchdog stops it when this process is gone, so a
        // tool that dies without cleanup (killed, crashed) leaves no server behind. Set after the overrides: not optional.
        options["Qa:ParentPid"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var args = new List<string> { dllPath, "--qa-mode" };
        foreach (var pair in options) args.Add($"--{pair.Key}={pair.Value}");
        return args;
    }

    // Starts the server and returns its ports once the QA API answers healthy.
    public async Task<(int GamePort, int QaPort)> StartAsync(string dllPath, int seed, IReadOnlyDictionary<string, string> overrides,
        Func<Uri, IQaServerClient> clientFactory, CancellationToken token)
    {
        if (!File.Exists(dllPath))
            throw new QaToolException($"Server build not found: {dllPath}. Build it first: dotnet build Server/ProjectH.Server.slnx (or pass --server-dll).");

        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dllPath)!,
        };
        foreach (string a in BuildArguments(dllPath, seed, overrides)) info.ArgumentList.Add(a);
        // D3: QA mode only turns on outside Production; `dotnet X.dll` defaults to Production.
        info.Environment["DOTNET_ENVIRONMENT"] = "Development";

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, true);
        process.Exited += (_, _) => _ready.TrySetException(new QaToolException("The server exited before it was ready."));
        if (!process.Start()) throw new QaToolException("Could not start the server process.");
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(ReadyTimeout);
        (int game, int qa) ports;
        try
        {
            ports = await _ready.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new QaToolException($"The server printed no QA_READY line within {ReadyTimeout.TotalSeconds:0} s (is QA mode enabled?).");
        }

        IQaServerClient client = clientFactory(new Uri($"http://127.0.0.1:{ports.qa}/"));
        try
        {
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new QaToolException("The server exited before /qa/health answered.");
                try
                {
                    JsonElementHelpers.RequireOk(await client.GetHealthAsync(cts.Token).ConfigureAwait(false));
                    return ports;
                }
                catch (QaApiException)
                {
                    // Not listening yet: keep polling until the ready deadline.
                }
                await Task.Delay(100, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new QaToolException($"/qa/health did not answer ok within {ReadyTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    // Graceful stop first; kill our own child only after StopGrace. Returns a cleanup line; Stopped=false means the
    // process could not be ended (the caller turns that into exit code 2, D18).
    public async Task<(bool Stopped, string Message)> StopAsync(IQaServerClient? client)
    {
        Process? p = _process;
        if (p == null) return (true, "not started");
        // On Ctrl+C the child (same console) may already be gone: that is a stopped server, not a failure.
        if (p.HasExited) return (true, $"already exited (code {SafeExitCode(p)})");
        string how = "stop requested";
        if (client != null)
        {
            try
            {
                using var stopCts = new CancellationTokenSource(QaServerClient.RequestTimeout);
                await client.StopServerAsync(stopCts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (p.HasExited) return (true, $"exited (code {SafeExitCode(p)})");
                how = $"stop request failed ({e.Message})";
            }
        }
        if (await WaitExitAsync(p, StopGrace).ConfigureAwait(false)) return (true, $"{how}; exited (code {SafeExitCode(p)})");
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            if (!p.HasExited) return (false, $"{how}; did not exit in {StopGrace.TotalSeconds:0} s and kill failed: {e.Message}");
        }
        bool gone = await WaitExitAsync(p, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return gone ? (true, $"{how}; did not exit in {StopGrace.TotalSeconds:0} s, killed") : (false, $"{how}; kill did not end pid {p.Id}");
    }

    // Scenario step (request §129): POST /qa/server/stop and measure until the process is gone. The bound is
    // StopGrace; past it our own child is killed and the result says so (Exited=false: the graceful path failed).
    public async Task<Faults.ServerExitInfo> ShutdownAsync(IQaServerClient client, TimeSpan grace, CancellationToken token)
    {
        Process p = _process ?? throw new QaStepException("The server was not started by the tool.");
        if (p.HasExited) return new Faults.ServerExitInfo(true, 0, ExitCodeOrNull(p), false, "already exited");
        var clock = Stopwatch.StartNew();
        string how = "stop requested";
        try
        {
            await client.StopServerAsync(token).ConfigureAwait(false);
        }
        catch (QaApiException e)
        {
            // The response may be lost when the server shuts its HTTP side first; the exit decides.
            how = $"stop request: {e.Message}";
        }
        if (await WaitExitAsync(p, grace, token).ConfigureAwait(false))
            return new Faults.ServerExitInfo(true, clock.ElapsedMilliseconds, ExitCodeOrNull(p), false, how);
        Faults.ServerExitInfo killed = await KillAsync(token).ConfigureAwait(false);
        return killed with { Exited = false, ExitMs = clock.ElapsedMilliseconds, Message = $"{how}; no exit within {grace.TotalSeconds:0} s, {killed.Message}" };
    }

    // Crash test or the end of a failed stop: kills this child's process tree only (never another server).
    public async Task<Faults.ServerExitInfo> KillAsync(CancellationToken token)
    {
        Process p = _process ?? throw new QaStepException("The server was not started by the tool.");
        if (p.HasExited) return new Faults.ServerExitInfo(true, 0, ExitCodeOrNull(p), false, "already exited");
        var clock = Stopwatch.StartNew();
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!p.HasExited) return new Faults.ServerExitInfo(false, clock.ElapsedMilliseconds, null, false, $"kill failed: {e.Message}");
        }
        bool gone = await WaitExitAsync(p, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        return new Faults.ServerExitInfo(gone, clock.ElapsedMilliseconds, gone ? ExitCodeOrNull(p) : null, true, gone ? "killed" : $"kill did not end pid {p.Id}");
    }

    private static async Task<bool> WaitExitAsync(Process p, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return p.HasExited;
        }
    }

    private static int? ExitCodeOrNull(Process p)
    {
        try
        {
            return p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Process? p = _process;
        _process = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best effort on the crash path; StopAsync reports the normal path.
        }
        p.Dispose();
    }

    private static async Task<bool> WaitExitAsync(Process p, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return p.HasExited;
        }
    }

    private static string SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private void OnLine(string? line, bool error)
    {
        if (line == null) return;
        Log.Add(error ? "[err] " + line : line);
        _liveLog?.Invoke(line);
        if (!_ready.Task.IsCompleted)
        {
            Match m = ReadyLine().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int game) && int.TryParse(m.Groups[2].Value, out int qa))
                _ready.TrySetResult((game, qa));
        }
    }
}

// The tool itself cannot do its job (bad input file, server would not start, internal error): exit code 2 (D18).
public sealed class QaToolException : Exception
{
    public QaToolException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class JsonElementHelpers
{
    public static void RequireOk(System.Text.Json.JsonElement health)
    {
        if (JsonPath.Child(health, "ok") is { ValueKind: System.Text.Json.JsonValueKind.False })
            throw new QaApiException("/qa/health answered ok=false");
    }
}
