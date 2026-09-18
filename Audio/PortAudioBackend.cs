using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PortAudioSharp;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// PortAudio backend (Linux/macOS). Mic capture via PortAudio; loopback
/// enumeration via <c>pactl</c> (PortAudio is usually built without a
/// PulseAudio host API on Linux, so it can't see PipeWire's <c>.monitor</c>
/// sources directly). Loopback capture itself lives in
/// <see cref="PulseSubprocessBackend"/>.
/// </summary>
internal static class PortAudioBackend
{
    private static readonly object _initLock = new();
    private static bool _initialized;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            PortAudio.Initialize();
            _initialized = true;
        }
    }

    public static IReadOnlyList<AudioDevice> ListInputs()
    {
        EnsureInitialized();
        var result = new List<AudioDevice>();
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            if (d.maxInputChannels > 0)
                result.Add(new AudioDevice
                {
                    Name = d.name,
                    Kind = LooksLikeMonitor(d.name) ? DeviceKind.LoopbackSource : DeviceKind.Input,
                    Handle = i,
                });
        }
        return result;
    }

    public static IReadOnlyList<AudioDevice> ListLoopbackSources()
    {
        // PortAudio on Linux is typically built without a PulseAudio host API, so its
        // device enumeration doesn't see PipeWire's `.monitor` sources. Query PulseAudio
        // directly (works through pipewire-pulse on PipeWire systems) for those.
        var result = new List<AudioDevice>();
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("sources");
            psi.ArgumentList.Add("short");
            using var p = Process.Start(psi);
            if (p is null) return result;

            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);

            foreach (var line in stdout.Split('\n'))
            {
                // <id>\t<name>\t<driver>\t<format>\t<state>
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var name = parts[1];
                if (LooksLikeMonitor(name))
                    result.Add(new AudioDevice
                    {
                        Name = name,
                        Kind = DeviceKind.LoopbackSource,
                        Handle = name,
                    });
            }
        }
        catch
        {
            // pactl missing or failed — caller treats empty result as "no loopback".
        }
        return result;
    }

    public static IAudioBackend Open(int deviceIndex, RingBuffer ring) =>
        new PortAudioCaptureBackend(deviceIndex, ring);

    public static IAudioBackend OpenLoopback(string monitorSource, RingBuffer ring) =>
        new PulseSubprocessBackend(monitorSource, ring);

    private static bool LooksLikeMonitor(string name) =>
        name.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".monitorfallback", StringComparison.OrdinalIgnoreCase);
}

internal sealed class PortAudioCaptureBackend : IAudioBackend
{
    private readonly PortAudioSharp.Stream _stream;
    private readonly RingBuffer _ring;
    private readonly int _srcRate;
    private readonly int _channels;

    public string DeviceName { get; }

    public PortAudioCaptureBackend(int deviceIndex, RingBuffer ring)
    {
        _ring = ring;

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        DeviceName = info.name;
        _channels = info.maxInputChannels;
        _srcRate = (int)info.defaultSampleRate;
        if (_channels <= 0)
            throw new NotSupportedException($"PortAudio device '{DeviceName}' has no input channels.");

        var inputParams = new StreamParameters
        {
            device = deviceIndex,
            channelCount = _channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        _stream = new PortAudioSharp.Stream(
            inParams: inputParams,
            outParams: null,
            sampleRate: _srcRate,
            framesPerBuffer: PortAudio.FramesPerBufferUnspecified,
            streamFlags: StreamFlags.NoFlag,
            callback: OnSamples,
            userData: null);
    }

    public void Start() => _stream.Start();
    public void Stop()  => _stream.Stop();
    public void Dispose() => _stream.Dispose();

    private StreamCallbackResult OnSamples(IntPtr input, IntPtr output, uint frameCount,
                                           ref StreamCallbackTimeInfo timeInfo,
                                           StreamCallbackFlags statusFlags,
                                           IntPtr userData)
    {
        int nFrames = (int)frameCount;
        int nTotal = nFrames * _channels;
        if (nTotal <= 0) return StreamCallbackResult.Continue;

        var src = ArrayPool<float>.Shared.Rent(nTotal);
        try
        {
            Marshal.Copy(input, src, 0, nTotal);
            var mono = new float[nFrames];
            Resampler.ToMono(mono, src.AsSpan(0, nTotal), _channels);
            var resampled = Resampler.DownsampleTo16kMono(mono, _srcRate);
            if (resampled.Length > 0) _ring.Write(resampled);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(src, clearArray: true);
        }
        return StreamCallbackResult.Continue;
    }
}
