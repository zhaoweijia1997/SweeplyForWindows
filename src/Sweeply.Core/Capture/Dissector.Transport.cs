using System.Globalization;
using System.Text;

namespace Sweeply.Core.Capture;

public static partial class Dissector
{
    private static void Transport(Context c, int protocol, int at, int end, ProtoNode ipLayer)
    {
        switch (protocol)
        {
            case 6: Tcp(c, at, end); break;
            case 17: Udp(c, at, end); break;
            case 1: Icmp(c, at, end); break;
            case 58: Icmpv6(c, at, end); break;
            case 2: Igmp(c, at, end); break;
            case 4: Ipv4(c, at); break;
            case 41: Ipv6(c, at); break;
            case 59: break; // no next header
            default:
            {
                string name = IpProtocolName(protocol);
                string filter = protocol switch { 47 => "gre", 50 => "esp", 51 => "ah", 89 => "ospf", 103 => "pim", 112 => "vrrp", 132 => "sctp", _ => "data" };
                var layer = c.D.Layer(filter, name == "Unknown" ? $"IP protocol {protocol}" : name, at, Math.Max(0, end - at));
                if (end > at) layer.AddText($"Data ({end - at} bytes)", at, end - at);
                c.D.Protocol = name == "Unknown" ? c.D.Protocol : name;
                c.D.Info = name == "Unknown" ? $"IP protocol {protocol}" : name;
                break;
            }
        }
    }

    // ---- TCP ----

