using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using DS5BtHaptics.App.Setup;
using DS5BtHaptics.App.Ui.Theme;

namespace DS5BtHaptics.App.Ui.ViewModels;

/// <summary>
/// Everything the flyout shows. Status is polled from <see cref="BridgeService"/> rather than
/// pushed: the bridge raises its events on worker threads, many times a second while a game runs.
/// </summary>
internal sealed class MainViewModel : ViewModelBase
{
    private readonly AppSettings settings;
    private readonly AppController app;

    private string controllerTitle = "", controllerDetail = "";
    private string virtualTitle = "", virtualDetail = "";
    private string hapticsTitle = "", hapticsDetail = "";
    private bool controllerOk, virtualOk, hapticsOk, canTest, isTesting;
    private string? forceLabel;
    private string? setupTitle, setupDetail, setupAction, busyText;
    private Func<Task>? setupRun;
    private HidHideStatus? hidHide;

    public MainViewModel(AppSettings settings, AppController app)
    {
        this.settings = settings;
        this.app = app;
        TestCommand = new RelayCommand(RunTest);
        RestartCommand = new RelayCommand(() => app.RestartBridge());
        OpenLogCommand = new RelayCommand(OpenLog);
        SetupCommand = new RelayCommand(RunSetup);
        AddGameCommand = new RelayCommand(AddGame);
        ForceCommand = new RelayCommand(() =>
        {
            app.SetForced(app.PausedFor != null);
            Refresh();
        });
        Games = new ObservableCollection<GameEntry>(settings.NativeGames.Select(Entry));
    }

    // ══ Status ════════════════════════════════════════════════════════════════

    public string ControllerTitle { get => controllerTitle; private set => Set(ref controllerTitle, value); }
    public string ControllerDetail { get => controllerDetail; private set => Set(ref controllerDetail, value); }
    public bool ControllerOk { get => controllerOk; private set => Set(ref controllerOk, value); }

    public string VirtualTitle { get => virtualTitle; private set => Set(ref virtualTitle, value); }
    public string VirtualDetail { get => virtualDetail; private set => Set(ref virtualDetail, value); }
    public bool VirtualOk { get => virtualOk; private set => Set(ref virtualOk, value); }

    public string HapticsTitle { get => hapticsTitle; private set => Set(ref hapticsTitle, value); }
    public string HapticsDetail { get => hapticsDetail; private set => Set(ref hapticsDetail, value); }
    public bool HapticsOk { get => hapticsOk; private set => Set(ref hapticsOk, value); }

    /// <summary>Button on the controller card while a listed game runs; null hides it.</summary>
    public string? ForceLabel { get => forceLabel; private set => Set(ref forceLabel, value); }

    public bool CanTest { get => canTest; private set => Set(ref canTest, value); }

    public bool IsTesting
    {
        get => isTesting;
        private set
        {
            if (Set(ref isTesting, value)) Raise(nameof(TestLabel));
        }
    }

    public string TestLabel => isTesting ? "Läuft …" : "Testen";

    public bool BridgeEnabled
    {
        get => settings.BridgeEnabled;
        set
        {
            if (settings.BridgeEnabled == value) return;
            settings.BridgeEnabled = value;
            settings.Save();
            Raise();
            app.ApplyBridgeEnabled();
        }
    }

    // ══ Setup ═════════════════════════════════════════════════════════════════

    public bool HasSetupIssue => setupTitle != null;
    public string? SetupTitle { get => setupTitle; private set { if (Set(ref setupTitle, value)) Raise(nameof(HasSetupIssue)); } }
    public string? SetupDetail { get => setupDetail; private set => Set(ref setupDetail, value); }
    public string? SetupAction { get => setupAction; private set => Set(ref setupAction, value); }

    /// <summary>Progress of a running install; null when idle.</summary>
    public string? BusyText
    {
        get => busyText;
        private set
        {
            if (Set(ref busyText, value)) Raise(nameof(IsIdle));
        }
    }

    public bool IsIdle => busyText == null;

    /// <summary>For the tray: anything the user still has to set up.</summary>
    public bool SetupIncomplete => HasSetupIssue;

