using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Sweeply.Core.Monitoring;

/// <summary>The kinds of parts whose temperature can be read.</summary>
public enum ThermalPart
{
    Disk,
    Graphics,
}

/// <summary>One part's temperature in degrees Celsius.</summary>
public readonly record struct ThermalReading(ThermalPart Part, string Name, double Celsius);

/// <summary>
/// Reads the temperatures Windows gives out without administrator rights or a driver: NVMe drives
/// from their own health log (the composite temperature that Settings shows), other internal drives
/// through the storage driver's temperature query, and graphics cards through the query Task Manager
/// uses (integrated graphics usually report nothing there). The processor is not read: that needs a
/// kernel driver. USB, SD and virtual drives are skipped. Nothing is ever written to a drive.
/// <para>
/// The parts are looked for on the first read and again every few minutes, so a drive that is added
/// shows up; a part that stops answering is left out until the next look. Use from one thread.
/// </para>
/// </summary>
public sealed class ThermalSampler : IDisposable
{
    /// <summary>How often to look again for drives and graphics cards.</summary>
    public static readonly TimeSpan LookAgainEvery = TimeSpan.FromMinutes(5);

    // STORAGE_BUS_TYPE of drives inside the PC: SCSI, ATA, RAID, SAS, SATA, NVMe, SCM, UFS.
    // Not USB, 1394, SD, MMC, iSCSI, virtual disks or Storage Spaces.
    private static readonly HashSet<int> InternalBuses = new() { 1, 3, 8, 10, 11, 17, 18, 19 };

    private readonly List<Drive> _drives = new();
    private readonly List<Adapter> _adapters = new();
    private long? _lookedAt; // Environment.TickCount64
    private bool _disposed;

    /// <summary>The temperatures that could be read now; empty when there are none.</summary>
    public IReadOnlyList<ThermalReading> Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long now = Environment.TickCount64;
        if (_lookedAt is not long looked || now - looked >= (long)LookAgainEvery.TotalMilliseconds)
        {
            LookForParts();
            _lookedAt = now;
        }

