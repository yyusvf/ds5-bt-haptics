using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS5BtHaptics.App;

internal enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Everything the app remembers, in %APPDATA%\DS5BtHaptics\settings.json. Saved immediately on
/// every change - there is no save button.
/// </summary>
internal sealed class AppSettings
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS5BtHaptics");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public Ui.AppLanguage Language { get; set; } = Ui.AppLanguage.System;

    /// <summary>Windows sound of the virtual pad on the controller speaker / headphone jack.</summary>
    public bool Speaker { get; set; } = true;

    /// <summary>Mute the controller audio while nothing plays; stops a quiet whine on headphones.</summary>
    public bool MuteWhenSilent { get; set; } = true;

    /// <summary>Switch a controller off after this many minutes without input; 0 = never.</summary>
    public int IdleMinutes { get; set; }

    /// <summary>Tray notification at 20 % and 10 % battery.</summary>
    public bool BatteryWarning { get; set; } = true;

    /// <summary>Screen corner of the notification banner.</summary>
    public Ui.Views.NotificationPosition NotificationPosition { get; set; } = Ui.Views.NotificationPosition.BottomRight;

    /// <summary>Tray notification when a controller connects or disconnects.</summary>
    public bool ConnectionHints { get; set; } = true;

    /// <summary>Look for a new release on GitHub at start and once a day.</summary>
    public bool UpdateCheck { get; set; } = true;

    /// <summary>Off means the app only sits in the tray and the controller stays plain Bluetooth.</summary>
    public bool BridgeEnabled { get; set; } = true;

    public float Gain { get; set; } = 1.0f;

    /// <summary>One frame per 0x32 report: lower latency, twice the Bluetooth traffic.</summary>
    public bool LowLatency { get; set; }

    /// <summary>Report the controller's real MAC to Windows - drops the Bluetooth link when Steam runs.</summary>
    public bool RealMac { get; set; }

    /// <summary>Full exe paths of games with their own Bluetooth haptics, see <see cref="App.NativeGames"/>.</summary>
    public List<string> NativeGames { get; set; } = [];

    /// <summary>Known games were looked for once; removing one from the list then sticks.</summary>
    public bool NativeGamesSearched { get; set; }

    [JsonIgnore]
    public bool IsFirstRun { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) is { } loaded)
            {
                loaded.Gain = Math.Clamp(loaded.Gain, 0.25f, 4f);
                loaded.NativeGames ??= [];
                loaded.IdleMinutes = Math.Clamp(loaded.IdleMinutes, 0, 240);
                return loaded;
            }
        }
        catch
        {
            // Broken file - better to start with defaults than not at all.
        }

        return new AppSettings { IsFirstRun = !File.Exists(FilePath) };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Not writable - the setting then only lasts until exit.
        }
    }

    public Options ToOptions() => new()
    {
        Command = "bridge",
        Gain = Gain,
        Single = LowLatency,
        RealMac = RealMac,
        Speaker = Speaker,
        MuteWhenSilent = MuteWhenSilent,
    };
}
