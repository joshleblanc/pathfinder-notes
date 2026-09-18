using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Pathfinder.Notes.Audio;

namespace Pathfinder.Notes.Transcription;

/// <summary>
/// <c>--transcribe-file &lt;path&gt;</c> mode: decode an existing audio file
/// (WAV/MP3/M4A/AAC/OGG/FLAC), downmix + resample to 16 kHz mono, slice into
/// <c>chunk_seconds</c> windows, and upload each to the ASR endpoint. Once the
/// file is exhausted, drain the tail. Optionally generates the PF2e summary
/// afterwards. Supported decoders: Media Foundation (Windows; broad format
/// coverage) with plain <see cref="WaveFileReader"/> as the WAV fallback.
/// </summary>
public sealed class FileTranscriber : IDisposable
{
    private readonly Config _config;
    private readonly TranscriptionClient _client;
    private readonly Transcripts _transcripts;
    private long _chunkIndex;
    private WaveStream? _reader;   // disposed in Dispose so the underlying file handle is released

    public FileTranscriber(Config config, TranscriptionClient client, Transcripts transcripts)
    {
        _config      = config;
        _client      = client;
        _transcripts = transcripts;
    }

    public void Dispose()
    {
        try { _reader?.Dispose(); } catch { /* ignore */ }
    }

    public async Task<int> RunAsync(string audioPath, CancellationToken ct)
    {
        if (!File.Exists(audioPath))
        {
            Console.Error.WriteLine($"file not found: {audioPath}");
            return 1;
        }

        Console.WriteLine($"audio file:  {Path.GetFullPath(audioPath)} ({new FileInfo(audioPath).Length / 1024.0 / 1024.0:F2} MB)");

        // Open + downmix + resample.
        ISampleProvider provider;
        try
        {
            provider = OpenAsSampleProvider(audioPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not open audio file: " + ex.Message);
            Console.Error.WriteLine("hint: most WAV / MP3 / M4A / AAC / OGG / FLAC files work. Convert and retry if your format is exotic.");
            return 2;
        }

        var inFmt = provider.WaveFormat;
        if (inFmt.Channels > 1)
        {
            Console.WriteLine($"  channels:   {inFmt.Channels} → 1 (downmix)");
            provider = provider.ToMono();
        }
        else
        {
            Console.WriteLine($"  channels:   1");
        }

        if (inFmt.SampleRate != Config.AsrSampleRate)
        {
            Console.WriteLine($"  sample rate:{inFmt.SampleRate} → {Config.AsrSampleRate} Hz");
            provider = new WdlResamplingSampleProvider(provider, Config.AsrSampleRate);
        }
        else
        {
            Console.WriteLine($"  sample rate:{Config.AsrSampleRate} Hz");
        }

        int sr             = Config.AsrSampleRate;
        int chunkSamples   = _config.ChunkSeconds * sr;
        var buffer         = new float[chunkSamples * 2];   // 2× chunk size for partial accumulation
        int writeIdx       = 0;
        long totalSamples  = 0;
        var stopwatch      = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            int want = buffer.Length - writeIdx;
            int n = provider.Read(buffer, writeIdx, want);
            if (n > 0)
            {
                writeIdx += n;
                totalSamples += n;
            }

            // Slice + upload full chunks.
            while (writeIdx >= chunkSamples)
            {
                var slice = new float[chunkSamples];
                Array.Copy(buffer, 0, slice, 0, chunkSamples);
                // shift the remainder
                int rem = writeIdx - chunkSamples;
                Array.Copy(buffer, chunkSamples, buffer, 0, rem);
                writeIdx = rem;

                await UploadChunkAsync(slice, sr, ct).ConfigureAwait(false);
            }

            if (n == 0)
            {
                // EOF
                break;
            }
        }

        // Drain the partial tail.
        if (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("[file] cancelled mid-stream.");
            return 130;
        }
        if (writeIdx > 0)
        {
            double seconds = writeIdx / (double)sr;
            if (seconds >= 1.0)
                await UploadTailAsync(buffer, writeIdx, sr, ct).ConfigureAwait(false);
            else
                Console.Error.WriteLine($"[file] tail was only {seconds:F2}s; skipped final ASR (below 1 s threshold).");
        }

        stopwatch.Stop();
        double totalSeconds = totalSamples / (double)sr;
        Console.WriteLine();
        Console.WriteLine($"file transcription: {(long)_chunkIndex} chunk(s), {totalSeconds:F1}s of audio in {stopwatch.Elapsed.TotalSeconds:F1}s wall clock");
        Console.WriteLine("session saved:");
        Console.WriteLine($"  text  {Path.GetFullPath(_transcripts.TextPath)}");
        Console.WriteLine($"  log   {Path.GetFullPath(_transcripts.LogPath)}");
        return 0;
    }

    private async Task UploadChunkAsync(float[] slice, int sr, CancellationToken ct)
    {
        double seconds = slice.Length / (double)sr;
        byte[] wav = WavEncoder.Encode(slice);

        try
        {
            var result = await _client.TranscribeAsync(wav, TimeSpan.FromSeconds(seconds), ct)
                                       .ConfigureAwait(false);
            int idx = (int)Interlocked.Increment(ref _chunkIndex);
            _transcripts.Append(idx, seconds, result);

            var stamp = DateTime.Now.ToString("HH:mm:ss");
            var (preview, label) = PreviewFirstSegment(result);
            if (preview.Length == 0)
                Console.WriteLine($"[{stamp}] chunk {idx} ({seconds:F1}s) — no transcribed text");
            else
                Console.WriteLine($"[{stamp}] chunk {idx} ({seconds:F1}s, {label}) ✓ \"{preview}\"");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            Console.Error.WriteLine($"[{stamp}] chunk upload failed: {ex.Message}");
        }
    }

    private async Task UploadTailAsync(float[] buffer, int count, int sr, CancellationToken ct)
    {
        var tail = new float[count];
        Array.Copy(buffer, 0, tail, 0, count);
        await UploadChunkAsync(tail, sr, ct).ConfigureAwait(false);
    }

    private ISampleProvider OpenAsSampleProvider(string path)
    {
        Exception? lastEx = null;

        // Preferred: Media Foundation — handles MP3, M4A, AAC, OGG, FLAC, etc.
        try
        {
            var mf = new MediaFoundationReader(path);
            if (mf.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat &&
                mf.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
            {
                mf.Dispose();
                throw new NotSupportedException(
                    $"Media Foundation decoded to encoding {mf.WaveFormat.Encoding}, expected IeeeFloat or Pcm.");
            }
            _reader = mf;
            return mf.ToSampleProvider();
        }
        catch (Exception ex)
        {
            lastEx = ex;
        }

        // Fallback: plain WAV.
        try
        {
            var wav = new WaveFileReader(path);
            _reader = wav;
            return wav.ToSampleProvider();
        }
        catch (Exception ex)
        {
            lastEx = ex;
        }

        throw new InvalidOperationException(
            $"Could not decode '{Path.GetFileName(path)}' as audio. " +
            $"Tried Media Foundation + WaveFileReader. Last error: {lastEx?.Message}",
            lastEx);
    }

    private static (string text, string label) PreviewFirstSegment(TranscriptionResult r)
    {
        if (r.Segments.Count == 0) return ("", "0 speakers");
        var first = r.Segments[0];
        var clean = first.Text.Replace("\n", " ").Trim();
        var preview = clean.Length <= 80 ? clean : clean[..77] + "…";
        var label = $"{r.SpeakerCount} speaker{(r.SpeakerCount == 1 ? "" : "s")}";
        return (preview, label);
    }
}
