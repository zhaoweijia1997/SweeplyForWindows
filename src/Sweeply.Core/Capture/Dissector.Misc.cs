using System.Globalization;
using System.Net;
using System.Text;

namespace Sweeply.Core.Capture;

public static partial class Dissector
{
    // ---- DHCP ----

    private static bool Dhcp(Context c, int at, int end)
    {
        var data = c.Data;
        if (end - at < 240 || U32(data, at + 236) != 0x63825363) return false; // magic cookie
        int op = data[at];
        uint xid = U32(data, at + 4);
        var layer = c.D.Layer("dhcp", "Dynamic Host Configuration Protocol", at, end - at);
        c.D.Protocol = "DHCP";
        layer.AddField("dhcp.type", "Message type", op, at, 1, op == 1 ? "Boot Request (1)" : "Boot Reply (2)");
        layer.AddField("dhcp.hw.type", "Hardware type", (int)data[at + 1], at + 1, 1, data[at + 1] == 1 ? "Ethernet (0x01)" : Format.Hex8(data[at + 1]));
        layer.AddField("dhcp.hw.len", "Hardware address length", (int)data[at + 2], at + 2, 1);
        layer.AddField("dhcp.hops", "Hops", (int)data[at + 3], at + 3, 1);
        layer.AddField("dhcp.id", "Transaction ID", (long)xid, at + 4, 4, Format.Hex32(xid));
        layer.AddField("dhcp.secs", "Seconds elapsed", (int)U16(data, at + 8), at + 8, 2);
        layer.AddField("dhcp.flags", "Bootp flags", (int)U16(data, at + 10), at + 10, 2, (U16(data, at + 10) & 0x8000) != 0 ? $"{Format.Hex16(U16(data, at + 10))} (Broadcast)" : $"{Format.Hex16(U16(data, at + 10))} (Unicast)");
        layer.AddField("dhcp.ip.client", "Client IP address", Ip4(data, at + 12), at + 12, 4);
        layer.AddField("dhcp.ip.your", "Your (client) IP address", Ip4(data, at + 16), at + 16, 4);
        layer.AddField("dhcp.ip.server", "Next server IP address", Ip4(data, at + 20), at + 20, 4);
        layer.AddField("dhcp.ip.relay", "Relay agent IP address", Ip4(data, at + 24), at + 24, 4);
        layer.AddField("dhcp.hw.mac_addr", "Client MAC address", Bytes(data, at + 28, 6), at + 28, 6);
        layer.AddField("dhcp.cookie", "Magic cookie", "DHCP", at + 236, 4);

        string type = "";
        for (int p = at + 240; p < end;)
        {
            int code = data[p];
            if (code == 0) { p++; continue; }
            if (code == 255) { layer.AddText("Option: (255) End", p, 1); break; }
            if (p + 1 >= end) break;
            int length = data[p + 1];
            int v = p + 2;
            if (v + length > end) { Malformed(c, layer, "DHCP option"); break; }
            string name = DhcpOptionName(code);
            var option = layer.Add(new ProtoNode($"Option: ({code}) {name}", "dhcp.option.type", code, p, length + 2));
            switch (code)
            {
                case 53 when length == 1:
                    type = DhcpMessageType(data[v]);
                    option.AddField("dhcp.option.dhcp", "DHCP", (int)data[v], v, 1, $"{type} ({data[v]})");
                    break;
                case 50 when length == 4: option.AddField("dhcp.option.requested_ip_address", "Requested IP Address", Ip4(data, v), v, 4); break;
                case 54 when length == 4: option.AddField("dhcp.option.dhcp_server_id", "DHCP Server Identifier", Ip4(data, v), v, 4); break;
                case 1 when length == 4: option.AddField("dhcp.option.subnet_mask", "Subnet Mask", Ip4(data, v), v, 4); break;
                case 3:
                    for (int i = 0; i + 4 <= length; i += 4) option.AddField("dhcp.option.router", "Router", Ip4(data, v + i), v + i, 4);
                    break;
                case 6:
                    for (int i = 0; i + 4 <= length; i += 4) option.AddField("dhcp.option.domain_name_server", "Domain Name Server", Ip4(data, v + i), v + i, 4);
                    break;
                case 51 when length == 4:
                {
                    uint seconds = U32(data, v);
                    option.AddField("dhcp.option.ip_address_lease_time", "IP Address Lease Time", (long)seconds, v, 4, $"({seconds}s) {Ttl(seconds)}");
                    break;
                }
                case 12: option.AddField("dhcp.option.hostname", "Host Name", Encoding.ASCII.GetString(data, v, length), v, length); break;
                case 15: option.AddField("dhcp.option.domain_name", "Domain Name", Encoding.ASCII.GetString(data, v, length), v, length); break;
                case 60: option.AddField("dhcp.option.vendor_class_id", "Vendor class identifier", Encoding.ASCII.GetString(data, v, length), v, length); break;
                case 61: option.AddField("dhcp.option.client_id", "Client identifier", Bytes(data, v, length), v, length, Format.Hex(data.AsSpan(v, length))); break;
                default:
                    if (length > 0) option.AddField("dhcp.option.value", "Value", Bytes(data, v, length), v, length, Format.Hex(data.AsSpan(v, length), 32));
                    break;
            }
            p = v + length;
        }
        c.D.Info = $"DHCP {(type.Length > 0 ? type : op == 1 ? "Request" : "Reply")} - Transaction ID {Format.Hex32(xid)}";
        return true;
    }

