using System.Net;
using System.Net.NetworkInformation;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class NetworkToolTests
{
    [Fact]
    public void Ping_statistics()
    {
        var stats = new PingStats();
        Assert.Equal(0, stats.LossPercent);
        Assert.Null(stats.Average);
        foreach (long? ms in new long?[] { 12, null, 4, 20, null })
            stats.Add(ms);
        Assert.Equal((5, 3), (stats.Sent, stats.Received));
        Assert.Equal(40, stats.LossPercent);
        Assert.Equal((4L, 20L), (stats.Min!.Value, stats.Max!.Value));
        Assert.Equal(12, stats.Average!.Value, 6);
        Assert.Null(stats.Last);
    }

    [Fact]
    public async Task Addresses_typed_in_or_looked_up()
    {
        Assert.Equal(IPAddress.Parse("192.0.2.7"), await NetworkTools.ResolveAsync(" 192.0.2.7 ", default));
        Assert.Equal(IPAddress.Parse("2001:db8::1"), await NetworkTools.ResolveAsync("2001:db8::1", default));
        Assert.True(IPAddress.IsLoopback((await NetworkTools.ResolveAsync("localhost", default))!));
        Assert.Null(await NetworkTools.ResolveAsync("", default));
        // Not asserted: that "no-such-host.invalid" finds nothing. A proxy in TUN mode (fake-ip) answers
        // every name with a 198.18.x.x address, this PC's included.
    }

    [Theory]
    [InlineData("198.18.0.81", true)]
    [InlineData("198.19.255.1", true)]
    [InlineData("198.20.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("2001:db8::1", false)]
    public void Proxy_fake_addresses_are_recognised(string address, bool fake)
    {
        Assert.Equal(fake, NetworkTools.IsProxyFakeAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Pinging_this_pc_answers()
    {
        using var ping = new Ping();
        var (ms, status, _) = await NetworkTools.PingAsync(ping, IPAddress.Loopback);
        Assert.Equal(IPStatus.Success, status);
        Assert.NotNull(ms);
    }

    [Fact]
    public async Task Route_to_this_pc_is_one_hop()
    {
        var hops = new List<TraceHop>();
        bool reached = await NetworkTools.TraceAsync(IPAddress.Loopback, new SyncProgress<TraceHop>(hops.Add), default);
        Assert.True(reached);
        Assert.Equal(1, Assert.Single(hops).Number);
        Assert.True(hops[0].IsTarget);
    }

    /// <summary>Reports straight away (Progress&lt;T&gt; posts to a context, which tests don't have).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
