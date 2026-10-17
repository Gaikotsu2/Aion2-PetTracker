using System.Globalization;
using System.Text.Json;

namespace PetTracker.Core.Protocol;

public static class MessageNames
{
    public const string Heartbeat = "heartbeat";
    public const string PetCollectionSnapshot = "PetCollectionSnapshot";
    public const string PetSoulsGained = "PetSoulsGained";
    public const string PetLevelChanged = "PetLevelChanged";
    public const string PetSoulsConsumed = "PetSoulsConsumed";
}

/// <summary>Maps logical message names to the opcodes that currently carry them (patches renumber opcodes).</summary>
public sealed class OpcodeRegistry
{
    public const string DefaultFileName = "opcodes.default.json";

    private readonly Dictionary<string, ushort[]> _byName;
    private readonly Dictionary<ushort, List<string>> _byOpcode = [];

    public OpcodeRegistry(IReadOnlyDictionary<string, IReadOnlyList<ushort>> messages)
    {
        _byName = messages.ToDictionary(m => m.Key, m => m.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, opcodes) in _byName)
        foreach (var opcode in opcodes)
        {
            if (!_byOpcode.TryGetValue(opcode, out var names))
                _byOpcode[opcode] = names = [];
            names.Add(name);
        }
    }

    public IEnumerable<string> MessageNamesKnown => _byName.Keys;

    /// <summary>A copy of the whole map (logical name to opcodes).</summary>
    public Dictionary<string, IReadOnlyList<ushort>> ToDictionary() =>
        _byName.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ushort>)kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ushort> OpcodesFor(string name) => _byName.TryGetValue(name, out var o) ? o : [];

    public IReadOnlyList<string> NamesFor(ushort opcode) => _byOpcode.TryGetValue(opcode, out var n) ? n : [];

    public bool IsKnown(ushort opcode) => _byOpcode.ContainsKey(opcode);

    public bool Is(string name, ushort opcode) => _byName.TryGetValue(name, out var o) && Array.IndexOf(o, opcode) >= 0;

    public string Describe(ushort opcode) => _byOpcode.TryGetValue(opcode, out var n) ? string.Join("|", n) : "";

    public static OpcodeRegistry LoadDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", DefaultFileName);
        return File.Exists(path) ? Load(path) : new OpcodeRegistry(new Dictionary<string, IReadOnlyList<ushort>>
        {
            [MessageNames.Heartbeat] = [0x0036],
            [MessageNames.PetCollectionSnapshot] = [0x0090],
            [MessageNames.PetSoulsGained] = [0x0D90],
            [MessageNames.PetLevelChanged] = [0x0990, 0x1490],
            [MessageNames.PetSoulsConsumed] = [0x0E90],
        });
    }

    public static OpcodeRegistry Load(string path) => Parse(File.ReadAllText(path));

    public static OpcodeRegistry Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip })
                  ?? throw new InvalidDataException("Opcode file is empty.");
        return new OpcodeRegistry(raw.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ushort>)kv.Value.Select(ParseOpcode).ToArray()));
    }

    public static ushort ParseOpcode(string text)
    {
        var hex = text.Replace(" ", "").Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (hex.Length != 4 || !ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"'{text}' is not a 2-byte opcode in hex (example: 3336 or \"33 36\").");
        return value;
    }
}
