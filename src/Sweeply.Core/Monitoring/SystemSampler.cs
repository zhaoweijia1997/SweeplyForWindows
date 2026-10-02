using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Sweeply.Core.Monitoring;

/// <summary>Whole-system activity over the last sampling interval.</summary>
public readonly record struct SystemSample(
    double CpuPercent, double DiskWriteBytesPerSecond, double DownloadBytesPerSecond, double UploadBytesPerSecond)
{
    public double DiskReadBytesPerSecond { get; init; }

    /// <summary>Physical memory in use and installed (0 when Windows would not say).</summary>
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }

    /// <summary>The processors' speed right now (above the base speed when boosting); null when unknown.</summary>
    public double? CpuGigahertz { get; init; }

    /// <summary>Only when asked for (it costs more than the rest together); null otherwise or when unavailable.</summary>
    public GpuUsage? Gpu { get; init; }
}

/// <summary>
/// Reads CPU use and disk traffic from Windows performance counters, memory from the memory manager
/// and network traffic from the network adapters. Counters are added by their English names, which
/// work on every display language (the localized names differ, e.g. on Chinese Windows).
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private readonly IntPtr _query;
    private readonly IntPtr _cpu;
    private readonly IntPtr _diskWrite;
    private readonly IntPtr _diskRead;
    private readonly IntPtr _cpuPerformance;
    private readonly IntPtr _cpuFrequency;
    private IntPtr _gpuQuery;
    private IntPtr _gpuEngines;
    private bool _gpuStarted;
    // Listing the network adapters takes ~20 ms, reading one adapter's counters ~0.02 ms: the list is
    // kept, and made again when Windows says the network changed, or every 30 seconds to be sure.
    private static readonly TimeSpan AdapterListEvery = TimeSpan.FromSeconds(30);
    private NetworkInterface[] _adapters = Array.Empty<NetworkInterface>();
    private long _adaptersListedAt;
    private volatile bool _adaptersChanged = true;
    private Dictionary<string, (long Received, long Sent)> _network;
    private long _lastTimestamp;
    private bool _disposed;

    public SystemSampler()
    {
        if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
        if (_query != IntPtr.Zero)
        {
            // "% Processor Utility" matches Task Manager; older systems only have "% Processor Time".
            if (Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) != 0 &&
                Pdh.PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpu) != 0)
                _cpu = IntPtr.Zero;
            _diskWrite = Add(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            _diskRead = Add(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            // Speed now = base speed x "% Processor Performance" (over 100 while boosting), as Task Manager shows it.
            _cpuPerformance = Add(_query, @"\Processor Information(_Total)\% Processor Performance");
            _cpuFrequency = Add(_query, @"\Processor Information(_Total)\Processor Frequency");
            Pdh.PdhCollectQueryData(_query); // rates need two collections; this is the first
        }
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _network = ReadNetwork();
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _adaptersChanged = true;

    /// <summary>False when Windows would not provide the CPU counter; <see cref="SystemSample.CpuPercent"/> is then 0.</summary>
    public bool HasCpu => _cpu != IntPtr.Zero;

    /// <summary>False when Windows would not provide the disk counter (disk counters can be turned off).</summary>
    public bool HasDiskWrite => _diskWrite != IntPtr.Zero;

    /// <summary>Activity since the previous call (the first call covers the time since construction).</summary>
    /// <param name="includeGpu">Also read how busy the graphics cards are; the first such call gives none yet.</param>
    public SystemSample Sample(bool includeGpu = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double cpu = 0, diskWrite = 0, diskRead = 0;
        double? gigahertz = null;
        if (_query != IntPtr.Zero && Pdh.PdhCollectQueryData(_query) == 0)
        {
            cpu = Read(_cpu);
            diskWrite = Read(_diskWrite);
            diskRead = Read(_diskRead);
            double performance = Read(_cpuPerformance, Pdh.PDH_FMT_NOCAP100), baseMhz = Read(_cpuFrequency);
            if (performance > 0 && baseMhz > 0) gigahertz = baseMhz * performance / 100 / 1000;
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        // Per adapter, so an adapter that appears or disappears does not look like a burst of traffic.
        var network = ReadNetwork();
        long received = 0, sent = 0;
        foreach (var (id, current) in network)
        {
            if (!_network.TryGetValue(id, out var previous)) continue;
            received += Math.Max(0, current.Received - previous.Received);
            sent += Math.Max(0, current.Sent - previous.Sent);
        }
        _network = network;

        var memory = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        bool hasMemory = Native.GlobalMemoryStatusEx(ref memory);

        return new SystemSample(
            Math.Clamp(cpu, 0, 100),
            Math.Max(0, diskWrite),
            seconds <= 0 ? 0 : received / seconds,
            seconds <= 0 ? 0 : sent / seconds)
        {
            DiskReadBytesPerSecond = Math.Max(0, diskRead),
            MemoryTotalBytes = hasMemory ? (long)memory.TotalPhys : 0,
            MemoryUsedBytes = hasMemory ? (long)(memory.TotalPhys - memory.AvailPhys) : 0,
            CpuGigahertz = gigahertz,
            Gpu = includeGpu ? ReadGpu() : null,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged; // static events: would keep this object alive
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        if (_query != IntPtr.Zero) Pdh.PdhCloseQuery(_query);
        if (_gpuQuery != IntPtr.Zero) Pdh.PdhCloseQuery(_gpuQuery);
    }

    private static IntPtr Add(IntPtr query, string path) =>
        Pdh.PdhAddEnglishCounter(query, path, IntPtr.Zero, out IntPtr counter) == 0 ? counter : IntPtr.Zero;

    private static double Read(IntPtr counter, uint extraFormat = 0)
    {
        if (counter == IntPtr.Zero) return 0;
        return Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_DOUBLE | extraFormat, out _, out var value) == 0 && value.CStatus == 0
            ? value.DoubleValue
            : 0;
    }

    /// <summary>
    /// The graphics engines, in a query of their own: there is one counter per process and engine
    /// (several hundred), so they are only read while someone looks at them.
    /// </summary>
    private GpuUsage? ReadGpu()
    {
        if (!_gpuStarted)
        {
            _gpuStarted = true;
            if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != 0) _gpuQuery = IntPtr.Zero;
            else if ((_gpuEngines = Add(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage")) == IntPtr.Zero)
            {
                Pdh.PdhCloseQuery(_gpuQuery);
                _gpuQuery = IntPtr.Zero;
            }
            if (_gpuQuery != IntPtr.Zero) Pdh.PdhCollectQueryData(_gpuQuery); // the first of two collections
            return null;
        }
        if (_gpuQuery == IntPtr.Zero || Pdh.PdhCollectQueryData(_gpuQuery) != 0) return null;
        return GpuEngines.Busiest(ReadArray(_gpuEngines));
    }

    /// <summary>Every instance of a wildcard counter, skipping those without a valid value.</summary>
    private static List<(string Instance, double Value)> ReadArray(IntPtr counter)
    {
        var result = new List<(string, double)>();
        uint size = 0;
        uint status = Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        if (status != Pdh.PDH_MORE_DATA || size == 0) return result;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.PDH_FMT_DOUBLE, ref size, out uint count, buffer) != 0) return result;
            int itemSize = IntPtr.Size + 16; // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then CStatus + padding + double
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * itemSize;
                uint itemStatus = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                if (itemStatus > 1) continue; // only PDH_CSTATUS_VALID_DATA and PDH_CSTATUS_NEW_DATA
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                if (name is not null) result.Add((name, value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    /// <summary>
    /// Which adapters to add up, so that every byte counts once. Not the layers Windows stacks on a card
    /// (WFP and QoS filters: they have no IP stack and repeat their card's counters, five times over for
    /// a typical Wi-Fi card); and only real cards, because traffic through a VPN or proxy tunnel, or a
    /// virtual machine's, also passes a real card. With no real card up (e.g. one bound to a Hyper-V
    /// switch), the adapters that do have an IP stack.
    /// </summary>
    internal static List<T> Counted<T>(IReadOnlyList<(T Item, int Index, bool Hardware)> adapters)
    {
        var withStack = adapters.Where(a => a.Index > 0).ToList();
        var hardware = withStack.Where(a => a.Hardware).ToList();
        return (hardware.Count > 0 ? hardware : withStack).Select(a => a.Item).ToList();
    }

    private Dictionary<string, (long Received, long Sent)> ReadNetwork()
    {
        long now = Environment.TickCount64;
        if (_adaptersChanged || now - _adaptersListedAt >= (long)AdapterListEvery.TotalMilliseconds)
        {
            _adaptersChanged = false;
            _adaptersListedAt = now;
            try
            {
                var up = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(a => a.OperationalStatus == OperationalStatus.Up &&
                                a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .Select(a => (Item: a, Index: NetworkAdapters.InterfaceIndex(a)))
                    .ToList();
                _adapters = Counted(up.Select(x => (x.Item, x.Index, x.Index > 0 && NetworkAdapters.IsHardwareInterface(x.Index))).ToList()).ToArray();
            }
            catch (NetworkInformationException) { _adapters = Array.Empty<NetworkInterface>(); }
        }

        var result = new Dictionary<string, (long Received, long Sent)>();
        foreach (var adapter in _adapters)
        {
            try
            {
                var stats = adapter.GetIPStatistics(); // asks Windows afresh each time
                result[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
            }
            catch (NetworkInformationException) { _adaptersChanged = true; } // gone: list them again next time
        }
        return result;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    }

    private static class Pdh
    {
        public const uint PDH_FMT_DOUBLE = 0x00000200;
        public const uint PDH_FMT_NOCAP100 = 0x00008000;
        public const uint PDH_MORE_DATA = 0x800007D2;

        [StructLayout(LayoutKind.Explicit)]
        public struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);
    }
}
