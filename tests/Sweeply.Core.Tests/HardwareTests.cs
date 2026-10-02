using System.Buffers.Binary;
using System.Text;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class HardwareTests
{
    /// <summary>An SMBIOS structure: type, the formatted bytes after the 4-byte header, then its strings.</summary>
    private static byte[] Structure(int type, byte[] body, params string[] strings)
    {
        var bytes = new List<byte> { (byte)type, (byte)(4 + body.Length), 0, 0 };
        bytes.AddRange(body);
        if (strings.Length == 0) bytes.AddRange(new byte[] { 0, 0 });
        foreach (string s in strings) { bytes.AddRange(Encoding.ASCII.GetBytes(s)); bytes.Add(0); }
        if (strings.Length > 0) bytes.Add(0);
        return bytes.ToArray();
    }

    private static byte[] Table(params byte[][] structures)
    {
        var data = structures.SelectMany(s => s).Concat(Structure(127, Array.Empty<byte>())).ToArray();
        var raw = new byte[8 + data.Length];
        raw[1] = 3; raw[2] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)data.Length);
        data.CopyTo(raw, 8);
        return raw;
    }

    /// <summary>Type 1: manufacturer, product, version are string numbers at offsets 4, 5, 6.</summary>
    private static byte[] SystemInfo(string maker, string product, string version) =>
        Structure(1, new byte[] { 1, 2, 3, 0 }.Concat(new byte[23]).ToArray(), maker, product, version);

    private static byte[] MemoryDevice(int sizeField, byte type, int speed, int configuredSpeed, string maker, string part, long extendedMb = 0)
    {
        var body = new byte[0x54 - 4];
        void Word(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset - 4), (ushort)value);
        Word(0x0C, sizeField);
        body[0x12 - 4] = type;
        Word(0x15, speed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x1C - 4), (uint)extendedMb);
        Word(0x20, configuredSpeed);
        // SMBIOS has no empty strings: a missing one is string number 0.
        var strings = new List<string>();
        if (maker.Length > 0) { strings.Add(maker); body[0x17 - 4] = (byte)strings.Count; }
        if (part.Length > 0) { strings.Add(part); body[0x1A - 4] = (byte)strings.Count; }
        return Structure(17, body, strings.ToArray());
    }

    [Fact]
    public void Model_shows_the_known_name_when_the_product_is_a_code()
    {
        var table = Smbios.Parse(Table(SystemInfo("LENOVO", "83ND", "XiaoXinPro 16c IAH10")));
        Assert.Equal("LENOVO XiaoXinPro 16c IAH10 (83ND)", Smbios.ComputerModel(table));
    }

    [Theory]
    [InlineData("Dell Inc.", "XPS 15 9530", "", "Dell Inc. XPS 15 9530")]
    [InlineData("HP", "HP Laptop 15-fd0xxx", "Type1ProductConfigId", "HP Laptop 15-fd0xxx")] // no maker twice, no placeholder
    [InlineData("ASUS", "System Product Name", "System Version", "ASUS")]
    [InlineData("To be filled by O.E.M.", "To be filled by O.E.M.", "", "")]
    public void Model_skips_placeholders(string maker, string product, string version, string expected)
    {
        Assert.Equal(expected, Smbios.ComputerModel(Smbios.Parse(Table(SystemInfo(maker, product, version)))));
    }

    [Fact]
    public void Memory_modules_skip_empty_slots_and_read_extended_sizes()
    {
        var table = Smbios.Parse(Table(
            MemoryDevice(16384, 0x22, 5600, 5600, "Samsung", "M425R2GA3EB0-CWMOD"),
            MemoryDevice(0, 0, 0, 0, "", ""),                                     // empty slot
            MemoryDevice(0x7FFF, 0x1A, 3200, 2933, "Unknown", "", extendedMb: 65536)));
        var modules = Smbios.MemoryModules(table);
        Assert.Equal(2, modules.Count);
        Assert.Equal(new MemoryModule(16L << 30, "DDR5", 5600, "Samsung", "M425R2GA3EB0-CWMOD"), modules[0]);
        Assert.Equal(64L << 30, modules[1].Bytes);
        Assert.Equal(2933, modules[1].SpeedMts); // the configured speed wins
        Assert.Equal("", modules[1].Manufacturer);
    }

    [Fact]
    public void Smbios_parse_survives_a_cut_off_table()
    {
        var raw = Table(SystemInfo("A", "B", "C"));
        Assert.Empty(Smbios.Parse(raw[..6]));
        Assert.NotNull(Smbios.Parse(raw[..20])); // no exception, whatever it finds
    }

    [Theory]
    [InlineData(new[] { 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 14, 4, 10)]
    [InlineData(new[] { 0, 0, 0, 0 }, 4, 0, 0)]
    [InlineData(new int[0], 0, 0, 0)]
    public void Hybrid_processors_count_fast_and_efficient_cores(int[] classes, int cores, int performance, int efficient)
    {
        Assert.Equal((cores, performance, efficient), HardwareInfo.CoreCounts(classes));
    }

    [Theory]
    [InlineData("Windows 10 Pro", "25H2", 26300, 6725, "Windows 11 Pro 25H2 (26300.6725)")]
    [InlineData("Windows 10 Home", "22H2", 19045, 0, "Windows 10 Home 22H2 (19045)")]
    public void Windows_11_is_named_by_its_build(string product, string version, int build, int revision, string expected)
    {
        Assert.Equal(expected, HardwareInfo.WindowsName(product, version, build, revision));
    }

    private static byte[] HealthLog(Action<byte[]> fill, int length = 512)
    {
        var data = new byte[8 + 40 + length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + 16), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + 20), length);
        var log = new byte[length];
        fill(log);
        log.CopyTo(data, 48);
        return data;
    }

    [Fact]
    public void Nvme_health_log_fields()
    {
        var log = HealthLog(l =>
        {
            l[0] = 0b0000_0110;                                          // temperature + reliability
            BinaryPrimitives.WriteUInt16LittleEndian(l.AsSpan(1), 311); // 37.85 °C
            l[3] = 100; l[4] = 10; l[5] = 6;
            BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(32), 2_000_000);  // data units read
            BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(48), 1_000_000);  // data units written
            BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(112), 567);
            BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(128), 1234);
            BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(144), 12);
            BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(160), 0);
        });
        var identify = HealthLog(l =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(l.AsSpan(266), 273 + 83);
            BinaryPrimitives.WriteUInt16LittleEndian(l.AsSpan(268), 273 + 85);
        }, 4096);

        var health = DriveHealthReader.Parse("SSD", log, identify)!;
        Assert.Equal(DriveWarnings.Temperature | DriveWarnings.Reliability, health.Warnings);
        Assert.Equal(37.85, health.Celsius!.Value, 2);
        Assert.Equal((100, 10, 6), (health.AvailableSpare, health.SpareThreshold, health.PercentUsed));
        Assert.Equal(2_000_000L * 512_000, health.BytesRead);
        Assert.Equal(1_000_000L * 512_000, health.BytesWritten);
        Assert.Equal((567, 1234, 12, 0), (health.PowerCycles, health.PowerOnHours, health.UnsafeShutdowns, health.MediaErrors));
        Assert.Equal(82.85, health.WarningCelsius!.Value, 2);
        Assert.Equal(84.85, health.CriticalCelsius!.Value, 2);
    }

    [Fact]
    public void Nvme_health_without_identify_or_with_a_short_log()
    {
        var log = HealthLog(l => l[5] = 3);
        var health = DriveHealthReader.Parse("SSD", log, default)!;
        Assert.Null(health.WarningCelsius);
        Assert.Null(health.Celsius); // 0 K: not reported
        Assert.Equal(3, health.PercentUsed);
        Assert.Null(DriveHealthReader.Parse("SSD", HealthLog(_ => { }, 100), default));
    }

    [Fact]
    public void Hardware_summary_reads_on_this_machine()
    {
        var summary = HardwareInfo.Read();
        Assert.True(summary.LogicalProcessors > 0);
        Assert.True(summary.Cores is 0 || summary.Cores <= summary.LogicalProcessors);
        Assert.True(summary.MemoryBytes > 1L << 30);
        Assert.StartsWith("Windows", summary.Windows);
        Assert.NotEmpty(summary.Drives);
    }
}
