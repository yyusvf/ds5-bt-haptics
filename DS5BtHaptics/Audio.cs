using NAudio.Wave;

namespace DS5BtHaptics;

/// <summary>Stereo float samples at 3 kHz, consumed by the streamer.</summary>
internal interface IHapticSource
{
    /// <summary>Fills dest (interleaved L/R) and returns the number of stereo frames written; -1 = end.</summary>
    int Read(Span<float> dest);
}

/// <summary>Streaming linear-interpolation resampler, like DS5Dongle's WDL_Resampler in mode (interp, no filter).</summary>
internal sealed class LinearResampler(double inRate, double outRate)
{
    readonly double _step = inRate / outRate;
    double _pos;           // position of next output sample relative to _prev
    float _prevL, _prevR;
    bool _primed;

    public void Process(ReadOnlySpan<float> stereoIn, List<float> stereoOut)
    {
        for (var i = 0; i < stereoIn.Length / 2; i++)
        {
            float l = stereoIn[2 * i], r = stereoIn[2 * i + 1];
            if (!_primed) { _prevL = l; _prevR = r; _primed = true; continue; }
            while (_pos < 1.0)
            {
                stereoOut.Add(_prevL + (float)_pos * (l - _prevL));
                stereoOut.Add(_prevR + (float)_pos * (r - _prevR));
                _pos += _step;
            }
            _pos -= 1.0;
            _prevL = l; _prevR = r;
        }
    }
}

/// <summary>Fixed buffer of 3 kHz samples (test patterns, WAV files).</summary>
internal sealed class BufferSource(float[] stereo) : IHapticSource
{
    int _pos;
    public double Seconds => stereo.Length / 2.0 / Ds5BtProtocol.HapticSampleRate;

    public int Read(Span<float> dest)
    {
        if (_pos >= stereo.Length) return -1;
        var n = Math.Min(dest.Length, stereo.Length - _pos);
        stereo.AsSpan(_pos, n).CopyTo(dest);
        dest[n..].Clear();
        _pos += n;
        return dest.Length / 2;
    }

    public static BufferSource FromWav(string path)
    {
        using var reader = new AudioFileReader(path); // float output, any format NAudio/Media Foundation can read
        var ch = reader.WaveFormat.Channels;
        var all = new List<float>();
        var block = new float[reader.WaveFormat.SampleRate * ch];
        int n;
        while ((n = reader.Read(block, 0, block.Length)) > 0) all.AddRange(block.AsSpan(0, n).ToArray());
        var stereo = ToHapticStereo(all.ToArray(), ch);
        var res = new LinearResampler(reader.WaveFormat.SampleRate, Ds5BtProtocol.HapticSampleRate);
        var outList = new List<float>();
        res.Process(stereo, outList);
        return new BufferSource(outList.ToArray());
    }

    /// <summary>
    /// 4-channel files are treated like the USB DualSense audio device: channels 3/4 are haptics.
    /// Mono is duplicated, stereo used as-is.
    /// </summary>
    public static float[] ToHapticStereo(float[] interleaved, int channels)
    {
        var frames = interleaved.Length / channels;
        var o = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var s = i * channels;
            (o[2 * i], o[2 * i + 1]) = channels switch
            {
                1 => (interleaved[s], interleaved[s]),
                >= 4 => (interleaved[s + 2], interleaved[s + 3]),
                _ => (interleaved[s], interleaved[s + 1]),
            };
        }
        return o;
    }

    public static BufferSource TestPattern()
    {
        const int sr = Ds5BtProtocol.HapticSampleRate;
        var o = new List<float>();
        void Tone(double sec, double freqStart, double freqEnd, float ampL, float ampR)
        {
            var n = (int)(sec * sr);
            double phase = 0;
            for (var i = 0; i < n; i++)
            {
                var f = freqStart + (freqEnd - freqStart) * i / n;
                phase += 2 * Math.PI * f / sr;
                var env = Math.Min(1.0, Math.Min(i, n - i) / (0.01 * sr)); // 10 ms fade, no clicks
                var v = (float)(Math.Sin(phase) * env);
                o.Add(v * ampL); o.Add(v * ampR);
            }
        }
        void Silence(double sec) { for (var i = 0; i < (int)(sec * sr); i++) { o.Add(0); o.Add(0); } }
        void Clicks(int count, double interval)
        {
            for (var c = 0; c < count; c++)
            {
                Tone(0.025, 160, 160, 1, 1);
                Silence(interval - 0.025);
            }
        }

        Console.WriteLine("  Muster: links 100 Hz -> rechts 100 Hz -> beide 200 Hz -> Sweep 30..1000 Hz -> Klicks");
        Tone(0.6, 100, 100, 1, 0); Silence(0.3);
        Tone(0.6, 100, 100, 0, 1); Silence(0.3);
        Tone(0.6, 200, 200, 1, 1); Silence(0.3);
        Tone(3.0, 30, 1000, 0.9f, 0.9f); Silence(0.3);
        Clicks(8, 0.15);
        Silence(0.2);
        return new BufferSource(o.ToArray());
    }
}

