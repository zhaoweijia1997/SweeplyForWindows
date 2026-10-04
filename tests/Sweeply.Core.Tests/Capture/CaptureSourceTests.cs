using System.Net;
using Sweeply.Core.Capture;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests.Capture;

public class CaptureSourceTests
{
    [Fact]
    public void Ndis_keywords_say_which_way_and_what_kind_of_frame()
    {
        Assert.Equal(PacketDirection.Outbound, NdisFrames.Direction(NdisFrames.Send | NdisFrames.PacketStart | NdisFrames.PacketEnd));
        Assert.Equal(PacketDirection.Inbound, NdisFrames.Direction(NdisFrames.Receive | NdisFrames.PacketStart | NdisFrames.PacketEnd));
        Assert.Equal(PacketDirection.Unknown, NdisFrames.Direction(NdisFrames.PacketStart));
        Assert.Equal(LinkType.Ethernet, NdisFrames.Link(NdisFrames.Receive));
        Assert.Equal(LinkType.Ieee80211, NdisFrames.Link(NdisFrames.Receive | NdisFrames.Native80211));
        Assert.Equal(LinkType.Raw, NdisFrames.Link(NdisFrames.Send | NdisFrames.WirelessWan));
    }

    [Fact]
    public void Ndis_frames_in_one_event_or_in_parts()
    {
        var frames = new NdisFrames();
        const ulong whole = NdisFrames.PacketStart | NdisFrames.PacketEnd;
        Assert.Equal(new byte[] { 1, 2 }, frames.Add(7, whole, new byte[] { 1, 2 }));

        // In parts (Windows 8 and older): start, middle, end; another adapter's frame in between doesn't mix in.
        Assert.Null(frames.Add(7, NdisFrames.PacketStart, new byte[] { 1 }));
        Assert.Equal(new byte[] { 9 }, frames.Add(8, whole, new byte[] { 9 }));
        Assert.Null(frames.Add(7, 0, new byte[] { 2 }));
        Assert.Equal(new byte[] { 1, 2, 3 }, frames.Add(7, NdisFrames.PacketEnd, new byte[] { 3 }));

        // A new start throws away an unfinished frame.
        Assert.Null(frames.Add(7, NdisFrames.PacketStart, new byte[] { 4 }));
        Assert.Null(frames.Add(7, NdisFrames.PacketStart, new byte[] { 5 }));
        Assert.Equal(new byte[] { 5, 6 }, frames.Add(7, NdisFrames.PacketEnd, new byte[] { 6 }));
    }

