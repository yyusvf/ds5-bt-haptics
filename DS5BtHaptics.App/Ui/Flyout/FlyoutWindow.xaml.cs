using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DS5BtHaptics.App.Ui.Shell;
using DS5BtHaptics.App.Ui.Theme;
using DS5BtHaptics.App.Ui.ViewModels;
using Screen = System.Windows.Forms.Screen;
using WinFormsCursor = System.Windows.Forms.Cursor;

namespace DS5BtHaptics.App.Ui.Flyout;

/// <summary>
/// The panel above the tray icon and the app's only UI: no taskbar entry, no Alt+Tab entry,
/// and it disappears as soon as you click elsewhere.
/// </summary>
public partial class FlyoutWindow : Window
{
    /// <summary>
    /// After hiding, a click on the tray icon does not reopen within this time. Clicking the icon
    /// first takes focus from the flyout (which closes it) and only then reaches the icon -
    /// without this guard it would open again right away.
    /// </summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private const double EdgeMargin = 12;

    private DateTime hiddenAt = DateTime.MinValue;

    /// <summary>
    /// What the flyout was placed above when last opened (see <see cref="TrayAnchor"/>). Kept
    /// rather than recomputed on resize, so the flyout doesn't jump when the overflow closes.
    /// </summary>
    private System.Drawing.Rectangle? anchor;

    /// <summary>
    /// While the flyout is open but not active, Windows sends no Deactivated, so a click
    /// elsewhere wouldn't close it. This timer checks which window is in front instead.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer outsideWatch =
        new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>
    /// The window in front when opening: the overflow or the taskbar. Once focus moves away from
    /// it - click elsewhere, overflow closed - the flyout closes too.
    /// </summary>
    private IntPtr openedFrom;
    private bool hiding;
    private bool isTrayPositioned;
    private int fadeRevision;
    private Screen? placementScreen;

    internal FlyoutWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;

        outsideWatch.Tick += (_, _) => CloseIfClickedOutside();
        Activated += (_, _) => outsideWatch.Stop();

        SourceInitialized += (_, _) =>
        {
            ApplyWindowEffects();
            HideFromAltTab();
        };
        ThemeManager.Changed += ApplyWindowEffects;
        Loaded += (_, _) => LockContentHeight();

        Deactivated += (_, _) => HideFlyout();
        DpiChanged += (_, _) =>
        {
            if (IsVisible && isTrayPositioned)
            {
                UpdateHeightLimit();
                PlaceAtTray();
            }
        };