    private static void Tcp(Context c, int at, int end)
    {
        var data = c.Data;
        var layer = c.D.Layer("tcp", "Transmission Control Protocol", at, Math.Max(0, end - at));
        c.D.Protocol = "TCP";
        if (!Need(c, layer, at, 20, end, "TCP header")) return;
        int sourcePort = U16(data, at), destinationPort = U16(data, at + 2);
        uint seq = U32(data, at + 4), ack = U32(data, at + 8);
        int headerLength = (data[at + 12] >> 4) * 4;
        int flags = U16(data, at + 12) & 0x0FFF;
        int window = U16(data, at + 14), checksum = U16(data, at + 16), urgent = U16(data, at + 18);
        c.IsTcp = true;
        c.TcpFlags = (byte)flags;
        c.SourcePort = sourcePort;
        c.DestinationPort = destinationPort;
        if (headerLength < 20 || !Need(c, layer, at, headerLength, end, "TCP header length")) return;
        int payload = end - (at + headerLength);
        var s = c.Stream;
        bool hasAck = (flags & 0x10) != 0;
        long seqShown = s.HasRelative ? s.RelativeSeq : seq;
        long ackShown = s.HasRelativeAck ? s.RelativeAck : ack;

        layer = new ProtoNode($"Transmission Control Protocol, Src Port: {sourcePort}, Dst Port: {destinationPort}, Seq: {seqShown}{(hasAck ? $", Ack: {ackShown}" : "")}, Len: {payload}",
            "tcp", true, at, end - at) { IsLayer = true };
        c.D.Layers[^1] = layer;
        layer.AddField("tcp.srcport", "Source Port", sourcePort, at, 2);
        layer.AddField("tcp.dstport", "Destination Port", destinationPort, at + 2, 2);
        if (s.TcpStream >= 0) layer.Add(new ProtoNode($"[Stream index: {s.TcpStream}]", "tcp.stream", s.TcpStream));
        layer.Add(new ProtoNode($"[TCP Segment Len: {payload}]", "tcp.len", payload));
        if (s.HasRelative)
        {
            layer.AddField("tcp.seq", "Sequence Number", (long)s.RelativeSeq, at + 4, 4, $"{s.RelativeSeq}    (relative sequence number)");
            layer.AddField("tcp.seq_raw", "Sequence Number (raw)", (long)seq, at + 4, 4);
            layer.Add(new ProtoNode($"[Next Sequence Number: {s.NextSeq}    (relative sequence number)]", "tcp.nxtseq", (long)s.NextSeq));
        }
        else layer.AddField("tcp.seq", "Sequence Number", (long)seq, at + 4, 4);
        if (s.HasRelativeAck)
        {
            layer.AddField("tcp.ack", "Acknowledgment Number", (long)s.RelativeAck, at + 8, 4, $"{s.RelativeAck}    (relative ack number)");
            layer.AddField("tcp.ack_raw", "Acknowledgment number (raw)", (long)ack, at + 8, 4);
        }
        else layer.AddField("tcp.ack", "Acknowledgment Number", (long)ack, at + 8, 4, hasAck ? null : $"{ack} (not used: ACK not set)");
        layer.Add(new ProtoNode(Format.Bits(data[at + 12], 0xF0, 8, "Header Length", $"{headerLength} bytes ({headerLength / 4})"), "tcp.hdr_len", headerLength, at + 12, 1));

        string flagNames = TcpFlagNames(flags);
        var flagNode = layer.AddField("tcp.flags", "Flags", flags, at + 12, 2, $"0x{flags:x3} ({flagNames})");
        int raw = U16(data, at + 12);
        (int Mask, string Label, string Field)[] bits =
        {
            (0x0E00, "Reserved", "tcp.flags.res"), (0x0100, "Accurate ECN", "tcp.flags.ae"), (0x0080, "Congestion Window Reduced", "tcp.flags.cwr"),
            (0x0040, "ECN-Echo", "tcp.flags.ece"), (0x0020, "Urgent", "tcp.flags.urg"), (0x0010, "Acknowledgment", "tcp.flags.ack"),
            (0x0008, "Push", "tcp.flags.push"), (0x0004, "Reset", "tcp.flags.reset"), (0x0002, "Syn", "tcp.flags.syn"), (0x0001, "Fin", "tcp.flags.fin"),
        };
        foreach (var (mask, label, field) in bits)
            flagNode.Add(new ProtoNode(Format.Bits(raw, mask, 12, label, mask == 0x0E00 ? "Not set" : null), field, (raw & mask) != 0, at + 12, 2));
        flagNode.Add(new ProtoNode($"[TCP Flags: {FlagString(flags)}]", "tcp.flags.str", FlagString(flags)));

        long scaled = window;
        layer.AddField("tcp.window_size_value", "Window", window, at + 14, 2);
        if (s.WindowShift >= 0)
        {
            scaled = (long)window << s.WindowShift;
            layer.Add(new ProtoNode($"[Calculated window size: {scaled}]", "tcp.window_size", scaled));
            layer.Add(new ProtoNode($"[Window size scaling factor: {1 << s.WindowShift}]", "tcp.window_size_scalefactor", 1 << s.WindowShift));
        }
        else
        {
            layer.Add(new ProtoNode($"[Calculated window size: {window}]", "tcp.window_size", (long)window));
            layer.Add(new ProtoNode("[Window size scaling factor: -1 (unknown)]", "tcp.window_size_scalefactor", -1));
        }
        layer.AddField("tcp.checksum", "Checksum", checksum, at + 16, 2, $"{Format.Hex16(checksum)} [unverified]");
        layer.AddField("tcp.urgent_pointer", "Urgent Pointer", urgent, at + 18, 2);

        var optionInfo = new StringBuilder();
        if (headerLength > 20) TcpOptions(c, layer, at + 20, at + headerLength, optionInfo, (flags & 0x02) != 0);

        // What sequence and acknowledgement numbers say (from the packets before this one).
        var analysis = s.Analysis;
        var prefix = new StringBuilder();
        if (analysis != TcpAnalysis.None)
        {
            var node = layer.AddText("[SEQ/ACK analysis]");
            var flagsNode = node.Add(new ProtoNode("[TCP Analysis Flags]", "tcp.analysis.flags", true) { IsWarning = true });
            void Note(TcpAnalysis flag, string field, string text, string label)
            {
                if ((analysis & flag) == 0) return;
                flagsNode.AddFlag(field, text);
                if (label.Length > 0) prefix.Append('[').Append(label).Append("] ");
            }
            if ((analysis & TcpAnalysis.FastRetransmission) != 0)
            {
                flagsNode.AddFlag("tcp.analysis.fast_retransmission", "[This frame is a (suspected) fast retransmission]");
                flagsNode.AddFlag("tcp.analysis.retransmission", "[This frame is a (suspected) retransmission]");
                prefix.Append("[TCP Fast Retransmission] ");
            }
            else Note(TcpAnalysis.Retransmission, "tcp.analysis.retransmission", "[This frame is a (suspected) retransmission]", "TCP Retransmission");
            Note(TcpAnalysis.OutOfOrder, "tcp.analysis.out_of_order", "[This frame is a (suspected) out-of-order segment]", "TCP Out-Of-Order");
            Note(TcpAnalysis.LostSegment, "tcp.analysis.lost_segment", "[A segment before this frame wasn't captured]", "TCP Previous segment not captured");
            Note(TcpAnalysis.KeepAlive, "tcp.analysis.keep_alive", "[This is a TCP keep-alive segment]", "TCP Keep-Alive");
            Note(TcpAnalysis.KeepAliveAck, "tcp.analysis.keep_alive_ack", "[This is an ACK to a TCP keep-alive segment]", "TCP Keep-Alive ACK");
            Note(TcpAnalysis.ZeroWindow, "tcp.analysis.zero_window", "[TCP Zero Window: the receiver can't take more data right now]", "TCP ZeroWindow");
            Note(TcpAnalysis.AckedUnseen, "tcp.analysis.ack_lost_segment", "[This frame ACKs a segment we have not seen (lost)]", "TCP ACKed unseen segment");
            if ((analysis & TcpAnalysis.DuplicateAck) != 0)
            {
                flagsNode.AddFlag("tcp.analysis.duplicate_ack", "[This is a TCP duplicate ack]");
                node.Add(new ProtoNode($"[Duplicate ACK #: {s.DupAckCount}]", "tcp.analysis.duplicate_ack_num", s.DupAckCount));
                node.Add(new ProtoNode($"[Duplicate to the ACK in frame: {s.DupAckFrame}]", "tcp.analysis.duplicate_ack_frame", s.DupAckFrame));
                prefix.Append($"[TCP Dup ACK {s.DupAckFrame}#{s.DupAckCount}] ");
            }
        }

        string arrow = $"{sourcePort} → {destinationPort}";
        string tcpInfo = $"{prefix}{arrow} [{flagNames}] Seq={seqShown}{(hasAck ? $" Ack={ackShown}" : "")} Win={scaled} Len={payload}{optionInfo}";
        c.D.Info = tcpInfo;

        if (payload <= 0) return;
        int payloadAt = at + headerLength;
        bool retransmitted = (analysis & (TcpAnalysis.Retransmission | TcpAnalysis.KeepAlive)) != 0;
        if (!retransmitted && App(c, tcp: true, payloadAt, end))
        {
            if (prefix.Length > 0) c.D.Info = prefix + c.D.Info;
            return;
        }
        layer.AddField("tcp.payload", "TCP payload", Bytes(data, payloadAt, payload), payloadAt, payload, $"({payload} bytes)");
        if (!retransmitted) DataLayer(c, payloadAt, end);
    }

