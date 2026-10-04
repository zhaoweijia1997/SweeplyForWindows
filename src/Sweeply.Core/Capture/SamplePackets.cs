using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Sweeply.Core.Capture;

/// <summary>
/// A short made-up capture for screenshots: a name lookup, a TLS connection with a retransmission, an MQTT
/// publish, an ARP exchange and a ping. Documentation addresses only (RFC 5737, 3849, 7042); nothing real.
/// </summary>
internal static class SamplePackets
{
    private static readonly byte[] Pc = { 0x00, 0x00, 0x5E, 0x00, 0x53, 0x2A };
    private static readonly byte[] Router = { 0x00, 0x00, 0x5E, 0x00, 0x53, 0x01 };
    private static readonly IPAddress Me = IPAddress.Parse("192.0.2.23");
    private static readonly IPAddress Gateway = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress Web = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress Broker = IPAddress.Parse("192.0.2.50");

    public static List<CapturedPacket> Build()
    {
        var start = new DateTime(2026, 10, 4, 1, 30, 0, DateTimeKind.Utc);
        var packets = new List<CapturedPacket>();
        double t = 0;
        void Add(byte[] frame, bool outbound, string? program = null, int pid = 0, double after = 0.0004)
        {
            t += after;
            packets.Add(new CapturedPacket
            {
                TimestampUtc = start.AddSeconds(t), Link = LinkType.Ethernet, Data = frame,
                Direction = outbound ? PacketDirection.Outbound : PacketDirection.Inbound,
                ProcessName = program, ProcessId = program is null ? 0 : pid,
            });
        }
        byte[] Out(IPAddress to, int protocol, byte[] payload) => Ethernet(Router, Pc, 0x0800, Ipv4(Me, to, protocol, payload, 128));
        byte[] In(IPAddress from, int protocol, byte[] payload) => Ethernet(Pc, Router, 0x0800, Ipv4(from, Me, protocol, payload, 54));

        const string browser = "chrome.exe";
        const int browserPid = 9812;
        Add(Out(Gateway, 17, Udp(51034, 53, DnsQuery(0x7c3e, "www.example.com"))), true, "svchost.exe", 1388, 0);
        Add(In(Gateway, 17, Udp(53, 51034, DnsAnswer(0x7c3e, "www.example.com", Web))), false, "svchost.exe", 1388, 0.0121);
        uint c = 2_840_112_301, s = 1_102_558_430;
        var synOptions = new byte[] { 2, 4, 0x05, 0xB4, 1, 3, 3, 8, 1, 1, 4, 2 };
        Add(Out(Web, 6, Tcp(52240, 443, c, 0, 0x02, 64240, Array.Empty<byte>(), synOptions)), true, browser, browserPid);
        Add(In(Web, 6, Tcp(443, 52240, s, c + 1, 0x12, 65535, Array.Empty<byte>(), synOptions)), false, browser, browserPid, 0.0182);
        Add(Out(Web, 6, Tcp(52240, 443, c + 1, s + 1, 0x10, 1026, Array.Empty<byte>())), true, browser, browserPid);
        var hello = ClientHello("www.example.com");
        Add(Out(Web, 6, Tcp(52240, 443, c + 1, s + 1, 0x18, 1026, hello)), true, browser, browserPid);
        Add(In(Web, 6, Tcp(443, 52240, s + 1, c + 1 + (uint)hello.Length, 0x10, 501, Array.Empty<byte>())), false, browser, browserPid, 0.0179);
        var serverData = Record(22, 1210);
        Add(In(Web, 6, Tcp(443, 52240, s + 1, c + 1 + (uint)hello.Length, 0x18, 501, serverData)), false, browser, browserPid, 0.0011);
        Add(Out(Web, 6, Tcp(52240, 443, c + 1 + (uint)hello.Length, s + 1 + (uint)serverData.Length, 0x10, 1026, Array.Empty<byte>())), true, browser, browserPid);
        var request = Record(23, 412);
        Add(Out(Web, 6, Tcp(52240, 443, c + 1 + (uint)hello.Length, s + 1 + (uint)serverData.Length, 0x18, 1026, request)), true, browser, browserPid, 0.0009);
        Add(Out(Web, 6, Tcp(52240, 443, c + 1 + (uint)hello.Length, s + 1 + (uint)serverData.Length, 0x18, 1026, request)), true, browser, browserPid, 0.2103);
        Add(In(Web, 6, Tcp(443, 52240, s + 1 + (uint)serverData.Length, c + 1 + (uint)(hello.Length + request.Length), 0x18, 501, Record(23, 1380))), false, browser, browserPid, 0.0205);

        // A sensor publishing a reading over MQTT.
        uint m = 77_510_220, b = 3_901_446_002;
        var publish = MqttPublish("home/livingroom/temperature", "{\"celsius\":21.5}");
        Add(Out(Broker, 6, Tcp(53118, 1883, m, b, 0x18, 513, publish)), true, "sensor-bridge.exe", 4410, 0.0510);
        Add(In(Broker, 6, Tcp(1883, 53118, b, m + (uint)publish.Length, 0x10, 501, Array.Empty<byte>())), false, "sensor-bridge.exe", 4410, 0.0013);

        // Who has the gateway's address, and a ping to it.
        Add(Ethernet(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, Pc, 0x0806, Arp(1, Pc, Me, new byte[6], Gateway)), true, null, 0, 0.3200);
        Add(Ethernet(Pc, Router, 0x0806, Arp(2, Router, Gateway, Pc, Me)), false, null, 0, 0.0021);
        var echo = new byte[40];
        echo[0] = 8; echo[5] = 1; echo[7] = 7;
        Add(Out(Gateway, 1, echo), true, "PING.EXE", 12204, 0.1004);
        var reply = (byte[])echo.Clone();
        reply[0] = 0;
        Add(In(Gateway, 1, reply), false, "PING.EXE", 12204, 0.0018);
        return packets;
    }

