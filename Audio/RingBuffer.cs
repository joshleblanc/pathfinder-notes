using System;

namespace Pathfinder.Notes.Audio;

/// <summary>
/// Fixed-capacity, thread-safe circular buffer of mono float samples.
/// Always-overwrites on Write, so Snapshot() returns "the most recent N samples".
/// </summary>
public sealed class RingBuffer
{
    private readonly float[] _buf;
    private readonly object _lock = new();
    private int _writeIdx;
    private long _totalWritten;

    public RingBuffer(int capacity) { _buf = new float[capacity]; }
    public int Capacity => _buf.Length;
    public long TotalWritten { get { lock (_lock) { return _totalWritten; } } }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            int n = samples.Length;
            if (n >= _buf.Length)
            {
                samples = samples[^_buf.Length..];
                n = _buf.Length;
            }

            int end = _writeIdx + n;
            if (end <= _buf.Length)
            {
                samples.CopyTo(_buf.AsSpan(_writeIdx, n));
            }
            else
            {
                int k = _buf.Length - _writeIdx;
                samples.Slice(0, k).CopyTo(_buf.AsSpan(_writeIdx, k));
                samples.Slice(k, n - k).CopyTo(_buf.AsSpan(0, n - k));
            }
            _writeIdx = (_writeIdx + n) % _buf.Length;
            _totalWritten += n;
        }
    }

    /// <summary>Time-ordered copy of the most-recent samples (length == Capacity).</summary>
    public float[] Snapshot()
    {
        lock (_lock)
        {
            var copy = new float[_buf.Length];
            int tailLen = _buf.Length - _writeIdx;
            Array.Copy(_buf, _writeIdx, copy, 0, tailLen);
            Array.Copy(_buf, 0, copy, tailLen, _writeIdx);
            return copy;
        }
    }
}
