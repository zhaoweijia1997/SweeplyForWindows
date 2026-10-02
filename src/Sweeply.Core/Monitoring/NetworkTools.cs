using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sweeply.Core.Monitoring;

/// <summary>Running totals of a ping: sent, answered, lost, and the round-trip times.</summary>
public sealed class PingStats
{
    private long _sum;

    public int Sent { get; private set; }
    public int Received { get; private set; }
    public long? Last { get; private set; }
    public long? Min { get; private set; }
    public long? Max { get; private set; }
    public double? Average => Received == 0 ? null : (double)_sum / Received;

    /// <summary>Lost as a whole percentage of what was sent.</summary>
    public int LossPercent => Sent == 0 ? 0 : (int)Math.Round(100.0 * (Sent - Received) / Sent);

    /// <param name="milliseconds">The round trip, or null when no answer came.</param>
    public void Add(long? milliseconds)
    {
        Sent++;
        Last = milliseconds;
        if (milliseconds is not long ms) return;
        Received++;
        _sum += ms;
        Min = Min is long min ? Math.Min(min, ms) : ms;
        Max = Max is long max ? Math.Max(max, ms) : ms;
    }
}

/// <summary>One hop on the way to a target: who answered (null: nobody did in time) and how fast.</summary>
public readonly record struct TraceHop(int Number, IPAddress? Address, long? Milliseconds, bool IsTarget);

/// <summary>
/// Ping and trace route, with ICMP echo as Windows' own ping and tracert send it (no administrator rights
/// needed). Nothing is sent unless one of these is called.
/// </summary>
public static class NetworkTools
{
    public const int Timeout = 1000;   // ms to wait for each answer
    public const int MaxHops = 30;
    private static readonly byte[] Payload = new byte[32]; // what ping.exe sends

    /// <summary>An address typed in ("192.0.2.1", "fe80::1", "example.com"); IPv4 preferred. Null when it can't be found.</summary>
    public static async Task<IPAddress?> ResolveAsync(string target, CancellationToken cancel)
    {
        target = target.Trim();
        if (target.Length == 0) return null;
        if (IPAddress.TryParse(target, out var literal)) return literal;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target, cancel);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (SocketException) { return null; }
        catch (ArgumentException) { return null; } // not a valid name at all
    }

    /// <summary>
    /// 198.18.0.0/15, set aside for network testing, is where proxies in TUN mode ("fake-ip") put the
    /// addresses they give for names: pinging such an address measures the proxy, not the network.
    /// </summary>
    public static bool IsProxyFakeAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] & 0xFE) == 18;
    }

    /// <summary>One echo: the round trip in ms, or null with the reason.</summary>
    public static async Task<(long? Milliseconds, IPStatus Status, IPAddress? From)> PingAsync(Ping ping, IPAddress target, int ttl = 128)
    {
        var watch = Stopwatch.StartNew();
        PingReply reply;
        try { reply = await ping.SendPingAsync(target, Timeout, Payload, new PingOptions(ttl, dontFragment: false)); }
        catch (PingException) { return (null, IPStatus.Unknown, null); }
        // Windows gives no round trip for "time to live exceeded"; the clock around the call is close enough.
        long ms = reply.Status == IPStatus.Success ? reply.RoundtripTime : watch.ElapsedMilliseconds;
        return reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded
            ? (ms, reply.Status, reply.Address)
            : (null, reply.Status, reply.Address);
    }

    /// <summary>
    /// The route to <paramref name="target"/>: an echo with a time-to-live of 1, 2, 3… so that each router
    /// on the way answers in turn, until the target answers or <see cref="MaxHops"/>. Each hop is tried
    /// twice before it counts as silent. Hops are reported as they come.
    /// </summary>
    /// <returns>True when the target was reached.</returns>
    public static async Task<bool> TraceAsync(IPAddress target, IProgress<TraceHop> progress, CancellationToken cancel)
    {
        using var ping = new Ping();
        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            cancel.ThrowIfCancellationRequested();
            (long? Milliseconds, IPStatus Status, IPAddress? From) answer = default;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                answer = await PingAsync(ping, target, ttl);
                if (answer.Milliseconds is not null) break;
                cancel.ThrowIfCancellationRequested();
            }
            bool reached = answer.Status == IPStatus.Success;
            progress.Report(new TraceHop(ttl, answer.Milliseconds is null ? null : answer.From, answer.Milliseconds, reached));
            if (reached) return true;
        }
        return false;
    }

    /// <summary>The name a DNS server gives for an address, or null (most routers have none; give up after 2 s).</summary>
    public static async Task<string?> NameOfAsync(IPAddress address, CancellationToken cancel)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address);
            var done = await Task.WhenAny(lookup, Task.Delay(2000, cancel));
            if (done != lookup) return null;
            string name = (await lookup).HostName;
            return name.Length == 0 || name == address.ToString() ? null : name;
        }
        catch (Exception e) when (e is SocketException or ArgumentException) { return null; }
    }
}