        var readings = new List<ThermalReading>();
        foreach (var drive in _drives.ToArray())
        {
            if (ReadDrive(drive.Number, drive.NvmeLog) is double celsius)
                readings.Add(new ThermalReading(ThermalPart.Disk, drive.Name, celsius));
            else
                _drives.Remove(drive);
        }
        foreach (var adapter in _adapters.ToArray())
        {
            if (ReadAdapter(adapter.Handle) is double celsius)
                readings.Add(new ThermalReading(ThermalPart.Graphics, adapter.Name, celsius));
            else
            {
                GraphicsAdapters.Close(adapter.Handle);
                _adapters.Remove(adapter);
            }
        }
        return readings;
    }

    /// <summary>The hottest reading, of one kind of part or of all; null when there is none.</summary>
    public static ThermalReading? Hottest(IEnumerable<ThermalReading> readings, ThermalPart? part = null)
    {
        ThermalReading? hottest = null;
        foreach (var reading in readings)
        {
            if (part is not null && reading.Part != part) continue;
            if (hottest is null || reading.Celsius > hottest.Value.Celsius) hottest = reading;
        }
        return hottest;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseAdapters();
    }

    private void LookForParts()
    {
        _drives.Clear();
        foreach (var device in StorageQuery.ListDevices())
        {
            if (FindDrive(device) is { } drive) _drives.Add(drive);
        }
        var driveNames = UniqueNames(_drives.Select(d => d.Name).ToList());
        for (int i = 0; i < _drives.Count; i++) _drives[i] = _drives[i] with { Name = driveNames[i] };

        CloseAdapters();
        FindAdapters();
        var adapterNames = UniqueNames(_adapters.Select(a => a.Name).ToList());
        for (int i = 0; i < _adapters.Count; i++) _adapters[i] = _adapters[i] with { Name = adapterNames[i] };
    }

    /// <summary>Two identical drives get "(1)" and "(2)" after their names so both can be told apart.</summary>
    internal static List<string> UniqueNames(IReadOnlyList<string> names)
    {
        var result = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            int total = names.Count(n => n == names[i]);
            int nth = names.Take(i + 1).Count(n => n == names[i]);
            result.Add(total > 1 ? $"{names[i]} ({nth})" : names[i]);
        }
        return result;
    }

    // ---- Drives ----

    private sealed record Drive(int Number, string Name, bool NvmeLog);

    private static Drive? FindDrive(StorageQuery.Device device)
    {
        if (!IsInternal(device.BusType, device.Removable)) return null;
        using var handle = StorageQuery.Open(device.Number);
        if (handle.IsInvalid) return null;
        if (device.BusType == StorageQuery.BusTypeNvme && ReadNvmeHealth(handle) is not null) return new Drive(device.Number, device.Name, NvmeLog: true);
        if (ReadTemperatureProperty(handle) is not null) return new Drive(device.Number, device.Name, NvmeLog: false);
        return null;
    }

    private static double? ReadDrive(int number, bool nvmeLog)
    {
        using var handle = StorageQuery.Open(number);
        if (handle.IsInvalid) return null;
        return nvmeLog ? ReadNvmeHealth(handle) : ReadTemperatureProperty(handle);
    }

    private static double? ReadNvmeHealth(SafeFileHandle handle) =>
        StorageQuery.NvmeHealthLog(handle) is { } log ? ParseNvmeHealth(log) : null;

    private static double? ReadTemperatureProperty(SafeFileHandle handle) =>
        StorageQuery.TemperatureDescriptor(handle) is { } descriptor ? ParseTemperatureDescriptor(descriptor) : null;

    internal static bool IsInternal(int busType, bool removable) => !removable && InternalBuses.Contains(busType);

    /// <summary>
    /// The composite temperature from an NVMe health log (a STORAGE_PROTOCOL_DATA_DESCRIPTOR). The log
    /// keeps it in kelvin in bytes 1 and 2; 0 means the drive does not report one.
    /// </summary>
    internal static double? ParseNvmeHealth(ReadOnlySpan<byte> descriptor)
    {
        var log = StorageQuery.ProtocolData(descriptor);
        if (log.Length < 3) return null;
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        return kelvin == 0 ? null : Plausible(kelvin - 273.15);
    }

    /// <summary>The first sensor of a STORAGE_TEMPERATURE_DATA_DESCRIPTOR, in whole degrees Celsius.</summary>
    internal static double? ParseTemperatureDescriptor(ReadOnlySpan<byte> data)
    {
        // Version, Size, CriticalTemperature, WarningTemperature, InfoCount, reserved (24 bytes),
        // then 16-byte STORAGE_TEMPERATURE_INFO entries: Index, Temperature, ...
        if (data.Length < 24 + 16 || BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) == 0) return null;
        return Plausible(BinaryPrimitives.ReadInt16LittleEndian(data[26..]));
    }

    // ---- Graphics cards ----

    private sealed record Adapter(uint Handle, string Name);

    private void FindAdapters()
    {
        foreach (var adapter in GraphicsAdapters.OpenAll())
        {
            if (ReadAdapter(adapter.Handle) is not null && GraphicsAdapters.Name(adapter.Handle) is { Length: > 0 } name)
                _adapters.Add(new Adapter(adapter.Handle, name));
            else
                GraphicsAdapters.Close(adapter.Handle);
        }
    }

    private static double? ReadAdapter(uint handle) =>
        GraphicsAdapters.Temperature(handle) is uint deciCelsius ? GraphicsCelsius(deciCelsius) : null;

    /// <summary>Graphics drivers report tenths of a degree; 0 means no reading (integrated graphics, or asleep).</summary>
    internal static double? GraphicsCelsius(uint deciCelsius) => deciCelsius == 0 ? null : Plausible(deciCelsius / 10.0);

    private void CloseAdapters()
    {
        foreach (var adapter in _adapters) GraphicsAdapters.Close(adapter.Handle);
        _adapters.Clear();
    }

    /// <summary>A sensor that is missing or broken can report nonsense; such a value is not shown.</summary>
    internal static double? Plausible(double celsius) => celsius > -40 && celsius < 150 ? celsius : null;
}
