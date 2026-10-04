using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Sweeply.Core.Capture;

/// <summary>Two ends talking: TCP or UDP (address and port), or other IP traffic (address only).</summary>
public sealed class Conversation
{
    public required string Protocol { get; init; } // "TCP", "UDP", "IPv4", "IPv6"
    public required IPAddress AddressA { get; init; }
    public required IPAddress AddressB { get; init; }
    public int PortA { get; init; }
    public int PortB { get; init; }

    /// <summary>The stream number tcp.stream or udp.stream has for it; -1 for other IP traffic.</summary>
    public int Stream { get; init; } = -1;

    public long PacketsAToB { get; internal set; }
    public long BytesAToB { get; internal set; }
    public long PacketsBToA { get; internal set; }
    public long BytesBToA { get; internal set; }
    public DateTime FirstUtc { get; internal set; }
    public DateTime LastUtc { get; internal set; }

    /// <summary>The program on this PC's end, when the packets say (the most common one).</summary>
    public string? Program { get; internal set; }

    /// <summary>Names for the ends, from the capture itself: the server name a TLS or HTTP request asked for, or a DNS answer.</summary>
    public string? NameA { get; internal set; }
    public string? NameB { get; internal set; }

    public long Packets => PacketsAToB + PacketsBToA;
    public long Bytes => BytesAToB + BytesBToA;
    public TimeSpan Duration => LastUtc - FirstUtc;

    /// <summary>A display filter for just this conversation.</summary>
    public string Filter
    {
        get
        {
            if (Stream >= 0) return $"{Protocol.ToLowerInvariant()}.stream == {Stream}";
            string field = AddressA.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6.addr" : "ip.addr";
            return $"{field} == {AddressA} && {field} == {AddressB}";
        }
    }
}

/// <summary>One address and how much it sent and received.</summary>
public sealed class Endpoint
{
    public required IPAddress Address { get; init; }
    public long PacketsSent { get; internal set; }
    public long BytesSent { get; internal set; }
    public long PacketsReceived { get; internal set; }
    public long BytesReceived { get; internal set; }

    /// <summary>A name from the capture (a DNS answer, or else a server name asked for).</summary>
    public string? Name { get; internal set; }

    /// <summary>This PC's own address: it sent what went out, or received what came in.</summary>
    public bool IsThisPc { get; internal set; }

    public long Packets => PacketsSent + PacketsReceived;
    public long Bytes => BytesSent + BytesReceived;
    public string Filter => $"{(Address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6.addr" : "ip.addr")} == {Address}";
}

/// <summary>What one program sent and received, and where to.</summary>
public sealed class ProgramUse
{
    public required string Name { get; init; }
    public long Packets { get; internal set; }
    public long BytesSent { get; internal set; }
    public long BytesReceived { get; internal set; }
    public int Conversations { get; internal set; }

    /// <summary>The other ends it exchanged the most with (names where the capture has them), most first.</summary>
    public IReadOnlyList<string> Hosts { get; internal set; } = Array.Empty<string>();

    public long Bytes => BytesSent + BytesReceived;
    public string Filter => $"{DisplayFilter.ProcessName} == {DisplayFilter.Quote(Name)}";
}

/// <summary>A protocol and the packets that carry it, with the protocols carried inside it.</summary>
public sealed class ProtocolShare
{
    public required string Name { get; init; }
    public long Packets { get; internal set; }
    public long Bytes { get; internal set; }
    public List<ProtocolShare> Children { get; } = new();
    public string Filter => Name;
}

public enum ProblemKind
{
    Retransmission,
    FastRetransmission,
    OutOfOrder,
    LostSegment,
    AckedUnseen,
    DuplicateAck,
    ZeroWindow,
    Reset,
    IcmpError,
    DnsError,
}

/// <summary>A kind of trouble in the capture: how often, the first packet with it, and the filter that shows them all.</summary>
public sealed record Problem(ProblemKind Kind, int Count, int FirstFrame, string Filter);

/// <summary>Packets and bytes per step of time (10 ms to hours, so there are enough points), from the first packet on; also split by direction where known.</summary>
public sealed class TrafficSeries
{
    public required TimeSpan Step { get; init; }
    public required long[] Packets { get; init; }
    public required long[] Bytes { get; init; }
    public required long[] BytesOut { get; init; }
    public required long[] BytesIn { get; init; }
}

