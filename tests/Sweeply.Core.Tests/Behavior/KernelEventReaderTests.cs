using System.Buffers.Binary;
using System.Net;
using System.Text;
using Sweeply.Core.Behavior;

namespace Sweeply.Core.Tests.Behavior;

/// <summary>
/// Kernel events built byte by byte as each provider's manifest lays them out (dumped with TDH on Windows 11:
/// Kernel-Process start v0–v4 and stop v0–v2, Kernel-File v0/v1, Kernel-Registry v0, Kernel-Network v0, DNS client).
/// </summary>
public class KernelEventReaderTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc);

    private sealed class Data
    {
        private readonly List<byte> _bytes = new();
        public Data U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _bytes.AddRange(b); return this; }
        public Data U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _bytes.AddRange(b); return this; }
        public Data U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _bytes.AddRange(b); return this; }
        public Data Text(string s) { _bytes.AddRange(Encoding.Unicode.GetBytes(s + "\0")); return this; }
        public Data Ansi(string s) { _bytes.AddRange(Encoding.ASCII.GetBytes(s + "\0")); return this; }
        public Data Raw(byte[] b) { _bytes.AddRange(b); return this; }

        /// <summary>S-1-16-8192 (medium integrity): revision, 1 sub-authority, authority 16, then 8192.</summary>
        public Data MediumLabel() => Raw(new byte[] { 1, 1, 0, 0, 0, 0, 0, 16 }).U32(8192);
        public byte[] Bytes => _bytes.ToArray();
    }

    private static (KernelEventReader Reader, TrackedProcesses Tracked) Reader(params int[] roots)
    {
        var tracked = new TrackedProcesses(roots);
        return (new KernelEventReader(tracked, pid => $"child.exe --pid {pid}"), tracked);
    }

    private static BehaviorEvent? Read(KernelEventReader reader, Guid provider, int id, int version, int pid, Data data, double seconds = 0)
    {
        var bytes = data.Bytes;
        uint first = bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0;
        return reader.Wanted(provider, id, pid, first) ? reader.Read(provider, id, version, pid, T0.AddSeconds(seconds), bytes) : null;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_child_process_is_followed_in_every_version_of_the_start_event(int version)
    {
        var (reader, tracked) = Reader(100);
        var d = new Data().U32(200);
        if (version >= 3) d.U64(7);
        d.U64(0x01DB000000000000).U32(100);
        if (version >= 3) d.U64(6);
        d.U32(1);
        if (version >= 1) d.U32(0);
        if (version >= 3) d.U32(1).U32(0).MediumLabel();
        d.Text(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe");
        if (version >= 2) d.U32(0).U32(0).Text("").Text("");
        var e = Read(reader, KernelEventReader.ProcessProvider, 1, version, 4, d)!;
        Assert.Equal((BehaviorKind.ProcessStarted, 200, 100), (e.Kind, e.ProcessId, e.Number));
        Assert.Equal(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe", e.Target);
        Assert.Equal("child.exe --pid 200", e.Detail);
        Assert.True(tracked.Contains(200));

        // Someone else's child: not recorded, nothing to say.
        var other = new Data().U32(300);
        if (version >= 3) other.U64(8);
        other.U64(0).U32(999);
        if (version >= 3) other.U64(9);
        other.U32(1);
        if (version >= 1) other.U32(0);
        if (version >= 3) other.U32(1).U32(0).MediumLabel();
        other.Text(@"\Device\HarddiskVolume3\x.exe");
        Assert.Null(Read(reader, KernelEventReader.ProcessProvider, 1, version, 4, other));
        Assert.False(tracked.Contains(300));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_process_ending_says_its_exit_code(int version)
    {
        var (reader, tracked) = Reader(100);
        var d = new Data().U32(100);
        if (version >= 2) d.U64(5);
        d.U64(1).U64(2).U32(3).U32(0).U32(10).U64(0).U64(0);
        d.Ansi("setup.exe");
        var e = Read(reader, KernelEventReader.ProcessProvider, 2, version, 100, d)!;
        Assert.Equal((BehaviorKind.ProcessExited, 100, 3), (e.Kind, e.ProcessId, e.Number));
        Assert.Equal(0, tracked.Running);
        Assert.Null(Read(reader, KernelEventReader.ProcessProvider, 2, version, 100, d)); // already gone
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Files_created_written_deleted_and_renamed(int version)
    {
        var (reader, _) = Reader(100);
        const string path = @"\Device\HarddiskVolume3\Users\Test\AppData\Local\Example\data.db";
        Data Create(ulong fileObject, uint options, string name)
        {
            var d = new Data().U64(0xAAAA);
            if (version == 0) d.U64(0xBBBB);
            d.U64(fileObject);
            if (version >= 1) d.U32(4321);
            return d.U32(options).U32(0x80).U32(7).Text(name);
        }
        Data Write(ulong fileObject, uint size)
        {
            var d = new Data().U64(0).U64(0xAAAA);
            if (version == 0) d.U64(0xBBBB);
            d.U64(fileObject).U64(0xCCCC);
            if (version >= 1) d.U32(4321);
            d.U32(size).U32(0);
            if (version >= 1) d.U32(0);
            return d;
        }
        Data ByPath(string name)
        {
            var d = new Data().U64(0xAAAA);
            if (version == 0) d.U64(0xBBBB);
            d.U64(0x1111).U64(0xCCCC).U64(0);
            if (version >= 1) d.U32(4321);
            return d.U32(13).Text(name);
        }

        var created = Read(reader, KernelEventReader.FileProvider, 30, version, 100, Create(0x5000, 0x05000020, path))!;
        Assert.Equal((BehaviorKind.FileCreated, path), (created.Kind, created.Target));
        Assert.Null(Read(reader, KernelEventReader.FileProvider, 12, version, 100, Create(0x6000, 0x01000021, @"\Device\HarddiskVolume3\Users"))); // a folder
        Assert.Null(Read(reader, KernelEventReader.FileProvider, 16, version, 100, Write(0x5000, 4096)));
        Assert.Null(Read(reader, KernelEventReader.FileProvider, 16, version, 100, Write(0x5000, 100)));
        Assert.Null(Read(reader, KernelEventReader.FileProvider, 16, version, 100, Write(0x9999, 100))); // a file opened before recording
        Assert.Null(Read(reader, KernelEventReader.FileProvider, 16, version, 555, Write(0x5000, 100)));  // not a tracked process
        var writes = reader.Flush();
        Assert.Equal((BehaviorKind.FileWritten, path, 4196L), (writes.Single().Kind, writes.Single().Target, writes.Single().Size));
        Assert.Empty(reader.Flush());

        Assert.Equal((BehaviorKind.FileDeleted, path), Pair(Read(reader, KernelEventReader.FileProvider, 26, version, 100, ByPath(path))!));
        Assert.Equal((BehaviorKind.FileRenamed, path), Pair(Read(reader, KernelEventReader.FileProvider, 27, version, 100, ByPath(path))!));
    }

    private static (BehaviorKind, string) Pair(BehaviorEvent e) => (e.Kind, e.Target);

    [Fact]
    public void Registry_keys_are_named_through_the_objects_opened_earlier()
    {
        var (reader, _) = Reader(100);
        const string run = @"\REGISTRY\USER\S-1-5-21-1-2-3-1001\Software\Microsoft\Windows\CurrentVersion\Run";
        Data Open(ulong baseObject, ulong keyObject, uint disposition, string baseName, string relative) =>
            new Data().U64(baseObject).U64(keyObject).U32(0).U32(disposition).Text(baseName).Text(relative);

        // OpenKey by full name, then CreateKey relative to that object (the base given only as the object).
        Assert.Null(Read(reader, KernelEventReader.RegistryProvider, 2, 0, 100, Open(0, 0x10, 0, "", run)));
        var created = Read(reader, KernelEventReader.RegistryProvider, 1, 0, 100, Open(0x10, 0x20, 1, "", @"Example\Sub"))!;
        Assert.Equal((BehaviorKind.KeyCreated, run + @"\Example\Sub"), Pair(created));
        Assert.Null(Read(reader, KernelEventReader.RegistryProvider, 1, 0, 100, Open(0x10, 0x30, 2, "", "Existing"))); // opened, not new

        var data = Encoding.Unicode.GetBytes(@"C:\x\update.exe /silent" + "\0");
        var set = new Data().U64(0x10).U32(0).U32(1).U32((uint)data.Length).Text("").Text("Updater").U16((ushort)data.Length).Raw(data).U32(0).U32(0).U16(0);
        var e = Read(reader, KernelEventReader.RegistryProvider, 5, 0, 100, set)!;
        Assert.Equal((BehaviorKind.ValueSet, run, "Updater", @"C:\x\update.exe /silent"), (e.Kind, e.Target, e.Detail, e.Data));

        var dword = new Data().U64(0x20).U32(0).U32(4).U32(4).Text("").Text("Count").U16(4).U32(42).U32(0).U32(0).U16(0);
        Assert.Equal("42", Read(reader, KernelEventReader.RegistryProvider, 5, 0, 100, dword)!.Data);

        var failed = new Data().U64(0x10).U32(0xC0000022).U32(1).U32(0).Text("").Text("Denied").U16(0).U32(0).U32(0).U16(0);
        Assert.Null(Read(reader, KernelEventReader.RegistryProvider, 5, 0, 100, failed)); // access denied: nothing changed

        var deleted = Read(reader, KernelEventReader.RegistryProvider, 6, 0, 100, new Data().U64(0x10).U32(0).Text("").Text("Updater"))!;
        Assert.Equal((BehaviorKind.ValueDeleted, run, "Updater"), (deleted.Kind, deleted.Target, deleted.Detail));
        var keyGone = Read(reader, KernelEventReader.RegistryProvider, 3, 0, 100, new Data().U64(0x20).U32(0).Text(""))!;
        Assert.Equal((BehaviorKind.KeyDeleted, run + @"\Example\Sub"), Pair(keyGone));

        // A name given in full wins over the object.
        var full = Read(reader, KernelEventReader.RegistryProvider, 6, 0, 100, new Data().U64(0x10).U32(0).Text(@"\REGISTRY\MACHINE\SOFTWARE\Other").Text("V"))!;
        Assert.Equal(@"\REGISTRY\MACHINE\SOFTWARE\Other", full.Target);
        Assert.Null(Read(reader, KernelEventReader.RegistryProvider, 5, 0, 555, set)); // not a tracked process
    }

    [Fact]
    public void Connections_name_the_other_end_and_udp_only_once_per_address()
    {
        var (reader, _) = Reader(100);
        Data V4(uint pid, string remote, ushort port) =>
            new Data().U32(pid).U32(0).Raw(IPAddress.Parse(remote).GetAddressBytes()).Raw(IPAddress.Parse("192.0.2.23").GetAddressBytes())
                .U16(BinaryPrimitives.ReverseEndianness(port)).U16(BinaryPrimitives.ReverseEndianness((ushort)51432)).U32(0).U32(0);
        Data V6(uint pid, string remote, ushort port) =>
            new Data().U32(pid).U32(0).Raw(IPAddress.Parse(remote).GetAddressBytes()).Raw(IPAddress.Parse("2001:db8::23").GetAddressBytes())
                .U16(BinaryPrimitives.ReverseEndianness(port)).U16(BinaryPrimitives.ReverseEndianness((ushort)51432)).U32(0).U32(0);

        var tcp = Read(reader, KernelEventReader.NetworkProvider, 12, 0, 4, V4(100, "203.0.113.10", 443))!; // the header says System; the data says who
        Assert.Equal((BehaviorKind.Connected, 100, "203.0.113.10:443", "TCP"), (tcp.Kind, tcp.ProcessId, tcp.Target, tcp.Detail));
        var accepted = Read(reader, KernelEventReader.NetworkProvider, 31, 0, 4, V6(100, "2001:db8::77", 50000))!;
        Assert.Equal((BehaviorKind.Accepted, "[2001:db8::77]:50000"), Pair(accepted));
        Assert.Equal("UDP", Read(reader, KernelEventReader.NetworkProvider, 42, 0, 4, V4(100, "198.51.100.53", 53))!.Detail);
        Assert.Null(Read(reader, KernelEventReader.NetworkProvider, 42, 0, 4, V4(100, "198.51.100.53", 53))); // the same again
        Assert.Null(Read(reader, KernelEventReader.NetworkProvider, 12, 0, 4, V4(555, "203.0.113.10", 443)));
        Assert.Null(Read(reader, KernelEventReader.NetworkProvider, 10, 0, 4, V4(100, "203.0.113.10", 443))); // a send: not read at all
    }

    [Fact]
    public void Lookups_come_from_the_program_or_the_dns_service_on_its_behalf()
    {
        var (reader, _) = Reader(100);
        var own = new Data().Text("www.example.com").U32(1).U64(0).U32(0).Text("::ffff:203.0.113.10;");
        var e = Read(reader, KernelEventReader.DnsProvider, 3008, 0, 100, own)!;
        Assert.Equal((BehaviorKind.DnsLookup, "www.example.com", "::ffff:203.0.113.10;"), (e.Kind, e.Target, e.Detail));

        // The service's event for the same lookup a moment later: once is enough.
        var service = new Data().Text("www.example.com").U32(1).U32(1).U32(29).U32(0).Text("::ffff:203.0.113.10;").U32(100).U64(0);
        Assert.Null(Read(reader, KernelEventReader.DnsProvider, 3020, 2, 1234, service, seconds: 0.5));
        var later = Read(reader, KernelEventReader.DnsProvider, 3020, 2, 1234, service, seconds: 5)!;
        Assert.Equal(100, later.ProcessId);

        var cache = new Data().Text("cdn.example.net").U32(1).U64(0).U32(9003).Text("").U32(100);
        var failed = Read(reader, KernelEventReader.DnsProvider, 3018, 1, 1234, cache)!;
        Assert.Equal(("cdn.example.net", ""), (failed.Target, failed.Detail));
        Assert.Null(Read(reader, KernelEventReader.DnsProvider, 3020, 0, 1234, service, seconds: 20)); // no client in version 0
        var someoneElse = new Data().Text("other.example").U32(1).U64(0).U32(0).Text("").U32(555);
        Assert.Null(Read(reader, KernelEventReader.DnsProvider, 3018, 1, 1234, someoneElse));
    }

    [Fact]
    public void Registry_data_is_shown_as_text()
    {
        Assert.Equal("a | b", KernelEventReader.ValueText(7, Encoding.Unicode.GetBytes("a\0b\0\0")));
        Assert.Equal("18446744073709551615", KernelEventReader.ValueText(11, Enumerable.Repeat((byte)0xFF, 8).ToArray()));
        Assert.Equal("00ff10", KernelEventReader.ValueText(3, new byte[] { 0, 255, 16 }));
        Assert.EndsWith("…", KernelEventReader.ValueText(1, Encoding.Unicode.GetBytes(new string('x', 400))));
        Assert.Equal("0000", KernelEventReader.ValueText(4, new byte[2])); // too short for a DWORD: shown as its bytes
    }

    /// <summary>The kernel's value-set events carry no data (seen on Windows 11): it is read from the registry right after.</summary>
    [Fact]
    public void A_value_set_is_read_back_by_its_kernel_path()
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        const string sub = @"Software\SweeplyForWindowsTest-" + nameof(A_value_set_is_read_back_by_its_kernel_path);
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(sub))
        {
            key.SetValue("Run", @"""C:\Tools\x.exe"" --background");
            key.SetValue("Expand", @"%TEMP%\x", Microsoft.Win32.RegistryValueKind.ExpandString);
            key.SetValue("Lines", new[] { "a", "b" });
            key.SetValue("Count", unchecked((int)0xFFFFFFFE), Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("Big", 1L << 40, Microsoft.Win32.RegistryValueKind.QWord);
            key.SetValue("Bytes", new byte[] { 0, 255 });
            key.SetValue("", "default");
        }
        try
        {
            string path = $@"\REGISTRY\USER\{sid}\{sub}";
            Assert.Equal(@"""C:\Tools\x.exe"" --background", KernelEventReader.CurrentValue(path, "Run"));
            Assert.Equal(@"%TEMP%\x", KernelEventReader.CurrentValue(path, "Expand")); // as stored, not expanded
            Assert.Equal("a | b", KernelEventReader.CurrentValue(path, "Lines"));
            Assert.Equal("4294967294", KernelEventReader.CurrentValue(path, "Count"));
            Assert.Equal("1099511627776", KernelEventReader.CurrentValue(path, "Big"));
            Assert.Equal("00ff", KernelEventReader.CurrentValue(path, "Bytes"));
            Assert.Equal("default", KernelEventReader.CurrentValue(path, ""));
            Assert.Null(KernelEventReader.CurrentValue(path, "Missing"));
            Assert.Null(KernelEventReader.CurrentValue(path + @"\Gone", "Run"));
            Assert.Null(KernelEventReader.CurrentValue(@"Software\Relative", "Run")); // a name not known in full
            Assert.NotNull(KernelEventReader.CurrentValue(@"\REGISTRY\MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Events_cut_short_never_throw()
    {
        var (reader, _) = Reader(100);
        var rnd = new Random(7);
        foreach (var (provider, id) in new[]
        {
            (KernelEventReader.ProcessProvider, 1), (KernelEventReader.ProcessProvider, 2), (KernelEventReader.FileProvider, 12),
            (KernelEventReader.FileProvider, 16), (KernelEventReader.FileProvider, 26), (KernelEventReader.RegistryProvider, 5),
            (KernelEventReader.NetworkProvider, 28), (KernelEventReader.DnsProvider, 3020),
        })
            for (int length = 0; length < 80; length++)
            {
                var bytes = new byte[length];
                rnd.NextBytes(bytes);
                for (int version = 0; version < 5; version++)
                    reader.Read(provider, id, version, 100, T0, bytes);
            }
    }
}
