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
    private readonly DispatcherTimer gameWatch = new() { Interval = TimeSpan.FromMilliseconds(1500) };
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
        gameWatch.Tick += (_, _) => CheckGames();

        Autostart.Refresh();
        if (settings.IsFirstRun)
        {
            // Installed to live in the tray: start with Windows unless the user turns it off.
            try { Autostart.SetEnabled(true); } catch { }
            settings.Save();
        }

        if (!settings.NativeGamesSearched)
        {
            settings.NativeGames.AddRange(NativeGames.FindInstalled()
                .Where(g => !settings.NativeGames.Contains(g, StringComparer.OrdinalIgnoreCase)));
            settings.NativeGamesSearched = true;
            settings.Save();
        }

        // Before the bridge starts: a game that is already running keeps the real controller.
        CheckGames();
        gameWatch.Start();
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

    /// <summary>The running game the bridge steps aside for (see <see cref="NativeGames"/>), or null.</summary>
    public string? PausedFor => runningGame != null && !string.Equals(runningGame, forcedFor, StringComparison.OrdinalIgnoreCase)
        ? runningGame
        : null;

    /// <summary>The running listed game the user switched the bridge on for anyway, or null.</summary>
    public string? ForcedFor => runningGame != null && string.Equals(runningGame, forcedFor, StringComparison.OrdinalIgnoreCase)
        ? runningGame
        : null;

    private string? runningGame;

    /// <summary>Set by "Trotzdem aktivieren"; lasts until that game exits.</summary>
    private string? forcedFor;

    /// <summary>Bridge on despite the running game, or back to paused.</summary>
    public void SetForced(bool forced)
    {
        if (runningGame == null) return;
        forcedFor = forced ? runningGame : null;
        AppLog.Write(forced
            ? $"Bridge trotz {NativeGames.DisplayName(runningGame)} aktiviert"
            : $"Bridge wieder pausiert für {NativeGames.DisplayName(runningGame)}");
        ApplyBridgeEnabled();
    }

    public void ApplyBridgeEnabled()
    {
        bool wanted = settings.BridgeEnabled && PausedFor == null;
        if (wanted && Service == null)
        {
            Service = new BridgeService(settings.ToOptions());
            Service.Changed += QueueRefresh;
            Service.Start();
        }
        else if (!wanted && Service != null)
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
            (null, _) when PausedFor != null => (TrayBadge.None, $"Pausiert für {NativeGames.DisplayName(PausedFor)}"),
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

    /// <summary>Pauses the bridge while a listed game runs and resumes once it has exited.</summary>
    private void CheckGames()
    {
        string? game = settings.NativeGames.Count == 0 ? null : NativeGames.FindRunning(settings.NativeGames);
        if (string.Equals(game, runningGame, StringComparison.OrdinalIgnoreCase)) return;
        runningGame = game;
        forcedFor = null;
        AppLog.Write(game != null
            ? $"Bridge pausiert: {NativeGames.DisplayName(game)} steuert die Bluetooth-Haptik selbst"
            : "Spiel beendet, Bridge läuft wieder");
        ApplyBridgeEnabled();
    }

    /// <summary>After the list changed in the settings.</summary>
    public void GamesChanged()
    {
        settings.Save();
        CheckGames();
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

    public string? PickFile(Microsoft.Win32.OpenFileDialog dialog) => Flyout().PickFile(dialog);

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
        gameWatch.Stop();
        StopService();
        messages.Dispose();
        tray.Dispose();
    }
}
