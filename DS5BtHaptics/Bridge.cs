using System.ComponentModel;
using System.Diagnostics;

namespace DS5BtHaptics;

/// <summary>
/// Software DS5Dongle: exposes the Bluetooth DualSense to Windows as a wired USB DualSense
/// (via usbip-win2) and translates between both, including HD haptics from USB audio channels 3/4.
/// Report conversion follows DS5Dongle src/main.cpp (on_bt_data, forward_output_report, tud_hid_*_report_cb).
///
/// Like the Pico, the virtual pad stays plugged in while the Bluetooth controller is away: games
/// such as 007 First Light (libScePad) don't pick up a pad that is unplugged and plugged in again,
/// so a dropped Bluetooth link must not unplug it. <see cref="Connect"/> runs once per Bluetooth
/// connection; the virtual pad lives from the first one until <see cref="Dispose"/>.
/// </summary>
internal sealed class Bridge(Options opts, int slot = 0) : IDisposable
{
    readonly Ds5BtProtocol _proto = new() { BufferLength = opts.BufferLength };
    readonly object _btLock = new();
    readonly Dictionary<byte, byte[]> _featureCache = new();
    readonly CancellationTokenSource _life = new();
    PushSource? _haptics;
    readonly SpeakerEncoder? _speaker = TryCreateSpeaker();
    VirtualDualSense? _vds;
    Task? _serverTask;
    Thread? _hapticThread;
    volatile DualSenseDevice? _dev;
    long _inputReports, _outputReports, _outputSkipped, _audioPackets;
    byte[]? _lastOutput;
    long _lastOutputTicks;
    byte _lastStatus = 0xFF;
    long _lastHapticTicks = -10_000;
    volatile object? _sentMute; // bool? boxed: last mute state sent to the controller
    byte[]? _lastControls;

    public const string UsbIpExe = @"C:\Program Files\USBip\usbip.exe";

    /// <summary>Product ID of the controller the virtual pad imitates (DualSense or Edge).</summary>
    public ushort Pid { get; private set; }
    /// <summary>The Bluetooth controller is connected right now.</summary>
    public bool Connected => _dev != null;
    /// <summary>Windows has imported the virtual pad via usbip-win2.</summary>
    public bool HostAttached { get; private set; }
    /// <summary>A game or Windows has the speaker/haptics audio interface open.</summary>
    public bool AudioStreaming { get; private set; }
    public int? Battery { get; private set; }
    public bool Charging { get; private set; }
    /// <summary>Last usbip attach failure, null while fine.</summary>
    public string? AttachError { get; private set; }
    public bool HapticsActive => Environment.TickCount64 - Volatile.Read(ref _lastHapticTicks) < 1500;
    /// <summary>Sound is playing on the controller speaker or headphone jack.</summary>
    public bool SpeakerActive => _speaker?.Active == true && opts.Speaker;
    /// <summary>Headphones are plugged into the controller.</summary>
    public bool Headphones { get; private set; }
    /// <summary>Real Bluetooth address of the controller (0 if unknown); identifies it across reconnects.</summary>
    public ulong Address { get; private set; }
    /// <summary>Position among the bridges: picks the USB/IP port (3240 + slot) and the player number.</summary>
    public int Slot => slot;
    /// <summary>When sticks, triggers or buttons last moved (Environment.TickCount64).</summary>
    public long LastInputTicks { get; private set; } = Environment.TickCount64;
    public string ControllerName { get; private set; } = "DualSense";

    /// <summary>The USB/IP server could not run (port taken), null while fine.</summary>
    public Exception? ServerError => _serverTask is { IsFaulted: true } t ? t.Exception!.InnerException : null;

    /// <summary>Any status above changed. Raised on worker threads.</summary>
    public event Action? Changed;

    /// <summary>Drops the report while the controller is away; a write racing a disconnect fails harmlessly.</summary>
    void SendBt(Func<byte[]> build)
    {
        lock (_btLock)
        {
            if (_dev is not { } dev) return;
            try { dev.Write(build()); }
            catch (Win32Exception) { }
        }
    }

