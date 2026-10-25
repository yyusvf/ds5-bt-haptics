using System.Diagnostics;
using System.Text;

namespace DS5BtHaptics;

/// <summary>
/// A wired (USB) DualSense as seen by Windows: audio control, 4-ch speaker/haptics OUT (ep 0x01),
/// 2-ch mic IN (ep 0x82), HID (ep 0x84 IN, ep 0x03 OUT). Behaviour mirrors DS5Dongle's TinyUSB callbacks.
/// </summary>
internal sealed class VirtualDualSense : IUsbIpDevice
{
    const int EpAudioOut = 0x01, EpMicIn = 0x02, EpHidIn = 0x04, EpHidOut = 0x03;
    const int MicBytesPerMs = 48 * 2 * 2; // 48 kHz, 2 ch, 16 bit

    public UsbIpDeviceInfo Info { get; }
    readonly bool _edge;
    readonly byte[] _config;
    readonly byte[] _hidReport;

    // Hooks into the Bluetooth side
    public Func<byte, byte[]?>? GetFeature;
    public Action<byte, byte[]>? SetFeature;
    public Action<byte[]>? OutputReport;          // USB report 0x02 incl. id
    public Action<byte[], int, int>? SpeakerAudio;  // buffer, offset, length of one 1-ms iso packet (4 ch int16 @ 48 kHz)
    public Action<int, int>? InterfaceChanged;
    public Action<string>? Log;
    public bool Trace;
    /// <summary>Per-URB trace of isochronous traffic (bridge -v writes it to iso-trace.log).</summary>
    public TextWriter? IsoTrace;

    // HID IN: pending host reads and the latest unsent input report
    readonly object _hidLock = new();
    readonly Queue<(Urb urb, UsbIpConnection conn)> _pendingIn = new();
    byte[]? _latestInput;
    bool _latestSent = true;

    // Isochronous completions are paced in real time (1 packet = 1 ms) like a real bus
    readonly IsoScheduler _iso = new();
    readonly Dictionary<int, double> _isoNextDue = new();

    readonly int[] _alt = new int[4];
    short _spkVolume = 0, _micVolume = 48 * 256;
    byte _spkMute, _micMute;

    public VirtualDualSense(bool edge)
    {
        _edge = edge;
        _hidReport = edge ? Descriptors.HidReportDualSenseEdge : Descriptors.HidReportDualSense;
        _config = (byte[])Descriptors.Configuration.Clone();
        _config[Descriptors.Configuration.Length - 16] = (byte)_hidReport.Length; // wDescriptorLength of the HID class descriptor
        _config[Descriptors.Configuration.Length - 15] = (byte)(_hidReport.Length >> 8);
        Info = new UsbIpDeviceInfo("1-1", 0x054C, edge ? (ushort)0x0DF2 : (ushort)0x0CE6, 0x0100, 4,
            new byte[] { 1, 1, 0, 1, 2, 0, 1, 2, 0, 3, 0, 0 });
    }

    byte[] DeviceDescriptor => new byte[]
    {
        18, 1, 0x00, 0x02, 0, 0, 0, 64, 0x4C, 0x05, (byte)Info.Pid, (byte)(Info.Pid >> 8), 0x00, 0x01, 1, 2, 0, 1,
    };

    public void PushInput(byte[] usbReport)
    {
        lock (_hidLock)
        {
            if (_pendingIn.TryDequeue(out var p)) p.conn.Complete(p.urb, UsbIpConnection.StatusOk, usbReport);
            else { _latestInput = usbReport; _latestSent = false; }
        }
    }

