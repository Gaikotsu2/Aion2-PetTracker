using System.Net;
using System.Text;
using PetTracker.Core.Capture;
using PetTracker.Core.Recording;

namespace PetTracker.Tests.Capture;

public class TcpReassemblerTests
{
    private static readonly IPEndPoint Server = new(IPAddress.Parse("203.0.113.5"), 7777);
    private static readonly IPEndPoint Client = new(IPAddress.Parse("192.168.1.20"), 50000);

    private static TcpSegment S2C(uint seq, string text, TcpFlags flags = TcpFlags.Ack) =>
        new(1, Server, Client, seq, flags, Encoding.ASCII.GetBytes(text), Direction.ServerToClient);

    private static string Text(IEnumerable<A2CapRecord> records) =>
        string.Concat(records.OfType<S2CChunk>().Select(c => Encoding.ASCII.GetString(c.Data)));

    private static string? Str(StreamResult r) => r.Data is null ? null : Encoding.ASCII.GetString(r.Data);

    private static StreamResult Push(TcpStreamReassembler r, uint seq, string text) =>
        r.Push(seq, Encoding.ASCII.GetBytes(text));

    [Fact]
    public void InOrderPassesThrough()
    {
        var r = new TcpStreamReassembler();
        Assert.Equal("abc", Str(Push(r, 100, "abc")));
        Assert.Equal("def", Str(Push(r, 103, "def")));
    }

    [Fact]
    public void OutOfOrderIsHeldThenReleased()
    {
        var r = new TcpStreamReassembler();
        Push(r, 100, "aa");
        Assert.Null(Str(Push(r, 104, "cc")));
        Assert.Null(Str(Push(r, 108, "ee")));
        Assert.Equal("bbcc", Str(Push(r, 102, "bb")));
        Assert.Equal("ddee", Str(Push(r, 106, "dd")));
        Assert.Equal(2, r.OutOfOrder);
    }

    [Fact]
    public void DuplicatesAndRetransmitsAreDropped()
    {
        var r = new TcpStreamReassembler();
        Push(r, 100, "abcd");
        Assert.Null(Str(Push(r, 100, "abcd")));
        Assert.Null(Str(Push(r, 102, "cd")));
        Assert.Equal(2, r.Duplicates);
    }

    [Fact]
    public void PartialOverlapKeepsOnlyNewBytes()
    {
        var r = new TcpStreamReassembler();
        Push(r, 100, "abcd");
        Assert.Equal("ef", Str(Push(r, 102, "cdef")));
    }

    [Fact]
    public void SequenceWrapsAround()
    {
        var r = new TcpStreamReassembler();
        var start = uint.MaxValue - 3;
        Assert.Equal("abcdef", Str(Push(r, start, "abcdef")));
        Assert.Equal("gh", Str(Push(r, start + 6, "gh")));
    }

    [Fact]
    public void WraparoundFillsHole()
    {
        var r = new TcpStreamReassembler();
        var start = uint.MaxValue - 1;
        Push(r, start, "ab");
        Assert.Null(Str(Push(r, start + 4, "ef")));
        Assert.Equal("cdef", Str(Push(r, start + 2, "cd")));
    }

    [Fact]
    public void GapSignalsResyncAndRecoversPendingData()
    {
        var r = new TcpStreamReassembler(maxPendingBytes: 10, maxPendingSegments: 100);
        Push(r, 100, "ab");
        Assert.False(Push(r, 200, "123456").Gap);
        var result = Push(r, 210, "7890xx");
        Assert.True(result.Gap);
        Assert.Equal("123456", Str(result));
        Assert.Equal(1, r.Gaps);
    }

    [Fact]
    public void ConnectionLevelEmitsFlowEventsAndOrderedChunks()
    {
        var r = new TcpReassembler();
        var records = new List<A2CapRecord>();
        records.AddRange(r.Process(S2C(999, "", TcpFlags.Syn | TcpFlags.Ack)));
        records.AddRange(r.Process(S2C(1000, "hello ")));
        records.AddRange(r.Process(S2C(1009, "ld")));
        records.AddRange(r.Process(S2C(1006, "wor")));
        records.AddRange(r.Process(S2C(1000, "hello ")));

        Assert.Equal(ConnectionEventKind.FlowOpened, records.OfType<ConnectionEvent>().First().Kind);
        Assert.Equal("hello world", Text(records));
        Assert.Equal(1, r.Duplicates);
    }

    [Fact]
    public void MidStreamJoinIsFlaggedAndFinClosesFlow()
    {
        var r = new TcpReassembler();
        var records = r.Process(S2C(5000, "xyz")).ToList();
        Assert.Equal(ConnectionEventKind.FlowJoined, records.OfType<ConnectionEvent>().Single().Kind);

        records = r.Process(S2C(5003, "", TcpFlags.Fin | TcpFlags.Ack)).ToList();
        Assert.Equal(ConnectionEventKind.FlowClosed, records.OfType<ConnectionEvent>().Single().Kind);
        Assert.Equal(0, r.ActiveFlows);
    }

    [Fact]
    public void ClientToServerPayloadIsIgnored()
    {
        var r = new TcpReassembler();
        var segment = new TcpSegment(1, Client, Server, 1, TcpFlags.Ack, [1, 2, 3], Direction.ClientToServer);
        Assert.Empty(r.Process(segment));
    }

    [Fact]
    public void GapIsReportedAsConnectionEvent()
    {
        var r = new TcpReassembler();
        r.Process(S2C(0, "a"));
        var records = new List<A2CapRecord>();
        for (uint i = 0; i < 400; i++)
            records.AddRange(r.Process(S2C(1000 + i * 1000, new string('z', 1000))));
        Assert.Contains(records.OfType<ConnectionEvent>(), e => e.Kind == ConnectionEventKind.Gap);
        Assert.True(r.Gaps >= 1);
    }
}
