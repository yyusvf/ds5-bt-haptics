using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DS5BtHaptics.App.Setup;

/// <summary>What HidHide currently does for us.</summary>
internal sealed record HidHideStatus(bool AppRegistered, bool CloakOn, bool ControllerHidden, IReadOnlyList<string> Apps)
{
    public bool Allows(string exe) => Apps.Contains(exe, StringComparer.OrdinalIgnoreCase);

    public bool Ready => AppRegistered && CloakOn && ControllerHidden;
}

/// <summary>
/// The two drivers the bridge needs, neither redistributed here: usbip-win2 (virtual USB device)
/// and HidHide (hides the Bluetooth pad from games, so they only see the virtual wired one).
/// Missing ones are downloaded from their official GitHub releases, checked and installed on request.
/// </summary>
internal static class Dependencies
{
    public const string HidHideCli = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";

    /// <summary>The elevated helper run of this exe that configures HidHide.</summary>
    public const string HidHideSetupArgument = "--setup-hidhide";

    /// <summary>Elevated helper: "--hidhide-apps reg|unreg &lt;exe&gt;..." lets games see the Bluetooth pad, or not.</summary>
    public const string HidHideAppsArgument = "--hidhide-apps";

    /// <summary>Run by the uninstaller (elevated): makes the Bluetooth controller visible to games again.</summary>
    public const string HidHideUndoArgument = "--undo-hidhide";

    public static readonly Installer UsbIp = new(
        "usbip-win2",
        "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe",
        "38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a");

    // HidHide's release publishes no checksum; Windows' signature check still applies.
    public static readonly Installer HidHide = new(
        "HidHide",
        "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe",
        null);

    public static bool UsbIpInstalled => File.Exists(Bridge.UsbIpExe);

    public static bool HidHideInstalled => File.Exists(HidHideCli);

    private static string ExePath => Environment.ProcessPath!;

