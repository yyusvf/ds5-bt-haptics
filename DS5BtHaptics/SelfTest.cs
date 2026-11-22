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

        var spk = new SpeakerEncoder();
        for (var i = 0; i < 512 * 3; i++) spk.Push(0.3f * MathF.Sin(i * 0.05f), 0.3f * MathF.Sin(i * 0.07f));
        Check("Lautsprecher: Opus-Frames erst nach Vorpuffer, dann 2 x 200 B", spk.Take(2) is { Length: 400 } && spk.Take(2) == null);
        var opus = new byte[400];
        opus[0] = 0xAB;
        var a39 = new Ds5BtProtocol().BuildAudio(frames, opus, headphones: false);
        Check("0x39: Länge 547, Haptik ab 12, Lautsprecher-Block 0xD3 an 140, Opus ab 142, CRC",
            a39.Length == 547 && a39[10] == 0xD2 && a39[12] == unchecked((byte)-64) && a39[140] == 0xD3 && a39[141] == 200 &&
            a39[142] == 0xAB && BitConverter.ToUInt32(a39, 543) == RefCrc32(new byte[] { 0xA2 }.Concat(a39[..543])));
        var a36 = new Ds5BtProtocol().BuildAudio(frames.AsSpan(0, 64), opus.AsSpan(0, 200), headphones: true);
        Check("0x36: Länge 398, Kopfhörer-Block 0x96 an 77, Opus ab 79",
            a36.Length == 398 && a36[3] == 7 && a36[11] == 0x92 && a36[77] == 0x96 && a36[79] == 0xAB);
        Check("Bluetooth-Adresse aus Feature 0x09 (little endian)",
            Bluetooth.AddressFromPairing(new byte[] { 9, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 }) == 0x112233445566UL);

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
