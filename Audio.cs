using System;
using System.Buffers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pathfinder.Notes;

// ────────────────────────────────────────────────────────────────────────────
// RingBuffer — fixed-capacity, thread-safe circular buffer of mono float samples.
// Always-overwrites on Write, so Snapshot() returns "the most recent N samples".
// ────────────────────────────────────────────────────────────────────────────
public sealed class RingBuffer
{
    private readonly float[] _buf;
    private readonly object _lock = new();
    private int _writeIdx;
    private long _totalWritten;

    public RingBuffer(int capacity) { _buf = new float[capacity]; }
    public int Capacity => _buf.Length;
    public long TotalWritten { get { lock (_lock) { return _totalWritten; } } }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            int n = samples.Length;
            if (n >= _buf.Length)
            {
                samples = samples[^_buf.Length..];
                n = _buf.Length;
            }

            int end = _writeIdx + n;
            if (end <= _buf.Length)
            {
                samples.CopyTo(_buf.AsSpan(_writeIdx, n));
            }
            else
            {
                int k = _buf.Length - _writeIdx;
                samples.Slice(0, k).CopyTo(_buf.AsSpan(_writeIdx, k));
                samples.Slice(k, n - k).CopyTo(_buf.AsSpan(0, n - k));
            }
            _writeIdx = (_writeIdx + n) % _buf.Length;
            _totalWritten += n;
        }
    }

    /// <summary>Time-ordered copy of the most-recent samples (length == Capacity).</summary>
    public float[] Snapshot()
    {
        lock (_lock)
        {
            var copy = new float[_buf.Length];
            int tailLen = _buf.Length - _writeIdx;
            Array.Copy(_buf, _writeIdx, copy, 0, tailLen);
            Array.Copy(_buf, 0, copy, tailLen, _writeIdx);
            return copy;
        }
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Resampler — downmix multichannel to mono, then linear-interp resample to 16kHz.
// Good enough for speech ASR; not a textbook-quality resampler.
// ────────────────────────────────────────────────────────────────────────────
public static class Resampler
{
    public static void ToMono(Span<float> dst, ReadOnlySpan<float> src, int channels)
    {
        if (channels == 1)
        {
            src.CopyTo(dst);
            return;
        }
        int frames = src.Length / channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += src[i * channels + c];
            dst[i] = sum / channels;
        }
    }

    public static float[] DownsampleTo16kMono(ReadOnlySpan<float> mono, int srcRate, int targetRate = Config.AsrSampleRate)
    {
        if (srcRate == targetRate) return mono.ToArray();

        double step = (double)srcRate / targetRate;
        int outLen = (int)(mono.Length / step);
        if (outLen <= 0) return Array.Empty<float>();

        var dst = new float[outLen];
        double pos = 0;
        for (int i = 0; i < outLen; i++)
        {
            int idx = (int)pos;
            float frac = (float)(pos - idx);
            float a = idx < mono.Length ? mono[idx] : 0f;
            float b = (idx + 1) < mono.Length ? mono[idx + 1] : 0f;
            dst[i] = a * (1f - frac) + b * frac;
            pos += step;
        }
        return dst;
    }
}

// ────────────────────────────────────────────────────────────────────────────
// WavEncoder — float[] → 16-bit PCM mono WAV bytes.
// ────────────────────────────────────────────────────────────────────────────
public static class WavEncoder
{
    public static byte[] Encode(ReadOnlySpan<float> monoSamples, int sampleRate = Config.AsrSampleRate)
    {
        // Float -> PCM16
        var pcm = new byte[monoSamples.Length * 2];
        for (int i = 0; i < monoSamples.Length; i++)
        {
            float s = Math.Clamp(monoSamples[i], -1f, 1f);
            short v = (short)(s * 32767f);
            pcm[i * 2]     = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return BuildWavBytes(pcm, sampleRate, channels: 1, bitsPerSample: 16);
    }

    private static byte[] BuildWavBytes(byte[] pcm16, int sampleRate, int channels, int bitsPerSample)
    {
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataLen = pcm16.Length;
        int riffLen = 36 + dataLen;

        using var ms = new MemoryStream(44 + dataLen);
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffLen);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                         // PCM fmt chunk size
        bw.Write((short)1);                   // PCM format
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        bw.Write(pcm16);
        return ms.ToArray();
    }
}

// ────────────────────────────────────────────────────────────────────────────
// AudioCapture — wraps NAudio's WasapiCapture or WasapiLoopbackCapture.
// Converts each callback's interleaved IEEE-float samples to mono float at 16kHz
// and pushes into the supplied RingBuffer.
// ────────────────────────────────────────────────────────────────────────────
public sealed class AudioCapture : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly RingBuffer _ring;
    private readonly int _srcRate;
    private readonly int _channels;
    private float[]? _monoScratch;

    public string DeviceName { get; }

    public AudioCapture(MMDevice device, RingBuffer ring, bool loopback)
    {
        DeviceName = device.FriendlyName;
        _ring = ring;
        _capture = loopback
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device);

        var fmt = _capture.WaveFormat;
        _srcRate = fmt.SampleRate;
        _channels = fmt.Channels;

        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new NotSupportedException(
                $"Device '{DeviceName}' reports encoding {fmt.Encoding}; only IEEE float is supported.");

        _capture.DataAvailable += OnData;
    }

    public void Start() { _capture.StartRecording(); }
    public void Stop() { _capture.StopRecording(); }
    public void Dispose() { _capture.Dispose(); }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int nFrames = e.BytesRecorded / (4 * _channels);
        if (nFrames <= 0) return;

        // 1) bytes -> float[]
        var src = ArrayPool<float>.Shared.Rent(nFrames * _channels);
        try
        {
            Buffer.BlockCopy(e.Buffer, 0, src, 0, e.BytesRecorded);

            // 2) downmix to mono
            _monoScratch ??= new float[2048];
            if (_monoScratch.Length < nFrames) _monoScratch = new float[nFrames];
            Resampler.ToMono(_monoScratch.AsSpan(0, nFrames),
                             src.AsSpan(0, nFrames * _channels),
                             _channels);

            // 3) resample to 16kHz
            float[] resampled = Resampler.DownsampleTo16kMono(
                _monoScratch.AsSpan(0, nFrames), _srcRate);

            // 4) push to ring
            if (resampled.Length > 0) _ring.Write(resampled);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(src, clearArray: true);
        }
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Devices — discovery helpers for choosing capture + loopback devices.
// ────────────────────────────────────────────────────────────────────────────
public static class Devices
{
    public static (MMDeviceEnumerator enumerator, List<MMDevice> mics, List<MMDevice> renderers) List()
    {
        var en = new MMDeviceEnumerator();
        var mics = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var renderers = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        return (en, mics, renderers);
    }

    public static MMDevice FindByName(MMDeviceEnumerator en, DataFlow flow, string? nameOrNull)
    {
        if (string.IsNullOrWhiteSpace(nameOrNull) || nameOrNull == "-" || nameOrNull.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return en.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            if (string.Equals(d.FriendlyName, nameOrNull, StringComparison.OrdinalIgnoreCase))
                return d;
        throw new InvalidOperationException(
            $"No {flow} device named '{nameOrNull}'. Run with --list-devices to see available names.");
    }
}
