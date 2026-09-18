using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pathfinder.Notes;
using Pathfinder.Notes.Audio;
using Pathfinder.Notes.Recording;
using Pathfinder.Notes.Summary;
using Pathfinder.Notes.Tests;
using Pathfinder.Notes.Transcription;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await DispatchAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Contains("--version"))      { Console.WriteLine("pathfinder-notes 0.1.0 (.NET 8 / NAudio 2.2)"); return 0; }
        if (args.Contains("--help") || args.Contains("-h")) { PrintHelp(); return 0; }
        if (args.Contains("--list-devices"))  return ListDevices();
        if (args.Contains("--self-test"))     return SelfTest.Run();
        if (args.Contains("--parse"))         return ParseTest.Run(args.Skip(1).ToArray());
        if (args.Contains("--final-flush-test")) return FinalFlushTest.Run(args.Skip(1).ToArray());
        if (args.Contains("--summarize-test"))   return SummaryTest.Run(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        if (args.Contains("--summarize-from"))   return ManualSummary.Run(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        if (args.Contains("--transcribe-file"))   return await TranscribeFileAsync(args);

        var opts = ParseArgs(args);
        return await RunAsync(opts);
    }

    private static Config LoadConfig(Options opts)
    {
        var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
        if (!File.Exists(envPath))
        {
            var alt = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            if (File.Exists(alt)) envPath = alt;
        }
        var env = EnvConfig.LoadDotEnv(envPath);

        if (opts.ChunkSeconds is > 0) env["CHUNK_SECONDS"] = opts.ChunkSeconds.ToString();
        if (opts.Language     is { Length: > 0 }) env["ASR_LANGUAGE"]         = opts.Language;
        if (opts.ResponseFormat is { Length: > 0 }) env["ASR_RESPONSE_FORMAT"] = opts.ResponseFormat;
        if (opts.ApiUrl        is { Length: > 0 }) env["MINIMAX_API_URL"]    = opts.ApiUrl;
        if (opts.Summarize) env["ENABLE_SESSION_SUMMARY"] = "true";
        if (opts.SummaryModel   is { Length: > 0 }) env["SUMMARY_MODEL"]       = opts.SummaryModel;
        if (opts.SummaryApiUrl  is { Length: > 0 }) env["SUMMARY_API_URL"]     = opts.SummaryApiUrl;
        if (opts.SummaryTemplate is { Length: > 0 }) env["SUMMARY_TEMPLATE_PATH"] = opts.SummaryTemplate;

        return new Config(
            ApiKey:             EnvConfig.Required("MINIMAX_API_KEY", env),
            ApiUrl:             EnvConfig.Optional("MINIMAX_API_URL", env, "https://api.minimax.io/v1/speech_to_text")!,
            Model:              EnvConfig.Optional("ASR_MODEL", env, "asr-1.0")!,
            Language:           EnvConfig.Optional("ASR_LANGUAGE", env, null),
            ResponseFormat:     EnvConfig.Optional("ASR_RESPONSE_FORMAT", env, "verbose_json")!,
            ChunkSeconds:       int.Parse(EnvConfig.Optional("CHUNK_SECONDS", env, "30")!),
            TranscriptDir:      EnvConfig.Optional("TRANSCRIPT_DIR", env, "transcripts")!,
            MicDeviceName:      opts.Mic,
            LoopbackDeviceName: opts.Loopback,
            SampleRate:         Config.AsrSampleRate,
            EnableSessionSummary: bool.TryParse(EnvConfig.Optional("ENABLE_SESSION_SUMMARY", env, "false"), out var en) && en,
            SummaryModel:       EnvConfig.Optional("SUMMARY_MODEL", env, "MiniMax-M3")!,
            SummaryApiUrl:      EnvConfig.Optional("SUMMARY_API_URL", env, "https://api.minimax.io/anthropic/v1/messages")!,
            SummaryMaxTokens:   int.Parse(EnvConfig.Optional("SUMMARY_MAX_TOKENS", env, "4096")!),
            SummaryTemplatePath:EnvConfig.Optional("SUMMARY_TEMPLATE_PATH", env, "")!);
    }

    private static int ListDevices()
    {
        var inputs = Devices.ListInputs();
        var loops  = Devices.ListLoopbackSources();
        var defInput = inputs.Count > 0 ? inputs[0].Name : null;
        var defLoop  = loops.Count  > 0 ? loops[0].Name  : null;

        Console.WriteLine("input  (microphones):");
        foreach (var d in inputs)
            Console.WriteLine($"  {d.Name}{(d.Name == defInput ? "  (default)" : "")}");
        if (loops.Count > 0)
        {
            Console.WriteLine("loopback (system audio sources — usable for capture):");
            foreach (var d in loops)
                Console.WriteLine($"  {d.Name}{(d.Name == defLoop ? "  (default)" : "")}");
        }
        else
        {
            Console.WriteLine("loopback (system audio sources — none detected on this system).");
        }
        return 0;
    }

    private static async Task<int> RunAsync(Options opts)
    {
        var cfg = LoadConfig(opts);

        var micDevice = Devices.ResolveInput(cfg.MicDeviceName);
        AudioDevice? loopDevice = null;
        if (!opts.NoLoopback)
        {
            try
            {
                loopDevice = Devices.ResolveLoopbackSource(cfg.LoopbackDeviceName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"warning: loopback disabled — {ex.Message}");
            }
        }

        var transcripts = new Transcripts(cfg.TranscriptDir, cfg.Model, cfg.ResponseFormat);
        using var client = new TranscriptionClient(cfg);
        using var service = new RecordingService(cfg, client, transcripts, micDevice, loopDevice);

        Console.WriteLine($"session      {transcripts.SessionId}");
        Console.WriteLine($"api url      {cfg.ApiUrl}");
        Console.WriteLine($"model        {cfg.Model}{(cfg.Language is { } lang ? $"  lang={lang}" : "")}");
        Console.WriteLine($"chunk        {cfg.ChunkSeconds}s  @ {cfg.SampleRate} Hz mono");
        Console.WriteLine($"mic device   {service.MicDeviceName}");
        if (loopDevice != null)
            Console.WriteLine($"loopback     {service.LoopDeviceName}  (system audio)");
        Console.WriteLine($"transcripts  {Path.GetFullPath(cfg.TranscriptDir)}\\");
        Console.WriteLine();
        Console.WriteLine("recording…  (Ctrl-C = graceful stop + final flush; Ctrl-C twice = force exit)");

        var cts = new CancellationTokenSource();
        int ctrlCHits = 0;
        Console.CancelKeyPress += (_, e) =>
        {
            ctrlCHits++;
            if (ctrlCHits == 1)
            {
                e.Cancel = true;            // first Ctrl-C: soft-stop, do final ASR
                Console.Error.WriteLine();
                Console.Error.WriteLine("[stop] cancelling — final ASR pending; press Ctrl-C again to bail.");
                cts.Cancel();
                service.Stop();
            }
            else
            {
                // second Ctrl-C during flush → exit immediately
                Environment.Exit(130);
            }
        };

        if (opts.MaxMinutes > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(opts.MaxMinutes), cts.Token);
                    Console.Error.WriteLine($"[stop] reached --max-minutes {opts.MaxMinutes}; final ASR pending.");
                    cts.Cancel();
                    service.Stop();
                }
                catch (OperationCanceledException) { /* user already cancelled */ }
            });
        }

        try { await service.RunAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        Console.WriteLine();
        Console.WriteLine("session saved:");
        Console.WriteLine($"  text  {Path.GetFullPath(service.TranscriptTextPath)}");
        Console.WriteLine($"  log   {Path.GetFullPath(service.TranscriptLogPath)}");

        // After the recording is fully wound down (regular ticks + final flush),
        // optionally generate a PF2e session summary from the transcript.
        if (cfg.EnableSessionSummary)
        {
            await RunSummaryAsync(cfg, transcripts).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> TranscribeFileAsync(string[] originalArgs)
    {
        // The first non-flag arg after --transcribe-file is the file path.
        string? audioPath = null;
        for (int i = 1; i < originalArgs.Length; i++)
        {
            if (!originalArgs[i].StartsWith("--") && originalArgs[i] != "-")
            {
                audioPath = originalArgs[i];
                break;
            }
        }
        if (string.IsNullOrEmpty(audioPath))
        {
            Console.Error.WriteLine("usage: --transcribe-file <path-to-audio>");
            return 1;
        }

        // Reuse the same config plumbing; file mode never touches the mic.
        var opts = ParseArgs(originalArgs);
        opts.NoLoopback = true;
        var cfg = LoadConfig(opts);

        using var client      = new TranscriptionClient(cfg);
        var transcripts       = new Transcripts(cfg.TranscriptDir, cfg.Model, cfg.ResponseFormat);
        using var transcriber = new FileTranscriber(cfg, client, transcripts);

        Console.WriteLine($"session      {transcripts.SessionId}");
        Console.WriteLine($"api url      {cfg.ApiUrl}");
        Console.WriteLine($"model        {cfg.Model}{(cfg.Language is { } lang ? $"  lang={lang}" : "")}");
        Console.WriteLine($"chunk        {cfg.ChunkSeconds}s  @ {cfg.SampleRate} Hz mono");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("[file] cancelling — in-flight chunk will finish; press Ctrl-C again to bail.");
            cts.Cancel();
        };

        int rc = await transcriber.RunAsync(audioPath, cts.Token).ConfigureAwait(false);
        if (rc != 0) return rc;

        if (cfg.EnableSessionSummary)
            await RunSummaryAsync(cfg, transcripts).ConfigureAwait(false);

        return 0;
    }

    private static async Task RunSummaryAsync(Config cfg, Transcripts transcripts)
    {
        Console.WriteLine();
        Console.WriteLine($"[summary] generating PF2e recap (model={cfg.SummaryModel})…");
        try
        {
            var text = File.ReadAllText(transcripts.TextPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("[summary] transcript is empty — skipping.");
                return;
            }
            using var summarizer = new SessionSummary(cfg);
            var template = summarizer.LoadTemplate();
            var recap = await summarizer.GenerateAsync(text, template, CancellationToken.None)
                                       .ConfigureAwait(false);
            var outPath = Path.Combine(
                Path.GetDirectoryName(transcripts.TextPath)!,
                $"summary_{transcripts.SessionId}.md");
            await File.WriteAllTextAsync(outPath, recap).ConfigureAwait(false);
            Console.WriteLine($"[summary] saved → {Path.GetFullPath(outPath)}");
        }
        catch (Exception ex)
        {
            // Summary is best-effort. We don't want a failed LLM call to mask the rest of the run.
            Console.Error.WriteLine($"[summary] FAILED — {ex.Message}");
            Console.Error.WriteLine("[summary] your transcripts are still saved; rerun --summarize from a session file when ready.");
        }
    }

    private static Options ParseArgs(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mic":             o.Mic             = Require(args, ++i, "--mic"); break;
                case "--loopback":        o.Loopback        = Require(args, ++i, "--loopback"); break;
                case "--no-loopback":     o.NoLoopback      = true; break;
                case "--chunk-seconds":   o.ChunkSeconds    = int.Parse(Require(args, ++i, "--chunk-seconds")); break;
                case "--language":        o.Language        = Require(args, ++i, "--language"); break;
                case "--response-format": o.ResponseFormat  = Require(args, ++i, "--response-format"); break;
                case "--api-url":         o.ApiUrl          = Require(args, ++i, "--api-url"); break;
                case "--max-minutes":     o.MaxMinutes      = int.Parse(Require(args, ++i, "--max-minutes")); break;
                case "--summarize":       o.Summarize       = true; break;
                case "--summary-model":   o.SummaryModel    = Require(args, ++i, "--summary-model"); break;
                case "--summary-api-url": o.SummaryApiUrl   = Require(args, ++i, "--summary-api-url"); break;
                case "--summary-template":o.SummaryTemplate = Require(args, ++i, "--summary-template"); break;
                default:
                    // ignore unknown flags so users can forward pass-through args later
                    break;
            }
        }
        return o;
    }

    private static string Require(string[] args, int i, string flag)
        => (i < args.Length) ? args[i] : throw new ArgumentException($"missing value after {flag}");

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Usage:  pathfinder-notes [--flag value ...]

        Modes (mutually exclusive — pick one or none for "run"):
          --list-devices                   list capture + render devices and exit
          --self-test                      5-second mic+loopback capture, dump self-test.wav, exit
          --parse <file>                   parse a sample verbose_json file and write a test transcript, exit
          --final-flush-test [N]           run capture loop for N seconds then cancel; verifies the final-flush upload path (no API call), exit
          --summarize-test <transcript>    build + write the prompt that would be sent to the LLM (no API call), exit
          --summarize-from <transcript>    read an existing transcript and write the recap next to it (uses API), exit
          --transcribe-file <path>        decode an audio file (WAV/MP3/M4A/AAC/...),
                                            chunk it, upload to ASR, write transcript
                                            (and optionally run --summarize), exit
          --version                        print version and exit
          -h, --help                       show this help and exit

        Run flags:
          --mic "name"                    capture mic (default: system default)
          --loopback "name"               capture loopback of "name" output device
                                            (default: system default render device)
          --no-loopback                   disable system audio capture (mic only)
          --chunk-seconds N               upload every N seconds (default 30, max 500)
          --language "en"                 BCP-47 tag for ASR; omit for mixed/auto
          --response-format json|verbose_json   default: json
          --api-url URL                   override MiniMax endpoint
          --max-minutes N                 auto-stop after N minutes (0 = off)
          --summarize                     generate a PF2e session summary on shutdown
                                            (requires ENABLE_SESSION_SUMMARY or this flag)
          --summary-model NAME            LLM model for recap (default MiniMax-M3)
          --summary-api-url URL           override chat endpoint
                                            (default https://api.minimax.io/anthropic/v1/messages)
          --summary-template PATH         override template markdown
                                            (default: pf2e-session-template.md beside binary)

        Environment (.env file or process env):
          MINIMAX_API_KEY                  required
          MINIMAX_API_URL                  default https://api.minimax.io/v1/speech_to_text
          ASR_MODEL                        default asr-1.0
          ASR_LANGUAGE                     (optional)
          ASR_RESPONSE_FORMAT              default verbose_json
          CHUNK_SECONDS                    default 30
          TRANSCRIPT_DIR                   default transcripts
          ENABLE_SESSION_SUMMARY          "true" to opt in to --summarize
          SUMMARY_MODEL                    default MiniMax-M3
          SUMMARY_API_URL                  default https://api.minimax.io/anthropic/v1/messages
          SUMMARY_MAX_TOKENS               default 4096
          SUMMARY_TEMPLATE_PATH            optional override path to template
        """);
    }

    private sealed class Options
    {
        public string? Mic;
        public string? Loopback;
        public bool NoLoopback;
        public int ChunkSeconds;
        public string? Language;
        public string? ResponseFormat;
        public string? ApiUrl;
        public int MaxMinutes = 0;
        public bool Summarize;
        public string? SummaryModel;
        public string? SummaryApiUrl;
        public string? SummaryTemplate;
    }
}
