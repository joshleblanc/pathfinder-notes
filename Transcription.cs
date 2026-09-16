using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pathfinder.Notes;

// ────────────────────────────────────────────────────────────────────────────
// Data shapes from the API's verbose_json response:
//
//   text       = concatenation of segments[] in time order
//   duration   = input audio length, seconds
//   n_speakers = integer, present only for verbose_json
//   segments[] = [{ id, start, end, speaker, text }]
//   trace_id   = useful for diagnostics
// ────────────────────────────────────────────────────────────────────────────
public sealed record TranscriptSegment(
    int Id,
    double Start,
    double End,
    string Speaker,
    string Text);

public sealed record TranscriptionResult(
    string FullText,
    double Duration,
    int SpeakerCount,
    IReadOnlyList<TranscriptSegment> Segments,
    string? TraceId);

// ────────────────────────────────────────────────────────────────────────────
// TranscriptionClient — POSTs a multipart audio chunk to MiniMax ASR and
// returns the parsed response. Supports any response_format; segments only
// appear when verbose_json (or srt/vtt) is requested.
// ────────────────────────────────────────────────────────────────────────────
public sealed class TranscriptionClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Config _config;

    public TranscriptionClient(Config config)
    {
        _config = config;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);
    }

    public void Dispose() => _http.Dispose();

    public async Task<TranscriptionResult> TranscribeAsync(
        byte[] wavBytes,
        TimeSpan? audioDuration,
        CancellationToken ct)
    {
        using var form = new MultipartFormDataContent("--pathfinder-notes-boundary");

        form.Add(new StringContent(_config.Model),          "model");
        form.Add(new StringContent(_config.ResponseFormat), "response_format");

        // Optional: per-segment timestamps with character/word granularity.
        // Only meaningful with verbose_json / srt / vtt — ignored for json.
        if (string.Equals(_config.ResponseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
            form.Add(new StringContent("sentence"), "timestamp_level");

        var audio = new ByteArrayContent(wavBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "chunk.wav");

        using var req = new HttpRequestMessage(HttpMethod.Post, _config.ApiUrl) { Content = form };
        if (!string.IsNullOrEmpty(_config.Language))
            req.Headers.Add("language", _config.Language);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ASR request failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

        return Parse(body);
    }

    private static TranscriptionResult Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string fullText = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
        double duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble() : 0d;
        int speakerCount = root.TryGetProperty("n_speakers", out var nSpk) && nSpk.ValueKind == JsonValueKind.Number
            ? nSpk.GetInt32() : 0;
        string? traceId = root.TryGetProperty("trace_id", out var tr) ? tr.GetString() : null;

        var segments = new List<TranscriptSegment>();
        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in segs.EnumerateArray())
            {
                segments.Add(new TranscriptSegment(
                    Id:      s.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                    Start:   s.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetDouble() : 0d,
                    End:     s.TryGetProperty("end", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetDouble() : 0d,
                    Speaker: s.TryGetProperty("speaker", out var sp) ? (sp.GetString() ?? "?") : "?",
                    Text:    s.TryGetProperty("text", out var tx) ? (tx.GetString() ?? "") : ""));
            }
        }

        return new TranscriptionResult(fullText, duration, speakerCount, segments, traceId);
    }

    /// <summary>Static test helper: parse a sample body without hitting the API.</summary>
    internal static TranscriptionResult ParseSample(string body) => Parse(body);
}

// ────────────────────────────────────────────────────────────────────────────
// Transcripts — rolling per-session files:
//
//   transcript_<session>.txt  clean text with speaker labels:
//                              [S1] Hello everyone.
//                              [S2] Let me check the question.
//
//   transcript_<session>.log  verbose log with timestamps + per-segment timing
// ────────────────────────────────────────────────────────────────────────────
public sealed class Transcripts
{
    private readonly string _txtPath;
    private readonly string _logPath;
    private readonly object _lock = new();
    private readonly string _model;
    private readonly string _format;

    public string SessionId { get; }
    public string TextPath => _txtPath;
    public string LogPath => _logPath;

    public Transcripts(string dir, string model, string format)
    {
        _model  = model;
        _format = format;

        SessionId = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        Directory.CreateDirectory(dir);
        _txtPath = Path.Combine(dir, $"transcript_{SessionId}.txt");
        _logPath = Path.Combine(dir, $"transcript_{SessionId}.log");

        File.WriteAllText(_txtPath,
            $"# Live transcription session started {SessionId}\n" +
            $"# MiniMax ASR model={_model} response_format={_format}\n" +
            $"# speakers {SpeakerNote(_format)}\n\n");

        File.WriteAllText(_logPath,
            $"# log {SessionId}\n");
    }

