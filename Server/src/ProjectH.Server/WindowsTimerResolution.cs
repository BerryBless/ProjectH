using System;
using System.Runtime.InteropServices;

namespace ProjectH.Server;

// Windows' default timer granularity (~15.6 ms) makes Thread.Sleep overshoot a 33 ms tick badly.
// Raising it to 1 ms for the lifetime of the game loop thread keeps tick timing close to target.
internal sealed class WindowsTimerResolution : IDisposable
{
    private readonly bool _active;

    // 기능: Timer 해상도 요청 상태를 기억하는 객체를 만든다. Begin에서만 호출한다.
    // 입력: active - timeBeginPeriod(1)이 성공해 Dispose에서 되돌려야 하면 true.
    // 출력: active 상태를 가진 WindowsTimerResolution 객체.
    private WindowsTimerResolution(bool active)
    {
        _active = active;
    }

    // 기능: Windows에서 System Timer 해상도를 1ms로 올린다. Windows가 아니면 아무것도 하지 않는다.
    // 입력: 없음.
    // 출력: Dispose할 때 해상도를 되돌릴 WindowsTimerResolution 객체. 올리기에 성공했을 때만 활성 상태.
    public static WindowsTimerResolution Begin()
    {
        if (!OperatingSystem.IsWindows()) return new WindowsTimerResolution(false);
        return new WindowsTimerResolution(timeBeginPeriod(1) == 0);
    }

    // 기능: Begin에서 올린 Timer 해상도를 되돌린다.
    // 입력: 없음.
    // 출력: 반환값 없음. 활성 상태였으면 1ms 해상도 요청이 해제된다.
    public void Dispose()
    {
        if (_active) timeEndPeriod(1);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
