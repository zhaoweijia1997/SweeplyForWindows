using System.Buffers.Binary;
using Sweeply.Core.Capture;
using static Sweeply.Core.Tests.Capture.PacketBuilder;

namespace Sweeply.Core.Tests.Capture;

public class StreamTrackerTests
{
    /// <summary>A handshake (with window scaling), a request, the reply in two parts, and the client's ACKs.</summary>
    private static List<CapturedPacket> Conversation(uint client = 1000, uint server = 5000) => new()
    {
        TcpPacket(true, client, 0, Syn, 64240, options: SynOptions, seconds: 0.000),
        TcpPacket(false, server, client + 1, Syn | Ack, 65535, options: SynOptions, seconds: 0.010),
        TcpPacket(true, client + 1, server + 1, Ack, 1024, seconds: 0.011),
        TcpPacket(true, client + 1, server + 1, Psh | Ack, 1024, new byte[100], seconds: 0.012),
        TcpPacket(false, server + 1, client + 101, Ack, 512, new byte[1400], seconds: 0.030),
        TcpPacket(false, server + 1401, client + 101, Psh | Ack, 512, new byte[600], seconds: 0.031),
        TcpPacket(true, client + 101, server + 2001, Ack, 1024, seconds: 0.032),
    };

    private static List<StreamInfo> Track(IReadOnlyList<CapturedPacket> packets)
    {
        var tracker = new StreamTracker();
        return packets.Select((p, i) => tracker.Process(p, i + 1)).ToList();
    }

    [Fact]
    public void Relative_numbers_and_window_scaling()
    {
        var s = Track(Conversation());
        Assert.All(s, x => Assert.Equal(0, x.TcpStream));
        Assert.Equal(0u, s[0].RelativeSeq);
        Assert.Equal(1u, s[1].RelativeAck);
        Assert.Equal((1u, 1u), (s[3].RelativeSeq, s[3].RelativeAck));
        Assert.Equal(101u, s[3].NextSeq);
        Assert.Equal(1401u, s[5].RelativeSeq);
        Assert.Equal(2001u, s[6].RelativeAck);
        Assert.Equal(0, s[0].WindowShift == -1 ? 0 : 1); // only the first SYN seen: not known yet
        Assert.Equal(8, s[2].WindowShift);              // both SYNs had "WS=256"
        Assert.All(s, x => Assert.Equal(TcpAnalysis.None, x.Analysis));
        var d = DissectAll(Conversation());
        Assert.Contains("Win=262144", d[2].Info); // 1024 << 8
    }

    [Fact]
    public void Retransmission_duplicate_acks_and_lost_segments()
    {
        var packets = Conversation();
        packets.Add(TcpPacket(false, 5001 + 2000, 1101, Psh | Ack, 512, new byte[500], seconds: 0.040));     // 8: next data
        packets.Add(TcpPacket(false, 5001 + 3000, 1101, Psh | Ack, 512, new byte[100], seconds: 0.041));     // 9: a gap before it
        packets.Add(TcpPacket(true, 1101, 5001 + 2500, Ack, 1024, seconds: 0.042));                         // 10: ACK
        packets.Add(TcpPacket(true, 1101, 5001 + 2500, Ack, 1024, seconds: 0.043));                         // 11: dup ACK #1
        packets.Add(TcpPacket(true, 1101, 5001 + 2500, Ack, 1024, seconds: 0.044));                         // 12: dup ACK #2
        packets.Add(TcpPacket(false, 5001 + 2500, 1101, Psh | Ack, 512, new byte[500], seconds: 0.045));     // 13: fast retransmission
        packets.Add(TcpPacket(false, 5001 + 2000, 1101, Psh | Ack, 512, new byte[500], seconds: 0.500));     // 14: plain retransmission
        var s = Track(packets);
        Assert.True(s[8].Analysis.HasFlag(TcpAnalysis.LostSegment));
        Assert.True(s[10].Analysis.HasFlag(TcpAnalysis.DuplicateAck));
        Assert.Equal((10, 1), (s[10].DupAckFrame, s[10].DupAckCount));
        Assert.Equal(2, s[11].DupAckCount);
        Assert.True(s[12].Analysis.HasFlag(TcpAnalysis.FastRetransmission));
        Assert.True(s[13].Analysis.HasFlag(TcpAnalysis.Retransmission));
        Assert.False(s[13].Analysis.HasFlag(TcpAnalysis.FastRetransmission));
        var d = DissectAll(packets);
        Assert.StartsWith("[TCP Dup ACK 10#1] 51432 → 443 [ACK]", d[10].Info);
        Assert.StartsWith("[TCP Fast Retransmission] ", d[12].Info);
        Assert.StartsWith("[TCP Retransmission] ", d[13].Info);
        Assert.StartsWith("[TCP Previous segment not captured] ", d[8].Info);
        Assert.Equal(PacketColor.BadTcp, d[13].Color);
        Assert.Equal(true, d[13].Field("tcp.analysis.retransmission"));
    }

