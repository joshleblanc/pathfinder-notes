using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pathfinder.Notes.Transcription;

/// <summary>
/// Rolling per-session transcript files for one recording session:
///   <c>transcript_&lt;session&gt;.txt</c> — clean, speaker-labeled turns.
///   <c>transcript_&lt;session&gt;.log</c> — verbose chunk + per-segment log.
/// </summary>
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
                var sb = new StringBuilder();
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
        var currentText = new StringBuilder();
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
            turns.Add(new TranscriptTurn(currentSpeaker, currentStart, lastEnd, currentText.ToString().TrimEnd()));

        return turns;
    }

    private static bool IsPunctuation(char c) => c is ',' or '.' or '?' or '!' or ':' or ';' or ')' or ']';

    private static string CleanOneLine(string s) =>
        s.Replace("\r", " ").Replace("\n", " ").Trim();

    private sealed record TranscriptTurn(string Speaker, double Start, double End, string Text);
}
