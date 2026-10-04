using System.Buffers.Binary;
using System.Net;

namespace Sweeply.Core.Capture;

/// <summary>The IP and transport headers of a packet, read quickly without decoding anything else.</summary>
public struct QuickHeader
{
    public bool IsIp;
    public byte IpVersion;
    public AddressKey Source, Destination;
    public byte Protocol;          // 6 TCP, 17 UDP, 1 ICMP, 58 ICMPv6
    public bool LaterFragment;     // an IP fragment after the first: no transport header
    public int SourcePort, DestinationPort;
    public int IpOffset, TransportOffset, PayloadOffset, PayloadLength, IpPacketLength;
    public uint Seq, Ack;
    public byte TcpFlags;
    public ushort Window;
    public int WindowShift;        // the window scale option of a SYN; -1 when there is none

    public const byte Fin = 0x01, Syn = 0x02, Rst = 0x04, Psh = 0x08, AckFlag = 0x10;

    public readonly bool IsTcp => Protocol == 6 && !LaterFragment && TransportOffset > 0;
    public readonly bool IsUdp => Protocol == 17 && !LaterFragment && TransportOffset > 0;

    /// <summary>Reads the headers; <see cref="IsIp"/> is false for anything that isn't IP (ARP, broken packets).</summary>
    public static QuickHeader Read(LinkType link, byte[] data)
    {
        var h = new QuickHeader { WindowShift = -1 };
        int ip = IpStart(link, data);
        if (ip < 0 || ip >= data.Length) return h;
        int version = data[ip] >> 4;
        int next, end;
        if (version == 4)
        {
            if (ip + 20 > data.Length) return h;
            int headerLength = (data[ip] & 0x0F) * 4;
            int total = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(ip + 2));
            if (headerLength < 20 || ip + headerLength > data.Length) return h;
            end = total >= headerLength && ip + total <= data.Length ? ip + total : data.Length; // 0 or too long: offloaded send
            h.Protocol = data[ip + 9];
            h.LaterFragment = (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(ip + 6)) & 0x1FFF) != 0;
            h.Source = AddressKey.From(data.AsSpan(ip + 12, 4));
            h.Destination = AddressKey.From(data.AsSpan(ip + 16, 4));
            next = ip + headerLength;
        }
        else if (version == 6)
        {
            if (ip + 40 > data.Length) return h;
            int payload = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(ip + 4));
            end = payload > 0 && ip + 40 + payload <= data.Length ? ip + 40 + payload : data.Length;
            h.Source = AddressKey.From(data.AsSpan(ip + 8, 16));
            h.Destination = AddressKey.From(data.AsSpan(ip + 24, 16));
            byte header = data[ip + 6];
            next = ip + 40;
            // Hop-by-hop, routing, destination options and fragment headers come before the transport header.
            for (int guard = 0; guard < 8 && header is 0 or 43 or 60 or 44; guard++)
            {
                if (next + 8 > end) return h;
                if (header == 44)
                {
                    if ((BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(next + 2)) & 0xFFF8) != 0) h.LaterFragment = true;
                    header = data[next];
                    next += 8;
                }
                else
                {
                    int length = (data[next + 1] + 1) * 8;
                    header = data[next];
                    next += length;
                }
            }
            h.Protocol = header;
        }
        else return h;

        h.IsIp = true;
        h.IpVersion = (byte)version;
        h.IpOffset = ip;
        h.IpPacketLength = end - ip;
        if (h.LaterFragment || next > end) return h;
        if (h.Protocol == 6 && next + 20 <= end)
        {
            int headerLength = (data[next + 12] >> 4) * 4;
            if (headerLength < 20 || next + headerLength > end) return h;
            h.TransportOffset = next;
            h.SourcePort = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(next));
            h.DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(next + 2));
            h.Seq = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(next + 4));
            h.Ack = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(next + 8));
            h.TcpFlags = data[next + 13];
            h.Window = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(next + 14));
            h.PayloadOffset = next + headerLength;
            h.PayloadLength = end - h.PayloadOffset;
            if ((h.TcpFlags & Syn) != 0) h.WindowShift = WindowScaleOption(data.AsSpan(next + 20, headerLength - 20));
        }
        else if (h.Protocol == 17 && next + 8 <= end)
        {
            h.TransportOffset = next;
            h.SourcePort = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(next));
            h.DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(next + 2));
            h.PayloadOffset = next + 8;
            h.PayloadLength = end - h.PayloadOffset;
        }
        return h;
    }

    /// <summary>Where the IP header starts for each link type; -1 when there is no IP in it.</summary>
    internal static int IpStart(LinkType link, byte[] data)
    {
        switch (link)
        {
            case LinkType.Raw:
            case LinkType.Ipv4:
            case LinkType.Ipv6:
                return 0;
            case LinkType.Null:
            case LinkType.Loop:
                return data.Length >= 4 ? 4 : -1;
            case LinkType.Ethernet:
            {
                int at = 12;
                for (int tags = 0; tags < 2 && at + 2 <= data.Length; tags++)
                {
                    ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
                    if (type is 0x8100 or 0x88A8) { at += 4; continue; }
                    return type is 0x0800 or 0x86DD ? at + 2 : -1;
                }
                return -1;
            }
            case LinkType.Ieee80211:
            {
                // A data frame with a body: the MAC header (longer with four addresses, QoS, HT control), then LLC/SNAP.
                if (data.Length < 24) return -1;
                int type = (data[0] >> 2) & 3, subtype = data[0] >> 4, flags = data[1];
                if (type != 2 || (subtype & 4) != 0) return -1;
                bool qos = (subtype & 8) != 0;
                int at = 24 + ((flags & 3) == 3 ? 6 : 0) + (qos ? 2 : 0) + (qos && (flags & 0x80) != 0 ? 4 : 0);
                if (data.Length < at + 8 || data[at] != 0xAA || data[at + 1] != 0xAA || data[at + 2] != 0x03) return -1;
                return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 6)) is 0x0800 or 0x86DD ? at + 8 : -1;
            }
            case LinkType.LinuxSll:
                return data.Length >= 16 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(14)) is 0x0800 or 0x86DD ? 16 : -1;
            case LinkType.LinuxSll2:
                return data.Length >= 20 && BinaryPrimitives.ReadUInt16BigEndian(data) is 0x0800 or 0x86DD ? 20 : -1;
            default:
                return -1;
        }
    }

    private static int WindowScaleOption(ReadOnlySpan<byte> options)
    {
        for (int i = 0; i < options.Length;)
        {
            byte kind = options[i];
            if (kind == 0) break;
            if (kind == 1) { i++; continue; }
            if (i + 1 >= options.Length) break;
            int length = options[i + 1];
            if (length < 2) break;
            if (kind == 3 && length == 3 && i + 2 < options.Length) return Math.Min(options[i + 2], (byte)14);
            i += length;
        }
        return -1;
    }
}

