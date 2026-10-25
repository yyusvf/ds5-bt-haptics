using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using DS5BtHaptics.App.Ui;
using DS5BtHaptics.App.Ui.Flyout;
using DS5BtHaptics.App.Ui.ViewModels;
using Application = System.Windows.Application;

namespace DS5BtHaptics.App;

/// <summary>
/// Owns the bridge, the tray icon and the flyout. The bridge runs on its own threads; everything
/// here stays on the UI thread.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly AppSettings settings;
    private readonly TrayController tray;
    private readonly MainViewModel model;
    private readonly HwndSource messages;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private FlyoutWindow? flyout;
    private bool refreshQueued;
    private bool disposed;

    public AppController(AppSettings settings, bool startQuietly)
    {
        this.settings = settings;
        model = new MainViewModel(settings, this);
        tray = new TrayController();
        tray.FlyoutToggleRequested += anchor => Flyout().Toggle(anchor);
        tray.FlyoutRequested += anchor => Flyout().ShowFlyout(anchor);

        // Starting the exe again opens the flyout of the running instance. A hidden top-level
        // window, since broadcast messages never reach message-only windows.
        messages = new HwndSource(new HwndSourceParameters("DS5BtHaptics.Messages") { Width = 0, Height = 0, WindowStyle = 0 });
        messages.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == SingleInstance.ShowWindowMessage && msg != 0)
            {
                ShowFlyout();
                handled = true;
            }
            return IntPtr.Zero;
        });

        poll.Tick += (_, _) => Refresh();

        Autostart.Refresh();
        if (settings.IsFirstRun)
        {
            // Installed to live in the tray: start with Windows unless the user turns it off.
            try { Autostart.SetEnabled(true); } catch { }
            settings.Save();
        }

        ApplyBridgeEnabled();
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            await model.RefreshSetupAsync();
            // First start, or something is missing: show what to do instead of hiding silently.
            if (!disposed && (!startQuietly || model.SetupIncomplete))
            {
                if (startQuietly) tray.ShowHint("DS5 BT Haptics", "Einrichtung unvollständig – hier klicken.");
                else ShowFlyout();
            }
        });
    }

    public BridgeService? Service { get; private set; }

    public void ApplyBridgeEnabled()
    {
        if (settings.BridgeEnabled && Service == null)
        {
            Service = new BridgeService(settings.ToOptions());
            Service.Changed += QueueRefresh;
            Service.Start();
        }
        else if (!settings.BridgeEnabled && Service != null)
        {
            StopService();
        }
        Refresh();
    }

    /// <summary>New options (latency, MAC) or a new driver: start over with a fresh service.</summary>
    public void RestartBridge()
    {
        AppLog.Write("Bridge wird neu gestartet ...");
        StopService();
        ApplyBridgeEnabled();
    }

    public void ApplyGain(float gain)
    {
        // Read for every haptics packet, so this applies immediately.
        if (Service != null) Service.Options.Gain = gain;
    }

    public void ShowHint(string title, string message) => tray.ShowHint(title, message, ToolTipIcon.Info);

    public void UpdateTray()
    {
        BridgeService? service = Service;
        Bridge? bridge = service?.Current;
        (TrayBadge badge, string text) = (service, bridge) switch
        {
            (null, _) => (TrayBadge.None, "Bridge aus"),
            _ when model.SetupIncomplete => (TrayBadge.Problem, "Einrichtung unvollständig"),
            (_, null) => (TrayBadge.Waiting, "Warte auf Controller (PS-Taste)"),
            (_, { HostAttached: true }) => (TrayBadge.Ready,
                "Verbunden" + (bridge.Battery is int level ? $", Akku {level} %" : "")
                + (bridge.HapticsActive ? ", Haptik aktiv" : "")),
            (_, { AttachError: not null }) => (TrayBadge.Problem, "Einbinden fehlgeschlagen"),
            _ => (TrayBadge.Waiting, "Wird eingebunden …"),
        };
        tray.SetStatus(badge, "DS5 BT Haptics – " + text);
    }

    private void StopService()
    {
        BridgeService? service = Service;
        if (service == null) return;
        Service = null;
        service.Changed -= QueueRefresh;
        // Stopping waits for the bridge threads; a frozen tray for up to a few seconds is worse.
        Task.Run(service.Dispose).Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Bridge events arrive on worker threads and can be frequent; fold them into one refresh.</summary>
    private void QueueRefresh()
    {
        if (refreshQueued || disposed) return;
        refreshQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            refreshQueued = false;
            if (!disposed) Refresh();
        });
    }

    private void Refresh()
    {
        model.Refresh();
        UpdateTray();
    }

    private void ShowFlyout() => Flyout().ShowFlyout(TrayAnchor.Resolve(tray.GetIconBounds()), activate: true);

    private FlyoutWindow Flyout()
    {
        if (flyout == null)
        {
            flyout = new FlyoutWindow(model);
            flyout.ExitRequested += Exit;
            flyout.Opening += () =>
            {
                Refresh();
                _ = model.RefreshSetupAsync();
                poll.Start();
            };
            flyout.IsVisibleChanged += (_, _) =>
            {
                if (!flyout.IsVisible) poll.Stop();
            };
        }
        return flyout;
    }

    private void Exit()
    {
        flyout?.Close();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        disposed = true;
        poll.Stop();
        StopService();
        messages.Dispose();
        tray.Dispose();
    }
}
