using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DS5BtHaptics.App.Ui.Shell;

/// <summary>
/// Dark frame and rounded corners via DwmSetWindowAttribute - WPF has no properties for either.
/// Older Windows versions just return an error code, so no fallback is needed.
/// </summary>
internal static class WindowEffects
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int CloakAttribute = 13;

    private const int CornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Apply once the window has a handle, and again after every theme switch.</summary>
    public static void Apply(Window window, bool dark)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Otherwise a light line would surround a dark window.
        Set(handle, UseImmersiveDarkMode, dark ? 1 : 0);
        Set(handle, WindowCornerPreference, CornerRound);
    }

    /// <summary>
    /// Cloaks the window: it exists and can be measured and placed, but isn't drawn. So the
    /// flyout appears right where it belongs instead of briefly somewhere else.
    /// </summary>
    public static void Cloak(IntPtr handle, bool cloaked) => Set(handle, CloakAttribute, cloaked ? 1 : 0);

    private static void Set(IntPtr handle, int attribute, int value)
    {
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }
}
