using Sweeply.Core.Capture;
using static Sweeply.Core.Tests.Capture.PacketBuilder;

namespace Sweeply.Core.Tests.Capture;

/// <summary>802.11 frames, which Windows' packet capture hands over for Wi-Fi cards.</summary>
public class WlanTests
{
    private static byte[] Syn() => Tcp(51432, 443, 1000, 0, PacketBuilder.Syn, 64240, options: SynOptions);

    [Fact]
    public void A_frame_sent_to_the_access_point_is_decoded_down_to_tcp()
    {
        var frame = Wifi(toAccessPoint: true, destination: MacB, source: MacA, 0x0800, Ipv4(ClientIp, ServerIp, 6, Syn()));
        var d = DissectAll(new[] { Packet(frame, LinkType.Ieee80211) })[0];
        Assert.Equal(new[] { "frame", "wlan", "llc", "ip", "tcp" }, d.Protocols);
        Assert.Equal(("192.0.2.23", "203.0.113.10", "TCP"), (d.Source, d.Destination, d.Protocol));
        Assert.StartsWith("51432 → 443 [SYN]", d.Info);
        Assert.Equal("IEEE 802.11 QoS Data, Flags: .......T", d.Layers[1].Text);
        Assert.Equal(MacA, d.Field("wlan.sa"));
        Assert.Equal(MacB, d.Field("wlan.da"));
        Assert.Equal(AccessPoint, d.Field("wlan.bssid"));
        Assert.Equal(AccessPoint, d.Field("wlan.ra"));
        Assert.Equal(0x0800, d.Field("llc.type"));

        var h = QuickHeader.Read(LinkType.Ieee80211, frame);
        Assert.True(h.IsTcp);
        Assert.Equal((51432, 443), (h.SourcePort, h.DestinationPort));
    }

    [Fact]
    public void A_frame_from_the_access_point_names_its_addresses_the_other_way()
    {
        var reply = Tcp(443, 51432, 5000, 1001, PacketBuilder.Syn | Ack, 65535);
        var frame = Wifi(toAccessPoint: false, destination: MacA, source: MacB, 0x0800, Ipv4(ServerIp, ClientIp, 6, reply), qos: false);
        var d = DissectAll(new[] { Packet(frame, LinkType.Ieee80211) })[0];
        Assert.Equal("IEEE 802.11 Data, Flags: ......F.", d.Layers[1].Text);
        Assert.Equal(MacB, d.Field("wlan.sa"));
        Assert.Equal(MacA, d.Field("wlan.da"));
        Assert.Equal(AccessPoint, d.Field("wlan.bssid"));
        Assert.Equal(("203.0.113.10", "192.0.2.23"), (d.Source, d.Destination));
        Assert.True(QuickHeader.Read(LinkType.Ieee80211, frame).IsTcp);
    }

    [Fact]
    public void Frames_without_data_are_named_and_not_taken_for_ip()
    {
        var frame = Wifi(toAccessPoint: true, destination: MacB, source: MacA, 0, Array.Empty<byte>(), noData: true);
        var d = DissectAll(new[] { Packet(frame, LinkType.Ieee80211) })[0];
        Assert.Equal(("802.11", "QoS Null function (No data), SN=1, FN=0, Flags=.......T"), (d.Protocol, d.Info));
        Assert.Equal(new[] { "frame", "wlan" }, d.Protocols);
        Assert.False(QuickHeader.Read(LinkType.Ieee80211, frame).IsIp);

        var beacon = new byte[40];
        beacon[0] = 0x80; // management, beacon
        Assert.Equal("Beacon frame, SN=0, FN=0, Flags=........", DissectAll(new[] { Packet(beacon, LinkType.Ieee80211) })[0].Info);
        var ack = new byte[10];
        ack[0] = 0xD4; // control, acknowledgement (no sequence number)
        ack[1] = 0x48; // retry, protected
        Assert.Equal("Acknowledgement, Flags=.p..R...", DissectAll(new[] { Packet(ack, LinkType.Ieee80211) })[0].Info);
    }

    [Fact]
    public void Arp_and_ipv6_over_wifi()
    {
        var arp = Concat(U16(1), U16(0x0800), new byte[] { 6, 4 }, U16(1), MacA, ClientIp.GetAddressBytes(), new byte[6], ServerIp.GetAddressBytes());
        var d = DissectAll(new[] { Packet(Wifi(true, Broadcast, MacA, 0x0806, arp), LinkType.Ieee80211) })[0];
        Assert.Equal("ARP", d.Protocol);
        Assert.Equal("Broadcast", d.Destination);

        var v6 = Ipv6(System.Net.IPAddress.Parse("2001:db8::23"), System.Net.IPAddress.Parse("2001:db8::10"), 17, Udp(5353, 5353, new byte[4]));
        var frame = Wifi(true, MacB, MacA, 0x86DD, v6);
        Assert.True(QuickHeader.Read(LinkType.Ieee80211, frame).IsUdp);
        Assert.Equal("2001:db8::23", DissectAll(new[] { Packet(frame, LinkType.Ieee80211) })[0].Source);
    }

    [Fact]
    public void Every_field_of_a_wifi_frame_can_become_a_filter_that_finds_it()
    {
        var p = Packet(Wifi(true, MacB, MacA, 0x0800, Ipv4(ClientIp, ServerIp, 6, Syn())), LinkType.Ieee80211);
        var d = DissectAll(new[] { p })[0];
        foreach (var node in d.Layers.SelectMany(l => l.Walk()))
            if (DisplayFilter.For(node) is { } text)
                Assert.True(DisplayFilter.Parse(text).Matches(d, p), text);
        Assert.True(DisplayFilter.Parse("wlan.sa == 00:00:5e:00:53:01 && tcp.flags.syn == 1").Matches(d, p));
    }
}
