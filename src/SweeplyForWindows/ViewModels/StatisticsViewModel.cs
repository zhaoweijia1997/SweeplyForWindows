using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Sweeply.Core;
using Sweeply.Core.Capture;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>A number at the top of the overview: what it is, and its value.</summary>
public sealed record SummaryItem(string Label, string Value);

public sealed class ProgramStatsRow
{
    public required string Name { get; init; }
    public required string Packets { get; init; }
    public required string Sent { get; init; }
    public required string Received { get; init; }
    public required string Connections { get; init; }
    public required string Hosts { get; init; }
    public required ICommand FilterCommand { get; init; }
    public override string ToString() => string.Join("  ", Name, Packets, Sent, Received, Hosts);
}

public sealed class ConversationRow
{
    public required string Protocol { get; init; }
    public required string EndA { get; init; }
    public required string EndB { get; init; }
    public required string Name { get; init; }
    public required string Packets { get; init; }
    public required string AToB { get; init; }
    public required string BToA { get; init; }
    public required string Duration { get; init; }
    public required string Program { get; init; }
    public bool CanFollow { get; init; }
    public required ICommand FilterCommand { get; init; }
    public required ICommand FollowCommand { get; init; }
    public override string ToString() => string.Join("  ", Protocol, EndA, EndB, Name, Packets, Program);
}

public sealed class EndpointRow
{
    public required string Address { get; init; }
    public required string Name { get; init; }
    public required string Here { get; init; }
    public required string Packets { get; init; }
    public required string Sent { get; init; }
    public required string Received { get; init; }
    public required ICommand FilterCommand { get; init; }
    public override string ToString() => string.Join("  ", Address, Name, Here, Packets);
}

public sealed class ProtocolRow
{
    public required string Name { get; init; }
    public required string Packets { get; init; }
    public required string Bytes { get; init; }
    public required string PacketShare { get; init; }

    /// <summary>The share of all packets, as the width of a bar (0–80).</summary>
    public double BarWidth { get; init; }

    /// <summary>Wider the higher up in the tree, so the columns after the name line up despite the indent.</summary>
    public double NameWidth { get; init; }
    public required IReadOnlyList<ProtocolRow> Children { get; init; }
    public required ICommand FilterCommand { get; init; }
    public bool IsExpanded { get; set; } = true;
    public override string ToString() => $"{Name}  {Packets}  {PacketShare}";
}

public sealed class ProblemRow
{
    public required string What { get; init; }
    public required string Explanation { get; init; }
    public required string Count { get; init; }
    public required string First { get; init; }
    public required ICommand FilterCommand { get; init; }
    public override string ToString() => $"{What}  {Count}";
}

/// <summary>
/// The capture page's statistics: what the packets add up to, worked out in the background when the view opens
/// (and again on Refresh). Every row can become a display filter, which takes the user back to the packets;
/// conversations can also be followed.
/// </summary>
public sealed class StatisticsViewModel : ObservableObject
{
    public const int OverviewSection = 0, ProgramsSection = 1, ConversationsSection = 2, EndpointsSection = 3, ProtocolsSection = 4, ProblemsSection = 5;

    private readonly Func<bool, (IReadOnlyList<CapturedPacket> Packets, IReadOnlyList<StreamInfo> Streams, IReadOnlyList<string> Interfaces)> _snapshot;
    private readonly Action<string> _applyFilter;
    private readonly Func<bool, int, Task> _follow;
    private readonly DispatcherTimer _progress = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _run;
    private CaptureStatistics? _stats;
    private int _section, _done, _total;
    private bool _isComputing, _onlyShown;
    private string _header = "", _progressText = "", _chartTop = "", _chartSpan = "";
    private IReadOnlyList<SummaryItem> _summary = Array.Empty<SummaryItem>();
    private IReadOnlyList<ChartPoint> _traffic = Array.Empty<ChartPoint>();
    private IReadOnlyList<ChartPoint>? _trafficOut;
    private double _chartMax = 1, _chartWindow = 60;
    private IReadOnlyList<ProgramStatsRow> _programs = Array.Empty<ProgramStatsRow>();
    private IReadOnlyList<ConversationRow> _conversations = Array.Empty<ConversationRow>();
    private IReadOnlyList<EndpointRow> _endpoints = Array.Empty<EndpointRow>();
    private IReadOnlyList<ProtocolRow> _protocols = Array.Empty<ProtocolRow>();
    private IReadOnlyList<ProblemRow> _problems = Array.Empty<ProblemRow>();

