using System.ComponentModel;
using System.Diagnostics;
using DS5BtHaptics;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var opts = Options.Parse(args);
if (opts is null) { Options.PrintUsage(); return 1; }

if (opts.Command == "selftest") return SelfTest.Run();
if (opts.Command == "devtest") return DeviceTest.Run();

if (opts.Command == "bridge")
{
    // Runs until Ctrl+C; reconnects whenever the controller comes back.
    using var service = new BridgeService(opts);
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    service.Start();
    stop.Wait();
    service.Stop();
    return 0;
}

var devices = DualSenseDevice.Enumerate();
if (opts.Command == "list")
{
    if (devices.Count == 0) Console.WriteLine("Kein DualSense gefunden (per Bluetooth koppeln oder USB anschließen).");
    for (var i = 0; i < devices.Count; i++)
    {
        var d = devices[i];
        Console.WriteLine($"[{i}] {d.Product} (PID 0x{d.Pid:X4}) - {(d.IsBluetooth ? "Bluetooth" : "USB")}");
        Console.WriteLine($"    Input {d.Caps.InputReportByteLength} B, Output {d.Caps.OutputReportByteLength} B, Feature {d.Caps.FeatureReportByteLength} B");
        Console.WriteLine($"    Output-Report-IDs: {string.Join(" ", d.OutputReportIds.Select(id => $"0x{id:X2}"))}");
        if (d.IsBluetooth)
            Console.WriteLine($"    Haptik-Report 0x32 im Deskriptor: {(d.OutputReportIds.Contains(0x32) ? "ja" : "NEIN")}");
        Console.WriteLine($"    {d.Path}");
    }
    devices.ForEach(d => d.Dispose());
    return 0;
}

var bt = devices.Where(d => d.IsBluetooth).ToList();
if (bt.Count == 0)
{
    Console.Error.WriteLine("Kein DualSense über Bluetooth gefunden. 'ds5-bt-haptics list' zeigt, was erkannt wurde.");
    return 2;
}
var dev = bt[Math.Min(opts.Index, bt.Count - 1)];
foreach (var d in devices.Where(d => d != dev)) d.Dispose();
Console.WriteLine($"Controller: {dev.Product} über Bluetooth (Output-Puffer {dev.Caps.OutputReportByteLength} B)");

var proto = new Ds5BtProtocol { BufferLength = opts.BufferLength };
try
{
    // Reading calibration feature 0x05 switches the controller from the simple 0x01
    // report into the full 0x31 mode (same trick as hid-playstation / DS4Windows).
    Console.WriteLine(dev.GetFeature(0x05) is null ? "  Feature 0x05 lesen fehlgeschlagen (weiter)" : "  Controller im Vollmodus");
    dev.Write(proto.BuildLightbar(0, 64, 255));
    Console.WriteLine("  Lightbar -> blau (0x31 funktioniert)");
    dev.Write(proto.BuildAudioControl());
}
catch (Win32Exception e)
{
    Console.Error.WriteLine($"Schreiben fehlgeschlagen: {e.Message} (Win32 {e.NativeErrorCode})");
    return 3;
}

if (opts.Command == "speaker")
{
    // 3 s of 440 Hz on the controller speaker (or headphone jack with --headphones), straight over Bluetooth.
    var headphones = args.Contains("--headphones");
    var frames = opts.Single ? 1 : 2;
    var spk = new SpeakerEncoder();
    var silence = new sbyte[frames * Ds5BtProtocol.FrameBytes];
    var period = TimeSpan.FromSeconds(frames * 512 / 48000.0);
    long phase = 0, ok = 0, failed = 0, skipped = 0;
    int? lastError = null;
    try { dev.Write(proto.BuildSpeakerInit()); dev.Write(proto.BuildVolume(80, false)); Console.WriteLine("  AudioControl + Lautstärke 80 gesendet"); }
    catch (Win32Exception e) { Console.WriteLine($"  AudioControl fehlgeschlagen: Win32 {e.NativeErrorCode}"); }
    Console.WriteLine($"  Ton 440 Hz, Report 0x{(frames == 1 ? 0x36 : 0x39):X2}, {(headphones ? "Kopfhörer" : "Lautsprecher")} ...");
    var sw = Stopwatch.StartNew();
    var next = TimeSpan.Zero;
    while (sw.Elapsed < TimeSpan.FromSeconds(3))
    {
        for (var i = 0; i < frames * 512; i++, phase++)
        {
            var v = 0.4f * MathF.Sin(2 * MathF.PI * 440 * phase / 48000f);
            spk.Push(v, v);
        }
        if (spk.Take(frames) is not { } opus) { skipped++; continue; }
        try { dev.Write(proto.BuildAudio(silence, opus, headphones)); ok++; }
        catch (Win32Exception e) { failed++; lastError = e.NativeErrorCode; }
        next += period;
        var wait = next - sw.Elapsed;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
    }
    Console.WriteLine($"  gesendet {ok}, fehlgeschlagen {failed}{(lastError is { } err ? $" (Win32 {err})" : "")}, Vorpuffer {skipped}");
    dev.Dispose();
    return failed > 0 ? 4 : 0;
}

IHapticSource source;
LoopbackSource? loopback = null;
switch (opts.Command)
{
    case "test":
        source = BufferSource.TestPattern();
        break;
    case "wav":
        var wav = BufferSource.FromWav(opts.File!);
        Console.WriteLine($"  Datei: {opts.File} ({wav.Seconds:F1} s)");
        source = wav;
        break;
    case "loopback":
        loopback = new LoopbackSource(opts.MaxLatencyMs);
        Console.WriteLine($"  System-Audio: {loopback.DeviceName}. Strg+C beendet.");
        loopback.Start();
        source = loopback;
        break;
    default:
        Options.PrintUsage();
        return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int result;
try { result = Streamer.Run(pcm => dev.Write(proto.BuildHapticsOnly(pcm)), source, opts, cts.Token); }
catch (Win32Exception e)
{
    Console.Error.WriteLine($"Senden abgebrochen: {e.Message} (Win32 {e.NativeErrorCode})");
    result = 4;
}
loopback?.Dispose();
try { dev.Write(proto.BuildLightbar(0, 0, 0)); } catch (Win32Exception) { }
dev.Dispose();
return result;
