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

    /// <summary>Off means the app only sits in the tray and the controller stays plain Bluetooth.</summary>
    public bool BridgeEnabled { get; set; } = true;

    public float Gain { get; set; } = 1.0f;

    /// <summary>One frame per 0x32 report: lower latency, twice the Bluetooth traffic.</summary>
    public bool LowLatency { get; set; }

    /// <summary>Report the controller's real MAC to Windows - drops the Bluetooth link when Steam runs.</summary>
    public bool RealMac { get; set; }

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
    };
}
