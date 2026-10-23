using PetTracker.Core.Domain;
using PetTracker.Core.Protocol;
using PetTracker.Core.Recording;

namespace PetTracker.Tests.Protocol;

/// <summary>
/// A ping reducer or VPN opens extra connections whose stream is the game's heartbeat followed by bytes that do not
/// parse (one desync per heartbeat). They must not drown out a flow that decodes real game frames.
/// </summary>
public class RelayFlowTests
{
    private static readonly LengthMode V4 = LengthMode.Fixed(-4);
    private static readonly byte[] NotAFrame = [.. Enumerable.Repeat((byte)0xFF, 40)];

    private static byte[] RelayStream(int beats) =>
        [.. Enumerable.Range(0, beats).SelectMany(_ => TestFrames.Heartbeat(V4).Concat(NotAFrame))];

    private static byte[] GameStream(int frames) =>
        [.. TestFrames.Heartbeat(V4), .. Enumerable.Range(0, frames).SelectMany(i => TestFrames.Build(V4, 0x2a38, (byte)i, 2, 3))];

    private static StreamProcessor Run(params A2CapRecord[] records)
    {
        var processor = new StreamProcessor();
        foreach (var r in records)
            processor.Process(r);
        return processor;
    }

    private static A2CapRecord Joined(int flow) => new ConnectionEvent(0, ConnectionEventKind.FlowJoined, flow, $"relay{flow} -> client");
    private static A2CapRecord Chunk(int flow, byte[] data) => new S2CChunk(1, flow, data);

    [Fact]
    public void RelayFlowsAreDroppedOnceAnotherFlowDecodesGameFrames()
    {
        var game = new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 9, "game -> client");
        var p = Run(Joined(1), Joined(2), Joined(3), game,
            Chunk(1, RelayStream(60)), Chunk(2, RelayStream(60)), Chunk(3, RelayStream(60)));

        Assert.True(p.Stats.Desyncs > 100); // nothing healthy yet: the noise is reported as before
        Assert.False(p.Relay.Healthy);

        p.Process(Chunk(9, GameStream(60)));

        var s = p.Stats;
        Assert.Equal(0, s.Desyncs);
        Assert.Equal(3, p.Flows.Count(f => f.Rejected && f.RelayLike));
        var relay = p.Relay;
        Assert.True(relay.Detected);
        Assert.Equal(3, relay.Flows);
        Assert.True(relay.Identical);
        Assert.True(relay.Healthy);
    }

    [Fact]
    public void RelayFlowsAreKeptWhenNoFlowDecodesGameFrames()
    {
        // The same pattern on a single flow is also what a changed frame format looks like: keep reporting it.
        var p = Run(Joined(1), Chunk(1, RelayStream(80)));

        Assert.True(p.Stats.Desyncs >= 79);
        Assert.False(p.Flows.Single().Rejected);
        Assert.False(p.Relay.Detected);
    }

    [Fact]
    public void RelayFlowThatAppearsLaterIsDroppedAsWell()
    {
        var p = Run(new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 9, "game -> client"), Chunk(9, GameStream(60)),
            Joined(1), Chunk(1, RelayStream(40)));

        Assert.Equal(0, p.Stats.Desyncs);
        Assert.True(p.Flows.Single(f => f.FlowId == 1).RelayLike);
        Assert.False(p.Flows.Single(f => f.FlowId == 9).Rejected);
    }

    [Fact]
    public void FlowWithRealFramesAndAFewDesyncsIsNotRelayLike()
    {
        var mixed = new List<byte>();
        for (var i = 0; i < 40; i++)
            mixed.AddRange(TestFrames.Heartbeat(V4).Concat(TestFrames.Build(V4, 0x2a38, 1, 2, 3)));
        mixed.AddRange(NotAFrame);
        mixed.AddRange(TestFrames.Heartbeat(V4));

        var p = Run(new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 9, "game -> client"), Chunk(9, [.. mixed]),
            Joined(1), Chunk(1, RelayStream(40)));

        Assert.False(p.Flows.Single(f => f.FlowId == 9).Rejected);
        Assert.False(p.Flows.Single(f => f.FlowId == 9).RelayLike);
    }

    [Fact]
    public void DifferingByteCountsAreNotCalledIdentical()
    {
        var p = Run(Joined(1), Joined(2), new ConnectionEvent(0, ConnectionEventKind.FlowOpened, 9, "game -> client"),
            Chunk(1, RelayStream(60)), Chunk(2, RelayStream(120)), Chunk(9, GameStream(60)));

        Assert.True(p.Relay.Detected);
        Assert.False(p.Relay.Identical);
        Assert.Contains("ping reducer or VPN", p.Relay.Describe());
    }

    [Fact]
    public void DriftMonitorRebaseClearsTheSuspicion()
    {
        var monitor = new FramingDriftMonitor();
        var now = DateTime.UtcNow;
        Assert.True(monitor.Observe(600, now));
        monitor.Rebase(0);
        Assert.False(monitor.Suspected);
        Assert.False(monitor.Observe(2, now.AddSeconds(1)));
        Assert.True(monitor.Observe(10, now.AddSeconds(2)));
    }
}
