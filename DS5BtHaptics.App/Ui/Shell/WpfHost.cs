using Application = System.Windows.Application;
using ResourceDictionary = System.Windows.ResourceDictionary;
using ShutdownMode = System.Windows.ShutdownMode;

namespace DS5BtHaptics.App.Ui.Shell;

/// <summary>Creates the WPF application by hand, since <see cref="Program"/> stays the entry point.</summary>
internal static class WpfHost
{
    private static Application? application;

    public static Application Ensure(ThemeMode theme = ThemeMode.System)
    {
        if (application != null)
        {
            return application;
        }

        application = new Application
        {
            // Closing the flyout never exits; only Quit does.
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        // Metrics first, since templates fetch them via StaticResource. The palette then goes
        // into its fixed slot, see ThemeManager.
        foreach (string path in new[]
        {
            "Ui/Theme/Metrics.xaml",
            "Ui/Theme/Controls.xaml",
            "Ui/Theme/Inputs.xaml",
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{path}", UriKind.Absolute),
            });
        }

        Theme.SystemAccent.Attach(application);
        Theme.ThemeManager.Attach(application, theme);

        return application;
    }
}
