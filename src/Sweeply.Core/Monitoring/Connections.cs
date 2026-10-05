using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Sweeply.Core.Monitoring;

public enum TransportProtocol
{
    Tcp,
    Udp,
}

/// <summary>The states of a TCP connection, numbered as Windows numbers them (MIB_TCP_STATE).</summary>
public enum TcpConnectionState
{
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
}

/// <summary>
/// One TCP connection or UDP endpoint: the program that owns it, where it is on this PC and, for TCP, where
/// it leads. UDP endpoints have no remote side and no state.
/// </summary>
public readonly record struct Connection(
    TransportProtocol Protocol, IPAddress LocalAddress, int LocalPort, IPAddress? RemoteAddress, int RemotePort,
    TcpConnectionState? State, int ProcessId)
{
    /// <summary>Waiting for others to connect: a listening TCP port, or any UDP endpoint.</summary>
    public bool IsListening => Protocol == TransportProtocol.Udp || State == TcpConnectionState.Listen;

    /// <summary>Gone already: Windows keeps closed connections for a while (TIME_WAIT) without a program.</summary>
    public bool IsClosed => State is TcpConnectionState.TimeWait or TcpConnectionState.Closed or TcpConnectionState.DeleteTcb;

    /// <summary>Both ends on this PC (127.0.0.1, ::1).</summary>
    public bool IsLoopback => IPAddress.IsLoopback(LocalAddress) && (RemoteAddress is null || IPAddress.IsLoopback(RemoteAddress));

    /// <summary>The same connection from one read to the next.</summary>
    public string Key => $"{Protocol}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}|{ProcessId}";
}

/// <summary>
/// Windows' table of TCP connections and UDP endpoints with the program that owns each (what netstat -ano
/// and TCPView show). Needs no administrator rights and sends nothing.
/// </summary>
public static class ConnectionTable
{
    private const int TcpTableOwnerPidAll = 5, UdpTableOwnerPid = 1;
    private const int AfInet = 2, AfInet6 = 23;

    public static List<Connection> Read()
    {
        var result = new List<Connection>();
        if (Table(isTcp: true, AfInet) is { } tcp4) result.AddRange(ParseTcp4(tcp4));
        if (Table(isTcp: true, AfInet6) is { } tcp6) result.AddRange(ParseTcp6(tcp6));
        if (Table(isTcp: false, AfInet) is { } udp4) result.AddRange(ParseUdp4(udp4));
        if (Table(isTcp: false, AfInet6) is { } udp6) result.AddRange(ParseUdp6(udp6));
        return result;
    }

    /// <summary>A port as Windows stores it in these tables: in network byte order, in the low two bytes of a DWORD.</summary>
    private static int Port(ReadOnlySpan<byte> dword) => dword[0] << 8 | dword[1];

    private static IPAddress V6(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> scope)
    {
        uint scopeId = BinaryPrimitives.ReadUInt32LittleEndian(scope);
        var address = new IPAddress(bytes.ToArray(), scopeId);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>MIB_TCPTABLE_OWNER_PID: a count, then rows of state, local address, local port, remote address, remote port, process.</summary>
    internal static IEnumerable<Connection> ParseTcp4(byte[] table)
    {
        const int rowSize = 24;
        int count = Count(table, rowSize);
        for (int i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + i * rowSize, rowSize);
            var state = (TcpConnectionState)BinaryPrimitives.ReadInt32LittleEndian(row);
            yield return new Connection(TransportProtocol.Tcp, new IPAddress(row.Slice(4, 4)), Port(row[8..]),
                new IPAddress(row.Slice(12, 4)), state == TcpConnectionState.Listen ? 0 : Port(row[16..]), state,
                BinaryPrimitives.ReadInt32LittleEndian(row[20..]));
        }
    }

    /// <summary>MIB_TCP6TABLE_OWNER_PID: local address, scope, port; remote address, scope, port; state; process.</summary>
    internal static IEnumerable<Connection> ParseTcp6(byte[] table)
    {
        const int rowSize = 56;
        int count = Count(table, rowSize);
        for (int i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + i * rowSize, rowSize);
            var state = (TcpConnectionState)BinaryPrimitives.ReadInt32LittleEndian(row[48..]);
            yield return new Connection(TransportProtocol.Tcp, V6(row[..16], row[16..]), Port(row[20..]),
                V6(row.Slice(24, 16), row[40..]), state == TcpConnectionState.Listen ? 0 : Port(row[44..]), state,
                BinaryPrimitives.ReadInt32LittleEndian(row[52..]));
        }
    }

    /// <summary>MIB_UDPTABLE_OWNER_PID: local address, local port, process.</summary>
    internal static IEnumerable<Connection> ParseUdp4(byte[] table)
    {
        const int rowSize = 12;
        int count = Count(table, rowSize);
        for (int i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + i * rowSize, rowSize);
            yield return new Connection(TransportProtocol.Udp, new IPAddress(row[..4]), Port(row[4..]), null, 0, null,
                BinaryPrimitives.ReadInt32LittleEndian(row[8..]));
        }
    }

    /// <summary>MIB_UDP6TABLE_OWNER_PID: local address, scope, port, process.</summary>
    internal static IEnumerable<Connection> ParseUdp6(byte[] table)
    {
        const int rowSize = 28;
        int count = Count(table, rowSize);
        for (int i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + i * rowSize, rowSize);
            yield return new Connection(TransportProtocol.Udp, V6(row[..16], row[16..]), Port(row[20..]), null, 0, null,
                BinaryPrimitives.ReadInt32LittleEndian(row[24..]));
        }
    }

    /// <summary>The number of rows, never more than the buffer holds.</summary>
    private static int Count(byte[] table, int rowSize) =>
        table.Length < 4 ? 0 : (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)((table.Length - 4) / rowSize));

    /// <summary>The whole table as bytes; asked twice when it grew between asking for its size and reading it.</summary>
    private static byte[]? Table(bool isTcp, int family)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int size = 0;
            Get(isTcp, IntPtr.Zero, ref size, family);
            if (size <= 0) return null;
            size += 4096; // room for a few more rows
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint status = Get(isTcp, buffer, ref size, family);
                if (status == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                if (status != 0) return null;
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private static uint Get(bool isTcp, IntPtr buffer, ref int size, int family) => isTcp
        ? GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0)
        : GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
}

