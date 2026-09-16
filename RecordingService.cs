using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace Pathfinder.Notes;

// ────────────────────────────────────────────────────────────────────────────
// RecordingService — owns the mic + (optional) loopback AudioCapture objects,
// their ring buffers, and the wall-clock tick loop. Every `chunk_seconds` it
// mixes the captured audio, encodes WAV, uploads to ASR, and appends to the
// rolling transcript. On cancellation, FinalFlushAsync stops the captures
// and uploads whatever new audio has accumulated since the last tick — so
// the user's last partial chunk isn't lost when they hit Ctrl-C.
// ────────────────────────────────────────────────────────────────────────────
public sealed class RecordingService : IDisposable
{
    private readonly Config _config;
    private readonly RingBuffer _micBuf;
    private readonly RingBuffer _loopBuf;
    private readonly Transcripts _transcripts;

    private readonly AudioCapture? _micCapture;
    private readonly AudioCapture? _loopCapture;
    private readonly CancellationTokenSource _linkedCts = new();
    private long _chunkIndex;

    // Delegate for the transcribe step. Production wires this to TranscriptionClient;
    // tests can substitute a stub so no API call is required.
    private readonly Func<byte[], TimeSpan, CancellationToken, Task<TranscriptionResult>> _transcribe;

    // Total-written cursors captured at the moment of each TickAsync snapshot.
    // Used to slice out *new* audio for FinalFlushAsync so we never re-upload
    // audio the previous tick already processed.
    private long _lastSnapshotTotalMic;
    private long _lastSnapshotTotalLoop;

    public RecordingService(
        Config config,
        TranscriptionClient client,
        Transcripts transcripts,
        MMDevice micDevice,
        MMDevice? loopDevice)
        : this(config, transcripts, micDevice, loopDevice,
              (wav, dur, ct) => client.TranscribeAsync(wav, dur, ct))
    { }

    /// <summary>Test-friendly constructor: caller supplies the transcribe delegate.</summary>
    public RecordingService(
        Config config,
        Transcripts transcripts,
        MMDevice micDevice,
        MMDevice? loopDevice,
        Func<byte[], TimeSpan, CancellationToken, Task<TranscriptionResult>> transcribe)
    {
        _config      = config;
        _transcripts = transcripts;
        _transcribe  = transcribe;

        int bufferSamples = config.ChunkSeconds * Config.AsrSampleRate;
        _micBuf  = new RingBuffer(bufferSamples);
        _loopBuf = new RingBuffer(bufferSamples);

        _micCapture  = new AudioCapture(micDevice, _micBuf,  loopback: false);
        _loopCapture = loopDevice is null
            ? null
            : new AudioCapture(loopDevice, _loopBuf, loopback: true);
    }

    public string MicDeviceName   => _micCapture?.DeviceName ?? "(none)";
    public string? LoopDeviceName => _loopCapture?.DeviceName;
    public string TranscriptTextPath => _transcripts.TextPath;
    public string TranscriptLogPath  => _transcripts.LogPath;

    public async Task RunAsync(CancellationToken externalCt)
    {
        try { _micCapture?.Start();  } catch (Exception ex) { Console.Error.WriteLine("mic start: " + ex.Message); }
        try { _loopCapture?.Start(); } catch (Exception ex) { Console.Error.WriteLine("loop start: " + ex.Message); }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _linkedCts.Token);
        try
        {
            await LoopAsync(linked.Token);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }

