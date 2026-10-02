using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
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

    private const int MaxDrives = 32;

    // STORAGE_BUS_TYPE of drives inside the PC: SCSI, ATA, RAID, SAS, SATA, NVMe, SCM, UFS.
    // Not USB, 1394, SD, MMC, iSCSI, virtual disks or Storage Spaces.
    private static readonly HashSet<int> InternalBuses = new() { 1, 3, 8, 10, 11, 17, 18, 19 };
    private const int BusTypeNvme = 17;

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
        for (int number = 0; number < MaxDrives; number++)
        {
            if (FindDrive(number) is { } drive) _drives.Add(drive);
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

    private static Drive? FindDrive(int number)
    {
        using var handle = Storage.Open(number);
        if (handle.IsInvalid) return null;
        var buffer = new byte[1024];
        if (!Storage.Query(handle, Storage.PropertyQuery(Storage.StorageDeviceProperty), buffer, out int length)) return null;
        if (ParseDeviceDescriptor(buffer.AsSpan(0, length)) is not { } device || !IsInternal(device.BusType, device.Removable))
            return null;

        string name = device.Name.Length > 0 ? device.Name : $"PhysicalDrive{number}";
        if (device.BusType == BusTypeNvme && ReadNvmeHealth(handle) is not null) return new Drive(number, name, NvmeLog: true);
        if (ReadTemperatureProperty(handle) is not null) return new Drive(number, name, NvmeLog: false);
        return null;
    }

    private static double? ReadDrive(int number, bool nvmeLog)
    {
        using var handle = Storage.Open(number);
        if (handle.IsInvalid) return null;
        return nvmeLog ? ReadNvmeHealth(handle) : ReadTemperatureProperty(handle);
    }

    private static double? ReadNvmeHealth(SafeFileHandle handle)
    {
        var output = new byte[Storage.NvmeHealthQueryLength];
        return Storage.Query(handle, Storage.NvmeHealthQuery(), output, out int length)
            ? ParseNvmeHealth(output.AsSpan(0, length))
            : null;
    }

    private static double? ReadTemperatureProperty(SafeFileHandle handle)
    {
        var output = new byte[1024];
        return Storage.Query(handle, Storage.PropertyQuery(Storage.StorageDeviceTemperatureProperty), output, out int length)
            ? ParseTemperatureDescriptor(output.AsSpan(0, length))
            : null;
    }

    internal static bool IsInternal(int busType, bool removable) => !removable && InternalBuses.Contains(busType);

    /// <summary>Name, bus type and "removable" from a STORAGE_DEVICE_DESCRIPTOR.</summary>
    internal static (string Name, int BusType, bool Removable)? ParseDeviceDescriptor(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) return null;
        string vendor = AsciiAt(data, BinaryPrimitives.ReadInt32LittleEndian(data[12..]));
        string product = AsciiAt(data, BinaryPrimitives.ReadInt32LittleEndian(data[16..]));
        string name = vendor.Length == 0 ? product : product.Length == 0 ? vendor : vendor + " " + product;
        return (name, BinaryPrimitives.ReadInt32LittleEndian(data[28..]), data[10] != 0);
    }

    /// <summary>
    /// The composite temperature from an NVMe health log, as returned in a STORAGE_PROTOCOL_DATA_DESCRIPTOR:
    /// Version and Size, then STORAGE_PROTOCOL_SPECIFIC_DATA, whose data offset counts from itself.
    /// The log keeps the temperature in kelvin in bytes 1 and 2; 0 means the drive does not report one.
    /// </summary>
    internal static double? ParseNvmeHealth(ReadOnlySpan<byte> data)
    {
        const int specific = 8;
        if (data.Length < specific + 24) return null;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data[(specific + 16)..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(data[(specific + 20)..]);
        int start = specific + offset;
        if (offset <= 0 || length < 3 || start + 3 > data.Length) return null;
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(data[(start + 1)..]);
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

    private static string AsciiAt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length) return "";
        var rest = data[offset..];
        int end = rest.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]).Trim();
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
    private static double? Plausible(double celsius) => celsius > -40 && celsius < 150 ? celsius : null;

    private static class Storage
    {
        public const int StorageDeviceProperty = 0;
        public const int StorageDeviceTemperatureProperty = 52;
        private const int StorageDeviceProtocolSpecificProperty = 50;
        private const uint IoctlStorageQueryProperty = 0x002D1400;
        private const int NvmeLogLength = 512;
        private const int ProtocolSpecificDataLength = 40;
        public const int NvmeHealthQueryLength = 8 + ProtocolSpecificDataLength + NvmeLogLength;

        /// <summary>
        /// Access 0 only asks the driver about the device; it needs no administrator rights and
        /// can neither read nor write the drive's contents.
        /// </summary>
        public static SafeFileHandle Open(int number) =>
            CreateFileW($@"\\.\PhysicalDrive{number}", 0, 3 /* share read and write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

        public static bool Query(SafeFileHandle handle, byte[] input, byte[] output, out int length) =>
            DeviceIoControl(handle, IoctlStorageQueryProperty, input, input.Length, output, output.Length, out length, IntPtr.Zero);

        /// <summary>STORAGE_PROPERTY_QUERY: property id, PropertyStandardQuery, no extra parameters.</summary>
        public static byte[] PropertyQuery(int propertyId)
        {
            var query = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
            return query;
        }

        /// <summary>STORAGE_PROPERTY_QUERY followed by STORAGE_PROTOCOL_SPECIFIC_DATA asking for the NVMe health log.</summary>
        public static byte[] NvmeHealthQuery()
        {
            var query = new byte[NvmeHealthQueryLength];
            var span = query.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(span, StorageDeviceProtocolSpecificProperty);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], 3);    // ProtocolTypeNvme
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], 2);   // NVMeDataTypeLogPage
            BinaryPrimitives.WriteInt32LittleEndian(span[16..], 2);   // log page 02h: SMART / health information
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], ProtocolSpecificDataLength); // data follows the struct
            BinaryPrimitives.WriteInt32LittleEndian(span[28..], NvmeLogLength);
            return query;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength,
            byte[] output, int outputLength, out int returned, IntPtr overlapped);
    }
}
