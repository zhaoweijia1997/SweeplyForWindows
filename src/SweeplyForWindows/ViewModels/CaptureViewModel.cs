using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sweeply.Core;
using Sweeply.Core.Capture;
using Sweeply.Core.Monitoring;
using SweeplyForWindows.Capture;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.ViewModels;

/// <summary>The packets of the capture page, with what the stream tracker worked out for each, in capture order.</summary>
public sealed class CaptureStore
{
    public List<CapturedPacket> Packets { get; } = new();
    public List<StreamInfo> Streams { get; } = new();
    public List<CaptureInterface> Interfaces { get; } = new();
    public StreamTracker Tracker { get; private set; } = new();
    public long Bytes { get; private set; }

    public DateTime FirstUtc => Packets.Count > 0 ? Packets[0].TimestampUtc : DateTime.UtcNow;

    public void Add(CapturedPacket packet) => Add(packet, Tracker.Process(packet, Packets.Count + 1));

    /// <summary>A packet whose stream information was already worked out (by the store it comes from).</summary>
    public void Add(CapturedPacket packet, StreamInfo stream)
    {
        Streams.Add(stream);
        Packets.Add(packet);
        Bytes += packet.Data.Length;
    }

    /// <summary>Takes over another store's packets and tracker (a file read in the background).</summary>
    public void TakeOver(CaptureStore other)
    {
        Clear();
        Interfaces.AddRange(other.Interfaces);
        for (int i = 0; i < other.Packets.Count; i++) Add(other.Packets[i], other.Streams[i]);
        Tracker = other.Tracker;
    }

    public void Clear()
    {
        Packets.Clear();
        Streams.Clear();
        Interfaces.Clear();
        Tracker = new StreamTracker();
        Bytes = 0;
    }

    public string InterfaceName(CapturedPacket p) => p.InterfaceId >= 0 && p.InterfaceId < Interfaces.Count ? Interfaces[p.InterfaceId].Name : "";

    public Dissection Dissect(int index) => Dissector.Dissect(Packets[index], index + 1, Streams[index], FirstUtc, InterfaceName(Packets[index]));
}

/// <summary>One line of the packet list. The columns are worked out the first time the line is shown.</summary>
public sealed class PacketRow
{
    private static readonly Dictionary<PacketColor, Brush> Brushes = new()
    {
        [PacketColor.Default] = System.Windows.Media.Brushes.Transparent,
        [PacketColor.Broadcast] = System.Windows.Media.Brushes.Transparent,
        [PacketColor.Tcp] = Tint(0x7B, 0x6C, 0xF6, 0x26),
        [PacketColor.TcpSynFin] = Tint(0x80, 0x80, 0x80, 0x48),
        [PacketColor.TcpReset] = Tint(0xC4, 0x2B, 0x1C, 0x58),
        [PacketColor.BadTcp] = Tint(0xC4, 0x2B, 0x1C, 0x90),
        [PacketColor.Udp] = Tint(0x3A, 0x96, 0xDD, 0x2E),
        [PacketColor.Arp] = Tint(0xE2, 0xB0, 0x4A, 0x45),
        [PacketColor.Icmp] = Tint(0xE3, 0x6C, 0xE6, 0x30),
        [PacketColor.IcmpError] = Tint(0x6B, 0x3F, 0xA0, 0x60),
        [PacketColor.Http] = Tint(0x6C, 0xCB, 0x5F, 0x40),
    };

    private readonly CaptureStore _store;
    private string? _source, _destination, _protocol, _info;
    private Brush? _background;

    public PacketRow(CaptureStore store, int index)
    {
        _store = store;
        Index = index;
    }

    public int Index { get; }
    public int Number => Index + 1;
    private CapturedPacket Packet => _store.Packets[Index];

    public string Time => Format.Seconds((Packet.TimestampUtc - _store.FirstUtc).TotalSeconds);
    public string Program => Packet.ProcessName ?? "";
    public string Length => Packet.Length.ToString(CultureInfo.InvariantCulture);
    public string Source { get { Summarize(); return _source!; } }
    public string Destination { get { Summarize(); return _destination!; } }
    public string Protocol { get { Summarize(); return _protocol!; } }
    public string Info { get { Summarize(); return _info!; } }
    public Brush Background { get { Summarize(); return _background!; } }

    private void Summarize()
    {
        if (_info is not null) return;
        var d = _store.Dissect(Index);
        _source = d.Source;
        _destination = d.Destination;
        _protocol = d.Protocol;
        _info = d.Info.Length > 400 ? d.Info[..400] + "…" : d.Info;
        _background = Brushes[d.Color];
    }

    private static Brush Tint(byte r, byte g, byte b, byte alpha)
    {
        // Semi-transparent, so the same tint reads on the light and the dark theme.
        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        brush.Freeze();
        return brush;
    }