        await FinalFlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Soft-cancel: signals the loop to exit. Captures keep running so the
    /// final-flush path can read whatever's in the rings. FinalFlushAsync
    /// (called automatically at the end of RunAsync) is what stops the captures.
    /// </summary>
    public void Stop()
    {
        try { _linkedCts.Cancel(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        // Captures were already stopped by FinalFlushAsync. Just dispose them.
        try { _micCapture?.Dispose();  } catch { /* ignore */ }
        try { _loopCapture?.Dispose(); } catch { /* ignore */ }
        _linkedCts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromSeconds(_config.ChunkSeconds);
        var lastTick = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                if (now - lastTick < tick) continue;
                lastTick = now;

                await TickAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        long need = (long)_config.ChunkSeconds * Config.AsrSampleRate;
        if (_micBuf.TotalWritten < need) return;

        var monoMic = _micBuf.Snapshot();
        float[] mix = _loopCapture != null ? Mix(monoMic, _loopBuf.Snapshot()) : monoMic;

        _lastSnapshotTotalMic  = _micBuf.TotalWritten;
        _lastSnapshotTotalLoop = _loopBuf.TotalWritten;

        double seconds = mix.Length / (double)Config.AsrSampleRate;
        if (seconds < 1.0) return;

        byte[] wav = WavEncoder.Encode(mix);

        try
        {
            var result = await _transcribe(wav, TimeSpan.FromSeconds(seconds), ct)
                                       .ConfigureAwait(false);
            int idx = (int)Interlocked.Increment(ref _chunkIndex);
            _transcripts.Append(idx, seconds, result);

            var stamp = DateTime.Now.ToString("HH:mm:ss");
            var (preview, spkLabel) = PreviewFirstSegment(result);
            if (preview.Length == 0)
                Console.WriteLine($"[{stamp}] chunk {idx} ({seconds:F1}s) — no transcribed text");
            else
                Console.WriteLine($"[{stamp}] chunk {idx} ({seconds:F1}s, {spkLabel}) ✓ \"{preview}\"");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            Console.Error.WriteLine($"[{stamp}] chunk upload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called from RunAsync once the main loop has been cancelled. Stops both
    /// WASAPI captures, drains any in-flight NAudio callbacks, and uploads the
    /// audio that arrived since the last regular tick as one final chunk.
    ///
    /// Final flush always uses CancellationToken.None for the upload (so even
    /// if the user cancels the loop, the in-progress upload completes).
    /// </summary>
    private async Task FinalFlushAsync()
    {
        // Stop captures synchronously so no new audio races with serialization.
        try { _micCapture?.Stop();  } catch { /* ignore */ }
        try { _loopCapture?.Stop(); } catch { /* ignore */ }

        // Small grace period so any in-flight DataAvailable callback finishes writing to the rings.
        try { await Task.Delay(150).ConfigureAwait(false); } catch { /* ignore */ }

        long sr = Config.AsrSampleRate;
        long deltaMic  = _micBuf.TotalWritten - _lastSnapshotTotalMic;
        long deltaLoop = _loopCapture != null
            ? _loopBuf.TotalWritten - _lastSnapshotTotalLoop
            : long.MaxValue;
        long delta = Math.Min(deltaMic, deltaLoop);

        if (delta < sr)  // less than 1 second of new audio — not worth an API call
        {
            Console.Error.WriteLine($"[final] only {(double)delta / sr:F2}s since last chunk; skipping final ASR");
            return;
        }

        // Slice the trailing `delta` samples out of each snapshot. Bounded by
        // ring capacity so we never read past the buffer.
        int n = (int)Math.Min(delta, (long)_micBuf.Capacity);
        var monoMic  = _micBuf.Snapshot();
        var monoLoop = _loopBuf.Snapshot();
        var micSlice  = new float[n];
        var loopSlice = new float[n];
        Array.Copy(monoMic,  monoMic.Length  - n, micSlice,  0, n);
        if (_loopCapture != null)
            Array.Copy(monoLoop, monoLoop.Length - n, loopSlice, 0, n);

        float[] mix = _loopCapture != null ? Mix(micSlice, loopSlice) : micSlice;

        double seconds = mix.Length / (double)Config.AsrSampleRate;
        byte[] wav = WavEncoder.Encode(mix);

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Console.Error.WriteLine($"[final] uploading last {seconds:F1}s of audio…");

        try
        {
            var result = await _transcribe(wav, TimeSpan.FromSeconds(seconds), CancellationToken.None)
                                       .ConfigureAwait(false);
            int idx = (int)Interlocked.Increment(ref _chunkIndex);
            _transcripts.Append(idx, seconds, result);

            var (preview, spkLabel) = PreviewFirstSegment(result);
            if (preview.Length == 0)
                Console.WriteLine($"[{stamp}] [final] chunk {idx} ({seconds:F1}s) — no transcribed text");
            else
                Console.WriteLine($"[{stamp}] [final] chunk {idx} ({seconds:F1}s, {spkLabel}) ✓ \"{preview}\"");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[final] upload failed: {ex.Message}");
        }
    }

    private static float[] Mix(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        var dst = new float[n];
        for (int i = 0; i < n; i++)
            dst[i] = Math.Clamp((a[i] + b[i]) * 0.5f, -1f, 1f);
        return dst;
    }

    private static (string text, string speakerLabel) PreviewFirstSegment(TranscriptionResult r)
    {
        if (r.Segments.Count == 0) return ("", "0 speakers");
        var first = r.Segments[0];
        var clean = first.Text.Replace("\n", " ").Trim();
        var preview = clean.Length <= 80 ? clean : clean[..77] + "…";
        var label = $"{r.SpeakerCount} speaker{(r.SpeakerCount == 1 ? "" : "s")}";
        return (preview, label);
    }
}