    private static string TcpFlagNames(int flags)
    {
        // Wireshark's order: from the lowest bit up.
        string[] all = { "FIN", "SYN", "RST", "PSH", "ACK", "URG", "ECE", "CWR", "AE" };
        var names = new List<string>();
        for (int bit = 0; bit < all.Length; bit++)
            if ((flags & (1 << bit)) != 0) names.Add(all[bit]);
        return names.Count == 0 ? "<None>" : string.Join(", ", names);
    }

    /// <summary>Wireshark's one-glance form: "··········S·" with a letter for each flag that is set.</summary>
    private static string FlagString(int flags)
    {
        const string letters = "RRRACEUAPRSF";
        var text = new StringBuilder(12);
        for (int i = 0; i < 12; i++)
        {
            int mask = 0x800 >> i;
            text.Append((flags & mask) != 0 ? letters[i] : '·');
        }
        return text.ToString();
    }

    private static void TcpOptions(Context c, ProtoNode layer, int at, int end, StringBuilder info, bool syn)
    {
        var data = c.Data;
        var names = new List<string>();
        var options = new ProtoNode($"Options: ({end - at} bytes)", null, null, at, end - at);
        for (int i = at; i < end;)
        {
            int kind = data[i];
            if (kind == 0) { options.AddField("tcp.option_kind", "TCP Option - End of Option List (EOL)", kind, i, 1, "0"); names.Add("End of Option List (EOL)"); break; }
            if (kind == 1) { options.Add(new ProtoNode("TCP Option - No-Operation (NOP)", "tcp.option_kind", kind, i, 1)); names.Add("No-Operation (NOP)"); i++; continue; }
            if (i + 1 >= end) { Malformed(c, options, "TCP option length"); break; }
            int length = data[i + 1];
            if (length < 2 || i + length > end) { Malformed(c, options, "TCP option length"); break; }
            switch (kind)
            {
                case 2 when length == 4:
                {
                    int mss = U16(data, i + 2);
                    var node = options.Add(new ProtoNode($"TCP Option - Maximum segment size: {mss} bytes", "tcp.option_kind", kind, i, length));
                    node.AddField("tcp.options.mss_val", "MSS Value", mss, i + 2, 2);
                    names.Add("Maximum segment size");
                    info.Append(" MSS=").Append(mss);
                    break;
                }
                case 3 when length == 3:
                {
                    int shift = Math.Min((int)data[i + 2], 14);
                    var node = options.Add(new ProtoNode($"TCP Option - Window scale: {shift} (multiply by {1 << shift})", "tcp.option_kind", kind, i, length));
                    node.AddField("tcp.options.wscale.shift", "Shift count", shift, i + 2, 1);
                    node.Add(new ProtoNode($"[Multiplier: {1 << shift}]", "tcp.options.wscale.multiplier", 1 << shift));
                    names.Add("Window scale");
                    info.Append(" WS=").Append(1 << shift);
                    break;
                }
                case 4 when length == 2:
                    options.Add(new ProtoNode("TCP Option - SACK permitted", "tcp.options.sack_perm", true, i, length));
                    names.Add("SACK permitted");
                    info.Append(" SACK_PERM");
                    break;
                case 5:
                {
                    var node = options.Add(new ProtoNode($"TCP Option - SACK", "tcp.option_kind", kind, i, length));
                    for (int b = i + 2; b + 8 <= i + length; b += 8)
                    {
                        uint left = U32(data, b), right = U32(data, b + 4);
                        node.AddField("tcp.options.sack_le", "left edge", (long)left, b, 4);
                        node.AddField("tcp.options.sack_re", "right edge", (long)right, b + 4, 4);
                        info.Append(" SLE=").Append(left).Append(" SRE=").Append(right);
                    }
                    names.Add("SACK");
                    break;
                }
                case 8 when length == 10:
                {
                    uint value = U32(data, i + 2), echo = U32(data, i + 6);
                    var node = options.Add(new ProtoNode($"TCP Option - Timestamps: TSval {value}, TSecr {echo}", "tcp.option_kind", kind, i, length));
                    node.AddField("tcp.options.timestamp.tsval", "Timestamp value", (long)value, i + 2, 4);
                    node.AddField("tcp.options.timestamp.tsecr", "Timestamp echo reply", (long)echo, i + 6, 4);
                    names.Add("Timestamps");
                    info.Append(" TSval=").Append(value).Append(" TSecr=").Append(echo);
                    break;
                }
                case 30:
                    options.Add(new ProtoNode("TCP Option - Multipath TCP", "tcp.option_kind", kind, i, length));
                    names.Add("Multipath TCP");
                    break;
                case 34:
                    options.Add(new ProtoNode("TCP Option - TCP Fast Open", "tcp.option_kind", kind, i, length));
                    names.Add("TCP Fast Open");
                    break;
                default:
                    options.Add(new ProtoNode($"TCP Option - Unknown (kind {kind}, {length} bytes)", "tcp.option_kind", kind, i, length));
                    names.Add($"Unknown ({kind})");
                    break;
            }
            i += length;
        }
        var titled = new ProtoNode($"Options: ({end - at} bytes), {string.Join(", ", names)}", null, null, at, end - at);
        if (options.Children is not null)
            foreach (var child in options.Children) titled.Add(child);
        layer.Add(titled);
    }

