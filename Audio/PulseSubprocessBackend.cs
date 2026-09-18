using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// Loopback capture (Linux) — spawns <c>parec</c> to record from a
/// PulseAudio/PipeWire source (typically a sink's <c>.monitor</c>). Used because
/// libportaudio on most Linux distros ships without a PulseAudio host API and
/// therefore can't see monitor sources directly.
/// </summary>
internal sealed class PulseSubprocessBackend : IAudioBackend
{
    private const int SampleRate = 48_000;
    private const int Channels   = 2;

    private readonly Process _proc;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _readerTask;
    private readonly Task _stderrTask;
    private bool _stopped;

    public string DeviceName { get; }

    public PulseSubprocessBackend(string sourceName, RingBuffer ring)
    {
        DeviceName = sourceName;

        var psi = new ProcessStartInfo("parec")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("--device=" + sourceName);
        psi.ArgumentList.Add("--format=float32le");
        psi.ArgumentList.Add("--rate=" + SampleRate);
        psi.ArgumentList.Add("--channels=" + Channels);

        try { _proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start `parec` to capture '{sourceName}'. " +
                "Is `pipewire-pulse` (or `pulseaudio`) installed and running? " +
                $"Underlying error: {ex.Message}");
        }

        _readerTask = Task.Run(() => PumpAsync(_proc.StandardOutput.BaseStream, ring));
        _stderrTask = Task.Run(() => DrainStderr(_proc.StandardError));
    }

    public void Start() { /* parec starts streaming immediately */ }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _stopCts.Cancel(); } catch { /* ignore */ }
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Stop();
        try { _readerTask.Wait(500); } catch { /* ignore */ }
        try { _stderrTask.Wait(500); } catch { /* ignore */ }
        try { _proc.Dispose(); } catch { /* ignore */ }
        _stopCts.Dispose();
    }

    private async Task PumpAsync(Stream stdout, RingBuffer ring)
    {
        var buf = new byte[8192];
        var pool = ArrayPool<float>.Shared;
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                int n;
                try { n = await stdout.ReadAsync(buf, _stopCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }   // parec killed → pipe closed
                if (n <= 0) break;

                int nFloats = n / 4;
                int nFrames = nFloats / Channels;
                if (nFrames <= 0) continue;

                var src = pool.Rent(nFloats);
                try
                {
                    Buffer.BlockCopy(buf, 0, src, 0, nFloats * 4);
                    var mono = new float[nFrames];
                    Resampler.ToMono(mono, src.AsSpan(0, nFloats), Channels);
                    var resampled = Resampler.DownsampleTo16kMono(mono, SampleRate);
                    if (resampled.Length > 0) ring.Write(resampled);
                }
                finally { pool.Return(src); }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[loopback] reader error: {ex.Message}");
        }
    }

    private async Task DrainStderr(StreamReader err)
    {
        // Drain stderr so the subprocess doesn't block on a full pipe; surface
        // only the first non-empty line as a warning so the user can debug.
        try
        {
            string? line;
            bool shown = false;
            while ((line = await err.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (!shown && !string.IsNullOrWhiteSpace(line))
                {
                    Console.Error.WriteLine($"[loopback/parec] {line}");
                    shown = true;
                }
            }
        }
        catch { /* ignore */ }
    }
}