    // ══ Settings ══════════════════════════════════════════════════════════════

    public double Gain
    {
        get => settings.Gain;
        set
        {
            float gain = (float)Math.Round(Math.Clamp(value, 0.25, 4.0), 2);
            if (Math.Abs(settings.Gain - gain) < 0.001f) return;
            settings.Gain = gain;
            settings.Save();
            app.ApplyGain(gain);
            Raise();
            Raise(nameof(GainText));
        }
    }

    public string GainText => $"{settings.Gain:0.0}×";

    public bool LowLatency
    {
        get => settings.LowLatency;
        set
        {
            if (settings.LowLatency == value) return;
            settings.LowLatency = value;
            settings.Save();
            Raise();
            // Applied live: a restart would unplug the virtual pad under a running game.
            app.ApplyLowLatency(value);
        }
    }

    public bool RealMac
    {
        get => settings.RealMac;
        set
        {
            if (settings.RealMac == value) return;
            settings.RealMac = value;
            settings.Save();
            Raise();
            app.RestartBridge();
        }
    }

    public bool AutostartEnabled
    {
        get => Autostart.IsEnabled();
        set
        {
            try
            {
                Autostart.SetEnabled(value);
            }
            catch
            {
                // Registry not writable - the switch snaps back below.
            }
            Raise();
        }
    }

    public IReadOnlyList<string> Themes { get; } = ["Wie Windows", "Hell", "Dunkel"];

    public int ThemeIndex
    {
        get => (int)settings.Theme;
        set
        {
            var mode = (ThemeMode)Math.Clamp(value, 0, 2);
            if (settings.Theme == mode) return;
            settings.Theme = mode;
            settings.Save();
            ThemeManager.SetMode(mode);
            Raise();
        }
    }

    public string Version { get; } = "Version " + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    public ICommand TestCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand SetupCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand ForceCommand { get; }

    // ══ Games with their own Bluetooth haptics ════════════════════════════════

    public ObservableCollection<GameEntry> Games { get; }

    private GameEntry Entry(string exe) => new(NativeGames.DisplayName(exe), exe, new RelayCommand(() => RemoveGame(exe)));

    private async void AddGame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Spiel mit eigener Bluetooth-Haptik wählen",
            Filter = "Spiel (*.exe)|*.exe",
        };
        if (app.PickFile(dialog) is not { } exe) return;
        if (settings.NativeGames.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;

        settings.NativeGames.Add(exe);
        Games.Add(Entry(exe));
        app.GamesChanged();
        // The game has to see the Bluetooth pad that HidHide hides from everyone else.
        if (Dependencies.HidHideInstalled && !await Dependencies.SetGamesAllowedAsync([exe], allowed: true))
        {
            app.ShowHint("HidHide", "Spiel nicht freigegeben – es sieht den Bluetooth-Controller sonst nicht.");
        }
        await RefreshSetupAsync();
    }

    private async void RemoveGame(string exe)
    {
        settings.NativeGames.RemoveAll(g => string.Equals(g, exe, StringComparison.OrdinalIgnoreCase));
        if (Games.FirstOrDefault(g => g.Path == exe) is { } entry) Games.Remove(entry);
        app.GamesChanged();
        // Otherwise the game would see both controllers next time.
        if (hidHide?.Allows(exe) == true)
        {
            await Dependencies.SetGamesAllowedAsync([exe], allowed: false);
        }
        await RefreshSetupAsync();
    }

#if DEBUG
    /// <summary>Sample state for --render.</summary>
    public void Preview(bool setup)
    {
        ControllerTitle = "DualSense Wireless Controller";
        ControllerDetail = "Über Bluetooth verbunden · Akku 75 %";
        ControllerOk = true;
        VirtualTitle = "Als USB-Controller eingebunden";
        VirtualDetail = "Spiele sehen einen kabelgebundenen DualSense.";
        VirtualOk = true;
        HapticsTitle = "Haptik aktiv";
        HapticsDetail = "Ein Spiel sendet HD-Haptik an den Controller.";
        HapticsOk = true;
        CanTest = true;
        if (setup)
        {
            ShowIssue("HidHide einrichten",
                "Diese App freigeben und den Bluetooth-Controller vor Spielen verstecken (Admin-Rechte).",
                "Einrichten", () => Task.CompletedTask);
        }
    }
