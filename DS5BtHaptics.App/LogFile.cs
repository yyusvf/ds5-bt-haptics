using System.IO;

namespace DS5BtHaptics.App;

/// <summary>
/// The bridge log in %LOCALAPPDATA%\DS5BtHaptics\bridge.log. Stays on this PC: it contains the
/// controller's Bluetooth address. The previous session is kept as bridge.old.log.
/// </summary>
internal static class LogFile
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DS5BtHaptics");

    public static readonly string FilePath = Path.Combine(Folder, "bridge.log");

    private static TextWriter? writer;

    public static void Attach()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, Path.Combine(Folder, "bridge.old.log"), overwrite: true);
            }
            writer = TextWriter.Synchronized(new StreamWriter(FilePath, append: false) { AutoFlush = true });
            AppLog.Sink = message =>
            {
                try { writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message.TrimEnd()}"); }
                catch (IOException) { }
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Sink = _ => { };
        }
    }
}
