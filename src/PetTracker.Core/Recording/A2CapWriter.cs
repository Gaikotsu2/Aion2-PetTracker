using System.Buffers.Binary;
using System.Text;

namespace PetTracker.Core.Recording;

/// <summary>
/// .a2cap layout: "A2CAP" magic, u8 version, u16 reserved, i64 created (UTC ticks); then records of
/// u8 kind, i64 ticks, u32 payload length, payload. All integers little-endian.
/// Chunk payload = i32 flowId + bytes. Marker payload = i32 number + optional UTF-8 note.
/// Event payload = u8 kind + i32 flowId + UTF-8 detail.
/// </summary>
public static class A2CapFormat
{
    public static ReadOnlySpan<byte> Magic => "A2CAP"u8;
    public const byte Version = 1;
    public const int HeaderLength = 5 + 1 + 2 + 8;
    public const int RecordHeaderLength = 1 + 8 + 4;
    public const int MaxPayload = 64 * 1024 * 1024;
}

public sealed class A2CapWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly object _gate = new();

    public A2CapWriter(Stream stream, DateTime? created = null)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[A2CapFormat.HeaderLength];
        A2CapFormat.Magic.CopyTo(header);
        header[5] = A2CapFormat.Version;
        header[6] = header[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], (created ?? DateTime.UtcNow).Ticks);
        _stream.Write(header);
        _stream.Flush();
    }

    public static A2CapWriter Create(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024));

    public void Write(A2CapRecord record)
    {
        switch (record)
        {
            case S2CChunk c:
                WriteChunk(c.Ticks, c.FlowId, c.Data);
                break;
            case Marker m:
                WriteMarker(m.Ticks, m.Number, m.Note);
                break;
            case ConnectionEvent e:
                WriteEvent(e.Ticks, e.Kind, e.FlowId, e.Detail);
                break;
            default:
                throw new ArgumentException($"Unknown record type {record.GetType().Name}.", nameof(record));
        }
    }

    public void WriteChunk(long ticks, int flowId, ReadOnlySpan<byte> data)
    {
        var payload = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, flowId);
        data.CopyTo(payload.AsSpan(4));
        WriteRecord(A2CapKind.S2CChunk, ticks, payload, flush: false);
    }

    public void WriteMarker(long ticks, int number, string? note = null)
    {
        var text = string.IsNullOrEmpty(note) ? [] : Encoding.UTF8.GetBytes(note);
        var payload = new byte[4 + text.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, number);
        text.CopyTo(payload, 4);
        WriteRecord(A2CapKind.Marker, ticks, payload, flush: true);
    }

    public void WriteEvent(long ticks, ConnectionEventKind kind, int flowId, string detail)
    {
        var text = Encoding.UTF8.GetBytes(detail);
        var payload = new byte[5 + text.Length];
        payload[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), flowId);
        text.CopyTo(payload, 5);
        WriteRecord(A2CapKind.ConnectionEvent, ticks, payload, flush: true);
    }

    public void Flush()
    {
        lock (_gate)
            _stream.Flush();
    }

    private void WriteRecord(A2CapKind kind, long ticks, byte[] payload, bool flush)
    {
        Span<byte> header = stackalloc byte[A2CapFormat.RecordHeaderLength];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(header[1..], ticks);
        BinaryPrimitives.WriteUInt32LittleEndian(header[9..], (uint)payload.Length);
        lock (_gate)
        {
            _stream.Write(header);
            _stream.Write(payload);
            if (flush)
                _stream.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Flush();
            _stream.Dispose();
        }
    }
}