    private static string DhcpMessageType(int type) => type switch
    {
        1 => "Discover", 2 => "Offer", 3 => "Request", 4 => "Decline", 5 => "ACK", 6 => "NAK", 7 => "Release", 8 => "Inform",
        _ => $"Unknown ({type})",
    };

    private static string DhcpOptionName(int code) => code switch
    {
        1 => "Subnet Mask", 3 => "Router", 6 => "Domain Name Server", 12 => "Host Name", 15 => "Domain Name",
        28 => "Broadcast Address", 42 => "Network Time Protocol Servers", 43 => "Vendor-Specific Information",
        50 => "Requested IP Address", 51 => "IP Address Lease Time", 53 => "DHCP Message Type", 54 => "DHCP Server Identifier",
        55 => "Parameter Request List", 57 => "Maximum DHCP Message Size", 58 => "Renewal Time Value", 59 => "Rebinding Time Value",
        60 => "Vendor class identifier", 61 => "Client identifier", 81 => "Client Fully Qualified Domain Name",
        _ => "Unknown",
    };

    // ---- NTP ----

    private static bool Ntp(Context c, int at, int end)
    {
        var data = c.Data;
        if (end - at < 48) return false;
        int flags = data[at];
        int leap = flags >> 6, version = (flags >> 3) & 7, mode = flags & 7;
        string modeName = mode switch { 1 => "symmetric active", 2 => "symmetric passive", 3 => "client", 4 => "server", 5 => "broadcast", 6 => "control", _ => "reserved" };
        var layer = c.D.Layer("ntp", "Network Time Protocol (NTP Version " + version + ", " + modeName + ")", at, end - at);
        c.D.Protocol = "NTP";
        var flagNode = layer.AddField("ntp.flags", "Flags", flags, at, 1, $"{Format.Hex8(flags)}, Leap Indicator: {leap}, Version number: NTP Version {version}, Mode: {modeName}");
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0xC0, 8, "Leap Indicator", leap == 0 ? "no warning (0)" : leap.ToString(CultureInfo.InvariantCulture)), "ntp.flags.li", leap, at, 1));
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x38, 8, "Version number", $"NTP Version {version} ({version})"), "ntp.flags.vn", version, at, 1));
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x07, 8, "Mode", $"{modeName} ({mode})"), "ntp.flags.mode", mode, at, 1));
        layer.AddField("ntp.stratum", "Peer Clock Stratum", (int)data[at + 1], at + 1, 1);
        layer.AddField("ntp.ppoll", "Peer Polling Interval", (int)(sbyte)data[at + 2], at + 2, 1);
        layer.AddField("ntp.precision", "Peer Clock Precision", (int)(sbyte)data[at + 3], at + 3, 1);
        foreach (var (offset, field, label) in new[] { (16, "ntp.reftime", "Reference Timestamp"), (24, "ntp.org", "Origin Timestamp"), (32, "ntp.rec", "Receive Timestamp"), (40, "ntp.xmt", "Transmit Timestamp") })
        {
            uint seconds = U32(data, at + offset), fraction = U32(data, at + offset + 4);
            string shown = seconds == 0 ? "(0)" : new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds + fraction / 4294967296.0).ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'", CultureInfo.InvariantCulture);
            layer.AddField(field, label, shown, at + offset, 8);
        }
        c.D.Info = $"NTP Version {version}, {modeName}";
        return true;
    }

    // ---- MQTT ----

    private static readonly string[] MqttTypes =
    {
        "Reserved", "Connect Command", "Connect Ack", "Publish Message", "Publish Ack", "Publish Received", "Publish Release",
        "Publish Complete", "Subscribe Request", "Subscribe Ack", "Unsubscribe Request", "Unsubscribe Ack", "Ping Request",
        "Ping Response", "Disconnect Req", "Authentication Exchange",
    };

    /// <summary>MQTT 3.1, 3.1.1 and 5.0 control packets; several may share a TCP segment.</summary>
    private static bool Mqtt(Context c, int at, int end)
    {
        var data = c.Data;
        if (end - at < 2 || (data[at] >> 4) == 0) return false;
        int level = c.Stream.MqttLevel != 0 ? c.Stream.MqttLevel : 4;
        var infos = new List<string>();
        var layers = 0;
        for (int p = at; p < end;)
        {
            int header = data[p], type = header >> 4;
            if (type == 0) break;
            if (!MqttHints.ReadVarInt(data, p + 1, end, out int remaining, out int lengthBytes))
            {
                var partial = c.D.Layer("mqtt", "MQ Telemetry Transport Protocol", p, end - p);
                partial.AddText("[The start of a message whose length continues in the next packet]", p, end - p, warning: true);
                break;
            }
            int body = p + 1 + lengthBytes;
            int messageEnd = body + remaining;
            bool complete = messageEnd <= end;
            int stop = Math.Min(end, messageEnd);
            var layer = c.D.Layer("mqtt", $"MQ Telemetry Transport Protocol, {MqttTypes[type]}", p, stop - p);
            layers++;
            var headerNode = layer.AddField("mqtt.hdrflags", "Header Flags", header, p, 1, $"{Format.Hex8(header)}, Message Type: {MqttTypes[type]}");
            headerNode.Add(new ProtoNode(Format.Bits(header, 0xF0, 8, "Message Type", $"{MqttTypes[type]} ({type})"), "mqtt.msgtype", type, p, 1));
            int qos = (header >> 1) & 3;
            if (type == 3)
            {
                headerNode.Add(new ProtoNode(Format.Bits(header, 0x08, 8, "DUP Flag"), "mqtt.dupflag", (header & 8) != 0, p, 1));
                headerNode.Add(new ProtoNode(Format.Bits(header, 0x06, 8, "QoS Level", QosName(qos)), "mqtt.qos", qos, p, 1));
                headerNode.Add(new ProtoNode(Format.Bits(header, 0x01, 8, "Retain"), "mqtt.retain", (header & 1) != 0, p, 1));
            }
            layer.AddField("mqtt.len", "Msg Len", remaining, p + 1, lengthBytes);
            string info = MqttTypes[type];
            if (!complete) layer.AddText($"[This message continues in the next packet(s): {end - body} of {remaining} bytes here]", warning: true);
            try
            {
                info = MqttBody(c, layer, type, qos, body, stop, level, complete) ?? info;
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
            {
                Malformed(c, layer, "MQTT message");
            }
            infos.Add(info);
            if (!complete) break;
            p = messageEnd;
        }
        if (layers == 0) return infos.Count > 0;
        c.D.Protocol = "MQTT";
        c.D.Info = string.Join(", ", infos);
        return true;
    }

    private static string? MqttBody(Context c, ProtoNode layer, int type, int qos, int at, int end, int level, bool complete)
    {
        var data = c.Data;
        int p = at;
        string? String(string field, string label)
        {
            if (p + 2 > end) return null;
            int length = U16(data, p);
            if (p + 2 + length > end) return null;
            string text = Encoding.UTF8.GetString(data, p + 2, length);
            layer.AddField(field, label, text, p + 2, length);
            p += 2 + length;
            return text;
        }
        void Properties()
        {
            if (level < 5 || p >= end) return;
            if (!MqttHints.ReadVarInt(data, p, end, out int length, out int bytes)) return;
            if (length > 0) layer.AddField("mqtt.properties", "Properties", Bytes(data, p + bytes, Math.Min(length, end - p - bytes)), p, bytes + length, $"{length} bytes");
            p += bytes + length;
        }
        switch (type)
        {
            case 1: // CONNECT
            {
                string? name = String("mqtt.protoname", "Protocol Name");
                if (name is null || p + 4 > end) return null;
                int protocolLevel = data[p], flags = data[p + 1], keepAlive = U16(data, p + 2);
                layer.AddField("mqtt.ver", "Version", protocolLevel, p, 1, protocolLevel switch { 3 => "MQTT v3.1 (3)", 4 => "MQTT v3.1.1 (4)", 5 => "MQTT v5.0 (5)", _ => protocolLevel.ToString(CultureInfo.InvariantCulture) });
                var flagNode = layer.AddField("mqtt.conflags", "Connect Flags", flags, p + 1, 1, Format.Hex8(flags));
                flagNode.Add(new ProtoNode(Format.Bits(flags, 0x80, 8, "User Name Flag"), "mqtt.conflag.uname", (flags & 0x80) != 0, p + 1, 1));
                flagNode.Add(new ProtoNode(Format.Bits(flags, 0x40, 8, "Password Flag"), "mqtt.conflag.passwd", (flags & 0x40) != 0, p + 1, 1));
                flagNode.Add(new ProtoNode(Format.Bits(flags, 0x20, 8, "Will Retain"), "mqtt.conflag.retain", (flags & 0x20) != 0, p + 1, 1));
                flagNode.Add(new ProtoNode(Format.Bits(flags, 0x18, 8, "QoS Level", QosName((flags >> 3) & 3)), "mqtt.conflag.qos", (flags >> 3) & 3, p + 1, 1));
                flagNode.Add(new ProtoNode(Format.Bits(flags, 0x04, 8, "Will Flag"), "mqtt.conflag.willflag", (flags & 0x04) != 0, p + 1, 1));
                flagNode.Add(new ProtoNode(Format.Bits(flags, 0x02, 8, "Clean Session Flag"), "mqtt.conflag.cleansess", (flags & 0x02) != 0, p + 1, 1));
                layer.AddField("mqtt.kalive", "Keep Alive", keepAlive, p + 2, 2);
                p += 4;
                level = protocolLevel;
                Properties();
                String("mqtt.clientid", "Client ID");
                if ((flags & 0x04) != 0)
                {
                    Properties();
                    String("mqtt.willtopic", "Will Topic");
                    if (p + 2 <= end)
                    {
                        int length = U16(data, p);
                        if (p + 2 + length <= end) layer.AddField("mqtt.willmsg", "Will Message", Bytes(data, p + 2, length), p + 2, length, Preview(data, p + 2, length));
                        p += 2 + length;
                    }
                }
                if ((flags & 0x80) != 0) String("mqtt.username", "User Name");
                if ((flags & 0x40) != 0 && p + 2 <= end)
                {
                    int length = U16(data, p);
                    if (p + 2 + length <= end) layer.AddField("mqtt.passwd", "Password", Encoding.UTF8.GetString(data, p + 2, length), p + 2, length);
                }
                return "Connect Command";
            }
            case 2: // CONNACK
            {
                if (p + 2 > end) return null;
                int flags = data[p], code = data[p + 1];
                layer.AddField("mqtt.conack.flags", "Acknowledge Flags", flags, p, 1, $"{Format.Hex8(flags)}{((flags & 1) != 0 ? " (Session Present)" : "")}");
                layer.AddField("mqtt.conack.val", "Return Code", code, p + 1, 1, $"{ConnackName(code, level)} ({code})");
                return code == 0 ? "Connect Ack" : $"Connect Ack ({ConnackName(code, level)})";
            }
            case 3: // PUBLISH
            {
                string? topic = String("mqtt.topic", "Topic");
                if (topic is null) return "Publish Message";
                int id = -1;
                if (qos > 0 && p + 2 <= end) { id = U16(data, p); layer.AddField("mqtt.msgid", "Message Identifier", id, p, 2); p += 2; }
                Properties();
                if (p < end) layer.AddField("mqtt.msg", "Message", Bytes(data, p, end - p), p, end - p, Preview(data, p, end - p) + (complete ? "" : " …"));
                return id >= 0 ? $"Publish Message (id={id}) [{topic}]" : $"Publish Message [{topic}]";
            }
            case 4 or 5 or 6 or 7 or 11: // the acknowledgements that carry only an id
            {
                if (p + 2 > end) return null;
                int id = U16(data, p);
                layer.AddField("mqtt.msgid", "Message Identifier", id, p, 2);
                return $"{MqttTypes[type]} (id={id})";
            }
            case 8 or 10: // SUBSCRIBE, UNSUBSCRIBE
            {
                if (p + 2 > end) return null;
                int id = U16(data, p);
                layer.AddField("mqtt.msgid", "Message Identifier", id, p, 2);
                p += 2;
                Properties();
                var topics = new List<string>();
                while (p + 2 <= end)
                {
                    string? filter = String("mqtt.topic", "Topic");
                    if (filter is null) break;
                    topics.Add(filter);
                    if (type == 8 && p < end) { layer.AddField("mqtt.sub.qos", "Requested QoS", data[p] & 3, p, 1, QosName(data[p] & 3)); p++; }
                }
                return $"{MqttTypes[type]} (id={id}) [{string.Join(", ", topics)}]";
            }
            case 9: // SUBACK
            {
                if (p + 2 > end) return null;
                int id = U16(data, p);
                layer.AddField("mqtt.msgid", "Message Identifier", id, p, 2);
                p += 2;
                Properties();
                for (; p < end; p++) layer.AddField("mqtt.suback.qos", "Granted QoS", (int)data[p], p, 1, data[p] >= 0x80 ? $"Failure ({Format.Hex8(data[p])})" : QosName(data[p]));
                return $"Subscribe Ack (id={id})";
            }
            default:
                return null;
        }
    }

    private static string QosName(int qos) => qos switch
    {
        0 => "At most once delivery (Fire and Forget) (0)",
        1 => "At least once delivery (Acknowledged deliver) (1)",
        2 => "Exactly once delivery (Assured Delivery) (2)",
        _ => $"Reserved ({qos})",
    };

    private static string ConnackName(int code, int level) => level >= 5
        ? code switch { 0 => "Success", 0x80 => "Unspecified error", 0x84 => "Unsupported Protocol Version", 0x85 => "Client Identifier not valid", 0x86 => "Bad User Name or Password", 0x87 => "Not authorized", 0x88 => "Server unavailable", _ => "Error" }
        : code switch { 0 => "Connection Accepted", 1 => "Connection Refused: unacceptable protocol version", 2 => "Connection Refused: identifier rejected", 3 => "Connection Refused: server unavailable", 4 => "Connection Refused: bad user name or password", 5 => "Connection Refused: not authorized", _ => "Reserved" };

    /// <summary>A payload as text when it is printable, in hex otherwise; at most 120 characters.</summary>
    private static string Preview(byte[] data, int at, int length)
    {
        var span = data.AsSpan(at, length);
        int printable = 0;
        foreach (byte b in span[..Math.Min(span.Length, 256)]) if (b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t' or >= 0x80) printable++;
        if (length > 0 && printable >= Math.Min(span.Length, 256) * 0.9)
        {
            try
            {
                string text = new UTF8Encoding(false, true).GetString(span[..Math.Min(span.Length, 400)]).Replace("\r", "\\r").Replace("\n", "\\n");
                return text.Length > 120 ? text[..120] + "…" : text;
            }
            catch (DecoderFallbackException) { }
        }
        return Format.Hex(span, 60);
    }

    // ---- Modbus/TCP ----

    private static bool Modbus(Context c, int at, int end)
    {
        var data = c.Data;
        if (end - at < 8 || U16(data, at + 2) != 0) return false; // the protocol id is always 0
        bool query = c.DestinationPort == 502;
        var infos = new List<string>();
        for (int p = at; p + 8 <= end;)
        {
            int transaction = U16(data, p), length = U16(data, p + 4), unit = data[p + 6];
            if (length < 2 || length > 254) break;
            int pduEnd = Math.Min(end, p + 6 + length);
            var mbap = c.D.Layer("mbtcp", "Modbus/TCP", p, pduEnd - p);
            mbap.AddField("mbtcp.trans_id", "Transaction Identifier", transaction, p, 2);
            mbap.AddField("mbtcp.prot_id", "Protocol Identifier", 0, p + 2, 2);
            mbap.AddField("mbtcp.len", "Length", length, p + 4, 2);
            mbap.AddField("mbtcp.unit_id", "Unit Identifier", unit, p + 6, 1);
            int f = p + 7;
            int function = data[f];
            bool exception = (function & 0x80) != 0;
            int code = function & 0x7F;
            var pdu = c.D.Layer("modbus", "Modbus", f, pduEnd - f);
            pdu.AddField("modbus.func_code", "Function Code", code, f, 1, $"{ModbusFunction(code)} ({code})");
            int v = f + 1;
            if (exception && v < pduEnd)
            {
                pdu.AddField("modbus.exception_code", "Exception Code", (int)data[v], v, 1, $"{ModbusException(data[v])} ({data[v]})");
            }
            else if (code is >= 1 and <= 4)
            {
                if (query && v + 4 <= pduEnd)
                {
                    pdu.AddField("modbus.reference_num", "Reference Number", (int)U16(data, v), v, 2);
                    pdu.AddField(code <= 2 ? "modbus.bit_cnt" : "modbus.word_cnt", code <= 2 ? "Bit Count" : "Word Count", (int)U16(data, v + 2), v + 2, 2);
                }
                else if (!query && v < pduEnd)
                {
                    int count = data[v];
                    pdu.AddField("modbus.byte_cnt", "Byte Count", count, v, 1);
                    if (code >= 3)
                        for (int r = 0; r * 2 + 2 <= count && v + 1 + r * 2 + 2 <= pduEnd; r++)
                            pdu.AddField("modbus.regval_uint16", $"Register {r} (UINT16)", (int)U16(data, v + 1 + r * 2), v + 1 + r * 2, 2);
                }
            }
            else if (code is 5 or 6 && v + 4 <= pduEnd)
            {
                pdu.AddField("modbus.reference_num", "Reference Number", (int)U16(data, v), v, 2);
                pdu.AddField(code == 5 ? "modbus.data" : "modbus.regval_uint16", code == 5 ? "Data" : "Register Value (UINT16)", (int)U16(data, v + 2), v + 2, 2,
                    code == 5 ? (U16(data, v + 2) == 0xFF00 ? "On (0xff00)" : "Off (0x0000)") : null);
            }
            else if (code is 15 or 16 && v + 4 <= pduEnd)
            {
                pdu.AddField("modbus.reference_num", "Reference Number", (int)U16(data, v), v, 2);
                pdu.AddField(code == 15 ? "modbus.bit_cnt" : "modbus.word_cnt", code == 15 ? "Bit Count" : "Word Count", (int)U16(data, v + 2), v + 2, 2);
                if (query && code == 16 && v + 5 <= pduEnd)
                    for (int r = 0; v + 5 + r * 2 + 2 <= pduEnd && r < data[v + 4] / 2; r++)
                        pdu.AddField("modbus.regval_uint16", $"Register {r} (UINT16)", (int)U16(data, v + 5 + r * 2), v + 5 + r * 2, 2);
            }
            infos.Add($"{(query ? "Query" : "Response")}: Trans: {transaction,5}; Unit: {unit,3}, Func: {code,3}: {ModbusFunction(code)}{(exception ? $". Exception returned " : "")}");
            p += 6 + length;
        }
        if (infos.Count == 0) return false;
        c.D.Protocol = "Modbus/TCP";
        c.D.Info = string.Join(", ", infos);
        return true;
    }

    private static string ModbusFunction(int code) => code switch
    {
        1 => "Read Coils",
        2 => "Read Discrete Inputs",
        3 => "Read Holding Registers",
        4 => "Read Input Registers",
        5 => "Write Single Coil",
        6 => "Write Single Register",
        8 => "Diagnostics",
        15 => "Write Multiple Coils",
        16 => "Write Multiple Registers",
        17 => "Report Slave ID",
        22 => "Mask Write Register",
        23 => "Read Write Register",
        43 => "Encapsulated Interface Transport",
        _ => "Unknown function",
    };

    private static string ModbusException(int code) => code switch
    {
        1 => "Illegal function",
        2 => "Illegal data address",
        3 => "Illegal data value",
        4 => "Slave device failure",
        5 => "Acknowledge",
        6 => "Slave device busy",
        10 => "Gateway path unavailable",
        11 => "Gateway target device failed to respond",
        _ => "Unknown",
    };

    // ---- SSH ----

    private static bool Ssh(Context c, int at, int end)
    {
        var data = c.Data;
        bool fromClient = c.DestinationPort == 22;
        string side = fromClient ? "Client" : c.SourcePort == 22 ? "Server" : "";
        var layer = c.D.Layer("ssh", "SSH Protocol", at, end - at);
        c.D.Protocol = "SSH";
        if (data.AsSpan(at, end - at).StartsWith("SSH-"u8))
        {
            int newline = Array.IndexOf(data, (byte)'\n', at, end - at);
            int length = (newline < 0 ? end : newline) - at;
            string banner = Format.Line(data.AsSpan(at, length));
            layer.AddField("ssh.protocol", "Protocol", banner, at, length);
            c.D.Info = $"{side}{(side.Length > 0 ? ": " : "")}Protocol ({banner})";
        }
        else
        {
            layer.AddField("ssh.encrypted_packet", "Encrypted Packet", Bytes(data, at, end - at), at, end - at, Format.Hex(data.AsSpan(at, end - at), 24));
            c.D.Info = $"{side}{(side.Length > 0 ? ": " : "")}Encrypted packet (len={end - at})";
        }
        return true;
    }

    // ---- text-line protocols ----

    /// <summary>FTP, SMTP, POP and IMAP: commands and replies are lines of text; the first one goes in Info.</summary>
    private static bool TextLines(Context c, int at, int end, int serverPort, string filter, string shortName, string title)
    {
        var data = c.Data;
        bool request = c.DestinationPort == serverPort;
        var layer = c.D.Layer(filter, title, at, end - at);
        c.D.Protocol = shortName;
        string? first = null;
        int lines = 0;
        for (int p = at; p < end && lines < 40; lines++)
        {
            int newline = Array.IndexOf(data, (byte)'\n', p, end - p);
            int lineEnd = newline < 0 ? end : newline + 1;
            string line = Format.Line(data.AsSpan(p, lineEnd - p));
            var node = layer.Add(new ProtoNode(line, null, null, p, lineEnd - p));
            if (first is null)
            {
                first = line;
                int space = line.IndexOf(' ');
                string head = space < 0 ? line : line[..space], rest = space < 0 ? "" : line[(space + 1)..];
                if (request)
                {
                    node.AddField($"{filter}.request.command", "Request command", head, p, head.Length);
                    if (rest.Length > 0) node.AddField($"{filter}.request.arg", "Request arg", rest, p + head.Length + 1, rest.Length);
                }
                else
                {
                    if (int.TryParse(head.TrimEnd('-'), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                        node.AddField($"{filter}.response.code", "Response code", code, p, head.Length);
                    if (rest.Length > 0) node.AddField($"{filter}.response.arg", "Response arg", rest, p + head.Length + 1, rest.Length);
                }
            }
            p = lineEnd;
        }
        // Wireshark writes SMTP and POP as "C: …" / "S: …", FTP and IMAP as "Request: …" / "Response: …".
        bool shortForm = filter is "smtp" or "pop";
        c.D.Info = $"{(shortForm ? request ? "C" : "S" : request ? "Request" : "Response")}: {first}";
        return true;
    }
}

/// <summary>What the stream tracker needs from MQTT: the protocol level a CONNECT gives, and its varint lengths.</summary>
internal static class MqttHints
{
    /// <summary>A CONNECT at the start: fixed header 0x10, a length, then "MQTT" or "MQIsdp".</summary>
    public static bool LooksLikeConnect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10 || payload[0] != 0x10) return false;
        int p = 1;
        while (p < 5 && p < payload.Length && (payload[p] & 0x80) != 0) p++;
        p++;
        if (p + 6 > payload.Length) return false;
        int nameLength = payload[p] << 8 | payload[p + 1];
        var name = payload.Slice(p + 2, Math.Min(nameLength, payload.Length - p - 2));
        return name.SequenceEqual("MQTT"u8) || name.SequenceEqual("MQIsdp"u8);
    }

    /// <summary>The protocol level (3, 4 or 5) of a CONNECT at the start of the payload; 0 when it isn't one.</summary>
    public static byte ConnectLevel(ReadOnlySpan<byte> payload)
    {
        if (!LooksLikeConnect(payload)) return 0;
        int p = 1;
        while ((payload[p] & 0x80) != 0) p++;
        p++;
        int nameLength = payload[p] << 8 | payload[p + 1];
        int levelAt = p + 2 + nameLength;
        return levelAt < payload.Length ? payload[levelAt] : (byte)0;
    }

    /// <summary>MQTT's "remaining length": 1 to 4 bytes, 7 bits each, least significant first.</summary>
    public static bool ReadVarInt(byte[] data, int at, int end, out int value, out int bytes)
    {
        value = 0;
        bytes = 0;
        int multiplier = 1;
        while (bytes < 4)
        {
            if (at + bytes >= end) return false;
            int b = data[at + bytes];
            value += (b & 0x7F) * multiplier;
            bytes++;
            if ((b & 0x80) == 0) return true;
            multiplier *= 128;
        }
        return false;
    }
}
