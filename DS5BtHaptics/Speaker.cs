/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 * Speaker path derived from DS5Dongle src/audio.cpp speaker_proc (Copyright awalol), https://github.com/awalol/DS5Dongle
 */

using Concentus;
using Concentus.Enums;

namespace DS5BtHaptics;

/// <summary>
/// Controller speaker / headphone jack over Bluetooth (DS5Dongle speaker_proc): USB channels 1/2 in
/// blocks of 512 frames at 48 kHz (exactly one haptics frame, 10.67 ms), resampled to 480 frames and
/// Opus-encoded (CELT only, CBR) into 200-byte frames that ride along in reports 0x36/0x39.
/// </summary>
internal sealed class SpeakerEncoder
{
    public const int OpusFrameBytes = 200;
    const int InFrames = 512, OutFrames = 480;
    const int MaxQueued = 4;

    readonly IOpusEncoder _encoder;
    readonly float[] _in = new float[InFrames * 2];
    readonly float[] _out = new float[OutFrames * 2];
    readonly byte[] _packet = new byte[OpusFrameBytes];
    readonly Queue<byte[]> _frames = new();
    readonly object _lock = new();
    int _pos;
    bool _primed;
    long _lastAudibleTicks = -10_000;

    public SpeakerEncoder()
    {
        _encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;
        _encoder.Bitrate = OpusFrameBytes * 8 * 100;
        _encoder.UseVBR = false;
        _encoder.Complexity = 0;
    }

    /// <summary>Real sound came in during the last half second.</summary>
    public bool Active => Environment.TickCount64 - Volatile.Read(ref _lastAudibleTicks) < 500;

    /// <summary>One sample frame of USB channels 1/2.</summary>
    public void Push(float left, float right)
    {
        _in[_pos++] = left;
        _in[_pos++] = right;
        if (Math.Abs(left) > 1f / 4096 || Math.Abs(right) > 1f / 4096)
            Volatile.Write(ref _lastAudibleTicks, Environment.TickCount64);
        if (_pos < _in.Length) return;
        _pos = 0;
        if (!Active)
        {
            lock (_lock) { _frames.Clear(); _primed = false; }
            return;
        }
        Encode();
    }

    void Encode()
    {
        // 512 -> 480 frames: both span the same 10.67 ms, the controller plays them in step with the haptics.
        const double step = (double)InFrames / OutFrames;
        for (var j = 0; j < OutFrames; j++)
        {
            var t = j * step;
            var i = (int)t;
            var f = (float)(t - i);
            var n = Math.Min(i + 1, InFrames - 1);
            _out[2 * j] = (_in[2 * i] + (_in[2 * n] - _in[2 * i]) * f);
            _out[2 * j + 1] = (_in[2 * i + 1] + (_in[2 * n + 1] - _in[2 * i + 1]) * f);
        }
        Array.Clear(_packet);
        var len = _encoder.Encode(_out, OutFrames, _packet, _packet.Length);
        if (len <= 0) return;
        var frame = (byte[])_packet.Clone();
        lock (_lock)
        {
            _frames.Enqueue(frame);
            while (_frames.Count > MaxQueued) _frames.Dequeue();
        }
    }

    /// <summary>
    /// Takes <paramref name="count"/> consecutive frames for one report, or returns null (send haptics
    /// only). After running dry it waits for one spare frame, so USB and BT timing jitter don't
    /// cause a gap every other packet.
    /// </summary>
    public byte[]? Take(int count)
    {
        lock (_lock)
        {
            if (!_primed && _frames.Count < count + 1) return null;
            if (_frames.Count < count) { _primed = false; return null; }
            _primed = true;
            var result = new byte[count * OpusFrameBytes];
            for (var i = 0; i < count; i++) _frames.Dequeue().CopyTo(result, i * OpusFrameBytes);
            return result;
        }
    }
}
