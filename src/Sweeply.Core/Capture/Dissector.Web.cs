using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Sweeply.Core.Capture;

public static partial class Dissector
{
    // ---- HTTP and SSDP (same message format) ----

    private static bool Http(Context c, int at, int end, string filter, string shortName, string title)
    {
        var data = c.Data;
        var layer = c.D.Layer(filter, title, at, end - at);
        c.D.Protocol = shortName;
        var lines = new List<(int Start, int Length)>();
        int p = at, headerEnd = -1;
        while (p < end && lines.Count < 100)
        {
            int newline = Array.IndexOf(data, (byte)'\n', p, end - p);
            if (newline < 0) break;
            int length = newline - p;
            if (length > 0 && data[newline - 1] == '\r') length--;
            if (length == 0) { headerEnd = newline + 1; break; }
            lines.Add((p, length));
            p = newline + 1;
        }
        string first = lines.Count > 0 ? Format.Line(data.AsSpan(lines[0].Start, lines[0].Length)) : "";
        string[] parts = first.Split(' ', 3);
        bool response = parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.Ordinal);
        bool request = !response && parts.Length == 3 && parts[2].StartsWith("HTTP/", StringComparison.Ordinal);
        if (!request && !response)
        {
            // The rest of a message that started in an earlier packet.
            layer.AddField($"{filter}.data", "Continuation", Bytes(data, at, end - at), at, end - at, $"{end - at} bytes");
            c.D.Info = "Continuation";
            return true;
        }

