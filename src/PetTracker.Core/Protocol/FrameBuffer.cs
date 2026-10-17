namespace PetTracker.Core.Protocol;

/// <summary>
/// Rolling buffer of recent frames for the unknown-version fallback: the last <see cref="Window"/> of traffic (or everything since
/// the last <see cref="Clear"/>, whichever is smaller), never more than <see cref="MaxBytes"/> of bodies. Thread-safe: the pipeline
/// thread adds, the UI thread takes snapshots. Oversized frames are not kept (no pet message is anywhere near that big).
/// </summary>
public sealed class FrameBuffer(TimeSpan? window = null, long maxBytes = 16 * 1024 * 1024, int maxFrameBytes = 64 * 1024)
{
    private readonly object _gate = new();
    private readonly Queue<ObservedFrame> _frames = new();
    private long _bytes;

    public TimeSpan Window { get; } = window ?? TimeSpan.FromMinutes(5);
    public long MaxBytes { get; } = maxBytes;

    public int Count { get { lock (_gate) return _frames.Count; } }
    public long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Adds frames that arrived at <paramref name="ticks"/>; frames for which <paramref name="skip"/> is true (heartbeats) are not kept.</summary>
    public void Add(long ticks, IEnumerable<Frame> frames, Func<Frame, bool>? skip = null)
    {
        lock (_gate)
        {
            foreach (var frame in frames)
            {
                if (frame.Body.Length > maxFrameBytes || skip?.Invoke(frame) == true)
                    continue;
                _frames.Enqueue(new ObservedFrame(ticks, frame));
                _bytes += frame.Body.Length;
            }
            Trim(ticks);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _bytes = 0;
        }
    }

    public List<ObservedFrame> Snapshot()
    {
        lock (_gate)
            return [.. _frames];
    }

    private void Trim(long now)
    {
        var oldest = now - Window.Ticks;
        while (_frames.Count > 0 && (_frames.Peek().Ticks < oldest || _bytes > MaxBytes))
            _bytes -= _frames.Dequeue().Frame.Body.Length;
    }
}
