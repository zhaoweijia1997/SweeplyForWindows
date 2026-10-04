using System.IO.Pipes;
using System.Net;
using Sweeply.Core.Capture;
using Sweeply.Core.Monitoring;
using static Sweeply.Core.Tests.Capture.PacketBuilder;

namespace Sweeply.Core.Tests.Capture;

public class HelperProtocolTests
{
    [Fact]
    public void A_packet_message_round_trips()
    {
        var packet = new CapturedPacket
        {
            TimestampUtc = new DateTime(2026, 10, 4, 1, 2, 3, DateTimeKind.Utc).AddTicks(7), Link = LinkType.Raw,
            Data = new byte[] { 0x45, 0, 0, 20 }, Direction = PacketDirection.Outbound, InterfaceId = 3, OriginalLength = 1500,
        };
        var message = HelperProtocol.EncodePacket(packet);
        Assert.Equal((byte)HelperMessage.Packet, message[0]);
        Assert.Equal(message.Length - 5, BitConverter.ToInt32(message, 1));
        var back = HelperProtocol.DecodePacket(message.AsSpan(5));
        Assert.Equal(packet.TimestampUtc, back.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, back.TimestampUtc.Kind);
        Assert.Equal((LinkType.Raw, PacketDirection.Outbound, 3, 1500), (back.Link, back.Direction, back.InterfaceId, back.Length));
        Assert.Equal(packet.Data, back.Data);
    }

