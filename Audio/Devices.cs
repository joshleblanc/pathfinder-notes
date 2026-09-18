using System;
using System.Collections.Generic;
using System.Linq;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// Discover mics + loopback sources on the current platform.
/// The "default" device is whichever the platform backend puts first; users
/// can always override with an exact <c>--mic</c> / <c>--loopback</c> name.
/// </summary>
public static class Devices
{
    public static IReadOnlyList<AudioDevice> ListInputs() =>
        OperatingSystem.IsWindows() ? WasapiBackend.ListInputs() : PortAudioBackend.ListInputs();

    public static IReadOnlyList<AudioDevice> ListLoopbackSources() =>
        OperatingSystem.IsWindows() ? WasapiBackend.ListLoopbackSources() : PortAudioBackend.ListLoopbackSources();

    public static AudioDevice ResolveInput(string? nameOrNull)
    {
        var all = ListInputs();
        var pick = Resolve(all, nameOrNull);
        return pick ?? throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(nameOrNull)
                ? "No input devices found."
                : $"No input device named '{nameOrNull}'. Run with --list-devices to see available names.");
    }

    public static AudioDevice? ResolveLoopbackSource(string? nameOrNull)
    {
        var all = ListLoopbackSources();
        return Resolve(all, nameOrNull);
    }

    private static AudioDevice? Resolve(IReadOnlyList<AudioDevice> devices, string? nameOrNull)
    {
        if (devices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(nameOrNull) || nameOrNull == "-" ||
            nameOrNull.Equals("default", StringComparison.OrdinalIgnoreCase))
            return devices[0];
        return devices.FirstOrDefault(d =>
            string.Equals(d.Name, nameOrNull, StringComparison.OrdinalIgnoreCase));
    }
}
