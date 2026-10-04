using System.Globalization;
using System.Net;
using System.Text;

namespace Sweeply.Core.Capture;

/// <summary>
/// One line of a decoded packet, as Wireshark's packet details show it: a protocol layer, a group of fields or
/// a field. A field has a filter name (e.g. "tcp.srcport") and a typed value, so the display filter can test it,
/// and the bytes it came from, so the hex view can highlight them.
/// </summary>
public sealed class ProtoNode
{
    public ProtoNode(string text, string? field = null, object? value = null, int offset = -1, int length = 0)
    {
        Text = text;
        Field = field;
        Value = value;
        Offset = offset;
        Length = length;
    }

    public string Text { get; }
    public string? Field { get; }
    public object? Value { get; }
    public int Offset { get; }
    public int Length { get; }
    public List<ProtoNode>? Children { get; private set; }

    /// <summary>The top line of a protocol, e.g. "Transmission Control Protocol, Src Port: 51432, Dst Port: 443".</summary>
    public bool IsLayer { get; init; }

    /// <summary>Something worth a look: a retransmission, a malformed packet, a reset.</summary>
    public bool IsWarning { get; init; }

    public ProtoNode Add(ProtoNode child)
    {
        (Children ??= new List<ProtoNode>()).Add(child);
        return child;
    }

    /// <summary>A field shown as "Label: value" (or "Label: <paramref name="shown"/>").</summary>
    public ProtoNode AddField(string field, string label, object? value, int offset, int length, string? shown = null) =>
        Add(new ProtoNode($"{label}: {shown ?? Format.Value(value)}", field, value, offset, length));

    /// <summary>A line without a filter name (a heading for a group of fields, or a note).</summary>
    public ProtoNode AddText(string text, int offset = -1, int length = 0, bool warning = false) =>
        Add(new ProtoNode(text, null, null, offset, length) { IsWarning = warning });

    /// <summary>A note Wireshark would show in brackets, with a field so that it can be filtered on.</summary>
    public ProtoNode AddFlag(string field, string text, bool warning = true) =>
        Add(new ProtoNode(text, field, true) { IsWarning = warning });

    /// <summary>This node and everything under it.</summary>
    public IEnumerable<ProtoNode> Walk()
    {
        yield return this;
        if (Children is null) yield break;
        foreach (var child in Children)
            foreach (var node in child.Walk())
                yield return node;
    }

    public override string ToString() => Text;
}

/// <summary>Background colour classes for the packet list, after Wireshark's default colouring rules.</summary>
public enum PacketColor
{
    Default,
    Tcp,
    TcpSynFin,
    TcpReset,
    BadTcp,
    Udp,
    Arp,
    Icmp,
    IcmpError,
    Http,
    Broadcast,
}

/// <summary>A decoded packet: the layers for the details view, and the columns of the packet list.</summary>
public sealed class Dissection
{
    public List<ProtoNode> Layers { get; } = new();

    /// <summary>The protocols from the bottom up, by their filter names: "frame", "eth", "ip", "tcp", "tls".</summary>
    public List<string> Protocols { get; } = new();

    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";

    /// <summary>The highest protocol, as the Protocol column shows it: "TCP", "TLSv1.3", "DNS".</summary>
    public string Protocol { get; set; } = "";
    public string Info { get; set; } = "";
    public PacketColor Color { get; set; }

    public ProtoNode Layer(string protocol, string title, int offset, int length)
    {
        var node = new ProtoNode(title, protocol, true, offset, length) { IsLayer = true };
        Layers.Add(node);
        Protocols.Add(protocol);
        return node;
    }

    /// <summary>Every field with a filter name, the protocol layers included (value true).</summary>
    public IEnumerable<ProtoNode> Fields() => Layers.SelectMany(l => l.Walk()).Where(n => n.Field is not null);
}

/// <summary>How values are written in the details, as Wireshark writes them.</summary>
public static class Format
{
    public static string Value(object? value) => value switch
    {
        null => "",
        bool b => b ? "Set" : "Not set",
        byte[] bytes when bytes.Length == 6 => Mac(bytes),
        byte[] bytes => Hex(bytes),
        IPAddress address => address.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>"00:00:5e:00:53:01", as Wireshark writes hardware addresses.</summary>
    public static string Mac(ReadOnlySpan<byte> mac)
    {
        var text = new StringBuilder(17);
        for (int i = 0; i < mac.Length; i++)
        {
            if (i > 0) text.Append(':');
            text.Append(mac[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    /// <summary>"0a1b2c…", at most <paramref name="max"/> bytes.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes, int max = 64)
    {
        var text = new StringBuilder(Math.Min(bytes.Length, max) * 2 + 1);
        for (int i = 0; i < bytes.Length && i < max; i++) text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        if (bytes.Length > max) text.Append('…');
        return text.ToString();
    }

    public static string Hex16(int value) => "0x" + value.ToString("x4", CultureInfo.InvariantCulture);
    public static string Hex8(int value) => "0x" + value.ToString("x2", CultureInfo.InvariantCulture);
    public static string Hex32(uint value) => "0x" + value.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>Printable ASCII as is, anything else as a dot (for a preview of a payload).</summary>
    public static string Printable(ReadOnlySpan<byte> bytes, int max = 60)
    {
        var text = new StringBuilder(Math.Min(bytes.Length, max) + 1);
        for (int i = 0; i < bytes.Length && i < max; i++) text.Append(bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '.');
        if (bytes.Length > max) text.Append('…');
        return text.ToString();
    }

    /// <summary>A line of text: UTF-8 when it is, Latin-1 otherwise, without the line break.</summary>
    public static string Line(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\r', '\n'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n'); }
    }

    /// <summary>A bit field drawn as Wireshark draws it: ".... ..1. = Syn: Set".</summary>
    public static string Bits(long value, long mask, int width, string label, string? shown = null)
    {
        var text = new StringBuilder(width + width / 4 + label.Length + 12);
        for (int bit = width - 1; bit >= 0; bit--)
        {
            long m = 1L << bit;
            text.Append((mask & m) == 0 ? '.' : (value & m) != 0 ? '1' : '0');
            if (bit % 4 == 0 && bit > 0) text.Append(' ');
        }
        text.Append(" = ").Append(label).Append(": ");
        text.Append(shown ?? ((value & mask) != 0 ? "Set" : "Not set"));
        return text.ToString();
    }

    public static string Seconds(double seconds) => seconds.ToString("0.000000", CultureInfo.InvariantCulture);
}