    [Fact]
    public void Zero_window_keep_alive_and_reset()
    {
        var packets = Conversation();
        packets.Add(TcpPacket(true, 1101, 7001, Ack, 0, seconds: 0.1));                       // zero window
        packets.Add(TcpPacket(false, 7000, 1101, Ack, 512, new byte[1], seconds: 0.2));        // keep-alive: one byte before next
        packets.Add(TcpPacket(true, 1101, 7001, Ack, 1024, seconds: 0.21));
        packets.Add(TcpPacket(true, 1101, 7001, Rst | Ack, 0, seconds: 0.3));
        var s = Track(packets);
        Assert.True(s[7].Analysis.HasFlag(TcpAnalysis.ZeroWindow));
        Assert.True(s[8].Analysis.HasFlag(TcpAnalysis.KeepAlive));
        Assert.Equal(TcpAnalysis.None, s[10].Analysis & TcpAnalysis.ZeroWindow); // no zero-window note on a reset
        Assert.Equal(PacketColor.TcpReset, DissectAll(packets)[10].Color);
    }

    [Fact]
    public void Separate_conversations_and_reused_ports()
    {
        var packets = Conversation();
        packets.Add(TcpPacket(true, 1000, 0, Syn, 64240, options: SynOptions, clientPort: 51433, seconds: 1)); // another port
        packets.Add(TcpPacket(true, 90000, 0, Syn, 64240, options: SynOptions, seconds: 2));                   // same ports, new ISN
        var s = Track(packets);
        Assert.Equal(1, s[7].TcpStream);
        Assert.Equal(2, s[8].TcpStream);
    }

    [Fact]
    public void Tls_13_is_known_for_the_rest_of_the_conversation()
    {
        // A ServerHello choosing TLS 1.3 in supported_versions.
        var serverHelloBody = Concat(new byte[] { 3, 3 }, new byte[32], new byte[] { 0 }, new byte[] { 0x13, 0x01 }, new byte[] { 0 },
            U16(6), U16(43), U16(2), new byte[] { 3, 4 });
        var handshake = Concat(new byte[] { 2, 0, 0, (byte)serverHelloBody.Length }, serverHelloBody);
        var record = Concat(new byte[] { 22, 3, 3 }, U16((ushort)handshake.Length), handshake);
        var packets = Conversation();
        packets.Add(TcpPacket(false, 7001, 1101, Psh | Ack, payload: record, seconds: 0.1));
        packets.Add(TcpPacket(false, 7001 + (uint)record.Length, 1101, Psh | Ack, payload: Concat(new byte[] { 23, 3, 3, 0, 20 }, new byte[20]), seconds: 0.2));
        var d = DissectAll(packets);
        Assert.Equal("TLSv1.3", d[7].Protocol);
        Assert.Equal("Server Hello", d[7].Info);
        Assert.Equal("TLSv1.3", d[8].Protocol);
        Assert.Equal("Application Data", d[8].Info);
    }

    [Fact]
    public void Quick_header_reads_ipv6_with_extension_headers()
    {
        var udp = Udp(5353, 5353, new byte[4]);
        var hopByHop = Concat(new byte[] { 17, 0, 1, 4, 0, 0, 0, 0 }, udp);
        var packet = Packet(Ipv6(System.Net.IPAddress.Parse("2001:db8::1"), System.Net.IPAddress.Parse("ff02::fb"), 0, hopByHop), LinkType.Raw);
        var h = QuickHeader.Read(packet.Link, packet.Data);
        Assert.True(h.IsUdp);
        Assert.Equal((5353, 5353, 4), (h.SourcePort, h.DestinationPort, h.PayloadLength));
    }
}

