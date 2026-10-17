using PetTracker.Core.Protocol;

namespace PetTracker.Tests.Protocol;

public class OpcodeRegistryTests
{
    [Fact]
    public void ParsesMessagesWithSeveralOpcodes()
    {
        var registry = OpcodeRegistry.Parse("""{ "heartbeat": ["0036"], "soulGained": ["1e 56", "0x0438"] }""");
        Assert.True(registry.Is("soulGained", 0x1e56));
        Assert.True(registry.Is("soulGained", 0x0438));
        Assert.Equal(["heartbeat"], registry.NamesFor(0x0036));
        Assert.False(registry.IsKnown(0x9999));
    }

    [Fact]
    public void DefaultFileHoldsHeartbeatAndPetMessages()
    {
        var registry = OpcodeRegistry.LoadDefault();
        Assert.True(registry.Is(MessageNames.Heartbeat, 0x0036));
        Assert.True(registry.Is(MessageNames.PetCollectionSnapshot, 0x0090));
        Assert.True(registry.Is(MessageNames.PetSoulsGained, 0x0D90));
        Assert.True(registry.Is(MessageNames.PetLevelChanged, 0x0990));
        Assert.True(registry.Is(MessageNames.PetSoulsConsumed, 0x0E90));
    }

    [Fact]
    public void RejectsBadHex() => Assert.Throws<FormatException>(() => OpcodeRegistry.ParseOpcode("zz36"));
}