/// <summary>
/// What a capture adds up to, as Wireshark's Statistics menu shows it: conversations, endpoints, the protocol
/// hierarchy, trouble (retransmissions, resets, failed lookups…) and traffic over time — and, which Wireshark can't,
/// the programs. Addresses get names from the capture itself (DNS answers, the server names TLS and HTTP ask for),
/// so nothing is looked up on the network.
/// </summary>
public sealed class CaptureStatistics
{
    private static readonly TimeSpan[] Steps =
    {
        TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(6),
    };

    /// <summary>Points of the traffic series at most: the step is the finest one that keeps to this.</summary>
    public const int MaxPoints = 150;

    public int Packets { get; private init; }
    public long Bytes { get; private init; }
    public DateTime FirstUtc { get; private init; }
    public DateTime LastUtc { get; private init; }
    public TimeSpan Duration => LastUtc - FirstUtc;
    public int PacketsWithProgram { get; private init; }
    public IReadOnlyList<Conversation> Conversations { get; private init; } = Array.Empty<Conversation>();
    public IReadOnlyList<Endpoint> Endpoints { get; private init; } = Array.Empty<Endpoint>();
    public IReadOnlyList<ProgramUse> Programs { get; private init; } = Array.Empty<ProgramUse>();
    public ProtocolShare Protocols { get; private init; } = new() { Name = "frame" };
    public IReadOnlyList<Problem> Problems { get; private init; } = Array.Empty<Problem>();
    public TrafficSeries Traffic { get; private init; } = new() { Step = TimeSpan.FromSeconds(1), Packets = Array.Empty<long>(), Bytes = Array.Empty<long>(), BytesOut = Array.Empty<long>(), BytesIn = Array.Empty<long>() };

    /// <summary>Names for addresses, from the capture's own DNS answers and requested server names.</summary>
    public IReadOnlyDictionary<IPAddress, string> Names { get; private init; } = new Dictionary<IPAddress, string>();

    /// <summary>What each packet tells beyond its headers; worked out in parallel, one decode per packet.</summary>
    private struct Facts
    {
        public string Path;                                // "frame:eth:ip:tcp:tls"
        public string? Host;                               // the server name a TLS Client Hello or an HTTP request asked for
        public List<(IPAddress Address, string Name)>? Answers; // A and AAAA records of a DNS answer, with the name asked for
        public bool DnsError, IcmpError;
    }

