using System;
using System.IO;

namespace Pathfinder.Notes.Audio;

/// <summary>float[] → 16-bit PCM mono WAV bytes.</summary>
public static class WavEncoder
{
    public static byte[] Encode(ReadOnlySpan<float> monoSamples, int sampleRate = Config.AsrSampleRate)
    {
        // Float -> PCM16
        var pcm = new byte[monoSamples.Length * 2];
        for (int i = 0; i < monoSamples.Length; i++)
        {
            float s = Math.Clamp(monoSamples[i], -1f, 1f);
            short v = (short)(s * 32767f);
            pcm[i * 2]     = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return BuildWavBytes(pcm, sampleRate, channels: 1, bitsPerSample: 16);
    }

    private static byte[] BuildWavBytes(byte[] pcm16, int sampleRate, int channels, int bitsPerSample)
    {
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataLen = pcm16.Length;
        int riffLen = 36 + dataLen;

        using var ms = new MemoryStream(44 + dataLen);
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffLen);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                         // PCM fmt chunk size
        bw.Write((short)1);                   // PCM format
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        bw.Write(pcm16);
        return ms.ToArray();
    }
}
