using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Tests.Recording;

public class A2CapTests
{
    private static List<A2CapRecord> Sample() =>
    [
        new ConnectionEvent(10, ConnectionEventKind.FlowOpened, 1, "1.2.3.4:7777 -> 10.0.0.2:50000"),
        new S2CChunk(11, 1, [1, 2, 3, 4]),
        new Marker(12, 1),
        new Marker(13, 2, "soul drop é中"),
        new S2CChunk(14, 1, []),
        new ConnectionEvent(15, ConnectionEventKind.Gap, 1, "resync"),
    ];

    [Fact]
    public void RoundTripsAllRecordKinds()
    {
        var records = Sample();
        using var ms = new MemoryStream();
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        using (var writer = new A2CapWriter(new NonClosingStream(ms), created))
            records.ForEach(writer.Write);

        ms.Position = 0;
        using var reader = new A2CapReader(ms);
        Assert.Equal(created, reader.Created);
        Assert.Equal(A2CapFormat.Version, reader.Version);
        var read = reader.ReadAll().ToList();

        Assert.Equal(records.Count, read.Count);
        for (var i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].GetType(), read[i].GetType());
            Assert.Equal(records[i].Ticks, read[i].Ticks);
        }
        Assert.Equal([1, 2, 3, 4], ((S2CChunk)read[1]).Data);
        Assert.Null(((Marker)read[2]).Note);
        Assert.Equal("soul drop é中", ((Marker)read[3]).Note);
        Assert.Equal(2, ((Marker)read[3]).Number);
        Assert.Equal(ConnectionEventKind.Gap, ((ConnectionEvent)read[5]).Kind);
        Assert.Equal("resync", ((ConnectionEvent)read[5]).Detail);
    }

    [Fact]
    public void RejectsBadMagic() =>
        Assert.Throws<InvalidDataException>(() => new A2CapReader(new MemoryStream(new byte[32])));

    [Fact]
    public void TruncatedTailEndsReadCleanly()
    {
        using var ms = new MemoryStream();
        using (var writer = new A2CapWriter(new NonClosingStream(ms)))
        {
            writer.WriteChunk(1, 1, [1, 2, 3]);
            writer.WriteChunk(2, 1, new byte[100]);
        }

        var cut = new MemoryStream(ms.ToArray()[..^10]);
        using var reader = new A2CapReader(cut);
        Assert.Single(reader.ReadAll());
    }

    [Fact]
    public void ReplayThroughStreamProcessorDecodesFramesAndRealignsOnGap()
    {
        var mode = LengthMode.Fixed(-4);
        var heartbeat = TestFrames.Heartbeat(mode);
        var records = new A2CapRecord[]
        {
            new ConnectionEvent(1, ConnectionEventKind.FlowOpened, 1, "x"),
            new S2CChunk(2, 1, [.. heartbeat, .. TestFrames.Build(mode, 0x3336, 1)]),
            new ConnectionEvent(3, ConnectionEventKind.Gap, 1, "gap"),
            new S2CChunk(4, 1, [9, 9, 9, .. heartbeat, .. TestFrames.Build(mode, 0x3436, 2)]),
        };

        var processor = new StreamProcessor();
        var opcodes = records.Select(processor.Process).OfType<DecodedFrames>()
            .SelectMany(d => d.Frames).Select(f => f.Opcode).ToList();

        Assert.Equal([(ushort)0x0036, (ushort)0x3336, (ushort)0x0036, (ushort)0x3436], opcodes);
        Assert.Equal(0, processor.Stats.Desyncs);
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}
