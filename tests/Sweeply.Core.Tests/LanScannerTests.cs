using System.Net;
using System.Text;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class LanScannerTests
{
    [Theory]
    [InlineData("192.0.2.23", 24, "192.0.2.1", "192.0.2.254", 254, false)]
    [InlineData("198.51.100.77", 26, "198.51.100.65", "198.51.100.126", 62, false)]
    [InlineData("198.18.7.9", 16, "198.18.7.1", "198.18.7.254", 254, true)]   // too big: the /24 around this PC
    [InlineData("203.0.113.5", 30, "203.0.113.5", "203.0.113.6", 2, false)]
    public void Range_is_the_subnet_without_network_and_broadcast(string own, int prefix, string first, string last, int count, bool trimmed)
    {
        var range = LanScanner.Range(IPAddress.Parse(own), prefix);
        Assert.Equal(IPAddress.Parse(first), range.FirstAddress);
        Assert.Equal(IPAddress.Parse(last), range.LastAddress);
        Assert.Equal(count, range.Count);
        Assert.Equal(trimmed, range.Trimmed);
    }

    [Fact]
    public void Point_to_point_links_have_nothing_to_scan()
    {
        Assert.Equal(0, LanScanner.Range(IPAddress.Parse("192.0.2.1"), 31).Count);
        Assert.Equal(0, LanScanner.Range(IPAddress.Parse("192.0.2.1"), 32).Count);
    }

    [Theory]
    [InlineData("02-00-5E-00-53-01", true)]   // locally administered
    [InlineData("DA-8E-00-0E-B1-6C", true)]
    [InlineData("24-B2-B9-2C-55-05", false)]
    [InlineData("", false)]
    public void Random_mac_addresses(string mac, bool random)
    {
        Assert.Equal(random, new LanDevice { Address = IPAddress.Loopback, Mac = mac }.RandomMac);
    }

    [Fact]
    public void NetBios_node_status_query_asks_for_star()
    {
        var q = LanScanner.NodeStatusQuery();
        Assert.Equal(50, q.Length);
        Assert.Equal(1, q[5]);                        // one question
        Assert.Equal(32, q[12]);
        Assert.Equal("CK" + new string('A', 30), Encoding.ASCII.GetString(q, 13, 32));
        Assert.Equal((0x00, 0x21, 0x00, 0x01), (q[46], q[47], q[48], q[49])); // NBSTAT, IN
    }

    private static byte[] NodeStatusReply(params (string Name, byte Suffix, bool Group)[] names)
    {
        var reply = new List<byte>(new byte[56]) { (byte)names.Length };
        foreach (var (name, suffix, group) in names)
        {
            reply.AddRange(Encoding.ASCII.GetBytes(name.PadRight(15)));
            reply.Add(suffix);
            reply.Add(group ? (byte)0x84 : (byte)0x04);
            reply.Add(0);
        }
        return reply.ToArray();
    }

    [Fact]
    public void NetBios_reply_gives_the_computer_name()
    {
        var reply = NodeStatusReply(("WORKGROUP", 0x00, true), ("OFFICE-PRINTER", 0x20, false), ("OFFICE-PRINTER", 0x00, false));
        Assert.Equal("OFFICE-PRINTER", LanScanner.ParseNodeStatus(reply));
        Assert.Null(LanScanner.ParseNodeStatus(NodeStatusReply(("WORKGROUP", 0x00, true))));
        Assert.Null(LanScanner.ParseNodeStatus(new byte[20]));
    }
}
