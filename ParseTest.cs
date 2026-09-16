using System;
using System.IO;
using Pathfinder.Notes;

namespace Pathfinder.Notes;

/// <summary>
/// `--parse &lt;file&gt;` mode: read a JSON file (in the API's verbose_json shape),
/// parse it via TranscriptionClient.ParseSample, write a sample transcript chunk
/// to ./transcripts/_parse_test/, then print what would have appeared on stdout.
/// Used to confirm the parser + transcript writer behave correctly without
/// spending API credits on a real call.
/// </summary>
public static class ParseTest
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: --parse <api-response.json>");
            return 1;
        }
        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"file not found: {path}");
            return 1;
        }
        var body = File.ReadAllText(path);
        var result = TranscriptionClient.ParseSample(body);
        Console.WriteLine($"full text: {result.FullText}");
        Console.WriteLine($"duration:  {result.Duration:F2}s");
        Console.WriteLine($"speakers:  {result.SpeakerCount}");
        Console.WriteLine($"trace:     {result.TraceId ?? "(none)"}");
        Console.WriteLine($"segments:  {result.Segments.Count}");
        foreach (var s in result.Segments)
            Console.WriteLine($"  @{s.Start,5:F2}s–@{s.End,5:F2}s [{s.Speaker}] {s.Text}");

        var dir = Path.Combine(Directory.GetCurrentDirectory(), "transcripts", "_parse_test");
        Directory.CreateDirectory(dir);
        var t = new Transcripts(dir, "asr-1.0", "verbose_json");
        t.Append(chunkIdx: 1, seconds: result.Duration, result);
        Console.WriteLine();
        Console.WriteLine($"wrote test transcript to: {t.TextPath}");
        Console.WriteLine($"and test log to:          {t.LogPath}");
        return 0;
    }
}
