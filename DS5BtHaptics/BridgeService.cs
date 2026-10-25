namespace DS5BtHaptics;

internal enum BridgeState { Stopped, WaitingForController, Running }

/// <summary>
/// Keeps the bridge up: waits for a Bluetooth DualSense, runs <see cref="Bridge"/> while it is
/// connected and starts over once it disconnects. Used by the tray app and by `bridge`.
/// </summary>
internal sealed class BridgeService(Options opts) : IDisposable
{
    readonly object _lock = new();
    CancellationTokenSource? _cts;
    Thread? _thread;

    public BridgeState State { get; private set; }
    /// <summary>The bridge for the connected controller, null while waiting or stopped.</summary>
    public Bridge? Current { get; private set; }
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
        Bridge? bridge = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (bridge?.ServerError is { } failed)
                {
                    // e.g. port 3240 taken by a second bridge
                    LastError = failed.Message;
                    AppLog.Write($"Bridge-Fehler: {failed}");
                    DropBridge(ref bridge);
                    ct.WaitHandle.WaitOne(5000);
                    continue;
                }

                var dev = FindController();
                if (dev == null)
                {
                    if (!announced) AppLog.Write("Warte auf DualSense über Bluetooth (PS-Taste drücken) ...");
                    announced = true;
                    SetState(BridgeState.WaitingForController);
                    ct.WaitHandle.WaitOne(2000);
                    continue;
                }

                announced = false;
                AppLog.Write($"Controller: {dev.Product} über Bluetooth");
                // A different model (DualSense vs. Edge) needs a different virtual pad.
                if (bridge != null && bridge.Pid != dev.Pid) DropBridge(ref bridge);
                if (bridge == null)
                {
                    bridge = new Bridge(opts);
                    bridge.Changed += () => Changed?.Invoke();
                    Current = bridge;
                }
                LastError = null;
                SetState(BridgeState.Running);
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
                    dev.Dispose();
                }
                if (ct.IsCancellationRequested) break;
                SetState(BridgeState.WaitingForController);
                ct.WaitHandle.WaitOne(LastError == null ? 1000 : 5000);
            }
        }
        finally
        {
            DropBridge(ref bridge);
        }
    }

    void DropBridge(ref Bridge? bridge)
    {
        if (bridge == null) return;
        Current = null;
        bridge.Dispose();
        bridge = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// A paired controller that is switched off can leave its HID node behind for a moment,
    /// so only count it once it answers a feature request.
    /// </summary>
    DualSenseDevice? FindController()
    {
        var devices = DualSenseDevice.Enumerate();
        var bt = devices.Where(d => d.IsBluetooth).ToList();
        var dev = bt.Count > 0 ? bt[Math.Min(opts.Index, bt.Count - 1)] : null;
        foreach (var d in devices.Where(d => d != dev)) d.Dispose();
        if (dev != null && dev.GetFeature(0x05) == null)
        {
            dev.Dispose();
            return null;
        }
        return dev;
    }

    void SetState(BridgeState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
