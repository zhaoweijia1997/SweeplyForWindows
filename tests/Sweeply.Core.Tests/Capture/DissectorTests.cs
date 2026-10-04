using System.Net;
using System.Text;
using Sweeply.Core.Capture;
using static Sweeply.Core.Tests.Capture.PacketBuilder;

namespace Sweeply.Core.Tests.Capture;

public class DissectorTests
{
    private static Dissection One(CapturedPacket packet) => DissectAll(new[] { packet })[0];

    [Fact]
    public void Tcp_syn_columns_and_fields_like_wireshark()
    {
        var d = One(TcpPacket(true, 3456789012, 0, Syn, 64240, options: SynOptions));
        Assert.Equal("192.0.2.23", d.Source);
        Assert.Equal("203.0.113.10", d.Destination);
        Assert.Equal("TCP", d.Protocol);
        Assert.Equal("51432 → 443 [SYN] Seq=0 Win=64240 Len=0 MSS=1460 WS=256 SACK_PERM", d.Info);
        Assert.Equal(new[] { "frame", "eth", "ip", "tcp" }, d.Protocols);
        Assert.Equal(51432, d.Field("tcp.srcport"));
        Assert.Equal(true, d.Field("tcp.flags.syn"));
        Assert.Equal(false, d.Field("tcp.flags.ack"));
        Assert.Equal(1460, d.Field("tcp.options.mss_val"));
        Assert.Equal(0L, d.Field("tcp.seq"));
        Assert.Equal(3456789012L, d.Field("tcp.seq_raw"));
        Assert.Equal(PacketColor.TcpSynFin, d.Color);
        Assert.Equal(IPAddress.Parse("192.0.2.23"), d.Field("ip.src"));
        Assert.Equal("eth:ip:tcp", d.Field("frame.protocols"));
    }

    [Theory]
    [InlineData(Syn | 0x40 | 0x80, "[SYN, ECE, CWR]")]
    [InlineData(Syn | Ack | 0x40, "[SYN, ACK, ECE]")]
    [InlineData(Fin | Psh | Ack, "[FIN, PSH, ACK]")]
    [InlineData(Rst | Ack, "[RST, ACK]")]
    [InlineData(0, "[<None>]")]
    public void Tcp_flags_in_wiresharks_order(int flags, string shown)
    {
        Assert.Contains(shown, One(TcpPacket(true, 1, 1, flags, serverPort: 9000)).Info);
    }

    [Fact]
    public void Every_field_with_bytes_points_inside_the_packet()
    {
        var packet = TcpPacket(true, 1, 1, Psh | Ack, payload: ClientHello("www.example.com"));
        var d = One(packet);
        foreach (var node in d.Fields().Where(n => n.Offset >= 0))
            Assert.True(node.Offset + node.Length <= packet.Data.Length, $"{node.Field} at {node.Offset}+{node.Length}");
    }

