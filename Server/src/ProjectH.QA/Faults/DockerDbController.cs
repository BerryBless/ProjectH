using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectH.QA.Faults;

// Result of one `docker` CLI call. NotFound = the executable could not be started (Docker not installed / not on PATH).
public sealed record DockerCommandResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool NotFound)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !NotFound;

    // 기능: 결과를 로그·오류 메시지용 한 줄로 요약한다.
    // 입력: 없음.
    // 출력: 실행 파일 없음·시간 초과·"exit N: <stderr 또는 stdout>" 중 하나.
    public override string ToString() =>
        NotFound ? "docker not found" :
        TimedOut ? "docker timed out" :
        $"exit {ExitCode}: {(StdErr.Length > 0 ? StdErr.Trim() : StdOut.Trim())}";
}

public readonly record struct DockerHealthResult(bool Healthy, string LastStatus);

// Attempted = this controller had stopped the container and tried to start it again.
public readonly record struct DockerRestoreResult(bool Attempted, bool Ok, string Detail);

// Runs `docker <args>` with a timeout. Injectable so tests can check the arguments and timeout handling without Docker.
public delegate Task<DockerCommandResult> DockerCommandRunner(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

// QA-3 DB fault (D13, request §76·§77): stops and starts ONE configured MySQL container with `docker stop/start`.
// It never runs `down`, `rm` or anything with `-v` — the data volume must survive a QA run. Docker missing -> the
// caller skips the scenario (IsAvailableAsync is false).
//
// Cleanup contract: StopAsync remembers when it tried to stop a running container; RestoreAsync starts it again only in
// that case, so a container a developer stopped on purpose stays stopped. Call RestoreAsync from cleanup with a token
// that is not the (possibly already cancelled) scenario token.
//
// Not thread-safe: one orchestrator flow drives it, one call at a time. Lifetime: one per run; holds no resources
// between calls (every docker process is disposed when its call returns).
public sealed partial class DockerDbController
{
    public const string DefaultContainerName = "projecth-mysql";   // docker-compose.yml container_name
    public const int StopGraceSeconds = 10;

    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    // Must exceed `docker stop --time StopGraceSeconds`, or we would kill the CLI while Docker is still stopping.
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(StopGraceSeconds + 20);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(60);

    // Bounded capture per stream (request §143): docker inspect/stop/start output is tiny; anything past this is drained
    // and discarded so a full pipe cannot block the child.
    internal const int MaxCaptureChars = 16 * 1024;
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(2);

    // `{{if .State.Health}}…` prints the health check status when the container has one, else the plain state.
    internal const string HealthFormat = "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}";

    private readonly DockerCommandRunner _runner;

    // 기능: 컨테이너 이름을 Docker 이름 규칙으로 검증하고 docker CLI 실행기를 정한다.
    // 입력: containerName - 제어할 MySQL 컨테이너 이름, runner - docker 명령 실행기(null이면 실제 docker 프로세스).
    // 출력: 아직 아무 컨테이너도 멈추지 않은 상태의 컨트롤러. 이름이 규칙에 어긋나면 ArgumentException.
    public DockerDbController(string containerName = DefaultContainerName, DockerCommandRunner? runner = null)
    {
        // ArgumentList already prevents shell injection; this also keeps a scenario value from turning into a flag.
        if (string.IsNullOrEmpty(containerName) || !ContainerNamePattern().IsMatch(containerName))
            throw new ArgumentException($"invalid container name: {containerName}", nameof(containerName));
        ContainerName = containerName;
        _runner = runner ?? ((args, timeout, ct) => RunProcessAsync("docker", args, timeout, ct));
    }

    public string ContainerName { get; }

    // True from the moment this controller sends `docker stop` to a running container (even if that call then fails,
    // times out or is cancelled) until a successful StartAsync.
    public bool StoppedByThisController { get; private set; }

    // 기능: Docker가 PATH에 있고 데몬이 응답하며 정확히 이 이름의 컨테이너가 있는지 inspect로 확인한다.
    // 입력: ct - 취소 토큰.
    // 출력: 제어할 수 있으면 true, Docker 없음·컨테이너 없음·시간 초과면 false.
    // Docker is on PATH, the daemon answers and a container with exactly this name exists.
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var (result, _) = await InspectAsync("{{.Name}}", QueryTimeout, ct).ConfigureAwait(false);
        return result.Ok;
    }

    // 기능: 컨테이너가 지금 실행 중인지 이름 검증된 inspect로 확인한다.
    // 입력: ct - 취소 토큰.
    // 출력: inspect 결과와 실행 여부. 결과의 Ok가 false면 조회 자체가 실패한 것이고 Running은 false.
    // Whether the container is running now (name-checked inspect). Result.Ok=false: could not be inspected.
    public async Task<(DockerCommandResult Result, bool Running)> IsRunningAsync(CancellationToken ct = default)
    {
        var (state, running) = await InspectAsync("{{.State.Running}}", QueryTimeout, ct).ConfigureAwait(false);
        return (state, state.Ok && string.Equals(running, "true", StringComparison.OrdinalIgnoreCase));
    }

    // 기능: 컨테이너가 실행 중이면 `docker stop --time StopGraceSeconds`로 멈추고, 멈춤을 시도했음을 StoppedByThisController에 기록한다.
    // 입력: ct - 취소 토큰.
    // 출력: 실행 중이었으면 stop 명령 결과, 실행 중이 아니었거나 inspect에 실패했으면 그 inspect 결과(이때 기록은 바뀌지 않음).
    // Stops the container if it is running. Returns the stop result, or the inspect result when it was not running
    // (Ok, nothing recorded) or could not be inspected.
    public async Task<DockerCommandResult> StopAsync(CancellationToken ct = default)
    {
        var (state, running) = await InspectAsync("{{.State.Running}}", QueryTimeout, ct).ConfigureAwait(false);
        if (!state.Ok) return state;
        if (!string.Equals(running, "true", StringComparison.OrdinalIgnoreCase)) return state;

        // Recorded before `docker stop` runs: if the CLI times out or the token is cancelled, Docker may still finish
        // stopping the container, and cleanup must then start it again. A `docker start` on a container that is in
        // fact still running is harmless.
        StoppedByThisController = true;
        return await _runner(["stop", "--time", StopGraceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), ContainerName], StopTimeout, ct)
            .ConfigureAwait(false);
    }

    // 기능: 컨테이너 이름을 inspect로 확인한 뒤 `docker start`로 시작하고, 성공하면 StoppedByThisController 기록을 지운다.
    // 입력: ct - 취소 토큰.
    // 출력: start 명령 결과. inspect에 실패하면 그 inspect 결과.
    public async Task<DockerCommandResult> StartAsync(CancellationToken ct = default)
    {
        var (check, _) = await InspectAsync("{{.Name}}", QueryTimeout, ct).ConfigureAwait(false);
        if (!check.Ok) return check;

        var start = await _runner(["start", ContainerName], StartTimeout, ct).ConfigureAwait(false);
        if (start.Ok) StoppedByThisController = false;
        return start;
    }

    // 기능: 컨테이너 health 상태를 HealthPollInterval 간격으로 폴링해 "healthy"(health check가 없으면 "running")가 될 때까지 기다린다.
    // 입력: timeout - 전체 대기 한도, ct - 취소 토큰.
    // 출력: 건강해지면 Healthy=true와 그 상태, 시간이 다 되면 Healthy=false와 마지막 상태(또는 inspect 오류 요약). Docker가 없으면 즉시 false.
    // Polls the health status until "healthy" (or "running" for a container without a health check) or the timeout.
    // "starting" and a transient "unhealthy" keep polling; the last status is returned either way.
    public async Task<DockerHealthResult> WaitHealthyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        string last = "unknown";
        while (true)
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return new DockerHealthResult(false, last);

            var perCall = remaining < QueryTimeout ? remaining : QueryTimeout;
            var (result, status) = await InspectAsync(HealthFormat, perCall, ct).ConfigureAwait(false);
            if (result.NotFound) return new DockerHealthResult(false, "docker not found");
            if (result.Ok)
            {
                last = status;
                if (last is "healthy" or "running") return new DockerHealthResult(true, last);
            }
            else
            {
                last = result.ToString();
            }

            remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return new DockerHealthResult(false, last);
            await Task.Delay(remaining < HealthPollInterval ? remaining : HealthPollInterval, ct).ConfigureAwait(false);
        }
    }

    // 기능: `docker inspect`로 컨테이너의 한 필드를 읽되, 출력의 이름이 정확히 "/" + ContainerName일 때만 인정한다.
    // 입력: field - Go template 필드 식("{{.State.Running}}" 등), timeout - 명령 시간 한도, ct - 취소 토큰.
    // 출력: 명령 결과와 필드 값. 명령 실패나 다른 컨테이너로 해석됐으면(ExitCode 1로 바꿔) 빈 값.
    // `docker inspect <name>` also matches a container ID prefix, so a missing all-hex name could resolve to another
    // container. Every inspect therefore prints "{{.Name}}|<field>" and is refused (ExitCode 1) unless the name is
    // exactly "/" + ContainerName. Returns the field value on success.
    private async Task<(DockerCommandResult Result, string Value)> InspectAsync(string field, TimeSpan timeout, CancellationToken ct)
    {
        var result = await _runner(["inspect", "--type", "container", "--format", "{{.Name}}|" + field, ContainerName], timeout, ct)
            .ConfigureAwait(false);
        if (!result.Ok) return (result, "");

        var text = result.StdOut.Trim();
        int bar = text.IndexOf('|');
        var name = bar < 0 ? text : text[..bar];
        if (!string.Equals(name, "/" + ContainerName, StringComparison.Ordinal))
            return (result with { ExitCode = 1, StdErr = $"'{ContainerName}' resolved to another container ({name})" }, "");
        return (result, bar < 0 ? "" : text[(bar + 1)..].Trim());
    }

    // 기능: 정리 단계에서 이 컨트롤러가 멈춘 컨테이너만 다시 시작하고 건강해질 때까지 기다린다.
    // 입력: healthTimeout - health 대기 한도, ct - 취소 토큰(시나리오 토큰이 아닌 정리용 토큰).
    // 출력: 멈춘 적이 없으면 Attempted=false·Ok=true, 시도했으면 start·health 결과에 따른 Ok와 상세 메시지.
    // Cleanup: starts the container again only if this controller stopped it, then waits until it is healthy.
    public async Task<DockerRestoreResult> RestoreAsync(TimeSpan healthTimeout, CancellationToken ct = default)
    {
        if (!StoppedByThisController) return new DockerRestoreResult(false, true, "not stopped by this controller");

        var start = await StartAsync(ct).ConfigureAwait(false);
        if (!start.Ok) return new DockerRestoreResult(true, false, $"docker start failed: {start}");

        var health = await WaitHealthyAsync(healthTimeout, ct).ConfigureAwait(false);
        return health.Healthy
            ? new DockerRestoreResult(true, true, $"started, {health.LastStatus}")
            : new DockerRestoreResult(true, false, $"started but not healthy: {health.LastStatus}");
    }

    // 기능: 외부 프로세스를 인자 목록으로 실행하고 stdout/stderr를 상한까지 받으며 종료를 기다린다. 시간 초과·취소 시 그 프로세스 트리만 죽인다.
    // 입력: fileName - 실행 파일("docker"), args - 인자 목록, timeout - 종료 대기 한도, ct - 취소 토큰.
    // 출력: 종료 코드·출력·시간 초과 여부가 담긴 결과. 실행 파일을 시작하지 못하면 NotFound=true. ct가 취소됐으면 kill 후 OperationCanceledException.
    // Default runner. On timeout or cancellation it kills only the process it started (and that process's own
    // children) and never touches other docker processes. Cancellation of `ct` is rethrown after the kill.
    internal static async Task<DockerCommandResult> RunProcessAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new DockerCommandResult(-1, "", $"{fileName} did not start", false, true);
        }
        catch (Win32Exception e)
        {
            return new DockerCommandResult(-1, "", e.Message, false, true);
        }

        // Both streams are drained concurrently from the start; a child blocked on a full pipe would never exit.
        var stdOut = ReadBoundedAsync(process.StandardOutput, MaxCaptureChars);
        var stdErr = ReadBoundedAsync(process.StandardError, MaxCaptureChars);

        bool timedOut = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                Kill(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillWait).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
        }

        string output = await FinishReadAsync(stdOut).ConfigureAwait(false);
        string error = await FinishReadAsync(stdErr).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        int exitCode = timedOut || !process.HasExited ? -1 : process.ExitCode;
        return new DockerCommandResult(exitCode, output, error, timedOut, false);
    }

    // 기능: 프로세스와 그 자식 트리를 강제 종료하며, 이미 끝났거나 접근이 거부된 경우는 무시한다.
    // 입력: process - 이 컨트롤러가 시작한 프로세스.
    // 출력: 반환값 없음. 프로세스 트리가 종료된다.
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exiting or access denied; nothing more we can do for our own child.
        }
    }

    // 기능: 스트림 읽기 Task가 끝나기를 KillWait까지만 기다린다.
    // 입력: read - 진행 중인 출력 읽기 Task.
    // 출력: 읽힌 텍스트. 제한 시간 안에 끝나지 않으면 빈 문자열.
    private static async Task<string> FinishReadAsync(Task<string> read)
    {
        try
        {
            return await read.WaitAsync(KillWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "";
        }
    }

    // 기능: 스트림을 끝까지 읽어 비우되 처음 maxChars 문자만 보관한다(파이프가 가득 차 자식이 막히지 않게).
    // 입력: reader - 자식 프로세스의 stdout 또는 stderr, maxChars - 보관할 최대 문자 수.
    // 출력: 보관된 텍스트. IO 오류·스트림 폐기 시 그때까지 읽은 내용.
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                int keep = Math.Min(read, maxChars - text.Length);
                if (keep > 0) text.Append(buffer, 0, keep);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        return text.ToString();
    }

    // Docker's container name rule: [a-zA-Z0-9][a-zA-Z0-9_.-]+
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex ContainerNamePattern();
}
