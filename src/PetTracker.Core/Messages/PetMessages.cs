namespace PetTracker.Core.Messages;

/// <summary>A decoded server-to-client pet message. Ids and amounts are as they appear on the wire.</summary>
public abstract record PetMessage;

public readonly record struct PetRegistration(uint PetId, uint DuplicateId, uint Level);

public readonly record struct PetProgress(uint PetId, uint Progress);

public readonly record struct PetAmount(uint PetId, uint Amount);

/// <summary>Opcode "PetCollectionSnapshot" (wire 00 90): the whole collection, sent once per login.</summary>
/// <param name="Unknown">Leading u32 of unknown meaning (99 04 00 00 in both captures).</param>
/// <param name="Registered">List 1: registered pets and their level (1-3).</param>
/// <param name="Progress">List 2: souls toward the next level; ids missing from list 1 are unregistered pets.</param>
/// <param name="Tail">Everything after list 2, kept raw for later research.</param>
public sealed record PetCollectionSnapshotMessage(
    uint Unknown,
    IReadOnlyList<PetRegistration> Registered,
    IReadOnlyList<PetProgress> Progress,
    byte[] Tail) : PetMessage;

/// <summary>Opcode "PetSoulsGained" (wire 0D 90): progress += amount.</summary>
public sealed record PetSoulsGainedMessage(IReadOnlyList<PetAmount> Entries) : PetMessage;

/// <summary>Opcodes "PetLevelChanged" (wire 09 90 registration/L1-L2, 14 90 L2-L3): 2 fixed header bytes (not a varint), u32 petId, u32 newLevel, then raw extras.</summary>
/// <param name="Extra">Bytes after the level (effect stats?), kept raw.</param>
public sealed record PetLevelChangedMessage(ushort Header, uint PetId, uint NewLevel, byte[] Extra) : PetMessage;

/// <summary>Opcode "PetSoulsConsumed" (wire 0E 90): hypothesis, souls consumed by the level change that arrives with it.</summary>
public sealed record PetSoulsConsumedMessage(IReadOnlyList<PetAmount> Entries) : PetMessage;
