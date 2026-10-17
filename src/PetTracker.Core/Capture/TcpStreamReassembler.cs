namespace PetTracker.Core.Capture;

public readonly record struct StreamResult(byte[]? Data, bool Gap);

/// <summary>
/// Reassembles one direction of one TCP connection. Orders by sequence number (wraparound safe), drops duplicates
/// and retransmits, buffers out-of-order segments, and reports a gap when the hole never fills.
/// </summary>
public sealed class TcpStreamReassembler(int maxPendingBytes = 256 * 1024, int maxPendingSegments = 128)
{
    private readonly SortedDictionary<long, byte[]> _pending = [];
    private long _next;
    private int _pendingBytes;

    public bool Started { get; private set; }
    public long Duplicates { get; private set; }
    public long OutOfOrder { get; private set; }
    public long Gaps { get; private set; }

    public void Start(uint nextSequence)
    {
        _next = nextSequence;
        Started = true;
        _pending.Clear();
        _pendingBytes = 0;
    }

    public StreamResult Push(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (!Started)
            Start(sequence);
        if (payload.IsEmpty)
            return default;

        var delta = (int)(sequence - (uint)_next);
        var absolute = _next + delta;

        if (delta < 0)
        {
            if (-delta >= payload.Length)
            {
                Duplicates++;
                return default;
            }
            payload = payload[-delta..];
            absolute = _next;
        }

        if (absolute == _next)
        {
            var output = new MemoryStream();
            output.Write(payload);
            _next += payload.Length;
            Drain(output);
            return new StreamResult(output.ToArray(), false);
        }

        OutOfOrder++;
        Store(absolute, payload.ToArray());
        if (_pendingBytes <= maxPendingBytes && _pending.Count <= maxPendingSegments)
            return default;

        Gaps++;
        _next = _pending.Keys.First();
        var recovered = new MemoryStream();
        Drain(recovered);
        _pending.Clear();
        _pendingBytes = 0;
        return new StreamResult(recovered.ToArray(), true);
    }

    private void Store(long absolute, byte[] data)
    {
        if (_pending.TryGetValue(absolute, out var existing))
        {
            if (existing.Length >= data.Length)
            {
                Duplicates++;
                return;
            }
            _pendingBytes -= existing.Length;
        }
        _pending[absolute] = data;
        _pendingBytes += data.Length;
    }

    private void Drain(MemoryStream output)
    {
        while (_pending.Count > 0)
        {
            var (key, data) = _pending.First();
            if (key > _next)
                return;

            _pending.Remove(key);
            _pendingBytes -= data.Length;
            var skip = (int)(_next - key);
            if (skip >= data.Length)
            {
                Duplicates++;
                continue;
            }
            output.Write(data, skip, data.Length - skip);
            _next += data.Length - skip;
        }
    }
}
