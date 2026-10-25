using System.Runtime.InteropServices;

namespace DS5BtHaptics;

/// <summary>
/// Sub-millisecond waits for background processes. Windows 11 ignores timeBeginPeriod for processes
/// without a visible window, so Thread.Sleep / WaitOne fall back to 15.6 ms ticks, which made the
/// 1-ms isochronous audio completions arrive in bursts and Windows reset the audio endpoint.
/// </summary>
internal static class Timing
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    const uint CreateWaitableTimerHighResolution = 0x2;
    const uint TimerAllAccess = 0x1F0003;

    [ThreadStatic] static IntPtr _timer;

    /// <summary>Opt this process out of EcoQoS and timer-resolution throttling.</summary>
    public static void DisableThrottling()
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4 /*EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION*/, StateMask = 0 };
        SetProcessInformation(GetCurrentProcess(), 4 /*ProcessPowerThrottling*/, ref state, Marshal.SizeOf<PowerThrottlingState>());
        Native.timeBeginPeriod(1);
    }

    /// <summary>Sleeps roughly <paramref name="ms"/> milliseconds with ~0.5 ms precision (high-resolution timer).</summary>
    public static void Sleep(double ms)
    {
        if (ms <= 0) return;
        if (_timer == IntPtr.Zero)
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_timer == IntPtr.Zero) { Thread.Sleep(TimeSpan.FromMilliseconds(ms)); return; } // pre-1803 Windows
        }
        var due = -(long)(ms * 10_000); // relative, 100-ns units
        if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_timer, 0xFFFFFFFF);
    }
}
