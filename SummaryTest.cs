using System;
using System.IO;
using System.Threading.Tasks;
using Pathfinder.Notes;

namespace Pathfinder.Notes;

/// <summary>
/// `--summarize-test &lt;transcript.txt&gt;` — builds the prompt that would be
/// sent to the LLM and writes it to disk so a human (or a non-network code
/// reviewer) can verify the prompt shape. Does NOT call the LLM, so no API
/// key is required.
///
/// Also locates the template, reports which file it picked, and prints the
/// first 200 chars of the rendered prompt for sanity.
/// </summary>
public static class SummaryTest
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: --summarize-test <transcript.txt>");
            return 1;
        }
        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"file not found: {path}");
            return 1;
        }

        var transcript = await File.ReadAllTextAsync(path);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            Console.Error.WriteLine("transcript file is empty");
            return 2;
        }

        var cfg = new Config(
            ApiKey: "(stub)",
            ApiUrl: "(stub)",
            Model: "asr-1.0",
            Language: null,
            ResponseFormat: "verbose_json",
            ChunkSeconds: 30,
            TranscriptDir: "transcripts",
            MicDeviceName: null,
            LoopbackDeviceName: null,
            SampleRate: Config.AsrSampleRate,
            EnableSessionSummary: true,
            SummaryModel: "MiniMax-M3",
            SummaryApiUrl: "https://api.minimax.io/anthropic/v1/messages",
            SummaryMaxTokens: 4096,
            SummaryTemplatePath: "");

        using var summarizer = new SessionSummary(cfg);

        var templatePath = ResolveTemplatePath(cfg.SummaryTemplatePath);
        Console.WriteLine($"template:    {(templatePath ?? "(fallback embedded)")}");
        Console.WriteLine($"transcript:  {Path.GetFullPath(path)}  ({transcript.Length:N0} chars)");
        var template = summarizer.LoadTemplate();
        Console.WriteLine($"template size: {template.Length:N0} chars");

        // Reconstruct the user prompt the way GenerateAsync does, just without the API call.
        var userPrompt = BuildPromptPublic(template, transcript);
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "transcripts", "_summarize_test");
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, $"prompt_{DateTime.Now:HHmmss}.txt");
        await File.WriteAllTextAsync(outFile, userPrompt);

        Console.WriteLine();
        Console.WriteLine($"wrote prompt → {outFile}");
        Console.WriteLine();
        Console.WriteLine("--- first 600 chars of the prompt ---");
        var preview = userPrompt.Length <= 600 ? userPrompt : userPrompt[..600] + "…";
        Console.WriteLine(preview);
        return 0;
    }

    private static string? ResolveTemplatePath(string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        var installed = Path.Combine(AppContext.BaseDirectory, "pf2e-session-template.md");
        if (File.Exists(installed)) return installed;
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "pf2e-session-template.md");
        if (File.Exists(cwd)) return cwd;
        return null;
    }

    // Mirror SessionSummary.BuildPrompt's structure so we can preview the prompt without
    // touching the real network path. If BuildPrompt's body ever changes, keep this in sync.
    private static string BuildPromptPublic(string template, string transcript) =>
        $"""
        You are an experienced Pathfinder 2e game master writing a session recap.

        Below are two blocks:
        1. A markdown template that defines the structure of the recap.
        2. A speaker-labeled transcript captured live during the session (each line is prefixed with [S1] / [S2] / etc.).

        Your job: produce the FULLY-FILLED recap. **No blanks are acceptable.** Every section header, every table cell, every list item must contain real content. Where the transcript genuinely doesn't support a value, write a brief explicit note ("unclear", "Not mentioned in this session.", "None.", "n/a", etc.) so the human reading the recap never sees an empty cell to fill in.

        Hard rules:
        - **Every heading in the template must appear in your output**, in the same order, at the same depth, even if its body is just one short sentence like "No OOC notes recorded this session." — section headers are never skipped.
        - Preserve the template's markdown structure exactly: heading levels, table layout, list ordering, table column count. Fill every cell — never output an empty "|" column.
        - Strip any explanatory preamble the template contains above the first "---" (those are instructions to you, not content for the recap). Start the recap with the first heading or first "---"-delimited section of the template.
        - Use ONLY information present in the transcript. If a creature's level, an XP award, a gold count, or a character level is never stated, write "unclear" (table cell) or "Not mentioned in this session." (sentence). Do not invent.
        - The "Summary" section should be 2–4 paragraphs of past-tense narrative capturing the arc and mood.
        - The "Encounters" section: one markdown-table row per encounter.
        - The "Story Beats" section: bullets, chronological, plot-relevant only.
        - The "Treasure" section lists anything explicitly mentioned as changing hands (currency, items, favors).
        - The "NPCs" table: name, role, disposition, and a short First Action or Quote.
        - "Notes for the GM" and "Out-of-Game / Real Life": if the transcript has nothing for them, write a single sentence explaining so.
        - Output ONLY the filled-in recap markdown — no commentary, no "Here is your recap:" preamble, no fenced ```markdown``` wrap.

        # Template

        {template}

        # Transcript

        {transcript}
        """;
}
