using System.Net;
using PetTracker.Core.Diagnostics;
using SharpPcap;
using SharpPcap.LibPcap;

namespace PetTracker.Core.Capture;

public sealed class NpcapNotAvailableException(string message, Exception? inner = null) : Exception(message, inner)
{
    public const string InstallHint = "Install Npcap from https://npcap.com (WinPcap API-compatible mode).";
}

public sealed record AdapterInfo(string Name, string Description, IReadOnlyList<IPAddress> Addresses)
{
    public override string ToString() =>
        $"{Description} [{string.Join(", ", Addresses)}] ({Name})";
}

/// <summary>
/// Receive-only Npcap capture with a stable BPF filter (all TCP except web ports, plus optional exclusions), opened once
/// so no packet is missed while the game connection is being identified. Direction is taken from the adapter's own
/// addresses: traffic addressed to us is server-to-client. Picking the game's flows is <see cref="FlowGate"/>'s job.
/// </summary>
public sealed class NpcapSource(string? adapterOverride = null, IEnumerable<int>? excludedPorts = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<LibPcapLiveDevice> _open = [];
    private readonly Dictionary<string, HashSet<IPAddress>> _locals = [];
    private readonly int[] _excluded = (excludedPorts ?? []).ToArray();
    private long _packets;
    private bool _disposed;

    public event Action<TcpSegment>? SegmentCaptured;

    public long PacketsSeen => Interlocked.Read(ref _packets);
    public string Description { get; private set; } = "idle";

    public static IReadOnlyList<AdapterInfo> ListAdapters() =>
        GetDevices().Select(d => new AdapterInfo(d.Name, d.Description ?? d.Interface?.FriendlyName ?? d.Name, AddressesOf(d))).ToList();

    /// <summary>Throws <see cref="NpcapNotAvailableException"/> with install instructions when Npcap is missing or unusable.</summary>
    public static void EnsureAvailable()
    {
        if (GetDevices().Count == 0)
        {
            DiagLog.Error("Capture", "Npcap loaded but lists no capture devices");
            throw new NpcapNotAvailableException($"No capture adapters were found. {NpcapNotAvailableException.InstallHint}");
        }
    }

    /// <summary>Opens the adapters. Safe to call again; it does nothing once capturing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _open.Count > 0)
                return;

            var devices = SelectDevices();
            var filter = BuildFilter(_excluded);
            DiagLog.Info("Capture", $"opening {devices.Count} adapter(s); filter: {filter}");
            foreach (var device in devices)
                Open(device, filter);

            Description = $"{(devices.Count == 0 ? "no adapter" : string.Join(" + ", devices.Select(d => d.Description ?? d.Name)))} | filter: {filter}";
        }
    }

    private List<LibPcapLiveDevice> SelectDevices()
    {
        var all = GetDevices();
        DiagLog.Info("Capture", $"{all.Count} capture device(s) listed by Npcap; adapter override {(string.IsNullOrWhiteSpace(adapterOverride) ? "not set" : "set")}");
        foreach (var d in all)
        {
            var addresses = AddressesOf(d);
            DiagLog.Info("Capture", $"  device '{d.Description ?? d.Interface?.FriendlyName ?? "?"}': " +
                $"{addresses.Count(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)} IPv4, " +
                $"{addresses.Count(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)} IPv6, loopback {addresses.Any(IPAddress.IsLoopback)}");
        }

        if (!string.IsNullOrWhiteSpace(adapterOverride))
        {
            var forced = all.Where(d => MatchesOverride(d, adapterOverride)).ToList();
            if (forced.Count == 0)
                throw new InvalidOperationException($"No adapter matches '{adapterOverride}'. Run 'pt-research adapters' to list them.");
            return forced;
        }

        return all.Where(d => AddressesOf(d).Any(a => !IPAddress.IsLoopback(a))).ToList();
    }

    private static bool MatchesOverride(LibPcapLiveDevice d, string text) =>
        d.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (d.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
        || AddressesOf(d).Any(a => a.ToString() == text);

    /// <summary>Web/launcher/CDN ports are always excluded; the game never uses them.</summary>
    public static string BuildFilter(IEnumerable<int>? excludedPorts = null)
    {
        var ports = ConnectionLocator.IgnoredRemotePorts.Order().Concat((excludedPorts ?? []).Where(p => !ConnectionLocator.IgnoredRemotePorts.Contains(p)).Distinct());
        return "tcp and " + string.Join(" and ", ports.Select(p => $"not port {p}"));
    }

    private void Open(LibPcapLiveDevice device, string filter)
    {
        device.OnPacketArrival += OnPacket;
        try
        {
            device.Open(new DeviceConfiguration { Mode = DeviceModes.MaxResponsiveness, ReadTimeout = 100, Snaplen = 65535 });
            device.Filter = filter;
            _locals[device.Name] = AddressesOf(device).ToHashSet();
            device.StartCapture();
            _open.Add(device);
            DiagLog.Info("Capture", $"  capturing on '{device.Description ?? device.Name}' (link type {device.LinkType})");
        }
        catch (Exception ex) when (ex is PcapException or DllNotFoundException or InvalidOperationException)
        {
            DiagLog.Error("Capture", $"could not open adapter '{device.Description ?? device.Name}'", ex);
            device.OnPacketArrival -= OnPacket;
            throw new NpcapNotAvailableException($"Could not open adapter '{device.Description ?? device.Name}': {ex.Message}", ex);
        }
    }

    private void OnPacket(object sender, PacketCapture e)
    {
        Interlocked.Increment(ref _packets);
        var device = (LibPcapLiveDevice)sender;
        var frame = e.Data;
        if (!TcpPacketParser.TryParse(frame, (int)device.LinkType, out var tcp))
            return;

        var source = new IPEndPoint(tcp.SourceAddress, tcp.SourcePort);
        var destination = new IPEndPoint(tcp.DestinationAddress, tcp.DestinationPort);
        var payload = frame.Slice(tcp.PayloadOffset, tcp.PayloadLength);

        var direction = DirectionFor(device.Name, tcp.SourceAddress, tcp.DestinationAddress);
        if (direction == Direction.Unknown)
            return;

        // Client-to-server payload is encrypted and never needed, so it is not even copied.
        var data = direction == Direction.ServerToClient ? payload.ToArray() : [];
        SegmentCaptured?.Invoke(new TcpSegment(DateTime.UtcNow.Ticks, source, destination, tcp.Sequence, tcp.Flags, data, direction));
    }

    private Direction DirectionFor(string device, IPAddress source, IPAddress destination)
    {
        if (!_locals.TryGetValue(device, out var locals))
            return Direction.Unknown;
        var fromUs = locals.Contains(source);
        var toUs = locals.Contains(destination);
        return toUs && !fromUs ? Direction.ServerToClient
            : fromUs && !toUs ? Direction.ClientToServer
            : Direction.Unknown;
    }

    private static IReadOnlyList<LibPcapLiveDevice> GetDevices()
    {
        try
        {
            return LibPcapLiveDeviceList.Instance.ToList();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or PcapException or EntryPointNotFoundException)
        {
            DiagLog.Error("Capture", "Npcap could not be loaded", ex);
            throw new NpcapNotAvailableException($"Npcap is not installed or could not be loaded. {NpcapNotAvailableException.InstallHint}", ex);
        }
    }

    private static List<IPAddress> AddressesOf(LibPcapLiveDevice d) =>
        (d.Interface?.Addresses ?? [])
            .Select(a => a.Addr?.ipAddress)
            .OfType<IPAddress>()
            .Select(TcpPacketParser.Normalize)
            .ToList();

    private void CloseDevices()
    {
        foreach (var device in _open)
        {
            try
            {
                device.OnPacketArrival -= OnPacket;
                device.StopCapture();
                device.Close();
            }
            catch (PcapException) { }
        }
        _open.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseDevices();
        }
    }
}
