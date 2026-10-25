using System.ComponentModel;
using System.Diagnostics;
using DS5BtHaptics;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var opts = Options.Parse(args);
if (opts is null) { Options.PrintUsage(); return 1; }

if (opts.Command == "selftest") return SelfTest.Run();
if (opts.Command == "devtest") return DeviceTest.Run();

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

if (opts.Command == "bridge")
{
    using var bcts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; bcts.Cancel(); };
    var rc = new Bridge(dev, opts).Run(bcts.Token);
    dev.Dispose();
    return rc;
}

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

static class Streamer
{
    public static long Sent;
    public static double MaxWriteMs;

    /// <param name="idleSkip">Stop sending while the source is silent (bridge mode: no game audio).</param>
    public static int Run(Action<sbyte[]> send, IHapticSource source, Options opts, CancellationToken ct, bool idleSkip = false)
    {
        var framesPerPacket = opts.Single ? 1 : 2;
        var samples = new float[framesPerPacket * Ds5BtProtocol.FrameBytes]; // interleaved stereo
        var pcm = new sbyte[samples.Length];
        var period = TimeSpan.FromSeconds((double)framesPerPacket * Ds5BtProtocol.FrameSamples / Ds5BtProtocol.HapticSampleRate);
        Console.WriteLine($"  Haptik: Report 0x32, {framesPerPacket} Frame(s) alle {period.TotalMilliseconds:F2} ms, Gain {opts.Gain}");

        Timing.DisableThrottling();
        var sw = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        long sent = 0, late = 0, idle = 0;
        var maxWrite = TimeSpan.Zero;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wait = next - sw.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1)) Timing.Sleep(wait.TotalMilliseconds - 0.5);
                while (sw.Elapsed < next) Thread.SpinWait(50);
                next += period;
                if (sw.Elapsed - next > period * 4) { late++; next = sw.Elapsed; } // we fell behind; resync

                var real = source.Read(samples);
                if (real < 0) break;
                var audible = false;
                for (var i = 0; i < real * 2 && !audible; i++) audible = Math.Abs(samples[i]) > 1f / 256;
                idle = audible ? 0 : idle + 1; // Windows streams zeros all the time; only real signal counts
                if (idleSkip && idle > 25) continue; // ~0.5 s silence: stop streaming until audio comes back

                for (var i = 0; i < samples.Length; i++)
                    pcm[i] = (sbyte)Math.Clamp((int)(samples[i] * 127f * opts.Gain), -128, 127);

                var t0 = sw.Elapsed;
                send(pcm);
                var dt = sw.Elapsed - t0;
                if (dt > maxWrite) maxWrite = dt;
                sent++;
                Sent = sent;
                MaxWriteMs = maxWrite.TotalMilliseconds;
                if (opts.Verbose && sent % 500 == 0)
                    Console.WriteLine($"    {sent} Haptik-Pakete, max. WriteFile {maxWrite.TotalMilliseconds:F1} ms, Resyncs {late}");
            }
            if (!idleSkip || idle <= 25)
            {
                Array.Clear(pcm); // a little silence so the voice coils stop cleanly
                for (var i = 0; i < 4; i++) { send(pcm); Thread.Sleep(period); }
            }
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
        Console.WriteLine($"Fertig: {sent} Pakete in {sw.Elapsed.TotalSeconds:F1} s, max. WriteFile {maxWrite.TotalMilliseconds:F1} ms, Resyncs {late}");
        return 0;
    }
}

sealed class Options
{
    public string Command = "";
    public string? File;
    public float Gain = 1.0f;
    public bool Single;
    public byte BufferLength = 48;
    public int Index;
    public int MaxLatencyMs = 60;
    public bool Verbose;
    public bool NoAttach;
    public bool RealMac;

    public static Options? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        var o = new Options { Command = args[0].ToLowerInvariant() };
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} braucht einen Wert");
            switch (args[i])
            {
                case "--gain": o.Gain = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--single": o.Single = true; break;
                case "--buf": o.BufferLength = byte.Parse(Next()); break;
                case "--index": o.Index = int.Parse(Next()); break;
                case "--latency": o.MaxLatencyMs = int.Parse(Next()); break;
                case "-v": case "--verbose": o.Verbose = true; break;
                case "--no-attach": o.NoAttach = true; break;
                case "--real-mac": o.RealMac = true; break;
                default:
                    if (o.Command == "wav" && o.File is null) o.File = args[i];
                    else return null;
                    break;
            }
        }
        if (o.Command == "wav" && o.File is null) return null;
        return o;
    }

    public static void PrintUsage() => Console.WriteLine("""
        ds5-bt-haptics - DualSense HD-Haptik über Bluetooth, ohne Dongle (Machbarkeitstest)

          ds5-bt-haptics list                 erkannte DualSense-Controller + Report-IDs anzeigen
          ds5-bt-haptics test                 Testmuster abspielen (links, rechts, Sweep, Klicks)
          ds5-bt-haptics wav <datei>          Audiodatei als Haptik abspielen (4 Kanäle: Kanal 3/4)
          ds5-bt-haptics loopback             Windows-Sound live als Haptik (Strg+C beendet)
          ds5-bt-haptics bridge               Software-Dongle: BT-Controller erscheint als USB-DualSense
                                              (braucht usbip-win2), Spiele bekommen echte HD-Haptik
          ds5-bt-haptics devtest              Testmuster über das USB-Audiogerät (Kanal 3/4) abspielen, während bridge läuft
          ds5-bt-haptics selftest             interne Prüfungen ohne Controller

        Optionen:
          --gain <1.0..4.0>   Verstärkung (Standard 1.0)
          --single            1 Frame pro Paket (0x32 alle 10,7 ms) statt 2 (alle 21,3 ms)
          --buf <16..127>     AudioBufferLength-Byte an den Controller (Standard 48)
          --latency <ms>      max. Haptik-Puffer bei loopback/bridge (Standard 60)
          --no-attach         bridge: usbip attach nicht automatisch ausführen
          --real-mac          bridge: echte Controller-MAC melden (trennt BT, wenn Steam läuft)
          --index <n>         welcher BT-Controller, falls mehrere
          -v                  Timing-Statistik ausgeben
        """);
}
