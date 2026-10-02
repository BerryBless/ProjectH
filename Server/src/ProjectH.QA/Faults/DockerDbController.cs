using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectH.QA.Faults;

// Result of one `docker` CLI call. NotFound = the executable could not be started (Docker not installed / not on PATH).
public sealed record DockerCommandResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool NotFound)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !NotFound;

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

    // Docker is on PATH, the daemon answers and a container with exactly this name exists.
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var (result, _) = await InspectAsync("{{.Name}}", QueryTimeout, ct).ConfigureAwait(false);
        return result.Ok;
    }

    // Whether the container is running now (name-checked inspect). Result.Ok=false: could not be inspected.
    public async Task<(DockerCommandResult Result, bool Running)> IsRunningAsync(CancellationToken ct = default)
    {
        var (state, running) = await InspectAsync("{{.State.Running}}", QueryTimeout, ct).ConfigureAwait(false);
        return (state, state.Ok && string.Equals(running, "true", StringComparison.OrdinalIgnoreCase));
    }

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

    public async Task<DockerCommandResult> StartAsync(CancellationToken ct = default)
    {
        var (check, _) = await InspectAsync("{{.Name}}", QueryTimeout, ct).ConfigureAwait(false);
        if (!check.Ok) return check;

        var start = await _runner(["start", ContainerName], StartTimeout, ct).ConfigureAwait(false);
        if (start.Ok) StoppedByThisController = false;
        return start;
    }

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
