namespace Sweeply.Core.Behavior;

/// <summary>What a recorded program did.</summary>
public enum BehaviorKind
{
    ProcessStarted,
    ProcessExited,
    FileCreated,
    FileWritten,
    FileDeleted,
    FileRenamed,
    KeyCreated,
    KeyDeleted,
    ValueSet,
    ValueDeleted,

    /// <summary>A TCP connection it made, or the first UDP datagram it sent to an address.</summary>
    Connected,

    /// <summary>A TCP connection it accepted.</summary>
    Accepted,
    DnsLookup,
}

/// <summary>
/// One thing a recorded process did. Paths are as Windows' kernel names them ("\Device\HarddiskVolume3\…",
/// "\REGISTRY\MACHINE\…") when they come from the helper, and are turned into "C:\…" and "HKLM\…" for showing
/// (<see cref="SystemPaths"/>).
/// </summary>
public sealed record BehaviorEvent
{
    public DateTime TimeUtc { get; init; }
    public int ProcessId { get; init; }
    public BehaviorKind Kind { get; init; }

    /// <summary>What it acted on: a program's, file's or registry key's path, "203.0.113.10:443", or a host name.</summary>
    public string Target { get; init; } = "";

    /// <summary>
    /// More about it: a new process's command line, a registry value's name, a lookup's answers ("203.0.113.10;…"),
    /// "TCP" or "UDP".
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>A registry value's new data, as text (up to a few hundred characters).</summary>
    public string Data { get; init; } = "";

    /// <summary>ProcessStarted: the parent's id. ProcessExited: the exit code.</summary>
    public int Number { get; init; }

    /// <summary>FileWritten: how many bytes.</summary>
    public long Size { get; init; }
}
