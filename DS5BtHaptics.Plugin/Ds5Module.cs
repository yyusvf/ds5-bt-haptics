using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DS5BtHaptics;
using YsfUtil.Ui.ViewModels;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;

namespace DS5BtHaptics.Plugin;

/// <summary>One line in the games list of the settings.</summary>
internal sealed record GameEntry(string Name, string Path, ICommand RemoveCommand);

/// <summary>
/// DualSense HD haptics over Bluetooth - the standalone DS5 BT Haptics as a module. The bridge
/// runs in its own process (see <see cref="BridgeProcess"/>); this class starts and stops it,
/// pauses it while a game with its own Bluetooth haptics runs, sets up the drivers and shows status.
/// </summary>
internal sealed class Ds5Module : ViewModelBase, IModule
{
    /// <summary>More crashes than this within <see cref="CrashWindow"/> and the bridge stays off.</summary>
    private const int MaxCrashes = 3;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(1);

    private readonly IModuleContext context;
    private readonly Ds5Settings settings;
    private readonly BridgeProcess bridge = new();
    private readonly DispatcherTimer gameWatch = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer restartTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<DateTime> crashes = [];
    private readonly Ds5FlyoutView flyoutView;
    private readonly Ds5SettingsView settingsView;
    private bool refreshQueued;
    private bool disposed;
    private string? crashError;
    private string? runningGame;

    /// <summary>Set by "Activate anyway"; lasts until that game exits.</summary>
    private string? forcedFor;

    private string summary = "", controllerTitle = "", controllerDetail = "";
    private string virtualTitle = "", virtualDetail = "", hapticsTitle = "", hapticsDetail = "";
    private bool controllerOk, virtualOk, hapticsOk, canTest, isTesting, summaryIsProblem;
    private string? forceLabel, setupTitle, setupDetail, setupAction, busyText;
    private Func<Task>? setupRun;
    private HidHideStatus? hidHide;