/// <summary>Thread-safe FIFO of 3 kHz stereo samples, fed from any rate; bounded to a maximum latency.</summary>
internal sealed class PushSource : IHapticSource
{
    readonly LinearResampler _resampler;
    readonly Queue<float> _queue = new();
    readonly List<float> _tmp = new();
    readonly int _maxQueued;

    public PushSource(int inputRate, int maxLatencyMs)
    {
        _resampler = new LinearResampler(inputRate, Ds5BtProtocol.HapticSampleRate);
        _maxQueued = Ds5BtProtocol.HapticSampleRate * maxLatencyMs / 1000 * 2;
    }

    /// <summary>Adds interleaved stereo samples at the input rate.</summary>
    public void Push(ReadOnlySpan<float> stereo)
    {
        lock (_queue)
        {
            _tmp.Clear();
            _resampler.Process(stereo, _tmp);
            foreach (var s in _tmp) _queue.Enqueue(s);
            while (_queue.Count > _maxQueued) { _queue.Dequeue(); _queue.Dequeue(); } // bound latency
        }
    }

    /// <summary>Returns the number of real (non-padding) frames; the rest is silence.</summary>
    public int Read(Span<float> dest)
    {
        lock (_queue)
        {
            var n = Math.Min(dest.Length, _queue.Count & ~1);
            for (var i = 0; i < n; i++) dest[i] = _queue.Dequeue();
            dest[n..].Clear();
            return n / 2;
        }
    }
}

/// <summary>Captures what Windows is playing (WASAPI loopback) and turns it into haptics.</summary>
internal sealed class LoopbackSource : IHapticSource, IDisposable
{
    readonly WasapiLoopbackCapture _capture;
    readonly PushSource _fifo;
    float[] _conv = Array.Empty<float>();

    public string DeviceName { get; }

    public LoopbackSource(int maxLatencyMs)
    {
        _capture = new WasapiLoopbackCapture();
        var fmt = _capture.WaveFormat;
        DeviceName = $"{fmt.SampleRate} Hz, {fmt.Channels} ch, {fmt.Encoding}";
        _fifo = new PushSource(fmt.SampleRate, maxLatencyMs);
        _capture.DataAvailable += OnData;
    }

    public void Start() => _capture.StartRecording();

    void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture.WaveFormat;
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample != 32) return; // shared-mode mix is float
        var count = e.BytesRecorded / 4;
        if (_conv.Length < count) _conv = new float[count];
        Buffer.BlockCopy(e.Buffer, 0, _conv, 0, count * 4);
        _fifo.Push(BufferSource.ToHapticStereo(_conv[..count], fmt.Channels));
    }

    public int Read(Span<float> dest) => _fifo.Read(dest);

    public void Dispose()
    {
        _capture.StopRecording();
        _capture.Dispose();
    }
}

/// <summary>Plays the haptic test pattern on channels 3/4 of the DualSense USB audio device, like a game does.</summary>
internal static class DeviceTest
{
    public static int Run()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var device = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase));
        if (device == null)
        {
            Console.Error.WriteLine("Kein DualSense-Audiogerät gefunden (läuft 'bridge'?).");
            return 2;
        }
        var mix = device.AudioClient.MixFormat;
        Console.WriteLine($"Audiogerät: {device.FriendlyName} ({mix.SampleRate} Hz, {mix.Channels} Kanäle, {mix.BitsPerSample} Bit)");
        if (mix.Channels != 4 || mix.SampleRate != 48000) { Console.Error.WriteLine("Unerwartetes Format, erwartet 48 kHz / 4 Kanäle."); return 3; }

        // 3 kHz stereo test pattern -> 48 kHz, 4 channels, haptics on channels 3/4
        var pattern = BufferSource.TestPattern();
        var stereo3k = new float[(int)(pattern.Seconds * Ds5BtProtocol.HapticSampleRate) * 2];
        pattern.Read(stereo3k);
        var up = new List<float>();
        new LinearResampler(Ds5BtProtocol.HapticSampleRate, 48000).Process(stereo3k, up);
        var four = new float[up.Count * 2];
        for (var i = 0; i < up.Count / 2; i++)
        {
            four[i * 4 + 2] = up[2 * i];
            four[i * 4 + 3] = up[2 * i + 1];
        }
        // Shared mode only accepts the device mix format (WAVE_FORMAT_EXTENSIBLE, float or 16 bit)
        byte[] bytes;
        if (mix.BitsPerSample == 32)
        {
            bytes = new byte[four.Length * 4];
            Buffer.BlockCopy(four, 0, bytes, 0, bytes.Length);
        }
        else
        {
            bytes = new byte[four.Length * 2];
            for (var i = 0; i < four.Length; i++)
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)Math.Clamp(four[i] * 32767f, -32768f, 32767f));
        }
        var provider = new RawSourceWaveStream(new MemoryStream(bytes), mix);

        using var output = new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 50);
        output.Init(provider);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(100);
        Console.WriteLine("Abgespielt.");
        return 0;
    }
}