    private sealed class Way(string backend, HelperError? cantStart) : IPacketSource
    {
        public Action<HelperError>? Failed;
        public bool Disposed;
        public string Backend => backend;
        public IReadOnlyList<CaptureInterface> Interfaces { get; } = new[] { new CaptureInterface(LinkType.Ethernet, backend) };
        public long Received => 0;
        public long Dropped => 0;
        public void Start(Action<CapturedPacket> packet, Action<HelperError> failed)
        {
            Failed = failed;
            if (cantStart is not null) failed(cantStart);
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void The_next_way_takes_over_when_one_cannot_start_and_says_why()
    {
        var ways = new[] { new Way("NdisCap", new HelperError("EtwFailed", "no more sessions")), new Way("RawSocket", null) };
        int made = 0;
        var source = new FallbackSource(() => ways[made++], () => ways[made++]);
        HelperError? reported = null;
        source.Start(_ => { }, e => reported = e);
        Assert.Null(reported);
        Assert.Equal("RawSocket", source.Backend);
        Assert.Equal("RawSocket", source.Interfaces[0].Name);
        Assert.Equal(new HelperError("EtwFailed", "no more sessions"), source.Note);
        Assert.True(ways[0].Disposed);

        // Failing later is the app's to hear, not a reason to switch.
        ways[1].Failed!(new HelperError("SocketFailed", "gone"));
        Assert.Equal("SocketFailed", reported!.Code);
    }

    [Fact]
    public void When_every_way_fails_the_last_reason_is_reported_and_the_first_one_starting_needs_no_note()
    {
        var source = new FallbackSource(() => new Way("NdisCap", new HelperError("NeedAdmin", "")), () => new Way("RawSocket", new HelperError("NeedAdmin", "denied")));
        HelperError? reported = null;
        source.Start(_ => { }, e => reported = e);
        Assert.Equal(("NeedAdmin", "denied"), (reported!.Code, reported.Detail));

        var fine = new FallbackSource(() => new Way("NdisCap", null), () => throw new InvalidOperationException("not needed"));
        fine.Start(_ => { }, _ => Assert.Fail("no failure"));
        Assert.Null(fine.Note);
    }

    [Fact]
    public void Two_ways_at_once_both_start_or_the_next_way_takes_over()
    {
        var main = new Way("NdisCap", null);
        var extra = new Way("RawSocket", null);
        var both = new CombinedSource(main, extra);
        HelperError? reported = null;
        both.Start(_ => { }, e => reported = e);
        Assert.Null(reported);
        Assert.Equal(("NdisCap", "NdisCap"), (both.Backend, both.Interfaces[0].Name));
        extra.Failed!(new HelperError("SocketFailed", "gone")); // later: the app's to hear
        Assert.Equal("SocketFailed", reported!.Code);

        // Raw sockets on the tunnel refused (no administrator rights): raw sockets everywhere is tried next, and fails the same way.
        var tunnelRefused = new Way("RawSocket", new HelperError("NeedAdmin", "denied"));
        var first = new Way("NdisCap", null);
        var source = new FallbackSource(() => new CombinedSource(first, tunnelRefused), () => new Way("RawSocket", new HelperError("NeedAdmin", "denied")));
        HelperError? last = null;
        source.Start(_ => { }, e => last = e);
        Assert.Equal("NeedAdmin", last!.Code);
        Assert.True(first.Disposed && tunnelRefused.Disposed);
    }

    [Fact]
    public void Adapters_without_windows_capture_filter_are_known_from_the_registry_without_failing()
    {
        string unknown = "{00000000-0000-0000-0000-00000000c0de}";
        var ids = NetworkAdapters.Read().Select(a => a.Id).Append(unknown).ToList();
        var uncovered = NdisCaptureSource.Uncovered(ids);
        Assert.True(uncovered.IsSubsetOf(ids));
        Assert.True(uncovered.Count == 0 || uncovered.Contains(unknown)); // an adapter that isn't there has no filter
    }

    [Fact]
    public void Hello_carries_why_a_way_was_passed_over()
    {
        var hello = new HelperHello("RawSocket", new() { new CaptureInterface(LinkType.Raw, "WLAN") }, new HelperError("EtwFailed", "no more sessions"));
        var back = HelperProtocol.DecodeJson<HelperHello>(HelperProtocol.EncodeJson(HelperMessage.Hello, hello).AsSpan(5))!;
        Assert.Equal(hello.Fallback, back.Fallback);
        Assert.Null(HelperProtocol.DecodeJson<HelperHello>(HelperProtocol.EncodeJson(HelperMessage.Hello, new HelperHello("NdisCap", new())).AsSpan(5))!.Fallback);
    }

    /// <summary>
    /// Without the rights to trace, Windows' packet capture can't start, and must say so (not crash, not hang).
    /// With them (elevated, or in Performance Log Users) it would really capture, and stop a capture in progress.
    /// </summary>
    [Fact]
    public void Windows_packet_capture_says_it_needs_administrator_rights()
    {
        if (EtwSession.CanStart) return;
        var adapter = new NetworkAdapter { Id = "{test}", Name = "Test", InterfaceIndex = 1, IsUp = true, IPv4 = new[] { (IPAddress.Parse("192.0.2.23"), 24) } };
        using var source = new NdisCaptureSource(new[] { adapter });
        HelperError? reported = null;
        source.Start(_ => { }, e => reported = e);
        Assert.Equal("NeedAdmin", reported?.Code);
        Assert.Equal(0, source.Dropped);
    }
}
