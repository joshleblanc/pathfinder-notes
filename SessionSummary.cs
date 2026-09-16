using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pathfinder.Notes;

// ────────────────────────────────────────────────────────────────────────────
// SessionSummary — reads the rolling transcript + a user-editable PF2e template,
// then asks MiniMax's chat-completions endpoint to fill in the template.
//
// Output:  transcripts/summary_<session-id>.md
//
// Default API URL matches the Anthropic-Messages endpoint the user already
// has configured for Claude Code:
//     https://api.minimax.io/anthropic/v1/messages
// Override via env SUMMARY_API_URL or the --summary-api-url flag.
// ────────────────────────────────────────────────────────────────────────────
public sealed class SessionSummary : IDisposable
{
    private readonly HttpClient _http;
    private readonly Config _config;

    public SessionSummary(Config config)
    {
        _config = config;
        _http = new HttpClient
        {
            // Recaps can take a while — large prompt + ~2k token output.
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        // Anthropic-Messages endpoint identifies the API version via header.
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Read the user's PF2e template. Falls back to a bundled default.</summary>
    public string LoadTemplate()
    {
        // 1) explicit path from config (CLI flag or env)
        var explicitPath = _config.SummaryTemplatePath;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (File.Exists(explicitPath)) return File.ReadAllText(explicitPath);
            throw new FileNotFoundException($"Summary template not found: {explicitPath}");
        }

        // 2) next to the binary (cleanly installed)
        var installedPath = Path.Combine(AppContext.BaseDirectory, "pf2e-session-template.md");
        if (File.Exists(installedPath)) return File.ReadAllText(installedPath);

        // 3) current working directory (developer / dotnet run)
        var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "pf2e-session-template.md");
        if (File.Exists(cwdPath)) return File.ReadAllText(cwdPath);

        // 4) bundled fallback — kept short, still useful.
        return DefaultTemplate;
    }

    public async Task<string> GenerateAsync(
        string transcriptText,
        string template,
        CancellationToken ct)
    {
        var userPrompt = BuildPrompt(transcriptText, template);
        var body = SerializeRequest(_config.SummaryModel, _config.SummaryMaxTokens, userPrompt);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_config.SummaryApiUrl, content, ct);
        var responseText = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Summary API failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n{responseText}");

        return ExtractAssistantMessage(responseText);
    }

    private string BuildPrompt(string transcript, string template) =>
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
        - The "Encounters" section: one markdown-table row per encounter. Columns: encounter name (or descriptive label), creatures (a short list — "1 hobgoblin soldier, 2 skeletons"), level(s), XP awarded (or "unclear"), outcome (short — "won, no PC down" / "negotiated" / "fled, will return" etc.).
        - The "Story Beats" section: bullets, chronological, plot-relevant only. Skip shopping, banter, recap-of-the-recap. If there are no plot events, write "- None in this session."
        - The "Treasure" section lists anything explicitly mentioned as changing hands (currency, items, favors). Do not estimate prices unless they are said aloud. If nothing is mentioned, write "Nothing changed hands this session." for that subsection.
        - The "NPCs" table: name, role, disposition (e.g., "hostile", "wary ally", "neutral merchant"), and a short First Action or Quote (one sentence from the transcript). If a field is unknown, fill the cell with "unclear" — never with "TODO" or blank.
        - The "Locations Visited" list is bulleted — one entry per distinct location. If only one place features, that's fine. If the party never left a single spot, write "- The entire session took place in: <location>".
        - "Notes for the GM" and "Out-of-Game / Real Life": if the transcript has nothing for them, write a single sentence explaining so (e.g., "No OOC notes recorded this session.") — do not leave the section heading with nothing under it.
        - The "**Date:**" line: if a session date is not stated in the transcript, write "Date unclear from the recording." for that line — do not invent.
        - The "**Audio duration:**" line: I will fill this in from the recording metadata — leave that exact line verbatim, do not edit it.
        - Output ONLY the filled-in recap markdown — no commentary, no "Here is your recap:" preamble, no fenced ```markdown``` wrap.

        # Template

        {template}

        # Transcript

        {transcript}
        """;

    private static string SerializeRequest(string model, int maxTokens, string userPrompt)
    {
        // Anthropic Messages API request schema.
        var req = new
        {
            model,
            max_tokens = maxTokens,
            system = "You are an expert Pathfinder 2e game master.",
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };
        return JsonSerializer.Serialize(req, JsonOpts);
    }

    private static string ExtractAssistantMessage(string body)
    {
        // Anthropic Messages response: { content: [ { type:"text", text:"..." }, ... ] }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
            }
            if (sb.Length > 0) return sb.ToString();
        }

        // Fall back to OpenAI-compatible shape { choices: [ { message: { content }} ] }
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var c))
                {
                    return c.GetString() ?? "";
                }
            }
        }

        // Last resort: dump the body for debugging.
        return body;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // Bundled fallback — used only when no template file is found anywhere on disk.
    private const string DefaultTemplate = """
        # Pathfinder 2e Session Recap

        ## At a Glance

        ## Summary

        ## Encounters

        | # | Encounter | Creatures | Level | XP | Outcome |
        |---|-----------|-----------|-------|----|---------|

        ## Treasure

        ### Currency
        ### Items
        ### Other

        ## Story Beats

        ## NPCs

        | Name | Role | Disposition |

        ## Locations Visited

        ## Cliffhanger / Next Session

        ## XP & Advancement

        ## Notes for the GM

        ## Out-of-Game / Real Life
        """;
}
