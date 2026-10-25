using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace DS5BtHaptics;

/// <summary>Plays the usbip-win2 client side against the server + virtual DualSense, without any driver.</summary>
internal static class UsbIpSelfTest
{
    const int Port = 32400;

    public static int Run(Action<string, bool> check)
    {
        var vds = new VirtualDualSense(edge: false);
        var audio = new List<short>();
        var outputs = new List<byte[]>();
        var alts = new List<(int, int)>();
        vds.SpeakerAudio = (b, o, l) => { lock (audio) for (var i = 0; i < l / 2; i++) audio.Add(BitConverter.ToInt16(b, o + i * 2)); };
        vds.OutputReport = r => { lock (outputs) outputs.Add(r); };
        vds.InterfaceChanged = (i, a) => alts.Add((i, a));
        vds.GetFeature = id => id == 0x05 ? Enumerable.Range(0, 64).Select(i => (byte)(i == 0 ? 5 : i)).ToArray() : null;

        using var cts = new CancellationTokenSource();
        var server = new UsbIpServer(vds, Port);
        _ = server.RunAsync(cts.Token);
        Thread.Sleep(200);

        // --- OP_REQ_DEVLIST
        using (var c = new TcpClient("127.0.0.1", Port))
        {
            var s = c.GetStream();
            s.Write(new byte[] { 0x01, 0x11, 0x80, 0x05, 0, 0, 0, 0 });
            var hdr = UsbIpServer.ReadExact(s, 12);
            var dev = UsbIpServer.ReadExact(s, 312 + 4 * 4);
            check("DEVLIST: Version 0x0111, 1 Gerät", hdr[0] == 1 && hdr[1] == 0x11 && hdr[3] == 0x05 && hdr[11] == 1);
            check("DEVLIST: Bus-ID 1-1, VID 054C PID 0CE6, 4 Interfaces, HID an Interface 3",
                Encoding.ASCII.GetString(dev, 256, 3) == "1-1" && Be16(dev, 300) == 0x054C && Be16(dev, 302) == 0x0CE6 &&
                dev[311] == 4 && dev[312 + 12] == 3);
        }

        // --- OP_REQ_IMPORT and URBs
        using var client = new TcpClient("127.0.0.1", Port) { NoDelay = true };
        var st = client.GetStream();
        var imp = new byte[40];
        imp[0] = 1; imp[1] = 0x11; imp[2] = 0x80; imp[3] = 0x03;
        Encoding.ASCII.GetBytes("1-1").CopyTo(imp, 8);
        st.Write(imp);
        var rep = UsbIpServer.ReadExact(st, 8 + 312);
        check("IMPORT: Status 0, Full-Speed", rep[3] == 0x03 && Be32(rep, 4) == 0 && Be32(rep, 8 + 288 + 8) == 2);

        uint seq = 1;
        (int status, byte[] data, byte[] raw) Control(byte[] setup, byte[]? outData = null)
        {
            var dirIn = (setup[0] & 0x80) != 0;
            var len = dirIn ? setup[6] | setup[7] << 8 : outData?.Length ?? 0;
            SendSubmit(st, seq++, dirIn, 0, len, 0, setup, outData, null);
            var r = UsbIpServer.ReadExact(st, 48);
            var actual = Be32(r, 24);
            var d = dirIn ? UsbIpServer.ReadExact(st, actual) : Array.Empty<byte>();
            return (Be32(r, 20), d, r);
        }

        var devDesc = Control(new byte[] { 0x80, 6, 0, 1, 0, 0, 18, 0 });
        check("GET_DESCRIPTOR Gerät: 18 Bytes, 054C:0CE6", devDesc.status == 0 && devDesc.data.Length == 18 && devDesc.data[8] == 0x4C && devDesc.data[10] == 0xE6);
        var cfg9 = Control(new byte[] { 0x80, 6, 0, 2, 0, 0, 9, 0 });
        check("GET_DESCRIPTOR Konfiguration (9 B): wTotalLength 227", cfg9.data.Length == 9 && (cfg9.data[2] | cfg9.data[3] << 8) == 227);
        var cfg = Control(new byte[] { 0x80, 6, 0, 2, 0, 0, 227, 0 });
        check("GET_DESCRIPTOR Konfiguration komplett", cfg.data.Length == 227 && cfg.data.SequenceEqual(Descriptors.Configuration));
        var str2 = Control(new byte[] { 0x80, 6, 2, 3, 9, 4, 255, 0 });
        check("String 2 = \"DualSense Wireless Controller\"", Encoding.Unicode.GetString(str2.data, 2, str2.data.Length - 2) == "DualSense Wireless Controller");
        var qual = Control(new byte[] { 0x80, 6, 0, 6, 0, 0, 10, 0 });
        check("Device-Qualifier -> STALL (Full-Speed-Gerät)", qual.status == -32);
        check("SET_CONFIGURATION 1", Control(new byte[] { 0x00, 9, 1, 0, 0, 0, 0, 0 }).status == 0);
        var hidRep = Control(new byte[] { 0x81, 6, 0, 0x22, 3, 0, 0x51, 1 });
        check("HID-Report-Deskriptor 337 Bytes", hidRep.data.Length == 337);
        check("SET_IDLE", Control(new byte[] { 0x21, 0x0A, 0, 0, 3, 0, 0, 0 }).status == 0);
        var feat = Control(new byte[] { 0xA1, 0x01, 0x05, 0x03, 3, 0, 41, 0 });
        check("GET_REPORT Feature 0x05 -> BT, auf wLength 41 gekürzt", feat.status == 0 && feat.data.Length == 41 && feat.data[0] == 5);
        var unknown = Control(new byte[] { 0xA1, 0x01, 0x99, 0x03, 3, 0, 16, 0 });
        check("Unbekannter Feature-Report -> STALL", unknown.status == -32);
        check("SET_INTERFACE 1 alt 1 (Lautsprecher an)", Control(new byte[] { 0x01, 0x0B, 1, 0, 1, 0, 0, 0 }).status == 0 && alts.Contains((1, 1)));
        var vmin = Control(new byte[] { 0xA1, 0x82, 0, 2, 0, 2, 2, 0 });
        check("Audio GET_MIN Lautstärke = -100 dB", vmin.data.SequenceEqual(new byte[] { 0x00, 0x9C }));
        var freq = Control(new byte[] { 0xA2, 0x81, 0, 1, 0x01, 0, 3, 0 });
        check("Endpoint-Samplerate = 48000", freq.data.SequenceEqual(new byte[] { 0x80, 0xBB, 0x00 }));

        // --- HID OUT (output report 0x02 -> BT)
        var outRep = new byte[48]; outRep[0] = 0x02; outRep[45] = 0xAB;
        SendSubmit(st, seq++, false, 3, outRep.Length, 0, new byte[8], outRep, null);
        var r3 = UsbIpServer.ReadExact(st, 48);
        check("Interrupt-OUT 0x02 angenommen und weitergereicht", Be32(r3, 20) == 0 && Be32(r3, 24) == 48 && outputs.Count == 1 && outputs[0][45] == 0xAB);

        // --- HID IN: pending until input arrives
        var inSeq = seq++;
        SendSubmit(st, inSeq, true, 4, 64, 0, new byte[8], null, null);
        Thread.Sleep(30);
        var input = new byte[64]; input[0] = 1; input[1] = 0x80; input[8] = 0x28;
        vds.PushInput(input);
        var r4 = UsbIpServer.ReadExact(st, 48);
        var d4 = UsbIpServer.ReadExact(st, Be32(r4, 24));
        check("Interrupt-IN wartet und liefert Eingabe-Report 0x01", (uint)Be32(r4, 4) == inSeq && d4.Length == 64 && d4[8] == 0x28);

        // --- UNLINK of a pending interrupt IN
        var pendSeq = seq++;
        SendSubmit(st, pendSeq, true, 4, 64, 0, new byte[8], null, null);
        Thread.Sleep(20);
        var unlink = new byte[48];
        BinaryPrimitives.WriteInt32BigEndian(unlink, 2);
        BinaryPrimitives.WriteUInt32BigEndian(unlink.AsSpan(4), seq++);
        BinaryPrimitives.WriteUInt32BigEndian(unlink.AsSpan(20), pendSeq);
        st.Write(unlink);
        var r5 = UsbIpServer.ReadExact(st, 48);
        check("UNLINK wartender URB -> RET_UNLINK -ECONNRESET", Be32(r5, 0) == 4 && Be32(r5, 20) == -104);

        // --- isochronous OUT: 10 x 1 ms of 4-ch audio, sine on ch 3
        const int packets = 10, bytesPerPacket = 48 * 8;
        var iso = new byte[packets * bytesPerPacket];
        for (var f = 0; f < packets * 48; f++)
            BinaryPrimitives.WriteInt16LittleEndian(iso.AsSpan(f * 8 + 4), (short)(Math.Sin(2 * Math.PI * 100 * f / 48000.0) * 20000));
        var sw = Stopwatch.StartNew();
        var isoSeqA = seq++;
        SendSubmit(st, isoSeqA, false, 1, iso.Length, packets, new byte[8], iso, Enumerable.Range(0, packets).Select(i => (i * bytesPerPacket, bytesPerPacket)).ToArray());
        var isoSeqB = seq++;
        SendSubmit(st, isoSeqB, false, 1, iso.Length, packets, new byte[8], iso, Enumerable.Range(0, packets).Select(i => (i * bytesPerPacket, bytesPerPacket)).ToArray());
        var ra = UsbIpServer.ReadExact(st, 48); UsbIpServer.ReadExact(st, packets * 16);
        var ta = sw.Elapsed.TotalMilliseconds;
        var rb = UsbIpServer.ReadExact(st, 48); var descB = UsbIpServer.ReadExact(st, packets * 16);
        var tb = sw.Elapsed.TotalMilliseconds;
        check($"Iso-OUT im Echtzeittakt abgeschlossen ({ta:F0} ms / {tb:F0} ms, erwartet ~10 / ~20)",
            (uint)Be32(ra, 4) == isoSeqA && (uint)Be32(rb, 4) == isoSeqB && ta is > 7 and < 25 && tb is > 17 and < 35);
        check("Iso-OUT: actual_length = 3840, Paket-Deskriptoren korrekt",
            Be32(rb, 24) == packets * bytesPerPacket && Be32(rb, 32) == packets && Be32(descB, 16 * 9 + 8) == bytesPerPacket);
        lock (audio) check("Audio-Kanal 3 kommt beim Haptik-Pfad an", audio.Count == 2 * packets * 48 * 4 && audio.Where((_, i) => i % 4 == 2).Max() > 19000 && audio.Where((_, i) => i % 4 == 0).All(v => v == 0));

        // --- isochronous IN (mic): 192 bytes per ms, packed
        var micSeq = seq++;
        SendSubmit(st, micSeq, true, 2, packets * 196, packets, new byte[8], null, Enumerable.Range(0, packets).Select(i => (i * 196, 196)).ToArray());
        var rm = UsbIpServer.ReadExact(st, 48);
        var micData = UsbIpServer.ReadExact(st, Be32(rm, 24));
        var micDesc = UsbIpServer.ReadExact(st, packets * 16);
        check("Iso-IN Mikrofon: 10 x 192 Bytes Stille, Offsets erhalten",
            micData.Length == packets * 192 && Be32(micDesc, 16 * 3) == 3 * 196 && Be32(micDesc, 16 * 3 + 8) == 192);

        cts.Cancel();
        return 0;
    }

    static void SendSubmit(Stream s, uint seq, bool dirIn, int ep, int len, int packets, byte[] setup, byte[]? data, (int off, int len)[]? iso)
    {
        var h = new byte[48];
        BinaryPrimitives.WriteInt32BigEndian(h, 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seq);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(8), 0x10001);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(12), dirIn ? 1 : 0);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(16), ep);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), len);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(32), iso == null ? -1 : packets);
        setup.CopyTo(h, 40);
        var m = new MemoryStream();
        m.Write(h);
        if (!dirIn && data != null) m.Write(data);
        if (iso != null)
            foreach (var (off, l) in iso)
            {
                var d = new byte[16];
                BinaryPrimitives.WriteInt32BigEndian(d, off);
                BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4), l);
                m.Write(d);
            }
        s.Write(m.ToArray());
    }

    static int Be32(byte[] b, int o) => BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o));
    static int Be16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
}