    public void Submit(Urb urb, UsbIpConnection conn)
    {
        var ep = urb.Endpoint & 0x0F;
        if (ep == 0) { Control(urb, conn); return; }

        if (urb.IsIso)
        {
            if (!urb.In && ep == EpAudioOut)
                foreach (var p in urb.IsoPackets)
                    SpeakerAudio?.Invoke(urb.OutData, p.Offset, Math.Min(p.Length, urb.OutData.Length - p.Offset));
            ScheduleIso(urb, conn, ep);
            return;
        }

        if (urb.In && ep == EpHidIn)
        {
            lock (_hidLock)
            {
                if (!_latestSent && _latestInput != null)
                {
                    _latestSent = true;
                    conn.Complete(urb, UsbIpConnection.StatusOk, _latestInput);
                }
                else _pendingIn.Enqueue((urb, conn));
            }
            return;
        }

        if (!urb.In && ep == EpHidOut)
        {
            if (urb.OutData.Length > 0 && urb.OutData[0] == 0x02) OutputReport?.Invoke(urb.OutData);
            conn.Complete(urb, UsbIpConnection.StatusOk, outActual: urb.OutData.Length);
            return;
        }

        conn.Complete(urb, UsbIpConnection.StatusStall);
    }

    void ScheduleIso(Urb urb, UsbIpConnection conn, int ep)
    {
        var now = _iso.NowMs;
        lock (_isoNextDue)
        {
            var start = _isoNextDue.TryGetValue(ep | (urb.In ? 0x80 : 0), out var d) && d > now ? d : now;
            var due = start + urb.NumberOfPackets;
            _isoNextDue[ep | (urb.In ? 0x80 : 0)] = due;
            IsoTrace?.WriteLine($"{now,10:F2} SUB ep{(urb.In ? "IN" : "OUT")}{ep} seq={urb.SeqNum} n={urb.NumberOfPackets} len={urb.BufferLength} " +
                                $"flags=0x{urb.TransferFlags:X} sf={urb.StartFrame} int={urb.Interval} pk0={urb.IsoPackets[0].Length} due=+{due - now:F1}");
            _iso.Add(urb.SeqNum, due, () =>
            {
                IsoTrace?.WriteLine($"{_iso.NowMs,10:F2} RET seq={urb.SeqNum} late={_iso.NowMs - due:F2}");
                var actual = new int[urb.NumberOfPackets];
                byte[]? inBuf = null;
                if (urb.In)
                {
                    inBuf = new byte[Math.Max(urb.BufferLength, 1)]; // microphone: silence for now
                    for (var i = 0; i < actual.Length; i++) actual[i] = Math.Min(MicBytesPerMs, urb.IsoPackets[i].Length);
                }
                else
                    for (var i = 0; i < actual.Length; i++) actual[i] = urb.IsoPackets[i].Length;
                conn.CompleteIso(urb, actual, inBuf);
            });
        }
    }

    public bool Unlink(uint seqNum)
    {
        lock (_hidLock)
        {
            var before = _pendingIn.Count;
            var keep = _pendingIn.Where(p => p.urb.SeqNum != seqNum).ToList();
            if (keep.Count != before)
            {
                _pendingIn.Clear();
                keep.ForEach(_pendingIn.Enqueue);
                return true;
            }
        }
        var cancelled = _iso.Cancel(seqNum);
        IsoTrace?.WriteLine($"{_iso.NowMs,10:F2} UNLINK seq={seqNum} pending={cancelled}");
        return cancelled;
    }

    public void Detached()
    {
        lock (_hidLock) _pendingIn.Clear();
        _iso.Clear();
        lock (_isoNextDue) _isoNextDue.Clear();
        Array.Clear(_alt);
    }

    // ---------------------------------------------------------------- control endpoint

