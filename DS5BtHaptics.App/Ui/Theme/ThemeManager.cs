using Microsoft.Win32;
using Application = System.Windows.Application;
using ResourceDictionary = System.Windows.ResourceDictionary;

namespace DS5BtHaptics.App.Ui.Theme;

/// <summary>
/// Light or dark - or follow Windows.
///
/// Switching swaps the palette dictionary in the application resources. Templates fetch
/// colours via DynamicResource so the change reaches them; a StaticResource would keep the old
/// brush.
/// </summary>
internal static class ThemeManager
{
    /// <summary>
    /// Position of the palette among the merged dictionaries: after metrics, before templates.
    /// Inserted the first time, replaced afterwards.
    /// </summary>
    private const int PaletteSlot = 1;

    private static Application? host;
    private static bool installed;

    /// <summary>What is configured - not what it resolves to.</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>What it resolves to: for <see cref="ThemeMode.System"/> the Windows setting.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>After every switch. Windows and the tray hook in here.</summary>
    public static event Action? Changed;

    public static void Attach(Application application, ThemeMode mode)
    {
        host = application;
        Mode = mode;
        IsDark = Resolve(mode);
        Swap();

        // Windows reports light/dark changes with the same message as other personalization
        // settings. Only follow them when set to follow Windows.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
            {
                return;
            }

            application.Dispatcher.BeginInvoke(() =>
            {
                if (Mode == ThemeMode.System)
                {
                    SetMode(ThemeMode.System);
                }
            });
        };
    }

    public static void SetMode(ThemeMode mode)
    {
        bool dark = Resolve(mode);
        if (host == null || (mode == Mode && dark == IsDark))
        {
            Mode = mode;
            return;
        }

        Mode = mode;
        IsDark = dark;
        Swap();
        Changed?.Invoke();
    }

    private static void Swap()
    {
        if (host == null)
        {
            return;
        }

        var palette = new ResourceDictionary
        {
            Source = new Uri(
                IsDark ? "pack://application:,,,/Ui/Theme/Dark.xaml"
                       : "pack://application:,,,/Ui/Theme/Light.xaml",
                UriKind.Absolute),
        };

        // Replace rather than add: two palettes at once would shadow each other.
        if (!installed)
        {
            host.Resources.MergedDictionaries.Insert(PaletteSlot, palette);
            installed = true;
        }
        else
        {
            host.Resources.MergedDictionaries[PaletteSlot] = palette;
        }

        // The accent is computed against the palette (darkened on light, lightened on dark).
        SystemAccent.Refresh();
    }

    private static bool Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => SystemPrefersDark(),
    };

    /// <summary>The Windows app theme (AppsUseLightTheme), not the system UI theme.</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);

            return value is int light ? light == 0 : true;
        }
        catch
        {
            return true;
        }
    }
}
