using System;
using System.Runtime.InteropServices;

namespace ProjectH.Server;

// Windows' default timer granularity (~15.6 ms) makes Thread.Sleep overshoot a 33 ms tick badly.
// Raising it to 1 ms for the lifetime of the game loop thread keeps tick timing close to target.
internal sealed class WindowsTimerResolution : IDisposable
{
    private readonly bool _active;

    // 기능: 해제 때 timeEndPeriod를 부를지 기억하는 핸들을 만든다.
    // 입력: active - timeBeginPeriod(1)이 성공했으면 true.
    // 출력: Dispose할 WindowsTimerResolution.
    private WindowsTimerResolution(bool active)
    {
        _active = active;
    }

    // 기능: Windows면 Timer 해상도를 1ms로 올린다. 다른 OS에서는 아무것도 하지 않는다.
    // 입력: 없음.
    // 출력: Dispose하면 해상도를 되돌리는 핸들(올리지 못했거나 Windows가 아니면 아무것도 되돌리지 않는 핸들).
    public static WindowsTimerResolution Begin()
    {
        if (!OperatingSystem.IsWindows()) return new WindowsTimerResolution(false);
        return new WindowsTimerResolution(timeBeginPeriod(1) == 0);
    }

    // 기능: Begin이 올린 Timer 해상도를 되돌린다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public void Dispose()
    {
        if (_active) timeEndPeriod(1);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