    public Ds5Module(IModuleContext context)
    {
        this.context = context;
        if (!context.IsPreview) Ds5Log.Attach("ds5");
        Dependencies.CreateProcess = context.CreateProcess;
        bool imported = false;
        settings = context.LoadSettings(() =>
        {
            // First start: take over the standalone app's settings.
            imported = true;
            return Ds5Settings.ImportStandalone();
        });
        if (imported) Save();
        if (!settings.NativeGamesSearched)
        {
            settings.NativeGames.AddRange(NativeGames.FindInstalled()
                .Where(g => !settings.NativeGames.Contains(g, StringComparer.OrdinalIgnoreCase)));
            settings.NativeGamesSearched = true;
            Save();
        }

        TestCommand = new RelayCommand(RunTest);
        RestartCommand = new RelayCommand(RestartBridge);
        OpenLogCommand = new RelayCommand(OpenLog);
        SetupCommand = new RelayCommand(RunSetup);
        AddGameCommand = new RelayCommand(AddGame);
        UndoHidHideCommand = new RelayCommand(UndoHidHide);
        ForceCommand = new RelayCommand(() => SetForced(PausedFor != null));
        Games = new ObservableCollection<GameEntry>(settings.NativeGames.Select(Entry));

        bridge.Changed += QueueRefresh;
        bridge.Crashed += message => Application.Current.Dispatcher.BeginInvoke(() => OnCrashed(message));
        gameWatch.Tick += (_, _) => CheckGames();
        restartTimer.Tick += (_, _) =>
        {
            restartTimer.Stop();
            ApplyBridgeEnabled();
        };

        flyoutView = new Ds5FlyoutView { DataContext = this };
        settingsView = new Ds5SettingsView { DataContext = this };

        if (context.IsPreview)
        {
            Refresh();
            return;
        }
        // Before the bridge starts: a game that is already running keeps the real controller.
        CheckGames();
        gameWatch.Start();
        ApplyBridgeEnabled();
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            await RefreshSetupAsync();
            if (!disposed && HasSetupIssue)
                context.ShowHint("DS5 BT Haptics", "Setup incomplete. Open ysfUtil to finish it.", ToolTipIcon.Info);
        });
    }

    // ══ Module ══════════════════════════════════════════════════════════════

    public IReadOnlyList<FeatureViewModel> Features => [];

    public IReadOnlyList<ModuleCard> FlyoutCards => [new(ModuleCategory.Controller, 0, flyoutView)];

    public IReadOnlyList<ModuleCard> SettingsCards => [new(ModuleCategory.Controller, 0, settingsView)];

    public bool HasVisibleContent => true;

    public event Action? ContentChanged { add { } remove { } }

    public string? UpdateBlocker => null;

    public void OnFlyoutOpening()
    {
        Refresh();
        if (!context.IsPreview) _ = RefreshSetupAsync();
    }

    // ══ Bridge ══════════════════════════════════════════════════════════════

    /// <summary>The running game the bridge steps aside for, or null.</summary>
    private string? PausedFor => runningGame != null && !string.Equals(runningGame, forcedFor, StringComparison.OrdinalIgnoreCase)
        ? runningGame
        : null;

    /// <summary>The running listed game the user switched the bridge on for anyway, or null.</summary>
    private string? ForcedFor => runningGame != null && string.Equals(runningGame, forcedFor, StringComparison.OrdinalIgnoreCase)
        ? runningGame
        : null;

    private void ApplyBridgeEnabled()
    {
        if (disposed || context.IsPreview) return;
        bool wanted = settings.BridgeEnabled && PausedFor == null && crashError == null;
        if (wanted && !bridge.IsRunning)
        {
            try
            {
                bridge.Start(settings, context.CreateProcess);
            }
            catch (Exception e)
            {
                crashError = e.Message;
            }
        }
        else if (!wanted && bridge.IsRunning)
        {
            bridge.Stop();
        }
        Refresh();
    }

    /// <summary>New options (MAC) or a new driver: start over with a fresh bridge.</summary>
    private void RestartBridge()
    {
        AppLog.Write("Restarting the bridge ...");
        crashError = null;
        crashes.Clear();
        bridge.Stop();
        ApplyBridgeEnabled();
    }

    private void OnCrashed(string message)
    {
        if (disposed) return;
        AppLog.Write(message);
        DateTime now = DateTime.UtcNow;
        crashes.RemoveAll(t => now - t > CrashWindow);
        crashes.Add(now);
        if (crashes.Count > MaxCrashes)
        {
            crashError = message + " Restart it in Settings once the cause is fixed.";
            context.ShowHint("DS5 BT Haptics", "The bridge keeps stopping and was turned off.", ToolTipIcon.Error);
        }
        else
        {
            restartTimer.Start();
        }
        Refresh();
    }

    private void SetForced(bool forced)
    {
        if (runningGame == null) return;
        forcedFor = forced ? runningGame : null;
        AppLog.Write(forced
            ? $"Bridge activated despite {NativeGames.DisplayName(runningGame)}"
            : $"Bridge paused again for {NativeGames.DisplayName(runningGame)}");
        ApplyBridgeEnabled();
    }

    /// <summary>Pauses the bridge while a listed game runs and resumes once it has exited.</summary>
    private void CheckGames()
    {
        string? game = settings.NativeGames.Count == 0 ? null : NativeGames.FindRunning(settings.NativeGames);
        if (string.Equals(game, runningGame, StringComparison.OrdinalIgnoreCase)) return;
        runningGame = game;
        forcedFor = null;
        AppLog.Write(game != null
            ? $"Bridge paused: {NativeGames.DisplayName(game)} drives the Bluetooth haptics itself"
            : "Game exited, bridge resumes");
        ApplyBridgeEnabled();
    }

    /// <summary>Status lines can arrive several times a second; fold them into one refresh.</summary>
    private void QueueRefresh()
    {
        if (refreshQueued || disposed) return;
        refreshQueued = true;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            refreshQueued = false;
            if (!disposed && context.IsFlyoutVisible) Refresh();
        });
    }

    // ══ Bindings ════════════════════════════════════════════════════════════

    public string Summary { get => summary; private set => Set(ref summary, value); }
    public bool SummaryIsProblem { get => summaryIsProblem; private set => Set(ref summaryIsProblem, value); }

    public string ControllerTitle { get => controllerTitle; private set => Set(ref controllerTitle, value); }
    public string ControllerDetail { get => controllerDetail; private set => Set(ref controllerDetail, value); }
    public bool ControllerOk { get => controllerOk; private set => Set(ref controllerOk, value); }

    public string VirtualTitle { get => virtualTitle; private set => Set(ref virtualTitle, value); }
    public string VirtualDetail { get => virtualDetail; private set => Set(ref virtualDetail, value); }
    public bool VirtualOk { get => virtualOk; private set => Set(ref virtualOk, value); }

    public string HapticsTitle { get => hapticsTitle; private set => Set(ref hapticsTitle, value); }
    public string HapticsDetail { get => hapticsDetail; private set => Set(ref hapticsDetail, value); }
    public bool HapticsOk { get => hapticsOk; private set => Set(ref hapticsOk, value); }

    /// <summary>Button text while a listed game runs; null hides it.</summary>
    public string? ForceLabel
    {
        get => forceLabel;
        private set
        {
            if (Set(ref forceLabel, value)) Raise(nameof(HasForce));
        }
    }

    public bool HasForce => forceLabel != null;

    public bool CanTest { get => canTest; private set => Set(ref canTest, value); }

    public bool IsTesting
    {
        get => isTesting;
        private set
        {
            if (Set(ref isTesting, value)) Raise(nameof(TestLabel));
        }
    }

    public string TestLabel => isTesting ? "Playing …" : "Test";

    public bool BridgeEnabled
    {
        get => settings.BridgeEnabled;
        set
        {
            if (settings.BridgeEnabled == value) return;
            settings.BridgeEnabled = value;
            Save();
            Raise();
            if (value)
            {
                crashError = null;
                crashes.Clear();
            }
            ApplyBridgeEnabled();
        }
    }

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
            if (Set(ref busyText, value))
            {
                Raise(nameof(IsIdle));
                Raise(nameof(IsBusy));
            }
        }
    }

    public bool IsIdle => busyText == null;
    public bool IsBusy => busyText != null;

    public double Gain
    {
        get => settings.Gain;
        set
        {
            float gain = (float)Math.Round(Math.Clamp(value, 0.25, 4.0), 2);
            if (Math.Abs(settings.Gain - gain) < 0.001f) return;
            settings.Gain = gain;
            Save();
            bridge.Send(new BridgeCommand(gain, null));
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
            Save();
            Raise();
            // Applied live: a restart would unplug the virtual pad under a running game.
            bridge.Send(new BridgeCommand(null, value));
        }
    }

    public bool RealMac
    {
        get => settings.RealMac;
        set
        {
            if (settings.RealMac == value) return;
            settings.RealMac = value;
            Save();
            Raise();
            RestartBridge();
        }
    }

    public ICommand TestCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand SetupCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand UndoHidHideCommand { get; }
    public ICommand ForceCommand { get; }

    // ══ Games with their own Bluetooth haptics ═══════════════════════════════

    public ObservableCollection<GameEntry> Games { get; }

    private GameEntry Entry(string exe) => new(NativeGames.DisplayName(exe), exe, new RelayCommand(() => RemoveGame(exe)));

    private async void AddGame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a game with its own Bluetooth haptics",
            Filter = "Game (*.exe)|*.exe",
        };
        if (context.PickFile(dialog) is not { } exe) return;
        if (settings.NativeGames.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;

        settings.NativeGames.Add(exe);
        Games.Add(Entry(exe));
        GamesChanged();
        // The game has to see the Bluetooth pad that HidHide hides from everyone else.
        if (Dependencies.HidHideInstalled && !await Dependencies.SetGamesAllowedAsync([exe], allowed: true))
        {
            context.ShowHint("HidHide", "Game not allowed. It won't see the Bluetooth controller otherwise.", ToolTipIcon.Warning);
        }
        await RefreshSetupAsync();
    }

    private async void RemoveGame(string exe)
    {
        settings.NativeGames.RemoveAll(g => string.Equals(g, exe, StringComparison.OrdinalIgnoreCase));
        if (Games.FirstOrDefault(g => g.Path == exe) is { } entry) Games.Remove(entry);
        GamesChanged();
        // Otherwise the game would see both controllers next time.
        if (hidHide?.Allows(exe) == true)
        {
            await Dependencies.SetGamesAllowedAsync([exe], allowed: false);
        }
        await RefreshSetupAsync();
    }

    private void GamesChanged()
    {
        Save();
        CheckGames();
    }

    // ══ Refresh ═════════════════════════════════════════════════════════════

    /// <summary>Turns the last status from the bridge process into the cards' text.</summary>
    private void Refresh()
    {
        BridgeStatus? status = bridge.Status;
        bool hasBridge = status?.HasBridge == true;

        ForceLabel = PausedFor != null ? "Activate anyway" : ForcedFor != null ? "Pause" : null;
        if (PausedFor is { } game)
        {
            ControllerTitle = $"Paused for {NativeGames.DisplayName(game)}";
            ControllerDetail = "The game drives the Bluetooth haptics itself. The bridge resumes once it exits.";
            ControllerOk = false;
        }
        else if (crashError != null)
        {
            ControllerTitle = "Bridge stopped";
            ControllerDetail = crashError;
            ControllerOk = false;
        }
        else if (!settings.BridgeEnabled)
        {
            ControllerTitle = "Bridge off";
            ControllerDetail = "The controller stays a plain Bluetooth gamepad.";
            ControllerOk = false;
        }
        else if (status == null || status.State == BridgeState.Stopped)
        {
            ControllerTitle = "Starting …";
            ControllerDetail = "The bridge is starting.";
            ControllerOk = false;
        }
        else if (!hasBridge)
        {
            ControllerTitle = "No controller";
            ControllerDetail = status.LastError is { } error
                ? "Error: " + error
                : "Press the PS button to connect the DualSense over Bluetooth.";
            ControllerOk = false;
        }
        else if (!status.Connected)
        {
            ControllerTitle = "Controller disconnected";
            ControllerDetail = "Press the PS button. The virtual controller stays plugged in, running games won't notice.";
            ControllerOk = false;
        }
        else
        {
            ControllerTitle = status.ControllerName;
            string battery = status.Battery is int level ? $" · Battery {level}%{(status.Charging ? ", charging" : "")}" : "";
            ControllerDetail = "Connected over Bluetooth" + battery;
            ControllerOk = true;
        }

        bool attached = hasBridge && status!.HostAttached;
        if (!hasBridge)
        {
            VirtualTitle = "Virtual USB controller";
            VirtualDetail = "Appears once the controller is connected.";
            VirtualOk = false;
        }
        else if (attached)
        {
            VirtualTitle = "Attached as USB controller";
            VirtualDetail = "Games see a wired DualSense.";
            VirtualOk = true;
        }
        else if (!Dependencies.UsbIpInstalled)
        {
            VirtualTitle = "Not attached";
            VirtualDetail = "usbip-win2 is missing, see above.";
            VirtualOk = false;
        }
        else if (status!.AttachError is { } attachError)
        {
            VirtualTitle = "Attaching failed";
            VirtualDetail = attachError;
            VirtualOk = false;
        }
        else
        {
            VirtualTitle = "Attaching …";
            VirtualDetail = "usbip-win2 announces the virtual DualSense to Windows.";
            VirtualOk = false;
        }

        if (attached && status!.HapticsActive)
        {
            HapticsTitle = "Haptics active";
            HapticsDetail = "A game is sending HD haptics to the controller.";
            HapticsOk = true;
        }
        else if (attached)
        {
            HapticsTitle = "Haptics ready";
            HapticsDetail = "Waiting for a game. Start games after the bridge.";
            HapticsOk = true;
        }
        else
        {
            HapticsTitle = "HD haptics";
            HapticsDetail = "Available once the virtual controller is attached.";
            HapticsOk = false;
        }
        CanTest = attached && !IsTesting;

        (Summary, SummaryIsProblem) = true switch
        {
            _ when PausedFor != null => ($"Paused for {NativeGames.DisplayName(PausedFor)}", false),
            _ when crashError != null => ("Bridge stopped", true),
            _ when !settings.BridgeEnabled => ("Bridge off", false),
            _ when HasSetupIssue => ("Setup incomplete", true),
            _ when attached && status!.HapticsActive => (ControllerDetail.Replace("Connected over Bluetooth", "Connected") + " · Haptics active", false),
            _ when attached => (ControllerDetail.Replace("Connected over Bluetooth", "Connected"), false),
            _ => (ControllerTitle, false),
        };
    }

    /// <summary>Checks the drivers. Starts HidHideCLI, so only on opening and after setup steps.</summary>
    private async Task RefreshSetupAsync()
    {
        hidHide = await Task.Run(Dependencies.QueryHidHide);
        if (disposed) return;
        if (!Dependencies.UsbIpInstalled)
        {
            ShowIssue("usbip-win2 missing",
                "Provides the virtual USB controller. Downloaded from GitHub; Windows asks for admin rights.",
                "Install", () => Install(Dependencies.UsbIp));
        }
        else if (!Dependencies.HidHideInstalled)
        {
            ShowIssue("HidHide missing",
                "Hides the Bluetooth controller from games, so they only see the virtual one.",
                "Install", () => Install(Dependencies.HidHide));
        }
        else if (hidHide is not { Ready: true })
        {
            ShowIssue("Set up HidHide",
                hidHide is { AppRegistered: true, ControllerHidden: false }
                    ? "Turn the controller on, then set up: it gets hidden from games."
                    : "Allow ysfUtil and hide the Bluetooth controller from games (admin rights).",
                "Set up", ConfigureHidHide);
        }
        else if (settings.NativeGames.Where(g => !hidHide!.Allows(g)).ToList() is { Count: > 0 } blocked)
        {
            ShowIssue("Allow games",
                $"{string.Join(", ", blocked.Select(NativeGames.DisplayName))} drives the Bluetooth haptics itself and "
                + "must be allowed to see the Bluetooth controller (admin rights).",
                "Allow", async () =>
                {
                    BusyText = "Allowing, confirm the Windows prompt …";
                    if (!await Dependencies.SetGamesAllowedAsync(blocked, allowed: true))
                        context.ShowHint("HidHide", "Cancelled or failed.", ToolTipIcon.Warning);
                });
        }
        else
        {
            SetupTitle = null;
            SetupDetail = null;
            SetupAction = null;
            setupRun = null;
        }
        Refresh();
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
            AppLog.Write($"Setup failed: {e}");
            context.ShowHint("Setup failed", e.Message, ToolTipIcon.Error);
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
        RestartBridge();
    }

    private async Task ConfigureHidHide()
    {
        BusyText = "Setting up HidHide, confirm the Windows prompt …";
        if (!await Dependencies.ConfigureHidHideAsync())
        {
            context.ShowHint("HidHide", "Setup cancelled or failed.", ToolTipIcon.Warning);
        }
        RestartBridge();
    }

    /// <summary>Makes the Bluetooth controller visible to games again, e.g. before removing the module.</summary>
    private async void UndoHidHide()
    {
        if (!Dependencies.HidHideInstalled || !IsIdle) return;
        BusyText = "Making the controller visible, confirm the Windows prompt …";
        bridge.Stop();
        bool done = await Dependencies.UndoHidHideAsync();
        BusyText = null;
        context.ShowHint("HidHide", done
            ? "Games see the Bluetooth controller again. Turn the bridge off too, or set up HidHide again to use it."
            : "Cancelled or failed.", done ? ToolTipIcon.Info : ToolTipIcon.Warning);
        await RefreshSetupAsync();
        ApplyBridgeEnabled();
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
                AppLog.Write($"Test failed: {e.Message}");
                return -1;
            }
        });
        IsTesting = false;
        if (result != 0)
        {
            context.ShowHint("Test", "The test pattern could not be played, details in the log.", ToolTipIcon.Warning);
        }
        Refresh();
    }

    private static void OpenLog()
    {
        Directory.CreateDirectory(Ds5Log.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(Ds5Log.BridgeLog)
            ? $"/select,\"{Ds5Log.BridgeLog}\""
            : $"\"{Ds5Log.Folder}\"") { UseShellExecute = true });
    }

    private void Save()
    {
        context.SaveSettings(settings);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gameWatch.Stop();
        restartTimer.Stop();
        bridge.Dispose();
    }
}
