using System.Globalization;
using System.Text;

namespace Sweeply.Core.Capture;

public static partial class Dissector
{
    private static bool DnsOverTcp(Context c, int at, int end)
    {
        if (end - at < 14) return false;
        int length = U16(c.Data, at);
        if (length < 12 || at + 2 + length > end) return false;
        if (!Dns(c, at + 2, at + 2 + length, "dns", "DNS", "Domain Name System")) return false;
        c.D.Layers[^1].Children?.Insert(0, new ProtoNode($"Length: {length}", "dns.length", length, at, 2));
        return true;
    }

    /// <summary>DNS and the protocols that share its message format: mDNS, LLMNR, NetBIOS name service.</summary>
    private static bool Dns(Context c, int at, int end, string filter, string shortName, string title)
    {
        var data = c.Data;
        if (end - at < 12) return false;
        int id = U16(data, at), flags = U16(data, at + 2);
        int questions = U16(data, at + 4), answers = U16(data, at + 6), authority = U16(data, at + 8), additional = U16(data, at + 10);
        if (questions > 64 || answers > 512 || authority > 512 || additional > 512) return false; // not DNS after all
        bool response = (flags & 0x8000) != 0;
        int opcode = (flags >> 11) & 0xF, rcode = flags & 0xF;
        bool netbios = filter == "nbns";

        var layer = c.D.Layer(filter, $"{title} ({(response ? "response" : "query")})", at, end - at);
        c.D.Protocol = shortName;
        layer.AddField("dns.id", "Transaction ID", id, at, 2, Format.Hex16(id));
        string kind = OpcodeName(opcode, response);
        var flagNode = layer.AddField("dns.flags", "Flags", flags, at + 2, 2, $"{Format.Hex16(flags)} {kind}{(response && rcode != 0 ? ", " + RcodeName(rcode) : "")}");
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x8000, 16, "Response", response ? "Message is a response" : "Message is a query"), "dns.flags.response", response, at + 2, 2));
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x7800, 16, "Opcode", $"{OpcodeName(opcode, false)} ({opcode})"), "dns.flags.opcode", opcode, at + 2, 2));
        if (response) flagNode.Add(new ProtoNode(Format.Bits(flags, 0x0400, 16, "Authoritative"), "dns.flags.authoritative", (flags & 0x0400) != 0, at + 2, 2));
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x0200, 16, "Truncated"), "dns.flags.truncated", (flags & 0x0200) != 0, at + 2, 2));
        flagNode.Add(new ProtoNode(Format.Bits(flags, 0x0100, 16, "Recursion desired"), "dns.flags.recdesired", (flags & 0x0100) != 0, at + 2, 2));
        if (response) flagNode.Add(new ProtoNode(Format.Bits(flags, 0x0080, 16, "Recursion available"), "dns.flags.recavail", (flags & 0x0080) != 0, at + 2, 2));
        if (response) flagNode.Add(new ProtoNode(Format.Bits(flags, 0x000F, 16, "Reply code", $"{RcodeName(rcode)} ({rcode})"), "dns.flags.rcode", rcode, at + 2, 2));
        layer.AddField("dns.count.queries", "Questions", questions, at + 4, 2);
        layer.AddField("dns.count.answers", "Answer RRs", answers, at + 6, 2);
        layer.AddField("dns.count.auth_rr", "Authority RRs", authority, at + 8, 2);
        layer.AddField("dns.count.add_rr", "Additional RRs", additional, at + 10, 2);

        var info = new StringBuilder($"{kind} {Format.Hex16(id)}");
        if (response && rcode != 0) info.Append(' ').Append(RcodeName(rcode));
        int offset = at + 12;
        if (questions > 0)
        {
            var queries = layer.AddText("Queries", offset, 0);
            for (int i = 0; i < questions; i++)
            {
                int start = offset;
                if (!ReadName(data, at, end, ref offset, netbios, out string name) || offset + 4 > end) { Malformed(c, queries, "query"); c.D.Info = info.ToString(); return true; }
                int type = U16(data, offset), cls = U16(data, offset + 2);
                bool unicastResponse = filter == "mdns" && (cls & 0x8000) != 0;
                var q = queries.Add(new ProtoNode($"{name}: type {TypeName(type)}, class {ClassName(cls & 0x7FFF)}{(unicastResponse ? ", \"QU\" question" : "")}", null, null, start, offset + 4 - start));
                q.AddField("dns.qry.name", "Name", name, start, offset - start);
                q.Add(new ProtoNode($"[Name Length: {name.Length}]", "dns.qry.name.len", name.Length));
                q.AddField("dns.qry.type", "Type", type, offset, 2, $"{TypeName(type)} ({type})");
                q.AddField("dns.qry.class", "Class", cls & 0x7FFF, offset + 2, 2, $"{ClassName(cls & 0x7FFF)} ({Format.Hex16(cls & 0x7FFF)})");
                if (netbios) q.AddField("nbns.name", "NetBIOS name", name, start, offset - start);
                offset += 4;
                info.Append(' ').Append(TypeName(type)).Append(' ').Append(name);
            }
        }
        foreach (var (count, heading) in new[] { (answers, "Answers"), (authority, "Authoritative nameservers"), (additional, "Additional records") })
        {
            if (count == 0) continue;
            var section = layer.AddText(heading, offset, 0);
            for (int i = 0; i < count; i++)
            {
                string? summary = Record(c, section, at, end, ref offset, netbios, filter == "mdns");
                if (summary is null) { c.D.Info = info.ToString(); return true; }
                if (summary.Length > 0 && (heading == "Answers" || (heading == "Authoritative nameservers" && response && rcode != 0)))
                    info.Append(' ').Append(summary);
            }
        }
        c.D.Info = info.ToString();
        return true;
    }

    /// <summary>One resource record; returns its short form for the Info column ("A 192.0.2.1"), or null when broken.</summary>
    private static string? Record(Context c, ProtoNode section, int message, int end, ref int offset, bool netbios, bool mdns)
    {
        var data = c.Data;
        int start = offset;
        if (!ReadName(data, message, end, ref offset, netbios, out string name) || offset + 10 > end) { Malformed(c, section, "resource record"); return null; }
        int type = U16(data, offset), cls = U16(data, offset + 2);
        uint ttl = U32(data, offset + 4);
        int length = U16(data, offset + 8);
        int rdata = offset + 10;
        if (rdata + length > end) { Malformed(c, section, "resource record data"); return null; }
        string shortForm = TypeName(type);
        string detail = "";
        var record = new ProtoNode("", null, null, start, rdata + length - start);
        var children = new List<ProtoNode>
        {
            new($"Name: {name}", "dns.resp.name", name, start, offset - start),
            new($"Type: {TypeName(type)} ({type})", "dns.resp.type", type, offset, 2),
        };
        if (type == 41)
            children.Add(new ProtoNode($"UDP payload size: {cls}", "dns.rr.udp_payload_size", cls, offset + 2, 2));
        else
        {
            children.Add(new ProtoNode($"Class: {ClassName(cls & 0x7FFF)} ({Format.Hex16(cls & 0x7FFF)})", "dns.resp.class", cls & 0x7FFF, offset + 2, 2));
            if (mdns && (cls & 0x8000) != 0) children.Add(new ProtoNode("Cache flush: True", "mdns.cache_flush", true, offset + 2, 2));
            children.Add(new ProtoNode($"Time to live: {ttl} ({Ttl(ttl)})", "dns.resp.ttl", (long)ttl, offset + 4, 4));
        }
        children.Add(new ProtoNode($"Data length: {length}", "dns.resp.len", length, offset + 8, 2));

        int r = rdata;
        switch (type)
        {
            case 1 when length == 4:
                var a = Ip4(data, r);
                children.Add(new ProtoNode($"Address: {a}", "dns.a", a, r, 4));
                detail = $"addr {a}";
                shortForm = $"A {a}";
                break;
            case 28 when length == 16:
                var aaaa = Ip6(data, r);
                children.Add(new ProtoNode($"AAAA Address: {aaaa}", "dns.aaaa", aaaa, r, 16));
                detail = $"addr {aaaa}";
                shortForm = $"AAAA {aaaa}";
                break;
            case 5 or 12 or 2 or 39:
            {
                int p = r;
                if (!ReadName(data, message, end, ref p, false, out string target)) break;
                (string field, string label, string tag) = type switch
                {
                    5 => ("dns.cname", "CNAME", "cname"),
                    12 => ("dns.ptr.domain_name", "Domain Name", "domain name"),
                    2 => ("dns.ns", "Name Server", "ns"),
                    _ => ("dns.dname", "DNAME", "dname"),
                };
                children.Add(new ProtoNode($"{label}: {target}", field, target, r, length));
                detail = $"{tag} {target}";
                shortForm = $"{TypeName(type)} {target}";
                break;
            }
            case 15 when length >= 3:
            {
                int preference = U16(data, r), p = r + 2;
                if (!ReadName(data, message, end, ref p, false, out string exchange)) break;
                children.Add(new ProtoNode($"Preference: {preference}", "dns.mx.preference", preference, r, 2));
                children.Add(new ProtoNode($"Mail Exchange: {exchange}", "dns.mx.mail_exchange", exchange, r + 2, length - 2));
                detail = $"preference {preference}, mx {exchange}";
                shortForm = $"MX {preference} {exchange}";
                break;
            }
            case 16:
            {
                var texts = new List<string>();
                for (int p = r; p < r + length;)
                {
                    int l = data[p];
                    if (p + 1 + l > r + length) break;
                    string text = Format.Line(data.AsSpan(p + 1, l));
                    children.Add(new ProtoNode($"TXT: {text}", "dns.txt", text, p + 1, l));
                    texts.Add(text);
                    p += 1 + l;
                }
                detail = string.Join(" ", texts);
                shortForm = "TXT";
                break;
            }
            case 33 when length >= 7:
            {
                int priority = U16(data, r), weight = U16(data, r + 2), port = U16(data, r + 4), p = r + 6;
                if (!ReadName(data, message, end, ref p, false, out string target)) break;
                children.Add(new ProtoNode($"Priority: {priority}", "dns.srv.priority", priority, r, 2));
                children.Add(new ProtoNode($"Weight: {weight}", "dns.srv.weight", weight, r + 2, 2));
                children.Add(new ProtoNode($"Port: {port}", "dns.srv.port", port, r + 4, 2));
                children.Add(new ProtoNode($"Target: {target}", "dns.srv.target", target, r + 6, length - 6));
                detail = $"priority {priority}, weight {weight}, port {port}, target {target}";
                shortForm = $"SRV {priority} {weight} {port} {target}";
                break;
            }
            case 6:
            {
                int p = r;
                if (!ReadName(data, message, end, ref p, false, out string primary) || !ReadName(data, message, end, ref p, false, out string mailbox) || p + 20 > r + length) break;
                children.Add(new ProtoNode($"Primary name server: {primary}", "dns.soa.mname", primary, r, 0));
                children.Add(new ProtoNode($"Responsible authority's mailbox: {mailbox}", "dns.soa.rname", mailbox, r, 0));
                children.Add(new ProtoNode($"Serial Number: {U32(data, p)}", "dns.soa.serial_number", (long)U32(data, p), p, 4));
                detail = $"mname {primary}";
                shortForm = $"SOA {primary}";
                break;
            }
            case 32 when netbios && length >= 6:
            {
                var address = Ip4(data, r + 2);
                children.Add(new ProtoNode($"Addr: {address}", "nbns.addr", address, r + 2, 4));
                detail = $"addr {address}";
                shortForm = $"NB {address}";
                break;
            }
            default:
                if (length > 0) children.Add(new ProtoNode($"Data: {Format.Hex(data.AsSpan(r, length), 32)}", "dns.data", Bytes(data, r, length), r, length));
                break;
        }
        record = new ProtoNode($"{(type == 41 ? "<Root>" : name)}: type {TypeName(type)}{(type == 41 ? "" : $", class {ClassName(cls & 0x7FFF)}")}{(detail.Length > 0 ? ", " + detail : "")}",
            null, null, start, rdata + length - start);
        foreach (var child in children) record.Add(child);
        section.Add(record);
        offset = rdata + length;
        return type == 41 ? "" : shortForm; // the EDNS record says nothing worth a place in Info
    }

    /// <summary>A DNS name with compression pointers (and, for NetBIOS, the 32-letter encoded first label).</summary>
    private static bool ReadName(byte[] data, int message, int end, ref int offset, bool netbios, out string name)
    {
        var labels = new List<string>();
        int p = offset, jumps = 0, total = 0;
        int after = -1;
        name = "";
        while (true)
        {
            if (p >= end) return false;
            int length = data[p];
            if (length == 0) { p++; break; }
            if ((length & 0xC0) == 0xC0)
            {
                if (p + 1 >= end || ++jumps > 20) return false;
                int target = message + (((length & 0x3F) << 8) | data[p + 1]);
                if (after < 0) after = p + 2;
                if (target >= end || target < message) return false;
                p = target;
                continue;
            }
            if ((length & 0xC0) != 0 || p + 1 + length > end) return false;
            total += length + 1;
            if (total > 255) return false;
            var label = data.AsSpan(p + 1, length);
            labels.Add(netbios && labels.Count == 0 && length == 32 ? NetBiosName(label) : Encoding.UTF8.GetString(label));
            p += 1 + length;
        }
        offset = after >= 0 ? after : p;
        name = labels.Count == 0 ? "<Root>" : string.Join('.', labels);
        return true;
    }

    /// <summary>NetBIOS's first-level encoding: each byte as two letters 'A'+high nibble, 'A'+low; the 16th byte is the type, shown as &lt;20&gt;.</summary>
    private static string NetBiosName(ReadOnlySpan<byte> encoded)
    {
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = (byte)(((encoded[2 * i] - 'A') << 4) | ((encoded[2 * i + 1] - 'A') & 0xF));
        string text = Encoding.ASCII.GetString(bytes, 0, 15).TrimEnd(' ');
        if (text == "*" || bytes[0] == '*') text = "*";
        return $"{text}<{bytes[15]:x2}>";
    }

    private static string Ttl(uint seconds)
    {
        if (seconds < 60) return seconds == 1 ? "1 second" : $"{seconds} seconds";
        var span = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        if (span.Days > 0) parts.Add(span.Days == 1 ? "1 day" : $"{span.Days} days");
        if (span.Hours > 0) parts.Add(span.Hours == 1 ? "1 hour" : $"{span.Hours} hours");
        if (span.Minutes > 0) parts.Add(span.Minutes == 1 ? "1 minute" : $"{span.Minutes} minutes");
        if (span.Seconds > 0) parts.Add(span.Seconds == 1 ? "1 second" : $"{span.Seconds} seconds");
        return string.Join(", ", parts);
    }

    private static string OpcodeName(int opcode, bool response) => opcode switch
    {
        0 => response ? "Standard query response" : "Standard query",
        1 => response ? "Inverse query response" : "Inverse query",
        2 => response ? "Server status response" : "Server status request",
        4 => response ? "Zone change notification response" : "Zone change notification",
        5 => response ? "Dynamic update response" : "Dynamic update",
        _ => $"Unknown operation ({opcode})",
    };

    private static string RcodeName(int rcode) => rcode switch
    {
        0 => "No error",
        1 => "Format error",
        2 => "Server failure",
        3 => "No such name",
        4 => "Not implemented",
        5 => "Refused",
        _ => $"Error {rcode}",
    };

    internal static string TypeName(int type) => type switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        13 => "HINFO",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        29 => "LOC",
        32 => "NB",
        35 => "NAPTR",
        44 => "SSHFP",
        52 => "TLSA",
        99 => "SPF",
        257 => "CAA",
        251 => "IXFR",
        252 => "AXFR",
        33 => "SRV",
        39 => "DNAME",
        41 => "OPT",
        43 => "DS",
        46 => "RRSIG",
        47 => "NSEC",
        48 => "DNSKEY",
        64 => "SVCB",
        65 => "HTTPS",
        255 => "ANY",
        _ => $"Unknown ({type})",
    };

    private static string ClassName(int cls) => cls switch { 1 => "IN", 3 => "CH", 4 => "HS", 255 => "ANY", _ => $"Unknown ({cls})" };
}