    /// <summary>
    /// Serves one Bluetooth connection: blocks until the controller disconnects or
    /// <paramref name="ct"/> is cancelled. The first call also plugs in the virtual pad.
    /// </summary>
    public void Connect(DualSenseDevice dev, CancellationToken ct)
    {
        // Like DS5Dongle init_feature(): reading 0x05 switches to full reports. The static ones are
        // cached from the first connection only, so the virtual pad never changes its identity.
        var calibration = dev.GetFeature(0x05);
        if (Bluetooth.AddressFromPairing(dev.GetFeature(0x09)) is var address and not 0) Address = address;
        if (_vds == null)
        {
            if (calibration != null) _featureCache[0x05] = calibration;
            foreach (byte id in new byte[] { 0x09, 0x20, 0x22 })
                if (dev.GetFeature(id) is { } f) _featureCache[id] = f;

            // Unlike the Pico, this PC is also the controller's Bluetooth host. If the virtual USB pad reports
            // the real controller MAC (0x09 bytes 1..6, little endian), Steam/GameInput treat it as "the same
            // controller was plugged in" and drop the Bluetooth link. Report a locally administered variant instead.
            if (!opts.RealMac && _featureCache.TryGetValue(0x09, out var pairing) && pairing.Length >= 7)
            {
                pairing[1] ^= 0x01;
                pairing[6] |= 0x02;
                AppLog.Write($"  Pairing-MAC für virtuelles Gerät: {string.Join(":", Enumerable.Reverse(pairing[1..7]).Select(b => b.ToString("X2")))}");
            }
            Pid = dev.Pid;
            ControllerName = dev.Product;
            PlugIn(dev.Pid == 0x0DF2);
        }

        lock (_btLock) _dev = dev;
        _lastOutput = null; // the controller lost its state: resend the next output report
        SendBt(() => _proto.BuildAudioControl());
        if (_speaker != null && opts.Speaker) SendBt(() => _proto.BuildSpeakerInit());
        SendVolume();
        LastInputTicks = Environment.TickCount64;
        _lastControls = null;
        Changed?.Invoke();

        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        InputLoop(dev, both.Token);

        lock (_btLock) _dev = null;
        // Release whatever was held when the link dropped; the virtual pad stays plugged in.
        _vds!.PushInput(NeutralInput());
        Battery = null;
        Headphones = false;
        _lastStatus = 0xFF;
        Changed?.Invoke();
    }

    /// <summary>Creates the virtual pad, its USB/IP server and the haptics sender, then attaches it.</summary>
    void PlugIn(bool edge)
    {
        var ct = _life.Token;
        var vds = new VirtualDualSense(edge) { Log = AppLog.Write, Trace = opts.Verbose };
        if (opts.Verbose)
            vds.IsoTrace = TextWriter.Synchronized(new StreamWriter(Path.Combine(AppContext.BaseDirectory, "iso-trace.log"), false) { AutoFlush = true });
        _haptics = new PushSource(48000, opts.MaxLatencyMs);

        vds.GetFeature = id =>
        {
            if (_featureCache.TryGetValue(id, out var c)) return c;
            byte[]? live = null;
            lock (_btLock) live = _dev?.GetFeature(id);
            if (opts.Verbose) AppLog.Write($"    Feature 0x{id:X2} live vom Controller: {(live == null ? "fehlgeschlagen" : live.Length + " B")}");
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
            if (opts.Verbose) AppLog.Write($"    Ausgabe-Report vom Host: {Convert.ToHexString(usb)}");
            SendBt(() => _proto.BuildStateFromUsb(usb.AsSpan(1)));
        };
        vds.SpeakerAudio = OnSpeakerAudio;
        vds.SpeakerVolumeChanged = SendVolume;
        vds.InterfaceChanged = (itf, alt) =>
        {
            AppLog.Write($"  Audio-Interface {itf} -> {(alt == 0 ? "aus" : "an")}{(itf == 1 ? " (Lautsprecher/Haptik)" : itf == 2 ? " (Mikrofon)" : "")}");
            if (itf == 1) { AudioStreaming = alt != 0; Changed?.Invoke(); }
        };
        _vds = vds;

        _hapticThread = new Thread(() =>
        {
            try
            {
                Streamer.Run(pcm => SendBt(() => BuildAudioReport(pcm)), _haptics, opts, ct, idleSkip: true,
                    busy: () => SpeakerActive,
                    onAudible: () => Volatile.Write(ref _lastHapticTicks, Environment.TickCount64));
            }
            catch (Win32Exception e) { AppLog.Write($"Haptik-Sender beendet: {e.Message}"); }
        }) { IsBackground = true, Name = "haptics", Priority = ThreadPriority.Highest };
        _hapticThread.Start();

        var server = new UsbIpServer(vds, Port)
        {
            Log = m => AppLog.Write("  " + m),
            AttachedChanged = on => { HostAttached = on; if (!on) AudioStreaming = false; Changed?.Invoke(); },
        };
        _serverTask = server.RunAsync(ct);

        if (File.Exists(UsbIpExe) && !opts.NoAttach) AttachLater(ct);
        else
        {
            AppLog.Write("\n  Jetzt in einer Admin-Konsole einbinden:");
            AppLog.Write($"    \"{UsbIpExe}\" {AttachArguments}\n");
        }

        if (opts.Verbose)
        {
            var sw = Stopwatch.StartNew();
            new Thread(() =>
            {
                while (!ct.WaitHandle.WaitOne(5000))
                    AppLog.Write($"  [{sw.Elapsed:mm\\:ss}] Eingaben {_inputReports}, Ausgaben {_outputReports} ({_outputSkipped} doppelt verworfen), Audio-Pakete {_audioPackets}, Haptik-Pakete {Streamer.Sent} (max. {Streamer.MaxWriteMs:F1} ms)");
            }) { IsBackground = true, Name = "stats" }.Start();
        }
    }

