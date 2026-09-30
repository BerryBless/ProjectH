using System;
using System.Runtime.InteropServices;

namespace ProjectH.Server;

// Windows' default timer granularity (~15.6 ms) makes Thread.Sleep overshoot a 33 ms tick badly.
// Raising it to 1 ms for the lifetime of the game loop thread keeps tick timing close to target.
internal sealed class WindowsTimerResolution : IDisposable
{
    private readonly bool _active;

    private WindowsTimerResolution(bool active)
    {
        _active = active;
    }

    public static WindowsTimerResolution Begin()
    {
        if (!OperatingSystem.IsWindows()) return new WindowsTimerResolution(false);
        return new WindowsTimerResolution(timeBeginPeriod(1) == 0);
    }

    public void Dispose()
    {
        if (_active) timeEndPeriod(1);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
