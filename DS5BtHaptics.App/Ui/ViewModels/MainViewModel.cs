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
    private static readonly int[] IdleChoices = [0, 5, 10, 15, 30, 60];

    private readonly AppSettings settings;
    private readonly AppController app;

    private string controllerTitle = "", controllerDetail = "";
    private string virtualTitle = "", virtualDetail = "";
    private string hapticsTitle = "", hapticsDetail = "";
    private bool controllerOk, virtualOk, hapticsOk, canTest, isTesting;
    private string? forceLabel;
    private string? setupTitle, setupDetail, setupAction, busyText;
    private string? updateTitle, updateBusy, updateStatus;
    private Func<Task>? setupRun;
    private HidHideStatus? hidHide;
    private Bridge? firstBridge;

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
        PowerOffCommand = new RelayCommand(() =>
        {
            if (firstBridge != null) app.PowerOff(firstBridge);
        });
        InstallUpdateCommand = new RelayCommand(InstallUpdate);
        CheckUpdateCommand = new RelayCommand(CheckUpdate);
        Games = new ObservableCollection<GameEntry>(settings.NativeGames.Select(Entry));
    }

    // ══ Status ════════════════════════════════════════════════════════════════

    public string ControllerTitle { get => controllerTitle; private set => Set(ref controllerTitle, value); }
    public string ControllerDetail { get => controllerDetail; private set => Set(ref controllerDetail, value); }
    public bool ControllerOk { get => controllerOk; private set => Set(ref controllerOk, value); }

    /// <summary>Further controllers (player 2 and up), each with its own card.</summary>
    public ObservableCollection<ControllerEntry> ExtraControllers { get; } = [];

    public string VirtualTitle { get => virtualTitle; private set => Set(ref virtualTitle, value); }
    public string VirtualDetail { get => virtualDetail; private set => Set(ref virtualDetail, value); }
    public bool VirtualOk { get => virtualOk; private set => Set(ref virtualOk, value); }

    public string HapticsTitle { get => hapticsTitle; private set => Set(ref hapticsTitle, value); }
    public string HapticsDetail { get => hapticsDetail; private set => Set(ref hapticsDetail, value); }
    public bool HapticsOk { get => hapticsOk; private set => Set(ref hapticsOk, value); }

    /// <summary>Button on the controller card while a listed game runs; null hides it.</summary>
    public string? ForceLabel { get => forceLabel; private set => Set(ref forceLabel, value); }

    /// <summary>The first controller is connected and can be switched off from here.</summary>
    public bool CanPowerOff => firstBridge?.Connected == true && app.PausedFor == null;

    public bool CanTest { get => canTest; private set => Set(ref canTest, value); }

    public bool IsTesting
    {
        get => isTesting;
        private set
        {
            if (Set(ref isTesting, value)) Raise(nameof(TestLabel));
        }
    }

    public string TestLabel => isTesting ? Loc.T("Läuft …", "Playing …") : Loc.T("Testen", "Test");

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

    // ══ Update ════════════════════════════════════════════════════════════════

    /// <summary>"Version x is available"; null hides the card.</summary>
    public string? UpdateTitle { get => updateTitle; private set => Set(ref updateTitle, value); }

    /// <summary>Download progress; null when idle.</summary>
    public string? UpdateBusy
    {
        get => updateBusy;
        private set
        {
            if (Set(ref updateBusy, value)) Raise(nameof(UpdateIdle));
        }
    }

    public bool UpdateIdle => updateBusy == null;

    /// <summary>Result of "Jetzt suchen" in the settings.</summary>
    public string? UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }

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

    public bool Speaker
    {
        get => settings.Speaker;
        set
        {
            if (settings.Speaker == value) return;
            settings.Speaker = value;
            settings.Save();
            Raise();
            app.ApplySpeaker(value);
        }
    }

    public bool MuteWhenSilent
    {
        get => settings.MuteWhenSilent;
        set
        {
            if (settings.MuteWhenSilent == value) return;
            settings.MuteWhenSilent = value;
            settings.Save();
            Raise();
            app.ApplyMuteWhenSilent(value);
        }
    }

    public bool BatteryWarning
    {
        get => settings.BatteryWarning;
        set
        {
            if (settings.BatteryWarning == value) return;
            settings.BatteryWarning = value;
            settings.Save();
            Raise();
        }
    }

    public bool ConnectionHints
    {
        get => settings.ConnectionHints;
        set
        {
            if (settings.ConnectionHints == value) return;
            settings.ConnectionHints = value;
            settings.Save();
            Raise();
        }
    }

    public IReadOnlyList<string> NotificationPositions =>
        [Loc.T("Unten rechts", "Bottom right"), Loc.T("Unten links", "Bottom left"), Loc.T("Oben rechts", "Top right"), Loc.T("Oben links", "Top left")];

    public int NotificationPositionIndex
    {
        get => (int)settings.NotificationPosition;
        set
        {
            if (value < 0 || value > 3 || value == (int)settings.NotificationPosition) return;
            settings.NotificationPosition = (Views.NotificationPosition)value;
            settings.Save();
            Raise();
            app.PreviewBanner();
        }
    }

    public IReadOnlyList<string> IdleOptions => IdleChoices
        .Select(m => m == 0 ? Loc.T("Nie", "Never") : Loc.T($"Nach {m} Minuten", $"After {m} minutes"))
        .ToList();

    public int IdleIndex
    {
        get => Math.Max(0, Array.IndexOf(IdleChoices, settings.IdleMinutes));
        set
        {
            int minutes = IdleChoices[Math.Clamp(value, 0, IdleChoices.Length - 1)];
            if (settings.IdleMinutes == minutes) return;
            settings.IdleMinutes = minutes;
            settings.Save();
            Raise();
        }
    }

    public bool UpdateCheck
    {
        get => settings.UpdateCheck;
        set
        {
            if (settings.UpdateCheck == value) return;
            settings.UpdateCheck = value;
            settings.Save();
            Raise();
            if (value) _ = app.CheckForUpdateAsync(manual: false);
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

    public IReadOnlyList<string> Themes => [Loc.T("Wie Windows", "Same as Windows"), Loc.T("Hell", "Light"), Loc.T("Dunkel", "Dark")];

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

    public IReadOnlyList<string> Languages => [Loc.T("Wie Windows", "Same as Windows"), "Deutsch", "English"];

    public int LanguageIndex
    {
        get => (int)settings.Language;
        set
        {
            var language = (AppLanguage)Math.Clamp(value, 0, 2);
            if (settings.Language == language) return;
            settings.Language = language;
            settings.Save();
            // Rebuilds the flyout; must not run inside the ComboBox's own selection change.
            System.Windows.Application.Current.Dispatcher.BeginInvoke(app.ApplyLanguage);
        }
    }

    public string Version { get; } = "Version " + Updater.Current;

    public ICommand TestCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand SetupCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand ForceCommand { get; }
    public ICommand PowerOffCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand CheckUpdateCommand { get; }

    /// <summary>Texts come from <see cref="Loc"/>: after a language switch everything is read again.</summary>
    public void LanguageChanged()
    {
        UpdateStatus = null;
        ExtraControllers.Clear();
        Raise(string.Empty);
        _ = RefreshSetupAsync();
    }

    // ══ Games with their own Bluetooth haptics ════════════════════════════════

    public ObservableCollection<GameEntry> Games { get; }

    private GameEntry Entry(string exe) => new(NativeGames.DisplayName(exe), exe, new RelayCommand(() => RemoveGame(exe)));

    private async void AddGame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Spiel mit eigener Bluetooth-Haptik wählen", "Choose a game with its own Bluetooth haptics"),
            Filter = Loc.T("Spiel", "Game") + " (*.exe)|*.exe",
        };
        if (app.PickFile(dialog) is not { } exe) return;
        if (settings.NativeGames.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;

        settings.NativeGames.Add(exe);
        Games.Add(Entry(exe));
        app.GamesChanged();
        // The game has to see the Bluetooth pad that HidHide hides from everyone else.
        if (Dependencies.HidHideInstalled && !await Dependencies.SetGamesAllowedAsync([exe], allowed: true))
        {
            app.ShowHint("HidHide", Loc.T("Spiel nicht freigegeben – es sieht den Bluetooth-Controller sonst nicht.",
                "Game not allowed – otherwise it can't see the Bluetooth controller."));
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
        ControllerDetail = Loc.T("Controller 1 · Über Bluetooth verbunden · Akku 75 %", "Controller 1 · Connected via Bluetooth · Battery 75 %");
        ControllerOk = true;
        ExtraControllers.Add(new ControllerEntry(null)
        {
            Title = "DualSense Edge",
            Detail = Loc.T("Controller 2 · Über Bluetooth verbunden · Akku 45 %", "Controller 2 · Connected via Bluetooth · Battery 45 %"),
            IsOk = true,
            CanPowerOff = true,
        });
        VirtualTitle = Loc.T("2 USB-Controller eingebunden", "2 USB controllers attached");
        VirtualDetail = Loc.T("Spiele sehen kabelgebundene DualSense.", "Games see wired DualSense controllers.");
        VirtualOk = true;
        HapticsTitle = Loc.T("Haptik und Ton aktiv", "Haptics and sound active");
        HapticsDetail = Loc.T("Ein Spiel sendet HD-Haptik an den Controller.", "A game is sending HD haptics to the controller.");
        HapticsOk = true;
        CanTest = true;
        UpdateTitle = Loc.T("Version 9.9.9 verfügbar", "Version 9.9.9 available");
        if (setup)
        {
            ShowIssue(Loc.T("HidHide einrichten", "Set up HidHide"),
                Loc.T("Diese App freigeben und den Bluetooth-Controller vor Spielen verstecken (Admin-Rechte).",
                    "Allow this app and hide the Bluetooth controller from games (admin rights)."),
                Loc.T("Einrichten", "Set up"), () => Task.CompletedTask);
        }
    }
#endif

    // ══ Refresh ═══════════════════════════════════════════════════════════════

    /// <summary>Reads the bridge state; cheap, called every half second while the flyout is open.</summary>
    public void Refresh()
    {
        BridgeService? service = app.Service;
        IReadOnlyList<Bridge> bridges = service?.Bridges ?? [];
        Bridge? bridge = bridges.FirstOrDefault(b => b.Connected) ?? bridges.FirstOrDefault();
        firstBridge = bridge;
        bool many = bridges.Count > 1;

        ForceLabel = app.PausedFor != null ? Loc.T("Trotzdem aktivieren", "Enable anyway")
            : app.ForcedFor != null ? Loc.T("Pausieren", "Pause") : null;
        if (app.PausedFor is { } game)
        {
            ControllerTitle = Loc.T("Pausiert für ", "Paused for ") + NativeGames.DisplayName(game);
            ControllerDetail = Loc.T("Das Spiel steuert die Bluetooth-Haptik selbst. Danach läuft die Bridge wieder.",
                "The game drives the Bluetooth haptics itself. The bridge resumes afterwards.");
            ControllerOk = false;
        }
        else if (service == null || service.State == BridgeState.Stopped)
        {
            ControllerTitle = Loc.T("Bridge aus", "Bridge off");
            ControllerDetail = Loc.T("Der Controller bleibt ein normales Bluetooth-Gamepad.", "The controller stays a plain Bluetooth gamepad.");
            ControllerOk = false;
        }
        else if (bridge == null)
        {
            ControllerTitle = Loc.T("Kein Controller", "No controller");
            ControllerDetail = service.LastError is { } error
                ? Loc.T("Fehler: ", "Error: ") + error
                : Loc.T("PS-Taste drücken, um den DualSense per Bluetooth zu verbinden.", "Press the PS button to connect the DualSense via Bluetooth.");
            ControllerOk = false;
        }
        else
        {
            (ControllerTitle, ControllerDetail, ControllerOk) = Describe(bridge, many);
        }
        Raise(nameof(CanPowerOff));

        // Player 2 and up: cards that keep their identity, so the list doesn't flicker every refresh.
        List<Bridge> extra = app.PausedFor == null ? bridges.Where(b => b != bridge).ToList() : [];
        for (int i = ExtraControllers.Count - 1; i >= 0; i--)
        {
            if (ExtraControllers[i].Bridge is not { } shown || !extra.Contains(shown)) ExtraControllers.RemoveAt(i);
        }
        foreach (Bridge other in extra)
        {
            ControllerEntry? entry = ExtraControllers.FirstOrDefault(e => e.Bridge == other);
            if (entry == null)
            {
                entry = new ControllerEntry(other) { PowerCommand = new RelayCommand(() => app.PowerOff(other)) };
                ExtraControllers.Add(entry);
            }
            (string title, string detail, bool ok) = Describe(other, many: true);
            entry.Title = title;
            entry.Detail = detail;
            entry.IsOk = ok;
            entry.CanPowerOff = other.Connected;
        }

        int attachedCount = bridges.Count(b => b.HostAttached);
        if (bridge == null)
        {
            VirtualTitle = Loc.T("Virtueller USB-Controller", "Virtual USB controller");
            VirtualDetail = Loc.T("Erscheint, sobald der Controller verbunden ist.", "Appears once the controller is connected.");
            VirtualOk = false;
        }
        else if (attachedCount > 1)
        {
            VirtualTitle = Loc.T($"{attachedCount} USB-Controller eingebunden", $"{attachedCount} USB controllers attached");
            VirtualDetail = Loc.T("Spiele sehen kabelgebundene DualSense.", "Games see wired DualSense controllers.");
            VirtualOk = true;
        }
        else if (bridge.HostAttached)
        {
            VirtualTitle = Loc.T("Als USB-Controller eingebunden", "Attached as a USB controller");
            VirtualDetail = Loc.T("Spiele sehen einen kabelgebundenen DualSense.", "Games see a wired DualSense.");
            VirtualOk = true;
        }
        else if (!Dependencies.UsbIpInstalled)
        {
            VirtualTitle = Loc.T("Nicht eingebunden", "Not attached");
            VirtualDetail = Loc.T("usbip-win2 fehlt, siehe oben.", "usbip-win2 is missing, see above.");
            VirtualOk = false;
        }
        else if (bridge.AttachError is { } attachError)
        {
            VirtualTitle = Loc.T("Einbinden fehlgeschlagen", "Attach failed");
            VirtualDetail = attachError;
            VirtualOk = false;
        }
        else
        {
            VirtualTitle = Loc.T("Wird eingebunden …", "Attaching …");
            VirtualDetail = Loc.T("usbip-win2 meldet den virtuellen DualSense bei Windows an.", "usbip-win2 registers the virtual DualSense with Windows.");
            VirtualOk = false;
        }

        bool attached = attachedCount > 0;
        bool haptics = bridges.Any(b => b.HapticsActive);
        bool sound = bridges.Any(b => b.SpeakerActive);
        if (attached && (haptics || sound))
        {
            HapticsTitle = haptics && sound ? Loc.T("Haptik und Ton aktiv", "Haptics and sound active")
                : haptics ? Loc.T("Haptik aktiv", "Haptics active")
                : Loc.T("Ton aktiv", "Sound active");
            HapticsDetail = sound && !haptics
                ? (bridges.Any(b => b.SpeakerActive && b.Headphones)
                    ? Loc.T("Ton läuft über die Kopfhörerbuchse des Controllers.", "Sound plays on the controller's headphone jack.")
                    : Loc.T("Ton läuft über den Lautsprecher des Controllers.", "Sound plays on the controller speaker."))
                : Loc.T("Ein Spiel sendet HD-Haptik an den Controller.", "A game is sending HD haptics to the controller.");
            HapticsOk = true;
        }
        else if (attached)
        {
            HapticsTitle = Loc.T("Haptik bereit", "Haptics ready");
            HapticsDetail = Loc.T("Wartet auf ein Spiel. Spiele nach der Bridge starten.", "Waiting for a game. Start games after the bridge.");
            HapticsOk = true;
        }
        else
        {
            HapticsTitle = Loc.T("HD-Haptik", "HD haptics");
            HapticsDetail = Loc.T("Verfügbar, sobald der virtuelle Controller eingebunden ist.", "Available once the virtual controller is attached.");
            HapticsOk = false;
        }
        CanTest = attached && !IsTesting;

        UpdateTitle = app.AvailableUpdate is { } update
            ? Loc.T($"Version {update.Version} verfügbar", $"Version {update.Version} available")
            : null;
    }

    /// <summary>Card text for one controller.</summary>
    private static (string Title, string Detail, bool Ok) Describe(Bridge bridge, bool many)
    {
        string player = many ? $"Controller {bridge.Slot + 1} · " : "";
        if (!bridge.Connected)
        {
            return (Loc.T("Controller getrennt", "Controller disconnected"),
                player + Loc.T("PS-Taste drücken. Der virtuelle Controller bleibt so lange eingesteckt, laufende Spiele merken nichts.",
                    "Press the PS button. The virtual controller stays plugged in meanwhile, running games don't notice."),
                false);
        }
        string battery = bridge.Battery is int level
            ? Loc.T($" · Akku {level} %", $" · Battery {level} %") + (bridge.Charging ? Loc.T(", lädt", ", charging") : "")
            : "";
        string headphones = bridge.Headphones ? Loc.T(" · Kopfhörer", " · headphones") : "";
        return (bridge.ControllerName, player + Loc.T("Über Bluetooth verbunden", "Connected via Bluetooth") + battery + headphones, true);
    }

    /// <summary>Checks the drivers. Starts HidHideCLI, so only on opening and after setup steps.</summary>
    public async Task RefreshSetupAsync()
    {
        hidHide = await Task.Run(Dependencies.QueryHidHide);
        if (!Dependencies.UsbIpInstalled)
        {
            ShowIssue(Loc.T("usbip-win2 fehlt", "usbip-win2 missing"),
                Loc.T("Stellt den virtuellen USB-Controller bereit. Wird von GitHub geladen; Windows fragt nach Admin-Rechten.",
                    "Provides the virtual USB controller. Downloaded from GitHub; Windows asks for admin rights."),
                Loc.T("Installieren", "Install"), () => Install(Dependencies.UsbIp));
        }
        else if (!Dependencies.HidHideInstalled)
        {
            ShowIssue(Loc.T("HidHide fehlt", "HidHide missing"),
                Loc.T("Versteckt den Bluetooth-Controller vor Spielen, damit sie nur den virtuellen sehen.",
                    "Hides the Bluetooth controller from games so they only see the virtual one."),
                Loc.T("Installieren", "Install"), () => Install(Dependencies.HidHide));
        }
        else if (hidHide is not { Ready: true })
        {
            ShowIssue(Loc.T("HidHide einrichten", "Set up HidHide"),
                hidHide is { NewController: true }
                    ? Loc.T("Ein weiterer Controller ist verbunden: einrichten, damit er vor Spielen versteckt wird (Admin-Rechte).",
                        "Another controller is connected: set up to hide it from games as well (admin rights).")
                    : hidHide is { AppRegistered: true, ControllerHidden: false }
                    ? Loc.T("Controller einschalten, dann einrichten: er wird vor Spielen versteckt.",
                        "Switch the controller on, then set up: it gets hidden from games.")
                    : Loc.T("Diese App freigeben und den Bluetooth-Controller vor Spielen verstecken (Admin-Rechte).",
                        "Allow this app and hide the Bluetooth controller from games (admin rights)."),
                Loc.T("Einrichten", "Set up"), ConfigureHidHide);
        }
        else if (settings.NativeGames.Where(g => !hidHide!.Allows(g)).ToList() is { Count: > 0 } blocked)
        {
            string names = string.Join(", ", blocked.Select(NativeGames.DisplayName));
            ShowIssue(Loc.T("Spiele freigeben", "Allow games"),
                Loc.T($"{names} steuert die Bluetooth-Haptik selbst und muss den Bluetooth-Controller sehen dürfen (Admin-Rechte).",
                    $"{names} drives the Bluetooth haptics itself and must be allowed to see the Bluetooth controller (admin rights)."),
                Loc.T("Freigeben", "Allow"), async () =>
                {
                    BusyText = Loc.T("Wird freigegeben – bitte Windows-Abfrage bestätigen …", "Allowing – please confirm the Windows prompt …");
                    if (!await Dependencies.SetGamesAllowedAsync(blocked, allowed: true))
                        app.ShowHint("HidHide", Loc.T("Freigabe abgebrochen oder fehlgeschlagen.", "Allowing was cancelled or failed."));
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
            app.ShowHint(Loc.T("Einrichtung fehlgeschlagen", "Setup failed"), e.Message);
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
        BusyText = Loc.T("HidHide wird eingerichtet – bitte Windows-Abfrage bestätigen …", "Setting up HidHide – please confirm the Windows prompt …");
        if (!await Dependencies.ConfigureHidHideAsync())
        {
            app.ShowHint("HidHide", Loc.T("Einrichtung abgebrochen oder fehlgeschlagen.", "Setup was cancelled or failed."));
        }
        app.RestartBridge();
    }

    private async void InstallUpdate()
    {
        if (!UpdateIdle) return;
        try
        {
            await app.InstallUpdateAsync(new Progress<string>(text => UpdateBusy = text));
        }
        catch (Exception e)
        {
            AppLog.Write($"Update fehlgeschlagen: {e}");
            app.ShowHint(Loc.T("Update fehlgeschlagen", "Update failed"), e.Message);
        }
        finally
        {
            UpdateBusy = null;
        }
    }

    private async void CheckUpdate()
    {
        UpdateStatus = Loc.T("Suche …", "Checking …");
        await app.CheckForUpdateAsync(manual: true);
        UpdateStatus = app.AvailableUpdate is { } update
            ? Loc.T($"Version {update.Version} verfügbar – siehe Startseite.", $"Version {update.Version} available – see the main page.")
            : Loc.T("Du hast die neueste Version.", "You're up to date.");
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
            app.ShowHint("Test", Loc.T("Das Testmuster konnte nicht abgespielt werden, Details im Log.",
                "The test pattern could not be played, details in the log."));
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

/// <summary>Card of a further controller (player 2 and up).</summary>
internal sealed class ControllerEntry(Bridge? bridge) : ViewModelBase
{
    private string title = "", detail = "";
    private bool isOk, canPowerOff;

    public Bridge? Bridge => bridge;
    public string Title { get => title; set => Set(ref title, value); }
    public string Detail { get => detail; set => Set(ref detail, value); }
    public bool IsOk { get => isOk; set => Set(ref isOk, value); }
    public bool CanPowerOff { get => canPowerOff; set => Set(ref canPowerOff, value); }
    public ICommand? PowerCommand { get; init; }
}