    /// <summary>Unplugs the virtual pad (closing the USB/IP connection detaches it) and stops the sender.</summary>
    public void Dispose()
    {
        _life.Cancel();
        try { _serverTask?.Wait(1000); } catch (AggregateException) { }
        _hapticThread?.Join(500);
        _life.Dispose();
    }

    int Port => 3240 + slot;
    string AttachArguments => slot == 0 ? "attach -r 127.0.0.1 -b 1-1" : $"--tcp-port {Port} attach -r 127.0.0.1 -b 1-1";

    /// <summary>Switches the controller off by dropping its Bluetooth link; the virtual pad stays.</summary>
    public bool PowerOff()
    {
        if (Address == 0 || !Connected) return false;
        AppLog.Write($"Controller {slot + 1} wird ausgeschaltet (Bluetooth getrennt)");
        return Bluetooth.Disconnect(Address);
    }

    /// <summary>The Windows volume slider of the virtual speaker, applied by the controller itself.</summary>
    void SendVolume()
    {
        if (_vds is { } vds) AppLog.Write($"  Lautstärke Controller: {vds.SpeakerVolume}{(vds.SpeakerMuted ? " (stumm)" : "")}");
        _sentMute = null;
        UpdateOutputMute();
    }

    /// <summary>
    /// Mutes speaker and headphone jack while nothing plays: with the amplifier open at the Windows
    /// volume, headphones on the controller whine quietly even in silence. Unmutes before the first sound.
    /// </summary>
    void UpdateOutputMute()
    {
        if (_vds is not { } vds || _speaker == null || _dev == null) return;
        var mute = vds.SpeakerMuted || !opts.Speaker || opts.MuteWhenSilent && !_speaker.Active;
        if (_sentMute is bool sent && sent == mute) return;
        _sentMute = mute;
        if (opts.Verbose) AppLog.Write($"    Controller-Audio {(mute ? "stumm" : "an")}");
        SendBt(() => _proto.BuildVolume(vds.SpeakerVolume, mute));
    }

    static SpeakerEncoder? TryCreateSpeaker()
    {
        try { return new SpeakerEncoder(); }
        catch (Exception e) { AppLog.Write($"Lautsprecher-Encoder nicht verfügbar: {e.Message}"); return null; }
    }

    /// <summary>Haptics alone (0x32), or with the speaker frames when there is sound to play (0x36/0x39).</summary>
    byte[] BuildAudioReport(sbyte[] pcm)
    {
        if (_speaker != null && opts.Speaker)
        {
            if (_speaker.Take(pcm.Length / Ds5BtProtocol.FrameBytes) is { } opus)
                return _proto.BuildAudio(pcm, opus, Headphones);
        }
        return _proto.BuildHapticsOnly(pcm);
    }