    /// <summary>Reads HidHide's configuration; listing works without admin rights.</summary>
    public static HidHideStatus? QueryHidHide()
    {
        if (!HidHideInstalled || RunCli("--app-list --dev-list --cloak-state") is not { } output)
        {
            return null;
        }

        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var apps = lines.Where(l => l.StartsWith("--app-reg", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["--app-reg".Length..].Trim().Trim('"'))
            .ToList();
        bool registered = apps.Contains(ExePath, StringComparer.OrdinalIgnoreCase);
        bool cloak = lines.Any(l => l.Equals("--cloak-on", StringComparison.OrdinalIgnoreCase));
        bool hidden = lines.Any(IsBluetoothDualSense);
        return new HidHideStatus(registered, cloak, hidden, apps);
    }

    /// <summary>
    /// Starts this exe elevated with <see cref="HidHideSetupArgument"/> and waits for it.
    /// False if the user declined the Windows prompt or the setup failed.
    /// </summary>
    public static Task<bool> ConfigureHidHideAsync() => RunElevatedAsync(HidHideSetupArgument);

    /// <summary>Registers (or unregisters) games with HidHide, elevated. False if declined or failed.</summary>
    public static Task<bool> SetGamesAllowedAsync(IEnumerable<string> exes, bool allowed) =>
        RunElevatedAsync($"{HidHideAppsArgument} {(allowed ? "reg" : "unreg")} {string.Join(" ", exes.Select(e => $"\"{e}\""))}");

    /// <summary>The elevated half of <see cref="SetGamesAllowedAsync"/>.</summary>
    public static int RunElevatedHidHideApps(string[] args)
    {
        int at = Array.IndexOf(args, HidHideAppsArgument);
        if (at < 0 || at + 2 >= args.Length || args[at + 1] is not ("reg" or "unreg"))
        {
            return 2;
        }
        string verb = args[at + 1] == "reg" ? "--app-reg" : "--app-unreg";
        string commands = string.Join(" ", args[(at + 2)..].Select(exe => $"{verb} \"{exe}\""));
        return RunCli(commands) == null ? 1 : 0;
    }

    private static Task<bool> RunElevatedAsync(string arguments) => Task.Run(() =>
    {
        try
        {
            using Process? helper = Process.Start(new ProcessStartInfo(ExePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            helper?.WaitForExit();
            return helper?.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    });

    /// <summary>
    /// The elevated half: lets this exe see hidden devices, then hides every Bluetooth DualSense
    /// and turns hiding on. Two CLI runs, since HidHide applies a registration only when its CLI
    /// exits - only then can this process see a controller that is already hidden.
    /// </summary>
    public static int RunElevatedHidHideSetup()
    {
        if (RunCli($"--app-reg \"{ExePath}\"") == null)
        {
            return 1;
        }

        var commands = new List<string>();
        foreach (DualSenseDevice device in DualSenseDevice.Enumerate())
        {
            if (device.IsBluetooth && InstanceId(device.Path) is { } id)
            {
                commands.Add($"--dev-hide \"{id}\"");
            }
            device.Dispose();
        }
        commands.Add("--cloak-on");
        return RunCli(string.Join(" ", commands)) == null ? 1 : 0;
    }

    /// <summary>Unhides the Bluetooth DualSense entries and removes this exe from HidHide's list.</summary>
    public static int RunElevatedHidHideUndo()
    {
        if (RunCli("--dev-list") is not { } output)
        {
            return 1;
        }
        IEnumerable<string> commands = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(IsBluetoothDualSense)
            .Select(l => "--dev-unhide" + l["--dev-hide".Length..])
            .Append($"--app-unreg \"{ExePath}\"");
        return RunCli(string.Join(" ", commands)) == null ? 1 : 0;
    }

    /// <summary>
    /// A "--dev-hide" line for a Bluetooth DualSense. Bluetooth HID instances start with the HID
    /// service GUID: HID\{00001124-...}_VID&amp;0002054C_PID&amp;0CE6\...
    /// </summary>
    private static bool IsBluetoothDualSense(string line) =>
        line.StartsWith("--dev-hide", StringComparison.OrdinalIgnoreCase)
        && line.Contains("{00001124-", StringComparison.OrdinalIgnoreCase)
        && line.Contains("_VID&0002054C", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// \\?\hid#{00001124-...}_vid&amp;0002054c_pid&amp;0ce6#7&amp;2a3b4c5d&amp;0&amp;0000#{4d1e55b2-...}
    /// becomes HID\{00001124-...}_VID&amp;0002054C_PID&amp;0CE6\7&amp;2A3B4C5D&amp;0&amp;0000.
    /// </summary>
    public static string? InstanceId(string devicePath)
    {
        string path = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        int iface = path.LastIndexOf("#{", StringComparison.Ordinal);
        return iface <= 0 ? null : path[..iface].Replace('#', '\\').ToUpperInvariant();
    }

    private static string? RunCli(string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(HidHideCli, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd() + error.Result;
            process.WaitForExit();
            AppLog.Write($"HidHideCLI {arguments.Split(' ')[0]} -> Exit {process.ExitCode}");
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception e)
        {
            AppLog.Write($"HidHideCLI nicht startbar: {e.Message}");
            return null;
        }
    }
}

/// <summary>An official installer: downloaded to %TEMP%, checked, then run (it asks for admin rights itself).</summary>
internal sealed class Installer(string name, string url, string? sha256)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public string Name => name;

    /// <summary>Downloads, verifies and runs the installer, waiting until it exits.</summary>
    public async Task InstallAsync(IProgress<string> progress)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DS5BtHaptics-setup");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Path.GetFileName(new Uri(url).LocalPath));

        progress.Report($"{name} wird heruntergeladen …");
        using (HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using FileStream target = File.Create(file);
            await response.Content.CopyToAsync(target);
        }

        progress.Report($"{name} wird geprüft …");
        if (sha256 != null)
        {
            string actual;
            await using (FileStream stream = File.OpenRead(file))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            }
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidOperationException($"{name}: Prüfsumme stimmt nicht, Installation abgebrochen.");
            }
        }
        if (!Authenticode.IsTrusted(file))
        {
            File.Delete(file);
            throw new InvalidOperationException($"{name}: Signatur ungültig, Installation abgebrochen.");
        }

        progress.Report($"{name}-Setup läuft – bitte dort bestätigen …");
        await Task.Run(() =>
        {
            try
            {
                using Process? setup = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                setup?.WaitForExit();
            }
            catch (Win32Exception)
            {
                // Windows prompt declined.
            }
        });
        try { File.Delete(file); } catch (IOException) { }
    }
}

/// <summary>Windows' own signature check (WinVerifyTrust): signed, untampered, trusted publisher chain.</summary>
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsTrusted(string file)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = file,
        };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,       // WTD_UI_NONE
                dwUnionChoice = 1,    // WTD_CHOICE_FILE
                pFile = filePtr,
            };
            Guid action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}
