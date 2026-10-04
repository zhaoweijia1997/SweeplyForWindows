using System.Net;
using System.Text;
using Sweeply.Core.Capture;
using static Sweeply.Core.Tests.Capture.PacketBuilder;

namespace Sweeply.Core.Tests.Capture;

public class StreamFollowerTests
{
    private static byte[] Text(string s) => Encoding.ASCII.GetBytes(s);

    private static FollowedStream Follow(List<CapturedPacket> packets, bool tcp = true, int stream = 0)
    {
        var tracker = new StreamTracker();
        var streams = packets.Select((p, i) => tracker.Process(p, i + 1)).ToList();
        return StreamFollower.Follow(packets, streams, tcp, stream)!;
    }

    private static string Show(FollowedStream f) =>
        string.Join("|", f.Chunks.Select(c => (c.FromA ? "A:" : "B:") + (c.MissingBefore > 0 ? $"[{c.MissingBefore} missing]" : "") + Encoding.ASCII.GetString(c.Data)));

    /// <summary>Handshake with the client's data starting at 1001 and the server's at 5001.</summary>
    private static List<CapturedPacket> Opened(uint client = 1000, uint server = 5000) => new()
    {
        TcpPacket(true, client, 0, Syn, 64240, seconds: 0),
        TcpPacket(false, server, client + 1, Syn | Ack, 65535, seconds: 0.01),
        TcpPacket(true, client + 1, server + 1, Ack, 1024, seconds: 0.02),
    };

    [Fact]
    public void A_request_and_its_answer_in_two_parts()
    {
        var packets = Opened();
        packets.Add(TcpPacket(true, 1001, 5001, Psh | Ack, payload: Text("GET / HTTP/1.1\r\n\r\n"), seconds: 0.03));
        packets.Add(TcpPacket(false, 5001, 1019, Ack, payload: Text("HTTP/1.1 200 OK\r\n\r\n"), seconds: 0.04));
        packets.Add(TcpPacket(false, 5020, 1019, Psh | Ack, payload: Text("hello"), seconds: 0.05));
        packets.Add(TcpPacket(true, 1019, 5025, Ack, seconds: 0.06));
        var f = Follow(packets);
        Assert.Equal("A:GET / HTTP/1.1\r\n\r\n|B:HTTP/1.1 200 OK\r\n\r\nhello", Show(f));
        Assert.Equal((ClientIp, 51432, ServerIp, 443), (f.AddressA, f.PortA, f.AddressB, f.PortB));
        Assert.Equal((18L, 24L, 0L, 7), (f.BytesFromA, f.BytesFromB, f.Missing, f.Packets));
        Assert.Equal((4, 5), (f.Chunks[0].FirstFrame, f.Chunks[1].FirstFrame));
        Assert.Equal("tcp.stream == 0", f.Filter);
        Assert.Equal(Text("GET / HTTP/1.1\r\n\r\n"), f.Bytes(fromA: true));
    }

    [Fact]
    public void What_is_sent_again_counts_once_and_what_comes_early_waits()
    {
        var packets = Opened();
        packets.Add(TcpPacket(false, 5001, 1001, Ack, payload: Text("one-"), seconds: 0.03));
        packets.Add(TcpPacket(false, 5009, 1001, Ack, payload: Text("three"), seconds: 0.04)); // early: "two-" is missing yet
        packets.Add(TcpPacket(false, 5001, 1001, Ack, payload: Text("one-"), seconds: 0.05));  // sent again
        packets.Add(TcpPacket(false, 5005, 1001, Ack, payload: Text("two-"), seconds: 0.06));  // the missing one
        packets.Add(TcpPacket(false, 5007, 1001, Ack, payload: Text("o-thr"), seconds: 0.07)); // overlaps what is there
        packets.Add(TcpPacket(false, 5012, 1001, Ack, payload: Text("ee!"), seconds: 0.08));   // overlaps, and goes on
        Assert.Equal("B:one-two-three!", Show(Follow(packets)));
    }

    [Fact]
    public void Data_never_captured_is_marked_as_missing()
    {
        var packets = Opened();
        packets.Add(TcpPacket(true, 1001, 5001, Ack, payload: Text("abc"), seconds: 0.03));
        packets.Add(TcpPacket(true, 1014, 5001, Ack, payload: Text("xyz"), seconds: 0.04)); // 10 bytes before it never came
        packets.Add(TcpPacket(false, 5001, 1017, Ack, payload: Text("ok"), seconds: 0.05));
        var f = Follow(packets);
        Assert.Equal("A:abc|B:ok|A:[10 missing]xyz", Show(f));
        Assert.Equal(10, f.Missing);
    }

    [Fact]
    public void A_capture_that_starts_late_still_follows()
    {
        // No SYN: the first data starts the stream. A SYN-ACK first: the other end opened the connection.
        var lateData = new List<CapturedPacket>
        {
            TcpPacket(false, 9000, 1001, Ack, payload: Text("late"), seconds: 0),
            TcpPacket(true, 1001, 9004, Ack, payload: Text("reply"), seconds: 0.01),
        };
        var f = Follow(lateData);
        Assert.Equal("A:late|B:reply", Show(f));
        Assert.Equal(ServerIp, f.AddressA);

        var lateSyn = new List<CapturedPacket>
        {
            TcpPacket(false, 5000, 1001, Syn | Ack, 65535, seconds: 0),
            TcpPacket(true, 1001, 5001, Ack, payload: Text("hi"), seconds: 0.01),
        };
        f = Follow(lateSyn);
        Assert.Equal(ClientIp, f.AddressA);
        Assert.Equal("A:hi", Show(f));
    }

    [Fact]
    public void Sequence_numbers_that_wrap_around()
    {
        var packets = Opened(client: 0xFFFFFFF0);
        packets.Add(TcpPacket(true, 0xFFFFFFF1, 5001, Ack, payload: Text("0123456789ABCDEF"), seconds: 0.03)); // crosses 2^32
        packets.Add(TcpPacket(true, 0x00000001, 5001, Ack, payload: Text("next"), seconds: 0.04));
        Assert.Equal("A:0123456789ABCDEFnext", Show(Follow(packets)));
    }

    [Fact]
    public void A_udp_stream()
    {
        var router = IPAddress.Parse("192.0.2.1");
        var packets = new List<CapturedPacket>
        {
            Packet(Ethernet(MacB, MacA, 0x0800, Ipv4(ClientIp, router, 17, Udp(53000, 53, Text("ask")))), seconds: 0),
            Packet(Ethernet(MacA, MacB, 0x0800, Ipv4(router, ClientIp, 17, Udp(53, 53000, Text("answer")))), seconds: 0.01),
        };
        var f = Follow(packets, tcp: false);
        Assert.Equal("UDP", f.Protocol);
        Assert.Equal("A:ask|B:answer", Show(f));
        Assert.Equal("udp.stream == 0", f.Filter);
    }

    [Fact]
    public void A_stream_that_isnt_there()
    {
        Assert.Null(StreamFollower.Follow(Opened(), Opened().Select(_ => StreamInfo.None).ToList(), tcp: true, stream: 0));
    }
}
