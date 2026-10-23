namespace PetTracker.Core.Diagnostics;

/// <summary>Counts decoded frames per opcode (the number only, never the contents) so a log shows what the game sent when the opcode map is out of date.</summary>
public sealed class OpcodeHistogram
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, long> _counts = [];
    private readonly Dictionary<ushort, long> _reported = [];

    public void Add(ushort opcode)
    {
        lock (_gate)
            _counts[opcode] = _counts.GetValueOrDefault(opcode) + 1;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _counts.Clear();
            _reported.Clear();
        }
    }

    /// <summary>"0x1a2b=120 (+30), ..." for the busiest opcodes, or null when nothing new arrived since the last call.</summary>
    public string? TakeReport(Func<ushort, string?> nameOf, int top = 20)
    {
        lock (_gate)
        {
            var changed = _counts.Where(kv => kv.Value != _reported.GetValueOrDefault(kv.Key)).ToList();
            if (changed.Count == 0)
                return null;
            var line = string.Join(", ", _counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(top)
                .Select(kv => $"{kv.Key:x4}{(nameOf(kv.Key) is { } n ? $"[{n}]" : "")}={kv.Value} (+{kv.Value - _reported.GetValueOrDefault(kv.Key)})"));
            foreach (var kv in _counts)
                _reported[kv.Key] = kv.Value;
            return $"{_counts.Count} distinct opcodes, {_counts.Values.Sum()} frames; top: {line}";
        }
    }
}