    void Control(Urb urb, UsbIpConnection conn)
    {
        var s = urb.Setup;
        int bmRequestType = s[0], bRequest = s[1];
        int wValue = s[2] | s[3] << 8, wIndex = s[4] | s[5] << 8, wLength = s[6] | s[7] << 8;
        var type = (bmRequestType >> 5) & 3;     // 0 standard, 1 class
        var recipient = bmRequestType & 0x1F;    // 0 device, 1 interface, 2 endpoint

        byte[]? reply = null;
        var ok = true;

        if (type == 0)
        {
            switch (bRequest)
            {
                case 0x06: // GET_DESCRIPTOR
                    reply = GetDescriptor(wValue >> 8, wValue & 0xFF, recipient);
                    ok = reply != null;
                    break;
                case 0x00: reply = new byte[2]; break;              // GET_STATUS
                case 0x08: reply = new byte[] { 1 }; break;         // GET_CONFIGURATION
                case 0x0A: reply = new byte[] { (byte)_alt[Math.Min(wIndex & 0xFF, 3)] }; break; // GET_INTERFACE
                case 0x09: break;                                   // SET_CONFIGURATION
                case 0x0B:                                          // SET_INTERFACE
                    var itf = wIndex & 0xFF;
                    IsoTrace?.WriteLine($"{_iso.NowMs,10:F2} SET_INTERFACE itf={wIndex & 0xFF} alt={wValue}");
                    if (itf < 4)
                    {
                        _alt[itf] = wValue;
                        InterfaceChanged?.Invoke(itf, wValue);
                    }
                    break;
                case 0x01: case 0x03:                               // CLEAR/SET_FEATURE
                    IsoTrace?.WriteLine($"{_iso.NowMs,10:F2} {(bRequest == 1 ? "CLEAR" : "SET")}_FEATURE {Convert.ToHexString(s)}");
                    break;
                default: ok = false; break;
            }
        }
        else if (type == 1 && recipient == 1 && (wIndex & 0xFF) == 3)
            (ok, reply) = HidClass(bRequest, wValue, urb.OutData);
        else if (type == 1)
            (ok, reply) = AudioClass(bmRequestType, bRequest, wValue, wIndex, urb.OutData);
        else ok = false;

        if (Trace)
            Log?.Invoke($"    EP0 {Convert.ToHexString(s)}{(urb.In ? "" : " data " + Convert.ToHexString(urb.OutData.AsSpan(0, Math.Min(16, urb.OutData.Length))))} -> {(ok ? $"OK {reply?.Length ?? 0} B" : "STALL")}");
        if (!ok)
        {
            conn.Complete(urb, UsbIpConnection.StatusStall);
            return;
        }
        if (urb.In) conn.Complete(urb, UsbIpConnection.StatusOk, (reply ?? Array.Empty<byte>()).AsSpan(0, Math.Min(wLength, reply?.Length ?? 0)));
        else conn.Complete(urb, UsbIpConnection.StatusOk, outActual: urb.OutData.Length);
    }

    byte[]? GetDescriptor(int type, int index, int recipient)
    {
        switch (type)
        {
            case 1: return DeviceDescriptor;
            case 2: return _config;
            case 3: return StringDescriptor(index);
            case 0x22 when recipient == 1: return _hidReport;
            case 0x21 when recipient == 1: return _config.AsSpan(_config.Length - 23, 9).ToArray(); // HID class descriptor
            default: return null; // device qualifier, BOS, ... -> STALL (full-speed USB 2.0 device)
        }
    }

    byte[]? StringDescriptor(int index)
    {
        string? s = index switch
        {
            1 => "Sony Interactive Entertainment",
            2 => _edge ? "DualSense Edge Wireless Controller" : "DualSense Wireless Controller",
            _ => null,
        };
        if (index == 0) return new byte[] { 4, 3, 0x09, 0x04 };
        if (s == null) return null;
        var u = Encoding.Unicode.GetBytes(s);
        var d = new byte[2 + u.Length];
        d[0] = (byte)d.Length; d[1] = 3;
        u.CopyTo(d, 2);
        return d;
    }

    (bool, byte[]?) HidClass(int bRequest, int wValue, byte[] outData)
    {
        int reportType = wValue >> 8, reportId = wValue & 0xFF;
        switch (bRequest)
        {
            case 0x01: // GET_REPORT
                if (reportType == 3 && GetFeature?.Invoke((byte)reportId) is { } f) return (true, f);
                if (reportType == 1)
                    lock (_hidLock) return (_latestInput != null, _latestInput);
                return (false, null);
            case 0x09: // SET_REPORT
                if (reportType == 2 && outData.Length > 0)
                {
                    OutputReport?.Invoke(outData[0] == 0x02 ? outData : new byte[] { 0x02 }.Concat(outData).ToArray());
                    return (true, null);
                }
                if (reportType == 3) { SetFeature?.Invoke((byte)reportId, outData); return (true, null); }
                return (false, null);
            case 0x0A: return (true, null); // SET_IDLE
            case 0x0B: return (true, null); // SET_PROTOCOL
            default: return (false, null);
        }
    }

