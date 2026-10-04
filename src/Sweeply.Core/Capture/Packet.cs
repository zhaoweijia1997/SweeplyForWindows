namespace Sweeply.Core.Capture;

/// <summary>What a captured packet starts with: the pcap link-layer types (LINKTYPE_*) this app understands.</summary>
public enum LinkType
{
    /// <summary>A 4-byte address family in host byte order, then the packet (BSD loopback; Npcap's loopback adapter).</summary>
    Null = 0,
    Ethernet = 1,

    /// <summary>An IPv4 or IPv6 packet with no link layer, which is what raw sockets give.</summary>
    Raw = 101,
    Ieee80211 = 105,

    /// <summary>Like <see cref="Null"/> with the address family in network byte order (OpenBSD loopback).</summary>
    Loop = 108,
    LinuxSll = 113,
    Ipv4 = 228,
    Ipv6 = 229,
    LinuxSll2 = 276,
}

public enum PacketDirection : byte
{
    Unknown,
    Inbound,
    Outbound,
}

/// <summary>An interface packets were captured on (a capture file can hold several).</summary>
public sealed record CaptureInterface(LinkType Link, string Name, string Description = "");

/// <summary>One captured packet: when, on which interface, its bytes and, when known, the program it belongs to.</summary>
public sealed class CapturedPacket
{
    public required DateTime TimestampUtc { get; init; }
    public required LinkType Link { get; init; }
    public required byte[] Data { get; init; }

    /// <summary>The length on the wire; more than <see cref="Data"/> when the capture kept only the start. 0 means the same.</summary>
    public int OriginalLength { get; init; }
    public int InterfaceId { get; init; }
    public PacketDirection Direction { get; init; }

    /// <summary>The program that sent or received it, when it could be told (0 and null when not).</summary>
    public int ProcessId { get; set; }
    public string? ProcessName { get; set; }

    /// <summary>A comment from a capture file, other than the program.</summary>
    public string? Comment { get; init; }

    public int Length => OriginalLength > Data.Length ? OriginalLength : Data.Length;
}

/// <summary>A capture read from a file.</summary>
public sealed class CaptureFile
{
    public List<CaptureInterface> Interfaces { get; } = new();
    public List<CapturedPacket> Packets { get; } = new();

    /// <summary>The program that wrote the file, when it says.</summary>
    public string? Application { get; set; }

    /// <summary>What was skipped as unreadable at the end (a file cut short while being written).</summary>
    public bool Truncated { get; set; }
}
