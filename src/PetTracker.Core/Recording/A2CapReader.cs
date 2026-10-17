using System.Buffers.Binary;
using System.Text;

namespace PetTracker.Core.Recording;

public sealed class A2CapReader : IDisposable
{
    private readonly Stream _stream;

    public A2CapReader(Stream stream)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[A2CapFormat.HeaderLength];
        if (!TryReadExactly(header))
            throw new InvalidDataException("File is too short to be an .a2cap capture.");
        if (!header[..5].SequenceEqual(A2CapFormat.Magic))
            throw new InvalidDataException("Not an .a2cap file (bad magic).");
        Version = header[5];
        if (Version != A2CapFormat.Version)
            throw new InvalidDataException($"Unsupported .a2cap version {Version}.");
        Created = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(header[8..]), DateTimeKind.Utc);
    }

    public byte Version { get; }
    public DateTime Created { get; }

    public static A2CapReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024));

    /// <summary>Reads the next record; null at a clean end of file. A truncated last record also ends the read.</summary>
    public A2CapRecord? Read()
    {
        Span<byte> header = stackalloc byte[A2CapFormat.RecordHeaderLength];
        if (!TryReadExactly(header))
            return null;

        var kind = (A2CapKind)header[0];
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(header[1..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header[9..]);
        if (length > A2CapFormat.MaxPayload)
            throw new InvalidDataException($"Corrupt record length {length}.");

        var payload = new byte[length];
        if (!TryReadExactly(payload))
            return null;

        return kind switch
        {
            A2CapKind.S2CChunk => new S2CChunk(ticks, BinaryPrimitives.ReadInt32LittleEndian(payload), payload[4..]),
            A2CapKind.Marker => new Marker(ticks, BinaryPrimitives.ReadInt32LittleEndian(payload),
                payload.Length > 4 ? Encoding.UTF8.GetString(payload, 4, payload.Length - 4) : null),
            A2CapKind.ConnectionEvent => new ConnectionEvent(ticks, (ConnectionEventKind)payload[0],
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1)), Encoding.UTF8.GetString(payload, 5, payload.Length - 5)),
            _ => throw new InvalidDataException($"Unknown record kind {(byte)kind}."),
        };
    }

    public IEnumerable<A2CapRecord> ReadAll()
    {
        while (Read() is { } record)
            yield return record;
    }

    private bool TryReadExactly(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = _stream.Read(buffer[total..]);
            if (n == 0)
                return false;
            total += n;
        }
        return true;
    }

    public void Dispose() => _stream.Dispose();
}
