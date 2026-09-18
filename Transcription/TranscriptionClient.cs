using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pathfinder.Notes.Transcription;

/// <summary>
/// POSTs a multipart audio chunk to the MiniMax ASR endpoint and returns the
/// parsed <see cref="TranscriptionResult"/>. Supports any response_format;
/// segments only appear with <c>verbose_json</c> (or srt/vtt).
/// </summary>
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