public class PcapFileTests
{
    [Fact]
    public void Pcapng_round_trip_keeps_time_direction_program_and_comments()
    {
        var packets = new List<CapturedPacket>
        {
            new() { TimestampUtc = new DateTime(2026, 10, 4, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234567), Link = LinkType.Raw, Data = new byte[] { 0x45, 1, 2 },
                Direction = PacketDirection.Outbound, ProcessName = "chrome.exe", ProcessId = 9812 },
            new() { TimestampUtc = new DateTime(2026, 10, 4, 1, 2, 4, DateTimeKind.Utc), Link = LinkType.Ethernet, Data = new byte[61], OriginalLength = 1514,
                Direction = PacketDirection.Inbound, Comment = "a note" },
        };
        var stream = new MemoryStream();
        using (var writer = new PcapNgWriter(stream, "SweeplyForWindows test", leaveOpen: true))
            foreach (var p in packets) writer.Write(p, writer.Interface(p.Link, p.Link == LinkType.Raw ? "Wi-Fi" : "Ethernet"));
        Assert.Equal(0, stream.Length % 4);

        stream.Position = 0;
        var file = PcapReader.Read(stream);
        Assert.Equal("SweeplyForWindows test", file.Application);
        Assert.Equal(new[] { "Wi-Fi", "Ethernet" }, file.Interfaces.Select(i => i.Name));
        Assert.Equal(2, file.Packets.Count);
        var a = file.Packets[0];
        Assert.Equal(packets[0].TimestampUtc, a.TimestampUtc);
        Assert.Equal(LinkType.Raw, a.Link);
        Assert.Equal(packets[0].Data, a.Data);
        Assert.Equal(PacketDirection.Outbound, a.Direction);
        Assert.Equal(("chrome.exe", 9812), (a.ProcessName, a.ProcessId));
        Assert.Null(a.Comment);
        var b = file.Packets[1];
        Assert.Equal((1514, 61, 1), (b.Length, b.Data.Length, b.InterfaceId));
        Assert.Equal("a note", b.Comment);
        Assert.Null(b.ProcessName);
        Assert.False(file.Truncated);
    }

    /// <summary>A classic pcap file, written by hand in either byte order and time resolution.</summary>
    private static byte[] Pcap(bool bigEndian, bool nano, params (uint Seconds, uint Fraction, byte[] Data)[] records)
    {
        var bytes = new List<byte>();
        void U32(uint v) { var b = new byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); bytes.AddRange(b); }
        void U16(ushort v) { var b = new byte[2]; if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v); bytes.AddRange(b); }
        U32(nano ? 0xA1B23C4D : 0xA1B2C3D4);
        U16(2); U16(4); U32(0); U32(0); U32(65535); U32(1); // Ethernet
        foreach (var (seconds, fraction, data) in records)
        {
            U32(seconds); U32(fraction); U32((uint)data.Length); U32((uint)data.Length);
            bytes.AddRange(data);
        }
        return bytes.ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Classic_pcap_in_any_byte_order_and_resolution(bool bigEndian, bool nano)
    {
        var raw = Pcap(bigEndian, nano, (1_790_000_000, nano ? 123_456_700u : 123_456u, new byte[] { 1, 2, 3 }), (1_790_000_001, 0, new byte[60]));
        var file = PcapReader.Read(new MemoryStream(raw));
        Assert.Equal(2, file.Packets.Count);
        Assert.Equal(LinkType.Ethernet, file.Packets[0].Link);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_790_000_000).AddTicks(1_234_560 + (nano ? 7 : 0)), file.Packets[0].TimestampUtc);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Packets[0].Data);
    }

    [Fact]
    public void A_file_cut_short_keeps_the_whole_packets_and_says_so()
    {
        var raw = Pcap(false, false, (1, 0, new byte[100]), (2, 0, new byte[100]));
        var file = PcapReader.Read(new MemoryStream(raw[..(raw.Length - 30)]));
        Assert.Single(file.Packets);
        Assert.True(file.Truncated);
    }

    [Fact]
    public void Limits_stop_reading_early()
    {
        var raw = Pcap(false, false, (1, 0, new byte[100]), (2, 0, new byte[100]), (3, 0, new byte[100]));
        var file = PcapReader.Read(new MemoryStream(raw), new PcapReader.Limits(2, long.MaxValue));
        Assert.Equal(2, file.Packets.Count);
        Assert.True(file.Truncated);
    }

    [Fact]
    public void Other_files_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => PcapReader.Read(new MemoryStream(new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0 })));
        Assert.Throws<InvalidDataException>(() => PcapReader.Read(new MemoryStream(Array.Empty<byte>())));
    }

    [Fact]
    public void Process_comments_are_read_back_and_other_comments_kept()
    {
        Assert.Equal(("svchost.exe", 1388, (string?)null), PcapReader.ProcessFromComment("SweeplyForWindows process: svchost.exe (1388)"));
        Assert.Equal(("a b.exe", 7, "hello"), PcapReader.ProcessFromComment("hello\nSweeplyForWindows process: a b.exe (7)"));
        Assert.Equal(((string?)null, 0, "just a note"), PcapReader.ProcessFromComment("just a note"));
    }
}
