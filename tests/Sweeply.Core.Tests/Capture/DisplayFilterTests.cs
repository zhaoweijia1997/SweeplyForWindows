using System.Net;
using Sweeply.Core.Capture;
using static Sweeply.Core.Tests.Capture.PacketBuilder;

namespace Sweeply.Core.Tests.Capture;

public class DisplayFilterTests
{
    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.1");

    /// <summary>A TLS Client Hello from 192.0.2.23:51432 to 203.0.113.10:443, over Ethernet.</summary>
    private static CapturedPacket Tls() => TcpPacket(true, 1, 1, Psh | Ack, payload: ClientHello("www.example.com"));

    private static CapturedPacket Dns() =>
        Packet(Ethernet(MacA, MacB, 0x0800, Ipv4(ClientIp, Router, 17, Udp(53000, 53, DnsQuery(0x1234, "www.example.com")))));

    /// <summary>"Who has 192.0.2.1? Tell 192.0.2.23", to everyone.</summary>
    private static CapturedPacket Arp() => Packet(Ethernet(Broadcast, MacB, 0x0806, Concat(
        U16(1), U16(0x0800), new byte[] { 6, 4 }, U16(1),
        MacB, ClientIp.GetAddressBytes(), new byte[6], Router.GetAddressBytes())));

    private static bool Match(string filter, CapturedPacket packet) =>
        DisplayFilter.Parse(filter).Matches(DissectAll(new[] { packet })[0], packet);

    [Theory]
    [InlineData("tcp", true)]
    [InlineData("tls", true)]
    [InlineData("udp", false)]
    [InlineData("tls.handshake.extensions_server_name", true)]
    [InlineData("dns.qry.name", false)]
    [InlineData("tcp.dstport == 443", true)]
    [InlineData("tcp.dstport eq 443", true)]
    [InlineData("tcp.dstport == 0x1bb", true)]
    [InlineData("tcp.dstport != 443", false)]
    [InlineData("tcp.dstport > 442 && tcp.dstport < 444", true)]
    [InlineData("tcp.dstport ge 443 and tcp.dstport le 443", true)]
    [InlineData("tcp.port == 51432", true)]
    [InlineData("tcp.port == 80", false)]
    [InlineData("ip.addr == 203.0.113.10", true)]
    [InlineData("ip.addr == 203.0.113.0/24", true)]
    [InlineData("ip.addr == 198.51.100.0/24", false)]
    [InlineData("ip.addr != 203.0.113.10", false)]
    [InlineData("ip.src == 192.0.2.23 && ip.dst == 203.0.113.10", true)]
    [InlineData("eth.src == 00:00:5e:00:53:01", true)]
    [InlineData("eth.src == 00-00-5e-00-53-01", true)]
    [InlineData("eth.src == 00:00:5e:00:53:2a", false)]
    [InlineData("eth.addr == 00:00:5e:00:53:2a", true)]
    [InlineData("tls.handshake.extensions_server_name == \"www.example.com\"", true)]
    [InlineData("tls.handshake.extensions_server_name == \"WWW.EXAMPLE.COM\"", false)]
    [InlineData("tls.handshake.extensions_server_name contains \"example\"", true)]
    [InlineData("tls.handshake.extensions_server_name matches \"EXAMPLE\\.com$\"", true)]
    [InlineData("tls.handshake.extensions_server_name ~ \"^mail\\.\"", false)]
    [InlineData("tcp.port in {80 443}", true)]
    [InlineData("tcp.port in {80, 8080}", false)]
    [InlineData("tcp.port in {51000..52000}", true)]
    [InlineData("tcp.dstport in {8000..9000}", false)]
    [InlineData("tcp and not tls", false)]
    [InlineData("!tls", false)]
    [InlineData("dns or tls", true)]
    [InlineData("tcp xor tls", false)]
    [InlineData("tcp ^^ udp", true)]
    [InlineData("dns or tcp and udp", false)]
    [InlineData("(dns or tcp) and not udp", true)]
    [InlineData("not (tcp.flags.syn == 1) && tcp.flags.ack == 1", true)]
    [InlineData("tcp.flags.push == true", true)]
    [InlineData("frame.len > 100", true)]
    [InlineData("frame.time_relative >= 0", true)]
    public void Tls_client_hello(string filter, bool expected) => Assert.Equal(expected, Match(filter, Tls()));

    [Theory]
    [InlineData("dns", true)]
    [InlineData("dns.qry.name == \"www.example.com\"", true)]
    [InlineData("dns.qry.name == www.example.com", true)] // text without quotes, as Wireshark allows
    [InlineData("udp.port == 53 && ip.dst == 192.0.2.1", true)]
    [InlineData("tcp", false)]
    [InlineData("dns or tcp and udp", true)]
    public void Dns_query(string filter, bool expected) => Assert.Equal(expected, Match(filter, Dns()));