    private static byte[] Ethernet(byte[] to, byte[] from, int type, byte[] payload)
    {
        var frame = new byte[14 + payload.Length];
        to.CopyTo(frame, 0);
        from.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), (ushort)type);
        payload.CopyTo(frame, 14);
        return frame;
    }

    private static int _ipId = 0x4a10;

    private static byte[] Ipv4(IPAddress from, IPAddress to, int protocol, byte[] payload, int ttl)
    {
        var packet = new byte[20 + payload.Length];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)_ipId++);
        packet[6] = 0x40;
        packet[8] = (byte)ttl;
        packet[9] = (byte)protocol;
        from.GetAddressBytes().CopyTo(packet, 12);
        to.GetAddressBytes().CopyTo(packet, 16);
        payload.CopyTo(packet, 20);
        return packet;
    }

    private static byte[] Tcp(int from, int to, uint seq, uint ack, int flags, int window, byte[] payload, byte[]? options = null)
    {
        options ??= Array.Empty<byte>();
        int header = 20 + options.Length;
        var segment = new byte[header + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(segment, (ushort)from);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)to);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(8), ack);
        segment[12] = (byte)(header / 4 << 4);
        segment[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(14), (ushort)window);
        options.CopyTo(segment, 20);
        payload.CopyTo(segment, header);
        return segment;
    }

    private static byte[] Udp(int from, int to, byte[] payload)
    {
        var datagram = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(datagram, (ushort)from);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)to);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), (ushort)datagram.Length);
        payload.CopyTo(datagram, 8);
        return datagram;
    }

    private static byte[] Name(string name)
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

    private static byte[] DnsQuery(int id, string name) =>
        new byte[] { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }.Concat(Name(name)).Concat(new byte[] { 0, 1, 0, 1 }).ToArray();

    private static byte[] DnsAnswer(int id, string name, IPAddress address) =>
        new byte[] { (byte)(id >> 8), (byte)id, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 }.Concat(Name(name)).Concat(new byte[] { 0, 1, 0, 1 })
            .Concat(new byte[] { 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 0xB4, 0, 4 }).Concat(address.GetAddressBytes()).ToArray();

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static byte[] ClientHello(string server)
    {
        var host = Encoding.ASCII.GetBytes(server);
        var sni = U16(0).Concat(U16(host.Length + 5)).Concat(U16(host.Length + 3)).Concat(new byte[] { 0 }).Concat(U16(host.Length)).Concat(host);
        var alpn = U16(16).Concat(U16(5)).Concat(U16(3)).Concat(new byte[] { 2, (byte)'h', (byte)'2' });
        var versions = U16(43).Concat(U16(3)).Concat(new byte[] { 2, 3, 4 });
        var extensions = sni.Concat(alpn).Concat(versions).ToArray();
        var random = Enumerable.Range(0, 32).Select(i => (byte)(i * 37 + 11)).ToArray();
        var body = new byte[] { 3, 3 }.Concat(random).Concat(new byte[] { 0 }).Concat(U16(6)).Concat(new byte[] { 0x13, 1, 0x13, 2, 0x13, 3, 1, 0 })
            .Concat(U16(extensions.Length)).Concat(extensions).ToArray();
        var handshake = new byte[] { 1, 0, (byte)(body.Length >> 8), (byte)body.Length }.Concat(body).ToArray();
        return new byte[] { 22, 3, 1 }.Concat(U16(handshake.Length)).Concat(handshake).ToArray();
    }

    /// <summary>A TLS record of made-up (as if encrypted) bytes; a handshake one starts with a ServerHello choosing TLS 1.3.</summary>
    private static byte[] Record(int type, int length)
    {
        var body = Enumerable.Range(0, length).Select(i => (byte)(i * 151 + 7)).ToArray();
        if (type == 22)
        {
            var hello = new byte[] { 3, 3 }.Concat(Enumerable.Range(0, 32).Select(i => (byte)(i * 13 + 5))).Concat(new byte[] { 0, 0x13, 1, 0 })
                .Concat(U16(6)).Concat(U16(43)).Concat(U16(2)).Concat(new byte[] { 3, 4 }).ToArray();
            var message = new byte[] { 2, 0, (byte)(hello.Length >> 8), (byte)hello.Length }.Concat(hello).ToArray();
            var rest = body.Skip(message.Length + 5).ToArray();
            return new byte[] { 22, 3, 3 }.Concat(U16(message.Length)).Concat(message)
                .Concat(new byte[] { 23, 3, 3 }).Concat(U16(rest.Length)).Concat(rest).ToArray();
        }
        return new byte[] { (byte)type, 3, 3 }.Concat(U16(length)).Concat(body).ToArray();
    }

    private static byte[] MqttPublish(string topic, string message)
    {
        var body = U16(topic.Length).Concat(Encoding.ASCII.GetBytes(topic)).Concat(Encoding.ASCII.GetBytes(message)).ToArray();
        return new byte[] { 0x30, (byte)body.Length }.Concat(body).ToArray();
    }

    private static byte[] Arp(int op, byte[] senderMac, IPAddress senderIp, byte[] targetMac, IPAddress targetIp) =>
        new byte[] { 0, 1, 8, 0, 6, 4, 0, (byte)op }.Concat(senderMac).Concat(senderIp.GetAddressBytes()).Concat(targetMac).Concat(targetIp.GetAddressBytes()).ToArray();
}
