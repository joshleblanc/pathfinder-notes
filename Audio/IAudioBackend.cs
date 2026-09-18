using System;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// Platform-specific audio-capture backend. Implementations live in
/// <c>WasapiBackend</c> (Windows), <c>PortAudioBackend</c> (Linux/macOS mic),
/// and <c>PulseSubprocessBackend</c> (Linux loopback via <c>parec</c>).
/// </summary>
internal interface IAudioBackend : IDisposable
{
    string DeviceName { get; }
    void Start();
    void Stop();
}
