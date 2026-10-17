using PetTracker.Core.Domain;
using PetTracker.Core.Protocol;

namespace PetTracker.Tests.Protocol;

public sealed class OpcodeOverridesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-opcodes-" + Guid.NewGuid().ToString("N"));

    public OpcodeOverridesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static OpcodeRegistry Defaults() => OpcodeRegistry.Parse("""
        { "heartbeat": ["0036"], "PetCollectionSnapshot": ["0090"], "PetSoulsGained": ["0D90"],
          "PetLevelChanged": ["0990", "1490"], "PetSoulsConsumed": ["0E90"] }
        """);

    [Fact]
    public void OverrideReplacesOnlyTheListedNames()
    {
        Assert.True(OpcodeOverrides.TryParseJson("""{ "PetCollectionSnapshot": ["1A2B"] }""", out var overrides, out var error));
        Assert.Null(error);

        var merged = OpcodeOverrides.Merge(Defaults(), overrides);

        Assert.Equal([(ushort)0x1A2B], merged.OpcodesFor(MessageNames.PetCollectionSnapshot));
        Assert.False(merged.Is(MessageNames.PetCollectionSnapshot, 0x0090));
        Assert.Equal([(ushort)0x0D90], merged.OpcodesFor(MessageNames.PetSoulsGained));
        Assert.Equal([(ushort)0x0990, (ushort)0x1490], merged.OpcodesFor(MessageNames.PetLevelChanged));
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var path = Path.Combine(_dir, "opcodes.json");
        OpcodeOverrides.Save(path, new Dictionary<string, IReadOnlyList<ushort>> { ["PetLevelChanged"] = [0x0A90, 0x0B90] });

        var loaded = OpcodeOverrides.Load(path, out var error);

        Assert.Null(error);
        Assert.Equal([(ushort)0x0A90, (ushort)0x0B90], loaded["PetLevelChanged"]);
        Assert.Contains("0A90", File.ReadAllText(path));
    }

    [Fact]
    public void SavingAnEmptyMapRemovesTheFile()
    {
        var path = Path.Combine(_dir, "opcodes.json");
        OpcodeOverrides.Save(path, new Dictionary<string, IReadOnlyList<ushort>> { ["PetSoulsGained"] = [0x1111] });
        Assert.True(File.Exists(path));

        OpcodeOverrides.Save(path, new Dictionary<string, IReadOnlyList<ushort>>());

        Assert.False(File.Exists(path));
        Assert.Empty(OpcodeOverrides.Load(path, out _));
    }

    [Fact]
    public void BrokenFileIsIgnoredWithAnError()
    {
        var path = Path.Combine(_dir, "opcodes.json");
        File.WriteAllText(path, """{ "PetSoulsGained": ["ZZZZ"] }""");

        var loaded = OpcodeOverrides.Load(path, out var error);

        Assert.Empty(loaded);
        Assert.NotNull(error);
        Assert.Equal([(ushort)0x0D90], OpcodeOverrides.Merge(Defaults(), loaded).OpcodesFor(MessageNames.PetSoulsGained));
    }

    [Theory]
    [InlineData("0990, 1490", new[] { 0x0990, 0x1490 })]
    [InlineData("09 90,0x1490", new[] { 0x0990, 0x1490 })]
    [InlineData("0d90", new[] { 0x0D90 })]
    [InlineData("0990, 0990", new[] { 0x0990 })]
    public void ParseListAcceptsHexInManySpellings(string text, int[] expected) =>
        Assert.Equal(expected.Select(e => (ushort)e), OpcodeOverrides.ParseList(text));

    [Theory]
    [InlineData("099")]
    [InlineData("12345")]
    [InlineData("0990; 1490")]
    [InlineData("hello")]
    public void ParseListRejectsMalformedOpcodes(string text) =>
        Assert.Throws<FormatException>(() => OpcodeOverrides.ParseList(text));

    [Fact]
    public void LoadEffectiveReadsTheOverrideFile()
    {
        var path = Path.Combine(_dir, "opcodes.json");
        File.WriteAllText(path, """{ "PetSoulsConsumed": ["0F90"] }""");
        var registry = OpcodeOverrides.LoadEffective(path, out var error);
        Assert.Null(error);
        Assert.True(registry.Is(MessageNames.PetSoulsConsumed, 0x0F90));
    }
}

public class FrameBufferTests
{
    private static Frame F(int size, ushort opcode = 0x1234) => new(opcode, new byte[size]);

    private static long Sec(double s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + (long)(s * TimeSpan.TicksPerSecond);

    [Fact]
    public void KeepsOnlyTheRecentWindow()
    {
        var buffer = new FrameBuffer(window: TimeSpan.FromSeconds(10));
        buffer.Add(Sec(0), [F(10)]);
        buffer.Add(Sec(5), [F(10)]);
        buffer.Add(Sec(12), [F(10)]);
        Assert.Equal(2, buffer.Count); // the frame from t=0 is older than 10 s
        Assert.Equal(Sec(5), buffer.Snapshot()[0].Ticks);
    }

    [Fact]
    public void ByteCapDropsTheOldestFirst()
    {
        var buffer = new FrameBuffer(maxBytes: 100);
        for (var i = 0; i < 10; i++)
            buffer.Add(Sec(i), [F(30)]);
        Assert.True(buffer.Bytes <= 100);
        Assert.Equal(Sec(9), buffer.Snapshot()[^1].Ticks);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void SkipsHeartbeatsAndOversizedFrames()
    {
        var buffer = new FrameBuffer(maxFrameBytes: 1000);
        buffer.Add(Sec(0), [F(8, 0x0036), F(5000), F(20)], skip: f => f.Opcode == 0x0036);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void ClearEmptiesIt()
    {
        var buffer = new FrameBuffer();
        buffer.Add(Sec(0), [F(10)]);
        buffer.Clear();
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Bytes);
    }
}

public class FramingDriftMonitorTests
{
    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StaysQuietForAFewDesyncs()
    {
        var monitor = new FramingDriftMonitor(threshold: 5);
        Assert.False(monitor.Observe(5, T));
        Assert.False(monitor.Suspected);
    }

    [Fact]
    public void RaisesOnceWhenMoreThanNInThirtySeconds()
    {
        var monitor = new FramingDriftMonitor(threshold: 5);
        Assert.False(monitor.Observe(3, T));
        Assert.True(monitor.Observe(6, T.AddSeconds(10)));
        Assert.True(monitor.Suspected);
        Assert.False(monitor.Observe(9, T.AddSeconds(11))); // already raised
    }

    [Fact]
    public void SlowTrickleDoesNotCount()
    {
        var monitor = new FramingDriftMonitor(threshold: 5);
        for (var i = 1; i <= 20; i++)
            Assert.False(monitor.Observe(i, T.AddSeconds(i * 20))); // one every 20 s: at most 2 inside any 30 s window
        Assert.False(monitor.Suspected);
    }

    [Fact]
    public void ResetClearsTheSuspicion()
    {
        var monitor = new FramingDriftMonitor(threshold: 2);
        monitor.Observe(10, T);
        Assert.True(monitor.Suspected);
        monitor.Reset();
        Assert.False(monitor.Suspected);
        Assert.Contains("--auto-detect", FramingDriftMonitor.Hint);
    }
}