    // ---- UDP ----

    private static void Udp(Context c, int at, int end)
    {
        var data = c.Data;
        var layer = c.D.Layer("udp", "User Datagram Protocol", at, Math.Max(0, end - at));
        c.D.Protocol = "UDP";
        if (!Need(c, layer, at, 8, end, "UDP header")) return;
        int sourcePort = U16(data, at), destinationPort = U16(data, at + 2), length = U16(data, at + 4), checksum = U16(data, at + 6);
        c.SourcePort = sourcePort;
        c.DestinationPort = destinationPort;
        int payloadEnd = length >= 8 && at + length <= end ? at + length : end;
        int payload = payloadEnd - at - 8;
        layer = new ProtoNode($"User Datagram Protocol, Src Port: {sourcePort}, Dst Port: {destinationPort}", "udp", true, at, payloadEnd - at) { IsLayer = true };
        c.D.Layers[^1] = layer;
        layer.AddField("udp.srcport", "Source Port", sourcePort, at, 2);
        layer.AddField("udp.dstport", "Destination Port", destinationPort, at + 2, 2);
        layer.AddField("udp.length", "Length", length, at + 4, 2);
        layer.AddField("udp.checksum", "Checksum", checksum, at + 6, 2, checksum == 0 ? "0x0000 [zero-value ignored]" : $"{Format.Hex16(checksum)} [unverified]");
        if (c.Stream.UdpStream >= 0) layer.Add(new ProtoNode($"[Stream index: {c.Stream.UdpStream}]", "udp.stream", c.Stream.UdpStream));
        layer.AddField("udp.payload", "UDP payload", payload > 0 ? Bytes(data, at + 8, payload) : Array.Empty<byte>(), at + 8, Math.Max(0, payload), $"({payload} bytes)");
        c.D.Info = $"{sourcePort} → {destinationPort} Len={payload}";
        if (payload > 0 && !App(c, tcp: false, at + 8, payloadEnd)) DataLayer(c, at + 8, payloadEnd);
    }

