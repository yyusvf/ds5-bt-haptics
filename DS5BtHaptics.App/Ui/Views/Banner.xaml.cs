using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace DS5BtHaptics.App.Ui.Views;

internal enum NotificationPosition
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>
/// Notifications in ysfUtil's style (taken from its AudioSwitchBanner): no focus, click-through,
/// fades out after a moment. Because it can't be clicked, messages say where to go instead.
/// </summary>
public partial class Banner : Window
{
    public const string ControllerGlyph = "";
    public const string BatteryGlyph = "";
    public const string DownloadGlyph = "";
    public const string WarningGlyph = "";
    public const string ReadyGlyph = "";
    public const string PowerGlyph = "";

    private readonly DispatcherTimer timeout = new();
    private int revision;

    internal NotificationPosition Position { get; set; } = NotificationPosition.BottomRight;

    public Banner()
    {
        InitializeComponent();
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            int current = ++revision;
            Fade(0, 180, () => { if (current == revision) Hide(); });
        };
        Closed += (_, _) => timeout.Stop();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Layered + WS_EX_TRANSPARENT passes mouse input to windows in other processes;
            // WPF IsHitTestVisible alone only disables input inside the WPF visual tree.
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x080000A0L));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x0084) { handled = true; return new IntPtr(-1); } // WM_NCHITTEST: HTTRANSPARENT
                return IntPtr.Zero;
            });
        };
    }

    /// <param name="seconds">ysfUtil shows device switches for 1.5 s; warnings with more to read stay longer.</param>
    internal void ShowMessage(string title, string message, string glyph, double seconds = 1.5)
    {
        ++revision;
        timeout.Stop();
        timeout.Interval = TimeSpan.FromSeconds(seconds);
        BannerTitle.Text = title;
        Glyph.Text = glyph;
        Message.Text = message;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int marginX = (int)Math.Round(16 * dpi.DpiScaleX);
        int marginY = (int)Math.Round(16 * dpi.DpiScaleY);
        bool onLeft = Position is NotificationPosition.BottomLeft or NotificationPosition.TopLeft;
        bool onTop = Position is NotificationPosition.TopLeft or NotificationPosition.TopRight;
        int left = onLeft ? area.Left + marginX
            : area.Right - (int)Math.Round(ActualWidth * dpi.DpiScaleX) - marginX;
        int top = onTop ? area.Top + marginY
            : area.Bottom - (int)Math.Round(ActualHeight * dpi.DpiScaleY) - marginY;
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            left, top, 0, 0, 0x0015);
        Fade(1, 160);
        timeout.Start();
    }

    private void Fade(double opacity, int milliseconds, Action? completed = null)
    {
        double from = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = opacity;
        if (!SystemParameters.ClientAreaAnimation) { completed?.Invoke(); return; }
        var animation = new DoubleAnimation(from, opacity, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) => completed?.Invoke();
        BeginAnimation(OpacityProperty, animation);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