    /// <summary>UAC1 feature unit (2 = speaker, 5 = mic) and endpoint sampling-rate requests; values as in DS5Dongle usb.cpp.</summary>
    (bool, byte[]?) AudioClass(int bmRequestType, int bRequest, int wValue, int wIndex, byte[] outData)
    {
        var get = (bmRequestType & 0x80) != 0;
        var toEndpoint = (bmRequestType & 0x1F) == 2;
        if (toEndpoint)
        {
            // sampling frequency control: always 48000 Hz
            return get ? (true, new byte[] { 0x80, 0xBB, 0x00 }) : (true, null);
        }

        int entity = wIndex >> 8, control = wValue >> 8;
        var spk = entity == 2;
        if (entity != 2 && entity != 5) return (false, null);

        if (!get)
        {
            if (bRequest != 0x01) return (false, null); // SET_CUR only
            if (control == 1 && outData.Length >= 1) { if (spk) _spkMute = outData[0]; else _micMute = outData[0]; }
            if (control == 2 && outData.Length >= 2) { var v = BitConverter.ToInt16(outData); if (spk) _spkVolume = v; else _micVolume = v; }
            return (true, null);
        }

        if (control == 1) return (true, new[] { spk ? _spkMute : _micMute });
        if (control != 2) return (false, null);
        return bRequest switch
        {
            0x81 => (true, BitConverter.GetBytes(spk ? _spkVolume : _micVolume)),   // GET_CUR
            0x82 => (true, spk ? new byte[] { 0x00, 0x9C } : new byte[] { 0x00, 0x00 }), // GET_MIN (-100 dB / 0 dB)
            0x83 => (true, spk ? new byte[] { 0x00, 0x00 } : new byte[] { 0x00, 0x30 }), // GET_MAX (0 dB / 48 dB)
            0x84 => (true, spk ? new byte[] { 0x00, 0x01 } : new byte[] { 0x7A, 0x00 }), // GET_RES
            _ => (false, null),
        };
    }
}

/// <summary>Completes isochronous URBs at their real-time due date.</summary>
internal sealed class IsoScheduler
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly PriorityQueue<(uint seq, Action done), double> _queue = new();
    readonly HashSet<uint> _pending = new();
    readonly object _lock = new();
    readonly AutoResetEvent _wake = new(false);

    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public IsoScheduler()
    {
        Timing.DisableThrottling(); // 15.6 ms timer granularity would bunch up the 1-ms packets
        new Thread(Run) { IsBackground = true, Name = "iso", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void Add(uint seq, double dueMs, Action done)
    {
        lock (_lock)
        {
            _queue.Enqueue((seq, done), dueMs);
            _pending.Add(seq);
        }
        _wake.Set();
    }

    public bool Cancel(uint seq) { lock (_lock) return _pending.Remove(seq); }
    public void Clear() { lock (_lock) { _queue.Clear(); _pending.Clear(); } }

    void Run()
    {
        while (true)
        {
            double due;
            bool any;
            lock (_lock) any = _queue.TryPeek(out _, out due);
            if (!any) { _wake.WaitOne(100); continue; }
            var wait = due - NowMs;
            // short high-resolution naps: new URBs never become due earlier than ~1 ms from now
            if (wait > 0.6) { Timing.Sleep(Math.Min(wait - 0.5, 1.0)); continue; }
            if (wait > 0) { Thread.SpinWait(20); continue; }

            (uint seq, Action done) item;
            bool run;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out item, out _)) continue;
                run = _pending.Remove(item.seq);
            }
            if (run) item.done();
        }
    }
}