        // Win+Up would maximize a flyout too.
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }
        };
    }

    /// <summary>Right before showing - status is re-read then.</summary>
    internal event Action? Opening;

    internal event Action? ExitRequested;

    /// <summary>For a left click on the tray icon: open if closed, and vice versa.</summary>
    public void Toggle(System.Drawing.Rectangle? trayAnchor)
    {
        if (IsVisible)
        {
            HideFlyout();
        }
        else if (DateTime.UtcNow - hiddenAt > ReopenGuard)
        {
            ShowFlyout(trayAnchor);
        }
    }

    /// <summary>
    /// Shows the flyout above <paramref name="trayAnchor"/>.
    ///
    /// From the tray icon without taking focus, like Razer Synapse: taking focus would make
    /// Windows close the overflow, leaving the flyout above an empty spot. It becomes active
    /// with the first click into it.
    /// </summary>
    public void ShowFlyout(System.Drawing.Rectangle? trayAnchor, bool settings = false, bool activate = false)
    {
        bool resumeFade = hiding;
        hiding = false;
        Root.IsHitTestVisible = true;
        ++fadeRevision; // Invalidate a pending fade-out's completion before reopening.
        Opening?.Invoke();
        SwitchView(settings);

        if (IsVisible)
        {
            if (resumeFade) FadeWindow(1, 160);
            Activate();
            return;
        }

        anchor = trayAnchor;
        placementScreen = anchor is { } a ? Screen.FromRectangle(a) : Screen.FromPoint(WinFormsCursor.Position);

        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        UpdateHeightLimit();
        // Fit the main content once per opening, then keep both views at that exact height.
        MainView.Height = double.NaN;
        WindowEffects.Cloak(handle, true);
        BeginAnimation(OpacityProperty, null);
        Opacity = SystemParameters.ClientAreaAnimation ? 0 : 1;
        ShowActivated = activate;
        Show();
        UpdateLayout();
        LockContentHeight();
        PlaceAtTray();
        isTrayPositioned = true;
        WindowEffects.Cloak(handle, false);
        FadeWindow(1, 160);

        if (activate)
        {
            Activate();
        }
        else
        {
            openedFrom = GetForegroundWindow();
            outsideWatch.Start();
        }
    }

    public void HideFlyout()
    {
        outsideWatch.Stop();
        if (!IsVisible || hiding)
        {
            return;
        }
        hiddenAt = DateTime.UtcNow;
        hiding = true;
        Root.IsHitTestVisible = false;
        FadeWindow(0, 110, () =>
        {
            isTrayPositioned = false;
            Hide();
            hiding = false;
            hiddenAt = DateTime.UtcNow;
            Root.IsHitTestVisible = true;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        });
    }

    private void FadeWindow(double target, int milliseconds, Action? completed = null)
    {
        int revision = ++fadeRevision;
        double from = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = target;
        if (!SystemParameters.ClientAreaAnimation)
        {
            completed?.Invoke();
            return;
        }
        var fade = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            if (revision == fadeRevision) completed?.Invoke();
        };
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateHeightLimit()
    {
        var screen = placementScreen ?? Screen.FromPoint(WinFormsCursor.Position);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        MaxHeight = Math.Max(MinHeight, Math.Min(560, screen.WorkingArea.Height / scale - 2 * EdgeMargin));
    }

    private void LockContentHeight()
    {
        if (MainView.ActualHeight <= 0) return;
        // Freeze the content viewport rather than the native window's frame-inclusive height.
        // This avoids adding the non-client border again when changing sizing modes.
        MainView.Height = MainView.ActualHeight;
    }

    /// <summary>Open but inactive: close once neither the opening window nor the flyout is in front.</summary>
    private void CloseIfClickedOutside()
    {
        if (IsActive)
        {
            outsideWatch.Stop();
            return;
        }

        IntPtr front = GetForegroundWindow();
        if (front != new WindowInteropHelper(this).Handle && front != openedFrom)
        {
            HideFlyout();
        }
    }

    /// <summary>For the preview: show the settings view.</summary>
    public void ShowSettingsView() => SwitchView(true);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (!e.Handled && e.Key == Key.Escape)
        {
            if (SettingsView.Visibility == Visibility.Visible)
            {
                SwitchView(false);
            }
            else
            {
                HideFlyout();
            }
            e.Handled = true;
        }
    }

    private void SwitchView(bool settings)
    {
        Ui.Views.SmoothScroll.Stop(MainView);
        Ui.Views.SmoothScroll.Stop(SettingsScroll);
        bool changed = (SettingsView.Visibility == Visibility.Visible) != settings;
        // Hidden still participates in layout, so Settings never determines the window height.
        MainView.Visibility = settings ? Visibility.Hidden : Visibility.Visible;
        SettingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        Logo.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        HeaderText.Text = settings ? "Einstellungen" : "DS5 BT Haptics";
        if (changed && IsVisible && !hiding && SystemParameters.ClientAreaAnimation)
        {
            FrameworkElement view = settings ? SettingsView : MainView;
            var duration = TimeSpan.FromMilliseconds(170);
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
            {
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SwitchView(true);

    private void OnBack(object sender, RoutedEventArgs e) => SwitchView(false);

    private void OnMinimize(object sender, RoutedEventArgs e) => HideFlyout();

    private void OnExit(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void ApplyWindowEffects() => WindowEffects.Apply(this, ThemeManager.IsDark);

    /// <summary>
    /// Places the flyout above the tray icon, centred and kept on screen - beside or below it for
    /// a side or top taskbar. Without a known icon position (e.g. opened by a second start) it
    /// goes to the corner at the taskbar.
    ///
    /// Works in screen pixels, since monitors with different scaling would otherwise disagree,
    /// and with the visible outline rather than the window rect, which includes the frame's
    /// invisible border.
    /// </summary>
    private void PlaceAtTray()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out Rect32 outer))
        {
            return;
        }
        Rect32 visible = DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out Rect32 frame, Marshal.SizeOf<Rect32>()) == 0
            ? frame
            : outer;

        int width = visible.Right - visible.Left;
        int height = visible.Bottom - visible.Top;
        int margin = (int)Math.Round(EdgeMargin * VisualTreeHelper.GetDpi(this).DpiScaleX);
        TaskbarEdge edge = GetTaskbarEdge();

        int x, y;
        System.Drawing.Rectangle screen;
        if (anchor is System.Drawing.Rectangle a)
        {
            screen = Screen.FromRectangle(a).Bounds;
            int centreX = a.Left + a.Width / 2;
            int centreY = a.Top + a.Height / 2;
            (x, y) = edge switch
            {
                TaskbarEdge.Top => (centreX - width / 2, a.Bottom + margin),
                TaskbarEdge.Left => (a.Right + margin, centreY - height / 2),
                TaskbarEdge.Right => (a.Left - margin - width, centreY - height / 2),
                _ => (centreX - width / 2, a.Top - margin - height),
            };
        }
        else
        {
            screen = (placementScreen ?? Screen.FromPoint(WinFormsCursor.Position)).WorkingArea;
            x = edge == TaskbarEdge.Left ? screen.Left + margin : screen.Right - width - margin;
            y = edge == TaskbarEdge.Top ? screen.Top + margin : screen.Bottom - height - margin;
        }

        // Never past the screen edge - an icon at the far right would push it half off screen.
        x = Math.Clamp(x, screen.Left + margin, Math.Max(screen.Left + margin, screen.Right - width - margin));
        y = Math.Clamp(y, screen.Top + margin, Math.Max(screen.Top + margin, screen.Bottom - height - margin));

        // Back from the visible outline to the window rect.
        x -= visible.Left - outer.Left;
        y -= visible.Top - outer.Top;

        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>No Alt+Tab entry - a flyout isn't a window you switch to.</summary>
    private void HideFromAltTab()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExToolWindow));
    }

    private static TaskbarEdge GetTaskbarEdge()
    {
        var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>() };
        return SHAppBarMessage(AbmGetTaskbarPos, ref data) != UIntPtr.Zero
            ? (TaskbarEdge)data.uEdge
            : TaskbarEdge.Bottom;
    }

    private enum TaskbarEdge : uint
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3,
    }

    private const uint AbmGetTaskbarPos = 0x5;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const int DwmExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect32 rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out Rect32 value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
