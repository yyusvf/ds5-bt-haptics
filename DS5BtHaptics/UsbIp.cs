using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DS5BtHaptics;

/// <summary>One USB request block as received with USBIP_CMD_SUBMIT.</summary>
internal sealed class Urb
{
    public uint SeqNum;
    public uint DevId;
    public bool In;
    public int Endpoint;
    public uint TransferFlags;
    public int BufferLength;
    public int StartFrame;
    public int NumberOfPackets;   // -1 / 0 = not isochronous
    public int Interval;
    public byte[] Setup = new byte[8];
    public byte[] OutData = Array.Empty<byte>();
    public IsoPacket[] IsoPackets = Array.Empty<IsoPacket>();
    public bool IsIso => NumberOfPackets > 0;

    public struct IsoPacket
    {
        public int Offset, Length;
    }
}

/// <summary>What a USB device implementation must provide to the server.</summary>
internal interface IUsbIpDevice
{
    UsbIpDeviceInfo Info { get; }
    /// <summary>Handle a submitted URB; complete it (now or later) through <paramref name="conn"/>.</summary>
    void Submit(Urb urb, UsbIpConnection conn);
    /// <summary>Try to cancel a pending URB. True if it was still pending (and will never be completed).</summary>
    bool Unlink(uint seqNum);
    void Detached();
}

internal sealed record UsbIpDeviceInfo(
    string BusId, ushort Vid, ushort Pid, ushort BcdDevice, byte NumInterfaces, byte[] InterfaceClasses);

/// <summary>
/// Minimal USB/IP 1.1.1 server (the protocol usbip-win2 speaks), exporting one virtual device.
/// All fields are big endian. Spec: Linux Documentation/usb/usbip_protocol.rst
/// </summary>
internal sealed class UsbIpServer(IUsbIpDevice device, int port = 3240)
{
    const ushort Version = 0x0111;
    const ushort OpReqDevlist = 0x8005, OpRepDevlist = 0x0005, OpReqImport = 0x8003, OpRepImport = 0x0003;
    const int UsbSpeedFull = 2;

    public Action<string>? Log;

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Log?.Invoke($"USB/IP-Server lauscht auf 127.0.0.1:{port}, Bus-ID {device.Info.BusId}");
        using var reg = ct.Register(listener.Stop);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClient(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    void HandleClient(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            var hdr = ReadExact(stream, 8);
            var code = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(2));
            if (code == OpReqDevlist)
            {
                var reply = new MemoryStream();
                WriteOpHeader(reply, OpRepDevlist);
                WriteBe32(reply, 1);
                WriteDeviceInfo(reply, withInterfaces: true);
                stream.Write(reply.ToArray());
                Log?.Invoke("Geräteliste abgefragt");
                return;
            }
            if (code != OpReqImport) { Log?.Invoke($"Unbekannte Operation 0x{code:X4}"); return; }

            var busId = Encoding.ASCII.GetString(ReadExact(stream, 32)).TrimEnd('\0');
            var rep = new MemoryStream();
            if (busId != device.Info.BusId)
            {
                WriteOpHeader(rep, OpRepImport, status: 1);
                stream.Write(rep.ToArray());
                Log?.Invoke($"Import von unbekannter Bus-ID '{busId}' abgelehnt");
                return;
            }
            WriteOpHeader(rep, OpRepImport);
            WriteDeviceInfo(rep, withInterfaces: false);
            stream.Write(rep.ToArray());
            Log?.Invoke("Virtueller DualSense wurde von Windows eingebunden");

            var conn = new UsbIpConnection(stream);
            UrbLoop(stream, conn, ct);
        }
        catch (Exception e) when (e is IOException or SocketException or EndOfStreamException)
        {
            Log?.Invoke($"Verbindung beendet ({e.GetType().Name}: {e.Message})");
        }
        finally
        {
            device.Detached();
        }
    }

    void UrbLoop(NetworkStream stream, UsbIpConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var h = ReadExact(stream, 48);
            var cmd = Be32(h, 0);
            var seq = (uint)Be32(h, 4);
            switch (cmd)
            {
                case 1: // USBIP_CMD_SUBMIT
                {
                    var urb = new Urb
                    {
                        SeqNum = seq, DevId = (uint)Be32(h, 8), In = Be32(h, 12) == 1, Endpoint = Be32(h, 16),
                        TransferFlags = (uint)Be32(h, 20), BufferLength = Be32(h, 24), StartFrame = Be32(h, 28),
                        NumberOfPackets = Be32(h, 32), Interval = Be32(h, 36),
                    };
                    Array.Copy(h, 40, urb.Setup, 0, 8);
                    if (!urb.In && urb.BufferLength > 0) urb.OutData = ReadExact(stream, urb.BufferLength);
                    if (urb.IsIso)
                    {
                        var d = ReadExact(stream, urb.NumberOfPackets * 16);
                        urb.IsoPackets = new Urb.IsoPacket[urb.NumberOfPackets];
                        for (var i = 0; i < urb.NumberOfPackets; i++)
                            urb.IsoPackets[i] = new Urb.IsoPacket { Offset = Be32(d, i * 16), Length = Be32(d, i * 16 + 4) };
                    }
                    device.Submit(urb, conn);
                    break;
                }
                case 2: // USBIP_CMD_UNLINK
                {
                    var victim = (uint)Be32(h, 20);
                    var status = device.Unlink(victim) ? -104 /*-ECONNRESET*/ : 0;
                    conn.SendUnlinkReply(seq, status);
                    break;
                }
                default:
                    throw new IOException($"Unbekanntes USB/IP-Kommando {cmd}");
            }
        }
    }

    void WriteDeviceInfo(Stream s, bool withInterfaces)
    {
        var info = device.Info;
        var path = new byte[256];
        Encoding.ASCII.GetBytes("/sys/devices/virtual/ds5-bt-haptics/" + info.BusId).CopyTo(path, 0);
        s.Write(path);
        var bus = new byte[32];
        Encoding.ASCII.GetBytes(info.BusId).CopyTo(bus, 0);
        s.Write(bus);
        WriteBe32(s, 1);            // busnum
        WriteBe32(s, 1);            // devnum
        WriteBe32(s, UsbSpeedFull);
        WriteBe16(s, info.Vid);
        WriteBe16(s, info.Pid);
        WriteBe16(s, info.BcdDevice);
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); // device class/subclass/protocol (per interface)
        s.WriteByte(1);             // bConfigurationValue
        s.WriteByte(1);             // bNumConfigurations
        s.WriteByte(info.NumInterfaces);
        if (!withInterfaces) return;
        for (var i = 0; i < info.NumInterfaces; i++)
        {
            s.WriteByte(info.InterfaceClasses[i * 3]);
            s.WriteByte(info.InterfaceClasses[i * 3 + 1]);
            s.WriteByte(info.InterfaceClasses[i * 3 + 2]);
            s.WriteByte(0);
        }
    }

    static void WriteOpHeader(Stream s, ushort code, uint status = 0)
    {
        WriteBe16(s, Version);
        WriteBe16(s, code);
        WriteBe32(s, (int)status);
    }

    internal static byte[] ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        s.ReadExactly(buf);
        return buf;
    }

    static int Be32(byte[] b, int o) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o));
    static void WriteBe16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    static void WriteBe32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
}