/// <summary>An IPv4 or IPv6 address as two numbers, for fast dictionary keys (IPv4 in the low one).</summary>
public readonly record struct AddressKey(ulong High, ulong Low)
{
    public static AddressKey From(ReadOnlySpan<byte> bytes) => bytes.Length == 4
        ? new AddressKey(0, 0xFFFF00000000UL | BinaryPrimitives.ReadUInt32BigEndian(bytes))
        : new AddressKey(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));

    public static AddressKey From(IPAddress address) => From(address.GetAddressBytes());

    public IPAddress ToAddress()
    {
        if (High == 0 && (Low >> 32) == 0xFFFF)
        {
            var v4 = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(v4, (uint)Low);
            return new IPAddress(v4);
        }
        var v6 = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(v6, High);
        BinaryPrimitives.WriteUInt64BigEndian(v6.AsSpan(8), Low);
        return new IPAddress(v6);
    }

    public int CompareTo(AddressKey other) => High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);
}

/// <summary>What TCP's sequence and acknowledgement numbers say about a segment (Wireshark's tcp.analysis).</summary>
[Flags]
public enum TcpAnalysis : ushort
{
    None = 0,
    Retransmission = 1,
    FastRetransmission = 2,
    OutOfOrder = 4,
    LostSegment = 8,
    DuplicateAck = 16,
    ZeroWindow = 32,
    KeepAlive = 64,
    KeepAliveAck = 128,
    AckedUnseen = 256,
    WindowFull = 512,
}

/// <summary>What the tracker worked out for one packet, from the packets before it.</summary>
public struct StreamInfo
{
    /// <summary>Which TCP or UDP conversation (0, 1, 2… in order of appearance); -1 when neither.</summary>
    public int TcpStream, UdpStream;

    public bool HasRelative;
    public uint RelativeSeq, RelativeAck, NextSeq;
    public bool HasRelativeAck;
    public TcpAnalysis Analysis;
    public int DupAckFrame, DupAckCount;