        var firstNode = layer.Add(new ProtoNode(first, null, null, lines[0].Start, lines[0].Length));
        if (request)
        {
            firstNode.AddField("http.request.method", "Request Method", parts[0], lines[0].Start, parts[0].Length);
            firstNode.AddField("http.request.uri", "Request URI", parts[1], lines[0].Start + parts[0].Length + 1, parts[1].Length);
            firstNode.AddField("http.request.version", "Request Version", parts[2], lines[0].Start + parts[0].Length + parts[1].Length + 2, parts[2].Length);
            layer.Add(new ProtoNode("[This is a request]", "http.request", true));
        }
        else
        {
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int code);
            firstNode.AddField("http.response.version", "Response Version", parts[0], lines[0].Start, parts[0].Length);
            firstNode.AddField("http.response.code", "Status Code", code, lines[0].Start + parts[0].Length + 1, parts[1].Length);
            if (parts.Length > 2) firstNode.AddField("http.response.phrase", "Response Phrase", parts[2], lines[0].Start + parts[0].Length + parts[1].Length + 2, parts[2].Length);
            layer.Add(new ProtoNode("[This is a response]", "http.response", true));
        }

        string? contentType = null;
        foreach (var (start, length) in lines.Skip(1))
        {
            string line = Format.Line(data.AsSpan(start, length));
            int colon = line.IndexOf(':');
            if (colon <= 0) { layer.AddText(line, start, length); continue; }
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            string? field = name.ToLowerInvariant() switch
            {
                "host" => "http.host",
                "user-agent" => "http.user_agent",
                "accept" => "http.accept",
                "content-type" => "http.content_type",
                "content-length" => "http.content_length",
                "referer" => "http.referer",
                "cookie" => "http.cookie",
                "set-cookie" => "http.set_cookie",
                "server" => "http.server",
                "location" => "http.location",
                "connection" => "http.connection",
                "authorization" => "http.authorization",
                "upgrade" => "http.upgrade",
                "transfer-encoding" => "http.transfer_encoding",
                "content-encoding" => "http.content_encoding",
                "cache-control" => "http.cache_control",
                "date" => "http.date",
                "st" => "ssdp.st",
                "nt" => "ssdp.nt",
                "usn" => "ssdp.usn",
                _ => null,
            };
            object fieldValue = value;
            if (field == "http.content_length" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long contentLength)) fieldValue = contentLength;
            if (field == "http.content_type") contentType = value;
            layer.Add(new ProtoNode(line, field, field is null ? null : fieldValue, start, length));
        }
        if (headerEnd > 0 && headerEnd < end)
            layer.AddField("http.file_data", "File Data", Bytes(data, headerEnd, end - headerEnd), headerEnd, end - headerEnd, $"{end - headerEnd} bytes");
        else if (headerEnd < 0)
            layer.AddText("[The headers continue in the next packet(s)]");

        c.D.Info = response && contentType is not null && filter == "http" ? $"{first}  ({contentType.Split(';')[0]})" : first;
        return true;
    }

    // ---- TLS ----

    private static bool Tls(Context c, int at, int end, bool continuation)
    {
        var data = c.Data;
        bool tls13 = c.Stream.TlsVersion == 0x0304;
        string streamVersion = tls13 ? "TLSv1.3" : "";
        var layer = c.D.Layer("tls", "Transport Layer Security", at, end - at);
        var infos = new List<string>();
        string? protocol = null;
        int p = at;
        while (p + 5 <= end)
        {
            int type = data[p], version = U16(data, p + 1), length = U16(data, p + 3);
            if (type is < 20 or > 24 || data[p + 1] != 3 || data[p + 2] > 4)
            {
                if (p == at && !continuation) { c.D.Layers.RemoveAt(c.D.Layers.Count - 1); c.D.Protocols.RemoveAt(c.D.Protocols.Count - 1); return false; }
                layer.AddField("tls.continuation_data", "Continuation Data", Bytes(data, p, end - p), p, end - p, $"{end - p} bytes (the rest of a record that started earlier)");
                infos.Add("Continuation Data");
                p = end;
                break;
            }
            string recordVersion = streamVersion.Length > 0 ? streamVersion : VersionName(version);
            int recordEnd = Math.Min(end, p + 5 + length);
            string content = type switch { 20 => "Change Cipher Spec", 21 => "Alert", 22 => "Handshake", 23 => "Application Data", _ => "Heartbeat" };
            // The fields first, under a holder: the record's title needs what the handshake inside says.
            var holder = new ProtoNode("");
            holder.AddField("tls.record.content_type", "Content Type", type, p, 1, $"{content} ({type})");
            holder.AddField("tls.record.version", "Version", version, p + 1, 2, $"{VersionLongName(version)} ({Format.Hex16(version)})");
            holder.AddField("tls.record.length", "Length", length, p + 3, 2);
            int body = p + 5;
            string what;
            switch (type)
            {
                case 22:
                    (what, string? helloVersion) = Handshake(c, holder, body, recordEnd, version);
                    if (helloVersion is not null && streamVersion.Length == 0) recordVersion = helloVersion;
                    break;
                case 23:
                    holder.AddField("tls.app_data", "Encrypted Application Data", Bytes(data, body, recordEnd - body), body, recordEnd - body, Format.Hex(data.AsSpan(body, recordEnd - body), 24));
                    what = "Application Data";
                    break;
                case 20:
                    what = "Change Cipher Spec";
                    break;
                case 21:
                    if (length == 2 && recordEnd - body == 2)
                    {
                        int level = data[body], description = data[body + 1];
                        holder.AddField("tls.alert_message.level", "Level", level, body, 1, level == 2 ? "Fatal (2)" : "Warning (1)");
                        holder.AddField("tls.alert_message.desc", "Description", description, body + 1, 1, $"{AlertName(description)} ({description})");
                        what = $"Alert (Level: {(level == 2 ? "Fatal" : "Warning")}, Description: {AlertName(description)})";
                    }
                    else what = "Encrypted Alert";
                    break;
                default:
                    what = "Heartbeat";
                    break;
            }
            infos.Add(what);
            var record = layer.Add(new ProtoNode($"{recordVersion} Record Layer: {content} Protocol: {what}", null, null, p, recordEnd - p));
            foreach (var child in holder.Children!) record.Add(child);
            if (p + 5 + length > end) record.AddText($"[This record continues in the next packet(s): {end - body} of {length} bytes here]");
            protocol ??= recordVersion;
            p = p + 5 + length;
        }
        c.D.Protocol = protocol ?? (streamVersion.Length > 0 ? streamVersion : "TLS");
        c.D.Info = string.Join(", ", infos.Count > 0 ? infos : new List<string> { "Continuation Data" });
        return true;
    }

    /// <summary>The handshake messages of a record; returns what to show in Info and, for TLS 1.3, its name.</summary>
    private static (string Info, string? Version) Handshake(Context c, ProtoNode record, int at, int end, int recordVersion)
    {
        var data = c.Data;
        var infos = new List<string>();
        string? version = null;
        for (int p = at; p + 4 <= end;)
        {
            int type = data[p];
            int length = data[p + 1] << 16 | U16(data, p + 2);
            string name = HandshakeName(type);
            // After the keys change (TLS 1.2's Finished, anything of TLS 1.3 after ServerHello) the type byte is
            // encrypted too: an unknown type or an impossible length gives that away.
            if (name.Length == 0 || length > 1 << 20)
            {
                record.AddText("Handshake Protocol: Encrypted Handshake Message", p, end - p);
                infos.Add("Encrypted Handshake Message");
                break;
            }
            var message = record.Add(new ProtoNode($"Handshake Protocol: {name}", null, null, p, Math.Min(end, p + 4 + length) - p));
            message.AddField("tls.handshake.type", "Handshake Type", type, p, 1, $"{name} ({type})");
            message.AddField("tls.handshake.length", "Length", length, p + 1, 3);
            int body = p + 4, bodyEnd = Math.Min(end, body + length);
            string info = name;
            if (type is 1 or 2 && bodyEnd - body >= 38)
            {
                var hello = Hello(c, message, body, bodyEnd, client: type == 1);
                if (hello.Sni is { } sni) info = $"{name} (SNI={sni})";
                // The record layer often says TLS 1.0 for compatibility; the hello itself says more.
                version = hello.Tls13 ? "TLSv1.3" : VersionName(Math.Max(U16(data, body), recordVersion));
            }
            infos.Add(info);
            if (p + 4 + length > end) { message.AddText($"[This message continues in the next packet(s)]"); break; }
            p += 4 + length;
        }
        return (string.Join(", ", infos), version);
    }

    private static (string? Sni, bool Tls13) Hello(Context c, ProtoNode message, int at, int end, bool client)
    {
        var data = c.Data;
        string? sni = null;
        bool tls13 = false;
        int version = U16(data, at);
        message.AddField("tls.handshake.version", "Version", version, at, 2, $"{VersionLongName(version)} ({Format.Hex16(version)})");
        message.AddField("tls.handshake.random", "Random", Bytes(data, at + 2, 32), at + 2, 32, Format.Hex(data.AsSpan(at + 2, 32)));
        int p = at + 34;
        int sessionLength = data[p];
        message.AddField("tls.handshake.session_id_length", "Session ID Length", sessionLength, p, 1);
        p += 1 + sessionLength;
        if (p + 2 > end) return (sni, tls13);
        if (client)
        {
            int suitesLength = U16(data, p);
            var suites = message.AddText($"Cipher Suites ({suitesLength / 2} suites)", p, 2 + suitesLength);
            for (int s = p + 2; s + 2 <= Math.Min(end, p + 2 + suitesLength); s += 2)
            {
                int suite = U16(data, s);
                suites.AddField("tls.handshake.ciphersuite", "Cipher Suite", suite, s, 2, $"{SuiteName(suite)} ({Format.Hex16(suite)})");
            }
            p += 2 + suitesLength;
            if (p >= end) return (sni, tls13);
            int compressionLength = data[p];
            p += 1 + compressionLength;
        }
        else
        {
            int suite = U16(data, p);
            message.AddField("tls.handshake.ciphersuite", "Cipher Suite", suite, p, 2, $"{SuiteName(suite)} ({Format.Hex16(suite)})");
            p += 3; // suite and compression method
        }
        if (p + 2 > end) return (sni, tls13);
        int extensionsLength = U16(data, p);
        message.AddField("tls.handshake.extensions_length", "Extensions Length", extensionsLength, p, 2);
        int extensionsEnd = Math.Min(end, p + 2 + extensionsLength);
        for (int e = p + 2; e + 4 <= extensionsEnd;)
        {
            int type = U16(data, e), length = U16(data, e + 2);
            int body = e + 4, bodyEnd = Math.Min(extensionsEnd, body + length);
            string name = ExtensionName(type);
            // server_name: list length, then name type (0 = host name), length, name.
            int nameLength = type == 0 && bodyEnd - body >= 5 ? U16(data, body + 3) : -1;
            string? serverName = nameLength >= 0 && body + 5 + nameLength <= bodyEnd ? Encoding.ASCII.GetString(data, body + 5, nameLength) : null;
            var extension = message.Add(new ProtoNode($"Extension: {name} (len={length}){(serverName is null ? "" : $" name={serverName}")}", null, null, e, bodyEnd - e));
            extension.AddField("tls.handshake.extension.type", "Type", type, e, 2, $"{name} ({type})");
            if (serverName is not null)
            {
                sni = serverName;
                extension.AddField("tls.handshake.extensions_server_name", "Server Name", serverName, body + 5, nameLength);
            }
            else if (type == 16 && bodyEnd - body >= 2)
            {
                var protocols = new List<string>();
                for (int a = body + 2; a < bodyEnd;)
                {
                    int l = data[a];
                    if (a + 1 + l > bodyEnd) break;
                    string alpn = Encoding.ASCII.GetString(data, a + 1, l);
                    extension.AddField("tls.handshake.extensions_alpn_str", "ALPN Next Protocol", alpn, a + 1, l);
                    protocols.Add(alpn);
                    a += 1 + l;
                }
            }
            else if (type == 43)
            {
                if (client && bodyEnd - body >= 1)
                {
                    for (int v = body + 1; v + 2 <= bodyEnd; v += 2)
                    {
                        int offered = U16(data, v);
                        extension.AddField("tls.handshake.extensions.supported_version", "Supported Version", offered, v, 2, $"{VersionLongName(offered)} ({Format.Hex16(offered)})");
                        if (offered == 0x0304) tls13 = true;
                    }
                }
                else if (!client && bodyEnd - body >= 2)
                {
                    int chosen = U16(data, body);
                    extension.AddField("tls.handshake.extensions.supported_version", "Supported Version", chosen, body, 2, $"{VersionLongName(chosen)} ({Format.Hex16(chosen)})");
                    if (chosen == 0x0304) tls13 = true;
                }
            }
            e = body + length;
        }
        return (sni, tls13);
    }

    private static string VersionName(int version) => version switch
    {
        0x0300 => "SSLv3",
        0x0301 => "TLSv1",
        0x0302 => "TLSv1.1",
        0x0303 => "TLSv1.2",
        0x0304 => "TLSv1.3",
        _ => "TLS",
    };

    private static string VersionLongName(int version) => version switch
    {
        0x0300 => "SSL 3.0",
        0x0301 => "TLS 1.0",
        0x0302 => "TLS 1.1",
        0x0303 => "TLS 1.2",
        0x0304 => "TLS 1.3",
        _ when (version & 0x0F0F) == 0x0A0A => "Reserved (GREASE)",
        _ => "Unknown",
    };

    private static string HandshakeName(int type) => type switch
    {
        0 => "Hello Request",
        1 => "Client Hello",
        2 => "Server Hello",
        4 => "New Session Ticket",
        5 => "End of Early Data",
        8 => "Encrypted Extensions",
        11 => "Certificate",
        12 => "Server Key Exchange",
        13 => "Certificate Request",
        14 => "Server Hello Done",
        15 => "Certificate Verify",
        16 => "Client Key Exchange",
        20 => "Finished",
        24 => "Key Update",
        _ => "",
    };

    private static string ExtensionName(int type) => type switch
    {
        0 => "server_name",
        1 => "max_fragment_length",
        5 => "status_request",
        10 => "supported_groups",
        11 => "ec_point_formats",
        13 => "signature_algorithms",
        16 => "application_layer_protocol_negotiation",
        18 => "signed_certificate_timestamp",
        21 => "padding",
        22 => "encrypt_then_mac",
        23 => "extended_master_secret",
        27 => "compress_certificate",
        35 => "session_ticket",
        41 => "pre_shared_key",
        42 => "early_data",
        43 => "supported_versions",
        45 => "psk_key_exchange_modes",
        49 => "post_handshake_auth",
        50 => "signature_algorithms_cert",
        51 => "key_share",
        17513 or 17613 => "application_settings",
        65037 => "encrypted_client_hello",
        65281 => "renegotiation_info",
        _ when (type & 0x0F0F) == 0x0A0A => "Reserved (GREASE)",
        _ => $"Unknown type {type}",
    };

    private static string SuiteName(int suite) => suite switch
    {
        0x1301 => "TLS_AES_128_GCM_SHA256",
        0x1302 => "TLS_AES_256_GCM_SHA384",
        0x1303 => "TLS_CHACHA20_POLY1305_SHA256",
        0xC02B => "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256",
        0xC02C => "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384",
        0xC02F => "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
        0xC030 => "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384",
        0xCCA8 => "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256",
        0xCCA9 => "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256",
        0xC013 => "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA",
        0xC014 => "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA",
        0x009C => "TLS_RSA_WITH_AES_128_GCM_SHA256",
        0x009D => "TLS_RSA_WITH_AES_256_GCM_SHA384",
        0x002F => "TLS_RSA_WITH_AES_128_CBC_SHA",
        0x0035 => "TLS_RSA_WITH_AES_256_CBC_SHA",
        0x00FF => "TLS_EMPTY_RENEGOTIATION_INFO_SCSV",
        _ when (suite & 0x0F0F) == 0x0A0A => "Reserved (GREASE)",
        _ => "Unknown",
    };

    private static string AlertName(int description) => description switch
    {
        0 => "Close Notify",
        10 => "Unexpected Message",
        20 => "Bad Record MAC",
        40 => "Handshake Failure",
        42 => "Bad Certificate",
        45 => "Certificate Expired",
        46 => "Certificate Unknown",
        48 => "Unknown CA",
        50 => "Decode Error",
        51 => "Decrypt Error",
        70 => "Protocol Version",
        80 => "Internal Error",
        90 => "User Canceled",
        112 => "Unrecognized Name",
        120 => "No Application Protocol",
        _ => $"Unknown ({description})",
    };

    // ---- QUIC ----

    private static bool Quic(Context c, int at, int end)
    {
        var data = c.Data;
        int first = data[at];
        var layer = c.D.Layer("quic", "QUIC IETF", at, end - at);
        c.D.Protocol = "QUIC";
        bool longHeader = (first & 0x80) != 0;
        layer.Add(new ProtoNode(Format.Bits(first, 0x80, 8, "Header Form", longHeader ? "Long Header (1)" : "Short Header (0)"), "quic.header_form", longHeader ? 1 : 0, at, 1));
        if (!longHeader)
        {
            layer.AddText("Protected Payload (the rest is encrypted)", at + 1, end - at - 1);
            c.D.Info = "Protected Payload";
            return true;
        }
        if (!Need(c, layer, at, 7, end, "QUIC long header")) return true;
        uint version = U32(data, at + 1);
        int dcidLength = data[at + 5];
        if (!Need(c, layer, at + 6, dcidLength + 1, end, "QUIC connection ID")) return true;
        var dcid = Bytes(data, at + 6, dcidLength);
        int scidAt = at + 6 + dcidLength;
        int scidLength = data[scidAt];
        if (!Need(c, layer, scidAt + 1, scidLength, end, "QUIC connection ID")) return true;
        var scid = Bytes(data, scidAt + 1, scidLength);
        string typeName;
        if (version == 0) typeName = "Version Negotiation";
        else
        {
            int type = (first >> 4) & 3;
            if (version == 0x6B3343CF) type = type switch { 1 => 0, 2 => 1, 3 => 2, _ => 3 }; // QUIC v2 numbers them differently
            typeName = type switch { 0 => "Initial", 1 => "0-RTT", 2 => "Handshake", _ => "Retry" };
            layer.AddField("quic.long.packet_type", "Packet Type", type, at, 1, typeName);
        }
        layer.AddField("quic.version", "Version", (long)version, at + 1, 4, $"{QuicVersion(version)} ({Format.Hex32(version)})");
        layer.AddField("quic.dcil", "Destination Connection ID Length", dcidLength, at + 5, 1);
        layer.AddField("quic.dcid", "Destination Connection ID", dcid, at + 6, dcidLength, Format.Hex(dcid));
        layer.AddField("quic.scil", "Source Connection ID Length", scidLength, scidAt, 1);
        layer.AddField("quic.scid", "Source Connection ID", scid, scidAt + 1, scidLength, Format.Hex(scid));
        c.D.Info = $"{typeName}, DCID={Format.Hex(dcid)}{(scidLength > 0 ? $", SCID={Format.Hex(scid)}" : "")}";
        return true;
    }

    private static string QuicVersion(uint version) => version switch
    {
        0 => "Version Negotiation",
        1 => "1",
        0x6B3343CF => "2",
        _ when (version & 0xFF000000) == 0xFF000000 => $"draft-{version & 0xFF}",
        _ => "Unknown",
    };
}

