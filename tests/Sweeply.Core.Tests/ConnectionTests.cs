using System.Buffers.Binary;
using System.Net;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class ConnectionTests
{
    /// <summary>A port as these tables keep it: network byte order in the first two bytes of a DWORD.</summary>
    private static void Port(Span<byte> dword, int port)
    {
        dword[0] = (byte)(port >> 8);
        dword[1] = (byte)port;
    }

    private static byte[] Table(int rowSize, int rows, Action<Span<byte>, int> fill)
    {
        var table = new byte[4 + rowSize * rows];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows);
        for (int i = 0; i < rows; i++) fill(table.AsSpan(4 + i * rowSize, rowSize), i);
        return table;
    }

    [Fact]
    public void Tcp4_rows()
    {
        var table = Table(24, 2, (row, i) =>
        {
            BinaryPrimitives.WriteInt32LittleEndian(row, i == 0 ? 5 : 2); // established, listen
            IPAddress.Parse("192.0.2.23").GetAddressBytes().CopyTo(row[4..]);
            Port(row[8..], i == 0 ? 51432 : 445);
            IPAddress.Parse(i == 0 ? "203.0.113.9" : "0.0.0.0").GetAddressBytes().CopyTo(row[12..]);
            Port(row[16..], i == 0 ? 443 : 4711); // a listening row's remote port is left over: ignored
            BinaryPrimitives.WriteInt32LittleEndian(row[20..], 1234);
        });
        var rows = ConnectionTable.ParseTcp4(table).ToList();
        Assert.Equal(new Connection(TransportProtocol.Tcp, IPAddress.Parse("192.0.2.23"), 51432, IPAddress.Parse("203.0.113.9"), 443,
            TcpConnectionState.Established, 1234), rows[0]);
        Assert.Equal(0, rows[1].RemotePort);
        Assert.True(rows[1].IsListening);
        Assert.False(rows[0].IsListening);
    }

    [Fact]
    public void Tcp6_rows_and_mapped_addresses()
    {
        var table = Table(56, 1, (row, _) =>
        {
            IPAddress.Parse("::ffff:192.0.2.23").GetAddressBytes().CopyTo(row);
            Port(row[20..], 50000);
            IPAddress.Parse("2001:db8::7").GetAddressBytes().CopyTo(row[24..]);
            Port(row[44..], 8883);
            BinaryPrimitives.WriteInt32LittleEndian(row[48..], 11); // time wait
            BinaryPrimitives.WriteInt32LittleEndian(row[52..], 0);
        });
        var c = Assert.Single(ConnectionTable.ParseTcp6(table));
        Assert.Equal(IPAddress.Parse("192.0.2.23"), c.LocalAddress); // shown as plain IPv4
        Assert.Equal(IPAddress.Parse("2001:db8::7"), c.RemoteAddress);
        Assert.Equal((50000, 8883), (c.LocalPort, c.RemotePort));
        Assert.True(c.IsClosed);
    }

    [Fact]
    public void Udp_rows_have_no_remote_side()
    {
        var udp4 = Table(12, 1, (row, _) =>
        {
            IPAddress.Loopback.GetAddressBytes().CopyTo(row);
            Port(row[4..], 53);
            BinaryPrimitives.WriteInt32LittleEndian(row[8..], 42);
        });
        var u = Assert.Single(ConnectionTable.ParseUdp4(udp4));
        Assert.Equal((TransportProtocol.Udp, 53, 42), (u.Protocol, u.LocalPort, u.ProcessId));
        Assert.Null(u.RemoteAddress);
        Assert.True(u.IsListening);
        Assert.True(u.IsLoopback);

        var udp6 = Table(28, 1, (row, _) =>
        {
            IPAddress.IPv6Any.GetAddressBytes().CopyTo(row);
            Port(row[20..], 5353);
            BinaryPrimitives.WriteInt32LittleEndian(row[24..], 7);
        });
        Assert.Equal(5353, Assert.Single(ConnectionTable.ParseUdp6(udp6)).LocalPort);
    }

    [Fact]
    public void A_count_larger_than_the_buffer_is_cut()
    {
        var table = Table(12, 1, (_, _) => { });
        BinaryPrimitives.WriteUInt32LittleEndian(table, 1000);
        Assert.Single(ConnectionTable.ParseUdp4(table));
        Assert.Empty(ConnectionTable.ParseUdp4(new byte[2]));
    }

    [Fact]
    public void Reads_this_pc_and_names_its_programs()
    {
        var connections = ConnectionTable.Read();
        Assert.NotEmpty(connections);
        var names = new ProgramNames();
        var mine = connections.Where(c => c.ProcessId == Environment.ProcessId).ToList();
        foreach (var c in connections.Where(c => c.ProcessId > 4).Take(20))
            Assert.False(string.IsNullOrEmpty(names.Get(c.ProcessId).Name) && IsRunning(c.ProcessId), $"no name for process {c.ProcessId}");
        // svchost.exe processes say which services they run.
        var svchost = connections.Select(c => names.Get(c.ProcessId)).FirstOrDefault(p => p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase));
        if (svchost is not null) Assert.NotEmpty(svchost.Services);
    }

    private static bool IsRunning(int processId)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(processId); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