    private static void DataLayer(Context c, int at, int end)
    {
        var layer = c.D.Layer("data", $"Data ({end - at} bytes)", at, end - at);
        layer.AddField("data.data", "Data", Bytes(c.Data, at, end - at), at, end - at, Format.Hex(c.Data.AsSpan(at, end - at), 48));
        layer.Add(new ProtoNode($"[Length: {end - at}]", "data.len", end - at));
    }

    // ---- which application protocol ----

    /// <summary>Hands the payload to the protocol its ports (or its first bytes) say; false when none fits.</summary>
    private static bool App(Context c, bool tcp, int at, int end)
    {
        var data = c.Data;
        int sp = c.SourcePort, dp = c.DestinationPort;
        bool Port(params int[] ports) => ports.Contains(sp) || ports.Contains(dp);
        var payload = data.AsSpan(at, end - at);
        if (tcp)
        {
            if (Port(1883) && Mqtt(c, at, end)) return true;
            if (Port(502) && Modbus(c, at, end)) return true;
            if (Port(53) && DnsOverTcp(c, at, end)) return true;
            if (LooksLikeTls(payload) && Tls(c, at, end, continuation: false)) return true;
            if (LooksLikeHttp(payload) || Port(80, 8080, 8000, 8008, 3128, 8888)) return Http(c, at, end, "http", "HTTP", "Hypertext Transfer Protocol");
            if (payload.StartsWith("SSH-"u8) || Port(22)) return Ssh(c, at, end);
            if (Port(443, 8443, 853, 993, 995, 465, 636, 5061, 8883)) return Tls(c, at, end, continuation: true);
            if (Port(21)) return TextLines(c, at, end, 21, "ftp", "FTP", "File Transfer Protocol (FTP)");
            if (Port(25, 587)) return TextLines(c, at, end, sp is 25 or 587 ? sp : dp, "smtp", "SMTP", "Simple Mail Transfer Protocol");
            if (Port(110)) return TextLines(c, at, end, 110, "pop", "POP", "Post Office Protocol");
            if (Port(143)) return TextLines(c, at, end, 143, "imap", "IMAP", "Internet Message Access Protocol");
            if (Port(1883) || MqttHints.LooksLikeConnect(payload)) return Mqtt(c, at, end);
            return false;
        }
        if (Port(53)) return Dns(c, at, end, "dns", "DNS", "Domain Name System");
        if (Port(5353)) return Dns(c, at, end, "mdns", "MDNS", "Multicast Domain Name System");
        if (Port(5355)) return Dns(c, at, end, "llmnr", "LLMNR", "Link-local Multicast Name Resolution");
        if (Port(137)) return Dns(c, at, end, "nbns", "NBNS", "NetBIOS Name Service");
        if (Port(67, 68)) return Dhcp(c, at, end);
        if (Port(123)) return Ntp(c, at, end);
        if (Port(1900)) return Http(c, at, end, "ssdp", "SSDP", "Simple Service Discovery Protocol");
        if (Port(443) && LooksLikeQuic(payload)) return Quic(c, at, end);
        return false;
    }

    private static bool LooksLikeTls(ReadOnlySpan<byte> p) =>
        p.Length >= 5 && p[0] is >= 20 and <= 24 && p[1] == 3 && p[2] <= 4 && (p[3] << 8 | p[4]) <= 18432 + 2048;

    private static readonly string[] HttpMethods = { "GET ", "POST ", "PUT ", "DELETE ", "HEAD ", "OPTIONS ", "PATCH ", "CONNECT ", "TRACE ", "HTTP/1.", "M-SEARCH ", "NOTIFY " };

    private static bool LooksLikeHttp(ReadOnlySpan<byte> p)
    {
        foreach (string method in HttpMethods)
        {
            if (p.Length < method.Length) continue;
            bool same = true;
            for (int i = 0; i < method.Length && same; i++) same = p[i] == method[i];
            if (same) return true;
        }
        return false;
    }

    private static bool LooksLikeQuic(ReadOnlySpan<byte> p) =>
        p.Length >= 7 && (p[0] & 0x40) != 0 && ((p[0] & 0x80) == 0 || p[1] != 0 || p[2] != 0 || p[3] != 0 || p[4] is 1 or 0);
}
