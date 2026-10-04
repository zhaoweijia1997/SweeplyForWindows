using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace Sweeply.Core.Capture;

/// <summary>
/// Decodes a captured packet into Wireshark-style layers and the columns of the packet list. Field names follow
/// Wireshark's display filter names (ip.src, tcp.port, dns.qry.name…), so filters people know work here, and
/// protocol texts stay in English as in Wireshark. Never throws on bad input: whatever doesn't fit is marked
/// "[Malformed Packet]".
/// </summary>
public static partial class Dissector
{
    private sealed class Context
    {
        public Context(CapturedPacket packet, Dissection dissection, StreamInfo stream)
        {
            Packet = packet;
            Data = packet.Data;
            D = dissection;
            Stream = stream;
        }

        public readonly CapturedPacket Packet;
        public readonly byte[] Data;
        public readonly Dissection D;
        public readonly StreamInfo Stream;
        public ProtoNode? Frame, Ethernet;
        public string? LinkSource, LinkDestination, NetworkSource, NetworkDestination;
        public bool Broadcast, IcmpError, Malformed;
        public int SourcePort, DestinationPort;
        public byte TcpFlags;
        public bool IsTcp;
    }

    public static Dissection Dissect(CapturedPacket packet, int number, in StreamInfo stream, DateTime firstTimestampUtc, string interfaceName = "")
    {
        var d = new Dissection();
        var c = new Context(packet, d, stream);
        Frame(c, number, firstTimestampUtc, interfaceName);
        try
        {
            Link(c);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            // A bounds check missed somewhere: show what was decoded, never fail.
            Malformed(c, d.Layers[^1], "Exception occurred while decoding");
        }
        Finish(c);
        return d;
    }

    // ---- frame ----

