using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DS5BtHaptics.App;

/// <summary>
/// Games that drive the DualSense's haptics over Bluetooth themselves (report 0x32), such as
/// Cyberpunk 2077. Behind the bridge they only see the virtual wired pad and the two paths get in
/// each other's way, so while one runs the bridge pauses and the game talks to the real controller.
/// </summary>
internal static partial class NativeGames
{
    /// <summary>Known games, relative to a Steam library. Found on first start and added to the list.</summary>
    private static readonly string[] KnownSteamPaths =
    [
        @"steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
    ];

    /// <summary>The first listed game that is running, or null.</summary>
    public static string? FindRunning(IEnumerable<string> games)
    {
        foreach (string game in games)
        {
            Process[] running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game));
            try
            {
                foreach (Process process in running)
                {
                    if (SamePath(process, game))
                    {
                        return game;
                    }
                }
            }
            finally
            {
                foreach (Process process in running) process.Dispose();
            }
        }
        return null;
    }

    /// <summary>"Cyberpunk 2077" from the exe's version info, else its file name.</summary>
    public static string DisplayName(string exe)
    {
        try
        {
            string? product = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            if (!string.IsNullOrEmpty(product)) return product;
        }
        catch (FileNotFoundException)
        {
        }
        return Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>Known games installed in any Steam library on this PC.</summary>
    public static IEnumerable<string> FindInstalled()
    {
        foreach (string library in SteamLibraries())
        {
            foreach (string relative in KnownSteamPaths)
            {
                string exe = Path.Combine(library, relative);
                if (File.Exists(exe)) yield return exe;
            }
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var libraries = new List<string> { steam };
        try
        {
            string vdf = File.ReadAllText(Path.Combine(steam, @"steamapps\libraryfolders.vdf"));
            libraries.AddRange(LibraryPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No Steam: nothing to find.
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();

    private static bool SamePath(Process process, string exe)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // Elevated or exiting process: the name has to do.
            return true;
        }
    }
}
