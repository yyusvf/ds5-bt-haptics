using System.ComponentModel;
using System.Diagnostics;

namespace DS5BtHaptics;

/// <summary>
/// Software DS5Dongle: exposes the Bluetooth DualSense to Windows as a wired USB DualSense
/// (via usbip-win2) and translates between both, including HD haptics from USB audio channels 3/4.
/// Report conversion follows DS5Dongle src/main.cpp (on_bt_data, forward_output_report, tud_hid_*_report_cb).
/// </summary>
internal sealed class Bridge(DualSenseDevice dev, Options opts)
{
    readonly Ds5BtProtocol _proto = new() { BufferLength = opts.BufferLength };
    readonly object _btLock = new();
    readonly Dictionary<byte, byte[]> _featureCache = new();
    PushSource? _haptics;
    long _inputReports, _outputReports, _outputSkipped, _audioPackets;
    byte[]? _lastOutput;
    long _lastOutputTicks;

    const string UsbIpExe = @"C:\Program Files\USBip\usbip.exe";

    void SendBt(Func<byte[]> build)
    {
        lock (_btLock) dev.Write(build());
    }

    public int Run(CancellationToken ct)
    {
        var edge = dev.Pid == 0x0DF2;
        var vds = new VirtualDualSense(edge) { Log = Console.WriteLine, Trace = opts.Verbose };
        if (opts.Verbose)
            vds.IsoTrace = TextWriter.Synchronized(new StreamWriter(Path.Combine(AppContext.BaseDirectory, "iso-trace.log"), false) { AutoFlush = true });
        _haptics = new PushSource(48000, opts.MaxLatencyMs);

        // Like DS5Dongle init_feature(): reading 0x05 switches to full reports; cache the static ones.
        foreach (byte id in new byte[] { 0x05, 0x09, 0x20, 0x22 })
            if (dev.GetFeature(id) is { } f) _featureCache[id] = f;

        // Unlike the Pico, this PC is also the controller's Bluetooth host. If the virtual USB pad reports
        // the real controller MAC (0x09 bytes 1..6, little endian), Steam/GameInput treat it as "the same
        // controller was plugged in" and drop the Bluetooth link. Report a locally administered variant instead.
        if (!opts.RealMac && _featureCache.TryGetValue(0x09, out var pairing) && pairing.Length >= 7)
        {
            pairing[1] ^= 0x01;
            pairing[6] |= 0x02;
            Console.WriteLine($"  Pairing-MAC für virtuelles Gerät: {string.Join(":", pairing[1..7].Reverse().Select(b => b.ToString("X2")))}");
        }
        SendBt(() => _proto.BuildAudioControl());

        vds.GetFeature = id =>
        {
            if (_featureCache.TryGetValue(id, out var c)) return c;
            byte[]? live;
            lock (_btLock) live = dev.GetFeature(id);
            if (opts.Verbose) Console.WriteLine($"    Feature 0x{id:X2} live vom Controller: {(live == null ? "fehlgeschlagen" : live.Length + " B")}");
            return live;
        };
        vds.SetFeature = SetFeature;
        vds.OutputReport = usb =>
        {
            Interlocked.Increment(ref _outputReports);
            // Steam repeats the same state ~100x/s; over BT that competes with the haptics stream.
            // Forward changes immediately, unchanged state at most every 500 ms.
            var now = Environment.TickCount64;
            lock (_btLock)
            {
                if (_lastOutput != null && usb.AsSpan().SequenceEqual(_lastOutput) && now - _lastOutputTicks < 500)
                {
                    _outputSkipped++;
                    return;
                }
                _lastOutput = usb;
                _lastOutputTicks = now;
            }
            if (opts.Verbose) Console.WriteLine($"    Ausgabe-Report vom Host: {Convert.ToHexString(usb)}");
            SendBt(() => _proto.BuildStateFromUsb(usb.AsSpan(1)));
        };
        vds.SpeakerAudio = OnSpeakerAudio;
        vds.InterfaceChanged = (itf, alt) =>
            Console.WriteLine($"  Audio-Interface {itf} -> {(alt == 0 ? "aus" : "an")}{(itf == 1 ? " (Lautsprecher/Haptik)" : itf == 2 ? " (Mikrofon)" : "")}");

        new Thread(() => InputLoop(vds, ct)) { IsBackground = true, Name = "bt-input", Priority = ThreadPriority.AboveNormal }.Start();
        var hapticThread = new Thread(() =>
        {
            try { Streamer.Run(pcm => SendBt(() => _proto.BuildHapticsOnly(pcm)), _haptics, opts, ct, idleSkip: true); }
            catch (Win32Exception e) { Console.Error.WriteLine($"Haptik-Sender beendet: {e.Message}"); }
        }) { IsBackground = true, Name = "haptics", Priority = ThreadPriority.Highest };
        hapticThread.Start();

        var server = new UsbIpServer(vds) { Log = m => Console.WriteLine("  " + m) };
        var serverTask = server.RunAsync(ct);

        if (File.Exists(UsbIpExe) && !opts.NoAttach) AttachLater(ct);
        else
        {
            Console.WriteLine("\n  Jetzt in einer Admin-Konsole einbinden:");
            Console.WriteLine($"    \"{UsbIpExe}\" attach -r 127.0.0.1 -b 1-1\n");
        }

        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && !serverTask.IsCompleted)
        {
            ct.WaitHandle.WaitOne(5000);
            if (opts.Verbose)
                Console.WriteLine($"  [{sw.Elapsed:mm\\:ss}] Eingaben {_inputReports}, Ausgaben {_outputReports} ({_outputSkipped} doppelt verworfen), Audio-Pakete {_audioPackets}, Haptik-Pakete {Streamer.Sent} (max. {Streamer.MaxWriteMs:F1} ms)");
        }
        serverTask.Wait(1000);
        hapticThread.Join(500);
        return 0;
    }

    void AttachLater(CancellationToken ct) => Task.Run(async () =>
    {
        await Task.Delay(500, ct);
        Console.WriteLine("  Binde virtuellen DualSense über usbip-win2 ein ...");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(UsbIpExe, "attach -r 127.0.0.1 -b 1-1")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            var output = (await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            Console.WriteLine($"  usbip: {(output.Length > 0 ? output : "ok")} (Exit {p.ExitCode})");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"  usbip konnte nicht gestartet werden: {e.Message}");
        }
    }, ct);

    void InputLoop(VirtualDualSense vds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var r = dev.ReadInput();
            if (r == null)
            {
                Console.Error.WriteLine($"  Bluetooth-Controller getrennt (ReadFile Win32-Fehler {dev.LastReadError}, nach {_inputReports} Eingaben).");
                return;
            }
            if (r.Length >= 65 && r[0] == 0x31)
            {
                if (((r[1] >> 1) & 1) != 0) continue; // controller microphone payload, not input state
                var usb = new byte[64];
                usb[0] = 0x01;
                Array.Copy(r, 2, usb, 1, 63);
                vds.PushInput(usb);
                Interlocked.Increment(ref _inputReports);
            }
            else if (r.Length > 0 && r[0] == 0x01)
            {
                dev.GetFeature(0x05); // still in simple BT mode: request the full report mode again
            }
        }
    }

    /// <summary>4-ch 16-bit 48 kHz packet; channels 3/4 are the voice coils (DS5Dongle audio_loop).</summary>
    void OnSpeakerAudio(byte[] buf, int offset, int length)
    {
        var frames = length / 8;
        if (frames <= 0) return;
        Span<float> stereo = stackalloc float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var o = offset + i * 8;
            stereo[2 * i] = BitConverter.ToInt16(buf, o + 4) / 32768f;
            stereo[2 * i + 1] = BitConverter.ToInt16(buf, o + 6) / 32768f;
        }
        _haptics!.Push(stereo);
        Interlocked.Increment(ref _audioPackets);
    }

    /// <summary>Feature writes DS5Dongle forwards (0x80 test command / Edge unlock, 0x60-0x62 profiles), with BT CRC.</summary>
    void SetFeature(byte id, byte[] data)
    {
        if (opts.Verbose) Console.WriteLine($"    SET Feature 0x{id:X2} vom Host: {Convert.ToHexString(data.AsSpan(0, Math.Min(24, data.Length)))}");
        if (id != 0x80 && id is < 0x60 or > 0x62) return;
        var report = data.Length > 0 && data[0] == id ? (byte[])data.Clone() : new[] { id }.Concat(data).ToArray();
        if (report.Length < 5) return;
        Ds5BtProtocol.FillFeatureCrc(report);
        lock (_btLock) dev.SetFeature(report);
    }
}
