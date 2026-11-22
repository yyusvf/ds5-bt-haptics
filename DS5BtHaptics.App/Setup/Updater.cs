using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DS5BtHaptics.App.Ui;

namespace DS5BtHaptics.App.Setup;

/// <summary>A newer release on GitHub.</summary>
internal sealed record UpdateInfo(Version Version, string SetupUrl, string Sha256, string PageUrl);

/// <summary>
/// Looks for a newer release on GitHub and installs it: the setup is downloaded, checked against
/// the SHA-256 GitHub publishes for the release asset and run silently once the app has exited.
/// </summary>
internal static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/yyusvf/ds5-bt-haptics/releases/latest";
    private const string SetupAsset = "DS5BtHaptics-Setup.exe";

    private static readonly HttpClient Http = CreateClient();

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
        : new Version(0, 0, 0);

    /// <summary>Setup downloaded and verified, run by <see cref="Program"/> after shutdown.</summary>
    public static string? PendingSetup { get; private set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DS5BtHaptics-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The latest release if it is newer than this version; null otherwise or offline.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(LatestRelease));
            JsonElement root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= Current) return null;

            foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != SetupAsset) continue;
                string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
                string? url = asset.GetProperty("browser_download_url").GetString();
                // Without GitHub's checksum the download can't be checked: then only point to the page.
                string sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "";
                return new UpdateInfo(latest, url ?? "", sha, root.GetProperty("html_url").GetString() ?? "");
            }
            AppLog.Write($"Update {latest} gefunden, aber ohne {SetupAsset}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Write($"Update-Prüfung fehlgeschlagen: {e.Message}");
        }
        return null;
    }

    /// <summary>Downloads and verifies the setup; it runs once the app has shut down.</summary>
    public static async Task DownloadAsync(UpdateInfo update, IProgress<string> progress)
    {
        if (update.Sha256.Length == 0 || !update.SetupUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "DS5BtHaptics-update");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"DS5BtHaptics-Setup-{update.Version}.exe");

        progress.Report(Loc.T($"Version {update.Version} wird heruntergeladen …", $"Downloading version {update.Version} …"));
        using (HttpResponseMessage response = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using FileStream target = File.Create(file);
            await response.Content.CopyToAsync(target);
        }

        string actual;
        await using (FileStream stream = File.OpenRead(file))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidOperationException(Loc.T("Prüfsumme des Updates stimmt nicht, abgebrochen.", "Update checksum mismatch, cancelled."));
        }
        AppLog.Write($"Update {update.Version} heruntergeladen und geprüft, wird nach dem Beenden installiert");
        PendingSetup = file;
    }

    /// <summary>Called after the app has released its mutex and stopped the bridge.</summary>
    public static void RunPendingSetup()
    {
        if (PendingSetup is not { } file) return;
        try
        {
            // /UPDATE=1 makes the setup start the app again afterwards (see DS5BtHaptics.iss).
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Write($"Update-Setup konnte nicht gestartet werden: {e.Message}");
        }
    }
}
