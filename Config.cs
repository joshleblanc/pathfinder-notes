using System;

namespace Pathfinder.Notes;

/// <summary>Runtime configuration, populated from .env + process env + CLI flags.</summary>
public sealed record Config(
    string ApiKey,
    string ApiUrl,
    string Model,
    string? Language,
    string ResponseFormat,
    int ChunkSeconds,
    string TranscriptDir,
    string? MicDeviceName,
    string? LoopbackDeviceName,
    int SampleRate,                      // output sample rate for ASR (16kHz)
    bool EnableSessionSummary,           // generate PF2e summary on shutdown
    string SummaryModel,                 // LLM model for summary generation
    string SummaryApiUrl,                // MiniMax chat endpoint
    int SummaryMaxTokens,                // LLM max_tokens for the recap
    string SummaryTemplatePath           // override location of pf2e-session-template.md
)
{
    public const int AsrSampleRate = 16_000;
}

public static class EnvConfig
{
    /// <summary>
    /// Tiny .env loader. Reads KEY=VALUE pairs from <paramref name="path"/>,
    /// strips optional surrounding quotes, and also exports each value into the
    /// process environment so downstream code can use Environment.GetEnvironmentVariable.
    /// Returns the in-memory dictionary view of all loaded vars.
    /// </summary>
    public static Dictionary<string, string> LoadDotEnv(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val[1..^1];

            d[key] = val;
            Environment.SetEnvironmentVariable(key, val);
        }
        return d;
    }

    public static string Required(string key, Dictionary<string, string> env)
    {
        if (env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        var fromProc = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromProc)) return fromProc;
        throw new InvalidOperationException(
            $"Required environment variable {key} is not set. " +
            $"Add it to .env or set it in your shell.");
    }

    public static string? Optional(string key, Dictionary<string, string> env, string? fallback = null)
    {
        if (env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        var fromProc = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(fromProc) ? fallback : fromProc;
    }
}
