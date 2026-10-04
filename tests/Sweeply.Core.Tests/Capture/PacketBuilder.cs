using System.Buffers.Binary;
using System.Net;
using System.Text;
using Sweeply.Core.Capture;

namespace Sweeply.Core.Tests.Capture;

/// <summary>
/// Builds packets byte by byte for the decoder tests. Only documentation addresses are used
/// (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32; MACs 00-00-5E-00-53-xx).
/// </summary>
internal static class PacketBuilder
{
    public static readonly byte[] MacA = { 0x00, 0x00, 0x5E, 0x00, 0x53, 0x01 };
    public static readonly byte[] MacB = { 0x00, 0x00, 0x5E, 0x00, 0x53, 0x2A };
    public static readonly byte[] Broadcast = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    public static readonly IPAddress ClientIp = IPAddress.Parse("192.0.2.23");
    public static readonly IPAddress ServerIp = IPAddress.Parse("203.0.113.10");

    public static byte[] Ethernet(byte[] destination, byte[] source, int type, byte[] payload)
    {
        var frame = new byte[14 + payload.Length];
        destination.CopyTo(frame, 0);
        source.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), (ushort)type);
        payload.CopyTo(frame, 14);
        return frame;
    }

    public static byte[] Ipv4(IPAddress source, IPAddress destination, int protocol, byte[] payload, int ttl = 64, int id = 0x1c46, bool dontFragment = true, int fragmentOffset = 0, bool more = false)
    {
        var packet = new byte[20 + payload.Length];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)((dontFragment ? 0x4000 : 0) | (more ? 0x2000 : 0) | (fragmentOffset / 8)));
        packet[8] = (byte)ttl;
        packet[9] = (byte)protocol;
        source.GetAddressBytes().CopyTo(packet, 12);
        destination.GetAddressBytes().CopyTo(packet, 16);
        payload.CopyTo(packet, 20);
        return packet;
    }

    public static byte[] Ipv6(IPAddress source, IPAddress destination, int nextHeader, byte[] payload, int hopLimit = 64)
    {
        var packet = new byte[40 + payload.Length];
        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)payload.Length);
        packet[6] = (byte)nextHeader;
        packet[7] = (byte)hopLimit;
        source.GetAddressBytes().CopyTo(packet, 8);
        destination.GetAddressBytes().CopyTo(packet, 24);
        payload.CopyTo(packet, 40);
        return packet;
    }

    public const int Fin = 0x01, Syn = 0x02, Rst = 0x04, Psh = 0x08, Ack = 0x10;

    public static byte[] Tcp(int sourcePort, int destinationPort, uint seq, uint ack, int flags, int window, byte[]? payload = null, byte[]? options = null)
    {
        payload ??= Array.Empty<byte>();
        options ??= Array.Empty<byte>();
        int headerLength = 20 + options.Length;
        var segment = new byte[headerLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(segment, (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(8), ack);
        segment[12] = (byte)((headerLength / 4) << 4);
        segment[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(14), (ushort)window);
        options.CopyTo(segment, 20);
        payload.CopyTo(segment, headerLength);
        return segment;
    }

    /// <summary>MSS 1460, NOP, window scale 8, NOP, NOP, SACK permitted: 12 bytes, as Windows sends in a SYN.</summary>
    public static readonly byte[] SynOptions = { 2, 4, 0x05, 0xB4, 1, 3, 3, 8, 1, 1, 4, 2 };

    public static byte[] Udp(int sourcePort, int destinationPort, byte[] payload)
    {
        var datagram = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(datagram, (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), (ushort)datagram.Length);
        payload.CopyTo(datagram, 8);
        return datagram;
    }

    public static byte[] DnsName(string name)
    {
        var bytes = new List<byte>();
        foreach (string label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    public static byte[] DnsQuery(int id, string name, int type = 1)
    {
        var message = new List<byte>();
        message.AddRange(new byte[] { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 });
        message.AddRange(DnsName(name));
        message.AddRange(new byte[] { 0, (byte)type, 0, 1 });
        return message.ToArray();
    }

    /// <summary>A response with a CNAME and an A record, both pointing back at the question name with compression.</summary>
    public static byte[] DnsResponse(int id, string name, string alias, IPAddress address)
    {
        var message = new List<byte>();
        message.AddRange(new byte[] { (byte)(id >> 8), (byte)id, 0x81, 0x80, 0, 1, 0, 2, 0, 0, 0, 0 });
        message.AddRange(DnsName(name));
        message.AddRange(new byte[] { 0, 1, 0, 1 });
        var aliasName = DnsName(alias);
        message.AddRange(new byte[] { 0xC0, 12, 0, 5, 0, 1, 0, 0, 0x0E, 0x10, 0, (byte)aliasName.Length });
        int aliasAt = message.Count;
        message.AddRange(aliasName);
        message.AddRange(new byte[] { 0xC0, (byte)aliasAt, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4 });
        message.AddRange(address.GetAddressBytes());
        return message.ToArray();
    }

    /// <summary>A TLS 1.3 ClientHello record with server_name, ALPN h2, and supported_versions 1.3.</summary>
    public static byte[] ClientHello(string serverName)
    {
        var extensions = new List<byte>();
        var name = Encoding.ASCII.GetBytes(serverName);
        Extension(extensions, 0, Concat(U16((ushort)(name.Length + 3)), new byte[] { 0 }, U16((ushort)name.Length), name));
        Extension(extensions, 16, Concat(U16(3), new byte[] { 2 }, "h2"u8.ToArray()));
        Extension(extensions, 43, new byte[] { 2, 3, 4 });
        var body = Concat(new byte[] { 3, 3 }, new byte[32], new byte[] { 0 }, U16(4), new byte[] { 0x13, 0x01, 0x13, 0x02 }, new byte[] { 1, 0 },
            U16((ushort)extensions.Count), extensions.ToArray());
        var handshake = Concat(new byte[] { 1, 0, (byte)(body.Length >> 8), (byte)body.Length }, body);
        return Concat(new byte[] { 22, 3, 1 }, U16((ushort)handshake.Length), handshake);
    }

    private static void Extension(List<byte> to, int type, byte[] body)
    {
        to.AddRange(U16((ushort)type));
        to.AddRange(U16((ushort)body.Length));
        to.AddRange(body);
    }

    public static byte[] U16(ushort value) => new[] { (byte)(value >> 8), (byte)value };

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static CapturedPacket Packet(byte[] data, LinkType link = LinkType.Ethernet, double seconds = 0) => new()
    {
        TimestampUtc = new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc).AddSeconds(seconds),
        Link = link,
        Data = data,
    };

    /// <summary>A TCP packet over Ethernet and IPv4 from the client to the server (or back).</summary>
    public static CapturedPacket TcpPacket(bool fromClient, uint seq, uint ack, int flags, int window = 512, byte[]? payload = null,
        byte[]? options = null, int clientPort = 51432, int serverPort = 443, double seconds = 0)
    {
        var segment = fromClient
            ? Tcp(clientPort, serverPort, seq, ack, flags, window, payload, options)
            : Tcp(serverPort, clientPort, seq, ack, flags, window, payload, options);
        var ip = fromClient ? Ipv4(ClientIp, ServerIp, 6, segment) : Ipv4(ServerIp, ClientIp, 6, segment);
        return Packet(fromClient ? Ethernet(MacB, MacA, 0x0800, ip) : Ethernet(MacA, MacB, 0x0800, ip), seconds: seconds);
    }

    /// <summary>Decodes a list of packets in order, the way the capture page does.</summary>
    public static List<Dissection> DissectAll(IReadOnlyList<CapturedPacket> packets)
    {
        var tracker = new StreamTracker();
        var result = new List<Dissection>();
        for (int i = 0; i < packets.Count; i++)
        {
            var stream = tracker.Process(packets[i], i + 1);
            result.Add(Dissector.Dissect(packets[i], i + 1, stream, packets[0].TimestampUtc));
        }
        return result;
    }

    public static object? Field(this Dissection d, string name) => d.Fields().FirstOrDefault(n => n.Field == name)?.Value;

    public static IEnumerable<object?> FieldAll(this Dissection d, string name) => d.Fields().Where(n => n.Field == name).Select(n => n.Value);
}
