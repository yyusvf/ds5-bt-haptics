using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DS5BtHaptics.App.Ui.Flyout;

/// <summary>
/// What the flyout should sit above: the tray icon - or, if it is in the open overflow (^), the
/// whole overflow window, so the flyout sits on top of it like Razer Synapse does.
///
/// Must be resolved while the overflow is still open, i.e. on mouse down rather than mouse up.
/// </summary>
internal static class TrayAnchor
{
    private const uint GaRoot = 2;

    public static Rectangle? Resolve(Rectangle? icon)
    {
        if (icon is not Rectangle r)
        {
            return null;
        }

        var centre = new Point32 { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };
        IntPtr host = GetAncestor(WindowFromPoint(centre), GaRoot);
        if (host != IntPtr.Zero && !IsTaskbar(host) && GetWindowRect(host, out Rect32 popup))
        {
            return Rectangle.FromLTRB(popup.Left, popup.Top, popup.Right, popup.Bottom);
        }
        return r;
    }

    private static bool IsTaskbar(IntPtr window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int capacity);
}
