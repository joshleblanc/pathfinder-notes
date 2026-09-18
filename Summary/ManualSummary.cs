using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pathfinder.Notes.Summary;

/// <summary>
/// <c>--summarize-from &lt;transcript.txt&gt;</c> — read an existing transcript
/// file, call the MiniMax chat endpoint, and write the recap next to it as
/// <c>summary_&lt;session-id&gt;.md</c>. The session id is derived from the
/// transcript filename (<c>transcript_2026-09-16_11-50-04.txt</c> →
/// <c>summary_2026-09-16_11-50-04.md</c>).
///
/// Useful for retro-summarizing sessions recorded without
/// <c>--summarize</c>, re-running with a different model/template, or running
/// offline on a transcript obtained elsewhere.
/// </summary>
public static class ManualSummary
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: --summarize-from <transcript.txt> [--summary-model X] [--summary-api-url Y] [--summary-template Z]");
            return 1;
        }

        var transcriptPath = args[0];
        if (!File.Exists(transcriptPath))
        {
            Console.Error.WriteLine($"file not found: {transcriptPath}");
            return 1;
        }

        var transcript = await File.ReadAllTextAsync(transcriptPath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            Console.Error.WriteLine("transcript is empty; nothing to summarize.");
            return 2;
        }

        var sessionId = ExtractSessionId(transcriptPath);
        if (string.IsNullOrEmpty(sessionId))
            Console.WriteLine("(note) transcript filename doesn't match `transcript_<session>.txt`; using prefix-derived id.");

        // CLI overrides for the LLM call
        string? summaryModel    = null;
        string? summaryApiUrl   = null;
        string? summaryTemplate = null;
        int     summaryMaxTokens = -1;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--summary-model":    summaryModel = ManualSummary.Require(args, ++i, "--summary-model"); break;
                case "--summary-api-url":  summaryApiUrl = ManualSummary.Require(args, ++i, "--summary-api-url"); break;
                case "--summary-template": summaryTemplate = ManualSummary.Require(args, ++i, "--summary-template"); break;
                case "--summary-max-tokens":
                    summaryMaxTokens = int.Parse(ManualSummary.Require(args, ++i, "--summary-max-tokens"));
                    break;
                default:
                    Console.Error.WriteLine($"warning: ignoring unknown flag {args[i]}");
                    break;
            }
        }

        var cfg = BuildConfig(summaryModel, summaryApiUrl, summaryTemplate, summaryMaxTokens);
        if (string.IsNullOrEmpty(cfg.ApiKey))
        {
            Console.Error.WriteLine("MINIMAX_API_KEY is not set; add it to .env or your environment.");
            return 3;
        }

        Console.WriteLine($"transcript:  {Path.GetFullPath(transcriptPath)}  ({transcript.Length:N0} chars)");
        Console.WriteLine($"session id:  {sessionId ?? "(derived from file name)"}");
        Console.WriteLine($"model:       {cfg.SummaryModel}");
        Console.WriteLine($"api url:     {cfg.SummaryApiUrl}");
        Console.WriteLine();
        Console.WriteLine("[summary] generating PF2e recap — this will take a moment…");

        try
        {
            using var summarizer = new SessionSummary(cfg);
            var template = summarizer.LoadTemplate();
            var recap = await summarizer.GenerateAsync(transcript, template, CancellationToken.None)
                                       .ConfigureAwait(false);

            var dir  = Path.GetDirectoryName(transcriptPath)!;
            var outPath = string.IsNullOrEmpty(sessionId)
                ? Path.Combine(dir, $"summary_{Path.GetFileNameWithoutExtension(transcriptPath)}.md")
                : Path.Combine(dir, $"summary_{sessionId}.md");

            await File.WriteAllTextAsync(outPath, recap).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"[summary] saved → {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[summary] FAILED — {ex.Message}");
            return 4;
        }
    }

    /// <summary>Pull the session id out of `transcript_xxx.txt` → `xxx`.</summary>
    private static string? ExtractSessionId(string transcriptPath)
    {
        var name = Path.GetFileNameWithoutExtension(transcriptPath);
        const string prefix = "transcript_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return name.Substring(prefix.Length);
        return null;
    }

    private static string Require(string[] args, int i, string flag) =>
        (i < args.Length) ? args[i] : throw new ArgumentException($"missing value after {flag}");

    /// <summary>
    /// Build a Config focused on the summary call. Loads .env from the binary
    /// directory or cwd so MINIMAX_API_KEY / SUMMARY_* env vars resolve the same
    /// way they do at run-time.
    /// </summary>
    private static Config BuildConfig(string? model, string? apiUrl, string? template, int maxTokens)
    {
        var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
        if (!File.Exists(envPath))
        {
            var alt = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            if (File.Exists(alt)) envPath = alt;
        }
        var env = EnvConfig.LoadDotEnv(envPath);

        // CLI overrides bump the env values
        if (!string.IsNullOrEmpty(model))   env["SUMMARY_MODEL"]       = model;
        if (!string.IsNullOrEmpty(apiUrl))  env["SUMMARY_API_URL"]     = apiUrl;
        if (!string.IsNullOrEmpty(template)) env["SUMMARY_TEMPLATE_PATH"] = template;
        if (maxTokens > 0) env["SUMMARY_MAX_TOKENS"] = maxTokens.ToString();

        string Get(string key, string fallback) =>
            env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        return new Config(
            ApiKey:             Get("MINIMAX_API_KEY", ""),
            ApiUrl:             "(unused)",
            Model:              "asr-1.0",
            Language:           null,
            ResponseFormat:     "verbose_json",
            ChunkSeconds:       30,
            TranscriptDir:      Path.GetDirectoryName(envPath)!,
            MicDeviceName:      null,
            LoopbackDeviceName: null,
            SampleRate:         Config.AsrSampleRate,
            EnableSessionSummary: false,
            SummaryModel:       Get("SUMMARY_MODEL", "MiniMax-M3"),
            SummaryApiUrl:      Get("SUMMARY_API_URL", "https://api.minimax.io/anthropic/v1/messages"),
            SummaryMaxTokens:   int.TryParse(Get("SUMMARY_MAX_TOKENS", "4096"), out var mt) ? mt : 4096,
            SummaryTemplatePath:Get("SUMMARY_TEMPLATE_PATH", ""));
    }
}
