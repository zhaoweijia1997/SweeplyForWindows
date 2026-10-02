using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class ThermalTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>What the storage driver returns for an NVMe health log query: descriptor, then the 512-byte log.</summary>
    private static byte[] NvmeAnswer(int kelvin, int dataOffset = 40, int dataLength = 512)
    {
        var data = new byte[8 + 40 + 512];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + 16), dataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + 20), dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8 + dataOffset + 1), (ushort)kelvin);
        return data;
    }

    [Fact]
    public void Nvme_composite_temperature_is_converted_from_kelvin()
    {
        Assert.Equal(37.85, ThermalSampler.ParseNvmeHealth(NvmeAnswer(311))!.Value, 2);
    }

    [Fact]
    public void Nvme_drive_without_a_temperature_or_a_short_answer_gives_nothing()
    {
        Assert.Null(ThermalSampler.ParseNvmeHealth(NvmeAnswer(0)));             // 0 K: not reported
        Assert.Null(ThermalSampler.ParseNvmeHealth(NvmeAnswer(273 + 400)));     // nonsense
        Assert.Null(ThermalSampler.ParseNvmeHealth(NvmeAnswer(311).AsSpan(0, 30)));
        Assert.Null(ThermalSampler.ParseNvmeHealth(NvmeAnswer(311, dataOffset: 0)));
        Assert.Null(ThermalSampler.ParseNvmeHealth(NvmeAnswer(311, dataLength: 2)));
    }

    [Fact]
    public void Storage_temperature_descriptor_gives_the_first_sensor()
    {
        var data = new byte[24 + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 1); // one sensor
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 41);
        Assert.Equal(41, ThermalSampler.ParseTemperatureDescriptor(data));

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 0); // no sensors
        Assert.Null(ThermalSampler.ParseTemperatureDescriptor(data));
        Assert.Null(ThermalSampler.ParseTemperatureDescriptor(new byte[20]));
    }

    private static byte[] DeviceDescriptor(string? vendor, string product, int busType, bool removable)
    {
        var data = new byte[200];
        data[10] = removable ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), busType);
        int at = 64;
        if (vendor is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), at);
            at += Encoding.ASCII.GetBytes(vendor, data.AsSpan(at)) + 1;
        }
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), at);
        Encoding.ASCII.GetBytes(product, data.AsSpan(at));
        return data;
    }

    [Fact]
    public void Drive_name_and_bus_come_from_the_device_descriptor()
    {
        // As read from this PC: an NVMe drive has no vendor string, a USB stick pads with spaces.
        var nvme = StorageQuery.ParseDeviceDescriptor(DeviceDescriptor(null, "YMTC YMSS2ED08B66MC", 17, false))!.Value;
        Assert.Equal("YMTC YMSS2ED08B66MC", nvme.Name);
        Assert.True(ThermalSampler.IsInternal(nvme.BusType, nvme.Removable));

        var stick = StorageQuery.ParseDeviceDescriptor(DeviceDescriptor("Lexar   ", "USB Flash Drive ", 7, true))!.Value;
        Assert.Equal("Lexar USB Flash Drive", stick.Name);
        Assert.False(ThermalSampler.IsInternal(stick.BusType, stick.Removable));

        Assert.Null(StorageQuery.ParseDeviceDescriptor(new byte[16]));
    }

    [Theory]
    [InlineData(11, false, true)]  // SATA
    [InlineData(17, false, true)]  // NVMe
    [InlineData(7, false, false)]  // USB enclosure that says it is not removable
    [InlineData(16, false, false)] // Storage Spaces
    [InlineData(15, false, false)] // VHD
    [InlineData(11, true, false)]  // removable
    public void Only_internal_drives_are_asked(int busType, bool removable, bool expected)
    {
        Assert.Equal(expected, ThermalSampler.IsInternal(busType, removable));
    }

    [Fact]
    public void Graphics_temperature_is_in_tenths_and_0_means_none()
    {
        Assert.Equal(52.3, ThermalSampler.GraphicsCelsius(523)!.Value, 3);
        Assert.Null(ThermalSampler.GraphicsCelsius(0));
        Assert.Null(ThermalSampler.GraphicsCelsius(2000));
    }

    [Fact]
    public void Identical_names_are_numbered()
    {
        Assert.Equal(new[] { "A (1)", "B", "A (2)" }, ThermalSampler.UniqueNames(new[] { "A", "B", "A" }));
        Assert.Equal(new[] { "A", "B" }, ThermalSampler.UniqueNames(new[] { "A", "B" }));
    }

    [Fact]
    public void Hottest_reading_overall_and_per_part()
    {
        var readings = new[]
        {
            new ThermalReading(ThermalPart.Disk, "SSD", 38),
            new ThermalReading(ThermalPart.Graphics, "GPU", 61),
            new ThermalReading(ThermalPart.Disk, "HDD", 44),
        };
        Assert.Equal("GPU", ThermalSampler.Hottest(readings)!.Value.Name);
        Assert.Equal("HDD", ThermalSampler.Hottest(readings, ThermalPart.Disk)!.Value.Name);
        Assert.Null(ThermalSampler.Hottest(readings.Take(1), ThermalPart.Graphics));
        Assert.Null(ThermalSampler.Hottest(Array.Empty<ThermalReading>()));
    }

    [Theory]
    [InlineData(37.85, "38°C", "38°")]
    [InlineData(-0.3, "0°C", "0°")]
    [InlineData(double.NaN, "0°C", "0°")]
    [InlineData(104.6, "105°C", "105°")]
    public void Temperatures_are_whole_degrees(double celsius, string full, string compact)
    {
        Assert.Equal(full, RateFormatter.Celsius(celsius, Inv));
        Assert.Equal(compact, RateFormatter.CompactCelsius(celsius, Inv));
    }

    [Fact]
    public void Sampler_reads_without_administrator_rights_on_this_machine()
    {
        using var sampler = new ThermalSampler();
        var first = sampler.Read();
        var second = sampler.Read(); // reads again without looking for the parts again
        Assert.All(first.Concat(second), r => Assert.InRange(r.Celsius, -40, 150));
        Assert.Equal(first.Select(r => r.Name), second.Select(r => r.Name));
    }
}