    public override string ToString() => string.Join('\t', Number, Time, Program, Source, Destination, Protocol, Length, Info);
}

/// <summary>A line of the packet details, expandable.</summary>
public sealed class DetailNode : ObservableObject
{
    private bool _isExpanded, _isSelected;

    public DetailNode(ProtoNode node, DetailNode? parent, string key)
    {
        Node = node;
        Parent = parent;
        Key = key;
        Children = node.Children is null ? Array.Empty<DetailNode>()
            : node.Children.Select((c, i) => new DetailNode(c, this, key + "/" + KeyOf(c, i))).ToList();
    }

    public ProtoNode Node { get; }
    public DetailNode? Parent { get; }

    /// <summary>The same line in another packet: by protocol, field name or label, not by position.</summary>
    public string Key { get; }

    public string Text => Node.Text;
    public bool IsWarning => Node.IsWarning;
    public bool IsLayer => Node.IsLayer;
    public IReadOnlyList<DetailNode> Children { get; }
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    private static string KeyOf(ProtoNode n, int i)
    {
        if (n.Field is { } field) return field;
        int colon = n.Text.IndexOf(':');
        return colon > 0 ? n.Text[..colon] : n.Text.Length > 40 ? n.Text[..40] : n.Text;
    }

    public IEnumerable<DetailNode> Walk()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Walk())
                yield return node;
    }

    /// <summary>Also what screen readers announce for the line.</summary>
    public override string ToString() => Text;
}

/// <summary>How the display filter being typed reads: nothing yet, a filter, or a mistake.</summary>
public enum FilterState
{
    Empty,
    Valid,
    Invalid,
}

/// <summary>A ready-made display filter in the menu next to the filter box.</summary>
public sealed record FilterPreset(string Name, string Text);

/// <summary>A line in the adapter list: one network adapter, or all of them.</summary>
public sealed class AdapterChoice
{
    public required string Name { get; init; }

    /// <summary>The card's own name and its address, to tell similar ones apart.</summary>
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Ids { get; init; } = Array.Empty<string>();

    /// <summary>This PC's addresses on it, to tell which end of a packet is this PC.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = Array.Empty<IPAddress>();

    public override string ToString() => Name;
}

/// <summary>
/// The capture page: a capture file, or a live capture, shown as Wireshark shows it — the packet list, the selected
/// packet's details and its bytes. Capturing only starts when the user clicks Start (and allows it in Windows'
/// administrator prompt); it goes on while other pages are open, until Stop, the limit, or the app ends. Files are
/// only read when the user opens them, and only written when they save.
/// </summary>
public sealed class CaptureViewModel : ObservableObject
{
    /// <summary>The most a capture keeps (in packets and bytes); a file with more is read up to that, a live capture stops there.</summary>
    public const int MaxPackets = 200_000;
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>Rows added to the list per refresh (5 a second), so a flood of packets can't freeze the window.</summary>
    private const int MaxRowsPerTick = 5000;

    private readonly HashSet<string> _expanded = new(); // what the user opened, kept from packet to packet
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private PacketRow? _selected;
    private DetailNode? _selectedNode;
    private byte[]? _bytes;
    private int _highlightStart = -1, _highlightLength;
    private string _statusText = "", _fileName = "", _note = "", _liveName = "";
    private bool _isLoading, _isStarting, _isCapturing, _isStopping, _autoScroll = true, _isAskingDiscard, _unsaved;
    private bool _sampleMode; // screenshots: this PC's adapters must never show up
    private IReadOnlyList<DetailNode> _details = Array.Empty<DetailNode>();
    private AdapterChoice? _selectedAdapter;
    private CaptureSession? _session;
    private DateTime _startedUtc;

    // Display filter: every row is in _allRows (same order as Store.Packets); Rows shows those that pass.
    private readonly List<PacketRow> _allRows = new();
    private DisplayFilter? _filter;
    private CancellationTokenSource? _filterRun;
    private string _filterText = "", _filterMessage = "";
    private FilterState _filterState;
    private bool _isFiltering;
    private int _filterDone, _filterTotal;
    private readonly DispatcherTimer _filterProgress = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private enum FilterJoin { Only, Not, And }

