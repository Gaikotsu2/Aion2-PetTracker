using System.Net;

namespace PetTracker.Core.Capture;

public delegate Direction DirectionClassifier(IPEndPoint source, IPEndPoint destination, ReadOnlySpan<byte> payload);

/// <summary>
/// Fallback when the TCP table finds nothing: any flow whose payload contains the heartbeat signature is taken to be
/// the game's server-to-client stream. Only a byte pattern is matched; nothing is decrypted.
/// </summary>
public sealed class HeartbeatLocker(byte[]? signature = null, int maxLocks = 4)
{
    private readonly byte[] _signature = signature ?? [0x0E, 0x00, 0x36];
    private readonly List<FlowKey> _locked = [];
    private readonly object _gate = new();

    /// <summary>Raised the first time a flow is locked.</summary>
    public event Action<FlowKey>? Locked;

    public IReadOnlyList<FlowKey> LockedFlows
    {
        get { lock (_gate) return _locked.ToArray(); }
    }

    public void Reset()
    {
        lock (_gate)
            _locked.Clear();
    }

    public Direction Classify(IPEndPoint source, IPEndPoint destination, ReadOnlySpan<byte> payload)
    {
        var key = new FlowKey(source, destination);
        lock (_gate)
        {
            if (_locked.Contains(key)) return Direction.ServerToClient;
            if (_locked.Contains(key.Reversed)) return Direction.ClientToServer;
        }

        if (payload.Length < _signature.Length || payload.IndexOf(_signature) < 0)
            return Direction.Unknown;

        lock (_gate)
        {
            if (_locked.Count >= maxLocks)
                _locked.RemoveAt(0);
            _locked.Add(key);
        }
        Locked?.Invoke(key);
        return Direction.ServerToClient;
    }
}
