namespace PetTracker.Core.Protocol;

/// <summary>
/// Streaming frame decoder: feed reassembled S2C bytes, get complete frames, leftovers are kept for the next feed.
/// After a desync (or when told to start scanning) it discards bytes until the heartbeat signature appears.
/// </summary>
public sealed class FrameDecoder
{
    private const int MaxBundleDepth = 2;

    private readonly FrameDecoderOptions _options;
    private readonly BundleDecoder _bundles;
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public FrameDecoder(FrameDecoderOptions? options = null, BundleDecoder? bundles = null, bool startScanning = false)
    {
        _options = options ?? new FrameDecoderOptions();
        _bundles = bundles ?? new BundleDecoder();
        Scanning = startScanning;
    }

    public DecoderStats Stats { get; } = new();
    public BundleDecoder Bundles => _bundles;
    public bool Scanning { get; private set; }
    public int BufferedBytes => _end - _start;

    /// <summary>Drops buffered bytes and waits for the next heartbeat. Use after a TCP gap.</summary>
    public void Resync()
    {
        Stats.BytesDiscarded += BufferedBytes;
        _start = _end = 0;
        Scanning = true;
    }

    public IReadOnlyList<Frame> Feed(ReadOnlySpan<byte> data)
    {
        Append(data);
        var frames = new List<Frame>();

        while (_end > _start)
        {
            if (Scanning)
            {
                if (!SkipToHeartbeat(0))
                    break;
                continue;
            }

            var span = new ReadOnlySpan<byte>(_buffer, _start, _end - _start);
            var status = FrameParser.TryParse(span, _options.LengthMode, _options.MaxFrameSize, out var opcode, out var bodyOffset, out var total);
            if (status == ParseStatus.NeedMore)
                break;
            if (status == ParseStatus.Invalid)
            {
                Stats.Desyncs++;
                Scanning = true;
                SkipToHeartbeat(1);
                continue;
            }

            if (span.StartsWith(_options.HeartbeatSignature))
                Stats.Heartbeats++;

            var payload = span[(bodyOffset - 2)..total];
            if (!TryExpandBundle(payload, 0, frames))
            {
                Stats.Frames++;
                frames.Add(new Frame(opcode, span[bodyOffset..total].ToArray()));
            }
            _start += total;
        }

        if (_start == _end)
            _start = _end = 0;
        return frames;
    }

    /// <summary>Handles a bundle whose frame opcode is FF FF, or whose body starts with FF FF.</summary>
    private bool TryExpandBundle(ReadOnlySpan<byte> payload, int depth, List<Frame> output)
    {
        if (depth >= MaxBundleDepth)
            return false;

        var candidate = BundleDecoder.LooksLikeBundle(payload) ? payload
            : payload.Length > 2 && BundleDecoder.LooksLikeBundle(payload[2..]) ? payload[2..]
            : default;
        if (candidate.IsEmpty)
            return false;

        if (!_bundles.TryDecompress(candidate, out var inner, out var format))
        {
            Stats.BundleFailures++;
            return false;
        }

        if (format == BundleFormat.Block) Stats.BundlesBlock++; else Stats.BundlesFrame++;
        ParseInner(inner, depth, output);
        return true;
    }

    private void ParseInner(byte[] data, int depth, List<Frame> output)
    {
        var span = data.AsSpan();
        while (!span.IsEmpty)
        {
            if (FrameParser.TryParse(span, _options.LengthMode, _options.MaxFrameSize, out var opcode, out var bodyOffset, out var total) != ParseStatus.Ok)
            {
                Stats.BundleFailures++;
                return;
            }

            var payload = span[(bodyOffset - 2)..total];
            if (!TryExpandBundle(payload, depth + 1, output))
            {
                Stats.Frames++;
                Stats.InnerFrames++;
                if (span.StartsWith(_options.HeartbeatSignature))
                    Stats.Heartbeats++;
                output.Add(new Frame(opcode, span[bodyOffset..total].ToArray(), Bundled: true));
            }
            span = span[total..];
        }
    }

    private bool SkipToHeartbeat(int from)
    {
        var span = new ReadOnlySpan<byte>(_buffer, _start, _end - _start);
        var signature = _options.HeartbeatSignature;
        var index = from < span.Length ? span[from..].IndexOf(signature) : -1;
        if (index >= 0)
        {
            Discard(from + index);
            Scanning = false;
            return true;
        }

        Discard(Math.Max(from, span.Length - (signature.Length - 1)));
        return false;
    }

    private void Discard(int count)
    {
        count = Math.Min(count, _end - _start);
        Stats.BytesDiscarded += count;
        _start += count;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_buffer.Length - _end < data.Length)
        {
            var live = _end - _start;
            if (_buffer.Length - live < data.Length + 1024)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, live + data.Length + 1024));
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, live);
            _start = 0;
            _end = live;
        }
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }
}
