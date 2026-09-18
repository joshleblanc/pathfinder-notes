namespace Pathfinder.Notes.Audio;

/// <summary>
/// Cross-platform handle to a single audio endpoint.
///   Windows: <see cref="Handle"/> is an NAudio MMDevice.
///   Linux/macOS: <see cref="Handle"/> is a boxed int (PortAudio device index) for
///     mic capture, or a string (PulseAudio `.monitor` source name) for loopback.
/// <see cref="Kind"/> tells the backend whether to treat the device as an input
/// or as a loopback source.
/// </summary>
public enum DeviceKind
{
    /// <summary>Microphone / capture endpoint.</summary>
    Input,

    /// <summary>Render endpoint (Windows) or PulseAudio `.monitor` source (Linux).</summary>
    LoopbackSource,
}

public sealed class AudioDevice
{
    public required string Name { get; init; }
    public required DeviceKind Kind { get; init; }
    internal object? Handle { get; init; }
}