    /// <summary>The window scale both sides agreed on in the handshake: -1 not seen (window shown unscaled), 0 none.</summary>
    public int WindowShift;

    /// <summary>The TLS version the server chose for this conversation (0x0304 for TLS 1.3), when seen so far.</summary>
    public ushort TlsVersion;

    /// <summary>The MQTT protocol level from this conversation's CONNECT (3, 4 or 5), when seen so far.</summary>
    public byte MqttLevel;

    public static StreamInfo None => new() { TcpStream = -1, UdpStream = -1, WindowShift = -1 };
}

/// <summary>
/// Follows TCP and UDP conversations through the packets in the order they were captured, as Wireshark does:
/// numbers the conversations, makes sequence numbers relative to the start, and flags retransmissions,
/// duplicate ACKs, zero windows, keep-alives and segments the capture missed. Also remembers, per conversation,
/// the TLS version chosen and the MQTT protocol level, which later packets need to be shown right.
/// </summary>
public sealed class StreamTracker
{
    private sealed class Direction
    {
        public bool BaseSet, NextSet, AckSeen, SynSeen, LastWasKeepAlive;
        public uint Base, Next, LastAck;
        public ushort LastWindow;
        public int WindowShift = -1;
        public int DupAcks, LastAckFrame;
        public long LastTicks;
    }

    private sealed class TcpConversation
    {
        public int Index;
        public readonly Direction A = new(), B = new(); // A: from the lower endpoint
        public ushort TlsVersion;
        public byte MqttLevel;
    }

    private readonly record struct Key(AddressKey Low, int LowPort, AddressKey High, int HighPort);

    private readonly Dictionary<Key, TcpConversation> _tcp = new();
    private readonly Dictionary<Key, int> _udp = new();
    private int _tcpCount;
    private static readonly long OutOfOrderTicks = TimeSpan.FromMilliseconds(3).Ticks;

    public int TcpStreams => _tcpCount;
    public int UdpStreams => _udp.Count;

    public StreamInfo Process(CapturedPacket packet, int frameNumber) => Process(QuickHeader.Read(packet.Link, packet.Data), packet, frameNumber);