    [Fact]
    public void Broken_packet_messages_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => HelperProtocol.DecodePacket(new byte[10]));
        var bad = new byte[17];
        BitConverter.GetBytes(long.MaxValue).CopyTo(bad, 0); // a time past year 9999
        Assert.Throws<InvalidDataException>(() => HelperProtocol.DecodePacket(bad));
    }

    [Fact]
    public void Requests_and_answers_survive_json()
    {
        var request = new HelperRequest { Backend = HelperRequest.NpcapBackend, Adapters = { "{A}", "{B}" } };
        var back = HelperProtocol.DecodeJson<HelperRequest>(HelperProtocol.EncodeJson(HelperMessage.Start, request).AsSpan(5))!;
        Assert.Equal((HelperRequest.CaptureMode, "Npcap", "{A},{B}", true), (back.Mode, back.Backend, string.Join(",", back.Adapters), back.RealTime));

        var hello = new HelperHello("RawSocket", new() { new CaptureInterface(LinkType.Raw, "WLAN", "Wi-Fi card") });
        var helloBack = HelperProtocol.DecodeJson<HelperHello>(HelperProtocol.EncodeJson(HelperMessage.Hello, hello).AsSpan(5))!;
        Assert.Equal(hello.Interfaces, helloBack.Interfaces);

        var (received, dropped) = HelperProtocol.DecodeStats(HelperProtocol.EncodeStats(123_456_789_012, 7).AsSpan(5));
        Assert.Equal((123_456_789_012L, 7L), (received, dropped));
    }

    [Fact]
    public async Task Messages_are_read_whole_however_the_bytes_arrive()
    {
        var bytes = HelperProtocol.EncodeStats(1, 2).Concat(HelperProtocol.Encode(HelperMessage.Stop, ReadOnlySpan<byte>.Empty)).ToArray();
        var stream = new Trickle(bytes);
        var first = await HelperProtocol.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(HelperMessage.Stats, first!.Value.Type);
        Assert.Equal(16, first.Value.Payload.Length);
        var second = await HelperProtocol.ReadAsync(stream, CancellationToken.None);
        Assert.Equal((HelperMessage.Stop, 0), (second!.Value.Type, second.Value.Payload.Length));
        Assert.Null(await HelperProtocol.ReadAsync(stream, CancellationToken.None));

        // Cut off in the middle of a message: the end, not a garbled message.
        Assert.Null(await HelperProtocol.ReadAsync(new Trickle(bytes[..10]), CancellationToken.None));

        var huge = new byte[5];
        huge[0] = (byte)HelperMessage.Packet;
        BitConverter.GetBytes(HelperProtocol.MaxPayload + 1).CopyTo(huge, 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperProtocol.ReadAsync(new MemoryStream(huge), CancellationToken.None));
    }

    /// <summary>Hands out one byte per read, as a pipe may.</summary>
    private sealed class Trickle(byte[] data) : Stream
    {
        private int _at;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= data.Length || count == 0) return 0;
            buffer[offset] = data[_at++];
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class HelperPipeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>The app's end (server) and the helper's end (client), connected, as the real two processes use them.</summary>
    private static (NamedPipeServerStream App, NamedPipeClientStream Helper) Pipes()
    {
        string name = "SweeplyForWindows-test-" + Guid.NewGuid().ToString("N");
        var app = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var helper = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); // as the helper connects
        var accepted = app.WaitForConnectionAsync();
        helper.Connect(5000);
        accepted.Wait(5000);
        return (app, helper);
    }

    /// <summary>Runs the helper's loop the way the helper process does: the pipe closes when it is done.</summary>
    private static Task RunHelper(NamedPipeClientStream pipe, Func<HelperRequest, IPacketSource> source) => Task.Run(async () =>
    {
        await HelperHost.RunAsync(pipe, source, CancellationToken.None);
        pipe.Dispose();
    });

    private sealed class FakeSource(Action<Action<CapturedPacket>, Action<HelperError>> start) : IPacketSource
    {
        public string Backend => "Fake";
        public IReadOnlyList<CaptureInterface> Interfaces { get; } = new[] { new CaptureInterface(LinkType.Raw, "test") };
        public long Received { get; set; }
        public long Dropped => 0;
        public bool Disposed { get; private set; }
        public void Start(Action<CapturedPacket> packet, Action<HelperError> failed) => start(packet, failed);
        public void Dispose() => Disposed = true;
    }

    private static CapturedPacket Small(int i) => new()
    {
        TimestampUtc = DateTime.UtcNow, Link = LinkType.Raw, Data = Ipv4(ClientIp, ServerIp, 17, Udp(50000, 53, new[] { (byte)i })),
    };

    private static async Task<List<CapturedPacket>> Collect(CaptureConnection connection, int count)
    {
        var got = new List<CapturedPacket>();
        var deadline = DateTime.UtcNow + Wait;
        while (got.Count < count && DateTime.UtcNow < deadline)
        {
            while (connection.Packets.TryDequeue(out var p)) got.Add(p);
            if (got.Count < count) await Task.Delay(10);
        }
        return got;
    }

    [Fact]
    public async Task A_replay_comes_through_the_pipe_whole_and_in_order()
    {
        string file = Path.Combine(Path.GetTempPath(), $"sweeply-test-{Guid.NewGuid():N}.pcapng");
        try
        {
            var packets = Enumerable.Range(0, 500).Select(i => TcpPacket(i % 2 == 0, (uint)(1000 + i), 1, Ack, payload: new byte[i % 50], seconds: i * 0.001)).ToList();
            using (var stream = File.Create(file))
            using (var writer = new PcapNgWriter(stream, "test"))
            {
                int id = writer.Interface(LinkType.Ethernet, "Ethernet");
                foreach (var p in packets) writer.Write(p, id);
            }
            var (app, helper) = Pipes();
            using var _ = app;
            var host = RunHelper(helper, r => new ReplaySource(r.File!, r.RealTime));
            using var connection = new CaptureConnection(app);
            Assert.True(await connection.StartAsync(new HelperRequest { Backend = HelperRequest.ReplayBackend, File = file, RealTime = false }, Wait, CancellationToken.None));
            Assert.Equal("Replay", connection.Hello!.Backend);
            Assert.Equal("Ethernet", connection.Hello.Interfaces.Single().Name);

            var got = await Collect(connection, packets.Count);
            Assert.Equal(packets.Select(p => p.Data), got.Select(p => p.Data));
            await connection.StopAsync(Wait);
            await host.WaitAsync(Wait);
            Assert.Equal(500, connection.Received);
            Assert.Null(connection.Error);
            Assert.True(connection.Completion.IsCompleted);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_helper_that_cannot_start_says_why()
    {
        var (app, helper) = Pipes();
        using var _ = app;
        var host = RunHelper(helper, _ => throw new HelperException("NoAdapter"));
        using var connection = new CaptureConnection(app);
        Assert.False(await connection.StartAsync(new HelperRequest(), Wait, CancellationToken.None));
        Assert.Equal("NoAdapter", connection.Error!.Code);
        await host.WaitAsync(Wait);

        var (app2, helper2) = Pipes();
        using var __ = app2;
        var source = new FakeSource((_, failed) => failed(new HelperError("NeedAdmin", "access denied")));
        var host2 = RunHelper(helper2, _ => source);
        using var connection2 = new CaptureConnection(app2);
        Assert.False(await connection2.StartAsync(new HelperRequest(), Wait, CancellationToken.None));
        Assert.Equal(("NeedAdmin", "access denied"), (connection2.Error!.Code, connection2.Error.Detail));
        await host2.WaitAsync(Wait);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task A_source_that_fails_later_ends_with_its_reason_after_its_packets()
    {
        var (app, helper) = Pipes();
        using var _ = app;
        var source = new FakeSource((packet, failed) => new Thread(() =>
        {
            for (int i = 0; i < 3; i++) packet(Small(i));
            Thread.Sleep(300); // after Hello has gone out
            failed(new HelperError("SocketFailed", "the adapter went away"));
        }).Start());
        var host = RunHelper(helper, _ => source);
        using var connection = new CaptureConnection(app);
        Assert.True(await connection.StartAsync(new HelperRequest(), Wait, CancellationToken.None));
        await connection.Completion.WaitAsync(Wait);
        Assert.Equal(3, connection.Packets.Count);
        Assert.Equal("SocketFailed", connection.Error!.Code);
        await host.WaitAsync(Wait);
    }

    [Fact]
    public async Task The_helper_ends_when_the_app_goes_away()
    {
        var (app, helper) = Pipes();
        var running = true;
        var source = new FakeSource((packet, _) => new Thread(() =>
        {
            for (int i = 0; Volatile.Read(ref running); i++)
            {
                packet(Small(i));
                Thread.Sleep(5);
            }
        }) { IsBackground = true }.Start());
        var host = RunHelper(helper, _ => source);
        var connection = new CaptureConnection(app);
        Assert.True(await connection.StartAsync(new HelperRequest(), Wait, CancellationToken.None));
        await Collect(connection, 5);
        connection.Dispose(); // the app closes, or crashes
        await host.WaitAsync(Wait);
        Volatile.Write(ref running, false);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void Each_end_can_tell_which_program_is_at_the_other()
    {
        var (app, helper) = Pipes();
        using var _ = app;
        using var __ = helper;
        Assert.Equal(Environment.ProcessId, HelperIdentity.ServerProcessId(helper.SafePipeHandle));
        Assert.Equal(Environment.ProcessId, HelperIdentity.ClientProcessId(app.SafePipeHandle));
        Assert.True(HelperIdentity.IsSameProgram(Environment.ProcessId, Environment.ProcessPath));
        Assert.False(HelperIdentity.IsSameProgram(Environment.ProcessId, Path.Combine(Environment.SystemDirectory, "notepad.exe")));
        Assert.False(HelperIdentity.IsSameProgram(0, Environment.ProcessPath));
    }
}

public class ProgramResolverTests
{
    private static readonly IPAddress Own = ClientIp, Remote = ServerIp;
    private static readonly IPAddress OwnV6 = IPAddress.Parse("2001:db8::23"), RemoteV6 = IPAddress.Parse("2001:db8:ffff::10");

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc);
    }

    private static CapturedPacket Raw(byte[] ip, PacketDirection direction) => new() { TimestampUtc = DateTime.UtcNow, Link = LinkType.Raw, Data = ip, Direction = direction };

    private static CapturedPacket TcpOut(int localPort, int flags = Ack) => Raw(Ipv4(Own, Remote, 6, Tcp(localPort, 443, 1, 1, flags, 1024)), PacketDirection.Outbound);

    private static Connection TcpRow(int localPort, int processId) => new(TransportProtocol.Tcp, Own, localPort, Remote, 443, TcpConnectionState.Established, processId);

    private static ProgramResolver Resolver(Func<IReadOnlyList<Connection>> table, Clock clock) =>
        new(new[] { Own, OwnV6 }, table, id => id switch { 100 => "chrome.exe", 200 => "svchost.exe", 300 => "game.exe", _ => "" }, () => clock.Now);

    [Fact]
    public void Both_directions_of_a_connection_get_its_program()
    {
        var table = new List<Connection> { TcpRow(50000, 100) };
        var resolver = Resolver(() => table, new Clock());
        var request = TcpOut(50000);
        var reply = Raw(Ipv4(Remote, Own, 6, Tcp(443, 50000, 1, 1, Ack, 1024)), PacketDirection.Inbound);
        resolver.Resolve(request);
        resolver.Resolve(reply);
        Assert.Equal((100, "chrome.exe"), (request.ProcessId, request.ProcessName));
        Assert.Equal((100, "chrome.exe"), (reply.ProcessId, reply.ProcessName));
    }

    [Fact]
    public void Sockets_bound_to_every_address_match_by_port()
    {
        var table = new List<Connection>
        {
            new(TransportProtocol.Udp, IPAddress.Any, 53000, null, 0, null, 200),
            new(TransportProtocol.Udp, IPAddress.IPv6Any, 5353, null, 0, null, 100),
        };
        var resolver = Resolver(() => table, new Clock());
        var v4 = Raw(Ipv4(Remote, Own, 17, Udp(53, 53000, new byte[4])), PacketDirection.Inbound);
        var v6 = Raw(Ipv6(RemoteV6, OwnV6, 17, Udp(5353, 5353, new byte[4])), PacketDirection.Inbound);
        resolver.Resolve(v4);
        resolver.Resolve(v6);
        Assert.Equal(200, v4.ProcessId);
        Assert.Equal(100, v6.ProcessId);
    }

    [Fact]
    public void Without_a_direction_this_pcs_end_is_told_by_its_address()
    {
        var table = new List<Connection> { new(TransportProtocol.Tcp, Own, 51432, Remote, 443, TcpConnectionState.Established, 100) };
        var resolver = Resolver(() => table, new Clock());
        var outgoing = TcpPacket(true, 1, 1, Ack);   // Ethernet frames, as Npcap gives them: no direction
        var incoming = TcpPacket(false, 1, 1, Ack);
        resolver.Resolve(outgoing);
        resolver.Resolve(incoming);
        Assert.Equal((100, 100), (outgoing.ProcessId, incoming.ProcessId));
    }

    [Fact]
    public void The_table_is_read_again_on_a_miss_but_not_more_than_every_50_ms()
    {
        int reads = 0;
        var table = new List<Connection>();
        var clock = new Clock();
        var resolver = Resolver(() => { reads++; return table; }, clock);
        var first = TcpOut(50001);
        resolver.Resolve(first);
        Assert.Equal((1, 0), (reads, first.ProcessId));

        table.Add(TcpRow(50001, 100));
        var soon = TcpOut(50001);
        clock.Now += TimeSpan.FromMilliseconds(20);
        resolver.Resolve(soon);
        Assert.Equal((1, 0), (reads, soon.ProcessId));

        var later = TcpOut(50001);
        clock.Now += TimeSpan.FromMilliseconds(40);
        resolver.Resolve(later);
        Assert.Equal((2, 100), (reads, later.ProcessId));
    }

    [Fact]
    public void A_closed_connection_keeps_its_program_for_two_minutes()
    {
        var table = new List<Connection> { TcpRow(50002, 100) };
        var clock = new Clock();
        var resolver = Resolver(() => table, clock);
        resolver.Resolve(TcpOut(50002));
        table.Clear();

        clock.Now += TimeSpan.FromSeconds(5);
        var fin = TcpOut(50002, Fin | Ack);
        resolver.Resolve(fin);
        Assert.Equal(100, fin.ProcessId);

        clock.Now += TimeSpan.FromMinutes(2);
        var stray = TcpOut(50002);
        resolver.Resolve(stray);
        Assert.Equal((0, null), (stray.ProcessId, stray.ProcessName));
    }

    [Fact]
    public void A_port_another_program_takes_over_goes_to_that_program()
    {
        var table = new List<Connection> { TcpRow(50003, 100) };
        var clock = new Clock();
        var resolver = Resolver(() => table, clock);
        resolver.Resolve(TcpOut(50003));
        table = new List<Connection> { new(TransportProtocol.Tcp, Own, 50003, Remote, 443, TcpConnectionState.SynSent, 300) };

        // A new connection from the same port, 100 ms later: the old table would still say chrome.exe.
        clock.Now += TimeSpan.FromMilliseconds(100);
        var syn = TcpOut(50003, Syn);
        resolver.Resolve(syn);
        Assert.Equal((300, "game.exe"), (syn.ProcessId, syn.ProcessName));
    }

    [Fact]
    public void Packets_without_ports_are_left_alone()
    {
        int reads = 0;
        var resolver = Resolver(() => { reads++; return new List<Connection>(); }, new Clock());
        var ping = Raw(Ipv4(Own, Remote, 1, new byte[] { 8, 0, 0, 0, 0, 1, 0, 1 }), PacketDirection.Outbound);
        resolver.Resolve(ping);
        Assert.Equal((0, null, 0), (ping.ProcessId, ping.ProcessName, reads));
    }
}