    [Fact]
    public void Dns_query_and_response_over_raw_ip()
    {
        var query = Packet(Ipv4(ClientIp, IPAddress.Parse("192.0.2.1"), 17, Udp(51000, 53, DnsQuery(0x1a2b, "www.example.com"))), LinkType.Raw);
        var answer = Packet(Ipv4(IPAddress.Parse("192.0.2.1"), ClientIp, 17,
            Udp(53, 51000, DnsResponse(0x1a2b, "www.example.com", "edge.example.net", IPAddress.Parse("198.51.100.7")))), LinkType.Raw, 0.01);
        var d = DissectAll(new[] { query, answer });
        Assert.Equal("DNS", d[0].Protocol);
        Assert.Equal("Standard query 0x1a2b A www.example.com", d[0].Info);
        Assert.Equal("www.example.com", d[0].Field("dns.qry.name"));
        Assert.Equal(false, d[0].Field("dns.flags.response"));
        Assert.Equal("Standard query response 0x1a2b A www.example.com CNAME edge.example.net A 198.51.100.7", d[1].Info);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), d[1].Field("dns.a"));
        Assert.Equal("edge.example.net", d[1].Field("dns.cname"));
        Assert.Equal(0, d[0].Field("udp.stream"));
        Assert.Equal(0, d[1].Field("udp.stream"));
        Assert.Equal(PacketColor.Udp, d[0].Color);
    }

    [Fact]
    public void Dns_name_pointer_loops_are_malformed_not_endless()
    {
        var message = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1 }; // name points at itself
        var d = One(Packet(Ipv4(ClientIp, ServerIp, 17, Udp(51000, 53, message.ToArray())), LinkType.Raw));
        Assert.Contains("[Malformed Packet]", d.Info);
    }

    [Fact]
    public void Arp_request_reply_and_gratuitous()
    {
        byte[] Arp(int op, byte[] senderMac, IPAddress senderIp, byte[] targetMac, IPAddress targetIp) => Concat(
            new byte[] { 0, 1, 8, 0, 6, 4, 0, (byte)op }, senderMac, senderIp.GetAddressBytes(), targetMac, targetIp.GetAddressBytes());
        var gateway = IPAddress.Parse("192.0.2.1");
        var request = One(Packet(Ethernet(Broadcast, MacB, 0x0806, Arp(1, MacB, ClientIp, new byte[6], gateway))));
        Assert.Equal("ARP", request.Protocol);
        Assert.Equal("Who has 192.0.2.1? Tell 192.0.2.23", request.Info);
        Assert.Equal("Broadcast", request.Destination);
        Assert.Equal("00:00:5e:00:53:2a", request.Source);
        Assert.Equal(PacketColor.Arp, request.Color);
        var reply = One(Packet(Ethernet(MacB, MacA, 0x0806, Arp(2, MacA, gateway, MacB, ClientIp))));
        Assert.Equal("192.0.2.1 is at 00:00:5e:00:53:01", reply.Info);
        var gratuitous = One(Packet(Ethernet(Broadcast, MacA, 0x0806, Arp(1, MacA, gateway, new byte[6], gateway))));
        Assert.Equal("Gratuitous ARP for 192.0.2.1 (Request)", gratuitous.Info);
    }

    [Fact]
    public void Icmp_echo_and_error()
    {
        var echo = Concat(new byte[] { 8, 0, 0, 0, 0, 1, 0, 1 }, new byte[32]);
        var d = One(Packet(Ethernet(MacB, MacA, 0x0800, Ipv4(ClientIp, ServerIp, 1, echo, ttl: 128))));
        Assert.Equal("ICMP", d.Protocol);
        Assert.Equal("Echo (ping) request  id=0x0001, seq=1/256, ttl=128", d.Info);
        Assert.Equal(PacketColor.Icmp, d.Color);

        var quoted = Ipv4(ClientIp, ServerIp, 17, Udp(51000, 33434, new byte[8]));
        var unreachable = Concat(new byte[] { 3, 3, 0, 0, 0, 0, 0, 0 }, quoted.Take(28).ToArray());
        var e = One(Packet(Ipv4(ServerIp, ClientIp, 1, unreachable), LinkType.Raw));
        Assert.Equal("Destination unreachable (Port unreachable)", e.Info);
        Assert.Equal(PacketColor.IcmpError, e.Color);
        Assert.Equal(IPAddress.Parse("203.0.113.10"), e.Field("icmp.ip.dst"));
    }

    [Fact]
    public void Tls_client_hello_with_server_name()
    {
        var d = One(TcpPacket(true, 1, 1, Psh | Ack, payload: ClientHello("www.example.com")));
        Assert.Equal("TLSv1.3", d.Protocol);
        Assert.Equal("Client Hello (SNI=www.example.com)", d.Info);
        Assert.Equal("www.example.com", d.Field("tls.handshake.extensions_server_name"));
        Assert.Equal("h2", d.Field("tls.handshake.extensions_alpn_str"));
        Assert.Equal(1, d.Field("tls.handshake.type"));
    }

    [Fact]
    public void Tls_application_data_and_records_split_across_packets()
    {
        var record = Concat(new byte[] { 23, 3, 3, 0, 40 }, new byte[40]);
        var d = One(TcpPacket(false, 1, 1, Psh | Ack, payload: Concat(record, new byte[] { 23, 3, 3, 0x10, 0 }, new byte[100])));
        Assert.Equal("Application Data, Application Data", d.Info);
        Assert.Contains(d.Layers.Single(l => l.Field == "tls").Walk(), n => n.Text.Contains("continues in the next packet"));
        var continuation = One(TcpPacket(false, 1, 1, Ack, payload: new byte[200]));
        Assert.Equal("Continuation Data", continuation.Info); // port 443, no record header
    }

    [Fact]
    public void Http_request_and_response()
    {
        var request = One(TcpPacket(true, 1, 1, Psh | Ack, serverPort: 80,
            payload: Encoding.ASCII.GetBytes("GET /index.html HTTP/1.1\r\nHost: www.example.com\r\nUser-Agent: test\r\n\r\n")));
        Assert.Equal("HTTP", request.Protocol);
        Assert.Equal("GET /index.html HTTP/1.1", request.Info);
        Assert.Equal("www.example.com", request.Field("http.host"));
        Assert.Equal("GET", request.Field("http.request.method"));
        Assert.Equal(PacketColor.Http, request.Color);
        var response = One(TcpPacket(false, 1, 1, Psh | Ack, serverPort: 80,
            payload: Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: 12\r\n\r\nnot found...")));
        Assert.Equal("HTTP/1.1 404 Not Found  (text/html)", response.Info);
        Assert.Equal(404, response.Field("http.response.code"));
        Assert.Equal(12L, response.Field("http.content_length"));
    }

    private static byte[] MqttString(string text) => Concat(U16((ushort)Encoding.UTF8.GetByteCount(text)), Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Mqtt_connect_and_publish_in_one_segment()
    {
        var connectBody = Concat(MqttString("MQTT"), new byte[] { 4, 0x02, 0, 60 }, MqttString("probe-01"));
        var connect = Concat(new byte[] { 0x10, (byte)connectBody.Length }, connectBody);
        var publishBody = Concat(MqttString("sensors/depth"), Encoding.ASCII.GetBytes("{\"depth\":12.5}"));
        var publish = Concat(new byte[] { 0x30, (byte)publishBody.Length }, publishBody);
        var d = One(TcpPacket(true, 1, 1, Psh | Ack, serverPort: 1883, payload: Concat(connect, publish)));
        Assert.Equal("MQTT", d.Protocol);
        Assert.Equal("Connect Command, Publish Message [sensors/depth]", d.Info);
        Assert.Equal("probe-01", d.Field("mqtt.clientid"));
        Assert.Equal("sensors/depth", d.Field("mqtt.topic"));
        Assert.Equal(4, d.Field("mqtt.ver"));
        Assert.Contains("{\"depth\":12.5}", d.Fields().Single(n => n.Field == "mqtt.msg").Text);
    }

    [Fact]
    public void Mqtt_v5_publish_skips_properties_once_the_connect_was_seen()
    {
        var connectBody = Concat(MqttString("MQTT"), new byte[] { 5, 0x02, 0, 60, 0 }, MqttString("c"));
        var connect = Concat(new byte[] { 0x10, (byte)connectBody.Length }, connectBody);
        var publishBody = Concat(MqttString("a/b"), new byte[] { 0 }, Encoding.ASCII.GetBytes("hi")); // empty properties
        var publish = Concat(new byte[] { 0x30, (byte)publishBody.Length }, publishBody);
        var d = DissectAll(new[]
        {
            TcpPacket(true, 1, 1, Psh | Ack, serverPort: 1883, payload: connect),
            TcpPacket(true, (uint)(1 + connect.Length), 1, Psh | Ack, serverPort: 1883, payload: publish, seconds: 0.1),
        });
        Assert.Equal("Publish Message [a/b]", d[1].Info);
        Assert.Equal("hi", d[1].Fields().Single(n => n.Field == "mqtt.msg").Text.Split(": ")[1]);
    }

    [Fact]
    public void Modbus_query_and_response()
    {
        var query = One(TcpPacket(true, 1, 1, Psh | Ack, serverPort: 502, payload: new byte[] { 0, 7, 0, 0, 0, 6, 1, 3, 0, 0x10, 0, 2 }));
        Assert.Equal("Modbus/TCP", query.Protocol);
        Assert.Equal("Query: Trans:     7; Unit:   1, Func:   3: Read Holding Registers", query.Info);
        Assert.Equal(16, query.Field("modbus.reference_num"));
        var response = One(TcpPacket(false, 1, 1, Psh | Ack, serverPort: 502, payload: new byte[] { 0, 7, 0, 0, 0, 7, 1, 3, 4, 0x01, 0x2C, 0x00, 0x05 }));
        Assert.Equal(new object?[] { 300, 5 }, response.FieldAll("modbus.regval_uint16").ToArray());
    }

    [Fact]
    public void Dhcp_discover()
    {
        var body = new byte[240];
        body[0] = 1; body[1] = 1; body[2] = 6;
        new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(body, 4);
        MacB.CopyTo(body, 28);
        new byte[] { 0x63, 0x82, 0x53, 0x63 }.CopyTo(body, 236);
        var options = new byte[] { 53, 1, 1, 12, 4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 255 };
        var d = One(Packet(Ethernet(Broadcast, MacB, 0x0800, Ipv4(IPAddress.Any, IPAddress.Broadcast, 17, Udp(68, 67, Concat(body, options))))));
        Assert.Equal("DHCP", d.Protocol);
        Assert.Equal("DHCP Discover - Transaction ID 0x12345678", d.Info);
        Assert.Equal("test", d.Field("dhcp.option.hostname"));
    }

    [Fact]
    public void Ipv6_with_hop_by_hop_header_and_icmpv6_neighbor_solicitation()
    {
        var target = IPAddress.Parse("2001:db8::1");
        var ns = Concat(new byte[] { 135, 0, 0, 0, 0, 0, 0, 0 }, target.GetAddressBytes(), new byte[] { 1, 1 }, MacB);
        var hopByHop = Concat(new byte[] { 58, 0, 5, 2, 0, 0, 1, 0 }, ns);
        var d = One(Packet(Ethernet(MacA, MacB, 0x86DD, Ipv6(IPAddress.Parse("2001:db8::23"), IPAddress.Parse("ff02::1:ff00:1"), 0, hopByHop))));
        Assert.Equal("ICMPv6", d.Protocol);
        Assert.Equal("Neighbor Solicitation for 2001:db8::1 from 00:00:5e:00:53:2a", d.Info);
        Assert.Equal("2001:db8::23", d.Source);
    }

    [Fact]
    public void Later_ip_fragments_are_not_decoded_as_transport()
    {
        var d = One(Packet(Ipv4(ClientIp, ServerIp, 17, new byte[100], fragmentOffset: 1480, dontFragment: false), LinkType.Raw));
        Assert.Equal("IPv4", d.Protocol);
        Assert.StartsWith("Fragmented IP protocol (proto=UDP 17, off=1480", d.Info);
    }

    [Fact]
    public void Offloaded_sends_with_ip_length_zero_still_decode()
    {
        var segment = Tcp(51432, 9000, 1, 1, Psh | Ack, 512, new byte[3000]); // a port with no protocol: plain TCP info
        var ip = Ipv4(ClientIp, ServerIp, 6, segment);
        ip[2] = 0; ip[3] = 0; // what raw sockets show for a large send split by the network card
        var d = One(Packet(ip, LinkType.Raw));
        Assert.Contains("Len=3000", d.Info);
        Assert.Contains(d.Fields(), n => n.Field == "ip.len" && n.Text.Contains("offload"));
    }

    [Fact]
    public void Truncated_packets_are_marked_malformed()
    {
        var full = TcpPacket(true, 1, 1, Ack).Data;
        var cut = Packet(full[..40]); // the TCP header is cut after 6 bytes
        var d = One(cut);
        Assert.Contains("[Malformed Packet]", d.Info);
        Assert.Contains(d.Fields(), n => n.Field == "_ws.malformed");
    }

    [Fact]
    public void Random_bytes_never_make_the_decoder_throw()
    {
        var random = new Random(20261004);
        var links = Enum.GetValues<LinkType>();
        var seeds = new[]
        {
            TcpPacket(true, 1, 1, Psh | Ack, payload: ClientHello("a.example")).Data,
            TcpPacket(true, 1, 1, Psh | Ack, serverPort: 1883, payload: new byte[] { 0x30, 5, 0, 1, (byte)'a', 1, 2 }).Data,
            Packet(Ipv4(ClientIp, ServerIp, 17, Udp(5353, 5353, DnsQuery(1, "x.local")))).Data,
        };
        for (int i = 0; i < 20000; i++)
        {
            byte[] data;
            if (i % 2 == 0)
            {
                data = new byte[random.Next(0, 200)];
                random.NextBytes(data);
            }
            else
            {
                // A real packet with a few bytes changed: gets deep into the protocol decoders.
                data = (byte[])seeds[random.Next(seeds.Length)].Clone();
                for (int k = 0; k < 4; k++) data[random.Next(data.Length)] = (byte)random.Next(256);
                if (random.Next(3) == 0) data = data[..random.Next(data.Length)];
            }
            var packet = Packet(data, links[random.Next(links.Length)]);
            var stream = new StreamTracker().Process(packet, 1);
            var d = Dissector.Dissect(packet, 1, stream, packet.TimestampUtc);
            Assert.NotNull(d.Protocol);
        }
    }
}
