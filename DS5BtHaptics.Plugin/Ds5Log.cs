using System.IO;
using DS5BtHaptics;

namespace DS5BtHaptics.Plugin;

/// <summary>
/// The DS5 logs in %LOCALAPPDATA%\ysfUtil: ds5-bridge.log from the bridge process, ds5.log from
/// setup steps in ysfUtil itself. They stay on this PC: they contain the controller's Bluetooth
/// address. The previous session is kept as *.old.log.
/// </summary>
internal static class Ds5Log
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ysfUtil");

    public static string BridgeLog => Path.Combine(Folder, "ds5-bridge.log");

    private static TextWriter? writer;

    /// <summary>Points <see cref="AppLog"/> at <paramref name="name"/>.log, once per process.</summary>
    public static void Attach(string name)
    {
        if (writer != null) return;
        string path = Path.Combine(Folder, name + ".log");
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(path)) File.Copy(path, Path.Combine(Folder, name + ".old.log"), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Old log locked or gone: not worth losing this session's log over.
        }

        // A previous bridge that is still shutting down may hold the file; then log beside it.
        writer = Open(path) ?? Open(Path.Combine(Folder, $"{name}-{Environment.ProcessId}.log"));
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
