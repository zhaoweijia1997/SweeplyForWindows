using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sweeply.Core.Monitoring;

/// <summary>
/// Questions to the storage driver about a physical drive. The drive is opened with access 0, which
/// only allows asking the driver about the device: it needs no administrator rights and can neither
/// read nor write the drive's contents.
/// </summary>
internal static class StorageQuery
{
    public const int MaxDrives = 32;
    public const int BusTypeUsb = 7, BusTypeNvme = 17;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int StorageDeviceTemperatureProperty = 52;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0; // FILE_ANY_ACCESS, like the query above
    private const int ProtocolSpecificDataLength = 40;

    /// <summary>What the driver says about a drive: its name, how it is connected, and whether it is removable.</summary>
    public readonly record struct Device(int Number, string Name, int BusType, bool Removable);

    public static SafeFileHandle Open(int number) =>
        CreateFileW($@"\\.\PhysicalDrive{number}", 0, 3 /* share read and write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

    /// <summary>Every physical drive Windows numbers, internal or not.</summary>
    public static List<Device> ListDevices()
    {
        var devices = new List<Device>();
        for (int number = 0; number < MaxDrives; number++)
        {
            using var handle = Open(number);
            if (handle.IsInvalid) continue;
            if (Describe(handle) is { } d) devices.Add(d with { Number = number, Name = d.Name.Length > 0 ? d.Name : $"PhysicalDrive{number}" });
        }
        return devices;
    }

    public static Device? Describe(SafeFileHandle handle)
    {
        var buffer = new byte[1024];
        return Query(handle, PropertyQuery(StorageDeviceProperty), buffer, out int length)
            ? ParseDeviceDescriptor(buffer.AsSpan(0, length))
            : null;
    }

    /// <summary>The drive's size in bytes, or null.</summary>
    public static long? Size(SafeFileHandle handle)
    {
        var output = new byte[256];
        if (!DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, output, output.Length, out int length, IntPtr.Zero) || length < 32)
            return null;
        long size = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24)); // DISK_GEOMETRY (24 bytes), then DiskSize
        return size > 0 ? size : null;
    }

    /// <summary>The NVMe health log (log page 02h) as returned in a STORAGE_PROTOCOL_DATA_DESCRIPTOR, or null.</summary>
    public static byte[]? NvmeHealthLog(SafeFileHandle handle) => NvmeQuery(handle, dataType: 2 /* log page */, requestValue: 2, length: 512);

    /// <summary>The NVMe controller's identify data (CNS 01h), or null.</summary>
    public static byte[]? NvmeIdentifyController(SafeFileHandle handle) => NvmeQuery(handle, dataType: 1 /* identify */, requestValue: 1, length: 4096);

    /// <summary>The driver's own temperature query (STORAGE_TEMPERATURE_DATA_DESCRIPTOR), or null.</summary>
    public static byte[]? TemperatureDescriptor(SafeFileHandle handle)
    {
        var output = new byte[1024];
        return Query(handle, PropertyQuery(StorageDeviceTemperatureProperty), output, out int length) ? output[..length] : null;
    }

    private static byte[]? NvmeQuery(SafeFileHandle handle, int dataType, int requestValue, int length)
    {
        // STORAGE_PROPERTY_QUERY (property id, query type), then STORAGE_PROTOCOL_SPECIFIC_DATA in its
        // AdditionalParameters, then room for the data.
        var query = new byte[8 + ProtocolSpecificDataLength + length];
        var span = query.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 3);        // ProtocolTypeNvme
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], dataType);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], requestValue);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], ProtocolSpecificDataLength); // the data follows the struct
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], length);
        var output = new byte[query.Length];
        return Query(handle, query, output, out int returned) ? output[..returned] : null;
    }

    /// <summary>
    /// The data part of a STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version and Size, then
    /// STORAGE_PROTOCOL_SPECIFIC_DATA, whose data offset counts from the start of that struct.
    /// </summary>
    public static ReadOnlySpan<byte> ProtocolData(ReadOnlySpan<byte> descriptor)
    {
        const int specific = 8;
        if (descriptor.Length < specific + 24) return default;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor[(specific + 16)..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(descriptor[(specific + 20)..]);
        int start = specific + offset;
        if (offset <= 0 || length <= 0 || start >= descriptor.Length) return default;
        return descriptor.Slice(start, Math.Min(length, descriptor.Length - start));
    }

    /// <summary>Name, bus type and "removable" from a STORAGE_DEVICE_DESCRIPTOR (the number is left 0).</summary>
    public static Device? ParseDeviceDescriptor(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) return null;
        string vendor = AsciiAt(data, BinaryPrimitives.ReadInt32LittleEndian(data[12..]));
        string product = AsciiAt(data, BinaryPrimitives.ReadInt32LittleEndian(data[16..]));
        string name = vendor.Length == 0 ? product : product.Length == 0 ? vendor : vendor + " " + product;
        return new Device(0, name, BinaryPrimitives.ReadInt32LittleEndian(data[28..]), data[10] != 0);
    }

    /// <summary>How a drive is connected, as people call it: "NVMe", "SATA", "USB"…; empty when unusual.</summary>
    public static string BusName(int busType) => busType switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 or 15 => "VHD",
        16 => "Storage Spaces",
        17 => "NVMe",
        19 => "UFS",
        _ => "",
    };

    private static byte[] PropertyQuery(int propertyId)
    {
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY: property id, PropertyStandardQuery, no extra parameters
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
        return query;
    }

    private static bool Query(SafeFileHandle handle, byte[] input, byte[] output, out int length) =>
        DeviceIoControl(handle, IoctlStorageQueryProperty, input, input.Length, output, output.Length, out length, IntPtr.Zero);

    private static string AsciiAt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length) return "";
        var rest = data[offset..];
        int end = rest.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]).Trim();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