#endif

    // ══ Refresh ═══════════════════════════════════════════════════════════════

    /// <summary>Reads the bridge state; cheap, called every half second while the flyout is open.</summary>
    public void Refresh()
    {
        BridgeService? service = app.Service;
        Bridge? bridge = service?.Current;

        ForceLabel = app.PausedFor != null ? "Trotzdem aktivieren" : app.ForcedFor != null ? "Pausieren" : null;
        if (app.PausedFor is { } game)
        {
            ControllerTitle = $"Pausiert für {NativeGames.DisplayName(game)}";
            ControllerDetail = "Das Spiel steuert die Bluetooth-Haptik selbst. Danach läuft die Bridge wieder.";
            ControllerOk = false;
        }
        else if (service == null || service.State == BridgeState.Stopped)
        {
            ControllerTitle = "Bridge aus";
            ControllerDetail = "Der Controller bleibt ein normales Bluetooth-Gamepad.";
            ControllerOk = false;
        }
        else if (bridge == null)
        {
            ControllerTitle = "Kein Controller";
            ControllerDetail = service.LastError is { } error
                ? "Fehler: " + error
                : "PS-Taste drücken, um den DualSense per Bluetooth zu verbinden.";
            ControllerOk = false;
        }
        else if (!bridge.Connected)
        {
            ControllerTitle = "Controller getrennt";
            ControllerDetail = "PS-Taste drücken. Der virtuelle Controller bleibt so lange eingesteckt, laufende Spiele merken nichts.";
            ControllerOk = false;
        }
        else
        {
            ControllerTitle = bridge.ControllerName;
            string battery = bridge.Battery is int level
                ? $" · Akku {level} %{(bridge.Charging ? ", lädt" : "")}"
                : "";
            ControllerDetail = "Über Bluetooth verbunden" + battery;
            ControllerOk = true;
        }

        if (bridge == null)
        {
            VirtualTitle = "Virtueller USB-Controller";
            VirtualDetail = "Erscheint, sobald der Controller verbunden ist.";
            VirtualOk = false;
        }
        else if (bridge.HostAttached)
        {
            VirtualTitle = "Als USB-Controller eingebunden";
            VirtualDetail = "Spiele sehen einen kabelgebundenen DualSense.";
            VirtualOk = true;
        }
        else if (!Dependencies.UsbIpInstalled)
        {
            VirtualTitle = "Nicht eingebunden";
            VirtualDetail = "usbip-win2 fehlt, siehe oben.";
            VirtualOk = false;
        }
        else if (bridge.AttachError is { } attachError)
        {
            VirtualTitle = "Einbinden fehlgeschlagen";
            VirtualDetail = attachError;
            VirtualOk = false;
        }
        else
        {
            VirtualTitle = "Wird eingebunden …";
            VirtualDetail = "usbip-win2 meldet den virtuellen DualSense bei Windows an.";
            VirtualOk = false;
        }

        bool attached = bridge?.HostAttached == true;
        if (attached && bridge!.HapticsActive)
        {
            HapticsTitle = "Haptik aktiv";
            HapticsDetail = "Ein Spiel sendet HD-Haptik an den Controller.";
            HapticsOk = true;
        }
        else if (attached)
        {
            HapticsTitle = "Haptik bereit";
            HapticsDetail = "Wartet auf ein Spiel. Spiele nach der Bridge starten.";
            HapticsOk = true;
        }
        else
        {
            HapticsTitle = "HD-Haptik";
            HapticsDetail = "Verfügbar, sobald der virtuelle Controller eingebunden ist.";
            HapticsOk = false;
        }
        CanTest = attached && !IsTesting;
    }

    /// <summary>Checks the drivers. Starts HidHideCLI, so only on opening and after setup steps.</summary>
    public async Task RefreshSetupAsync()
    {
        hidHide = await Task.Run(Dependencies.QueryHidHide);
        if (!Dependencies.UsbIpInstalled)
        {
            ShowIssue("usbip-win2 fehlt",
                "Stellt den virtuellen USB-Controller bereit. Wird von GitHub geladen; Windows fragt nach Admin-Rechten.",
                "Installieren", () => Install(Dependencies.UsbIp));
        }
        else if (!Dependencies.HidHideInstalled)
        {
            ShowIssue("HidHide fehlt",
                "Versteckt den Bluetooth-Controller vor Spielen, damit sie nur den virtuellen sehen.",
                "Installieren", () => Install(Dependencies.HidHide));
        }
        else if (hidHide is not { Ready: true })
        {
            ShowIssue("HidHide einrichten",
                hidHide is { AppRegistered: true, ControllerHidden: false }
                    ? "Controller einschalten, dann einrichten: er wird vor Spielen versteckt."
                    : "Diese App freigeben und den Bluetooth-Controller vor Spielen verstecken (Admin-Rechte).",
                "Einrichten", ConfigureHidHide);
        }
        else if (settings.NativeGames.Where(g => !hidHide!.Allows(g)).ToList() is { Count: > 0 } blocked)
        {
            ShowIssue("Spiele freigeben",
                $"{string.Join(", ", blocked.Select(NativeGames.DisplayName))} steuert die Bluetooth-Haptik selbst und "
                + "muss den Bluetooth-Controller sehen dürfen (Admin-Rechte).",
                "Freigeben", async () =>
                {
                    BusyText = "Wird freigegeben – bitte Windows-Abfrage bestätigen …";
                    if (!await Dependencies.SetGamesAllowedAsync(blocked, allowed: true))
                        app.ShowHint("HidHide", "Freigabe abgebrochen oder fehlgeschlagen.");
                });
        }
        else
        {
            SetupTitle = null;
            SetupDetail = null;
            SetupAction = null;
            setupRun = null;
        }
        app.UpdateTray();
    }

    private void ShowIssue(string title, string detail, string action, Func<Task> run)
    {
        SetupTitle = title;
        SetupDetail = detail;
        SetupAction = action;
        setupRun = run;
    }

    private async void RunSetup()
    {
        if (setupRun == null || !IsIdle) return;
        try
        {
            await setupRun();
        }
        catch (Exception e)
        {
            AppLog.Write($"Setup fehlgeschlagen: {e}");
            app.ShowHint("Einrichtung fehlgeschlagen", e.Message);
        }
        finally
        {
            BusyText = null;
        }
        await RefreshSetupAsync();
    }

    private async Task Install(Installer installer)
    {
        await installer.InstallAsync(new Progress<string>(text => BusyText = text));
        // A new driver: the bridge starts over so usbip attach or the hidden pad are picked up.
        app.RestartBridge();
    }

    private async Task ConfigureHidHide()
    {
        BusyText = "HidHide wird eingerichtet – bitte Windows-Abfrage bestätigen …";
        if (!await Dependencies.ConfigureHidHideAsync())
        {
            app.ShowHint("HidHide", "Einrichtung abgebrochen oder fehlgeschlagen.");
        }
        app.RestartBridge();
    }

    private async void RunTest()
    {
        if (IsTesting) return;
        IsTesting = true;
        CanTest = false;
        int result = await Task.Run(() =>
        {
            try
            {
                return DeviceTest.Run();
            }
            catch (Exception e)
            {
                AppLog.Write($"Test fehlgeschlagen: {e.Message}");
                return -1;
            }
        });
        IsTesting = false;
        if (result != 0)
        {
            app.ShowHint("Test", "Das Testmuster konnte nicht abgespielt werden, Details im Log.");
        }
        Refresh();
    }

    private static void OpenLog()
    {
        if (File.Exists(LogFile.FilePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LogFile.FilePath}\"") { UseShellExecute = true });
        }
        else
        {
            Directory.CreateDirectory(LogFile.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogFile.Folder}\"") { UseShellExecute = true });
        }
    }
}

/// <summary>One line in the games list of the settings.</summary>
internal sealed record GameEntry(string Name, string Path, ICommand RemoveCommand);
