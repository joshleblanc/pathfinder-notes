using System;
using System.IO;
using System.Threading;
using Pathfinder.Notes.Audio;

namespace Pathfinder.Notes.Tests;

/// <summary>
/// <c>--self-test</c> — open mic + loopback for 5 seconds, mix + encode the
/// most-recent window to <c>self-test.wav</c>. Validates the capture pipeline
/// without spending API credits.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        Console.WriteLine("self-test: opening mic + loopback for 5s, writing self-test.wav");
        int seconds = 5;

        var mic = Devices.ResolveInput(null);
        var spk = Devices.ResolveLoopbackSource(null);
        if (spk is null)
        {
            Console.Error.WriteLine("no loopback source detected on this system — run with --no-loopback or fix audio routing.");
            return 1;
        }

        Console.WriteLine($"  mic     {mic.Name}");
        Console.WriteLine($"  loop    {spk.Name}");

        const int sr = Config.AsrSampleRate;
        var micBuf  = new RingBuffer(seconds * sr);
        var loopBuf = new RingBuffer(seconds * sr);

        using var micCap = AudioCapture.Open(mic, micBuf);
        using var spkCap = AudioCapture.Open(spk, loopBuf);

        try
        {
            micCap.Start();
            spkCap.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("capture start failed: " + ex.Message);
            return 1;
        }

        Thread.Sleep(seconds * 1000 + 250);

        try { micCap.Stop(); } catch { }
        try { spkCap.Stop(); } catch { }

        long micSamples = micBuf.TotalWritten;
        long loopSamples = loopBuf.TotalWritten;
        Console.WriteLine($"  mic buffer:  {micSamples} samples ({micSamples / (double)sr:F2}s)");
        Console.WriteLine($"  loop buffer: {loopSamples} samples ({loopSamples / (double)sr:F2}s)");

        if (micSamples == 0)
        {
            Console.Error.WriteLine("FAIL: mic buffer is empty — capture did not stream samples.");
            return 2;
        }

        // Mix + encode to wav (use whichever ring has fewer samples as the length).
        var micSnap = micBuf.Snapshot();
        var loopSnap = loopBuf.Snapshot();
        int n = (int)Math.Min(micSnap.Length, loopSnap.Length);
        var mix = new float[n];
        for (int i = 0; i < n; i++)
            mix[i] = Math.Clamp((micSnap[i] + loopSnap[i]) * 0.5f, -1f, 1f);

        // RMS level over the whole window for a quick "is anything happening" check.
        double sumSq = 0;
        for (int i = 0; i < n; i++) sumSq += mix[i] * mix[i];
        double rms = Math.Sqrt(sumSq / Math.Max(1, n));
        Console.WriteLine($"  mix RMS level: {rms:F4}  (silence ≈ 0.0000, loud ≈ 0.3+)");

        byte[] wav = WavEncoder.Encode(mix);
        var outPath = Path.Combine(Directory.GetCurrentDirectory(), "self-test.wav");
        File.WriteAllBytes(outPath, wav);
        Console.WriteLine($"  wrote {wav.Length} bytes → {outPath}");
        return 0;
    }
}
