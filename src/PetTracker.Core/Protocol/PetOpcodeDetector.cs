using System.Buffers.Binary;
using System.Globalization;
using PetTracker.Core.Domain;

namespace PetTracker.Core.Protocol;

/// <summary>A decoded frame with the time (UTC ticks) it arrived. The detector's input.</summary>
public readonly record struct ObservedFrame(long Ticks, Frame Frame);

/// <summary>One opcode the detector thinks carries a logical message.</summary>
/// <param name="LogicalName">A <see cref="MessageNames"/> value.</param>
/// <param name="Matches">Frames of this opcode that have the message's shape.</param>
/// <param name="Frames">All frames of this opcode in the input.</param>
/// <param name="Confidence">0..1: consistency (Matches / Frames) weighted by how much evidence there is.</param>
public sealed record OpcodeCandidate(
    string LogicalName, ushort Opcode, int Matches, int Frames, double Confidence, string Evidence, string SampleHex)
{
    public string OpcodeHex => Opcode.ToString("X4", CultureInfo.InvariantCulture);
}

/// <summary>What the detector found: candidates per logical name, best first.</summary>
public sealed record OpcodeDetection(IReadOnlyList<OpcodeCandidate> Candidates, int FramesExamined)
{
    public const double DefaultMinConfidence = 0.5;

    /// <summary>Highest confidence of the snapshot candidates, or 0.</summary>
    public double SnapshotConfidence => Candidates.Where(c => c.LogicalName == MessageNames.PetCollectionSnapshot)
        .Select(c => c.Confidence).DefaultIfEmpty(0).Max();

    /// <summary>The snapshot shape is very specific, so one clean match is enough to propose new codes.</summary>
    public bool HasConfidentSnapshot => SnapshotConfidence >= 0.8;

    /// <summary>The proposed {logical name -> opcodes}, using candidates at or above <paramref name="minConfidence"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ushort>> Mapping(double minConfidence = DefaultMinConfidence) =>
        Candidates.Where(c => c.Confidence >= minConfidence)
            .GroupBy(c => c.LogicalName)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ushort>)g.OrderByDescending(c => c.Confidence).ThenBy(c => c.Opcode).Select(c => c.Opcode).ToArray());

    public IEnumerable<OpcodeCandidate> For(string logicalName) => Candidates.Where(c => c.LogicalName == logicalName);
}

/// <summary>
/// Finds the pet messages in a stream of frames by their SHAPE, ignoring opcodes, so a patch that renumbers them can be
/// recovered from one login. Shapes (all little-endian, ids from the catalog):
/// snapshot = u32, varint N (50..400), N x {id, id, level 1..3}, varint M, M x {id, progress &lt; 75};
/// souls gained/consumed = u8 n (1..10) + n x {id, value 1..200}, body length 1+8n, told apart by whether the entries follow
/// a level change of the same pet within a second with value 5/25/75 (consumed);
/// level changed = 23 bytes, id at offset 2, level 1..3 at offset 6.
/// </summary>
public sealed class PetOpcodeDetector
{
    public static readonly TimeSpan ConsumeWindow = TimeSpan.FromSeconds(1);
    private static readonly uint[] ConsumeValues = [5, 25, 75];

    private readonly HashSet<int> _ids;

    public PetOpcodeDetector(IEnumerable<int> catalogIds) => _ids = catalogIds.ToHashSet();

    public PetOpcodeDetector(PetCatalog catalog) : this(catalog.KnownIds) { }