    /// <param name="snapshot">The packets to add up: all of them, or (true) only those the display filter shows.</param>
    /// <param name="applyFilter">Shows the packets a filter picks (on the packet view).</param>
    /// <param name="follow">Follows a TCP (true) or UDP stream by number.</param>
    public StatisticsViewModel(
        Func<bool, (IReadOnlyList<CapturedPacket>, IReadOnlyList<StreamInfo>, IReadOnlyList<string>)> snapshot,
        Action<string> applyFilter, Func<bool, int, Task> follow)
    {
        _snapshot = snapshot;
        _applyFilter = applyFilter;
        _follow = follow;
        RefreshCommand = new RelayCommand(_ => _ = ComputeAsync(), () => !IsComputing);
        _progress.Tick += (_, _) => ProgressText = Loc.Instance.Format("cap.stats.computing", _total == 0 ? 100 : (int)((long)Volatile.Read(ref _done) * 100 / _total));
    }

    public int SectionIndex { get => _section; set => SetField(ref _section, value); }

    /// <summary>Add up only the packets the display filter shows.</summary>
    public bool OnlyShown
    {
        get => _onlyShown;
        set { if (SetField(ref _onlyShown, value)) _ = ComputeAsync(); }
    }

    public bool IsComputing
    {
        get => _isComputing;
        private set
        {
            if (!SetField(ref _isComputing, value)) return;
            if (value) _progress.Start();
            else _progress.Stop();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ProgressText { get => _progressText; private set => SetField(ref _progressText, value); }
    public string Header { get => _header; private set => SetField(ref _header, value); }
    public IReadOnlyList<SummaryItem> Summary { get => _summary; private set => SetField(ref _summary, value); }
    public IReadOnlyList<ChartPoint> Traffic { get => _traffic; private set => SetField(ref _traffic, value); }

    /// <summary>What went out, where directions are known (a live capture); null otherwise.</summary>
    public IReadOnlyList<ChartPoint>? TrafficOut { get => _trafficOut; private set => SetField(ref _trafficOut, value); }
    public double ChartMax { get => _chartMax; private set => SetField(ref _chartMax, value); }
    public double ChartWindow { get => _chartWindow; private set => SetField(ref _chartWindow, value); }
    public string ChartTop { get => _chartTop; private set => SetField(ref _chartTop, value); }
    public string ChartSpan { get => _chartSpan; private set => SetField(ref _chartSpan, value); }
    public bool HasOutbound => TrafficOut is not null;

    public IReadOnlyList<ProgramStatsRow> Programs { get => _programs; private set => SetField(ref _programs, value); }
    public IReadOnlyList<ProgramStatsRow> TopPrograms => Programs.Take(5).ToList();
    public IReadOnlyList<ConversationRow> Conversations { get => _conversations; private set => SetField(ref _conversations, value); }
    public IReadOnlyList<EndpointRow> Endpoints { get => _endpoints; private set => SetField(ref _endpoints, value); }
    public IReadOnlyList<ProtocolRow> Protocols { get => _protocols; private set => SetField(ref _protocols, value); }
    public IReadOnlyList<ProblemRow> Problems { get => _problems; private set => SetField(ref _problems, value); }
    public bool HasPrograms => Programs.Count > 0;
    public bool HasProblems => Problems.Count > 0;

    public ICommand RefreshCommand { get; }

    /// <summary>Adds the packets up again (in the background; an earlier run still going is dropped).</summary>
    public async Task ComputeAsync()
    {
        _run?.Cancel();
        var run = _run = new CancellationTokenSource();
        var (packets, streams, interfaces) = _snapshot(OnlyShown);
        _done = 0;
        _total = packets.Count;
        ProgressText = Loc.Instance.Format("cap.stats.computing", 0);
        IsComputing = true;
        CaptureStatistics stats;
        try
        {
            stats = await Task.Run(() => CaptureStatistics.Compute(packets, streams, interfaces, done => Volatile.Write(ref _done, done), run.Token));
        }
        catch (OperationCanceledException)
        {
            if (_run == run) IsComputing = false;
            return;
        }
        if (_run != run) return;
        IsComputing = false;
        Show(stats);
    }

    /// <summary>Drops what was added up (the packets changed completely: another file, a new capture).</summary>
    public void Clear()
    {
        _run?.Cancel();
        IsComputing = false;
        _stats = null;
        Show(null);
    }

    public bool IsStale(int packets) => _stats is null || (!OnlyShown && _stats.Packets != packets);

    private void Show(CaptureStatistics? stats)
    {
        _stats = stats;
        var loc = Loc.Instance;
        var c = loc.Culture;
        if (stats is null || stats.Packets == 0)
        {
            Header = "";
            Summary = Array.Empty<SummaryItem>();
            Traffic = Array.Empty<ChartPoint>();
            TrafficOut = null;
            Programs = Array.Empty<ProgramStatsRow>();
            Conversations = Array.Empty<ConversationRow>();
            Endpoints = Array.Empty<EndpointRow>();
            Protocols = Array.Empty<ProtocolRow>();
            Problems = Array.Empty<ProblemRow>();
            RaiseDerived();
            return;
        }

        string N(long n) => n.ToString("N0", c);
        string Size(long bytes) => SizeFormatter.Format(bytes, c);
        double seconds = Math.Max(stats.Duration.TotalSeconds, 0.001);
        Header = loc.Format("cap.stats.header", N(stats.Packets), Duration(stats.Duration), Size(stats.Bytes));
        int problemCount = stats.Problems.Sum(p => p.Count);
        Summary = new[]
        {
            new SummaryItem(loc["cap.stats.duration"], Duration(stats.Duration)),
            new SummaryItem(loc["cap.stats.packets"], N(stats.Packets)),
            new SummaryItem(loc["cap.stats.bytes"], Size(stats.Bytes)),
            new SummaryItem(loc["cap.stats.rate"], Size((long)(stats.Bytes / seconds)) + "/s"),
            new SummaryItem(loc["cap.stats.programs"], stats.Programs.Count == 0 ? "—" : N(stats.Programs.Count)),
            new SummaryItem(loc["cap.stats.problems"], N(problemCount)),
        };

        // Traffic: bytes per second at each step, the newest at the right edge.
        var t = stats.Traffic;
        double step = t.Step.TotalSeconds;
        int n = t.Bytes.Length;
        Traffic = Enumerable.Range(0, n).Select(k => new ChartPoint((n - 1 - k) * step, t.Bytes[k] / step)).ToList();
        TrafficOut = t.BytesOut.Any(b => b > 0) ? Enumerable.Range(0, n).Select(k => new ChartPoint((n - 1 - k) * step, t.BytesOut[k] / step)).ToList() : null;
        double peak = Traffic.Count == 0 ? 0 : Traffic.Max(p => p.Value);
        ChartMax = Math.Max(1, peak * 1.15);
        ChartWindow = Math.Max(step, (n - 1) * step);
        ChartTop = loc.Format("cap.stats.peak", Size((long)peak) + "/s");
        ChartSpan = loc.Format("cap.stats.span", Duration(stats.Duration), Duration(t.Step));

        Programs = stats.Programs.Select(p => new ProgramStatsRow
        {
            Name = p.Name,
            Packets = N(p.Packets),
            Sent = Size(p.BytesSent),
            Received = Size(p.BytesReceived),
            Connections = N(p.Conversations),
            Hosts = string.Join(", ", p.Hosts),
            FilterCommand = Filter(p.Filter),
        }).ToList();

        Conversations = stats.Conversations.Select(v => new ConversationRow
        {
            Protocol = v.Protocol,
            EndA = v.Stream >= 0 ? Endpoint(v.AddressA, v.PortA) : v.AddressA.ToString(),
            EndB = v.Stream >= 0 ? Endpoint(v.AddressB, v.PortB) : v.AddressB.ToString(),
            Name = v.NameB ?? v.NameA ?? "",
            Packets = N(v.Packets),
            AToB = Size(v.BytesAToB),
            BToA = Size(v.BytesBToA),
            Duration = Duration(v.Duration),
            Program = v.Program ?? "",
            CanFollow = v.Stream >= 0,
            FilterCommand = Filter(v.Filter),
            FollowCommand = new RelayCommand(_ => { if (v.Stream >= 0) _ = _follow(v.Protocol == "TCP", v.Stream); }),
        }).ToList();

        Endpoints = stats.Endpoints.Select(e => new EndpointRow
        {
            Address = e.Address.ToString(),
            Name = e.Name ?? "",
            Here = e.IsThisPc ? loc["cap.stats.thisPc"] : "",
            Packets = N(e.Packets),
            Sent = Size(e.BytesSent),
            Received = Size(e.BytesReceived),
            FilterCommand = Filter(e.Filter),
        }).ToList();

        static int Depth(ProtocolShare share) => 1 + share.Children.Select(Depth).DefaultIfEmpty(0).Max();
        int deepest = Depth(stats.Protocols);
        const double indent = 19; // what the tree indents each level by
        ProtocolRow Row(ProtocolShare share, int depth) => new()
        {
            Name = share.Name,
            Packets = N(share.Packets),
            Bytes = Size(share.Bytes),
            PacketShare = (share.Packets * 100.0 / stats.Packets).ToString("0.#", c) + "%",
            BarWidth = 80.0 * share.Packets / stats.Packets,
            NameWidth = 100 + (deepest - 1 - depth) * indent,
            Children = share.Children.Select(child => Row(child, depth + 1)).ToList(),
            FilterCommand = Filter(share.Filter),
        };
        Protocols = new[] { Row(stats.Protocols, 0) };

        Problems = stats.Problems.Select(p => new ProblemRow
        {
            What = loc["cap.problem." + p.Kind],
            Explanation = loc["cap.problem." + p.Kind + ".why"],
            Count = N(p.Count),
            First = "#" + p.FirstFrame.ToString(CultureInfo.InvariantCulture),
            FilterCommand = Filter(p.Filter),
        }).ToList();
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(TopPrograms));
        OnPropertyChanged(nameof(HasPrograms));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasOutbound));
    }

    private ICommand Filter(string text) => new RelayCommand(_ => _applyFilter(text));

    public void Relocalize()
    {
        if (_stats is not null) Show(_stats);
        else if (IsComputing) ProgressText = Loc.Instance.Format("cap.stats.computing", 0);
    }

    private static string Endpoint(System.Net.IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    /// <summary>"0.42 s", "1:05", "1:02:03".</summary>
    public static string Duration(TimeSpan span)
    {
        var c = Loc.Instance.Culture;
        if (span.TotalSeconds < 60) return Loc.Instance.Format("cap.stats.seconds", span.TotalSeconds.ToString(span.TotalSeconds < 10 ? "0.##" : "0.#", c));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", c) : span.ToString(@"m\:ss", c);
    }
}