    public CaptureViewModel()
    {
        OpenCommand = new RelayCommand(_ => { if (PickOpenFile?.Invoke() is string path) _ = OpenAsync(path); }, () => !IsLoading && IsIdle);
        SaveCommand = new RelayCommand(_ => Save(), () => !IsLoading && Store.Packets.Count > 0);
        StartCommand = new RelayCommand(_ => RequestStart(), () => CanStart);
        StopCommand = new RelayCommand(async _ => await StopAsync(), () => IsCapturing && !_isStopping);
        DiscardAndStartCommand = new RelayCommand(async _ =>
        {
            IsAskingDiscard = false;
            _unsaved = false;
            await StartAsync();
        });
        CancelDiscardCommand = new RelayCommand(_ => IsAskingDiscard = false);
        CopyRowCommand = new RelayCommand(p => Copy((p as PacketRow ?? Selected)?.ToString()));
        CopyBytesCommand = new RelayCommand(p => { if ((p as PacketRow ?? Selected) is { } row) Copy(Format.Hex(Store.Packets[row.Index].Data, int.MaxValue)); });
        CopyNodeCommand = new RelayCommand(p => Copy((p as DetailNode ?? SelectedNode)?.Text));
        ByteClickedCommand = new RelayCommand(p => { if (p is int offset) SelectByte(offset); });
        ExpandAllCommand = new RelayCommand(_ => SetAllExpanded(true));
        CollapseAllCommand = new RelayCommand(_ => SetAllExpanded(false));
        ApplyFilterCommand = new RelayCommand(_ => _ = ApplyFilterAsync(FilterText));
        ClearFilterCommand = new RelayCommand(_ =>
        {
            FilterText = "";
            _ = ApplyFilterAsync("");
        });
        PresetCommand = new RelayCommand(p => { if (p is string text) UseFilter(text, FilterJoin.Only); });
        FilterNodeCommand = new RelayCommand(p => UseFilter(FilterOf(p), FilterJoin.Only), () => SelectedNode?.Node.Field is not null);
        ExcludeNodeCommand = new RelayCommand(p => UseFilter(FilterOf(p), FilterJoin.Not), () => SelectedNode?.Node.Field is not null);
        AndNodeCommand = new RelayCommand(p => UseFilter(FilterOf(p), FilterJoin.And), () => SelectedNode?.Node.Field is not null);
        ConversationCommand = new RelayCommand(p => { if ((p as PacketRow ?? Selected) is { } row) UseFilter(ConversationFilter(row), FilterJoin.Only); });
        ProgramFilterCommand = new RelayCommand(p =>
        {
            if ((p as PacketRow ?? Selected) is { Program.Length: > 0 } row)
                UseFilter($"{DisplayFilter.ProcessName} == {DisplayFilter.Quote(row.Program)}", FilterJoin.Only);
        });
        _timer.Tick += (_, _) => Pump();
        _filterProgress.Tick += (_, _) => UpdateStatus();
    }

    public CaptureStore Store { get; } = new();

    /// <summary>The packets shown; replaced as a whole when a file is opened (one change instead of thousands).</summary>
    public ObservableCollection<PacketRow> Rows { get; private set; } = new();