/// <summary>Sends URB completions back to the host; thread safe.</summary>
internal sealed class UsbIpConnection(Stream stream)
{
    readonly object _lock = new();
    public const int StatusOk = 0, StatusStall = -32 /*-EPIPE*/;

    /// <summary>USBIP_RET_SUBMIT for a non-isochronous URB.</summary>
    public void Complete(Urb urb, int status, ReadOnlySpan<byte> inData = default, int outActual = 0)
    {
        var actual = urb.In ? inData.Length : (status == StatusOk ? outActual : 0);
        var buf = new byte[48 + (urb.In ? inData.Length : 0)];
        WriteRetHeader(buf, urb, status, actual, 0, 0);
        if (urb.In) inData.CopyTo(buf.AsSpan(48));
        Send(buf);
    }

    /// <summary>
    /// USBIP_RET_SUBMIT for an isochronous URB. For IN, packet payloads are sent back-to-back
    /// (only their actual length), followed by the descriptors with the original offsets.
    /// </summary>
    public void CompleteIso(Urb urb, int[] actualLengths, byte[]? inBuffer)
    {
        var dataLen = urb.In ? actualLengths.Sum() : 0;
        var buf = new byte[48 + dataLen + urb.NumberOfPackets * 16];
        WriteRetHeader(buf, urb, StatusOk, actualLengths.Sum(), urb.NumberOfPackets, 0);
        var o = 48;
        if (urb.In && inBuffer != null)
            for (var i = 0; i < urb.NumberOfPackets; i++)
            {
                inBuffer.AsSpan(urb.IsoPackets[i].Offset, actualLengths[i]).CopyTo(buf.AsSpan(o));
                o += actualLengths[i];
            }
        for (var i = 0; i < urb.NumberOfPackets; i++, o += 16)
        {
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(o), urb.IsoPackets[i].Offset);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(o + 4), urb.IsoPackets[i].Length);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(o + 8), actualLengths[i]);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(o + 12), 0);
        }
        Send(buf);
    }

    public void SendUnlinkReply(uint seq, int status)
    {
        var buf = new byte[48];
        BinaryPrimitives.WriteInt32BigEndian(buf, 4); // USBIP_RET_UNLINK
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), seq);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(20), status);
        Send(buf);
    }

    static void WriteRetHeader(byte[] b, Urb urb, int status, int actual, int packets, int errors)
    {
        BinaryPrimitives.WriteInt32BigEndian(b, 3); // USBIP_RET_SUBMIT
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), urb.SeqNum);
        // devid, direction, ep are zero in replies (like the Linux stub driver)
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), status);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(24), actual);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(28), urb.StartFrame);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(32), urb.IsIso ? packets : 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(36), errors);
    }

    void Send(byte[] buf)
    {
        lock (_lock)
        {
            try { stream.Write(buf); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { /* host detached */ }
        }
    }
}
