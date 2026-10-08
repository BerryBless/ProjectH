using System;
using LiteNetLib;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Diagnostics;

// Review fix A1 follow-up: LiteNetLib reports what it drops (for example a message of more fragments than
// MaxFragmentsCount, "Invalid FragmentsTotal") through NetDebug. With no NetDebug.Logger it writes each one to the console
// synchronously, on the receive thread that serves every connection and inside its own lock, so a connection sending such
// fragments could flood the console and slow everyone's receive. This bridge sends them to ILogger at Debug instead
// (off by default; the console logger queues and drops when full), and formats nothing when Debug is off.
// Lifetime: NetDebug.Logger is process-wide; Program sets it once at startup (tests leave LiteNetLib's default).
public sealed class LiteNetLogBridge : INetLogger
{
    private readonly ILogger _logger;

    // 기능: LiteNetLib 내부 로그를 받을 다리를 만든다.
    // 입력: logger - 내보낼 ILogger(카테고리 "LiteNetLib").
    // 출력: NetDebug.Logger에 넣을 수 있는 LiteNetLogBridge.
    public LiteNetLogBridge(ILogger logger)
    {
        _logger = logger;
    }

    // 기능: LiteNetLib 로그 한 줄을 Debug로 남긴다. Debug가 꺼져 있으면 서식도 만들지 않는다. 예외를 밖으로 내보내지 않는다.
    // 입력: level - LiteNetLib 수준, str - 서식 문자열, args - 서식 인자.
    // 출력: 반환값 없음.
    public void WriteNet(NetLogLevel level, string str, params object[] args)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        try
        {
            string message = args == null || args.Length == 0 ? str : string.Format(str, args);
            _logger.LogDebug("LiteNetLib {Level}: {Message}", level, message);
        }
        catch
        {
            // A bad format string or a throwing logger must not escape into LiteNetLib's receive thread.
        }
    }
}
