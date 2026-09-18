using System.Collections.Generic;

namespace Pathfinder.Notes.Transcription;

/// <summary>One ASR segment: a single speaker utterance within a chunk.</summary>
public sealed record TranscriptSegment(
    int Id,
    double Start,
    double End,
    string Speaker,
    string Text);

/// <summary>
/// Parsed ASR response for one audio chunk. Shape mirrors the API's
/// <c>verbose_json</c> format (MiniMax speech-to-text); other formats return
/// only <see cref="FullText"/> and <see cref="Duration"/>.
/// </summary>
public sealed record TranscriptionResult(
    string FullText,
    double Duration,
    int SpeakerCount,
    IReadOnlyList<TranscriptSegment> Segments,
    string? TraceId);