/// <summary>What the stream tracker needs from TLS: the version a ServerHello chose.</summary>
internal static class TlsHints
{
    /// <summary>0x0304 when a ServerHello at the start of <paramref name="payload"/> chose TLS 1.3, its legacy version otherwise, 0 when it isn't one.</summary>
    public static ushort ChosenVersion(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9 + 38 || payload[0] != 22 || payload[5] != 2) return 0;
        int length = payload[6] << 16 | payload[7] << 8 | payload[8];
        var hello = payload.Slice(9, Math.Min(length, payload.Length - 9));
        if (hello.Length < 38) return 0;
        ushort legacy = BinaryPrimitives.ReadUInt16BigEndian(hello);
        int p = 34;
        p += 1 + hello[p];      // session id
        p += 3;                 // cipher suite, compression
        if (p + 2 > hello.Length) return legacy;
        int end = Math.Min(hello.Length, p + 2 + BinaryPrimitives.ReadUInt16BigEndian(hello[p..]));
        for (int e = p + 2; e + 4 <= end;)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(hello[e..]), extensionLength = BinaryPrimitives.ReadUInt16BigEndian(hello[(e + 2)..]);
            if (type == 43 && extensionLength >= 2 && e + 6 <= end) return BinaryPrimitives.ReadUInt16BigEndian(hello[(e + 4)..]);
            e += 4 + extensionLength;
        }
        return legacy;
    }
}
