using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using DS5BtHaptics;

namespace DS5BtHaptics.Plugin;

/// <summary>What the bridge process reports, a few times a second.</summary>
internal sealed record BridgeStatus(
    BridgeState State,
    bool HasBridge,
    bool Connected,
    bool HostAttached,
    int? Battery,
    bool Charging,
    bool HapticsActive,
    string? AttachError,
    string? LastError,
    string ControllerName);

/// <summary>A live change sent to the bridge process; null fields stay as they are.</summary>
internal sealed record BridgeCommand(float? Gain, bool? LowLatency);

/// <summary>
/// Runs the DS5 bridge in a child process of ysfUtil (ysfUtil.exe running this plugin's
/// <see cref="Ds5PluginProcess"/> with <see cref="Argument"/>). The bridge drives HID, USB/IP and audio on real-time threads; on its
/// own it can neither take the tray down with it nor keep ysfUtil alive with a stuck thread.
/// It talks to ysfUtil over a named pipe: status lines out, commands in. When the pipe closes -
/// ysfUtil stopped it, quit or crashed - the bridge stops too.
/// </summary>
internal sealed class BridgeProcess : IDisposable
{
    public const string Argument = "--ds5-bridge";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object gate = new();
    private Process? process;
    private NamedPipeServerStream? pipe;
    private StreamWriter? writer;
    private bool stopping;

    public BridgeStatus? Status { get; private set; }

    public bool IsRunning => process != null;

    /// <summary>A new status arrived. Raised on a worker thread.</summary>
    public event Action? Changed;

    /// <summary>The bridge process ended without being asked to. Raised on a worker thread.</summary>
    public event Action<string>? Crashed;

    /// <param name="createProcess">ysfUtil.exe running this plugin with the given arguments.</param>
    public void Start(Ds5Settings settings, Func<IEnumerable<string>, ProcessStartInfo> createProcess)
    {
        lock (gate)
        {
            if (process != null) return;
            stopping = false;
            string name = $"ysfUtil-ds5-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var arguments = new List<string> { Argument, name, "--gain", settings.Gain.ToString(CultureInfo.InvariantCulture) };
            if (settings.LowLatency) arguments.Add("--single");
            if (settings.RealMac) arguments.Add("--real-mac");
            ProcessStartInfo info = createProcess(arguments);
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("The bridge process did not start.");
            }
            catch
            {
                server.Dispose();
                throw;
            }
            pipe = server;
            Status = null;
            Process started = process;
            _ = Task.Run(() => PumpAsync(server, started));
        }
    }

    public void Send(BridgeCommand command)
    {
        lock (gate)
        {
            try { writer?.WriteLine(JsonSerializer.Serialize(command, Json)); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Closes the pipe so the bridge shuts down, and waits a few seconds for it.</summary>
    public void Stop()
    {
        Process? running;
        lock (gate)
        {
            running = process;
            if (running == null) return;
            stopping = true;
            process = null;
            writer = null;
            pipe?.Dispose();
            pipe = null;
            Status = null;
        }
        // Stopping waits for the bridge threads; past that it is ended the hard way.
        if (!running.WaitForExit(5000))
        {
            try { running.Kill(); } catch (InvalidOperationException) { }
        }
        running.Dispose();
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    private async Task PumpAsync(NamedPipeServerStream server, Process child)
    {
        string? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await server.WaitForConnectionAsync(timeout.Token);
            lock (gate)
            {
                if (pipe == server) writer = new StreamWriter(server) { AutoFlush = true };
            }
            using var reader = new StreamReader(server);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (JsonSerializer.Deserialize<BridgeStatus>(line, Json) is { } status)
                {
                    Status = status;
                    Changed?.Invoke();
                }
            }
            failure = "The bridge process ended unexpectedly.";
        }
        catch (OperationCanceledException)
        {
            failure = "The bridge process did not respond.";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or JsonException)
        {
            failure = "The bridge process ended unexpectedly.";
        }

        bool crashed;
        lock (gate)
        {
            crashed = !stopping && process == child;
            if (crashed)
            {
                process = null;
                writer = null;
                pipe?.Dispose();
                pipe = null;
                Status = null;
            }
        }
        if (crashed)
        {
            if (!child.WaitForExit(2000)) try { child.Kill(); } catch (InvalidOperationException) { }
            string code = child.HasExited ? $" (exit code {child.ExitCode})" : "";
            child.Dispose();
            Crashed?.Invoke(failure + code);
        }
    }

    // ══ The bridge process itself ═══════════════════════════════════════════

    /// <summary>Entry point of the child process: ysfUtil.exe --ds5-bridge &lt;pipe&gt; [options].</summary>
    public static int RunChild(string[] args)
    {
        int at = Array.IndexOf(args, Argument);
        if (at < 0 || at + 1 >= args.Length) return 2;
        Ds5Log.Attach("ds5-bridge");

        var options = new Options { Command = "bridge" };
        for (int i = at + 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--gain" when i + 1 < args.Length:
                    options.Gain = float.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--single": options.Single = true; break;
                case "--real-mac": options.RealMac = true; break;
            }
        }

        // Asynchronous: on a synchronous pipe a pending read would block the status writes.
        using var client = new NamedPipeClientStream(".", args[at + 1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            client.Connect(10000);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            AppLog.Write($"No connection to ysfUtil: {e.Message}");
            return 3;
        }

        AppLog.Write($"DS5 bridge started (ysfUtil {typeof(BridgeProcess).Assembly.GetName().Version?.ToString(3)})");
        var output = new StreamWriter(client) { AutoFlush = true };
        var outputGate = new object();
        var service = new BridgeService(options);
        int dirty = 1;
        service.Changed += () => Interlocked.Exchange(ref dirty, 1);
        service.Start();

        // Changes are folded into at most four lines a second; once a second regardless, since
        // "haptics active" is time based and raises no event.
        var status = new Thread(() =>
        {
            long last = 0;
            while (client.IsConnected)
            {
                Thread.Sleep(250);
                if (Interlocked.Exchange(ref dirty, 0) == 0 && Environment.TickCount64 - last < 1000) continue;
                last = Environment.TickCount64;
                Bridge? bridge = service.Current;
                var snapshot = new BridgeStatus(service.State, bridge != null, bridge?.Connected == true,
                    bridge?.HostAttached == true, bridge?.Battery, bridge?.Charging == true, bridge?.HapticsActive == true,
                    bridge?.AttachError, service.LastError, bridge?.ControllerName ?? "DualSense");
                lock (outputGate)
                {
                    try { output.WriteLine(JsonSerializer.Serialize(snapshot, Json)); }
                    catch (IOException) { break; }
                }
            }
        }) { IsBackground = true, Name = "bridge-status" };
        status.Start();

        using (var input = new StreamReader(client))
        {
            try
            {
                while (input.ReadLine() is { } line)
                {
                    if (JsonSerializer.Deserialize<BridgeCommand>(line, Json) is not { } command) continue;
                    // Both are read for every packet, so they apply immediately.
                    if (command.Gain is float gain) service.Options.Gain = gain;
                    if (command.LowLatency is bool single) service.Options.Single = single;
                }
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                // ysfUtil is gone: stop as if asked to.
            }
        }

        AppLog.Write("DS5 bridge stopping");
        service.Stop();
        AppLog.Write("DS5 bridge stopped");
        // Don't let a stuck worker thread keep the process alive.
        Environment.Exit(0);
        return 0;
    }
}
