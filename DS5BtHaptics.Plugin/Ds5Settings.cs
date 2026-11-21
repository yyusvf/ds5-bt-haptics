using System.IO;
using System.Text.Json;

namespace DS5BtHaptics.Plugin;

/// <summary>The DS5 module's settings, saved within ysfUtil's settings.</summary>
internal sealed class Ds5Settings
{
    /// <summary>Off means the controller stays a plain Bluetooth gamepad.</summary>
    public bool BridgeEnabled { get; set; } = true;

    public float Gain { get; set; } = 1.0f;

    /// <summary>One frame per 0x32 report: lower latency, twice the Bluetooth traffic.</summary>
    public bool LowLatency { get; set; }

    /// <summary>Report the controller's real MAC to Windows - drops the Bluetooth link when Steam runs.</summary>
    public bool RealMac { get; set; }

    /// <summary>Full exe paths of games with their own Bluetooth haptics, see <see cref="NativeGames"/>.</summary>
    public List<string> NativeGames { get; set; } = [];

    /// <summary>Known games were looked for once; removing one from the list then sticks.</summary>
    public bool NativeGamesSearched { get; set; }

    /// <summary>
    /// The standalone DS5 BT Haptics app's settings from %APPDATA%\DS5BtHaptics\settings.json, so
    /// strength, latency and the games list carry over.
    /// </summary>
    public static Ds5Settings ImportStandalone()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS5BtHaptics", "settings.json");
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Ds5Settings>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } old)
            {
                old.Gain = Math.Clamp(old.Gain, 0.25f, 4f);
                old.NativeGames ??= [];
                return old;
            }
        }
        catch
        {
            // Unreadable - start fresh rather than not at all.
        }
        return new Ds5Settings();
    }
}
