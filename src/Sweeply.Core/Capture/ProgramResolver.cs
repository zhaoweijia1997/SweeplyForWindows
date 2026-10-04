using System.Net;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Capture;

/// <summary>
/// Names the program each captured packet belongs to, from Windows' table of which program has which local port
/// (the table the Connections section shows). The table is read again when a packet's port isn't in it, when a TCP
/// connection opens (its port may have belonged to another program before), and at least every second — but never
/// more often than every 50 ms. A port once matched is remembered for 2 minutes, so the last packets of a closed
/// connection still get their program. A socket that opens and closes between two reads can be missed.
/// </summary>
public sealed class ProgramResolver
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(2);
    private static readonly AddressKey AnyV4 = AddressKey.From(IPAddress.Any), AnyV6 = AddressKey.From(IPAddress.IPv6Any);

    private readonly Func<IReadOnlyList<Connection>> _readTable;
    private readonly Func<int, string> _nameOf;
    private readonly Func<DateTime> _now;
    private readonly HashSet<AddressKey> _own;
    private Dictionary<(bool Tcp, int Port), List<(AddressKey Local, int ProcessId)>> _ports = new();
    private readonly Dictionary<(bool Tcp, AddressKey Local, int Port), (int ProcessId, DateTime Seen)> _remembered = new();
    private DateTime _readUtc = DateTime.MinValue, _prunedUtc = DateTime.MinValue;

    /// <param name="ownAddresses">This PC's addresses, to tell its end of a packet that comes without a direction.</param>
    public ProgramResolver(IEnumerable<IPAddress> ownAddresses, Func<IReadOnlyList<Connection>>? readTable = null,
        Func<int, string>? nameOf = null, Func<DateTime>? now = null)
    {
        _own = ownAddresses.Select(AddressKey.From).ToHashSet();
        _readTable = readTable ?? ConnectionTable.Read;
        var names = new ProgramNames();
        _nameOf = nameOf ?? (id => names.Get(id).Name);
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Sets the packet's program when it can be told; leaves it alone when not.</summary>
    public void Resolve(CapturedPacket packet)
    {
        var h = QuickHeader.Read(packet.Link, packet.Data);
        if (!h.IsTcp && !h.IsUdp) return;
        bool tcp = h.IsTcp;

        // This PC's end: the sender of what goes out, the receiver of what comes in. Without a direction, the end
        // whose address is this PC's; when both or neither are, both are tried.
        var source = (h.Source, h.SourcePort);
        var destination = (h.Destination, h.DestinationPort);
        (AddressKey, int) first = source;
        (AddressKey, int)? second = null;
        if (packet.Direction == PacketDirection.Inbound) first = destination;
        else if (packet.Direction == PacketDirection.Unknown)
        {
            bool sourceOwn = _own.Contains(h.Source), destinationOwn = _own.Contains(h.Destination);
            if (destinationOwn && !sourceOwn) first = destination;
            else if (sourceOwn == destinationOwn) second = destination;
        }

        var now = _now();
        bool opening = tcp && (h.TcpFlags & (QuickHeader.Syn | QuickHeader.AckFlag)) == QuickHeader.Syn;
        if (now - _readUtc >= MaxAge || (opening && now - _readUtc >= MinInterval)) Refresh(now);
        int id = Find(tcp, first, second);
        if (id == 0 && now - _readUtc >= MinInterval)
        {
            Refresh(now);
            id = Find(tcp, first, second);
        }
        if (id == 0) id = Remembered(tcp, first, second);
        if (id <= 0) return;
        packet.ProcessId = id;
        packet.ProcessName = _nameOf(id) is { Length: > 0 } name ? name : null;
    }

    private int Find(bool tcp, (AddressKey, int) first, (AddressKey, int)? second)
    {
        int id = FindInTable(tcp, first.Item1, first.Item2);
        if (id == 0 && second is { } other) id = FindInTable(tcp, other.Item1, other.Item2);
        return id;
    }

    /// <summary>The owner of that local port: bound to exactly that address, or else to every address (0.0.0.0, ::).</summary>
    private int FindInTable(bool tcp, AddressKey address, int port)
    {
        if (!_ports.TryGetValue((tcp, port), out var owners)) return 0;
        int id = 0;
        foreach (var (local, owner) in owners)
        {
            if (local == address)
            {
                id = owner;
                break;
            }
            if (id == 0 && (local == AnyV4 || local == AnyV6)) id = owner;
        }
        if (id > 0) _remembered[(tcp, address, port)] = (id, _now());
        return id;
    }

    private int Remembered(bool tcp, (AddressKey, int) first, (AddressKey, int)? second)
    {
        var now = _now();
        foreach (var end in second is { } other ? new[] { first, other } : new[] { first })
            if (_remembered.TryGetValue((tcp, end.Item1, end.Item2), out var known) && now - known.Seen < RememberFor)
                return known.ProcessId;
        return 0;
    }

    private void Refresh(DateTime now)
    {
        _readUtc = now;
        var ports = new Dictionary<(bool, int), List<(AddressKey, int)>>();
        foreach (var c in _readTable())
        {
            if (c.ProcessId <= 0) continue; // closed connections (TIME_WAIT) have no program any more
            var key = (c.Protocol == TransportProtocol.Tcp, c.LocalPort);
            if (!ports.TryGetValue(key, out var list)) ports[key] = list = new List<(AddressKey, int)>(1);
            list.Add((AddressKey.From(c.LocalAddress), c.ProcessId));
        }
        _ports = ports;
        if (now - _prunedUtc > TimeSpan.FromSeconds(30) || _remembered.Count > 100_000)
        {
            _prunedUtc = now;
            foreach (var key in _remembered.Where(kv => now - kv.Value.Seen >= RememberFor).Select(kv => kv.Key).ToList())
                _remembered.Remove(key);
        }
    }
}