    public OpcodeDetection Detect(IEnumerable<ObservedFrame> input)
    {
        var frames = input as IReadOnlyList<ObservedFrame> ?? input.ToList();
        var totals = frames.GroupBy(f => f.Frame.Opcode).ToDictionary(g => g.Key, g => g.Count());
        var candidates = new List<OpcodeCandidate>();

        // Level changes first: the consumed-souls rule needs them.
        var levelHits = new Dictionary<ushort, List<int>>();
        var levelEvents = new List<(int Index, long Ticks, uint PetId)>();
        for (var i = 0; i < frames.Count; i++)
        {
            var body = frames[i].Frame.Body;
            if (!IsLevelChanged(body))
                continue;
            var op = frames[i].Frame.Opcode;
            if (!levelHits.TryGetValue(op, out var list))
                levelHits[op] = list = [];
            list.Add(i);
            levelEvents.Add((i, frames[i].Ticks, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2))));
        }
        foreach (var (op, hits) in levelHits)
            candidates.Add(Candidate(MessageNames.PetLevelChanged, op, hits, totals[op], frames,
                $"{hits.Count} of {totals[op]} frames are 23 bytes with a catalog pet id at offset 2 and a level 1..3 at offset 6", specific: true));

        // Snapshot.
        var snapshotHits = new Dictionary<ushort, List<int>>();
        var snapshotShape = new Dictionary<ushort, (int N, int M)>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (!IsSnapshot(frames[i].Frame.Body, out var n, out var m))
                continue;
            var op = frames[i].Frame.Opcode;
            if (!snapshotHits.TryGetValue(op, out var list))
                snapshotHits[op] = list = [];
            list.Add(i);
            snapshotShape[op] = (n, m);
        }
        foreach (var (op, hits) in snapshotHits)
            candidates.Add(Candidate(MessageNames.PetCollectionSnapshot, op, hits, totals[op], frames,
                $"{hits.Count} of {totals[op]} frames hold a registered-pet list (N={snapshotShape[op].N}) and a progress list (M={snapshotShape[op].M}) of catalog pets",
                specific: true));

        // Souls gained / consumed.
        var soulHits = new Dictionary<ushort, List<int>>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (!IsSoulList(frames[i].Frame.Body))
                continue;
            var op = frames[i].Frame.Opcode;
            if (!soulHits.TryGetValue(op, out var list))
                soulHits[op] = list = [];
            list.Add(i);
        }
        foreach (var (op, hits) in soulHits)
        {
            var afterLevel = hits.Count(i => FollowsLevelChange(frames[i], i, levelEvents));
            var ratio = (double)afterLevel / hits.Count;
            if (afterLevel > 0 && ratio >= 0.8)
            {
                candidates.Add(Candidate(MessageNames.PetSoulsConsumed, op, hits, totals[op], frames,
                    $"{afterLevel} of {hits.Count} soul-list frames (value 5/25/75) come within 1 s after a level change of the same pet",
                    specific: true, extra: ratio));
            }
            else
            {
                var plain = hits.Count - afterLevel;
                candidates.Add(Candidate(MessageNames.PetSoulsGained, op, hits, totals[op], frames,
                    $"{hits.Count} of {totals[op]} frames are soul lists; {plain} stand alone without a level change{(afterLevel > 0 ? $" ({afterLevel} follow one)" : "")}",
                    extra: 1.0 - ratio));
            }
        }

        // Heartbeat: a frequent frame with an 8-byte body holding a plausible Unix-ms timestamp.
        var beats = new Dictionary<ushort, List<int>>();
        for (var i = 0; i < frames.Count; i++)
        {
            var body = frames[i].Frame.Body;
            if (body.Length != 8)
                continue;
            var ms = BinaryPrimitives.ReadInt64LittleEndian(body);
            if (ms < 1_500_000_000_000 || ms > 4_000_000_000_000)
                continue;
            var op = frames[i].Frame.Opcode;
            if (!beats.TryGetValue(op, out var list))
                beats[op] = list = [];
            list.Add(i);
        }
        foreach (var (op, hits) in beats.Where(b => b.Value.Count >= 10))
            candidates.Add(Candidate(MessageNames.Heartbeat, op, hits, totals[op], frames,
                $"{hits.Count} of {totals[op]} frames are 8-byte Unix-millisecond timestamps"));

        return new OpcodeDetection(
            candidates.OrderBy(c => c.LogicalName).ThenByDescending(c => c.Confidence).ThenBy(c => c.Opcode).ToList(),
            frames.Count);
    }

    private static OpcodeCandidate Candidate(string name, ushort opcode, List<int> hits, int total, IReadOnlyList<ObservedFrame> frames,
        string evidence, bool specific = false, double extra = 1.0)
    {
        var consistency = (double)hits.Count / total;
        // Specific shapes (snapshot) need one match; loose shapes need a few to be convincing.
        var amount = specific ? Math.Min(1.0, 0.85 + 0.05 * hits.Count) : hits.Count / (hits.Count + 1.0);
        var confidence = Math.Round(consistency * amount * extra, 3);
        var sample = frames[hits[0]].Frame.Body;
        var hex = Convert.ToHexString(sample.AsSpan(0, Math.Min(24, sample.Length))) + (sample.Length > 24 ? "..." : "");
        return new OpcodeCandidate(name, opcode, hits.Count, total, confidence, evidence, hex);
    }

    private bool InCatalog(uint id) => id <= int.MaxValue && _ids.Contains((int)id);

    private bool IsLevelChanged(byte[] body)
    {
        if (body.Length != 23)
            return false;
        var level = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6));
        return level is >= 1 and <= 3 && InCatalog(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
    }

    private bool IsSoulList(byte[] body)
    {
        if (body.Length < 9 || body[0] is 0 or > 10 || body.Length != 1 + 8 * body[0])
            return false;
        for (var i = 0; i < body[0]; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1 + 8 * i));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5 + 8 * i));
            if (!InCatalog(id) || value is < 1 or > 200)
                return false;
        }
        return true;
    }

    /// <summary>True when every entry is 5/25/75 and follows (later in the stream, within a second) a level change of the same pet.</summary>
    private bool FollowsLevelChange(ObservedFrame frame, int index, List<(int Index, long Ticks, uint PetId)> levels)
    {
        var body = frame.Frame.Body;
        for (var i = 0; i < body[0]; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1 + 8 * i));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5 + 8 * i));
            if (Array.IndexOf(ConsumeValues, value) < 0)
                return false;
            if (!levels.Any(l => l.PetId == id && l.Index < index && frame.Ticks - l.Ticks <= ConsumeWindow.Ticks))
                return false;
        }
        return true;
    }

    private bool IsSnapshot(byte[] body, out int registered, out int progress)
    {
        registered = progress = 0;
        if (body.Length < 4 + 1 + 12 * 50)
            return false;
        var data = body.AsSpan();
        var pos = 4;
        if (Varint.TryRead(data[pos..], out var n, out var len) != VarintStatus.Ok || n is < 50 or > 400)
            return false;
        pos += len;
        if ((long)n * 12 > data.Length - pos)
            return false;

        int total = 0, known = 0;
        for (var i = 0; i < (int)n; i++, pos += 12)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            var again = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            var level = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 8)..]);
            if (id != again || level is < 1 or > 3)
                return false;
            total++;
            if (InCatalog(id)) known++;
        }

        if (Varint.TryRead(data[pos..], out var m, out len) != VarintStatus.Ok || (long)m * 8 > data.Length - pos - len)
            return false;
        pos += len;
        for (var i = 0; i < (int)m; i++, pos += 8)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            if (value >= 75)
                return false;
            total++;
            if (InCatalog(id)) known++;
        }

        registered = (int)n;
        progress = (int)m;
        return known >= 0.9 * total;
    }
}
