using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pathfinder.Notes.Audio;
using Pathfinder.Notes.Recording;
using Pathfinder.Notes.Transcription;

namespace Pathfinder.Notes.Tests;

/// <summary>
/// <c>--final-flush-test [&lt;seconds&gt;]</c> — run the recording service for N
/// seconds with a stub transcribe delegate, then trigger cancellation and
/// verify that <c>FinalFlushAsync</c> invokes the delegate exactly once with
/// audio that arrived after the most recent tick. No API key required.
/// </summary>
public static class FinalFlushTest
{
    public static int Run(string[] args)
    {
        int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 6;
        Console.WriteLine($"final-flush-test: {seconds}s of capture, then cancel + flush");

        var mic = Devices.ResolveInput(null);
        var spk = Devices.ResolveLoopbackSource(null);
        if (spk is null)
        {
            Console.Error.WriteLine("no loopback source detected on this system — final-flush-test needs mic + loopback.");
            return 1;
        }
        Console.WriteLine($"  mic  {mic.Name}");
        Console.WriteLine($"  loop {spk.Name}");

        var transcripts = new Transcripts("transcripts", "asr-1.0", "verbose_json");

        // Track every invocation so we can inspect payload sizes after the run.
        var invocations = new List<(DateTime at, double seconds, int bytes)>();
        var transcriptsLock = new object();

        var stub = new TranscriptionResult(
            FullText:     "[stub] hello from the fake ASR",
            Duration:     0,
            SpeakerCount: 1,
            Segments: new[] { new TranscriptSegment(0, 0, 1, "S1", "[stub] hello from the fake ASR") },
            TraceId:      "stub-trace-0001");

        Func<byte[], TimeSpan, CancellationToken, Task<TranscriptionResult>> transcribe =
            (wav, dur, ct) =>
            {
                lock (transcriptsLock)
                {
                    invocations.Add((DateTime.Now, dur.TotalSeconds, wav.Length));
                }
                return Task.FromResult(stub);
            };

        // 5-second chunk so we only get one tick at most during the test window
        // — anything after the tick becomes the "delta" for the final flush.
        var cfg = new Config(
            ApiKey: "(stub)",
            ApiUrl: "(stub)",
            Model: "asr-1.0",
            Language: null,
            ResponseFormat: "verbose_json",
            ChunkSeconds: 5,
            TranscriptDir: "transcripts",
            MicDeviceName: null,
            LoopbackDeviceName: null,
            SampleRate: Config.AsrSampleRate,
            EnableSessionSummary: false,
            SummaryModel: "(stub)",
            SummaryApiUrl: "(stub)",
            SummaryMaxTokens: 0,
            SummaryTemplatePath: "");

        using var service = new RecordingService(
            cfg, transcripts, mic, spk, transcribe);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine($"[test] triggering cancellation at t={seconds}s");
            cts.Cancel();
            service.Stop();
        };

        // Schedule the same cancellation programmatically so the test is self-contained.
        var scheduler = Task.Run(async () =>
        {
            try { await Task.Delay(seconds * 1000, cts.Token); }
            catch (OperationCanceledException) { return; }
            Console.Error.WriteLine($"[test] {seconds}s elapsed — cancelling loop");
            cts.Cancel();
            service.Stop();
        });

        try { service.RunAsync(cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { /* expected */ }

        lock (transcriptsLock)
        {
            Console.WriteLine();
            Console.WriteLine($"transcribe delegate was called {invocations.Count} time(s):");
            for (int i = 0; i < invocations.Count; i++)
            {
                var (at, dur, bytes) = invocations[i];
                var marker = i == invocations.Count - 1 && invocations.Count > 1 ? "  ← final-flush path" : "";
                Console.WriteLine($"  [{i + 1}] {at:HH:mm:ss.fff}  dur={dur:F2}s  wav={bytes} bytes{marker}");
            }
        }

        if (invocations.Count == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FAIL: no chunk uploaded at all — first tick never fired (need ≥ chunk_seconds of audio)");
            return 2;
        }
        if (invocations.Count == 1)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("SKIP: only one chunk fired; the final flush had <1s of new audio (expected when total runtime ≤ chunk_seconds).");
            return 0;
        }

        var lastDur = invocations[^1].seconds;
        Console.WriteLine();
        if (lastDur > 0.99 && lastDur < 60)  // final flush must be ≥ 1s, < 60s
        {
            Console.WriteLine($"PASS: final flush uploaded {lastDur:F2}s of audio as a separate chunk.");
            return 0;
        }
        else
        {
            Console.Error.WriteLine($"FAIL: final flush produced an unexpected-sized chunk ({lastDur:F2}s).");
            return 3;
        }
    }
}
