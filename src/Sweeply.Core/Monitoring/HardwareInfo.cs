using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Sweeply.Core.Monitoring;

/// <summary>One memory module: size, kind (e.g. "DDR5"), speed in MT/s, maker and part number; unknown parts are empty or 0.</summary>
public sealed record MemoryModule(long Bytes, string Type, int SpeedMts, string Manufacturer, string PartNumber);

/// <summary>One drive as Windows lists it: name, size (null when unknown) and how it is connected ("NVMe", "USB"…).</summary>
public sealed record DriveSummary(string Name, long? Bytes, string Bus);

/// <summary>What this PC is made of.</summary>
public sealed record HardwareSummary
{
    /// <summary>The maker and model, e.g. "LENOVO XiaoXinPro 16c IAH10 (83ND)"; empty when the firmware does not say.</summary>
    public string ComputerModel { get; init; } = "";

    /// <summary>e.g. "Windows 11 Pro 25H2 (26300.6725)".</summary>
    public string Windows { get; init; } = "";
    public string ProcessorName { get; init; } = "";
    public int Cores { get; init; }

    /// <summary>Hybrid processors: the fast and the efficient cores; both 0 when all cores are alike.</summary>
    public int PerformanceCores { get; init; }
    public int EfficientCores { get; init; }
    public int LogicalProcessors { get; init; }

    /// <summary>Installed memory (not just what Windows can use).</summary>
    public long MemoryBytes { get; init; }
    public IReadOnlyList<MemoryModule> MemoryModules { get; init; } = Array.Empty<MemoryModule>();
    public IReadOnlyList<string> Graphics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DriveSummary> Drives { get; init; } = Array.Empty<DriveSummary>();
}

/// <summary>
/// Reads what this PC is made of without administrator rights: the firmware's SMBIOS table (model,
/// memory modules), the processor topology, the registry (processor and Windows names), the graphics
/// kernel (graphics cards) and the storage driver (drives). Serial numbers are never read.
/// </summary>
public static class HardwareInfo
{
    public static HardwareSummary Read()
    {
        var summary = new HardwareSummary
        {
            ProcessorName = SystemInfo.ProcessorName(),
            LogicalProcessors = SystemInfo.LogicalProcessors,
            Windows = Attempt(WindowsName, ""),
        };

        var structures = Attempt(() => Smbios.ReadRaw() is { } raw ? Smbios.Parse(raw) : new List<Smbios.Structure>(), new List<Smbios.Structure>());
        var modules = Smbios.MemoryModules(structures);
        var (cores, performance, efficient) = Attempt(() => CoreCounts(EfficiencyClasses()), (0, 0, 0));
        long installed = Attempt(InstalledMemory, 0L);

        return summary with
        {
            ComputerModel = Smbios.ComputerModel(structures),
            Cores = cores,
            PerformanceCores = performance,
            EfficientCores = efficient,
            MemoryModules = modules,
            MemoryBytes = installed > 0 ? installed : modules.Sum(m => m.Bytes),
            Graphics = Attempt(() => GraphicsAdapters.Names().Values
                .Where(name => !name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)) // basic render / remote display adapters
                .Distinct().ToList(), new List<string>()),
            Drives = Attempt(() => StorageQuery.ListDevices().Select(d =>
            {
                using var handle = StorageQuery.Open(d.Number);
                return new DriveSummary(d.Name, handle.IsInvalid ? null : StorageQuery.Size(handle), StorageQuery.BusName(d.BusType));
            }).ToList(), new List<DriveSummary>()),
        };
    }

    /// <summary>Cores counted by efficiency class: with two or more classes, the highest class are the performance cores.</summary>
    internal static (int Cores, int Performance, int Efficient) CoreCounts(IReadOnlyCollection<int> efficiencyClasses)
    {
        if (efficiencyClasses.Count == 0) return (0, 0, 0);
        int top = efficiencyClasses.Max();
        if (efficiencyClasses.All(c => c == top)) return (efficiencyClasses.Count, 0, 0);
        int performance = efficiencyClasses.Count(c => c == top);
        return (efficiencyClasses.Count, performance, efficiencyClasses.Count - performance);
    }

    /// <summary>"Windows 11 Pro 25H2 (26300.6725)". The registry still says "Windows 10" on Windows 11; the build tells them apart.</summary>
    internal static string WindowsName(string productName, string displayVersion, int build, int revision)
    {
        string name = build >= 22000 && productName.StartsWith("Windows 10", StringComparison.Ordinal)
            ? "Windows 11" + productName["Windows 10".Length..]
            : productName;
        var text = new StringBuilder(name.Trim());
        if (displayVersion.Length > 0) text.Append(' ').Append(displayVersion);
        if (build > 0) text.Append(" (").Append(build).Append(revision > 0 ? "." + revision : "").Append(')');
        return text.ToString();
    }

    private static string WindowsName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is null) return "";
        int.TryParse(key.GetValue("CurrentBuildNumber") as string, out int build);
        int revision = key.GetValue("UBR") is int ubr ? ubr : 0;
        return WindowsName(key.GetValue("ProductName") as string ?? "", key.GetValue("DisplayVersion") as string ?? "", build, revision);
    }

    private static List<int> EfficiencyClasses()
    {
        const int relationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return new List<int>();
        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length)) return new List<int>();
            var classes = new List<int>();
            for (int offset = 0; offset < length;)
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship, Size, then PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass…
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                if (Marshal.ReadInt32(buffer, offset) == relationProcessorCore) classes.Add(Marshal.ReadByte(buffer, offset + 9));
                offset += size;
            }
            return classes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long InstalledMemory() => GetPhysicallyInstalledSystemMemory(out ulong kilobytes) ? (long)kilobytes * 1024 : 0;

    /// <summary>Each part is a nicety: one that fails leaves the rest.</summary>
    private static T Attempt<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);
}

