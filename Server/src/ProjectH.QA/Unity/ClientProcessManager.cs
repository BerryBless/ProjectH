using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ProjectH.QA;

// QA-4: launches one Unity Development player for a UnityClient actor and ends it. The player is not a console app:
// its log goes to a file (-logFile) next to the report, not to a pipe. Rendering is needed for screenshots, so no
// -batchmode; a small window instead.
// Lifetime: one per launched actor; StopAsync (cleanup) asks the window to close, then kills our own child's process
// tree after a grace. If the QA tool itself dies (crash, hard kill), Windows ends the player through the kill-on-close
// job object it was put in at start (KillOnCloseJob); elsewhere only the normal cleanup closes it.
public sealed class ClientProcessManager : IDisposable
{
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);

    private Process? _process;

    public int? Pid => _process?.Id;
    // Set when the player could not be put in the kill-on-close job (logged by the actor; the run goes on).
    public string? JobWarning { get; private set; }
    public bool HasExited => _process == null || _process.HasExited;

    public string? ExitDescription
    {
        get
        {
            Process? p = _process;
            if (p == null || !p.HasExited) return null;
            try
            {
                return $"exit code {p.ExitCode}";
            }
            catch (InvalidOperationException)
            {
                return "exited";
            }
        }
    }

    // 기능: loopback TCP의 빈 포트를 하나 찾는다(bind 후 바로 놓는다).
    // 입력: 없음.
    // 출력: 포트 번호.
    // A free loopback TCP port for the player's QA receiver (bind, read, release: another process could take it in
    // between; the readiness wait then fails with a clear message).
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // 기능: Unity Development 플레이어의 명령줄 인자를 만든다(자동 접속, QA 수신기, 로그 파일, 창 크기).
    // 입력: host·gamePort - 접속할 서버, devPlayerId - Dev 플레이어 ID, qaPort - QA 수신기 포트, shotDir - 스크린샷 폴더, logFile - 로그 파일 경로, width·height - 창 크기.
    // 출력: 인자 목록.
    public static IReadOnlyList<string> BuildArguments(string host, int gamePort, string devPlayerId, int qaPort, string shotDir, string logFile, int width, int height) =>
        new[]
        {
            "-host", host, "-port", gamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-devId", devPlayerId, "-autoConnect",
            "-qaPort", qaPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-qaShotDir", shotDir,
            "-logFile", logFile,
            "-screen-fullscreen", "0",
            "-screen-width", width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-screen-height", height.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    // 기능: 플레이어를 띄우고 stdout/stderr를 읽어 버리며 kill-on-close job에 넣는다.
    // 입력: exe - 플레이어 실행 파일, arguments - 명령줄 인자.
    // 출력: 반환값 없음. _process가 설정되고 job에 못 넣으면 JobWarning이 남는다. exe가 없거나 시작에 실패하면 QaToolException.
    public void Start(string exe, IReadOnlyList<string> arguments)
    {
        if (!File.Exists(exe)) throw new QaToolException($"Unity player not found: {exe}");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            // The player also prints its start-up lines to stdout; its real log is -logFile. Drain and drop the pipes
            // so they neither flood the QA console nor fill up and block the player.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in arguments) info.ArgumentList.Add(a);
        _process = Process.Start(info) ?? throw new QaToolException($"Could not start {exe}.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // Backstop for a tool that dies without cleanup: the player is in the kill-on-close job (Windows).
        if (!KillOnCloseJob.TryAssign(_process, out string? jobError)) JobWarning = $"not in the kill-on-close job ({jobError}): a hard-killed QA tool would leave this player running";
    }

    // 기능: 창 닫기를 요청하고 CloseGrace 안에 끝나지 않으면 이 자식의 프로세스 트리를 죽인다.
    // 입력: 없음.
    // 출력: Stopped - 프로세스가 끝났으면 true, Message - 정리 과정 설명.
    // Graceful first (the window closes: the client sends its disconnect), then a kill of our own child only.
    public async Task<(bool Stopped, string Message)> StopAsync()
    {
        Process? p = _process;
        if (p == null) return (true, "not started");
        if (p.HasExited) return (true, $"already exited ({ExitDescription})");
        try
        {
            p.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            // Exited meanwhile.
        }
        if (await WaitExitAsync(p, CloseGrace).ConfigureAwait(false)) return (true, "closed");
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!p.HasExited) return (false, $"did not close and kill failed: {e.Message}");
        }
        bool gone = await WaitExitAsync(p, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return gone ? (true, "did not close in time, killed") : (false, $"kill did not end pid {p.Id}");
    }

    // 기능: 아직 살아 있는 자식 프로세스 트리를 죽이고 Process 핸들을 놓는다(크래시 경로의 최후 정리).
    // 입력: 없음.
    // 출력: 반환값 없음. _process가 null이 되고 핸들이 해제된다.
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
            // Crash path; StopAsync reports the normal one.
        }
        p.Dispose();
    }

    // 기능: 프로세스 종료를 timeout 동안 기다린다.
    // 입력: p - 기다릴 프로세스, timeout - 최대 대기 시간.
    // 출력: 시간 안에 끝났으면 true, 넘겼으면 그 시점의 HasExited.
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
}