    void AttachLater(CancellationToken ct) => Task.Run(async () =>
    {
        await Task.Delay(500, ct);
        // Port taken (another bridge?): attaching would import whatever listens there instead.
        if (_serverTask is { IsFaulted: true }) return;
        AppLog.Write("  Binde virtuellen DualSense über usbip-win2 ein ...");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(UsbIpExe, AttachArguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            var output = (await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            AppLog.Write($"  usbip: {(output.Length > 0 ? output : "ok")} (Exit {p.ExitCode})");
            AttachError = p.ExitCode == 0 ? null : (output.Length > 0 ? output : $"usbip Exit {p.ExitCode}");
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"  usbip konnte nicht gestartet werden: {e.Message}");
            AttachError = e.Message;
            Changed?.Invoke();
        }
    }, ct);

    void InputLoop(DualSenseDevice dev, CancellationToken ct)
    {
        var vds = _vds!;
        long reports = 0;
        while (!ct.IsCancellationRequested)
        {
            var r = dev.ReadInput();
            if (r == null)
            {
                if (ct.IsCancellationRequested) return;
                AppLog.Write($"  Bluetooth-Controller getrennt (ReadFile Win32-Fehler {dev.LastReadError}, nach {reports} Eingaben). Virtueller Controller bleibt eingesteckt.");
                return;
            }
            if (r.Length >= 65 && r[0] == 0x31)
            {
                if (((r[1] >> 1) & 1) != 0) continue; // controller microphone payload, not input state
                var usb = new byte[64];
                usb[0] = 0x01;
                Array.Copy(r, 2, usb, 1, 63);
                vds.PushInput(usb);
                reports++;
                Interlocked.Increment(ref _inputReports);
                if (usb[53] != _lastStatus) UpdateBattery(usb[53]);
                if (((usb[54] & 1) != 0) != Headphones) { Headphones = (usb[54] & 1) != 0; Changed?.Invoke(); }
                TrackActivity(usb);
                UpdateOutputMute();
            }
            else if (r.Length > 0 && r[0] == 0x01)
            {
                dev.GetFeature(0x05); // still in simple BT mode: request the full report mode again
            }
        }
    }

    /// <summary>Sticks beyond a small dead zone, triggers or any button count as use; gyro and touchpad noise don't.</summary>
    void TrackActivity(byte[] usb)
    {
        var prev = _lastControls;
        var moved = prev == null;
        if (prev != null)
        {
            for (var i = 1; i <= 6 && !moved; i++) moved = Math.Abs(usb[i] - prev[i]) > 12; // sticks, triggers
            for (var i = 8; i <= 10 && !moved; i++) moved = usb[i] != prev[i];               // buttons, D-pad
        }
        if (!moved) return;
        _lastControls = usb[..11];
        LastInputTicks = Environment.TickCount64;
    }

    /// <summary>USB input report 0x01 with sticks centred, D-pad released and no buttons.</summary>
    static byte[] NeutralInput()
    {
        var usb = new byte[64];
        usb[0] = 0x01;
        usb[1] = usb[2] = usb[3] = usb[4] = 0x80;
        usb[8] = 0x08;
        return usb;
    }

    /// <summary>Status byte of the input report: low nibble level 0..10, high nibble 0 discharging, 1 charging, 2 full (hid-playstation).</summary>
    void UpdateBattery(byte status)
    {
        _lastStatus = status;
        var level = status & 0x0F;
        var state = status >> 4;
        Charging = state == 1;
        Battery = state == 2 ? 100 : Math.Min(level * 10 + 5, 100);
        Changed?.Invoke();
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
        if (_speaker != null && opts.Speaker)
            for (var i = 0; i < frames; i++)
            {
                var o = offset + i * 8;
                _speaker.Push(BitConverter.ToInt16(buf, o) / 32768f, BitConverter.ToInt16(buf, o + 2) / 32768f);
            }
        Interlocked.Increment(ref _audioPackets);
    }

    /// <summary>Feature writes DS5Dongle forwards (0x80 test command / Edge unlock, 0x60-0x62 profiles), with BT CRC.</summary>
    void SetFeature(byte id, byte[] data)
    {
        if (opts.Verbose) AppLog.Write($"    SET Feature 0x{id:X2} vom Host: {Convert.ToHexString(data.AsSpan(0, Math.Min(24, data.Length)))}");
        if (id != 0x80 && id is < 0x60 or > 0x62) return;
        var report = data.Length > 0 && data[0] == id ? (byte[])data.Clone() : new[] { id }.Concat(data).ToArray();
        if (report.Length < 5) return;
        Ds5BtProtocol.FillFeatureCrc(report);
        lock (_btLock) _dev?.SetFeature(report);
    }
}