    public PacketRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            // Looking at a packet while capturing: the list stops following the newest ones.
            if (value is not null && IsCapturing) AutoScroll = false;
            ShowDetails();
        }
    }

    public IReadOnlyList<DetailNode> Details { get => _details; private set => SetField(ref _details, value); }

    public DetailNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetField(ref _selectedNode, value)) return;
            HighlightStart = value?.Node.Offset ?? -1;
            HighlightLength = value is { Node.Offset: >= 0 } ? value.Node.Length : 0;
        }
    }

    public byte[]? Bytes { get => _bytes; private set => SetField(ref _bytes, value); }
    public int HighlightStart { get => _highlightStart; private set => SetField(ref _highlightStart, value); }
    public int HighlightLength { get => _highlightLength; private set => SetField(ref _highlightLength, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string FileName { get => _fileName; private set => SetField(ref _fileName, value); }

    /// <summary>A line about the capture (cut short, read only in part, why capturing stopped); empty when there's nothing to say.</summary>
    public string Note { get => _note; private set => SetField(ref _note, value); }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetField(ref _isLoading, value)) return;
            OnPropertyChanged(nameof(IsWaiting));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Something is under way while the list is still empty: a file opening, the helper starting, the first packet.</summary>
    public bool IsWaiting => IsLoading || IsStarting || IsCapturing;

    public bool HasPackets => Store.Packets.Count > 0;

    /// <summary>The adapters that can be captured on (connected, with an address), and "all of them".</summary>
    public ObservableCollection<AdapterChoice> Adapters { get; } = new();

    public AdapterChoice? SelectedAdapter
    {
        get => _selectedAdapter;
        set { if (SetField(ref _selectedAdapter, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>Waiting for the helper (and Windows' administrator prompt).</summary>
    public bool IsStarting
    {
        get => _isStarting;
        private set
        {
            if (!SetField(ref _isStarting, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsWaiting));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (!SetField(ref _isCapturing, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsWaiting));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Neither capturing nor starting to: the adapter can be changed, a file opened.</summary>
    public bool IsIdle => !IsCapturing && !IsStarting;

    public bool CanStart => IsIdle && !IsLoading && SelectedAdapter is not null;

    /// <summary>Keep the newest packet in view while capturing.</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set { if (SetField(ref _autoScroll, value) && value && IsCapturing) ScrollToEndRequested?.Invoke(); }
    }

    /// <summary>Start was clicked while the last capture is still unsaved: asking whether to throw it away.</summary>
    public bool IsAskingDiscard { get => _isAskingDiscard; private set => SetField(ref _isAskingDiscard, value); }

    public string DiscardText => Loc.Instance.Format("cap.discard.text", Store.Packets.Count.ToString("N0", Loc.Instance.Culture));

    /// <summary>"--capture-replay": Start plays this file back instead of capturing (for tests; no administrator rights needed).</summary>
    public string? ReplayFile { get; set; }

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DiscardAndStartCommand { get; }
    public ICommand CancelDiscardCommand { get; }
    public ICommand CopyRowCommand { get; }
    public ICommand CopyBytesCommand { get; }
    public ICommand CopyNodeCommand { get; }
    public ICommand ByteClickedCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand ApplyFilterCommand { get; }
    public ICommand ClearFilterCommand { get; }
    public ICommand PresetCommand { get; }
    public ICommand FilterNodeCommand { get; }
    public ICommand ExcludeNodeCommand { get; }
    public ICommand AndNodeCommand { get; }
    public ICommand ConversationCommand { get; }
    public ICommand ProgramFilterCommand { get; }

    /// <summary>The display filter being typed; it applies on Enter (or Apply).</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetField(ref _filterText, value ?? "")) return;
            FilterState state = FilterState.Empty;
            if (!string.IsNullOrWhiteSpace(_filterText))
            {
                try
                {
                    DisplayFilter.Parse(_filterText);
                    state = FilterState.Valid;
                }
                catch (FilterSyntaxException) { state = FilterState.Invalid; }
            }
            FilterState = state;
            if (state != FilterState.Invalid && _filterMessage.Length > 0 && !IsFiltering) FilterMessage = ""; // the mistake is fixed
        }
    }

    public FilterState FilterState { get => _filterState; private set => SetField(ref _filterState, value); }

    /// <summary>What went wrong with the filter, or why nothing passes it; empty otherwise.</summary>
    public string FilterMessage { get => _filterMessage; private set => SetField(ref _filterMessage, value); }

    /// <summary>The filter is being applied to the packets in the background (its progress shows in the status line).</summary>
    public bool IsFiltering
    {
        get => _isFiltering;
        private set
        {
            if (!SetField(ref _isFiltering, value)) return;
            if (value) _filterProgress.Start();
            else _filterProgress.Stop();
            UpdateStatus();
        }
    }

    /// <summary>A display filter is applied (some packets may be hidden).</summary>
    public bool IsFiltered => _filter is not null;

    public IReadOnlyList<FilterPreset> Presets => new[]
    {
        new FilterPreset(Loc.Instance["cap.filter.preset.dns"], "dns"),
        new FilterPreset(Loc.Instance["cap.filter.preset.web"], "http || tls || quic"),
        new FilterPreset(Loc.Instance["cap.filter.preset.problems"], "tcp.analysis.flags"),
        new FilterPreset(Loc.Instance["cap.filter.preset.handshakes"], "tcp.flags.syn == 1 || tcp.flags.fin == 1 || tcp.flags.reset == 1"),
        new FilterPreset(Loc.Instance["cap.filter.preset.quiet"], "!(arp || ssdp || mdns || llmnr || nbns || igmp || stp)"),
    };

    /// <summary>Set by the window: asks for a capture file to open, or where to save.</summary>
    public Func<string?>? PickOpenFile { get; set; }
    public Func<string, string?>? PickSaveFile { get; set; }

    /// <summary>Set by the window: the window Windows' administrator prompt belongs to.</summary>
    public Func<IntPtr>? OwnerWindow { get; set; }

    /// <summary>Raised when the list should show its last row.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Raised when the list should bring the selected row into view (after filtering).</summary>
    public event Action? ScrollToSelectedRequested;

    /// <summary>The page came into view: read the adapters again (one may have connected meanwhile).</summary>
    public void OnShown()
    {
        if (IsIdle && !_sampleMode) _ = RefreshAdaptersAsync();
    }

    public async Task RefreshAdaptersAsync()
    {
        if (ReplayFile is { } replay)
        {
            SetAdapters(new[] { new AdapterChoice { Name = Loc.Instance.Format("cap.replay", Path.GetFileName(replay)), Ids = new[] { "replay" } } });
            return;
        }
        IReadOnlyList<NetworkAdapter> adapters;
        try { adapters = await Task.Run(NetworkAdapters.Read); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return; }
        var usable = adapters.Where(a => a.IsUp && (a.IPv4.Count > 0 || a.IPv6.Count > 0)).ToList();
        var choices = usable.Select(a => new AdapterChoice
        {
            Name = a.Name,
            Description = a.IPv4.Count > 0 ? $"{a.Description} · {a.IPv4[0].Address}" : a.Description,
            Ids = new[] { a.Id },
            Addresses = Own(a).ToList(),
        }).ToList();
        if (usable.Count > 1)
            choices.Add(new AdapterChoice
            {
                Name = Loc.Instance["cap.allAdapters"],
                Description = Loc.Instance.Format("cap.allAdapters.count", usable.Count),
                Ids = usable.Select(a => a.Id).ToList(),
                Addresses = usable.SelectMany(Own).ToList(),
            });
        if (IsIdle) SetAdapters(choices);

        static IEnumerable<IPAddress> Own(NetworkAdapter a) => a.IPv4.Select(v4 => v4.Address).Concat(a.IPv6);
    }

    private void SetAdapters(IReadOnlyList<AdapterChoice> choices)
    {
        string? keep = SelectedAdapter is { } old ? string.Join(",", old.Ids) : null;
        Adapters.Clear();
        foreach (var choice in choices) Adapters.Add(choice);
        SelectedAdapter = choices.FirstOrDefault(c => string.Join(",", c.Ids) == keep) ?? choices.FirstOrDefault();
    }

    private void RequestStart()
    {
        if (!CanStart) return;
        if (_unsaved && Store.Packets.Count > 0)
        {
            OnPropertyChanged(nameof(DiscardText));
            IsAskingDiscard = true;
            return;
        }
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (!CanStart || SelectedAdapter is not { } choice) return;
        IsStarting = true;
        Note = "";
        try
        {
            string backend = ReplayFile is not null ? HelperRequest.ReplayBackend
                : Npcap.Installed ? HelperRequest.NpcapBackend : HelperRequest.RawSocketBackend;
            var (session, error) = await OpenSessionAsync(backend, choice);
            // Npcap there but not working (an old or broken install): Windows' own way instead.
            if (session is null && error?.Code == "NpcapFailed")
                (session, error) = await OpenSessionAsync(HelperRequest.RawSocketBackend, choice);
            if (session is null)
            {
                Note = ErrorText(error);
                return;
            }
            Begin(session, choice);
        }
        finally
        {
            IsStarting = false;
            UpdateStatus();
        }
    }

    private Task<(CaptureSession? Session, HelperError? Error)> OpenSessionAsync(string backend, AdapterChoice choice)
    {
        bool elevate = backend switch
        {
            HelperRequest.RawSocketBackend => !IsElevated.Value,
            HelperRequest.NpcapBackend => Npcap.AdminOnly && !IsElevated.Value,
            _ => false,
        };
        StatusText = Loc.Instance[elevate ? "cap.waitingAdmin" : "cap.starting"];
        var request = new HelperRequest { Backend = backend, Adapters = choice.Ids.ToList(), File = ReplayFile };
        // A file played back isn't this PC's traffic: no programs to name.
        Action<CapturedPacket>? name = backend == HelperRequest.ReplayBackend ? null : new ProgramResolver(choice.Addresses).Resolve;
        return CaptureSession.StartAsync(request, elevate, name, OwnerWindow?.Invoke() ?? IntPtr.Zero);
    }

    private void Begin(CaptureSession session, AdapterChoice choice)
    {
        _session = session;
        _filterRun?.Cancel(); // the filter stays; it applies to the new packets as they come
        _filterRun = null;
        IsFiltering = false;
        FilterMessage = "";
        Selected = null;
        Store.Clear();
        Store.Interfaces.AddRange(session.Connection.Hello!.Interfaces);
        _allRows.Clear();
        Rows = new ObservableCollection<PacketRow>();
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasPackets));
        _liveName = choice.Name;
        FileName = "";
        Note = "";
        _unsaved = false;
        _startedUtc = DateTime.UtcNow;
        IsCapturing = true;
        AutoScroll = true;
        _timer.Start();
    }

    /// <summary>Moves what arrived since the last refresh into the list; stops at the limit or when the helper ended.</summary>
    private void Pump()
    {
        if (_session is not { } session || _isStopping) return;
        bool full = Take(session, MaxRowsPerTick);
        UpdateStatus();
        var culture = Loc.Instance.Culture;
        if (full)
            _ = StopAsync(Loc.Instance.Format("cap.limit", MaxPackets.ToString("N0", culture), SizeFormatter.Format(MaxBytes, culture)));
        else if (session.Connection.Completion.IsCompleted && session.Connection.Packets.IsEmpty)
            _ = StopAsync(ErrorText(session.Connection.Error)); // the helper ended by itself
    }

    /// <summary>
    /// Adds up to <paramref name="most"/> waiting packets; true when the capture is full. With a display filter each
    /// one is decoded to test it, so a refresh then also stops after 60 ms and leaves the rest for the next one.
    /// </summary>
    private bool Take(CaptureSession session, int most)
    {
        var queue = session.Connection.Packets;
        bool wasEmpty = Store.Packets.Count == 0, full = false;
        int added = 0, shown = 0;
        long started = Environment.TickCount64;
        while (added < most && queue.TryPeek(out var packet))
        {
            if (Store.Packets.Count >= MaxPackets || Store.Bytes + packet.Data.Length > MaxBytes)
            {
                full = true;
                break;
            }
            queue.TryDequeue(out _);
            Store.Add(packet);
            var row = new PacketRow(Store, Store.Packets.Count - 1);
            _allRows.Add(row);
            added++;
            // While a new filter runs over the earlier packets, it picks up these ones too when it finishes.
            if (_filter is null || (!IsFiltering && Passes(row.Index)))
            {
                Rows.Add(row);
                shown++;
            }
            if (_filter is not null && most != int.MaxValue && Environment.TickCount64 - started > 60) break;
        }
        if (added > 0)
        {
            _unsaved = true;
            if (wasEmpty) OnPropertyChanged(nameof(HasPackets));
            if (shown > 0 && AutoScroll) ScrollToEndRequested?.Invoke();
        }
        return full;
    }

    private bool Passes(int index, bool[]? seen = null) => _filter is null || _filter.Matches(Store.Dissect(index), Store.Packets[index], seen);

    /// <summary>Applies a display filter (or, for empty text, none): the packets are tested in the background, on all cores.</summary>
    private async Task ApplyFilterAsync(string text)
    {
        DisplayFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { filter = DisplayFilter.Parse(text); }
            catch (FilterSyntaxException e)
            {
                FilterState = FilterState.Invalid;
                FilterMessage = SyntaxText(e); // the filter applied before stays
                return;
            }
        }
        _filterRun?.Cancel();
        _filter = filter;
        OnPropertyChanged(nameof(IsFiltered));
        FilterMessage = "";
        if (filter is null)
        {
            IsFiltering = false;
            ShowRows(_allRows);
            UpdateStatus();
            return;
        }

        var run = _filterRun = new CancellationTokenSource();
        int count = Store.Packets.Count;
        var packets = Store.Packets.GetRange(0, count);
        var streams = Store.Streams.GetRange(0, count);
        var names = Store.Interfaces.Select(i => i.Name).ToArray();
        var first = Store.FirstUtc;
        var matches = new bool[count];
        var seen = new bool[filter.Fields.Count];
        _filterDone = 0;
        _filterTotal = count;
        IsFiltering = true;
        try
        {
            var options = new ParallelOptions { CancellationToken = run.Token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            await Task.Run(() => Parallel.For(0, count, options, i =>
            {
                var p = packets[i];
                string name = p.InterfaceId >= 0 && p.InterfaceId < names.Length ? names[p.InterfaceId] : "";
                matches[i] = filter.Matches(Dissector.Dissect(p, i + 1, streams[i], first, name), p, seen);
                Interlocked.Increment(ref _filterDone);
            }));
        }
        catch (OperationCanceledException)
        {
            if (_filterRun == run) IsFiltering = false; // stopped, and nothing else took over
            return;
        }
        if (_filterRun != run) return;

        var shown = new List<PacketRow>();
        for (int i = 0; i < count; i++)
            if (matches[i]) shown.Add(_allRows[i]);
        for (int i = count; i < Store.Packets.Count; i++) // captured while the filter ran
            if (Passes(i, seen)) shown.Add(_allRows[i]);
        IsFiltering = false;
        ShowRows(shown);
        if (shown.Count == 0 && Store.Packets.Count > 0)
        {
            // Nothing passes: say so, and point at a name no packet has (likely misspelt).
            string? never = filter.Fields.Where((_, k) => !seen[k]).FirstOrDefault();
            FilterMessage = never is not null ? Loc.Instance.Format("cap.filter.unknownField", never) : Loc.Instance["cap.filter.none"];
        }
        UpdateStatus();
    }

    /// <summary>Shows these rows, keeping the selected packet when it is among them.</summary>
    private void ShowRows(IReadOnlyList<PacketRow> rows)
    {
        var keep = Selected;
        Rows = new ObservableCollection<PacketRow>(rows);
        OnPropertyChanged(nameof(Rows));
        if (keep is not null && rows.Contains(keep))
        {
            _selected = null; // the list let go of it when its items changed; select it again
            Selected = keep;
            ScrollToSelectedRequested?.Invoke();
        }
        else if (!IsCapturing) Selected = Rows.FirstOrDefault();
    }

    private void UseFilter(string? text, FilterJoin join)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string combined = join switch
        {
            FilterJoin.Not => $"!({text})",
            FilterJoin.And when FilterState == FilterState.Valid => $"({FilterText.Trim()}) && {text}",
            _ => text,
        };
        FilterText = combined;
        _ = ApplyFilterAsync(combined);
    }

    private string? FilterOf(object? parameter) => (parameter as DetailNode ?? SelectedNode)?.Node is { } node ? DisplayFilter.For(node) : null;

    /// <summary>"Only this conversation": its TCP or UDP stream, or else the two addresses.</summary>
    private string? ConversationFilter(PacketRow row)
    {
        var stream = Store.Streams[row.Index];
        if (stream.TcpStream >= 0) return $"tcp.stream == {stream.TcpStream}";
        if (stream.UdpStream >= 0) return $"udp.stream == {stream.UdpStream}";
        var packet = Store.Packets[row.Index];
        var h = QuickHeader.Read(packet.Link, packet.Data);
        if (h.IsIp)
        {
            string field = h.IpVersion == 4 ? "ip.addr" : "ipv6.addr";
            return $"{field} == {h.Source.ToAddress()} && {field} == {h.Destination.ToAddress()}";
        }
        if (packet.Link == LinkType.Ethernet && packet.Data.Length >= 12)
            return $"eth.addr == {DisplayFilter.WriteValue(packet.Data[..6])} && eth.addr == {DisplayFilter.WriteValue(packet.Data[6..12])}";
        return null;
    }

    private static string SyntaxText(FilterSyntaxException e) =>
        Loc.Instance.Format("cap.filter.error." + e.Error, e.Position + 1, e.Near);

    private async Task StopAsync(string? note = null)
    {
        if (_session is not { } session || _isStopping) return;
        _isStopping = true;
        CommandManager.InvalidateRequerySuggested();
        _timer.Stop();
        try
        {
            await session.StopAsync(); // the helper sends what it still has, then the pipe closes
            Take(session, int.MaxValue); // the last ones (up to the limit)
            long dropped = session.Connection.Dropped;
            var culture = Loc.Instance.Culture;
            Note = note ?? (session.Connection.Error is { } error ? ErrorText(error) : "");
            if (dropped > 0) Note = (Note + " " + Loc.Instance.Format("cap.droppedNote", dropped.ToString("N0", culture))).Trim();
        }
        finally
        {
            _session = null;
            _isStopping = false;
            IsCapturing = false;
            FileName = _liveName;
            UpdateStatus();
        }
    }

    /// <summary>The app is exiting: close the pipe at once (the helper ends with it).</summary>
    public void StopNow()
    {
        _timer.Stop();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>Opens a pcap or pcapng file (from the button, a drop on the page, or "--open-capture").</summary>
    public async Task OpenAsync(string path)
    {
        if (IsLoading) return;
        if (!IsIdle)
        {
            Note = Loc.Instance["cap.stopFirst"];
            return;
        }
        IsLoading = true;
        StatusText = Loc.Instance.Format("cap.opening", Path.GetFileName(path));
        try
        {
            var (file, store) = await Task.Run(() =>
            {
                var read = PcapReader.Read(path, new PcapReader.Limits(MaxPackets, MaxBytes));
                var built = new CaptureStore();
                built.Interfaces.AddRange(read.Interfaces);
                foreach (var p in read.Packets) built.Add(p);
                return (read, built);
            });
            Replace(store);
            FileName = Path.GetFileName(path);
            Note = file.Truncated ? Loc.Instance.Format("cap.truncated", file.Packets.Count) : "";
            _unsaved = false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OutOfMemoryException)
        {
            Note = Loc.Instance.Format("cap.openFailed", Path.GetFileName(path), e.Message);
        }
        finally
        {
            IsLoading = false;
            UpdateStatus();
        }
    }

    private void Replace(CaptureStore loaded)
    {
        _filterRun?.Cancel();
        _filterRun = null;
        IsFiltering = false;
        Selected = null;
        Store.TakeOver(loaded);
        _allRows.Clear();
        _allRows.AddRange(Enumerable.Range(0, Store.Packets.Count).Select(i => new PacketRow(Store, i)));
        OnPropertyChanged(nameof(HasPackets));
        if (_filter is null)
        {
            ShowRows(_allRows); // and the first packet selected
            return;
        }
        // The filter applied stays applied, as in Wireshark: the new packets go through it first.
        Rows = new ObservableCollection<PacketRow>();
        OnPropertyChanged(nameof(Rows));
        _ = ApplyFilterAsync(_filter.Text);
    }

    private void Save()
    {
        string suggested = FileName.Length > 0 && !IsCapturing && Path.HasExtension(FileName)
            ? Path.ChangeExtension(FileName, ".pcapng")
            : $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.pcapng";
        if (PickSaveFile?.Invoke(suggested) is not string path) return;
        try
        {
            int count = Store.Packets.Count; // a live capture goes on growing; this many are written
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new PcapNgWriter(stream, "SweeplyForWindows");
            for (int i = 0; i < count; i++)
            {
                var packet = Store.Packets[i];
                writer.Write(packet, writer.Interface(packet.Link, Store.InterfaceName(packet)));
            }
            Note = Loc.Instance.Format("cap.saved", Path.GetFileName(path), count);
            if (!IsCapturing) _unsaved = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note = Loc.Instance.Format("cap.saveFailed", e.Message);
        }
    }

    private void ShowDetails()
    {
        if (Selected is not { } row)
        {
            Details = Array.Empty<DetailNode>();
            Bytes = null;
            SelectedNode = null;
            return;
        }
        var d = Store.Dissect(row.Index);
        var nodes = d.Layers.Select(l => new DetailNode(l, null, l.Field ?? l.Text)).ToList();
        foreach (var node in nodes.SelectMany(n => n.Walk()))
            node.IsExpanded = _expanded.Contains(node.Key);
        foreach (var node in nodes.SelectMany(n => n.Walk()))
            node.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(DetailNode.IsExpanded) || s is not DetailNode n) return;
                if (n.IsExpanded) _expanded.Add(n.Key);
                else _expanded.Remove(n.Key);
            };
        Details = nodes;
        Bytes = Store.Packets[row.Index].Data;
        SelectedNode = null;
        HighlightStart = -1;
        HighlightLength = 0;
    }

    /// <summary>A byte was clicked in the hex view: select the smallest field that covers it, opening its parents.</summary>
    private void SelectByte(int offset)
    {
        DetailNode? best = null;
        foreach (var node in Details.SelectMany(n => n.Walk()))
        {
            if (node.Node.Offset < 0 || offset < node.Node.Offset || offset >= node.Node.Offset + node.Node.Length) continue;
            if (best is null || node.Node.Length <= best.Node.Length) best = node;
        }
        if (best is null) return;
        for (var p = best.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        if (SelectedNode is { } old) old.IsSelected = false;
        best.IsSelected = true;
        SelectedNode = best;
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var node in Details.SelectMany(n => n.Walk()).Where(n => n.Children.Count > 0)) node.IsExpanded = expanded;
    }

    private void UpdateStatus()
    {
        var loc = Loc.Instance;
        var culture = loc.Culture;
        if (IsStarting) return; // "waiting for Windows" stays until it is answered
        // With a display filter: how far it got, or how many packets it shows.
        string filtered = _filter is null ? ""
            : IsFiltering ? loc.Format("cap.filter.busy", _filterTotal == 0 ? 100 : (int)((long)Volatile.Read(ref _filterDone) * 100 / _filterTotal))
            : loc.Format("cap.filter.shown", Rows.Count.ToString("N0", culture));
        string text;
        if (IsCapturing)
        {
            var elapsed = DateTime.UtcNow - _startedUtc;
            text = loc.Format("cap.live.status", _liveName, Store.Packets.Count.ToString("N0", culture),
                SizeFormatter.Format(Store.Bytes, culture), elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss"));
            long dropped = _session?.Connection.Dropped ?? 0;
            if (dropped > 0) text += " · " + loc.Format("cap.live.dropped", dropped.ToString("N0", culture));
        }
        else text = Store.Packets.Count == 0 ? "" : loc.Format("cap.status", Store.Packets.Count.ToString("N0", culture), SizeFormatter.Format(Store.Bytes, culture));
        StatusText = filtered.Length > 0 && text.Length > 0 ? text + " · " + filtered : text;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The words for why capturing failed or stopped (the helper only sends a code).</summary>
    private static string ErrorText(HelperError? error)
    {
        var loc = Loc.Instance;
        if (error is null) return loc["cap.error.HelperLost"];
        string key = "cap.error." + error.Code;
        string text = loc[key];
        return text == key
            ? loc.Format("cap.error.Failed", error.Detail.Length > 0 ? error.Detail : error.Code)
            : string.Format(loc.Culture, text, error.Detail);
    }

    private static readonly Lazy<bool> IsElevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public void Relocalize()
    {
        UpdateStatus();
        OnPropertyChanged(nameof(Presets));
        if (IsIdle && Adapters.Count > 0 && ReplayFile is null && !_sampleMode) _ = RefreshAdaptersAsync(); // "All adapters" in the new language
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy
    }

    /// <summary>Made-up packets for screenshots (documentation addresses only), as if a capture file was open.</summary>
    public void LoadSample()
    {
        _sampleMode = true;
        var store = new CaptureStore();
        store.Interfaces.Add(new CaptureInterface(LinkType.Ethernet, "Wi-Fi"));
        foreach (var packet in SamplePackets.Build()) store.Add(packet);
        Replace(store);
        FileName = "example.pcapng";
        SetAdapters(new[] { new AdapterChoice { Name = "Wi-Fi", Description = "Wireless network adapter · 192.0.2.23", Ids = new[] { "{sample}" } } });
        Selected = Rows.Count > 5 ? Rows[5] : Rows.FirstOrDefault();
        foreach (var node in Details) node.IsExpanded = node.Node.Field is "tls";
        _unsaved = false;
        UpdateStatus();
    }
}
