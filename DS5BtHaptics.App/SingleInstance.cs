using System.Runtime.InteropServices;

namespace DS5BtHaptics.App;

/// <summary>
/// Ensures only one DS5 BT Haptics runs. A second start notifies the first one, which then opens its
/// flyout, and exits.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\DS5BtHaptics.SingleInstance";
    private static readonly IntPtr BroadcastWindow = new(0xFFFF);

    private static Mutex? mutex;

    /// <summary>Window message a second instance sends to request the flyout.</summary>
    public static uint ShowWindowMessage { get; } = RegisterWindowMessage("DS5BtHaptics.ShowWindow");

    public static bool TryAcquire()
    {
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
            return created;
        }
        catch
        {
            return true;
        }
    }

    public static void SignalExistingInstance()
    {
        if (ShowWindowMessage != 0)
        {
            PostMessage(BroadcastWindow, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void Release()
    {
        try
        {
            mutex?.ReleaseMutex();
        }
        catch
        {
            // Not critical on exit.
        }
        mutex?.Dispose();
        mutex = null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