/// <summary>The firmware's SMBIOS table: a list of structures, each a formatted part followed by its strings.</summary>
internal static class Smbios
{
    public sealed record Structure(int Type, byte[] Formatted, IReadOnlyList<string> Strings)
    {
        /// <summary>The string a formatted byte points to (1-based; 0 means none).</summary>
        public string Text(int offset) =>
            offset < Formatted.Length && Formatted[offset] is > 0 and var index && index <= Strings.Count ? Strings[index - 1].Trim() : "";

        public int Word(int offset) => offset + 2 <= Formatted.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Formatted.AsSpan(offset)) : 0;
        public long DWord(int offset) => offset + 4 <= Formatted.Length ? BinaryPrimitives.ReadUInt32LittleEndian(Formatted.AsSpan(offset)) : 0;
    }

    public static byte[]? ReadRaw()
    {
        const uint rsmb = 0x52534D42; // 'RSMB'
        uint size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        return GetSystemFirmwareTable(rsmb, 0, buffer, size) == size ? buffer : null;
    }

    /// <summary>RawSMBIOSData: four header bytes, the table length, then the table.</summary>
    public static List<Structure> Parse(byte[] raw)
    {
        var result = new List<Structure>();
        if (raw.Length < 8) return result;
        int end = Math.Min(raw.Length, 8 + (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)), int.MaxValue - 8));
        int p = 8;
        while (p + 4 <= end)
        {
            int type = raw[p], length = raw[p + 1];
            if (length < 4 || p + length > end) break;
            var formatted = raw[p..(p + length)];
            var strings = new List<string>();
            int q = p + length;
            while (q < end && raw[q] != 0) // strings, each ending in 0; the list ends with one more 0
            {
                int s = q;
                while (q < end && raw[q] != 0) q++;
                strings.Add(Encoding.ASCII.GetString(raw, s, q - s));
                q++;
            }
            q = strings.Count == 0 ? q + 2 : q + 1;
            result.Add(new Structure(type, formatted, strings));
            if (type == 127) break; // end of table
            p = q;
        }
        return result;
    }

    private static readonly string[] Placeholders =
    {
        "to be filled", "default string", "system product name", "system version", "system manufacturer",
        "not specified", "not applicable", "type1productconfigid", "invalid", "unknown", "n/a", "none",
    };

    private static string Clean(string value) =>
        Placeholders.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase)) || value.All(c => c == '0' || c == ' ') ? "" : value.Trim();

    /// <summary>
    /// The maker and model from the system structure (type 1). Some makers put a code in "product" and
    /// the name people know in "version" (Lenovo: "83ND" and "XiaoXinPro 16c IAH10"): then both are shown.
    /// </summary>
    public static string ComputerModel(IEnumerable<Structure> structures)
    {
        var system = structures.FirstOrDefault(s => s.Type == 1);
        if (system is null) return "";
        string maker = Clean(system.Text(4)), product = Clean(system.Text(5)), version = Clean(system.Text(6));
        bool productIsCode = product.Length is > 0 and <= 12 && !product.Contains(' ') && product.Any(char.IsDigit);
        string model = productIsCode && version.Contains(' ') ? $"{version} ({product})" : product.Length > 0 ? product : version;
        if (model.Length == 0) return maker;
        return maker.Length == 0 || model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : $"{maker} {model}";
    }

    /// <summary>Installed memory modules (type 17); empty slots are left out.</summary>
    public static List<MemoryModule> MemoryModules(IEnumerable<Structure> structures)
    {
        var modules = new List<MemoryModule>();
        foreach (var device in structures.Where(s => s.Type == 17))
        {
            int size = device.Word(0x0C);
            if (size is 0 or 0xFFFF) continue;
            long bytes = size == 0x7FFF ? device.DWord(0x1C) << 20                    // extended size, in MB
                       : (size & 0x8000) != 0 ? (long)(size & 0x7FFF) << 10              // in KB
                       : (long)size << 20;                                               // in MB
            int speed = device.Word(0x20) is > 0 and < 0xFFFF and var configured ? configured : device.Word(0x15);
            if (speed == 0xFFFF) speed = (int)device.DWord(0x54); // extended speed
            string type = device.Formatted.Length > 0x12 ? device.Formatted[0x12] switch
            {
                0x12 => "DDR", 0x13 => "DDR2", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2",
                0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5",
                _ => "",
            } : "";
            modules.Add(new MemoryModule(bytes, type, speed, Clean(device.Text(0x17)), Clean(device.Text(0x1A))));
        }
        return modules;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);
}