    [Theory]
    [InlineData("arp", true)]
    [InlineData("eth.dst == ff:ff:ff:ff:ff:ff", true)]
    [InlineData("arp.opcode == 1", true)]
    [InlineData("ip", false)]
    [InlineData("ip.addr == 192.0.2.1", false)]
    [InlineData("ip.addr != 192.0.2.1", true)] // "not (ip.addr == …)": true where there is no IP at all
    [InlineData("!(arp or dns)", false)]
    public void Arp_request(string filter, bool expected) => Assert.Equal(expected, Match(filter, Arp()));

    [Fact]
    public void The_program_is_a_field_too_and_its_name_ignores_case()
    {
        var packet = Tls();
        packet.ProcessName = "chrome.exe";
        packet.ProcessId = 4410;
        Assert.True(Match("process.name == \"Chrome.EXE\"", packet));
        Assert.True(Match("process.name contains \"chrome\"", packet));
        Assert.True(Match("process.pid == 4410", packet));
        Assert.False(Match("process.name == \"svchost.exe\"", packet));
        var unknown = Tls();
        Assert.False(Match("process.name", unknown));
        Assert.True(Match("!process.name", unknown));
    }

    [Theory]
    [InlineData("", FilterError.UnexpectedEnd, 0, "")]
    [InlineData("   ", FilterError.UnexpectedEnd, 3, "")]
    [InlineData("tcp ==", FilterError.UnexpectedEnd, 6, "")]
    [InlineData("tcp and", FilterError.UnexpectedEnd, 7, "")]
    [InlineData("(tcp", FilterError.Unclosed, 0, "(")]
    [InlineData("tcp.port in {80", FilterError.Unclosed, 12, "{")]
    [InlineData("http.host == \"abc", FilterError.Unclosed, 13, "\"")]
    [InlineData("tcp.port == 80 80", FilterError.Unexpected, 15, "80")]
    [InlineData("tcp.port == and", FilterError.Unexpected, 12, "and")]
    [InlineData("== 80", FilterError.Unexpected, 0, "==")]
    [InlineData("and", FilterError.Unexpected, 0, "and")]
    [InlineData("tcp #", FilterError.Unexpected, 4, "#")]
    [InlineData("tcp)", FilterError.Unexpected, 3, ")")]
    [InlineData("ip.addr == 999.1.1.1", FilterError.BadValue, 11, "999.1.1.1")]
    [InlineData("ip.addr == 192.0.2.0/40", FilterError.BadValue, 11, "192.0.2.0/40")]
    [InlineData("tcp.port in {..80}", FilterError.BadValue, 13, "..80")]
    public void Mistakes_say_what_and_where(string filter, FilterError error, int position, string near)
    {
        var e = Assert.Throws<FilterSyntaxException>(() => DisplayFilter.Parse(filter));
        Assert.Equal((error, position, near), (e.Error, e.Position, e.Near));
    }

    [Fact]
    public void A_wrong_regular_expression_is_a_mistake_too()
    {
        var e = Assert.Throws<FilterSyntaxException>(() => DisplayFilter.Parse("http.host matches \"[\""));
        Assert.Equal((FilterError.BadRegex, 18), (e.Error, e.Position));
    }

    /// <summary>"Apply as filter" on any line of any packet must find that packet again.</summary>
    [Fact]
    public void A_filter_made_from_a_field_finds_its_packet()
    {
        var packets = SamplePackets.Build();
        packets.Add(Arp());
        var dissections = DissectAll(packets);
        int checkedFields = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            foreach (var node in dissections[i].Layers.SelectMany(l => l.Walk()))
            {
                if (DisplayFilter.For(node) is not { } text) continue;
                var filter = DisplayFilter.Parse(text);
                Assert.True(filter.Matches(dissections[i], packets[i]), $"packet {i + 1}: {text}");
                checkedFields++;
            }
        }
        Assert.True(checkedFields > 300, $"only {checkedFields} fields");
    }

    [Fact]
    public void Text_values_are_written_so_they_read_back()
    {
        Assert.Equal("\"a \\\"quoted\\\" \\\\ path\\n\"", DisplayFilter.Quote("a \"quoted\" \\ path\n"));
        Assert.Equal("1E-05", DisplayFilter.WriteValue(0.00001));
        Assert.Equal("de:ad:be:ef", DisplayFilter.WriteValue(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
        Assert.Equal("2001:db8::1", DisplayFilter.WriteValue(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void Names_that_never_occur_can_be_told_apart()
    {
        var filter = DisplayFilter.Parse("tcp.port == 1 || tls.handshak.type == 1 || ip.addr");
        Assert.Equal(new[] { "tcp.port", "tls.handshak.type", "ip.addr" }, filter.Fields);
        var seen = new bool[filter.Fields.Count];
        foreach (var packet in new[] { Tls(), Dns() })
            filter.Matches(DissectAll(new[] { packet })[0], packet, seen);
        Assert.Equal(new[] { true, false, true }, seen);
    }
}
