namespace DS5BtHaptics;

internal enum BridgeState { Stopped, WaitingForController, Running }

/// <summary>
/// Keeps the bridges up: one <see cref="Bridge"/> (virtual pad) per Bluetooth DualSense, recognised
/// by its Bluetooth address, so a controller that reconnects gets its own pad back. Each bridge
/// serves its controller on a thread of its own. Used by the tray app, the plugin and `bridge`.
/// </summary>
internal sealed class BridgeService(Options opts) : IDisposable
{
    /// <summary>usbip-win2 offers 8 USB ports; leave room for real devices.</summary>
    public const int MaxControllers = 4;

    readonly object _lock = new();
    readonly List<Bridge> _bridges = new();
    readonly HashSet<string> _connectedPaths = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _cts;
    Thread? _thread;

    public BridgeState State { get; private set; }

    /// <summary>The first controller's bridge, null while none has connected yet.</summary>
    public Bridge? Current
    {
        get { lock (_lock) return _bridges.Count > 0 ? _bridges[0] : null; }
    }

    /// <summary>All bridges by slot; a bridge stays while its controller is away.</summary>
    public IReadOnlyList<Bridge> Bridges
    {
        get { lock (_lock) return _bridges.ToArray(); }
    }

    public string? LastError { get; private set; }
    public Options Options => opts;

    /// <summary>State or bridge status changed. Raised on worker threads.</summary>
    public event Action? Changed;

    public void Start()
    {
        lock (_lock)
        {
            if (_thread != null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _thread = new Thread(() => Loop(ct)) { IsBackground = true, Name = "bridge-service" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            thread = _thread;
            cts = _cts;
            _thread = null;
            _cts = null;
        }
        if (thread == null) return;
        cts!.Cancel();
        thread.Join(4000);
        cts.Dispose();
        SetState(BridgeState.Stopped);
    }

    public void Dispose() => Stop();

    void Loop(CancellationToken ct)
    {
        var announced = false;
        var sessions = new List<Thread>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var failed in Bridges.Where(b => b.ServerError != null))
                {
                    // e.g. the port taken by a second bridge
                    LastError = failed.ServerError!.Message;
                    AppLog.Write($"Bridge-Fehler: {failed.ServerError}");
                    Drop(failed);
                }

                foreach (var dev in FindNewControllers())
                {
                    var address = Bluetooth.AddressFromPairing(dev.GetFeature(0x09));
                    var bridge = BridgeFor(dev, address);
                    if (bridge == null)
                    {
                        AppLog.Write($"Controller ignoriert: schon {MaxControllers} verbunden");
                        dev.Dispose();
                        continue;
                    }
                    AppLog.Write($"Controller {bridge.Slot + 1}: {dev.Product} über Bluetooth");
                    LastError = null;
                    lock (_lock) _connectedPaths.Add(dev.Path);
                    var t = new Thread(() => Serve(bridge, dev, ct)) { IsBackground = true, Name = $"bridge-{bridge.Slot + 1}" };
                    sessions.Add(t);
                    t.Start();
                    announced = false;
                }
                sessions.RemoveAll(t => !t.IsAlive);

                var running = sessions.Count > 0;
                if (!running && !announced)
                {
                    AppLog.Write("Warte auf DualSense über Bluetooth (PS-Taste drücken) ...");
                    announced = true;
                }
                SetState(running ? BridgeState.Running : BridgeState.WaitingForController);
                ct.WaitHandle.WaitOne(2000);
            }
        }
        finally
        {
            foreach (var t in sessions) t.Join(2000);
            foreach (var b in Bridges) Drop(b);
        }
    }

    /// <summary>One Bluetooth connection of one controller, until it drops.</summary>
    void Serve(Bridge bridge, DualSenseDevice dev, CancellationToken ct)
    {
        try
        {
            bridge.Connect(dev, ct);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            AppLog.Write($"Bridge-Fehler: {e}");
        }
        finally
        {
            lock (_lock) _connectedPaths.Remove(dev.Path);
            dev.Dispose();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The bridge this controller had before; otherwise a pad whose controller is away (one player
    /// switching controllers keeps the pad the game already uses); otherwise a new one in the first free slot.
    /// </summary>
    Bridge? BridgeFor(DualSenseDevice dev, ulong address)
    {
        Bridge? bridge;
        lock (_lock)
        {
            bridge = address != 0 ? _bridges.FirstOrDefault(b => b.Address == address && !b.Connected) : null;
            bridge ??= _bridges.FirstOrDefault(b => !b.Connected && b.Pid == dev.Pid);
            bridge ??= _bridges.Count >= MaxControllers ? _bridges.FirstOrDefault(b => !b.Connected) : null;
        }
        // A different model (DualSense vs. Edge) needs a different virtual pad.
        if (bridge != null && bridge.Pid != dev.Pid)
        {
            Drop(bridge);
            bridge = null;
        }
        if (bridge != null) return bridge;

        lock (_lock)
        {
            var slot = Enumerable.Range(0, MaxControllers).FirstOrDefault(i => _bridges.All(b => b.Slot != i), -1);
            if (slot < 0) return null;
            bridge = new Bridge(opts, slot);
            bridge.Changed += () => Changed?.Invoke();
            _bridges.Add(bridge);
            _bridges.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        }
        Changed?.Invoke();
        return bridge;
    }

    void Drop(Bridge bridge)
    {
        lock (_lock) _bridges.Remove(bridge);
        bridge.Dispose();
        Changed?.Invoke();
    }

    /// <summary>
    /// Bluetooth controllers not served yet. A paired controller that is switched off can leave its
    /// HID node behind for a moment, so only count it once it answers a feature request.
    /// </summary>
    List<DualSenseDevice> FindNewControllers()
    {
        var result = new List<DualSenseDevice>();
        foreach (var d in DualSenseDevice.Enumerate())
        {
            bool known;
            lock (_lock) known = _connectedPaths.Contains(d.Path);
            if (!known && d.IsBluetooth && d.GetFeature(0x05) != null) result.Add(d);
            else d.Dispose();
        }
        return result;
    }

    void SetState(BridgeState state)
    {
        if (State == state) return;
        State = state;
        Changed?.Invoke();
    }
}
