using System.Runtime.InteropServices;

namespace Sweeply.Core.Monitoring;

/// <summary>
/// Graphics adapters as Windows' graphics kernel lists them (the D3DKMT calls Task Manager uses);
/// needs neither a driver nor administrator rights.
/// </summary>
internal static class GraphicsAdapters
{
    private const int AdapterRegistryInfo = 8;  // KMTQAITYPE_ADAPTERREGISTRYINFO
    private const int AdapterPerfData = 62;     // KMTQAITYPE_ADAPTERPERFDATA (Windows 10 2004 and later)
    private const int RegistryInfoLength = 4 * 260 * 2; // four WCHAR[MAX_PATH] strings

    /// <summary>An opened adapter: its handle, and its LUID (the id in "GPU Engine" counter names).</summary>
    public readonly record struct OpenAdapter(uint Handle, long Luid);

    /// <summary>Opens every adapter; each handle must be closed with <see cref="Close"/>.</summary>
    public static List<OpenAdapter> OpenAll()
    {
        var result = new List<OpenAdapter>();
        var list = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref list) != 0 || list.NumAdapters == 0) return result;

        int size = Marshal.SizeOf<AdapterInfo>();
        IntPtr buffer = Marshal.AllocHGlobal(size * (int)list.NumAdapters);
        try
        {
            list.Adapters = buffer;
            if (D3DKMTEnumAdapters2(ref list) != 0) return result; // e.g. an adapter was just added; try again later
            for (int i = 0; i < (int)list.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<AdapterInfo>(buffer + i * size);
                result.Add(new OpenAdapter(info.Handle, Luid(info.LuidHighPart, info.LuidLowPart)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    /// <summary>Every adapter's name by LUID, e.g. "Intel(R) Arc(TM) 130T GPU (16GB)". Virtual display adapters have none.</summary>
    public static Dictionary<long, string> Names()
    {
        var names = new Dictionary<long, string>();
        foreach (var adapter in OpenAll())
        {
            if (Name(adapter.Handle) is { Length: > 0 } name) names[adapter.Luid] = name;
            Close(adapter.Handle);
        }
        return names;
    }

    public static long Luid(int highPart, uint lowPart) => ((long)highPart << 32) | lowPart;

    /// <summary>The adapter's temperature in tenths of a degree, or null when the query failed.</summary>
    public static uint? Temperature(uint adapter)
    {
        int size = Marshal.SizeOf<AdapterPerfDataResult>();
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new AdapterPerfDataResult(), data, false);
            var query = new QueryAdapterInfo { Adapter = adapter, Type = AdapterPerfData, Data = data, DataSize = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;
            return Marshal.PtrToStructure<AdapterPerfDataResult>(data).Temperature;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>The adapter's name as Device Manager shows it.</summary>
    public static string? Name(uint adapter)
    {
        IntPtr data = Marshal.AllocHGlobal(RegistryInfoLength);
        try
        {
            var query = new QueryAdapterInfo { Adapter = adapter, Type = AdapterRegistryInfo, Data = data, DataSize = RegistryInfoLength };
            return D3DKMTQueryAdapterInfo(ref query) == 0 ? Marshal.PtrToStringUni(data)?.Trim() : null;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public static void Close(uint adapter) => D3DKMTCloseAdapter(ref adapter);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Handle;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public IntPtr Data;
        public uint DataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfDataResult
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PcieBandwidth;
        public uint FanRpm, Power, Temperature; // Temperature in tenths of a degree Celsius
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 adapters);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref uint adapter); // D3DKMT_CLOSEADAPTER holds just the handle
}
