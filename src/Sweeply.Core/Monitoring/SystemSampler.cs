using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Sweeply.Core.Monitoring;

/// <summary>Whole-system activity over the last sampling interval.</summary>
public readonly record struct SystemSample(
    double CpuPercent, double DiskWriteBytesPerSecond, double DownloadBytesPerSecond, double UploadBytesPerSecond);

/// <summary>
/// Reads CPU use and disk writes from Windows performance counters and network traffic from the
/// network adapters. Counters are added by their English names, which work on every display
/// language (the localized names differ, e.g. on Chinese Windows).
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private readonly IntPtr _query;
    private readonly IntPtr _cpu;
    private readonly IntPtr _diskWrite;
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
            if (Pdh.PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWrite) != 0)
                _diskWrite = IntPtr.Zero;
            Pdh.PdhCollectQueryData(_query); // rates need two collections; this is the first
        }
        _network = ReadNetwork();
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>False when Windows would not provide the CPU counter; <see cref="SystemSample.CpuPercent"/> is then 0.</summary>
    public bool HasCpu => _cpu != IntPtr.Zero;

    /// <summary>False when Windows would not provide the disk counter (disk counters can be turned off).</summary>
    public bool HasDiskWrite => _diskWrite != IntPtr.Zero;

    /// <summary>Activity since the previous call (the first call covers the time since construction).</summary>
    public SystemSample Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double cpu = 0, diskWrite = 0;
        if (_query != IntPtr.Zero && Pdh.PdhCollectQueryData(_query) == 0)
        {
            cpu = Read(_cpu);
            diskWrite = Read(_diskWrite);
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

        return seconds <= 0
            ? new SystemSample(cpu, diskWrite, 0, 0)
            : new SystemSample(Math.Clamp(cpu, 0, 100), Math.Max(0, diskWrite), received / seconds, sent / seconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero) Pdh.PdhCloseQuery(_query);
    }

    private static double Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return 0;
        return Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_DOUBLE, out _, out var value) == 0 && value.CStatus == 0
            ? value.DoubleValue
            : 0;
    }

    private static Dictionary<string, (long Received, long Sent)> ReadNetwork()
    {
        var result = new Dictionary<string, (long Received, long Sent)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = adapter.GetIPStatistics();
                result[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    private static class Pdh
    {
        public const uint PDH_FMT_DOUBLE = 0x00000200;

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

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);
    }
}
