namespace DS5BtHaptics;

/// <summary>Checks that can run without a controller: CRC, report layout, resampling, timing.</summary>
internal static class SelfTest
{
    static int _failed;

    static void Check(string name, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "OK" : "FEHLER")}] {name}");
        if (!ok) _failed++;
    }

    /// <summary>Plain bitwise CRC32 (independent of the table version under test).</summary>
    static uint RefCrc32(IEnumerable<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static int Run()
    {
        Console.WriteLine("Selbsttest:");
        Check("Referenz-CRC32(\"123456789\") = CBF43926", RefCrc32("123456789"u8.ToArray()) == 0xCBF43926);
        Check("Seed 0xEADA2D49 = CRC32(0xA2)", RefCrc32(new byte[] { 0xA2 }) == 0xEADA2D49);

        var proto = new Ds5BtProtocol();
        var frames = new sbyte[128];
        for (var i = 0; i < frames.Length; i++) frames[i] = (sbyte)(i - 64);
        var pkt = proto.BuildHapticsOnly(frames);
        var crc = RefCrc32(new byte[] { 0xA2 }.Concat(pkt[..^4]));
        Check("0x32 CRC = CRC32(A2 + Report)", BitConverter.ToUInt32(pkt, pkt.Length - 4) == crc);
        Check("0x32 Länge 142", pkt.Length == 142);
        Check("0x32 Header wie DS5Dongle (32 00 91 04 66 30 30 00 D2 40)",
            pkt[..10].SequenceEqual(new byte[] { 0x32, 0x00, 0x91, 0x04, 0x66, 48, 48, 0x00, 0xD2, 0x40 }));
        Check("Haptik-Daten ab Byte 10, 128 Bytes", pkt[10] == unchecked((byte)-64) && pkt[137] == 63);
        var pkt2 = proto.BuildHapticsOnly(frames);
        Check("Sequenz +1, PacketCounter +2", pkt2[1] == 0x10 && pkt2[7] == 2);
        var single = proto.BuildHapticsOnly(frames.AsSpan(0, 64));
        Check("Einzel-Frame: Blockheader 0x92, PacketCounter danach +1", single[8] == 0x92 && single[7] == 4 &&
            proto.BuildHapticsOnly(frames.AsSpan(0, 64))[7] == 5);

        var lb = proto.BuildLightbar(1, 2, 3);
        Check("0x31 Lightbar: Länge 78, RGB an 47..49, CRC",
            lb.Length == 78 && lb[47] == 1 && lb[48] == 2 && lb[49] == 3 &&
            BitConverter.ToUInt32(lb, 74) == RefCrc32(new byte[] { 0xA2 }.Concat(lb[..74])));

        var res = new LinearResampler(48000, 3000);
        var outL = new List<float>();
        var oneSec = new float[48000 * 2];
        for (var i = 0; i < 48000; i++) { oneSec[2 * i] = (float)Math.Sin(2 * Math.PI * 100 * i / 48000.0); oneSec[2 * i + 1] = 0.5f; }
        for (var c = 0; c < 100; c++) res.Process(oneSec.AsSpan(c * 960, 960), outL); // 10-ms Blöcke wie WASAPI
        Check($"48 kHz -> 3 kHz: 1 s ergibt ~3000 Frames ({outL.Count / 2})", Math.Abs(outL.Count / 2 - 3000) <= 1);
        Check("Resampler erhält Pegel", Math.Abs(outL[^1] - 0.5f) < 1e-5 && outL.Where((_, i) => i % 2 == 0).Max() > 0.99f);

        var stereo = BufferSource.ToHapticStereo(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 4);
        Check("4-Kanal: nimmt Kanal 3/4 (wie USB-DualSense)", stereo.SequenceEqual(new float[] { 3, 4, 7, 8 }));

        var pattern = BufferSource.TestPattern();
        Check($"Testmuster {pattern.Seconds:F1} s", pattern.Seconds is > 5 and < 10);

        Console.WriteLine("USB/IP + virtueller DualSense:");
        try { UsbIpSelfTest.Run(Check); }
        catch (Exception e) { Check($"USB/IP-Test abgebrochen: {e.GetType().Name}: {e.Message}", false); }

        Console.WriteLine(_failed == 0 ? "Alle Prüfungen bestanden." : $"{_failed} Prüfung(en) fehlgeschlagen.");
        return _failed == 0 ? 0 : 5;
    }
}
