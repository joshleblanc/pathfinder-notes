using System;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// Downmix multichannel to mono, then linear-interp resample to 16 kHz.
/// Good enough for speech ASR; not a textbook-quality resampler.
/// </summary>
public static class Resampler
{
    public static void ToMono(Span<float> dst, ReadOnlySpan<float> src, int channels)
    {
        if (channels == 1)
        {
            src.CopyTo(dst);
            return;
        }
        int frames = src.Length / channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += src[i * channels + c];
            dst[i] = sum / channels;
        }
    }

    public static float[] DownsampleTo16kMono(ReadOnlySpan<float> mono, int srcRate, int targetRate = Config.AsrSampleRate)
    {
        if (srcRate == targetRate) return mono.ToArray();

        double step = (double)srcRate / targetRate;
        int outLen = (int)(mono.Length / step);
        if (outLen <= 0) return Array.Empty<float>();

        var dst = new float[outLen];
        double pos = 0;
        for (int i = 0; i < outLen; i++)
        {
            int idx = (int)pos;
            float frac = (float)(pos - idx);
            float a = idx < mono.Length ? mono[idx] : 0f;
            float b = (idx + 1) < mono.Length ? mono[idx + 1] : 0f;
            dst[i] = a * (1f - frac) + b * frac;
            pos += step;
        }
        return dst;
    }
}