    private static string SpeakerNote(string format) =>
        string.Equals(format, "verbose_json", StringComparison.OrdinalIgnoreCase)
            ? "tracked per segment"
            : "not tracked (response_format=" + format + "; pass --response-format verbose_json to enable)";

    public void Append(int chunkIdx, double seconds, TranscriptionResult result)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var turns = GroupIntoTurns(result.Segments);

        lock (_lock)
        {
            // .txt — clean, speaker-labeled, one turn per line.
            if (turns.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var t in turns)
                {
                    if (t.Text.Length == 0) continue;
                    sb.Append('[').Append(t.Speaker).Append("] ").AppendLine(t.Text);
                }
                if (sb.Length > 0)
                    File.AppendAllText(_txtPath, sb.ToString() + "\n");
            }

            // .log — chunk metadata + per-turn start time + text.
            var meta = $"[{stamp}] chunk {chunkIdx} ({seconds:F1}s, {result.SpeakerCount} speaker{(result.SpeakerCount == 1 ? "" : "s")}, trace={result.TraceId ?? "-"})\n";
            if (turns.Count == 0)
            {
                meta += "  (no transcribed segments)\n";
            }
            else
            {
                foreach (var t in turns)
                {
                    if (t.Text.Length == 0) continue;
                    meta += $"  @{t.Start,5:F2}s–@{t.End,5:F2}s [{t.Speaker}] {t.Text}\n";
                }
            }
            meta += "\n";
            File.AppendAllText(_logPath, meta);
        }
    }

    /// <summary>
    /// Collapse consecutive same-speaker segments into one turn, splitting on:
    ///   • speaker change, or
    ///   • time gap of more than <see cref="TurnGapSeconds"/>.
    /// Even when the API is asked for sentence-level timestamps it occasionally
    /// returns short fragments; this keeps the transcript readable.
    /// </summary>
    private static List<TranscriptTurn> GroupIntoTurns(IReadOnlyList<TranscriptSegment> segs)
    {
        const double TurnGapSeconds = 3.0;

        var turns = new List<TranscriptTurn>();
        string? currentSpeaker = null;
        var currentText = new System.Text.StringBuilder();
        double currentStart = 0;
        double lastEnd = 0;

        foreach (var s in segs)
        {
            var txt = CleanOneLine(s.Text);
            if (txt.Length == 0) continue;

            if (currentSpeaker == null)
            {
                currentSpeaker = s.Speaker;
                currentStart   = s.Start;
                currentText.Clear();
                currentText.Append(txt);
                lastEnd = s.End;
                continue;
            }

            var speakerChanged = s.Speaker != currentSpeaker;
            var bigGap         = (s.Start - lastEnd) > TurnGapSeconds;

            if (speakerChanged || bigGap)
            {
                turns.Add(new TranscriptTurn(currentSpeaker, currentStart, lastEnd, currentText.ToString()));
                currentSpeaker = s.Speaker;
                currentStart   = s.Start;
                currentText.Clear();
                currentText.Append(txt);
            }
            else
            {
                // Same turn — concatenate, but mind word boundaries. The first
                // word has no preceding space; subsequent words do, unless the
                // previous text ends in punctuation that should "kiss" the next.
                var hasTrailingSpace = currentText.Length > 0 && currentText[currentText.Length - 1] == ' ';
                var startsWithPunct  = txt.Length > 0 && IsPunctuation(txt[0]);
                if (!hasTrailingSpace && !startsWithPunct)
                    currentText.Append(' ');
                currentText.Append(txt);
            }
            lastEnd = s.End;
        }

        if (currentSpeaker != null && currentText.Length > 0)
            turns.Add(new TranscriptTurn(currentSpeaker, currentStart, lastEnd, currentText.ToString().ToString().TrimEnd()));

        return turns;
    }

    private static bool IsPunctuation(char c) => c is ',' or '.' or '?' or '!' or ':' or ';' or ')' or ']';

    private static string CleanOneLine(string s) =>
        s.Replace("\r", " ").Replace("\n", " ").Trim();

    private sealed record TranscriptTurn(string Speaker, double Start, double End, string Text);
}
