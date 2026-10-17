using PetTracker.Core.Protocol;

namespace PetTracker.Core.Messages;

/// <summary>
/// Turns frames into typed pet messages. Which opcode means what comes from the <see cref="OpcodeRegistry"/> by logical
/// name, never from constants here, so a patch only needs a new opcodes JSON.
/// </summary>
public static class PetMessageParser
{
    /// <summary>Returns null when the frame is not a pet message; throws <see cref="InvalidDataException"/> when it is one but malformed.</summary>
    public static PetMessage? Parse(OpcodeRegistry registry, Frame frame)
    {
        var op = frame.Opcode;
        if (registry.Is(MessageNames.PetCollectionSnapshot, op)) return ParseSnapshot(frame.Body);
        if (registry.Is(MessageNames.PetSoulsGained, op)) return new PetSoulsGainedMessage(ReadAmounts(frame.Body));
        if (registry.Is(MessageNames.PetLevelChanged, op)) return ParseLevelChanged(frame.Body);
        if (registry.Is(MessageNames.PetSoulsConsumed, op)) return new PetSoulsConsumedMessage(ReadAmounts(frame.Body));
        return null;
    }

    /// <summary>Like <see cref="Parse"/> but reports malformed bodies through <paramref name="error"/> instead of throwing.</summary>
    public static bool TryParse(OpcodeRegistry registry, Frame frame, out PetMessage? message, out string? error)
    {
        try
        {
            message = Parse(registry, frame);
            error = null;
            return message is not null;
        }
        catch (InvalidDataException ex)
        {
            message = null;
            error = $"{frame.OpcodeHex}: {ex.Message}";
            return false;
        }
    }

    /// <summary>The pet messages among <paramref name="frames"/>, in order. Malformed ones are reported to <paramref name="onError"/> and skipped.</summary>
    public static IEnumerable<PetMessage> Extract(OpcodeRegistry registry, IEnumerable<Frame> frames, Action<string>? onError = null)
    {
        foreach (var frame in frames)
        {
            if (TryParse(registry, frame, out var message, out var error))
                yield return message!;
            else if (error is not null)
                onError?.Invoke(error);
        }
    }

    public static PetCollectionSnapshotMessage ParseSnapshot(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body);
        var unknown = r.ReadU32();

        var n = ReadCount(ref r, 12);
        var registered = new List<PetRegistration>();
        for (var i = 0; i < n; i++)
            registered.Add(new PetRegistration(r.ReadU32(), r.ReadU32(), r.ReadU32()));

        var m = ReadCount(ref r, 8);
        var progress = new List<PetProgress>();
        for (var i = 0; i < m; i++)
            progress.Add(new PetProgress(r.ReadU32(), r.ReadU32()));

        return new PetCollectionSnapshotMessage(unknown, registered, progress, r.ReadBytes(r.Remaining).ToArray());
    }

    public static PetLevelChangedMessage ParseLevelChanged(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body);
        var header = r.ReadU16();
        var petId = r.ReadU32();
        var level = r.ReadU32();
        return new PetLevelChangedMessage(header, petId, level, r.ReadBytes(r.Remaining).ToArray());
    }

    public static IReadOnlyList<PetAmount> ParseAmounts(ReadOnlySpan<byte> body) => ReadAmounts(body);

    private static List<PetAmount> ReadAmounts(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body);
        int count = r.ReadU8();
        var list = new List<PetAmount>(count);
        for (var i = 0; i < count; i++)
            list.Add(new PetAmount(r.ReadU32(), r.ReadU32()));
        return list;
    }

    private static int ReadCount(ref PacketReader r, int entrySize)
    {
        var count = r.ReadVarint();
        if (count > (ulong)(r.Remaining / entrySize))
            throw new InvalidDataException($"List of {count} entries does not fit in the {r.Remaining} bytes left.");
        return (int)count;
    }
}