    public StreamInfo Process(in QuickHeader h, CapturedPacket packet, int frameNumber)
    {
        var info = StreamInfo.None;
        if (!h.IsIp) return info;
        bool fromLow = Compare(h.Source, h.SourcePort, h.Destination, h.DestinationPort) <= 0;
        var key = fromLow
            ? new Key(h.Source, h.SourcePort, h.Destination, h.DestinationPort)
            : new Key(h.Destination, h.DestinationPort, h.Source, h.SourcePort);

        if (h.IsUdp)
        {
            if (!_udp.TryGetValue(key, out int index)) _udp[key] = index = _udp.Count;
            info.UdpStream = index;
            return info;
        }
        if (!h.IsTcp) return info;

        bool syn = (h.TcpFlags & QuickHeader.Syn) != 0, fin = (h.TcpFlags & QuickHeader.Fin) != 0;
        bool rst = (h.TcpFlags & QuickHeader.Rst) != 0, ack = (h.TcpFlags & QuickHeader.AckFlag) != 0;

        // A SYN with a new initial sequence number on a known address pair is a new connection (the port was reused);
        // the same SYN again is only a retransmission.
        _tcp.TryGetValue(key, out var conversation);
        var mine = conversation is null ? null : fromLow ? conversation.A : conversation.B;
        if (conversation is null || (syn && !ack && mine!.SynSeen && mine.Base != h.Seq))
        {
            conversation = new TcpConversation { Index = _tcpCount++ };
            _tcp[key] = conversation;
        }
        var d = fromLow ? conversation.A : conversation.B;
        var o = fromLow ? conversation.B : conversation.A;
        info.TcpStream = conversation.Index;

        if (syn)
        {
            d.Base = h.Seq;
            d.BaseSet = true;
            d.SynSeen = true;
            d.WindowShift = h.WindowShift;
        }
        else if (!d.BaseSet)
        {
            d.Base = h.Seq - 1; // captured mid-way: the first byte seen is numbered 1, as in Wireshark
            d.BaseSet = true;
        }
        if (ack && !o.BaseSet)
        {
            o.Base = h.Ack - 1;
            o.BaseSet = true;
        }

        uint segment = (uint)h.PayloadLength + (syn ? 1u : 0) + (fin ? 1u : 0);
        info.HasRelative = true;
        info.RelativeSeq = h.Seq - d.Base;
        info.NextSeq = h.Seq + segment - d.Base;
        if (ack)
        {
            info.HasRelativeAck = true;
            info.RelativeAck = h.Ack - o.Base;
        }

        // The window is scaled once both SYNs carried the scale option; SYNs themselves never are.
        if (d.SynSeen && o.SynSeen)
            info.WindowShift = syn || d.WindowShift < 0 || o.WindowShift < 0 ? 0 : d.WindowShift;

        var analysis = TcpAnalysis.None;
        if (!syn && !fin && !rst && h.PayloadLength <= 1 && d.NextSet && h.Seq == d.Next - 1)
        {
            analysis |= TcpAnalysis.KeepAlive;
        }
        else if (segment > 0)
        {
            if (d.NextSet && SeqLess(h.Seq, d.Next))
            {
                bool fast = o.DupAcks >= 2 && o.LastAck == h.Seq;
                bool recent = packet.TimestampUtc.Ticks - d.LastTicks < OutOfOrderTicks;
                if (fast) analysis |= TcpAnalysis.FastRetransmission | TcpAnalysis.Retransmission;
                else if (recent && SeqLess(d.Next, h.Seq + segment)) analysis |= TcpAnalysis.OutOfOrder;
                else analysis |= TcpAnalysis.Retransmission;
            }
            else if (d.NextSet && SeqLess(d.Next, h.Seq) && !syn)
            {
                analysis |= TcpAnalysis.LostSegment; // "previous segment not captured"
            }
            if (!d.NextSet || SeqLess(d.Next, h.Seq + segment))
            {
                d.Next = h.Seq + segment;
                d.NextSet = true;
            }
        }

        if (ack && !syn)
        {
            bool pureAck = h.PayloadLength == 0 && !fin && !rst;
            if (pureAck && d.AckSeen && h.Ack == d.LastAck && h.Window == d.LastWindow && h.Window != 0
                && (analysis & TcpAnalysis.KeepAlive) == 0)
            {
                if (o.LastWasKeepAlive)
                {
                    analysis |= TcpAnalysis.KeepAliveAck;
                }
                else
                {
                    d.DupAcks++;
                    analysis |= TcpAnalysis.DuplicateAck;
                    info.DupAckFrame = d.LastAckFrame;
                    info.DupAckCount = d.DupAcks;
                }
            }
            else if (!d.AckSeen || h.Ack != d.LastAck)
            {
                d.DupAcks = 0;
                d.LastAckFrame = frameNumber;
            }
            if (o.NextSet && SeqLess(o.Next, h.Ack)) analysis |= TcpAnalysis.AckedUnseen;
            d.LastAck = h.Ack;
            d.LastWindow = h.Window;
            d.AckSeen = true;
        }
        if (h.Window == 0 && !rst && !syn && !fin) analysis |= TcpAnalysis.ZeroWindow;
        d.LastWasKeepAlive = (analysis & TcpAnalysis.KeepAlive) != 0;
        d.LastTicks = packet.TimestampUtc.Ticks;
        info.Analysis = analysis;

        if (h.PayloadLength > 0) Notice(conversation, packet.Data.AsSpan(h.PayloadOffset, h.PayloadLength), h);
        info.TlsVersion = conversation.TlsVersion;
        info.MqttLevel = conversation.MqttLevel;
        return info;
    }

    /// <summary>Remembers what later packets of the conversation need: the TLS version a ServerHello chose, the MQTT level of CONNECT.</summary>
    private static void Notice(TcpConversation c, ReadOnlySpan<byte> payload, in QuickHeader h)
    {
        if (c.TlsVersion == 0 && payload.Length > 11 && payload[0] == 22 && payload[1] == 3 && payload[5] == 2)
            c.TlsVersion = TlsHints.ChosenVersion(payload);
        if (c.MqttLevel == 0 && payload.Length > 9 && payload[0] == 0x10 && (h.SourcePort == 1883 || h.DestinationPort == 1883 || MqttHints.LooksLikeConnect(payload)))
            c.MqttLevel = MqttHints.ConnectLevel(payload);
    }

    private static int Compare(AddressKey a, int portA, AddressKey b, int portB)
    {
        int byAddress = a.CompareTo(b);
        return byAddress != 0 ? byAddress : portA.CompareTo(portB);
    }

    /// <summary>a before b in 32-bit sequence space (wrapping).</summary>
    internal static bool SeqLess(uint a, uint b) => (int)(a - b) < 0;
}
