using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace DS5BtHaptics.App.Ui.Theme;

/// <summary>
/// Colours the accent brushes with the accent colour set in Windows: checkmarks, switches, radio
/// dots and so on. The indigo in Dark.xaml / Light.xaml is the fallback if it can't be read.
///
/// Brushes are replaced, not recoloured: WPF freezes brushes from dictionaries. The new ones go
/// into the application's own resources, which take precedence over merged dictionaries.
/// </summary>
internal static class SystemAccent
{
    /// <summary>
    /// Minimum luminance of the accent on dark backgrounds - Windows also uses a lighter shade in
    /// dark mode, a dark blue would barely stand out from #202020.
    /// </summary>
    private const double MinimumLuminance = 0.30;

    /// <summary>The counterpart for light backgrounds, where a bright accent would vanish on white.</summary>
    private const double MaximumLuminance = 0.32;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colour, [MarshalAs(UnmanagedType.Bool)] out bool opaque);

    private static Application? host;

    /// <summary>Applies the colours and follows changes made in Windows.</summary>
    public static void Attach(Application application)
    {
        host = application;
        Apply(application);

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                application.Dispatcher.BeginInvoke(() => Apply(application));
            }
        };
    }

    /// <summary>Recompute because the palette underneath changed.</summary>
    public static void Refresh()
    {
        if (host != null)
        {
            Apply(host);
        }
    }

    private static void Apply(Application application)
    {
        if (Read() is not Color system)
        {
            return;
        }

        Color accent = ThemeManager.IsDark
            ? Lighten(system, MinimumLuminance)
            : Darken(system, MaximumLuminance);

        Set(application, "AccentBrush", accent);
        Set(application, "AccentHoverBrush",
            Blend(accent, ThemeManager.IsDark ? Colors.White : Colors.Black, 0.18));
        Set(application, "AccentSoftBrush", Color.FromArgb(0x26, accent.R, accent.G, accent.B));

        // Content on the accent uses whichever of white or near-black contrasts more.
        Color dark = Color.FromRgb(0x20, 0x20, 0x20);
        Set(application, "OnAccentBrush",
            Contrast(accent, dark) >= Contrast(accent, Colors.White) ? dark : Colors.White);
    }

    private static void Set(Application application, string key, Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        application.Resources[key] = brush;
    }

    /// <summary>
    /// The accent colour. From the registry first, where it is stored as ABGR; otherwise from
    /// the window manager as ARGB.
    /// </summary>
    private static Color? Read()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null)
                is int stored)
            {
                uint abgr = unchecked((uint)stored);
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch
        {
            // Fall back to the window manager.
        }

        if (DwmGetColorizationColor(out uint argb, out _) == 0)
        {
            return Color.FromRgb((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        }

        return null;
    }

    private static Color Lighten(Color colour, double target)
    {
        Color result = colour;
        for (int i = 0; i < 8 && Luminance(result) < target; i++)
        {
            result = Blend(result, Colors.White, 0.15);
        }
        return result;
    }

    private static Color Darken(Color colour, double target)
    {
        Color result = colour;
        for (int i = 0; i < 8 && Luminance(result) > target; i++)
        {
            result = Blend(result, Colors.Black, 0.15);
        }
        return result;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    /// <summary>Perceived sRGB luminance - green weighs far more than blue.</summary>
    private static double Luminance(Color colour) =>
        0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);

    /// <summary>WCAG contrast ratio, from 1 (identical) to 21 (black on white).</summary>
    private static double Contrast(Color a, Color b)
    {
        double first = Luminance(a);
        double second = Luminance(b);
        return first > second
            ? (first + 0.05) / (second + 0.05)
            : (second + 0.05) / (first + 0.05);
    }

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
