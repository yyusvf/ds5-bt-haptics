using DS5BtHaptics.App.Ui.Shell;

namespace DS5BtHaptics.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper started by the flyout's "Einrichten" button.
        if (args.Contains(Setup.Dependencies.HidHideSetupArgument))
        {
            return Setup.Dependencies.RunElevatedHidHideSetup();
        }
        if (args.Contains(Setup.Dependencies.HidHideAppsArgument))
        {
            return Setup.Dependencies.RunElevatedHidHideApps(args);
        }
        if (args.Contains(Setup.Dependencies.HidHideUndoArgument))
        {
            return Setup.Dependencies.RunElevatedHidHideUndo();
        }
#if DEBUG
        // Rewrites DS5BtHaptics.ico from the drawing in AppIconArt.
        int iconAt = Array.FindIndex(args, a => string.Equals(a, "--makeicon", StringComparison.OrdinalIgnoreCase));
        if (iconAt >= 0 && iconAt + 1 < args.Length)
        {
            Ui.AppIconFile.Write(args[iconAt + 1]);
            return 0;
        }

        // Renders the flyout to a PNG without putting it on screen:
        // --render <file.png> [light|dark] [settings]
        int renderAt = Array.FindIndex(args, a => string.Equals(a, "--render", StringComparison.OrdinalIgnoreCase));
        if (renderAt >= 0 && renderAt + 1 < args.Length)
        {
            Render(args, args[renderAt + 1]);
            return 0;
        }
#endif

        bool quiet = args.Any(a => string.Equals(a, Autostart.QuietArgument, StringComparison.OrdinalIgnoreCase));

        // Already running? Then that instance opens its flyout and this one exits.
        if (!SingleInstance.TryAcquire())
        {
            if (!quiet)
            {
                SingleInstance.SignalExistingInstance();
            }
            return 0;
        }

        LogFile.Attach();
        AppLog.Write($"DS5 BT Haptics {typeof(Program).Assembly.GetName().Version?.ToString(3)} gestartet");
        AppSettings settings = AppSettings.Load();
        System.Windows.Application application = WpfHost.Ensure(settings.Theme);
        application.DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Write($"Unerwarteter Fehler: {e.Exception}");
            System.Windows.MessageBox.Show(
                "Unerwarteter Fehler: " + e.Exception.Message,
                "DS5 BT Haptics",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            e.Handled = true;
        };

        using (new AppController(settings, quiet))
        {
            application.Run();
        }

        AppLog.Write("Beendet");
        SingleInstance.Release();
        return 0;
    }

#if DEBUG
    private static void Render(string[] args, string path)
    {
        WpfHost.Ensure(ThemeMode.System);
        if (args.Any(a => string.Equals(a, "light", StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Theme.ThemeManager.SetMode(ThemeMode.Light);
        }
        else if (args.Any(a => string.Equals(a, "dark", StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Theme.ThemeManager.SetMode(ThemeMode.Dark);
        }

        // A preview: settings are never saved and no bridge runs.
        var settings = new AppSettings();
        settings.NativeGames.AddRange(NativeGames.FindInstalled());
        var model = new Ui.ViewModels.MainViewModel(settings, null!);
        model.Preview(args.Any(a => string.Equals(a, "setup", StringComparison.OrdinalIgnoreCase)));
        var flyout = new Ui.Flyout.FlyoutWindow(model);
        if (args.Any(a => string.Equals(a, "settings", StringComparison.OrdinalIgnoreCase)))
        {
            flyout.ShowSettingsView();
        }

        DesignRender.Capture(flyout, path, 360, 0);
    }
#endif
}
