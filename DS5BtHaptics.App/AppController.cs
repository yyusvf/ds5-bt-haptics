using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using DS5BtHaptics.App.Setup;
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
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(12);

    private readonly AppSettings settings;
    private readonly TrayController tray;
    private readonly MainViewModel model;
    private readonly HwndSource messages;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer gameWatch = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer controllerWatch = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    /// <summary>Per controller (bridge slot): battery level last warned about.</summary>
    private readonly Dictionary<int, int> batteryWarned = [];

    /// <summary>Per controller: when haptics or speaker last played (they count as use).</summary>
    private readonly Dictionary<int, long> lastBusy = [];

    /// <summary>Per controller: connected at the last refresh, for the connect/disconnect notifications.</summary>
    private readonly Dictionary<int, bool> wasConnected = [];

    private FlyoutWindow? flyout;
    private bool refreshQueued;
    private bool disposed;
    private Version? announcedUpdate;

    public AppController(AppSettings settings, bool startQuietly, bool updated = false)
    {
        this.settings = settings;
        model = new MainViewModel(settings, this);
        tray = new TrayController();
        tray.FlyoutToggleRequested += anchor => Flyout().Toggle(anchor);
        tray.FlyoutRequested += anchor => Flyout().ShowFlyout(anchor);
        tray.HintClicked += ShowFlyout;

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
        controllerWatch.Tick += (_, _) => CheckControllers();
        updateTimer.Tick += async (_, _) =>
        {
            // First check shortly after start (the network may still be coming up), then twice a day.
            updateTimer.Interval = UpdateInterval;
            if (settings.UpdateCheck) await CheckForUpdateAsync(manual: false);
        };

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
        controllerWatch.Start();
        updateTimer.Start();
        ApplyBridgeEnabled();
        if (updated)
        {
            Notify("DS5 BT Haptics", Loc.T($"Auf Version {Updater.Current} aktualisiert.", $"Updated to version {Updater.Current}."), Ui.Views.Banner.ReadyGlyph);
        }
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            await model.RefreshSetupAsync();
            // First start, or something is missing: show what to do instead of hiding silently.
            if (!disposed && (!startQuietly || model.SetupIncomplete))
            {
                if (startQuietly) Notify("DS5 BT Haptics", Loc.T("Einrichtung unvollständig – Tray-Symbol öffnen.", "Setup incomplete – open the tray icon."), Ui.Views.Banner.WarningGlyph, 4);
                else ShowFlyout();
            }
        });
    }

    public BridgeService? Service { get; private set; }

    /// <summary>A newer release, once found; shown as a card in the flyout.</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }

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

    /// <summary>New options (MAC) or a new driver: start over with a fresh service.</summary>
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

    public void ApplyLowLatency(bool lowLatency)
    {
        if (Service != null) Service.Options.Single = lowLatency;
    }

    public void ApplyMuteWhenSilent(bool mute)
    {
        // Checked with every input report, so this applies within milliseconds.
        if (Service != null) Service.Options.MuteWhenSilent = mute;
    }

    public void ApplySpeaker(bool speaker)
    {
        // Read for every packet as well; no restart, which would unplug the pad under a game.
        if (Service != null) Service.Options.Speaker = speaker;
    }

    /// <summary>Switches one controller off (Bluetooth link dropped).</summary>
    public void PowerOff(Bridge bridge)
    {
        Task.Run(() =>
        {
            if (!bridge.PowerOff()) AppLog.Write("Ausschalten fehlgeschlagen (Bluetooth-Trennung abgelehnt)");
        });
    }

    /// <summary>Language switched in the settings: build the flyout again in the new language.</summary>
    public void ApplyLanguage()
    {
        Loc.Apply(settings.Language);
        bool open = flyout?.IsVisible == true;
        if (flyout != null)
        {
            flyout.ExitRequested -= Exit;
            flyout.Close();
            flyout = null;
        }
        model.LanguageChanged();
        Refresh();
        if (open) Flyout().ShowFlyout(TrayAnchor.Resolve(tray.GetIconBounds()), settings: true, activate: true);
    }

    public void ShowHint(string title, string message) => Notify(title, message, Ui.Views.Banner.WarningGlyph, 4);

    private Ui.Views.Banner? banner;

    /// <summary>Notification in ysfUtil's banner style.</summary>
    private void Notify(string title, string message, string glyph, double seconds = 1.5)
    {
        if (disposed) return;
        banner ??= new Ui.Views.Banner();
        banner.Position = settings.NotificationPosition;
        banner.ShowMessage(title, message, glyph, seconds);
    }

    /// <summary>Sample banner after choosing its position.</summary>
    public void PreviewBanner() => Notify("DS5 BT Haptics", Loc.T("Hinweise erscheinen hier.", "Notifications will appear here."), Ui.Views.Banner.ReadyGlyph);

    public void UpdateTray()
    {
        BridgeService? service = Service;
        IReadOnlyList<Bridge> bridges = service?.Bridges ?? [];
        List<Bridge> connected = bridges.Where(b => b.Connected).ToList();
        Bridge? bridge = connected.FirstOrDefault() ?? bridges.FirstOrDefault();
        (TrayBadge badge, string text) = (service, bridge) switch
        {
            (null, _) when PausedFor != null => (TrayBadge.None, Loc.T("Pausiert für ", "Paused for ") + NativeGames.DisplayName(PausedFor)),
            (null, _) => (TrayBadge.None, Loc.T("Bridge aus", "Bridge off")),
            _ when model.SetupIncomplete => (TrayBadge.Problem, Loc.T("Einrichtung unvollständig", "Setup incomplete")),
            (_, null) => (TrayBadge.Waiting, Loc.T("Warte auf Controller (PS-Taste)", "Waiting for controller (PS button)")),
            (_, { Connected: false }) => (TrayBadge.Waiting, Loc.T("Controller getrennt (PS-Taste)", "Controller disconnected (PS button)")),
            (_, { HostAttached: true }) => (TrayBadge.Ready, ConnectedText(connected)),
            (_, { AttachError: not null }) => (TrayBadge.Problem, Loc.T("Einbinden fehlgeschlagen", "Attach failed")),
            _ => (TrayBadge.Waiting, Loc.T("Wird eingebunden …", "Attaching …")),
        };
        tray.SetStatus(badge, "DS5 BT Haptics – " + text);
    }

    private static string ConnectedText(List<Bridge> connected)
    {
        if (connected.Count > 1)
        {
            return Loc.T($"{connected.Count} Controller verbunden", $"{connected.Count} controllers connected")
                + string.Concat(connected.Select(b => b.Battery is int level ? $", {b.Slot + 1}: {level} %" : ""));
        }
        Bridge bridge = connected[0];
        return Loc.T("Verbunden", "Connected")
            + (bridge.Battery is int battery ? Loc.T($", Akku {battery} %", $", battery {battery} %") : "")
            + (bridge.HapticsActive ? Loc.T(", Haptik aktiv", ", haptics active") : "");
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

    /// <summary>Battery warnings and switching idle controllers off.</summary>
    private void CheckControllers()
    {
        if (Service is not { } service) return;
        long now = Environment.TickCount64;
        foreach (Bridge bridge in service.Bridges)
        {
            if (!bridge.Connected)
            {
                lastBusy.Remove(bridge.Slot);
                continue;
            }
            string name = service.Bridges.Count > 1
                ? Loc.T($"Controller {bridge.Slot + 1}", $"Controller {bridge.Slot + 1}")
                : "DualSense";

            if (bridge.Battery is int level)
            {
                int warned = batteryWarned.GetValueOrDefault(bridge.Slot, 100);
                if (bridge.Charging || level > 25)
                {
                    batteryWarned[bridge.Slot] = 100;
                }
                else if (settings.BatteryWarning && (level <= 10 && warned > 10 || level <= 20 && warned > 20))
                {
                    batteryWarned[bridge.Slot] = level;
                    AppLog.Write($"Akku schwach: {name} {level} %");
                    Notify(Loc.T("Akku schwach", "Battery low"),
                        Loc.T($"{name}: noch {level} %. Bitte bald laden.", $"{name}: {level} % left. Charge it soon."), Ui.Views.Banner.BatteryGlyph, 4);
                }
            }

            if (bridge.HapticsActive || bridge.SpeakerActive) lastBusy[bridge.Slot] = now;
            if (settings.IdleMinutes > 0)
            {
                long lastUse = Math.Max(bridge.LastInputTicks, lastBusy.GetValueOrDefault(bridge.Slot));
                if (now - lastUse > settings.IdleMinutes * 60_000L)
                {
                    AppLog.Write($"{name}: {settings.IdleMinutes} min ohne Eingabe");
                    lastBusy[bridge.Slot] = now; // one attempt per idle period
                    PowerOff(bridge);
                }
            }
        }
    }

    /// <summary>Asks GitHub for a newer release. Manual checks also say when there is none.</summary>
    public async Task CheckForUpdateAsync(bool manual)
    {
        UpdateInfo? update = await Updater.CheckAsync();
        if (disposed) return;
        AvailableUpdate = update;
        model.Refresh();
        if (update != null && (manual || update.Version != announcedUpdate))
        {
            announcedUpdate = update.Version;
            AppLog.Write($"Update verfügbar: {update.Version}");
            if (!manual)
            {
                Notify(Loc.T("Update verfügbar", "Update available"),
                    Loc.T($"Version {update.Version} ist da – im Tray-Menü installieren.", $"Version {update.Version} is out – install it from the tray menu."), Ui.Views.Banner.DownloadGlyph, 4);
            }
        }
    }

    /// <summary>Downloads the update, then exits so the setup can replace the app.</summary>
    public async Task InstallUpdateAsync(IProgress<string> progress)
    {
        if (AvailableUpdate is not { } update) return;
        await Updater.DownloadAsync(update, progress);
        if (Updater.PendingSetup != null) Exit();
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
        NotifyConnections();
    }

    /// <summary>Tray notification when a controller comes or goes (not when the bridge itself stops).</summary>
    private void NotifyConnections()
    {
        if (Service is not { } service)
        {
            wasConnected.Clear();
            return;
        }
        IReadOnlyList<Bridge> bridges = service.Bridges;
        foreach (Bridge bridge in bridges)
        {
            bool now = bridge.Connected;
            bool before = wasConnected.GetValueOrDefault(bridge.Slot);
            wasConnected[bridge.Slot] = now;
            if (now == before || !settings.ConnectionHints) continue;

            string name = bridges.Count > 1 ? $"Controller {bridge.Slot + 1}" : bridge.ControllerName;
            if (now)
            {
                // The battery arrives with the first input reports, a moment after connecting.
                _ = Task.Delay(1200).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (disposed || !bridge.Connected) return;
                    string battery = bridge.Battery is int level
                        ? Loc.T($" · Akku {level} %", $" · Battery {level} %") + (bridge.Charging ? Loc.T(", lädt", ", charging") : "")
                        : "";
                    Notify(Loc.T("Controller verbunden", "Controller connected"), name + battery, Ui.Views.Banner.ControllerGlyph);
                }));
            }
            else
            {
                Notify(Loc.T("Controller getrennt", "Controller disconnected"),
                    name + Loc.T(" · Zum Verbinden PS-Taste drücken.", " · Press the PS button to reconnect."), Ui.Views.Banner.ControllerGlyph, 3);
            }
        }
        foreach (int slot in wasConnected.Keys.Where(s => bridges.All(b => b.Slot != s)).ToList()) wasConnected.Remove(slot);
    }

    public string? PickFile(Microsoft.Win32.OpenFileDialog dialog) => Flyout().PickFile(dialog);

    private void ShowFlyout() => Flyout().ShowFlyout(TrayAnchor.Resolve(tray.GetIconBounds()), activate: true);

    private FlyoutWindow Flyout()
    {
        if (flyout == null)
        {
            var window = new FlyoutWindow(model);
            window.ExitRequested += Exit;
            window.Opening += () =>
            {
                Refresh();
                _ = model.RefreshSetupAsync();
                poll.Start();
            };
            window.IsVisibleChanged += (_, _) =>
            {
                if (!window.IsVisible && flyout == window) poll.Stop();
            };
            flyout = window;
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
        controllerWatch.Stop();
        updateTimer.Stop();
        StopService();
        messages.Dispose();
        banner?.Close();
        tray.Dispose();
    }
}
