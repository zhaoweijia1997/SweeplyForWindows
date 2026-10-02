using System.Buffers.Binary;

namespace Sweeply.Core.Monitoring;

/// <summary>What an NVMe drive's own health log says is wrong (its "critical warning" byte).</summary>
[Flags]
public enum DriveWarnings
{
    None = 0,
    SpareLow = 1,         // the spare blocks fell below the drive's threshold
    Temperature = 2,      // too hot or too cold
    Reliability = 4,      // reliability degraded by media errors
    ReadOnly = 8,         // the drive went read-only
    BackupFailed = 16,    // the volatile memory backup failed
}

/// <summary>An NVMe drive's health, from its own health log (SMART / health information, log page 02h).</summary>
public sealed record DriveHealth
{
    public required string Name { get; init; }
    public double? Celsius { get; init; }

    /// <summary>The drive's own lines: above the first it reports "too hot", above the second it may slow down or stop.</summary>
    public double? WarningCelsius { get; init; }
    public double? CriticalCelsius { get; init; }

    /// <summary>Share of the rated life used up, by the maker's estimate; can pass 100.</summary>
    public int PercentUsed { get; init; }
    public int AvailableSpare { get; init; }
    public int SpareThreshold { get; init; }
    public long BytesRead { get; init; }
    public long BytesWritten { get; init; }
    public long PowerOnHours { get; init; }
    public long PowerCycles { get; init; }
    public long UnsafeShutdowns { get; init; }
    public long MediaErrors { get; init; }
    public DriveWarnings Warnings { get; init; }
}

public static class DriveHealthReader
{
    private const long DataUnit = 1000 * 512; // the log counts reads and writes in thousands of 512-byte sectors

    /// <summary>Every internal NVMe drive that answers; other drives keep no such log. Read-only, no rights needed.</summary>
    public static IReadOnlyList<DriveHealth> ReadAll()
    {
        var result = new List<DriveHealth>();
        foreach (var device in StorageQuery.ListDevices())
        {
            if (device.BusType != StorageQuery.BusTypeNvme || !ThermalSampler.IsInternal(device.BusType, device.Removable)) continue;
            using var handle = StorageQuery.Open(device.Number);
            if (handle.IsInvalid || StorageQuery.NvmeHealthLog(handle) is not { } log) continue;
            if (Parse(device.Name, log, StorageQuery.NvmeIdentifyController(handle)) is { } health) result.Add(health);
        }
        var names = ThermalSampler.UniqueNames(result.Select(h => h.Name).ToList());
        return result.Select((h, i) => h with { Name = names[i] }).ToList();
    }

    /// <param name="logDescriptor">The health log as the driver returns it (STORAGE_PROTOCOL_DATA_DESCRIPTOR).</param>
    /// <param name="identifyDescriptor">The controller's identify data, the same way; null when not available.</param>
    internal static DriveHealth? Parse(string name, ReadOnlySpan<byte> logDescriptor, ReadOnlySpan<byte> identifyDescriptor)
    {
        var log = StorageQuery.ProtocolData(logDescriptor);
        if (log.Length < 176) return null;
        static long Counter(ReadOnlySpan<byte> log, int at) => // 128-bit counters; the low 64 bits are plenty
            (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(log[at..]), long.MaxValue);
        static double? Kelvin(int kelvin) => kelvin == 0 ? null : ThermalSampler.Plausible(kelvin - 273.15);

        var identify = StorageQuery.ProtocolData(identifyDescriptor);
        return new DriveHealth
        {
            Name = name,
            Warnings = (DriveWarnings)(log[0] & 0x1F) | ((log[0] & 0x20) != 0 ? DriveWarnings.ReadOnly : 0), // bit 5: persistent memory read-only
            Celsius = Kelvin(BinaryPrimitives.ReadUInt16LittleEndian(log[1..])),
            AvailableSpare = log[3],
            SpareThreshold = log[4],
            PercentUsed = log[5],
            BytesRead = Counter(log, 32) * DataUnit,
            BytesWritten = Counter(log, 48) * DataUnit,
            PowerCycles = Counter(log, 112),
            PowerOnHours = Counter(log, 128),
            UnsafeShutdowns = Counter(log, 144),
            MediaErrors = Counter(log, 160),
            // Identify Controller: WCTEMP at bytes 266-267, CCTEMP at 268-269, both in kelvin.
            WarningCelsius = identify.Length >= 270 ? Kelvin(BinaryPrimitives.ReadUInt16LittleEndian(identify[266..])) : null,
            CriticalCelsius = identify.Length >= 270 ? Kelvin(BinaryPrimitives.ReadUInt16LittleEndian(identify[268..])) : null,
        };
    }
}