    /// <param name="progress">Called now and then from worker threads with how many packets are done.</param>
    public static CaptureStatistics Compute(IReadOnlyList<CapturedPacket> packets, IReadOnlyList<StreamInfo> streams,
        IReadOnlyList<string> interfaceNames, Action<int>? progress = null, CancellationToken cancel = default)
    {
        int count = packets.Count;
        if (count == 0) return new CaptureStatistics();
        var first = packets[0].TimestampUtc;
        var facts = new Facts[count];
        var paths = new ConcurrentDictionary<string, string>(); // one string per distinct path
        int done = 0;
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, count, options, i =>
        {
            var p = packets[i];
            string name = p.InterfaceId >= 0 && p.InterfaceId < interfaceNames.Count ? interfaceNames[p.InterfaceId] : "";
            facts[i] = Extract(Dissector.Dissect(p, i + 1, streams[i], first, name), paths);
            int now = Interlocked.Increment(ref done);
            if (now % 2000 == 0) progress?.Invoke(now);
        });
        cancel.ThrowIfCancellationRequested();
        return Aggregate(packets, streams, facts);
    }

    private static Facts Extract(Dissection d, ConcurrentDictionary<string, string> paths)
    {
        string path = string.Join(":", d.Protocols);
        var facts = new Facts { Path = paths.GetOrAdd(path, path) };
        string? question = null;
        bool response = false;
        foreach (var node in d.Layers.SelectMany(l => l.Walk()))
        {
            switch (node.Field)
            {
                case "tls.handshake.extensions_server_name" or "http.host" when node.Value is string host && host.Length > 0:
                    facts.Host ??= host.Split(':')[0]; // "example.com:8080" asks for example.com
                    break;
                case "dns.qry.name" when node.Value is string asked:
                    question ??= asked;
                    break;
                case "dns.flags.response" when node.Value is true:
                    response = true;
                    break;
                case "dns.flags.rcode" when node.Value is int rcode && rcode > 0:
                    facts.DnsError = true;
                    break;
                case "dns.a" or "dns.aaaa" when node.Value is IPAddress address && question is not null:
                    (facts.Answers ??= new()).Add((address, question));
                    break;
                case "icmp.type" when Convert.ToInt32(node.Value) is 3 or 4 or 5 or 11 or 12:
                case "icmpv6.type" when Convert.ToInt32(node.Value) is 1 or 2 or 3 or 4:
                    facts.IcmpError = true;
                    break;
            }
        }
        if (!response) facts.Answers = null;
        return facts;
    }

    private sealed class ConversationBuilder
    {
        public required Conversation Conversation { get; init; }
        public required AddressKey KeyA { get; init; }
        public Dictionary<string, int>? Programs;
        public string? HostA, HostB;
    }

    private static CaptureStatistics Aggregate(IReadOnlyList<CapturedPacket> packets, IReadOnlyList<StreamInfo> streams, Facts[] facts)
    {
        int count = packets.Count;
        var first = packets[0].TimestampUtc;
        var last = first;
        foreach (var p in packets)
            if (p.TimestampUtc > last) last = p.TimestampUtc;
        var step = Steps.FirstOrDefault(s => (last - first).Ticks / s.Ticks < MaxPoints, Steps[^1]);
        int buckets = (int)Math.Min(MaxPoints, (last - first).Ticks / step.Ticks + 1);
        var traffic = new TrafficSeries { Step = step, Packets = new long[buckets], Bytes = new long[buckets], BytesOut = new long[buckets], BytesIn = new long[buckets] };

        var root = new ProtocolShare { Name = "frame" };
        var conversations = new Dictionary<(string, ulong, ulong, ulong, ulong), ConversationBuilder>();
        var endpoints = new Dictionary<AddressKey, Endpoint>();
        var programs = new Dictionary<string, (ProgramUse Use, HashSet<ConversationBuilder> Conversations)>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<AddressKey, string>();
        var problems = new Dictionary<ProblemKind, (int Count, int First)>();
        long bytes = 0;
        int withProgram = 0;

        for (int i = 0; i < count; i++)
        {
            var p = packets[i];
            var f = facts[i];
            long length = p.Length;
            bytes += length;
            int bucket = (int)Math.Min(buckets - 1, Math.Max(0, (p.TimestampUtc - first).Ticks / step.Ticks));
            traffic.Packets[bucket]++;
            traffic.Bytes[bucket] += length;
            if (p.Direction == PacketDirection.Outbound) traffic.BytesOut[bucket] += length;
            else if (p.Direction == PacketDirection.Inbound) traffic.BytesIn[bucket] += length;

            // The protocol hierarchy: every protocol on the packet's path counts it.
            var node = root;
            root.Packets++;
            root.Bytes += length;
            foreach (var name in f.Path.Split(':').Skip(1))
            {
                var child = node.Children.Find(c => c.Name == name);
                if (child is null) node.Children.Add(child = new ProtocolShare { Name = name });
                child.Packets++;
                child.Bytes += length;
                node = child;
            }

            if (f.Answers is not null)
                foreach (var (address, name) in f.Answers)
                    names.TryAdd(AddressKey.From(address), name);

            var h = QuickHeader.Read(p.Link, p.Data);
            var analysis = streams[i].Analysis;
            void Note(ProblemKind kind) => problems[kind] = problems.TryGetValue(kind, out var seen) ? (seen.Count + 1, seen.First) : (1, i + 1);
            if ((analysis & TcpAnalysis.Retransmission) != 0) Note(ProblemKind.Retransmission);
            if ((analysis & TcpAnalysis.FastRetransmission) != 0) Note(ProblemKind.FastRetransmission);
            if ((analysis & TcpAnalysis.OutOfOrder) != 0) Note(ProblemKind.OutOfOrder);
            if ((analysis & TcpAnalysis.LostSegment) != 0) Note(ProblemKind.LostSegment);
            if ((analysis & TcpAnalysis.AckedUnseen) != 0) Note(ProblemKind.AckedUnseen);
            if ((analysis & TcpAnalysis.DuplicateAck) != 0) Note(ProblemKind.DuplicateAck);
            if ((analysis & TcpAnalysis.ZeroWindow) != 0) Note(ProblemKind.ZeroWindow);
            if (h.IsTcp && (h.TcpFlags & QuickHeader.Rst) != 0) Note(ProblemKind.Reset);
            if (f.IcmpError) Note(ProblemKind.IcmpError);
            if (f.DnsError) Note(ProblemKind.DnsError);

            if (!h.IsIp) continue;
            var source = EndpointOf(endpoints, h.Source);
            var destination = EndpointOf(endpoints, h.Destination);
            source.PacketsSent++;
            source.BytesSent += length;
            destination.PacketsReceived++;
            destination.BytesReceived += length;
            if (p.Direction == PacketDirection.Outbound) source.IsThisPc = true;
            else if (p.Direction == PacketDirection.Inbound) destination.IsThisPc = true;

            var conversation = ConversationOf(conversations, h, streams[i], p.TimestampUtc);
            var c = conversation.Conversation;
            bool aToB = conversation.KeyA == h.Source && (c.Stream < 0 || c.PortA == h.SourcePort);
            if (aToB)
            {
                c.PacketsAToB++;
                c.BytesAToB += length;
            }
            else
            {
                c.PacketsBToA++;
                c.BytesBToA += length;
            }
            if (p.TimestampUtc < c.FirstUtc) c.FirstUtc = p.TimestampUtc;
            if (p.TimestampUtc > c.LastUtc) c.LastUtc = p.TimestampUtc;
            // A server name asked for belongs to the end the request went to.
            if (f.Host is { } host)
            {
                if (aToB) conversation.HostB ??= host;
                else conversation.HostA ??= host;
            }

            if (p.ProcessName is { Length: > 0 } program)
            {
                withProgram++;
                var counts = conversation.Programs ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                counts[program] = counts.GetValueOrDefault(program) + 1;
                if (!programs.TryGetValue(program, out var use))
                    programs[program] = use = (new ProgramUse { Name = program }, new HashSet<ConversationBuilder>());
                use.Use.Packets++;
                if (p.Direction == PacketDirection.Outbound) use.Use.BytesSent += length;
                else if (p.Direction == PacketDirection.Inbound) use.Use.BytesReceived += length;
                use.Conversations.Add(conversation);
            }
        }

        // Names: the server name a connection asked for is exact; a DNS answer names the address in general.
        foreach (var builder in conversations.Values)
        {
            var c = builder.Conversation;
            c.NameA = builder.HostA ?? names.GetValueOrDefault(builder.KeyA);
            c.NameB = builder.HostB ?? names.GetValueOrDefault(AddressKey.From(c.AddressB));
            if (builder.Programs is { } counts) c.Program = counts.MaxBy(kv => kv.Value).Key;
            if (builder.HostB is { } hostB) names.TryAdd(AddressKey.From(c.AddressB), hostB);
            if (builder.HostA is { } hostA) names.TryAdd(builder.KeyA, hostA);
        }
        foreach (var (key, endpoint) in endpoints)
            endpoint.Name = names.GetValueOrDefault(key);
        foreach (var (use, used) in programs.Values)
        {
            use.Conversations = used.Count;
            use.Hosts = used
                .Select(b => (Other: OtherEnd(b), b.Conversation.Bytes))
                .GroupBy(x => x.Other)
                .Select(g => (Host: g.Key, Bytes: g.Sum(x => x.Bytes)))
                .OrderByDescending(x => x.Bytes)
                .Take(5)
                .Select(x => x.Host)
                .ToList();
        }
        SortChildren(root);

        return new CaptureStatistics
        {
            Packets = count,
            Bytes = bytes,
            FirstUtc = first,
            LastUtc = last,
            PacketsWithProgram = withProgram,
            Conversations = conversations.Values.Select(b => b.Conversation).OrderByDescending(c => c.Bytes).ToList(),
            Endpoints = endpoints.Values.OrderByDescending(e => e.Bytes).ToList(),
            Programs = programs.Values.Select(p => p.Use).OrderByDescending(p => p.Bytes).ThenByDescending(p => p.Packets).ToList(),
            Protocols = root,
            Problems = problems.OrderBy(kv => kv.Key).Select(kv => new Problem(kv.Key, kv.Value.Count, kv.Value.First, FilterFor(kv.Key))).ToList(),
            Traffic = traffic,
            Names = names.ToDictionary(kv => kv.Key.ToAddress(), kv => kv.Value),
        };

        // The end that isn't this PC, by name where there is one. Without directions (Npcap), the end with the
        // lower port is taken for the server, which is usually the other end.
        string OtherEnd(ConversationBuilder b)
        {
            var c = b.Conversation;
            bool aIsHere = endpoints.TryGetValue(b.KeyA, out var a) && a.IsThisPc;
            bool bIsHere = endpoints.TryGetValue(AddressKey.From(c.AddressB), out var other) && other.IsThisPc;
            bool otherIsB = aIsHere || (!bIsHere && c.PortB <= c.PortA);
            return otherIsB ? c.NameB ?? c.AddressB.ToString() : c.NameA ?? c.AddressA.ToString();
        }
    }

    private static Endpoint EndpointOf(Dictionary<AddressKey, Endpoint> endpoints, AddressKey key)
    {
        if (!endpoints.TryGetValue(key, out var endpoint)) endpoints[key] = endpoint = new Endpoint { Address = key.ToAddress() };
        return endpoint;
    }

    private static ConversationBuilder ConversationOf(Dictionary<(string, ulong, ulong, ulong, ulong), ConversationBuilder> all, in QuickHeader h, in StreamInfo stream, DateTime at)
    {
        (string, ulong, ulong, ulong, ulong) key;
        string protocol;
        int number = -1;
        if (h.IsTcp && stream.TcpStream >= 0)
        {
            protocol = "TCP";
            number = stream.TcpStream;
            key = (protocol, (ulong)number, 0, 0, 0);
        }
        else if (h.IsUdp && stream.UdpStream >= 0)
        {
            protocol = "UDP";
            number = stream.UdpStream;
            key = (protocol, (ulong)number, 0, 0, 0);
        }
        else
        {
            protocol = h.IpVersion == 6 ? "IPv6" : "IPv4";
            var (low, high) = Compare(h.Source, h.Destination) <= 0 ? (h.Source, h.Destination) : (h.Destination, h.Source);
            key = (protocol, low.High, low.Low, high.High, high.Low);
        }
        if (all.TryGetValue(key, out var builder)) return builder;
        // The end that sent the first packet is A, as in Wireshark.
        builder = new ConversationBuilder
        {
            KeyA = h.Source,
            Conversation = new Conversation
            {
                Protocol = protocol,
                AddressA = h.Source.ToAddress(),
                AddressB = h.Destination.ToAddress(),
                PortA = number >= 0 ? h.SourcePort : 0,
                PortB = number >= 0 ? h.DestinationPort : 0,
                Stream = number,
                FirstUtc = at,
                LastUtc = at,
            },
        };
        all[key] = builder;
        return builder;
    }

    private static int Compare(AddressKey a, AddressKey b) => a.High != b.High ? a.High.CompareTo(b.High) : a.Low.CompareTo(b.Low);

    private static void SortChildren(ProtocolShare node)
    {
        node.Children.Sort((a, b) => b.Packets.CompareTo(a.Packets));
        foreach (var child in node.Children) SortChildren(child);
    }

    private static string FilterFor(ProblemKind kind) => kind switch
    {
        ProblemKind.Retransmission => "tcp.analysis.retransmission",
        ProblemKind.FastRetransmission => "tcp.analysis.fast_retransmission",
        ProblemKind.OutOfOrder => "tcp.analysis.out_of_order",
        ProblemKind.LostSegment => "tcp.analysis.lost_segment",
        ProblemKind.AckedUnseen => "tcp.analysis.ack_lost_segment",
        ProblemKind.DuplicateAck => "tcp.analysis.duplicate_ack",
        ProblemKind.ZeroWindow => "tcp.analysis.zero_window",
        ProblemKind.Reset => "tcp.flags.reset == 1",
        ProblemKind.IcmpError => "icmp.type in {3 4 5 11 12} || icmpv6.type in {1..4}",
        ProblemKind.DnsError => "dns.flags.rcode > 0",
        _ => "",
    };
}
