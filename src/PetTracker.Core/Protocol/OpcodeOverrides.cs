using System.Text.Json;
using PetTracker.Core.Persistence;

namespace PetTracker.Core.Protocol;

/// <summary>
/// The user's opcode override file (%AppData%\Aion2PetTracker\opcodes.json): the same shape as data/opcodes.default.json,
/// but only the logical names it lists replace the defaults; every other name keeps its default opcodes.
/// </summary>
public static class OpcodeOverrides
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ushort>> None = new Dictionary<string, IReadOnlyList<ushort>>();

    public static bool TryParseJson(string json, out Dictionary<string, IReadOnlyList<ushort>> overrides, out string? error)
    {
        overrides = new Dictionary<string, IReadOnlyList<ushort>>(StringComparer.OrdinalIgnoreCase);
        error = null;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip })
                      ?? [];
            foreach (var (name, list) in raw)
                overrides[name] = list.Select(OpcodeRegistry.ParseOpcode).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            overrides.Clear();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The overrides in <paramref name="path"/>; empty when the file is missing. A broken file is reported through <paramref name="error"/> and ignored.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ushort>> Load(string? path, out string? error)
    {
        error = null;
        if (path is null || !File.Exists(path))
            return None;
        if (!TryParseJson(File.ReadAllText(path), out var overrides, out error))
            return None;
        return overrides;
    }

    public static string ToJson(IReadOnlyDictionary<string, IReadOnlyList<ushort>> overrides) =>
        JsonSerializer.Serialize(
            overrides.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Select(Format).ToArray()),
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes the file atomically. An empty map deletes it (back to the defaults).</summary>
    public static void Save(string path, IReadOnlyDictionary<string, IReadOnlyList<ushort>> overrides)
    {
        if (overrides.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        AtomicFile.WriteAllText(path, ToJson(overrides));
    }

    public static string Format(ushort opcode) => opcode.ToString("X4");

    public static string FormatList(IEnumerable<ushort> opcodes) => string.Join(", ", opcodes.Select(Format));

    /// <summary>Parses "0990, 1490" (hex, comma separated; "09 90" and "0x0990" also work). Throws <see cref="FormatException"/>.</summary>
    public static IReadOnlyList<ushort> ParseList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(OpcodeRegistry.ParseOpcode).Distinct().ToArray();

    /// <summary>The defaults with each name in <paramref name="overrides"/> replaced.</summary>
    public static OpcodeRegistry Merge(OpcodeRegistry defaults, IReadOnlyDictionary<string, IReadOnlyList<ushort>> overrides)
    {
        var map = defaults.ToDictionary();
        foreach (var (name, opcodes) in overrides)
            map[name] = opcodes;
        return new OpcodeRegistry(map);
    }

    /// <summary>Defaults plus the user's file (when valid). <paramref name="error"/> says why a broken file was ignored.</summary>
    public static OpcodeRegistry LoadEffective(string? overridesPath, out string? error) =>
        Merge(OpcodeRegistry.LoadDefault(), Load(overridesPath, out error));
}