/// <summary>A program as the connection list shows it: "chrome.exe", where it is, and for svchost.exe the services it runs.</summary>
public sealed record ProgramInfo(int ProcessId, string Name, string? Path, IReadOnlyList<ServiceInfo> Services);

/// <summary>A Windows service: its short name ("wuauserv") and the name Windows shows for it, in Windows' language ("Windows Update").</summary>
public sealed record ServiceInfo(string Name, string DisplayName);

/// <summary>
/// Names programs by process id, with what Windows lets a normal user see: the full path for most programs
/// (PROCESS_QUERY_LIMITED_INFORMATION), the name only for protected ones, and the services that run inside each
/// svchost.exe. Names are kept for a minute, since process ids can be reused later.
/// </summary>
public sealed class ProgramNames
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(1);
    private readonly Dictionary<int, (ProgramInfo Info, DateTime Utc)> _cache = new();
    private Dictionary<int, List<ServiceInfo>> _services = new();
    private DateTime _servicesUtc = DateTime.MinValue;

    public ProgramInfo Get(int processId)
    {
        var now = DateTime.UtcNow;
        lock (_cache)
        {
            if (_cache.TryGetValue(processId, out var cached) && now - cached.Utc < KeepFor) return cached.Info;
        }
        var info = Look(processId, now);
        lock (_cache) _cache[processId] = (info, now);
        return info;
    }

    private ProgramInfo Look(int processId, DateTime now)
    {
        if (processId == 0) return new ProgramInfo(0, "", null, Array.Empty<ServiceInfo>());
        if (processId == 4) return new ProgramInfo(4, "System", null, Array.Empty<ServiceInfo>());
        string? path = ImagePath(processId);
        string name = path is not null ? System.IO.Path.GetFileName(path) : FallbackName(processId);
        IReadOnlyList<ServiceInfo> services = Array.Empty<ServiceInfo>();
        if (name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            lock (_cache)
            {
                if (now - _servicesUtc > KeepFor)
                {
                    _services = ServicesByProcess();
                    _servicesUtc = now;
                }
                if (_services.TryGetValue(processId, out var list)) services = list;
            }
        }
        return new ProgramInfo(processId, name, path, services);
    }

    internal static string? ImagePath(int processId)
    {
        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Protected processes keep their path to themselves; the process list still has the name.</summary>
    private static string FallbackName(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return ""; } // ended meanwhile
    }

    /// <summary>The services that run in each process, from the service manager (readable by any user), with the names it shows for them.</summary>
    private static Dictionary<int, List<ServiceInfo>> ServicesByProcess()
    {
        var result = new Dictionary<int, List<ServiceInfo>>();
        IntPtr manager = OpenSCManagerW(null, null, 0x0004 /* SC_MANAGER_ENUMERATE_SERVICE */);
        if (manager == IntPtr.Zero) return result;
        try
        {
            int needed = 0, returned = 0, resume = 0;
            const int infoLevel = 0 /* SC_ENUM_PROCESS_INFO */, win32 = 0x30 /* SERVICE_WIN32 */, active = 0x1 /* SERVICE_ACTIVE */;
            EnumServicesStatusExW(manager, infoLevel, win32, active, IntPtr.Zero, 0, ref needed, ref returned, ref resume, null);
            if (needed <= 0) return result;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(manager, infoLevel, win32, active, buffer, needed, ref needed, ref returned, ref resume, null)) return result;
                // ENUM_SERVICE_STATUS_PROCESS: service name, display name, then SERVICE_STATUS_PROCESS (nine DWORDs, the
                // process id is the 8th); padded to pointer size (56 bytes on 64-bit Windows).
                int size = (IntPtr.Size * 2 + 36 + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
                for (int i = 0; i < returned; i++)
                {
                    IntPtr entry = buffer + i * size;
                    string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry));
                    string? display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, IntPtr.Size));
                    int processId = Marshal.ReadInt32(entry, IntPtr.Size * 2 + 28);
                    if (name is null || processId == 0) continue;
                    if (!result.TryGetValue(processId, out var list)) result[processId] = list = new List<ServiceInfo>();
                    list.Add(new ServiceInfo(name, string.IsNullOrWhiteSpace(display) ? name : display));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, int access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusExW(IntPtr manager, int infoLevel, int serviceType, int serviceState, IntPtr services,
        int bufferSize, ref int bytesNeeded, ref int servicesReturned, ref int resumeHandle, string? groupName);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
