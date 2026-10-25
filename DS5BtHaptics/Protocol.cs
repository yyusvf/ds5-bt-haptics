/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 * Derived from DS5Dongle src/audio.cpp (Copyright awalol), https://github.com/awalol/DS5Dongle
 */

namespace DS5BtHaptics;

/// <summary>
/// DualSense Bluetooth output reports, ported from DS5Dongle (awalol/DS5Dongle, MPL-2.0):
/// src/audio.cpp (audio_send_haptics_only) and src/utils.h (CRC).
/// </summary>
internal sealed class Ds5BtProtocol
{
    public const int HapticSampleRate = 3000;     // the voice coils run on 3 kHz audio
    public const int FrameBytes = 64;             // 32 stereo samples, int8
    public const int FrameSamples = FrameBytes / 2;
    public const byte ReportHapticsOnly = 0x32;
    public const int ReportHapticsOnlySize = 142;
    public const byte ReportState = 0x31;
    public const int ReportStateSize = 78;

    byte _seq;          // shared 4-bit sequence over all BT output reports
    byte _packetCounter;

    public byte BufferLength { get; init; } = 48; // DS5Dongle default "audio_buffer_length"

    /// <summary>CRC32 seeded with the BT HID header byte 0xA2 (DATA | OUTPUT).</summary>
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var b = 0; b < 8; b++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
        return c;
    }).ToArray();

    internal static void FillCrc(Span<byte> report)
    {
        var crc = ~0xEADA2D49u; // == crc32 state after the byte 0xA2
        foreach (var b in report[..^4]) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        crc = ~crc;
        report[^4] = (byte)crc;
        report[^3] = (byte)(crc >> 8);
        report[^2] = (byte)(crc >> 16);
        report[^1] = (byte)(crc >> 24);
    }

    byte NextSeq()
    {
        var s = (byte)(_seq << 4);
        _seq = (byte)((_seq + 1) & 0x0F);
        return s;
    }

    /// <summary>Report 0x32 with one or two 64-byte haptic frames (stereo int8 @ 3 kHz).</summary>
    public byte[] BuildHapticsOnly(ReadOnlySpan<sbyte> frames, bool micEnabled = false)
    {
        var doubleFrame = frames.Length == 2 * FrameBytes;
        if (!doubleFrame && frames.Length != FrameBytes) throw new ArgumentException("1 or 2 frames expected");

        var pkt = new byte[ReportHapticsOnlySize];
        pkt[0] = ReportHapticsOnly;
        pkt[1] = NextSeq();
        pkt[2] = 0x11 | 1 << 7;
        pkt[3] = 4;
        pkt[4] = micEnabled ? (byte)0b01100111 : (byte)0b01100110;
        pkt[5] = BufferLength;
        pkt[6] = BufferLength;
        pkt[7] = _packetCounter;
        _packetCounter += (byte)(doubleFrame ? 2 : 1);

        pkt[8] = (byte)(0x12 | (doubleFrame ? 1 : 0) << 6 | 1 << 7);
        pkt[9] = FrameBytes;
        for (var i = 0; i < frames.Length; i++) pkt[10 + i] = (byte)frames[i];
        FillCrc(pkt);
        return pkt;
    }

    /// <summary>Same 0x32 control block DS5Dongle sends right after connecting (update_mic_status).</summary>
    public byte[] BuildAudioControl(bool micEnabled = false)
    {
        var pkt = new byte[ReportHapticsOnlySize];
        pkt[0] = ReportHapticsOnly;
        pkt[1] = NextSeq();
        pkt[2] = 0x11 | 0 << 6 | 1 << 7;
        pkt[3] = 1;
        pkt[4] = micEnabled ? (byte)0b00000011 : (byte)0b00000010;
        FillCrc(pkt);
        return pkt;
    }

    /// <summary>
    /// USB output report 0x02 body (SetStateData, 47 bytes, without id) -> BT report 0x31
    /// (DS5Dongle main.cpp forward_output_report).
    /// </summary>
    public byte[] BuildStateFromUsb(ReadOnlySpan<byte> state)
    {
        var pkt = new byte[ReportStateSize];
        pkt[0] = ReportState;
        pkt[1] = NextSeq();
        pkt[2] = 0x10;
        state[..Math.Min(state.Length, 47)].CopyTo(pkt.AsSpan(3));
        FillCrc(pkt);
        return pkt;
    }

    /// <summary>Feature reports over BT carry a CRC seeded with the SET_REPORT header byte 0x53.</summary>
    internal static void FillFeatureCrc(Span<byte> report)
    {
        var crc = ~0x2060EFC3u; // == crc32 state after the byte 0x53
        foreach (var b in report[..^4]) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        crc = ~crc;
        report[^4] = (byte)crc;
        report[^3] = (byte)(crc >> 8);
        report[^2] = (byte)(crc >> 16);
        report[^1] = (byte)(crc >> 24);
    }

    /// <summary>Classic 0x31 state report (as in Linux hid-playstation); used here for the light bar.</summary>
    public byte[] BuildLightbar(byte r, byte g, byte b)
    {
        var pkt = new byte[ReportStateSize];
        pkt[0] = ReportState;
        pkt[1] = NextSeq();
        pkt[2] = 0x10;                 // DS_OUTPUT_TAG
        const int common = 3;
        pkt[common + 1] = 0x04;        // valid_flag1: LIGHTBAR_CONTROL_ENABLE
        pkt[common + 44] = r;
        pkt[common + 45] = g;
        pkt[common + 46] = b;
        FillCrc(pkt);
        return pkt;
    }
}
