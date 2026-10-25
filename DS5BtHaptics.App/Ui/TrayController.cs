using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DS5BtHaptics.App.Ui;

/// <summary>
/// The notification area icon. Left click toggles the flyout; right click opens it.
/// </summary>
internal sealed class TrayController : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon notifyIcon;
    private Icon? icon;
    private IntPtr iconHandle;
    private TrayBadge? badge;
    private Rectangle? pressAnchor;

    public TrayController()
    {
        notifyIcon = new NotifyIcon
        {
            Text = "DS5 BT Haptics",
        };
        SetStatus(TrayBadge.Waiting, "DS5 BT Haptics");
        notifyIcon.Visible = true;

        // Capture the icon position before Windows can close the tray overflow.
        notifyIcon.MouseDown += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
            {
                pressAnchor = Flyout.TrayAnchor.Resolve(GetIconBounds());
            }
        };
        notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                FlyoutToggleRequested?.Invoke(pressAnchor ?? Flyout.TrayAnchor.Resolve(GetIconBounds()));
                pressAnchor = null;
            }
            else if (e.Button == MouseButtons.Right)
            {
                FlyoutRequested?.Invoke(pressAnchor ?? Flyout.TrayAnchor.Resolve(GetIconBounds()));
                pressAnchor = null;
            }
        };
    }

    /// <summary>Left click: toggle the flyout, with the spot it should sit above.</summary>
    public event Action<Rectangle?>? FlyoutToggleRequested;

    /// <summary>Right click: show the flyout at the tray icon.</summary>
    public event Action<Rectangle?>? FlyoutRequested;

    /// <summary>
    /// Where the icon currently is, in screen pixels - on the taskbar or in the open overflow (^).
    /// Null if Windows doesn't say, e.g. while the icon sits in the closed overflow.
    ///
    /// NotifyIcon doesn't expose the window and id it registered with; both are private fields.
    /// Looked up by type rather than name, since names and the id type vary between versions.
    /// </summary>
    public Rectangle? GetIconBounds()
    {
        try
        {
            FieldInfo[] fields = typeof(NotifyIcon).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            NativeWindow? native = fields
                .Where(f => typeof(NativeWindow).IsAssignableFrom(f.FieldType))
                .Select(f => f.GetValue(notifyIcon) as NativeWindow)
                .FirstOrDefault(w => w != null);
            object? id = fields
                .FirstOrDefault(f => f.Name.TrimStart('_').Equals("id", StringComparison.OrdinalIgnoreCase))
                ?.GetValue(notifyIcon);
            if (native == null || id == null)
            {
                return null;
            }

            var identifier = new NotifyIconIdentifier
            {
                cbSize = Marshal.SizeOf<NotifyIconIdentifier>(),
                hWnd = native.Handle,
                uID = Convert.ToUInt32(id),
            };
            if (Shell_NotifyIconGetRect(ref identifier, out IconRect r) != 0 || r.Right <= r.Left)
            {
                return null;
            }
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Status dot on the icon and the hover text (Windows cuts it at 127 characters).</summary>
    public void SetStatus(TrayBadge newBadge, string text)
    {
        notifyIcon.Text = text.Length > 127 ? text[..127] : text;
        if (badge == newBadge)
        {
            return;
        }
        badge = newBadge;

        Icon? oldIcon = icon;
        IntPtr oldHandle = iconHandle;
        using (Bitmap bitmap = AppIconArt.Render(32, newBadge))
        {
            iconHandle = bitmap.GetHicon();
            icon = Icon.FromHandle(iconHandle);
        }
        notifyIcon.Icon = icon;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero)
        {
            DestroyIcon(oldHandle);
        }
    }

    public void ShowHint(string title, string message, ToolTipIcon kind = ToolTipIcon.Info)
    {
        notifyIcon.BalloonTipTitle = title;
        notifyIcon.BalloonTipText = message;
        notifyIcon.BalloonTipIcon = kind;
        notifyIcon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        // Without hiding explicitly, the icon lingers until the mouse passes over it.
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        icon?.Dispose();
        if (iconHandle != IntPtr.Zero)
        {
            DestroyIcon(iconHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out IconRect location);
}
