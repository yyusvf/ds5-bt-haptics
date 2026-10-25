using Microsoft.Win32;

namespace DS5BtHaptics.App;

/// <summary>
/// "Start with Windows" via the user's Run key. The entry carries <see cref="QuietArgument"/> so
/// DS5 BT Haptics starts silently in the notification area.
/// </summary>
internal static class Autostart
{
    public const string QuietArgument = "--tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DS5BtHaptics";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {QuietArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Points an existing entry at the current exe path, e.g. after the exe was moved.</summary>
    public static void Refresh()
    {
        if (IsEnabled())
        {
            try
            {
                SetEnabled(true);
            }
            catch
            {
                // Not critical.
            }
        }
    }
}
