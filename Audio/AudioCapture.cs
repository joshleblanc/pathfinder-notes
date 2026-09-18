using System;
using NAudio.CoreAudioApi;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// Opens an input or loopback stream and pushes mono float @ 16 kHz into the
/// supplied <see cref="RingBuffer"/>.
///   Windows: NAudio WASAPI (WasapiCapture / WasapiLoopbackCapture).
///   Linux/macOS: PortAudio (mic) or a <c>parec</c> subprocess for loopback.
/// Use <see cref="Open"/>; do not construct directly.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly IAudioBackend _backend;
    public string DeviceName { get; }

    public static AudioCapture Open(AudioDevice device, RingBuffer ring)
    {
        if (device.Handle is MMDevice mm)
            return new AudioCapture(WasapiBackend.Open(mm, device.Kind == DeviceKind.LoopbackSource, ring));
        if (device.Handle is int idx)
            return new AudioCapture(PortAudioBackend.Open(idx, ring));
        if (device.Handle is string monitor && device.Kind == DeviceKind.LoopbackSource)
            return new AudioCapture(PortAudioBackend.OpenLoopback(monitor, ring));
        throw new PlatformNotSupportedException(
            $"Audio device '{device.Name}' has no usable backend on this OS.");
    }

    private AudioCapture(IAudioBackend backend) { _backend = backend; DeviceName = backend.DeviceName; }

    public void Start() => _backend.Start();
    public void Stop()  => _backend.Stop();
    public void Dispose() => _backend.Dispose();
}
