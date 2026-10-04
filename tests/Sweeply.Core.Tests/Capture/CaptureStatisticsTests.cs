using System.Net;
using Sweeply.Core.Capture;
using static Sweeply.Core.Tests.Capture.PacketBuilder;

namespace Sweeply.Core.Tests.Capture;

public class CaptureStatisticsTests
{
    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.1");

    private static CapturedPacket As(CapturedPacket p, PacketDirection direction, string? program, double seconds) => new()
    {
        TimestampUtc = new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc).AddSeconds(seconds),
        Link = p.Link,
        Data = p.Data,
        Direction = direction,
        ProcessName = program,
        ProcessId = program is null ? 0 : 100,
    };

    /// <summary>
    /// svchost.exe looks up www.example.com (a CNAME to edge.example.net, 203.0.113.10); chrome.exe connects there,
    /// says hello twice (a retransmission) and the server resets the connection; an unanswered lookup fails.
    /// </summary>
    private static (List<CapturedPacket> Packets, CaptureStatistics Stats) Session()
    {
        var nxdomain = DnsQuery(0x2222, "nosuch.example");
        nxdomain[2] = 0x81;
        nxdomain[3] = 0x83; // a response: no such name
        var hello = ClientHello("www.example.com");
        var packets = new List<CapturedPacket>
        {
            As(Packet(Ethernet(MacB, MacA, 0x0800, Ipv4(ClientIp, Router, 17, Udp(53000, 53, DnsQuery(0x1111, "www.example.com"))))), PacketDirection.Outbound, "svchost.exe", 0.0),
            As(Packet(Ethernet(MacA, MacB, 0x0800, Ipv4(Router, ClientIp, 17, Udp(53, 53000, DnsResponse(0x1111, "www.example.com", "edge.example.net", ServerIp))))), PacketDirection.Inbound, "svchost.exe", 0.010),
            As(TcpPacket(true, 1000, 0, Syn, 64240, options: SynOptions), PacketDirection.Outbound, "chrome.exe", 0.020),
            As(TcpPacket(false, 5000, 1001, Syn | Ack, 65535, options: SynOptions), PacketDirection.Inbound, "chrome.exe", 0.040),
            As(TcpPacket(true, 1001, 5001, Ack, 1024), PacketDirection.Outbound, "chrome.exe", 0.041),
            As(TcpPacket(true, 1001, 5001, Psh | Ack, 1024, hello), PacketDirection.Outbound, "chrome.exe", 0.042),
            As(TcpPacket(true, 1001, 5001, Psh | Ack, 1024, hello), PacketDirection.Outbound, "chrome.exe", 1.500), // again: a retransmission
            As(TcpPacket(false, 5001, 1001 + (uint)hello.Length, Rst | Ack, 0), PacketDirection.Inbound, "chrome.exe", 1.520),
            As(Packet(Ethernet(MacA, MacB, 0x0800, Ipv4(Router, ClientIp, 17, Udp(53, 53001, nxdomain)))), PacketDirection.Inbound, null, 2.000),
        };
        var tracker = new StreamTracker();
        var streams = packets.Select((p, i) => tracker.Process(p, i + 1)).ToList();
        return (packets, CaptureStatistics.Compute(packets, streams, new[] { "Wi-Fi" }));
    }

    [Fact]
    public void Totals_and_traffic_over_time()
    {
        var (packets, stats) = Session();
        Assert.Equal(9, stats.Packets);
        Assert.Equal(packets.Sum(p => (long)p.Length), stats.Bytes);
        Assert.Equal(TimeSpan.FromSeconds(2), stats.Duration);
        Assert.Equal(8, stats.PacketsWithProgram);
        // Two seconds in steps of 20 ms: the finest step that keeps to 150 points.
        Assert.Equal(TimeSpan.FromMilliseconds(20), stats.Traffic.Step);
        Assert.Equal(101, stats.Traffic.Packets.Length);
        Assert.Equal((2L, 1L, 3L, 1L, 1L, 1L), (stats.Traffic.Packets[0], stats.Traffic.Packets[1], stats.Traffic.Packets[2],
            stats.Traffic.Packets[75], stats.Traffic.Packets[76], stats.Traffic.Packets[100]));
        Assert.Equal(9, stats.Traffic.Packets.Sum());
        Assert.Equal(stats.Bytes, stats.Traffic.Bytes.Sum());
        Assert.Equal(packets.Where(p => p.Direction == PacketDirection.Outbound).Sum(p => (long)p.Length), stats.Traffic.BytesOut.Sum());
    }

    [Fact]
    public void Conversations_with_names_from_the_capture()
    {
        var (_, stats) = Session();
        var tcp = Assert.Single(stats.Conversations, c => c.Protocol == "TCP");
        Assert.Equal((ClientIp, 51432, ServerIp, 443), (tcp.AddressA, tcp.PortA, tcp.AddressB, tcp.PortB));
        Assert.Equal((4L, 2L), (tcp.PacketsAToB, tcp.PacketsBToA));
        Assert.Equal("www.example.com", tcp.NameB); // what the Client Hello asked for
        Assert.Equal("chrome.exe", tcp.Program);
        Assert.Equal("tcp.stream == 0", tcp.Filter);
        Assert.Equal(TimeSpan.FromSeconds(1.5), tcp.Duration);

        var lookups = stats.Conversations.Where(c => c.Protocol == "UDP").ToList();
        Assert.Equal(2, lookups.Count);
        Assert.Contains(lookups, c => c.Program == "svchost.exe" && c.Filter == "udp.stream == 0");
        Assert.Contains(lookups, c => c.Program is null && c.PacketsAToB == 1 && c.AddressA.Equals(Router));
    }

    [Fact]
    public void Endpoints_know_this_pc_and_name_others_from_dns_answers()
    {
        var (_, stats) = Session();
        var here = Assert.Single(stats.Endpoints, e => e.Address.Equals(ClientIp));
        Assert.True(here.IsThisPc);
        Assert.Equal((5L, 4L), (here.PacketsSent, here.PacketsReceived));
        var server = Assert.Single(stats.Endpoints, e => e.Address.Equals(ServerIp));
        Assert.False(server.IsThisPc);
        Assert.Equal("www.example.com", server.Name); // the name asked for, not the CNAME's target
        Assert.Equal("ip.addr == 203.0.113.10", server.Filter);
        Assert.Equal("www.example.com", stats.Names[ServerIp]);
    }

    [Fact]
    public void Programs_with_what_they_sent_and_where_to()
    {
        var (packets, stats) = Session();
        Assert.Equal(new[] { "chrome.exe", "svchost.exe" }, stats.Programs.Select(p => p.Name));
        var chrome = stats.Programs[0];
        Assert.Equal(6, chrome.Packets);
        Assert.Equal(1, chrome.Conversations);
        Assert.Equal(packets.Where(p => p.ProcessName == "chrome.exe" && p.Direction == PacketDirection.Outbound).Sum(p => (long)p.Length), chrome.BytesSent);
        Assert.Equal(new[] { "www.example.com" }, chrome.Hosts);
        Assert.Equal("process.name == \"chrome.exe\"", chrome.Filter);
        Assert.Equal(new[] { "192.0.2.1" }, stats.Programs[1].Hosts);
    }

    [Fact]
    public void The_protocol_hierarchy_counts_every_protocol_on_the_way()
    {
        var (_, stats) = Session();
        var frame = stats.Protocols;
        Assert.Equal(("frame", 9L), (frame.Name, frame.Packets));
        var eth = Assert.Single(frame.Children);
        var ip = Assert.Single(eth.Children);
        Assert.Equal(("ip", 9L), (ip.Name, ip.Packets));
        Assert.Equal(new[] { ("tcp", 6L), ("udp", 3L) }, ip.Children.Select(c => (c.Name, c.Packets)));
        var dns = Assert.Single(ip.Children[1].Children);
        Assert.Equal(("dns", 3L), (dns.Name, dns.Packets));
        // The retransmitted hello isn't decoded again (as in Wireshark), so TLS is on one packet.
        Assert.Equal(("tls", 1L), ip.Children[0].Children.Select(c => (c.Name, c.Packets)).Single());
    }

    [Fact]
    public void Problems_with_counts_first_packets_and_filters_that_find_them()
    {
        var (packets, stats) = Session();
        Assert.Equal(new[] { ProblemKind.Retransmission, ProblemKind.Reset, ProblemKind.DnsError }, stats.Problems.Select(p => p.Kind));
        Assert.Equal((1, 7), (stats.Problems[0].Count, stats.Problems[0].FirstFrame));
        Assert.Equal((1, 8), (stats.Problems[1].Count, stats.Problems[1].FirstFrame));
        Assert.Equal((1, 9), (stats.Problems[2].Count, stats.Problems[2].FirstFrame));

        // Each problem's filter shows exactly its packets.
        var dissections = DissectAll(packets);
        foreach (var problem in stats.Problems)
        {
            var filter = DisplayFilter.Parse(problem.Filter);
            var shown = Enumerable.Range(0, packets.Count).Where(i => filter.Matches(dissections[i], packets[i])).Select(i => i + 1).ToList();
            Assert.Equal(problem.Count, shown.Count);
            Assert.Equal(problem.FirstFrame, shown[0]);
        }
    }

    [Fact]
    public void Filters_of_conversations_endpoints_and_programs_find_their_packets()
    {
        var (packets, stats) = Session();
        var dissections = DissectAll(packets);
        int Count(string text)
        {
            var filter = DisplayFilter.Parse(text);
            return Enumerable.Range(0, packets.Count).Count(i => filter.Matches(dissections[i], packets[i]));
        }
        foreach (var c in stats.Conversations) Assert.Equal(c.Packets, Count(c.Filter));
        foreach (var e in stats.Endpoints) Assert.Equal(e.Packets, Count(e.Filter));
        foreach (var p in stats.Programs) Assert.Equal(p.Packets, Count(p.Filter));
    }

    [Fact]
    public void An_empty_capture_adds_up_to_nothing()
    {
        var stats = CaptureStatistics.Compute(new List<CapturedPacket>(), new List<StreamInfo>(), Array.Empty<string>());
        Assert.Equal((0, 0L), (stats.Packets, stats.Bytes));
        Assert.Empty(stats.Conversations);
        Assert.Empty(stats.Problems);
    }
}
