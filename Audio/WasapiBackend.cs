using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// WASAPI backend (Windows). Device discovery + capture via NAudio.
/// </summary>
internal static class WasapiBackend
{
    public static IReadOnlyList<AudioDevice> ListInputs()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                 .Select(mm => new AudioDevice { Name = mm.FriendlyName, Kind = DeviceKind.Input, Handle = mm })
                 .ToList();
    }

    public static IReadOnlyList<AudioDevice> ListLoopbackSources()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                 .Select(mm => new AudioDevice { Name = mm.FriendlyName, Kind = DeviceKind.LoopbackSource, Handle = mm })
                 .ToList();
    }

    public static IAudioBackend Open(MMDevice mm, bool loopback, RingBuffer ring) =>
        new WasapiCaptureBackend(mm, loopback, ring);
}

internal sealed class WasapiCaptureBackend : IAudioBackend
{
    private readonly WasapiCapture _capture;
    public string DeviceName { get; }

    public WasapiCaptureBackend(MMDevice mm, bool loopback, RingBuffer ring)
    {
        DeviceName = mm.FriendlyName;
        _capture = loopback ? new WasapiLoopbackCapture(mm) : new WasapiCapture(mm);

        var fmt = _capture.WaveFormat;
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new NotSupportedException(
                $"Device '{DeviceName}' reports encoding {fmt.Encoding}; only IEEE float is supported.");

        int srcRate = fmt.SampleRate;
        int channels = fmt.Channels;
        _capture.DataAvailable += (_, e) => PushFrames(e.Buffer, e.BytesRecorded, srcRate, channels, ring);
    }

    public void Start() => _capture.StartRecording();
    public void Stop()  => _capture.StopRecording();
    public void Dispose() => _capture.Dispose();

    private static void PushFrames(byte[] buffer, int bytesRecorded, int srcRate, int channels, RingBuffer ring)
    {
        int nFrames = bytesRecorded / (4 * channels);
        if (nFrames <= 0) return;

        var src = ArrayPool<float>.Shared.Rent(nFrames * channels);
        try
        {
            Buffer.BlockCopy(buffer, 0, src, 0, bytesRecorded);
            var mono = new float[nFrames];
            Resampler.ToMono(mono, src.AsSpan(0, nFrames * channels), channels);
            var resampled = Resampler.DownsampleTo16kMono(mono, srcRate);
            if (resampled.Length > 0) ring.Write(resampled);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(src, clearArray: true);
        }
    }
}
