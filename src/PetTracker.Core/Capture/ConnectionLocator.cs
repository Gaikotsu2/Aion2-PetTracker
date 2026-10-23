using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PetTracker.Core.Diagnostics;

namespace PetTracker.Core.Capture;

public readonly record struct TcpTableRow(int Pid, uint State, IPEndPoint Local, IPEndPoint Remote);

/// <summary>
/// Maps the game process to its established TCP connections using only the OS socket table (GetExtendedTcpTable)
/// and a process-name lookup that reads nothing but the PID. Polls so reconnects and the lobby-to-world redirect show up.
/// </summary>
public sealed class ConnectionLocator(string processName = "AION2") : IDisposable
{
    private const uint Established = 5;

    /// <summary>Web/launcher/CDN traffic: never a game server.</summary>
    public static IReadOnlySet<int> IgnoredRemotePorts { get; } = new HashSet<int> { 443, 80 };
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IReadOnlyList<GameConnection> _current = [];
    private int _lastPidCount = -1;
    private int _pollErrors;

    public string ProcessName { get; } = processName;
    public IReadOnlyList<GameConnection> Current => _current;

    /// <summary>Raised from the polling thread when the set of connections changes (including becoming empty).</summary>
    public event Action<IReadOnlyList<GameConnection>>? Changed;

    public IReadOnlyList<int> FindPids()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try { return processes.Select(p => p.Id).ToArray(); }
        finally { Array.ForEach(processes, p => p.Dispose()); }
    }

    public IReadOnlyList<GameConnection> Poll()
    {
        var pids = FindPids();
        if (pids.Count != _lastPidCount)
        {
            DiagLog.Info("Locator", pids.Count == 0
                ? $"process '{ProcessName}' not running (was {_lastPidCount} instance(s))"
                : $"process '{ProcessName}' found: {pids.Count} instance(s)");
            _lastPidCount = pids.Count;
        }
        return pids.Count == 0 ? [] : Select(TcpTable.Read(), pids);
    }

    public static IReadOnlyList<GameConnection> Select(IEnumerable<TcpTableRow> rows, IReadOnlyCollection<int> pids) =>
        rows.Where(r => r.State == Established && pids.Contains(r.Pid) && !IgnoredRemotePorts.Contains(r.Remote.Port) && IsRoutable(r.Remote.Address))
            .Select(r => new GameConnection(r.Pid, r.Local, r.Remote))
            .Distinct()
            .OrderBy(c => c.Remote.ToString(), StringComparer.Ordinal)
            .ToArray();

    public void Start(TimeSpan? interval = null)
    {
        if (_loop is not null)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(interval ?? TimeSpan.FromSeconds(1), _cts.Token));
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try { Publish(Poll()); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Logged for the first failure and then every 60th, so a persistent fault is visible without flooding the file.
                if (_pollErrors++ % 60 == 0)
                    DiagLog.Warn("Locator", $"TCP-table poll failed (failure #{_pollErrors})", ex);
                Publish([]);
            }
        }
        while (await WaitAsync(timer, token));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }

    private void Publish(IReadOnlyList<GameConnection> latest)
    {
        if (_current.SequenceEqual(latest))
            return;
        _current = latest;
        DiagLog.Info("Locator", $"game connections changed: {latest.Count} established ({string.Join("; ", latest.Select(c => c.Remote))})");
        Changed?.Invoke(latest);
    }

    private static bool IsRoutable(IPAddress address) =>
        !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts?.Dispose();
    }
}

internal static partial class TcpTable
{
    private const int TcpTableOwnerPidAll = 5;
    private const uint InsufficientBuffer = 122;

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, int order, int addressFamily, int tableClass, uint reserved);

    public static List<TcpTableRow> Read()
    {
        var rows = new List<TcpTableRow>();
        ReadFamily((int)AddressFamily.InterNetwork, 24, ParseV4, rows);
        ReadFamily((int)AddressFamily.InterNetworkV6, 56, ParseV6, rows);
        return rows;
    }

    private delegate TcpTableRow RowParser(ReadOnlySpan<byte> row);

    private static void ReadFamily(int family, int rowSize, RowParser parse, List<TcpTableRow> rows)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var result = GetExtendedTcpTable(buffer, ref size, 0, family, TcpTableOwnerPidAll, 0);
                if (result == 0)
                    break;
                if (result != InsufficientBuffer)
                    return;
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
            }
            if (buffer == IntPtr.Zero)
                return;

            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            for (var i = 0; i < count && 4 + (i + 1) * rowSize <= size; i++)
                rows.Add(parse(bytes.AsSpan(4 + i * rowSize, rowSize)));
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (ports are network order in the low 16 bits).
    private static TcpTableRow ParseV4(ReadOnlySpan<byte> r) => new(
        (int)BinaryPrimitives.ReadUInt32LittleEndian(r[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(r),
        new IPEndPoint(new IPAddress(r.Slice(4, 4)), Port(r[8..])),
        new IPEndPoint(new IPAddress(r.Slice(12, 4)), Port(r[16..])));

    // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid.
    private static TcpTableRow ParseV6(ReadOnlySpan<byte> r) => new(
        (int)BinaryPrimitives.ReadUInt32LittleEndian(r[52..]),
        BinaryPrimitives.ReadUInt32LittleEndian(r[48..]),
        new IPEndPoint(TcpPacketParser.Normalize(new IPAddress(r[..16])), Port(r[20..])),
        new IPEndPoint(TcpPacketParser.Normalize(new IPAddress(r.Slice(24, 16))), Port(r[44..])));

    private static int Port(ReadOnlySpan<byte> r) => (r[0] << 8) | r[1];
}
