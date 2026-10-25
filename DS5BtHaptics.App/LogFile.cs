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
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Sink = _ => { };
            return;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, Path.Combine(Folder, "bridge.old.log"), overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Old log locked or gone: not worth losing this session's log over.
        }

        // A previous instance that is still shutting down may hold bridge.log; then log beside it.
        writer = Open(FilePath) ?? Open(Path.Combine(Folder, $"bridge-{Environment.ProcessId}.log"));
        if (writer is not { } log)
        {
            AppLog.Sink = _ => { };
            return;
        }
        AppLog.Sink = message =>
        {
            try { log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message.TrimEnd()}"); }
            catch (IOException) { }
        };
    }

    private static TextWriter? Open(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return TextWriter.Synchronized(new StreamWriter(stream) { AutoFlush = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