    private static void Frame(Context c, int number, DateTime first, string interfaceName)
    {
        var p = c.Packet;
        int wire = p.Length, captured = p.Data.Length;
        string on = interfaceName.Length > 0 ? $" on interface {interfaceName}" : "";
        var frame = c.D.Layer("frame", $"Frame {number}: {wire} bytes on wire ({wire * 8} bits), {captured} bytes captured ({captured * 8} bits){on}", 0, captured);
        c.Frame = frame;
        var local = p.TimestampUtc.ToLocalTime();
        frame.AddField("frame.time", "Arrival Time", local, -1, 0, local.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        double epoch = (p.TimestampUtc - DateTime.UnixEpoch).TotalSeconds;
        frame.AddField("frame.time_epoch", "Epoch Arrival Time", epoch, -1, 0, epoch.ToString("0.0000000", CultureInfo.InvariantCulture));
        double relative = (p.TimestampUtc - first).TotalSeconds;
        frame.Add(new ProtoNode($"[Time since reference or first frame: {Format.Seconds(relative)} seconds]", "frame.time_relative", relative));
        frame.AddField("frame.number", "Frame Number", number, -1, 0);
        frame.AddField("frame.len", "Frame Length", wire, -1, 0, $"{wire} bytes ({wire * 8} bits)");
        frame.AddField("frame.cap_len", "Capture Length", captured, -1, 0, $"{captured} bytes ({captured * 8} bits)");
        if (p.Direction != PacketDirection.Unknown)
            frame.AddField("frame.direction", "Direction", p.Direction == PacketDirection.Inbound ? "in" : "out", -1, 0,
                p.Direction == PacketDirection.Inbound ? "Inbound" : "Outbound");
        if (p.ProcessName is { Length: > 0 } program)
        {
            frame.AddField("frame.process", "Program", program, -1, 0, p.ProcessId > 0 ? $"{program} (PID {p.ProcessId})" : program);
            if (p.ProcessId > 0) frame.Add(new ProtoNode($"[Process ID: {p.ProcessId}]", "frame.pid", (long)p.ProcessId));
        }
        if (p.Comment is { Length: > 0 } comment) frame.AddField("frame.comment", "Comment", comment, -1, 0);
    }

    private static void Finish(Context c)
    {
        var d = c.D;
        c.Frame!.Add(new ProtoNode($"[Protocols in frame: {string.Join(':', d.Protocols.Skip(1))}]", "frame.protocols",
            string.Join(':', d.Protocols.Skip(1))));
        d.Source = c.NetworkSource ?? c.LinkSource ?? "";
        d.Destination = c.NetworkDestination ?? c.LinkDestination ?? "";
        if (d.Protocol.Length == 0) d.Protocol = d.Protocols.Count > 1 ? d.Protocols[^1].ToUpperInvariant() : "Data";
        if (c.Malformed && !d.Info.Contains("[Malformed Packet]", StringComparison.Ordinal))
            d.Info = (d.Info + " [Malformed Packet]").Trim();

        bool tcp = c.IsTcp;
        d.Color =
            tcp && c.Stream.Analysis != TcpAnalysis.None && (c.Stream.Analysis & ~(TcpAnalysis.KeepAliveAck)) != 0 ? PacketColor.BadTcp :
            c.IcmpError ? PacketColor.IcmpError :
            d.Protocols.Contains("arp") ? PacketColor.Arp :
            d.Protocols.Contains("icmp") || d.Protocols.Contains("icmpv6") ? PacketColor.Icmp :
            tcp && (c.TcpFlags & QuickHeader.Rst) != 0 ? PacketColor.TcpReset :
            d.Protocols.Contains("http") || d.Protocols.Contains("ssdp") ? PacketColor.Http :
            tcp && (c.TcpFlags & (QuickHeader.Syn | QuickHeader.Fin)) != 0 ? PacketColor.TcpSynFin :
            tcp ? PacketColor.Tcp :
            d.Protocols.Contains("udp") ? PacketColor.Udp :
            c.Broadcast ? PacketColor.Broadcast :
            PacketColor.Default;
    }

    /// <summary>Marks the packet as cut short or broken where a field was expected.</summary>
    private static void Malformed(Context c, ProtoNode under, string what)
    {
        c.Malformed = true;
        under.Add(new ProtoNode($"[Malformed Packet: {what}]", "_ws.malformed", true) { IsWarning = true });
    }

    /// <summary>True when <paramref name="count"/> bytes are there at <paramref name="offset"/> (before <paramref name="end"/>); marks the packet otherwise.</summary>
    private static bool Need(Context c, ProtoNode under, int offset, int count, int end, string what)
    {
        if (offset >= 0 && count >= 0 && offset + count <= end && end <= c.Data.Length) return true;
        Malformed(c, under, what);
        return false;
    }

    private static ushort U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at, 2));
    private static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at, 4));
    private static IPAddress Ip4(byte[] d, int at) => new(d.AsSpan(at, 4));
    private static IPAddress Ip6(byte[] d, int at) => new(d.AsSpan(at, 16));
    private static byte[] Bytes(byte[] d, int at, int length) => d.AsSpan(at, length).ToArray();

    // ---- link layer ----

    private static void Link(Context c)
    {
        var data = c.Data;
        switch (c.Packet.Link)
        {
            case LinkType.Ethernet:
                Ethernet(c, 0);
                break;
            case LinkType.Raw:
            case LinkType.Ipv4:
            case LinkType.Ipv6:
                if (data.Length == 0) { Malformed(c, c.Frame!, "empty packet"); break; }
                IpByVersion(c, 0);
                break;
            case LinkType.Null:
            case LinkType.Loop:
            {
                var layer = c.D.Layer("null", "Null/Loopback", 0, Math.Min(4, data.Length));
                if (!Need(c, layer, 0, 4, data.Length, "loopback header")) break;
                uint family = c.Packet.Link == LinkType.Loop ? U32(data, 0) : BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (family > 0xFFFF) family = BinaryPrimitives.ReverseEndianness(family); // written in the other byte order
                string name = family == 2 ? "IP" : family is 23 or 24 or 28 or 30 ? "IPv6" : $"Unknown ({family})";
                layer.AddField("null.family", "Family", (long)family, 0, 4, $"{name} ({family})");
                if (data.Length > 4) IpByVersion(c, 4);
                break;
            }
            case LinkType.LinuxSll:
            {
                var layer = c.D.Layer("sll", "Linux cooked capture v1", 0, Math.Min(16, data.Length));
                if (!Need(c, layer, 0, 16, data.Length, "cooked header")) break;
                int packetType = U16(data, 0), addressLength = U16(data, 4);
                layer.AddField("sll.pkttype", "Packet type", packetType, 0, 2, $"{SllPacketType(packetType)} ({packetType})");
                layer.AddField("sll.hatype", "Link-layer address type", (int)U16(data, 2), 2, 2);
                if (addressLength == 6) layer.AddField("sll.src.eth", "Source", Bytes(data, 6, 6), 6, 6);
                int type = U16(data, 14);
                layer.AddField("sll.etype", "Protocol", type, 14, 2, $"{EtherTypeName(type)} ({Format.Hex16(type)})");
                if (addressLength == 6) c.LinkSource = Format.Mac(data.AsSpan(6, 6));
                EtherPayload(c, type, 16, layer);
                break;
            }
            case LinkType.LinuxSll2:
            {
                var layer = c.D.Layer("sll", "Linux cooked capture v2", 0, Math.Min(20, data.Length));
                if (!Need(c, layer, 0, 20, data.Length, "cooked header")) break;
                int type = U16(data, 0);
                layer.AddField("sll.etype", "Protocol", type, 0, 2, $"{EtherTypeName(type)} ({Format.Hex16(type)})");
                layer.AddField("sll.ifindex", "Interface index", (long)U32(data, 4), 4, 4);
                layer.AddField("sll.pkttype", "Packet type", (int)data[10], 10, 1, $"{SllPacketType(data[10])} ({data[10]})");
                if (data[11] == 6) { layer.AddField("sll.src.eth", "Source", Bytes(data, 12, 6), 12, 6); c.LinkSource = Format.Mac(data.AsSpan(12, 6)); }
                EtherPayload(c, type, 20, layer);
                break;
            }
            case LinkType.Ieee80211:
                Wlan(c);
                break;
            default:
                c.D.Layer("data", $"Link-layer type {(int)c.Packet.Link} (not decoded here)", 0, data.Length);
                c.D.Info = $"Link-layer type {(int)c.Packet.Link}";
                break;
        }
    }

    /// <summary>
    /// An 802.11 frame as a Wi-Fi card hands it over (Windows' packet capture does, already decrypted): the MAC header,
    /// whose addresses mean different things by the "to/from distribution system" bits, then for data frames
    /// LLC/SNAP and what it carries. Management and control frames only come in monitor mode; they are named.
    /// </summary>
    private static void Wlan(Context c)
    {
        var data = c.Data;
        var layer = c.D.Layer("wlan", "IEEE 802.11", 0, Math.Min(24, data.Length));
        if (!Need(c, layer, 0, 2, data.Length, "802.11 frame control")) return;
        int type = (data[0] >> 2) & 3, subtype = data[0] >> 4, flags = data[1];
        int typeSubtype = type << 4 | subtype;
        string kind = WlanKind(type, subtype), flagText = WlanFlags(flags);
        c.D.Protocol = "802.11";
        // As Wireshark: the sequence and fragment numbers (control frames have none) and the flags; a data frame's
        // contents write over it.
        c.D.Info = type != 1 && data.Length >= 24
            ? $"{kind}, SN={BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(22)) >> 4}, FN={data[22] & 0xF}, Flags={flagText}"
            : $"{kind}, Flags={flagText}";
        if (type != 2)
        {
            c.D.Layers[^1] = layer = new ProtoNode($"IEEE 802.11 {kind}, Flags: {flagText}", "wlan", true, 0, data.Length) { IsLayer = true };
            layer.AddField("wlan.fc.type_subtype", "Type/Subtype", typeSubtype, 0, 1, $"{kind} ({Format.Hex16(typeSubtype)})");
            return;
        }
        bool toDs = (flags & 1) != 0, fromDs = (flags & 2) != 0, qos = (subtype & 8) != 0, order = (flags & 0x80) != 0;
        int header = 24 + (toDs && fromDs ? 6 : 0) + (qos ? 2 : 0) + (qos && order ? 4 : 0);
        if (!Need(c, layer, 0, header, data.Length, "802.11 header")) return;

        // Which address is which: 1 receiver, 2 transmitter; destination, source and the access point by the DS bits.
        int daAt, saAt, bssidAt;
        (daAt, saAt, bssidAt) = (toDs, fromDs) switch
        {
            (false, false) => (4, 10, 16),
            (true, false) => (16, 10, 4),
            (false, true) => (4, 16, 10),
            _ => (16, 24, -1), // between access points: no BSS Id
        };
        var destination = Bytes(data, daAt, 6);
        bool broadcast = destination.All(b => b == 0xFF);
        c.Broadcast = (destination[0] & 1) != 0;
        string destinationText = broadcast ? "Broadcast" : Format.Mac(destination);
        c.LinkSource = Format.Mac(data.AsSpan(saAt, 6));
        c.LinkDestination = destinationText;

        c.D.Layers[^1] = layer = new ProtoNode($"IEEE 802.11 {kind}, Flags: {flagText}", "wlan", true, 0, header) { IsLayer = true };
        layer.AddField("wlan.fc.type_subtype", "Type/Subtype", typeSubtype, 0, 1, $"{kind} ({Format.Hex16(typeSubtype)})");
        var flagNode = layer.AddField("wlan.flags", "Flags", flags, 1, 1, Format.Hex8(flags));
        string ds = (toDs, fromDs) switch
        {
            (false, false) => "Not leaving DS or network is operating in AD-HOC mode (To DS: 0 From DS: 0)",
            (true, false) => "Frame from STA to DS via an AP (To DS: 1 From DS: 0)",
            (false, true) => "Frame from DS to a STA via AP(To DS: 0 From DS: 1)",
            _ => "WDS (AP to AP) or Mesh (MP to MP) Frame (To DS: 1 From DS: 1)",
        };
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x03, 8, "DS status", ds), "wlan.fc.ds", flags & 3, 1, 1));
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x08, 8, "Retry", (flags & 0x08) != 0 ? "Frame is being retransmitted" : "Frame is not being retransmitted"), "wlan.fc.retry", (flags & 0x08) != 0, 1, 1));
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x40, 8, "Protected flag", (flags & 0x40) != 0 ? "Data is protected" : "Data is not protected"), "wlan.fc.protected", (flags & 0x40) != 0, 1, 1));
        int duration = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        layer.AddField("wlan.duration", "Duration", duration, 2, 2, $"{duration} microseconds");
        layer.AddField("wlan.ra", "Receiver address", Bytes(data, 4, 6), 4, 6);
        layer.AddField("wlan.ta", "Transmitter address", Bytes(data, 10, 6), 10, 6);
        layer.AddField("wlan.da", "Destination address", destination, daAt, 6, broadcast ? $"Broadcast ({Format.Mac(destination)})" : destinationText);
        layer.AddField("wlan.sa", "Source address", Bytes(data, saAt, 6), saAt, 6);
        if (bssidAt >= 0) layer.AddField("wlan.bssid", "BSS Id", Bytes(data, bssidAt, 6), bssidAt, 6);
        int sequence = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(22));
        layer.AddField("wlan.frag", "Fragment number", sequence & 0xF, 22, 2);
        layer.AddField("wlan.seq", "Sequence number", sequence >> 4, 22, 2);
        if (qos)
        {
            int qosAt = 24 + (toDs && fromDs ? 6 : 0);
            layer.AddField("wlan.qos.tid", "TID", data[qosAt] & 0xF, qosAt, 2);
        }
        if ((subtype & 4) != 0) return; // Null function: no body (power saving signals only)

        int at = header;
        if (data.Length >= at + 8 && data[at] == 0xAA && data[at + 1] == 0xAA && data[at + 2] == 0x03)
        {
            int etherType = U16(data, at + 6);
            int oui = data[at + 3] << 16 | data[at + 4] << 8 | data[at + 5];
            var llc = c.D.Layer("llc", "Logical-Link Control", at, 8);
            llc.AddField("llc.dsap", "DSAP", 0xAA, at, 1, "SNAP (0xaa)");
            llc.AddField("llc.ssap", "SSAP", 0xAA, at + 1, 1, "SNAP (0xaa)");
            llc.AddField("llc.control", "Control field", 3, at + 2, 1, "U, func=UI (0x03)");
            llc.AddField("llc.oui", "Organization Code", oui, at + 3, 3, oui == 0 ? "Encapsulated Ethernet (0x000000)" : $"0x{oui:x6}");
            llc.AddField("llc.type", "Type", etherType, at + 6, 2, $"{EtherTypeName(etherType)} ({Format.Hex16(etherType)})");
            EtherPayload(c, etherType, at + 8, llc);
        }
        else if (data.Length > at) Llc(c, at, data.Length);
    }

    private static string WlanKind(int type, int subtype) => (type, subtype) switch
    {
        (0, 0) => "Association Request",
        (0, 1) => "Association Response",
        (0, 4) => "Probe Request",
        (0, 5) => "Probe Response",
        (0, 8) => "Beacon frame",
        (0, 10) => "Disassociate",
        (0, 11) => "Authentication",
        (0, 12) => "Deauthentication",
        (0, 13) => "Action",
        (1, 11) => "Request-to-send",
        (1, 12) => "Clear-to-send",
        (1, 13) => "Acknowledgement",
        (1, 9) => "802.11 Block Ack",
        (2, 0) => "Data",
        (2, 4) => "Null function (No data)",
        (2, 8) => "QoS Data",
        (2, 12) => "QoS Null function (No data)",
        (0, _) => "Management frame",
        (1, _) => "Control frame",
        (2, _) => "Data",
        _ => "Extension frame",
    };

    /// <summary>"...PR..T" as Wireshark writes the flags: order, protected, more data, power management, retry, more fragments, from DS, to DS.</summary>
    private static string WlanFlags(int flags)
    {
        const string letters = "opmPRMFT";
        var text = new char[8];
        for (int i = 0; i < 8; i++) text[i] = (flags & (0x80 >> i)) != 0 ? letters[i] : '.';
        return new string(text);
    }

    private static string SllPacketType(int type) => type switch
    {
        0 => "Unicast to us",
        1 => "Broadcast",
        2 => "Multicast",
        3 => "Unicast to another host",
        4 => "Sent by us",
        _ => "Unknown",
    };

    private static void Ethernet(Context c, int at)
    {
        var data = c.Data;
        var layer = c.D.Layer("eth", "Ethernet II", at, Math.Min(14, data.Length - at));
        c.Ethernet = layer;
        if (!Need(c, layer, at, 14, data.Length, "Ethernet header")) return;
        var destination = Bytes(data, at, 6);
        var source = Bytes(data, at + 6, 6);
        int type = U16(data, at + 12);
        bool broadcast = destination.All(b => b == 0xFF);
        bool multicast = (destination[0] & 1) != 0;
        c.Broadcast = broadcast || multicast;
        string destinationText = broadcast ? "Broadcast" : Format.Mac(destination);
        c.LinkSource = Format.Mac(source);
        c.LinkDestination = destinationText;

        if (type < 0x0600)
        {
            // IEEE 802.3: a length instead of a type, then LLC (spanning tree, NetBIOS over LLC…).
            var title = new ProtoNode($"IEEE 802.3 Ethernet, Src: {Format.Mac(source)}, Dst: {destinationText}");
            c.D.Layers[^1] = new ProtoNode(title.Text, "eth", true, at, 14) { IsLayer = true };
            layer = c.D.Layers[^1];
            layer.AddField("eth.dst", "Destination", destination, at, 6, destinationText);
            layer.AddField("eth.src", "Source", source, at + 6, 6);
            layer.AddField("eth.len", "Length", type, at + 12, 2);
            Llc(c, at + 14, Math.Min(data.Length, at + 14 + type));
            return;
        }

        c.D.Layers[^1] = layer = new ProtoNode($"Ethernet II, Src: {Format.Mac(source)}, Dst: {destinationText}", "eth", true, at, 14) { IsLayer = true };
        c.Ethernet = layer;
        layer.AddField("eth.dst", "Destination", destination, at, 6, destinationText == "Broadcast" ? $"Broadcast ({Format.Mac(destination)})" : destinationText);
        layer.AddField("eth.src", "Source", source, at + 6, 6);
        layer.AddField("eth.type", "Type", type, at + 12, 2, $"{EtherTypeName(type)} ({Format.Hex16(type)})");
        c.D.Protocol = "Ethernet";
        EtherPayload(c, type, at + 14, layer);
    }

    private static void EtherPayload(Context c, int type, int at, ProtoNode link)
    {
        switch (type)
        {
            case 0x0800: Ipv4(c, at); break;
            case 0x86DD: Ipv6(c, at); break;
            case 0x0806: Arp(c, at); break;
            case 0x8100:
            case 0x88A8:
                Vlan(c, at, type);
                break;
            default:
                string name = EtherTypeName(type);
                var layer = c.D.Layer(EtherTypeFilter(type), name, at, Math.Max(0, c.Data.Length - at));
                c.D.Protocol = EtherTypeShort(type);
                c.D.Info = name;
                if (c.Data.Length > at) layer.AddField("data.data", "Data", Bytes(c.Data, at, c.Data.Length - at), at, c.Data.Length - at,
                    $"{Format.Hex(c.Data.AsSpan(at))} ({c.Data.Length - at} bytes)");
                break;
        }
    }

    private static void Vlan(Context c, int at, int tpid)
    {
        var data = c.Data;
        var layer = c.D.Layer("vlan", "802.1Q Virtual LAN", at, Math.Min(4, Math.Max(0, data.Length - at)));
        if (!Need(c, layer, at, 4, data.Length, "VLAN tag")) return;
        int tci = U16(data, at), type = U16(data, at + 2);
        int priority = tci >> 13, dei = (tci >> 12) & 1, id = tci & 0x0FFF;
        c.D.Layers[^1] = layer = new ProtoNode($"802.1Q Virtual LAN, PRI: {priority}, DEI: {dei}, ID: {id}", "vlan", true, at, 4) { IsLayer = true };
        layer.Add(new ProtoNode(Format.Bits(tci, 0xE000, 16, "Priority", priority.ToString(CultureInfo.InvariantCulture)), "vlan.priority", priority, at, 2));
        layer.Add(new ProtoNode(Format.Bits(tci, 0x1000, 16, "DEI", dei == 1 ? "Eligible" : "Ineligible"), "vlan.dei", dei, at, 2));
        layer.Add(new ProtoNode(Format.Bits(tci, 0x0FFF, 16, "ID", id.ToString(CultureInfo.InvariantCulture)), "vlan.id", id, at, 2));
        layer.AddField("vlan.etype", "Type", type, at + 2, 2, $"{EtherTypeName(type)} ({Format.Hex16(type)})");
        EtherPayload(c, type, at + 4, layer);
    }

    private static void Llc(Context c, int at, int end)
    {
        var data = c.Data;
        var layer = c.D.Layer("llc", "Logical-Link Control", at, Math.Max(0, Math.Min(3, end - at)));
        if (!Need(c, layer, at, 3, end, "LLC header")) return;
        int dsap = data[at], ssap = data[at + 1], control = data[at + 2];
        layer.AddField("llc.dsap", "DSAP", dsap, at, 1, $"{SapName(dsap)} ({Format.Hex8(dsap)})");
        layer.AddField("llc.ssap", "SSAP", ssap, at + 1, 1, $"{SapName(ssap)} ({Format.Hex8(ssap)})");
        layer.AddField("llc.control", "Control field", control, at + 2, 1, Format.Hex8(control));
        c.D.Protocol = "LLC";
        c.D.Info = $"S, func={control:x2}; DSAP {SapName(dsap)}, SSAP {SapName(ssap)}";
        if (dsap == 0x42)
        {
            c.D.Layer("stp", "Spanning Tree Protocol", at + 3, Math.Max(0, end - at - 3));
            c.D.Protocol = "STP";
            c.D.Info = "Spanning Tree";
        }
    }

    private static string SapName(int sap) => sap switch
    {
        0x42 => "Spanning Tree BPDU",
        0xAA => "SNAP",
        0xF0 => "NetBIOS",
        0xE0 => "NetWare",
        0xFE => "ISO Network Layer",
        _ => "Unknown",
    };

    internal static string EtherTypeName(int type) => type switch
    {
        0x0800 => "IPv4",
        0x86DD => "IPv6",
        0x0806 => "ARP",
        0x8100 => "802.1Q Virtual LAN",
        0x88A8 => "802.1ad Provider Bridge (Q-in-Q)",
        0x88CC => "Link Layer Discovery Protocol",
        0x888E => "802.1X Authentication",
        0x8863 => "PPPoE Discovery",
        0x8864 => "PPPoE Session",
        0x0842 => "Wake on LAN",
        0x88E1 => "HomePlug AV",
        0x893A => "IEEE 1905.1",
        0x88F7 => "Precision Time Protocol",
        0x8808 => "MAC Control",
        0x8899 => "Realtek Layer 2 Protocols",
        _ => "Unknown",
    };

    private static string EtherTypeShort(int type) => type switch
    {
        0x88CC => "LLDP",
        0x888E => "EAPOL",
        0x8863 or 0x8864 => "PPPoE",
        0x0842 => "WOL",
        0x88E1 => "HomePlug AV",
        0x893A => "IEEE1905",
        0x88F7 => "PTPv2",
        0x8808 => "MAC Control",
        0x8899 => "RRCP",
        _ => Format.Hex16(type),
    };

    private static string EtherTypeFilter(int type) => type switch
    {
        0x88CC => "lldp",
        0x888E => "eapol",
        0x8863 or 0x8864 => "pppoe",
        0x0842 => "wol",
        0x88F7 => "ptp",
        _ => "data",
    };

    // ---- ARP ----

    private static void Arp(Context c, int at)
    {
        var data = c.Data;
        var layer = c.D.Layer("arp", "Address Resolution Protocol", at, Math.Max(0, data.Length - at));
        c.D.Protocol = "ARP";
        if (!Need(c, layer, at, 8, data.Length, "ARP header")) return;
        int hardwareType = U16(data, at), protocolType = U16(data, at + 2), hardwareSize = data[at + 4], protocolSize = data[at + 5];
        int opcode = U16(data, at + 6);
        layer.AddField("arp.hw.type", "Hardware type", hardwareType, at, 2, hardwareType == 1 ? "Ethernet (1)" : hardwareType.ToString(CultureInfo.InvariantCulture));
        layer.AddField("arp.proto.type", "Protocol type", protocolType, at + 2, 2, $"{EtherTypeName(protocolType)} ({Format.Hex16(protocolType)})");
        layer.AddField("arp.hw.size", "Hardware size", hardwareSize, at + 4, 1);
        layer.AddField("arp.proto.size", "Protocol size", protocolSize, at + 5, 1);
        string op = opcode switch { 1 => "request", 2 => "reply", 3 => "reverse request", 4 => "reverse reply", _ => "unknown" };
        layer.AddField("arp.opcode", "Opcode", opcode, at + 6, 2, $"{op} ({opcode})");
        if (hardwareSize != 6 || protocolSize != 4 || !Need(c, layer, at + 8, 20, data.Length, "ARP addresses")) { c.D.Info = $"ARP {op}"; return; }
        var senderMac = Bytes(data, at + 8, 6);
        var senderIp = Ip4(data, at + 14);
        var targetMac = Bytes(data, at + 18, 6);
        var targetIp = Ip4(data, at + 24);
        layer.AddField("arp.src.hw_mac", "Sender MAC address", senderMac, at + 8, 6);
        layer.AddField("arp.src.proto_ipv4", "Sender IP address", senderIp, at + 14, 4);
        layer.AddField("arp.dst.hw_mac", "Target MAC address", targetMac, at + 18, 6);
        layer.AddField("arp.dst.proto_ipv4", "Target IP address", targetIp, at + 24, 4);
        bool gratuitous = senderIp.Equals(targetIp);
        c.D.Layers[^1] = new ProtoNode($"Address Resolution Protocol ({op}{(gratuitous ? "/gratuitous ARP" : "")})", "arp", true, at, 28) { IsLayer = true };
        foreach (var child in layer.Children!) c.D.Layers[^1].Add(child);
        if (gratuitous) c.D.Layers[^1].AddFlag("arp.isgratuitous", "[Is gratuitous: True]", warning: false);
        c.D.Info = opcode switch
        {
            1 when gratuitous => $"Gratuitous ARP for {senderIp} (Request)",
            1 => $"Who has {targetIp}? Tell {senderIp}",
            2 when gratuitous => $"Gratuitous ARP for {senderIp} (Reply)",
            2 => $"{senderIp} is at {Format.Mac(senderMac)}",
            _ => $"ARP {op}",
        };
    }

    // ---- IPv4 / IPv6 ----

    private static void IpByVersion(Context c, int at)
    {
        int version = c.Data[at] >> 4;
        if (version == 4) Ipv4(c, at);
        else if (version == 6) Ipv6(c, at);
        else
        {
            var layer = c.D.Layer("data", "Raw packet data", at, c.Data.Length - at);
            Malformed(c, layer, $"IP version {version}");
        }
    }

    private static void Ipv4(Context c, int at)
    {
        var data = c.Data;
        var layer = c.D.Layer("ip", "Internet Protocol Version 4", at, Math.Max(0, data.Length - at));
        c.D.Protocol = "IPv4";
        if (!Need(c, layer, at, 20, data.Length, "IPv4 header")) return;
        int versionAndLength = data[at], headerLength = (versionAndLength & 0x0F) * 4;
        int total = U16(data, at + 2), id = U16(data, at + 4), flagsAndOffset = U16(data, at + 6);
        int ttl = data[at + 8], protocol = data[at + 9], checksum = U16(data, at + 10);
        if (headerLength < 20 || !Need(c, layer, at, headerLength, data.Length, "IPv4 header length")) return;
        var source = Ip4(data, at + 12);
        var destination = Ip4(data, at + 16);
        // 0: Windows handed a large send over before the network card split it ("segmentation offload").
        bool offloaded = total == 0;
        int end = total >= headerLength && at + total <= data.Length ? at + total : data.Length;

        layer = new ProtoNode($"Internet Protocol Version 4, Src: {source}, Dst: {destination}", "ip", true, at, end - at) { IsLayer = true };
        c.D.Layers[^1] = layer;
        layer.Add(new ProtoNode(Format.Bits(versionAndLength, 0xF0, 8, "Version", (versionAndLength >> 4).ToString(CultureInfo.InvariantCulture)), "ip.version", versionAndLength >> 4, at, 1));
        layer.Add(new ProtoNode(Format.Bits(versionAndLength, 0x0F, 8, "Header Length", $"{headerLength} bytes ({headerLength / 4})"), "ip.hdr_len", headerLength, at, 1));
        int ds = data[at + 1];
        var dsNode = layer.AddField("ip.dsfield", "Differentiated Services Field", ds, at + 1, 1, $"{Format.Hex8(ds)} (DSCP: {DscpName(ds >> 2)}, ECN: {EcnName(ds & 3)})");
        dsNode.Add(new ProtoNode(Format.Bits(ds, 0xFC, 8, "Differentiated Services Codepoint", $"{DscpName(ds >> 2)} ({ds >> 2})"), "ip.dsfield.dscp", ds >> 2, at + 1, 1));
        dsNode.Add(new ProtoNode(Format.Bits(ds, 0x03, 8, "Explicit Congestion Notification", $"{EcnName(ds & 3)} ({ds & 3})"), "ip.dsfield.ecn", ds & 3, at + 1, 1));
        layer.AddField("ip.len", "Total Length", total, at + 2, 2, offloaded
            ? $"{total} (not right: Windows hands over sent packets before splitting them, \"TCP segmentation offload\")"
            : total.ToString(CultureInfo.InvariantCulture));
        layer.AddField("ip.id", "Identification", id, at + 4, 2, $"{Format.Hex16(id)} ({id})");
        int flags = flagsAndOffset >> 13;
        bool df = (flags & 2) != 0, mf = (flags & 1) != 0;
        var flagNode = layer.AddField("ip.flags", "Flags", flags, at + 6, 1,
            $"{Format.Hex8(flags)}{(df ? ", Don't fragment" : "")}{(mf ? ", More fragments" : "")}");
        flagNode.Add(new ProtoNode(Format.Bits(data[at + 6], 0x80, 8, "Reserved bit"), "ip.flags.rb", (flags & 4) != 0, at + 6, 1));
        flagNode.Add(new ProtoNode(Format.Bits(data[at + 6], 0x40, 8, "Don't fragment"), "ip.flags.df", df, at + 6, 1));
        flagNode.Add(new ProtoNode(Format.Bits(data[at + 6], 0x20, 8, "More fragments"), "ip.flags.mf", mf, at + 6, 1));
        int fragmentOffset = (flagsAndOffset & 0x1FFF) * 8;
        layer.Add(new ProtoNode(Format.Bits(flagsAndOffset, 0x1FFF, 16, "Fragment Offset", fragmentOffset.ToString(CultureInfo.InvariantCulture)), "ip.frag_offset", fragmentOffset, at + 6, 2));
        layer.AddField("ip.ttl", "Time to Live", ttl, at + 8, 1);
        layer.AddField("ip.proto", "Protocol", protocol, at + 9, 1, $"{IpProtocolName(protocol)} ({protocol})");
        layer.AddField("ip.checksum", "Header Checksum", checksum, at + 10, 2, $"{Format.Hex16(checksum)} [validation disabled]");
        layer.AddField("ip.src", "Source Address", source, at + 12, 4);
        layer.AddField("ip.dst", "Destination Address", destination, at + 16, 4);
        if (headerLength > 20) layer.AddText($"Options: ({headerLength - 20} bytes)", at + 20, headerLength - 20);
        c.NetworkSource = source.ToString();
        c.NetworkDestination = destination.ToString();
        if (destination.Equals(IPAddress.Broadcast) || destination.GetAddressBytes()[0] >= 224) c.Broadcast = true;
        if (c.Ethernet is { } ethernet && end < data.Length)
            ethernet.AddField("eth.padding", "Padding", Bytes(data, end, data.Length - end), end, data.Length - end);

        if (fragmentOffset > 0)
        {
            c.D.Info = $"Fragmented IP protocol (proto={IpProtocolName(protocol)} {protocol}, off={fragmentOffset}, ID={id:x4})";
            if (end > at + headerLength) c.D.Layer("data", $"Data ({end - at - headerLength} bytes)", at + headerLength, end - at - headerLength);
            return;
        }
        if (mf) layer.AddText("[This is the first fragment; the rest of the datagram follows in later packets]");
        Transport(c, protocol, at + headerLength, end, layer);
    }

    private static void Ipv6(Context c, int at)
    {
        var data = c.Data;
        var layer = c.D.Layer("ipv6", "Internet Protocol Version 6", at, Math.Max(0, data.Length - at));
        c.D.Protocol = "IPv6";
        if (!Need(c, layer, at, 40, data.Length, "IPv6 header")) return;
        uint first = U32(data, at);
        int trafficClass = (int)((first >> 20) & 0xFF), flow = (int)(first & 0xFFFFF);
        int payloadLength = U16(data, at + 4), nextHeader = data[at + 6], hopLimit = data[at + 7];
        var source = Ip6(data, at + 8);
        var destination = Ip6(data, at + 24);
        int end = payloadLength > 0 && at + 40 + payloadLength <= data.Length ? at + 40 + payloadLength : data.Length;

        layer = new ProtoNode($"Internet Protocol Version 6, Src: {source}, Dst: {destination}", "ipv6", true, at, end - at) { IsLayer = true };
        c.D.Layers[^1] = layer;
        layer.Add(new ProtoNode(Format.Bits(data[at], 0xF0, 8, "Version", "6"), "ipv6.version", 6, at, 1));
        layer.AddField("ipv6.tclass", "Traffic Class", trafficClass, at, 2, $"{Format.Hex8(trafficClass)} (DSCP: {DscpName(trafficClass >> 2)}, ECN: {EcnName(trafficClass & 3)})");
        layer.AddField("ipv6.flow", "Flow Label", flow, at + 1, 3, "0x" + flow.ToString("x5", CultureInfo.InvariantCulture));
        layer.AddField("ipv6.plen", "Payload Length", payloadLength, at + 4, 2);
        layer.AddField("ipv6.nxt", "Next Header", nextHeader, at + 6, 1, $"{IpProtocolName(nextHeader)} ({nextHeader})");
        layer.AddField("ipv6.hlim", "Hop Limit", hopLimit, at + 7, 1);
        layer.AddField("ipv6.src", "Source Address", source, at + 8, 16);
        layer.AddField("ipv6.dst", "Destination Address", destination, at + 24, 16);
        c.NetworkSource = source.ToString();
        c.NetworkDestination = destination.ToString();
        if (destination.IsIPv6Multicast) c.Broadcast = true;
        if (c.Ethernet is { } ethernet && end < data.Length)
            ethernet.AddField("eth.padding", "Padding", Bytes(data, end, data.Length - end), end, data.Length - end);

        int next = at + 40, header = nextHeader;
        for (int guard = 0; guard < 10; guard++)
        {
            if (header is 0 or 43 or 60)
            {
                if (!Need(c, layer, next, 8, end, "IPv6 extension header")) return;
                int length = (data[next + 1] + 1) * 8;
                string name = header == 0 ? "IPv6 Hop-by-Hop Option" : header == 43 ? "Routing Header for IPv6" : "Destination Options for IPv6";
                string prefix = header == 0 ? "ipv6.hopopts" : header == 43 ? "ipv6.routing" : "ipv6.dstopts";
                var ext = layer.AddText(name, next, length);
                ext.AddField(prefix + ".nxt", "Next Header", (int)data[next], next, 1, $"{IpProtocolName(data[next])} ({data[next]})");
                ext.AddField(prefix + ".len", "Length", (int)data[next + 1], next + 1, 1, $"{data[next + 1]} ({length} bytes)");
                header = data[next];
                next += length;
            }
            else if (header == 44)
            {
                if (!Need(c, layer, next, 8, end, "IPv6 fragment header")) return;
                int offsetAndFlags = U16(data, next + 2);
                int offset = offsetAndFlags & 0xFFF8;
                bool more = (offsetAndFlags & 1) != 0;
                var ext = layer.AddText("Fragment Header for IPv6", next, 8);
                ext.AddField("ipv6.fragment.nxt", "Next header", (int)data[next], next, 1, $"{IpProtocolName(data[next])} ({data[next]})");
                ext.AddField("ipv6.fragment.offset", "Offset", offset, next + 2, 2);
                ext.AddField("ipv6.fragment.more", "More Fragments", more, next + 2, 2, more ? "Yes" : "No");
                ext.AddField("ipv6.fragment.id", "Identification", (long)U32(data, next + 4), next + 4, 4, Format.Hex32(U32(data, next + 4)));
                header = data[next];
                next += 8;
                if (offset > 0)
                {
                    c.D.Info = $"IPv6 fragment (off={offset} more={(more ? "y" : "n")} ident={Format.Hex32(U32(data, next - 4))} nxt={header})";
                    return;
                }
            }
            else if (header == 51)
            {
                if (!Need(c, layer, next, 8, end, "authentication header")) return;
                int length = (data[next + 1] + 2) * 4;
                layer.AddText("Authentication Header", next, length);
                header = data[next];
                next += length;
            }
            else break;
        }
        Transport(c, header, next, end, layer);
    }

    private static string DscpName(int dscp) => dscp switch
    {
        0 => "CS0",
        8 => "CS1",
        16 => "CS2",
        24 => "CS3",
        32 => "CS4",
        40 => "CS5",
        48 => "CS6",
        56 => "CS7",
        46 => "EF",
        10 => "AF11", 12 => "AF12", 14 => "AF13",
        18 => "AF21", 20 => "AF22", 22 => "AF23",
        26 => "AF31", 28 => "AF32", 30 => "AF33",
        34 => "AF41", 36 => "AF42", 38 => "AF43",
        44 => "VOICE-ADMIT",
        1 => "LE",
        _ => $"Unknown ({dscp})",
    };

    private static string EcnName(int ecn) => ecn switch { 0 => "Not-ECT", 1 => "ECT(1)", 2 => "ECT(0)", _ => "CE" };

    internal static string IpProtocolName(int protocol) => protocol switch
    {
        0 => "IPv6 Hop-by-Hop Option",
        1 => "ICMP",
        2 => "IGMP",
        4 => "IPIP",
        6 => "TCP",
        17 => "UDP",
        41 => "IPv6",
        43 => "IPv6 Routing",
        44 => "IPv6 Fragment",
        47 => "GRE",
        50 => "ESP",
        51 => "AH",
        58 => "ICMPv6",
        59 => "IPv6 No Next Header",
        60 => "IPv6 Destination Options",
        89 => "OSPF",
        103 => "PIM",
        112 => "VRRP",
        132 => "SCTP",
        _ => "Unknown",
    };

    // ---- ICMP / ICMPv6 / IGMP ----

    private static void Icmp(Context c, int at, int end)
    {
        var data = c.Data;
        var layer = c.D.Layer("icmp", "Internet Control Message Protocol", at, Math.Max(0, end - at));
        c.D.Protocol = "ICMP";
        if (!Need(c, layer, at, 4, end, "ICMP header")) return;
        int type = data[at], code = data[at + 1];
        string typeName = IcmpTypeName(type), codeName = IcmpCodeName(type, code);
        layer.AddField("icmp.type", "Type", type, at, 1, $"{type} ({typeName})");
        layer.AddField("icmp.code", "Code", code, at + 1, 1, codeName.Length > 0 ? $"{code} ({codeName})" : code.ToString(CultureInfo.InvariantCulture));
        layer.AddField("icmp.checksum", "Checksum", (int)U16(data, at + 2), at + 2, 2, $"{Format.Hex16(U16(data, at + 2))} [unverified]");
        if (type is 0 or 8 or 13 or 14)
        {
            if (!Need(c, layer, at + 4, 4, end, "ICMP echo header")) return;
            int ident = U16(data, at + 4), seq = U16(data, at + 6);
            layer.AddField("icmp.ident", "Identifier (BE)", ident, at + 4, 2, $"{ident} ({Format.Hex16(ident)})");
            layer.AddField("icmp.seq", "Sequence Number (BE)", seq, at + 6, 2, $"{seq} ({Format.Hex16(seq)})");
            if (end > at + 8) layer.AddField("data.data", "Data", Bytes(data, at + 8, end - at - 8), at + 8, end - at - 8, $"{end - at - 8} bytes");
            int ttl = c.D.Layers.FirstOrDefault(l => l.Field == "ip")?.Children?.FirstOrDefault(n => n.Field == "ip.ttl")?.Value is int t ? t : -1;
            c.D.Info = $"{typeName}  id={Format.Hex16(ident)}, seq={seq}/{BinaryPrimitives.ReverseEndianness((ushort)seq)}{(ttl >= 0 ? $", ttl={ttl}" : "")}";
            return;
        }
        c.D.Info = codeName.Length > 0 ? $"{typeName} ({codeName})" : typeName;
        if (type is 3 or 4 or 5 or 11 or 12)
        {
            c.IcmpError = true;
            // The packet that caused the error: its IP header and the first bytes after it.
            if (end >= at + 8 + 20 && data[at + 8] >> 4 == 4)
            {
                int inner = at + 8, innerLength = (data[inner] & 0x0F) * 4;
                if (innerLength >= 20 && inner + innerLength <= end)
                {
                    var src = Ip4(data, inner + 12);
                    var dst = Ip4(data, inner + 16);
                    int proto = data[inner + 9];
                    var quoted = layer.AddText($"Internet Protocol Version 4, Src: {src}, Dst: {dst} (the packet this is about)", inner, end - inner);
                    quoted.AddField("icmp.ip.src", "Source Address", src, inner + 12, 4);
                    quoted.AddField("icmp.ip.dst", "Destination Address", dst, inner + 16, 4);
                    quoted.AddField("icmp.ip.proto", "Protocol", proto, inner + 9, 1, $"{IpProtocolName(proto)} ({proto})");
                    if (proto is 6 or 17 && inner + innerLength + 4 <= end)
                    {
                        int sp = U16(data, inner + innerLength), dp = U16(data, inner + innerLength + 2);
                        quoted.AddText($"{IpProtocolName(proto)} Src Port: {sp}, Dst Port: {dp}", inner + innerLength, 4);
                    }
                }
            }
        }
        if (type == 5 && end >= at + 8) layer.AddField("icmp.redir_gw", "Gateway Address", Ip4(data, at + 4), at + 4, 4);
    }

    private static string IcmpTypeName(int type) => type switch
    {
        0 => "Echo (ping) reply",
        3 => "Destination unreachable",
        4 => "Source quench (flow control)",
        5 => "Redirect",
        8 => "Echo (ping) request",
        9 => "Router advertisement",
        10 => "Router solicitation",
        11 => "Time-to-live exceeded",
        12 => "Parameter problem",
        13 => "Timestamp request",
        14 => "Timestamp reply",
        _ => $"Unknown ICMP (type {type})",
    };

    private static string IcmpCodeName(int type, int code) => (type, code) switch
    {
        (3, 0) => "Network unreachable",
        (3, 1) => "Host unreachable",
        (3, 2) => "Protocol unreachable",
        (3, 3) => "Port unreachable",
        (3, 4) => "Fragmentation needed",
        (3, 9) or (3, 10) => "Communication administratively prohibited",
        (3, 13) => "Communication administratively filtered",
        (5, 0) => "Redirect for network",
        (5, 1) => "Redirect for host",
        (11, 0) => "Time to live exceeded in transit",
        (11, 1) => "Fragment reassembly time exceeded",
        _ => "",
    };

    private static void Icmpv6(Context c, int at, int end)
    {
        var data = c.Data;
        var layer = c.D.Layer("icmpv6", "Internet Control Message Protocol v6", at, Math.Max(0, end - at));
        c.D.Protocol = "ICMPv6";
        if (!Need(c, layer, at, 4, end, "ICMPv6 header")) return;
        int type = data[at], code = data[at + 1];
        string typeName = type switch
        {
            1 => "Destination Unreachable",
            2 => "Packet Too Big",
            3 => "Time Exceeded",
            4 => "Parameter Problem",
            128 => "Echo (ping) request",
            129 => "Echo (ping) reply",
            130 => "Multicast Listener Query",
            131 => "Multicast Listener Report",
            132 => "Multicast Listener Done",
            133 => "Router Solicitation",
            134 => "Router Advertisement",
            135 => "Neighbor Solicitation",
            136 => "Neighbor Advertisement",
            137 => "Redirect",
            143 => "Multicast Listener Report Message v2",
            _ => $"Unknown (type {type})",
        };
        layer.AddField("icmpv6.type", "Type", type, at, 1, $"{typeName} ({type})");
        layer.AddField("icmpv6.code", "Code", code, at + 1, 1);
        layer.AddField("icmpv6.checksum", "Checksum", (int)U16(data, at + 2), at + 2, 2, $"{Format.Hex16(U16(data, at + 2))} [unverified]");
        c.D.Info = typeName;
        c.IcmpError = type is >= 1 and <= 4;
        if (type is 128 or 129 && end >= at + 8)
        {
            int ident = U16(data, at + 4), seq = U16(data, at + 6);
            layer.AddField("icmpv6.echo.identifier", "Identifier", ident, at + 4, 2, Format.Hex16(ident));
            layer.AddField("icmpv6.echo.sequence_number", "Sequence", seq, at + 6, 2);
            c.D.Info = $"{typeName} id={Format.Hex16(ident)}, seq={seq}";
        }
        else if (type is 135 or 136 && end >= at + 24)
        {
            var target = Ip6(data, at + 8);
            string field = type == 135 ? "icmpv6.nd.ns.target_address" : "icmpv6.nd.na.target_address";
            layer.AddField(field, "Target Address", target, at + 8, 16);
            c.D.Info = type == 135 ? $"Neighbor Solicitation for {target}" : $"Neighbor Advertisement {target}";
            // Options: the link-layer address that goes with it.
            for (int o = at + 24; o + 8 <= end;)
            {
                int optionType = data[o], optionLength = data[o + 1] * 8;
                if (optionLength == 0) break;
                if (optionType is 1 or 2 && optionLength == 8)
                {
                    var mac = Bytes(data, o + 2, 6);
                    layer.AddField(optionType == 1 ? "icmpv6.opt.linkaddr" : "icmpv6.opt.linkaddr", optionType == 1 ? "Source link-layer address" : "Target link-layer address", mac, o + 2, 6);
                    c.D.Info += (optionType == 1 ? " from " : " is at ") + Format.Mac(mac);
                }
                o += optionLength;
            }
        }
    }

    private static void Igmp(Context c, int at, int end)
    {
        var data = c.Data;
        var layer = c.D.Layer("igmp", "Internet Group Management Protocol", at, Math.Max(0, end - at));
        c.D.Protocol = "IGMP";
        if (!Need(c, layer, at, 8, end, "IGMP message")) return;
        int type = data[at];
        string name = type switch
        {
            0x11 => "Membership Query",
            0x12 => "Membership Report (v1)",
            0x16 => "Membership Report (v2)",
            0x17 => "Leave Group",
            0x22 => "Membership Report (v3)",
            _ => $"Unknown (type {Format.Hex8(type)})",
        };
        layer.AddField("igmp.type", "Type", type, at, 1, $"{name} ({Format.Hex8(type)})");
        if (type != 0x22)
        {
            var group = Ip4(data, at + 4);
            layer.AddField("igmp.maddr", "Multicast Address", group, at + 4, 4);
            c.D.Info = type == 0x17 ? $"Leave Group {group}" : $"{name} {group}";
        }
        else c.D.Info = name;
    }
}
